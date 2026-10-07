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
        Assert.Equal(118, reading.GetProperty("pinned").GetProperty("carriers").GetInt32());
        Assert.False(reading.TryGetProperty("extra", out _));

        var unknown = Json(CharacterFaceReading.From(Json("""{"id":5,"found":false,"tracking":"guess"}"""), 5));
        Assert.False(unknown.GetProperty("found").GetBoolean());
        Assert.False(unknown.TryGetProperty("tracking", out _));
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
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
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
}
