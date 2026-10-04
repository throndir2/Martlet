using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Audio;
using Martlet.Conversation;
using Martlet.Core.Audio;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Mcp.Shared;
using Martlet.Providers;

namespace Martlet.Mcp;

/// <summary>straight_voice_check: Companion › Listening › When Thinking can hear you › Send my voice straight to Thinking,
/// rehearsed headless with the production pieces: the conversation runtime and Chat Completions adapter, the desktop's own
/// SpokenWords (the words of a recording sent alone, speech-to-text beside the reply) and ConversationContextBuffer (the
/// conversation each next request carries). Utterances are synthesized with a Windows voice (never a microphone, nothing played).
/// Each turn sends the recording alone (no transcript) and transcribes it in parallel (Parakeet on this PC when it is
/// downloaded, else a fixture transcriber); once the words come they replace the recording in the conversation, and the next
/// turn's request carries them. Then the same utterances transcribed first (the transcript, then both), and a model that refuses
/// the recording: the turn waits for the words and asks again with them. Thinking is Ollama on this PC with live (a model that
/// hears, gemma4:e2b by default) or a fixture endpoint on 127.0.0.1 (canned replies, NOT AI); either way a loopback relay
/// records each request body. Nothing leaves this PC; reads no credentials.</summary>
internal static class StraightVoiceCheck
{
    private const string LocalOllama = "http://127.0.0.1:11434";
    private const string FixtureModel = "fixture-hearing-model";
    private const int Rate = 16_000;
    private static readonly TimeSpan FixtureTranscription = TimeSpan.FromMilliseconds(300);
    // What the user "says" (a Windows voice), one utterance a turn.
    private static readonly string[] Said =
        ["What is two plus three?", "Now tell me one fun fact about owls.", "Thanks. What is the capital of France?"];
    private const string Persona = "You are Martlet, a friendly desktop companion talking with the user. Keep every reply to one or " +
        "two short sentences.";

