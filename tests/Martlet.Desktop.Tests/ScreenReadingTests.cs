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
        public (int Width, int Height) Size { get; private set; }
        public TaskCompletionSource? Gate { get; set; }
        public string Engine => "the test reader";

        public async Task<IReadOnlyList<ReadLine>> ReadAsync(byte[] bgra, int width, int height, CancellationToken token)
        {
            Size = (width, height);
            var lines = reads[Math.Min(next++, reads.Length - 1)];
            if (Gate is { } gate) await gate.Task;
            return [.. lines.Select((text, i) => new ReadLine(text, 10, 10 + i * 30, 200, 20))];
        }

        public void Dispose() { }
    }

    // A full-size picture whose grey thumbnail is all one shade: the same shade is the same picture.
    private static ScreenFrame Picture(byte shade) => new(new byte[8 * 4 * 4], 8, 4, "game", 0, signature: Enumerable.Repeat(shade, 16 * 9).ToArray());

    [Fact]
    public async Task Watching_reads_the_full_size_look_and_not_the_downscaled_one()
    {
        var fake = new FakeReader(["HEALTH 87"]);
        using var reader = new ScreenReader(fake, new Clock());
        var full = new ScreenFrame(new byte[32 * 16 * 4], 32, 16, "game", 0);

        var read = await reader.Offer(() => full)!;

        Assert.Equal((32, 16), fake.Size);
        Assert.Equal((32, 16), (read!.Width, read.Height));
        Assert.Null(full.CopyPixels());
        Assert.Contains("(32 x 16 screenshot).", read.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_skipped_full_size_look_reads_nothing_and_frees_the_reader()
    {
        var clock = new Clock();
        var fake = new FakeReader(["HEALTH 87"]);
        using var reader = new ScreenReader(fake, clock);

        Assert.Null(await reader.Offer(() => null)!);
        Assert.Equal(0, fake.Calls);
        Assert.False(reader.Busy);
        Assert.Null(reader.Latest);
        clock.Advance(reader.Every);
        Assert.Null(await reader.Offer(() => throw new InvalidOperationException("no screen"))!);
        Assert.Contains("Couldn't take a full-size picture of the screen (no screen)", reader.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_full_size_copy_keeps_every_pixel_and_makes_it_opaque()
    {
        const int width = 5, height = 3, sourceWidth = 9;
        var source = new byte[sourceWidth * height * 4];
        Random.Shared.NextBytes(source);
        var destination = new byte[width * height * 4];
        DesktopDuplication.Downscale((row, column, into, count) =>
            Buffer.BlockCopy(source, (row * sourceWidth + column) * 4, into, 0, count * 4), 2, 0, width, height, destination, width, height);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var from = (y * sourceWidth + x + 2) * 4;
                var to = (y * width + x) * 4;
                Assert.Equal(source[from..(from + 3)], destination[to..(to + 3)]);
                Assert.Equal(255, destination[to + 3]);
            }
    }

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
        var fake = new FakeReader(["HEALTH 87"], ["HEALTH 87"], ["HEALTH 87"], ["VICTORY"], ["VICTORY"]) { Gate = new() };
        using var reader = new ScreenReader(fake, clock);

        var first = reader.Offer(() => Picture(40));
        Assert.NotNull(first);
        Assert.Null(reader.Offer(() => Picture(200)));
        fake.Gate.SetResult();
        var read = await first!;
        Assert.Equal("HEALTH 87", read!.Text);
        Assert.Equal("", read.Steady);
        Assert.Equal(1, read.Pending);
        Assert.Equal(0, read.Change);
        Assert.EndsWith("1 line waits for the next read to count.", read.Describe(), StringComparison.Ordinal);

        // A first read waits for a second: the same picture is read once more at once, then counts.
        clock.Advance(reader.Every);
        var confirmed = await reader.Offer(() => Picture(40))!;
        Assert.Equal("HEALTH 87", confirmed!.Steady);
        Assert.Equal(0, confirmed.Pending);

        // The same picture again soon: not read; after a while, or once it changed, it is.
        clock.Advance(reader.Every);
        Assert.Null(await reader.Offer(() => Picture(40))!);
        clock.Advance(ScreenReadPace.Refresh - reader.Every);
        var again = await reader.Offer(() => Picture(40))!;
        Assert.Equal(0, again!.Change);
        Assert.Contains($", {ScreenReadPace.Refresh.TotalSeconds:0.0} s after the read before.", again.Describe(), StringComparison.Ordinal);
        clock.Advance(reader.Every);
        var changed = await reader.Offer(() => Picture(200))!;
        Assert.Equal("VICTORY", changed!.Text);
        // New text counts only once a second read agrees, so the first read of it changes nothing yet.
        Assert.Equal("", changed.Steady);
        Assert.Equal(1, changed.Change);
        Assert.Same(changed, reader.Latest);
        Assert.Contains("Read 1 line with the test reader", changed.Describe(), StringComparison.Ordinal);
        clock.Advance(reader.Every);
        var counts = await reader.Offer(() => Picture(200))!;
        Assert.Equal("VICTORY", counts!.Steady);
        Assert.Equal(1, counts.Change);
        Assert.Equal(5, fake.Calls);
    }

    [Fact]
    public async Task A_character_misread_once_never_counts_and_changes_nothing()
    {
        var clock = new Clock();
        var fake = new FakeReader(["Apple pie", "HEALTH 87"], ["Apple pie", "HEALTH 87"], ["App1e pie", "HEALTH 87"],
            ["Apple pie", "HEALTH 87"]);
        using var reader = new ScreenReader(fake, clock);
        var pictures = 0;
        ScreenFrame Next() => Picture((byte)(pictures++ % 2 == 0 ? 40 : 200));
        var reads = new List<ScreenRead>();
        for (var i = 0; i < 4; i++)
        {
            reads.Add((await reader.Offer(Next)!)!);
            clock.Advance(reader.Every);
        }

        Assert.Equal("Apple pie\nHEALTH 87", reads[1].Steady);
        Assert.Equal("App1e pie\nHEALTH 87", reads[2].Text);
        Assert.Equal("Apple pie\nHEALTH 87", reads[2].Steady);
        Assert.Equal(0, reads[2].Change);
        Assert.Equal("Apple pie\nHEALTH 87", reads[3].Steady);
        Assert.Equal(0, reads[3].Change);
    }

    [Fact]
    public async Task Reader_takes_a_picture_no_more_often_than_its_pace_and_as_often_as_it_is_set()
    {
        var clock = new Clock();
        var fake = new FakeReader(["Score 1"], ["Score 2"], ["Score 3"], ["Score 4"]);
        using var reader = new ScreenReader(fake, clock) { Every = TimeSpan.FromMilliseconds(500) };
        var pictures = 0;
        ScreenFrame Next() => Picture((byte)(pictures++ % 2 == 0 ? 40 : 200));

        Assert.Equal("Score 1", (await reader.Offer(Next)!)!.Text);
        clock.Advance(TimeSpan.FromMilliseconds(400));
        Assert.Null(reader.Offer(Next));
        Assert.Equal(1, pictures);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal("Score 2", (await reader.Offer(Next)!)!.Text);
        // Twice a second: four reads of a changing screen in 1.5 seconds, where the looks' 3-second pace read one.
        clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal("Score 3", (await reader.Offer(Next)!)!.Text);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal("Score 4", (await reader.Offer(Next)!)!.Text);
        Assert.Equal(4, fake.Calls);
    }

    [Fact]
    public void Read_the_screen_travels_to_another_computer_and_an_older_one_leaves_it_alone()
    {
        // Computer A reads twice a second; computer B (still the default) follows the talk setting A shares.
        var a = new TalkPreferences(ReadEveryMs: 500, ScreenChattiness: 2);
        var b = new TalkPreferences(VoiceVolume: 0.4);
        var followed = TalkSharing.Follow(b, TalkSharing.Value(a));
        Assert.Equal(500, followed.ReadEveryMs);
        Assert.Equal(2, followed.ScreenChattiness);
        Assert.Equal(0.4, followed.VoiceVolume);

        // A computer on an older Martlet shares how you talk without Read the screen: B keeps its own choice.
        var older = """{"hands_free":true,"pause_index":1,"speak_replies":true,"hear_voice":false,"barge_in":false,"screen_chattiness":1}""";
        Assert.Equal(500, TalkSharing.Follow(followed, older).ReadEveryMs);
        Assert.Equal(1000, TalkSharing.Follow(b, """{"read_every_ms":1200,"screen_chattiness":1}""").ReadEveryMs);
        Assert.Contains("\"read_every_ms\":500", TalkSharing.Value(a), StringComparison.Ordinal);
    }

    [Fact]
    public void Read_the_screen_keeps_a_known_choice_and_snaps_others_to_the_nearest()
    {
        Assert.Equal(3000, ScreenReadPace.DefaultMs);
        Assert.All(ScreenReadPace.ChoicesMs, ms => Assert.Equal(ms, ScreenReadPace.Normalize(ms)));
        Assert.Equal(ScreenReadPace.DefaultMs, ScreenReadPace.Normalize(0));
        Assert.Equal(500, ScreenReadPace.Normalize(100));
        Assert.Equal(10000, ScreenReadPace.Normalize(60000));
        Assert.Equal("Twice a second", ScreenReadPace.Describe(500));
        Assert.Equal("Every 3 seconds", ScreenReadPace.Describe(3000));

        var directory = Directory.CreateTempSubdirectory("martlet-read-every-").FullName;
        try
        {
            Assert.Equal(ScreenReadPace.DefaultMs, TalkPreferences.Load(directory).ReadEveryMs);
            Assert.True(new TalkPreferences(ReadEveryMs: 1000).Save(directory));
            Assert.Equal(1000, TalkPreferences.Load(directory).ReadEveryMs);
            File.WriteAllText(Path.Combine(directory, "talk-preferences.json"), """{"ReadEveryMs":1200}""");
            Assert.Equal(1000, TalkPreferences.Load(directory).ReadEveryMs);
        }
        finally { Directory.Delete(directory, recursive: true); }
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
        var lines = PoolScreenTextReader.Lines(answer.RootElement);
        Assert.Equal([new ReadLine("Score: 12,450", 699, 41, 180, 30), new ReadLine("VICTORY", 383, 269, 240, 60)], lines);

        using var failed = JsonDocument.Parse("""{"error":{"code":"request.invalid","summary":"not a picture"}}""");
        Assert.Contains("not a picture", Assert.Throws<ScreenReadException>(() => PoolScreenTextReader.Lines(failed.RootElement)).Message);
        using var invalid = JsonDocument.Parse("""{"lines":[{"text":"x"}]}""");
        Assert.Throws<ScreenReadException>(() => PoolScreenTextReader.Lines(invalid.RootElement));
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
