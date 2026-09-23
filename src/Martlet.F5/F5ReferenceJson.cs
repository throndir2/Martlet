using System.Text.Json;
using System.Text.Json.Serialization;

namespace Martlet.F5;

internal sealed record F5ReferenceStoreDocument
{
    public required int SchemaVersion { get; init; }
    public required long StoreRevision { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
    public required Guid? AppliedPresetId { get; init; }
    public required string? AppliedReferenceRevision { get; init; }
    public required F5ReferencePresetDocument[] Presets { get; init; }
}

internal sealed record F5ReferencePresetDocument
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required F5ReferenceSnapshotDocument[] Snapshots { get; init; }
}

internal sealed record F5ReferenceSnapshotDocument
{
    public required Guid PresetId { get; init; }
    public required string ReferenceRevision { get; init; }
    public required string SourcePath { get; init; }
    public required string AudioRelativePath { get; init; }
    public required string AudioSha256 { get; init; }
    public required string Transcript { get; init; }
    public required string TranscriptRevision { get; init; }
    public required F5ReferenceAudioFormat AudioFormat { get; init; }
    public required F5VoiceRightsAcknowledgement Rights { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }

    internal F5ReferenceSnapshot ToPublic(string presetName) => new()
    {
        PresetId = PresetId,
        PresetName = presetName,
        ReferenceRevision = ReferenceRevision,
        AudioSha256 = AudioSha256,
        TranscriptRevision = TranscriptRevision,
        AudioFormat = AudioFormat,
        Rights = Rights,
        CreatedAtUtc = CreatedAtUtc
    };
}

