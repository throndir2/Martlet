using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Martlet.Mcp.Client;

namespace Martlet.Desktop;

/// <summary>Browses an MCP directory (the GitHub MCP Registry or the official MCP Registry) and installs a server into mcp.json
/// with what it needs filled in, so nobody has to edit JSON by hand. Nothing is fetched until the window opens, and nothing is
/// installed until Install is pressed.</summary>
public partial class McpDirectoryWindow : ThemedWindow
{
    private readonly McpToolService service;
    private readonly HttpClient http = CreateHttpClient();
    private readonly McpDirectoryClient client;
    private readonly List<McpDirectoryEntry> entries = [];
    private readonly Dictionary<string, Func<string?>> readers = new(StringComparer.Ordinal);
    private CancellationTokenSource? searching;
    private McpDirectorySource source = McpDirectorySource.GitHub;
    private string query = "";
    private string? nextCursor;
    private McpDirectoryEntry? selected;
    private McpInstallOption? option;
    private StackPanel? optionPanel;
    private TextBox? nameBox;
    private TextBlock? preview;

    internal McpDirectoryWindow(McpToolService service)
    {
        this.service = service;
        client = new(http);
        InitializeComponent();
        foreach (var directory in McpDirectorySource.All)
        {
            var item = new ComboBoxItem { Content = directory.Name, Tag = directory, ToolTip = directory.About };
            AutomationProperties.SetName(item, directory.Name);
            SourceChoice.Items.Add(item);
        }
        SourceChoice.SelectedIndex = 0;
        ShowNothingSelected();
    }

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true, MaxAutomaticRedirections = 3, UseCookies = false, Credentials = null, UseProxy = true,
            AutomaticDecompression = DecompressionMethods.All, ConnectTimeout = TimeSpan.FromSeconds(15)
        }) { Timeout = TimeSpan.FromSeconds(90) };
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
        http.Dispose();
    }

    private void SourceChoice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SourceChoice.SelectedItem is not ComboBoxItem { Tag: McpDirectorySource chosen }) return;
        source = chosen;
        if (IsLoaded) SearchAsync(append: false).Forget();
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        SearchAsync(append: false).Forget();
    }

    private void Search_Click(object sender, RoutedEventArgs e) => SearchAsync(append: false).Forget();

    private void More_Click(object sender, RoutedEventArgs e) => SearchAsync(append: true).Forget();

    private async Task SearchAsync(bool append)
    {
        if (append && nextCursor is null) return;
        searching?.Cancel();
        var run = searching = new CancellationTokenSource();
        if (!append) query = SearchBox.Text.Trim();
        var directory = source;
        MoreButton.IsEnabled = false;
        StatusText.Text = append ? $"Loading more from the {directory.Name}..."
            : query.Length == 0 ? $"Loading the {directory.Name}..." : $"Searching the {directory.Name} for \"{query}\"..." +
                (directory == McpDirectorySource.Official ? " Its search can take up to a minute." : "");
        try
        {
            var page = await client.SearchAsync(directory, query, append ? nextCursor : null, run.Token);
            if (run.IsCancellationRequested) return;
            if (!append)
            {
                entries.Clear();
                Results.Items.Clear();
                ShowNothingSelected();
            }
            foreach (var entry in page.Servers.Where(entry => !entries.Any(e => string.Equals(e.Name, entry.Name, StringComparison.OrdinalIgnoreCase))))
            {
                entries.Add(entry);
                Results.Items.Add(ResultItem(entry));
            }
            nextCursor = page.NextCursor;
            MoreButton.Visibility = nextCursor is null ? Visibility.Collapsed : Visibility.Visible;
            StatusText.Text = entries.Count == 0
                ? query.Length == 0 ? $"The {directory.Name} didn't list any servers."
                    : $"No servers in the {directory.Name} match \"{query}\"." + (directory == McpDirectorySource.Official
                        ? " It searches server names only; try a shorter word or the GitHub MCP Registry." : " Try a shorter word.")
                : $"{entries.Count} server{(entries.Count == 1 ? "" : "s")}{(query.Length == 0 ? "" : $" matching \"{query}\"")} from the " +
                  $"{directory.Name}{(nextCursor is null ? "" : " (Load more shows the next ones)")}. Choose one to see what it needs.";
        }
        catch (McpDirectoryException error) when (!run.IsCancellationRequested)
        {
            StatusText.Text = error.Message;
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(searching, run)) MoreButton.IsEnabled = true;
        }
    }

    private McpServerDefinition? Installed(McpDirectoryEntry entry) =>
        service.Configuration.Servers.FirstOrDefault(s => string.Equals(s.Registry, entry.Name, StringComparison.OrdinalIgnoreCase));

    private ListBoxItem ResultItem(McpDirectoryEntry entry)
    {
        var item = new ListBoxItem { Tag = entry, Padding = new Thickness(8, 6, 8, 6) };
        AutomationProperties.SetAutomationId(item, "McpDirectoryResult-" + entry.Name);
        AutomationProperties.SetName(item, entry.DisplayName);
        FillResultItem(item, entry);
        return item;
    }

    private void FillResultItem(ListBoxItem item, McpDirectoryEntry entry)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = entry.DisplayName, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (entry.Description.Length > 0)
            panel.Children.Add(Muted(entry.Description, new Thickness(0, 2, 0, 0), maxHeight: 38));
        var facts = new List<string>();
        facts.Add(entry.Options.Count == 0 ? "can't be installed here" : string.Join(" · ", entry.Options.Select(o => KindName(o.Kind)).Distinct()));
        if (entry.Stars is { } stars) facts.Add($"★ {Stars(stars)}");
        if (Installed(entry) is { } installed) facts.Add($"installed as {installed.Name}");
        panel.Children.Add(Muted(string.Join("   ", facts), new Thickness(0, 2, 0, 0)));
        item.Content = panel;
    }

    private static string KindName(McpInstallKind kind) => kind switch
    {
        McpInstallKind.Npm => "npm", McpInstallKind.PyPI => "Python", McpInstallKind.Docker => "Docker",
        McpInstallKind.NuGet => ".NET", _ => "hosted"
    };

    private static string Stars(long stars) => stars >= 1000
        ? (stars / 1000.0).ToString(stars >= 100_000 ? "0" : "0.#", System.Globalization.CultureInfo.CurrentCulture) + "k"
        : stars.ToString(System.Globalization.CultureInfo.CurrentCulture);

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

    private static Button LinkButton(string label, string id, Action run)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 0, 16, 0) };
        button.SetResourceReference(StyleProperty, "LinkButton");
        AutomationProperties.SetAutomationId(button, id);
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => run();
        return button;
    }

    private void Results_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Results.SelectedItem is ListBoxItem { Tag: McpDirectoryEntry entry }) ShowEntry(entry);
    }

    private void ShowNothingSelected()
    {
        selected = null;
        option = null;
        readers.Clear();
        Details.Children.Clear();
        Details.Children.Add(Muted("Choose a server on the left to see what it does, what it needs and how Martlet would run it.",
            new Thickness(0, 4, 0, 0), id: "McpDirectoryNoSelection"));
    }

    private void ShowEntry(McpDirectoryEntry entry)
    {
        selected = entry;
        option = null;
        readers.Clear();
        Details.Children.Clear();
        var title = new TextBlock { Text = entry.DisplayName, FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(title, "McpDirectoryDetailTitle");
        Details.Children.Add(title);
        var facts = new List<string> { entry.Name };
        if (entry.Version.Length > 0) facts.Add("version " + entry.Version);
        if (entry.Stars is { } stars) facts.Add($"★ {Stars(stars)} on GitHub");
        Details.Children.Add(Muted(string.Join(" · ", facts), new Thickness(0, 2, 0, 0), id: "McpDirectoryDetailName"));
        if (entry.Description.Length > 0) Details.Children.Add(Text(entry.Description, new Thickness(0, 8, 0, 0), "McpDirectoryDetailDescription"));
        var links = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        if (entry.Repository is { } repository) links.Children.Add(LinkButton("Source code", "McpDirectoryRepository", () => Open(repository)));
        if (entry.Website is { } website && website != entry.Repository) links.Children.Add(LinkButton("Website", "McpDirectoryWebsite", () => Open(website)));
        if (links.Children.Count > 0) Details.Children.Add(links);
        if (Installed(entry) is { } installed)
            Details.Children.Add(Text($"Installed as \"{installed.Name}\"{(installed.RegistryVersion is { } v ? $" (version {v})" : "")}. " +
                "Installing it again under that name replaces it.", new Thickness(0, 8, 0, 0), "McpDirectoryInstalled"));
        if (entry.Options.Count == 0)
        {
            Details.Children.Add(Text("Martlet can't install this server by itself. " + string.Join(" ", entry.Unsupported) +
                " Its page may describe other ways to run it, which you can add in mcp.json.", new Thickness(0, 12, 0, 0), "McpDirectoryCantInstall"));
            return;
        }
        var preferred = entry.Options.FirstOrDefault(o => o.RuntimeAvailable()) ?? entry.Options[0];
        if (entry.Options.Count > 1)
        {
            var choice = new ComboBox { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetAutomationId(choice, "McpDirectoryOption");
            AutomationProperties.SetName(choice, "How to run it");
            foreach (var candidate in entry.Options)
            {
                var item = new ComboBoxItem { Content = candidate.ShortName, Tag = candidate };
                AutomationProperties.SetName(item, candidate.ShortName);
                choice.Items.Add(item);
            }
            choice.SelectedIndex = entry.Options.ToList().IndexOf(preferred);
            choice.SelectionChanged += (_, _) =>
            {
                if (choice.SelectedItem is ComboBoxItem { Tag: McpInstallOption picked }) ShowOption(entry, picked);
            };
            Details.Children.Add(new Label { Content = "_How to run it", Target = choice, Padding = new Thickness(0, 14, 0, 4) });
            Details.Children.Add(choice);
        }
        optionPanel = new StackPanel();
        Details.Children.Add(optionPanel);
        if (entry.Unsupported.Count > 0)
            Details.Children.Add(Muted("Not offered: " + string.Join(" ", entry.Unsupported), new Thickness(0, 12, 0, 0)));
        ShowOption(entry, preferred);
    }

    private void ShowOption(McpDirectoryEntry entry, McpInstallOption chosen)
    {
        if (optionPanel is null) return;
        option = chosen;
        readers.Clear();
        optionPanel.Children.Clear();
        optionPanel.Children.Add(Muted(chosen.Summary, new Thickness(0, 10, 0, 0), id: "McpDirectorySummary"));
        TextBlock needs;
        if (chosen.Kind == McpInstallKind.Remote)
            needs = Text($"Martlet connects to {chosen.Host}, so what your tool calls send goes to its publisher. If it wants you to sign " +
                "in through a browser (OAuth) instead of a key, Martlet can't do that yet.", new Thickness(0, 6, 0, 0), "McpDirectoryNeeds");
        else if (chosen.RuntimeAvailable())
            needs = Text($"It runs with {chosen.Runtime} from {chosen.RuntimeHelp}, which is on this PC. Its first start downloads the " +
                "server, which can take a minute. It runs as a program on this PC with your permissions.", new Thickness(0, 6, 0, 0), "McpDirectoryNeeds");
        else
        {
            needs = Text($"It runs with {chosen.Runtime} from {chosen.RuntimeHelp}, which isn't on this PC. Install it and restart Martlet " +
                "first; until then this server can't start.", new Thickness(0, 6, 0, 0), "McpDirectoryNeeds");
            needs.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        }
        optionPanel.Children.Add(needs);

        var installed = Installed(entry);
        nameBox = new TextBox
        {
            MaxLength = McpServerDefinition.MaxNameLength, Width = 320, HorizontalAlignment = HorizontalAlignment.Left,
            Text = installed?.Name ?? McpConfiguration.UniqueName(ReadConfig(), entry.SuggestedName)
        };
        AutomationProperties.SetAutomationId(nameBox, "McpDirectoryName");
        optionPanel.Children.Add(new Label { Content = "_Name in mcp.json", Target = nameBox, Padding = new Thickness(0, 12, 0, 4) });
        optionPanel.Children.Add(nameBox);
        nameBox.TextChanged += (_, _) => UpdatePreview();

        var required = chosen.Inputs.Where(i => i.Required).ToArray();
        var optional = chosen.Inputs.Where(i => !i.Required).ToArray();
        foreach (var input in required) AddField(optionPanel, input);
        if (optional.Length > 0)
        {
            var more = new StackPanel();
            foreach (var input in optional) AddField(more, input);
            var expander = new Expander
            {
                Header = $"Optional settings ({optional.Length}); leave empty to use the server's defaults", Content = more,
                Margin = new Thickness(0, 12, 0, 0), IsExpanded = required.Length == 0 && optional.Length <= 4
            };
            AutomationProperties.SetAutomationId(expander, "McpDirectoryOptional");
            optionPanel.Children.Add(expander);
        }
        if (chosen.Inputs.Any(i => i.Secret))
            optionPanel.Children.Add(Muted("Secret values are kept in Windows Credential Manager; mcp.json only refers to them as " +
                "${secret:...}. In any field you can type ${env:NAME} to use an environment variable instead.", new Thickness(0, 10, 0, 0)));

        preview = Muted("", new Thickness(0, 12, 0, 0), id: "McpDirectoryRuns");
        preview.TextTrimming = TextTrimming.None;
        optionPanel.Children.Add(preview);
        var install = new Button { Content = installed is null ? "_Install and start" : "_Reinstall", Margin = new Thickness(0, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 140 };
        install.SetResourceReference(StyleProperty, "PrimaryButton");
        AutomationProperties.SetAutomationId(install, "McpDirectoryInstall");
        install.IsEnabled = service.FilePath is not null;
        install.Click += (_, _) => Install(entry);
        optionPanel.Children.Add(install);
        UpdatePreview();
    }

    private void AddField(Panel panel, McpInstallInput input)
    {
        var id = "McpDirectoryInput-" + input.Key;
        var label = input.Label + (input.Required ? " (required)" : "");
        Control field;
        if (input.Flag)
        {
            var check = new CheckBox { Content = label, IsChecked = input.Initial == "true", Margin = new Thickness(0, 12, 0, 0) };
            check.Click += (_, _) => UpdatePreview();
            readers[input.Key] = () => check.IsChecked == true ? "true" : "false";
            field = check;
        }
        else if (input.Choices.Count > 0)
        {
            var combo = new ComboBox { MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Left };
            if (!input.Required) combo.Items.Add(new ComboBoxItem { Content = "(not set)", Tag = "" });
            foreach (var value in input.Choices) combo.Items.Add(new ComboBoxItem { Content = value, Tag = value });
            combo.SelectedIndex = input.Initial is { } initial && input.Choices.ToList().IndexOf(initial) is >= 0 and var at
                ? at + (input.Required ? 0 : 1) : 0;
            combo.SelectionChanged += (_, _) => UpdatePreview();
            readers[input.Key] = () => (combo.SelectedItem as ComboBoxItem)?.Tag as string;
            field = combo;
        }
        else if (input.Secret)
        {
            var secret = new PasswordBox { MaxLength = McpInstallOption.MaxValueLength, Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
            secret.PasswordChanged += (_, _) => UpdatePreview();
            readers[input.Key] = () => secret.Password;
            field = secret;
        }
        else
        {
            var text = new TextBox { MaxLength = McpInstallOption.MaxValueLength, Width = 420, HorizontalAlignment = HorizontalAlignment.Left,
                Text = input.Initial ?? "" };
            text.TextChanged += (_, _) => UpdatePreview();
            readers[input.Key] = () => text.Text;
            field = text;
        }
        AutomationProperties.SetAutomationId(field, id);
        AutomationProperties.SetName(field, input.Label);
        if (!input.Flag) panel.Children.Add(new Label { Content = label, Target = field, Padding = new Thickness(0, 12, 0, 4) });
        panel.Children.Add(field);
        var hints = new List<string>();
        if (input.Description is { } description) hints.Add(description);
        if (!input.Required && input.Default is { Length: > 0 } fallback && !input.Flag) hints.Add($"Default: {fallback}.");
        if (input.Placeholder is { Length: > 0 } example) hints.Add($"For example: {example}.");
        if (input.IsPath) hints.Add("A full path on this PC.");
        if (hints.Count > 0) panel.Children.Add(Muted(string.Join(" ", hints), new Thickness(input.Flag ? 22 : 0, 2, 0, 0), maxHeight: 120));
    }

    private Dictionary<string, string?> Values() => readers.ToDictionary(pair => pair.Key, pair => pair.Value(), StringComparer.Ordinal);

    private void UpdatePreview()
    {
        if (option is null || preview is null || nameBox is null) return;
        var values = Values();
        foreach (var input in option.Inputs.Where(i => i.Required && !i.Flag && string.IsNullOrWhiteSpace(values.GetValueOrDefault(i.Key))))
            values[input.Key] = input.Choices.Count > 0 ? input.Choices[0] : $"<{input.Label}>";
        try
        {
            var plan = option.Build(nameBox.Text.Trim().Length == 0 ? "server" : nameBox.Text.Trim(), values);
            preview.Text = (option.Kind == McpInstallKind.Remote ? "Connects to: " : "Runs: ") + plan.Preview;
        }
        catch (McpConfigurationException error)
        {
            preview.Text = error.Message;
        }
    }

    private string ReadConfig()
    {
        try { return service.ReadText(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or McpConfigurationException) { return ""; }
    }

    private void Install(McpDirectoryEntry entry)
    {
        if (option is null || nameBox is null || !ReferenceEquals(selected, entry)) return;
        var name = nameBox.Text.Trim();
        var existing = service.Configuration.Servers.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        var replace = existing is not null;
        if (existing is not null && !string.Equals(existing.Registry, entry.Name, StringComparison.OrdinalIgnoreCase) &&
            !ConfirmationDialog.Confirm(this, $"mcp.json already has a server named \"{existing.Name}\". Replace it with {entry.DisplayName}?",
                "Martlet - MCP directory"))
            return;
        try
        {
            var plan = option.Build(name, Values());
            service.Install(name, plan, replace);
            StatusText.Text = $"Installed \"{name}\" and started it. Companion > Tools shows whether it's running and its tools" +
                (option.Kind == McpInstallKind.Remote ? "." : "; the first start downloads it, which can take a minute.");
            foreach (var item in Results.Items.OfType<ListBoxItem>().Where(i => ReferenceEquals(i.Tag, entry))) FillResultItem(item, entry);
            ShowEntry(entry);
        }
        catch (Exception error) when (error is McpConfigurationException or IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Not installed: {error.Message}";
        }
    }

    private void Open(Uri address)
    {
        try { Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true })?.Dispose(); }
        catch (Win32Exception error) { StatusText.Text = $"Couldn't open {address.Host}: {error.Message}"; }
    }

    private void EditConfig_Click(object sender, RoutedEventArgs e)
    {
        new McpConfigWindow(service) { Owner = this }.ShowDialog();
        foreach (var item in Results.Items.OfType<ListBoxItem>()) if (item.Tag is McpDirectoryEntry entry) FillResultItem(item, entry);
        if (selected is { } current) ShowEntry(current);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
