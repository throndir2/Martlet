using Martlet.Core.Settings;
using Martlet.Core.Sync;
using Martlet.Desktop;
using Martlet.Memory;

namespace Martlet.Desktop.Tests;

/// <summary>Memory spaces on the desktop (docs/MEMORY.md, "Memory spaces on the desktop"): one store per space, recall over the
/// active space and the household, remembering into the active space, the owner taking the memories from before accounts, and
/// facts shared with the account kept read-only.</summary>
public sealed class MemorySpaceServiceTests
{
    private sealed class Scope : IDisposable
    {
        internal string Root { get; } = Path.Combine(AppContext.BaseDirectory, "memory-spaces", Guid.NewGuid().ToString("N"));
        internal string Data => Path.Combine(Root, "data");

        internal MemoryAccount Account(Guid id, bool owner = false, IReadOnlyList<string>? shared = null) =>
            new(id, MemorySpaceFolders.AccountFolder(Data, id), Data, owner, Shared: shared);

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private static async Task<(DesktopMemoryService Memory, MemorySettings Settings)> StartAsync(Scope scope, MemoryAccount? account)
    {
        var store = new SettingsStore(scope.Data);
        var initial = SetupSettings.Begin(null);
        var saved = await store.SaveAsync(initial, null);
        Assert.True(saved.Saved);
        var memory = new DesktopMemoryService(store);
        memory.UseAccount(account);
        var configured = await memory.SaveConfigurationAsync(initial, saved.Revision, enabled: true,
            policy: MemoryStoragePolicy.AppLocalData, customDirectory: null);
        return (memory, configured.Settings.Memory!);
    }

    private static string StoreFile(string directory) => Path.Combine(directory, MemorySpaceFolders.StoreFileName);

    [Fact]
    public async Task Recall_reads_the_account_space_and_the_household_and_remembering_writes_where_each_fact_is()
    {
        using var scope = new Scope();
        var sam = scope.Account(Guid.NewGuid());
        var (memory, settings) = await StartAsync(scope, sam);
        using var _ = memory;
        var revision = settings.ConfigurationRevision;

        var wifi = (await memory.SaveFactAsync(revision, "The household Wi-Fi network is called Nest.", MemoryRetention.UntilDeleted(),
            space: MemorySpaceId.Household)).Fact;
        var cat = (await memory.SaveFactAsync(revision, "Sam's cat is called Miso.", MemoryRetention.UntilDeleted())).Fact;
        Assert.True(File.Exists(StoreFile(Path.Combine(sam.Folder, "memory"))));
        Assert.True(File.Exists(StoreFile(Path.Combine(scope.Data, MemorySpaceFolders.SpacesFolder, MemorySpaceId.Household))));
        Assert.False(File.Exists(StoreFile(Path.Combine(scope.Data, "memory"))));

        // The household's snapshot was refreshed by the change, so recall finds its fact without opening that store.
        var recalled = await memory.RecallAsync(settings, "What is the Wi-Fi network called?");
        Assert.Equal(wifi.Id, recalled.Facts[0].Id);
        Assert.Contains(recalled.Facts, f => f.Id == cat.Id);
        Assert.Equal(MemorySpaceId.Household, recalled.Spaces![wifi.Id]);
        Assert.False(recalled.Spaces.ContainsKey(cat.Id));

        // New facts go to the account's space; an update of the household's fact stays in the household; a near-duplicate of a
        // household fact isn't saved again.
        var shown = recalled.Facts;
        var wifiIndex = shown.ToList().FindIndex(f => f.Id == wifi.Id) + 1;
        var changes = await memory.RememberAsync(revision, shown,
        [
            new(MemoryCaptureKind.Remember, Content: "Sam drinks green tea."),
            new(MemoryCaptureKind.Update, wifiIndex, "The guest Wi-Fi password is written on the fridge door."),
            new(MemoryCaptureKind.Remember, Content: "The household Wi-Fi network is called Nest.")
        ], spaces: recalled.Spaces);
        Assert.Equal(2, changes.Count);
        var spaces = await memory.InspectSpacesAsync(revision);
        Assert.Equal([sam.Space, MemorySpaceId.Household], spaces.Select(s => s.Space.Id));
        Assert.Equal(["Sam's cat is called Miso.", "Sam drinks green tea."], spaces[0].Inspection.Facts.Select(f => f.Content));
        Assert.Equal("The guest Wi-Fi password is written on the fridge door.", Assert.Single(spaces[1].Inspection.Facts).Content);

        // Another account on this device has its own space and reads the same household.
        var alex = scope.Account(Guid.NewGuid());
        memory.UseAccount(alex);
        Assert.Empty(memory.Snapshots);
        Assert.Equal(1, await memory.LoadSpacesAsync());
        var alexs = await memory.RecallAsync(settings, "Wi-Fi");
        Assert.Equal(wifi.Id, Assert.Single(alexs.Facts).Id);
        Assert.Empty((await memory.InspectAsync(revision)).Facts);
    }

