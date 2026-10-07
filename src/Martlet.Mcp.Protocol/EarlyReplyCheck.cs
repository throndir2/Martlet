using System.Diagnostics;
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

/// <summary>early_reply_check: Companion › Listening › Start replies early, rehearsed headless in real time with the production
/// pieces: the end-of-turn gate (<see cref="EndOfTurnGate"/>) and the early-reply gate (<see cref="EarlyReplyGate"/>) frame by
/// frame through a scripted pause, the request comparison (<see cref="EarlyAsk"/>), the conversation runtime's held turn
/// (<see cref="ConversationRuntime.StartEarly"/>, <see cref="ConversationTurn.Release"/>) with the Chat Completions adapter, a
/// paired host's voice stream and the playback sink, and the reply latency line. FIXTURES, NOT AI: a quick transcript and a
/// judge answer after set times, a Chat Completions endpoint on 127.0.0.1 that streams a canned reply after thinkingMs, a host
/// voice that sends a quiet tone after voiceMs and speakers that open no device. Each scenario runs twice, with replies
/// started early and without, and says how much sooner the first audio came after the turn ended. Nothing is recorded,
/// played, downloaded or sent off this PC.</summary>
internal static class EarlyReplyCheck
{
    internal static readonly string[] Scenarios = ["incomplete", "plain", "complete", "resumed", "changed"];
    private const string Model = "fixture-model";
    private const string VoiceModel = "chatterbox-turbo";
    private static readonly TimeSpan Plain = TimeSpan.FromMilliseconds(800);
    // The reply the fixture endpoint streams, a piece at a time: the first piece is ready once "Let" arrives after it, and the
    // rest comes over the next 1.5 s, as a model writing a few sentences, so a reply let go is still streaming.
    private static readonly string[] Chunks = ["Sure, that sounds lovely. Let", "'s do it tonight. ", "I'll be ready at eight."];
    private static readonly int[] ChunkGapsMs = [0, 600, 900];

    // One scripted pause (or two): when the user's voice comes back, what the quick transcript says, what the judge answers
    // (null: no judge, the plain pause rule decides) and what the final transcript of the kept speech says.
    private sealed record Script(string Name, string Expected, TurnVerdict? Verdict, string[] Quick, string Final, int ResumeAtMs = 0,
        int ResumeForMs = 0);

    private static Script For(string name) => name switch
    {
        "incomplete" => new(name, "the judge finds it unfinished, you say nothing more and the longer pause ends the turn at 1600 ms: " +
            "the reply started early is promoted", TurnVerdict.Incomplete, ["So I was thinking we could go out tonight"],
            "So I was thinking we could go out tonight"),
        "plain" => new(name, "no judge (off or missing): the quick transcript still starts at 260 ms and the plain 800 ms pause ends " +
            "the turn: the reply started early is promoted", null, ["So I was thinking we could go out tonight"],
            "So I was thinking we could go out tonight"),
        "complete" => new(name, "the judge finds it finished: the turn ends about 300 ms into the pause, usually before the quick " +
            "transcript, so little or nothing starts early", TurnVerdict.Complete, ["Can you remind me to call my sister?"],
            "Can you remind me to call my sister?"),
        "resumed" => new(name, "you go on talking 700 ms into the pause: the reply started early is let go at once, and the next " +
            "pause starts another with the longer words, which is promoted", TurnVerdict.Incomplete,
            ["So I was thinking", "So I was thinking we could go out tonight"], "So I was thinking we could go out tonight", 700, 800),
        _ => new(name, "the final transcript of the speech kept differs from the quick one: the reply started early is let go and " +
            "a new one starts with the final words", TurnVerdict.Incomplete, ["So I was thinking we could go out"],
            "So I was thinking we could go out tonight")
    };

