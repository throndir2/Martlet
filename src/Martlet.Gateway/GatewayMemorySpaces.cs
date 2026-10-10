using System.Security.Cryptography;
using Martlet.Core.Contracts;
using Martlet.Core.Sync;
using Microsoft.AspNetCore.Http;

namespace Martlet.Gateway;

/// <summary>Where a gateway keeps its memory spaces between restarts (memories-&lt;space&gt;.json beside host.json on Linux hosts):
/// one <see cref="SharedMemories"/> document per space ID (<see cref="MemorySpaceId"/>). The spaces hold every account's
/// memories, so they must be private to the gateway owner. <see cref="Load"/> returns null when the space has no copy; every
/// call may throw on storage failure.</summary>
public interface IGatewayMemorySpaceStorage
{
    /// <summary>The IDs of the spaces saved here.</summary>
    IReadOnlyCollection<string> List();
    byte[]? Load(string space);
    void Save(string space, byte[] bytes);
}

/// <summary>What a device asks to do with a memory space.</summary>
internal enum GatewayMemorySpaceUse
{
    Read,
    Write
}

/// <summary>Whether a paired device may read or write a memory space. This is the seam where the account directory decides
/// which device may use which space (docs/ACCOUNTS.md, workstream W9). Friends and API keys never reach it: the routes refuse
/// them first.</summary>
internal delegate bool GatewayMemorySpaceAccess(GatewayPrincipal principal, string space, GatewayMemorySpaceUse use);

/// <summary>This host's memory spaces (docs/ACCOUNTS.md, "Memory spaces"): one copy of <see cref="SharedMemories"/> per space,
/// so every host keeps every account's memories apart. Paired desktops read the spaces they may read and merge their changes
/// into them; the host itself never looks inside a fact. A space exists once a merge put something in it; a host keeps at most
/// <see cref="MaximumSpaces"/>.</summary>
internal sealed class GatewayMemorySpaces
{
    internal const int MaximumSpaces = 64;
    private static readonly string EmptyDigest = SharedMemories.Empty.Digest();
    private readonly object gate = new();
    private readonly Dictionary<string, GatewayMemoryStore> spaces = new(StringComparer.Ordinal);
    private IGatewayMemorySpaceStorage? storage;

    /// <summary>Decides which device may use which space. The gateway application sets the account directory's rules
    /// (GatewayMemorySpaceAccess.cs); a store on its own lets every member device use every space.</summary>
    internal GatewayMemorySpaceAccess Access { get; set; } = EveryMember;

    internal static bool EveryMember(GatewayPrincipal principal, string space, GatewayMemorySpaceUse use) =>
        principal is { Access: GatewayAccess.Full, Key: null };

    /// <summary>The IDs of the spaces this host keeps, in order.</summary>
    internal IReadOnlyList<string> Spaces
    {
        get { lock (gate) return [.. spaces.Keys.Order(StringComparer.Ordinal)]; }
    }

    internal void Attach(IGatewayMemorySpaceStorage value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string[] saved;
        try { saved = [.. value.List().Where(MemorySpaceId.IsValid).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]; }
        // Spaces that cannot be listed come back from the next desktops that sync them.
        catch (Exception) { saved = []; }
        lock (gate)
        {
            storage = value;
            foreach (var (space, store) in spaces) store.Attach(new SpaceStorage(value, space));
            foreach (var space in saved)
            {
                if (spaces.ContainsKey(space)) continue;
                if (spaces.Count >= MaximumSpaces) break;
                var store = new GatewayMemoryStore();
                store.Attach(new SpaceStorage(value, space));
                spaces[space] = store;
            }
        }
    }

    internal SharedMemories Read(string space)
    {
        lock (gate) return spaces.TryGetValue(space, out var store) ? store.Current : SharedMemories.Empty;
    }

    internal string Digest(string space)
    {
        lock (gate) return spaces.TryGetValue(space, out var store) ? store.Digest : EmptyDigest;
    }

