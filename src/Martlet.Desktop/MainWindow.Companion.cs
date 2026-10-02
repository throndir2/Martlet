using System.ComponentModel;
using System.Diagnostics;
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
internal enum CompanionTab { Thinking, Voice, Listening, LipSync, Character, Personality, Lorebook, Memory }

/// <summary>The side list's groups, in order: how it works (where each job runs), who it is (look, personality, what it knows)
/// and what it does (how it answers and acts). A group with no pages yet is not shown.</summary>
internal enum CompanionGroup { HowItWorks, WhoItIs, WhatItDoes }

/// <summary>A conversation model Ollama can download and run on this PC. <paramref name="MinimumVramGb"/> is the GPU memory it
/// needs to run comfortably on the graphics card (0 means any PC).</summary>
internal sealed record LocalChatModel(string Id, string Size, string Fits, double MinimumVramGb);

/// <summary>The Companion page: a side list of pages in groups (How it works: Thinking, Voice, Listening, Lip-sync; Who it is:
/// Character, Personality, Lorebook, Memory). Each job page asks where the job runs (this PC by default, another of your computers, or a
/// cloud provider; voice loudness for lip-sync) and shows only that place's fields, including the API key for a cloud provider.
/// Everything saves through the same setup service, consent and credential rules as Setup.</summary>
public partial class MainWindow
{
    /// <summary>Where a job runs. Lip-sync's third place is the voice's loudness (no Audio2Face) instead of a cloud provider.</summary>
    private enum JobPlace { ThisPc, Computer, Cloud, Loudness }

    private sealed record CloudProvider(string Name, string? BaseUrl, bool Chat, string? DefaultModel, bool NeedsKey)
    {
        public override string ToString() => Name;
    }

    internal const string LocalOllamaBaseUrl = "http://127.0.0.1:11434/v1";

    internal static readonly IReadOnlyList<LocalChatModel> LocalChatModels =
    [
        new("llama3.2:3b", "2.0 GB", "any PC", 0),
        new("qwen2.5:7b", "4.7 GB", "a graphics card with 8 GB or more", 8),
        new("llama3.1:8b", "4.9 GB", "a graphics card with 8 GB or more", 8),
        new("gemma3:12b", "8.1 GB", "a graphics card with 12 GB or more", 12)
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
    private bool savingTab;
    private IReadOnlyList<string>? ollamaModels;
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
        CompanionTab.Thinking or CompanionTab.Voice or CompanionTab.Listening or CompanionTab.LipSync => CompanionGroup.HowItWorks,
        CompanionTab.Character or CompanionTab.Personality or CompanionTab.Lorebook or CompanionTab.Memory => CompanionGroup.WhoItIs,
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
        CompanionTab.Voice => "Voice",
        CompanionTab.Listening => "Listening",
        CompanionTab.LipSync => "Lip-sync",
        CompanionTab.Character => "Character",
        CompanionTab.Personality => "Personality",
        CompanionTab.Lorebook => "Lorebook",
        CompanionTab.Memory => "Memory",
        _ => section.ToString()
    };

    /// <summary>The page's icon in the side list (Segoe Fluent Icons).</summary>
    private static string TabGlyph(CompanionTab section) => section switch
    {
        CompanionTab.Thinking => "\uE82F",
        CompanionTab.Voice => "\uE767",
        CompanionTab.Listening => "\uE720",
        CompanionTab.LipSync => "\uE8BD",
        CompanionTab.Character => "\uE77B",
        CompanionTab.Personality => "\uE76E",
        CompanionTab.Lorebook => "\uE736",
        CompanionTab.Memory => "\uE8F1",
        _ => "\uE76E"
    };

    /// <summary>The line under the page title: what the page decides, in one or two sentences.</summary>
    private static string TabIntro(CompanionTab section) => section switch
    {
        CompanionTab.Thinking => "The conversation model that writes Martlet's replies: where it runs, the provider, the model and its API key. " +
            "It runs on this PC by default, so nothing leaves your computer.",
        CompanionTab.Voice => "How Martlet speaks its replies: where the voice runs, the voice itself and the speakers it plays on. " +
            "By default it speaks on this PC, with the F5 voice in Docker or a Windows voice with no Docker.",
        CompanionTab.Listening => "How Martlet hears you: your microphone and the speech-to-text provider, model and key. " +
            "By default whisper runs on this PC. You can always type instead.",
        CompanionTab.LipSync => "Who moves the character's mouth in time with its voice. It switches right away, even while the character talks.",
        CompanionTab.Character => "What Martlet looks like: the character on your desktop, its model, size, position and motion.",
        CompanionTab.Personality => "Who Martlet is: its personas and how helpful, sarcastic, silly or playful it is, including characters " +
            "from SillyTavern or Chub character cards.",
        CompanionTab.Lorebook => "What Martlet knows about its world: lore entries that are added to a reply when their keywords come up, " +
            "like SillyTavern's World Info. Import SillyTavern lorebooks or the lorebook inside a character card.",
        CompanionTab.Memory => "Facts Martlet remembers about you between conversations.",
        _ => ""
    };

