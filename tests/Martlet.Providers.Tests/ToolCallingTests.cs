using System.Text.Json;
using Martlet.Core.Contracts;
using Martlet.Core.Streaming;

namespace Martlet.Providers.Tests;

public sealed class ToolCallingTests
{
    private const string ChatBase = "https://example.test/api/v1";
    private static TextModelSelection ChatModel => new("chat-completions", "org/model:q4");

    // A schema nested deep enough that an echo of it in a Responses event exceeds the default JSON depth.
    private static string DeepSchema()
    {
        var inner = """{"type":"string"}""";
        for (var i = 0; i < 8; i++) inner = "{\"type\":\"object\",\"properties\":{\"n" + i + "\":" + inner + "}}";
        return inner;
    }

    private static TextToolDefinition Tool(string name = "read_file") =>
        new(name, "Reads a file.", """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""");

    private static async Task<(List<ProviderEvent> Events, TextGenerationResult Result)> Collect(TextGenerationStream stream)
    {
        var events = new List<ProviderEvent>();
        using var validator = new ProviderSequenceValidator(new()
        {
            Ids = ProviderFixtures.Context().Ids, Epoch = ProviderFixtures.Context().Epoch, Capabilities = stream.Capabilities
        }, new SequenceLimits { AllowEmptyCompletion = true });
        await foreach (var item in stream)
        {
            var update = validator.Accept(item);
            Assert.True(update.Decision is SequenceDecision.Accepted or SequenceDecision.Terminal, update.ToString());
            while (validator.TryReadText(out _)) { }
            events.Add(item);
        }
        return (events, stream.Result!);
    }

    private static object ResponseObject(string status, object[] output, object[] tools) => new
    {
        id = "resp_tools", @object = "response", model = TextFixtures.Model, status, store = false, background = false,
        output, tools, error = (object?)null, incomplete_details = (object?)null
    };

    [Fact]
    public async Task Responses_offers_tools_and_returns_text_and_function_calls()
    {
        var echoed = new object[]
        {
            new { type = "function", name = "read_file", strict = false, parameters = JsonSerializer.Deserialize<JsonElement>(DeepSchema()) }
        };
        var message = new { id = "msg_1", type = "message", role = "assistant", status = "completed",
            content = new[] { TextFixtures.Part("Let me look.") } };
        var call = new { id = "fc_1", type = "function_call", call_id = "call_a", name = "read_file",
            arguments = """{"path":"notes.txt"}""", status = "completed" };
        var trace = string.Concat(
            TextFixtures.Event("response.created", 0, new { response = ResponseObject("in_progress", [], echoed) }),
            TextFixtures.Event("response.in_progress", 1, new { response = ResponseObject("in_progress", [], echoed) }),
            TextFixtures.Event("response.output_item.added", 2, new { output_index = 0,
                item = new { id = "msg_1", type = "message", role = "assistant", status = "in_progress", content = Array.Empty<object>() } }),
            TextFixtures.Event("response.content_part.added", 3, new { output_index = 0, content_index = 0, item_id = "msg_1", part = TextFixtures.Part("") }),
            TextFixtures.Event("response.output_text.delta", 4, new { output_index = 0, content_index = 0, item_id = "msg_1", delta = "Let me look." }),
            TextFixtures.Event("response.output_text.done", 5, new { output_index = 0, content_index = 0, item_id = "msg_1", text = "Let me look." }),
            TextFixtures.Event("response.content_part.done", 6, new { output_index = 0, content_index = 0, item_id = "msg_1", part = TextFixtures.Part("Let me look.") }),
            TextFixtures.Event("response.output_item.done", 7, new { output_index = 0, item = message }),
            TextFixtures.Event("response.output_item.added", 8, new { output_index = 1,
                item = new { id = "fc_1", type = "function_call", call_id = "call_a", name = "read_file", arguments = "", status = "in_progress" } }),
            TextFixtures.Event("response.function_call_arguments.delta", 9, new { output_index = 1, item_id = "fc_1", delta = """{"path":""" }),
            TextFixtures.Event("response.function_call_arguments.delta", 10, new { output_index = 1, item_id = "fc_1", delta = "\"notes.txt\"}" }),
            TextFixtures.Event("response.function_call_arguments.done", 11, new { output_index = 1, item_id = "fc_1", arguments = """{"path":"notes.txt"}""" }),
            TextFixtures.Event("response.output_item.done", 12, new { output_index = 1, item = call }),
            TextFixtures.Event("response.completed", 13, new { response = ResponseObject("completed", [message, call], echoed) }));
        var handler = new TextRecordingHandler { Respond = (_, _) => Task.FromResult(TextRecordingHandler.Sse(trace)) };
        using var adapter = OpenAiTextGenerationAdapter.CreateForFixture(handler, new FixtureCredentials(), new FixtureClock());
        var context = ProviderFixtures.Context();
        var limits = new TextGenerationLimits { MaxEventBytes = ContractRules.MaxJsonBytes };
        var input = new BoundedTextInput("What's in my notes?", tools: [new("read_file", "Reads a file.", DeepSchema())]);
        var result = await Collect(adapter.Stream(context, TextFixtures.Selection, input, limits, TextFixtures.Authorize(context, limits)));

        Assert.Equal(TextGenerationOutcome.Completed, result.Result.Outcome);
        Assert.Equal("Let me look.", string.Concat(result.Events.Where(e => e.Kind == ProviderEventKind.TextDelta).Select(e => e.Text)));
        var made = Assert.Single(result.Result.ToolCalls);
        Assert.Equal(("call_a", "read_file", """{"path":"notes.txt"}"""), (made.CallId, made.Name, made.ArgumentsJson));
        using var body = JsonDocument.Parse(handler.Body);
        var tool = Assert.Single(body.RootElement.GetProperty("tools").EnumerateArray());
        Assert.Equal("function", tool.GetProperty("type").GetString());
        Assert.False(tool.GetProperty("strict").GetBoolean());
        Assert.Equal("auto", body.RootElement.GetProperty("tool_choice").GetString());
    }

