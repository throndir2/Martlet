using System.Collections.Concurrent;
using System.Text.Json;

namespace Martlet.Conversation.Guides;

/// <summary>Keeps the guide library and the guides as JSON files in one folder: library.json and one &lt;key&gt;.guide.json per
/// app (docs/APP_GUIDES.md).
/// <list type="bullet">
/// <item><b>Bounded</b>: at most <see cref="MaximumApps"/> apps, a guide file of at most <see cref="MaximumGuideBytes"/>, at most
/// <see cref="MaximumChunks"/> chunks of at most <see cref="MaximumChunkCharacters"/> each. Saving more is refused with an
/// <see cref="ArgumentException"/> (or <see cref="InvalidOperationException"/> when the library is full).</item>
/// <item><b>Safe to load</b>: each file says its <see cref="FormatVersion"/>. A file that can't be read, breaks the bounds or was
/// written by a newer Martlet is refused with an <see cref="AppGuideFileException"/> naming it; it is never reset. A guide save or
/// delete leaves an unreadable library as it is; saving a new library keeps a copy of an unreadable one
/// (library.json.unreadable) and refuses to replace a newer one.</item>
/// <item><b>Atomic</b>: each write goes to a temporary file written through to the disk, which then replaces the earlier
/// file, so a failed write leaves the earlier file.</item>
/// <item><b>One at a time</b>: every store on one folder in this process shares one gate.</item>
/// </list></summary>
public sealed class FileAppGuideStore : IAppGuideStore
{
    public const string LibraryFile = "library.json";
    public const string GuideSuffix = ".guide.json";
    public const string UnreadableSuffix = ".unreadable";
    /// <summary>The file format this store writes; it reads this and earlier ones.</summary>
    public const int FormatVersion = 1;
    public const int MaximumApps = 200;
    public const long MaximumGuideBytes = 20_000_000;
    public const long MaximumLibraryBytes = 2_000_000;
    public const int MaximumChunks = 25_000;
    public const int MaximumChunkCharacters = 4_000;
    public const int MaximumSources = 2_000;
    private const string VersionProperty = "formatVersion";
    private const int MaximumNameCharacters = 200;
    private const int MaximumUrlCharacters = 2_048;
    private const int MaximumListItems = 50;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim gate;

    public FileAppGuideStore(string directory)
    {
        Directory = Path.GetFullPath(directory ?? throw new ArgumentNullException(nameof(directory)));
        gate = Gates.GetOrAdd(Path.TrimEndingDirectorySeparator(Directory), _ => new SemaphoreSlim(1, 1));
    }

    public string Directory { get; }

