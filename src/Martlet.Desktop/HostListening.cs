using System.IO;
using System.Net.Http;
using System.Text.Json;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Contracts;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>Transcribes utterances with a paired host's whisper (its stt role) through its pinned gateway, reading the
/// pairing secret from Windows Credential Manager for each request (as <see cref="HostTextClient"/> does).</summary>
internal sealed class HostTranscriptionClient : IHostTranscriptionClient
{
    public async Task<string> TranscribeAsync(HostTextTarget target, string modelId, ReadOnlyMemory<byte> pcm16kMono,
        CorrelationIds ids, long epoch, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        using var connection = HostTextClient.Connect(target);
        try
        {
            var routes = await connection.ReadRoutesAsync(cancellationToken).ConfigureAwait(false);
            var route = routes.FirstOrDefault(r => r.RouteId == Audio2FaceHostConnection.TranscriptionRouteId && r.ModelId == modelId) ??
                throw HostTextClient.Failed("listening", ProviderFailureCode.ModelNotFound, $"the host offers no transcription route for model {modelId}");
            return await connection.TranscribeAsync(route, ids, epoch, deadline, pcm16kMono, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (HostTextClient.Failure("listening", error) is { } failure) { throw failure; }
    }
}
