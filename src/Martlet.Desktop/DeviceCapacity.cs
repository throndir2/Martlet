using System.Globalization;

namespace Martlet.Desktop;

/// <summary>A computer resource the capacity view measures.</summary>
internal enum CapacityResource { GraphicsMemory, Memory, Processor, Disk }

/// <summary>What a device has: its best graphics card's memory, memory, processor threads and free disk (null: not reported).</summary>
internal sealed record CapacitySpecs(double? GraphicsMemoryGb, double? MemoryGb, double? Threads, double? DiskGb)
{
    internal double? Total(CapacityResource resource) => resource switch
    {
        CapacityResource.GraphicsMemory => GraphicsMemoryGb,
        CapacityResource.Memory => MemoryGb,
        CapacityResource.Processor => Threads,
        _ => DiskGb
    };
}

/// <summary>What one component takes while it runs (its planned footprint from the catalog), in GB and processor threads.</summary>
internal sealed record CapacityNeed(double GraphicsMemoryGb, double MemoryGb, double Threads, double DiskGb)
{
    internal static CapacityNeed None { get; } = new(0, 0, 0, 0);

    internal double Of(CapacityResource resource) => resource switch
    {
        CapacityResource.GraphicsMemory => GraphicsMemoryGb,
        CapacityResource.Memory => MemoryGb,
        CapacityResource.Processor => Threads,
        _ => DiskGb
    };

    internal CapacityNeed Plus(CapacityNeed other) =>
        new(GraphicsMemoryGb + other.GraphicsMemoryGb, MemoryGb + other.MemoryGb, Threads + other.Threads, DiskGb + other.DiskGb);
}

/// <summary>A component placed on a device: its key (for automation IDs), plain name and planned footprint: <paramref name="Need"/>
/// is the most it takes at once (what Martlet plans with), <paramref name="UsualNeed"/> what it usually holds (null: the same).</summary>
internal sealed record CapacityComponent(string Key, string Name, CapacityNeed Need, CapacityNeed? UsualNeed = null)
{
    /// <summary>What it usually holds, never more than <see cref="Need"/>; it grows to <see cref="Need"/> while it works hardest.</summary>
    internal double Usual(CapacityResource resource) => Math.Min((UsualNeed ?? Need).Of(resource), Need.Of(resource));
}

/// <summary>What a device itself reports in use right now (null: not reported). Live, unlike the planned footprints.</summary>
internal sealed record CapacityLive(double? GraphicsMemoryUsedGb, double? MemoryUsedGb)
{
    internal double? Used(CapacityResource resource) => resource switch
    {
        CapacityResource.GraphicsMemory => GraphicsMemoryUsedGb,
        CapacityResource.Memory => MemoryUsedGb,
        _ => null
    };
}

/// <summary>One job's part of a resource bar: the most it takes at once and what it usually holds, in percent of the total.</summary>
internal sealed record CapacityShare(CapacityComponent Component, double Percent, double UsualPercent);

/// <summary>One resource bar: the device's total, what its components are planned to take at most (<paramref name="Planned"/>)
/// and usually (<paramref name="UsuallyPlanned"/>, null: the same), what it reports in use now, and the share each component
/// takes (percent of the total). <paramref name="Usable"/> is what Martlet may plan with after leaving room for the system
/// (null: all of it); processor threads may be shared up to <see cref="DeviceCapacity.CpuSharing"/>.</summary>
internal sealed record CapacityBar(CapacityResource Resource, double? Total, double Planned, double? Live,
    IReadOnlyList<CapacityShare> Shares, double? Usable = null, double? UsuallyPlanned = null)
{
    /// <summary>What the jobs usually hold together; they grow to <see cref="Planned"/> while they work hardest.</summary>
    internal double Usual => Math.Min(UsuallyPlanned ?? Planned, Planned);
    internal double? PlannedPercent => Total is > 0 ? DeviceCapacity.Percent(Planned, Total.Value) : null;
    internal double? UsualPercent => Total is > 0 ? DeviceCapacity.Percent(Usual, Total.Value) : null;
    internal double? LivePercent => Total is > 0 && Live is { } live ? DeviceCapacity.Percent(live, Total.Value) : null;
    internal double? Headroom => (Usable ?? Total) is { } room ? Math.Round(Math.Max(0, room - Planned), 1) : null;
    /// <summary>The most the device can give its jobs (processor threads shared up to <see cref="DeviceCapacity.CpuSharing"/>).</summary>
    internal double? Limit => (Usable ?? Total) is { } room ? room * (Resource == CapacityResource.Processor ? DeviceCapacity.CpuSharing : 1) : null;
    /// <summary>Even what the jobs usually hold is more than the device can give.</summary>
    internal bool Over => Limit is { } limit && Usual > limit + 0.05;
    /// <summary>The jobs usually fit, but at their busiest they can need more than the device can give, and slow down or fail.</summary>
    internal bool Tight => !Over && Limit is { } limit && Planned > limit + 0.05;

    /// <summary>"Graphics memory: 12-14 of 24 GB planned (50-58%), 10 GB free. In use now: 9.5 GB (40%)."</summary>
    internal string Text => DeviceCapacity.BarText(this);
}