    internal static async Task<object> RunAsync(string martletDirectory, string speechDirectory, bool live, string? model,
        CancellationToken cancellation)
    {
        var thinking = live ? model ?? "gemma4:e2b" : FixtureModel;
        if (live)
        {
            try { ChatCompletionsSetup.ModelId(thinking); }
            catch (ContractException error) { throw new ArgumentException(error.Message); }
        }
        var clips = await Task.Run(() => Said.Select(Synthesize).ToArray(), cancellation);
        using var listener = Listener.Create(martletDirectory, speechDirectory);
        if (live && await WarmAsync(thinking, cancellation) is { } problem)
            return new { ok = false, thinking = new { endpoint = "Ollama on this PC", model = thinking }, problem };
        await using var relay = Relay.Start(live ? LocalOllama : null);
        await using var runtime = ConversationRuntime.Create(new NoCredentials(), new NoSpeakers());
        var permissions = new Permissions(ChatCompletionsSetup.BaseUri(relay.BaseUrl));
        ConversationRequest Request(BoundedTextInput input, Func<CancellationToken, Task<string?>>? words) => new(input,
            new TextModelSelection(ChatCompletionsSetup.Alias, thinking), new TextGenerationLimits { MaxOutputTokens = 120 },
            new ConversationLimits(), chat: new ChatCompletionsTarget(relay.BaseUrl, Keyless: true),
            generation: new GenerationSettings { Reasoning = false }, spokenWords: words);
        var straightInstructions = Instructions(PromptCatalog.HeardVoiceOnly);

        // Straight to Thinking, turn after turn, each next request carrying the conversation so far.
        var context = new ConversationContextBuffer();
        var straight = new List<Turn>();
        for (var i = 0; i < clips.Length; i++)
        {
            var words = new SpokenWords(TimeProvider.System);
            IReadOnlyList<TextHistoryMessage> earlier;
            lock (context) earlier = context.Snapshot(sent: true);
            var input = new BoundedTextInput(SpokenWords.StandIn, straightInstructions, earlier, audio: clips[i].Clip);
            var before = relay.Bodies.Length;
            var turn = runtime.Start(Request(input, token => SpokenWords.TranscriptAsync([words], token)), permissions, cancellation);
            words.ReplyStarted(TimeProvider.System.GetTimestamp());
            // Speech-to-text beside the reply, never before it.
            var (clip, said) = (clips[i], Said[i]);
            var transcribing = Task.Run(() => listener.Transcribe(clip, words, said), cancellation);
            var terminal = await turn.Completion.WaitAsync(TimeSpan.FromSeconds(90), cancellation);
            await transcribing;
            var sent = relay.Bodies.Skip(before).FirstOrDefault();
            // The exchange is kept at once; its words replace the recording once they came (the desktop waits at most 1.5 s).
            Task[] pending;
            lock (context)
            {
                var exchange = context.Add(SpokenWords.StandIn, turn.Content.Text, input.SentUserText);
                ConversationContextBuffer.Pending(exchange, FillAsync(context, exchange, words, input));
                pending = context.Filling();
            }
            var waited = Stopwatch.StartNew();
            await Task.WhenAny(Task.WhenAll(pending), Task.Delay(1500, cancellation));
            straight.Add(new(i, terminal, sent is null ? null : Describe(sent), words, turn.Content.Text, waited.Elapsed));
        }
        IReadOnlyList<TextHistoryMessage> history;
        lock (context) history = context.Snapshot(sent: true);
        var carried = history.Where(message => message.Role == TextHistoryRole.User).Select(message => message.Text).ToArray();
        // The request after the last turn: a typed message with the conversation as it is now.
        var next = new BoundedTextInput("Please say all of that again in one sentence.", straightInstructions, history);
        var beforeNext = relay.Bodies.Length;
        var nextTurn = runtime.Start(Request(next, null), permissions, cancellation);
        var nextTerminal = await nextTurn.Completion.WaitAsync(TimeSpan.FromSeconds(90), cancellation);
        var nextBody = relay.Bodies.Skip(beforeNext).FirstOrDefault() is { } body ? Describe(body) : null;

        // Transcribe first (today's other choice): the transcript, then both, in a conversation of its own.
        var firstInstructions = Instructions(PromptCatalog.HeardVoice);
        var firstContext = new ConversationContextBuffer();
        var transcribeFirst = new List<object>();
        var firstTotals = new List<double>();
        foreach (var clip in clips)
        {
            var words = new SpokenWords(TimeProvider.System);
            var started = Stopwatch.StartNew();
            await Task.Run(() => listener.Transcribe(clip, words, Said[Array.IndexOf(clips, clip)]), cancellation);
            var sttMs = started.Elapsed.TotalMilliseconds;
            var input = new BoundedTextInput(words.Text is { Length: > 0 } text ? text : SpokenWords.NotTranscribed, firstInstructions,
                firstContext.Snapshot(sent: true), audio: clip.Clip);
            var turn = runtime.Start(Request(input, null), permissions, cancellation);
            var terminal = await turn.Completion.WaitAsync(TimeSpan.FromSeconds(90), cancellation);
            firstContext.Add(input.UserText, turn.Content.Text, input.SentUserText);
            var firstWords = terminal.FirstTextAfter?.TotalMilliseconds;
            if (firstWords is { } ms) firstTotals.Add(sttMs + ms);
            transcribeFirst.Add(new
            {
                state = terminal.State.ToString(), speechToTextMs = Math.Round(sttMs), firstWordsMs = Round(firstWords),
                firstWordsAfterSpeechMs = firstWords is { } after ? Math.Round(sttMs + after) : (double?)null,
                inputTokens = terminal.InputTokens, cachedTokens = terminal.CachedInputTokens
            });
        }

        // A model that refuses the recording: the reply waits for the words and asks again with them (never the stand-in).
        var refusal = await RefusalAsync(clips[0], listener, cancellation);
        // The quick check of something short that went straight (the desktop drops the reply if it isn't words before it plays).
        var quick = await Task.Run(() => QuickChecks(listener), cancellation);
        // With Thinking on this PC: does Parakeet running beside the request slow the model's first words?
        var contention = live ? await ContentionAsync(runtime, permissions, Request, straightInstructions, listener, cancellation) : null;

        var straightFirst = straight.Select(turn => turn.Terminal.FirstTextAfter?.TotalMilliseconds).OfType<double>().ToArray();
        var straightOk = straight.All(turn => turn.Terminal.State == ConversationState.Completed && turn.Sent is
            { HasAudio: true, WavValid: true, TextPart: SpokenWords.StandIn } && !turn.Sent.UserTextContainsWords(Said[turn.Index]));
        var historyOk = carried.Length == clips.Length && carried.All(text => !text.Contains(SpokenWords.StandIn, StringComparison.Ordinal)) &&
            straight.All(turn => turn.Words.Text is { Length: > 0 } && carried[turn.Index].StartsWith(turn.Words.Said!, StringComparison.Ordinal)) &&
            nextBody is { HistoryAudio: 0, HistoryHasStandIn: false } && nextTerminal.State == ConversationState.Completed &&
            // Each straight turn's request carried the earlier turns' words, never their recordings.
            straight.Skip(1).All(turn => turn.Sent is { HistoryAudio: 0, HistoryHasStandIn: false });
        var ok = straightOk && historyOk && refusal.Ok && quick.Ok;
        return new
        {
            ok,
            thinking = new { endpoint = live ? "Ollama on this PC (through a loopback relay that records each request)" : relay.BaseUrl + " (fixture, NOT AI)", model = thinking },
            speechToText = listener.Engine,
            utterances = clips.Select((clip, i) => new { said = Said[i], seconds = Math.Round(clip.Clip.Duration.TotalSeconds, 2), source = "Windows voice (synthesized)" }),
            straight = new
            {
                ok = straightOk,
                turns = straight.Select(turn => new
                {
                    state = turn.Terminal.State.ToString(), requestHadAudio = turn.Sent?.HasAudio, wavValid = turn.Sent?.WavValid,
                    audioSeconds = turn.Sent?.Seconds, requestText = turn.Sent?.TextPart, transcriptInRequest = turn.Sent?.UserTextContainsWords(Said[turn.Index]),
                    historyMessages = turn.Sent?.HistoryMessages, historyRecordings = turn.Sent?.HistoryAudio,
                    firstWordsMs = Round(turn.Terminal.FirstTextAfter?.TotalMilliseconds),
                    transcriptReadyAfterReplyStartMs = Round(turn.Words.ReadyAfterReply?.TotalMilliseconds),
                    speechToTextMs = Round(turn.Words.Took?.TotalMilliseconds), transcript = turn.Words.Text,
                    wordCheck = turn.Words.Ignored?.Reason ?? "words", nextWaitedForWordsMs = Math.Round(turn.Waited.TotalMilliseconds),
                    inputTokens = turn.Terminal.InputTokens, cachedTokens = turn.Terminal.CachedInputTokens,
                    cachedShare = turn.Terminal.CachedShare is { } share ? Math.Round(share, 3) : (double?)null,
                    reply = Clip(turn.Reply)
                })
            },
            history = new
            {
                ok = historyOk, userMessagesNowCarried = carried,
                nextRequest = nextBody is null ? null : new
                {
                    state = nextTerminal.State.ToString(), historyMessages = nextBody.HistoryMessages, recordingsInHistory = nextBody.HistoryAudio,
                    standInLeft = nextBody.HistoryHasStandIn, inputTokens = nextTerminal.InputTokens, cachedTokens = nextTerminal.CachedInputTokens,
                    cachedShare = nextTerminal.CachedShare is { } share ? Math.Round(share, 3) : (double?)null
                }
            },
            transcribeFirst = transcribeFirst,
            compare = new
            {
                straightFirstWordsMedianMs = Median(straightFirst),
                transcribeFirstFirstWordsAfterSpeechMedianMs = Median([.. firstTotals]),
                note = "Milliseconds from the end of the recording (the request start for straight; speech-to-text start for transcribe " +
                    "first) to the model's first words. The desktop log's Reply latency line adds the end-of-speech pause and the voice."
            },
            refusal = refusal.Report,
            quickCheck = quick.Report,
            contention
        };
    }

