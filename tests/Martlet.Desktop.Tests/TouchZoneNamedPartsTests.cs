using Martlet.Avatar.Hosting;

namespace Martlet.Desktop.Tests;

/// <summary>Detect zones with a Live2D model's own part names (its DisplayInfo file), on a stand-in for Jane Doe: the user's model
/// whose drawable IDs name nothing (ArtMesh...) and whose parts are named in Chinese.</summary>
public sealed class TouchZoneNamedPartsTests
{
    // Jane Doe's own parts (简.cdi3.json's names, the parents from her model) as the renderer reports them.
    private static readonly (string Id, string Name, string? Parent)[] JaneParts =
    [
        ("Part", "立绘", null), ("Part4", "前发", "Part"), ("Part5", "侧发", "Part4"), ("Part6", "刘海", "Part4"), ("Part7", "头发阴影", "Part"),
        ("Part8", "头", "Part"), ("Part9", "表情", "Part8"), ("Part3", "眉毛2", "Part8"), ("Part15", "左眼", "Part8"), ("Part16", "右眼", "Part8"),
        ("Part17", "鼻子", "Part8"), ("Part18", "张嘴", "Part8"), ("Part20", "脸部", "Part8"), ("Part21", "耳朵", "Part"), ("Part22", "动作1", "Part"),
        ("Part23", "左手", "Part22"), ("Part24", "右手", "Part22"), ("Part25", "上半身", "Part"), ("Part26", "领带", "Part25"), ("Part27", "脖子", "Part25"),
        ("Part28", "上衣", "Part25"), ("Part29", "后发", "Part"), ("Part30", "腿部", "Part"), ("Part31", "右腿", "Part30"), ("Part32", "左腿", "Part30"),
        ("Part34", "左臂", "Part"), ("Part35", "右臂", "Part"), ("Part36", "马尾", "Part"), ("Part37", "尾巴", null),
        ("ArtMesh209_Skinning", "尾巴(蒙皮)", "Part37"), ("Part41", "尾巴参考(旋转)", "Part37")
    ];

    // Her visible drawables in model units (x to the right, y up), by part: the hair, head and face, both ears (one part), the
    // tie, neck and top of her upper body, her midriff and shorts directly in 腿部 (legs) and each leg down to its boot, the
    // raised arm (左臂, on the picture's left) and the hanging one (右臂), the ponytail (马尾) hanging to her hips, and the
    // tail behind her legs, down past her boots.
    private static readonly (string Part, double Left, double Top, double Right, double Bottom)[] JaneDrawables =
    [
        ("Part4", -0.134, 0.917, 0.131, 0.72), ("Part5", -0.12, 0.89, 0.12, 0.69), ("Part6", -0.09, 0.87, 0.09, 0.73), ("Part7", -0.105, 0.857, 0.102, 0.697),
        ("Part8", -0.099, 0.757, 0.096, 0.695), ("Part3", -0.071, 0.777, -0.019, 0.772), ("Part3", 0.016, 0.777, 0.067, 0.772),
        ("Part15", -0.08, 0.759, -0.021, 0.732), ("Part15", 0.018, 0.759, 0.076, 0.732), ("Part17", -0.008, 0.723, 0.004, 0.701),
        ("Part18", -0.02, 0.703, 0.017, 0.669), ("Part20", -0.097, 0.894, 0.093, 0.654),
        ("Part21", -0.133, 0.805, -0.037, 0.702), ("Part21", 0.033, 0.805, 0.13, 0.702),
        ("Part26", -0.122, 0.608, -0.012, 0.38), ("Part27", -0.083, 0.75, 0.083, 0.592),
        ("Part28", -0.213, 0.607, -0.056, 0.436), ("Part28", -0.056, 0.622, 0.133, 0.38), ("Part28", -0.162, 0.589, 0.09, 0.345),
        ("Part28", -0.065, 0.661, 0.06, 0.616), ("Part29", -0.154, 0.909, 0.151, 0.641),
        ("Part30", -0.149, 0.447, 0.079, 0.253), ("Part30", -0.198, 0.331, 0.124, 0.109),
        ("Part31", -0.038, 0.241, 0.169, -0.378), ("Part31", -0.001, -0.28, 0.117, -0.571), ("Part31", -0.024, -0.429, 0.132, -0.962),
        ("Part32", -0.224, 0.24, -0.027, -0.364), ("Part32", -0.149, -0.261, -0.033, -0.574), ("Part32", -0.169, -0.419, -0.035, -0.951),
        ("Part34", -0.319, 0.599, -0.183, 0.324), ("Part34", -0.25, 0.618, -0.068, 0.343), ("Part34", -0.405, 0.625, -0.313, 0.539),
        ("Part35", 0.068, 0.618, 0.263, 0.085), ("Part35", 0.162, 0.123, 0.26, -0.028),
        ("Part36", -0.342, 0.797, 0.048, -0.138), ("Part36", -0.403, 0.261, -0.153, 0.162), ("Part36", -0.104, 0.546, 0.093, 0.237),
        ("ArtMesh209_Skinning", -0.078, 0.271, 0, -0.198), ("ArtMesh209_Skinning", -0.075, -0.127, -0.007, -0.632),
        ("ArtMesh209_Skinning", -0.071, -0.549, -0.016, -1.054), ("ArtMesh209_Skinning", -0.09, -0.971, -0.001, -1.238)
    ];

