using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Contracts;
using Martlet.Gateway.Stt;

namespace Martlet.NodeLinkCheck;

/// <summary>
/// listening_engine_check: transcribes 16 kHz mono PCM16 clips (each a <c>.pcm</c> file in a folder, in name order) with a
/// live speech-to-text service on loopback (the stt host role's whisper.cpp or Parakeet on 127.0.0.1:8178) through the
/// production path: the role's relay (Martlet.Gateway.Stt.SttRelayWorker, as the Linux host's HostApplication creates it)
/// inside a real gateway on 127.0.0.1 (Kestrel, pinned TLS, pairing) and the desktop's paired client, as Listening on
/// another computer does. Reports the service's own /status (Martlet's Parakeet service has one; whisper.cpp doesn't), the
/// route's model and engine release, and each clip's transcript and time. Nothing is played, recorded or kept.
/// </summary>
internal static class ListeningEngineCheck
{
    internal static async Task<(bool Ok, object Report)> RunAsync(string endpointText, string? model, string clipsDirectory,
        CancellationToken token)
    {
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttp ||
            !IPAddress.TryParse(endpoint.Host, out var address) || !IPAddress.IsLoopback(address) || endpoint.AbsolutePath != "/")
            throw new ArgumentException("endpoint must be a numeric loopback address such as http://127.0.0.1:8178/.");
        var clips = Directory.GetFiles(clipsDirectory, "*.pcm").Order(StringComparer.Ordinal)
            .Select(file => (Name: Path.GetFileNameWithoutExtension(file), Pcm: File.ReadAllBytes(file))).ToArray();
        if (clips.Length == 0) throw new ArgumentException("The clips folder has no .pcm files.");

        var (status, served) = await StatusAsync(endpoint, token);
        // The relay names the model the host installed; Martlet's Parakeet service says which one it runs.
        var routeModel = model ?? served ?? "small";
        var heard = new List<object>();
        string? failure = null, problem = null, revision = null;
        try
        {
            await using var host = await VoiceEngineCheck.LiveHost.StartAsync(new SttRelayWorker(endpoint, routeModel));
            using var connection = await host.PairAsync("listening-check-desktop", token);
            var route = (await connection.ReadRoutesAsync(token)).Single(r => r.RouteId == Audio2FaceHostConnection.TranscriptionRouteId);
            revision = route.ModelRevision;
            foreach (var (name, pcm) in clips)
            {
                var ids = new CorrelationIds { SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() };
                var watch = Stopwatch.StartNew();
                var text = await connection.TranscribeAsync(route, ids, 0, DateTimeOffset.UtcNow.AddSeconds(60), pcm, token);
                heard.Add(new
                {
                    clip = name, seconds = Math.Round(pcm.Length / 2.0 / Audio2FaceHostConnection.TranscriptionSampleRate, 2), text,
                    transcribeMs = Math.Round(watch.Elapsed.TotalMilliseconds, 1)
                });
            }
        }
        catch (Audio2FaceHostException error)
        {
            failure = error.Code;
            problem = error.Message;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidOperationException or ContractException)
        {
            failure = error.GetType().Name;
            problem = error.Message;
        }
        var ok = failure is null && heard.Count == clips.Length;
        return (ok, new
        {
            ok, endpoint = endpoint.ToString(), route = Audio2FaceHostConnection.TranscriptionRouteId, model = routeModel,
            modelRevision = revision, status, clips = heard, failure, problem
        });
    }

    /// <summary>The service's own GET /status (engine, model, threads, runtime versions; nothing secret) and the model it
    /// names, or that it has none (whisper.cpp).</summary>
    private static async Task<(object Status, string? Model)> StatusAsync(Uri endpoint, CancellationToken token)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var response = await http.GetAsync(new Uri(endpoint, "status"), token);
            if (response.StatusCode != HttpStatusCode.OK)
                return (new { answered = true, httpStatus = (int)response.StatusCode }, null);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var root = document.RootElement.Clone();
            var served = root.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String ? model.GetString() : null;
            return (new { answered = true, httpStatus = 200, service = root }, served);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return (new { answered = false, problem = $"{error.GetType().Name}: {error.Message}" }, null);
        }
    }
}
