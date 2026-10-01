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
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>The setup pages Home links to. Each is one in-window page.</summary>
internal enum SetupSection { Thinking, Voice, Listening, Character }

/// <summary>A conversation model Ollama can download and run on this PC. <paramref name="MinimumVramGb"/> is the GPU memory it
/// needs to run comfortably on the graphics card (0 means any PC).</summary>
internal sealed record LocalChatModel(string Id, string Size, string Fits, double MinimumVramGb);

/// <summary>The setup pages: How Martlet thinks, Its voice, How it listens and Character. Each job page asks where the job runs
/// (this PC by default, another of your computers, or a cloud provider) and shows only that place's fields, including the API
/// key for a cloud provider. Everything saves through the same setup service, consent and credential rules as Setup.</summary>
public partial class MainWindow
{
    private enum JobPlace { ThisPc, Computer, Cloud }

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

    private SetupSection? openSection;
    private bool sectionEdited;
    private bool savingSection;
    private IReadOnlyList<string>? ollamaModels;
    private readonly Dictionary<SetupSection, JobPlace> sectionPlace = [];

    private static SetupRole RoleOf(SetupSection section) => section switch
    {
        SetupSection.Voice => SetupRole.Tts,
        SetupSection.Listening => SetupRole.Stt,
        _ => SetupRole.Llm
    };

