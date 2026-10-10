using Martlet.Providers;
using Martlet.Providers.Tests;

namespace Martlet.Conversation.Tests;

/// <summary>The Thinking trace (<see cref="ThinkingTrace"/>): each turn's start, requests, tool rounds, waits and end, and each
/// Thinking pool request's way through the line, as the desktop log gets them.</summary>
public sealed class ThinkingTraceTests
{
    private sealed class Lines
    {
        private readonly List<string> lines = [];
        internal void Add(string line)
        {
            lock (lines) lines.Add(line);
        }
        internal string[] All { get { lock (lines) return [.. lines]; } }
        internal string Single(string contains) => Assert.Single(All, line => line.Contains(contains, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_turn_says_where_it_asked_each_step_of_its_request_and_how_it_ended()
    {
        await using var harness = new Harness(textOnly: true);
        var lines = new Lines();
        harness.Runtime.Trace = lines.Add;
        harness.Answer("Hello ", "there.");
        var turn = harness.Runtime.Start(Harness.Request(speech: false), harness.Permissions, purpose: "trace check");
        var snapshot = await Harness.Finish(turn, harness.Clock);
        await Harness.Until(() => lines.All.Any(line => line.Contains(" started: ", StringComparison.Ordinal)));

        Assert.Equal(ConversationState.Completed, snapshot.State);
        var name = turn.TraceName!;
        Assert.Matches(@"^Thinking turn \d+ \(trace check\)$", name);
        Assert.All(lines.All, line => Assert.StartsWith(name, line, StringComparison.Ordinal));
        var started = lines.Single(" started: ");
        Assert.Contains($" started: OpenAI Responses, model {TextFixtures.Selection.UpstreamModelId}; input of about ", started);
        Assert.Contains("with 0 earlier messages; text only; at most 256 output tokens; first words within 15 s, no words for at most 10 s, " +
            "each request within 60 s, the turn within 90 s.", started);
        var request = lines.Single(": request 1 (first) answered at ");
        Assert.Contains("authorized at ", request);
        Assert.Contains(" ms, sent at ", request);
        Assert.Contains(" ms, response at ", request);
        Assert.Contains(" ms, first words at ", request);
        Assert.Contains(", 12 characters", request);
        var ended = lines.Single(" ended at ");
        Assert.Contains(" ms: Completed; 1 request, first words at ", ended);
        Assert.EndsWith(", 12 characters.", ended);
        Assert.DoesNotContain(lines.All, line => line.Contains("Hello", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_tool_round_says_each_call_and_the_next_request_says_why_it_was_sent()
    {
        await using var harness = new Harness(textOnly: true);
        var lines = new Lines();
        harness.Runtime.Trace = lines.Add;
        harness.Llm.Respond = (_, _) => Task.FromResult(TextRecordingHandler.Sse(
            harness.Llm.Calls == 1 ? ToolLoopTests.CallTrace() : Harness.Trace("You need ", "milk.")));
        var turn = harness.Runtime.Start(ToolLoopTests.Request(new ToolLoopTests.FixtureTools("Buy milk.")), harness.Permissions);
        var snapshot = await Harness.Finish(turn, harness.Clock);

        Assert.Equal(ConversationState.Completed, snapshot.State);
        Assert.Contains("(Thinking)", turn.TraceName);
        Assert.Contains(": request 1 (first) answered with 1 tool call at ", lines.Single(": request 1 "));
        Assert.Matches(@": tool round 1: read_file \d+ ms; \d+ ms in all\.$", lines.Single(": tool round 1: "));
        Assert.Contains(": request 2 (after tool round 1) answered at ", lines.Single(": request 2 "));
        Assert.Contains(" ms: Completed; 2 requests, 1 tool call, ", lines.Single(" ended at "));
    }

    [Fact]
    public async Task A_slow_answer_says_what_the_turn_waits_for_while_it_waits()
    {
        await using var harness = new Harness(textOnly: true);
        var lines = new Lines();
        harness.Runtime.Trace = lines.Add;
        var gate = new TaskCompletionSource();
        harness.Llm.Respond = async (_, token) =>
        {
            await gate.Task.WaitAsync(token);
            return TextRecordingHandler.Sse(Harness.Trace("Sorry, ", "I was slow."));
        };
        var turn = harness.Runtime.Start(Harness.Request(speech: false), harness.Permissions, purpose: "slow");
        await Harness.Until(() => harness.Llm.Calls == 1);
        harness.Clock.Advance(TimeSpan.FromMilliseconds(2_100));
        await Harness.Until(() => lines.All.Any(line => line.Contains(": still waiting ", StringComparison.Ordinal)));

        var waiting = lines.Single(": still waiting ");
        Assert.Matches(@": still waiting at \d+ ms for request 1's answer \(\d+ ms so far; sent at \d+ ms, no response yet\)\.$", waiting);
        // Said once until the wait reaches its next notice (5 s).
        harness.Clock.Advance(TimeSpan.FromMilliseconds(1_000));
        await Task.Delay(50);
        Assert.Single(lines.All, line => line.Contains(": still waiting ", StringComparison.Ordinal));
        gate.SetResult();
        var snapshot = await Harness.Finish(turn, harness.Clock);
        Assert.Equal(ConversationState.Completed, snapshot.State);
        Assert.Contains(": request 1 (first) answered at ", lines.Single(": request 1 "));
    }

    [Fact]
    public async Task A_full_voice_queue_is_said_as_a_wait_for_the_voice_never_as_the_models_silence()
    {
        await using var harness = new Harness();
        var lines = new Lines();
        harness.Runtime.Trace = lines.Add;
        harness.Answer("The first sentence is here. ", "The second sentence is here. ", "The third sentence is here. ",
            "The fourth sentence is here. ", "The fifth sentence is here. ", "The sixth sentence is here. ", "The seventh one ends it.");
        // Started early and not taken: only its first piece is made and nothing plays, so the voice's queue fills up.
        var turn = harness.Runtime.StartEarly(Harness.Request(), harness.Permissions, prepareVoice: true, purpose: "held");
        await Harness.Until(() => turn.Snapshot.Timings?.FirstSpeechAudioAfter is not null, harness.Clock);
        for (var i = 0; i < 300 && !lines.All.Any(line => line.Contains(": still waiting ", StringComparison.Ordinal)); i++)
        {
            harness.Clock.Advance(TimeSpan.FromMilliseconds(10));
            await Task.Delay(1);
        }

        var waiting = lines.Single(": still waiting ");
        Assert.Matches(@": still waiting at \d+ ms for the voice to take the next piece \(\d+ ms so far; request 1 has \d+ characters so far; " +
            @"the reply started early and isn't taken yet\)\.$", waiting);
        Assert.DoesNotContain(lines.All, line => line.Contains("no new words", StringComparison.Ordinal));
        Assert.True(turn.Release());
        var snapshot = await Harness.Finish(turn, harness.Clock);
        Assert.Equal(ConversationState.Completed, snapshot.State);
        Assert.Contains("started early, taken as the reply at ", lines.Single(" ended at "));
    }

    [Fact]
    public async Task Nothing_is_traced_without_a_listener()
    {
        await using var harness = new Harness(textOnly: true);
        harness.Answer("Hi.");
        // Another test's listener may be on; this runtime's own sink says where its lines go.
        harness.Runtime.Trace = null;
        var turn = harness.Runtime.Start(Harness.Request(speech: false), harness.Permissions);
        await Harness.Finish(turn, harness.Clock);
        Assert.Equal(ThinkingTrace.Enabled, turn.TraceName is not null);

        var heard = new Lines();
        using (ThinkingTrace.Listen(heard.Add))
        {
            harness.Answer("Hi again.");
            var traced = harness.Runtime.Start(Harness.Request(speech: false), harness.Permissions, purpose: "listened");
            await Harness.Finish(traced, harness.Clock);
            Assert.NotNull(traced.TraceName);
            await Harness.Until(() => heard.All.Any(line => line.StartsWith(traced.TraceName + " started: ", StringComparison.Ordinal)));
            Assert.Contains(heard.All, line => line.StartsWith(traced.TraceName + " ended at ", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_pool_request_says_when_it_waits_in_line_starts_lets_go_and_ends()
    {
        var clock = new RuntimeClock();
        var places = new BackgroundPlaces(clock);
        var lines = new Lines();
        places.Requests.Trace = lines.Add;
        BackgroundPlace busy = new("host:busy", "busy") { Model = "fixture-a" }, ok = new("host:ok", "ok", Rank: 1);
        var board = new ThinkingJobBoard(places, () => [busy, ok], (member, _, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            return Task.FromResult(member.Id == "host:busy" ? ThinkingAnswer.Failed("busy came back empty") : ThinkingAnswer.Done("abc"));
        });
        var result = await board.RunAsync(new ThinkingJob
        {
            Kind = ThinkingJobKind.Memory, Instructions = "Answer in one word.", Text = "fixture", Timeout = TimeSpan.FromSeconds(10),
            Label = "remembering a fixture"
        }, CancellationToken.None);

        Assert.True(result.Succeeded);
        var id = Assert.Single(places.Requests.List()).Id;
        var all = lines.All;
        Assert.All(all, line => Assert.StartsWith($"Thinking request {id} (memory, memory-", line, StringComparison.Ordinal));
        Assert.Contains("): waits in line: a pool job (remembering a fixture), priority ", all[0]);
        Assert.EndsWith("): started on busy (fixture-a) after 0 ms in line.", all[1]);
        Assert.EndsWith("): let go of busy after 1000 ms: busy came back empty; it waits in line again.", all[2]);
        Assert.EndsWith("): started on ok after 0 ms waiting again (try 2).", all[3]);
        Assert.EndsWith("): Succeeded after 2000 ms: waited 0 ms, ran 2000 ms, 2 tries (last on ok), an answer of 3 characters.", all[4]);
        Assert.Equal(5, all.Length);
    }
}
