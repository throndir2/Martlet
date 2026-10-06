using System.Buffers.Binary;
using System.Globalization;

namespace Martlet.Core.Pictures;

/// <summary>
/// Draws one picture from a description (docs/PICTURES.md). Implementations: ComfyUI (a URL the owner runs, or Martlet's own
/// <c>pictures</c> host role through its gateway route <c>martlet.gateway.picture.v1</c>), OpenRouter's image API and NVIDIA
/// Build's image models; <see cref="FixturePictureMaker"/> is a deterministic FIXTURE - NOT AI stand-in for tests. A picture
/// takes seconds to a minute, so callers run it as a background job and report <see cref="PictureProgress"/>.
/// </summary>
public interface IPictureMaker
{
    /// <summary>Where pictures are drawn, in words ("ComfyUI at http://studio:8188", "OpenRouter (google/...)").</summary>
    string Where { get; }

    /// <summary>Whether a picture can be drawn now. Never throws for "not set up": it returns <see cref="PictureMakerAvailability.Available"/>
    /// false with a reason the owner can act on.</summary>
    Task<PictureMakerAvailability> GetAvailabilityAsync(CancellationToken cancellationToken);

    /// <summary>Draws one picture. Cancelling <paramref name="cancellationToken"/> cancels it where it runs (when the place can) and
    /// throws <see cref="OperationCanceledException"/>; other failures throw <see cref="PictureException"/> with a stable code.</summary>
    Task<PictureResult> GenerateAsync(PictureRequest request, IProgress<PictureProgress>? progress, CancellationToken cancellationToken);
}

/// <summary>A picture's shape. Every size is a multiple of 64 between 768 and 1344 so every place accepts it.</summary>
public enum PictureShape { Square, Landscape, Portrait, Wide, Tall }

public static class PictureShapes
{
    public static (int Width, int Height) Size(PictureShape shape) => shape switch
    {
        PictureShape.Landscape => (1216, 832),
        PictureShape.Portrait => (832, 1216),
        PictureShape.Wide => (1344, 768),
        PictureShape.Tall => (768, 1344),
        _ => (1024, 1024)
    };

    /// <summary>The aspect ratio as providers name it ("1:1", "3:2", "2:3", "16:9", "9:16").</summary>
    public static string Ratio(PictureShape shape) => shape switch
    {
        PictureShape.Landscape => "3:2",
        PictureShape.Portrait => "2:3",
        PictureShape.Wide => "16:9",
        PictureShape.Tall => "9:16",
        _ => "1:1"
    };

    public static string Name(PictureShape shape) => shape.ToString().ToLowerInvariant();

    public static PictureShape? Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "square" or "1:1" => PictureShape.Square,
        "landscape" or "3:2" or "4:3" or "horizontal" => PictureShape.Landscape,
        "portrait" or "2:3" or "3:4" or "vertical" => PictureShape.Portrait,
        "wide" or "16:9" or "widescreen" or "banner" => PictureShape.Wide,
        "tall" or "9:16" or "phone" or "story" => PictureShape.Tall,
        _ => null
    };
}

/// <summary>One picture to draw: an English description written by Martlet (<see cref="Prompt"/>), what to keep out of it
/// (used where the place supports it), its shape and an optional seed.</summary>
public sealed record PictureRequest
{
    public const int MaximumPromptCharacters = 2_000;
    public const int MaximumNegativeCharacters = 500;

    public required string Prompt { get; init; }
    public string? NegativePrompt { get; init; }
    public PictureShape Shape { get; init; } = PictureShape.Square;
    /// <summary>Optional seed for a repeatable picture where the place supports it; a random one is chosen when null.</summary>
    public long? Seed { get; init; }

    public int Width => PictureShapes.Size(Shape).Width;
    public int Height => PictureShapes.Size(Shape).Height;

    /// <summary>Throws <see cref="PictureException"/> (<c>request.invalid</c>) when a field is outside its bounds.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Prompt) || Prompt.Length > MaximumPromptCharacters)
            throw new PictureException(PictureErrorCodes.RequestInvalid, $"A picture's description must be 1-{MaximumPromptCharacters} characters.");
        if (NegativePrompt is { Length: > MaximumNegativeCharacters })
            throw new PictureException(PictureErrorCodes.RequestInvalid, $"What to leave out must be at most {MaximumNegativeCharacters} characters.");
        if (!Enum.IsDefined(Shape)) throw new PictureException(PictureErrorCodes.RequestInvalid, "The picture's shape is unknown.");
        if (Seed is < 0) throw new PictureException(PictureErrorCodes.RequestInvalid, "The seed must not be negative.");
    }
}

/// <summary>How far a picture is: queued (with its place in the queue), drawing (fraction when known) or fetching.</summary>
public sealed record PictureProgress(string Stage, double Fraction = 0, int? QueuePosition = null)
{
    public const string Queued = "queued", Drawing = "drawing", Fetching = "fetching";

    public string Describe() => Stage switch
    {
        Queued when QueuePosition is > 0 => $"Waiting to draw ({QueuePosition.Value.ToString(CultureInfo.InvariantCulture)} ahead)",
        Queued => "Waiting to draw",
        Fetching => "Fetching the picture",
        _ when Fraction > 0 => $"Drawing ({Math.Round(Fraction * 100).ToString(CultureInfo.InvariantCulture)}%)",
        _ => "Drawing"
    };
}

