using System.Text;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;

namespace Martlet.Desktop.Tests;

/// <summary>Companion › Emotes and motions' words (<see cref="EmotesPageModel"/> in Pages\Emotes), without WPF.</summary>
public sealed class EmotesPageModelTests
{
    private static readonly byte[] Expression = Encoding.UTF8.GetBytes(
        "{\"Type\":\"Live2D Expression\",\"Parameters\":[{\"Id\":\"ParamCheek\",\"Value\":1}]}");

    // A Live2D model with two expressions (Smile and Glasses) and one motion (Wave).
    private static CharacterActionCatalog Catalog(params CharacterCombo[] combos)
    {
        var model = Encoding.UTF8.GetBytes("{\"Version\":3,\"FileReferences\":{\"Moc\":\"m.moc3\",\"Textures\":[]," +
            "\"Expressions\":[{\"Name\":\"Smile\",\"File\":\"smile.exp3.json\"},{\"Name\":\"Glasses\",\"File\":\"glasses.exp3.json\"}]," +
            "\"Motions\":{\"Wave\":[{\"File\":\"wave.motion3.json\"}]}}}");
        var motion = Encoding.UTF8.GetBytes("{\"Version\":3,\"Meta\":{\"Duration\":1},\"Curves\":[]}");
        var inventory = CharacterActionInventory.From(AvatarRenderer.Live2D, "m.model3.json",
        [
            new("m.model3.json", model, "application/json"), new("m.moc3", new byte[64], "application/octet-stream"),
            new("smile.exp3.json", Expression, "application/json"), new("glasses.exp3.json", Expression, "application/json"),
            new("wave.motion3.json", motion, "application/json")
        ]);
        return new(inventory, CharacterActions.Merge(inventory, null) with { Combos = combos.Length == 0 ? null : combos });
    }

    [Fact]
    public void The_status_line_says_why_the_emotes_are_missing_or_how_many_there_are()
    {
        Assert.Equal("Martlet couldn't read this model's emotes and motions: no model file.", EmotesPageModel.Status("no model file.", null));
        Assert.Equal("Reading the character's emotes and motions...", EmotesPageModel.Status(null, null));
        var catalog = Catalog();
        Assert.Equal("2 emotes, 1 motion and Martlet's nod and shake.", EmotesPageModel.Counts(catalog.Inventory));
        Assert.Equal("2 emotes, 1 motion and Martlet's nod and shake.; named from the model's own files.", EmotesPageModel.Status(null, catalog));
    }

    [Fact]
    public void Each_row_says_its_kind_and_the_lingering_emotes_are_listed()
    {
        var catalog = Catalog();
        Assert.Equal("emote", EmotesPageModel.Kind(catalog.Inventory.Find("expression:Smile")!));
        Assert.Equal("motion", EmotesPageModel.Kind(catalog.Inventory.Find("motion:Wave")!));
        Assert.Equal("Martlet gesture", EmotesPageModel.Kind(catalog.Inventory.Sources.First(s => s.Kind == CharacterActionKind.Gesture)));

        Assert.Equal("No lingering emotes are on.", EmotesPageModel.HeldText([], DateTimeOffset.Now));
        var glasses = catalog.Inventory.Find("expression:Glasses")!;
        var held = EmotesPageModel.HeldText([new HeldEmote(glasses, "m.model3.json", DateTimeOffset.Now)], DateTimeOffset.Now);
        Assert.StartsWith("On now: Glasses (", held);
        Assert.EndsWith(". Clear emotes on the character's menu turns them off.", held);
    }

    [Fact]
    public void The_combos_line_says_which_tags_replies_get()
    {
        Assert.Equal("No combos yet.", EmotesPageModel.CombosStatus(Catalog()));
        var agree = new CharacterCombo { Tag = "agree", Parts = ["expression:Smile", "motion:Wave"] };
        Assert.Equal("1 combo, and replies can use 1: {agree}.", EmotesPageModel.CombosStatus(Catalog(agree)));
        Assert.Equal("1 combo, but replies aren't offered any: a combo needs to be on, with a part that is on.",
            EmotesPageModel.CombosStatus(Catalog(agree with { Enabled = false })));
    }

    [Fact]
    public void Offered_lists_the_tags_replies_can_use()
    {
        var offered = EmotesPageModel.Offered(Catalog(), null);
        Assert.StartsWith("Replies can use ", offered);
        Assert.EndsWith(".", offered);
    }
}
