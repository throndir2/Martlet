using System.Text;
using System.Text.Json;
using Martlet.Core.Contracts;

namespace Martlet.Providers.Tests;

public sealed class TextProtocolTests
{
    internal static async Task<(List<ProviderEvent> Events, TextGenerationResult Result)> Run(string trace,
        TextGenerationLimits? limits = null, int fragment = 4096)
    {
        var handler = new TextRecordingHandler { Respond = (_, _) => Task.FromResult(TextRecordingHandler.Sse(trace, fragment: fragment)) };
        return await Run(handler, limits);
    }

    internal static async Task<(List<ProviderEvent> Events, TextGenerationResult Result)> Run(TextRecordingHandler handler,
        TextGenerationLimits? limits = null)
    {
        limits ??= new();
        var context = ProviderFixtures.Context();
        using var adapter = OpenAiTextGenerationAdapter.CreateForFixture(handler, new FixtureCredentials(), new FixtureClock());
        return await TextFixtures.Collect(adapter.Stream(context, TextFixtures.Selection, new("Fixture prompt"),
            limits, TextFixtures.Authorize(context, limits)));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(17, true)]
    [InlineData(4096, false)]
    public async Task Incremental_utf8_and_newline_splits_preserve_text_without_final_replay(int fragment, bool crlf)
    {
        const string text = "Fixture \u00e9 \u6f22 \ud83d\ude42\r\nsecond line.";
        // Put actual multibyte UTF-8 on the wire instead of JSON's optional ASCII escapes.
        var trace = string.Concat(TextFixtures.Trace(text)).Replace("\\u00E9", "\u00e9").Replace("\\u6F22", "\u6f22")
            .Replace("\\uD83D\\uDE42", "\ud83d\ude42");
        if (crlf) trace = trace.Replace("\n", "\r\n");
        var result = await Run(trace, fragment: fragment);
        Assert.Equal(TextGenerationOutcome.Completed, result.Result.Outcome);
        Assert.Equal(text, Assert.Single(result.Events, x => x.Kind == ProviderEventKind.TextDelta).Text);
        Assert.Null(result.Events[^1].Text);
        Assert.Equal(new long[] { 0, 1, 2 }, result.Events.Select(x => x.Sequence));
        Assert.Equal(13, result.Result.Usage.InputTokens);
        Assert.Equal(0, result.Result.Usage.CachedInputTokens);
        Assert.Null(result.Result.Usage.EstimatedCost);
    }

    [Fact]
    public async Task Comments_multiline_data_optional_fields_and_sse_id_are_bounded_and_accepted()
    {
        var trace = string.Concat(TextFixtures.Trace()).Replace("data: {", ": heartbeat\nid: no-reconnect\nretry: 1000\nx-extension: ignored\ndata: {\ndata: \"future_field\":{\"nested\":\"ok\"},");
        var result = await Run(trace, fragment: 1);
        Assert.Equal(TextGenerationOutcome.Completed, result.Result.Outcome);
    }

    [Fact]
    public async Task Refusal_is_separate_from_user_text_and_does_not_enter_text_delta_channel()
    {
        var result = await Run(string.Concat(TextFixtures.Trace(ProviderFixtures.ContentCanary, refusal: true)));
        Assert.Equal(TextGenerationOutcome.Refused, result.Result.Outcome);
        Assert.Equal(ProviderFixtures.ContentCanary, result.Result.RefusalText);
        Assert.DoesNotContain(result.Events, x => x.Kind == ProviderEventKind.TextDelta);
        Assert.Equal(ProviderEventKind.Refused, result.Events[^1].Kind);
        Assert.DoesNotContain(ProviderFixtures.ContentCanary, result.Result.ToString() + JsonSerializer.Serialize(result.Result));
    }

    [Theory]
    [InlineData("max_output_tokens", TextGenerationOutcome.OutputTokenLimit, ProviderFailureCode.OutputTokenLimit)]
    [InlineData("content_filter", TextGenerationOutcome.Incomplete, ProviderFailureCode.ContentFiltered)]
    [InlineData("future_reason", TextGenerationOutcome.Incomplete, ProviderFailureCode.Incomplete)]
    public async Task Incomplete_terminal_preserves_partial_semantics(string reason, TextGenerationOutcome outcome, ProviderFailureCode failure)
    {
        var trace = TextFixtures.Trace();
        trace.RemoveRange(5, 4);
        trace.Add(TextFixtures.Event("response.incomplete", 5, new
        {
            response = new
            {
                id = "resp_fixture", @object = "response", model = TextFixtures.Model, status = "incomplete",
                output = new[] { TextFixtures.Item("Hello fixture.", "incomplete") },
                incomplete_details = new { reason }
            }
        }));
        var result = await Run(string.Concat(trace));
        Assert.Equal(outcome, result.Result.Outcome);
        Assert.Equal(failure, result.Result.Failure!.Code);
        Assert.Equal(ProviderEventKind.TextDelta, result.Events[1].Kind);
        Assert.Equal(ProviderEventKind.Failed, result.Events[^1].Kind);
        Assert.Null(result.Events[^1].Text);
        Assert.Null(result.Result.Usage.TotalTokens);
    }

