using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Martlet.Avatars;
using Martlet.Core.Characters;

namespace Martlet.Avatar.Hosting;

/// <summary>What an emote or motion is: a Live2D expression or a VRM expression (held while it shows), a Live2D motion group
/// (played once) or one of Martlet's own head gestures (every model: nod and shake).</summary>
public enum CharacterActionKind { Expression, Motion, Gesture }

/// <summary>One emote or motion a character model has. <see cref="Id"/> is stable for the model (<c>expression:F01</c>,
/// <c>motion:TapBody</c>, <c>gesture:nod</c>); <see cref="Name"/> is what the renderer plays (the expression's name or
/// motion group); <see cref="Detail"/> says what it changes, for the Thinking model and the owner (parameter IDs, their
/// display names and values, shape names, the motion's length), never a path.</summary>
public sealed record CharacterActionSource(string Id, CharacterActionKind Kind, string Name, string Detail);

/// <summary>Every emote and motion of one character model, read from its files without showing it. <see cref="ModelId"/>
/// is the model's ID as the shared character list computes it (<see cref="CharacterModelLibrary.ModelId"/>), so a model has
/// the same emotes and settings wherever it is shown from.</summary>
public sealed record CharacterActionInventory(string ModelId, AvatarRenderer Renderer, IReadOnlyList<CharacterActionSource> Sources)
{
    public const int MaximumSources = 160;
    private const int MaximumDetail = 400;

    public static readonly IReadOnlyList<CharacterActionSource> Gestures =
    [
        new("gesture:nod", CharacterActionKind.Gesture, "nod", "Martlet's own gesture on every model: nods the head twice, for yes or agreement."),
        new("gesture:shake", CharacterActionKind.Gesture, "shake", "Martlet's own gesture on every model: shakes the head, for no or disbelief.")
    ];

    public CharacterActionSource? Find(string id) => Sources.FirstOrDefault(s => s.Id == id);

    /// <summary>The model at <paramref name="modelPath"/> (a profile's model path; <c>builtin:</c> for the bundled one).</summary>
    public static async Task<CharacterActionInventory> ReadAsync(AvatarRenderer renderer, string modelPath, CancellationToken token)
    {
        var path = BundledLive2D.IsBuiltIn(modelPath) ? BundledLive2D.ModelPath(modelPath) : modelPath;
        var assets = await LocalAvatarFiles.ReadModelAsync(renderer, path, token);
        return From(renderer, renderer == AvatarRenderer.Vrm ? assets[0].Name : Path.GetFileName(path), assets);
    }

    public static CharacterActionInventory From(AvatarRenderer renderer, string entry, IReadOnlyList<AvatarAsset> assets)
    {
        var files = assets.Select(a => CharacterModelLibrary.File(a.Name, a.Bytes)).ToArray();
        var id = CharacterModelLibrary.ModelId(SharedCharacterModels.RendererName(renderer), entry, files);
        var sources = renderer == AvatarRenderer.Vrm ? VrmSources(assets[0].Bytes) : Live2DSources(entry, assets);
        return new(id, renderer, [.. sources.Take(MaximumSources - Gestures.Count), .. Gestures]);
    }

    private static string Clip(string text) => text.Length <= MaximumDetail ? text : text[..(MaximumDetail - 3)] + "...";

    private static string Number(double value) => value.ToString(value == Math.Round(value) ? "0" : "0.##", CultureInfo.InvariantCulture);

