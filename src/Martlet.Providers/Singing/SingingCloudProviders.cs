using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Martlet.Core.Singing;

namespace Martlet.Providers.Singing;

/// <summary>ElevenLabs Eleven Music (<c>POST /v1/music</c>, docs read 2026-10-15): the song from a <c>music_v1</c> composition plan
/// (one section per lyric section, with its lines and length, the style as global styles), as raw 48 kHz PCM
/// (<c>output_format=pcm_48000</c>). The key goes in the <c>xi-api-key</c> header.</summary>
public sealed class ElevenLabsSongWriter(string key, string model = SingingCloud.ElevenLabsMusicModel, Uri? origin = null,
    HttpMessageHandler? handler = null) : SingingCloudClient(origin ?? new Uri(SingingCloud.ElevenLabsOrigin), key, handler), ICloudSongWriter
{
    protected override string Name => "ElevenLabs";
    protected override void Authorize(HttpRequestMessage request) => request.Headers.TryAddWithoutValidation("xi-api-key", Key);

    public async Task<CloudSong> WriteAsync(SongRequest request, ReadOnlyMemory<byte> voiceRecording, IProgress<double>? progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireKey();
        var styles = new JsonArray(request.Style.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0).Take(20).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
        var sections = new JsonArray();
        foreach (var (name, lines, milliseconds) in CloudLyrics.Sections(request.Lyrics, request.DurationSeconds))
            sections.Add(new JsonObject
            {
                ["section_name"] = name.Length > 100 ? name[..100] : name,
                ["positive_local_styles"] = new JsonArray(),
                ["negative_local_styles"] = new JsonArray(),
                ["duration_ms"] = milliseconds,
                ["lines"] = new JsonArray(lines.Select(l => (JsonNode?)JsonValue.Create(l)).ToArray())
            });
        var body = new JsonObject
        {
            ["model_id"] = model,
            ["composition_plan"] = new JsonObject
            {
                ["positive_global_styles"] = styles,
                ["negative_global_styles"] = new JsonArray(),
                ["sections"] = sections
            }
        };
        if (request.Seed is { } seed) body["seed"] = seed;
        progress?.Report(0.1);
        var pcm = await PostJsonForBytesAsync("/v1/music?output_format=pcm_48000", body, token).ConfigureAwait(false);
        if (pcm.Length < 2) throw Failed("ElevenLabs sent no audio.");
        progress?.Report(1);
        return new CloudSong(new CloudAudio(pcm, 48_000), $"elevenlabs/{model}");
    }
}

/// <summary>ElevenLabs' Voice Changer (<c>POST /v1/speech-to-speech/{voice_id}</c>, docs read 2026-10-15) with an Instant Voice
/// Clone of the voice's recording (<c>POST /v1/voices/add</c>), made once on the owner's account and found again by its name
/// (<c>GET /v1/voices</c>). The changed vocals come back as MP3 (44.1 kHz; PCM at that rate needs a Pro plan).</summary>
public sealed class ElevenLabsVoiceChanger(string key, string model = SingingCloud.ElevenLabsVoiceChangerModel, Uri? origin = null,
    HttpMessageHandler? handler = null) : SingingCloudClient(origin ?? new Uri(SingingCloud.ElevenLabsOrigin), key, handler), ICloudVoiceChanger
{
    protected override string Name => "ElevenLabs";
    protected override void Authorize(HttpRequestMessage request) => request.Headers.TryAddWithoutValidation("xi-api-key", Key);

    public async Task<CloudAudio> ChangeAsync(string voiceName, ReadOnlyMemory<byte> recordingWav, ReadOnlyMemory<byte> vocalsWav, CancellationToken token)
    {
        RequireKey();
        var voice = await VoiceAsync(voiceName, recordingWav, token).ConfigureAwait(false);
        var audio = await SendAsync(() =>
        {
            var form = new MultipartFormDataContent();
            var file = new ReadOnlyMemoryContent(vocalsWav);
            file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(file, "audio", "vocals.wav");
            form.Add(new StringContent(model), "model_id");
            form.Add(new StringContent("false"), "remove_background_noise");
            return new HttpRequestMessage(HttpMethod.Post, new Uri(Origin, $"/v1/speech-to-speech/{Uri.EscapeDataString(voice)}?output_format=mp3_44100_128"))
            {
                Content = form
            };
        }, token).ConfigureAwait(false);
        if (audio.Length < 2) throw Failed("ElevenLabs sent no audio.");
        return new CloudAudio(audio);
    }

    // The voice made from this recording before (by its name), or a new Instant Voice Clone of it.
    private async Task<string> VoiceAsync(string name, ReadOnlyMemory<byte> recordingWav, CancellationToken token)
    {
        var voices = await GetJsonAsync(new Uri(Origin, "/v1/voices"), token).ConfigureAwait(false);
        if (voices["voices"] is JsonArray list)
            foreach (var voice in list)
                if (voice?["name"]?.GetValue<string>() == name && voice["voice_id"]?.GetValue<string>() is { Length: > 0 } id) return id;
        var made = await SendAsync(() =>
        {
            var form = new MultipartFormDataContent();
            form.Add(new StringContent(name), "name");
            var file = new ReadOnlyMemoryContent(recordingWav);
            file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(file, "files", "voice.wav");
            form.Add(new StringContent("false"), "remove_background_noise");
            form.Add(new StringContent("Made by Martlet to sing its songs in this voice."), "description");
            return new HttpRequestMessage(HttpMethod.Post, new Uri(Origin, "/v1/voices/add")) { Content = form };
        }, token).ConfigureAwait(false);
        var answer = JsonNode.Parse(made);
        return answer?["voice_id"]?.GetValue<string>() is { Length: > 0 } voiceId ? voiceId : throw Failed("ElevenLabs made no voice from the recording.");
    }
}