    /// <summary>Recommended local model: the largest in the list this PC's graphics card fits.</summary>
    internal static LocalChatModel RecommendedLocalModel(double? vramGb) =>
        LocalChatModels.Where(m => m.MinimumVramGb <= (vramGb ?? 0)).OrderBy(m => m.MinimumVramGb).LastOrDefault() ?? LocalChatModels[0];

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
        if (SelfHostSetup.IsGateway(route.RouteType) && route.Gateway is { } gateway)
        {
            var engine = route.RouteType switch
            {
                SetupRouteType.GatewayOllama => "Ollama",
                SetupRouteType.GatewayF5 => "F5",
                _ => "whisper"
            };
            return $"{engine} on {(ThisPcHost()?.HostId == gateway.HostId ? "this PC" : gateway.HostId)}";
        }
        return NetworkMap.ProviderName(route);
    }

    private string CharacterModelName()
    {
        if (homeAvatar is null) return "Built-in character";
        return BundledLive2D.IsBuiltIn(homeAvatar.ModelPath)
            ? homeAvatar.ModelPath[BundledLive2D.Prefix.Length..] + " (built-in)"
            : Path.GetFileNameWithoutExtension(homeAvatar.ModelPath);
    }

    private string LipSyncOwnerName() => NetworkMap.LipSync(homeAvatar) switch
    {
        LipSyncHandler.Loudness => "voice loudness",
        LipSyncHandler.Host => homeAvatar!.RemoteHost!.HostId,
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
            case CompanionTab.LipSync: RenderLipSyncTab(body); break;
            case CompanionTab.Character: RenderCharacterTab(body); break;
            case CompanionTab.Personality: RenderPersonalityTab(body); break;
            case CompanionTab.Lorebook: RenderLorebookTab(body); break;
            case CompanionTab.Memory: RenderMemoryTab(body); break;
            default: throw new UnreachableException($"The Companion page {section} has no content.");
        }
    }

    // ---------- job pages ----------

    /// <summary>Routes that run on this PC without Martlet's host service: Ollama for thinking, installed Windows speech and
    /// native whisper.cpp.</summary>
    private static bool RunsHereWithoutHost(SetupRoute route) =>
        IsLocalOllama(route) || route.RouteType is SetupRouteType.LocalWindowsTts or SetupRouteType.LocalWindowsStt or SetupRouteType.LocalWhisper;

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
                SetupRole.Llm => "Ollama runs a free conversation model here. Nothing leaves this PC and there is no per-request charge.",
                SetupRole.Tts => "The natural F5 voice in Docker, or a voice already installed in Windows with no Docker at all. " +
                    "Nothing leaves this PC and there is no per-request charge.",
                _ => "whisper turns your speech into text here. Your voice never leaves this PC."
            }),
            (JobPlace.Computer, "Another of your computers",
                $"Hand {job.Job} to a paired Martlet host on your network, for example a gaming PC. Add one here if you have none yet."),
            (JobPlace.Cloud, "A cloud provider", role == SetupRole.Llm
                ? "OpenAI, OpenRouter, NVIDIA Build or any OpenAI-compatible server, with your API key. May cost money."
                : "OpenAI, with your API key. May cost money.")));

        var otherHosts = NetworkMap.Hosts(Inputs()).Where(h => h.HostId != thisPc?.HostId).ToArray();
        page.Children.Add(place switch
        {
            JobPlace.ThisPc when role == SetupRole.Llm => LocalThinkingCard(route),
            JobPlace.ThisPc when role == SetupRole.Tts => LocalVoiceCard(job, route, thisPc),
            JobPlace.ThisPc => LocalListeningCard(route, thisPc),
            JobPlace.Computer => ComputersCard(job, route, role == SetupRole.Llm ? null : thisPc),
            _ => CloudCard(section, job, route)
        });

        // The Voice Library prepares recordings for self-hosted voices, so it shows only where F5 speaks: this PC's F5 or
        // another of your computers. A cloud provider and a Windows voice have their own voices.
        if (section == CompanionTab.Voice && (place == JobPlace.Computer && otherHosts.Length > 0 ||
                place == JobPlace.ThisPc && route?.RouteType == SetupRouteType.GatewayF5 && thisPc is not null && route.Gateway?.HostId == thisPc.HostId))
            page.Children.Add(VoicesCard());

        if (section == CompanionTab.Voice) page.Children.Add(AudioCard(output: true));
        if (section == CompanionTab.Listening) page.Children.Add(AudioCard(output: false));

        var advanced = PageButton("Advanced setup: every job, stored keys and detached keys", () =>
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
        var status = route is null
            ? section == CompanionTab.Voice
                ? "Not chosen yet. Martlet speaks on this PC by default: set up the F5 voice with Docker, or use a Windows voice with no Docker."
                : "Not chosen yet. This PC is recommended below."
            : (route.RouteType == SetupRouteType.LocalWindowsTts ? "Windows voice on this PC" : $"{PlaceName(route)}: {route.ModelId}") +
                VoiceSuffix(route) +
                (route.Enabled == false ? " (turned off)" : route.Consent is null ? " (not confirmed yet; choose it again below)" : "");
        var now = new StackPanel();
        now.Children.Add(Heading("Now"));
        now.Children.Add(new TextBlock { Text = status, FontSize = 15, TextWrapping = TextWrapping.Wrap });
        if (problem is not null)
        {
            var warning = new TextBlock { Text = $"Not working now: {problem.Problem} {problem.Effect}", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            warning.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            now.Children.Add(warning);
        }
        if (section == CompanionTab.Listening && AudioMissing(output: false))
        {
            var mic = new TextBlock { Text = homeSettings?.Audio?.Input.EndpointId is null
                    ? "No microphone found. Plug one in so Martlet can hear you; you can still type."
                    : "Your chosen microphone isn't connected. Plug it in or pick another below; you can still type.",
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
        var line = Note(text + (output ? " Martlet plays its voice here." : " Martlet listens only while you hold to talk or turn on hands-free."),
            new Thickness(0, 0, 0, 8));
        if (missing) line.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        return Card(Heading(what), line,
            Row(PageButton(missing ? $"Fix {what.ToLowerInvariant()}" : $"Test or change {what.ToLowerInvariant()}",
                () => RunNodeAction(NodeAction.AudioSetup), primary: missing, id: "OpenAudioSetup")));
    }

    private Border VoicesCard() => Card(Heading("Voice Library"),
        Note("F5 speaks in the voice of a short recording. It starts with F5-TTS's published English sample voice, so it works right away; " +
            "Choose another voice hears the voices or records your own. The Voice Library keeps recordings for self-hosted voices on this PC.",
            new Thickness(0, 0, 0, 8)),
        Row(PageButton("Open the Voice Library", () => VoiceLibrary_Click(this, new RoutedEventArgs()), id: "OpenVoiceLibrary")));

    // ---------- this PC: Ollama for thinking ----------

    private Border LocalThinkingCard(SetupRoute? route)
    {
        var installed = !Prerequisites.IsMissing(Prerequisites.Ollama);
        var recommended = RecommendedLocalModel(machine.BestGpu?.MemoryGb);
        var model = new TextBox { MaxLength = 128, Width = 420, HorizontalAlignment = HorizontalAlignment.Left,
            Text = IsLocalOllama(route) ? route!.ModelId : recommended.Id };
        AutomationProperties.SetName(model, "Local model");
        AutomationProperties.SetAutomationId(model, "SetupLocalModel");
        model.TextChanged += (_, _) => tabEdited = true;
        var picks = new ComboBox { Width = 420, HorizontalAlignment = HorizontalAlignment.Left,
            ItemsSource = LocalChatModels.Select(m => $"{m.Id}  ({m.Size}, fits {m.Fits}{(m == recommended ? ", recommended here" : "")})")
                .Concat((ollamaModels ?? []).Where(id => LocalChatModels.All(m => m.Id != id)).Select(id => $"{id}  (downloaded)")).ToArray() };
        AutomationProperties.SetName(picks, "Suggested local models");
        AutomationProperties.SetAutomationId(picks, "SetupLocalModelPicks");
        picks.SelectionChanged += (_, _) => { if (picks.SelectedItem is string pick) model.Text = pick.Split(' ')[0]; };

        var gpu = machine.BestGpu is { } best ? $"This PC has {best.Describe()}." : "No dedicated graphics card was found; small models still run on the processor.";
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), FontWeight = FontWeights.SemiBold,
            Text = !installed ? "Ollama isn't installed on this PC yet."
                : ollamaModels is null ? "Ollama is installed. Check it to see which models are downloaded."
                : ollamaModels.Count == 0 ? "Ollama is running, but no model is downloaded yet."
                : $"Ollama is running with: {string.Join(", ", ollamaModels)}." };
        AutomationProperties.SetAutomationId(status, "SetupOllamaStatus");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);

        string ModelId() => (model.Text ?? "").Trim();
        // Until Ollama is installed, installing it (with the chosen model, then switching to it) is the only step that does anything.
        var buttons = installed
            ? Row(
                PageButton("Download model", () => PullOllamaModelAsync(ModelId()).Forget(), id: "SetupPullModel"),
                PageButton("Check Ollama", () => CheckOllamaAsync().Forget(), id: "SetupCheckOllama"),
                PageButton("Use Ollama on this PC", () => SaveLocalThinkingAsync(ModelId()).Forget(), primary: true, id: "SetupUseLocalThinking"))
            : Row(
                PageButton("Install Ollama and use it", () => InstallOllamaAsync(ModelId()).Forget(), primary: true, id: "SetupInstallOllama"),
                PageButton("Use Ollama on this PC", () => SaveLocalThinkingAsync(ModelId()).Forget(), id: "SetupUseLocalThinking"));

        return Card(Heading("Ollama on this PC"),
            Note($"Martlet talks to Ollama at {LocalOllamaBaseUrl}. Your messages, persona and any memory facts you allow stay on this PC; " +
                "there is no key and no per-request charge. Ollama is free (MIT license) and installs from ollama.com through winget.", new Thickness(0, 0, 0, 8)),
            status,
            new Label { Content = "_Model (any model Ollama serves)", Target = model, Padding = new Thickness(0, 4, 0, 4) },
            model,
            new Label { Content = "_Suggestions", Target = picks, Padding = new Thickness(0, 8, 0, 4) },
            picks,
            Note(gpu + " Prefer instruct/chat models.", new Thickness(0, 8, 0, 10)),
            buttons);
    }

    /// <summary>Downloads a model into this PC's Ollama in a run window (no console), then refreshes what Ollama has.</summary>
    private async Task PullOllamaModelAsync(string model)
    {
        try { ChatCompletionsSetup.ModelId(model); }
        catch (ContractException error) { ActionText.Text = error.Message; return; }
        ActionText.Text = $"Downloading {model} with Ollama; the run window shows the progress.";
        var done = await HostRunWindow.RunAsync(this, $"Download {model}", async run =>
        {
            await LocalOllama.PullAsync(model, run.Status, run.Output, run.Token);
            return $"{model} is downloaded. Choose Use Ollama on this PC to think with it.";
        });
        if (closing) return;
        ActionText.Text = done ?? $"{model} was not downloaded. The run window shows why.";
        if (done is not null) await CheckOllamaAsync();
    }

    /// <summary>One click: installs Ollama with the chosen model (no console) and, once it is there, thinks with it.</summary>
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
        if (ollamaModels?.Contains(model, StringComparer.Ordinal) == true) await SaveLocalThinkingAsync(model);
        else ActionText.Text = $"Ollama is installed, but {model} isn't downloaded yet: choose Download model.";
    }

    /// <summary>Asks the local Ollama (loopback only, on request) which models it has.</summary>
    private async Task CheckOllamaAsync()
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
            ActionText.Text = ollamaModels.Count == 0 ? "Ollama is running on this PC, with no model downloaded yet."
                : $"Ollama is running on this PC with {ollamaModels.Count} model(s).";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            ollamaModels = null;
            ActionText.Text = Prerequisites.IsMissing(Prerequisites.Ollama)
                ? "Ollama isn't installed on this PC yet. Install it first."
                : "Ollama didn't answer on this PC. Start Ollama from the Start menu, then check again.";
        }
        if (!closing && openTab == CompanionTab.Thinking) RenderTab();
    }

    private Task SaveLocalThinkingAsync(string model) => SaveSectionRouteAsync(HostJob.Thinking,
        settings => ChatCompletionsSetup.SelectRoute(settings, LocalOllamaBaseUrl, model), key: null,
        $"Martlet now thinks with {model} in Ollama on this PC. Nothing leaves this PC." +
        (ollamaModels is { } known && !known.Contains(model, StringComparer.Ordinal) ? $" {model} isn't downloaded yet: choose Download model." : ""));

    // ---------- this PC: F5 through this PC's host service, or a Windows voice ----------

    /// <summary>F5 in Martlet's host service on this PC (Docker Desktop). Without the host service, one click sets it up and
    /// pairs it, so this PC also becomes one of your hosts, then installs F5 and switches over, all in one run window.</summary>
    private List<UIElement> HostServiceSteps(HostJob job, SetupRoute? route, PairedHost? thisPc, bool primary)
    {
        var inUse = route?.RouteType == job.RouteType && thisPc is not null && route.Gateway?.HostId == thisPc.HostId;
        var steps = new List<UIElement>();
        if (thisPc is null)
        {
            steps.Add(Note((machine.DockerRunning ? "Docker Desktop is running. "
                    : machine.DockerInstalled ? "Docker Desktop is installed; Martlet starts it when needed. "
                    : "Docker Desktop isn't installed yet; Martlet installs it first. ") +
                $"Setting it up adds Martlet's host service on this PC (this PC then also appears as one of your hosts), installs {job.Engine} " +
                "in it and switches over by itself.", new Thickness(0, 0, 0, 8)));
            steps.Add(Row(PageButton("Set up F5 with Docker", () => UseF5HereAsync().Forget(), primary, id: "SetupHostThisPc")));
            return steps;
        }
        var model = hostChecks.GetValueOrDefault(thisPc.HostId)?.Offers?.GetValueOrDefault(job.HostRoleKind);
        steps.Add(Note(inUse ? $"In use: {job.Engine} on this PC ({thisPc.HostId}){(route!.Reference is { } voice ? $", voice {voice.PresetName}" : "")}."
            : model is not null ? $"This PC's host service runs {job.Engine} ({model})."
            : $"This PC's host service ({thisPc.HostId}) is set up. If it doesn't run {job.Engine} yet, Martlet installs it and switches over by itself.",
            new Thickness(0, 0, 0, 8)));
        steps.Add(Row(
            PageButton(inUse ? "Choose another voice" : "Use F5 on this PC",
                () => (inUse ? AssignJobAsync(job, "host:" + thisPc.HostId) : UseF5HereAsync()).Forget(), primary: primary && !inUse, id: "SetupUseLocal-" + job.Job),
            PageButton("Check it", () => RunNodeAction(NodeAction.CheckHost, thisPc.HostId), id: "SetupCheckLocal-" + job.Job)));
        return steps;
    }

    /// <summary>Its voice on this PC: two ways, each set up by one click. F5 in Docker (through this PC's host service, with
    /// F5-TTS's published sample voice so it speaks right away) or an installed Windows voice (no Docker, no host service).
    /// The one in use, otherwise the one this PC's hardware suits, comes first.</summary>
    private Border LocalVoiceCard(HostJob job, SetupRoute? route, PairedHost? thisPc)
    {
        var gpu = machine.BestGpu;
        var f5Fits = gpu is { IsNvidia: true } && (gpu.MemoryGb ?? 0) >= 6;
        var f5InUse = route?.RouteType == SetupRouteType.GatewayF5 && thisPc is not null && route.Gateway?.HostId == thisPc.HostId;
        var windowsInUse = route?.RouteType == SetupRouteType.LocalWindowsTts;
        var nothingHere = !f5InUse && !windowsInUse;

        var f5 = new List<UIElement>
        {
            OptionTitle("F5 voice, with Docker", f5InUse ? "in use" : nothingHere && f5Fits ? "recommended for this PC" : null),
            Note("A natural voice copied from a short recording. It comes ready with F5-TTS's published English sample voice (MIT licence), " +
                "and you can choose another voice or record your own later. The F5 model is licensed for non-commercial use (CC-BY-NC-4.0).",
                new Thickness(0, 2, 0, 6)),
            Note(gpu is null ? "No dedicated graphics card was found on this PC; F5 needs an NVIDIA graphics card with 6 GB or more."
                : $"This PC has {gpu.Describe()}." + (f5Fits ? "" : " F5 needs an NVIDIA graphics card with 6 GB or more, so a Windows voice suits this PC better."),
                new Thickness(0, 0, 0, 6))
        };
        f5.AddRange(HostServiceSteps(job, route, thisPc, primary: nothingHere && f5Fits));

        var windows = new List<UIElement>
        {
            OptionTitle("Windows voice, no Docker", windowsInUse ? "in use" : nothingHere && !f5Fits ? "recommended for this PC" : null),
            Note("A voice already installed in Windows. Nothing to download and no Docker or host service; it works on any PC and nothing " +
                "leaves it. It sounds more robotic than F5.", new Thickness(0, 2, 0, 6))
        };
        windows.AddRange(WindowsVoiceSteps(route, windowsInUse, primary: nothingHere && !f5Fits));

        var f5First = f5InUse || !windowsInUse && f5Fits;
        return Card(Heading("Its voice on this PC"),
            Note("Choose one; it sets itself up.", new Thickness(0, 0, 0, 4)),
            Option(f5First ? f5 : windows, f5First ? f5InUse : windowsInUse),
            Option(f5First ? windows : f5, f5First ? windowsInUse : f5InUse));
    }

    private IEnumerable<UIElement> WindowsVoiceSteps(SetupRoute? route, bool inUse, bool primary)
    {
        if (windowsVoices is { Count: 0 })
        {
            yield return Note("No voices are installed in Windows on this PC. Add a language with its voice in Windows Settings, then try again.",
                new Thickness(0, 0, 0, 4));
            yield return Row(
                PageButton("Open Windows speech settings", OpenWindowsSpeechSettings, id: "SetupWindowsSpeechSettings"),
                PageButton("Try again", () => UseWindowsVoiceAsync(null).Forget(), id: "SetupWindowsVoiceRetry"));
            yield break;
        }
        if (!inUse)
        {
            yield return Row(PageButton("Use a Windows voice", () => UseWindowsVoiceAsync(null).Forget(), primary, id: "SetupUseWindowsVoice"));
            yield break;
        }
        var voiceId = route!.VoiceId!;
        if (windowsVoices is null)
        {
            yield return Note($"In use: {WindowsVoices.DisplayName(voiceId)}.", new Thickness(0, 0, 0, 4));
            yield return Row(
                PageButton("Change Windows voice", () => FindWindowsVoicesAsync().Forget(), id: "SetupChangeWindowsVoice"),
                PageButton("Hear it", () => PreviewWindowsVoiceAsync(voiceId).Forget(), id: "SetupHearWindowsVoice"));
            yield break;
        }
        var choice = new ComboBox { ItemsSource = windowsVoices, MinHeight = 30, MaxWidth = 420, MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Left,
            SelectedItem = windowsVoices.FirstOrDefault(v => v.Id == voiceId), Margin = new Thickness(0, 4, 0, 0) };
        AutomationProperties.SetName(choice, "Windows voice");
        AutomationProperties.SetAutomationId(choice, "SetupWindowsVoice");
        choice.SelectionChanged += (_, _) =>
        {
            if (choice.SelectedItem is WindowsVoice picked && picked.Id != voiceId) UseWindowsVoiceAsync(picked.Id).Forget();
        };
        yield return choice;
        yield return Row(PageButton("Hear it", () => PreviewWindowsVoiceAsync((choice.SelectedItem as WindowsVoice)?.Id ?? voiceId).Forget(),
            id: "SetupHearWindowsVoice"));
    }

    private static TextBlock OptionTitle(string title, string? tag)
    {
        var text = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        text.Inlines.Add(title);
        if (tag is not null)
        {
            var badge = new System.Windows.Documents.Run("  \u00b7  " + tag) { FontWeight = FontWeights.Normal, FontSize = 14 };
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
                : $"Windows has {windowsVoices.Count} voice(s) installed on this PC.";
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

    /// <summary>Makes Martlet speak with a Windows voice: <paramref name="voiceId"/>, or the one in this PC's language.</summary>
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
        await SaveSectionRouteAsync(HostJob.Speaking, settings => WindowsSpeechSetup.SelectTts(settings, voice.Id), key: null,
            $"Martlet now speaks with the Windows voice {voice} on this PC. Nothing leaves this PC and there is no Docker or host service.");
    }

    private async Task PreviewWindowsVoiceAsync(string voiceId)
    {
        try
        {
            ActionText.Text = "Playing a short sample on Windows' default speakers...";
            await WindowsVoices.PreviewAsync(voiceId, lifetime.Token);
            ActionText.Text = "That's how the Windows voice sounds.";
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
            $"Does the {job.Job} now ({job.Engine} {route?.ModelId}).",
            job.RouteType == SetupRouteType.GatewayF5 ? "Choose another voice" : null,
            key => AssignJobAsync(job, key), job.Disclosure, exclude);

    /// <summary>Every paired host (except <paramref name="exclude"/>) with what it runs and <i>Use it</i>, plus <i>Add a
    /// computer</i>, <i>Check hosts</i> and the Devices map. <paramref name="again"/> labels the owner's button when using it
    /// again does something (choosing another voice); otherwise the owner shows <i>In use</i>.</summary>
    private Border ComputersCard(string job, string engine, string roleKind, string? owner, string ownerDetail, string? again,
        Func<string, Task> assign, string disclosure, PairedHost? exclude)
    {
        var stack = new List<UIElement> { Heading("Your computers") };
        var hosts = NetworkMap.Hosts(Inputs()).Where(h => h.HostId != exclude?.HostId).ToArray();
        if (hosts.Length == 0)
            stack.Add(Note("No other Martlet host is paired yet. Add a computer with a graphics card, such as a gaming PC, then hand " +
                $"{job} to it here. Martlet installs {engine} there when you do.", new Thickness(0, 0, 0, 8)));
        foreach (var host in hosts)
        {
            var check = hostChecks.GetValueOrDefault(host.HostId);
            var model = check?.Offers?.GetValueOrDefault(roleKind);
            var cannot = model is null ? CannotHand(host.HostId, roleKind, job) : null;
            var detail = owner == host.HostId ? ownerDetail
                : cannot is not null ? $"Can't take it now: {cannot}"
                : model is not null ? $"Runs {engine} ({model})."
                : check?.Reachable == true ? $"{engine} isn't installed there yet; Martlet offers to install it."
                : check?.Reachable == false ? "Not reachable right now." : "Not checked yet.";
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = host.HostId, FontSize = 15, FontWeight = FontWeights.SemiBold });
            text.Children.Add(Note(detail, new Thickness(0, 2, 0, 0)));
            var use = PageButton(owner == host.HostId ? again ?? "In use" : "Use it",
                () => assign("host:" + host.HostId).Forget(), primary: owner != host.HostId && cannot is null, id: $"SetupUseHost-{job}-{host.HostId}");
            use.IsEnabled = cannot is null && !(owner == host.HostId && again is null);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            DockPanel.SetDock(use, Dock.Right);
            row.Children.Add(use);
            row.Children.Add(text);
            stack.Add(row);
        }
        stack.Add(Row(
            PageButton("Add a computer", () => RunNodeAction(NodeAction.AddComputer), primary: hosts.Length == 0, id: "SetupAddComputer-" + job),
            hosts.Length == 0 ? null : PageButton("Check hosts", () => RunNodeAction(NodeAction.CheckHost), id: "SetupCheckHosts-" + job),
            PageButton("Open the Devices map", () => Navigate(NavDevices), id: "SetupOpenMap-" + job)));
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
        var hint = Note("", new Thickness(0, 4, 0, 0));
        var consent = new CheckBox { Margin = new Thickness(0, 12, 0, 8) };
        AutomationProperties.SetAutomationId(consent, "SetupCloudConsent-" + section);
        var consentText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        consent.Content = consentText;

        CloudProvider Selected() => provider.SelectedItem as CloudProvider ?? OpenAiCloud;
        bool SameAsSaved(CloudProvider p) => cloudRoute is not null && (p.Chat
            ? cloudRoute.RouteType == SetupRouteType.ChatCompletions && cloudRoute.Origin == (p.BaseUrl is { Length: > 0 } fixedUrl ? fixedUrl : baseUrl.Text.Trim())
            : cloudRoute.RouteType is null or SetupRouteType.OpenAi);
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
            var saved = SameAsSaved(p) && cloudRoute!.CredentialId is not null;
            keySaved = saved;
            RefreshKeyMark();
            keyStatus.Text = saved ? $"Your {p.Name} key is saved in Windows Credential Manager. For security it isn't shown here. Leave this empty to keep it, or paste a new one to replace it."
                : p.NeedsKey ? $"Paste your {p.Name} API key. It is kept in Windows Credential Manager, never in settings."
                : "Optional: only if your server needs a key.";
            hint.Text = p == OpenAiCloud ? (role == SetupRole.Llm ? $"Recommended: {OpenAiTextGenerationCatalog.DefaultModelId}." : "The recommended model is prefilled.")
                : p.BaseUrl == ChatCompletionsEndpointCatalog.OpenRouterBaseUrl ? $"Recommended: {p.DefaultModel}. Any exact OpenRouter model ID works (':free' variants use its free tier). OpenRouter picks the upstream provider; fallback is off."
                : p.BaseUrl == ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl ? $"Recommended: {p.DefaultModel}. Any model ID shown on build.nvidia.com works; keys start with nvapi-."
                : "For example https://api.groq.com/openai/v1, or a local server such as http://127.0.0.1:1234/v1 (LM Studio). HTTP is allowed only for a loopback IP. Enter the exact model ID it serves.";
            consentText.Text = $"I choose {p.Name} for {job.Job}. {job.Sent} go to it, and requests may cost money there. " +
                $"{OpenAiSetup.Boundary(role)} This isn't permission to record or send anything yet.";
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
        baseUrl.TextChanged += (_, _) => { keySaved = SameAsSaved(Selected()) && cloudRoute!.CredentialId is not null; RefreshKeyMark(); };

        var save = PageButton("Save", () => SaveCloudAsync(job, Selected(), baseUrl.Text.Trim(), Selected().Chat ? modelText.Text.Trim() : model.SelectedItem as string ?? "",
            role == SetupRole.Tts ? voice.SelectedItem as string : null, key, consent.IsChecked == true).Forget(), primary: true, id: "SetupCloudSave-" + section);

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
            ActionText.Text = $"Tick the box to confirm you choose {provider.Name} for {job.Job}, then save.";
            return;
        }
        var role = job.Role;
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
                : route.RouteType is null or SetupRouteType.OpenAi);
            if (key is null && provider.NeedsKey && !keySaved)
                throw new ContractException(ErrorCode.InvalidContract, $"Paste your {provider.Name} API key first.");
            await SaveSectionRouteAsync(job, settings => provider.Chat
                    ? ChatCompletionsSetup.SelectRoute(settings, url!, model)
                    : SetupSettings.SelectRoute(settings, role, model, role == SetupRole.Tts ? voice : null),
                key, $"{job.Title} now uses {provider.Name} ({model}{(voice is null ? "" : ", voice " + voice)}).{(key is null ? "" : " Your API key is saved securely in Windows Credential Manager.")} Requests may cost money there.");
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
    /// confirmed choice. A key the route no longer uses is listed for explicit removal in Setup, as there.</summary>
    private async Task SaveSectionRouteAsync(HostJob job, Func<AppSettings, AppSettings> select, SecretLease? key, string done)
    {
        if (store is null || setupService is null || closing) return;
        if (savingTab || assigningRole || setupOperations.IsRunning)
        {
            ActionText.Text = "Another change is still finishing. Try again in a moment.";
            return;
        }
        savingTab = true;
        var role = job.Role;
        var token = lifetime.Token;
        try
        {
            var loaded = await setupService.LoadAsync(token);
            if (loaded.Error is not null) throw new InvalidOperationException(loaded.Error.Summary);
            var settings = UseModels(SetupSettings.Begin(loaded.Settings));
            var revision = loaded.Revision;
            var old = settings.Setup!.Routes.SingleOrDefault(r => r.Role == role);
            var updated = select(settings);
            var chosen = updated.Setup!.Routes.Single(r => r.Role == role);
            if (old is not null && (old.RouteType != chosen.RouteType || old.Origin != chosen.Origin) &&
                settings.Setup.PendingRemovals.Any(removal => removal.Role == role && !SelfHostSetup.IsGateway(removal.Scope?.RouteType)))
                throw new InvalidOperationException($"Remove {job.Job}'s detached key in Advanced setup (Credentials) before switching it to another provider.");
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
            pendingJobHosts.Remove(role);
            pendingJobVoices.Remove(role);
            RecordClusterJob(job.Job, new(null, false));
            ActionText.Text = done + " An open conversation window picks it up on Reload.";
            tabPlace.Remove(openTab ?? CompanionTab.Thinking);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or JsonException)
        {
            ActionText.Text = error.Message;
        }
        finally
        {
            savingTab = false;
            if (!closing)
            {
                tabEdited = false;
                RenderHome();
            }
        }
    }

    // ---------- character ----------

    /// <summary>A page's Now card: what it uses, then any problem that stops it.</summary>
    private Border PageNowCard(string text, Martlet.Core.Platforms.JobCoverage? problem)
    {
        var now = new StackPanel();
        now.Children.Add(Heading("Now"));
        now.Children.Add(new TextBlock { Text = text, FontSize = 15, TextWrapping = TextWrapping.Wrap });
        if (problem is not null)
        {
            var warning = new TextBlock { Text = $"Not working now: {problem.Problem} {problem.Effect}", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            warning.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            now.Children.Add(warning);
        }
        return Card(now);
    }

    private void RenderCharacterTab(Panel page)
    {
        var showing = avatar.IsShowing;
        page.Children.Add(PageNowCard(CharacterModelName() + (homeAvatar is { } profile ? $" ({profile.Renderer})" : "") +
            (showing ? ", on your desktop." : ", hidden."), null));

        page.Children.Add(Card(Heading("Character model"),
            Note("Choose a built-in Live2D character or your own Live2D or VRM model, and tune its size, position and motion.", new Thickness(0, 0, 0, 8)),
            Row(PageButton(showing ? "Hide character" : "Show character", () => RunNodeAction(NodeAction.ToggleCharacter), primary: !showing, id: "SetupCharacterToggle"),
                PageButton("Choose and customize", () => RunNodeAction(NodeAction.Character), id: "OpenAvatar"),
                showing ? PageButton("Reset position", () => ResetCharacterPositionAsync().Forget(), id: "SetupCharacterResetPosition") : null,
                showing ? PageButton("Reset zoom", () => ResetCharacterZoomAsync().Forget(), id: "SetupCharacterResetZoom") : null)));
    }

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
            ? "The default persona, always helpful."
            : $"{persona.Name}{(count > 1 ? $", one of {count} personas" : "")}. Style: {StyleMix(persona.Styles)}.", null));

        page.Children.Add(Card(Heading("Personas"),
            Note("Each persona has its own instructions and style mix: how helpful, sarcastic, silly, distracted or playfully teasing " +
                "it is. Create, edit or switch personas; the next message you send uses the one selected.", new Thickness(0, 0, 0, 8)),
            Row(PageButton("Edit personality", () => Companion_Click(this, new RoutedEventArgs()), primary: true, id: "OpenCompanion"))));

        page.Children.Add(Card(Heading("Character cards"),
            Note("Bring in characters from SillyTavern or Chub (CharacterHub): a PNG card image, a JSON card or a CHARX file " +
                "becomes a new persona you review before saving.", new Thickness(0, 0, 0, 8)),
            Row(PageButton("Import a character card", () => OpenCompanionWindowAsync(importCard: true).Forget(), id: "ImportCharacterCard"))));
    }

    // ---------- lip-sync: where it runs, like every job ----------

    /// <summary>Who moves the character's mouth, chosen like every job: this PC (Audio2Face in its host service, or a service you
    /// run yourself), another of your computers, or the voice's loudness. It switches right away, even while the character talks.</summary>
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
                (ownerMissing ? ", not installed there yet, so the mouth follows the voice's loudness." : "."),
            _ => $"Your own Audio2Face service on this PC ({new Uri(homeAvatar?.Endpoint ?? AvatarProfile.DefaultEndpoint).Authority})."
        }, coverage.FirstOrDefault(c => c.Job == ClusterJobs.LipSync && c.IsProblem)));

        var current = handler switch
        {
            LipSyncHandler.Loudness => JobPlace.Loudness,
            LipSyncHandler.Host when owner != thisPc?.HostId => JobPlace.Computer,
            _ => JobPlace.ThisPc
        };
        var place = tabPlace.TryGetValue(CompanionTab.LipSync, out var chosen) ? chosen : current;
        var gpu = machine.BestGpu;
        var fits = gpu is { IsNvidia: true } && (gpu.MemoryGb ?? 0) >= 4;
        var otherHosts = NetworkMap.Hosts(Inputs()).Where(h => h.HostId != thisPc?.HostId).ToArray();
        var recommended = fits ? JobPlace.ThisPc
            : otherHosts.Any(h => CannotHand(h.HostId, HostRoles.Audio2Face, ClusterJobs.LipSync) is null) ? JobPlace.Computer
            : JobPlace.Loudness;
        string Label(JobPlace value, string label) => value == recommended ? label + " (recommended)" : label;

        page.Children.Add(WhereItRunsCard(CompanionTab.LipSync, "LipSync", "Where it runs", null, current, place,
            (JobPlace.ThisPc, Label(JobPlace.ThisPc, "This PC"),
                "NVIDIA Audio2Face looks most natural. It needs an NVIDIA graphics card with 4 GB or more and a free NVIDIA NGC key. " +
                "Only the generated voice is used, and nothing leaves this PC."),
            (JobPlace.Computer, Label(JobPlace.Computer, "Another of your computers"),
                "Hand lip-sync to a paired Martlet host on your network, for example a gaming PC. Add one here if you have none yet."),
            (JobPlace.Loudness, Label(JobPlace.Loudness, "Voice loudness"),
                "No Audio2Face: the mouth opens and closes with the voice's loudness. Works with any character on any PC, with nothing to set up.")));

        page.Children.Add(place switch
        {
            JobPlace.ThisPc => LocalLipSyncCard(thisPc, handler, owner, fits),
            JobPlace.Computer => ComputersCard(ClusterJobs.LipSync, "Audio2Face", HostRoles.Audio2Face, owner,
                ownerMissing ? "Has the lip-sync, but Audio2Face isn't installed there yet, so the mouth follows the voice's loudness."
                    : $"Does the lip-sync now (Audio2Face{(hostChecks.GetValueOrDefault(owner ?? "")?.Offers?.GetValueOrDefault(HostRoles.Audio2Face) is { } model ? " " + model : "")}).",
                ownerMissing ? "Install Audio2Face" : null, AssignLipSyncAsync,
                "Only Martlet's generated voice then goes to that computer, over its pinned TLS gateway; no microphone audio, keys or files. " +
                "Audio2Face there needs an NVIDIA graphics card with 4 GB or more and a free NVIDIA NGC key. Until it is ready, and whenever it " +
                "doesn't answer, the mouth follows the voice's loudness.", thisPc),
            _ => LoudnessCard(handler == LipSyncHandler.Loudness)
        });
    }

    /// <summary>Lip-sync on this PC: two ways, like its voice. Audio2Face in Martlet's host service here (one click sets the host
    /// service up and pairs it, installs Audio2Face and hands lip-sync to it), or an Audio2Face service you already run yourself.
    /// Docker's Audio2Face comes first unless your own service is in use and this PC's graphics card can't run it.</summary>
    private Border LocalLipSyncCard(PairedHost? thisPc, LipSyncHandler handler, string? owner, bool fits)
    {
        var gpu = machine.BestGpu;
        var dockerInUse = handler == LipSyncHandler.Host && thisPc is not null && owner == thisPc.HostId;
        var ownInUse = handler == LipSyncHandler.ThisPc;
        // Lip-sync can be handed to this PC's host service before Audio2Face is installed there (or after it was removed).
        var notInstalled = dockerInUse && HostServes(thisPc!.HostId, ClusterJobs.LipSync, HostRoles.Get(HostRoles.Audio2Face).RouteId) == false;

        var docker = new List<UIElement>
        {
            OptionTitle("Audio2Face, with Docker", notInstalled ? "chosen, not installed yet" : dockerInUse ? "in use" : fits ? "recommended for this PC" : null),
            Note("NVIDIA Audio2Face-3D moves the mouth and face in time with Martlet's generated voice. It runs in Martlet's host service " +
                "on this PC, inside Docker Desktop, and needs a free NVIDIA NGC API key, which you enter when it installs. Until it is " +
                "ready, the mouth follows the voice's loudness.", new Thickness(0, 2, 0, 6)),
            Note(gpu is null ? "No dedicated graphics card was found on this PC; Audio2Face needs an NVIDIA graphics card with 4 GB or more."
                : $"This PC has {gpu.Describe()}." + (fits ? "" : " Audio2Face needs an NVIDIA graphics card with 4 GB or more, so another " +
                    "computer or voice loudness suits this PC better."), new Thickness(0, 0, 0, 6))
        };
        if (thisPc is null)
        {
            docker.Add(Note((machine.DockerRunning ? "Docker Desktop is running. "
                    : machine.DockerInstalled ? "Docker Desktop is installed; Martlet starts it when needed. "
                    : "Docker Desktop isn't installed yet; Martlet offers to install it first. ") +
                "Setting it up adds Martlet's host service on this PC (this PC then also appears as one of your hosts), installs Audio2Face " +
                "in it and hands lip-sync to it.", new Thickness(0, 0, 0, 8)));
            docker.Add(Row(PageButton("Set up Audio2Face with Docker", () => SetUpThisPcHostAsync(AssignLipSyncAsync).Forget(),
                primary: fits, id: "SetupLipSyncHostThisPc")));
        }
        else
        {
            var model = hostChecks.GetValueOrDefault(thisPc.HostId)?.Offers?.GetValueOrDefault(HostRoles.Audio2Face);
            docker.Add(Note(notInstalled
                    ? $"Lip-sync is handed to this PC's host service ({thisPc.HostId}), but Audio2Face isn't installed in it yet, so the " +
                      "mouth follows the voice's loudness. Install it to finish; Martlet asks for your NVIDIA NGC API key and switches over by itself."
                : dockerInUse ? $"In use: Audio2Face in this PC's host service ({thisPc.HostId}){(model is null ? "" : $", model {model}")}."
                : model is not null ? $"This PC's host service runs Audio2Face ({model})."
                : $"This PC's host service ({thisPc.HostId}) is set up. If it doesn't run Audio2Face yet, Martlet installs it and switches over by itself.",
                new Thickness(0, 0, 0, 8)));
            docker.Add(Row(
                PageButton(notInstalled ? "Install Audio2Face" : dockerInUse ? "Set up Audio2Face again" : "Use Audio2Face on this PC",
                    () => AssignLipSyncAsync("host:" + thisPc.HostId).Forget(), primary: notInstalled || fits && !dockerInUse, id: "SetupLipSyncUseLocal"),
                PageButton("Check it", () => RunNodeAction(NodeAction.CheckHost, thisPc.HostId), id: "SetupLipSyncCheckLocal")));
        }

        var endpoint = new Uri(homeAvatar?.Endpoint ?? AvatarProfile.DefaultEndpoint).Authority;
        var own = new List<UIElement>
        {
            OptionTitle("Your own Audio2Face service", ownInUse ? "in use" : null),
            Note($"An Audio2Face-3D service you already run on this PC at {endpoint}, without Martlet's host service. Martlet uses it " +
                "whenever it answers; otherwise the mouth follows the voice's loudness.", new Thickness(0, 2, 0, 6))
        };
        if (ownInUse)
            own.Add(Note(homeAvatar?.LipSync == AvatarLipSync.Audio2Face
                ? "In use, with Audio2Face-only lip-sync activated in the character's settings (Choose and customize)."
                : $"In use: Martlet checks {endpoint} before each sentence.", new Thickness(0, 0, 0, 4)));
        else own.Add(Row(PageButton("Use my own service", () => AssignLipSyncAsync("this-pc").Forget(), id: "SetupLipSyncOwnService")));

        // The own service is the default fallback, so it comes first only when it is in use and Docker's Audio2Face doesn't fit here.
        var dockerFirst = dockerInUse || !ownInUse || fits;
        return Card(Heading("Lip-sync on this PC"),
            Note("Choose one. Until Audio2Face answers, the mouth follows the voice's loudness.", new Thickness(0, 0, 0, 4)),
            Option(dockerFirst ? docker : own, dockerFirst ? dockerInUse : ownInUse),
            Option(dockerFirst ? own : docker, dockerFirst ? ownInUse : dockerInUse));
    }

    private Border LoudnessCard(bool inUse) => Card(Heading("Voice loudness"),
        Note("The mouth opens and closes with how loud Martlet's voice is. It works with every character on any PC, with nothing to " +
            "install and nothing sent anywhere. It looks simpler than Audio2Face.", new Thickness(0, 0, 0, 8)),
        inUse ? (UIElement)Note("In use: Audio2Face is off.", new Thickness(0, 0, 0, 4))
            : Row(PageButton("Use voice loudness", () => AssignLipSyncAsync("off").Forget(), primary: true, id: "SetupLipSyncLoudness")));

    // ---------- memory ----------

    private void RenderMemoryTab(Panel page)
    {
        var on = homeSettings?.Memory?.Enabled == true;
        page.Children.Add(Card(Heading("Now"),
            new TextBlock { Text = on ? "On" : "Off", FontSize = 15, TextWrapping = TextWrapping.Wrap },
            Note(on
                ? "Martlet remembers lasting things you talk about (your name, people and pets in your life, likes, plans...) and recalls them " +
                  "in later conversations. After each reply the Thinking model picks out what is worth keeping. Everything stays on this PC; " +
                  "review, edit or delete any fact, or turn memory off, in Manage memory."
                : "Martlet doesn't remember or recall anything between conversations. Turn memory on in Manage memory.",
                new Thickness(0, 2, 0, 8)),
            Row(PageButton("Manage memory", () => Memory_Click(this, new RoutedEventArgs()), primary: !on, id: "OpenMemory"))));
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
