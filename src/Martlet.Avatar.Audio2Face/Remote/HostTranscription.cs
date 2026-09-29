using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Martlet.Core.Contracts;

namespace Martlet.Avatar.Audio2Face.Remote;

public sealed partial class Audio2FaceHostConnection
{
    public const string TranscriptionRouteId = "martlet.gateway.transcription.v1";
    public const string TranscriptionPath = "/martlet/v1/inference/transcription";
    public const int TranscriptionSampleRate = 16_000;

    /// <summary>Transcribes one 16 kHz mono PCM16 utterance with the host's own speech-to-text model (its stt role) through
    /// the gateway relay and returns the final text (empty when no speech was recognized). Failures throw
    /// <see cref="Audio2FaceHostException"/> with the gateway's code.</summary>
    public async Task<string> TranscribeAsync(HostRoute route, CorrelationIds ids, long epoch, DateTimeOffset deadline,
        ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(ids);
        ids.Validate();
        if (route.RouteId != TranscriptionRouteId || route.Path != TranscriptionPath)
            throw new ArgumentException("The route is not the host's speech-to-text.", nameof(route));
        if (pcm.Length is 0 || pcm.Length % 2 != 0 || pcm.Length > route.MaximumInputBytes)
            throw new Audio2FaceHostException("request.too_large", "The utterance is too long for the host's speech-to-text route.");
        var now = clock.GetUtcNow();
        var latest = now + route.MaximumDuration - TimeSpan.FromSeconds(1);
        if (deadline > latest) deadline = latest;
        if (deadline <= now) throw new Audio2FaceHostException("job.deadline", "No time is left for the host's transcription.");
        var body = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["protocol_version"] = new Dictionary<string, int> { ["major"] = 2, ["minor"] = 0 },
            ["route_id"] = route.RouteId, ["contract_id"] = route.ContractId, ["contract_version"] = route.ContractVersion,
            ["destination_id"] = route.DestinationId, ["worker_id"] = route.WorkerId, ["adapter_version"] = route.AdapterVersion,
            ["model_id"] = route.ModelId, ["model_revision"] = route.ModelRevision, ["model_sha256"] = route.ModelSha256,
            ["artifact_identity_sha256"] = route.ArtifactIdentitySha256,
            ["session_id"] = ids.SessionId, ["turn_id"] = ids.TurnId, ["request_id"] = ids.RequestId, ["epoch"] = epoch,
            ["deadline_utc"] = deadline.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["payload"] = new Dictionary<string, object>
            {
                ["sample_rate"] = TranscriptionSampleRate, ["pcm_base64"] = Convert.ToBase64String(pcm.Span)
            }
        });
        if (body.Length > route.MaximumRequestBytes)
            throw new Audio2FaceHostException("request.too_large", "The utterance is too long for the host's speech-to-text route.");
        using var request = new HttpRequestMessage(HttpMethod.Post, pairing.Origin + route.Path)
        {
            Content = Audio2FaceHostClient.JsonContent(body)
        };
        Sign(request, body);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(deadline - clock.GetUtcNow() + TimeSpan.FromSeconds(1));
        using var response = await Audio2FaceHostClient.Send(http, request, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            using var failure = await Audio2FaceHostClient.ReadJson(response, 64 * 1024, timeout.Token).ConfigureAwait(false);
            throw Audio2FaceHostClient.Remote(failure.RootElement);
        }
        if (response.Content.Headers.ContentType?.MediaType != "application/x-ndjson")
            throw new Audio2FaceHostException("response.invalid", "The host returned an invalid transcription stream.");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        var text = new StringBuilder();
        var total = 0L;
        while (await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } line)
        {
            total += line.Length + 1;
            if (line.Length > route.MaximumEventBytes * 2 || total > route.MaximumStreamBytes)
                throw new Audio2FaceHostException("stream.limit", "The host's transcription stream exceeded its bounds.");
            var (delta, terminal) = ParseChatEvent(line, ids);
            if (delta is not null) text.Append(delta);
            if (terminal) return text.ToString().Trim();
        }
        throw new Audio2FaceHostException("stream.truncated", "The host's transcription stream ended early.");
    }
}