    [Fact]
    public async Task Responses_continuation_sends_calls_and_outputs_and_can_forbid_more_calls()
    {
        var handler = new TextRecordingHandler();
        using var adapter = OpenAiTextGenerationAdapter.CreateForFixture(handler, new FixtureCredentials(), new FixtureClock());
        var context = ProviderFixtures.Context();
        var limits = new TextGenerationLimits();
        var first = new BoundedTextInput("What's in my notes?", tools: [Tool()]);
        var call = new TextToolCall("call_a", "read_file", """{"path":"notes.txt"}""");
        var next = first.WithToolRounds([new("Let me look.", [call], [new("call_a", "Buy milk.")])], callsAllowed: false);
        Assert.Same(first, next.Origin);
        Assert.True(next.InputTokenReservation > first.InputTokenReservation);
        var result = await TextFixtures.Collect(adapter.Stream(context, TextFixtures.Selection, next, limits, TextFixtures.Authorize(context, limits)));
        Assert.Equal(TextGenerationOutcome.Completed, result.Result.Outcome);
        using var body = JsonDocument.Parse(handler.Body);
        Assert.Equal("none", body.RootElement.GetProperty("tool_choice").GetString());
        var items = body.RootElement.GetProperty("input").EnumerateArray().ToArray();
        Assert.Equal("assistant", items[^3].GetProperty("role").GetString());
        Assert.Equal("function_call", items[^2].GetProperty("type").GetString());
        Assert.Equal("call_a", items[^2].GetProperty("call_id").GetString());
        Assert.Equal("function_call_output", items[^1].GetProperty("type").GetString());
        Assert.Equal("Buy milk.", items[^1].GetProperty("output").GetString());
    }

