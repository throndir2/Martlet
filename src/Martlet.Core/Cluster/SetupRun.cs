using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;
using Martlet.Core.Sync;

namespace Martlet.Core.Cluster;

/// <summary>Where one computer stands in a run that applies the recommended setup: still waiting for its turn, being changed
/// now, finished, stopped by a failed step, or waiting for the owner (a key to enter, someone at that computer).</summary>
public enum SetupMachineState { Pending, Configuring, Done, Failed, NeedsAttention }

/// <summary>One computer in a <see cref="SetupRun"/>: its state, the step it works on now or ended with, in words
/// ("Installing Chatterbox Turbo (2 of 4)"), and how many of its steps are finished.</summary>
public sealed record SetupRunMachine
{
    public required string MachineId { get; init; }
    public SetupMachineState State { get; init; }
    public string? Step { get; init; }
    public int Done { get; init; }
    public int Steps { get; init; }
}

/// <summary>A run that applies the recommended setup to all the owner's computers: who started it (a Martlet device ID) and
/// when, one entry per computer, and when it finished. The computer that runs it publishes it under its own shared settings
/// entry (<see cref="Key"/>, "setup-run.desktop-a"), so every computer shows Configuring within one settings sync. Only that
/// computer writes its entry; like the other per-computer entries it is not counted as a shared setting and leaves first when
/// a copy is full. Nonsecret: machine IDs and step texts only.</summary>
public sealed record SetupRun
{
    public const int SchemaVersion1 = 1;
    public const int MaximumMachines = 64;
    public const int MaximumStepCharacters = 300;
    public const string KeyPrefix = SharedSettings.SetupRunPrefix;

    /// <summary>A run that has not finished and not changed for this long is shown as stopped: the computer running it closed
    /// or lost its connection. One step can download for a long time, so this is generous.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(2);

