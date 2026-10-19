using System.Security.Cryptography;
using Martlet.Core.Contracts;
using Martlet.Core.Sync;
using Microsoft.AspNetCore.Http;

namespace Martlet.Gateway;

/// <summary>Where a gateway keeps the record of conversations between restarts (conversations-&lt;32 hex&gt;-&lt;yyyy-MM&gt;.json
/// beside host.json on Linux hosts): one <see cref="SharedConversations"/> document per account and UTC month, keyed
/// <c>&lt;32 lowercase hex account&gt;-&lt;yyyy-MM&gt;</c> (<see cref="GatewayConversationRecords.Key"/>). The documents hold
/// what every account said, so they must be private to the gateway owner. <see cref="Load"/> returns null when there is no
/// copy; every call may throw on storage failure.</summary>
public interface IGatewayConversationStorage
{
    /// <summary>The keys of the documents saved here.</summary>
    IReadOnlyCollection<string> List();
    byte[]? Load(string key);
    void Save(string key, byte[] bytes);
}

/// <summary>This host's copies of each account's record of conversations (docs/MEMORY.md, "One record on every computer"): one
/// <see cref="SharedConversations"/> per account and month. Paired desktops where the account is signed in read the months they
/// lack and merge their changes into them; the host never looks inside an exchange. A month exists once a merge put an exchange
/// in it; a host keeps at most <see cref="MaximumDocuments"/>. A copy that cannot be saved is still served from memory, and
/// desktops push it again.</summary>
internal sealed class GatewayConversationRecords
{
    internal const int MaximumDocuments = 8_192;
    private const int KeyLength = 32 + 1 + 7;
    private readonly object gate = new();
    private readonly Dictionary<string, Document> documents = new(StringComparer.Ordinal);
    private IGatewayConversationStorage? storage;
    private GatewayStoreHealth health = new();

    private sealed class Document(SharedConversations copy, string digest)
    {
        internal readonly object Gate = new();
        internal SharedConversations Copy = copy;
        internal string Digest = digest;
    }

    /// <summary>The storage key of <paramref name="account"/>'s <paramref name="month"/>.</summary>
    internal static string Key(Guid account, string month) => account.ToString("N") + "-" + month;

    /// <summary>Whether <paramref name="key"/> is <c>&lt;32 lowercase hex&gt;-&lt;yyyy-MM&gt;</c>.</summary>
    internal static bool IsKey(string? key) =>
        key is { Length: KeyLength } && key[32] == '-' && MemorySpaceId.IsValid(MemorySpaceId.AccountPrefix + key[..32]) &&
        SharedConversations.IsMonth(key[33..]);

    /// <summary>The keys of the documents this host keeps, in order.</summary>
    internal IReadOnlyList<string> Keys
    {
        get { lock (gate) return [.. documents.Keys.Order(StringComparer.Ordinal)]; }
    }

    internal void Attach(IGatewayConversationStorage value, GatewayStoreHealth? storeHealth = null)
    {
        if (storeHealth is not null) health = storeHealth;
        ArgumentNullException.ThrowIfNull(value);
        string[] saved;
        saved = [];
        // Months that cannot be listed come back from the next desktops that sync them.
        health.Try("conversations", "load", () => saved = [.. value.List().Where(IsKey).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
        lock (gate) storage = value;
        foreach (var key in saved)
        {
            SharedConversations copy;
            byte[]? bytes = null;
            try
            {
                bytes = value.Load(key);
                if (bytes is null) continue;
                copy = SharedConversations.Parse(bytes);
            }
            // An unreadable or malformed copy is replaced by the next desktop that syncs the month.
            catch (Exception error)
            {
                health.Failed("conversations", "load", error);
                continue;
            }
            finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
            if (copy.Month != key[33..] || copy.Exchanges.Count == 0) continue;
            var digest = Digest(copy, out _, keep: false);
            Document? document;
            lock (gate)
            {
                if (!documents.TryGetValue(key, out document))
                {
                    if (documents.Count >= MaximumDocuments) break;
                    documents[key] = new(copy, digest);
                    continue;
                }
            }
            lock (document.Gate) Replace(key, document, SharedConversations.Merge(document.Copy, copy), save: false);
        }
    }

    /// <summary>Each month this host keeps for <paramref name="account"/>, with the digest of its copy.</summary>
    internal IReadOnlyDictionary<string, string> Months(Guid account)
    {
        var prefix = account.ToString("N") + "-";
        KeyValuePair<string, Document>[] kept;
        lock (gate) kept = [.. documents.Where(d => d.Key.StartsWith(prefix, StringComparison.Ordinal))];
        var months = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, document) in kept)
            lock (document.Gate)
                if (document.Copy.Exchanges.Count > 0) months[key[33..]] = document.Digest;
        return months;
    }

