using Martlet.Avatar.Hosting;
using Martlet.Avatars;

namespace Martlet.Desktop.Tests;

public sealed class CharacterTouchZoneTests
{
    private static CharacterTouch Touch(double x, double y, string[]? drawables = null, string? bone = null, bool hair = false,
        string[]? hitAreas = null) => new(x, y, hitAreas ?? [], drawables ?? [], bone, null, hair, null, null);

    private static CharacterActionCatalog Catalog(params CharacterActionSource[] extra)
    {
        var gestures = CharacterActionInventory.AllGestures.Where(g => g.Name is "tilt" or "nod" or "blush" or "surprise" or "smile")
            .Select(g => new CharacterActionSource(g.Id, CharacterActionKind.Gesture, g.Name, g.Does));
        var inventory = new CharacterActionInventory("model-1", AvatarRenderer.Live2D, [.. extra, .. gestures]);
        return new(inventory, CharacterActions.Merge(inventory, null));
    }

    [Fact]
    public void ParsesFractionsPixelsAndQwenGrounding()
    {
        var asked = CharacterTouchZones.Parse("Here you go:\n```json\n{\"zones\":[{\"id\":\"top_of_head\",\"box\":[0.4,0.02,0.6,0.1]}," +
            "{\"id\":\"Left Cheek\",\"box\":[0.55,0.15,0.6,0.2]},{\"id\":\"tentacle\",\"box\":[0,0,1,1]}]}\n```", 400, 800)!;
        Assert.Equal(["top_of_head", "cheek_left"], asked.Select(z => z.Id));
        Assert.Equal(0.4, asked[0].Box.X, 3);
        Assert.Equal(0.08, asked[0].Box.Height, 3);

        var pixels = CharacterTouchZones.Parse("[{\"bbox_2d\":[100,40,300,120],\"label\":\"hair\"}]", 400, 800)!;
        Assert.Equal("hair", pixels[0].Id);
        Assert.Equal(0.25, pixels[0].Box.X, 3);
        Assert.Equal(0.05, pixels[0].Box.Y, 3);

        var grid = CharacterTouchZones.Parse("[{\"bbox_2d\":[100,40,900,980],\"label\":\"chest\"}]", 200, 300)!;
        Assert.Equal(0.1, grid[0].Box.X, 3);
        Assert.Equal(0.94, grid[0].Box.Height, 3);

        Assert.Null(CharacterTouchZones.Parse("I can't see a character.", 400, 800));
        Assert.Null(CharacterTouchZones.Parse("{\"zones\":[]}", 400, 800));
    }

    [Theory]
    [InlineData("groin")]
    [InlineData("Crotch")]
    [InlineData("pelvis")]
    [InlineData("between legs")]
    [InlineData("genital area")]
    public void FindsTheGroinZoneByItsCommonNames(string name)
    {
        Assert.Equal("groin", CharacterTouchZones.Normalize(name));
        Assert.True(CharacterTouchZones.Kind("groin")!.Intimate);
    }

    [Fact]
    public void IncludeIntimateZonesNamesEveryIntimatePart()
    {
        // The check box's words (and the Thinking model's) name each intimate kind, left and right together.
        foreach (var kind in CharacterTouchZones.Kinds.Where(k => k.Intimate))
            Assert.Contains(kind.Label.ToLowerInvariant().Replace("left ", "").Replace("right ", ""), CharacterTouchZones.IntimateParts);
        Assert.Contains("breasts", CharacterTouchZones.IntimateParts);
        Assert.Contains("groin", CharacterTouchZones.IntimateParts);
        Assert.Contains(CharacterTouchZones.Kinds, k => k.Id is "breast_left" or "breast_right" or "groin");
    }

    [Fact]
    public void BindsDrawablesMostlyInsideAndBonesInsideEachBox()
    {
        var zones = new[]
        {
            new CharacterTouchZone { Id = "top_of_head", Box = new(0.25, 0, 0.5, 0.25) },
            new CharacterTouchZone { Id = "hand_right", Box = new(0, 0.5, 0.25, 0.25) }
        };
        // The snapshot sat in the middle half of the page.
        var crop = new TouchZoneBox(0.25, 0.25, 0.5, 0.5);
        var probe = new RendererZoneProbe(
            [new("HairTop", 0.4, 0.26, 0.6, 0.3), new("Body", 0.3, 0.3, 0.7, 0.75), new("HandR", 0.26, 0.52, 0.3, 0.6)],
            [new("head", 0.5, 0.3), new("rightHand", 0.28, 0.55)]);

        var bound = CharacterTouchZones.Bind(zones, crop, probe);

        Assert.Equal(["HairTop"], bound[0].Drawables);
        Assert.Equal(["head"], bound[0].Bones);
        Assert.Equal(["HandR"], bound[1].Drawables);
        Assert.Equal(["rightHand"], bound[1].Bones);
    }

