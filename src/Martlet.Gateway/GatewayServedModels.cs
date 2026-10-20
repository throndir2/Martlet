namespace Martlet.Gateway;

public sealed partial class GatewayInferenceRouteRegistry
{
    /// <summary>How long one look for the model apps on this host serves the machine report.</summary>
    internal static readonly TimeSpan ServedModelsFor = TimeSpan.FromMinutes(5);
    private readonly object servedGate = new();
    private (DateTimeOffset At, GatewayServedModel[] Models)? servedModels;
    private Task? servedLook;

    /// <summary>Looks for the model apps on this host's loopback (<see cref="Martlet.Providers.LocalModelServers.DetectAsync"/>);
    /// a test gives fixed answers instead. Null: the real look.</summary>
    internal Func<CancellationToken, Task<IReadOnlyList<Martlet.Providers.LocalModelServer>>>? LookForServers { get; set; }

    /// <summary>The chat models the model apps on this host serve, for the machine report (<c>served_models</c>): the last look's
    /// answer at once (null before the first look ends), and a new look in the background when it is older than
    /// <see cref="ServedModelsFor"/>. It asks 127.0.0.1 only, never waits on the request, and is never on a reply's path. The
    /// Ollama servers of Martlet's own roles are left out. A gateway in a Docker container sees only its own loopback, so it
    /// finds no apps there.</summary>
    internal GatewayServedModel[]? ServedModels()
    {
        var now = clock.GetUtcNow();
        lock (servedGate)
        {
            if (servedModels is not { } kept || now - kept.At >= ServedModelsFor || now < kept.At)
                if (servedLook is null or { IsCompleted: true }) servedLook = Task.Run(LookAsync);
            return servedModels?.Models;
        }
    }

    /// <summary>Waits for the look in progress (tests).</summary>
    internal Task ServedLookDone() { lock (servedGate) return servedLook ?? Task.CompletedTask; }

    private async Task LookAsync()
    {
        IReadOnlyList<Martlet.Providers.LocalModelServer> servers;
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            servers = await (LookForServers?.Invoke(limit.Token) ?? Martlet.Providers.LocalModelServers.DetectAsync(cancellationToken: limit.Token))
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or HttpRequestException or IOException) { servers = []; }
        HashSet<int> own;
        lock (gate) own = byId.Values.Select(r => r.Worker).OfType<IOllamaGatewayInferenceWorker>().Select(w => w.OwnPort).OfType<int>().ToHashSet();
        var found = new List<GatewayServedModel>();
        foreach (var server in servers.Where(s => !s.NeedsKey))
        {
            if (!Uri.TryCreate(server.ChatCompletionsBaseUrl, UriKind.Absolute, out var url) || own.Contains(url.Port)) continue;
            var path = url.AbsolutePath.TrimEnd('/');
            if (path is not ("/v1" or "/api/v1" or "/engines/v1")) continue;
            foreach (var model in server.Models)
            {
                if (found.Count >= GatewayMachineReport.MaximumLoadedModels) break;
                if (!Martlet.Core.Planning.ServedModels.Chats(model) || !ModelName(model)) continue;
                found.Add(new()
                {
                    App = server.Id, AppName = server.Name, Port = url.Port, Path = path, Model = model,
                    Bytes = server.SizesGb.TryGetValue(model, out var gb) && gb > 0 ? (long)(gb * 1e9) : null,
                    LoadedBytes = server.LoadedGb.TryGetValue(model, out var loaded) ? (long)(loaded * 1e9) : null
                });
            }
        }
        lock (servedGate) servedModels = (clock.GetUtcNow(), [.. found]);
    }

    /// <summary>The host engine's model pattern (the deep-thinking-app role relays only to such a name).</summary>
    private static bool ModelName(string model) => model is { Length: > 0 and <= 128 } && char.IsAsciiLetterOrDigit(model[0]) &&
        !model.Contains("..", StringComparison.Ordinal) && model.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '/' or '-');
}
