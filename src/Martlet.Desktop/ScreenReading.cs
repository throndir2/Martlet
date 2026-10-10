using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;
using Martlet.Core.Platforms;
using Martlet.Core.Reading;
using Martlet.Discord.Calls;

namespace Martlet.Desktop;

/// <summary>One read of the text on a screenshot: the lines in reading order, their text, how much the text changed since the
/// read before (0 to 1; 0 for the first read), how long it took, which engine read it, when, and the screenshot's size.</summary>
internal sealed record ScreenRead(IReadOnlyList<ReadLine> Lines, string Text, double Change, TimeSpan Took, string Engine, DateTimeOffset At,
    int Width = 0, int Height = 0)
{
    /// <summary>The read in words, for Companion › Reading and the talk window's vision tooltip.</summary>
    internal string Describe() =>
        $"Read {Lines.Count} {(Lines.Count == 1 ? "line" : "lines")} with {Engine} in {Took.TotalMilliseconds:0} ms at {At.ToLocalTime():t}" +
        (Width > 0 && Height > 0 ? $" ({Width} x {Height} screenshot)." : ".");
}

/// <summary>Why the text on the screen couldn't be read (Windows has no OCR language, the host doesn't answer). <see cref="Busy"/>:
/// the host's Reading role was reading for another computer; the next capture tries again.</summary>
internal sealed class ScreenReadException(string message, bool busy = false, Exception? inner = null) : Exception(message, inner)
{
    internal bool Busy { get; } = busy;
}

/// <summary>Reads the text in a screenshot (BGRA32, top-down): Windows' own OCR on this PC or a host's Reading role.</summary>
internal interface IScreenTextReader : IDisposable
{
    /// <summary>Which engine reads, in words ("Windows OCR on this PC", "gpu-pc's Reading role").</summary>
    string Engine { get; }
    Task<IReadOnlyList<ReadLine>> ReadAsync(byte[] bgra, int width, int height, CancellationToken token);
}

/// <summary>Windows' own OCR (Windows.Media.Ocr) on this PC's processor, in the user's Windows languages: no download, no
/// network, nothing kept. The same reader Discord calls use to find who speaks.</summary>
internal sealed class WindowsScreenTextReader : IScreenTextReader
{
    internal const string NoLanguage =
        "Windows can't read text yet: it has no text recognition (OCR) language. Add your language in Windows Settings › Time & " +
        "language › Language & region (its Optical character recognition feature comes with it).";

    // A full desktop has more lines than the 60 a prompt keeps; ScreenText.Order picks them in reading order.
    private readonly WindowsCallTextReader reader = new(maximumLines: ScreenText.MaximumLines * 4);

    public string Engine => "Windows OCR on this PC";

    /// <summary>Whether Windows has an OCR language for the user (checked once; blocks the first time, so call it off the UI thread).</summary>
    internal static bool Available => WindowsCallTextReader.Available;

    public async Task<IReadOnlyList<ReadLine>> ReadAsync(byte[] bgra, int width, int height, CancellationToken token)
    {
        if (!await Task.Run(() => Available, token).ConfigureAwait(false)) throw new ScreenReadException(NoLanguage);
        var lines = await reader.ReadAsync(bgra, width, height, token).ConfigureAwait(false);
        return [.. lines.Select(line => new ReadLine(line.Text, line.Box.X, line.Box.Y, line.Box.Width, line.Box.Height))];
    }

    public void Dispose() { }
}

/// <summary>Companion › Reading's list (<see cref="ReadingList"/>, docs/READING.md#the-reading-pool) as one reader: Windows OCR on
/// this PC and the Reading role (RapidOCR or PP-OCRv5) of paired computers, through their gateways (route
/// <c>martlet.gateway.ocr.v1</c>). Each read goes through <see cref="WorkQueue"/> (lane <c>reading</c>) at background priority to
/// the list's places in order (<see cref="Targets"/>, read again from local files for each read): a place that is busy, doesn't
/// answer or can't read now (a computer without the role, Windows without an OCR language) is passed over for the next at once;
/// when every one is busy the read waits up to <see cref="Wait"/> for the first to free. While the first is free nothing changes:
/// no extra request. A screenshot goes to a computer as a JPEG (encoded once, only when a computer reads), is read in memory
/// there and is not kept. Each computer's connection is kept between reads and dropped when that computer fails.</summary>
internal sealed class PoolScreenTextReader : IScreenTextReader
{
    /// <summary>Reads <paramref name="jpeg"/> on one computer: the route it used and the lines.</summary>
    internal delegate Task<(HostRoute Route, IReadOnlyList<ReadLine> Lines)> HostRead(PairedHost host, byte[] jpeg, CancellationToken token);

