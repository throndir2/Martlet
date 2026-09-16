using System.Text;
using Martlet.Core.Contracts;
using Martlet.Providers.Ollama;

namespace Martlet.Providers.Tests.Ollama;

public sealed class OllamaChatProtocolTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(17, true)]
    [InlineData(4096, false)]
    public async Task Fragmented_unicode_ndjson_and_terminal_text_are_emitted_exactly_once(int fragment, bool crlf)
    {
        const string content = "Fixture \u00e9 \u6f22 \ud83d\ude42\n";
        var trace = "\n" + OllamaFixtures.Frame(content) + "\n" + OllamaFixtures.Frame("End.", true, "stop", true) + "\n";
        trace = trace.Replace("\\uD83D\\uDE42", "\ud83d\ude42");
        if (crlf) trace = trace.Replace("\n", "\r\n");
        var result = await OllamaFixtures.Run(trace, fragment: fragment);
        Assert.Equal(TextGenerationOutcome.Completed, result.Result.Outcome);
        Assert.Equal(content + "End.", string.Concat(result.Events.Where(e => e.Kind == ProviderEventKind.TextDelta).Select(e => e.Text)));
        Assert.Null(result.Events[^1].Text);
        Assert.Equal(12, result.Result.Usage.PromptEvalCount);
        Assert.Equal(0, result.Result.Usage.PromptEvalCachedCount);
        Assert.Equal(3, result.Result.Usage.EvalCount);
        Assert.Equal(100, result.Result.Usage.TotalDurationNanoseconds);
        Assert.False(result.Result.HasPartialOutput);
    }

    [Theory]
    [InlineData("stop", TextGenerationOutcome.Completed)]
    [InlineData("length", TextGenerationOutcome.OutputTokenLimit)]
    [InlineData("", TextGenerationOutcome.Incomplete)]
    [InlineData(null, TextGenerationOutcome.Incomplete)]
    [InlineData("unknown", TextGenerationOutcome.Incomplete)]
    public async Task Only_supported_stop_completes(string? reason, TextGenerationOutcome outcome)
    {
        var result = await OllamaFixtures.Run(OllamaFixtures.Frame() + OllamaFixtures.Frame("", true, reason));
        Assert.Equal(outcome, result.Result.Outcome);
        Assert.Equal(outcome != TextGenerationOutcome.Completed, result.Result.HasPartialOutput);
        Assert.Null(result.Result.Usage.EvalCount);
    }

    public static IEnumerable<object[]> InvalidRecords()
    {
        var normal = OllamaFixtures.Frame();
        foreach (var field in new[] { "remote_model", "remote_host", "context", "refusal", "logprobs", "_debug_info",
            "version", "protocol_version", "response", "messages", "choices", "future" })
            yield return [normal.Replace("\"done\":false", $"\"done\":false,\"{field}\":null"), ProviderFailureCode.UnsupportedOutput];
        foreach (var field in new[] { "thinking", "tool_calls", "images", "tool_name", "tool_call_id", "future" })
            foreach (var value in new[] { "null", "\"\"", "[]", "\"private hidden text\"" })
                yield return [normal.Replace("\"role\":\"assistant\"", $"\"role\":\"assistant\",\"{field}\":{value}"), ProviderFailureCode.UnsupportedOutput];
        foreach (var fragment in new[] { "\"role\":\"user\"", "\"role\":null", "\"role\":17" })
            yield return [normal.Replace("\"role\":\"assistant\"", fragment), ProviderFailureCode.ResponseSchema];
        foreach (var fragment in new[] { "\"done\":null", "\"done\":\"false\"", "\"done\":0" })
            yield return [normal.Replace("\"done\":false", fragment), ProviderFailureCode.ResponseSchema];
        yield return [normal.Replace("fixture-model:v1:local", "fixture-model:v1"), ProviderFailureCode.ResponseSchema];
        yield return [normal.Replace("fixture-model:v1:local", "fixture-model:v1:cloud"), ProviderFailureCode.ResponseSchema];
        yield return [normal.Replace("\"done\":false", "\"done\":false,\"done_reason\":\"stop\""), ProviderFailureCode.ResponseSchema];
        yield return [normal.Replace("\"done\":false", "\"done\":false,\"done\":false"), ProviderFailureCode.ResponseSchema];
        yield return [normal.Replace("\"role\":\"assistant\"", "\"role\":\"assistant\",\"r\\u006fle\":\"assistant\""), ProviderFailureCode.ResponseSchema];
        yield return [normal.Replace("Hello fixture.", "\\ud800"), ProviderFailureCode.ResponseSchema];
        yield return [normal.Replace("2026-09-16T06:40:00.123456789Z", "2026-02-30T06:40:00Z"), ProviderFailureCode.ResponseSchema];
        yield return [normal.Replace("2026-09-16T06:40:00.123456789Z", "2026-09-16T06:40:00Z\\n"), ProviderFailureCode.ResponseSchema];
        yield return [normal.Replace("2026-09-16T06:40:00.123456789Z", "2026-09-16T06:40:00.1234567890Z"), ProviderFailureCode.ResponseSchema];
        yield return ["{\"error\":\"secret\",\"done\":true}\n", ProviderFailureCode.ResponseSchema];
        yield return ["{\"error\":null}\n", ProviderFailureCode.ResponseSchema];
        yield return ["{\"message\":[]}\n", ProviderFailureCode.ResponseSchema];
        yield return ["[]\n", ProviderFailureCode.ResponseSchema];
        yield return [" \n", ProviderFailureCode.ResponseSchema];
        yield return ["\uFEFF" + normal, ProviderFailureCode.ResponseSchema];
        yield return [normal.Replace("false", "false,"), ProviderFailureCode.ResponseSchema];
        yield return ["//comment\n", ProviderFailureCode.ResponseSchema];
        yield return ["data: " + normal, ProviderFailureCode.ResponseSchema];
        yield return ["[DONE]\n", ProviderFailureCode.ResponseSchema];
        yield return [normal.Replace("\"done\":false", "\"done\":false,\r\"extra\":1"), ProviderFailureCode.ResponseSchema];
        yield return [OllamaFixtures.Frame("", true, "stop"), ProviderFailureCode.ResponseSchema];
        yield return [OllamaFixtures.Frame(" \t", true, "stop"), ProviderFailureCode.ResponseSchema];
        yield return [OllamaFixtures.Frame("hidden", true, "load"), ProviderFailureCode.UnsupportedOutput];
        yield return [OllamaFixtures.Frame("hidden", true, "unload"), ProviderFailureCode.UnsupportedOutput];
    }

    [Theory]
    [MemberData(nameof(InvalidRecords))]
    public async Task Invalid_record_is_rejected_atomically_before_its_content(string record, ProviderFailureCode code)
    {
        var result = await OllamaFixtures.Run(record, fragment: 3);
        Assert.Equal(code, result.Result.FailureCode);
        Assert.DoesNotContain(result.Events, e => e.Kind == ProviderEventKind.TextDelta || e.Kind == ProviderEventKind.Completed);
    }

    [Theory]
    [InlineData("prompt_eval_count")]
    [InlineData("prompt_eval_cached_count")]
    [InlineData("eval_count")]
    [InlineData("total_duration")]
    [InlineData("load_duration")]
    [InlineData("prompt_eval_duration")]
    [InlineData("eval_duration")]
    public async Task Every_known_numeric_field_is_bounded_typed_and_not_silently_ignored(string field)
    {
        foreach (var bad in new[] { "-1", "-0", "1.5", "1e0", "1e1000", "9223372036854775808", "null", "\"1\"", "true", "{}" })
        {
            var trace = OllamaFixtures.Frame().Replace("\"done\":false", $"\"done\":false,\"{field}\":{bad}");
            var result = await OllamaFixtures.Run(trace);
            Assert.Equal(ProviderFailureCode.ResponseSchema, result.Result.FailureCode);
            Assert.Equal(0, result.Result.EmittedTextCharacters);
        }
    }

    [Theory]
    [InlineData("eof")]
    [InlineData("newline")]
    [InlineData("extra")]
    [InlineData("second terminal")]
    [InlineData("partial utf8")]
    public async Task Truncation_and_extra_records_never_complete(string fault)
    {
        var trace = fault switch
        {
            "eof" => OllamaFixtures.Frame(),
            "newline" => OllamaFixtures.Trace.TrimEnd('\n'),
            "extra" => OllamaFixtures.Trace + OllamaFixtures.Frame("late"),
            "second terminal" => OllamaFixtures.Trace + OllamaFixtures.Frame("", true, "stop"),
            _ => OllamaFixtures.Frame()
        };
        var handler = OllamaFixtures.Handler(trace);
        if (fault == "partial utf8")
        {
            var bytes = Encoding.UTF8.GetBytes(trace).Concat(new byte[] { 0xc3, 0x28, 0x0a }).ToArray();
            handler.Respond = (_, _) => Task.FromResult(OllamaFixtures.Response(new FragmentedTextBody(bytes, 1)));
        }
        await using var adapter = OllamaChatAdapter.CreateForFixture(handler, new OllamaAuthority(), new FixtureClock());
        var result = await OllamaFixtures.Collect(OllamaFixtures.Stream(adapter));
        Assert.NotEqual(TextGenerationOutcome.Completed, result.Result.Outcome);
        Assert.True(result.Result.HasPartialOutput);
    }

    [Fact]
    public async Task Errors_are_sanitized_and_natural_language_refusal_is_not_a_machine_refusal()
    {
        var failed = await OllamaFixtures.Run(OllamaFixtures.Frame() + "{\"error\":\"" + ProviderFixtures.ContentCanary + "\"}\n");
        Assert.Equal(ProviderFailureCode.Server, failed.Result.FailureCode);
        Assert.True(failed.Result.HasPartialOutput);
        Assert.DoesNotContain(ProviderFixtures.ContentCanary, System.Text.Json.JsonSerializer.Serialize(failed.Result));
        var text = await OllamaFixtures.Run(OllamaFixtures.Frame("I cannot help with that.", true, "stop"));
        Assert.Equal(TextGenerationOutcome.Completed, text.Result.Outcome);
        Assert.DoesNotContain(text.Events, e => e.Kind == ProviderEventKind.Refused);
    }

    [Fact]
    public async Task Counts_are_terminal_wire_facts_not_intermediate_sums()
    {
        var result = await OllamaFixtures.Run(OllamaFixtures.Frame(usage: true) + OllamaFixtures.Frame("same") +
            OllamaFixtures.Frame("", true, "stop", true));
        Assert.Equal(12, result.Result.Usage.PromptEvalCount);
        Assert.Equal(3, result.Result.Usage.EvalCount);
        var missing = await OllamaFixtures.Run(OllamaFixtures.Frame(usage: true) + OllamaFixtures.Frame("", true, "stop"));
        Assert.Null(missing.Result.Usage.EvalCount);
        var excessive = await OllamaFixtures.Run(OllamaFixtures.Trace.Replace("\"eval_count\":3", "\"eval_count\":257"));
        Assert.Equal(ProviderFailureCode.OutputTokenLimit, excessive.Result.FailureCode);
        var cached = await OllamaFixtures.Run(OllamaFixtures.Trace.Replace("\"prompt_eval_cached_count\":0", "\"prompt_eval_cached_count\":13"));
        Assert.Equal(ProviderFailureCode.ResponseSchema, cached.Result.FailureCode);
    }

    [Theory]
    [InlineData("line")]
    [InlineData("body")]
    [InlineData("records")]
    [InlineData("text")]
    [InlineData("depth")]
    public async Task Exact_bounds_pass_and_one_over_fails(string bound)
    {
        var trace = OllamaFixtures.Trace;
        var limits = new TextGenerationLimits();
        if (bound == "depth")
        {
            var result = await OllamaFixtures.Run("{\"future\":" + new string('[', 17) + "0" + new string(']', 17) + "}\n");
            Assert.Equal(ProviderFailureCode.ResponseSchema, result.Result.FailureCode);
            return;
        }
        limits = bound switch
        {
            "line" => limits with { MaxEventBytes = Encoding.UTF8.GetByteCount(OllamaFixtures.Frame("", true, "stop", true)) },
            "body" => limits with { MaxEventBytes = 384, MaxStreamBytes = Encoding.UTF8.GetByteCount(trace) },
            "records" => limits with { MaxEvents = 2 },
            _ => limits with { MaxTextCharacters = "Hello fixture.".Length }
        };
        Assert.Equal(TextGenerationOutcome.Completed, (await OllamaFixtures.Run(trace, limits, 1)).Result.Outcome);
        var smaller = bound switch
        {
            "line" => limits with { MaxEventBytes = limits.MaxEventBytes - 1 },
            "body" => limits with { MaxStreamBytes = limits.MaxStreamBytes - 1 },
            "text" => limits with { MaxTextCharacters = limits.MaxTextCharacters - 1 },
            _ => limits
        };
        if (bound == "records") trace = OllamaFixtures.Frame("") + trace;
        var rejected = await OllamaFixtures.Run(trace, smaller, 1);
        Assert.Equal(ProviderFailureCode.ResponseTooLarge, rejected.Result.FailureCode);
    }
}
