using System.Net;
using System.Text;
using System.Text.Json;
using Martlet.Audio;
using Martlet.Audio.Tests;
using Martlet.Core.Contracts;
using Martlet.Providers;
using Martlet.Providers.Tests;

namespace Martlet.Conversation.Tests;

public sealed class IntegrationTests
{
    [Fact]
    public async Task Production_construction_idle_and_disposal_are_passive()
    {
        var credentials = new FixtureCredentials();
        var devices = new ControlledDevice();
        await using (var runtime = ConversationRuntime.Create(credentials, devices))
        {
            Assert.Equal(ConversationState.Idle, runtime.State);
            Assert.Equal(0, runtime.CurrentEpoch);
            Assert.Null(await runtime.StopAsync());
            Assert.Equal(0, credentials.Calls);
            Assert.Equal(0, devices.Opens);
        }
        Assert.Equal(0, credentials.Calls);
        Assert.Equal(0, devices.Opens);
    }

    [Fact]
    public async Task Real_adapters_validator_segmenter_and_sink_preserve_text_pcm_and_order()
    {
        await using var h = new Harness(new ControlledDevice { MaximumWriteSamples = 137 });
        h.Answer("First ", "sentence.", " Second ", "sentence!", " Final fragment");
        var turn = h.Start();
        var result = await Harness.Finish(turn);
        Assert.Equal(ConversationState.Completed, result.State);
        Assert.Equal("First sentence. Second sentence! Final fragment", turn.Content.Text);
        Assert.True(result.TextComplete);
        Assert.True(result.OwnershipReleased);
        Assert.Equal(EvidenceProvenance.Fixture, result.TextProvenance);
        Assert.Equal(EvidenceProvenance.Fixture, result.SpeechProvenance);
        Assert.Equal(1, h.Llm.Calls);
        Assert.Equal(3, h.Tts.Calls);
        Assert.Equal(3, h.Device.Opens);
        Assert.Equal(3, h.Device.Disposals);
        var actions = h.Permissions.SpeechActions.ToArray();
        Assert.Equal(new[] { "First sentence.", "Second sentence!", "Final fragment" }, actions.Select(x => x.Input.Text));
        Assert.Equal(new[] { 1, 2, 3 }, actions.Select(x => x.Segment));
        Assert.Equal(4, actions.Select(x => x.Context.Ids.RequestId).Append(turn.TextIds.RequestId).Distinct().Count());
        Assert.All(actions, a =>
        {
            Assert.Equal(turn.SessionId, a.Context.Ids.SessionId);
            Assert.Equal(turn.TurnId, a.Context.Ids.TurnId);
            Assert.Equal(turn.Epoch, a.Context.Epoch);
            Assert.Equal(ProviderRole.Tts, a.Budget.Role);
            Assert.Equal(1, a.Budget.Requests);
        });
        var expected = Enumerable.Range(0, 3).SelectMany(_ => SpeechFixtures.Audio()).ToArray();
        Assert.Equal(expected, h.Device.Bytes);
        Assert.Equal(3003, result.AcceptedSamples);
        Assert.Equal(3003, result.SubmittedSamples);
        Assert.Equal(3003, result.DeviceConsumedSamples);
        Assert.True(result.Playback!.DeviceDrainObserved);
        Assert.Null(result.AudibleSamples);
        Assert.Null(result.EstimatedCost);
        Assert.True(result.MayHavePlayed);
        Assert.InRange(result.PeakQueuedSegments, 1, 2);
        Assert.Equal(0, result.QueuedSegments);
        var events = new List<ConversationEvent>();
        while (turn.Events.TryRead(out var item)) events.Add(item);
        Assert.Equal(turn.Content.Text, string.Concat(events.Select(x => x.Text)));
        Assert.Equal(events.Select(x => x.Sequence).Order(), events.Select(x => x.Sequence));
        using var llmBody = JsonDocument.Parse(h.Llm.Body);
        Assert.False(llmBody.RootElement.GetProperty("store").GetBoolean());
        Assert.Empty(llmBody.RootElement.GetProperty("tools").EnumerateArray());
        using var speechBody = JsonDocument.Parse(h.Tts.Body);
        Assert.Equal("Final fragment", speechBody.RootElement.GetProperty("input").GetString());
        Assert.Equal("pcm", speechBody.RootElement.GetProperty("response_format").GetString());
    }

