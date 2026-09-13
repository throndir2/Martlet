using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Martlet.Core.Contracts;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace Martlet.Audio.Windows;

public sealed class WasapiCaptureDeviceFactory : ICaptureDeviceFactory
{
    private int leased;
    private Device? retainedDevice;

    public ICaptureDevice Open(CaptureDeviceAccess access, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        access.CheckAuthorization();
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref leased, 1, 0) != 0)
            throw new CaptureDeviceException(ErrorCode.AudioDeviceBusy);
        var device = new Device(access, () =>
        {
            retainedDevice = null;
            Volatile.Write(ref leased, 0);
        });
        retainedDevice = device;
        try
        {
            device.Initialize(cancellationToken);
            return device;
        }
        catch (Exception ex)
        {
            try { device.Dispose(); }
            catch (Exception)
            {
                throw new CaptureDeviceException(ErrorCode.AudioCaptureFailed, resourcesReleased: false);
            }
            if (ex is OperationCanceledException) throw;
            throw Normalize(ex);
        }
    }

    // This inspects managed format metadata only; it never activates a device.
    public static CaptureSourceFormat DescribeFormat(WaveFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        if (format is WaveFormatExtensible extended && extended.ValidBitsPerSample != format.BitsPerSample)
            throw new CaptureDeviceException(ErrorCode.AudioFormatUnsupported);
        var encoding = format is WaveFormatExtensible extensible
            ? extensible.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT ? DeviceSampleEncoding.IeeeFloat
                : extensible.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_PCM ? DeviceSampleEncoding.IntegerPcm
                : throw new CaptureDeviceException(ErrorCode.AudioFormatUnsupported)
            : format.Encoding == WaveFormatEncoding.IeeeFloat ? DeviceSampleEncoding.IeeeFloat
                : format.Encoding == WaveFormatEncoding.Pcm ? DeviceSampleEncoding.IntegerPcm
                : throw new CaptureDeviceException(ErrorCode.AudioFormatUnsupported);
        var source = new CaptureSourceFormat(format.SampleRate, format.Channels, format.BitsPerSample, encoding);
        source.Validate();
        if (format.BlockAlign != source.BlockAlignment || format.AverageBytesPerSecond != source.SampleRate * source.BlockAlignment)
            throw new CaptureDeviceException(ErrorCode.AudioFormatUnsupported);
        return source;
    }

    internal static CaptureDeviceException Normalize(Exception exception)
    {
        if (exception is CaptureDeviceException failure) return failure;
        var code = exception switch
        {
            UnauthorizedAccessException => ErrorCode.AudioAccessDenied,
            COMException native => native.HResult switch
            {
                unchecked((int)0x80070005) => ErrorCode.AudioAccessDenied,
                unchecked((int)0x80070490) => ErrorCode.AudioDeviceUnavailable,
                AudioClientErrorCode.DeviceInUse => ErrorCode.AudioDeviceBusy,
                AudioClientErrorCode.DeviceInvalidated => ErrorCode.AudioDeviceLost,
                AudioClientErrorCode.UnsupportedFormat => ErrorCode.AudioFormatUnsupported,
                AudioClientErrorCode.ServiceNotRunning => ErrorCode.AudioDeviceUnavailable,
                _ => ErrorCode.AudioCaptureFailed
            },
            _ => ErrorCode.AudioCaptureFailed
        };
        return new(code);
    }

    private sealed class Device(CaptureDeviceAccess access, Action released) : ICaptureDevice
    {
        private readonly int ownerThread = Environment.CurrentManagedThreadId;
        private NativeInputNotifications? notifications;
        private MMDevice? endpoint;
        private AudioClient? client;
        private AudioCaptureClient? capture;
        private byte[]? scratch;
        private long? nextPosition;
        private bool initialized, started, disposed, cleanupFailed;
        public CaptureSourceFormat Format { get; private set; } = null!;

        public void Initialize(CancellationToken token)
        {
            Check(token);
            notifications = new();
            notifications.Open();
            Check(token);
            endpoint = access.Input.Policy == InputPolicy.FixedEndpoint
                ? notifications.Enumerator.GetDevice(access.Input.EndpointId!)
                : notifications.Enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            if (endpoint.DataFlow != DataFlow.Capture || endpoint.State != DeviceState.Active)
                throw new CaptureDeviceException(ErrorCode.AudioDeviceUnavailable);
            notifications.Bind(endpoint.ID, access.Input.Policy);
            Check(token);
            client = endpoint.CreateAudioClient();
            var mix = client.MixFormat;
            Format = DescribeFormat(mix);
            Check(token);
            client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.NoPersist,
                500_000, 0, mix, Guid.NewGuid());
            initialized = true;
            if (client.BufferSize <= 0 || client.BufferSize > Format.SampleRate / 10)
                throw new CaptureDeviceException(ErrorCode.AudioFormatUnsupported);
            if (DescribeFormat(client.MixFormat) != Format)
                throw new CaptureDeviceException(ErrorCode.AudioDeviceChanged);
            capture = client.AudioCaptureClient;
            scratch = new byte[Format.MaximumPacketBytes];
            Check(token);
        }

        public void Start(CancellationToken cancellationToken)
        {
            Check(cancellationToken);
            try
            {
                notifications!.CheckSelected();
                client!.Start();
                started = true;
            }
            catch (Exception ex) { throw Normalize(ex); }
        }

        public CapturePacket Read(Span<byte> destination, CancellationToken cancellationToken)
        {
            Check(cancellationToken);
            try
            {
                notifications!.CheckSelected();
                if (endpoint!.State != DeviceState.Active) throw new CaptureDeviceException(ErrorCode.AudioDeviceLost);
                var available = capture!.GetNextPacketSize();
                if (available == 0) return new(0);
                if (available < 0 || available > Format.SampleRate / 10)
                    throw new CaptureDeviceException(ErrorCode.PayloadTooLarge);
                var pointer = capture.GetBuffer(out var frames, out var flags, out var position, out _);
                try
                {
                    if (frames < 0 || frames > Format.SampleRate / 10 || frames * Format.BlockAlignment > destination.Length)
                        throw new CaptureDeviceException(ErrorCode.PayloadTooLarge);
                    var bytes = frames * Format.BlockAlignment;
                    if (bytes == 0) return new(0);
                    if ((flags & ~(AudioClientBufferFlags.Silent | AudioClientBufferFlags.DataDiscontinuity | AudioClientBufferFlags.TimestampError)) != 0)
                        throw new CaptureDeviceException(ErrorCode.StreamTruncated);
                    if (position < 0 || position > long.MaxValue - frames || (flags & AudioClientBufferFlags.TimestampError) != 0
                        || (nextPosition is { } expected && (position != expected || (flags & AudioClientBufferFlags.DataDiscontinuity) != 0)))
                        throw new CaptureDeviceException(ErrorCode.StreamTruncated);
                    nextPosition = position + frames;
                    if ((flags & AudioClientBufferFlags.Silent) != 0)
                        destination[..bytes].Clear();
                    else
                    {
                        if (pointer == IntPtr.Zero) throw new CaptureDeviceException(ErrorCode.StreamTruncated);
                        Marshal.Copy(pointer, scratch!, 0, bytes);
                        scratch.AsSpan(0, bytes).CopyTo(destination);
                    }
                    return new(bytes);
                }
                finally
                {
                    if (scratch is not null) CryptographicOperations.ZeroMemory(scratch);
                    capture.ReleaseBuffer(frames);
                }
            }
            catch (Exception ex) { throw Normalize(ex); }
        }

        public void Stop()
        {
            CheckThread();
            try
            {
                try
                {
                    if (started) client!.Stop();
                }
                finally
                {
                    if (initialized) client!.Reset();
                    started = false;
                }
                // Release can seal before the next Read consumes a sticky notification.
                // Check after Stop/Reset so a recorded device failure cannot skip native cleanup.
                notifications?.CheckSelected();
            }
            catch (Exception ex) { throw Normalize(ex); }
        }

        public void Dispose()
        {
            CheckThread();
            if (disposed) return;
            if (cleanupFailed) throw new CaptureDeviceException(ErrorCode.AudioCaptureFailed, resourcesReleased: false);
            Exception? failure = null;
            // Attempt every owned release on this thread; retain failed wrappers, never cross-thread retry.
            try { notifications?.Dispose(); notifications = null; }
            catch (Exception ex) { failure = ex; }
            try { client?.Dispose(); client = null; capture = null; }
            catch (Exception ex) { failure = ex; }
            try { endpoint?.Dispose(); endpoint = null; }
            catch (Exception ex) { failure = ex; }
            if (scratch is not null) CryptographicOperations.ZeroMemory(scratch);
            scratch = null;
            if (failure is not null)
            {
                cleanupFailed = true;
                throw new CaptureDeviceException(ErrorCode.AudioCaptureFailed, resourcesReleased: false);
            }
            disposed = true;
            released();
        }

        private void Check(CancellationToken token)
        {
            CheckThread();
            token.ThrowIfCancellationRequested();
            access.CheckAuthorization();
        }
        private void CheckThread()
        {
            if (Environment.CurrentManagedThreadId != ownerThread)
                throw new InvalidOperationException("The capture device must remain on its owning worker thread.");
        }
    }
}
