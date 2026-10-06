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
internal enum CompanionTab { Thinking, DeepThinking, Voice, Listening, Vision, LipSync, Character, Personality, Prompts, Lorebook, Memory, People, Replies, Tools, SmartHome }

/// <summary>The side list's groups, in order: how it works (where each job runs), who it is (look, personality, what it knows)
/// and what it does (how it answers and acts). A group with no pages yet is not shown.</summary>
internal enum CompanionGroup { HowItWorks, WhoItIs, WhatItDoes }

/// <summary>A conversation model Ollama can download and run on this PC. <paramref name="MinimumVramGb"/> is the graphics card
/// it needs to run comfortably beside a game and Martlet's character (0 means any PC). <paramref name="Hears"/>: Ollama takes
/// your recording for it (Gemma 4 E2B, E4B and 12B); the others always get the transcript (the Parakeet cascade).</summary>
internal sealed record LocalChatModel(string Id, string Size, string Fits, double MinimumVramGb, bool Hears);

/// <summary>The Companion page: a side list of pages in groups (How it works: Thinking, Voice, Listening, Lip-sync; Who it is:
/// Character, Personality, Lorebook, Memory; What it does: Smart home). Each job page asks where the job runs (this PC by default, another of your computers, or a
/// cloud provider; voice loudness for lip-sync) and shows only that place's fields, including the API key for a cloud provider.
/// Everything saves through the same setup service, consent and credential rules as Setup.</summary>
public partial class MainWindow
{
    /// <summary>Where a job runs. Lip-sync has no cloud provider; its voice-loudness way is one of this PC's.</summary>
    private enum JobPlace { ThisPc, Computer, Cloud }

    private sealed record CloudProvider(string Name, string? BaseUrl, bool Chat, string? DefaultModel, bool NeedsKey)
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
    private static readonly IReadOnlyList<CloudProvider> ThinkingProviders =
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
    private IReadOnlyList<WindowsVoice>? windowsVoices;
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

    private static CompanionGroup GroupOf(CompanionTab section) => section switch
    {
        CompanionTab.Thinking or CompanionTab.DeepThinking or CompanionTab.Voice or CompanionTab.Listening or CompanionTab.Vision or CompanionTab.LipSync => CompanionGroup.HowItWorks,
        CompanionTab.Character or CompanionTab.Personality or CompanionTab.Prompts or CompanionTab.Lorebook or CompanionTab.Memory or CompanionTab.People => CompanionGroup.WhoItIs,
        CompanionTab.Replies => CompanionGroup.WhatItDoes,
        CompanionTab.Tools => CompanionGroup.WhatItDoes,
        CompanionTab.SmartHome => CompanionGroup.WhatItDoes,
        _ => CompanionGroup.WhatItDoes
    };

    private static string GroupTitle(CompanionGroup group) => group switch
    {
        CompanionGroup.HowItWorks => "How it works",
        CompanionGroup.WhoItIs => "Who it is",
        _ => "What it does"
    };

    private static string TabTitle(CompanionTab section) => section switch
    {
        CompanionTab.Thinking => "Thinking",
        CompanionTab.DeepThinking => "Deep thinking",
        CompanionTab.Voice => "Voice",
        CompanionTab.Listening => "Listening",
        CompanionTab.Vision => "Vision",
        CompanionTab.LipSync => "Lip-sync",
        CompanionTab.Character => "Character",
        CompanionTab.Personality => "Personality",
        CompanionTab.Prompts => "Prompts",
        CompanionTab.Lorebook => "Lorebook",
        CompanionTab.Memory => "Memory",
        CompanionTab.People => "People",
        CompanionTab.Replies => "Replies",
        CompanionTab.Tools => "Tools",
        CompanionTab.SmartHome => "Smart home",
        _ => section.ToString()
    };

    /// <summary>The page's icon in the side list (Segoe Fluent Icons).</summary>
    private static string TabGlyph(CompanionTab section) => section switch
    {
        CompanionTab.Thinking => "\uE82F",
        CompanionTab.DeepThinking => "\uE945",
        CompanionTab.Voice => "\uE767",
        CompanionTab.Listening => "\uE720",
        CompanionTab.Vision => "\uE890",
        CompanionTab.LipSync => "\uE8BD",
        CompanionTab.Character => "\uE77B",
        CompanionTab.Personality => "\uE76E",
        CompanionTab.Prompts => "\uE943",
        CompanionTab.Lorebook => "\uE736",
        CompanionTab.Memory => "\uE8F1",
        CompanionTab.People => "\uE716",
        CompanionTab.Replies => "\uE8F2",
        CompanionTab.Tools => "\uE90F",
        CompanionTab.SmartHome => "\uEC26",
        _ => "\uE76E"
    };

    /// <summary>The line under the page title: what the page decides, in one or two sentences.</summary>
    private static string TabIntro(CompanionTab section) => section switch
    {
        CompanionTab.Thinking => "Choose where Martlet thinks and which model it uses. This PC keeps conversations local.",
        CompanionTab.DeepThinking => "Thinking answers you. Deep thinking works out hard tasks in the background, ideally on another machine, so Martlet keeps talking.",
        CompanionTab.Voice => "Choose how Martlet speaks and where speech is generated.",
        CompanionTab.Listening => "Choose the microphone, push-to-talk mode and speech recognition.",
        CompanionTab.Vision => "Choose whether Martlet can see your screen or camera once you press Start watching.",
        CompanionTab.LipSync => "Choose what moves the character's mouth.",
        CompanionTab.Character => "Choose Martlet's character, size, position and motion.",
        CompanionTab.Personality => "Edit Martlet's personas and response style.",
        CompanionTab.Prompts => "Every instruction Martlet sends to the Thinking model. Edit any of them; your text is used instead of the built-in one.",
        CompanionTab.Lorebook => "Add lore entries Martlet can use when keywords come up.",
        CompanionTab.Memory => "Facts Martlet remembers about you between conversations.",
        CompanionTab.People => "Teach Martlet whose voices it hears and the names they use.",
        CompanionTab.Replies => "Control reply length and creativity.",
        CompanionTab.Tools => "Let Martlet run terminal commands and use MCP tools while you talk, and choose when it must ask first.",
        CompanionTab.SmartHome => "Find, set up or install Home Assistant, share it with your other computers, and let Martlet control your home when you ask.",
        _ => ""
    };

    /// <summary>Recommended local model: the fastest, gemma4:e2b, on every PC. It starts a spoken reply soonest (about 150 ms to
    /// its first sentence on an RTX 4070, against 100-200 ms more for E4B and 12B; docs/VOICE_LATENCY.md) and hears recordings
    /// itself. A larger model the card fits is smarter but slower (<see cref="LargestLocalModel"/>).</summary>
    internal static LocalChatModel RecommendedLocalModel(double? vramGb) => LocalChatModels[0];

