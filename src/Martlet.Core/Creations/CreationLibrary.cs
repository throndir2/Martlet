using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Creations;

/// <summary>One named part of a creation (a song's <c>vocals</c>, <c>backing</c>, <c>mix</c> or <c>mouth</c>), stored by the
/// SHA-256 of its bytes. Assets travel between computers piece by piece (<see cref="CreationLibrary.ChunkBytes"/> each, one
/// signed request per piece), so <see cref="Chunks"/> lists each piece's SHA-256 in order.</summary>
public sealed record CreationAsset
{
    public required string Name { get; init; }
    /// <summary>One of <see cref="CreationLibrary.MediaTypes"/>, such as <c>audio/flac</c> or <c>application/json</c>.</summary>
    public required string MediaType { get; init; }
    public required string Sha256 { get; init; }
    public required long Bytes { get; init; }
    public required IReadOnlyList<string> Chunks { get; init; }
}

/// <summary>Which Martlet made a creation: the computer (its device ID and, for people, its name) and the voice and
/// personality Martlet had.</summary>
public sealed record CreationAuthor
{
    /// <summary>The Martlet computer's device ID.</summary>
    public required string Device { get; init; }
    /// <summary>The computer's name as the owner knows it.</summary>
    public string? Computer { get; init; }
    /// <summary>The voice Martlet used (a shared speaking voice's ID or name).</summary>
    public string? Voice { get; init; }
    /// <summary>The personality Martlet had.</summary>
    public string? Persona { get; init; }
}

/// <summary>Something Martlet made: a song now, other kinds later (<see cref="CreationKind"/>). <see cref="Kind"/> names a
/// registered kind; <see cref="Text"/> is its readable text (a song's lyrics with section tags) and <see cref="Metadata"/>
/// its kind's own JSON object (<see cref="KindVersion"/> says which version of it). Assets are stored by SHA-256.
/// <see cref="Removed"/> is a tombstone, so a deleted creation does not return from an older copy.</summary>
public sealed record Creation
{
    public required string Id { get; init; }
    public string? Kind { get; init; }
    public int KindVersion { get; init; }
    public string? Title { get; init; }
    public string? Summary { get; init; }
    public string? Text { get; init; }
    /// <summary>How long it lasts when performed, for kinds that have a length (a song).</summary>
    public long? DurationMs { get; init; }
    public CreationAuthor? CreatedBy { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public JsonElement? Metadata { get; init; }
    public IReadOnlyList<CreationAsset>? Assets { get; init; }
    /// <summary>Whether Martlet may delete it by itself, oldest first, when a new creation needs the room (its kind's
    /// choice, kept in the entry so every computer and host applies the same rule).</summary>
    public bool AutoCleanup { get; init; }
    public bool Removed { get; init; }
    public required long Revision { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public required string UpdatedBy { get; init; }

    /// <summary>The size of its assets together.</summary>
    [JsonIgnore]
    public long Bytes => Assets?.Sum(a => a.Bytes) ?? 0;

    [JsonIgnore]
    public TimeSpan? Duration => DurationMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null;

    /// <summary>The short ID Martlet's tools and the Creations page use: the first <see cref="CreationLibrary.KeyLength"/>
    /// hex digits.</summary>
    [JsonIgnore]
    public string Key => Id[..CreationLibrary.KeyLength];

    public CreationAsset? Asset(string name) => Assets?.FirstOrDefault(a => a.Name == name);

    internal string Content => JsonSerializer.Serialize(this with { Revision = 0, UpdatedAt = default, UpdatedBy = "" }, CreationLibrary.Json);
}

/// <summary>Everything Martlet made, shared by every paired Martlet host and desktop so Martlet on any computer can perform,
/// show or activate any creation. Hosts keep the list and every asset piece only to pass them on (desktops reach each other
/// only through hosts). One last-writer-wins entry per creation; <see cref="Merge"/> is commutative, associative and
/// idempotent, and revisions are hybrid clocks like the cluster plan's. The list knows nothing about kinds, so hosts and
/// older Martlets keep and pass on kinds they don't know.</summary>
public sealed record CreationLibrary
{
    public const int SchemaVersion1 = 1;
    /// <summary>The list's JSON size limit.</summary>
    public const int MaximumBytes = 8 * 1024 * 1024;
    public const int MaximumCreations = 256;
    public const int MaximumTombstones = 1024;
    public const int MaximumAssets = 16;
    public const int MaximumTitleLength = 120;
    public const int MaximumSummaryLength = 600;
    public const int MaximumTextUtf8Bytes = 16 * 1024;
    public const int MaximumMetadataUtf8Bytes = 16 * 1024;
    public const int MaximumKindVersion = 1000;
    /// <summary>One asset's size limit; a kind may set a lower one.</summary>
    public const long MaximumAssetBytes = 256L * 1024 * 1024;
    /// <summary>One creation's size limit (its assets together); a kind may set a lower one.</summary>
    public const long MaximumCreationBytes = 512L * 1024 * 1024;
    /// <summary>The combined size of every live creation.</summary>
    public const long MaximumTotalBytes = 2L * 1024 * 1024 * 1024;
    /// <summary>The size of each piece an asset travels in (the last piece may be shorter); the same as the character models'.</summary>
    public const int ChunkBytes = 3 * 1024 * 1024;
    /// <summary>How many hex digits of the ID make its short <see cref="Creation.Key"/>.</summary>
    public const int KeyLength = 12;
    private const long MaximumRevision = long.MaxValue / 4;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>The media types an asset may have. Assets are data: nothing in them is ever run.</summary>
    public static readonly IReadOnlySet<string> MediaTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "audio/flac", "audio/wav", "application/json", "text/plain", "image/png", "image/jpeg", "image/webp", "application/octet-stream"
    };

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        MaxDepth = 32
    };

