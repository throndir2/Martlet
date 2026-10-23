using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Companion › Skills › Find skills: browses the public skill directory (ClawHub, the skill registry of OpenClaw, and the
/// skills.sh skills its search finds) with a search, an order (most popular first) and filters, shows a skill's SKILL.md and adds
/// it as one of the user's own skills. Nothing is fetched until the window opens, and nothing is added until Add is pressed.</summary>
public partial class SkillDirectoryWindow : ThemedWindow
{
    private readonly Func<Skill, Task<string?>> add;
    private readonly Func<IReadOnlyList<Skill>> current;
    private readonly HttpClient http = CreateHttpClient();
    private readonly SkillDirectoryClient client;
    private readonly List<SkillDirectoryEntry> entries = [];
    private readonly HashSet<string> added = new(StringComparer.Ordinal);
    private CancellationTokenSource? searching, reading;
    private SkillDirectoryQuery query = new();
    private string? nextCursor;
    private SkillDirectoryEntry? selected;
    private string? markdown;
    private bool ready;

    /// <param name="add">Saves a skill as one of the user's own; returns why it couldn't, or null when it was saved.</param>
    /// <param name="current">Every skill the user has now (built in and their own).</param>
    internal SkillDirectoryWindow(Func<Skill, Task<string?>> add, Func<IReadOnlyList<Skill>> current)
    {
        this.add = add;
        this.current = current;
        client = new(http);
        InitializeComponent();
        foreach (var sort in Enum.GetValues<SkillDirectorySort>())
            AddChoice(SortChoice, Capital(SkillDirectoryClient.SortName(sort)), sort);
        AddChoice(SourceChoice, "ClawHub and skills.sh", SkillDirectorySource.All);
        AddChoice(SourceChoice, "ClawHub", SkillDirectorySource.ClawHub);
        AddChoice(SourceChoice, "skills.sh", SkillDirectorySource.SkillsSh);
        foreach (var minimum in new long[] { 0, 100, 1_000, 10_000 })
            AddChoice(DownloadsChoice, minimum == 0 ? "Any number" : $"{minimum.ToString("N0", CultureInfo.CurrentCulture)} or more", minimum);
        SortChoice.SelectedIndex = SourceChoice.SelectedIndex = DownloadsChoice.SelectedIndex = 0;
        ShowNothingSelected();
        ready = true;
    }

    private static void AddChoice(ComboBox box, string label, object tag)
    {
        var item = new ComboBoxItem { Content = label, Tag = tag };
        AutomationProperties.SetName(item, label);
        box.Items.Add(item);
    }

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true, MaxAutomaticRedirections = 3, UseCookies = false, Credentials = null, UseProxy = true,
            AutomaticDecompression = DecompressionMethods.All, ConnectTimeout = TimeSpan.FromSeconds(15)
        }) { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.TryParseAdd($"Martlet/{AppVersions.Current}");
        return http;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        SearchBox.Focus();
        SearchAsync(append: false).Forget();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        searching?.Cancel();
        reading?.Cancel();
        http.Dispose();
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        SearchAsync(append: false).Forget();
    }

    private void Search_Click(object sender, RoutedEventArgs e) => SearchAsync(append: false).Forget();

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (ready && IsLoaded) SearchAsync(append: false).Forget();
    }

    private void More_Click(object sender, RoutedEventArgs e) => SearchAsync(append: true).Forget();

    private SkillDirectoryQuery Chosen() => new(SearchBox.Text.Trim(),
        (SortChoice.SelectedItem as ComboBoxItem)?.Tag is SkillDirectorySort sort ? sort : SkillDirectorySort.Popular,
        HideFlagged.IsChecked == true,
        (DownloadsChoice.SelectedItem as ComboBoxItem)?.Tag is long minimum ? minimum : 0,
        (SourceChoice.SelectedItem as ComboBoxItem)?.Tag is SkillDirectorySource source ? source : SkillDirectorySource.All);

    private async Task SearchAsync(bool append)
    {
        if (append && nextCursor is null) return;
        searching?.Cancel();
        var run = searching = new CancellationTokenSource();
        if (!append) query = Chosen();
        MoreButton.IsEnabled = false;
        StatusText.Text = append ? "Loading more skills..." : query.Searching ? $"Searching for \"{query.Search}\"..." : "Loading the skills...";
        try
        {
            if (!query.Searching && query.Source == SkillDirectorySource.SkillsSh)
            {
                ClearResults();
                nextCursor = null;
                MoreButton.Visibility = Visibility.Collapsed;
                StatusText.Text = "skills.sh skills can only be searched: type a word and press Search.";
                return;
            }
            var page = await client.SearchAsync(query, append ? nextCursor : null, run.Token);
            if (run.IsCancellationRequested) return;
            if (!append) ClearResults();
            foreach (var entry in page.Skills.Where(entry => entries.All(e => e.Id != entry.Id)))
            {
                entries.Add(entry);
                Results.Items.Add(ResultItem(entry));
            }
            nextCursor = page.NextCursor;
            MoreButton.Visibility = nextCursor is null ? Visibility.Collapsed : Visibility.Visible;
            var order = query.Searching ? SkillDirectoryClient.SortName(query.Sort)
                : SkillDirectoryClient.SortName(query.Sort == SkillDirectorySort.BestMatch ? SkillDirectorySort.Popular : query.Sort);
            StatusText.Text = entries.Count == 0
                ? query.Searching ? $"No skills match \"{query.Search}\" with these filters. Try another word or fewer filters."
                    : "No skills match these filters" + (nextCursor is null ? "." : "; Load more looks further.")
                : $"{entries.Count} skill{(entries.Count == 1 ? "" : "s")}{(query.Searching ? $" matching \"{query.Search}\"" : " on ClawHub")}, " +
                  $"{order} first{(nextCursor is null ? "" : ", and Load more shows the next ones")}: choose one to read it.";
        }
        catch (SkillDirectoryException error) when (!run.IsCancellationRequested)
        {
            StatusText.Text = error.Message;
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(searching, run)) MoreButton.IsEnabled = true;
        }
    }

    private void ClearResults()
    {
        entries.Clear();
        Results.Items.Clear();
        ShowNothingSelected();
    }

    private string? Have(SkillDirectoryEntry entry) => added.Contains(entry.Id) ? "added"
        : current().FirstOrDefault(s => string.Equals(s.Name, entry.Name, StringComparison.OrdinalIgnoreCase)) is { } same
            ? $"you have a skill named {same.Name}" : null;

    private ListBoxItem ResultItem(SkillDirectoryEntry entry)
    {
        var item = new ListBoxItem { Tag = entry, Padding = new Thickness(8, 6, 8, 6) };
        AutomationProperties.SetAutomationId(item, "SkillDirectoryResult-" + entry.Id);
        AutomationProperties.SetName(item, entry.Name);
        FillResultItem(item, entry);
        return item;
    }

    private void FillResultItem(ListBoxItem item, SkillDirectoryEntry entry)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = entry.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (entry.Summary.Length > 0) panel.Children.Add(Muted(entry.Summary, new Thickness(0, 2, 0, 0), maxHeight: 38));
        var facts = new List<string> { $"{entry.Owner} · {entry.SourceName}", $"↓ {Count(entry.Downloads)}" };
        if (entry.Stars is > 0 and var stars) facts.Add($"★ {Count(stars)}");
        if (entry.Official) facts.Add("official");
        if (entry.Flagged == true) facts.Add("flagged");
        if (Have(entry) is { } have) facts.Add(have);
        panel.Children.Add(Muted(string.Join("   ", facts), new Thickness(0, 2, 0, 0)));
        item.Content = panel;
    }

    private static string Count(long count) => count >= 1000
        ? (count / 1000.0).ToString(count >= 100_000 ? "0" : "0.#", CultureInfo.CurrentCulture) + "k"
        : count.ToString(CultureInfo.CurrentCulture);

    private static TextBlock Muted(string text, Thickness margin, double maxHeight = double.PositiveInfinity, string? id = null)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = margin, MaxHeight = maxHeight, TextTrimming = TextTrimming.WordEllipsis };
        block.SetResourceReference(StyleProperty, "Muted");
        if (id is not null) AutomationProperties.SetAutomationId(block, id);
        return block;
    }

    private static TextBlock Text(string text, Thickness margin, string? id = null)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = margin };
        if (id is not null) AutomationProperties.SetAutomationId(block, id);
        return block;
    }

    private void Results_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Results.SelectedItem is ListBoxItem { Tag: SkillDirectoryEntry entry }) ShowEntry(entry);
    }

    private void ShowNothingSelected()
    {
        reading?.Cancel();
        selected = null;
        markdown = null;
        Details.Children.Clear();
        Details.Children.Add(Muted("Choose a skill on the left to read it and add it to your skills.", new Thickness(0, 4, 0, 0),
            id: "SkillDirectoryNoSelection"));
    }

    private void ShowEntry(SkillDirectoryEntry entry)
    {
        reading?.Cancel();
        selected = entry;
        markdown = null;
        Details.Children.Clear();
        var title = new TextBlock { Text = entry.Name, FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(title, "SkillDirectoryDetailTitle");
        Details.Children.Add(title);
        var facts = new List<string> { $"by {entry.Owner} on {entry.SourceName}", $"{entry.Downloads.ToString("N0", CultureInfo.CurrentCulture)} downloads" };
        if (entry.Installs is { } installs) facts.Add($"{installs.ToString("N0", CultureInfo.CurrentCulture)} installs");
        if (entry.Stars is { } stars) facts.Add($"★ {stars.ToString("N0", CultureInfo.CurrentCulture)}");
        if (entry.Updated is { } updated) facts.Add("updated " + updated.LocalDateTime.ToString("d", CultureInfo.CurrentCulture));
        if (entry.Official) facts.Add("official publisher");
        Details.Children.Add(Muted(string.Join(" · ", facts), new Thickness(0, 2, 0, 0), id: "SkillDirectoryDetailFacts"));
        if (entry.Topics.Count > 0) Details.Children.Add(Muted("Topics: " + string.Join(", ", entry.Topics), new Thickness(0, 2, 0, 0)));
        if (entry.Summary.Length > 0) Details.Children.Add(Text(entry.Summary, new Thickness(0, 8, 0, 0), "SkillDirectoryDetailSummary"));
        if (entry.Flagged == true)
        {
            var warning = Text("This skill is flagged as suspicious. Read it carefully before you add it.", new Thickness(0, 8, 0, 0), "SkillDirectoryFlagged");
            warning.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            Details.Children.Add(warning);
        }
        if (entry.Page is { } page)
        {
            var open = new Button { Content = $"Open it on {page.Host}", Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            open.SetResourceReference(StyleProperty, "LinkButton");
            AutomationProperties.SetAutomationId(open, "SkillDirectoryPage");
            open.Click += (_, _) => Open(page);
            Details.Children.Add(open);
        }
        if (Have(entry) is { } have) Details.Children.Add(Text(Capital(have) + ".", new Thickness(0, 8, 0, 0), "SkillDirectoryHave"));
        var addButton = new Button { Content = "_Add to my skills", Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 140, IsEnabled = false };
        addButton.SetResourceReference(StyleProperty, "PrimaryButton");
        AutomationProperties.SetAutomationId(addButton, "SkillDirectoryAdd");
        addButton.Click += (_, _) => AddAsync(entry, addButton).Forget();
        Details.Children.Add(addButton);
        Details.Children.Add(Muted("Martlet adds its text as one of your own skills, not pinned, so a reply gets it when Martlet's director " +
            "decides it helps. You can edit or delete it on Companion › Skills.", new Thickness(0, 6, 0, 0)));
        var state = Muted("Reading its SKILL.md...", new Thickness(0, 14, 0, 4), id: "SkillDirectoryPreviewState");
        Details.Children.Add(state);
        var preview = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MaxHeight = 420, MinHeight = 120,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new System.Windows.Media.FontFamily("Consolas") };
        AutomationProperties.SetAutomationId(preview, "SkillDirectoryPreview");
        AutomationProperties.SetName(preview, "The skill's SKILL.md");
        Details.Children.Add(preview);
        ReadAsync(entry, state, preview, addButton).Forget();
    }

    private async Task ReadAsync(SkillDirectoryEntry entry, TextBlock state, TextBox preview, Button addButton)
    {
        var run = reading = new CancellationTokenSource();
        try
        {
            var text = await client.ReadSkillAsync(entry, run.Token);
            if (run.IsCancellationRequested || !ReferenceEquals(selected, entry)) return;
            markdown = text;
            preview.Text = text;
            var skill = SkillDirectoryClient.ToSkill(entry, text, current().Select(s => s.Key), out var shortened);
            state.Text = $"Its SKILL.md: about {PromptsPageModel.PromptTokens(skill.Body):N0} tokens as a skill" +
                (shortened ? $"; it is long, so Martlet keeps only its first {PromptSettings.MaximumTextCharacters:N0} characters." : ".");
            addButton.IsEnabled = true;
        }
        catch (SkillDirectoryException error) when (!run.IsCancellationRequested)
        {
            state.Text = "Couldn't read its SKILL.md: " + error.Message;
        }
        catch (OperationCanceledException) { }
    }

    private async Task AddAsync(SkillDirectoryEntry entry, Button addButton)
    {
        if (markdown is not { } text || !ReferenceEquals(selected, entry)) return;
        addButton.IsEnabled = false;
        var skill = SkillDirectoryClient.ToSkill(entry, text, current().Select(s => s.Key), out var shortened);
        StatusText.Text = $"Adding {skill.Name}...";
        var problem = await add(skill);
        if (problem is not null)
        {
            StatusText.Text = $"{skill.Name} wasn't added: {problem}";
            addButton.IsEnabled = true;
            return;
        }
        added.Add(entry.Id);
        StatusText.Text = $"Added \"{skill.Name}\" to your own skills (key {skill.Key})" +
            (shortened ? $", with the first {PromptSettings.MaximumTextCharacters:N0} characters of its text." : ".");
        foreach (var item in Results.Items.OfType<ListBoxItem>().Where(i => ReferenceEquals(i.Tag, entry))) FillResultItem(item, entry);
        ShowEntry(entry);
    }

    private void Open(Uri address)
    {
        try { Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true })?.Dispose(); }
        catch (Win32Exception error) { StatusText.Text = $"Couldn't open {address.Host}: {error.Message}"; }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