    /// <summary>Reads the screenshot with Windows OCR on this PC.</summary>
    internal delegate Task<IReadOnlyList<ReadLine>> LocalRead(byte[] bgra, int width, int height, CancellationToken token);

    /// <summary>How long a read waits in line when every place is busy. The next capture brings a newer screenshot anyway.</summary>
    internal static TimeSpan Wait => TimeSpan.FromSeconds(3);

    private readonly string? dataDirectory;
    private readonly WorkQueue queue;
    private readonly TimeSpan wait;
    private readonly HostRead read;
    private readonly LocalRead local;
    private readonly object gate = new();
    private readonly Dictionary<string, (HostRoute Route, Audio2FaceHostConnection Connection)> open = new(StringComparer.Ordinal);
    private WindowsScreenTextReader? windows;
    private volatile string? last;

    /// <summary>Reads with the list in <paramref name="dataDirectory"/>. The other arguments are for tests: the queue (default
    /// <see cref="WorkQueue.Shared"/>), the wait, the read on one computer and the read with Windows OCR.</summary>
    internal PoolScreenTextReader(string? dataDirectory, WorkQueue? queue = null, TimeSpan? wait = null, HostRead? read = null,
        LocalRead? local = null)
    {
        this.dataDirectory = dataDirectory;
        this.queue = queue ?? WorkQueue.Shared;
        this.wait = wait ?? Wait;
        this.read = read ?? ReadOnAsync;
        this.local = local ?? ((bgra, width, height, token) => (windows ??= new()).ReadAsync(bgra, width, height, token));
    }

    /// <summary>The place that did the last read, else the list's first that is on.</summary>
    public string Engine => last ?? (ReadingList.Load(dataDirectory).Members.FirstOrDefault(m => !m.Off) is { } first
        ? ReadingPool.Describe(first) : "nothing (the Reading list is empty)");

    public async Task<IReadOnlyList<ReadLine>> ReadAsync(byte[] bgra, int width, int height, CancellationToken token)
    {
        var targets = Targets();
        byte[]? jpeg = null;
        // The queue throws the last refusal, which is always from the place it tried last.
        var tried = targets[0];
        try
        {
            var (engine, lines) = await queue.RunAsync(PoolAreas.Reading.Id, targets, p => p.Target.Key, async (p, t) =>
            {
                tried = p;
                if (p.Host is not { } host)
                {
                    // Windows can't read here (no OCR language): the next place reads.
                    try { return ("Windows OCR on this PC", await local(bgra, width, height, t).ConfigureAwait(false)); }
                    catch (ScreenReadException error) { throw new Audio2FaceHostException("worker.unavailable", error.Message); }
                }
                // Encoded once, below normal priority: the conversation's own work on this PC comes first.
                jpeg ??= await LowPriority.RunAsync(() => Jpeg(bgra, width, height), t, "Martlet screen reading").ConfigureAwait(false);
                var (route, lines) = await WorkSharingRoster.WatchedOnce(host.HostId, "reading", read(host, jpeg, t)).ConfigureAwait(false);
                return ($"{host.HostId}'s Reading role" + (OptionalExtras.ReadingModelOf(route.ModelId) is { } model ? $" ({model.Name})" : ""), lines);
            }, WorkSharingRoster.Classify, DateTimeOffset.UtcNow + wait, null, token, WorkPriority.Background).ConfigureAwait(false);
            last = engine;
            return lines;
        }
        catch (WorkPreemptedException) when (!token.IsCancellationRequested)
        {
            // Background priority: a computer that keeps its graphics card for a live turn, or for its owner (a friend's host),
            // was passed over, and no other place took the read.
            throw new ScreenReadException(targets.Count == 1 && tried.Host is { Shared: true } shared
                ? $"{shared.HostId} is busy with its owner's own work; Martlet reads again in a moment."
                : "The computers in the Reading list keep their graphics cards for other work now; Martlet reads again in a moment.", busy: true);
        }
        catch (Audio2FaceHostException error) when (error.Code == "job.busy")
        {
            throw new ScreenReadException(error.OwnerFirst ? $"{Name(tried)} is busy with its owner's own work; Martlet reads again in a moment."
                : targets.Count == 1 ? $"{Name(tried)}'s Reading role is busy."
                : $"Every place in the Reading list is busy ({targets.Count}); Martlet reads again in a moment.", busy: true, error);
        }
        catch (Audio2FaceHostException error) when (error.Code is "host.unreachable" or "worker.unavailable")
        {
            throw new ScreenReadException(error.Message, inner: error);
        }
        catch (Audio2FaceHostException error)
        {
            throw new ScreenReadException($"{Name(tried)}'s Reading role: {error.Message}", inner: error);
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            throw new ScreenReadException($"{Name(tried)} stopped answering ({error.Message}).", inner: error);
        }
        finally
        {
            if (jpeg is not null) Array.Clear(jpeg);
        }
    }

