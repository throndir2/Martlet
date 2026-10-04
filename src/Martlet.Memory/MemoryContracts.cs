using System.Text;

namespace Martlet.Memory;

public enum MemoryConsentDecision
{
    No,
    Allow
}

public enum MemorySourceKind
{
    UserEntry,
    UserReviewedImport,
    Conversation
}

public enum MemoryRetentionKind
{
    UntilDeleted,
    ExpiresAt
}

public static class MemoryLimits
{
    public const int SchemaVersion = 1;
    public const int MaximumFacts = 512;
    public const int MaximumContentCharacters = 4096;
    public const int MaximumContentUtf8Bytes = 8192;
    public const int MaximumQueryCharacters = 256;
    public const int MaximumQueryUtf8Bytes = 512;
    public const int MaximumQueryTerms = 24;
    public const int MaximumResults = 20;
    public const int MaximumCachedQueries = 64;
    public const int MaximumStoreBytes = 8 * 1024 * 1024;
    public const int MaximumExportBytes = MaximumStoreBytes;
    public const long MaximumRevision = long.MaxValue - 1;
    public static readonly TimeSpan MaximumExpiringRetention = TimeSpan.FromDays(366);
}

public sealed record MemoryProvenance
{
    public int SchemaVersion { get; init; } = MemoryLimits.SchemaVersion;
    public required MemorySourceKind SourceKind { get; init; }
    public required Guid ConsentId { get; init; }
    public required DateTimeOffset ObservedAtUtc { get; init; }

    public static MemoryProvenance UserEntry(Guid consentId, DateTimeOffset observedAtUtc) =>
        new() { SourceKind = MemorySourceKind.UserEntry, ConsentId = consentId, ObservedAtUtc = observedAtUtc };

    public static MemoryProvenance UserReviewedImport(Guid consentId, DateTimeOffset observedAtUtc) =>
        new() { SourceKind = MemorySourceKind.UserReviewedImport, ConsentId = consentId, ObservedAtUtc = observedAtUtc };

    /// <summary>Picked out of a conversation exchange while memory was ON.</summary>
    public static MemoryProvenance Conversation(Guid consentId, DateTimeOffset observedAtUtc) =>
        new() { SourceKind = MemorySourceKind.Conversation, ConsentId = consentId, ObservedAtUtc = observedAtUtc };

    internal void Validate()
    {
        MemoryGuard.Require(SchemaVersion == MemoryLimits.SchemaVersion, MemoryFailure.UnsupportedVersion);
        MemoryGuard.Defined(SourceKind);
        MemoryGuard.Require(ConsentId != Guid.Empty);
        MemoryGuard.Utc(ObservedAtUtc);
    }

    public override string ToString() =>
        $"MemoryProvenance {{ SourceKind = {SourceKind}, ConsentId = {ConsentId}, ObservedAtUtc = {ObservedAtUtc:O} }}";
}

public sealed record MemoryRetention
{
    public int SchemaVersion { get; init; } = MemoryLimits.SchemaVersion;
    public required MemoryRetentionKind Kind { get; init; }
    public DateTimeOffset? ExpiresAtUtc { get; init; }

    public static MemoryRetention UntilDeleted() =>
        new() { Kind = MemoryRetentionKind.UntilDeleted };

    public static MemoryRetention ExpiringAt(DateTimeOffset expiresAtUtc) =>
        new() { Kind = MemoryRetentionKind.ExpiresAt, ExpiresAtUtc = expiresAtUtc };

    internal void ValidatePersisted(DateTimeOffset createdAtUtc)
    {
        MemoryGuard.Require(SchemaVersion == MemoryLimits.SchemaVersion, MemoryFailure.UnsupportedVersion);
        MemoryGuard.Defined(Kind);
        if (Kind == MemoryRetentionKind.UntilDeleted)
            MemoryGuard.Require(ExpiresAtUtc is null);
        else
        {
            if (ExpiresAtUtc is not { } expires)
                throw new MemoryException(MemoryFailure.InvalidData);
            MemoryGuard.Utc(expires);
            MemoryGuard.Require(expires > createdAtUtc);
        }
    }

    internal void ValidateForWrite(DateTimeOffset now)
    {
        ValidatePersisted(now);
        if (Kind == MemoryRetentionKind.ExpiresAt)
            MemoryGuard.Require(ExpiresAtUtc <= now + MemoryLimits.MaximumExpiringRetention);
    }

