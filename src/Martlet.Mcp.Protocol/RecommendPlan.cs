using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Martlet.Core.Installation;
using Martlet.Core.Planning;

namespace Martlet.Mcp;

/// <summary>recommend_plan: the placement engine (Martlet.Core.Planning, docs/RECOMMENDATIONS.md) on given machines, or on
/// this PC as detected (memory and processor threads from the runtime, NVIDIA cards from nvidia-smi when it is installed),
/// plus the paired hosts a data directory's host-hardware.json reports when dataDirectory is given. Returns the ranking, the
/// plan (assignments with reasons, per-machine usage, dropped parts, suggestions, notes), what a joining machine would
/// change, spare capacity for Deep thinking and optionally the footprint catalog. Pure apart from reading those facts:
/// it saves nothing and contacts nothing.</summary>
internal static class RecommendPlan
{
    private const int MaxMachines = 16, MaxGpus = 8, MaxText = 128;

    internal static object Run(JsonElement arguments, string? dataDirectory)
    {
        var preference = Text(arguments, "preference") switch
        {
            null or "balanced" => HostingPreference.Balanced,
            "local" => HostingPreference.PreferLocal,
            "hosted" => HostingPreference.PreferHosted,
            var other => throw new ArgumentException($"'preference' must be balanced, local or hosted, not '{other}'.")
        };
        var machines = new List<MachineSpecs>();
        var sources = new List<string>();
        if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("machines", out var given))
        {
            if (given.ValueKind != JsonValueKind.Array || given.GetArrayLength() > MaxMachines)
                throw new ArgumentException($"'machines' must be an array of at most {MaxMachines} machines.");
            machines.AddRange(given.EnumerateArray().Select(Machine));
            sources.Add("given");
        }
        if (!machines.Any(m => m.IsPrimary))
        {
            machines.Insert(0, DetectThisPc(Flag(arguments, "games")));
            sources.Add("this PC detected");
        }
        if (dataDirectory is not null)
        {
            var hosts = new HostHardwareStore(dataDirectory).Load().Where(h => machines.All(m => m.Id != h.HostId)).Take(MaxMachines).ToList();
            machines.AddRange(hosts.Select(h => MachineSpecs.FromHostHardware(h)));
            sources.Add($"{hosts.Count} paired host{(hosts.Count == 1 ? "" : "s")} from host-hardware.json");
        }
        if (machines.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() != machines.Count)
            throw new ArgumentException("Machine ids must be unique.");
        var request = new PlanRequest(machines)
        {
            Preference = preference,
            ThinkingFirst = Flag(arguments, "thinkingFirst"),
            ConfiguredProviders = Strings(arguments, "providers"),
            Wanted = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("wanted", out _)
                ? Strings(arguments, "wanted").Select(Component).ToArray() : null,
            Current = Current(arguments)
        };
        var measure = Text(arguments, "mode") switch
        {
            null or "plan" => false,
            "measure" => true,
            var other => throw new ArgumentException($"'mode' must be plan or measure, not '{other}'.")
        };
        var plan = measure ? PlacementEngine.Measure(request) : PlacementEngine.Plan(request);
        var join = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("join", out var joining)
            ? PlacementEngine.SuggestForJoiningMachine(request, Machine(joining)).Select(Suggestion).ToArray()
            : null;
        var affordIds = Strings(arguments, "afford");
        var afford = (affordIds.Count > 0 ? affordIds.Select(id => FootprintCatalog.Default.Find(id)
                ?? throw new ArgumentException($"Unknown option '{id}'.")) : FootprintCatalog.Default.For(PlanComponent.DeepThinking).Where(o => o.IsLocal))
            .Select(o => new
            {
                option = o.Id, network = PlacementEngine.Afford(plan, o),
                perMachine = plan.Machines.ToDictionary(m => m.MachineId, m => PlacementEngine.Afford(plan, o, m.MachineId))
            }).ToArray();
        return new
        {
            mode = measure ? "measure" : "plan",
            preference = Name(plan.Preference),
            sources,
            ranking = ComponentRanking.All.Select(i => new { rank = i.Rank, component = Name(i.Component), necessity = i.Necessity.ToString() }),
            claimOrder = ComponentRanking.ClaimOrder(preference, request.ThinkingFirst).Select(s => s.ToString()),
            assignments = plan.Assignments.Select(a => new
            {
                component = Name(a.Component), role = a.Role.ToString(), option = a.Option.Id, name = a.Option.DisplayName,
                hosting = a.Option.IsLocal ? "local" : "external", provider = a.Option.ProviderId, machine = a.MachineId, gpu = a.GpuIndex,
                why = a.Why
            }),
            machines = plan.Machines.Select(m => new
            {
                id = m.MachineId, name = m.Name, platform = m.Platform, primary = m.IsPrimary,
                gpus = m.Gpus.Select(g => new
                {
                    index = g.Index, name = g.Name, vendor = g.Vendor.ToString(), totalGb = g.TotalGb, unified = g.UnifiedMemory,
                    vram = Gauge(g.Vram)
                }),
                ram = Gauge(m.Ram), cpu = Gauge(m.Cpu), disk = Gauge(m.Disk),
                items = m.Items.Select(i => new
                {
                    component = Name(i.Component), option = i.OptionId, gpu = i.GpuIndex, vramGb = Round(i.Use.VramGb),
                    ramGb = Round(i.Use.RamGb), cpuThreads = Round(i.Use.CpuThreads), diskGb = Round(i.Use.DiskGb)
                })
            }),
            dropped = plan.Dropped.Select(d => new { component = Name(d.Component), reason = d.Reason.ToString(), why = d.Why }),
            suggestions = plan.Suggestions.Select(Suggestion),
            notes = plan.Notes,
            joining = join,
            afford,
            catalog = Flag(arguments, "catalog")
                ? FootprintCatalog.Default.Options.Select(o => new
                {
                    id = o.Id, component = Name(o.Component), name = o.DisplayName, hosting = o.IsLocal ? "local" : "external",
                    gpu = o.Gpu.ToString(), minGpuGb = o.MinGpuGb, gpuGb = Round(o.GpuGb), ramGb = o.Peak.RamGb, cpuThreads = o.Steady.CpuThreads,
                    diskGb = o.Peak.DiskGb, tier = o.QualityTier, firstWordMs = o.FirstWordMs, hears = o.HearsAudio, provider = o.ProviderId,
                    free = o.FreeTier, reliability = o.Reliability.ToString(), evidence = o.Evidence.ToString(), source = o.Source
                }).ToArray()
                : null
        };
    }

    private static object Suggestion(PlanSuggestion s) => new
    {
        kind = s.Kind.ToString(), component = Name(s.Component), machine = s.MachineId, from = s.FromOptionId, to = s.ToOptionId, why = s.Why
    };

    private static object Gauge(ResourceGauge g) => new { capacity = Round(g.Capacity), used = Round(g.Used), percent = g.Percent };

    private static double Round(double value) => Math.Round(value, 2);

    private static string Name(PlanComponent component) => component.ToString();

    private static string Name(HostingPreference preference) => preference switch
    {
        HostingPreference.PreferLocal => "local",
        HostingPreference.PreferHosted => "hosted",
        _ => "balanced"
    };

    private static PlanComponent Component(string name) =>
        Enum.TryParse<PlanComponent>(name, ignoreCase: true, out var component) && Enum.IsDefined(component)
            ? component : throw new ArgumentException($"Unknown component '{name}'.");

    private static IReadOnlyList<CurrentAssignment> Current(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("current", out var current)) return [];
        if (current.ValueKind != JsonValueKind.Array || current.GetArrayLength() > 32)
            throw new ArgumentException("'current' must be an array of at most 32 assignments.");
        return current.EnumerateArray().Select(c => new CurrentAssignment(
            Component(Text(c, "component") ?? throw new ArgumentException("Each current assignment needs a component.")),
            Text(c, "option") ?? throw new ArgumentException("Each current assignment needs an option."),
            Text(c, "machine"))).ToArray();
    }

    private static MachineSpecs Machine(JsonElement m)
    {
        if (m.ValueKind != JsonValueKind.Object) throw new ArgumentException("Each machine must be an object.");
        var id = Text(m, "id") ?? throw new ArgumentException("Each machine needs an id.");
        var gpus = new List<MachineGpu>();
        if (m.TryGetProperty("gpus", out var list))
        {
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > MaxGpus)
                throw new ArgumentException($"'gpus' must be an array of at most {MaxGpus} cards.");
            foreach (var g in list.EnumerateArray())
            {
                if (g.ValueKind != JsonValueKind.Object) throw new ArgumentException("Each GPU must be an object.");
                var name = Text(g, "name") ?? Text(g, "vendor") ?? "GPU";
                var vendor = Text(g, "vendor") is { } v ? MachineGpu.VendorOf(v) : MachineGpu.VendorOf(name);
                gpus.Add(new(name, vendor, Number(g, "vramGb", 0, 512) ?? throw new ArgumentException("Each GPU needs vramGb."))
                {
                    UsedGb = Number(g, "usedGb", 0, 512) ?? 0, UnifiedMemory = Flag(g, "unified")
                });
            }
        }
        return new(id, Text(m, "name") ?? id)
        {
            Gpus = gpus, RamGb = Number(m, "ramGb", 0, 4096) ?? 16, CpuThreads = (int)(Number(m, "cpuThreads", 1, 1024) ?? 8),
            Platform = (Text(m, "platform") ?? "windows").ToLowerInvariant(), Architecture = (Text(m, "architecture") ?? "x64").ToLowerInvariant(),
            DiskFreeGb = Number(m, "diskFreeGb", 0, 1_000_000), IsPrimary = Flag(m, "primary"), KeepGpuForGames = Flag(m, "games"),
            OnBattery = Flag(m, "battery")
        };
    }

    /// <summary>This PC: memory and threads from the runtime, NVIDIA cards (and what they use now) from nvidia-smi.</summary>
    private static MachineSpecs DetectThisPc(bool games)
    {
        var ramGb = Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024d / 1024 / 1024, 1);
        var gpus = new List<MachineGpu>();
        try
        {
            using var smi = Process.Start(new ProcessStartInfo("nvidia-smi", "--query-gpu=name,memory.total,memory.used --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
            });
            if (smi is not null && smi.WaitForExit(5000))
                foreach (var line in smi.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(MaxGpus))
                {
                    var parts = line.Split(',', StringSplitOptions.TrimEntries);
                    if (parts.Length == 3 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var totalMb) &&
                        double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var usedMb))
                        gpus.Add(new(parts[0], GpuVendor.Nvidia, Math.Round(totalMb / 1024, 1)) { UsedGb = Math.Round(usedMb / 1024, 1) });
                }
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // No NVIDIA driver: plan without a card.
        }
        return MachineSpecs.ThisPc(gpus, ramGb, Environment.ProcessorCount,
            OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux",
            System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()) with { KeepGpuForGames = games };
    }

    private static string? Text(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 and <= MaxText } text || text.Any(char.IsControl))
            throw new ArgumentException($"'{property}' must be 1-{MaxText} characters of text.");
        return text;
    }

    private static double? Number(JsonElement element, string property, double min, double max)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && number >= min && number <= max
            ? number : throw new ArgumentException($"'{property}' must be a number from {min} to {max}.");
    }

    private static bool Flag(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False or JsonValueKind.Null => false,
            _ => throw new ArgumentException($"'{property}' must be a boolean.")
        };

    private static IReadOnlyList<string> Strings(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
            return [];
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 32)
            throw new ArgumentException($"'{property}' must be an array of at most 32 strings.");
        return value.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 and <= MaxText } s
            ? s : throw new ArgumentException($"'{property}' must hold strings.")).ToArray();
    }
}
