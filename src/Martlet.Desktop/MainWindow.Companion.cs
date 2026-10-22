using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;


/// <summary>A conversation model Ollama can download and run on this PC. <paramref name="MinimumVramGb"/> is the graphics card
/// it needs to run comfortably beside a game and Martlet's character (0 means any PC). <paramref name="Hears"/>: Ollama takes
/// your recording for it (Gemma 4 E2B, E4B and 12B); the others always get the transcript (the Parakeet cascade).</summary>
internal sealed record LocalChatModel(string Id, string Size, string Fits, double MinimumVramGb, bool Hears);

/// <summary>The Companion page: a side list of pages in groups (How it works: Thinking, Listening, Voice, Lip-sync; Optional
/// extras: Vision, Reading, Hearing, Thinking pool, Smart home, Singing, Pictures; Who it is: Profiles, Personality, Lorebook,
/// Memory; How it looks: Character, Speech bubbles, Emotes and motions, Eyes, Touch; What it does: Discord). Each job page asks
/// where the job runs (this PC by default, another of your computers, or a
/// cloud provider; voice loudness for lip-sync) and shows only that place's fields, including the API key for a cloud provider.
/// Everything saves through the same setup service, consent and credential rules as Setup.</summary>
public partial class MainWindow
{
    /// <summary>Where a job runs. Lip-sync has no cloud provider; its voice-loudness way is one of this PC's.</summary>
    internal enum JobPlace { ThisPc, Computer, Cloud }

    internal sealed record CloudProvider(string Name, string? BaseUrl, bool Chat, string? DefaultModel, bool NeedsKey)
    {
        public override string ToString() => Name;
    }

    internal const string LocalOllamaBaseUrl = "http://127.0.0.1:11434/v1";

    // Every suggestion also sees (screen watching), calls tools and answers with Thinking steps Off. The smallest is recommended:
    // it gives the fastest replies (latency is king in conversation) and hears recordings itself; the larger ones are smarter
    // but slower. Qwen3.5 4B replaces Qwen3-VL 8B, which kept thinking with Thinking steps Off (no words for seconds); it doesn't
    // hear, so its replies get the transcript. Each leaves about 5 GB of the card for the game, Martlet's character and
    // Windows: a model that overfills the card is paged out to system memory and stalls, and its Gemma 4 draft model can't load
    // (see OllamaDraftHead). Measured on an RTX 4070 in docs/VOICE_LATENCY.md (Small models in Ollama).
    internal static readonly IReadOnlyList<LocalChatModel> LocalChatModels =
    [
        new("gemma4:e2b", "4.6 GB", "any PC", 0, Hears: true),
        new("qwen3.5:4b", "3.4 GB", "a graphics card with 12 GB or more", 12, Hears: false),
        new("gemma4:e4b", "6.6 GB", "a graphics card with 12 GB or more", 12, Hears: true),
        new("gemma4:12b", "8.0 GB", "a graphics card with 16 GB or more", 16, Hears: true),
        new("gemma4:26b", "18.7 GB", "a graphics card with 24 GB or more", 24, Hears: false)
    ];

    internal static readonly CloudProvider OpenAiCloud = new("OpenAI", null, false, null, true);
    internal static readonly CloudProvider CustomCloud = new("Custom OpenAI-compatible server", "", true, null, false);
    /// <summary>The Thinking providers, each with the model it suggests now: the model catalog's pick when it is loaded
    /// (<see cref="PlanningCatalog.SuggestedModel"/>), else the preset's default.</summary>
    internal static IReadOnlyList<CloudProvider> ThinkingProviders =>
    [
        OpenAiCloud,
        .. ChatCompletionsEndpointCatalog.NamedEndpoints.Select(e => new CloudProvider(e.Name, e.BaseUrl, true, Martlet.Core.Planning.PlanningCatalog.SuggestedModel(e.Id) ?? e.DefaultModelId, true)),
        CustomCloud
    ];

    private CompanionTab companionTab = CompanionTab.Thinking;
    private CompanionTab? openTab;
    private bool tabEdited;
    /// <summary>The open page's auto-save (Prompts, Replies), saved at once when another page opens.</summary>
    private AutoSave? tabAutoSave;
    private IReadOnlyList<string>? ollamaModels;
    private bool ollamaAutoChecked;
    private LocalModelTestOutcome? localModelTest;
    private Action? showLocalTest;
    private bool testingLocalModel;
    private readonly Dictionary<CompanionTab, JobPlace> tabPlace = [];


