using Martlet.Core.Contracts;
using Martlet.Core.Creations;

namespace Martlet.Core.Tests;

/// <summary>Creations per account (docs/ACCOUNTS.md): the one move of the data folder's creations into the first account's
/// folder, and the owner bridge that keeps the owner's list and the old single list the same.</summary>
public sealed class CreationAccountTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly CreationAuthor Author = new() { Device = "desktop-a", Computer = "Desk PC" };
    private static readonly Guid Owner = Guid.Parse("5a3f0c9e-8b7d-4e21-a6c3-b2f1d0e9a8b7");
    private static readonly Guid Alex = Guid.Parse("0b1c2d3e-4f50-6172-8394-a5b6c7d8e9f0");
    private readonly string data = Path.Combine(Path.GetTempPath(), "martlet-creation-accounts-" + Guid.NewGuid().ToString("N"));
    private readonly CreationRegistry registry = new();

    public CreationAccountTests() => registry.Register(FixtureCreations.Kind);

    public void Dispose()
    {
        try { Directory.Delete(data, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    [Fact]
    public async Task The_data_folder_creations_move_once_into_the_first_account_and_nothing_is_lost()
    {
        var first = await CreationStore.AddAsync(data, FixtureCreations.Draft(Author, "Morning tone", 0.5, 440), registry, Now, default);
        var second = await CreationStore.AddAsync(data, FixtureCreations.Draft(Author, "Evening tone", 0.5, 220), registry, Now.AddSeconds(1), default);
        new CreationSyncState { CheckedAt = Now, Summary = "Creations shared with 1 of 1 computer.", Hosts = [] }.Save(data);
        Directory.CreateDirectory(Path.Combine(data, CreationStore.IncomingDirectoryName, "0123456789abcdef"));
        // The account already took one creation from a host before the move.
        var folder = CreationAccounts.Folder(data, Owner);
        var synced = await CreationStore.AddAsync(folder, FixtureCreations.Draft(Author, "Synced tone", 0.5, 330), registry, Now.AddSeconds(2), default);
        Assert.Null(CreationAccounts.Moved(data));
        var files = Directory.GetFiles(CreationStore.Root(data)).Length;

        var move = await CreationAccounts.MoveDataFolderCreationsOnceAsync(data, Owner, folder, Now, default);
        Assert.Equal((Owner, 2, files), (move.Account, move.Creations, move.Assets));
        Assert.Equal(move, CreationAccounts.Moved(data));
        var library = CreationStore.View(folder);
        Assert.Equal(new[] { first.Id, second.Id, synced.Id }.Order(StringComparer.Ordinal), library.Live.Select(c => c.Id).Order(StringComparer.Ordinal));
        Assert.All(library.Live, c => Assert.True(CreationStore.IsComplete(folder, c)));
        Assert.NotNull(CreationSyncState.Load(folder));
        Assert.True(Directory.Exists(Path.Combine(folder, CreationStore.IncomingDirectoryName, "0123456789abcdef")));
        Assert.False(File.Exists(Path.Combine(data, CreationStore.LibraryFile)));
        Assert.False(Directory.Exists(CreationStore.Root(data)));
        Assert.False(File.Exists(Path.Combine(data, CreationSyncState.FileName)));
        Assert.True(File.Exists(CreationAccounts.MarkerPath(data)));

        // Later accounts get nothing: the move happened once.
        var stray = await CreationStore.AddAsync(data, FixtureCreations.Draft(Author, "Stray tone", 0.5, 550), registry, Now.AddSeconds(3), default);
        var again = await CreationAccounts.MoveDataFolderCreationsOnceAsync(data, Alex, CreationAccounts.Folder(data, Alex), Now.AddDays(1), default);
        Assert.Equal(move, again);
        Assert.Empty(CreationStore.View(CreationAccounts.Folder(data, Alex)).Live);
        Assert.Equal(stray.Id, CreationStore.View(data).Live.Single().Id);
    }

    [Fact]
    public async Task An_unreadable_list_is_left_in_place_and_no_move_is_recorded()
    {
        await CreationStore.AddAsync(data, FixtureCreations.Draft(Author, "Morning tone", 0.5, 440), registry, Now, default);
        await File.WriteAllTextAsync(Path.Combine(data, CreationStore.LibraryFile), "{ damaged");
        var folder = CreationAccounts.Folder(data, Owner);
        await Assert.ThrowsAsync<ContractException>(() => CreationAccounts.MoveDataFolderCreationsOnceAsync(data, Owner, folder, Now, default));
        Assert.Null(CreationAccounts.Moved(data));
        Assert.True(File.Exists(Path.Combine(data, CreationStore.LibraryFile)));
        Assert.Equal(2, Directory.GetFiles(CreationStore.Root(data)).Length);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task The_owner_bridge_keeps_the_owner_list_and_the_old_list_the_same_both_ways()
    {
        var mine = new FakeHost();
        var old = new FakeHost();
        old.Library = CreationLibrary.Empty.Add(Entry("Made by an older desktop"), Now);
        mine.Library = CreationLibrary.Empty.Add(Entry("Made by the owner's account"), Now.AddSeconds(1));
        var bridge = new CreationOwnerBridge(mine, old);

        var (digest, _) = await bridge.ReadDigestAsync(default);
        Assert.NotEqual(mine.Library.Digest(), digest);
        Assert.NotEqual(old.Library.Digest(), digest);
        var (joined, _) = await bridge.ReadAsync(default);
        Assert.Equal(2, joined.Live.Count);
        Assert.Equal(joined.Digest(), mine.Library.Digest());
        Assert.Equal(joined.Digest(), old.Library.Digest());
        Assert.Equal(joined.Digest(), (await bridge.ReadDigestAsync(default)).Digest);

        // A deletion on the owner's computer reaches the old list too, and a piece goes to the host once.
        var removed = joined.Remove(joined.Live[0].Id, "desktop-a", Now.AddMinutes(1));
        var (merged, _) = await bridge.MergeAsync(removed, default);
        Assert.Single(merged.Live);
        Assert.Equal(merged.Digest(), old.Library.Digest());
        var piece = merged.Live[0].Assets!.Single().Chunks.Single();
        var present = await bridge.SendChunkAsync(piece, new byte[] { 1 }, default);
        Assert.Contains(piece, present);
        Assert.Equal((1, 0), (mine.Sent, old.Sent));
        Assert.False(bridge.OldListOnly);
    }

    [Fact]
    public async Task On_a_host_older_than_accounts_the_owner_bridge_uses_the_old_list_alone()
    {
        var mine = new FakeHost { Old = true };
        var old = new FakeHost { Library = CreationLibrary.Empty.Add(Entry("Made by an older desktop"), Now) };
        var bridge = new CreationOwnerBridge(mine, old);
        Assert.Equal(old.Library.Digest(), (await bridge.ReadDigestAsync(default)).Digest);
        Assert.True(bridge.OldListOnly);
        Assert.Single((await bridge.ReadAsync(default)).Library.Live);
        var more = old.Library.Add(Entry("Made by the owner"), Now.AddSeconds(5));
        Assert.Equal(2, (await bridge.MergeAsync(more, default)).Library.Live.Count);
        await bridge.SendChunkAsync(more.Live[0].Assets!.Single().Chunks.Single(), new byte[] { 1 }, default);
        Assert.Equal((0, 1), (mine.Sent, old.Sent));

        // An account that is not the owner gets no bridge: a host older than accounts reads as old.
        var sync = new CreationSync(Path.Combine(data, "alex"));
        var result = await sync.RunAsync([new FakeHost { Old = true }], default);
        Assert.Equal(CreationSyncState.Old, result.Hosts.Single().State);
    }

    private static Creation Entry(string title)
    {
        var asset = CreationLibrary.Asset("audio", "audio/flac", System.Text.Encoding.UTF8.GetBytes(title));
        return new()
        {
            Id = CreationLibrary.NewId(), Kind = "fixture", KindVersion = 1, Title = title, CreatedBy = Author, CreatedAt = Now,
            Assets = [asset], Revision = 1, UpdatedAt = Now, UpdatedBy = "desktop-a"
        };
    }

    /// <summary>One list on a host, in memory; <see cref="Old"/> answers like a host that doesn't know the route.</summary>
    private sealed class FakeHost : ICreationHost
    {
        internal CreationLibrary Library = CreationLibrary.Empty;
        internal readonly HashSet<string> Present = new(StringComparer.Ordinal);
        internal bool Old;
        internal int Sent;
        public string HostId => "lab-host";

        public Task<(string Digest, string PresentDigest)> ReadDigestAsync(CancellationToken token) =>
            Answer(() => (Library.Digest(), CreationLibrary.PresentDigest(Present)));

        public Task<(CreationLibrary Library, IReadOnlySet<string> Present)> ReadAsync(CancellationToken token) =>
            Answer(() => (Library, (IReadOnlySet<string>)Present.ToHashSet()));

        public Task<(CreationLibrary Library, IReadOnlySet<string> Present)> MergeAsync(CreationLibrary library, CancellationToken token) =>
            Answer(() =>
            {
                Library = CreationLibrary.Merge(Library, library);
                return (Library, (IReadOnlySet<string>)Present.ToHashSet());
            });

        public Task<byte[]?> ReadChunkAsync(string sha256, CancellationToken token) => Answer(() => (byte[]?)null);

        public Task<IReadOnlySet<string>> SendChunkAsync(string sha256, ReadOnlyMemory<byte> data, CancellationToken token) =>
            Answer(() =>
            {
                Sent++;
                Present.Add(sha256);
                return (IReadOnlySet<string>)Present.ToHashSet();
            });

        private Task<T> Answer<T>(Func<T> value) =>
            Old ? Task.FromException<T>(new CreationHostException("lab-host runs a Martlet older than accounts.", old: true)) : Task.FromResult(value());
    }
}
