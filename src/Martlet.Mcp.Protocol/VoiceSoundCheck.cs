using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Martlet.Audio;
using Martlet.Avatar.Hosting;
using Martlet.Conversation;
using Martlet.Core.Audio;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Diagnostics;
using Martlet.Providers;

namespace Martlet.Mcp;

/// <summary>Touch zones' voice sounds ("sound:&lt;cue&gt;" entries in a zone's reaction list). voice_sounds_status reads, from a
/// data directory, the voice replies speak with and the sounds it makes alone, which are made on this PC (voice-sounds\), which
/// zones list sounds and whether each plays with this voice, and the newest desktop log lines (the last sound played or skipped,
/// and why). voice_sounds_check rehearses the production path with fixtures: the sounds each engine offers (VoiceSounds), the
/// rules (VoiceSoundGate), making a clip with a fixture host voice through ConversationRuntime.SynthesizeAsync (the engine's tag
/// alone), and playing it with PlayClipAsync on a fixture speaker paced like a real one (whole, with lip sync, and cut the moment a
/// reply's own voice starts). Fixture voices make a quiet tone (NOT AI); nothing is played aloud.</summary>
internal static class VoiceSoundCheck
{
    internal static readonly string[] Scenarios = ["sounds", "rules", "make", "play", "cut"];
    private const string VoiceModel = "chatterbox-turbo";