    /// <summary>Recommended local model: the fastest, gemma4:e2b, on every PC. It starts a spoken reply soonest (about 150 ms to
    /// its first sentence on an RTX 4070, against 100-200 ms more for E4B and 12B; docs/VOICE_LATENCY.md) and hears recordings
    /// itself. A larger model the card fits is smarter but slower (<see cref="LargestLocalModel"/>).</summary>
    internal static LocalChatModel RecommendedLocalModel(double? vramGb) => LocalChatModels[0];

    /// <summary>The largest suggested model this PC's graphics card fits (a "12 GB" card reports a little less): the smartest
    /// local choice, offered beside the fastest.</summary>
    internal static LocalChatModel LargestLocalModel(double? vramGb) =>
        LocalChatModels.Where(m => m.MinimumVramGb <= (vramGb ?? 0) + 0.5).OrderBy(m => m.MinimumVramGb).LastOrDefault() ?? LocalChatModels[0];

    /// <summary>The largest suggested model at least 1 GB smaller than <paramref name="model"/>, or null when none is.</summary>
    internal static LocalChatModel? SmallerLocalModel(string model)
    {
        var size = ListeningAdvisor.OllamaModelGb(model) - 0.5;
        return LocalChatModels.Where(m => SizeGb(m) <= size - 1).OrderBy(SizeGb).LastOrDefault();
        static double SizeGb(LocalChatModel m) => ListeningAdvisor.OllamaModelGb(m.Id) - 0.5;
    }

    internal static bool IsLocalOllama(SetupRoute? route) =>
        route?.RouteType == SetupRouteType.ChatCompletions && route.Origin == LocalOllamaBaseUrl;

    internal static AppSettings UseModels(AppSettings settings) =>
        settings.Profile.Kind == ProfileKind.Api ? settings : settings with { Profile = settings.Profile with { Kind = ProfileKind.Api } };

    /// <summary>The paired host that is this PC (Martlet's host service in Docker Desktop here), if any.</summary>
    private PairedHost? ThisPcHost()
    {
        var hosts = NetworkMap.Hosts(Inputs());
        return hosts.FirstOrDefault(h => h.Method == HostSetupMethod.ThisPcDocker)
            ?? hosts.FirstOrDefault(h => machine.LanAddress is { } address && h.Address == address);
    }

    /// <summary>Where a job runs, in words: "Ollama on this PC", "F5 on gpu-pc", "OpenRouter".</summary>
    private string PlaceName(SetupRoute route)
    {
        if (IsLocalOllama(route)) return "Ollama on this PC";
        if (IsLocalServer(route)) return Capitalized($"{LocalServerName(route.Origin)} on this PC");
        if (route.RouteType == SetupRouteType.LocalParakeet) return "Speech recognition on this PC";
        if (SelfHostSetup.IsGateway(route.RouteType) && route.Gateway is { } gateway)
        {
            var engine = route.RouteType switch
            {
                SetupRouteType.GatewayOllama => "Ollama",
                SetupRouteType.GatewayF5 => SpeechEngines.ForRoute(route.GatewaySnapshot?.RouteId)?.Name ?? "F5",
                _ => "speech recognition"
            };
            return $"{engine} on {(ThisPcHost()?.HostId == gateway.HostId ? "this PC" : gateway.HostId)}";
        }
        return NetworkMap.ProviderName(route);
    }

    private string LipSyncOwnerName() => NetworkMap.LipSync(homeAvatar) switch
    {
        LipSyncHandler.Loudness => "voice loudness",
        LipSyncHandler.Host => homeAvatar!.RemoteHost!.HostId,
        _ when ownLipSyncAnswers == false => "voice loudness (Audio2Face isn't running on this PC)",
        _ => "this PC"
    };

    // ---------- page frame ----------

    /// <summary>Shows a Companion page and selects Companion in the navigation rail. Every shortcut to a choice (Home, the
    /// Devices map, fix cards, the tour and the advisor) lands here, so each choice has one home.</summary>
    private void OpenCompanion(CompanionTab tab)
    {
        if (CompanionPage is null) return;
        if (Role == DeviceRole.Host)
        {
            // A host PC doesn't talk, so it has no Companion page and chooses no jobs.
            ActionText.Text = HostHasNoCompanionText;
            return;
        }
        companionTab = tab;
        tabPlace.Remove(tab);
        freeKeyPreset = FreeKeyUse.None;
        freeKeyFocus = false;
        reviewAfterKey = false;
        if (NavCompanion.IsChecked == true) ShowCompanionTab(entering: false);
        else NavCompanion.IsChecked = true;
    }