    private static string Name((ReadingTarget Target, PairedHost? Host) place) => place.Host?.HostId ?? "This PC";

    /// <summary>The places a read tries, first to last (<see cref="ReadingList.Targets"/>): the list's members that are on, kept
    /// for this PC and paired here, Windows OCR as This PC, each computer once.</summary>
    internal IReadOnlyList<(ReadingTarget Target, PairedHost? Host)> Targets()
    {
        var targets = ReadingList.Targets(dataDirectory);
        return targets.Count > 0 ? targets
            : throw new ScreenReadException("Nothing in the Reading list can read on this PC now. Check Companion › Reading.");
    }

    /// <summary>The Reading role's state on <paramref name="host"/> ("ready", "loading" or "failed").</summary>
    internal static async Task<string> StatusAsync(PairedHost host, CancellationToken token)
    {
        using var reader = new PoolScreenTextReader(null);
        try
        {
            var (route, connection) = await reader.OpenAsync(host, token).ConfigureAwait(false);
            var answer = await connection.OcrStatusAsync(route, token).ConfigureAwait(false);
            return answer.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String ? state.GetString()! : "unknown";
        }
        catch (Audio2FaceHostException error) when (error.Code is "host.unreachable" or "worker.unavailable")
        {
            throw new ScreenReadException(error.Message, inner: error);
        }
        catch (Exception error) when (error is Audio2FaceHostException or HttpRequestException or IOException)
        {
            throw new ScreenReadException($"{host.HostId}'s Reading role: {error.Message}", inner: error);
        }
    }

    // One read on one computer. A failure other than busy (or a bad answer) drops its connection, so the next read connects
    // again; the queue passes over a computer that is busy or doesn't answer.
    private async Task<(HostRoute, IReadOnlyList<ReadLine>)> ReadOnAsync(PairedHost host, byte[] jpeg, CancellationToken token)
    {
        var (route, connection) = await OpenAsync(host, token).ConfigureAwait(false);
        try
        {
            return (route, Lines(await connection.OcrReadAsync(route, jpeg, "image/jpeg", token).ConfigureAwait(false)));
        }
        catch (Exception error) when (error is not (OperationCanceledException or ScreenReadException or
            Audio2FaceHostException { Code: "job.busy" or "job.preempted" }))
        {
            Drop(host.HostId, connection);
            throw;
        }
    }

