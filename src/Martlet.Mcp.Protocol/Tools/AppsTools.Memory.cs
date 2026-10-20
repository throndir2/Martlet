using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Installation;
using Martlet.Doctor;
using Martlet.Mcp.Client;

namespace Martlet.Mcp;

internal sealed partial class McpServer
{
    /// <summary>One memory on every computer as this PC keeps it (memory-sync.json, the name Martlet.Core.Sync.MemorySyncState
    /// uses): whether sync is on, which store it last synced, when, how many facts it had then and who wrote them, and the
    /// forgotten facts every computer agreed on. Never a fact: the file holds IDs, revisions, digests and device IDs only.</summary>
    private static object MemorySyncStatus(JsonElement arguments)
    {
        var directory = DataDirectory(arguments);
        var sync = Martlet.Core.Cluster.ClusterSyncChoice.Describe(directory);
        object Described(Martlet.Core.Sync.MemorySyncState state) => new
        {
            state = state.StoreId == Guid.Empty ? "unreadable" : "loaded", syncedAt = state.SyncedAt, facts = state.Observed.Count,
            byComputer = state.Observed.Values.GroupBy(s => s.By, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new { device = g.Key, facts = g.Count() }).ToArray(),
            forgotten = state.Forgotten.Count
        };
        // Each memory space syncs on its own (memory-sync\<space>\memory-sync.json).
        var spaces = new List<object>();
        var folder = Path.Combine(directory, Martlet.Core.Sync.MemorySpaceFolders.SyncFolder);
        if (Directory.Exists(folder))
            foreach (var space in Directory.EnumerateDirectories(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal))
                if (Martlet.Core.Sync.MemorySpaceId.IsValid(space) && File.Exists(Path.Combine(folder, space!, Martlet.Core.Sync.MemorySyncState.FileName)))
                    spaces.Add(new { space, sync = Described(Martlet.Core.Sync.MemorySyncState.Load(Path.Combine(folder, space!))) });
        // memory-sync.json in the data folder is from before accounts, until it moves to the owner's space.
        if (!File.Exists(Path.Combine(directory, Martlet.Core.Sync.MemorySyncState.FileName))) return new { sync, state = "none", spaces };
        var legacy = Martlet.Core.Sync.MemorySyncState.Load(directory);
        return new
        {
            sync, state = legacy.StoreId == Guid.Empty ? "unreadable" : "loaded", syncedAt = legacy.SyncedAt, facts = legacy.Observed.Count,
            byComputer = legacy.Observed.Values.GroupBy(s => s.By, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new { device = g.Key, facts = g.Count() }).ToArray(),
            forgotten = legacy.Forgotten.Count, spaces
        };
    }