    private void ShowCompanionTab(bool entering)
    {
        openTab = companionTab;
        CompanionScroll.ScrollToTop();
        tabOpenedAt = Stopwatch.GetTimestamp();
        openingTab = true;
        try { RenderTab(); }
        finally { openingTab = false; }
        var built = Stopwatch.GetElapsedTime(tabOpenedAt);
        Motion.Enter(entering ? CompanionPage : CompanionContent);
        if (openTab == CompanionTab.LipSync) CheckOwnLipSyncAsync().Forget();
        LogPageDrawn(companionTab, tabOpenedAt, built);
    }

    /// <summary>Logs how long an opened page took to show: building its controls, then laying them out (done when the
    /// dispatcher reaches loaded-priority work), and how many elements it holds. The desktop log's "Companion › Character drew
    /// in 12 ms (...)" lines.</summary>
    private void LogPageDrawn(CompanionTab tab, long started, TimeSpan built) =>
        Dispatcher.InvokeAsync(() =>
        {
            if (closing || openTab != tab) return;
            var drawn = Stopwatch.GetElapsedTime(started);
            ErrorLog.Info($"Companion › {TabTitle(tab)} drew in {drawn.TotalMilliseconds:0} ms ({built.TotalMilliseconds:0} ms to build, " +
                $"{(drawn - built).TotalMilliseconds:0} ms to lay out), {Elements(CompanionContent)} elements.");
        }, System.Windows.Threading.DispatcherPriority.Loaded);

    private static int Elements(DependencyObject parent)
    {
        var count = 1;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            count += Elements(System.Windows.Media.VisualTreeHelper.GetChild(parent, i));
        return count;
    }

    // The rows of the page's long lists (emotes and motions, touch zones): on a page just opened, or drawn again at its top,
    // they join a batch at a time once the page has drawn (RowBatches).
    private readonly RowBatches rowBatches = new();
    private bool openingTab;
    private long tabOpenedAt;

    /// <summary>Adds a row of a long list to <paramref name="panel"/>, at once or a moment after the page draws (RowBatches).</summary>
    private void AddRow(Panel panel, UIElement row) => rowBatches.Add(panel, row);

    private readonly Dictionary<CompanionTab, RadioButton> companionNav = [];
    private bool selectingNav;
    private string? companionNavShape;