    [Fact]
    public void MatchesDrawableThenBoneThenBoxThenCoarseAndSkipsIntimateZonesUnlessIncluded()
    {
        var settings = new CharacterTouchZoneSettings
        {
            ModelId = "model-1", Crop = new(0, 0, 1, 1), IncludeIntimate = false,
            Zones =
            [
                new() { Id = "face", Box = new(0.3, 0.1, 0.4, 0.3), Drawables = ["Face", "Body"] },
                new() { Id = "cheek_left", Box = new(0.55, 0.25, 0.1, 0.1), Drawables = ["Face"] },
                new() { Id = "chest", Box = new(0.3, 0.45, 0.4, 0.2), Drawables = ["Body"] },
                new() { Id = "hand_right", Box = new(0.05, 0.6, 0.1, 0.1), Bones = ["rightHand"] },
                new() { Id = "hair", Box = new(0.2, 0, 0.6, 0.3) }
            ]
        };

        // The topmost drawable that belongs to a zone wins; with several, the smallest whose box holds the point.
        Assert.Equal(("cheek_left", "drawable"), Of(CharacterTouchZones.Match(settings, Touch(0.6, 0.3, ["Unknown", "Face"]))));
        Assert.Equal(("face", "drawable"), Of(CharacterTouchZones.Match(settings, Touch(0.4, 0.2, ["Face"]))));
        Assert.Equal(("hand_right", "bone"), Of(CharacterTouchZones.Match(settings, Touch(0.5, 0.9, bone: "rightHand"))));
        Assert.Equal(("hair", "hair"), Of(CharacterTouchZones.Match(settings, Touch(0.5, 0.05, hair: true))));
        Assert.Equal(("hand_right", "box"), Of(CharacterTouchZones.Match(settings, Touch(0.1, 0.65))));
        // Chest is intimate and off: its drawable falls through to the face zone that also holds it.
        Assert.Equal(("face", "drawable"), Of(CharacterTouchZones.Match(settings, Touch(0.5, 0.5, ["Body"]))));
        Assert.Equal(("chest", "drawable"), Of(CharacterTouchZones.Match(settings with { IncludeIntimate = true }, Touch(0.5, 0.5, ["Body"]))));
        // Nothing found there: the rough zone's default.
        Assert.Equal(("foot_right", "coarse"), Of(CharacterTouchZones.Match(settings, Touch(0.9, 0.95, ["ArtMesh_Foot"]))));
        Assert.Equal(("top_of_head", "coarse"), Of(CharacterTouchZones.Match(null, Touch(0.5, 0.05, hitAreas: ["Head"]))));
    }

