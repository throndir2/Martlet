using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Martlet.Audio;
using Martlet.Audio.Windows;
using Martlet.Core.Contracts;

namespace Martlet.Mcp;

/// <summary>pc_audio_check: Companion › Listening › Hear what this PC plays. Reads the saved choice (off by default) with the
/// choices it depends on, asks Windows whether it can hear the PC without Martlet's own sound (a process loopback is set up and
/// closed without starting, so nothing is recorded), and rehearses the production path (PcAudioCaptureFactory,
/// MicrophoneCapture, the capture normalizer and Martlet's voice-activity detector) with a fixture loopback on a simulated
/// clock: a synthesized video voice plays 0-3 s, the video is paused 3-6 s (the loopback delivers nothing at all, like a real
/// one), plays again 6-9 s, then nothing plays. It reports whether the stream stayed continuous and whether the pause ended
/// the first utterance, as it must for always listening to send it, and runs the production matcher (PcEcho) that leaves out
/// your own voice when this PC plays it back on fixed samples.</summary>
internal static class PcAudioCheck
{
    private const int Rate = 48_000;
    private const int Out = 16_000;
    private const int Seconds = 12;
    private const int Packet = Rate / 100;
    private const long Origin = 10_000_000_000;
    private static readonly CaptureSourceFormat Stereo16 = new(Rate, 2, 16, DeviceSampleEncoding.IntegerPcm);

    internal static async Task<object> RunAsync(string dataDirectory, CancellationToken cancellation)
    {
        var saved = Saved(dataDirectory);
        var probe = WasapiPcAudioSourceFactory.Probe(cancellationToken: cancellation);
        var watch = Stopwatch.StartNew();
        var clock = new SimulatedClock();
        var devices = new PcAudioCaptureFactory(new FixtureSources(clock), clock);
        var pcm = await RecordAsync(devices, cancellation);
        var elapsed = watch.ElapsedMilliseconds;

        var recordedSeconds = Math.Round(pcm.Length / 2.0 / Out, 2);
        var segments = Segments(pcm);
        var pauseSpeechFrames = SpeechFrames(pcm, 4.0, 5.7);
        var continuous = recordedSeconds is >= Seconds - 0.2 and <= Seconds + 0.05;
        var endedInPause = segments.Count > 0 && segments[0].EndedAt is >= 3.0 and <= 4.6;
        var resumed = segments.Count == 2 && segments[1].Start is >= 5.5 and <= 6.4;
        var ownVoice = OwnVoice();
        var ownVoiceOk = ownVoice.All(sample => sample.LeftOut == sample.Expected);
        var ok = continuous && endedInPause && resumed && pauseSpeechFrames == 0 && devices.WithoutMartlet == true && ownVoiceOk;
        return new
        {
            ok,
            hearPc = saved.HearPc,
            hearPcSource = saved.Source,
            handsFree = saved.HandsFree,
            reduceEcho = saved.ReduceEcho,
            windows = new
            {
                withoutMartlet = probe.WithoutMartlet,
                format = probe.Format is { } format ? $"{format.SampleRate} Hz, {format.Channels} ch, {format.BitsPerSample}-bit {format.Encoding}" : null,
                problem = probe.Problem,
                recorded = false,
                fallback = probe.WithoutMartlet ? null : "the default output's loopback, Martlet included; listening to the PC holds off while Martlet speaks"
            },
            rehearsal = new
            {
                scene = "fixture loopback on a simulated clock (nothing recorded or played): a synthesized video voice 0-3 s, paused " +
                    "3-6 s with no packets at all, playing 6-9 s, then nothing",
                seconds = Seconds,
                elapsedMs = elapsed,
                deliveredSeconds = 6,
                recordedSeconds,
                continuous,
                segments = segments.Select(s => new { startS = s.Start, endS = s.End, endedAtS = s.EndedAt }).ToArray(),
                endedInPause,
                resumed,
                pauseSpeechFrames
            },
            yourVoice = new
            {
                rule = "a line the PC played that mostly repeats, in order, what the microphone heard you say just before (or " +
                    "while it played) is your own voice played back on this PC and is left out, so Martlet never answers you twice",
                share = PcEcho.Share,
                ok = ownVoiceOk,
                samples = ownVoice.Select(sample => new
                {
                    scene = sample.Scene, spoken = sample.Spoken, played = sample.Played, expected = sample.Expected, leftOut = sample.LeftOut
                }).ToArray()
            }
        };
    }

    private sealed record OwnVoiceSample(string Scene, string Spoken, string Played, bool Expected, bool LeftOut);

    // The production matcher (PcEcho) on what the talk window compares: your words and a line the PC played meanwhile. The first
    // two are the reports on a PC whose voice changer played the user's voice back (the companion is called Jane).
    private static OwnVoiceSample[] OwnVoice() =>
    [
        Sample("your voice played back", "Hello Jane.", "Hello, Jane.", true),
        Sample("your voice played back, transcribed differently", "Why is that the way?", "Why is that the right?", true),
        Sample("your voice played back, transcribed the same", "Do you like being called that?", "Do you like being called that?", true),
        Sample("part of what you said played back", "Okay, so I was thinking we could watch the next episode tonight. What do you think?",
            "we could watch the next episode tonight", true),
        Sample("a video while you talk", "Can you pause the video?", "And now the weather for the weekend.", false),
        Sample("a video you quote", "Ha, he said rain all week.",
            "Expect rain all week across the region, with flooding in the north.", false)
    ];

