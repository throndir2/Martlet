using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Martlet.Avatar.Hosting;
using Martlet.Core.Contracts;
using Martlet.Providers;

namespace Martlet.Desktop.Tests;

public sealed class CharacterEyesTests
{
    // Boxes on the standard close-up (1.6 face widths square around an upright face): each iris 0.1 face widths across, 0.2
    // face widths left and right of the face's middle and 0.02 above it, in an opening 0.2 wide and 0.08 tall.
    private const string Answer = """
        {"left":{"iris":{"left":0.34375,"top":0.45625,"right":0.40625,"bottom":0.51875},"eye":{"left":0.3125,"top":0.4625,"right":0.4375,"bottom":0.5125}},
         "right":{"iris":{"left":0.59375,"top":0.45625,"right":0.65625,"bottom":0.51875},"eye":{"left":0.5625,"top":0.4625,"right":0.6875,"bottom":0.5125}}}
        """;

    private static EyeBoxes Boxes(string answer = Answer) => CharacterEyes.Parse(answer, 768, 768).Boxes!;

    private static void Near(double expected, double actual) => Assert.Equal(expected, actual, 4);

    [Fact]
    public void AnswersAreReadInTheUsualShapesAndUnits()
    {
        var nested = Boxes();
        Near(0.34375, nested.LeftIris.X);
        Near(0.0625, nested.RightIris.Height);
        Near(0.05, nested.RightEye.Height);

        // Flat keys with arrays, in pixels of the 768-pixel close-up.
        var flat = CharacterEyes.Parse("Here: {\"left_iris\":[264,350,312,398],\"left_eye\":[240,355,336,394],\"right_iris\":[456,350,504,398]," +
            "\"right_eye\":[432,355,528,394]}", 768, 768).Boxes!;
        Near(264 / 768.0, flat.LeftIris.X);
        Near((528 - 432) / 768.0, flat.RightEye.Width);

        // A list of eyes, each naming its side (the right one first), with boxes on the 0..1000 grid of a smaller picture.
        var listed = CharacterEyes.Parse("""
            {"eyes":[{"side":"right","iris":[600,450,660,520],"eye":[560,460,690,510]},
                     {"side":"left","iris":{"x1":340,"y1":450,"x2":400,"y2":520},"eye":{"x1":310,"y1":460,"x2":440,"y2":510}}]}
            """, 400, 400).Boxes!;
        Near(0.34, listed.LeftIris.X);
        Near(0.6, listed.RightIris.X);
        // Boxes named by label, edges given as x, y, width, height.
        var labelled = CharacterEyes.Parse("""
            [{"label":"left iris","x":0.34,"y":0.45,"width":0.06,"height":0.07},{"label":"left eye","x":0.31,"y":0.46,"width":0.13,"height":0.05},
             {"label":"right iris","x":0.6,"y":0.45,"width":0.06,"height":0.07},{"label":"right eye","x":0.56,"y":0.46,"width":0.13,"height":0.05}]
            """, 768, 768).Boxes!;
        Near(0.44, labelled.LeftEye.X + labelled.LeftEye.Width);

        var (none, missing) = CharacterEyes.Parse("{\"left\":{\"iris\":[0.3,0.4,0.4,0.5],\"eye\":[0.25,0.4,0.45,0.5]},\"right\":{\"eye\":[0.55,0.4,0.75,0.5]}}", 768, 768);
        Assert.Null(none);
        Assert.Equal("it gave no box for the right iris", missing);
        Assert.Equal("the answer had no JSON", CharacterEyes.Parse("I can't see any eyes.", 768, 768).Problem);
        Assert.Equal("the answer was empty", CharacterEyes.Parse(" ", 768, 768).Problem);
    }

