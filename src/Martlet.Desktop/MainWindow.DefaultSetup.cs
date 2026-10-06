using System.Globalization;
using Martlet.Core.Settings;
using Martlet.Sherpa;

namespace Martlet.Desktop;

/// <summary>Set it all up for me (the welcome wizard's Use these suggestions, Home's first step and the Listening and Voice notices): on a PC that isn't
/// in a Martlet network, one confirmation sets up Thinking, Listening and Voice the way <see cref="DefaultSetup"/> plans them for
/// this PC's hardware. The quick parts come first (Ollama with the smallest model that hears, Parakeet and a Windows voice), so
/// Martlet talks within minutes; a voice engine and Whisper on the graphics card follow when they fit, and switch over when ready.</summary>
public partial class MainWindow
{
    private bool settingUpDefaults;

    private HealthFix DefaultsFix() => new("defaults", "Set it all up for me", () => SetUpDefaultsAsync().Forget());

    /// <summary>Whether this PC is in a Martlet network (paired with another computer): it then uses that network's setup.</summary>
    private bool InMartletNetwork()
    {
        var local = ThisPcHost();
        return NetworkMap.Hosts(Inputs()).Any(h => h.HostId != local?.HostId);
    }

    /// <summary>The default setup for this PC, from a live read of its NVIDIA cards. <paramref name="thinking"/>: Thinking is set
    /// up too (otherwise what Thinking uses now counts against the graphics card).</summary>
    private async Task<DefaultSetupPlan> DefaultPlanAsync(bool thinking)
    {
        if (ReferenceEquals(machine, MachineInfo.Unknown)) await ReadMachineAsync();
        IReadOnlyList<GpuNow> gpus;
        try { gpus = await ListeningAdvisor.ReadGpusAsync(lifetime.Token); }
        catch (OperationCanceledException) { gpus = []; }
        var current = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        double? thinkingGb = thinking ? null : IsLocalOllama(current) ? ListeningAdvisor.OllamaModelGb(current!.ModelId) : 0;
        return DefaultSetup.Plan(gpus, machine.BestGpu, machine.Threads, CultureInfo.CurrentUICulture, thinkingGb);
    }

