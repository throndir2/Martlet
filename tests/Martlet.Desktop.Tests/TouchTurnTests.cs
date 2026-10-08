using System.Text;
using System.Text.Json;
using Martlet.Conversation;
using Martlet.Core.Settings;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

/// <summary>Touches reach the Thinking model through the real controller: in the notes of the user's own message (after their
/// words, never the instructions), or as a short touch-only reply, and the conversation keeps a short touch line.</summary>
public sealed class TouchTurnTests
{
    [Fact]
    public async Task ATypedMessageCarriesTheTouchesInItsNotesAndTheHistoryKeepsTheTouchLine()
    {
        await using var fixture = await LiveFixture.Create();
        fixture.Answer("Sure.");
        var before = fixture.Start("Hello.");
        await fixture.Finish(before);
        var plain = Instructions(fixture.Llm.Body);
        Assert.Null(before.Touches);

        for (var i = 0; i < 3; i++)
            fixture.Controller.Touches.Record(new(PhysicalKind.Pat, fixture.Controller.TouchNow, "the top of your head", "top of head"));
        fixture.Answer("Hehe, hi.");
        var typed = fixture.Start("How are you?");
        await fixture.Finish(typed);
        Assert.Equal("runtime.Completed", typed.Status.Code);
        var message = LastUser(fixture.Llm.Body);
        var words = message.IndexOf("How are you?", StringComparison.Ordinal);
        var touch = message.IndexOf("They patted the top of your head 3 times", StringComparison.Ordinal);
        Assert.True(words >= 0 && touch > words, message);
        Assert.Equal(plain, Instructions(fixture.Llm.Body));
        Assert.Equal("your words and 3 touches", typed.Inputs);
        Assert.Null(fixture.Controller.Touches.Peek(fixture.Controller.TouchNow));

        fixture.Answer("Fine.");
        var next = fixture.Start("And now?");
        await fixture.Finish(next);
        Assert.Contains("(touch: top of head pat x3)", Encoding.UTF8.GetString(fixture.Llm.Body), StringComparison.Ordinal);
        Assert.DoesNotContain("patted", LastUser(fixture.Llm.Body), StringComparison.Ordinal);
        Assert.Null(next.Touches);
    }

    [Fact]
    public async Task TouchesOnTheirOwnStartAShortReplyWhoseMessageIsTheTouches()
    {
        await using var fixture = await LiveFixture.Create();
        fixture.Answer("Sure.");
        var first = fixture.Start("Hello.");
        await fixture.Finish(first);
        var plain = Instructions(fixture.Llm.Body);

        // Zooming the view only goes with the next reply.
        fixture.Controller.Touches.Record(new(PhysicalKind.Zoomed, fixture.Controller.TouchNow, Detail: "in on your face"));
        Assert.Null(fixture.Controller.StartTouch(voice: false));

        fixture.Controller.Touches.Record(new(PhysicalKind.Tap, fixture.Controller.TouchNow, "your left cheek", "left cheek"));
        fixture.Controller.Touches.Record(new(PhysicalKind.Tap, fixture.Controller.TouchNow, "your left cheek", "left cheek"));
        fixture.Answer("Hey, that tickles!");
        var touched = fixture.Controller.StartTouch(voice: false);
        Assert.NotNull(touched);
        await fixture.Finish(touched);
        Assert.Equal("runtime.Completed", touched!.Status.Code);
        Assert.True(touched.Touch);
        var message = LastUser(fixture.Llm.Body);
        Assert.Contains("They zoomed in on your face, then poked your left cheek twice.", message, StringComparison.Ordinal);
        Assert.Contains("touched you, their desktop character", message, StringComparison.Ordinal);
        // It asks for words out loud, never only an emote or the silent reply.
        Assert.Contains("always say something, never only an emote, a sound or [pass]", message, StringComparison.Ordinal);
        Assert.Equal(plain, Instructions(fixture.Llm.Body));
        Assert.Equal("2 touches", touched.Inputs);

        fixture.Answer("Okay.");
        var next = fixture.Start("Sorry!");
        await fixture.Finish(next);
        var body = Encoding.UTF8.GetString(fixture.Llm.Body);
        // The touch reply stays in the conversation as it was sent, so the next request starts the same.
        Assert.Contains("touched you, their desktop character", body, StringComparison.Ordinal);
        Assert.Contains("Hey, that tickles!", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MovingTheCharacterAroundStartsAShortReplyToo()
    {
        await using var fixture = await LiveFixture.Create();
        fixture.Answer("Sure.");
        var first = fixture.Start("Hello.");
        await fixture.Finish(first);

        fixture.Controller.Touches.Record(new(PhysicalKind.Moved, fixture.Controller.TouchNow, Detail: "to their other monitor"));
        fixture.Answer("Whoa, where are we going?");
        var moved = fixture.Controller.StartTouch(voice: false);
        Assert.NotNull(moved);
        await fixture.Finish(moved);
        Assert.Equal("runtime.Completed", moved!.Status.Code);
        Assert.True(moved.Touch);
        var message = LastUser(fixture.Llm.Body);
        Assert.Contains("or moved you around, without saying anything.) They moved you to their other monitor. React to it out loud",
            message, StringComparison.Ordinal);
        Assert.Equal("1 touch", moved.Inputs);
        Assert.Null(fixture.Controller.Touches.Peek(fixture.Controller.TouchNow));
    }

    [Fact]
    public void TheTouchedPromptsAreEditableAndNameTheirPlaceholder()
    {
        Assert.Contains("{touches}", PromptCatalog.All.Single(p => p.Id == PromptCatalog.Touched).Default, StringComparison.Ordinal);
        Assert.Contains("{touches}", PromptCatalog.All.Single(p => p.Id == PromptCatalog.TouchedNotes).Default, StringComparison.Ordinal);
        Assert.Equal(["touches", "silent"], PromptCatalog.All.Single(p => p.Id == PromptCatalog.Touched).Placeholders);
        Assert.Contains("{silent}", PromptCatalog.All.Single(p => p.Id == PromptCatalog.Touched).Default, StringComparison.Ordinal);
        Assert.Equal("Last reply took 2 touches.", LiveConversationWindow.TurnInputsLine(false, false,
            MomentTurn.Describe(false, 0, false, null, 0, touches: 2), false));
    }

    private static string Instructions(byte[] body)
    {
        using var json = JsonDocument.Parse(body);
        return json.RootElement.TryGetProperty("instructions", out var instructions) ? instructions.GetString() ?? "" : "";
    }

    private static string LastUser(byte[] body)
    {
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("input").EnumerateArray()
            .Where(item => item.GetProperty("role").GetString() == "user")
            .Select(item => item.GetProperty("content") is { ValueKind: JsonValueKind.String } text ? text.GetString()!
                : item.GetProperty("content").EnumerateArray().Single(part => part.GetProperty("type").GetString() == "input_text")
                    .GetProperty("text").GetString()!)
            .Last();
    }
}
