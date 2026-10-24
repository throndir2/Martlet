using System.IO;
using Martlet.Core.Planning;

namespace Martlet.Mcp;

/// <summary>MCP's <c>thinking_speed_check</c>: when Martlet calls Thinking slow (<see cref="ThinkingSpeed"/>), from a data
/// directory's model-speed.json and model-memory.json (Home's warning and Diagnostics › Check your computers use the same rules),
/// and a fixed rehearsal of a model that went from about 2 s to over a minute after part of it left the graphics card.</summary>
internal static class ThinkingSpeedCheck
{
    internal static object Run(string? dataDirectory)
    {
        var speeds = MeasuredFirstWords.Load(dataDirectory);
        var memory = MeasuredModelMemory.Load(dataDirectory);
        var models = speeds.Models.Select(m =>
        {
            var slow = ThinkingSpeed.Slowdown(m);
            return new
            {
                m.Host, m.Model, samples = m.Samples, recentMs = ThinkingSpeed.RecentMs(m.Samples), usualMs = m.UsualMs,
                slow = slow is not null, slowerThanUsual = slow?.SlowerThanUsual == true, warning = slow?.Describe(m.Host)
            };
        }).ToArray();
        var placements = memory.Models.Select(m => new
        {
            m.Host, m.Model, gb = Math.Round(m.Bytes / 1e9, 2), graphicsGb = Math.Round(m.GraphicsBytes / 1e9, 2),
            placement = ThinkingSpeed.Placement(m.Bytes, m.GraphicsBytes).ToString(),
            line = ThinkingSpeed.DescribePlacement(m.Model, m.Host, m.Bytes, m.GraphicsBytes)
        }).ToArray();
        var rehearsal = Rehearse();
        return new
        {
            ok = rehearsal.Ok,
            rules = new
            {
                ThinkingSpeed.RecentCount, ThinkingSpeed.SlowMs, ThinkingSpeed.SlowerFactor, ThinkingSpeed.SlowerByMs,
                ThinkingSpeed.SlowTokensPerSecond
            },
            dataDirectory = new { models, placements },
            rehearsal
        };
    }

    /// <summary>The FIXTURE Thinking route and model <see cref="SeedAsync"/> saves: a custom server on a closed local port, NOT AI.</summary>
    internal const string SeedOrigin = "http://127.0.0.1:9/v1", SeedModel = "lab/slow";

