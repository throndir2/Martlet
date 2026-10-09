using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Hosting;
using Martlet.Conversation;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Touch zones' voice sounds: a zone's reaction list can hold "sound:&lt;cue&gt;" entries (a gasp, a giggle, a sigh)
/// that Martlet's own voice makes alone when the zone is touched (<see cref="LiveConversationController.PlayVoiceSound"/>). Add a
/// reaction offers the sounds the voice replies speak with makes; the page says which voice that is, or that it makes none (the
/// entries then stay and don't play), and what the last sound did. The sounds the zones use are made in the background for the
/// voice, so a touch plays one at once.</summary>
public partial class MainWindow
{
    private string? wantedVoiceSounds;
    private TextBlock? touchZonesVoiceSounds;

    private void WireVoiceSounds()
    {
        if (conversation is null) return;
        conversation.WantedVoiceSounds = ZoneVoiceSounds;
        conversation.VoiceSoundsChanged += () => Dispatcher.InvokeAsync(ShowVoiceSounds);
        // A zone list that gains a sound makes it for the voice now (a free voice only), not at the first touch.
        characterTouchZones.Changed += () =>
        {
            var wanted = ZoneVoiceSounds();
            var signature = string.Join("|", wanted);
            if (Interlocked.Exchange(ref wantedVoiceSounds, signature) == signature || wanted.Count == 0) return;
            conversation.MakeVoiceSounds(wanted, click: false);
        };
    }

    /// <summary>The voice sounds (cues) the zones in use list now, each once.</summary>
    private IReadOnlyList<string> ZoneVoiceSounds() =>
        [.. (characterTouchZones.Current?.Zones ?? []).Where(z => z.Enabled).SelectMany(z => z.Reaction.Actions ?? [])
            .Select(VoiceSounds.CueOf).OfType<string>().Distinct(StringComparer.Ordinal)];

    /// <summary>The voice replies speak with, from the settings (a talk window need not be open): its engine, and whether there
    /// is a voice at all.</summary>
    private (SpeechEngine? Engine, bool Voice) TouchVoice()
    {
        var tts = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Tts && r.Enabled != false);
        return (VoiceSounds.EngineOf(tts), tts is not null);
    }

    /// <summary>Add a reaction's voice sounds: each sound the voice makes alone, "Laugh  ·  sound".</summary>
    private IEnumerable<(string Id, string Label)> VoiceSoundItems() =>
        VoiceSounds.Supported(TouchVoice().Engine).Select(tag => (VoiceSounds.Entry(tag.Cue), $"{VoiceSounds.Label(tag.Cue)}  \u00b7  sound"));

    /// <summary>The page's voice sounds line (TouchZonesVoiceSounds).</summary>
    private TextBlock VoiceSoundsNote()
    {
        var note = Note(VoiceSoundsText(), new Thickness(0, 4, 0, 6));
        AutomationProperties.SetAutomationId(note, "TouchZonesVoiceSounds");
        touchZonesVoiceSounds = note;
        return note;
    }

    private void ShowVoiceSounds()
    {
        if (!closing && touchZonesVoiceSounds is { } note) note.Text = VoiceSoundsText();
    }

    /// <summary>Which voice makes the sounds and which, or why none plays; what is being made or went wrong; and what the last
    /// sound did.</summary>
    private string VoiceSoundsText()
    {
        var (engine, voice) = TouchVoice();
        var sounds = VoiceSounds.Supported(engine);
        var text = VoiceSounds.NoSoundsReason(engine, voice) is { } none
            ? none + " Sounds already in a zone's list stay there for a voice that makes them."
            : $"Voice sounds: {engine!.Name} makes {string.Join(", ", sounds.Select(t => t.Cue))}. Add one with Add a reaction: Martlet's " +
              "own voice makes it when the zone is touched, never while Martlet speaks or you talk." +
              (engine.Cloud ? $" {engine.Name} is paid, so a sound is made only when you click \u25B6 on it." : "");
        if (conversation?.VoiceSoundsNow is { } status && status.Voice is not null)
        {
            if (status.Making.Count > 0) text += $" Making {string.Join(", ", status.Making)}...";
            if (status.Problem is { } problem) text += $" Last problem: {problem}.";
            if (status.Last is { } last) text += $" Last sound: {last}.";
        }
        return text;
    }

    /// <summary>A touch on <paramref name="zone"/> asks for one of its voice sounds (<paramref name="cues"/>), from the renderer's
    /// request relay: it goes on on the UI thread.</summary>
    private void PlayTouchSoundsLater(CharacterTouchZone zone, IReadOnlyList<string> cues, string reason) =>
        Dispatcher.InvokeAsync(() => PlayTouchSounds(zone, cues, reason));

    /// <summary>A touch on <paramref name="zone"/> asks for one of its voice sounds (<paramref name="cues"/>): it plays when
    /// Martlet isn't speaking, you aren't talking and Speak Martlet's replies aloud is on. Without a talk window yet, the
    /// conversation starts in the background so the voice is known by the next touch. Returns what happened, in words, or null
    /// when Martlet can't talk here.</summary>
    private string? PlayTouchSounds(CharacterTouchZone zone, IReadOnlyList<string> cues, string reason)
    {
        if (cues.Count == 0 || closing || Role == DeviceRole.Host || conversation is null) return null;
        if (conversation.Configuration is null && ConversationSession() is { IsVisible: false } talk) talk.StartInBackground();
        var decision = conversation.PlayVoiceSound(zone.Id, cues, reason, openConversation?.UserTalking == true, Talk.SpeakReplies);
        ShowVoiceSounds();
        return decision.Why;
    }

    /// <summary>▶ on a voice sound in a zone's list: plays it now (made first when needed, also with a paid voice: one short
    /// request), and says what happens on the page's voice sounds line.</summary>
    private void HearTouchSound(string cue)
    {
        if (closing || Role == DeviceRole.Host || conversation is null) return;
        if (conversation.Configuration is null && ConversationSession() is { IsVisible: false } talk) talk.StartInBackground();
        var said = !Talk.SpeakReplies ? "Speak Martlet's replies aloud is off, so voice sounds don't play." : conversation.HearVoiceSound(cue);
        ErrorLog.Info($"Voice sound: Hear it on {cue}: {said}");
        if (touchZonesVoiceSounds is { } note) note.Text = VoiceSoundsText() + " " + said;
    }
}
