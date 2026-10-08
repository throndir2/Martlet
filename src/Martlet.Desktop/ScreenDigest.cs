using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Platforms;
using Martlet.Core.Settings;
using Martlet.Providers;

// Also built into Martlet's MCP server (screen_digest_check), in its own namespace.
#if MARTLET_MCP
namespace Martlet.Mcp.Shared;
#else
namespace Martlet.Desktop;
#endif

/// <summary>How the screen summary over time keeps pictures and how often it asks for a summary (docs/SCREEN_COMMENTARY.md).</summary>
internal sealed record ScreenDigestTiming
{
    /// <summary>How far back the kept pictures go. The newest picture always stays: nothing changed since it.</summary>
    internal TimeSpan Window { get; init; } = TimeSpan.FromSeconds(24);
    /// <summary>The most pictures kept.</summary>
    internal int MaximumFrames { get; init; } = 8;
    /// <summary>How much a picture must change (0 to 1, the glancer's grey thumbnail score) to be kept; a new window title or
    /// program in front is always kept.</summary>
    internal double MinimumChange { get; init; } = 0.01;
    /// <summary>The least time between two summaries while the picture changes.</summary>
    internal TimeSpan Every { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>A summary newer than this is still fresh when you start to speak, so no new one is asked for.</summary>
    internal TimeSpan Fresh { get; init; } = TimeSpan.FromSeconds(12);
    /// <summary>The least time between two summaries when you start to speak.</summary>
    internal TimeSpan SpeechGap { get; init; } = TimeSpan.FromSeconds(4);
    /// <summary>A summary that arrives later than this after its newest picture is dropped; the job is stopped then too.</summary>
    internal TimeSpan Stale { get; init; } = TimeSpan.FromSeconds(20);
    /// <summary>How long a posted summary stays on the context board.</summary>
    internal TimeSpan MaximumAge { get; init; } = TimeSpan.FromSeconds(45);
    /// <summary>How long Martlet waits after a failed summary before it asks again.</summary>
    internal TimeSpan FailureWait { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>How many pictures go into one summary's contact sheet (2 to 4).</summary>
    internal int SheetFrames { get; init; } = 4;
    /// <summary>The longest edge of each kept picture, in pixels.</summary>
    internal int PanelEdge { get; init; } = 512;
}

/// <summary>One kept picture: downscaled BGRA32 pixels (top-down) in memory only, zeroed by <see cref="Clear"/>.</summary>
internal sealed class ScreenDigestFrame(byte[] pixels, int width, int height, string title, DateTimeOffset at, string? text,
    string app = "")
{
    private byte[]? pixels = pixels;
    internal int Width { get; } = width;
    internal int Height { get; } = height;
    internal string Title { get; } = title;
    /// <summary>The program in front, as the conversation names it ("Google Chrome, full screen"); empty when unknown.</summary>
    internal string App { get; } = app;
    /// <summary>When Martlet first saw this picture; it stayed on screen until the next kept one.</summary>
    internal DateTimeOffset At { get; } = at;
    /// <summary>The text Companion › Reading read on the screen at that time, or null.</summary>
    internal string? Text { get; } = text;
    internal byte[]? CopyPixels() => Volatile.Read(ref pixels) is { } owned ? (byte[])owned.Clone() : null;

    internal void Clear()
    {
        if (Interlocked.Exchange(ref pixels, null) is { } owned) Array.Clear(owned);
    }
}

/// <summary>The recent pictures that changed, for the screen summary over time. Memory only: never saved or logged, and
/// every picture it lets go is zeroed. A picture that hardly changed (and has the same window title and program in front) is
/// skipped, so the newest
/// kept picture is still what the screen shows. A picture goes once the picture after it is older than
/// <see cref="ScreenDigestTiming.Window"/>; the newest always stays.</summary>
internal sealed class ScreenDigestRing(ScreenDigestTiming timing)
{
    private readonly List<ScreenDigestFrame> frames = [];

    internal int Count => frames.Count;
    internal IReadOnlyList<ScreenDigestFrame> Frames => frames;
    internal DateTimeOffset? NewestAt => frames.Count == 0 ? null : frames[^1].At;

    /// <summary>Keeps a picture when it changed enough or shows another window. <paramref name="pixels"/> (BGRA32, top-down)
    /// is read only then and downscaled to <see cref="ScreenDigestTiming.PanelEdge"/>; the caller keeps (and clears) its own
    /// copy. <paramref name="app"/> is the program in front. Returns whether the picture was kept.</summary>
    internal bool Observe(int width, int height, string title, double change, DateTimeOffset at, string? text, Func<byte[]?> pixels,
        string app = "")
    {
        Prune(at);
        if (width <= 0 || height <= 0) return false;
        if (frames.Count > 0 && change < timing.MinimumChange && frames[^1].Title == title && frames[^1].App == app) return false;
        if (pixels() is not { } source || source.Length < width * height * 4) return false;
        try
        {
            var (small, w, h) = Downscale(source, width, height, timing.PanelEdge);
            frames.Add(new(small, w, h, title, at, string.IsNullOrWhiteSpace(text) ? null : text, app));
        }
        finally { Array.Clear(source); }
        while (frames.Count > Math.Max(2, timing.MaximumFrames)) RemoveAt(0);
        return true;
    }

    /// <summary>Lets go of pictures that left the screen before the window began: a picture stays while the one after it is
    /// inside the window, so the summary still sees what the screen showed before the oldest change. The newest always
    /// stays.</summary>
    internal void Prune(DateTimeOffset now)
    {
        while (frames.Count > 1 && now - frames[1].At > timing.Window) RemoveAt(0);
    }

    internal void Clear()
    {
        foreach (var frame in frames) frame.Clear();
        frames.Clear();
    }

    private void RemoveAt(int index)
    {
        frames[index].Clear();
        frames.RemoveAt(index);
    }

    /// <summary>An area-average downscale of BGRA32 pixels so the longest edge is at most <paramref name="edge"/>.</summary>
    internal static (byte[] Pixels, int Width, int Height) Downscale(byte[] source, int width, int height, int edge)
    {
        var scale = Math.Min(1.0, (double)edge / Math.Max(width, height));
        int w = Math.Max(1, (int)Math.Round(width * scale)), h = Math.Max(1, (int)Math.Round(height * scale));
        var result = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            int y0 = y * height / h, y1 = Math.Max(y0 + 1, (y + 1) * height / h);
            for (var x = 0; x < w; x++)
            {
                int x0 = x * width / w, x1 = Math.Max(x0 + 1, (x + 1) * width / w);
                long b = 0, g = 0, r = 0;
                for (var sy = y0; sy < y1; sy++)
                {
                    var row = sy * width * 4;
                    for (var sx = x0; sx < x1; sx++)
                    {
                        var i = row + sx * 4;
                        b += source[i];
                        g += source[i + 1];
                        r += source[i + 2];
                    }
                }
                var count = (y1 - y0) * (x1 - x0);
                var o = (y * w + x) * 4;
                result[o] = (byte)(b / count);
                result[o + 1] = (byte)(g / count);
                result[o + 2] = (byte)(r / count);
                result[o + 3] = 255;
            }
        }
        return (result, w, h);
    }
}

/// <summary>One picture made of the summary's screenshots (a contact sheet): two side by side, or three or four in a 2x2
/// grid, oldest first, with thin grey lines between them. One picture keeps the request to one image, which every Thinking
/// model that sees accepts, and its size (at most about 1024 px wide) bounds the vision tokens.</summary>
internal static class ScreenDigestSheet
{
    private const int Gap = 4;