    private static IEnumerable<CharacterActionSource> Live2DSources(string entry, IReadOnlyList<AvatarAsset> assets)
    {
        var byName = assets.ToDictionary(a => a.Name, StringComparer.Ordinal);
        if (!byName.TryGetValue(entry, out var model)) yield break;
        var declared = LocalAvatarFiles.Live2DDeclarations(model.Bytes);
        var extras = LocalAvatarFiles.Extras(assets, entry);
        var names = DisplayNames(model.Bytes, byName);
        string Label(string id) => names.TryGetValue(id, out var display) && display != id ? $"{id} ({display})" : id;
        foreach (var (name, file) in declared.Expressions.Select(e => (e.Key, e.Value)).Concat(extras.Expressions.Select(e => (e.Name, e.File))))
        {
            if (!byName.TryGetValue(file, out var asset)) continue;
            yield return new("expression:" + name, CharacterActionKind.Expression, name, Clip(ExpressionDetail(file, asset.Bytes, Label)));
        }
        var groups = declared.Motions.Select(g => (Group: g.Key, Files: g.Value))
            .Concat(extras.Motions.GroupBy(m => m.Group).Select(g => (Group: g.Key, Files: (IReadOnlyList<string>)g.Select(m => m.File).ToArray())));
        foreach (var (group, motionFiles) in groups)
        {
            if (group.Equals(LocalAvatarFiles.IdleGroup, StringComparison.OrdinalIgnoreCase)) continue;
            var present = motionFiles.Where(byName.ContainsKey).ToArray();
            if (present.Length == 0) continue;
            yield return new("motion:" + group, CharacterActionKind.Motion, group, Clip(MotionDetail(present, byName, Label)));
        }
    }

    private static Dictionary<string, string> DisplayNames(byte[] model, IReadOnlyDictionary<string, AvatarAsset> assets)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(model, new JsonDocumentOptions { MaxDepth = 16 });
            if (!document.RootElement.TryGetProperty("FileReferences", out var files) ||
                !files.TryGetProperty("DisplayInfo", out var info) || info.ValueKind != JsonValueKind.String ||
                !assets.TryGetValue(info.GetString()!, out var display)) return names;
            using var cdi = JsonDocument.Parse(display.Bytes, new JsonDocumentOptions { MaxDepth = 16 });
            if (cdi.RootElement.TryGetProperty("Parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Array)
                foreach (var parameter in parameters.EnumerateArray().Take(2048))
                    if (parameter.ValueKind == JsonValueKind.Object && parameter.TryGetProperty("Id", out var id) && id.ValueKind == JsonValueKind.String &&
                        parameter.TryGetProperty("Name", out var name) && name.ValueKind == JsonValueKind.String && name.GetString() is { Length: > 0 and <= 64 } text)
                        names.TryAdd(id.GetString()!, text.Trim());
        }
        catch (JsonException) { }
        return names;
    }

