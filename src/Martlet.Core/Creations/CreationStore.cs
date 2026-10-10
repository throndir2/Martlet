using System.Security.Cryptography;
using System.Text.Json;
using Martlet.Core.Contracts;

namespace Martlet.Core.Creations;

/// <summary>One asset of a new creation: its name, media type and bytes.</summary>
public sealed record CreationDraftAsset(string Name, string MediaType, ReadOnlyMemory<byte> Bytes);

/// <summary>A creation to add (<see cref="CreationStore.AddAsync"/>): its kind (registered), title, optional summary, text,
/// length and kind metadata (a JSON object), who made it and its assets.</summary>
public sealed record CreationDraft
{
    public required string Kind { get; init; }
    public required string Title { get; init; }
    public string? Summary { get; init; }
    public string? Text { get; init; }
    public TimeSpan? Duration { get; init; }
    public JsonElement? Metadata { get; init; }
    public required CreationAuthor CreatedBy { get; init; }
    public required IReadOnlyList<CreationDraftAsset> Assets { get; init; }
}

/// <summary>What <see cref="CreationStore.ReconcileAsync"/> did: the list, how many assets were copied in and how many
/// files were deleted, how many creations still wait for pieces no reachable computer has, and the live creations whose
/// assets are all here (by ID).</summary>
public sealed record CreationReconcileResult(CreationLibrary Library, int Added, int Removed, int Waiting, IReadOnlySet<string> Local);

/// <summary>This computer's copy of Martlet's creations (<see cref="CreationLibrary"/>): the list in creations.json and each
/// asset once, by SHA-256, in creations\&lt;sha256&gt;.bin. Pieces copied from another computer wait in creations-incoming
/// until the asset is complete and its SHA-256 checks; then it moves into place in one step. Files no live creation uses are
/// deleted.</summary>
public static class CreationStore
{
    public const string LibraryFile = "creations.json";
    public const string DirectoryName = "creations";
    public const string IncomingDirectoryName = "creations-incoming";
    private const string AssetExtension = ".bin";
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Raised (with the data directory) after a creation is added, renamed or deleted on this computer, so the
    /// Creations page shows it and the sync shares it soon. Not raised for changes a sync brings in.</summary>
    public static event Action<string>? Changed;

    public static string Root(string dataDirectory) => Path.Combine(dataDirectory, DirectoryName);

    public static string AssetPath(string dataDirectory, string sha256)
    {
        ContractRules.Require(CreationLibrary.IsSha256(sha256), "An asset's SHA-256 is invalid.");
        return Path.Combine(Root(dataDirectory), sha256 + AssetExtension);
    }

