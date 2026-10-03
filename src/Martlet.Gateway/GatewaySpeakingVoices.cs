using System.Security.Cryptography;
using Martlet.Core.Contracts;
using Martlet.Core.Voices;
using Microsoft.AspNetCore.Http;

namespace Martlet.Gateway;

/// <summary>Where a gateway keeps its copy of the shared speaking-voice list and each voice's recording between restarts
/// (speaking-voices.json and speaking-voice-&lt;sha256&gt;.wav beside host.json on Linux hosts). Loads return null when there
/// is nothing; any call may throw on storage failure.</summary>
public interface IGatewaySpeakingVoiceStorage
{
    byte[]? LoadLibrary();
    void SaveLibrary(byte[] bytes);
    byte[]? LoadAudio(string sha256);
    void SaveAudio(string sha256, byte[] bytes);
    void RemoveAudio(string sha256);
}

/// <summary>This host's copy of the voices Martlet speaks with and their recordings. Paired desktops merge the list into it
/// and send each recording once, so a speaking request names its voice by the recording's SHA-256 instead of carrying the
/// recording, and any desktop can be the companion with the same voices. Recordings of removed voices are deleted.
/// A copy that cannot be saved is still served from memory, and desktops send it again.</summary>
internal sealed class GatewaySpeakingVoiceStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, byte[]> audio = new(StringComparer.Ordinal);
    private SpeakingVoiceLibrary library = SpeakingVoiceLibrary.Empty;
    private string digest = SpeakingVoiceLibrary.Empty.Digest();
    private IGatewaySpeakingVoiceStorage? storage;

    internal SpeakingVoiceLibrary Current { get { lock (gate) return library; } }

    /// <summary>The recordings this host holds, by SHA-256, sorted.</summary>
    internal IReadOnlyList<string> Present { get { lock (gate) return audio.Keys.Order(StringComparer.Ordinal).ToArray(); } }

    internal void Attach(IGatewaySpeakingVoiceStorage value)
    {
        ArgumentNullException.ThrowIfNull(value);
        SpeakingVoiceLibrary? saved = null;
        try { if (value.LoadLibrary() is { } bytes) saved = SpeakingVoiceLibrary.Parse(bytes); }
        // An unreadable or malformed copy is replaced by the next desktop that syncs.
        catch (Exception) { }
        lock (gate)
        {
            storage = value;
            if (saved is not null) Replace(SpeakingVoiceLibrary.Merge(library, saved), save: false);
            foreach (var sha256 in Referenced())
            {
                if (audio.ContainsKey(sha256)) continue;
                try
                {
                    if (value.LoadAudio(sha256) is { } bytes && Valid(sha256, bytes)) audio[sha256] = bytes;
                }
                catch (Exception) { }
            }
        }
    }

    internal SpeakingVoiceLibrary Merge(SpeakingVoiceLibrary incoming)
    {
        lock (gate)
        {
            Replace(SpeakingVoiceLibrary.Merge(library, incoming), save: true);
            return library;
        }
    }

    /// <summary>Keeps a live voice's recording. False when no live voice has that SHA-256 or the bytes are not that
    /// recording (a mono 16-bit PCM WAV of 1 to 30 seconds).</summary>
    internal bool Store(string sha256, byte[] bytes)
    {
        lock (gate)
        {
            if (!Referenced().Contains(sha256) || !Valid(sha256, bytes)) return false;
            if (audio.ContainsKey(sha256)) return true;
            var copy = (byte[])bytes.Clone();
            audio[sha256] = copy;
            try { storage?.SaveAudio(sha256, copy); }
            catch (Exception) { }
            return true;
        }
    }

    /// <summary>A copy of the recording with <paramref name="sha256"/>, or null when this host does not hold it.</summary>
    internal byte[]? Audio(string sha256)
    {
        lock (gate) return audio.TryGetValue(sha256, out var bytes) ? (byte[])bytes.Clone() : null;
    }

    /// <summary>The live voice with this ID (its reference revision), or null: a speaking request finds a voice's recordings
    /// (<see cref="SpeakingVoice.Clips"/>) here.</summary>
    internal SpeakingVoice? Voice(string id)
    {
        lock (gate) return library.Find(id) is { Removed: false } voice ? voice : null;
    }

    /// <summary>Keeps a recording a speaking request carried when a live voice has it and this host lacked it, so the next
    /// request can name it instead.</summary>
    internal void Remember(string sha256, ReadOnlySpan<byte> bytes)
    {
        lock (gate)
        {
            if (audio.ContainsKey(sha256) || !Referenced().Contains(sha256)) return;
        }
        Store(sha256, bytes.ToArray());
    }

    private HashSet<string> Referenced() =>
        library.Live.Select(v => v.AudioSha256!).ToHashSet(StringComparer.Ordinal);

    private static bool Valid(string sha256, byte[] bytes)
    {
        if (bytes.Length is < 44 or > SpeakingVoiceLibrary.MaximumAudioBytes ||
            Convert.ToHexStringLower(SHA256.HashData(bytes)) != sha256)
            return false;
        try
        {
            return PcmWaveInfo.Inspect(bytes, SpeakingVoiceLibrary.MaximumAudioBytes).DurationMilliseconds is
                >= SpeakingVoiceLibrary.MinimumDurationMilliseconds and <= SpeakingVoiceLibrary.MaximumDurationMilliseconds;
        }
        catch (Exception error) when (error is ContractException or ArgumentException or OverflowException) { return false; }
    }

    private void Replace(SpeakingVoiceLibrary next, bool save)
    {
        var nextDigest = next.Digest();
        if (nextDigest == digest) return;
        library = next;
        digest = nextDigest;
        var referenced = Referenced();
        foreach (var sha256 in audio.Keys.Where(k => !referenced.Contains(k)).ToArray())
        {
            CryptographicOperations.ZeroMemory(audio[sha256]);
            audio.Remove(sha256);
            try { storage?.RemoveAudio(sha256); }
            catch (Exception) { }
        }
        if (!save || storage is null) return;
        try { storage.SaveLibrary(next.Write()); }
        catch (Exception) { }
    }
}

