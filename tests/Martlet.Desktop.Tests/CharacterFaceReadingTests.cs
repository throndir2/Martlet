using System.Text.Json;
using Martlet.Avatar.RendererHost;
using Martlet.Mcp;

namespace Martlet.Desktop.Tests;

public sealed class CharacterFaceReadingTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void TheFaceReadingIsKeptBoundedAndTyped()
    {
        var reading = Json(CharacterFaceReading.From(Json("""
            {"id":4,"found":true,"tracking":"mesh","x":0.51234567,"y":0.2,"width":0.16,"tilt":-3.5,
             "cheekLeft":{"x":0.46,"y":0.23,"visible":1,"across":1.2,"hit":true,"drawables":["D_FACE","D_HAIR","D_EAR","D_NECK"],
               "bone":null,"mesh":null},
             "cheekRight":{"x":"far","y":0.23,"visible":0.4,"across":0.6,"hit":false,"drawables":[],"bone":"bad\u0001name"},
             "eyeLeft":{"x":0.4812345,"y":0.19},"eyeRight":{"x":0.53,"y":0.19},"mouth":{"x":0.51,"y":"low"},"top":null,
             "overlays":["blush",7,""],"pinned":{"carriers":118,"milliseconds":12},"extra":"dropped"}
            """), 4));
        Assert.Equal(4, reading.GetProperty("n").GetInt32());
        Assert.True(reading.GetProperty("found").GetBoolean());
        Assert.Equal("mesh", reading.GetProperty("tracking").GetString());
        Assert.Equal(0.5123, reading.GetProperty("x").GetDouble());
        Assert.Equal(-3.5, reading.GetProperty("tilt").GetDouble());
        var left = reading.GetProperty("cheekLeft");
        Assert.True(left.GetProperty("hit").GetBoolean());
        Assert.Equal(["D_FACE", "D_HAIR", "D_EAR"], left.GetProperty("drawables").EnumerateArray().Select(d => d.GetString()));
        var right = reading.GetProperty("cheekRight");
        Assert.False(right.TryGetProperty("x", out _));
        Assert.Equal(JsonValueKind.Null, right.GetProperty("bone").ValueKind);
        Assert.Equal(0.6, right.GetProperty("across").GetDouble());
        Assert.Equal(["blush"], reading.GetProperty("overlays").EnumerateArray().Select(o => o.GetString()));
        Assert.Equal(0.4812, reading.GetProperty("eyeLeft").GetProperty("x").GetDouble());
        Assert.Equal(0.19, reading.GetProperty("eyeRight").GetProperty("y").GetDouble());
        Assert.False(reading.TryGetProperty("mouth", out _), "a point without both numbers is left out");
        Assert.False(reading.TryGetProperty("top", out _));
        Assert.Equal(118, reading.GetProperty("pinned").GetProperty("carriers").GetInt32());
        Assert.False(reading.TryGetProperty("extra", out _));

        var unknown = Json(CharacterFaceReading.From(Json("""{"id":5,"found":false,"tracking":"guess"}"""), 5));
        Assert.False(unknown.GetProperty("found").GetBoolean());
        Assert.False(unknown.TryGetProperty("tracking", out _));
    }

    [Fact]
    public void TheFaceReadingKeepsEachEyesIrisAndOpeningBoundedAndTyped()
    {
        var reading = Json(CharacterFaceReading.From(Json("""
            {"id":6,"found":true,"tracking":"mesh","eyesFrom":"mesh",
             "irisLeft":{"x":0.47,"y":0.2,"rx":0.012345678,"ry":"wide"},"irisRight":null,
             "eyeLeftShape":{"points":28,"triangles":40,"left":0.45,"top":0.18,"right":0.49,"bottom":0.22,"irisInside":true,"extra":1},
             "eyeRightShape":{"points":99999,"triangles":null,"irisInside":"yes"},
             "pinned":{"carriers":291,"milliseconds":17,"eyeMilliseconds":8}}
            """), 6));
        Assert.Equal("mesh", reading.GetProperty("eyesFrom").GetString());
        var iris = reading.GetProperty("irisLeft");
        Assert.Equal(0.0123, iris.GetProperty("rx").GetDouble());
        Assert.False(iris.TryGetProperty("ry", out _));
        Assert.Equal(JsonValueKind.Null, reading.GetProperty("irisRight").ValueKind);
        var left = reading.GetProperty("eyeLeftShape");
        Assert.Equal(28, left.GetProperty("points").GetInt32());
        Assert.Equal(40, left.GetProperty("triangles").GetInt32());
        Assert.Equal(0.22, left.GetProperty("bottom").GetDouble());
        Assert.True(left.GetProperty("irisInside").GetBoolean());
        Assert.False(left.TryGetProperty("extra", out _));
        var right = reading.GetProperty("eyeRightShape");
        Assert.Equal(CharacterFaceReading.MaximumShapePoints, right.GetProperty("points").GetInt32());
        Assert.Equal(JsonValueKind.Null, right.GetProperty("triangles").ValueKind);
        Assert.Equal(JsonValueKind.Null, right.GetProperty("irisInside").ValueKind);
        Assert.Equal(8, reading.GetProperty("pinned").GetProperty("eyeMilliseconds").GetInt32());
        var guess = Json(CharacterFaceReading.From(Json("""{"id":7,"found":true,"eyesFrom":"guess"}"""), 7));
        Assert.False(guess.TryGetProperty("eyesFrom", out _));
    }

    [Fact]
    public void TheOverlaySurfaceReadsTheLastFaceWithTheLastTap()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var viewport = new CharacterViewport { LastTouch = """{"n":1,"hit":true}""" };
                Assert.Equal("""{"n":1,"hit":true}""", viewport.Reading());
                viewport.LastFace = """{"n":2,"found":true}""";
                var reading = Json(viewport.Reading());
                Assert.True(reading.GetProperty("hit").GetBoolean());
                Assert.Equal(2, reading.GetProperty("face").GetProperty("n").GetInt32());
                viewport.LastPicture = """{"n":3,"path":"C:\\Temp\\overlay-1.png","width":420,"height":560}""";
                var pictured = Json(viewport.Reading());
                Assert.Equal(3, pictured.GetProperty("picture").GetProperty("n").GetInt32());
                Assert.Equal(2, pictured.GetProperty("face").GetProperty("n").GetInt32());
                Assert.Equal(3, Json(new CharacterViewport { LastPicture = """{"n":3}""" }.Reading()).GetProperty("picture").GetProperty("n").GetInt32());
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    [Fact]
    public async Task APictureIsSavedOnlyToAFullPngPath()
    {
        var automation = new DesktopAutomation(false);
        await Assert.ThrowsAsync<ArgumentException>(() => automation.PictureCharacterAsync("face.png"));
        await Assert.ThrowsAsync<ArgumentException>(() => automation.PictureCharacterAsync(Path.Combine(Path.GetTempPath(), "face.txt")));
    }

    [Fact]
    public void TheFaceSummarySaysHowFarTheFaceMovedAndWhatIsUnderEachCheek()
    {
        static string N(double value) => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        static JsonElement Face(double x, double tilt, string under, double visible) => Json($$"""
            {"n":1,"found":true,"tracking":"mesh","x":{{N(x)}},"y":0.2,"width":0.16,"tilt":{{N(tilt)}},
             "cheekLeft":{"x":{{N(x - 0.04)}},"y":0.23,"visible":1,"across":1.1,"hit":true,"drawables":["{{under}}"]},
             "cheekRight":{"x":{{N(x + 0.04)}},"y":0.23,"visible":{{N(visible)}},"across":0.8,"hit":false,"drawables":[]} }
            """);
        var summary = Json(JsonSerializer.Serialize(DesktopAutomation.FaceSummary(
            [Face(0.5, 0, "D_FACE", 1), Face(0.52, 6, "D_FACE", 0.7), Face(0.55, -2, "D_HAIR", 0.5), Json("""{"n":4,"found":false}""")])));
        Assert.Equal(3, summary.GetProperty("found").GetInt32());
        Assert.Equal(["mesh"], summary.GetProperty("tracking").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal(0.05, summary.GetProperty("moved").GetProperty("x").GetDouble());
        Assert.Equal(8, summary.GetProperty("moved").GetProperty("tilt").GetDouble());
        var left = summary.GetProperty("cheekLeft");
        Assert.Equal(1, left.GetProperty("onCharacter").GetDouble());
        Assert.Equal("D_FACE", left.GetProperty("mostlyOver").GetString());
        Assert.Equal(0.67, left.GetProperty("mostlyOverShare").GetDouble());
        var right = summary.GetProperty("cheekRight");
        Assert.Equal(0, right.GetProperty("onCharacter").GetDouble());
        Assert.Equal(0.5, right.GetProperty("visibleLeast").GetDouble());
    }

    [Fact]
    public void TheFaceSummarySaysWhereTheEyesCameFromHowEachIrisMovedAndHowFarEachEyeClosed()
    {
        static string N(double value) => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        static JsonElement Face(double irisX, double top, int points, bool inside) => Json($$"""
            {"n":1,"found":true,"tracking":"mesh","eyesFrom":"mesh","x":0.5,"y":0.2,"width":0.16,"tilt":0,
             "irisLeft":{"x":{{N(irisX)}},"y":0.2,"rx":0.01,"ry":0.012},"irisRight":null,
             "eyeLeftShape":{"points":{{points}},"triangles":40,"left":0.45,"top":{{N(top)}},"right":0.49,"bottom":0.22,"irisInside":{{(inside ? "true" : "false")}}},
             "eyeRightShape":null}
            """);
        var summary = Json(JsonSerializer.Serialize(DesktopAutomation.FaceSummary(
            [Face(0.47, 0.18, 28, true), Face(0.48, 0.21, 28, true), Face(0.475, 0.219, 2, false), Face(0.49, 0.18, 0, false)])));
        Assert.Equal(["mesh"], summary.GetProperty("eyesFrom").EnumerateArray().Select(e => e.GetString()));
        var left = summary.GetProperty("eyeLeft");
        Assert.Equal(1, left.GetProperty("iris").GetDouble());
        Assert.Equal(0.02, left.GetProperty("irisMoved").GetProperty("x").GetDouble());
        Assert.Equal(1, left.GetProperty("irisInside").GetDouble());
        Assert.Equal(0.001, left.GetProperty("opening").GetProperty("least").GetDouble());
        Assert.Equal(0.04, left.GetProperty("opening").GetProperty("most").GetDouble());
        Assert.Equal(0.25, left.GetProperty("closed").GetDouble());
        var right = summary.GetProperty("eyeRight");
        Assert.Equal(0, right.GetProperty("iris").GetDouble());
        Assert.Equal(JsonValueKind.Null, right.GetProperty("irisInside").ValueKind);
        Assert.Equal(JsonValueKind.Null, right.GetProperty("opening").ValueKind);
    }
}
