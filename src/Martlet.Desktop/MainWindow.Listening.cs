using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Avatar.Hosting;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.F5;
using Martlet.Sherpa;

namespace Martlet.Desktop;

/// <summary>Jobs that run in Martlet's host service on this PC (whisper for listening, F5 for its voice): each option is
/// one click and one confirmation, then a single run window sets up the host service when needed, installs the engine
/// with the choices already made here and switches the job over. No console window and no second round of questions.</summary>
public partial class MainWindow
{
    private sealed record GpuProbe(IReadOnlyList<GpuNow> Gpus, string? SttAccelerator);

    private GpuProbe? gpuProbe;
    private Task? gpuProbing;

    /// <summary>Reads this PC's graphics card and how its whisper runs, once, then redraws the Listening tab.</summary>
    private Task ProbeGpuAsync()
    {
        if (gpuProbing is { IsCompleted: false } running) return running;
        return gpuProbing = Run();

        async Task Run()
        {
            // Never redraw inside the render that asked for the probe.
            await Task.Yield();
            try
            {
                var token = lifetime.Token;
                var gpu = ListeningAdvisor.ReadGpusAsync(token);
                var accelerator = ListeningAdvisor.InstalledAcceleratorAsync(token);
                // What the host service here runs also counts against the graphics card.
                if (ThisPcHost() is { } thisPc) await ThisPcOffersAsync(thisPc);
                gpuProbe = new(await gpu, await accelerator);
            }
            catch (OperationCanceledException) { return; }
            if (!closing && openTab == CompanionTab.Listening) RenderTab();
        }
    }

    /// <summary>What already uses this PC's graphics card when it runs: the models Martlet set up here (Ollama natively or
    /// in the host service, F5, Audio2Face). whisper itself is left out, since it is what is being placed.</summary>
    private List<GpuLoad> GpuLoads()
    {
        var loads = new List<GpuLoad>();
        var thinking = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        if (IsLocalOllama(thinking)) loads.Add(new($"Ollama {thinking!.ModelId}", ListeningAdvisor.OllamaModelGb(thinking.ModelId)));
        if (ThisPcHost() is { } thisPc && hostChecks.GetValueOrDefault(thisPc.HostId)?.Offers is { } offers)
        {
            if (offers.GetValueOrDefault(HostRoles.Ollama) is { } model) loads.Add(new($"Ollama {model}", ListeningAdvisor.OllamaModelGb(model)));
            if (offers.GetValueOrDefault(HostRoles.DeepThinking) is { } deep)
                loads.Add(new($"Deep thinking's {deep}", ListeningAdvisor.OllamaModelGb(deep)));


            if (offers.ContainsKey(HostRoles.Xtts)) loads.Add(new("the XTTS voice", 3));
            if (offers.ContainsKey(HostRoles.GptSovits)) loads.Add(new("the GPT-SoVITS voice", 3));
            if (offers.ContainsKey(HostRoles.Dia)) loads.Add(new("the Dia voice", 5));
            if (offers.ContainsKey(HostRoles.Audio2Face)) loads.Add(new("Audio2Face lip-sync", 5));
        }
        return loads;
    }

    private ListeningAdvice ListeningAdviceNow()
    {
        var containers = ThisPcHost() is { } thisPc ? HardwareStore?.Find(thisPc.HostId)?.NvidiaContainers : null;
        return ListeningAdvisor.Advise(gpuProbe?.Gpus ?? [], machine.BestGpu, GpuLoads(), machine.Threads,
            containers switch { "yes" => true, "no" => false, _ => null });
    }

    private async Task<ListeningAdvice> ListeningAdviceAsync()
    {
        if (gpuProbe is null) await ProbeGpuAsync();
        return ListeningAdviceNow();
    }

    /// <summary>What this PC's host service runs, checked now when Martlet hasn't checked it yet (so an engine that is
    /// already installed is switched to, not installed again).</summary>
    private async Task<IReadOnlyDictionary<string, string>?> ThisPcOffersAsync(PairedHost thisPc)
    {
        if (hostChecks.GetValueOrDefault(thisPc.HostId) is { Reachable: true, Offers: { } known }) return known;
        try
        {
            var check = await HostControl.CheckAsync(thisPc.Pairing, HardwareStore, lifetime.Token);
            hostChecks[thisPc.HostId] = check;
            return check.Offers;
        }
        catch (OperationCanceledException) { return null; }
    }

