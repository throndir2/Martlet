namespace Martlet.Gateway;

public sealed partial class GatewayInferenceRouteRegistry
{
    /// <summary>How long one read of the Ollama roles' loaded models serves the machine report: paired desktops ask for it on
    /// each host check, and Ollama is asked at most this often.</summary>
    internal static readonly TimeSpan LoadedModelsFor = TimeSpan.FromSeconds(30);
    private readonly object loadedGate = new();
    private (DateTimeOffset At, GatewayLoadedModel[]? Models)? loadedModels;

    /// <summary>What this host's Ollama roles have loaded now (each one's <c>/api/ps</c>, together at most two seconds), for the
    /// machine report; null when no Ollama role answers. Reads only, never on a reply's path, and kept for
    /// <see cref="LoadedModelsFor"/>.</summary>
    internal async Task<GatewayLoadedModel[]?> LoadedModelsAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        lock (loadedGate)
            if (loadedModels is { } kept && now - kept.At < LoadedModelsFor && now >= kept.At) return kept.Models;
        var workers = byId.Values.Select(r => r.Worker).OfType<IOllamaGatewayInferenceWorker>().Distinct().ToArray();
        if (workers.Length == 0) return null;
        var answers = await Task.WhenAll(workers.Select(async worker =>
        {
            try { return await worker.ReadLoadedAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
            catch (Exception error) when (error is HttpRequestException or IOException or InvalidOperationException or ObjectDisposedException) { return null; }
        })).ConfigureAwait(false);
        var models = answers.All(a => a is null) ? null
            : answers.Where(a => a is not null).SelectMany(a => a!).Where(Valid).Take(GatewayMachineReport.MaximumLoadedModels).ToArray();
        lock (loadedGate) loadedModels = (now, models);
        return models;
    }

    private static bool Valid(GatewayLoadedModel? model) =>
        model is { Role.Length: > 0 and <= 32, Model.Length: > 0 and <= 256, Bytes: > 0, GraphicsBytes: >= 0 } && model.GraphicsBytes <= model.Bytes * 2 &&
        model.Model.All(c => c is >= ' ' and <= '~') && model.Digest is null or { Length: <= 128 } && model.ContextTokens is null or > 0;
}