    /// <summary>Sets up the chosen jobs that aren't set up yet, as planned for this PC (or as <paramref name="planned"/>, the
    /// welcome wizard's accepted plan), after one confirmation that also names <paramref name="extra"/>. Returns whether the owner
    /// went ahead (true when nothing was left to set up).</summary>
    private async Task<bool> SetUpDefaultsAsync(bool thinking = true, bool listening = true, bool voice = true, string? extra = null,
        DefaultSetupPlan? planned = null)
    {
        if (store is null || setupService is null || closing || settingUpDefaults) return false;
        var routes = homeSettings?.Setup?.Routes ?? [];
        thinking &= routes.All(r => r.Role != SetupRole.Llm);
        listening &= routes.All(r => r.Role != SetupRole.Stt);
        voice &= routes.All(r => r.Role != SetupRole.Tts);
        if (!thinking && !listening && !voice)
        {
            ActionText.Text = "Thinking, listening and voice are already set up. Change them in Companion.";
            return true;
        }
        settingUpDefaults = true;
        try
        {
            ActionText.Text = "Checking what fits this PC...";
            var plan = planned ?? await DefaultPlanAsync(thinking);
            if (closing) return false;
            var parakeetModel = ParakeetModels.Find(plan.ParakeetModel);
            var downloads = new List<string>();
            if (thinking)
                downloads.Add((Prerequisites.IsMissing(Prerequisites.Ollama) ? "Ollama and " : "") + $"{plan.Thinking.Id} ({plan.Thinking.Size})");
            if (listening && parakeetModel is not null && parakeet?.Installed(parakeetModel.Id) != true)
                downloads.Add($"{parakeetModel.Name} ({SherpaComponents.Megabytes(parakeetModel.DownloadBytes)})");
            if (voice && plan.Voice is { } gpuVoice)
                downloads.Add((machine.DockerInstalled ? "" : "Docker Desktop (it shows its own terms) and ") + $"{gpuVoice.Name} (a large download)");
            if (listening && plan.ListenOnGpu) downloads.Add($"Whisper {plan.Listening.GpuModel}");
            if (!ConfirmationDialog.Confirm(this,
                    $"Set up Martlet for this PC ({plan.Gpu})?\n\n{plan.Describe(thinking, listening, voice)}{(extra is null ? "" : "\n" + extra)}\n\n" +
                    (downloads.Count > 0 ? $"Martlet downloads {string.Join(", ", downloads)}. " : "") +
                    (thinking || homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm) is not { RouteType: SetupRouteType.ChatCompletions or SetupRouteType.OpenAi }
                        ? "Everything runs on this PC: what you say and Martlet's replies aren't sent online. "
                        : "Listening and the voice run on this PC; Thinking uses the online service you chose. ") + "The microphone only listens " +
                    "after you press Start listening." +
                    (voice && plan.Voice is { } terms ? "\n\n" + EngineTerms(terms) : "") + " The models' own licenses apply.",
                    "Set it all up for me", "Set it up", "Not now", questionId: "DefaultSetupQuestion"))
            {
                ActionText.Text = "Nothing changed. Set up each job in Companion whenever you like.";
                return false;
            }
            ErrorLog.Info($"Setting up this PC's defaults ({plan.Gpu}): {plan.Describe(thinking, listening, voice).Replace('\n', ' ')}");

            // Quick parts first, so Martlet can talk within minutes.
            if (thinking && !await SetUpDefaultThinkingAsync(plan.Thinking.Id))
                ErrorLog.Warn("The default setup couldn't set up Thinking; it carries on with the voice and listening.");
            if (voice && !closing) await UseWindowsVoiceAsync(null);
            if (listening && !closing)
            {
                if (parakeetModel is null || parakeet is null || SherpaComponents.RuntimeDirectory() is null)
                    ActionText.Text = "Parakeet isn't available in this Martlet. Choose how it listens in Companion › Listening.";
                else await UseParakeetAsync(parakeetModel, confirmed: true);
            }
            // Then what runs on the graphics card, voice first: each switches over once it's ready.
            if (voice && plan.Voice is { } engine && !closing) await UseVoiceEngineAsync(engine, null, confirmed: true);
            var speaking = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Tts);
            if (listening && plan.ListenOnGpu && !closing && speaking?.RouteType == SetupRouteType.GatewayF5 && ThisPcHost() is not null)
                await UseListeningHereAsync(gpu: true, confirmed: true);
        }
        catch (OperationCanceledException) { return false; }
        finally
        {
            settingUpDefaults = false;
        }
        if (closing) return false;
        await RefreshHomeAsync();
        ActionText.Text = DefaultSetupOutcome();
        return true;
    }

    /// <summary>Thinking with <paramref name="model"/> in Ollama on this PC, installing Ollama (with the model) first when needed.</summary>
    private async Task<bool> SetUpDefaultThinkingAsync(string model)
    {
        if (Prerequisites.IsMissing(Prerequisites.Ollama))
        {
            await InstallPrerequisitesAsync([Prerequisites.Ollama], model);
            if (closing || Prerequisites.IsMissing(Prerequisites.Ollama)) return false;
        }
        return await SaveLocalThinkingAsync(model, confirmed: true);
    }

    /// <summary>What the three jobs use after the default setup, in one status line.</summary>
    private string DefaultSetupOutcome()
    {
        var routes = homeSettings?.Setup?.Routes ?? [];
        string Job(SetupRole role, string name) => routes.FirstOrDefault(r => r.Role == role) is { } route
            ? $"{name}: {PlaceName(route)}" : $"{name}: not set up";
        var ready = routes.Any(r => r.Role == SetupRole.Llm);
        return string.Join(" · ", Job(SetupRole.Llm, "Thinking"), Job(SetupRole.Stt, "Listening"), Job(SetupRole.Tts, "Voice")) +
            (ready ? ". Press Start listening or Start talking, and Show character." : ". Set up Thinking in Companion to start talking.");
    }

}
