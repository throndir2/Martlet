using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Martlet.Desktop;

/// <summary>Ollama on this PC, driven over its loopback API so model downloads show their progress in a Martlet run
/// window instead of a console.</summary>
internal static class LocalOllama
{
    private const string Origin = "http://127.0.0.1:11434";

    internal static string? Executable() => new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Ollama", "ollama.exe")
    }.FirstOrDefault(File.Exists);

    private static async Task<bool> AnswersAsync(HttpClient client, CancellationToken token)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await client.GetAsync(Origin + "/api/version", limit.Token);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
        catch (HttpRequestException) { return false; }
    }

    /// <summary>Starts Ollama on this PC (its tray app, or a hidden server); false when it isn't installed or won't start.</summary>
    internal static bool Start()
    {
        if (Executable() is not { } ollama) return false;
        var app = Path.Combine(Path.GetDirectoryName(ollama)!, "ollama app.exe");
        try
        {
            if (File.Exists(app)) Process.Start(new ProcessStartInfo(app) { UseShellExecute = true })?.Dispose();
            else Process.Start(new ProcessStartInfo(ollama, "serve") { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
            return true;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    /// <summary>The models this PC's Ollama serves, or null when nothing answers on its loopback port within
    /// <paramref name="timeout"/>. Reads only; contacts nothing beyond this PC.</summary>
    internal static async Task<IReadOnlyList<string>?> ModelsAsync(TimeSpan timeout, CancellationToken token)
    {
        try
        {
            using var client = new HttpClient { Timeout = timeout };
            using var response = await client.GetAsync(Origin + "/api/tags", token);
            if (!response.IsSuccessStatusCode) return null;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            return document.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array
                ? models.EnumerateArray().Select(m => m.TryGetProperty("name", out var name) ? name.GetString() : null)
                    .OfType<string>().Where(name => name.Length is > 0 and <= 128).Take(200).ToArray()
                : [];
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException) { return null; }
    }

    /// <summary>Whether Ollama serves <paramref name="model"/>; a name without a tag means its ":latest".</summary>
    internal static bool Serves(IEnumerable<string> models, string model) => models.Any(name =>
        string.Equals(name, model, StringComparison.OrdinalIgnoreCase) ||
        !model.Contains(':', StringComparison.Ordinal) && string.Equals(name, model + ":latest", StringComparison.OrdinalIgnoreCase));

    /// <summary>Makes sure Ollama answers on this PC, starting its tray app (or a hidden server) when it does not.</summary>
    private static async Task EnsureRunningAsync(HttpClient client, Action<string> status, IProgress<string> output, CancellationToken token)
    {
        if (await AnswersAsync(client, token)) return;
        var ollama = Executable() ?? throw new InvalidOperationException("Ollama isn't installed on this PC yet. Install it first.");
        var app = Path.Combine(Path.GetDirectoryName(ollama)!, "ollama app.exe");
        status("Starting Ollama on this PC...");
        output.Report("Starting Ollama...");
        try
        {
            if (File.Exists(app)) Process.Start(new ProcessStartInfo(app) { UseShellExecute = true })?.Dispose();
            else Process.Start(new ProcessStartInfo(ollama, "serve") { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception error) { throw new InvalidOperationException("Ollama could not be started: " + error.Message); }
        for (var attempt = 0; attempt < 30; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), token);
            if (await AnswersAsync(client, token)) return;
        }
        throw new InvalidOperationException("Ollama did not start on this PC. Start Ollama from the Start menu, then try again.");
    }

    /// <summary>Downloads <paramref name="model"/> into this PC's Ollama, reporting each step and its progress.</summary>
    internal static async Task PullAsync(string model, Action<string> status, IProgress<string> output, CancellationToken token)
    {
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        await EnsureRunningAsync(client, status, output, token);
        status($"Downloading {model} with Ollama...");
        output.Report($"$ ollama pull {model}  (on this PC)");
        using var request = new HttpRequestMessage(HttpMethod.Post, Origin + "/api/pull")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { model, stream = true }), Encoding.UTF8, "application/json")
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        using var stream = await response.Content.ReadAsStreamAsync(token);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? last = null;
        var shown = -1;
        while (await reader.ReadLineAsync(token) is { } line)
        {
            if (line.Length == 0) continue;
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException) { output.Report(line); continue; }
            using var parsed = document;
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error))
                throw new InvalidOperationException($"Ollama could not download {model}: {error.GetString()}");
            var text = root.TryGetProperty("status", out var value) ? value.GetString() ?? "" : "";
            if (root.TryGetProperty("total", out var size) && size.TryGetInt64(out var total) && total > 0 &&
                root.TryGetProperty("completed", out var got) && got.TryGetInt64(out var completed))
            {
                var percent = (int)Math.Min(100, completed * 100 / total);
                status($"Downloading {model}: {percent}% of {total / 1e9:0.0} GB");
                if (text != last) shown = -1;
                if (percent / 10 != shown / 10)
                {
                    output.Report($"{text}: {percent}% of {total / 1e9:0.00} GB");
                    shown = percent;
                }
                last = text;
                continue;
            }
            if (text != last) output.Report(text);
            last = text;
        }
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Ollama could not download {model} (HTTP {(int)response.StatusCode}).");
        if (last != "success") throw new InvalidOperationException($"Ollama did not finish downloading {model}. The output shows why.");
    }
}
