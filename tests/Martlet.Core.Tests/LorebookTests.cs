using System.Buffers.Binary;
using System.Text;
using Martlet.Core.Contracts;
using Martlet.Core.Lorebooks;
using Martlet.Core.Settings;

namespace Martlet.Core.Tests;

public sealed class LorebookTests
{
    private static LorebookEntry Entry(int uid, string content, params string[] keys) => new() { Uid = uid, Content = content, Keys = keys };

    private static LorebookLibrary Library(params LorebookEntry[] entries) => LorebookLibrary.Create() with
    {
        Books = [new Lorebook { Id = Guid.NewGuid(), Name = "World", Entries = entries }]
    };

    private static string[] Scan(LorebookLibrary library, string current, params string[] earlier) =>
        LorebookScanner.Scan(library, new(current, earlier), _ => 0).Included.Select(hit => hit.Content).ToArray();

    [Fact]
    public void KeywordsTriggerWithinScanDepthAsWholeWords()
    {
        var library = Library(Entry(0, "Echo is Aria's lute.", "lute"), Entry(1, "Cats rule.", "cat"));
        Assert.Equal(["Echo is Aria's lute."], Scan(library, "Play your LUTE?"));
        Assert.Empty(Scan(library, "What category is it?"));
        Assert.Equal(["Cats rule."], Scan(library with { MatchWholeWords = false }, "What category is it?"));
        Assert.Equal(["Echo is Aria's lute."], Scan(library, "Sure.", "The lute is out of tune"));
        Assert.Empty(Scan(library with { ScanDepth = 1 }, "Sure.", "The lute is out of tune"));
        Assert.Empty(Scan(library with { CaseSensitive = true }, "Play your LUTE?"));
    }

    [Fact]
    public void SecondaryLogicConstantRegexAndPersonaScope()
    {
        var persona = Guid.NewGuid();
        var library = LorebookLibrary.Create() with
        {
            Books =
            [
                new Lorebook
                {
                    Id = Guid.NewGuid(), Name = "Aria",
                    Activation = LorebookActivation.SelectedPersonas, PersonaIds = [persona],
                    Entries =
                    [
                        new() { Uid = 0, Content = "{{char}} hates rain.", Constant = true },
                        new() { Uid = 1, Content = "Inn at night.", Keys = ["inn"], SecondaryKeys = ["night", "evening"] },
                        new() { Uid = 2, Content = "Inn by day.", Keys = ["inn"], SecondaryKeys = ["night"], SecondaryLogic = LorebookSecondaryLogic.NotAny },
                        new() { Uid = 3, Content = "Dragons!", Keys = ["/drag(on|ons)\\b/i"] }
                    ]
                }
            ]
        };
        Assert.Empty(LorebookScanner.Scan(library, new("the inn tonight", []), _ => 0).Included);
        var hits = LorebookScanner.Scan(library, new("Back to the inn at night, DRAGONS overhead", [], persona, "Aria"), _ => 0).Included;
        Assert.Equal(["Aria hates rain.", "Inn at night.", "Dragons!"], hits.Select(hit => hit.Content));
        hits = LorebookScanner.Scan(library, new("the inn", [], persona, "Aria"), _ => 0).Included;
        Assert.Equal(["Aria hates rain.", "Inn by day."], hits.Select(hit => hit.Content));
    }

    [Fact]
    public void BudgetKeepsHigherOrderAndRecursionChains()
    {
        var library = Library(
            Entry(0, "The castle belongs to Queen Mab.", "castle") with { Order = 10 },
            Entry(1, "Queen Mab rules the fae.", "Mab") with { Order = 50 },
            Entry(2, new string('x', 300), "castle") with { Order = 100 }) with { BudgetUtf8Bytes = 330 };
        var result = LorebookScanner.Scan(library, new("Show me the castle", []), _ => 0);
        Assert.Equal([2], result.Included.Select(hit => hit.Entry.Uid));
        Assert.Equal([0], result.OverBudget.Select(hit => hit.Entry.Uid));

        library = library with { BudgetUtf8Bytes = 4096 };
        Assert.Equal([2, 0, 1], LorebookScanner.Scan(library, new("Show me the castle", []), _ => 0).Included.Select(hit => hit.Entry.Uid));
        Assert.Equal([2, 0], LorebookScanner.Scan(library with { Recursive = false }, new("Show me the castle", []), _ => 0)
            .Included.Select(hit => hit.Entry.Uid));
        Assert.Empty(LorebookScanner.Scan(library with
        {
            Books = [library.Books[0] with { Entries = [Entry(0, "Chance.", "castle") with { Probability = 30 }] }]
        }, new("castle", []), _ => 50).Included);
    }