    /// <summary>Up to <paramref name="count"/> pictures spread evenly over the kept ones, always the oldest and the newest.</summary>
    internal static IReadOnlyList<ScreenDigestFrame> Pick(IReadOnlyList<ScreenDigestFrame> frames, int count)
    {
        count = Math.Clamp(count, 2, 4);
        if (frames.Count <= count) return [.. frames];
        return [.. Enumerable.Range(0, count).Select(i => frames[(int)Math.Round(i * (frames.Count - 1) / (double)(count - 1))])];
    }

    private static readonly string[] Two = ["left", "right"], Four = ["top left", "top right", "bottom left", "bottom right"];

    /// <summary>Where each picture is on the sheet, in the words the prompt uses.</summary>
    internal static string[] Places(int count) => count <= 2 ? Two[..count] : Four[..count];

    /// <summary>Lays the pictures out (BGRA32, top-down); null when a picture was already cleared.</summary>
    internal static (byte[] Pixels, int Width, int Height)? Compose(IReadOnlyList<ScreenDigestFrame> picked)
    {
        if (picked.Count == 0) return null;
        var columns = picked.Count == 1 ? 1 : 2;
        var rows = picked.Count <= 2 ? 1 : 2;
        int cell = picked.Max(f => f.Width), cellHeight = picked.Max(f => f.Height);
        int width = columns * cell + (columns - 1) * Gap, height = rows * cellHeight + (rows - 1) * Gap;
        var sheet = new byte[width * height * 4];
        // Grey lines between the pictures; each cell is black around a smaller picture.
        for (var i = 0; i < sheet.Length; i += 4) { sheet[i] = sheet[i + 1] = sheet[i + 2] = 96; sheet[i + 3] = 255; }
        for (var n = 0; n < picked.Count; n++)
        {
            int left = n % 2 * (cell + Gap), top = n / 2 * (cellHeight + Gap);
            for (var y = 0; y < cellHeight; y++)
                Array.Fill(sheet, (byte)0, ((top + y) * width + left) * 4, cell * 4);
            var frame = picked[n];
            if (frame.CopyPixels() is not { } pixels || pixels.Length < frame.Width * frame.Height * 4)
            {
                Array.Clear(sheet);
                return null;
            }
            int ox = left + (cell - frame.Width) / 2, oy = top + (cellHeight - frame.Height) / 2;
            for (var y = 0; y < frame.Height; y++)
                Buffer.BlockCopy(pixels, y * frame.Width * 4, sheet, ((oy + y) * width + ox) * 4, frame.Width * 4);
            Array.Clear(pixels);
        }
        for (var i = 3; i < sheet.Length; i += 4) sheet[i] = 255;
        return (sheet, width, height);
    }

