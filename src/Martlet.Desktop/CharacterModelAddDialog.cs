using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Core.Characters;
using Microsoft.Win32;

namespace Martlet.Desktop;

/// <summary>Adds a character: a Live2D model (its .model3.json, with the rest of its folder) or a VRM model (.vrm). Martlet
/// keeps its own copy, so the original can be moved or deleted afterwards, and shares it with the owner's paired Martlet
/// computers.</summary>
internal sealed class CharacterModelAddDialog : ThemedWindow
{
    private readonly string dataDirectory;
    private readonly string device;
    private readonly TextBox path = new() { MinWidth = 380 };
    private readonly TextBox name = new();
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button ok = new() { Content = "_Add character", IsDefault = true, MinWidth = 120 };
    private CharacterModel? added;

    private CharacterModelAddDialog(string dataDirectory, string device)
    {
        this.dataDirectory = dataDirectory;
        this.device = device;
        SetResourceReference(StyleProperty, "AppWindowStyle");
        Title = "Martlet - Add a character";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        AutomationProperties.SetAutomationId(this, "CharacterModelAddDialog");
        var root = new StackPanel { Margin = new Thickness(24) };
        var heading = new TextBlock { Text = "Add a character", TextWrapping = TextWrapping.Wrap };
        heading.SetResourceReference(StyleProperty, "SectionHeading");
        root.Children.Add(heading);
        root.Children.Add(new TextBlock
        {
            Text = "Choose a Live2D model's .model3.json (Martlet copies the files it uses: model, textures, motions and sounds, up " +
                "to 128 MB) or a VRM 1.0 .vrm file of up to 32 MB. Martlet keeps a copy and shares it with your paired Martlet computers, so any of " +
                "them can show this character. Only add models you may use.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6)
        });

        var browse = new Button { Content = "_Browse...", Margin = new Thickness(8, 0, 0, 0), MinWidth = 90 };
        browse.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = "Character models (*.model3.json;*.vrm)|*.model3.json;*.vrm", CheckFileExists = true };
            if (dialog.ShowDialog(this) != true) return;
            path.Text = dialog.FileName;
            if (name.Text.Length == 0) name.Text = SharedCharacterModels.NameFor(dialog.FileName);
        };
        var pick = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        pick.Children.Add(browse);
        pick.Children.Add(path);
        AutomationProperties.SetName(path, "Model file");
        AutomationProperties.SetName(name, "Character name");
        AutomationProperties.SetAutomationId(path, "CharacterModelAddPath");
        AutomationProperties.SetAutomationId(name, "CharacterModelAddName");
        AutomationProperties.SetAutomationId(error, "CharacterModelAddProblem");
        root.Children.Add(new Label { Content = "_Model file", Target = path, Padding = new Thickness(0, 10, 0, 4) });
        root.Children.Add(pick);
        root.Children.Add(new Label { Content = "_Name", Target = name, Padding = new Thickness(0, 8, 0, 4) });
        root.Children.Add(name);

        error.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        root.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "_Cancel", IsCancel = true, MinWidth = 90, Margin = new Thickness(0, 0, 12, 0) };
        AutomationProperties.SetAutomationId(cancel, "CharacterModelAddCancel");
        ok.SetResourceReference(StyleProperty, "PrimaryButton");
        AutomationProperties.SetAutomationId(ok, "CharacterModelAddOk");
        ok.Click += async (_, _) => await AcceptAsync();
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);
        Content = root;
    }

    /// <summary>The newly added character, or null when canceled.</summary>
    internal static CharacterModel? Add(Window owner, string dataDirectory, string device)
    {
        var dialog = new CharacterModelAddDialog(dataDirectory, device) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.added : null;
    }

    private async Task AcceptAsync()
    {
        error.Visibility = Visibility.Collapsed;
        var file = path.Text.Trim();
        var renderer = file.EndsWith(".vrm", StringComparison.OrdinalIgnoreCase) ? AvatarRenderer.Vrm
            : file.EndsWith(".model3.json", StringComparison.OrdinalIgnoreCase) ? AvatarRenderer.Live2D
            : (AvatarRenderer?)null;
        var problem = !Path.IsPathFullyQualified(file) || !File.Exists(file) ? "Choose the model file (a .model3.json or .vrm)."
            : renderer is null ? "Choose a Live2D .model3.json or a VRM .vrm file."
            : name.Text.Trim().Length == 0 ? "Give the character a name."
            : !CharacterModelLibrary.IsName(name.Text.Trim()) ? "Use a name of at most 80 characters on one line."
            : null;
        if (problem is not null)
        {
            Show(problem);
            return;
        }
        ok.IsEnabled = false;
        try
        {
            added = await SharedCharacterModels.ImportAsync(dataDirectory, renderer!.Value, Path.GetFullPath(file), name.Text.Trim(), device,
                DateTimeOffset.UtcNow, CancellationToken.None);
            DialogResult = true;
        }
        catch (Exception failure) when (SharedCharacterModels.IsFailure(failure) || failure is ArgumentException)
        {
            Show(failure.Message);
        }
        finally { ok.IsEnabled = true; }
    }

    private void Show(string text)
    {
        error.Text = text;
        error.Visibility = Visibility.Visible;
    }
}
