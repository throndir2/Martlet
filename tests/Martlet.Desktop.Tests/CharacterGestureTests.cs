using System.Buffers.Binary;
using System.Text;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;

namespace Martlet.Desktop.Tests;

public sealed class CharacterGestureTests
{
    private static byte[] Moc(params string[] ids)
    {
        var bytes = new byte[64 + ids.Length * 64];
        Encoding.ASCII.GetBytes("MOC3").CopyTo(bytes, 0);
        for (var i = 0; i < ids.Length; i++) Encoding.ASCII.GetBytes(ids[i]).CopyTo(bytes, 64 + i * 64);
        return bytes;
    }

    private static byte[] Glb(string json)
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

    private static readonly string[] HeldOverlays = ["{/sweat}", "{/hearts}", "{/gloom}", "{/sleepy}"];

    private static readonly string[] Overlays = ["sweat", "anger", "hearts", "sparkles", "tears", "gloom", "question", "exclaim", "sleepy", "music"];

    private static string[] Gestures(CharacterActionInventory inventory) =>
        inventory.Sources.Where(s => s.Kind == CharacterActionKind.Gesture).Select(s => s.Name).ToArray();

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
            "shy", "giggle", "flinch", "lean_in", "look_away", "think", .. Overlays], Gestures(inventory));
        var prompt = new CharacterActionCatalog(inventory, CharacterActions.Merge(inventory, null)).Prompt(null, null);
        Assert.NotNull(prompt);
        Assert.Equal(["{smile}", "{nod}", "{shake_head}", "{tilt_head}", "{bow}", "{blush}", "{shy}", "{giggle}", "{flinch}", "{lean_in}",
            "{look_away}", "{think}", .. Overlays.Select(o => "{" + o + "}"), "{/shy}", "{/look_away}", .. HeldOverlays], prompt.Tags);
        Assert.Contains("{tilt_head} - tilt your head, for curiosity or confusion", prompt.Instructions);
        Assert.Contains("ParamAngleZ", inventory.Find("gesture:tilt")!.Detail);
        Assert.Contains("(moves ParamCheek)", inventory.Find("gesture:blush")!.Detail);
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
            "whispering", "wink", "pout", "shy", "giggle", "flinch", "lean_in", "look_away", "think", "eye_roll", "drowsy", .. Overlays,
            "mouth_open"], Gestures(inventory));
        var prompt = new CharacterActionCatalog(inventory, CharacterActions.Merge(inventory, null)).Prompt(null, null);
        Assert.NotNull(prompt);
        Assert.Contains("{wave}", prompt.Tags);
        Assert.Contains("{happy}", prompt.Tags);
        Assert.Contains("{blush}", prompt.Tags);
        Assert.Contains("draws a pink glow on the cheeks, following the head bone", inventory.Find("gesture:blush")!.Detail);
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
        var catalog = new CharacterActionCatalog(inventory, CharacterActions.Merge(inventory, null));
        Assert.Null(CharacterActions.Problem(catalog.Settings));

        Assert.Equal(["laugh"], catalog.For("[laugh]").Select(s => s.Name));
        Assert.Equal(["clear_throat"], catalog.For("[clear throat]").Select(s => s.Name));
        Assert.Equal(["surprise"], catalog.For("[surprised]").Select(s => s.Name));
        Assert.Equal(["sigh"], catalog.For("(sighs)").Select(s => s.Name));
        Assert.Equal("laugh", catalog.Settings.Find("gesture:laugh")!.Cue);

        // Voice emotes follow the voice and never lengthen the reply instructions; the reply gestures stay offered, the holdable ones with off tags.
        var silent = catalog.Prompt(null, null)!.Tags;
        Assert.Equal(["{nod}", "{shake_head}", "{tilt_head}", "{bow}", "{smile}", "{blush}", "{surprised}", "{shy}", "{giggle}", "{flinch}",
            "{lean_in}", "{look_away}", "{think}", .. Overlays.Select(o => "{" + o + "}"), "{/shy}", "{/look_away}", .. HeldOverlays], silent);
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
        Assert.Equal(added, names.Where(n => !Overlays.Contains(n) && !FaceParts.Contains(n)).ToArray()[^added.Length..]);
        Assert.Equal(["pout", "shy", "look_away", "drowsy", "sweat", "hearts", "gloom", "sleepy", .. FaceParts],
            CharacterActionInventory.AllGestures.Where(g => g.Holdable).Select(g => g.Name));
        Assert.All(CharacterActionInventory.AllGestures, g => Assert.True(CharacterActions.IsTag(g.Tag) &&
            g.Use.Length <= CharacterActionCatalog.MaximumUseLength, g.Name));
        Assert.Equal(names.Length, names.Distinct().Count());

        var moc = Moc("ParamEyeLOpen", "ParamEyeROpen", "ParamMouthForm", "ParamEyeBallX", "ParamEyeBallY");
        var model = Encoding.UTF8.GetBytes("{\"Version\":3,\"FileReferences\":{\"Moc\":\"m.moc3\",\"Textures\":[]}}");
        var inventory = CharacterActionInventory.From(AvatarRenderer.Live2D, "m.model3.json",
            [new("m.model3.json", model, "application/json"), new("m.moc3", moc, "application/octet-stream")]);
        Assert.Equal(["blush", "wink", "pout", "eye_roll", "drowsy", .. Overlays, "eyes_up"], Gestures(inventory));
    }

    private static readonly string[] FaceParts = ["eyes_up", "mouth_open"];

    [Fact]
    public void TheHeldFacePartsComeLastLingerAndGoToModelsWithTheirParameterOrEyeBones()
    {
        // Last of all, so the reply instructions' earlier lines (and prompt caches) stay the same.
        var names = CharacterActionInventory.AllGestures.Select(g => g.Name).ToArray();
        Assert.Equal([.. Overlays, .. FaceParts], names[^(Overlays.Length + FaceParts.Length)..]);
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
        Assert.Equal(["blush", .. Overlays, .. FaceParts], Gestures(live2D));
        Assert.Contains("(moves ParamEyeBallY)", live2D.Find("gesture:eyes_up")!.Detail);
        Assert.Contains("(moves ParamMouthOpenY)", live2D.Find("gesture:mouth_open")!.Detail);
        var prompt = new CharacterActionCatalog(live2D, CharacterActions.Merge(live2D, null)).Prompt(null, null)!;
        Assert.Equal(["{blush}", .. Overlays.Select(o => "{" + o + "}"), "{eyes_up}", "{mouth_open}", .. HeldOverlays, "{/eyes_up}", "{/mouth_open}"],
            prompt.Tags);
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
        Assert.Equal(["blush", .. Overlays.Skip(1)], Gestures(live2D));
        Assert.Equal("Martlet's own gesture: small hearts float up around the head (drawn over the character).",
            live2D.Find("gesture:hearts")!.Detail);
        var prompt = new CharacterActionCatalog(live2D, CharacterActions.Merge(live2D, null)).Prompt(null, null)!;
        Assert.Equal(["{sweat}", "{blush}", .. Overlays.Skip(1).Select(o => "{" + o + "}"), .. HeldOverlays.Skip(1)], prompt.Tags);
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
}