    [Fact]
    public void TheLeftEyeComesFirstAndMartletsChecksFindWhatIsWrong()
    {
        var close = EyeCloseUp.Standard();
        var good = Boxes();
        Assert.Empty(CharacterEyes.Problems(good, close));
        // Labelled by the character's own sides: put back by where the eyes are.
        var (ordered, swapped) = CharacterEyes.Ordered(good.Swapped);
        Assert.True(swapped);
        Assert.Equal(good, ordered);
        Assert.False(CharacterEyes.Ordered(good).Swapped);

        // The left iris on the right eye.
        var outside = CharacterEyes.Problems(good with { LeftIris = good.RightIris }, close);
        Assert.Contains("The left iris (box 1) lies mostly outside the left eye's opening (box 2).", outside);
        // A closed (flat) eye, an eye far too wide, both eyes in one place and eyes that aren't level.
        Assert.Contains(CharacterEyes.Problems(good with { LeftEye = good.LeftEye with { Y = 0.485, Height = 0.004 } }, close),
            p => p.Contains("very flat", StringComparison.Ordinal));
        Assert.Contains(CharacterEyes.Problems(good with { LeftEye = new(0.05, 0.4625, 0.45, 0.05) }, close),
            p => p.Contains("face widths wide", StringComparison.Ordinal));
        Assert.Contains("The two eyes' openings (boxes 2 and 4) overlap: they must be the character's two different eyes.",
            CharacterEyes.Problems(good with { RightIris = good.LeftIris, RightEye = good.LeftEye }, close));
        Assert.Contains(CharacterEyes.Problems(good with { RightIris = good.RightIris with { Y = 0.7 }, RightEye = good.RightEye with { Y = 0.7 } }, close),
            p => p.Contains("aren't level", StringComparison.Ordinal));
        // An iris far too small for the face.
        Assert.Contains(CharacterEyes.Problems(good with { LeftIris = new(0.37, 0.48, 0.005, 0.005) }, close),
            p => p.StartsWith("The left iris (box 1) is 0.01 face widths across", StringComparison.Ordinal));
    }

    [Fact]
    public void BoxesBecomeTheHintInFaceWidthsFromTheFacesMiddleWithItsRollTakenOut()
    {
        var hint = CharacterEyes.Hint(Boxes(), EyeCloseUp.Standard());
        Assert.False(hint.Clears);
        Assert.True(hint.IsValid);
        Assert.Equal(new RendererEye(new(-0.2, -0.02, 0.05), new(-0.2, -0.02, 0.1, 0.04)), hint.Left);
        Assert.Equal(new RendererEye(new(0.2, -0.02, 0.05), new(0.2, -0.02, 0.1, 0.04)), hint.Right);

        // A face rolled 30 degrees clockwise: a point of its own frame lands on the picture turned with it, and reads back.
        var angle = Math.PI / 6;
        var rolled = new EyeCloseUp(null, new(100, 50, 400, 400), new(300, 250, 250, angle), TouchZonePictures.LightBackdrop);
        var (u, v) = (-0.2, -0.05);
        double px = 300 + 250 * (u * Math.Cos(angle) - v * Math.Sin(angle)), py = 250 + 250 * (u * Math.Sin(angle) + v * Math.Cos(angle));
        var (x, y) = rolled.InFace((px - 100) / 400, (py - 50) / 400);
        Near(u, x);
        Near(v, y);
        Near(0.16, rolled.Across(0.1));
    }