/// <summary>Another component that also fits in what a device (or the whole network) has left: <paramref name="Noun"/> is
/// what one copy is called ("Deep thinking model") and <paramref name="Detail"/> which one ("Gemma 4 12B"). Only
/// <paramref name="Many"/> components (more models help) say how many fit; others name what would run.</summary>
internal sealed record CapacityFit(string Key, string Noun, string? Detail, int Count, bool Many = true);

/// <summary>One device's resource view: a bar per resource, the components it runs, what is left and what else fits there.</summary>
internal sealed record DeviceCapacityView(string Specs, IReadOnlyList<CapacityBar> Bars, IReadOnlyList<CapacityComponent> Components,
    IReadOnlyList<string> AlsoFits)
{
    internal string Headroom => DeviceCapacity.HeadroomText(Bars);
}

/// <summary>The whole network: which parts run on your computers, online or nowhere, the totals and what else would fit.</summary>
internal sealed record NetworkCapacityView(string Coverage, string Totals, string Fits);

/// <summary>Turns a device's specs and the components placed on it into resource bars, headroom and "also fits here" lines,
/// and a whole network's into a plain-language capacity summary. Reads nothing itself.</summary>
internal static class DeviceCapacity
{
    internal static readonly CapacityResource[] Resources =
        [CapacityResource.GraphicsMemory, CapacityResource.Memory, CapacityResource.Processor, CapacityResource.Disk];

    internal static string Label(CapacityResource resource) => resource switch
    {
        CapacityResource.GraphicsMemory => "Graphics memory",
        CapacityResource.Memory => "Memory",
        CapacityResource.Processor => "Processor",
        _ => "Disk"
    };

    /// <summary>The short word used inside a sentence ("58% graphics memory").</summary>
    internal static string Word(CapacityResource resource) => resource switch
    {
        CapacityResource.GraphicsMemory => "graphics memory",
        CapacityResource.Memory => "memory",
        CapacityResource.Processor => "processor",
        _ => "disk"
    };

    internal static string Key(CapacityResource resource) => resource switch
    {
        CapacityResource.GraphicsMemory => "vram",
        CapacityResource.Memory => "ram",
        CapacityResource.Processor => "cpu",
        _ => "disk"
    };

    internal static double Percent(double part, double total) => total <= 0 ? 0 : Math.Round(part / total * 100, 0);

    private static string Amount(CapacityResource resource, double value) => resource == CapacityResource.Processor
        ? $"{Number(value)} thread{(Math.Abs(value - 1) < 0.05 ? "" : "s")}"
        : $"{Number(value)} GB";

    private static string Number(double value) => value.ToString(value >= 10 || value % 1 == 0 ? "0" : "0.#", CultureInfo.CurrentCulture);

    private static string Pct(double value) => value.ToString("0", CultureInfo.CurrentCulture) + "%";

    /// <summary>"12-14" from what jobs usually hold and the most they take; just "14" when both read the same.</summary>
    private static string Range(double usual, double most)
    {
        var (low, high) = (Number(usual), Number(most));
        return low == high ? high : $"{low}-{high}";
    }

    /// <summary>"12-14 GB", or "14 GB" when both read the same.</summary>
    private static string AmountRange(CapacityResource resource, double usual, double most) => Number(usual) == Number(most)
        ? Amount(resource, most)
        : $"{Range(usual, most)} {(resource == CapacityResource.Processor ? "threads" : "GB")}";

    /// <summary>"50-58%", or "58%" when both read the same.</summary>
    private static string PctRange(double usual, double most) => Pct(usual) == Pct(most) ? Pct(most) : $"{usual.ToString("0", CultureInfo.CurrentCulture)}-{Pct(most)}";

