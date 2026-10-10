using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
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

/// <summary>The Companion page's pages: the one place each choice that shapes Martlet is made, listed by group in a side list.
/// Home and Devices link here. A new page adds its value here (in list order) and one arm each in GroupOf, TabTitle, TabGlyph,
/// TabIntro and RenderTab.</summary>
internal enum CompanionTab
{
    Thinking, Listening, Voice, LipSync, DeepThinking, Singing, Pictures, Vision, Reading, Hearing, Profiles, Personality, Prompts, Lorebook,
    Memory, People, Character, SpeechBubbles, Emotes, Eyes, Touch, Replies, CheckIns, Tools, SmartHome, Discord, Messaging
}

/// <summary>The side list's groups, in order: how it works (the jobs Martlet needs, in priority order: Thinking, then listening,
/// voice and lip-sync), optional extras (parts Martlet works without, in the priority list's order: vision, reading, hearing, the
/// Thinking pool, smart home, singing and pictures; <see cref="OptionalExtras.Order"/>), who it is (personality and what it
/// knows), how it looks (the desktop character: its model, speech bubbles, emotes, eyes and touch) and what it does (how it
/// answers and acts). A group with no pages yet is not shown.</summary>
internal enum CompanionGroup { HowItWorks, Extras, WhoItIs, HowItLooks, WhatItDoes }

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
    private enum JobPlace { ThisPc, Computer, Cloud }

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

    private static readonly CloudProvider OpenAiCloud = new("OpenAI", null, false, null, true);
    private static readonly CloudProvider CustomCloud = new("Custom OpenAI-compatible server", "", true, null, false);
    internal static readonly IReadOnlyList<CloudProvider> ThinkingProviders =
    [
        OpenAiCloud,
        .. ChatCompletionsEndpointCatalog.NamedEndpoints.Select(e => new CloudProvider(e.Name, e.BaseUrl, true, e.DefaultModelId, true)),
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

    /// <summary>The setup job a page sets up, or null for a page that isn't one of Setup's jobs.</summary>
    private static SetupRole? JobRole(CompanionTab section) => section switch
    {
        CompanionTab.Thinking => SetupRole.Llm,
        CompanionTab.Voice => SetupRole.Tts,
        CompanionTab.Listening => SetupRole.Stt,
        _ => null
    };

    internal static CompanionTab TabFor(SetupRole role) => role switch
    {
        SetupRole.Tts => CompanionTab.Voice,
        SetupRole.Stt => CompanionTab.Listening,
        _ => CompanionTab.Thinking
    };

    internal static CompanionTab TabFor(string job) => job switch
    {
        ClusterJobs.Speaking => CompanionTab.Voice,
        ClusterJobs.Listening => CompanionTab.Listening,
        ClusterJobs.LipSync => CompanionTab.LipSync,
        _ => CompanionTab.Thinking
    };

    internal static CompanionGroup GroupOf(CompanionTab section) => section switch
    {
        CompanionTab.Thinking or CompanionTab.Voice or CompanionTab.Listening or CompanionTab.LipSync => CompanionGroup.HowItWorks,
        CompanionTab.DeepThinking or CompanionTab.Singing or CompanionTab.Pictures or CompanionTab.Vision or CompanionTab.Reading or
            CompanionTab.Hearing or CompanionTab.SmartHome => CompanionGroup.Extras,
        CompanionTab.Profiles or CompanionTab.Personality or CompanionTab.Prompts or CompanionTab.Lorebook or CompanionTab.Memory or CompanionTab.People => CompanionGroup.WhoItIs,
        CompanionTab.Character or CompanionTab.SpeechBubbles or CompanionTab.Emotes or CompanionTab.Eyes or CompanionTab.Touch => CompanionGroup.HowItLooks,
        CompanionTab.Replies => CompanionGroup.WhatItDoes,
        CompanionTab.CheckIns => CompanionGroup.WhatItDoes,
        CompanionTab.Tools => CompanionGroup.WhatItDoes,
        CompanionTab.Discord => CompanionGroup.WhatItDoes,
        CompanionTab.Messaging => CompanionGroup.WhatItDoes,
        _ => CompanionGroup.WhatItDoes
    };

    /// <summary>The side list's pages in order: by group, then the jobs and the other groups in their list order, and the
    /// Optional extras in the priority list's order (<see cref="OptionalExtras.Order"/>).</summary>
    internal static IReadOnlyList<CompanionTab> SideListOrder() =>
    [
        .. Enum.GetValues<CompanionTab>().Select((tab, index) => (tab, index))
            .OrderBy(t => GroupOf(t.tab))
            .ThenBy(t => GroupOf(t.tab) == CompanionGroup.Extras ? OptionalExtras.Order(t.tab) : t.index)
            .Select(t => t.tab)
    ];

    internal static string GroupTitle(CompanionGroup group) => group switch
    {
        CompanionGroup.HowItWorks => "How it works",
        CompanionGroup.Extras => "Optional extras",
        CompanionGroup.WhoItIs => "Who it is",
        CompanionGroup.HowItLooks => "How it looks",
        _ => "What it does"
    };

    internal static string TabTitle(CompanionTab section) => section switch
    {
        CompanionTab.Thinking => "Thinking",
        CompanionTab.DeepThinking => "Thinking pool",
        CompanionTab.Singing => "Singing",
        CompanionTab.Voice => "Voice",
        CompanionTab.Listening => "Listening",
        CompanionTab.Vision => "Vision",
        CompanionTab.Reading => "Reading",
        CompanionTab.Hearing => "Hearing",
        CompanionTab.LipSync => "Lip-sync",
        CompanionTab.Profiles => "Profiles",
        CompanionTab.Character => "Character",
        CompanionTab.SpeechBubbles => "Speech bubbles",
        CompanionTab.Emotes => "Emotes and motions",
        CompanionTab.Eyes => "Eyes",
        CompanionTab.Touch => "Touch",
        CompanionTab.Personality => "Personality",
        CompanionTab.Prompts => "Prompts",
        CompanionTab.Lorebook => "Lorebook",
        CompanionTab.Memory => "Memory",
        CompanionTab.People => "People",
        CompanionTab.Replies => "Replies",
        CompanionTab.CheckIns => "Check-ins",
        CompanionTab.Tools => "Tools",
        CompanionTab.Pictures => "Pictures",
        CompanionTab.SmartHome => "Smart home",
        CompanionTab.Discord => "Discord",
        CompanionTab.Messaging => "Messaging",
        _ => section.ToString()
    };

    /// <summary>The page's icon in the side list (Segoe Fluent Icons).</summary>
    internal static string TabGlyph(CompanionTab section) => section switch
    {
        CompanionTab.Thinking => "\uE82F",
        CompanionTab.DeepThinking => "\uE945",
        CompanionTab.Singing => "\uEC4F",
        CompanionTab.Voice => "\uE767",
        CompanionTab.Listening => "\uE720",
        CompanionTab.Vision => "\uE890",
        CompanionTab.Reading => "\uE7BC",
        CompanionTab.Hearing => "\uE7F6",
        CompanionTab.LipSync => "\uE8BD",
        CompanionTab.Profiles => "\uE748",
        CompanionTab.Character => "\uE77B",
        CompanionTab.SpeechBubbles => "\uE90A",
        CompanionTab.Emotes => "\uE899",
        CompanionTab.Eyes => "\uE7B3",
        CompanionTab.Touch => "\uE815",
        CompanionTab.Personality => "\uE76E",
        CompanionTab.Prompts => "\uE943",
        CompanionTab.Lorebook => "\uE736",
        CompanionTab.Memory => "\uE8F1",
        CompanionTab.People => "\uE716",
        CompanionTab.Replies => "\uE8F2",
        CompanionTab.CheckIns => "\uE73E",
        CompanionTab.Tools => "\uE90F",
        CompanionTab.Pictures => "\uE8B9",
        CompanionTab.SmartHome => "\uEC26",
        CompanionTab.Discord => "\uE902",
        CompanionTab.Messaging => "\uE724",
        _ => "\uE76E"
    };

    /// <summary>The line under the page title: what the page decides, in one or two sentences. Every Optional extras page starts
    /// with "Optional." and ends with what Off means (<see cref="OptionalExtras.OffMeans"/>).</summary>
    internal static string TabIntro(CompanionTab section) => section switch
    {
        CompanionTab.Thinking => "Martlet needs Thinking to answer you. Choose where it thinks and which model it uses. This PC keeps conversations local, free and with no account.",
        CompanionTab.DeepThinking => Extra(section, "Other models work out hard tasks and background jobs, ideally on another computer, so Thinking keeps talking at full speed. The machine list is the pool: turn each machine on or off, and with none on, the pool is off."),
        CompanionTab.Singing => Extra(section, "Martlet sings songs you ask for, in the voice it speaks with, on an NVIDIA graphics card."),
        CompanionTab.Voice => "Choose how Martlet speaks and where speech is generated.",
        CompanionTab.Listening => "Choose the microphone, push-to-talk mode and speech recognition.",
        CompanionTab.Vision => Extra(section, "Martlet looks at your screen or a camera once you press Start watching, with the model you choose."),
        CompanionTab.Reading => Extra(section, "While Martlet watches your screen, it reads the text on it: with Windows OCR on this PC or Martlet's Reading role."),
        CompanionTab.Hearing => Extra(section, "A model hears how you say things (your tone, laughter, sighs and the sounds around you), not only your words."),
        CompanionTab.LipSync => "Choose what moves the character's mouth.",
        CompanionTab.Profiles => "Switch who Martlet is in one step: each profile sets the character's look, voice and personality together.",
        CompanionTab.Character => "Choose Martlet's character, and its size and position on your desktop.",
        CompanionTab.SpeechBubbles => "Show Martlet's spoken words in a bubble beside the character, or as subtitles.",
        CompanionTab.Emotes => "Choose the emotes and motions that Martlet's replies can play on the character, and make combos of them.",
        CompanionTab.Eyes => "Choose where the character looks. Martlet also finds its eyes, so that emotes drawn over them fit.",
        CompanionTab.Touch => "Choose what the character does when you click or stroke it: its touch zones, and how it feels about each touch.",
        CompanionTab.Personality => "Edit Martlet's personas.",
        CompanionTab.Prompts => "Every instruction Martlet sends to the Thinking model. Edit any of them; your text is used instead of the built-in one.",
        CompanionTab.Lorebook => "Add lore entries Martlet can use when keywords come up.",
        CompanionTab.Memory => "Facts Martlet remembers about you between conversations.",
        CompanionTab.People => "Teach Martlet whose voices it hears and the names they use.",
        CompanionTab.Replies => "Control reply length and creativity.",
        CompanionTab.CheckIns => "Every few minutes the Thinking pool checks what Martlet left on and said, and reminds it of what it forgot: small models forget a lot.",
        CompanionTab.Tools => "Let Martlet run terminal commands and use MCP tools while you talk, and choose when it must ask first.",
        CompanionTab.Pictures => Extra(section, "Martlet draws pictures when you ask: on your own graphics card or with a cloud provider."),
        CompanionTab.SmartHome => Extra(section, "Find, set up or install Home Assistant, share it with your other computers, and let Martlet control your home when you ask."),
        CompanionTab.Discord => "Put Martlet on Discord: set up its bot, connect it, invite it to servers and choose where it chats.",
        CompanionTab.Messaging => "Talk to Martlet from Telegram or WhatsApp on your phone, with the same memory and personality, while Martlet runs on this PC.",
        _ => ""
    };

    /// <summary>An Optional extras page's intro: "Optional.", what it does, then what Off means.</summary>
    private static string Extra(CompanionTab section, string what) => $"Optional. {what} Off: {OptionalExtras.OffMeans(section)}.";

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

    /// <summary>The side list, built once: each group's title, then its pages. Choosing a page opens it.</summary>
    private void BuildCompanionNav()
    {
        if (companionNav.Count > 0) return;
        foreach (var group in SideListOrder().GroupBy(GroupOf).OrderBy(g => g.Key))
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
    }

    private void RenderTab()
    {
        if (openTab is not { } section) return;
        if (tabAutoSave is { Pending: true } pending) pending.SaveNowAsync().Forget();
        tabAutoSave = null;
        tabEdited = false;
        BuildCompanionNav();
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
        page.Children.Add(heading);
        var intro = Note(TabIntro(section), new Thickness(0, 4, 0, 18));
        AutomationProperties.SetAutomationId(intro, "CompanionIntro");
        page.Children.Add(intro);

        var body = new StackPanel();
        AutomationProperties.SetName(body, TabTitle(section));
        page.Children.Add(body);
        switch (section)
        {
            case CompanionTab.Thinking or CompanionTab.Voice or CompanionTab.Listening: RenderJobTab(body, section); break;
            case CompanionTab.Vision: RenderVisionPage(body); break;
            case CompanionTab.Reading: RenderReadingTab(body); break;
            case CompanionTab.Hearing: RenderHearingPage(body); break;
            case CompanionTab.LipSync: RenderLipSyncTab(body); break;
            case CompanionTab.Profiles: RenderProfilesTab(body); break;
            case CompanionTab.Character: RenderCharacterTab(body); break;
            case CompanionTab.SpeechBubbles: RenderSpeechBubblesTab(body); break;
            case CompanionTab.Emotes: RenderEmotesTab(body); break;
            case CompanionTab.Eyes: RenderEyesTab(body); break;
            case CompanionTab.Touch: RenderTouchTab(body); break;
            case CompanionTab.Personality: RenderPersonalityTab(body); break;
            case CompanionTab.Prompts: RenderPromptsTab(body); break;
            case CompanionTab.Lorebook: RenderLorebookTab(body); break;
            case CompanionTab.Memory: RenderMemoryTab(body); break;
            case CompanionTab.People: RenderPeopleTab(body); break;
            case CompanionTab.Replies: RenderRepliesTab(body); break;
            case CompanionTab.CheckIns: RenderCheckInsTab(body); break;
            case CompanionTab.DeepThinking: RenderDeepThinkingTab(body); break;
            case CompanionTab.Singing: RenderSingingTab(body); break;
            case CompanionTab.Tools: RenderToolsTab(body); break;
            case CompanionTab.Pictures: RenderPicturesTab(body); break;
            case CompanionTab.SmartHome: RenderSmartHomeTab(body); break;
            case CompanionTab.Discord: RenderDiscordTab(body); break;
            case CompanionTab.Messaging: RenderMessagingTab(body); break;
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

    // ---------- job pages ----------

    /// <summary>Routes that run on this PC without Martlet's host service: Ollama or another model app on this PC for thinking,
    /// installed Windows speech recognition, Parakeet and native whisper.cpp.</summary>
    private static bool RunsHereWithoutHost(SetupRoute route) =>
        IsLocalOllama(route) || IsLocalServer(route) ||
        route.RouteType is SetupRouteType.LocalWindowsStt or SetupRouteType.LocalWhisper or SetupRouteType.LocalParakeet;

    private void RenderJobTab(Panel page, CompanionTab section)
    {
        var role = JobRole(section)!.Value;
        var job = HostJob.For(role)!;
        var route = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == role);
        var thisPc = ThisPcHost();
        // A computer that hasn't chosen yet shows what your Martlet network does for this job, not Martlet's default.
        var networkHost = clusterEnabled ? clusterPlan.For(job.Job)?.HostId : null;
        var current = route is null ? networkHost is not null && networkHost != thisPc?.HostId ? JobPlace.Computer : JobPlace.ThisPc
            : RunsHereWithoutHost(route) ? JobPlace.ThisPc
            : SelfHostSetup.IsGateway(route.RouteType)
                ? route.Gateway?.HostId == thisPc?.HostId && role != SetupRole.Llm ? JobPlace.ThisPc : JobPlace.Computer
                : JobPlace.Cloud;
        var place = tabPlace.TryGetValue(section, out var chosen) ? chosen : current;

        page.Children.Add(JobNowCard(section, job, route));

        page.Children.Add(WhereItRunsCard(section, section.ToString(), "Where it runs", null, route is null ? null : current, place,
            (JobPlace.ThisPc, "This PC (recommended)", role switch
            {
                SetupRole.Llm => "Ollama or your own model app, on this PC's graphics card. Private and free.",
                SetupRole.Tts => "A voice engine on this PC's NVIDIA graphics card, or Chatterbox Nano on the processor. Audio stays here.",
                _ => "Parakeet on the processor (no setup) or Whisper on the graphics card. Your voice stays here."
            }),
            (JobPlace.Computer, "Another of your computers",
                $"A paired computer does {job.Job} with its own graphics card, so this PC stays free."),
            (JobPlace.Cloud, "A cloud provider", role switch
            {
                SetupRole.Llm => "NVIDIA Build (free key), OpenAI, OpenRouter and others. Your messages leave this PC; some charge.",
                SetupRole.Tts => "OpenAI's voices, or ElevenLabs with your cloned voice. No graphics card needed, but reply text leaves " +
                    "this PC and requests cost money.",
                _ => "OpenAI with your API key. Your recordings leave this PC and requests cost money."
            })));

        if (place == JobPlace.ThisPc && role == SetupRole.Llm)
        {
            // Which model apps answer here decides what This PC offers, so look once without being asked (loopback only).
            if (localServers is null && !lookingForLocalServers) LookForLocalServersAsync(quiet: true).Forget();
            var app = localApp ?? DefaultLocalApp(route, !Prerequisites.IsMissing(Prerequisites.Ollama), OtherLocalServers().Count);
            page.Children.Add(LocalAppCard(route, app));
            page.Children.Add(app == LocalApp.Ollama ? LocalThinkingCard(route) : LocalServerCard(route));
        }
        else
            page.Children.Add(place switch
            {
                JobPlace.ThisPc when role == SetupRole.Tts => VoiceEnginesCard(route, thisPc, onThisPc: true),
                JobPlace.Computer when role == SetupRole.Tts => VoiceEnginesCard(route, thisPc, onThisPc: false),
                JobPlace.ThisPc => LocalListeningCard(route, thisPc),
                JobPlace.Computer => ComputersCard(job, route, role == SetupRole.Llm ? null : thisPc),
                _ => CloudCard(section, job, route)
            });


        // Chatterbox Original's General and Expressive style, while it speaks or is the chosen engine.
        if (section == CompanionTab.Voice && place != JobPlace.Cloud &&
            (SpeechEngines.ForRoute(route?.GatewaySnapshot?.RouteId) == SpeechEngines.ChatterboxOriginal ||
             SpeakingEngineChoice.Current == SpeechEngines.ChatterboxOriginal))
            page.Children.Add(ChatterboxStyleCard());

        // No hosted provider has a key: a free one from NVIDIA Build keeps Martlet able to reply when your computers are offline or
        // have no room for thinking. It goes to If Thinking fails while Thinking has a place (a local model answers sooner), and
        // to Thinking itself when nothing does it.
        if (section == CompanionTab.Thinking && place != JobPlace.Cloud && FreeKeyPrompt.Shows(ConfiguredProviders()))
        {
            var nobody = route is null && networkHost is null ||
                recommendedNotice is { } notice && notice.Recommendation.CannotReply;
            var tip = Note(FreeKeyPrompt.Tip, new Thickness(0, 0, 0, 0));
            AutomationProperties.SetAutomationId(tip, "FreeKeyTip-Thinking");
            page.Children.Add(Card(Heading(FreeKeyPrompt.Title), tip,
                Row(PageButton(FreeKeyPrompt.AddLabel, () => OpenFreeKey(FreeKeyPrompt.Use(true, nobody), fromReview: false), id: "FreeKeyAdd-Thinking"),
                    PageButton(FreeKeyPrompt.GetLabel, () => OpenKeyPageFrom(tip), link: true, id: "FreeKeyGet-Thinking"))));
        }

        if (role == SetupRole.Llm) page.Children.Add(FallbackCard());

        // The voices the self-hosted engines copy from your recordings, wherever one can speak: this PC or another of your
        // computers. A cloud provider has its own voices.
        if (section == CompanionTab.Voice && place != JobPlace.Cloud) page.Children.Add(VoicesCard(route));

        if (section == CompanionTab.Voice) page.Children.Add(AudioCard(output: true));
        if (section == CompanionTab.Voice)
        {
            page.Children.Add(SpeakRepliesCard());
            page.Children.Add(QuickSoundsCard());
        }
        if (section == CompanionTab.Listening) page.Children.Add(AudioCard(output: false));
        if (section == CompanionTab.Listening) page.Children.Add(TalkModeCard());
        if (section == CompanionTab.Listening) page.Children.Add(EchoCard());
        if (section == CompanionTab.Listening) page.Children.Add(PcAudioCard());
        // How you say things (the audio model, and whether a model hears your recording) is on Companion › Hearing.
        if (section == CompanionTab.Listening)
            page.Children.Add(Card(Heading("How Martlet hears your tone"),
                Row(PageButton("Companion › Hearing", () => OpenCompanion(CompanionTab.Hearing), link: true, id: "ListeningOpenHearing"))));
        if (section == CompanionTab.Listening)
            page.Children.Add(Card(Heading("Who is talking"),
                Note(localVoices.Active
                    ? "Voice recognition is on. Manage known voices on People."
                    : "Turn on voice recognition so Martlet can learn who's speaking.",
                    new Thickness(0, 0, 0, 0)),
                Row(PageButton("Open People", () => OpenCompanion(CompanionTab.People), link: true, id: "OpenPeople"))));

        if (OldKeysCard(section, role) is { } oldKeys) page.Children.Add(oldKeys);
    }

    /// <summary>The voice a route speaks with, in words: ", voice alloy", ", voice Mia (cloned on ElevenLabs)", or nothing.</summary>
    private static string VoiceSuffix(SetupRoute route) =>
        route.Reference is { } reference ? $", voice {reference.PresetName}"
        : route.ClonedVoice is { } cloned ? $", voice {cloned.Name} (cloned on ElevenLabs)"
        : route.VoiceId is { } voice ? $", voice {voice}"
        : "";

    /// <summary>What the job uses now, first on every setup page, with any problem that stops it.</summary>
    private Border JobNowCard(CompanionTab section, HostJob job, SetupRoute? route)
    {
        var problem = coverage.FirstOrDefault(c => c.Job == job.Job && c.IsProblem);
        var routeName = route is null ? ""
            : route.RouteType == SetupRouteType.LocalParakeet ? $"{PlaceName(route)}: {ParakeetName(route.ModelId)}"
            : route.RouteType == SetupRouteType.LocalWhisper ? PlaceName(route)
            : $"{PlaceName(route)}: {HostInputDialog.OptionText(route.ModelId)}";
        var status = route is null
            ? clusterEnabled && clusterPlan.For(job.Job)?.HostId is not null
                ? "Not set up on this PC yet."
                : section == CompanionTab.Voice
                ? "Not chosen yet. Pick a voice engine below."
                : "Not chosen yet. This PC is recommended below."
            : routeName +
                VoiceSuffix(route) +
                (route.Enabled == false ? " (turned off)" : route.Consent is null ? " (not confirmed yet)" : "");
        var now = new StackPanel();
        now.Children.Add(Heading("Now"));
        var nowText = new TextBlock { Text = status, FontSize = 15, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(nowText, "SetupJobNow-" + section);
        now.Children.Add(nowText);
        if (section == CompanionTab.Thinking) now.Children.Add(TextModelLine(route));
        if (NetworkJobNote(job.Job) is { } network)
        {
            var line = new TextBlock { Text = network, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            line.SetResourceReference(StyleProperty, "Muted");
            AutomationProperties.SetAutomationId(line, "SetupJobNetwork-" + section);
            now.Children.Add(line);
        }
        if (section == CompanionTab.Listening && StandInLine(route) is { } standIn) now.Children.Add(standIn);
        if (problem is not null)
        {
            var warning = new TextBlock { Text = $"Needs attention: {problem.Problem} {problem.Effect}", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            warning.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            now.Children.Add(warning);
        }
        if (section == CompanionTab.Listening && AudioMissing(output: false))
        {
            var mic = new TextBlock { Text = homeSettings?.Audio?.Input.EndpointId is null
                    ? "No microphone found. Plug one in or type instead."
                    : "Your chosen microphone isn't connected. Plug it in, pick another or type instead.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            mic.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            AutomationProperties.SetAutomationId(mic, "ListeningMicMissing");
            now.Children.Add(mic);
        }
        return Card(now);
    }

    /// <summary>The "Where it runs" chooser, the same on every job: one option per place, the one in use marked. Choosing a place
    /// shows that place's card below it; the card's own button commits the choice.</summary>
    private Border WhereItRunsCard(CompanionTab section, string id, string heading, string? note, JobPlace? inUse, JobPlace place,
        params (JobPlace Value, string Label, string Detail)[] options)
    {
        var where = new StackPanel();
        where.Children.Add(Heading(heading));
        if (note is not null) where.Children.Add(Note(note, new Thickness(0, 0, 0, 10)));
        foreach (var (value, label, detail) in options)
        {
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = label + (value == inUse ? "  \u00b7  in use" : ""),
                FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            text.Children.Add(Note(detail, new Thickness(0, 2, 0, 0)));
            var option = new RadioButton { Content = text, GroupName = "Place-" + id, IsChecked = value == place, Margin = new Thickness(0, 0, 0, 10) };
            AutomationProperties.SetName(option, $"{label}: {detail}");
            AutomationProperties.SetAutomationId(option, $"Place-{id}-{value}");
            option.Checked += (_, _) =>
            {
                tabPlace[section] = value;
                RenderTab();
            };
            where.Children.Add(option);
        }
        return Card(where);
    }

    private Border AudioCard(bool output)
    {
        var choice = output ? homeSettings?.Audio?.Output : homeSettings?.Audio?.Input;
        var checkpoint = choice?.Checkpoint;
        var missing = AudioMissing(output);
        var what = output ? "Speakers" : "Microphone";
        var device = choice?.EndpointId is null
            ? output ? "your Windows default speakers" : "your Windows default microphone"
            : $"\"{choice.DisplayName}\"";
        var text = missing
            ? choice?.EndpointId is null
                ? output ? "No speakers or headset found. Plug them in or turn them on in Windows Sound settings."
                    : "No microphone found. Plug one in or turn it on in Windows Sound settings."
                : $"{device} isn't connected. Plug it in or pick another."
            : $"Using {device}" + (checkpoint is not null ? $", tested on {checkpoint.TestedAt.ToLocalTime():d}." : ".");
        var line = Note(text + (output ? " Martlet plays its voice here." : " Martlet listens here when you talk in the talk window."),
            new Thickness(0, 0, 0, 8));
        if (missing) line.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        return Card(Heading(what), line,
            Row(PageButton(missing ? $"Fix {what.ToLowerInvariant()}" : $"Test or change {what.ToLowerInvariant()}",
                () => RunNodeAction(NodeAction.AudioSetup), primary: missing, id: "OpenAudioSetup")));
    }

    // ---------- this PC: Ollama for thinking ----------

    /// <summary>The model typed under Another Ollama model, kept while the page draws again.</summary>
    private string? ollamaTyped;

    /// <summary>Thinking › This PC › Ollama: Ollama's state, then its models as an option picker (<c>Picker-OllamaModel-&lt;model&gt;</c>):
    /// the suggestions with their facts (fit on this PC's card, graphics memory, how soon they answer, whether they hear),
    /// the models Ollama already has, and Another Ollama model for typing any name (<c>SetupLocalModel</c>). The shown
    /// model's details hold Download model, Check Ollama, Test model and Use Ollama on this PC, and the last test.</summary>
    private Border LocalThinkingCard(SetupRoute? route)
    {
        var installed = !Prerequisites.IsMissing(Prerequisites.Ollama);
        // What Ollama already has decides whether switching needs a download, so look once without being asked (loopback only).
        if (installed && ollamaModels is null && !ollamaAutoChecked)
        {
            ollamaAutoChecked = true;
            CheckOllamaAsync(quiet: true).Forget();
        }
        var inUse = IsLocalOllama(route) ? route!.ModelId : null;
        var options = JobOptions.OllamaModels(LocalChatModels, ollamaModels, inUse, machine.BestGpu?.MemoryGb)
            .Select(option => option with { Details = () => OllamaModelControls(option.Key == JobOptions.OtherOllamaModel ? null : option.Key, installed) })
            .ToList();

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), FontWeight = FontWeights.SemiBold,
            Text = (!installed ? "Ollama isn't installed on this PC yet."
                : ollamaModels is null && LocalOllamaRecovery.Last is { Stops: true, OllamaError: { } said }
                    ? $"Ollama is installed, but it stops when it starts. Ollama says: \"{said.TrimEnd('.')}\". Check Ollama shows what to do."
                : ollamaModels is null ? "Ollama is installed. Check it to see which models are downloaded."
                : ollamaModels.Count == 0 ? "Ollama is running, but no model is downloaded yet."
                : $"Ollama is running with: {string.Join(", ", ollamaModels)}.") +
                (inUse is not null ? $" Thinking uses {inUse}." : "") };
        AutomationProperties.SetAutomationId(status, "SetupOllamaStatus");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);

        var recommended = RecommendedLocalModel(machine.BestGpu?.MemoryGb);
        var smartest = LargestLocalModel(machine.BestGpu?.MemoryGb);
        var suggestion = Note((machine.BestGpu is { } best ? $"This PC has {best.Describe()}" : "No dedicated graphics card was found, so models run on the processor") +
            (smartest.Id != recommended.Id ? $": it fits up to {smartest.Id} with room for a game and Martlet's character. " : ". ") +
            $"Recommended: {recommended.Id}, the fastest. Bigger models are smarter but slower.", new Thickness(0, 0, 0, 8));
        AutomationProperties.SetAutomationId(suggestion, "SetupLocalRecommendation");

        return Card(Heading("Ollama on this PC"),
            Note("Ollama runs a local conversation model. Your messages stay on this PC, with no API key or per-request cost.", new Thickness(0, 0, 0, 8)),
            status,
            suggestion,
            OptionPickerBody("OllamaModel", options));
    }

    /// <summary>The shown Ollama model's controls: for Another Ollama model (<paramref name="fixedModel"/> null) the name box,
    /// then Ollama's buttons for that model and the last test of it.</summary>
    private IEnumerable<UIElement> OllamaModelControls(string? fixedModel, bool installed)
    {
        TextBox? model = null;
        if (fixedModel is null)
        {
            model = new TextBox { MaxLength = 128, Width = 420, HorizontalAlignment = HorizontalAlignment.Left, Text = ollamaTyped ?? "" };
            AutomationProperties.SetName(model, "Local model");
            AutomationProperties.SetAutomationId(model, "SetupLocalModel");
            model.TextChanged += (_, _) => { tabEdited = true; ollamaTyped = model.Text; };
            yield return new Label { Content = "Ollama _model", Target = model, Padding = new Thickness(0, 4, 0, 4) };
            yield return model;
            var own = Note("Download model gets it; models already downloaded are listed above.", new Thickness(0, 4, 0, 0));
            AutomationProperties.SetAutomationId(own, "SetupLocalOwnModels");
            yield return own;
        }

        string ModelId() => fixedModel ?? (model!.Text ?? "").Trim();
        var tested = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        AutomationProperties.SetAutomationId(tested, "SetupLocalModelTest");
        AutomationProperties.SetLiveSetting(tested, AutomationLiveSetting.Polite);
        void ShowTest()
        {
            var last = localModelTest is { } outcome && outcome.Model == ModelId() ? outcome : null;
            tested.Text = last?.Text ?? (ModelId().Length == 0 ? "" : $"{ModelId()} isn't tested yet. Test it before using it.");
            tested.SetResourceReference(TextBlock.ForegroundProperty, last is null ? "MutedBrush" : last.Passed && !last.Warning ? "SuccessBrush"
                : "WarningBrush");
        }
        ShowTest();
        showLocalTest = ShowTest;
        if (model is not null) model.TextChanged += (_, _) => ShowTest();
        var test = PageButton("Test model", () => TestLocalModelAsync(ModelId()).Forget(), id: "SetupTestLocalModel");
        // Until Ollama is installed, installing it (with the chosen model, then switching to it) is the only step that does anything.
        var buttons = installed
            ? Row(
                PageButton("Download model", () => PullOllamaModelAsync(ModelId()).Forget(), id: "SetupPullModel"),
                PageButton("Check Ollama", () => CheckOllamaAsync().Forget(), id: "SetupCheckOllama"),
                test,
                PageButton("Use Ollama on this PC", () => SaveLocalThinkingAsync(ModelId()).Forget(), primary: true, id: "SetupUseLocalThinking"))
            : Row(
                PageButton("Install Ollama and use it", () => InstallOllamaAsync(ModelId()).Forget(), primary: true, id: "SetupInstallOllama"),
                test,
                PageButton("Use Ollama on this PC", () => SaveLocalThinkingAsync(ModelId()).Forget(), id: "SetupUseLocalThinking"));
        buttons.Margin = new Thickness(0, 8, 0, 0);
        yield return buttons;
        yield return tested;
    }
    /// <summary>The last model test on this PC's Ollama: which model, what it found, and whether it worked.</summary>
    private sealed record LocalModelTestOutcome(string Model, string Text, bool Passed, bool Warning);

    /// <summary>Loads <paramref name="model"/> in this PC's Ollama and asks it for a short streamed reply, the way Martlet's
    /// replies do, so a model that won't run here shows up while it is being set up rather than on the first message.</summary>
    private async Task TestLocalModelAsync(string model)
    {
        try { ChatCompletionsSetup.ModelId(model); }
        catch (ContractException error) { ActionText.Text = error.Message; return; }
        if (testingLocalModel)
        {
            ActionText.Text = "A model test is already running.";
            return;
        }
        testingLocalModel = true;
        ActionText.Text = $"Testing {model} in Ollama...";
        // Like a reply on this route: no reply budget unless a max reply length is set, and the local route's first-answer wait.
        int? replyTokens = GenerationSupport.SendsReplyBudget(GenerationSupport.LocalOllamaChatBaseUrl, homeSettings?.Generation)
            ? homeSettings!.Generation!.ReplyTokens : null;
        LocalModelTestResult? result = null;
        string? failure = null;
        try
        {
            await HostRunWindow.RunAsync(this, $"Test {model}", async run =>
            {
                try
                {
                    result = await LocalOllama.TestAsync(model, replyTokens, LiveConversationConfiguration.LocalOllamaTextLimits.FirstDeltaTimeout,
                        run.Status, run.Output, run.Token, GenerationSettings.ThinkingSteps(homeSettings?.Generation));
                    return result.Summary;
                }
                catch (InvalidOperationException error)
                {
                    failure = error.Message;
                    throw;
                }
            });
        }
        finally { testingLocalModel = false; }
        if (closing) return;
        var route = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        var inUse = IsLocalOllama(route) && route!.ModelId == model;
        if (result is not null)
        {
            // The test loaded the model, so Ollama says exactly how much context it gives it.
            var context = await CheckLocalModelContextAsync(model);
            var summary = result.Summary + (context?.ContextTokens is { } given
                ? $" Ollama gives it {given:N0} tokens of context{(context.ModelMaximum is { } most && most > given ? $" of the {most:N0} it holds; raise Ollama's context length setting to give it more" : "")}."
                : "");
            if (closing) return;
            localModelTest = new(model, summary, true, result.Warning);
            ActionText.Text = summary + (inUse ? "" : " Choose Use Ollama on this PC to use it.");
            showContextStatus?.Invoke();
        }
        else if (failure is not null)
        {
            localModelTest = new(model, $"Test failed: {failure}", false, false);
            ActionText.Text = localModelTest.Text;
        }
        else ActionText.Text = $"Testing {model} stopped before it finished.";
        showLocalTest?.Invoke();
    }

    /// <summary>Downloads a model into this PC's Ollama in a run window (no console), then refreshes what Ollama has.</summary>
    private async Task PullOllamaModelAsync(string model)
    {
        try { ChatCompletionsSetup.ModelId(model); }
        catch (ContractException error) { ActionText.Text = error.Message; return; }
        ActionText.Text = $"Downloading {model} with Ollama...";
        var done = await HostRunWindow.RunAsync(this, $"Download {model}", async run =>
        {
            await LocalOllama.PullAsync(model, run.Status, run.Output, run.Token);
            return $"{model} is downloaded. Test it, then choose Use Ollama on this PC.";
        }, join: true);
        if (closing) return;
        ActionText.Text = done ?? $"{model} was not downloaded.";
        if (done is not null) await CheckOllamaAsync();
    }

    /// <summary>One click: installs Ollama with the chosen model (no console), thinks with it once it is there, then tests it so
    /// a model that won't run on this PC shows up now rather than on the first message.</summary>
    private async Task InstallOllamaAsync(string model)
    {
        try { ChatCompletionsSetup.ModelId(model); }
        catch (ContractException error) { ActionText.Text = error.Message; return; }
        await InstallPrerequisitesAsync([Prerequisites.Ollama], model);
        if (closing) return;
        if (Prerequisites.IsMissing(Prerequisites.Ollama))
        {
            if (openTab == CompanionTab.Thinking) RenderTab();
            return;
        }
        await CheckOllamaAsync();
        if (closing) return;
        if (ollamaModels?.Contains(model, StringComparer.Ordinal) != true)
        {
            ActionText.Text = $"Ollama is installed. Download {model} to use it.";
            return;
        }
        await SaveLocalThinkingAsync(model);
        if (!closing) await TestLocalModelAsync(model);
    }

    /// <summary>Asks the local Ollama (loopback only) which models it has. <paramref name="quiet"/>: the Thinking tab's own
    /// check when it opens, which leaves the status line and a model being typed alone. Otherwise, when Ollama doesn't answer,
    /// the status line says why in Ollama's own words, after Martlet repaired the known cause (<see cref="OllamaNotAnsweringAsync"/>).</summary>
    private async Task CheckOllamaAsync(bool quiet = false)
    {
        IReadOnlyList<string>? models;
        try { models = await LocalOllama.ModelsAsync(TimeSpan.FromSeconds(4), lifetime.Token); }
        catch (OperationCanceledException) { return; }
        if (closing) return;
        string? said = null;
        if (models is null && !quiet && !Prerequisites.IsMissing(Prerequisites.Ollama))
        {
            var (text, repaired) = await OllamaNotAnsweringAsync("check again");
            if (closing) return;
            said = text;
            if (repaired)
            {
                try { models = await LocalOllama.ModelsAsync(TimeSpan.FromSeconds(4), lifetime.Token); }
                catch (OperationCanceledException) { return; }
                if (models is not null && said.Length > 0) said += " ";
            }
        }
        ollamaModels = models?.Take(50).ToArray();
        if (!quiet)
            ActionText.Text = ollamaModels is null
                ? said ?? "Ollama isn't installed on this PC yet. Install it first."
                : said + (ollamaModels.Count == 0 ? "Ollama is running on this PC, with no model downloaded yet."
                    : $"Ollama is running on this PC with {ollamaModels.Count} {(ollamaModels.Count == 1 ? "model" : "models")}.");
        if (!closing && openTab == CompanionTab.Thinking && !(quiet && tabEdited)) RenderTab();
    }

    /// <summary>Use Ollama on this PC. With Ollama installed, the model in the box is downloaded first when it isn't here yet
    /// (after one confirmation) and loaded, in a run window, and only then does Thinking switch to it: the current Thinking
    /// keeps answering until then, the first reply doesn't wait for the load, and a model that won't download or load leaves
    /// Thinking as it was.</summary>
    private async Task<bool> SaveLocalThinkingAsync(string model, bool confirmed = false)
    {
        try { ChatCompletionsSetup.ModelId(model); }
        catch (ContractException error) { ActionText.Text = error.Message; return false; }
        if (!Prerequisites.IsMissing(Prerequisites.Ollama) && !await PrepareLocalThinkingAsync(model, confirmed)) return false;
        if (closing) return false;
        var saved = await SaveSectionRouteAsync(HostJob.Thinking,
            settings => ChatCompletionsSetup.SelectRoute(settings, LocalOllamaBaseUrl, model), key: null,
            $"Martlet now uses {model} in Ollama on this PC." +
            (ollamaModels is { } known && !LocalOllama.Serves(known, model) ? $" Download {model} to use it." : ""));
        // Ollama says how much context it gives the model once it has loaded it (Test model does); nothing leaves this PC.
        if (!closing && IsLocalOllama(homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm)))
            CheckNewModelContextAsync().Forget();
        return saved;
    }

    /// <summary>The model chosen last with Use Ollama on this PC. Several can download and load side by side; one chosen
    /// earlier that gets ready after it doesn't switch Thinking.</summary>
    private string? localThinkingWanted;

    /// <summary>Gets <paramref name="model"/> ready in this PC's Ollama before Thinking switches to it: starts Ollama when it
    /// isn't answering, downloads the model when it isn't here (the owner confirms the download, unless
    /// <paramref name="confirmed"/> already did), then loads it. Returns
    /// whether Thinking may switch now; otherwise the status line says why it didn't. Choosing another model meanwhile
    /// doesn't wait for this one: the newest choice is the one Thinking switches to.</summary>
    private async Task<bool> PrepareLocalThinkingAsync(string model, bool confirmed = false)
    {
        try
        {
            var token = lifetime.Token;
            var models = await LocalOllama.ModelsAsync(TimeSpan.FromSeconds(3), token);
            if (models is null && LocalOllama.Start())
            {
                ActionText.Text = "Starting Ollama on this PC...";
                for (var attempt = 0; models is null && attempt < 15 && !closing; attempt++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), token);
                    models = await LocalOllama.ModelsAsync(TimeSpan.FromSeconds(2), token);
                }
            }
            if (closing) return false;
            if (models is null)
            {
                var (text, repaired) = await OllamaNotAnsweringAsync("try again");
                if (closing) return false;
                if (repaired) models = await LocalOllama.ModelsAsync(TimeSpan.FromSeconds(3), token);
                if (closing) return false;
                if (models is null)
                {
                    ActionText.Text = (text.Length > 0 ? text : "Ollama didn't answer on this PC.") + " Thinking didn't change.";
                    return false;
                }
            }
            ollamaModels = models;
            var thinking = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
            var keeps = thinking is null || IsLocalOllama(thinking) && thinking.ModelId == model ? ""
                : $" Thinking keeps using {(IsLocalOllama(thinking) ? thinking.ModelId : NetworkMap.ProviderName(thinking))} until it's ready.";
            var download = !LocalOllama.Serves(models, model);
            if (download)
            {
                var size = LocalChatModels.FirstOrDefault(m => m.Id == model)?.Size;
                if (!confirmed && !ConfirmationDialog.Confirm(this,
                        $"{model} isn't downloaded on this PC yet. Download it with Ollama{(size is null ? "" : $" ({size})")}, load it and then " +
                        $"switch Thinking to it?{keeps} The model's own license applies.",
                        "Download and switch", questionId: "LocalModelDownloadQuestion"))
                {
                    ActionText.Text = "Thinking didn't change.";
                    return false;
                }
            }
            localThinkingWanted = model;
            var done = await HostRunWindow.RunAsync(this, $"Switch Thinking to {model}", async run =>
            {
                if (keeps.Length > 0) run.Output.Report(keeps.Trim());
                if (download) await LocalOllama.PullAsync(model, run.Status, run.Output, run.Token);
                var loaded = await LocalOllama.LoadAsync(model, run.Status, run.Output, run.Token);
                return $"{model} is {(download ? "downloaded and " : "")}loaded{(loaded >= TimeSpan.FromSeconds(1) ? $" ({loaded.TotalSeconds:0.0} s)" : "")}. " +
                    (localThinkingWanted == model ? "Thinking switches to it now." : $"You chose {localThinkingWanted} since, so Thinking doesn't switch to it.");
            }, join: true);
            if (closing) return false;
            if (download) await CheckOllamaAsync(quiet: true);
            if (done is null)
            {
                ActionText.Text = $"Thinking didn't change: {model} isn't ready. The run window says why.";
                return false;
            }
            if (localThinkingWanted != model)
            {
                ActionText.Text = $"{model} is ready, but you chose {localThinkingWanted} since; Thinking switches to that one when it's ready.";
                return false;
            }
            return true;
        }
        catch (OperationCanceledException) { return false; }
    }

    /// <summary>What Ollama on this PC says about <paramref name="model"/>'s context, kept for replies; loopback only, and the
    /// model isn't loaded for it.</summary>
    private async Task<ModelContextReport?> CheckLocalModelContextAsync(string model)
    {
        try
        {
            using var client = ModelContextProbe.CreateClient(loopback: true);
            var report = await ModelContextProbe.OllamaAsync(client, LocalOllamaOrigin, model, load: false, lifetime.Token);
            if (report.Reached) RecordModelLimit(LocalOllamaBaseUrl, model, report);
            return report;
        }
        catch (OperationCanceledException) { return null; }
    }

    // ---------- option rows (the voice engines are in MainWindow.VoiceEngines.cs) ----------

    private static TextBlock OptionTitle(string title, string? tag, double size = 16)
    {
        var text = new TextBlock { FontSize = size, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        text.Inlines.Add(title);
        if (tag is not null)
        {
            var badge = new System.Windows.Documents.Run("  \u00b7  " + tag) { FontWeight = FontWeights.Normal, FontSize = size - 2 };
            badge.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, tag == "in use" ? "SuccessBrush" : "AccentBrush");
            text.Inlines.Add(badge);
        }
        return text;
    }

    private static Border Option(IEnumerable<UIElement> children, bool inUse)
    {
        var stack = new StackPanel();
        foreach (var child in children) stack.Children.Add(child);
        var option = new Border { Child = stack, BorderThickness = new Thickness(inUse ? 2 : 1), CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16), Margin = new Thickness(0, 10, 0, 0) };
        option.SetResourceReference(Border.BorderBrushProperty, inUse ? "AccentBrush" : "BorderBrush");
        return option;
    }

    // ---------- another of your computers ----------

    private Border ComputersCard(HostJob job, SetupRoute? route, PairedHost? exclude) =>
        ComputersCard(job.Job, job.Engine, job.HostRoleKind, NetworkMap.JobHost(homeSettings, job.Role),
            $"In use: {job.Engine} {(route?.ModelId is { } model ? HostInputDialog.OptionText(model) : null)}.",
            null,
            key => AssignJobAsync(job, key), job.Disclosure, exclude, change: "Change model");

    /// <summary>Every paired host (except <paramref name="exclude"/>) that can run the job, with what it runs and <i>Use it</i>,
    /// plus <i>Add a computer</i>, <i>Check hosts</i> and the Devices map; hosts whose platform or hardware can't run it are
    /// named underneath with why. <paramref name="again"/> labels the owner's button when using it again does something
    /// (choosing another voice); otherwise the owner shows <i>In use</i>. A host that runs the role also has
    /// <paramref name="change"/>, which opens the role's settings there (its model, GPU or CPU...), showing what it runs now.</summary>
    private Border ComputersCard(string job, string engine, string roleKind, string? owner, string ownerDetail, string? again,
        Func<string, Task> assign, string disclosure, PairedHost? exclude, string change = "Change settings")
    {
        var stack = new List<UIElement> { Heading("Your computers") };
        var paired = NetworkMap.Hosts(Inputs()).Where(h => h.HostId != exclude?.HostId).ToArray();
        bool Runs(PairedHost host) => hostChecks.GetValueOrDefault(host.HostId)?.Offers?.ContainsKey(roleKind) == true;
        var unable = paired.Where(h => h.HostId != owner && !Runs(h) && HostCan(h.HostId, roleKind) is { Allowed: false }).ToArray();
        var hosts = paired.Except(unable).ToArray();
        if (hosts.Length == 0)
        {
            var add = $"Add another computer to run {job} there.";
            var none = Note(unable.Length > 0 ? $"None of your paired computers can run {engine}. {add}"
                : exclude is not null ? $"This PC is already selected. {add}"
                : $"No other computers are paired yet. {add}", new Thickness(0, 0, 0, 8));
            AutomationProperties.SetAutomationId(none, "HostChoices-" + job);
            stack.Add(none);
        }
        foreach (var host in hosts)
        {
            var check = hostChecks.GetValueOrDefault(host.HostId);
            var model = check?.Offers?.GetValueOrDefault(roleKind);
            var cannot = model is null ? CannotHand(host.HostId, roleKind, job) : null;
            var detail = owner == host.HostId ? ownerDetail
                : cannot is not null ? $"Unavailable: {cannot}"
                : model is not null ? $"Runs {engine} ({HostInputDialog.OptionText(model)})."
                : check?.Reachable == true ? $"{engine} isn't installed there yet. Martlet can set it up."
                : check?.Reachable == false ? "Not reachable right now." : "Not checked yet.";
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = host.HostId, FontSize = 15, FontWeight = FontWeights.SemiBold });
            var line = Note(detail, new Thickness(0, 2, 0, 0));
            AutomationProperties.SetName(line, $"{host.HostId}: {detail}");
            AutomationProperties.SetAutomationId(line, $"HostChoice-{job}-{host.HostId}");
            text.Children.Add(line);
            var use = PageButton(owner == host.HostId ? again ?? "In use" : "Use it",
                () => assign("host:" + host.HostId).Forget(), primary: owner != host.HostId && cannot is null, id: $"SetupUseHost-{job}-{host.HostId}");
            use.IsEnabled = cannot is null && !(owner == host.HostId && again is null);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (model is not null && check?.Reachable == true && ChangesRolesOn(host))
            {
                var settings = PageButton(change, () => LaunchOnHost(host, HostAction.Change(roleKind)), id: $"SetupChangeHost-{job}-{host.HostId}");
                AutomationProperties.SetName(settings, $"{change}: {engine} on {host.HostId} (now {model})");
                settings.Margin = new Thickness(0, 0, 8, 0);
                buttons.Children.Add(settings);
            }
            buttons.Children.Add(use);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            DockPanel.SetDock(buttons, Dock.Right);
            row.Children.Add(buttons);
            row.Children.Add(text);
            stack.Add(row);
        }
        if (unable.Length > 0)
        {
            var why = Note(string.Join(" ", unable.Select(h => $"{h.HostId} can't run {engine}: {HostCan(h.HostId, roleKind)!.Reason}")),
                new Thickness(0, 0, 0, 8));
            AutomationProperties.SetAutomationId(why, "HostChoicesUnable-" + job);
            stack.Add(why);
        }
        // Hosts friends share with this PC: this PC only, never your network's choice; their engines are their owners'.
        if (sharedHosts.Count > 0) stack.Add(Heading("Shared with this PC"));
        foreach (var host in sharedHosts)
        {
            var check = hostChecks.GetValueOrDefault(host.HostId);
            var offers = check?.Offers?.Keys.Any(k => k == roleKind || HostRoles.Speaks(roleKind) && HostRoles.Speaks(k)) == true;
            var detail = owner == host.HostId ? ownerDetail + " A friend shares it with this PC."
                : offers ? $"A friend shares it with this PC. It offers {engine}."
                : check?.Reachable == true ? $"A friend shares it with this PC, but it doesn't offer {engine}. Only its owner can add it."
                : check?.Reachable == false ? "A friend shares it with this PC. Not reachable right now." : "A friend shares it with this PC.";
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = host.HostId, FontSize = 15, FontWeight = FontWeights.SemiBold });
            var line = Note(detail, new Thickness(0, 2, 0, 0));
            AutomationProperties.SetName(line, $"{host.HostId}: {detail}");
            AutomationProperties.SetAutomationId(line, $"HostChoice-{job}-{host.HostId}");
            text.Children.Add(line);
            var use = PageButton(owner == host.HostId ? again ?? "In use" : "Use it on this PC",
                () => assign("host:" + host.HostId).Forget(), id: $"SetupUseHost-{job}-{host.HostId}");
            var inUse = owner == host.HostId && again is null;
            use.IsEnabled = !inUse && (offers || check?.Reachable != true);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            DockPanel.SetDock(use, Dock.Right);
            row.Children.Add(use);
            row.Children.Add(text);
            stack.Add(row);
        }
        stack.Add(Row(
            PageButton("Add a computer", () => RunNodeAction(NodeAction.AddComputer), primary: hosts.Length == 0, id: "SetupAddComputer-" + job),
            paired.Length == 0 ? null : PageButton("Check hosts", () => RunNodeAction(NodeAction.CheckHost), id: "SetupCheckHosts-" + job),
            PageButton("Open Devices", () => Navigate(NavDevices), id: "SetupOpenMap-" + job)));
        stack.Add(Note(disclosure, new Thickness(0, 8, 0, 0)));
        return Card([.. stack]);
    }

    // ---------- cloud provider ----------

    /// <summary>A cloud provider for a job, as an option picker (<c>Picker-Cloud&lt;page&gt;-&lt;provider&gt;</c>: "openai",
    /// "nvidia-build", "openrouter", "google-gemini", "custom"): each row says what it costs and how soon it answers; the
    /// shown provider's details hold its model, key, consent and Use button. Voice's list is the cloud voices: OpenAI's voice
    /// and ElevenLabs with a voice cloned from yours. A job with one provider (Listening) shows only its details.</summary>
    private Border CloudCard(CompanionTab section, HostJob job, SetupRoute? route)
    {
        var role = job.Role;
        var cloudRoute = route is not null && (route.RouteType is null or SetupRouteType.OpenAi ||
            route.RouteType == SetupRouteType.ChatCompletions && !IsLocalOllama(route) && !IsLocalServer(route)) ? route : null;
        var id = "Cloud" + section;
        IReadOnlyList<CloudProvider> providers = role == SetupRole.Llm ? ThinkingProviders : [OpenAiCloud];
        var saved = cloudRoute is null ? null
            : cloudRoute.RouteType == SetupRouteType.ChatCompletions ? providers.FirstOrDefault(p => p.BaseUrl == cloudRoute.Origin) ?? CustomCloud
            : OpenAiCloud;
        // Add your key (FreeKeyPrompt): NVIDIA Build's free keys, ready for the one the owner pastes.
        if (freeKeyFocus && freeKeyPreset == FreeKeyUse.Thinking && role == SetupRole.Llm && cloudRoute is null)
            pickerShown[id] = ChatCompletionsEndpointCatalog.NvidiaBuildId;

        if (role == SetupRole.Tts)
        {
            var voices = JobOptions.CloudVoices(saved is not null, route?.RouteType == SetupRouteType.ElevenLabs)
                .Select(option => option with
                {
                    Details = option.Key == SpeechEngines.ElevenLabs.Key ? () => ElevenLabsFields(route) : () => CloudFields(section, job, cloudRoute, OpenAiCloud)
                }).ToList();
            return OptionPicker(id, "Cloud voice", null, voices);
        }
        var options = JobOptions.Providers(providers, role == SetupRole.Llm ? Martlet.Core.Planning.PlanComponent.Thinking : Martlet.Core.Planning.PlanComponent.Listening,
                saved is null ? null : JobOptions.ProviderKey(saved), Lowered(job.Sent), role == SetupRole.Llm ? ChatCompletionsEndpointCatalog.NvidiaBuildId : null)
            .Select(option => option with { Details = () => CloudFields(section, job, cloudRoute, providers.First(p => JobOptions.ProviderKey(p) == option.Key)) })
            .ToList();
        return OptionPicker(id, "Cloud provider", null, options, rows: 3);
    }

    private static string Lowered(string text) => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];

    /// <summary>The fields of cloud provider <paramref name="p"/> for a job: its address (a custom server), model (and voice),
    /// key, the consent box and its Use button. The automation IDs are the page's (<c>SetupCloudModel-&lt;page&gt;</c>,
    /// <c>SetupCloudKey-&lt;page&gt;</c>, <c>SetupCloudConsent-&lt;page&gt;</c>, <c>SetupCloudSave-&lt;page&gt;</c>...).</summary>
    private IEnumerable<UIElement> CloudFields(CompanionTab section, HostJob job, SetupRoute? cloudRoute, CloudProvider p)
    {
        var role = job.Role;
        var baseUrl = new TextBox { MaxLength = 2048, Width = 420, HorizontalAlignment = HorizontalAlignment.Left,
            Text = cloudRoute?.RouteType == SetupRouteType.ChatCompletions ? cloudRoute.Origin : "" };
        AutomationProperties.SetAutomationId(baseUrl, "SetupCloudBaseUrl");

        var model = new ComboBox { MinHeight = 30, MaxWidth = 420, MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(model, "Model");
        AutomationProperties.SetAutomationId(model, "SetupCloudModel-" + section);
        var modelText = new TextBox { MaxLength = 128, Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(modelText, "Model ID");
        AutomationProperties.SetAutomationId(modelText, "SetupCloudModelId-" + section);
        var voice = new ComboBox { ItemsSource = OpenAiSpeechSynthesisCatalog.SupportedVoices, MinHeight = 30, MaxWidth = 420, MinWidth = 300,
            HorizontalAlignment = HorizontalAlignment.Left, SelectedItem = cloudRoute?.VoiceId is { } v && OpenAiSpeechSynthesisCatalog.SupportsVoice(v) ? v : OpenAiSpeechSynthesisCatalog.DefaultVoice };
        AutomationProperties.SetName(voice, "Voice");
        AutomationProperties.SetAutomationId(voice, "SetupCloudVoice");

        var key = new PasswordBox { MaxLength = SecretLease.MaximumLength, Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(key, "API key");
        AutomationProperties.SetAutomationId(key, "SetupCloudKey-" + section);
        if (freeKeyFocus && freeKeyPreset == FreeKeyUse.Thinking && role == SetupRole.Llm)
        {
            freeKeyFocus = false;
            FocusWhenShown(key);
        }
        var keySavedMark = new TextBlock
        {
            Text = "••••••••  Key saved (type to replace)", IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0), Opacity = 0.7
        };
        keySavedMark.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        var keyField = new Grid { Width = 420, HorizontalAlignment = HorizontalAlignment.Left, Children = { key, keySavedMark } };
        var consent = new CheckBox { Margin = new Thickness(0, 8, 0, 6) };
        AutomationProperties.SetAutomationId(consent, "SetupCloudConsent-" + section);

        string? ChatUrl() => p.Chat ? p.BaseUrl is { Length: > 0 } fixedUrl ? fixedUrl : baseUrl.Text.Trim() : null;
        bool SameAsSaved() => cloudRoute is not null && (p.Chat
            ? cloudRoute.RouteType == SetupRouteType.ChatCompletions && cloudRoute.Origin == ChatUrl()
            : cloudRoute.RouteType is null or SetupRouteType.OpenAi);
        // A key this job used with the provider before, set aside when it switched away, is used again.
        bool SetAside() => !(SameAsSaved() && cloudRoute!.CredentialId is not null) &&
            SetupSettings.SetAsideCredentials(homeSettings, role, ChatUrl()).Count > 0;
        bool HasKey() => SameAsSaved() && cloudRoute!.CredentialId is not null || SetAside();
        var defaultModel = p.Chat ? p.DefaultModel : role switch
        {
            SetupRole.Llm => OpenAiTextGenerationCatalog.DefaultModelId,
            SetupRole.Stt => OpenAiTranscriptionCatalog.DefaultModelId,
            _ => OpenAiSpeechSynthesisCatalog.DefaultModelId
        };
        IReadOnlyList<string> catalog = p.Chat ? (p.DefaultModel is { } d ? [d] : []) : role switch
        {
            SetupRole.Llm => OpenAiTextGenerationCatalog.SupportedModelIds,
            SetupRole.Stt => OpenAiTranscriptionCatalog.SupportedModelIds,
            _ => OpenAiSpeechSynthesisCatalog.SupportedModelIds
        };
        var value = SameAsSaved() ? cloudRoute!.ModelId : defaultModel ?? "";
        if (p.Chat) modelText.Text = value;
        else
        {
            model.ItemsSource = catalog;
            model.SelectedItem = catalog.Contains(value, StringComparer.Ordinal) ? value : defaultModel;
        }
        var keySaved = HasKey();
        void RefreshKeyMark()
        {
            using var entered = key.SecurePassword;
            keySavedMark.Visibility = keySaved && entered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        RefreshKeyMark();
        var keyStatus = Note(SetAside() ? $"Your {p.Name} key from before is still saved. Leave this empty to use it again, or paste a new key."
                : keySaved ? $"Your {p.Name} key is saved. Leave this empty to keep it, or paste a new key."
                : p.BaseUrl == ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl
                    ? $"Paste your {p.Name} API key (it starts with nvapi-). Martlet saves it in Windows Credential Manager."
                : p.NeedsKey ? $"Paste your {p.Name} API key. Martlet saves it in Windows Credential Manager."
                : "Add a key only if your server needs one.", new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(keyStatus, "SetupCloudKeyStatus-" + section);
        var retired = SameAsSaved() && p.Chat ? ChatCompletionsEndpointCatalog.RetiredOn(p.BaseUrl, cloudRoute!.ModelId) : null;
        // The recommended model is in the provider's summary above and prefilled; the hint adds only what it doesn't say.
        var hint = Note(((retired is null ? "" : $"{retired.Name} no longer supports {cloudRoute!.ModelId}. Choose another model. ") +
            (p == OpenAiCloud || p.BaseUrl == ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl ? ""
            : p.BaseUrl == ChatCompletionsEndpointCatalog.OpenRouterBaseUrl ? "Any exact OpenRouter model ID works."
            : ChatCompletionsEndpointCatalog.Named(p.BaseUrl)?.Guidance is { } guidance ? guidance
            : "Use the server's HTTPS base URL and enter the exact model ID. For a model app on this PC (LM Studio, llama.cpp and " +
                "others), choose This PC › Model app.")).Trim(), new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(hint, "SetupCloudHint-" + section);
        if (hint.Text.Length == 0) hint.Visibility = Visibility.Collapsed;
        consent.Content = new TextBlock { TextWrapping = TextWrapping.Wrap,
            Text = $"I choose {p.Name} for {job.Job}. {job.Sent} will be sent there" +
                (FreeKeyPrompt.IsFree(p.BaseUrl) ? ". " : ", and requests may cost money. ") + OpenAiSetup.Boundary(role) };
        consent.IsChecked = SameAsSaved() && cloudRoute!.Consent is not null;
        modelText.TextChanged += (_, _) => { if (!modelText.IsKeyboardFocusWithin) return; tabEdited = true; consent.IsChecked = false; };
        model.SelectionChanged += (_, _) => { tabEdited = true; consent.IsChecked = false; };
        baseUrl.TextChanged += (_, _) => { tabEdited = true; consent.IsChecked = false; keySaved = HasKey(); RefreshKeyMark(); };
        voice.SelectionChanged += (_, _) => { tabEdited = true; consent.IsChecked = false; };
        key.PasswordChanged += (_, _) => { tabEdited = true; RefreshKeyMark(); };

        if (role == SetupRole.Tts) yield return AbilitiesLine("openai", VoiceAbilities.OpenAiVoice, []);
        if (p == CustomCloud)
        {
            yield return new Label { Content = "API _base URL (without /chat/completions)", Target = baseUrl, Padding = new Thickness(0, 6, 0, 3) };
            yield return baseUrl;
        }
        Control modelControl = p.Chat ? modelText : model;
        yield return new Label { Content = "_Model", Target = modelControl, Padding = new Thickness(0, 6, 0, 3) };
        yield return modelControl;
        yield return hint;
        if (role == SetupRole.Tts)
        {
            yield return new Label { Content = "_Voice", Target = voice, Padding = new Thickness(0, 6, 0, 3) };
            yield return voice;
        }
        yield return new Label { Target = key, Padding = new Thickness(0, 6, 0, 3),
            Content = p == CustomCloud ? "API _key (only if the server needs one)" : $"Your {p.Name} _key" };
        yield return keyField;
        yield return keyStatus;
        if (p.BaseUrl == ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl && !keySaved)
        {
            var getKey = PageButton(FreeKeyPrompt.GetLabel, () => OpenKeyPageFrom(keyStatus), link: true, id: "SetupCloudGetKey-" + section);
            getKey.HorizontalAlignment = HorizontalAlignment.Left;
            yield return getKey;
        }
        // What leaves this PC and what it costs are in the facts above (their help adds OpenAiSetup.Disclosure) and in the box.
        consent.ToolTip = OpenAiSetup.Disclosure;
        yield return consent;
        // A cloud provider is a commitment (a key, data sent elsewhere, possible costs), so it stays an explicit action named for
        // what it does rather than an automatic save.
        yield return Row(PageButton(p == CustomCloud ? "Use this server" : $"Use {p.Name}",
            () => SaveCloudAsync(job, p, baseUrl.Text.Trim(), p.Chat ? modelText.Text.Trim() : model.SelectedItem as string ?? "",
                role == SetupRole.Tts ? voice.SelectedItem as string : null, key, consent.IsChecked == true).Forget(),
            primary: true, id: "SetupCloudSave-" + section));
    }
    private async Task SaveCloudAsync(HostJob job, CloudProvider provider, string baseUrl, string model, string? voice, PasswordBox keyBox, bool consent)
    {
        if (!consent)
        {
            ActionText.Text = $"Tick the box to confirm {provider.Name} for {job.Job}, then press Use.";
            return;
        }
        var role = job.Role;
        // A voice engine Speaking leaves on a host stops there once the cloud voice is saved (asked first, before the key is taken).
        var leaving = role == SetupRole.Tts ? LeavingEngine(null, null) : null;
        if (leaving is not null && !ConfirmationDialog.Confirm(this, $"Use {provider.Name} for {job.Job}?" + LeavingNote(leaving),
                $"Use {provider.Name}"))
            return;
        var url = provider.Chat ? provider.BaseUrl is { Length: > 0 } fixedUrl ? fixedUrl : baseUrl : null;
        SecretLease? key = null;
        try
        {
            using (var entered = keyBox.SecurePassword)
                if (entered.Length > 0) key = TakeKey(keyBox);
            if (!provider.Chat)
            {
                var supported = role switch
                {
                    SetupRole.Llm => OpenAiTextGenerationCatalog.SupportedModelIds,
                    SetupRole.Stt => OpenAiTranscriptionCatalog.SupportedModelIds,
                    _ => OpenAiSpeechSynthesisCatalog.SupportedModelIds
                };
                if (!supported.Contains(model, StringComparer.Ordinal))
                    throw new ContractException(ErrorCode.ProviderCapability, $"Choose one of the listed OpenAI models: {string.Join(", ", supported)}.");
                if (role == SetupRole.Tts && !OpenAiSpeechSynthesisCatalog.SupportsVoice(voice))
                    throw new ContractException(ErrorCode.ProviderCapability, "Choose one of the listed voices.");
            }
            var route = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == role);
            var keySaved = route?.CredentialId is not null && (provider.Chat
                ? route.RouteType == SetupRouteType.ChatCompletions && route.Origin == url
                : route.RouteType is null or SetupRouteType.OpenAi) || SetupSettings.SetAsideCredentials(homeSettings, role, url).Count > 0;
            var missingKey = $"Paste your {provider.Name} API key first.";
            if (key is null && provider.NeedsKey && !keySaved)
                throw new ContractException(ErrorCode.InvalidContract, missingKey);
            if (!await SaveSectionRouteAsync(job, settings => provider.Chat
                    ? ChatCompletionsSetup.SelectRoute(settings, url!, model)
                    : SetupSettings.SelectRoute(settings, role, model, role == SetupRole.Tts ? voice : null),
                key, $"{job.Title} now uses {provider.Name} ({model}{(voice is null ? "" : ", voice " + voice)}).{(key is null ? "" : " Your API key is saved in Windows Credential Manager.")}" +
                    (FreeKeyPrompt.IsFree(url) ? "" : " Requests may cost money there."),
                provider.NeedsKey ? missingKey : null))
                return;
            // A new Thinking model: ask its server how much context it takes, so replies stay within it.
            if (!closing && role == SetupRole.Llm && provider.Chat &&
                homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm) is { } chosen && chosen.Origin == url && chosen.ModelId == model)
                CheckNewModelContextAsync().Forget();
            if (!closing && leaving is not null) ActionText.Text += await StopLeftVoiceEngineAsync(leaving);
        }
        catch (ContractException error) { ActionText.Text = error.Message; }
        finally { key?.Dispose(); }
    }

    private static SecretLease TakeKey(PasswordBox box)
    {
        using var secure = box.SecurePassword;
        var chars = new char[secure.Length];
        var pointer = Marshal.SecureStringToGlobalAllocUnicode(secure);
        try
        {
            Marshal.Copy(pointer, chars, 0, chars.Length);
            return new SecretLease(chars);
        }
        finally
        {
            box.Clear();
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(chars.AsSpan()));
            Marshal.ZeroFreeGlobalAllocUnicode(pointer);
        }
    }

    /// <summary>Saves a job's route chosen on its Companion tab: the route, then its key (which resets consent), then the user's
    /// confirmed choice. A key the route no longer uses is set aside (listed under Keys from before), and a key set aside
    /// earlier for the chosen destination is used again, so switching providers never waits on old keys. With
    /// <paramref name="missingKey"/>, a route that ends without a key is refused with that message. Returns whether it saved.
    /// A change still saving goes first (<see cref="ChangeTurns"/>); installs, runs and replies in progress don't hold it up.</summary>
    private async Task<bool> SaveSectionRouteAsync(HostJob job, Func<AppSettings, AppSettings> select, SecretLease? key, string done,
        string? missingKey = null)
    {
        if (store is null || setupService is null || closing) return false;
        ChangeTurns.Turn? turn = null;
        var role = job.Role;
        var token = lifetime.Token;
        try
        {
            turn = await ChangeTurnAsync();
            var loaded = await setupService.LoadAsync(token);
            if (loaded.Error is not null) throw new InvalidOperationException(loaded.Error.Summary);
            var settings = UseModels(SetupSettings.Begin(loaded.Settings));
            var revision = loaded.Revision;
            var old = settings.Setup!.Routes.SingleOrDefault(r => r.Role == role);
            var updated = select(settings);
            var chosen = updated.Setup!.Routes.Single(r => r.Role == role);
            var reused = false;
            if (key is null && chosen is { CredentialId: null, RouteType: SetupRouteType.OpenAi or SetupRouteType.ChatCompletions or SetupRouteType.ElevenLabs })
            {
                var service = setupService;
                foreach (var setAside in chosen.RouteType == SetupRouteType.ElevenLabs
                             ? SetupSettings.SetAsideElevenLabsCredentials(updated)
                             : SetupSettings.SetAsideCredentials(updated, role, chosen.RouteType == SetupRouteType.ChatCompletions ? chosen.Origin : null))
                {
                    // Only a key still in Windows Credential Manager is used again; it is read on a worker and never shown.
                    var candidate = SetupSettings.ReattachSetAsideCredential(updated, setAside);
                    if (await Task.Run(() => service.CheckCredential(candidate, role), token) != CredentialError.None) continue;
                    updated = candidate;
                    reused = true;
                    break;
                }
            }
            if (key is null && missingKey is not null && updated.Setup!.Routes.Single(r => r.Role == role).CredentialId is null)
                throw new InvalidOperationException(missingKey);
            updated = SetupSettings.QueueReplacedCredential(updated, old);
            if (key is not null)
            {
                var staged = await setupService.SaveAsync(updated, revision, token);
                if (!staged.Save.Saved) throw new InvalidOperationException(staged.Summary);
                var service = setupService;
                var stored = await Task.Run(() => service.ReplaceCredentialAsync(staged.Settings, staged.Save.Revision, role, key, token), token);
                if (!stored.Save.Saved) throw new InvalidOperationException(stored.Summary);
                updated = stored.Settings;
                revision = stored.Save.Revision;
            }
            var route = updated.Setup!.Routes.Single(r => r.Role == role);
            var confirmed = SetupSettings.ReplaceRoute(updated, route with { Consent = route.Selection() });
            var saved = await setupService.SaveAsync(confirmed, revision, token);
            if (!saved.Save.Saved) throw new InvalidOperationException(saved.Summary);
            homeSettings = confirmed;
            FollowSavedSetup(saved.Save.Revision);
            pendingJobHosts.Remove(role);
            pendingJobVoices.Remove(role);
            RecordClusterJob(job.Job, new(null, false));
            ActionText.Text = done + (reused ? " It uses the key you saved for it before." : "") + OpenConversationFollows;
            tabPlace.Remove(openTab ?? CompanionTab.Thinking);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or JsonException)
        {
            ActionText.Text = error.Message;
            return false;
        }
        finally
        {
            turn?.Dispose();
            if (!closing)
            {
                tabEdited = false;
                RenderHome();
            }
        }
    }

    // ---------- character ----------

    /// <summary>A page's Now card: what it uses, then any problem that stops it. <paramref name="id"/> names its status text for
    /// UI Automation ("<c>id</c>" and "<c>id</c>Problem").</summary>
    private Border PageNowCard(string text, Martlet.Core.Platforms.JobCoverage? problem, string? id = null, string? warning = null)
    {
        var now = new StackPanel();
        now.Children.Add(Heading("Now"));
        var status = new TextBlock { Text = text, FontSize = 15, TextWrapping = TextWrapping.Wrap };
        if (id is not null) AutomationProperties.SetAutomationId(status, id);
        now.Children.Add(status);
        // A job's coverage problem, or another reason (such as a character that did not close cleanly) it is not working.
        if ((problem is null ? warning : $"Needs attention: {problem.Problem} {problem.Effect}") is { } line)
        {
            var problemText = new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            problemText.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            if (id is not null) AutomationProperties.SetAutomationId(problemText, id + "Problem");
            now.Children.Add(problemText);
        }
        return Card(now);
    }

    private void RenderCharacterTab(Panel page)
    {
        var showing = avatar.IsShowing;
        page.Children.Add(PageNowCard(CharacterModelName() + (homeAvatar is { } profile ? $" ({profile.Renderer})" : "") +
            (showing ? ", on your desktop." : ", hidden."), null, "SetupCharacterNow", characterCleanupProblem));

        var locked = avatar.PlacementLocked;
        // Reset stays available while hidden once a place is saved, in case the character got lost off screen.
        var resetPosition = showing || avatar.Placement is not null
            ? PageButton("Reset position", () => ResetCharacterPositionAsync().Forget(), id: "SetupCharacterResetPosition") : null;
        var modelCard = Card(Heading("Character model"),
            Note("Choose a character model, then adjust its size, position and motion. Choices save on their own and a showing character switches right away.", new Thickness(0, 0, 0, 8)),
            Row(PageButton(showing ? "Hide character" : "Show character", () => RunNodeAction(NodeAction.ToggleCharacter), primary: !showing, id: "SetupCharacterToggle"),
                PageButton("Choose and customize", () => RunNodeAction(NodeAction.Character), id: "OpenAvatar"),
                resetPosition,
                showing ? PageButton("Reset zoom", () => ResetCharacterZoomAsync().Forget(), id: "SetupCharacterResetZoom") : null,
                // Unlocking is only here and on Home, never on the character itself.
                showing || locked ? PageButton(locked ? "Unlock position" : "Lock position",
                    () => SetCharacterLockAsync(!avatar.PlacementLocked).Forget(), id: "SetupCharacterLock") : null,
                // The same goes for click-through: the mouse can't reach the character to turn it off.
                showing || avatar.ClickThrough ? PageButton(avatar.ClickThrough ? "Turn off click-through" : "Turn on click-through",
                    () => SetCharacterClickThroughAsync(!avatar.ClickThrough).Forget(), id: "SetupCharacterClickThrough") : null));
        if (modelCard.Child is Panel modelPanel)
        {
            var placementNote = characterPlacementNote = Note(CharacterPlacementText(), new Thickness(0, 4, 0, 0));
            AutomationProperties.SetAutomationId(placementNote, "SetupCharacterPlacement");
            modelPanel.Children.Add(placementNote);
            var clickThroughNote = Note(avatar.ClickThrough
                ? "Click-through is on: clicks pass through the character to the windows under it, so you can't drag, zoom or right-click it. Turn it off here, on Home or from Martlet's icon in the notification area."
                : "Click-through is off: the character catches clicks. Turn it on here or from the character's right-click menu to let clicks pass through it, for example while you play a game.",
                new Thickness(0, 4, 0, 0));
            AutomationProperties.SetAutomationId(clickThroughNote, "SetupCharacterClickThroughNote");
            modelPanel.Children.Add(clickThroughNote);
        }
        // What the showing model drives: textures (and any downscaling), blinking, mouth, motions and physics.
        if (showing && avatar.Capabilities is { } loaded && modelCard.Child is Panel modelStack)
        {
            var modelNote = Note("Model: " + AvatarRendererProcess.Describe(loaded), new Thickness(0, 4, 0, 0));
            AutomationProperties.SetAutomationId(modelNote, "SetupCharacterModel");
            modelStack.Children.Add(modelNote);
        }
        page.Children.Add(modelCard);
        characterViewText = null;
        if (showing)
        {
            // Wheel zoom happens on the overlay itself; Martlet reads the overlay's view whenever this page renders.
            characterViewText = Note("", new Thickness(0, 8, 0, 0));
            AutomationProperties.SetAutomationId(characterViewText, "SetupCharacterView");
            page.Children.Add(Card(Heading("Zoom"),
                Note("Use the mouse wheel over the character to zoom. Ctrl+drag or middle-drag pans when zoomed in.", new Thickness(0, 0, 0, 0)),
                Row(PageButton("Zoom in", () => ZoomCharacterAsync("in").Forget(), id: "SetupCharacterZoomIn"),
                    PageButton("Zoom out", () => ZoomCharacterAsync("out").Forget(), id: "SetupCharacterZoomOut")),
                characterViewText));
            ZoomCharacterAsync("status").Forget();
        }
        page.Children.Add(CharacterModelsCard());
        page.Children.Add(ProfilesLinkCard());
    }

    /// <summary>Companion › Eyes: Now, where the character looks, then where its eyes are (for the emotes drawn over them).</summary>
    private void RenderEyesTab(Panel page)
    {
        FollowCharacterActions();
        page.Children.Add(CharacterGazeCard());
        page.Children.Add(CharacterEyesCard());
        page.Children.Insert(0, EyesNowCard());
    }

    /// <summary>Companion › Touch: Now, the shown model's touch zones, then the active persona's touch temperament (which joins
    /// with the zones' last rows on a page just opened).</summary>
    private void RenderTouchTab(Panel page)
    {
        FollowCharacterActions();
        page.Children.Add(CharacterTouchZonesCard());
        page.Children.Insert(0, TouchNowCard());
        AddRow(page, CharacterTemperamentCard());
        page.Children.Add(CharacterReactionChangesCard());
    }

    private ComboBox? bubblePlacementChoice;
    private TextBox? bubbleOffsetX, bubbleOffsetY;
    private TextBlock? speechDisplayText;
    private Button? speechPreviewButton;

    /// <summary>Companion › Speech bubbles' configuration: where the bubble goes, following the character's head (default) or
    /// staying in one place, nudged by horizontal and vertical offsets; the same saved choices as the character settings window,
    /// applied from Martlet's next sentence. Whether bubbles and subtitles show at all is the page's main choice
    /// (<see cref="SpeechDisplayChoiceCard"/>).</summary>
    private Border SpeechDisplayCard()
    {
        var prefs = captions.Preferences;
        bubblePlacementChoice = new ComboBox
        {
            ItemsSource = new[] { BubbleFollows, BubbleStays }, MinHeight = 30, MinWidth = 260, MaxWidth = 420,
            HorizontalAlignment = HorizontalAlignment.Left, SelectedItem = prefs.StaticBubble ? BubbleStays : BubbleFollows
        };
        AutomationProperties.SetName(bubblePlacementChoice, "Bubble position");
        AutomationProperties.SetAutomationId(bubblePlacementChoice, "SetupCharacterBubblePlacement");
        bubblePlacementChoice.SelectionChanged += (_, _) => SaveSpeechDisplay();
        TextBox Offset(string name, string id, double value)
        {
            var box = new TextBox { Width = 90, MaxLength = 6, Text = value.ToString("0.##", CultureInfo.CurrentCulture) };
            AutomationProperties.SetName(box, name);
            AutomationProperties.SetAutomationId(box, id);
            box.TextChanged += (_, _) => SaveSpeechDisplay();
            return box;
        }
        bubbleOffsetX = Offset("Bubble horizontal offset", "SetupCharacterBubbleOffsetX", prefs.BubbleOffsetX);
        bubbleOffsetY = Offset("Bubble vertical offset", "SetupCharacterBubbleOffsetY", prefs.BubbleOffsetY);
        var offsets = new WrapPanel
        {
            Margin = new Thickness(0, 8, 0, 0),
            Children =
            {
                new Label { Content = "_Horizontal offset", Target = bubbleOffsetX, Padding = new Thickness(0, 4, 8, 4) }, bubbleOffsetX,
                new Label { Content = "_Vertical offset", Target = bubbleOffsetY, Padding = new Thickness(18, 4, 8, 4) }, bubbleOffsetY
            }
        };

        speechDisplayText = Note("", new Thickness(0, 8, 0, 0));
        AutomationProperties.SetAutomationId(speechDisplayText, "SetupCharacterSpeechDisplay");
        speechPreviewButton = PageButton("Preview a speech bubble", () => PreviewSpeechBubbleAsync().Forget(), id: "SetupCharacterPreviewBubble");
        ShowSpeechDisplay();
        return Card(Heading("Where the bubble goes"),
            Note("Speech bubbles show Martlet's spoken words beside the character; subtitles show them at the bottom of the active screen.",
                new Thickness(0, 0, 0, 0)),
            new Label { Content = "Bubble _position", Target = bubblePlacementChoice, Padding = new Thickness(0, 12, 0, 4) },
            bubblePlacementChoice, offsets,
            Note("Following the character, the bubble sits beside its head on whichever side has room, and keeps up as you move or zoom; " +
                "the offsets nudge it (in pixels, positive is right and down). Staying in one place, the offsets are measured from the " +
                "top-left of the character's screen.", new Thickness(0, 6, 0, 0)),
            speechDisplayText, Row(speechPreviewButton));
    }

    private const string BubbleFollows = "Follows the character", BubbleStays = "Stays in one place";

    private void SaveSpeechDisplay()
    {
        if (showingSpeechDisplay || bubblePlacementChoice is null) return;
        var prefs = captions.Preferences with { StaticBubble = ReferenceEquals(bubblePlacementChoice.SelectedItem, BubbleStays) };
        static double? Read(TextBox? box) =>
            box is null ? null
            : double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var value) &&
              double.IsFinite(value) && Math.Abs(value) <= SpeechDisplayPreferences.MaximumOffset ? value
            : double.NaN;
        var x = Read(bubbleOffsetX);
        var y = Read(bubbleOffsetY);
        if (x is double.NaN || y is double.NaN)
        {
            // Leave the half-typed text alone (such as a lone "-"); just say what's expected.
            if (speechDisplayText is not null)
                speechDisplayText.Text = $"Offsets must be numbers of pixels from -{SpeechDisplayPreferences.MaximumOffset:0} to {SpeechDisplayPreferences.MaximumOffset:0}.";
            return;
        }
        prefs = prefs with { BubbleOffsetX = x ?? prefs.BubbleOffsetX, BubbleOffsetY = y ?? prefs.BubbleOffsetY };
        var saved = captions.Update(prefs);
        if (!saved) ShowSpeechDisplay("Applied for now, but couldn't save your speech display settings.");
    }

    private bool showingSpeechDisplay;

    /// <summary>Follows the saved choices, whichever window changed them.</summary>
    private void ShowSpeechDisplay(string? outcome = null)
    {
        if (speechDisplayText is null) return;
        var prefs = captions.Preferences;
        showingSpeechDisplay = true;
        try
        {
            ShowSpeechDisplayChoice(prefs);
            if (bubblePlacementChoice is not null) bubblePlacementChoice.SelectedItem = prefs.StaticBubble ? BubbleStays : BubbleFollows;
            // Only rewrite an offset box when its number differs, so typing in it isn't interrupted.
            foreach (var (box, value) in new[] { (bubbleOffsetX, prefs.BubbleOffsetX), (bubbleOffsetY, prefs.BubbleOffsetY) })
                if (box is not null && !(double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var shown) && shown == value))
                    box.Text = value.ToString("0.##", CultureInfo.CurrentCulture);
        }
        finally { showingSpeechDisplay = false; }
        if (speechPreviewButton is not null) speechPreviewButton.IsEnabled = avatar.IsShowing && prefs.SpeechBubbles;
        var bubbles = !prefs.SpeechBubbles ? "Speech bubbles are off."
            : avatar.IsShowing ? "Speech bubbles are on."
            : "Speech bubbles will appear when the character is showing.";
        var position = prefs.StaticBubble
            ? $" Bubbles stay in one place, {prefs.BubbleOffsetX:0.##}, {prefs.BubbleOffsetY:0.##} from the top-left of the character's screen."
            : $" Bubbles follow the character, offset by {prefs.BubbleOffsetX:0.##}, {prefs.BubbleOffsetY:0.##}.";
        speechDisplayText.Text = $"{bubbles} Subtitles are {(prefs.Subtitles ? "on" : "off")}." + position + (outcome is null ? "" : " " + outcome);
    }

    private async Task PreviewSpeechBubbleAsync()
    {
        var shown = await captions.PreviewAsync("Hi! While I talk, what I say shows up here.");
        if (closing) return;
        ShowSpeechDisplay(shown is null ? "Couldn't show the preview. Show the character and try again."
            : $"Preview shown {BubbleWhere(shown)}" +
              $"{BubbleTheme(shown, SelectedTheme.Name(), key => TryFindResource(key) as System.Windows.Media.Brush, SystemParameters.HighContrast)}.");
    }

    /// <summary>Whether the shown bubble is drawn in the palette Martlet's windows use now (<paramref name="brush"/> by resource
    /// key): its fill, outline, text and halo are the palette's Surface, Accent, Text and Glow (no halo in high contrast).
    /// Empty when the overlay didn't say which colors it used.</summary>
    internal static string BubbleTheme(RendererBubble bubble, string palette, Func<string, System.Windows.Media.Brush?> brush, bool highContrast)
    {
        if (bubble.Colors is not { } drawn) return "";
        var differing = new List<string>();
        void Check(string part, string? actual, string? role)
        {
            var expected = role is null ? null
                : brush(role + "Brush") is System.Windows.Media.SolidColorBrush solid ? $"#{solid.Color.R:X2}{solid.Color.G:X2}{solid.Color.B:X2}" : "";
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) differing.Add($"{part} {actual ?? "none"}");
        }
        Check("fill", drawn.Fill, "Surface");
        Check("outline", drawn.Outline, "Accent");
        Check("text", drawn.Text, "Text");
        Check("halo", drawn.Halo, highContrast ? null : "Glow");
        return differing.Count == 0 ? $", in the {palette} colors" : $", but not in the {palette} colors ({string.Join(", ", differing)})";
    }

    internal static string BubbleWhere(RendererBubble bubble) => bubble.Placement switch
    {
        "left" => "to the left of the character's head",
        "right" => "to the right of the character's head",
        "above" => "above the character's head",
        "static" => "in its fixed place",
        "hidden" => "hidden",
        _ => "beside the character"
    } + (bubble.Width > 0 ? $" at {bubble.Left:0}, {bubble.Top:0} ({bubble.Width:0} × {bubble.Height:0})" : "") + (bubble.TextFits switch
    {
        true => ", holding all its text",
        false => ", but its text doesn't fit inside it",
        null => ""
    });

    // ---------- personality ----------

    private void RenderPersonalityTab(Panel page)
    {
        var companion = homeSettings?.Companion;
        var persona = companion?.ActivePersona;
        var count = companion?.Personas.Count ?? 1;
        page.Children.Add(PageNowCard(persona is null
            ? "Default persona."
            : $"{persona.Name}{(count > 1 ? $", one of {count} personas" : "")}.", null));

        page.Children.Add(Card(Heading("Personas"),
            Note("Create, edit or switch personas. Changes save on their own, and the next message uses the chosen persona.", new Thickness(0, 0, 0, 8)),
            Row(PageButton("Edit personality", () => Companion_Click(this, new RoutedEventArgs()), primary: true, id: "OpenCompanion"))));

        page.Children.Add(ProfilesLinkCard());

        page.Children.Add(Card(Heading("Character cards"),
            Note("Import a PNG, JSON or CHARX character card to create a persona.", new Thickness(0, 0, 0, 8)),
            Row(PageButton("Import a character card", () => OpenCompanionWindowAsync(importCard: true).Forget(), id: "ImportCharacterCard"))));

        page.Children.Add(Card(Heading("Prompts"),
            Note("The persona is wrapped in Martlet's own instructions, along with lore, memory, reply length and more. See and edit every one of them.",
                new Thickness(0, 0, 0, 8)),
            Row(PageButton("Edit prompts", () => OpenCompanion(CompanionTab.Prompts), id: "OpenPrompts"))));
    }

    // ---------- lip-sync: where it runs, like every job ----------

    /// <summary>Who moves the character's mouth, chosen like every job: this PC (Audio2Face in its host service, or simply the
    /// voice's loudness) or another of your computers. It switches right away, even while the character talks.</summary>
    private void RenderLipSyncTab(Panel page)
    {
        var thisPc = ThisPcHost();
        var handler = NetworkMap.LipSync(homeAvatar);
        var owner = handler == LipSyncHandler.Host ? homeAvatar!.RemoteHost!.HostId : null;
        var ownerMissing = owner is not null && HostServes(owner, ClusterJobs.LipSync, HostRoles.Get(HostRoles.Audio2Face).RouteId) == false;
        page.Children.Add(PageNowCard(handler switch
        {
            LipSyncHandler.Loudness => "Voice loudness: the mouth opens and closes with the voice. Audio2Face is off.",
            LipSyncHandler.Host => $"Audio2Face on {(owner == thisPc?.HostId ? "this PC" : owner)}" +
                (ownerMissing ? ". Not installed yet, so voice loudness is used for now." : "."),
            _ when homeAvatar?.LipSync == AvatarLipSync.Audio2Face => $"Your own Audio2Face service on this PC ({OwnLipSyncEndpoint().Authority}), " +
                "with Audio2Face-only lip-sync activated in the character's settings.",
            _ => ownLipSyncAnswers switch
            {
                true => $"Your own Audio2Face service is answering at {OwnLipSyncEndpoint().Authority}.",
                false => "Voice loudness: Audio2Face isn't running on this PC.",
                _ => $"Voice loudness, or Audio2Face at {OwnLipSyncEndpoint().Authority} when it is running."
            }
        }, coverage.FirstOrDefault(c => c.Job == ClusterJobs.LipSync && c.IsProblem), "LipSyncNow"));

        // Voice loudness is worked out on this PC, so it is one of this PC's ways rather than a place of its own.
        var current = handler == LipSyncHandler.Host && owner != thisPc?.HostId ? JobPlace.Computer : JobPlace.ThisPc;
        var place = tabPlace.TryGetValue(CompanionTab.LipSync, out var chosen) ? chosen : current;
        var gpu = machine.BestGpu;
        var fits = gpu is { IsNvidia: true } && (gpu.MemoryGb ?? 0) >= 4;
        var otherHosts = NetworkMap.Hosts(Inputs()).Where(h => h.HostId != thisPc?.HostId).ToArray();
        var recommended = !fits && otherHosts.Any(h => CannotHand(h.HostId, HostRoles.Audio2Face, ClusterJobs.LipSync) is null)
            ? JobPlace.Computer : JobPlace.ThisPc;
        string Label(JobPlace value, string label) => value == recommended ? label + " (recommended)" : label;

        page.Children.Add(WhereItRunsCard(CompanionTab.LipSync, "LipSync", "Where it runs", null, current, place,
            (JobPlace.ThisPc, Label(JobPlace.ThisPc, "This PC"),
                "Audio2Face on this PC's NVIDIA graphics card, or voice loudness with no setup. Voice audio stays here."),
            (JobPlace.Computer, Label(JobPlace.Computer, "Another of your computers"),
                "Audio2Face on a paired computer's NVIDIA graphics card, so this PC stays free.")));

        page.Children.Add(place == JobPlace.Computer
            ? ComputersCard(ClusterJobs.LipSync, "Audio2Face", HostRoles.Audio2Face, owner,
                ownerMissing ? "Audio2Face isn't installed there yet, so voice loudness is used for now."
                    : $"In use: Audio2Face{(hostChecks.GetValueOrDefault(owner ?? "")?.Offers?.GetValueOrDefault(HostRoles.Audio2Face) is { } model ? " " + model : "")}.",
                ownerMissing ? "Install Audio2Face" : null, AssignLipSyncAsync,
                "Only Martlet's generated voice is sent to that computer. Audio2Face needs an NVIDIA graphics card; its open-source " +
                "engine needs no NVIDIA account (NVIDIA's NIM engine needs an NGC key). Until it is ready, the mouth follows voice loudness.", thisPc)
            : LocalLipSyncCard(thisPc, handler, owner, fits));
    }

    /// <summary>Lip-sync on this PC, as an option picker (<c>Picker-LipSync-&lt;key&gt;</c>): Audio2Face in Martlet's host
    /// service here (one click sets the host service up and pairs it, installs Audio2Face and hands lip-sync to it), voice
    /// loudness (no setup; advanced lip-sync off), and an Audio2Face service you run yourself, which Martlet's default looks for
    /// before each sentence. Each row says where it runs and what it takes; the shown way's details hold its state and buttons.</summary>
    private Border LocalLipSyncCard(PairedHost? thisPc, LipSyncHandler handler, string? owner, bool fits)
    {
        var gpu = machine.BestGpu;
        var dockerInUse = handler == LipSyncHandler.Host && thisPc is not null && owner == thisPc.HostId;
        // Lip-sync can be handed to this PC's host service before Audio2Face is installed there (or after it was removed).
        var notInstalled = dockerInUse && HostServes(thisPc!.HostId, ClusterJobs.LipSync, HostRoles.Get(HostRoles.Audio2Face).RouteId) == false;
        // Martlet's default (no explicit choice) only looks for your own service before each sentence, so that service is in
        // use only when one answers (or Audio2Face-only lip-sync is activated); otherwise the mouth follows the voice's loudness.
        var ownSetting = handler == LipSyncHandler.ThisPc;
        var audio2FaceOnly = homeAvatar?.LipSync == AvatarLipSync.Audio2Face;
        var ownInUse = ownSetting && (audio2FaceOnly || ownLipSyncAnswers == true);
        var loudnessInUse = handler == LipSyncHandler.Loudness || ownSetting && !ownInUse;
        var endpoint = OwnLipSyncEndpoint().Authority;
        var arm = machine.ArmRefusal("audio2face");

        IEnumerable<UIElement> DockerDetails()
        {
            var about = Note(gpu is null ? "No dedicated graphics card was found on this PC; Audio2Face needs an NVIDIA graphics card with 4 GB or more."
                : $"This PC has {gpu.Describe()}." + (fits ? "" : " Audio2Face needs an NVIDIA graphics card with 4 GB or more, so voice " +
                    "loudness or another computer suits this PC better."), new Thickness(0, 4, 0, 0));
            if (!fits) about.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            AutomationProperties.SetAutomationId(about, "LipSyncDockerAbout");
            yield return about;
            if (thisPc is null)
            {
                yield return Note((machine.DockerRunning ? "Docker Desktop is running. "
                        : machine.DockerInstalled ? "Docker Desktop is installed. Martlet can start it when needed. "
                        : "Docker Desktop isn't installed yet. Martlet installs it first. ") +
                    "Setting this up installs the local lip-sync service and Audio2Face, then uses it for lip-sync. Until it is ready, " +
                    "voice loudness is used.", new Thickness(0, 4, 0, 0));
                var setUp = Row(PageButton("Set up Audio2Face with Docker", () => SetUpThisPcHostAsync(AssignLipSyncAsync).Forget(),
                    primary: fits, id: "SetupLipSyncHostThisPc"));
                setUp.Margin = new Thickness(0, 8, 0, 0);
                yield return setUp;
                yield break;
            }
            var model = hostChecks.GetValueOrDefault(thisPc.HostId)?.Offers?.GetValueOrDefault(HostRoles.Audio2Face);
            yield return Note(notInstalled
                    ? "Audio2Face is selected but not installed yet. Install it to finish."
                : dockerInUse ? $"In use: Audio2Face on this PC{(model is null ? "" : $", model {model}")}."
                : model is not null ? "Audio2Face is ready on this PC."
                : "Audio2Face isn't ready on this PC yet. Martlet installs it when you choose Use.", new Thickness(0, 4, 0, 0));
            var use = PageButton(notInstalled ? "Install Audio2Face" : dockerInUse ? "Set up Audio2Face again" : "Use Audio2Face on this PC",
                () => AssignLipSyncAsync("host:" + thisPc.HostId).Forget(), primary: notInstalled || fits && !dockerInUse, id: "SetupLipSyncUseLocal");
            use.IsEnabled = arm is null;
            var buttons = Row(use, PageButton("Check it", () => RunNodeAction(NodeAction.CheckHost, thisPc.HostId), id: "SetupLipSyncCheckLocal"));
            buttons.Margin = new Thickness(0, 8, 0, 0);
            yield return buttons;
        }

        IEnumerable<UIElement> OwnDetails()
        {
            var state = Note(!ownSetting
                    ? $"Already run NVIDIA's Audio2Face service yourself? Martlet can use it at {endpoint}, with voice loudness whenever it doesn't answer."
                : audio2FaceOnly ? $"In use at {endpoint}, with Audio2Face-only lip-sync in the character settings."
                : ownLipSyncAnswers switch
                {
                    true => $"In use: an Audio2Face service you run yourself is answering at {endpoint}.",
                    false => $"Martlet looks for an Audio2Face service you run yourself at {endpoint} before each sentence. " +
                        "Nothing answers there, so the mouth follows voice loudness.",
                    _ => $"Martlet looks for an Audio2Face service you run yourself at {endpoint} before each sentence, " +
                        "and uses voice loudness when nothing answers."
                }, new Thickness(0, 4, 0, 0));
            AutomationProperties.SetAutomationId(state, "LipSyncOwnState");
            yield return state;
        }

        var options = JobOptions.LipSyncWays(dockerInUse, notInstalled, loudnessInUse, ownInUse, fits, arm, endpoint)
            .Select(option => option.Key switch
            {
                JobOptions.Audio2Face => option with { Details = DockerDetails },
                JobOptions.Loudness => option with
                {
                    Action = loudnessInUse ? null : () => PageButton("Use voice loudness", () => AssignLipSyncAsync("off").Forget(),
                        primary: true, id: "SetupLipSyncLoudness")
                },
                _ => option with
                {
                    Details = OwnDetails,
                    Badge = ownInUse ? JobOptions.InUse : ownSetting ? ownLipSyncAnswers == false ? "not running" : "checking" : null,
                    Action = ownSetting ? null : () => PageButton("Use my own service", () => AssignLipSyncAsync("this-pc").Forget(),
                        id: "SetupLipSyncOwnService")
                }
            }).ToList();
        return Card(Heading("Lip-sync on this PC"),
            Note("Choose how the mouth moves. Until Audio2Face is ready, the mouth follows voice loudness.", new Thickness(0, 0, 0, 6)),
            OptionPickerBody("LipSync", options));
    }
    // ---------- memory ----------

    private void RenderMemoryTab(Panel page)
    {
        var on = homeSettings?.Memory?.Enabled == true;
        page.Children.Add(Card(Heading("Now"),
            new TextBlock { Text = on ? "On" : "Off", FontSize = 15, TextWrapping = TextWrapping.Wrap },
            Note(on
                ? "Martlet remembers lasting facts you share and uses them later. Everything stays on this PC; manage facts in Manage memory."
                : "Martlet doesn't remember or recall anything between conversations. Turn memory on in Manage memory.",
                new Thickness(0, 2, 0, 8)),
            Row(PageButton("Manage memory", () => Memory_Click(this, new RoutedEventArgs()), primary: !on, id: "OpenMemory"))));
        if (conversationHistory is { } record) page.Children.Add(HistoryCard(record, on));
    }

    /// <summary>Companion › Memory › Conversation history: whether Martlet keeps a record of conversations on this PC (on by
    /// default while memory is on) and may search it on its own (search_conversations, off by default), what it holds and the
    /// window to read, search and delete it. Each choice saves at once (conversation-history.json).</summary>
    private Border HistoryCard(DesktopConversationHistory record, bool memoryOn)
    {
        var prefs = record.Preferences;
        var status = Note(record.Describe(homeSettings?.Memory), new Thickness(0, 2, 0, 8));
        AutomationProperties.SetAutomationId(status, "HistoryStatus");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        var keep = new CheckBox { Content = "Keep a record of my conversations", IsChecked = prefs.Keep, IsEnabled = memoryOn };
        AutomationProperties.SetAutomationId(keep, "HistoryKeep");
        var search = new CheckBox
        {
            Content = "Let Martlet search the record on its own", IsChecked = prefs.Search, IsEnabled = memoryOn && prefs.Keep,
            Margin = new Thickness(0, 10, 0, 0)
        };
        AutomationProperties.SetAutomationId(search, "HistorySearch");
        void Save()
        {
            var next = new ConversationHistoryPreferences(keep.IsChecked == true, search.IsChecked == true);
            if (next == record.Preferences) return;
            if (!record.SetPreferences(next))
                ErrorLog.Warn("Couldn't save the conversation history choice on this PC; it applies until Martlet restarts.");
            search.IsEnabled = memoryOn && next.Keep;
            status.Text = record.Describe(homeSettings?.Memory);
        }
        foreach (var choice in new[] { keep, search })
        {
            choice.Checked += (_, _) => Save();
            choice.Unchecked += (_, _) => Save();
        }
        // The counts appear once the record has been read (in the background, the first time).
        if (!record.Store.Loaded)
            _ = record.Store.LoadAsync().ContinueWith(_ => Dispatcher.InvokeAsync(() => status.Text = record.Describe(homeSettings?.Memory)),
                TaskScheduler.Default);
        return Card(Heading("Conversation history"), status, keep,
            HelpTip.Explain("Each exchange (what you typed or said and Martlet's reply) is kept on this PC. When you mention an earlier " +
                "conversation, like \"remember when...\" or \"what did we talk about yesterday?\", Martlet brings back what was said. " +
                "Screen glances and what this PC plays are never recorded.", new Thickness(24, 2, 0, 0), "ConversationHistory", "conversation history"),
            search,
            HelpTip.Explain("With a Thinking model that uses tools, Martlet can also look things up in the record whenever it thinks that " +
                "helps. Its search tool makes every request a little longer, so the first reply of a conversation may start a " +
                "little later.", new Thickness(24, 2, 0, 0), "ConversationSearch", "searching the record"),
            Row(PageButton("Open conversations", History_Click, id: "OpenHistory")));
    }

    // ---------- small builders ----------

    private Border Card(params UIElement[] children)
    {
        var stack = new StackPanel();
        foreach (var child in children) stack.Children.Add(child);
        var card = new Border { Child = stack };
        card.SetResourceReference(StyleProperty, "CardStyle");
        return card;
    }

    private static TextBlock Heading(string text)
    {
        var heading = new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 6) };
        heading.SetResourceReference(StyleProperty, "SectionHeading");
        return heading;
    }

    private static TextBlock Note(string text, Thickness margin)
    {
        var note = new TextBlock { Text = text, Margin = margin };
        note.SetResourceReference(StyleProperty, "Muted");
        note.TextWrapping = TextWrapping.Wrap;
        return note;
    }

    private static WrapPanel Row(params Button?[] buttons)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var button in buttons.OfType<Button>())
        {
            button.Margin = new Thickness(0, 0, 10, 6);
            row.Children.Add(button);
        }
        return row;
    }

    private static Button PageButton(string label, Action run, bool primary = false, bool link = false, string? id = null)
    {
        var button = new Button { Content = label, MinWidth = link ? 0 : 96 };
        if (primary) button.SetResourceReference(StyleProperty, "PrimaryButton");
        else if (link) button.SetResourceReference(StyleProperty, "LinkButton");
        AutomationProperties.SetName(button, label);
        if (id is not null) AutomationProperties.SetAutomationId(button, id);
        button.Click += (_, _) => run();
        return button;
    }

    // ---------- dense editor rows (Companion › Emotes and motions, and Touch's touch zones and touch temperament) ----------

    /// <summary>Where a dense row's fields start, under its name: an empty check box is 29 pixels wide (its box and the gap its
    /// template keeps before content), then a 6-pixel gap.</summary>
    private const double RowIndent = 35;

    /// <summary>The check box that turns a dense row on, before its name.</summary>
    private static CheckBox RowSwitch(bool on) =>
        new() { IsChecked = on, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, RowIndent - 29, 0) };

    /// <summary>Makes a text box, choice or button one of a dense row's compact controls: 32 pixels tall, on the row's centre line.</summary>
    private static T Compact<T>(T control) where T : Control
    {
        control.SetResourceReference(StyleProperty, control switch
        {
            TextBox => "CompactTextBox", ComboBox => "CompactComboBox", _ => "CompactButton"
        });
        if (control is Button) control.MinWidth = 56;
        return control;
    }

    /// <summary>A short muted label in a dense row, on the centre line of the control it names. Labels that start a row's lines get
    /// the same <paramref name="width"/>, so the boxes after them line up.</summary>
    private static Label RowLabel(string text, UIElement target, double left = 0, double right = 6, double width = 0)
    {
        var label = new Label
        {
            Content = text, Target = target, Padding = new Thickness(left, 0, right, 0), VerticalAlignment = VerticalAlignment.Center,
            MinWidth = width
        };
        label.SetResourceReference(ForegroundProperty, "MutedBrush");
        return label;
    }

    /// <summary>Items of a dense row that stay together when the row wraps, such as a label and its box.</summary>
    private static StackPanel RowGroup(params UIElement[] items)
    {
        var group = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 6) };
        foreach (var item in items) group.Children.Add(item);
        return group;
    }

    /// <summary>A text box with a grey <paramref name="hint"/> over it while it is empty.</summary>
    private static Grid WithHint(TextBox box, string hint, string? id = null)
    {
        var shown = new TextBlock
        {
            Text = hint, IsHitTestVisible = false, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(box.Padding.Left + 1, 0, box.Padding.Right + 1, 0),
            Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed
        };
        shown.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        if (id is not null) AutomationProperties.SetAutomationId(shown, id);
        box.TextChanged += (_, _) => shown.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var grid = new Grid { VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(box);
        grid.Children.Add(shown);
        return grid;
    }
}
