using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Martlet.Core.Contracts;
using Martlet.Core.Sync;

namespace Martlet.Avatar.Audio2Face.Remote;

/// <summary>The host's copy of the settings the owner's computers share (docs/CLUSTER.md, "One Martlet on every computer"),
/// including their API keys. Hosts older than it refuse with <c>request.invalid</c>.</summary>
public sealed partial class Audio2FaceHostConnection
{
    private const string SettingsPath = "/martlet/v1/settings";
    private const string SettingsDigestPath = SettingsPath + "/digest";
    private const int MaximumSettingsResponseBytes = SharedSettings.MaximumBytes + 16_384;

    /// <summary>The digest of the host's copy, to read the copy only when it changed.</summary>
    public async Task<string> ReadSettingsDigestAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, pairing.Origin + SettingsDigestPath);
        Sign(request, []);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var response = await Audio2FaceHostClient.Send(http, request, timeout.Token).ConfigureAwait(false);
        using var document = await Audio2FaceHostClient.ReadJson(response, 4_096, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw Audio2FaceHostClient.Remote(document.RootElement);
        try
        {
            var root = document.RootElement;
            if (root.GetProperty("host_id").GetString() != pairing.HostId)
                throw new Audio2FaceHostException("response.invalid", "The host identity changed; pair again.");
            var digest = root.GetProperty("digest").GetString();
            if (digest is not { Length: 64 } || !digest.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) throw new FormatException();
            return digest;
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new Audio2FaceHostException("response.invalid", "The host's shared settings digest was invalid.");
        }
    }

    /// <summary>Reads the host's copy of the shared settings, with the API keys it holds.</summary>
    public async Task<SharedSettings> ReadSettingsAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, pairing.Origin + SettingsPath);
        Sign(request, []);
        return await SettingsAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Merges <paramref name="settings"/> (with the API keys this computer knows) into the host's copy and returns the
    /// merged copy, which may include newer changes another computer made.</summary>
    public async Task<SharedSettings> MergeSettingsAsync(SharedSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var body = settings.Write();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, pairing.Origin + SettingsPath)
            {
                Content = Audio2FaceHostClient.JsonContent(body)
            };
            Sign(request, body);
            return await SettingsAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(body); }
    }

    private async Task<SharedSettings> SettingsAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await Audio2FaceHostClient.Send(http, request, timeout.Token).ConfigureAwait(false);
        using var document = await Audio2FaceHostClient.ReadJson(response, MaximumSettingsResponseBytes, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw Audio2FaceHostClient.Remote(document.RootElement);
        byte[]? bytes = null;
        try
        {
            var root = document.RootElement;
            if (root.GetProperty("host_id").GetString() != pairing.HostId)
                throw new Audio2FaceHostException("response.invalid", "The host identity changed; pair again.");
            bytes = JsonSerializer.SerializeToUtf8Bytes(root.GetProperty("settings"));
            return SharedSettings.Parse(bytes);
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or ContractException)
        {
            throw new Audio2FaceHostException("response.invalid", "The host's copy of the shared settings was invalid.");
        }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }
}
