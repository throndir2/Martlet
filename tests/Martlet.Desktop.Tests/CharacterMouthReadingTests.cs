using System.Text.Json;
using Martlet.Avatar.RendererHost;
using Martlet.Mcp;

namespace Martlet.Desktop.Tests;

public sealed class CharacterMouthReadingTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void TheMouthReadingIsKeptBoundedAndTyped()
    {
        var reading = Json(CharacterMouthReading.From(Json("""
            {"id":7,"found":true,"renderer":"Live2D","parameter":"ParamMouthOpenY","voice":0.987654,"speaking":true,"level":1.5,
             "emote":0.6,"open":-0.2,"blocked":"lots","extra":"dropped"}
            """), 7));
        Assert.Equal(7, reading.GetProperty("n").GetInt32());
        Assert.True(reading.GetProperty("found").GetBoolean());
        Assert.Equal("Live2D", reading.GetProperty("renderer").GetString());
        Assert.Equal("ParamMouthOpenY", reading.GetProperty("parameter").GetString());
        Assert.Equal(0.9877, reading.GetProperty("voice").GetDouble());
        Assert.True(reading.GetProperty("speaking").GetBoolean());
        Assert.Equal(1, reading.GetProperty("level").GetDouble());
        Assert.Equal(0.6, reading.GetProperty("emote").GetDouble());
        Assert.Equal(0, reading.GetProperty("open").GetDouble());
        Assert.False(reading.TryGetProperty("blocked", out _));
        Assert.False(reading.TryGetProperty("extra", out _));

        var vrm = Json(CharacterMouthReading.From(Json("""{"id":8,"found":true,"renderer":"Vrm","parameter":"<script>","blocked":1}"""), 8));
        Assert.False(vrm.TryGetProperty("parameter", out _));
        Assert.Equal(0, vrm.GetProperty("voice").GetDouble());
        Assert.False(vrm.GetProperty("speaking").GetBoolean());
        Assert.Equal(1, vrm.GetProperty("blocked").GetDouble());

        var missing = Json(CharacterMouthReading.From(Json("""{"id":9,"found":false,"renderer":"Live2D","voice":1}"""), 9));
        Assert.False(missing.GetProperty("found").GetBoolean());
        Assert.False(missing.TryGetProperty("voice", out _));
        Assert.False(Json(CharacterMouthReading.From(Json("""[1]"""), 10)).GetProperty("found").GetBoolean());
    }

    [Fact]
    public void TheOverlaySurfaceReadsTheMouthAndPlaysVoiceLevels()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var viewport = new CharacterViewport();
                viewport.LastMouth = """{"n":2,"found":true}""";
                Assert.Equal(2, Json(viewport.Reading()).GetProperty("mouth").GetProperty("n").GetInt32());

                var (levels, stepMs) = CharacterViewport.VoiceLevels("voice:40;0;0.5;1;0.25");
                Assert.Equal(40, stepMs);
                Assert.Equal(new double[] { 0, 0.5, 1, 0.25 }, levels);
                foreach (var bad in new[] { "voice:40", "voice:5;0.5", "voice:2000;0.5", "voice:40;1.5", "voice:40;-0.1", "voice:40;loud",
                    "voice:" + string.Join(";", Enumerable.Repeat("0.5", CharacterViewport.MaximumVoiceLevels + 2)) })
                    Assert.Throws<ArgumentException>(() => CharacterViewport.VoiceLevels(bad));
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    [Fact]
    public void TheMouthSummarySaysWhenTheVoiceHadTheMouthAndWhereItEnded()
    {
        static string N(double value) => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        static JsonElement Mouth(bool speaking, double voice, double open) => Json($$"""
            {"n":1,"found":true,"renderer":"Live2D","parameter":"ParamMouthOpenY","voice":{{N(voice)}},"speaking":{{(speaking ? "true" : "false")}},
             "level":0,"emote":0.6,"open":{{N(open)}} }
            """);
        var summary = Json(JsonSerializer.Serialize(DesktopAutomation.MouthSummary(
            [Mouth(false, 0, 0.6), Mouth(true, 1, 0.1), Mouth(true, 1, 0), Mouth(false, 0, 0.6), Json("""{"n":5,"found":false}""")])));
        Assert.Equal(4, summary.GetProperty("found").GetInt32());
        Assert.Equal(0.5, summary.GetProperty("speaking").GetDouble());
        Assert.Equal(0, summary.GetProperty("voice").GetProperty("least").GetDouble());
        Assert.Equal(1, summary.GetProperty("voice").GetProperty("most").GetDouble());
        Assert.Equal(0, summary.GetProperty("open").GetProperty("least").GetDouble());
        Assert.Equal(0.6, summary.GetProperty("emote").GetProperty("most").GetDouble());
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("blocked").ValueKind);
        Assert.Equal(0.6, summary.GetProperty("last").GetProperty("open").GetDouble());

        var none = Json(JsonSerializer.Serialize(DesktopAutomation.MouthSummary([Json("""{"n":1,"found":false}""")])));
        Assert.Equal(0, none.GetProperty("found").GetInt32());
        Assert.Equal(JsonValueKind.Null, none.GetProperty("last").ValueKind);
    }
}
