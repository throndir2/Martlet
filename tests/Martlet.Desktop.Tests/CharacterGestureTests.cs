using System.Buffers.Binary;
using System.Text;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;

namespace Martlet.Desktop.Tests;

public sealed class CharacterGestureTests
{
    internal static byte[] Moc(params string[] ids)
    {
        var bytes = new byte[64 + ids.Length * 64];
        Encoding.ASCII.GetBytes("MOC3").CopyTo(bytes, 0);
        for (var i = 0; i < ids.Length; i++) Encoding.ASCII.GetBytes(ids[i]).CopyTo(bytes, 64 + i * 64);
        return bytes;
    }

    internal static byte[] Glb(string json)
    {
        var text = Encoding.UTF8.GetBytes(json);
        var padded = (text.Length + 3) / 4 * 4;
        var glb = new byte[20 + padded];
        BinaryPrimitives.WriteUInt32LittleEndian(glb, 0x46546C67);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(8), (uint)glb.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(12), (uint)padded);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(16), 0x4E4F534A);
        text.CopyTo(glb, 20);
        glb.AsSpan(20 + text.Length).Fill((byte)' ');
        return glb;
    }

    // The overlay emotes before the held face parts and blush levels (eyes_up ... blush_fierce), and those after them.
    private static readonly string[] FirstOverlays = ["sweat", "anger", "hearts", "sparkles", "tears", "gloom", "question", "exclaim", "sleepy", "music"];
    private static readonly string[] LaterOverlays = ["heart_eyes", "star_eyes", "tongue_out", "drool", "steam", "dizzy", "idea", "ellipsis"];
    private static readonly string[] Overlays = [.. FirstOverlays, .. LaterOverlays];

    private static readonly string[] FirstHeldOverlays = ["{/sweat}", "{/hearts}", "{/gloom}", "{/sleepy}"];
    private static readonly string[] LaterHeldOverlays = ["{/heart_eyes}", "{/star_eyes}", "{/tongue_out}", "{/drool}", "{/steam}", "{/dizzy}"];

    private static string[] Tags(IEnumerable<string> names) => [.. names.Select(n => "{" + n + "}")];

    // The stronger blush levels every model gets, after the rest; both linger by default.
    private static readonly string[] Levels = ["blush_deep", "blush_fierce"];

    private static readonly string[] HeldLevels = ["{/blush_deep}", "{/blush_fierce}"];

    private static string[] Gestures(CharacterActionInventory inventory) =>
        inventory.Sources.Where(s => s.Kind == CharacterActionKind.Gesture).Select(s => s.Name).ToArray();

    // The model's emotes without Martlet's combos, whose lines and tags come after them (EmoteComboTests covers those).
    private static CharacterActionCatalog WithoutCombos(CharacterActionInventory inventory) =>
        new(inventory, CharacterActions.Merge(inventory, null) with { Combos = null });

    [Fact]
    public void Live2DModelGetsTheGesturesItsParametersAllowAndKeepsItsOwnSmile()
    {
        var model = Encoding.UTF8.GetBytes("{\"Version\":3,\"FileReferences\":{\"Moc\":\"m.moc3\",\"Textures\":[]," +
            "\"Expressions\":[{\"Name\":\"Smile\",\"File\":\"smile.exp3.json\"}]}}");
        var expression = Encoding.UTF8.GetBytes("{\"Type\":\"Live2D Expression\",\"Parameters\":[{\"Id\":\"ParamEyeLSmile\",\"Value\":1}]}");
        var moc = Moc("ParamAngleX", "ParamAngleY", "ParamAngleZ", "ParamCheek", "ParamEyeLSmile", "ParamEyeRSmile", "XParamBodyAngleZ", "ParamBrowLY");
        var inventory = CharacterActionInventory.From(AvatarRenderer.Live2D, "m.model3.json",
        [
            new("m.model3.json", model, "application/json"), new("m.moc3", moc, "application/octet-stream"),
            new("smile.exp3.json", expression, "application/json")
        ]);

        Assert.Equal(["nod", "shake", "tilt", "bow", "blush", "laugh", "chuckle", "sigh", "gasp", "cough", "clear_throat", "groan", "sniff",
            "shush", "inhale", "exhale", "mumble", "hum", "sneeze", "whistle", "happy", "sarcastic", "fear", "crying", "whispering", "dramatic",
            "shy", "giggle", "flinch", "lean_in", "look_away", "think", .. FirstOverlays, .. Levels, .. LaterOverlays], Gestures(inventory));
        var prompt = WithoutCombos(inventory).Prompt(null, null);
        Assert.NotNull(prompt);
        Assert.Equal(["{smile}", "{nod}", "{shake_head}", "{tilt_head}", "{bow}", "{blush}", "{shy}", "{giggle}", "{flinch}", "{lean_in}",
            "{look_away}", "{think}", .. Tags(FirstOverlays), .. Tags(Levels), .. Tags(LaterOverlays), "{/shy}", "{/look_away}",
            .. FirstHeldOverlays, .. HeldLevels, .. LaterHeldOverlays], prompt.Tags);
        Assert.Contains("{tilt_head} - tilt your head, for curiosity or confusion", prompt.Instructions);
        Assert.Contains("ParamAngleZ", inventory.Find("gesture:tilt")!.Detail);
        Assert.Contains("(moves ParamCheek)", inventory.Find("gesture:blush")!.Detail);
        Assert.Contains("(moves ParamCheek and Martlet draws it on the cheeks, following the face)", inventory.Find("gesture:blush_deep")!.Detail);
    }

    [Fact]
    public void VrmModelGetsTheBodyGesturesItsBonesAllowAndNoLive2DFaceGestures()
    {
        var bones = string.Join(",", new[] { "hips", "spine", "head", "rightUpperArm", "rightLowerArm" }.Select((b, i) => $"\"{b}\":{{\"node\":{i}}}"));
        var glb = Glb("{\"asset\":{\"version\":\"2.0\"},\"extensions\":{\"VRMC_vrm\":{\"specVersion\":\"1.0\",\"humanoid\":{\"humanBones\":{" + bones +
            "}},\"expressions\":{\"preset\":{\"happy\":{},\"aa\":{}}}}}}");
        var inventory = CharacterActionInventory.From(AvatarRenderer.Vrm, "m.vrm", [new("m.vrm", glb, "model/gltf-binary")]);

        Assert.Equal(["nod", "shake", "tilt", "bow", "sway", "blush", "wave", "bounce", "laugh", "chuckle", "sigh", "gasp", "cough", "clear_throat",
            "groan", "sniff", "shush", "inhale", "exhale", "mumble", "hum", "sneeze", "whistle", "sarcastic", "angry", "fear", "crying",
            "whispering", "wink", "pout", "shy", "giggle", "flinch", "lean_in", "look_away", "think", "eye_roll", "drowsy", .. FirstOverlays,
            "mouth_open", .. Levels, .. LaterOverlays], Gestures(inventory));
        var prompt = new CharacterActionCatalog(inventory, CharacterActions.Merge(inventory, null)).Prompt(null, null);
        Assert.NotNull(prompt);
        Assert.Contains("{wave}", prompt.Tags);
        Assert.Contains("{happy}", prompt.Tags);
        Assert.Contains("{blush}", prompt.Tags);
        Assert.Contains("draws a pink glow on the cheeks, following the head bone", inventory.Find("gesture:blush")!.Detail);
        Assert.Contains("(Martlet draws it on the cheeks, following the head bone, over the model's own blush expression when it has one)",
            inventory.Find("gesture:blush_fierce")!.Detail);
        Assert.Contains("(sets the oh mouth expression, or aa)", inventory.Find("gesture:mouth_open")!.Detail);
        Assert.DoesNotContain("{shrug}", prompt.Tags);
        Assert.Contains("rightUpperArm", inventory.Find("gesture:wave")!.Detail);
    }

    [Fact]
    public void AnActionTheRendererDrewOverTheFaceSaysWhere()
    {
        static System.Text.Json.JsonElement Reply(string json) => System.Text.Json.JsonDocument.Parse(json).RootElement;
        Assert.Null(AvatarController.Drawn(Reply("{\"started\":true}")));
        Assert.Equal(", drawn by Martlet over the face at 250, 100 (80 pixels wide)",
            AvatarController.Drawn(Reply("{\"started\":true,\"overlay\":true,\"face\":{\"x\":250,\"y\":100,\"width\":80}}")));
        Assert.Equal(", drawn by Martlet over the face at 250, 100 (80 pixels wide, tilted -4°)",
            AvatarController.Drawn(Reply("{\"started\":true,\"overlay\":true,\"face\":{\"x\":250,\"y\":100,\"width\":80,\"tilt\":-4}}")));
        Assert.Equal(", drawn by Martlet over the face at 250, 100 (80 pixels wide, tilted 3°, pinned to the face's meshes)",
            AvatarController.Drawn(Reply("{\"started\":true,\"overlay\":true,\"face\":{\"x\":250,\"y\":100,\"width\":80,\"tilt\":3,\"tracking\":\"mesh\"}}")));
        Assert.Equal(", drawn by Martlet over the face at 250, 100 (80 pixels wide, tilted 0°, following the head bone)",
            AvatarController.Drawn(Reply("{\"started\":true,\"overlay\":true,\"face\":{\"x\":250,\"y\":100,\"width\":80,\"tilt\":0,\"tracking\":\"bones\"}}")));
        Assert.Equal(", drawn by Martlet over the face (not in view now)",
            AvatarController.Drawn(Reply("{\"started\":true,\"overlay\":true,\"face\":null}")));
    }

    [Fact]
    public void EveryVoiceSoundAndToneHasAGlobalEmoteLinkedToItThatFollowsTheVoiceOnly()
    {
        var gestureCues = CharacterActionInventory.AllGestures.Where(g => g.Cue is not null).Select(g => g.Cue!).ToArray();
        Assert.Equal(Martlet.Core.Settings.VoiceTags.Cues.Order(), gestureCues.Order());
        Assert.All(CharacterActionInventory.AllGestures.Where(g => g.VoiceOnly), g => Assert.True(CharacterActions.IsTag(g.Tag), g.Tag));

        var moc = Moc("ParamAngleX", "ParamAngleY", "ParamAngleZ", "ParamEyeLSmile", "ParamEyeRSmile", "ParamBrowLY", "ParamBrowRY");
        var model = Encoding.UTF8.GetBytes("{\"Version\":3,\"FileReferences\":{\"Moc\":\"m.moc3\",\"Textures\":[]}}");
        var inventory = CharacterActionInventory.From(AvatarRenderer.Live2D, "m.model3.json",
            [new("m.model3.json", model, "application/json"), new("m.moc3", moc, "application/octet-stream")]);
        var catalog = WithoutCombos(inventory);
        Assert.Null(CharacterActions.Problem(catalog.Settings));

        Assert.Equal(["laugh"], catalog.For("[laugh]").Select(s => s.Name));
        Assert.Equal(["clear_throat"], catalog.For("[clear throat]").Select(s => s.Name));
        Assert.Equal(["surprise"], catalog.For("[surprised]").Select(s => s.Name));
        Assert.Equal(["sigh"], catalog.For("(sighs)").Select(s => s.Name));
        Assert.Equal("laugh", catalog.Settings.Find("gesture:laugh")!.Cue);

        // Voice emotes follow the voice and never lengthen the reply instructions; the reply gestures stay offered, the holdable ones with off tags.
        var silent = catalog.Prompt(null, null)!.Tags;
        Assert.Equal(["{nod}", "{shake_head}", "{tilt_head}", "{bow}", "{smile}", "{blush}", "{surprised}", "{shy}", "{giggle}", "{flinch}",
            "{lean_in}", "{look_away}", "{think}", .. Tags(FirstOverlays), .. Tags(Levels), .. Tags(LaterOverlays), "{/shy}",
            "{/look_away}", .. FirstHeldOverlays, .. HeldLevels, .. LaterHeldOverlays], silent);
        Assert.Contains("draws a pink glow on the cheeks", inventory.Find("gesture:blush")!.Detail);
        var chatterbox = catalog.Prompt(Martlet.Core.Settings.SpeechEngines.Chatterbox, null)!.Tags;
        Assert.DoesNotContain("{surprised}", chatterbox);
        Assert.DoesNotContain("{laugh}", chatterbox);

        // Clearing a voice emote's cue offers it to replies as a tag instead.
        var cleared = catalog with { Settings = catalog.Settings with { Actions = catalog.Settings.Actions
            .Select(a => a.Id == "gesture:laugh" ? a with { Cue = null } : a).ToArray() } };
        Assert.Contains("{laugh}", cleared.Prompt(null, null)!.Tags);
        Assert.Empty(cleared.For("[laugh]"));
    }

    [Fact]
    public void TouchAndMoodGesturesAreOfferedAfterTheOthersAndTheMoodsCanBeHeld()
    {
        var names = CharacterActionInventory.AllGestures.Select(g => g.Name).ToArray();
        string[] added = ["wink", "pout", "shy", "giggle", "flinch", "lean_in", "look_away", "think", "eye_roll", "drowsy"];
        Assert.Equal(added, names.Where(n => !Overlays.Contains(n) && !FaceParts.Contains(n) && !Levels.Contains(n)).ToArray()[^added.Length..]);
        Assert.Equal(["pout", "shy", "look_away", "drowsy", "sweat", "hearts", "gloom", "sleepy", .. FaceParts, .. Levels, "heart_eyes", "star_eyes",
            "tongue_out", "drool", "steam", "dizzy"], CharacterActionInventory.AllGestures.Where(g => g.Holdable).Select(g => g.Name));
        Assert.All(CharacterActionInventory.AllGestures, g => Assert.True(CharacterActions.IsTag(g.Tag) &&
            g.Use.Length <= CharacterActionCatalog.MaximumUseLength, g.Name));
        Assert.Equal(names.Length, names.Distinct().Count());

        var moc = Moc("ParamEyeLOpen", "ParamEyeROpen", "ParamMouthForm", "ParamEyeBallX", "ParamEyeBallY");
        var model = Encoding.UTF8.GetBytes("{\"Version\":3,\"FileReferences\":{\"Moc\":\"m.moc3\",\"Textures\":[]}}");
        var inventory = CharacterActionInventory.From(AvatarRenderer.Live2D, "m.model3.json",
            [new("m.model3.json", model, "application/json"), new("m.moc3", moc, "application/octet-stream")]);
        Assert.Equal(["blush", "wink", "pout", "eye_roll", "drowsy", .. FirstOverlays, "eyes_up", .. Levels, .. LaterOverlays], Gestures(inventory));
    }

    private static readonly string[] FaceParts = ["eyes_up", "mouth_open"];

    [Fact]
    public void TheHeldFacePartsComeAfterTheFirstOverlaysLingerAndGoToModelsWithTheirParameterOrEyeBones()
    {
        // After the first overlay emotes (only the blush levels and the later overlay emotes come after them): each group was added
        // at the end, so the reply instructions' earlier lines (and prompt caches) stay the same.
        var names = CharacterActionInventory.AllGestures.Select(g => g.Name).ToArray();
        Assert.Equal([.. FirstOverlays, .. FaceParts, .. Levels, .. LaterOverlays],
            names[^(Overlays.Length + FaceParts.Length + Levels.Length)..]);
        Assert.All(FaceParts.Select(n => CharacterActionInventory.Gesture("gesture:" + n)!), g =>
        {
            Assert.True(g.Holdable);
            Assert.False(g.Overlay || g.VoiceOnly);
            Assert.Null(g.Cue);
            Assert.Equal(g.Name, g.Tag);
            Assert.Equal(CharacterActions.Lingering, CharacterActions.DefaultMode(new(g.Id, CharacterActionKind.Gesture, g.Name, ""), g.Tag));
        });

        var model = Encoding.UTF8.GetBytes("{\"Version\":3,\"FileReferences\":{\"Moc\":\"m.moc3\",\"Textures\":[]}}");
        var live2D = CharacterActionInventory.From(AvatarRenderer.Live2D, "m.model3.json",
            [new("m.model3.json", model, "application/json"), new("m.moc3", Moc("ParamEyeBallY", "ParamMouthOpenY"), "application/octet-stream")]);
        Assert.Equal(["blush", .. FirstOverlays, .. FaceParts, .. Levels, .. LaterOverlays], Gestures(live2D));
        Assert.Contains("(moves ParamEyeBallY)", live2D.Find("gesture:eyes_up")!.Detail);
        Assert.Contains("(moves ParamMouthOpenY)", live2D.Find("gesture:mouth_open")!.Detail);
        var prompt = WithoutCombos(live2D).Prompt(null, null)!;
        Assert.Equal(["{blush}", .. Tags(FirstOverlays), "{eyes_up}", "{mouth_open}", .. Tags(Levels), .. Tags(LaterOverlays),
            .. FirstHeldOverlays, "{/eyes_up}", "{/mouth_open}", .. HeldLevels, .. LaterHeldOverlays], prompt.Tags);
        Assert.Contains("{eyes_up} - turn just your eyes up, for daydreaming, exasperation or being dazed (stays on until you write {/eyes_up})",
            prompt.Instructions);
        Assert.True(prompt.Instructions.IndexOf("{mouth_open} - ", StringComparison.Ordinal) >
            prompt.Instructions.IndexOf("{music} - ", StringComparison.Ordinal), "their lines come after the others");

        // A VRM turns its eye bones up and opens its oh (or aa) mouth; without eye bones it keeps only the mouth.
        string Vrm(params string[] bones) => "{\"asset\":{\"version\":\"2.0\"},\"extensions\":{\"VRMC_vrm\":{\"specVersion\":\"1.0\",\"humanoid\":" +
            "{\"humanBones\":{" + string.Join(",", bones.Select((b, i) => $"\"{b}\":{{\"node\":{i}}}")) + "}}}}}";
        var eyes = CharacterActionInventory.From(AvatarRenderer.Vrm, "m.vrm", [new("m.vrm", Glb(Vrm("head", "leftEye", "rightEye")), "model/gltf-binary")]);
        Assert.Equal(FaceParts, Gestures(eyes).Intersect(FaceParts));
        Assert.Contains("(moves the leftEye, rightEye bones)", eyes.Find("gesture:eyes_up")!.Detail);
        Assert.Contains("(sets the oh mouth expression, or aa)", eyes.Find("gesture:mouth_open")!.Detail);
        var noEyes = CharacterActionInventory.From(AvatarRenderer.Vrm, "m.vrm", [new("m.vrm", Glb(Vrm("head")), "model/gltf-binary")]);
        Assert.Equal(["mouth_open"], Gestures(noEyes).Intersect(FaceParts));
    }

    [Fact]
    public void TheLastActionSaysWhichGesturePlaysAndEveryOneHeld()
    {
        static System.Text.Json.JsonElement Reply(string json) => System.Text.Json.JsonDocument.Parse(json).RootElement;
        Assert.Equal(" Gestures now: wink playing, eyes_up, mouth_open, blush, hearts held.",
            AvatarController.GestureState(Reply("{\"started\":true,\"gesture\":{\"playing\":\"wink\",\"held\":[\"eyes_up\",\"mouth_open\",\"blush\",\"hearts\"]}}")));
        Assert.Equal(" Gestures now: none playing, shy held.", AvatarController.GestureState(Reply("{\"started\":true,\"gesture\":{\"held\":[\"shy\"]}}")));
        Assert.Equal(" Gestures now: none playing, sweat held.",
            AvatarController.GestureState(Reply("{\"started\":true,\"gesture\":{\"held\":[\"Not a tag!\",3,\"sweat\"]}}")));
        Assert.Equal(" Gestures now: none playing, none held.", AvatarController.GestureState(Reply("{\"started\":true,\"gesture\":{\"held\":[]}}")));
        Assert.Equal(" Gestures now: none playing, none held.", AvatarController.GestureState(Reply("{\"started\":true,\"gesture\":{}}")));
        Assert.Equal("", AvatarController.GestureState(Reply("{\"started\":true}")));
    }

    [Fact]
    public void OverlayEmotesGoToEveryLive2DModelAndEveryVrmWithAHeadUnlessTheModelHasItsOwn()
    {
        Assert.Equal(Overlays, CharacterActionInventory.AllGestures.Where(g => g.Overlay).Select(g => g.Name));
        Assert.All(CharacterActionInventory.AllGestures.Where(g => g.Overlay), g =>
        {
            Assert.Equal(g.Name, g.Tag);
            Assert.True(CharacterActions.IsTag(g.Tag), g.Tag);
            Assert.InRange(g.Use.Length, 1, CharacterActionCatalog.MaximumUseLength);
            Assert.Null(g.Cue);
            Assert.False(g.VoiceOnly);
        });

        // A Live2D model with none of the standard parameters and its own "sweat" expression: every overlay but sweat (and the blush
        // every model gets).
        var model = Encoding.UTF8.GetBytes("{\"Version\":3,\"FileReferences\":{\"Moc\":\"m.moc3\",\"Textures\":[]," +
            "\"Expressions\":[{\"Name\":\"Sweat\",\"File\":\"sweat.exp3.json\"}]}}");
        var expression = Encoding.UTF8.GetBytes("{\"Type\":\"Live2D Expression\",\"Parameters\":[{\"Id\":\"ParamSweat\",\"Value\":1}]}");
        var live2D = CharacterActionInventory.From(AvatarRenderer.Live2D, "m.model3.json",
        [
            new("m.model3.json", model, "application/json"), new("m.moc3", Moc("ParamSweat"), "application/octet-stream"),
            new("sweat.exp3.json", expression, "application/json")
        ]);
        Assert.Equal(["blush", .. FirstOverlays.Skip(1), .. Levels, .. LaterOverlays], Gestures(live2D));
        Assert.Equal("Martlet's own gesture: small hearts float up around the head (drawn over the character).",
            live2D.Find("gesture:hearts")!.Detail);
        var prompt = WithoutCombos(live2D).Prompt(null, null)!;
        Assert.Equal(["{sweat}", "{blush}", .. Tags(FirstOverlays.Skip(1)), .. Tags(Levels), .. Tags(LaterOverlays),
            .. FirstHeldOverlays.Skip(1), .. HeldLevels, .. LaterHeldOverlays], prompt.Tags);
        Assert.Contains("{gloom} - gloom lines, for feeling depressed or mortified", prompt.Instructions);

        // A VRM needs the head bone its face is found from.
        string Vrm(params string[] names) => "{\"asset\":{\"version\":\"2.0\"},\"extensions\":{\"VRMC_vrm\":{\"specVersion\":\"1.0\",\"humanoid\":" +
            "{\"humanBones\":{" + string.Join(",", names.Select((b, i) => $"\"{b}\":{{\"node\":{i}}}")) + "}}}}}";
        Assert.Empty(Gestures(CharacterActionInventory.From(AvatarRenderer.Vrm, "m.vrm", [new("m.vrm", Glb(Vrm("hips")), "model/gltf-binary")]))
            .Intersect(Overlays));
        var vrm = CharacterActionInventory.From(AvatarRenderer.Vrm, "m.vrm", [new("m.vrm", Glb(Vrm("head")), "model/gltf-binary")]);
        Assert.Equal(Overlays, Gestures(vrm).Intersect(Overlays));
        Assert.EndsWith("(drawn over the character).", vrm.Find("gesture:tears")!.Detail);
    }

    [Fact]
    public void TheEyeAndMouthOverlaysComeLastAndLingerAndAModelsOwnReplacesThem()
    {
        // Appended last, so the reply instructions' earlier lines (and prompt caches) stay the same.
        var model = Encoding.UTF8.GetBytes("{\"Version\":3,\"FileReferences\":{\"Moc\":\"m.moc3\",\"Textures\":[]}}");
        var plain = CharacterActionInventory.From(AvatarRenderer.Live2D, "m.model3.json",
            [new("m.model3.json", model, "application/json"), new("m.moc3", Moc("ParamAngleX"), "application/octet-stream")]);
        var prompt = new CharacterActionCatalog(plain, CharacterActions.Merge(plain, null)).Prompt(null, null)!;
        Assert.Contains("(stays on until you write {/blush_fierce})\n" +
            "{heart_eyes} - heart eyes, for being smitten or adoring (stays on until you write {/heart_eyes})\n" +
            "{star_eyes} - starry eyes, for being starstruck or thrilled (stays on until you write {/star_eyes})\n" +
            "{tongue_out} - stick your tongue out, for a playful tease (stays on until you write {/tongue_out})\n" +
            "{drool} - drool, for craving something tasty or dozing off (stays on until you write {/drool})\n" +
            "{steam} - steam puffs, for fuming or being overheated (stays on until you write {/steam})\n" +
            "{dizzy} - swirly eyes, for being dizzy or dazed (stays on until you write {/dizzy})\n" +
            "{idea} - a light bulb, for a sudden idea\n" +
            "{ellipsis} - an ellipsis, for being speechless or an awkward silence\n", prompt.Instructions);

        // A model whose own expressions are named 爱心眼 (heart eyes) and 吐舌 (tongue out) keeps them for those tags.
        var named = Encoding.UTF8.GetBytes("{\"Version\":3,\"FileReferences\":{\"Moc\":\"m.moc3\",\"Textures\":[]," +
            "\"Expressions\":[{\"Name\":\"爱心眼\",\"File\":\"a.exp3.json\"},{\"Name\":\"吐舌\",\"File\":\"b.exp3.json\"}]}}");
        var expression = Encoding.UTF8.GetBytes("{\"Type\":\"Live2D Expression\",\"Parameters\":[{\"Id\":\"ParamEyeForm\",\"Value\":1}]}");
        var own = CharacterActionInventory.From(AvatarRenderer.Live2D, "m.model3.json",
        [
            new("m.model3.json", named, "application/json"), new("m.moc3", Moc("ParamEyeForm"), "application/octet-stream"),
            new("a.exp3.json", expression, "application/json"), new("b.exp3.json", expression, "application/json")
        ]);
        Assert.Equal(["blush", .. FirstOverlays, .. Levels, .. LaterOverlays.Except(["heart_eyes", "tongue_out"])], Gestures(own));
        var tags = new CharacterActionCatalog(own, CharacterActions.Merge(own, null)).Prompt(null, null)!.Tags;
        Assert.Equal(["{heart_eyes}", "{tongue_out}"], tags.Take(2));
        Assert.Single(tags, "{heart_eyes}");
        Assert.Contains("{star_eyes}", tags);
    }

    [Fact]
    public void BlushLevelsGoToEveryModelLingerAndSayHowEachShows()
    {
        // The blush stays as it was; the stronger levels come after the gestures that were there before them, linger and are
        // short, general hints.
        Assert.Equal(["blush", .. Levels], CharacterActionInventory.BlushLevels);
        var names = CharacterActionInventory.AllGestures.Select(g => g.Name).ToList();
        Assert.True(names.IndexOf("blush_deep") > names.IndexOf("music") && names.IndexOf("blush_fierce") == names.IndexOf("blush_deep") + 1);
        var blush = CharacterActionInventory.Gesture("gesture:blush")!;
        Assert.Equal(("blush", "blush, for embarrassment or being flattered", false), (blush.Tag, blush.Use, blush.Holdable));
        Assert.All(Levels.Select(l => CharacterActionInventory.Gesture("gesture:" + l)!), g =>
        {
            Assert.Equal(g.Name, g.Tag);
            Assert.True(g.Holdable && !g.Overlay && !g.VoiceOnly && g.Cue is null, g.Name);
            Assert.InRange(g.Use.Length, 1, 70);
            Assert.Empty(g.Live2DParameters!);
            Assert.Equal(["head"], g.VrmBones);
        });

        // A Live2D model with no standard parameters gets every level, drawn by Martlet.
        var model = Encoding.UTF8.GetBytes("{\"Version\":3,\"FileReferences\":{\"Moc\":\"m.moc3\",\"Textures\":[]}}");
        CharacterActionInventory Live2D(params string[] parameters) => CharacterActionInventory.From(AvatarRenderer.Live2D, "m.model3.json",
            [new("m.model3.json", model, "application/json"), new("m.moc3", Moc(parameters), "application/octet-stream")]);
        var bare = Live2D("ParamSomething");
        Assert.Equal(["blush", .. Levels], Gestures(bare).Where(CharacterActionInventory.BlushLevels.Contains));
        Assert.Equal("Martlet's own gesture: blushes deeply: redder and wider, with more lines (Martlet draws it on the cheeks, following the face).",
            bare.Find("gesture:blush_deep")!.Detail);
        Assert.Equal("Martlet's own gesture: flushes fiercely: deep red across both cheeks and the nose, densely lined (Martlet draws it on the " +
            "cheeks, following the face).", bare.Find("gesture:blush_fierce")!.Detail);
        // With ParamCheek the blush is the model's own, and the stronger levels add Martlet's drawing over it.
        var cheek = Live2D("ParamCheek");
        Assert.EndsWith("(moves ParamCheek).", cheek.Find("gesture:blush")!.Detail);
        Assert.EndsWith("(moves ParamCheek and Martlet draws it on the cheeks, following the face).", cheek.Find("gesture:blush_fierce")!.Detail);

        // The blush is brief and the stronger levels linger, each turned off with its own off tag.
        var catalog = new CharacterActionCatalog(cheek, CharacterActions.Merge(cheek, null));
        Assert.Equal([CharacterActions.Brief, CharacterActions.Lingering, CharacterActions.Lingering],
            CharacterActionInventory.BlushLevels.Select(l => CharacterActions.DefaultMode(cheek.Find("gesture:" + l)!, l)));
        var prompt = catalog.Prompt(null, null)!;
        Assert.Contains("{blush} - blush, for embarrassment or being flattered\n", prompt.Instructions);
        Assert.Contains("{blush_deep} - a deep blush, for strong embarrassment (stays on until you write {/blush_deep})", prompt.Instructions);
        Assert.Contains("{blush_fierce} - a fierce flush across the face, for being overwhelmed or flustered (stays on until you write " +
            "{/blush_fierce})", prompt.Instructions);
        Assert.True(prompt.Instructions.IndexOf("{blush_deep}", StringComparison.Ordinal) > prompt.Instructions.IndexOf("{music}", StringComparison.Ordinal),
            "the new lines come after the ones before them, so the start of the instructions stays the same");
        Assert.Equal(["blush_fierce"], catalog.Off("{/blush_fierce}").Select(s => s.Name));
        Assert.Empty(catalog.Off("{/blush}"));
        Assert.Equal(["blush_deep"], catalog.For("{blush_deep}").Select(s => s.Name));

        // A VRM gets them with the head bone its face is found from.
        string Vrm(params string[] names) => "{\"asset\":{\"version\":\"2.0\"},\"extensions\":{\"VRMC_vrm\":{\"specVersion\":\"1.0\",\"humanoid\":" +
            "{\"humanBones\":{" + string.Join(",", names.Select((b, i) => $"\"{b}\":{{\"node\":{i}}}")) + "}}}}}";
        Assert.Equal(["blush", .. Levels], Gestures(CharacterActionInventory.From(AvatarRenderer.Vrm, "m.vrm",
            [new("m.vrm", Glb(Vrm("head")), "model/gltf-binary")])).Where(CharacterActionInventory.BlushLevels.Contains));
        Assert.Empty(Gestures(CharacterActionInventory.From(AvatarRenderer.Vrm, "m.vrm", [new("m.vrm", Glb(Vrm("hips")), "model/gltf-binary")]))
            .Intersect(CharacterActionInventory.BlushLevels));
    }
}
