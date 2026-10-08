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

    // A standing VRM character on the page (fractions), facing the viewer: its left side is on the picture's right.
    private static readonly RendererZoneProbe Skeleton = new(null,
    [
        new("hips", 0.5, 0.55), new("spine", 0.5, 0.5), new("chest", 0.5, 0.42), new("neck", 0.5, 0.3), new("head", 0.5, 0.27),
        new("leftEye", 0.52, 0.2), new("rightEye", 0.48, 0.2), new("leftUpperLeg", 0.52, 0.56), new("leftLowerLeg", 0.53, 0.72),
        new("leftFoot", 0.53, 0.88), new("leftToes", 0.53, 0.92), new("rightUpperLeg", 0.48, 0.56), new("rightLowerLeg", 0.47, 0.72),
        new("leftUpperArm", 0.56, 0.32), new("leftLowerArm", 0.58, 0.44), new("leftHand", 0.6, 0.55)
    ]);

    [Fact]
    public void AVrmZoneBindsTheBonesOfThePartItLiesOn()
    {
        CharacterTouchZone Zone(string id, double x, double y, double width, double height) => new() { Id = id, Box = new(x, y, width, height) };

        var bones = CharacterTouchZones.Bind(
        [
            Zone("knee_left", 0.515, 0.7, 0.03, 0.04), Zone("calf_left", 0.515, 0.75, 0.03, 0.1), Zone("cheek_left", 0.51, 0.21, 0.02, 0.03),
            Zone("breast_left", 0.505, 0.34, 0.04, 0.05), Zone("groin", 0.485, 0.56, 0.03, 0.04), Zone("buttocks", 0.45, 0.575, 0.1, 0.035)
        ], null, Skeleton).ToDictionary(z => z.Id, z => z.Bones);

        // The knee holds the shin bone's joint, which the thigh bone's part ends at; the calf the shin bone's part (knee to ankle).
        Assert.Equal(["leftLowerLeg", "leftUpperLeg"], bones["knee_left"]);
        Assert.Equal(["leftLowerLeg"], bones["calf_left"]);
        // The hips move the pelvis, from their joint down to the crotch (a quarter of the way from the hip joints to the knees),
        // even with their joint level with the hip joints.
        Assert.Equal(["hips"], bones["groin"]);
        Assert.Equal(["hips", "leftUpperLeg", "rightUpperLeg"], bones["buttocks"]);
        // No joint inside and no part crossing: the bones of the zone's own part of the body whose parts pass near it (the
        // head, not an eye or the neck; the chest).
        Assert.Equal(["head"], bones["cheek_left"]);
        Assert.Equal(["chest"], bones["breast_left"]);
    }

    [Fact]
    public void AVrmTapTakesTheSmallestZoneOnTheTouchedPartUnderItAndAMovedPartItsOwnZone()
    {
        // Bound as before this change: only the face holds the head bone, the knee the shin bone and the calf the ankle.
        var settings = new CharacterTouchZoneSettings
        {
            ModelId = "model-1", Crop = new(0, 0, 1, 1),
            Zones =
            [
                new() { Id = "face", Box = new(0.45, 0.18, 0.1, 0.1), Bones = ["head", "leftEye", "rightEye"] },
                new() { Id = "cheek_left", Box = new(0.51, 0.22, 0.03, 0.03) }, new() { Id = "lips", Box = new(0.485, 0.255, 0.03, 0.015) },
                new() { Id = "chest", Box = new(0.45, 0.32, 0.1, 0.12), Bones = ["chest"] }, new() { Id = "breast_left", Box = new(0.505, 0.34, 0.04, 0.05) },
                new() { Id = "knee_left", Box = new(0.515, 0.7, 0.03, 0.04), Bones = ["leftLowerLeg"] },
                new() { Id = "calf_left", Box = new(0.515, 0.75, 0.03, 0.1), Bones = ["leftFoot"] },
                new() { Id = "hand_left", Box = new(0.58, 0.52, 0.04, 0.06), Bones = ["leftHand"] }
            ]
        };

        // The head bone moves the whole head and the shin bone the knee to the ankle: the zone under the tap wins.
        Assert.Equal(("cheek_left", "box"), Of(CharacterTouchZones.Match(settings, Touch(0.52, 0.235, bone: "head"))));
        Assert.Equal(("lips", "box"), Of(CharacterTouchZones.Match(settings, Touch(0.5, 0.26, bone: "head"))));
        Assert.Equal(("face", "bone"), Of(CharacterTouchZones.Match(settings, Touch(0.47, 0.2, bone: "head"))));
        Assert.Equal(("breast_left", "box"), Of(CharacterTouchZones.Match(settings, Touch(0.52, 0.36, bone: "chest"))));
        Assert.Equal(("calf_left", "box"), Of(CharacterTouchZones.Match(settings, Touch(0.53, 0.8, bone: "leftLowerLeg"))));
        Assert.Equal(("knee_left", "bone"), Of(CharacterTouchZones.Match(settings, Touch(0.53, 0.72, bone: "leftLowerLeg"))));
        // A hand raised to the face is still the hand, and a part no box holds any more finds the zone that holds its bone.
        Assert.Equal(("hand_left", "bone"), Of(CharacterTouchZones.Match(settings, Touch(0.52, 0.235, bone: "leftHand"))));
        Assert.Equal(("calf_left", "bone"), Of(CharacterTouchZones.Match(settings, Touch(0.53, 0.95, bone: "leftFoot"))));
        // The zones lie on the character's own side.
        Assert.False(CharacterTouchZones.OnPart("calf_left", "rightLowerLeg"));
        Assert.True(CharacterTouchZones.OnPart("groin", "hips"));
        Assert.False(CharacterTouchZones.OnPart("cheek_left", "leftHand"));
    }

    // Zones that overlap: the groin and the left thigh where they meet, the nose and the left cheek, the hair's box around the
    // face, and the left hand held in front of the thigh.
    private static CharacterTouchZoneSettings Overlapping() => new()
    {
        ModelId = "model-1", Crop = new(0, 0, 1, 1),
        Zones =
        [
            new() { Id = "hair", Box = new(0.3, 0.02, 0.4, 0.3) },
            new() { Id = "eye_left", Box = new(0.52, 0.15, 0.06, 0.04) },
            new() { Id = "nose", Box = new(0.48, 0.18, 0.04, 0.06) },
            new() { Id = "cheek_left", Box = new(0.5, 0.2, 0.08, 0.06) },
            new() { Id = "groin", Box = new(0.44, 0.55, 0.12, 0.08) },
            new() { Id = "thigh_left", Box = new(0.5, 0.58, 0.12, 0.2) },
            new() { Id = "hand_left", Box = new(0.56, 0.64, 0.06, 0.06), Drawables = ["HandL"], Bones = ["leftHand"] }
        ]
    };

    private static string[] Ids(IReadOnlyList<CharacterTouchZone> zones) => [.. zones.Select(z => z.Id)];

    private static string[] On(CharacterTouchZoneSettings settings, CharacterTouch touch) =>
        Ids(CharacterTouchZones.Touched(settings, touch, CharacterTouchZones.Match(settings, touch)));

    [Fact]
    public void ATouchWhereZonesOverlapIsOnEachOfThem()
    {
        var settings = Overlapping();

        // Where the groin meets the left thigh: the groin (the smaller box) is matched and plays, and the thigh is touched too.
        Assert.Equal(("groin", "box"), Of(CharacterTouchZones.Match(settings, Touch(0.53, 0.6))));
        Assert.Equal(["groin", "thigh_left"], On(settings, Touch(0.53, 0.6)));
        Assert.Equal(["groin", "thigh_left"], On(settings, Touch(0.53, 0.6, bone: "hips")));
        Assert.Equal(["thigh_left"], On(settings, Touch(0.52, 0.72)));
        // The nose and the left cheek overlap: both. The hair's box holds the point too, but it frames them, so it says less.
        Assert.Equal(["nose", "cheek_left"], On(settings, Touch(0.51, 0.22)));
        Assert.Equal(["eye_left"], On(settings, Touch(0.55, 0.17)));
        Assert.Equal(["hair"], On(settings, Touch(0.35, 0.05)));
        // A hand in front of the thigh touches the hand, on a Live2D drawable and a VRM bone alike.
        Assert.Equal(["hand_left"], On(settings, Touch(0.58, 0.66, ["HandL", "LegL"])));
        Assert.Equal(["hand_left"], On(settings, Touch(0.58, 0.66, bone: "leftHand")));
        // Zones that aren't in use aren't touched: intimate ones without Include intimate zones, ones turned off.
        Assert.Equal(["thigh_left"], On(settings with { IncludeIntimate = false }, Touch(0.53, 0.6)));
        Assert.Equal(["groin"], On(settings with { Zones = [.. settings.Zones.Select(z => z.Id == "thigh_left" ? z with { Enabled = false } : z)] },
            Touch(0.53, 0.6)));
        // Zones found with the character framed whole compare with that framing, as Match does.
        Assert.Equal(["groin", "thigh_left"], On(settings with { Whole = true }, Touch(0.9, 0.1) with { WholeX = 0.53, WholeY = 0.6 }));
        // A touch matched only by its rough zone is on that zone alone, and no match touches nothing.
        Assert.Equal(["foot_right"], On(settings, Touch(0.9, 0.95, ["ArtMesh_Foot"])));
        Assert.Empty(CharacterTouchZones.Touched(settings, Touch(0.5, 0.5), null));
    }

    [Fact]
    public async Task MartletHearsEveryZoneATouchLandsInAndTheMatchedOnePlays()
    {
        var directory = Directory.CreateTempSubdirectory("martlet-touch-").FullName;
        try
        {
            var settings = Overlapping();
            await CharacterTouchZones.SaveAsync(directory, settings with
            {
                Zones = [.. settings.Zones.Select(z => z.Id == "nose" ? z with { Reaction = z.Reaction with { Notices = false } } : z)]
            }, DateTimeOffset.Now);
            var service = new CharacterTouchZoneService(directory);
            service.Follow("model-1");
            var heard = new List<string[]>();
            var played = new List<string>();
            TouchReactionPlan Plan(CharacterTouchZone zone, int repeats)
            {
                played.Add(zone.Id);
                return new([]);
            }

            Assert.Equal("groin", service.React(Touch(0.53, 0.6), Plan, (_, _, _) => Task.CompletedTask, zones => heard.Add(Ids(zones)))?.Zone.Id);
            Assert.Equal(["groin", "thigh_left"], Assert.Single(heard));
            Assert.Equal(["groin"], played);
            Assert.StartsWith("Groin (box), with Left thigh, at ", service.LastMatch);
            Assert.EndsWith(", and Martlet noticed them.", service.LastMatch);

            // Martlet hears only the zones it notices: here not the nose, which still plays its reaction.
            service.React(Touch(0.51, 0.22), Plan, (_, _, _) => Task.CompletedTask, zones => heard.Add(Ids(zones)));
            Assert.Equal(["cheek_left"], heard[^1]);
            Assert.Equal(["groin", "nose"], played);
            Assert.EndsWith(", and Martlet noticed Left cheek.", service.LastMatch);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task TheMcpCheckSaysEveryZoneATouchLandsInAndWhatMartletHears()
    {
        var directory = Directory.CreateTempSubdirectory("martlet-touch-").FullName;
        try
        {
            const string answer = "{\"zones\":[{\"id\":\"groin\",\"box\":[0.44,0.55,0.56,0.63]},{\"id\":\"thigh_left\",\"box\":[0.5,0.58,0.62,0.78]}]}";
            var result = System.Text.Json.JsonSerializer.SerializeToElement(await Martlet.Mcp.TouchZonesCheck.RunAsync(directory, true, null, "model-1",
                answer, 400, 800, null, null, "{\"x\":0.53,\"y\":0.6,\"hitAreas\":[],\"drawables\":[]}", false, null, null, CancellationToken.None));
            var match = result.GetProperty("match");
            Assert.Equal("groin", match.GetProperty("zone").GetString());
            Assert.Equal(["groin", "thigh_left"], match.GetProperty("touched").EnumerateArray().Select(e => e.GetString()));
            Assert.Equal(["groin", "thigh_left"], match.GetProperty("noticing").EnumerateArray().Select(e => e.GetString()));
            Assert.Equal("They poked your groin and your left thigh once.", match.GetProperty("noticed").GetString());
        }
        finally { Directory.Delete(directory, true); }
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
        Assert.Null(CharacterTouchZones.Narration(chosen with { Reaction = new() { Narration = "*boops you*", Notices = false } }));
        Assert.Empty(CharacterTouchZones.Plan(chosen with { Reaction = new() { Actions = [] } }, catalog));
        Assert.Null(CharacterTouchZones.Narration(chosen with { Reaction = new() }));
    }

    [Fact]
    public async Task MartletNoticesEveryZoneByDefaultAndOlderZonesNobodyTurnedItOnForGetItOnce()
    {
        // A new zone, a found zone and the rough zone used before any were found: all noticed.
        Assert.True(new CharacterTouchReaction().Notices);
        Assert.True(CharacterTouchZones.Detected(null, "m", [new() { Id = "chest", Box = new(0.3, 0.3, 0.3, 0.1) }], null, null, DateTimeOffset.Now)
            .Zones[0].Reaction.Notices);
        var rough = CharacterTouchZones.Match(null, new(0.5, 0.5, [], [], null, null, false, null, null))!;
        Assert.Equal(("stomach", "coarse"), (rough.Zone.Id, rough.How));
        Assert.True(rough.Zone.Reaction.Notices);

        var directory = Directory.CreateTempSubdirectory("martlet-touch-").FullName;
        try
        {
            // Saved when it was off by default: a model nobody turned it on for gets it on everywhere; one where the owner turned
            // it on for some zones keeps its choices.
            File.WriteAllText(CharacterTouchZones.Path(directory), """
                { "version": 1, "models": [
                  { "model_id": "untouched", "updated_at": "2026-01-01T00:00:00+00:00", "zones": [
                    { "id": "chest", "box": { "x": 0.3, "y": 0.3, "width": 0.3, "height": 0.1 }, "reaction": { "notices": false } },
                    { "id": "groin", "box": { "x": 0.4, "y": 0.5, "width": 0.2, "height": 0.1 }, "reaction": { "notices": false } } ] },
                  { "model_id": "chosen", "updated_at": "2026-01-01T00:00:00+00:00", "zones": [
                    { "id": "hair", "box": { "x": 0.4, "y": 0, "width": 0.2, "height": 0.1 }, "reaction": { "notices": true } },
                    { "id": "groin", "box": { "x": 0.4, "y": 0.5, "width": 0.2, "height": 0.1 }, "reaction": { "notices": false } } ] } ] }
                """);
            Assert.All(CharacterTouchZones.Load(directory, "untouched")!.Zones, z => Assert.True(z.Reaction.Notices));
            Assert.Equal([true, false], CharacterTouchZones.Load(directory, "chosen")!.Zones.Select(z => z.Reaction.Notices));

            // Saving writes the new version: turning a zone off now stays off.
            var untouched = CharacterTouchZones.Load(directory, "untouched")!;
            await CharacterTouchZones.SaveAsync(directory, untouched with
            {
                Zones = [untouched.Zones[0], untouched.Zones[1] with { Reaction = untouched.Zones[1].Reaction with { Notices = false } }]
            }, DateTimeOffset.Now);
            Assert.Contains("\"version\": 2", File.ReadAllText(CharacterTouchZones.Path(directory)), StringComparison.Ordinal);
            Assert.Equal([true, false], CharacterTouchZones.Load(directory, "untouched")!.Zones.Select(z => z.Reaction.Notices));
            Assert.Equal([true, false], CharacterTouchZones.Load(directory, "chosen")!.Zones.Select(z => z.Reaction.Notices));
        }
        finally { Directory.Delete(directory, true); }
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
