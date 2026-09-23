using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Martlet.Host.Setup;

internal enum SetupJournalPurpose { LocalReview, UnqualifiedExecution }
internal enum SetupJournalState { Approved, Running, Interrupted, Blocked, Completed, ReviewRecorded }
internal enum SetupJournalStepStatus { Pending, Running, Completed, Failed, Conflict, Reviewed }
internal enum SetupCompletionOrigin { None, Executed, Reconciled }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SetupJournalSupersession
{
    public required string PlanFingerprint { get; init; }
    public required string HostFactsFingerprint { get; init; }
    public required long JournalRevision { get; init; }
    public required DateTimeOffset AcceptedAtUtc { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SetupJournalStep
{
    public required string Id { get; init; }
    public required SetupJournalStepStatus Status { get; init; }
    public required int Attempts { get; init; }
    public required SetupCompletionOrigin CompletionOrigin { get; init; }
    public required string? ObservationFingerprint { get; init; }
    public required DateTimeOffset? CompletedAtUtc { get; init; }
    public required SetupFailure? LastFailure { get; init; }
    public required string? ReviewFingerprint { get; init; }
    public required DateTimeOffset? ReviewedAtUtc { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SetupJournalDocument
{
    public required int FormatVersion { get; init; }
    public required SetupJournalPurpose Purpose { get; init; }
    public required Guid JournalId { get; init; }
    public required string PlanId { get; init; }
    public required string PlanFingerprint { get; init; }
    public required string DesiredStateFingerprint { get; init; }
    public required string ConfigurationFingerprint { get; init; }
    public required string HostFactsFingerprint { get; init; }
    public required string ArtifactFactsFingerprint { get; init; }
    public required long Revision { get; init; }
    public required SetupJournalState State { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
    public required SetupConsentScope[] ApprovedScopes { get; init; }
    public required SetupPrivilege MaximumPrivilege { get; init; }
    public required bool RollbackAvailable { get; init; }
    public required SetupFailure? LastFailure { get; init; }
    public required SetupJournalSupersession[] Supersessions { get; init; }
    public required SetupJournalStep[] Steps { get; init; }
    public required string IntegritySha256 { get; init; }

    internal static SetupJournalDocument Create(SetupPlan plan, SetupApproval approval, DateTimeOffset now) => new()
    {
        FormatVersion = 2,
        Purpose = SetupJournalPurpose.UnqualifiedExecution,
        JournalId = Guid.NewGuid(),
        PlanId = plan.Id,
        PlanFingerprint = plan.Fingerprint,
        DesiredStateFingerprint = plan.DesiredStateFingerprint,
        ConfigurationFingerprint = plan.ConfigurationFingerprint,
        HostFactsFingerprint = plan.HostFactsFingerprint,
        ArtifactFactsFingerprint = plan.ArtifactFactsFingerprint,
        Revision = 0,
        State = SetupJournalState.Approved,
        CreatedAtUtc = now,
        UpdatedAtUtc = now,
        ApprovedScopes = approval.ApprovedScopes.ToArray(),
        MaximumPrivilege = approval.MaximumPrivilege,
        RollbackAvailable = false,
        LastFailure = null,
        Supersessions = [],
        Steps = plan.Steps.Select(step => new SetupJournalStep
        {
            Id = step.Id,
            Status = SetupJournalStepStatus.Pending,
            Attempts = 0,
            CompletionOrigin = SetupCompletionOrigin.None,
            ObservationFingerprint = null,
            CompletedAtUtc = null,
            LastFailure = null,
            ReviewFingerprint = null,
            ReviewedAtUtc = null
        }).ToArray(),
        IntegritySha256 = ""
    };
}

internal static class SetupJournalCodec
{
    internal const int MaximumBytes = 262_144;
    private static readonly JsonSerializerOptions Options = CreateOptions();

    internal static byte[] Write(SetupJournalDocument document)
    {
        Validate(document, requireRevision: true, requireIntegrity: false);
        var unsealed = JsonSerializer.SerializeToUtf8Bytes(document with { IntegritySha256 = "" }, Options);
        var hash = Convert.ToHexStringLower(SHA256.HashData(unsealed));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document with { IntegritySha256 = hash }, Options);
        if (bytes.Length > MaximumBytes) throw new SetupException(SetupFailure.JournalTooLarge);
        return bytes;
    }

    internal static SetupJournalDocument Read(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > MaximumBytes) throw new SetupException(SetupFailure.JournalTooLarge);
        try
        {
            _ = new UTF8Encoding(false, true).GetCharCount(bytes.Span);
            RejectDuplicateProperties(bytes.Span);
            var document = JsonSerializer.Deserialize<SetupJournalDocument>(bytes.Span, Options)
                ?? throw new InvalidDataException();
            Validate(document, requireRevision: true, requireIntegrity: true);
            var unsealed = JsonSerializer.SerializeToUtf8Bytes(document with { IntegritySha256 = "" }, Options);
            var expected = Convert.ToHexStringLower(SHA256.HashData(unsealed));
            if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(document.IntegritySha256),
                Convert.FromHexString(expected)))
                throw new InvalidDataException();
            return document;
        }
        catch (JsonException)
        {
            throw new SetupException(SetupFailure.JournalCorrupt);
        }
        catch (InvalidDataException)
        {
            throw new SetupException(SetupFailure.JournalCorrupt);
        }
        catch (FormatException)
        {
            throw new SetupException(SetupFailure.JournalCorrupt);
        }
        catch (DecoderFallbackException)
        {
            throw new SetupException(SetupFailure.JournalCorrupt);
        }
    }

    private static void Validate(SetupJournalDocument document, bool requireRevision, bool requireIntegrity)
    {
        if (document.FormatVersion != 2 || !Enum.IsDefined(document.Purpose) || document.JournalId == Guid.Empty ||
            document.CreatedAtUtc == default || document.UpdatedAtUtc < document.CreatedAtUtc ||
            requireRevision && document.Revision < 1 ||
            !Enum.IsDefined(document.State) || !Enum.IsDefined(document.MaximumPrivilege) ||
            document.PlanId is null || document.PlanFingerprint is null ||
            document.DesiredStateFingerprint is null || document.ConfigurationFingerprint is null ||
            document.HostFactsFingerprint is null || document.ArtifactFactsFingerprint is null ||
            document.IntegritySha256 is null ||
            document.RollbackAvailable || document.Steps is null || document.Steps.Length is < 1 or > 32 ||
            document.Steps.Any(step => step is null) ||
            document.Supersessions is null || document.Supersessions.Length > 16 ||
            document.Supersessions.Any(item => item is null) ||
            document.ApprovedScopes is null || document.ApprovedScopes.Length is < 1 or > 16 ||
            document.ApprovedScopes.Any(scope => !Enum.IsDefined(scope)) ||
            document.ApprovedScopes.Distinct().Count() != document.ApprovedScopes.Length ||
            document.ApprovedScopes.Order().SequenceEqual(document.ApprovedScopes) is false ||
            document.LastFailure is { } failure && !Enum.IsDefined(failure))
            throw new InvalidDataException();
        ValidateIdentifier(document.PlanId);
        ValidateFingerprint(document.PlanFingerprint);
        ValidateFingerprint(document.DesiredStateFingerprint);
        ValidateFingerprint(document.ConfigurationFingerprint);
        ValidateFingerprint(document.HostFactsFingerprint);
        ValidateFingerprint(document.ArtifactFactsFingerprint);
        if (requireIntegrity) ValidateFingerprint(document.IntegritySha256);
        else if (document.IntegritySha256.Length != 0) throw new InvalidDataException();
        if (document.Steps.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != document.Steps.Length)
            throw new InvalidDataException();
        long previousRevision = 0;
        foreach (var supersession in document.Supersessions)
        {
            ValidateFingerprint(supersession.PlanFingerprint);
            ValidateFingerprint(supersession.HostFactsFingerprint);
            if (supersession.JournalRevision <= previousRevision ||
                supersession.JournalRevision >= document.Revision ||
                supersession.AcceptedAtUtc < document.CreatedAtUtc ||
                supersession.AcceptedAtUtc > document.UpdatedAtUtc)
                throw new InvalidDataException();
            previousRevision = supersession.JournalRevision;
        }
        foreach (var step in document.Steps)
        {
            if (step.Id is null) throw new InvalidDataException();
            ValidateIdentifier(step.Id);
            if (!Enum.IsDefined(step.Status) || !Enum.IsDefined(step.CompletionOrigin) ||
                step.Attempts is < 0 or > 100 ||
                step.LastFailure is { } stepFailure && !Enum.IsDefined(stepFailure) ||
                step.Status == SetupJournalStepStatus.Completed != (step.CompletedAtUtc is not null) ||
                step.Status == SetupJournalStepStatus.Completed != (step.ObservationFingerprint is not null) ||
                step.Status == SetupJournalStepStatus.Completed != (step.CompletionOrigin != SetupCompletionOrigin.None) ||
                step.CompletionOrigin == SetupCompletionOrigin.Executed && step.Attempts == 0 ||
                step.Status == SetupJournalStepStatus.Running && step.Attempts == 0 ||
                step.Status is SetupJournalStepStatus.Failed or SetupJournalStepStatus.Conflict &&
                    step.LastFailure is null ||
                step.Status is SetupJournalStepStatus.Pending or SetupJournalStepStatus.Running or SetupJournalStepStatus.Completed &&
                    step.LastFailure is not null ||
                step.CompletedAtUtc < document.CreatedAtUtc || step.CompletedAtUtc > document.UpdatedAtUtc)
                throw new InvalidDataException();
            if (step.ObservationFingerprint is { } fingerprint) ValidateFingerprint(fingerprint);
            if (document.Purpose == SetupJournalPurpose.LocalReview)
            {
                if (step.Status is not (SetupJournalStepStatus.Pending or SetupJournalStepStatus.Reviewed) ||
                    step.Attempts != 0 || step.CompletionOrigin != SetupCompletionOrigin.None ||
                    step.CompletedAtUtc is not null || step.ObservationFingerprint is not null || step.LastFailure is not null ||
                    (step.Status == SetupJournalStepStatus.Reviewed) != (step.ReviewFingerprint is not null) ||
                    (step.Status == SetupJournalStepStatus.Reviewed) != (step.ReviewedAtUtc is not null) ||
                    step.ReviewedAtUtc < document.CreatedAtUtc || step.ReviewedAtUtc > document.UpdatedAtUtc ||
                    step.ReviewFingerprint is not null &&
                        step.ReviewFingerprint != FingerprintBuilder.Create(document.PlanFingerprint, step.Id))
                    throw new InvalidDataException();
            }
            else if (step.ReviewFingerprint is not null || step.ReviewedAtUtc is not null ||
                step.Status == SetupJournalStepStatus.Reviewed)
                throw new InvalidDataException();
        }
        if (document.Purpose == SetupJournalPurpose.LocalReview
            ? document.MaximumPrivilege != SetupPrivilege.None ||
              !document.ApprovedScopes.SequenceEqual([SetupConsentScope.LocalJournal]) ||
              document.LastFailure is not null ||
              document.State is not (SetupJournalState.Approved or SetupJournalState.Running or SetupJournalState.ReviewRecorded) ||
              document.State == SetupJournalState.ReviewRecorded && document.Steps.Any(s => s.Status != SetupJournalStepStatus.Reviewed)
            : document.State == SetupJournalState.ReviewRecorded)
            throw new InvalidDataException();
        if (document.State == SetupJournalState.Completed &&
                (document.LastFailure is not null ||
                 document.Steps.Any(step => step.Status != SetupJournalStepStatus.Completed)) ||
            document.State == SetupJournalState.Approved &&
                (document.LastFailure is not null ||
                 document.Steps.Any(step => step.Status != SetupJournalStepStatus.Pending)) ||
            document.State is SetupJournalState.Blocked or SetupJournalState.Interrupted &&
                document.LastFailure is null)
            throw new InvalidDataException();
    }

    private static void ValidateIdentifier(string? value)
    {
        if (value is null || value.Length is < 1 or > 64 ||
            value[0] is not (>= 'a' and <= 'z' or >= '0' and <= '9') ||
            value.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.')))
            throw new InvalidDataException();
    }

    private static void ValidateFingerprint(string? value)
    {
        if (value is null || value.Length != 64 ||
            value.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidDataException();
    }

    private static void RejectDuplicateProperties(ReadOnlySpan<byte> bytes)
    {
        using var json = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
        Visit(json.RootElement);
        static void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    string name;
                    try { name = property.Name; }
                    catch (InvalidOperationException) { throw new InvalidDataException(); }
                    if (!names.Add(name)) throw new InvalidDataException();
                    Visit(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray()) Visit(item);
            }
            else if (element.ValueKind == JsonValueKind.String)
            {
                try { _ = element.GetString(); }
                catch (InvalidOperationException) { throw new InvalidDataException(); }
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
            MaxDepth = 16,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };
        options.Converters.Add(new ExactEnumConverter<SetupJournalState>());
        options.Converters.Add(new ExactEnumConverter<SetupJournalPurpose>());
        options.Converters.Add(new ExactEnumConverter<SetupJournalStepStatus>());
        options.Converters.Add(new ExactEnumConverter<SetupCompletionOrigin>());
        options.Converters.Add(new ExactEnumConverter<SetupConsentScope>());
        options.Converters.Add(new ExactEnumConverter<SetupPrivilege>());
        options.Converters.Add(new ExactEnumConverter<SetupFailure>());
        options.MakeReadOnly();
        return options;
    }

    private sealed class ExactEnumConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String) throw new JsonException();
            var text = reader.GetString();
            return text is not null && Enum.TryParse<T>(text, ignoreCase: false, out var value) &&
                Enum.IsDefined(value) && value.ToString() == text
                ? value
                : throw new JsonException();
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            if (!Enum.IsDefined(value)) throw new JsonException();
            writer.WriteStringValue(value.ToString());
        }
    }
}