    private static OwnVoiceSample Sample(string scene, string spoken, string played, bool expected) =>
        new(scene, spoken, played, expected, PcEcho.Repeats(played, [spoken]));

    private sealed record Choices(bool HearPc, string Source, bool HandsFree, bool ReduceEcho);

    // talk-preferences.json (Martlet.Desktop's TalkPreferences): HearPc is off unless saved on; files older than version 2
    // listen always.
    private static Choices Saved(string directory)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "talk-preferences.json")));
            var root = document.RootElement;
            bool? Flag(string name) => root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean() : null;
            var version = root.TryGetProperty("Version", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
            var hear = Flag("HearPc");
            return new(hear ?? false, hear is null ? "default" : "saved", version < 2 || (Flag("HandsFree") ?? true), Flag("ReduceEcho") ?? true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(false, "default", true, true);
        }
    }

    private static async Task<byte[]> RecordAsync(ICaptureDeviceFactory devices, CancellationToken cancellation)
    {
        var session = Guid.NewGuid();
        await using var capture = new MicrophoneCapture(session, devices);
        var request = new CaptureRequest(new CorrelationIds { SessionId = session, TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() }, 1,
            new InputSelection(InputPolicy.FollowDefaultOnNextPress), TimeSpan.FromSeconds(Seconds), DateTimeOffset.UtcNow.AddSeconds(25));
        var run = capture.Press(request, new CaptureAuthorization(request, true), cancellation);
        var terminal = await run.Completion.WaitAsync(TimeSpan.FromSeconds(60), cancellation);
        await run.DeviceRelease.WaitAsync(TimeSpan.FromSeconds(10), cancellation);
        using var utterance = run.TakeUtterance() ?? throw new InvalidOperationException(
            $"The rehearsal recorded nothing ({terminal.State}, {terminal.Error?.Code}).");
        var pcm = new byte[utterance.ByteCount];
        utterance.CopyPcmTo(pcm);
        return pcm;
    }

    private sealed record Segment(double Start, double End, double EndedAt);

    // What always listening's voice-activity detector (the default settings it uses for what the PC plays) makes of the stream.
    private static List<Segment> Segments(byte[] pcm)
    {
        var detector = new EnergyVoiceActivityDetector(new VoiceActivitySettings());
        var segments = new List<Segment>();
        var frames = pcm.Length / EnergyVoiceActivityDetector.FrameBytes;
        for (var i = 0; i < frames; i++)
        {
            var transition = detector.Process(pcm.AsSpan(i * EnergyVoiceActivityDetector.FrameBytes, EnergyVoiceActivityDetector.FrameBytes));
            if (transition == VoiceActivityTransition.SpeechEnded)
                segments.Add(new(Time(detector.SpeechStartFrame), Time(detector.SpeechEndFrame), Time(i + 1)));
        }
        if (detector.Speaking) segments.Add(new(Time(detector.SpeechStartFrame), Time(frames), Time(frames)));
        return segments;

        static double Time(int frame) => Math.Round(frame * 0.02, 2);
    }

    private static int SpeechFrames(byte[] pcm, double from, double to)
    {
        var detector = new EnergyVoiceActivityDetector(new VoiceActivitySettings());
        var count = 0;
        for (var i = 0; i < pcm.Length / EnergyVoiceActivityDetector.FrameBytes; i++)
        {
            detector.Process(pcm.AsSpan(i * EnergyVoiceActivityDetector.FrameBytes, EnergyVoiceActivityDetector.FrameBytes));
            if (detector.Speaking && i * 0.02 >= from && i * 0.02 < to) count++;
        }
        return count;
    }

    private static bool Playing(double t) => t < 3 || t >= 6 && t < 9;

    /// <summary>100 ns ticks, moved on 10 ms by each poll of the fixture loopback, so the rehearsal runs faster than real time.</summary>
    private sealed class SimulatedClock : TimeProvider
    {
        private long now = Origin;
        public long Now { get => Volatile.Read(ref now); set => Volatile.Write(ref now, value); }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Now;
        public static long At(long sample) => Origin + sample * TimeSpan.TicksPerSecond / Rate;
    }

    private sealed class FixtureSources(SimulatedClock clock) : IPcAudioSourceFactory
    {
        public IPcAudioSource Open(CancellationToken cancellationToken) => new Loopback(clock);
    }

    // Like Windows' process loopback (48 kHz stereo PCM16, Martlet left out): each poll is 10 ms later and returns what played
    // in those 10 ms, or nothing at all while the video is paused or nothing plays.
    private sealed class Loopback(SimulatedClock clock) : IPcAudioSource
    {
        private static readonly double[][] Vowels = [[650, 1080, 2650], [300, 2200, 2950], [500, 900, 2500]];
        private long position;
        public bool WithoutMartlet => true;
        public CaptureSourceFormat Format => Stereo16;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }

        public CapturePacket Read(Span<byte> destination)
        {
            var start = position;
            position += Packet;
            clock.Now = SimulatedClock.At(position);
            if (!Playing(start / (double)Rate)) return new(0);
            for (var i = 0; i < Packet; i++)
            {
                var t = (start + i) / (double)Rate;
                var value = (short)Math.Clamp(Math.Round(0.12 * EchoCheck.Voice(t, 140, Vowels, 0.25) * short.MaxValue), short.MinValue, short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(destination[(i * 4)..], value);
                BinaryPrimitives.WriteInt16LittleEndian(destination[(i * 4 + 2)..], value);
            }
            return new(Packet * 4);
        }
    }
}
