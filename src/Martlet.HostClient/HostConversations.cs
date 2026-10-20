using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Martlet.Core.Contracts;
using Martlet.Core.Sync;

namespace Martlet.HostClient;

/// <summary>The host's copies of an account's record of conversations (docs/MEMORY.md, "One record on every computer"): one
/// <see cref="SharedConversations"/> per UTC month at /conversations/accounts/&lt;32 hex&gt;/&lt;yyyy-MM&gt;. Hosts older than
/// it refuse with <c>request.invalid</c>; a device where the account isn't signed in gets
/// <c>conversations.account_denied</c>.</summary>
public sealed partial class HostConnection
{
    private const string ConversationAccountsPath = "/martlet/v1/conversations/accounts/";
    private const int MaximumConversationMonthsBytes = 1_048_576;
    private const int MaximumConversationsResponseBytes = SharedConversations.MaximumBytes + 16_384;

    /// <summary>Each month the host keeps of <paramref name="account"/>'s record, with the digest of its copy
    /// (<see cref="SharedConversations.Digest"/>), to read only the months that changed.</summary>
    public async Task<IReadOnlyDictionary<string, string>> ReadConversationMonthsAsync(Guid account, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, pairing.Origin + ConversationsRoute(account));
        Sign(request, []);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var response = await HostTransport.Send(http, request, timeout.Token).ConfigureAwait(false);
        using var document = await HostTransport.ReadJson(response, MaximumConversationMonthsBytes, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw HostTransport.Remote(document.RootElement);
        try
        {
            var root = document.RootElement;
            if (root.GetProperty("host_id").GetString() != pairing.HostId)
                throw new HostConnectionException("response.invalid", "The host identity changed; pair again.");
            if (root.GetProperty("account").GetString() != account.ToString("N")) throw new FormatException();
            var months = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var month in root.GetProperty("months").EnumerateObject())
            {
                var digest = month.Value.GetString();
                if (!SharedConversations.IsMonth(month.Name) || !IsConversationDigest(digest) || !months.TryAdd(month.Name, digest!))
                    throw new FormatException();
            }
            return months;
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new HostConnectionException("response.invalid", "The host's list of conversation months was invalid.");
        }
    }

    /// <summary>Reads the host's copy of <paramref name="month"/> of <paramref name="account"/>'s record; empty when the host
    /// keeps none.</summary>
    public async Task<SharedConversations> ReadConversationMonthAsync(Guid account, string month, CancellationToken cancellationToken = default)
    {
        RequireConversationMonth(month);
        using var request = new HttpRequestMessage(HttpMethod.Get, pairing.Origin + ConversationsRoute(account) + "/" + month);
        Sign(request, []);
        return await ConversationMonthAsync(request, account, month, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Merges <paramref name="copy"/> into the host's copy of its month of <paramref name="account"/>'s record and
    /// returns the merged copy, which may include changes another computer made. A host that keeps as many months as it allows
    /// refuses a new one with <c>conversations.full</c>.</summary>
    public async Task<SharedConversations> MergeConversationMonthAsync(Guid account, SharedConversations copy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(copy);
        var body = copy.Write();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, pairing.Origin + ConversationsRoute(account) + "/" + copy.Month)
            {
                Content = HostTransport.JsonContent(body)
            };
            Sign(request, body);
            return await ConversationMonthAsync(request, account, copy.Month, cancellationToken).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(body); }
    }

    private static string ConversationsRoute(Guid account) => ConversationAccountsPath + account.ToString("N");

    private static void RequireConversationMonth(string month)
    {
        if (!SharedConversations.IsMonth(month)) throw new ArgumentException("Not a month of the record (yyyy-MM).", nameof(month));
    }

    private static bool IsConversationDigest(string? digest) =>
        digest is { Length: 64 } && digest.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private async Task<SharedConversations> ConversationMonthAsync(HttpRequestMessage request, Guid account, string month,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var response = await HostTransport.Send(http, request, timeout.Token).ConfigureAwait(false);
        using var document = await HostTransport.ReadJson(response, MaximumConversationsResponseBytes, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw HostTransport.Remote(document.RootElement);
        byte[]? bytes = null;
        try
        {
            var root = document.RootElement;
            if (root.GetProperty("host_id").GetString() != pairing.HostId)
                throw new HostConnectionException("response.invalid", "The host identity changed; pair again.");
            if (root.GetProperty("account").GetString() != account.ToString("N") || root.GetProperty("month").GetString() != month ||
                !IsConversationDigest(root.GetProperty("digest").GetString()))
                throw new FormatException();
            bytes = JsonSerializer.SerializeToUtf8Bytes(root.GetProperty("record"));
            var copy = SharedConversations.Parse(bytes);
            if (copy.Month != month) throw new FormatException();
            return copy;
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException or ContractException)
        {
            throw new HostConnectionException("response.invalid", "The host's copy of the record of conversations was invalid.");
        }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }
}
