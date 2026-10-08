namespace Martlet.F5;

public static class F5ReferenceLimits
{
    public const int SchemaVersion = 1;
    public const int MaximumPresets = 32;
    public const int MaximumSnapshotsPerPreset = 16;
    public const int MaximumTotalSnapshots = 64;
    public const int MaximumPresetNameCharacters = 80;
    public const int MaximumPresetNameUtf8Bytes = 160;
    public const int MaximumTranscriptCharacters = 4096;
    public const int MaximumTranscriptUtf8Bytes = 8192;
    public const int MaximumSourcePathCharacters = 1024;
    public const int MaximumAudioFileBytes = 4 * 1024 * 1024;
    public const int MaximumStoreBytes = 1024 * 1024;
    public const int MinimumDurationMilliseconds = 1_000;
    public const int MaximumDurationMilliseconds = 30_000;
    public const long MaximumStoreRevision = long.MaxValue - 1;
    public const string RightsStatementVersion = "voice-rights-v1";
}

public sealed record F5ReferenceAudioFormat
{
    public required int SampleRate { get; init; }
    public required int Channels { get; init; }
    public required int BitsPerSample { get; init; }
    public required long SampleCount { get; init; }
    public required int DataBytes { get; init; }
    public required int DurationMilliseconds { get; init; }

    /// <summary>The format of a reference WAV, checked against the rules the reference store applies to every recording
    /// (throws <see cref="F5Exception"/>).</summary>
    public static F5ReferenceAudioFormat Parse(ReadOnlySpan<byte> wave) => F5ReferenceAudio.Parse(wave);

    internal void Validate()
    {
        var computedDuration = SampleRate > 0 && SampleCount > 0
            ? Math.Ceiling(SampleCount * 1000d / SampleRate)
            : double.PositiveInfinity;
        F5Guard.Require(SampleRate is 16_000 or 22_050 or 24_000 or 44_100 or 48_000 &&
            Channels == 1 && BitsPerSample == 16 &&
            SampleCount is > 0 and <= int.MaxValue / 2 &&
            DataBytes == SampleCount * 2 &&
            computedDuration <= int.MaxValue &&
            DurationMilliseconds == (int)computedDuration &&
            DurationMilliseconds is >= F5ReferenceLimits.MinimumDurationMilliseconds and
                <= F5ReferenceLimits.MaximumDurationMilliseconds,
            F5Failure.InvalidAudio);
    }
}

public enum F5VoiceRightsBasis
{
    OwnVoice,
    ExplicitPermission,
    /// <summary>A published recording anyone may use, such as Martlet's starter voices (<see cref="F5BundledVoices"/>: public
    /// domain, CC0, CMU ARCTIC or the Jenny TTS dataset). Earlier versions also used it for the F5-TTS example clip they bundled.</summary>
    PublishedSample
}

public sealed record F5VoiceRightsAcknowledgement
{
    public int SchemaVersion { get; init; } = F5ReferenceLimits.SchemaVersion;
    public required Guid AcknowledgementId { get; init; }
    public required F5VoiceRightsBasis Basis { get; init; }
    public required string StatementVersion { get; init; }
    public required string ProcessingDestinationId { get; init; }
    public required DateTimeOffset AcknowledgedAtUtc { get; init; }
    public required bool Confirmed { get; init; }

    internal void Validate(DateTimeOffset now)
    {
        F5Guard.Require(SchemaVersion == F5ReferenceLimits.SchemaVersion,
            F5Failure.UnsupportedVersion);
        F5Guard.Require(AcknowledgementId != Guid.Empty && Confirmed,
            F5Failure.RightsRequired);
        F5Guard.Defined(Basis);
        F5Guard.Require(StatementVersion == F5ReferenceLimits.RightsStatementVersion,
            F5Failure.RightsRequired);
        F5Guard.Identifier(ProcessingDestinationId, 128);
        F5Guard.Utc(AcknowledgedAtUtc);
        F5Guard.Require(AcknowledgedAtUtc <= now + TimeSpan.FromMinutes(5),
            F5Failure.RightsRequired);
    }

    public override string ToString() =>
        $"F5 voice-rights acknowledgement {{ AcknowledgementId = {AcknowledgementId}, Basis = {Basis}, Destination = {ProcessingDestinationId} }}";
}

public sealed record F5ReferenceSnapshotRequest
{
    public Guid? PresetId { get; init; }
    public required string PresetName { get; init; }
    public required string AbsoluteSourcePath { get; init; }
    public required string Transcript { get; init; }
    public required F5VoiceRightsAcknowledgement Rights { get; init; }

    public override string ToString() =>
        $"F5 reference snapshot request {{ PresetId = {PresetId}, content and path = omitted }}";
}

