using System.Buffers.Binary;
using System.IO.Compression;

namespace Martlet.Perception;

public static class PerceptionProtocol
{
    public const string ContractId = "martlet.perception.worker";
    public const int MaximumImageBytes = 4 * 1024 * 1024;
    public const int MaximumImageWidth = 4096;
    public const int MaximumImageHeight = 4096;
    public const long MaximumImagePixels = 8_294_400;
    public const int MaximumQuestionCharacters = 512;
    public const int MaximumQuestionUtf8Bytes = 1024;
    public const int MaximumOcrRegions = 128;
    public const int MaximumRegionTextCharacters = 1024;
    public const int MaximumRegionTextUtf8Bytes = 2048;
    public const int MaximumOutputUtf8Bytes = 16 * 1024;
    public static readonly TimeSpan MaximumJobDuration = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan MaximumFrameAge = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaximumClockSkew = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MaximumCancelDuration = TimeSpan.FromMilliseconds(500);
}

public enum PerceptionCaptureScope
{
    SelectedWindow
}

public sealed record SelectedWindowSource
{
    public required Guid SelectionId { get; init; }
    public required string SourceRevision { get; init; }
    public required string CapturePermissionRevision { get; init; }

    internal void Validate()
    {
        PerceptionWorkerGuard.Require(SelectionId != Guid.Empty);
        PerceptionWorkerGuard.Sha256(SourceRevision);
        PerceptionWorkerGuard.Sha256(CapturePermissionRevision);
    }
}

public enum PerceptionFrameContentKind
{
    InlinePng,
    EphemeralGatewayReference
}

public sealed class SelectedWindowFrameContent
{
    private readonly ReadOnlyMemory<byte> inlineBytes;

    private SelectedWindowFrameContent(
        PerceptionFrameContentKind kind,
        ReadOnlyMemory<byte> bytes,
        string? referenceId,
        string sha256,
        int byteCount,
        int width,
        int height,
        DateTimeOffset? referenceExpiresAtUtc)
    {
        Kind = kind;
        inlineBytes = bytes;
        ReferenceId = referenceId;
        Sha256 = sha256;
        ByteCount = byteCount;
        Width = width;
        Height = height;
        ReferenceExpiresAtUtc = referenceExpiresAtUtc;
    }

    public PerceptionFrameContentKind Kind { get; }
    public string? ReferenceId { get; }
    public string Sha256 { get; }
    public int ByteCount { get; }
    public int Width { get; }
    public int Height { get; }
    public DateTimeOffset? ReferenceExpiresAtUtc { get; }
    public ReadOnlyMemory<byte> InlineBytes => inlineBytes.ToArray();

    public static SelectedWindowFrameContent FromInlinePng(ReadOnlySpan<byte> pngBytes)
    {
        PerceptionWorkerGuard.Require(pngBytes.Length <= PerceptionProtocol.MaximumImageBytes,
            PerceptionWorkerFailure.LimitExceeded);
        var copy = pngBytes.ToArray();
        var inspected = PngInspector.Inspect(copy);
        return new(
            PerceptionFrameContentKind.InlinePng,
            copy,
            null,
            PerceptionWorkerGuard.Sha256Hex(copy),
            copy.Length,
            inspected.Width,
            inspected.Height,
            null);
    }

    public static SelectedWindowFrameContent FromEphemeralGatewayReference(
        string referenceId,
        string sha256,
        int byteCount,
        int width,
        int height,
        DateTimeOffset expiresAtUtc)
    {
        try
        {
            PerceptionWorkerGuard.Identifier(referenceId, 96);
            PerceptionWorkerGuard.Sha256(sha256);
            ValidateDimensions(byteCount, width, height);
            PerceptionWorkerGuard.Utc(expiresAtUtc);
        }
        catch (PerceptionWorkerException)
        {
            throw new PerceptionWorkerException(PerceptionWorkerFailure.InvalidFrameReference);
        }

        return new(
            PerceptionFrameContentKind.EphemeralGatewayReference,
            ReadOnlyMemory<byte>.Empty,
            referenceId,
            sha256,
            byteCount,
            width,
            height,
            expiresAtUtc);
    }