    [Theory]
    [InlineData("error")]
    [InlineData("response.failed")]
    public async Task Typed_failures_never_copy_upstream_messages(string type)
    {
        var trace = TextFixtures.Trace().Take(5).ToList();
        trace.Add(TextFixtures.Event(type, 5, type == "error"
            ? new { code = "server_error", message = ProviderFixtures.Secret + ProviderFixtures.ContentCanary, param = "input" }
            : (object)new
            {
                response = new
                {
                    id = "resp_fixture", @object = "response", model = TextFixtures.Model, status = "failed",
                    output = new[] { TextFixtures.Item("Hello fixture.", "incomplete") },
                    error = new { code = "server_error", message = ProviderFixtures.Secret + ProviderFixtures.ContentCanary }
                }
            }));
        var result = await Run(string.Concat(trace));
        Assert.Equal(ProviderFailureCode.Server, result.Result.Failure!.Code);
        var metadata = JsonSerializer.Serialize(result.Result) + result.Result.Failure + result.Result;
        Assert.DoesNotContain(ProviderFixtures.Secret, metadata);
        Assert.DoesNotContain(ProviderFixtures.ContentCanary, metadata);
        Assert.Equal(Stage.Generation, result.Result.Failure.Error.Stage);
    }

    public static IEnumerable<object[]> InvalidStreams()
    {
        var trace = TextFixtures.Trace();
        yield return ["eof", string.Concat(trace.Take(5)), ProviderFailureCode.ResponseTruncated];
        yield return ["done sentinel", "data: [DONE]\n\n", ProviderFailureCode.ResponseTruncated];
        yield return ["missing blank boundary", string.Concat(trace)[..^1], ProviderFailureCode.ResponseTruncated];
        yield return ["wrong event", string.Concat(trace).Replace("event: response.output_text.delta", "event: response.refusal.delta"), ProviderFailureCode.ResponseSchema];
        yield return ["duplicate event", string.Concat(trace.Take(5).Concat([trace[4]]).Concat(trace.Skip(5))), ProviderFailureCode.ResponseSchema];
        yield return ["reordered sequence", string.Concat(trace).Replace("\"sequence_number\":4", "\"sequence_number\":5"), ProviderFailureCode.ResponseSchema];
        yield return ["negative sequence", string.Concat(trace).Replace("\"sequence_number\":4", "\"sequence_number\":-1"), ProviderFailureCode.ResponseSchema];
        yield return ["missing sequence", string.Concat(trace).Replace(",\"sequence_number\":4", ""), ProviderFailureCode.ResponseSchema];
        yield return ["wrong item", string.Concat(trace).Replace("\"item_id\":\"msg_fixture\"", "\"item_id\":\"msg_wrong\""), ProviderFailureCode.ResponseSchema];
        yield return ["wrong index", string.Concat(trace).Replace("\"output_index\":0", "\"output_index\":1"), ProviderFailureCode.ResponseSchema];
        yield return ["wrong content index", string.Concat(trace).Replace("\"content_index\":0", "\"content_index\":1"), ProviderFailureCode.ResponseSchema];
        yield return ["reasoning event", string.Concat(trace).Replace("response.output_text.delta", "response.reasoning_text.delta"), ProviderFailureCode.UnsupportedOutput];
        yield return ["unknown event", string.Concat(trace).Replace("response.output_text.delta", "response.future_event"), ProviderFailureCode.UnsupportedOutput];
        yield return ["tool item", string.Concat(trace).Replace("\"type\":\"message\"", "\"type\":\"function_call\""), ProviderFailureCode.UnsupportedOutput];
        yield return ["unknown part", string.Concat(trace).Replace("\"type\":\"output_text\"", "\"type\":\"audio\""), ProviderFailureCode.UnsupportedOutput];
        yield return ["nonassistant", string.Concat(trace).Replace("\"role\":\"assistant\"", "\"role\":\"user\""), ProviderFailureCode.ResponseSchema];
        yield return ["commentary phase", string.Concat(trace).Replace("\"role\":\"assistant\"", "\"role\":\"assistant\",\"phase\":\"commentary\""), ProviderFailureCode.UnsupportedOutput];
        yield return ["wrong final text", string.Concat(trace.Take(5)) + string.Concat(trace.Skip(5)).Replace("Hello fixture.", "Different text."), ProviderFailureCode.ResponseSchema];
        yield return ["wrong response", string.Concat(trace.Take(8)) + trace[8].Replace("resp_fixture", "resp_other"), ProviderFailureCode.ResponseSchema];
        yield return ["wrong model", string.Concat(trace).Replace(TextFixtures.Model, "unapproved-model"), ProviderFailureCode.ResponseSchema];
        yield return ["stored response", string.Concat(trace).Replace("\"store\":false", "\"store\":true"), ProviderFailureCode.ResponseSchema];
        yield return ["background response", string.Concat(trace).Replace("\"background\":false", "\"background\":true"), ProviderFailureCode.ResponseSchema];
        yield return ["duplicate json property", string.Concat(trace).Replace("\"sequence_number\":4", "\"sequence_number\":4,\"sequence_number\":4"), ProviderFailureCode.ResponseSchema];
        yield return ["invalid optional surrogate", string.Concat(trace).Replace("\"sequence_number\":4", "\"sequence_number\":4,\"unknown\":\"\\ud800\""), ProviderFailureCode.ResponseSchema];
        yield return ["usage totals", string.Concat(trace).Replace("\"total_tokens\":17", "\"total_tokens\":18"), ProviderFailureCode.ResponseSchema];
        yield return ["hidden reasoning usage", string.Concat(trace).Replace("\"reasoning_tokens\":0", "\"reasoning_tokens\":1"), ProviderFailureCode.UnsupportedOutput];
        yield return ["empty completed", string.Concat(TextFixtures.Trace("")), ProviderFailureCode.ResponseSchema];
        yield return ["whitespace completed", string.Concat(TextFixtures.Trace("   ")), ProviderFailureCode.ResponseSchema];
        yield return ["skipped done boundaries", string.Concat(trace.Take(5)) + trace[8].Replace("\"sequence_number\":8", "\"sequence_number\":5"), ProviderFailureCode.ResponseSchema];
        yield return ["no output completed", trace[0] + TextFixtures.Event("response.completed", 1, new
        {
            response = new { id = "resp_fixture", @object = "response", model = TextFixtures.Model, status = "completed", output = Array.Empty<object>() }
        }), ProviderFailureCode.ResponseSchema];
    }

