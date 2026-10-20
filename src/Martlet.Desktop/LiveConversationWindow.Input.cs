using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Martlet.Audio;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Reading;
using Martlet.Core.Settings;
using Martlet.Diagnostics;
using Martlet.Providers;

namespace Martlet.Desktop;

// Typing, push-to-talk and the header buttons.
public partial class LiveConversationWindow
{
    // ---------- typing ----------

    private void Input_Changed(object sender, TextChangedEventArgs e)
    {
        if (InputText.Text.Length > 0)
        {
            warmup?.Touch();
            // Typing counts as talking to Martlet: an image model of its own describes the newest picture meanwhile.
            typedAt = clock.GetTimestamp();
        }
        RenderActions();
    }

    /// <summary>Puts the "Message Martlet" hint where typed text starts. A TextBox applies its padding inside its content host
    /// as well as through the theme's template, so the caret's own position is the only reliable offset.</summary>
    private void Input_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (InputText.Text.Length > 0) return;
        var start = InputText.GetRectFromCharacterIndex(0);
        if (start.IsEmpty) return;
        Placeholder.Margin = new Thickness(start.Left, start.Top, start.Left, 0);
    }

    private void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;
        e.Handled = true;
        Send();
    }

    private void Send_Click(object sender, RoutedEventArgs e) => Send();

    private void Send()
    {
        var text = InputText.Text.Trim();
        if (text.Length == 0 || pendingText is not null || !Available) return;
        notice = null;
        pendingText = text;
        pendingMessage = Add(ChatRole.User, text, "You");
        activityAt = clock.GetTimestamp();
        reportHeld = false;
        InputText.Clear();
        follow = true;
        Pump();
        RenderActions();
    }

    // ---------- push-to-talk ----------

    private bool StartPushToTalk()
    {
        if (closed || !Available) return false;
        // Pressing talk while Martlet sings ends the song musically; you talk as it rings out.
        controller.StopSong(musical: true, "the talk button");
        // You come first: talking stops a remark about your screen or a reply that is still playing.
        if (commentary is { OwnershipReleased: false } glance)
        {
            controller.Stop(glance, "commentary.interrupted", keepContext: true);
            notice = "Martlet stopped its remark; hold to talk again.";
            return false;
        }
        if (owned is { OwnershipReleased: false } live)
        {
            controller.Stop(live, "conversation.interrupted", keepContext: true);
            notice = "Martlet stopped; hold to talk again.";
            return false;
        }
        return StartTurn(null);
    }

    private void Ptt_Click(object sender, RoutedEventArgs e)
    {
        if (Recording) Release();
        else StartPushToTalk();
        RenderActions();
    }

    private void Ptt_Down(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        mouseHeld = true;
        if (!StartPushToTalk()) { mouseHeld = false; RenderActions(); return; }
        if (!PttButton.CaptureMouse()) Discard("conversation.focus_lost");
    }

    private void Ptt_Up(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (mouseHeld) Release();
    }

    private void Ptt_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space) return;
        e.Handled = true;
        if (!e.IsRepeat && !keyHeld)
        {
            keyHeld = true;
            if (!StartPushToTalk()) { keyHeld = false; RenderActions(); }
        }
    }

    private void Ptt_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space) return;
        e.Handled = true;
        if (keyHeld) Release();
    }

    private void Ptt_LostCapture(object sender, MouseEventArgs e) { if (mouseHeld) Discard("conversation.focus_lost"); }
    private void Ptt_LostFocus(object sender, KeyboardFocusChangedEventArgs e) { if (keyHeld) Discard("conversation.focus_lost"); }

    private void Release()
    {
        mouseHeld = keyHeld = false;
        PttButton.ReleaseMouseCapture();
        owned?.ReleasePress();
        RenderActions();
    }

    // A held recording that lost the button is thrown away, never sent.
    private void Discard(string reason)
    {
        mouseHeld = keyHeld = false;
        PttButton.ReleaseMouseCapture();
        if (owned is { OwnershipReleased: false, HandsFree: false } live) controller.Stop(live, reason, keepContext: true);
        RenderActions();
    }

    // ---------- the header buttons ----------

    // Start listening / Stop listening.
    private void Mic_Click(object sender, RoutedEventArgs e)
    {
        notice = null;
        if (!listenPaused)
        {
            listening = false;
            listenPaused = true;
            StopListening(keepHeard: true);
        }
        else
        {
            listenRetryAt = 0;
            if (MicrophoneUsable)
            {
                pausedWith = null;
                listenPaused = false;
                listening = true;
            }
            else notice = ListeningProblem();
        }
        RenderActions();
    }

    /// <summary>Why always listening can't start, with where to fix it.</summary>
    private string ListeningProblem() =>
        controller.Configuration?.Unavailable(Voice, true) is { } why
            ? Lead("Martlet can't listen yet", why)
            : "Martlet can't listen yet; check Companion › Listening.";

    // Start watching / Stop watching. Watching that can't start (a text-only model, no camera chosen) says why and stays off.
    private void Vision_Click(object sender, RoutedEventArgs e)
    {
        notice = null;
        if (!watchPaused)
        {
            watchPaused = true;
            if (watching) StopWatching(null);
        }
        else
        {
            pausedWith = null;
            watchPaused = false;
            StartWatching();
        }
        RenderActions();
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => StopAll("conversation.canceled");
}
