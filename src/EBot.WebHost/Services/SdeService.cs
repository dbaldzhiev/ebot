using Microsoft.Data.Sqlite;

namespace EBot.WebHost.Services;

/// <summary>
/// Reads EVE station and system data from a local SQLite database
/// populated by setup_sde.py (Fuzzwork CSV bz2 import — identical approach
/// to the industrialist example in examples/industrialist/backend/).
///
/// Run once before starting EBot:
///   python src/EBot.WebHost/setup_sde.py
/// </summary>
public sealed class SdeService : BackgroundService
{
    private readonly string               _dbPath;
    private readonly ILogger<SdeService>  _logger;

    public bool   IsReady        { get; private set; }
    public string StatusMessage  { get; private set; } = "Checking SDE database…";
    public bool   IsDownloading  => false;
    public double DownloadProgress => IsReady ? 1.0 : 0.0;
    public string DataDir        => Path.GetDirectoryName(_dbPath)!;

    public SdeService(ILogger<SdeService> logger, IWebHostEnvironment env)
    {
        _logger = logger;
        // ContentRootPath == project source dir during 'dotnet run',
        // matching where setup_sde.py places data/eve_sde.db.
        _dbPath = Path.Combine(env.ContentRootPath, "data", "eve_sde.db");
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        CheckDatabase();
        return Task.CompletedTask;
    }

    private void CheckDatabase()
    {
        if (!File.Exists(_dbPath))
        {
            IsReady       = false;
            StatusMessage = "eve_sde.db not found — run setup_sde.py";
            _logger.LogWarning("SDE database not found at {Path}. Run: python setup_sde.py", _dbPath);
            return;
        }

        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
            conn.Open();

            long stationCount;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM staStations";
                stationCount    = (long)(cmd.ExecuteScalar() ?? 0L);
            }

            long systemCount;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM mapSolarSystems";
                systemCount     = (long)(cmd.ExecuteScalar() ?? 0L);
            }

            IsReady       = stationCount > 0 && systemCount > 0;
            StatusMessage = IsReady
                ? $"SDE ready — {stationCount:N0} stations, {systemCount:N0} systems"
                : "SDE database exists but is empty — run setup_sde.py";

            _logger.LogInformation("SDE: {Status}", StatusMessage);
        }
        catch (Exception ex)
        {
            IsReady       = false;
            StatusMessage = $"SDE error: {ex.Message}";
            _logger.LogError(ex, "Failed to open SDE database at {Path}", _dbPath);
        }
    }

    // ── Search result ────────────────────────────────────────────────────────

    public sealed record SdeResult(
        string  Id,
        string  Name,
        string? SystemName,
        string? RegionName,
        double? Security,
        int?    TypeId,
        bool    IsStation);

    /// <summary>
    /// Search NPC stations and solar systems by name prefix.
    /// Returns up to <paramref name="limit"/> results (stations first, then systems).
    /// </summary>
    public List<SdeResult> Search(string q, int limit = 10)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(q)) return [];

        q = q.Trim();
        var results = new List<SdeResult>();

        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
            conn.Open();

            // --- NPC stations (prefix match on station name) ---
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT s.stationID,
                           s.stationName,
                           sys.solarSystemName,
                           r.regionName,
                           sys.security,
                           s.stationTypeID
                    FROM   staStations s
                    JOIN   mapSolarSystems sys ON sys.solarSystemID = s.solarSystemID
                    JOIN   mapRegions r        ON r.regionID        = s.regionID
                    WHERE  s.stationName LIKE $q
                      AND  r.regionID < 11000000
                    ORDER  BY s.stationName
                    LIMIT  $limit
                    """;
                cmd.Parameters.AddWithValue("$q",     q + "%");
                cmd.Parameters.AddWithValue("$limit", limit);

                using var rdr = cmd.ExecuteReader();
                while (rdr.Read())
                {
                    results.Add(new SdeResult(
                        Id:         rdr.GetInt64(0).ToString(),
                        Name:       rdr.GetString(1),
                        SystemName: rdr.IsDBNull(2) ? null : rdr.GetString(2),
                        RegionName: rdr.IsDBNull(3) ? null : rdr.GetString(3),
                        Security:   rdr.IsDBNull(4) ? null : rdr.GetDouble(4),
                        TypeId:     rdr.IsDBNull(5) ? null : rdr.GetInt32(5),
                        IsStation:  true));
                }
            }

            // --- Solar systems — fill remaining slots ---
            var remaining = limit - results.Count;
            if (remaining > 0)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT s.solarSystemID,
                           s.solarSystemName,
                           r.regionName,
                           s.security
                    FROM   mapSolarSystems s
                    JOIN   mapRegions r ON r.regionID = s.regionID
                    WHERE  s.solarSystemName LIKE $q
                      AND  r.regionID < 11000000
                    ORDER  BY s.solarSystemName
                    LIMIT  $limit
                    """;
                cmd.Parameters.AddWithValue("$q",     q + "%");
                cmd.Parameters.AddWithValue("$limit", remaining);

                using var rdr = cmd.ExecuteReader();
                while (rdr.Read())
                {
                    results.Add(new SdeResult(
                        Id:         rdr.GetInt64(0).ToString(),
                        Name:       rdr.GetString(1),
                        SystemName: rdr.GetString(1),
                        RegionName: rdr.IsDBNull(2) ? null : rdr.GetString(2),
                        Security:   rdr.IsDBNull(3) ? null : rdr.GetDouble(3),
                        TypeId:     null,
                        IsStation:  false));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SDE search failed for query '{Q}'", q);
        }

        return results;
    }

    /// <summary>Re-check the database (no download — use setup_sde.py for that).</summary>
    public Task ForceRefreshAsync(CancellationToken ct = default)
    {
        CheckDatabase();
        return Task.CompletedTask;
    }
}