    private const int Rounds = 8;

    /// <summary>The same short straight request ("Yes, please." said by a Windows voice), alternating: alone; with Parakeet
    /// started at the request's start; with Parakeet started once the model's first words arrive (what the desktop does). The
    /// model's first words and the reply's end (milliseconds from the request's start), medians of each.</summary>
    private static async Task<object?> ContentionAsync(ConversationRuntime runtime, IConversationAuthorizationSource permissions,
        Func<BoundedTextInput, Func<CancellationToken, Task<string?>>?, ConversationRequest> request, string instructions, Listener listener,
        CancellationToken cancellation)
    {
        var pcm = UtteranceFilterCheck.Synthesize("speech:Yes, please.");
        var samples = UtteranceFilterCheck.ToFloats(pcm, 0, pcm.Length);
        if (listener.Quick(samples) is null) return new { ran = false, reason = "Parakeet isn't downloaded on this PC" };
        var clip = BoundedWaveAudio.FromPcm(new PcmFormat { SampleRate = Rate, Channels = 1, Encoding = PcmEncoding.Signed16LittleEndian }, pcm);
        string[] modes = ["alone", "atRequestStart", "atFirstWords"];
        var first = modes.ToDictionary(m => m, _ => new List<double>());
        var done = modes.ToDictionary(m => m, _ => new List<double>());
        var checkedAt = modes.ToDictionary(m => m, _ => new List<double>());
        for (var round = 0; round < 6; round++)
            foreach (var mode in modes)
            {
                var input = new BoundedTextInput(SpokenWords.StandIn, instructions, audio: clip);
                var clock = Stopwatch.StartNew();
                var turn = runtime.Start(request(input, null), permissions, cancellation);
                Task<double>? check = null;
                if (mode == "atRequestStart") check = Task.Run(() => { listener.Quick(samples); return clock.Elapsed.TotalMilliseconds; });
                else if (mode == "atFirstWords")
                    check = Task.Run(async () =>
                    {
                        while (turn.Snapshot.FirstTextAfter is null && !turn.Completion.IsCompleted) await Task.Delay(5);
                        listener.Quick(samples);
                        return clock.Elapsed.TotalMilliseconds;
                    });
                var terminal = await turn.Completion.WaitAsync(TimeSpan.FromSeconds(60), cancellation);
                var end = clock.Elapsed.TotalMilliseconds;
                if (check is not null) checkedAt[mode].Add(await check);
                if (terminal.FirstTextAfter is { } words) first[mode].Add(words.TotalMilliseconds);
                done[mode].Add(end);
            }
        return new
        {
            ran = true, rounds = Rounds, clip = "Yes, please. (Windows voice)",
            firstWordsMedianMs = modes.ToDictionary(m => m, m => Median([.. first[m]])),
            replyEndMedianMs = modes.ToDictionary(m => m, m => Median([.. done[m]])),
            quickCheckDoneMedianMs = modes.Skip(1).ToDictionary(m => m, m => Median([.. checkedAt[m]])),
            firstWordsMs = modes.ToDictionary(m => m, m => first[m].Select(v => Math.Round(v)).ToArray())
        };
    }

