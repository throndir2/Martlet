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

/// <summary>A host route's credential reference for a paired host is its pairing credential ID (16 random bytes), shared by
/// every job handed to that host: the device secret stays where pairing saved it in Windows Credential Manager, never copied.</summary>
internal static class HostPairingCredential
{
    internal static Guid ToGuid(string credentialId)
    {
        var bytes = new byte[16];
        try
        {
            if (credentialId.Length == 22 && Base64Url.DecodeFromChars(credentialId, bytes) == 16 &&
                Base64Url.EncodeToString(bytes) == credentialId)
                return new Guid(bytes);
        }
        catch (FormatException) { }
        throw new InvalidOperationException("The saved host pairing is invalid. Pair the host again.");
    }

    internal static string FromGuid(Guid id) => Base64Url.EncodeToString(id.ToByteArray());
}

/// <summary>Streams replies from a paired host's Ollama through its pinned gateway, reading the pairing secret from
/// Windows Credential Manager for each request (as <see cref="HostControl.CheckAsync"/> does).</summary>
internal sealed class HostTextClient : IHostTextClient
{
    public async IAsyncEnumerable<string> StreamAsync(HostTextTarget target, TextModelSelection model, BoundedTextInput input,
        TextGenerationLimits limits, CorrelationIds ids, long epoch, DateTimeOffset deadline, GenerationSettings? generation,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var connection = Connect(target);
        var routes = await Guard(() => connection.ReadRoutesAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
        var route = routes.FirstOrDefault(r => r.RouteId == HostRoute.OllamaChatRouteId && r.ModelId == model.UpstreamModelId) ??
            throw Failed("reply", ProviderFailureCode.ModelNotFound, $"the host offers no Ollama chat route for model {model.UpstreamModelId}");
        var history = input.History.Select(m => new HostChatMessage(m.Role == TextHistoryRole.Assistant, m.Text)).ToArray();
        // A host older than background thinks takes at most 4,096 output tokens and a minute a request (its route says how long):
        // a think there is held to that, and the host should be updated.
        var outputTokens = limits.MaxOutputTokens;
        if (route.MaximumDuration <= LegacyRouteDuration && outputTokens > LegacyOutputTokens)
        {
            outputTokens = LegacyOutputTokens;
            ErrorLog.Info($"Martlet host {target.HostId} takes at most {LegacyOutputTokens:N0} tokens and " +
                $"{route.MaximumDuration.TotalSeconds:0} s a request (an older version); update it to this Martlet version for longer thinks.");
        }
        await using var deltas = connection.StreamChatAsync(route, ids, epoch, deadline, input.PersonalityWithNotes, history, input.UserText,
            generation?.Temperature ?? HostTextGenerationStream.Temperature, outputTokens, limits.MaxContextTokens,
            input.Image is { } image ? [image.ToBase64()] : null, generation, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        while (await Guard(() => deltas.MoveNextAsync().AsTask(), cancellationToken).ConfigureAwait(false))
            yield return deltas.Current;
    }

    /// <summary>What a host's Ollama route took before background thinks: at most this long a request and this many tokens.</summary>
    internal static readonly TimeSpan LegacyRouteDuration = TimeSpan.FromMinutes(2);
    internal const int LegacyOutputTokens = 4_096;

    internal static Audio2FaceHostConnection Connect(HostTextTarget target)
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
        catch (Exception error) when (Failure("reply", error) is { } failure) { throw failure; }
    }

    /// <summary>Maps a host gateway, transport or schema error to a provider failure, recording what the host said locally.</summary>
    internal static HostTextException? Failure(string job, Exception error) => error switch
    {
        Audio2FaceHostException host => Failed(job, Map(host.Code), $"[{host.Code}] {host.Message}"),
        HttpRequestException or IOException => Failed(job, ProviderFailureCode.Network, $"{error.GetType().Name}: {error.Message}"),
        JsonException or FormatException or InvalidOperationException =>
            Failed(job, ProviderFailureCode.ResponseSchema, $"{error.GetType().Name}: {error.Message}"),
        _ => null
    };

    internal static HostTextException Failed(string job, ProviderFailureCode code, string detail)
    {
        ProviderDiagnostics.Report($"Martlet host {job}", code, detail);
        return new(code);
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