    internal static object Status(string dataDirectory)
    {
        var loaded = new SettingsStore(dataDirectory).LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
        var tts = loaded.Settings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Tts && r.Enabled == true);
        var voice = QuickSoundLibrary.Voice(tts);
        var engine = VoiceSounds.EngineOf(tts);
        var key = voice is { } v ? QuickSoundLibrary.Key(v.Identity, QuickSoundLibrary.Character(loaded.Settings?.Companion?.ActivePersona?.Id)) : null;
        var made = key is null ? [] : VoiceSoundLibrary.Made(dataDirectory, key);
        var supported = VoiceSounds.Supported(engine);
        var logs = LocalLogs.Directory(dataDirectory);
        var lines = Directory.Exists(logs)
            ? LocalLogs.Read(logs, LocalLogs.ThisDeviceId())
                .Where(r => r.Component == "desktop" && r.Message.StartsWith("Voice sound", StringComparison.Ordinal))
                .OrderBy(r => r.At).TakeLast(10).Select(r => new { at = r.At, message = r.Message }).ToArray()
            : [];
        var last = lines.LastOrDefault(l => l.message.Contains("played", StringComparison.Ordinal) && !l.message.StartsWith("Voice sound: made", StringComparison.Ordinal));
        var root = Path.Combine(dataDirectory, VoiceSoundLibrary.Folder);
        return new
        {
            voice = voice is { } chosen ? new { words = chosen.Words, paid = chosen.Paid, engine = engine?.Name, key } : null,
            // A paid cloud voice makes a sound only on the owner's click (Hear it on the zone).
            noSounds = VoiceSounds.NoSoundsReason(engine, voice is not null),
            sounds = supported.Select(tag => new
            {
                entry = VoiceSounds.Entry(tag.Cue), cue = tag.Cue, label = VoiceSounds.Label(tag.Cue), tag = tag.Text,
                made = made.Any(m => m.Cue == tag.Cue),
                milliseconds = made.Where(m => m.Cue == tag.Cue).Select(m => (double?)Math.Round(m.Duration.TotalMilliseconds)).FirstOrDefault()
            }).ToArray(),
            zones = CharacterTouchZones.LoadAll(dataDirectory).SelectMany(model => model.Zones
                .Select(zone => (zone, cues: (zone.Reaction.Actions ?? []).Select(VoiceSounds.CueOf).OfType<string>().ToArray()))
                .Where(z => z.cues.Length > 0)
                .Select(z => new
                {
                    model = model.ModelId, zone = z.zone.Id, enabled = z.zone.Enabled,
                    sounds = z.cues.Select(cue => new { entry = VoiceSounds.Entry(cue), plays = supported.Any(t => t.Cue == cue) }).ToArray()
                })).ToArray(),
            kept = Directory.Exists(root) ? Directory.GetDirectories(root).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)
                .Select(k => new { key = k, sounds = VoiceSoundLibrary.Made(dataDirectory, k).Select(m => m.Cue).ToArray() }).ToArray() : [],
            last = last?.message,
            log = lines
        };
    }

    internal static async Task<object> RunAsync(string? scenario, CancellationToken cancellation)
    {
        string[] chosen = scenario is null ? Scenarios : Scenarios.Contains(scenario) ? [scenario]
            : throw new ArgumentException($"'scenario' must be one of {string.Join(", ", Scenarios)}.");
        var results = new List<object>();
        var ok = true;
        foreach (var name in chosen)
        {
            var (passed, detail) = name switch
            {
                "sounds" => Sounds(),
                "rules" => Rules(),
                "make" => await MakeAsync(cancellation),
                "play" => await PlayAsync(cancellation),
                "cut" => await CutAsync(cancellation),
                _ => (false, (object)"unknown")
            };
            ok &= passed;
            results.Add(new { scenario = name, ok = passed, detail });
        }
        return new { ok, provenance = "FIXTURE, NOT AI: a fixture voice makes a quiet tone and a fixture speaker plays nothing", scenarios = results };
    }

    // The sounds each voice offers: Chatterbox's nine, Dia's without (mumbles), ElevenLabs' six, none for F5 or OpenAI.
    private static (bool, object) Sounds()
    {
        var engines = SpeechEngines.All.Concat(SpeechEngines.CloudVoices).Select(engine => new
        {
            engine = engine.Name, sounds = VoiceSounds.Supported(engine).Select(tag => $"{VoiceSounds.Entry(tag.Cue)} {tag.Text}").ToArray(),
            noSounds = VoiceSounds.NoSoundsReason(engine, true)
        }).ToArray();
        var turbo = VoiceSounds.Supported(SpeechEngines.Chatterbox);
        var dia = VoiceSounds.Supported(SpeechEngines.Dia);
        var passed = turbo.Count == 9 && turbo[0].Cue == "laugh" && VoiceSounds.Text(turbo[0]) == "[laugh]" &&
            dia.All(t => t.Cue != "mumble") && dia.Any(t => t.Cue == "gasp" && t.Text == "(gasps)") &&
            VoiceSounds.Supported(SpeechEngines.ElevenLabs).Count == 6 && VoiceSounds.Supported(SpeechEngines.F5).Count == 0 &&
            VoiceSounds.NoSoundsReason(null, true) is not null && VoiceSounds.NoSoundsReason(SpeechEngines.Chatterbox, true) is null &&
            VoiceSounds.CueOf("sound:clear throat") == "clear throat" && VoiceSounds.CueOf("expression:smile") is null;
        return (passed, engines);
    }

    // The rules, one condition at a time from a moment where the sound plays.
    private static (bool, object) Rules()
    {
        var plays = new VoiceSoundMoment(Voice: true, Aloud: true, Stopped: false, Makes: true, MartletSpeaking: false, UserTalking: false,
            Playing: false, Ready: true);
        var cases = new (string Name, VoiceSoundMoment Moment, VoiceSoundVerdict Expected)[]
        {
            ("plays", plays, VoiceSoundVerdict.Play),
            ("no voice", plays with { Voice = false }, VoiceSoundVerdict.Skip),
            ("speak aloud off", plays with { Aloud = false }, VoiceSoundVerdict.Skip),
            ("paused or muted", plays with { Stopped = true }, VoiceSoundVerdict.Skip),
            ("voice can't make it", plays with { Makes = false }, VoiceSoundVerdict.Skip),
            ("Martlet speaking", plays with { MartletSpeaking = true }, VoiceSoundVerdict.Skip),
            ("you talking", plays with { UserTalking = true }, VoiceSoundVerdict.Skip),
            ("another sound playing", plays with { Playing = true }, VoiceSoundVerdict.Skip),
            ("not made yet", plays with { Ready = false }, VoiceSoundVerdict.Make)
        };
        var rows = cases.Select(c => (c.Name, Decision: VoiceSoundGate.Decide(c.Moment), c.Expected)).ToArray();
        return (rows.All(r => r.Decision.Verdict == r.Expected),
            rows.Select(r => new { r.Name, verdict = r.Decision.Verdict.ToString(), why = r.Decision.Why, expected = r.Expected.ToString() }).ToArray());
    }

    // A clip is made with the engine's tag alone through the production synthesis path, trimmed and kept.
    private static async Task<(bool, object)> MakeAsync(CancellationToken cancellation)
    {
        var tag = VoiceSounds.Tag(SpeechEngines.Chatterbox, "laugh")!;
        var voice = new Voice();
        var (target, speech) = Fixture();
        await using var runtime = ConversationRuntime.Create(new SpokenReplyCheck.NoCredentials(), new PacedSpeakers(), hostSpeech: voice);
        var pcm = await runtime.SynthesizeAsync(speech, target, VoiceSounds.Text(tag), 1,
            new SpokenReplyCheck.Permissions(new Uri("https://127.0.0.1:9443"), target), cancellation);
        var clip = QuickSoundAudio.Prepare(pcm, VoiceSoundLibrary.MaximumLength);
        var folder = Path.Combine(Path.GetTempPath(), "martlet-voice-sounds-" + Guid.NewGuid().ToString("N"));
        try
        {
            var kept = VoiceSoundLibrary.Save(folder, "fixture", tag.Cue, clip);
            var back = VoiceSoundLibrary.Load(folder, "fixture", tag.Cue);
            var made = VoiceSoundLibrary.Made(folder, "fixture");
            var passed = voice.Texts.SequenceEqual(["[laugh]"]) && clip.Length > 0 && clip.Length < pcm.Length && kept &&
                back is not null && back.AsSpan().SequenceEqual(clip) && made is [("laugh", _)];
            return (passed, new
            {
                sent = voice.Texts, rawMs = Ms(pcm.Length), clipMs = Ms(clip.Length), kept, readBack = back is not null,
                file = VoiceSoundLibrary.FileName(tag.Cue)
            });
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // A clip plays whole on its own run, at the speakers' pace, with lip sync.
    private static async Task<(bool, object)> PlayAsync(CancellationToken cancellation)
    {
        var clip = Tone(600, 2000);
        var speakers = new PacedSpeakers();
        using var observer = new GeneratedSpeechObserver();
        observer.Enable();
        await using var runtime = ConversationRuntime.Create(new SpokenReplyCheck.NoCredentials(), speakers, generatedSpeech: observer);
        var watch = Stopwatch.StartNew();
        var playing = runtime.PlayClipAsync(clip, new OutputSelection(OutputPolicy.DefaultAtStart), cancellation);
        var busy = runtime.ClipPlaying;
        var second = await runtime.PlayClipAsync(clip, new OutputSelection(OutputPolicy.DefaultAtStart), cancellation);
        var played = await playing;
        var took = watch.Elapsed.TotalMilliseconds;
        observer.Segments.TryRead(out var observation);
        var runs = speakers.Runs;
        var passed = played && busy && !second && !runtime.ClipPlaying && runs.Length == 1 && runs[0].Samples == clip.Length / 2 &&
            took >= 500 && observation is not null && observation.SampleCount == clip.Length / 2;
        return (passed, new
        {
            played, secondWhilePlaying = second, tookMs = Math.Round(took), runs = runs.Select(r => new { samples = r.Samples }).ToArray(),
            lipSync = observation is null ? null : new { samples = observation.SampleCount, ended = observation.Failure.ToString() }
        });
    }

    // A reply's own voice cuts a sound still playing the moment it starts: the reply never waits for it. The same reply without a
    // sound (after one to warm up) gives the first-audio time to compare with.
    private static async Task<(bool, object)> CutAsync(CancellationToken cancellation)
    {
        _ = await ReplyAsync(null, cancellation);
        var alone = await ReplyAsync(null, cancellation);
        var clip = Tone(2_000, 2000);
        var cut = await ReplyAsync(clip, cancellation);
        var runs = cut.Runs;
        // The sound's run closed (cut short) no later than a moment after the reply's own run opened, and the reply's first audio
        // came no later than without the sound.
        var passed = cut.Played == false && runs.Length == 2 && runs[0].Samples < clip.Length / 2 && runs[1].OpenedMs >= runs[0].OpenedMs &&
            runs[0].ClosedMs <= runs[1].OpenedMs + 250 && cut.State == ConversationState.Completed && runs[1].Samples > 0 &&
            alone.FirstAudioMs is { } before && cut.FirstAudioMs is { } after && after <= before + 150;
        return (passed, new
        {
            soundPlayedWhole = cut.Played, replyState = cut.State.ToString(), replyFirstAudioMs = cut.FirstAudioMs,
            sameReplyWithoutSoundFirstAudioMs = alone.FirstAudioMs,
            runs = runs.Select(r => new { samples = r.Samples, openedMs = Math.Round(r.OpenedMs), closedMs = Math.Round(r.ClosedMs) }).ToArray(),
            soundSamples = clip.Length / 2
        });
    }

    private sealed record Reply(bool? Played, ConversationState State, double? FirstAudioMs, (long Samples, double OpenedMs, double ClosedMs)[] Runs);

    // One spoken reply through the production runtime (a fixture endpoint answers after 300 ms), with a sound playing from just
    // before it starts when there is one.
    private static async Task<Reply> ReplyAsync(byte[]? clip, CancellationToken cancellation)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var baseUrl = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1";
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var serving = QuickSoundCheck.ServeAsync(listener, TimeSpan.FromMilliseconds(300), false, stop.Token);
        try
        {
            var speakers = new PacedSpeakers();
            var (target, speech) = Fixture();
            var request = new ConversationRequest(new BoundedTextInput("Say hi.", "Fixture check."),
                new TextModelSelection(ChatCompletionsSetup.Alias, "fixture-model"), new TextGenerationLimits(),
                new ConversationLimits { MaxSpeechSegments = 8, MaxSpeechTextBytes = 12_288, MaxReservedSpeechSamples = 1_920_000 },
                speech, new ChatCompletionsTarget(baseUrl, Keyless: true), hostSpeech: target);
            await using var runtime = ConversationRuntime.Create(new SpokenReplyCheck.NoCredentials(), speakers, hostSpeech: new Voice());
            var playing = clip is null ? null : runtime.PlayClipAsync(clip, new OutputSelection(OutputPolicy.DefaultAtStart), cancellation);
            await Task.Delay(50, cancellation);
            var turn = runtime.Start(request, new SpokenReplyCheck.Permissions(ChatCompletionsSetup.BaseUri(baseUrl), target), cancellation);
            var terminal = await turn.Completion.WaitAsync(TimeSpan.FromSeconds(30), cancellation);
            await turn.OwnershipRelease.WaitAsync(TimeSpan.FromSeconds(10), cancellation);
            bool? played = playing is null ? null : await playing.WaitAsync(TimeSpan.FromSeconds(10), cancellation);
            return new(played, terminal.State, terminal.FirstAudioAfter is { } first ? Math.Round(first.TotalMilliseconds) : null, speakers.Runs);
        }
        finally
        {
            await stop.CancelAsync();
            listener.Stop();
            try { await serving; }
            catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException or IOException) { }
        }
    }

    private static (HostSpeechTarget Target, SpeechOutput Speech) Fixture()
    {
        var preset = Guid.NewGuid();
        var target = new HostSpeechTarget("https://127.0.0.1:9443", "fixture-host", "sha256:" + new string('0', 64),
            "desktop-fixture", Guid.NewGuid(), VoiceModel, preset, new string('0', 64));
        var speech = new SpeechOutput(new SpeechSynthesisSelection(SelfHostSetup.GatewayF5Alias, VoiceModel, preset.ToString("N"),
            SpeechOutputFormat.Pcm24KhzMono16Le), new OutputSelection(OutputPolicy.DefaultAtStart), new SpeechSynthesisLimits
            {
                MaxAudioBytes = 480_000, MaxAudioDuration = TimeSpan.FromSeconds(10), MaxRequestTime = TimeSpan.FromSeconds(20)
            });
        return (target, speech);
    }

    private static double Ms(int bytes) => Math.Round(bytes / 48.0);

    // FIXTURE, NOT AI: a quiet square wave, 24 kHz mono 16-bit.
    private static byte[] Tone(int milliseconds, short level)
    {
        var pcm = new byte[24_000 * milliseconds / 1000 * 2];
        for (var i = 0; i < pcm.Length / 2; i++) BitConverter.TryWriteBytes(pcm.AsSpan(i * 2), (short)(i / 40 % 2 == 0 ? level : -level));
        return pcm;
    }

    // FIXTURE, NOT AI: each piece is 200 ms of silence, a quarter second of a quiet tone and 200 ms of silence, after 50 ms. It
    // keeps the text it was sent.
    private sealed class Voice : IHostSpeechClient
    {
        private readonly List<string> texts = [];
        internal string[] Texts { get { lock (texts) return [.. texts]; } }

        public async IAsyncEnumerable<byte[]> StreamAsync(HostSpeechTarget target, BoundedSpeechInput input, CorrelationIds ids,
            long epoch, DateTimeOffset deadline, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            lock (texts) texts.Add(input.Text);
            await Task.Delay(50, cancellationToken);
            var pcm = new byte[(4_800 + 6_000 + 4_800) * 2];
            for (var i = 4_800; i < 10_800; i++)
                BitConverter.TryWriteBytes(pcm.AsSpan(i * 2), (short)(Math.Sin(2 * Math.PI * 220 * i / 24_000.0) * 3000));
            yield return pcm;
        }
    }

    // Opens no device and plays nothing, but takes audio at the pace a real speaker plays it (a 100 ms buffer that drains in real
    // time once started): each run's samples and when it opened and closed.
    private sealed class PacedSpeakers : IPlaybackDeviceFactory
    {
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly List<Speaker> opened = [];
        internal (long Samples, double OpenedMs, double ClosedMs)[] Runs
        {
            get { lock (opened) return [.. opened.Select(s => (s.Samples, s.OpenedMs, s.ClosedMs))]; }
        }

        public IPlaybackDevice Open(OutputSelection selection, PcmFormat format, CancellationToken cancellationToken)
        {
            var speaker = new Speaker(this, format) { OpenedMs = clock.Elapsed.TotalMilliseconds };
            lock (opened) opened.Add(speaker);
            return speaker;
        }

        private sealed class Speaker(PacedSpeakers owner, PcmFormat format) : IPlaybackDevice
        {
            private readonly object gate = new();
            private double queued, last;
            private bool started;
            private long samples;
            internal long Samples { get { lock (gate) return samples; } }
            internal double OpenedMs { get; init; }
            internal double ClosedMs { get; private set; } = double.MaxValue;
            private int Buffer => format.SampleRate / 10;
            public PlaybackDeviceInfo Info => new(format.SampleRate, format.Channels, 16, DeviceSampleEncoding.IntegerPcm, Buffer, false);

            private void Drain()
            {
                var now = owner.clock.Elapsed.TotalSeconds;
                if (started) queued = Math.Max(0, queued - (now - last) * format.SampleRate);
                last = now;
            }

            public int GetPadding(CancellationToken cancellationToken)
            {
                lock (gate)
                {
                    Drain();
                    return (int)Math.Ceiling(queued);
                }
            }

            public int Write(ReadOnlySpan<byte> pcm, CancellationToken cancellationToken)
            {
                lock (gate)
                {
                    Drain();
                    var room = Math.Max(0, Buffer - (int)Math.Ceiling(queued));
                    var written = Math.Min(room, pcm.Length / format.BlockAlignment);
                    queued += written;
                    samples += written;
                    return written;
                }
            }

            public void Start(CancellationToken cancellationToken)
            {
                lock (gate)
                {
                    Drain();
                    started = true;
                }
            }

            public void StopAndReset()
            {
                lock (gate) queued = 0;
            }

            public void Dispose() => ClosedMs = owner.clock.Elapsed.TotalMilliseconds;
        }
    }
}
