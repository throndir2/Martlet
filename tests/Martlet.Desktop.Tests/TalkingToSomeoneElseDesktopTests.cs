using System.Collections.Concurrent;
using System.Text;
using Martlet.Conversation;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

/// <summary>Talking to someone else through the real controller: what always listening heard is noted for the check-in, answered
/// or not (never typed words), the check-ins' trigger fires for both, and while Martlet holds back its note goes at the end of
/// every request, not once.</summary>
public sealed class TalkingToSomeoneElseDesktopTests
{
    [Fact]
    public async Task WhatAlwaysListeningHeardIsNotedAndTheHoldGoesWithEveryRequest()
    {
        await using var fixture = await LiveFixture.Create();
        var spoke = new ConcurrentQueue<bool>();
        fixture.Controller.Spoke += spoke.Enqueue;
        LiveConversationOperation Heard(string words) =>
            fixture.Controller.Start(words, voice: false, microphone: false, approved: true, spoken: true);

        // Typed words are never noted.
        fixture.Answer("Hi!");
        await fixture.Finish(fixture.Start("Hello there."));
        Assert.Empty(fixture.Controller.RecentHeard(fixture.Clock.GetLocalNow()));

        // A one-sided phone call: a pass and an answer are both noted, with whether Martlet answered.
        fixture.Answer("[pass]");
        await fixture.Finish(Heard("Hello? Oh, hi Mom."));
        fixture.Answer("Ooh, what did you have?");
        await fixture.Finish(Heard("No, I already ate."));
        var heard = fixture.Controller.RecentHeard(fixture.Clock.GetLocalNow());
        Assert.Equal(new[] { "Hello? Oh, hi Mom.", "No, I already ate." }, heard.Select(h => h.Words).ToArray());
        Assert.Equal(new[] { false, true }, heard.Select(h => h.Answered).ToArray());
        Assert.Equal(new[] { false, true }, spoke.ToArray());

        // While Martlet holds back, the note goes at the end of every request.
        var hold = CheckIns.HoldBack(null, "on the phone with their mom", CheckIns.Aside, fixture.Clock.GetLocalNow());
        fixture.Controller.Board.Post(CheckIns.Source(CheckIns.Aside), CheckIns.AsideNote(null, hold)!, hold.Heard, CheckIns.AsideQuiet);
        fixture.Answer("[pass]");
        await fixture.Finish(Heard("Yeah, uh-huh."));
        Assert.Contains("not to you (on the phone with their mom)", Body(fixture), StringComparison.Ordinal);
        fixture.Answer("[pass]");
        await fixture.Finish(Heard("Okay, love you too."));
        Assert.Contains("not to you (on the phone with their mom)", Body(fixture), StringComparison.Ordinal);
        Assert.Equal(4, fixture.Controller.RecentHeard(fixture.Clock.GetLocalNow()).Count);

        // Refresh context forgets what was heard.
        Assert.True(fixture.Controller.ForgetContext());
        Assert.Empty(fixture.Controller.RecentHeard(fixture.Clock.GetLocalNow()));
    }

    private static string Body(LiveFixture fixture) => Encoding.UTF8.GetString(fixture.Llm.Body);
}
