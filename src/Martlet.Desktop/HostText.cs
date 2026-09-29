using System.Buffers.Text;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>A gateway route's credential reference for a paired host is its pairing credential ID (16 random bytes): the
/// device secret stays where pairing saved it in Windows Credential Manager, never copied. The LLM route uses the ID's
/// bytes as they are; other roles use a fixed per-role mask, so thinking and speaking handed to the same host keep
/// distinct references (settings never share one credential across role policies) that map back to the same pairing.</summary>
internal static class HostPairingCredential
{
    internal static Guid ToGuid(string credentialId, SetupRole role = SetupRole.Llm)
    {
        var bytes = new byte[16];
        try
        {
            if (credentialId.Length == 22 && Base64Url.DecodeFromChars(credentialId, bytes) == 16 &&
                Base64Url.EncodeToString(bytes) == credentialId)
                return new Guid(Mask(bytes, role));
        }
        catch (FormatException) { }
        throw new InvalidOperationException("The saved host credential reference is invalid; pair again.");
    }

    internal static string FromGuid(Guid id, SetupRole role = SetupRole.Llm) => Base64Url.EncodeToString(Mask(id.ToByteArray(), role));

    private static byte[] Mask(byte[] bytes, SetupRole role)
    {
        var mask = role switch { SetupRole.Llm => (byte)0, SetupRole.Tts => (byte)0x5a, _ => (byte)0xa5 };
        for (var i = 0; i < bytes.Length; i++) bytes[i] ^= mask;
        return bytes;
    }
}

/// <summary>Streams replies from a paired host's Ollama through its pinned gateway, reading the pairing secret from
/// Windows Credential Manager for each request (as <see cref="HostControl.CheckAsync"/> does).</summary>
internal sealed class HostTextClient : IHostTextClient
{
    public async IAsyncEnumerable<string> StreamAsync(HostTextTarget target, TextModelSelection model, BoundedTextInput input,
        TextGenerationLimits limits, CorrelationIds ids, long epoch, DateTimeOffset deadline,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var connection = Connect(target);
        var routes = await Guard(() => connection.ReadRoutesAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
        var route = routes.FirstOrDefault(r => r.RouteId == HostRoute.OllamaChatRouteId && r.ModelId == model.UpstreamModelId) ??
            throw new HostTextException(ProviderFailureCode.ModelNotFound);
        var history = input.History.Select(m => new HostChatMessage(m.Role == TextHistoryRole.Assistant, m.Text)).ToArray();
        await using var deltas = connection.StreamChatAsync(route, ids, epoch, deadline, input.Personality, history, input.UserText,
            HostTextGenerationStream.Temperature, limits.MaxOutputTokens, limits.MaxContextTokens, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        while (await Guard(() => deltas.MoveNextAsync().AsTask(), cancellationToken).ConfigureAwait(false))
            yield return deltas.Current;
    }

    private static Audio2FaceHostConnection Connect(HostTextTarget target)
    {
        var credentialId = HostPairingCredential.FromGuid(target.CredentialId);
        using var read = new WindowsCredentialStore().ReadAvatarHostSecret(target.HostId, credentialId);
        if (read.Error != CredentialError.None || read.Secret is null)
            throw new HostTextException(ProviderFailureCode.CredentialUnavailable);
        Audio2FaceHostConnection? connection = null;
        try
        {
            read.Secret.Use(secret => connection = new Audio2FaceHostConnection(new Audio2FaceHostPairing
            {
                Origin = target.Origin, HostId = target.HostId, SpkiFingerprint = target.SpkiFingerprint,
                DeviceId = target.DeviceId, CredentialId = credentialId
            }, secret));
        }
        catch (Audio2FaceHostException) { throw new HostTextException(ProviderFailureCode.CredentialUnavailable); }
        return connection!;
    }

    private static async Task<T> Guard<T>(Func<Task<T>> call, CancellationToken token)
    {
        try { return await call().ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Audio2FaceHostException error) { throw new HostTextException(Map(error.Code)); }
        catch (Exception error) when (error is HttpRequestException or IOException) { throw new HostTextException(ProviderFailureCode.Network); }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException)
        {
            throw new HostTextException(ProviderFailureCode.ResponseSchema);
        }
    }

    internal static ProviderFailureCode Map(string code) => code switch
    {
        "host.unreachable" or "host.redirect" => ProviderFailureCode.Network,
        "action.denied" or "auth.role" => ProviderFailureCode.PermissionDenied,
        _ when code.StartsWith("auth.", StringComparison.Ordinal) || code == "pairing.invalid" => ProviderFailureCode.Authentication,
        "worker.unavailable" => ProviderFailureCode.ModelNotFound,
        "job.deadline" => ProviderFailureCode.DeadlineExceeded,
        "request.too_large" => ProviderFailureCode.InputLimit,
        "request.invalid" => ProviderFailureCode.RequestRejected,
        "stream.limit" => ProviderFailureCode.ResponseTooLarge,
        "stream.truncated" => ProviderFailureCode.ResponseTruncated,
        "stream.invalid" or "response.invalid" => ProviderFailureCode.ResponseSchema,
        _ => ProviderFailureCode.Server
    };
}
