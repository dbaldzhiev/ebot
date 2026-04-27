namespace EBot.Core.GameState;

/// <summary>
/// Resolves EVE typeIDs to human-readable names and group info via a backing cache
/// (populated from ESI / SDE on first encounter).
///
/// Callers get null on a cache miss and should retry on the next tick — the service
/// queues a background fetch and the value will be present within ~1 second.
/// </summary>
public interface ITypeNameResolver
{
    /// <summary>
    /// Returns cached type info, or null if not yet resolved (fetch queued in background).
    /// </summary>
    TypeEntry? Resolve(int typeId);

    public sealed record TypeEntry(
        int    TypeId,
        string TypeName,
        int    GroupId,
        string GroupName,
        int    CategoryId);
}
