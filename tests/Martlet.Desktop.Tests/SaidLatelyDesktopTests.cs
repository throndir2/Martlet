using System.Text;
using System.Text.Json;
using Martlet.Conversation;
using Martlet.Core.Settings;
using Martlet.Desktop;
using Martlet.Providers;

namespace Martlet.Desktop.Tests;

/// <summary>What Martlet said lately, through the real controller: what it says on its own (a look, a remark on what this PC
/// played, a due reminder) carries it with when, last in notes the conversation never keeps; a reply to the user's words or
/// touches never does, so its request stays as it was.</summary>
public sealed class SaidLatelyDesktopTests
{
    private const string Header = "What you said lately, oldest first (it is ";

    [Fact]
    public async Task WhatMartletSaysOnItsOwnChecksWhatItSaidLatelyWhileRepliesToYouStayTheSame()
    {
        await using var fixture = await LiveFixture.Create();
        var image = new BoundedImage([0xFF, 0xD8, 0xFF, .. new byte[32]], ImageMediaType.Jpeg, 4, 4);

        // A reply to the user carries nothing about it, and what it says is noted.
        fixture.Answer("Ooh, that boss is almost down!");
        var first = fixture.Start("Look at this fight!");
        await fixture.Finish(first);
        Assert.DoesNotContain(Header, Body(fixture), StringComparison.Ordinal);
        Assert.Equal("your words", first.Inputs);

        // Twelve minutes later a look gets it, with when it was said, in the last notes of its message.
        fixture.Clock.ShiftUtc(TimeSpan.FromMinutes(12));
        fixture.Answer("[pass]");
        var look = fixture.Controller.StartCommentary(image, "ELDEN RING", ChattinessChoice.Normal, voice: false, screenApproved: true);
        await fixture.Finish(look);
        var message = Current(fixture.Llm.Body);
        var at = message.IndexOf(Header, StringComparison.Ordinal);
        Assert.True(at > message.IndexOf("(Screen glance.", StringComparison.Ordinal), message);
        Assert.Contains("(12 min ago): \"Ooh, that boss is almost down!\"", message[at..], StringComparison.Ordinal);
        Assert.EndsWith("[/MARTLET_NOTES]", message, StringComparison.Ordinal);
        Assert.Equal(1, look.SaidLately);
        Assert.Equal("the picture and 1 thing Martlet said lately", look.Inputs);

        // A reply to the user never carries it, and no earlier message kept it.
        fixture.Answer("Sure.");
        var second = fixture.Start("And now?");
        await fixture.Finish(second);
        Assert.DoesNotContain(Header, Body(fixture), StringComparison.Ordinal);
        Assert.Equal(0, second.SaidLately);
        Assert.Equal("your words", second.Inputs);

        // Nor does a reaction to a touch.
        fixture.Controller.Touches.Record(new(PhysicalKind.Tap, fixture.Controller.TouchNow, "your left cheek", "left cheek"));
        fixture.Answer("Hey, that tickles!");
        var touched = fixture.Controller.StartTouch(voice: false)!;
        await fixture.Finish(touched);
        Assert.DoesNotContain(Header, Body(fixture), StringComparison.Ordinal);
        Assert.Equal("1 touch", touched.Inputs);

        // What this PC played on its own does, with everything Martlet said (a [pass] isn't noted).
        fixture.Answer("Ha, what a comeback!");
        var played = fixture.Controller.Start("[PC audio] And he's back up!", voice: false, microphone: false, approved: true,
            spoken: true, pcAudio: true);
        await fixture.Finish(played);
        message = Current(fixture.Llm.Body);
        Assert.Contains(Header, message, StringComparison.Ordinal);
        Assert.Contains("(12 min ago): \"Ooh, that boss is almost down!\"", message, StringComparison.Ordinal);
        Assert.Contains("\"Sure.\"", message, StringComparison.Ordinal);
        Assert.Contains("\"Hey, that tickles!\"", message, StringComparison.Ordinal);
        Assert.DoesNotContain("\"[pass]\"", message, StringComparison.Ordinal);
        Assert.Equal("1 line this PC played and 3 things Martlet said lately", played.Inputs);

        // So does a due reminder Martlet brings up on its own.
        Assert.NotNull(fixture.Controller.Remind("oven", "check the oven (FIXTURE)"));
        await LiveConversationTests.Until(() => fixture.Controller.Jobs.HasNews);
        fixture.Answer("Hey, time to check the oven!");
        var report = fixture.Controller.StartReport(voice: false, noticesOnly: true)!;
        await fixture.Finish(report);
        message = Current(fixture.Llm.Body);
        Assert.Contains("a reminder they asked you for is due now", message, StringComparison.Ordinal);
        Assert.Contains("\"Ha, what a comeback!\"", message, StringComparison.Ordinal);
        Assert.Equal(4, report.SaidLately);
        var said = fixture.Controller.RecentSayings(fixture.Clock.GetLocalNow());
        Assert.Equal(["Ooh, that boss is almost down!", "Sure.", "Hey, that tickles!", "Ha, what a comeback!", "Hey, time to check the oven!"],
            said.Select(s => s.Text));

        // Refresh context forgets it too: the next look carries none.
        Assert.True(fixture.Controller.ForgetContext());
        fixture.Answer("[pass]");
        var fresh = fixture.Controller.StartCommentary(image, "ELDEN RING", ChattinessChoice.Normal, voice: false, screenApproved: true);
        await fixture.Finish(fresh);
        Assert.DoesNotContain(Header, Body(fixture), StringComparison.Ordinal);
        Assert.Equal(0, fresh.SaidLately);
        Assert.Empty(fixture.Controller.RecentSayings(fixture.Clock.GetLocalNow()));
    }