    /// <summary>This host's copy of <paramref name="account"/>'s <paramref name="month"/>; empty when it keeps none.</summary>
    internal SharedConversations Read(Guid account, string month) =>
        Find(account, month) is { } document ? Locked(document, d => d.Copy) : SharedConversations.Empty(month);

    internal string Digest(Guid account, string month) =>
        Find(account, month) is { } document ? Locked(document, d => d.Digest) : SharedConversations.Empty(month).Digest();

    /// <summary>Merges <paramref name="incoming"/> into <paramref name="account"/>'s copy of its month and returns the merged
    /// copy. A merge that brings no exchange makes no month; a new month past <see cref="MaximumDocuments"/> is
    /// <c>conversations.full</c>.</summary>
    internal SharedConversations Merge(Guid account, SharedConversations incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        var key = Key(account, incoming.Month);
        Document? document;
        lock (gate)
        {
            if (!documents.TryGetValue(key, out document))
            {
                if (incoming.Exchanges.Count == 0) return SharedConversations.Empty(incoming.Month);
                GatewayRules.Require(documents.Count < MaximumDocuments, "conversations.full");
                var empty = SharedConversations.Empty(incoming.Month);
                document = new(empty, empty.Digest());
                documents[key] = document;
            }
        }
        lock (document.Gate)
        {
            Replace(key, document, SharedConversations.Merge(document.Copy, incoming), save: true);
            return document.Copy;
        }
    }

    private Document? Find(Guid account, string month)
    {
        lock (gate) return documents.GetValueOrDefault(Key(account, month));
    }

    private static T Locked<T>(Document document, Func<Document, T> read)
    {
        lock (document.Gate) return read(document);
    }

    // Writes the copy once: its bytes give the digest (SharedConversations.Digest) and, when saved, the file.
    private static string Digest(SharedConversations copy, out byte[]? bytes, bool keep)
    {
        var written = copy.Write();
        var digest = Convert.ToHexStringLower(SHA256.HashData(written));
        if (keep) bytes = written;
        else
        {
            bytes = null;
            CryptographicOperations.ZeroMemory(written);
        }
        return digest;
    }

    private void Replace(string key, Document document, SharedConversations next, bool save)
    {
        var digest = Digest(next, out var bytes, keep: save);
        try
        {
            if (digest == document.Digest) return;
            document.Copy = next;
            document.Digest = digest;
            IGatewayConversationStorage? target;
            lock (gate) target = storage;
            if (!save || target is null || bytes is null) return;
            health.Try("conversations", "save", () => { target.Save(key, bytes); });
        }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }
}

internal sealed partial class GatewayHttpApplication
{
    internal const string ConversationsPath = "/martlet/v1/conversations";
    internal const string ConversationAccountsPath = ConversationsPath + "/accounts/";
    private const int MaximumConversationsResponseBytes = SharedConversations.MaximumBytes + 16_384;
    /// <summary>The months index: at most <see cref="GatewayConversationRecords.MaximumDocuments"/> months of about 80 bytes.</summary>
    private const int MaximumConversationMonthsBytes = 1_048_576;
    private static System.Text.Json.JsonSerializerOptions? conversationsJson;

    internal GatewayConversationRecords Conversations { get; } = new();

    private static bool IsConversationsTarget(string rawTarget) =>
        rawTarget == ConversationsPath || rawTarget.StartsWith(ConversationsPath + "/", StringComparison.Ordinal);