    private sealed record Quick(bool Ok, object Report);

    // The quick check (the desktop's CheckWordsBesideAsync) on fixtures: what the production voice-activity detector measures as
    // voice (under QuickVoice gets the check), what Parakeet hears and whether the production word check counts it as words.
    private static readonly TimeSpan QuickVoice = TimeSpan.FromSeconds(1);
    private static readonly (string Name, string Kind, bool Words)[] QuickFixtures =
    [
        ("mmm (hum)", "hum", false), ("cough", "cough", false), ("Mmm. (Windows voice)", "speech:Mmm.", false),
        ("Yes, please.", "speech:Yes, please.", true), ("Stop.", "speech:Stop.", true)
    ];

    private static Quick QuickChecks(Listener listener)
    {
        var results = new List<object>();
        var ok = true;
        var ran = false;
        foreach (var (name, kind, words) in QuickFixtures)
        {
            var pcm = UtteranceFilterCheck.Synthesize(kind);
            var (voiced, speech, _, _) = UtteranceFilterCheck.Voice(pcm);
            var checkedQuickly = voiced < QuickVoice;
            var heard = listener.Quick(UtteranceFilterCheck.ToFloats(pcm, 0, pcm.Length));
            if (heard is null)
            {
                results.Add(new { fixture = name, voicedMs = (int)voiced.TotalMilliseconds, quickCheck = checkedQuickly, ran = false });
                continue;
            }
            ran = true;
            var decision = UtteranceFilter.Check(heard.Value.Text, new UtteranceContext { Voiced = voiced, Speech = speech, Evidence = heard.Value.Evidence },
                ListeningSensitivity.Normal);
            var dropped = checkedQuickly && !decision.Keep;
            // Not words must be dropped; real words must never be.
            var right = words ? !dropped : dropped || !checkedQuickly;
            ok &= right;
            results.Add(new
            {
                fixture = name, voicedMs = (int)voiced.TotalMilliseconds, quickCheck = checkedQuickly, transcript = heard.Value.Text,
                words = decision.Keep, reason = decision.Keep ? null : decision.Reason, parakeetMs = Math.Round(heard.Value.Ms),
                replyDropped = dropped, expectWords = words, ok = right
            });
        }
        return new(ok, new
        {
            ok, ran, belowVoiceMs = QuickVoice.TotalMilliseconds,
            note = "The desktop runs this check beside the reply's request for what went straight to Thinking with less voice than " +
                "belowVoiceMs, and drops the reply if it isn't words before its first audio.",
            fixtures = results
        });
    }

