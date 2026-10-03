using System.Text.Json;

namespace Martlet.Avatar.Hosting;

public sealed record Live2DExtraExpression(string Name, string File);
public sealed record Live2DExtraMotion(string Group, string File);

/// <summary>A Live2D model's expressions and motions that its model3.json doesn't declare (VTube Studio keeps them in its own
/// settings): what the renderer loads besides the declared ones.</summary>
public sealed record Live2DExtras(IReadOnlyList<Live2DExtraExpression> Expressions, IReadOnlyList<Live2DExtraMotion> Motions);

/// <summary>The parts of a VTube Studio <c>.vtube.json</c> Martlet reads: the model3.json it belongs to, its idle animation and
/// its expression and animation hotkeys (name and file). Everything else in it (tracking, items, physics tweaks) is ignored,
/// and nothing in it is ever run.</summary>
public sealed record VTubeStudio(string Model, string? Idle, IReadOnlyList<VTubeStudio.Hotkey> Hotkeys)
{
    public sealed record Hotkey(string Name, string Action, string File);

    public static VTubeStudio? Read(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32, AllowTrailingCommas = true });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("FileReferences", out var files) ||
                files.ValueKind != JsonValueKind.Object || !files.TryGetProperty("Model", out var model) || model.ValueKind != JsonValueKind.String)
                return null;
            string? Text(JsonElement element, string name) =>
                element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
                    ? text : null;
            var hotkeys = new List<Hotkey>();
            if (root.TryGetProperty("Hotkeys", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var hotkey in list.EnumerateArray().Take(256))
                    if (hotkey.ValueKind == JsonValueKind.Object && Text(hotkey, "Action") is { } action &&
                        action is "ToggleExpression" or "TriggerAnimation" or "ChangeIdleAnimation" && Text(hotkey, "File") is { } file)
                        hotkeys.Add(new((Text(hotkey, "Name") ?? "").Trim(), action, file.Replace('\\', '/')));
            return new(model.GetString()!, Text(files, "IdleAnimation")?.Replace('\\', '/'), hotkeys);
        }
        catch (JsonException) { return null; }
    }
}
