using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Martlet.Audio;
using Martlet.Core.Contracts;
using Martlet.EchoCancellation;

namespace Martlet.Mcp;

/// <summary>echo_check: Companion › Listening › Reduce echo from my speakers. Reads the saved choice (on by default), loads the
/// production WebRTC echo canceller, and rehearses the production microphone path (MicrophoneCapture, EchoReducer, the echo
/// canceller, CaptureNormalizer) twice on the same synthesized scene: once with echo reduction and once without. No
/// microphone or speaker is opened and nothing plays: a fixture microphone (48 kHz stereo float) hears a synthesized voice
/// ("Martlet", from the fixture speakers' loopback, through a simulated room: delay, reflections, gain) plus the user's own
/// synthesized voice and faint noise, on a simulated clock with device timestamps. Scene: 0-4 s only Martlet speaks, 4.5-6 s
/// only the user, 6-8 s both at once (barge-in), then quiet. It reports how much quieter Martlet's echo got, whether the
/// user's voice was kept, what Martlet's own voice-activity detector heard in each part, and what the barge-in gate
/// (TalkOverDetector with the capture's echo timeline) made of each part: Martlet's echo and a short sound never talk over it.</summary>
internal static class EchoCheck
{
    private const int Rate = 48_000;
    private const int Out = 16_000;
    private const int Seconds = 9;
    private const int Packet = Rate / 100;
    private const long Origin = 10_000_000_000;

