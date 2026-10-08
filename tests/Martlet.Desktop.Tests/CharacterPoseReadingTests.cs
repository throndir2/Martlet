using System.Text.Json;
using Martlet.Avatar.RendererHost;
using Martlet.Mcp;

namespace Martlet.Desktop.Tests;

public sealed class CharacterPoseReadingTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void ThePoseReadingIsKeptBoundedAndTyped()
    {
        var reading = Json(CharacterPoseReading.From(Json("""
            {"id":4,"found":true,"renderer":"Vrm","idle":true,"breathing":{"phase":0.123456,"inhale":0.5,"perMinute":14.3,"extra":1},
             "arms":{"left":{"fromDown":15.21234,"elbow":17.2},"right":{"fromDown":"far","elbow":17.2},"middle":{"fromDown":1}},
             "curl":{"left":60.2,"right":"curled"},"sway":-0.4,
             "bones":{"leftHand":{"x":0.61234567,"y":0.5},"rightHand":{"x":0.4},"leftIndexProximal":{"x":0.6,"y":0.5},"head":"top"},
             "extra":"dropped"}
            """), 4));
        Assert.Equal(4, reading.GetProperty("n").GetInt32());
        Assert.True(reading.GetProperty("found").GetBoolean());
        Assert.Equal("Vrm", reading.GetProperty("renderer").GetString());
        Assert.True(reading.GetProperty("idle").GetBoolean());
        var breathing = reading.GetProperty("breathing");
        Assert.Equal(0.1235, breathing.GetProperty("phase").GetDouble());
        Assert.Equal(14.3, breathing.GetProperty("perMinute").GetDouble());
        Assert.False(breathing.TryGetProperty("extra", out _));
        Assert.Equal(15.2123, reading.GetProperty("arms").GetProperty("left").GetProperty("fromDown").GetDouble());
        Assert.False(reading.GetProperty("arms").GetProperty("right").TryGetProperty("fromDown", out _));
        Assert.False(reading.GetProperty("arms").TryGetProperty("middle", out _));
        Assert.Equal(60.2, reading.GetProperty("curl").GetProperty("left").GetDouble());
        Assert.False(reading.GetProperty("curl").TryGetProperty("right", out _));
        Assert.Equal(-0.4, reading.GetProperty("sway").GetDouble());
        var bones = reading.GetProperty("bones");
        Assert.Equal(["leftHand"], bones.EnumerateObject().Select(b => b.Name));
        Assert.Equal(0.6123, bones.GetProperty("leftHand").GetProperty("x").GetDouble());
        Assert.False(reading.TryGetProperty("extra", out _));

        var live2D = Json(CharacterPoseReading.From(Json("""{"id":5,"found":false,"renderer":"Live2D","idle":true}"""), 5));
        Assert.False(live2D.GetProperty("found").GetBoolean());
        Assert.Equal("Live2D", live2D.GetProperty("renderer").GetString());
        Assert.False(live2D.TryGetProperty("idle", out _));
        Assert.False(Json(CharacterPoseReading.From(Json("""[1]"""), 6)).GetProperty("found").GetBoolean());
    }

    [Fact]
    public void TheOverlaySurfaceReadsTheLastPoseWithTheLastTap()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var viewport = new CharacterViewport();
                viewport.LastPose = """{"n":3,"found":true}""";
                Assert.Equal(3, Json(viewport.Reading()).GetProperty("pose").GetProperty("n").GetInt32());
                viewport.LastTouch = """{"n":1,"hit":true}""";
                var reading = Json(viewport.Reading());
                Assert.True(reading.GetProperty("hit").GetBoolean());
                Assert.True(reading.GetProperty("pose").GetProperty("found").GetBoolean());
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    [Fact]
    public void ThePoseSummarySaysHowFullTheBreathGotHowTheArmsHangAndWhatMoved()
    {
        static string N(double value) => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        static JsonElement Pose(double inhale, double shoulderY, double sway) => Json($$"""
            {"n":1,"found":true,"renderer":"Vrm","idle":true,"breathing":{"phase":0.2,"inhale":{{N(inhale)}},"perMinute":14},
             "arms":{"left":{"fromDown":15,"elbow":17},"right":{"fromDown":16,"elbow":17} },"curl":{"left":60,"right":60},
             "sway":{{N(sway)}},"bones":{"leftShoulder":{"x":0.55,"y":{{N(shoulderY)}} },"head":{"x":0.5,"y":0.12} } }
            """);
        var summary = Json(JsonSerializer.Serialize(DesktopAutomation.PoseSummary(
            [Pose(0, 0.301, 0.2), Pose(0.98, 0.298, -0.3), Pose(0.4, 0.3, 0), Json("""{"n":4,"found":false,"renderer":"Live2D"}""")])));
        Assert.Equal(3, summary.GetProperty("found").GetInt32());
        Assert.True(summary.GetProperty("idle").GetBoolean());
        Assert.Equal(0, summary.GetProperty("inhale").GetProperty("least").GetDouble());
        Assert.Equal(0.98, summary.GetProperty("inhale").GetProperty("most").GetDouble());
        Assert.Equal(16, summary.GetProperty("arms").GetProperty("right").GetProperty("fromDown").GetProperty("most").GetDouble());
        Assert.Equal(60, summary.GetProperty("curl").GetProperty("left").GetProperty("least").GetDouble());
        Assert.Equal(-0.3, summary.GetProperty("sway").GetProperty("least").GetDouble());
        Assert.Equal(0.003, summary.GetProperty("moved").GetProperty("leftShoulder").GetProperty("y").GetDouble());
        Assert.Equal(0, summary.GetProperty("moved").GetProperty("head").GetProperty("x").GetDouble());

        var none = Json(JsonSerializer.Serialize(DesktopAutomation.PoseSummary([Json("""{"n":1,"found":false}""")])));
        Assert.Equal(0, none.GetProperty("found").GetInt32());
        Assert.False(none.GetProperty("idle").GetBoolean());
        Assert.Equal(JsonValueKind.Null, none.GetProperty("inhale").ValueKind);
    }
}
