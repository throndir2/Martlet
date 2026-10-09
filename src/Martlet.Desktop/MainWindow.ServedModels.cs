using Martlet.Core.Planning;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>Use models your apps already run (Home's Recommended setup and Set it all up for me): Martlet looks for the chat
/// models that the model apps on this PC serve (Ollama, LM Studio, llama.cpp, vLLM and others; loopback only, about 1.5 s) and
/// plans Thinking with the best one that fits, because the owner runs them on purpose. It is on unless the owner turns it off
/// (recommended-setup.json). Looking happens only when a recommendation is worked out, never while Martlet replies.</summary>
public partial class MainWindow
{
    /// <summary>The chat models found the last time Martlet looked, for Reconfigure and Set it all up for me.</summary>
    private IReadOnlyList<ServedModel> lastServed = [];

    private bool UseServedModels => RecommendedSetupMemory.Load(store?.DataDirectory).UseServedModels;

    /// <summary>Looks for the chat models the model apps on this PC serve, when Use models your apps already run is on.</summary>
    private async Task<IReadOnlyList<ServedModel>> FindServedModelsAsync()
    {
        if (!UseServedModels) return lastServed = [];
        try { localServers = await LocalModelServers.DetectAsync(cancellationToken: lifetime.Token); }
        catch (OperationCanceledException) { return lastServed = []; }
        lastServed = RecommendedSetupInputs.Served(localServers);
        ErrorLog.Info(lastServed.Count == 0 ? "Recommended setup: no model app on this PC serves a model now."
            : $"Recommended setup: model apps on this PC serve {string.Join(", ", RecommendedSetupReview.ServedLines(lastServed))}.");
        return lastServed;
    }

    /// <summary>The app found serving <paramref name="model"/> the last time Martlet looked, or null.</summary>
    internal ServedModel? ServedApp(string? model) => model is null ? null
        : lastServed.FirstOrDefault(m => string.Equals(m.ModelId, model, StringComparison.Ordinal))
          ?? lastServed.FirstOrDefault(m => string.Equals(m.ModelId, model, StringComparison.OrdinalIgnoreCase));

    /// <summary>The review's Use models your apps already run: saved on this PC, then the review opens again, planned with (or
    /// without) the models found.</summary>
    private void UseServedChanged(bool use)
    {
        var directory = store?.DataDirectory;
        if (!RecommendedSetupMemory.Load(directory).WithServed(use).Save(directory))
        {
            ActionText.Text = "Martlet couldn't save that choice on this PC.";
            return;
        }
        ErrorLog.Info($"Recommended setup: Use models your apps already run is {(use ? "on" : "off")} on this PC.");
        Dispatcher.BeginInvoke(() => OpenRecommendedSetupAsync().Forget());
    }

    /// <summary>Thinking with a model the owner's own model app serves on this PC, the way Companion › Thinking › This PC uses
    /// it. Ollama's own address goes through Ollama's choice.</summary>
    private async Task<bool> UseServedThinkingAsync(ServedModel served)
    {
        if (served.BaseUrl == LocalOllamaBaseUrl)
        {
            localApp = LocalApp.Ollama;
            return await SaveLocalThinkingAsync(served.ModelId, confirmed: true);
        }
        localApp = LocalApp.Other;
        localServerPicked = served.BaseUrl;
        var name = served.AppName.Length > 0 ? served.AppName : LocalServerName(served.BaseUrl);
        return await SaveSectionRouteAsync(HostJob.Thinking,
            settings => ChatCompletionsSetup.SelectRoute(settings, served.BaseUrl, served.ModelId), null,
            $"Martlet now thinks with {served.ModelId} in {name} on this PC.");
    }
}
