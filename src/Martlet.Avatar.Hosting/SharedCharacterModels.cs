using System.Security.Cryptography;
using Martlet.Avatars;
using Martlet.Core.Characters;
using Martlet.Core.Contracts;

namespace Martlet.Avatar.Hosting;

/// <summary>What <see cref="SharedCharacterModels.ReconcileAsync"/> did: the list, how many models were copied in and how
/// many copies were deleted, how many models still wait for pieces no reachable computer has, and the live models whose
/// copy is complete here (by ID).</summary>
public sealed record SharedCharacterModelsResult(CharacterModelLibrary Library, int Added, int Removed, int Waiting, IReadOnlySet<string> Local);

/// <summary>This computer's copy of the shared character models (<see cref="CharacterModelLibrary"/>): the list in
/// character-models.json and each model's files in character-models\&lt;first 16 hex digits of its ID&gt;, laid out as the
/// original model folder, so the character renderer shows a copy exactly like the file the owner chose. Pieces copied from
/// another computer wait in character-models-incoming until a model is complete and checked; then it moves into place in one
/// step. The original model files are never touched.</summary>
public static class SharedCharacterModels
{
    public const string DirectoryName = "character-models";
    public const string LibraryFile = "character-models.json";
    public const string IncomingDirectoryName = "character-models-incoming";
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static string Root(string dataDirectory) => Path.Combine(dataDirectory, DirectoryName);

    /// <summary>A model's short key: the first 16 hex digits of its ID (its folder name and automation key).</summary>
    public static string Key(string id) => id[..16];

    public static string Folder(string dataDirectory, string id) => Path.Combine(Root(dataDirectory), Key(id));

    /// <summary>The path of the copy's model file, as a character profile names it.</summary>
    public static string EntryPath(string dataDirectory, CharacterModel model) =>
        Path.Combine(Folder(dataDirectory, model.Id), model.Entry!.Replace('/', Path.DirectorySeparatorChar));

    public static AvatarRenderer Renderer(CharacterModel model) =>
        model.Renderer == CharacterModelLibrary.Vrm ? AvatarRenderer.Vrm : AvatarRenderer.Live2D;

    public static string RendererName(AvatarRenderer renderer) =>
        renderer == AvatarRenderer.Vrm ? CharacterModelLibrary.Vrm : CharacterModelLibrary.Live2D;

    /// <summary>The live model whose copy <paramref name="modelPath"/> (a character profile's model path) is, or null.</summary>
    public static CharacterModel? ForPath(string dataDirectory, CharacterModelLibrary library, string? modelPath) => modelPath is null ? null
        : library.Live.FirstOrDefault(m => string.Equals(EntryPath(dataDirectory, m), modelPath, StringComparison.OrdinalIgnoreCase));