    /// <summary>How a suggested local model reads in Companion › Thinking's list: its size, the card it fits, whether it hears
    /// your recording (or gets the transcript) and whether it is the fastest or the smartest that fits here.</summary>
    internal static string LocalModelPick(LocalChatModel model, LocalChatModel recommended, LocalChatModel smartest) =>
        $"{model.Id}  ({model.Size}, fits {model.Fits}, {(model.Hears ? "hears your voice" : "gets the transcript")}" +
        $"{(model == recommended ? ", fastest, recommended" : model == smartest ? ", smartest that fits here" : "")})";

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
        if (route.RouteType == SetupRouteType.LocalWindowsTts) return "Windows voice on this PC";
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
            // A host has no Companion page; its routes are still reachable through full Setup.
            nextSetupJob = JobRole(tab);
            Setup_Click(this, new RoutedEventArgs());
            return;
        }
        companionTab = tab;
        tabPlace.Remove(tab);
        if (NavCompanion.IsChecked == true) ShowCompanionTab(entering: false);
        else NavCompanion.IsChecked = true;
    }

    private void ShowCompanionTab(bool entering)
    {
        openTab = companionTab;
        CompanionScroll.ScrollToTop();
        RenderTab();
        Motion.Enter(entering ? CompanionPage : CompanionContent);
        if (openTab == CompanionTab.LipSync) CheckOwnLipSyncAsync().Forget();
    }

    private readonly Dictionary<CompanionTab, RadioButton> companionNav = [];
    private bool selectingNav;

    /// <summary>The side list, built once: each group's title, then its pages. Choosing a page opens it.</summary>
    private void BuildCompanionNav()
    {
        if (companionNav.Count > 0) return;
        foreach (var group in Enum.GetValues<CompanionTab>().GroupBy(GroupOf).OrderBy(g => g.Key))
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
        page.Children.Clear();
        var group = new TextBlock { Text = GroupTitle(GroupOf(section)).ToUpperInvariant() };
        group.SetResourceReference(StyleProperty, "Eyebrow");
        page.Children.Add(group);
        var heading = new TextBlock { Text = TabTitle(section), Margin = new Thickness(0, 4, 0, 0) };
        heading.SetResourceReference(StyleProperty, "PageTitle");
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level1);
        page.Children.Add(heading);
        page.Children.Add(Note(TabIntro(section), new Thickness(0, 4, 0, 18)));

        var body = new StackPanel();
        AutomationProperties.SetName(body, TabTitle(section));
        page.Children.Add(body);
        switch (section)
        {
            case CompanionTab.Thinking or CompanionTab.Voice or CompanionTab.Listening: RenderJobTab(body, section); break;
            case CompanionTab.Vision: RenderVisionPage(body); break;
            case CompanionTab.LipSync: RenderLipSyncTab(body); break;
            case CompanionTab.Character: RenderCharacterTab(body); break;
            case CompanionTab.Personality: RenderPersonalityTab(body); break;
            case CompanionTab.Prompts: RenderPromptsTab(body); break;
            case CompanionTab.Lorebook: RenderLorebookTab(body); break;
            case CompanionTab.Memory: RenderMemoryTab(body); break;
            case CompanionTab.People: RenderPeopleTab(body); break;
            case CompanionTab.Replies: RenderRepliesTab(body); break;
            case CompanionTab.DeepThinking: RenderDeepThinkingTab(body); break;
            case CompanionTab.Tools: RenderToolsTab(body); break;
            case CompanionTab.SmartHome: RenderSmartHomeTab(body); break;
            default: throw new UnreachableException($"The Companion page {section} has no content.");
        }
    }

    // ---------- job pages ----------

    /// <summary>Routes that run on this PC without Martlet's host service: Ollama for thinking, installed Windows speech and
    /// native whisper.cpp.</summary>
    private static bool RunsHereWithoutHost(SetupRoute route) =>
        IsLocalOllama(route) || route.RouteType is SetupRouteType.LocalWindowsTts or SetupRouteType.LocalWindowsStt or SetupRouteType.LocalWhisper or
            SetupRouteType.LocalParakeet;

    private void RenderJobTab(Panel page, CompanionTab section)
    {
        var role = JobRole(section)!.Value;
        var job = HostJob.For(role)!;
        var route = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == role);
        var thisPc = ThisPcHost();
        var current = route is null || RunsHereWithoutHost(route) ? JobPlace.ThisPc
            : SelfHostSetup.IsGateway(route.RouteType)
                ? route.Gateway?.HostId == thisPc?.HostId && role != SetupRole.Llm ? JobPlace.ThisPc : JobPlace.Computer
                : JobPlace.Cloud;
        var place = tabPlace.TryGetValue(section, out var chosen) ? chosen : current;

        page.Children.Add(JobNowCard(section, job, route));

        page.Children.Add(WhereItRunsCard(section, section.ToString(), "Where it runs", null, route is null ? null : current, place,
            (JobPlace.ThisPc, "This PC (recommended)", role switch
            {
                SetupRole.Llm => "Run a local model here. Conversations stay on this PC, with no API key or per-request cost.",
                SetupRole.Tts => "Run a voice engine here, or use a Windows voice. Audio stays on this PC.",
                _ => "Turn your speech into text on this PC. Your voice stays here."
            }),
            (JobPlace.Computer, "Another of your computers",
                $"Use another paired computer on your network for {job.Job}."),
            (JobPlace.Cloud, "A cloud provider", role == SetupRole.Llm
                ? "Use OpenAI, OpenRouter, NVIDIA Build or another compatible provider. Requests may cost money."
                : "Use OpenAI with your API key. Requests may cost money.")));

        page.Children.Add(place switch
        {
            JobPlace.ThisPc when role == SetupRole.Llm => LocalThinkingCard(route),
            JobPlace.ThisPc when role == SetupRole.Tts => VoiceEnginesCard(route, thisPc, onThisPc: true),
            JobPlace.Computer when role == SetupRole.Tts => VoiceEnginesCard(route, thisPc, onThisPc: false),
            JobPlace.ThisPc => LocalListeningCard(route, thisPc),
            JobPlace.Computer => ComputersCard(job, route, role == SetupRole.Llm ? null : thisPc),
            _ => CloudCard(section, job, route)
        });

        // Singing uses the voices of the voice library on a computer with the singing role, wherever Speaking runs.
        if (section == CompanionTab.Voice) page.Children.Add(SingingCard());

        if (role == SetupRole.Llm) page.Children.Add(FallbackCard());

        // The voices the self-hosted engines copy from your recordings, wherever one can speak: this PC or another of your
        // computers. A cloud provider has its own voices.
        if (section == CompanionTab.Voice && place != JobPlace.Cloud) page.Children.Add(VoicesCard(route));

        if (section == CompanionTab.Voice) page.Children.Add(AudioCard(output: true));
        if (section == CompanionTab.Voice) page.Children.Add(SpeakRepliesCard());
        if (section == CompanionTab.Listening) page.Children.Add(AudioCard(output: false));
        if (section == CompanionTab.Listening) page.Children.Add(TalkModeCard());
        if (section == CompanionTab.Listening) page.Children.Add(EchoCard());
        if (section == CompanionTab.Listening) page.Children.Add(PcAudioCard());
        if (section == CompanionTab.Listening) page.Children.Add(HearVoiceCard());
        if (section == CompanionTab.Listening)
            page.Children.Add(Card(Heading("Who is talking"),
                Note(localVoices.Active
                    ? "Voice recognition is on. Manage known voices on People."
                    : "Turn on voice recognition so Martlet can learn who's speaking.",
                    new Thickness(0, 0, 0, 0)),
                Row(PageButton("Open People", () => OpenCompanion(CompanionTab.People), link: true, id: "OpenPeople"))));

        var advanced = PageButton("Advanced setup", () =>
        {
            nextSetupJob = role;
            Setup_Click(this, new RoutedEventArgs());
        }, link: true, id: "OpenSetup");
        advanced.HorizontalAlignment = HorizontalAlignment.Left;
        advanced.Margin = new Thickness(0, 4, 0, 0);
        page.Children.Add(advanced);
    }

    /// <summary>The voice a route speaks with, in words: ", voice Zira (en-US)", or nothing.</summary>
    private static string VoiceSuffix(SetupRoute route) =>
        route.Reference is { } reference ? $", voice {reference.PresetName}"
        : route.VoiceId is { } voice ? $", voice {(route.RouteType == SetupRouteType.LocalWindowsTts ? WindowsVoices.DisplayName(voice) : voice)}"
        : "";

    /// <summary>What the job uses now, first on every setup page, with any problem that stops it.</summary>
    private Border JobNowCard(CompanionTab section, HostJob job, SetupRoute? route)
    {
        var problem = coverage.FirstOrDefault(c => c.Job == job.Job && c.IsProblem);
        var routeName = route is null ? "" : route.RouteType == SetupRouteType.LocalWindowsTts ? "Windows voice on this PC"
            : route.RouteType == SetupRouteType.LocalParakeet ? $"{PlaceName(route)}: {ParakeetName(route.ModelId)}"
            : route.RouteType == SetupRouteType.LocalWhisper ? PlaceName(route)
            : $"{PlaceName(route)}: {route.ModelId}";
        var status = route is null
            ? section == CompanionTab.Voice
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

    private Border LocalThinkingCard(SetupRoute? route)
    {
        var installed = !Prerequisites.IsMissing(Prerequisites.Ollama);
        // What Ollama already has decides whether switching needs a download, so look once without being asked (loopback only).
        if (installed && ollamaModels is null && !ollamaAutoChecked)
        {
            ollamaAutoChecked = true;
            CheckOllamaAsync(quiet: true).Forget();
        }
        var recommended = RecommendedLocalModel(machine.BestGpu?.MemoryGb);
        var model = new TextBox { MaxLength = 128, Width = 420, HorizontalAlignment = HorizontalAlignment.Left,
            Text = IsLocalOllama(route) ? route!.ModelId : recommended.Id };
        AutomationProperties.SetName(model, "Local model");
        AutomationProperties.SetAutomationId(model, "SetupLocalModel");
        model.TextChanged += (_, _) => tabEdited = true;
        var picks = new ComboBox { Width = 420, HorizontalAlignment = HorizontalAlignment.Left,
            ItemsSource = LocalChatModels.Select(m => LocalModelPick(m, recommended, LargestLocalModel(machine.BestGpu?.MemoryGb)))
                .Concat((ollamaModels ?? []).Where(id => LocalChatModels.All(m => m.Id != id)).Select(id => $"{id}  (downloaded)")).ToArray() };
        AutomationProperties.SetName(picks, "Suggested local models");
        AutomationProperties.SetAutomationId(picks, "SetupLocalModelPicks");
        picks.SelectionChanged += (_, _) => { if (picks.SelectedItem is string pick) model.Text = pick.Split(' ')[0]; };

        var gpu = machine.BestGpu is { } best ? $"This PC has {best.Describe()}." : "No dedicated graphics card was found; small models still run on the processor.";
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), FontWeight = FontWeights.SemiBold,
            Text = (!installed ? "Ollama isn't installed on this PC yet."
                : ollamaModels is null ? "Ollama is installed. Check it to see which models are downloaded."
                : ollamaModels.Count == 0 ? "Ollama is running, but no model is downloaded yet."
                : $"Ollama is running with: {string.Join(", ", ollamaModels)}.") +
                (IsLocalOllama(route) ? $" Thinking uses {route!.ModelId}." : "") };
        AutomationProperties.SetAutomationId(status, "SetupOllamaStatus");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);

        string ModelId() => (model.Text ?? "").Trim();
        var tested = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
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
        model.TextChanged += (_, _) => ShowTest();
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

        var smartest = LargestLocalModel(machine.BestGpu?.MemoryGb);
        var suggestion = Note($"Recommended: {recommended.Id} ({recommended.Size}), the fastest: replies start soonest, and it hears your voice. " +
            (smartest.Id != recommended.Id
                ? $"Bigger models are smarter but slower; this PC's graphics card fits up to {smartest.Id} ({smartest.Size}). "
                : "Bigger models are smarter but slower. ") +
            "Each leaves room on the graphics card for a game and Martlet's character.", new Thickness(0, 6, 0, 0));
        AutomationProperties.SetAutomationId(suggestion, "SetupLocalRecommendation");

        return Card(Heading("Ollama on this PC"),
            Note("Ollama runs a local conversation model. Your messages stay on this PC, with no API key or per-request cost.", new Thickness(0, 0, 0, 8)),
            status,
            new Label { Content = "Ollama _model", Target = model, Padding = new Thickness(0, 4, 0, 4) },
            model,
            new Label { Content = "_Suggestions", Target = picks, Padding = new Thickness(0, 8, 0, 4) },
            picks,
            suggestion,
            Note(gpu, new Thickness(0, 8, 0, 10)),
            buttons,
            tested);
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
    /// check when it opens, which leaves the status line and a model being typed alone.</summary>
    private async Task CheckOllamaAsync(bool quiet = false)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            using var response = await client.GetAsync(new Uri("http://127.0.0.1:11434/api/tags"), lifetime.Token);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(lifetime.Token));
            ollamaModels = document.RootElement.TryGetProperty("models", out var models)
                ? models.EnumerateArray().Select(m => m.TryGetProperty("name", out var name) ? name.GetString() : null)
                    .OfType<string>().Where(name => name.Length is > 0 and <= 128).Take(50).ToArray()
                : [];
            if (!quiet)
                ActionText.Text = ollamaModels.Count == 0 ? "Ollama is running on this PC, with no model downloaded yet."
                    : $"Ollama is running on this PC with {ollamaModels.Count} {(ollamaModels.Count == 1 ? "model" : "models")}.";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            ollamaModels = null;
            if (!quiet)
                ActionText.Text = Prerequisites.IsMissing(Prerequisites.Ollama)
                    ? "Ollama isn't installed on this PC yet. Install it first."
                    : "Ollama didn't answer on this PC. Start Ollama from the Start menu, then check again.";
        }
        if (!closing && openTab == CompanionTab.Thinking && !(quiet && tabEdited)) RenderTab();
    }

    /// <summary>Use Ollama on this PC. With Ollama installed, the model in the box is downloaded first when it isn't here yet
    /// (after one confirmation) and loaded, in a run window, and only then does Thinking switch to it: the current Thinking
    /// keeps answering until then, the first reply doesn't wait for the load, and a model that won't download or load leaves
    /// Thinking as it was.</summary>
    private async Task SaveLocalThinkingAsync(string model)
    {
        try { ChatCompletionsSetup.ModelId(model); }
        catch (ContractException error) { ActionText.Text = error.Message; return; }
        if (!Prerequisites.IsMissing(Prerequisites.Ollama) && !await PrepareLocalThinkingAsync(model)) return;
        if (closing) return;
        await SaveSectionRouteAsync(HostJob.Thinking,
            settings => ChatCompletionsSetup.SelectRoute(settings, LocalOllamaBaseUrl, model), key: null,
            $"Martlet now uses {model} in Ollama on this PC." +
            (ollamaModels is { } known && !LocalOllama.Serves(known, model) ? $" Download {model} to use it." : ""));
        // Ollama says how much context it gives the model once it has loaded it (Test model does); nothing leaves this PC.
        if (!closing && IsLocalOllama(homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm)))
            CheckNewModelContextAsync().Forget();
    }

    /// <summary>The model chosen last with Use Ollama on this PC. Several can download and load side by side; one chosen
    /// earlier that gets ready after it doesn't switch Thinking.</summary>
    private string? localThinkingWanted;

    /// <summary>Gets <paramref name="model"/> ready in this PC's Ollama before Thinking switches to it: starts Ollama when it
    /// isn't answering, downloads the model when it isn't here (the owner confirms the download), then loads it. Returns
    /// whether Thinking may switch now; otherwise the status line says why it didn't. Choosing another model meanwhile
    /// doesn't wait for this one: the newest choice is the one Thinking switches to.</summary>
    private async Task<bool> PrepareLocalThinkingAsync(string model)
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
                ActionText.Text = "Ollama didn't answer on this PC. Start Ollama from the Start menu, then try again. Thinking didn't change.";
                return false;
            }
            ollamaModels = models;
            var thinking = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
            var keeps = thinking is null || IsLocalOllama(thinking) && thinking.ModelId == model ? ""
                : $" Thinking keeps using {(IsLocalOllama(thinking) ? thinking.ModelId : NetworkMap.ProviderName(thinking))} until it's ready.";
            var download = !LocalOllama.Serves(models, model);
            if (download)
            {
                var size = LocalChatModels.FirstOrDefault(m => m.Id == model)?.Size;
                if (!ConfirmationDialog.Confirm(this,
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

    // ---------- this PC: a Windows voice (the voice engines are in MainWindow.VoiceEngines.cs) ----------

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

    /// <summary>Asks Windows (on request, locally) which voices are installed.</summary>
    private async Task<IReadOnlyList<WindowsVoice>?> FindWindowsVoicesAsync()
    {
        ActionText.Text = "Looking for the voices installed in Windows...";
        try
        {
            windowsVoices = await WindowsVoices.ListAsync(lifetime.Token);
            ActionText.Text = windowsVoices.Count == 0 ? "No voices are installed in Windows on this PC."
                : $"Windows has {windowsVoices.Count} installed {(windowsVoices.Count == 1 ? "voice" : "voices")}.";
            return windowsVoices;
        }
        catch (OperationCanceledException) { return null; }
        catch (InvalidOperationException error)
        {
            ActionText.Text = error.Message;
            return null;
        }
        finally
        {
            if (!closing && openTab == CompanionTab.Voice) RenderTab();
        }
    }

    /// <summary>Makes Martlet speak with a Windows voice: <paramref name="voiceId"/>, or the one in this PC's language. A voice
    /// engine Speaking leaves on a host stops there afterwards (asked first).</summary>
    private async Task UseWindowsVoiceAsync(string? voiceId)
    {
        var voices = windowsVoices is { Count: > 0 } known && voiceId is not null ? known : await FindWindowsVoicesAsync();
        if (voices is null || closing) return;
        var voice = voiceId is null ? WindowsVoices.Recommended(voices) : voices.FirstOrDefault(v => v.Id == voiceId);
        if (voice is null)
        {
            ActionText.Text = "No voices are installed in Windows on this PC. Add a language with its voice in Windows Settings, then try again.";
            return;
        }
        var leaving = LeavingEngine(null, null);
        if (leaving is not null && !ConfirmationDialog.Confirm(this, $"Speak with the Windows voice {voice} on this PC?" + LeavingNote(leaving),
                "Use a Windows voice"))
            return;
        var saved = await SaveSectionRouteAsync(HostJob.Speaking, settings => WindowsSpeechSetup.SelectTts(settings, voice.Id), key: null,
            $"Martlet now speaks with the Windows voice {voice} on this PC. Audio stays on this PC.");
        if (!saved || closing || leaving is null) return;
        ActionText.Text += await StopLeftVoiceEngineAsync(leaving);
    }

    private async Task PreviewWindowsVoiceAsync(string voiceId)
    {
        try
        {
            ActionText.Text = "Playing a short sample...";
            await WindowsVoices.PreviewAsync(voiceId, lifetime.Token);
            ActionText.Text = "Sample played.";
        }
        catch (OperationCanceledException) { }
        catch (WindowsVoiceException) { ActionText.Text = "That Windows voice is no longer installed. Choose another one."; }
        catch (InvalidOperationException error) { ActionText.Text = error.Message; }
    }

    private void OpenWindowsSpeechSettings()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:speech") { UseShellExecute = true })?.Dispose(); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException) { ActionText.Text = error.Message; }
    }
    // ---------- another of your computers ----------

    private Border ComputersCard(HostJob job, SetupRoute? route, PairedHost? exclude) =>
        ComputersCard(job.Job, job.Engine, job.HostRoleKind, NetworkMap.JobHost(homeSettings, job.Role),
            $"In use: {job.Engine} {route?.ModelId}.",
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
                : model is not null ? $"Runs {engine} ({model})."
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
        stack.Add(Row(
            PageButton("Add a computer", () => RunNodeAction(NodeAction.AddComputer), primary: hosts.Length == 0, id: "SetupAddComputer-" + job),
            paired.Length == 0 ? null : PageButton("Check hosts", () => RunNodeAction(NodeAction.CheckHost), id: "SetupCheckHosts-" + job),
            PageButton("Open Devices", () => Navigate(NavDevices), id: "SetupOpenMap-" + job)));
        stack.Add(Note(disclosure, new Thickness(0, 8, 0, 0)));
        return Card([.. stack]);
    }

    // ---------- cloud provider ----------

    private Border CloudCard(CompanionTab section, HostJob job, SetupRoute? route)
    {
        var role = job.Role;
        var cloudRoute = route is not null && (route.RouteType is null or SetupRouteType.OpenAi ||
            route.RouteType == SetupRouteType.ChatCompletions && !IsLocalOllama(route)) ? route : null;
        IReadOnlyList<CloudProvider> providers = role == SetupRole.Llm ? ThinkingProviders : [OpenAiCloud];
        var provider = new ComboBox { ItemsSource = providers, MinHeight = 30, MaxWidth = 420, MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(provider, "Provider");
        AutomationProperties.SetAutomationId(provider, "SetupCloudProvider-" + section);
        provider.SelectedItem = cloudRoute?.RouteType == SetupRouteType.ChatCompletions
            ? providers.FirstOrDefault(p => p.BaseUrl == cloudRoute.Origin) ?? CustomCloud
            : OpenAiCloud;

        var baseUrl = new TextBox { MaxLength = 2048, Width = 420, HorizontalAlignment = HorizontalAlignment.Left,
            Text = cloudRoute?.RouteType == SetupRouteType.ChatCompletions ? cloudRoute.Origin : "" };
        AutomationProperties.SetAutomationId(baseUrl, "SetupCloudBaseUrl");
        var baseUrlPanel = new StackPanel { Children = { new Label { Content = "API _base URL (without /chat/completions)", Target = baseUrl, Padding = new Thickness(0, 8, 0, 4) }, baseUrl } };

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
        var keySavedMark = new TextBlock
        {
            Text = "••••••••  Key saved (type to replace)", IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0), Opacity = 0.7
        };
        keySavedMark.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        var keyField = new Grid { Width = 420, HorizontalAlignment = HorizontalAlignment.Left, Children = { key, keySavedMark } };
        var keySaved = false;
        void RefreshKeyMark()
        {
            using var entered = key.SecurePassword;
            keySavedMark.Visibility = keySaved && entered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        var keyStatus = Note("", new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(keyStatus, "SetupCloudKeyStatus-" + section);
        var hint = Note("", new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(hint, "SetupCloudHint-" + section);
        var consent = new CheckBox { Margin = new Thickness(0, 12, 0, 8) };
        AutomationProperties.SetAutomationId(consent, "SetupCloudConsent-" + section);
        var consentText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        consent.Content = consentText;

        CloudProvider Selected() => provider.SelectedItem as CloudProvider ?? OpenAiCloud;
        string? ChatUrl(CloudProvider p) => p.Chat ? p.BaseUrl is { Length: > 0 } fixedUrl ? fixedUrl : baseUrl.Text.Trim() : null;
        bool SameAsSaved(CloudProvider p) => cloudRoute is not null && (p.Chat
            ? cloudRoute.RouteType == SetupRouteType.ChatCompletions && cloudRoute.Origin == ChatUrl(p)
            : cloudRoute.RouteType is null or SetupRouteType.OpenAi);
        // A key this job used with the provider before, set aside when it switched away, is used again.
        bool SetAside(CloudProvider p) => !(SameAsSaved(p) && cloudRoute!.CredentialId is not null) &&
            SetupSettings.SetAsideCredentials(homeSettings, role, ChatUrl(p)).Count > 0;
        bool HasKey(CloudProvider p) => SameAsSaved(p) && cloudRoute!.CredentialId is not null || SetAside(p);
        string? Default(CloudProvider p) => p.Chat ? p.DefaultModel : role switch
        {
            SetupRole.Llm => OpenAiTextGenerationCatalog.DefaultModelId,
            SetupRole.Stt => OpenAiTranscriptionCatalog.DefaultModelId,
            _ => OpenAiSpeechSynthesisCatalog.DefaultModelId
        };
        IReadOnlyList<string> Catalog(CloudProvider p) => p.Chat ? (p.DefaultModel is { } d ? [d] : []) : role switch
        {
            SetupRole.Llm => OpenAiTextGenerationCatalog.SupportedModelIds,
            SetupRole.Stt => OpenAiTranscriptionCatalog.SupportedModelIds,
            _ => OpenAiSpeechSynthesisCatalog.SupportedModelIds
        };

        void Refresh(bool keepModel)
        {
            var p = Selected();
            baseUrlPanel.Visibility = p == CustomCloud ? Visibility.Visible : Visibility.Collapsed;
            model.ItemsSource = Catalog(p);
            model.Visibility = p.Chat ? Visibility.Collapsed : Visibility.Visible;
            modelText.Visibility = p.Chat ? Visibility.Visible : Visibility.Collapsed;
            if (!keepModel)
            {
                var value = SameAsSaved(p) ? cloudRoute!.ModelId : Default(p) ?? "";
                if (p.Chat) modelText.Text = value;
                else model.SelectedItem = Catalog(p).Contains(value, StringComparer.Ordinal) ? value : Default(p);
            }
            var saved = HasKey(p);
            keySaved = saved;
            RefreshKeyMark();
            keyStatus.Text = SetAside(p) ? $"Your {p.Name} key from before is still saved. Leave this empty to use it again, or paste a new key."
                : saved ? $"Your {p.Name} key is saved. Leave this empty to keep it, or paste a new key."
                : p.NeedsKey ? $"Paste your {p.Name} API key. Martlet saves it in Windows Credential Manager."
                : "Add a key only if your server needs one.";
            var retired = SameAsSaved(p) && p.Chat ? ChatCompletionsEndpointCatalog.RetiredOn(p.BaseUrl, cloudRoute!.ModelId) : null;
            hint.Text = (retired is null ? "" : $"{retired.Name} no longer supports {cloudRoute!.ModelId}. Choose another model. ") +
                (p == OpenAiCloud ? (role == SetupRole.Llm ? $"Recommended: {OpenAiTextGenerationCatalog.DefaultModelId}." : "The recommended model is prefilled.")
                : p.BaseUrl == ChatCompletionsEndpointCatalog.OpenRouterBaseUrl ? $"Recommended: {p.DefaultModel}. Any exact OpenRouter model ID works."
                : p.BaseUrl == ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl ? $"Recommended: {p.DefaultModel}. Keys start with nvapi-."
                : "Use an HTTPS URL, or a local http://127.0.0.1 address. Enter the exact model ID.");
            consentText.Text = $"I choose {p.Name} for {job.Job}. {job.Sent} will be sent there, and requests may cost money. " +
                OpenAiSetup.Boundary(role);
            consent.IsChecked = SameAsSaved(p) && cloudRoute!.Consent is not null && keepModel;
        }
        Refresh(keepModel: false);
        consent.IsChecked = cloudRoute?.Consent is not null;
        provider.SelectionChanged += (_, _) => { tabEdited = true; Refresh(keepModel: false); };
        modelText.TextChanged += (_, _) => { if (!modelText.IsKeyboardFocusWithin) return; tabEdited = true; consent.IsChecked = false; };
        model.SelectionChanged += (_, _) => { tabEdited = true; consent.IsChecked = false; };
        baseUrl.TextChanged += (_, _) => { tabEdited = true; consent.IsChecked = false; };
        voice.SelectionChanged += (_, _) => { tabEdited = true; consent.IsChecked = false; };
        key.PasswordChanged += (_, _) => { tabEdited = true; RefreshKeyMark(); };
        baseUrl.TextChanged += (_, _) => { keySaved = HasKey(Selected()); RefreshKeyMark(); };

        // A cloud provider is a commitment (a key, data sent elsewhere, possible costs), so it stays an explicit action named for
        // what it does rather than an automatic save.
        string UseLabel() => Selected() == CustomCloud ? "Use this server" : $"Use {Selected().Name}";
        var save = PageButton(UseLabel(), () => SaveCloudAsync(job, Selected(), baseUrl.Text.Trim(), Selected().Chat ? modelText.Text.Trim() : model.SelectedItem as string ?? "",
            role == SetupRole.Tts ? voice.SelectedItem as string : null, key, consent.IsChecked == true).Forget(), primary: true, id: "SetupCloudSave-" + section);
        provider.SelectionChanged += (_, _) => save.Content = UseLabel();

        var stack = new List<UIElement> { Heading("Cloud provider") };
        // A drop-down with a single entry chooses nothing; name the provider instead.
        if (providers.Count > 1)
        {
            stack.Add(new Label { Content = "_Provider", Target = provider, Padding = new Thickness(0, 0, 0, 4) });
            stack.Add(provider);
        }
        else stack.Add(new TextBlock { Text = providers[0].Name, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        stack.AddRange(
        [
            baseUrlPanel,
            new Label { Content = "_Model", Target = model, Padding = new Thickness(0, 8, 0, 4) },
            model,
            modelText,
            hint
        ]);
        if (role == SetupRole.Tts)
        {
            stack.Add(new Label { Content = "_Voice", Target = voice, Padding = new Thickness(0, 8, 0, 4) });
            stack.Add(voice);
        }
        stack.Add(new Label { Content = "API _key", Target = key, Padding = new Thickness(0, 8, 0, 4) });
        stack.Add(keyField);
        stack.Add(keyStatus);
        stack.Add(Note(OpenAiSetup.Disclosure, new Thickness(0, 10, 0, 0)));
        stack.Add(consent);
        stack.Add(Row(save));
        return Card([.. stack]);
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
                key, $"{job.Title} now uses {provider.Name} ({model}{(voice is null ? "" : ", voice " + voice)}).{(key is null ? "" : " Your API key is saved in Windows Credential Manager.")} Requests may cost money there.",
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
    /// confirmed choice. A key the route no longer uses is set aside (listed for removal in Advanced setup), and a key set aside
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
            if (key is null && chosen is { CredentialId: null, RouteType: SetupRouteType.OpenAi or SetupRouteType.ChatCompletions })
            {
                var service = setupService;
                foreach (var setAside in SetupSettings.SetAsideCredentials(updated, role,
                             chosen.RouteType == SetupRouteType.ChatCompletions ? chosen.Origin : null))
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
        var resetPosition = showing ? PageButton("Reset position", () => ResetCharacterPositionAsync().Forget(), id: "SetupCharacterResetPosition") : null;
        if (resetPosition is not null) resetPosition.IsEnabled = !locked;
        var modelCard = Card(Heading("Character model"),
            Note("Choose a character model, then adjust its size, position and motion. Choices save on their own and a showing character switches right away.", new Thickness(0, 0, 0, 8)),
            Row(PageButton(showing ? "Hide character" : "Show character", () => RunNodeAction(NodeAction.ToggleCharacter), primary: !showing, id: "SetupCharacterToggle"),
                PageButton("Choose and customize", () => RunNodeAction(NodeAction.Character), id: "OpenAvatar"),
                resetPosition,
                showing ? PageButton("Reset zoom", () => ResetCharacterZoomAsync().Forget(), id: "SetupCharacterResetZoom") : null,
                // Unlocking is only here and on Home, never on the character itself.
                showing || locked ? PageButton(locked ? "Unlock position" : "Lock position",
                    () => SetCharacterLockAsync(!avatar.PlacementLocked).Forget(), id: "SetupCharacterLock") : null));
        if (modelCard.Child is Panel modelPanel)
        {
            var placementNote = Note(CharacterPlacementText(), new Thickness(0, 4, 0, 0));
            AutomationProperties.SetAutomationId(placementNote, "SetupCharacterPlacement");
            modelPanel.Children.Add(placementNote);
        }
        // What the showing model drives: textures (and any downscaling), blinking, mouth, motions and physics.
        if (showing && avatar.Capabilities is { } loaded && modelCard.Child is Panel modelStack)
        {
            var modelNote = Note("Model: " + AvatarRendererProcess.Describe(loaded), new Thickness(0, 4, 0, 0));
            AutomationProperties.SetAutomationId(modelNote, "SetupCharacterModel");
            modelStack.Children.Add(modelNote);
        }
        page.Children.Add(modelCard);
        page.Children.Add(CharacterActionsCard());
        page.Children.Add(CharacterModelsCard());
        page.Children.Add(SpeechDisplayCard());
        characterViewText = null;
        if (!showing) return;
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

    private CheckBox? speechBubbleChoice, subtitleChoice;
    private ComboBox? bubblePlacementChoice;
    private TextBox? bubbleOffsetX, bubbleOffsetY;
    private TextBlock? speechDisplayText;
    private Button? speechPreviewButton;

    /// <summary>Companion › Character's speech bubbles (on by default, shown while the character is) and subtitles: the same
    /// saved choices as the character settings window, applied from Martlet's next sentence. Also where the bubble goes:
    /// following the character's head (default) or staying in one place, nudged by horizontal and vertical offsets.</summary>
    private Border SpeechDisplayCard()
    {
        var prefs = captions.Preferences;
        speechBubbleChoice = new CheckBox { Content = "Speech bubbles", IsChecked = prefs.SpeechBubbles };
        subtitleChoice = new CheckBox
        {
            Content = "Subtitles", IsChecked = prefs.Subtitles,
            Margin = new Thickness(0, 6, 0, 0)
        };
        AutomationProperties.SetAutomationId(speechBubbleChoice, "SetupCharacterSpeechBubbles");
        AutomationProperties.SetAutomationId(subtitleChoice, "SetupCharacterSubtitles");
        speechBubbleChoice.Checked += (_, _) => SaveSpeechDisplay();
        speechBubbleChoice.Unchecked += (_, _) => SaveSpeechDisplay();
        subtitleChoice.Checked += (_, _) => SaveSpeechDisplay();
        subtitleChoice.Unchecked += (_, _) => SaveSpeechDisplay();

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
        return Card(Heading("Speech bubbles and subtitles"), speechBubbleChoice, subtitleChoice,
            Note("Show Martlet's spoken words beside the character or at the bottom of the active screen. Subtitles are hidden from screen capture.",
                new Thickness(0, 6, 0, 0)),
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
        if (showingSpeechDisplay || speechBubbleChoice is null || subtitleChoice is null) return;
        var prefs = captions.Preferences with
        {
            SpeechBubbles = speechBubbleChoice.IsChecked == true, Subtitles = subtitleChoice.IsChecked == true
        };
        if (bubblePlacementChoice is not null) prefs = prefs with { StaticBubble = ReferenceEquals(bubblePlacementChoice.SelectedItem, BubbleStays) };
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
            if (speechBubbleChoice is not null) speechBubbleChoice.IsChecked = prefs.SpeechBubbles;
            if (subtitleChoice is not null) subtitleChoice.IsChecked = prefs.Subtitles;
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
            : $"Preview shown {BubbleWhere(shown)}.");
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

    /// <summary>A persona's response-style mix in words: "always helpful", or "helpful 70%, silly 30%".</summary>
    internal static string StyleMix(ResponseStyleWeights styles)
    {
        var parts = new (string Name, int Weight)[]
        {
            ("helpful", styles.Helpful), ("sarcastic", styles.Sarcastic), ("silly", styles.Silly),
            ("distracted", styles.Distracted), ("playful teasing", styles.PlayfulTeasing)
        }.Where(p => p.Weight > 0).OrderByDescending(p => p.Weight).ToArray();
        var total = parts.Sum(p => p.Weight);
        return parts.Length switch
        {
            0 => "helpful",
            1 => "always " + parts[0].Name,
            _ => string.Join(", ", parts.Select(p => $"{p.Name} {Math.Round(100.0 * p.Weight / total):0}%"))
        };
    }

    private void RenderPersonalityTab(Panel page)
    {
        var companion = homeSettings?.Companion;
        var persona = companion?.ActivePersona;
        var count = companion?.Personas.Count ?? 1;
        page.Children.Add(PageNowCard(persona is null
            ? "Default persona. Style: helpful."
            : $"{persona.Name}{(count > 1 ? $", one of {count} personas" : "")}. Style: {StyleMix(persona.Styles)}.", null));

        page.Children.Add(Card(Heading("Personas"),
            Note("Create, edit or switch personas. Changes save on their own, and the next message uses the chosen persona.", new Thickness(0, 0, 0, 8)),
            Row(PageButton("Edit personality", () => Companion_Click(this, new RoutedEventArgs()), primary: true, id: "OpenCompanion"))));

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
                "Use NVIDIA Audio2Face in Docker for natural mouth movement, or follow the voice's loudness with no setup. Voice audio stays on this PC."),
            (JobPlace.Computer, Label(JobPlace.Computer, "Another of your computers"),
                "Use Audio2Face on a paired computer on your network.")));

        page.Children.Add(place == JobPlace.Computer
            ? ComputersCard(ClusterJobs.LipSync, "Audio2Face", HostRoles.Audio2Face, owner,
                ownerMissing ? "Audio2Face isn't installed there yet, so voice loudness is used for now."
                    : $"In use: Audio2Face{(hostChecks.GetValueOrDefault(owner ?? "")?.Offers?.GetValueOrDefault(HostRoles.Audio2Face) is { } model ? " " + model : "")}.",
                ownerMissing ? "Install Audio2Face" : null, AssignLipSyncAsync,
                "Only Martlet's generated voice is sent to that computer. Audio2Face needs an NVIDIA graphics card; its open-source " +
                "engine needs no NVIDIA account (NVIDIA's NIM engine needs an NGC key). Until it is ready, the mouth follows voice loudness.", thisPc)
            : LocalLipSyncCard(thisPc, handler, owner, fits));
    }

    /// <summary>Lip-sync on this PC: two ways, like its voice. Audio2Face in Martlet's host service here (one click sets the host
    /// service up and pairs it, installs Audio2Face and hands lip-sync to it), or the voice's loudness, which needs no setup.
    /// The one in use, otherwise the one this PC's graphics card suits, comes first. An Audio2Face service you run yourself is
    /// an advanced extra below them, shown as a full option only while it is the setting in effect and answering.</summary>
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

        var dockerTitle = OptionTitle("Audio2Face, with Docker",
            notInstalled ? "chosen, not installed yet" : dockerInUse ? "in use" : fits ? "recommended for this PC" : null);
        AutomationProperties.SetAutomationId(dockerTitle, "LipSyncDockerTitle");
        var dockerAbout = Note("NVIDIA Audio2Face moves the mouth naturally with Martlet's voice. It runs in Docker on this PC with NVIDIA's " +
            "open-source Audio2Face, so no NVIDIA account or key is needed (NVIDIA's NIM, which needs an NGC key, is the other engine " +
            "offered). Until it is ready, voice loudness is used.", new Thickness(0, 2, 0, 6));
        AutomationProperties.SetAutomationId(dockerAbout, "LipSyncDockerAbout");
        var docker = new List<UIElement>
        {
            dockerTitle,
            dockerAbout,
            Note(gpu is null ? "No dedicated graphics card was found on this PC; Audio2Face needs an NVIDIA graphics card with 4 GB or more."
                : $"This PC has {gpu.Describe()}." + (fits ? "" : " Audio2Face needs an NVIDIA graphics card with 4 GB or more, so voice " +
                    "loudness or another computer suits this PC better."), new Thickness(0, 0, 0, 6))
        };
        if (thisPc is null)
        {
            docker.Add(Note((machine.DockerRunning ? "Docker Desktop is running. "
                    : machine.DockerInstalled ? "Docker Desktop is installed. Martlet can start it when needed. "
                    : "Docker Desktop isn't installed yet. Martlet installs it first. ") +
                "Setting this up installs the local lip-sync service and Audio2Face, then uses it for lip-sync.", new Thickness(0, 0, 0, 8)));
            docker.Add(Row(PageButton("Set up Audio2Face with Docker", () => SetUpThisPcHostAsync(AssignLipSyncAsync).Forget(),
                primary: fits, id: "SetupLipSyncHostThisPc")));
        }
        else
        {
            var model = hostChecks.GetValueOrDefault(thisPc.HostId)?.Offers?.GetValueOrDefault(HostRoles.Audio2Face);
            docker.Add(Note(notInstalled
                    ? "Audio2Face is selected but not installed yet. Install it to finish."
                : dockerInUse ? $"In use: Audio2Face on this PC{(model is null ? "" : $", model {model}")}."
                : model is not null ? "Audio2Face is ready on this PC."
                : "Audio2Face isn't ready on this PC yet. Martlet installs it when you choose Use.",
                new Thickness(0, 0, 0, 8)));
            docker.Add(Row(
                PageButton(notInstalled ? "Install Audio2Face" : dockerInUse ? "Set up Audio2Face again" : "Use Audio2Face on this PC",
                    () => AssignLipSyncAsync("host:" + thisPc.HostId).Forget(), primary: notInstalled || fits && !dockerInUse, id: "SetupLipSyncUseLocal"),
                PageButton("Check it", () => RunNodeAction(NodeAction.CheckHost, thisPc.HostId), id: "SetupLipSyncCheckLocal")));
        }

        var loudnessTitle = OptionTitle("Voice loudness, no setup", loudnessInUse ? "in use" : !fits ? "recommended for this PC" : null);
        AutomationProperties.SetAutomationId(loudnessTitle, "LipSyncLoudnessTitle");
        var loudness = new List<UIElement>
        {
            loudnessTitle,
            Note("The mouth opens and closes with Martlet's voice. It works with any character and graphics card, needs nothing installed, " +
                "and nothing leaves this PC. It looks simpler than Audio2Face.", new Thickness(0, 2, 0, 6))
        };
        if (!loudnessInUse)
            loudness.Add(Row(PageButton("Use voice loudness", () => AssignLipSyncAsync("off").Forget(),
                primary: !fits && !dockerInUse && !ownInUse, id: "SetupLipSyncLoudness")));

        var endpoint = OwnLipSyncEndpoint().Authority;
        var ownTitle = OptionTitle("Your own Audio2Face service", !ownSetting ? null
            : ownInUse ? "in use" : ownLipSyncAnswers == false ? "not running" : "checking", ownInUse ? 16 : 14);
        AutomationProperties.SetAutomationId(ownTitle, "LipSyncOwnTitle");
        var own = new List<UIElement> { ownTitle };
        if (ownSetting)
        {
            var state = Note(audio2FaceOnly
                ? $"In use at {endpoint}, with Audio2Face-only lip-sync in the character settings."
                : ownLipSyncAnswers switch
                {
                    true => $"In use: an Audio2Face service you run yourself is answering at {endpoint}.",
                    false => $"Advanced. Martlet looks for an Audio2Face service you run yourself at {endpoint} before each sentence. " +
                        "Nothing answers there, so the mouth follows voice loudness.",
                    _ => $"Advanced. Martlet looks for an Audio2Face service you run yourself at {endpoint} before each sentence, " +
                        "and uses voice loudness when nothing answers."
                }, new Thickness(0, 2, 0, 4));
            AutomationProperties.SetAutomationId(state, "LipSyncOwnState");
            own.Add(state);
        }
        else
        {
            own.Add(Note($"Advanced. Already run NVIDIA's Audio2Face service yourself? Martlet can use it at {endpoint}, " +
                "with voice loudness whenever it doesn't answer.", new Thickness(0, 2, 0, 0)));
            own.Add(Row(PageButton("Use my own service", () => AssignLipSyncAsync("this-pc").Forget(), link: true, id: "SetupLipSyncOwnService")));
        }

        var dockerFirst = dockerInUse || !loudnessInUse && fits;
        var options = new List<UIElement>
        {
            Heading("Lip-sync on this PC"),
            Note("Choose how the mouth moves. Until Audio2Face is ready, the mouth follows voice loudness.", new Thickness(0, 0, 0, 4))
        };
        if (ownInUse) options.Add(Option(own, inUse: true));
        options.Add(Option(dockerFirst ? docker : loudness, dockerFirst ? dockerInUse : loudnessInUse));
        options.Add(Option(dockerFirst ? loudness : docker, dockerFirst ? loudnessInUse : dockerInUse));
        if (!ownInUse)
        {
            var advanced = new StackPanel { Margin = new Thickness(4, 14, 4, 0) };
            foreach (var child in own) advanced.Children.Add(child);
            options.Add(advanced);
        }
        return Card(options.ToArray());
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
            Note("Each exchange (what you typed or said and Martlet's reply) is kept on this PC. When you mention an earlier " +
                "conversation, like \"remember when...\" or \"what did we talk about yesterday?\", Martlet brings back what was said. " +
                "Screen glances and what this PC plays are never recorded.", new Thickness(24, 2, 0, 0)),
            search,
            Note("With a Thinking model that uses tools, Martlet can also look things up in the record whenever it thinks that " +
                "helps. Its search tool makes every request a little longer, so the first reply of a conversation may start a " +
                "little later.", new Thickness(24, 2, 0, 0)),
            Row(PageButton("Open conversation history", History_Click, id: "OpenHistory")));
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
}