    /// <summary>For checking Home's warning in a real desktop: saves a FIXTURE Thinking route (<see cref="SeedOrigin"/>) in a
    /// disposable <paramref name="dataDirectory"/> when it has none, and model-speed.json with nine fast replies and three that took
    /// over a minute on it. Never Martlet's own data folder.</summary>
    internal static async Task<object> SeedAsync(string? dataDirectory, CancellationToken token)
    {
        if (dataDirectory is not { Length: > 0 } directory || !Path.IsPathFullyQualified(directory) ||
            string.Equals(Path.GetFullPath(directory).TrimEnd('\\'), Path.GetFullPath(Martlet.Core.Settings.SettingsStore.DefaultDataDirectory()).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("seedSlow needs an explicit, disposable dataDirectory, never Martlet's own data folder.");
        Directory.CreateDirectory(directory);
        var store = new Martlet.Core.Settings.SettingsStore(directory);
        var loaded = await store.LoadAsync(token);
        if (loaded.Error is not null) throw new InvalidOperationException(loaded.Error.Summary);
        var settings = loaded.Settings ?? Martlet.Core.Settings.AppSettings.CreateUnconfigured() with
        {
            SchemaVersion = Martlet.Core.Settings.AppSettings.CurrentSchemaVersion,
            Companion = Martlet.Core.Settings.CompanionSettings.Create(), Memory = Martlet.Core.Settings.MemorySettings.Create()
        };
        var route = settings.Setup?.Routes.FirstOrDefault(r => r.Role == Martlet.Core.Settings.SetupRole.Llm);
        var saved = false;
        if (route is null)
        {
            var open = settings.Setup is not null ? settings : settings with
            {
                Setup = new Martlet.Core.Settings.SetupSettings
                {
                    SchemaVersion = Martlet.Core.Settings.SetupSettings.CurrentSchemaVersion, Checkpoint = Martlet.Core.Settings.SetupStep.Choice,
                    Routes = [], PendingRemovals = []
                }
            };
            var next = Martlet.Core.Settings.ChatCompletionsSetup.SelectRoute(open, SeedOrigin, SeedModel);
            var save = await store.SaveAsync(next, loaded.Revision, token);
            if (save.Error is not null) throw new InvalidOperationException(save.Error.Summary);
            saved = true;
            route = next.Setup!.Routes.First(r => r.Role == Martlet.Core.Settings.SetupRole.Llm);
        }
        var list = new MeasuredFirstWords();
        var at = DateTimeOffset.UtcNow.AddMinutes(-30);
        foreach (var ms in new[] { 2_100, 1_900, 2_300, 2_000, 2_200, 1_800, 61_000, 75_000, 88_000 })
            list = list.With(route.Origin, route.ModelId, ms, at = at.AddMinutes(2));
        if (!list.Save(directory)) throw new IOException("model-speed.json couldn't be saved.");
        var measured = list.Find(route.Origin, route.ModelId)!;
        return new
        {
            seeded = true, savedRoute = saved, route = new { route.Origin, route.ModelId }, measured.Samples, measured.UsualMs,
            warning = ThinkingSpeed.Slowdown(measured)?.Describe("this PC"),
            note = "FIXTURE route and times, NOT AI. Start the desktop on this dataDirectory: Home shows HealthIssue-thinking-slow."
        };
    }

    private sealed record Rehearsal(bool Ok, object Steps);

    // FIXTURE times, NOT a real host: nine fast replies, then three slow ones, then fast again; a model split with the processor;
    // and the test reply's judge.
    private static Rehearsal Rehearse()
    {
        const string host = "https://192.0.2.10:9443";
        const string model = "gemma4-12b";
        var at = DateTimeOffset.Parse("2026-10-20T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var list = new MeasuredFirstWords();
        foreach (var ms in new[] { 9_000, 2_100, 1_900, 2_300, 2_000, 2_200, 1_800, 2_400, 2_100 }) list = list.With(host, model, ms, at = at.AddMinutes(1));
        var fast = list.Find(host, model)!;
        var fastSlow = ThinkingSpeed.Slowdown(fast);
        foreach (var ms in new[] { 51_000, 102_000, 113_000 }) list = list.With(host, model, ms, at = at.AddMinutes(1));
        var slowNow = list.Find(host, model)!;
        var slow = ThinkingSpeed.Slowdown(slowNow);
        foreach (var ms in new[] { 2_000, 1_900, 2_200 }) list = list.With(host, model, ms, at = at.AddMinutes(1));
        var recovered = ThinkingSpeed.Slowdown(list.Find(host, model));
        var noBaseline = ThinkingSpeed.Slowdown(new MeasuredFirstWords().With(host, model, 50_000, at).With(host, model, 60_000, at)
            .With(host, model, 70_000, at).Find(host, model));
        var split = ThinkingSpeed.Placement(9_000_000_000, 2_700_000_000);
        var judgedSlow = ThinkingSpeed.Judge(model, "aibox01", 50_600, 3, 2_000);
        var judgedFast = ThinkingSpeed.Judge(model, "aibox01", 1_200, 45, 2_000);
        var ok = fastSlow is null && fast.UsualMs is > 0 and < 3_000 && slow is { SlowerThanUsual: true } && recovered is null &&
            noBaseline is { SlowerThanUsual: false } && split == ModelPlacement.Split && judgedSlow.Slow && !judgedFast.Slow &&
            ThinkingSpeed.SameModel("gemma4:12b", "gemma4-12b") && !ThinkingSpeed.SameModel("gemma4:12b", "gemma4-e4b");
        return new(ok, new
        {
            fixture = "FIXTURE first-word times, NOT a real host",
            fast = new { fast.Samples, fast.UsualMs, slow = fastSlow is not null },
            slowed = new { slowNow.Samples, recentMs = slow?.RecentMs, slow?.UsualMs, warning = slow?.Describe("aibox01") },
            recovered = new { slow = recovered is not null },
            slowWithoutBaseline = noBaseline?.Describe("aibox01"),
            split = new { placement = split.ToString(), line = ThinkingSpeed.DescribePlacement("gemma4:12b", "aibox01", 9_000_000_000, 2_700_000_000) },
            testReply = new
            {
                slow = new { judgedSlow.Slow, judgedSlow.Text },
                fast = new { judgedFast.Slow, judgedFast.Text }
            }
        });
    }
}
