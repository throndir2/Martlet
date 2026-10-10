using System.Net;
using System.Text.Json;
using Martlet.Core.Accounts;
using Martlet.Core.Contracts;

namespace Martlet.Avatar.Audio2Face.Remote;

/// <summary>The host's copy of the household's account directory (docs/ACCOUNTS.md). Hosts older than it refuse with
/// <c>request.invalid</c>.</summary>
public sealed partial class Audio2FaceHostConnection
{
    private const string AccountsPath = "/martlet/v1/accounts";
    private const string AccountsDigestPath = AccountsPath + "/digest";
    private const int MaximumAccountsResponseBytes = AccountDirectory.MaximumBytes + 4_096;

    /// <summary>The digest of the host's copy (<see cref="AccountDirectory.Digest"/>), to read the copy only when it changed.</summary>
    public async Task<string> ReadAccountsDigestAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, pairing.Origin + AccountsDigestPath);
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
            throw new Audio2FaceHostException("response.invalid", "The host's account directory digest was invalid.");
        }
    }

    /// <summary>Reads the host's copy of the account directory. Check its entries against this PC's roster
    /// (<see cref="AccountDirectory.Accept"/>) before taking them.</summary>
    public async Task<AccountDirectory> ReadAccountsAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, pairing.Origin + AccountsPath);
        Sign(request, []);
        return (await AccountsAsync(request, cancellationToken).ConfigureAwait(false)).Directory;
    }

    /// <summary>Merges <paramref name="directory"/> into the host's copy and returns the merged copy, which may include newer
    /// changes another computer made, and how many of the posted entries the host refused (not signed by a member desktop of
    /// its network).</summary>
    public async Task<AccountDirectoryMerge> MergeAccountsAsync(AccountDirectory directory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var body = directory.Write();
        using var request = new HttpRequestMessage(HttpMethod.Post, pairing.Origin + AccountsPath)
        {
            Content = Audio2FaceHostClient.JsonContent(body)
        };
        Sign(request, body);
        return await AccountsAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AccountDirectoryMerge> AccountsAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await Audio2FaceHostClient.Send(http, request, timeout.Token).ConfigureAwait(false);
        using var document = await Audio2FaceHostClient.ReadJson(response, MaximumAccountsResponseBytes, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw Audio2FaceHostClient.Remote(document.RootElement);
        try
        {
            var root = document.RootElement;
            if (root.GetProperty("host_id").GetString() != pairing.HostId)
                throw new Audio2FaceHostException("response.invalid", "The host identity changed; pair again.");
            var rejected = root.TryGetProperty("rejected", out var count) ? count.GetInt32() : 0;
            if (rejected < 0) throw new FormatException();
            return new(AccountDirectory.Parse(JsonSerializer.SerializeToUtf8Bytes(root.GetProperty("accounts"))), rejected);
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException or ContractException)
        {
            throw new Audio2FaceHostException("response.invalid", "The host's copy of the account directory was invalid.");
        }
    }
}
