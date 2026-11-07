using System.Globalization;
using System.Net.Http.Json;

namespace Martlet.Gateway.Ollama;

/// <summary>Keeps the conversation model loaded with the window the owner's requests use (issue #1156). The role's warm step
/// (<c>ollama run</c>) and a host restart load the model with Ollama's default window; the owner's next reply then waited for a
/// second load. The relay remembers the last window in the gateway's state folder and, while no request runs, loads the model
/// again with that window when Ollama has another one (a load only: no prompt, so no cached conversation is touched).
/// Owns: the window file and the keeper loop. Tests: OllamaRelayTests (window keeper).</summary>
public sealed partial class OllamaRelayWorker
{
    private string? windowFile;

    /// <summary>Starts keeping the window: reads the last one from <paramref name="file"/> and checks every
    /// <paramref name="every"/> (30 s by default). Only for Martlet's own one-slot Ollama; call once after construction.</summary>
    public void KeepWindow(string file, TimeSpan? every = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        if (app is not null || Route.MaximumConcurrency != 1 || windowFile is not null) return;
        windowFile = file;
        try
        {
            if (File.Exists(file) && int.TryParse(File.ReadAllText(file).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var saved) &&
                saved is > 0 and <= Martlet.Core.Settings.GenerationSettings.MaximumHostContextTokens)
                Interlocked.CompareExchange(ref ownerContext, saved, 0);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        var period = every ?? TimeSpan.FromSeconds(30);
        _ = Task.Run(async () =>
        {
            while (!lifetime.IsCancellationRequested)
            {
                try
                {
                    await KeepWindowOnceAsync(lifetime.Token).ConfigureAwait(false);
                    await Task.Delay(period, lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (Exception) { try { await Task.Delay(period, lifetime.Token).ConfigureAwait(false); } catch (Exception) { return; } }
            }
        });
    }

    /// <summary>One check: when no request runs and Ollama has the model loaded with another window than the owner's last, or
    /// not at all, loads it with that window. Returns true when it loaded.</summary>
    public async Task<bool> KeepWindowOnceAsync(CancellationToken token)
    {
        if (Volatile.Read(ref ownerContext) is not (> 0 and var wanted) || !running.IsEmpty) return false;
        var loaded = await ReadLoadedAsync(token).ConfigureAwait(false);
        if (loaded is null) return false;
        if (loaded.FirstOrDefault(m => string.Equals(m.Model, model, StringComparison.Ordinal)) is { ContextTokens: var context } &&
            (context is null || context == wanted))
            return false;
        if (!running.IsEmpty) return false;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        limit.CancelAfter(TimeSpan.FromMinutes(10));
        using var response = await http.PostAsJsonAsync(chat, new
        {
            model, messages = Array.Empty<object>(), stream = false, keep_alive = -1, options = new { num_ctx = wanted }
        }, limit.Token).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    /// <summary>Saves a new window the owner's requests use, so a restarted gateway loads it again.</summary>
    private void RememberWindow(int context)
    {
        if (windowFile is not { } file) return;
        try { File.WriteAllText(file, context.ToString(CultureInfo.InvariantCulture)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
