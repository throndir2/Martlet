using System.Security.Cryptography;
using Martlet.Core.Contracts;
using Martlet.Core.Sync;
using Microsoft.AspNetCore.Http;

namespace Martlet.Gateway;

/// <summary>Where a gateway keeps its copy of everything Martlet remembers between restarts (memories.json beside host.json on
/// Linux hosts). The copy holds the owner's memories, so it must be private to the gateway owner. <see cref="Load"/> returns
/// null when there is none; either call may throw on storage failure.</summary>
public interface IGatewayMemoryStorage
{
    byte[]? Load();
    void Save(byte[] bytes);
}

/// <summary>This host's copy of what Martlet remembers (docs/MEMORY.md, "One memory on every computer"). Paired desktops read
/// it and merge their changes into it; the host itself never uses it. A copy that cannot be saved is still served from
/// memory, and desktops push it again.</summary>
internal sealed class GatewayMemoryStore
{
    private readonly object gate = new();
    private SharedMemories memories = SharedMemories.Empty;
    private string digest = SharedMemories.Empty.Digest();
    private IGatewayMemoryStorage? storage;

    internal SharedMemories Current { get { lock (gate) return memories; } }

    internal string Digest { get { lock (gate) return digest; } }

    internal void Attach(IGatewayMemoryStorage value)
    {
        ArgumentNullException.ThrowIfNull(value);
        SharedMemories? saved = null;
        try { if (value.Load() is { } bytes) saved = SharedMemories.Parse(bytes); }
        // An unreadable or malformed copy is replaced by the next desktop that syncs.
        catch (Exception) { }
        lock (gate)
        {
            storage = value;
            if (saved is not null) Replace(SharedMemories.Merge(memories, saved), save: false);
        }
    }

    internal SharedMemories Merge(SharedMemories incoming)
    {
        lock (gate)
        {
            Replace(SharedMemories.Merge(memories, incoming), save: true);
            return memories;
        }
    }

    /// <summary>Adds the live facts of <paramref name="given"/> whose IDs this copy doesn't have (not even as forgotten) and
    /// returns how many.</summary>
    internal int Give(SharedMemories given)
    {
        lock (gate)
        {
            var added = given.Live.Where(fact => memories.Find(fact.Id) is null).ToArray();
            if (added.Length == 0) return 0;
            Replace(SharedMemories.Merge(memories, new SharedMemories { SchemaVersion = SharedMemories.SchemaVersion1, Facts = added }), save: true);
            return added.Count(fact => memories.Find(fact.Id) is not null);
        }
    }

    private void Replace(SharedMemories next, bool save)
    {
        var nextDigest = next.Digest();
        if (nextDigest == digest) return;
        memories = next;
        digest = nextDigest;
        if (!save || storage is null) return;
        byte[]? bytes = null;
        try
        {
            bytes = next.Write();
            storage.Save(bytes);
        }
        catch (Exception) { }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }
}

internal sealed partial class GatewayHttpApplication
{
    internal const string MemoriesPath = "/martlet/v1/memories";
    internal const string MemoriesDigestPath = MemoriesPath + "/digest";
    private const int MaximumMemoriesResponseBytes = SharedMemories.MaximumBytes + 16_384;
    private static System.Text.Json.JsonSerializerOptions? memoriesJson;

    internal GatewayMemoryStore Memories { get; } = new();

    private static bool IsMemoriesTarget(string rawTarget) =>
        rawTarget is MemoriesPath or MemoriesDigestPath || IsMemorySpaceTarget(rawTarget);

    /// <summary>GET /memories returns this host's copy of what Martlet remembers and POST merges a desktop's copy into it and
    /// returns the merged result; GET /memories/digest returns only the copy's digest, so desktops read the copy when it
    /// changed. The copy holds the owner's memories: only paired devices may use these, over their signed, pinned connection;
    /// API keys may not. /memories/spaces/... are the memory spaces (GatewayMemorySpaces.cs).</summary>
    private async ValueTask InvokeMemoriesAsync(HttpContext context, string rawTarget)
    {
        if (IsMemorySpaceTarget(rawTarget))
        {
            await InvokeMemorySpaceAsync(context, rawTarget).ConfigureAwait(false);
            return;
        }
        if (rawTarget == MemoriesDigestPath)
        {
            GatewayRules.Require(context.Request.Method == HttpMethods.Get, "request.invalid");
            EnsureEmptyRequest(context.Request);
            AdmitOldMemories(authenticator.Authenticate(context.Request));
            await WriteJsonAsync(context, StatusCodes.Status200OK, new MemoriesDigestDocument
            {
                ProtocolVersion = GatewayProtocolVersion.Current,
                HostId = identity.HostId,
                Digest = Memories.Digest
            }).ConfigureAwait(false);
            return;
        }
        SharedMemories result;
        if (context.Request.Method == HttpMethods.Get)
        {
            EnsureEmptyRequest(context.Request);
            AdmitOldMemories(authenticator.Authenticate(context.Request));
            result = Memories.Current;
        }
        else if (context.Request.Method == HttpMethods.Post)
        {
            var bytes = await ReadInferenceBodyAsync(context.Request, SharedMemories.MaximumBytes, context.RequestAborted).ConfigureAwait(false);
            try
            {
                AdmitOldMemories(authenticator.Authenticate(context.Request, crypto.Sha256(bytes)));
                SharedMemories incoming;
                try { incoming = SharedMemories.Parse(bytes); }
                catch (ContractException) { throw new GatewayProtocolException("request.invalid"); }
                result = Memories.Merge(incoming);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        else throw new GatewayProtocolException("request.invalid");
        await WriteMemoriesAsync(context, result, space: null).ConfigureAwait(false);
    }

    /// <summary>Writes <paramref name="result"/> with its digest; <paramref name="space"/> names the memory space (null for the
    /// single memory document, which has no <c>space</c> field).</summary>
    private async ValueTask WriteMemoriesAsync(HttpContext context, SharedMemories result, string? space)
    {
        var document = result.Write();
        try
        {
            using var parsed = System.Text.Json.JsonDocument.Parse(document, new System.Text.Json.JsonDocumentOptions { MaxDepth = 32 });
            memoriesJson ??= new(Json) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, MaxDepth = 40 };
            await WriteJsonAsync(context, StatusCodes.Status200OK, new MemoriesDocument
            {
                ProtocolVersion = GatewayProtocolVersion.Current,
                HostId = identity.HostId,
                Space = space,
                Digest = result.Digest(),
                Memories = parsed.RootElement
            }, MaximumMemoriesResponseBytes, memoriesJson).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(document); }
    }

    private sealed record MemoriesDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public string? Space { get; init; }
        public required string Digest { get; init; }
        public required System.Text.Json.JsonElement Memories { get; init; }
    }

    private sealed record MemoriesDigestDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public string? Space { get; init; }
        public required string Digest { get; init; }
    }
}
