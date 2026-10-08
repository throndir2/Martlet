using Martlet.Avatar.Hosting;

namespace Martlet.Desktop.Tests;

/// <summary>Touch zones with several areas, and zones that follow the model's own swinging parts, on a stand-in for Jane Doe: the
/// user's Live2D model whose tail (尾巴, 21 drawables) hangs straight down behind her legs at rest and swings up beside her in her
/// idle motion, and whose ponytail (马尾) hangs to her hips. The tail's drawables and chains are the ones her own model reports.</summary>
public sealed class TouchZoneAreasTests
{
    // Her parts as the renderer reports them (简.cdi3.json's names, the parents from her model).
    private static readonly (string Id, string Name, string? Parent)[] JaneParts =
    [
        ("Part", "立绘", null), ("Part4", "前发", "Part"), ("Part8", "头", "Part"), ("Part20", "脸部", "Part8"), ("Part17", "鼻子", "Part8"),
        ("Part18", "张嘴", "Part8"), ("Part15", "左眼", "Part8"), ("Part25", "上半身", "Part"), ("Part27", "脖子", "Part25"), ("Part28", "上衣", "Part25"),
        ("Part30", "腿部", "Part"), ("Part31", "右腿", "Part30"), ("Part32", "左腿", "Part30"), ("Part36", "马尾", "Part"), ("Part37", "尾巴", null),
        ("ArtMesh209_Skinning", "尾巴(蒙皮)", "Part37")
    ];

    // Her body in model units (x to the right, y up): hair, head, face, nose, mouth, eyes, neck, top, shorts and both legs, and
    // the ponytail; then the tail's 21 drawables at rest, root first, as her model lists them.
    private static readonly (string Id, string Part, double Left, double Top, double Right, double Bottom)[] Body =
    [
        ("Hair0", "Part4", -0.134, 0.917, 0.131, 0.72), ("Head0", "Part8", -0.099, 0.757, 0.096, 0.695), ("Face0", "Part20", -0.097, 0.894, 0.093, 0.654),
        ("Nose0", "Part17", -0.008, 0.723, 0.004, 0.701), ("Mouth0", "Part18", -0.02, 0.703, 0.017, 0.669),
        ("EyeL", "Part15", -0.08, 0.759, -0.021, 0.732), ("EyeR", "Part15", 0.018, 0.759, 0.076, 0.732), ("Neck0", "Part27", -0.083, 0.75, 0.083, 0.592),
        ("Top0", "Part28", -0.162, 0.589, 0.09, 0.345), ("Shorts0", "Part30", -0.198, 0.331, 0.124, 0.109),
        ("LegR0", "Part31", -0.038, 0.241, 0.169, -0.378), ("LegR1", "Part31", -0.024, -0.429, 0.132, -0.962),
        ("LegL0", "Part32", -0.224, 0.24, -0.027, -0.364), ("LegL1", "Part32", -0.169, -0.419, -0.035, -0.951),
        ("Pony0", "Part36", -0.104, 0.546, 0.093, 0.237), ("Pony1", "Part36", -0.342, 0.797, 0.048, -0.138), ("Pony2", "Part36", -0.403, 0.261, -0.153, 0.162)
    ];

    private static readonly (string Id, double Left, double Top, double Right, double Bottom)[] Tail =
    [
        ("ArtMesh210", -0.078, 0.271, 0, 0.236), ("ArtMesh211", -0.078, 0.26, 0, 0.177), ("ArtMesh212", -0.077, 0.225, 0, 0.084),
        ("ArtMesh213", -0.077, 0.154, -0.001, -0.01), ("ArtMesh214", -0.076, 0.072, -0.003, -0.116), ("ArtMesh215", -0.076, -0.033, -0.005, -0.198),
        ("ArtMesh216", -0.075, -0.127, -0.007, -0.268), ("ArtMesh217", -0.074, -0.221, -0.009, -0.327), ("ArtMesh218", -0.074, -0.291, -0.01, -0.374),
        ("ArtMesh220", -0.073, -0.35, -0.011, -0.432), ("ArtMesh221", -0.073, -0.385, -0.012, -0.538), ("ArtMesh222", -0.072, -0.455, -0.013, -0.632),
        ("ArtMesh223", -0.071, -0.549, -0.015, -0.702), ("ArtMesh224", -0.071, -0.643, -0.016, -0.772), ("ArtMesh225", -0.07, -0.714, -0.017, -0.819),
        ("ArtMesh226", -0.07, -0.783, -0.019, -0.878), ("ArtMesh227", -0.069, -0.843, -0.02, -0.96), ("ArtMesh228", -0.068, -0.889, -0.021, -1.054),
        ("ArtMesh229", -0.068, -0.971, -0.022, -1.129), ("ArtMesh230", -0.067, -1.065, -0.024, -1.129), ("ArtMesh162", -0.09, -1.076, -0.001, -1.238)
    ];