    public required int SchemaVersion { get; init; }
    public required IReadOnlyList<Creation> Creations { get; init; }

    public static CreationLibrary Empty { get; } = new() { SchemaVersion = SchemaVersion1, Creations = [] };

    /// <summary>The creations that are not removed, newest first.</summary>
    [JsonIgnore]
    public IReadOnlyList<Creation> Live => Creations.Where(c => !c.Removed).OrderByDescending(c => c.CreatedAt)
        .ThenBy(c => c.Id, StringComparer.Ordinal).ToArray();

    [JsonIgnore]
    public long Revision => Creations.Select(c => c.Revision).DefaultIfEmpty(0).Max();

    [JsonIgnore]
    public long LiveBytes => Creations.Where(c => !c.Removed).Sum(c => c.Bytes);

    public long NextRevision(DateTimeOffset now) => Math.Max(Revision + 1, now.ToUnixTimeMilliseconds());

    public Creation? Find(string id) => Creations.FirstOrDefault(c => c.Id == id);

    /// <summary>The live creation <paramref name="reference"/> names: its full ID, or the start of it (at least six hex
    /// digits, such as its <see cref="Creation.Key"/>) when only one live creation starts that way.</summary>
    public Creation? Resolve(string? reference)
    {
        var value = reference?.Trim().ToLowerInvariant();
        if (value is not { Length: >= 6 and <= 32 } || !value.All(IsHex)) return null;
        var matches = Creations.Where(c => !c.Removed && c.Id.StartsWith(value, StringComparison.Ordinal)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    /// <summary>Every piece a live creation has, by SHA-256, with its length: what a host may be given.</summary>
    public IReadOnlyDictionary<string, int> LiveChunks()
    {
        var chunks = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var asset in Creations.Where(c => !c.Removed).SelectMany(c => c.Assets!))
            for (var index = 0; index < asset.Chunks.Count; index++)
                chunks.TryAdd(asset.Chunks[index], ChunkLength(asset.Bytes, index));
        return chunks;
    }

    /// <summary>Every asset a live creation has, by SHA-256.</summary>
    public IReadOnlyDictionary<string, CreationAsset> LiveAssets()
    {
        var assets = new Dictionary<string, CreationAsset>(StringComparer.Ordinal);
        foreach (var asset in Creations.Where(c => !c.Removed).SelectMany(c => c.Assets!)) assets.TryAdd(asset.Sha256, asset);
        return assets;
    }

    public static int ChunkCount(long bytes) => bytes <= 0 ? 0 : (int)((bytes + ChunkBytes - 1) / ChunkBytes);

    public static int ChunkLength(long assetBytes, int index) => (int)Math.Min(ChunkBytes, assetBytes - (long)index * ChunkBytes);

    /// <summary>An asset entry for <paramref name="bytes"/>: its SHA-256 and its pieces'.</summary>
    public static CreationAsset Asset(string name, string mediaType, ReadOnlySpan<byte> bytes)
    {
        var chunks = new string[ChunkCount(bytes.Length)];
        for (var index = 0; index < chunks.Length; index++)
            chunks[index] = Convert.ToHexStringLower(SHA256.HashData(bytes.Slice(index * ChunkBytes, ChunkLength(bytes.Length, index))));
        return new() { Name = name, MediaType = mediaType, Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), Bytes = bytes.Length, Chunks = chunks };
    }