    [Fact]
    public void TheCloseUpIsASquareAboutOnePointSixFaceWidthsAroundTheFaceKeptInsideTheSnapshot()
    {
        var snapshot = Picture(400, 600);
        var close = CharacterEyes.CloseUp(snapshot, new(200, 150, 100))!;
        Assert.Equal(new PixelRect(120, 70, 160, 160), close.Area);
        Assert.Equal((768, 768), close.Size);
        var picture = close.Picture()!;
        Assert.Equal((768, 768), (picture.Width, picture.Height));
        // Near the top, the square moves down into the picture rather than losing its shape.
        Assert.Equal(new PixelRect(120, 0, 160, 160), CharacterEyes.CloseUp(snapshot, new(200, 40, 100))!.Area);
        Assert.Null(CharacterEyes.CloseUp(snapshot, new(200, 150, 8)));
        Assert.Null(CharacterEyes.CloseUp(snapshot, new(900, 150, 100)));

        // The probe's face (fractions of the page, its width a fraction of the page's width) in the snapshot's pixels.
        var face = EyeFace.InSnapshot(new RendererFace(0.5, 0.3, 0.1, 0.1, "mesh"), new(0.25, 0.1, 0.5, 0.8), 500, 800)!;
        Assert.Equal((250d, 200d, 100d, 0.1), (Math.Round(face.X, 6), Math.Round(face.Y, 6), Math.Round(face.Width, 6), face.Angle));
        Assert.Null(EyeFace.InSnapshot(null, new(0, 0, 1, 1), 500, 800));
        Assert.Null(EyeFace.InSnapshot(new RendererFace(0.5, 0.3, double.NaN), new(0, 0, 1, 1), 500, 800));
    }

    [Fact]
    public void AZoomedOutPicturesFaceReadsAsItWouldFramedWhole()
    {
        var (zoom, x, y) = (0.5, 0.2, 0.1);
        (double X, double Y) Page(double u, double v) => (((2 * u - 1) * zoom + x * 0.5 + 1) / 2, (1 - ((1 - 2 * v) * zoom + y)) / 2);
        var (fx, fy) = Page(0.5, 0.2);
        var probe = WholeFraming.Unframed(new RendererZoneProbe(Face: new RendererFace(fx, fy, 0.05, 0.2, "bones")), zoom, x, y, 0.5);
        Assert.Equal((0.5, 0.2, 0.1, 0.2, "bones"), (Math.Round(probe.Face!.X, 6), Math.Round(probe.Face.Y, 6), Math.Round(probe.Face.Width, 6),
            probe.Face.Angle, probe.Face.Tracking));
        Assert.Null(WholeFraming.Unframed(new RendererZoneProbe(), zoom, x, y, 0.5).Face);
    }

    [Fact]
    public void TheEyeHintAndTheRenderersAnswerAreChecked()
    {
        var eye = new RendererEye(new(-0.2, -0.02, 0.05), new(-0.2, -0.02, 0.1, 0.04));
        Assert.True(new RendererEyes(eye, eye with { Iris = eye.Iris with { X = 0.2 } }).IsValid);
        Assert.True(new RendererEyes().Clears);
        Assert.True(new RendererEyes(eye).Clears);
        Assert.False(new RendererEyes(eye with { Iris = eye.Iris with { R = 0 } }, eye).IsValid);
        Assert.False(new RendererEyes(eye with { Eye = eye.Eye with { X = 2.5 } }, eye).IsValid);
        Assert.False(new RendererEyes(eye with { Iris = eye.Iris with { Y = double.NaN } }, eye).IsValid);
        Assert.Equal("vision", RendererEyesFrom.Read("vision"));
        Assert.Null(RendererEyesFrom.Read("Vision!"));
        Assert.Null(RendererEyesFrom.Read(new string('a', 17)));
        // The page's message: camelCase with the contract's names.
        var json = JsonSerializer.Serialize(new RendererEyes(eye, eye), RendererProtocol.Json);
        Assert.Equal("{\"left\":{\"iris\":{\"x\":-0.2,\"y\":-0.02,\"r\":0.05},\"eye\":{\"x\":-0.2,\"y\":-0.02,\"rx\":0.1,\"ry\":0.04}}," +
            "\"right\":{\"iris\":{\"x\":-0.2,\"y\":-0.02,\"r\":0.05},\"eye\":{\"x\":-0.2,\"y\":-0.02,\"rx\":0.1,\"ry\":0.04}}}", json);
        Assert.False(new RendererFace(0.5, 0.5, 0.1, 0, "Mesh").IsValid);
        Assert.True(new RendererFace(0.5, 0.5, 0.1, 0, "mesh").IsValid);
    }

