using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Martlet.Audio;
using Martlet.Conversation;
using Martlet.Core.Audio;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Diagnostics;
using Martlet.Providers;

namespace Martlet.Mcp;

/// <summary>Companion › Voice › Quick sounds while Martlet thinks. quick_sounds_status reads the choice (talk-preferences.json),
/// the clips kept per voice and character (quick-sounds\), whether the current voice has them and the newest log lines.
/// quick_sounds_check rehearses the production rules (QuickSoundGate, QuickSoundWatcher) on fixture turns through the production
/// conversation runtime (ConversationRuntime, the Chat Completions adapter, the host voice stream and the playback sink): a
/// fixture Chat Completions endpoint on 127.0.0.1 answers after a set wait (canned words, NOT AI), a fixture host voice makes a
/// quiet tone (NOT AI) and a fixture speaker plays nothing. It reports when the quick sound played and why or why not.</summary>
internal static class QuickSoundCheck
{
    internal static readonly string[] Scenarios = ["slow", "fast", "cooldown", "early", "let-go", "reasoning", "paused"];
    private const string Model = "fixture-model";
    private const string VoiceModel = "chatterbox-turbo";
    // How far past its due time a quick sound may play here: the watcher looks every 25 ms and the fixture runs in real time.
    private const double Slack = 300;

