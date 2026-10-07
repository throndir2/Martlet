using System.Buffers.Text;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Cluster;
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
/// Windows Credential Manager for each request (as <see cref="HostControl.CheckAsync"/> does). A reply waits for a host busy
/// with another companion PC's reply; with Thinking shared (Devices › Sharing work) it goes to the next paired computer that
/// runs the same model instead (<see cref="WorkSharingRoster"/>). Deep thinking's own route is placed by its broker.</summary>
internal sealed class HostTextClient : IHostTextClient
{
    public async IAsyncEnumerable<string> StreamAsync(HostTextTarget target, TextModelSelection model, BoundedTextInput input,
        TextGenerationLimits limits, CorrelationIds ids, long epoch, DateTimeOffset deadline, GenerationSettings? generation,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (target.RouteId != SelfHostSetup.OllamaRouteId)
        {
            await foreach (var delta in ReplyAsync(target, model, input, limits, ids, epoch, deadline, generation, cancellationToken, true)
                .ConfigureAwait(false))
                yield return delta;
            yield break;
        }
        IReadOnlyList<HostTextTarget> targets = [.. WorkSharingRoster.Order(WorkSharingRoster.DataDirectory, WorkSharingJobs.Thinking,
                HostRoles.Ollama, model.UpstreamModelId, target.HostId)
            .Select(place => place.Host is not { } host || host.HostId == target.HostId ? target
                : WorkSharingRoster.TextTarget(host, target.RouteId) with { Background = target.Background })];
        // Background work on the conversation's route (remembering after a reply) gives way to the live turn.
        await using var deltas = WorkQueue.Shared.StreamAsync(WorkSharingJobs.Thinking, targets, t => t.HostId,
                (t, token) => ReplyAsync(t, model, input, limits, ids, epoch, deadline, generation, token, false), WorkSharingRoster.Classify,
                deadline, null, cancellationToken, target.Background ? WorkPriority.Background : WorkPriority.Live)
            .GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            bool moved;
            // Background work a live turn stopped (this PC's own, or a host's hold) goes on later (HostLiveHolds).
            try { moved = await Guard(() => deltas.MoveNextAsync().AsTask(), cancellationToken).ConfigureAwait(false); }
            catch (WorkPreemptedException)
            {
                HostLiveHolds.Note(target.HostId);
                throw;
            }
            if (!moved) break;
            yield return deltas.Current;
        }
    }

    private async IAsyncEnumerable<string> ReplyAsync(HostTextTarget target, TextModelSelection model, BoundedTextInput input,
        TextGenerationLimits limits, CorrelationIds ids, long epoch, DateTimeOffset deadline, GenerationSettings? generation,
        [EnumeratorCancellation] CancellationToken cancellationToken, bool guarded)
    {
        using var connection = Connect(target);
        var routes = guarded ? await Guard(() => connection.ReadRoutesAsync(cancellationToken), cancellationToken, target.HostId).ConfigureAwait(false)
            : await connection.ReadRoutesAsync(cancellationToken).ConfigureAwait(false);
        // Which graphics cards serve each route, for the live floor (when the host says).
        HostRouteGpus.Note(target.HostId, routes);
        var route = routes.FirstOrDefault(r => r.RouteId == target.RouteId && r.ModelId == model.UpstreamModelId) ??
            throw Failed("reply", ProviderFailureCode.ModelNotFound, target.RouteId == HostRoute.DeepThinkingRouteId
                ? $"the host's Deep thinking role doesn't run model {model.UpstreamModelId}"
                : $"the host offers no Ollama chat route for model {model.UpstreamModelId}");
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
        // The host takes a request for at most its route's longest job; a think (no time limit of its own) asks for that much.
        var longest = DateTimeOffset.UtcNow + route.MaximumDuration - TimeSpan.FromSeconds(5);
        if (deadline > longest) deadline = longest;
        await using var deltas = connection.StreamChatAsync(route, ids, epoch, deadline, input.PersonalityWithNotes, history, input.UserText,
            generation?.Temperature ?? HostTextGenerationStream.Temperature, outputTokens, limits.MaxContextTokens,
            input.Image is { } image ? [image.ToBase64()] : null, generation, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        while (await (guarded ? Guard(() => deltas.MoveNextAsync().AsTask(), cancellationToken, target.HostId) : deltas.MoveNextAsync().AsTask())
            .ConfigureAwait(false))
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

    private static async Task<T> Guard<T>(Func<Task<T>> call, CancellationToken token, string? hostId = null)
    {
        try { return await call().ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (Failure("reply", error) is { } failure)
        {
            // The host keeps its graphics card for a live turn: pool work there waits and goes on later (HostLiveHolds).
            if (hostId is not null && error is Audio2FaceHostException { HeldForLive: true }) HostLiveHolds.Note(hostId);
            throw failure;
        }
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

/// <summary>When each paired host last kept its graphics card for a live conversation turn (its own companion PC's or another's)
/// and refused or stopped this PC's pool work there (job.busy with detail live, job.preempted): that work waits and goes on
/// later instead of failing.</summary>
internal static class HostLiveHolds
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> Last = new(StringComparer.Ordinal);

    internal static void Note(string hostId) => Last[hostId] = System.Diagnostics.Stopwatch.GetTimestamp();

    /// <summary>Whether <paramref name="hostId"/> held its graphics card for a live turn since <paramref name="timestamp"/>
    /// (<see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>).</summary>
    internal static bool Since(string hostId, long timestamp) => Last.TryGetValue(hostId, out var at) && at >= timestamp;
}