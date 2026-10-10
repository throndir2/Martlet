using System.Security.Cryptography;
using Martlet.Core.Creations;
using Martlet.Core.Sync;
using Microsoft.AspNetCore.Http;

namespace Martlet.Gateway;

/// <summary>Where a gateway keeps its copy of Martlet's creations and their pieces between restarts (creations.json and
/// creation-chunk-&lt;sha256&gt;.bin beside host.json on Linux hosts). Loads return null when there is nothing; any call may
/// throw on storage failure.</summary>
public interface IGatewayCreationStorage
{
    byte[]? LoadLibrary();
    void SaveLibrary(byte[] bytes);
    bool HasChunk(string sha256);
    byte[]? LoadChunk(string sha256);
    void SaveChunk(string sha256, byte[] bytes);
    void RemoveChunk(string sha256);
}

/// <summary>Where a gateway keeps each account's creation list between restarts (creations-account-&lt;32 hex&gt;.json beside
/// host.json on Linux hosts; docs/ACCOUNTS.md). The pieces stay in <see cref="IGatewayCreationStorage"/>: one pool for every
/// account. The lists hold every account's creations, so they must be private to the gateway owner. <see cref="Load"/> returns
/// null when the account has no copy; every call may throw on storage failure.</summary>
public interface IGatewayAccountCreationStorage
{
    /// <summary>The accounts whose lists are saved here.</summary>
    IReadOnlyCollection<Guid> List();
    byte[]? Load(Guid account);
    void Save(Guid account, byte[] bytes);
}

/// <summary>This host's copies of everything Martlet made (docs/CREATIONS.md) and the pieces of their assets: one list for each
/// account (docs/ACCOUNTS.md) and the old single list that desktops on an older Martlet use, with one pool of pieces for all of
/// them. A host performs nothing itself: it keeps them so every paired desktop can copy them, including desktops that were off
/// when a creation was made, since desktops reach each other only through hosts. Each list sees only its own pieces. Pieces live
/// in storage, not memory; without storage (or when a piece can't be saved) up to <see cref="MaximumMemoryBytes"/> are kept in
/// memory and desktops send any piece a restarted host lacks again. A piece is deleted when no list's live creation uses it. An
/// account's list exists once a merge put something in it; a host keeps at most <see cref="MaximumAccounts"/>.</summary>
internal sealed class GatewayCreationStore
{
    internal const long MaximumMemoryBytes = 256L * 1024 * 1024;
    internal const int MaximumAccounts = 64;
    private static readonly string EmptyDigest = CreationLibrary.Empty.Digest();
    private readonly object gate = new();
    private readonly HashSet<string> stored = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> memory = new(StringComparer.Ordinal);
    private readonly Library legacy = new();
    private readonly Dictionary<Guid, Library> accounts = [];
    private long memoryBytes;
    private Dictionary<string, int> chunks = new(StringComparer.Ordinal);
    private IGatewayCreationStorage? storage;
    private IGatewayAccountCreationStorage? accountStorage;

    private sealed class Library
    {
        internal CreationLibrary Value = CreationLibrary.Empty;
        internal string Digest = EmptyDigest;
        internal IReadOnlyDictionary<string, int> Chunks = new Dictionary<string, int>();
    }

    /// <summary>The accounts this host keeps a list for, in the order of their 32 hex digits.</summary>
    internal IReadOnlyList<Guid> Accounts
    {
        get { lock (gate) return [.. accounts.Keys.OrderBy(a => a.ToString("N"), StringComparer.Ordinal)]; }
    }

    /// <summary>The list of <paramref name="account"/> (null: the old single list); empty when the host keeps none.</summary>
    internal CreationLibrary Current(Guid? account = null) { lock (gate) return Find(account)?.Value ?? CreationLibrary.Empty; }

    internal string Digest(Guid? account = null) { lock (gate) return Find(account)?.Digest ?? EmptyDigest; }

    /// <summary>The pieces of <paramref name="account"/>'s live creations this host holds, by SHA-256, sorted.</summary>
    internal IReadOnlyList<string> Present(Guid? account = null)
    {
        lock (gate)
            return Find(account) is { } library
                ? [.. library.Chunks.Keys.Where(k => stored.Contains(k) || memory.ContainsKey(k)).Order(StringComparer.Ordinal)]
                : [];
    }

    /// <summary>Identifies <see cref="Present"/> (<see cref="CreationLibrary.PresentDigest"/>).</summary>
    internal static string PresentDigest(IEnumerable<string> present) => CreationLibrary.PresentDigest(present);

