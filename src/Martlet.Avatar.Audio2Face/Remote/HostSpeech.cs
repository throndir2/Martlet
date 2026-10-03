using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Avatar.Audio2Face.Remote;

/// <summary>The reference recording a host's F5 voice clones: the applied preset snapshot's exact bytes, transcript and
/// revisions (see Martlet.F5's reference preset store). Only its bytes and transcript are sent, never the source path.</summary>
public sealed record HostSpeechReference(Guid PresetId, string ReferenceRevision, string AudioSha256, string Transcript,
    string TranscriptRevision, ReadOnlyMemory<byte> Audio)
{
    public override string ToString() => $"{nameof(HostSpeechReference)} {{ PresetId = {PresetId}, content omitted }}";
}

public sealed partial class Audio2FaceHostConnection
{
    /// <summary>Speaks one reply segment with one of the host's voice engines (its f5 or xtts role, see
    /// <see cref="SpeechEngines"/>) through the gateway relay and yields its contiguous 24 kHz mono PCM16 frames in order
    /// as they arrive (XTTS sends them while it is still generating). Failures throw <see cref="Audio2FaceHostException"/>.</summary>
    public async IAsyncEnumerable<byte[]> StreamSpeechAsync(HostRoute route, CorrelationIds ids, long epoch,
        DateTimeOffset deadline, HostSpeechReference reference, string text,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(reference);
        ids.Validate();
        if (SpeechEngines.ForRoute(route.RouteId) is not { } engine || route.Path != engine.Path)
            throw new ArgumentException("The route is not one of the host's voice engines.", nameof(route));
        int duration;
        try { duration = Martlet.Core.Voices.PcmWaveInfo.Inspect(reference.Audio.Span, 4 * 1024 * 1024).DurationMilliseconds; }
        catch (Exception error) when (error is ContractException or ArgumentException or OverflowException) { duration = 0; }
        if (SpeechEngines.ReferenceProblem(engine, duration) is { } problem)
            throw new Audio2FaceHostException("request.invalid", problem + " Choose another voice in Companion > Voice.");
        var now = clock.GetUtcNow();
        var latest = now + route.MaximumDuration - TimeSpan.FromSeconds(1);
        if (deadline > latest) deadline = latest;
        if (deadline <= now) throw new Audio2FaceHostException("job.deadline", "No time is left for the host's voice.");
        var payload = new Dictionary<string, object>
        {
            ["preset_id"] = reference.PresetId,
            ["reference_revision"] = reference.ReferenceRevision,
            ["reference_audio_sha256"] = reference.AudioSha256,
            ["transcript"] = reference.Transcript,
            ["transcript_revision"] = reference.TranscriptRevision,
            ["reference_audio_base64"] = Convert.ToBase64String(reference.Audio.Span),
            ["chunks"] = new[]
            {
                new Dictionary<string, object> { ["index"] = 0, ["chunk_id"] = "segment-0", ["text"] = ChatText(text) }
            }
        };
        // GPT-SoVITS reads the recording's transcript in the recording's language.
        if (engine == SpeechEngines.GptSovits) payload["reference_language"] = SpeechEngines.ReferenceLanguage(reference.Transcript);
        var body = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["protocol_version"] = new Dictionary<string, int> { ["major"] = 2, ["minor"] = 0 },
            ["route_id"] = route.RouteId, ["contract_id"] = route.ContractId, ["contract_version"] = route.ContractVersion,
            ["destination_id"] = route.DestinationId, ["worker_id"] = route.WorkerId, ["adapter_version"] = route.AdapterVersion,
            ["model_id"] = route.ModelId, ["model_revision"] = route.ModelRevision, ["model_sha256"] = route.ModelSha256,
            ["artifact_identity_sha256"] = route.ArtifactIdentitySha256,
            ["session_id"] = ids.SessionId, ["turn_id"] = ids.TurnId, ["request_id"] = ids.RequestId, ["epoch"] = epoch,
            ["deadline_utc"] = deadline.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["payload"] = payload
        }, ChatJson);
        if (body.Length > route.MaximumRequestBytes)
            throw new Audio2FaceHostException("request.too_large", "The reference recording is too large for the host's voice route.");
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
            throw new Audio2FaceHostException("response.invalid", "The host returned an invalid voice stream.");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        long total = 0, samples = 0;
        while (await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } line)
        {
            total += line.Length + 1;
            if (line.Length > route.MaximumEventBytes * 2 || total > route.MaximumStreamBytes * 2L)
                throw new Audio2FaceHostException("stream.limit", "The host's voice stream exceeded its bounds.");
            var (pcm, terminal) = ParseSpeechEvent(line, ids, samples);
            if (pcm is not null)
            {
                samples += pcm.Length / 2;
                yield return pcm;
            }
            if (terminal) yield break;
        }
        throw new Audio2FaceHostException("stream.truncated", "The host's voice stream ended early.");
    }

    private static (byte[]? Pcm, bool Terminal) ParseSpeechEvent(string line, CorrelationIds ids, long samples)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.GetProperty("request_id").GetGuid() != ids.RequestId) throw new FormatException();
            switch (root.GetProperty("type").GetString())
            {
                case "started":
                case "chunk_completed":
                    return (null, false);
                case "audio_frame":
                    var pcm = Convert.FromBase64String(root.GetProperty("data_base64").GetString() ?? throw new FormatException());
                    if (root.GetProperty("sample_offset").GetInt64() != samples || pcm.Length == 0 ||
                        pcm.Length != root.GetProperty("sample_count").GetInt32() * 2)
                        throw new FormatException();
                    return (pcm, false);
                case "completed":
                    if (root.GetProperty("final_sample_count").GetInt64() != samples) throw new FormatException();
                    return (null, true);
                case "canceled":
                    throw new Audio2FaceHostException("job.canceled", "The host canceled the voice.");
                case "failed":
                    throw Audio2FaceHostClient.Remote(root);
                default:
                    throw new FormatException();
            }
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new Audio2FaceHostException("stream.invalid", "The host's voice stream was invalid.");
        }
    }
}
