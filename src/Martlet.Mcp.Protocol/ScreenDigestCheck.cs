using System.IO;
using System.Text.Json;
using Martlet.Core.Settings;
using Martlet.Mcp.Shared;

namespace Martlet.Mcp;

/// <summary>screen_digest_check: the screen summary over time (docs/SCREEN_COMMENTARY.md), run once with the desktop's production
/// ScreenDigester on FIXTURE frames and a FIXTURE thinker and context board: no screen capture, no model and nothing sent. It
/// returns the setting from a data directory's talk-preferences.json, which frames the ring kept, the job (contact sheet and
/// message as the data directory's prompts make it), what went to the board, the status and the talk window's line, then
/// shows that a stale answer is dropped. Reads no credentials and contacts nothing.</summary>
internal static class ScreenDigestCheck
{
    internal const string FixtureReply = "FIXTURE: They switched from Visual Studio Code to a boss fight; health dropped to 20%.";

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan time) => now += time;
    }

    private sealed class Thinker(string reply, Action? beforeAnswer = null) : IScreenDigestThinker
    {
        public ScreenDigestJob? Job { get; private set; }
        public bool CanSee => true;
        public Task<string?> DigestAsync(ScreenDigestJob job, CancellationToken cancellation)
        {
            Job = job;
            beforeAnswer?.Invoke();
            return Task.FromResult<string?>(reply);
        }
    }

    private sealed class Board : IScreenDigestBoard
    {
        public List<object> Posts { get; } = [];
        public void Post(string text, DateTimeOffset at, TimeSpan maximumAge) =>
            Posts.Add(new { source = "screen", text, maximumAgeSeconds = maximumAge.TotalSeconds });
        public void Clear() => Posts.Add(new { source = "screen", cleared = true });
    }

    private static byte[] Picture(int width, int height, bool game)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                // A dark editor with light text lines, or a red arena with a short health bar.
                var (b, g, r) = game
                    ? y < 40 && x < width / 5 ? ((byte)0, (byte)200, (byte)0) : ((byte)30, (byte)30, (byte)150)
                    : y % 24 < 4 && x % 300 < 200 ? ((byte)220, (byte)220, (byte)220) : ((byte)40, (byte)35, (byte)30);
                pixels[i] = b; pixels[i + 1] = g; pixels[i + 2] = r; pixels[i + 3] = 255;
            }
        return pixels;
    }

    internal static async Task<object> RunAsync(string directory, string? reply, CancellationToken cancellation)
    {
        if (reply is not null && (string.IsNullOrWhiteSpace(reply) || reply.Length > 1024))
            throw new ArgumentException("'reply' must be 1-1024 characters.");
        var setting = Setting(directory);
        var loaded = await new SettingsStore(directory).LoadAsync(cancellation);
        var prompts = loaded.Settings?.Prompts;

        var clock = new Clock();
        var thinker = new Thinker(reply ?? FixtureReply);
        var board = new Board();
        var digester = new ScreenDigester(thinker, board, clock, prompts: () => prompts);
        digester.Turn(true);
        var frames = new[]
        {
            ("Program.cs - Visual Studio Code", 1.0, "dotnet build", false),
            ("Program.cs - Visual Studio Code", 0.001, "dotnet build", false),
            ("ELDEN RING", 0.6, "HP 20%", true),
            ("ELDEN RING", 0.05, "HP 20%", true)
        };
        var observed = new List<object>();
        foreach (var (title, change, text, game) in frames)
        {
            var kept = digester.Observe(1280, 720, title, change, text, () => Picture(1280, 720, game));
            observed.Add(new { title, change, kept });
            clock.Advance(TimeSpan.FromSeconds(3));
        }
        var started = digester.Tick();
        if (started is not null) await started.WaitAsync(TimeSpan.FromSeconds(30), cancellation);
        var status = digester.Status;
        var job = thinker.Job;

        // A stale answer: the model answers later than the stale limit after the newest picture, so it is dropped.
        var staleClock = new Clock();
        ScreenDigester? stale = null;
        var staleBoard = new Board();
        var slow = new Thinker(FixtureReply, () => staleClock.Advance(stale!.Timing.Stale + TimeSpan.FromSeconds(1)));
        stale = new ScreenDigester(slow, staleBoard, staleClock);
        stale.Turn(true);
        stale.Observe(1280, 720, "Program.cs - Visual Studio Code", 1, null, () => Picture(1280, 720, false));
        stale.Observe(1280, 720, "ELDEN RING", 1, null, () => Picture(1280, 720, true));
        if (stale.Tick() is { } staleJob) await staleJob.WaitAsync(TimeSpan.FromSeconds(30), cancellation);

        return new
        {
            fixture = "FIXTURE frames, thinker and board: no screen capture, no model, nothing sent.",
            setting = new { screenSummary = setting.On, source = setting.Source },
            timing = new
            {
                windowSeconds = digester.Timing.Window.TotalSeconds,
                everySeconds = digester.Timing.Every.TotalSeconds,
                freshSeconds = digester.Timing.Fresh.TotalSeconds,
                staleSeconds = digester.Timing.Stale.TotalSeconds,
                maximumAgeSeconds = digester.Timing.MaximumAge.TotalSeconds,
                sheetFrames = digester.Timing.SheetFrames,
                panelEdge = digester.Timing.PanelEdge
            },
            prompt = loaded.State switch
            {
                SettingsLoadState.Loaded => "loaded",
                SettingsLoadState.FirstRun => "none",
                _ => "unreadable"
            },
            observed,
            job = job is null ? null : new
            {
                job.Reason, job.Frames, sheet = $"{job.Picture.Width}x{job.Picture.Height}", sheetBytes = job.Picture.ByteCount,
                mediaType = job.Picture.MimeType, job.Message, spanSeconds = (job.To - job.From).TotalSeconds
            },
            answer = reply ?? FixtureReply,
            parsed = ScreenDigestPrompt.Parse(reply ?? FixtureReply),
            board = board.Posts,
            status = new
            {
                status.On, status.Frames, status.LastText, lastAgeSeconds = status.LastAge?.TotalSeconds,
                tookMilliseconds = status.LastTook?.TotalMilliseconds, status.Jobs, status.Posted, status.Quiet, status.Dropped,
                status.Failed, status.Problem
            },
            line = ScreenDigester.Line(status),
            staleAnswer = new { dropped = stale.Status.Dropped, posted = staleBoard.Posts.Count }
        };
    }

    private static (bool On, string Source) Setting(string directory)
    {
        var path = Path.Combine(directory, "talk-preferences.json");
        if (!File.Exists(path)) return (true, "default (no talk-preferences.json)");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("ScreenSummary", out var value) && value.ValueKind is JsonValueKind.False
                ? (false, "talk-preferences.json")
                : (true, document.RootElement.TryGetProperty("ScreenSummary", out _) ? "talk-preferences.json" : "default");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return (true, "default (talk-preferences.json unreadable)");
        }
    }
}
