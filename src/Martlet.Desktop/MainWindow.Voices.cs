using System.IO;
using System.Media;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.HostClient;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Core.Voices;
using Martlet.F5;

namespace Martlet.Desktop;

/// <summary>Companion › Voice › Voices, the work: <see cref="VoiceCards"/> (Pages\Voice\VoiceCards.Voices.cs) draws the card, and
/// this file plays, adds, records, removes and chooses the voices Martlet speaks with. The list, the chosen voice and every
/// recording are shared with the owner's paired Martlet computers (through each paired host), so whichever computer speaks
/// already holds the recording and any of them can be the companion. Using a voice applies it in the F5 preset store and, when
/// a computer speaks, saves it on the speaking route.</summary>
public partial class MainWindow
{
    private SoundPlayer? voicePlayer;
    private bool retiredVoiceChecked;

    internal static string Capitalized(string text) => char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>Plays a voice's recording on Windows' default speakers: this PC's copy, or a starter clip Martlet carries.</summary>
    private async Task PlayVoiceAsync(VoiceItem item)
    {
        if (store is null || closing) return;
        try
        {
            var audio = await F5Voices.ReadAudioAsync(store.DataDirectory, item.Local, item.AudioSha256, lifetime.Token);
            voicePlayer?.Stop();
            voicePlayer = new SoundPlayer(new MemoryStream(audio));
            voicePlayer.Play();
            ActionText.Text = $"Playing '{item.Name}'...";
        }
        catch (OperationCanceledException) { }
        catch (F5Exception error) { ActionText.Text = F5Voices.Describe(error); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ActionText.Text = $"Couldn't play '{item.Name}': {error.Message}";
        }
    }

    private Task PlayAppliedAsync(F5ReferenceSnapshot? applied, string name) =>
        applied is null ? Task.CompletedTask : PlayVoiceAsync(new(applied.ReferenceRevision, name, null, null, applied.AudioSha256,
            applied.AudioFormat.DurationMilliseconds, applied.CreatedAtUtc, applied, applied.ReferenceRevision[..16], false));

    /// <summary>Makes a voice the one Martlet speaks with on all of the owner's computers. This PC applies it now (when a
    /// computer speaks, the speaking route keeps it, so the next conversation uses it); the others follow when they next
    /// share voices.</summary>
    private async Task UseVoiceAsync(string voiceId, string destination)
    {
        if (store is null || setupService is null || closing) return;
        ChangeTurns.Turn? turn = null;
        var token = lifetime.Token;
        try
        {
            turn = await ChangeTurnAsync();
            var (name, saved) = await SwitchVoiceAsync(voiceId, destination, token);
            ActionText.Text = saved
                ? $"Martlet now uses the voice '{name}' on all your computers.{OpenConversationFollows}"
                : $"'{name}' is your voice on all your computers.";
        }
        catch (OperationCanceledException) { }
        catch (F5Exception error) { ActionText.Text = F5Voices.Describe(error); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or JsonException)
        {
            ActionText.Text = error.Message;
        }
        finally
        {
            turn?.Dispose();
            if (!closing) RenderHome();
        }
    }

    /// <summary>Makes a voice the chosen one on all your computers and applies it here (on the speaking route too, when a
    /// computer speaks). The caller holds the change turn. Returns its name and whether the speaking route changed.</summary>
    private async Task<(string Name, bool Saved)> SwitchVoiceAsync(string voiceId, string destination, CancellationToken token)
    {
        var loaded = await setupService!.LoadAsync(token);
        if (loaded.Error is not null) throw new InvalidOperationException(loaded.Error.Summary);
        var route = loaded.Settings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Tts);
        var f5 = route is { RouteType: SetupRouteType.GatewayF5, GatewaySnapshot: not null } ? route : null;
        var result = await F5Voices.ReconcileAsync(store!.DataDirectory, destination, ClusterDevice, null, null, token);
        if (!result.Local.TryGetValue(voiceId, out var voice))
            throw new InvalidOperationException(result.Library.Find(voiceId) is { Removed: false } listed
                ? $"The voice '{listed.Name}' is still being copied to this PC. Try again in a moment."
                : "That voice is no longer in your voice list.");
        var engine = SpeechEngines.ForRoute(f5?.GatewaySnapshot!.RouteId) ?? SpeakingEngineChoice.Current;
        if (SpeechEngines.ReferenceProblem(engine, voice.AudioFormat.DurationMilliseconds,
                result.Library.Find(voiceId)?.ClipMilliseconds) is { } problem)
            throw new InvalidOperationException($"{problem} Add another recording of '{voice.PresetName}' or choose another engine.");
        var saved = await ApplyVoiceAsync(loaded, voice, token);
        F5Voices.Choose(store.DataDirectory, voiceId);
        QueueSpeakingVoiceSync();
        return (voice.PresetName, saved);
    }
}