    internal static async Task<object> RunAsync(string? scenario, int? thinkingMs, int? voiceMs, int? sttMs, int? judgeMs,
        CancellationToken cancellation)
    {
        var names = scenario is null or "all" ? Scenarios : Scenarios.Contains(scenario) ? [scenario]
            : throw new ArgumentException($"'scenario' must be all or one of {string.Join(", ", Scenarios)}.");
        var thinking = Bounded(thinkingMs ?? 200, "thinkingMs", 0, 3000);
        var voice = Bounded(voiceMs ?? 350, "voiceMs", 0, 3000);
        var stt = Bounded(sttMs ?? 90, "sttMs", 0, 1000);
        var judge = Bounded(judgeMs ?? 26, "judgeMs", 0, 500);
        var results = new List<object>();
        var ok = true;
        foreach (var name in names)
        {
            cancellation.ThrowIfCancellationRequested();
            var script = For(name);
            var late = await RunOnceAsync(script, early: false, thinking, voice, stt, judge, cancellation);
            var soon = await RunOnceAsync(script, early: true, thinking, voice, stt, judge, cancellation);
            var saved = late.FirstAudioAfterTurnEndMs is { } a && soon.FirstAudioAfterTurnEndMs is { } b ? Math.Round(a - b) : (double?)null;
            var passed = Passed(script, soon, late, saved, thinking + voice);
            ok &= passed;
            results.Add(new
            {
                scenario = name, expected = script.Expected, ok = passed, savedMs = saved,
                withEarlyReplies = soon.Report(), without = late.Report()
            });
        }
        return new
        {
            ok,
            fixture = $"FIXTURES, NOT AI: quick transcript after {stt} ms, judge answer after {judge} ms, Thinking's first words after " +
                $"{thinking} ms, the voice's first audio after {voice} ms; the turn's pauses run in real time, 20 ms a frame",
            thinkingMs = thinking, voiceMs = voice, sttMs = stt, judgeMs = judge,
            scenarios = results
        };
    }

    private static int Bounded(int value, string name, int minimum, int maximum) =>
        value >= minimum && value <= maximum ? value : throw new ArgumentException($"'{name}' must be {minimum} through {maximum}.");

    // What each scenario must show: promoted where expected, nothing heard or shown before the turn ended, every start that was
    // let go stopped (still streaming, so its request aborted) with nothing of it played, and (where the reply started early in
    // the pause that ended the turn) the first audio much sooner.
    private static bool Passed(Script script, Run soon, Run late, double? saved, int pipeline)
    {
        var common = soon.PlayedBeforeTurnEnded == 0 && soon.ShownBeforeTurnEnded == 0 && late.Starts == 0 &&
            soon.FirstAudioAfterTurnEndMs is not null && late.FirstAudioAfterTurnEndMs is not null &&
            soon.LetGo.All(turn => turn is { State: nameof(ConversationState.Canceled), MayHavePlayed: false });
        return script.Name switch
        {
            "incomplete" or "plain" => common && soon.Outcome == EarlyReplyRecord.Promoted && soon.Starts == 1 && soon.Requests == 1 &&
                saved >= pipeline * 0.6,
            "resumed" => common && soon.Outcome == EarlyReplyRecord.Promoted && soon.Starts == 2 && soon.Cancelled == 1 &&
                soon.LetGo.Count == 1 && soon.Requests == 2 && soon.Aborted >= 1 && saved >= pipeline * 0.6,
            "changed" => common && soon.Outcome == EarlyReplyRecord.Changed && soon.Starts == 1 && soon.LetGo.Count == 1 &&
                soon.Requests == 2 && soon.Aborted >= 1 && soon.Asked[^1] == script.Final,
            // A finished turn: whatever started early never makes the first audio later.
            _ => common && (soon.Starts == 0 || soon.Outcome == EarlyReplyRecord.Promoted) && saved >= -60
        };
    }

    private sealed class Run
    {
        internal readonly List<(double AtMs, string What)> Decisions = [];
        internal double? TurnEndedMs, AnsweredMs, FirstAudioMs, FirstVoiceAskedMs;
        internal int Starts, Cancelled, Requests, Aborted, VoicePieces, PlayedBeforeTurnEnded, ShownBeforeTurnEnded;
        // Counted by the fixture endpoint and the captions feed as they go.
        internal int RequestCount, AbortedCount, CaptionCount;
        internal string? Outcome, Reason, LatencyLine;
        internal readonly List<string> Asked = [];
        // Each reply started early that was let go, as it ended: its state, whether any of it may have played, and how much
        // text it had (never shown).
        internal readonly List<(string State, bool MayHavePlayed, int Characters)> LetGo = [];
        internal double? FirstAudioAfterTurnEndMs => FirstAudioMs is { } audio && TurnEndedMs is { } ended ? Math.Round(audio - ended) : null;

