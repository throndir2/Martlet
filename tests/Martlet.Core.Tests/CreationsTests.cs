using System.Buffers.Binary;
using System.Text.Json;
using Martlet.Core.Audio;
using Martlet.Core.Contracts;
using Martlet.Core.Creations;

namespace Martlet.Core.Tests;

public sealed class CreationsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly CreationAuthor Author = new() { Device = "desktop-a", Computer = "Desk PC" };

    [Theory]
    [InlineData(1, 48_000, 1.0)]
    [InlineData(2, 48_000, 2.3)]
    [InlineData(2, 44_100, 0.0002)]
    [InlineData(1, 16_000, 0.5)]
    public void FlacRoundTripsExactly(int channels, int rate, double seconds)
    {
        var frames = Math.Max(1, (int)(rate * seconds));
        var random = new Random(channels * 7 + rate);
        var pcm = new byte[frames * 2 * channels];
        for (var i = 0; i < frames; i++)
        for (var c = 0; c < channels; c++)
        {
            // Music-like: two sines, noise, a silent stretch and full-scale extremes.
            var value = i > frames / 3 && i < frames / 2 ? 0
                : i % 997 == 0 ? (c == 0 ? short.MaxValue : short.MinValue)
                : (int)(9000 * Math.Sin(i * 0.031 * (c + 1)) + 4000 * Math.Sin(i * 0.17) + random.Next(-600, 600));
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan((i * channels + c) * 2), (short)Math.Clamp(value, short.MinValue, short.MaxValue));
        }
        var flac = FlacCodec.Encode(pcm, rate, channels);
        Assert.True(FlacCodec.IsFlac(flac));
        var decoded = FlacCodec.Decode(flac);
        Assert.Equal(rate, decoded.SampleRate);
        Assert.Equal(channels, decoded.Channels);
        Assert.Equal(pcm, decoded.Pcm16);
        if (frames > 10_000) Assert.True(flac.Length < pcm.Length * 0.8, $"{flac.Length} of {pcm.Length}");
    }

    [Fact]
    public void FlacRefusesDamage()
    {
        var flac = FlacCodec.Encode(FixtureCreations.Tone(0.5, 440, 2), 48_000, 2);
        flac[flac.Length / 2] ^= 0x40;
        Assert.Throws<ContractException>(() => FlacCodec.Decode(flac));
        Assert.Throws<ContractException>(() => FlacCodec.Decode("RIFF0000WAVE"u8));
    }

    [Fact]
    public void LibraryMergesWithTombstonesInAnyOrder()
    {
        var a = CreationLibrary.Empty.Add(Entry("Song one"), Now);
        var id = a.Live[0].Id;
        var b = CreationLibrary.Merge(CreationLibrary.Empty, a).Rename(id, "Song one, again", "desktop-b", Now.AddSeconds(5));
        var c = a.Remove(id, "desktop-a", Now.AddSeconds(10));
        var one = CreationLibrary.Merge(CreationLibrary.Merge(a, b), c);
        var two = CreationLibrary.Merge(c, CreationLibrary.Merge(b, a));
        Assert.Equal(one.Digest(), two.Digest());
        Assert.Empty(one.Live);
        Assert.True(one.Find(id)!.Removed);
        var renamed = CreationLibrary.Merge(a, b);
        Assert.Equal("Song one, again", renamed.Live.Single().Title);
        Assert.Equal(renamed.Digest(), CreationLibrary.Parse(renamed.Write()).Digest());
        Assert.Equal(renamed.Live.Single().Id, renamed.Resolve(renamed.Live.Single().Key)!.Id);
    }

    [Fact]
    public void FullListCleansUpOnlyKindsThatAllowIt()
    {
        var library = CreationLibrary.Empty;
        for (var i = 0; i < CreationLibrary.MaximumCreations; i++)
            library = library.Add(Entry($"Tone {i}", autoCleanup: i < 2, at: Now.AddMinutes(i)), Now.AddMinutes(i));
        var oldest = library.Live.Last();
        library = library.Add(Entry("One more", at: Now.AddDays(1)), Now.AddDays(1));
        Assert.Equal(CreationLibrary.MaximumCreations, library.Live.Count);
        Assert.True(library.Find(oldest.Id)!.Removed);
        library = library.Add(Entry("Another", at: Now.AddDays(2)), Now.AddDays(2));
        var full = Assert.Throws<ContractException>(() => library.Add(Entry("Too many", at: Now.AddDays(3)), Now.AddDays(3)));
        Assert.Equal(ErrorCode.PayloadTooLarge, full.Code);
    }

    [Fact]
    public void ParseRefusesBadEntries()
    {
        var bytes = CreationLibrary.Empty.Add(Entry("Fine"), Now).Write();
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.Throws<ContractException>(() => CreationLibrary.Parse(System.Text.Encoding.UTF8.GetBytes(text.Replace("\"audio/flac\"", "\"text/html\""))));
        Assert.Throws<ContractException>(() => CreationLibrary.Parse(System.Text.Encoding.UTF8.GetBytes(text.Replace("\"schema_version\":1", "\"schema_version\":2"))));
        Assert.Throws<ContractException>(() => CreationLibrary.Parse(System.Text.Encoding.UTF8.GetBytes(text.Replace("\"title\"", "\"unknown\":1,\"title\""))));
    }

    [Fact]
    public async Task StoreKeepsAssetsByHashAndCopiesBetweenComputers()
    {
        var root = Path.Combine(Path.GetTempPath(), "martlet-creations-test-" + Guid.NewGuid().ToString("N"));
        var a = Path.Combine(root, "a");
        var b = Path.Combine(root, "b");
        try
        {
            var registry = new CreationRegistry();
            registry.Register(FixtureCreations.Kind);
            var made = await CreationStore.AddAsync(a, FixtureCreations.Draft(Author, "Tone", 1.5), registry, Now, CancellationToken.None);
            Assert.True(CreationStore.IsComplete(a, made));
            var unknown = FixtureCreations.Draft(Author, "Tone", 1) with { Kind = "song" };
            await Assert.ThrowsAsync<ContractException>(() => CreationStore.AddAsync(a, unknown, registry, Now, CancellationToken.None));

            CreationStore.Commit(b, CreationStore.View(a));
            var fetched = 0;
            var result = await CreationStore.ReconcileAsync(b, async (sha256, token) =>
            {
                fetched++;
                return await CreationStore.ReadChunkAsync(a, CreationStore.View(a), sha256, token);
            }, CancellationToken.None);
            Assert.Equal(2, result.Added);
            Assert.Contains(made.Id, result.Local);
            Assert.Equal(made.Assets!.Sum(x => x.Chunks.Count), fetched);
            var played = await FixtureCreations.Handler.PerformAsync(new(made, JsonSerializer.SerializeToElement(new { start_ms = 250 }),
                CreationStore.Assets(b, made)), CancellationToken.None);
            Assert.False(played.IsError);
            Assert.Contains("250 ms", played.Text);

            await CreationStore.RemoveAsync(b, made.Id, "desktop-b", Now.AddMinutes(1), CancellationToken.None);
            Assert.Empty(Directory.EnumerateFiles(CreationStore.Root(b)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ARevisedCreationReachesTheOtherComputerAndItsOldAssetsGo()
    {
        var root = Path.Combine(Path.GetTempPath(), "martlet-creations-test-" + Guid.NewGuid().ToString("N"));
        var a = Path.Combine(root, "a");
        var b = Path.Combine(root, "b");
        try
        {
            var registry = new CreationRegistry();
            registry.Register(FixtureCreations.Kind);
            var made = await CreationStore.AddAsync(a, FixtureCreations.Draft(Author, "Tone", 1.0), registry, Now, CancellationToken.None);
            Task<byte[]?> From(string sha256, CancellationToken token) => CreationStore.ReadChunkAsync(a, CreationStore.View(a), sha256, token);
            CreationStore.Commit(b, CreationStore.View(a));
            await CreationStore.ReconcileAsync(b, From, CancellationToken.None);

            var shorter = FixtureCreations.Draft(Author, "Tone", 0.5, frequencyHz: 660);
            var revised = await CreationStore.ReviseAsync(a, made.Id, "Checked", "New text", shorter.Assets, registry, "desktop-a",
                Now.AddMinutes(1), CancellationToken.None);
            Assert.NotNull(revised);
            Assert.True(revised.Revision > made.Revision);
            Assert.Equal((made.Title, made.CreatedAt, "Checked", "New text"), (revised.Title, revised.CreatedAt, revised.Summary, revised.Text));
            Assert.True(CreationStore.IsComplete(a, revised));
            var old = made.Assets!.Select(x => x.Sha256).Except(revised.Assets!.Select(x => x.Sha256)).ToArray();
            Assert.NotEmpty(old);
            Assert.All(old, sha256 => Assert.False(File.Exists(CreationStore.AssetPath(a, sha256))));

            // The other computer gets the new version (the newest revision wins, whichever copy merges first).
            CreationStore.Commit(b, CreationStore.View(a));
            await CreationStore.ReconcileAsync(b, From, CancellationToken.None);
            var there = CreationStore.View(b).Find(made.Id)!;
            Assert.Equal(("Checked", revised.Revision), (there.Summary, there.Revision));
            Assert.True(CreationStore.IsComplete(b, there));
            Assert.Equal("Checked", CreationLibrary.Merge(CreationStore.View(b), CreationLibrary.Empty.Add(made, Now)).Find(made.Id)!.Summary);

            // A creation that is gone isn't brought back, and an asset its kind refuses is refused.
            await CreationStore.RemoveAsync(a, made.Id, "desktop-a", Now.AddMinutes(2), CancellationToken.None);
            Assert.Null(await CreationStore.ReviseAsync(a, made.Id, "Again", null, shorter.Assets, registry, "desktop-a", Now.AddMinutes(3),
                CancellationToken.None));
            var kept = await CreationStore.AddAsync(a, FixtureCreations.Draft(Author, "Tone two", 0.5), registry, Now.AddMinutes(4), CancellationToken.None);
            await Assert.ThrowsAsync<ContractException>(() => CreationStore.ReviseAsync(a, kept.Id, null, null,
                [new("audio", "text/html", new byte[] { 1 })], registry, "desktop-a", Now.AddMinutes(5), CancellationToken.None));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static Creation Entry(string title, bool autoCleanup = false, DateTimeOffset? at = null)
    {
        var asset = CreationLibrary.Asset("audio", "audio/flac", System.Text.Encoding.UTF8.GetBytes(title));
        return new()
        {
            Id = CreationLibrary.NewId(), Kind = "fixture", KindVersion = 1, Title = title, CreatedBy = Author, CreatedAt = at ?? Now,
            Assets = [asset], AutoCleanup = autoCleanup, Revision = 1, UpdatedAt = Now, UpdatedBy = "desktop-a"
        };
    }
}