    /// <summary>Processor threads may be shared this much: jobs rarely work at the same moment (the engine's own factor).</summary>
    internal const double CpuSharing = Martlet.Core.Planning.PlacementEngine.CpuOversubscription;

    internal static IReadOnlyList<CapacityBar> Bars(CapacitySpecs specs, IReadOnlyList<CapacityComponent> components, CapacityLive? live = null,
        CapacitySpecs? usable = null) =>
        [.. Resources.Select(resource =>
        {
            var total = specs.Total(resource);
            var shares = components.Where(c => c.Need.Of(resource) > 0)
                .Select(c => new CapacityShare(c, total is > 0 ? Percent(c.Need.Of(resource), total.Value) : 0,
                    total is > 0 ? Percent(c.Usual(resource), total.Value) : 0)).ToList();
            return new CapacityBar(resource, total, Math.Round(components.Sum(c => c.Need.Of(resource)), 2), live?.Used(resource), shares,
                usable?.Total(resource), Math.Round(components.Sum(c => c.Usual(resource)), 2));
        })];

    internal static string BarText(CapacityBar bar)
    {
        var label = Label(bar.Resource);
        if (bar.Total is not { } total || total <= 0)
            return bar.Planned > 0 ? $"{label}: {AmountRange(bar.Resource, bar.Usual, bar.Planned)} planned; this device hasn't reported how much it has."
                : $"{label}: not reported.";
        var room = bar.Usable ?? total;
        var limit = bar.Limit!.Value;
        var planned = $"{label}: {Range(bar.Usual, bar.Planned)} of {Amount(bar.Resource, total)} planned " +
                      $"({PctRange(bar.UsualPercent!.Value, bar.PlannedPercent!.Value)})";
        var text = bar.Planned <= 0
            ? $"{label}: nothing planned of {Amount(bar.Resource, total)}, {(bar.Usable is null ? "all free" : $"{Amount(bar.Resource, room)} free for Martlet")}."
            : bar.Over
                ? $"{planned}, {AmountRange(bar.Resource, bar.Usual - limit, bar.Planned - limit)} more than it can give."
                : bar.Tight
                    ? $"{planned}, tight: at their busiest the jobs can need {Amount(bar.Resource, bar.Planned - limit)} more than it can give, " +
                      "and slow down or fail."
                    : bar.Planned > room
                        ? $"{planned}: shared, as jobs rarely work at the same moment."
                        : $"{planned}, {Amount(bar.Resource, bar.Headroom!.Value)} free{(bar.Usable is null ? "" : " for Martlet")}.";
        if (bar.Live is { } used) text += $" In use now: {Amount(bar.Resource, used)} ({Pct(bar.LivePercent!.Value)}).";
        return text;
    }

    /// <summary>"Voice (Dia): 37-82% graphics memory, 5-6% memory, 2% processor." (the resources it takes on this device: what it
    /// usually holds and the most it takes, when they differ).</summary>
    internal static string ShareText(CapacityComponent component, IReadOnlyList<CapacityBar> bars)
    {
        var parts = bars.Where(b => b.Total is > 0 && Percent(component.Need.Of(b.Resource), b.Total.Value) >= 1)
            .Select(b => $"{PctRange(Percent(component.Usual(b.Resource), b.Total!.Value), Percent(component.Need.Of(b.Resource), b.Total.Value))} " +
                         Word(b.Resource)).ToList();
        if (parts.Count == 0)
            return bars.Any(b => b.Total is > 0) || Resources.All(r => component.Need.Of(r) <= 0)
                ? $"{component.Name}: hardly any of this device's resources."
                : $"{component.Name}: this device hasn't reported its hardware, so its share isn't known.";
        return $"{component.Name}: {string.Join(", ", parts)}.";
    }