    private static string SectionTitle(SetupSection section) => section switch
    {
        SetupSection.Thinking => "How Martlet thinks",
        SetupSection.Voice => "Its voice",
        SetupSection.Listening => "How it listens",
        _ => "Character"
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

    private void OpenSection(SetupSection section)
    {
        if (SetupPage is null) return;
        openSection = section;
        sectionPlace.Remove(section);
        foreach (var nav in new[] { NavHome, NavDevices, NavCompanion, NavSettings }) nav.IsChecked = false;
        foreach (var page in new FrameworkElement[] { HomePage, DevicesPage, CompanionPage, SettingsPage }) page.Visibility = Visibility.Collapsed;
        SetupPage.Visibility = Visibility.Visible;
        SetupPage.ScrollToTop();
        RenderSection();
        Motion.Enter(SetupPage);
    }

    private void RenderSection()
    {
        if (openSection is not { } section) return;
        sectionEdited = false;
        var page = SetupPageContent;
        page.Children.Clear();

        var back = PageButton("\u2190 Your setup", () => Navigate(NavHome), link: true, id: "SetupSectionBack");
        back.HorizontalAlignment = HorizontalAlignment.Left;
        page.Children.Add(back);

        var tabs = new WrapPanel { Margin = new Thickness(0, 10, 0, 16) };
        AutomationProperties.SetName(tabs, "Setup pages");
        foreach (var other in Enum.GetValues<SetupSection>())
        {
            var tab = PageButton(SectionTitle(other), () => OpenSection(other), primary: other == section, id: "SetupTab-" + other);
            tab.Margin = new Thickness(0, 0, 8, 6);
            tabs.Children.Add(tab);
        }
        page.Children.Add(tabs);

        var title = new TextBlock { Text = SectionTitle(section) };
        title.SetResourceReference(StyleProperty, "PageTitle");
        page.Children.Add(title);
        page.Children.Add(Note(section switch
        {
            SetupSection.Thinking => "The conversation model that writes Martlet's replies: where it runs, the provider, the model and its API key. " +
                "It runs on this PC by default, so nothing leaves your computer.",
            SetupSection.Voice => "How Martlet speaks its replies: the text-to-speech provider, model, voice and key, plus the speakers it plays on. " +
                "By default the F5 voice runs on this PC.",
            SetupSection.Listening => "How Martlet hears you: your microphone and the speech-to-text provider, model and key. " +
                "By default whisper runs on this PC. You can always type instead.",
            _ => "What Martlet looks like and who it is: the character model on your desktop, its personality and who moves its lips."
        }, new Thickness(0, 4, 0, 16)));

        if (section == SetupSection.Character) RenderCharacterSection(page);
        else RenderJobSection(page, section);
    }

    // ---------- job pages ----------

    private void RenderJobSection(Panel page, SetupSection section)
    {
        var role = RoleOf(section);
        var job = HostJob.For(role)!;
        var route = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == role);
        var thisPc = ThisPcHost();
        var current = route is null ? JobPlace.ThisPc
            : role == SetupRole.Llm && IsLocalOllama(route) ? JobPlace.ThisPc
            : SelfHostSetup.IsGateway(route.RouteType)
                ? route.Gateway?.HostId == thisPc?.HostId && role != SetupRole.Llm ? JobPlace.ThisPc : JobPlace.Computer
                : JobPlace.Cloud;
        var place = sectionPlace.TryGetValue(section, out var chosen) ? chosen : current;

        if (section == SetupSection.Voice) page.Children.Add(AudioCard(output: true));
        if (section == SetupSection.Listening) page.Children.Add(AudioCard(output: false));

        var problem = coverage.FirstOrDefault(c => c.Job == job.Job && c.IsProblem);
        var status = route is null ? "Not chosen yet. This PC is recommended below."
            : $"{PlaceName(route)}: {route.ModelId}" +
                (route.Reference is { } reference ? $", voice {reference.PresetName}" : route.VoiceId is { } voice ? $", voice {voice}" : "") +
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
        page.Children.Add(Card(now));

        var where = new StackPanel();
        where.Children.Add(Heading("Where it runs"));
        void Place(JobPlace value, string label, string detail)
        {
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = label, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            text.Children.Add(Note(detail, new Thickness(0, 2, 0, 0)));
            var option = new RadioButton { Content = text, GroupName = "Place-" + section, IsChecked = value == place, Margin = new Thickness(0, 0, 0, 10) };
            AutomationProperties.SetName(option, $"{label}: {detail}");
            AutomationProperties.SetAutomationId(option, $"Place-{section}-{value}");
            option.Checked += (_, _) =>
            {
                sectionPlace[section] = value;
                RenderSection();
            };
            where.Children.Add(option);
        }
        Place(JobPlace.ThisPc, "This PC (recommended)", role switch
        {
            SetupRole.Llm => "Ollama runs a free conversation model here. Nothing leaves this PC and there is no per-request charge.",
            SetupRole.Tts => "The F5 voice runs here, cloned from a recording you may use. Needs an NVIDIA graphics card with 6 GB or more.",
            _ => "whisper turns your speech into text here. Your voice never leaves this PC."
        });
        Place(JobPlace.Computer, "Another of your computers",
            $"Hand {job.Job} to a paired Martlet host on your network, for example a gaming PC. Add one here if you have none yet.");
        Place(JobPlace.Cloud, "A cloud provider", role == SetupRole.Llm
            ? "OpenAI, OpenRouter, NVIDIA Build or any OpenAI-compatible server, with your API key. May cost money."
            : "OpenAI, with your API key. May cost money.");
        page.Children.Add(Card(where));

        page.Children.Add(place switch
        {
            JobPlace.ThisPc when role == SetupRole.Llm => LocalThinkingCard(route),
            JobPlace.ThisPc => LocalHostJobCard(job, route, thisPc),
            JobPlace.Computer => ComputersCard(job, route, role == SetupRole.Llm ? null : thisPc),
            _ => CloudCard(section, job, route)
        });

        if (section == SetupSection.Voice)
        {
            var library = PageButton("Voice Library", () => VoiceLibrary_Click(this, new RoutedEventArgs()), id: "SetupVoiceLibrary");
            page.Children.Add(Card(Heading("Voices"),
                Note("Prepare and keep voice samples locally. F5 asks which voice to use when it takes over speaking.", new Thickness(0, 0, 0, 8)),
                Row(library)));
        }

        var advanced = PageButton("Advanced setup: every job, stored keys and detached keys", () =>
        {
            nextSetupJob = role;
            RunNodeAction(NodeAction.Setup);
        }, link: true, id: "SetupAdvanced-" + section);
        advanced.HorizontalAlignment = HorizontalAlignment.Left;
        advanced.Margin = new Thickness(0, 4, 0, 0);
        page.Children.Add(advanced);
    }