/// <summary>A drawn picture: its encoded bytes (PNG, JPEG or WebP), their media type and size, the seed when known, what drew
/// it (<see cref="Engine"/> such as "comfyui", "openrouter", "nvidia-build" or "fixture"; <see cref="Model"/>), where, and how
/// long it took.</summary>
public sealed record PictureResult
{
    public required byte[] Image { get; init; }
    public required string MediaType { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public long? Seed { get; init; }
    public required string Engine { get; init; }
    public string Model { get; init; } = "";
    public string Where { get; init; } = "";
    public bool Fixture { get; init; }
    public TimeSpan Took { get; init; }
}

public sealed record PictureMakerAvailability(bool Available, string? Reason, string? Where, bool Fixture = false)
{
    public static PictureMakerAvailability Unavailable(string reason) => new(false, reason, null);
}

/// <summary>Stable failure codes of <see cref="PictureException"/>.</summary>
public static class PictureErrorCodes
{
    /// <summary>Pictures aren't set up, or the place isn't reachable or ready.</summary>
    public const string Unavailable = "pictures.unavailable";
    /// <summary>The place is busy with other pictures.</summary>
    public const string Busy = "pictures.busy";
    /// <summary>The key is missing or was refused.</summary>
    public const string NotAuthorized = "pictures.not_authorized";
    /// <summary>The place refused this description (a content filter).</summary>
    public const string Refused = "pictures.refused";
    /// <summary>A request field is outside its bounds, or the place rejected the workflow or model.</summary>
    public const string RequestInvalid = "request.invalid";
    public const string TimedOut = "pictures.timeout";
    public const string Failed = "pictures.failed";
}

public sealed class PictureException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

/// <summary>Recognizes PNG, JPEG and WebP bytes and reads their size without decoding them.</summary>
public static class PictureImages
{
    public const string Png = "image/png", Jpeg = "image/jpeg", Webp = "image/webp";
    /// <summary>The largest picture Martlet keeps (a creation asset).</summary>
    public const int MaximumBytes = 24 * 1024 * 1024;

    public static IReadOnlyList<string> MediaTypes { get; } = [Png, Jpeg, Webp];

    /// <summary>The media type and size of <paramref name="bytes"/>, or null when they aren't a PNG, JPEG or WebP picture.</summary>
    public static (string MediaType, int Width, int Height)? Probe(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 24 && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }) &&
            bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
            return Sized(Png, BinaryPrimitives.ReadInt32BigEndian(bytes[16..]), BinaryPrimitives.ReadInt32BigEndian(bytes[20..]));
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xD8) return JpegSize(bytes);
        if (bytes.Length >= 30 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8)) return WebpSize(bytes);
        return null;
    }

    private static (string, int, int)? Sized(string type, int width, int height) =>
        width is > 0 and <= 16_384 && height is > 0 and <= 16_384 ? (type, width, height) : null;

    private static (string, int, int)? JpegSize(ReadOnlySpan<byte> bytes)
    {
        var at = 2;
        while (at + 9 < bytes.Length)
        {
            if (bytes[at] != 0xFF) return null;
            var marker = bytes[at + 1];
            if (marker == 0xFF) { at++; continue; }
            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) { at += 2; continue; }
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[(at + 2)..]);
            if (length < 2) return null;
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                return Sized(Jpeg, BinaryPrimitives.ReadUInt16BigEndian(bytes[(at + 7)..]), BinaryPrimitives.ReadUInt16BigEndian(bytes[(at + 5)..]));
            at += 2 + length;
        }
        return null;
    }

    private static (string, int, int)? WebpSize(ReadOnlySpan<byte> bytes)
    {
        var chunk = bytes.Slice(12, 4);
        if (chunk.SequenceEqual("VP8X"u8))
            return Sized(Webp, 1 + (bytes[24] | bytes[25] << 8 | bytes[26] << 16), 1 + (bytes[27] | bytes[28] << 8 | bytes[29] << 16));
        if (chunk.SequenceEqual("VP8L"u8) && bytes[20] == 0x2F)
        {
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(bytes[21..]);
            return Sized(Webp, (int)(bits & 0x3FFF) + 1, (int)(bits >> 14 & 0x3FFF) + 1);
        }
        if (chunk.SequenceEqual("VP8 "u8) && bytes[23] == 0x9D && bytes[24] == 0x01 && bytes[25] == 0x2A)
            return Sized(Webp, BinaryPrimitives.ReadUInt16LittleEndian(bytes[26..]) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(bytes[28..]) & 0x3FFF);
        return null;
    }

    /// <summary>Checks a picture a place returned: a PNG, JPEG or WebP no larger than <see cref="MaximumBytes"/>.</summary>
    public static (string MediaType, int Width, int Height) Require(byte[] bytes, string where)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length > MaximumBytes) throw new PictureException(PictureErrorCodes.Failed, $"{where} returned a picture that is too large.");
        return Probe(bytes) ?? throw new PictureException(PictureErrorCodes.Failed, $"{where} returned something that isn't a PNG, JPEG or WebP picture.");
    }
}