public sealed record F5ReferenceSnapshot
{
    public required Guid PresetId { get; init; }
    public required string PresetName { get; init; }
    public required string ReferenceRevision { get; init; }
    public required string AudioSha256 { get; init; }
    public required string TranscriptRevision { get; init; }
    public required F5ReferenceAudioFormat AudioFormat { get; init; }
    public required F5VoiceRightsAcknowledgement Rights { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }

    public override string ToString() =>
        $"F5 reference snapshot {{ PresetId = {PresetId}, ReferenceRevision = {ReferenceRevision}, content and path = omitted }}";
}

public sealed class F5ReferencePresetInfo
{
    internal F5ReferencePresetInfo(Guid id, string name, F5ReferenceSnapshot[] snapshots)
    {
        Id = id;
        Name = name;
        Snapshots = Array.AsReadOnly(snapshots);
    }

    public Guid Id { get; }
    public string Name { get; }
    public IReadOnlyList<F5ReferenceSnapshot> Snapshots { get; }
}

public sealed class F5ReferenceStoreInspection
{
    internal F5ReferenceStoreInspection(long storeRevision, Guid? appliedPresetId,
        string? appliedReferenceRevision, F5ReferencePresetInfo[] presets)
    {
        StoreRevision = storeRevision;
        AppliedPresetId = appliedPresetId;
        AppliedReferenceRevision = appliedReferenceRevision;
        Presets = Array.AsReadOnly(presets);
    }

    public long StoreRevision { get; }
    public Guid? AppliedPresetId { get; }
    public string? AppliedReferenceRevision { get; }
    public IReadOnlyList<F5ReferencePresetInfo> Presets { get; }
}

public enum F5ApplyDecision
{
    No,
    Allow
}

public sealed class F5ReferenceApplyPreview
{
    internal F5ReferenceApplyPreview(Guid id, long storeRevision, Guid presetId,
        string referenceRevision, string destinationId, string? previousReferenceRevision)
    {
        Id = id;
        StoreRevision = storeRevision;
        PresetId = presetId;
        ReferenceRevision = referenceRevision;
        DestinationId = destinationId;
        PreviousReferenceRevision = previousReferenceRevision;
    }

    public Guid Id { get; }
    public long StoreRevision { get; }
    public Guid PresetId { get; }
    public string ReferenceRevision { get; }
    public string DestinationId { get; }
    public string? PreviousReferenceRevision { get; }

    public F5ReferenceApplyAuthorization Authorize(F5ApplyDecision decision = F5ApplyDecision.No)
    {
        F5Guard.Defined(decision);
        F5Guard.Require(decision == F5ApplyDecision.Allow, F5Failure.AuthorizationRequired);
        return new(this);
    }

    public override string ToString() =>
        $"F5 apply preview {{ PresetId = {PresetId}, ReferenceRevision = {ReferenceRevision}, content and path = omitted }}";
}

public sealed class F5ReferenceApplyAuthorization
{
    internal F5ReferenceApplyAuthorization(F5ReferenceApplyPreview preview) => Preview = preview;
    internal F5ReferenceApplyPreview Preview { get; }
    internal int Used;
    public override string ToString() => "One-use F5 reference apply authorization";
}

public sealed record F5ReferenceApplyReceipt(
    long StoreRevision,
    Guid PresetId,
    string ReferenceRevision,
    string? PreviousReferenceRevision);

public sealed class F5ReferenceUseLease : IDisposable
{
    private readonly F5ReferencePresetStore owner;
    private byte[]? audio;

    internal F5ReferenceUseLease(
        F5ReferencePresetStore owner,
        F5ReferenceSnapshotDocument snapshot,
        byte[] audio)
    {
        this.owner = owner;
        this.audio = audio;
        PresetId = snapshot.PresetId;
        ReferenceRevision = snapshot.ReferenceRevision;
        DestinationId = snapshot.Rights.ProcessingDestinationId;
        Reference = new(
            snapshot.PresetId,
            snapshot.ReferenceRevision,
            snapshot.AudioSha256,
            snapshot.Transcript,
            snapshot.TranscriptRevision,
            snapshot.AudioFormat,
            audio);
    }

    public Guid PresetId { get; }
    public string ReferenceRevision { get; }
    public string DestinationId { get; }
    public F5ReferenceInput Reference { get; }

    public void Dispose()
    {
        var bytes = Interlocked.Exchange(ref audio, null);
        if (bytes is null)
            return;
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
        owner.ReleaseUse();
    }

    public override string ToString() =>
        $"F5 reference use lease {{ PresetId = {PresetId}, ReferenceRevision = {ReferenceRevision}, content = omitted }}";
}
