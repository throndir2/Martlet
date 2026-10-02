using System.Net;
using System.Text.Json;
using Martlet.Core.Access;
using Martlet.Core.Contracts;

namespace Martlet.Avatar.Audio2Face.Remote;

/// <summary>A host's copy of the network's API keys and when each was last used there.</summary>
public sealed record HostApiKeys(ApiKeyList Keys, IReadOnlyDictionary<string, DateTimeOffset> Used);

/// <summary>The host's copy of the API keys of the owner's Martlet network (docs/API.md). Only paired devices can read or
/// change it; the keys themselves are presented by software outside the network as Authorization: Bearer.</summary>
public sealed partial class Audio2FaceHostConnection
{
    private const string ApiKeysPath = "/martlet/v1/api-keys";

    /// <summary>Reads the host's copy of the API keys. Hosts older than API keys refuse with code <c>request.invalid</c>.</summary>
    public async Task<HostApiKeys> ReadApiKeysAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, pairing.Origin + ApiKeysPath);
        Sign(request, []);
        return await ApiKeysAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Merges <paramref name="keys"/> into the host's copy and returns the merged copy, which may include keys another
    /// computer created or revoked.</summary>
    public async Task<HostApiKeys> MergeApiKeysAsync(ApiKeyList keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var body = keys.Write();
        using var request = new HttpRequestMessage(HttpMethod.Post, pairing.Origin + ApiKeysPath)
        {
            Content = Audio2FaceHostClient.JsonContent(body)
        };
        Sign(request, body);
        return await ApiKeysAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HostApiKeys> ApiKeysAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var response = await Audio2FaceHostClient.Send(http, request, timeout.Token).ConfigureAwait(false);
        using var document = await Audio2FaceHostClient.ReadJson(response, ApiKeyList.MaximumBytes + 16_384, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw Audio2FaceHostClient.Remote(document.RootElement);
        try
        {
            var root = document.RootElement;
            if (root.GetProperty("host_id").GetString() != pairing.HostId)
                throw new Audio2FaceHostException("response.invalid", "The host identity changed; pair again.");
            var keys = ApiKeyList.Parse(JsonSerializer.SerializeToUtf8Bytes(root.GetProperty("keys")));
            var used = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            if (root.TryGetProperty("used", out var map) && map.ValueKind == JsonValueKind.Object)
                foreach (var item in map.EnumerateObject().Take(ApiKeyList.MaximumKeys))
                    if (ApiKeyList.IsId(item.Name) && item.Value.TryGetDateTimeOffset(out var at)) used[item.Name] = at;
            return new(keys, used);
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or ContractException)
        {
            throw new Audio2FaceHostException("response.invalid", "The host's copy of the API keys was invalid.");
        }
    }
}
