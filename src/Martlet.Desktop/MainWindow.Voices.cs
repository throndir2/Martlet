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
    private bool retiredSampleChecked;

    /// <summary>Which self-hosted engine speaks: F5-TTS, XTTS-v2, GPT-SoVITS or Dia (<see cref="SpeechEngines"/>). All use the voices below.
    /// Choosing another engine while a computer speaks hands Speaking to that engine on the same computer (installing its
    /// role there first, after showing what it needs and its licence); otherwise the choice is used the next time Speaking
    /// goes to a computer. Readable as <c>SpeakingEngine</c> and <c>SpeakingEngineStatus</c>.</summary>
    private Border SpeakingEngineCard(SetupRoute? route)
    {
        var current = SpeakingEngineChoice.Current;
        var host = route is { RouteType: SetupRouteType.GatewayF5 } ? route.Gateway?.HostId : null;
        var choice = new ComboBox { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(choice, "Voice engine");
        AutomationProperties.SetAutomationId(choice, "SpeakingEngine");
        foreach (var engine in SpeechEngines.All)
        {
            var item = new ComboBoxItem { Content = $"{engine.Name}: {engine.Summary}", Tag = engine.Key };
            AutomationProperties.SetAutomationId(item, "SpeakingEngine-" + engine.Key);
            choice.Items.Add(item);
            if (engine == current) choice.SelectedItem = item;
        }
        choice.SelectionChanged += (_, _) =>
        {
            if (choice.SelectedItem is ComboBoxItem { Tag: string key } && SpeechEngines.ForKey(key) is { } picked && picked != current)
                SelectSpeakingEngineAsync(picked, host).Forget();
        };
        var status = Note(host is null
                ? $"{current.Name} speaks once you hand Speaking to a computer (or set it up on this PC). Its model licence: {current.WeightsLicense}."
                : $"{current.Name} speaks on {host}. Its model licence: {current.WeightsLicense}.",
            new Thickness(0, 6, 0, 0));
        AutomationProperties.SetAutomationId(status, "SpeakingEngineStatus");
        return Card(Heading("Voice engine"),
            Note("Every engine copies a voice from the same recordings, on an NVIDIA graphics card. XTTS-v2 starts speaking sooner; " +
                "F5-TTS often sounds closer to the recording; GPT-SoVITS suits anime-style voices and needs a 3-10 second recording; " +
                "Dia can laugh, sigh and cough (English only). " +
                "The F5-TTS and XTTS-v2 models are for non-commercial use only; GPT-SoVITS's are MIT and Dia's Apache-2.0.", new Thickness(0, 0, 0, 8)),
            choice, status);
    }

    private async Task SelectSpeakingEngineAsync(SpeechEngine engine, string? host)
    {
        if (store is null || closing) return;
        try
        {
            SpeakingEngineChoice.Save(store.DataDirectory, engine);
            if (host is null)
            {
                ActionText.Text = $"Speaking will use {engine.Name} when it goes to a computer.";
                return;
            }
            await AssignJobAsync(HostJob.Speaking, "host:" + host);
            // Unless Speaking moved (or is moving) to the new engine, the choice follows the engine that still speaks.
            if (!pendingJobHosts.ContainsKey(SetupRole.Tts)) SpeakingEngineChoice.Sync(store.DataDirectory, homeSettings);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ActionText.Text = $"Couldn't save the voice engine: {error.Message}";
        }
        finally
        {
            if (!closing) RenderHome();
        }
    }

    private Border VoicesCard(SetupRoute? route)
    {
        var f5 = route?.RouteType == SetupRouteType.GatewayF5 ? route : null;
        var destination = f5?.GatewaySnapshot?.DestinationId ?? F5Destination;
        // The engine that speaks (or will): some clone only recordings of certain lengths (GPT-SoVITS: 3-10 seconds).
        var engine = SpeechEngines.ForRoute(f5?.GatewaySnapshot?.RouteId) ?? SpeakingEngineChoice.Current;
        string? Cannot(int milliseconds) => SpeechEngines.ReferenceProblem(engine, milliseconds);
        string Language(string transcript) => engine == SpeechEngines.GptSovits
            ? SpeechEngines.ReferenceLanguage(transcript) == "ja" ? " Japanese recording." : " English recording."
            : "";
        var stack = new List<UIElement>
        {
            Heading("Voices"),
            Note($"{engine.Name} copies a voice from a short recording" +
                (engine.MinimumReferenceMilliseconds > 1_000 || engine.MaximumReferenceMilliseconds < 30_000
                    ? $" of {engine.MinimumReferenceMilliseconds / 1000}-{engine.MaximumReferenceMilliseconds / 1000} seconds" : "") +
                ". Use an included voice or add your own; recordings stay on this PC and go only " +
                "to the computer that speaks." + (f5 is null ? $" The chosen voice will be used when {engine.Name} speaks." : ""),
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
        var chosenVoice = voices.FirstOrDefault(v => v.PresetId == chosen);
        var own = voices.Where(v => F5Voices.Bundled(v) is null).ToArray();
        var status = Note($"{F5BundledVoices.All.Count} included voices, {own.Length} of yours. " + (chosenVoice is null
                ? $"None {mark} yet; F5 starts with {F5BundledVoices.Default.Name}."
                : F5Voices.Bundled(chosenVoice) is { } bundledInUse ? $"{Capitalized(mark)}: {bundledInUse.Name}."
                : F5Voices.IsRetiredSample(chosenVoice) ? $"{Capitalized(mark)}: old sample voice. Switch to another voice."
                : $"{Capitalized(mark)}: one of your voices."),
            new Thickness(0, 0, 0, 10));
        AutomationProperties.SetAutomationId(status, "F5VoicesStatus");
        stack.Add(status);

        foreach (var (title, group, cute) in new[] { ("Cute voices", "cute", true), ("More included voices", "included", false) })
        {
            var included = F5BundledVoices.All.Where(v => v.Cute == cute).ToArray();
            if (included.Length == 0) continue;
            stack.Add(VoiceGroup(title, group));
            foreach (var bundled in included)
            {
                var stored = voices.FirstOrDefault(v => F5Voices.Bundled(v) == bundled);
                var inUse = stored is not null && stored.PresetId == chosen;
                int length;
                try { length = bundled.Check().DurationMilliseconds; }
                catch (F5Exception) { length = 0; }
                var cannot = Cannot(length);
                stack.Add(VoiceRow(bundled.Name, bundled.Description + (cannot is null ? Language(bundled.Transcript) : " " + cannot),
                    inUse, mark, bundled.Key, status: true,
                    () => PlayVoiceAsync(stored, bundled.Name, bundled), () => UseVoiceAsync(stored, bundled),
                    stored is null || inUse || stored.PresetId == applied ? null : () => RemoveVoiceAsync(stored), cannot));
            }
        }

        if (own.Length > 0) stack.Add(VoiceGroup("Your voices", "own"));
        foreach (var voice in own)
        {
            var inUse = voice.PresetId == chosen;
            var cannot = Cannot(voice.AudioFormat.DurationMilliseconds);
            var detail = F5Voices.IsRetiredSample(voice)
                ? "This old sample is no longer included. Switch to another voice, then remove it."
                : $"{voice.AudioFormat.DurationMilliseconds / 1000d:0.#} second recording, added {voice.CreatedAtUtc.ToLocalTime():d}." +
                  (cannot is null ? "" : " " + cannot);
            stack.Add(VoiceRow(voice.PresetName, detail, inUse, mark, voice.PresetId.ToString("N"), status: false,
                () => PlayVoiceAsync(voice, voice.PresetName), () => UseVoiceAsync(voice),
                inUse || voice.PresetId == applied ? null : () => RemoveVoiceAsync(voice), cannot));
        }
        stack.Add(Row(PageButton("Add a voice...", () => AddVoiceAsync(destination).Forget(), id: "F5AddVoice")));
        return Card([.. stack]);
    }

    private static string Capitalized(string text) => char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>A group heading in the voice list, readable as <c>F5VoiceGroup-id</c> (cute, included, own).</summary>
    private static TextBlock VoiceGroup(string text, string id)
    {
        var label = Note(text.ToUpperInvariant(), new Thickness(0, 6, 0, 8));
        label.FontWeight = FontWeights.SemiBold;
        AutomationProperties.SetAutomationId(label, $"F5VoiceGroup-{id}");
        return label;
    }

    /// <summary>One voice: its name (with the in-use mark), a detail line, and Play, Use and (when it may go) Remove. Controls
    /// are identified by <paramref name="key"/>; included voices' titles are also readable status (<c>F5VoiceRow-key</c>).
    /// <paramref name="cannot"/> says why the speaking engine cannot use it (Use is then off).</summary>
    private UIElement VoiceRow(string name, string detail, bool inUse, string mark, string key, bool status, Func<Task> play, Func<Task> use,
        Func<Task>? remove, string? cannot = null)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock { Text = name + (inUse ? $"  \u00b7  {mark}" : "") + (cannot is null ? "" : "  \u00b7  wrong length for this engine"),
            FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        if (status) AutomationProperties.SetAutomationId(title, $"F5VoiceRow-{key}");
        text.Children.Add(title);
        text.Children.Add(Note(detail, new Thickness(0, 2, 0, 0)));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Button Small(string label, Func<Task> run, string id)
        {
            var button = PageButton(label, () => run().Forget(), id: $"F5Voice{id}-{key}");
            button.MinWidth = 72;
            button.Margin = new Thickness(8, 0, 0, 0);
            AutomationProperties.SetName(button, $"{label} {name}");
            return button;
        }
        buttons.Children.Add(Small("Play", play, "Play"));
        var useButton = Small(inUse ? "In use" : "Use", use, "Use");
        useButton.IsEnabled = !inUse && cannot is null;
        if (cannot is not null)
        {
            useButton.ToolTip = cannot;
            ToolTipService.SetShowOnDisabled(useButton, true);
            AutomationProperties.SetHelpText(useButton, cannot);
        }
        buttons.Children.Add(useButton);
        if (remove is not null) buttons.Children.Add(Small("Remove", remove, "Remove"));
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(text);
        return row;
    }

    /// <summary>Plays a voice's recording on Windows' default speakers: Martlet's copy, or a bundled voice's clip.</summary>
    private async Task PlayVoiceAsync(F5ReferenceSnapshot? voice, string name, F5BundledVoice? bundled = null)
    {
        if (store is null || closing) return;
        try
        {
            var audio = voice is null ? (bundled ?? F5BundledVoices.Default).ReadAudio()
                : await F5Voices.ReadAudioAsync(store.DataDirectory, voice, lifetime.Token);
            voicePlayer?.Stop();
            voicePlayer = new SoundPlayer(new MemoryStream(audio));
            voicePlayer.Play();
            ActionText.Text = $"Playing '{name}'...";
        }
        catch (OperationCanceledException) { }
        catch (F5Exception error) { ActionText.Text = F5Voices.Describe(error); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ActionText.Text = $"Couldn't play '{name}': {error.Message}";
        }
    }

    /// <summary>Makes a voice the one F5 speaks with (null: <paramref name="bundled"/>, added to the list first). When F5
    /// speaks now, the speaking route keeps the voice, so the next conversation uses it; otherwise it is used once F5 speaks.</summary>
    private async Task UseVoiceAsync(F5ReferenceSnapshot? voice, F5BundledVoice? bundled = null)
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
            if (voice is null || voice.Rights.ProcessingDestinationId != destination && bundled is not null)
            {
                using var voices = F5Voices.Open(store.DataDirectory);
                voice = await F5Voices.BundledAsync(voices, store.DataDirectory, destination, bundled ?? F5BundledVoices.Default, token);
            }
            if (voice.Rights.ProcessingDestinationId != destination)
                throw new InvalidOperationException($"Add '{voice.PresetName}' again for the computer that will speak it.");
            var reference = await F5Voices.ApplyAsync(store.DataDirectory, voice, token);
            if (f5 is not null)
            {
                var next = SetupSettings.ApplyF5Reference(loaded.Settings!, reference);
                var saved = await setupService.SaveAsync(next, loaded.Revision, token);
                if (!saved.Save.Saved) throw new InvalidOperationException(saved.Summary);
                homeSettings = next;
                ActionText.Text = $"Martlet now uses the voice '{voice.PresetName}'. Reload any open conversation to use it.";
            }
            else
                ActionText.Text = $"'{voice.PresetName}' is your F5 voice.";
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

    /// <summary>Earlier versions started F5 with the F5-TTS example clip, a male voice Martlet no longer ships, and kept it on
    /// the speaking route. A route still speaking with it, or a voice list that still applies it, moves to
    /// <see cref="F5BundledVoices.Default"/> (a female voice); a voice the owner picked is left alone. Returns the saved
    /// settings when the route moved, otherwise null.</summary>
    private async Task<AppSettings?> LeaveRetiredSampleAsync(SettingsLoadResult loaded, CancellationToken token)
    {
        if (store is null || setupService is null || closing || savingTab || assigningRole || loaded.Settings is not { } settings)
            return null;
        var route = settings.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Tts);
        var f5 = route is { RouteType: SetupRouteType.GatewayF5, GatewaySnapshot: not null } ? route : null;
        var speaking = f5?.Reference is { } used && F5BundledVoices.IsRetiredSample(used.AudioSha256);
        // Without a speaking route the voice list's applied voice is the chosen one; read it once per run.
        if (!speaking && (f5?.Reference is not null || retiredSampleChecked)) return null;
        retiredSampleChecked = true;
        if (!speaking && !System.IO.Directory.Exists(F5Voices.Directory(store.DataDirectory))) return null;
        var destination = f5?.GatewaySnapshot!.DestinationId ?? F5Destination;
        assigningRole = true;
        try
        {
            F5ReferenceSnapshot voice;
            using (var voices = F5Voices.Open(store.DataDirectory))
            {
                if (!speaking)
                {
                    var inspection = voices.Inspect();
                    var applied = inspection.Presets.FirstOrDefault(p => p.Id == inspection.AppliedPresetId)?.Snapshots
                        .FirstOrDefault(s => s.ReferenceRevision == inspection.AppliedReferenceRevision);
                    if (applied is null || !F5Voices.IsRetiredSample(applied)) return null;
                }
                voice = await F5Voices.BundledAsync(voices, store.DataDirectory, destination, F5BundledVoices.Default, token);
            }
            var reference = await F5Voices.ApplyAsync(store.DataDirectory, voice, token);
            var moved = $"An old sample voice is no longer used. F5 now uses {F5BundledVoices.Default.Name}. " +
                "Pick another under Companion › Voice › Voices.";
            if (!speaking)
            {
                ActionText.Text = moved;
                return null;
            }
            var next = SetupSettings.ApplyF5Reference(settings, reference);
            var saved = await setupService.SaveAsync(next, loaded.Revision, token);
            if (!saved.Save.Saved) throw new InvalidOperationException(saved.Summary);
            ActionText.Text = moved;
            openConversation?.ReloadWhenIdle("Martlet's voice changed.");
            return next;
        }
        catch (OperationCanceledException) { return null; }
        catch (F5Exception error)
        {
            ActionText.Text = "Couldn't switch from the old sample voice: " + F5Voices.Describe(error);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or
            ContractException or JsonException)
        {
            ActionText.Text = "Couldn't switch from the old sample voice: " + error.Message;
            return null;
        }
        finally { assigningRole = false; }
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
        if (!ConfirmationDialog.Confirm(this, $"Remove '{voice.PresetName}'?\n\nMartlet deletes its copy on this PC. Your original file isn't touched.",
                "Remove voice"))
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
