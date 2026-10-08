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
        // It asks for a sound or words out loud, never silence or an emote alone.
        Assert.Contains("make a sound", message, StringComparison.Ordinal);
        Assert.Contains("Never stay silent: don't answer with only an emote or [pass].", message, StringComparison.Ordinal);
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
        Assert.Contains("or moved you around, without saying a word. That is how they talk to you right now.) They moved you to their " +
            "other monitor. React to it out loud", message, StringComparison.Ordinal);
        Assert.Equal("1 touch", moved.Inputs);
        Assert.Null(fixture.Controller.Touches.Peek(fixture.Controller.TouchNow));
    }

    [Fact]
    public async Task ATouchThatStoppedMartletTellsTheReactionWhatItWasSayingAndAnswering()
    {
        await using var fixture = await LiveFixture.Create();
        fixture.Answer("Sure.");
        await fixture.Finish(fixture.Start("Hello."));

        var now = fixture.Controller.TouchNow;
        fixture.Controller.Touches.Record(new(PhysicalKind.Tap, now, "your groin", "groin", Intimate: true, Feeling: "you hate being touched there"));
        fixture.Controller.Touches.CutIn(new("Once upon a time there was a fox.", "Tell me a story.", now));
        fixture.Answer("Hey! Hands off while I'm telling a story!");
        var reaction = fixture.Controller.StartTouch(voice: false);
        Assert.NotNull(reaction);
        await fixture.Finish(reaction);
        Assert.Equal("runtime.Completed", reaction!.Status.Code);
        var message = LastUser(fixture.Llm.Body);
        Assert.Contains("They poked your groin once (you hate being touched there). They did it while you were talking, so you stopped " +
            "mid-sentence. You had said, out loud: \"Once upon a time there was a fox.\" You were answering their message: \"Tell me a " +
            "story.\" Decide for yourself how to go on", message, StringComparison.Ordinal);
        Assert.Contains(reaction.TouchText!, message, StringComparison.Ordinal);
        Assert.Null(fixture.Controller.Touches.Peek(fixture.Controller.TouchNow));

        // Touches that come with a message go in its notes, and Martlet decides what comes first.
        fixture.Controller.Touches.Record(new(PhysicalKind.Stroke, fixture.Controller.TouchNow, "your hair", "hair", "slowly"));
        fixture.Answer("Mm, nice. Anyway, the fox...");
        await fixture.Finish(fixture.Start("Go on."));
        var notes = LastUser(fixture.Llm.Body);
        Assert.Contains("They slowly stroked your hair once. Take it in together with what they said", notes, StringComparison.Ordinal);
        Assert.Contains("You decide what comes first.", notes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdultContentFollowsOneMomentInEveryReplyOnlyWhileItIsOn()
    {
        await using var fixture = await LiveFixture.Create();
        var adult = PromptSettings.Fill(null, PromptCatalog.AdultContent)!;
        var moment = LiveConversationConfiguration.Moment(null)!;
        fixture.Answer("Hi.");
        await fixture.Finish(fixture.Start("Hello."));
        Assert.DoesNotContain(adult, Instructions(fixture.Llm.Body), StringComparison.Ordinal);

        var loaded = await fixture.Store.LoadAsync();
        await fixture.Save(loaded.Settings! with { Generation = new() { AdultContent = true } });
        fixture.Answer("Hi again.");
        await fixture.Finish(fixture.Start("Hello again."));
        var first = Instructions(fixture.Llm.Body);
        Assert.Contains(moment + "\n\n" + adult, first, StringComparison.Ordinal);
        // The same every time, so the prompt cache keeps it; a touch reaction starts the same way.
        fixture.Answer("Again.");
        await fixture.Finish(fixture.Start("And again."));
        Assert.Equal(first, Instructions(fixture.Llm.Body));
        fixture.Controller.Touches.Record(new(PhysicalKind.Tap, fixture.Controller.TouchNow, "your chest", "chest", Intimate: true));
        fixture.Answer("Eek!");
        await fixture.Finish(fixture.Controller.StartTouch(voice: false));
        Assert.Equal(first, Instructions(fixture.Llm.Body));
        Assert.Contains("under 18", adult, StringComparison.Ordinal);
        // MainWindow's statics need WPF's pack: scheme, which a test that shows no window hasn't registered yet.
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        Assert.Contains("Adult content is on.", MainWindow.DescribeGeneration(new() { AdultContent = true }));
        Assert.DoesNotContain("Adult content", MainWindow.DescribeGeneration(null));
    }

    [Fact]
    public void TheTouchedPromptsAreEditableAndNameTheirPlaceholder()
    {
        Assert.Contains("{touches}", PromptCatalog.All.Single(p => p.Id == PromptCatalog.Touched).Default, StringComparison.Ordinal);
        Assert.Contains("{touches}", PromptCatalog.All.Single(p => p.Id == PromptCatalog.TouchedNotes).Default, StringComparison.Ordinal);
        Assert.Equal(["touches", "silent"], PromptCatalog.All.Single(p => p.Id == PromptCatalog.Touched).Placeholders);
        Assert.Contains("{silent}", PromptCatalog.All.Single(p => p.Id == PromptCatalog.Touched).Default, StringComparison.Ordinal);
        Assert.Equal(["said", "answering"], PromptCatalog.All.Single(p => p.Id == PromptCatalog.TouchedCutIn).Placeholders);
        Assert.Empty(PromptCatalog.All.Single(p => p.Id == PromptCatalog.AdultContent).Placeholders);
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
