using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Martlet.Avatars;
using Martlet.Core.Characters;

namespace Martlet.Avatar.Hosting;

/// <summary>What an emote or motion is: a Live2D expression or a VRM expression (held while it shows), a Live2D motion group
/// (played once) or one of Martlet's own gestures (played once; see <see cref="CharacterGesture"/>).</summary>
public enum CharacterActionKind { Expression, Motion, Gesture }

/// <summary>One of Martlet's own gestures. <see cref="Name"/> is what the renderer plays, <see cref="Tag"/> the default reply
/// tag and <see cref="Use"/> when to use it. A Live2D model gets it when it has every standard Cubism parameter in
/// <see cref="Live2DParameters"/>, a VRM when it has every humanoid bone in <see cref="VrmBones"/>; null means that renderer
/// never gets it. The renderers (Martlet.Avatar.Live2D's gestures.ts, Martlet.Avatar.Vrm's runtime.ts) check the same.
/// <see cref="Cue"/> is the voice cue (<see cref="Martlet.Core.Settings.VoiceTag.Cue"/>) it is linked to by default. A
/// <see cref="VoiceOnly"/> gesture (a voice emote, such as a laugh or a cough) plays when the voice makes its sound or tone and
/// isn't offered to replies as a tag while it keeps a cue, so replies' instructions don't grow. A <see cref="Holdable"/> gesture
/// can also be held (a renderer action with <c>hold</c>): eased into and kept, gently alive, until it is ended. Held gestures
/// layer: the renderer lets a held gesture go only when a new held one moves a part of the face it moves too (the eyes, the
/// mouth, the cheeks, the brows or the head), and held overlays all show together on top. An <see cref="Overlay"/>
/// gesture is a symbol drawn over the face (a sweat drop, hearts) rather than a movement.</summary>
public sealed record CharacterGesture(string Name, string Tag, string Use, string Does,
    IReadOnlyList<string>? Live2DParameters, IReadOnlyList<string>? VrmBones, string? Cue = null, bool VoiceOnly = false,
    bool Holdable = false, bool Overlay = false)
{
    public string Id => "gesture:" + Name;
}

/// <summary>One emote or motion a character model has. <see cref="Id"/> is stable for the model (<c>expression:F01</c>,
/// <c>motion:TapBody</c>, <c>gesture:nod</c>); <see cref="Name"/> is what the renderer plays (the expression's name or
/// motion group); <see cref="Detail"/> says what it changes, for the Thinking model and the owner (parameter IDs, their
/// display names and values, shape names, the motion's length), never a path. <see cref="Toggle"/>: a VTube Studio
/// ToggleExpression hotkey turns it on and off (a look meant to stay on).</summary>
public sealed record CharacterActionSource(string Id, CharacterActionKind Kind, string Name, string Detail, bool Toggle = false);

/// <summary>Every emote and motion of one character model, read from its files without showing it. <see cref="ModelId"/>
/// is the model's ID as the shared character list computes it (<see cref="CharacterModelLibrary.ModelId"/>), so a model has
/// the same emotes and settings wherever it is shown from.</summary>
public sealed record CharacterActionInventory(string ModelId, AvatarRenderer Renderer, IReadOnlyList<CharacterActionSource> Sources)
{
    public const int MaximumSources = 160;
    private const int MaximumDetail = 400;

    private static readonly string[] Head = ["head"], Spine = ["spine"], HeadSpine = ["head", "spine"],
        AngleX = ["ParamAngleX"], AngleY = ["ParamAngleY"], AngleZ = ["ParamAngleZ"];

    private static CharacterGesture Voice(string cue, string use, string does, string[] live2D, string[] vrm) =>
        new(cue.Replace(' ', '_'), cue.Replace(' ', '_'), use, does, live2D, vrm, cue, true);