        internal object Report() => new
        {
            turnEndedMs = TurnEndedMs, answeredMs = AnsweredMs, firstAudioMs = FirstAudioMs, firstAudioAfterTurnEndMs = FirstAudioAfterTurnEndMs,
            startedEarly = Starts, cancelled = Cancelled, outcome = Outcome, reason = Reason,
            thinkingRequests = Requests, abortedRequests = Aborted, askedWith = Asked,
            letGo = LetGo.Select(turn => new { state = turn.State, mayHavePlayed = turn.MayHavePlayed, textCharacters = turn.Characters }),
            voicePieces = VoicePieces, firstVoiceAskedMs = FirstVoiceAskedMs,
            playedBeforeTurnEnded = PlayedBeforeTurnEnded, shownBeforeTurnEnded = ShownBeforeTurnEnded,
            decisions = Decisions.Select(d => new { atMs = Math.Round(d.AtMs), what = d.What }),
            latencyLine = LatencyLine
        };
    }

    // Stops a reply started early that is let go and notes how it ended.
    private static async Task LetGoAsync(ConversationTurn held, Run run)
    {
        var ended = await held.StopAsync();
        await held.OwnershipRelease;
        run.LetGo.Add((ended.State.ToString(), ended.MayHavePlayed, ended.TextCharacters));
    }

