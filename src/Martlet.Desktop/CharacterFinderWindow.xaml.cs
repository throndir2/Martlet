using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Martlet.Avatar.Hosting;

namespace Martlet.Desktop;

/// <summary>What the Find a character window needs from Martlet.</summary>
internal interface ICharacterFinderHost
{
    VRoidHubService VRoidHub { get; }
    /// <summary>Asks the Thinking model to write the personality of <paramref name="name"/>; null with why not.</summary>
    Task<(string? Text, string? Failure)> WritePersonalityAsync(string name, string? notes, CancellationToken token);
    /// <summary>Makes the character (a personality and a profile with <paramref name="look"/>, or without a new look when it is
    /// null) and switches Martlet to it. Reports progress in words; returns what happened.</summary>
    Task<string> CreateCharacterAsync(string name, string personality, CharacterModelCandidate? look, Action<string> progress, CancellationToken token);
    /// <summary>The backup finder: web pages that may have the character's model or voice recordings (nothing is downloaded).</summary>
    Task<Martlet.Conversation.CharacterWebFinds> FindOnWebAsync(string name, bool models, bool voices, CancellationToken token);
    /// <summary>Opens Add a character for a model the owner downloaded; then Martlet shows it.</summary>
    void AddModelFile();
    /// <summary>Opens Companion › Voice, where Add a voice takes a clip the owner downloaded.</summary>
    void OpenVoices();
}

/// <summary>
/// Find a character (Companion › Character, docs/AVATARS.md#find-a-character-online): the owner names a character; Martlet
/// writes its personality with the Thinking model and searches VRoid Hub and the Open Source Avatars registry for models of
/// it. Each look shows its picture, creator, source, license and every condition of use before it can be used. A VRoid Hub
/// look is shown from memory with the owner's linked VRoid Hub account and never saved; a free Open Source Avatars model joins
/// your characters. Every control has an automation ID; the status lines are MCP SafeValues (never the typed name or text).
/// </summary>
public partial class CharacterFinderWindow : ThemedWindow
{
    private const int ResultCount = 24;
    private readonly ICharacterFinderHost host;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? search, writing;
    private IReadOnlyList<CharacterModelCandidate> results = [];
    private bool busy;

    internal CharacterFinderWindow(ICharacterFinderHost host)
    {
        InitializeComponent();
        this.host = host;
        Closed += (_, _) => lifetime.Cancel();
        host.VRoidHub.Changed += VRoidHubChanged;
        Closed += (_, _) => host.VRoidHub.Changed -= VRoidHubChanged;
        Loaded += (_, _) =>
        {
            ShowVRoidHub();
            QueryText.Focus();
        };
        PersonalityStateText.Text = "Search to have the Thinking model write a personality, or type your own.";
    }

    private void VRoidHubChanged() => Dispatcher.BeginInvoke(() =>
    {
        ShowVRoidHub();
        ShowResults();
    });

