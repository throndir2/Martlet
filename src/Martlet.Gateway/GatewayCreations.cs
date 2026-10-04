using System.Security.Cryptography;
using System.Text;
using Martlet.Core.Creations;
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

/// <summary>This host's copy of everything Martlet made (docs/CREATIONS.md) and the pieces of their assets. A host performs
/// nothing itself: it keeps them so every paired desktop can copy them, including desktops that were off when a creation was
/// made, since desktops reach each other only through hosts. Pieces live in storage, not memory; without storage (or when a
/// piece can't be saved) up to <see cref="MaximumMemoryBytes"/> are kept in memory and desktops send any piece a restarted
/// host lacks again. Pieces no live creation uses are deleted.</summary>
internal sealed class GatewayCreationStore
{
    internal const long MaximumMemoryBytes = 256L * 1024 * 1024;
    private readonly object gate = new();
    private readonly HashSet<string> stored = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> memory = new(StringComparer.Ordinal);
    private long memoryBytes;
    private CreationLibrary library = CreationLibrary.Empty;
    private IReadOnlyDictionary<string, int> chunks = new Dictionary<string, int>();
    private string digest = CreationLibrary.Empty.Digest();
    private IGatewayCreationStorage? storage;

    internal CreationLibrary Current { get { lock (gate) return library; } }

    internal string Digest { get { lock (gate) return digest; } }

    /// <summary>The pieces this host holds, by SHA-256, sorted.</summary>
    internal IReadOnlyList<string> Present
    {
        get { lock (gate) return stored.Concat(memory.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(); }
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
            if (saved is not null) Replace(CreationLibrary.Merge(library, saved), save: false);
            foreach (var sha256 in chunks.Keys)
            {
                try { if (value.HasChunk(sha256)) stored.Add(sha256); }
                catch (Exception) { }
            }
        }
    }

    internal CreationLibrary Merge(CreationLibrary incoming)
    {
        lock (gate)
        {
            Replace(CreationLibrary.Merge(library, incoming), save: true);
            return library;
        }
    }