    /// <summary>What Martlet remembers and whose, from a data directory: the memory setting in settings.json, then the store's
    /// authoritative file (".martlet-memory.v1.json", the name Martlet.Memory's MemoryStore uses) read as JSON without opening or
    /// locking the store, and voices.json for the voices facts belong to. Counts and voice tags only: never a fact's text, a
    /// name, a voice ID or a path.</summary>
    private static async Task<object> MemoryStatusAsync(JsonElement arguments, CancellationToken cancellation)
    {
        var directory = DataDirectory(arguments);
        var loaded = await new Martlet.Core.Settings.SettingsStore(directory).LoadAsync(cancellation);
        var settings = loaded.Settings?.Memory;
        var memory = loaded.State switch
        {
            Martlet.Core.Settings.SettingsLoadState.FirstRun => "not set up",
            Martlet.Core.Settings.SettingsLoadState.Loaded when settings is null => "on (default)",
            Martlet.Core.Settings.SettingsLoadState.Loaded => settings!.Enabled ? "on" : "off",
            _ => "unreadable"
        };
        var storage = settings?.StoragePolicy == Martlet.Core.Settings.MemoryStoragePolicy.CustomLocalDirectory ? "custom folder" : "Martlet folder";
        string folder;
        try { folder = (settings ?? Martlet.Core.Settings.MemorySettings.Create()).ResolveDirectory(directory); }
        catch (Martlet.Core.Contracts.ContractException) { return new { memory, storage, state = "unreadable" }; }

        var roster = Martlet.Core.Speakers.VoiceRoster.Empty;
        string voiceList;
        var voicesPath = Path.Combine(directory, Martlet.Core.Speakers.VoiceChoices.RosterFile);
        try
        {
            if (File.Exists(voicesPath))
            {
                roster = Martlet.Core.Speakers.VoiceRoster.Parse(File.ReadAllBytes(voicesPath));
                voiceList = "loaded";
            }
            else voiceList = "none";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException)
        {
            voiceList = "unreadable";
        }

        // Memory spaces (docs/MEMORY.md, "Memory spaces on the desktop"): each account's own space in its folder (accounts\<id>\
        // memory, or the account's custom folder, which is the folder above) and the other spaces in memory-spaces\<space>.
        var spaces = new List<object>();
        var accounts = Path.Combine(directory, Martlet.Core.Sync.MemorySpaceFolders.AccountsFolder);
        if (Directory.Exists(accounts))
            foreach (var account in Directory.EnumerateDirectories(accounts).Order(StringComparer.Ordinal))
                if (Martlet.Core.Sync.MemorySpaceId.IsValid(Martlet.Core.Sync.MemorySpaceId.AccountPrefix + Path.GetFileName(account)) &&
                    File.Exists(Path.Combine(account, Martlet.Core.Settings.MemorySettings.AppLocalDirectoryName, Martlet.Core.Sync.MemorySpaceFolders.StoreFileName)))
                    spaces.Add(new { space = Martlet.Core.Sync.MemorySpaceId.AccountPrefix + Path.GetFileName(account),
                        store = Shaped(await MemoryStoreCountsAsync(Path.Combine(account, Martlet.Core.Settings.MemorySettings.AppLocalDirectoryName), roster, cancellation)) });
        var others = Path.Combine(directory, Martlet.Core.Sync.MemorySpaceFolders.SpacesFolder);
        if (Directory.Exists(others))
            foreach (var space in Directory.EnumerateDirectories(others).Select(Path.GetFileName).Order(StringComparer.Ordinal))
                if (Martlet.Core.Sync.MemorySpaceId.IsValid(space) &&
                    File.Exists(Path.Combine(others, space!, Martlet.Core.Sync.MemorySpaceFolders.StoreFileName)))
                    spaces.Add(new { space, store = Shaped(await MemoryStoreCountsAsync(Path.Combine(others, space!), roster, cancellation)) });

        // The store in the memory setting's folder: this PC's memories from before accounts (until they move into the owner's
        // space) or the owner's custom folder.
        var here = await MemoryStoreCountsAsync(folder, roster, cancellation);
        var limits = new
        {
            facts = Martlet.Memory.MemoryLimits.MaximumFacts, factCharacters = Martlet.Memory.MemoryLimits.MaximumContentCharacters,
            storeBytes = Martlet.Memory.MemoryLimits.MaximumStoreBytes
        };
        var upkeep = MemoryUpkeepStatus(directory);
        return here is null ? new { memory, storage, state = "none", voiceList, spaces = (object)spaces, upkeep, limits }
            : here.State == "unreadable" ? new { memory, storage, state = "unreadable", voiceList, spaces = (object)spaces, upkeep, limits }
            : (object)new { memory, storage, state = "loaded", voiceList, facts = here.Facts, typed = here.Typed,
                fromConversation = here.FromConversation, expiring = here.Expiring, replaced = here.Replaced,
                withSearchWords = here.WithSearchWords, whose = here.Whose, where = here.Where, spaces, upkeep, limits };
    }