    internal void Attach(IGatewayCreationStorage value)
    {
        ArgumentNullException.ThrowIfNull(value);
        CreationLibrary? saved = null;
        try { if (value.LoadLibrary() is { } bytes) saved = CreationLibrary.Parse(bytes); }
        // An unreadable or malformed copy is replaced by the next desktop that syncs.
        catch (Exception) { }
        lock (gate)
        {
            storage = value;
            if (saved is not null) Replace(null, legacy, CreationLibrary.Merge(legacy.Value, saved), save: false);
            NoteStored();
        }
    }

    /// <summary>Loads the account lists saved in <paramref name="value"/> and saves each later change there. Attach it before
    /// <see cref="Attach"/>, so the pieces only an account uses are known when the pool is checked.</summary>
    internal void AttachAccounts(IGatewayAccountCreationStorage value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var saved = new List<(Guid Account, CreationLibrary Library)>();
        try
        {
            foreach (var account in value.List().Distinct().OrderBy(a => a.ToString("N"), StringComparer.Ordinal))
            {
                // A list that cannot be read comes back from the next desktop of that account that syncs.
                try { if (value.Load(account) is { } bytes) saved.Add((account, CreationLibrary.Parse(bytes))); }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
        lock (gate)
        {
            accountStorage = value;
            foreach (var (account, library) in saved)
            {
                if (!accounts.TryGetValue(account, out var kept))
                {
                    if (accounts.Count >= MaximumAccounts) continue;
                    accounts[account] = kept = new Library();
                }
                Replace(account, kept, CreationLibrary.Merge(kept.Value, library), save: false);
            }
            NoteStored();
        }
    }

    /// <summary>Merges <paramref name="incoming"/> into <paramref name="account"/>'s list (null: the old single list) and
    /// returns the merged copy. A merge that brings nothing makes no account list; a new one past <see cref="MaximumAccounts"/>
    /// is <c>creations.accounts_full</c>.</summary>
    internal CreationLibrary Merge(Guid? account, CreationLibrary incoming)
    {
        lock (gate)
        {
            var library = Find(account);
            if (library is null)
            {
                if (incoming.Creations.Count == 0) return CreationLibrary.Empty;
                GatewayRules.Require(accounts.Count < MaximumAccounts, "creations.accounts_full");
                accounts[account!.Value] = library = new Library();
            }
            Replace(account, library, CreationLibrary.Merge(library.Value, incoming), save: true);
            return library.Value;
        }
    }

    /// <summary>Keeps a piece of a live creation of <paramref name="account"/>'s list. False when no live creation there has a
    /// piece with that SHA-256 and length, or this host has nowhere to keep it.</summary>
    internal bool Store(Guid? account, string sha256, byte[] bytes)
    {
        lock (gate)
        {
            if (Find(account) is not { } library || !library.Chunks.TryGetValue(sha256, out var length) || bytes.Length != length ||
                Convert.ToHexStringLower(SHA256.HashData(bytes)) != sha256)
                return false;
            if (stored.Contains(sha256) || memory.ContainsKey(sha256)) return true;
            if (storage is not null)
            {
                try
                {
                    storage.SaveChunk(sha256, bytes);
                    stored.Add(sha256);
                    return true;
                }
                catch (Exception) { }
            }
            if (memoryBytes + bytes.Length > MaximumMemoryBytes) return false;
            memory[sha256] = (byte[])bytes.Clone();
            memoryBytes += bytes.Length;
            return true;
        }
    }

    /// <summary>A copy of the piece with <paramref name="sha256"/> of a live creation of <paramref name="account"/>'s list, or
    /// null when this host does not hold it or that list has no such piece (a damaged saved piece is forgotten, so a desktop
    /// sends it again).</summary>
    internal byte[]? Chunk(Guid? account, string sha256)
    {
        lock (gate)
        {
            if (Find(account) is not { } library || !library.Chunks.TryGetValue(sha256, out var length)) return null;
            if (memory.TryGetValue(sha256, out var kept)) return (byte[])kept.Clone();
            if (!stored.Contains(sha256) || storage is null) return null;
            byte[]? bytes = null;
            try { bytes = storage.LoadChunk(sha256); }
            catch (Exception) { }
            if (bytes is not null && bytes.Length == length && Convert.ToHexStringLower(SHA256.HashData(bytes)) == sha256)
                return bytes;
            stored.Remove(sha256);
            try { storage.RemoveChunk(sha256); }
            catch (Exception) { }
            return null;
        }
    }

    private Library? Find(Guid? account) => account is { } id ? accounts.GetValueOrDefault(id) : legacy;

    // Notes which live pieces the storage already holds.
    private void NoteStored()
    {
        if (storage is null) return;
        foreach (var sha256 in chunks.Keys)
        {
            if (stored.Contains(sha256)) continue;
            try { if (storage.HasChunk(sha256)) stored.Add(sha256); }
            catch (Exception) { }
        }
    }

    private void Replace(Guid? account, Library library, CreationLibrary next, bool save)
    {
        var nextDigest = next.Digest();
        if (nextDigest == library.Digest) return;
        library.Value = next;
        library.Digest = nextDigest;
        library.Chunks = next.LiveChunks();
        var live = new Dictionary<string, int>(legacy.Chunks, StringComparer.Ordinal);
        foreach (var other in accounts.Values)
        foreach (var (sha256, length) in other.Chunks)
            live.TryAdd(sha256, length);
        chunks = live;
        foreach (var sha256 in memory.Keys.Where(k => !chunks.ContainsKey(k)).ToArray())
        {
            memoryBytes -= memory[sha256].Length;
            memory.Remove(sha256);
        }
        foreach (var sha256 in stored.Where(k => !chunks.ContainsKey(k)).ToArray())
        {
            stored.Remove(sha256);
            try { storage?.RemoveChunk(sha256); }
            catch (Exception) { }
        }
        if (!save) return;
        try
        {
            if (account is { } id) accountStorage?.Save(id, next.Write());
            else storage?.SaveLibrary(next.Write());
        }
        catch (Exception) { }
    }
}

internal sealed partial class GatewayHttpApplication
{
    internal const string CreationsPath = "/martlet/v1/creations";
    internal const string CreationsDigestPath = CreationsPath + "/digest";
    internal const string CreationChunkPath = CreationsPath + "/chunks/";
    internal const string CreationAccountsPath = CreationsPath + "/accounts/";
    private const int MaximumCreationsResponseBytes = CreationLibrary.MaximumBytes + 2 * 1024 * 1024;
    private const int MaximumCreationChunkBytes = (CreationLibrary.ChunkBytes + 2) / 3 * 4 + 4_096;

    internal GatewayCreationStore Creations { get; } = new();

    private static bool IsCreationsTarget(string rawTarget) =>
        rawTarget is CreationsPath or CreationsDigestPath || rawTarget.StartsWith(CreationChunkPath, StringComparison.Ordinal) ||
        rawTarget.StartsWith(CreationAccountsPath, StringComparison.Ordinal);

    /// <summary>GET /creations returns this host's copy of Martlet's creations and the pieces it holds; POST merges a desktop's
    /// copy into it; GET /creations/digest returns only the digests of both, so desktops read the copy when it changed.
    /// GET /creations/chunks/&lt;sha256&gt; returns a piece; POST sends one of a live creation. Any paired device may use these
    /// over its signed, pinned connection; friends and API keys may not. /creations/accounts/&lt;32 hex&gt; (with /digest and
    /// /chunks/&lt;sha256&gt;) are the same for one account's own list (docs/ACCOUNTS.md), for paired member devices that
    /// <see cref="GatewayMemorySpaces.Access"/> lets use that account's memory space: reading needs read access, a POST read and
    /// write access. The answers on those routes name the <c>account</c>.</summary>
    private async ValueTask InvokeCreationsAsync(HttpContext context, string rawTarget)
    {
        Guid? account = null;
        string rest;
        if (rawTarget.StartsWith(CreationAccountsPath, StringComparison.Ordinal))
        {
            var tail = rawTarget[CreationAccountsPath.Length..];
            var hex = tail.Length >= 32 ? tail[..32] : "";
            GatewayRules.Require(MemorySpaceId.IsValid(MemorySpaceId.AccountPrefix + hex), "request.invalid");
            account = Guid.ParseExact(hex, "N");
            rest = tail[32..];
        }
        else rest = rawTarget[CreationsPath.Length..];
        var named = account?.ToString("N");

        if (rest == "/digest")
        {
            GatewayRules.Require(context.Request.Method == HttpMethods.Get, "request.invalid");
            EnsureEmptyRequest(context.Request);
            AdmitCreations(authenticator.Authenticate(context.Request), account, GatewayMemorySpaceUse.Read);
            var present = Creations.Present(account);
            await WriteJsonAsync(context, StatusCodes.Status200OK, new CreationsDigestDocument
            {
                ProtocolVersion = GatewayProtocolVersion.Current,
                HostId = identity.HostId,
                Account = named,
                Digest = Creations.Digest(account),
                PresentDigest = GatewayCreationStore.PresentDigest(present),
                PresentCount = present.Count
            }).ConfigureAwait(false);
            return;
        }
        if (rest.Length == 0)
        {
            CreationLibrary result;
            if (context.Request.Method == HttpMethods.Get)
            {
                EnsureEmptyRequest(context.Request);
                AdmitCreations(authenticator.Authenticate(context.Request), account, GatewayMemorySpaceUse.Read);
                result = Creations.Current(account);
            }
            else if (context.Request.Method == HttpMethods.Post)
            {
                var bytes = await ReadInferenceBodyAsync(context.Request, CreationLibrary.MaximumBytes, context.RequestAborted).ConfigureAwait(false);
                AdmitCreations(authenticator.Authenticate(context.Request, crypto.Sha256(bytes)), account,
                    GatewayMemorySpaceUse.Read, GatewayMemorySpaceUse.Write);
                CreationLibrary incoming;
                try { incoming = CreationLibrary.Parse(bytes); }
                catch (Martlet.Core.Contracts.ContractException) { throw new GatewayProtocolException("request.invalid"); }
                result = Creations.Merge(account, incoming);
            }
            else throw new GatewayProtocolException("request.invalid");
            var document = result.Write();
            using var parsed = System.Text.Json.JsonDocument.Parse(document, new System.Text.Json.JsonDocumentOptions { MaxDepth = 32 });
            await WriteJsonAsync(context, StatusCodes.Status200OK, new CreationsDocument
            {
                ProtocolVersion = GatewayProtocolVersion.Current,
                HostId = identity.HostId,
                Account = named,
                Library = parsed.RootElement,
                Present = Creations.Present(account)
            }, MaximumCreationsResponseBytes, CreationsJson).ConfigureAwait(false);
            return;
        }

        GatewayRules.Require(rest.StartsWith("/chunks/", StringComparison.Ordinal), "request.invalid");
        var sha256 = rest["/chunks/".Length..];
        GatewayRules.Require(CreationLibrary.IsSha256(sha256), "request.invalid");
        if (context.Request.Method == HttpMethods.Get)
        {
            EnsureEmptyRequest(context.Request);
            AdmitCreations(authenticator.Authenticate(context.Request), account, GatewayMemorySpaceUse.Read);
            var chunk = Creations.Chunk(account, sha256) ?? throw new GatewayProtocolException("chunk.missing");
            await WriteJsonAsync(context, StatusCodes.Status200OK, new CreationChunkDocument
            {
                ProtocolVersion = GatewayProtocolVersion.Current,
                HostId = identity.HostId,
                Account = named,
                ChunkSha256 = sha256,
                DataBase64 = Convert.ToBase64String(chunk)
            }, MaximumCreationChunkBytes, UnescapedJson).ConfigureAwait(false);
            return;
        }
        GatewayRules.Require(context.Request.Method == HttpMethods.Post, "request.invalid");
        var body = await ReadInferenceBodyAsync(context.Request, MaximumCreationChunkBytes, context.RequestAborted).ConfigureAwait(false);
        AdmitCreations(authenticator.Authenticate(context.Request, crypto.Sha256(body)), account,
            GatewayMemorySpaceUse.Read, GatewayMemorySpaceUse.Write);
        byte[] data;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            var root = document.RootElement;
            GatewayRules.Require(root.ValueKind == System.Text.Json.JsonValueKind.Object &&
                root.EnumerateObject().Select(p => p.Name).SequenceEqual(["data_base64"]), "request.invalid");
            data = Convert.FromBase64String(root.GetProperty("data_base64").GetString() ?? "");
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or FormatException or InvalidOperationException)
        {
            throw new GatewayProtocolException("request.invalid");
        }
        GatewayRules.Require(Creations.Store(account, sha256, data), "request.invalid");
        await WriteJsonAsync(context, StatusCodes.Status200OK, new CreationsStoredDocument
        {
            ProtocolVersion = GatewayProtocolVersion.Current,
            HostId = identity.HostId,
            Account = named,
            Present = Creations.Present(account)
        }, MaximumCreationsResponseBytes).ConfigureAwait(false);
    }

    // An account's creations are for the devices that may use that account's memory space (docs/ACCOUNTS.md); the old single
    // list is for every paired device, as before.
    private void AdmitCreations(GatewayPrincipal principal, Guid? account, params GatewayMemorySpaceUse[] uses)
    {
        if (account is not { } id) return;
        foreach (var use in uses)
            GatewayRules.Require(MemorySpaces.Access(principal, MemorySpaceId.Account(id), use), "creations.account_denied");
    }

    // Titles and lyrics are written as they are, so the response stays within its limit.
    private static System.Text.Json.JsonSerializerOptions? creationsJson;
    private static System.Text.Json.JsonSerializerOptions CreationsJson =>
        creationsJson ??= new(Json) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, MaxDepth = 40 };

    private sealed record CreationsDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public string? Account { get; init; }
        public required System.Text.Json.JsonElement Library { get; init; }
        public required IReadOnlyList<string> Present { get; init; }
    }

    private sealed record CreationsDigestDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public string? Account { get; init; }
        public required string Digest { get; init; }
        public required string PresentDigest { get; init; }
        public required int PresentCount { get; init; }
    }

    private sealed record CreationChunkDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public string? Account { get; init; }
        public required string ChunkSha256 { get; init; }
        public required string DataBase64 { get; init; }
    }

    private sealed record CreationsStoredDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public string? Account { get; init; }
        public required IReadOnlyList<string> Present { get; init; }
    }
}
