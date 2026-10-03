using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Martlet.Core.Contracts;
using Martlet.Core.Voices;

namespace Martlet.Avatar.Audio2Face.Remote;

/// <summary>A host's copy of the voices Martlet speaks with and the SHA-256 of each recording it holds.</summary>
public sealed record HostSpeakingVoices(SpeakingVoiceLibrary Library, IReadOnlySet<string> Present);

/// <summary>The host's copy of the shared speaking-voice list and its recordings (docs/CLUSTER.md, "The shared speaking
/// voices"). Hosts older than it refuse with <c>request.invalid</c>.</summary>
public sealed partial class Audio2FaceHostConnection
{
    private const string SpeakingVoicesPath = "/martlet/v1/speaking-voices";
    private const string SpeakingVoiceAudioPath = SpeakingVoicesPath + "/audio/";
    private const int MaximumSpeakingVoicesBytes = SpeakingVoiceLibrary.MaximumBytes + 16_384;
    private const int MaximumSpeakingVoiceAudioBytes = (SpeakingVoiceLibrary.MaximumAudioBytes + 2) / 3 * 4 + 4_096;

    public async Task<HostSpeakingVoices> ReadSpeakingVoicesAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, pairing.Origin + SpeakingVoicesPath);
        Sign(request, []);
        return await SpeakingVoicesAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Merges <paramref name="library"/> into the host's copy and returns the merged list, which may include changes
    /// another computer made, and the recordings the host holds.</summary>
    public async Task<HostSpeakingVoices> MergeSpeakingVoicesAsync(SpeakingVoiceLibrary library, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(library);
        var body = library.Write();
        using var request = new HttpRequestMessage(HttpMethod.Post, pairing.Origin + SpeakingVoicesPath)
        {
            Content = Audio2FaceHostClient.JsonContent(body)
        };
        Sign(request, body);
        return await SpeakingVoicesAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends a live voice's recording to the host. The host keeps it only when its list has a live voice with that
    /// recording (merge the list first).</summary>
    public async Task<IReadOnlySet<string>> SendSpeakingVoiceAudioAsync(string sha256, ReadOnlyMemory<byte> audio,
        CancellationToken cancellationToken = default)
    {
        if (!SpeakingVoiceLibrary.IsSha256(sha256)) throw new ArgumentException("Invalid recording SHA-256.", nameof(sha256));
        var body = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["audio_base64"] = Convert.ToBase64String(audio.Span) });
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, pairing.Origin + SpeakingVoiceAudioPath + sha256)
            {
                Content = Audio2FaceHostClient.JsonContent(body)
            };
            Sign(request, body);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            using var response = await Audio2FaceHostClient.Send(http, request, timeout.Token).ConfigureAwait(false);
            using var document = await Audio2FaceHostClient.ReadJson(response, MaximumSpeakingVoicesBytes, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK) throw Audio2FaceHostClient.Remote(document.RootElement);
            return Present(document.RootElement);
        }
        finally { CryptographicOperations.ZeroMemory(body); }
    }

    /// <summary>Reads a recording the host holds, checked against <paramref name="sha256"/>; null when it has none.</summary>
    public async Task<byte[]?> ReadSpeakingVoiceAudioAsync(string sha256, CancellationToken cancellationToken = default)
    {
        if (!SpeakingVoiceLibrary.IsSha256(sha256)) throw new ArgumentException("Invalid recording SHA-256.", nameof(sha256));
        using var request = new HttpRequestMessage(HttpMethod.Get, pairing.Origin + SpeakingVoiceAudioPath + sha256);
        Sign(request, []);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var response = await Audio2FaceHostClient.Send(http, request, timeout.Token).ConfigureAwait(false);
        using var document = await Audio2FaceHostClient.ReadJson(response, MaximumSpeakingVoiceAudioBytes, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            var failure = Audio2FaceHostClient.Remote(document.RootElement);
            if (failure.Code == "reference.missing") return null;
            throw failure;
        }
        try
        {
            var root = document.RootElement;
            if (root.GetProperty("host_id").GetString() != pairing.HostId)
                throw new Audio2FaceHostException("response.invalid", "The host identity changed; pair again.");
            var audio = Convert.FromBase64String(root.GetProperty("audio_base64").GetString() ?? "");
            if (root.GetProperty("audio_sha256").GetString() != sha256 || audio.Length > SpeakingVoiceLibrary.MaximumAudioBytes ||
                Convert.ToHexStringLower(SHA256.HashData(audio)) != sha256)
                throw new Audio2FaceHostException("response.invalid", "The host's copy of the recording was invalid.");
            return audio;
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new Audio2FaceHostException("response.invalid", "The host's copy of the recording was invalid.");
        }
    }

    private async Task<HostSpeakingVoices> SpeakingVoicesAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var response = await Audio2FaceHostClient.Send(http, request, timeout.Token).ConfigureAwait(false);
        using var document = await Audio2FaceHostClient.ReadJson(response, MaximumSpeakingVoicesBytes, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw Audio2FaceHostClient.Remote(document.RootElement);
        try
        {
            var root = document.RootElement;
            if (root.GetProperty("host_id").GetString() != pairing.HostId)
                throw new Audio2FaceHostException("response.invalid", "The host identity changed; pair again.");
            return new(SpeakingVoiceLibrary.Parse(JsonSerializer.SerializeToUtf8Bytes(root.GetProperty("library"))), Present(root));
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or ContractException)
        {
            throw new Audio2FaceHostException("response.invalid", "The host's copy of the voice list was invalid.");
        }
    }

    private static HashSet<string> Present(JsonElement root)
    {
        try
        {
            return root.GetProperty("present").EnumerateArray().Select(item => item.GetString())
                .Where(SpeakingVoiceLibrary.IsSha256).Select(item => item!).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException)
        {
            throw new Audio2FaceHostException("response.invalid", "The host's list of recordings was invalid.");
        }
    }
}