    internal bool IsExpired(DateTimeOffset now) =>
        Kind == MemoryRetentionKind.ExpiresAt && ExpiresAtUtc <= now;

    /// <summary>Whether the fact is gone by <paramref name="now"/> (it is removed on the store's next operation).</summary>
    public bool HasExpired(DateTimeOffset now) => IsExpired(now);
}

public sealed record MemoryFact
{
    public int SchemaVersion { get; init; } = MemoryLimits.SchemaVersion;
    public required Guid Id { get; init; }
    public required long Revision { get; init; }
    public required string Content { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
    public required MemoryProvenance CreatedFrom { get; init; }
    public required MemoryProvenance LastModifiedBy { get; init; }
    public required MemoryRetention Retention { get; init; }

    internal void ValidatePersisted()
    {
        MemoryGuard.Require(SchemaVersion == MemoryLimits.SchemaVersion, MemoryFailure.UnsupportedVersion);
        MemoryGuard.Require(Id != Guid.Empty && Revision is > 0 and <= MemoryLimits.MaximumRevision,
            MemoryFailure.CorruptStore);
        ValidateContent(Content);
        MemoryGuard.Utc(CreatedAtUtc);
        MemoryGuard.Utc(UpdatedAtUtc);
        MemoryGuard.Require(UpdatedAtUtc >= CreatedAtUtc);
        MemoryGuard.Require(CreatedFrom is not null && LastModifiedBy is not null && Retention is not null);
        CreatedFrom!.Validate();
        LastModifiedBy!.Validate();
        MemoryGuard.Require(CreatedFrom.ObservedAtUtc <= CreatedAtUtc + TimeSpan.FromMinutes(5) &&
            LastModifiedBy.ObservedAtUtc <= UpdatedAtUtc + TimeSpan.FromMinutes(5));
        Retention!.ValidatePersisted(CreatedAtUtc);
    }

    internal static void ValidateContent(string content)
    {
        MemoryGuard.Require(content is { Length: > 0 } && !string.IsNullOrWhiteSpace(content) &&
            !content.Contains('\0'));
        MemoryGuard.Require(content.Length <= MemoryLimits.MaximumContentCharacters, MemoryFailure.LimitExceeded);
        MemoryGuard.Require(content.All(character =>
            !char.IsControl(character) || character is '\r' or '\n' or '\t'));
        try
        {
            MemoryGuard.Require(new UTF8Encoding(false, true).GetByteCount(content) <= MemoryLimits.MaximumContentUtf8Bytes,
                MemoryFailure.LimitExceeded);
        }
        catch (EncoderFallbackException)
        {
            throw new MemoryException(MemoryFailure.InvalidData);
        }
    }

    public override string ToString() =>
        $"MemoryFact {{ Id = {Id}, Revision = {Revision}, Content = [redacted], CreatedAtUtc = {CreatedAtUtc:O}, UpdatedAtUtc = {UpdatedAtUtc:O} }}";
}

public sealed record SaveFactRequest
{
    public required string Content { get; init; }
    public required MemoryProvenance Provenance { get; init; }
    public required MemoryRetention Retention { get; init; }

    public override string ToString() => "Explicit memory fact save request (content omitted)";
}

public sealed record EditFactRequest
{
    public required Guid Id { get; init; }
    public required long ExpectedRevision { get; init; }
    public required string Content { get; init; }
    public required MemoryProvenance Provenance { get; init; }
    public required MemoryRetention Retention { get; init; }

    public override string ToString() =>
        $"Explicit memory fact edit request {{ Id = {Id}, ExpectedRevision = {ExpectedRevision}, Content = [redacted] }}";
}

public sealed record DeleteFactRequest
{
    public required Guid Id { get; init; }
    public required long ExpectedRevision { get; init; }
    public required Guid ConsentId { get; init; }
}

public sealed record MemoryMutationReceipt(long StoreRevision, MemoryFact Fact);

/// <summary>Facts the owner's other computers share, to put into this store exactly as they are (each the newest version every
/// computer agreed on, as Martlet.Memory's fact JSON from <see cref="MemoryFactJson"/>), and facts to forget.</summary>
public sealed record MemoryMergeRequest
{
    public required IReadOnlyList<string> Facts { get; init; }
    public required IReadOnlyCollection<Guid> Forget { get; init; }