    // Model units to fractions of the snapshot (the whole page, here): x -0.45..0.30, y 0.95..-1.30.
    private static double X(double x) => (x + 0.45) / 0.75;
    private static double Y(double y) => (0.95 - y) / 2.25;
    private static TouchZoneBox Box(double left, double top, double right, double bottom) => new(X(left), Y(top), X(right) - X(left), Y(bottom) - Y(top));
    private static readonly TouchZoneBox Whole = new(0, 0, 1, 1);

    private static string[] TailIds => [.. Tail.Select(t => t.Id)];

    // Her probe: her drawables with their parts, her own part names (or none) and the chains her physics swings: the tail (its two
    // settings, named 尾巴), a hip sway that swings only the tail's lower part, and the ponytail.
    private static RendererZoneProbe Probe(bool named = true, bool chainNames = true) => new(
        [.. Body.Select(d => new RendererDrawableBox(d.Id, X(d.Left), Y(d.Top), X(d.Right), Y(d.Bottom), named ? d.Part : null)),
         .. Tail.Select(t => new RendererDrawableBox(t.Id, X(t.Left), Y(t.Top), X(t.Right), Y(t.Bottom), named ? "ArtMesh209_Skinning" : null))],
        Parts: named ? [.. JaneParts.Select(p => new RendererModelPart(p.Id, p.Name, p.Parent))] : null,
        Chains:
        [
            new(chainNames ? "X Hip" : null, TailIds[4..], X(-0.3), Y(0.072), X(0.15), Y(-1.238)),
            new(chainNames ? "尾巴 / 尾巴(2)" : null, TailIds, X(-0.4), Y(0.6), X(0.3), Y(-1.238)),
            new(chainNames ? "马尾 SY / 马尾 S" : null, ["Pony0", "Pony1", "Pony2"], X(-0.45), Y(0.797), X(0.2), Y(-0.138))
        ]);

    private static CharacterTouchZone Zone(string id, TouchZoneBox box) => new() { Id = id, Box = box };

    private static CharacterTouch Touch(double x, double y, params string[] drawables) => new(x, y, [], drawables, null, null, false, null, null);

    [Fact]
    public void ATailHiddenBehindTheLegsFollowsAllOfItsDrawablesInAreasFromRootToTip()
    {
        // Where her named parts put the tail (the first guess and Detect zones both start from here).
        var place = TouchZoneDetection.NamedPlace("tail", TouchZoneDetection.Hints(Probe(), Whole))!.Value.Box;

        var tail = Assert.Single(CharacterTouchZones.Bind([Zone("tail", place)], Whole, Probe()));

        Assert.Equal("尾巴 / 尾巴(2)", tail.Follows);
        var areas = tail.Areas!;
        Assert.InRange(areas.Count, 5, CharacterTouchZones.MaximumAreas);
        Assert.All(areas, a => Assert.True(a.FromModel));
        // Every drawable of the tail, once, in order from the root (at her hips) to the tip (below her boots).
        Assert.Equal(TailIds, areas.SelectMany(a => a.Drawables));
        Assert.Contains("ArtMesh210", areas[0].Drawables);
        Assert.Contains("ArtMesh162", areas[^1].Drawables);
        Assert.True(areas[0].Box.CenterY < areas[^1].Box.CenterY);
        Assert.Equal(TailIds.Order(StringComparer.Ordinal), tail.Drawables.Order(StringComparer.Ordinal));
        // The zone's box holds all its areas.
        Assert.All(areas, a => Assert.True(tail.Box.Covers(a.Box) > 0.99, $"{a.Box} in {tail.Box}"));
    }