    /// <summary>GET /conversations/accounts/&lt;32 hex&gt; lists the months this host keeps of the account's record of
    /// conversations, each with its digest. GET /conversations/accounts/&lt;32 hex&gt;/&lt;yyyy-MM&gt; returns the host's copy of
    /// one month (empty when it keeps none), and POST merges a desktop's copy of that month into it and returns the merged copy.
    /// Only paired member devices that <see cref="GatewayMemorySpaces.Access"/> lets use the account's memory space may use them,
    /// over their signed, pinned connection (never friends or API keys): reading needs read access, a POST read and write
    /// access. Anything else is <c>request.invalid</c>.</summary>
    private async ValueTask InvokeConversationsAsync(HttpContext context, string rawTarget)
    {
        GatewayRules.Require(rawTarget.StartsWith(ConversationAccountsPath, StringComparison.Ordinal), "request.invalid");
        var tail = rawTarget[ConversationAccountsPath.Length..];
        var hex = tail.Length >= 32 ? tail[..32] : "";
        GatewayRules.Require(MemorySpaceId.IsValid(MemorySpaceId.AccountPrefix + hex), "request.invalid");
        var account = Guid.ParseExact(hex, "N");
        var rest = tail[32..];
        if (rest.Length == 0)
        {
            GatewayRules.Require(context.Request.Method == HttpMethods.Get, "request.invalid");
            EnsureEmptyRequest(context.Request);
            AdmitConversations(authenticator.Authenticate(context.Request), account, GatewayMemorySpaceUse.Read);
            await WriteJsonAsync(context, StatusCodes.Status200OK, new ConversationMonthsDocument
            {
                ProtocolVersion = GatewayProtocolVersion.Current,
                HostId = identity.HostId,
                Account = hex,
                Months = Conversations.Months(account)
            }, MaximumConversationMonthsBytes).ConfigureAwait(false);
            return;
        }
        GatewayRules.Require(rest[0] == '/' && SharedConversations.IsMonth(rest[1..]), "request.invalid");
        var month = rest[1..];
        SharedConversations result;
        if (context.Request.Method == HttpMethods.Get)
        {
            EnsureEmptyRequest(context.Request);
            AdmitConversations(authenticator.Authenticate(context.Request), account, GatewayMemorySpaceUse.Read);
            result = Conversations.Read(account, month);
        }
        else if (context.Request.Method == HttpMethods.Post)
        {
            var bytes = await ReadInferenceBodyAsync(context.Request, SharedConversations.MaximumBytes, context.RequestAborted).ConfigureAwait(false);
            try
            {
                AdmitConversations(authenticator.Authenticate(context.Request, crypto.Sha256(bytes)), account,
                    GatewayMemorySpaceUse.Read, GatewayMemorySpaceUse.Write);
                SharedConversations incoming;
                try { incoming = SharedConversations.Parse(bytes); }
                catch (ContractException) { throw new GatewayProtocolException("request.invalid"); }
                GatewayRules.Require(incoming.Month == month, "request.invalid");
                result = Conversations.Merge(account, incoming);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        else throw new GatewayProtocolException("request.invalid");
        await WriteConversationsAsync(context, hex, result).ConfigureAwait(false);
    }

    // An account's record of conversations is for the devices that may use that account's memory space (docs/ACCOUNTS.md).
    private void AdmitConversations(GatewayPrincipal principal, Guid account, params GatewayMemorySpaceUse[] uses)
    {
        foreach (var use in uses)
            GatewayRules.Require(MemorySpaces.Access(principal, MemorySpaceId.Account(account), use), "conversations.account_denied");
    }

    private async ValueTask WriteConversationsAsync(HttpContext context, string account, SharedConversations result)
    {
        var document = result.Write();
        try
        {
            using var parsed = System.Text.Json.JsonDocument.Parse(document, new System.Text.Json.JsonDocumentOptions { MaxDepth = 32 });
            // What people said is written as it is, so the response stays within its limit.
            conversationsJson ??= new(Json) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, MaxDepth = 40 };
            await WriteJsonAsync(context, StatusCodes.Status200OK, new ConversationRecordDocument
            {
                ProtocolVersion = GatewayProtocolVersion.Current,
                HostId = identity.HostId,
                Account = account,
                Month = result.Month,
                Digest = Convert.ToHexStringLower(SHA256.HashData(document)),
                Record = parsed.RootElement
            }, MaximumConversationsResponseBytes, conversationsJson).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(document); }
    }

    private sealed record ConversationMonthsDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public required string Account { get; init; }
        public required IReadOnlyDictionary<string, string> Months { get; init; }
    }

    private sealed record ConversationRecordDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public required string Account { get; init; }
        public required string Month { get; init; }
        public required string Digest { get; init; }
        public required System.Text.Json.JsonElement Record { get; init; }
    }
}