    /// <summary>JPEG-encodes the sheet for one request (WPF imaging; works off the UI thread).</summary>
    internal static BoundedImage Encode(byte[] pixels, int width, int height)
    {
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
        foreach (var quality in new[] { 70, 50, 35 })
        {
            var encoder = new JpegBitmapEncoder { QualityLevel = quality };
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            try
            {
                if (stream.Length <= BoundedImage.HardMaxBytes)
                    return new(stream.GetBuffer().AsSpan(0, (int)stream.Length), ImageMediaType.Jpeg, width, height);
            }
            finally { Array.Clear(stream.GetBuffer()); }
        }
        throw new InvalidOperationException("The screen summary picture could not be made small enough.");
    }
}

/// <summary>The summary's message (Companion › Prompts › Screen summary over time, then the text read on the pictures) and
/// how its answer is read.</summary>
internal static class ScreenDigestPrompt
{
    /// <summary>The longest summary kept, in characters.</summary>
    internal const int MaximumLength = 240;
    private const int TextPerFrame = 300, TextInAll = 900;

    /// <summary>The message for the <paramref name="picked"/> pictures, or null when the prompt is emptied (no summaries).</summary>
    internal static string? Message(PromptSettings? prompts, IReadOnlyList<ScreenDigestFrame> picked, DateTimeOffset now)
    {
        if (picked.Count == 0) return null;
        var places = ScreenDigestSheet.Places(picked.Count);
        var panels = string.Join(", ", picked.Select((frame, i) =>
        {
            var ago = (int)Math.Round((now - frame.At).TotalSeconds);
            var title = Clean(frame.Title);
            var app = Clean(frame.App);
            var where = title.Length > 0 && app.Length > 0 ? $" ({app}, window \"{title}\")"
                : title.Length > 0 ? $" (window \"{title}\")" : app.Length > 0 ? $" ({app})" : "";
            return $"{places[i]} {(ago <= 1 ? "just now" : $"{ago} s ago")}{where}";
        }));
        var seconds = Math.Max(1, (int)Math.Round((now - picked[0].At).TotalSeconds));
        var prompt = PromptSettings.Fill(prompts, PromptCatalog.ScreenDigest, ("count", picked.Count.ToString()),
            ("seconds", seconds.ToString()), ("panels", panels), ("silent", StayQuiet.Marker));
        if (prompt is null) return null;
        var read = new StringBuilder();
        string? last = null;
        foreach (var frame in picked)
        {
            if (frame.Text is not { } text || text == last) continue;
            last = text;
            var line = text.Length <= TextPerFrame ? text : text[..TextPerFrame] + "…";
            if (read.Length + line.Length > TextInAll) break;
            read.Append('\n').Append(ScreenDigestSheet.Places(picked.Count)[IndexOf(picked, frame)]).Append(": ").Append(line.Replace('\n', ' '));
        }
        return read.Length == 0 ? prompt
            : prompt + "\n\nText read on these screenshots (OCR; it can have small mistakes, and the picture is right when they differ):" + read;
    }

