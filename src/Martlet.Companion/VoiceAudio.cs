using System.Buffers.Binary;
using System.Collections.Concurrent;
using Martlet.Companion.Platform;
using SoundFlow.Abstracts;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Enums;
using SoundFlow.Structs;

namespace Martlet.Companion;

/// <summary>Microphone recording for push-to-talk: 16-bit mono PCM at <see cref="SampleRate"/>.</summary>
public interface IVoiceRecording : IDisposable
{
    int SampleRate { get; }
    /// <summary>Loudness of the latest audio, 0-1.</summary>
    float Level { get; }
    byte[] Stop();
}

/// <summary>Speaker playback of 24 kHz mono 16-bit PCM, with its loudness for lip-sync.</summary>
public interface IVoicePlayback : IDisposable
{
    void Write(ReadOnlySpan<byte> pcm16);
    /// <summary>Waits until everything written has played.</summary>
    Task DrainAsync(CancellationToken cancellationToken);
    /// <summary>Loudness of what is playing now, 0-1 (0 when silent).</summary>
    float Level { get; }
    void Stop();
}

public interface IVoiceAudio : IDisposable
{
    FeatureStatus Status { get; }
    IVoiceRecording StartRecording();
    IVoicePlayback StartPlayback();
}

/// <summary>Pure PCM helpers shared by the audio backend and its tests.</summary>
public static class Pcm
{
    public static float[] ToFloat(ReadOnlySpan<byte> pcm16)
    {
        var samples = new float[pcm16.Length / 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm16[(i * 2)..]) / 32768f;
        return samples;
    }

    public static void ToPcm16(ReadOnlySpan<float> samples, Span<byte> destination)
    {
        for (var i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(destination[(i * 2)..], (short)Math.Clamp(MathF.Round(samples[i] * 32767f), short.MinValue, short.MaxValue));
    }

    /// <summary>Mouth openness from loudness: RMS scaled so normal speech (about -20 dBFS) opens the mouth well, 0-1.</summary>
    public static float Loudness(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return 0;
        double sum = 0;
        foreach (var s in samples) sum += s * s;
        var rms = Math.Sqrt(sum / samples.Length);
        return rms < 0.005 ? 0 : (float)Math.Clamp(rms * 6, 0, 1);
    }
}

/// <summary>The cross-platform audio backend: SoundFlow over miniaudio (MIT), which uses PipeWire/PulseAudio/ALSA on Linux
/// and CoreAudio on macOS (and WASAPI on Windows dev runs). Devices open only while recording or speaking.</summary>
public sealed class SoundFlowAudio : IVoiceAudio
{
    public const int PlaybackRate = 24_000;
    public const int CaptureRate = 24_000;
    private readonly Lock gate = new();
    private MiniAudioEngine? engine;

    public FeatureStatus Status { get; private set; } = FeatureStatus.Yes("miniaudio (SoundFlow)");

    private MiniAudioEngine Engine()
    {
        lock (gate)
        {
            try { return engine ??= new MiniAudioEngine(); }
            catch (Exception error) when (error is DllNotFoundException or BadImageFormatException or InvalidOperationException or EntryPointNotFoundException)
            {
                Status = FeatureStatus.No($"Audio isn't available: {error.Message}");
                throw new CompanionException(Status.Reason);
            }
        }
    }

    private static AudioFormat Mono(int rate) => new()
    {
        Format = SampleFormat.F32, Channels = 1, SampleRate = rate, Layout = AudioFormat.GetLayoutFromChannels(1)
    };

    public IVoiceRecording StartRecording()
    {
        var device = Engine().InitializeCaptureDevice(null, Mono(CaptureRate), null);
        return new Recording(device);
    }

    public IVoicePlayback StartPlayback()
    {
        var audio = Engine();
        var format = Mono(PlaybackRate);
        var device = audio.InitializePlaybackDevice(null, format, null);
        var queue = new QueueComponent(audio, format);
        device.MasterMixer.AddComponent(queue);
        device.Start();
        return new Playback(device, queue);
    }

    public void Dispose()
    {
        lock (gate)
        {
            engine?.Dispose();
            engine = null;
        }
    }

    private sealed class Recording : IVoiceRecording
    {
        private readonly AudioCaptureDevice device;
        private readonly List<float> samples = [];
        private readonly Lock gate = new();
        private bool stopped;

        public Recording(AudioCaptureDevice device)
        {
            this.device = device;
            device.OnAudioProcessed += OnAudio;
            device.Start();
        }

        public int SampleRate => CaptureRate;
        public float Level { get; private set; }

        private void OnAudio(Span<float> buffer, Capability capability)
        {
            Level = Pcm.Loudness(buffer);
            lock (gate)
                if (!stopped && samples.Count < CaptureRate * 85)
                    foreach (var s in buffer) samples.Add(s);
        }

        public byte[] Stop()
        {
            lock (gate)
            {
                if (!stopped)
                {
                    stopped = true;
                    device.OnAudioProcessed -= OnAudio;
                    device.Stop();
                }
                var pcm = new byte[samples.Count * 2];
                Pcm.ToPcm16(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(samples), pcm);
                return pcm;
            }
        }

        public void Dispose()
        {
            Stop();
            device.Dispose();
        }
    }

    private sealed class Playback(AudioPlaybackDevice device, QueueComponent queue) : IVoicePlayback
    {
        public float Level => queue.Level;
        public void Write(ReadOnlySpan<byte> pcm16) => queue.Enqueue(Pcm.ToFloat(pcm16));

        public async Task DrainAsync(CancellationToken cancellationToken)
        {
            while (!queue.IsEmpty) await Task.Delay(50, cancellationToken);
        }

        public void Stop() => queue.Clear();

        public void Dispose()
        {
            queue.Clear();
            device.Stop();
            device.Dispose();
        }
    }

    /// <summary>A mixer input that plays queued samples and reports their loudness.</summary>
    private sealed class QueueComponent(AudioEngine engine, AudioFormat format) : SoundComponent(engine, format)
    {
        private readonly ConcurrentQueue<float[]> chunks = new();
        private float[]? current;
        private int offset;

        public override string Name { get; set; } = "Martlet voice";
        public float Level { get; private set; }
        public bool IsEmpty => current is null && chunks.IsEmpty;

        public void Enqueue(float[] samples) { if (samples.Length > 0) chunks.Enqueue(samples); }

        public void Clear()
        {
            chunks.Clear();
            current = null;
            Level = 0;
        }

        protected override void GenerateAudio(Span<float> buffer, int channels)
        {
            var written = 0;
            while (written < buffer.Length)
            {
                if (current is null || offset >= current.Length)
                {
                    if (!chunks.TryDequeue(out current)) { current = null; break; }
                    offset = 0;
                }
                var count = Math.Min(buffer.Length - written, current.Length - offset);
                current.AsSpan(offset, count).CopyTo(buffer[written..]);
                offset += count;
                written += count;
            }
            buffer[written..].Clear();
            Level = Pcm.Loudness(buffer[..written]);
        }
    }
}
