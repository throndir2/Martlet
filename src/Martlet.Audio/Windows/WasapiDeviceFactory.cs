using System.Runtime.InteropServices;
using System.Diagnostics;
using Martlet.Core.Audio;
using Martlet.Core.Contracts;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace Martlet.Audio.Windows;

public sealed class WasapiDeviceFactory : IPlaybackDeviceFactory
{
    public IPlaybackDevice Open(OutputSelection selection, PcmFormat format, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(format);
        selection.Validate();
        format.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        MMDevice? endpoint = null;
        AudioClient? client = null;
        var transferred = false;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            // Resolve the deliberate default once to a concrete endpoint. No automatic routing/failover.
            endpoint = selection.Policy == OutputPolicy.FixedEndpoint
                ? enumerator.GetDevice(selection.EndpointId!)
                : enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            if (endpoint.DataFlow != DataFlow.Render || endpoint.State != DeviceState.Active)
                throw Failure(ErrorCode.AudioDeviceUnavailable);
            cancellationToken.ThrowIfCancellationRequested();
            client = endpoint.CreateAudioClient();
            var input = new WaveFormat(format.SampleRate, 16, format.Channels);
            client.Initialize(AudioClientShareMode.Shared,
                AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality | AudioClientStreamFlags.NoPersist,
                500_000, 0, input, Guid.NewGuid());
            var mix = client.MixFormat;
            var encoding = mix is WaveFormatExtensible extensible
                ? extensible.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT ? DeviceSampleEncoding.IeeeFloat
                    : extensible.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_PCM ? DeviceSampleEncoding.IntegerPcm
                    : throw Failure(ErrorCode.AudioFormatUnsupported)
                : mix.Encoding == WaveFormatEncoding.IeeeFloat ? DeviceSampleEncoding.IeeeFloat
                    : mix.Encoding == WaveFormatEncoding.Pcm ? DeviceSampleEncoding.IntegerPcm
                    : throw Failure(ErrorCode.AudioFormatUnsupported);
            var info = new PlaybackDeviceInfo(mix.SampleRate, mix.Channels, mix.BitsPerSample,
                encoding, client.BufferSize, true);
            cancellationToken.ThrowIfCancellationRequested();
            var device = new WasapiDevice(endpoint, client, format.BlockAlignment, info);
            transferred = true;
            return device;
        }
        catch (COMException ex) { throw Normalize(ex, opening: true); }
        finally
        {
            if (!transferred)
            {
                try { client?.Dispose(); }
                finally { endpoint?.Dispose(); }
            }
        }
    }

    private static ContractException Failure(ErrorCode code) => new(code, PlaybackErrors.Create(code).Summary);

    internal static ContractException Normalize(COMException ex, bool opening = false) =>
        Failure(ex.HResult == AudioClientErrorCode.UnsupportedFormat ? ErrorCode.AudioFormatUnsupported
            : ex.HResult == AudioClientErrorCode.DeviceInvalidated ? ErrorCode.AudioDeviceLost
            : opening ? ErrorCode.AudioDeviceUnavailable : ErrorCode.AudioPlaybackFailed);

    private sealed class WasapiDevice : IPlaybackDevice, IPlaybackClockDevice
    {
        private readonly MMDevice endpoint;
        private readonly AudioClient client;
        private readonly AudioRenderClient render;
        private readonly int alignment;
        public PlaybackDeviceInfo Info { get; }

        public DeviceClockReading? ReadClock(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var clock = client.AudioClockClient;
            var before = Stopwatch.GetTimestamp();
            if (!clock.GetPosition(out var position, out var qpc)) return null;
            var after = Stopwatch.GetTimestamp();
            var now = (UInt128)(ulong)after * 10_000_000 / (ulong)Stopwatch.Frequency;
            // NAudio's Boolean alone does not distinguish every native inaccurate-result status.
            if (Stopwatch.GetElapsedTime(before, after) > TimeSpan.FromMilliseconds(10) ||
                qpc > now || now - qpc > 1_000_000)
                return null;
            return new(position, clock.Frequency, PlaybackClockOrigin.NativeDevice);
        }

        internal WasapiDevice(MMDevice endpoint, AudioClient client, int alignment, PlaybackDeviceInfo info)
        {
            this.endpoint = endpoint;
            this.client = client;
            this.alignment = alignment;
            Info = info;
            render = client.AudioRenderClient;
        }

        public int GetPadding(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (endpoint.State != DeviceState.Active)
                    throw Failure(ErrorCode.AudioDeviceLost);
                return client.CurrentPadding;
            }
            catch (COMException ex) { throw Normalize(ex); }
        }

        public int Write(ReadOnlySpan<byte> pcm, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ContractRules.Require(pcm.Length > 0 && pcm.Length % alignment == 0,
                "The device write must contain aligned PCM samples.");
            try
            {
                var samples = pcm.Length / alignment;
                using var lease = render.GetBufferLease(samples, alignment);
                pcm.CopyTo(lease.Buffer);
                return samples;
            }
            catch (COMException ex) { throw Normalize(ex); }
        }

        public void Start(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { client.Start(); }
            catch (COMException ex) { throw Normalize(ex); }
        }

        public void StopAndReset()
        {
            try
            {
                client.Stop();
                client.Reset();
            }
            catch (COMException ex) { throw Normalize(ex); }
        }

        public void Dispose()
        {
            try { client.Dispose(); }
            finally { endpoint.Dispose(); }
        }
    }
}
