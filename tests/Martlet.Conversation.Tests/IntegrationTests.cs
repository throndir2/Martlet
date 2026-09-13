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
        Assert.Equal(ConversationState.Partial, result.State);
        Assert.Equal("A complete answer.", turn.Content.Text);
        Assert.True(result.TextComplete);
        Assert.Equal(ConversationFailure.ProviderFailed, result.Failure);
        Assert.Equal(1, h.Tts.Calls);
        Assert.InRange(result.AcceptedSamples, 0, 480);
        Assert.NotEqual(PlaybackState.Completed, result.Playback?.State);
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
    public async Task Aggregate_reservations_stop_additional_paid_segments(string cap)
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
        Assert.Equal(ConversationState.Partial, result.State);
        Assert.Equal(ConversationFailure.LimitExceeded, result.Failure);
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
}
