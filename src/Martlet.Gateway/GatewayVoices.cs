using Martlet.Core.Contracts;
using Martlet.Core.Speakers;
using Microsoft.AspNetCore.Http;

namespace Martlet.Gateway;

/// <summary>Where a gateway keeps its copy of the shared voice list between restarts (voices.json beside host.json on Linux
/// hosts). <see cref="Load"/> returns null when there is none; either call may throw on storage failure.</summary>
public interface IGatewayVoiceStorage
{
    byte[]? Load();
    void Save(byte[] bytes);
}

/// <summary>This host's copy of the voices the owner's computers recognize. Paired desktops read it and merge their changes
/// into it, so whichever computer is the companion knows the same people; the host itself never uses it. A copy that
/// cannot be saved is still served from memory, and desktops push it again.</summary>
internal sealed class GatewayVoiceStore
{
    private readonly object gate = new();
    private VoiceRoster roster = VoiceRoster.Empty;
    private string digest = VoiceRoster.Empty.Digest();
    private IGatewayVoiceStorage? storage;

    internal VoiceRoster Current { get { lock (gate) return roster; } }

    internal void Attach(IGatewayVoiceStorage value)
    {
        ArgumentNullException.ThrowIfNull(value);
        VoiceRoster? saved = null;
        try { if (value.Load() is { } bytes) saved = VoiceRoster.Parse(bytes); }
        // An unreadable or malformed copy is replaced by the next desktop that syncs.
        catch (Exception) { }
        lock (gate)
        {
            storage = value;
            if (saved is not null) Replace(VoiceRoster.Merge(roster, saved), save: false);
        }
    }

    internal VoiceRoster Merge(VoiceRoster incoming)
    {
        lock (gate)
        {
            Replace(VoiceRoster.Merge(roster, incoming), save: true);
            return roster;
        }
    }

    private void Replace(VoiceRoster next, bool save)
    {
        var nextDigest = next.Digest();
        if (nextDigest == digest) return;
        roster = next;
        digest = nextDigest;
        if (!save || storage is null) return;
        try { storage.Save(next.Write()); }
        catch (Exception) { }
    }
}

internal sealed partial class GatewayHttpApplication
{
    internal const string VoicesPath = "/martlet/v1/voices";
    private const int MaximumVoicesResponseBytes = VoiceRoster.MaximumBytes + 4_096;

    internal GatewayVoiceStore Voices { get; } = new();

    /// <summary>GET returns this host's copy of the voice list; POST merges a desktop's copy into it and returns the merged
    /// result. Any paired device may do either over its signed, pinned connection.</summary>
    private async ValueTask InvokeVoicesAsync(HttpContext context)
    {
        VoiceRoster result;
        if (context.Request.Method == HttpMethods.Get)
        {
            EnsureEmptyRequest(context.Request);
            _ = authenticator.Authenticate(context.Request);
            result = Voices.Current;
        }
        else if (context.Request.Method == HttpMethods.Post)
        {
            var bytes = await ReadInferenceBodyAsync(context.Request, VoiceRoster.MaximumBytes, context.RequestAborted)
                .ConfigureAwait(false);
            _ = authenticator.Authenticate(context.Request, crypto.Sha256(bytes));
            VoiceRoster incoming;
            try { incoming = VoiceRoster.Parse(bytes); }
            catch (ContractException) { throw new GatewayProtocolException("request.invalid"); }
            result = Voices.Merge(incoming);
        }
        else throw new GatewayProtocolException("request.invalid");
        await WriteJsonAsync(context, StatusCodes.Status200OK, new VoicesDocument
        {
            ProtocolVersion = GatewayProtocolVersion.Current,
            HostId = identity.HostId,
            Roster = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(result.Write())
        }, MaximumVoicesResponseBytes).ConfigureAwait(false);
    }

    private sealed record VoicesDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public required System.Text.Json.JsonElement Roster { get; init; }
    }
}