    /// <summary>The saved list, or null when this computer has none yet. A damaged copy reads as none (the hosts' copies
    /// restore it).</summary>
    public static CreationLibrary? Load(string dataDirectory)
    {
        try { return CreationLibrary.Parse(File.ReadAllBytes(Path.Combine(dataDirectory, LibraryFile))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ContractException) { return null; }
    }

    public static CreationLibrary View(string dataDirectory) => Load(dataDirectory) ?? CreationLibrary.Empty;

    /// <summary>Merges <paramref name="library"/> into the saved list (so a change saved meanwhile is never lost) and returns
    /// what was saved. An unchanged list is not written again, and nothing is written while there is nothing to keep.</summary>
    public static CreationLibrary Commit(string dataDirectory, CreationLibrary library)
    {
        var saved = Load(dataDirectory);
        var merged = CreationLibrary.Merge(saved ?? CreationLibrary.Empty, library);
        if (saved is not null ? saved.Digest() == merged.Digest() : merged.Creations.Count == 0) return saved ?? merged;
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, LibraryFile);
        var temporary = Path.Combine(dataDirectory, $"creations.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, merged.Write());
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return merged;
    }

    /// <summary>Adds a creation of a registered kind: its assets are kept here and the next sync shares it with every paired
    /// Martlet computer. Throws <see cref="ContractException"/> when the kind isn't registered, the creation breaks its kind's
    /// rules or there is no room (see <see cref="CreationLibrary.Add"/>).</summary>
    public static async Task<Creation> AddAsync(string dataDirectory, CreationDraft draft, CreationRegistry registry, DateTimeOffset now,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(registry);
        var kind = registry.Find(draft.Kind);
        ContractRules.Require(kind is not null, $"Martlet doesn't know how to keep a {draft.Kind}.");
        kind!.Check([.. draft.Assets.Select(a => (a.Name, a.MediaType, (long)a.Bytes.Length))]);
        ContractRules.Require(CreationLibrary.IsTitle(draft.Title), $"Give it a title of at most {CreationLibrary.MaximumTitleLength} characters on one line.");
        var assets = draft.Assets.Select(a => CreationLibrary.Asset(a.Name, a.MediaType, a.Bytes.Span)).ToArray();
        var creation = new Creation
        {
            Id = CreationLibrary.NewId(), Kind = kind.Name, KindVersion = kind.KindVersion, Title = draft.Title, Summary = draft.Summary,
            Text = draft.Text, DurationMs = draft.Duration is { } duration ? (long)duration.TotalMilliseconds : null, CreatedBy = draft.CreatedBy,
            CreatedAt = now.ToUniversalTime(), Metadata = draft.Metadata?.Clone(), Assets = assets, AutoCleanup = kind.AutoCleanup,
            Revision = 1, UpdatedAt = now.ToUniversalTime(), UpdatedBy = draft.CreatedBy.Device
        };
        await Gate.WaitAsync(token);
        Creation added;
        try
        {
            var library = View(dataDirectory).Add(creation, now);
            foreach (var (asset, draftAsset) in assets.Zip(draft.Assets))
                await PlaceAsync(dataDirectory, asset.Sha256, draftAsset.Bytes, token);
            library = Commit(dataDirectory, library);
            DeleteUnused(dataDirectory, library);
            added = library.Find(creation.Id)!;
        }
        finally { Gate.Release(); }
        Changed?.Invoke(dataDirectory);
        return added;
    }

    /// <summary>Gives a creation a new title on every computer.</summary>
    public static async Task RenameAsync(string dataDirectory, string id, string title, string by, DateTimeOffset now, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try { Commit(dataDirectory, View(dataDirectory).Rename(id, title, by, now)); }
        finally { Gate.Release(); }
        Changed?.Invoke(dataDirectory);
    }

    /// <summary>Deletes a creation on every computer (a tombstone travels) and this computer's copy of its assets that no
    /// other creation uses.</summary>
    public static async Task RemoveAsync(string dataDirectory, string id, string by, DateTimeOffset now, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try { DeleteUnused(dataDirectory, Commit(dataDirectory, View(dataDirectory).Remove(id, by, now))); }
        finally { Gate.Release(); }
        Changed?.Invoke(dataDirectory);
    }

    /// <summary>Whether this computer holds every asset of <paramref name="creation"/> (by length; the contents were checked
    /// when each was placed).</summary>
    public static bool IsComplete(string dataDirectory, Creation creation) =>
        !creation.Removed && creation.Assets!.All(a => Holds(dataDirectory, a));

    private static bool Holds(string dataDirectory, CreationAsset asset) =>
        new FileInfo(AssetPath(dataDirectory, asset.Sha256)) is { Exists: true } info && info.Length == asset.Bytes;

    /// <summary>The bytes of <paramref name="creation"/>'s asset <paramref name="name"/>, checked against its SHA-256; null
    /// when this computer doesn't hold it (yet).</summary>
    public static async Task<byte[]?> ReadAssetAsync(string dataDirectory, Creation creation, string name, CancellationToken token)
    {
        if (creation.Asset(name) is not { } asset) return null;
        byte[] bytes;
        try { bytes = await File.ReadAllBytesAsync(AssetPath(dataDirectory, asset.Sha256), token); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
        return bytes.LongLength == asset.Bytes && Convert.ToHexStringLower(SHA256.HashData(bytes)) == asset.Sha256 ? bytes : null;
    }

    /// <summary>A creation's assets on this computer, for its kind's handler.</summary>
    public static ICreationAssets Assets(string dataDirectory, Creation creation) => new LocalAssets(dataDirectory, creation);

    /// <summary>Brings this computer's assets in step with the saved list: each asset of a live creation it lacks is copied
    /// piece by piece with <paramref name="fetch"/> (a piece by SHA-256 from another computer, or null when none has it yet),
    /// checked and moved into place; files no live creation uses are deleted. Pieces already copied are kept, so an
    /// interrupted copy continues where it stopped.</summary>
    public static async Task<CreationReconcileResult> ReconcileAsync(string dataDirectory, Func<string, CancellationToken, Task<byte[]?>> fetch,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        var library = View(dataDirectory);
        var added = 0;
        foreach (var asset in library.LiveAssets().Values.Where(a => !Holds(dataDirectory, a)))
        {
            var pieces = Pieces(dataDirectory, asset.Sha256);
            var complete = true;
            for (var index = 0; index < asset.Chunks.Count; index++)
            {
                var sha256 = asset.Chunks[index];
                var length = CreationLibrary.ChunkLength(asset.Bytes, index);
                var path = Path.Combine(pieces, sha256);
                if (new FileInfo(path) is { Exists: true } info && info.Length == length) continue;
                var bytes = await fetch(sha256, token);
                if (bytes is null || bytes.Length != length || Convert.ToHexStringLower(SHA256.HashData(bytes)) != sha256)
                {
                    complete = false;
                    continue;
                }
                Directory.CreateDirectory(pieces);
                await WriteAtomicallyAsync(path, bytes, token);
            }
            if (complete && await AssembleAsync(dataDirectory, asset, token)) added++;
        }

        await Gate.WaitAsync(token);
        try
        {
            library = View(dataDirectory);
            var removed = DeleteUnused(dataDirectory, library);
            var local = library.Live.Where(c => IsComplete(dataDirectory, c)).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            return new(library, added, removed, library.Live.Count - local.Count, local);
        }
        finally { Gate.Release(); }
    }

    /// <summary>Reads one piece of an asset this computer holds, for a computer that lacks it; null when this computer has no
    /// such piece or its copy changed.</summary>
    public static async Task<byte[]?> ReadChunkAsync(string dataDirectory, CreationLibrary library, string sha256, CancellationToken token)
    {
        foreach (var asset in library.LiveAssets().Values)
        {
            var index = asset.Chunks.ToList().IndexOf(sha256);
            if (index < 0) continue;
            var bytes = new byte[CreationLibrary.ChunkLength(asset.Bytes, index)];
            try
            {
                await using var stream = new FileStream(AssetPath(dataDirectory, asset.Sha256), FileMode.Open, FileAccess.Read, FileShare.Read,
                    4096, FileOptions.Asynchronous);
                if (stream.Length != asset.Bytes) continue;
                stream.Position = (long)index * CreationLibrary.ChunkBytes;
                await stream.ReadExactlyAsync(bytes, token);
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException or EndOfStreamException) { continue; }
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) == sha256) return bytes;
        }
        return null;
    }

