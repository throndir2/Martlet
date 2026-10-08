using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Martlet.Audio;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Sherpa;

namespace Martlet.Mcp;

/// <summary>sound_digest_check: Companion › Listening › Describe PC sounds. Reads the saved choices (Hear what this PC plays, off
/// by default; Describe PC sounds, on by default) and the desktop's sound-digest.json (on, the judge, counts and times; never a
/// line), then rehearses the production path with a FIXTURE clip: synthesized music with hand claps, played by a fixture loopback
/// on a simulated clock through PcAudioCaptureFactory, MicrophoneCapture and the capture normalizer into the PcSoundBuffer, then
/// one tick of the SoundDigestScheduler with the CPU sound tagger bundled in martletDirectory (CpuSoundJudge: the sherpa-onnx
/// Zipformer AudioSet tagger and SoundDigest.Line). Returns the tagger's labels, the line and how long it took. An optional
/// 16 kHz mono 16-bit wavFile is tagged the same way. Nothing is recorded, played, sent or saved.</summary>
internal static class SoundDigestCheck
{
    private const int Rate = 48_000;
    private const int Seconds = 12;
    private const int MusicSeconds = 10;
    private const int Packet = Rate / 100;
    private static readonly CaptureSourceFormat Stereo16 = new(Rate, 2, 16, DeviceSampleEncoding.IntegerPcm);

    internal static async Task<object> RunAsync(string dataDirectory, string martletDirectory, string? wavFile, CancellationToken cancellation)
    {
        var saved = Saved(dataDirectory);
        var status = Status(dataDirectory);
        var judges = await JudgesAsync(dataDirectory, cancellation);
        var included = SoundTagger.Included(martletDirectory);
        if (!included)
            return new
            {
                ok = false, saved, status, judges,
                tagger = new { included, martletDirectory, runtime = SherpaComponents.RuntimeDirectory(martletDirectory) is not null }
            };
        using var judge = new CpuSoundJudge(new SoundTagger(martletDirectory));
        var watch = Stopwatch.StartNew();
        // Loads the model once with a short tone, so the rehearsal's time is the tagging alone.
        await judge.TagAsync(new float[PcSoundBuffer.SampleRate], cancellation);
        var loadMs = watch.ElapsedMilliseconds;

        var clock = new PcAudioCheck.SimulatedClock();
        var buffer = new PcSoundBuffer(clock: clock);
        SoundDigestLine? posted = null;
        using var scheduler = new SoundDigestScheduler(buffer, () => judge, () => false, line => posted = line,
            new SoundDigestOptions { Interval = Timeout.InfiniteTimeSpan });
        scheduler.On = true;
        var devices = new PcAudioCaptureFactory(new MusicSources(clock), clock, buffer);
        var recordedBytes = (await PcAudioCheck.RecordAsync(devices, cancellation)).Length;
        var bufferedSeconds = Math.Round(buffer.Buffered.TotalSeconds, 2);
        var clip = buffer.Latest(scheduler.Options.Clip);
        var activeShare = Math.Round(SoundDigest.ActiveShare(clip), 2);
        var tagStarted = watch.ElapsedMilliseconds;
        var tags = await judge.TagAsync(clip, cancellation);
        var tagMs = watch.ElapsedMilliseconds - tagStarted;
        Array.Clear(clip);
        var step = scheduler.Tick();
        await scheduler.Idle.WaitAsync(TimeSpan.FromSeconds(30), cancellation);
        var digest = scheduler.Status;
        scheduler.On = false;

        object? file = null;
        if (wavFile is not null)
        {
            var samples = ReadWave(wavFile);
            var started = watch.ElapsedMilliseconds;
            var heard = await judge.TagAsync(samples, cancellation);
            file = new
            {
                file = Path.GetFileName(wavFile), seconds = Math.Round(samples.Length / (double)PcSoundBuffer.SampleRate, 2),
                tags = heard.Take(8).Select(t => new { name = t.Name, score = Math.Round(t.Probability, 3) }),
                line = SoundDigest.Line(heard.Select(t => new SoundTag(t.Name, t.Probability))),
                ms = watch.ElapsedMilliseconds - started
            };
            Array.Clear(samples);
        }
        var ok = step == SoundDigestStep.Started && posted is not null && buffer.Buffered == TimeSpan.Zero;
        return new
        {
            ok,
            saved,
            status,
            judges,
            tagger = new { included, judge = judge.Name, model = "Zipformer small AudioSet tagger (k2-fsa, Apache-2.0), int8, 1 thread", loadMs },
            rehearsal = new
            {
                fixture = "FIXTURE: synthesized music (a C-G-Am-F chord loop with bass, kick and hi-hat) with hand claps from 5 s, " +
                    $"{MusicSeconds} s, then silence, through a fixture loopback on a simulated clock. Not a real recording.",
                recordedSeconds = Math.Round(recordedBytes / 2.0 / PcSoundBuffer.SampleRate, 2),
                bufferedSeconds,
                clipSeconds = scheduler.Options.Clip.TotalSeconds,
                activeShare,
                tags = tags.Take(10).Select(t => new { name = t.Name, score = Math.Round(t.Probability, 3) }),
                tagMs,
                step = step.ToString(),
                line = posted?.Text,
                judged = digest.Lines,
                bufferClearedWhenOff = buffer.Buffered == TimeSpan.Zero
            },
            file
        };
    }