    // An overlay emote: a symbol drawn over the face (Martlet.Avatar.RendererHost's web/effects/manpu.mjs), so any Live2D model
    // gets it and a VRM needs only the head bone the face is found from. A holdable one stays drawn while it is held.
    private static CharacterGesture Overlay(string name, string use, string does, bool holdable = false) =>
        new(name, name, use, does, [], Head, Holdable: holdable, Overlay: true);

    /// <summary>Every gesture Martlet has; each model gets those its renderer and rig support (<see cref="GesturesFor"/>).
    /// After the reply gestures come the voice emotes, one for each sound and tone a voice engine makes (except
    /// <c>surprised</c>, which the surprise gesture follows), then the touch and mood gestures, then the overlay emotes,
    /// anime symbols drawn over the face (a sweat drop, an anger vein, hearts...), and then the held face parts (the eyes
    /// turned up, an open mouth).</summary>
    public static readonly IReadOnlyList<CharacterGesture> AllGestures =
    [
        new("nod", "nod", "nod, for yes or agreement", "nods the head twice", ["ParamAngleY"], Head),
        new("shake", "shake_head", "shake your head, for no or disbelief", "shakes the head", ["ParamAngleX"], Head),
        new("tilt", "tilt_head", "tilt your head, for curiosity or confusion", "tilts the head to one side", ["ParamAngleZ"], Head),
        new("bow", "bow", "bow your head, for thanks, an apology or a greeting", "lowers the head in a small bow", ["ParamAngleY"], Spine),
        new("sway", "sway", "sway side to side, for contentment or enjoying something", "sways the body side to side",
            ["ParamBodyAngleZ"], Spine),
        new("smile", "smile", "smile with your eyes, for warmth or happiness", "smiles with the eyes and mouth for a few seconds",
            ["ParamEyeLSmile", "ParamEyeRSmile"], null),
        new("blush", "blush", "blush, for embarrassment or being flattered", "blushes for a few seconds", [], Head),
        new("surprise", "surprised", "raise your brows wide-eyed, for surprise", "raises the brows and widens the eyes",
            ["ParamBrowLY", "ParamBrowRY"], null, "surprised"),
        new("wave", "wave", "wave your hand, for hello or goodbye", "raises the right hand and waves", null, ["rightUpperArm", "rightLowerArm"]),
        new("shrug", "shrug", "shrug, for not knowing or not minding", "shrugs with both arms",
            null, ["leftUpperArm", "rightUpperArm", "leftLowerArm", "rightLowerArm"]),
        new("bounce", "bounce", "bounce with excitement", "bounces up and down", null, ["hips"]),
        Voice("laugh", "laugh, after something genuinely funny", "laughs: smiling eyes, the head thrown back and bobbing", AngleY, HeadSpine),
        Voice("chuckle", "a small amused chuckle", "chuckles: a small smile and a little bob of the head", AngleY, Head),
        Voice("sigh", "sigh, for relief, tiredness or mild exasperation", "sighs: the head and shoulders drop, the eyes half close",
            AngleY, HeadSpine),
        Voice("gasp", "gasp, for a surprise", "gasps: the head jerks back, the eyes and brows go wide", AngleY, HeadSpine),
        Voice("cough", "cough", "coughs: the head and body jerk forward a few times", AngleY, HeadSpine),
        Voice("clear throat", "clear your throat before saying something", "clears the throat: a small dip of the head, looking aside",
            AngleY, Head),
        Voice("groan", "groan, for something annoying or painful", "groans: the head rolls back, the eyes close", AngleZ, Head),
        Voice("sniff", "sniff", "sniffs: two quick lifts of the head", AngleY, Head),
        Voice("shush", "shush, for quiet", "shushes: leans in with the head down and the eyes narrowed", AngleY, HeadSpine),
        Voice("inhale", "breathe in, before something big", "breathes in: the chest and head rise", AngleY, HeadSpine),
        Voice("exhale", "breathe out, letting go of tension", "breathes out: the shoulders settle and the head lowers", AngleY, HeadSpine),
        Voice("mumble", "mumble", "mumbles: looks down and away", AngleX, Head),
        Voice("hum", "hum", "hums: the eyes close and the head sways gently", AngleZ, Head),
        Voice("sneeze", "sneeze", "sneezes: the head tips back, then snaps forward", AngleY, HeadSpine),
        Voice("whistle", "whistle, impressed or surprised", "whistles: looks up and away with the head tilted", AngleZ, Head),
        Voice("happy", "happy and cheerful, for good news or delight", "beams: smiling eyes and a cheerful bob of the head",
            ["ParamEyeLSmile", "ParamEyeRSmile"], HeadSpine),
        Voice("sarcastic", "sarcastic, for dry teasing or an obvious joke", "rolls the eyes with the head tilted", AngleZ, Head),
        Voice("angry", "angry, for real annoyance or outrage", "glowers: the brows drop and the head lowers",
            ["ParamBrowLY", "ParamBrowRY"], HeadSpine),
        Voice("fear", "fearful, for something scary or worrying", "shrinks back trembling, the brows raised", AngleX, HeadSpine),
        Voice("crying", "tearful, for something genuinely sad", "cries: the head bows, the eyes close and the shoulders sob",
            AngleY, HeadSpine),
        Voice("whispering", "whisper, for a secret or something hushed", "leans in with the head tilted, as if whispering",
            AngleZ, HeadSpine),
        Voice("dramatic", "dramatic, for playful theatrics", "throws the head back with a sweeping flourish", AngleZ,
            ["head", "leftUpperArm", "rightUpperArm"]),
        // Touch and mood gestures, after the rest so the reply instructions' earlier lines (and prompt caches) stay the same.
        new("wink", "wink", "wink, for a playful joke, teasing or a shared secret", "winks one eye with a little smile and a head tilt",
            ["ParamEyeLOpen"], Head),
        new("pout", "pout", "pout, for playful sulking or mock annoyance", "pouts: the mouth turns down, the cheeks puff, the head turns aside",
            ["ParamMouthForm"], Head, Holdable: true),
        new("shy", "shy", "act shy, for being flattered, teased or touched gently",
            "looks down and away bashfully with a half smile, peeking back now and then", ["ParamAngleX", "ParamAngleY"], HeadSpine,
            Holdable: true),
        new("giggle", "giggle", "giggle, for something cute or ticklish", "giggles: quick little bounces with smiling eyes", AngleY, HeadSpine),
        new("flinch", "flinch", "flinch, when startled or poked unexpectedly", "jerks back startled, then settles", AngleY, HeadSpine),
        new("lean_in", "lean_in", "lean in, for affection or enjoying a head pat", "leans in with the head tilted and the eyes softly closing",
            AngleZ, HeadSpine),
        new("look_away", "look_away", "look away, for embarrassment or dodging a question",
            "turns the head and eyes aside, glancing back now and then", AngleX, Head, Holdable: true),
        new("think", "think", "think it over, for pondering or remembering", "looks up and to the side, thinking", AngleY, Head),
        new("eye_roll", "eye_roll", "roll your eyes, for playful exasperation", "rolls the eyes up and around",
            ["ParamEyeBallX", "ParamEyeBallY"], Head),
        new("drowsy", "drowsy", "be drowsy, for tiredness or late at night",
            "half closes the eyes, the head slowly nodding off and catching itself", ["ParamEyeLOpen", "ParamEyeROpen"], HeadSpine,
            Holdable: true),
        Overlay("sweat", "a sweat drop, for nervousness or an awkward moment", "a sweat drop slides down beside the head", holdable: true),
        Overlay("anger", "an anger vein, for annoyance or irritation", "an anger vein throbs on the forehead"),
        Overlay("hearts", "floating hearts, for love, adoration or a crush", "small hearts float up around the head", holdable: true),
        Overlay("sparkles", "sparkles, for delight, excitement or pride", "sparkles twinkle around the face"),
        Overlay("tears", "tears, for sadness or being deeply moved", "tears stream from the eyes"),
        Overlay("gloom", "gloom lines, for feeling depressed or mortified", "dark gloom lines fall over the upper face", holdable: true),
        Overlay("question", "a question mark, for confusion", "a question mark pops up beside the head"),
        Overlay("exclaim", "an exclamation mark, for being startled or suddenly realizing something",
            "an exclamation mark pops up beside the head"),
        Overlay("sleepy", "a floating Zzz, for sleepiness or boredom", "Zzz floats up from the head", holdable: true),
        Overlay("music", "music notes, for humming or a happy, carefree mood", "music notes float up around the head"),
        // Held face parts, after the rest so the reply instructions' earlier lines (and prompt caches) stay the same. Each moves
        // one part of the face, so it stays on with held gestures that move other parts (a blush, a pout's brows).
        new("eyes_up", "eyes_up", "turn just your eyes up, for daydreaming, exasperation or being dazed",
            "turns only the eyes up and keeps them there; the head doesn't move", ["ParamEyeBallY"], ["leftEye", "rightEye"], Holdable: true),
        new("mouth_open", "mouth_open", "keep your mouth open, for awe, shock or being out of breath",
            "keeps the mouth open; the voice still moves it while it speaks", ["ParamMouthOpenY"], Head, Holdable: true)
    ];

