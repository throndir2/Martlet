using System.Text;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;

namespace Martlet.Desktop.Tests;

public sealed class KeptMotionInventoryTests
{
    private static CharacterActionInventory Vrm()
    {
        var bones = string.Join(",", new[] { "hips", "spine", "head", "rightUpperArm", "rightLowerArm" }.Select((b, i) => $"\"{b}\":{{\"node\":{i}}}"));
        var glb = CharacterGestureTests.Glb("{\"asset\":{\"version\":\"2.0\"},\"extensions\":{\"VRMC_vrm\":{\"specVersion\":\"1.0\",\"humanoid\":{\"humanBones\":{" +
            bones + "}},\"expressions\":{\"preset\":{\"happy\":{}}}}}}");
        return CharacterActionInventory.From(AvatarRenderer.Vrm, "m.vrm", [new("m.vrm", glb, "model/gltf-binary")]);
    }

    private static readonly KeptMotion Cheer = new("3f9a0c1b2d4e", "cheer", "A person raises both fists above the head and jumps once.", 3);
    private static readonly KeptMotion Clap = new("aa11bb22cc33", "clap", "A person claps the hands in front of the chest four times.", 3);

    [Fact]
    public void A_vrm_gets_every_kept_motion_last_with_its_name_as_tag_and_what_it_does_as_hint()
    {
        var plain = Vrm();
        var inventory = plain.WithKeptMotions([Cheer, Clap]);
        Assert.Equal(plain.ModelId, inventory.ModelId);
        Assert.Equal([.. plain.Sources.Select(s => s.Id), "motion:kept-3f9a0c1b2d4e", "motion:kept-aa11bb22cc33"], inventory.Sources.Select(s => s.Id));
        var cheer = inventory.Find("motion:kept-3f9a0c1b2d4e")!;
        Assert.Equal(CharacterActionKind.Motion, cheer.Kind);
        Assert.Equal("cheer", cheer.Name);
        Assert.Equal("3f9a0c1b2d4e", CharacterActionInventory.KeptMotionKey(cheer.Id));
        Assert.Null(CharacterActionInventory.KeptMotionKey("motion:TapBody"));
        Assert.DoesNotContain(cheer, CharacterActions.Nameable(inventory));

        var catalog = new CharacterActionCatalog(inventory, CharacterActions.Merge(inventory, null));
        var prompt = catalog.Prompt(null, null)!;
        Assert.Contains("{cheer}", prompt.Tags);
        Assert.Contains("{cheer} - A person raises both fists above the head and jumps once.", prompt.Instructions);
        Assert.Equal([cheer], catalog.For("{cheer}"));
        // The emote lines before the kept motions are those the VRM had without them, so prompt caches keep working.
        static CharacterActionCatalog Bare(CharacterActionInventory i) => new(i, CharacterActions.Merge(i, null) with { Combos = null });
        var beforeLines = Lines(Bare(plain).Prompt(null, null)!.Instructions).Where(l => l.StartsWith('{')).ToArray();
        var afterLines = Lines(Bare(inventory).Prompt(null, null)!.Instructions).Where(l => l.StartsWith('{')).ToArray();
        Assert.Equal(beforeLines, afterLines.Take(beforeLines.Length));
        Assert.StartsWith("{cheer} - ", afterLines[beforeLines.Length]);
        Assert.Equal(inventory.Sources, inventory.WithKeptMotions([Cheer, Clap]).Sources);
        Assert.Equal(plain.Sources, inventory.WithKeptMotions([]).Sources);
    }

    [Fact]
    public void A_live2d_model_gets_no_kept_motions_and_a_vrm_keeps_within_its_limits()
    {
        var model = Encoding.UTF8.GetBytes("{\"Version\":3,\"FileReferences\":{\"Moc\":\"m.moc3\",\"Textures\":[]}}");
        var live2D = CharacterActionInventory.From(AvatarRenderer.Live2D, "m.model3.json",
            [new("m.model3.json", model, "application/json"), new("m.moc3", CharacterGestureTests.Moc("ParamAngleY"), "application/octet-stream")]);
        Assert.Same(live2D, live2D.WithKeptMotions([Cheer]));

        var many = Enumerable.Range(0, 200).Select(i => new KeptMotion($"{i:x12}", "m" + i, "A person jumps.", 2)).ToArray();
        var full = Vrm().WithKeptMotions(many);
        var kept = full.Sources.Where(s => CharacterActionInventory.IsKeptMotion(s.Id)).ToArray();
        Assert.True(full.Sources.Count <= CharacterActionInventory.MaximumSources);
        Assert.True(kept.Length <= CharacterActionInventory.MaximumKeptMotions);
        // The newest stay (the list is oldest first).
        Assert.Equal("m199", kept[^1].Name);
    }

    private static string[] Lines(string text) => text.Split('\n');
}
