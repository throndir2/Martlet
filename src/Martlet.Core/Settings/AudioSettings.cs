using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

public enum LocalAudioOutcome { SamplesReceived, ToneDrained, Heard }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AudioCheckpoint : IContract
{
    public required Guid ConfigurationRevision { get; init; }
    public required DateTimeOffset TestedAt { get; init; }
    public required LocalAudioOutcome Outcome { get; init; }

    public void Validate()
    {
        ContractRules.Require(ConfigurationRevision != Guid.Empty && TestedAt.Offset == TimeSpan.Zero &&
            TestedAt > DateTimeOffset.UnixEpoch, "Audio evidence requires a configuration revision and UTC observation time.");
        ContractRules.Defined(Outcome);
    }
}

// Local UI data only. Neither endpoint identity nor friendly label belongs in exported metadata.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AudioChoice : IContract
{
    public required Guid ConfigurationRevision { get; init; }
    public string? EndpointId { get; init; }
    public required string DisplayName { get; init; }
    public AudioCheckpoint? Checkpoint { get; init; }

    public static AudioChoice Default(bool input) => new()
    {
        ConfigurationRevision = Guid.NewGuid(),
        DisplayName = input ? "Windows default - follow on NEXT test only" : "Windows default - bind once at test start"
    };

    public void Validate()
    {
        ContractRules.Require(ConfigurationRevision != Guid.Empty, "Audio selection needs a configuration revision.");
        ContractRules.Require(EndpointId is null || EndpointId is { Length: > 0 and <= 1024 } &&
            !string.IsNullOrWhiteSpace(EndpointId) && !EndpointId.Any(char.IsControl), "Invalid bounded audio endpoint identity.");
        ContractRules.Require(DisplayName is { Length: > 0 and <= 256 } && !string.IsNullOrWhiteSpace(DisplayName) &&
            !DisplayName.Any(char.IsControl), "Invalid bounded audio display label.");
        Checkpoint?.Validate();
        ContractRules.Require(Checkpoint is null || Checkpoint.ConfigurationRevision == ConfigurationRevision,
            "Changed audio selection invalidates its qualification checkpoint.");
    }

    public AudioChoice Select(string? endpointId, string displayName) =>
        EndpointId == endpointId && DisplayName == displayName ? this : new()
        {
            ConfigurationRevision = Guid.NewGuid(), EndpointId = endpointId, DisplayName = displayName
        };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AudioSettings : IContract
{
    public required int SchemaVersion { get; init; }
    public required AudioChoice Input { get; init; }
    public required AudioChoice Output { get; init; }

    public static AudioSettings Create() => new() { SchemaVersion = 1, Input = AudioChoice.Default(true), Output = AudioChoice.Default(false) };

    public void Validate()
    {
        ContractRules.Require(SchemaVersion == 1, "Unsupported audio settings version.", ErrorCode.UnsupportedVersion);
        ContractRules.Require(Input is not null && Output is not null, "Both audio policies are required.");
        Input!.Validate();
        Output!.Validate();
        ContractRules.Require(Input.Checkpoint is null or { Outcome: LocalAudioOutcome.SamplesReceived } &&
            Output.Checkpoint is null or { Outcome: LocalAudioOutcome.ToneDrained or LocalAudioOutcome.Heard },
            "Audio evidence must match its input or output stage.");
    }
}

// Historical local observations, not device identities, fresh authorization, or readiness.
public sealed record AudioSetupStatus(bool InputSelected, bool OutputSelected, bool InputUsesDefault, bool OutputUsesDefault,
    AudioCheckpointStatus? Input, AudioCheckpointStatus? Output)
{
    public void Validate()
    {
        Input?.Validate();
        Output?.Validate();
        ContractRules.Require((InputSelected || Input is null) && (OutputSelected || Output is null) &&
            Input is null or { Outcome: LocalAudioOutcome.SamplesReceived } &&
            Output is null or { Outcome: LocalAudioOutcome.ToneDrained or LocalAudioOutcome.Heard }, "Invalid local audio status metadata.");
    }

    public static AudioSetupStatus From(AudioSettings? settings) => new(settings is not null, settings is not null,
        settings?.Input.EndpointId is null, settings?.Output.EndpointId is null,
        AudioCheckpointStatus.From(settings?.Input.Checkpoint), AudioCheckpointStatus.From(settings?.Output.Checkpoint));

    public string Describe() =>
        $"LOCAL audio setup (not fixture, VAD or AI readiness):{Environment.NewLine}" +
        $"Mic: {Describe(Input, InputUsesDefault)}{Environment.NewLine}" +
        $"Output: {Describe(Output, OutputUsesDefault)}{Environment.NewLine}" +
        "Historical checkpoints only; current devices/permissions are UNVERIFIED. Open Audio setup for fresh, separately permitted tests. Nothing starts on launch.";

    private static string Describe(AudioCheckpointStatus? checkpoint, bool usesDefault) =>
        checkpoint is null ? "Never tested / selection changed; UNVERIFIED." :
        $"{checkpoint.Outcome}; {checkpoint.Provenance} at {checkpoint.TestedAt:O}; STALE for current readiness. " +
        (usesDefault ? "Default policy tested at that action; today's default is unverified." : "Chosen fixed configuration only.");
}

public sealed record AudioCheckpointStatus(DateTimeOffset TestedAt, LocalAudioOutcome Outcome)
{
    public string Provenance => Outcome == LocalAudioOutcome.Heard ? "LocalUserReported" : "LocalObserved";

    public void Validate()
    {
        ContractRules.Require(TestedAt.Offset == TimeSpan.Zero && TestedAt > DateTimeOffset.UnixEpoch, "Invalid local audio observation time.");
        ContractRules.Defined(Outcome);
    }

    public static AudioCheckpointStatus? From(AudioCheckpoint? checkpoint) =>
        checkpoint is null ? null : new(checkpoint.TestedAt, checkpoint.Outcome);
}
