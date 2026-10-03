using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Martlet.Core.Contracts;
using NAudio.CoreAudioApi;

namespace Martlet.Audio.Windows;

/// <summary>What an output endpoint plays, through a shared-mode WASAPI loopback stream: the speaker reference for echo
/// reduction. Opened and polled on the capture's worker thread only while the microphone listens; it never plays, keeps or
/// sends anything.</summary>
public sealed class WasapiLoopbackReferenceFactory : IEchoReferenceFactory
{
    public IEchoReference Open(string? outputEndpointId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var loopback = new Loopback();
        try
        {
            loopback.Initialize(outputEndpointId, cancellationToken);
            return loopback;
        }
        catch (Exception ex)
        {
            try { loopback.Dispose(); }
            catch (Exception) { throw new CaptureDeviceException(ErrorCode.AudioCaptureFailed, resourcesReleased: false); }
            if (ex is OperationCanceledException) throw;
            throw WasapiCaptureDeviceFactory.Normalize(ex);
        }
    }

    private sealed class Loopback : IEchoReference
    {
        private MMDeviceEnumerator? enumerator;
        private MMDevice? endpoint;
        private AudioClient? client;
        private AudioCaptureClient? capture;
        private byte[]? scratch;
        private bool initialized, started, disposed;
        public CaptureSourceFormat Format { get; private set; } = null!;

        public void Initialize(string? endpointId, CancellationToken token)
        {
            enumerator = new();
            // The same endpoint Martlet's own voice plays on: the chosen output, or Windows' default as playback binds it.
            endpoint = endpointId is null
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console)
                : enumerator.GetDevice(endpointId);
            if (endpoint.DataFlow != DataFlow.Render || endpoint.State != DeviceState.Active)
                throw new CaptureDeviceException(ErrorCode.AudioDeviceUnavailable);
            token.ThrowIfCancellationRequested();
            client = endpoint.CreateAudioClient();
            var mix = client.MixFormat;
            Format = WasapiCaptureDeviceFactory.DescribeFormat(mix);
            client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.Loopback | AudioClientStreamFlags.NoPersist,
                2_000_000, 0, mix, Guid.NewGuid());
            initialized = true;
            capture = client.AudioCaptureClient;
            scratch = new byte[Format.MaximumPacketBytes];
            token.ThrowIfCancellationRequested();
        }

        public void Start()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            try
            {
                client!.Start();
                started = true;
            }
            catch (Exception ex) { throw WasapiCaptureDeviceFactory.Normalize(ex); }
        }

        public CapturePacket Read(Span<byte> destination)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            try
            {
                if (endpoint!.State != DeviceState.Active) throw new CaptureDeviceException(ErrorCode.AudioDeviceLost);
                if (capture!.GetNextPacketSize() == 0) return new(0);
                var pointer = capture.GetBuffer(out var frames, out var flags, out _, out var timestamp);
                try
                {
                    var bytes = frames * Format.BlockAlignment;
                    if (frames <= 0) return new(0);
                    if (bytes > destination.Length || bytes > scratch!.Length) throw new CaptureDeviceException(ErrorCode.PayloadTooLarge);
                    if ((flags & AudioClientBufferFlags.Silent) != 0 || pointer == IntPtr.Zero)
                        destination[..bytes].Clear();
                    else
                    {
                        Marshal.Copy(pointer, scratch, 0, bytes);
                        scratch.AsSpan(0, bytes).CopyTo(destination);
                        CryptographicOperations.ZeroMemory(scratch.AsSpan(0, bytes));
                    }
                    // A pause in playback is a gap, not lost audio; echo reduction starts a new stretch there.
                    return new(bytes, (flags & AudioClientBufferFlags.DataDiscontinuity) != 0,
                        (flags & AudioClientBufferFlags.TimestampError) != 0 ? null : timestamp);
                }
                finally { capture.ReleaseBuffer(frames); }
            }
            catch (Exception ex) { throw WasapiCaptureDeviceFactory.Normalize(ex); }
        }

        public void Stop()
        {
            if (disposed) return;
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
            }
            catch (Exception ex) { throw WasapiCaptureDeviceFactory.Normalize(ex); }
        }

        public void Dispose()
        {
            if (disposed) return;
            Exception? failure = null;
            try { client?.Dispose(); client = null; capture = null; }
            catch (Exception ex) { failure = ex; }
            try { endpoint?.Dispose(); endpoint = null; }
            catch (Exception ex) { failure = ex; }
            try { enumerator?.Dispose(); enumerator = null; }
            catch (Exception ex) { failure = ex; }
            if (scratch is not null) CryptographicOperations.ZeroMemory(scratch);
            scratch = null;
            if (failure is not null) throw new CaptureDeviceException(ErrorCode.AudioCaptureFailed, resourcesReleased: false);
            disposed = true;
        }
    }
}
