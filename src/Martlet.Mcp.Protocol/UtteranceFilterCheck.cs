using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Martlet.Audio;
using Martlet.Conversation;
using Martlet.Providers;

namespace Martlet.Mcp;

/// <summary>utterance_filter_check: Companion › Listening › Word check. Runs the production utterance filter
/// (<see cref="UtteranceFilter"/>) and barge-in policy (<see cref="BargeInPolicy"/>) on transcripts with their evidence (given
/// samples, or a fixed set with the expected outcome, including evidence measured from whisper.cpp and Parakeet on this PC),
/// and, when Parakeet is downloaded on this PC, on audio fixtures through the real local speech-to-text path: phrases and
/// non-words synthesized with a Windows voice, a hummed tone, coughs and noise, each run through the production voice-activity
/// detector and barge-in gate (<see cref="BargeInGate"/>) the way always listening hears someone talking over Martlet, with the
/// measured time from the start of their voice to the decision. Nothing is recorded or played, and nothing leaves this PC.</summary>
internal static class UtteranceFilterCheck
{
    private const int Rate = 16_000;
    private const int MaximumSamples = 64;

    private sealed record Sample(string Name, string Text, UtteranceContext Context, bool? ExpectKeep = null, bool? ExpectInterrupt = null,
        PlaybackMode Playback = PlaybackMode.Reply);

    internal static async Task<object> RunAsync(JsonElement arguments, string dataDirectory, string martletDirectory, string speechDirectory,
        CancellationToken cancellation)
    {
        var (saved, savedSource, bargeIn) = Saved(dataDirectory);
        var sensitivity = Optional(arguments, "sensitivity") is { } chosen
            ? Enum.TryParse<ListeningSensitivity>(chosen, true, out var parsed) && Enum.IsDefined(parsed) ? parsed
                : throw new ArgumentException("sensitivity must be relaxed, normal or sensitive.")
            : saved;
        var samples = Samples(arguments) ?? Defaults(sensitivity == ListeningSensitivity.Normal);
        var results = samples.Select(sample => Report(sample, sensitivity)).ToArray();
        var samplesOk = results.All(r => r.Ok);
        var cost = Cost(samples, sensitivity);

        object audio;
        var audioOk = true;
        if (Bool(arguments, "audio") == false) audio = new { ran = false, reason = "audio: false" };
        else audio = await AudioAsync(martletDirectory, speechDirectory, sensitivity, cancellation, ok => audioOk = ok);

        return new
        {
            ok = samplesOk && audioOk,
            wordCheck = sensitivity.ToString(),
            savedWordCheck = saved.ToString(),
            savedWordCheckSource = savedSource,
            bargeIn,
            limits = UtteranceFilter.For(sensitivity),
            bargeInPolicy = new
            {
                voiceBeforeCheckMs = (int)BargeInPolicy.VoiceBeforeCheck(sensitivity).TotalMilliseconds,
                firstRecheckMs = (int)BargeInGate.FirstRecheck.TotalMilliseconds,
                recheckMs = (int)BargeInGate.Recheck.TotalMilliseconds,
                pauseMs = (int)BargeInGate.Pause.TotalMilliseconds,
                wordsToInterrupt = BargeInPolicy.WordsToInterrupt(sensitivity)
            },
            filterCost = cost,
            samplesOk,
            samples = results.Select(r => r.Value),
            audio
        };
    }

    private sealed record Result(bool Ok, object Value);

    private static Result Report(Sample sample, ListeningSensitivity sensitivity)
    {
        var decision = UtteranceFilter.Check(sample.Text, sample.Context, sensitivity);
        var bargeIn = BargeInPolicy.Decide(sample.Text, sample.Context, sensitivity, sample.Playback);
        var ok = (sample.ExpectKeep is null || sample.ExpectKeep == decision.Keep) &&
            (sample.ExpectInterrupt is null || sample.ExpectInterrupt == bargeIn.Interrupt);
        return new(ok, new
        {
            name = sample.Name, text = sample.Text, ok,
            keep = decision.Keep, kind = decision.Kind.ToString(), decision.Reason, decision.Words,
            shown = decision.Keep ? null : decision.Describe(sample.Text),
            interrupts = bargeIn.Interrupt, interruptReason = bargeIn.Reason, playback = sample.Playback.ToString(),
            expectKeep = sample.ExpectKeep, expectInterrupt = sample.ExpectInterrupt
        });
    }