    [Theory]
    [MemberData(nameof(InvalidStreams))]
    public async Task Malformed_replayed_or_unsupported_streams_fail_closed(string name, string stream, ProviderFailureCode failure)
    {
        Assert.NotEmpty(name);
        var result = await Run(stream, fragment: 3);
        Assert.Equal(TextGenerationOutcome.Failed, result.Result.Outcome);
        Assert.Equal(failure, result.Result.Failure!.Code);
        Assert.DoesNotContain(result.Events, x => x.Kind == ProviderEventKind.Completed);
        Assert.True(result.Events.Count(x => x.Kind == ProviderEventKind.TextDelta) <= 1);
    }

    [Theory]
    [InlineData("event")]
    [InlineData("stream")]
    [InlineData("count")]
    [InlineData("text")]
    [InlineData("comment")]
    public async Task Resource_bounds_fail_without_unbounded_buffering(string kind)
    {
        var limits = new TextGenerationLimits();
        string trace = string.Concat(TextFixtures.Trace());
        limits = kind switch
        {
            "event" or "comment" => limits with { MaxEventBytes = 128 },
            "stream" => limits with { MaxEventBytes = 1024, MaxStreamBytes = 1024 },
            "count" => limits with { MaxEvents = 4 },
            "text" => limits with { MaxTextCharacters = 3 },
            _ => limits
        };
        if (kind == "comment") trace = ":" + new string('a', 1024) + "\n\n" + trace;
        var result = await Run(trace, limits, fragment: 1);
        Assert.Equal(ProviderFailureCode.ResponseTooLarge, result.Result.Failure!.Code);
    }

    [Fact]
    public async Task Invalid_utf8_in_an_ignored_comment_is_rejected()
    {
        var body = new FragmentedTextBody([0x3a, 0xff, 0x0a, 0x0a]);
        var handler = new TextRecordingHandler { Respond = (_, _) => Task.FromResult(TextRecordingHandler.Sse(body)) };
        var result = await Run(handler);
        Assert.Equal(ProviderFailureCode.ResponseSchema, result.Result.Failure!.Code);
        Assert.True(body.Disposed);
    }

