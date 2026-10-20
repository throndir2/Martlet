using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Martlet.Core.Settings;
using Martlet.Providers;
using static Martlet.Conversation.CheckIns;
using static Martlet.Conversation.CheckInPrompts;

namespace Martlet.Conversation;

/// <summary>When a check-in runs (docs/CONVERSATION.md#check-ins): why it waits now (<see cref="Wait"/>), its pace and the
/// state it may act on. Its settings and the built-in check-ins are <see cref="CheckIns"/>.</summary>
public static class CheckInSchedule
{
    /// <summary>Why <paramref name="checkIn"/> doesn't run now, in a few plain words, or null when it may. With
    /// <paramref name="now"/> (the owner's Check now) it runs whether it is on, due or settled, as long as it has something to
    /// check. <paramref name="last"/> is its last run on this PC. Every check-in waits the same way: for its pace, for someone to
    /// use this PC, for its prompt (<paramref name="prompts"/>), for each of its <see cref="CheckIn.Conditions"/>, for the sound it
    /// records and for the conversation to settle. A check-in with <see cref="CheckIn.Triggers"/> also waits for one of them to
    /// fire (<paramref name="fired"/>, not older than <see cref="TriggerAge"/>), its pace is then its cooldown, and it doesn't
    /// wait for the conversation to settle: the touches that fired it, and the reaction to them, never hold it up.</summary>
    public static string? Wait(CheckIn checkIn, CheckInState state, CheckInRun? last, bool now = false, PromptSettings? prompts = null,
        CheckInTrigger? fired = null)
    {
        ArgumentNullException.ThrowIfNull(checkIn);
        ArgumentNullException.ThrowIfNull(state);
        if (!now && !checkIn.On) return "it's off";
        var triggered = !now && checkIn.Triggers != CheckInTriggers.None;
        if (triggered && !Fires(checkIn, fired, state.Now)) return "it waits for " + TriggerWords(checkIn.Triggers);
        var pace = Pace(checkIn, last, state.Exchanged);
        if (!now && last is not null && state.Now - last.At < pace)
            return "next in " + Reminders.Span(last.At + pace - state.Now);
        if (!now && state.Away is { } away && away > Idle) return $"nobody used this PC for {Reminders.Span(Idle)}";
        if (string.IsNullOrWhiteSpace(Template(checkIn, prompts))) return "its prompt is empty";
        if (checkIn.Outcome == CheckInOutcome.Tools && checkIn.ToolSets.Count == 0) return "it has no tools to use";
        var when = checkIn.Conditions;
        if (when.HasFlag(CheckInConditions.CharacterShows) && !state.CharacterShows) return "the character isn't showing";
        if (when.HasFlag(CheckInConditions.EmoteShown) && state.Emotes.Count == 0) return "no emote a reply turned on is showing";
        if (when.HasFlag(CheckInConditions.GazeChosen) && state.Gaze is null) return "the eyes do their usual";
        if (when.HasFlag(CheckInConditions.Talked) && state.Exchanges.Count == 0) return "nothing was said in a conversation here yet";
        if (when.HasFlag(CheckInConditions.Persona) && string.IsNullOrWhiteSpace(state.Persona)) return "no personality is active to compare with";
        if (when.HasFlag(CheckInConditions.Replies) && Replies(state).Count < 2) return "it needs at least 2 replies to read";
        if (when.HasFlag(CheckInConditions.Sayings) && Said(state).Count < RepeatsSayings)
            return $"it needs at least {RepeatsSayings} things Martlet said in the last hour";
        if (checkIn.Recording == CheckInRecording.Microphone && !state.HearsMicrophone)
            return checkIn.On ? "Martlet doesn't hear the microphone now" : "Martlet keeps the microphone only for a check-in that's on";
        if (checkIn.Recording == CheckInRecording.PcSound && !state.HearsPc)
            return checkIn.On ? "Martlet doesn't hear what this PC plays now" : "Martlet keeps what this PC plays only for a check-in that's on";
        if (!now)
        {
            if (when.HasFlag(CheckInConditions.EmoteShown) && !state.Emotes.Any(e => e.Shown >= MinimumShown))
                return $"no emote a reply turned on has shown for {Reminders.Span(MinimumShown)} yet";
            if (when.HasFlag(CheckInConditions.GazeChosen) && state.Gaze is { } gaze && gaze.Since < MinimumShown)
                return $"the gaze a reply chose is less than {Reminders.Span(MinimumShown)} old";
            if (when.HasFlag(CheckInConditions.SomethingNew) && last is not null && state.Exchanged <= last.Exchanged)
                return "nothing new was said since the last check";
            if (when.HasFlag(CheckInConditions.Talked) && state.Quiet is { } still && still > PromiseWindow)
                return $"the conversation has been quiet for more than {Reminders.Span(PromiseWindow)}";
            if (when.HasFlag(CheckInConditions.NewReplies) && state.Exchanged - (last?.Exchanged ?? 0) < CharacterReplies)
                return $"it waits for {CharacterReplies} new replies";
        }
        if (WaitForSignals(checkIn, state, last, now) is { } signal) return signal;
        return !now && !triggered && state.Quiet is { } quiet && quiet < Settle ? "the conversation is busy" : null;
    }

    /// <summary>How long <paramref name="checkIn"/> waits after <paramref name="last"/> while the conversation has had
    /// <paramref name="exchanged"/> exchanges: its pace, or <see cref="KeptPace"/> times that with
    /// <see cref="CheckInConditions.SlowWhenKept"/> after an answer that kept everything with nothing new said since.</summary>
    public static TimeSpan Pace(CheckIn checkIn, CheckInRun? last, long exchanged) =>
        checkIn.Every * (last is { Kept: true } && last.Exchanged == exchanged && checkIn.Conditions.HasFlag(CheckInConditions.SlowWhenKept) ? KeptPace : 1);

    /// <summary>The state narrowed to what <paramref name="checkIn"/> may act on: with <see cref="CheckInConditions.EmoteShown"/>
    /// it looks only at the emotes shown for <see cref="MinimumShown"/> (all of them when the owner asks <paramref name="now"/>),
    /// oldest first.</summary>
    public static CheckInState Focus(CheckIn checkIn, CheckInState state, bool now = false) =>
        checkIn.Conditions.HasFlag(CheckInConditions.EmoteShown)
            ? state with { Emotes = [.. state.Emotes.Where(e => now || e.Shown >= MinimumShown).OrderByDescending(e => e.Shown)] }
            : state;
}