    // ---------- Listening › This PC ----------

    /// <summary>How it listens on this PC, as one option picker (<c>Picker-Listening-&lt;key&gt;</c>): the three Parakeet models
    /// inside Martlet (no Docker), then Whisper on the graphics card and on the processor in Martlet's host service. Each row
    /// says where it runs, how soon the transcript comes and its languages; the chosen one's details hold its button
    /// (<c>SetupListenParakeet-&lt;model&gt;</c>, <c>SetupListenGpu</c>, <c>SetupListenCpu</c>).</summary>
    private Border LocalListeningCard(SetupRoute? route, PairedHost? thisPc)
    {
        if (gpuProbe is null) ProbeGpuAsync().Forget();
        var advice = gpuProbe is null ? null : ListeningAdviceNow();
        var inUse = route?.RouteType == SetupRouteType.GatewayStt && thisPc is not null && route.Gateway?.HostId == thisPc.HostId;
        var running = inUse ? gpuProbe?.SttAccelerator : null;
        var gpuInUse = inUse && running == "gpu";
        var cpuInUse = inUse && running == "cpu";
        var parakeetInUse = route?.RouteType == SetupRouteType.LocalParakeet ? route.ModelId : null;
        var runtime = SherpaComponents.RuntimeDirectory() is not null && parakeet is not null;
        parakeetState = null;

        IEnumerable<UIElement> WhisperDetails(bool gpu)
        {
            if (advice is not null)
                yield return Note(advice.GpuNote + $" For Whisper, {(advice.UseGpu ? "the graphics card" : "the processor")} suits this PC best, " +
                    $"because {advice.Reason}.", new Thickness(0, 4, 0, 0));
            if (thisPc is null)
                yield return Note((machine.DockerRunning ? "Docker Desktop is running. "
                        : machine.DockerInstalled ? "Docker Desktop is installed. Martlet starts it when needed. "
                        : "Docker Desktop isn't installed yet. Martlet installs it first. ") +
                    "The first setup may take a while.", new Thickness(0, 4, 0, 0));
            if (inUse && running is null && gpu)
                yield return Note(LocalSpeechSetup.IsParakeetModel(route!.ModelId)
                    ? $"In use: {ParakeetName(route.ModelId)} in this PC's host service."
                    : "In use: Whisper on this PC.", new Thickness(0, 4, 0, 0));
        }

        var options = JobOptions.Recognizers(new(parakeetInUse, LocalSpeechSetup.RecommendedParakeetModel(CultureInfo.CurrentUICulture),
                id => parakeet?.Installed(id) == true, installingParakeet, parakeetProgress, runtime, advice, gpuInUse, cpuInUse, machine.Threads))
            .Select(option => option.Key switch
            {
                JobOptions.WhisperGpu => option with
                {
                    Details = () => WhisperDetails(gpu: true),
                    Action = () =>
                    {
                        var button = PageButton(gpuInUse ? "Set it up again" : "Use Whisper on the graphics card",
                            () => UseListeningHereAsync(gpu: true).Forget(), primary: !gpuInUse, id: "SetupListenGpu");
                        button.IsEnabled = advice is not null && advice.GpuBlocked is null;
                        return button;
                    }
                },
                JobOptions.WhisperCpu => option with
                {
                    Details = () => WhisperDetails(gpu: false),
                    Action = () => PageButton(cpuInUse ? "Set it up again" : "Use Whisper on the processor",
                        () => UseListeningHereAsync(gpu: false).Forget(), primary: !cpuInUse, id: "SetupListenCpu")
                },
                _ when ParakeetModels.Find(option.Key) is { } model => option with
                {
                    // The download's progress line, updated while it downloads.
                    State = installingParakeet == model.Id ? null : option.State,
                    Details = () => ParakeetDetails(model),
                    Action = () => ParakeetButton(model, parakeetInUse, runtime)
                },
                _ => option
            }).ToList();

        var stack = new List<UIElement>
        {
            Heading("How it listens on this PC"),
            Note("Choose a speech recognizer. Speech stays on this PC and recordings aren't saved.", new Thickness(0, 0, 0, 6)),
            OptionPickerBody("Listening", options)
        };
        stack.Add(Row(
            PageButton("Check the graphics card again", () => { gpuProbe = null; ProbeGpuAsync().Forget(); RenderTab(); }, link: true, id: "SetupListenRecheck"),
            thisPc is null ? null : PageButton("Check it", () => RunNodeAction(NodeAction.CheckHost, thisPc.HostId), link: true, id: "SetupCheckLocal-listening")));
        return Card([.. stack]);
    }
    private static TextBlock Warning(string text)
    {
        var warning = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        warning.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        return warning;
    }

