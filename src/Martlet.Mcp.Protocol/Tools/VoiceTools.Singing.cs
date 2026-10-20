using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Installation;
using Martlet.Doctor;
using Martlet.Mcp.Client;

namespace Martlet.Mcp;

internal sealed partial class McpServer
{
    /// <summary>singing_check: Martlet.NodeLinkCheck's singing-check mode, with the fixture service, a live one on loopback, or
    /// (with dataDirectory) a real paired host through its own gateway.</summary>
    private static async Task<object> SingingCheckAsync(JsonElement arguments, CancellationToken cancellation)
    {
        var endpoint = OptionalString(arguments, "endpoint") ?? "fixture";
        var paired = OptionalString(arguments, "dataDirectory") is { Length: > 0 } pairedData ? pairedData : null;
        if (paired is not null)
        {
            if (!Path.IsPathFullyQualified(paired) || !File.Exists(Path.Combine(paired, "hosts.json")))
                throw new ArgumentException("dataDirectory must be the absolute path of a Martlet desktop data directory paired with a host (it has hosts.json).");
            endpoint = "paired:" + paired;
        }
        else if (endpoint != "fixture" && (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp ||
            !System.Net.IPAddress.TryParse(uri.Host, out var address) || !System.Net.IPAddress.IsLoopback(address)))
            throw new ArgumentException("endpoint must be \"fixture\" or a numeric loopback address such as http://127.0.0.1:50085/.");
        if (paired is null && endpoint != "fixture") endpoint = new Uri(endpoint).GetLeftPart(UriPartial.Authority) + "/";
        var seconds = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("seconds", out var value) &&
            value.TryGetInt32(out var number) ? number : endpoint == "fixture" ? 20 : 30;
        if (seconds is < 15 or > 180) throw new ArgumentException("seconds must be 15 to 180.");
        var quality = OptionalString(arguments, "quality") ?? "high_quality";
        var voiceMatch = OptionalString(arguments, "voiceMatch") ?? "soulx";
        if (quality is not ("fast" or "high_quality" or "xl")) throw new ArgumentException("quality must be fast, high_quality or xl.");
        if (voiceMatch is not ("soulx" or "vevosing" or "vevo2" or "seedvc"))
            throw new ArgumentException("voiceMatch must be soulx, vevosing, vevo2 or seedvc.");
        if (OptionalString(arguments, "model") is { Length: > 0 } model)
        {
            if (Martlet.Core.Singing.SingingModels.Music(model) is null)
                throw new ArgumentException("model must be ace-step-v15, ace-step-v15-xl, heartmula, diffrhythm or yue.");
            if (quality == "xl") quality = "high_quality";
            quality = $"{quality}:{model}";
        }
        string[] command = ["singing-check", endpoint, seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), quality, voiceMatch];
        var save = OptionalString(arguments, "saveDirectory") is { Length: > 0 } folder ? folder : null;
        if (save is not null && !Path.IsPathFullyQualified(save)) throw new ArgumentException("saveDirectory must be an absolute folder.");
        command = [.. command, save ?? "-"];
        var recording = OptionalString(arguments, "voiceRecording") is { Length: > 0 } wav ? wav : null;
        if (recording is not null && (!Path.IsPathFullyQualified(recording) || !recording.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(recording)))
            throw new ArgumentException("voiceRecording must be the absolute path of an existing .wav file.");
        var bpm = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("bpm", out var tempo) && tempo.TryGetInt32(out var beats)
            ? beats : 90;
        if (bpm is not 0 and (< 40 or > 240)) throw new ArgumentException("bpm must be 0 (none) or 40 to 240.");
        var key = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("key", out var keyValue) ? keyValue.GetString() ?? "" : "G major";
        var voiceId = OptionalString(arguments, "voiceId") is { Length: > 0 } id ? id : null;
        var host = OptionalString(arguments, "host") is { Length: > 0 } named ? named : null;
        if (voiceId is not null && (voiceId.Length < 8 || !voiceId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new ArgumentException("voiceId must be a voice ID or a prefix of 8+ of its characters.");
        if (host is not null && !host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            throw new ArgumentException("host must be a paired host's ID.");
        if (paired is null && (voiceId is not null || host is not null))
            throw new ArgumentException("voiceId and host need dataDirectory (a paired desktop data directory).");
        command = [.. command, recording ?? "-", OptionalString(arguments, "voiceTranscript") is { Length: > 0 } transcript ? transcript : "-",
            bpm == 0 ? "-" : bpm.ToString(System.Globalization.CultureInfo.InvariantCulture), key.Length == 0 ? "-" : key,
            voiceId ?? "-", host ?? "-",
            arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("split", out var split) && split.ValueKind == JsonValueKind.True
                ? "split" : "-"];
        return await NodeLinkCheckAsync(TimeSpan.FromMinutes(20), cancellation, command);
    }

    /// <summary>singing_status: the singing service's own /status over loopback (nothing secret: model IDs, licences, sizes,
    /// state, queue, GPU memory) and the Singing card's saved choices in a data directory.</summary>
    private static async Task<object> SingingStatusAsync(JsonElement arguments, CancellationToken cancellation)
    {
        var endpoint = OptionalString(arguments, "endpoint") ?? "http://127.0.0.1:50085/";
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp ||
            !System.Net.IPAddress.TryParse(uri.Host, out var address) || !System.Net.IPAddress.IsLoopback(address))
            throw new ArgumentException("endpoint must be a numeric loopback address such as http://127.0.0.1:50085/.");
        object service;
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var response = await http.GetAsync(new Uri(new Uri(uri.GetLeftPart(UriPartial.Authority) + "/"), "status"), cancellation);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
            var root = document.RootElement;
            JsonElement? Field(string name) => root.TryGetProperty(name, out var field) ? field.Clone() : null;
            var worker = root.TryGetProperty("worker", out var identity) && identity.ValueKind == JsonValueKind.Object ? identity : default;
            var artifacts = worker.ValueKind == JsonValueKind.Object && worker.TryGetProperty("artifacts", out var list) &&
                list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().ToArray() : [];
            service = new
            {
                answered = true, state = Field("state"), ready = Field("ready"), engine = Field("engine"), error = Field("error"),
                qualities = Field("qualities"), voiceMatches = Field("voice_matches"), kinds = Field("kinds"), queue = Field("queue"), running = Field("running"),
                workerRunning = Field("worker_running"), restarts = Field("restarts"), idleReleaseSeconds = Field("idle_release_seconds"),
                gpu = Field("gpu"),
                evidence = worker.ValueKind == JsonValueKind.Object && worker.TryGetProperty("evidence", out var evidence) ? evidence.GetString() : null,
                sources = worker.ValueKind == JsonValueKind.Object && worker.TryGetProperty("sources", out var sources) ? sources.Clone() : (JsonElement?)null,
                models = artifacts.Select(a => new
                {
                    id = a.GetProperty("artifact_id").GetString(), revision = a.GetProperty("revision").GetString(),
                    license = a.GetProperty("license_id").GetString(), bytes = a.GetProperty("bytes").GetInt64()
                }),
                modelBytes = artifacts.Sum(a => a.GetProperty("bytes").GetInt64())
            };
        }
        catch (Exception error) when (error is System.Net.Http.HttpRequestException or TaskCanceledException or JsonException or
            InvalidOperationException or KeyNotFoundException)
        {
            service = new { answered = false, problem = error.Message };
        }
        object? choices = null;
        object? paired = null;
        if (OptionalString(arguments, "dataDirectory") is { } directory)
        {
            // Singing on the hosts this desktop is paired with, through their own gateways (as the card reads it).
            if (Path.IsPathFullyQualified(directory) && File.Exists(Path.Combine(directory, "hosts.json")))
                paired = await NodeLinkCheckAsync(TimeSpan.FromMinutes(2), cancellation, ["singing-status", directory]);
            var path = Path.Combine(directory, "singing.json");
            try
            {
                using var saved = JsonDocument.Parse(File.ReadAllBytes(path));
                var host = saved.RootElement.TryGetProperty("host", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null;
                choices = new
                {
                    // The voice match the page chose before each computer had its own (the voice match list is made from it once);
                    // the quality and the model are now each pool member's settings (pool.members and pool.voiceMatch.members).
                    olderVoiceMatch = saved.RootElement.TryGetProperty("voiceMatch", out var m) ? m.GetString() : null,
                    // The computer the desktop last saw running Singing; set up (SongClient.IsSetUp) while it is still paired.
                    host,
                    setUp = host is not null && PairedHostIds(directory).Contains(host)
                };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                choices = new { olderVoiceMatch = (string?)null, saved = false };
            }
        }
        return new { endpoint = uri.GetLeftPart(UriPartial.Authority) + "/", route = "martlet.gateway.song.v1", port = 50085, service, choices, paired };
    }

    /// <summary>The host IDs in a data directory's hosts.json (nothing secret: pairing secrets stay in Credential Manager), without
    /// hosts friends share with that desktop (they never offer Singing to it).</summary>
    private static HashSet<string> PairedHostIds(string directory)
    {
        try
        {
            using var hosts = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "hosts.json")));
            return hosts.RootElement.TryGetProperty("hosts", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Where(h => !IsSharedHost(h)).Select(h => h.TryGetProperty("pairing", out var pairing) && pairing.TryGetProperty("hostId", out var id)
                    ? id.GetString() : null).OfType<string>().ToHashSet(StringComparer.Ordinal)
                : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return []; }
    }
}
