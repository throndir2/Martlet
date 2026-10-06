using Martlet.Discord.Calls;

namespace Martlet.Desktop;

/// <summary>Martlet in your own Discord calls (<see cref="DiscordCallService"/>), in the conversation: while the call mode is
/// on, always listening also hears the call (the Discord app alone where Windows allows it) as a group conversation. Each line
/// heard is named after who spoke it (the Discord window's speaking indicators, read on this PC) and goes to Thinking marked as
/// the PC's, never as you; a line that says Martlet's name is answered without waiting its turn, the rest only now and then
/// (Thinking may pass). Someone in the call talking over Martlet stops it. Off, nothing here changes the conversation.</summary>
public partial class LiveConversationWindow
{
    /// <summary>The Discord call mode's service; null where there is none (tests), which is the same as off.</summary>
    internal DiscordCallService? Calls { get; init; }

    /// <summary>How long someone in the call must be talking over Martlet before it stops.</summary>
    internal static TimeSpan CallBargeInAfter => TimeSpan.FromMilliseconds(700);

    private bool CallOn => Calls?.Preferences.On == true;
    private long callTalkingSince;
    // A line from the call that says Martlet's name waits in the queue: it goes to Thinking as soon as the call pauses.
    private bool callAddressed;

    /// <summary>Every tick while the call mode is on: the PC listener opens again when what it should hear changed (Discord
    /// started or quit, or the mode was turned on or off), and who is speaking is sampled while the call is heard.</summary>
    private void FollowCall()
    {
        if (Calls is not { } calls) return;
        // The call mode turned off while only it kept the call's listener running.
        if (pcListener is not null && !preferences.HearPc && !CallOn)
        {
            StopPcListening(keepHeard: false);
            return;
        }
        if (pcListener is not null && clock.GetElapsedTime(callCheckedAt) >= TimeSpan.FromSeconds(5))
        {
            callCheckedAt = clock.GetTimestamp();
            if (calls.CaptureOutdated(listening)) StopPcListening(keepHeard: true);
        }
        if (CallOn) calls.Attribution.Tick(pcListener is { Hearing: true }, clock);
    }

    private long callCheckedAt;

    /// <summary>A line heard on the PC, as it shows and goes to Thinking: in the call mode, named after who said it.</summary>
    private (string Text, string Label) CallLine(string text)
    {
        if (!CallOn || Calls is not { } calls) return (text, "Playing on this PC");
        var line = DiscordCallLine.Format(calls.Attribution.Take(), text);
        if (controller.Configuration is { } configured && DiscordCallLine.Addressed(text, configured.CompanionNames.Names)) callAddressed = true;
        return (line, "Discord call");
    }

    /// <summary>Someone in the call said Martlet's name: what the call said goes to Thinking once the call pauses, without
    /// waiting <see cref="PcPace"/>.</summary>
    private bool CallDue() => callAddressed && pcListener is not { Hearing: true } && pcListener is not { Transcribing: > 0 };

    private void CallAnswered() => callAddressed = false;

    /// <summary>Someone in the call talks over Martlet for <see cref="CallBargeInAfter"/>: Martlet stops, keeping what it said
    /// so far in context, and what they say is heard next.</summary>
    private void CallBargeIn()
    {
        if (!CallOn || Calls?.Preferences.BargeIn != true || pcListener is not { Hearing: true })
        {
            callTalkingSince = 0;
            return;
        }
        if (callTalkingSince == 0) callTalkingSince = clock.GetTimestamp();
        if (clock.GetElapsedTime(callTalkingSince) < CallBargeInAfter) return;
        if (owned is { OwnershipReleased: false } speaking && !ReferenceEquals(yielded, speaking) && Speaking(speaking))
        {
            yielded = speaking;
            controller.Stop(speaking, "conversation.interrupted", keepContext: true);
            ErrorLog.Info($"Barge-in: someone in the Discord call talked over Martlet for {CallBargeInAfter.TotalMilliseconds:0} ms, so it stopped its reply.");
        }
    }

    /// <summary>The talk window's line about the call while the mode is on (null otherwise).</summary>
    private (string Line, string Detail)? CallStatus()
    {
        if (!CallOn || Calls is not { } calls) return null;
        if (!controller.CanHearPc) return ("Martlet can't hear your Discord call here.", "");
        if (!listening) return ("In your Discord call once you start listening.", "");
        if (pcProblem is { } problem) return (problem + " Martlet keeps trying.", "");
        if (pcListener is null) return ("Getting ready to hear your Discord call…", "");
        return (pcListener.Hearing ? "Hearing someone in your Discord call…" : "In your Discord call.", calls.Status(listening));
    }
}
