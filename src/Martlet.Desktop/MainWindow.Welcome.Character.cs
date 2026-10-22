using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Characters;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>The welcome wizard's character step (docs/WELCOME_WIZARD.md, step 6): who Martlet is. One card for each character
/// profile you have (after joining a network, the ones your other computers share), one for Martlet itself, Make my own (a name,
/// who it is, a look and a voice) and Import a character card. Each makes or uses a character profile, so Home's Get started
/// ticks its second step. Then the accepted setup is applied as before.</summary>
public partial class MainWindow
{
    private bool welcomeCharacterBusy;

    private void ShowWelcomeCharacter()
    {
        WizardCharacterStatus.Text = "";
        WizardCharacterForm.Visibility = Visibility.Collapsed;
        RenderWelcomeCharacters();
        ShowTour(TourCharacter);
    }

    /// <summary>The cards for the characters you have, then Martlet itself.</summary>
    private void RenderWelcomeCharacters()
    {
        WizardCharacterList.Children.Clear();
        var companion = homeSettings?.Companion;
        var profiles = companion?.CharacterList ?? [];
        var library = CharacterLibrary();
        var voices = VoiceLibrary();
        for (var i = 0; i < profiles.Count; i++)
        {
            var profile = profiles[i];
            var detail = $"Look: {LookName(profile.ModelId, library)} · Voice: {VoiceName(profile.VoiceId, voices)} · " +
                $"Personality: {PersonaName(profile.PersonaId, companion)}";
            WizardCharacterList.Children.Add(CharacterCard("\uE77B", profile.Name, detail, $"WizardCharacterProfile-{i}",
                () => UseWelcomeCharacterAsync(profile.Id)));
        }
        var persona = companion?.ActivePersona.Name ?? "Martlet";
        WizardCharacterList.Children.Add(CharacterCard("\uE76E", profiles.Count == 0 ? persona : $"Keep {persona}",
            "Martlet's own friendly personality, with the look and voice it has now.", "WizardCharacterKeep", KeepWelcomeCharacterAsync));
    }

