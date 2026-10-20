using System.Net;
using System.Text.Json;
using Martlet.Core.Access;

namespace Martlet.HostClient;

/// <summary>The language models a host offers through its OpenAI-compatible endpoint (docs/OPENAI_ENDPOINT.md), read with this
/// computer's signed pairing: the owner's computers and friends' computers see the host's own models.</summary>
public sealed partial class HostConnection
{
    /// <summary>The model names the host runs itself (Ollama tags such as qwen3:8b). Hosts older than the endpoint refuse with
    /// code <c>request.invalid</c>.</summary>
    public async Task<IReadOnlyList<string>> ReadOpenAiModelsAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, pairing.Origin + OpenAiEndpoint.ModelsPath);
        Sign(request, []);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var response = await HostTransport.Send(http, request, timeout.Token).ConfigureAwait(false);
        using var document = await HostTransport.ReadJson(response, 256 * 1024, timeout.Token).ConfigureAwait(false);
        var root = document.RootElement;
        if (response.StatusCode != HttpStatusCode.OK)
        {
            // An older host answers with Martlet's failure; a newer one in OpenAI's form.
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                throw new HostConnectionException(error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
                    ? code.GetString()! : "request.invalid", error.TryGetProperty("message", out var message) ? message.GetString() ?? "" : "");
            throw HostTransport.Remote(root);
        }
        try
        {
            return root.GetProperty("data").EnumerateArray().Select(m => m.GetProperty("id").GetString()).OfType<string>()
                .Where(m => m.Length is > 0 and <= 128).Distinct(StringComparer.Ordinal).Take(64).ToArray();
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException)
        {
            throw new HostConnectionException("response.invalid", "The host's list of models was invalid.");
        }
    }
}
