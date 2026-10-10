using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Lorebooks;

/// <summary>The saved library, its revision (a digest of the file; null when no file exists yet) and, when the file could not be
/// used, why. A failed load returns an empty library so conversations go ahead without lore.</summary>
public sealed record LorebookLoadResult(LorebookLibrary Library, string? Revision, string? Error)
{
    public bool Loaded => Error is null;
}

public sealed record LorebookSaveResult(bool Saved, LorebookLibrary Library, string? Revision, string? Error);

/// <summary>All lorebooks on this PC, in one local JSON file (<c>lorebooks.json</c> in the data directory). Saves replace the file
/// atomically and refuse to overwrite a file that changed since it was read.</summary>
public sealed class LorebookStore
{
    public const string FileName = "lorebooks.json";
    public const int MaximumFileBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = CreateOptions();
    private readonly SemaphoreSlim gate = new(1, 1);
    private (DateTime WrittenUtc, long Length, LorebookLoadResult Result)? cache;

    public LorebookStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        directory = dataDirectory;
    }

    private volatile string directory;

    /// <summary>The folder the lorebooks are in: the data directory, or the folder of the account signed in
    /// (docs/ACCOUNTS.md, "Account settings").</summary>
    public string DataDirectory => directory;
    public string FilePath => Path.Combine(DataDirectory, FileName);

    /// <summary>Uses the lorebooks in <paramref name="folder"/> from now on (another account's, after a switch); the next load
    /// reads them.</summary>
    public void UseDirectory(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        gate.Wait();
        try
        {
            directory = folder;
            cache = null;
        }
        finally { gate.Release(); }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            MaxDepth = 16
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    public async Task<LorebookLoadResult> LoadAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { return await LoadCoreAsync(token).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public async Task<LorebookSaveResult> SaveAsync(LorebookLibrary library, string? expectedRevision, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(library);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var current = await LoadCoreAsync(token).ConfigureAwait(false);
            if (current.Revision != expectedRevision)
                return new(false, current.Library, current.Revision,
                    "The lorebooks were changed somewhere else since they were opened. Reload them and make your changes again.");
            return await WriteAsync(library, token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    /// <summary>Applies <paramref name="change"/> to the current library and saves it, for edits that don't come from an editor
    /// (for example a character card's lorebook). A file that can't be read is never overwritten.</summary>
    public async Task<LorebookSaveResult> UpdateAsync(Func<LorebookLibrary, LorebookLibrary> change, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var current = await LoadCoreAsync(token).ConfigureAwait(false);
            if (!current.Loaded) return new(false, current.Library, current.Revision, current.Error);
            LorebookLibrary next;
            try { next = change(current.Library); }
            catch (ContractException error) { return new(false, current.Library, current.Revision, error.Message); }
            return await WriteAsync(next, token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task<LorebookLoadResult> LoadCoreAsync(CancellationToken token)
    {
        var path = FilePath;
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists)
            {
                cache = null;
                return new(LorebookLibrary.Create(), null, null);
            }
            if (cache is { } cached && cached.WrittenUtc == info.LastWriteTimeUtc && cached.Length == info.Length)
                return cached.Result;
            if (info.Length > MaximumFileBytes)
                return Failed($"{FileName} is larger than {MaximumFileBytes / (1024 * 1024)} MB, so Martlet doesn't use it.", null);
            byte[] bytes;
            await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                81_920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                bytes = new byte[input.Length];
                await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            }
            var revision = Convert.ToHexStringLower(SHA256.HashData(bytes));
            LorebookLoadResult result;
            try
            {
                var library = JsonSerializer.Deserialize<LorebookLibrary>(bytes, Options)
                    ?? throw new ContractException(ErrorCode.InvalidContract, "The file is empty.");
                library.Validate();
                result = new(library, revision, null);
            }
            catch (Exception error) when (error is JsonException or ContractException or NotSupportedException)
            {
                result = Failed($"{FileName} can't be used: {(error is ContractException ? error.Message : "it is not valid lorebook JSON.")} " +
                    "Fix or remove the file, then reload. Martlet won't overwrite it.", revision);
            }
            cache = (info.LastWriteTimeUtc, info.Length, result);
            return result;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Failed($"{FileName} could not be read. Check access to your data folder, then reload.", null);
        }
    }

    private static LorebookLoadResult Failed(string error, string? revision) => new(LorebookLibrary.Create(), revision, error);

    private async Task<LorebookSaveResult> WriteAsync(LorebookLibrary library, CancellationToken token)
    {
        try { library.Validate(); }
        catch (ContractException error) { return new(false, library, null, error.Message); }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(library, Options);
        if (bytes.Length > MaximumFileBytes)
            return new(false, library, null, $"The lorebooks are too large to save (over {MaximumFileBytes / (1024 * 1024)} MB). Remove some entries.");
        var path = FilePath;
        var temporary = Path.Combine(DataDirectory, $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(DataDirectory);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81_920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await output.WriteAsync(bytes, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
            }
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(temporary, path, overwrite: true);
                    break;
                }
                catch (IOException) when (attempt < 4)
                {
                    await Task.Delay(50 * (attempt + 1), token).ConfigureAwait(false);
                }
            }
            cache = null;
            return new(true, library, Convert.ToHexStringLower(SHA256.HashData(bytes)), null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new(false, library, null, $"The lorebooks could not be saved to {FileName}. Check access to your data folder and free space, then try again.");
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