    /// <summary>"Left free: 10 GB graphics memory, 58 GB memory, 10 processor threads." from the resources the device reported.</summary>
    internal static string HeadroomText(IReadOnlyList<CapacityBar> bars)
    {
        var known = bars.Where(b => b.Total is > 0).ToList();
        if (known.Count == 0) return "This device hasn't reported its hardware yet, so Martlet can't tell what is left.";
        var over = known.Where(b => b.Over).Select(b => Word(b.Resource)).ToList();
        var tight = known.Where(b => b.Tight).Select(b => Word(b.Resource)).ToList();
        var free = string.Join(", ", known.Where(b => !b.Over && !b.Tight).Select(b => b.Resource == CapacityResource.Processor
            ? $"{Number(b.Headroom!.Value)} processor thread{(Math.Abs(b.Headroom.Value - 1) < 0.05 ? "" : "s")}"
            : $"{Number(b.Headroom!.Value)} GB {Word(b.Resource)}{(b.Resource == CapacityResource.Disk ? " space" : "")}"));
        var text = free.Length > 0 ? $"Left free: {free}." : "";
        if (tight.Count > 0)
            text += (text.Length > 0 ? " " : "") + $"Tight on {string.Join(" and ", tight)}: at their busiest the jobs can need more than it has.";
        if (over.Count > 0) text += (text.Length > 0 ? " " : "") + $"Planned to use more {string.Join(" and ", over)} than it has.";
        return text;
    }

    /// <summary>"Room for 2 more Deep Thinking models here." lines, best first; at most <paramref name="limit"/>.</summary>
    internal static IReadOnlyList<string> AlsoFits(IReadOnlyList<CapacityFit> fits, string where, int limit = 4) =>
        [.. Best(fits, limit).Select(f => $"Room for {Count(f)} {where}.")];

    /// <summary>The ones that fit, in the order given (most important part first).</summary>
    private static IEnumerable<CapacityFit> Best(IReadOnlyList<CapacityFit> fits, int limit) => fits.Where(f => f.Count > 0).Take(limit);

    /// <summary>"another Deep thinking model (Gemma 4 12B)", "2 more Deep thinking models (Gemma 4 12B)" or, for a part one
    /// copy of is enough, what would run ("Voice (Chatterbox Turbo)").</summary>
    private static string Count(CapacityFit fit) => !fit.Many ? fit.Detail ?? fit.Noun
        : (fit.Count == 1 ? $"another {fit.Noun}" : $"{fit.Count} more {Plural(fit.Noun)}") + (fit.Detail is { } detail ? $" ({detail})" : "");

    /// <summary>"Deep Thinking model" → "Deep Thinking models"; a name that already ends in s stays as it is.</summary>
    internal static string Plural(string name) => name.EndsWith('s') ? name : name + "s";

    /// <summary>"Totals across 3 computers: 56 GB graphics memory, 192 GB memory, 64 processor threads."</summary>
    internal static string Totals(IReadOnlyList<CapacitySpecs> devices)
    {
        var parts = new List<string>();
        var vram = devices.Sum(d => d.GraphicsMemoryGb ?? 0);
        var ram = devices.Sum(d => d.MemoryGb ?? 0);
        var threads = devices.Sum(d => d.Threads ?? 0);
        var disk = devices.Sum(d => d.DiskGb ?? 0);
        if (vram > 0) parts.Add($"{Number(vram)} GB graphics memory");
        if (ram > 0) parts.Add($"{Number(ram)} GB memory");
        if (threads > 0) parts.Add($"{Number(threads)} processor threads");
        if (disk > 0) parts.Add($"{Number(disk)} GB free disk");
        var count = $"{devices.Count} computer{(devices.Count == 1 ? "" : "s")}";
        return parts.Count == 0 ? $"Totals across {count}: no hardware reported yet." : $"Totals across {count}: {string.Join(", ", parts)}.";
    }

    /// <summary>"On your computers: Thinking, Listening. Online: Speaking (ElevenLabs). Not set up: Lip-sync." Empty groups are left out.</summary>
    internal static string Coverage(IReadOnlyList<string> local, IReadOnlyList<string> hosted, IReadOnlyList<string> missing)
    {
        var parts = new List<string>();
        if (local.Count > 0) parts.Add($"On your computers: {string.Join(", ", local)}.");
        if (hosted.Count > 0) parts.Add($"Online: {string.Join(", ", hosted)}.");
        parts.Add(missing.Count > 0 ? $"Not set up: {string.Join(", ", missing)}." : "Every part of Martlet is covered.");
        return string.Join(" ", parts);
    }

    /// <summary>"Your computers could also run 2 more Deep Thinking models and another Voice." or that nothing more fits.</summary>
    internal static string NetworkFits(IReadOnlyList<CapacityFit> fits)
    {
        var shown = Best(fits, 4).Select(Count).ToList();
        if (shown.Count == 0) return "Your computers have no room for more right now; add a computer to run more.";
        var list = shown.Count == 1 ? shown[0] : $"{string.Join(", ", shown.Take(shown.Count - 1))} and {shown[^1]}";
        return $"Your computers could also run {list}.";
    }
}