    internal static async Task<object> RunAsync(string dataDirectory, int? delayMs, CancellationToken cancellation)
    {
        var delay = delayMs ?? 60;
        if (delay is < 0 or > 300) throw new ArgumentException("delayMs must be 0-300.");
        var (reduceEcho, source) = ReduceEcho(dataDirectory);
        var (bargeIn, bargeInSource) = BargeIn(dataDirectory);
        var (wordCheckValue, wordCheckSource, _) = UtteranceFilterCheck.Saved(dataDirectory);
        var wordCheck = wordCheckValue.ToString();
        string? problem = null;
        try { WebRtcEchoCanceller.Create().Dispose(); }
        catch (Exception error) when (error is not OperationCanceledException) { problem = error.GetType().Name + ": " + error.Message; }
        if (problem is not null)
            return new { ok = false, reduceEcho, reduceEchoSource = source, bargeIn, bargeInSource, wordCheck, wordCheckSource, canceller = (string?)null, cancellerProblem = problem };

        var scene = Scene.Create(delay);
        var watch = Stopwatch.StartNew();
        var clock = new SimulatedClock();
        var reducer = new EchoReducer(new FixtureMicrophones(scene, clock), new FixtureSpeakers(scene, clock), WebRtcEchoCanceller.Create, clock);
        var timeline = new EchoTimeline(TimeSpan.FromSeconds(Seconds + 1));
        byte[] with, without;
        EchoReductionReport report;
        try
        {
            with = await RecordAsync(reducer.For(null, timeline), cancellation);
            report = reducer.Report;
        }
        finally { reducer.Dispose(); }
        without = await RecordAsync(new FixtureMicrophones(scene, new SimulatedClock()), cancellation);
        var elapsed = watch.ElapsedMilliseconds;

        var speechWith = Speaking(with);
        var speechWithout = Speaking(without);
        var echoOnly = Compare(with, without, 1.5, 4.0);
        var settling = Compare(with, without, 0.0, 1.5);
        var nearEnd = Compare(with, without, 4.6, 6.0);
        var bothWith = Level(with, 6.2, 8.0);
        var bothWithout = Level(without, 6.2, 8.0);
        var nearAlone = Level(scene.NearEnd16, 6.2, 8.0);
        var echoHeardWith = Count(speechWith, 1.0, 4.0);
        var echoHeardWithout = Count(speechWithout, 1.0, 4.0);
        var userHeardWith = Count(speechWith, 4.5, 6.0);
        var bargeInHeardWith = Count(speechWith, 6.0, 8.0);
        // Talking over Martlet, as always listening judges it: the production detector on the cleaned microphone, with the
        // capture's own record of which frames were the speakers' sound.
        var martletOver = TalkOver(with, timeline, 0.0, 4.0);
        var userOver = TalkOver(with, timeline, 4.5, 6.0);
        var shortOver = TalkOver(with, timeline, 4.5, 5.3);
        var bothOver = TalkOver(with, timeline, 6.0, 8.0);
        var talkOverOk = !martletOver.TalkedOver && !shortOver.TalkedOver && bothOver.TalkedOver &&
            bothOver.AfterMs >= TalkOverDetector.Required.TotalMilliseconds;
        var ok = report.State == EchoReductionState.Active && echoOnly.ReducedDb >= 20 && nearEnd.ReducedDb <= 3 &&
            echoHeardWith == 0 && echoHeardWithout > 0 && userHeardWith > 0 && bargeInHeardWith > 0 && talkOverOk;
        return new
        {
            ok,
            reduceEcho,
            reduceEchoSource = source,
            bargeIn,
            bargeInSource,
            wordCheck,
            wordCheckSource,
            canceller = "WebRTC AEC3",
            rehearsal = new
            {
                scene = "fixture devices on a simulated clock (no microphone, nothing played)",
                delayMs = delay,
                seconds = Seconds,
                elapsedMs = elapsed,
                state = report.State.ToString(),
                frames = report.Frames,
                speakerFrames = report.SpeakerFrames,
                deviceReducedDb = report.ReducedDb,
                martletOnly = new
                {
                    withoutDb = echoOnly.WithoutDb, withDb = echoOnly.WithDb, reducedDb = echoOnly.ReducedDb,
                    firstSecondsReducedDb = settling.ReducedDb,
                    speechFramesWithout = echoHeardWithout, speechFramesWith = echoHeardWith
                },
                userOnly = new { withoutDb = nearEnd.WithoutDb, withDb = nearEnd.WithDb, keptDb = -nearEnd.ReducedDb, speechFramesWith = userHeardWith },
                bothTalking = new { withoutDb = bothWithout, withDb = bothWith, userAloneDb = nearAlone, speechFramesWith = bargeInHeardWith },
                talkOver = new
                {
                    ok = talkOverOk,
                    requiredMs = (int)TalkOverDetector.Required.TotalMilliseconds,
                    gapMs = (int)TalkOverDetector.Gap.TotalMilliseconds,
                    speakersRemovedDb = EchoTimeline.SpeakersRemovedDb,
                    martletOnly = martletOver.Report(),
                    userOnly = userOver.Report(),
                    shortSound = shortOver.Report(),
                    bothTalking = bothOver.Report()
                }
            }
        };
    }