    [Fact]
    public void ABoxOnTheTipThatShowsBelowHerBootsFindsTheWholeTailFromItsPhysics()
    {
        // The owner (or a vision model that only saw the tip) boxed what shows below her boots; her parts name nothing and
        // neither do her physics settings.
        var tip = Box(-0.095, -0.97, 0.005, -1.245);

        var tail = Assert.Single(CharacterTouchZones.Bind([Zone("tail", tip)], Whole, Probe(named: false, chainNames: false)));

        Assert.NotNull(tail.Follows);
        // The hip sway swings only the tail's lower part; the tail's own sway, which holds it and adds its root, is the same part.
        Assert.Equal(TailIds, tail.AllAreas.SelectMany(a => a.Drawables));
    }

    [Fact]
    public void TouchingHerTailWhereverItSwungIsTheTailAndTheBoxesItLeftAreNot()
    {
        var probe = Probe();
        var tailPlace = TouchZoneDetection.NamedPlace("tail", TouchZoneDetection.Hints(probe, Whole))!.Value.Box;
        var settings = new CharacterTouchZoneSettings
        {
            ModelId = "jane", Crop = Whole, Whole = true,
            Zones = CharacterTouchZones.Bind([Zone("tail", tailPlace), Zone("thigh_right", Box(-0.224, 0.24, -0.027, -0.364)),
                Zone("hips", Box(-0.198, 0.331, 0.124, 0.109))], Whole, probe)
        };
        var tail = settings.Zones.Single(z => z.Id == "tail");

        // In her idle motion the tail's middle (ArtMesh224) swings up beside her waist, far from where it hung at rest.
        var swung = CharacterTouchZones.Match(settings, Touch(X(-0.22), Y(0.48), "ArtMesh224"))!;
        Assert.Equal(("tail", "drawable"), (swung.Zone.Id, swung.How));
        Assert.Equal(tail.AllAreas.ToList().FindIndex(a => a.Drawables.Contains("ArtMesh224")), swung.Area);
        // The renderer traces the touched point to the rest pose: for the tail, where it hung behind her right thigh. The touch
        // is on the tail where it swung to, not on the thigh too.
        var traced = Touch(X(-0.22), Y(0.48), "ArtMesh216") with { WholeX = X(-0.22), WholeY = Y(0.48), RestWholeX = X(-0.041), RestWholeY = Y(-0.198) };
        Assert.True(settings.Zones.Single(z => z.Id == "thigh_right").Holds(Whole, X(-0.041), Y(-0.198)));
        var onTail = CharacterTouchZones.Match(settings, traced)!;
        Assert.Equal(("tail", false), (onTail.Zone.Id, onTail.Traced));
        Assert.Equal(["tail"], CharacterTouchZones.Touched(settings, traced, onTail).Select(z => z.Id));
        Assert.Equal((X(-0.22), Y(0.48), false), CharacterTouchZones.TouchPoint(settings, traced, onTail));
        // Where it hung, behind her right leg, a touch on the leg is the leg's, not the tail's old box.
        Assert.Equal("thigh_right", CharacterTouchZones.Match(settings, Touch(X(-0.04), Y(-0.1), "LegL0"))!.Zone.Id);
        Assert.False(tail.Holds(Whole, X(-0.04), Y(-0.1)));
        // The tail's root, behind her shorts at rest, is the tail's only: the hips' box lets it go.
        Assert.DoesNotContain("ArtMesh210", settings.Zones.Single(z => z.Id == "hips").Drawables);
        Assert.Equal("tail", CharacterTouchZones.Match(settings, Touch(X(-0.04), Y(0.25), "ArtMesh210"))!.Zone.Id);
    }

    [Fact]
    public void HerHairFollowsTheSwingingPonytailItsBoxDoesntHold()
    {
        // The hair's box stops just below her chin (the first guess); her ponytail hangs to her hips.
        var hair = Assert.Single(CharacterTouchZones.Bind([Zone("hair", Box(-0.16, 0.93, 0.16, 0.62))], Whole, Probe()));

        Assert.Equal("马尾 SY / 马尾 S", hair.Follows);
        var areas = hair.Areas!;
        Assert.False(areas[0].FromModel);
        Assert.Contains("Hair0", areas[0].Drawables);
        Assert.Equal(["Pony0", "Pony1", "Pony2"], areas.Where(a => a.FromModel).SelectMany(a => a.Drawables));
        var settings = new CharacterTouchZoneSettings { ModelId = "jane", Crop = Whole, Zones = [hair] };
        Assert.Equal("hair", CharacterTouchZones.Match(settings, Touch(X(-0.3), Y(0.2), "Pony2"))!.Zone.Id);
        // A tail zone never follows the ponytail, which her names call hair: a box on it stays only its box.
        var notTail = Assert.Single(CharacterTouchZones.Bind([Zone("tail", Box(-0.403, 0.261, -0.153, 0.162))], Whole, Probe()));
        Assert.Null(notTail.Follows);
        Assert.Null(notTail.Areas);
    }

