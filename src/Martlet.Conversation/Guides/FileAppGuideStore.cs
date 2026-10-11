using System.Text.Json;

namespace Martlet.Conversation.Guides;

/// <summary>Keeps the guide library and the guides as JSON files in one folder: library.json and one &lt;key&gt;.guide.json per
/// app. Each write goes to a temporary file first and then replaces the earlier one, one write at a time.</summary>
public sealed class FileAppGuideStore(string directory) : IAppGuideStore
{
    public const string LibraryFile = "library.json";
    public const string GuideSuffix = ".guide.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private readonly SemaphoreSlim gate = new(1, 1);

    public string Directory { get; } = Path.GetFullPath(directory ?? throw new ArgumentNullException(nameof(directory)));

    public async Task<AppGuideLibrary> LoadLibraryAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ReadLibraryAsync(cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public async Task SaveLibraryAsync(AppGuideLibrary library, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(library);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await WriteAsync(LibraryFile, library, cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public async Task<AppGuide?> LoadGuideAsync(string key, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Directory, Checked(key) + GuideSuffix);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path)) return null;
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<AppGuide>(stream, Json, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task SaveGuideAsync(AppGuide guide, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(guide);
        var key = Checked(guide.Key);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var bytes = await WriteAsync(key + GuideSuffix, guide, cancellationToken).ConfigureAwait(false);
            var library = await ReadLibraryAsync(cancellationToken).ConfigureAwait(false);
            var entry = library.Apps.FirstOrDefault(a => a.Key == key) ?? new AppGuideEntry { Key = key, Name = guide.Name };
            entry = entry with
            {
                BuiltAt = guide.BuiltAt, Pages = guide.Sources.Count, Chunks = guide.Chunks.Count, Bytes = bytes, Problem = null
            };
            library = library with { Apps = [.. library.Apps.Where(a => a.Key != key), entry] };
            await WriteAsync(LibraryFile, library, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task DeleteGuideAsync(string key, CancellationToken cancellationToken)
    {
        key = Checked(key);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            File.Delete(Path.Combine(Directory, key + GuideSuffix));
            var library = await ReadLibraryAsync(cancellationToken).ConfigureAwait(false);
            if (library.Apps.Any(a => a.Key == key))
                await WriteAsync(LibraryFile, library with { Apps = [.. library.Apps.Where(a => a.Key != key)] }, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task<AppGuideLibrary> ReadLibraryAsync(CancellationToken token)
    {
        var path = Path.Combine(Directory, LibraryFile);
        if (!File.Exists(path)) return new();
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<AppGuideLibrary>(stream, Json, token).ConfigureAwait(false) ?? new();
    }

    private async Task<long> WriteAsync<T>(string name, T value, CancellationToken token)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = Path.Combine(Directory, name);
        var pending = path + ".pending";
        try
        {
            await using (var stream = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None, 81_920, FileOptions.WriteThrough))
                await JsonSerializer.SerializeAsync(stream, value, Json, token).ConfigureAwait(false);
            var bytes = new FileInfo(pending).Length;
            File.Move(pending, path, overwrite: true);
            return bytes;
        }
        catch
        {
            try { File.Delete(pending); }
            catch (IOException) { }
            throw;
        }
    }

    private static string Checked(string key) =>
        AppGuideKeys.Of(key) == key ? key : throw new ArgumentException("Not an app guide key.", nameof(key));
}
