using System.Security.Cryptography;
using System.Text;
using Martlet.Avatars;
using Martlet.Core.Contracts;

namespace Martlet.Avatar.Hosting;

public sealed record AvatarAsset(string Name, byte[] Bytes, string ContentType);
public sealed record AvatarAssetSnapshot(string ModelFile, string Revision, IReadOnlyList<AvatarAsset> Assets);

public static class LocalAvatarFiles
{
    public static void ValidatePath(string path)
    {
        ContractRules.Require(!string.IsNullOrWhiteSpace(path) && path.Length <= 1024 &&
            Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\", StringComparison.Ordinal) &&
            !path.Any(char.IsControl), "Select a bounded absolute local path, not a network path.");
    }

    public static void CheckAncestors(string path)
    {
        ValidatePath(path);
        var current = new FileInfo(Path.GetFullPath(path));
        if (current.Exists)
            ContractRules.Require(!current.Attributes.HasFlag(FileAttributes.ReparsePoint), "Avatar paths cannot follow reparse points.");
        for (var directory = current.Directory; directory is not null; directory = directory.Parent)
            if (directory.Exists)
                ContractRules.Require(!directory.Attributes.HasFlag(FileAttributes.ReparsePoint), "Avatar paths cannot follow reparse points.");
    }

    public static async Task<byte[]> ReadBoundedAsync(string path, int maximum, CancellationToken token)
    {
        CheckAncestors(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        ContractRules.Require(stream.Length is > 0 && stream.Length <= maximum, "Avatar resource exceeds its byte limit.");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, token);
        ContractRules.Require(stream.ReadByte() == -1, "Avatar resource changed while being read.");
        return bytes;
    }

    public static async Task<AvatarAssetSnapshot> SnapshotAsync(AvatarProfile profile, CancellationToken token)
    {
        profile.Validate();
        var assets = new List<AvatarAsset>();
        var modelFile = Path.GetFileName(profile.ModelPath);
        if (profile.Renderer == AvatarRenderer.Vrm)
        {
            ContractRules.Require(Path.GetExtension(profile.ModelPath).Equals(".vrm", StringComparison.OrdinalIgnoreCase),
                "Select a local VRM1 .vrm model.");
            assets.Add(new("model.vrm", await ReadBoundedAsync(profile.ModelPath, 32 * 1024 * 1024, token), "application/octet-stream"));
            modelFile = "model.vrm";
        }
        else
        {
            ContractRules.Require(modelFile.EndsWith(".model3.json", StringComparison.Ordinal), "Select a model3.json model.");
            var root = Path.GetDirectoryName(profile.ModelPath)!;
            var pending = new Queue<string>();
            var directories = 0;
            pending.Enqueue(root);
            while (pending.TryDequeue(out var directory))
            {
                CheckAncestors(Path.Combine(directory, "_"));
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    token.ThrowIfCancellationRequested();
                    var attributes = File.GetAttributes(path);
                    ContractRules.Require(!attributes.HasFlag(FileAttributes.ReparsePoint), "Model bundle contains a reparse point.");
                    if (attributes.HasFlag(FileAttributes.Directory))
                    {
                        ContractRules.Require(++directories <= 128, "Model directory count exceeds its bound.");
                        pending.Enqueue(path);
                        continue;
                    }
                    var extension = Path.GetExtension(path).ToLowerInvariant();
                    ContractRules.Require(extension is ".json" or ".moc3" or ".png" or ".wav",
                        "Model folder must contain only supported inert model assets, never scripts.");
                    ContractRules.Require(assets.Count < 128, "Model bundle exceeds its file limit.");
                    var name = Path.GetRelativePath(root, path).Replace('\\', '/');
                    ContractRules.Require(name.Length <= 240, "Model path exceeds its bound.");
                    assets.Add(new(name, await ReadBoundedAsync(path, extension == ".json" ? 1024 * 1024 : 16 * 1024 * 1024, token),
                        extension == ".png" ? "image/png" : "application/octet-stream"));
                    ContractRules.Require(assets.Sum(a => (long)a.Bytes.Length) <= 64 * 1024 * 1024, "Model bundle exceeds its byte limit.");
                }
            }
            ContractRules.Require(profile.SdkDirectory is not null, "Live2D needs explicitly prepared local SDK/Core resources.");
            foreach (var name in new[] { "core.js", "sdk.js" })
                assets.Add(new(name, await ReadBoundedAsync(Path.Combine(profile.SdkDirectory!, name), 16 * 1024 * 1024, token),
                    "text/javascript"));
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(profile.Renderer + "\0" + modelFile + "\0"));
        foreach (var asset in assets.OrderBy(a => a.Name, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(asset.Name + "\0"));
            hash.AppendData(SHA256.HashData(asset.Bytes));
        }
        return new(modelFile, Convert.ToHexString(hash.GetHashAndReset()), assets);
    }
}
