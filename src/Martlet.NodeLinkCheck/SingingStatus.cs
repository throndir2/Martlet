using System.Diagnostics;
using System.Text.Json;
using Martlet.Avatar.Audio2Face.Remote;

namespace Martlet.NodeLinkCheck;

/// <summary>
/// singing_status with a desktop data directory: Singing on every host paired there, read the way the desktop's Singing card
/// reads it, through each host's own gateway (pinned TLS, the pairing secret from Windows Credential Manager, used only to sign
/// the requests): whether the host answers and offers the song route, and its singing service's status (state, engine, voice
/// matches set up there, pinned models and their size and licences, the worker, the queue, the graphics card). Also what this
/// PC's Docker shows of the role: its container, images, models volume and whether a "martlet-host add singing" is running
/// now (a setup in progress). Read-only.
/// </summary>
internal static class SingingStatus
{
    internal static async Task<object> RunAsync(string dataDirectory, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(dataDirectory) || !Directory.Exists(dataDirectory))
            throw new ArgumentException("singing-status needs the absolute path of a Martlet desktop data directory.");
        var hosts = new List<object>();
        foreach (var host in SingingCheck.PairedHosts(dataDirectory))
        {
            try
            {
                using var connection = SingingCheck.Connect(host);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                var routes = await connection.ReadRoutesAsync(timeout.Token);
                var route = routes.FirstOrDefault(r => r.RouteId == Audio2FaceHostConnection.SongRouteId);
                hosts.Add(new
                {
                    host = host.HostId, reachable = true, offersSinging = route is not null, model = route?.ModelId,
                    service = route is null ? null : await ServiceAsync(connection, route, timeout.Token)
                });
            }
            catch (Exception error) when (error is Audio2FaceHostException or HttpRequestException or IOException or
                InvalidOperationException or OperationCanceledException && !token.IsCancellationRequested)
            {
                hosts.Add(new { host = host.HostId, reachable = false, problem = error.Message });
            }
        }
        return new { dataDirectory, hosts, thisPcDocker = await DockerAsync(token) };
    }

    private static async Task<object> ServiceAsync(Audio2FaceHostConnection connection, HostRoute route, CancellationToken token)
    {
        try
        {
            var answer = await connection.SongOperationAsync(route, new Dictionary<string, object> { ["operation"] = "status" }, token);
            if (answer.Count != 1) return new { answered = false, problem = "no status" };
            var status = answer[0];
            JsonElement? Field(string name) => status.TryGetProperty(name, out var value) ? value.Clone() : null;
            var artifacts = status.TryGetProperty("worker", out var worker) && worker.ValueKind == JsonValueKind.Object &&
                worker.TryGetProperty("artifacts", out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().ToArray() : [];
            return new
            {
                answered = true, state = Field("state"), ready = Field("ready"), engine = Field("engine"), error = Field("error"),
                voiceMatches = Field("voice_matches"), qualities = Field("qualities"), queue = Field("queue"), running = Field("running"),
                workerRunning = Field("worker_running"), restarts = Field("restarts"), idleReleaseSeconds = Field("idle_release_seconds"),
                gpu = Field("gpu"),
                sources = worker.ValueKind == JsonValueKind.Object && worker.TryGetProperty("sources", out var sources) ? sources.Clone() : (JsonElement?)null,
                models = artifacts.Length,
                modelBytes = artifacts.Sum(a => a.TryGetProperty("bytes", out var bytes) ? bytes.GetInt64() : 0),
                licenses = artifacts.Select(a => a.TryGetProperty("license_id", out var license) ? license.GetString() : null).Distinct()
            };
        }
        catch (Exception error) when (error is Audio2FaceHostException or HttpRequestException or IOException or JsonException or
            InvalidOperationException)
        {
            return new { answered = false, problem = error.Message };
        }
    }

    /// <summary>What this PC's Docker shows of the singing role (nothing when Docker isn't answering).</summary>
    private static async Task<object> DockerAsync(CancellationToken token)
    {
        var containers = await DockerLinesAsync(token, "ps", "-a", "--filter", "label=com.docker.compose.project=martlet-singing",
            "--format", "{{.Names}}|{{.Image}}|{{.State}}|{{.Status}}");
        if (containers is null) return new { answered = false };
        var images = await DockerLinesAsync(token, "image", "ls", "martlet-singing", "--format", "{{.Repository}}:{{.Tag}}|{{.Size}}|{{.CreatedSince}}") ?? [];
        var volumes = await DockerLinesAsync(token, "volume", "ls", "-q", "--filter", "name=martlet-singing-models") ?? [];
        var commands = await DockerLinesAsync(token, "ps", "--no-trunc", "--format", "{{.Image}}|{{.Command}}|{{.RunningFor}}") ?? [];
        // A martlet-host engine session adding the role (the desktop's Set up, or "martlet-host add singing").
        var setup = commands.Where(c => c.StartsWith("martlet-host:", StringComparison.Ordinal) && c.Contains(" add singing", StringComparison.Ordinal))
            .Select(c => c.Split('|')).Select(p => new { image = p[0], runningFor = p.Length > 2 ? p[2] : null }).FirstOrDefault();
        return new
        {
            answered = true,
            containers = containers.Select(c => c.Split('|')).Select(p => new { name = p[0], image = p[1], state = p[2], status = p[3] }),
            images = images.Select(i => i.Split('|')).Select(p => new { image = p[0], size = p[1], created = p[2] }),
            modelsVolume = volumes.Contains("martlet-singing-models"),
            setupRunning = setup is not null,
            setup
        };
    }

    private static async Task<IReadOnlyList<string>?> DockerLinesAsync(CancellationToken token, params string[] arguments)
    {
        var start = new ProcessStartInfo("docker") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start);
            if (process is null) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0
                ? (await output).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : null;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or OperationCanceledException && !token.IsCancellationRequested)
        {
            return null;
        }
    }
}
