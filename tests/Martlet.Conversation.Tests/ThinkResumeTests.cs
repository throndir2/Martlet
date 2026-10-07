using System.Text;
using Martlet.Core.Settings;
using Martlet.Providers;
using Martlet.Providers.Tests;

namespace Martlet.Conversation.Tests;

/// <summary>A think the live floor stops keeps what it wrote and goes on later (docs/CONVERSATION.md, Live floor).</summary>
public sealed class ThinkResumeTests
{
    [Fact]
    public void A_think_going_on_in_place_sends_what_it_wrote_as_the_assistants_unfinished_message()
    {
        var input = ThinkLonger.Input(null, null, "Write a poem about rain.", null, null, "Persona", new ThinkResume("The rain falls", true));
        Assert.Equal("The rain falls", input.Continuation);
        Assert.DoesNotContain("You started on this before", input.UserText);
        // Fitting it to a smaller destination keeps the unfinished message.
        var fitted = ThinkLonger.Fit(input, new ThinkBounds(BoundedTextInput.HardMaxUtf8Bytes, 16, 4_000, false));
        Assert.Equal("The rain falls", fitted.Continuation);
        Assert.Null(ThinkLonger.Input(null, null, "Write a poem about rain.", null, null, "Persona").Continuation);
    }

    [Fact]
    public void A_think_starting_again_gets_what_it_wrote_as_context()
    {
        var input = ThinkLonger.Input(null, null, "Write a poem about rain.", null, null, "Persona", new ThinkResume("The rain falls", false));
        Assert.Null(input.Continuation);
        Assert.Contains("You started on this before and were stopped partway", input.UserText);
        Assert.Contains("The rain falls", input.UserText);
        Assert.Contains("Write a poem about rain.", input.UserText);
        var long_ = ThinkLonger.ResumeNote(new string('x', ThinkLonger.MaxResumeBytes * 2));
        Assert.Contains("(its start)", long_);
        Assert.True(Encoding.UTF8.GetByteCount(long_) < ThinkLonger.MaxResumeBytes + 400);
    }

    [Fact]
    public void Going_on_in_place_joins_the_two_parts_and_starting_again_keeps_only_the_new_result()
    {
        Assert.Equal("First half. Second half.", new ThinkResume("First half. ", true).Combine(BackgroundJobOutcome.Done("Second half.")).Result);
        Assert.Equal("First half. Second half.", new ThinkResume("First half.", true).Combine(BackgroundJobOutcome.Done("Second half.")).Result);
        Assert.Equal("The whole thing.", new ThinkResume("First half. ", false).Combine(BackgroundJobOutcome.Done("The whole thing.")).Result);
        Assert.Equal("failed", new ThinkResume("First half. ", true).Combine(BackgroundJobOutcome.Failed("failed")).Problem);
        // The spacing at the stop may be lost: a space goes back after a sentence's end; a word cut in two joins as it came.
        Assert.Equal("The rain falls", ThinkResume.Join("The rain", " falls"));
        Assert.Equal("Done. Next", ThinkResume.Join("Done.", "Next"));
        Assert.Equal("understanding", ThinkResume.Join("under", "standing"));
        Assert.Equal("3.14", ThinkResume.Join("3.", "14"));
    }

    [Fact]
    public void Only_a_server_that_continues_an_unfinished_message_goes_on_in_place()
    {
        Assert.True(ThinkLonger.ContinuesInPlace(new() { Place = DeepThinkingPlace.Endpoint, Origin = "http://127.0.0.1:11434/v1", ModelId = "qwen3:8b" }, null));
        Assert.False(ThinkLonger.ContinuesInPlace(new() { Place = DeepThinkingPlace.Endpoint, Origin = "https://openrouter.ai/api/v1", ModelId = "x" }, null));
        Assert.False(ThinkLonger.ContinuesInPlace(new() { Place = DeepThinkingPlace.Host, HostId = "diva", ModelId = "qwen3:8b" }, null));
        var thinking = new SetupRoute
        {
            RouteType = SetupRouteType.ChatCompletions, Role = SetupRole.Llm, ProviderAlias = "chat-completions",
            Origin = "http://192.168.1.20:11434/v1", ModelId = "gemma4", ConfigurationRevision = Guid.NewGuid()
        };
        Assert.True(ThinkLonger.ContinuesInPlace(new(), thinking));
    }