    /// <summary>Merges <paramref name="incoming"/> into the space and returns the merged copy. A merge that brings nothing makes
    /// no space; a new space past <see cref="MaximumSpaces"/> is <c>memories.spaces_full</c>.</summary>
    internal SharedMemories Merge(string space, SharedMemories incoming)
    {
        GatewayMemoryStore? store;
        lock (gate)
        {
            if (!spaces.TryGetValue(space, out store))
            {
                if (incoming.Facts.Count == 0) return SharedMemories.Empty;
                GatewayRules.Require(spaces.Count < MaximumSpaces, "memories.spaces_full");
                store = new GatewayMemoryStore();
                if (storage is not null) store.Attach(new SpaceStorage(storage, space));
                spaces[space] = store;
            }
        }
        return store.Merge(incoming);
    }

    private sealed class SpaceStorage(IGatewayMemorySpaceStorage storage, string space) : IGatewayMemoryStorage
    {
        public byte[]? Load() => storage.Load(space);
        public void Save(byte[] bytes) => storage.Save(space, bytes);
    }
}

internal sealed partial class GatewayHttpApplication
{
    internal const string MemorySpacesPath = MemoriesPath + "/spaces/";
    private const string MemorySpaceDigestSuffix = "/digest";

    internal GatewayMemorySpaces MemorySpaces { get; } = new();

    private static bool IsMemorySpaceTarget(string rawTarget) => rawTarget.StartsWith(MemorySpacesPath, StringComparison.Ordinal);

    /// <summary>GET /memories/spaces/{space} returns this host's copy of one memory space, POST merges a desktop's copy into it
    /// and returns the merged result, and GET /memories/spaces/{space}/digest returns only the copy's digest. The same rules as
    /// the single memory document apply (paired member devices over their signed, pinned connection; never friends or API
    /// keys), and <see cref="GatewayMemorySpaces.Access"/> decides which device may use which space. A POST needs both read
    /// and write access, because it returns the merged space.</summary>
    private async ValueTask InvokeMemorySpaceAsync(HttpContext context, string rawTarget)
    {
        var rest = rawTarget[MemorySpacesPath.Length..];
        var digestOnly = rest.EndsWith(MemorySpaceDigestSuffix, StringComparison.Ordinal);
        var space = digestOnly ? rest[..^MemorySpaceDigestSuffix.Length] : rest;
        GatewayRules.Require(MemorySpaceId.IsValid(space), "request.invalid");
        if (digestOnly)
        {
            GatewayRules.Require(context.Request.Method == HttpMethods.Get, "request.invalid");
            EnsureEmptyRequest(context.Request);
            AdmitMemorySpace(authenticator.Authenticate(context.Request), space, GatewayMemorySpaceUse.Read);
            await WriteJsonAsync(context, StatusCodes.Status200OK, new MemoriesDigestDocument
            {
                ProtocolVersion = GatewayProtocolVersion.Current,
                HostId = identity.HostId,
                Space = space,
                Digest = MemorySpaces.Digest(space)
            }).ConfigureAwait(false);
            return;
        }
        SharedMemories result;
        if (context.Request.Method == HttpMethods.Get)
        {
            EnsureEmptyRequest(context.Request);
            AdmitMemorySpace(authenticator.Authenticate(context.Request), space, GatewayMemorySpaceUse.Read);
            result = MemorySpaces.Read(space);
        }
        else if (context.Request.Method == HttpMethods.Post)
        {
            var bytes = await ReadInferenceBodyAsync(context.Request, SharedMemories.MaximumBytes, context.RequestAborted).ConfigureAwait(false);
            try
            {
                var principal = authenticator.Authenticate(context.Request, crypto.Sha256(bytes));
                AdmitMemorySpace(principal, space, GatewayMemorySpaceUse.Read);
                AdmitMemorySpace(principal, space, GatewayMemorySpaceUse.Write);
                SharedMemories incoming;
                try { incoming = SharedMemories.Parse(bytes); }
                catch (ContractException) { throw new GatewayProtocolException("request.invalid"); }
                result = MemorySpaces.Merge(space, incoming);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        else throw new GatewayProtocolException("request.invalid");
        await WriteMemoriesAsync(context, result, space).ConfigureAwait(false);
    }

    private void AdmitMemorySpace(GatewayPrincipal principal, string space, GatewayMemorySpaceUse use) =>
        GatewayRules.Require(MemorySpaces.Access(principal, space, use), "memories.space_denied");
}