    /// <summary>A new creation ID: 128 random bits as 32 lower-case hex digits.</summary>
    public static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>Adds <paramref name="creation"/> (its revision is stamped here). When the list is full, the oldest creations
    /// that allow it (<see cref="Creation.AutoCleanup"/>) are removed to make room; when that isn't enough, throws
    /// <see cref="ContractException"/> with <see cref="ErrorCode.PayloadTooLarge"/>.</summary>
    public CreationLibrary Add(Creation creation, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(creation);
        ContractRules.Require(Find(creation.Id) is null, "That creation is already in the list.");
        var stamped = creation with { Removed = false, Revision = NextRevision(now), UpdatedAt = now.ToUniversalTime() };
        Validate(stamped);
        var library = this;
        var live = Live;
        var total = live.Sum(c => c.Bytes);
        var count = live.Count;
        foreach (var old in live.Where(c => c.AutoCleanup).OrderBy(c => c.CreatedAt).ThenBy(c => c.Id, StringComparer.Ordinal))
        {
            if (count < MaximumCreations && total + stamped.Bytes <= MaximumTotalBytes) break;
            library = library.Remove(old.Id, stamped.UpdatedBy, now);
            count--;
            total -= old.Bytes;
        }
        ContractRules.Require(count < MaximumCreations && total + stamped.Bytes <= MaximumTotalBytes,
            $"Martlet's creations are full ({MaximumCreations} or 2 GB). Delete some in Creations first.", ErrorCode.PayloadTooLarge);
        return library.Put(stamped with { Revision = library.NextRevision(now) });
    }

    /// <summary>Gives a live creation a new title.</summary>
    public CreationLibrary Rename(string id, string title, string by, DateTimeOffset now)
    {
        ContractRules.Require(IsTitle(title), $"Give it a title of at most {MaximumTitleLength} characters on one line.");
        return Find(id) is { Removed: false } creation && creation.Title != title
            ? Put(creation with { Title = title, Revision = NextRevision(now), UpdatedAt = now.ToUniversalTime(), UpdatedBy = by })
            : this;
    }

    /// <summary>Removes a creation everywhere: only a tombstone remains.</summary>
    public CreationLibrary Remove(string id, string by, DateTimeOffset now) => Find(id) is { Removed: false } creation
        ? Put(new Creation { Id = creation.Id, Removed = true, Revision = NextRevision(now), UpdatedAt = now.ToUniversalTime(), UpdatedBy = by })
        : this;

    private CreationLibrary Put(Creation creation) => this with { Creations = Bounded(Creations.Where(c => c.Id != creation.Id).Append(creation)) };

    /// <summary>Joins two copies: per creation, the entry with the newest (revision, writer, content) wins.</summary>
    public static CreationLibrary Merge(CreationLibrary left, CreationLibrary right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var creations = left.Creations.Concat(right.Creations).GroupBy(c => c.Id, StringComparer.Ordinal)
            .Select(group => group.Aggregate((a, b) => Newer(a, b) ? a : b));
        return new() { SchemaVersion = SchemaVersion1, Creations = Bounded(creations) };
    }

