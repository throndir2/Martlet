using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Audio;
using Martlet.Conversation;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>The Companion choices the talk window follows: how you talk (Listening), whether replies are spoken (Voice) and
/// what Martlet may look at (Vision). They save to talk-preferences.json and an open talk window follows them at once; the
/// talk window itself shows the conversation, and listening and watching each start and stop from their own buttons there, on
/// Home and in the notification-area menu.</summary>
public partial class MainWindow
{
    private TalkPreferences? talk;
    // A camera address exactly as typed (a password included), for this app session only; the saved copy has no password.
    private string? visionAddress;
    // Companion › Listening's end-of-turn judge status line while the page shows it (it follows each decision).
    private TextBlock? turnJudgeStatus;

    /// <summary>Companion › Listening's end-of-turn judge status (TalkJudgeTurnsStatus): whether it is on, which judge runs and
    /// whether it can, and how the newest turns were decided (no words).</summary>
    internal static string TurnJudgeText(bool on,
        (string? Judge, bool Available, string? Problem, TimeSpan? LoadTime, Martlet.Conversation.EndOfTurnDecision[] Decisions)? status)
    {
        if (!on) return "Off: the pause above alone decides when you finished talking.";
        if (status is not { } s || !s.Available)
            return $"On, but the judge can't run here ({status?.Problem ?? "no judge"}), so the pause above decides until it can.";
        var text = s.Problem is not null
            ? $"On, but Smart Turn can't run here ({s.Problem}), so {s.Judge} judges instead"
            : $"On: {s.Judge} on this PC" + (s.LoadTime is { } load ? $" (loaded in {load.TotalMilliseconds:0} ms)" : "");
        if (s.Decisions.Length == 0) return text + ", and no turns are judged yet.";
        var complete = s.Decisions.Count(d => d.Outcome == Martlet.Conversation.EndOfTurnDecision.Complete);
        var unfinished = s.Decisions.Count(d => d.Outcome is Martlet.Conversation.EndOfTurnDecision.Incomplete or Martlet.Conversation.EndOfTurnDecision.WentOn);
        var plain = s.Decisions.Length - complete - unfinished;
        var times = s.Decisions.Where(d => d.JudgeTime is not null).Select(d => d.JudgeTime!.Value.TotalMilliseconds).Order().ToArray();
        text += $"; last {s.Decisions.Length} pause{(s.Decisions.Length == 1 ? "" : "s")}: {complete} finished, {unfinished} unfinished, {plain} left to the pause";
        if (times.Length > 0) text += $", judge median {times[times.Length / 2]:0} ms";
        var last = s.Decisions[^1];
        return text + $"; last: {last.Outcome} after {last.Silence.TotalMilliseconds:0} ms of silence.";
    }

    private void ShowTurnJudge()
    {
        if (turnJudgeStatus is { } line && conversation is not null) line.Text = TurnJudgeText(Talk.JudgeTurns, conversation.TurnJudgeStatus);
    }

    // Companion › Listening's Start replies early status line while the page shows it (it follows each reply started early).
    private TextBlock? earlyRepliesStatus;

    internal const string EarlyRepliesAbout = "Martlet starts its reply in the short pause after you speak and keeps it to itself " +
        "until your turn ends, so it answers sooner. It needs Parakeet on this PC as Listening, and with a cloud model a dropped " +
        "start can cost a little, so cloud models need the second box.";

    /// <summary>Companion › Listening's Start replies early status (TalkEarlyRepliesStatus): whether it is on, whether replies
    /// can start early with this setup (or why not) and what became of the newest ones (never words or audio).</summary>
    internal static string EarlyRepliesText(TalkPreferences prefs, LiveConversationConfiguration? configured,
        IReadOnlyList<Martlet.Conversation.EarlyReplyRecord> records)
    {
        if (!prefs.EarlyReplies) return "Off: Martlet starts each reply once you finished talking.";
        if (configured is not null && (!configured.LocalStt() || configured.SttHostTarget() is not null))
            return "On, but replies start early only with Parakeet on this PC as Listening.";
        var (thinking, _, why) = LiveConversationController.EarlyAllowed(prefs.EarlyReplyOptions, configured);
        if (configured is not null && !thinking) return $"On, but not with this setup: {why}.";
        var text = configured is null ? "On" : $"On: {why}";
        if (records.Count == 0) return text + (configured is null ? ", and" : ";") + " no replies started early yet.";
        var taken = records.Count(r => r.Outcome == Martlet.Conversation.EarlyReplyRecord.Promoted);
        var talkedOn = records.Count(r => r.Outcome == Martlet.Conversation.EarlyReplyRecord.Cancelled);
        var other = records.Count - taken - talkedOn;
        text += $"; last {records.Count}: {taken} taken as the reply, {talkedOn} let go because you went on talking" +
            (other > 0 ? $", {other} let go for another reason" : "");
        var last = records[^1];
        return text + $"; last: {last.Outcome} after {last.Waited.TotalMilliseconds:0} ms.";
    }

    private void ShowEarlyReplies()
    {
        if (earlyRepliesStatus is { } line && conversation is not null)
            line.Text = EarlyRepliesText(Talk, conversation.Configuration, conversation.EarlyReplies);
    }

    private static readonly string[] ChattinessChoices = ["Quiet", "Normal", "Chatty", "Martlet decides"];
    internal const string WordCheckAbout = "Martlet ignores sounds that aren't words (mm, a cough, laughter) and the words " +
        "speech-to-text makes up from noise, so they never get a reply. Relaxed ignores more, Sensitive also takes single short " +
        "words, and what Martlet ignored shows faded in the talk window.";
}
