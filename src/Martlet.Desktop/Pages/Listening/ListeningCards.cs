using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Settings;
using Martlet.HostClient;
using Martlet.Sherpa;

namespace Martlet.Desktop;

/// <summary>The cards of Companion › Listening that the job page shows, the UI only: How it listens on this PC (this file), How
/// you talk (ListeningCards.HowYouTalk.cs), Speakers and echo and Watch along (ListeningCards.Speakers.cs). What the cards need
/// from the app is <see cref="IListeningCardsServices"/>; MainWindow keeps the work they start.</summary>
internal sealed partial class ListeningCards(IListeningCardsServices services)
{
    /// <summary>How it listens on this PC, as one option picker (<c>Picker-Listening-&lt;key&gt;</c>): the three Parakeet models
    /// inside Martlet (no Docker), then Whisper on the graphics card and on the processor in Martlet's host service. Each row
    /// says where it runs, how soon the transcript comes and its languages; the chosen one's details hold its button
    /// (<c>SetupListenParakeet-&lt;model&gt;</c>, <c>SetupListenGpu</c>, <c>SetupListenCpu</c>).</summary>
    internal Border LocalListening(SetupRoute? route, PairedHost? thisPc)
    {
        if (!services.GpuProbed) services.ProbeGpu();
        var advice = services.GpuProbed ? services.ListeningAdvice() : null;
        var machine = services.Machine;
        var installingParakeet = services.InstallingParakeet;
        var inUse = route?.RouteType == SetupRouteType.GatewayStt && thisPc is not null && route.Gateway?.HostId == thisPc.HostId;
        var running = inUse ? services.SttAccelerator : null;
        var gpuInUse = inUse && running == "gpu";
        var cpuInUse = inUse && running == "cpu";
        var parakeetInUse = route?.RouteType == SetupRouteType.LocalParakeet ? route.ModelId : null;
        var runtime = SherpaComponents.RuntimeDirectory() is not null && services.HasParakeet;
        services.KeepParakeetLine(null);

        IEnumerable<UIElement> WhisperDetails(bool gpu)
        {
            if (advice is not null)
                yield return HelpTip.Explain($"For Whisper, {(advice.UseGpu ? "the graphics card" : "the processor")} suits this PC best, " +
                    $"because {advice.Reason}. " + advice.GpuNote, new Thickness(0, 4, 0, 0), gpu ? "ListeningWhisperGpu" : "ListeningWhisperCpu",
                    "Whisper on this PC", wiki: "Listening-and-Voice-ID#listening");
            if (thisPc is null)
                yield return Note(machine.DockerRunning ? "Docker Desktop is running; the first setup may take a while."
                        : machine.DockerInstalled ? "Docker Desktop is installed; Martlet starts it when needed, and the first setup may take a while."
                        : "Docker Desktop isn't installed yet; Martlet installs it first, and the first setup may take a while.",
                    new Thickness(0, 4, 0, 0));
            if (inUse && running is null && gpu)
                yield return Note(LocalSpeechSetup.IsParakeetModel(route!.ModelId)
                    ? $"In use: {MainWindow.ParakeetName(route.ModelId)} in this PC's host service."
                    : "In use: Whisper on this PC.", new Thickness(0, 4, 0, 0));
        }

        var options = JobOptions.Recognizers(new(parakeetInUse, LocalSpeechSetup.RecommendedParakeetModel(CultureInfo.CurrentUICulture),
                services.ParakeetInstalled, installingParakeet, services.ParakeetProgress, runtime, advice, gpuInUse, cpuInUse, machine.Threads))
            .Select(option => option.Key switch
            {
                JobOptions.WhisperGpu => option with
                {
                    Details = () => WhisperDetails(gpu: true),
                    Action = () =>
                    {
                        var button = PageButton(gpuInUse ? "Set it up again" : "Use Whisper on the graphics card",
                            () => services.UseListeningHere(gpu: true), primary: !gpuInUse, id: "SetupListenGpu");
                        button.IsEnabled = advice is not null && advice.GpuBlocked is null;
                        return button;
                    }
                },
                JobOptions.WhisperCpu => option with
                {
                    Details = () => WhisperDetails(gpu: false),
                    Action = () => PageButton(cpuInUse ? "Set it up again" : "Use Whisper on the processor",
                        () => services.UseListeningHere(gpu: false), primary: !cpuInUse, id: "SetupListenCpu")
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
            HelpTip.Explain("Choose a speech recognizer. With Parakeet or Whisper on this PC, speech stays on this PC and recordings aren't saved.",
                new Thickness(0, 0, 0, 6), "ListeningRecognizer", "speech recognizers", wiki: "Listening-and-Voice-ID#listening"),
            services.OptionPickerBody("Listening", options)
        };
        stack.Add(Row(
            PageButton("Check the graphics card again", services.RecheckGpu, link: true, id: "SetupListenRecheck"),
            thisPc is null ? null : PageButton("Check it", () => services.CheckHost(thisPc.HostId), link: true, id: "SetupCheckLocal-listening")));
        return Card([.. stack]);
    }

    /// <summary>A Parakeet model's own lines in its details: its full name and, while it downloads, the progress line
    /// (<c>ListenParakeetModelState-&lt;model&gt;</c>), which the download updates.</summary>
    private IEnumerable<UIElement> ParakeetDetails(ParakeetModel model)
    {
        var name = Note($"{model}, inside Martlet: no Docker or host service.", new Thickness(0, 4, 0, 0));
        yield return name;
        if (services.InstallingParakeet != model.Id) yield break;
        var state = Note(services.ParakeetProgress ?? "Downloading...", new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(state, "ListenParakeetModelState-" + model.Id);
        AutomationProperties.SetLiveSetting(state, AutomationLiveSetting.Polite);
        services.KeepParakeetLine(state);
        yield return state;
    }

    /// <summary>A Parakeet model's button: download it once (when needed) and listen with it (<c>SetupListenParakeet-&lt;model&gt;</c>).</summary>
    private Button ParakeetButton(ParakeetModel model, string? inUse, bool runtime)
    {
        var installed = services.ParakeetInstalled(model.Id);
        var used = inUse == model.Id;
        var downloading = services.InstallingParakeet == model.Id;
        var button = PageButton(downloading ? "Downloading..." : used ? "In use" : installed ? $"Use {model.Name}"
                : $"Download and use ({SherpaComponents.Megabytes(model.DownloadBytes)})",
            () => services.UseParakeet(model), primary: !used, id: "SetupListenParakeet-" + model.Id);
        button.IsEnabled = !used && services.InstallingParakeet is null && runtime;
        return button;
    }
}
