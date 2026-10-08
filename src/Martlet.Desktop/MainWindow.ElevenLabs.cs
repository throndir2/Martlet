using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;
using Martlet.F5;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>Companion › Voice › A cloud provider › ElevenLabs: the owner's cloned voice with tones (docs/ELEVENLABS_VOICE.md).
/// The owner picks one of their saved speaking voices, a model and their own ElevenLabs API key, and ticks the box that allows
/// Martlet to upload that recording to their ElevenLabs account once (Instant Voice Cloning) and to send reply text there.
/// Nothing is uploaded or spent before that click. The same recording again is used without another upload.</summary>
public partial class MainWindow
{
    /// <summary>One of the owner's saved voices that can be cloned: its library ID, name and recording.</summary>
    private sealed record ElevenLabsVoiceChoice(string Id, string Name, string AudioSha256)
    {
        public override string ToString() => Name;
    }

    private Border ElevenLabsCard(SetupRoute? route)
    {
        var saved = route?.RouteType == SetupRouteType.ElevenLabs ? route : null;
        var engine = SpeechEngines.ElevenLabs;
        var stack = new List<UIElement>
        {
            Heading("ElevenLabs: your cloned voice with tones"),
            AbilitiesLine("elevenlabs", engine.Abilities, engine.Tags),
            RunsOnLine("elevenlabs", engine.RunsOn),
            Note("ElevenLabs copies one of your voices and speaks replies with it in real time, with tones such as [whispers], " +
                "[happy] or [sad] and sounds such as [laughs]. Martlet follows ElevenLabs' documentation; it hasn't been tried with " +
                "a live ElevenLabs account yet.", new Thickness(0, 6, 0, 0))
        };

        var status = Note(ElevenLabsStatus(saved), new Thickness(0, 8, 0, 0));
        AutomationProperties.SetAutomationId(status, "ElevenLabsStatus");
        stack.Add(status);

        var model = new ComboBox { MinHeight = 30, MaxWidth = 420, MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Left,
            ItemsSource = ElevenLabsSetup.ModelIds.Select(id => new ComboBoxItem { Content = ElevenLabsModelChoice(id), Tag = id }).ToArray() };
        model.SelectedItem = model.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == (saved?.ModelId ?? ElevenLabsSetup.DefaultModelId));
        AutomationProperties.SetName(model, "ElevenLabs model");
        AutomationProperties.SetAutomationId(model, "ElevenLabsModel");

        var voices = ElevenLabsVoiceChoices();
        var voice = new ComboBox { ItemsSource = voices, MinHeight = 30, MaxWidth = 420, MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Left };
        var savedVoice = voices.FirstOrDefault(v => v.Id == saved?.ClonedVoice?.LibraryVoiceId && v.AudioSha256 == saved.ClonedVoice.AudioSha256);
        voice.SelectedItem = savedVoice ?? voices.FirstOrDefault();
        AutomationProperties.SetName(voice, "Voice to clone");
        AutomationProperties.SetAutomationId(voice, "ElevenLabsVoice");
        // The selected recording is the one already cloned: nothing is uploaded again.
        bool Cloned() => savedVoice is not null && voice.SelectedItem is ElevenLabsVoiceChoice selected && selected == savedVoice;

