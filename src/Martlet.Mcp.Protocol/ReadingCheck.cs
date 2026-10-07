using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using Martlet.Core.Reading;
using Martlet.Discord.Calls;

namespace Martlet.Mcp;

/// <summary>reading_check (docs/READING.md): Companion › Reading for a data directory (reading.json: where Martlet reads the text
/// on the screen) and a real read of a drawn test picture with known text (a HUD-like line, a score, "VICTORY" and a chat
/// line). Windows OCR on this PC reads it through the same reader the desktop uses; with endpoint (a Reading role's worker on
/// loopback, such as http://127.0.0.1:50087/) that worker reads the same picture as a PNG through its POST /read contract.
/// Both go through Martlet's reading order and text join (<see cref="ScreenText"/>). The real screen is never captured, and no
/// text from a real screen is returned.</summary>
internal static class ReadingCheck
{
    /// <summary>The words drawn on the test picture, and the ones a read must find.</summary>
    internal static readonly string[] Expected = ["HEALTH", "87", "Score", "12450", "VICTORY", "Player2", "eliminated"];

    internal static async Task<object> RunAsync(string? dataDirectory, string? endpoint, CancellationToken cancellation)
    {
        var (settings, state) = ReadingSettings.Read(dataDirectory);
        var (pixels, width, height) = await RenderAsync().ConfigureAwait(false);
        var windows = await WindowsAsync(pixels, width, height, cancellation).ConfigureAwait(false);
        var worker = endpoint is null ? null : await WorkerAsync(Endpoint(endpoint), pixels, width, height, cancellation).ConfigureAwait(false);
        return new
        {
            settings = dataDirectory is null ? null : new
            {
                file = state, place = settings.Place.ToString(), on = settings.On, hostId = settings.HostId, describe = settings.Describe()
            },
            picture = new { width, height, expected = Expected },
            windowsOcr = windows,
            worker
        };
    }

    private static async Task<object> WindowsAsync(byte[] pixels, int width, int height, CancellationToken cancellation)
    {
        var available = await Task.Run(() => WindowsCallTextReader.Available, cancellation).ConfigureAwait(false);
        if (!available) return new { available, ok = false, problem = "Windows has no text recognition (OCR) language." };
        var reader = new WindowsCallTextReader();
        // The first read loads Windows' engine; the second is what each screenshot costs while watching.
        var watch = Stopwatch.StartNew();
        var lines = await reader.ReadAsync(pixels, width, height, cancellation).ConfigureAwait(false);
        var first = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        lines = await reader.ReadAsync(pixels, width, height, cancellation).ConfigureAwait(false);
        var again = watch.Elapsed.TotalMilliseconds;
        return Result(lines.Select(line => new ReadLine(line.Text, line.Box.X, line.Box.Y, line.Box.Width, line.Box.Height)),
            new { available, firstMilliseconds = Math.Round(first), milliseconds = Math.Round(again) });
    }

    private static async Task<object> WorkerAsync(Uri endpoint, byte[] pixels, int width, int height, CancellationToken cancellation)
    {
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };
        try
        {
            using var status = await http.GetAsync(new Uri(endpoint, "status"), cancellation).ConfigureAwait(false);
            var statusJson = JsonDocument.Parse(await status.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false)).RootElement.Clone();
            using var content = new ByteArrayContent(Png(pixels, width, height));
            content.Headers.ContentType = new("image/png");
            var watch = Stopwatch.StartNew();
            using var response = await http.PostAsync(new Uri(endpoint, "read"), content, cancellation).ConfigureAwait(false);
            var milliseconds = Math.Round(watch.Elapsed.TotalMilliseconds);
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false)).RootElement;
            if (!response.IsSuccessStatusCode || !body.TryGetProperty("lines", out var found) || found.ValueKind != JsonValueKind.Array)
                return new { ok = false, status = statusJson, httpStatus = (int)response.StatusCode, answer = body.Clone() };
            var lines = found.EnumerateArray().Select(line =>
            {
                var box = line.GetProperty("box").EnumerateArray().Select(v => (int)Math.Round(v.GetDouble())).ToArray();
                return new ReadLine(line.GetProperty("text").GetString() ?? "", box[0], box[1], box[2], box[3]);
            }).ToList();
            return Result(lines, new
            {
                status = statusJson, roundTripMilliseconds = milliseconds,
                workerMilliseconds = body.TryGetProperty("milliseconds", out var took) ? took.GetDouble() : (double?)null
            });
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException ||
            error is TaskCanceledException && !cancellation.IsCancellationRequested)
        {
            return new { ok = false, problem = error.Message };
        }
    }

    private static object Result(IEnumerable<ReadLine> lines, object timing)
    {
        var ordered = ScreenText.Order(lines);
        var text = ScreenText.Join(ordered);
        var words = ScreenText.Words(text);
        var missing = Expected.Where(word => !words.Contains(word.ToLowerInvariant())).ToArray();
        return new
        {
            ok = missing.Length == 0, lines = ordered.Count, text, missing, timing,
            changeFromEmpty = ScreenText.Change("", text), changeFromSame = ScreenText.Change(text, text)
        };
    }

    private static Uri Endpoint(string value)
    {
        if (!Uri.TryCreate(value.EndsWith('/') ? value : value + "/", UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttp ||
            !System.Net.IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out var address) || !System.Net.IPAddress.IsLoopback(address) ||
            endpoint.AbsolutePath != "/")
            throw new ArgumentException("endpoint must be a numeric loopback address such as http://127.0.0.1:50087/.");
        return endpoint;
    }

    // A 1024 x 576 game-like screen: a health line and a score at the top, a big banner in the middle and a chat line below.
    private static Task<(byte[] Pixels, int Width, int Height)> RenderAsync() => Task.Run(() =>
    {
        using var picture = new DiscordCallCheck.FixturePicture(1024, 576);
        picture.Fill(0, 0, picture.Width, picture.Height, (20, 30, 40));
        picture.Text(40, 36, "HEALTH 87 / 100", 28, (255, 80, 80));
        picture.Text(700, 36, "Score: 12450", 28, (255, 255, 255));
        picture.Text(380, 250, "VICTORY", 56, (255, 215, 0));
        picture.Text(40, 516, "Player2 eliminated Player5", 18, (200, 200, 200));
        return (picture.Pixels(), picture.Width, picture.Height);
    });

    /// <summary>An 8-bit RGB PNG of a BGRA32 picture (no filtering; zlib from .NET).</summary>
    internal static byte[] Png(byte[] bgra, int width, int height)
    {
        using var raw = new MemoryStream();
        using (var zlib = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[1 + width * 3];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var i = (y * width + x) * 4;
                    row[1 + x * 3] = bgra[i + 2];
                    row[2 + x * 3] = bgra[i + 1];
                    row[3 + x * 3] = bgra[i];
                }
                zlib.Write(row);
            }
        }
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 2;
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", raw.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();

        static void Chunk(Stream stream, string type, byte[] data)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
            stream.Write(length);
            var typed = new byte[4 + data.Length];
            System.Text.Encoding.ASCII.GetBytes(type, typed);
            data.CopyTo(typed, 4);
            stream.Write(typed);
            Span<byte> crc = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typed));
            stream.Write(crc);
        }

        static uint Crc32(byte[] data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var b in data)
            {
                crc ^= b;
                for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
            return ~crc;
        }
    }
}
