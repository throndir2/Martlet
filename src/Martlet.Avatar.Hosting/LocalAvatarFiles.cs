using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Martlet.Avatars;
using Martlet.Core.Contracts;

namespace Martlet.Avatar.Hosting;

public sealed record AvatarAsset(string Name, byte[] Bytes, string ContentType);
public sealed record AvatarAssetSnapshot(string ModelFile, string Revision, IReadOnlyList<AvatarAsset> Assets);

/// <summary>Live2D runtime and default character shipped under the renderer's <c>live2d</c> folder.</summary>
public static class BundledLive2D
{
    public const string Prefix = "builtin:";
    public const string DefaultCharacter = "Hiyori";
    public static IReadOnlyList<string> Characters { get; } = [DefaultCharacter];

    public static bool IsBuiltIn(string? path) => path?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    /// <summary>Desktop resolves <c>AvatarRenderer\live2d</c>; the renderer process resolves its own <c>live2d</c>.</summary>
    public static string? Root =>
        new[] { Path.Combine(AppContext.BaseDirectory, "live2d"), Path.Combine(AppContext.BaseDirectory, "AvatarRenderer", "live2d") }
            .FirstOrDefault(candidate => File.Exists(Path.Combine(candidate, "sdk", "core.js")) &&
                File.Exists(Path.Combine(candidate, "sdk", "sdk.js")));

    public static bool Available => Root is not null;

    public static string SdkDirectory => Path.Combine(RequireRoot(), "sdk");

    public static string ModelPath(string builtIn)
    {
        ContractRules.Require(IsBuiltIn(builtIn) && Characters.Contains(builtIn[Prefix.Length..]), "Unknown bundled character.");
        var name = builtIn[Prefix.Length..];
        return Path.Combine(RequireRoot(), "characters", name, name + ".model3.json");
    }

    private static string RequireRoot() => Root ?? throw new ContractException(ErrorCode.InvalidContract,
        "This Martlet build does not include the Live2D runtime. Rebuild with network access or install an official release.");
}

public static class LocalAvatarFiles
{
    public const int MaximumModelFiles = 128;
    public const int MaximumModelJsonBytes = 1024 * 1024;
    public const int MaximumModelAssetBytes = 64 * 1024 * 1024;
    public const long MaximumModelBytes = 128L * 1024 * 1024;

    // Letters and digits of any script plus a few punctuation marks; never separators, escapes, URL syntax or traversal.
    // Mirrors localPath in Martlet.Avatar.Live2D/lib/assets.ts.
    private static readonly Regex SafeModelName = new(@"^[\p{L}\p{N}_(\[][\p{L}\p{N}\p{M}_. ()\[\]+&',!~@=-]*$",
        RegexOptions.CultureInvariant);

    public static bool IsSafeModelName(string segment) => segment.Length is > 0 and <= 240 &&
        SafeModelName.IsMatch(segment) && !segment.EndsWith('.') && !segment.EndsWith(' ');

    private static string Shorten(string value) => value.Length <= 80 ? value : value[..77] + "...";

