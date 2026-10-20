using System.Text.Json;

namespace Martlet.Core.Planning;

/// <summary>A recommended setup the owner declined (Not now), by its <see cref="NetworkRecommendation.Fingerprint"/>.</summary>
public sealed record DeclinedSetup(string Fingerprint, DateTimeOffset At);

/// <summary>The recommended setups the owner declined (recommended-setup.json in the data folder; the same on all the owner's
/// computers, the household setting <see cref="SharedKey"/>): an automatic check on any computer doesn't ask about the same
/// recommended setup again until something changes it. The newest <see cref="Kept"/> are kept.
/// <see cref="Off"/> holds the parts the owner turned off in the review (<see cref="ComponentRanking.CanBeOff"/>), by name;
/// the recommendation removes them and plans without them. <see cref="UseServedModels"/>: the review's Use models your apps
/// already run.</summary>
public sealed record RecommendedSetupMemory
{
    public const string FileName = "recommended-setup.json";
    public const int Kept = 20;
    private const int MaxFileBytes = 64 * 1024;

    public IReadOnlyList<DeclinedSetup> Declined { get; init; } = [];

    public IReadOnlyList<string> Off { get; init; } = [];

    /// <summary>Use models your apps already run: the recommendation looks for the chat models the model apps on this PC serve
    /// (Ollama, LM Studio, llama.cpp, vLLM...) and plans Thinking with the best one that fits. On unless the owner turns it off.</summary>
    public bool UseServedModels { get; init; } = true;

    public RecommendedSetupMemory WithServed(bool use) => this with { UseServedModels = use };

    /// <summary>Prefer models your hosts already have: Thinking (and a new Deep thinking role) uses a better model a host
    /// service already runs or keeps downloaded before one it must download, also when its first word comes later. Off unless
    /// the owner turns it on.</summary>
    public bool PreferHostModels { get; init; }

    public RecommendedSetupMemory WithHostModels(bool prefer) => this with { PreferHostModels = prefer };

    /// <summary>The parts turned off, as the planner takes them (a part this PC sets on its Companion page is turned off there,
    /// not here: <see cref="ComponentRanking.SetOnPage"/>).</summary>
    public IReadOnlyCollection<PlanComponent> OffParts =>
        [.. Off.Select(name => Enum.TryParse<PlanComponent>(name, out var part) ? part : (PlanComponent?)null)
            .OfType<PlanComponent>().Where(OffHere).Distinct()];

    /// <summary>The same memory with <paramref name="part"/> turned off or back on; a part that can't be off here is ignored.</summary>
    public RecommendedSetupMemory WithOff(PlanComponent part, bool off) => !OffHere(part) ? this : this with
    {
        Off = [.. OffParts.Where(p => p != part).Concat(off ? [part] : []).Order().Select(p => p.ToString())]
    };

    private static bool OffHere(PlanComponent part) => ComponentRanking.CanBeOff(part) && !ComponentRanking.SetOnPage(part);

    public bool WasDeclined(string? fingerprint) =>
        fingerprint is { Length: > 0 } && Declined.Any(d => string.Equals(d.Fingerprint, fingerprint, StringComparison.Ordinal));

    public RecommendedSetupMemory Decline(string fingerprint, DateTimeOffset at) => fingerprint.Length == 0 ? this : this with
    {
        Declined = [new DeclinedSetup(fingerprint, at), .. Declined.Where(d => d.Fingerprint != fingerprint).Take(Kept - 1)]
    };

    public static RecommendedSetupMemory Load(string? directory)
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
    public bool Save(string? directory)
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
    /// <summary>The household setting that carries the declined setups and the parts turned off to the owner's other computers.</summary>
    public const string SharedKey = "recommended-setup";
    private static readonly JsonSerializerOptions ShareJson = new(JsonSerializerDefaults.Web);

    /// <summary>The memory as it travels.</summary>
    public string Share() => JsonSerializer.Serialize(this, ShareJson);

    /// <summary>A travelled memory, or null when it can't be read.</summary>
    public static RecommendedSetupMemory? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return JsonSerializer.Deserialize<RecommendedSetupMemory>(value, ShareJson) is { Declined: not null, Off: not null } memory ? memory : null; }
        catch (Exception error) when (error is JsonException or NotSupportedException) { return null; }
    }

    /// <summary>The household setting backed by <see cref="FileName"/> in <paramref name="directory"/>.</summary>
    public static Martlet.Core.Sync.ISharedSection Section(string directory) => new Martlet.Core.Sync.DelegateSection(SharedKey,
        "Recommended setups you declined", _ =>
        {
            var path = Path.Combine(directory, FileName);
            DateTimeOffset? changed = File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) : null;
            return Task.FromResult<Martlet.Core.Sync.SharedLocal?>(new(Load(directory).Share(), null, changed is null, changed));
        }, (setting, _) =>
        {
            if (Parse(setting.Value) is not { } shared) return Task.FromResult(Martlet.Core.Sync.SharedApply.Waiting("It was saved by a newer Martlet."));
            if (Load(directory).Share() == shared.Share()) return Task.FromResult(Martlet.Core.Sync.SharedApply.Done);
            return Task.FromResult(shared.Save(directory) ? Martlet.Core.Sync.SharedApply.Done : Martlet.Core.Sync.SharedApply.Waiting("It couldn't be saved on this PC."));
        });
}