    [Fact]
    public void A_request_that_goes_on_in_place_doesnt_think_first()
    {
        var on = ThinkLonger.Generation(null, ThinkEffort.High, withoutReasoning: false)!;
        Assert.True(on.Reasoning);
        Assert.Equal(GenerationSupport.ReasoningEffortHigh, on.ReasoningEffort);
        var continuing = ThinkLonger.Generation(null, ThinkEffort.High, withoutReasoning: false, continuing: true)!;
        Assert.False(continuing.Reasoning);
        Assert.Null(continuing.ReasoningEffort);
    }

    // A response stream that sends its first part, then works on (sends nothing more) until the request is stopped.
    private static string FirstPart(string text) =>
        string.Concat(TextFixtures.Trace(text).Take(4)) + TextFixtures.Event("response.output_text.delta", 4,
            new { output_index = 0, content_index = 0, item_id = "msg_fixture", delta = text });

    [Fact]
    public async Task A_think_the_live_floor_stops_keeps_what_it_wrote_and_goes_on_from_there()
    {
        await using var harness = new Harness(textOnly: true);
        var clock = harness.Clock;
        using var floor = new LiveFloor(clock);
        using var jobs = new BackgroundJobs(clock);
        new LiveFloorRules(floor, new([new("thinking", LiveResources.ThisPc, [])])).Attach(jobs.Places);
        var place = new BackgroundPlace("endpoint:local", "this PC") { Machine = LiveResources.ThisPc, Slots = 2 };
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        harness.Llm.Respond = (_, _) =>
        {
            if (Interlocked.Increment(ref calls) > 1) return Task.FromResult(TextRecordingHandler.Sse(Harness.Trace("Second half.")));
            var bytes = Encoding.UTF8.GetBytes(FirstPart("First half. "));
            var body = new FragmentedTextBody(bytes, 4096) { IgnoreCancellation = false };
            body.BeforeRead = async token =>
            {
                if (body.BytesRead < bytes.Length) return;
                stalled.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            };
            return Task.FromResult(TextRecordingHandler.Sse(body));
        };
        List<ThinkResume?> resumes = [];
        List<string> stops = [];
        var start = jobs.Start(ThinkLonger.Kind(new()), "a long task", (job, token) => YieldingThink.RunAsync(jobs, job, (at, resume) =>
        {
            lock (resumes) resumes.Add(resume);
            return new BackgroundThink(harness.Runtime, _ => (Harness.Request(speech: false), harness.Permissions), clock);
        }, _ => true, token, (at, kept) => { lock (stops) stops.Add($"{at.Name}: {kept?.Partial}"); }), [place], wait: true);
        var job = start.Job!;
        await stalled.Task.WaitAsync(TimeSpan.FromSeconds(20));
        // Its first words reached the turn before the stream stalled.
        await Task.Delay(200);
        var reply = floor.BeginReply();
        await Harness.Until(() => job.State == BackgroundJobState.Paused, clock);
        Assert.Equal(BackgroundJob.WaitingForConversation, job.Progress);
        // The turn's text keeps what was shown, so the space at the stop is gone; the join puts it back.
        Assert.Equal(["this PC: First half."], stops);
        reply.End();
        floor.Clear();
        await Harness.Until(() => job.Finished, clock);
        Assert.Equal(BackgroundJobState.Succeeded, job.State);
        Assert.Equal("First half. Second half.", job.Result);
        Assert.Equal(2, resumes.Count);
        Assert.Null(resumes[0]);
        Assert.Equal(new ThinkResume("First half.", true), resumes[1]);
        Assert.Equal(1, job.Preemptions);
    }
}
