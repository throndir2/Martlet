using System.Runtime.InteropServices;
using Martlet.Core.Contracts;
using Martlet.Core.Platforms;
using Martlet.Core.Settings;

namespace Martlet.Diagnostics;

public sealed record ProbeObservation(string FindingId, EvidenceProvenance Provenance,
    DateTimeOffset? ObservedAt = null, SettingsLoadResult? Settings = null);

public sealed class ProbeDefinition
{
    public string Id { get; }
    public Stage Stage { get; }
    public bool Required { get; }
    public IReadOnlyList<ProbeEffect> Effects { get; }
    public TimeSpan Timeout { get; }
    public TimeSpan MaximumAge { get; }
    public string UnavailableFindingId { get; }
    internal Func<CancellationToken, Task<ProbeObservation>>? Execute { get; }

    public ProbeDefinition(string id, Stage stage, bool required, IEnumerable<ProbeEffect> effects,
        Func<CancellationToken, Task<ProbeObservation>>? execute = null, string unavailableFindingId = "probe.not_run",
        TimeSpan? timeout = null, TimeSpan? maximumAge = null)
    {
        ContractRules.Identifier(id);
        ContractRules.Defined(stage);
        Id = id;
        Stage = stage;
        Required = required;
        Effects = Array.AsReadOnly(effects.ToArray());
        if (Effects.Count == 0 || Effects.Count != Effects.Distinct().Count())
            throw new ArgumentException("Declare unique probe effects.");
        foreach (var effect in Effects)
            ContractRules.Defined(effect);
        Timeout = timeout ?? TimeSpan.FromSeconds(2);
        MaximumAge = maximumAge ?? TimeSpan.FromMinutes(1);
        if (Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromSeconds(30) ||
            MaximumAge <= TimeSpan.Zero || MaximumAge > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(timeout), "Probe time bounds are invalid.");
        if (DiagnosticCatalog.Finding(unavailableFindingId).Outcome is not (ProbeOutcome.Skipped or ProbeOutcome.NotConfigured or ProbeOutcome.Unknown))
            throw new ArgumentException("An unavailable definition cannot claim evidence.");
        Execute = execute;
        UnavailableFindingId = unavailableFindingId;
    }
}

public sealed class ProbeRegistry
{
    public IReadOnlyList<ProbeDefinition> Definitions { get; }

    public ProbeRegistry(IEnumerable<ProbeDefinition> definitions)
    {
        Definitions = Array.AsReadOnly(definitions.ToArray());
        if (Definitions.Count > 64 || Definitions.Any(item => item is null) ||
            Definitions.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != Definitions.Count)
            throw new ArgumentException("A registry supports at most 64 unique probe IDs.");
    }

    public IReadOnlyList<ProbeDefinition> Select(IEnumerable<string>? ids = null)
    {
        if (ids is null)
            return Definitions;
        var selection = ids.ToArray();
        if (selection.Length is 0 or > 64 || selection.Distinct(StringComparer.Ordinal).Count() != selection.Length)
            throw new ArgumentException("Select one to 64 unique probe IDs.");
        var selected = selection.Select(id => Definitions.FirstOrDefault(item => item.Id == id)
            ?? throw new ArgumentException("Unknown probe selection.")).ToArray();
        return Array.AsReadOnly(selected);
    }

    public static ProbeRegistry Local(SettingsStore store) => new(
    [
        new("settings.load", Stage.Settings, true, [ProbeEffect.LocalReadOnly], async token =>
        {
            var settings = await store.LoadAsync(token).ConfigureAwait(false);
            return new(DiagnosticCatalog.SettingsFinding(settings), EvidenceProvenance.Live, Settings: settings);
        }),
        new("provider.connection", Stage.Provider, true, [ProbeEffect.Network, ProbeEffect.Permissioned, ProbeEffect.ProviderCost],
            unavailableFindingId: "provider.unavailable"),
        new("audio.playback", Stage.Playback, true, [ProbeEffect.Device, ProbeEffect.Permissioned],
            unavailableFindingId: "audio.output_unavailable"),
        new("application.version", Stage.Application, true, [ProbeEffect.LocalReadOnly],
            _ => Task.FromResult(new ProbeObservation("application.available", EvidenceProvenance.Live))),
        new("runtime.version", Stage.Application, true, [ProbeEffect.LocalReadOnly],
            _ => Task.FromResult(new ProbeObservation(Environment.Version.Major == 10 ? "runtime.available" : "runtime.unsupported", EvidenceProvenance.Live))),
        new("platform.architecture", Stage.Application, true, [ProbeEffect.LocalReadOnly],
            _ => Task.FromResult(new ProbeObservation(ArchitectureFinding(MachineArchitecture.Current), EvidenceProvenance.Live))),
        new("audio.input", Stage.Application, true, [ProbeEffect.Device, ProbeEffect.Permissioned],
            unavailableFindingId: "audio.input_unavailable"),
        new("pipeline.vad", Stage.Application, true, [ProbeEffect.LocalReadOnly], unavailableFindingId: "pipeline.unavailable"),
        new("pipeline.stt", Stage.Transcription, true, [ProbeEffect.Permissioned, ProbeEffect.ProviderCost], unavailableFindingId: "pipeline.unavailable"),
        new("pipeline.policy", Stage.TurnPolicy, true, [ProbeEffect.LocalReadOnly], unavailableFindingId: "pipeline.unavailable"),
        new("pipeline.llm", Stage.Generation, true, [ProbeEffect.Permissioned, ProbeEffect.ProviderCost], unavailableFindingId: "pipeline.unavailable"),
        new("pipeline.tts", Stage.Synthesis, true, [ProbeEffect.Permissioned, ProbeEffect.ProviderCost], unavailableFindingId: "pipeline.unavailable"),
        new("host.connection", Stage.Provider, false, [ProbeEffect.Network, ProbeEffect.Permissioned], unavailableFindingId: "host.unavailable"),
        new("planning.catalog", Stage.Application, false, [ProbeEffect.LocalReadOnly],
            token => Task.Run(() => new ProbeObservation(PlanningFinding(store.DataDirectory), EvidenceProvenance.Live), token),
            timeout: TimeSpan.FromSeconds(15))
    ]);

    /// <summary>The planning.catalog finding: where Recommended setup's options come from (docs/RECOMMENDATION_DESIGN.md, "Planner
    /// on the catalog"): the model catalog's daily copy, its shipped snapshot, or Martlet's own list alone. Reads files only.</summary>
    public static string PlanningFinding(string dataDirectory)
    {
        try
        {
            var models = Martlet.Core.Planning.ModelCatalogStore.For(dataDirectory).Load();
            if (models.Models.Count == 0) return "planning.seed_only";
            return Martlet.Core.Planning.PlanningCatalog.Copy(models).StartsWith("the daily copy", StringComparison.Ordinal)
                ? "planning.catalog_daily" : "planning.catalog_snapshot";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return "planning.seed_only";
        }
    }

    /// <summary>The platform.architecture finding for this PC's processor and Martlet's process.</summary>
    public static string ArchitectureFinding(MachineArchitecture architecture) => (architecture.Machine, architecture.Process) switch
    {
        (Architecture.X64, Architecture.X64) => "platform.x64",
        (Architecture.Arm64, Architecture.Arm64) => "platform.arm64_native",
        (Architecture.Arm64, _) => "platform.arm64_emulated",
        _ => "platform.unsupported"
    };
}
