using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Martlet.Conversation;
using Martlet.Conversation.Tests;
using Martlet.Providers.Tests;

namespace Martlet.Desktop.Tests;

public sealed class ContextBoardReplyTests
{
    [Fact]
    public async Task Board_notes_go_after_the_words_once_and_never_into_the_conversation()
    {
        await using var fixture = await LiveFixture.Create();
        var bodies = new ConcurrentQueue<string>();
        fixture.Llm.Respond = (_, _) =>
        {
            bodies.Enqueue(Encoding.UTF8.GetString(fixture.Llm.Body));
            return Task.FromResult(TextRecordingHandler.Sse(Harness.Trace("Fixture reply.")));
        };
        var board = fixture.Controller.Board;
        var now = fixture.Clock.GetLocalNow();
        board.Post(ContextBoard.Screen, "Screen digest: a code editor.", now, TimeSpan.FromMinutes(5));
        board.Post(ContextBoard.Sound, "Stale music line.", now.AddMinutes(-10), TimeSpan.FromMinutes(1));
        board.Post(ContextBoard.Touch, "The user patted the character's head.", now, TimeSpan.FromMinutes(5), consume: true,
            kept: "(touch: a pat on the head)");
        var delivered = new ConcurrentQueue<ContextBoardSnapshot>();
        board.Sent += delivered.Enqueue;
        // Nothing shows on this PC: the conversation clears the character's note as it builds the request.
        board.Post(ContextBoard.Character, "Old emote note.", now, TimeSpan.FromMinutes(5));

        var first = fixture.Start("First message.");
        await fixture.Finish(first);
        var second = fixture.Start("Second message.");
        await fixture.Finish(second);

        Assert.Equal(2, bodies.Count);
        var one = UserTexts(bodies.ElementAt(0));
        var two = UserTexts(bodies.ElementAt(1));
        var sent = one[^1];
        Assert.StartsWith("First message.", sent);
        Assert.EndsWith("The user patted the character's head.\n[/MARTLET_NOTES]", sent);
        Assert.Contains("Screen digest: a code editor.\nThe user patted the character's head.", sent);
        Assert.DoesNotContain("Stale music line.", sent);
        Assert.DoesNotContain("Old emote note.", sent);
        Assert.Equal("your words and 2 context notes", first.Inputs);

        // The second request carries the first message as kept: without the board's notes, with the touch's kept line last.
        Assert.Equal("First message.\n(touch: a pat on the head)", two[^2]);
        Assert.StartsWith("First message.\n", sent);
        Assert.Contains("Screen digest: a code editor.", two[^1]);
        Assert.DoesNotContain("patted", two[^1]);
        Assert.DoesNotContain("(touch:", two[^1]);
        Assert.Equal("your words and 1 context note", second.Inputs);
        Assert.Equal([ContextBoard.Screen], board.LastSent.Sources);
        Assert.Equal(2, delivered.Count);
        Assert.Contains(delivered.First().Notes, n => n.Source == ContextBoard.Touch);
    }

    [Fact]
    public async Task What_a_check_in_adds_goes_after_the_words_once_and_leaves_the_instructions_alone()
    {
        await using var fixture = await LiveFixture.Create();
        var bodies = new ConcurrentQueue<string>();
        fixture.Llm.Respond = (_, _) =>
        {
            bodies.Enqueue(Encoding.UTF8.GetString(fixture.Llm.Body));
            return Task.FromResult(TextRecordingHandler.Sse(Harness.Trace("Fixture reply.")));
        };
        var context = CheckIns.Context(null, "Her tail is still curled from the long stroke down her back.")!;
        fixture.Controller.Board.Post(CheckIns.Source("c1"), context, fixture.Clock.GetLocalNow(), CheckIns.ContextAge, consume: true);

        var first = fixture.Start("First message.");
        await fixture.Finish(first);
        var second = fixture.Start("Second message.");
        await fixture.Finish(second);

        Assert.Equal(2, bodies.Count);
        var one = UserTexts(bodies.ElementAt(0));
        var two = UserTexts(bodies.ElementAt(1));
        Assert.StartsWith("First message.", one[^1]);
        Assert.EndsWith(context + "\n[/MARTLET_NOTES]", one[^1]);
        Assert.Equal("your words and 1 context note", first.Inputs);
        Assert.Equal("First message.", two[^2]);
        Assert.DoesNotContain(two, text => text.Contains("tail is still curled", StringComparison.Ordinal));
        Assert.Equal(Instructions(bodies.ElementAt(0)), Instructions(bodies.ElementAt(1)));
        Assert.DoesNotContain("tail is still curled", Instructions(bodies.ElementAt(0)));
    }

    private static string Instructions(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("instructions").GetString()!;
    }

    // The user messages of a Responses request, in order.
    private static string[] UserTexts(string body)
    {
        using var document = JsonDocument.Parse(body);
        return [.. document.RootElement.GetProperty("input").EnumerateArray()
            .Where(item => item.GetProperty("role").GetString() == "user")
            .Select(item => item.GetProperty("content") is { ValueKind: JsonValueKind.String } text ? text.GetString()!
                : item.GetProperty("content").EnumerateArray().Single(part => part.GetProperty("type").GetString() == "input_text")
                    .GetProperty("text").GetString()!)];
    }
}
