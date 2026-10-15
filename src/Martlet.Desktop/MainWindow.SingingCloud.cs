using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Core.Singing;

namespace Martlet.Desktop;

/// <summary>
/// Companion › Singing's cloud providers (<see cref="SingingCloud"/>): a cloud member's line and Settings in the music and voice match
/// lists (its key, and for Mureka whether it sings in Martlet's voice), and each list's Add form (the provider, its model, a key and the
/// owner's agreement that each song may cost money). One key per provider serves both lists; it is kept in Windows Credential Manager
/// and travels to the owner's other computers (<see cref="SingingCloudKeys"/>). Automation IDs: the Add form's
/// <c>SingingCloudProvider-&lt;area&gt;</c>, <c>SingingCloudModel-&lt;area&gt;</c>, <c>SingingCloudKey-&lt;area&gt;</c>,
/// <c>SingingCloudKeyStatus-&lt;area&gt;</c>, <c>SingingCloudConsent-&lt;area&gt;</c>, <c>SingingCloudAdd-&lt;area&gt;</c> and
/// <c>SingingCloudState-&lt;area&gt;</c>; a member's <c>Pool-&lt;area&gt;-CloudKey-&lt;key&gt;</c>,
/// <c>Pool-&lt;area&gt;-CloudKeyStatus-&lt;key&gt;</c>, <c>Pool-&lt;area&gt;-CloudSaveKey-&lt;key&gt;</c> and
/// <c>Pool-singing-OwnVoice-&lt;key&gt;</c>.
/// </summary>
public partial class MainWindow
{
    /// <summary>The provider each list's Add form shows, by area ID.</summary>
    private readonly Dictionary<string, string> singingCloudProviders = new(StringComparer.Ordinal);
    /// <summary>What the last Add did, by area ID (shown under the form).</summary>
    private readonly Dictionary<string, string> singingCloudStates = new(StringComparer.Ordinal);

    private string SingingKeyText(string provider)
    {
        if (store is null) return "no key yet";
        var name = SingingCloud.Name(provider);
        return SingingKeys.Load(store.DataDirectory).For(provider) is not null ? $"its {name} key is saved"
            : SingingCloudKeys.UsesSpeakingKey(store.DataDirectory, provider) ? $"it uses Speaking's {name} key"
            : $"no {name} key yet (add one in its Settings)";
    }

    /// <summary>A cloud member's line: what it is, that each song may cost money, and its key.</summary>
    private string? SingingCloudStatus(PoolArea area, PoolMember member)
    {
        var known = area == PoolAreas.VoiceMatch ? SingingCloud.MatchesVoice(member) : SingingCloud.WritesMusic(member);
        if (!known) return $"Martlet can't use {SingingCloud.Name(member.Provider)} in this list; remove it";
        var parts = new List<string> { SingingCloud.Describe(member, area), "each song may cost money", SingingKeyText(member.Provider!) };
        if (area == PoolAreas.Singing && member.Provider != SingingCloud.ElevenLabs || member.Model != SingingCloud.DefaultModel(member.Provider!, area))
            parts.Insert(1, "model " + member.Model);
        if (!member.Consented(area.Id)) parts.Add("not agreed to: remove it and add it again");
        return string.Join("; ", parts);
    }

