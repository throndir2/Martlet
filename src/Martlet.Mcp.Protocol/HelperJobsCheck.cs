using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Mcp.Shared;
using Martlet.Providers;

namespace Martlet.Mcp;

/// <summary>helper_jobs_status and helper_jobs_check: where Martlet's helper jobs (remembering and learning names after a reply,
/// naming a character's emotes, finding its touch zones) run. The status reads the desktop's helper-jobs.json: for each kind,
/// whether the last one ran on a Thinking pool member (and which) or on the conversation's own Thinking model after the reply
/// (fallback), how it ended and how long it waited. The check rehearses the desktop's production router (<see cref="HelperJobs"/>)
/// with a fixture pool and fixture answers (NOT AI): no model, network or credentials.</summary>
internal static class HelperJobsCheck
{
    internal static object Status(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, HelperJobs.StatusFile);
        try
        {
            if (!File.Exists(path))
                return new { state = "none", why = "The desktop hasn't run a helper job with this data directory." };
            if (new FileInfo(path).Length > 65_536) return new { state = "unreadable", why = $"{HelperJobs.StatusFile} is too large." };
            return new { state = "loaded", file = JsonNode.Parse(File.ReadAllText(path)) };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new { state = "unreadable", why = error.GetType().Name };
        }
    }

    internal static async Task<object> RunAsync(CancellationToken cancellation)
    {
        var directory = Path.Combine(Path.GetTempPath(), "martlet-helper-jobs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var pool = new FixturePool();
            var busy = false;
            var helpers = new HelperJobs(() => pool, () => Volatile.Read(ref busy), directory);
            var steps = new List<object>();
            async Task Step(string name, HelperJobKind kind, BoundedImage? image, bool expectPool, int busyMs = 0)
            {
                var fellBack = false;
                if (busyMs > 0)
                {
                    Volatile.Write(ref busy, true);
                    _ = Task.Delay(busyMs, cancellation).ContinueWith(_ => Volatile.Write(ref busy, false), TaskScheduler.Default);
                }
                var result = await helpers.RunAsync(kind, "Helper jobs check: " + name,
                    image is null ? HelperCapability.Text : HelperCapability.Vision,
                    () => new BoundedTextInput("fixture text", "fixture instructions", image: image),
                    _ =>
                    {
                        fellBack = true;
                        return Task.FromResult<(string?, string?)>(("fallback answer (FIXTURE - NOT AI)", null));
                    }, cancellation);
                var route = helpers.Last.First(r => r.Kind == kind);
                steps.Add(new
                {
                    step = name, kind = HelperJobs.Name(kind), priority = HelperJobs.PriorityOf(kind).ToString(), route = route.Route,
                    member = route.Member, outcome = route.Outcome, waitedMs = route.WaitedMs, answer = result.Answer,
                    passed = result.Pooled == expectPool && fellBack == !expectPool && (busyMs == 0 || route.WaitedMs >= busyMs / 2)
                });
            }

            // A text member and no vision member: memory and naming go to the pool, touch zones fall back after the "reply".
            pool.Text = true;
            await Step("memory on a free text member", HelperJobKind.Memory, null, expectPool: true);
            await Step("emote naming on a free text member", HelperJobKind.ActionNaming, null, expectPool: true);
            await Step("touch zones with no vision member wait for the reply, then fall back", HelperJobKind.TouchZones,
                Picture(), expectPool: false, busyMs: 300);
            pool.Vision = true;
            await Step("touch zones on a free vision member", HelperJobKind.TouchZones,
                Picture(), expectPool: true);
            pool.Free = false;
            await Step("memory when no member is free falls back", HelperJobKind.Memory, null, expectPool: false);
            var file = Status(directory);
            return new
            {
                passed = steps.All(step => (bool)step.GetType().GetProperty("passed")!.GetValue(step)!),
                steps,
                statusFile = file
            };
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // A tiny PNG-signed picture (its pixels are never read here).
    private static BoundedImage Picture() =>
        new([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0x49, 0x48, 0x44, 0x52, 0, 0, 0, 1], ImageMediaType.Png, 1, 1);

    private sealed class FixturePool : IHelperJobPool
    {
        internal bool Text { get; set; }
        internal bool Vision { get; set; }
        internal bool Free { get; set; } = true;

        public bool Has(HelperCapability capability) => capability == HelperCapability.Vision ? Vision : Text;

        public Task<HelperPoolAnswer?> TryRunAsync(HelperJob job, CancellationToken token) =>
            Task.FromResult(Free && Has(job.Capability)
                ? new HelperPoolAnswer(job.Capability == HelperCapability.Vision ? "fixture-vision-member" : "fixture-text-member",
                    $"pool answer for {job.Kind} (FIXTURE - NOT AI)", null)
                : null);
    }
}