        var key = new PasswordBox { MaxLength = SecretLease.MaximumLength, Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(key, "ElevenLabs API key");
        AutomationProperties.SetAutomationId(key, "ElevenLabsKey");
        var keyStatus = Note(saved?.CredentialId is not null ? "Your ElevenLabs key is saved. Leave this empty to keep it, or paste a new key."
            : SetupSettings.SetAsideElevenLabsCredentials(homeSettings).Count > 0
                ? "Your ElevenLabs key from before is still saved. Leave this empty to use it again, or paste a new key."
            : "Paste your ElevenLabs API key (elevenlabs.io › Developers › API keys). Martlet saves it in Windows Credential Manager.",
            new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(keyStatus, "ElevenLabsKeyStatus");

        // The box comes ticked only for the exact choice the owner confirmed: never over a voice they didn't pick.
        var consent = new CheckBox { Margin = new Thickness(0, 12, 0, 8), IsChecked = saved?.Consent is not null && Cloned(),
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text =
                "I choose ElevenLabs for Voice. Martlet uploads the chosen recording to my ElevenLabs account once to clone it, and " +
                "sends reply text there while it speaks. The recording is my own voice, a voice I have its speaker's permission for, " +
                "or a published sample anyone may use. Requests cost money." } };
        AutomationProperties.SetAutomationId(consent, "ElevenLabsConsent");

        Button? save = null;
        string Label() => Cloned() ? "Use these settings" : "Clone and use ElevenLabs";
        save = PageButton(Label(), () =>
        {
            // One click at a time: a second click during an upload would clone the voice again.
            save!.IsEnabled = false;
            UseElevenLabsAsync((model.SelectedItem as ComboBoxItem)?.Tag as string ?? ElevenLabsSetup.DefaultModelId,
                voice.SelectedItem as ElevenLabsVoiceChoice, key, consent.IsChecked == true, () => save.IsEnabled = true).Forget();
        }, primary: true, id: "ElevenLabsSave");
        model.SelectionChanged += (_, _) => { tabEdited = true; consent.IsChecked = false; };
        voice.SelectionChanged += (_, _) =>
        {
            tabEdited = true;
            consent.IsChecked = false;
            save.Content = Label();
            AutomationProperties.SetName(save, Label());
        };
        key.PasswordChanged += (_, _) => tabEdited = true;

        stack.AddRange(
        [
            new Label { Content = "_Model", Target = model, Padding = new Thickness(0, 10, 0, 4) },
            model,
            Note($"{ElevenLabsSetup.ModelName(ElevenLabsSetup.V4Turbo)} is the fastest (about 100 ms, as ElevenLabs documents). If ElevenLabs " +
                $"refuses it, Martlet says so; then choose {ElevenLabsSetup.ModelName(ElevenLabsSetup.V3Conversational)}.", new Thickness(0, 4, 0, 0)),
            new Label { Content = "_Voice to clone", Target = voice, Padding = new Thickness(0, 10, 0, 4) },
            voices.Count == 0 ? Note("Add a voice first: choose This PC above, then Add a voice under Voices.", new Thickness(0, 0, 0, 0)) : voice,
            new Label { Content = "ElevenLabs API _key", Target = key, Padding = new Thickness(0, 10, 0, 4) },
            key,
            keyStatus,
            Note(ElevenLabsSetup.Disclosure + " The cloned voice stays in your ElevenLabs account (My Voices) until you delete it there.",
                new Thickness(0, 10, 0, 0)),
            consent,
            Row(save)
        ]);
        return Card([.. stack]);
    }

    private static string ElevenLabsModelChoice(string id) => id == ElevenLabsSetup.V4Turbo
        ? $"{ElevenLabsSetup.ModelName(id)} (real time, recommended)" : $"{ElevenLabsSetup.ModelName(id)} (real time)";

    /// <summary>Where ElevenLabs stands, without the owner's voice names (ElevenLabsStatus is MCP-readable).</summary>
    private string ElevenLabsStatus(SetupRoute? saved) =>
        saved is null
            ? "Not in use. Choose a voice, paste your ElevenLabs API key, tick the box, then press Clone and use ElevenLabs."
            : $"In use: {ElevenLabsSetup.ModelName(saved.ModelId)} with your cloned voice." +
              (saved.Enabled == false ? " Turned off." : saved.Consent is null ? " Not confirmed yet." : "") +
              (saved.CredentialId is null ? " Add your ElevenLabs key." : " Key saved.") +
              (saved.ClonedVoice?.RequiresVerification == true
                  ? " ElevenLabs asked to verify this voice; verify it on elevenlabs.io before it speaks." : "");