    /// <summary>Every file a model3.json declares (MOC, textures, physics, pose, user data, display info, expressions,
    /// motions and their sounds), as written. Shapes the renderer would reject are left for it to report.</summary>
    public static IReadOnlyList<string> ModelReferences(byte[] model)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(model, new JsonDocumentOptions { MaxDepth = 16 }); }
        catch (JsonException) { throw new ContractException(ErrorCode.InvalidContract, "The model3.json file isn't valid JSON."); }
        using (document)
        {
            var root = document.RootElement;
            var files = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("FileReferences", out var found) ? found : default;
            ContractRules.Require(files.ValueKind == JsonValueKind.Object,
                "The model3.json file has no FileReferences, so it isn't a Live2D Cubism 3 or later model.");
            var references = new List<string>();
            void Add(JsonElement value)
            {
                if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text) references.Add(text);
            }
            foreach (var name in new[] { "Moc", "Physics", "Pose", "UserData", "DisplayInfo" })
                if (files.TryGetProperty(name, out var value)) Add(value);
            if (files.TryGetProperty("Textures", out var textures) && textures.ValueKind == JsonValueKind.Array)
                foreach (var texture in textures.EnumerateArray()) Add(texture);
            if (files.TryGetProperty("Expressions", out var expressions) && expressions.ValueKind == JsonValueKind.Array)
                foreach (var expression in expressions.EnumerateArray())
                    if (expression.ValueKind == JsonValueKind.Object && expression.TryGetProperty("File", out var file)) Add(file);
            if (files.TryGetProperty("Motions", out var motions) && motions.ValueKind == JsonValueKind.Object)
                foreach (var group in motions.EnumerateObject())
                    if (group.Value.ValueKind == JsonValueKind.Array)
                        foreach (var motion in group.Value.EnumerateArray())
                        {
                            if (motion.ValueKind != JsonValueKind.Object) continue;
                            if (motion.TryGetProperty("File", out var file)) Add(file);
                            if (motion.TryGetProperty("Sound", out var sound)) Add(sound);
                        }
            ContractRules.Require(references.Count <= MaximumModelFiles * 4, "Model bundle exceeds its file limit.");
            return references;
        }
    }
    public static void ValidatePath(string path)
    {
        ContractRules.Require(!string.IsNullOrWhiteSpace(path) && path.Length <= 1024 &&
            Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\", StringComparison.Ordinal) &&
            !path.Any(char.IsControl), "Select a bounded absolute local path, not a network path.");
        ContractRules.Require(path[Path.GetPathRoot(path)!.Length..].Split(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar).All(segment => segment.Length == 0 ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !segment.EndsWith('.') && !segment.EndsWith(' ') &&
                segment is not ("." or "..")),
            "Avatar paths cannot contain alternate streams, traversal or ambiguous trailing characters.");
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

    /// <summary>A model's own files, without the Live2D runtime: a VRM file under its file name, or a Live2D model's
    /// <c>.model3.json</c> and every file it declares (MOC, textures, physics, pose, user data, display info, expressions,
    /// motions and their sounds) by its path relative to the model3.json with forward slashes, as written there. Models made
    /// for VTube Studio keep their emotes and motions beside the model instead, so its <c>.vtube.json</c> (the one naming this
    /// model3.json) and the expression and motion files it names, and any other <c>.exp3.json</c> or <c>.motion3.json</c> at the
    /// top of the folder, come too (<see cref="Live2DExtras"/> says what they are); one of those that breaks the rules is
    /// skipped. Readmes, icons and backups are never opened. Enforces the renderer's limits; scripts, reparse points, paths
    /// outside the folder and anything but inert model assets are refused.</summary>
    public static async Task<IReadOnlyList<AvatarAsset>> ReadModelAsync(AvatarRenderer renderer, string modelPath, CancellationToken token)
    {
        var assets = new List<AvatarAsset>();
        var modelFile = Path.GetFileName(modelPath);
        if (renderer == AvatarRenderer.Vrm)
        {
            ContractRules.Require(Path.GetExtension(modelPath).Equals(".vrm", StringComparison.OrdinalIgnoreCase),
                "Select a local VRM1 .vrm model.");
            assets.Add(new(modelFile, await ReadBoundedAsync(modelPath, 32 * 1024 * 1024, token), "application/octet-stream"));
            return assets;
        }
        ContractRules.Require(modelFile.EndsWith(".model3.json", StringComparison.Ordinal), "Select a model3.json model.");
        ContractRules.Require(IsSafeModelName(modelFile),
            $"Rename {modelFile}: model file names may use letters, digits, spaces and . _ - ( ) [ ] + & ' , ! ~ @ = only.");
        var root = Path.GetDirectoryName(modelPath)!;
        var model = await ReadBoundedAsync(modelPath, MaximumModelJsonBytes, token);
        assets.Add(new(modelFile, model, "application/octet-stream"));
        long total = model.Length;
        var seen = new HashSet<string>(StringComparer.Ordinal) { modelFile };
        foreach (var reference in ModelReferences(model))
        {
            token.ThrowIfCancellationRequested();
            if (!seen.Add(reference)) continue;
            ContractRules.Require(assets.Count < MaximumModelFiles, "Model bundle exceeds its file limit.");
            ContractRules.Require(reference.Length <= 240 && reference.Split('/').All(IsSafeModelName),
                $"The model refers to an unsupported file name ({Shorten(reference)}). Use plain relative names: letters, " +
                "digits, spaces and . _ - ( ) [ ] + & ' , ! ~ @ = in folders below the model3.json.");
            var extension = Path.GetExtension(reference).ToLowerInvariant();
            ContractRules.Require(extension is ".json" or ".moc3" or ".png" or ".wav",
                $"The model refers to {reference}, which isn't an inert model asset (JSON, MOC3, PNG or WAV). Scripts are never loaded.");
            var path = Path.GetFullPath(Path.Combine(root, reference.Replace('/', Path.DirectorySeparatorChar)));
            ContractRules.Require(path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase), "Model assets must stay inside the model's folder.");
            var file = new FileInfo(path);
            ContractRules.Require(file.Exists, $"The model refers to {reference}, which isn't in its folder.");
            var limit = extension == ".json" ? MaximumModelJsonBytes : MaximumModelAssetBytes;
            ContractRules.Require(file.Length <= limit,
                $"{reference} is larger than Martlet's {limit / (1024 * 1024)} MB limit for one model file.");
            var bytes = await ReadBoundedAsync(path, limit, token);
            total += bytes.Length;
            ContractRules.Require(total <= MaximumModelBytes,
                $"The model's files add up to more than Martlet's {MaximumModelBytes / (1024 * 1024)} MB limit.");
            assets.Add(new(reference, bytes, extension == ".png" ? "image/png" : "application/octet-stream"));
        }
        foreach (var extra in ExtraReferences(root, modelFile))
        {
            token.ThrowIfCancellationRequested();
            if (assets.Count >= MaximumModelFiles || !seen.Add(extra) || seen.Any(s => s != extra && string.Equals(s, extra, StringComparison.OrdinalIgnoreCase)))
                continue;
            var path = Path.GetFullPath(Path.Combine(root, extra.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue;
            var file = new FileInfo(path);
            if (!file.Exists || file.Length is 0 or > MaximumModelJsonBytes || total + file.Length > MaximumModelBytes) continue;
            byte[] bytes;
            try { bytes = await ReadBoundedAsync(path, MaximumModelJsonBytes, token); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ContractException) { continue; }
            total += bytes.Length;
            assets.Add(new(extra, bytes, "application/octet-stream"));
        }
        return assets;
    }

    private const int MaximumExtraFiles = 64;

    /// <summary>The VTube Studio settings naming <paramref name="modelFile"/>, the expression and motion files they name (its
    /// idle animation and hotkeys) and the loose expression and motion files at the top of <paramref name="root"/>, as
    /// relative paths with forward slashes; at most <see cref="MaximumExtraFiles"/>, only plain safe names.</summary>
    private static IEnumerable<string> ExtraReferences(string root, string modelFile)
    {
        var found = new List<string>();
        void Add(string? relative)
        {
            if (relative is null) return;
            relative = relative.Replace('\\', '/').TrimStart('/');
            if (relative.Length is 0 or > 240 || !relative.Split('/').All(IsSafeModelName) || found.Contains(relative, StringComparer.OrdinalIgnoreCase) ||
                !(relative.EndsWith(".exp3.json", StringComparison.OrdinalIgnoreCase) || relative.EndsWith(".motion3.json", StringComparison.OrdinalIgnoreCase) ||
                  relative.EndsWith(".vtube.json", StringComparison.OrdinalIgnoreCase)))
                return;
            if (found.Count < MaximumExtraFiles) found.Add(relative);
        }
        IEnumerable<string> Top(string pattern)
        {
            try { return Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly).Select(Path.GetFileName).OfType<string>()
                .Order(StringComparer.Ordinal).Take(MaximumExtraFiles).ToArray(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
        }
        foreach (var settings in Top("*.vtube.json").Take(4))
        {
            var path = Path.Combine(root, settings);
            try
            {
                if (new FileInfo(path).Length > MaximumModelJsonBytes) continue;
                if (VTubeStudio.Read(File.ReadAllBytes(path)) is not { } vts || !string.Equals(vts.Model, modelFile, StringComparison.Ordinal)) continue;
                Add(settings);
                Add(vts.Idle);
                foreach (var hotkey in vts.Hotkeys) Add(hotkey.File);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        foreach (var name in Top("*.exp3.json").Concat(Top("*.motion3.json"))) Add(name);
        return found;
    }

    /// <summary>What <see cref="ReadModelAsync"/> adds to a Live2D model beyond its model3.json, for the renderer: the
    /// expressions and motions it doesn't declare, each named by its VTube Studio hotkey or else its file name (made unique
    /// beside the declared ones), and the motion VTube Studio idles with as an <c>Idle</c> group when the model has none.</summary>
    public static Live2DExtras Extras(IReadOnlyList<AvatarAsset> assets, string modelFile)
    {
        var model = assets.FirstOrDefault(a => a.Name == modelFile);
        var expressions = new List<Live2DExtraExpression>();
        var motions = new List<Live2DExtraMotion>();
        if (model is null) return new(expressions, motions);
        var declared = Live2DDeclarations(model.Bytes);
        var vts = assets.Where(a => a.Name.EndsWith(".vtube.json", StringComparison.OrdinalIgnoreCase))
            .Select(a => VTubeStudio.Read(a.Bytes)).FirstOrDefault(v => v is not null && v.Model == modelFile);
        string Named(string file, IEnumerable<string> taken, string suffix)
        {
            var hotkey = vts?.Hotkeys.FirstOrDefault(h => string.Equals(h.File, file, StringComparison.OrdinalIgnoreCase))?.Name;
            var stem = Path.GetFileName(file);
            stem = stem[..^suffix.Length];
            var name = hotkey is { Length: > 0 and <= 64 } && !hotkey.Any(char.IsControl) ? hotkey.Trim() : stem;
            if (name.Length == 0) name = stem;
            var unique = name;
            for (var n = 2; taken.Contains(unique, StringComparer.Ordinal); n++) unique = $"{name} {n}";
            return unique;
        }
        var usedExpressions = new List<string>(declared.Expressions.Keys);
        var usedGroups = new List<string>(declared.Motions.Keys);
        var idleDeclared = usedGroups.Any(g => g.Equals("idle", StringComparison.OrdinalIgnoreCase));
        foreach (var asset in assets.OrderBy(a => a.Name, StringComparer.Ordinal))
        {
            if (asset.Name.EndsWith(".exp3.json", StringComparison.OrdinalIgnoreCase) && !declared.Expressions.Values.Contains(asset.Name, StringComparer.Ordinal))
            {
                var name = Named(asset.Name, usedExpressions, ".exp3.json");
                usedExpressions.Add(name);
                expressions.Add(new(name, asset.Name));
            }
            else if (asset.Name.EndsWith(".motion3.json", StringComparison.OrdinalIgnoreCase) &&
                !declared.Motions.Values.Any(files => files.Contains(asset.Name, StringComparer.Ordinal)))
            {
                if (vts?.Idle is { } idle && string.Equals(idle.Replace('\\', '/'), asset.Name, StringComparison.OrdinalIgnoreCase))
                {
                    if (!idleDeclared) motions.Add(new(IdleGroup, asset.Name));
                    idleDeclared = true;
                    continue;
                }
                var group = Named(asset.Name, usedGroups.Append(IdleGroup), ".motion3.json");
                usedGroups.Add(group);
                motions.Add(new(group, asset.Name));
            }
        }
        return new(expressions, motions);
    }

    /// <summary>The idle motion group's name: the renderer idles with the group of that name, whatever its case.</summary>
    public const string IdleGroup = "Idle";

    /// <summary>A model3.json's declared expressions (name to file) and motion groups (name to files), as written and
    /// relative to the model3.json; malformed parts are left out.</summary>
    public static (IReadOnlyDictionary<string, string> Expressions, IReadOnlyDictionary<string, IReadOnlyList<string>> Motions) Live2DDeclarations(byte[] model)
    {
        var expressions = new Dictionary<string, string>(StringComparer.Ordinal);
        var motions = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(model, new JsonDocumentOptions { MaxDepth = 16 });
            if (!document.RootElement.TryGetProperty("FileReferences", out var files) || files.ValueKind != JsonValueKind.Object)
                return (expressions, motions);
            if (files.TryGetProperty("Expressions", out var declared) && declared.ValueKind == JsonValueKind.Array)
                foreach (var expression in declared.EnumerateArray())
                    if (expression.ValueKind == JsonValueKind.Object && expression.TryGetProperty("Name", out var name) &&
                        name.ValueKind == JsonValueKind.String && expression.TryGetProperty("File", out var file) && file.ValueKind == JsonValueKind.String)
                        expressions.TryAdd(name.GetString()!, file.GetString()!);
            if (files.TryGetProperty("Motions", out var groups) && groups.ValueKind == JsonValueKind.Object)
                foreach (var group in groups.EnumerateObject())
                    if (group.Value.ValueKind == JsonValueKind.Array)
                        motions[group.Name] = group.Value.EnumerateArray()
                            .Where(m => m.ValueKind == JsonValueKind.Object && m.TryGetProperty("File", out var f) && f.ValueKind == JsonValueKind.String)
                            .Select(m => m.GetProperty("File").GetString()!).ToArray();
        }
        catch (JsonException) { }
        return (expressions, motions);
    }

    public static async Task<AvatarAssetSnapshot> SnapshotAsync(AvatarProfile profile, CancellationToken token)
    {
        profile.Validate();
        var assets = new List<AvatarAsset>();
        var modelPath = BundledLive2D.IsBuiltIn(profile.ModelPath) ? BundledLive2D.ModelPath(profile.ModelPath) : profile.ModelPath;
        var modelFile = Path.GetFileName(modelPath);
        if (profile.Renderer == AvatarRenderer.Vrm)
        {
            var model = await ReadModelAsync(AvatarRenderer.Vrm, modelPath, token);
            assets.Add(model[0] with { Name = "model.vrm" });
            modelFile = "model.vrm";
        }
        else
        {
            assets.AddRange(await ReadModelAsync(AvatarRenderer.Live2D, modelPath, token));
            var sdk = profile.SdkDirectory ?? BundledLive2D.SdkDirectory;
            foreach (var name in new[] { "core.js", "sdk.js" })
                assets.Add(new(name, await ReadBoundedAsync(Path.Combine(sdk, name), 16 * 1024 * 1024, token),
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
