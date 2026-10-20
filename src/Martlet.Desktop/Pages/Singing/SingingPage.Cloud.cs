using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Core.Singing;
using Martlet.HostClient;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>Companion › Singing's cloud providers: a cloud member's Settings (its key, and Mureka's own voice) and each list's Add form.</summary>
internal sealed partial class SingingPage
{
    /// <summary>The provider each list's Add form shows, by area ID.</summary>
    private readonly Dictionary<string, string> cloudProviders = new(StringComparer.Ordinal);

    /// <summary>A cloud member's Settings: its key (one per provider, for both lists and all your computers), and for Mureka in the
    /// music list whether it sings in the voice Martlet speaks with.</summary>
    private UIElement? CloudSettings(PoolArea area, PoolMember member)
    {
        if (member.Provider is not { } provider || !SingingCloud.Providers.Contains(provider)) return null;
        var id = $"Pool-{area.Id}-";
        var name = SingingCloud.Name(provider);
        var stack = new StackPanel();
        stack.Children.Add(HelpTip.Explain($"To use another {name} model, add {name} again with that model. One {name} key serves " +
            "both of Singing's lists and your other computers.", new Thickness(0, 0, 0, 8), $"{id}CloudModel-{member.Key}", $"{name} models",
            wiki: "Singing#cloud-providers"));
        if (area == PoolAreas.Singing && provider == SingingCloud.Mureka)
        {
            var own = new CheckBox
            {
                IsChecked = SingingCloud.SingsInVoice(member), Margin = new Thickness(0, 0, 0, 8),
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = "Sing in the voice Martlet speaks with (Mureka clones it from the voice's recording, which goes to Mureka), " +
                        "so songs from Mureka need no voice match."
                }
            };
            AutomationProperties.SetAutomationId(own, id + "OwnVoice-" + member.Key);
            own.Click += (_, _) => services.SaveMember(area, member.WithSetting(SingingCloud.VoiceSetting, own.IsChecked == true ? SingingCloud.OwnVoice : null),
                own.IsChecked == true ? "Mureka now sings songs in the voice Martlet speaks with." : "Mureka now sings songs with its own singer.");
            stack.Children.Add(own);
        }
        var key = new PasswordBox { MaxLength = SecretLease.MaximumLength, Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(key, $"{name} API key for Singing");
        AutomationProperties.SetAutomationId(key, id + "CloudKey-" + member.Key);
        InputHint.SetText(key, $"Paste your {name} API key");
        key.PasswordChanged += (_, _) => services.MarkEdited();
        var status = Note(MainWindow.Capitalized(SingingPageModel.KeyText(services.DataDirectory, provider)) + "; paste a key to save it for Singing.", new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(status, id + "CloudKeyStatus-" + member.Key);
        var save = PageButton("Save key", () => services.SaveKey(provider, key), primary: true, id: id + "CloudSaveKey-" + member.Key);
        AutomationProperties.SetName(save, $"Save the {name} key for Singing");
        stack.Children.Add(new Label { Content = "API _key", Target = key, Padding = new Thickness(0, 0, 0, 4) });
        stack.Children.Add(key);
        stack.Children.Add(status);
        stack.Children.Add(Row(save));
        return stack;
    }

    /// <summary>A list's Add a cloud provider form: the providers that do this list's part, the model, a key (or the one already
    /// saved for Singing, or Speaking's ElevenLabs key) and the owner's agreement to send songs there and pay. Add puts it last.</summary>
    private UIElement CloudAddForm(PoolArea area)
    {
        var providers = area == PoolAreas.VoiceMatch ? SingingCloud.VoiceMatchProviders : SingingCloud.MusicProviders;
        var provider = cloudProviders.GetValueOrDefault(area.Id) is { } chosen && providers.Contains(chosen) ? chosen : providers[0];
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
            cloudProviders[area.Id] = tag;
            services.ClearCloudState(area.Id);
            services.RedrawLater();
        };
        var model = new TextBox { MaxLength = 128, Width = 420, HorizontalAlignment = HorizontalAlignment.Left, Text = SingingCloud.DefaultModel(provider, area) };
        AutomationProperties.SetName(model, $"{name} model for {area.Title}");
        AutomationProperties.SetAutomationId(model, "SingingCloudModel-" + area.Id);
        InputHint.SetText(model, SingingCloud.DefaultModel(provider, area));
        var key = new PasswordBox { MaxLength = SecretLease.MaximumLength, Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(key, $"{name} API key");
        AutomationProperties.SetAutomationId(key, "SingingCloudKey-" + area.Id);
        InputHint.SetText(key, $"Paste your {name} API key");
        var has = services.DataDirectory is { } directory && SingingCloudKeys.Has(directory, provider);
        var keyStatus = Note(has ? $"Leave this empty to use the {name} key Martlet has ({SingingPageModel.KeyText(services.DataDirectory, provider)}), or paste another key."
            : $"Paste your {name} API key: Martlet saves it in Windows Credential Manager and gives it to your other computers.", new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(keyStatus, "SingingCloudKeyStatus-" + area.Id);
        var consent = new CheckBox
        {
            Margin = new Thickness(0, 12, 0, 8),
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = $"I add {name} to Singing's {(area == PoolAreas.VoiceMatch ? "voice match" : "music")} list: " +
                MainWindow.Lowered(SingingCloud.Disclosure(provider, area)) }
        };
        AutomationProperties.SetAutomationId(consent, "SingingCloudConsent-" + area.Id);
        model.TextChanged += (_, _) => { if (model.IsKeyboardFocusWithin) { services.MarkEdited(); consent.IsChecked = false; } };
        key.PasswordChanged += (_, _) => services.MarkEdited();
        var add = PageButton($"Add {name}", () => services.AddCloud(area, provider, model.Text.Trim(), key, consent.IsChecked == true),
            id: "SingingCloudAdd-" + area.Id);
        var state = Note(services.CloudState(area.Id) ?? "", new Thickness(0, 4, 0, 0));
        state.Visibility = services.CloudState(area.Id) is not null ? Visibility.Visible : Visibility.Collapsed;
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
        stack.Children.Add(MainWindow.Labeled("Provider", choice));
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
}