    [Fact]
    public async Task Upstream_sequence_can_start_nonzero_with_gaps_but_core_sequence_is_contiguous()
    {
        var trace = TextFixtures.Trace();
        for (int i = 0; i < trace.Count; i++)
            trace[i] = trace[i].Replace($"\"sequence_number\":{i}", $"\"sequence_number\":{17 + 3 * i}");
        var result = await Run(string.Concat(trace));
        Assert.Equal(TextGenerationOutcome.Completed, result.Result.Outcome);
        Assert.Equal(new long[] { 0, 1, 2 }, result.Events.Select(e => e.Sequence));
    }

    [Theory]
    [InlineData(0, false, TextGenerationOutcome.Completed)]
    [InlineData(0, true, TextGenerationOutcome.Completed)]
    [InlineData(1, false, TextGenerationOutcome.Failed)]
    [InlineData(-1, false, TextGenerationOutcome.Failed)]
    public async Task Declared_content_length_is_verified_before_publishing_terminal(int adjustment, bool sentinel, TextGenerationOutcome outcome)
    {
        string trace = string.Concat(TextFixtures.Trace()) + (sentinel ? "data: [DONE]\n\n" : "");
        var handler = new TextRecordingHandler
        {
            Respond = (_, _) =>
            {
                var response = TextRecordingHandler.Sse(trace, fragment: 1);
                response.Content.Headers.ContentLength = Encoding.UTF8.GetByteCount(trace) + adjustment;
                return Task.FromResult(response);
            }
        };
        var result = await Run(handler);
        Assert.Equal(outcome, result.Result.Outcome);
        if (adjustment != 0) Assert.Equal(ProviderFailureCode.ResponseTruncated, result.Result.Failure!.Code);
    }

    [Theory]
    [InlineData("max_messages")]
    [InlineData("steered")]
    [InlineData(null)]
    public async Task Other_current_incomplete_reasons_are_not_success(string? reason)
    {
        var trace = TextFixtures.Trace()[0] + TextFixtures.Event("response.incomplete", 1, new
        {
            response = new
            {
                id = "resp_fixture", @object = "response", model = TextFixtures.Model, status = "incomplete",
                output = Array.Empty<object>(), incomplete_details = new { reason }
            }
        });
        var result = await Run(trace);
        Assert.Equal(TextGenerationOutcome.Incomplete, result.Result.Outcome);
        Assert.Null(result.Result.Usage.InputTokens);
    }

    [Fact]
    public async Task Obfuscation_and_optional_usage_are_not_text_or_known_zero_cost()
    {
        var trace = string.Concat(TextFixtures.Trace()).Replace("\"sequence_number\":4",
            "\"sequence_number\":4,\"obfuscation\":\"private metadata\",\"logprobs\":[]");
        trace = trace.Replace("\"usage\":{\"input_tokens\":13,\"output_tokens\":4,\"total_tokens\":17,\"input_tokens_details\":{\"cached_tokens\":0},\"output_tokens_details\":{\"reasoning_tokens\":0}}",
            "\"usage\":null");
        var result = await Run(trace);
        Assert.Equal(TextGenerationOutcome.Completed, result.Result.Outcome);
        Assert.Null(result.Result.Usage.InputTokens);
        Assert.Null(result.Result.Usage.OutputTokens);
        Assert.Null(result.Result.Usage.EstimatedCost);
        Assert.DoesNotContain(result.Events, e => e.Text?.Contains("private metadata", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Multiple_text_deltas_are_incremental_and_final_full_text_is_only_checked()
    {
        const string final = "Hello fixture.";
        var trace = TextFixtures.Trace();
        trace[4] = TextFixtures.Event("response.output_text.delta", 4,
            new { output_index = 0, content_index = 0, item_id = "msg_fixture", delta = "Hello " });
        for (int i = 5; i < trace.Count; i++)
            trace[i] = trace[i].Replace($"\"sequence_number\":{i}", $"\"sequence_number\":{i + 1}");
        trace.Insert(5, TextFixtures.Event("response.output_text.delta", 5,
            new { output_index = 0, content_index = 0, item_id = "msg_fixture", delta = "fixture." }));
        var result = await Run(string.Concat(trace), fragment: 1);
        Assert.Equal(final, string.Concat(result.Events.Where(e => e.Kind == ProviderEventKind.TextDelta).Select(e => e.Text)));
        Assert.Equal(2, result.Events.Count(e => e.Kind == ProviderEventKind.TextDelta));
        Assert.Null(result.Events[^1].Text);
    }
}
