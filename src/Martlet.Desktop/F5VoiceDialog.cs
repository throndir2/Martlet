using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.F5;
using Microsoft.Win32;

namespace Martlet.Desktop;

/// <summary>Chooses the reference voice a host's F5 clones: one already in the local F5 preset store, or a new recording
/// with its exact transcript and the owner's voice-rights confirmation (voice-rights-v1), which the store requires.</summary>
internal sealed class F5VoiceDialog : ThemedWindow
{
    private const string NewKey = "new";
    private readonly string dataDirectory;
    private readonly string destination;
    private readonly ComboBox voices = new();
    private readonly StackPanel newVoice = new();
    private readonly TextBox path = new() { MinWidth = 380 };
    private readonly TextBox name = new();
    private readonly TextBox transcript = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64, MaxHeight = 140 };
    private readonly ComboBox basis = new();
    private readonly CheckBox rights = new()
    {
        Content = new TextBlock
        {
            Text = "I confirm I may use this voice to generate speech with AI: it is my own voice, or its speaker gave me explicit " +
                "permission. The words above are exactly what the recording says.",
            TextWrapping = TextWrapping.Wrap
        },
        Margin = new Thickness(0, 10, 0, 0)
    };
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button ok = new() { Content = "_Use this voice", IsDefault = true, MinWidth = 110 };
    private F5ReferenceSnapshot? chosen;

    private F5VoiceDialog(string dataDirectory, string host, string destination, IReadOnlyList<F5ReferenceSnapshot> existing)
    {
        this.dataDirectory = dataDirectory;
        this.destination = destination;
        SetResourceReference(StyleProperty, "AppWindowStyle");
        Title = "Martlet - Voice";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        AutomationProperties.SetAutomationId(this, "F5VoiceDialog");
        var root = new StackPanel { Margin = new Thickness(24) };
        var heading = new TextBlock { Text = $"Which voice should {host} speak with?", TextWrapping = TextWrapping.Wrap };
        heading.SetResourceReference(StyleProperty, "SectionHeading");
        root.Children.Add(heading);
        root.Children.Add(new TextBlock
        {
            Text = "F5 clones a voice from a short reference recording: a mono 16-bit PCM WAV of 1 to 30 seconds (5 to 15 seconds " +
                "of clear speech works best) and the exact words it says. Martlet keeps a copy in its voice list on this PC and " +
                "sends it with each reply only to that host. Keep the original file where it is; Martlet checks it has not changed.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10)
        });
        root.Children.Add(new Label { Content = "_Voice", Target = voices, Padding = new Thickness(0, 0, 0, 4) });
        AutomationProperties.SetAutomationId(voices, "F5Voice");
        foreach (var snapshot in existing)
            voices.Items.Add(new ComboBoxItem { Content = $"{snapshot.PresetName} (added {snapshot.CreatedAtUtc.ToLocalTime():d})", Tag = snapshot });
        voices.Items.Add(new ComboBoxItem { Content = "A new recording...", Tag = NewKey });
        voices.SelectedIndex = 0;
        voices.SelectionChanged += (_, _) => ShowNew();
        root.Children.Add(voices);

        var browse = new Button { Content = "_Browse...", Margin = new Thickness(8, 0, 0, 0), MinWidth = 90 };
        browse.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = "WAV recordings (*.wav)|*.wav", CheckFileExists = true };
            if (dialog.ShowDialog(this) != true) return;
            path.Text = dialog.FileName;
            if (name.Text.Length == 0) name.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
        };
        var pick = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        pick.Children.Add(browse);
        pick.Children.Add(path);
        AutomationProperties.SetName(path, "Recording file");
        AutomationProperties.SetName(name, "Voice name");
        AutomationProperties.SetName(transcript, "Exact words in the recording");
        AutomationProperties.SetName(basis, "Whose voice it is");
        AutomationProperties.SetAutomationId(rights, "F5VoiceRights");
        newVoice.Children.Add(new Label { Content = "_Recording", Target = path, Padding = new Thickness(0, 10, 0, 4) });
        newVoice.Children.Add(pick);
        newVoice.Children.Add(new Label { Content = "_Name", Target = name, Padding = new Thickness(0, 8, 0, 4) });
        newVoice.Children.Add(name);
        newVoice.Children.Add(new Label { Content = "_What the recording says, word for word", Target = transcript, Padding = new Thickness(0, 8, 0, 4) });
        newVoice.Children.Add(transcript);
        newVoice.Children.Add(new Label { Content = "W_hose voice is it?", Target = basis, Padding = new Thickness(0, 8, 0, 4) });
        basis.Items.Add(new ComboBoxItem { Content = "My own voice", Tag = F5VoiceRightsBasis.OwnVoice });
        basis.Items.Add(new ComboBoxItem { Content = "Someone who gave me explicit permission", Tag = F5VoiceRightsBasis.ExplicitPermission });
        basis.SelectedIndex = 0;
        newVoice.Children.Add(basis);
        newVoice.Children.Add(rights);
        root.Children.Add(newVoice);

        error.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        root.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "_Cancel", IsCancel = true, MinWidth = 90, Margin = new Thickness(0, 0, 12, 0) };
        ok.SetResourceReference(StyleProperty, "PrimaryButton");
        AutomationProperties.SetAutomationId(ok, "F5VoiceOk");
        ok.Click += async (_, _) => await AcceptAsync();
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);
        Content = root;
        ShowNew();
    }

    /// <summary>The chosen or newly added voice snapshot (bound to <paramref name="destination"/>), or null when canceled.</summary>
    internal static F5ReferenceSnapshot? Choose(Window owner, string dataDirectory, string host, string destination)
    {
        IReadOnlyList<F5ReferenceSnapshot> existing;
        try
        {
            using var store = F5Voices.Open(dataDirectory);
            var inspection = store.Inspect();
            existing = inspection.Presets
                .Select(p => p.Snapshots.LastOrDefault(s => s.Rights.ProcessingDestinationId == destination))
                .OfType<F5ReferenceSnapshot>()
                .OrderByDescending(s => s.PresetId == inspection.AppliedPresetId)
                .ToArray();
        }
        catch (F5Exception error)
        {
            ConfirmationDialog.Confirm(owner, F5Voices.Describe(error), "OK");
            return null;
        }
        var dialog = new F5VoiceDialog(dataDirectory, host, destination, existing) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.chosen : null;
    }

    private void ShowNew() =>
        newVoice.Visibility = (voices.SelectedItem as ComboBoxItem)?.Tag is NewKey ? Visibility.Visible : Visibility.Collapsed;

    private async Task AcceptAsync()
    {
        error.Visibility = Visibility.Collapsed;
        if ((voices.SelectedItem as ComboBoxItem)?.Tag is F5ReferenceSnapshot existing)
        {
            chosen = existing;
            DialogResult = true;
            return;
        }
        var problem = !File.Exists(path.Text) ? "Choose the recording (a WAV file)."
            : name.Text.Trim().Length == 0 ? "Give the voice a name."
            : transcript.Text.Trim().Length == 0 ? "Type exactly what the recording says."
            : rights.IsChecked != true ? "Confirm that you may use this voice."
            : null;
        if (problem is not null)
        {
            Show(problem);
            return;
        }
        ok.IsEnabled = false;
        try
        {
            using var store = F5Voices.Open(dataDirectory);
            chosen = await store.SnapshotAsync(new()
            {
                PresetName = name.Text.Trim(),
                AbsoluteSourcePath = Path.GetFullPath(path.Text),
                Transcript = transcript.Text.Trim(),
                Rights = new()
                {
                    AcknowledgementId = Guid.NewGuid(),
                    Basis = (basis.SelectedItem as ComboBoxItem)?.Tag is F5VoiceRightsBasis chosenBasis ? chosenBasis : F5VoiceRightsBasis.OwnVoice,
                    StatementVersion = F5ReferenceLimits.RightsStatementVersion,
                    ProcessingDestinationId = destination,
                    AcknowledgedAtUtc = DateTimeOffset.UtcNow,
                    Confirmed = true
                }
            });
            DialogResult = true;
        }
        catch (F5Exception failure) { Show(F5Voices.Describe(failure)); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException)
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
