using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Martlet.Audio.Windows;
using Martlet.Core.Singing;
using Martlet.Providers.Singing;

namespace Martlet.Mcp;

/// <summary>singing_cloud_check: runs Singing's cloud clients (ElevenLabs Music, ElevenLabs Voice Changer, Mureka with its vocal
/// clone, fal.ai MiniMax Music and ACE-Step) against simulated providers in this process that answer as each one's documentation
/// says, and checks what each request sends (address, key header, body fields) and what Martlet makes of the answers: decoding
/// (raw PCM, WAV, and an MP3 Windows encodes and decodes here), Mureka's lyric times, the rate limit as busy, a refused key as
/// unavailable, and putting a cloud song together without a host (not separated, lines spread over the singing). FIXTURE - NOT
/// AI: no real provider is called and nothing costs money.</summary>
internal static class SingingCloudCheck
{
    private sealed record Seen(string Method, string Path, string? Key, string Body);

    /// <summary>A provider that answers by path, and keeps what it was sent.</summary>
    private sealed class Provider(Func<HttpRequestMessage, string, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = request.Content is null ? "" : request.Content is MultipartFormDataContent form
                ? string.Join("|", form.Select(p => p.Headers.ContentDisposition?.Name?.Trim('"') ?? ""))
                : await request.Content.ReadAsStringAsync(token);
            var key = request.Headers.TryGetValues("xi-api-key", out var xi) ? xi.First()
                : request.Headers.Authorization?.ToString();
            Requests.Add(new(request.Method.Method, request.RequestUri!.PathAndQuery, key, body));
            return answer(request, body);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    // A song's worth of sound: a tone with "sung" bursts, as raw 16-bit PCM (mono or stereo).
    private static byte[] Tone(int rate, int channels, double seconds)
    {
        var frames = (int)(rate * seconds);
        var pcm = new byte[frames * channels * 2];
        for (var i = 0; i < frames; i++)
        {
            var t = (double)i / rate;
            var sung = (int)(t * 2) % 2 == 0 ? 0.4 : 0.02;
            var value = (short)(Math.Sin(2 * Math.PI * 330 * t) * sung * short.MaxValue);
            for (var c = 0; c < channels; c++) System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan((i * channels + c) * 2), value);
        }
        return pcm;
    }

    private static SongRequest Request => new()
    {
        Lyrics = "[verse]\nMorning light is on the window\nCoffee steaming by the door\n\n[chorus]\nSing it with me, sing it slowly",
        Style = "acoustic pop, warm female vocal, guitar", VoiceId = "fixture-voice", DurationSeconds = 20, Seed = 7
    };