    // How long one filter decision takes: the reply path's only addition.
    private static object Cost(IReadOnlyList<Sample> samples, ListeningSensitivity sensitivity)
    {
        for (var i = 0; i < 200; i++) foreach (var sample in samples) UtteranceFilter.Check(sample.Text, sample.Context, sensitivity);
        const int rounds = 2000;
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < rounds; i++) foreach (var sample in samples) UtteranceFilter.Check(sample.Text, sample.Context, sensitivity);
        var each = watch.Elapsed.TotalMilliseconds * 1000 / (rounds * Math.Max(1, samples.Count));
        return new { calls = rounds * samples.Count, microsecondsPerCall = Math.Round(each, 2) };
    }

    private static UtteranceContext Context(int? voicedMs = null, double? mean = null, double? noSpeech = null, double? logProbability = null,
        bool afterQuestion = false) => new()
    {
        Voiced = voicedMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null, AfterQuestion = afterQuestion,
        Evidence = mean is null && noSpeech is null && logProbability is null ? null : new TranscriptionEvidence
        {
            Engine = noSpeech is null && logProbability is null ? "parakeet" : "whisper", MeanProbability = mean,
            NoSpeechProbability = noSpeech, AverageLogProbability = logProbability
        }
    };

    /// <summary>Fixed samples with the outcome Normal must give: non-words and noise go, short answers and real words stay, a
    /// stop word interrupts a reply and a backchannel doesn't, and a song stops only when asked to. Another sensitivity only
    /// reports what it makes of them (<paramref name="expect"/> false).</summary>
    private static IReadOnlyList<Sample> Defaults(bool expect) => expect ? Expected
        : Expected.Select(sample => sample with { ExpectKeep = null, ExpectInterrupt = null }).ToArray();

    private static readonly IReadOnlyList<Sample> Expected =
    [
        new("filler", "Mmm.", Context(600), false, false),
        new("filler-question", "Hmm?", Context(400), false, false),
        new("long-hum", "Mmmmmmmmm", Context(1800), false, false),
        new("uh-huh", "Uh-huh.", Context(500), false, false),
        new("uh-huh-after-question", "Uh-huh.", Context(500, afterQuestion: true), true, false),
        new("laughter", "Haha", Context(700), false, false),
        new("laughter-tag", "[Laughter]", Context(900), false, false),
        new("cough-tag", "(coughs)", Context(300), false, false),
        new("music", "♪ ♪", Context(2000), false, false),
        new("punctuation", "...", Context(400), false, false),
        new("thank-you-whisper-noise", "Thank you.", Context(700, noSpeech: 0.55, logProbability: -0.9), false, false),
        new("thank-you-short-voice", "Thank you.", Context(250), false, false),
        new("thank-you-clear", "Thank you.", Context(450, mean: 0.95), true, false),
        new("thanks-for-watching", "Thanks for watching!", Context(900), false, false),
        new("subtitle-credit", "Subtitles by the Amara.org community", Context(1500), false, false),
        new("lone-you", "you", Context(300), false, false),
        new("lone-so", "So.", Context(300), false, false),
        new("yes-after-question", "Yes.", Context(250, afterQuestion: true), true, false),
        new("no", "No.", Context(220), true, false),
        new("stop", "Stop.", Context(300), true, true),
        new("wait", "Wait.", Context(260), true, true),
        new("okay-backchannel", "Okay.", Context(300), true, false),
        new("yeah-right", "Yeah, right.", Context(500), true, false),
        // Parakeet on this PC, as utterance_filter_check's audio measured it: "Yeah." made up from a cough, a breath or a beat
        // of music, and a Windows voice saying it.
        new("parakeet-yeah-from-noise", "Yeah.", Context(300, mean: 0.69) with
            { Evidence = new TranscriptionEvidence { Engine = "parakeet", MeanProbability = 0.69, MinimumProbability = 0.28 } }, false, false),
        new("parakeet-yeah-said", "Yeah.", Context(340) with
            { Evidence = new TranscriptionEvidence { Engine = "parakeet", MeanProbability = 0.79, MinimumProbability = 0.51 } }, true, false),
        // whisper.cpp on this PC's martlet-stt (large-v3-turbo, verbose_json): "Yeah." it wrote for two coughs, and "Stop." said.
        new("whisper-yeah-from-cough", "Yeah.", Context(380, mean: 0.07, noSpeech: 0.0, logProbability: -0.9), false, false),
        new("whisper-stop", "Stop.", Context(360, mean: 0.74, noSpeech: 0.0, logProbability: -0.17), true, true),
        new("too-many-words", "I think the second one is better.", Context(250), false, false),
        new("real-sentence", "I think the second one is better.", Context(1400), true, true),
        new("unsure-word", "Banana.", Context(500, mean: 0.32), false, false),
        new("clear-word", "Pizza.", Context(600, mean: 0.9), true, false),
        new("name", "Hey Martlet", Context(500), true, true),
        new("whisper-loop", "Thank you. Thank you. Thank you. Thank you. Thank you. Thank you.", Context(3000), false, false),
        new("song-chatter", "That's so cool.", Context(700), true, false, PlaybackMode.Song),
        new("song-stop", "Okay okay Martlet, stop singing.", Context(1500), true, true, PlaybackMode.Song)
    ];

    private static IReadOnlyList<Sample>? Samples(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("samples", out var given) || given.ValueKind == JsonValueKind.Null)
            return null;
        if (given.ValueKind != JsonValueKind.Array || given.GetArrayLength() > MaximumSamples)
            throw new ArgumentException($"samples must be at most {MaximumSamples} objects.");
        var list = new List<Sample>();
        foreach (var item in given.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new ArgumentException("Each sample must be an object.");
            var text = Optional(item, "text") ?? throw new ArgumentException("Each sample needs a text.");
            if (text.Length > 1000) throw new ArgumentException("A sample's text is at most 1000 characters.");
            var playback = Optional(item, "playback") switch
            {
                null or "reply" => PlaybackMode.Reply,
                "song" => PlaybackMode.Song,
                _ => throw new ArgumentException("playback must be reply or song.")
            };
            var evidence = new TranscriptionEvidence
            {
                Engine = Optional(item, "engine") ?? "given", MeanProbability = Number(item, "meanProbability"),
                MinimumProbability = Number(item, "minimumProbability"), NoSpeechProbability = Number(item, "noSpeechProbability"),
                AverageLogProbability = Number(item, "averageLogProbability")
            };
            var any = evidence.MeanProbability is not null || evidence.MinimumProbability is not null ||
                evidence.NoSpeechProbability is not null || evidence.AverageLogProbability is not null;
            list.Add(new(Optional(item, "name") ?? $"sample-{list.Count + 1}", text, new UtteranceContext
            {
                Voiced = Number(item, "voicedMs") is { } ms ? TimeSpan.FromMilliseconds(Math.Clamp(ms, 0, 120_000)) : null,
                Evidence = any ? evidence : null, AfterQuestion = Bool(item, "afterQuestion") ?? false,
                Names = Optional(item, "persona") is { Length: > 0 and <= 64 } persona ? [persona] : []
            }, Bool(item, "expectKeep"), Bool(item, "expectInterrupt"), playback));
        }
        return list;
    }

    // ---------- audio through Parakeet on this PC ----------

    // KeepWhenRelaxed: what Relaxed must make of it when that differs (a lone "Yeah." needs clearer evidence there).
    private sealed record Fixture(string Name, string Kind, bool ExpectKeep, bool ExpectInterrupt, bool AfterQuestion = false,
        bool? KeepWhenRelaxed = null);

    private static readonly Fixture[] Fixtures =
    [
        new("stop", "speech:Stop!", true, true),
        new("wait", "speech:Wait, hold on a second.", true, true),
        new("question", "speech:Can you tell me more about that?", true, true),
        new("yes", "speech:Yes.", true, false, AfterQuestion: true),
        new("yeah", "speech:Yeah.", true, false, KeepWhenRelaxed: false),
        new("mmm", "speech:Mmmmmm.", false, false),
        new("hmm", "speech:Hmm.", false, false),
        new("laughter", "speech:Ha ha ha ha!", false, false),
        new("hum", "hum", false, false),
        new("cough", "cough", false, false),
        new("breath", "breath", false, false),
        new("clicks", "clicks", false, false),
        new("music", "music", false, false),
        new("noise", "noise", false, false)
    ];

    private static async Task<object> AudioAsync(string martletDirectory, string speechDirectory, ListeningSensitivity sensitivity,
        CancellationToken cancellation, Action<bool> setOk)
    {
        var runtime = Martlet.Sherpa.SherpaComponents.RuntimeDirectory(martletDirectory);
        if (runtime is null) return new { ran = false, reason = "the sherpa-onnx runtime isn't in martletDirectory (build Martlet.Desktop)" };
        if (!Martlet.Sherpa.SherpaComponents.IsParakeetInstalled(speechDirectory))
            return new { ran = false, reason = "Parakeet isn't downloaded in speechDirectory (Companion > Listening on this PC)" };
        var load = Stopwatch.StartNew();
        using var engine = new Martlet.Sherpa.ParakeetEngine(speechDirectory, runtimeDirectory: runtime);
        await Task.Run(engine.Warm, cancellation);
        var loadMs = load.ElapsedMilliseconds;
        var results = new List<object>();
        var allOk = true;
        var stopDelays = new List<double>();
        foreach (var fixture in Fixtures)
        {
            cancellation.ThrowIfCancellationRequested();
            var pcm = await Task.Run(() => Synthesize(fixture.Kind), cancellation);
            var (voiced, frames, loud) = Voice(pcm);
            var full = Stopwatch.StartNew();
            var heard = await Task.Run(() => engine.Transcribe(ToFloats(pcm, 0, pcm.Length)), cancellation);
            var fullMs = full.Elapsed.TotalMilliseconds;
            var evidence = Evidence(heard);
            var context = new UtteranceContext { Voiced = voiced, Evidence = evidence, AfterQuestion = fixture.AfterQuestion };
            var decision = UtteranceFilter.Check(heard.Text, context, sensitivity);
            var bargeIn = await TalkOverAsync(engine, pcm, loud, sensitivity, fixture.AfterQuestion, cancellation);
            var expectKeep = sensitivity == ListeningSensitivity.Relaxed ? fixture.KeepWhenRelaxed ?? fixture.ExpectKeep : fixture.ExpectKeep;
            var ok = decision.Keep == expectKeep && bargeIn.Interrupted == fixture.ExpectInterrupt;
            allOk &= ok;
            if (bargeIn.Interrupted && bargeIn.AfterMs is { } after) stopDelays.Add(after);
            results.Add(new
            {
                fixture = fixture.Name, source = fixture.Kind.StartsWith("speech:", StringComparison.Ordinal) ? "Windows voice" : fixture.Kind,
                ok, seconds = Math.Round(pcm.Length / 2.0 / Rate, 2), voicedMs = (int)voiced.TotalMilliseconds, frames,
                transcript = heard.Text, evidence, transcribeMs = Math.Round(fullMs, 1),
                keep = decision.Keep, kind = decision.Kind.ToString(), decision.Reason, expectKeep,
                shown = decision.Keep ? null : decision.Describe(heard.Text),
                bargeIn = new
                {
                    interrupted = bargeIn.Interrupted, expectInterrupt = fixture.ExpectInterrupt, reason = bargeIn.Reason,
                    checks = bargeIn.Quick.Count, afterMs = bargeIn.AfterMs, quick = bargeIn.Quick
                }
            });
        }
        setOk(allOk);
        stopDelays.Sort();
        return new
        {
            ran = true, ok = allOk, engine = "Parakeet TDT 0.6B v3 (sherpa-onnx) on this PC", loadMs, wordCheck = sensitivity.ToString(),
            scene = "each fixture starts after 0.3 s of faint noise and ends with 1 s of it; Martlet is taken to be speaking the whole time",
            stopDelayMs = stopDelays.Count == 0 ? null : new { min = stopDelays[0], median = stopDelays[stopDelays.Count / 2], max = stopDelays[^1] },
            fixtures = results
        };
    }

    private sealed record QuickCheck(string Text, double? MeanProbability, double? MinimumProbability, int VoicedMs, double Ms, bool Words,
        string Reason);

    private sealed record TalkOver(bool Interrupted, string Reason, int Checks, List<QuickCheck> Quick, double? AfterMs)
    {
        public List<string> Transcripts => Quick.Select(q => q.Text).ToList();
        public List<double> CheckMs => Quick.Select(q => q.Ms).ToList();
    }

    /// <summary>Talking over Martlet as always listening hears it: the production voice-activity detector and barge-in gate fed
    /// 20 ms at a time, each quick check transcribing the stretch of voice so far (with the listener's pre-roll) through Parakeet
    /// and the production barge-in policy deciding. As in the listener, the audio keeps coming while a check runs and no other
    /// check starts until it is done; the time to the decision is the audio up to the check plus the check itself.</summary>
    private static async Task<TalkOver> TalkOverAsync(Martlet.Sherpa.ParakeetEngine engine, byte[] pcm, bool[] loud,
        ListeningSensitivity sensitivity, bool afterQuestion, CancellationToken cancellation)
    {
        var gate = new BargeInGate(sensitivity);
        var preRoll = EnergyVoiceActivityDetector.Samples(new VoiceActivitySettings().PreRoll) / EnergyVoiceActivityDetector.FrameSamples;
        var quick = new List<QuickCheck>();
        var busyUntil = 0;
        for (var frame = 0; frame < loud.Length; frame++)
        {
            if (!gate.Process(loud[frame], speakers: false, busy: frame < busyUntil)) continue;
            var from = Math.Max(0, gate.StretchStartFrame - preRoll);
            var watch = Stopwatch.StartNew();
            var heard = await Task.Run(() => engine.Transcribe(ToFloats(pcm, from * EnergyVoiceActivityDetector.FrameBytes,
                (frame + 1 - from) * EnergyVoiceActivityDetector.FrameBytes)), cancellation);
            var checkMs = watch.Elapsed.TotalMilliseconds;
            busyUntil = frame + 1 + (int)Math.Ceiling(checkMs / BargeInGate.FrameMilliseconds);
            var decision = BargeInPolicy.Decide(heard.Text, new UtteranceContext
            {
                Voiced = gate.Voice, Evidence = Evidence(heard), AfterQuestion = afterQuestion
            }, sensitivity);
            quick.Add(new(heard.Text, heard.Confidence, heard.Minimum, (int)gate.Voice.TotalMilliseconds, Math.Round(checkMs, 1),
                decision.Words.Keep, decision.Words.Reason));
            if (decision.Interrupt)
                return new(true, decision.Reason, gate.Checks, quick, Math.Round((frame + 1 - gate.StretchStartFrame) * 20 + checkMs, 0));
            if (quick.Count >= 12) return new(false, decision.Reason, gate.Checks, quick, null);
        }
        return new(false, quick.Count == 0 ? "never enough voice to check" : "no check found words that stop Martlet", gate.Checks, quick, null);
    }

    private static TranscriptionEvidence Evidence(Martlet.Sherpa.ParakeetTranscript heard) => new()
    {
        Engine = "parakeet", MeanProbability = heard.Confidence, MinimumProbability = heard.Minimum,
        WordsStart = heard.FirstToken is { } first ? TimeSpan.FromSeconds(first) : null,
        WordsEnd = heard.LastToken is { } last ? TimeSpan.FromSeconds(last) : null
    };

    /// <summary>How much of the fixture was a voice by the production detector (loud 20 ms frames, as always listening counts).</summary>
    private static (TimeSpan Voiced, int Frames, bool[] Loud) Voice(byte[] pcm)
    {
        var detector = new EnergyVoiceActivityDetector(new VoiceActivitySettings());
        var frames = pcm.Length / EnergyVoiceActivityDetector.FrameBytes;
        var loud = new bool[frames];
        var count = 0;
        for (var i = 0; i < frames; i++)
        {
            detector.Process(pcm.AsSpan(i * EnergyVoiceActivityDetector.FrameBytes, EnergyVoiceActivityDetector.FrameBytes));
            loud[i] = detector.LastFrameLoud;
            if (loud[i]) count++;
        }
        return (TimeSpan.FromMilliseconds(count * 20), frames, loud);
    }

    private static float[] ToFloats(byte[] pcm, int offset, int bytes)
    {
        bytes = Math.Min(bytes, pcm.Length - offset) & ~1;
        var samples = new float[bytes / 2];
        for (var i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(pcm, offset + i * 2) / 32768f;
        return samples;
    }

    // 16 kHz mono PCM16: 0.3 s of faint noise, the fixture, then 1 s of faint noise.
    private static byte[] Synthesize(string kind)
    {
        var random = new Random(kind.Aggregate(17, (hash, c) => unchecked(hash * 31 + c)) & 0x7fffffff);
        float[] body = kind switch
        {
            "hum" => Hum(),
            "cough" => Coughs(random),
            "breath" => Breath(random),
            "clicks" => Clicks(random),
            "music" => Music(),
            "noise" => Noise(random, 2.0, 0.03),
            _ when kind.StartsWith("speech:", StringComparison.Ordinal) => Speak(kind["speech:".Length..]),
            _ => throw new ArgumentException("Unknown fixture.")
        };
        var lead = (int)(0.3 * Rate);
        var tail = Rate;
        var all = new float[lead + body.Length + tail];
        Array.Copy(body, 0, all, lead, body.Length);
        var pcm = new byte[all.Length * 2];
        for (var i = 0; i < all.Length; i++)
        {
            var value = all[i] + (float)(random.NextDouble() * 2 - 1) * 0.001f;
            BitConverter.TryWriteBytes(pcm.AsSpan(i * 2, 2), (short)Math.Clamp(value * 32767, short.MinValue, short.MaxValue));
        }
        return pcm;
    }

    private static float[] Speak(string text)
    {
        using var stream = new MemoryStream();
        using (var voice = new System.Speech.Synthesis.SpeechSynthesizer())
        {
            voice.SetOutputToAudioStream(stream, new System.Speech.AudioFormat.SpeechAudioFormatInfo(Rate,
                System.Speech.AudioFormat.AudioBitsPerSample.Sixteen, System.Speech.AudioFormat.AudioChannel.Mono));
            voice.Speak(text);
        }
        var bytes = stream.ToArray();
        var samples = new float[bytes.Length / 2];
        for (var i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f * 0.8f;
        return samples;
    }

    // A hummed "mmmm": 1.8 s of a voiced harmonic tone gliding in pitch, with a slow swell (no words in it).
    private static float[] Hum()
    {
        var samples = new float[(int)(1.8 * Rate)];
        double phase = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            var t = i / (double)Rate;
            var pitch = 150 + 25 * Math.Sin(2 * Math.PI * 0.7 * t) + 5 * Math.Sin(2 * Math.PI * 5.5 * t);
            phase += 2 * Math.PI * pitch / Rate;
            var envelope = Math.Min(1, t / 0.15) * Math.Min(1, (1.8 - t) / 0.2);
            samples[i] = (float)(envelope * 0.25 * (Math.Sin(phase) + 0.5 * Math.Sin(2 * phase) + 0.25 * Math.Sin(3 * phase) + 0.1 * Math.Sin(4 * phase)));
        }
        return samples;
    }

    // Two coughs: short bursts of shaped noise.
    private static float[] Coughs(Random random)
    {
        var samples = new float[(int)(1.0 * Rate)];
        foreach (var start in new[] { 0.0, 0.5 })
        {
            var from = (int)(start * Rate);
            var length = (int)(0.22 * Rate);
            float previous = 0;
            for (var i = 0; i < length && from + i < samples.Length; i++)
            {
                var t = i / (double)length;
                var envelope = Math.Exp(-5 * t) * Math.Min(1, i / (0.01 * Rate));
                previous = 0.6f * previous + 0.4f * (float)(random.NextDouble() * 2 - 1);
                samples[from + i] = (float)(envelope * 0.6) * previous;
            }
        }
        return samples;
    }

    private static float[] Noise(Random random, double seconds, double level)
    {
        var samples = new float[(int)(seconds * Rate)];
        for (var i = 0; i < samples.Length; i++) samples[i] = (float)((random.NextDouble() * 2 - 1) * level);
        return samples;
    }

    // A breath into the microphone: 0.7 s of soft, low-passed noise swelling and fading.
    private static float[] Breath(Random random)
    {
        var samples = new float[(int)(0.7 * Rate)];
        float previous = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            var t = i / (double)samples.Length;
            previous = 0.85f * previous + 0.15f * (float)(random.NextDouble() * 2 - 1);
            samples[i] = (float)(Math.Sin(Math.PI * t) * 0.35) * previous;
        }
        return samples;
    }

    // Typing: 2 s of sharp clicks about every 120 ms.
    private static float[] Clicks(Random random)
    {
        var samples = new float[2 * Rate];
        for (var at = 0; at < samples.Length; at += (int)(Rate * (0.09 + random.NextDouble() * 0.06)))
            for (var i = 0; i < 160 && at + i < samples.Length; i++)
                samples[at + i] = (float)(Math.Exp(-i / 25.0) * 0.5 * (random.NextDouble() * 2 - 1));
        return samples;
    }

    // Music: 3 s of chords (a major triad moving every half second) with a beat.
    private static float[] Music()
    {
        var samples = new float[3 * Rate];
        double[][] chords = [[261.6, 329.6, 392.0], [220.0, 261.6, 329.6], [174.6, 220.0, 261.6], [196.0, 246.9, 293.7]];
        for (var i = 0; i < samples.Length; i++)
        {
            var t = i / (double)Rate;
            var chord = chords[(int)(t * 2) % chords.Length];
            var beat = Math.Exp(-((t * 4) % 1) * 4);
            samples[i] = (float)(0.12 * chord.Sum(f => Math.Sin(2 * Math.PI * f * t)) * (0.5 + 0.5 * beat));
        }
        return samples;
    }

    // ---------- the saved choice and arguments ----------

    // talk-preferences.json (Martlet.Desktop's TalkPreferences): WordCheck (0 Normal, 1 Relaxed, 2 Sensitive; Normal unless saved)
    // and BargeIn (off unless saved on, as echo_check reads it).
    internal static (ListeningSensitivity WordCheck, string Source, bool BargeIn) Saved(string directory)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "talk-preferences.json")));
            var root = document.RootElement;
            var bargeIn = root.TryGetProperty("BargeIn", out var b) && b.ValueKind == JsonValueKind.True &&
                root.TryGetProperty("Version", out var v) && v.ValueKind == JsonValueKind.Number && v.GetInt32() >= 3;
            if (root.TryGetProperty("WordCheck", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) &&
                Enum.IsDefined((ListeningSensitivity)number))
                return ((ListeningSensitivity)number, "saved", bargeIn);
            return (ListeningSensitivity.Normal, "default", bargeIn);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return (ListeningSensitivity.Normal, "default", false);
        }
    }

    private static string? Optional(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : throw new ArgumentException($"'{property}' must be a string.")
            : null;

    private static double? Number(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? number
                : throw new ArgumentException($"'{property}' must be a number.")
            : null;

    private static bool? Bool(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new ArgumentException($"'{property}' must be a boolean.")
            }
            : null;
}