    /// <summary>Whether <paramref name="error"/> is one a sync or an edit reports rather than throws past.</summary>
    public static bool IsFailure(Exception error) => error is IOException or UnauthorizedAccessException or ContractException;

    /// <summary>Moves every creation kept in <paramref name="fromDirectory"/> (the list, the asset files, copies in progress and
    /// the last sync record) into <paramref name="toDirectory"/>, merged with what is there, and deletes them from
    /// <paramref name="fromDirectory"/>. Nothing is lost: the list is saved in its new place before anything is deleted, an asset
    /// is deleted only when the new place holds a file of the same SHA-256 and length, and running it again finishes an
    /// interrupted move. Throws <see cref="ContractException"/> and moves nothing when the old list can't be read. Returns how
    /// many creations the old list had and how many asset files moved.</summary>
    public static async Task<(int Creations, int Assets)> MoveAsync(string fromDirectory, string toDirectory, CancellationToken token)
    {
        ContractRules.Require(!string.Equals(Path.GetFullPath(fromDirectory).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(toDirectory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase), "Creations can't move to the same folder.");
        int creations, assets = 0;
        await Gate.WaitAsync(token);
        try
        {
            var listPath = Path.Combine(fromDirectory, LibraryFile);
            var from = Load(fromDirectory);
            ContractRules.Require(from is not null || !File.Exists(listPath), "The creations list in the data folder can't be read, so it was left there.");
            creations = from?.Creations.Count(c => !c.Removed) ?? 0;
            if (from is not null) Commit(toDirectory, from);

            var root = Root(fromDirectory);
            if (Directory.Exists(root))
                foreach (var file in Directory.EnumerateFiles(root))
                {
                    var name = Path.GetFileName(file);
                    var target = Path.Combine(Root(toDirectory), name);
                    if (!name.EndsWith(AssetExtension, StringComparison.Ordinal)) File.Delete(file);
                    else if (new FileInfo(target) is { Exists: true } there && there.Length == new FileInfo(file).Length) File.Delete(file);
                    else
                    {
                        Directory.CreateDirectory(Root(toDirectory));
                        File.Move(file, target, overwrite: true);
                        assets++;
                    }
                }
            var incoming = Path.Combine(fromDirectory, IncomingDirectoryName);
            if (Directory.Exists(incoming))
                foreach (var folder in Directory.EnumerateDirectories(incoming))
                {
                    var target = Path.Combine(toDirectory, IncomingDirectoryName, Path.GetFileName(folder));
                    if (Directory.Exists(target)) DeleteDirectory(folder);
                    else
                    {
                        Directory.CreateDirectory(Path.Combine(toDirectory, IncomingDirectoryName));
                        Directory.Move(folder, target);
                    }
                }
            var syncState = Path.Combine(fromDirectory, CreationSyncState.FileName);
            if (File.Exists(syncState))
            {
                if (File.Exists(Path.Combine(toDirectory, CreationSyncState.FileName))) File.Delete(syncState);
                else
                {
                    Directory.CreateDirectory(toDirectory);
                    File.Move(syncState, Path.Combine(toDirectory, CreationSyncState.FileName));
                }
            }
            if (File.Exists(listPath)) File.Delete(listPath);
            foreach (var folder in new[] { root, incoming })
                if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) DeleteDirectory(folder);
        }
        finally { Gate.Release(); }
        Changed?.Invoke(toDirectory);
        return (creations, assets);
    }