    /// <summary>The screenshot as a JPEG for the host: quality 90 keeps small text sharp; a busy full-size screenshot that would
    /// pass the host's limit is encoded at a lower quality instead. WPF imaging runs on any thread here.</summary>
    internal static byte[] Jpeg(byte[] bgra, int width, int height)
    {
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, bgra, width * 4);
        foreach (var quality in (int[])[90, 75, 60])
        {
            var encoder = new JpegBitmapEncoder { QualityLevel = quality };
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            if (stream.Length <= Audio2FaceHostConnection.OcrMaximumImageBytes) return stream.ToArray();
            Array.Clear(stream.GetBuffer());
        }
        throw new ScreenReadException("The screenshot is too large for the Reading role.");
    }

    /// <summary>The lines of a Reading role's answer: <c>lines</c>, each <c>text</c> and <c>box</c> [x, y, width, height].</summary>
    internal static IReadOnlyList<ReadLine> Lines(JsonElement answer)
    {
        if (answer.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            throw new ScreenReadException("The Reading role couldn't read the screenshot: " +
                (error.TryGetProperty("summary", out var summary) ? summary.GetString() : "it failed") + ".");
        if (!answer.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Array)
            throw new ScreenReadException("The Reading role returned an invalid answer.");
        var found = new List<ReadLine>();
        foreach (var line in lines.EnumerateArray().Take(ScreenText.MaximumLines * 4))
        {
            if (!line.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String ||
                !line.TryGetProperty("box", out var box) || box.ValueKind != JsonValueKind.Array || box.GetArrayLength() != 4)
                throw new ScreenReadException("The Reading role returned an invalid answer.");
            var b = box.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Number ? (int)Math.Round(v.GetDouble()) : 0).ToArray();
            found.Add(new(text.GetString()!, b[0], b[1], b[2], b[3]));
        }
        return found;
    }

    // The computer's kept connection and its Reading route; a new one when there is none. A computer that doesn't answer, or
    // doesn't run the role, refuses as unavailable (host.unreachable, worker.unavailable), so the queue passes it over.
    private async Task<(HostRoute Route, Audio2FaceHostConnection Connection)> OpenAsync(PairedHost host, CancellationToken token)
    {
        lock (gate)
            if (open.TryGetValue(host.HostId, out var known)) return known;
        Audio2FaceHostConnection? connection = null;
        try
        {
            connection = ClusterSync.Connect(host.Pairing);
            var routes = await connection.ReadRoutesAsync(token).ConfigureAwait(false);
            var route = routes.FirstOrDefault(r => r.RouteId == Audio2FaceHostConnection.OcrRouteId) ?? throw new Audio2FaceHostException(
                "worker.unavailable", $"{host.HostId} doesn't run the Reading role. Set it up there in Companion › Reading.");
            lock (gate)
            {
                if (open.TryGetValue(host.HostId, out var raced)) return raced;
                open[host.HostId] = (route, connection);
            }
            var kept = (route, connection);
            connection = null;
            return kept;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (ClusterSync.IsHostFailure(error) && error is not Audio2FaceHostException { Code: "worker.unavailable" })
        {
            throw new Audio2FaceHostException("host.unreachable", $"{host.HostId} isn't reachable ({error.Message}).");
        }
        finally { connection?.Dispose(); }
    }

    private void Drop(string host, Audio2FaceHostConnection connection)
    {
        lock (gate)
            if (open.TryGetValue(host, out var kept) && ReferenceEquals(kept.Connection, connection)) open.Remove(host);
        connection.Dispose();
    }

    public void Dispose()
    {
        (HostRoute, Audio2FaceHostConnection Connection)[] all;
        lock (gate)
        {
            all = [.. open.Values];
            open.Clear();
        }
        foreach (var kept in all) kept.Connection.Dispose();
        windows?.Dispose();
    }
}

/// <summary>
/// Reads the text on the screen while Martlet watches it (docs/READING.md), one read at a time and never on the UI thread or a
/// reply's path. A capture is read when no read is running and the picture changed, or the last read is older than
/// <see cref="Refresh"/> (small text such as a score can change without changing the coarse picture much). Each read keeps
/// only its text; the pixels are zeroed when it ends.
/// </summary>
internal sealed class ScreenReader : IDisposable
{
    /// <summary>A screen that looks the same is read again after this long.</summary>
    internal static TimeSpan Refresh => TimeSpan.FromSeconds(9);
    /// <summary>The picture changed at least this much (0 to 1) since the capture before: read it now.</summary>
    internal const double PictureChange = 0.002;

    private readonly IScreenTextReader reader;
    private readonly TimeProvider clock;
    private readonly CancellationTokenSource lifetime = new();
    private Task<ScreenRead?>? running;
    private long? lastStarted;
    private volatile ScreenRead? latest;
    private volatile string? problem;

