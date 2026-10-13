using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Talking to someone else (docs/CONVERSATION.md#talking-to-someone-else): always listening heard the user, so the
/// check-ins that start on <see cref="CheckInTriggers.Spoke"/> run on the Thinking pool beside the conversation, and while one says
/// the user talks to someone else (<see cref="CheckInOutcome.HoldBack"/>) Martlet holds back: every request carries a note on the
/// context board to stay quiet unless the user talks to it. The hold lasts while the user keeps speaking, and ends
/// <see cref="CheckIns.AsideQuiet"/> after they last spoke, when a check-in answers BACK, or when its check-in is turned off. What
/// the user said and who they talk to are never logged or written to the status file.</summary>
public partial class MainWindow
{
    private CheckInAside? checkInAside;

    /// <summary>Always listening heard the user and its exchange is in the conversation (<paramref name="answered"/>: Martlet
    /// answered, false: it stayed quiet): a hold lasts from now, and the check-ins that start when the user speaks fire.</summary>
    private void CheckInSpoke(bool answered)
    {
        if (closing || Role != DeviceRole.Companion) return;
        var now = DateTimeOffset.Now;
        if (checkInAside is { } holding)
        {
            if (CheckIns.Spoke(holding, now) is { } longer) PostAside(longer);
            else EndAside($"you were quiet for {Reminders.Span(CheckIns.AsideQuiet)}");
        }
        if (!CheckIns.All(checkInSettings).Any(c => c.On && c.Triggers.HasFlag(CheckInTriggers.Spoke))) return;
        FireCheckIns(CheckIns.SpokeTrigger(now, answered));
    }

    /// <summary>At each look: a hold whose time passed, or whose check-in was turned off, ends.</summary>
    private void FollowAside(IReadOnlyList<CheckIn> all, DateTimeOffset now)
    {
        if (checkInAside is not { } holding) return;
        if (!holding.HoldsAt(now)) EndAside($"you were quiet for {Reminders.Span(CheckIns.AsideQuiet)}");
        else if (Role != DeviceRole.Companion || !all.Any(c => c.Id == holding.By && c.On)) EndAside("its check-in is off");
    }

    /// <summary>What a check-in's answer does when it holds Martlet back: an ASIDE: line starts or renews the hold, BACK ends it.</summary>
    private (string Result, bool Acted) ActOnAside(CheckIn checkIn, CheckInVerdict verdict, PromptSettings? prompts)
    {
        var now = DateTimeOffset.Now;
        var holding = checkInAside is { } held && held.HoldsAt(now) ? held : null;
        if (verdict.Text is not { } about)
        {
            if (holding is null) return ("Martlet wasn't holding back", false);
            EndAside("a check-in said you talk to Martlet again");
            return ("Martlet talks as usual again", true);
        }
        var hold = CheckIns.HoldBack(holding, about, checkIn.Id, now);
        if (PostAside(hold, prompts) is { } problem) return (problem, false);
        if (holding is null) ErrorLog.Info($"Check-ins: {checkIn.Name} says you talk to someone else, so Martlet holds back until you " +
            $"talk to it, that conversation ends or you're quiet for {Reminders.Span(CheckIns.AsideQuiet)}.");
        return (holding is null ? "Martlet holds back while you talk to someone else" : "Martlet still holds back while you talk to someone else", true);
    }

    // Keeps the hold and its note on the context board (which goes with every request until it ends); a problem in a few words.
    private string? PostAside(CheckInAside hold, PromptSettings? prompts = null)
    {
        if (CheckIns.AsideNote(prompts ?? homeSettings?.Prompts, hold) is not { } note)
        {
            EndAside("its prompt is empty");
            return "the Check-in: holding back while you talk to someone else prompt is empty, so Martlet doesn't hold back";
        }
        try { contextBoard.Post(CheckIns.Source(hold.By), note, hold.Heard, CheckIns.AsideQuiet); }
        catch (InvalidOperationException)
        {
            EndAside("the context board is full");
            return "the context board is full, so Martlet doesn't hold back";
        }
        checkInAside = hold;
        return null;
    }

    private void EndAside(string why)
    {
        if (checkInAside is not { } holding) return;
        checkInAside = null;
        contextBoard.Clear(CheckIns.Source(holding.By));
        ErrorLog.Info($"Check-ins: Martlet talks as usual again ({why}); it held back for {Reminders.Span(DateTimeOffset.Now - holding.Since)}.");
        WriteCheckInStatus();
    }

    // check-ins-status.json: whether Martlet holds back now and until when (never who the user talks to or what they said).
    private object AsideStatus()
    {
        var now = DateTimeOffset.Now;
        return checkInAside is { } holding && holding.HoldsAt(now)
            ? new { holding = true, by = (string?)holding.By, since = (DateTimeOffset?)holding.Since, until = (DateTimeOffset?)holding.Until, aboutCharacters = holding.About.Length }
            : new { holding = false, by = (string?)null, since = (DateTimeOffset?)null, until = (DateTimeOffset?)null, aboutCharacters = 0 };
    }
}