    /// <summary>The key of the copy <paramref name="modelPath"/> lies in (live or removed), or null when it is not a copy.</summary>
    public static string? KeyOfPath(string dataDirectory, string? modelPath)
    {
        var root = Root(dataDirectory) + Path.DirectorySeparatorChar;
        if (modelPath is null || !modelPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = modelPath[root.Length..];
        return rest.Length > 17 && rest[16] == Path.DirectorySeparatorChar && rest[..16].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? rest[..16] : null;
    }

    /// <summary>The saved list, or null when this computer has none yet. A damaged copy reads as none (the hosts' copies
    /// restore it).</summary>
    public static CharacterModelLibrary? Load(string dataDirectory)
    {
        try { return CharacterModelLibrary.Parse(File.ReadAllBytes(Path.Combine(dataDirectory, LibraryFile))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ContractException) { return null; }
    }

    public static CharacterModelLibrary View(string dataDirectory) => Load(dataDirectory) ?? CharacterModelLibrary.Empty;

    /// <summary>Merges <paramref name="library"/> into the saved list (so a change saved meanwhile is never lost) and returns
    /// what was saved. An unchanged list is not written again, and nothing is written while there is nothing to keep.</summary>
    public static CharacterModelLibrary Commit(string dataDirectory, CharacterModelLibrary library)
    {
        var saved = Load(dataDirectory);
        var merged = CharacterModelLibrary.Merge(saved ?? CharacterModelLibrary.Empty, library);
        if (saved is not null ? saved.Digest() == merged.Digest() : merged.Models.Count == 0) return saved ?? merged;
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, LibraryFile);
        var temporary = Path.Combine(dataDirectory, $"character-models.{Guid.NewGuid():N}.tmp");
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

    /// <summary>Whether this computer holds every file of <paramref name="model"/> (by name and length; the contents were
    /// checked when the copy was made).</summary>
    public static bool IsComplete(string dataDirectory, CharacterModel model)
    {
        if (model.Removed || !Directory.Exists(Folder(dataDirectory, model.Id))) return false;
        foreach (var file in model.Files!)
        {
            var info = new FileInfo(FilePath(dataDirectory, model, file));
            if (!info.Exists || info.Length != file.Bytes) return false;
        }
        return true;
    }

    /// <summary>A name for a model file: its file name without <c>.vrm</c> or <c>.model3.json</c>, on one line, at most
    /// 80 characters.</summary>
    public static string NameFor(string modelPath)
    {
        var file = Path.GetFileName(modelPath);
        var name = file.EndsWith(".model3.json", StringComparison.OrdinalIgnoreCase) ? file[..^".model3.json".Length]
            : Path.GetFileNameWithoutExtension(file);
        name = new string(name.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        if (name.Length > CharacterModelLibrary.MaximumNameLength) name = name[..CharacterModelLibrary.MaximumNameLength].TrimEnd();
        return CharacterModelLibrary.IsName(name) ? name : "Character";
    }

    /// <summary>Adds the model at <paramref name="modelPath"/> to the shared list: Martlet keeps its own copy (the original can
    /// be moved or deleted afterwards) and the next sync shares it. Adding a model the list already has keeps that entry (and
    /// renames it). Throws <see cref="ContractException"/> when the model breaks the renderer's rules or the list has no
    /// room.</summary>
    public static async Task<CharacterModel> ImportAsync(string dataDirectory, AvatarRenderer renderer, string modelPath, string name, string by,
        DateTimeOffset now, CancellationToken token)
    {
        var assets = await LocalAvatarFiles.ReadModelAsync(renderer, modelPath, token);
        var files = assets.Select(a => CharacterModelLibrary.File(a.Name, a.Bytes)).ToArray();
        var entry = renderer == AvatarRenderer.Vrm ? assets[0].Name : Path.GetFileName(modelPath);
        var id = CharacterModelLibrary.ModelId(RendererName(renderer), entry, files);
        await Gate.WaitAsync(token);
        try
        {
            var library = View(dataDirectory).Add(name, RendererName(renderer), entry, files, by, now);
            var model = library.Find(id)!;
            if (!IsComplete(dataDirectory, model))
            {
                var staging = Staging(dataDirectory, model.Id);
                try
                {
                    foreach (var asset in assets)
                    {
                        var path = Path.Combine(staging, asset.Name.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        await File.WriteAllBytesAsync(path, asset.Bytes, token);
                    }
                    Place(dataDirectory, model, staging);
                }
                finally { DeleteDirectory(staging); }
            }
            Commit(dataDirectory, library);
            return model;
        }
        finally { Gate.Release(); }
    }

    /// <summary>Removes a model on every computer and deletes this computer's copy, unless its key is in
    /// <paramref name="keep"/> (the character this computer shows).</summary>
    public static async Task RemoveAsync(string dataDirectory, string id, string by, DateTimeOffset now, IReadOnlySet<string>? keep,
        CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try
        {
            Commit(dataDirectory, View(dataDirectory).Remove(id, by, now));
            if (keep?.Contains(Key(id)) != true) DeleteDirectory(Folder(dataDirectory, id));
        }
        finally { Gate.Release(); }
    }

    /// <summary>Brings this computer's copies in step with the saved list: each live model it lacks is copied piece by piece
    /// with <paramref name="fetch"/> (a piece by SHA-256 from another computer, or null when none has it yet), checked and
    /// moved into place; copies of models no longer in the list are deleted, except the keys in <paramref name="keep"/> (the
    /// character this computer shows, until another is chosen). Pieces already copied are kept, so an interrupted copy
    /// continues where it stopped.</summary>
    public static async Task<SharedCharacterModelsResult> ReconcileAsync(string dataDirectory, Func<string, CancellationToken, Task<byte[]?>> fetch,
        IReadOnlySet<string>? keep, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        var library = View(dataDirectory);
        var ready = new List<CharacterModel>();
        var waiting = 0;
        foreach (var model in library.Live.Where(m => !IsComplete(dataDirectory, m)))
        {
            var pieces = Pieces(dataDirectory, model.Id);
            var complete = true;
            foreach (var file in model.Files!)
            {
                for (var index = 0; index < file.Chunks.Count; index++)
                {
                    var sha256 = file.Chunks[index];
                    var length = CharacterModelLibrary.ChunkLength(file.Bytes, index);
                    var path = Path.Combine(pieces, sha256);
                    if (new FileInfo(path) is { Exists: true } info && info.Length == length) continue;
                    var bytes = await fetch(sha256, token);
                    if (bytes is null || bytes.Length != length || Convert.ToHexStringLower(SHA256.HashData(bytes)) != sha256)
                    {
                        complete = false;
                        continue;
                    }
                    Directory.CreateDirectory(pieces);
                    var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        await File.WriteAllBytesAsync(temporary, bytes, token);
                        File.Move(temporary, path, overwrite: true);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
            }
            if (complete) ready.Add(model);
            else waiting++;
        }

        var added = 0;
        var removed = 0;
        await Gate.WaitAsync(token);
        try
        {
            library = View(dataDirectory);
            foreach (var model in ready.Where(m => library.Find(m.Id) is { Removed: false } && !IsComplete(dataDirectory, m)))
            {
                if (await AssembleAsync(dataDirectory, model, token)) added++;
                else waiting++;
            }
            var live = library.Live.Select(m => Key(m.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(Root(dataDirectory)))
                foreach (var folder in Directory.EnumerateDirectories(Root(dataDirectory)))
                {
                    var key = Path.GetFileName(folder);
                    if (live.Contains(key) || keep?.Contains(key) == true) continue;
                    if (DeleteDirectory(folder)) removed++;
                }
            var incoming = Path.Combine(dataDirectory, IncomingDirectoryName);
            if (Directory.Exists(incoming))
            {
                foreach (var folder in Directory.EnumerateDirectories(incoming).Where(f => !live.Contains(Path.GetFileName(f))))
                    DeleteDirectory(folder);
                if (!Directory.EnumerateFileSystemEntries(incoming).Any()) DeleteDirectory(incoming);
            }
            var local = library.Live.Where(m => IsComplete(dataDirectory, m)).Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
            return new(library, added, removed, waiting, local);
        }
        finally { Gate.Release(); }
    }

    /// <summary>Reads one piece of a model this computer holds, for a computer that lacks it; null when this computer has no
    /// such piece or its copy changed.</summary>
    public static async Task<byte[]?> ReadChunkAsync(string dataDirectory, CharacterModel model, string sha256, CancellationToken token)
    {
        foreach (var file in model.Files ?? [])
        {
            var index = file.Chunks.ToList().IndexOf(sha256);
            if (index < 0) continue;
            var length = CharacterModelLibrary.ChunkLength(file.Bytes, index);
            var bytes = new byte[length];
            try
            {
                await using var stream = new FileStream(FilePath(dataDirectory, model, file), FileMode.Open, FileAccess.Read, FileShare.Read,
                    4096, FileOptions.Asynchronous);
                if (stream.Length != file.Bytes) return null;
                stream.Position = (long)index * CharacterModelLibrary.ChunkBytes;
                await stream.ReadExactlyAsync(bytes, token);
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException or EndOfStreamException) { return null; }
            return Convert.ToHexStringLower(SHA256.HashData(bytes)) == sha256 ? bytes : null;
        }
        return null;
    }

    /// <summary>Whether <paramref name="error"/> is one a sync reports rather than throws past.</summary>
    public static bool IsFailure(Exception error) => error is IOException or UnauthorizedAccessException or ContractException;

    private static string FilePath(string dataDirectory, CharacterModel model, CharacterModelFile file) =>
        Path.Combine(Folder(dataDirectory, model.Id), file.Path.Replace('/', Path.DirectorySeparatorChar));

    private static string Pieces(string dataDirectory, string id) => Path.Combine(dataDirectory, IncomingDirectoryName, Key(id));

    private static string Staging(string dataDirectory, string id) =>
        Path.Combine(dataDirectory, IncomingDirectoryName, Key(id) + "-" + Guid.NewGuid().ToString("N")[..8]);

    // Joins a model's pieces into its files in a staging folder, checks every file's SHA-256 and moves the folder into place.
    // Pieces that make a wrong file are deleted, so the next sync copies them again.
    private static async Task<bool> AssembleAsync(string dataDirectory, CharacterModel model, CancellationToken token)
    {
        var pieces = Pieces(dataDirectory, model.Id);
        var staging = Staging(dataDirectory, model.Id);
        try
        {
            foreach (var file in model.Files!)
            {
                var path = Path.Combine(staging, file.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                {
                    foreach (var sha256 in file.Chunks)
                    {
                        var bytes = await File.ReadAllBytesAsync(Path.Combine(pieces, sha256), token);
                        hash.AppendData(bytes);
                        await output.WriteAsync(bytes, token);
                    }
                }
                if (Convert.ToHexStringLower(hash.GetHashAndReset()) != file.Sha256)
                {
                    DeleteDirectory(pieces);
                    return false;
                }
            }
            Place(dataDirectory, model, staging);
            DeleteDirectory(pieces);
            return true;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return false; }
        finally { DeleteDirectory(staging); }
    }

    private static void Place(string dataDirectory, CharacterModel model, string staging)
    {
        var folder = Folder(dataDirectory, model.Id);
        Directory.CreateDirectory(Root(dataDirectory));
        DeleteDirectory(folder);
        Directory.Move(staging, folder);
    }

    private static bool DeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return false;
            Directory.Delete(path, recursive: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}