    // A fake vision model: answers each request in turn and keeps what it was asked.
    private sealed class Asker(params (string? Answer, string? Failure)[] answers)
    {
        private int next;
        internal List<EyeAsk> Asked { get; } = [];
        internal Task<(string? Answer, string? Failure)> AskAsync(EyeAsk ask, CancellationToken token)
        {
            Asked.Add(ask);
            return Task.FromResult(next < answers.Length ? answers[next++] : (null, "no more answers"));
        }
    }

    [Fact]
    public async Task AMeasurementAsksOnceMoreWithItsBoxesDrawnWhenTheFirstAnswerFailsTheChecks()
    {
        var snapshot = Picture(400, 600);
        var close = CharacterEyes.CloseUp(snapshot, new(200, 150, 100))!;
        var good = await CharacterEyes.RunAsync(close, new Asker((Answer, null)).AskAsync, null, default);
        Assert.Equal((1, null), (good.Requests, good.Failure));
        Assert.Equal(-0.2, good.Hint!.Left!.Iris.X);

        // The left iris on the wrong eye: a check with the four boxes drawn and numbered and the problem listed.
        var wrong = Answer.Replace("\"left\":{\"iris\":{\"left\":0.34375", "\"left\":{\"iris\":{\"left\":0.59375", StringComparison.Ordinal)
            .Replace("\"right\":0.40625", "\"right\":0.65625", StringComparison.Ordinal);
        var checking = new Asker((wrong, null), (Answer, null));
        var corrected = await CharacterEyes.RunAsync(close, checking.AskAsync, null, default);
        Assert.Equal(2, corrected.Requests);
        Assert.NotNull(corrected.Hint);
        var check = checking.Asked[1];
        Assert.Equal((EyeAskKind.Check, "check", 4), (check.Kind, check.Step, check.Marks.Count));
        Assert.Contains("lies mostly outside the left eye's opening", check.Text, StringComparison.Ordinal);
        Assert.Equal((768, 768), (check.Picture!.Width, check.Picture.Height));
        Assert.NotEqual(checking.Asked[0].Picture!.Bgra, check.Picture.Bgra);

        // An answer that can't be read: the question again, with why.
        var again = new Asker(("no eyes here", null), (Answer, null));
        Assert.NotNull((await CharacterEyes.RunAsync(close, again.AskAsync, null, default)).Hint);
        Assert.Equal(EyeAskKind.Again, again.Asked[1].Kind);
        Assert.Contains("the answer had no JSON", again.Asked[1].Text, StringComparison.Ordinal);

        // A request that fails isn't asked again, and two answers that fail the checks save nothing.
        var failed = await CharacterEyes.RunAsync(close, new Asker((null, "provider.timeout")).AskAsync, null, default);
        Assert.Equal((1, "Couldn't ask the Thinking model (provider.timeout).", null), (failed.Requests, failed.Failure, failed.Hint));
        var twice = await CharacterEyes.RunAsync(close, new Asker((wrong, null), (wrong, null)).AskAsync, null, default);
        Assert.Null(twice.Hint);
        Assert.StartsWith("The Thinking model's eyes didn't pass Martlet's checks: The left iris (box 1)", twice.Failure, StringComparison.Ordinal);
        Assert.Equal(2, twice.Steps.Count);
    }

