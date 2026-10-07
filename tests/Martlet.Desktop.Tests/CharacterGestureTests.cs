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
            "shush", "inhale", "exhale", "mumble", "hum", "sneeze", "whistle", "happy", "sarcastic", "fear", "crying", "whispering", "dramatic"],
            Gestures(inventory));
        var prompt = new CharacterActionCatalog(inventory, CharacterActions.Merge(inventory, null)).Prompt(null, null);
        Assert.NotNull(prompt);
        Assert.Equal(["{smile}", "{nod}", "{shake_head}", "{tilt_head}", "{bow}", "{blush}"], prompt.Tags);
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
            "whispering"], Gestures(inventory));
        var prompt = new CharacterActionCatalog(inventory, CharacterActions.Merge(inventory, null)).Prompt(null, null);
        Assert.NotNull(prompt);
        Assert.Contains("{wave}", prompt.Tags);
        Assert.Contains("{happy}", prompt.Tags);
        Assert.Contains("{blush}", prompt.Tags);
        Assert.Contains("draws a pink glow on the cheeks, following the head bone", inventory.Find("gesture:blush")!.Detail);
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

        // Voice emotes follow the voice and never lengthen the reply instructions; the reply gestures stay offered.
        var silent = catalog.Prompt(null, null)!.Tags;
        Assert.Equal(["{nod}", "{shake_head}", "{tilt_head}", "{bow}", "{smile}", "{blush}", "{surprised}"], silent);
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
}