    [Fact]
    public void SillyTavernWorldInfoRoundTrips()
    {
        const string json = """
            {"entries":{"0":{"uid":0,"key":["Echo","lute"],"keysecondary":["play"],"comment":"Lute","content":"Echo is Aria's lute.",
              "constant":false,"selective":true,"selectiveLogic":3,"order":42,"position":0,"disable":false,"probability":60,"useProbability":true,
              "scanDepth":3,"caseSensitive":true,"matchWholeWords":null,"excludeRecursion":true,"preventRecursion":false},
             "1":{"uid":1,"key":[],"content":"Always.","constant":true,"position":4,"disable":true},
             "2":{"uid":2,"key":["empty"],"content":"  "}}}
            """;
        var import = SillyTavernLorebooks.Import(new MemoryStream(Encoding.UTF8.GetBytes(json)), "Aria world");
        Assert.Equal("Aria world", import.Book.Name);
        Assert.Equal(1, import.SkippedEntries);
        var lute = import.Book.Entries[0];
        Assert.Equal(["Echo", "lute"], lute.Keys);
        Assert.Equal(["play"], lute.SecondaryKeys);
        Assert.Equal((LorebookSecondaryLogic.AndAll, 42, LorebookPosition.BeforePersona, 60, (int?)3, (bool?)true, (bool?)null, true),
            (lute.SecondaryLogic, lute.Order, lute.Position, lute.Probability, lute.ScanDepth, lute.CaseSensitive,
                lute.MatchWholeWords, lute.ExcludeRecursion));
        Assert.True(import.Book.Entries[1] is { Constant: true, Enabled: false, Position: LorebookPosition.AfterPersona });

        var again = SillyTavernLorebooks.Import(new MemoryStream(SillyTavernLorebooks.Export(import.Book)), "x");
        Assert.Equal("Aria world", again.Book.Name);
        Assert.Equal(import.Book.Entries.Count, again.Book.Entries.Count);
        string[] none = [];
        foreach (var (before, after) in import.Book.Entries.Zip(again.Book.Entries))
        {
            Assert.Equal(before.Keys, after.Keys);
            Assert.Equal(before.SecondaryKeys, after.SecondaryKeys);
            Assert.Equal(before with { Keys = none, SecondaryKeys = none }, after with { Keys = none, SecondaryKeys = none });
        }
    }

    [Fact]
    public void CharacterCardBookComesAlongFromPngAndCardImport()
    {
        const string card = """
            {"spec":"chara_card_v2","data":{"name":"Aria","description":"A bard.","character_book":{"entries":[
              {"keys":[],"content":"Always lore.","enabled":true,"insertion_order":0,"constant":true},
              {"keys":["inn"],"secondary_keys":["night"],"selective":true,"content":"The inn is the Gilded Goose.","enabled":true,
               "insertion_order":7,"position":"before_char","extensions":{"selectiveLogic":2,"probability":80,"useProbability":true}},
              {"keys":["lute"],"content":"Regex on.","enabled":true,"insertion_order":1,"use_regex":true}]}}}
            """;
        var parsed = CharacterCardReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(card)));
        Assert.Equal(3, parsed.Lorebook!.Book.Entries.Count);
        Assert.Equal("Aria lore", parsed.Lorebook.Book.Name);

        var import = SillyTavernLorebooks.Import(Png(Encoding.ASCII.GetBytes(Convert.ToBase64String(Encoding.UTF8.GetBytes(card)))), "file");
        var inn = import.Book.Entries[1];
        Assert.Equal((LorebookPosition.BeforePersona, LorebookSecondaryLogic.NotAny, 80, 7),
            (inn.Position, inn.SecondaryLogic, inn.Probability, inn.Order));
        Assert.Equal(["night"], inn.SecondaryKeys);
        Assert.Equal(["/lute/i"], import.Book.Entries[2].Keys);

        var error = Assert.Throws<ContractException>(() => SillyTavernLorebooks.Import(
            new MemoryStream("""{"spec":"chara_card_v2","data":{"name":"Bo","description":"x"}}"""u8.ToArray()), "file"));
        Assert.Contains("no lorebook", error.Message);
    }

    [Fact]
    public async Task StoreSavesAtomicallyAndRefusesStaleRevisions()
    {
        var directory = Directory.CreateTempSubdirectory("martlet-lore-");
        try
        {
            var store = new LorebookStore(directory.FullName);
            var empty = await store.LoadAsync();
            Assert.True(empty.Loaded);
            Assert.Null(empty.Revision);
            var library = Library(Entry(0, "Echo is a lute.", "lute"));
            var saved = await store.SaveAsync(library, null);
            Assert.True(saved.Saved);
            Assert.False((await store.SaveAsync(library, null)).Saved);
            var reloaded = await new LorebookStore(directory.FullName).LoadAsync();
            Assert.Equal(saved.Revision, reloaded.Revision);
            Assert.Equal("Echo is a lute.", reloaded.Library.Books[0].Entries[0].Content);

            await File.WriteAllTextAsync(store.FilePath, "{ not json");
            var broken = await store.LoadAsync();
            Assert.False(broken.Loaded);
            Assert.False((await store.UpdateAsync(current => current)).Saved);
            Assert.Equal("{ not json", await File.ReadAllTextAsync(store.FilePath));
        }
        finally { directory.Delete(recursive: true); }
    }

    private static MemoryStream Png(byte[] chara)
    {
        var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Chunk("IHDR", [0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0]);
        Chunk("tEXt", [.. "chara"u8, 0, .. chara]);
        Chunk("IEND", []);
        png.Position = 0;
        return png;

        void Chunk(string type, byte[] data)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
            png.Write(length);
            png.Write(Encoding.ASCII.GetBytes(type));
            png.Write(data);
            png.Write(new byte[4]);
        }
    }
}
