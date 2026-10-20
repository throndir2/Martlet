using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Hosting;
using Martlet.Core.Characters;
using Martlet.Core.Settings;
using Martlet.Core.Sharing;
using Martlet.Core.Voices;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>Companion › Profiles, the UI only: Now, your profiles (each with what it sets, what it keeps on this PC, its household
/// sharing and Use, Edit and Remove or Leave), the profile editor, the household's characters and links to the parts. What each line
/// says is in <see cref="ProfilesPageModel"/>; what the page needs from the app is <see cref="IProfilesPageServices"/>. MainWindow
/// builds a profile's sharing controls (<paramref name="sharingControls"/>) and the household's characters card
/// (<paramref name="householdCard"/>) (MainWindow.Sharing.cs). MainWindow keeps one page for the window's life.</summary>
internal sealed class ProfilesPage(IProfilesPageServices services, Func<CharacterProfile, HouseholdSharing?, UIElement?> sharingControls,
    Func<CompanionSettings?, CharacterModelLibrary, SpeakingVoiceLibrary, Border> householdCard)
{
    // The editor's controls while it shows.
    private TextBox? nameBox;
    private ComboBox? personaChoice, lookChoice, voiceChoice;
    private TextBlock? editorProblem;

    /// <summary>Puts the keyboard in the editor's name box, with its text selected.</summary>
    internal void FocusName()
    {
        nameBox?.Focus();
        nameBox?.SelectAll();
    }

    /// <summary>Draws the page's cards into <paramref name="page"/>.</summary>
    internal void Render(Panel page)
    {
        var model = new ProfilesPageModel(services);
        var companion = model.Companion;
        var profiles = model.Profiles;
        page.Children.Add(PageNowCard(model.NowText, null, "CharacterProfilesNow"));

        var stack = new List<UIElement>
        {
            Heading("Your profiles"),
            HelpTip.Explain("Each profile is a whole character: its look, its voice and its personality. Use one to switch all three at once, " +
                "here, on Home or from Martlet's icon by the clock. Profiles are shared with your other Martlet computers.",
                new Thickness(0, 0, 0, 6), "Profiles", "profiles"),
            HelpTip.Explain("On each computer, a profile also keeps what you set there while you use it: where its character stands and how " +
                "big it is, where it looks, and which touches stop it while it talks. Switch back to it and they come back.",
                new Thickness(0, 0, 0, 10), "ProfilesPerComputer", "profiles on each computer")
        };
        var status = Note(model.StatusText, new Thickness(0, 0, 0, 10));
        AutomationProperties.SetAutomationId(status, "CharacterProfilesStatus");
        stack.Add(status);
        var here = services.ProfilesHere();
        var sharing = services.OwnSharing();
        foreach (var profile in profiles)
        {
            var inUse = profile.Id == model.Current?.Id;
            stack.Add(ProfileRow(profile, model.Detail(profile), model.State(profile), MainWindow.ProfileHereText(here.For(profile.Id)), inUse,
                sharingControls(profile, sharing), sharing?.HasJoined(profile.Id) == true));
        }
        stack.Add(PageKit.Row(PageButton(profiles.Count == 0 ? "Save what Martlet uses now as a profile..." : "New profile...",
            () => services.Edit(Guid.Empty), primary: profiles.Count == 0, id: "CharacterProfileNew")));
        page.Children.Add(Card([.. stack]));

        if (services.Editing is { } editing)
        {
            page.Children.Add(EditorCard(editing, companion, model.Library, model.Voices, model.ModelId, model.VoiceId));
            // Keep the half-filled form while Martlet refreshes.
            services.MarkEdited();
        }

        page.Children.Add(householdCard(companion, model.Library, model.Voices));

        page.Children.Add(Card(Heading("The parts"),
            Note("Add looks in Character, voices in Voice and personalities in Personality; every profile can use them.", new Thickness(0, 0, 0, 8)),
            PageKit.Row(PageButton("Character", () => services.Open(CompanionTab.Character), id: "ProfilesOpenCharacter"),
                PageButton("Voice", () => services.Open(CompanionTab.Voice), id: "ProfilesOpenVoice"),
                PageButton("Personality", () => services.Open(CompanionTab.Personality), id: "ProfilesOpenPersonality"))));
    }

    /// <summary>One profile: its name (with the in-use mark), what it sets, a readable state line
    /// (<c>CharacterProfileState-key</c>: never a name), what it keeps on this PC (<c>CharacterProfileHere-key</c>), how it is shared
    /// with the household (<paramref name="sharing"/>) and Use, Edit and Remove. A character someone else shares with you together
    /// (<paramref name="joined"/>) can't be edited here, and Leave (<c>CharacterProfileLeave-key</c>) replaces Remove.</summary>
    private UIElement ProfileRow(CharacterProfile profile, string detail, string state, string here, bool inUse, UIElement? sharing = null,
        bool joined = false)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = profile.Name + (inUse ? "  \u00b7  in use" : ""), FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap
        });
        text.Children.Add(Note(detail, new Thickness(0, 2, 0, 0)));
        var hereText = Note(here, new Thickness(0, 2, 0, 0));
        AutomationProperties.SetAutomationId(hereText, "CharacterProfileHere-" + profile.Key);
        text.Children.Add(hereText);
        var stateText = Note(state, new Thickness(0, 2, 0, 0));
        AutomationProperties.SetAutomationId(stateText, "CharacterProfileState-" + profile.Key);
        text.Children.Add(stateText);
        if (sharing is not null) text.Children.Add(sharing);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Button Small(string label, Action run, string id)
        {
            var button = PageButton(label, run, id: $"CharacterProfile{id}-{profile.Key}");
            button.MinWidth = 72;
            button.Margin = new Thickness(8, 0, 0, 0);
            AutomationProperties.SetName(button, $"{label} {profile.Name}");
            return button;
        }
        var use = Small(inUse ? "In use" : "Use", () => services.Use(profile.Id), "Use");
        use.IsEnabled = !inUse;
        buttons.Children.Add(use);
        var edit = Small("Edit", () => services.Edit(profile.Id), "Edit");
        edit.IsEnabled = !joined;
        if (joined) edit.ToolTip = "Only the person who shares it changes it.";
        buttons.Children.Add(edit);
        buttons.Children.Add(joined ? Small("Leave", () => services.Leave(profile), "Leave")
            : Small("Remove", () => services.Remove(profile), "Remove"));
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(text);
        return row;
    }

    /// <summary>The form for a new profile (filled in with what Martlet uses now) or an existing one: its name, personality,
    /// look and voice. "Keep the current" look or voice leaves that part alone when the profile is used.</summary>
    private Border EditorCard(Guid id, CompanionSettings? companion, CharacterModelLibrary library, SpeakingVoiceLibrary voices,
        string? modelNow, string? voiceNow)
    {
        var existing = companion?.CharacterList.FirstOrDefault(c => c.Id == id);
        var personaId = existing?.PersonaId ?? companion?.ActivePersonaId ?? Guid.Empty;
        var lookId = existing is null ? modelNow : existing.ModelId;
        var voiceId = existing is null ? voiceNow : existing.VoiceId;
        var name = existing?.Name ?? ProfilesPageModel.UniqueName(companion?.ActivePersona.Name ?? "Martlet", companion);

        nameBox = new TextBox { Text = name, MaxLength = CharacterProfile.MaximumNameCharacters, MinWidth = 260, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetAutomationId(nameBox, "CharacterProfileName");
        AutomationProperties.SetName(nameBox, "Profile name");
        InputHint.SetText(nameBox, "Gaming buddy");

        ComboBox Choice(string automationId, string accessibleName, IEnumerable<(object? Tag, string Text)> options, object? selected)
        {
            var box = new ComboBox { MinHeight = 30, MinWidth = 260, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var (tag, text) in options)
            {
                var item = new ComboBoxItem { Content = text, Tag = tag };
                box.Items.Add(item);
                if (Equals(tag, selected)) box.SelectedItem = item;
            }
            box.SelectedIndex = box.SelectedIndex < 0 ? 0 : box.SelectedIndex;
            AutomationProperties.SetAutomationId(box, automationId);
            AutomationProperties.SetName(box, accessibleName);
            return box;
        }
        personaChoice = Choice("CharacterProfilePersona", "Personality",
            companion is null ? [((object?)Guid.Empty, "Martlet (default)")] : companion.Personas.Select(p => ((object?)p.Id, p.Name)),
            companion is null ? Guid.Empty : personaId);
        var looks = new List<(object?, string)> { (null, "Keep the current look"), (CharacterProfile.BuiltInModel, MainWindow.BuiltInLookName) };
        looks.AddRange(library.Live.Select(m => ((object?)m.Id, m.Name!)));
        looks.AddRange(services.VroidLooks().Select(l => ((object?)l.Path, l.Name + " (VRoid Hub)")));
        if (lookId is not null && looks.All(l => !Equals(l.Item1, lookId)))
            looks.Add((lookId, RemoteCharacterModels.IsRemote(lookId) ? "A VRoid Hub character" : "A character no longer in your list"));
        lookChoice = Choice("CharacterProfileLook", "Look", looks, lookId);
        var voiceOptions = new List<(object?, string)> { (null, "Keep the current voice") };
        voiceOptions.AddRange(voices.Live.Select(v => ((object?)v.Id, v.Name!)));
        if (voiceId is not null && voiceOptions.All(v => !Equals(v.Item1, voiceId))) voiceOptions.Add((voiceId, "A voice no longer in your list"));
        voiceChoice = Choice("CharacterProfileVoice", "Voice", voiceOptions, voiceId);

        editorProblem = Note("", new Thickness(0, 8, 0, 0));
        editorProblem.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        AutomationProperties.SetAutomationId(editorProblem, "CharacterProfileEditorProblem");
        Label Labeled(string text, Control target) => new() { Content = text, Target = target, Padding = new Thickness(0, 12, 0, 4) };
        return Card(Heading(existing is null ? "New profile" : $"Edit '{existing.Name}'"),
            Note(existing is null ? "It starts as what Martlet uses now; change any part, then save." : "Change any part, then save.",
                new Thickness(0, 0, 0, 0)),
            Labeled("_Name", nameBox), nameBox,
            Labeled("_Personality", personaChoice), personaChoice,
            Labeled("_Look", lookChoice), lookChoice,
            Labeled("_Voice", voiceChoice), voiceChoice,
            Note("Voices are the ones in Companion › Voice › Voices, spoken by voice-cloning engines.", new Thickness(0, 6, 0, 0)),
            editorProblem,
            Row(PageButton(existing is null ? "Save profile" : "Save changes", () => Save(id), primary: true, id: "CharacterProfileSave"),
                PageButton("Cancel", services.CancelEdit, id: "CharacterProfileCancel")));
    }

    /// <summary>Checks the editor's name and personality, then saves the profile (its look and voice may be "keep the current").</summary>
    private void Save(Guid id)
    {
        if (nameBox is null || personaChoice is null || lookChoice is null || voiceChoice is null) return;
        var name = nameBox.Text.Trim();
        if (name.Length == 0)
        {
            if (editorProblem is not null) editorProblem.Text = "Give the profile a name.";
            return;
        }
        if ((personaChoice.SelectedItem as ComboBoxItem)?.Tag is not Guid personaId)
        {
            if (editorProblem is not null) editorProblem.Text = "Choose a personality.";
            return;
        }
        var modelId = (lookChoice.SelectedItem as ComboBoxItem)?.Tag as string;
        var voiceId = (voiceChoice.SelectedItem as ComboBoxItem)?.Tag as string;
        var problem = editorProblem;
        services.Save(id, name, personaId, modelId, voiceId, why => { if (problem is not null) problem.Text = why; });
    }
}
