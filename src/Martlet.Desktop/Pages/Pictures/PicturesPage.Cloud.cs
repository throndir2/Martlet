using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Martlet.Core.Cluster;
using Martlet.Core.Pictures;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;
using Martlet.Presentation;
using Martlet.Providers.Pictures;

namespace Martlet.Desktop;

/// <summary>Companion › Picture making's cloud places: a cloud place's key, and the Add a cloud provider form.</summary>
internal sealed partial class PicturesPage
{
    // A cloud place's settings: its key (its model is part of the place: another model is another place).
    private void KeyEditor(PicturesPageModel model, PoolMember member, int i, Panel stack)
    {
        var name = PictureMakingPoolMembers.ProviderName(member.Provider);
        var key = new PasswordBox { MaxLength = SecretLease.MaximumLength, Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(key, $"{name} API key for pictures");
        AutomationProperties.SetAutomationId(key, $"PicturesKey-{i}");
        InputHint.SetText(key, member.Provider == PictureMakingPoolMembers.NvidiaBuild ? "Paste your NVIDIA Build key (nvapi-…)"
            : member.Provider == PictureMakingPoolMembers.OpenRouter ? "Paste your OpenRouter key (sk-or-v1-…)"
            : $"Paste your {name} API key");
        key.PasswordChanged += (_, _) => services.MarkEdited();
        var status = Note(MainWindow.Capitalized(model.KeyText(member)) + "; paste a key to save it as its own.", new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(status, $"PicturesKeyStatus-{i}");
        var save = PageButton("Save key", () => services.SaveKey(member, key), primary: true, id: $"PicturesSaveKey-{i}");
        AutomationProperties.SetName(save, $"Save the {name} key for pictures");
        stack.Children.Add(Note($"Its model is part of the place, so add {name} again to draw with another {name} model.",
            new Thickness(0, 0, 0, 8)));
        stack.Children.Add(new Label { Content = "API _key", Target = key, Padding = new Thickness(0, 0, 0, 4) });
        stack.Children.Add(key);
        stack.Children.Add(status);
        stack.Children.Add(Row(save));
    }

    /// <summary>The list's Add a cloud provider form: OpenRouter or NVIDIA Build, its model, a key (or Thinking's for the same
    /// provider) and the owner's agreement to send descriptions there and pay. Add puts it last in the list.</summary>
    private UIElement CloudAddForm()
    {
        var provider = cloudProvider;
        var name = PictureMakingPoolMembers.ProviderName(provider);
        var draft = new PicturesSettings { Place = provider == PictureMakingPoolMembers.NvidiaBuild ? PicturePlace.NvidiaBuild : PicturePlace.OpenRouter };
        var thinking = services.Settings.ThinkingRoute();
        var choice = new ComboBox { MinHeight = 30, MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left };
        choice.Items.Add(new ComboBoxItem { Content = "OpenRouter", Tag = PictureMakingPoolMembers.OpenRouter });
        choice.Items.Add(new ComboBoxItem { Content = "NVIDIA Build", Tag = PictureMakingPoolMembers.NvidiaBuild });
        choice.SelectedIndex = provider == PictureMakingPoolMembers.NvidiaBuild ? 1 : 0;
        AutomationProperties.SetName(choice, "Cloud provider for pictures");
        AutomationProperties.SetAutomationId(choice, "PicturesCloudProvider");
        choice.SelectionChanged += (_, _) =>
        {
            if (choice.SelectedItem is not ComboBoxItem { Tag: string tag } || tag == cloudProvider) return;
            cloudProvider = tag;
            services.CloudState = null;
            services.RedrawLater();
        };
        var model = new TextBox { MaxLength = 128, Width = 420, HorizontalAlignment = HorizontalAlignment.Left, Text = draft.Model };
        AutomationProperties.SetName(model, "Picture model ID");
        AutomationProperties.SetAutomationId(model, "PicturesModel");
        InputHint.SetText(model, draft.Model);
        var key = new PasswordBox { MaxLength = SecretLease.MaximumLength, Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(key, $"{name} API key");
        AutomationProperties.SetAutomationId(key, "PicturesKey");
        InputHint.SetText(key, provider == PictureMakingPoolMembers.NvidiaBuild ? "Paste your NVIDIA Build key (nvapi-…)"
            : "Paste your OpenRouter key (sk-or-v1-…)");
        var elsewhere = draft.UsesThinkingKey(thinking) ? null : services.SavedKeyWhere(draft.Origin);
        var keyStatus = Note(draft.UsesThinkingKey(thinking) ? $"Leave this empty to use Thinking's {name} key, or paste another key."
            : elsewhere is not null ? $"Your {name} key from {elsewhere} is saved: leave this empty to use it here too, or paste a new key."
            : $"Paste your {name} API key; Martlet saves it in Windows Credential Manager.", new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(keyStatus, "PicturesKeyStatus");
        var consent = new CheckBox { Margin = new Thickness(0, 12, 0, 8),
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap,
                Text = $"I add {name} to the Picture making list: the description of each picture drawn there goes to {name}, and each picture may cost money." } };
        AutomationProperties.SetAutomationId(consent, "PicturesConsent");
        model.TextChanged += (_, _) => { if (model.IsKeyboardFocusWithin) { services.MarkEdited(); consent.IsChecked = false; } };
        key.PasswordChanged += (_, _) => services.MarkEdited();
        var add = PageButton($"Add {name}", () => services.AddCloud(provider, model.Text.Trim(), key, consent.IsChecked == true), id: "PicturesAddCloud");
        var state = Note(services.CloudState ?? "", new Thickness(0, 4, 0, 0));
        state.Visibility = services.CloudState is null ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetAutomationId(state, "PicturesCloudState");
        var stack = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        stack.Children.Add(new TextBlock { Text = "A cloud provider", FontWeight = FontWeights.SemiBold });
        stack.Children.Add(Note(provider == PictureMakingPoolMembers.NvidiaBuild
            ? "NVIDIA's FLUX models (build.nvidia.com), with the same nvapi- key as NVIDIA Build's chat models."
            : "Any OpenRouter model that draws (openrouter.ai/models, output: image), for one picture of about 1K in the shape Martlet chose.",
            new Thickness(0, 2, 0, 8)));
        stack.Children.Add(MainWindow.Labeled("Provider", choice));
        stack.Children.Add(new Label { Content = "_Model ID", Target = model, Padding = new Thickness(0, 8, 0, 4) });
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