    private void ShowVRoidHub()
    {
        var linked = host.VRoidHub.Tokens();
        VRoidHubText.Text = !VRoidHubService.CanLink ? "This Martlet build can't link VRoid Hub. VRoid Hub models are listed, but only free Open Source Avatars models can be used."
            : linked is not null ? $"Linked as {linked.UserName}. Your own VRoid Hub models come first, and models their creators let anyone use can be shown."
            : "Not linked. Link your VRoid Hub account to show VRoid Hub models (you sign in on VRoid Hub's own page).";
        LinkButton.Visibility = VRoidHubService.CanLink && linked is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Link_Click(object sender, RoutedEventArgs e)
    {
        LinkButton.IsEnabled = false;
        StatusText.Text = "Sign in on VRoid Hub's page in your browser and allow Martlet. This window waits up to 5 minutes.";
        try
        {
            var tokens = await host.VRoidHub.LinkAsync(lifetime.Token);
            StatusText.Text = $"VRoid Hub is linked as {tokens.UserName}." + (results.Count > 0 ? " Search again to see your own models." : "");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is VRoidHubException or HttpRequestException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            StatusText.Text = "VRoid Hub wasn't linked: " + error.Message;
        }
        finally
        {
            LinkButton.IsEnabled = true;
            ShowVRoidHub();
        }
    }

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        var query = QueryText.Text.Trim();
        if (query.Length == 0)
        {
            StatusText.Text = "Type the character's name first.";
            QueryText.Focus();
            return;
        }
        search?.Cancel();
        search = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = search.Token;
        SearchButton.IsEnabled = false;
        ResultsText.Text = "Searching VRoid Hub and Open Source Avatars...";
        ResultRows.Children.Clear();
        StatusText.Text = "";
        // The personality is written meanwhile, unless the owner already wrote or changed one.
        var write = PersonalityText.Text.Trim().Length == 0 ? WriteAsync(query) : Task.CompletedTask;
        try
        {
            var found = await host.VRoidHub.SearchAsync(query, ResultCount, token);
            if (token.IsCancellationRequested) return;
            // The owner's own models first, then the looks Martlet can use now, then the rest in the sources' order.
            var linkedNow = host.VRoidHub.Tokens() is not null;
            results = found.Candidates.Select((c, i) => (c, i)).OrderByDescending(x => x.c.Own)
                .ThenByDescending(x => x.c.Usable && (x.c.Kept || linkedNow)).ThenBy(x => x.i).Select(x => x.c).ToArray();
            ResultsText.Text = (results.Count == 0 ? "No looks were found. Try another spelling or a shorter name, or use the character without a new look."
                : $"{results.Count} look{(results.Count == 1 ? "" : "s")} found: {results.Count(r => r.Source == CharacterModelCandidate.VRoidHubSource)} on VRoid Hub, " +
                  $"{results.Count(r => r.Source == CharacterModelCandidate.OpenSourceAvatarsSource)} on Open Source Avatars.") +
                (found.Notes.Count > 0 ? " " + string.Join(" ", found.Notes) : "");
            ShowResults();
            // Nothing Martlet can use by itself: look on the web for a model too. The voice search always runs (Martlet finds no
            // voices by itself).
            var linked = host.VRoidHub.Tokens() is not null;
            var usable = results.Any(r => r.Usable && (r.Kept || linked));
            FindOnWebAsync(query, models: !usable, voices: true).Forget();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is Martlet.Core.Contracts.ContractException or HttpRequestException or VRoidHubException)
        {
            ResultsText.Text = "The search didn't work: " + error.Message;
        }
        finally
        {
            SearchButton.IsEnabled = true;
        }
        await write;
    }

    private void Write_Click(object sender, RoutedEventArgs e)
    {
        var query = QueryText.Text.Trim();
        if (query.Length == 0)
        {
            StatusText.Text = "Type the character's name first.";
            return;
        }
        WriteAsync(query).Forget();
    }

    private async Task WriteAsync(string name)
    {
        writing?.Cancel();
        writing = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = writing.Token;
        WriteButton.IsEnabled = false;
        PersonalityStateText.Text = "The Thinking model is writing the personality...";
        try
        {
            var notes = NotesText.Text.Trim();
            var (text, failure) = await host.WritePersonalityAsync(name, notes.Length == 0 ? null : notes, token);
            if (token.IsCancellationRequested) return;
            if (text is { Length: > 0 })
            {
                PersonalityText.Text = text;
                PersonalityStateText.Text = "Written by the Thinking model. Change anything you like before you use it.";
            }
            else PersonalityStateText.Text = $"The Thinking model didn't write one ({failure ?? "no answer"}). Type a personality yourself, or try again.";
        }
        catch (OperationCanceledException) { }
        finally { WriteButton.IsEnabled = true; }
    }

    private void ShowResults()
    {
        ResultRows.Children.Clear();
        var linked = host.VRoidHub.Tokens() is not null;
        for (var index = 0; index < results.Count; index++)
            ResultRows.Children.Add(Row(results[index], index, linked));
    }

