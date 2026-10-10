using System.Text;
using Martlet.Conversation;
using Martlet.Core.Settings;
using Martlet.Desktop;
using Martlet.Providers;

namespace Martlet.Desktop.Tests;

/// <summary>A look too long for Thinking's context size, through the real controller: it goes without what it can leave out
/// (the text read on the screen first) instead of stopping vision, and fails only when the picture and the persona alone don't
/// fit, without a provider call.</summary>
public sealed class LookContextSizeTests
{
    private static readonly BoundedImage Image = new([0xFF, 0xD8, 0xFF, .. new byte[32]], ImageMediaType.Jpeg, 4, 4);

    [Fact]
    public async Task ALookTooLongGoesWithoutTheTextReadOnTheScreenAndKeepsItsNotes()
    {
        await using var fixture = await LiveFixture.Create();
        var loaded = await fixture.Store.LoadAsync();
        await fixture.Save(loaded.Settings! with { Generation = new() { ContextTokens = GenerationSettings.MinimumContextTokens } });
        fixture.Controller.Board.Post(ContextBoard.Activity, "The user is playing a game (FIXTURE).", fixture.Clock.GetLocalNow(),
            TimeSpan.FromMinutes(5), consume: true);
        // About 4,000 tokens of text read on the screen: with the picture and the instructions, more than the smallest context.
        var read = "READ-FIXTURE " + new string('x', 12_000);

        fixture.Answer("[pass]");
        var look = fixture.Controller.StartCommentary(Image, "ELDEN RING", ChattinessChoice.Normal, voice: false, screenApproved: true,
            screenText: read);
        await fixture.Finish(look);

        Assert.True(look.Passed, look.Status.Code);
        Assert.Equal([LiveConversationController.LookReadText], look.LookLeftOut);
        Assert.Equal(1, fixture.Llm.Calls);
        var body = Encoding.UTF8.GetString(fixture.Llm.Body);
        Assert.DoesNotContain("READ-FIXTURE", body, StringComparison.Ordinal);
        Assert.Contains("input_image", body, StringComparison.Ordinal);
        // The context board's notes still went, and the consume-on-read one is gone.
        Assert.Contains("The user is playing a game (FIXTURE).", body, StringComparison.Ordinal);
        Assert.Equal(1, look.BoardNotes);
        Assert.Empty(fixture.Controller.Board.Snapshot(fixture.Clock.GetLocalNow()).Notes);
        Assert.Equal(" To fit Thinking's context size, it went without the text read on the screen.",
            LiveConversationWindow.LookTrimmed(look.LookLeftOut));

        // The next look with text that fits sends it again.
        fixture.Answer("[pass]");
        var next = fixture.Controller.StartCommentary(Image, "ELDEN RING", ChattinessChoice.Normal, voice: false, screenApproved: true,
            screenText: "READ-FIXTURE short");
        await fixture.Finish(next);
        Assert.Empty(next.LookLeftOut);
        Assert.Contains("READ-FIXTURE short", Encoding.UTF8.GetString(fixture.Llm.Body), StringComparison.Ordinal);
        Assert.Equal("", LiveConversationWindow.LookTrimmed(next.LookLeftOut));
    }

    [Fact]
    public async Task ALookWhosePictureAndPersonaDontFitFailsWithoutAProviderCallOrUsingTheNotes()
    {
        await using var fixture = await LiveFixture.Create();
        var loaded = await fixture.Store.LoadAsync();
        var persona = loaded.Settings!.Companion!.ActivePersona;
        await fixture.Save(loaded.Settings with
        {
            Generation = new() { ContextTokens = GenerationSettings.MinimumContextTokens },
            Companion = loaded.Settings.Companion.Update(persona.Id, persona.Name, new string('\u00e9', PersonaProfile.MaximumTextCharacters))
        });
        fixture.Controller.Board.Post(ContextBoard.Activity, "The user is playing a game (FIXTURE).", fixture.Clock.GetLocalNow(),
            TimeSpan.FromMinutes(5), consume: true);

        var look = fixture.Controller.StartCommentary(Image, "ELDEN RING", ChattinessChoice.Normal, voice: false, screenApproved: true,
            screenText: "READ-FIXTURE short", look: true);
        await fixture.Finish(look);

        Assert.Equal("conversation.input_limit", look.Status.Code);
        fixture.NoEffects();
        Assert.Single(fixture.Controller.Board.Snapshot(fixture.Clock.GetLocalNow()).Notes);
        Assert.StartsWith("Martlet stopped vision. The picture and the persona together are too long", LiveConversationWindow.LookTooLong,
            StringComparison.Ordinal);
    }
}
