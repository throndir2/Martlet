using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Martlet.Audio;
using Martlet.Conversation;
using Martlet.Core.Settings;
using Martlet.Providers;
using Martlet.Providers.Tests;

namespace Martlet.Desktop.Tests;

/// <summary>Companion › Listening › Start replies early, in the conversation controller with always listening on the fixture
/// microphone, Parakeet on this PC (a fixture transcriber), a fixture end-of-turn judge and a Thinking model on this PC (a
/// fixture Chat Completions endpoint): a reply starts at the quick transcript and is promoted when the turn ends with the same
/// words (no second request), is let go when the user goes on talking or the words change, does nothing that only a reply
/// may do before it is taken (history, the context board's consume-on-read notes, committing to answer) and starts early with
/// a cloud Thinking model only when Also for cloud models is on.</summary>
public sealed class EarlyReplyDesktopTests
{
    private const string Short = "So I was thinking we could go out";
    private const string Long = "So I was thinking we could go out tonight to the new place";

    // Parakeet on this PC (FIXTURE): the words of the speech it is given, more of them for longer speech.
    private sealed class Words : ILocalTranscriber
    {
        internal int Calls;
        internal int LongerThan { get; set; } = int.MaxValue;
        public Task<LocalTranscript> TranscribeAsync(string modelId, ReadOnlyMemory<byte> pcm16kMono, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new LocalTranscript(pcm16kMono.Length > LongerThan ? Long : Short));
        }
    }

    // The end-of-turn judge (FIXTURE): always the same verdict.
    private sealed class Judge(TurnVerdict verdict) : IEndOfTurnJudge
    {
        public string Name => "fixture judge";
        public bool Available => true;
        public Task<EndOfTurnJudgement> JudgeAsync(EndOfTurnRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new EndOfTurnJudgement(verdict, verdict == TurnVerdict.Complete ? 0.9 : 0.1));
    }

    private static HttpResponseMessage Reply(string text) => TextRecordingHandler.Sse(
        "data: {\"id\":\"chat-fixture\",\"object\":\"chat.completion.chunk\",\"model\":\"server-model\",\"choices\":[{\"index\":0," +
        "\"delta\":{\"role\":\"assistant\",\"content\":" + JsonSerializer.Serialize(text) + "},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n");

    // A fixture conversation: Listening on Parakeet here and, unless cloud, Thinking at a server on this PC; without judge, the
    // plain pause rule ends each turn.
    private static async Task<(LiveFixture Fixture, Words Words)> Create(TurnVerdict verdict = TurnVerdict.Incomplete, bool cloud = false,
        bool judge = true)
    {
        var words = new Words();
        var fixture = await LiveFixture.Create(localListener: words, turnJudge: judge ? new Judge(verdict) : null);
        var settings = (await fixture.Store.LoadAsync()).Settings!;
        var oldStt = settings.Setup!.Routes.Single(route => route.Role == SetupRole.Stt);
        settings = SetupSettings.QueueReplacedCredential(LocalSpeechSetup.SelectParakeet(settings, LocalSpeechSetup.Parakeet110mEnglishModelId), oldStt);
        var stt = settings.Setup!.Routes.Single(route => route.Role == SetupRole.Stt);
        settings = SetupSettings.ReplaceRoute(settings, stt with { Consent = stt.Selection() });
        if (!cloud)
        {
            var oldLlm = settings.Setup!.Routes.Single(route => route.Role == SetupRole.Llm);
            settings = SetupSettings.QueueReplacedCredential(ChatCompletionsSetup.SelectRoute(settings, "http://127.0.0.1:1234/v1", "llama3.2:3b"), oldLlm);
            var llm = settings.Setup!.Routes.Single(route => route.Role == SetupRole.Llm);
            settings = SetupSettings.ReplaceRoute(settings, llm with { Consent = llm.Selection() });
        }
        await fixture.Save(settings);
        Assert.True(fixture.Controller.Configuration!.LocalStt());
        Assert.Equal(!cloud, fixture.Controller.Configuration.NetworkThinking);
        // What the talk window would send with the next thing heard: text only, nothing else waiting.
        fixture.Controller.EarlyPlan = new(Voice: false, Chattiness: null, DiscordCall: false);
        return (fixture, words);
    }

    private static LiveListener Listen(LiveFixture fixture, EarlyReplyOptions? early = null) =>
        fixture.Controller.Listen(new ListeningOptions(true, new VoiceActivitySettings(), RequireVoiceId: false, Early: early ?? new()));

    // 100 ms packets of the fixture microphone: quiet, or a voice.
    private static void Say(LiveFixture fixture, int packets, bool voice)
    {
        for (var p = 0; p < packets; p++)
        {
            var packet = new byte[3200];
            for (var i = 0; i < 1600; i++)
            {
                var t = (p * 1600 + i) / 16000.0;
                var value = (voice ? 0.25 : 0.001) * Math.Sin(2 * Math.PI * 220 * t) * (0.7 + 0.3 * Math.Sin(2 * Math.PI * 4 * t));
                System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(i * 2), (short)(value * 32767));
            }
            fixture.Capture.Packets.Enqueue(packet);
        }
    }

    private static async Task<HeardSpeech> Heard(LiveFixture fixture, LiveListener listener)
    {
        HeardSpeech? heard = null;
        await fixture.Advance(() => listener.TryTake(out heard) && heard!.Status.Code == "listen.heard");
        return heard!;
    }

    // What the talk window asks for once the turn ended (TryAnswer, for one utterance with nothing else waiting).
    private static LiveConversationOperation Answer(LiveFixture fixture, HeardSpeech heard, string? text = null) =>
        fixture.Controller.Start(text ?? heard.Text, voice: false, microphone: false, approved: true, spoken: true, heard: heard.Voices,
            confidence: heard.Confidence, timeline: heard.Timeline?.Copy(), hearLocalOnly: true);

    private static string[] Messages(string body) =>
        [.. JsonDocument.Parse(body).RootElement.GetProperty("messages").EnumerateArray().Select(message => message.GetRawText())];

    [Fact]
    public async Task A_reply_started_at_the_quick_transcript_is_promoted_when_the_longer_pause_ends_the_turn_with_the_same_words()
    {
        var (fixture, words) = await Create();
        await using var _ = fixture;
        var bodies = new ConcurrentQueue<string>();
        fixture.Chat.Respond = (_, _) =>
        {
            bodies.Enqueue(Encoding.UTF8.GetString(fixture.Chat.Body));
            return Task.FromResult(Reply("Sure, let's go."));
        };
        var board = fixture.Controller.Board;
        board.Post(ContextBoard.Touch, "Touched: a pat on the head.", fixture.Clock.GetLocalNow(), TimeSpan.FromMinutes(5), consume: true);
        var listener = Listen(fixture);
        try
        {
            Say(fixture, 5, voice: false);
            Say(fixture, 25, voice: true);
            Say(fixture, 3, voice: false);
            // 260 ms into the pause the judge (unfinished) and the quick transcript: the reply starts before the turn ends.
            await fixture.Advance(() => fixture.Chat.Calls == 1 && fixture.Controller.EarlyReply is { Turn.Snapshot.TextComplete: true });
            var early = fixture.Controller.EarlyReply!;
            Assert.Contains(Short, bodies.Single());
            Assert.Contains("Touched: a pat on the head.", bodies.Single());
            // Thinking runs on this PC and the voice is a paid cloud one: the reply starts early, its voice only once it is taken.
            Assert.Equal((true, false), LiveConversationController.EarlyAllowed(new EarlyReplyOptions(), fixture.Controller.Configuration) is var allowed
                ? (allowed.Thinking, allowed.Voice) : default);
            // Nothing that only a reply may do happened yet: no history, the note still waits, nothing committed, nothing said,
            // and barge-in doesn't see Martlet speaking.
            Assert.Equal(0, fixture.Controller.ContextTurns);
            Assert.Contains(board.Snapshot(fixture.Clock.GetLocalNow()).Notes, note => note.Source == ContextBoard.Touch);
            Assert.Empty(board.LastSent.Notes);
            Assert.Null(fixture.Controller.PolicySnapshot.ActiveIntentId);
            Assert.Null(fixture.Controller.Speaking);
            Assert.True(early.Turn!.Held);
            Assert.False(early.Status.Finished);

            // The longer pause for unfinished speech runs out: the turn ends with the same words, and the reply goes on as it is.
            Say(fixture, 14, voice: false);
            var heard = await Heard(fixture, listener);
            Assert.Equal(Short, heard.Text);
            Assert.Equal(1, words.Calls);
            var reply = Answer(fixture, heard);
            Assert.Same(early, reply);
            await fixture.Finish(reply);
            Assert.Equal("runtime.Completed", reply.Status.Code);
            Assert.Equal("Sure, let's go.", reply.Turn!.Content.Text);
            Assert.Equal(1, fixture.Chat.Calls);
            Assert.Equal(1, fixture.Controller.ContextTurns);
            Assert.DoesNotContain(board.Snapshot(fixture.Clock.GetLocalNow()).Notes, note => note.Source == ContextBoard.Touch);
            Assert.Contains(board.LastSent.Notes, note => note.Source == ContextBoard.Touch);
            var record = Assert.Single(fixture.Controller.EarlyReplies);
            Assert.Equal(EarlyReplyRecord.Promoted, record.Outcome);
            Assert.InRange(record.StartedAfter.TotalMilliseconds, 260, 400);
            // The reply latency line counts from when you stopped talking and says it started early.
            var line = ReplyLatency.Describe(reply.LatencyTimeline, reply.ReplyStartedAt, fixture.Clock, reply.Turn.Snapshot, null)!;
            Assert.Contains("ms, promoted.", line);
            Assert.Contains("end-of-turn wait", line);
            Assert.Contains(ReplyLatency.EndOfSpeech, line);
        }
        finally { fixture.Controller.StopListening(listener); }
    }

    [Fact]
    public async Task A_reply_started_early_is_let_go_at_once_when_you_go_on_talking_and_the_next_pause_starts_one_with_the_longer_words()
    {
        var (fixture, words) = await Create();
        await using var _ = fixture;
        var bodies = new ConcurrentQueue<string>();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aborted = 0;
        fixture.Chat.Respond = async (_, token) =>
        {
            bodies.Enqueue(Encoding.UTF8.GetString(fixture.Chat.Body));
            if (bodies.Count == 1)
            {
                // Still thinking when you go on talking: this request is aborted.
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { Interlocked.Increment(ref aborted); throw; }
            }
            return Reply("Sounds good.");
        };
        var board = fixture.Controller.Board;
        board.Post(ContextBoard.Touch, "Touched: a pat on the head.", fixture.Clock.GetLocalNow(), TimeSpan.FromMinutes(5), consume: true);
        var listener = Listen(fixture);
        try
        {
            Say(fixture, 5, voice: false);
            Say(fixture, 25, voice: true);
            Say(fixture, 3, voice: false);
            await fixture.Advance(() => fixture.Chat.Calls == 1 && fixture.Controller.EarlyReply is not null);
            // It holds the live floor from its start, like any reply (the quick transcript's real words made it Live already).
            var held = fixture.Controller.EarlyReply!;
            Assert.Equal(LiveFloorLevel.Live, fixture.Controller.LiveFloor.Level);
            Assert.Equal(1, fixture.Controller.LiveFloor.Replies);
            Assert.Equal("a reply started early", held.FloorReply?.Why);
            // You go on talking: the reply started early goes at once, and its request is aborted.
            words.LongerThan = 110_000;
            Say(fixture, 10, voice: true);
            await fixture.Advance(() => fixture.Controller.EarlyReplies.Count == 1 && Volatile.Read(ref aborted) == 1 && !fixture.Runner.IsRunning);
            var cancelled = fixture.Controller.EarlyReplies[0];
            Assert.Equal(EarlyReplyRecord.Cancelled, cancelled.Outcome);
            Assert.Equal("you went on talking", cancelled.Reason);
            Assert.Equal(0, fixture.Controller.ContextTurns);
            // Let go, it no longer holds the live floor (the floor's grace and your voice still do).
            Assert.True(held.FloorReply!.Ended);
            Assert.Equal(0, fixture.Controller.LiveFloor.Replies);
            // The note it carried was never consumed: it goes with the next request too.
            Assert.Contains(board.Snapshot(fixture.Clock.GetLocalNow()).Notes, note => note.Source == ContextBoard.Touch);

            // The next pause starts another with the longer words, which the end of the turn promotes.
            Say(fixture, 3, voice: false);
            await fixture.Advance(() => fixture.Chat.Calls == 2 && fixture.Controller.EarlyReply is { Turn.Snapshot.TextComplete: true });
            Say(fixture, 14, voice: false);
            var heard = await Heard(fixture, listener);
            Assert.Equal(Long, heard.Text);
            var reply = Answer(fixture, heard);
            await fixture.Finish(reply);
            Assert.Equal("Sounds good.", reply.Turn!.Content.Text);
            Assert.Equal(2, fixture.Chat.Calls);
            var sent = bodies.ToArray();
            Assert.Contains(Long, sent[1]);
            Assert.Contains("Touched: a pat on the head.", sent[1]);
            // Both requests start the same (instructions and history): only the message differs, so the prompt cache holds.
            Assert.Equal(Messages(sent[0])[..^1], Messages(sent[1])[..^1]);
            Assert.Equal([EarlyReplyRecord.Cancelled, EarlyReplyRecord.Promoted], fixture.Controller.EarlyReplies.Select(r => r.Outcome));
            Assert.Equal(2, fixture.Controller.EarlyReplies[1].Start);
            Assert.Equal(1, fixture.Controller.ContextTurns);
            Assert.DoesNotContain(board.Snapshot(fixture.Clock.GetLocalNow()).Notes, note => note.Source == ContextBoard.Touch);
            var line = ReplyLatency.Describe(reply.LatencyTimeline, reply.ReplyStartedAt, fixture.Clock, reply.Turn.Snapshot, null)!;
            Assert.Contains("promoted (started early 2 times, 1 cancelled).", line);
        }
        finally { fixture.Controller.StopListening(listener); }
    }

    [Fact]
    public async Task A_reply_started_early_for_other_words_is_let_go_and_the_reply_starts_with_the_final_words()
    {
        var (fixture, _) = await Create();
        await using var __ = fixture;
        var bodies = new ConcurrentQueue<string>();
        fixture.Chat.Respond = (_, _) =>
        {
            bodies.Enqueue(Encoding.UTF8.GetString(fixture.Chat.Body));
            return Task.FromResult(Reply("Okay."));
        };
        var listener = Listen(fixture);
        try
        {
            Say(fixture, 5, voice: false);
            Say(fixture, 25, voice: true);
            Say(fixture, 3, voice: false);
            await fixture.Advance(() => fixture.Chat.Calls == 1 && fixture.Controller.EarlyReply is { Turn.Snapshot.TextComplete: true });
            var early = fixture.Controller.EarlyReply!;
            Say(fixture, 14, voice: false);
            var heard = await Heard(fixture, listener);
            // The talk window asks with other words (say, more of what you said joined to it): the one started early goes, and
            // the reply starts once it has left the app slot (the window asks again then).
            const string final = "So I was thinking we could go out. What do you think?";
            LiveConversationOperation? reply = null;
            try { reply = Answer(fixture, heard, final); }
            catch (LiveActionException error) when (error.Code == "conversation.ownership_busy") { }
            Assert.Equal(EarlyReplyRecord.Changed, fixture.Controller.EarlyReplies[^1].Outcome);
            Assert.Equal("what you said changed", fixture.Controller.EarlyReplies[^1].Reason);
            await fixture.Finish(early);
            Assert.Equal(0, fixture.Controller.ContextTurns);
            reply ??= Answer(fixture, heard, final);
            Assert.NotSame(early, reply);
            await fixture.Finish(reply);
            Assert.Equal(2, fixture.Chat.Calls);
            Assert.Contains(final, bodies.ToArray()[1]);
            Assert.Equal(1, fixture.Controller.ContextTurns);
            var line = ReplyLatency.Describe(reply.LatencyTimeline, reply.ReplyStartedAt, fixture.Clock, reply.Turn!.Snapshot, null)!;
            Assert.Contains("Started early 1 time, 1 cancelled.", line);
        }
        finally { fixture.Controller.StopListening(listener); }
    }

    [Fact]
    public async Task With_a_cloud_Thinking_model_replies_start_early_only_when_also_for_cloud_models_is_on()
    {
        var (fixture, _) = await Create(cloud: true);
        await using var _ = fixture;
        fixture.Answer("Cloud reply.");
        var configured = fixture.Controller.Configuration!;
        var (thinking, voice, why) = LiveConversationController.EarlyAllowed(new EarlyReplyOptions(), configured);
        Assert.False(thinking);
        Assert.False(voice);
        Assert.Contains("Also for cloud models", why);
        // With Also for cloud models on, the reply starts early and its paid cloud voice is prepared early too.
        Assert.Equal((true, true), LiveConversationController.EarlyAllowed(new EarlyReplyOptions { Cloud = true }, configured) is var allowed
            ? (allowed.Thinking, allowed.Voice) : default);

        var listener = Listen(fixture);
        try
        {
            Say(fixture, 5, voice: false);
            Say(fixture, 25, voice: true);
            Say(fixture, 6, voice: false);
            await fixture.Pass(TimeSpan.FromMilliseconds(600));
            Assert.Equal(0, fixture.Llm.Calls);
            Assert.Null(fixture.Controller.EarlyReply);
            Assert.Empty(fixture.Controller.EarlyReplies);
        }
        finally { fixture.Controller.StopListening(listener); }
        await fixture.Advance(() => !listener.Running);

        var cloudListener = Listen(fixture, new EarlyReplyOptions { Cloud = true });
        try
        {
            Say(fixture, 5, voice: false);
            Say(fixture, 25, voice: true);
            Say(fixture, 3, voice: false);
            await fixture.Advance(() => fixture.Llm.Calls == 1 && fixture.Controller.EarlyReply is not null);
        }
        finally { fixture.Controller.StopListening(cloudListener); }
        await fixture.Advance(() => fixture.Controller.EarlyReplies.Count == 1);
        Assert.Equal(EarlyReplyRecord.Cancelled, fixture.Controller.EarlyReplies[0].Outcome);
        Assert.Equal("listening stopped", fixture.Controller.EarlyReplies[0].Reason);
    }

    [Fact]
    public async Task Without_a_judge_a_cloud_Thinking_model_gets_no_quick_transcripts_unless_its_replies_may_start_early()
    {
        var (fixture, words) = await Create(cloud: true, judge: false);
        await using var _ = fixture;
        fixture.Answer("Cloud reply.");
        // You say something with a short pause in it (400 ms, which the plain 800 ms rule doesn't end the turn at), then stop.
        async Task<int> Transcripts(EarlyReplyOptions options)
        {
            var before = Volatile.Read(ref words.Calls);
            var listener = Listen(fixture, options);
            try
            {
                Say(fixture, 5, voice: false);
                Say(fixture, 20, voice: true);
                Say(fixture, 4, voice: false);
                Say(fixture, 10, voice: true);
                Say(fixture, 10, voice: false);
                await Heard(fixture, listener);
            }
            finally { fixture.Controller.StopListening(listener); }
            await fixture.Advance(() => !listener.Running && !fixture.Runner.IsRunning);
            return Volatile.Read(ref words.Calls) - before;
        }
        // Its replies can't start early (Also for cloud models is off): only the turn's own transcript is made.
        Assert.Equal(1, await Transcripts(new EarlyReplyOptions()));
        Assert.Empty(fixture.Controller.EarlyReplies);
        // With it on, each pause gets a quick transcript for a reply to start on (the last is the turn's own).
        Assert.Equal(2, await Transcripts(new EarlyReplyOptions { Cloud = true }));
        Assert.NotEmpty(fixture.Controller.EarlyReplies);
    }

    [Fact]
    public async Task A_reply_started_early_asks_the_Thinking_fallback_only_once_it_is_taken()
    {
        var (fixture, _) = await Create();
        await using var _ = fixture;
        // If Thinking fails: a cloud endpoint (FIXTURE), which charges for every request it gets.
        var settings = (await fixture.Store.LoadAsync()).Settings!;
        await fixture.Save(settings with
        {
            ThinkingFallback = new() { Origin = "https://openrouter.ai/api/v1", ModelId = "fixture/cloud-model", ConfigurationRevision = Guid.NewGuid() }
        });
        Assert.NotNull(fixture.Controller.Configuration!.TextFallback());
        var hosts = new ConcurrentQueue<string>();
        fixture.Chat.Respond = (request, _) =>
        {
            hosts.Enqueue(request.RequestUri!.Host);
            // Thinking on this PC fails before it answers; the fallback answers.
            return Task.FromResult(request.RequestUri.Host == "127.0.0.1" ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : Reply("From the fallback."));
        };
        var listener = Listen(fixture);
        try
        {
            Say(fixture, 5, voice: false);
            Say(fixture, 25, voice: true);
            Say(fixture, 3, voice: false);
            // The reply starts early and Thinking on this PC fails: while it is held, the fallback isn't asked.
            await fixture.Advance(() => fixture.Controller.EarlyReply is { Turn.Snapshot.FellBack: true });
            var early = fixture.Controller.EarlyReply!;
            await fixture.Pass(TimeSpan.FromMilliseconds(300));
            Assert.Equal(["127.0.0.1"], hosts);
            // The turn ends with the same words: taken as the reply, it asks the fallback now.
            Say(fixture, 14, voice: false);
            var heard = await Heard(fixture, listener);
            var reply = Answer(fixture, heard);
            Assert.Same(early, reply);
            await fixture.Finish(reply);
            Assert.Equal("From the fallback.", reply.Turn!.Content.Text);
            Assert.Equal(["127.0.0.1", "openrouter.ai"], hosts);
        }
        finally { fixture.Controller.StopListening(listener); }
    }

    [Fact]
    public void Starting_replies_early_is_on_by_default_with_cloud_models_off_and_stays_as_chosen()
    {
        var defaults = new TalkPreferences();
        Assert.True(defaults.EarlyReplies);
        Assert.False(defaults.EarlyRepliesCloud);
        Assert.True(defaults.EarlyVoice);
        Assert.Equal(new EarlyReplyOptions(), defaults.EarlyReplyOptions);
        var directory = Directory.CreateTempSubdirectory("martlet-early-");
        try
        {
            // A file saved before the choice existed has it on, without cloud models.
            File.WriteAllText(Path.Combine(directory.FullName, "talk-preferences.json"), JsonSerializer.Serialize(new { HandsFree = true, Version = 4 }));
            var loaded = TalkPreferences.Load(directory.FullName);
            Assert.True(loaded.EarlyReplies);
            Assert.False(loaded.EarlyRepliesCloud);
            Assert.True((defaults with { EarlyReplies = false, EarlyRepliesCloud = true, EarlyVoice = false }).Save(directory.FullName));
            var saved = TalkPreferences.Load(directory.FullName);
            Assert.Equal(new EarlyReplyOptions { Enabled = false, Cloud = true, Voice = false }, saved.EarlyReplyOptions);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public void Companion_says_whether_replies_start_early_and_what_became_of_them()
    {
        Assert.Equal("Off. Martlet starts each reply once you finished talking.",
            MainWindow.EarlyRepliesText(new TalkPreferences(EarlyReplies: false), null, []));
        Assert.Equal("On. No replies started early yet.", MainWindow.EarlyRepliesText(new TalkPreferences(), null, []));
        var at = DateTimeOffset.UnixEpoch;
        EarlyReplyRecord[] records =
        [
            new(at, EarlyReplyRecord.Cancelled, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(200), 1, "you went on talking"),
            new(at, EarlyReplyRecord.Promoted, TimeSpan.FromMilliseconds(280), TimeSpan.FromMilliseconds(1300), 2),
            new(at, EarlyReplyRecord.Changed, TimeSpan.FromMilliseconds(290), TimeSpan.FromMilliseconds(900), 1, "what you said changed")
        ];
        Assert.Equal("On. Last 3: 1 taken as the reply, 1 let go because you went on talking, 1 let go for another reason. " +
            "Last: changed after 900 ms.", MainWindow.EarlyRepliesText(new TalkPreferences(), null, records));
    }
}