    private sealed record Choices(bool HearPc, bool DescribePcSounds, string Source);

    // Which judges describe PC sounds, in order, as the desktop picks them (LiveConversationController.SoundJudge): the audio model
    // of its own while it takes recordings (sense-models.json, the audio path Described), then a Thinking pool member that hears,
    // then the CPU sound tagger. Reads settings.json, sense-models.json and model-abilities.json; never a key.
    private static async Task<object> JudgesAsync(string directory, CancellationToken cancellation)
    {
        var loaded = await new SettingsStore(directory).LoadAsync(cancellation);
        var thinking = loaded.Settings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        var (senses, file) = SenseModels.Read(directory);
        var audio = SenseRouting.For(SenseKind.Audio, senses, thinking, ModelAbilities.Load(directory));
        return new
        {
            audioRoute = new { file, path = audio.Path.ToString(), model = audio.Model?.Describe(), unknown = audio.Unknown, why = audio.Why },
            order = audio.Described ? new[] { "audiomodel", "pool", "cpu" } : ["pool", "cpu"]
        };
    }

    // talk-preferences.json (Martlet.Desktop's TalkPreferences): HearPc is off and DescribePcSounds on unless saved otherwise.
    private static Choices Saved(string directory)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "talk-preferences.json")));
            var root = document.RootElement;
            bool? Flag(string name) => root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean() : null;
            var describe = Flag("DescribePcSounds");
            return new(Flag("HearPc") ?? false, describe ?? true, describe is null ? "default" : "saved");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(false, true, "default");
        }
    }

    // sound-digest.json (Martlet.Desktop's PcSoundDigest): the state, the judge, counts and times; never a line or a sound.
    private static object? Status(string directory)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "sound-digest.json")));
            var root = document.RootElement;
            string? Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            int? Number(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;
            var lastAt = Text("lastAt") is { } at && DateTimeOffset.TryParse(at, out var parsed) ? parsed : (DateTimeOffset?)null;
            return new
            {
                on = root.TryGetProperty("on", out var on) && on.ValueKind == JsonValueKind.True,
                judge = Text("judge"), judgeKind = Text("judgeKind"), runs = Number("runs"), lines = Number("lines"),
                dropped = Number("dropped"), skipped = Number("skipped"), lastStep = Text("lastStep"), lastJudge = Text("lastJudge"),
                lastAgeSeconds = lastAt is { } when ? (int?)Math.Max(0, (DateTimeOffset.UtcNow - when).TotalSeconds) : null,
                lastMs = Number("lastMs"), maximumAgeSeconds = Number("maximumAgeSeconds"), everySeconds = Number("everySeconds"),
                clipSeconds = Number("clipSeconds")
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    // A 16 kHz mono 16-bit PCM WAV (other chunks, such as LIST, are skipped).
    private static float[] ReadWave(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("wavFile must be an absolute path.");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 12 || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            throw new ArgumentException("wavFile must be a WAV file.");
        bool format = false;
        for (var at = 12; at + 8 <= bytes.Length;)
        {
            var size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at + 4));
            if (size < 0 || at + 8 + (long)size > bytes.Length) size = bytes.Length - at - 8;
            var body = bytes.AsSpan(at + 8, size);
            if (bytes.AsSpan(at, 4).SequenceEqual("fmt "u8) && size >= 16)
                format = BinaryPrimitives.ReadInt16LittleEndian(body) == 1 && BinaryPrimitives.ReadInt16LittleEndian(body[2..]) == 1 &&
                    BinaryPrimitives.ReadInt32LittleEndian(body[4..]) == PcSoundBuffer.SampleRate && BinaryPrimitives.ReadInt16LittleEndian(body[14..]) == 16;
            else if (bytes.AsSpan(at, 4).SequenceEqual("data"u8))
            {
                if (!format) break;
                var samples = new float[Math.Min(size / 2, SoundTagger.MaximumSeconds * PcSoundBuffer.SampleRate)];
                for (var i = 0; i < samples.Length; i++) samples[i] = BinaryPrimitives.ReadInt16LittleEndian(body.Slice(i * 2, 2)) / 32768f;
                return samples;
            }
            at += 8 + size + (size & 1);
        }
        throw new ArgumentException("wavFile must be 16 kHz mono 16-bit PCM.");
    }

    private sealed class MusicSources(PcAudioCheck.SimulatedClock clock) : IPcAudioSourceFactory
    {
        public IPcAudioSource Open(CancellationToken cancellationToken) => new Music(clock);
    }

    // Like Windows' process loopback (48 kHz stereo PCM16, Martlet left out): each poll is 10 ms later and returns what played.
    private sealed class Music(PcAudioCheck.SimulatedClock clock) : IPcAudioSource
    {
        // C, G, A minor, F: two seconds each, root position triads.
        private static readonly double[][] Chords = [[261.63, 329.63, 392.00], [196.00, 246.94, 293.66], [220.00, 261.63, 329.63], [174.61, 220.00, 261.63]];
        private readonly Random noise = new(7);
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
            clock.Now = PcAudioCheck.SimulatedClock.At(position);
            if (start / (double)Rate >= MusicSeconds) return new(0);
            for (var i = 0; i < Packet; i++)
            {
                var t = (start + i) / (double)Rate;
                var value = Sample(t);
                var pcm = (short)Math.Clamp(Math.Round(value * short.MaxValue), short.MinValue, short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(destination[(i * 4)..], pcm);
                BinaryPrimitives.WriteInt16LittleEndian(destination[(i * 4 + 2)..], pcm);
            }
            return new(Packet * 4);
        }

        private double Sample(double t)
        {
            var chord = Chords[(int)(t / 2) % Chords.Length];
            var beat = t % 0.5;
            double value = 0;
            // Plucked chord tones with a few harmonics, restruck each beat.
            foreach (var f in chord)
                for (var h = 1; h <= 4; h++) value += 0.05 / h * Math.Sin(2 * Math.PI * f * h * t) * Math.Exp(-3 * beat);
            value += 0.12 * Math.Sin(2 * Math.PI * chord[0] / 2 * t) * Math.Exp(-2 * beat);
            // Kick on each beat, hi-hat on the off-beat.
            value += 0.35 * Math.Sin(2 * Math.PI * (50 + 100 * Math.Exp(-30 * beat)) * beat) * Math.Exp(-12 * beat);
            var off = (t + 0.25) % 0.5;
            value += 0.04 * (noise.NextDouble() * 2 - 1) * Math.Exp(-60 * off);
            // Hand claps from 5 s: dense bursts of noise, a few per second, at random moments.
            if (t >= 5)
            {
                var clap = (t * 7.3) % 1;
                value += 0.3 * (noise.NextDouble() * 2 - 1) * Math.Exp(-40 * clap);
            }
            return Math.Clamp(value, -1, 1);
        }
    }
}
