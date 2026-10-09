using Martlet.Avatar.Hosting;
using Martlet.Conversation;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Touch zones' voice sounds: a zone's reaction list can hold "sound:&lt;cue&gt;" entries (a gasp, a giggle, a sigh)
/// that Martlet's own voice makes alone when the zone is touched (<see cref="LiveConversationController.PlayVoiceSound"/>). The
/// sounds the zones use are made in the background for the voice replies speak with, so a touch plays one at once.</summary>
public partial class MainWindow
{
    private string? wantedVoiceSounds;

    private void WireVoiceSounds()
    {
        if (conversation is null) return;
        conversation.WantedVoiceSounds = ZoneVoiceSounds;
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

    /// <summary>A touch on <paramref name="zone"/> asks for one of its voice sounds (<paramref name="cues"/>): it plays when
    /// Martlet isn't speaking, you aren't talking and Speak Martlet's replies aloud is on. Without a talk window yet, the
    /// conversation starts in the background so the voice is known by the next touch. Returns what happened, in words, or null
    /// when Martlet can't talk here.</summary>
    private string? PlayTouchSounds(CharacterTouchZone zone, IReadOnlyList<string> cues, string reason)
    {
        if (cues.Count == 0 || closing || Role == DeviceRole.Host || conversation is null) return null;
        if (conversation.Configuration is null && ConversationSession() is { IsVisible: false } talk) talk.StartInBackground();
        var decision = conversation.PlayVoiceSound(zone.Id, cues, reason, openConversation?.UserTalking == true, Talk.SpeakReplies);
        return decision.Why;
    }
}
