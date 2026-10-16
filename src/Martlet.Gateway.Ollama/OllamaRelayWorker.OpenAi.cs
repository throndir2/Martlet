using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Martlet.Core.Access;
using Martlet.Providers.Ollama;

namespace Martlet.Gateway.Ollama;

/// <summary>The OpenAI-compatible endpoint on this role's Ollama (docs/OPENAI_ENDPOINT.md). Each request is translated to
/// Ollama's native API, with the context size of the owner's own requests and the model kept loaded, so a request from
/// outside never makes Ollama load the model again (which would make the owner's next reply wait). Only this role's one
/// model is reachable, and pictures come only as data: URLs, so the host never fetches anything for a request.</summary>
public sealed partial class OllamaRelayWorker
{
    private const int MaximumOpenAiLineBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan LoadedRead = TimeSpan.FromSeconds(5);
    private int ownerContext;
    private (DateTimeOffset At, int? Context)? loadedContext;

    public string OpenAiModel => model;

    /// <summary>The context size an OpenAI-compatible request runs with: the owner's last request here, else what Ollama has
    /// loaded now (one slot only; a role with several slots splits its context), else the relay's default.</summary>
    internal async Task<int> OpenAiContextAsync(CancellationToken token)
    {
        if (Volatile.Read(ref ownerContext) is > 0 and var owner) return owner;
        if (Route.MaximumConcurrency == 1)
        {
            var now = DateTimeOffset.UtcNow;
            if (loadedContext is not { } read || now - read.At > LoadedRead)
            {
                var loaded = await ReadLoadedAsync(token).ConfigureAwait(false);
                read = (now, loaded?.FirstOrDefault(m => string.Equals(m.Model, model, StringComparison.Ordinal))?.ContextTokens);
                loadedContext = read;
            }
            if (read.Context is > 0 and var context) return context;
        }
        return MaximumContextTokens;
    }

    public async Task<GatewayOpenAiAnswer> RunOpenAiAsync(string path, JsonElement request, CancellationToken cancellationToken)
    {
        if (lifetime.IsCancellationRequested)
            return GatewayOpenAiAnswer.Error(503, "This model's host is stopping. Try again in a moment.", "worker.unavailable");
        var context = await OpenAiContextAsync(cancellationToken).ConfigureAwait(false);
        OllamaOpenAi.Translation translation;
        try
        {
            translation = path switch
            {
                OpenAiEndpoint.ChatPath => OllamaOpenAi.Chat(request, model, context),
                OpenAiEndpoint.CompletionsPath => OllamaOpenAi.Completion(request, model, context),
                OpenAiEndpoint.EmbeddingsPath => OllamaOpenAi.Embedding(request, model, context),
                _ => throw new OllamaOpenAi.RefusedException("This path isn't part of the OpenAI-compatible endpoint.", null, 404)
            };
        }
        catch (OllamaOpenAi.RefusedException refused)
        {
            return GatewayOpenAiAnswer.Error(refused.Status, refused.Message, "invalid_request", refused.Param);
        }
        var target = new Uri(origin, translation.NativePath.TrimStart('/'));
        var response = await PostAsync(target, translation.Body, cancellationToken).ConfigureAwait(false);
        if (response is null) return GatewayOpenAiAnswer.Error(503, "The model's server on the host didn't answer.", "worker.unavailable");
        if (response.StatusCode != HttpStatusCode.OK)
        {
            var error = await ErrorAsync(response, cancellationToken).ConfigureAwait(false);
            if (OllamaDraftHead.FailedToLoad(error))
            {
                // Gemma 4's bundled draft model didn't fit beside it: turn the draft off, as the native relay does, and try again.
                response.Dispose();
                using (var repair = await PostAsync(create, Encoding.UTF8.GetBytes(OllamaDraftHead.DisableRequest(model)), cancellationToken)
                    .ConfigureAwait(false))
                    if (repair is not { IsSuccessStatusCode: true })
                        return GatewayOpenAiAnswer.Error(502, "The model's server on the host couldn't load the model.", "worker.failed");
                response = await PostAsync(target, translation.Body, cancellationToken).ConfigureAwait(false);
                if (response is null) return GatewayOpenAiAnswer.Error(503, "The model's server on the host didn't answer.", "worker.unavailable");
                if (response.StatusCode != HttpStatusCode.OK) error = await ErrorAsync(response, cancellationToken).ConfigureAwait(false);
            }
            if (response.StatusCode != HttpStatusCode.OK)
            {
                var status = (int)response.StatusCode;
                response.Dispose();
                return GatewayOpenAiAnswer.Error(status is >= 400 and < 500 ? status : 502,
                    "The model's server on the host refused the request: " + OllamaOpenAi.ErrorText(error), "worker.refused");
            }
        }
        if (!translation.Stream)
        {
            using (response)
            {
                var bytes = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
                try
                {
                    using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
                    return GatewayOpenAiAnswer.Json(200, translation.Finish(document.RootElement));
                }
                catch (JsonException)
                {
                    return GatewayOpenAiAnswer.Error(502, "The model's server on the host gave an answer Martlet couldn't read.", "worker.failed");
                }
            }
        }
        return new GatewayOpenAiAnswer { Status = 200, ContentType = "text/event-stream", Body = StreamAsync(response, translation, cancellationToken) };
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, CancellationToken token)
    {
        await using var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16_384];
        int read;
        while ((read = await body.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > MaximumOpenAiLineBytes * 4) throw new InvalidDataException("The Ollama answer is too large.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> StreamAsync(HttpResponseMessage response, OllamaOpenAi.Translation translation,
        [EnumeratorCancellation] CancellationToken token)
    {
        using (response)
        {
            await using var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var reader = new OpenAiLineReader(body);
            var finished = false;
            while (!finished)
            {
                byte[]? line;
                try { line = await reader.ReadLineAsync(token).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or HttpRequestException or InvalidDataException)
                {
                    token.ThrowIfCancellationRequested();
                    line = null;
                }
                if (line is null)
                {
                    yield return OllamaOpenAi.Event(GatewayOpenAiAnswer.ErrorBody(502, "The model's server on the host stopped answering.", "worker.failed"));
                    yield return OllamaOpenAi.Done;
                    yield break;
                }
                if (line.Length == 0) continue;
                IReadOnlyList<byte[]> events;
                try
                {
                    using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 64 });
                    (events, finished) = translation.Next(document.RootElement);
                }
                catch (JsonException)
                {
                    (events, finished) = ([OllamaOpenAi.Event(GatewayOpenAiAnswer.ErrorBody(502, "The model's server on the host gave an answer Martlet couldn't read.", "worker.failed"))], true);
                }
                foreach (var item in events) yield return item;
            }
            yield return OllamaOpenAi.Done;
        }
    }

    private sealed class OpenAiLineReader(Stream stream)
    {
        private readonly byte[] buffer = new byte[16_384];
        private readonly MemoryStream line = new();
        private int offset, count;

        internal async Task<byte[]?> ReadLineAsync(CancellationToken token)
        {
            line.SetLength(0);
            while (true)
            {
                if (offset == count)
                {
                    count = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                    offset = 0;
                    if (count == 0) return line.Length == 0 ? null : line.ToArray();
                }
                var newline = Array.IndexOf(buffer, (byte)'\n', offset, count - offset);
                var end = newline < 0 ? count : newline;
                line.Write(buffer, offset, end - offset);
                offset = newline < 0 ? count : newline + 1;
                if (line.Length > MaximumOpenAiLineBytes) throw new InvalidDataException("An Ollama line is too long.");
                if (newline >= 0) return line.ToArray();
            }
        }
    }
}