    private Border AudioCard(bool output)
    {
        var audio = homeSettings?.Audio;
        var tested = output ? audio?.Output.Checkpoint is not null : audio?.Input.Checkpoint is not null;
        var what = output ? "Speakers" : "Microphone";
        var text = tested ? $"{what} chosen and tested on this PC." : audio is not null ? $"{what} chosen, not tested yet." : $"No {what.ToLowerInvariant()} chosen yet.";
        return Card(Heading(what),
            Note(text + (output ? " Martlet plays its voice here." : " Martlet listens only while you hold to talk or turn on hands-free.") +
                " Choosing and testing devices stays on this PC.", new Thickness(0, 0, 0, 8)),
            Row(PageButton(tested ? $"Change {what.ToLowerInvariant()}" : $"Choose and test {what.ToLowerInvariant()}",
                () => RunNodeAction(NodeAction.AudioSetup), primary: !tested, id: "SetupAudio-" + what)));
    }

    // ---------- this PC: Ollama for thinking ----------

    private Border LocalThinkingCard(SetupRoute? route)
    {
        var installed = !Prerequisites.IsMissing(Prerequisites.Ollama);
        var recommended = RecommendedLocalModel(machine.BestGpu?.MemoryGb);
        var model = new TextBox { MaxLength = 128, Width = 420, HorizontalAlignment = HorizontalAlignment.Left,
            Text = IsLocalOllama(route) ? route!.ModelId : recommended.Id };
        AutomationProperties.SetName(model, "Local model");
        AutomationProperties.SetAutomationId(model, "SetupLocalModel");
        model.TextChanged += (_, _) => sectionEdited = true;
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
        var buttons = Row(
            installed ? null : PageButton("Install Ollama", () => ActionText.Text = Prerequisites.Launch([Prerequisites.Ollama]), primary: true, id: "SetupInstallOllama"),
            PageButton("Download model", () => PullOllamaModel(ModelId()), id: "SetupPullModel"),
            PageButton("Check Ollama", () => _ = CheckOllamaAsync(), id: "SetupCheckOllama"),
            PageButton("Use Ollama on this PC", () => _ = SaveLocalThinkingAsync(ModelId()), primary: installed, id: "SetupUseLocalThinking"));

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

    private void PullOllamaModel(string model)
    {
        try
        {
            ChatCompletionsSetup.ModelId(model);
            var ollama = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Ollama", "ollama.exe")
            }.FirstOrDefault(File.Exists) ?? "ollama";
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/k \"\"{ollama}\" pull {model}\"")
                { UseShellExecute = true })?.Dispose();
            ActionText.Text = $"Downloading {model} with Ollama in a console window. When it finishes, choose Use Ollama on this PC.";
        }
        catch (ContractException error) { ActionText.Text = error.Message; }
        catch (Exception error) when (error is Win32Exception or IOException) { ActionText.Text = error.Message; }
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
        if (!closing && openSection == SetupSection.Thinking) RenderSection();
    }

    private Task SaveLocalThinkingAsync(string model) => SaveSectionRouteAsync(HostJob.Thinking,
        settings => ChatCompletionsSetup.SelectRoute(settings, LocalOllamaBaseUrl, model), key: null,
        $"Martlet now thinks with {model} in Ollama on this PC. Nothing leaves this PC." +
        (ollamaModels is { } known && !known.Contains(model, StringComparer.Ordinal) ? $" {model} isn't downloaded yet: choose Download model." : ""));

    // ---------- this PC: F5 and whisper through this PC's host service ----------

    private Border LocalHostJobCard(HostJob job, SetupRoute? route, PairedHost? thisPc)
    {
        var f5 = job.RouteType == SetupRouteType.GatewayF5;
        var gpu = machine.BestGpu;
        var hardware = gpu is null ? "No dedicated graphics card was found on this PC."
            : $"This PC has {gpu.Describe()}." + (f5 && !(gpu.IsNvidia && (gpu.MemoryGb ?? 0) >= 6)
                ? " F5 needs an NVIDIA graphics card with 6 GB or more, so choose another computer or the cloud instead." : "");
        var stack = new List<UIElement>
        {
            Heading(f5 ? "F5 voice on this PC" : "whisper on this PC"),
            Note((f5
                ? "F5 speaks every reply in a voice cloned from a short recording you are allowed to use. Each reply's text stays on this PC. The F5 model is licensed for non-commercial use (CC-BY-NC-4.0). "
                : "whisper transcribes what you say in memory on this PC and stores nothing. ") +
                "It runs in Martlet's host service on this PC, inside Docker Desktop.", new Thickness(0, 0, 0, 8)),
            Note(hardware, new Thickness(0, 0, 0, 8))
        };
        var inUse = route?.Gateway?.HostId is { } host && host == thisPc?.HostId;
        if (thisPc is null)
        {
            stack.Add(Note((machine.DockerRunning ? "Docker Desktop is running. "
                    : machine.DockerInstalled ? "Docker Desktop is installed but not running. Start it first. "
                    : "Docker Desktop isn't installed yet. ") +
                $"First set up Martlet's host service on this PC (once). Then come back and choose Use {job.Engine} on this PC; Martlet installs {job.Engine} there when you do.",
                new Thickness(0, 0, 0, 8)));
            stack.Add(Row(
                machine.DockerInstalled ? null : PageButton("Install Docker Desktop", () => ActionText.Text = Prerequisites.Launch([Prerequisites.DockerDesktop]), id: "SetupInstallDocker"),
                PageButton("Set up this PC's host service", () => RunNodeAction(NodeAction.HostThisPc), primary: true, id: "SetupHostThisPc")));
        }
        else
        {
            var model = hostChecks.GetValueOrDefault(thisPc.HostId)?.Offers?.GetValueOrDefault(job.HostRoleKind);
            stack.Add(Note(inUse ? $"In use: {job.Engine} on this PC ({thisPc.HostId})."
                : model is not null ? $"This PC's host service runs {job.Engine} ({model})."
                : $"This PC's host service ({thisPc.HostId}) is paired. If it doesn't run {job.Engine} yet, Martlet offers to install it.",
                new Thickness(0, 0, 0, 8)));
            stack.Add(Row(
                PageButton(inUse ? (f5 ? "Choose another voice" : $"Set up {job.Engine} again") : $"Use {job.Engine} on this PC",
                    () => _ = AssignJobAsync(job, "host:" + thisPc.HostId), primary: !inUse, id: "SetupUseLocal-" + job.Job),
                PageButton("Check it", () => RunNodeAction(NodeAction.CheckHost, thisPc.HostId), id: "SetupCheckLocal-" + job.Job)));
        }
        return Card([.. stack]);
    }

    // ---------- another of your computers ----------

    private Border ComputersCard(HostJob job, SetupRoute? route, PairedHost? exclude)
    {
        var stack = new List<UIElement> { Heading("Your computers") };
        var hosts = NetworkMap.Hosts(Inputs()).Where(h => h.HostId != exclude?.HostId).ToArray();
        var owner = NetworkMap.JobHost(homeSettings, job.Role);
        if (hosts.Length == 0)
            stack.Add(Note("No other Martlet host is paired yet. Add a computer with a graphics card, such as a gaming PC, then hand " +
                $"{job.Job} to it here. Martlet installs {job.Engine} there when you do.", new Thickness(0, 0, 0, 8)));
        foreach (var host in hosts)
        {
            var check = hostChecks.GetValueOrDefault(host.HostId);
            var model = check?.Offers?.GetValueOrDefault(job.HostRoleKind);
            var cannot = model is null ? CannotHand(host.HostId, job.HostRoleKind, job.Job) : null;
            var detail = owner == host.HostId ? $"Does the {job.Job} now ({job.Engine} {route?.ModelId})."
                : cannot is not null ? $"Can't take it now: {cannot}"
                : model is not null ? $"Runs {job.Engine} ({model})."
                : check?.Reachable == true ? $"{job.Engine} isn't installed there yet; Martlet offers to install it."
                : check?.Reachable == false ? "Not reachable right now." : "Not checked yet.";
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = host.HostId, FontSize = 15, FontWeight = FontWeights.SemiBold });
            text.Children.Add(Note(detail, new Thickness(0, 2, 0, 0)));
            var use = PageButton(owner == host.HostId ? (job.RouteType == SetupRouteType.GatewayF5 ? "Choose another voice" : "In use") : "Use it",
                () => _ = AssignJobAsync(job, "host:" + host.HostId), primary: owner != host.HostId && cannot is null, id: $"SetupUseHost-{job.Job}-{host.HostId}");
            use.IsEnabled = cannot is null && !(owner == host.HostId && job.RouteType != SetupRouteType.GatewayF5);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            DockPanel.SetDock(use, Dock.Right);
            row.Children.Add(use);
            row.Children.Add(text);
            stack.Add(row);
        }
        stack.Add(Row(
            PageButton("Add a computer", () => RunNodeAction(NodeAction.AddComputer), primary: hosts.Length == 0, id: "SetupAddComputer-" + job.Job),
            hosts.Length == 0 ? null : PageButton("Check hosts", () => RunNodeAction(NodeAction.CheckHost), id: "SetupCheckHosts-" + job.Job),
            PageButton("Open the Devices map", () => Navigate(NavDevices), id: "SetupOpenMap-" + job.Job)));
        stack.Add(Note(job.Disclosure, new Thickness(0, 8, 0, 0)));
        return Card([.. stack]);
    }

    // ---------- cloud provider ----------

    private Border CloudCard(SetupSection section, HostJob job, SetupRoute? route)
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
            keyStatus.Text = saved ? "A key is saved for this provider. Leave this empty to keep it, or paste a new one to replace it."
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
        provider.SelectionChanged += (_, _) => { sectionEdited = true; Refresh(keepModel: false); };
        modelText.TextChanged += (_, _) => { if (!modelText.IsKeyboardFocusWithin) return; sectionEdited = true; consent.IsChecked = false; };
        model.SelectionChanged += (_, _) => { sectionEdited = true; consent.IsChecked = false; };
        baseUrl.TextChanged += (_, _) => { sectionEdited = true; consent.IsChecked = false; };
        voice.SelectionChanged += (_, _) => { sectionEdited = true; consent.IsChecked = false; };
        key.PasswordChanged += (_, _) => sectionEdited = true;

        var save = PageButton("Save", () => _ = SaveCloudAsync(job, Selected(), baseUrl.Text.Trim(), Selected().Chat ? modelText.Text.Trim() : model.SelectedItem as string ?? "",
            role == SetupRole.Tts ? voice.SelectedItem as string : null, key, consent.IsChecked == true), primary: true, id: "SetupCloudSave-" + section);

        var stack = new List<UIElement>
        {
            Heading("Cloud provider"),
            new Label { Content = "_Provider", Target = provider, Padding = new Thickness(0, 0, 0, 4) },
            provider,
            baseUrlPanel,
            new Label { Content = "_Model", Target = model, Padding = new Thickness(0, 8, 0, 4) },
            model,
            modelText,
            hint
        };
        if (role == SetupRole.Tts)
        {
            stack.Add(new Label { Content = "_Voice", Target = voice, Padding = new Thickness(0, 8, 0, 4) });
            stack.Add(voice);
        }
        stack.Add(new Label { Content = "API _key", Target = key, Padding = new Thickness(0, 8, 0, 4) });
        stack.Add(key);
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
                key, $"{job.Title} now uses {provider.Name} ({model}{(voice is null ? "" : ", voice " + voice)}). Requests may cost money there.");
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

    /// <summary>Saves a job's route chosen on a setup page: the route, then its key (which resets consent), then the user's
    /// confirmed choice. A key the route no longer uses is listed for explicit removal in Setup, as there.</summary>
    private async Task SaveSectionRouteAsync(HostJob job, Func<AppSettings, AppSettings> select, SecretLease? key, string done)
    {
        if (store is null || setupService is null || closing) return;
        if (savingSection || assigningRole || setupOperations.IsRunning)
        {
            ActionText.Text = "Another change is still finishing. Try again in a moment.";
            return;
        }
        savingSection = true;
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
            sectionPlace.Remove(openSection ?? SetupSection.Thinking);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or JsonException)
        {
            ActionText.Text = error.Message;
        }
        finally
        {
            savingSection = false;
            if (!closing)
            {
                sectionEdited = false;
                RenderHome();
            }
        }
    }

    // ---------- character ----------

    private void RenderCharacterSection(Panel page)
    {
        var showing = avatar.IsShowing;
        page.Children.Add(Card(Heading("Character model"),
            new TextBlock { Text = CharacterModelName() + (homeAvatar is { } profile ? $" ({profile.Renderer})" : ""), FontSize = 15, TextWrapping = TextWrapping.Wrap },
            Note((showing ? "It is on your desktop now. " : "") +
                "Choose a built-in Live2D character or your own Live2D or VRM model, and tune its size, position and motion.", new Thickness(0, 2, 0, 8)),
            Row(PageButton(showing ? "Hide" : "Show", () => RunNodeAction(NodeAction.ToggleCharacter), primary: !showing, id: "SetupCharacterToggle"),
                PageButton("Choose and customize", () => RunNodeAction(NodeAction.Character), id: "SetupCharacterCustomize"),
                showing ? PageButton("Reset position", () => _ = ResetCharacterPositionAsync(), id: "SetupCharacterResetPosition") : null,
                showing ? PageButton("Reset zoom", () => _ = ResetCharacterZoomAsync(), id: "SetupCharacterResetZoom") : null)));

        var persona = homeSettings?.Companion?.ActivePersona;
        page.Children.Add(Card(Heading("Personality"),
            new TextBlock { Text = persona?.Name ?? "Default", FontSize = 15, TextWrapping = TextWrapping.Wrap },
            Note("Its personas and how playful, helpful or silly it is. Memory keeps facts it may remember (off by default).", new Thickness(0, 2, 0, 8)),
            Row(PageButton("Edit personality", () => Companion_Click(this, new RoutedEventArgs()), primary: true, id: "SetupPersonality"),
                PageButton("Memory", () => Memory_Click(this, new RoutedEventArgs()), id: "SetupMemory"))));

        var hosts = NetworkMap.Hosts(Inputs());
        renderingBoard = true;
        ComboBox choice;
        try { choice = LipSyncChoice(); }
        finally { renderingBoard = false; }
        choice.MaxWidth = 420;
        choice.HorizontalAlignment = HorizontalAlignment.Left;
        page.Children.Add(Card(Heading("Lip-sync"),
            Note("Who moves the character's mouth with its voice. Audio2Face looks most natural and needs an NVIDIA graphics card, on this PC or " +
                "another computer; otherwise the mouth follows the voice's loudness. It switches right away, even while the character talks.",
                new Thickness(0, 0, 0, 8)),
            choice,
            Row(PageButton("Add a computer", () => RunNodeAction(NodeAction.AddComputer), id: "SetupLipSyncAddComputer"),
                hosts.Count == 0 ? null : PageButton("Check hosts", () => RunNodeAction(NodeAction.CheckHost), id: "SetupLipSyncCheckHosts"),
                PageButton("Open the Devices map", () => Navigate(NavDevices), id: "SetupLipSyncMap"))));
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