    internal ScreenReader(IScreenTextReader reader, TimeProvider? clock = null)
    {
        this.reader = reader;
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>The newest read of this PC's watching, for Companion › Reading (null until one finished).</summary>
    internal static string? LastReport { get; set; }

    /// <summary>The reader for this PC's list in Companion › Reading (<see cref="ReadingList"/>): Windows OCR on this PC and the
    /// Reading role of your computers, in the list's order; null when nothing in the list is on (reading is off).</summary>
    internal static ScreenReader? For(string? dataDirectory, TimeProvider? clock = null) =>
        ReadingList.Load(dataDirectory).NoneOn ? null : new(new PoolScreenTextReader(dataDirectory), clock);

    internal string Engine => reader.Engine;
    internal bool Busy => running is { IsCompleted: false };
    /// <summary>The newest read; null until one finished.</summary>
    internal ScreenRead? Latest => latest;
    /// <summary>Why the last read failed; null after one that worked.</summary>
    internal string? Problem => problem;

    /// <summary>Starts reading <paramref name="frame"/> unless a read is running, or the picture barely changed and the last read
    /// is recent. With <paramref name="fullSize"/> (the same look at full size, <see cref="IScreenGlancer.CaptureText"/>), that
    /// picture is taken off the UI thread and read instead: small text survives only at full size. Returns the read (null when
    /// it failed or the full-size look was skipped), or null when it didn't start.</summary>
    internal Task<ScreenRead?>? Offer(ScreenFrame frame, Func<ScreenFrame?>? fullSize = null)
    {
        if (Busy || lifetime.IsCancellationRequested) return null;
        if (latest is not null && frame.Change < PictureChange && lastStarted is { } at && clock.GetElapsedTime(at) < Refresh) return null;
        if (fullSize is not null)
        {
            lastStarted = clock.GetTimestamp();
            return running = Task.Run(async () =>
            {
                if (lifetime.IsCancellationRequested) return null;
                ScreenFrame? full;
                try { full = fullSize(); }
                catch (Exception error) when (error is InvalidOperationException or ArgumentException or OutOfMemoryException or
                    System.Runtime.InteropServices.ExternalException)
                {
                    problem = $"Couldn't take a full-size picture of the screen ({error.Message}).";
                    return null;
                }
                if (full is null) return null;
                var (width, height) = (full.Width, full.Height);
                if (lifetime.IsCancellationRequested)
                {
                    full.Clear();
                    return null;
                }
                return full.TakePixels() is { } taken ? await ReadAsync(taken, width, height).ConfigureAwait(false) : null;
            });
        }
        if (frame.CopyPixels() is not { } pixels) return null;
        lastStarted = clock.GetTimestamp();
        return running = ReadAsync(pixels, frame.Width, frame.Height);
    }

    /// <summary>Reads one picture now (Companion › Reading's Read my screen now), even while watching reads too.</summary>
    internal async Task<ScreenRead> ReadNowAsync(byte[] bgra, int width, int height, CancellationToken token)
    {
        var started = clock.GetTimestamp();
        var lines = ScreenText.Order(await reader.ReadAsync(bgra, width, height, token).ConfigureAwait(false));
        return new(lines, ScreenText.Join(lines), 0, clock.GetElapsedTime(started), reader.Engine, clock.GetUtcNow(), width, height);
    }

    private async Task<ScreenRead?> ReadAsync(byte[] pixels, int width, int height)
    {
        var started = clock.GetTimestamp();
        try
        {
            var lines = ScreenText.Order(await reader.ReadAsync(pixels, width, height, lifetime.Token).ConfigureAwait(false));
            var text = ScreenText.Join(lines);
            var before = latest;
            var read = new ScreenRead(lines, text, before is null ? 0 : ScreenText.Change(before.Text, text),
                clock.GetElapsedTime(started), reader.Engine, clock.GetUtcNow(), width, height);
            latest = read;
            problem = null;
            return read;
        }
        catch (ScreenReadException error)
        {
            if (!error.Busy) problem = error.Message;
            return null;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.ExternalException)
        {
            problem = $"Couldn't read the screen ({error.Message}).";
            return null;
        }
        finally { Array.Clear(pixels); }
    }

    public void Dispose()
    {
        lifetime.Cancel();
        reader.Dispose();
    }
}
