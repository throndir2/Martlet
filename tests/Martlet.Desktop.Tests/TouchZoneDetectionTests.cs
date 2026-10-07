using Martlet.Avatar.Hosting;

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
    }
}
