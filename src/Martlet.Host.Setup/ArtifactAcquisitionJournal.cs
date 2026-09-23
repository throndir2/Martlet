using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Martlet.Host.Setup;

internal enum ArtifactAcquisitionJournalState
{
    Prepared,
    Downloading,
    Interrupted,
    Verified,
    Finalized,
    Failed,
    Quarantining,
    Quarantined
}

internal enum ArtifactAcquisitionJournalPurpose { ArtifactAcquisition }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ArtifactAcquisitionJournalDocument
{
    public required int FormatVersion { get; init; }
    public required ArtifactAcquisitionJournalPurpose Purpose { get; init; }
    public required string SourceIdentityFingerprint { get; init; }
    public required string SelectionFingerprint { get; init; }
    public required string? TerminalOrigin { get; init; }
    public required string? PartialIdentity { get; init; }
    public required string? FinalIdentity { get; init; }
    public required string? QuarantineIdentity { get; init; }
    public required long QuarantineBytes { get; init; }
    public required int Supersessions { get; init; }
    public required Guid JournalId { get; init; }
    public required string PlanFingerprint { get; init; }
    public required string ArtifactIdentityFingerprint { get; init; }
    public required string ManifestSha256 { get; init; }
    public required string ArtifactId { get; init; }
    public required string SourceUrl { get; init; }
    public required string SourceRevision { get; init; }
    public required long ExpectedBytes { get; init; }
    public required string ExpectedSha256 { get; init; }
    public required string RightsAuthorizationFingerprint { get; init; }
    public required string SetupPlanFingerprint { get; init; }
    public required string SetupDesiredStateFingerprint { get; init; }
    public required string HostFactsFingerprint { get; init; }
    public required string ArtifactFactsFingerprint { get; init; }
    public required string SetupJournalPath { get; init; }
    public required string SetupJournalVersion { get; init; }
    public required long SetupJournalRevision { get; init; }
    public required Guid SetupJournalId { get; init; }
    public required string DestinationPath { get; init; }
    public required string PartialPath { get; init; }
    public required long Revision { get; init; }
    public required ArtifactAcquisitionJournalState State { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
    public required long PersistedBytes { get; init; }
    public required string? EntityTag { get; init; }
    public required DateTimeOffset? LastModifiedUtc { get; init; }
    public required bool RangeSupported { get; init; }
    public required bool PartialOwned { get; init; }
    public required bool FinalOwned { get; init; }
    public required string? ComputedSha256 { get; init; }
    public required ArtifactAcquisitionFailure? LastFailure { get; init; }
    public required string IntegritySha256 { get; init; }

    internal static ArtifactAcquisitionJournalDocument Create(
        ArtifactAcquisitionPlan plan,
        DateTimeOffset now) => new()
        {
            FormatVersion = 2,
            Purpose = ArtifactAcquisitionJournalPurpose.ArtifactAcquisition,
            SourceIdentityFingerprint = plan.Source.IdentityFingerprint,
            SelectionFingerprint = plan.Selection.Fingerprint,
            TerminalOrigin = null,
            PartialIdentity = null,
            FinalIdentity = null,
            QuarantineIdentity = null,
            QuarantineBytes = 0,
            Supersessions = 0,
            JournalId = Guid.NewGuid(),
            PlanFingerprint = plan.Fingerprint,
            ArtifactIdentityFingerprint = plan.Candidate.IdentityFingerprint,
            ManifestSha256 = plan.Candidate.ManifestSha256,
            ArtifactId = plan.Candidate.ArtifactId,
            SourceUrl = plan.Candidate.SourceUrl,
            SourceRevision = plan.Candidate.SourceRevision,
            ExpectedBytes = plan.Candidate.ExpectedBytes,
            ExpectedSha256 = plan.Candidate.ExpectedSha256!,
            RightsAuthorizationFingerprint = plan.RightsAuthorizationFingerprint,
            SetupPlanFingerprint = plan.SetupPlanFingerprint,
            SetupDesiredStateFingerprint = plan.SetupDesiredStateFingerprint,
            HostFactsFingerprint = plan.HostFactsFingerprint,
            ArtifactFactsFingerprint = plan.ArtifactFactsFingerprint,
            SetupJournalPath = plan.SetupJournalPath,
            SetupJournalVersion = plan.SetupJournalVersion,
            SetupJournalRevision = plan.SetupJournalRevision,
            SetupJournalId = plan.SetupJournalId,
            DestinationPath = plan.DestinationPath,
            PartialPath = plan.PartialPath,
            Revision = 0,
            State = ArtifactAcquisitionJournalState.Prepared,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            PersistedBytes = 0,
            EntityTag = null,
            LastModifiedUtc = null,
            RangeSupported = false,
            PartialOwned = false,
            FinalOwned = false,
            ComputedSha256 = null,
            LastFailure = null,
            IntegritySha256 = ""
        };
}

internal sealed class ArtifactAcquisitionJournalSnapshot
{
    internal ArtifactAcquisitionJournalDocument Document { get; }
    internal string Version { get; }
    internal string? EntityTag => Document.EntityTag;
    internal DateTimeOffset? LastModifiedUtc => Document.LastModifiedUtc;

