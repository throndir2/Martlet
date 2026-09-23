using System.Security.Cryptography;

namespace Martlet.Perception;

internal sealed class OwnedWindowFrame : IDisposable
{
    private readonly object gate = new();
    private byte[]? pixels;
    private long leaseSequence;
    private long activeLease;
    private readonly TimeSpan remainingFreshness;

    internal WindowFrameProvenance Provenance { get; }
    internal long AcceptedTimestamp { get; }
    internal int ByteCount
    {
        get
        {
            lock (gate)
                return pixels?.Length ?? 0;
        }
    }

    internal OwnedWindowFrame(
        WindowFrameProvenance provenance,
        long acceptedTimestamp,
        TimeSpan remainingFreshness,
        byte[] ownedPixels)
    {
        provenance.Validate();
        ArgumentNullException.ThrowIfNull(ownedPixels);
        PerceptionGuard.Require(ownedPixels.Length == provenance.ByteCount,
            PerceptionFailureCode.MalformedFrame);
        Provenance = provenance;
        AcceptedTimestamp = acceptedTimestamp;
        this.remainingFreshness = remainingFreshness;
        pixels = ownedPixels;
    }

    internal bool IsFresh(TimeProvider clock)
    {
        lock (gate)
        {
            return pixels is not null &&
                clock.GetUtcNow() >= Provenance.CapturedAtUtc &&
                clock.GetUtcNow() < Provenance.FreshUntilUtc &&
                clock.GetElapsedTime(AcceptedTimestamp) < remainingFreshness;
        }
    }

    internal long AcquireLease()
    {
        lock (gate)
        {
            PerceptionGuard.Require(pixels is not null &&
                activeLease == 0, PerceptionFailureCode.InvalidState);
            activeLease = checked(++leaseSequence);
            return activeLease;
        }
    }

    internal void CopyTo(long lease, Span<byte> destination)
    {
        lock (gate)
        {
            var available = pixels;
            PerceptionGuard.Require(available is not null &&
                activeLease == lease, PerceptionFailureCode.InvalidState);
            if (available is null)
                throw new PerceptionException(
                    PerceptionFailureCode.InvalidState);
            PerceptionGuard.Require(destination.Length == available.Length,
                PerceptionFailureCode.InvalidInput);
            available.CopyTo(destination);
        }
    }

    internal void ReleaseLease(long lease)
    {
        lock (gate)
        {
            if (activeLease == lease)
                activeLease = 0;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (pixels is not null)
                CryptographicOperations.ZeroMemory(pixels);
            pixels = null;
            activeLease = 0;
        }
    }
}

public abstract class WindowFrameLease : IDisposable
{
    private readonly OwnedWindowFrame frame;
    private readonly long lease;
    private readonly WindowCaptureOperation owner;
    private readonly OneUseAuthorizationState authorization;
    private int disposed;

    public WindowFrameProvenance Provenance => frame.Provenance;
    public int ByteCount => Provenance.ByteCount;

    private protected WindowFrameLease(
        OwnedWindowFrame frame,
        long lease,
        WindowCaptureOperation owner,
        OneUseAuthorizationState authorization)
    {
        this.frame = frame;
        this.lease = lease;
        this.owner = owner;
        this.authorization = authorization;
    }

    public void CopyPixelsTo(Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        owner.CopyPixelsTo(frame, lease, authorization, destination);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
            frame.ReleaseLease(lease);
    }

    public override string ToString() =>
        $"{GetType().Name} {{ Provenance = {Provenance}, Pixels = [redacted] }}";
}

public sealed class WindowPreviewFrame : WindowFrameLease
{
    internal WindowPreviewFrame(OwnedWindowFrame frame, long lease,
        WindowCaptureOperation owner, OneUseAuthorizationState authorization)
        : base(frame, lease, owner, authorization)
    {
    }
}

public sealed class WindowDisclosureFrame : WindowFrameLease
{
    public CaptureDestination Destination { get; }

    internal WindowDisclosureFrame(
        OwnedWindowFrame frame,
        long lease,
        CaptureDestination destination,
        WindowCaptureOperation owner,
        OneUseAuthorizationState authorization)
        : base(frame, lease, owner, authorization)
    {
        Destination = destination;
    }
}