internal static class F5ReferenceJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        MaxDepth = 16,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) }
    };

    internal static F5ReferenceStoreDocument Read(ReadOnlyMemory<byte> bytes, DateTimeOffset now)
    {
        F5Guard.Require(bytes.Length is > 0 and <= F5ReferenceLimits.MaximumStoreBytes,
            F5Failure.CorruptStore);
        try
        {
            using var parsed = JsonDocument.Parse(bytes, new()
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
            RejectDuplicateProperties(parsed.RootElement);
            var document = JsonSerializer.Deserialize<F5ReferenceStoreDocument>(bytes.Span, Options);
            F5Guard.Require(document is not null, F5Failure.CorruptStore);
            try
            {
                Validate(document!, now);
            }
            catch (F5Exception error) when (error.Failure != F5Failure.UnsupportedVersion)
            {
                throw new F5Exception(F5Failure.CorruptStore);
            }
            return document!;
        }
        catch (F5Exception)
        {
            throw;
        }
        catch (JsonException)
        {
            throw new F5Exception(F5Failure.CorruptStore);
        }
        catch (NotSupportedException)
        {
            throw new F5Exception(F5Failure.CorruptStore);
        }
    }

    internal static byte[] Write(F5ReferenceStoreDocument document, DateTimeOffset now)
    {
        Validate(document, now);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Options);
        F5Guard.Require(bytes.Length <= F5ReferenceLimits.MaximumStoreBytes,
            F5Failure.LimitExceeded);
        return bytes;
    }

    internal static string RelativeAudioPath(Guid presetId, string referenceRevision)
    {
        F5Guard.Require(presetId != Guid.Empty);
        F5Guard.Sha256(referenceRevision);
        return $"audio/{presetId:N}/{referenceRevision}.wav";
    }

    private static void Validate(F5ReferenceStoreDocument document, DateTimeOffset now)
    {
        F5Guard.Require(document.SchemaVersion == F5ReferenceLimits.SchemaVersion,
            document.SchemaVersion > F5ReferenceLimits.SchemaVersion
                ? F5Failure.UnsupportedVersion
                : F5Failure.CorruptStore);
        F5Guard.Require(document.StoreRevision is >= 0 and <= F5ReferenceLimits.MaximumStoreRevision,
            F5Failure.CorruptStore);
        F5Guard.Utc(document.UpdatedAtUtc);
        F5Guard.Require(document.UpdatedAtUtc <= now + TimeSpan.FromMinutes(5),
            F5Failure.CorruptStore);
        F5Guard.Require((document.AppliedPresetId is null) ==
            (document.AppliedReferenceRevision is null), F5Failure.CorruptStore);
        F5Guard.Require(document.AppliedPresetId is null || document.AppliedPresetId != Guid.Empty,
            F5Failure.CorruptStore);
        if (document.AppliedReferenceRevision is not null)
            F5Guard.Sha256(document.AppliedReferenceRevision);
        if (document.Presets is null)
            throw new F5Exception(F5Failure.CorruptStore);
        var presets = document.Presets;
        F5Guard.Require(presets.Length <= F5ReferenceLimits.MaximumPresets,
            F5Failure.CorruptStore);
        F5Guard.Require(presets.All(preset => preset is not null),
            F5Failure.CorruptStore);
        F5Guard.Require(presets.Select(preset => preset.Id).Distinct().Count() ==
            presets.Length, F5Failure.CorruptStore);

        var totalSnapshots = 0;
        foreach (var preset in presets)
        {
            if (preset.Snapshots is null)
                throw new F5Exception(F5Failure.CorruptStore);
            var snapshots = preset.Snapshots;
            F5Guard.Require(snapshots.All(snapshot => snapshot is not null),
                F5Failure.CorruptStore);
            F5Guard.Require(preset.Id != Guid.Empty &&
                snapshots.Length is > 0 and <= F5ReferenceLimits.MaximumSnapshotsPerPreset,
                F5Failure.CorruptStore);
            F5Guard.Utf8Text(preset.Name, F5ReferenceLimits.MaximumPresetNameCharacters,
                F5ReferenceLimits.MaximumPresetNameUtf8Bytes, allowNewLines: false);
            F5Guard.Require(snapshots.Select(snapshot => snapshot.ReferenceRevision)
                .Distinct(StringComparer.Ordinal).Count() == snapshots.Length,
                F5Failure.CorruptStore);
            totalSnapshots = checked(totalSnapshots + snapshots.Length);
            foreach (var snapshot in snapshots)
            {
                F5Guard.Require(snapshot.PresetId == preset.Id, F5Failure.CorruptStore);
                F5Guard.Sha256(snapshot.ReferenceRevision);
                F5Guard.Sha256(snapshot.AudioSha256);
                F5Guard.Sha256(snapshot.TranscriptRevision);
                F5Guard.Require(F5ReferencePaths.NormalizeSource(snapshot.SourcePath,
                    requireExisting: false) == snapshot.SourcePath, F5Failure.CorruptStore);
                F5Guard.Require(snapshot.AudioRelativePath ==
                    RelativeAudioPath(preset.Id, snapshot.ReferenceRevision), F5Failure.CorruptStore);
                F5Guard.Utf8Text(snapshot.Transcript, F5ReferenceLimits.MaximumTranscriptCharacters,
                    F5ReferenceLimits.MaximumTranscriptUtf8Bytes);
                var audioFormat = snapshot.AudioFormat ??
                    throw new F5Exception(F5Failure.CorruptStore);
                var rights = snapshot.Rights ??
                    throw new F5Exception(F5Failure.CorruptStore);
                audioFormat.Validate();
                rights.Validate(now);
                F5ReferenceDigests.Validate(snapshot);
                F5Guard.Utc(snapshot.CreatedAtUtc);
                F5Guard.Require(snapshot.CreatedAtUtc <= now + TimeSpan.FromMinutes(5),
                    F5Failure.CorruptStore);
            }
        }
        F5Guard.Require(totalSnapshots <= F5ReferenceLimits.MaximumTotalSnapshots,
            F5Failure.CorruptStore);
        if (document.AppliedPresetId is { } appliedPreset)
        {
            var preset = presets.SingleOrDefault(candidate => candidate.Id == appliedPreset);
            F5Guard.Require(preset is not null && preset.Snapshots.Any(snapshot =>
                snapshot.ReferenceRevision == document.AppliedReferenceRevision),
                F5Failure.CorruptStore);
        }
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                F5Guard.Require(names.Add(property.Name), F5Failure.CorruptStore);
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                RejectDuplicateProperties(item);
        }
    }
}