    // talk-preferences.json (Martlet.Desktop's TalkPreferences): ReduceEcho is on unless saved off.
    private static (bool On, string Source) ReduceEcho(string directory)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "talk-preferences.json")));
            return document.RootElement.TryGetProperty("ReduceEcho", out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? (value.GetBoolean(), "saved") : (true, "default");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return (true, "default"); }
    }

    // talk-preferences.json (Martlet.Desktop's TalkPreferences): barge-in is opt-in, off unless saved on; files older than
    // version 3 had it on only because it was the old default, so they read as off ("reset").
    private static (bool On, string Source) BargeIn(string directory)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "talk-preferences.json")));
            var root = document.RootElement;
            if (!root.TryGetProperty("BargeIn", out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return (false, "default");
            var version = root.TryGetProperty("Version", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
            return version < 3 ? (false, value.GetBoolean() ? "reset" : "saved") : (value.GetBoolean(), "saved");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return (false, "default"); }
    }

    private static async Task<byte[]> RecordAsync(ICaptureDeviceFactory devices, CancellationToken cancellation)
    {
        var session = Guid.NewGuid();
        await using var microphone = new MicrophoneCapture(session, devices);
        var request = new CaptureRequest(new CorrelationIds { SessionId = session, TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() }, 1,
            new InputSelection(InputPolicy.FollowDefaultOnNextPress), TimeSpan.FromSeconds(Seconds), DateTimeOffset.UtcNow.AddSeconds(25));
        var run = microphone.Press(request, new CaptureAuthorization(request, true), cancellation);
        var terminal = await run.Completion.WaitAsync(TimeSpan.FromSeconds(60), cancellation);
        await run.DeviceRelease.WaitAsync(TimeSpan.FromSeconds(10), cancellation);
        using var utterance = run.TakeUtterance() ?? throw new InvalidOperationException(
            $"The rehearsal recorded nothing ({terminal.State}, {terminal.Error?.Code}).");
        var pcm = new byte[utterance.ByteCount];
        utterance.CopyPcmTo(pcm);
        return pcm;
    }

    private sealed record Comparison(double WithoutDb, double WithDb, double ReducedDb);

    private static Comparison Compare(byte[] with, byte[] without, double from, double to)
    {
        var a = Level(without, from, to);
        var b = Level(with, from, to);
        return new(a, b, Math.Round(a - b, 1));
    }

    private static double Level(byte[] pcm, double from, double to)
    {
        int start = (int)(from * Out), end = Math.Min((int)(to * Out), pcm.Length / 2);
        double squares = 0;
        for (var i = start; i < end; i++)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2)) / 32768.0;
            squares += sample * sample;
        }
        return Math.Round(10 * Math.Log10(squares / Math.Max(1, end - start) + 1e-12), 1);
    }

    private static double Level(float[] samples, double from, double to)
    {
        int start = (int)(from * Out), end = Math.Min((int)(to * Out), samples.Length);
        double squares = 0;
        for (var i = start; i < end; i++) squares += samples[i] * samples[i];
        return Math.Round(10 * Math.Log10(squares / Math.Max(1, end - start) + 1e-12), 1);
    }

    // Martlet's own hands-free voice-activity detector over the recording: whether each 20 ms frame counted as speech.
    private static bool[] Speaking(byte[] pcm)
    {
        var detector = new EnergyVoiceActivityDetector(new VoiceActivitySettings());
        var frames = new bool[pcm.Length / EnergyVoiceActivityDetector.FrameBytes];
        for (var i = 0; i < frames.Length; i++)
        {
            detector.Process(pcm.AsSpan(i * EnergyVoiceActivityDetector.FrameBytes, EnergyVoiceActivityDetector.FrameBytes));
            frames[i] = detector.Speaking;
        }
        return frames;
    }

    private static int Count(bool[] frames, double from, double to)
    {
        var count = 0;
        for (var i = (int)(from * 50); i < Math.Min((int)(to * 50), frames.Length); i++) if (frames[i]) count++;
        return count;
    }

    /// <summary>What talking over Martlet heard in one part of the scene: loud 20 ms frames that were a voice the speakers don't
    /// explain (<c>userFrames</c>) or the speakers' own sound (<c>speakerFrames</c>), the 10 ms frames the echo timeline gave
    /// each source, and whether (and how long after the part began) the user talked over Martlet.</summary>
    private sealed record TalkOverPart(int UserFrames, int SpeakerFrames, int RoomTimeline, int UserTimeline, int SpeakersTimeline,
        bool TalkedOver, int? AfterMs)
    {
        public object Report() => new
        {
            userFrames = UserFrames, speakerFrames = SpeakerFrames,
            timeline = new { room = RoomTimeline, user = UserTimeline, speakers = SpeakersTimeline },
            talkedOver = TalkedOver, afterMs = AfterMs
        };
    }

    private static TalkOverPart TalkOver(byte[] pcm, EchoTimeline timeline, double from, double to)
    {
        // Voice activity runs from the start of the recording (its noise floor learns the room, as always listening's does);
        // the talk-over count starts with the part.
        var detector = new EnergyVoiceActivityDetector(new VoiceActivitySettings());
        var over = new TalkOverDetector();
        int first = (int)(from * 50), last = Math.Min((int)(to * 50), pcm.Length / EnergyVoiceActivityDetector.FrameBytes);
        int? after = null;
        for (var i = 0; i < last; i++)
        {
            detector.Process(pcm.AsSpan(i * EnergyVoiceActivityDetector.FrameBytes, EnergyVoiceActivityDetector.FrameBytes));
            if (i < first) continue;
            var speakers = timeline.Speakers((long)i * EnergyVoiceActivityDetector.FrameSamples, EnergyVoiceActivityDetector.FrameSamples);
            if (over.Process(detector.LastFrameLoud, speakers) && after is null) after = (i + 1 - first) * 20;
        }
        int room = 0, user = 0, speakerTimeline = 0;
        for (var frame = (int)(from * 100); frame < Math.Min((int)(to * 100), timeline.Frames); frame++)
        {
            switch (timeline[frame])
            {
                case HeardSource.Room: room++; break;
                case HeardSource.User: user++; break;
                case HeardSource.Speakers: speakerTimeline++; break;
            }
        }
        return new(over.UserFrames, over.SpeakerFrames, room, user, speakerTimeline, over.Sustained, after);
    }

    /// <summary>The synthesized scene at 48 kHz: what the speakers play (Martlet's voice while it "speaks"), what the microphone
    /// hears (the user's voice, Martlet's voice through the room, faint noise) and the user's voice alone at 16 kHz.</summary>
    private sealed class Scene
    {
        public required float[] Speaker { get; init; }
        public required float[] Microphone { get; init; }
        public required float[] NearEnd16 { get; init; }
        public static bool Playing(long sample) => sample < 4 * Rate || sample >= 6 * Rate && sample < 8 * Rate;
        private static bool Talking(double t) => t >= 4.5 && t < 8;

        public static Scene Create(int delayMs)
        {
            var total = (Seconds + 1) * Rate;
            var speaker = new float[total];
            var microphone = new float[total];
            double[][] martletVowels = [[800, 1150, 2900], [350, 2000, 2800], [450, 800, 2830]];
            double[][] userVowels = [[730, 1090, 2440], [270, 2290, 3010], [570, 840, 2410]];
            for (var i = 0; i < total; i++)
                if (Playing(i)) speaker[i] = (float)(0.12 * Voice(i / (double)Rate, 205, martletVowels, 0.25));
            // The room: Martlet's voice reaches the microphone after the delay, with a few reflections, about 6 dB down.
            (double Seconds, double Gain)[] room = [(0, 0.5), (0.003, 0.22), (0.007, -0.14), (0.015, 0.08), (0.031, 0.04)];
            var random = new Random(7);
            for (var i = 0; i < total; i++)
            {
                double echo = 0;
                foreach (var (seconds, gain) in room)
                {
                    var j = i - (int)Math.Round((delayMs / 1000.0 + seconds) * Rate);
                    if (j >= 0) echo += gain * speaker[j];
                }
                var t = i / (double)Rate;
                var user = Talking(t) ? 0.1 * Voice(t, 115, userVowels, 0.3) : 0;
                microphone[i] = (float)(user + echo + 0.0006 * (random.NextDouble() * 2 - 1));
            }
            var near = new float[Seconds * Out];
            for (var i = 0; i < near.Length; i++)
            {
                var t = i / (double)Out;
                near[i] = Talking(t) ? (float)(0.1 * Voice(t, 115, userVowels, 0.3)) : 0f;
            }
            return new() { Speaker = speaker, Microphone = microphone, NearEnd16 = near };
        }

        // A speech-like voice: a pulse train at the pitch shaped by three vowel formants, in syllables with short gaps.
        private static double Voice(double t, double pitch, double[][] vowels, double syllable) => EchoCheck.Voice(t, pitch, vowels, syllable);
    }

    /// <summary>A speech-like voice at time <paramref name="t"/> (seconds): a pulse train at the pitch shaped by three vowel
    /// formants, in syllables with short gaps. Also pc_audio_check's synthesized video voice.</summary>
    internal static double Voice(double t, double pitch, double[][] vowels, double syllable)
    {
        var index = (int)(t / syllable);
        var phase = t / syllable - index;
        var envelope = phase < 0.85 ? Math.Sin(Math.PI * phase / 0.85) : 0;
        if (envelope == 0) return 0;
        var vowel = vowels[index % vowels.Length];
        var wobble = pitch * (1 + 0.04 * Math.Sin(2 * Math.PI * 3 * t));
        double value = 0;
        for (var harmonic = 1; harmonic * wobble < 4000; harmonic++)
        {
            var frequency = harmonic * wobble;
            var weight = vowel.Sum(f => 1 / (1 + Math.Pow((frequency - f) / 90, 2))) / harmonic;
            value += weight * Math.Sin(2 * Math.PI * frequency * t + harmonic);
        }
        return value * envelope;
    }

    /// <summary>One clock for both fixture devices and the echo reducer: 100 ns ticks, moved on by the microphone as it
    /// delivers each 10 ms packet, so the rehearsal runs faster than real time with real-looking timestamps.</summary>
    private sealed class SimulatedClock : TimeProvider
    {
        private long now = Origin;
        public long Now { get => Volatile.Read(ref now); set => Volatile.Write(ref now, value); }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Now;
        public static long At(long sample) => Origin + sample * TimeSpan.TicksPerSecond / Rate;
    }

    private static readonly CaptureSourceFormat Stereo48 = new(Rate, 2, 32, DeviceSampleEncoding.IeeeFloat);

    private sealed class FixtureMicrophones(Scene scene, SimulatedClock clock) : ICaptureDeviceFactory
    {
        public ICaptureDevice Open(CaptureDeviceAccess access, CancellationToken cancellationToken)
        {
            access.CheckAuthorization();
            return new Microphone(scene, clock);
        }
    }

    private sealed class Microphone(Scene scene, SimulatedClock clock) : ICaptureDevice
    {
        private long position;
        public CaptureSourceFormat Format => Stereo48;
        public void Start(CancellationToken cancellationToken) { }
        public void Stop() { }
        public void Dispose() { }

        public CapturePacket Read(Span<byte> destination, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stamp = SimulatedClock.At(position);
            for (var i = 0; i < Packet; i++)
            {
                var index = position + i;
                var value = index < scene.Microphone.Length ? scene.Microphone[index] : 0f;
                BinaryPrimitives.WriteSingleLittleEndian(destination[(i * 8)..], value);
                BinaryPrimitives.WriteSingleLittleEndian(destination[(i * 8 + 4)..], value);
            }
            position += Packet;
            clock.Now = SimulatedClock.At(position);
            return new(Packet * 8, Timestamp: stamp);
        }
    }

    private sealed class FixtureSpeakers(Scene scene, SimulatedClock clock) : IEchoReferenceFactory
    {
        public IEchoReference Open(string? outputEndpointId, CancellationToken cancellationToken) => new Loopback(scene, clock);
    }

    // Like a WASAPI loopback: packets of what played, 10 ms after it played, none while nothing plays, and a discontinuity
    // flag on the first packet after a pause.
    private sealed class Loopback(Scene scene, SimulatedClock clock) : IEchoReference
    {
        private const long Latency = TimeSpan.TicksPerSecond / 100;
        private long position;
        private bool paused;
        public CaptureSourceFormat Format => Stereo48;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }

        public CapturePacket Read(Span<byte> destination)
        {
            var available = clock.Now - Latency;
            while (SimulatedClock.At(position + Packet) <= available && !Scene.Playing(position))
            {
                position += Packet;
                paused = true;
            }
            if (SimulatedClock.At(position + Packet) > available || position + Packet > scene.Speaker.Length) return new(0);
            for (var i = 0; i < Packet; i++)
            {
                var value = scene.Speaker[position + i];
                BinaryPrimitives.WriteSingleLittleEndian(destination[(i * 8)..], value);
                BinaryPrimitives.WriteSingleLittleEndian(destination[(i * 8 + 4)..], value);
            }
            var packet = new CapturePacket(Packet * 8, paused, SimulatedClock.At(position));
            position += Packet;
            paused = false;
            return packet;
        }
    }
}
