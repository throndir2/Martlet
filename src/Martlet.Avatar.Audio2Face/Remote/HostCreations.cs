using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Martlet.Core.Contracts;
using Martlet.Core.Creations;

namespace Martlet.Avatar.Audio2Face.Remote;

/// <summary>A host's copy of Martlet's creations and the SHA-256 of each piece it holds.</summary>
public sealed record HostCreations(CreationLibrary Library, IReadOnlySet<string> Present);

/// <summary>The digests of a host's copy of Martlet's creations and of the pieces it holds, to tell whether reading the copy
/// is needed.</summary>
public sealed record HostCreationsDigest(string Digest, string PresentDigest, int PresentCount);

/// <summary>The host's copy of Martlet's creations and their pieces (docs/CREATIONS.md): the old single list, and each
/// account's own list (docs/ACCOUNTS.md) at /creations/accounts/&lt;32 hex&gt;. Hosts older than creations refuse the first with
/// <c>request.invalid</c>; hosts older than accounts refuse the account routes the same way. A device that may not use an
/// account's creations gets <c>creations.account_denied</c>.</summary>
public sealed partial class Audio2FaceHostConnection
{
    private const string CreationsPath = "/martlet/v1/creations";
    private const string CreationAccountsPath = CreationsPath + "/accounts/";
    private const int MaximumCreationsBytes = CreationLibrary.MaximumBytes + 2 * 1024 * 1024;
    private const int MaximumCreationChunkBytes = (CreationLibrary.ChunkBytes + 2) / 3 * 4 + 4_096;

    public Task<HostCreations> ReadCreationsAsync(CancellationToken cancellationToken = default) =>
        ReadCreationsAsync(null, cancellationToken);

    /// <summary>Reads <paramref name="account"/>'s own list on the host; empty when the host keeps none for it.</summary>
    public Task<HostCreations> ReadAccountCreationsAsync(Guid account, CancellationToken cancellationToken = default) =>
        ReadCreationsAsync(account, cancellationToken);

    public Task<HostCreationsDigest> ReadCreationsDigestAsync(CancellationToken cancellationToken = default) =>
        ReadCreationsDigestAsync(null, cancellationToken);

    public Task<HostCreationsDigest> ReadAccountCreationsDigestAsync(Guid account, CancellationToken cancellationToken = default) =>
        ReadCreationsDigestAsync(account, cancellationToken);

    /// <summary>Merges <paramref name="library"/> into the host's copy and returns the merged list, which may include changes
    /// another computer made, and the pieces the host holds.</summary>
    public Task<HostCreations> MergeCreationsAsync(CreationLibrary library, CancellationToken cancellationToken = default) =>
        MergeCreationsAsync(null, library, cancellationToken);

    /// <summary>Merges <paramref name="library"/> into <paramref name="account"/>'s own list on the host. A host that keeps as
    /// many account lists as it allows refuses a new one with <c>creations.accounts_full</c>.</summary>
    public Task<HostCreations> MergeAccountCreationsAsync(Guid account, CreationLibrary library, CancellationToken cancellationToken = default) =>
        MergeCreationsAsync(account, library, cancellationToken);

