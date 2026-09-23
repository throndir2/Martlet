using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Martlet.Perception;

namespace Martlet.Perception.Tests;

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

internal static class PerceptionTestData
{
    internal static readonly DateTimeOffset Now =
        new(2026, 9, 22, 7, 0, 0, TimeSpan.Zero);
    internal static readonly TimeProvider Clock = new FixedTimeProvider(Now);
    internal static readonly Guid SelectionId =
        Guid.Parse("11111111-2222-3333-4444-555555555555");
    internal const string Destination = "host-2-perception";
    internal const string Host = "host-2-fixture";
    internal const string SourceRevision =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    internal const string CapturePermissionRevision =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    internal static PerceptionGatewayBinding Binding() => new()
    {
        DestinationId = Destination,
        HostId = Host,
        Role = PerceptionGatewayRole.Perception,
        Authentication = PerceptionGatewayAuthentication.ScopedDeviceCredential,
        AutomaticRedirectsAllowed = false
    };

    internal static PerceptionJobIds Ids() => new()
    {
        SessionId = Guid.NewGuid(),
        TurnId = Guid.NewGuid(),
        RequestId = Guid.NewGuid()
    };

    internal static SelectedWindowSource Source() => new()
    {
        SelectionId = SelectionId,
        SourceRevision = SourceRevision,
        CapturePermissionRevision = CapturePermissionRevision
    };

    internal static SelectedWindowFrame Frame(
        long epoch,
        DateTimeOffset? capturedAt = null,
        SelectedWindowFrameContent? content = null) => new(
            Guid.NewGuid(),
            epoch,
            Source(),
            capturedAt ?? Now,
            content ?? SelectedWindowFrameContent.FromInlinePng(Png()));

    internal static PerceptionJobIntent Intent(
        PerceptionRole role,
        long epoch = 1,
        PerceptionWorkerIdentity? worker = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? deadline = null,
        DateTimeOffset? capturedAt = null,
        TimeSpan? maximumFrameAge = null,
        SelectedWindowFrameContent? content = null,
        string question = "What synthetic state is visible?") =>
        new(
            Ids(),
            Destination,
            worker ?? DeterministicPerceptionFixtureIdentity.Create(role),
            Frame(epoch, capturedAt, content),
            role == PerceptionRole.Ocr
                ? PerceptionTaskDefinition.ReadVisibleText()
                : PerceptionTaskDefinition.AskAboutSelectedWindow(question),
            createdAt ?? Now,
            deadline ?? (createdAt ?? Now).AddSeconds(10),
            maximumFrameAge ?? TimeSpan.FromSeconds(5));

    internal static PerceptionVisionAuthorization Authorize(
        PerceptionJobIntent request,
        DateTimeOffset? expiry = null) =>
        request.Authorize(
            PerceptionVisionAuthorizationDecision.Allow,
            Guid.NewGuid(),
            expiry ?? request.DeadlineUtc);

    internal static (
        PerceptionGatewayClientAdapter Adapter,
        DeterministicPerceptionWorkerTransport Transport)
        Adapter(
            PerceptionWorkerIdentity worker,
            DeterministicPerceptionWorkerOptions? options = null,
            TimeProvider? clock = null)
    {
        var transport = new DeterministicPerceptionWorkerTransport(
            Binding(), worker, options, clock ?? Clock);
        var adapter = new PerceptionGatewayClientAdapter(
            transport, Destination, Host, worker, clock ?? Clock);
        return (adapter, transport);
    }

    internal static byte[] Png(
        int width = 2,
        int height = 2,
        byte colorType = 6,
        byte interlace = 0,
        byte filter = 0,
        byte[]? compressedSuffix = null)
    {
        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(
            header.AsSpan(0, 4), checked((uint)width));
        BinaryPrimitives.WriteUInt32BigEndian(
            header.AsSpan(4, 4), checked((uint)height));
        header[8] = 8;
        header[9] = colorType;
        header[10] = 0;
        header[11] = 0;
        header[12] = interlace;
        WriteChunk(output, "IHDR"u8, header);

        var bytesPerPixel = colorType == 2 ? 3 : 4;
        using var raw = new MemoryStream();
        for (var row = 0; row < height; row++)
        {
            raw.WriteByte(filter);
            for (var column = 0; column < width; column++)
            {
                raw.WriteByte((byte)(32 + row));
                raw.WriteByte((byte)(64 + column));
                raw.WriteByte(96);
                if (bytesPerPixel == 4)
                    raw.WriteByte(255);
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(
            compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            raw.Position = 0;
            raw.CopyTo(zlib);
        }
        var imageData = compressed.ToArray();
        if (compressedSuffix is { Length: > 0 })
            imageData = imageData.Concat(compressedSuffix).ToArray();
        WriteChunk(output, "IDAT"u8, imageData);
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static void WriteChunk(
        Stream output,
        ReadOnlySpan<byte> type,
        ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)data.Length));
        output.Write(length);
        output.Write(type);
        output.Write(data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, Crc32(type, data));
        output.Write(crcBytes);
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

    internal static async Task WaitForRequestsAsync(
        DeterministicPerceptionWorkerTransport transport,
        int count)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (transport.Requests.Count < count)
        {
            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException("The deterministic transport did not receive the expected request.");
            await Task.Delay(10);
        }
    }

    internal static async Task WaitForRequestAsync(
        DeterministicPerceptionWorkerTransport transport,
        Guid actionId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!transport.Requests.Any(request => request.ActionId == actionId))
        {
            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException("The deterministic transport did not receive the expected action.");
            await Task.Delay(10);
        }
    }

    internal static PerceptionWorkerException Failure(
        PerceptionWorkerFailure expected,
        Action action)
    {
        var error = Assert.Throws<PerceptionWorkerException>(action);
        Assert.Equal(expected, error.Failure);
        return error;
    }
}