    // ---------- Listening › This PC › Parakeet ----------

    /// <summary>The Parakeet model downloading now, or null.</summary>
    private string? installingParakeet;
    /// <summary>The downloading model's line on the Listening card and how far its download is, updated as it downloads.</summary>
    private TextBlock? parakeetState;
    private string? parakeetProgress;

    /// <summary>A Parakeet model's name for status lines: "Parakeet TDT 110M (English)", or the ID of one this Martlet doesn't know.</summary>
    private static string ParakeetName(string modelId) => ParakeetModels.Find(modelId)?.ToString() ?? modelId;

    /// <summary>Why this PC can't listen yet with the Parakeet model another computer chose (Settings for all devices): it isn't
    /// downloaded here (the owner downloads it in Listening; nothing downloads by itself) or a newer Martlet chose it. Null when
    /// it can.</summary>
    internal static string? SharedParakeetWaiting(string modelId, Func<string, bool> installed) =>
        ParakeetModels.Find(modelId) is not { } model ? "It was chosen on a newer Martlet. Update this PC to use it."
        : installed(model.Id) ? null
        : $"{model} isn't downloaded on this PC yet. Download it in Companion › Listening › Parakeet in Martlet.";

    // ---------- Listening › When Listening's own choice can't hear you ----------

    /// <summary>The Parakeet model on this PC that hears an utterance when Listening's own route (a paired host or OpenAI)
    /// fails (<see cref="LocalSpeechSetup.ListeningStandIn"/>), or null.</summary>
    private string? ListeningStandIn(SetupRoute? route) => parakeet is { } local
        ? LocalSpeechSetup.ListeningStandIn(route, local.Installed, CultureInfo.CurrentUICulture, local.Loaded) : null;

    /// <summary>The Listening Now card's line on what hears you when Listening's own route (a paired host or OpenAI) can't:
    /// Parakeet on this PC's processor, or the download that makes it so (Listening doesn't change). Null when Listening runs
    /// on this PC or isn't chosen, or no Parakeet model hears Windows' display language.</summary>
    private UIElement? StandInLine(SetupRoute? route)
    {
        if (parakeet is null || route is null || route.RouteType is not (null or SetupRouteType.OpenAi or SetupRouteType.GatewayStt))
            return null;
        var where = route.RouteType == SetupRouteType.GatewayStt ? route.Gateway?.HostId ?? "your host" : "OpenAI";
        var standIn = ListeningStandIn(route);
        var download = standIn is null ? ParakeetModels.Find(LocalSpeechSetup.ListeningStandIn(route, _ => true, CultureInfo.CurrentUICulture)) : null;
        if (standIn is null && (download is null || SherpaComponents.RuntimeDirectory() is null)) return null;
        var line = new TextBlock
        {
            Text = standIn is not null
                ? $"If {where} can't hear you, {ParakeetName(standIn)} hears you on this PC's processor instead. Nothing is sent anywhere."
                : $"If {where} can't hear you, Martlet can't either. Download {download} and this PC's processor hears you instead.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0)
        };
        line.SetResourceReference(StyleProperty, "Muted");
        AutomationProperties.SetAutomationId(line, "SetupJobStandIn-Listening");
        if (download is null) return line;
        var button = PageButton(installingParakeet == download.Id ? "Downloading..."
                : $"Download {download.Name} ({SherpaComponents.Megabytes(download.DownloadBytes)})",
            () => UseParakeetAsync(download, use: false).Forget(), link: true, id: "SetupListenStandInDownload");
        button.IsEnabled = installingParakeet is null;
        var panel = new StackPanel();
        panel.Children.Add(line);
        panel.Children.Add(Row(button));
        return panel;
    }

