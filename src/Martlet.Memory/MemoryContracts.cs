using System.Text;
using System.Text.Json.Serialization;

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
    public const int MaximumFacts = 100_000;
    public const int MaximumContentCharacters = 32_768;
    // Three UTF-8 bytes per UTF-16 character: the byte bound never refuses text the character bound allows.
    public const int MaximumContentUtf8Bytes = 3 * MaximumContentCharacters;
    /// <summary>One fact's JSON as it travels between computers (<see cref="MemoryFactJson"/>): the longest fact with every
    /// character escaped (\uXXXX) and its fields.</summary>
    public const int MaximumFactJsonBytes = 256 * 1024;
    public const int MaximumVoiceIdCharacters = 64;
    /// <summary>A fact's hidden search words (<see cref="MemoryFact.SearchWords"/>): one line of at most this many characters.</summary>
    public const int MaximumSearchWordsCharacters = 512;
    public const int MaximumQueryCharacters = 256;
    public const int MaximumQueryUtf8Bytes = 512;
    public const int MaximumQueryTerms = 24;
    public const int MaximumResults = 20;
    public const int MaximumCachedQueries = 64;
    // The store is one JSON file read whole into memory, so it keeps a bound: room for MaximumFacts facts of a few hundred
    // characters each.
    public const int MaximumStoreBytes = 256 * 1024 * 1024;
    public const int MaximumExportBytes = MaximumStoreBytes;
    public const long MaximumRevision = long.MaxValue - 1;
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

    internal void ValidateForWrite(DateTimeOffset now) => ValidatePersisted(now);

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
    /// <summary>The person this fact belongs to (who said it, or who it is about): the ID of a voice in Martlet's voice list, or
    /// null for a fact about no one in particular. Martlet.Memory only checks its form; a fact without one is written exactly
    /// as before voices existed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VoiceId { get; init; }
    /// <summary>Hidden search words (topics, synonyms and related words, comma separated) that recall indexes with the content, so
    /// a question in other words finds the fact ("cook" finds "The user is vegetarian." through "diet, food, meat, cooking"). They
    /// are never sent as the fact's text. Made in the background; null when the fact has none yet. Like
    /// <see cref="VoiceId"/>, a fact without them is written exactly as before.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SearchWords { get; init; }
    /// <summary>Where the fact matters (<see cref="MemoryActivity"/>): null for everywhere, "gaming" while the user plays any game,
    /// "gaming/Palworld" only while they play Palworld. Recall leaves out a fact for another activity. Like <see cref="VoiceId"/>,
    /// a fact without one (every fact made before activities) is written exactly as before and matters everywhere.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Activity { get; init; }
    /// <summary>When the fact stopped being true: set on an earlier version kept as history after a correction, or on a fact a
    /// newer fact made outdated. A replaced fact is never recalled as current; null for a current fact.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? ReplacedAtUtc { get; init; }
    /// <summary>The fact that replaced this one (it may have been deleted since), or null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ReplacedBy { get; init; }

    /// <summary>Whether this is history: a fact that stopped being true (<see cref="ReplacedAtUtc"/>), kept but never recalled as
    /// current.</summary>
    [JsonIgnore]
    public bool IsHistory => ReplacedAtUtc is not null;

    internal void ValidatePersisted()
    {
        MemoryGuard.Require(SchemaVersion == MemoryLimits.SchemaVersion, MemoryFailure.UnsupportedVersion);
        MemoryGuard.Require(Id != Guid.Empty && Revision is > 0 and <= MemoryLimits.MaximumRevision,
            MemoryFailure.CorruptStore);
        ValidateContent(Content);
        ValidateVoiceId(VoiceId);
        ValidateSearchWords(SearchWords);
        MemoryActivity.Validate(Activity);
        MemoryGuard.Utc(CreatedAtUtc);
        MemoryGuard.Utc(UpdatedAtUtc);
        MemoryGuard.Require(UpdatedAtUtc >= CreatedAtUtc);
        if (ReplacedAtUtc is { } replaced)
        {
            MemoryGuard.Utc(replaced);
            MemoryGuard.Require(replaced >= CreatedAtUtc && replaced <= UpdatedAtUtc + TimeSpan.FromMinutes(5));
        }
        MemoryGuard.Require(ReplacedBy is null || ReplacedAtUtc is not null && ReplacedBy != Guid.Empty && ReplacedBy != Id);
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

    /// <summary>Null, or 1-64 ASCII letters, digits, '-' or '_' (a voice list ID).</summary>
    internal static void ValidateVoiceId(string? voiceId) =>
        MemoryGuard.Require(voiceId is null || voiceId.Length is > 0 and <= MemoryLimits.MaximumVoiceIdCharacters &&
            voiceId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'));

    /// <summary>Null, or one line (no control characters) of 1-512 characters with a letter or digit.</summary>
    internal static void ValidateSearchWords(string? words) =>
        MemoryGuard.Require(words is null || words.Length is > 0 and <= MemoryLimits.MaximumSearchWordsCharacters &&
            !words.Any(char.IsControl) && words.Any(char.IsLetterOrDigit) && !char.IsSurrogate(words[^1]) &&
            !char.IsWhiteSpace(words[0]) && !char.IsWhiteSpace(words[^1]));

    public override string ToString() =>
        $"MemoryFact {{ Id = {Id}, Revision = {Revision}, Content = [redacted], CreatedAtUtc = {CreatedAtUtc:O}, UpdatedAtUtc = {UpdatedAtUtc:O}, History = {IsHistory} }}";
}

public sealed record SaveFactRequest
{
    public required string Content { get; init; }
    public required MemoryProvenance Provenance { get; init; }
    public required MemoryRetention Retention { get; init; }
    /// <summary>Whose fact it is (<see cref="MemoryFact.VoiceId"/>); null for no one in particular.</summary>
    public string? VoiceId { get; init; }
    /// <summary>Hidden search words for the fact (<see cref="MemoryFact.SearchWords"/>), or null.</summary>
    public string? SearchWords { get; init; }
    /// <summary>Where the fact matters (<see cref="MemoryFact.Activity"/>); null for everywhere.</summary>
    public string? Activity { get; init; }

    public override string ToString() => "Explicit memory fact save request (content omitted)";
}

public sealed record EditFactRequest
{
    public required Guid Id { get; init; }
    public required long ExpectedRevision { get; init; }
    public required string Content { get; init; }
    public required MemoryProvenance Provenance { get; init; }
    public required MemoryRetention Retention { get; init; }
    /// <summary>Whose fact it is after the edit (<see cref="MemoryFact.VoiceId"/>); null for no one in particular. Required, so an
    /// edit never drops who a fact belongs to by accident: pass the fact's own to keep it.</summary>
    public required string? VoiceId { get; init; }
    /// <summary>New search words for the fact. Null keeps its words when the content stays the same and drops them when the
    /// content changes (they described the old words; the background upkeep makes new ones).</summary>
    public string? SearchWords { get; init; }
    /// <summary>A correction: when the content changes, the earlier version stays as history (a new fact with the old content,
    /// <see cref="MemoryFact.ReplacedAtUtc"/> now and <see cref="MemoryFact.ReplacedBy"/> this fact). A full store skips the
    /// history copy rather than the correction.</summary>
    public bool KeepHistory { get; init; }
    /// <summary>Gives the fact <see cref="Activity"/>. Without it the fact keeps where it matters, so an edit never moves it by
    /// accident.</summary>
    public bool SetActivity { get; init; }
    /// <summary>Where the fact matters after the edit (<see cref="MemoryFact.Activity"/>; null for everywhere), when
    /// <see cref="SetActivity"/>.</summary>
    public string? Activity { get; init; }

    public override string ToString() =>
        $"Explicit memory fact edit request {{ Id = {Id}, ExpectedRevision = {ExpectedRevision}, Content = [redacted], KeepHistory = {KeepHistory} }}";
}

public sealed record DeleteFactRequest
{
    public required Guid Id { get; init; }
    public required long ExpectedRevision { get; init; }
    public required Guid ConsentId { get; init; }
    /// <summary>Keep the fact's earlier versions (history facts it replaced). By default deleting a fact deletes them too, so a
    /// forgotten fact leaves nothing behind.</summary>
    public bool KeepEarlierVersions { get; init; }
}

/// <summary>Hidden search words for one fact (<see cref="MemoryStore.TidyAsync"/>), applied only to that exact revision.</summary>
public sealed record MemoryFactWords(Guid Id, long ExpectedRevision, string SearchWords)
{
    public override string ToString() => $"{nameof(MemoryFactWords)} {{ Id = {Id}, ExpectedRevision = {ExpectedRevision} }}";
}

/// <summary>A current fact a newer current fact (<paramref name="ReplacedBy"/>) made outdated: it becomes history.</summary>
public sealed record MemoryFactRetirement(Guid Id, long ExpectedRevision, Guid ReplacedBy);

/// <summary>Duplicates folded into one fact: <paramref name="Others"/> (exact revisions) are forgotten, their history moves to
/// the kept fact, and the kept fact takes <paramref name="Content"/> and <paramref name="SearchWords"/> when given.</summary>
public sealed record MemoryFactMerge(Guid KeepId, long KeepRevision, IReadOnlyList<(Guid Id, long Revision)> Others,
    string? Content = null, string? SearchWords = null)
{
    public override string ToString() => $"{nameof(MemoryFactMerge)} {{ KeepId = {KeepId}, Others = {Others?.Count} }}";
}

/// <summary>Background tidying of the store in one commit (docs/MEMORY.md, "Search words, history and tidying"): outdated
/// facts become history, duplicates are merged and facts get search words. An item whose fact changed since it was read is
/// skipped; the rest still apply.</summary>
public sealed record MemoryTidyRequest
{
    public IReadOnlyList<MemoryFactRetirement> Retirements { get; init; } = [];
    public IReadOnlyList<MemoryFactMerge> Merges { get; init; } = [];
    public IReadOnlyList<MemoryFactWords> Words { get; init; } = [];
}

/// <summary>What <see cref="MemoryStore.TidyAsync"/> changed: facts made history, duplicates forgotten, facts given search words.</summary>
public sealed record MemoryTidyReceipt(int Retired, int Merged, int Worded, long StoreRevision);

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
    /// <summary>Facts an embedding search found for the same question, best first (docs/SEARCH.md, "Finding by meaning"): they are
    /// fused with the lexical order by reciprocal rank fusion, so a fact that says the same thing in other words is found too. IDs
    /// a store doesn't have (or whose fact expired) are left out. Null: words only.</summary>
    public IReadOnlyList<Guid>? Similar { get; init; }
    /// <summary>Which facts it may find by where they matter (<see cref="MemoryActivityFilter"/>): those for everywhere, those
    /// for the activity now and those whose activity the words name. Null: every fact.</summary>
    public MemoryActivityFilter? Activity { get; init; }

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
        $"MemoryQuery {{ Text = [redacted], MaximumResults = {MaximumResults}, Similar = {Similar?.Count ?? 0}, ByActivity = {Activity is not null} }}";
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
