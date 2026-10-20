using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Presentation;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>Companion's job pages: Thinking, Listening and Voice. Each asks where the job runs (this PC by default, another of your computers, or a cloud provider) and shows only that place's card, then the job's own cards. This PC's Ollama for Thinking is in MainWindow.LocalThinking.cs.</summary>
public partial class MainWindow
{
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
        // FIXTURE (MARTLET_SIMULATE_SITUATION): buttons that start a simulated game or make a host simulated away.
        if (role == SetupRole.Llm && SituationFixtureCard() is { } fixture) page.Children.Add(fixture);

        // Speaking and Listening: the pool list replaces the single place; the chosen member's own card shows below it.
        if (role != SetupRole.Llm) page.Children.Add(JobPoolCard(section, role, route));
        else page.Children.Add(WhereItRunsCard(section, section.ToString(), "Where it runs", null, route is null ? null : current, place,
            (JobPlace.ThisPc, "This PC (recommended)", role switch
            {
                SetupRole.Llm => "Ollama or your own model app on this PC's graphics card: private and free.",
                SetupRole.Tts => "A voice engine on this PC's NVIDIA graphics card, or Chatterbox Nano on the processor; audio stays here.",
                _ => "Parakeet on the processor (no setup) or Whisper on the graphics card; your voice stays here."
            }),
            (JobPlace.Computer, "Another of your computers",
                $"A paired computer does {job.Job} with its own graphics card, so this PC stays free."),
            (JobPlace.Cloud, "A cloud provider", role switch
            {
                SetupRole.Llm => "NVIDIA Build (free key), OpenAI, OpenRouter and others; your messages leave this PC, and some charge.",
                SetupRole.Tts => "OpenAI's voices, or ElevenLabs with your cloned voice: no graphics card needed, but reply text leaves " +
                    "this PC and requests cost money.",
                _ => "OpenAI with your API key; your recordings leave this PC, and requests cost money."
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

        // Thinking: the computers a reply may go to when the conversation's own model is busy (none by default).
        if (role == SetupRole.Llm) page.Children.Add(JobPoolCard(section, role, route));


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
                    ? "Voice recognition is on; manage known voices on People."
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
                ? "Not chosen yet: pick a voice engine below."
                : "Not chosen yet: this PC is recommended below."
            : routeName +
                VoiceSuffix(route) +
                (route.Enabled == false ? " (turned off)" : route.Consent is null ? " (not confirmed yet)" : "");
        var now = new StackPanel();
        now.Children.Add(Heading("Now"));
        var nowText = new TextBlock { Text = status, FontSize = 15, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(nowText, "SetupJobNow-" + section);
        now.Children.Add(nowText);
        if (section == CompanionTab.Thinking)
        {
            now.Children.Add(TextModelLine(route));
            // What Martlet knows the model takes on this route, and Test tools for an OpenAI-compatible endpoint.
            if (route is not null)
            {
                var known = Note(ThinkingAbilitiesText(route, SavedModelAbilities()), new Thickness(0, 6, 0, 0));
                AutomationProperties.SetAutomationId(known, "ThinkingAbilities");
                now.Children.Add(known);
            }
            foreach (var element in ToolsTestControls(route)) now.Children.Add(element);
            // Where live Thinking goes in the situation now (Normal, While gaming, Host away).
            now.Children.Add(SituationNowLine());
        }
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
            // The problem shows; the "?" also has what it means for you.
            var warning = HelpTip.Explain($"Needs attention: {problem.Problem} {problem.Effect}".Trim(), new Thickness(0, 6, 0, 0),
                "SetupJobProblem-" + section, job.Job, wiki: "Multiple-Computers#when-no-machine-can-do-a-job");
            warning.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            now.Children.Add(warning);
        }
        if (section == CompanionTab.Listening && AudioMissing(output: false))
        {
            var mic = new TextBlock { Text = homeSettings?.Audio?.Input.EndpointId is null
                    ? "No microphone found: plug one in or type instead."
                    : "Your chosen microphone isn't connected: plug it in, pick another or type instead.",
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
        var tested = checkpoint is not null ? $" (tested on {checkpoint.TestedAt.ToLocalTime():d})" : "";
        var text = missing
            ? choice?.EndpointId is null
                ? output ? "No speakers or headset found: plug them in or turn them on in Windows Sound settings."
                    : "No microphone found: plug one in or turn it on in Windows Sound settings."
                : $"{device} isn't connected: plug it in or pick another."
            : output ? $"Martlet speaks through {device}{tested}."
            : $"Martlet listens with {device}{tested} when you talk in the talk window.";
        var line = Note(text, new Thickness(0, 0, 0, 8));
        if (missing) line.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        return Card(Heading(what), line,
            Row(PageButton(missing ? $"Fix {what.ToLowerInvariant()}" : $"Test or change {what.ToLowerInvariant()}",
                () => RunNodeAction(NodeAction.AudioSetup), primary: missing, id: "OpenAudioSetup")));
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
            var add = $"add another computer to run {job} there.";
            var none = Note(unable.Length > 0 ? $"None of your paired computers can run {engine}, so {add}"
                : exclude is not null ? $"This PC is already selected, so {add}"
                : $"No other computers are paired yet, so {add}", new Thickness(0, 0, 0, 8));
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
                : check?.Reachable == true ? $"{engine} isn't installed there yet, but Martlet can set it up."
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
            var detail = owner == host.HostId ? ownerDetail.TrimEnd('.') + "; a friend shares it with this PC."
                : offers ? $"A friend shares it with this PC, and it offers {engine}."
                : check?.Reachable == true ? $"A friend shares it with this PC, but it doesn't offer {engine}, and only its owner can add it."
                : check?.Reachable == false ? "A friend shares it with this PC, but it isn't reachable right now." : "A friend shares it with this PC.";
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
        // What goes to that computer shows; the "?" also has the voice engine's model licence.
        stack.Add(HelpTip.Explain(disclosure, new Thickness(0, 8, 0, 0), "HostJobData-" + job, $"{job} on your computers",
            wiki: "Multiple-Computers#hand-a-job-to-another-computer"));
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

    internal static string Lowered(string text) => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];

    /// <summary>The fields of cloud provider <paramref name="p"/> for a job: its address (a custom server), model (and voice),
    /// key, the consent box and its Use button. The automation IDs are the page's (<c>SetupCloudModel-&lt;page&gt;</c>,
    /// <c>SetupCloudKey-&lt;page&gt;</c>, <c>SetupCloudConsent-&lt;page&gt;</c>, <c>SetupCloudSave-&lt;page&gt;</c>...).</summary>
    private IEnumerable<UIElement> CloudFields(CompanionTab section, HostJob job, SetupRoute? cloudRoute, CloudProvider p)
    {
        var role = job.Role;
        var baseUrl = new TextBox { MaxLength = 2048, Width = 420, HorizontalAlignment = HorizontalAlignment.Left,
            Text = cloudRoute?.RouteType == SetupRouteType.ChatCompletions ? cloudRoute.Origin : "" };
        AutomationProperties.SetAutomationId(baseUrl, "SetupCloudBaseUrl");
        InputHint.SetText(baseUrl, "https://llm.example.com/v1");

        var model = new ComboBox { MinHeight = 30, MaxWidth = 420, MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(model, "Model");
        AutomationProperties.SetAutomationId(model, "SetupCloudModel-" + section);
        var modelText = new TextBox { MaxLength = 128, Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(modelText, "Model ID");
        AutomationProperties.SetAutomationId(modelText, "SetupCloudModelId-" + section);
        InputHint.SetText(modelText, p.DefaultModel ?? "gemma4:e4b");
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
        // The saved-key mark fills the empty box, so the example shows only while no key is saved.
        var keyHint = p.BaseUrl == ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl ? "Paste your NVIDIA Build key (nvapi-…)"
            : p.BaseUrl == ChatCompletionsEndpointCatalog.OpenRouterBaseUrl ? "Paste your OpenRouter key (sk-or-v1-…)"
            : p == CustomCloud ? "Paste the server's key, if it needs one"
            : $"Paste your {p.Name} API key";
        void RefreshKeyMark()
        {
            using var entered = key.SecurePassword;
            keySavedMark.Visibility = keySaved && entered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            InputHint.SetText(key, keySaved ? null : keyHint);
        }
        RefreshKeyMark();
        var keyStatus = Note(SetAside() ? $"Your {p.Name} key from before is still saved: leave this empty to use it again, or paste a new key."
                : keySaved ? $"Your {p.Name} key is saved: leave this empty to keep it, or paste a new key."
                : p.BaseUrl == ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl
                    ? $"Paste your {p.Name} API key (it starts with nvapi-); Martlet saves it in Windows Credential Manager."
                : p.NeedsKey ? $"Paste your {p.Name} API key; Martlet saves it in Windows Credential Manager."
                : "Add a key only if your server needs one.", new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(keyStatus, "SetupCloudKeyStatus-" + section);
        var retired = SameAsSaved() && p.Chat ? ChatCompletionsEndpointCatalog.RetiredOn(p.BaseUrl, cloudRoute!.ModelId, SavedModelAbilities()) : null;
        // The recommended model is in the provider's summary above and prefilled; the hint adds only what it doesn't say. A
        // retired model says only that, so the line stays one sentence.
        var hint = Note((retired is not null ? $"{retired.Server} no longer supports {cloudRoute!.ModelId}, so choose another model."
            : p == OpenAiCloud || p.BaseUrl == ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl ? ""
            : p.BaseUrl == ChatCompletionsEndpointCatalog.OpenRouterBaseUrl ? "Any exact OpenRouter model ID works."
            : ChatCompletionsEndpointCatalog.Named(p.BaseUrl)?.Guidance is { } guidance ? guidance
            : "Use the server's HTTPS base URL and its exact model ID; for a model app on this PC (LM Studio, llama.cpp and " +
                "others), choose This PC › Model app.").Trim(), new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(hint, "SetupCloudHint-" + section);
        if (hint.Text.Length == 0) hint.Visibility = Visibility.Collapsed;
        // One sentence that names what is sent there and that requests may cost money (OpenAiSetup.Boundary says the same).
        consent.Content = new TextBlock { TextWrapping = TextWrapping.Wrap,
            Text = $"I choose {p.Name} for {job.Job}, and {Lowered(job.Sent)} will be sent there" +
                (FreeKeyPrompt.IsFree(p.BaseUrl) ? "." : ", where requests may cost money.") };
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
        if (leaving is not null && !ConfirmationDialog.Confirm(this, $"Use {provider.Name} for {job.Job}{StopsClause([], leaving)}?",
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
                key, $"{job.Title} now uses {provider.Name} ({model}{(voice is null ? "" : ", voice " + voice)})" +
                    (key is null ? "" : ", with your API key saved in Windows Credential Manager") +
                    (FreeKeyPrompt.IsFree(url) ? "." : ", where requests may cost money."),
                provider.NeedsKey ? missingKey : null))
                return;
            // A new Thinking model: ask its server how much context it takes, so replies stay within it. Choosing a model Martlet
            // found retired tries it again; another HTTP 410 marks it retired again.
            if (!closing && role == SetupRole.Llm && provider.Chat &&
                homeSettings.ThinkingRoute() is { } chosen && chosen.Origin == url && chosen.ModelId == model)
            {
                ForgetRetired(chosen.Origin, model, "you chose it again");
                CheckNewModelContextAsync().Forget();
            }
            if (!closing && leaving is not null) ActionText.Text += await StopLeftVoiceEngineAsync(leaving);
        }
        catch (ContractException error) { ActionText.Text = error.Message; }
        finally { key?.Dispose(); }
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
            ActionText.Text = (reused ? done.TrimEnd('.') + " (it uses the key you saved for it before)." : done) + OpenConversationFollows;
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
}
