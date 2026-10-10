using Martlet.Core.Contracts;
using Martlet.Providers;
using Martlet.Providers.Tests;

namespace Martlet.Conversation.Tests;

public sealed class ToolLoopTests
{
    internal sealed class FixtureTools(string output) : IConversationToolHost
    {
        internal List<TextToolCall> Calls { get; } = [];
        public ValueTask<ConversationToolResult> CallAsync(TextToolCall call, CancellationToken cancellationToken)
        {
            Calls.Add(call);
            return ValueTask.FromResult(new ConversationToolResult(output));
        }
    }

    private static TextToolDefinition Tool => new("read_file", "Reads a file.",
        """{"type":"object","properties":{"path":{"type":"string"}}}""");

    private static object Response(string status, object[] output) => new
    {
        id = "resp_call", @object = "response", model = TextFixtures.Model, status, store = false, background = false,
        output, error = (object?)null, incomplete_details = (object?)null
    };

    // The model answers with one function call and no text.
    internal static string CallTrace()
    {
        const string arguments = """{"path":"notes.txt"}""";
        var done = new { id = "fc_1", type = "function_call", call_id = "call_a", name = "read_file", arguments, status = "completed" };
        return string.Concat(
            TextFixtures.Event("response.created", 0, new { response = Response("in_progress", []) }),
            TextFixtures.Event("response.in_progress", 1, new { response = Response("in_progress", []) }),
            TextFixtures.Event("response.output_item.added", 2, new { output_index = 0,
                item = new { id = "fc_1", type = "function_call", call_id = "call_a", name = "read_file", arguments = "", status = "in_progress" } }),
            TextFixtures.Event("response.function_call_arguments.delta", 3, new { output_index = 0, item_id = "fc_1", delta = arguments }),
            TextFixtures.Event("response.function_call_arguments.done", 4, new { output_index = 0, item_id = "fc_1", arguments }),
            TextFixtures.Event("response.output_item.done", 5, new { output_index = 0, item = done }),
            TextFixtures.Event("response.completed", 6, new { response = Response("completed", [done]) }));
    }

    internal static ConversationRequest Request(IConversationToolHost tools) => new(
        new BoundedTextInput("What's in my notes?", tools: [Tool]), TextFixtures.Selection, new(), new(), tools: tools);

    [Fact]
    public async Task A_tool_round_runs_the_call_and_sends_its_result_back_for_the_answer()
    {
        await using var harness = new Harness(textOnly: true);
        harness.Llm.Respond = (_, _) => Task.FromResult(TextRecordingHandler.Sse(
            harness.Llm.Calls == 1 ? CallTrace() : Harness.Trace("You need ", "milk.")));
        var tools = new FixtureTools("Buy milk.");
        var request = Request(tools);
        var snapshot = await Harness.Finish(harness.Start(request), harness.Clock);

        Assert.Equal(ConversationState.Completed, snapshot.State);
        Assert.Equal(1, snapshot.ToolCalls);
        Assert.False(snapshot.ToolsRejected);
        Assert.Equal("read_file", Assert.Single(tools.Calls).Name);
        Assert.Equal(2, harness.Llm.Calls);
        var actions = harness.Permissions.TextActions.ToArray();
        Assert.Equal(2, actions.Length);
        Assert.Same(request.Input, actions[0].Input);
        Assert.Same(request.Input, actions[1].Input.Origin);
        Assert.Equal("Buy milk.", Assert.Single(Assert.Single(actions[1].Input.ToolRounds).Results).Output);
        Assert.NotEqual(actions[0].Context.Ids.RequestId, actions[1].Context.Ids.RequestId);
    }

    [Fact]
    public async Task A_model_that_rejects_tools_is_asked_once_more_without_them()
    {
        await using var harness = new Harness(textOnly: true);
        harness.Llm.Respond = (_, _) => Task.FromResult(harness.Llm.Calls == 1
            ? TextRecordingHandler.Sse("""{"error":{"message":"model does not support tools"}}""", 400)
            : TextRecordingHandler.Sse(Harness.Trace("Hello.")));
        var tools = new FixtureTools("unused");
        var snapshot = await Harness.Finish(harness.Start(Request(tools)), harness.Clock);

        Assert.Equal(ConversationState.Completed, snapshot.State);
        Assert.True(snapshot.ToolsRejected);
        Assert.Empty(tools.Calls);
        var actions = harness.Permissions.TextActions.ToArray();
        Assert.Equal(2, actions.Length);
        Assert.Empty(actions[1].Input.Tools);
    }

    [Fact]
    public async Task A_model_that_refuses_thinking_steps_off_is_asked_once_more_with_its_default()
    {
        await using var harness = new Harness(textOnly: true);
        harness.Llm.Respond = (_, _) => Task.FromResult(harness.Llm.Calls == 1
            ? TextRecordingHandler.Sse("""{"error":{"message":"reasoning is mandatory for this model"}}""", 400)
            : TextRecordingHandler.Sse(Harness.Trace("Hello.")));
        var request = new ConversationRequest(new BoundedTextInput("Hi"), TextFixtures.Selection, new(), new(),
            generation: new Martlet.Core.Settings.GenerationSettings { Reasoning = false });
        var snapshot = await Harness.Finish(harness.Start(request), harness.Clock);

        Assert.Equal(ConversationState.Completed, snapshot.State);
        Assert.True(snapshot.ReasoningRejected);
        Assert.False(snapshot.ToolsRejected);
        Assert.Equal(2, harness.Llm.Calls);
        Assert.Null(Martlet.Core.Settings.GenerationSettings.WithoutReasoning(request.Generation));
    }
}