    internal static async Task<object> RunAsync(CancellationToken token)
    {
        List<object> steps = [];
        var ok = true;
        void Step(string name, bool passed, object detail)
        {
            ok &= passed;
            steps.Add(new { name, passed, detail });
        }
        var mono = new SongTrack(SongTrackKind.Vocals, 48_000, 1, Tone(48_000, 1, 6));
        var recording = mono.ToWave()[..(44 + 48_000 * 2 * 3)];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(recording.AsSpan(4), (uint)(recording.Length - 8));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(recording.AsSpan(40), (uint)(recording.Length - 44));

        // ElevenLabs Music: a composition plan with the lyric sections, raw 48 kHz PCM back.
        var eleven = new Provider((_, _) => Bytes(Tone(48_000, 1, 20)));
        using (var writer = new ElevenLabsSongWriter("fixture-key", handler: eleven))
        {
            var song = await writer.WriteAsync(Request, default, null, token);
            var sent = eleven.Requests.Single();
            var plan = JsonNode.Parse(sent.Body)!;
            var sections = plan["composition_plan"]!["sections"]!.AsArray();
            var mix = SongAudioImport.Decode(song.Mix.Bytes, 2, SongTrackKind.Mix, song.Mix.RawPcmRate);
            Step("ElevenLabs Music: POST /v1/music?output_format=pcm_48000 with the xi-api-key and a music_v1 composition plan of the lyric " +
                "sections (lines and lengths adding up to the song), decoded to 48 kHz stereo",
                sent.Path == "/v1/music?output_format=pcm_48000" && sent.Key == "fixture-key" && plan["model_id"]!.GetValue<string>() == "music_v1" &&
                sections.Count == 2 && sections.Sum(s => s!["duration_ms"]!.GetValue<int>()) == 20_000 &&
                sections[1]!["lines"]!.AsArray().Count == 1 && Math.Abs(mix.Duration.TotalSeconds - 20) < 0.01 && mix.Channels == 2,
                new { sent.Path, sections = sections.Select(s => new { name = s!["section_name"]!.GetValue<string>(), ms = s["duration_ms"]!.GetValue<int>() }),
                    seconds = mix.Duration.TotalSeconds });
        }

        // ElevenLabs Voice Changer: an Instant Voice Clone made once (found again by name), then speech-to-speech as MP3.
        var mp3 = SongAudioImport.VoiceSampleMp3(recording, 6);
        var voices = "{\"voices\":[]}";
        var changer = new Provider((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/v1/voices" => Json(voices),
            "/v1/voices/add" => Json("{\"voice_id\":\"cloned-voice-1\",\"requires_verification\":false}"),
            _ => Bytes(mp3)
        });
        using (var change = new ElevenLabsVoiceChanger("fixture-key", handler: changer))
        {
            var first = await change.ChangeAsync("Martlet 0123456789ab", recording, mono.ToWave(), token);
            voices = "{\"voices\":[{\"name\":\"Martlet 0123456789ab\",\"voice_id\":\"cloned-voice-1\"}]}";
            await change.ChangeAsync("Martlet 0123456789ab", recording, mono.ToWave(), token);
            var decoded = SongAudioImport.Decode(first.Bytes, 1, SongTrackKind.Vocals);
            var fitted = SongVoice.Gate(decoded, mono);
            var paths = changer.Requests.Select(r => r.Path).ToArray();
            Step("ElevenLabs Voice Changer: the voice cloned once (POST /v1/voices/add, then found by name), speech-to-speech on " +
                "/v1/speech-to-speech/{voice} with the vocals as a file and eleven_multilingual_sts_v2; the MP3 answer (encoded and decoded " +
                "by Windows here) fitted to the original vocals' length and phrases",
                paths.Count(p => p == "/v1/voices/add") == 1 &&
                paths.Count(p => p == "/v1/speech-to-speech/cloned-voice-1?output_format=mp3_44100_128") == 2 &&
                changer.Requests.Where(r => r.Path.StartsWith("/v1/speech-to-speech", StringComparison.Ordinal)).All(r => r.Body == "audio|model_id|remove_background_noise") &&
                fitted.Frames == mono.Frames && decoded.Duration.TotalSeconds > 5,
                new { paths, mp3Bytes = mp3.Length, decodedSeconds = Math.Round(decoded.Duration.TotalSeconds, 2) });
        }

        // Mureka: generate, poll the task, lyric times from lyrics_sections, the FLAC song; the voice cloned first in its own voice.
        var polls = 0;
        var flac = Martlet.Core.Audio.FlacCodec.Encode(Tone(44_100, 2, 4), 44_100, 2);
        var mureka = new Provider((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/v1/song/vocal-clone" => Json("{\"vocal_id\":\"vocal-9\"}"),
            "/v1/song/generate" => Json("{\"id\":\"task-1\",\"status\":\"preparing\"}"),
            "/v1/song/query/task-1" => Json(polls++ == 0 ? "{\"id\":\"task-1\",\"status\":\"running\"}" :
                "{\"id\":\"task-1\",\"status\":\"succeeded\",\"model\":\"mureka-9\",\"choices\":[{\"flac_url\":\"https://cdn.fixture/song.flac\"," +
                "\"lyrics_sections\":[{\"section_type\":\"verse\",\"lines\":[{\"start\":1200,\"end\":3000,\"text\":\"Morning light\"}]}," +
                "{\"section_type\":\"chorus\",\"lines\":[{\"start\":3100,\"end\":3900,\"text\":\"Sing it with me\"}]}]}]}"),
            _ => Bytes(flac)
        });
        using (var writer = new MurekaSongWriter("fixture-key", ownVoice: true, handler: mureka, pollInterval: TimeSpan.FromMilliseconds(10)))
        {
            var song = await writer.WriteAsync(Request, mp3, null, token);
            var generate = JsonNode.Parse(mureka.Requests.Single(r => r.Path == "/v1/song/generate").Body)!;
            var mix = SongAudioImport.Decode(song.Mix.Bytes, 2, SongTrackKind.Mix, song.Mix.RawPcmRate);
            Step("Mureka: the voice cloned (POST /v1/song/vocal-clone, multipart file), the song generated with its vocal_id and the lyrics and " +
                "style (Bearer key), polled until succeeded, its FLAC downloaded without the key and decoded to 48 kHz, and its lyric lines' times kept",
                mureka.Requests[0].Path == "/v1/song/vocal-clone" && generate["vocal_id"]!.GetValue<string>() == "vocal-9" &&
                generate["prompt"]!.GetValue<string>() == Request.Style && mureka.Requests.All(r => r.Path.StartsWith("/song.flac", StringComparison.Ordinal)
                    ? r.Key is null : r.Key == "Bearer fixture-key") &&
                song.SungInVoice && song.Lines.Count == 2 && song.Lines[1].Section == "chorus" && song.Lines[0].Start == TimeSpan.FromMilliseconds(1200) &&
                mix.SampleRate == 48_000 && Math.Abs(mix.Duration.TotalSeconds - 4) < 0.05,
                new { requests = mureka.Requests.Select(r => r.Path), lines = song.Lines.Select(l => new { l.Section, start = l.Start.TotalSeconds, l.Text }),
                    seconds = Math.Round(mix.Duration.TotalSeconds, 2), song.Generator });
        }

        // fal.ai: the queue (submit, status until COMPLETED, result), MiniMax's fields, ACE-Step's fields; a WAV song.
        foreach (var model in new[] { SingingCloud.FalMiniMax, SingingCloud.FalAceStep })
        {
            var statusPolls = 0;
            var wav = new SongTrack(SongTrackKind.Mix, 44_100, 2, Tone(44_100, 2, 3)).ToWave();
            var fal = new Provider((request, _) => request.RequestUri!.AbsolutePath switch
            {
                var p when p == "/" + model => Json("{\"request_id\":\"r1\",\"status_url\":\"https://queue.fal.run/x/requests/r1/status\"," +
                    "\"response_url\":\"https://queue.fal.run/x/requests/r1\"}"),
                "/x/requests/r1/status" => Json(statusPolls++ == 0 ? "{\"status\":\"IN_QUEUE\"}" : "{\"status\":\"COMPLETED\"}"),
                "/x/requests/r1" => Json("{\"audio\":{\"url\":\"https://fal.media/out.wav\"}}"),
                _ => Bytes(wav)
            });
            using var writer = new FalSongWriter("fixture-key", model, handler: fal, pollInterval: TimeSpan.FromMilliseconds(10));
            var song = await writer.WriteAsync(Request, default, null, token);
            var input = JsonNode.Parse(fal.Requests[0].Body)!;
            var mix = SongAudioImport.Decode(song.Mix.Bytes, 2, SongTrackKind.Mix, song.Mix.RawPcmRate);
            var fields = model == SingingCloud.FalMiniMax
                ? input["prompt"]?.GetValue<string>() == Request.Lyrics && input["lyrics_prompt"]?.GetValue<string>() == Request.Style
                : input["lyrics"]?.GetValue<string>() == Request.Lyrics && input["tags"]?.GetValue<string>() == Request.Style &&
                  input["duration"]?.GetValue<int>() == 20;
            Step($"fal.ai {model}: submitted to the queue with 'Key' auth and the model's own fields, polled until COMPLETED, the result's " +
                "audio downloaded and decoded",
                fields && fal.Requests[0].Key == "Key fixture-key" && statusPolls == 2 && Math.Abs(mix.Duration.TotalSeconds - 3) < 0.05,
                new { requests = fal.Requests.Select(r => r.Path), seconds = Math.Round(mix.Duration.TotalSeconds, 2) });
        }

        // Refusals: a rate limit is busy (the pool tries the next), a refused key unavailable.
        var limited = new Provider((_, _) => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{\"detail\":\"slow down\"}") });
        var refused = new Provider((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"detail\":\"bad key\"}") });
        string? Code(Provider handler)
        {
            try
            {
                using var writer = new ElevenLabsSongWriter("fixture-key", handler: handler);
                writer.WriteAsync(Request, default, null, token).GetAwaiter().GetResult();
                return null;
            }
            catch (SongException error) { return error.Code; }
        }
        var busy = Code(limited);
        var unavailable = Code(refused);
        Step("A rate limit (429) is singing.busy and a refused key (401) singing.unavailable, so the pool passes the song to the next member",
            busy == SongErrorCodes.Busy && unavailable == SongErrorCodes.Unavailable && SingingPool.Classify(new SongException(busy!, "")) == Core.Cluster.WorkRefusal.Busy,
            new { busy, unavailable });

        // Without a singing host: the cloud mix used as it is, the lines spread over the singing.
        var cloudMix = new SongTrack(SongTrackKind.Mix, 48_000, 2, Tone(48_000, 2, 8));
        var (vocals, backing) = SongVoice.Unseparated(cloudMix);
        var lines = SongVoice.SpreadLines(Request.Lyrics, vocals);
        var rebuilt = true;
        for (var i = 0; i < 48_000 * 8 && rebuilt; i += 997)
        {
            var mid = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(vocals.Pcm16.Span[(2 * i)..]);
            var left = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(backing.Pcm16.Span[(4 * i)..]);
            rebuilt = mid + left == System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(cloudMix.Pcm16.Span[(4 * i)..]);
        }
        Step("Without a singing computer to separate it, a cloud song is used as it is (backing + vocals is the mix exactly) and its lines are " +
            "spread over the singing in order",
            rebuilt && lines.Count == 3 && lines.Zip(lines.Skip(1)).All(p => p.First.Start < p.Second.Start) && lines[2].Section == "chorus",
            new { lines = lines.Select(l => new { start = Math.Round(l.Start.TotalSeconds, 2), l.Section }) });

        return new { ok, fixture = "simulated cloud providers (FIXTURE - NOT AI; no real provider was called)", steps };
    }
}