    private static bool Newer(Creation a, Creation b) =>
        a.Revision != b.Revision ? a.Revision > b.Revision
        : a.UpdatedBy != b.UpdatedBy ? string.CompareOrdinal(a.UpdatedBy, b.UpdatedBy) > 0
        : string.CompareOrdinal(a.Content, b.Content) >= 0;

    // The newest live creations that fit the count and size limits (those Martlet may not clean up first, so two computers
    // filling the list offline never push out one only the owner may delete), then the newest tombstones; sorted by ID so
    // equal content writes equal bytes.
    private static Creation[] Bounded(IEnumerable<Creation> creations)
    {
        var all = creations.ToArray();
        var live = new List<Creation>();
        long total = 0;
        foreach (var creation in all.Where(c => !c.Removed).OrderBy(c => c.AutoCleanup).ThenByDescending(c => c.CreatedAt)
                     .ThenBy(c => c.Id, StringComparer.Ordinal))
        {
            if (live.Count == MaximumCreations) break;
            if (total + creation.Bytes > MaximumTotalBytes) continue;
            live.Add(creation);
            total += creation.Bytes;
        }
        return live.Concat(all.Where(c => c.Removed).OrderByDescending(c => c.Revision).ThenBy(c => c.Id, StringComparer.Ordinal).Take(MaximumTombstones))
            .OrderBy(c => c.Id, StringComparer.Ordinal).ToArray();
    }