    public static CharacterGesture? Gesture(string id) => AllGestures.FirstOrDefault(g => g.Id == id);

    /// <summary>The gestures a model gets: those its renderer supports whose parameters (Live2D) or bones (VRM) are all in
    /// <paramref name="rig"/>.</summary>
    public static IReadOnlyList<CharacterGesture> GesturesFor(AvatarRenderer renderer, IReadOnlySet<string> rig) =>
        AllGestures.Where(g => (renderer == AvatarRenderer.Vrm ? g.VrmBones : g.Live2DParameters) is { } needs && needs.All(rig.Contains)).ToArray();

    private static CharacterActionSource Source(CharacterGesture gesture, AvatarRenderer renderer, IReadOnlySet<string> rig) =>
        new(gesture.Id, CharacterActionKind.Gesture, gesture.Name, gesture.Overlay ? $"Martlet's own gesture: {gesture.Does} (drawn over the character)."
            : $"Martlet's own gesture: {gesture.Does} (" + (gesture.Name == "blush"
            // Every model blushes: with its own ParamCheek (Live2D) or blush expression (VRM, offered as its own emote instead),
            // otherwise Martlet draws a glow on the cheeks over the character.
            ? renderer == AvatarRenderer.Live2D && rig.Contains(Live2DBlushParameter) ? "moves " + Live2DBlushParameter
                : "Martlet draws a pink glow on the cheeks, following the " + (renderer == AvatarRenderer.Vrm ? "head bone" : "face")
            // A VRM opens its mouth with the oh mouth expression (aa without one), which lip-sync shares.
            : gesture.Name == "mouth_open" && renderer == AvatarRenderer.Vrm ? "sets the oh mouth expression, or aa"
            : "moves " + (renderer == AvatarRenderer.Vrm ? "the " + string.Join(", ", gesture.VrmBones!) + (gesture.VrmBones!.Count == 1 ? " bone" : " bones")
                : string.Join(", ", gesture.Live2DParameters!))) + ").");

