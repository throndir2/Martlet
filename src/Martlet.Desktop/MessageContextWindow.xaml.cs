using System.Globalization;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Martlet.Presentation;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>What was sent (a message's What was sent link in the talk window): everything the Thinking model was sent to make one
/// of Martlet's messages, section by section in the order the request sends it, each with a colored bar: the instructions part by
/// part, the conversation history, the message, the notes with it, the context notes, the picture (click it for full size), the
/// recording (Play), the tool rounds, the tools it could call and the request's settings. A reply that called tools sent more than
/// one request; Show picks which (the last, which wrote the answer, first). The text shows on this PC only.</summary>
public partial class MessageContextWindow : ThemedWindow
{
    private MessageContext context;
    private int shown;
    private bool choosing;
    private SoundPlayer? player;
    private DispatcherTimer? playTimer;
    private Button? playButton;

    internal MessageContextWindow(MessageContext context)
    {
        InitializeComponent();
        this.context = context;
        Closed += (_, _) => StopPlaying();
        Display(context);
    }

    /// <summary>What it shows, and which of its requests (for tests).</summary>
    internal MessageContext Context => context;
    internal int Shown => shown;
    /// <summary>Whether the recording plays now.</summary>
    internal bool Playing => player is not null;

    /// <summary>Shows <paramref name="next"/>, at its last request (the one that wrote the answer).</summary>
    internal void Display(MessageContext next)
    {
        context = next;
        Title = "What was sent";
        HeadingText.Text = $"What was sent for this {next.Kind}";
        choosing = true;
        RequestChoice.Items.Clear();
        for (var i = 0; i < next.Requests.Count; i++)
        {
            var item = new ComboBoxItem { Content = next.RequestName(i) };
            AutomationProperties.SetAutomationId(item, $"MessageContextRequest-{i}");
            RequestChoice.Items.Add(item);
        }
        shown = next.Requests.Count - 1;
        RequestChoice.SelectedIndex = shown;
        choosing = false;
        RequestRow.Visibility = next.Requests.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        Render();
    }

    /// <summary>Shows request <paramref name="index"/> of the reply.</summary>
    internal void ShowRequest(int index)
    {
        if (index < 0 || index >= context.Requests.Count) return;
        if (RequestChoice.SelectedIndex != index) RequestChoice.SelectedIndex = index;
        else if (shown != index)
        {
            shown = index;
            Render();
        }
    }

    private void RequestChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (choosing || RequestChoice.SelectedIndex < 0 || RequestChoice.SelectedIndex == shown) return;
        shown = RequestChoice.SelectedIndex;
        Render();
    }

    private void Render()
    {
        StopPlaying();
        var input = context.Requests[shown].Input;
        var sections = context.Sections(shown);
        SummaryText.Text = context.Summary(shown);
        Shares(sections, input);
        SectionsPanel.Children.Clear();
        foreach (var section in sections) SectionsPanel.Children.Add(Section(section, input));
        Scroll.ScrollToTop();
    }

    // The bar at the top: how much of the request each section takes (estimated tokens), with a legend.
    private void Shares(IReadOnlyList<ContextSection> sections, BoundedTextInput input)
    {
        ShareBar.Children.Clear();
        ShareBar.ColumnDefinitions.Clear();
        Legend.Children.Clear();
        var shares = sections.Select(section => (Section: section, Tokens: Weight(section, input))).Where(share => share.Tokens > 0).ToArray();
        var total = (double)shares.Sum(share => share.Tokens);
        if (total <= 0) return;
        foreach (var (section, tokens) in shares)
        {
            var share = string.Create(CultureInfo.CurrentCulture, $"{Short(section.Key)} {tokens / total:P0}");
            ShareBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(tokens, GridUnitType.Star) });
            var piece = new Rectangle { Fill = Color(section.Key), ToolTip = share };
            Grid.SetColumn(piece, ShareBar.ColumnDefinitions.Count - 1);
            ShareBar.Children.Add(piece);
            var label = new TextBlock { Text = share, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap };
            AutomationProperties.SetAutomationId(label, "MessageContextShare-" + section.Key);
            var dot = new Ellipse { Width = 9, Height = 9, Fill = Color(section.Key), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            Legend.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 16, 0), Children = { dot, label } });
        }
    }

    // About how many tokens a section takes (a picture's and a recording's as Martlet reserves them).
    private static int Weight(ContextSection section, BoundedTextInput input) => section.Key switch
    {
        "picture" => input.Image is null ? 0 : BoundedImage.TokenReservation,
        "recording" => input.Audio is { } audio ? (int)Math.Ceiling(audio.Duration.TotalSeconds) * BoundedTextInput.AudioTokensPerSecond : 0,
        "settings" => 0,
        _ => section.Tokens
    };

    private static string Short(string key) => key switch
    {
        "instructions" => "Instructions",
        "history" => "History",
        "message" => "Message",
        "notes" => "Notes",
        "context" => "Context notes",
        "picture" => "Picture",
        "recording" => "Recording",
        "continuation" => "Unfinished answer",
        "toolrounds" => "Tool rounds",
        "tools" => "Tools",
        _ => "Settings"
    };

    /// <summary>Each section's color, the same in the bar, the legend and the section's own bar.</summary>
    internal static Brush Color(string key) => key switch
    {
        "instructions" => Frozen(0x8B, 0x5C, 0xF6),
        "history" => Frozen(0x3B, 0x82, 0xF6),
        "message" => Frozen(0x10, 0xB9, 0x81),
        "notes" => Frozen(0xF5, 0x9E, 0x0B),
        "context" => Frozen(0xEC, 0x48, 0x99),
        "picture" or "recording" => Frozen(0x14, 0xB8, 0xA6),
        "continuation" => Frozen(0x63, 0x66, 0xF1),
        "toolrounds" => Frozen(0xF9, 0x73, 0x16),
        "tools" => Frozen(0x64, 0x74, 0x8B),
        _ => Frozen(0x94, 0xA3, 0xB8)
    };

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private UIElement Section(ContextSection section, BoundedTextInput input)
    {
        var title = new TextBlock { Text = section.Title, FontSize = 15 };
        var size = new TextBlock { Text = Size(section, input), FontSize = 12, FontWeight = FontWeights.Normal, Margin = new Thickness(0, 2, 0, 0) };
        size.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        AutomationProperties.SetAutomationId(size, "MessageContextSize-" + section.Key);
        var where = new TextBlock { Text = section.Where, FontSize = 12, Margin = new Thickness(0, 0, 0, 8) };
        where.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        var body = new StackPanel { Margin = new Thickness(26, 0, 0, 8), Children = { where } };
        var expander = new Expander { Header = new StackPanel { Children = { title, size } }, Content = body, IsExpanded = section.StartsOpen };
        AutomationProperties.SetAutomationId(expander, "MessageContextSection-" + section.Key);
        AutomationProperties.SetName(expander, section.Title);
        // A section's text is made the first time it opens, so a long conversation history costs nothing while it is closed.
        var filled = false;
        void Fill()
        {
            if (filled) return;
            filled = true;
            Blocks(body, section, input);
        }
        if (expander.IsExpanded) Fill();
        expander.Expanded += (_, e) => { if (ReferenceEquals(e.OriginalSource, expander)) Fill(); };
        return new Border
        {
            BorderBrush = Color(section.Key), BorderThickness = new Thickness(4, 0, 0, 0), Padding = new Thickness(8, 0, 0, 0),
            Margin = new Thickness(0, 0, 0, 12), Child = expander
        };
    }

    private static string Size(ContextSection section, BoundedTextInput input) => section.Key switch
    {
        "picture" => input.Image is null ? "Not sent" : string.Create(CultureInfo.CurrentCulture, $"About {BoundedImage.TokenReservation:N0} tokens (each provider counts pictures its own way)"),
        "recording" => input.Audio is { } audio
            ? string.Create(CultureInfo.CurrentCulture, $"{audio.Duration.TotalSeconds:0.0} s, about {Weight(section, input):N0} tokens") : "Not sent",
        "settings" => "Model, where it went and how it answers",
        _ => string.Create(CultureInfo.CurrentCulture, $"{section.Characters:N0} characters, about {section.Tokens:N0} tokens") +
            (section.Blocks.Count > 1 && section.Key is "instructions" or "notes" or "context"
                ? string.Create(CultureInfo.CurrentCulture, $", in {section.Blocks.Count} parts") : "")
    };

    private void Blocks(StackPanel body, ContextSection section, BoundedTextInput input)
    {
        if (section.Key == "picture")
        {
            if (input.Image is { } image) body.Children.Add(Picture(image));
            return;
        }
        if (section.Key == "recording")
        {
            if (input.Audio is { } audio) body.Children.Add(Recording(audio));
            return;
        }
        var history = section.Key == "history";
        for (var i = 0; i < section.Blocks.Count; i++)
        {
            var block = section.Blocks[i];
            // An earlier message is labeled with who said it (the character's name never goes to MCP); a part with its name and size.
            var label = new TextBlock
            {
                Text = history ? block.Label : string.Create(CultureInfo.CurrentCulture, $"{block.Label} · {block.Text.Length:N0} characters"),
                FontWeight = FontWeights.SemiBold, FontSize = 12, Margin = new Thickness(history && block.Mine ? 48 : 0, i == 0 ? 0 : 10, 0, 4)
            };
            if (history) label.SetResourceReference(TextBlock.ForegroundProperty, block.Mine ? "AccentBrush" : "TextBrush");
            else AutomationProperties.SetAutomationId(label, $"MessageContextPart-{section.Key}-{i}");
            var box = new TextBox
            {
                Text = block.Text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, FontSize = 13,
                BorderThickness = new Thickness(0), Padding = new Thickness(10, 8, 10, 8),
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            if (block.Style == ContextBlockStyle.Code) box.FontFamily = new FontFamily("Cascadia Mono, Consolas");
            if (block.Style == ContextBlockStyle.Kept)
            {
                box.SetResourceReference(TextBox.ForegroundProperty, "MutedBrush");
                box.FontStyle = FontStyles.Italic;
            }
            AutomationProperties.SetAutomationId(box, $"MessageContextText-{section.Key}-{i}");
            AutomationProperties.SetName(box, history ? (block.Mine ? "Your earlier message" : "An earlier reply") : block.Label);
            if (history)
            {
                // Like the conversation: your messages on the right, the character's on the left; each message's own Copy would
                // crowd them (Copy all copies everything).
                CopyText.SetButton(box, false);
                box.Margin = block.Mine ? new Thickness(48, 0, 0, 0) : new Thickness(0, 0, 48, 0);
            }
            body.Children.Add(label);
            body.Children.Add(box);
        }
    }

    private UIElement Picture(BoundedImage image)
    {
        if (PictureView.Decode(image.Content.ToArray(), 2048) is not { } source) return Muted("Couldn't show the picture.");
        var picture = new Image
        {
            Source = source, MaxHeight = 380, MaxWidth = 760, Stretch = Stretch.Uniform, Cursor = Cursors.Hand,
            ToolTip = "Click to see it full size"
        };
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
        AutomationProperties.SetAutomationId(picture, "MessageContextPicture");
        AutomationProperties.SetName(picture, "The picture that was sent");
        picture.MouseLeftButtonUp += (_, _) => OpenPicture(source);
        var frame = new Border { CornerRadius = new CornerRadius(10), ClipToBounds = true, BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left, Child = picture };
        frame.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return frame;
    }

    // The picture full size (Esc closes it).
    private void OpenPicture(ImageSource source)
    {
        var viewer = new Window
        {
            Title = "The picture that was sent", Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = Math.Min(SystemParameters.WorkArea.Width * 0.8, 1400), Height = Math.Min(SystemParameters.WorkArea.Height * 0.85, 1100),
            Content = new Image { Source = source, Stretch = Stretch.Uniform, Margin = new Thickness(8) }
        };
        viewer.SetResourceReference(BackgroundProperty, "SurfaceBrush");
        AutomationProperties.SetAutomationId(viewer, "MessageContextPictureViewer");
        viewer.KeyDown += (_, key) => { if (key.Key == Key.Escape) viewer.Close(); };
        viewer.Show();
    }

    private UIElement Recording(BoundedWaveAudio audio)
    {
        var play = new Button { Content = "Play", MinWidth = 110, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetAutomationId(play, "MessageContextPlay");
        AutomationProperties.SetName(play, "Play the recording");
        play.Click += (_, _) => TogglePlay(play, audio);
        var about = Muted(string.Create(CultureInfo.CurrentCulture,
            $"{audio.Duration.TotalSeconds:0.0} s, {audio.Format.SampleRate:N0} Hz, mono, on Windows' default speakers"));
        about.Margin = new Thickness(12, 0, 0, 0);
        about.VerticalAlignment = VerticalAlignment.Center;
        return new StackPanel { Orientation = Orientation.Horizontal, Children = { play, about } };
    }

    // Play, or Stop while it plays: the recording plays from memory on Windows' default speakers (never written to a file).
    private void TogglePlay(Button button, BoundedWaveAudio audio)
    {
        if (player is not null)
        {
            StopPlaying();
            return;
        }
        try
        {
            player = new SoundPlayer(new MemoryStream(audio.Wave.ToArray(), writable: false));
            player.Play();
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or TimeoutException)
        {
            ErrorLog.Warn("What was sent: couldn't play the recording", error);
            player?.Dispose();
            player = null;
            button.Content = "Couldn't play";
            return;
        }
        playButton = button;
        button.Content = "Stop";
        AutomationProperties.SetName(button, "Stop the recording");
        playTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = audio.Duration + TimeSpan.FromMilliseconds(300) };
        playTimer.Tick += (_, _) => StopPlaying();
        playTimer.Start();
    }

    private void StopPlaying()
    {
        playTimer?.Stop();
        playTimer = null;
        if (player is { } playing)
        {
            playing.Stop();
            playing.Dispose();
            player = null;
        }
        if (playButton is { } button)
        {
            button.Content = "Play";
            AutomationProperties.SetName(button, "Play the recording");
            playButton = null;
        }
    }

    private static TextBlock Muted(string text)
    {
        var block = new TextBlock { Text = text, FontSize = 12 };
        block.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        return block;
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e) => CopyText.From(CopyAllButton, context.Text(shown));

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
