using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Characters;

/// <summary>One file of a character model: its path inside the model (forward slashes), SHA-256 and length, and the SHA-256
/// of each <see cref="CharacterModelLibrary.ChunkBytes"/> piece in order. Files travel between computers piece by piece (each
/// piece fits one signed request), so a host checks every piece it is given and a computer resumes an interrupted copy.</summary>
public sealed record CharacterModelFile
{
    public required string Path { get; init; }
    public required string Sha256 { get; init; }
    public required int Bytes { get; init; }
    public required IReadOnlyList<string> Chunks { get; init; }
}

/// <summary>One character model the owner added to Martlet (a Live2D model folder or a VRM file), shared with every paired
/// Martlet computer that can show a character. <see cref="Id"/> is the SHA-256 of its renderer, model file and every file's
/// path, SHA-256 and length, so the same model is the same entry on every computer. <see cref="Removed"/> is a tombstone, so
/// the model does not return from an older copy.</summary>
public sealed record CharacterModel
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    /// <summary><see cref="CharacterModelLibrary.Live2D"/> or <see cref="CharacterModelLibrary.Vrm"/>.</summary>
    public string? Renderer { get; init; }
    /// <summary>The model file inside the model: the <c>.model3.json</c> at its root, or the <c>.vrm</c> file.</summary>
    public string? Entry { get; init; }
    public IReadOnlyList<CharacterModelFile>? Files { get; init; }
    /// <summary>The computer the model was first added on.</summary>
    public string? AddedBy { get; init; }
    /// <summary>When the model joined the list; the list shows models in this order.</summary>
    public DateTimeOffset AddedAt { get; init; }
    public bool Removed { get; init; }
    public required long Revision { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public required string UpdatedBy { get; init; }

    /// <summary>The model's size: the sum of its files' lengths.</summary>
    [JsonIgnore]
    public long Bytes => Files?.Sum(f => (long)f.Bytes) ?? 0;

    internal string Content => JsonSerializer.Serialize(this with { Revision = 0, UpdatedAt = default, UpdatedBy = "" }, CharacterModelLibrary.Json);
}

