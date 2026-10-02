using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Martlet.Logging;
using Martlet.Providers.Ollama;

namespace Martlet.Desktop;

internal enum LocalModelState { Idle, Loading, Ready, NotRunning, MissingModel, Failed }

internal sealed record LocalModelStatus(LocalModelState State, TimeSpan Elapsed, TimeSpan? LoadTime, string? Detail);

/// <summary>Has Ollama on this PC load the Thinking model before a reply needs it. Ollama unloads an idle model after five
/// minutes (its default keep-alive); loading it again takes from 15 s to a couple of minutes, and Ollama abandons the load
/// when the request that started it gives up. So while the talk window is open Martlet asks Ollama to load the model (an
/// /api/generate request with no prompt: nothing is generated and Ollama keeps its own keep-alive) when the window opens and
/// when you start typing, talking or Martlet takes a look after a few quiet minutes. Loopback only; nothing you say is sent.</summary>
internal sealed class LocalOllamaWarmup : IDisposable
{
    internal static Uri Endpoint { get; } = new("http://127.0.0.1:11434/api/generate");
    /// <summary>Ollama's own default load timeout; the load keeps going for as long as this request waits.</summary>
    internal static TimeSpan LoadLimit => TimeSpan.FromMinutes(5);
    /// <summary>Activity after this long since the last load asks again: shorter than Ollama's default five-minute keep-alive.</summary>
    internal static TimeSpan Quiet => TimeSpan.FromMinutes(4);

    private readonly HttpClient client = new(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(3) })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };
    private readonly TimeProvider clock;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object gate = new();
    private Task running = Task.CompletedTask;
    private long? requested, started;
    private LocalModelState state;
    private TimeSpan? loadTime;
    private string? detail;
    private bool disposed;

    internal LocalOllamaWarmup(string model, TimeProvider? clock = null)
    {
        Model = model;
        this.clock = clock ?? TimeProvider.System;
    }

    internal string Model { get; }

    internal LocalModelStatus Status
    {
        get
        {
            lock (gate)
                return new(state, started is { } at ? clock.GetElapsedTime(at) : TimeSpan.Zero, loadTime, detail);
        }
    }

    /// <summary>Asks Ollama to load the model unless a request is still waiting or the last one was under
    /// <see cref="Quiet"/> ago.</summary>
    internal void Touch()
    {
        lock (gate)
        {
            if (disposed || !running.IsCompleted) return;
            // A problem (Ollama stopped, model missing) is checked again after a few seconds, so fixing it is noticed.
            if (requested is { } last && clock.GetElapsedTime(last) < (state == LocalModelState.Ready ? Quiet : TimeSpan.FromSeconds(5))) return;
            requested = clock.GetTimestamp();
            running = Task.Run(LoadAsync);
        }
    }

    private async Task LoadAsync()
    {
        var at = clock.GetTimestamp();
        lock (gate)
        {
            started = at;
            if (state != LocalModelState.Ready) state = LocalModelState.Loading;
        }
        LocalModelState result;
        string? why = null;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            limit.CancelAfter(LoadLimit);
            (result, why, var draftFailed) = await RequestLoadAsync(limit.Token).ConfigureAwait(false);
            if (draftFailed)
            {
                ErrorLog.Warn($"Ollama on this PC couldn't fit {Model}'s draft model in graphics memory: {why}.");
                await LocalOllama.DisableDraftAsync(client, Model, limit.Token).ConfigureAwait(false);
                (result, why, _) = await RequestLoadAsync(limit.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
        catch (OperationCanceledException)
        {
            result = LocalModelState.Failed;
            why = $"Ollama didn't finish loading it within {LoadLimit.TotalMinutes:0} minutes";
        }
        catch (HttpRequestException) { result = LocalModelState.NotRunning; }
        catch (InvalidOperationException error)
        {
            result = LocalModelState.Failed;
            why = error.Message.TrimEnd('.');
        }
        var took = clock.GetElapsedTime(at);
        lock (gate)
        {
            if (disposed) return;
            state = result;
            detail = why;
            if (result == LocalModelState.Ready) loadTime = took;
        }
        // Loading an already loaded model returns at once; only actual loads and problems are worth a line.
        if (result == LocalModelState.Ready && took >= TimeSpan.FromSeconds(1))
            ErrorLog.Info($"Ollama on this PC loaded {Model} in {took.TotalSeconds:0.0} s.");
        else if (result is LocalModelState.MissingModel or LocalModelState.Failed)
            ErrorLog.Warn($"Ollama on this PC couldn't load {Model} ({result}): {why}.");
    }

    private async Task<(LocalModelState State, string? Why, bool DraftFailed)> RequestLoadAsync(CancellationToken token)
    {
        using var content = new StringContent(JsonSerializer.Serialize(new { model = Model }), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(Endpoint, content, token).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return (LocalModelState.Ready, null, false);
        var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        var why = Error(body) ?? $"Ollama returned error {(int)response.StatusCode}";
        return (response.StatusCode == HttpStatusCode.NotFound ? LocalModelState.MissingModel : LocalModelState.Failed, why,
            OllamaDraftHead.FailedToLoad(body));
    }

    // Ollama answers errors as {"error":"..."}, for example "model requires more system memory (9.6 GiB) than is available".
    private static string? Error(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String &&
                error.GetString() is { Length: > 0 } text)
            {
                var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                return line.Length <= 200 ? line : line[..200] + "…";
            }
        }
        catch (JsonException) { }
        return null;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
        }
        lifetime.Cancel();
        running.ContinueWith(_ => { client.Dispose(); lifetime.Dispose(); }, TaskScheduler.Default);
    }
}