    [Fact]
    public async Task ALookRemarkIsNotedAndAnEmptiedPromptSendsNothing()
    {
        await using var fixture = await LiveFixture.Create();
        var image = new BoundedImage([0xFF, 0xD8, 0xFF, .. new byte[32]], ImageMediaType.Jpeg, 4, 4);
        fixture.Answer("Green build, nice! [seen: a green build]");
        var remark = fixture.Controller.StartCommentary(image, "Program.cs - Code", ChattinessChoice.Normal, voice: false, screenApproved: true);
        await fixture.Finish(remark);
        Assert.False(remark.Passed);
        Assert.Equal("Green build, nice!", Assert.Single(fixture.Controller.RecentSayings(fixture.Clock.GetLocalNow())).Text);

        fixture.Answer("[pass]");
        var next = fixture.Controller.StartCommentary(image, "Program.cs - Code", ChattinessChoice.Normal, voice: false, screenApproved: true);
        await fixture.Finish(next);
        Assert.Contains("\"Green build, nice!\"", Current(fixture.Llm.Body), StringComparison.Ordinal);
        // The look's own message no longer lists earlier remarks: they are in its notes.
        Assert.DoesNotContain("What you already said while watching", Body(fixture), StringComparison.Ordinal);

        var loaded = await fixture.Store.LoadAsync();
        await fixture.Save(loaded.Settings! with
        {
            Prompts = new PromptSettings { Overrides = new Dictionary<string, string> { [PromptCatalog.SaidLately] = "" } }
        });
        fixture.Answer("[pass]");
        var emptied = fixture.Controller.StartCommentary(image, "Program.cs - Code", ChattinessChoice.Normal, voice: false, screenApproved: true);
        await fixture.Finish(emptied);
        Assert.DoesNotContain("\"Green build, nice!\"", Current(fixture.Llm.Body), StringComparison.Ordinal);
        Assert.Equal(0, emptied.SaidLately);
        Assert.Equal("the picture", emptied.Inputs);
    }

    private static string Body(LiveFixture fixture) => Encoding.UTF8.GetString(fixture.Llm.Body);

    // The newest user message's text, with its notes.
    private static string Current(byte[] body)
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
