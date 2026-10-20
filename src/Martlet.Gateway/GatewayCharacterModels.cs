using System.Security.Cryptography;
using Martlet.Core.Characters;
using Microsoft.AspNetCore.Http;

namespace Martlet.Gateway;

/// <summary>Where a gateway keeps its copy of the shared character-model list and the models' pieces between restarts
/// (character-models.json and character-model-chunk-&lt;sha256&gt;.bin beside host.json on Linux hosts). Loads return null when
/// there is nothing; any call may throw on storage failure.</summary>
public interface IGatewayCharacterModelStorage
{
    byte[]? LoadLibrary();
    void SaveLibrary(byte[] bytes);
    bool HasChunk(string sha256);
    byte[]? LoadChunk(string sha256);
    void SaveChunk(string sha256, byte[] bytes);
    void RemoveChunk(string sha256);
}

/// <summary>This host's copy of the character models the owner added and their pieces. A host shows no character itself:
/// it keeps them so every paired desktop (every computer that can be the companion) can copy them, since desktops reach each
/// other only through hosts. Pieces live in storage, not memory; without storage (or when a piece can't be saved) up to
/// <see cref="MaximumMemoryBytes"/> are kept in memory instead, and desktops send any piece a restarted host lacks again.
/// Pieces no live model uses are deleted.</summary>
internal sealed class GatewayCharacterModelStore
{
    /// <summary>The most piece bytes kept in memory: the old limit of all characters together, so a host without storage
    /// can't run out of memory now that the characters may be much larger.</summary>
    internal const long MaximumMemoryBytes = 512L * 1024 * 1024;
    private readonly object gate = new();
    private readonly HashSet<string> stored = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> memory = new(StringComparer.Ordinal);
    private long memoryBytes;
    private CharacterModelLibrary library = CharacterModelLibrary.Empty;
    private IReadOnlyDictionary<string, int> chunks = new Dictionary<string, int>();
    private string digest = CharacterModelLibrary.Empty.Digest();
    private IGatewayCharacterModelStorage? storage;
    private GatewayStoreHealth health = new();

    internal CharacterModelLibrary Current { get { lock (gate) return library; } }

    /// <summary>The pieces this host holds, by SHA-256, sorted.</summary>
    internal IReadOnlyList<string> Present
    {
        get { lock (gate) return stored.Concat(memory.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(); }
    }

    internal void Attach(IGatewayCharacterModelStorage value, GatewayStoreHealth? storeHealth = null)
    {
        if (storeHealth is not null) health = storeHealth;
        ArgumentNullException.ThrowIfNull(value);
        CharacterModelLibrary? saved = null;
        // An unreadable or malformed copy is replaced by the next desktop that syncs.
        health.Try("character models", "load", () => { if (value.LoadLibrary() is { } bytes) saved = CharacterModelLibrary.Parse(bytes); });
        lock (gate)
        {
            storage = value;
            if (saved is not null) Replace(CharacterModelLibrary.Merge(library, saved), save: false);
            foreach (var sha256 in chunks.Keys)
            {
                health.Try("character models", "load", () => { if (value.HasChunk(sha256)) stored.Add(sha256); });
            }
        }
    }

    internal CharacterModelLibrary Merge(CharacterModelLibrary incoming)
    {
        lock (gate)
        {
            Replace(CharacterModelLibrary.Merge(library, incoming), save: true);
            return library;
        }
    }

    /// <summary>Keeps a piece of a live model. False when no live model has a piece with that SHA-256 and length, or when it
    /// can't be saved and <see cref="MaximumMemoryBytes"/> are already kept in memory.</summary>
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
                if (health.Try("character models", "save", () => storage.SaveChunk(sha256, bytes)))
                {
                    stored.Add(sha256);
                    return true;
                }
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
            health.Try("character models", "load", () => { bytes = storage.LoadChunk(sha256); });
            if (bytes is not null && chunks.TryGetValue(sha256, out var length) && bytes.Length == length &&
                Convert.ToHexStringLower(SHA256.HashData(bytes)) == sha256)
                return bytes;
            stored.Remove(sha256);
            health.Try("character models", "remove", () => { storage.RemoveChunk(sha256); });
            return null;
        }
    }

    private void Replace(CharacterModelLibrary next, bool save)
    {
        var nextDigest = next.Digest();
        if (nextDigest == digest) return;
        library = next;
        digest = nextDigest;
        chunks = next.LiveChunks();
        foreach (var sha256 in memory.Keys.Where(k => !chunks.ContainsKey(k)).ToArray())
        {
            memoryBytes -= memory[sha256].Length;
            CryptographicOperations.ZeroMemory(memory[sha256]);
            memory.Remove(sha256);
        }
        foreach (var sha256 in stored.Where(k => !chunks.ContainsKey(k)).ToArray())
        {
            stored.Remove(sha256);
            health.Try("character models", "remove", () => { storage?.RemoveChunk(sha256); });
        }
        if (!save || storage is null) return;
        health.Try("character models", "save", () => { storage.SaveLibrary(next.Write()); });
    }
}