    private UIElement Row(CharacterModelCandidate candidate, int index, bool linked)
    {
        var picture = new Image { Width = 96, Height = 128, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 12, 0) };
        if (candidate.ThumbnailUrl is { } url)
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(url);
                bitmap.DecodePixelWidth = 192;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bitmap.EndInit();
                picture.Source = bitmap;
            }
            catch (Exception error) when (error is UriFormatException or NotSupportedException or InvalidOperationException) { }
        AutomationProperties.SetName(picture, $"Picture of {candidate.Name}");
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = candidate.Name + (candidate.Own ? "  ·  your own" : ""), FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var source = candidate.Source == CharacterModelCandidate.VRoidHubSource ? "VRoid Hub" : "Open Source Avatars";
        var about = new TextBlock
        {
            Text = $"By {candidate.Author} · {source} · {candidate.License}" + (candidate.VrmVersion is { } v ? $" · VRM {v}" : "") +
                (candidate.Bytes is { } bytes ? $" · {bytes / (1024d * 1024):0.#} MB" : "") +
                (candidate.Character is { } character && character != candidate.Name ? $" · character: {character}" : ""),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0)
        };
        AutomationProperties.SetAutomationId(about, $"CharacterFinderAbout-{index}");
        text.Children.Add(about);
        var conditions = new TextBlock
        {
            Text = "Conditions of use: " + string.Join(" · ", candidate.Conditions), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0)
        };
        conditions.SetResourceReference(StyleProperty, "Muted");
        AutomationProperties.SetAutomationId(conditions, $"CharacterFinderConditions-{index}");
        text.Children.Add(conditions);
        var how = candidate.Kept
            ? "Martlet downloads it once and keeps it with your characters, shared with your Martlet computers."
            : "Shown from VRoid Hub each time with your linked account. Martlet never saves the model file." +
              (candidate.CreditRequired ? " Martlet shows the creator's name with it." : "");
        var why = !candidate.Usable ? "Its creator doesn't let others use it as a character."
            : candidate.Source == CharacterModelCandidate.VRoidHubSource && !linked
                ? VRoidHubService.CanLink ? "Link VRoid Hub above to use it." : "This Martlet build can't show VRoid Hub models."
            : null;
        var state = new TextBlock { Text = why ?? how, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        if (why is not null) state.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        AutomationProperties.SetAutomationId(state, $"CharacterFinderState-{index}");
        text.Children.Add(state);
        var use = new Button { Content = "_Use this look", MinWidth = 110, Margin = new Thickness(0, 6, 8, 0), IsEnabled = why is null };
        AutomationProperties.SetAutomationId(use, $"CharacterFinderUse-{index}");
        AutomationProperties.SetName(use, $"Use {candidate.Name}");
        use.Click += (_, _) => UseAsync(candidate).Forget();
        var page = new Button { Content = "Open its page", Margin = new Thickness(0, 6, 0, 0) };
        AutomationProperties.SetAutomationId(page, $"CharacterFinderPage-{index}");
        page.Click += (_, _) => Open(candidate.PageUrl);
        var buttons = new WrapPanel();
        buttons.Children.Add(use);
        buttons.Children.Add(page);
        text.Children.Add(buttons);
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(picture, Dock.Left);
        row.Children.Add(picture);
        row.Children.Add(text);
        return row;
    }

    private void NoLook_Click(object sender, RoutedEventArgs e) => UseAsync(null).Forget();

    // ---------- the backup finder: pages on the web ----------

    private CancellationTokenSource? webSearch;

    private void WebModels_Click(object sender, RoutedEventArgs e) => FindOnWebAsync(QueryText.Text.Trim(), models: true, voices: false).Forget();

    private void WebVoices_Click(object sender, RoutedEventArgs e) => FindOnWebAsync(QueryText.Text.Trim(), models: false, voices: true).Forget();

    private void AddModel_Click(object sender, RoutedEventArgs e) => host.AddModelFile();

    private void AddVoice_Click(object sender, RoutedEventArgs e) => host.OpenVoices();

    /// <summary>Asks the web for pages that may have the character's model (<paramref name="models"/>) or voice recordings
    /// (<paramref name="voices"/>) and lists them, with searches on the sites that have them. Nothing is downloaded.</summary>
    private async Task FindOnWebAsync(string name, bool models, bool voices)
    {
        if (name.Length == 0)
        {
            StatusText.Text = "Type the character's name first.";
            return;
        }
        if (!models && !voices) return;
        webSearch?.Cancel();
        webSearch = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = webSearch.Token;
        if (models) WebModelsText.Text = "Searching the web for models...";
        if (voices) WebVoicesText.Text = "Searching the web for voice recordings...";
        WebModelsButton.IsEnabled = WebVoicesButton.IsEnabled = false;
        try
        {
            var found = await host.FindOnWebAsync(name, models, voices, token);
            if (token.IsCancellationRequested) return;
            var notes = found.Notes.Count > 0 ? " " + string.Join(" ", found.Notes) : "";
            if (models)
            {
                WebModelsText.Text = (found.Models.Count == 0 ? "No pages with a model were found; try the site searches below." :
                    $"{found.Models.Count} page{(found.Models.Count == 1 ? "" : "s")} may have a model.") + notes;
                ShowWebLinks(WebModelRows, ModelSearchLinks, found.Models, found.Searches.Where(s => s.Kind == Martlet.Conversation.CharacterWebFinder.ModelKind), "model");
            }
            if (voices)
            {
                WebVoicesText.Text = (found.Voices.Count == 0 ? "No pages with recordings were found; try the site searches below." :
                    $"{found.Voices.Count} page{(found.Voices.Count == 1 ? "" : "s")} may have recordings of the voice.") + notes;
                ShowWebLinks(WebVoiceRows, VoiceSearchLinks, found.Voices, found.Searches.Where(s => s.Kind == Martlet.Conversation.CharacterWebFinder.VoiceKind), "voice");
            }
        }
        catch (OperationCanceledException) { }
        catch (ArgumentException error)
        {
            StatusText.Text = error.Message;
        }
        finally { WebModelsButton.IsEnabled = WebVoicesButton.IsEnabled = true; }
    }

    private void ShowWebLinks(Panel rows, Panel searches, IReadOnlyList<Martlet.Conversation.CharacterWebLink> links,
        IEnumerable<Martlet.Conversation.CharacterWebLink> sitesSearches, string kind)
    {
        rows.Children.Clear();
        for (var index = 0; index < links.Count; index++)
        {
            var link = links[index];
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = link.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            var about = new TextBlock { Text = link.Site + (string.IsNullOrEmpty(link.Snippet) ? "" : " · " + link.Snippet), TextWrapping = TextWrapping.Wrap };
            about.SetResourceReference(StyleProperty, "Muted");
            AutomationProperties.SetAutomationId(about, $"CharacterFinderWebAbout-{kind}-{index}");
            text.Children.Add(about);
            var open = new Button { Content = "Open", MinWidth = 72, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            AutomationProperties.SetAutomationId(open, $"CharacterFinderWebOpen-{kind}-{index}");
            AutomationProperties.SetName(open, $"Open {link.Title}");
            open.Click += (_, _) => Open(link.Url);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(open, Dock.Right);
            row.Children.Add(open);
            row.Children.Add(text);
            rows.Children.Add(row);
        }
        searches.Children.Clear();
        var number = 0;
        foreach (var search in sitesSearches)
        {
            var button = new Button { Content = search.Title, Margin = new Thickness(0, 0, 8, 6) };
            AutomationProperties.SetAutomationId(button, $"CharacterFinderWebSearch-{kind}-{number++}");
            button.Click += (_, _) => Open(search.Url);
            searches.Children.Add(button);
        }
    }

    private async Task UseAsync(CharacterModelCandidate? look)
    {
        if (busy) return;
        var name = QueryText.Text.Trim();
        if (name.Length == 0)
        {
            StatusText.Text = "Type the character's name first.";
            return;
        }
        if (name.Length > Martlet.Core.Settings.PersonaProfile.MaximumNameCharacters)
            name = name[..Martlet.Core.Settings.PersonaProfile.MaximumNameCharacters].TrimEnd();
        busy = true;
        IsEnabledChildren(false);
        try
        {
            StatusText.Text = await host.CreateCharacterAsync(name, PersonalityText.Text.Trim(), look, progress => StatusText.Text = progress, lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is Martlet.Core.Contracts.ContractException or HttpRequestException or VRoidHubException or
            InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "The character wasn't made: " + error.Message;
        }
        finally
        {
            busy = false;
            IsEnabledChildren(true);
        }
    }

    private void IsEnabledChildren(bool enabled)
    {
        SearchButton.IsEnabled = WriteButton.IsEnabled = NoLookButton.IsEnabled = enabled;
        foreach (var child in ResultRows.Children.OfType<DockPanel>()) child.IsEnabled = enabled;
    }

    private void Open(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose(); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            StatusText.Text = "The page couldn't open: " + error.Message;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
