using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Martlet.VoiceActivity;

internal sealed class SileroVadInferenceFactory : IVoiceActivityInferenceFactory
{
    public VoiceActivityEvidence Evidence => VoiceActivityEvidence.NativeQualification;
    public IVoiceActivityInferenceSession Create() => new OnnxVadInferenceSession();
}

internal sealed record VadNativeArtifact(string Name, int Bytes, string Sha256);
internal sealed record VadModelManifest(
    string ModelId, string SourceRevision, string FileName, int ModelBytes, string ModelSha256,
    string RuntimeVersion, string RuntimeSourceRevision, string SourceUrl, string DigestProvenance,
    string License, IReadOnlyList<VadNativeArtifact> NativeFiles);

internal static class SileroVadModel
{
    private const int MaximumManifestBytes = 16_384;
    private const string ResourceName = "Martlet.VoiceActivity.model-manifest.json";

    internal static VadModelManifest ReadManifest(VoiceActivityInferenceAccess access)
    {
        byte[] buffer = new byte[MaximumManifestBytes + 1];
        Stream? source = null;
        try
        {
            Check(access);
            source = typeof(SileroVadModel).Assembly.GetManifestResourceStream(ResourceName);
            Check(access);
            if (source is null) throw Failure(VoiceActivityFailureCode.ModelUnavailable);
            int used = 0;
            while (used < buffer.Length)
            {
                Check(access);
                int read = source.Read(buffer, used, buffer.Length - used);
                Check(access);
                if (read == 0) break;
                used += read;
            }
            VadCheck.Require(used <= MaximumManifestBytes, VoiceActivityFailureCode.ModelUnavailable);
            var manifest = ParseManifest(buffer.AsMemory(0, used));
            Close(source, access);
            source = null;
            Check(access);
            return manifest;
        }
        catch (Exception ex)
        {
            Check(access);
            if (ex is VoiceActivityException) throw;
            throw Failure(VoiceActivityFailureCode.ModelUnavailable);
        }
        finally
        {
            Array.Clear(buffer);
            if (source is not null) Close(source, access);
        }
    }

    // This parser never grants a caller-selected digest to the model loader.
    internal static VadModelManifest ParseManifest(ReadOnlyMemory<byte> json)
    {
        try
        {
            VadCheck.Require(json.Length is > 0 and <= MaximumManifestBytes, VoiceActivityFailureCode.ModelUnavailable);
            using var document = JsonDocument.Parse(json, new() { MaxDepth = 8 });
            var root = document.RootElement;
            Fields(root, ["schemaVersion", "modelId", "sourceRevision", "fileName", "modelBytes",
                "modelSha256", "sourceUrl", "digestProvenance", "license", "runtimeVersion",
                "runtimeSourceRevision", "nativeFiles"]);
            VadCheck.Require(root.GetProperty("schemaVersion").GetInt32() == 1, VoiceActivityFailureCode.ModelUnavailable);
            var id = Text(root, "modelId", 64);
            var fileName = Text(root, "fileName", 64);
            var version = Text(root, "runtimeVersion", 16);
            VadCheck.Require(id == NativeEligibility.ModelId && fileName == "silero_vad_16k_op15.onnx" &&
                version == "1.30.0", VoiceActivityFailureCode.ModelUnavailable);
            var modelBytes = PositiveSize(root, "modelBytes", 2 * 1024 * 1024);
            var url = Text(root, "sourceUrl", 2048);
            VadCheck.Require(Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                uri.Scheme == Uri.UriSchemeHttps, VoiceActivityFailureCode.ModelUnavailable);
            var artifacts = root.GetProperty("nativeFiles");
            VadCheck.Require(artifacts.ValueKind == JsonValueKind.Array && artifacts.GetArrayLength() == 2,
                VoiceActivityFailureCode.ModelUnavailable);
            var native = new List<VadNativeArtifact>(2);
            foreach (var artifact in artifacts.EnumerateArray())
            {
                Fields(artifact, ["name", "bytes", "sha256"]);
                var name = Text(artifact, "name", 64);
                VadCheck.Require((name is "onnxruntime.dll" or "onnxruntime_providers_shared.dll") &&
                    !native.Any(item => item.Name == name), VoiceActivityFailureCode.ModelUnavailable);
                native.Add(new(name, PositiveSize(artifact, "bytes", 32 * 1024 * 1024), Digest(artifact, "sha256", 64)));
            }
            return new(id, Digest(root, "sourceRevision", 40), fileName, modelBytes,
                Digest(root, "modelSha256", 64), version, Digest(root, "runtimeSourceRevision", 40),
                url, Text(root, "digestProvenance", 2048), Text(root, "license", 2048), native.AsReadOnly());
        }
        catch (VoiceActivityException) { throw; }
        catch (Exception) { throw Failure(VoiceActivityFailureCode.ModelUnavailable); }
    }

