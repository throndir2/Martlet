using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Core.Voices;

public sealed record VoiceImportRequest(string Name, string SourcePath, string Transcript,
    VoiceEngine Engine, VoiceAssetPurpose Purpose, VoiceRightsBasis Rights, bool LocalStorageConfirmed)
{
    public override string ToString() => "Voice import request (private content omitted)";
}

public sealed record VoiceAsset(Guid Id, string Name, string Transcript, VoiceEngine Engine,
    VoiceAssetPurpose Purpose, VoiceRightsBasis Rights, DateTimeOffset ImportedAtUtc,
    PcmWaveInfo Wave, string AudioSha256, string Revision)
{
    public override string ToString() => $"Voice asset {Id} (private content omitted)";
}

public sealed class VoiceLibrary
{
    public const int MaximumAudioBytes = 64 * 1024 * 1024;
    public const int MaximumAssets = 64;
    public const long MaximumLibraryBytes = 512L * 1024 * 1024;
    private const int MaximumMetadataBytes = 128 * 1024;
    private const int MaximumBundleBytes = MaximumAudioBytes + MaximumMetadataBytes + 4096;
    private readonly string directory;
    // Tests hold the real staging/commit boundary without replacing filesystem operations.
    internal Action<CancellationToken>? BeforePublication { get; init; }
    private static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        MaxDepth = 8,
        Converters = { new ExactEnumConverterFactory() }
    };

    public VoiceLibrary(string absoluteDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absoluteDirectory);
        if (!Path.IsPathFullyQualified(absoluteDirectory))
            throw new ArgumentException("Use an absolute local voice library directory.", nameof(absoluteDirectory));
        directory = Path.GetFullPath(absoluteDirectory);
    }

    public async Task<VoiceAsset> ImportAsync(VoiceImportRequest request, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateFields(request.Name, request.Transcript, request.Engine, request.Purpose, request.Rights);
        ContractRules.Require(request.LocalStorageConfirmed,
            "Confirm voice rights and local storage before importing. This does not authorize upload, training or synthesis.");
        token.ThrowIfCancellationRequested();
        var audio = await ReadFileAsync(LocalPath(request.SourcePath), MaximumAudioBytes, token);
        try
        {
            var wave = InspectAudio(audio, request.Purpose);
            var metadata = new AssetDocument(1, Guid.NewGuid(), request.Name, request.Transcript,
                request.Engine, request.Purpose, request.Rights, DateTimeOffset.UtcNow, Hash(audio));
            var metadataBytes = JsonSerializer.SerializeToUtf8Bytes(metadata, Json);
            ContractRules.Require(metadataBytes.Length <= MaximumMetadataBytes, "Voice metadata exceeds the import limit.");
            LocalPath(directory);
            Directory.CreateDirectory(directory);
            using var ownership = AcquireWriter();
            var files = Inventory();
            ContractRules.Require(files.Length < MaximumAssets &&
                files.Sum(file => new FileInfo(LocalPath(file)).Length) + audio.Length + metadataBytes.Length + 4096 <= MaximumLibraryBytes,
                "Voice library is full. Remove an imported copy before adding another; source files are never removed.");
            var pending = LocalPath(Path.Combine(directory, $"{metadata.Id:N}.pending"));
            var destination = AssetPath(metadata.Id);
            var staged = false;
            try
            {
                await using (var output = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    staged = true;
                    using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                    {
                        await WriteEntryAsync(archive, "manifest.json", metadataBytes, token);
                        await WriteEntryAsync(archive, "audio.wav", audio, token);
                    }
                    await output.FlushAsync(token);
                    output.Flush(flushToDisk: true);
                }
                // Compute the inspection receipt before publication so a committed import is never reported as canceled.
                var stagedBytes = await ReadFileAsync(pending, MaximumBundleBytes, token);
                string revision;
                try { revision = Hash(stagedBytes); }
                finally { CryptographicOperations.ZeroMemory(stagedBytes); }
                BeforePublication?.Invoke(token);
                token.ThrowIfCancellationRequested();
                LocalPath(directory);
                File.Move(pending, destination, overwrite: false);
                staged = false;
                return ToAsset(metadata, wave, revision);
            }
            finally
            {
                if (staged)
                {
                    try { File.Delete(LocalPath(pending)); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        throw new IOException("Import staging cleanup failed. A private .pending file remains in the voice library; no voice was published.", ex);
                    }
                }
            }
        }
        finally { CryptographicOperations.ZeroMemory(audio); }
    }

    public async Task<IReadOnlyList<VoiceAsset>> ListAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        LocalPath(directory);
        if (!Directory.Exists(directory)) return Array.Empty<VoiceAsset>();
        using var ownership = AcquireWriter();
        var assets = new List<VoiceAsset>();
        foreach (var file in Inventory())
        {
            token.ThrowIfCancellationRequested();
            ContractRules.Require(Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out var id),
                "An invalid voice library filename was found. Preserve the file and repair the library.");
            assets.Add(await ReadAsync(id, token));
        }
        return assets.OrderBy(asset => asset.Name, StringComparer.Ordinal).ThenBy(asset => asset.Id).ToArray();
    }

    public async Task DeleteAsync(VoiceAsset selected, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(selected);
        token.ThrowIfCancellationRequested();
        LocalPath(directory);
        using var ownership = AcquireWriter();
        var current = await ReadAsync(selected.Id, token);
        ContractRules.Require(current.Revision == selected.Revision,
            "The voice asset changed. Reload and inspect it before removing the imported copy.");
        token.ThrowIfCancellationRequested();
        File.Delete(AssetPath(selected.Id));
    }

    private async Task<VoiceAsset> ReadAsync(Guid id, CancellationToken token)
    {
        var bytes = await ReadFileAsync(AssetPath(id), MaximumBundleBytes, token);
        byte[]? audio = null;
        try
        {
            using var input = new MemoryStream(bytes, writable: false);
            using var archive = new ZipArchive(input, ZipArchiveMode.Read);
            ContractRules.Require(archive.Entries.Count == 2 &&
                archive.Entries.Count(entry => entry.FullName == "manifest.json") == 1 &&
                archive.Entries.Count(entry => entry.FullName == "audio.wav") == 1,
                "The voice bundle has an unsupported layout. It was not changed.");
            var metadataBytes = await ReadEntryAsync(archive.GetEntry("manifest.json")!, MaximumMetadataBytes, token);
            AssetDocument document;
            try
            {
                using var json = JsonDocument.Parse(metadataBytes, new JsonDocumentOptions { MaxDepth = 8 });
                ContractJson.InspectJson(json.RootElement);
                document = json.RootElement.Deserialize<AssetDocument>(Json)
                    ?? throw new JsonException();
            }
            catch (JsonException)
            {
                throw new ContractException(ErrorCode.InvalidContract, "The voice manifest is malformed or unsupported. It was not changed.");
            }
            ContractRules.Require(document.Version == 1, "The voice bundle uses an unsupported version. Use a compatible build; it was not changed.",
                ErrorCode.UnsupportedVersion);
            ValidateFields(document.Name, document.Transcript, document.Engine, document.Purpose, document.Rights);
            ContractRules.Require(document.Id == id && id != Guid.Empty &&
                document.ImportedAtUtc.Offset == TimeSpan.Zero && document.ImportedAtUtc != default,
                "Voice asset identity or import metadata is invalid. It was not changed.");
            audio = await ReadEntryAsync(archive.GetEntry("audio.wav")!, MaximumAudioBytes, token);
            ContractRules.Require(Hash(audio) == document.AudioSha256, "Voice audio integrity check failed. It was not changed.");
            var wave = InspectAudio(audio, document.Purpose);
            return ToAsset(document, wave, Hash(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (audio is not null) CryptographicOperations.ZeroMemory(audio);
        }
    }

    private string[] Inventory()
    {
        var files = Directory.EnumerateFiles(directory, "*.voice", SearchOption.TopDirectoryOnly)
            .Take(MaximumAssets + 1).ToArray();
        ContractRules.Require(files.Length <= MaximumAssets, "Voice library exceeds its supported asset count.");
        ContractRules.Require(files.Sum(file => new FileInfo(LocalPath(file)).Length) <= MaximumLibraryBytes,
            "Voice library exceeds its supported storage limit.");
        ContractRules.Require(!Directory.EnumerateFileSystemEntries(directory, "*.pending").Any(),
            "An interrupted import left private staging data (.pending) in the voice library. Remove that staging file after checking no import is running, then reload; saved voices and originals are unchanged.");
        return files;
    }

    private FileStream AcquireWriter()
    {
        var path = LocalPath(Path.Combine(directory, ".writer.lock"));
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
    }

    private string AssetPath(Guid id)
    {
        ContractRules.Require(id != Guid.Empty, "Select a valid voice asset.");
        return LocalPath(Path.Combine(directory, $"{id:N}.voice"));
    }

    private static string LocalPath(string path)
    {
        ContractRules.Require(!string.IsNullOrWhiteSpace(path) && path.Length <= 1024, "Use a bounded absolute local file path.");
        try { return SettingsStore.RecoveryPath(path); }
        catch (RecoveryException)
        {
            throw new ContractException(ErrorCode.InvalidContract,
                "Use a local file or directory, not a network path, alternate stream or symbolic link.");
        }
    }

    private static async Task<byte[]> ReadFileAsync(string path, int maximum, CancellationToken token)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ReadBytesAsync(input, input.Length, maximum, token);
    }

    private static async Task<byte[]> ReadEntryAsync(ZipArchiveEntry entry, int maximum, CancellationToken token)
    {
        await using var input = entry.Open();
        return await ReadBytesAsync(input, entry.Length, maximum, token);
    }

    private static async Task<byte[]> ReadBytesAsync(Stream input, long length, int maximum, CancellationToken token)
    {
        ContractRules.Require(length is > 0 && length <= maximum, "Voice file is empty or exceeds the displayed import limit.");
        var bytes = new byte[checked((int)length)];
        try
        {
            await input.ReadExactlyAsync(bytes, token);
            var extra = new byte[1];
            ContractRules.Require(await input.ReadAsync(extra, token) == 0, "Voice file changed or exceeds its declared length.");
            return bytes;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    }

    private static async Task WriteEntryAsync(ZipArchive archive, string name, byte[] bytes, CancellationToken token)
    {
        await using var output = archive.CreateEntry(name, CompressionLevel.NoCompression).Open();
        await output.WriteAsync(bytes, token);
    }

    private static void ValidateFields(string name, string transcript, VoiceEngine engine,
        VoiceAssetPurpose purpose, VoiceRightsBasis rights)
    {
        ContractRules.Text(name, 80);
        ContractRules.Text(transcript, 16_384);
        ContractRules.Require(!string.IsNullOrWhiteSpace(name) && !name.Any(char.IsControl),
            "Enter a voice name without line breaks (up to 80 characters).");
        ContractRules.Require(!string.IsNullOrWhiteSpace(transcript), "Supply a reviewed transcript matching the imported audio.");
        ContractRules.Defined(engine);
        ContractRules.Defined(purpose);
        ContractRules.Defined(rights);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static PcmWaveInfo InspectAudio(byte[] audio, VoiceAssetPurpose purpose)
    {
        var wave = PcmWaveInfo.Inspect(audio, MaximumAudioBytes);
        var maximumSeconds = purpose == VoiceAssetPurpose.Reference ? 30 : 600;
        ContractRules.Require(wave.SampleCount >= wave.SampleRate &&
            wave.SampleCount <= (long)wave.SampleRate * maximumSeconds,
            $"Use audio between 1 and {maximumSeconds} seconds for the selected purpose.");
        return wave;
    }

    private static VoiceAsset ToAsset(AssetDocument doc, PcmWaveInfo wave, string revision) =>
        new(doc.Id, doc.Name, doc.Transcript, doc.Engine, doc.Purpose, doc.Rights,
            doc.ImportedAtUtc, wave, doc.AudioSha256, revision);

    private sealed record AssetDocument(int Version, Guid Id, string Name, string Transcript,
        VoiceEngine Engine, VoiceAssetPurpose Purpose, VoiceRightsBasis Rights,
        DateTimeOffset ImportedAtUtc, string AudioSha256);
}
