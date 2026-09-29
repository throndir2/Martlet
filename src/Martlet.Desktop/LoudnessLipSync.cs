using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using Martlet.Audio;
using Martlet.Conversation;

namespace Martlet.Desktop;

/// <summary>Local mouth animation from the loudness of Martlet's own generated speech, aligned to playback.</summary>
internal static class LoudnessLipSync
{
    private const int WindowsPerSecond = 50;
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(33);

    internal static async Task RunAsync(GeneratedSpeechObservation segment, Func<double, Task> send, CancellationToken token)
    {
        var format = segment.Format;
        var window = Math.Max(1, format.SampleRate / WindowsPerSecond);
        var levels = new List<float>();
        var gate = new object();
        var clock = Stopwatch.StartNew();
        TimeSpan? firstAudio = null;
        var reader = Task.Run(async () =>
        {
            double sum = 0;
            var count = 0;
            await foreach (var frame in segment.Frames.ReadAllAsync(token))
            {
                lock (gate) firstAudio ??= clock.Elapsed;
                var data = frame.Data.Span;
                var stride = format.BlockAlignment;
                for (var offset = 0; offset + 1 < data.Length; offset += stride)
                {
                    var sample = BinaryPrimitives.ReadInt16LittleEndian(data[offset..]) / 32768.0;
                    sum += sample * sample;
                    if (++count < window) continue;
                    var level = Level(Math.Sqrt(sum / count));
                    lock (gate) levels.Add(level);
                    sum = 0;
                    count = 0;
                }
            }
        }, token);

        double last = -1;
        try
        {
            while (!token.IsCancellationRequested && !segment.Stopped.IsCompleted)
            {
                var snapshot = segment.Playback.Snapshot;
                if (snapshot.State is PlaybackState.Completed or PlaybackState.Canceled or PlaybackState.Replaced or PlaybackState.Failed)
                    break;
                var position = Position(segment, clock.Elapsed, firstAudio);
                double level = 0;
                if (position is { } played && snapshot.State == PlaybackState.Playing)
                {
                    var index = (int)Math.Min(int.MaxValue, played / window);
                    lock (gate) level = index < levels.Count ? levels[index] : 0;
                }
                if (Math.Abs(level - last) > 0.02 || level == 0 && last != 0)
                {
                    await send(Math.Round(level, 3));
                    last = level;
                }
                await Task.Delay(Tick, token);
            }
        }
        finally
        {
            if (last > 0 && !token.IsCancellationRequested)
                try { await send(0); }
                catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException or TimeoutException) { }
            try { await reader; }
            catch (Exception error) when (error is OperationCanceledException or InvalidOperationException) { }
        }
    }

    // -48 dBFS closed .. -15 dBFS fully open.
    internal static float Level(double rms)
    {
        var decibels = 20 * Math.Log10(rms + 1e-9);
        return (float)Math.Clamp((decibels + 48) / 33, 0, 1);
    }

    private static long? Position(GeneratedSpeechObservation segment, TimeSpan elapsed, TimeSpan? firstAudio)
    {
        var clock = segment.Playback.DeviceClock;
        if (clock.State == PlaybackClockState.Available && clock.SampleOffset is { } offset) return offset;
        var consumed = segment.Playback.Snapshot.DeviceConsumedSamples;
        if (consumed > 0) return consumed;
        // No device progress yet: approximate from arrival with the default 150 ms prebuffer.
        if (firstAudio is not { } start) return null;
        var seconds = (elapsed - start).TotalSeconds - 0.15;
        return seconds <= 0 ? null : (long)(seconds * segment.Format.SampleRate);
    }
}
