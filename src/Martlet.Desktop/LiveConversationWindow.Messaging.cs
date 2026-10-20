using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Threading;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Diagnostics;
using Martlet.Providers;

namespace Martlet.Desktop;

// Messages from paired chats (Companion › Messaging).
public partial class LiveConversationWindow
{
    // ---------- messages from paired chats (Companion › Messaging) ----------

    /// <summary>A message from a paired chat in a messaging app: it shows in the history as from <paramref name="from"/>, waits
    /// for Martlet like typed text and gets the reply's text (or why there is none). It is text only: no screen picture goes with
    /// it, and the reply is said aloud only with <paramref name="speak"/> while Windows isn't locked. <paramref name="origin"/>
    /// (the app, chat and message) and <paramref name="speaker"/> go to the record of conversations with it.</summary>
    internal Task<string> AskFromMessage(string text, string from, bool speak, CancellationToken token,
        Martlet.Conversation.HistorySource? origin = null, string? speaker = null)
    {
        if (closed) return Task.FromResult(RemoteCantAnswer);
        var ask = new RemoteAsk(text, Add(ChatRole.User, text, from), speak,
            new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously), origin, speaker);
        var registration = token.Register(() => Dispatcher.BeginInvoke(() =>
        {
            if (remoteQueue.Remove(ask)) ask.Bubble.AddNote("Not answered in time.");
            ask.Reply.TrySetCanceled(token);
        }));
        ask.Reply.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        remoteQueue.Add(ask);
        activityAt = clock.GetTimestamp();
        reportHeld = false;
        follow = true;
        Pump();
        RenderActions();
        return ask.Reply.Task;
    }

    /// <summary>Messages from paired chats waiting for Martlet.</summary>
    internal int RemoteWaiting => remoteQueue.Count;

    private const string RemoteCantAnswer = "Martlet can't talk right now, so try again in a moment.";

    private bool TryRemote()
    {
        while (remoteQueue.Count > 0)
        {
            var ask = remoteQueue[0];
            remoteQueue.RemoveAt(0);
            if (ask.Reply.Task.IsCompleted) continue;
            if (ready && controller.Configuration is null)
            {
                ask.Bubble.AddNote("Not answered: Thinking isn't set up.");
                ask.Reply.TrySetResult("Martlet isn't set up to think yet, so set up Thinking in Companion on your PC.");
                continue;
            }
            try
            {
                owned = controller.Start(ask.Text, ask.Speak && !locked && Voice, microphone: false, approved: true, remote: true,
                    origin: ask.Origin, originSpeaker: ask.Speaker);
                remoteAnswering = (owned, ask);
                typed = (owned, ask.Bubble);
                yielded = null;
                answeredAt = clock.GetTimestamp();
                pacer?.NoteConversation();
                Observe();
                return true;
            }
            catch (LiveActionException error) when (error.Code == "conversation.ownership_busy")
            {
                remoteQueue.Insert(0, ask);
                return false;
            }
            catch (Exception error) when (error is LiveActionException or ContractException)
            {
                var why = error is LiveActionException live ? Remedy(live.Code) : Remedy("conversation.invalid_input");
                ask.Bubble.AddNote("Not answered.");
                ask.Reply.TrySetResult("Martlet couldn't answer: " + why);
            }
        }
        return false;
    }

    private void FailRemote(string why)
    {
        foreach (var ask in remoteQueue)
        {
            ask.Bubble.AddNote("Not answered.");
            ask.Reply.TrySetResult(why);
        }
        remoteQueue.Clear();
    }

    // A finished turn that answered a message from a chat sends its text back (or why there is none).
    private void FinishRemote(LiveConversationOperation done)
    {
        if (remoteAnswering is not { } answering || !ReferenceEquals(answering.Operation, done)) return;
        remoteAnswering = null;
        var content = done.Turn?.Content;
        var text = content?.Text?.Trim() is { Length: > 0 } said ? said : content?.Refusal?.Trim() ?? "";
        if (text.Length > 0) answering.Ask.Reply.TrySetResult(text);
        else answering.Ask.Reply.TrySetResult(Outcome(done) is { } why ? "Martlet couldn't answer: " + why : "(Martlet had nothing to say.)");
    }
}