    /// <summary>The owner's saved voices whose recording this PC has or Martlet carries (the starter voices).</summary>
    private List<ElevenLabsVoiceChoice> ElevenLabsVoiceChoices()
    {
        if (store is null) return [];
        try
        {
            var library = F5Voices.View(store.DataDirectory);
            var (local, _) = F5Voices.Local(store.DataDirectory, F5Destination);
            return [.. VoiceItems(library, local).Where(item => item.Available).Select(item => new ElevenLabsVoiceChoice(item.Id, item.Name, item.AudioSha256))];
        }
        catch (Exception error) when (error is F5Exception or IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>Clones the chosen recording on ElevenLabs (unless the route already has a voice cloned from that exact recording
    /// with the saved key) and makes ElevenLabs speak replies. Only on the owner's click with the box ticked, one at a time:
    /// <paramref name="done"/> runs when it has finished (it turns the button on again).</summary>
    private async Task UseElevenLabsAsync(string model, ElevenLabsVoiceChoice? choice, PasswordBox keyBox, bool consent, Action done)
    {
        if (usingElevenLabs)
        {
            ActionText.Text = "Martlet is still setting up ElevenLabs. Wait for it to finish.";
            done();
            return;
        }
        usingElevenLabs = true;
        try { await UseElevenLabsOnceAsync(model, choice, keyBox, consent); }
        finally
        {
            usingElevenLabs = false;
            done();
        }
    }

    private bool usingElevenLabs;

    private async Task UseElevenLabsOnceAsync(string model, ElevenLabsVoiceChoice? choice, PasswordBox keyBox, bool consent)
    {
        if (!consent)
        {
            ActionText.Text = "Tick the box to confirm ElevenLabs for Voice, then press the button again.";
            return;
        }
        if (store is null || setupService is null || closing) return;
        if (choice is null)
        {
            ActionText.Text = "Choose one of your voices to clone first.";
            return;
        }
        var leaving = LeavingEngine(null, null);
        if (leaving is not null && !ConfirmationDialog.Confirm(this, "Use ElevenLabs for Voice?" + LeavingNote(leaving), "Use ElevenLabs"))
            return;
        SecretLease? typed = null;
        var token = lifetime.Token;
        try
        {
            using (var entered = keyBox.SecurePassword)
                if (entered.Length > 0) typed = TakeKey(keyBox);
            string? key = null;
            if (typed is not null) typed.Use(secret => key = new string(secret));
            else key = SavedElevenLabsKey();
            if (key is null) throw new InvalidOperationException("Paste your ElevenLabs API key first.");

            var route = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Tts);
            // The voice already cloned from this exact recording with the saved key is used again: nothing is uploaded.
            var reuse = typed is null && route is { RouteType: SetupRouteType.ElevenLabs, ClonedVoice: { } same, VoiceId: { } id } &&
                same.LibraryVoiceId == choice.Id && same.AudioSha256 == choice.AudioSha256;
            string voiceId;
            ClonedVoiceSettings cloned;
            if (reuse)
            {
                voiceId = route!.VoiceId!;
                cloned = route.ClonedVoice!;
            }
            else
            {
                ActionText.Text = $"Uploading {choice.Name} to your ElevenLabs account to clone it...";
                var dataDirectory = store.DataDirectory;
                // Starter voices are copied to this PC first, as using them for a voice engine does.
                var result = await F5Voices.ReconcileAsync(dataDirectory, F5Destination, HostSetupCommands.SuggestedDeviceId(), null, null, token);
                if (!result.Local.TryGetValue(choice.Id, out var snapshot) || snapshot.AudioSha256 != choice.AudioSha256)
                    throw new InvalidOperationException($"{choice.Name}'s recording isn't on this PC yet. Try again in a moment.");
                var wave = await F5SharedVoices.ReadAsync(F5Voices.Directory(dataDirectory), snapshot, token) ??
                    throw new InvalidOperationException($"Martlet couldn't read {choice.Name}'s recording.");
                using var cloner = new ElevenLabsVoiceCloner();
                var name = $"Martlet - {choice.Name}";
                var made = await cloner.CloneAsync(key, name.Length <= 100 ? name : name[..100], wave, token);
                voiceId = made.VoiceId;
                cloned = new()
                {
                    SchemaVersion = 1, LibraryVoiceId = choice.Id, AudioSha256 = choice.AudioSha256,
                    Name = choice.Name.Length <= 128 ? choice.Name : choice.Name[..128], RequiresVerification = made.RequiresVerification
                };
                ErrorLog.Info($"ElevenLabs: cloned a saved voice (voice_id {voiceId}{(made.RequiresVerification ? ", needs verification" : "")}).");
            }
            var done = $"Martlet now speaks with {choice.Name}, cloned on ElevenLabs ({ElevenLabsSetup.ModelName(model)})." +
                (reuse ? " Nothing was uploaded again." : "") + (typed is null ? "" : " Your ElevenLabs key is saved in Windows Credential Manager.") +
                (cloned.RequiresVerification ? " ElevenLabs asked to verify this voice; verify it on elevenlabs.io before it speaks." : "") +
                " Requests cost money there.";
            if (!await SaveSectionRouteAsync(HostJob.Speaking, settings => ElevenLabsSetup.Select(settings, model, voiceId, cloned), typed, done,
                    "Paste your ElevenLabs API key first."))
                return;
            if (!closing && leaving is not null) ActionText.Text += await StopLeftVoiceEngineAsync(leaving);
        }
        catch (OperationCanceledException) { }
        catch (ElevenLabsException error) { ActionText.Text = ElevenLabsProblem(error); }
        catch (Exception error) when (error is InvalidOperationException or ContractException or F5Exception or IOException or
            UnauthorizedAccessException or JsonException)
        {
            ActionText.Text = error.Message;
        }
        finally { typed?.Dispose(); }
    }

    /// <summary>The saved ElevenLabs key: the route's, else one set aside when Voice left ElevenLabs; null when there is none.</summary>
    private string? SavedElevenLabsKey()
    {
        if (homeSettings is null) return null;
        var route = homeSettings.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Tts);
        if (route is { RouteType: SetupRouteType.ElevenLabs, CredentialId: not null } && RouteKey(route) is { } own) return own;
        foreach (var removal in SetupSettings.SetAsideElevenLabsCredentials(homeSettings))
        {
            using var read = new WindowsCredentialStore().Read(CredentialBinding.For(homeSettings, removal));
            if (read.Error != CredentialError.None || read.Secret is null) continue;
            string? key = null;
            read.Secret.Use(secret => key = new string(secret));
            return key;
        }
        return null;
    }

    /// <summary>What went wrong with ElevenLabs, in words, with what to do.</summary>
    internal static string ElevenLabsProblem(ElevenLabsException error) => error.Code switch
    {
        ProviderFailureCode.Authentication or ProviderFailureCode.CredentialUnavailable =>
            "ElevenLabs didn't accept the API key. Check it on elevenlabs.io and paste it again.",
        ProviderFailureCode.PermissionDenied => "That ElevenLabs key may not clone voices or speak. Allow Voices and Text to Speech for it on elevenlabs.io.",
        ProviderFailureCode.QuotaExceeded => "Your ElevenLabs account is out of credits or its plan doesn't allow this. Check it on elevenlabs.io.",
        ProviderFailureCode.RateLimited => "ElevenLabs is busy for your account. Try again in a moment.",
        ProviderFailureCode.Network or ProviderFailureCode.DeadlineExceeded => "Martlet couldn't reach ElevenLabs. Check the connection and try again.",
        _ => "ElevenLabs refused it" + (error.Detail is { Length: > 0 } detail ? $": {detail}" : ".")
    };
}