internal sealed class GatewayCharacterModelRoutes(GatewayHttpServices services, GatewayCharacterModelStore characterModels) : GatewayRouteHandler(services)
{
    internal const string CharacterModelsPath = "/martlet/v1/character-models";
    internal const string CharacterModelChunkPath = CharacterModelsPath + "/chunks/";
    // The list, and the SHA-256 of each piece this host holds: at most one per piece the list names, so at most the list's size again.
    private const int MaximumCharacterModelsResponseBytes = 2 * CharacterModelLibrary.MaximumBytes + 262_144;
    private const int MaximumCharacterModelChunkBytes = (CharacterModelLibrary.ChunkBytes + 2) / 3 * 4 + 4_096;

    internal GatewayCharacterModelStore CharacterModels { get; } = characterModels;

    internal static bool IsCharacterModelsTarget(string rawTarget) =>
        rawTarget == CharacterModelsPath || rawTarget.StartsWith(CharacterModelChunkPath, StringComparison.Ordinal);

    /// <summary>GET /character-models returns this host's copy of the character list and the pieces it holds; POST merges a
    /// desktop's copy into it. GET /character-models/chunks/&lt;sha256&gt; returns a piece; POST sends one of a live model.
    /// Any paired device may do each over its signed, pinned connection; API keys may not.</summary>
    internal async ValueTask InvokeCharacterModelsAsync(HttpContext context, string rawTarget)
    {
        if (rawTarget == CharacterModelsPath)
        {
            CharacterModelLibrary result;
            if (context.Request.Method == HttpMethods.Get)
            {
                EnsureEmptyRequest(context.Request);
                _ = authenticator.Authenticate(context.Request);
                result = CharacterModels.Current;
            }
            else if (context.Request.Method == HttpMethods.Post)
            {
                var bytes = await ReadInferenceBodyAsync(context.Request, CharacterModelLibrary.MaximumBytes, context.RequestAborted)
                    .ConfigureAwait(false);
                _ = authenticator.Authenticate(context.Request, crypto.Sha256(bytes));
                CharacterModelLibrary incoming;
                try { incoming = CharacterModelLibrary.Parse(bytes); }
                catch (Martlet.Core.Contracts.ContractException) { throw new GatewayProtocolException("request.invalid"); }
                result = CharacterModels.Merge(incoming);
            }
            else throw new GatewayProtocolException("request.invalid");
            await WriteJsonAsync(context, StatusCodes.Status200OK, new CharacterModelsDocument
            {
                ProtocolVersion = GatewayProtocolVersion.Current,
                HostId = identity.HostId,
                Library = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(result.Write()),
                Present = CharacterModels.Present
            }, MaximumCharacterModelsResponseBytes).ConfigureAwait(false);
            return;
        }

        var sha256 = rawTarget[CharacterModelChunkPath.Length..];
        GatewayRules.Require(CharacterModelLibrary.IsSha256(sha256), "request.invalid");
        if (context.Request.Method == HttpMethods.Get)
        {
            EnsureEmptyRequest(context.Request);
            _ = authenticator.Authenticate(context.Request);
            var chunk = CharacterModels.Chunk(sha256) ?? throw new GatewayProtocolException("chunk.missing");
            await WriteJsonAsync(context, StatusCodes.Status200OK, new CharacterModelChunkDocument
            {
                ProtocolVersion = GatewayProtocolVersion.Current,
                HostId = identity.HostId,
                ChunkSha256 = sha256,
                DataBase64 = Convert.ToBase64String(chunk)
            }, MaximumCharacterModelChunkBytes, UnescapedJson).ConfigureAwait(false);
            return;
        }
        GatewayRules.Require(context.Request.Method == HttpMethods.Post, "request.invalid");
        var body = await ReadInferenceBodyAsync(context.Request, MaximumCharacterModelChunkBytes, context.RequestAborted).ConfigureAwait(false);
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
        GatewayRules.Require(CharacterModels.Store(sha256, data), "request.invalid");
        await WriteJsonAsync(context, StatusCodes.Status200OK, new CharacterModelsStoredDocument
        {
            ProtocolVersion = GatewayProtocolVersion.Current,
            HostId = identity.HostId,
            Present = CharacterModels.Present
        }, MaximumCharacterModelsResponseBytes).ConfigureAwait(false);
    }

    private sealed record CharacterModelsDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public required System.Text.Json.JsonElement Library { get; init; }
        public required IReadOnlyList<string> Present { get; init; }
    }

    private sealed record CharacterModelChunkDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public required string ChunkSha256 { get; init; }
        public required string DataBase64 { get; init; }
    }

    private sealed record CharacterModelsStoredDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public required IReadOnlyList<string> Present { get; init; }
    }
}