    private sealed record Turn(int Index, ConversationSnapshot Terminal, Sent? Sent, SpokenWords Words, string Reply, TimeSpan Waited);

    private static async Task FillAsync(ConversationContextBuffer context, object exchange, SpokenWords words, BoundedTextInput input)
    {
        await words.Ready.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        var (user, sent, _) = SpokenWords.Kept([words], "", input);
        lock (context) context.Fill(exchange, user, sent?.SentUserText);
    }

    private sealed record Refusal(bool Ok, object Report);

    // A fixture endpoint that refuses any request with a recording, and a fixture transcriber that is slower than the refusal.
    private static async Task<Refusal> RefusalAsync((BoundedWaveAudio Clip, float[] Samples) clip, Listener listener, CancellationToken cancellation)
    {
        await using var relay = Relay.Start(null, refuseAudio: true);
        await using var runtime = ConversationRuntime.Create(new NoCredentials(), new NoSpeakers());
        var words = new SpokenWords(TimeProvider.System);
        var input = new BoundedTextInput(SpokenWords.StandIn, Instructions(PromptCatalog.HeardVoiceOnly), audio: clip.Clip);
        var request = new ConversationRequest(input, new TextModelSelection(ChatCompletionsSetup.Alias, FixtureModel),
            new TextGenerationLimits(), new ConversationLimits(), chat: new ChatCompletionsTarget(relay.BaseUrl, Keyless: true),
            spokenWords: token => SpokenWords.TranscriptAsync([words], token));
        var started = Stopwatch.StartNew();
        var turn = runtime.Start(request, new Permissions(ChatCompletionsSetup.BaseUri(relay.BaseUrl)), cancellation);
        var transcribing = Task.Run(async () =>
        {
            await Task.Delay(FixtureTranscription, cancellation);
            words.Heard(Said[0], null, null, FixtureTranscription);
        }, cancellation);
        var terminal = await turn.Completion.WaitAsync(TimeSpan.FromSeconds(30), cancellation);
        await transcribing;
        var bodies = relay.Bodies.Select(Describe).ToArray();
        var ok = terminal.State == ConversationState.Completed && terminal.AudioRejected && bodies.Length == 2 &&
            bodies[0] is { HasAudio: true, TextPart: SpokenWords.StandIn } && bodies[1] is { HasAudio: false, PlainText: true } &&
            bodies[1].TextPart == Said[0];
        return new(ok, new
        {
            ok, state = terminal.State.ToString(), audioRejected = terminal.AudioRejected, requests = bodies.Length,
            first = bodies.Length > 0 ? new { audio = bodies[0].HasAudio, text = bodies[0].TextPart } : null,
            retry = bodies.Length > 1 ? new { audio = bodies[1].HasAudio, text = bodies[1].TextPart } : null,
            retryAfterMs = Math.Round(started.Elapsed.TotalMilliseconds), transcriberMs = FixtureTranscription.TotalMilliseconds,
            reply = turn.Content.Text
        });
    }

    // The production prompts a straight reply (or a transcribed-first one) sends after the persona: always listening, then the
    // recording's own prompt.
    private static string Instructions(string recording) => string.Join("\n\n", new[]
    {
        Persona, PromptSettings.Fill(null, PromptCatalog.Listening, ("silent", StayQuiet.Marker)), PromptSettings.Fill(null, recording)
    }.OfType<string>());

    private static double? Round(double? value) => value is { } ms ? Math.Round(ms) : null;
    private static double? Median(double[] values) => values.Length == 0 ? null : Math.Round(values.Order().ElementAt(values.Length / 2));
    private static string Clip(string text) => text.Length <= 200 ? text : text[..199] + "…";

