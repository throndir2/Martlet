using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Avatar.Audio2Face.Remote;

/// <summary>The reference recording a host's voice engine clones: the applied preset snapshot's exact bytes, transcript and
/// revisions (see Martlet.F5's reference preset store). Requests name the recording by its SHA-256 and send the transcript;
/// the bytes go only to a host that does not hold them yet. Never the source path. <paramref name="ClipMilliseconds"/> are
/// the lengths of the recordings a voice was made from (null for one), so an engine that learns from several can be checked
/// against them; the host finds where each lies in its own copy of the shared voice list.</summary>
public sealed record HostSpeechReference(Guid PresetId, string ReferenceRevision, string AudioSha256, string Transcript,
    string TranscriptRevision, ReadOnlyMemory<byte> Audio, IReadOnlyList<int>? ClipMilliseconds = null)
{
    public override string ToString() => $"{nameof(HostSpeechReference)} {{ PresetId = {PresetId}, content omitted }}";
}

public sealed partial class Audio2FaceHostConnection
{
    // Hosts that refused a request naming its recording (older than shared speaking voices): send the recording itself.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> RecordingHosts = new(StringComparer.Ordinal);

    /// <summary>Speaks on <paramref name="hostId"/> with the recording itself from the first request: a host a friend shares with
    /// this PC never holds this PC's voices (a recording named only by its SHA-256 answers <c>reference.missing</c> there), so
    /// asking by name first would only add a round trip to every sentence.</summary>
    public static void SendRecordingTo(string hostId) => RecordingHosts[hostId] = true;

    /// <summary>Whether speaking on <paramref name="hostId"/> sends the recording from the first request.</summary>
    public static bool SendsRecordingTo(string hostId) => RecordingHosts.ContainsKey(hostId);

    /// <summary>Whether the last <see cref="StreamSpeechAsync"/> on this connection had to send the recording itself.</summary>
    public bool LastSpeechSentRecording { get; private set; }

    /// <summary>Speaks one reply segment with one of the host's voice engines (its f5 or xtts role, see
    /// <see cref="SpeechEngines"/>) through the gateway relay and yields its contiguous 24 kHz mono PCM16 frames in order
    /// as they arrive (XTTS sends them while it is still generating). The request names the recording by its SHA-256, which
    /// the host keeps from the shared speaking-voice list; only a host that lacks it (<c>reference.missing</c>) or predates
    /// the list gets the recording itself. <paramref name="style"/> (Chatterbox Original's General and Expressive exaggeration
    /// and CFG weight) is sent only to that engine. Failures throw <see cref="Audio2FaceHostException"/>.</summary>
    public async IAsyncEnumerable<byte[]> StreamSpeechAsync(HostRoute route, CorrelationIds ids, long epoch,
        DateTimeOffset deadline, HostSpeechReference reference, string text,
        [EnumeratorCancellation] CancellationToken cancellationToken = default, ChatterboxStyle? style = null)
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
        if (SpeechEngines.ReferenceProblem(engine, duration, reference.ClipMilliseconds) is { } problem)
            throw new Audio2FaceHostException("request.invalid", problem + " Choose another voice in Companion > Voice.");
        var now = clock.GetUtcNow();
        var latest = now + route.MaximumDuration - TimeSpan.FromSeconds(1);
        if (deadline > latest) deadline = latest;
        if (deadline <= now) throw new Audio2FaceHostException("job.deadline", "No time is left for the host's voice.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(deadline - clock.GetUtcNow() + TimeSpan.FromSeconds(1));
        var sendRecording = RecordingHosts.ContainsKey(pairing.HostId);
        // Only Chatterbox Original takes a style; every other route refuses the field.
        var sentStyle = engine == SpeechEngines.ChatterboxOriginal ? style : null;
        HttpResponseMessage response;
        try
        {
            response = await SendSpeechAsync(route, engine, ids, epoch, deadline, reference, text, sendRecording, sentStyle, timeout.Token).ConfigureAwait(false);
        }
        catch (Audio2FaceHostException error) when (!sendRecording && error.Code is "reference.missing" or "request.invalid")
        {
            sendRecording = true;
            response = await SendSpeechAsync(route, engine, ids, epoch, deadline, reference, text, true, sentStyle, timeout.Token).ConfigureAwait(false);
            if (error.Code == "request.invalid") RecordingHosts[pairing.HostId] = true;
        }
        LastSpeechSentRecording = sendRecording;
        using (response)
        {
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
        }
        throw new Audio2FaceHostException("stream.truncated", "The host's voice stream ended early.");
    }

    // Sends the speaking request (with the recording only when sendRecording) and returns the open event stream; a refusal
    // throws the host's failure code.
    private async Task<HttpResponseMessage> SendSpeechAsync(HostRoute route, SpeechEngine engine, CorrelationIds ids, long epoch,
        DateTimeOffset deadline, HostSpeechReference reference, string text, bool sendRecording, ChatterboxStyle? style,
        CancellationToken token)
    {
        var payload = new Dictionary<string, object>
        {
            ["preset_id"] = reference.PresetId,
            ["reference_revision"] = reference.ReferenceRevision,
            ["reference_audio_sha256"] = reference.AudioSha256,
            ["transcript"] = reference.Transcript,
            ["transcript_revision"] = reference.TranscriptRevision,
            ["chunks"] = new[]
            {
                new Dictionary<string, object> { ["index"] = 0, ["chunk_id"] = "segment-0", ["text"] = ChatText(text) }
            }
        };
        if (sendRecording) payload["reference_audio_base64"] = Convert.ToBase64String(reference.Audio.Span);
        // GPT-SoVITS reads the recording's transcript in the recording's language.
        if (engine == SpeechEngines.GptSovits) payload["reference_language"] = SpeechEngines.ReferenceLanguage(reference.Transcript);
        if (style is not null) payload["voice_style"] = style.Wire();
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
        var response = await Audio2FaceHostClient.Send(http, request, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.OK) return response;
        using (response)
        {
            using var failure = await Audio2FaceHostClient.ReadJson(response, 64 * 1024, token).ConfigureAwait(false);
            throw Audio2FaceHostClient.Remote(failure.RootElement);
        }
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