    [Fact]
    public async Task Control_tags_the_request_offers_are_listed_never_shown_or_spoken()
    {
        await using var h = new Harness();
        h.Answer("Sure, I'll keep ", "it down. [chatti", "ness: quiet]");
        var request = Harness.Request();
        var turn = h.Start(new ConversationRequest(request.Input, request.Model, request.TextLimits, request.Limits, request.Speech,
            controlTags: Martlet.Core.Settings.ChattinessTags.All));
        var result = await Harness.Finish(turn);
        Assert.Equal(ConversationState.Completed, result.State);
        Assert.Equal("Sure, I'll keep it down.", turn.Content.Text);
        Assert.Equal(["[chattiness: quiet]"], turn.Controls);
        Assert.Equal(["Sure, I'll keep it down."], h.Permissions.SpeechActions.Select(action => action.Input.Text));
        var events = new List<ConversationEvent>();
        while (turn.Events.TryRead(out var item)) events.Add(item);
        Assert.DoesNotContain(events, e => e.Text?.Contains("chattiness", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task A_character_tag_in_the_wrong_brackets_still_acts_and_the_rest_of_its_line_is_still_spoken()
    {
        await using var h = new Harness();
        h.Answer("Oh, look at all that activity! [n", "od] What are you working on right now?");
        var request = Harness.Request();
        var turn = h.Start(new ConversationRequest(request.Input, request.Model, request.TextLimits, request.Limits, request.Speech,
            characterTags: ["{nod}", "{blush}"]));
        var result = await Harness.Finish(turn);
        Assert.Equal(ConversationState.Completed, result.State);
        Assert.Equal("Oh, look at all that activity! What are you working on right now?", turn.Content.Text);
        Assert.Equal(["Oh, look at all that activity!", "What are you working on right now?"],
            h.Permissions.SpeechActions.Select(action => action.Input.Text));
        var acted = Assert.Single(turn.Acted);
        Assert.Equal(new ReplyTag("{nod}", Martlet.Core.Settings.VoiceTagKind.Character, "nod", "[nod]"), acted);
        Assert.Equal("Emote: nod.", ReplyTag.Note(turn.Acted));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Text_only_never_authorizes_or_generates_audio_even_if_factory_exists(bool composeDevice)
    {
        await using var h = new Harness(textOnly: !composeDevice);
        h.Answer("Plain text response.");
        var turn = h.Start(Harness.Request(speech: false));
        var result = await Harness.Finish(turn);
        Assert.Equal(ConversationState.Completed, result.State);
        Assert.Equal("Plain text response.", turn.Content.Text);
        Assert.Empty(h.Permissions.SpeechActions);
        Assert.Equal(0, h.Tts.Calls);
        Assert.Equal(0, h.Device.Opens);
        Assert.Equal(1, h.Credentials.Calls);
        Assert.Null(result.SpeechProvenance);
        Assert.Null(result.Playback);
    }

    [Fact]
    public async Task Refusal_is_separate_not_completed_and_never_spoken()
    {
        await using var h = new Harness();
        h.Llm.Respond = (_, _) => Task.FromResult(TextRecordingHandler.Sse(string.Concat(TextFixtures.Trace("No fixture permission.", true))));
        var turn = h.Start();
        var result = await Harness.Finish(turn);
        Assert.Equal(ConversationState.Refused, result.State);
        Assert.False(result.TextComplete);
        Assert.Equal("", turn.Content.Text);
        Assert.Equal("No fixture permission.", turn.Content.Refusal);
        Assert.Equal(0, h.Tts.Calls);
        Assert.Equal(0, h.Device.Opens);
    }

    [Theory]
    [InlineData("```text\nThis is fenced.\n```")]
    [InlineData("https://example.test/path")]
    [InlineData("{\"tool\":\"untrusted\"}")]
    [InlineData("**This is unsupported Markdown.**")]
    public async Task Unsupported_speech_remains_visible_without_cost_or_device(string text)
    {
        await using var h = new Harness();
        h.Answer(text);
        var turn = h.Start();
        var result = await Harness.Finish(turn);
        Assert.Equal(ConversationState.Completed, result.State);
        Assert.Equal(text, turn.Content.Text);
        Assert.True(result.SuppressedFragments > 0);
        Assert.Equal(0, h.Tts.Calls);
        Assert.Equal(0, h.Device.Opens);
    }

    [Theory]
    [InlineData("eof")]
    [InlineData("duplicate")]
    [InlineData("wrong-item")]
    [InlineData("incomplete")]
    [InlineData("final-replay")]
    public async Task Malformed_or_incomplete_llm_is_partial_not_replayed(string fault)
    {
        await using var h = new Harness();
        var trace = TextFixtures.Trace("An unfinished fixture fragment");
        string body = fault switch
        {
            "eof" => string.Concat(trace.Take(5)),
            "duplicate" => string.Concat(trace.Take(5).Append(trace[4]).Concat(trace.Skip(5))),
            "wrong-item" => string.Concat(trace.Take(5)) + trace[5].Replace("msg_fixture", "foreign"),
            "incomplete" => string.Concat(trace.Take(5)) + TextFixtures.Event("response.incomplete", 5,
                new { response = TextFixtures.Response("An unfinished fixture fragment", "incomplete") }),
            _ => string.Concat(trace.Take(8)) + trace[8].Replace("An unfinished fixture fragment", "Different final response")
        };
        h.Llm.Respond = (_, _) => Task.FromResult(TextRecordingHandler.Sse(body));
        var turn = h.Start();
        var result = await Harness.Finish(turn);
        Assert.Equal(ConversationState.Partial, result.State);
        Assert.False(result.TextComplete);
        Assert.Equal("An unfinished fixture fragment", turn.Content.Text);
        Assert.Equal(0, h.Tts.Calls);
        Assert.Equal(1, h.Llm.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(961)]
    public async Task Empty_or_odd_pcm_fails_without_claiming_speech_completion(int bytes)
    {
        await using var h = new Harness();
        h.Answer("A complete answer.");
        h.Tts.Respond = (_, _) => Task.FromResult(SpeechFixtures.Pcm(new FragmentedTextBody(SpeechFixtures.Audio()[..bytes])));
        var turn = h.Start();
        var result = await Harness.Finish(turn);
        // The voice failing never ends the reply: it completes with its text, and only the voice stopped.
        Assert.Equal(ConversationState.Completed, result.State);
        Assert.Equal("A complete answer.", turn.Content.Text);
        Assert.True(result.TextComplete);
        Assert.Equal(ConversationFailure.ProviderFailed, result.SpeechFailure);
        Assert.Equal(ProviderRole.Tts, result.FailedProvider);
        Assert.Equal(1, h.Tts.Calls);
        Assert.InRange(result.AcceptedSamples, 0, 480);
        Assert.NotEqual(PlaybackState.Completed, result.Playback?.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(961)]
    public async Task Captions_still_show_what_a_failed_voice_could_not_say(int bytes)
    {
        var captions = new SpokenTextFeed();
        await using var h = new Harness(spokenText: captions);
        h.Answer("[laugh] A complete answer.");
        h.Tts.Respond = (_, _) => Task.FromResult(SpeechFixtures.Pcm(new FragmentedTextBody(SpeechFixtures.Audio()[..bytes])));
        var result = await Harness.Finish(h.Start());
        Assert.Equal(ConversationFailure.ProviderFailed, result.SpeechFailure);
        Assert.True(captions.Lines.TryRead(out var line));
        Assert.Equal("A complete answer.", line!.Text);
        Assert.False(captions.Lines.TryRead(out _));
    }

    [Fact]
    public async Task Captions_show_every_unsaid_sentence_in_order_one_after_another()
    {
        var captions = new SpokenTextFeed();
        await using var h = new Harness(spokenText: captions);
        h.Answer("One. Two. Three.");
        h.Tts.Respond = (_, _) => Task.FromResult(SpeechFixtures.Pcm(new FragmentedTextBody(SpeechFixtures.Audio()[..0])));
        var result = await Harness.Finish(h.Start());
        Assert.Equal(ConversationState.Completed, result.State);
        Assert.Equal(ConversationFailure.ProviderFailed, result.SpeechFailure);
        Assert.Equal(1, h.Tts.Calls);
        var shown = new List<string>();
        while (shown.Count < 3)
        {
            await Harness.Until(() => captions.Lines.TryPeek(out _));
            Assert.True(captions.Lines.TryRead(out var line));
            shown.Add(line!.Text);
            // The next sentence waits until this one has been shown for its reading time.
            await Task.Delay(50);
            Assert.False(line.Finished.IsCompleted);
            Assert.False(captions.Lines.TryPeek(out _));
            h.Clock.Advance(ConversationTurn.ReadingTime(line.Text));
        }
        Assert.Equal(["One.", "Two.", "Three."], shown);
    }

    [Fact]
    public async Task Voice_byte_limit_splits_without_breaking_unicode_in_real_requests()
    {
        await using var h = new Harness();
        string answer = string.Concat(Enumerable.Repeat("\U0001f600", 780)) + ".";
        h.Answer(answer);
        var result = await Harness.Finish(h.Start());
        Assert.Equal(ConversationState.Completed, result.State);
        var actions = h.Permissions.SpeechActions.ToArray();
        Assert.Equal(3, actions.Length);
        Assert.Equal(answer, string.Concat(actions.Select(x => x.Input.Text)));
        Assert.All(actions, a => Assert.InRange(Encoding.UTF8.GetByteCount(a.Input.Text), 1, 1536));
    }

    [Theory]
    [InlineData("segments")]
    [InlineData("bytes")]
    [InlineData("samples")]
    public async Task Aggregate_reservations_stop_additional_paid_segments_but_finish_the_reply(string cap)
    {
        await using var h = new Harness();
        h.Answer("One. Two. Three.");
        var limits = new ConversationLimits
        {
            MaxSpeechSegments = cap == "segments" ? 1 : 8,
            MaxSpeechTextBytes = cap == "bytes" ? 4 : 12_288,
            MaxReservedSpeechSamples = cap == "samples" ? Harness.SpeechLimits.MaxSamples : 2_160_000
        };
        var turn = h.Start(Harness.Request(limits: limits));
        var result = await Harness.Finish(turn);
        Assert.Equal(ConversationState.Completed, result.State);
        Assert.Equal(ConversationFailure.None, result.Failure);
        Assert.True(result.SpeechLimitReached);
        Assert.Equal(1, h.Tts.Calls);
        Assert.Single(h.Permissions.SpeechActions);
        Assert.Equal("One. Two. Three.", turn.Content.Text);
    }

    [Fact]
    public async Task Abandoned_subscriber_drops_bounded_events_but_preserves_content_and_completion()
    {
        await using var h = new Harness(textOnly: true);
        h.Answer(Enumerable.Repeat("x", 300).ToArray());
        var turn = h.Start(Harness.Request(false, new() { EventCapacity = 4 }));
        var result = await Harness.Finish(turn);
        Assert.Equal(ConversationState.Completed, result.State);
        Assert.True(result.DroppedEvents > 0);
        Assert.Equal(new string('x', 300), turn.Content.Text);
        var retained = new List<ConversationEvent>();
        while (turn.Events.TryRead(out var item)) retained.Add(item);
        Assert.Equal(4, retained.Count);
        Assert.Equal(ConversationState.Completed, retained[^1].Snapshot.State);
    }

    [Fact]
    public async Task Content_and_endpoint_never_enter_runtime_diagnostic_metadata()
    {
        await using var h = new Harness();
        h.Answer("private-output-canary");
        h.Tts.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("private-native-body-canary https://secret.invalid/")
        });
        var turn = h.Start(new(new("private-input-canary"), TextFixtures.Selection, new(), new(),
            new(SpeechFixtures.Selection, new(OutputPolicy.FixedEndpoint, "private-endpoint-canary"), Harness.SpeechLimits)));
        var result = await Harness.Finish(turn);
        string metadata = JsonSerializer.Serialize(result) + JsonSerializer.Serialize(turn.Content) + turn.Content;
        while (turn.Events.TryRead(out var item)) metadata += JsonSerializer.Serialize(item) + item;
        Assert.DoesNotContain("private-", metadata);
        Assert.DoesNotContain("https:", metadata);
        Assert.DoesNotContain(ProviderFixtures.Secret, metadata);
        Assert.Equal(ProviderFailureCode.Authentication, result.ProviderFailure);
    }

    [Fact]
    public async Task A_rate_limited_reply_keeps_the_providers_retry_after()
    {
        await using var h = new Harness();
        h.Llm.Respond = (_, _) =>
        {
            var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}") };
            limited.Headers.RetryAfter = new(TimeSpan.FromSeconds(30));
            return Task.FromResult(limited);
        };
        var result = await Harness.Finish(h.Start());
        Assert.Equal((ProviderFailureCode.RateLimited, TimeSpan.FromSeconds(30)), (result.ProviderFailure, result.ProviderRetryAfter));
    }

    [Fact]
    public async Task A_voice_that_fails_on_the_first_sentence_never_cuts_the_rest_of_the_reply()
    {
        await using var h = new Harness();
        h.Answer("First sentence here. ", "Second sentence here. ", "Third and last sentence.");
        h.Tts.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("{}")
        });
        var turn = h.Start();
        var result = await Harness.Finish(turn);
        Assert.Equal(ConversationState.Completed, result.State);
        Assert.Equal(ConversationFailure.None, result.Failure);
        Assert.True(result.TextComplete);
        Assert.Equal("First sentence here. Second sentence here. Third and last sentence.", turn.Content.Text);
        Assert.Equal(ConversationFailure.ProviderFailed, result.SpeechFailure);
        Assert.Equal(ProviderRole.Tts, result.FailedProvider);
        // Nothing more is asked of the failed voice, and nothing else speaks instead.
        Assert.Equal(1, h.Tts.Calls);
        Assert.Equal(0, h.Device.Opens);
    }
}