    /// <summary>The context board's note: one sentence to the model with how far back the summary looks.</summary>
    internal static string Note(string summary, TimeSpan span) =>
        $"Screen over the last {Math.Max(1, (int)Math.Round(span.TotalSeconds))} s: {summary}";

    private static int IndexOf(IReadOnlyList<ScreenDigestFrame> frames, ScreenDigestFrame frame)
    {
        for (var i = 0; i < frames.Count; i++) if (ReferenceEquals(frames[i], frame)) return i;
        return 0;
    }

    private static string Clean(string title) =>
        new string(title.Where(c => !char.IsControl(c) && c != '"').Take(80).ToArray()).Trim();

    /// <summary>The summary in an answer: at most two lines, without quotes, tags or a "Note:" label, at most
    /// <see cref="MaximumLength"/> characters. Null when the answer is empty or [pass] (nothing changed).</summary>
    internal static string? Parse(string? reply)
    {
        if (reply is null || StayQuiet.IsQuiet(reply)) return null;
        var lines = reply.Replace("\r", "").Split('\n')
            .Select(line => System.Text.RegularExpressions.Regex.Replace(line, @"\[[a-zA-Z_]+:[^\]]*\]", "").Trim())
            .Select(line => line.TrimStart('-', '*', '•', ' ').Trim())
            .Where(line => line.Length > 0)
            .Take(2)
            .ToArray();
        if (lines.Length == 0) return null;
        var text = string.Join(" ", lines);
        foreach (var label in new[] { "Note:", "Summary:", "Change:", "Changes:" })
            if (text.StartsWith(label, StringComparison.OrdinalIgnoreCase)) text = text[label.Length..].TrimStart();
        text = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim().Trim('"', '“', '”').Trim();
        if (text.Length == 0 || StayQuiet.IsQuiet(text)) return null;
        return text.Length <= MaximumLength ? text : text[..(MaximumLength - 1)].TrimEnd() + "…";
    }
}

/// <summary>One summary job: the message and the contact sheet, for a Thinking model that sees and is not the live
/// conversation's own route.</summary>
internal sealed record ScreenDigestJob(string Message, BoundedImage Picture, int Frames, DateTimeOffset From, DateTimeOffset To,
    string Reason);

/// <summary>Who makes the summaries: the Thinking pool's digest jobs on a member that sees.</summary>
internal interface IScreenDigestThinker
{
    /// <summary>Whether a Thinking model that sees, other than the live conversation's own route, can take digest jobs.</summary>
    bool CanSee { get; }
    /// <summary>Whether such a model may start a digest job now: while you talk with Martlet, only one that shares no hardware
    /// with the conversation may (the live floor). False skips the summary before any picture is prepared.</summary>
    bool MayStartNow => CanSee;
    /// <summary>Whether a model that sees shares no hardware with the live conversation: the summary right after you start to
    /// speak runs only there.</summary>
    bool SeesBesideConversation => CanSee;
    /// <summary>Runs one job and returns the model's answer. Throws when it fails; canceled when the job got stale.</summary>
    Task<string?> DigestAsync(ScreenDigestJob job, CancellationToken cancellation);
}

/// <summary>Where a summary goes: the context board, as source "screen", so the next live request takes it as a note after
/// the user's words. A reply never waits for it.</summary>
internal interface IScreenDigestBoard
{
    void Post(string text, DateTimeOffset at, TimeSpan maximumAge);
    /// <summary>Takes the screen note off the board (the summary turned off or watching stopped).</summary>
    void Clear();
}

/// <summary>What the screen summary over time is doing, for the talk window and Martlet MCP. Never a window title.</summary>
internal sealed record ScreenDigestStatus(bool On, bool CanSee, int Frames, string? LastText, TimeSpan? LastAge, TimeSpan? LastTook,
    int Jobs, int Posted, int Quiet, int Dropped, int Failed, bool Running, string? Problem);

/// <summary>The screen summary over time (docs/SCREEN_COMMENTARY.md): while Martlet watches, it keeps the recent pictures
/// that changed (<see cref="ScreenDigestRing"/>) and, every <see cref="ScreenDigestTiming.Every"/> while they change (and
/// right after you start to speak, when no fresh summary is there), posts one background job with a contact sheet of the
/// last few to a Thinking model that sees. The one or two lines it answers go to the context board for the next reply. One
/// job at a time; a job that gets stale is stopped and its answer dropped. Nothing waits for it, and nothing is saved.</summary>
internal sealed class ScreenDigester
{
    private readonly object gate = new();
    private readonly ScreenDigestTiming timing;
    private readonly ScreenDigestRing ring;
    private readonly TimeProvider clock;
    private readonly Func<PromptSettings?>? prompts;
    private IScreenDigestThinker thinker;
    private IScreenDigestBoard board;
    private CancellationTokenSource? stop;
    private Task? running;
    private DateTimeOffset? started, answered, posted, failed, covered;
    private TimeSpan? took;
    private string? lastText, problem;
    private int jobs, postedCount, quiet, dropped, failures;
    private bool on;