    internal static byte[] Load(string modelPath, VoiceActivityInferenceAccess access)
    {
        byte[]? snapshot = null;
        LocalFileLease? file = null;
        try
        {
            Check(access);
            file = OpenLocalFile(modelPath, access, VoiceActivityFailureCode.ModelUnavailable);
            snapshot = ReadPinnedSnapshot(file.Stream, access);
            file.Close(access);
            file = null;
            Check(access);
            return snapshot;
        }
        catch (Exception ex)
        {
            if (snapshot is not null) CryptographicOperations.ZeroMemory(snapshot);
            Check(access);
            if (ex is VoiceActivityException) throw;
            throw Failure(VoiceActivityFailureCode.ModelUnavailable);
        }
        finally
        {
            file?.Close(access);
        }
    }

    // BCL test seam: the embedded manifest remains the only source of accepted length and digest.
    internal static byte[] ReadPinnedSnapshot(Stream source, VoiceActivityInferenceAccess access)
    {
        var manifest = ReadManifest(access);
        byte[]? snapshot = null;
        bool returned = false;
        try
        {
            Check(access);
            VadCheck.Require(source.CanRead && source.CanSeek, VoiceActivityFailureCode.ModelUnavailable);
            long length = source.Length;
            Check(access);
            VadCheck.Require(length == manifest.ModelBytes, VoiceActivityFailureCode.InvalidModelSize);
            Check(access);
            source.Position = 0;
            Check(access);
            snapshot = new byte[manifest.ModelBytes];
            int offset = 0;
            while (offset < snapshot.Length)
            {
                Check(access);
                int read = source.Read(snapshot, offset, Math.Min(65_536, snapshot.Length - offset));
                Check(access);
                VadCheck.Require(read > 0, VoiceActivityFailureCode.InvalidModelSize);
                offset += read;
            }
            Check(access);
            int extra = source.ReadByte();
            Check(access);
            long finalLength = source.Length;
            Check(access);
            VadCheck.Require(extra == -1 && finalLength == manifest.ModelBytes, VoiceActivityFailureCode.InvalidModelSize);
            Check(access);
            Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(snapshot, digest);
            Check(access);
            VadCheck.Require(Convert.ToHexStringLower(digest) == manifest.ModelSha256,
                VoiceActivityFailureCode.InvalidModelHash);
            Check(access);
            returned = true;
            return snapshot;
        }
        catch (Exception ex)
        {
            Check(access);
            if (ex is VoiceActivityException) throw;
            throw Failure(VoiceActivityFailureCode.ModelUnavailable);
        }
        finally
        {
            if (!returned && snapshot is not null) CryptographicOperations.ZeroMemory(snapshot);
        }
    }

