using System.Security.Cryptography;
using Martlet.Core.Contracts;
using Martlet.Core.Sync;
using Microsoft.AspNetCore.Http;

namespace Martlet.Gateway;

/// <summary>Where a gateway keeps its copy of the owner's shared settings between restarts (shared-settings.json beside
/// host.json on Linux hosts). The copy holds API keys, so it must be private to the gateway owner. <see cref="Load"/> returns
/// null when there is none; either call may throw on storage failure.</summary>
public interface IGatewaySettingsStorage
{
    byte[]? Load();
    void Save(byte[] bytes);
}

/// <summary>This host's copy of the settings every computer of the owner shares (how Martlet thinks, listens and speaks, its
/// character and their API keys; docs/CLUSTER.md). Paired desktops read it and merge their changes into it; the host itself
/// never uses it. A copy that cannot be saved is still served from memory, and desktops push it again.</summary>
internal sealed class GatewaySettingsStore
{
    private readonly object gate = new();
    private SharedSettings settings = SharedSettings.Empty;
    private string digest = SharedSettings.Empty.Digest();
    private IGatewaySettingsStorage? storage;

    internal SharedSettings Current { get { lock (gate) return settings; } }

    internal string Digest { get { lock (gate) return digest; } }

    internal void Attach(IGatewaySettingsStorage value)
    {
        ArgumentNullException.ThrowIfNull(value);
        SharedSettings? saved = null;
        try { if (value.Load() is { } bytes) saved = SharedSettings.Parse(bytes); }
        // An unreadable or malformed copy is replaced by the next desktop that syncs.
        catch (Exception) { }
        lock (gate)
        {
            storage = value;
            if (saved is not null) Replace(SharedSettings.Merge(settings, saved), save: false);
        }
    }

    internal SharedSettings Merge(SharedSettings incoming)
    {
        lock (gate)
        {
            Replace(SharedSettings.Merge(settings, incoming), save: true);
            return settings;
        }
    }

    private void Replace(SharedSettings next, bool save)
    {
        var nextDigest = next.Digest();
        if (nextDigest == digest) return;
        settings = next;
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
    internal const string SettingsPath = "/martlet/v1/settings";
    internal const string SettingsDigestPath = SettingsPath + "/digest";
    private const int MaximumSettingsResponseBytes = SharedSettings.MaximumBytes + 16_384;

    internal GatewaySettingsStore Settings { get; } = new();

    private static bool IsSettingsTarget(string rawTarget) => rawTarget is SettingsPath or SettingsDigestPath;

    /// <summary>GET /settings returns this host's copy of the shared settings and POST merges a desktop's copy into it and returns
    /// the merged result; GET /settings/digest returns only the copy's digest, so desktops read the copy when it changed. The
    /// copy holds API keys: only paired devices may use these, over their signed, pinned connection; API keys may not.</summary>
    private async ValueTask InvokeSettingsAsync(HttpContext context, string rawTarget)
    {
        if (rawTarget == SettingsDigestPath)
        {
            GatewayRules.Require(context.Request.Method == HttpMethods.Get, "request.invalid");
            EnsureEmptyRequest(context.Request);
            _ = authenticator.Authenticate(context.Request);
            await WriteJsonAsync(context, StatusCodes.Status200OK, new SettingsDigestDocument
            {
                ProtocolVersion = GatewayProtocolVersion.Current,
                HostId = identity.HostId,
                Digest = Settings.Digest
            }).ConfigureAwait(false);
            return;
        }
        SharedSettings result;
        if (context.Request.Method == HttpMethods.Get)
        {
            EnsureEmptyRequest(context.Request);
            _ = authenticator.Authenticate(context.Request);
            result = Settings.Current;
        }
        else if (context.Request.Method == HttpMethods.Post)
        {
            var bytes = await ReadInferenceBodyAsync(context.Request, SharedSettings.MaximumBytes, context.RequestAborted).ConfigureAwait(false);
            try
            {
                _ = authenticator.Authenticate(context.Request, crypto.Sha256(bytes));
                SharedSettings incoming;
                try { incoming = SharedSettings.Parse(bytes); }
                catch (ContractException) { throw new GatewayProtocolException("request.invalid"); }
                result = Settings.Merge(incoming);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        else throw new GatewayProtocolException("request.invalid");
        var document = result.Write();
        try
        {
            using var parsed = System.Text.Json.JsonDocument.Parse(document, new System.Text.Json.JsonDocumentOptions { MaxDepth = 8 });
            await WriteJsonAsync(context, StatusCodes.Status200OK, new SettingsDocument
            {
                ProtocolVersion = GatewayProtocolVersion.Current,
                HostId = identity.HostId,
                Digest = result.Digest(),
                Settings = parsed.RootElement
            }, MaximumSettingsResponseBytes).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(document); }
    }

    private sealed record SettingsDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public required string Digest { get; init; }
        public required System.Text.Json.JsonElement Settings { get; init; }
    }

    private sealed record SettingsDigestDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public required string Digest { get; init; }
    }
}