    internal ScreenDigester(IScreenDigestThinker thinker, IScreenDigestBoard board, TimeProvider clock,
        ScreenDigestTiming? timing = null, Func<PromptSettings?>? prompts = null)
    {
        this.thinker = thinker;
        this.board = board;
        this.clock = clock;
        this.timing = timing ?? new();
        this.prompts = prompts;
        ring = new(this.timing);
    }

    internal ScreenDigestTiming Timing => timing;

    /// <summary>Whether the summary runs: the setting is on, Martlet watches and a Thinking model that sees can take the jobs.</summary>
    internal bool On { get { lock (gate) return on && thinker.CanSee; } }

    /// <summary>Uses another pool or board (a settings change).</summary>
    internal void Use(IScreenDigestThinker newThinker, IScreenDigestBoard newBoard)
    {
        lock (gate)
        {
            thinker = newThinker;
            board = newBoard;
        }
    }

    /// <summary>Turns the summary on or off; off lets every picture go, stops a job in progress and takes the screen note off
    /// the board.</summary>
    internal void Turn(bool value)
    {
        lock (gate)
        {
            var was = on;
            on = value;
            if (!value) ClearLocked();
            if (was && !value) board.Clear();
        }
    }

    /// <summary>Lets every kept picture go and stops a job in progress (stop watching, Windows locked, pause).</summary>
    internal void Clear()
    {
        lock (gate) ClearLocked();
    }

    private void ClearLocked()
    {
        ring.Clear();
        stop?.Cancel();
        stop = null;
        covered = null;
    }

    /// <summary>Offers a new picture of what Martlet watches; <paramref name="pixels"/> is read only when it is kept.
    /// <paramref name="app"/> is the program in front, as the conversation names it.</summary>
    internal bool Observe(int width, int height, string title, double change, string? text, Func<byte[]?> pixels, string app = "")
    {
        lock (gate)
        {
            if (!on || !thinker.CanSee) return false;
            return ring.Observe(width, height, title, change, clock.GetUtcNow(), text, pixels, app);
        }
    }

