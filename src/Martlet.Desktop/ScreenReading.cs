using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Avatar.Hosting;
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

/// <summary>A paired computer's Reading role (RapidOCR on its processor) through its gateway (route
/// <c>martlet.gateway.ocr.v1</c>). The screenshot goes there as a JPEG, is read in memory and is not kept. The computer named in
/// Companion › Reading reads; without one, the first paired computer that offers the route.</summary>
internal sealed class HostScreenTextReader(string dataDirectory, string? hostId) : IScreenTextReader
{
    private (PairedHost Host, HostRoute Route, Audio2FaceHostConnection Connection)? open;

    public string Engine => open is { } found ? $"{found.Host.HostId}'s Reading role"
        : hostId is null ? "Martlet's Reading role" : $"{hostId}'s Reading role";

    public async Task<IReadOnlyList<ReadLine>> ReadAsync(byte[] bgra, int width, int height, CancellationToken token)
    {
        // Encoded below normal priority: the conversation's own work on this PC comes first.
        var jpeg = await LowPriority.RunAsync(() => Jpeg(bgra, width, height), token, "Martlet screen reading").ConfigureAwait(false);
        var (host, route, connection) = await OpenAsync(token).ConfigureAwait(false);
        try
        {
            return Lines(await connection.OcrReadAsync(route, jpeg, "image/jpeg", token).ConfigureAwait(false));
        }
        catch (Audio2FaceHostException error)
        {
            if (error.Code == "job.busy")
                throw new ScreenReadException(error.OwnerFirst ? $"{host.HostId} is busy with its owner's own work; Martlet reads again in a moment."
                    : $"{host.HostId}'s Reading role is busy.", busy: true, error);
            Dispose();
            throw new ScreenReadException($"{host.HostId}'s Reading role: {error.Message}", inner: error);
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            Dispose();
            throw new ScreenReadException($"{host.HostId} stopped answering ({error.Message}).", inner: error);
        }
        finally { Array.Clear(jpeg); }
    }

    /// <summary>The Reading role's state on that computer ("ready" or "loading").</summary>
    internal async Task<string> StatusAsync(CancellationToken token)
    {
        var (host, route, connection) = await OpenAsync(token).ConfigureAwait(false);
        try
        {
            var answer = await connection.OcrStatusAsync(route, token).ConfigureAwait(false);
            return answer.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String ? state.GetString()! : "unknown";
        }
        catch (Exception error) when (error is Audio2FaceHostException or HttpRequestException or IOException)
        {
            Dispose();
            throw new ScreenReadException($"{host.HostId}'s Reading role: {error.Message}", inner: error);
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

    private async Task<(PairedHost, HostRoute, Audio2FaceHostConnection)> OpenAsync(CancellationToken token)
    {
        if (open is { } known) return known;
        IReadOnlyList<PairedHost> hosts;
        try { hosts = HostRegistry.Load(dataDirectory); }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new ScreenReadException(error.Message, inner: error);
        }
        // Without a computer named, only your own: a host a friend shares reads only once you chose it in Companion › Reading.
        foreach (var host in hosts.Where(h => hostId is null ? !h.Shared : h.HostId == hostId))
        {
            Audio2FaceHostConnection? connection = null;
            try
            {
                connection = ClusterSync.Connect(host.Pairing);
                var routes = await connection.ReadRoutesAsync(token).ConfigureAwait(false);
                if (routes.FirstOrDefault(r => r.RouteId == Audio2FaceHostConnection.OcrRouteId) is { } route)
                {
                    open = (host, route, connection);
                    connection = null;
                    return open.Value;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (ClusterSync.IsHostFailure(error))
            {
                if (hostId is not null) throw new ScreenReadException($"{hostId} isn't reachable ({error.Message}).", inner: error);
            }
            finally { connection?.Dispose(); }
        }
        throw new ScreenReadException(hostId is null
            ? "No paired computer runs the Reading role. Set it up in Companion › Reading."
            : $"{hostId} doesn't run the Reading role. Set it up there in Companion › Reading.");
    }

    public void Dispose()
    {
        open?.Connection.Dispose();
        open = null;
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

    /// <summary>The reader for this PC's choice in Companion › Reading (reading.json): Windows OCR on this PC (the default), a
    /// host's Reading role, or null when reading is off.</summary>
    internal static ScreenReader? For(string? dataDirectory, TimeProvider? clock = null)
    {
        var settings = ReadingSettings.Load(dataDirectory);
        return settings.Place switch
        {
            ReadingPlace.ThisPc => new(new WindowsScreenTextReader(), clock),
            ReadingPlace.Host when dataDirectory is not null => new(new HostScreenTextReader(dataDirectory, settings.HostId), clock),
            _ => null
        };
    }

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