    /// <summary>Keeps a piece of a live creation. False when no live creation has a piece with that SHA-256 and length, or
    /// this host has nowhere to keep it.</summary>
    internal bool Store(string sha256, byte[] bytes)
    {
        lock (gate)
        {
            if (!chunks.TryGetValue(sha256, out var length) || bytes.Length != length ||
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

    /// <summary>A copy of the piece with <paramref name="sha256"/>, or null when this host does not hold it (a damaged saved
    /// piece is forgotten, so a desktop sends it again).</summary>
    internal byte[]? Chunk(string sha256)
    {
        lock (gate)
        {
            if (memory.TryGetValue(sha256, out var kept)) return (byte[])kept.Clone();
            if (!stored.Contains(sha256) || storage is null) return null;
            byte[]? bytes = null;
            try { bytes = storage.LoadChunk(sha256); }
            catch (Exception) { }
            if (bytes is not null && chunks.TryGetValue(sha256, out var length) && bytes.Length == length &&
                Convert.ToHexStringLower(SHA256.HashData(bytes)) == sha256)
                return bytes;
            stored.Remove(sha256);
            try { storage.RemoveChunk(sha256); }
            catch (Exception) { }
            return null;
        }
    }

    private void Replace(CreationLibrary next, bool save)
    {
        var nextDigest = next.Digest();
        if (nextDigest == digest) return;
        library = next;
        digest = nextDigest;
        chunks = next.LiveChunks();
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
        if (!save || storage is null) return;
        try { storage.SaveLibrary(next.Write()); }
        catch (Exception) { }
    }
}

internal sealed partial class GatewayHttpApplication
{
    internal const string CreationsPath = "/martlet/v1/creations";
    internal const string CreationsDigestPath = CreationsPath + "/digest";
    internal const string CreationChunkPath = CreationsPath + "/chunks/";
    private const int MaximumCreationsResponseBytes = CreationLibrary.MaximumBytes + 2 * 1024 * 1024;
    private const int MaximumCreationChunkBytes = (CreationLibrary.ChunkBytes + 2) / 3 * 4 + 4_096;

    internal GatewayCreationStore Creations { get; } = new();

    private static bool IsCreationsTarget(string rawTarget) =>
        rawTarget is CreationsPath or CreationsDigestPath || rawTarget.StartsWith(CreationChunkPath, StringComparison.Ordinal);

    /// <summary>GET /creations returns this host's copy of Martlet's creations and the pieces it holds; POST merges a desktop's
    /// copy into it; GET /creations/digest returns only the digests of both, so desktops read the copy when it changed.
    /// GET /creations/chunks/&lt;sha256&gt; returns a piece; POST sends one of a live creation. The creations are the owner's
    /// own: any paired device may use these over its signed, pinned connection; API keys may not.</summary>
    private async ValueTask InvokeCreationsAsync(HttpContext context, string rawTarget)
    {
        if (rawTarget == CreationsDigestPath)
        {
            GatewayRules.Require(context.Request.Method == HttpMethods.Get, "request.invalid");
            EnsureEmptyRequest(context.Request);
            _ = authenticator.Authenticate(context.Request);
            var present = Creations.Present;
            await WriteJsonAsync(context, StatusCodes.Status200OK, new CreationsDigestDocument
            {
                ProtocolVersion = GatewayProtocolVersion.Current,
                HostId = identity.HostId,
                Digest = Creations.Digest,
                PresentDigest = GatewayCreationStore.PresentDigest(present),
                PresentCount = present.Count
            }).ConfigureAwait(false);
            return;
        }
        if (rawTarget == CreationsPath)
        {
            CreationLibrary result;
            if (context.Request.Method == HttpMethods.Get)
            {
                EnsureEmptyRequest(context.Request);
                _ = authenticator.Authenticate(context.Request);
                result = Creations.Current;
            }
            else if (context.Request.Method == HttpMethods.Post)
            {
                var bytes = await ReadInferenceBodyAsync(context.Request, CreationLibrary.MaximumBytes, context.RequestAborted).ConfigureAwait(false);
                _ = authenticator.Authenticate(context.Request, crypto.Sha256(bytes));
                CreationLibrary incoming;
                try { incoming = CreationLibrary.Parse(bytes); }
                catch (Martlet.Core.Contracts.ContractException) { throw new GatewayProtocolException("request.invalid"); }
                result = Creations.Merge(incoming);
            }
            else throw new GatewayProtocolException("request.invalid");
            var document = result.Write();
            using var parsed = System.Text.Json.JsonDocument.Parse(document, new System.Text.Json.JsonDocumentOptions { MaxDepth = 32 });
            await WriteJsonAsync(context, StatusCodes.Status200OK, new CreationsDocument
            {
                ProtocolVersion = GatewayProtocolVersion.Current,
                HostId = identity.HostId,
                Library = parsed.RootElement,
                Present = Creations.Present
            }, MaximumCreationsResponseBytes, CreationsJson).ConfigureAwait(false);
            return;
        }

        var sha256 = rawTarget[CreationChunkPath.Length..];
        GatewayRules.Require(CreationLibrary.IsSha256(sha256), "request.invalid");
        if (context.Request.Method == HttpMethods.Get)
        {
            EnsureEmptyRequest(context.Request);
            _ = authenticator.Authenticate(context.Request);
            var chunk = Creations.Chunk(sha256) ?? throw new GatewayProtocolException("chunk.missing");
            await WriteJsonAsync(context, StatusCodes.Status200OK, new CreationChunkDocument
            {
                ProtocolVersion = GatewayProtocolVersion.Current,
                HostId = identity.HostId,
                ChunkSha256 = sha256,
                DataBase64 = Convert.ToBase64String(chunk)
            }, MaximumCreationChunkBytes, UnescapedJson).ConfigureAwait(false);
            return;
        }
        GatewayRules.Require(context.Request.Method == HttpMethods.Post, "request.invalid");
        var body = await ReadInferenceBodyAsync(context.Request, MaximumCreationChunkBytes, context.RequestAborted).ConfigureAwait(false);
        _ = authenticator.Authenticate(context.Request, crypto.Sha256(body));
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
        GatewayRules.Require(Creations.Store(sha256, data), "request.invalid");
        await WriteJsonAsync(context, StatusCodes.Status200OK, new CreationsStoredDocument
        {
            ProtocolVersion = GatewayProtocolVersion.Current,
            HostId = identity.HostId,
            Present = Creations.Present
        }, MaximumCreationsResponseBytes).ConfigureAwait(false);
    }

    // Titles and lyrics are written as they are, so the response stays within its limit.
    private static System.Text.Json.JsonSerializerOptions? creationsJson;
    private static System.Text.Json.JsonSerializerOptions CreationsJson =>
        creationsJson ??= new(Json) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, MaxDepth = 40 };

    private sealed record CreationsDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public required System.Text.Json.JsonElement Library { get; init; }
        public required IReadOnlyList<string> Present { get; init; }
    }

    private sealed record CreationsDigestDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public required string Digest { get; init; }
        public required string PresentDigest { get; init; }
        public required int PresentCount { get; init; }
    }

    private sealed record CreationChunkDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public required string ChunkSha256 { get; init; }
        public required string DataBase64 { get; init; }
    }

    private sealed record CreationsStoredDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public required IReadOnlyList<string> Present { get; init; }
    }
}