    // Model units to fractions of the snapshot (the whole page, here): x -0.45..0.30, y 0.95..-1.30.
    private static double X(double x) => (x + 0.45) / 0.75;
    private static double Y(double y) => (0.95 - y) / 2.25;
    private static TouchZoneBox Box(double left, double top, double right, double bottom) => new(X(left), Y(top), X(right) - X(left), Y(bottom) - Y(top));

    private static RendererZoneProbe JaneProbe(bool parts = true) => new(
        [.. JaneDrawables.Select((d, i) => new RendererDrawableBox($"ArtMesh{i}", X(d.Left), Y(d.Top), X(d.Right), Y(d.Bottom), parts ? d.Part : null))],
        Parts: parts ? [.. JaneParts.Select(p => new RendererModelPart(p.Id, p.Name, p.Parent))] : null);

    // Her picture: each drawable opaque, transparent around her.
    private static ZonePixels JanePicture()
    {
        const int width = 300, height = 900;
        var bgra = new byte[width * height * 4];
        foreach (var d in JaneDrawables)
            for (var y = (int)(Y(d.Top) * height); y < Math.Min(height, (int)Math.Ceiling(Y(d.Bottom) * height)); y++)
                for (var x = (int)(X(d.Left) * width); x < Math.Min(width, (int)Math.Ceiling(X(d.Right) * width)); x++)
                {
                    var i = (y * width + x) * 4;
                    (bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]) = (40, 40, 160, 255);
                }
        return new(width, height, bgra);
    }

    private static CharacterTouchZone Zone(string id, double left, double top, double right, double bottom) => new() { Id = id, Box = Box(left, top, right, bottom) };

    // What gemma4:e4b answered on 2026-10-07: the neck on the midriff, the chest and both breasts on the shorts' waistband, the
    // waist on the shorts, the thighs, inner thighs and knees on the boots, the left foot on the tail tip, the left ear on the
    // face, the lips on the fur collar; the hips, groin and buttocks left out. The face, hair, right ear, upper arms and the
    // raised (right) hand were right.
    private static readonly CharacterTouchZone[] WrongAnswer =
    [
        Zone("hair", -0.15, 0.92, 0.15, 0.64), Zone("face", -0.09, 0.88, 0.09, 0.65), Zone("ear_right", -0.133, 0.805, -0.037, 0.705),
        Zone("ear_left", -0.04, 0.8, 0.05, 0.7), Zone("lips", -0.07, 0.64, 0.07, 0.58),
        Zone("neck", -0.1, 0.44, 0.06, 0.36), Zone("chest", -0.17, 0.34, 0.11, 0.28), Zone("breast_left", -0.03, 0.34, 0.11, 0.28),
        Zone("breast_right", -0.17, 0.34, -0.03, 0.28), Zone("waist", -0.19, 0.3, 0.12, 0.13),
        Zone("upper_arm_left", 0.07, 0.62, 0.2, 0.35), Zone("upper_arm_right", -0.25, 0.62, -0.07, 0.34), Zone("hand_right", -0.405, 0.6, -0.318, 0.54),
        Zone("thigh_left", 0, -0.55, 0.13, -0.85), Zone("thigh_right", -0.165, -0.55, -0.04, -0.85),
        Zone("inner_thigh_left", 0, -0.6, 0.06, -0.8), Zone("inner_thigh_right", -0.09, -0.6, -0.04, -0.8),
        Zone("knee_left", 0, -0.8, 0.13, -0.93), Zone("knee_right", -0.165, -0.8, -0.04, -0.93),
        Zone("foot_left", -0.09, -1.08, 0, -1.24), Zone("foot_right", -0.169, -0.85, -0.035, -0.951)
    ];

    // The whole character's parts as that model saw them: its lower body starting below the shorts, and the tail on the ponytail.
    private static string PartsAnswer() => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"{{\"parts\":[{Part("head", Box(-0.16, 0.93, 0.16, 0.62))},{Part("upper_body", Box(-0.41, 0.76, 0.27, 0.3))}," +
        $"{Part("lower_body", Box(-0.23, 0.1, 0.18, -0.97))},{Part("tail", Box(-0.38, 0.55, -0.22, -0.1))}]}}");

    private static string Part(string id, TouchZoneBox box) => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"{{\"id\":\"{id}\",\"left\":{box.X:0.####},\"top\":{box.Y:0.####},\"right\":{box.X + box.Width:0.####},\"bottom\":{box.Y + box.Height:0.####}}}");

    // A detection where the vision model gives those answers and its checks change nothing.
    private static async Task<(ZoneDetectionResult Result, List<ZoneAsk> Asks)> DetectWrongAnswer(ZoneHints? hints, int checks = 0)
    {
        var asks = new List<ZoneAsk>();
        var result = await TouchZoneDetection.RunAsync(JanePicture(), hints, (ask, _) =>
        {
            asks.Add(ask);
            return Task.FromResult<(string?, string?)>((ask.Kind switch
            {
                ZoneAskKind.Parts => PartsAnswer(),
                ZoneAskKind.Zones => TouchZoneDetection.Oracle(ask, WrongAnswer, WrongAnswer),
                _ => "{\"zones\":[" + string.Join(",", ask.Marks.Select(m => $"{{\"n\":{m.Number},\"ok\":true}}")) + "],\"done\":true}"
            }, null));
        }, null, CancellationToken.None, new ZoneDetectionOptions { Checks = checks, Required = TouchZoneDetection.Erogenous });
        return (result, asks);
    }

    private static bool Inside(TouchZoneBox box, TouchZoneBox place, double slack = 0.01) =>
        box.CenterX >= place.X - slack && box.CenterX <= place.X + place.Width + slack && box.CenterY >= place.Y - slack && box.CenterY <= place.Y + place.Height + slack;

    [Fact]
    public void PartNamesInAnyLanguageSayTheBodyPartAndTheLongestWordWins()
    {
        Assert.Equal(("hair", 0), TouchZoneDetection.PartName("马尾"));
        Assert.Equal(("tail", 0), TouchZoneDetection.PartName("尾巴(蒙皮)"));
        Assert.Equal(("hair", 0), TouchZoneDetection.PartName("头发阴影"));
        Assert.Equal(("head", 0), TouchZoneDetection.PartName("头"));
        Assert.Equal(("eyes", -1), TouchZoneDetection.PartName("左眼"));
        Assert.Equal(("legs", 1), TouchZoneDetection.PartName("右腿"));
        Assert.Equal(("torso", 0), TouchZoneDetection.PartName("上半身"));
        Assert.Equal(("lower_body", 0), TouchZoneDetection.PartName("下半身"));
        Assert.Equal(("torso", 0), TouchZoneDetection.PartName("上衣"));
        Assert.Equal(("neck", 0), TouchZoneDetection.PartName("脖子"));
        Assert.Equal(("arms", 0), TouchZoneDetection.PartName("手臂"));
        Assert.Equal(("hands", -1), TouchZoneDetection.PartName("左手"));
        Assert.Equal(("hair", 0), TouchZoneDetection.PartName("後ろ髪"));
        Assert.Equal(("legs", 0), TouchZoneDetection.PartName("太もも"));
        Assert.Equal(("feet", 0), TouchZoneDetection.PartName("足首"));
        Assert.Equal(("skirt", 0), TouchZoneDetection.PartName("スカート"));
        Assert.Equal(("arms", 1), TouchZoneDetection.PartName("Arm_R"));
        Assert.Equal(("lower_body", 0), TouchZoneDetection.PartName("LowerBody"));
        Assert.Equal(("hair", 0), TouchZoneDetection.PartName("HairFront"));
        Assert.Equal(("hips", 0), TouchZoneDetection.PartName("Shorts"));
        Assert.Equal(((string?)null, 0), TouchZoneDetection.PartName("Part8"));
        Assert.Equal(((string?)null, 0), TouchZoneDetection.PartName("动作1"));
    }

    [Fact]
    public void JaneDoesPartNamesPlaceHerBodyPartsAndPositionTellsLeftFromRight()
    {
        var hints = TouchZoneDetection.Hints(JaneProbe(), new(0, 0, 1, 1))!;

        Assert.True(hints.Named);
        Assert.Equal((JaneParts.Length, JaneParts.Length), (hints.ModelParts, hints.NamedModelParts));
        Assert.Equal(12, hints.NamedParts);
        foreach (var part in new[] { "head", "hair", "face", "eyes", "nose", "mouth", "ears", "neck", "torso", "arms", "legs", "tail" })
            Assert.Contains(hints.Areas, a => a.Part == part);
        // 左臂 ("left arm") is on the picture's left: facing the viewer that is her right arm, whatever the name says. Her left leg
        // (右腿) is on the picture's right too, and both eyes, in 左眼, are told apart by where they are.
        var leftArm = Assert.Single(hints.Areas, a => a.Part == "arms" && a.Side == "left");
        Assert.True(leftArm.Box.CenterX > 0.5, $"{leftArm.Box}");
        Assert.True(Assert.Single(hints.Areas, a => a.Part == "legs" && a.Side == "left").Box.CenterX > Assert.Single(hints.Areas, a => a.Part == "legs" && a.Side == "right").Box.CenterX);
        Assert.Equal(2, hints.Areas.Count(a => a.Part == "eyes"));
        Assert.Contains("the character's left arm (", TouchZoneDetection.PartsText(hints), StringComparison.Ordinal);
        Assert.Contains("The model's own parts, by the names in its files", TouchZoneDetection.PartsText(hints), StringComparison.Ordinal);

        // The tail is her tail, not the ponytail (马尾, "horse tail"); her left ear is the one on the picture's right.
        var tail = TouchZoneDetection.NamedPlace("tail", hints)!.Value.Box;
        Assert.True(Inside(tail, Box(-0.09, 0.271, 0, -1.238)), $"{tail}");
        Assert.True(tail.Width < Box(-0.09, 0, 0, 0).Width + 0.01);
        var ear = TouchZoneDetection.NamedPlace("ear_left", hints)!.Value;
        Assert.Equal("left ear", ear.What);
        Assert.True(Inside(ear.Box, Box(0.033, 0.805, 0.13, 0.702)), $"{ear.Box}");
        Assert.Equal("mouth", TouchZoneDetection.NamedPlace("lips", hints)!.Value.What);

        // The lower body's close-up holds her hips and groin: it starts above her shorts (y 0.331).
        var lower = TouchZoneDetection.NamedRegion("lower_body", hints)!;
        Assert.True(lower.Y < Y(0.331) && lower.Y + lower.Height >= Y(-0.951), $"{lower}");
        Assert.True(lower.Y < Y(0.345) - TouchZoneDetection.RegionOverlap + 0.001, "it reaches up past the bottom of her top");
        var head = TouchZoneDetection.NamedRegion("head", hints)!;
        Assert.True(head.Y <= Y(0.917) && head.Y + head.Height >= Y(0.654) + TouchZoneDetection.RegionOverlap - 0.001, $"{head}");
        // The ponytail hangs below her face: the head's close-up doesn't follow it down.
        Assert.True(head.Y + head.Height < Y(0.5), $"{head}");
    }

    [Fact]
    public void WithoutPartNamesNothingChanges()
    {
        var unnamed = TouchZoneDetection.Hints(JaneProbe(parts: false), new(0, 0, 1, 1))!;
        Assert.False(unnamed.Named);
        Assert.Empty(unnamed.Areas);
        Assert.Null(TouchZoneDetection.NamedRegion("lower_body", unnamed));
        Assert.Null(TouchZoneDetection.NamedPlace("neck", unnamed));
        // Parts whose names and IDs say nothing name nothing either.
        var ids = TouchZoneDetection.Hints(JaneProbe() with { Parts = [.. JaneParts.Select(p => new RendererModelPart(p.Id, null, p.Parent))] }, new(0, 0, 1, 1))!;
        Assert.False(ids.Named);
        Assert.Equal((JaneParts.Length, 0), (ids.ModelParts, ids.NamedModelParts));
        Assert.Null(TouchZoneDetection.NamedPlace("neck", null));
    }

    [Fact]
    public async Task TheWrongAnswersOfARealVisionModelAreCorrectedOntoJaneDoesNamedParts()
    {
        // Before: with no part names (as Martlet was), the wrong boxes stay and the lower body's close-up starts below the shorts.
        var (before, beforeAsks) = await DetectWrongAnswer(TouchZoneDetection.Hints(JaneProbe(parts: false), new(0, 0, 1, 1)));
        var was = before.Zones!.ToDictionary(z => z.Id, z => z.Box);
        Assert.True(Inside(was["neck"], Box(-0.1, 0.44, 0.06, 0.36)), "before: the neck stays on the midriff");
        Assert.True(was["thigh_left"].CenterY > Y(-0.5), "before: the thigh stays on the boot");
        Assert.True(beforeAsks.Single(a => a.Step == "lower_body").Region.Y > Y(0.25), "before: the lower body's close-up starts below the shorts' top");

        var (result, asks) = await DetectWrongAnswer(TouchZoneDetection.Hints(JaneProbe(), new(0, 0, 1, 1)));

        Assert.Null(result.Failure);
        var zones = result.Zones!.ToDictionary(z => z.Id, z => z.Box);
        // The close-ups come from her own parts, and the lower body's holds her hips and groin.
        Assert.Contains(result.Steps, s => s.Contains("took head, upper_body, lower_body from the model's own named parts", StringComparison.Ordinal));
        var lower = asks.Single(a => a.Step == "lower_body");
        Assert.True(lower.Region.Y < Y(0.331), $"the lower body's close-up starts above the shorts: {lower.Region}");
        Assert.Contains("hips - the hips", lower.Text, StringComparison.Ordinal);
        Assert.Contains("the character's left leg (", lower.Text, StringComparison.Ordinal);
        // Each wrong box is on its own part now.
        Assert.True(Inside(zones["neck"], Box(-0.083, 0.75, 0.083, 0.592)), $"neck {zones["neck"]}");
        Assert.True(zones["neck"].Y > Y(0.69), $"the neck starts below her mouth, not behind her face: {zones["neck"]}");
        Assert.True(Inside(zones["lips"], Box(-0.03, 0.71, 0.03, 0.66)), $"lips {zones["lips"]}");
        Assert.True(Inside(zones["ear_left"], Box(0.033, 0.805, 0.13, 0.702)), $"ear_left {zones["ear_left"]}");
        Assert.True(Inside(zones["tail"], Box(-0.09, 0.271, 0, -1.238)), $"tail {zones["tail"]}");
        foreach (var id in new[] { "chest", "breast_left", "breast_right" })
            Assert.True(zones[id].CenterY < Y(0.45) && zones[id].CenterY > Y(0.66), $"{id} {zones[id]}");
        Assert.True(zones["breast_left"].CenterX > zones["breast_right"].CenterX);
        Assert.True(zones["waist"].CenterY < Y(0.3) && zones["waist"].CenterY > Y(0.47), $"waist {zones["waist"]}");
        // Her left leg is on the picture's right; a thigh is at its top, a knee in its middle, the foot at its bottom.
        Assert.True(Inside(zones["thigh_left"], Box(-0.038, 0.241, 0.169, -0.3)), $"thigh_left {zones["thigh_left"]}");
        Assert.True(Inside(zones["thigh_right"], Box(-0.224, 0.24, -0.027, -0.3)), $"thigh_right {zones["thigh_right"]}");
        Assert.True(Inside(zones["inner_thigh_left"], Box(-0.04, 0.2, 0.07, -0.3)), $"inner_thigh_left {zones["inner_thigh_left"]}");
        Assert.True(Inside(zones["knee_left"], Box(-0.038, -0.1, 0.169, -0.5)), $"knee_left {zones["knee_left"]}");
        Assert.True(Inside(zones["foot_left"], Box(-0.038, -0.75, 0.169, -0.962)), $"foot_left {zones["foot_left"]}");
        // The boxes that were right stay as they were.
        foreach (var id in new[] { "face", "ear_right", "upper_arm_left", "upper_arm_right", "hand_right", "foot_right" })
            Assert.True(TouchZoneDetection.Moved(zones[id], Assert.Single(WrongAnswer, z => z.Id == id).Box) <= 0.03, $"{id} {zones[id]}");
        // Every intimate zone is found, the hips, groin and buttocks between her top and her legs.
        foreach (var id in TouchZoneDetection.Erogenous) Assert.Contains(id, zones.Keys);
        foreach (var id in new[] { "hips", "groin", "buttocks" })
            Assert.True(zones[id].CenterY > Y(0.37) && zones[id].CenterY < Y(0.08), $"{id} {zones[id]}");
        var finished = Assert.Single(result.Steps, s => s.StartsWith("finally:", StringComparison.Ordinal));
        Assert.Contains("moved neck onto the model's own neck", finished, StringComparison.Ordinal);
        Assert.Contains("moved thigh_left onto the model's own left leg (its top)", finished, StringComparison.Ordinal);
        Assert.Contains("moved tail onto the model's own tail", finished, StringComparison.Ordinal);
        Assert.Contains(result.Steps, s => s.StartsWith("worked out", StringComparison.Ordinal) && s.Contains("hips from the model's own hips", StringComparison.Ordinal));
    }

    [Fact]
    public void ABodyPartAsTallAsTheWholeBodyIsNotTakenForTheUpperBody()
    {
        // Hiyori, Martlet's built-in character: her 体 ("body") runs from her neck to her feet, past her arms (腕 A).
        (string Id, string Name, double Left, double Top, double Right, double Bottom)[] hiyori =
        [
            ("PartFace", "顔", -0.09, 0.56, 0.09, 0.36), ("PartEye", "目", -0.08, 0.48, 0.07, 0.42), ("PartMouth", "口", -0.01, 0.4, 0.01, 0.38),
            ("PartHairFront", "前髪", -0.08, 0.59, 0.09, 0.43), ("PartNeck", "首", -0.03, 0.39, 0.02, 0.34),
            ("PartArmA", "腕 A", -0.19, 0.34, -0.08, -0.08), ("PartArmA", "腕 A", 0.08, 0.34, 0.21, -0.08), ("PartBody", "体", -0.13, 0.37, 0.13, -0.67)
        ];
        var probe = new RendererZoneProbe([.. hiyori.Select((d, i) => new RendererDrawableBox($"ArtMesh{i}", X(d.Left), Y(d.Top), X(d.Right), Y(d.Bottom), d.Id))],
            Parts: [.. hiyori.Select(d => d.Id).Distinct().Select(id => new RendererModelPart(id, hiyori.First(d => d.Id == id).Name))]);

        var hints = TouchZoneDetection.Hints(probe, new(0, 0, 1, 1))!;

        Assert.DoesNotContain(hints.Areas, a => a.Part == "torso");
        Assert.Null(TouchZoneDetection.NamedPlace("waist", hints));
        Assert.Null(TouchZoneDetection.NamedPlace("chest", hints));
        Assert.Equal("neck", TouchZoneDetection.NamedPlace("neck", hints)!.Value.What);
        // Her upper body's close-up goes from her neck to her hands, not to her feet.
        var upper = TouchZoneDetection.NamedRegion("upper_body", hints)!;
        Assert.True(upper.Y + upper.Height < Y(-0.3), $"{upper}");
        Assert.Null(TouchZoneDetection.NamedRegion("lower_body", hints));
    }

    [Fact]
    public async Task ChecksAreToldWhichBoxesAreOffTheModelsOwnParts()
    {
        var (_, asks) = await DetectWrongAnswer(TouchZoneDetection.Hints(JaneProbe(), new(0, 0, 1, 1)), checks: 1);

        var check = asks.First(a => a.Kind == ZoneAskKind.Check && a.Step.StartsWith("upper_body", StringComparison.Ordinal));
        Assert.Contains("(neck) is off the model's own neck, which lies at (", check.Text, StringComparison.Ordinal);
        Assert.Contains("(chest) is off the model's own upper body (its chest)", check.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("(upper_arm_left) is off", check.Text, StringComparison.Ordinal);
    }
}
