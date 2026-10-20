using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Threading;
using Martlet.Audio;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Diagnostics;
using Martlet.Providers;

namespace Martlet.Desktop;

// Singing: the song line and Stop.
public partial class LiveConversationWindow
{
    // ---------- singing ----------

    /// <summary>The song panel: the song playing (where it is, the line being sung, Stop singing) or the last one (where and why
    /// it stopped, or that it's ready for Martlet to offer). Only Martlet performs songs, so nothing here plays one. Its first
    /// line says it without the song's title or words (MCP reads it).</summary>
    private void RenderSong()
    {
        var songs = controller.Singing;
        var player = songs?.Player;
        var playing = player is { Active: true };
        var song = songs?.Current;
        if (songs is null || song is null && !playing)
        {
            SongPanel.Visibility = Visibility.Collapsed;
            return;
        }
        SongPanel.Visibility = Visibility.Visible;
        var last = songs.Last is { } stopped && stopped.SongId == song?.Id ? stopped : null;
        SongText.Text = SongLine(playing ? player : null, song, last);
        var title = playing ? player!.Song.Title : song!.Title;
        SongLineText.Text = playing && player!.Caption is { Length: > 0 } words && player.State != SongPlaybackState.Stopping
            ? $"“{title}” · {words}" : $"“{title}”";
        SongStopButton.Visibility = playing && player!.State != SongPlaybackState.Stopping ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The song panel's line, without the song's title or words: its ID, state, position and line number.</summary>
    internal static string SongLine(SongPlayer? playing, StoredSong? song, SongStopRecord? last)
    {
        if (playing is { } player)
        {
            var at = $"{SongClock.Of(player.Position)} of {SongClock.Of(player.Map.Duration)}";
            var line = player.Line is { } index
                ? $" · {(player.Map.Lines[index].Section is { Length: > 0 } section ? section + " " : "")}line {index + 1} of {player.Map.Lines.Count}" : "";
            return player.State switch
            {
                SongPlaybackState.Starting => $"Starting {player.Song.Id}…",
                SongPlaybackState.LeadIn => $"Starting {player.Song.Id} {player.Plan.Target.Describe}: " +
                    (player.Plan.Top ? "the intro" : $"{(player.Plan.LeadInBars == 1 ? "a bar" : $"{player.Plan.LeadInBars} bars")} of the band first") +
                    (player.Mixer.Vamps > 0 ? $", waiting for Martlet to finish talking ({player.Mixer.Vamps})" : "") + $" · {at}.",
                SongPlaybackState.Stopping => $"Stopping {player.Song.Id} at {SongClock.Of(player.Record?.At ?? player.Position)}: " +
                    (player.Mixer.Stopping?.Musical == true ? "finishing the word, then the band rings out on the beat." : "fading out."),
                _ => $"Singing {player.Song.Id} · {at}{line}" + (player.Mixer.Ducked ? " · turned down while Martlet talks." : ".")
            };
        }
        if (song is null) return "";
        if (last is { Ended: true, Cause: SongStopCause.Ended }) return $"Sang {song.Id} to the end.";
        if (last is { } stopped)
        {
            var where = stopped.Line is { } index
                ? $"{(stopped.Section is { Length: > 0 } section ? section + " " : "")}line {index + 1} of {stopped.Lines}" : "between lines";
            var why = stopped.Cause switch
            {
                SongStopCause.UserWords => "you asked Martlet to stop",
                SongStopCause.Button => $"you pressed {stopped.Reason ?? "Stop"}",
                SongStopCause.Martlet => "Martlet stopped",
                SongStopCause.Failed => "it couldn't play on this PC",
                _ => "another song started"
            };
            return $"Stopped {song.Id} at {SongClock.Of(stopped.At)} ({where}): {why}." +
                (stopped.Ended ? "" : " Ask Martlet to pick up where it left off.");
        }
        return $"Song {song.Id} is ready ({SongClock.Of(TimeSpan.FromSeconds(song.DurationSeconds))}, {song.Lines.Count} lines), and " +
            "Martlet will offer it.";
    }

    private void SongStop_Click(object sender, RoutedEventArgs e)
    {
        controller.StopSong(musical: true, "Stop singing");
        RenderActions();
    }

    // A screen remark hands the app slot over right away; a reply in progress finishes first.
    private void YieldSlot()
    {
        if (commentary is { OwnershipReleased: false } glance && !ReferenceEquals(yielded, glance))
        {
            yielded = glance;
            controller.Stop(glance, "commentary.interrupted", keepContext: true);
        }
    }

    private bool StartTurn(string? text)
    {
        bool microphone = text is null;
        // One moment: what this PC played meanwhile goes with what you type, each line marked as the PC's (never your words), and
        // the reply takes the look vision wanted. Finished work goes with it anyway.
        var playing = !microphone && PcWaiting ? TakePlaying(text!.Length) : [];
        var plan = MomentTurn.Plan(MomentTrigger.User, playing.Count > 0, JobsWaiting, LookDue);
        var (seen, about) = Sight(plan.Look);
        try
        {
            // Pressing Send or the talk button is the action; the destinations were chosen in Companion.
            owned = playing.Count == 0
                ? controller.Start(text, Voice, microphone, approved: true, localCaptureApproved: microphone, uploadApproved: microphone,
                    listening: microphone ? Listening(false) : null, seen: seen, chattiness: BackgroundChattiness, attention: about,
                    look: plan.Look)
                : controller.Start(PcMessage(playing.Select(entry => (entry.Text, true, entry.From?.Named))
                        .Append((text!, false, (IReadOnlyList<PcSource>?)null))), Voice, microphone: false,
                    approved: true, seen: seen, pcAudio: true, userWords: text, chattiness: BackgroundChattiness, attention: about,
                    look: plan.Look);
            yielded = null;
            notice = null;
            answeredAt = clock.GetTimestamp();
            pacer?.NoteConversation();
            TookLook(owned);
            Observe();
            return true;
        }
        catch (LiveActionException error) { notice = Remedy(error.Code); }
        catch (ContractException) { notice = Remedy("conversation.invalid_input"); }
        catch (VoiceIdentityException error) { notice = error.Message; }
        Requeue(playing);
        return false;
    }

    private ListeningOptions Listening(bool handsFree) => new(handsFree,
        new VoiceActivitySettings
        {
            Sensitivity = preferences.Sensitivity,
            EndSilence = TalkPreferences.Pauses[Math.Clamp(preferences.PauseIndex, 0, TalkPreferences.Pauses.Length - 1)]
        },
        preferences.VoiceId, HearsVoice.On, preferences.BargeIn, preferences.ReduceEcho, WordCheck: preferences.WordCheck,
        Straight: handsFree && HearsVoice.On && !preferences.TranscribeFirst, HearLocalOnly: HearsVoice.LocalOnly,
        BargeInStyle: preferences.BargeInStyle, JudgeTurns: preferences.JudgeTurns, Early: preferences.EarlyReplyOptions);

    /// <summary>What a reply started early goes with (Companion › Listening › Start replies early): what TryAnswer sends with
    /// the next thing you say while nothing else waits for a reply; null while anything else would go with it (words heard
    /// before, what this PC played, a picture of what vision watches, typed text, a message from a chat) or a reply runs, so
    /// no reply starts early then.</summary>
    private EarlyReplyPlan? EarlyPlanNow() =>
        preferences.EarlyReplies && listening && Available && heardQueue.Count == 0 && playingQueue.Count == 0 && pcHeld.Count == 0 &&
        pendingText is null && remoteQueue.Count == 0 && !LookDue && !PictureGoes &&
        owned is not { OwnershipReleased: false } && commentary is not { OwnershipReleased: false }
            ? new(Voice, BackgroundChattiness, CallOn) : null;

    // The newest picture of what vision watches would go with what you say now (SeenNow), or the image model's description of it.
    private bool PictureGoes => watching && latestFrame is not null && clock.GetElapsedTime(latestAt) <= SeenFreshness && controller.CanSee;

    // Whether a model hears your recording (Companion › Listening): your own choice, or never chosen, only while the recording
    // stays on this PC where it goes, to Thinking or to the audio model of its own (LocalOnly: the conversation checks that again
    // before it sends one).
    private (bool On, bool LocalOnly) HearsVoice => preferences.HearVoiceFor(controller.RecordingStaysOnThisPc());
}