    internal void Validate()
    {
        PerceptionWorkerGuard.Defined(Kind);
        PerceptionWorkerGuard.Sha256(Sha256);
        ValidateDimensions(ByteCount, Width, Height);
        if (Kind == PerceptionFrameContentKind.InlinePng)
        {
            PerceptionWorkerGuard.Require(ReferenceId is null &&
                ReferenceExpiresAtUtc is null &&
                inlineBytes.Length == ByteCount &&
                PerceptionWorkerGuard.Sha256Hex(inlineBytes.Span) == Sha256,
                PerceptionWorkerFailure.InvalidImage);
            var inspected = PngInspector.Inspect(inlineBytes.Span);
            PerceptionWorkerGuard.Require(inspected.Width == Width && inspected.Height == Height,
                PerceptionWorkerFailure.InvalidImage);
            return;
        }

        PerceptionWorkerGuard.Require(inlineBytes.IsEmpty &&
            ReferenceId is not null &&
            ReferenceExpiresAtUtc is not null,
            PerceptionWorkerFailure.InvalidFrameReference);
        PerceptionWorkerGuard.Identifier(ReferenceId, 96);
        var expiresAtUtc = ReferenceExpiresAtUtc ??
            throw new PerceptionWorkerException(PerceptionWorkerFailure.InvalidFrameReference);
        PerceptionWorkerGuard.Utc(expiresAtUtc);
    }

    internal SelectedWindowFrameContent SnapshotForDispatch()
    {
        Validate();
        if (Kind == PerceptionFrameContentKind.InlinePng)
            return FromInlinePng(inlineBytes.Span);
        return FromEphemeralGatewayReference(
            ReferenceId!,
            Sha256,
            ByteCount,
            Width,
            Height,
            ReferenceExpiresAtUtc!.Value);
    }

    internal static void ValidateDimensions(int byteCount, int width, int height)
    {
        PerceptionWorkerGuard.Require(byteCount is > 0 and <= PerceptionProtocol.MaximumImageBytes,
            PerceptionWorkerFailure.LimitExceeded);
        PerceptionWorkerGuard.Require(width is > 0 and <= PerceptionProtocol.MaximumImageWidth &&
            height is > 0 and <= PerceptionProtocol.MaximumImageHeight &&
            checked((long)width * height) <= PerceptionProtocol.MaximumImagePixels,
            PerceptionWorkerFailure.LimitExceeded);
    }

    public override string ToString() =>
        $"Selected-window frame content {{ Kind = {Kind}, Width = {Width}, Height = {Height}, ByteCount = {ByteCount}, bytes = omitted }}";
}

public sealed class SelectedWindowFrame
{
    public SelectedWindowFrame(
        Guid frameId,
        long captureEpoch,
        SelectedWindowSource source,
        DateTimeOffset capturedAtUtc,
        SelectedWindowFrameContent content)
    {
        PerceptionWorkerGuard.Require(frameId != Guid.Empty);
        PerceptionWorkerGuard.Require(captureEpoch > 0);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(content);
        source.Validate();
        PerceptionWorkerGuard.Utc(capturedAtUtc);
        content.Validate();
        FrameId = frameId;
        CaptureEpoch = captureEpoch;
        Source = source;
        CapturedAtUtc = capturedAtUtc;
        Content = content;
    }

    public Guid FrameId { get; }
    public long CaptureEpoch { get; }
    public PerceptionCaptureScope Scope => PerceptionCaptureScope.SelectedWindow;
    public SelectedWindowSource Source { get; }
    public DateTimeOffset CapturedAtUtc { get; }
    public SelectedWindowFrameContent Content { get; }

    public override string ToString() =>
        $"Selected-window frame {{ FrameId = {FrameId}, CaptureEpoch = {CaptureEpoch}, content = omitted }}";

    internal SelectedWindowFrame SnapshotForDispatch() => new(
        FrameId,
        CaptureEpoch,
        Source with { },
        CapturedAtUtc,
        Content.SnapshotForDispatch());
}

internal readonly record struct PngInspection(int Width, int Height);

internal static class PngInspector
{
    private static ReadOnlySpan<byte> Signature =>
        [137, 80, 78, 71, 13, 10, 26, 10];