    [Fact]
    public void EachAreaTheOwnerDrewBindsWhatItsOwnBoxHolds()
    {
        var chest = new CharacterTouchZone
        {
            Id = "chest", Box = Box(-0.2, 0.6, 0.2, 0.0),
            Areas = [new() { Box = Box(-0.17, 0.6, 0.1, 0.34) }, new() { Box = Box(-0.2, 0.34, 0.13, 0.1) }]
        };

        var bound = Assert.Single(CharacterTouchZones.Bind([chest], Whole, Probe()));

        Assert.Equal(2, bound.Areas!.Count);
        Assert.Contains("Top0", bound.Areas[0].Drawables);
        Assert.Contains("Shorts0", bound.Areas[1].Drawables);
        Assert.DoesNotContain("Shorts0", bound.Areas[0].Drawables);
        Assert.Null(bound.Follows);
        var settings = new CharacterTouchZoneSettings { ModelId = "jane", Crop = Whole, Zones = [bound] };
        var boxed = CharacterTouchZones.Match(settings, Touch(X(0.11), Y(0.2)))!;
        Assert.Equal(("chest", "box", 1), (boxed.Zone.Id, boxed.How, boxed.Area));
        Assert.True(bound.Holds(Whole, X(0.11), Y(0.2)));
        Assert.False(bound.Holds(Whole, X(0.25), Y(0.8)));
        // A zone that is only its box is its one area, as before.
        var face = Zone("face", Box(-0.097, 0.894, 0.093, 0.654));
        Assert.Equal(face.Box, Assert.Single(face.AllAreas).Box);
        Assert.Null(CharacterTouchZones.Compose(face, [new() { Box = face.Box }], null).Areas);
    }