    /// <summary>The Live2D parameter a model's own blush moves; models without it get one Martlet draws.</summary>
    private const string Live2DBlushParameter = "ParamCheek";

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
        var sources = (renderer == AvatarRenderer.Vrm ? VrmSources(assets[0].Bytes) : Live2DSources(entry, assets)).ToList();
        var rig = renderer == AvatarRenderer.Vrm ? VrmBones(assets[0].Bytes) : Live2DParameters(entry, assets);
        // A gesture whose tag the model's own emote or motion already has is left to the model's.
        var own = sources.Select((s, i) => CharacterActions.EnglishTag(s, sources.Take(i).Count(o => o.Kind == s.Kind) + 1))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gestures = GesturesFor(renderer, rig).Where(g => !own.Contains(g.Tag)).Select(g => Source(g, renderer, rig)).ToArray();
        return new(id, renderer, [.. sources.Take(MaximumSources - gestures.Length), .. gestures]);
    }

    /// <summary>Which of the parameters Martlet's gestures move a Live2D model has, found in its .moc3 (whose parameter IDs are
    /// null-padded strings).</summary>
    private static HashSet<string> Live2DParameters(string entry, IReadOnlyList<AvatarAsset> assets)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var model = assets.FirstOrDefault(a => a.Name == entry);
        if (model is null) return found;
        string? mocName = null;
        try
        {
            using var document = JsonDocument.Parse(model.Bytes, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.TryGetProperty("FileReferences", out var references) && references.ValueKind == JsonValueKind.Object &&
                references.TryGetProperty("Moc", out var moc) && moc.ValueKind == JsonValueKind.String)
                mocName = moc.GetString();
        }
        catch (JsonException) { }
        var directory = Path.GetDirectoryName(entry.Replace('\\', '/'))?.Replace('\\', '/');
        var bytes = assets.FirstOrDefault(a => a.Name == mocName || a.Name == (string.IsNullOrEmpty(directory) ? mocName : directory + "/" + mocName))?.Bytes;
        if (bytes is null) return found;
        foreach (var parameter in AllGestures.SelectMany(g => g.Live2DParameters ?? []).Append(Live2DBlushParameter).Distinct())
        {
            var needle = Encoding.ASCII.GetBytes(parameter + "\0");
            var span = bytes.AsSpan();
            for (int offset = 0, at; (at = span[offset..].IndexOf(needle)) >= 0; offset += at + 1)
                if (offset + at == 0 || span[offset + at - 1] == 0) { found.Add(parameter); break; }
        }
        return found;
    }

    /// <summary>The humanoid bones a VRM 1.0 declares.</summary>
    private static HashSet<string> VrmBones(byte[] glb)
    {
        var bones = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (glb.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(glb) != 0x46546C67) return bones;
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12));
            if (length <= 0 || 20 + length > glb.Length || BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(16)) != 0x4E4F534A) return bones;
            using var document = JsonDocument.Parse(glb.AsMemory(20, length), new JsonDocumentOptions { MaxDepth = 64 });
            if (document.RootElement.TryGetProperty("extensions", out var extensions) && extensions.TryGetProperty("VRMC_vrm", out var vrm) &&
                vrm.TryGetProperty("humanoid", out var humanoid) && humanoid.TryGetProperty("humanBones", out var human) &&
                human.ValueKind == JsonValueKind.Object)
                foreach (var bone in human.EnumerateObject().Take(64)) bones.Add(bone.Name);
        }
        catch (JsonException) { }
        return bones;
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
        var toggles = assets.Where(a => a.Name.EndsWith(".vtube.json", StringComparison.OrdinalIgnoreCase)).Select(a => VTubeStudio.Read(a.Bytes))
            .FirstOrDefault(v => v is not null && v.Model == entry)?.Hotkeys.Where(h => h.Action == "ToggleExpression").Select(h => h.File)
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        foreach (var (name, file) in declared.Expressions.Select(e => (e.Key, e.Value)).Concat(extras.Expressions.Select(e => (e.Name, e.File))))
        {
            if (!byName.TryGetValue(file, out var asset)) continue;
            yield return new("expression:" + name, CharacterActionKind.Expression, name, Clip(ExpressionDetail(file, asset.Bytes, Label)),
                toggles.Contains(file));
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