    // Loads the model before anything is measured (Ollama loads it on its first request).
    private static async Task<string?> WarmAsync(string model, CancellationToken cancellation)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            using var show = await http.PostAsync(LocalOllama + "/api/show", new StringContent(JsonSerializer.Serialize(new { model }),
                Encoding.UTF8, "application/json"), cancellation);
            if (!show.IsSuccessStatusCode) return $"Ollama on this PC doesn't have {model} (HTTP {(int)show.StatusCode}).";
            using var shown = JsonDocument.Parse(await show.Content.ReadAsStringAsync(cancellation));
            if (!shown.RootElement.TryGetProperty("capabilities", out var capabilities) ||
                !capabilities.EnumerateArray().Any(c => c.GetString() == "audio"))
                return $"Ollama says {model} doesn't hear (no audio capability). Choose a model that hears, such as gemma4:e2b.";
            using var warm = await http.PostAsync(LocalOllama + "/api/generate", new StringContent(
                JsonSerializer.Serialize(new { model, prompt = "", keep_alive = "10m" }), Encoding.UTF8, "application/json"), cancellation);
            return warm.IsSuccessStatusCode ? null : $"Ollama couldn't load {model} (HTTP {(int)warm.StatusCode}).";
        }
        catch (HttpRequestException error) { return "Ollama on this PC isn't reachable: " + error.Message; }
    }

    // A Windows voice saying it, 16 kHz mono (never a microphone, nothing played).
    private static (BoundedWaveAudio Clip, float[] Samples) Synthesize(string text)
    {
        using var stream = new MemoryStream();
        using (var voice = new System.Speech.Synthesis.SpeechSynthesizer())
        {
            voice.SetOutputToAudioStream(stream, new System.Speech.AudioFormat.SpeechAudioFormatInfo(Rate,
                System.Speech.AudioFormat.AudioBitsPerSample.Sixteen, System.Speech.AudioFormat.AudioChannel.Mono));
            voice.Speak(text);
        }
        var pcm = stream.ToArray();
        var samples = new float[pcm.Length / 2];
        for (var i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(pcm, i * 2) / 32768f;
        return (BoundedWaveAudio.FromPcm(new PcmFormat { SampleRate = Rate, Channels = 1, Encoding = PcmEncoding.Signed16LittleEndian }, pcm), samples);
    }

    // Parakeet on this PC (the desktop's local speech-to-text) when it is downloaded, else a fixture that knows the words.
    private sealed class Listener : IDisposable
    {
        private readonly Martlet.Sherpa.ParakeetEngine? engine;
        internal string Engine { get; }

        private Listener(Martlet.Sherpa.ParakeetEngine? engine, string name)
        {
            this.engine = engine;
            Engine = name;
        }

        internal static Listener Create(string martletDirectory, string speechDirectory)
        {
            var runtime = Martlet.Sherpa.SherpaComponents.RuntimeDirectory(martletDirectory);
            // The fastest Parakeet downloaded on this PC.
            var model = runtime is null ? null : Martlet.Sherpa.SherpaComponents.InstalledParakeetModels(speechDirectory).FirstOrDefault();
            if (runtime is null || model is null)
                return new(null, $"fixture transcriber ({FixtureTranscription.TotalMilliseconds:0} ms, knows the words; Parakeet " +
                    (runtime is null ? "runtime isn't in martletDirectory)" : "isn't downloaded in speechDirectory)"));
            var engine = new Martlet.Sherpa.ParakeetEngine(speechDirectory, model.Id, runtimeDirectory: runtime);
            engine.Warm();
            return new(engine, $"{model.Name} on this PC (sherpa-onnx, CPU)");
        }

        internal void Transcribe((BoundedWaveAudio Clip, float[] Samples) clip, SpokenWords words, string said)
        {
            var took = Stopwatch.StartNew();
            string text;
            if (engine is null)
            {
                Thread.Sleep(FixtureTranscription);
                text = said;
            }
            else
            {
                lock (engine) text = engine.Transcribe(clip.Samples).Text;
            }
            var context = new UtteranceContext { Speech = clip.Clip.Duration, Voiced = clip.Clip.Duration };
            words.Heard(text, null, UtteranceFilter.Check(text, context, ListeningSensitivity.Normal) is { Keep: false } ignored ? ignored : null,
                took.Elapsed);
        }

        /// <summary>Parakeet's words and evidence for 16 kHz samples, and how long it took; null without Parakeet.</summary>
        internal (string Text, TranscriptionEvidence Evidence, double Ms)? Quick(float[] samples)
        {
            if (engine is null) return null;
            var took = Stopwatch.StartNew();
            Martlet.Sherpa.ParakeetTranscript heard;
            lock (engine) heard = engine.Transcribe(samples);
            return (heard.Text, UtteranceFilterCheck.Evidence(heard), took.Elapsed.TotalMilliseconds);
        }

        public void Dispose() => engine?.Dispose();
    }

    private sealed record Sent(string[] Parts, string TextPart, bool HasAudio, bool PlainText, bool WavValid, double? Seconds,
        int HistoryMessages, int HistoryAudio, bool HistoryHasStandIn)
    {
        internal bool UserTextContainsWords(string said) =>
            said.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.Trim('.', '?', ',').ToLowerInvariant())
                .Where(w => w.Length > 3).Any(w => TextPart.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    // The request's last user message (its text part, any recording) and what its earlier messages carried.
    private static Sent Describe(byte[] body)
    {
        using var document = JsonDocument.Parse(body);
        var messages = document.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        var lastUser = Array.FindLastIndex(messages, m => m.GetProperty("role").GetString() == "user");
        var earlier = messages.Take(lastUser).Where(m => m.GetProperty("role").GetString() is "user" or "assistant").ToArray();
        int historyAudio = 0;
        var standIn = false;
        foreach (var message in earlier)
        {
            var content = message.GetProperty("content");
            if (content.ValueKind == JsonValueKind.String)
                standIn |= content.GetString()!.Contains(SpokenWords.StandIn, StringComparison.Ordinal);
            else
                foreach (var part in content.EnumerateArray())
                {
                    if (part.GetProperty("type").GetString() == "input_audio") historyAudio++;
                    if (part.TryGetProperty("text", out var t)) standIn |= t.GetString()!.Contains(SpokenWords.StandIn, StringComparison.Ordinal);
                }
        }
        var current = messages[lastUser].GetProperty("content");
        if (current.ValueKind == JsonValueKind.String)
            return new(["text"], current.GetString()!, false, true, false, null, earlier.Length, historyAudio, standIn);
        var parts = current.EnumerateArray().Select(p => p.GetProperty("type").GetString() ?? "").ToArray();
        var text = current.EnumerateArray().Where(p => p.GetProperty("type").GetString() == "text").Select(p => p.GetProperty("text").GetString())
            .FirstOrDefault() ?? "";
        byte[]? wave = null;
        foreach (var part in current.EnumerateArray())
            if (part.GetProperty("type").GetString() == "input_audio")
                wave = Convert.FromBase64String(part.GetProperty("input_audio").GetProperty("data").GetString() ?? "");
        var valid = wave is { Length: > 44 } && wave.AsSpan(0, 4).SequenceEqual("RIFF"u8) && wave.AsSpan(8, 4).SequenceEqual("WAVE"u8);
        double? seconds = valid ? Math.Round((wave!.Length - 44) / (2.0 * BitConverter.ToInt32(wave, 24)), 2) : null;
        return new(parts, text, wave is not null, false, valid, seconds, earlier.Length, historyAudio, standIn);
    }

    /// <summary>A loopback endpoint that records each request body, then answers with a canned reply (NOT AI), refuses a request
    /// with a recording (refuseAudio), or relays it to Ollama on this PC as the desktop sends it there (Thinking steps Off, usage
    /// with its prompt cache).</summary>
    private sealed class Relay : IAsyncDisposable
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };
        private readonly TcpListener listener;
        private readonly CancellationTokenSource stop = new();
        private readonly List<byte[]> bodies = [];
        private readonly string? forward;
        private readonly bool refuseAudio;
        private Task serving = Task.CompletedTask;
        internal string BaseUrl { get; }

        private Relay(string? forward, bool refuseAudio)
        {
            this.forward = forward;
            this.refuseAudio = refuseAudio;
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            BaseUrl = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1";
        }

        internal static Relay Start(string? forward, bool refuseAudio = false)
        {
            var relay = new Relay(forward, refuseAudio);
            relay.serving = relay.ServeAsync();
            return relay;
        }

        internal byte[][] Bodies { get { lock (bodies) return [.. bodies]; } }

        private async Task ServeAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(stop.Token); }
                catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) { return; }
                _ = HandleAsync(client);
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    await using var stream = client.GetStream();
                    var body = await HearingCheck.ReadRequestAsync(stream, stop.Token);
                    lock (bodies) bodies.Add(body);
                    if (refuseAudio && Encoding.UTF8.GetString(body).Contains("\"input_audio\"", StringComparison.Ordinal))
                    {
                        var refusal = Encoding.UTF8.GetBytes("{\"error\":{\"message\":\"This model does not take audio input.\",\"type\":\"invalid_request_error\"}}");
                        await WriteAsync(stream, "400 Bad Request", "application/json", refusal);
                        return;
                    }
                    if (forward is null)
                    {
                        const string chunk = "{\"id\":\"fixture\",\"object\":\"chat.completion.chunk\",\"model\":\"fixture\",\"choices\":[{\"index\":0,";
                        var events = "data: " + chunk + "\"delta\":{\"role\":\"assistant\",\"content\":\"Fixture reply (not AI).\"},\"finish_reason\":null}]}\n\n" +
                            "data: " + chunk + "\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
                        await WriteAsync(stream, "200 OK", "text/event-stream", Encoding.UTF8.GetBytes(events));
                        return;
                    }
                    // As the desktop sends it to Ollama on this PC: Thinking steps Off as reasoning_effort, usage with the cache.
                    var request = JsonNode.Parse(body)!.AsObject();
                    request.Remove("chat_template_kwargs");
                    request["reasoning_effort"] = "none";
                    request["stream_options"] = new JsonObject { ["include_usage"] = true };
                    using var message = new HttpRequestMessage(HttpMethod.Post, forward + "/v1/chat/completions")
                    {
                        Content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json")
                    };
                    using var response = await Http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, stop.Token);
                    var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {(int)response.StatusCode} {response.ReasonPhrase}\r\n" +
                        $"Content-Type: {response.Content.Headers.ContentType?.ToString() ?? "text/event-stream"}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(head, stop.Token);
                    await using var upstream = await response.Content.ReadAsStreamAsync(stop.Token);
                    var buffer = new byte[16_384];
                    int read;
                    while ((read = await upstream.ReadAsync(buffer, stop.Token)) > 0)
                    {
                        await stream.WriteAsync(buffer.AsMemory(0, read), stop.Token);
                        await stream.FlushAsync(stop.Token);
                    }
                }
                catch (Exception error) when (error is OperationCanceledException or IOException or SocketException or
                    HttpRequestException or JsonException or ObjectDisposedException) { }
            }
        }

        private async Task WriteAsync(NetworkStream stream, string status, string type, byte[] payload)
        {
            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head, stop.Token);
            await stream.WriteAsync(payload, stop.Token);
            await stream.FlushAsync(stop.Token);
        }

        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            listener.Stop();
            try { await serving; } catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) { }
            stop.Dispose();
        }
    }

    private sealed class NoCredentials : IProviderCredentialSource
    {
        public ValueTask<BoundProviderCredential?> ResolveAsync(ProviderCredentialBinding binding, CancellationToken cancellationToken) =>
            ValueTask.FromResult<BoundProviderCredential?>(null);
    }

    // Replies here are never spoken: no speaker is ever opened.
    private sealed class NoSpeakers : IPlaybackDeviceFactory
    {
        public IPlaybackDevice Open(OutputSelection selection, PcmFormat format, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("straight_voice_check plays nothing.");
    }

    // Allows exactly what was asked, the recording included, bound to the loopback endpoint, as the desktop's authorization does
    // for a Thinking model that hears.
    private sealed class Permissions(Uri baseUri) : IConversationAuthorizationSource
    {
        public ValueTask<AuthorizedTextOperation?> AuthorizeTextAsync(TextAuthorizationAction action, CancellationToken cancellationToken)
        {
            var until = action.Context.Deadline < DateTimeOffset.UtcNow.AddSeconds(90) ? action.Context.Deadline : DateTimeOffset.UtcNow.AddSeconds(90);
            return ValueTask.FromResult<AuthorizedTextOperation?>(new(new TextDisclosureAuthorization(
                new(baseUri, ProviderRole.Llm, action.Model.UpstreamModelId), action.Model, action.Context.Ids, action.Context.Epoch,
                action.Limits, until, true, true, allowAudioDisclosure: action.Input.Audio is not null), new(action.Budget, until)));
        }

        public ValueTask<AuthorizedSpeechOperation?> AuthorizeSpeechAsync(SpeechAuthorizationAction action, CancellationToken cancellationToken) =>
            ValueTask.FromResult<AuthorizedSpeechOperation?>(null);
    }
}