    [Fact]
    public void ZonesSavedWithTellTheCharacterLoadWithMartletNotices()
    {
        var directory = Directory.CreateTempSubdirectory("martlet-touch-").FullName;
        try
        {
            File.WriteAllText(CharacterTouchZones.Path(directory), """
                { "version": 1, "models": [ { "model_id": "m", "updated_at": "2026-01-01T00:00:00+00:00", "zones": [
                  { "id": "top_of_head", "box": { "x": 0.4, "y": 0, "width": 0.2, "height": 0.1 },
                    "reaction": { "tell": true, "narration": "*ruffles your hair*", "cooldown_seconds": 4 } },
                  { "id": "nose", "box": { "x": 0.4, "y": 0.2, "width": 0.1, "height": 0.1 }, "reaction": { "tell": false } } ] } ] }
                """);
            var zones = CharacterTouchZones.Load(directory, "m")!.Zones;
            Assert.True(zones[0].Reaction.Notices);
            Assert.Equal("*ruffles your hair*", CharacterTouchZones.Narration(zones[0]));
            Assert.False(zones[1].Reaction.Notices);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ZonesSayWhereTheyAreAndWhichTapsArePats()
    {
        CharacterTouchZone Zone(string id, string? label = null) => new() { Id = id, Label = label, Box = new(0, 0, 1, 1) };
        Assert.Equal("the top of your head", CharacterTouchZones.Part(Zone("top_of_head")));
        Assert.Equal("your left cheek", CharacterTouchZones.Part(Zone("cheek_left")));
        Assert.Equal("your crown", CharacterTouchZones.Part(Zone("top_of_head", "Crown")));
        Assert.True(CharacterTouchZones.Pats(Zone("hair")));
        Assert.True(CharacterTouchZones.Pats(Zone("top_of_head")));
        Assert.False(CharacterTouchZones.Pats(Zone("cheek_left")));
    }

    private static (string, string)? Of(TouchZoneMatch? match) => match is null ? null : (match.Zone.Id, match.How);

    [Fact]
    public void DefaultReactionsUseWhatTheModelHasAndOwnerChoicesWin()
    {
        var blush = new CharacterActionSource("expression:Blush", CharacterActionKind.Expression, "Blush", "cheeks");
        var catalog = Catalog(blush);

        // The model's own blush expression comes before Martlet's blush gesture; the surprise gesture is the second slot.
        Assert.Equal(["Blush", "surprise"], CharacterTouchZones.Plan(new() { Id = "lips", Box = new(0, 0, 1, 1) }, catalog).Select(s => s.Name));
        // No lean_in here yet: the head pat tilts, then smiles.
        Assert.Equal(["tilt", "smile"], CharacterTouchZones.Plan(new() { Id = "top_of_head", Box = new(0, 0, 1, 1) }, catalog).Select(s => s.Name));
        var chosen = new CharacterTouchZone { Id = "nose", Box = new(0, 0, 1, 1), Reaction = new() { Actions = ["gesture:nod"], Notices = true } };
        Assert.Equal(["nod"], CharacterTouchZones.Plan(chosen, catalog).Select(s => s.Name));
        // The zone's built-in line is no hint; only the owner's own words go with a noticed touch.
        Assert.Null(CharacterTouchZones.Narration(chosen));
        Assert.Equal("*boops you*", CharacterTouchZones.Narration(chosen with { Reaction = chosen.Reaction with { Narration = "*boops you*" } }));
        Assert.Null(CharacterTouchZones.Narration(chosen with { Reaction = new() { Narration = "*boops you*" } }));
        Assert.Empty(CharacterTouchZones.Plan(chosen with { Reaction = new() { Actions = [] } }, catalog));
        Assert.Null(CharacterTouchZones.Narration(chosen with { Reaction = new() }));
    }

    [Fact]
    public async Task SavesPerModelAndKeepsOwnerEditsWhenDetectedAgain()
    {
        var directory = Directory.CreateTempSubdirectory("martlet-touch-").FullName;
        try
        {
            var first = CharacterTouchZones.Detected(null, "model-1", [new() { Id = "top_of_head", Box = new(0.4, 0, 0.2, 0.1) }], null, null,
                DateTimeOffset.Now);
            // Intimate zones are included unless the owner turns them off, and off stays off once saved.
            Assert.True(first.IncludeIntimate);
            Assert.True(new CharacterTouchZoneSettings { ModelId = "model-3" }.IncludeIntimate);
            await CharacterTouchZones.SaveAsync(directory, new() { ModelId = "model-3", IncludeIntimate = false }, DateTimeOffset.Now);
            Assert.False(CharacterTouchZones.Load(directory, "model-3")!.IncludeIntimate);
            var edited = first with
            {
                IncludeIntimate = true,
                Zones = [first.Zones[0] with { Label = "Crown", Enabled = false, Reaction = new() { Notices = true, CooldownSeconds = 9 } }]
            };
            await CharacterTouchZones.SaveAsync(directory, edited, DateTimeOffset.Now);
            await CharacterTouchZones.SaveAsync(directory, new() { ModelId = "model-2" }, DateTimeOffset.Now);

            var loaded = CharacterTouchZones.Load(directory, "model-1")!;
            Assert.True(loaded.IncludeIntimate);
            Assert.Equal("Crown", loaded.Zones[0].Name);
            Assert.Equal(3, CharacterTouchZones.LoadAll(directory).Count);

            var again = CharacterTouchZones.Detected(loaded, "model-1",
                [new() { Id = "top_of_head", Box = new(0.3, 0, 0.3, 0.1) }, new() { Id = "hand_left", Box = new(0.8, 0.5, 0.1, 0.1) }],
                new(0, 0, 1, 1), new RendererZoneProbe(null, [new("leftHand", 0.85, 0.55)]), DateTimeOffset.Now);
            Assert.Equal(0.3, again.Zones[0].Box.X);
            Assert.False(again.Zones[0].Enabled);
            Assert.Equal(9, again.Zones[0].Reaction.CooldownSeconds);
            Assert.True(again.Zones[0].Reaction.Notices);
            Assert.DoesNotContain("\"tell\"", File.ReadAllText(CharacterTouchZones.Path(directory)), StringComparison.Ordinal);
            Assert.Equal(["leftHand"], again.Zones[1].Bones);
            Assert.True(again.IncludeIntimate);

            Assert.NotNull(CharacterTouchZones.Problem(again with { Zones = [again.Zones[0] with { Box = new(0.9, 0.9, 0.5, 0.5) }] }));
        }
        finally { Directory.Delete(directory, true); }
    }
}