    public async Task<AppGuideLibrary> LoadLibraryAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ReadLibraryAsync(cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public async Task SaveLibraryAsync(AppGuideLibrary library, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(library);
        if (Problem(library) is { } problem) throw new ArgumentException("The guide library can't be saved: " + problem + ".", nameof(library));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try { await ReadLibraryAsync(cancellationToken).ConfigureAwait(false); }
            catch (AppGuideFileException error) when (!error.IsNewer)
            {
                File.Copy(error.File, error.File + UnreadableSuffix, overwrite: true);
            }
            await WriteAsync(LibraryFile, library, MaximumLibraryBytes, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task<AppGuide?> LoadGuideAsync(string key, CancellationToken cancellationToken)
    {
        var name = Checked(key) + GuideSuffix;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var guide = await ReadAsync<AppGuide>(name, MaximumGuideBytes, cancellationToken).ConfigureAwait(false);
            if (guide is null) return null;
            if (guide.Key != key) throw new AppGuideFileException(Path.Combine(Directory, name), "it holds the guide of another app", false);
            if (Problem(guide) is { } problem) throw new AppGuideFileException(Path.Combine(Directory, name), problem, false);
            return guide;
        }
        finally { gate.Release(); }
    }

    public async Task SaveGuideAsync(AppGuide guide, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(guide);
        var key = Checked(guide.Key);
        if (Problem(guide) is { } problem) throw new ArgumentException("The guide can't be saved: " + problem + ".", nameof(guide));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var library = await ReadLibraryAsync(cancellationToken).ConfigureAwait(false);
            var entry = library.Apps.FirstOrDefault(a => a.Key == key);
            if (entry is null && library.Apps.Count >= MaximumApps)
                throw new InvalidOperationException($"The guide library already has {MaximumApps} apps; delete one first.");
            var bytes = await WriteAsync(key + GuideSuffix, guide, MaximumGuideBytes, cancellationToken).ConfigureAwait(false);
            entry = (entry ?? new AppGuideEntry { Key = key, Name = Shortened(guide.Name) }) with
            {
                BuiltAt = guide.BuiltAt, Pages = guide.Sources.Count, Chunks = guide.Chunks.Count, Bytes = bytes, Problem = null
            };
            library = library with { Apps = [.. library.Apps.Where(a => a.Key != key), entry] };
            await WriteAsync(LibraryFile, library, MaximumLibraryBytes, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task DeleteGuideAsync(string key, CancellationToken cancellationToken)
    {
        key = Checked(key);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var library = await ReadLibraryAsync(cancellationToken).ConfigureAwait(false);
            File.Delete(Path.Combine(Directory, key + GuideSuffix));
            if (library.Apps.Any(a => a.Key == key))
                await WriteAsync(LibraryFile, library with { Apps = [.. library.Apps.Where(a => a.Key != key)] }, MaximumLibraryBytes,
                    cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task<AppGuideLibrary> ReadLibraryAsync(CancellationToken token)
    {
        var library = await ReadAsync<AppGuideLibrary>(LibraryFile, MaximumLibraryBytes, token).ConfigureAwait(false);
        if (library is null) return new();
        if (Problem(library) is { } problem) throw new AppGuideFileException(Path.Combine(Directory, LibraryFile), problem, false);
        return library;
    }

    private async Task<T?> ReadAsync<T>(string name, long maximumBytes, CancellationToken token) where T : class
    {
        var path = Path.Combine(Directory, name);
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81_920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        await using (stream.ConfigureAwait(false))
        {
            if (stream.Length > maximumBytes)
                throw new AppGuideFileException(path, $"it is larger than {maximumBytes / 1_000_000} MB", false);
            try
            {
                using var document = await JsonDocument.ParseAsync(stream, default, token).ConfigureAwait(false);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw new AppGuideFileException(path, "it isn't a JSON object", false);
                if (root.TryGetProperty(VersionProperty, out var version))
                {
                    if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number < 1)
                        throw new AppGuideFileException(path, "its format version isn't a number Martlet knows", false);
                    if (number > FormatVersion)
                        throw new AppGuideFileException(path, $"a newer Martlet wrote it (format {number}; this Martlet reads up to {FormatVersion})", true);
                }
                return root.Deserialize<T>(Json) ?? throw new AppGuideFileException(path, "it is empty", false);
            }
            catch (JsonException error)
            {
                throw new AppGuideFileException(path, "it isn't valid (" + error.Message + ")", false, error);
            }
        }
    }

    private async Task<long> WriteAsync<T>(string name, T value, long maximumBytes, CancellationToken token)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = Path.Combine(Directory, name);
        var pending = $"{path}.{Guid.NewGuid():N}.pending";
        try
        {
            var element = JsonSerializer.SerializeToElement(value, Json);
            long bytes;
            await using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920,
                FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                await using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber(VersionProperty, FormatVersion);
                    foreach (var property in element.EnumerateObject())
                        if (property.Name != VersionProperty) property.WriteTo(writer);
                    writer.WriteEndObject();
                    await writer.FlushAsync(token).ConfigureAwait(false);
                }
                stream.Flush(flushToDisk: true);
                bytes = stream.Length;
            }
            if (bytes > maximumBytes)
                throw new ArgumentException($"{name} would be larger than {maximumBytes / 1_000_000} MB.", nameof(value));
            await ReplaceAsync(pending, path, token).ConfigureAwait(false);
            return bytes;
        }
        catch
        {
            try { File.Delete(pending); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    // Another program (an antivirus scan, a backup) may hold the earlier file for a moment: a few short retries.
    private static async Task ReplaceAsync(string pending, string path, CancellationToken token)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(pending, path, overwrite: true);
                return;
            }
            catch (Exception error) when (attempt < 5 && error is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(20 * attempt, token).ConfigureAwait(false);
            }
        }
    }

    private static string? Problem(AppGuideLibrary library)
    {
        if (library.Apps is null) return "it has no app list";
        if (library.Apps.Count > MaximumApps) return $"it has more than {MaximumApps} apps";
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var app in library.Apps)
        {
            if (app is null) return "an app is missing";
            if (AppGuideKeys.Of(app.Key) != app.Key) return $"\"{app.Key}\" isn't an app guide key";
            if (!keys.Add(app.Key)) return $"the app \"{app.Key}\" is in it twice";
            if (string.IsNullOrWhiteSpace(app.Name) || app.Name.Length > MaximumNameCharacters) return $"the app \"{app.Key}\" has no usable name";
            if (TooMany(app.Programs, MaximumNameCharacters) || TooMany(app.Sites, MaximumUrlCharacters))
                return $"the app \"{app.Key}\" has too many or too long programs or sites";
            if (app.Problem is { Length: > 1_000 }) return $"the app \"{app.Key}\" has a too long problem";
            if (app.Pages < 0 || app.Chunks < 0 || app.Bytes < 0) return $"the app \"{app.Key}\" has negative counts";
        }
        return null;
    }

    private static string? Problem(AppGuide guide)
    {
        if (string.IsNullOrWhiteSpace(guide.Name)) return "it has no name";
        if (guide.Sites is null || guide.Sources is null || guide.Chunks is null) return "a list is missing";
        if (TooMany(guide.Sites, MaximumUrlCharacters)) return "it has too many or too long sites";
        if (guide.Sources.Count > MaximumSources) return $"it has more than {MaximumSources} pages";
        if (guide.Chunks.Count > MaximumChunks) return $"it has more than {MaximumChunks} chunks";
        foreach (var source in guide.Sources)
            if (source is null || source.Url is null || source.Title is null || source.Url.Length > MaximumUrlCharacters) return "a page is missing or too long";
        foreach (var chunk in guide.Chunks)
        {
            if (chunk is null || chunk.Url is null || chunk.Page is null || chunk.Section is null || chunk.Text is null) return "a chunk is missing";
            if (chunk.Text.Length > MaximumChunkCharacters) return $"a chunk is longer than {MaximumChunkCharacters} characters";
            if (chunk.Url.Length > MaximumUrlCharacters || chunk.Page.Length > 1_000 || chunk.Section.Length > 2_000) return "a chunk's page is too long";
        }
        return null;
    }

    private static bool TooMany(IReadOnlyList<string>? items, int longest) =>
        items is null || items.Count > MaximumListItems || items.Any(i => i is null || i.Length > longest);

    private static string Shortened(string name) => name.Length <= MaximumNameCharacters ? name : name[..MaximumNameCharacters];

    private static string Checked(string key) =>
        key is not null && AppGuideKeys.Of(key) == key ? key : throw new ArgumentException("Not an app guide key.", nameof(key));
}

/// <summary>A guide file Martlet can't use: it can't be read, breaks the store's bounds, or (<see cref="IsNewer"/>) a newer Martlet
/// wrote it. The file is left as it is.</summary>
public sealed class AppGuideFileException(string file, string reason, bool newer, Exception? inner = null)
    : IOException($"Martlet can't use {Path.GetFileName(file)}: {reason}.", inner)
{
    /// <summary>The file's full path.</summary>
    public string File { get; } = file;
    /// <summary>Why, in a few plain words.</summary>
    public string Reason { get; } = reason;
    /// <summary>A newer Martlet wrote it.</summary>
    public bool IsNewer { get; } = newer;
}