internal sealed partial class GatewayHttpApplication
{
    internal const string SpeakingVoicesPath = "/martlet/v1/speaking-voices";
    internal const string SpeakingVoiceAudioPath = SpeakingVoicesPath + "/audio/";
    private const int MaximumSpeakingVoicesResponseBytes = SpeakingVoiceLibrary.MaximumBytes + 16_384;
    private const int MaximumSpeakingVoiceAudioBytes = (SpeakingVoiceLibrary.MaximumAudioBytes + 2) / 3 * 4 + 4_096;

    internal GatewaySpeakingVoiceStore SpeakingVoices { get; } = new();

    private static bool IsSpeakingVoicesTarget(string rawTarget) =>
        rawTarget == SpeakingVoicesPath || rawTarget.StartsWith(SpeakingVoiceAudioPath, StringComparison.Ordinal);

    /// <summary>GET /speaking-voices returns this host's copy of the voice list and the recordings it holds; POST merges a
    /// desktop's copy into it. GET /speaking-voices/audio/&lt;sha256&gt; returns a recording; POST sends one for a live voice.
    /// Any paired device may do each over its signed, pinned connection; API keys may not.</summary>
    private async ValueTask InvokeSpeakingVoicesAsync(HttpContext context, string rawTarget)
    {
        if (rawTarget == SpeakingVoicesPath)
        {
            SpeakingVoiceLibrary result;
            if (context.Request.Method == HttpMethods.Get)
            {
                EnsureEmptyRequest(context.Request);
                _ = authenticator.Authenticate(context.Request);
                result = SpeakingVoices.Current;
            }
            else if (context.Request.Method == HttpMethods.Post)
            {
                var bytes = await ReadInferenceBodyAsync(context.Request, SpeakingVoiceLibrary.MaximumBytes, context.RequestAborted)
                    .ConfigureAwait(false);
                _ = authenticator.Authenticate(context.Request, crypto.Sha256(bytes));
                SpeakingVoiceLibrary incoming;
                try { incoming = SpeakingVoiceLibrary.Parse(bytes); }
                catch (ContractException) { throw new GatewayProtocolException("request.invalid"); }
                result = SpeakingVoices.Merge(incoming);
            }
            else throw new GatewayProtocolException("request.invalid");
            await WriteJsonAsync(context, StatusCodes.Status200OK, new SpeakingVoicesDocument
            {
                ProtocolVersion = GatewayProtocolVersion.Current,
                HostId = identity.HostId,
                Library = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(result.Write()),
                Present = SpeakingVoices.Present
            }, MaximumSpeakingVoicesResponseBytes).ConfigureAwait(false);
            return;
        }

        var sha256 = rawTarget[SpeakingVoiceAudioPath.Length..];
        GatewayRules.Require(SpeakingVoiceLibrary.IsSha256(sha256), "request.invalid");
        if (context.Request.Method == HttpMethods.Get)
        {
            EnsureEmptyRequest(context.Request);
            _ = authenticator.Authenticate(context.Request);
            var audio = SpeakingVoices.Audio(sha256) ?? throw new GatewayProtocolException("reference.missing");
            try
            {
                await WriteJsonAsync(context, StatusCodes.Status200OK, new SpeakingVoiceAudioDocument
                {
                    ProtocolVersion = GatewayProtocolVersion.Current,
                    HostId = identity.HostId,
                    AudioSha256 = sha256,
                    AudioBase64 = Convert.ToBase64String(audio)
                }, MaximumSpeakingVoiceAudioBytes, UnescapedJson).ConfigureAwait(false);
            }
            finally { CryptographicOperations.ZeroMemory(audio); }
            return;
        }
        GatewayRules.Require(context.Request.Method == HttpMethods.Post, "request.invalid");
        var body = await ReadInferenceBodyAsync(context.Request, MaximumSpeakingVoiceAudioBytes, context.RequestAborted).ConfigureAwait(false);
        _ = authenticator.Authenticate(context.Request, crypto.Sha256(body));
        byte[] recording;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            var root = document.RootElement;
            GatewayRules.Require(root.ValueKind == System.Text.Json.JsonValueKind.Object &&
                root.EnumerateObject().Select(p => p.Name).SequenceEqual(["audio_base64"]), "request.invalid");
            recording = Convert.FromBase64String(root.GetProperty("audio_base64").GetString() ?? "");
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or FormatException or InvalidOperationException)
        {
            throw new GatewayProtocolException("request.invalid");
        }
        finally { CryptographicOperations.ZeroMemory(body); }
        try { GatewayRules.Require(SpeakingVoices.Store(sha256, recording), "request.invalid"); }
        finally { CryptographicOperations.ZeroMemory(recording); }
        await WriteJsonAsync(context, StatusCodes.Status200OK, new SpeakingVoiceStoredDocument
        {
            ProtocolVersion = GatewayProtocolVersion.Current,
            HostId = identity.HostId,
            Present = SpeakingVoices.Present
        }, MaximumSpeakingVoicesResponseBytes).ConfigureAwait(false);
    }

    private sealed record SpeakingVoicesDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public required System.Text.Json.JsonElement Library { get; init; }
        public required IReadOnlyList<string> Present { get; init; }
    }

    private sealed record SpeakingVoiceAudioDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public required string AudioSha256 { get; init; }
        public required string AudioBase64 { get; init; }
    }

    private sealed record SpeakingVoiceStoredDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public required IReadOnlyList<string> Present { get; init; }
    }
}
