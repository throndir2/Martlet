using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Installation;

namespace Martlet.Core.Planning;

/// <summary>How one source did at the last refresh: when it was tried and last read well, how many items it gave, how long it
/// took and what went wrong (in words, never a key: the sources need none).</summary>
public sealed record CatalogSourceStatus
{
    public DateTimeOffset? Tried { get; init; }
    public DateTimeOffset? Read { get; init; }
    public int Items { get; init; }
    public long Bytes { get; init; }
    public double Seconds { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Problem { get; init; }
}

/// <summary>What the daily refresh did (model-catalog-status.json next to the daily copy).</summary>
public sealed record ModelCatalogStatus
{
    /// <summary>When a refresh last started (on any Martlet that shares this folder). The next one waits a day from it.</summary>
    public DateTimeOffset? LastAttempt { get; init; }
    /// <summary>When a refresh last finished: <see cref="Problem"/> is null when every source was read.</summary>
    public DateTimeOffset? LastFinished { get; init; }
    /// <summary>When a refresh last read every source.</summary>
    public DateTimeOffset? LastSuccess { get; init; }
    /// <summary>When a refresh last saved a new daily copy (a source that failed kept its last good part).</summary>
    public DateTimeOffset? LastSaved { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Problem { get; init; }
    public Dictionary<string, CatalogSourceStatus> Sources { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>Where the model catalog lives: the snapshot shipped inside Martlet (so it works offline and on first start), and the
/// daily copy in the PC folder (<see cref="PcFolder"/>: %ProgramData%\Martlet, shared by every Windows user and account, since
/// the data is public; a disposable data folder keeps its own). <see cref="Load"/> uses the newer of the two. A refresh that
/// fails keeps the last good copy.</summary>
public sealed class ModelCatalogStore
{
    public const string FileName = "model-catalog.json";
    public const string StatusFileName = "model-catalog-status.json";
    public const string ResourceName = "Martlet.Core.model-catalog.json";
    public static TimeSpan RefreshEvery { get; } = TimeSpan.FromDays(1);

    private static readonly Lazy<ModelCatalogData> Shipped = new(ReadSnapshot);
    private static readonly ConcurrentDictionary<string, (DateTime Written, long Length, ModelCatalog Catalog)> Loaded =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions StatusJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        MaxDepth = 8
    };

    public ModelCatalogStore(string directory, PcFolder? folder = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = Path.GetFullPath(directory);
        Folder = folder;
    }

    /// <summary>The store for a data folder: its PC folder (<see cref="PcFolder.For(string)"/>).</summary>
    public static ModelCatalogStore For(string dataDirectory)
    {
        var folder = PcFolder.For(dataDirectory);
        return new(folder.Directory, folder);
    }

    public string Directory { get; }
    public PcFolder? Folder { get; }
    public string FilePath => Path.Combine(Directory, FileName);
    public string StatusPath => Path.Combine(Directory, StatusFileName);

    /// <summary>The snapshot shipped with Martlet; empty when this build has none.</summary>
    public static ModelCatalogData Snapshot() => Shipped.Value;

    private static ModelCatalogData ReadSnapshot()
    {
        using var stream = typeof(ModelCatalogStore).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null) return new();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return ModelCatalogData.Parse(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)) ?? new();
    }

    /// <summary>The daily copy; null when there is none or it can't be read (the snapshot is used then).</summary>
    public ModelCatalogData? Cached()
    {
        try
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists || info.Length > ModelCatalogData.MaximumBytes) return null;
            return ModelCatalogData.Parse(File.ReadAllBytes(FilePath));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The newer of the daily copy and the snapshot, and which it is ("daily copy" or "snapshot").</summary>
    public (ModelCatalogData Data, string From) Current()
    {
        var snapshot = Snapshot();
        return Cached() is { } cached && cached.Built >= snapshot.Built ? (cached, "daily copy") : (snapshot, "snapshot");
    }

    /// <summary>The catalog worked out from <see cref="Current"/>, kept in memory until the daily copy changes. Building it
    /// takes a moment: call it off the reply path (a background task, a setup page).</summary>
    public ModelCatalog Load()
    {
        DateTime written = default;
        long length = -1;
        try
        {
            var info = new FileInfo(FilePath);
            if (info.Exists) (written, length) = (info.LastWriteTimeUtc, info.Length);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        if (Loaded.TryGetValue(Directory, out var held) && held.Written == written && held.Length == length) return held.Catalog;
        var catalog = ModelCatalog.Build(Current().Data);
        Loaded[Directory] = (written, length, catalog);
        return catalog;
    }

    /// <summary>Saves a new daily copy (all at once: readers see the old or the new file, never half of one).</summary>
    public string? Save(ModelCatalogData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return Write(FilePath, data.Write());
    }

    public ModelCatalogStatus Status()
    {
        try
        {
            if (!File.Exists(StatusPath) || new FileInfo(StatusPath).Length > 1024 * 1024) return new();
            return JsonSerializer.Deserialize<ModelCatalogStatus>(File.ReadAllText(StatusPath), StatusJson) is { Sources: not null } status
                ? status with { Sources = new(status.Sources, StringComparer.Ordinal) } : new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return new(); }
    }

    public string? SaveStatus(ModelCatalogStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return Write(StatusPath, JsonSerializer.Serialize(status, StatusJson));
    }

    /// <summary>Whether a refresh is due: none started in the last day (a start in the future, from a wrong clock, counts as
    /// none).</summary>
    public bool Due(DateTimeOffset now) => Due(Status(), now);

    public static bool Due(ModelCatalogStatus status, DateTimeOffset now) =>
        status.LastAttempt is not { } last || last > now + TimeSpan.FromMinutes(5) || now - last >= RefreshEvery;

    /// <summary>When the next refresh may start.</summary>
    public static DateTimeOffset? NextDue(ModelCatalogStatus status) => status.LastAttempt is { } last ? last + RefreshEvery : null;

    private string? Write(string path, string text)
    {
        try
        {
            if (Folder is { } folder) folder.Prepare();
            else System.IO.Directory.CreateDirectory(Directory);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, text);
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return $"Martlet couldn't save {Path.GetFileName(path)} in {Folder?.Where ?? "its folder"} ({error.GetType().Name}).";
        }
    }
}
