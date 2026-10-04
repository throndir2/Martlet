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
using Martlet.Providers;

namespace Martlet.Mcp;

/// <summary>spoken_reply_check: a spoken reply whose voice fails partway, rehearsed end to end with the production conversation
/// runtime (ConversationRuntime, the Chat Completions adapter, the host voice stream and the playback sink). A fixture Chat
/// Completions endpoint on 127.0.0.1 streams a canned reply (NOT AI) one sentence at a time, like OpenRouter; a fixture Martlet
/// host voice (a quiet tone, NOT AI) fails on the failAt-th piece it is asked to say the way a paired host's worker does
/// (server: worker.failed, unavailable: worker.unavailable, stall: no audio until the voice's time runs out); slow makes every
/// piece slower than real time instead, half its audio, then a pause longer than that audio and the old 1 s underrun limit,
/// then the rest, as Chatterbox streams on a busy graphics card; a fixture speaker opens no device and plays nothing. The
/// reply's whole text must still arrive and the turn complete; only the voice stops (with slow, nothing stops: every piece is
/// spoken whole, and the reply latency line says how often and how long the voice paused).</summary>
internal static class SpokenReplyCheck
{
    internal static readonly string[] Failures = ["server", "unavailable", "stall", "slow", "none"];
    // How long a slow voice keeps the speakers waiting in the middle of each piece.
    internal static readonly TimeSpan SlowGap = TimeSpan.FromMilliseconds(1_500);
    private const string Model = "fixture-model";
    private const string VoiceModel = "chatterbox-turbo";
    private static readonly string[] Sentences =
    [
        "Hey, you made it back. ", "I was just thinking about you. ", "Want to tell me how your day went? ",
        "I'll be right here, listening."
    ];
    private static readonly TimeSpan SentenceGap = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan WordGap = TimeSpan.FromMilliseconds(25);

