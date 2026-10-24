using System.Globalization;

namespace Martlet.Core.Planning;

/// <summary>A Thinking model that answers much later than it should on one server: <see cref="RecentMs"/> is the middle of its
/// newest replies' first words, <see cref="UsualMs"/> its usual time there (null when Martlet hasn't seen it fast yet), and
/// <see cref="SlowerThanUsual"/> says whether it got slower than before or is simply slow.</summary>
public sealed record ThinkingSlowdown(string Host, string Model, int RecentMs, int? UsualMs, bool SlowerThanUsual)
{
    /// <summary>One plain sentence for Home, Diagnostics and the log.</summary>
    public string Describe(string where) => SlowerThanUsual
        ? $"{Model} on {where} now takes about {ThinkingSpeed.Seconds(RecentMs)} to its first words; it usually takes " +
          $"{ThinkingSpeed.Seconds(UsualMs!.Value)}."
        : $"{Model} on {where} takes about {ThinkingSpeed.Seconds(RecentMs)} to its first words.";
}

/// <summary>How a loaded model sits on a host's memory, from the host's report (<c>/api/ps</c>): all on the graphics card, part
/// of it in the processor's memory (much slower), or none on the card.</summary>
public enum ModelPlacement { GraphicsCard, Split, Processor }

/// <summary>When a Thinking model counts as slow, the one place these rules live (docs/DIAGNOSTICS.md, "Check your computers"):
/// Home's warning, the Diagnostics check, the desktop log and MCP's <c>thinking_speed_check</c> all use it.</summary>
public static class ThinkingSpeed
{
    /// <summary>How many newest replies make the recent time (their middle, so one slow first load doesn't count).</summary>
    public const int RecentCount = 3;
    /// <summary>First words later than this are slow, whatever the model did before.</summary>
    public const int SlowMs = 15_000;
    /// <summary>The recent time is slower than usual when it is at least this many times the usual time...</summary>
    public const double SlowerFactor = 3;
    /// <summary>...and at least this much later, so 0.4 s becoming 1.3 s isn't a warning.</summary>
    public const int SlowerByMs = 4_000;
    /// <summary>A test reply that writes fewer tokens a second than this runs mostly on the processor or a busy card.</summary>
    public const double SlowTokensPerSecond = 6;

    /// <summary>The middle of the newest <see cref="RecentCount"/> samples, or null when there are fewer.</summary>
    public static int? RecentMs(IReadOnlyList<int> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count < RecentCount) return null;
        return samples.TakeLast(RecentCount).Order().ElementAt(RecentCount / 2);
    }

    /// <summary>Whether <paramref name="measured"/>'s newest replies are slow (null when they aren't or there are too few).</summary>
    public static ThinkingSlowdown? Slowdown(MeasuredFirstWord? measured)
    {
        if (measured is null || RecentMs(measured.Samples) is not { } recent) return null;
        if (measured.UsualMs is { } usual && recent >= usual * SlowerFactor && recent - usual >= SlowerByMs)
            return new(measured.Host, measured.Model, recent, usual, true);
        return recent >= SlowMs ? new(measured.Host, measured.Model, recent, measured.UsualMs, false) : null;
    }

    /// <summary>Where a loaded model of <paramref name="bytes"/> sits with <paramref name="graphicsBytes"/> of it on the card.</summary>
    public static ModelPlacement Placement(long bytes, long graphicsBytes) =>
        graphicsBytes >= bytes ? ModelPlacement.GraphicsCard : graphicsBytes <= 0 ? ModelPlacement.Processor : ModelPlacement.Split;

    /// <summary>One plain sentence on where <paramref name="model"/> sits, for Home, Diagnostics and the log.</summary>
    public static string DescribePlacement(string model, string where, long bytes, long graphicsBytes) => Placement(bytes, graphicsBytes) switch
    {
        ModelPlacement.GraphicsCard => $"{model} on {where} is all on the graphics card ({Gb(bytes)}).",
        ModelPlacement.Split => $"Only {Gb(graphicsBytes)} of {model}'s {Gb(bytes)} on {where} is on the graphics card; the rest " +
            "runs on the processor, which makes replies much slower.",
        _ => $"{model} on {where} runs on the processor ({Gb(bytes)}), not on a graphics card, so replies are slow."
    };

    /// <summary>What one test reply showed: its first words after <paramref name="firstWordsMs"/> and about
    /// <paramref name="tokensPerSecond"/> tokens a second after that (null when it wrote too little to tell), against the usual
    /// first words <paramref name="usualMs"/>. Slow is true when either is too slow.</summary>
    public static (bool Slow, string Text) Judge(string model, string where, int firstWordsMs, double? tokensPerSecond, int? usualMs)
    {
        var slowStart = firstWordsMs >= SlowMs || usualMs is { } usual && firstWordsMs >= usual * SlowerFactor && firstWordsMs - usual >= SlowerByMs;
        var slowWriting = tokensPerSecond is < SlowTokensPerSecond;
        var text = $"{model} on {where}: first words after {Seconds(firstWordsMs)}" +
            (tokensPerSecond is { } rate ? $", then about {rate.ToString("0", CultureInfo.InvariantCulture)} tokens a second" : "") +
            (usualMs is { } u ? $" (usually {Seconds(u)} to its first words)" : "") + ".";
        if (slowWriting) text += " That writing speed means the model runs at least partly on the processor, or its graphics card is busy.";
        else if (slowStart) text += " That is slow: check what else uses the host's graphics card.";
        return (slowStart || slowWriting, text);
    }

    /// <summary>Whether two names are one model: a host route names gemma4:12b as gemma4-12b, and no tag means ":latest".</summary>
    public static bool SameModel(string? a, string? b) => a is not null && b is not null &&
        string.Equals(Plain(a), Plain(b), StringComparison.OrdinalIgnoreCase);

    private static string Plain(string name) =>
        (name.EndsWith(":latest", StringComparison.OrdinalIgnoreCase) ? name[..^":latest".Length] : name).Replace(':', '-');

    /// <summary>What <paramref name="host"/> (any form of its origin) last reported about where <paramref name="model"/> sits.</summary>
    public static MeasuredModelUse? Placed(MeasuredModelMemory memory, string? host, string? model)
    {
        ArgumentNullException.ThrowIfNull(memory);
        return MeasuredFirstWords.HostKey(host) is not { } key ? null
            : memory.Models.Where(m => string.Equals(MeasuredFirstWords.HostKey(m.Host), key, StringComparison.OrdinalIgnoreCase) &&
                SameModel(m.Model, model)).OrderByDescending(m => m.MeasuredAt).FirstOrDefault();
    }

    /// <summary>Milliseconds as "1.2 s" (under 10 s) or "45 s".</summary>
    public static string Seconds(int ms) => ms < 10_000
        ? (ms / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s"
        : (ms / 1000.0).ToString("0", CultureInfo.InvariantCulture) + " s";

    private static string Gb(long bytes) => (bytes / 1e9).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
}
