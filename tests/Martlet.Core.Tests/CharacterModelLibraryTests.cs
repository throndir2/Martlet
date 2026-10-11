using System.Text;
using Martlet.Core.Characters;
using Martlet.Core.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace Martlet.Core.Tests;

public sealed class CharacterModelLibraryTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static string Hex(long value) => value.ToString("x64");

    private static CharacterModelLibrary Tiny(CharacterModelLibrary library, string prefix, int count, string by)
    {
        for (var i = 0; i < count; i++)
            library = library.Add($"{prefix} {i}", CharacterModelLibrary.Vrm, $"{prefix}{i}.vrm",
                [CharacterModelLibrary.File($"{prefix}{i}.vrm", Encoding.ASCII.GetBytes($"glTF {prefix} {i}"))], by, Start.AddSeconds(i));
        return library;
    }

    // A VRM file entry of the given size with made-up piece SHA-256s: Add and the list check pieces by count and shape only.
    private static CharacterModelFile BigVrm(string path, int bytes, long seed) => new()
    {
        Path = path, Sha256 = Hex(seed), Bytes = bytes,
        Chunks = Enumerable.Range(0, CharacterModelLibrary.ChunkCount(bytes)).Select(i => Hex(seed * 1000 + i + 1)).ToArray()
    };

    [Fact]
    public void Add_refuses_past_the_count_with_a_clear_message_but_a_merge_keeps_every_live_model()
    {
        var a = Tiny(CharacterModelLibrary.Empty, "a", CharacterModelLibrary.MaximumModels, "desk-a");
        var b = Tiny(CharacterModelLibrary.Empty, "b", 3, "desk-b");
        Assert.Equal(CharacterModelLibrary.MaximumModels, a.Live.Count);
        var full = Assert.Throws<ContractException>(() => a.Add("One more", CharacterModelLibrary.Vrm, "more.vrm",
            [CharacterModelLibrary.File("more.vrm", Encoding.ASCII.GetBytes("glTF more"))], "desk-a", Start));
        Assert.Equal(ErrorCode.PayloadTooLarge, full.Code);
        Assert.Contains("full (1,000 characters)", full.Message);

        var merged = CharacterModelLibrary.Merge(a, b);
        Assert.Equal(CharacterModelLibrary.MaximumModels + 3, merged.Live.Count);
        var read = CharacterModelLibrary.Parse(merged.Write());
        Assert.Equal(merged.Digest(), read.Digest());
        Assert.Equal(CharacterModelLibrary.MaximumModels + 3, read.Live.Count);
        Assert.Equal(merged.Digest(), CharacterModelLibrary.Merge(b, a).Digest());

        var renamed = read.Add("Renamed", CharacterModelLibrary.Vrm, "b0.vrm",
            [CharacterModelLibrary.File("b0.vrm", Encoding.ASCII.GetBytes("glTF b 0"))], "desk-b", Start.AddDays(1));
        Assert.Equal("Renamed", renamed.Live.Single(m => m.Name == "Renamed").Name);
        var removed = read.Remove(read.Live[0].Id, "desk-a", Start.AddDays(1));
        Assert.Equal(CharacterModelLibrary.MaximumModels + 2, removed.Live.Count);
    }

    [Fact]
    public void Add_refuses_past_the_total_size_with_a_clear_message_but_a_merge_keeps_every_live_model()
    {
        var count = (int)(CharacterModelLibrary.MaximumTotalBytes / CharacterModelLibrary.MaximumVrmBytes);
        CharacterModelLibrary Fill(string prefix, long seed)
        {
            var library = CharacterModelLibrary.Empty;
            for (var i = 0; i < count; i++)
                library = library.Add($"{prefix} {i}", CharacterModelLibrary.Vrm, $"{prefix}{i}.vrm",
                    [BigVrm($"{prefix}{i}.vrm", CharacterModelLibrary.MaximumVrmBytes, seed + i)], "desk-a", Start.AddSeconds(i));
            return library;
        }
        var a = Fill("a", 1);
        Assert.Equal(CharacterModelLibrary.MaximumTotalBytes, a.Live.Sum(m => m.Bytes));
        var noRoom = Assert.Throws<ContractException>(() => a.Add("One more", CharacterModelLibrary.Vrm, "more.vrm",
            [BigVrm("more.vrm", 1, 999_999)], "desk-a", Start));
        Assert.Equal(ErrorCode.PayloadTooLarge, noRoom.Code);
        Assert.Contains("16 GB together", noRoom.Message);

        var merged = CharacterModelLibrary.Merge(a, Fill("b", 1_000_000));
        Assert.Equal(2 * count, merged.Live.Count);
        Assert.Equal(2 * count, CharacterModelLibrary.Parse(merged.Write()).Live.Count);
    }

    [Fact]
    public void Model_size_limits_follow_the_renderer_and_name_the_numbers()
    {
        var vrm = Assert.Throws<ContractException>(() => CharacterModelLibrary.Empty.Add("Big", CharacterModelLibrary.Vrm, "big.vrm",
            [BigVrm("big.vrm", CharacterModelLibrary.MaximumVrmBytes + 1, 5)], "desk-a", Start));
        Assert.Contains("at most 512 MB", vrm.Message);
        var live2d = Assert.Throws<ContractException>(() => CharacterModelLibrary.Empty.Add("Big", CharacterModelLibrary.Live2D, "m.model3.json",
            [BigVrm("m.model3.json", 100, 6), BigVrm("a.moc3", CharacterModelLibrary.MaximumLive2DFileBytes, 7),
                BigVrm("b.png", CharacterModelLibrary.MaximumLive2DFileBytes, 8), BigVrm("c.png", 1, 9)], "desk-a", Start));
        Assert.Contains("larger than 1 GB", live2d.Message);
        var largest = CharacterModelLibrary.Empty.Add("Largest", CharacterModelLibrary.Live2D, "m.model3.json",
            [BigVrm("m.model3.json", 100, 6), BigVrm("a.moc3", CharacterModelLibrary.MaximumLive2DFileBytes, 7),
                BigVrm("b.png", CharacterModelLibrary.MaximumLive2DFileBytes - 100, 8)], "desk-a", Start);
        Assert.Equal(CharacterModelLibrary.MaximumModelBytes, largest.Live.Single().Bytes);
    }

    [Fact]
    public void Only_the_oldest_tombstones_past_the_limit_are_forgotten()
    {
        var tombstones = Enumerable.Range(1, CharacterModelLibrary.MaximumTombstones + 5).Select(i => new CharacterModel
        {
            Id = Hex(i), Removed = true, Revision = i, UpdatedAt = Start, UpdatedBy = "desk-a"
        });
        var library = new CharacterModelLibrary { SchemaVersion = CharacterModelLibrary.SchemaVersion1, Models = [.. tombstones] };
        var read = CharacterModelLibrary.Parse(library.Write());
        Assert.Equal(CharacterModelLibrary.MaximumTombstones, read.Models.Count);
        Assert.Equal(6, read.Models.Min(m => m.Revision));
    }

    [Fact]
    public void The_largest_list_fits_the_byte_limit()
    {
        // The largest model: 128 files with 240-character paths, 1 GB in as many 3 MB pieces as fit, and the longest name,
        // writers, revision and times. 87 files of 9 MB + 1 byte (4 pieces) and 40 of 6 MB + 1 (3 pieces), and the model3.json.
        var megabyte = 1024 * 1024;
        var entry = new string('m', CharacterModelLibrary.MaximumPathLength - ".model3.json".Length) + ".model3.json";
        var files = new List<CharacterModelFile> { BigVrm(entry, 1_000, 1) };
        for (var i = 0; i < CharacterModelLibrary.MaximumFiles - 1; i++)
            files.Add(BigVrm($"{i:D3}" + new string('x', CharacterModelLibrary.MaximumPathLength - 7) + ".png",
                (i < 87 ? 9 : 6) * megabyte + 1, i + 2));
        var writer = new string('w', 64);
        var time = Start.AddTicks(1_234_567);
        var model = new CharacterModel
        {
            Id = CharacterModelLibrary.ModelId(CharacterModelLibrary.Live2D, entry, files), Name = new string('n', CharacterModelLibrary.MaximumNameLength),
            Renderer = CharacterModelLibrary.Live2D, Entry = entry, Files = [.. files.OrderBy(f => f.Path, StringComparer.Ordinal)], AddedBy = writer,
            AddedAt = time, Revision = long.MaxValue / 4, UpdatedAt = time, UpdatedBy = writer
        };
        Assert.True(model.Bytes <= CharacterModelLibrary.MaximumModelBytes);
        Assert.Equal(469, files.Sum(f => f.Chunks.Count));
        var tombstone = new CharacterModel { Id = Hex(1), Removed = true, Revision = long.MaxValue / 4, UpdatedAt = time, UpdatedBy = writer };
        var empty = CharacterModelLibrary.Empty.Write().Length;
        var modelBytes = (CharacterModelLibrary.Empty with { Models = [model] }).Write().Length - empty;
        var tombstoneBytes = (CharacterModelLibrary.Empty with { Models = [tombstone] }).Write().Length - empty;
        long largest = empty + (long)CharacterModelLibrary.MaximumModels * (modelBytes + 1) +
            (long)CharacterModelLibrary.MaximumTombstones * (tombstoneBytes + 1);
        output.WriteLine($"model {modelBytes:N0} bytes, tombstone {tombstoneBytes:N0} bytes, largest list {largest:N0} of {CharacterModelLibrary.MaximumBytes:N0}");
        Assert.True(largest <= CharacterModelLibrary.MaximumBytes);
    }
}