    /// <summary>How long a finished run stays on Home and the Devices map, so the owner sees how it ended.</summary>
    public static readonly TimeSpan ShownAfterFinish = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        RespectNullableAnnotations = true,
        MaxDepth = 8,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) }
    };

    public int SchemaVersion { get; init; } = SchemaVersion1;
    public required string RunId { get; init; }
    public required string StartedBy { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public IReadOnlyList<SetupRunMachine> Machines { get; init; } = [];

    [JsonIgnore] public bool Finished => FinishedAt is not null;

    public SetupRunMachine? Machine(string machineId) => Machines.FirstOrDefault(m => m.MachineId == machineId);

    /// <summary>A new run: every computer waits, with the number of steps it has.</summary>
    public static SetupRun Start(string runId, string device, DateTimeOffset now, IEnumerable<(string MachineId, int Steps)> machines)
    {
        ContractRules.Identifier(runId);
        ContractRules.Identifier(device);
        var list = machines.GroupBy(m => m.MachineId, StringComparer.Ordinal)
            .Select(g => new SetupRunMachine { MachineId = g.Key, State = SetupMachineState.Pending, Steps = g.Sum(m => Math.Max(0, m.Steps)) })
            .Take(MaximumMachines).ToArray();
        return new() { RunId = runId, StartedBy = device, StartedAt = now.ToUniversalTime(), UpdatedAt = now.ToUniversalTime(), Machines = list };
    }

    /// <summary>The run with <paramref name="machineId"/> in <paramref name="state"/>, working on (or ended with)
    /// <paramref name="step"/> and <paramref name="done"/> of its steps finished. A computer the run doesn't list yet is added.</summary>
    public SetupRun With(string machineId, SetupMachineState state, string? step, int done, DateTimeOffset now)
    {
        var current = Machine(machineId);
        var next = (current ?? new SetupRunMachine { MachineId = machineId }) with
        {
            State = state, Step = Clip(step), Done = Math.Max(0, done), Steps = Math.Max(current?.Steps ?? 0, Math.Max(0, done))
        };
        var machines = current is null && Machines.Count >= MaximumMachines ? Machines
            : current is null ? [.. Machines, next]
            : Machines.Select(m => m.MachineId == machineId ? next : m).ToArray();
        return this with { Machines = machines, UpdatedAt = Later(now) };
    }

    /// <summary>The finished run: a computer that never started (its changes were all skipped) is Done when it had nothing
    /// left to do, else it needs the owner.</summary>
    public SetupRun Finish(DateTimeOffset now) => this with
    {
        Machines = Machines.Select(m => m.State is SetupMachineState.Pending or SetupMachineState.Configuring
            ? m with { State = m.Done >= m.Steps ? SetupMachineState.Done : SetupMachineState.NeedsAttention } : m).ToArray(),
        UpdatedAt = Later(now), FinishedAt = Later(now)
    };

    private DateTimeOffset Later(DateTimeOffset now) => now.ToUniversalTime() < UpdatedAt ? UpdatedAt : now.ToUniversalTime();

    /// <summary>The run ended without finishing (Martlet closed on the computer running it): every computer that still waited or
    /// worked needs the owner, with <paramref name="why"/>.</summary>
    public SetupRun Interrupted(DateTimeOffset now, string why)
    {
        var run = this;
        foreach (var machine in Machines.Where(m => m.State is SetupMachineState.Pending or SetupMachineState.Configuring && m.Done < m.Steps))
            run = run.With(machine.MachineId, SetupMachineState.NeedsAttention, why, machine.Done, now);
        return run.Finish(now);
    }

    /// <summary>Whether it is still working: not finished, and it changed within <see cref="StaleAfter"/>.</summary>
    public bool Active(DateTimeOffset now) => FinishedAt is null && now - UpdatedAt < StaleAfter;

    /// <summary>Whether Home and the Devices map show it: while it works, and for <see cref="ShownAfterFinish"/> after it ended.</summary>
    public bool Shown(DateTimeOffset now) => Active(now) || FinishedAt is { } finished && now - finished < ShownAfterFinish;

    /// <summary>The computers it changes now.</summary>
    public IReadOnlyList<SetupRunMachine> Configuring => Machines.Where(m => m.State == SetupMachineState.Configuring).ToArray();

    /// <summary>The run in one or two plain sentences, for Home and the tray ("Configuring your computers: 1 of 3 done.
    /// gpu-box: Installing Chatterbox Turbo (2 of 4).").</summary>
    public string Summary(DateTimeOffset now)
    {
        var done = Machines.Count(m => m.State == SetupMachineState.Done);
        var failed = Machines.Count(m => m.State == SetupMachineState.Failed);
        var attention = Machines.Count(m => m.State == SetupMachineState.NeedsAttention);
        if (FinishedAt is { } finished)
        {
            var parts = new List<string>();
            if (done > 0) parts.Add($"{done} done");
            if (failed > 0) parts.Add($"{failed} failed");
            if (attention > 0) parts.Add($"{attention} {(attention == 1 ? "needs" : "need")} you");
            return $"Your computers were reconfigured at {finished.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)}" +
                (parts.Count > 0 ? ": " + string.Join(", ", parts) : "") + ".";
        }
        if (!Active(now)) return $"Configuring your computers stopped (started by {StartedBy}, no news since " +
            $"{UpdatedAt.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)}).";
        var text = $"Configuring your computers: {done + failed + attention} of {Machines.Count} finished.";
        foreach (var machine in Configuring.Take(3)) text += $" {machine.MachineId}: {machine.Step ?? "working"}.";
        return text;
    }

    /// <summary>The shared settings entry for the run a device started ("setup-run.desktop-a").</summary>
    public static string Key(string deviceId)
    {
        var clean = new string(deviceId.ToLowerInvariant().Select(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '.' ? c : '-').ToArray());
        var key = KeyPrefix + clean;
        return key.Length > 64 ? key[..64] : key;
    }

    public static bool IsKey(string? key) => key?.StartsWith(KeyPrefix, StringComparison.Ordinal) == true;

    /// <summary>Every computer's latest run in a copy of the shared settings, newest first. An entry this Martlet can't read
    /// (a newer one wrote it) is left out.</summary>
    public static IReadOnlyList<SetupRun> All(SharedSettings settings) =>
        [.. settings.Settings.Where(s => IsKey(s.Key)).Select(s => Read(s.Value)).OfType<SetupRun>().OrderByDescending(r => r.UpdatedAt)];

    public string Write()
    {
        Validate();
        return JsonSerializer.Serialize(this, Json);
    }

    /// <summary>A published run, or null when it isn't one this Martlet reads.</summary>
    public static SetupRun? Read(string json)
    {
        try
        {
            var run = JsonSerializer.Deserialize<SetupRun>(json, Json);
            run?.Validate();
            return run;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or ContractException or ArgumentException) { return null; }
    }

    public void Validate()
    {
        ContractRules.Require(SchemaVersion == SchemaVersion1, "This run was written by a newer Martlet.", ErrorCode.UnsupportedVersion);
        ContractRules.Identifier(RunId);
        ContractRules.Identifier(StartedBy);
        ContractRules.Require(Machines is { Count: <= MaximumMachines }, "A run lists too many computers.");
        foreach (var machine in Machines)
        {
            ContractRules.Require(machine is not null, "A run's computer is missing.");
            ContractRules.Identifier(machine!.MachineId);
            ContractRules.Defined(machine.State);
            ContractRules.Require(machine.Step is null || machine.Step.Length <= MaximumStepCharacters && !machine.Step.Any(char.IsControl),
                "A run's step is too long.");
            ContractRules.Require(machine.Done is >= 0 and <= 1000 && machine.Steps is >= 0 and <= 1000, "A run's step count is out of range.");
        }
        ContractRules.Require(Machines.Select(m => m.MachineId).Distinct(StringComparer.Ordinal).Count() == Machines.Count,
            "A run lists a computer twice.");
    }

    private static string? Clip(string? text)
    {
        if (text is null) return null;
        var clean = new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        return clean.Length <= MaximumStepCharacters ? clean : clean[..(MaximumStepCharacters - 3)] + "...";
    }
}