    internal static void VerifyNativeFile(LocalFileLease file, VadNativeArtifact artifact,
        VoiceActivityInferenceAccess access)
    {
        byte[] buffer = new byte[65_536];
        try
        {
            Check(access);
            long length = file.Stream.Length;
            Check(access);
            VadCheck.Require(length == artifact.Bytes, VoiceActivityFailureCode.NativeRuntimeUnavailable);
            Check(access);
            file.Stream.Position = 0;
            Check(access);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            int remaining = artifact.Bytes;
            while (remaining > 0)
            {
                Check(access);
                int read = file.Stream.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                Check(access);
                VadCheck.Require(read > 0, VoiceActivityFailureCode.NativeRuntimeUnavailable);
                hash.AppendData(buffer, 0, read);
                remaining -= read;
            }
            Check(access);
            int extra = file.Stream.ReadByte();
            Check(access);
            VadCheck.Require(extra == -1 && Convert.ToHexStringLower(hash.GetHashAndReset()) == artifact.Sha256,
                VoiceActivityFailureCode.NativeRuntimeUnavailable);
            Check(access);
        }
        catch (Exception ex)
        {
            Check(access);
            if (ex is VoiceActivityException) throw;
            throw Failure(VoiceActivityFailureCode.NativeRuntimeUnavailable);
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    internal static void ValidateLocalPath(string path)
    {
        if (!(path is { Length: >= 4 and <= 1024 } &&
            char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\' &&
            !path.Contains('/') && !path.AsSpan(2).Contains(':') && !path.Any(char.IsControl)))
            throw Failure(VoiceActivityFailureCode.ModelUnavailable);
        var parts = path[3..].Split('\\');
        VadCheck.Require(parts.Length is >= 1 and <= 32, VoiceActivityFailureCode.ModelUnavailable);
        foreach (var part in parts)
        {
            VadCheck.Require(part.Length > 0 && part is not "." and not ".." &&
                !part.EndsWith('.') && !part.EndsWith(' ') &&
                part.IndexOfAny(['<', '>', '"', '|', '?', '*', ':']) < 0, VoiceActivityFailureCode.ModelUnavailable);
            var stem = part.Split('.')[0].TrimEnd(' ', '.').ToUpperInvariant();
            VadCheck.Require(stem is not ("CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" or "CLOCK$") &&
                !(stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) ||
                    stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                    (stem[3] is >= '0' and <= '9' or '¹' or '²' or '³')),
                VoiceActivityFailureCode.ModelUnavailable);
        }
    }

    internal static LocalFileLease OpenLocalFile(
        string path, VoiceActivityInferenceAccess access, VoiceActivityFailureCode failure)
    {
        var handles = new List<SafeFileHandle>();
        FileStream? stream = null;
        try
        {
            Check(access);
            ValidateLocalPath(path);
            VadCheck.Require(OperatingSystem.IsWindows(), VoiceActivityFailureCode.UnsupportedPlatform);
            string root = path[..3];
            Check(access);
            uint driveType = GetDriveTypeW(root);
            Check(access);
            VadCheck.Require(driveType == 3, failure);

            // Pin only the explicitly named ancestors, never enumerate/search directories.
            // Denying write/delete sharing prevents a checked ancestor becoming a reparse route.
            string current = root;
            var parts = path[3..].Split('\\');
            for (int index = -1; index < parts.Length; ++index)
            {
                bool directory = index < parts.Length - 1;
                if (index >= 0) current = Path.Combine(current, parts[index]);
                Check(access);
                var handle = CreateFileW(current, directory ? 0x80u : 0x80000000u,
                    1, IntPtr.Zero, 3, 0x00200000u | (directory ? 0x02000000u : 0u), IntPtr.Zero);
                handles.Add(handle);
                Check(access);
                VadCheck.Require(!handle.IsInvalid, failure);
                Check(access);
                bool obtained = GetFileInformationByHandleEx(handle, 9, out FileAttributeTagInfo attributes, 8);
                Check(access);
                VadCheck.Require(obtained && (attributes.Attributes & 0x400) == 0 &&
                    ((attributes.Attributes & 0x10) != 0) == directory, failure);
            }
            var finalPath = new StringBuilder(2048);
            Check(access);
            uint count = GetFinalPathNameByHandleW(handles[^1], finalPath, (uint)finalPath.Capacity, 0);
            Check(access);
            VadCheck.Require(count > 0 && count < finalPath.Capacity &&
                string.Equals(finalPath.ToString(), @"\\?\" + path, StringComparison.OrdinalIgnoreCase), failure);
            Check(access);
            stream = new FileStream(handles[^1], FileAccess.Read, 4096, isAsync: false);
            Check(access);
            return new(path, stream, handles);
        }
        catch (Exception ex)
        {
            if (stream is not null) Close(stream, access);
            for (int index = handles.Count - 1; index >= 0; --index) Close(handles[index], access);
            Check(access);
            if (ex is VoiceActivityException typed && typed.Failure.Code is
                VoiceActivityFailureCode.Canceled or VoiceActivityFailureCode.DeadlineExceeded or
                VoiceActivityFailureCode.UnsupportedPlatform) throw;
            throw Failure(failure);
        }
    }

    internal static void Check(VoiceActivityInferenceAccess access)
    {
        try { access.Check(); }
        catch (VoiceActivityException) { throw; }
        catch (Exception) { throw Failure(VoiceActivityFailureCode.InvalidState); }
    }

    internal static void CheckForRelease(VoiceActivityInferenceAccess access)
    {
        try { Check(access); }
        catch (VoiceActivityException ex) when (ex.Failure.Code is
            VoiceActivityFailureCode.Canceled or VoiceActivityFailureCode.DeadlineExceeded)
        {
            // Revocation stops new work, not synchronous retirement of already owned resources.
        }
    }

    internal static void Close(IDisposable resource, VoiceActivityInferenceAccess access)
    {
        CheckForRelease(access);
        try { resource.Dispose(); }
        catch (Exception) { CheckForRelease(access); throw Failure(VoiceActivityFailureCode.CleanupFailed); }
        CheckForRelease(access);
    }

    internal sealed class LocalFileLease(string path, FileStream stream, List<SafeFileHandle> handles)
    {
        internal string Path { get; } = path;
        internal FileStream Stream { get; } = stream;
        internal void Close(VoiceActivityInferenceAccess access)
        {
            SileroVadModel.Close(Stream, access);
            for (int index = handles.Count - 1; index >= 0; --index)
                SileroVadModel.Close(handles[index], access);
        }
    }

    private static void Fields(JsonElement value, string[] expected)
    {
        VadCheck.Require(value.ValueKind == JsonValueKind.Object, VoiceActivityFailureCode.ModelUnavailable);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            VadCheck.Require(expected.Contains(property.Name, StringComparer.Ordinal) && seen.Add(property.Name),
                VoiceActivityFailureCode.ModelUnavailable);
        VadCheck.Require(seen.Count == expected.Length, VoiceActivityFailureCode.ModelUnavailable);
    }

    private static string Text(JsonElement value, string name, int maximum)
    {
        var text = value.GetProperty(name).GetString();
        if (text is null || text.Length == 0 || text.Length > maximum || text.Any(char.IsControl))
            throw Failure(VoiceActivityFailureCode.ModelUnavailable);
        return text;
    }

    private static string Digest(JsonElement value, string name, int length)
    {
        var text = Text(value, name, length);
        VadCheck.Require(text.Length == length && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'),
            VoiceActivityFailureCode.ModelUnavailable);
        return text;
    }

    private static int PositiveSize(JsonElement value, string name, int maximum)
    {
        int size = value.GetProperty(name).GetInt32();
        VadCheck.Require(size > 0 && size <= maximum, VoiceActivityFailureCode.ModelUnavailable);
        return size;
    }

    private static VoiceActivityException Failure(VoiceActivityFailureCode code) => new(code);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo { internal uint Attributes; internal uint ReparseTag; }

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share,
        IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file, int kind, out FileAttributeTagInfo information, uint size);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint size, uint flags);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetDriveTypeW(string root);
}
