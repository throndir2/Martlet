using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Martlet.Core.Contracts;
using Martlet.Core.Singing;

namespace Martlet.Avatar.Audio2Face.Remote;

public sealed partial class Audio2FaceHostConnection
{
    public const string SongRouteId = "martlet.gateway.song.v1";
    public const string SongPath = "/martlet/v1/inference/song";
    /// <summary>PCM fetched per <c>result</c> request (25 s of 48 kHz stereo; the gateway's page bound).</summary>
    public const int SongPageBytes = 4_800_000;

    /// <summary>Sends one operation on the host's song jobs (its singing role) through the gateway relay and returns the JSON
    /// object of each text event. Gateway and transport failures throw <see cref="Audio2FaceHostException"/>.</summary>
    public async Task<IReadOnlyList<JsonElement>> SongOperationAsync(HostRoute route, IReadOnlyDictionary<string, object> payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(payload);
        if (route.RouteId != SongRouteId || route.Path != SongPath)
            throw new ArgumentException("The route is not the host's singing.", nameof(route));
        var ids = new CorrelationIds { SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() };
        var now = clock.GetUtcNow();
        var deadline = now + TimeSpan.FromSeconds(Math.Min(50, route.MaximumDuration.TotalSeconds - 1));
        var body = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["protocol_version"] = new Dictionary<string, int> { ["major"] = 2, ["minor"] = 0 },
            ["route_id"] = route.RouteId, ["contract_id"] = route.ContractId, ["contract_version"] = route.ContractVersion,
            ["destination_id"] = route.DestinationId, ["worker_id"] = route.WorkerId, ["adapter_version"] = route.AdapterVersion,
            ["model_id"] = route.ModelId, ["model_revision"] = route.ModelRevision, ["model_sha256"] = route.ModelSha256,
            ["artifact_identity_sha256"] = route.ArtifactIdentitySha256,
            ["session_id"] = ids.SessionId, ["turn_id"] = ids.TurnId, ["request_id"] = ids.RequestId, ["epoch"] = 0,
            ["deadline_utc"] = deadline.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["payload"] = payload
        });
        if (body.Length > route.MaximumRequestBytes)
            throw new Audio2FaceHostException("request.too_large", "The song request is too large for the host's singing route.");
        using var request = new HttpRequestMessage(HttpMethod.Post, pairing.Origin + route.Path)
        {
            Content = Audio2FaceHostClient.JsonContent(body)
        };
        Sign(request, body);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(deadline - clock.GetUtcNow() + TimeSpan.FromSeconds(2));
        using var response = await Audio2FaceHostClient.Send(http, request, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            using var failure = await Audio2FaceHostClient.ReadJson(response, 64 * 1024, timeout.Token).ConfigureAwait(false);
            throw Audio2FaceHostClient.Remote(failure.RootElement);
        }
        if (response.Content.Headers.ContentType?.MediaType != "application/x-ndjson")
            throw new Audio2FaceHostException("response.invalid", "The host returned an invalid singing stream.");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        var objects = new List<JsonElement>();
        var total = 0L;
        while (await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } line)
        {
            total += line.Length + 1;
            if (line.Length > route.MaximumEventBytes * 2 || total > route.MaximumStreamBytes)
                throw new Audio2FaceHostException("stream.limit", "The host's singing stream exceeded its bounds.");
            var (text, terminal) = ParseChatEvent(line, ids);
            if (text is not null)
            {
                try
                {
                    using var document = JsonDocument.Parse(text);
                    if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
                    objects.Add(document.RootElement.Clone());
                }
                catch (JsonException)
                {
                    throw new Audio2FaceHostException("stream.invalid", "The host's singing stream was invalid.");
                }
            }
            if (terminal) return objects;
        }
        throw new Audio2FaceHostException("stream.truncated", "The host's singing stream ended early.");
    }

    /// <summary>
    /// Makes one song with the host's singing role: starts the job (naming the voice by its shared-library ID; when the host
    /// lacks that voice's recording, sends <paramref name="recording"/> once), polls it every <paramref name="pollInterval"/>
    /// reporting each stage, then fetches the mix, vocals and backing in pages. Cancelling cancels the job on the host.
    /// </summary>
    public async Task<SongResult> MakeSongAsync(HostRoute route, SongRequest song, Func<CancellationToken, Task<byte[]?>>? recording,
        IProgress<SongProgress>? progress, CancellationToken cancellationToken, TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(song);
        song.Validate();
        var start = new Dictionary<string, object>
        {
            ["operation"] = "start", ["lyrics"] = song.Lyrics, ["style"] = song.Style, ["duration_seconds"] = song.DurationSeconds,
            ["language"] = song.Language, ["quality"] = song.Quality == SongQuality.HighQuality ? "high_quality" : "fast",
            ["voice_match"] = song.VoiceMatch == SongVoiceMatch.VevoSing ? "vevosing" : "soulx", ["voice_id"] = song.VoiceId
        };
        if (song.Bpm is { } bpm) start["bpm"] = bpm;
        if (song.Key is { } key) start["key"] = key;
        if (song.Seed is { } seed) start["seed"] = seed;
        IReadOnlyList<JsonElement> started;
        try
        {
            started = await SongCallAsync(route, start, cancellationToken).ConfigureAwait(false);
        }
        catch (SongException error) when (error.InnerException is Audio2FaceHostException { Code: "reference.missing" } &&
            recording is not null)
        {
            var bytes = await recording(cancellationToken).ConfigureAwait(false) ??
                throw new SongException(SongErrorCodes.VoiceMissing, "The voice's recording is not on this computer.", error);
            start["reference_audio_base64"] = Convert.ToBase64String(bytes);
            started = await SongCallAsync(route, start, cancellationToken).ConfigureAwait(false);
        }
        var view = Single(started);
        ThrowIfError(view);
        var jobId = view.GetProperty("job_id").GetString() ?? throw Invalid();
        var interval = pollInterval ?? TimeSpan.FromSeconds(1);
        SongProgress? last = null;
        void Report(SongProgress value)
        {
            if (last is not null && last.Stage == value.Stage && Math.Abs(last.Fraction - value.Fraction) < 0.01 &&
                last.QueuePosition == value.QueuePosition)
                return;
            last = value;
            progress?.Report(value);
        }
        JsonElement result;
        try
        {
            while (true)
            {
                var state = view.GetProperty("state").GetString();
                if (state != "completed") Report(Progress(view));
                if (state == "completed")
                {
                    result = view.GetProperty("result");
                    break;
                }
                if (state == "canceled") throw new OperationCanceledException("The song was canceled on the host.");
                if (state is "failed" or "unknown") ThrowIfError(view, fallback: true);
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                view = Single(await SongCallAsync(route, Job("status", jobId), cancellationToken).ConfigureAwait(false));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await SongCallAsync(route, Job("cancel", jobId), cancel.Token).ConfigureAwait(false); }
            catch (Exception error) when (error is SongException or HttpRequestException or IOException or OperationCanceledException) { }
            throw;
        }

        var delivering = System.Diagnostics.Stopwatch.StartNew();
        Report(new SongProgress(SongStage.Delivering, 0.95));
        var frames = result.GetProperty("frames").GetInt64();
        var mix = await TrackAsync(route, jobId, "mix", 2, frames, cancellationToken).ConfigureAwait(false);
        Report(new SongProgress(SongStage.Delivering, 0.97));
        var vocals = await TrackAsync(route, jobId, "vocals", 1, frames, cancellationToken).ConfigureAwait(false);
        var backing = await TrackAsync(route, jobId, "backing", 2, frames, cancellationToken).ConfigureAwait(false);
        var made = ReadResult(jobId, song, result, mix, vocals, backing);
        var timings = made.StageTimings.ToList();
        timings.Add(new SongStageTiming(SongStage.Delivering, delivering.Elapsed));
        Report(new SongProgress(SongStage.Completed, 1));
        return made with { StageTimings = timings };
    }

    private async Task<IReadOnlyList<JsonElement>> SongCallAsync(HostRoute route, IReadOnlyDictionary<string, object> payload,
        CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await SongOperationAsync(route, payload, token).ConfigureAwait(false);
            }
            // The gateway runs one request per route at a time: another computer's poll, or a request just stopped, can hold
            // it for a moment.
            catch (Audio2FaceHostException error) when (error.Code == "job.busy" && attempt < 40)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false);
            }
            catch (Audio2FaceHostException error)
            {
                throw new SongException(error.Code switch
                {
                    "voice.missing" => SongErrorCodes.VoiceMissing,
                    "reference.missing" => SongErrorCodes.VoiceMissing,
                    "request.invalid" or "request.too_large" => SongErrorCodes.RequestInvalid,
                    "job.busy" => SongErrorCodes.Busy,
                    "worker.unavailable" or "worker.quarantined" or "host.unreachable" or "auth.role" or "action.denied" =>
                        SongErrorCodes.Unavailable,
                    _ when error.Code.StartsWith("auth.", StringComparison.Ordinal) => SongErrorCodes.Unavailable,
                    _ => SongErrorCodes.Failed
                }, error.Message, error);
            }
        }
    }

    private async Task<byte[]> TrackAsync(HostRoute route, string jobId, string track, int channels, long frames,
        CancellationToken token)
    {
        var frameBytes = 2 * channels;
        var pcm = new byte[checked(frames * frameBytes)];
        var offset = 0L;
        while (offset < frames)
        {
            var page = await SongCallAsync(route, new Dictionary<string, object>
            {
                ["operation"] = "result", ["job_id"] = jobId, ["track"] = track, ["offset_frames"] = offset,
                ["maximum_frames"] = SongPageBytes / frameBytes
            }, token).ConfigureAwait(false);
            var received = 0L;
            foreach (var part in page)
            {
                ThrowIfError(part);
                if (part.GetProperty("track").GetString() != track || part.GetProperty("channels").GetInt32() != channels ||
                    part.GetProperty("sample_rate").GetInt32() != SongTrack.SampleRateHz ||
                    part.GetProperty("total_frames").GetInt64() != frames ||
                    part.GetProperty("offset_frames").GetInt64() != offset + received)
                    throw Invalid();
                var data = Convert.FromBase64String(part.GetProperty("pcm_base64").GetString() ?? "");
                if (data.Length != part.GetProperty("frames").GetInt64() * frameBytes ||
                    (offset + received) * frameBytes + data.Length > pcm.Length)
                    throw Invalid();
                data.CopyTo(pcm, (offset + received) * frameBytes);
                received += data.Length / frameBytes;
            }
            if (received == 0) throw Invalid();
            offset += received;
        }
        return pcm;
    }

    private static SongResult ReadResult(string jobId, SongRequest song, JsonElement meta, byte[] mix, byte[] vocals, byte[] backing)
    {
        var engine = meta.GetProperty("engine");
        static IReadOnlyList<TimeSpan> Times(JsonElement owner, string name) =>
            owner.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(t => TimeSpan.FromSeconds(t.GetDouble())).ToArray()
                : [];
        var lines = new List<SongLyricLine>();
        if (meta.TryGetProperty("lyrics", out var lyricList) && lyricList.ValueKind == JsonValueKind.Array)
            foreach (var line in lyricList.EnumerateArray())
                lines.Add(new SongLyricLine(TimeSpan.FromSeconds(line.GetProperty("start").GetDouble()),
                    line.TryGetProperty("end", out var end) && end.ValueKind == JsonValueKind.Number
                        ? TimeSpan.FromSeconds(end.GetDouble()) : null,
                    line.GetProperty("text").GetString() ?? "",
                    line.TryGetProperty("section", out var section) ? section.GetString() ?? "" : ""));
        var timings = new List<SongStageTiming>();
        if (meta.TryGetProperty("timings", out var timingList) && timingList.ValueKind == JsonValueKind.Array)
            foreach (var timing in timingList.EnumerateArray())
                if (Stage(timing.GetProperty("stage").GetString()) is { } stage)
                    timings.Add(new SongStageTiming(stage, TimeSpan.FromSeconds(timing.GetProperty("seconds").GetDouble())));
        var words = new List<SongLyricWord>();
        if (meta.TryGetProperty("words", out var wordList) && wordList.ValueKind == JsonValueKind.Array)
            foreach (var word in wordList.EnumerateArray())
                words.Add(new SongLyricWord(TimeSpan.FromSeconds(word.GetProperty("start").GetDouble()),
                    TimeSpan.FromSeconds(word.GetProperty("end").GetDouble()), word.GetProperty("text").GetString() ?? "",
                    word.GetProperty("line").GetInt32()));
        double? Number(string name) => meta.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble() : null;
        return new SongResult
        {
            JobId = jobId,
            VoiceId = song.VoiceId,
            Mix = new SongTrack(SongTrackKind.Mix, SongTrack.SampleRateHz, 2, mix),
            Vocals = new SongTrack(SongTrackKind.Vocals, SongTrack.SampleRateHz, 1, vocals),
            Backing = new SongTrack(SongTrackKind.Backing, SongTrack.SampleRateHz, 2, backing),
            Engine = new SongEngineIdentity(engine.GetProperty("generator").GetString() ?? "",
                engine.GetProperty("separator").GetString() ?? "", engine.GetProperty("converter").GetString() ?? "",
                engine.GetProperty("quality").GetString() == "high_quality" ? SongQuality.HighQuality : SongQuality.Fast,
                engine.GetProperty("voice_match").GetString() == "vevosing" ? SongVoiceMatch.VevoSing : SongVoiceMatch.SoulX,
                engine.GetProperty("fixture").GetBoolean()),
            Seed = meta.GetProperty("seed").GetInt64(),
            Bpm = Number("bpm"),
            Key = meta.TryGetProperty("key", out var keyValue) && keyValue.ValueKind == JsonValueKind.String ? keyValue.GetString() : null,
            BeatsPerBar = Number("beats_per_bar") is { } perBar ? (int)perBar : 4,
            Beats = Times(meta, "beats"),
            Downbeats = Times(meta, "downbeats"),
            LyricTimestamps = lines,
            Words = words,
            WordTimingSource = meta.TryGetProperty("word_timing_source", out var source) && source.ValueKind == JsonValueKind.String
                ? source.GetString() ?? "" : "",
            StageTimings = timings
        };
    }

    private static SongStage? Stage(string? name) => name switch
    {
        "queued" => SongStage.Queued,
        "loading" => SongStage.Loading,
        "writing_music" => SongStage.WritingMusic,
        "separating" => SongStage.Separating,
        "matching_voice" => SongStage.MatchingVoice,
        "mixing" => SongStage.Mixing,
        "aligning" => SongStage.Aligning,
        "delivering" => SongStage.Delivering,
        "completed" or "total" => SongStage.Completed,
        _ => null
    };

    private static SongProgress Progress(JsonElement view)
    {
        var stage = Stage(view.TryGetProperty("stage", out var s) ? s.GetString() : null) ?? SongStage.Queued;
        if (stage == SongStage.Completed) stage = SongStage.Mixing;
        var fraction = view.TryGetProperty("fraction", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetDouble() : 0;
        int? position = view.TryGetProperty("queue_position", out var q) && q.ValueKind == JsonValueKind.Number ? q.GetInt32() : null;
        return new SongProgress(stage, Math.Clamp(fraction * 0.95, 0, 0.95), position);
    }

    private static void ThrowIfError(JsonElement view, bool fallback = false)
    {
        if (view.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var code = error.TryGetProperty("code", out var c) ? c.GetString() : null;
            var summary = error.TryGetProperty("summary", out var m) ? m.GetString() : null;
            throw new SongException(code switch
            {
                "singing.busy" => SongErrorCodes.Busy,
                "singing.unavailable" => SongErrorCodes.Unavailable,
                "singing.voice_match_unavailable" => SongErrorCodes.VoiceMatchUnavailable,
                "voice.missing" => SongErrorCodes.VoiceMissing,
                "request.invalid" => SongErrorCodes.RequestInvalid,
                "song.timeout" => SongErrorCodes.TimedOut,
                _ => SongErrorCodes.Failed
            }, summary ?? "The song failed on the singing computer.");
        }
        if (fallback) throw new SongException(SongErrorCodes.Failed, "The song failed on the singing computer.");
    }

    private static JsonElement Single(IReadOnlyList<JsonElement> objects) =>
        objects.Count == 1 ? objects[0] : throw Invalid();

    private static Dictionary<string, object> Job(string operation, string jobId) =>
        new() { ["operation"] = operation, ["job_id"] = jobId };

    private static SongException Invalid() =>
        new(SongErrorCodes.Failed, "The singing computer returned an invalid answer.");
}