/// <summary>Mureka (<c>POST /v1/song/generate</c>, then <c>GET /v1/song/query/{id}</c> until it succeeds; docs read 2026-10-15). It
/// returns the song's lyric lines with their times. With <paramref name="ownVoice"/>, the voice's sample (an MP3 of 15-30 seconds) is
/// cloned first (<c>POST /v1/song/vocal-clone</c>) and the song is sung in it. Bearer key.</summary>
public sealed class MurekaSongWriter(string key, string model = SingingCloud.MurekaModel, bool ownVoice = false, Uri? origin = null,
    HttpMessageHandler? handler = null, TimeSpan? pollInterval = null) : SingingCloudClient(origin ?? new Uri(SingingCloud.MurekaOrigin), key, handler), ICloudSongWriter
{
    private static readonly Dictionary<string, string> Vocals = new(StringComparer.Ordinal);

    protected override string Name => "Mureka";
    protected override void Authorize(HttpRequestMessage request) => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Key);

    public async Task<CloudSong> WriteAsync(SongRequest request, ReadOnlyMemory<byte> voiceRecording, IProgress<double>? progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireKey();
        var body = new JsonObject
        {
            ["lyrics"] = request.Lyrics.Length > 5000 ? request.Lyrics[..5000] : request.Lyrics,
            ["model"] = model,
            ["n"] = 1,
            ["prompt"] = request.Style.Length > 1024 ? request.Style[..1024] : request.Style
        };
        if (ownVoice)
        {
            if (voiceRecording.IsEmpty) throw new SongException(SongErrorCodes.VoiceMissing, "The voice's recording isn't on this computer.");
            body["vocal_id"] = await VocalAsync(voiceRecording, token).ConfigureAwait(false);
        }
        var task = await PostJsonAsync("/v1/song/generate", body, token).ConfigureAwait(false);
        var id = task["id"]?.ToString() is { Length: > 0 } taskId ? taskId : throw Failed("Mureka gave no task.");
        progress?.Report(0.1);
        var done = await PollAsync(async t =>
        {
            var view = await GetJsonAsync(new Uri(Origin, $"/v1/song/query/{Uri.EscapeDataString(id)}"), t).ConfigureAwait(false);
            var status = view["status"]?.GetValue<string>();
            if (status is "failed" or "timeouted" or "cancelled")
                throw Failed($"Mureka didn't make the song ({status}{(view["failed_reason"]?.ToString() is { Length: > 0 } why ? ": " + why : "")}).");
            progress?.Report(status == "running" ? 0.5 : 0.2);
            return status == "succeeded" ? view : null;
        }, pollInterval ?? TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
        var choice = (done["choices"] as JsonArray)?.FirstOrDefault() ?? throw Failed("Mureka's song had no audio.");
        var url = choice["flac_url"]?.GetValue<string>() ?? choice["wav_url"]?.GetValue<string>() ?? choice["url"]?.GetValue<string>();
        var audio = await DownloadAsync(url, token).ConfigureAwait(false);
        progress?.Report(1);
        return new CloudSong(new CloudAudio(audio), $"mureka/{done["model"]?.GetValue<string>() ?? model}", ownVoice) { Lines = Lines(choice) };
    }

    // Its lyric lines with their times (milliseconds from the start), with each line's section.
    private static IReadOnlyList<SongLyricLine> Lines(JsonNode choice)
    {
        List<SongLyricLine> lines = [];
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        if (choice["lyrics_sections"] is not JsonArray sections) return lines;
        foreach (var section in sections)
        {
            var type = section?["section_type"]?.GetValue<string>() ?? "";
            var count = seen[type] = seen.GetValueOrDefault(type) + 1;
            var name = type.Length == 0 ? "" : count == 1 ? type : $"{type} {count}";
            if (section?["lines"] is not JsonArray each) continue;
            foreach (var line in each)
                if (line?["text"]?.GetValue<string>() is { Length: > 0 } text && line["start"] is { } start)
                    lines.Add(new SongLyricLine(TimeSpan.FromMilliseconds(start.GetValue<double>()),
                        line["end"] is { } end ? TimeSpan.FromMilliseconds(end.GetValue<double>()) : null, text, name));
        }
        return lines;
    }

    private async Task<string> VocalAsync(ReadOnlyMemory<byte> sample, CancellationToken token)
    {
        var name = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(sample.Span));
        lock (Vocals) if (Vocals.TryGetValue(Key.GetHashCode() + name, out var known)) return known;
        var made = await SendAsync(() =>
        {
            var form = new MultipartFormDataContent();
            var file = new ReadOnlyMemoryContent(sample);
            file.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
            form.Add(file, "file", "voice.mp3");
            form.Add(new StringContent("Martlet's speaking voice"), "description");
            return new HttpRequestMessage(HttpMethod.Post, new Uri(Origin, "/v1/song/vocal-clone")) { Content = form };
        }, token).ConfigureAwait(false);
        var id = JsonNode.Parse(made)?["vocal_id"]?.GetValue<string>() ?? throw Failed("Mureka made no voice from the recording.");
        lock (Vocals) Vocals[Key.GetHashCode() + name] = id;
        return id;
    }
}

