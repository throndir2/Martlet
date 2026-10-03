using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Martlet.Core.Settings;
using Martlet.Logging;
using Martlet.Providers.Ollama;

namespace Martlet.Desktop;

/// <summary>Ollama on this PC, driven over its loopback API so model downloads show their progress in a Martlet run
/// window instead of a console.</summary>
internal static class LocalOllama
{
    private const string Origin = "http://127.0.0.1:11434";
    internal static Uri OriginUri { get; } = new(Origin + "/");

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
        var ollama = Executable() ?? throw new InvalidOperationException("Ollama isn't installed on this PC. Install it first.");
        var app = Path.Combine(Path.GetDirectoryName(ollama)!, "ollama app.exe");
        status("Starting Ollama on this PC...");
        output.Report("Starting Ollama...");
        try
        {
            if (File.Exists(app)) Process.Start(new ProcessStartInfo(app) { UseShellExecute = true })?.Dispose();
            else Process.Start(new ProcessStartInfo(ollama, "serve") { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception error) { throw new InvalidOperationException("Couldn't start Ollama: " + error.Message); }
        for (var attempt = 0; attempt < 30; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), token);
            if (await AnswersAsync(client, token)) return;
        }
        throw new InvalidOperationException("Ollama didn't start. Start it from the Start menu, then try again.");
    }

    /// <summary>Downloads <paramref name="model"/> into this PC's Ollama, reporting each step and its progress.</summary>
    internal static async Task PullAsync(string model, Action<string> status, IProgress<string> output, CancellationToken token)
    {
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        await EnsureRunningAsync(client, status, output, token);
        status($"Downloading {model} with Ollama...");
        output.Report($"Downloading {model} with Ollama.");
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
                throw new InvalidOperationException($"Couldn't download {model}: {error.GetString()}");
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
            throw new InvalidOperationException($"Couldn't download {model} (error {(int)response.StatusCode}).");
        if (last != "success") throw new InvalidOperationException($"Ollama didn't finish downloading {model}. The output has details.");
    }

    private const string TestPrompt = "Say hello in one short, friendly sentence.";
    private static readonly TimeSpan LoadLimit = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AnswerLimit = TimeSpan.FromMinutes(2);

    /// <summary>Checks that <paramref name="model"/> loads and answers in this PC's Ollama the way a reply uses it: its
    /// OpenAI-compatible endpoint, streamed, with the reply budget (<paramref name="replyTokens"/>; null sends none, like a
    /// reply without a max reply length). Starts Ollama when it is
    /// installed but not running. Loopback only, so nothing leaves this PC. A model that works but is too slow for a reply's
    /// <paramref name="firstWordsLimit"/> passes with a warning; anything that stops a reply throws
    /// <see cref="InvalidOperationException"/> with what went wrong, in Ollama's own words where it gave any.</summary>
    internal static async Task<LocalModelTestResult> TestAsync(string model, int? replyTokens, TimeSpan firstWordsLimit,
        Action<string> status, IProgress<string> output, CancellationToken token, bool? reasoning = null)
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
        status("Checking Ollama...");
        await EnsureRunningAsync(client, status, output, token);
        using (await SendAsync(client, HttpMethod.Get, "/api/version", null, TimeSpan.FromSeconds(10), token))
            output.Report("Ollama is running.");

        status($"Checking {model} in Ollama...");
        string[] capabilities;
        using (var show = await SendAsync(client, HttpMethod.Post, "/api/show", new { model }, TimeSpan.FromSeconds(30), token))
        {
            if (show.Status == HttpStatusCode.NotFound)
                throw new InvalidOperationException($"{model} isn't downloaded in Ollama on this PC. Choose Download model, then test again.");
            if (!show.Ok) throw Failure($"Ollama couldn't read {model}", show);
            var root = show.Body!.RootElement;
            capabilities = root.TryGetProperty("capabilities", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(c => c.ValueKind == JsonValueKind.String ? c.GetString() : null).OfType<string>().ToArray()
                : [];
            var canChat = capabilities.Length == 0 || capabilities.Contains("completion", StringComparer.Ordinal);
            output.Report($"{model} is downloaded." + (capabilities.Length > 0 && canChat ? $" {Abilities(capabilities)}." : ""));
            if (!canChat)
                throw new InvalidOperationException($"{model} can't hold a conversation. Choose a chat model.");
        }

        status($"Loading {model}...");
        output.Report($"Loading {model}...");
        var clock = Stopwatch.StartNew();
        using (var load = await SendAsync(client, HttpMethod.Post, "/api/generate", new { model, stream = false }, LoadLimit, token))
        {
            if (!load.Ok && OllamaDraftHead.FailedToLoad(load.Text))
            {
                output.Report($"Ollama couldn't fit {model}'s speed-up draft model in graphics memory: {ErrorText(load.Text)}.");
                output.Report($"Turning off the draft model for {model} and loading it again...");
                await DisableDraftAsync(client, model, token);
                clock.Restart();
                using var retry = await SendAsync(client, HttpMethod.Post, "/api/generate", new { model, stream = false }, LoadLimit, token);
                if (!retry.Ok) throw Failure($"Ollama couldn't load {model}", retry, model, output);
            }
            else if (!load.Ok) throw Failure($"Ollama couldn't load {model}", load, model, output);
        }
        var loaded = clock.Elapsed;
        output.Report($"Loaded in {Seconds(loaded)}.");
        var placement = await PlacementAsync(client, model, token);
        if (placement is not null) output.Report(placement);
        status($"Asking {model} to say hello...");
        output.Report($"Asking {model} for a test reply...");
        var reply = await AskAsync(client, model, replyTokens, reasoning, output, token);
        output.Report($"First words after {Seconds(reply.FirstWords)}. Finished after {Seconds(reply.Total)}.");
        output.Report($"Reply: {reply.Text}");

        var abilities = capabilities.Length > 0 ? $" {Abilities(capabilities)}." : "";
        var limit = Seconds(firstWordsLimit);
        if (reply.FirstWords > firstWordsLimit)
            return new($"{model} works, but starts too slowly for Martlet. First words took {Seconds(reply.FirstWords)}. Martlet waits {limit}. Choose a smaller model.{abilities}", Warning: true);
        if (loaded > firstWordsLimit)
            return new($"{model} works once loaded, but loading took {Seconds(loaded)}. If a reply times out, try again after the model finishes loading.{abilities}", Warning: true);
        return new($"{model} works on this PC. It loaded in {Seconds(loaded)} and answered in {Seconds(reply.Total)}.{abilities}", Warning: false);
    }

    /// <summary>Saves draft_num_predict 0 on <paramref name="model"/> in this PC's Ollama so it loads without its
    /// speculative-decoding draft model (see <see cref="OllamaDraftHead"/>).</summary>
    internal static async Task DisableDraftAsync(HttpClient client, string model, CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromMinutes(1));
        try
        {
            using var content = new StringContent(OllamaDraftHead.DisableRequest(model), Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(Origin + OllamaDraftHead.CreatePath, content, limit.Token);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Ollama couldn't turn off {model}'s draft model (error {(int)response.StatusCode}): " +
                    $"{ErrorText(await response.Content.ReadAsStringAsync(limit.Token)) ?? "Ollama gave no reason"}.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Ollama didn't turn off {model}'s draft model within a minute.");
        }
        catch (HttpRequestException error)
        {
            throw new InvalidOperationException($"Ollama stopped answering ({error.Message}).");
        }
        ErrorLog.Info($"Turned off the speculative-decoding draft model of {model} in Ollama on this PC; it didn't fit in graphics memory.");
    }

    private sealed record Reply(string Text, TimeSpan FirstWords, TimeSpan Total);

    /// <summary>One streamed Chat Completions request, the shape a reply uses, with its Thinking steps choice
    /// (<paramref name="reasoning"/>). Like a reply, reasoning counts as the first words, and an empty or cut-off answer fails.</summary>
    private static async Task<Reply> AskAsync(HttpClient client, string model, int? replyTokens, bool? reasoning, IProgress<string> output,
        CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(AnswerLimit);
        var clock = Stopwatch.StartNew();
        try
        {
            var payload = new Dictionary<string, object>
            {
                ["model"] = model, ["stream"] = true,
                ["messages"] = new[] { new { role = "user", content = TestPrompt } }
            };
            if (replyTokens is { } budget) payload["max_tokens"] = budget;
            if (reasoning is { } think)
                payload["reasoning_effort"] = think ? GenerationSupport.ReasoningEffortOn : GenerationSupport.ReasoningEffortOff;
            using var request = new HttpRequestMessage(HttpMethod.Post, Origin + "/v1/chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(limit.Token);
                if (OutOfGraphicsMemory(body))
                {
                    output.Report($"Ollama said: {ErrorText(body)}.");
                    throw new InvalidOperationException(GraphicsMemoryAdvice(model));
                }
                throw new InvalidOperationException($"{model} didn't answer (error {(int)response.StatusCode}): {ErrorText(body) ?? "Ollama gave no reason"}.");
            }
            using var stream = await response.Content.ReadAsStreamAsync(limit.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = new StringBuilder();
            TimeSpan? first = null;
            var thinking = false;
            string? finish = null;
            var done = false;
            while (await reader.ReadLineAsync(limit.Token) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var data = line[5..].Trim();
                if (data == "[DONE]") { done = true; break; }
                using var chunk = JsonDocument.Parse(data);
                var root = chunk.RootElement;
                if (root.TryGetProperty("error", out _))
                    throw new InvalidOperationException($"{model} stopped answering: {ErrorText(data) ?? "Ollama gave no reason"}.");
                if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) continue;
                var choice = choices[0];
                if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                {
                    if (JsonText(delta, "content") is { Length: > 0 } content)
                    {
                        first ??= clock.Elapsed;
                        if (text.Length < 2000) text.Append(content);
                    }
                    if (JsonText(delta, "reasoning") is { Length: > 0 } || JsonText(delta, "reasoning_content") is { Length: > 0 })
                    {
                        first ??= clock.Elapsed;
                        if (!thinking) output.Report($"{model} is thinking first...");
                        thinking = true;
                    }
                }
                finish = JsonText(choice, "finish_reason") ?? finish;
            }
            var said = text.ToString().Trim();
            if (!done || finish is null) throw new InvalidOperationException($"{model}'s answer was cut off: Ollama ended the reply early.");
            if (finish == "length")
                throw new InvalidOperationException($"{model} used its whole reply budget{(replyTokens is { } cap ? $" ({cap} tokens)" : "")} before finishing" +
                    (thinking ? ", thinking before it answered" : "") + ". Raise or clear Max reply length in Companion › Replies, or choose a chat model.");
            if (said.Length == 0) throw new InvalidOperationException($"{model} answered with nothing.");
            return new(said.Length > 300 ? said[..300] + "…" : said, first ?? clock.Elapsed, clock.Elapsed);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            // A model that overfills the graphics card (beside a game, say) is paged out to system memory and stalls.
            throw new InvalidOperationException($"{model} didn't answer within {AnswerLimit.TotalMinutes:0} minutes. If a game or other " +
                "programs are using the graphics card, choose a smaller model or close them, then test again.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Ollama sent a response Martlet couldn't read.");
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            throw new InvalidOperationException($"Ollama stopped answering while {model} replied ({error.Message}).");
        }
    }

    /// <summary>Where Ollama put the loaded model: on the graphics card, the processor or split between them.</summary>
    private static async Task<string?> PlacementAsync(HttpClient client, string model, CancellationToken token)
    {
        using var running = await SendAsync(client, HttpMethod.Get, "/api/ps", null, TimeSpan.FromSeconds(10), token);
        if (!running.Ok || !running.Body!.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array) return null;
        foreach (var entry in models.EnumerateArray())
        {
            if (JsonText(entry, "name") != model && JsonText(entry, "model") != model) continue;
            if (!entry.TryGetProperty("size", out var s) || !s.TryGetInt64(out var size) || size <= 0) return null;
            var vram = entry.TryGetProperty("size_vram", out var v) && v.TryGetInt64(out var onGpu) ? onGpu : 0;
            var share = (int)Math.Round(100.0 * Math.Min(vram, size) / size);
            return "It runs " + share switch
            {
                >= 100 => "all on the graphics card.",
                <= 0 => "on the processor (slower than a graphics card).",
                _ => $"{share}% on the graphics card and the rest on the processor (slower)."
            };
        }
        return null;
    }

    private sealed class Answer(HttpStatusCode status, JsonDocument? body, string text) : IDisposable
    {
        internal HttpStatusCode Status { get; } = status;
        internal bool Ok => (int)Status is >= 200 and <= 299 && Body is not null;
        internal JsonDocument? Body { get; } = body;
        internal string Text { get; } = text;
        public void Dispose() => Body?.Dispose();
    }

    private static async Task<Answer> SendAsync(HttpClient client, HttpMethod method, string path, object? body, TimeSpan within, CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(within);
        try
        {
            using var request = new HttpRequestMessage(method, Origin + path);
            if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request, limit.Token);
            var text = await response.Content.ReadAsStringAsync(limit.Token);
            JsonDocument? parsed = null;
            try { parsed = JsonDocument.Parse(text); }
            catch (JsonException) { }
            return new(response.StatusCode, parsed, text);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Ollama didn't answer within {Seconds(within)}.");
        }
        catch (HttpRequestException error)
        {
            throw new InvalidOperationException($"Ollama stopped answering ({error.Message}). Start it from the Start menu, then test again.");
        }
    }

    private static InvalidOperationException Failure(string what, Answer answer) =>
        new($"{what} (error {(int)answer.Status}): {ErrorText(answer.Text) ?? "Ollama gave no reason"}.");

    /// <summary>Like <see cref="Failure(string, Answer)"/>, but a load that ran out of graphics memory says so in plain words
    /// (Ollama's own text goes to the run output).</summary>
    private static InvalidOperationException Failure(string what, Answer answer, string model, IProgress<string> output)
    {
        if (!OutOfGraphicsMemory(answer.Text)) return Failure(what, answer);
        output.Report($"Ollama said: {ErrorText(answer.Text)}.");
        return new(GraphicsMemoryAdvice(model));
    }

    /// <summary>Whether Ollama's error means the model didn't fit in the graphics memory left free. A game and other
    /// programs share the card. A Gemma 4 draft model that doesn't fit is turned off and the model loaded again (see
    /// <see cref="OllamaDraftHead"/>); this is for a load that still runs out of graphics memory after that.
    /// Checks the whole error body, before any shortening.</summary>
    internal static bool OutOfGraphicsMemory(string? error) =>
        error is not null && GraphicsMemoryErrors.Any(marker => error.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] GraphicsMemoryErrors =
    [
        "error loading model: vector", "invalid vector subscript", "vector::_M_range_check",
        "cudaMalloc failed", "CUDA error: out of memory", "ErrorOutOfDeviceMemory"
    ];

    /// <summary>What to do when <paramref name="model"/> doesn't fit in the graphics memory free on this PC.</summary>
    internal static string GraphicsMemoryAdvice(string model) =>
        $"{model} doesn't fit in the graphics memory free on this PC right now, so Ollama couldn't load it. " +
        "Games and other programs share the graphics card. " +
        (MainWindow.SmallerLocalModel(model) is { } smaller
            ? $"Choose a smaller model, such as {smaller.Id} ({smaller.Size}), or close programs that use the graphics card."
            : "Close programs that use the graphics card, or think with a cloud provider instead.");

    /// <summary>Ollama's own explanation from an error body (<c>{"error":"..."}</c>, or OpenAI-style
    /// <c>{"error":{"message":"..."}}</c>), on one bounded line.</summary>
    internal static string? ErrorText(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var text = body;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("error", out var error))
                text = error.ValueKind == JsonValueKind.String ? error.GetString() ?? body
                    : JsonText(error, "message") ?? error.GetRawText();
        }
        catch (JsonException) { }
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.');
        return line.Length <= 400 ? line : line[..400] + "…";
    }

    private static string? JsonText(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static string Abilities(IReadOnlyCollection<string> capabilities)
    {
        var extras = new List<string>();
        if (capabilities.Contains("vision")) extras.Add("vision");
        if (capabilities.Contains("tools")) extras.Add("tools");
        return extras.Count == 0 ? "Ready for chat" : "Also supports " + string.Join(" and ", extras);
    }

    private static string Seconds(TimeSpan time) => time.TotalSeconds < 10
        ? $"{time.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture)} s"
        : $"{Math.Round(time.TotalSeconds):0} s";
}

/// <summary>What testing a local model found: a summary for the owner and whether it works but needs attention.</summary>
internal sealed record LocalModelTestResult(string Summary, bool Warning);