/// <summary>The character models the owner added, shared by every paired Martlet host and desktop so any computer that can
/// be the companion shows the same characters. Hosts keep the list and every piece only to pass them on: desktops never talk
/// to each other directly. One last-writer-wins entry per model; <see cref="Merge"/> is commutative, associative and
/// idempotent, and revisions are hybrid clocks like the cluster plan's. Which character a computer shows stays its own
/// choice. The built-in character is part of Martlet and never in the list.</summary>
public sealed record CharacterModelLibrary
{
    public const int SchemaVersion1 = 1;
    public const string Live2D = "live2d", Vrm = "vrm";
    /// <summary>The written list's size limit. Every computer and host reads the whole list into memory, so it stays bounded.
    /// It holds <see cref="MaximumModels"/> models of <see cref="MaximumModelBytes"/> each (about 470 piece SHA-256s and
    /// <see cref="MaximumFiles"/> files with <see cref="MaximumPathLength"/>-character paths: about 80 KB of JSON a model) and
    /// <see cref="MaximumTombstones"/> tombstones, with room to spare.</summary>
    public const int MaximumBytes = 128 * 1024 * 1024;
    /// <summary>How many characters <see cref="Add"/> accepts. A merge of several computers' additions can hold more; no
    /// live model ever leaves the list unless it is removed.</summary>
    public const int MaximumModels = 1_000;
    /// <summary>How many removed models the list remembers, so a removed model doesn't return from an older copy. Beyond it
    /// the oldest tombstones are forgotten.</summary>
    public const int MaximumTombstones = 4_000;
    public const int MaximumNameLength = 80;
    public const int MaximumNameUtf8Bytes = 160;
    public const int MaximumFiles = 128;
    public const int MaximumDirectories = 128;
    public const int MaximumPathLength = 240;
    /// <summary>A model's size limit, the same as the character renderer's.</summary>
    public const long MaximumModelBytes = 1024L * 1024 * 1024;
    /// <summary>A VRM file's size limit, the same as the character renderer's.</summary>
    public const int MaximumVrmBytes = 512 * 1024 * 1024;
    /// <summary>A Live2D file's size limit (JSON files: <see cref="MaximumLive2DJsonBytes"/>), the same as the renderer's.</summary>
    public const int MaximumLive2DFileBytes = 512 * 1024 * 1024;
    public const int MaximumLive2DJsonBytes = 1024 * 1024;
    /// <summary>The combined size of the live models <see cref="Add"/> accepts a new model beside. A merge of several
    /// computers' additions can hold more; no live model ever leaves the list unless it is removed.</summary>
    public const long MaximumTotalBytes = 16L * 1024 * 1024 * 1024;
    /// <summary>The size of each piece a file travels in (the last piece of a file may be shorter).</summary>
    public const int ChunkBytes = 3 * 1024 * 1024;
    private const long MaximumRevision = long.MaxValue / 4;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] Live2DExtensions = [".json", ".moc3", ".png", ".wav"];
    private const string WindowsInvalid = "<>:\"/\\|?*";
    private static readonly string FullMessage =
        $"Your character list is full ({MaximumModels.ToString("N0", CultureInfo.InvariantCulture)} characters). Remove a character first.";
    private static readonly string NoRoomMessage =
        $"Your characters can use {SizeText(MaximumTotalBytes)} together, and this one doesn't fit. Remove a character first.";
    private static readonly string ModelTooLargeMessage = $"A character is larger than {SizeText(MaximumModelBytes)}.";
    private static readonly string VrmTooLargeMessage = $"A VRM character must be one .vrm file of at most {SizeText(MaximumVrmBytes)}.";
    private static readonly string ListTooLargeMessage =
        $"The character list is larger than {SizeText(MaximumBytes)}. Remove a character first.";

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        MaxDepth = 8
    };

    public required int SchemaVersion { get; init; }
    public required IReadOnlyList<CharacterModel> Models { get; init; }

    public static CharacterModelLibrary Empty { get; } = new() { SchemaVersion = SchemaVersion1, Models = [] };

    /// <summary>The models that are not removed, in the order they joined the list.</summary>
    [JsonIgnore]
    public IReadOnlyList<CharacterModel> Live => Models.Where(m => !m.Removed).OrderBy(m => m.AddedAt).ThenBy(m => m.Id, StringComparer.Ordinal).ToArray();

    [JsonIgnore]
    public long Revision => Models.Select(m => m.Revision).DefaultIfEmpty(0).Max();

    public long NextRevision(DateTimeOffset now) => Math.Max(Revision + 1, now.ToUnixTimeMilliseconds());

    public CharacterModel? Find(string id) => Models.FirstOrDefault(m => m.Id == id);

    /// <summary>Every piece a live model has, by SHA-256, with its length: what a host may be given.</summary>
    public IReadOnlyDictionary<string, int> LiveChunks()
    {
        var chunks = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in Live.SelectMany(m => m.Files!))
            for (var index = 0; index < file.Chunks.Count; index++)
                chunks.TryAdd(file.Chunks[index], ChunkLength(file.Bytes, index));
        return chunks;
    }

    public static int ChunkCount(int bytes) => bytes <= 0 ? 0 : (int)((bytes + (long)ChunkBytes - 1) / ChunkBytes);

    public static int ChunkLength(int fileBytes, int index) => (int)Math.Min(ChunkBytes, fileBytes - (long)index * ChunkBytes);

    /// <summary>A file entry for <paramref name="bytes"/> at <paramref name="path"/>: its SHA-256 and its pieces'.</summary>
    public static CharacterModelFile File(string path, ReadOnlySpan<byte> bytes)
    {
        var chunks = new string[ChunkCount(bytes.Length)];
        for (var index = 0; index < chunks.Length; index++)
            chunks[index] = Convert.ToHexStringLower(SHA256.HashData(bytes.Slice(index * ChunkBytes, ChunkLength(bytes.Length, index))));
        return new() { Path = path, Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), Bytes = bytes.Length, Chunks = chunks };
    }

    /// <summary>A model's ID: SHA-256 of its renderer, model file and every file's path, SHA-256 and length, lower-case hex.</summary>
    public static string ModelId(string renderer, string entry, IEnumerable<CharacterModelFile> files)
    {
        var text = new StringBuilder("martlet-character-model-v1\n").Append(renderer).Append('\n').Append(entry).Append('\n');
        foreach (var file in files.OrderBy(f => f.Path, StringComparer.Ordinal))
            text.Append(file.Path).Append('\n').Append(file.Sha256).Append('\n').Append(file.Bytes).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(StrictUtf8.GetBytes(text.ToString())));
    }

    /// <summary>Adds a model (or renames the live model it already is). Throws <see cref="ContractException"/> with
    /// <see cref="ErrorCode.PayloadTooLarge"/> when the list already has <see cref="MaximumModels"/> live models or the live
    /// models and this one together would pass <see cref="MaximumTotalBytes"/>.</summary>
    public CharacterModelLibrary Add(string name, string renderer, string entry, IReadOnlyList<CharacterModelFile> files, string by,
        DateTimeOffset now)
    {
        ContractRules.Require(IsName(name), "Give the character a name of at most 80 characters on one line.");
        var id = ModelId(renderer, entry, files);
        if (Find(id) is { Removed: false } existing)
            return existing.Name == name ? this : Put(existing with { Name = name, Revision = NextRevision(now), UpdatedAt = now.ToUniversalTime(), UpdatedBy = by });
        var live = Live;
        ContractRules.Require(live.Count < MaximumModels, FullMessage, ErrorCode.PayloadTooLarge);
        var model = new CharacterModel
        {
            Id = id, Name = name, Renderer = renderer, Entry = entry, Files = files.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray(),
            AddedBy = by, AddedAt = now.ToUniversalTime(), Revision = NextRevision(now), UpdatedAt = now.ToUniversalTime(), UpdatedBy = by
        };
        Validate(model);
        ContractRules.Require(live.Sum(m => m.Bytes) + model.Bytes <= MaximumTotalBytes, NoRoomMessage, ErrorCode.PayloadTooLarge);
        return Put(model);
    }

    /// <summary>Removes a model everywhere: only a tombstone remains.</summary>
    public CharacterModelLibrary Remove(string id, string by, DateTimeOffset now) => Find(id) is { Removed: false } model
        ? Put(new CharacterModel { Id = model.Id, Removed = true, Revision = NextRevision(now), UpdatedAt = now.ToUniversalTime(), UpdatedBy = by })
        : this;

    private CharacterModelLibrary Put(CharacterModel model) => this with { Models = Bounded(Models.Where(m => m.Id != model.Id).Append(model)) };

    /// <summary>Joins two copies: per model, the entry with the newest (revision, writer, content) wins.</summary>
    public static CharacterModelLibrary Merge(CharacterModelLibrary left, CharacterModelLibrary right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var models = left.Models.Concat(right.Models).GroupBy(m => m.Id, StringComparer.Ordinal)
            .Select(group => group.Aggregate((a, b) => Newer(a, b) ? a : b));
        return new() { SchemaVersion = SchemaVersion1, Models = Bounded(models) };
    }

    private static bool Newer(CharacterModel a, CharacterModel b) =>
        a.Revision != b.Revision ? a.Revision > b.Revision
        : a.UpdatedBy != b.UpdatedBy ? string.CompareOrdinal(a.UpdatedBy, b.UpdatedBy) > 0
        : string.CompareOrdinal(a.Content, b.Content) >= 0;

    // Every live model (none is ever dropped: Add refuses a model the list has no room for, and a merge keeps all) and the
    // newest MaximumTombstones tombstones; sorted by ID so equal content writes equal bytes.
    private static CharacterModel[] Bounded(IEnumerable<CharacterModel> models)
    {
        var all = models.ToArray();
        return all.Where(m => !m.Removed)
            .Concat(all.Where(m => m.Removed).OrderByDescending(m => m.Revision).ThenBy(m => m.Id, StringComparer.Ordinal).Take(MaximumTombstones))
            .OrderBy(m => m.Id, StringComparer.Ordinal).ToArray();
    }

    /// <summary>A size as the owner reads it, in whole GB or MB of 1,024: for example "16 GB" or "512 MB".</summary>
    public static string SizeText(long bytes)
    {
        const long Megabyte = 1024 * 1024, Gigabyte = 1024 * Megabyte;
        return bytes >= Gigabyte && bytes % Gigabyte == 0
            ? (bytes / Gigabyte).ToString(CultureInfo.InvariantCulture) + " GB"
            : ((bytes + Megabyte - 1) / Megabyte).ToString(CultureInfo.InvariantCulture) + " MB";
    }

    public static bool IsSha256(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>A name as it may be stored: one line, trimmed, 1 to <see cref="MaximumNameLength"/> characters.</summary>
    public static bool IsName(string? value) => value is { Length: > 0 and <= MaximumNameLength } && value == value.Trim() &&
        !value.Any(char.IsControl) && Utf8Bytes(value) is > 0 and <= MaximumNameUtf8Bytes;

    /// <summary>A file path inside a model as it may be stored: relative, forward slashes, at most
    /// <see cref="MaximumPathLength"/> characters, and every part a file name Windows accepts.</summary>
    public static bool IsPath(string? value) => value is { Length: > 0 and <= MaximumPathLength } && Utf8Bytes(value) > 0 &&
        value.Split('/').All(part => part.Length > 0 && part is not ("." or "..") && !part.EndsWith('.') && !part.EndsWith(' ') &&
            !part.Any(c => char.IsControl(c) || WindowsInvalid.Contains(c)));

    private static int Utf8Bytes(string value)
    {
        try { return StrictUtf8.GetByteCount(value); }
        catch (EncoderFallbackException) { return -1; }
    }

    private static string Directory(string path) => path.LastIndexOf('/') is var slash && slash > 0 ? path[..slash] : "";

    private static void Validate(CharacterModel model)
    {
        ContractRules.Require(IsSha256(model.Id), "A character's ID is invalid.");
        ContractRules.Require(model.Revision is > 0 and <= MaximumRevision, "A character's revision is out of range.");
        ContractRules.Identifier(model.UpdatedBy);
        if (model.Removed)
        {
            ContractRules.Require(model.Name is null && model.Renderer is null && model.Entry is null && model.Files is null && model.AddedBy is null,
                "A removed character still holds data.");
            return;
        }
        ContractRules.Require(IsName(model.Name), "A character's name is invalid.");
        ContractRules.Identifier(model.AddedBy);
        ContractRules.Require(model.Renderer is Live2D or Vrm, "A character's renderer is unsupported.");
        ContractRules.Require(IsPath(model.Entry) && model.Files is { Count: > 0 and <= MaximumFiles } && model.Files.All(f => f is not null),
            "A character's files are invalid.");
        var files = model.Files!;
        ContractRules.Require(files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == files.Count,
            "A character lists a file twice.");
        foreach (var file in files)
            ContractRules.Require(IsPath(file.Path) && IsSha256(file.Sha256) && file.Bytes > 0 && file.Chunks is not null &&
                file.Chunks.Count == ChunkCount(file.Bytes) && file.Chunks.All(IsSha256), "A character's file is invalid.");
        ContractRules.Require(model.Bytes <= MaximumModelBytes, ModelTooLargeMessage);
        if (model.Renderer == Vrm)
        {
            ContractRules.Require(files.Count == 1 && files[0].Path == model.Entry && !model.Entry!.Contains('/') &&
                model.Entry.EndsWith(".vrm", StringComparison.OrdinalIgnoreCase) && files[0].Bytes <= MaximumVrmBytes,
                VrmTooLargeMessage);
        }
        else
        {
            ContractRules.Require(!model.Entry!.Contains('/') && model.Entry.EndsWith(".model3.json", StringComparison.Ordinal) &&
                files.Any(f => f.Path == model.Entry), "A Live2D character needs its .model3.json at the top of its folder.");
            ContractRules.Require(files.Select(f => Directory(f.Path)).Where(d => d.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() <= MaximumDirectories,
                "A Live2D character has too many folders.");
            foreach (var file in files)
            {
                var extension = System.IO.Path.GetExtension(file.Path).ToLowerInvariant();
                ContractRules.Require(Live2DExtensions.Contains(extension), "A Live2D character may hold only model, texture, motion and sound files.");
                ContractRules.Require(file.Bytes <= (extension == ".json" ? MaximumLive2DJsonBytes : MaximumLive2DFileBytes),
                    "A Live2D character's file is too large.");
            }
        }
        ContractRules.Require(ModelId(model.Renderer!, model.Entry!, files) == model.Id, "A character's ID does not match its files.");
    }

    /// <summary>Checks the list and every model. The number of live models and their combined size are not checked here: a
    /// merge of several computers' additions can pass <see cref="MaximumModels"/> and <see cref="MaximumTotalBytes"/>, and
    /// the list's byte size (<see cref="MaximumBytes"/>) bounds it when it is read or written.</summary>
    public void Validate()
    {
        ContractRules.Require(SchemaVersion == SchemaVersion1, "This character list was written by a newer Martlet.", ErrorCode.UnsupportedVersion);
        ContractRules.Require(Models is not null && Models.All(m => m is not null), "The character list is malformed.");
        ContractRules.Require(Models!.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() == Models!.Count, "The character list lists a character twice.");
        foreach (var model in Models) Validate(model);
    }

    public byte[] Write()
    {
        Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this with { Models = Bounded(Models) }, Json);
        ContractRules.Require(bytes.Length <= MaximumBytes, ListTooLargeMessage, ErrorCode.PayloadTooLarge);
        return bytes;
    }

    public static CharacterModelLibrary Parse(ReadOnlySpan<byte> bytes)
    {
        ContractRules.Require(bytes.Length > 0, "The character list is empty.");
        ContractRules.Require(bytes.Length <= MaximumBytes, ListTooLargeMessage, ErrorCode.PayloadTooLarge);
        CharacterModelLibrary? library;
        try { library = JsonSerializer.Deserialize<CharacterModelLibrary>(bytes, Json); }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException or FormatException)
        {
            throw new ContractException(ErrorCode.InvalidContract, "The character list is malformed.");
        }
        ContractRules.Require(library is not null, "The character list is empty.");
        library!.Validate();
        return library with { Models = Bounded(library.Models) };
    }

    /// <summary>Identifies the list's content, to tell whether a copy is current.</summary>
    public string Digest() => Convert.ToHexStringLower(SHA256.HashData(Write()));

    public override string ToString() => $"Character model library r{Revision} ({Live.Count} models)";
}