    [Fact]
    public async Task AreasAndWhatAZoneFollowsAreSavedAndOlderZonesLoadAsOneArea()
    {
        var directory = Path.Combine(Path.GetTempPath(), "martlet-areas-" + Guid.NewGuid().ToString("N"));
        try
        {
            var probe = Probe();
            var place = TouchZoneDetection.NamedPlace("tail", TouchZoneDetection.Hints(probe, Whole))!.Value.Box;
            var settings = new CharacterTouchZoneSettings
            {
                ModelId = "jane", Crop = Whole, Whole = true,
                Zones = CharacterTouchZones.Bind([Zone("tail", place), Zone("nose", Box(-0.008, 0.723, 0.004, 0.701))], Whole, probe)
            };
            await CharacterTouchZones.SaveAsync(directory, settings, DateTimeOffset.Now);
            var json = await File.ReadAllTextAsync(CharacterTouchZones.Path(directory));
            Assert.Contains("\"areas\"", json, StringComparison.Ordinal);
            Assert.Contains("\"from_model\": true", json, StringComparison.Ordinal);
            Assert.Contains("\"follows\":", json, StringComparison.Ordinal);

            var loaded = CharacterTouchZones.Load(directory, "jane")!;
            var tail = loaded.Zones.Single(z => z.Id == "tail");
            Assert.Equal("尾巴 / 尾巴(2)", tail.Follows);
            Assert.Equal(settings.Zones.Single(z => z.Id == "tail").Areas!.Count, tail.Areas!.Count);
            Assert.Equal(TailIds, tail.AllAreas.SelectMany(a => a.Drawables));
            var nose = loaded.Zones.Single(z => z.Id == "nose");
            Assert.Null(nose.Areas);
            Assert.Single(nose.AllAreas);
            Assert.Null(CharacterTouchZones.Problem(loaded));
            Assert.NotNull(CharacterTouchZones.Problem(loaded with
            {
                Zones = [nose with { Areas = [.. Enumerable.Repeat(new CharacterTouchZoneArea { Box = nose.Box }, CharacterTouchZones.MaximumAreas + 1)] }]
            }));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void HerOwnPartsNameATailAndRebindingFollowsItAfterTheOwnerMovesIt()
    {
        var probe = Probe();
        var hints = TouchZoneDetection.Hints(probe, Whole);

        // Her part names place a tail, so the first guess and Detect zones add it as special to her; unnamed parts place none.
        Assert.Equal(["tail"], TouchZoneDetection.Named(hints));
        Assert.Empty(TouchZoneDetection.Named(TouchZoneDetection.Hints(Probe(named: false), Whole)));

        // The owner added a tail in the middle of the picture, then dragged it onto the tip below her boots: saving binds it again.
        var moved = new CharacterTouchZoneSettings
        {
            ModelId = "jane", Crop = Whole, Whole = true, Zones = [new() { Id = "tail", Box = Box(-0.095, -0.97, 0.005, -1.245), Added = true }]
        };
        var rebound = CharacterTouchZones.Rebind(moved, new(Whole, probe));
        Assert.Equal(TailIds, rebound.Zones.Single().AllAreas.SelectMany(a => a.Drawables));
        Assert.True(rebound.Zones.Single().Added);
        // A probe of another picture binds nothing.
        Assert.Same(moved, CharacterTouchZones.Rebind(moved, new(new(0.1, 0, 0.8, 1), probe)));
        Assert.Same(moved, CharacterTouchZones.Rebind(moved, null));
    }

    [Fact]
    public void TheFirstGuessPlacesHerTailAndItFollowsAllOfIt()
    {
        var probe = Probe();
        var hints = TouchZoneDetection.Hints(probe, Whole);
        var picture = Picture();

        var guess = TouchZoneDetection.Estimate(picture, hints, TouchZoneDetection.For(null));
        var settings = CharacterTouchZones.Estimated(null, "jane", guess.Zones!, Whole, probe, DateTimeOffset.Now);

        var tail = settings.Zones.Single(z => z.Id == "tail");
        Assert.Equal(TailIds, tail.AllAreas.SelectMany(a => a.Drawables));
        Assert.Equal("尾巴 / 尾巴(2)", tail.Follows);
    }

    // ---------- a VRM character's spring bones ----------

    // A VRM character standing whole (fractions of the page): its humanoid bones, its face, and its spring-bone chains as the
    // renderer reports them, each named by its root joint: a tail hanging behind its legs from the hips, cat ears on its head and
    // hair hanging to its neck. Unnamed, the joints' names say nothing.
    private static RendererZoneProbe VrmProbe(bool named = true)
    {
        string Joint(string name, int i) => named ? $"J_Sec_{name}_{i:00}" : $"Bone.{name.Length * 10 + i:000}";
        RendererSpring Spring(string name, params (double X, double Y)[] at) =>
            new(Joint(name, 1), [.. at.Select((p, i) => new RendererBonePoint(Joint(name, i + 1), p.X, p.Y))]);
        return new(Bones:
            [
                new("hips", 0.5, 0.5), new("spine", 0.5, 0.42), new("chest", 0.5, 0.34), new("neck", 0.5, 0.25), new("head", 0.5, 0.2),
                new("leftUpperLeg", 0.53, 0.52), new("leftLowerLeg", 0.53, 0.7), new("leftFoot", 0.53, 0.88), new("rightUpperLeg", 0.47, 0.52),
                new("rightLowerLeg", 0.47, 0.7), new("rightFoot", 0.47, 0.88), new("leftUpperArm", 0.56, 0.3), new("rightUpperArm", 0.44, 0.3)
            ],
            Face: new(0.5, 0.15, 0.08),
            Springs:
            [
                Spring("Tail", (0.5, 0.52), (0.5, 0.6), (0.5, 0.68), (0.51, 0.76), (0.52, 0.84)),
                Spring("L_CatEar", (0.53, 0.09), (0.54, 0.06), (0.545, 0.04)),
                Spring("Hair1", (0.5, 0.12), (0.52, 0.2), (0.53, 0.28))
            ]);
    }

    private static CharacterTouch VrmTouch(double x, double y, string bone, string node, bool hair = false, (double X, double Y)? rest = null) =>
        new(x, y, [], [], bone, node, hair, "Body", null, WholeX: x, WholeY: y, RestX: rest?.X, RestY: rest?.Y, RestWholeX: rest?.X, RestWholeY: rest?.Y);

    [Fact]
    public void AVrmTailFollowsItsSpringBonesAndATouchOnAJointIsTheTailWhereverItSwung()
    {
        var zones = CharacterTouchZones.Bind([Zone("tail", new(0.49, 0.8, 0.05, 0.06)), Zone("hips", new(0.44, 0.45, 0.12, 0.1)),
            Zone("animal_ears", new(0.51, 0.02, 0.06, 0.06)), Zone("hair", new(0.44, 0.04, 0.12, 0.14))], Whole, VrmProbe());
        var settings = new CharacterTouchZoneSettings { ModelId = "vrm", Crop = Whole, Whole = true, Zones = zones };

        // A box on the tip that shows between the boots follows the whole tail, root first.
        var tail = zones.Single(z => z.Id == "tail");
        Assert.Equal("J_Sec_Tail_01", tail.Follows);
        Assert.All(tail.AllAreas, a => Assert.True(a.FromModel));
        Assert.Equal(["J_Sec_Tail_01", "J_Sec_Tail_02", "J_Sec_Tail_03", "J_Sec_Tail_04", "J_Sec_Tail_05"], tail.AllAreas.SelectMany(a => a.Nodes));
        Assert.InRange(tail.AllAreas.Count, 2, 5);

        // The tail's root swung up beside the hips; the renderer traced the hit to where it hung at rest, inside the hips' box.
        var swung = VrmTouch(0.62, 0.45, "hips", "J_Sec_Tail_01", rest: (0.5, 0.53));
        var match = CharacterTouchZones.Match(settings, swung)!;
        Assert.Equal(("tail", "node", false, 0), (match.Zone.Id, match.How, match.Traced, match.Area));
        Assert.Equal(["tail"], CharacterTouchZones.Touched(settings, swung, match).Select(z => z.Id));

        // Cat ears hang from the head as hair does; a touch on their joint is the ears, not the hair.
        Assert.Equal("J_Sec_L_CatEar_01", zones.Single(z => z.Id == "animal_ears").Follows);
        Assert.Equal("animal_ears", CharacterTouchZones.Match(settings, VrmTouch(0.54, 0.06, "head", "J_Sec_L_CatEar_02", hair: true))!.Zone.Id);

        // The hair keeps its box and follows the hair hanging past it, to the neck.
        var hair = zones.Single(z => z.Id == "hair");
        Assert.Equal("J_Sec_Hair1_01", hair.Follows);
        Assert.False(hair.AllAreas[0].FromModel);
        Assert.Equal(["J_Sec_Hair1_01", "J_Sec_Hair1_02", "J_Sec_Hair1_03"], hair.AllAreas.Where(a => a.FromModel).SelectMany(a => a.Nodes));

        // The zones drawn over the character name the joint where each of the tail's areas ends, so the page draws its bones.
        var view = RendererZoneView.Of(settings, true, ["#E94F64"]).Areas.Where(a => a.Zone == "tail").ToArray();
        Assert.Equal(tail.AllAreas[1].Nodes[0], view[0].Nodes[^1]);
        Assert.Equal(tail.AllAreas[^1].Nodes, view[^1].Nodes);
    }

    [Fact]
    public void AnUnnamedVrmChainATailBoxHoldsIsTheTailButNeverOneRootedInTheHead()
    {
        var probe = VrmProbe(named: false);

        var tip = Assert.Single(CharacterTouchZones.Bind([Zone("tail", new(0.49, 0.8, 0.05, 0.06))], Whole, probe));
        Assert.Equal(["Bone.041", "Bone.042", "Bone.043", "Bone.044", "Bone.045"], tip.AllAreas.SelectMany(a => a.Nodes));

        // A tail box over the hair behind the head: a chain rooted above the chin is never a tail.
        var head = Assert.Single(CharacterTouchZones.Bind([Zone("tail", new(0.48, 0.1, 0.07, 0.2))], Whole, probe));
        Assert.Null(head.Follows);
        Assert.All(head.AllAreas, a => Assert.Empty(a.Nodes));
    }

    // Her picture: each drawable opaque, transparent around her.
    private static ZonePixels Picture()
    {
        const int width = 300, height = 900;
        var bgra = new byte[width * height * 4];
        foreach (var (left, top, right, bottom) in Body.Select(d => (d.Left, d.Top, d.Right, d.Bottom)).Concat(Tail.Select(t => (t.Left, t.Top, t.Right, t.Bottom))))
            for (var y = (int)(Y(top) * height); y < Math.Min(height, (int)Math.Ceiling(Y(bottom) * height)); y++)
                for (var x = (int)(X(left) * width); x < Math.Min(width, (int)Math.Ceiling(X(right) * width)); x++)
                {
                    var i = (y * width + x) * 4;
                    (bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]) = (40, 40, 160, 255);
                }
        return new(width, height, bgra);
    }
}