    /// <summary>A cloud member's Settings: its key (one per provider, for both lists and all your computers), and for Mureka in the
    /// music list whether it sings in the voice Martlet speaks with.</summary>
    private UIElement? SingingCloudSettings(PoolArea area, PoolMember member)
    {
        if (member.Provider is not { } provider || !SingingCloud.Providers.Contains(provider)) return null;
        var id = $"Pool-{area.Id}-";
        var name = SingingCloud.Name(provider);
        var stack = new StackPanel();
        stack.Children.Add(Note($"Its model is part of the member. To use another {name} model, add {name} again with that model. " +
            $"One {name} key serves both of Singing's lists and your other computers.", new Thickness(0, 0, 0, 8)));
        if (area == PoolAreas.Singing && provider == SingingCloud.Mureka)
        {
            var own = new CheckBox
            {
                IsChecked = SingingCloud.SingsInVoice(member), Margin = new Thickness(0, 0, 0, 8),
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = "Sing in the voice Martlet speaks with (Mureka clones it from the voice's recording, which goes to Mureka). " +
                        "Then songs from Mureka need no voice match."
                }
            };
            AutomationProperties.SetAutomationId(own, id + "OwnVoice-" + member.Key);
            own.Click += (_, _) => SaveSingingMember(area, member.WithSetting(SingingCloud.VoiceSetting, own.IsChecked == true ? SingingCloud.OwnVoice : null),
                own.IsChecked == true ? "Mureka now sings songs in the voice Martlet speaks with." : "Mureka now sings songs with its own singer.");
            stack.Children.Add(own);
        }
        var key = new PasswordBox { MaxLength = SecretLease.MaximumLength, Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(key, $"{name} API key for Singing");
        AutomationProperties.SetAutomationId(key, id + "CloudKey-" + member.Key);
        key.PasswordChanged += (_, _) => tabEdited = true;
        var status = Note(Capitalized(SingingKeyText(provider)) + ". Paste a key to save it for Singing.", new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(status, id + "CloudKeyStatus-" + member.Key);
        var save = PageButton("Save key", () => SaveSingingKeyAsync(provider, key).Forget(), primary: true, id: id + "CloudSaveKey-" + member.Key);
        AutomationProperties.SetName(save, $"Save the {name} key for Singing");
        stack.Children.Add(new Label { Content = "API _key", Target = key, Padding = new Thickness(0, 0, 0, 4) });
        stack.Children.Add(key);
        stack.Children.Add(status);
        stack.Children.Add(Row(save));
        return stack;
    }

    private async Task SaveSingingKeyAsync(string provider, PasswordBox box)
    {
        if (store is null || closing) return;
        var name = SingingCloud.Name(provider);
        using (var entered = box.SecurePassword)
            if (entered.Length == 0) { ActionText.Text = $"Paste your {name} API key first."; return; }
        var key = TakeKey(box);
        try
        {
            await SingingCloudKeys.SaveAsync(store.DataDirectory, provider, key, lifetime.Token);
            QueueSettingsSync();
            ErrorLog.Info($"Singing: saved a {name} key for Singing.");
            ActionText.Text = $"Saved the {name} key for Singing in Windows Credential Manager. Your other computers get it too.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is InvalidOperationException or ContractException) { ActionText.Text = error.Message; }
        finally
        {
            key.Dispose();
            if (!closing && openTab == CompanionTab.Singing) RenderTab();
        }
    }

    /// <summary>A list's Add a cloud provider form: the providers that do this list's part, the model, a key (or the one already
    /// saved for Singing, or Speaking's ElevenLabs key) and the owner's agreement to send songs there and pay. Add puts it last.</summary>
    private UIElement SingingCloudAddForm(PoolArea area)
    {
        var providers = area == PoolAreas.VoiceMatch ? SingingCloud.VoiceMatchProviders : SingingCloud.MusicProviders;
        var provider = singingCloudProviders.GetValueOrDefault(area.Id) is { } chosen && providers.Contains(chosen) ? chosen : providers[0];
        var name = SingingCloud.Name(provider);
        var choice = new ComboBox { MinHeight = 30, MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var each in providers)
            choice.Items.Add(new ComboBoxItem
            {
                Content = area == PoolAreas.VoiceMatch ? SingingCloud.Name(each) + " Voice Changer" : each == SingingCloud.ElevenLabs ? "ElevenLabs Music"
                    : each == SingingCloud.Fal ? "fal.ai (MiniMax Music or ACE-Step)" : SingingCloud.Name(each),
                Tag = each
            });
        choice.SelectedIndex = providers.ToList().IndexOf(provider);
        AutomationProperties.SetName(choice, $"Cloud provider for {area.Title}");
        AutomationProperties.SetAutomationId(choice, "SingingCloudProvider-" + area.Id);
        choice.SelectionChanged += (_, _) =>
        {
            if (choice.SelectedItem is not ComboBoxItem { Tag: string tag } || tag == provider) return;
            singingCloudProviders[area.Id] = tag;
            singingCloudStates.Remove(area.Id);
            Dispatcher.InvokeAsync(() => { if (!closing && openTab == CompanionTab.Singing) RenderTab(); });
        };
        var model = new TextBox { MaxLength = 128, Width = 420, HorizontalAlignment = HorizontalAlignment.Left, Text = SingingCloud.DefaultModel(provider, area) };
        AutomationProperties.SetName(model, $"{name} model for {area.Title}");
        AutomationProperties.SetAutomationId(model, "SingingCloudModel-" + area.Id);
        var key = new PasswordBox { MaxLength = SecretLease.MaximumLength, Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(key, $"{name} API key");
        AutomationProperties.SetAutomationId(key, "SingingCloudKey-" + area.Id);
        var has = store is not null && SingingCloudKeys.Has(store.DataDirectory, provider);
        var keyStatus = Note(has ? $"Leave this empty to use the {name} key Martlet has ({SingingKeyText(provider)}), or paste another key."
            : $"Paste your {name} API key. Martlet saves it in Windows Credential Manager and gives it to your other computers.", new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(keyStatus, "SingingCloudKeyStatus-" + area.Id);
        var consent = new CheckBox
        {
            Margin = new Thickness(0, 12, 0, 8),
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = $"I add {name} to Singing's {(area == PoolAreas.VoiceMatch ? "voice match" : "music")} list. " +
                SingingCloud.Disclosure(provider, area) }
        };
        AutomationProperties.SetAutomationId(consent, "SingingCloudConsent-" + area.Id);
        model.TextChanged += (_, _) => { if (model.IsKeyboardFocusWithin) { tabEdited = true; consent.IsChecked = false; } };
        key.PasswordChanged += (_, _) => tabEdited = true;
        var add = PageButton($"Add {name}", () => AddSingingCloudAsync(area, provider, model.Text.Trim(), key, consent.IsChecked == true).Forget(),
            id: "SingingCloudAdd-" + area.Id);
        var state = Note(singingCloudStates.GetValueOrDefault(area.Id) ?? "", new Thickness(0, 4, 0, 0));
        state.Visibility = singingCloudStates.ContainsKey(area.Id) ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetAutomationId(state, "SingingCloudState-" + area.Id);
        var stack = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        stack.Children.Add(new TextBlock { Text = "A cloud provider", FontWeight = FontWeights.SemiBold });
        stack.Children.Add(Note(provider switch
        {
            SingingCloud.ElevenLabs when area == PoolAreas.VoiceMatch => "ElevenLabs' Voice Changer sings each song's vocals in Martlet's voice, " +
                "from a voice ElevenLabs makes once from the voice's recording (Instant Voice Clone: a paid ElevenLabs plan).",
            SingingCloud.ElevenLabs => "Eleven Music writes each song from its lyrics and style with its own singer (a paid ElevenLabs plan).",
            SingingCloud.Mureka => "Mureka writes each song from its lyrics and style; it can also sing in Martlet's voice (its Settings).",
            _ => $"fal.ai runs a music model for Martlet: {SingingCloud.FalMiniMax} (MiniMax Music) or {SingingCloud.FalAceStep}."
        }, new Thickness(0, 2, 0, 8)));
        stack.Children.Add(Labeled("Provider", choice));
        stack.Children.Add(new Label { Content = "_Model", Target = model, Padding = new Thickness(0, 8, 0, 4) });
        stack.Children.Add(model);
        stack.Children.Add(new Label { Content = "API _key", Target = key, Padding = new Thickness(0, 8, 0, 4) });
        stack.Children.Add(key);
        stack.Children.Add(keyStatus);
        stack.Children.Add(consent);
        stack.Children.Add(Row(add));
        stack.Children.Add(state);
        return stack;
    }

    private async Task AddSingingCloudAsync(PoolArea area, string provider, string model, PasswordBox keyBox, bool consent)
    {
        if (store is null || closing) return;
        var name = SingingCloud.Name(provider);
        string Say(string text) => ActionText.Text = singingCloudStates[area.Id] = text;
        if (!consent)
        {
            Say($"Tick the box to agree, then press Add {name}.");
            RenderTab();
            return;
        }
        if (!SingingCloud.Model(model))
        {
            Say("Write the model as the provider names it (letters, digits and - _ . / :).");
            RenderTab();
            return;
        }
        var directory = store.DataDirectory;
        SecretLease? key = null;
        try
        {
            var member = PoolMember.Cloud(provider, model).WithConsent(area.Id, DateTimeOffset.UtcNow);
            var list = WorkSharingRoster.Shared.Pool(directory, area) ?? new PoolList { Area = area.Id };
            if (list.Find(member.Key) is not null)
                throw new ContractException(ErrorCode.InvalidContract, $"{SingingCloud.Describe(member, area)} is in the list already.");
            using (var entered = keyBox.SecurePassword)
                if (entered.Length > 0) key = TakeKey(keyBox);
            if (key is null && !SingingCloudKeys.Has(directory, provider)) throw new ContractException(ErrorCode.InvalidContract, $"Paste your {name} API key first.");
            if (key is not null) await SingingCloudKeys.SaveAsync(directory, provider, key, lifetime.Token);
            if (!PoolSettings.SaveFor(directory, area, list.With(member)))
                throw new InvalidOperationException($"Couldn't save the {area.Title} list on this PC.");
            QueueSettingsSync();
            ErrorLog.Info($"Singing: added {SingingCloud.Describe(member, area)} to the {area.Title} list (the owner agreed to pay).");
            Say($"{SingingCloud.Describe(member, area)} is now last in the list. Move it up to use it sooner." +
                (key is null ? "" : " Its API key is saved in Windows Credential Manager."));
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is ContractException or InvalidOperationException) { Say(error.Message); }
        finally
        {
            key?.Dispose();
            if (!closing && openTab == CompanionTab.Singing) RenderTab();
        }
    }
}