    private Button CharacterCard(string glyph, string title, string detail, string id, Func<Task> choose)
    {
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var line = new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap };
        line.SetResourceReference(StyleProperty, "Muted");
        text.Children.Add(line);
        var card = new Button { Content = text, Tag = glyph, Margin = new Thickness(0, 0, 0, 12) };
        card.SetResourceReference(StyleProperty, "CardButton");
        AutomationProperties.SetAutomationId(card, id);
        AutomationProperties.SetName(card, title);
        card.Click += (_, _) => choose().Forget();
        return card;
    }

    private void WizardCharacterMake_Click(object sender, RoutedEventArgs e)
    {
        var companion = homeSettings?.Companion;
        WizardCharacterLook.Items.Clear();
        var library = CharacterLibrary();
        AddChoice(WizardCharacterLook, null, "The look Martlet has now");
        AddChoice(WizardCharacterLook, CharacterProfile.BuiltInModel, BuiltInLookName);
        foreach (var model in library.Live) AddChoice(WizardCharacterLook, model.Id, model.Name!);
        WizardCharacterLook.SelectedIndex = 0;
        WizardCharacterVoice.Items.Clear();
        AddChoice(WizardCharacterVoice, null, "The voice Martlet has now");
        foreach (var voice in VoiceLibrary().Live) AddChoice(WizardCharacterVoice, voice.Id, voice.Name!);
        WizardCharacterVoice.SelectedIndex = 0;
        WizardCharacterForm.Visibility = Visibility.Visible;
        WizardCharacterStatus.Text = companion?.Personas.Count >= CompanionSettings.MaximumPersonas
            ? $"Martlet keeps at most {CompanionSettings.MaximumPersonas} personalities; remove one in Companion › Personality first." : "";
        Motion.Enter(WizardCharacterForm);
        WizardCharacterName.Focus();
    }

    private static void AddChoice(ComboBox box, string? id, string text) => box.Items.Add(new ComboBoxItem { Content = text, Tag = id });

    private void WizardCharacterSave_Click(object sender, RoutedEventArgs e) => MakeWelcomeCharacterAsync().Forget();

    /// <summary>Make my own: a new personality with the text typed and a profile with the chosen look and voice, used at once
    /// (the same steps as Find a character).</summary>
    private async Task MakeWelcomeCharacterAsync()
    {
        var name = WizardCharacterName.Text.Trim();
        var personality = WizardCharacterPersonality.Text.Trim();
        if (name.Length == 0)
        {
            WizardCharacterStatus.Text = "Give your character a name.";
            WizardCharacterName.Focus();
            return;
        }
        if (personality.Length == 0)
        {
            WizardCharacterStatus.Text = "Say who it is in a few words, for example how it talks and what it likes.";
            WizardCharacterPersonality.Focus();
            return;
        }
        try { PersonaProfile.ValidateText(personality); }
        catch (Exception error) when (error is Martlet.Core.Contracts.ContractException or ArgumentException)
        {
            WizardCharacterStatus.Text = error.Message;
            return;
        }
        var look = (WizardCharacterLook.SelectedItem as ComboBoxItem)?.Tag as string;
        var voice = (WizardCharacterVoice.SelectedItem as ComboBoxItem)?.Tag as string;
        await ChooseWelcomeCharacterAsync("Making your character...", companion =>
        {
            Martlet.Core.Contracts.ContractRules.Require(companion.Personas.Count < CompanionSettings.MaximumPersonas,
                $"Martlet keeps at most {CompanionSettings.MaximumPersonas} personalities. Remove one in Companion › Personality first.");
            var personaName = UniqueName(companion.Personas.Select(p => p.Name), name, PersonaProfile.MaximumNameCharacters);
            var next = companion.Add(personaName);
            next = next.Update(next.ActivePersonaId, personaName, personality);
            var profileName = UniqueName(next.CharacterList.Select(c => c.Name), personaName, CharacterProfile.MaximumNameCharacters);
            return next.AddCharacter(profileName, next.ActivePersonaId, look, voice, out _);
        });
    }

    /// <summary>Martlet itself: what Martlet uses now (its personality, look and voice) saved as a profile, so it is one step to
    /// switch back to later.</summary>
    private Task KeepWelcomeCharacterAsync() => ChooseWelcomeCharacterAsync("Saving Martlet as your character...", companion =>
    {
        var current = companion.CurrentCharacter(ShownCharacterModelId(), CurrentVoiceId());
        if (current is not null) return companion.SelectCharacter(current.Id);
        var name = UniqueName(companion.CharacterList.Select(c => c.Name), companion.ActivePersona.Name, CharacterProfile.MaximumNameCharacters);
        return companion.AddCharacter(name, companion.ActivePersonaId, null, null, out _);
    });

    private Task UseWelcomeCharacterAsync(Guid id) => ChooseWelcomeCharacterAsync("Switching to your character...", companion => companion, id);

    /// <summary>Saves <paramref name="change"/>, switches Martlet to the profile it made or chose (<paramref name="use"/>, else
    /// the newest profile for the active personality), then applies the accepted setup.</summary>
    private async Task ChooseWelcomeCharacterAsync(string doing, Func<CompanionSettings, CompanionSettings> change, Guid? use = null)
    {
        if (welcomeCharacterBusy || closing) return;
        welcomeCharacterBusy = true;
        TourCharacter.IsEnabled = false;
        WizardCharacterStatus.Text = doing;
        try
        {
            var before = homeSettings?.Companion?.CharacterList.Select(c => c.Id).ToHashSet() ?? [];
            var saved = await ChangeCompanionAsync(change, "Martlet's character changed.");
            if (closing) return;
            if (saved is null)
            {
                WizardCharacterStatus.Text = string.IsNullOrWhiteSpace(ActionText.Text) ? "Martlet couldn't save the character; try again." : ActionText.Text;
                return;
            }
            var id = use ?? saved.CharacterList.FirstOrDefault(c => !before.Contains(c.Id))?.Id ?? saved.ActiveCharacterId;
            if (id is { } chosen) await UseCharacterProfileAsync(chosen);
            if (closing) return;
            ErrorLog.Info("Welcome: Martlet's character is chosen.");
            await FinishWelcomeAsync();
        }
        finally
        {
            welcomeCharacterBusy = false;
            TourCharacter.IsEnabled = true;
        }
    }

    /// <summary>Import a character card: the Personality window's import, then a profile for the imported personality.</summary>
    private async void WizardCharacterImport_Click(object sender, RoutedEventArgs e)
    {
        if (welcomeCharacterBusy || closing) return;
        var before = homeSettings?.Companion?.Personas.Select(p => p.Id).ToHashSet() ?? [];
        await OpenCompanionWindowAsync(importCard: true);
        if (closing) return;
        var companion = homeSettings?.Companion;
        if (companion is null || before.Contains(companion.ActivePersonaId))
        {
            WizardCharacterStatus.Text = "No character card was imported.";
            return;
        }
        await ChooseWelcomeCharacterAsync("Saving the imported character...", next =>
        {
            var name = UniqueName(next.CharacterList.Select(c => c.Name), next.ActivePersona.Name, CharacterProfile.MaximumNameCharacters);
            return next.AddCharacter(name, next.ActivePersonaId, null, null, out _);
        });
    }

    /// <summary>Applies the accepted suggestion, or (without one) only closes the wizard on Home.</summary>
    private async Task FinishWelcomeAsync()
    {
        if (welcomePlan is not null) { await ApplyWelcomeAsync(); return; }
        HideTour();
        Navigate(NavHome);
    }

    private void WizardCharacterSkip_Click(object sender, RoutedEventArgs e)
    {
        if (welcomeCharacterBusy) return;
        ErrorLog.Info("Welcome: the character is left for later.");
        FinishWelcomeAsync().Forget();
    }
}
