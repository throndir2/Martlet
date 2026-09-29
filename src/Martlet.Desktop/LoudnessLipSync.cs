using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using Martlet.Audio;
using Martlet.Conversation;
using Martlet.Core.Audio;

namespace Martlet.Desktop;

/// <summary>Speech loudness levels (0..1) of one generated-speech segment, in 20 ms windows.</summary>
internal sealed class LoudnessMeter(PcmFormat format)
{
    private const int WindowsPerSecond = 50;
    private readonly object gate = new();
    private readonly List<float> levels = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private double sum;
    private int count;
    private TimeSpan? firstAudio;
    internal int Window { get; } = Math.Max(1, format.SampleRate / WindowsPerSecond);

    internal void Add(PcmFrame frame)
    {
        var data = frame.Data.Span;
        var stride = format.BlockAlignment;
        lock (gate)
        {
            firstAudio ??= clock.Elapsed;
            for (var offset = 0; offset + 1 < data.Length; offset += stride)
            {
                var sample = BinaryPrimitives.ReadInt16LittleEndian(data[offset..]) / 32768.0;
                sum += sample * sample;
                if (++count < Window) continue;
                levels.Add(Level(Math.Sqrt(sum / count)));
                sum = 0;
                count = 0;
            }
        }
    }

    internal float LevelAt(long sampleOffset)
    {
        var index = (int)Math.Min(int.MaxValue, sampleOffset / Window);
        lock (gate) return index < levels.Count ? levels[index] : 0;
    }

    /// <summary>Approximate played samples when the device reports no progress yet (default 150 ms prebuffer).</summary>
    internal long? EstimatedPlayedSamples(int sampleRate)
    {
        TimeSpan? start;
        lock (gate) start = firstAudio;
        if (start is not { } first) return null;
        var seconds = (clock.Elapsed - first).TotalSeconds - 0.15;
        return seconds <= 0 ? null : (long)(seconds * sampleRate);
    }

    // -48 dBFS closed .. -15 dBFS fully open.
    internal static float Level(double rms)
    {
        var decibels = 20 * Math.Log10(rms + 1e-9);
        return (float)Math.Clamp((decibels + 48) / 33, 0, 1);
    }
}

/// <summary>Local mouth animation from the loudness of Martlet's own generated speech, aligned to playback.</summary>
internal static class LoudnessLipSync
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(33);

    /// <summary>Sends the mouth level for the currently audible part of the segment until playback ends.</summary>
    /// <param name="paused">While true (another lip-sync source is animating), no levels are sent.</param>
    internal static async Task PresentAsync(GeneratedSpeechObservation segment, LoudnessMeter meter,
        Func<double, Task> send, Func<bool> paused, CancellationToken token)
    {
        double last = -1;
        try
        {
            while (!token.IsCancellationRequested && !segment.Stopped.IsCompleted)
            {
                var snapshot = segment.Playback.Snapshot;
                if (snapshot.State is PlaybackState.Completed or PlaybackState.Canceled or PlaybackState.Replaced or PlaybackState.Failed)
                    break;
                if (paused()) last = -1;
                else
                {
                    var position = Position(segment, meter);
                    var level = position is { } played && snapshot.State == PlaybackState.Playing ? meter.LevelAt(played) : 0;
                    if (Math.Abs(level - last) > 0.02 || level == 0 && last != 0)
                    {
                        await send(Math.Round(level, 3));
                        last = level;
                    }
                }
                await Task.Delay(Tick, token);
            }
        }
        finally
        {
            if (last > 0 && !token.IsCancellationRequested)
                try { await send(0); }
                catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException or TimeoutException) { }
        }
    }

    private static long? Position(GeneratedSpeechObservation segment, LoudnessMeter meter)
    {
        var clock = segment.Playback.DeviceClock;
        if (clock.State == PlaybackClockState.Available && clock.SampleOffset is { } offset) return offset;
        var consumed = segment.Playback.Snapshot.DeviceConsumedSamples;
        if (consumed > 0) return consumed;
        return meter.EstimatedPlayedSamples(segment.Format.SampleRate);
    }
}