    /// <summary>The side list: each group's title, then its pages, then the Show advanced settings link. With Show advanced
    /// settings off it lists only the essentials (<see cref="CompanionPages.Essentials"/>) and the advanced page a shortcut opened.
    /// It is built again only when what it lists changes. Choosing a page opens it.</summary>
    private void BuildCompanionNav(CompanionTab? open = null)
    {
        var tabs = SideListOrder(showAdvanced, open);
        var shape = showAdvanced + ":" + string.Join(",", tabs);
        if (shape == companionNavShape) return;
        companionNavShape = shape;
        companionNav.Clear();
        CompanionNav.Children.Clear();
        foreach (var group in tabs.GroupBy(GroupOf).OrderBy(g => g.Key))
        {
            var title = new TextBlock { Text = GroupTitle(group.Key).ToUpperInvariant(), Margin = new Thickness(12, companionNav.Count == 0 ? 0 : 18, 0, 4) };
            title.SetResourceReference(StyleProperty, "Eyebrow");
            CompanionNav.Children.Add(title);
            foreach (var tab in group)
            {
                var item = new RadioButton { Content = TabTitle(tab), Tag = TabGlyph(tab), GroupName = "CompanionNav" };
                item.SetResourceReference(StyleProperty, "NavItem");
                AutomationProperties.SetName(item, $"{TabTitle(tab)} ({GroupTitle(group.Key)})");
                AutomationProperties.SetAutomationId(item, "CompanionTab-" + tab);
                item.Checked += (_, _) =>
                {
                    if (!selectingNav) OpenCompanion(tab);
                };
                companionNav[tab] = item;
                CompanionNav.Children.Add(item);
            }
        }
        var hidden = Enum.GetValues<CompanionTab>().Length - tabs.Count;
        var toggle = new Button
        {
            Content = showAdvanced ? "Hide advanced settings" : $"Show advanced settings ({hidden} more pages)",
            Margin = new Thickness(12, 18, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = showAdvanced ? "Show only the pages most people need" : "Show every page, such as Prompts, Tools, Vision and Discord"
        };
        toggle.SetResourceReference(StyleProperty, "LinkButton");
        AutomationProperties.SetAutomationId(toggle, "CompanionShowAdvanced");
        AutomationProperties.SetName(toggle, showAdvanced ? "Hide advanced settings" : "Show advanced settings");
        AutomationProperties.SetHelpText(toggle, showAdvanced ? "Every page shows." : $"{hidden} advanced pages are hidden.");
        toggle.Click += (_, _) => SetShowAdvanced(!showAdvanced);
        CompanionNav.Children.Add(toggle);
    }

    private void RenderTab()
    {
        if (openTab is not { } section) return;
        if (tabAutoSave is { Pending: true } pending) pending.SaveNowAsync().Forget();
        tabAutoSave = null;
        tabEdited = false;
        BuildCompanionNav(section);
        selectingNav = true;
        try { companionNav[section].IsChecked = true; }
        finally { selectingNav = false; }

        var page = CompanionContent;
        // A page just opened, or drawn again while scrolled to its top, adds its long lists a few rows at a time; drawn again
        // further down, it adds every row at once, so it keeps its place.
        rowBatches.Begin(openingTab || CompanionScroll.VerticalOffset <= 0);
        page.Children.Clear();
        var group = new TextBlock { Text = GroupTitle(GroupOf(section)).ToUpperInvariant() };
        group.SetResourceReference(StyleProperty, "Eyebrow");
        AutomationProperties.SetAutomationId(group, "CompanionGroupTitle");
        page.Children.Add(group);
        var heading = new TextBlock { Text = TabTitle(section), Margin = new Thickness(0, 4, 0, 0) };
        heading.SetResourceReference(StyleProperty, "PageTitle");
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level1);
        AutomationProperties.SetAutomationId(heading, "CompanionPageTitle");
        page.Children.Add(heading);
        // The page shows the intro's first sentence; the rest (on an optional page, what Off means) is behind its "?".
        var intro = HelpTip.Explain(TabIntro(section), new Thickness(0, 4, 0, 18), "CompanionIntro", TabTitle(section));
        AutomationProperties.SetAutomationId(intro, "CompanionIntro");
        page.Children.Add(intro);

        var body = new StackPanel();
        AutomationProperties.SetName(body, TabTitle(section));
        page.Children.Add(body);
        switch (section)
        {
            case CompanionTab.Thinking or CompanionTab.Voice or CompanionTab.Listening: RenderJobTab(body, section); break;
            case CompanionTab.Vision: VisionPage.Render(body); break;
            case CompanionTab.Reading: ReadingPage.Render(body); break;
            case CompanionTab.Hearing: HearingPage.Render(body); break;
            case CompanionTab.LipSync: LipSyncPage.Render(body); break;
            case CompanionTab.Profiles: ProfilesPage.Render(body); break;
            case CompanionTab.Character: CharacterPage.Render(body); break;
            case CompanionTab.SpeechBubbles: SpeechBubblesPage.Render(body); break;
            case CompanionTab.Emotes: EmotesPage.Render(body); break;
            case CompanionTab.Eyes: EyesPage.Render(body); break;
            case CompanionTab.Touch: TouchPage.Render(body); break;
            case CompanionTab.Personality: PersonalityPage.Render(body); break;
            case CompanionTab.Prompts: PromptsPage.Render(body); break;
            case CompanionTab.Lorebook: LorebookPage.Render(body); break;
            case CompanionTab.AppGuides: AppGuidesPage.Render(body); break;
            case CompanionTab.Memory: MemoryPage.Render(body); break;
            case CompanionTab.People: RenderPeopleTab(body); break;
            case CompanionTab.Replies: new RepliesPage(this).Render(body); break;
            case CompanionTab.CheckIns: CheckInsPage.Render(body); break;
            case CompanionTab.DeepThinking: ThinkingPoolPage.Render(body); break;
            case CompanionTab.Singing: SingingPage.Render(body); break;
            case CompanionTab.Tools: ToolsPage.Render(body); break;
            case CompanionTab.Pictures: PicturesPage.Render(body); break;
            case CompanionTab.Motion: MotionPage.Render(body); break;
            case CompanionTab.SmartHome: SmartHomePage.Render(body); break;
            case CompanionTab.Discord: DiscordPage.Render(body); break;
            case CompanionTab.Messaging: MessagingPage.Render(body); break;
            default: throw new UnreachableException($"The Companion page {section} has no content.");
        }
        // The desktop log's "Companion › Touch showed all 49 rows in 8 batches, 260 ms after it opened." lines.
        var opened = openingTab ? tabOpenedAt : (long?)null;
        rowBatches.End(Dispatcher, opened is not { } start ? null : (rows, batches) =>
        {
            if (!closing) ErrorLog.Info($"Companion › {TabTitle(section)} showed all {rows} rows in {batches} batches, " +
                $"{Stopwatch.GetElapsedTime(start).TotalMilliseconds:0} ms after it opened.");
        });
    }

    // The moved pages are in Pages\<Page>; a page that is not moved yet is a Render...Tab method in its own MainWindow file. The
    // page catalog (titles, groups,
    // icons, intros) is Pages\Companion\CompanionPages.cs, and the small builders are in Pages\PageKit.cs.
}