    /// <summary>With <paramref name="reply"/>, that text is streamed a word at a time instead of the four canned sentences.
    /// <paramref name="breaks"/> are the persona's speech breaks the reply is spoken with, the defaults when null, as the desktop
    /// always passes them (<paramref name="persona"/> names the saved persona they came from, if any).</summary>
    internal static async Task<object> RunAsync(string? voiceFailure, int? failAt, CancellationToken cancellation,
        int? reasoningMs = null, int? voiceDelayMs = null, string? reply = null, SpeechBreaks? breaks = null, string? persona = null,
        string? thinkingSteps = null, bool refuseThinking = false)
    {
        if (thinkingSteps is not (null or "off" or "on")) throw new ArgumentException("'thinkingSteps' must be off or on.");
        if (refuseThinking && thinkingSteps is null) throw new ArgumentException("'refuseThinking' needs thinkingSteps.");
        var failure = voiceFailure ?? "server";
        if (!Failures.Contains(failure)) throw new ArgumentException($"'voiceFailure' must be one of {string.Join(", ", Failures)}.");
        var at = failAt ?? 1;
        if (at is < 1 or > 4) throw new ArgumentException("'failAt' must be 1 through 4.");
        var reasoning = TimeSpan.FromMilliseconds(reasoningMs ?? 0);
        var voiceDelay = TimeSpan.FromMilliseconds(voiceDelayMs ?? 0);
        if (reasoning < TimeSpan.Zero || reasoning > TimeSpan.FromSeconds(5)) throw new ArgumentException("'reasoningMs' must be 0 through 5000.");
        if (voiceDelay < TimeSpan.Zero || voiceDelay > TimeSpan.FromSeconds(5)) throw new ArgumentException("'voiceDelayMs' must be 0 through 5000.");
        if (reply is not null && (string.IsNullOrWhiteSpace(reply) || reply.Length > 1024 || reply.Any(char.IsControl)))
            throw new ArgumentException("'reply' must be 1-1024 characters of one-line text.");
        string[] chunks = reply is null ? Sentences : [.. System.Text.RegularExpressions.Regex.Matches(reply, @"\s*\S+").Select(m => m.Value)];
        var gap = reply is null ? SentenceGap : WordGap;
        breaks ??= SpeechBreaks.Default;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var baseUrl = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1";
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        // Whether each request the fixture endpoint got carried the Thinking steps control (a loopback server gets the chat
        // template's enable_thinking), in order; with refuseThinking it refuses those, as a model that always thinks does.
        var asked = new List<bool>();
        var serving = ServeAsync(listener, chunks, gap, reasoning, stop.Token, asked, refuseThinking);
        try
        {
            var voice = new Voice(failure, at, voiceDelay);
            var speakers = new Speakers();
            var preset = Guid.NewGuid();
            var target = new HostSpeechTarget("https://127.0.0.1:9443", "fixture-host", "sha256:" + new string('0', 64),
                "desktop-fixture", Guid.NewGuid(), VoiceModel, preset, new string('0', 64));
            // The desktop's own voice bounds (10 s per piece, 20 s each), shortened for a stalled voice so the check is quick.
            var limits = new SpeechSynthesisLimits
            {
                MaxAudioBytes = 480_000, MaxAudioDuration = TimeSpan.FromSeconds(10),
                FirstAudioTimeout = TimeSpan.FromSeconds(failure == "stall" ? 3 : 20),
                MaxRequestTime = TimeSpan.FromSeconds(failure == "stall" ? 4 : 20)
            };
            var speech = new SpeechOutput(new SpeechSynthesisSelection(SelfHostSetup.GatewayF5Alias, VoiceModel, preset.ToString("N"),
                SpeechOutputFormat.Pcm24KhzMono16Le), new OutputSelection(OutputPolicy.DefaultAtStart), limits);
            var request = new ConversationRequest(new BoundedTextInput("Say hi.", "Fixture check."),
                new TextModelSelection(ChatCompletionsSetup.Alias, Model), new TextGenerationLimits(),
                new ConversationLimits { MaxSpeechSegments = 8, MaxSpeechTextBytes = 12_288, MaxReservedSpeechSamples = 1_920_000 },
                speech, new ChatCompletionsTarget(baseUrl, Keyless: true), hostSpeech: target, speechBreaks: breaks,
                generation: thinkingSteps is null ? null : new GenerationSettings { Reasoning = thinkingSteps == "on" });
            // What the speech bubble and subtitles are given: each line as its playback starts, or, once the voice failed, each
            // sentence it couldn't say, shown one after another for its reading time.
            var captions = new SpokenTextFeed();
            var shown = new List<(string Text, long AtMs, bool Spoken)>();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var reading = ReadCaptionsAsync(captions, shown, clock, voice, stop.Token);
            await using var runtime = ConversationRuntime.Create(new NoCredentials(), speakers, hostSpeech: voice, spokenText: captions);
            // As the desktop counts a typed message: from sending it, through building the request, to the first audio.
            var timeline = new ReplyTimeline(TimeProvider.System, ReplyTimeline.YouSent);
            var startedAt = TimeProvider.System.GetTimestamp();
            timeline.Mark("building the request", startedAt);
            var turn = runtime.Start(request, new Permissions(ChatCompletionsSetup.BaseUri(baseUrl), target), cancellation);
            var terminal = await turn.Completion.WaitAsync(TimeSpan.FromSeconds(60), cancellation);
            await turn.OwnershipRelease.WaitAsync(TimeSpan.FromSeconds(10), cancellation);
            var latencyLine = ReplyLatency.Describe(timeline, startedAt, TimeProvider.System, terminal,
                $"Thinking {Model}, voice {VoiceModel}");
            var latency = latencyLine is null ? null : LatencyReport.Parse(DateTimeOffset.Now, latencyLine);
            var everyPiece = failure is "none" or "slow";
            string[] expectedSteps = everyPiece
                ? [ReplyLatency.ThinkingAuthorization, ReplyLatency.ThinkingConnection,
                    reasoning > TimeSpan.Zero ? ReplyLatency.HiddenReasoning : ReplyLatency.ThinkingFirstWords, ReplyLatency.FirstSentence,
                    ReplyLatency.VoiceAuthorization, ReplyLatency.VoiceSynthesis, ReplyLatency.PlaybackStart, ReplyLatency.Speakers]
                : [];
            var missingSteps = expectedSteps.Where(step => latency?.Steps.ContainsKey(step) != true).ToArray();
            var stepsSum = latency?.Steps.Values.Sum() ?? 0;
            // Each step is rounded to a millisecond on its own, so they add up to the total within a millisecond a step.
            bool[] sentControl;
            lock (asked) sentControl = [.. asked];
            // Off or On reaches the endpoint; a refused one is asked once more without it, and the reply still completes.
            var thinkingOk = thinkingSteps is null ? sentControl.All(control => !control) && !terminal.ReasoningRejected
                : refuseThinking ? terminal.ReasoningRejected && sentControl is [true, false]
                : !terminal.ReasoningRejected && sentControl is [true];
            var latencyOk = !everyPiece || latency is { TotalMs: { } total } && missingSteps.Length == 0 &&
                Math.Abs(stepsSum - total) <= latency.Steps.Count + 1 &&
                (reasoning == TimeSpan.Zero || latency.Steps.GetValueOrDefault(ReplyLatency.HiddenReasoning) >= reasoning.TotalMilliseconds * 0.8) &&
                latency.Steps.GetValueOrDefault(ReplyLatency.VoiceSynthesis) >= voiceDelay.TotalMilliseconds * 0.8 &&
                // A slow voice's pauses are said in the line: at least one per piece, each about as long as its gap.
                (failure != "slow" || latency.VoicePauses >= voice.Calls &&
                    latency.VoicePausedMs >= voice.Calls * SlowGap.TotalMilliseconds * 0.8);
            var text = turn.Content.Text;
            var served = string.Concat(chunks);
            var full = text == served;
            // Captions of unsaid sentences keep coming after the reply ends, one per reading time.
            bool Covered()
            {
                lock (shown) return Words(string.Join(" ", shown.Select(line => line.Text))) == Words(served);
            }
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (!Covered() && waited.Elapsed < TimeSpan.FromSeconds(25)) await Task.Delay(100, cancellation);
            var captionsComplete = Covered();
            var expected = failure switch
            {
                "server" => ProviderFailureCode.Server,
                "unavailable" => ProviderFailureCode.ModelNotFound,
                _ => (ProviderFailureCode?)null
            };
            var voiceOk = everyPiece
                ? !terminal.SpeechFailed && voice.Spoken == voice.Calls && voice.Calls > 0
                : terminal.SpeechFailed && voice.Spoken == at - 1 && voice.Calls >= at &&
                  (expected is null || terminal.ProviderFailure == expected && terminal.FailedProvider == ProviderRole.Tts);
            (string Text, long AtMs, bool Spoken)[] lines;
            lock (shown) lines = [.. shown];
            return new
            {
                ok = terminal.State == ConversationState.Completed && terminal.TextComplete && full && voiceOk && captionsComplete && latencyOk &&
                    thinkingOk,
                voiceFailure = failure,
                failAt = everyPiece ? (int?)null : at,
                endpoint = baseUrl,
                thinking = new
                {
                    ok = thinkingOk,
                    steps = thinkingSteps,
                    refused = refuseThinking,
                    requests = sentControl.Length,
                    sentControl,
                    reasoningRejected = terminal.ReasoningRejected
                },
                latency = new
                {
                    ok = latencyOk,
                    line = latencyLine,
                    totalMs = latency?.TotalMs,
                    measured = latency?.Measured,
                    steps = latency?.Steps,
                    stepsSumMs = stepsSum,
                    missingSteps,
                    firstWordsMs = terminal.FirstTextAfter?.TotalMilliseconds,
                    firstAudioMs = terminal.FirstAudioAfter?.TotalMilliseconds,
                    timings = terminal.Timings
                },
                reply = new
                {
                    state = terminal.State.ToString(),
                    failure = terminal.Failure.ToString(),
                    textComplete = terminal.TextComplete,
                    fullText = full,
                    characters = text.Length,
                    servedCharacters = served.Length,
                    text
                },
                voice = new
                {
                    stopped = terminal.SpeechFailed,
                    why = terminal.SpeechFailure.ToString(),
                    provider = terminal.ProviderFailure?.ToString(),
                    failedJob = terminal.FailedProvider?.ToString(),
                    piecesAsked = voice.Calls,
                    piecesSpoken = voice.Spoken,
                    pieces = voice.Pieces,
                    persona,
                    breaks = McpServer.Breaks(breaks),
                    speechLimitReached = terminal.SpeechLimitReached,
                    speakerOpens = speakers.Opens,
                    samplesPlayed = speakers.Samples,
                    mayHavePlayed = terminal.MayHavePlayed,
                    // How often and how long the speakers ran dry mid-piece waiting for the voice's next audio.
                    pauses = terminal.Timings?.VoiceWaits ?? 0,
                    pausedMs = Math.Round((terminal.Timings?.VoiceWaited ?? TimeSpan.Zero).TotalMilliseconds)
                },
                captions = new
                {
                    complete = captionsComplete,
                    shown = lines.Length,
                    spoken = lines.Count(line => line.Spoken),
                    unsaid = lines.Count(line => !line.Spoken),
                    lines = lines.Select(line => new { text = line.Text, atMs = line.AtMs, spoken = line.Spoken })
                }
            };
        }
        finally
        {
            stop.Cancel();
            listener.Stop();
            try { await serving; } catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException or IOException) { }
        }
    }

    private static string Words(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // A line is spoken when the fixture voice had already said that many pieces as it was shown; the rest are unsaid captions.
    private static async Task ReadCaptionsAsync(SpokenTextFeed captions, List<(string, long, bool)> shown,
        System.Diagnostics.Stopwatch clock, Voice voice, CancellationToken cancellation)
    {
        try
        {
            var spoken = 0;
            await foreach (var line in captions.Lines.ReadAllAsync(cancellation))
            {
                var aloud = spoken < voice.Started;
                if (aloud) spoken++;
                lock (shown) shown.Add((line.Text, clock.ElapsedMilliseconds, aloud));
            }
        }
        catch (OperationCanceledException) { }
    }

    // Streams the reply a chunk at a time (a sentence, or a word for a given reply) with a pause between, the way a cloud model
    // streams it; with reasoning, a hidden reasoning delta comes first and the words only after that long, like a reasoning
    // model on OpenRouter. It notes whether each request carried the Thinking steps control and, with refuseThinking, refuses those.
    private static async Task ServeAsync(TcpListener listener, string[] chunks, TimeSpan gap, TimeSpan reasoning,
        CancellationToken cancellation, List<bool> asked, bool refuseThinking)
    {
        while (!cancellation.IsCancellationRequested)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellation);
            await using var stream = client.GetStream();
            var received = Encoding.UTF8.GetString(await HearingCheck.ReadRequestAsync(stream, cancellation));
            var control = received.Contains("chat_template_kwargs", StringComparison.Ordinal) ||
                received.Contains("\"reasoning", StringComparison.Ordinal);
            lock (asked) asked.Add(control);
            if (control && refuseThinking)
            {
                const string refusal = "{\"error\":{\"message\":\"Reasoning is mandatory for this model and cannot be disabled.\"}}";
                await WriteAsync(stream, "HTTP/1.1 400 Bad Request\r\nContent-Type: application/json\r\nConnection: close\r\nContent-Length: " +
                    Encoding.UTF8.GetByteCount(refusal) + "\r\n\r\n" + refusal, cancellation);
                continue;
            }
            await WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n",
                cancellation);
            const string chunk = "{\"id\":\"fixture\",\"object\":\"chat.completion.chunk\",\"model\":\"fixture\",\"choices\":[{\"index\":0,";
            if (reasoning > TimeSpan.Zero)
            {
                await WriteAsync(stream, "data: " + chunk + "\"delta\":{\"role\":\"assistant\",\"content\":\"\",\"reasoning\":\"Thinking it over.\"}," +
                    "\"finish_reason\":null}]}\n\n", cancellation);
                await Task.Delay(reasoning, cancellation);
            }
            for (var i = 0; i < chunks.Length; i++)
            {
                if (i > 0) await Task.Delay(gap, cancellation);
                var role = i == 0 && reasoning == TimeSpan.Zero ? "\"role\":\"assistant\"," : "";
                await WriteAsync(stream, "data: " + chunk + "\"delta\":{" + role + "\"content\":" + JsonSerializer.Serialize(chunks[i]) +
                    "},\"finish_reason\":null}]}\n\n", cancellation);
            }
            await WriteAsync(stream, "data: " + chunk + "\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n", cancellation);
        }
    }

    private static async Task WriteAsync(NetworkStream stream, string text, CancellationToken cancellation)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text), cancellation);
        await stream.FlushAsync(cancellation);
    }

    private sealed class Voice(string failure, int failAt, TimeSpan delay) : IHostSpeechClient
    {
        private int calls, spoken, started;
        private readonly List<string> pieces = [];
        internal int Calls => Volatile.Read(ref calls);
        internal int Spoken => Volatile.Read(ref spoken);
        // Pieces whose audio it produced (counted as the audio is handed over, before it is played).
        internal int Started => Volatile.Read(ref started);
        // The fixture reply's pieces it was asked to say, in order.
        internal string[] Pieces { get { lock (pieces) return [.. pieces]; } }

        public async IAsyncEnumerable<byte[]> StreamAsync(HostSpeechTarget target, BoundedSpeechInput input, CorrelationIds ids,
            long epoch, DateTimeOffset deadline, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref calls);
            lock (pieces) pieces.Add(input.Text);
            await Task.Delay(TimeSpan.FromMilliseconds(50) + delay, cancellationToken);
            if (call == failAt)
            {
                // What the desktop's host client throws for the gateway's worker.failed and worker.unavailable.
                if (failure == "server") throw new HostTextException(ProviderFailureCode.Server);
                if (failure == "unavailable") throw new HostTextException(ProviderFailureCode.ModelNotFound);
                if (failure == "stall") await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            // FIXTURE, NOT AI: a quarter second of a quiet 220 Hz tone per piece (half a second for a slow voice, said in two
            // quarter-second bursts so playback has started before the pause), written only to the fixture speaker.
            var pcm = new byte[(failure == "slow" ? 12_000 : 6_000) * 2];
            for (var i = 0; i < pcm.Length / 2; i++)
                BitConverter.TryWriteBytes(pcm.AsSpan(i * 2), (short)(Math.Sin(2 * Math.PI * 220 * i / 24_000.0) * 3000));
            Interlocked.Increment(ref started);
            if (failure == "slow")
            {
                // Slower than real time: half the piece, then a pause longer than all of its audio, then the rest.
                yield return pcm[..(pcm.Length / 2)];
                await Task.Delay(SlowGap, cancellationToken);
                yield return pcm[(pcm.Length / 2)..];
            }
            else yield return pcm;
            Interlocked.Increment(ref spoken);
        }
    }

    // Takes every sample at once and opens no device: nothing is played.
    private sealed class Speakers : IPlaybackDeviceFactory
    {
        private long samples;
        private int opens;
        internal long Samples => Interlocked.Read(ref samples);
        internal int Opens => Volatile.Read(ref opens);

        public IPlaybackDevice Open(OutputSelection selection, PcmFormat format, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref opens);
            return new Speaker(this, format);
        }

        private sealed class Speaker(Speakers owner, PcmFormat format) : IPlaybackDevice
        {
            public PlaybackDeviceInfo Info => new(format.SampleRate, format.Channels, 16, DeviceSampleEncoding.IntegerPcm,
                format.SampleRate / 20, false);
            public int GetPadding(CancellationToken cancellationToken) => 0;
            public int Write(ReadOnlySpan<byte> pcm, CancellationToken cancellationToken)
            {
                var written = pcm.Length / format.BlockAlignment;
                Interlocked.Add(ref owner.samples, written);
                return written;
            }
            public void Start(CancellationToken cancellationToken) { }
            public void StopAndReset() { }
            public void Dispose() { }
        }
    }

    private sealed class NoCredentials : IProviderCredentialSource
    {
        public ValueTask<BoundProviderCredential?> ResolveAsync(ProviderCredentialBinding binding, CancellationToken cancellationToken) =>
            ValueTask.FromResult<BoundProviderCredential?>(null);
    }

    // Allows exactly what was asked, bound to the fixture endpoint and fixture host, as the desktop's own authorization does.
    private sealed class Permissions(Uri baseUri, HostSpeechTarget voice) : IConversationAuthorizationSource
    {
        public ValueTask<AuthorizedTextOperation?> AuthorizeTextAsync(TextAuthorizationAction action, CancellationToken cancellationToken)
        {
            var until = Until(action.Context.Deadline);
            return ValueTask.FromResult<AuthorizedTextOperation?>(new(new TextDisclosureAuthorization(
                new(baseUri, ProviderRole.Llm, action.Model.UpstreamModelId), action.Model, action.Context.Ids, action.Context.Epoch,
                action.Limits, until, true, true), new(action.Budget, until)));
        }

        public ValueTask<AuthorizedSpeechOperation?> AuthorizeSpeechAsync(SpeechAuthorizationAction action, CancellationToken cancellationToken)
        {
            var until = Until(action.Context.Deadline);
            return ValueTask.FromResult<AuthorizedSpeechOperation?>(new(new SpeechDisclosureAuthorization(
                HostSpeechSynthesisStream.Binding(voice), action.Selection, action.Input, action.Context.Ids, action.Context.Epoch,
                action.Limits, until, true, true, true), new(action.Budget, until)));
        }

        private static DateTimeOffset Until(DateTimeOffset deadline)
        {
            var cap = DateTimeOffset.UtcNow.AddSeconds(30);
            return deadline < cap ? deadline : cap;
        }
    }
}
