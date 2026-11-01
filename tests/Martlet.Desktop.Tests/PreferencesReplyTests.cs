using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Martlet.Conversation;
using Martlet.Conversation.Tests;
using Martlet.Core.Preferences;
using Martlet.Providers.Tests;

namespace Martlet.Desktop.Tests;

/// <summary>Your preferences through the real controller (docs/CONVERSATION.md, "Your preferences"): the reply's
/// manage_preferences tool saves "stop giving hints", and the next replies carry the preferences that apply now at the end of the
/// request, never in its instructions, its tools or the conversation kept.</summary>
public sealed class PreferencesReplyTests
{
    [Fact]
    public async Task StopGivingHintsIsSavedAndEveryLaterReplyCarriesItAtTheEnd()
    {
        await using var fixture = await LiveFixture.Create(tools: true, preferences: true);
        var bodies = new ConcurrentQueue<string>();
        var calls = 0;
        fixture.Llm.Respond = (_, _) =>
        {
            bodies.Enqueue(Encoding.UTF8.GetString(fixture.Llm.Body));
            return Task.FromResult(TextRecordingHandler.Sse(Interlocked.Increment(ref calls) == 1
                ? CallTrace(PreferenceTools.Name, """{"action":"add","text":"No hints unless I ask"}""")
                : Harness.Trace("Got it, no more hints.")));
        };

        var asked = fixture.Start("Please stop giving me hints.");
        await fixture.Finish(asked);
        var hint = Assert.Single(fixture.Preferences!.All);
        Assert.Equal(("No hints unless I ask", null, UserPreferences.Said), (hint.Text, hint.Activity, hint.Source));
        Assert.Contains(fixture.ToolService!.Log, entry => entry.Tool == PreferenceTools.Name && entry.Outcome == "added");
        Assert.DoesNotContain(fixture.ToolService.Log, entry => entry.Outcome.Contains("hints", StringComparison.Ordinal));

        var next = fixture.Start("Where do I go next?");
        await fixture.Finish(next);
        var sent = bodies.ToArray();
        var first = sent[0];
        var later = sent[^1];
        // The tool was offered in the same place before and after; the instructions never changed.
        Assert.Equal(PreferenceTools.Name, ToolNames(first)[^1]);
        Assert.Equal(ToolNames(first), ToolNames(later));
        Assert.Equal(Instructions(first), Instructions(later));
        Assert.DoesNotContain("No hints unless I ask", Instructions(later));
        // Before the preference, nothing; after it, at the end of the new message, and the kept message has none of it.
        Assert.DoesNotContain("standing preferences", UserTexts(first)[^1]);
        var users = UserTexts(later);
        Assert.StartsWith("Where do I go next?", users[^1]);
        Assert.EndsWith("- No hints unless I ask\n[/MARTLET_NOTES]", users[^1]);
        Assert.Equal("Please stop giving me hints.", users[0]);
    }

    private static string Instructions(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("instructions").GetString()!;
    }

    private static string[] ToolNames(string body)
    {
        using var json = JsonDocument.Parse(body);
        return json.RootElement.TryGetProperty("tools", out var tools)
            ? tools.EnumerateArray().Select(tool => tool.GetProperty("name").GetString()!).ToArray() : [];
    }

    // The user messages of a Responses request, in order.
    private static string[] UserTexts(string body)
    {
        using var document = JsonDocument.Parse(body);
        return [.. document.RootElement.GetProperty("input").EnumerateArray()
            .Where(item => item.TryGetProperty("role", out var role) && role.GetString() == "user")
            .Select(item => item.GetProperty("content") is { ValueKind: JsonValueKind.String } text ? text.GetString()!
                : item.GetProperty("content").EnumerateArray().Single(part => part.GetProperty("type").GetString() == "input_text")
                    .GetProperty("text").GetString()!)];
    }

    // The model answers with one function call and no text.
    private static string CallTrace(string name, string arguments)
    {
        var done = new { id = "fc_1", type = "function_call", call_id = "call_a", name, arguments, status = "completed" };
        return string.Concat(
            TextFixtures.Event("response.created", 0, new { response = Response("in_progress", []) }),
            TextFixtures.Event("response.in_progress", 1, new { response = Response("in_progress", []) }),
            TextFixtures.Event("response.output_item.added", 2, new { output_index = 0,
                item = new { id = "fc_1", type = "function_call", call_id = "call_a", name, arguments = "", status = "in_progress" } }),
            TextFixtures.Event("response.function_call_arguments.delta", 3, new { output_index = 0, item_id = "fc_1", delta = arguments }),
            TextFixtures.Event("response.function_call_arguments.done", 4, new { output_index = 0, item_id = "fc_1", arguments }),
            TextFixtures.Event("response.output_item.done", 5, new { output_index = 0, item = done }),
            TextFixtures.Event("response.completed", 6, new { response = Response("completed", [done]) }));
    }

    private static object Response(string status, object[] output) => new
    {
        id = "resp_call", @object = "response", model = TextFixtures.Model, status, store = false, background = false,
        output, error = (object?)null, incomplete_details = (object?)null
    };
}