    internal ArtifactAcquisitionJournalSnapshot(
        ArtifactAcquisitionJournalDocument document,
        string version)
    {
        Document = document;
        Version = version;
    }
}

internal static class ArtifactAcquisitionJournalCodec
{
    internal const int MaximumBytes = 131_072;
    private static readonly JsonSerializerOptions Options = CreateOptions();

    internal static byte[] Write(ArtifactAcquisitionJournalDocument document)
    {
        try
        {
            Validate(document, requireRevision: true, requireIntegrity: false);
            var unsealed = JsonSerializer.SerializeToUtf8Bytes(
                document with { IntegritySha256 = "" },
                Options);
            var hash = Convert.ToHexStringLower(SHA256.HashData(unsealed));
            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                document with { IntegritySha256 = hash },
                Options);
            if (bytes.Length > MaximumBytes)
                throw new ArtifactAcquisitionException(
                    ArtifactAcquisitionFailure.JournalTooLarge);
            return bytes;
        }
        catch (ArtifactAcquisitionException)
        {
            throw;
        }
        catch (Exception error) when (
            error is JsonException or InvalidDataException or FormatException)
        {
            throw new ArtifactAcquisitionException(
                ArtifactAcquisitionFailure.JournalCorrupt,
                error);
        }
    }

    internal static ArtifactAcquisitionJournalDocument Read(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > MaximumBytes)
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.JournalTooLarge);
        try
        {
            _ = new System.Text.UTF8Encoding(false, true).GetCharCount(bytes.Span);
            RejectDuplicateProperties(bytes.Span);
            var document = JsonSerializer.Deserialize<ArtifactAcquisitionJournalDocument>(
                bytes.Span,
                Options) ?? throw new InvalidDataException();
            Validate(document, requireRevision: true, requireIntegrity: true);
            var unsealed = JsonSerializer.SerializeToUtf8Bytes(
                document with { IntegritySha256 = "" },
                Options);
            var expected = Convert.ToHexStringLower(SHA256.HashData(unsealed));
            if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(document.IntegritySha256),
                Convert.FromHexString(expected)))
                throw new InvalidDataException();
            return document;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or FormatException or System.Text.DecoderFallbackException)
        {
            throw new ArtifactAcquisitionException(
                ArtifactAcquisitionFailure.JournalCorrupt,
                error);
        }
    }

    private static void Validate(
        ArtifactAcquisitionJournalDocument document,
        bool requireRevision,
        bool requireIntegrity)
    {
        if (document.FormatVersion != 2 ||
            document.Purpose != ArtifactAcquisitionJournalPurpose.ArtifactAcquisition ||
            document.JournalId == Guid.Empty ||
            document.SetupJournalId == Guid.Empty ||
            document.CreatedAtUtc == default ||
            document.UpdatedAtUtc < document.CreatedAtUtc ||
            requireRevision &&
                document.Revision is < 1 or >= long.MaxValue ||
            !Enum.IsDefined(document.State) ||
            document.ExpectedBytes <= 0 ||
            document.PersistedBytes < 0 ||
            document.PersistedBytes > document.ExpectedBytes ||
            document.SetupJournalRevision < 1 ||
            document.LastFailure is { } failure && !Enum.IsDefined(failure))
            throw new InvalidDataException();
        ValidateFingerprint(document.SourceIdentityFingerprint);
        ValidateFingerprint(document.SelectionFingerprint);
        if (document.Supersessions is < 0 or > 16 || document.QuarantineBytes < 0 ||
            document.QuarantineIdentity is null && document.QuarantineBytes != 0 ||
            document.TerminalOrigin is not (null or "https://api.github.com" or "https://release-assets.githubusercontent.com"))
            throw new InvalidDataException();
        if (document.PartialIdentity is { } partialIdentity) ValidateText(partialIdentity, 256);
        if (document.FinalIdentity is { } finalIdentity) ValidateText(finalIdentity, 256);
        if (document.QuarantineIdentity is { } quarantineIdentity) ValidateText(quarantineIdentity, 256);
        if (document.PartialOwned != (document.PartialIdentity is not null) ||
            document.FinalOwned != (document.FinalIdentity is not null))
            throw new InvalidDataException();
        if (document.LastModifiedUtc is { } lastModified &&
            lastModified == default)
            throw new InvalidDataException();

        ValidateIdentifier(document.ArtifactId);
        ValidateFingerprint(document.PlanFingerprint);
        ValidateFingerprint(document.ArtifactIdentityFingerprint);
        ValidateFingerprint(document.ManifestSha256);
        ValidateFingerprint(document.ExpectedSha256);
        ValidateFingerprint(document.RightsAuthorizationFingerprint);
        ValidateFingerprint(document.SetupPlanFingerprint);
        ValidateFingerprint(document.SetupDesiredStateFingerprint);
        ValidateFingerprint(document.HostFactsFingerprint);
        ValidateFingerprint(document.ArtifactFactsFingerprint);
        ValidateFingerprint(document.SetupJournalVersion);
        ValidateText(document.SourceUrl, 2048);
        ValidateText(document.SourceRevision, 128);
        ValidateText(document.SetupJournalPath, 1024);
        ValidateText(document.DestinationPath, 1024);
        ValidateText(document.PartialPath, 1024);
        if (requireIntegrity)
            ValidateFingerprint(document.IntegritySha256);
        else if (document.IntegritySha256.Length != 0)
            throw new InvalidDataException();
        if (document.ComputedSha256 is { } computed)
            ValidateFingerprint(computed);
        if (document.EntityTag is { } entityTag)
        {
            if (entityTag.Length > 256)
                throw new InvalidDataException();
            EntityTagHeaderValue parsed;
            try
            {
                parsed = EntityTagHeaderValue.Parse(entityTag);
            }
            catch (FormatException)
            {
                throw new InvalidDataException();
            }
            if (parsed.IsWeak || parsed.Tag == "*")
                throw new InvalidDataException();
        }

        var hasValidator = document.EntityTag is not null || document.LastModifiedUtc is not null;
        if (document.State == ArtifactAcquisitionJournalState.Prepared &&
                (document.PersistedBytes != 0 ||
                 document.RangeSupported ||
                 document.PartialOwned ||
                 document.FinalOwned ||
                 document.ComputedSha256 is not null ||
                 document.LastFailure is not null ||
                 hasValidator) ||
            document.State == ArtifactAcquisitionJournalState.Downloading &&
                (!hasValidator || document.TerminalOrigin is null ||
                 document.FinalOwned ||
                 document.ComputedSha256 is not null ||
                 document.LastFailure is not null) ||
            document.State == ArtifactAcquisitionJournalState.Interrupted &&
                (!hasValidator || document.TerminalOrigin is null ||
                 document.FinalOwned ||
                 document.LastFailure is null) ||
            document.State == ArtifactAcquisitionJournalState.Verified &&
                (document.PersistedBytes != document.ExpectedBytes ||
                 document.TerminalOrigin is null ||
                 !hasValidator ||
                 !document.PartialOwned ||
                 document.FinalOwned ||
                 document.ComputedSha256 != document.ExpectedSha256 ||
                 document.LastFailure is not null) ||
            document.State == ArtifactAcquisitionJournalState.Finalized &&
                (document.PersistedBytes != document.ExpectedBytes ||
                 document.TerminalOrigin is null ||
                 !hasValidator ||
                 document.PartialOwned ||
                 !document.FinalOwned ||
                 document.ComputedSha256 != document.ExpectedSha256 ||
                 document.LastFailure is not null) ||
            document.State == ArtifactAcquisitionJournalState.Failed &&
                (document.FinalOwned || document.LastFailure is null) ||
            document.State == ArtifactAcquisitionJournalState.Quarantined &&
                (document.QuarantineIdentity is null || document.PartialOwned || document.FinalOwned ||
                 document.PersistedBytes != 0 || document.LastFailure is not null) ||
            document.State == ArtifactAcquisitionJournalState.Quarantining &&
                (!document.FinalOwned || document.PartialOwned || document.QuarantineIdentity is not null ||
                 document.ComputedSha256 is null || document.LastFailure is not null))
            throw new InvalidDataException();
    }

    private static void ValidateIdentifier(string? value)
    {
        if (value is null ||
            value.Length is < 1 or > 64 ||
            value[0] is not (>= 'a' and <= 'z' or >= '0' and <= '9') ||
            value.Any(character =>
                character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.')))
            throw new InvalidDataException();
    }

    private static void ValidateFingerprint(string? value)
    {
        if (value is null ||
            value.Length != 64 ||
            value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidDataException();
    }

    private static void ValidateText(string? value, int maximum)
    {
        if (value is null ||
            value.Length is < 1 ||
            value.Length > maximum ||
            value.Any(char.IsControl))
            throw new InvalidDataException();
    }

    private static void RejectDuplicateProperties(ReadOnlySpan<byte> bytes)
    {
        using var json = JsonDocument.Parse(
            bytes.ToArray(),
            new JsonDocumentOptions { MaxDepth = 8 });
        Visit(json.RootElement);

        static void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                        throw new InvalidDataException();
                    Visit(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                    Visit(item);
            }
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 8,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };
        options.Converters.Add(new ExactEnumConverter<ArtifactAcquisitionJournalState>());
        options.Converters.Add(new ExactEnumConverter<ArtifactAcquisitionJournalPurpose>());
        options.Converters.Add(new ExactEnumConverter<ArtifactAcquisitionFailure>());
        options.MakeReadOnly();
        return options;
    }

    internal sealed class ExactEnumConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        public override T Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
                throw new JsonException();
            var text = reader.GetString();
            return text is not null &&
                Enum.TryParse<T>(text, ignoreCase: false, out var value) &&
                Enum.IsDefined(value) &&
                value.ToString() == text
                ? value
                : throw new JsonException();
        }

        public override void Write(
            Utf8JsonWriter writer,
            T value,
            JsonSerializerOptions options)
        {
            if (!Enum.IsDefined(value))
                throw new JsonException();
            writer.WriteStringValue(value.ToString());
        }
    }
}