    [Fact]
    public async Task The_owner_takes_the_memories_from_before_accounts_once_and_others_do_not()
    {
        using var scope = new Scope();
        var (before, settings) = await StartAsync(scope, null);
        var legacy = Path.Combine(scope.Data, "memory");
        Guid kept;
        using (before)
        {
            kept = (await before.SaveFactAsync(settings.ConfigurationRevision, "The owner's birthday is in May.", MemoryRetention.UntilDeleted())).Fact.Id;
            Assert.True(File.Exists(StoreFile(legacy)));
        }
        await File.WriteAllTextAsync(Path.Combine(scope.Data, MemorySyncState.FileName), """{"schema_version":1,"observed":{},"forgotten":[]}""");

        var alex = scope.Account(Guid.NewGuid());
        using (var other = new DesktopMemoryService(new SettingsStore(scope.Data)))
        {
            other.UseAccount(alex);
            Assert.Empty((await other.InspectAsync(settings.ConfigurationRevision)).Facts);
            Assert.True(File.Exists(StoreFile(legacy)));
        }

        var owner = scope.Account(Guid.NewGuid(), owner: true);
        using var memory = new DesktopMemoryService(new SettingsStore(scope.Data));
        memory.UseAccount(owner);
        Assert.Equal(kept, Assert.Single((await memory.InspectAsync(settings.ConfigurationRevision)).Facts).Id);
        Assert.False(File.Exists(StoreFile(legacy)));
        Assert.True(File.Exists(StoreFile(Path.Combine(owner.Folder, "memory"))));
        Assert.False(File.Exists(Path.Combine(scope.Data, MemorySyncState.FileName)));
        Assert.True(File.Exists(Path.Combine(MemorySpaceFolders.Sync(scope.Data, owner.Space), MemorySyncState.FileName)));
    }

    [Fact]
    public async Task The_reply_tools_find_household_facts_and_change_them_where_they_are_kept()
    {
        using var scope = new Scope();
        var sam = scope.Account(Guid.NewGuid());
        var (memory, settings) = await StartAsync(scope, sam);
        using var _ = memory;
        var revision = settings.ConfigurationRevision;
        var wifi = (await memory.SaveFactAsync(revision, "The household Wi-Fi network is called Nest.", MemoryRetention.UntilDeleted(),
            space: MemorySpaceId.Household)).Fact;
        await memory.SaveFactAsync(revision, "Sam's cat is called Miso.", MemoryRetention.UntilDeleted());

        var found = await MemoryTools.RunAsync(memory, revision, """{"action":"find","query":"Nest"}""", null, null, default);
        Assert.Contains("\"kept\":\"Household\"", found.Result.Output, StringComparison.Ordinal);
        var all = await MemoryTools.RunAsync(memory, revision, """{"action":"find"}""", null, null, default);
        Assert.Contains("\"count\":2", all.Result.Output, StringComparison.Ordinal);

        var forgot = await MemoryTools.RunAsync(memory, revision,
            $$"""{"action":"forget","ids":["{{MemoryTools.ShortId(wifi)}}"]}""", null, null, default);
        Assert.Equal("forgot 1", forgot.Outcome);
        var spaces = await memory.InspectSpacesAsync(revision);
        Assert.Single(spaces[0].Inspection.Facts);
        Assert.Empty(spaces[1].Inspection.Facts);
    }

    [Fact]
    public async Task Only_character_spaces_are_shared_and_they_are_read_only_here()
    {
        using var scope = new Scope();
        var character = MemorySpaceId.Character(Guid.NewGuid());
        var someoneElse = MemorySpaceId.Account(Guid.NewGuid());
        var sam = scope.Account(Guid.NewGuid(), shared: [character, someoneElse]);
        var (memory, settings) = await StartAsync(scope, sam);
        using var _ = memory;

        var set = await memory.SpacesAsync();
        Assert.Equal([sam.Space, MemorySpaceId.Household, character], set!.Readable.Select(s => s.Id));
        Assert.Equal([true, true, false], set.Readable.Select(s => s.Writable));
        var refused = await Assert.ThrowsAsync<DesktopMemoryException>(() => memory.SaveFactAsync(settings.ConfigurationRevision,
            "Ivy likes rain.", MemoryRetention.UntilDeleted(), space: character));
        Assert.Equal("memory.read_only", refused.Code);
        var unknown = await Assert.ThrowsAsync<DesktopMemoryException>(() => memory.SaveFactAsync(settings.ConfigurationRevision,
            "Alex likes rain.", MemoryRetention.UntilDeleted(), space: someoneElse));
        Assert.Equal("memory.space_unknown", unknown.Code);
    }
}