    [Fact]
    public async Task MeasurementsAreSavedPerModelAndForgottenWithTheirPictures()
    {
        var directory = Directory.CreateTempSubdirectory("martlet-eyes-").FullName;
        try
        {
            var hint = CharacterEyes.Hint(Boxes(), EyeCloseUp.Standard());
            var measured = new CharacterEyeMeasurement
            {
                ModelId = "model-a", Left = hint.Left!, Right = hint.Right!, MeasuredAt = DateTimeOffset.Now, Requests = 1, Boxes = Boxes(),
                Pictures = [new("01-eyes.png", "eyes", 768, 768, 1000, "image/png")], Steps = ["eyes: read all four boxes"]
            };
            await CharacterEyes.SaveAsync(directory, measured);
            await CharacterEyes.SaveAsync(directory, measured with { ModelId = "model-b", By = CharacterEyeMeasurement.ByFixture });
            var folder = CharacterEyes.ClearPictures(directory, "model-a");
            await File.WriteAllBytesAsync(Path.Combine(folder, CharacterEyes.BoxesFile), [1]);

            var loaded = CharacterEyes.Load(directory, "model-a")!;
            Assert.Equal(hint, loaded.Hint);
            Assert.Equal((CharacterEyeMeasurement.ByVision, 1, "01-eyes.png"), (loaded.By, loaded.Requests, loaded.Pictures[0].File));
            Assert.Equal(Boxes(), loaded.Boxes);
            Assert.Equal(2, CharacterEyes.LoadAll(directory).Count);
            using (var saved = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, CharacterEyes.FileName))))
                Assert.Equal(0.05, saved.RootElement.GetProperty("models")[0].GetProperty("left").GetProperty("iris").GetProperty("r").GetDouble());

            Assert.True(await CharacterEyes.ForgetAsync(directory, "model-a"));
            Assert.Null(CharacterEyes.Load(directory, "model-a"));
            Assert.False(Directory.Exists(folder));
            Assert.NotNull(CharacterEyes.Load(directory, "model-b"));
            Assert.False(await CharacterEyes.ForgetAsync(directory, "model-a"));
            await Assert.ThrowsAsync<ContractException>(() => CharacterEyes.SaveAsync(directory,
                measured with { Left = hint.Left! with { Iris = hint.Left.Iris with { R = double.NaN } } }));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void TheStatusSaysWhereTheEyesComeFrom()
    {
        var now = DateTimeOffset.Now;
        var hint = CharacterEyes.Hint(Boxes(), EyeCloseUp.Standard());
        var saved = new CharacterEyeMeasurement { ModelId = "m", Left = hint.Left!, Right = hint.Right!, MeasuredAt = now };
        Assert.Equal($"Measured with vision at {now.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)}.", CharacterEyes.Status("vision", saved, true, now));
        Assert.Equal("From the model's own meshes.", CharacterEyes.Status("mesh", null, true, now));
        Assert.StartsWith("From the model's own eye bones", CharacterEyes.Status("bones", null, true, now), StringComparison.Ordinal);
        Assert.EndsWith("Measure the eyes to fit them.", CharacterEyes.Status("estimate", null, true, now), StringComparison.Ordinal);
        Assert.EndsWith("The character uses it when it shows.", CharacterEyes.Status(null, saved, false, now), StringComparison.Ordinal);
        Assert.StartsWith("FIXTURE - NOT AI", CharacterEyes.Status("vision", saved with { By = CharacterEyeMeasurement.ByFixture }, true, now), StringComparison.Ordinal);
        Assert.Contains(" on ", CharacterEyes.Measured(saved with { MeasuredAt = now.AddDays(-2) }, now), StringComparison.Ordinal);
    }

    // A still renderer for the eyes' picture: answers with a snapshot whose probe has the face.
    private sealed class StillRenderer(byte[] png, RendererZoneProbe probe) : IAvatarRenderer
    {
        public RendererCapabilities? Capabilities => null;
        public bool HasExited { get; private set; }
        public Task Exited => Task.CompletedTask;
        public event Action<string>? Requested { add { } remove { } }
        public Task StartAsync(AvatarProfile profile, string revision, RendererPlacement? placement, bool voiceMuted, CancellationToken token) =>
            Task.CompletedTask;
        public Task<RendererMessage> SendAsync<T>(string kind, T data, CancellationToken token, TimeSpan? timeout = null) =>
            Task.FromResult(RendererProtocol.Message("picture", Guid.Empty, new RendererPicture(Convert.ToBase64String(png), 400, 600, Probe: probe)));
        public ValueTask DisposeAsync() { HasExited = true; return ValueTask.CompletedTask; }
    }

    [Fact]
    public async Task MeasuringTheEyesTakesTheRestPosePictureAsksOnceAndSavesTheHintWithThePicturesSent()
    {
        using var scope = new AvatarHostingTests.Scope();
        var png = TouchZoneImages.Encode(Picture(400, 600)).Content.ToArray();
        await using var avatar = new AvatarController(createRenderer: () => throw new InvalidOperationException("the character must not show"),
            createStillRenderer: () => new StillRenderer(png, new(Face: new RendererFace(0.5, 0.25, 0.25, 0, "estimate"))));
        var service = new CharacterEyeService(scope.DirectoryPath);
        service.Follow("model-1");
        var asked = new ConcurrentQueue<(string Purpose, BoundedImage? Image)>();
        var said = await service.MeasureAsync(avatar, scope.Profile(), (purpose, instructions, text, image, token) =>
        {
            asked.Enqueue((purpose, image));
            Assert.Equal(CharacterEyes.Instructions, instructions);
            return Task.FromResult<(string?, string?)>((Answer, null));
        }, automatically: true, default);

        Assert.StartsWith("Measured with vision at", said, StringComparison.Ordinal);
        var (purpose, image) = Assert.Single(asked);
        Assert.Equal(("Measuring the eyes", 768, 768), (purpose, image!.Width, image.Height));
        Assert.Equal(-0.2, service.Current!.Left.Iris.X);
        Assert.Equal(service.Current.Hint, service.HintFor("model-1"));
        Assert.Null(service.HintFor("model-2"));
        Assert.Equal(CharacterEyes.Load(scope.DirectoryPath, "model-1")!.Hint, service.Current.Hint);
        var folder = CharacterEyes.PictureFolder(scope.DirectoryPath, "model-1");
        Assert.True(File.Exists(Path.Combine(folder, "01-eyes.png")));
        Assert.Equal(service.BoxesPicture, Path.Combine(folder, CharacterEyes.BoxesFile));
        Assert.False(service.ClaimAutomatic(), "a measured model isn't measured again on its own");

        Assert.StartsWith("Forgot the measurement.", await service.ForgetAsync(default), StringComparison.Ordinal);
        Assert.Null(service.Current);
        Assert.False(service.ClaimAutomatic(), "nor right after it was forgotten");
        service.Follow("model-2");
        Assert.True(service.ClaimAutomatic());
        Assert.False(service.ClaimAutomatic());
    }

    [Fact]
    public async Task AfterEachModelLoadTheRendererGetsTheModelsHintAndSaysWhatTheEyesUse()
    {
        using var scope = new AvatarHostingTests.Scope();
        var renderer = new EyesRenderer();
        await using var avatar = new AvatarController(createRenderer: () => renderer, allowControlledClock: true);
        var hint = CharacterEyes.Hint(Boxes(), EyeCloseUp.Standard());
        RendererEyes? saved = null;
        avatar.UseEyes(path => path == scope.Model ? saved : null);
        var changed = 0;
        avatar.EyesChanged += () => Interlocked.Increment(ref changed);

        await avatar.ShowAsync(scope.Profile() with { LipSync = AvatarLipSync.Loudness }, default);
        var first = Assert.Single(renderer.Eyes);
        Assert.True(first.Clears);
        Assert.Equal(RendererEyesFrom.Estimate, avatar.EyesFrom);
        Assert.True(avatar.HasEyes(null));

        Assert.Equal(RendererEyesFrom.Vision, await avatar.SendEyesAsync(hint, default));
        Assert.Equal(RendererEyesFrom.Vision, avatar.EyesFrom);
        Assert.True(avatar.HasEyes(hint));
        Assert.Equal(RendererEyesFrom.Vision, await avatar.SendEyesAsync(hint, default));
        Assert.Equal(2, renderer.Eyes.Count);
        Assert.Equal(hint, renderer.Eyes.Last());
        Assert.Equal(2, changed);

        // A renderer that can't take the hint never fails the character: its eyes are only unknown.
        renderer.Fail = true;
        Assert.Null(await avatar.SendEyesAsync(null, default));
        Assert.Null(avatar.EyesFrom);
        Assert.True(avatar.IsShowing);
    }

    private sealed class EyesRenderer : IAvatarRenderer
    {
        internal ConcurrentQueue<RendererEyes> Eyes { get; } = new();
        internal bool Fail;
        public RendererCapabilities? Capabilities { get; private set; }
        public bool HasExited { get; private set; }
        private readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Exited => exited.Task;
        public event Action<string>? Requested { add { } remove { } }
        private readonly Guid activation = Guid.NewGuid();
        public Task StartAsync(AvatarProfile profile, string revision, RendererPlacement? placement, bool voiceMuted, CancellationToken token)
        {
            Capabilities = new(revision.ToLowerInvariant(), [new("Jaw", -10, 10, 0, ["Mouth"])]);
            return Task.CompletedTask;
        }
        public Task<RendererMessage> SendAsync<T>(string kind, T data, CancellationToken token, TimeSpan? timeout = null)
        {
            if (kind != "eyes") return Task.FromResult(RendererProtocol.Message(kind == "gaze" ? "look" : "ok", activation,
                kind == "gaze" ? new RendererLook("mouse", 0, 0) : (object)new { }));
            if (Fail) throw new InvalidDataException("The character renderer couldn't apply those controls.");
            var eyes = Assert.IsType<RendererEyes>(data);
            Eyes.Enqueue(eyes);
            return Task.FromResult(RendererProtocol.Message("eyes", activation, new RendererEyesFrom(eyes.Clears ? "estimate" : "vision")));
        }
        public ValueTask DisposeAsync() { HasExited = true; exited.TrySetResult(); return ValueTask.CompletedTask; }
    }

    [Fact]
    public async Task TheMcpRehearsalRunsTheProductionParserChecksAndConversionAndSaves()
    {
        var directory = Directory.CreateTempSubdirectory("martlet-eyes-mcp-").FullName;
        try
        {
            var wrong = Answer.Replace("\"right\":{\"iris\":{\"left\":0.59375", "\"right\":{\"iris\":{\"left\":0.20", StringComparison.Ordinal);
            var result = JsonSerializer.SerializeToElement(await Martlet.Mcp.CharacterEyesCheck.RunAsync(directory, true, null, "model-x", wrong, Answer,
                null, null, null, save: true, forget: false, eyesFrom: "vision", default));
            var measurement = result.GetProperty("measurement");
            Assert.Equal(2, measurement.GetProperty("requestCount").GetInt32());
            Assert.Equal(-0.2, measurement.GetProperty("hint").GetProperty("left").GetProperty("iris").GetProperty("x").GetDouble());
            Assert.StartsWith("FIXTURE - NOT AI", result.GetProperty("status").GetString(), StringComparison.Ordinal);
            Assert.Equal(CharacterEyeMeasurement.ByFixture, CharacterEyes.Load(directory, "model-x")!.By);
            Assert.Equal(768, result.GetProperty("closeUp").GetProperty("width").GetInt32());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    // An opaque picture: a light face with two dark eyes, so the close-up has something to show.
    private static ZonePixels Picture(int width, int height)
    {
        var bgra = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var eye = y is > 140 and < 160 && (x is > 160 and < 185 || x is > 215 and < 240);
                (bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]) = eye ? ((byte)90, (byte)40, (byte)30, (byte)255) : ((byte)200, (byte)215, (byte)240, (byte)255);
            }
        return new(width, height, bgra);
    }
}
