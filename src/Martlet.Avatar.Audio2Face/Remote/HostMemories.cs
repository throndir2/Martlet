using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Martlet.Core.Contracts;
using Martlet.Core.Sync;

namespace Martlet.Avatar.Audio2Face.Remote;

/// <summary>The host's copy of everything Martlet remembers (docs/MEMORY.md, "One memory on every computer"). Hosts older than
/// it refuse with <c>request.invalid</c>.</summary>
public sealed partial class Audio2FaceHostConnection
{
    private const string MemoriesPath = "/martlet/v1/memories";
    private const string MemoriesDigestPath = MemoriesPath + "/digest";
    private const int MaximumMemoriesResponseBytes = SharedMemories.MaximumBytes + 16_384;

    /// <summary>The digest of the host's copy, to read the copy only when it changed.</summary>
    public async Task<string> ReadMemoriesDigestAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, pairing.Origin + MemoriesDigestPath);
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
            throw new Audio2FaceHostException("response.invalid", "The host's shared memories digest was invalid.");
        }
    }

    /// <summary>Reads the host's copy of everything Martlet remembers.</summary>
    public async Task<SharedMemories> ReadMemoriesAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, pairing.Origin + MemoriesPath);
        Sign(request, []);
        return await MemoriesAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Merges <paramref name="memories"/> into the host's copy and returns the merged copy, which may include newer
    /// changes another computer made.</summary>
    public async Task<SharedMemories> MergeMemoriesAsync(SharedMemories memories, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(memories);
        var body = memories.Write();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, pairing.Origin + MemoriesPath)
            {
                Content = Audio2FaceHostClient.JsonContent(body)
            };
            Sign(request, body);
            return await MemoriesAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(body); }
    }

    private async Task<SharedMemories> MemoriesAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var response = await Audio2FaceHostClient.Send(http, request, timeout.Token).ConfigureAwait(false);
        using var document = await Audio2FaceHostClient.ReadJson(response, MaximumMemoriesResponseBytes, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw Audio2FaceHostClient.Remote(document.RootElement);
        byte[]? bytes = null;
        try
        {
            var root = document.RootElement;
            if (root.GetProperty("host_id").GetString() != pairing.HostId)
                throw new Audio2FaceHostException("response.invalid", "The host identity changed; pair again.");
            bytes = JsonSerializer.SerializeToUtf8Bytes(root.GetProperty("memories"));
            return SharedMemories.Parse(bytes);
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or ContractException)
        {
            throw new Audio2FaceHostException("response.invalid", "The host's copy of the shared memories was invalid.");
        }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }
}