    public override string ToString() => $"Memory merge request {{ Facts = {Facts?.Count}, Forget = {Forget?.Count}, Content = [redacted] }}";
}

public sealed record MemoryMergeReceipt(int Saved, int Forgotten, int Expired, long StoreRevision);

/// <summary>A fact as JSON, the form in which facts travel between the owner's computers. <see cref="Read"/> is as strict as
/// the store is with its own file and throws <see cref="MemoryException"/>.</summary>
public static class MemoryFactJson
{
    public static string Write(MemoryFact fact) => MemoryJson.WriteFact(fact);

    public static MemoryFact Read(string json) => MemoryJson.ReadFact(json);
}

public sealed record MemoryDeleteReceipt(Guid FactId, long DeletedFactRevision, long StoreRevision, Guid ConsentId);

public sealed record MemoryExpiryReceipt(int DeletedFacts, long StoreRevision);

public sealed class MemoryInspection
{
    internal MemoryInspection(Guid storeId, long storeRevision, DateTimeOffset inspectedAtUtc, MemoryFact[] facts)
    {
        StoreId = storeId;
        StoreRevision = storeRevision;
        InspectedAtUtc = inspectedAtUtc;
        Facts = Array.AsReadOnly(facts);
    }

    /// <summary>Identifies this store (a new memory folder starts a new one).</summary>
    public Guid StoreId { get; }
    public long StoreRevision { get; }
    public DateTimeOffset InspectedAtUtc { get; }
    public IReadOnlyList<MemoryFact> Facts { get; }
}

public sealed record MemoryQuery
{
    public required string Text { get; init; }
    public int MaximumResults { get; init; } = 5;

    public static MemoryQuery? TryFromBoundedSource(string source, int maximumResults = 5)
    {
        MemoryGuard.Require(maximumResults is >= 1 and <= MemoryLimits.MaximumResults);
        var text = LexicalIndex.TryBoundedQueryText(source);
        if (text is null)
            return null;
        return new()
        {
            Text = text,
            MaximumResults = maximumResults
        };
    }

    public override string ToString() =>
        $"MemoryQuery {{ Text = [redacted], MaximumResults = {MaximumResults} }}";
}

public sealed record MemoryRetrievalHit(MemoryFact Fact, double Score, int MatchedTerms);

public sealed class MemoryRetrievalResult
{
    internal MemoryRetrievalResult(long storeRevision, DateTimeOffset retrievedAtUtc, MemoryRetrievalHit[] hits)
    {
        StoreRevision = storeRevision;
        RetrievedAtUtc = retrievedAtUtc;
        Hits = Array.AsReadOnly(hits);
    }

    public long StoreRevision { get; }
    public DateTimeOffset RetrievedAtUtc { get; }
    public IReadOnlyList<MemoryRetrievalHit> Hits { get; }
}

public sealed class MemoryStoreActivationPreview
{
    private MemoryStoreActivationPreview(Guid id, string directoryPath)
    {
        Id = id;
        DirectoryPath = directoryPath;
    }

    public Guid Id { get; }
    public string DirectoryPath { get; }
    public bool EnabledByDefault => false;

    public static MemoryStoreActivationPreview Create(string absoluteDirectory) =>
        new(Guid.NewGuid(), MemoryPaths.NormalizeLocalPath(absoluteDirectory, directory: true, inspectFileSystem: false));

    public void ValidateLocalScope() =>
        MemoryPaths.NormalizeLocalPath(DirectoryPath, directory: true, inspectFileSystem: true);

    public MemoryStoreAuthorization Authorize(MemoryConsentDecision decision = MemoryConsentDecision.No)
    {
        MemoryGuard.Defined(decision);
        MemoryGuard.Require(decision == MemoryConsentDecision.Allow, MemoryFailure.ConsentRequired);
        return new MemoryStoreAuthorization(Id, DirectoryPath);
    }

    public override string ToString() => "Memory activation preview (OFF by default; local path omitted)";
}

public sealed class MemoryStoreAuthorization
{
    internal MemoryStoreAuthorization(Guid previewId, string directoryPath)
    {
        PreviewId = previewId;
        DirectoryPath = directoryPath;
    }

    internal Guid PreviewId { get; }
    internal string DirectoryPath { get; }
    internal int Used;

    public override string ToString() => "Explicit local memory activation approval (path omitted)";
}