    /// <summary>Sends one piece of a live creation to the host and returns the pieces it now holds. The host keeps it only
    /// when its list has a live creation with that piece (merge the list first).</summary>
    public Task<IReadOnlySet<string>> SendCreationChunkAsync(string sha256, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
        SendCreationChunkAsync(null, sha256, data, cancellationToken);

    /// <summary>Sends one piece of a live creation of <paramref name="account"/>'s list; returns the pieces of that list the
    /// host now holds.</summary>
    public Task<IReadOnlySet<string>> SendAccountCreationChunkAsync(Guid account, string sha256, ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default) =>
        SendCreationChunkAsync(account, sha256, data, cancellationToken);

    /// <summary>Reads a piece the host holds, checked against <paramref name="sha256"/>; null when it has none.</summary>
    public Task<byte[]?> ReadCreationChunkAsync(string sha256, CancellationToken cancellationToken = default) =>
        ReadCreationChunkAsync(null, sha256, cancellationToken);

    /// <summary>Reads a piece of a live creation of <paramref name="account"/>'s list; null when the host has none.</summary>
    public Task<byte[]?> ReadAccountCreationChunkAsync(Guid account, string sha256, CancellationToken cancellationToken = default) =>
        ReadCreationChunkAsync(account, sha256, cancellationToken);

    private static string CreationsRoute(Guid? account) => account is { } id ? CreationAccountsPath + id.ToString("N") : CreationsPath;

    private async Task<HostCreations> ReadCreationsAsync(Guid? account, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, pairing.Origin + CreationsRoute(account));
        Sign(request, []);
        return await CreationsAsync(request, account, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HostCreationsDigest> ReadCreationsDigestAsync(Guid? account, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, pairing.Origin + CreationsRoute(account) + "/digest");
        Sign(request, []);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await Audio2FaceHostClient.Send(http, request, timeout.Token).ConfigureAwait(false);
        using var document = await Audio2FaceHostClient.ReadJson(response, 16_384, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw Audio2FaceHostClient.Remote(document.RootElement);
        try
        {
            var root = document.RootElement;
            if (root.GetProperty("host_id").GetString() != pairing.HostId)
                throw new Audio2FaceHostException("response.invalid", "The host identity changed; pair again.");
            RequireAccount(root, account);
            var digest = root.GetProperty("digest").GetString();
            var present = root.GetProperty("present_digest").GetString();
            if (!CreationLibrary.IsSha256(digest) || !CreationLibrary.IsSha256(present))
                throw new Audio2FaceHostException("response.invalid", "The host's creation digest was invalid.");
            return new(digest!, present!, root.GetProperty("present_count").GetInt32());
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new Audio2FaceHostException("response.invalid", "The host's creation digest was invalid.");
        }
    }

    private async Task<HostCreations> MergeCreationsAsync(Guid? account, CreationLibrary library, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(library);
        var body = library.Write();
        using var request = new HttpRequestMessage(HttpMethod.Post, pairing.Origin + CreationsRoute(account))
        {
            Content = Audio2FaceHostClient.JsonContent(body)
        };
        Sign(request, body);
        return await CreationsAsync(request, account, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlySet<string>> SendCreationChunkAsync(Guid? account, string sha256, ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        if (!CreationLibrary.IsSha256(sha256)) throw new ArgumentException("Invalid piece SHA-256.", nameof(sha256));
        // Base64 needs no escaping; writing it as is keeps the body within the gateway's limit.
        var body = System.Text.Encoding.ASCII.GetBytes("{\"data_base64\":\"" + Convert.ToBase64String(data.Span) + "\"}");
        using var request = new HttpRequestMessage(HttpMethod.Post, pairing.Origin + CreationsRoute(account) + "/chunks/" + sha256)
        {
            Content = Audio2FaceHostClient.JsonContent(body)
        };
        Sign(request, body);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        using var response = await Audio2FaceHostClient.Send(http, request, timeout.Token).ConfigureAwait(false);
        using var document = await Audio2FaceHostClient.ReadJson(response, MaximumCreationsBytes, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw Audio2FaceHostClient.Remote(document.RootElement);
        try { RequireAccount(document.RootElement, account); }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new Audio2FaceHostException("response.invalid", "The host answered for another account.");
        }
        return Present(document.RootElement, "present");
    }

    private async Task<byte[]?> ReadCreationChunkAsync(Guid? account, string sha256, CancellationToken cancellationToken)
    {
        if (!CreationLibrary.IsSha256(sha256)) throw new ArgumentException("Invalid piece SHA-256.", nameof(sha256));
        using var request = new HttpRequestMessage(HttpMethod.Get, pairing.Origin + CreationsRoute(account) + "/chunks/" + sha256);
        Sign(request, []);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        using var response = await Audio2FaceHostClient.Send(http, request, timeout.Token).ConfigureAwait(false);
        using var document = await Audio2FaceHostClient.ReadJson(response, MaximumCreationChunkBytes, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            var failure = Audio2FaceHostClient.Remote(document.RootElement);
            if (failure.Code == "chunk.missing") return null;
            throw failure;
        }
        try
        {
            var root = document.RootElement;
            if (root.GetProperty("host_id").GetString() != pairing.HostId)
                throw new Audio2FaceHostException("response.invalid", "The host identity changed; pair again.");
            RequireAccount(root, account);
            var data = Convert.FromBase64String(root.GetProperty("data_base64").GetString() ?? "");
            if (root.GetProperty("chunk_sha256").GetString() != sha256 || data.Length > CreationLibrary.ChunkBytes ||
                Convert.ToHexStringLower(SHA256.HashData(data)) != sha256)
                throw new Audio2FaceHostException("response.invalid", "The host's copy of a creation piece was invalid.");
            return data;
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new Audio2FaceHostException("response.invalid", "The host's copy of a creation piece was invalid.");
        }
    }

    private async Task<HostCreations> CreationsAsync(HttpRequestMessage request, Guid? account, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await Audio2FaceHostClient.Send(http, request, timeout.Token).ConfigureAwait(false);
        using var document = await Audio2FaceHostClient.ReadJson(response, MaximumCreationsBytes, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw Audio2FaceHostClient.Remote(document.RootElement);
        try
        {
            var root = document.RootElement;
            if (root.GetProperty("host_id").GetString() != pairing.HostId)
                throw new Audio2FaceHostException("response.invalid", "The host identity changed; pair again.");
            RequireAccount(root, account);
            return new(CreationLibrary.Parse(JsonSerializer.SerializeToUtf8Bytes(root.GetProperty("library"))), Present(root, "present"));
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or ContractException or FormatException)
        {
            throw new Audio2FaceHostException("response.invalid", "The host's copy of Martlet's creations was invalid.");
        }
    }

    // An answer on an account's route names that account; the old single list's answers name none.
    private static void RequireAccount(JsonElement root, Guid? account)
    {
        if (account is { } id && root.GetProperty("account").GetString() != id.ToString("N"))
            throw new FormatException("The host answered for another account.");
    }
}
