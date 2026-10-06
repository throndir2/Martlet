using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Martlet.Core.Pictures;

/// <summary>
/// FIXTURE - NOT AI: a deterministic <see cref="IPictureMaker"/> for tests and plumbing checks. It "draws" a PNG gradient (a
/// quarter of the requested size) whose colours come from the description's hash, reports each stage, honours cancellation and
/// returns what a real picture has. Nothing it makes comes from a model, and <see cref="PictureResult.Fixture"/> is true.
/// </summary>
public sealed class FixturePictureMaker(TimeSpan? stageDelay = null) : IPictureMaker
{
    public const string Engine = "fixture";
    public const string Model = "FIXTURE - NOT AI gradient";
    private readonly TimeSpan delay = stageDelay ?? TimeSpan.FromMilliseconds(20);

    public string Where => "the FIXTURE - NOT AI picture maker";

    public Task<PictureMakerAvailability> GetAvailabilityAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new PictureMakerAvailability(true, null, Where, Fixture: true));
    }

    public async Task<PictureResult> GenerateAsync(PictureRequest request, IProgress<PictureProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        var started = DateTimeOffset.UtcNow;
        progress?.Report(new(PictureProgress.Queued));
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        progress?.Report(new(PictureProgress.Drawing, 0.5));
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(request.Prompt + "\n" + request.Seed));
        var (width, height) = (request.Width / 4, request.Height / 4);
        var png = Png(width, height, hash);
        progress?.Report(new(PictureProgress.Fetching, 0.95));
        return new()
        {
            Image = png, MediaType = PictureImages.Png, Width = width, Height = height,
            Seed = request.Seed ?? BinaryPrimitives.ReadInt64LittleEndian(hash) & long.MaxValue,
            Engine = Engine, Model = Model, Where = Where, Fixture = true, Took = DateTimeOffset.UtcNow - started
        };
    }

    /// <summary>An RGB PNG gradient between two colours taken from <paramref name="hash"/>.</summary>
    public static byte[] Png(int width, int height, ReadOnlySpan<byte> hash)
    {
        var raw = new byte[height * (1 + width * 3)];
        for (var y = 0; y < height; y++)
        {
            var row = y * (1 + width * 3);
            for (var x = 0; x < width; x++)
            {
                var t = (x + y) / (double)Math.Max(1, width + height - 2);
                for (var c = 0; c < 3; c++)
                    raw[row + 1 + x * 3 + c] = (byte)(hash[c] + (hash[3 + c] - hash[c]) * t);
            }
        }
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(raw);
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 2;
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        Span<byte> four = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(four, data.Length);
        stream.Write(four);
        var typed = new byte[4 + data.Length];
        Encoding.ASCII.GetBytes(type, typed);
        data.CopyTo(typed, 4);
        stream.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(four, Crc(typed));
        stream.Write(four);
    }

    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }
}
