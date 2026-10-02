using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.F5;
using Microsoft.Win32;

namespace Martlet.Desktop;

/// <summary>Adds a voice to the F5 voice list: a recording, its exact transcript and the owner's voice-rights confirmation
/// (voice-rights-v1), which the store requires. F5 copies the voice for each reply; nothing is trained. Martlet keeps its own
/// copy of the recording, so the original can be moved or deleted afterwards.</summary>
internal sealed class F5AddVoiceDialog : ThemedWindow
{
    private readonly string dataDirectory;
    private readonly string destination;
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
    private readonly Button ok = new() { Content = "_Add voice", IsDefault = true, MinWidth = 110 };
    private F5ReferenceSnapshot? added;
    private SoundPlayer? player;

    private F5AddVoiceDialog(string dataDirectory, string destination)
    {
        this.dataDirectory = dataDirectory;
        this.destination = destination;
        SetResourceReference(StyleProperty, "AppWindowStyle");
        Title = "Martlet - Add a voice";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        AutomationProperties.SetAutomationId(this, "F5AddVoiceDialog");
        var root = new StackPanel { Margin = new Thickness(24) };
        var heading = new TextBlock { Text = "Add a voice", TextWrapping = TextWrapping.Wrap };
        heading.SetResourceReference(StyleProperty, "SectionHeading");
        root.Children.Add(heading);
        root.Children.Add(new TextBlock
        {
            Text = "F5 copies a voice from a short recording, with no training: a mono 16-bit PCM WAV of 1 to 30 seconds (5 to 12 " +
                "seconds of clear speech works best) and the exact words it says. Martlet keeps its own copy on this PC, so you " +
                "can move or delete the original afterwards, and sends it with each reply only to the computer that speaks.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6)
        });

        var browse = new Button { Content = "_Browse...", Margin = new Thickness(8, 0, 0, 0), MinWidth = 90 };
        browse.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = "WAV recordings (*.wav)|*.wav", CheckFileExists = true };
            if (dialog.ShowDialog(this) != true) return;
            path.Text = dialog.FileName;
            if (name.Text.Length == 0) name.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
        };
        var play = new Button { Content = "_Play", Margin = new Thickness(8, 0, 0, 0), MinWidth = 90 };
        AutomationProperties.SetAutomationId(play, "F5AddVoicePlay");
        play.Click += async (_, _) => await PlayAsync();
        var pick = new DockPanel();
        DockPanel.SetDock(play, Dock.Right);
        DockPanel.SetDock(browse, Dock.Right);
        pick.Children.Add(play);
        pick.Children.Add(browse);
        pick.Children.Add(path);
        AutomationProperties.SetName(path, "Recording file");
        AutomationProperties.SetName(name, "Voice name");
        AutomationProperties.SetName(transcript, "Exact words in the recording");
        AutomationProperties.SetName(basis, "Whose voice it is");
        AutomationProperties.SetAutomationId(rights, "F5VoiceRights");
        root.Children.Add(new Label { Content = "_Recording", Target = path, Padding = new Thickness(0, 10, 0, 4) });
        root.Children.Add(pick);
        root.Children.Add(new Label { Content = "_Name", Target = name, Padding = new Thickness(0, 8, 0, 4) });
        root.Children.Add(name);
        root.Children.Add(new Label { Content = "_What the recording says, word for word", Target = transcript, Padding = new Thickness(0, 8, 0, 4) });
        root.Children.Add(transcript);
        root.Children.Add(new Label { Content = "W_hose voice is it?", Target = basis, Padding = new Thickness(0, 8, 0, 4) });
        basis.Items.Add(new ComboBoxItem { Content = "My own voice", Tag = F5VoiceRightsBasis.OwnVoice });
        basis.Items.Add(new ComboBoxItem { Content = "Someone who gave me explicit permission", Tag = F5VoiceRightsBasis.ExplicitPermission });
        basis.SelectedIndex = 0;
        root.Children.Add(basis);
        root.Children.Add(rights);

        error.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        root.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "_Cancel", IsCancel = true, MinWidth = 90, Margin = new Thickness(0, 0, 12, 0) };
        ok.SetResourceReference(StyleProperty, "PrimaryButton");
        AutomationProperties.SetAutomationId(ok, "F5AddVoiceOk");
        ok.Click += async (_, _) => await AcceptAsync();
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);
        Content = root;
        Closed += (_, _) => player?.Stop();
    }

    /// <summary>The newly added voice (bound to <paramref name="destination"/>), or null when canceled.</summary>
    internal static F5ReferenceSnapshot? Add(Window owner, string dataDirectory, string destination)
    {
        var dialog = new F5AddVoiceDialog(dataDirectory, destination) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.added : null;
    }

    private async Task PlayAsync()
    {
        error.Visibility = Visibility.Collapsed;
        if (!File.Exists(path.Text))
        {
            Show("Choose the recording (a WAV file) to play it.");
            return;
        }
        try
        {
            if (new FileInfo(path.Text).Length > F5ReferenceLimits.MaximumAudioFileBytes)
            {
                Show("The recording must be a mono 16-bit PCM WAV of 1 to 30 seconds, at most 4 MB.");
                return;
            }
            var audio = await File.ReadAllBytesAsync(path.Text);
            player?.Stop();
            player = new SoundPlayer(new MemoryStream(audio));
            player.Play();
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Show("This recording can't be played: " + failure.Message);
        }
    }

    private async Task AcceptAsync()
    {
        error.Visibility = Visibility.Collapsed;
        player?.Stop();
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
            added = await store.SnapshotAsync(new()
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
