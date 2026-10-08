using Martlet.Avatar.Hosting;
using Martlet.Core.Settings;

namespace Martlet.Desktop.Tests;

public sealed class TouchZoneDetectionTests
{
    // A picture with transparent pixels around opaque blocks (x, y, width, height in pixels) of one color.
    private static ZonePixels Picture(int width, int height, (byte R, byte G, byte B) color, params (int X, int Y, int W, int H)[] blocks)
    {
        var bgra = new byte[width * height * 4];
        foreach (var (bx, by, bw, bh) in blocks)
            for (var y = by; y < by + bh; y++)
                for (var x = bx; x < bx + bw; x++)
                {
                    var i = (y * width + x) * 4;
                    (bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]) = (color.B, color.G, color.R, 255);
                }
        return new(width, height, bgra);
    }

    private static readonly (byte, byte, byte) Dark = (120, 20, 20);

    [Fact]
    public void PicturesAreFlattenedOntoAContrastingBackdropScaledAndGridded()
    {
        var source = Picture(100, 200, Dark, (20, 40, 60, 120));
        Assert.Equal(TouchZonePictures.LightBackdrop, TouchZonePictures.Backdrop(source));
        Assert.Equal(TouchZonePictures.DarkBackdrop, TouchZonePictures.Backdrop(Picture(10, 10, (250, 250, 250), (0, 0, 10, 10))));

        var sent = TouchZonePictures.Compose(source, new(0, 0, 1, 1), 400, 4, TouchZonePictures.LightBackdrop, grid: true);

        Assert.Equal((200, 400), (sent.Width, sent.Height));
        // The backdrop where the character isn't, the character where it is, and a grid line at a tenth of the width.
        Assert.Equal(((byte)236, (byte)236, (byte)236, (byte)255), sent.Pixel(50, 60));
        Assert.Equal(((byte)120, (byte)20, (byte)20, (byte)255), sent.Pixel(110, 210));
        Assert.Equal(182, sent.Pixel(20, 60).R);
        // A close-up of the top half is enlarged (no more than four times).
        var close = TouchZonePictures.Compose(source, new(0, 0, 1, 0.5), 1000, 4, TouchZonePictures.LightBackdrop, grid: false);
        Assert.Equal((400, 400), (close.Width, close.Height));
    }

    [Fact]
    public void NumberedBoxesAreDrawnInTheirColors()
    {
        var source = Picture(100, 100, Dark, (0, 0, 100, 100));
        var red = TouchZonePictures.MarkColors[0];
        var marked = TouchZonePictures.Compose(source, new(0, 0, 1, 1), 300, 4, TouchZonePictures.LightBackdrop, grid: false,
            [new MarkedBox(1, new(0.5, 0.5, 0.4, 0.4), red)]);
        // The box's bottom edge, away from its number's tag.
        Assert.Equal((red.R, red.G, red.B, (byte)255), marked.Pixel(240, 268));
    }

    [Fact]
    public void OpaqueBoundsLeaveOutStrayPixels()
    {
        var source = Picture(100, 200, Dark, (20, 40, 60, 120), (95, 5, 1, 1));
        var (share, bounds) = TouchZonePictures.Opaque(source, new(0, 0, 100, 200));
        Assert.Equal(new PixelRect(20, 40, 60, 120), bounds);
        Assert.Equal((60 * 120 + 1) / 20000.0, share, 4);
        Assert.Equal(new PixelRect(20, 5, 76, 155), TouchZonePictures.OpaqueBounds(source, new(0, 0, 100, 200)));
    }

    [Fact]
    public void AnswersAreReadLeniently()
    {
        // Cut off by the reply's length: the zones it finished still count.
        var cut = CharacterTouchZones.Parse("{\"zones\":[{\"id\":\"hair\",\"left\":0.2,\"top\":0.05,\"right\":0.8,\"bottom\":0.4},{\"id\":\"nose\",\"left\":0.45", 400, 800)!;
        Assert.Equal(["hair"], cut.Select(z => z.Id));
        Assert.Equal(0.2, cut[0].Box.X, 3);
        var sized = CharacterTouchZones.Parse("[{\"label\":\"face\",\"x\":0.3,\"y\":0.1,\"width\":0.4,\"height\":0.2}]", 400, 800)!;
        Assert.Equal(0.4, sized[0].Box.Width, 3);
        Assert.Equal("chin", CharacterTouchZones.Parse("{\"chin\":[0.4,0.3,0.6,0.35]}", 400, 800)![0].Id);
        // On a tall picture, x beyond its width means the 0..1000 grid, not pixels.
        Assert.Equal(0.7, CharacterTouchZones.Parse("[{\"id\":\"hand_left\",\"box\":[700,500,900,600]}]", 400, 800)![0].Box.X, 3);
        Assert.Equal("upper_body", TouchZoneDetection.PartId("Upper body"));
        Assert.Equal("lower_body", TouchZoneDetection.PartId("legs"));
    }

    [Fact]
    public void ChecksSayWhichBoxesAreRightMovedGoneOrAdded()
    {
        ZoneMark[] marks = [new(1, "hair", new(0.1, 0.1, 0.8, 0.5)), new(2, "nose", new(0.4, 0.4, 0.1, 0.1)), new(3, "chin", new(0.4, 0.7, 0.2, 0.1))];
        var verdict = TouchZoneDetection.ReadCheck("{\"zones\":[{\"n\":1,\"ok\":true},{\"n\":2,\"ok\":false,\"left\":0.4,\"top\":0.5,\"right\":0.6,\"bottom\":0.6}," +
            "{\"n\":3,\"visible\":false},{\"id\":\"lips\",\"left\":0.45,\"top\":0.62,\"right\":0.55,\"bottom\":0.68},{\"id\":\"tail\",\"left\":0,\"top\":0,\"right\":1,\"bottom\":1}],\"done\":false}",
            marks, TouchZoneDetection.Regions[0].Zones, 800, 800)!;

        Assert.Equal([1], verdict.Right);
        Assert.Equal(0.5, verdict.Corrected[2].Y, 3);
        Assert.Equal([3], verdict.Gone);
        Assert.Equal(["lips"], verdict.Added.Keys);
        Assert.False(verdict.Done);
        Assert.Null(TouchZoneDetection.ReadCheck("Looks good to me.", marks, TouchZoneDetection.Regions[0].Zones, 800, 800));
    }

    [Fact]
    public void TidySwapsMirroredPairsAndFitsBoxesToTheCharacter()
    {
        var snapshot = Picture(100, 100, Dark, (20, 20, 60, 60));
        var zones = new Dictionary<string, TouchZoneBox>
        {
            // Facing the viewer, the character's left hand is on the picture's right.
            ["hand_left"] = new(0.2, 0.5, 0.1, 0.1), ["hand_right"] = new(0.7, 0.5, 0.1, 0.1),
            ["chest"] = new(0.1, 0.1, 0.8, 0.8), ["hair"] = new(0.1, 0.1, 0.8, 0.3)
        };

        var notes = TouchZoneDetection.Tidy(zones, snapshot, facesViewer: true);

        Assert.Equal(["swapped hand_left and hand_right"], notes);
        Assert.Equal(0.7, zones["hand_left"].X, 3);
        Assert.Equal(new TouchZoneBox(0.2, 0.2, 0.6, 0.6), zones["chest"]);
        // Hair can be wispy: never shrunk.
        Assert.Equal(0.1, zones["hair"].X, 3);
        Assert.Empty(TouchZoneDetection.Tidy(zones, snapshot, facesViewer: null));
    }

    // A small standing character: head, body and two legs (the character's left leg on the picture's right).
    private static ZonePixels Character() => Picture(200, 400, Dark, (70, 20, 60, 70), (60, 90, 80, 130), (65, 220, 30, 170), (105, 220, 30, 170));

    private static readonly CharacterTouchZone[] Truth =
    [
        new() { Id = "hair", Box = new(0.35, 0.05, 0.3, 0.0625) }, new() { Id = "face", Box = new(0.4, 0.1125, 0.2, 0.1) },
        new() { Id = "chest", Box = new(0.35, 0.25, 0.3, 0.125) }, new() { Id = "stomach", Box = new(0.375, 0.375, 0.25, 0.15) },
        new() { Id = "thigh_left", Box = new(0.525, 0.55, 0.15, 0.2) }, new() { Id = "thigh_right", Box = new(0.325, 0.55, 0.15, 0.2) },
        new() { Id = "foot_left", Box = new(0.525, 0.9, 0.15, 0.075) }
    ];

    [Fact]
    public async Task TheLoopFindsEachPartCloseUpAndCorrectsItsBoxesUntilTheyAreRight()
    {
        // The first answers are off: the hair too low, the chest too far left, the thighs swapped and the left foot missing.
        CharacterTouchZone[] guess =
        [
            new() { Id = "hair", Box = new(0.35, 0.1, 0.3, 0.0625) }, Truth[1], new() { Id = "chest", Box = new(0.3, 0.25, 0.3, 0.125) }, Truth[3],
            Truth[4] with { Id = "thigh_right" }, Truth[5] with { Id = "thigh_left" }
        ];
        var asks = new List<ZoneAsk>();
        var progress = new List<string>();

        var result = await TouchZoneDetection.RunAsync(Character(), null, (ask, _) =>
        {
            asks.Add(ask);
            return Task.FromResult<(string?, string?)>((TouchZoneDetection.Oracle(ask, Truth, guess), null));
        }, p => progress.Add(p.Text), CancellationToken.None);

        Assert.Null(result.Failure);
        foreach (var truth in Truth)
        {
            var found = Assert.Single(result.Zones!, z => z.Id == truth.Id);
            Assert.True(TouchZoneDetection.Moved(found.Box, truth.Box) <= 0.03, $"{truth.Id}: {found.Box} vs {truth.Box}");
        }
        Assert.Equal(ZoneAskKind.Parts, asks[0].Kind);
        Assert.Equal(["head", "upper_body", "lower_body"], asks.Where(a => a.Kind == ZoneAskKind.Zones).Select(a => a.Step));
        var check = asks.First(a => a.Kind == ZoneAskKind.Check && a.Step.StartsWith("upper_body", StringComparison.Ordinal));
        Assert.Contains("1 = chest", check.Text, StringComparison.Ordinal);
        Assert.NotEmpty(check.Marks);
        // Every picture sent is at most 1024 pixels on its longer side, the close-ups enlarged.
        Assert.All(asks, a => Assert.True(Math.Max(a.Picture.Width, a.Picture.Height) <= 1024));
        Assert.True(asks.First(a => a.Step == "head").Picture.Width > 300);
        Assert.Contains(result.Steps, s => s.Contains("swapped thigh_left and thigh_right", StringComparison.Ordinal));
        Assert.Contains(result.Steps, s => s.Contains("added foot_left", StringComparison.Ordinal));
        Assert.Contains(progress, p => p.Contains("round 1 of 2", StringComparison.Ordinal));
        Assert.Equal(result.Requests, asks.Count);
    }

    [Fact]
    public async Task WithoutPartsTheOutlineGivesTheCloseUpsAndAFailedFirstRequestStopsAtOnce()
    {
        var asks = new List<ZoneAsk>();
        var result = await TouchZoneDetection.RunAsync(Character(), null, (ask, _) =>
        {
            asks.Add(ask);
            return Task.FromResult<(string?, string?)>(ask.Kind == ZoneAskKind.Parts ? ("I see a character.", null)
                : (TouchZoneDetection.Oracle(ask, Truth), null));
        }, null, CancellationToken.None);
        Assert.Contains(result.Steps, s => s.Contains("guessed head, upper_body, lower_body from the character's outline", StringComparison.Ordinal));
        Assert.Contains(result.Zones!, z => z.Id == "face");

        var failed = await TouchZoneDetection.RunAsync(Character(), null,
            (_, _) => Task.FromResult<(string?, string?)>((null, "ModelUnsupported")), null, CancellationToken.None);
        Assert.Null(failed.Zones);
        Assert.Equal("ModelUnsupported", failed.Failure);
        Assert.Equal(1, failed.Requests);
    }

    [Fact]
    public async Task ARequestThatFailsPartWayStopsTheDetectionWithTheZonesFoundUntilThen()
    {
        var asks = new List<ZoneAsk>();
        // The parts, the head's close-up, then its check fails: the model's computer stopped answering.
        var result = await TouchZoneDetection.RunAsync(Character(), null, (ask, _) =>
        {
            asks.Add(ask);
            return Task.FromResult<(string?, string?)>(asks.Count == 3 ? (null, "ResponseTruncated") : (TouchZoneDetection.Oracle(ask, Truth), null));
        }, null, CancellationToken.None, new ZoneDetectionOptions { Required = TouchZoneDetection.Erogenous });

        Assert.Equal("ResponseTruncated", result.Failure);
        Assert.Equal(3, result.Requests);
        Assert.Equal(3, asks.Count);
        Assert.Equal(ZoneAskKind.Check, asks[^1].Kind);
        Assert.Contains(result.Zones!, z => z.Id == "face");
        Assert.DoesNotContain(result.Zones!, z => z.Id == "chest");
        Assert.Contains("head check 1: the request failed (ResponseTruncated), so finding zones stopped", result.Steps);
        // Nothing is asked again or worked out after a failure.
        Assert.DoesNotContain(result.Steps, s => s.StartsWith("worked out", StringComparison.Ordinal));

        // An answer with nothing usable in it is not a failure: the detection goes on.
        var quiet = await TouchZoneDetection.RunAsync(Character(), null, (ask, _) =>
            Task.FromResult<(string?, string?)>((ask.Step == "head" ? null : TouchZoneDetection.Oracle(ask, Truth), null)), null, CancellationToken.None);
        Assert.Null(quiet.Failure);
        Assert.Contains(quiet.Zones!, z => z.Id == "chest");
    }

    [Fact]
    public async Task TheIntimateZonesAreAskedForAgainThenWorkedOutWhileIncludeIntimateZonesIsOn()
    {
        var groin = new CharacterTouchZone { Id = "groin", Box = new(0.45, 0.5, 0.1, 0.08) };
        var asks = new List<ZoneAsk>();
        // The close-ups leave out every intimate zone but the chest; asked again on the whole character, the model finds the groin.
        var result = await TouchZoneDetection.RunAsync(Character(), null, (ask, _) =>
        {
            asks.Add(ask);
            return Task.FromResult<(string?, string?)>((TouchZoneDetection.Oracle(ask, ask.Step == TouchZoneDetection.MissingStep ? [.. Truth, groin] : Truth), null));
        }, null, CancellationToken.None, new ZoneDetectionOptions { Required = TouchZoneDetection.Erogenous });

        Assert.Null(result.Failure);
        var again = Assert.Single(asks, a => a.Step == TouchZoneDetection.MissingStep);
        Assert.Equal(new TouchZoneBox(0, 0, 1, 1), again.Region);
        Assert.Contains("groin - the groin, where the legs meet", again.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("chest -", again.Text, StringComparison.Ordinal);
        Assert.Contains("A zone that clothing or hair covers is still there", again.Instructions, StringComparison.Ordinal);
        Assert.Contains("A zone that clothing or hair covers is still there", TouchZoneDetection.ZonesInstructions, StringComparison.Ordinal);
        Assert.Contains("A zone that clothing or hair covers is still there", TouchZoneDetection.CheckInstructions, StringComparison.Ordinal);
        foreach (var id in TouchZoneDetection.Erogenous) Assert.Contains(result.Zones!, z => z.Id == id);
        var zones = result.Zones!.ToDictionary(z => z.Id, z => z.Box);
        // The groin is the model's own box; the rest are worked out from the zones around them.
        Assert.True(TouchZoneDetection.Moved(zones["groin"], groin.Box) <= 0.03, $"groin: {zones["groin"]}");
        var worked = Assert.Single(result.Steps, s => s.StartsWith("worked out", StringComparison.Ordinal));
        Assert.DoesNotContain("groin", worked, StringComparison.Ordinal);
        Assert.Contains("breast_left from the chest", worked, StringComparison.Ordinal);
        Assert.Contains("hips from the thighs", worked, StringComparison.Ordinal);
        Assert.Contains("inner_thigh_left from thigh_left", worked, StringComparison.Ordinal);
        // Facing the viewer, the character's left is on the picture's right; the inner side of a thigh faces the legs' middle.
        Assert.True(zones["breast_left"].CenterX > zones["breast_right"].CenterX);
        Assert.True(zones["ear_left"].CenterX > zones["ear_right"].CenterX);
        Assert.True(zones["inner_thigh_left"].CenterX > zones["inner_thigh_right"].CenterX);
        Assert.True(zones["inner_thigh_left"].CenterX < zones["thigh_left"].CenterX);
        Assert.True(zones["breast_left"].Y >= zones["chest"].Y && zones["breast_left"].Y + zones["breast_left"].Height <= zones["chest"].Y + zones["chest"].Height + 0.01);
        Assert.True(zones["lips"].CenterY > zones["face"].CenterY);
        Assert.True(zones["neck"].Y >= zones["face"].Y + zones["face"].Height - 0.01 && zones["neck"].Y < zones["chest"].Y);
        Assert.True(zones["hips"].Y < zones["thigh_left"].Y && zones["hips"].Y + zones["hips"].Height > zones["thigh_left"].Y);

        // With Include intimate zones off, nothing is asked again or worked out.
        var off = await TouchZoneDetection.RunAsync(Character(), null,
            (ask, _) => Task.FromResult<(string?, string?)>((TouchZoneDetection.Oracle(ask, Truth), null)), null, CancellationToken.None);
        Assert.DoesNotContain(off.Steps, s => s.StartsWith(TouchZoneDetection.MissingStep, StringComparison.Ordinal) || s.StartsWith("worked out", StringComparison.Ordinal));
        Assert.DoesNotContain(off.Zones!, z => z.Id == "groin");
    }

    [Fact]
    public void ACharacterWithNoLowerBodyGetsNoHipsOrGroinWorkedOut()
    {
        // A bust-up: head and chest only.
        var bust = Picture(200, 200, Dark, (50, 10, 100, 190));
        var zones = new Dictionary<string, TouchZoneBox> { ["face"] = new(0.35, 0.1, 0.3, 0.3), ["chest"] = new(0.3, 0.6, 0.4, 0.3) };
        var regions = new Dictionary<string, (TouchZoneBox, bool)>
        {
            ["head"] = (new(0.25, 0.05, 0.5, 0.45), true), ["upper_body"] = (new(0.25, 0.5, 0.5, 0.5), true),
            ["lower_body"] = (new(0.25, 0.9, 0.5, 0.1), false)
        };

        var notes = TouchZoneDetection.Derive(zones, TouchZoneDetection.Erogenous, bust, true, regions, new(0.25, 0.05, 0.5, 0.95));

        Assert.Contains("not hips, groin, buttocks, inner_thigh_left, inner_thigh_right: the character shows no lower body", notes);
        Assert.DoesNotContain("groin", zones.Keys);
        Assert.DoesNotContain("hips", zones.Keys);
        foreach (var id in new[] { "lips", "ear_left", "ear_right", "neck", "breast_left", "breast_right", "waist" }) Assert.Contains(id, zones.Keys);
        // Only missing zones are worked out: the chest stays the model's.
        Assert.Equal(new TouchZoneBox(0.3, 0.6, 0.4, 0.3), zones["chest"]);
    }

    [Fact]
    public void HintsComeFromTheSkeletonAndNamedParts()
    {
        var probe = new RendererZoneProbe(
            [new("D_HAIR_FRONT_00", 0.4, 0.1, 0.6, 0.3), new("HairBack", 0.35, 0.1, 0.65, 0.5), new("ArtMesh12", 0, 0, 1, 1), new("手L", 0.7, 0.5, 0.75, 0.55)],
            [new("leftUpperArm", 0.6, 0.3), new("rightUpperArm", 0.4, 0.3)]);

        var hints = TouchZoneDetection.Hints(probe, new(0, 0, 1, 1))!;

        Assert.True(hints.FacesViewer);
        var hair = Assert.Single(hints.Areas, a => a.Part == "hair");
        Assert.Equal(2, hair.Drawables);
        Assert.Equal(0.35, hair.Box.X, 3);
        Assert.Contains(hints.Areas, a => a.Part == "hands");
        Assert.DoesNotContain(hints.Areas, a => a.Part == "face");
        Assert.Contains("the character's left shoulder joint (0.60, 0.30)", TouchZoneDetection.PartsText(hints), StringComparison.Ordinal);
        Assert.False(TouchZoneDetection.Hints(new(null, [new("leftUpperArm", 0.4, 0.3), new("rightUpperArm", 0.6, 0.3)]), new(0, 0, 1, 1))!.FacesViewer);
    }

    [Fact]
    public void TheChestAndStomachAreExpectedInTheMiddleOfWhatTheirBonesMove()
    {
        // The chest and spine bones' joints sit low on what they move (the spine's near the waist); a VRM without an upper chest
        // has nothing higher to say where the chest is.
        var zones = new Dictionary<string, TouchZoneBox> { ["chest"] = Truth[2].Box, ["stomach"] = Truth[3].Box };
        ZoneMark[] marks = [new(1, "chest", Truth[2].Box), new(2, "stomach", Truth[3].Box)];
        ZoneHints Skeleton(params ZoneHintPoint[] bones) => new(bones, [], true);
        string[] Missed(ZoneHints hints) => [.. TouchZoneDetection.Problems(marks, zones, Character(), true, hints, new(0, 0, 1, 1))
            .Where(p => p.Contains("own skeleton", StringComparison.Ordinal))];

        Assert.Empty(Missed(Skeleton(new("spine", 0.5, 0.56), new("chest", 0.5, 0.42), new("neck", 0.5, 0.2))));
        Assert.Empty(Missed(Skeleton(new("spine", 0.5, 0.56), new("chest", 0.5, 0.45), new("upperChest", 0.5, 0.31), new("neck", 0.5, 0.2))));
        var missed = Assert.Single(Missed(Skeleton(new("spine", 0.5, 0.56), new("chest", 0.5, 0.45), new("upperChest", 0.5, 0.45), new("neck", 0.5, 0.2))));
        Assert.Contains("(chest)", missed, StringComparison.Ordinal);
        var text = TouchZoneDetection.CheckText("the upper body", marks, [], [], Skeleton(new("chest", 0.5, 0.45), new("spine", 0.5, 0.56)), new(0, 0, 1, 1));
        Assert.Contains("chest (its base, below the breasts) (0.50, 0.45)", text, StringComparison.Ordinal);
        Assert.Contains("spine (its base, near the waist) (0.50, 0.56)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ZonesFoundWithTheCharacterFramedWholeMatchWhereATouchLandsInThatFraming()
    {
        var settings = new CharacterTouchZoneSettings
        {
            ModelId = "m", Crop = new(0, 0, 1, 1), Whole = true, Zones = [new() { Id = "hand_right", Box = new(0.1, 0.6, 0.1, 0.1) }]
        };
        // Zoomed in, the hand shows in the middle of the page; framed whole it is where the snapshot had it.
        var touch = new CharacterTouch(0.5, 0.5, [], [], null, null, false, null, null, 0, 0.15, 0.65);

        Assert.Equal("box", CharacterTouchZones.Match(settings, touch)!.How);
        Assert.Equal("coarse", CharacterTouchZones.Match(settings with { Whole = false }, touch)!.How);

        // The renderers draw fitted * zoom + (x * frame, y): unzoomed nothing moves; zoomed in twice, a point a quarter of the
        // way in from the top right sits halfway between it and the middle.
        Assert.Equal((0.3, 0.7), CharacterTouch.Unframed(0.3, 0.7, 1, 0, 0, 0.5));
        var (x, y) = CharacterTouch.Unframed(0.75, 0.25, 2, 0, 0, 0.5);
        Assert.Equal(0.625, x, 6);
        Assert.Equal(0.375, y, 6);
        // Panned right by 0.4 of the frame's clip space in a frame half the page wide: the page point moved 0.1 to the right.
        Assert.Equal(0.5, CharacterTouch.Unframed(0.6, 0.5, 1, 0.4, 0, 0.5).X, 6);
    }

    [Fact]
    public void AWholePictureZoomsOutOnlyAsFarAsTheCutOffPartsReach()
    {
        // The page cuts the legs off at its bottom: a thigh crosses it, a calf below touches the thigh, and a part placed far
        // below touches nothing.
        RendererDrawableBox[] drawables =
        [
            new("Body", 0.3, 0.05, 0.7, 0.7), new("ThighL", 0.35, 0.6, 0.5, 1.1), new("CalfL", 0.38, 1.05, 0.48, 1.3), new("Far", 0.4, 3, 0.5, 3.2)
        ];
        var seen = new TouchZoneBox(0.3, 0.05, 0.4, 0.95);

        Assert.Equal((1d, 0d, 0d), WholeFraming.Fit(seen, PageEdges.None, drawables, 0.5));
        var (zoom, x, y) = WholeFraming.Fit(seen, PageEdges.Bottom, drawables, 0.5);
        Assert.Equal(0.96 / 1.25, zoom, 6);
        // The page in that framing, read with the character framed whole, holds the head to the calf with the margin around it.
        var page = WholeFraming.Unframed(new TouchZoneBox(0, 0, 1, 1), zoom, x, y, 0.5);
        Assert.Equal(0.05 - WholeFraming.Margin / zoom, page.Y, 6);
        Assert.Equal(1.3 + WholeFraming.Margin / zoom, page.Y + page.Height, 6);
        Assert.Equal(0.5, page.CenterX, 6);

        // A transparent mesh past an edge the character's pixels don't reach (the top) changes nothing, and an edge they reach
        // with nothing past it needs no zoom.
        Assert.Equal((zoom, x, y), WholeFraming.Fit(seen, PageEdges.Bottom, [.. drawables, new("Glow", 0.2, -0.5, 0.8, 0.3)], 0.5));
        Assert.Equal((1d, 0d, 0d), WholeFraming.Fit(seen, PageEdges.Top, drawables, 0.5));
        // Feet that end at the page's edge fit, and parts far past it that touch nothing on the page don't count.
        Assert.Equal((1d, 0d, 0d), WholeFraming.Fit(seen, PageEdges.Bottom, [new("Body", 0.3, 0.05, 0.7, 1), new("Far", 0.4, 3, 0.5, 3.2)], 0.5));
        // A part that runs far past the page from the character zooms out no further than the smallest zoom.
        Assert.Equal(WholeFraming.MinimumZoom, WholeFraming.Fit(seen, PageEdges.Bottom, [.. drawables, new("Cape", 0.3, 0.9, 0.7, 9)], 0.5).Zoom);
    }

    [Fact]
    public void AZoomedOutPicturesCropAndProbeReadAsTheyWouldFramedWhole()
    {
        var (zoom, x, y) = (0.75, 0.2, 0.25);
        // Where a point of the character framed whole shows in that framing: the renderers draw fitted * zoom + (x * frame, y).
        (double X, double Y) Page(double u, double v) => (((2 * u - 1) * zoom + x * 0.5 + 1) / 2, (1 - ((1 - 2 * v) * zoom + y)) / 2);
        var (left, top) = Page(0.3, 0.1);
        var (right, bottom) = Page(0.7, 1.3);

        var probe = WholeFraming.Unframed(new RendererZoneProbe([new("Calf", left, top, right, bottom)], [new("leftFoot", left, bottom)]), zoom, x, y, 0.5);
        var calf = Assert.Single(probe.Drawables!);
        Assert.Equal(0.3, calf.Left, 6);
        Assert.Equal(0.1, calf.Top, 6);
        Assert.Equal(0.7, calf.Right, 6);
        Assert.Equal(1.3, calf.Bottom, 6);
        var foot = Assert.Single(probe.Bones!);
        Assert.Equal((0.3, 1.3), (Math.Round(foot.X, 6), Math.Round(foot.Y, 6)));

        // The crop reaches past the page's bottom framed whole, and so does a zone found low on the picture.
        var crop = WholeFraming.Unframed(new TouchZoneBox(left, top, right - left, bottom - top), zoom, x, y, 0.5);
        Assert.Equal(1.2, crop.Height, 6);
        Assert.True(new TouchZoneBox(0.4, 0.9, 0.2, 0.1).Within(crop).Y > 1);
        // Unzoomed nothing moves.
        var same = WholeFraming.Unframed(new TouchZoneBox(0.1, 0.2, 0.3, 0.4), 1, 0, 0, 0.5);
        Assert.Equal((0.1, 0.2, 0.3, 0.4), (Math.Round(same.X, 9), Math.Round(same.Y, 9), Math.Round(same.Width, 9), Math.Round(same.Height, 9)));
    }

    [Fact]
    public void DetectZonesIsOffOnlyWithoutAModelThatCanSeeAndSaysWhy()
    {
        static SetupRoute Thinking(string model) => new()
        {
            Role = SetupRole.Llm, ProviderAlias = "openai", Origin = "https://api.openai.com", ModelId = model, ConfigurationRevision = Guid.NewGuid()
        };

        var none = MainWindow.TouchZonesSight(null, null, null, fixture: false);
        Assert.False(none.CanSee);
        Assert.Contains("no model that can see pictures", none.Off, StringComparison.Ordinal);
        var textOnly = MainWindow.TouchZonesSight(Thinking("llama3.1:8b"), null, null, fixture: false);
        Assert.False(textOnly.CanSee);
        Assert.Contains("text-only", textOnly.Off, StringComparison.Ordinal);

        // A model that sees, or one Martlet can't tell about, keeps it on with no note.
        var seeing = MainWindow.TouchZonesSight(Thinking("gpt-4o"), null, null, fixture: false);
        Assert.True(seeing.CanSee);
        Assert.Null(seeing.Off);
        Assert.True(MainWindow.TouchZonesSight(Thinking("my-own-model"), null, null, fixture: false).CanSee);
        // A Thinking pool member that sees takes the pictures even when the Thinking model is text-only.
        var pooled = MainWindow.TouchZonesSight(Thinking("llama3.1:8b"), null, "diva (qwen2.5vl:7b)", fixture: false);
        Assert.True(pooled.CanSee);
        Assert.Null(pooled.Off);
        Assert.StartsWith("diva (qwen2.5vl:7b) in your Thinking pool can see pictures", pooled.Line, StringComparison.Ordinal);
        Assert.True(MainWindow.TouchZonesSight(null, null, null, fixture: true).CanSee);
    }

    // A still renderer as the touch zones picture needs it: it records what it was started with and asked, and answers with a
    // picture (png, or three bytes that aren't one; or fails to start).
    private sealed class StillRenderer(bool failStart = false, byte[]? png = null) : IAvatarRenderer
    {
        internal List<RendererMessage> Sent { get; } = [];
        internal string? Revision { get; private set; }
        internal bool Disposed { get; private set; }
        public RendererCapabilities? Capabilities => null;
        public bool HasExited => Disposed;
        public Task Exited => Task.CompletedTask;
        public event Action<string>? Requested { add { } remove { } }
        public Task StartAsync(AvatarProfile profile, string revision, RendererPlacement? placement, bool voiceMuted, CancellationToken token)
        {
            Revision = revision;
            return failStart ? throw new InvalidOperationException("The character renderer couldn't load this model.") : Task.CompletedTask;
        }
        public Task<RendererMessage> SendAsync<T>(string kind, T data, CancellationToken token, TimeSpan? timeout = null)
        {
            Sent.Add(RendererProtocol.Message(kind, Guid.Empty, data));
            return Task.FromResult(RendererProtocol.Message("picture", Guid.Empty,
                new RendererPicture(Convert.ToBase64String(png ?? [1, 2, 3]), 1, 3, 0.34, 0.04, 0.28, 1.07, new([new("Hair", 0.4, 0.05, 0.6, 0.3)]), 0.94)));
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    [Fact]
    public async Task TheTouchZonesPictureComesFromAStillRendererOfItsOwnSoTheCharacterNeedntShow()
    {
        using var scope = new AvatarHostingTests.Scope();
        var stills = new List<StillRenderer>();
        var failing = false;
        await using var avatar = new AvatarController(createRenderer: () => throw new InvalidOperationException("the character must not show"),
            createStillRenderer: () => { var still = new StillRenderer(failing); stills.Add(still); return still; });

        var shot = await avatar.ZoneSnapshotAsync(scope.Profile(), default);
        Assert.NotNull(shot);
        Assert.False(avatar.IsShowing);
        var used = Assert.Single(stills);
        Assert.True(used.Disposed, "the still renderer closes once the picture is taken");
        Assert.Equal((await LocalAvatarFiles.SnapshotAsync(scope.Profile(), default)).Revision, used.Revision);
        var asked = Assert.Single(used.Sent);
        Assert.Equal("snapshot", asked.Kind);
        Assert.True(RendererProtocol.Data<RendererSnapshot>(asked).Whole);
        Assert.Equal([1, 2, 3], shot.Value.Png);
        Assert.Equal(("", 0.94, 1.07), (shot.Value.Picture.Png, shot.Value.Picture.Zoom, shot.Value.Picture.CropHeight));
        Assert.Equal("Hair", Assert.Single(shot.Value.Probe!.Drawables!).Id);

        // One that can't start is closed too, and the picture is only missing.
        failing = true;
        Assert.Null(await avatar.ZoneSnapshotAsync(scope.Profile(), default));
        Assert.True(stills[^1].Disposed);
        Assert.Empty(stills[^1].Sent);
    }

    private static byte[] Png(ZonePixels pixels)
    {
        var source = System.Windows.Media.Imaging.BitmapSource.Create(pixels.Width, pixels.Height, 96, 96, System.Windows.Media.PixelFormats.Bgra32,
            null, pixels.Bgra, pixels.Width * 4);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    // A vision model's answer to each request of a detection, by number: the parts, then the head's close-up; at request 3 (the
    // head's check) the computer it runs on stops answering.
    private static Task<(string?, string?)> FailsAtThree(int request) => Task.FromResult<(string?, string?)>(request switch
    {
        1 => ("{\"parts\":[{\"id\":\"head\",\"left\":0.3,\"top\":0.03,\"right\":0.7,\"bottom\":0.25},{\"id\":\"upper_body\",\"left\":0.25,\"top\":0.2," +
            "\"right\":0.75,\"bottom\":0.57},{\"id\":\"lower_body\",\"left\":0.3,\"top\":0.5,\"right\":0.7,\"bottom\":0.99}]}", null),
        2 => ("{\"zones\":[{\"id\":\"face\",\"left\":0.3,\"top\":0.3,\"right\":0.7,\"bottom\":0.7}]}", null),
        _ => (null, "ResponseTruncated")
    });

    [Fact]
    public async Task ADetectionThatFailsPartWayKeepsTheZonesFromBeforeAndTheirPicture()
    {
        using var scope = new AvatarHostingTests.Scope();
        var data = Path.Combine(scope.DirectoryPath, "data");
        var png = Png(Character());
        await using var avatar = new AvatarController(createRenderer: () => throw new InvalidOperationException("the character must not show"),
            createStillRenderer: () => new StillRenderer(png: png));
        await CharacterTouchZones.SaveAsync(data, new CharacterTouchZoneSettings
        {
            ModelId = "model-1", DetectedBy = CharacterTouchZoneSettings.ByVision, Zones = [.. Truth]
        }, DateTimeOffset.Now);
        byte[] earlierPicture = [9, 8, 7];
        await CharacterTouchZones.SaveSnapshotAsync(data, "model-1", earlierPicture);
        var service = new CharacterTouchZoneService(data);
        service.Follow("model-1");
        var requests = 0;

        var line = await service.DetectAsync(avatar, scope.Profile(), (_, _, _, _, _) => FailsAtThree(++requests), CancellationToken.None);

        Assert.Equal(3, requests);
        Assert.Equal("Finding zones stopped at request 3: couldn't ask the Thinking model (ResponseTruncated). Your 7 zones from before are kept. " +
            "Try again when it answers.", line);
        Assert.Equal(line, service.Detection);
        // The zones found until then were saved over the earlier ones as they came; the earlier ones and their picture are back.
        Assert.Equal(Truth.Select(z => z.Id), service.Current!.Zones.Select(z => z.Id));
        Assert.Equal(Truth.Select(z => z.Id), CharacterTouchZones.Load(data, "model-1")!.Zones.Select(z => z.Id));
        Assert.Equal(earlierPicture, File.ReadAllBytes(CharacterTouchZones.SnapshotPath(data, "model-1")));
        // The pictures sent belong to the detection that failed, not to the zones shown.
        Assert.False(service.Sent!.Saved);
        Assert.Equal(3, service.Sent.Requests);
        Assert.False(service.Busy);
    }

    [Fact]
    public async Task WithNoZonesBeforeADetectionThatFailsPartWayKeepsTheZonesFoundUntilThen()
    {
        using var scope = new AvatarHostingTests.Scope();
        var data = Path.Combine(scope.DirectoryPath, "data");
        var png = Png(Character());
        await using var avatar = new AvatarController(createRenderer: () => throw new InvalidOperationException("the character must not show"),
            createStillRenderer: () => new StillRenderer(png: png));
        var service = new CharacterTouchZoneService(data);
        service.Follow("model-1");
        var requests = 0;

        var line = await service.DetectAsync(avatar, scope.Profile(), (_, _, _, _, _) => FailsAtThree(++requests), CancellationToken.None);

        Assert.Equal("Finding zones stopped at request 3: couldn't ask the Thinking model (ResponseTruncated). The 1 zone found until then is kept. " +
            "Press Detect again to find the rest.", line);
        Assert.Equal(["face"], service.Current!.Zones.Select(z => z.Id));
        Assert.Equal(png, File.ReadAllBytes(CharacterTouchZones.SnapshotPath(data, "model-1")));
        Assert.True(service.Sent!.Saved);
    }

    [Fact]
    public void WhatWasSentIsDescribedPlainly()
    {
        var sent = new TouchZoneSent(DateTimeOffset.Now, false, 4,
        [
            new("01-parts.png", "parts", "Parts", 683, 1024, 400_000, "image/png"), new("02-head.png", "head", "Zones", 1024, 900, 300_000, "image/png"),
            new("03-head-check-1.png", "head check 1", "Check", 1024, 900, 310_000, "image/png"),
            new("04-upper_body.jpg", "upper_body", "Zones", 900, 1024, 200_000, "image/jpeg")
        ], []);

        var line = sent.Describe();

        Assert.Contains("Thinking saw 4 pictures", line, StringComparison.Ordinal);
        Assert.Contains("up to 1024 pixels", line, StringComparison.Ordinal);
        Assert.Contains("close-ups of the head and upper body", line, StringComparison.Ordinal);
        Assert.Contains("for 1 check.", line, StringComparison.Ordinal);
        Assert.StartsWith("FIXTURE - NOT AI", (sent with { Fixture = true }).Describe(), StringComparison.Ordinal);
        // Asking again for the zones the close-ups missed is said apart from the close-ups.
        var again = (sent with { Pictures = [.. sent.Pictures, new("05-missing.png", TouchZoneDetection.MissingStep, "Zones", 402, 1024, 180_000, "image/png")] })
            .Describe();
        Assert.Contains("close-ups of the head and upper body,", again, StringComparison.Ordinal);
        Assert.EndsWith(", and the whole character again for the zones the close-ups missed.", again, StringComparison.Ordinal);
    }
}