/// <summary>A music model hosted on fal.ai (its queue: <c>POST https://queue.fal.run/{model}</c>, then its <c>status_url</c> until
/// COMPLETED and its <c>response_url</c>; docs read 2026-10-15). <c>fal-ai/minimax-music/v1.5</c> takes the lyrics as
/// <c>prompt</c> (at most 600 characters) and the style as <c>lyrics_prompt</c>; <c>fal-ai/ace-step</c> takes <c>lyrics</c>,
/// <c>tags</c> and <c>duration</c>. Key header <c>Authorization: Key …</c>.</summary>
public sealed class FalSongWriter(string key, string model = SingingCloud.FalMiniMax, Uri? origin = null, HttpMessageHandler? handler = null,
    TimeSpan? pollInterval = null) : SingingCloudClient(origin ?? new Uri(SingingCloud.FalOrigin), key, handler), ICloudSongWriter
{
    protected override string Name => "fal.ai";
    protected override void Authorize(HttpRequestMessage request) => request.Headers.TryAddWithoutValidation("Authorization", "Key " + Key);

    public async Task<CloudSong> WriteAsync(SongRequest request, ReadOnlyMemory<byte> voiceRecording, IProgress<double>? progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireKey();
        JsonObject input;
        if (model.Contains("minimax", StringComparison.Ordinal))
        {
            var cut = request.Lyrics.Length > 600 ? request.Lyrics.LastIndexOf('\n', 599) : -1;
            var lyrics = request.Lyrics.Length > 600 ? request.Lyrics[..(cut > 0 ? cut : 600)] : request.Lyrics;
            var style = request.Style.Length < 10 ? request.Style + ", song with vocals" : request.Style;
            input = new JsonObject { ["prompt"] = lyrics, ["lyrics_prompt"] = style, ["audio_setting"] = new JsonObject { ["sample_rate"] = 44100, ["format"] = "flac" } };
        }
        else
        {
            input = new JsonObject { ["lyrics"] = request.Lyrics, ["tags"] = request.Style, ["duration"] = request.DurationSeconds };
            if (request.Seed is { } seed) input["seed"] = seed;
        }
        var queued = await PostJsonAsync("/" + model, input, token).ConfigureAwait(false);
        var status = queued["status_url"]?.GetValue<string>();
        var response = queued["response_url"]?.GetValue<string>();
        if (!Uri.TryCreate(status, UriKind.Absolute, out var statusUrl) || !Uri.TryCreate(response, UriKind.Absolute, out var responseUrl) ||
            statusUrl.Host != Origin.Host || responseUrl.Host != Origin.Host)
            throw Failed("fal.ai gave no queue addresses.");
        progress?.Report(0.1);
        await PollAsync(async t =>
        {
            var view = await GetJsonAsync(statusUrl, t).ConfigureAwait(false);
            var state = view["status"]?.GetValue<string>();
            progress?.Report(state == "IN_PROGRESS" ? 0.5 : 0.2);
            return state == "COMPLETED" ? view : null;
        }, pollInterval ?? TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
        var result = await GetJsonAsync(responseUrl, token).ConfigureAwait(false);
        var audio = await DownloadAsync(result["audio"]?["url"]?.GetValue<string>(), token).ConfigureAwait(false);
        progress?.Report(1);
        return new CloudSong(new CloudAudio(audio), $"fal/{model}");
    }
}

