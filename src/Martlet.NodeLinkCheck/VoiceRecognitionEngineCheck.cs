using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Martlet.HostClient;
using Martlet.Core.Contracts;
using Martlet.Gateway.VoiceRecognition;
using Martlet.Sherpa;

namespace Martlet.NodeLinkCheck;

/// <summary>
/// voice_recognition_engine_check: recognizes who speaks in 16 kHz mono PCM16 clips (each a <c>.pcm</c> file in a folder, in name
/// order) with a live voice recognition service on loopback (the voice-recognition host role's worker, 127.0.0.1:50091) through
/// the production path: the role's relay (Martlet.Gateway.VoiceRecognition, as the Linux host's HostApplication creates it)
/// inside a real gateway on 127.0.0.1 (Kestrel, pinned TLS, pairing) and the desktop's paired client and answer checks
/// (HostVoiceRecognition, SpeakerAnswers), as Companion › People's list does with a computer. Reports the service's own
/// /status, the route, the role's status through the gateway and each clip's analysis and voiceprints. Nothing is kept.
/// </summary>
internal static class VoiceRecognitionEngineCheck
{
    internal static async Task<(bool Ok, object Report)> RunAsync(string endpointText, string clipsDirectory, CancellationToken token)
    {
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttp ||
            !IPAddress.TryParse(endpoint.Host, out var address) || !IPAddress.IsLoopback(address) || endpoint.AbsolutePath != "/")
            throw new ArgumentException("endpoint must be a numeric loopback address such as http://127.0.0.1:50091/.");
        var clips = Directory.GetFiles(clipsDirectory, "*.pcm").Order(StringComparer.Ordinal)
            .Select(file => (Name: Path.GetFileNameWithoutExtension(file), Pcm: File.ReadAllBytes(file))).ToArray();
        if (clips.Length == 0) throw new ArgumentException("The clips folder has no .pcm files.");

        var service = await ServiceStatusAsync(endpoint, token);
        var heard = new List<object>();
        string? failure = null, problem = null, model = null, revision = null, state = null, provider = null;
        double? readyMs = null;
        try
        {
            await using var host = await VoiceEngineCheck.LiveHost.StartAsync(new VoiceRecognitionRelayWorker(endpoint));
            using var connection = await host.PairAsync("voice-recognition-check-desktop", token);
            var route = (await connection.ReadRoutesAsync(token)).Single(r => r.RouteId == HostConnection.VoiceRecognitionRouteId);
            (model, revision) = (route.ModelId, route.ModelRevision);
            // The service listens at once and loads its models after; the desktop asks again until it is ready.
            var waited = Stopwatch.StartNew();
            while (true)
            {
                (state, provider, _) = SpeakerAnswers.Status(await connection.VoiceRecognitionStatusAsync(route, token));
                if (state == "ready" || waited.Elapsed > TimeSpan.FromMinutes(3)) break;
                await Task.Delay(TimeSpan.FromSeconds(2), token);
            }
            readyMs = Math.Round(waited.Elapsed.TotalMilliseconds);
            if (state != "ready") throw new InvalidOperationException("The voice recognition role didn't become ready in 3 minutes.");
            foreach (var (name, pcm) in clips)
            {
                var seconds = pcm.Length / 2.0 / 16_000;
                var watch = Stopwatch.StartNew();
                var answer = await connection.VoiceRecognitionAnalyzeAsync(route, pcm, token);
                var analyzeMs = Math.Round(watch.Elapsed.TotalMilliseconds, 1);
                var analysis = SpeakerAnswers.Analysis(answer, seconds);
                watch.Restart();
                var voiceprint = SpeakerAnswers.Voiceprint(await connection.VoiceRecognitionVoiceprintAsync(route, pcm, token));
                var voiceprintMs = Math.Round(watch.Elapsed.TotalMilliseconds, 1);
                heard.Add(new
                {
                    clip = name, seconds = Math.Round(seconds, 2), voices = analysis.Voices, overlap = analysis.Overlap,
                    speechSeconds = analysis.SpeechSeconds,
                    speakers = analysis.Speakers.Select(s => new
                    {
                        local = s.Local, cleanSeconds = s.CleanSeconds, start = s.Start, end = s.End, voiceprint = s.Voiceprint
                    }),
                    whole = analysis.Whole, voiceprint, analyzeMs, voiceprintMs,
                    hostMs = answer.TryGetProperty("milliseconds", out var ms) && ms.TryGetInt32(out var value) ? value : (int?)null
                });
            }
        }
        catch (HostConnectionException error)
        {
            failure = error.Code;
            problem = error.Message;
        }
        catch (Exception error) when (error is SherpaException or HttpRequestException or IOException or InvalidOperationException or
            ContractException)
        {
            failure = error.GetType().Name;
            problem = error.Message;
        }
        var ok = failure is null && heard.Count == clips.Length;
        return (ok, new
        {
            ok, endpoint = endpoint.ToString(), route = HostConnection.VoiceRecognitionRouteId, model, modelRevision = revision,
            state, provider, readyMs, service, clips = heard, failure, problem
        });
    }

    /// <summary>The service's own GET /status (state, provider, model, threads, runtime versions; nothing secret).</summary>
    private static async Task<object> ServiceStatusAsync(Uri endpoint, CancellationToken token)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var response = await http.GetAsync(new Uri(endpoint, "status"), token);
            if (response.StatusCode != HttpStatusCode.OK) return new { answered = true, httpStatus = (int)response.StatusCode };
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            return new { answered = true, httpStatus = 200, status = document.RootElement.Clone() };
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return new { answered = false, problem = $"{error.GetType().Name}: {error.Message}" };
        }
    }
}