    private static bool IsHex(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f';

    public static bool IsSha256(string? value) => value is { Length: 64 } && value.All(IsHex);

    public static bool IsId(string? value) => value is { Length: 32 } && value.All(IsHex);

    /// <summary>A kind or asset name: a lower-case ASCII letter, then up to 31 lower-case letters, digits or hyphens.</summary>
    public static bool IsName(string? value) => value is { Length: > 0 and <= 32 } && value[0] is >= 'a' and <= 'z' &&
        value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    /// <summary>A title as it may be stored: one line, trimmed, 1 to <see cref="MaximumTitleLength"/> characters.</summary>
    public static bool IsTitle(string? value) => value is { Length: > 0 and <= MaximumTitleLength } && value == value.Trim() &&
        !value.Any(char.IsControl) && Utf8Bytes(value) > 0;

    /// <summary>Text as it may be stored: no control characters but line breaks and tabs, at most
    /// <paramref name="maximumUtf8Bytes"/> UTF-8 bytes.</summary>
    public static bool IsText(string? value, int maximumUtf8Bytes) => value is not null &&
        !value.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t') && Utf8Bytes(value) is >= 0 and var bytes &&
        bytes <= maximumUtf8Bytes;

    /// <summary>A title made from <paramref name="text"/>: its first line, cut to <see cref="MaximumTitleLength"/>
    /// characters, or <paramref name="fallback"/>.</summary>
    public static string TitleFrom(string? text, string fallback)
    {
        var line = (text ?? "").Split('\n').Select(l => new string(l.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim())
            .FirstOrDefault(l => l.Length > 0) ?? "";
        if (line.Length > MaximumTitleLength) line = line[..(MaximumTitleLength - 1)].TrimEnd() + "…";
        return IsTitle(line) ? line : fallback;
    }

    private static int Utf8Bytes(string value)
    {
        try { return StrictUtf8.GetByteCount(value); }
        catch (EncoderFallbackException) { return -1; }
    }

    private static void Validate(Creation creation)
    {
        ContractRules.Require(IsId(creation.Id), "A creation's ID is invalid.");
        ContractRules.Require(creation.Revision is > 0 and <= MaximumRevision, "A creation's revision is out of range.");
        ContractRules.Identifier(creation.UpdatedBy);
        if (creation.Removed)
        {
            ContractRules.Require(creation.Kind is null && creation.Title is null && creation.Summary is null && creation.Text is null &&
                creation.DurationMs is null && creation.CreatedBy is null && creation.Metadata is null && creation.Assets is null &&
                creation.KindVersion == 0 && !creation.AutoCleanup && creation.CreatedAt == default,
                "A deleted creation still holds data.");
            return;
        }
        ContractRules.Require(IsName(creation.Kind), "A creation's kind is invalid.");
        ContractRules.Require(creation.KindVersion is >= 1 and <= MaximumKindVersion, "A creation's kind version is out of range.");
        ContractRules.Require(IsTitle(creation.Title), "A creation's title is invalid.");
        ContractRules.Require(creation.Summary is null || (creation.Summary.Length <= MaximumSummaryLength &&
            IsText(creation.Summary, MaximumSummaryLength * 4)), "A creation's summary is invalid.");
        ContractRules.Require(creation.Text is null || IsText(creation.Text, MaximumTextUtf8Bytes), "A creation's text is invalid or too long.");
        ContractRules.Require(creation.DurationMs is null or (>= 0 and <= 24L * 3600 * 1000), "A creation's length is out of range.");
        ContractRules.Require(creation.CreatedAt.Year is >= 2020 and <= 9000, "A creation's time is invalid.");
        ContractRules.Require(creation.CreatedBy is { } author && ContractRules.IsIdentifier(author.Device) &&
            (author.Computer is null || IsTitle(author.Computer)) && (author.Voice is null || IsTitle(author.Voice)) &&
            (author.Persona is null || IsTitle(author.Persona)), "A creation's author is invalid.");
        if (creation.Metadata is { } metadata)
            ContractRules.Require(metadata.ValueKind == JsonValueKind.Object &&
                Encoding.UTF8.GetByteCount(metadata.GetRawText()) <= MaximumMetadataUtf8Bytes, "A creation's metadata is invalid or too large.");
        ContractRules.Require(creation.Assets is { Count: <= MaximumAssets } && creation.Assets.All(a => a is not null),
            "A creation's assets are invalid.");
        var assets = creation.Assets!;
        ContractRules.Require(assets.Select(a => a.Name).Distinct(StringComparer.Ordinal).Count() == assets.Count, "A creation lists an asset twice.");
        foreach (var asset in assets)
            ContractRules.Require(IsName(asset.Name) && MediaTypes.Contains(asset.MediaType) && IsSha256(asset.Sha256) &&
                asset.Bytes is > 0 and <= MaximumAssetBytes && asset.Chunks is not null && asset.Chunks.Count == ChunkCount(asset.Bytes) &&
                asset.Chunks.All(IsSha256), "A creation's asset is invalid.");
        ContractRules.Require(creation.Bytes <= MaximumCreationBytes, "A creation is larger than 512 MB.");
    }

    public void Validate()
    {
        ContractRules.Require(SchemaVersion == SchemaVersion1, "This creation list was written by a newer Martlet.", ErrorCode.UnsupportedVersion);
        ContractRules.Require(Creations is not null && Creations.All(c => c is not null) &&
            Creations.Count(c => !c.Removed) <= MaximumCreations && Creations.Count(c => c.Removed) <= MaximumTombstones,
            "The creation list has too many creations.");
        ContractRules.Require(Creations!.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() == Creations!.Count,
            "The creation list lists a creation twice.");
        foreach (var creation in Creations) Validate(creation);
        ContractRules.Require(LiveBytes <= MaximumTotalBytes, "The creation list is larger than 2 GB.");
    }

    public byte[] Write()
    {
        Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this with { Creations = Bounded(Creations) }, Json);
        ContractRules.Require(bytes.Length <= MaximumBytes, "The creation list is too large.", ErrorCode.PayloadTooLarge);
        return bytes;
    }

    public static CreationLibrary Parse(ReadOnlySpan<byte> bytes)
    {
        ContractRules.Require(bytes.Length is > 0 and <= MaximumBytes, "The creation list is empty or too large.", ErrorCode.PayloadTooLarge);
        CreationLibrary? library;
        try { library = JsonSerializer.Deserialize<CreationLibrary>(bytes, Json); }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException or FormatException)
        {
            throw new ContractException(ErrorCode.InvalidContract, "The creation list is malformed.");
        }
        ContractRules.Require(library is not null, "The creation list is empty.");
        library!.Validate();
        return library with { Creations = Bounded(library.Creations) };
    }

    /// <summary>Identifies the list's content, to tell whether a copy is current.</summary>
    public string Digest() => Convert.ToHexStringLower(SHA256.HashData(Write()));

    public override string ToString() => $"Creation library r{Revision} ({Live.Count} creations)";
}
