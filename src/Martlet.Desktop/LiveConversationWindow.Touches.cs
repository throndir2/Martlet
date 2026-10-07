using Martlet.Conversation;

namespace Martlet.Desktop;

/// <summary>What the user does to the desktop character, in the conversation: every touch on a zone Martlet notices (and what
/// the strokes and window log add) goes into the controller's <see cref="TouchLedger"/>. A reply to the user takes it all in
/// the notes of their message; touches on their own start a short reply of their own once they stop (<see cref="TouchDebounce"/>),
/// unless the user starts talking or typing first. Nothing here ever holds up what the user says.</summary>
public partial class LiveConversationWindow
{
    private readonly TouchDebounce touchDebounce = new();
    private string? touchStatus, touchLast, touchShown;
    private bool touchCanceled;

    /// <summary>Raised on the UI thread when what waits (and when a touch reply starts) or the last thing touches did changes:
    /// (waiting, last).</summary>
    internal event Action<string, string?>? TouchStatusChanged;

    /// <summary>What waits now and when a touch-only reply would start (MCP reads it as TouchZonesNoticed).</summary>
    internal string TouchStatus => touchStatus ?? NoTouches;

    /// <summary>What the last touches did: which reply carried them and what it was told (TouchZonesNoticedLast).</summary>
    internal string? TouchLast => touchLast;

    private const string NoTouches = "Nothing waits for Martlet.";

    /// <summary>The user did something to the desktop character (a touch on a zone Martlet notices, a stroke, moving it...). It
    /// waits for the next reply; one that may start a reply of its own (<see cref="PhysicalKinds.StartsTurn"/>) starts or
    /// moves on the short wait for it.</summary>
    internal void Physical(PhysicalEvent physical)
    {
        if (closed) return;
        controller.Touches.Record(physical);
        if (PhysicalKinds.StartsTurn(physical.Kind))
        {
            touchDebounce.Touched(physical.At);
            touchCanceled = false;
        }
        RenderTouches();
    }

    // A reply's request carried touches: say which and what it was told (off the UI thread).
    private void TouchesSent(bool touchOnly, TouchBurst burst, string? told) => Dispatcher.BeginInvoke(() =>
    {
        if (closed) return;
        touchLast = $"{DateTime.Now:T}: " + (touchOnly ? "a touch reply started" : "went with your message") +
            $" ({burst.HistoryLine}). Martlet was told: {told ?? burst.Line}";
        if (touchOnly) AddNote($"You touched Martlet {burst.HistoryLine}");
        RenderTouches();
    });

    /// <summary>Starts the short reply to touches on their own once they are due: Martlet is free, nobody talks or types, nothing
    /// else waits for it, and it isn't paused. The user talking or typing meanwhile cancels it; their reply takes the touches.</summary>
    private bool TryTouch()
    {
        if (!touchDebounce.Waiting || UserBusy || InputText.Text.Length > 0) return false;
        if (closed || !Available || Paused || operations.IsRunning || owned is { OwnershipReleased: false } ||
            commentary is { OwnershipReleased: false } || !touchDebounce.Due(controller.TouchNow))
            return false;
        try
        {
            var started = controller.StartTouch(Voice);
            touchDebounce.Started(controller.TouchNow);
            if (started is null) return false;
            owned = started;
            yielded = null;
            notice = null;
            answeredAt = clock.GetTimestamp();
            Observe();
            return true;
        }
        catch (LiveActionException error) when (error.Code is "conversation.ownership_busy" or "conversation.controls_blocked") { return false; }
        catch (Exception error) when (error is LiveActionException or Martlet.Core.Contracts.ContractException)
        {
            touchDebounce.Cancel();
            ErrorLog.Warn($"Touches: couldn't start a touch reply ({(error as LiveActionException)?.Code ?? "invalid input"}); they wait for your next message.");
            return false;
        }
        finally { RenderTouches(); }
    }

    // On every tick, whatever else the conversation does: the user talking (their voice began) or typing cancels a touch reply
    // that waits, so theirs takes the touches.
    private void WatchTouches()
    {
        if (touchDebounce.Waiting && (UserBusy || InputText.Text.Length > 0))
        {
            touchDebounce.Cancel();
            touchCanceled = true;
        }
        RenderTouches();
    }

    // The status MCP reads; raised only when it changes.
    private void RenderTouches()
    {
        var waiting = controller.Touches.Peek(controller.TouchNow);
        string status;
        if (waiting is null) status = NoTouches;
        else if (touchDebounce.Waiting && !Available)
            status = $"Waiting: {waiting.Line} A touch reply would start about {TouchDebounce.Quiet.TotalSeconds:0.#} s after the last touch, " +
                "but Martlet can't reply right now (set up Thinking, or unlock Windows), so it waits for your next message.";
        else if (touchDebounce.DueAt is { } due)
        {
            var at = DateTime.Now + (due - controller.TouchNow);
            status = $"Waiting: {waiting.Line} A touch reply starts at about {at:HH:mm:ss.f}" +
                (operations.IsRunning || owned is { OwnershipReleased: false } ? " or once Martlet is free" : "") +
                ", unless you talk or type first.";
        }
        else status = $"Waiting for your next message{(touchCanceled ? " (you started talking or typing)" : "")}: {waiting.Line}";
        if (status == touchStatus && ReferenceEquals(touchLast, touchShown)) return;
        touchStatus = status;
        touchShown = touchLast;
        TouchStatusChanged?.Invoke(status, touchLast);
    }
}
