using Martlet.Core.Settings;

namespace Martlet.Core.Sync;

/// <summary>Where a desktop keeps each memory space (docs/MEMORY.md, "Memory spaces on the desktop"), so Desktop and MCP agree.
/// An account's own space follows that account's memory setting: the default is <c>&lt;account folder&gt;\memory</c>, a custom
/// folder stays the custom folder. Every other space (<c>household</c>, <c>character-&lt;id&gt;</c>, a space shared with the
/// account) is <c>&lt;data&gt;\memory-spaces\&lt;space&gt;</c>. The sync state of each space (<see cref="MemorySyncState"/>) is
/// <c>&lt;data&gt;\memory-sync\&lt;space&gt;\memory-sync.json</c>. Before accounts there was one store in the memory setting's
/// folder (<c>&lt;data&gt;\memory</c>) and one <c>&lt;data&gt;\memory-sync.json</c>; they move to the owner's space.</summary>
public static class MemorySpaceFolders
{
    /// <summary>The folder of every account's own files (docs/ACCOUNTS.md, "Desktop account session").</summary>
    public const string AccountsFolder = "accounts";
    public const string SpacesFolder = "memory-spaces";
    public const string SyncFolder = "memory-sync";
    /// <summary>The authoritative file of a Martlet.Memory store in its folder.</summary>
    public const string StoreFileName = ".martlet-memory.v1.json";

    public static string AccountFolder(string dataDirectory, Guid accountId) =>
        Path.Combine(dataDirectory, AccountsFolder, accountId.ToString("N"));

    /// <summary>The store folder of <paramref name="space"/>: an account space in <paramref name="accountFolder"/> (when it is
    /// that account's own space) by <paramref name="memory"/>, any other space under the data folder.</summary>
    public static string Store(string dataDirectory, string space, MemorySettings memory, Guid? accountId = null, string? accountFolder = null)
    {
        ArgumentNullException.ThrowIfNull(memory);
        if (!MemorySpaceId.IsValid(space)) throw new ArgumentException("Not a memory space ID.", nameof(space));
        return accountId is { } id && space == MemorySpaceId.Account(id)
            ? memory.ResolveDirectory(accountFolder ?? AccountFolder(dataDirectory, id))
            : Path.Combine(dataDirectory, SpacesFolder, space);
    }

    public static string Sync(string dataDirectory, string space)
    {
        if (!MemorySpaceId.IsValid(space)) throw new ArgumentException("Not a memory space ID.", nameof(space));
        return Path.Combine(dataDirectory, SyncFolder, space);
    }
}