    private static string ExpressionDetail(string file, byte[] bytes, Func<string, string> label)
    {
        var parts = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.TryGetProperty("Parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Array)
                foreach (var parameter in parameters.EnumerateArray().Take(10))
                {
                    if (parameter.ValueKind != JsonValueKind.Object || !parameter.TryGetProperty("Id", out var id) || id.ValueKind != JsonValueKind.String) continue;
                    var value = parameter.TryGetProperty("Value", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
                    var blend = parameter.TryGetProperty("Blend", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : "Add";
                    parts.Add(label(id.GetString()!) + (blend switch
                    {
                        "Multiply" => " x" + Number(value),
                        "Overwrite" => " =" + Number(value),
                        _ => (value >= 0 ? " +" : " ") + Number(value)
                    }));
                }
        }
        catch (JsonException) { }
        return $"file {Path.GetFileName(file)}; " + (parts.Count == 0 ? "changes nothing listed" : "sets " + string.Join(", ", parts));
    }

    private static string MotionDetail(IReadOnlyList<string> files, IReadOnlyDictionary<string, AvatarAsset> assets, Func<string, string> label)
    {
        var first = files[0];
        var detail = new StringBuilder();
        detail.Append(files.Count == 1 ? $"file {Path.GetFileName(first)}" : $"{files.Count} motions played at random, first {Path.GetFileName(first)}");
        try
        {
            using var document = JsonDocument.Parse(assets[first].Bytes, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.TryGetProperty("Meta", out var meta) && meta.ValueKind == JsonValueKind.Object &&
                meta.TryGetProperty("Duration", out var duration) && duration.ValueKind == JsonValueKind.Number)
                detail.Append($"; {Number(Math.Round(duration.GetDouble(), 1))} s");
            if (root.TryGetProperty("Curves", out var curves) && curves.ValueKind == JsonValueKind.Array)
            {
                var moved = curves.EnumerateArray()
                    .Where(c => c.ValueKind == JsonValueKind.Object && c.TryGetProperty("Target", out var t) && t.GetString() == "Parameter" &&
                        c.TryGetProperty("Id", out var i) && i.ValueKind == JsonValueKind.String)
                    .Select(c => c.GetProperty("Id").GetString()!).Distinct().ToArray();
                if (moved.Length > 0)
                    detail.Append("; moves ").Append(string.Join(", ", moved.Take(10).Select(label))).Append(moved.Length > 10 ? $" and {moved.Length - 10} more" : "");
            }
        }
        catch (JsonException) { }
        return detail.ToString();
    }

    private static readonly HashSet<string> NotActions = new(StringComparer.Ordinal)
    {
        "aa", "ih", "ou", "ee", "oh", "blink", "blinkLeft", "blinkRight", "lookUp", "lookDown", "lookLeft", "lookRight", "neutral"
    };

    private static IEnumerable<CharacterActionSource> VrmSources(byte[] glb)
    {
        JsonDocument document;
        try
        {
            if (glb.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(glb) != 0x46546C67) yield break;
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12));
            if (length <= 0 || 20 + length > glb.Length || BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(16)) != 0x4E4F534A) yield break;
            document = JsonDocument.Parse(glb.AsMemory(20, length), new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException) { yield break; }
        using (document)
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("extensions", out var extensions) || !extensions.TryGetProperty("VRMC_vrm", out var vrm) ||
                !vrm.TryGetProperty("expressions", out var expressions) || expressions.ValueKind != JsonValueKind.Object) yield break;
            string Morph(int node, int index)
            {
                try
                {
                    var mesh = root.GetProperty("nodes")[node].GetProperty("mesh").GetInt32();
                    var meshes = root.GetProperty("meshes")[mesh];
                    JsonElement names;
                    if (meshes.TryGetProperty("extras", out var extras) && extras.TryGetProperty("targetNames", out names) ||
                        meshes.GetProperty("primitives")[0].TryGetProperty("extras", out extras) && extras.TryGetProperty("targetNames", out names))
                        return names[index].GetString() ?? $"shape {index}";
                }
                catch (Exception error) when (error is KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException) { }
                return $"shape {index}";
            }
            foreach (var kind in new[] { "preset", "custom" })
            {
                if (!expressions.TryGetProperty(kind, out var set) || set.ValueKind != JsonValueKind.Object) continue;
                foreach (var expression in set.EnumerateObject().Take(128))
                {
                    if (kind == "preset" && NotActions.Contains(expression.Name) || expression.Value.ValueKind != JsonValueKind.Object) continue;
                    var value = expression.Value;
                    var parts = new List<string> { kind == "preset" ? "VRM preset expression" : "custom expression" };
                    if (value.TryGetProperty("morphTargetBinds", out var binds) && binds.ValueKind == JsonValueKind.Array && binds.GetArrayLength() > 0)
                        parts.Add("shapes " + string.Join(", ", binds.EnumerateArray().Take(8).Select(b =>
                            b.TryGetProperty("node", out var n) && b.TryGetProperty("index", out var i) && n.ValueKind == JsonValueKind.Number &&
                            i.ValueKind == JsonValueKind.Number ? Morph(n.GetInt32(), i.GetInt32()) : "?")));
                    if (value.TryGetProperty("materialColorBinds", out var colors) && colors.ValueKind == JsonValueKind.Array && colors.GetArrayLength() > 0)
                        parts.Add($"{colors.GetArrayLength()} color changes");
                    if (value.TryGetProperty("overrideMouth", out var mouth) && mouth.GetString() is "block" or "blend")
                        parts.Add($"{mouth.GetString()}s the mouth");
                    yield return new("expression:" + expression.Name, CharacterActionKind.Expression, expression.Name, Clip(string.Join("; ", parts)));
                }
            }
        }
    }
}
