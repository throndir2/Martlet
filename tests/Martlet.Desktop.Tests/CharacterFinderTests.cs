using System.Text;
using System.Text.Json;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Core.Contracts;

namespace Martlet.Desktop.Tests;

/// <summary>Find a character (docs/AVATARS.md#find-a-character-online): reading VRoid Hub and Open Source Avatars answers into
/// looks with their conditions of use, VRoid Hub looks held in memory only and sent to the renderer in pieces, and VRM 0.x
/// emotes named as the renderer names them.</summary>
public sealed class CharacterFinderTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private const string Vrm0Model = """
        {"id":"2776400212384515224","name":"Miku","is_private":false,"is_downloadable":true,"is_other_users_available":true,
         "age_limit":{"is_r18":false,"is_r15":false,"is_adult":false},
         "portrait_image":{"is_default_image":false,"w300":{"url":"https://vroid-hub.pximg.net/c/300x400/p.png"}},
         "license":{"modification":"allow","redistribution":"disallow","credit":"necessary","characterization_allowed_user":"everyone",
           "sexual_expression":"disallow","violent_expression":"disallow","corporate_commercial_use":"disallow","personal_commercial_use":"nonprofit"},
         "character":{"id":"123","name":"Miku","user":{"id":"999","name":"Creator A"}},
         "latest_character_model_version":{"spec_version":"0.0","original_file_size":14883772}}
        """;

    [Fact]
    public void VRoidHubModelBecomesALookWithItsConditionsOfUse()
    {
        var candidate = VRoidHub.Candidate(Json(Vrm0Model), ownerId: null);

        Assert.NotNull(candidate);
        Assert.Equal("vroidhub:2776400212384515224", candidate.Look);
        Assert.Equal("Creator A", candidate.Author);
        Assert.True(candidate.Usable);
        Assert.True(candidate.CreditRequired);
        Assert.False(candidate.Kept);
        Assert.Equal("0.0", candidate.VrmVersion);
        Assert.Equal("https://hub.vroid.com/en/characters/123/models/2776400212384515224", candidate.PageUrl);
        Assert.Contains("Use as an avatar or character: anyone", candidate.Conditions);
        Assert.Contains("Credit: required", candidate.Conditions);
        Assert.Contains("Redistribution: not allowed", candidate.Conditions);
        Assert.Contains("Personal commercial use: only non-profit", candidate.Conditions);
        Assert.Equal("Miku by Creator A (VRoid Hub, VRoid Hub conditions of use)", candidate.Credit);
    }

    [Fact]
    public void VRoidHubModelsOnlyTheCreatorMayUseAgeRestrictedOrPrivateAreLeftOutOrNotUsable()
    {
        var authorOnly = Vrm0Model.Replace("\"characterization_allowed_user\":\"everyone\"", "\"characterization_allowed_user\":\"author\"");
        Assert.False(VRoidHub.Candidate(Json(authorOnly), ownerId: null)!.Usable);
        // The owner's own model is always usable.
        Assert.True(VRoidHub.Candidate(Json(authorOnly), ownerId: "999")!.Usable);
        Assert.Null(VRoidHub.Candidate(Json(Vrm0Model.Replace("\"is_r18\":false", "\"is_r18\":true")), ownerId: null));
        Assert.Null(VRoidHub.Candidate(Json(Vrm0Model.Replace("\"is_private\":false", "\"is_private\":true")), ownerId: null));
        Assert.Null(VRoidHub.Candidate(Json(Vrm0Model.Replace("\"id\":\"2776400212384515224\"", "\"id\":\"../x\"")), ownerId: null));
    }

    [Fact]
    public void VRoidHubVrm1ModelReadsItsConditionsFromItsVrmMeta()
    {
        var vrm1 = """
            {"id":"4424429329517961076","name":null,"is_downloadable":true,"license":null,
             "character":{"id":"7","name":"Bunny Miku","user":{"id":"5","name":"fruitsnaccy"}},
             "latest_character_model_version":{"spec_version":"1.0","vrm_meta":{"avatarPermission":"onlyAuthor","creditNotation":"required",
               "modification":"prohibited","allowRedistribution":false,"commercialUsage":"personalNonProfit","licenseUrl":"https://vrm.dev/licenses/1.0/"}}}
            """;
        var candidate = VRoidHub.Candidate(Json(vrm1), ownerId: null)!;

        Assert.Equal("Bunny Miku", candidate.Name);
        Assert.False(candidate.Usable);
        Assert.Contains("Use as an avatar or character: only the creator", candidate.Conditions);
        Assert.Contains("License: https://vrm.dev/licenses/1.0/", candidate.Conditions);
        Assert.True(VRoidHub.Candidate(Json(vrm1.Replace("onlyAuthor", "everyone")), ownerId: null)!.Usable);
    }

    [Fact]
    public void HeartsAndSearchesListTheirModels()
    {
        var hearts = Json("{\"data\":[{\"character_model\":" + Vrm0Model + "}]}");
        var search = Json("{\"data\":[" + Vrm0Model + "]}");
        Assert.Single(VRoidHub.Models(hearts, null));
        Assert.Single(VRoidHub.Models(search, null));
    }

    [Fact]
    public void OpenSourceAvatarsEntryBecomesAKeptLookAndIsFoundByItsWords()
    {
        var avatar = Json("""
            {"id":"27ccb24c","name":"Devil","project_id":"100avatars-r1","description":"Avatar 031: Devil",
             "model_file_url":"https://arweave.net/gfVz","format":"VRM","is_public":true,"is_draft":false,
             "metadata":{"series":"R1"},"thumbnail_url":"https://arweave.net/llxw"}
            """);
        var cc0 = OpenSourceAvatars.Candidate(avatar, "100avatars-r1", "100Avatars R1", "CC0", "Polygonal-Mind")!;

        Assert.Equal("osa:100avatars-r1/27ccb24c", cc0.Look);
        Assert.True(cc0.Kept);
        Assert.False(cc0.CreditRequired);
        Assert.Equal("CC0", cc0.License);
        Assert.Equal("Polygonal-Mind", cc0.Author);
        Assert.True(OpenSourceAvatars.Score(cc0, OpenSourceAvatars.Words("devil")) > 0);
        Assert.Equal(0, OpenSourceAvatars.Score(cc0, OpenSourceAvatars.Words("devil angel")));
        var ccBy = OpenSourceAvatars.Candidate(avatar, "vipe", "VIPE Heroes", "CC-BY", "VIPE")!;
        Assert.True(ccBy.CreditRequired);
        Assert.Contains("Credit: required (VIPE).", ccBy.Conditions);
        Assert.False(OpenSourceAvatars.IsFreeLicense("All rights reserved"));
        Assert.Null(OpenSourceAvatars.Candidate(Json("{\"id\":\"x\",\"name\":\"X\",\"model_file_url\":\"http://insecure/x.vrm\"}"), "p", "P", "CC0", null));
    }

    [Fact]
    public async Task VRoidHubLookIsHeldInMemoryAndReadLikeAModelFile()
    {
        var path = RemoteCharacterModels.VRoidHubPath("4242");
        var bytes = Encoding.ASCII.GetBytes("glTF-test-model");
        var loads = 0;
        var before = RemoteCharacterModels.Loader;
        RemoteCharacterModels.Forget(path);
        RemoteCharacterModels.Loader = (wanted, _) => { loads++; Assert.Equal(path, wanted); return Task.FromResult(bytes); };
        try
        {
            var assets = await LocalAvatarFiles.ReadModelAsync(AvatarRenderer.Vrm, path, default);
            Assert.Equal("vroidhub-4242.vrm", Assert.Single(assets).Name);
            Assert.Equal(bytes, assets[0].Bytes);
            await LocalAvatarFiles.ReadModelAsync(AvatarRenderer.Vrm, path, default);
            Assert.Equal(1, loads);
            Assert.True(RemoteCharacterModels.Has(path));
            var profile = AvatarProfile.BuiltIn(Guid.NewGuid()) with { Renderer = AvatarRenderer.Vrm, ModelPath = path };
            profile.Validate();
            var snapshot = await LocalAvatarFiles.SnapshotAsync(profile, default);
            Assert.Equal("model.vrm", snapshot.ModelFile);
            await Assert.ThrowsAsync<ContractException>(() => LocalAvatarFiles.ReadModelAsync(AvatarRenderer.Live2D, path, default));
            Assert.Throws<ContractException>(() => (profile with { Renderer = AvatarRenderer.Live2D }).Validate());
            Assert.Throws<ContractException>(() => (profile with { ModelPath = "vroidhub:12a" }).Validate());
        }
        finally
        {
            RemoteCharacterModels.Loader = before;
            RemoteCharacterModels.Forget(path);
        }
    }

    [Fact]
    public async Task VRoidHubLookCantBeReadWithoutALoader()
    {
        var path = RemoteCharacterModels.VRoidHubPath("4343");
        var before = RemoteCharacterModels.Loader;
        RemoteCharacterModels.Forget(path);
        RemoteCharacterModels.Loader = null;
        try { await Assert.ThrowsAsync<ContractException>(() => RemoteCharacterModels.ReadAsync(path, default)); }
        finally { RemoteCharacterModels.Loader = before; }
    }

    [Fact]
    public async Task AFailedVRoidHubDownloadIsTriedAgainAndAVRoidHubLookNeverJoinsYourCharacters()
    {
        var path = RemoteCharacterModels.VRoidHubPath("4545");
        var before = RemoteCharacterModels.Loader;
        RemoteCharacterModels.Forget(path);
        var linked = false;
        RemoteCharacterModels.Loader = (_, _) => linked ? Task.FromResult(new byte[] { 1, 2, 3 })
            : throw new ContractException(ErrorCode.InvalidContract, "Link your VRoid Hub account.");
        var folder = Directory.CreateTempSubdirectory("martlet-finder-").FullName;
        try
        {
            await Assert.ThrowsAsync<ContractException>(() => RemoteCharacterModels.ReadAsync(path, default));
            linked = true;
            Assert.Equal([1, 2, 3], await RemoteCharacterModels.ReadAsync(path, default));
            // The model is in memory now, and still can't be kept with your characters.
            var refused = await Assert.ThrowsAsync<ContractException>(() =>
                SharedCharacterModels.ImportAsync(folder, AvatarRenderer.Vrm, path, "Miku", "test-pc", DateTimeOffset.UtcNow, default));
            Assert.Contains("VRoid Hub", refused.Message);
            Assert.False(Directory.Exists(SharedCharacterModels.Root(folder)));
        }
        finally
        {
            RemoteCharacterModels.Loader = before;
            RemoteCharacterModels.Forget(path);
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ModelPiecesRebuildTheModelInOrderOnly()
    {
        var path = RemoteCharacterModels.VRoidHubPath("77");
        var bytes = new byte[RendererModelPiece.PieceBytes * 2 + 123];
        new Random(5).NextBytes(bytes);
        var pieces = RendererModelPiece.Split(path, bytes).ToArray();
        Assert.Equal(3, pieces.Length);
        // Each piece fits a renderer message once encoded.
        Assert.All(pieces, p => Assert.True(JsonSerializer.SerializeToUtf8Bytes(p).Length < RendererProtocol.MaximumMessageBytes));
        byte[]? rebuilt = null;
        for (var i = 0; i < pieces.Length; i++) rebuilt = pieces[i].AddTo(rebuilt, i);
        Assert.Equal(bytes, rebuilt);
        Assert.Throws<InvalidDataException>(() => pieces[1].AddTo(null, 0));
        Assert.Throws<InvalidDataException>(() => (pieces[0] with { Data = "not base64!" }).AddTo(null, 0));
        Assert.Throws<ContractException>(() => (pieces[0] with { Path = "C:\\model.vrm" }).AddTo(null, 0));
    }

    [Fact]
    public void Vrm0ModelsEmotesAreNamedAsTheRendererNamesThem()
    {
        var glb = CharacterGestureTests.Glb("""
            {"asset":{"version":"2.0"},"meshes":[{"primitives":[{"extras":{"targetNames":["Fcl_ALL_Joy","Fcl_ALL_Angry","Fcl_MTH_A"]}}]}],
             "extensions":{"VRM":{"humanoid":{"humanBones":[{"bone":"hips","node":0},{"bone":"head","node":1},{"bone":"rightUpperArm","node":2}]},
             "blendShapeMaster":{"blendShapeGroups":[
               {"name":"Joy","presetName":"joy","binds":[{"mesh":0,"index":0,"weight":100}]},
               {"name":"A","presetName":"a","binds":[{"mesh":0,"index":2,"weight":100}]},
               {"name":"Sorrow","presetName":"sorrow"},
               {"name":"Surprised","presetName":"unknown","binds":[{"mesh":0,"index":1,"weight":100}]}]}}}}
            """);
        var inventory = CharacterActionInventory.From(AvatarRenderer.Vrm, "m.vrm", [new("m.vrm", glb, "model/gltf-binary")]);

        var expressions = inventory.Sources.Where(s => s.Kind == CharacterActionKind.Expression).Select(s => s.Name).ToArray();
        Assert.Equal(["happy", "sad", "Surprised"], expressions);
        Assert.Contains("Fcl_ALL_Joy", inventory.Find("expression:happy")!.Detail);
        Assert.Contains("custom expression", inventory.Find("expression:Surprised")!.Detail);
        // VRM 0.x humanoid bones give the body gestures, as VRM 1.0's do.
        Assert.Contains(inventory.Sources, s => s.Id == "gesture:nod");
        Assert.Equal("relaxed", CharacterActionInventory.Vrm0Preset("fun"));
        Assert.Null(CharacterActionInventory.Vrm0Preset("unknown"));
    }

    [Fact]
    public void PersonalityFromTheThinkingModelLosesFencesQuotesAndThinking()
    {
        Assert.Equal("You are Rem.", MainWindow.CleanPersonality("<think>hmm</think>\n```text\nYou are Rem.\n```"));
        Assert.Equal("You are Rem.", MainWindow.CleanPersonality("\"You are Rem.\""));
        Assert.Null(MainWindow.CleanPersonality("  "));
        Assert.Equal("Hatsune Miku.vrm", MainWindow.ModelFileName("Hatsune Miku"));
        Assert.Equal("Rem Re Zero.vrm", MainWindow.ModelFileName("Rem / Re:Zero"));
        Assert.Equal("Character.vrm", MainWindow.ModelFileName("???"));
    }
}
