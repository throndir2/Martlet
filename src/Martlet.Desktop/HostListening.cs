using System.IO;
using System.Net.Http;
using System.Text.Json;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
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
        IReadOnlyList<(HostTextTarget Target, string Model)> targets = [.. WorkSharingRoster.Order(WorkSharingRoster.DataDirectory,
                WorkSharingJobs.Listening, HostRoles.Stt, null, target.HostId)
            .Select(place => place.Host is not { } host || host.HostId == target.HostId ? (target, modelId)
                : (WorkSharingRoster.TextTarget(host, target.RouteId), place.Model ?? modelId))];
        try
        {
            return await WorkQueue.Shared.RunAsync(WorkSharingJobs.Listening, targets, t => t.Target.HostId,
                (t, token) => OnceAsync(t.Target, t.Model, pcm16kMono, ids, epoch, deadline, token), WorkSharingRoster.Classify, deadline, null,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (HostTextClient.Failure("listening", error) is { } failure) { throw failure; }
    }

    private static async Task<string> OnceAsync(HostTextTarget target, string modelId, ReadOnlyMemory<byte> pcm16kMono,
        CorrelationIds ids, long epoch, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        using var connection = HostTextClient.Connect(target);
        var routes = await connection.ReadRoutesAsync(cancellationToken).ConfigureAwait(false);
        var route = routes.FirstOrDefault(r => r.RouteId == Audio2FaceHostConnection.TranscriptionRouteId && r.ModelId == modelId) ??
            throw HostTextClient.Failed("listening", ProviderFailureCode.ModelNotFound, $"{target.HostId} isn't ready for listening");
        return await connection.TranscribeAsync(route, ids, epoch, deadline, pcm16kMono, cancellationToken).ConfigureAwait(false);
    }
}