    internal static PngInspection Inspect(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return InspectCore(bytes);
        }
        catch (PerceptionWorkerException)
        {
            throw;
        }
        catch (Exception error) when (error is
            InvalidDataException or
            IOException or
            OverflowException or
            ArgumentException)
        {
            throw new PerceptionWorkerException(PerceptionWorkerFailure.InvalidImage);
        }
    }

    private static PngInspection InspectCore(ReadOnlySpan<byte> bytes)
    {
        PerceptionWorkerGuard.Require(bytes.Length is >= 57 and <= PerceptionProtocol.MaximumImageBytes,
            bytes.Length > PerceptionProtocol.MaximumImageBytes
                ? PerceptionWorkerFailure.LimitExceeded
                : PerceptionWorkerFailure.InvalidImage);
        PerceptionWorkerGuard.Require(bytes[..Signature.Length].SequenceEqual(Signature),
            PerceptionWorkerFailure.InvalidImage);

        var offset = Signature.Length;
        var chunkCount = 0;
        var sawHeader = false;
        var sawData = false;
        var dataEnded = false;
        var sawEnd = false;
        var width = 0;
        var height = 0;
        var bytesPerPixel = 0;
        using var compressed = new MemoryStream();

        while (offset < bytes.Length)
        {
            PerceptionWorkerGuard.Require(++chunkCount <= 256 &&
                bytes.Length - offset >= 12,
                PerceptionWorkerFailure.InvalidImage);
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(
                bytes.Slice(offset, 4)));
            offset += 4;
            PerceptionWorkerGuard.Require(length >= 0 && length <= bytes.Length - offset - 8,
                PerceptionWorkerFailure.InvalidImage);
            var type = bytes.Slice(offset, 4);
            offset += 4;
            var data = bytes.Slice(offset, length);
            offset += length;
            var expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
            offset += 4;
            PerceptionWorkerGuard.Require(Crc32(type, data) == expectedCrc,
                PerceptionWorkerFailure.InvalidImage);

            if (type.SequenceEqual("IHDR"u8))
            {
                PerceptionWorkerGuard.Require(!sawHeader && chunkCount == 1 && length == 13,
                    PerceptionWorkerFailure.InvalidImage);
                width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[..4]));
                height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4)));
                var bitDepth = data[8];
                var colorType = data[9];
                PerceptionWorkerGuard.Require(bitDepth == 8 &&
                    colorType is 2 or 6 &&
                    data[10] == 0 &&
                    data[11] == 0 &&
                    data[12] == 0,
                    PerceptionWorkerFailure.InvalidImage);
                SelectedWindowFrameContent.ValidateDimensions(1, width, height);
                bytesPerPixel = colorType == 2 ? 3 : 4;
                sawHeader = true;
                continue;
            }

            if (type.SequenceEqual("IDAT"u8))
            {
                PerceptionWorkerGuard.Require(sawHeader && !dataEnded && !sawEnd,
                    PerceptionWorkerFailure.InvalidImage);
                sawData = true;
                compressed.Write(data);
                continue;
            }

            if (type.SequenceEqual("IEND"u8))
            {
                PerceptionWorkerGuard.Require(sawHeader && sawData && !sawEnd && length == 0,
                    PerceptionWorkerFailure.InvalidImage);
                dataEnded = true;
                sawEnd = true;
                PerceptionWorkerGuard.Require(offset == bytes.Length,
                    PerceptionWorkerFailure.InvalidImage);
                break;
            }

            PerceptionWorkerGuard.Require(false, PerceptionWorkerFailure.InvalidImage);
        }

        PerceptionWorkerGuard.Require(sawHeader && sawData && sawEnd && compressed.Length > 0,
            PerceptionWorkerFailure.InvalidImage);
        ValidateImageData(compressed.ToArray(), width, height, bytesPerPixel);
        return new(width, height);
    }

    private static void ValidateImageData(
        byte[] compressed,
        int width,
        int height,
        int bytesPerPixel)
    {
        var rowBytes = checked(width * bytesPerPixel);
        var row = new byte[checked(rowBytes + 1)];
        using var input = new SingleByteReadStream(compressed);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        for (var rowIndex = 0; rowIndex < height; rowIndex++)
        {
            zlib.ReadExactly(row);
            PerceptionWorkerGuard.Require(row[0] <= 4, PerceptionWorkerFailure.InvalidImage);
        }
        PerceptionWorkerGuard.Require(zlib.ReadByte() == -1 &&
            input.Position == input.Length,
            PerceptionWorkerFailure.InvalidImage);
    }

    private static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        crc = UpdateCrc(crc, type);
        crc = UpdateCrc(crc, data);
        return ~crc;
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) == 0
                    ? crc >> 1
                    : (crc >> 1) ^ 0xedb88320U;
        }
        return crc;
    }

    private sealed class SingleByteReadStream(byte[] bytes)
        : MemoryStream(bytes, writable: false)
    {
        public override int Read(byte[] buffer, int offset, int count) =>
            base.Read(buffer, offset, Math.Min(count, 1));

        public override int Read(Span<byte> buffer) =>
            base.Read(buffer[..Math.Min(buffer.Length, 1)]);
    }
}
