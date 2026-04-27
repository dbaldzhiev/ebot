using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using EBot.Core.GameState;
using Microsoft.Data.Sqlite;

namespace EBot.WebHost.Services;

/// <summary>
/// Resolves EVE typeIDs → (typeName, groupId, groupName, categoryId) via CCP ESI,
/// with a persistent SQLite cache at data/module_types.db.
///
/// On first encounter of a new typeID, Resolve() returns null immediately and queues
/// a background fetch. Subsequent calls (next tick) return the cached value.
/// </summary>
public sealed class ModuleTypeService : BackgroundService, ITypeNameResolver
{
    private readonly ConcurrentDictionary<int, ITypeNameResolver.TypeEntry> _types  = new();
    private readonly ConcurrentDictionary<int, (string Name, int CategoryId)>      _groups = new();
    private readonly Channel<int> _typeQueue  = Channel.CreateBounded<int>(
        new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly Channel<int> _groupQueue = Channel.CreateBounded<int>(
        new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    private readonly string _dbPath;
    private readonly ILogger<ModuleTypeService> _logger;
    private readonly IHttpClientFactory _httpFactory;

    private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    public ModuleTypeService(
        ILogger<ModuleTypeService> logger,
        IWebHostEnvironment env,
        IHttpClientFactory httpFactory)
    {
        _logger      = logger;
        _httpFactory = httpFactory;
        _dbPath      = Path.Combine(env.ContentRootPath, "data", "module_types.db");
    }

    // ── ITypeNameResolver ────────────────────────────────────────────────────

    public ITypeNameResolver.TypeEntry? Resolve(int typeId)
    {
        if (_types.TryGetValue(typeId, out var entry)) return entry;
        _typeQueue.Writer.TryWrite(typeId);
        return null;
    }

    // ── Background worker ────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        EnsureSchema();
        LoadFromDatabase();

        // Two concurrent drain loops — types and groups in parallel
        var typeTask  = DrainTypeQueueAsync(ct);
        var groupTask = DrainGroupQueueAsync(ct);
        await Task.WhenAll(typeTask, groupTask);
    }

    private async Task DrainTypeQueueAsync(CancellationToken ct)
    {
        var http = _httpFactory.CreateClient("esi");
        await foreach (var typeId in _typeQueue.Reader.ReadAllAsync(ct))
        {
            if (_types.ContainsKey(typeId)) continue;
            try
            {
                var url  = $"universe/types/{typeId}/?datasource=tranquility";
                var resp = await http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode) { await Task.Delay(500, ct); continue; }

                var doc  = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                var root = doc.RootElement;
                var name    = root.GetProperty("name").GetString() ?? $"Type {typeId}";
                var groupId = root.GetProperty("group_id").GetInt32();

                // Enqueue group fetch if not yet known
                if (!_groups.ContainsKey(groupId))
                    _groupQueue.Writer.TryWrite(groupId);

                // Store a partial entry; full entry written once group resolves
                _types.TryAdd(typeId, new ITypeNameResolver.TypeEntry(typeId, name, groupId, $"Group {groupId}", 0));
                PersistType(typeId, name, groupId);

                await Task.Delay(100, ct); // be polite to ESI
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _logger.LogDebug(ex, "ESI type fetch failed for {TypeId}", typeId); }
        }
    }

    private async Task DrainGroupQueueAsync(CancellationToken ct)
    {
        var http = _httpFactory.CreateClient("esi");
        await foreach (var groupId in _groupQueue.Reader.ReadAllAsync(ct))
        {
            if (_groups.ContainsKey(groupId)) continue;
            try
            {
                var url  = $"universe/groups/{groupId}/?datasource=tranquility";
                var resp = await http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode) { await Task.Delay(500, ct); continue; }

                var doc        = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                var root       = doc.RootElement;
                var groupName  = root.GetProperty("name").GetString() ?? $"Group {groupId}";
                var categoryId = root.GetProperty("category_id").GetInt32();

                _groups[groupId] = (groupName, categoryId);
                PersistGroup(groupId, groupName, categoryId);

                // Back-fill any already-cached types that were waiting on this group
                foreach (var kv in _types.Where(kv => kv.Value.GroupId == groupId))
                {
                    var updated = kv.Value with { GroupName = groupName, CategoryId = categoryId };
                    _types[kv.Key] = updated;
                    PersistType(kv.Key, updated.TypeName, groupId, groupName, categoryId);
                }

                await Task.Delay(100, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _logger.LogDebug(ex, "ESI group fetch failed for {GroupId}", groupId); }
        }
    }

    // ── SQLite ───────────────────────────────────────────────────────────────

    private void EnsureSchema()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS types (
                type_id     INTEGER PRIMARY KEY,
                type_name   TEXT NOT NULL,
                group_id    INTEGER NOT NULL,
                group_name  TEXT NOT NULL DEFAULT '',
                category_id INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS groups (
                group_id    INTEGER PRIMARY KEY,
                group_name  TEXT NOT NULL,
                category_id INTEGER NOT NULL DEFAULT 0
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private void LoadFromDatabase()
    {
        if (!File.Exists(_dbPath)) return;
        try
        {
            using var conn = Open();

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT group_id, group_name, category_id FROM groups";
                using var rdr = cmd.ExecuteReader();
                while (rdr.Read())
                    _groups[rdr.GetInt32(0)] = (rdr.GetString(1), rdr.GetInt32(2));
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT type_id, type_name, group_id, group_name, category_id FROM types";
                using var rdr = cmd.ExecuteReader();
                while (rdr.Read())
                    _types[rdr.GetInt32(0)] = new ITypeNameResolver.TypeEntry(
                        rdr.GetInt32(0), rdr.GetString(1), rdr.GetInt32(2), rdr.GetString(3), rdr.GetInt32(4));
            }

            _logger.LogInformation("ModuleTypeService: loaded {T} types, {G} groups from cache",
                _types.Count, _groups.Count);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not load module type cache"); }
    }

    private void PersistType(int typeId, string typeName, int groupId,
        string groupName = "", int categoryId = 0)
    {
        try
        {
            using var conn = Open();
            using var cmd  = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO types (type_id, type_name, group_id, group_name, category_id)
                VALUES ($tid, $tn, $gid, $gn, $cat)
                ON CONFLICT(type_id) DO UPDATE SET
                    type_name   = excluded.type_name,
                    group_id    = excluded.group_id,
                    group_name  = excluded.group_name,
                    category_id = excluded.category_id
                """;
            cmd.Parameters.AddWithValue("$tid", typeId);
            cmd.Parameters.AddWithValue("$tn",  typeName);
            cmd.Parameters.AddWithValue("$gid", groupId);
            cmd.Parameters.AddWithValue("$gn",  groupName);
            cmd.Parameters.AddWithValue("$cat", categoryId);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not persist type {TypeId}", typeId); }
    }

    private void PersistGroup(int groupId, string groupName, int categoryId)
    {
        try
        {
            using var conn = Open();
            using var cmd  = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO groups (group_id, group_name, category_id)
                VALUES ($gid, $gn, $cat)
                ON CONFLICT(group_id) DO UPDATE SET
                    group_name  = excluded.group_name,
                    category_id = excluded.category_id
                """;
            cmd.Parameters.AddWithValue("$gid", groupId);
            cmd.Parameters.AddWithValue("$gn",  groupName);
            cmd.Parameters.AddWithValue("$cat", categoryId);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not persist group {GroupId}", groupId); }
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Returns how many typeIDs are currently cached.</summary>
    public int CachedTypeCount  => _types.Count;
    public int CachedGroupCount => _groups.Count;
}
