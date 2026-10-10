using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Martlet.Core.Contracts;
using Martlet.Core.Sync;

namespace Martlet.Avatar.Audio2Face.Remote;

/// <summary>The host's copy of everything Martlet remembers (docs/MEMORY.md, "One memory on every computer") and its memory
/// spaces (docs/ACCOUNTS.md, "Memory spaces"). Hosts older than it refuse with <c>request.invalid</c>.</summary>
public sealed partial class Audio2FaceHostConnection
{
    private const string MemoriesPath = "/martlet/v1/memories";
    private const string MemoriesDigestSuffix = "/digest";
    private const string MemoriesDigestPath = MemoriesPath + MemoriesDigestSuffix;
    private const string MemorySpacesPath = MemoriesPath + "/spaces/";
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
        return await MemoriesAsync(request, null, cancellationToken).ConfigureAwait(false);
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
            return await MemoriesAsync(request, null, cancellationToken).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(body); }
    }

    /// <summary>The digest of the host's copy of the memory space <paramref name="space"/> (<see cref="MemorySpaceId"/>), to
    /// read the space only when it changed. A space the host has never kept has the digest of an empty copy. Hosts older than
    /// memory spaces refuse with <c>request.invalid</c>; a device that may not read the space gets
    /// <c>memories.space_denied</c>.</summary>
    public async Task<string> ReadMemorySpaceDigestAsync(string space, CancellationToken cancellationToken = default)
    {
        RequireSpace(space);
        using var request = new HttpRequestMessage(HttpMethod.Get, pairing.Origin + MemorySpacesPath + space + MemoriesDigestSuffix);
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
            if (root.GetProperty("space").GetString() != space) throw new FormatException();
            var digest = root.GetProperty("digest").GetString();
            if (digest is not { Length: 64 } || !digest.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) throw new FormatException();
            return digest;
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new Audio2FaceHostException("response.invalid", "The host's memory space digest was invalid.");
        }
    }

    /// <summary>Reads the host's copy of the memory space <paramref name="space"/>; empty when the host has never kept it.</summary>
    public async Task<SharedMemories> ReadMemorySpaceAsync(string space, CancellationToken cancellationToken = default)
    {
        RequireSpace(space);
        using var request = new HttpRequestMessage(HttpMethod.Get, pairing.Origin + MemorySpacesPath + space);
        Sign(request, []);
        return await MemoriesAsync(request, space, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Merges <paramref name="memories"/> into the host's copy of the memory space <paramref name="space"/> and returns
    /// the merged copy, which may include newer changes another computer made. A host that keeps as many spaces as it allows
    /// refuses a new one with <c>memories.spaces_full</c>.</summary>
    public async Task<SharedMemories> MergeMemorySpaceAsync(string space, SharedMemories memories, CancellationToken cancellationToken = default)
    {
        RequireSpace(space);
        ArgumentNullException.ThrowIfNull(memories);
        var body = memories.Write();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, pairing.Origin + MemorySpacesPath + space)
            {
                Content = Audio2FaceHostClient.JsonContent(body)
            };
            Sign(request, body);
            return await MemoriesAsync(request, space, cancellationToken).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(body); }
    }

    private static void RequireSpace(string space)
    {
        if (!MemorySpaceId.IsValid(space)) throw new ArgumentException("Not a memory space ID.", nameof(space));
    }

    /// <summary>Gives <paramref name="memories"/> (1-64 facts, none forgotten) to the memory space <paramref name="space"/>: the
    /// host adds the facts whose IDs the space doesn't have and answers how many it took, never the space itself. This is how
    /// a fact is shared with another account, whose space this device may not read (docs/ACCOUNTS.md, "Sharing"). Hosts older
    /// than it refuse with <c>request.invalid</c>.</summary>
    public async Task<int> GiveMemoriesAsync(string space, SharedMemories memories, CancellationToken cancellationToken = default)
    {
        RequireSpace(space);
        ArgumentNullException.ThrowIfNull(memories);
        if (memories.Facts.Count is 0 or > 64 || memories.Forgotten.Any())
            throw new ArgumentException("Give 1-64 facts and no forgotten ones.", nameof(memories));
        var body = memories.Write();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, pairing.Origin + MemorySpacesPath + space + "/give")
            {
                Content = Audio2FaceHostClient.JsonContent(body)
            };
            Sign(request, body);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await Audio2FaceHostClient.Send(http, request, timeout.Token).ConfigureAwait(false);
            using var document = await Audio2FaceHostClient.ReadJson(response, 4_096, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK) throw Audio2FaceHostClient.Remote(document.RootElement);
            try
            {
                var root = document.RootElement;
                if (root.GetProperty("host_id").GetString() != pairing.HostId)
                    throw new Audio2FaceHostException("response.invalid", "The host identity changed; pair again.");
                if (root.GetProperty("space").GetString() != space) throw new FormatException();
                var taken = root.GetProperty("taken").GetInt32();
                if (taken < 0 || taken > memories.Facts.Count) throw new FormatException();
                return taken;
            }
            catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException)
            {
                throw new Audio2FaceHostException("response.invalid", "The host's answer to shared facts was invalid.");
            }
        }
        finally { CryptographicOperations.ZeroMemory(body); }
    }

    private async Task<SharedMemories> MemoriesAsync(HttpRequestMessage request, string? space, CancellationToken cancellationToken)
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
            if (space is not null && root.GetProperty("space").GetString() != space)
                throw new Audio2FaceHostException("response.invalid", "The host answered for another memory space.");
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