    [Fact]
    public async Task Chat_completions_collects_streamed_tool_call_pieces()
    {
        var trace = ChatCompletionsTests.Chunk(delta: new { role = "assistant", content = "" }) +
            ChatCompletionsTests.Chunk(delta: new { tool_calls = new[] { new { index = 0, id = "call_1", type = "function",
                function = new { name = "read_file", arguments = "" } } } }) +
            ChatCompletionsTests.Chunk(delta: new { tool_calls = new[] { new { index = 0, function = new { arguments = """{"path":""" } } } }) +
            ChatCompletionsTests.Chunk(delta: new { tool_calls = new[] { new { index = 0, function = new { arguments = "\"a.txt\"}" } } } }) +
            ChatCompletionsTests.Chunk(delta: new { }, finish: "tool_calls") + "data: [DONE]\n\n";
        var (result, body) = await RunChat(trace, new BoundedTextInput("Read a.txt", tools: [Tool()]));
        Assert.Equal(TextGenerationOutcome.Completed, result.Outcome);
        var call = Assert.Single(result.ToolCalls);
        Assert.Equal(("call_1", "read_file", """{"path":"a.txt"}"""), (call.CallId, call.Name, call.ArgumentsJson));
        using var json = JsonDocument.Parse(body);
        Assert.Equal("read_file", json.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("auto", json.RootElement.GetProperty("tool_choice").GetString());
    }

    [Fact]
    public async Task Chat_completions_accepts_whole_calls_without_index_finished_with_stop()
    {
        // Older Ollama: one chunk with the whole call (arguments as an object, no index or id), then finish "stop".
        var trace = ChatCompletionsTests.Chunk(delta: new { role = "assistant", content = "", tool_calls = new[] { new {
                function = new { name = "read_file", arguments = new { path = "b.txt" } } } } }) +
            ChatCompletionsTests.Chunk(delta: new { role = "assistant", content = "" }, finish: "stop") + "data: [DONE]\n\n";
        var (result, _) = await RunChat(trace, new BoundedTextInput("Read b.txt", tools: [Tool()]));
        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("call_1", call.CallId);
        Assert.Equal("read_file", call.Name);
        Assert.Equal("b.txt", JsonDocument.Parse(call.ArgumentsJson).RootElement.GetProperty("path").GetString());
    }

    [Fact]
    public async Task Chat_completions_continuation_sends_assistant_tool_calls_and_tool_messages()
    {
        var first = new BoundedTextInput("Read a.txt", tools: [Tool()]);
        var next = first.WithToolRounds([new("", [new("call_1", "read_file", """{"path":"a.txt"}""")], [new("call_1", "Hello.")])], true);
        var (result, body) = await RunChat(ChatCompletionsTests.Chunk("It says hello.", "stop") + "data: [DONE]\n\n", next);
        Assert.Equal(TextGenerationOutcome.Completed, result.Outcome);
        Assert.Empty(result.ToolCalls);
        using var json = JsonDocument.Parse(body);
        var messages = json.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal("assistant", messages[^2].GetProperty("role").GetString());
        Assert.Equal(JsonValueKind.Null, messages[^2].GetProperty("content").ValueKind);
        Assert.Equal("call_1", messages[^2].GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Assert.Equal("tool", messages[^1].GetProperty("role").GetString());
        Assert.Equal("call_1", messages[^1].GetProperty("tool_call_id").GetString());
        Assert.Equal("Hello.", messages[^1].GetProperty("content").GetString());
    }

    [Fact]
    public void Tool_results_are_cut_to_their_budget_and_inputs_drop_tools_on_request()
    {
        var bounded = TextToolResult.Bound(new string('x', 50_000), 1_000);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(bounded) <= 1_000);
        Assert.EndsWith("[output cut short]", bounded);
        var input = new BoundedTextInput("Hi", tools: [Tool()]);
        var plain = input.WithoutTools();
        Assert.Empty(plain.Tools);
        Assert.False(plain.ToolCallsAllowed);
        Assert.Same(input, plain.Origin);
        Assert.Equal(input.InputTokenReservation - input.ToolTokenReservation, plain.InputTokenReservation);
    }

    private static async Task<(TextGenerationResult Result, byte[] Body)> RunChat(string trace, BoundedTextInput input)
    {
        var handler = new TextRecordingHandler { Respond = (_, _) => Task.FromResult(TextRecordingHandler.Sse(trace)) };
        using var adapter = ChatCompletionsTextGenerationAdapter.CreateForFixture(ChatBase, handler, null, new FixtureClock());
        var context = ProviderFixtures.Context();
        var limits = new TextGenerationLimits();
        var authorization = new TextDisclosureAuthorization(new(new(ChatBase), ProviderRole.Llm, ChatModel.UpstreamModelId),
            ChatModel, context.Ids, context.Epoch, limits, context.Deadline, true, true);
        var (_, result) = await Collect(adapter.Stream(context, ChatModel, input, limits, authorization));
        return (result, handler.Body);
    }
}
