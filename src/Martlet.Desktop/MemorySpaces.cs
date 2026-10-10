using System.IO;
using Martlet.Core.Settings;
using Martlet.Core.Sync;
using Martlet.Memory;

namespace Martlet.Desktop;

/// <summary>Whose memories this device uses now (docs/ACCOUNTS.md, "Memory spaces"): the account signed in
/// (<see cref="Id"/>, its folder and the household folder, from the account session), whether it is the household's owner (the
/// owner's space holds the memories from before accounts and the old single memory document), the active character's own space
/// when that character remembers on its own or together with others (<c>character-&lt;id&gt;</c>, else null: the account's
/// space) and the character spaces other people share with this account. The last two are the seam for sharing (workstream W10).</summary>
internal sealed record MemoryAccount(Guid Id, string Folder, string HouseholdFolder, bool Owner = false, string? Character = null,
    IReadOnlyList<string>? Shared = null)
{
    internal string Space => MemorySpaceId.Account(Id);
}

/// <summary>One memory store this account uses: its space ID, its folder, where its sync state is (null: it doesn't sync) and
/// whether this device may change it.</summary>
internal sealed record MemorySpace(string Id, string Directory, string? SyncDirectory, bool Writable);

/// <summary>The spaces of the account signed in: <see cref="Active"/> is where remembering writes (the account's space, or the
/// active character's own); <see cref="Readable"/> lists it first, then <c>household</c> and any space shared with the account,
/// which recall reads too.</summary>
internal sealed record MemorySpaceSet(MemorySpace Active, IReadOnlyList<MemorySpace> Readable)
{
    internal IEnumerable<MemorySpace> Others => Readable.Skip(1);

    internal MemorySpace? Find(string? id) => id is null ? Active : Readable.FirstOrDefault(s => s.Id == id);
}

internal static class MemorySpaces
{
    /// <summary>The one store a desktop uses with no account signed in (tests and fixtures): the memory setting's folder, as
    /// before accounts. It never syncs.</summary>
    internal const string ThisPc = "this-pc";

    internal static MemorySpaceSet Resolve(MemoryAccount? account, MemorySettings memory, string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(memory);
        if (account is null)
        {
            var only = new MemorySpace(ThisPc, memory.ResolveDirectory(dataDirectory), null, true);
            return new(only, [only]);
        }
        var data = account.HouseholdFolder;
        MemorySpace Space(string id, bool writable) => new(id,
            MemorySpaceFolders.Store(data, id, memory, account.Id, account.Folder), MemorySpaceFolders.Sync(data, id), writable);
        var active = account.Character is { } character && MemorySpaceId.IsValid(character) &&
            character.StartsWith(MemorySpaceId.CharacterPrefix, StringComparison.Ordinal) ? character : account.Space;
        var spaces = new List<MemorySpace> { Space(active, true), Space(MemorySpaceId.Household, true) };
        // Only character spaces are shared: an account's own space (its memories, personality and prompts) never is.
        foreach (var shared in account.Shared ?? [])
            if (MemorySpaceId.IsValid(shared) && shared.StartsWith(MemorySpaceId.CharacterPrefix, StringComparison.Ordinal) &&
                spaces.All(s => s.Id != shared))
                spaces.Add(Space(shared, false));
        return new(spaces[0], spaces);
    }

    /// <summary>How the Memory window names a space.</summary>
    internal static string Label(string space, MemoryAccount? account) => space switch
    {
        ThisPc => "Your memories",
        MemorySpaceId.Household => "Household",
        _ when account is not null && space == account.Space => "Your memories",
        _ when space.StartsWith(MemorySpaceId.CharacterPrefix, StringComparison.Ordinal) => "This character's memories",
        _ => "Shared with you"
    };

    /// <summary>Moves the memories from before accounts into the owner's space once: the store in the memory setting's folder
    /// under the data folder (<c>&lt;data&gt;\memory</c>) and <c>&lt;data&gt;\memory-sync.json</c>. A custom memory folder is
    /// already where the owner's space keeps it. Run while nobody uses the owner's store. Returns what happened, for the log
    /// (never a path or a fact).</summary>
    internal static string? MigrateLegacy(MemoryAccount account, MemorySettings memory)
    {
        if (!account.Owner) return null;
        var data = account.HouseholdFolder;
        var legacy = memory.ResolveDirectory(data);
        var target = MemorySpaceFolders.Store(data, account.Space, memory, account.Id, account.Folder);
        string? outcome = null;
        if (!string.Equals(Path.GetFullPath(legacy), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase) &&
            File.Exists(Path.Combine(legacy, MemorySpaceFolders.StoreFileName)))
        {
            outcome = MemoryStore.MoveStore(legacy, target)
                ? "moved this PC's memories from before accounts into the owner's memory space"
                : "kept this PC's memories from before accounts where they are: the owner's memory space already has a store";
            if (outcome.StartsWith("moved", StringComparison.Ordinal))
                try { if (!Directory.EnumerateFileSystemEntries(legacy).Any()) Directory.Delete(legacy); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        var state = Path.Combine(data, MemorySyncState.FileName);
        var stateTarget = MemorySpaceFolders.Sync(data, account.Space);
        if (File.Exists(state) && !File.Exists(Path.Combine(stateTarget, MemorySyncState.FileName)))
        {
            Directory.CreateDirectory(stateTarget);
            File.Move(state, Path.Combine(stateTarget, MemorySyncState.FileName));
        }
        return outcome;
    }
}