    private static string Pieces(string dataDirectory, string sha256) => Path.Combine(dataDirectory, IncomingDirectoryName, sha256[..16]);

    private static async Task PlaceAsync(string dataDirectory, string sha256, ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        var path = AssetPath(dataDirectory, sha256);
        if (new FileInfo(path) is { Exists: true } info && info.Length == bytes.Length) return;
        Directory.CreateDirectory(Root(dataDirectory));
        await WriteAtomicallyAsync(path, bytes, token);
    }

    private static async Task WriteAtomicallyAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                await output.WriteAsync(bytes, token);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    // Joins an asset's pieces into one file, checks its SHA-256 and moves it into place. Pieces that make a wrong file are
    // deleted, so the next sync copies them again.
    private static async Task<bool> AssembleAsync(string dataDirectory, CreationAsset asset, CancellationToken token)
    {
        var pieces = Pieces(dataDirectory, asset.Sha256);
        Directory.CreateDirectory(Root(dataDirectory));
        var temporary = AssetPath(dataDirectory, asset.Sha256) + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                foreach (var sha256 in asset.Chunks)
                {
                    var bytes = await File.ReadAllBytesAsync(Path.Combine(pieces, sha256), token);
                    hash.AppendData(bytes);
                    await output.WriteAsync(bytes, token);
                }
            }
            if (Convert.ToHexStringLower(hash.GetHashAndReset()) != asset.Sha256)
            {
                DeleteDirectory(pieces);
                return false;
            }
            File.Move(temporary, AssetPath(dataDirectory, asset.Sha256), overwrite: true);
            DeleteDirectory(pieces);
            return true;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return false; }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    // Deletes asset files and waiting pieces no live creation uses; returns how many asset files went.
    private static int DeleteUnused(string dataDirectory, CreationLibrary library)
    {
        var live = library.LiveAssets();
        var removed = 0;
        var root = Root(dataDirectory);
        if (Directory.Exists(root))
            foreach (var file in Directory.EnumerateFiles(root))
            {
                var name = Path.GetFileName(file);
                var sha256 = name.EndsWith(AssetExtension, StringComparison.Ordinal) ? name[..^AssetExtension.Length] : "";
                if (live.ContainsKey(sha256)) continue;
                // A temporary file of a copy in progress is left to it.
                if (name.EndsWith(".tmp", StringComparison.Ordinal) && File.GetLastWriteTimeUtc(file) > DateTime.UtcNow.AddHours(-1)) continue;
                try
                {
                    File.Delete(file);
                    if (sha256.Length > 0) removed++;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        var incoming = Path.Combine(dataDirectory, IncomingDirectoryName);
        if (Directory.Exists(incoming))
        {
            var keys = live.Keys.Select(k => k[..16]).ToHashSet(StringComparer.Ordinal);
            foreach (var folder in Directory.EnumerateDirectories(incoming).Where(f => !keys.Contains(Path.GetFileName(f))))
                DeleteDirectory(folder);
            if (!Directory.EnumerateFileSystemEntries(incoming).Any()) DeleteDirectory(incoming);
        }
        return removed;
    }

    private static void DeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private sealed class LocalAssets(string dataDirectory, Creation creation) : ICreationAssets
    {
        public bool IsComplete => CreationStore.IsComplete(dataDirectory, creation);

        public async ValueTask<byte[]?> ReadAsync(string name, CancellationToken cancellationToken) =>
            await ReadAssetAsync(dataDirectory, creation, name, cancellationToken);
    }
}