    internal static object Status(string dataDirectory)
    {
        var (on, delay, source) = Setting(dataDirectory);
        var loaded = new SettingsStore(dataDirectory).LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
        var tts = loaded.Settings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Tts && r.Enabled == true);
        var voice = QuickSoundLibrary.Voice(tts);
        var key = voice is { } v ? QuickSoundLibrary.Key(v.Identity, QuickSoundLibrary.Character(loaded.Settings?.Companion?.ActivePersona?.Id)) : null;
        var sets = QuickSoundLibrary.All(dataDirectory);
        var current = key is null ? null : sets.FirstOrDefault(s => s.Key == key);
        var logs = LocalLogs.Directory(dataDirectory);
        var lines = Directory.Exists(logs)
            ? LocalLogs.Read(logs, LocalLogs.ThisDeviceId())
                .Where(r => r.Component == "desktop" && (r.Message.StartsWith("Quick sound", StringComparison.Ordinal) ||
                    r.Message.Contains("Quick sound at", StringComparison.Ordinal)))
                .OrderBy(r => r.At).TakeLast(10).Select(r => new { at = r.At, message = r.Message }).ToArray()
            : [];
        return new
        {
            on, delayMs = delay, source,
            cooldownSeconds = QuickSoundOptions.DefaultCooldown.TotalSeconds,
            voice = voice is { } chosen ? new { words = chosen.Words, paid = chosen.Paid, key } : null,
            // A paid cloud voice makes them only on the owner's click (Companion › Voice › Make quick sounds now).
            ready = current is not null,
            needsClick = on && voice is { Paid: true } && current is null,
            clips = current?.Clips.Select(c => new { text = c.Text, milliseconds = Math.Round(c.Duration.TotalMilliseconds) }).ToArray(),
            kept = sets.Select(s => new { key = s.Key, voice = s.Voice, madeAt = s.MadeAt, clips = s.Clips.Count }).ToArray(),
            log = lines
        };
    }

    private static (bool On, int DelayMs, string Source) Setting(string directory)
    {
        var path = Path.Combine(directory, "talk-preferences.json");
        var fallback = (int)QuickSoundOptions.DefaultDelay.TotalMilliseconds;
        if (!File.Exists(path)) return (false, fallback, "default (no talk-preferences.json)");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var on = document.RootElement.TryGetProperty("QuickSounds", out var value) && value.ValueKind is JsonValueKind.True;
            var delay = document.RootElement.TryGetProperty("QuickSoundDelayMs", out var ms) && ms.TryGetInt32(out var chosen) &&
                QuickSoundOptions.DelayChoices.Contains(chosen) ? chosen : fallback;
            return (on, delay, document.RootElement.TryGetProperty("QuickSounds", out _) ? "talk-preferences.json" : "default");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return (false, fallback, "default (talk-preferences.json unreadable)");
        }
    }

    internal static async Task<object> RunAsync(string? scenario, int? delayMs, CancellationToken cancellation)
    {
        string[] chosen = scenario is null ? Scenarios : Scenarios.Contains(scenario) ? [scenario]
            : throw new ArgumentException($"'scenario' must be one of {string.Join(", ", Scenarios)}.");
        if (delayMs is { } asked && !QuickSoundOptions.DelayChoices.Contains(asked))
            throw new ArgumentException($"'delayMs' must be one of {string.Join(", ", QuickSoundOptions.DelayChoices)}.");
        var options = QuickSoundOptions.Of(true, delayMs ?? (int)QuickSoundOptions.DefaultDelay.TotalMilliseconds);
        var delay = options.Delay.TotalMilliseconds;
        // FIXTURE, NOT AI: three quick sounds of a quiet tone, 250 ms each, so the reply's own audio can be told from them.
        IReadOnlyList<QuickSoundClip> clips = [new("Mm,", Tone(250, 1500)), new("Hmm...", Tone(250, 1600)), new("Oh,", Tone(250, 1700))];
        var results = new List<object>();
        var ok = true;
        foreach (var name in chosen)
        {
            var gate = new QuickSoundGate(TimeProvider.System) { Options = options };
            var runs = new List<Turn>();
            switch (name)
            {
                case "slow": runs.Add(await TurnAsync(gate, clips, firstWordsMs: 1_800, cancellation: cancellation)); break;
                case "fast": runs.Add(await TurnAsync(gate, clips, firstWordsMs: 40, cancellation: cancellation)); break;
                case "cooldown":
                    runs.Add(await TurnAsync(gate, clips, firstWordsMs: 1_800, cancellation: cancellation));
                    runs.Add(await TurnAsync(gate, clips, firstWordsMs: 1_800, cancellation: cancellation));
                    break;
                case "early": runs.Add(await TurnAsync(gate, clips, firstWordsMs: 3_000, holdMs: 1_000, cancellation: cancellation)); break;
                case "let-go": runs.Add(await TurnAsync(gate, clips, firstWordsMs: 3_000, holdMs: 1_000, letGo: true, cancellation: cancellation)); break;
                case "reasoning": runs.Add(await TurnAsync(gate, clips, firstWordsMs: 1_800, reasoning: true, cancellation: cancellation)); break;
                case "paused": runs.Add(await TurnAsync(gate, clips, firstWordsMs: 1_800, pauseAtMs: 200, cancellation: cancellation)); break;
            }
            var first = runs[0];
            var passed = name switch
            {
                // Played once its first audio was late, the whole clip before the reply, which then played on its own run.
                "slow" => first.Played && first.AfterConfirmedMs is { } a && a >= delay && a <= delay + Slack && first.ReplyFollowed &&
                    first.Line?.Contains("Quick sound at", StringComparison.Ordinal) == true,
                "fast" => !first.Played && first.Why == "its own first audio was ready in time" && first.Runs == 1,
                // At most one every 20 s, and the next clip comes next time.
                "cooldown" => first.Played && runs[1] is { Played: false } second && second.Why.Contains("played", StringComparison.Ordinal) &&
                    second.Runs == 1,
                // Counted from the moment the reply started early was taken, never while it was held.
                "early" => first.Played && first.QuickSoundAtMs >= first.ReleasedAtMs + delay - 30 &&
                    first.AfterConfirmedMs is { } e && e <= delay + Slack && first.ReplyFollowed,
                "let-go" => !first.Played && first.Runs == 0,
                // A model that thinks first: sooner, after the reasoning delay.
                "reasoning" => first.Played && first.AfterConfirmedMs is { } r && r >= QuickSoundOptions.DefaultReasoningDelay.TotalMilliseconds &&
                    r <= QuickSoundOptions.DefaultReasoningDelay.TotalMilliseconds + Slack,
                "paused" => !first.Played && first.Why.Contains("paused", StringComparison.Ordinal),
                _ => false
            };
            ok &= passed;
            results.Add(new { scenario = name, ok = passed, turns = runs.Select(Report).ToArray() });
        }
        return new
        {
            ok, delayMs = delay, reasoningDelayMs = QuickSoundOptions.DefaultReasoningDelay.TotalMilliseconds,
            cooldownSeconds = options.Cooldown.TotalSeconds, provenance = "FIXTURE, NOT AI: canned words and a quiet tone, nothing played",
            scenarios = results
        };
    }

    private static object Report(Turn turn) => new
    {
        played = turn.Played, why = turn.Why, clip = turn.Clip, quickSoundAtMs = turn.QuickSoundAtMs, afterConfirmedMs = turn.AfterConfirmedMs,
        releasedAtMs = turn.ReleasedAtMs, firstAudioAtMs = turn.FirstAudioAtMs, playbackRuns = turn.Runs, replyFollowedUncut = turn.ReplyFollowed,
        state = turn.State, latencyLine = turn.Line
    };

    private sealed record Turn(bool Played, string Why, string? Clip, double? QuickSoundAtMs, double? AfterConfirmedMs, double? ReleasedAtMs,
        double? FirstAudioAtMs, int Runs, bool ReplyFollowed, string State, string? Line);

    private static async Task<Turn> TurnAsync(QuickSoundGate gate, IReadOnlyList<QuickSoundClip> clips, int firstWordsMs,
        int? holdMs = null, bool letGo = false, bool reasoning = false, int? pauseAtMs = null, CancellationToken cancellation = default)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var baseUrl = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1";
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var serving = ServeAsync(listener, TimeSpan.FromMilliseconds(firstWordsMs), reasoning, stop.Token);
        try
        {
            var speakers = new Speakers();
            var preset = Guid.NewGuid();
            var target = new HostSpeechTarget("https://127.0.0.1:9443", "fixture-host", "sha256:" + new string('0', 64),
                "desktop-fixture", Guid.NewGuid(), VoiceModel, preset, new string('0', 64));
            var speech = new SpeechOutput(new SpeechSynthesisSelection(SelfHostSetup.GatewayF5Alias, VoiceModel, preset.ToString("N"),
                SpeechOutputFormat.Pcm24KhzMono16Le), new OutputSelection(OutputPolicy.DefaultAtStart), new SpeechSynthesisLimits
                {
                    MaxAudioBytes = 480_000, MaxAudioDuration = TimeSpan.FromSeconds(10), MaxRequestTime = TimeSpan.FromSeconds(20)
                });
            var request = new ConversationRequest(new BoundedTextInput("Say hi.", "Fixture check."),
                new TextModelSelection(ChatCompletionsSetup.Alias, Model), new TextGenerationLimits(),
                new ConversationLimits { MaxSpeechSegments = 8, MaxSpeechTextBytes = 12_288, MaxReservedSpeechSamples = 1_920_000 },
                speech, new ChatCompletionsTarget(baseUrl, Keyless: true), hostSpeech: target);
            await using var runtime = ConversationRuntime.Create(new SpokenReplyCheck.NoCredentials(), speakers, hostSpeech: new Voice());
            var permissions = new SpokenReplyCheck.Permissions(ChatCompletionsSetup.BaseUri(baseUrl), target);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var startedAt = TimeProvider.System.GetTimestamp();
            var turn = holdMs is null ? runtime.Start(request, permissions, cancellation)
                : runtime.StartEarly(request, permissions, prepareVoice: true, cancellation);
            var watching = QuickSoundWatcher.WatchAsync(turn, gate, clips, () => !turn.Held, reasoning: false, TimeProvider.System, cancellation);
            double? released = null;
            if (holdMs is { } hold)
            {
                await Task.Delay(hold, cancellation);
                if (letGo) _ = turn.StopAsync();
                else if (turn.Release()) released = clock.Elapsed.TotalMilliseconds;
            }
            if (pauseAtMs is { } pause)
            {
                await Task.Delay(pause, cancellation);
                turn.Pause();
                await Task.Delay(1_500, cancellation);
                turn.Resume();
            }
            var terminal = await turn.Completion.WaitAsync(TimeSpan.FromSeconds(30), cancellation);
            await turn.OwnershipRelease.WaitAsync(TimeSpan.FromSeconds(10), cancellation);
            var outcome = await watching.WaitAsync(TimeSpan.FromSeconds(10), cancellation);
            var timings = terminal.Timings;
            var line = ReplyLatency.Describe(null, startedAt, TimeProvider.System, terminal, $"Thinking {Model}, voice {VoiceModel}");
            var runs = speakers.Runs;
            // The clip's run played it whole and closed before the reply's own run opened.
            var followed = runs.Length == 2 && clips.Any(c => c.Pcm.Length / 2 == runs[0].Samples) && runs[1].OpenedMs >= runs[0].ClosedMs;
            return new(outcome.Played, outcome.Why, outcome.Clip, timings?.QuickSoundAfter?.TotalMilliseconds,
                outcome.AfterConfirmed?.TotalMilliseconds, released, terminal.FirstAudioAfter?.TotalMilliseconds, runs.Length,
                outcome.Played && followed, terminal.State.ToString(), line);
        }
        finally
        {
            await stop.CancelAsync();
            listener.Stop();
            try { await serving; }
            catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException or IOException) { }
        }
    }

    // FIXTURE, NOT AI: a quiet square wave, 24 kHz mono 16-bit.
    private static byte[] Tone(int milliseconds, short level)
    {
        var pcm = new byte[24_000 * milliseconds / 1000 * 2];
        for (var i = 0; i < pcm.Length / 2; i++) BitConverter.TryWriteBytes(pcm.AsSpan(i * 2), (short)(i / 40 % 2 == 0 ? level : -level));
        return pcm;
    }

    // Answers each request after the set wait with one short sentence (a hidden reasoning delta first with reasoning), the way
    // a cloud model streams.
    internal static async Task ServeAsync(TcpListener listener, TimeSpan firstWords, bool reasoning, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellation);
            await using var stream = client.GetStream();
            await HearingCheck.ReadRequestAsync(stream, cancellation);
            await WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n",
                cancellation);
            const string chunk = "{\"id\":\"fixture\",\"object\":\"chat.completion.chunk\",\"model\":\"fixture\",\"choices\":[{\"index\":0,";
            if (reasoning)
                await WriteAsync(stream, "data: " + chunk + "\"delta\":{\"role\":\"assistant\",\"content\":\"\",\"reasoning\":\"Thinking it over.\"}," +
                    "\"finish_reason\":null}]}\n\n", cancellation);
            await Task.Delay(firstWords, cancellation);
            await WriteAsync(stream, "data: " + chunk + "\"delta\":{" + (reasoning ? "" : "\"role\":\"assistant\",") +
                "\"content\":\"Here's the thing, it works.\"},\"finish_reason\":null}]}\n\n", cancellation);
            await WriteAsync(stream, "data: " + chunk + "\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n", cancellation);
        }
    }

    private static async Task WriteAsync(NetworkStream stream, string text, CancellationToken cancellation)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text), cancellation);
        await stream.FlushAsync(cancellation);
    }

    // FIXTURE, NOT AI: each piece is a quarter second of a quiet 220 Hz tone, after 50 ms.
    private sealed class Voice : IHostSpeechClient
    {
        public async IAsyncEnumerable<byte[]> StreamAsync(HostSpeechTarget target, BoundedSpeechInput input, CorrelationIds ids,
            long epoch, DateTimeOffset deadline, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(50, cancellationToken);
            var pcm = new byte[6_000 * 2];
            for (var i = 0; i < pcm.Length / 2; i++)
                BitConverter.TryWriteBytes(pcm.AsSpan(i * 2), (short)(Math.Sin(2 * Math.PI * 220 * i / 24_000.0) * 3000));
            yield return pcm;
        }
    }

    // Opens no device and plays nothing: each playback run's samples and when it opened and closed.
    private sealed class Speakers : IPlaybackDeviceFactory
    {
        private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        private readonly List<Speaker> opened = [];
        internal (long Samples, double OpenedMs, double ClosedMs)[] Runs
        {
            get { lock (opened) return [.. opened.Select(s => (Interlocked.Read(ref s.Samples), s.OpenedMs, s.ClosedMs))]; }
        }

        public IPlaybackDevice Open(OutputSelection selection, PcmFormat format, CancellationToken cancellationToken)
        {
            var speaker = new Speaker(this, format) { OpenedMs = clock.Elapsed.TotalMilliseconds };
            lock (opened) opened.Add(speaker);
            return speaker;
        }

        private sealed class Speaker(Speakers owner, PcmFormat format) : IPlaybackDevice
        {
            internal long Samples;
            internal double OpenedMs { get; init; }
            internal double ClosedMs { get; private set; } = double.MaxValue;
            public PlaybackDeviceInfo Info => new(format.SampleRate, format.Channels, 16, DeviceSampleEncoding.IntegerPcm,
                format.SampleRate / 20, false);
            public int GetPadding(CancellationToken cancellationToken) => 0;
            public int Write(ReadOnlySpan<byte> pcm, CancellationToken cancellationToken)
            {
                var written = pcm.Length / format.BlockAlignment;
                Interlocked.Add(ref Samples, written);
                return written;
            }
            public void Start(CancellationToken cancellationToken) { }
            public void StopAndReset() { }
            public void Dispose() => ClosedMs = owner.clock.Elapsed.TotalMilliseconds;
        }
    }
}