    /// <summary>What tidying memory last did, from the desktop's memory-upkeep.json (counts, times and the pool member only).</summary>
    private static object MemoryUpkeepStatus(string directory)
    {
        var path = Path.Combine(directory, "memory-upkeep.json");
        try
        {
            if (!File.Exists(path)) return new { state = "none" };
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            return new
            {
                state = "loaded",
                storesChecked = root.TryGetProperty("stores", out var stores) && stores.ValueKind == JsonValueKind.Object
                    ? stores.EnumerateObject().Count() : 0,
                last = root.TryGetProperty("last", out var last) && last.ValueKind == JsonValueKind.Object
                    ? last.Clone() : (JsonElement?)null
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new { state = "unreadable" };
        }
    }

    private static object? Shaped(MemoryCounts? counts) => counts is null ? null : new
    {
        state = counts.State, facts = counts.Facts, typed = counts.Typed, fromConversation = counts.FromConversation,
        expiring = counts.Expiring, replaced = counts.Replaced, withSearchWords = counts.WithSearchWords, whose = counts.Whose,
        where = counts.Where
    };

    /// <summary>The counts of one memory store's authoritative file (".martlet-memory.v1.json", the name Martlet.Memory's
    /// MemoryStore uses), read as JSON without opening or locking the store; null when there is no store there. Counts and voice
    /// tags only.</summary>
    private static async Task<MemoryCounts?> MemoryStoreCountsAsync(string folder, Martlet.Core.Speakers.VoiceRoster roster, CancellationToken cancellation)
    {
        var path = Path.Combine(folder, ".martlet-memory.v1.json");
        if (!File.Exists(path)) return null;
        List<(string? Voice, string? Source, string? Retention, bool Replaced, bool Words, string? Activity)> facts = [];
        try
        {
            byte[] bytes;
            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length > Martlet.Memory.MemoryLimits.MaximumStoreBytes) return new("unreadable");
                bytes = new byte[stream.Length];
                await stream.ReadExactlyAsync(bytes, cancellation);
            }
            using var document = JsonDocument.Parse(bytes);
            foreach (var fact in document.RootElement.GetProperty("facts").EnumerateArray())
            {
                string? Text(params string[] names)
                {
                    var element = fact;
                    foreach (var name in names)
                        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element)) return null;
                    return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
                }
                facts.Add((Text("voice_id"), Text("created_from", "source_kind"), Text("retention", "kind"),
                    Text("replaced_at_utc") is not null, Text("search_words") is not null, Text("activity")));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or
            InvalidOperationException)
        {
            return new("unreadable");
        }
        var owned = facts.Where(f => f.Voice is not null).Select(f => roster.Resolve(f.Voice!)).ToArray();
        return new("loaded", facts.Count,
            facts.Count(f => f.Source is "user_entry" or "user_reviewed_import"),
            facts.Count(f => f.Source == "conversation"),
            facts.Count(f => f.Retention == "expires_at"),
            new
            {
                everyone = facts.Count(f => f.Voice is null),
                voices = owned.OfType<Martlet.Core.Speakers.KnownVoice>().GroupBy(v => v.Id, StringComparer.Ordinal)
                    .OrderBy(g => g.First().Number)
                    .Select(g => new { voice = g.First().Tag, named = g.First().Named, owner = g.First().Owner, linked = g.First().Account is not null, facts = g.Count() }).ToArray(),
                forgottenVoices = owned.Count(v => v is null)
            }, facts.Count(f => f.Replaced), facts.Count(f => f.Words),
            // Where the current facts matter (docs/MEMORY.md, "Memories for a game"): everywhere, any game, and each game by name.
            new
            {
                everywhere = facts.Count(f => !f.Replaced && f.Activity is null),
                anyGame = facts.Count(f => !f.Replaced && f.Activity == Martlet.Memory.MemoryActivity.Gaming),
                activities = facts.Where(f => !f.Replaced && Martlet.Memory.MemoryActivity.Name(f.Activity) is not null)
                    .GroupBy(f => f.Activity!.ToLowerInvariant()).OrderByDescending(g => g.Count())
                    .Select(g => new { where = Martlet.Memory.MemoryActivity.Describe(g.First().Activity), facts = g.Count() }).ToArray()
            });
    }
}
