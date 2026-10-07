using System.Text.Json;
using Martlet.Core.Reading;
using Martlet.Core.Settings;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

public sealed class ScreenReadingTests
{
    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public void Advance(TimeSpan time) => ticks += time.Ticks;
    }

    private sealed class FakeReader(params string[][] reads) : IScreenTextReader
    {
        private int next;
        public int Calls => next;
        public TaskCompletionSource? Gate { get; set; }
        public string Engine => "the test reader";

        public async Task<IReadOnlyList<ReadLine>> ReadAsync(byte[] bgra, int width, int height, CancellationToken token)
        {
            var lines = reads[Math.Min(next++, reads.Length - 1)];
            if (Gate is { } gate) await gate.Task;
            return [.. lines.Select((text, i) => new ReadLine(text, 10, 10 + i * 30, 200, 20))];
        }

        public void Dispose() { }
    }

    private static ScreenFrame Frame(double change) => new(new byte[8 * 4 * 4], 8, 4, "game", change);

    [Fact]
    public void New_text_on_screen_makes_a_look_more_likely_even_when_the_picture_barely_changed()
    {
        var clock = new Clock();
        var pacer = new ScreenCommentaryPacer(Chattiness.Normal, clock, () => 0.3);
        clock.Advance(ScreenCommentaryPacer.Warmup);
        pacer.NoteLook(commented: false);
        clock.Advance(pacer.Settings.AfterLook);
        pacer.ObserveFrame(0.001);
        Assert.Equal(PacerVerdict.NothingNew, pacer.Decide(false, TimeSpan.Zero));
        pacer.ObserveText(1.0);
        Assert.True(pacer.Novelty >= 0.5);
        Assert.Equal(PacerVerdict.Look, pacer.Decide(false, TimeSpan.Zero));
    }

    [Fact]
    public async Task Reader_reads_one_screenshot_at_a_time_and_skips_a_still_screen_until_a_while_passed()
    {
        var clock = new Clock();
        var fake = new FakeReader(["HEALTH 87"], ["HEALTH 87"], ["VICTORY"]) { Gate = new() };
        using var reader = new ScreenReader(fake, clock);

        var first = reader.Offer(Frame(1.0));
        Assert.NotNull(first);
        Assert.Null(reader.Offer(Frame(1.0)));
        fake.Gate.SetResult();
        var read = await first!;
        Assert.Equal("HEALTH 87", read!.Text);
        Assert.Equal(0, read.Change);

        // The same picture again soon: not read; after a while, or once it changed, it is.
        Assert.Null(reader.Offer(Frame(0.0)));
        clock.Advance(ScreenReader.Refresh);
        Assert.Equal(0, (await reader.Offer(Frame(0.0))!)!.Change);
        var changed = await reader.Offer(Frame(0.5))!;
        Assert.Equal("VICTORY", changed!.Text);
        Assert.Equal(1, changed.Change);
        Assert.Same(changed, reader.Latest);
        Assert.Equal(3, fake.Calls);
        Assert.Contains("Read 1 line with the test reader", changed.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Read_text_goes_last_in_a_glance_and_only_when_there_is_some()
    {
        var glance = "(Screen glance. Active window: \"game\". Reply [pass] or one short remark.)";
        Assert.Equal(glance, LiveConversationController.GlanceMessage(glance, LiveConversationController.ReadOnScreen(null, null)));
        Assert.Null(LiveConversationController.ReadOnScreen(null, "  "));

        var read = LiveConversationController.ReadOnScreen(null, "HEALTH 87 / 100\nVICTORY");
        var message = LiveConversationController.GlanceMessage(glance, read);
        Assert.StartsWith(glance + "\n\n", message, StringComparison.Ordinal);
        Assert.EndsWith("HEALTH 87 / 100\nVICTORY", message, StringComparison.Ordinal);

        // Emptied on Companion › Prompts: nothing is added.
        var prompts = new PromptSettings { Overrides = new Dictionary<string, string> { [PromptCatalog.ReadOnScreen] = "" } };
        Assert.Null(LiveConversationController.ReadOnScreen(prompts, "VICTORY"));
    }

    [Fact]
    public void A_reading_roles_answer_becomes_lines_and_its_errors_are_reported()
    {
        using var answer = JsonDocument.Parse(
            """{"lines":[{"text":"Score: 12,450","score":0.99,"box":[699,41,180,30]},{"text":"VICTORY","score":0.98,"box":[383,269,240,60]}],"width":1024,"height":576,"milliseconds":640}""");
        var lines = HostScreenTextReader.Lines(answer.RootElement);
        Assert.Equal([new ReadLine("Score: 12,450", 699, 41, 180, 30), new ReadLine("VICTORY", 383, 269, 240, 60)], lines);

        using var failed = JsonDocument.Parse("""{"error":{"code":"request.invalid","summary":"not a picture"}}""");
        Assert.Contains("not a picture", Assert.Throws<ScreenReadException>(() => HostScreenTextReader.Lines(failed.RootElement)).Message);
        using var invalid = JsonDocument.Parse("""{"lines":[{"text":"x"}]}""");
        Assert.Throws<ScreenReadException>(() => HostScreenTextReader.Lines(invalid.RootElement));
    }

    [Fact]
    public void Reading_follows_the_saved_choice_and_defaults_to_windows_ocr_on_this_pc()
    {
        var directory = Directory.CreateTempSubdirectory("martlet-reading-").FullName;
        try
        {
            using (var reader = ScreenReader.For(directory)) Assert.Equal("Windows OCR on this PC", reader!.Engine);
            Assert.True(new ReadingSettings { Place = ReadingPlace.Host, HostId = "gpu-pc" }.Save(directory));
            using (var reader = ScreenReader.For(directory)) Assert.Equal("gpu-pc's Reading role", reader!.Engine);
            Assert.True(new ReadingSettings { Place = ReadingPlace.Off }.Save(directory));
            Assert.Null(ScreenReader.For(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void The_reading_host_role_is_listed_with_its_route()
    {
        var role = HostRoles.Get(HostRoles.Ocr);
        Assert.Equal("Reading", role.Name);
        Assert.Equal("martlet.gateway.ocr.v1", role.RouteId);
        Assert.Equal(HostRoles.Ocr, HostRoles.ForRoute(Martlet.Avatar.Audio2Face.Remote.Audio2FaceHostConnection.OcrRouteId)?.Kind);
    }
}