    // One pass through the script in real time. All times are milliseconds from when the user stopped talking.
    private static async Task<Run> RunOnceAsync(Script script, bool early, int thinkingMs, int voiceMs, int sttMs, int judgeMs,
        CancellationToken cancellation)
    {
        var run = new Run();
        var watch = Stopwatch.StartNew();
        double Now() => watch.Elapsed.TotalMilliseconds;
        void Note(string what) { lock (run.Decisions) run.Decisions.Add((Now(), what)); }
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var baseUrl = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1";
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var serving = ServeAsync(listener, thinkingMs, run, stop.Token);
        try
        {
            var speakers = new Speakers(Now);
            var voice = new Voice(voiceMs, run, Now);
            var captions = new SpokenTextFeed();
            _ = ReadCaptionsAsync(captions, run, stop.Token);
            var preset = Guid.NewGuid();
            var target = new HostSpeechTarget("https://127.0.0.1:9443", "fixture-host", "sha256:" + new string('0', 64),
                "desktop-fixture", Guid.NewGuid(), VoiceModel, preset, new string('0', 64));
            var output = new SpeechOutput(new SpeechSynthesisSelection(SelfHostSetup.GatewayF5Alias, VoiceModel, preset.ToString("N"),
                SpeechOutputFormat.Pcm24KhzMono16Le), new OutputSelection(OutputPolicy.DefaultAtStart), new SpeechSynthesisLimits
            {
                MaxAudioBytes = 480_000, MaxAudioDuration = TimeSpan.FromSeconds(10), FirstAudioTimeout = TimeSpan.FromSeconds(20),
                MaxRequestTime = TimeSpan.FromSeconds(20)
            });
            ConversationRequest Request(string words) => new(new BoundedTextInput(words, "Fixture check."),
                new TextModelSelection(ChatCompletionsSetup.Alias, Model), new TextGenerationLimits(),
                new ConversationLimits { MaxSpeechSegments = 8, MaxSpeechTextBytes = 12_288, MaxReservedSpeechSamples = 1_920_000 },
                output, new ChatCompletionsTarget(baseUrl, Keyless: true), hostSpeech: target);
            var permissions = new SpokenReplyCheck.Permissions(ChatCompletionsSetup.BaseUri(baseUrl), target);
            await using var runtime = ConversationRuntime.Create(new SpokenReplyCheck.NoCredentials(), speakers, hostSpeech: voice,
                spokenText: captions);
            var gate = script.Verdict is null ? null : new EndOfTurnGate(Plain);
            var cap = gate?.DetectorEndSilence ?? Plain;
            var starts = new EarlyReplyGate(early ? new EarlyReplyOptions() : EarlyReplyOptions.Off);
            ConversationTurn? held = null;
            EarlyAsk? heldAsk = null;
            long heldAt = 0;
            // The reply latency line's steps of the pause under way, at their own times (as the desktop marks them).
            var marks = new List<(string Step, long At)>();
            // The script: the pause (or the first one), then, when the voice comes back, more speech and a second pause.
            var pauses = 1;
            var silenceFrames = 0;
            Task<string>? quick = null;
            var quickPause = 0;
            var quickTried = false;
            Task<(TurnVerdict Verdict, long At)>? judging = null;
            var judgedPause = 0;
            var turnEnded = false;
            var frame = 0;
            while (!turnEnded)
            {
                cancellation.ThrowIfCancellationRequested();
                var due = (frame + 1) * 20.0;
                while (Now() < due) await Task.Delay(1, cancellation);
                frame++;
                var at = frame * 20.0;
                var voiceBack = script.ResumeAtMs > 0 && pauses == 1 && at > script.ResumeAtMs && at <= script.ResumeAtMs + script.ResumeForMs;
                if (voiceBack)
                {
                    if (silenceFrames > 0)
                    {
                        Note("your voice came back");
                        gate?.Step(0, judgeable: true);
                        if (starts.VoiceResumed() && held is not null)
                        {
                            Note("the reply started early was let go");
                            await LetGoAsync(held, run);
                            held = null;
                        }
                    }
                    silenceFrames = 0;
                    continue;
                }
                if (silenceFrames == 0 && frame > 1)
                {
                    pauses++;
                    marks.Clear();
                    Note("a second pause began");
                }
                silenceFrames++;
                var pauseIndex = pauses;
                var quickWords = script.Quick[Math.Min(pauseIndex, script.Quick.Length) - 1];
                // The judge's answer, once in, for the pause it was asked about (the desktop marks it when the turn doesn't end
                // complete).
                if (judging is { IsCompleted: true } answered && gate is not null)
                {
                    if (gate.Judged(judgedPause, new(answered.Result.Verdict, answered.Result.Verdict == TurnVerdict.Complete ? 0.9 : 0.1)) &&
                        answered.Result.Verdict != TurnVerdict.Complete)
                        marks.Add((ReplyLatency.EndOfTurnJudge, answered.Result.At));
                    Note($"the judge answered {answered.Result.Verdict.ToString().ToLowerInvariant()}");
                    judging = null;
                }
                var step = gate?.Step(silenceFrames, judgeable: true) ?? EndOfTurnStep.Wait;
                // After 260 ms of silence: the judge (when there is one) and the quick transcript of the speech so far.
                var quickNow = gate is not null ? step == EndOfTurnStep.Judge : silenceFrames == 13;
                if (quickNow)
                {
                    Note(gate is null ? "the quick transcript started (no judge)" : "the judge was asked and the quick transcript started");
                    quick = After(sttMs, quickWords, cancellation);
                    quickPause = pauseIndex;
                    quickTried = false;
                    if (gate is not null)
                    {
                        marks.Add((ReplyLatency.EndOfTurnWait, TimeProvider.System.GetTimestamp()));
                        judgedPause = gate.Pause;
                        judging = Answer(judgeMs, script.Verdict!.Value, cancellation);
                    }
                }
                if (!quickTried && quick is { IsCompleted: true } done && quickPause == pauseIndex)
                {
                    quickTried = true;
                    Note($"the quick transcript came: \"{done.Result}\"");
                    if (starts.TryStart(quickPause, pauseIndex, silent: true, worth: true))
                    {
                        heldAsk = new(done.Result, true, false, null, null, false, null);
                        heldAt = TimeProvider.System.GetTimestamp();
                        held = runtime.StartEarly(Request(done.Result), permissions, prepareVoice: true, cancellation);
                        lock (run.Asked) run.Asked.Add(done.Result);
                        Note($"a reply started early (start {starts.Starts})");
                    }
                }
                var ended = step is EndOfTurnStep.Complete or EndOfTurnStep.Fallback ||
                    gate is null && silenceFrames * 20 >= Plain.TotalMilliseconds || silenceFrames * 20 >= cap.TotalMilliseconds;
                if (!ended) continue;
                turnEnded = true;
                run.TurnEndedMs = Now();
                // The wait counts from when the user stopped talking: the start of the pause that ended the turn.
                var timeline = new ReplyTimeline(TimeProvider.System, ReplyTimeline.YouStopped,
                    TimeProvider.System.GetTimestamp() - (long)(silenceFrames * 0.02 * Stopwatch.Frequency));
                var kept = starts.Ended(pauseIndex);
                // As the desktop's timeline goes: the turn's own steps; a reply started early adds its own once it is promoted.
                timeline.Mark(marks);
                timeline.Mark(step == EndOfTurnStep.Complete ? ReplyLatency.EndOfTurnJudge : ReplyLatency.EndOfSpeech);
                Note(step == EndOfTurnStep.Complete ? "the turn ended: complete" : gate is null ? "the turn ended: the plain pause"
                    : "the turn ended: the longer pause for unfinished speech");
                run.PlayedBeforeTurnEnded = (int)speakers.Samples;
                run.ShownBeforeTurnEnded = Volatile.Read(ref run.CaptionCount);
                // Speech-to-text reuses the quick transcript of this pause (or transcribes the kept speech again).
                var final = script.Final;
                if (quick is not null && quickPause == pauseIndex) await quick;
                timeline.Mark("speech-to-text");
                run.AnsweredMs = Now();
                timeline.Mark("waiting to answer");
                var asked = new EarlyAsk(final, true, false, null, null, false, null);
                ConversationTurn reply;
                long replyStartedAt;
                if (kept && held is not null && heldAsk!.Differs(asked) is null)
                {
                    held.Release();
                    reply = held;
                    replyStartedAt = heldAt;
                    timeline.Mark("building the request", heldAt);
                    run.Outcome = EarlyReplyRecord.Promoted;
                    Note("the reply started early was promoted");
                    timeline.Early = new(true, starts.Starts, starts.Cancelled);
                }
                else
                {
                    if (held is not null)
                    {
                        if (kept) starts.Cancel();
                        run.Outcome = EarlyReplyRecord.Changed;
                        run.Reason = kept ? heldAsk!.Differs(asked) : "your turn didn't end in that pause";
                        Note($"the reply started early was let go ({run.Reason})");
                        await LetGoAsync(held, run);
                    }
                    timeline.Early = starts.Starts > 0 ? new(false, starts.Starts, starts.Cancelled) : null;
                    replyStartedAt = TimeProvider.System.GetTimestamp();
                    timeline.Mark("building the request", replyStartedAt);
                    reply = runtime.Start(Request(final), permissions, cancellation);
                    lock (run.Asked) run.Asked.Add(final);
                    Note("the reply started");
                }
                var terminal = await reply.Completion.WaitAsync(TimeSpan.FromSeconds(30), cancellation);
                await reply.OwnershipRelease.WaitAsync(TimeSpan.FromSeconds(10), cancellation);
                run.LatencyLine = ReplyLatency.Describe(timeline, replyStartedAt, TimeProvider.System, terminal, $"Thinking {Model}, voice {VoiceModel}");
            }
            run.Starts = starts.Starts;
            run.Cancelled = starts.Cancelled;
            run.FirstAudioMs = speakers.FirstAt;
            return run;
        }
        finally
        {
            stop.Cancel();
            listener.Stop();
            try { await serving; } catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException or IOException) { }
            run.Requests = run.RequestCount;
            run.Aborted = run.AbortedCount;
        }
    }

    private static async Task<T> After<T>(int milliseconds, T value, CancellationToken cancellation)
    {
        await Task.Delay(milliseconds, cancellation);
        return value;
    }

    // The fixture judge: its verdict after judgeMs, and when it answered.
    private static async Task<(TurnVerdict Verdict, long At)> Answer(int milliseconds, TurnVerdict verdict, CancellationToken cancellation)
    {
        await Task.Delay(milliseconds, cancellation);
        return (verdict, TimeProvider.System.GetTimestamp());
    }

    private static async Task ReadCaptionsAsync(SpokenTextFeed captions, Run run, CancellationToken cancellation)
    {
        try
        {
            await foreach (var _ in captions.Lines.ReadAllAsync(cancellation)) Interlocked.Increment(ref run.CaptionCount);
        }
        catch (OperationCanceledException) { }
    }

    // Every request on its own connection, at once: Thinking's first words after thinkingMs, the rest 100 ms later. A request the
    // conversation lets go is aborted (its connection closes), which the next write finds.
    private static async Task ServeAsync(TcpListener listener, int thinkingMs, Run run, CancellationToken cancellation)
    {
        var handlers = new List<Task>();
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellation);
                handlers.Add(Task.Run(() => HandleAsync(client, thinkingMs, run, cancellation), CancellationToken.None));
            }
        }
        finally
        {
            try { await Task.WhenAll(handlers); } catch (Exception error) when (error is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
        }
    }

    private static async Task HandleAsync(TcpClient client, int thinkingMs, Run run, CancellationToken cancellation)
    {
        using (client)
        {
            var completed = false;
            try
            {
                await using var stream = client.GetStream();
                await HearingCheck.ReadRequestAsync(stream, cancellation);
                Interlocked.Increment(ref run.RequestCount);
                await Write(stream, "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n", cancellation);
                const string chunk = "{\"id\":\"fixture\",\"object\":\"chat.completion.chunk\",\"model\":\"fixture\",\"choices\":[{\"index\":0,";
                await Task.Delay(thinkingMs, cancellation);
                for (var i = 0; i < Chunks.Length; i++)
                {
                    if (i > 0) await Task.Delay(ChunkGapsMs[i], cancellation);
                    var role = i == 0 ? "\"role\":\"assistant\"," : "";
                    await Write(stream, "data: " + chunk + "\"delta\":{" + role + "\"content\":" + JsonSerializer.Serialize(Chunks[i]) +
                        "},\"finish_reason\":null}]}\n\n", cancellation);
                }
                await Write(stream, "data: " + chunk + "\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n", cancellation);
                completed = true;
            }
            catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException) { }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            finally
            {
                if (!completed && !cancellation.IsCancellationRequested) Interlocked.Increment(ref run.AbortedCount);
            }
        }
    }

    private static async Task Write(NetworkStream stream, string text, CancellationToken cancellation)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text), cancellation);
        await stream.FlushAsync(cancellation);
    }

    // FIXTURE, NOT AI: a quarter second of a quiet 220 Hz tone per piece, its first audio after voiceMs.
    private sealed class Voice(int voiceMs, Run run, Func<double> now) : IHostSpeechClient
    {
        public async IAsyncEnumerable<byte[]> StreamAsync(HostSpeechTarget target, BoundedSpeechInput input, CorrelationIds ids,
            long epoch, DateTimeOffset deadline, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref run.VoicePieces) == 1) run.FirstVoiceAskedMs = Math.Round(now());
            await Task.Delay(voiceMs, cancellationToken);
            var pcm = new byte[6_000 * 2];
            for (var i = 0; i < pcm.Length / 2; i++)
                BitConverter.TryWriteBytes(pcm.AsSpan(i * 2), (short)(Math.Sin(2 * Math.PI * 220 * i / 24_000.0) * 3000));
            yield return pcm;
        }
    }

    // Takes every sample at once, opens no device and notes when the first one came: nothing is played.
    private sealed class Speakers(Func<double> now) : IPlaybackDeviceFactory
    {
        private readonly Func<double> clock = now;
        private long samples;
        private double first = -1;
        internal long Samples => Interlocked.Read(ref samples);
        internal double? FirstAt { get { var at = Volatile.Read(ref first); return at < 0 ? null : Math.Round(at); } }

        public IPlaybackDevice Open(OutputSelection selection, PcmFormat format, CancellationToken cancellationToken) => new Speaker(this, format);

        private sealed class Speaker(Speakers owner, PcmFormat format) : IPlaybackDevice
        {
            public PlaybackDeviceInfo Info => new(format.SampleRate, format.Channels, 16, DeviceSampleEncoding.IntegerPcm,
                format.SampleRate / 20, false);
            public int GetPadding(CancellationToken cancellationToken) => 0;
            public int Write(ReadOnlySpan<byte> pcm, CancellationToken cancellationToken)
            {
                var written = pcm.Length / format.BlockAlignment;
                if (written > 0 && Interlocked.Add(ref owner.samples, written) == written)
                    Interlocked.CompareExchange(ref owner.first, owner.clock(), -1);
                return written;
            }
            public void Start(CancellationToken cancellationToken) { }
            public void StopAndReset() { }
            public void Dispose() { }
        }
    }
}
