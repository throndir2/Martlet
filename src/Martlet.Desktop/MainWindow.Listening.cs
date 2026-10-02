using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Avatar.Hosting;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.F5;

namespace Martlet.Desktop;

/// <summary>Jobs that run in Martlet's host service on this PC (whisper for listening, F5 for its voice): each option is
/// one click and one confirmation, then a single run window sets up the host service when needed, installs the engine
/// with the choices already made here and switches the job over. No console window and no second round of questions.</summary>
public partial class MainWindow
{
    private sealed record GpuProbe(GpuNow? Gpu, string? SttAccelerator);

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
                var gpu = ListeningAdvisor.ReadGpuAsync(token);
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
            if (offers.ContainsKey(HostRoles.F5)) loads.Add(new("the F5 voice", 3));
            if (offers.ContainsKey(HostRoles.Audio2Face)) loads.Add(new("Audio2Face lip-sync", 5));
        }
        return loads;
    }

    private ListeningAdvice ListeningAdviceNow()
    {
        var containers = ThisPcHost() is { } thisPc ? HardwareStore?.Find(thisPc.HostId)?.NvidiaContainers : null;
        return ListeningAdvisor.Advise(gpuProbe?.Gpu, machine.BestGpu, GpuLoads(), machine.Threads,
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

    /// <summary>How it listens on this PC: whisper on the graphics card or on the processor, side by side like the voice
    /// options, with the one that suits this PC right now recommended (the card unless it is too busy or can't run it).</summary>
    private Border LocalListeningCard(SetupRoute? route, PairedHost? thisPc)
    {
        if (gpuProbe is null) ProbeGpuAsync().Forget();
        var advice = gpuProbe is null ? null : ListeningAdviceNow();
        var inUse = route?.RouteType == SetupRouteType.GatewayStt && thisPc is not null && route.Gateway?.HostId == thisPc.HostId;
        var running = inUse ? gpuProbe?.SttAccelerator : null;
        var gpuInUse = inUse && running == "gpu";
        var cpuInUse = inUse && running == "cpu";
        var nothingHere = !inUse;

        string? Tag(bool gpu, bool used) => used ? "in use" : nothingHere && advice is not null && advice.UseGpu == gpu ? "recommended for this PC" : null;

        var gpuOption = new List<UIElement>
        {
            OptionTitle("On the graphics card", Tag(true, gpuInUse)),
            Note("Fastest: replies start sooner. " + (advice?.GpuModel is { } gpuModel
                    ? $"Uses the {gpuModel} model, which takes about {(gpuModel == "small" ? "1" : "2")} GB of the graphics card."
                    : "Needs an NVIDIA graphics card with a current driver."), new Thickness(0, 2, 0, 6))
        };
        if (advice?.GpuBlocked is { } blocked) gpuOption.Add(Warning(blocked));
        var gpuButton = PageButton(gpuInUse ? "Set it up again" : "Use the graphics card", () => UseListeningHereAsync(gpu: true).Forget(),
            primary: nothingHere && advice?.UseGpu == true, id: "SetupListenGpu");
        gpuButton.IsEnabled = advice is not null && advice.GpuBlocked is null;
        gpuOption.Add(Row(gpuButton));

        var cpuOption = new List<UIElement>
        {
            OptionTitle("On the processor", Tag(false, cpuInUse)),
            Note($"Works on any PC and leaves the graphics card free for games and other models; replies take a moment longer. " +
                $"Uses the {advice?.CpuModel ?? (machine.Threads >= 6 ? "small" : "base")} model.", new Thickness(0, 2, 0, 6)),
            Row(PageButton(cpuInUse ? "Set it up again" : "Use the processor", () => UseListeningHereAsync(gpu: false).Forget(),
                primary: nothingHere && advice?.UseGpu == false, id: "SetupListenCpu"))
        };

        var gpuFirst = gpuInUse || !cpuInUse && advice?.UseGpu != false;
        var stack = new List<UIElement>
        {
            Heading("How it listens on this PC"),
            Note("whisper turns your speech into text in memory on this PC and stores nothing. It runs in Martlet's host service " +
                "inside Docker Desktop. Choose one; it sets itself up.", new Thickness(0, 0, 0, 4)),
            Note(advice is null ? "Checking this PC's graphics card..."
                : advice.GpuNote + $" Recommended: {(advice.UseGpu ? "the graphics card" : "the processor")}, because {advice.Reason}.",
                new Thickness(0, 4, 0, 0)),
            Option(gpuFirst ? gpuOption : cpuOption, gpuFirst ? gpuInUse : cpuInUse),
            Option(gpuFirst ? cpuOption : gpuOption, gpuFirst ? cpuInUse : gpuInUse)
        };
        if (inUse && running is null)
            stack.Add(Note($"In use: whisper {route!.ModelId} in this PC's host service.", new Thickness(0, 10, 0, 0)));
        if (thisPc is null)
            stack.Add(Note((machine.DockerRunning ? "Docker Desktop is running. "
                    : machine.DockerInstalled ? "Docker Desktop is installed; Martlet starts it when needed. "
                    : "Docker Desktop isn't installed yet; Martlet installs it first. ") +
                "The first time, Martlet also sets up its host service on this PC (it then appears as one of your hosts).", new Thickness(0, 10, 0, 0)));
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

    /// <summary>Listening on this PC with whisper on the graphics card or the processor: one confirmation, then one run window.</summary>
    private async Task UseListeningHereAsync(bool gpu)
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
        if (!ConfirmationDialog.Confirm(this,
                $"Listen with whisper {where}? " +
                (thisPc is null ? "Martlet first sets up its host service in Docker Desktop on this PC (once; this PC then also appears as one of your hosts). " : "") +
                $"It installs whisper.cpp (MIT) with the {model} Whisper model (MIT, downloaded once from Hugging Face), shows the progress " +
                "and switches listening to it by itself. " + (gpu ? "" : "The graphics card stays free. ") +
                "Your recorded speech is transcribed in memory on this PC and not stored; nothing leaves this PC.",
                "Set it up"))
            return;
        await SetUpJobHereAsync(job, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["choice.accelerator"] = accelerator, ["choice.STT_MODEL"] = model
        }, $"Listen with whisper {where}");
    }

    /// <summary>F5 on this PC: hands speaking over when it already runs here, otherwise one confirmation and one run window
    /// that sets everything up.</summary>
    private async Task UseF5HereAsync()
    {
        if (store is null || setupService is null || closing) return;
        var thisPc = ThisPcHost();
        if (thisPc is not null && (await ThisPcOffersAsync(thisPc))?.ContainsKey(HostRoles.F5) == true)
        {
            await AssignJobAsync(HostJob.Speaking, "host:" + thisPc.HostId);
            return;
        }
        if (!ConfirmationDialog.Confirm(this,
                "Speak with the F5 voice on this PC's graphics card? " +
                (thisPc is null ? "Martlet first sets up its host service in Docker Desktop on this PC (once; this PC then also appears as one of your hosts). " : "") +
                "It installs F5-TTS (MIT source; a large download) with the F5TTS_v1_Base model, licensed for non-commercial use only " +
                "(CC-BY-NC-4.0), shows the progress and switches its voice over by itself, starting with F5-TTS's published English " +
                "sample voice. Reply text and the voice recording stay on this PC.",
                "Set it up"))
            return;
        await SetUpJobHereAsync(HostJob.Speaking, new Dictionary<string, string>(StringComparer.Ordinal), "Speak with F5 on this PC");
    }

    /// <summary>One run window for a job on this PC: sets up and pairs the host service when needed, installs the job's
    /// engine with <paramref name="answers"/> (the choices already made in Martlet, so the engine asks nothing) and hands
    /// the job to it. The job keeps working where it is until the new engine answers.</summary>
    private async Task SetUpJobHereAsync(HostJob job, IReadOnlyDictionary<string, string> answers, string title)
    {
        if (store is null || setupService is null || closing) return;
        if (assigningRole || hostBusy) { ActionText.Text = "Another change is still finishing. Try again in a moment."; return; }
        var dataDirectory = store.DataDirectory;
        var service = setupService;
        assigningRole = true;
        hostBusy = true;
        try
        {
            async Task<string> Continue(HostRunWindow run, PairedHost host)
            {
                var target = host.Target(Version);
                await HostLocal.EnsureDockerAsync(run.Status, run.Output, run.Token);
                await HostLocal.EnsureImageAsync(target, run.Status, run.Output, run.Token);
                run.Status($"Installing {job.Engine} on this PC. The first time downloads its model; this can take a while...");
                var exit = await HostLocal.EngineAsync(target, ["add", job.HostRoleKind], run.Output, run.Token, answers: answers);
                if (exit != 0) throw new InvalidOperationException($"Installing {job.Engine} stopped (exit {exit}). {job.Title} stays where it is; the output shows why.");
                run.Status($"Switching {job.Job} to {job.Engine} on this PC...");
                var route = await WaitForRouteAsync(host, job, run);
                var voice = job.RouteType == SetupRouteType.GatewayF5
                    ? await F5Voices.DefaultAsync(dataDirectory, route.DestinationId ?? F5Destination, run.Token) : null;
                await SaveJobHostAsync(job, host, route, voice);
                RecordClusterJob(job.Job, new(host.HostId, false));
                return $"{job.Title} now uses {job.Engine} ({route.ModelId}) on this PC" +
                    (voice is null ? "." : $", in the voice '{voice.PresetName}'.") + " An open conversation window picks it up on Reload.";
            }

            string? status;
            if (ThisPcHost() is { } thisPc)
            {
                var done = await HostRunWindow.RunAsync(this, title, run => Continue(run, thisPc));
                status = done ?? $"{job.Title} stays where it is; the run window shows why.";
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
            assigningRole = false;
            hostBusy = false;
            gpuProbe = null;
        }
        if (closing) return;
        await RefreshHomeAsync();
        if (openTab is not null) RenderTab();
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
                throw new InvalidOperationException($"{job.Engine} is installed, but this PC's host service doesn't offer it yet ({check.Text}). " +
                    "Press Check it in a minute.");
            await Task.Delay(TimeSpan.FromSeconds(5), run.Token);
        }
    }
}
