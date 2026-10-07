namespace Martlet.Gateway;

/// <summary>Which work a route does for the live reply's time to first audio. <see cref="Pool"/> is Deep thinking's (the
/// Thinking pool's) chat route: background work that a live turn stops on a shared graphics card. <see cref="Live"/> is every
/// other route: replies, voices, listening, lip-sync and the rest.</summary>
public enum GatewayLane
{
    Live,
    Pool
}

public sealed partial class GatewayInferenceRoute
{
    private readonly object placement = new();
    private volatile string[] gpus = [];
    private bool frozen;

    /// <summary>The graphics cards the route's worker runs on: NVIDIA GPU UUIDs (GPU-...) where known, else CUDA indexes, or
    /// "cpu" for a worker that runs on the processor. Empty: the host doesn't know, which counts as the whole host.</summary>
    public IReadOnlyList<string> Gpus => gpus;

    /// <summary><see cref="GatewayLane.Pool"/> for Deep thinking's chat route, <see cref="GatewayLane.Live"/> for every other.</summary>
    public GatewayLane Lane => RouteId == Martlet.Core.Settings.SelfHostSetup.DeepThinkingRouteId ? GatewayLane.Pool : GatewayLane.Live;

    /// <summary>Records where the route's worker runs, from the host's configuration (the role's CUDA_VISIBLE_DEVICES or device
    /// setting). Once, before the route is registered; an empty list leaves it unknown (the whole host).</summary>
    public GatewayInferenceRoute PlaceOn(IEnumerable<string> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var placed = GatewayGpus.Validate(devices);
        lock (placement)
        {
            GatewayRules.Require(!frozen && gpus.Length == 0, "worker.invalid");
            gpus = placed;
        }
        return this;
    }

    /// <summary>A registered route's placement no longer changes.</summary>
    internal void Freeze()
    {
        lock (placement) frozen = true;
    }
}

/// <summary>The device IDs of route placement and GPU holds, and when two placements share a device. An empty placement is
/// unknown and shares every device of the host.</summary>
internal static class GatewayGpus
{
    internal const string Cpu = "cpu";
    internal const int MaximumDevices = 8;

    /// <summary>"cpu", a CUDA index (0 to 63) or an NVIDIA GPU or MIG UUID (GPU-..., MIG-...).</summary>
    internal static bool Valid(string? device) => device switch
    {
        null => false,
        Cpu => true,
        _ when device.Length is > 0 and <= 2 && device.All(char.IsAsciiDigit) =>
            (device.Length == 1 || device[0] != '0') && int.Parse(device, System.Globalization.CultureInfo.InvariantCulture) < 64,
        _ => device.Length is > 4 and <= 72 && (device.StartsWith("GPU-", StringComparison.Ordinal) ||
                device.StartsWith("MIG-", StringComparison.Ordinal)) &&
            device.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
    };

    /// <summary>The distinct devices of a placement, at most <see cref="MaximumDevices"/>; "cpu" stands alone.</summary>
    internal static string[] Validate(IEnumerable<string> devices)
    {
        var list = devices.Take(MaximumDevices + 1).ToArray();
        GatewayRules.Require(list.Length <= MaximumDevices && list.All(Valid) &&
            list.Distinct(StringComparer.Ordinal).Count() == list.Length &&
            (list.Length <= 1 || !list.Contains(Cpu, StringComparer.Ordinal)), "worker.invalid");
        return list;
    }

    /// <summary>Whether work placed on <paramref name="a"/> and on <paramref name="b"/> can share a device.</summary>
    internal static bool Overlap(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.Count == 0 || b.Count == 0 || a.Any(device => b.Contains(device, StringComparer.Ordinal));

    /// <summary>A placement in words: "GPU-1a2b", "GPU-1a2b and 1", "the processor" or "the whole host (placement unknown)".</summary>
    internal static string Describe(IReadOnlyList<string> devices) => devices.Count switch
    {
        0 => "the whole host (placement unknown)",
        1 when devices[0] == Cpu => "the processor",
        1 => devices[0],
        _ => string.Join(", ", devices.Take(devices.Count - 1)) + " and " + devices[^1]
    };

    /// <summary>The host role a route serves, in the words the owner sees in Martlet.</summary>
    internal static string RouteName(GatewayInferenceRoute route) => route.Kind switch
    {
        GatewayInferenceKind.OllamaChat => route.Lane == GatewayLane.Pool ? "Thinking pool (Deep thinking)" : "Thinking",
        GatewayInferenceKind.F5Synthesis => "Speaking",
        GatewayInferenceKind.Transcription => "Listening",
        GatewayInferenceKind.Audio2Face => "Lip-sync",
        GatewayInferenceKind.Song => "Singing",
        GatewayInferenceKind.Picture => "Pictures",
        GatewayInferenceKind.Ocr => "Reading",
        _ => "Watching"
    };

    /// <summary>The placement warnings for <paramref name="routes"/>: each pool route that shares a device with live routes, so a
    /// live turn stops its work there, with the advice to pin each Ollama server to its own GPU. Empty when none share.</summary>
    internal static IReadOnlyList<string> Warnings(IReadOnlyList<GatewayInferenceRoute> routes)
    {
        List<string> warnings = [];
        foreach (var pool in routes.Where(r => r.Lane == GatewayLane.Pool))
        {
            var live = routes.Where(r => r.Lane == GatewayLane.Live && Overlap(r.Gpus, pool.Gpus))
                .Select(RouteName).Distinct(StringComparer.Ordinal).ToArray();
            if (live.Length == 0) continue;
            var shared = pool.Gpus.Count == 0 ? "the whole host (its placement is unknown)" : Describe(pool.Gpus);
            var names = live.Length == 1 ? live[0] : string.Join(", ", live.Take(live.Length - 1)) + " and " + live[^1];
            warnings.Add($"{RouteName(pool)} shares {shared} with {names}, so a live turn stops its work there. " +
                "On a host with two or more graphics cards, pin each Ollama server to its own GPU (CUDA_VISIBLE_DEVICES).");
        }
        return warnings;
    }
}
