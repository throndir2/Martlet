using System.IO;
using System.Net.Http;
using System.Text.Json;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>Transcribes utterances with a paired host's speech-to-text (its stt role: whisper or Parakeet) through its pinned
/// gateway, reading the pairing secret from Windows Credential Manager for each request (as <see cref="HostTextClient"/> does).
/// Devices › Sharing work: when that computer is busy with another companion PC's speech, the utterance goes to the next paired
/// computer that listens, or waits for whichever frees first (<see cref="WorkSharingRoster"/>).</summary>
internal sealed class HostTranscriptionClient : IHostTranscriptionClient
{
    public async Task<string> TranscribeAsync(HostTextTarget target, string modelId, ReadOnlyMemory<byte> pcm16kMono,
        CorrelationIds ids, long epoch, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        IReadOnlyList<(HostTextTarget? Target, string Model, PoolMember? Cloud)> targets = [.. WorkSharingRoster.Places(WorkSharingRoster.DataDirectory,
                WorkSharingJobs.Listening, HostRoles.Stt, null, target.HostId, m => PoolCloud.Usable(WorkSharingRoster.DataDirectory, SetupRole.Stt, m))
            .Select(place => place.Cloud is { } cloud ? (null, cloud.Model!, cloud)
                : place.Host is not { } host || host.HostId == target.HostId ? (target, modelId, (PoolMember?)null)
                : (WorkSharingRoster.TextTarget(host, target.RouteId), place.Model ?? modelId, null))];
        try
        {
            return await WorkQueue.Shared.RunAsync(WorkSharingJobs.Listening, targets, t => t.Target?.HostId ?? t.Cloud!.Key,
                (t, token) => t.Target is { } host
                    ? WorkSharingRoster.WatchedOnce(host.HostId, "listening", OnceAsync(host, t.Model, pcm16kMono, ids, epoch, deadline, token))
                    // A cloud member of the Listening list, in its turn: the members before it are busy or don't answer.
                    : PoolCloud.TranscribeAsync(WorkSharingRoster.DataDirectory!, t.Cloud!, pcm16kMono, ids, epoch, deadline, token),
                WorkSharingRoster.Classify, deadline, null, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (HostTextClient.Failure("listening", error) is { } failure) { throw failure; }
    }

    private static async Task<string> OnceAsync(HostTextTarget target, string modelId, ReadOnlyMemory<byte> pcm16kMono,
        CorrelationIds ids, long epoch, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        using var connection = HostTextClient.Connect(target);
        var routes = await connection.ReadRoutesAsync(cancellationToken).ConfigureAwait(false);
        HostRouteGpus.Note(target.HostId, routes);
        var route = routes.FirstOrDefault(r => r.RouteId == Audio2FaceHostConnection.TranscriptionRouteId && r.ModelId == modelId) ??
            throw HostTextClient.Failed("listening", ProviderFailureCode.ModelNotFound, $"{target.HostId} isn't ready for listening");
        return await connection.TranscribeAsync(route, ids, epoch, deadline, pcm16kMono, cancellationToken).ConfigureAwait(false);
    }
}