    /// <summary>A Parakeet model's own lines in its details: its full name and, while it downloads, the progress line
    /// (<c>ListenParakeetModelState-&lt;model&gt;</c>), which the download updates.</summary>
    private IEnumerable<UIElement> ParakeetDetails(ParakeetModel model)
    {
        var name = Note($"{model}, inside Martlet: no Docker or host service.", new Thickness(0, 4, 0, 0));
        yield return name;
        if (installingParakeet != model.Id) yield break;
        var state = Note(parakeetProgress ?? "Downloading...", new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(state, "ListenParakeetModelState-" + model.Id);
        AutomationProperties.SetLiveSetting(state, AutomationLiveSetting.Polite);
        parakeetState = state;
        yield return state;
    }

    /// <summary>A Parakeet model's button: download it once (when needed) and listen with it (<c>SetupListenParakeet-&lt;model&gt;</c>).</summary>
    private Button ParakeetButton(ParakeetModel model, string? inUse, bool runtime)
    {
        var installed = parakeet?.Installed(model.Id) == true;
        var used = inUse == model.Id;
        var downloading = installingParakeet == model.Id;
        var button = PageButton(downloading ? "Downloading..." : used ? "In use" : installed ? $"Use {model.Name}"
                : $"Download and use ({SherpaComponents.Megabytes(model.DownloadBytes)})",
            () => UseParakeetAsync(model).Forget(), primary: !used, id: "SetupListenParakeet-" + model.Id);
        button.IsEnabled = !used && installingParakeet is null && runtime;
        return button;
    }
    /// <summary>Listening with Parakeet <paramref name="model"/> on this PC: one confirmation for its download (when needed),
    /// then the route switches to it. With <paramref name="use"/> false it is only downloaded, so this PC's processor hears you
    /// when Listening's own route (a paired host or OpenAI) can't, and Listening doesn't change.</summary>
    private async Task<bool> UseParakeetAsync(ParakeetModel model, bool confirmed = false, bool use = true)
    {
        if (store is null || setupService is null || parakeet is null || closing || installingParakeet is not null) return false;
        var root = parakeet.Root;
        if (!parakeet.Installed(model.Id))
        {
            var size = SherpaComponents.Megabytes(model.DownloadBytes);
            if (!confirmed && !ConfirmationDialog.Confirm(this, use
                    ? $"Download {model} ({size}) and use it for listening?\n\nSpeech stays on this PC and recordings aren't saved."
                    : $"Download {model} ({size}) so this PC's processor hears you when Listening's own choice can't?\n\n" +
                        "Listening doesn't change. Speech stays on this PC and recordings aren't saved.",
                    use ? "Download and use" : "Download"))
                return false;
            installingParakeet = model.Id;
            parakeetProgress = null;
            RenderTab();
            string? problem = null;
            BackgroundTask? task = null;
            try
            {
                // A background task (Background tasks lists it); this page shows its progress too.
                var done = await HostRunWindow.RunAsync(this, $"Download {model.Name}", async run =>
                {
                    task = run.BackgroundTask;
                    run.Status($"Downloading {model} ({size})...");
                    run.Output.Report($"Downloading {model} ({size}) from Hugging Face into {root}.");
                    using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, run.Token);
                    try
                    {
                        await SherpaComponents.InstallParakeetAsync(root, model, new Progress<SherpaProgress>(p =>
                        {
                            parakeetProgress = $"Downloading: {p.Received * 100 / Math.Max(1, p.Total)}% of {SherpaComponents.Megabytes(p.Total)}...";
                            run.Status($"{model.Name}: {parakeetProgress}");
                            ActionText.Text = $"{model.Name}: {parakeetProgress}";
                            if (parakeetState is { } line) line.Text = parakeetProgress;
                        }), cancellation.Token);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or
                        System.Net.Http.HttpRequestException or InvalidOperationException)
                    {
                        problem = error.Message;
                        throw;
                    }
                    return $"{model} is downloaded.";
                });
                if (done is null)
                {
                    if (!closing)
                        ActionText.Text = task?.State == BackgroundTaskState.Canceled ? $"The download of {model.Name} was canceled. Listening didn't change."
                            : $"Couldn't download {model.Name}. {problem ?? task?.Status} Listening didn't change.";
                    return false;
                }
            }
            finally
            {
                installingParakeet = null;
                parakeetState = null;
                parakeetProgress = null;
                if (!closing && openTab == CompanionTab.Listening) RenderTab();
            }
        }
        if (!use)
        {
            // Not loaded now: the stand-in loads it the first time Listening's own route fails.
            ActionText.Text = $"{model} is downloaded. This PC's processor hears you with it when Listening's own choice can't.";
            RenderHome();
            return true;
        }
        parakeet.WarmAsync(model.Id).Forget();
        var saved = await SaveSectionRouteAsync(HostJob.Listening, settings => LocalSpeechSetup.SelectParakeet(settings, model.Id), key: null,
            $"Martlet now listens with {model} on this PC. Speech stays on this PC.");
        if (!closing && openTab == CompanionTab.Listening) RenderTab();
        return saved;
    }

