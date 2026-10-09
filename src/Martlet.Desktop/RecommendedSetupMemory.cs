using System.IO;
using System.Text.Json;
using Martlet.Core.Planning;

// Also built into Martlet's MCP server (recommended_setup_status), in its own namespace.
#if MARTLET_MCP
namespace Martlet.Mcp.Shared;
#else
namespace Martlet.Desktop;
#endif

/// <summary>A recommended setup the owner declined on this PC (Not now), by its <see cref="NetworkRecommendation.Fingerprint"/>.</summary>
internal sealed record DeclinedSetup(string Fingerprint, DateTimeOffset At);

/// <summary>The recommended setups declined on this PC (recommended-setup.json in the data folder; never shared): an automatic
/// check doesn't ask about the same recommended setup again until something changes it. The newest <see cref="Kept"/> are kept.
/// <see cref="Off"/> holds the parts the owner turned off in the review (<see cref="ComponentRanking.CanBeOff"/>), by name;
/// the recommendation removes them and plans without them. <see cref="UseServedModels"/>: the review's Use models your apps
/// already run.</summary>
internal sealed record RecommendedSetupMemory
{
    internal const string FileName = "recommended-setup.json";
    internal const int Kept = 20;
    private const int MaxFileBytes = 64 * 1024;

    public IReadOnlyList<DeclinedSetup> Declined { get; init; } = [];

    public IReadOnlyList<string> Off { get; init; } = [];

    /// <summary>Use models your apps already run: the recommendation looks for the chat models the model apps on this PC serve
    /// (Ollama, LM Studio, llama.cpp, vLLM...) and plans Thinking with the best one that fits. On unless the owner turns it off.</summary>
    public bool UseServedModels { get; init; } = true;

    internal RecommendedSetupMemory WithServed(bool use) => this with { UseServedModels = use };

    /// <summary>Prefer models your hosts already have: Thinking (and a new Deep thinking role) uses a better model a host
    /// service already runs or keeps downloaded before one it must download, also when its first word comes later. Off unless
    /// the owner turns it on.</summary>
    public bool PreferHostModels { get; init; }

    internal RecommendedSetupMemory WithHostModels(bool prefer) => this with { PreferHostModels = prefer };

    /// <summary>The parts turned off, as the planner takes them (a part this PC sets on its Companion page is turned off there,
    /// not here: <see cref="ComponentRanking.SetOnPage"/>).</summary>
    internal IReadOnlyCollection<PlanComponent> OffParts =>
        [.. Off.Select(name => Enum.TryParse<PlanComponent>(name, out var part) ? part : (PlanComponent?)null)
            .OfType<PlanComponent>().Where(OffHere).Distinct()];

    /// <summary>The same memory with <paramref name="part"/> turned off or back on; a part that can't be off here is ignored.</summary>
    internal RecommendedSetupMemory WithOff(PlanComponent part, bool off) => !OffHere(part) ? this : this with
    {
        Off = [.. OffParts.Where(p => p != part).Concat(off ? [part] : []).Order().Select(p => p.ToString())]
    };

    private static bool OffHere(PlanComponent part) => ComponentRanking.CanBeOff(part) && !ComponentRanking.SetOnPage(part);

    internal bool WasDeclined(string? fingerprint) =>
        fingerprint is { Length: > 0 } && Declined.Any(d => string.Equals(d.Fingerprint, fingerprint, StringComparison.Ordinal));

    internal RecommendedSetupMemory Decline(string fingerprint, DateTimeOffset at) => fingerprint.Length == 0 ? this : this with
    {
        Declined = [new DeclinedSetup(fingerprint, at), .. Declined.Where(d => d.Fingerprint != fingerprint).Take(Kept - 1)]
    };

    internal static RecommendedSetupMemory Load(string? directory)
    {
        if (directory is null) return new();
        try
        {
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path) || new FileInfo(path).Length > MaxFileBytes) return new();
            var loaded = JsonSerializer.Deserialize<RecommendedSetupMemory>(File.ReadAllText(path));
            return loaded is null ? new() : loaded with
            {
                Declined = [.. (loaded.Declined ?? []).Where(d => d is { Fingerprint.Length: > 0 and <= 256 }).Take(Kept)],
                Off = [.. (loaded.Off ?? []).Where(n => n is { Length: > 0 and <= 32 }).Distinct(StringComparer.Ordinal).Take(16)]
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new();
        }
    }

    /// <summary>Saves recommended-setup.json atomically; false when the data folder can't be written.</summary>
    internal bool Save(string? directory)
    {
        if (directory is null) return false;
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            var temporary = Path.Combine(directory, $"recommended-setup.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}

/// <summary>What an automatic check does with a recommendation: nothing, keep it until someone uses this PC, or ask now.</summary>
internal enum SetupAskStep { Nothing, Wait, Ask }

/// <summary>Which companion PC asks about a better setup after a computer came back or stayed away. Every companion PC checks;
/// only the one someone is using asks: someone used its keyboard or mouse, or talked with Martlet there, in the last
/// <see cref="InUse"/> (the measure reminders use to find the companion PC used most recently). A companion PC nobody uses
/// keeps the suggestion and asks when someone comes back to it within <see cref="Keep"/>, if it is still worth asking. So the
/// PC in front of the owner asks, and a gaming PC left on in another room stays quiet. Host PCs never ask.</summary>
internal static class SetupAskRule
{
    internal static readonly TimeSpan InUse = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan Keep = TimeSpan.FromHours(1);

    internal static (SetupAskStep Step, string Why) Decide(NetworkRecommendation recommendation, RecommendedSetupMemory memory,
        bool companion, TimeSpan idle)
    {
        ArgumentNullException.ThrowIfNull(recommendation);
        ArgumentNullException.ThrowIfNull(memory);
        if (!companion) return (SetupAskStep.Nothing, "this is a host PC; companion PCs ask");
        if (recommendation.AlreadyOptimal) return (SetupAskStep.Nothing, "your computers already use the recommended setup");
        if (!recommendation.WorthAsking) return (SetupAskStep.Nothing, "only minor changes, not worth asking about");
        if (memory.WasDeclined(recommendation.Fingerprint)) return (SetupAskStep.Nothing, "you declined this setup on this PC before");
        if (idle >= InUse) return (SetupAskStep.Wait, $"nobody used this PC in the last {InUse.TotalMinutes:0} minutes; it asks when someone does");
        return (SetupAskStep.Ask, "someone is using this PC");
    }
}
