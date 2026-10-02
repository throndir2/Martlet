using System.IO;
using System.Media;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.F5;

namespace Martlet.Desktop;

/// <summary>Companion › Voice › Voices: the voices F5 copies from your recordings (no training). Add as many as you like,
/// hear them, switch between them in one click and remove the ones you no longer use. Switching applies the voice in the F5
/// preset store and, when F5 speaks, saves it on the speaking route.</summary>
public partial class MainWindow
{
    private SoundPlayer? voicePlayer;

    private Border VoicesCard(SetupRoute? route)
    {
        var f5 = route?.RouteType == SetupRouteType.GatewayF5 ? route : null;
        var destination = f5?.GatewaySnapshot?.DestinationId ?? F5Destination;
        var stack = new List<UIElement>
        {
            Heading("Voices"),
            Note("F5 speaks in the voice of any short recording you add, with no training. Add as many as you like and switch " +
                "between them any time. Martlet keeps each recording on this PC and sends it with each reply only to the computer " +
                "that speaks." + (f5 is null ? " The chosen voice speaks once F5 does the speaking, on this PC or another of your computers." : ""),
                new Thickness(0, 0, 0, 10))
        };
        IReadOnlyList<F5ReferenceSnapshot> voices = [];
        Guid? applied = null;
        try
        {
            if (store is not null) (voices, applied) = F5Voices.List(store.DataDirectory, destination);
        }
        catch (F5Exception error) { stack.Add(Note(F5Voices.Describe(error), new Thickness(0, 0, 0, 8))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            stack.Add(Note("Martlet couldn't read your voices: " + error.Message, new Thickness(0, 0, 0, 8)));
        }
        var chosen = f5?.Reference?.PresetId ?? applied;
        var mark = f5 is null ? "chosen" : "in use";
        if (!voices.Any(F5Voices.IsSample))
            stack.Add(VoiceRow(F5Voices.SampleName, "F5-TTS's published English sample voice (MIT licence), ready to use.", false, mark,
                () => PlayVoiceAsync(null, F5Voices.SampleName), () => UseVoiceAsync(null), remove: null));
        foreach (var voice in voices)
        {
            var inUse = voice.PresetId == chosen;
            var detail = F5Voices.IsSample(voice) ? "F5-TTS's published English sample voice (MIT licence)."
                : $"{voice.AudioFormat.DurationMilliseconds / 1000d:0.#} second recording, added {voice.CreatedAtUtc.ToLocalTime():d}.";
            stack.Add(VoiceRow(voice.PresetName, detail, inUse, mark, () => PlayVoiceAsync(voice, voice.PresetName), () => UseVoiceAsync(voice),
                inUse || voice.PresetId == applied ? null : () => RemoveVoiceAsync(voice)));
        }
        stack.Add(Row(PageButton("Add a voice...", () => AddVoiceAsync(destination).Forget(), primary: voices.Count == 0, id: "F5AddVoice")));
        return Card([.. stack]);
    }

    private UIElement VoiceRow(string name, string detail, bool inUse, string mark, Func<Task> play, Func<Task> use, Func<Task>? remove)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = name + (inUse ? $"  \u00b7  {mark}" : ""), FontSize = 15, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap });
        text.Children.Add(Note(detail, new Thickness(0, 2, 0, 0)));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Button Small(string label, Func<Task> run, string id)
        {
            var button = PageButton(label, () => run().Forget(), id: $"F5Voice{id}-{name}");
            button.MinWidth = 72;
            button.Margin = new Thickness(8, 0, 0, 0);
            AutomationProperties.SetName(button, $"{label} {name}");
            return button;
        }
        buttons.Children.Add(Small("Play", play, "Play"));
        var useButton = Small(inUse ? "In use" : "Use", use, "Use");
        useButton.IsEnabled = !inUse;
        buttons.Children.Add(useButton);
        if (remove is not null) buttons.Children.Add(Small("Remove", remove, "Remove"));
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(text);
        return row;
    }

    /// <summary>Plays a voice's recording on Windows' default speakers: Martlet's copy, or the bundled sample (null).</summary>
    private async Task PlayVoiceAsync(F5ReferenceSnapshot? voice, string name)
    {
        if (store is null || closing) return;
        try
        {
            var audio = voice is null ? F5Voices.SampleBytes : await F5Voices.ReadAudioAsync(store.DataDirectory, voice, lifetime.Token);
            voicePlayer?.Stop();
            voicePlayer = new SoundPlayer(new MemoryStream(audio));
            voicePlayer.Play();
            ActionText.Text = $"Playing '{name}' on Windows' default speakers.";
        }
        catch (OperationCanceledException) { }
        catch (F5Exception error) { ActionText.Text = F5Voices.Describe(error); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ActionText.Text = $"'{name}' can't be played: {error.Message}";
        }
    }

    /// <summary>Makes a voice the one F5 speaks with (null: the bundled sample, added to the list first). When F5 speaks
    /// now, the speaking route keeps the voice, so the next conversation uses it; otherwise it is used once F5 speaks.</summary>
    private async Task UseVoiceAsync(F5ReferenceSnapshot? voice)
    {
        if (store is null || setupService is null || closing) return;
        if (savingTab || assigningRole || setupOperations.IsRunning)
        {
            ActionText.Text = "Another change is still finishing. Try again in a moment.";
            return;
        }
        assigningRole = true;
        var token = lifetime.Token;
        try
        {
            var loaded = await setupService.LoadAsync(token);
            if (loaded.Error is not null) throw new InvalidOperationException(loaded.Error.Summary);
            var route = loaded.Settings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Tts);
            var f5 = route is { RouteType: SetupRouteType.GatewayF5, GatewaySnapshot: not null } ? route : null;
            var destination = f5?.GatewaySnapshot!.DestinationId ?? F5Destination;
            if (voice is null)
            {
                using var voices = F5Voices.Open(store.DataDirectory);
                voice = await F5Voices.SampleAsync(voices, store.DataDirectory, destination, token);
            }
            if (voice.Rights.ProcessingDestinationId != destination)
                throw new InvalidOperationException($"'{voice.PresetName}' was added for another F5 destination. Add its recording again.");
            var reference = await F5Voices.ApplyAsync(store.DataDirectory, voice, token);
            if (f5 is not null)
            {
                var next = SetupSettings.ApplyF5Reference(loaded.Settings!, reference);
                var saved = await setupService.SaveAsync(next, loaded.Revision, token);
                if (!saved.Save.Saved) throw new InvalidOperationException(saved.Summary);
                homeSettings = next;
                ActionText.Text = $"Martlet now speaks with the voice '{voice.PresetName}'. An open conversation window picks it up on Reload.";
            }
            else
                ActionText.Text = $"'{voice.PresetName}' is your F5 voice. Martlet speaks with it once F5 does the speaking, on this PC or another of your computers.";
        }
        catch (OperationCanceledException) { }
        catch (F5Exception error) { ActionText.Text = F5Voices.Describe(error); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or JsonException)
        {
            ActionText.Text = error.Message;
        }
        finally
        {
            assigningRole = false;
            if (!closing) RenderHome();
        }
    }

    /// <summary>Adds a voice from a recording, then switches to it.</summary>
    private async Task AddVoiceAsync(string destination)
    {
        if (store is null || closing) return;
        var added = F5AddVoiceDialog.Add(this, store.DataDirectory, destination);
        if (added is null) return;
        await UseVoiceAsync(added);
    }

    private async Task RemoveVoiceAsync(F5ReferenceSnapshot voice)
    {
        if (store is null || closing) return;
        if (!ConfirmationDialog.Confirm(this, $"Remove the voice '{voice.PresetName}'? Martlet deletes its copy of the recording on this " +
                "PC. Your original file isn't touched.", "Remove voice"))
            return;
        try
        {
            await F5Voices.RemoveAsync(store.DataDirectory, voice.PresetId, lifetime.Token);
            ActionText.Text = $"'{voice.PresetName}' was removed from your voices.";
        }
        catch (OperationCanceledException) { }
        catch (F5Exception error) { ActionText.Text = F5Voices.Describe(error); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ActionText.Text = error.Message;
        }
        finally
        {
            if (!closing) RenderHome();
        }
    }
}