    /// <summary>Listening on this PC with whisper on the graphics card or the processor: one confirmation (unless
    /// <paramref name="confirmed"/> already did), then one run window.</summary>
    private async Task UseListeningHereAsync(bool gpu, bool confirmed = false)
    {
        if (store is null || setupService is null || closing) return;
        var advice = await ListeningAdviceAsync();
        if (gpu && advice.GpuBlocked is { } blocked) { ActionText.Text = blocked; return; }
        var job = HostJob.Listening;
        var accelerator = gpu ? "gpu" : "cpu";
        var model = gpu ? advice.GpuModel ?? "small" : advice.CpuModel;
        var thisPc = ThisPcHost();
        var installed = thisPc is null ? null : (await ThisPcOffersAsync(thisPc))?.GetValueOrDefault(HostRoles.Stt);
        var where = gpu ? "on this PC's graphics card" : "on this PC's processor";
        var route = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == job.Role);
        var inUse = route?.RouteType == SetupRouteType.GatewayStt && thisPc is not null && route.Gateway?.HostId == thisPc.HostId;
        // Already installed the chosen way: just switch to it (Set it up again reinstalls with today's suggestion instead).
        if (thisPc is not null && installed is not null && !inUse && gpuProbe?.SttAccelerator == accelerator)
        {
            await AssignJobAsync(job, "host:" + thisPc.HostId);
            return;
        }
        if (!confirmed && !ConfirmationDialog.Confirm(this,
                $"Listen with Whisper {where}? " +
                (thisPc is null ? "Martlet will set up Docker Desktop on this PC first. " : "") +
                $"Martlet will download the {model} speech model and switch listening to it. " +
                (gpu ? "" : "The graphics card stays free. ") +
                "Speech stays on this PC and recordings aren't saved.",
                "Set it up"))
            return;
        await SetUpJobHereAsync(job, advice.InstallAnswers(gpu), $"Listen with Whisper {where}");
    }

    /// <summary>One run window for a job on this PC: sets up and pairs the host service when needed, installs the job's
    /// engine with <paramref name="answers"/> (the choices already made in Martlet, so the engine asks nothing) and hands
    /// the job to it. The job keeps working where it is until the new engine answers. Nothing else waits for it: other
    /// changes go ahead meanwhile (the switch at the end takes its turn like any change), and what it shares with other runs
    /// (Docker Desktop, the host service) is done once. Something else chosen for the job while it installs wins: the
    /// engine is then ready but the job keeps that choice. Returns whether the job switched.</summary>
    private async Task<bool> SetUpJobHereAsync(HostJob job, IReadOnlyDictionary<string, string> answers, string title)
    {
        if (store is null || setupService is null || closing) return false;
        if (HostRunWindow.IsRunningTitled(title))
        {
            // The same setup is still working: its window comes forward, and that run does the switch.
            await HostRunWindow.RunAsync(this, title, _ => Task.FromResult(""), join: true);
            return false;
        }
        var dataDirectory = store.DataDirectory;
        var service = setupService;
        // Every other route change for this job clears it (pendingJobHosts), so the switch at the end knows it still stands.
        var mark = $"this-pc-setup-{Guid.NewGuid():N}";
        pendingJobHosts[job.Role] = mark;
        pendingJobVoices.Remove(job.Role);
        var switched = false;
        try
        {
            async Task<string> Continue(HostRunWindow run, PairedHost host)
            {
                var target = host.Target(Version);
                await HostLocal.EnsureDockerAsync(run, Martlet.Core.Installation.ContinueSetupKind.Docker);
                target = await HostLocal.EngineForChangeAsync(target, job.HostRoleKind, run);
                // Several graphics cards: the owner picks the one this engine runs on.
                run.Status($"Checking this PC's graphics cards for {job.Engine}...");
                var inputs = await HostLocal.DescribeAsync(target, job.HostRoleKind, run.Output, run.Token);
                var chosen = HostInputDialog.WithGpu(run, "this PC", job.Engine, inputs, answers) ?? throw new OperationCanceledException();
                run.Status($"Installing {job.Engine} on this PC...");
                var exit = await HostLocal.EngineAsync(target, ["add", job.HostRoleKind], run.Output, run.Token, answers: chosen);
                if (exit != 0) throw new InvalidOperationException($"Installing {job.Engine} stopped (exit {exit}). {job.Title} didn't change. The output has details.");
                run.Status($"Switching {job.Job} to {job.Engine} on this PC...");
                var route = await WaitForRouteAsync(host, job, run);
                var voice = job.RouteType == SetupRouteType.GatewayF5
                    ? await F5Voices.DefaultAsync(dataDirectory, route.DestinationId ?? F5Destination, run.Token,
                        SpeechEngines.ForRoute(job.RouteId)) : null;
                using (await changes.TakeAsync(run.Token))
                {
                    if (pendingJobHosts.GetValueOrDefault(job.Role) != mark)
                        return $"{job.Engine} is ready on this PC. {job.Title} keeps what you chose for it meanwhile.";
                    await SaveJobHostAsync(job, host, route, voice);
                    pendingJobHosts.Remove(job.Role);
                    switched = true;
                }
                RecordClusterJob(job.Job, new(host.HostId, false));
                return $"{job.Title} now uses {job.Engine} on this PC" +
                    (voice is null ? "." : $", with the voice \"{voice.PresetName}\".") + OpenConversationFollows;
            }

            string? status;
            if (ThisPcHost() is { } thisPc)
            {
                var done = await HostRunWindow.RunAsync(this, title, run => Continue(run, thisPc));
                status = done ?? $"{job.Title} didn't change. The run window has details.";
            }
            else
            {
                (_, status) = await HostsWindow.SetUpThisPcAsync(this, new AvatarProfileStore(dataDirectory), service,
                    text => ActionText.Text = text, lifetime.Token, Continue, title);
                await ReadMachineAsync();
            }
            if (!closing && status is not null) ActionText.Text = status;
        }
        catch (OperationCanceledException) { }
        catch (F5Exception error) { ActionText.Text = F5Voices.Describe(error); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or
            JsonException or ArgumentException or Audio2FaceHostException)
        {
            ActionText.Text = error.Message;
        }
        finally
        {
            gpuProbe = null;
            if (pendingJobHosts.GetValueOrDefault(job.Role) == mark) pendingJobHosts.Remove(job.Role);
        }
        if (closing) return switched;
        await RefreshHomeAsync();
        if (openTab is not null) RenderTab();
        return switched;
    }

    /// <summary>Waits (about a minute) for this PC's host service to advertise the job's route after installing it.</summary>
    private async Task<HostRoute> WaitForRouteAsync(PairedHost host, HostJob job, HostRunWindow run)
    {
        for (var attempt = 0; ; attempt++)
        {
            var check = await HostControl.CheckAsync(host.Pairing, HardwareStore, run.Token);
            hostChecks[host.HostId] = check;
            if (check.Routes?.FirstOrDefault(r => r.RouteId == job.RouteId) is { } route) return route;
            if (attempt >= 12)
                throw new InvalidOperationException($"Setup finished, but Martlet can't see {job.Engine} yet ({check.Text}). Press Check it in a minute.");
            await Task.Delay(TimeSpan.FromSeconds(5), run.Token);
        }
    }
}