    /// <summary>Called on the watch timer: starts a summary when the pictures changed since the last one and it is time.
    /// Returns the job, or null.</summary>
    internal Task? Tick()
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            if (!Ready(now)) return null;
            if (started is { } last && now - last < timing.Every) return null;
            return StartLocked(now, "changes");
        }
    }

    /// <summary>Called right after you start to speak: starts a summary at once when no fresh one is there and the pictures
    /// changed since the last one. Returns the job, or null. The reply never waits for it, and while you talk only a member
    /// that shares no hardware with the conversation takes it (<see cref="IScreenDigestThinker.MayStartNow"/>).</summary>
    internal Task? UserSpeaking()
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            if (!thinker.SeesBesideConversation || !Ready(now)) return null;
            if (answered is { } fresh && now - fresh <= timing.Fresh) return null;
            if (started is { } last && now - last < timing.SpeechGap) return null;
            return StartLocked(now, "speech");
        }
    }

    private bool Ready(DateTimeOffset now)
    {
        if (!on || running is { IsCompleted: false } || !thinker.CanSee) return false;
        ring.Prune(now);
        if (ring.Count < 2 || ring.NewestAt <= covered) return false;
        if (failed is { } failure && now - failure < timing.FailureWait) return false;
        // No member may take it now (the live floor holds the ones the conversation uses): no picture is prepared for nothing.
        return thinker.MayStartNow;
    }

    private Task StartLocked(DateTimeOffset now, string reason)
    {
        var picked = ScreenDigestSheet.Pick(ring.Frames, timing.SheetFrames);
        // Copies, so a picture the ring lets go meanwhile is still there for the sheet; zeroed when the job ends.
        var copies = picked.Select(f => new ScreenDigestFrame(f.CopyPixels() ?? [], f.Width, f.Height, f.Title, f.At, f.Text)).ToArray();
        covered = picked[^1].At;
        started = now;
        jobs++;
        stop ??= new();
        var job = RunAsync(copies, reason, now, stop.Token, thinker, board);
        running = job;
        return job;
    }

    private async Task RunAsync(ScreenDigestFrame[] picked, string reason, DateTimeOffset begun, CancellationToken stopped,
        IScreenDigestThinker by, IScreenDigestBoard to)
    {
        using var timeout = new CancellationTokenSource(timing.Stale, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopped, timeout.Token);
        try
        {
            var message = ScreenDigestPrompt.Message(prompts?.Invoke(), picked, begun);
            if (message is null)
            {
                lock (gate) problem = "The Screen summary over time prompt is empty, so no summaries are made.";
                return;
            }
            // The contact sheet is made below normal priority, so the conversation's own work on this PC comes first.
            var job = await LowPriority.RunAsync(() =>
            {
                var sheet = ScreenDigestSheet.Compose(picked) ?? throw new OperationCanceledException();
                try { return new ScreenDigestJob(message, ScreenDigestSheet.Encode(sheet.Pixels, sheet.Width, sheet.Height), picked.Length, picked[0].At, picked[^1].At, reason); }
                finally { Array.Clear(sheet.Pixels); }
            }, linked.Token, "Martlet screen summary").ConfigureAwait(false);
            var reply = await by.DigestAsync(job, linked.Token).ConfigureAwait(false);
            var now = clock.GetUtcNow();
            lock (gate)
            {
                took = now - begun;
                if (stopped.IsCancellationRequested) return;
                if (now - job.To > timing.Stale)
                {
                    dropped++;
                    return;
                }
                answered = now;
                failed = null;
                problem = null;
                if (ScreenDigestPrompt.Parse(reply) is not { } text)
                {
                    quiet++;
                    return;
                }
                lastText = text;
                posted = now;
                postedCount++;
                to.Post(ScreenDigestPrompt.Note(text, job.To - job.From), now, timing.MaximumAge);
            }
        }
        catch (OperationCanceledException)
        {
            lock (gate)
            {
                took = clock.GetUtcNow() - begun;
                if (!stopped.IsCancellationRequested) dropped++;
            }
        }
        catch (Exception error) when (error is InvalidOperationException or ContractException or NotSupportedException or
            IOException or System.Net.Http.HttpRequestException or System.Runtime.InteropServices.ExternalException or ArgumentException)
        {
            lock (gate)
            {
                took = clock.GetUtcNow() - begun;
                failed = clock.GetUtcNow();
                failures++;
                problem = "The last screen summary failed; Martlet tries again in a minute.";
            }
        }
        finally
        {
            foreach (var frame in picked) frame.Clear();
            lock (gate) running = null;
        }
    }

    internal ScreenDigestStatus Status
    {
        get
        {
            lock (gate)
            {
                var now = clock.GetUtcNow();
                return new(on && thinker.CanSee, thinker.CanSee, ring.Count, lastText, posted is { } at ? now - at : null, took, jobs,
                    postedCount, quiet, dropped, failures, running is { IsCompleted: false }, problem);
            }
        }
    }

    /// <summary>The talk window's line while watching: how many pictures are kept and when the last summary came. Empty
    /// while the summary is off.</summary>
    internal static string Line(ScreenDigestStatus status)
    {
        if (!status.On) return "";
        var kept = $"Screen summary: {status.Frames} {(status.Frames == 1 ? "picture" : "pictures")} kept";
        if (status.Running) return kept + "; summarizing…";
        if (status.Problem is { } why) return kept + ". " + why;
        return status.LastAge is { } age
            ? $"{kept}; last summary {Ago(age)}" + (status.LastTook is { } t ? $" (took {t.TotalSeconds:0.0} s)." : ".")
            : kept + ".";
    }

    private static string Ago(TimeSpan age) => age.TotalSeconds < 2 ? "just now"
        : age.TotalMinutes < 2 ? $"{(int)age.TotalSeconds} s ago" : $"{(int)age.TotalMinutes} min ago";
}
