using System.Text;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Conversation;

namespace Martlet.Desktop.Tests;

public sealed class EmoteComboTests
{
    private static readonly byte[] Expression = Encoding.UTF8.GetBytes(
        "{\"Type\":\"Live2D Expression\",\"Parameters\":[{\"Id\":\"ParamCheek\",\"Value\":1}]}");

    // A Live2D model with a smile (brief), glasses (lingering: a look that stays) and a wave motion (brief), plus the gestures
    // every Live2D model gets (hearts lingers).
    private static CharacterActionInventory Inventory()
    {
        var model = Encoding.UTF8.GetBytes("{\"Version\":3,\"FileReferences\":{\"Moc\":\"m.moc3\",\"Textures\":[]," +
            "\"Expressions\":[{\"Name\":\"Smile\",\"File\":\"smile.exp3.json\"},{\"Name\":\"Glasses\",\"File\":\"glasses.exp3.json\"}]," +
            "\"Motions\":{\"Wave\":[{\"File\":\"wave.motion3.json\"}]}}}");
        var motion = Encoding.UTF8.GetBytes("{\"Version\":3,\"Meta\":{\"Duration\":1},\"Curves\":[]}");
        return CharacterActionInventory.From(AvatarRenderer.Live2D, "m.model3.json",
        [
            new("m.model3.json", model, "application/json"), new("m.moc3", new byte[64], "application/octet-stream"),
            new("smile.exp3.json", Expression, "application/json"), new("glasses.exp3.json", Expression, "application/json"),
            new("wave.motion3.json", motion, "application/json")
        ]);
    }

    private static readonly CharacterCombo Flustered = new() { Tag = "flustered", Parts = ["expression:Smile", "expression:Glasses", "motion:Wave"] };
    private static readonly CharacterCombo Agree = new() { Tag = "agree", Parts = ["expression:Smile", "motion:Wave"], Use = "when you agree warmly" };

    // The tags of all Martlet's own combos, as a model given them lists them.
    private static readonly string[] Given = [.. CharacterActions.MartletCombos.Select(c => c.Tag)];

    // A Live2D model with every parameter Martlet's gestures move, its own heart eyes (爱心眼) and an expression named Shocked.
    private static CharacterActionInventory Rigged()
    {
        var model = Encoding.UTF8.GetBytes("{\"Version\":3,\"FileReferences\":{\"Moc\":\"m.moc3\",\"Textures\":[]," +
            "\"Expressions\":[{\"Name\":\"爱心眼\",\"File\":\"a.exp3.json\"},{\"Name\":\"Shocked\",\"File\":\"b.exp3.json\"}]}}");
        var moc = CharacterGestureTests.Moc("ParamAngleX", "ParamAngleY", "ParamAngleZ", "ParamBodyAngleZ", "ParamEyeLSmile", "ParamEyeRSmile",
            "ParamBrowLY", "ParamBrowRY", "ParamEyeLOpen", "ParamEyeROpen", "ParamEyeBallX", "ParamEyeBallY", "ParamMouthForm", "ParamMouthOpenY");
        return CharacterActionInventory.From(AvatarRenderer.Live2D, "m.model3.json",
        [
            new("m.model3.json", model, "application/json"), new("m.moc3", moc, "application/octet-stream"),
            new("a.exp3.json", Expression, "application/json"), new("b.exp3.json", Expression, "application/json")
        ]);
    }

    private static CharacterActionCatalog Catalog(params CharacterCombo[] combos)
    {
        var inventory = Inventory();
        return new(inventory, CharacterActions.Merge(inventory, null) with { Combos = combos.Length == 0 ? null : combos });
    }

    private static CharacterActionCatalog Without(CharacterActionCatalog catalog, params string[] off) => catalog with
    {
        Settings = catalog.Settings with { Actions = [.. catalog.Settings.Actions.Select(a => off.Contains(a.Id) ? a with { Enabled = false } : a)] }
    };

    private static CharacterActionSource Source(string id) => Inventory().Find(id)!;

    [Fact]
    public async Task Combos_save_with_the_model_and_a_file_without_them_reads_and_writes_as_before()
    {
        var directory = Directory.CreateTempSubdirectory("martlet-combos-").FullName;
        var other = Directory.CreateTempSubdirectory("martlet-combos-other-").FullName;
        try
        {
            var inventory = Inventory();
            var id = inventory.ModelId;
            File.WriteAllText(CharacterActions.Path(directory), "{\"version\":1,\"models\":[{\"model_id\":\"" + id + "\",\"detected_by\":\"names\"," +
                "\"updated_at\":\"2026-01-01T00:00:00+00:00\",\"actions\":[{\"id\":\"expression:Glasses\",\"tag\":\"glasses\",\"enabled\":true}]}]}");
            var old = CharacterActions.Load(directory, id)!;
            Assert.Null(old.Combos);
            Assert.Null(old.GivenCombos);
            // A file from before Martlet's combos gets them once (see Each_model_gets_Martlet_combos_once...).
            var merged = CharacterActions.Merge(inventory, old);
            Assert.Equal(Given, merged.GivenCombos);
            Assert.NotEmpty(new CharacterActionCatalog(inventory, merged).Combos);
            // Settings with no combos still write none.
            await CharacterActions.SaveAsync(directory, merged with { Combos = null }, DateTimeOffset.Now);
            Assert.DoesNotContain("\"combos\"", File.ReadAllText(CharacterActions.Path(directory)));
            Assert.Null(CharacterActions.Load(directory, id)!.Combos);

            await CharacterActions.SaveAsync(directory, merged with { Combos = [Flustered, Agree with { Enabled = false }] }, DateTimeOffset.Now);
            var text = File.ReadAllText(CharacterActions.Path(directory));
            Assert.Contains("\"combos\": [", text);
            Assert.Contains("\"tag\": \"flustered\"", text);
            var loaded = new CharacterActionCatalog(inventory, CharacterActions.Merge(inventory, CharacterActions.Load(directory, id)));
            Assert.Equal(new[] { "flustered", "agree" }, loaded.Combos.Select(c => c.Tag));
            Assert.Equal(Flustered.Parts, loaded.Combos[0].Parts);
            Assert.True(loaded.Combos[0].Enabled);
            Assert.Null(loaded.Combos[0].Use);
            Assert.False(loaded.Combos[1].Enabled);
            Assert.Equal("when you agree warmly", loaded.Combos[1].Use);

            // They travel with the shared emote settings to the owner's other computers.
            await CharacterActions.ReplaceAllAsync(other, CharacterActions.Share(directory));
            Assert.Equal(new[] { "flustered", "agree" }, CharacterActions.Load(other, id)!.Combos!.Select(c => c.Tag));
        }
        finally
        {
            Directory.Delete(directory, true);
            Directory.Delete(other, true);
        }
    }

    [Fact]
    public void Problem_checks_each_combo_tag_and_its_parts()
    {
        var settings = Catalog().Settings;
        string? Problem(params CharacterCombo[] combos) => CharacterActions.Problem(settings with { Combos = combos });
        Assert.Null(Problem(Flustered, Agree));
        Assert.Null(Problem());
        Assert.Contains("can't be a combo's tag", Problem(Flustered with { Tag = "Flustered!" }));
        Assert.Equal("The combo {smile} has the tag of an emote or motion. Give it a tag of its own.", Problem(Flustered with { Tag = "smile" }));
        Assert.Contains("has the tag of an emote or motion", Problem(Flustered with { Tag = "hearts" }));
        Assert.Equal("Two combos use the tag {agree}.", Problem(Agree, Flustered with { Tag = "agree" }));
        Assert.Equal("The combo {flustered} needs 2 to 6 parts.", Problem(Flustered with { Parts = ["expression:Smile"] }));
        Assert.Equal("The combo {flustered} needs 2 to 6 parts.", Problem(Flustered with { Parts = [.. settings.Actions.Take(7).Select(a => a.Id)] }));
        Assert.Equal("The combo {flustered} has a part twice.", Problem(Flustered with { Parts = ["expression:Smile", "expression:Smile"] }));
        Assert.Equal("The combo {flustered} has a part this model doesn't have (gesture:moonwalk).",
            Problem(Flustered with { Parts = ["expression:Smile", "gesture:moonwalk"] }));
        Assert.Contains("When to use", Problem(Flustered with { Use = new string('a', CharacterActionCatalog.MaximumUseLength + 1) }));
        Assert.Contains("When to use", Problem(Flustered with { Use = "one\ntwo" }));
        Assert.Equal("A combo needs a tag and its parts.", Problem(Flustered with { Parts = null! }));
        Assert.Equal($"A model can have at most {CharacterActions.MaximumCombos} combos.",
            Problem([.. Enumerable.Range(0, CharacterActions.MaximumCombos + 1).Select(i => Flustered with { Tag = $"combo_{i}" })]));
        Assert.Null(CharacterActions.Problem(settings with { GivenCombos = Given }));
        Assert.Equal("The list of Martlet's combos given to this model is damaged.", CharacterActions.Problem(settings with { GivenCombos = ["Bad Tag"] }));
    }

    [Fact]
    public void The_parts_box_names_emotes_by_their_tags()
    {
        var actions = Catalog().Settings.Actions;
        Assert.Equal(Flustered.Parts, CharacterActions.ParseParts("smile glasses wave", actions, out var problem));
        Assert.Null(problem);
        // Braces, commas and +, any case; each part once.
        Assert.Equal(new[] { "gesture:hearts", "expression:Smile" }, CharacterActions.ParseParts(" {Hearts}, smile + hearts ", actions, out problem));
        Assert.Null(CharacterActions.ParseParts("smile moonwalk", actions, out problem));
        Assert.Equal("no emote has the tag 'moonwalk'.", problem);
        Assert.Empty(CharacterActions.ParseParts("  ", actions, out problem)!);
        Assert.Null(problem);
        Assert.Equal("smile glasses wave", CharacterActions.PartsText(Flustered.Parts, actions));
        // A part whose emote lost its tag shows, and reads back, as its ID.
        var untagged = actions.Select(a => a.Id == "motion:Wave" ? a with { Tag = null } : a).ToArray();
        Assert.Equal("smile glasses motion:Wave", CharacterActions.PartsText(Flustered.Parts, untagged));
        Assert.Equal(Flustered.Parts, CharacterActions.ParseParts("smile glasses motion:Wave", untagged, out _));
    }

    [Fact]
    public void Merge_keeps_the_combos_a_model_can_play_and_no_emote_takes_a_combo_tag()
    {
        var inventory = Inventory();
        var defaults = CharacterActions.Merge(inventory, null);
        var saved = defaults with
        {
            // A sync from another computer named the smile like a combo here.
            Actions = [.. defaults.Actions.Select(a => a.Id == "expression:Smile" ? a with { Tag = "flustered" } : a)],
            Combos =
            [
                Flustered,
                // A gesture of a newer Martlet: the parts this model has stay.
                Agree with { Parts = ["expression:Smile", "gesture:moonwalk", "motion:Wave"] },
                new() { Tag = "lonely", Parts = ["expression:Smile", "gesture:moonwalk"] },
                new() { Tag = "Bad Tag", Parts = ["expression:Smile", "motion:Wave"] },
                Flustered with { Parts = ["motion:Wave", "expression:Glasses"] }
            ]
        };
        var merged = CharacterActions.Merge(inventory, saved);
        Assert.Equal(new[] { "flustered", "agree" }, merged.Combos!.Select(c => c.Tag));
        Assert.Equal(new[] { "expression:Smile", "motion:Wave" }, merged.Combos![1].Parts);
        Assert.Equal("flustered_2", merged.Find("expression:Smile")!.Tag);
        Assert.Null(CharacterActions.Problem(merged));
        // The Thinking model's naming keeps them.
        var named = CharacterActions.Parse("1: grin | - | when happy", inventory, merged, DateTimeOffset.Now)!;
        Assert.Equal("grin", named.Find("expression:Smile")!.Tag);
        Assert.Equal(new[] { "flustered", "agree" }, named.Combos!.Select(c => c.Tag));
    }

    [Fact]
    public async Task Each_model_gets_Martlet_combos_once_with_the_parts_it_can_play()
    {
        // Martlet's combos are valid combos of its gestures' tags, and no gesture has a combo's tag.
        var gestureTags = CharacterActionInventory.AllGestures.Select(g => g.Tag).ToHashSet();
        Assert.All(CharacterActions.MartletCombos, c =>
        {
            Assert.True(CharacterActions.IsTag(c.Tag) && !gestureTags.Contains(c.Tag), c.Tag);
            Assert.InRange(c.Parts.Count, CharacterActions.MinimumComboParts, CharacterActions.MaximumComboParts);
            Assert.Equal(c.Parts.Count, c.Parts.Distinct().Count());
            Assert.All(c.Parts, part => Assert.Contains(part, gestureTags));
            Assert.InRange(c.Use.Length, 1, CharacterActionCatalog.MaximumUseLength);
        });
        Assert.Equal(Given.Length, Given.Distinct().Count());

        var inventory = Rigged();
        var merged = CharacterActions.Merge(inventory, null);
        // Every one but {shocked}: the model's own emote has that tag, and keeps it.
        Assert.Equal(["lovestruck", "flustered", "overheated", "fuming", "heartbroken", "dozing", "starstruck", "ahegao"],
            merged.Combos!.Select(c => c.Tag));
        Assert.Equal("shocked", merged.Find("expression:Shocked")!.Tag);
        Assert.Equal(Given, merged.GivenCombos);
        Assert.Null(CharacterActions.Problem(merged));
        // The model's own heart eyes stand in for Martlet's; ahegao starts turned off.
        var lovestruck = merged.Combos![0];
        Assert.Equal(["expression:爱心眼", "gesture:hearts", "gesture:blush_deep", "gesture:sway"], lovestruck.Parts);
        Assert.Equal(CharacterActions.MartletCombos[0].Use, lovestruck.Use);
        Assert.True(lovestruck.Enabled);
        var ahegao = merged.Combos![^1];
        Assert.Equal(["gesture:eyes_up", "gesture:mouth_open", "gesture:tongue_out", "gesture:drool", "gesture:blush_fierce", "expression:爱心眼"],
            ahegao.Parts);
        Assert.False(ahegao.Enabled);
        Assert.All(merged.Combos!.SkipLast(1), c => Assert.True(c.Enabled, c.Tag));

        // Replies get those that are on, after the emotes' lines; {ahegao} once the owner turns it on.
        var catalog = new CharacterActionCatalog(inventory, merged);
        var prompt = catalog.Prompt(null, null)!;
        Assert.Contains("\n{lovestruck} - heart eyes, hearts and a deep blush, for being smitten or madly in love (stays on until you write " +
            "{/lovestruck})\n", prompt.Instructions);
        Assert.True(prompt.Instructions.IndexOf("\n{lovestruck} - ", StringComparison.Ordinal) >
            prompt.Instructions.IndexOf("\n{ellipsis} - ", StringComparison.Ordinal), "the combos come after the emotes");
        Assert.Contains("{/lovestruck}", prompt.Tags);
        Assert.DoesNotContain("{ahegao}", prompt.Tags);
        Assert.DoesNotContain("{ahegao}", prompt.Instructions);
        var on = catalog with { Settings = merged with { Combos = [.. merged.Combos!.Select(c => c with { Enabled = true })] } };
        Assert.Contains("\n{ahegao} - eyes rolled up, tongue out and flushed, for being overwhelmed with pleasure (stays on until you write {/ahegao})",
            on.Prompt(null, null)!.Instructions);
        Assert.Equal(ahegao.Parts, on.For("{ahegao}").Select(s => s.Id));
        // The model's own heart eyes are brief, so the off tag turns off the other five.
        Assert.Equal(ahegao.Parts.SkipLast(1), on.Off("{/ahegao}").Select(s => s.Id));

        // A combo the owner removed or renamed never comes back, even with no combos left.
        var directory = Directory.CreateTempSubdirectory("martlet-combos-given-").FullName;
        try
        {
            await CharacterActions.SaveAsync(directory, merged with { Combos = [lovestruck with { Tag = "smitten" }, .. merged.Combos!.Skip(1).SkipLast(1)] },
                DateTimeOffset.Now);
            Assert.Contains("\"given_combos\": [", File.ReadAllText(CharacterActions.Path(directory)));
            var again = CharacterActions.Merge(inventory, CharacterActions.Load(directory, inventory.ModelId));
            Assert.Equal(["smitten", "flustered", "overheated", "fuming", "heartbroken", "dozing", "starstruck"], again.Combos!.Select(c => c.Tag));
            await CharacterActions.SaveAsync(directory, again with { Combos = null }, DateTimeOffset.Now);
            Assert.Null(CharacterActions.Merge(inventory, CharacterActions.Load(directory, inventory.ModelId)).Combos);
        }
        finally { Directory.Delete(directory, true); }

        // A file from before them keeps the owner's combos first; Martlet's come after, except one whose tag the owner's has.
        var owner = new CharacterCombo { Tag = "flustered", Parts = ["gesture:blush", "gesture:nod"] };
        var upgraded = CharacterActions.Merge(inventory, merged with { Combos = [owner], GivenCombos = null });
        Assert.Equal(["flustered", "lovestruck", "overheated", "fuming", "heartbroken", "dozing", "starstruck", "ahegao"],
            upgraded.Combos!.Select(c => c.Tag));
        Assert.Equal(owner.Parts, upgraded.Combos![0].Parts);
        // A Martlet with more combos gives a model only the new ones, and keeps the tags of combos it doesn't know.
        var newer = CharacterActions.Merge(inventory, merged with { Combos = null, GivenCombos = ["lovestruck", "flustered", "newer_combo"] });
        Assert.Equal(["overheated", "fuming", "heartbroken", "dozing", "starstruck", "ahegao"], newer.Combos!.Select(c => c.Tag));
        Assert.Equal(["lovestruck", "flustered", "newer_combo", .. Given.Except(["lovestruck", "flustered"])], newer.GivenCombos);
        // Use the model's own names keeps what was given.
        var reset = CharacterActions.Merge(inventory, new CharacterActionSettings
        {
            ModelId = inventory.ModelId, Actions = [], Combos = merged.Combos, GivenCombos = merged.GivenCombos
        });
        Assert.Equal(merged.Combos!.Select(c => c.Tag), reset.Combos!.Select(c => c.Tag));

        // A rig that moves nothing still gets the combos of what Martlet draws over it, but no {shocked} (only its exclamation
        // mark would show); it is given all the same.
        var bare = CharacterActions.Merge(Inventory(), null);
        Assert.Equal(["lovestruck", "flustered", "overheated", "fuming", "heartbroken", "dozing", "starstruck", "ahegao"], bare.Combos!.Select(c => c.Tag));
        Assert.Equal(["gesture:blush_deep", "gesture:sweat"], bare.Combos![1].Parts);
        Assert.Equal(Given, bare.GivenCombos);

        // A VRM uses its own surprised expression, as Martlet's surprise is Live2D's only; a combo of brief parts has no off tag.
        var vrm = CharacterActionInventory.From(AvatarRenderer.Vrm, "m.vrm", [new("m.vrm", CharacterGestureTests.Glb(
            "{\"asset\":{\"version\":\"2.0\"},\"extensions\":{\"VRMC_vrm\":{\"specVersion\":\"1.0\",\"humanoid\":{\"humanBones\":{" +
            "\"head\":{\"node\":0},\"spine\":{\"node\":1}}},\"expressions\":{\"preset\":{\"surprised\":{}}}}}}"), "model/gltf-binary")]);
        var vrmCatalog = new CharacterActionCatalog(vrm, CharacterActions.Merge(vrm, null));
        Assert.Equal(["gesture:exclaim", "gesture:gasp", "expression:surprised"], Assert.Single(vrmCatalog.Combos, c => c.Tag == "shocked").Parts);
        var vrmPrompt = vrmCatalog.Prompt(null, null)!;
        Assert.Contains("\n{shocked} - an exclamation mark, a gasp and wide eyes, for a big shock\n", vrmPrompt.Instructions);
        Assert.Contains("{shocked}", vrmPrompt.Tags);
        Assert.DoesNotContain("{/shocked}", vrmPrompt.Tags);
    }

    [Fact]
    public void Combos_are_offered_after_the_emotes_so_the_earlier_lines_stay_the_same()
    {
        var plain = Catalog().Prompt(null, null)!;
        var empty = (Catalog() with { Settings = Catalog().Settings with { Combos = [] } }).Prompt(null, null)!;
        Assert.Equal(plain.Instructions, empty.Instructions);
        Assert.Equal(plain.Tags, empty.Tags);
        var prompt = Catalog(Flustered, Agree).Prompt(null, null)!;
        const string lines = "{flustered} - a combination of {smile}, {glasses} and {wave} (stays on until you write {/flustered})\n" +
            "{agree} - when you agree warmly";
        Assert.Equal(plain.Instructions.Replace("\nWrite a tag exactly", "\n" + lines + "\nWrite a tag exactly", StringComparison.Ordinal),
            prompt.Instructions);
        // The emotes' tags, the combos', the emotes' off tags, then the off tags of combos with a lingering part.
        string[] expected = [.. plain.Tags.Where(t => !t.StartsWith("{/", StringComparison.Ordinal)), "{flustered}", "{agree}",
            .. plain.Tags.Where(t => t.StartsWith("{/", StringComparison.Ordinal)), "{/flustered}"];
        Assert.Equal(expected, prompt.Tags);

        // A part turned off leaves the default hint; a combo turned off, or with no part on, isn't offered.
        var fewer = Without(Catalog(Flustered, Agree with { Enabled = false }), "expression:Smile", "motion:Wave").Prompt(null, null)!;
        Assert.Contains("\n{flustered} - the same as {glasses} (stays on until you write {/flustered})\n", fewer.Instructions);
        Assert.DoesNotContain("{agree}", fewer.Instructions);
        Assert.DoesNotContain("{agree}", fewer.Tags);
        var none = Without(Catalog(Flustered), [.. Flustered.Parts]).Prompt(null, null)!;
        Assert.DoesNotContain("{flustered}", none.Tags);
        Assert.DoesNotContain("{flustered}", none.Instructions);

        // Only the combos whose tags fit beside the emotes' and the look tags are offered (each of these takes two: its tag and
        // its off tag), so the gaze still joins in.
        var crowded = Catalog([.. Enumerable.Range(0, 100).Select(i => Flustered with { Tag = $"combo_{i}" })]).Prompt(null, null)!;
        var looks = CharacterGaze.ModeTags.Count + CharacterGaze.Tags.Count;
        Assert.InRange(crowded.Tags.Count, CharacterActionCatalog.MaximumTags - looks - 1, CharacterActionCatalog.MaximumTags - looks);
        Assert.Contains("{look usual}", CharacterGaze.Join(crowded, CharacterGaze.ReplyPrompt(null, GazeMode.Mouse), null)!.Tags);
        var offered = crowded.Tags.Where(t => t.StartsWith("{combo_", StringComparison.Ordinal)).ToArray();
        Assert.Equal(crowded.Tags.Count(t => t.StartsWith("{/combo_", StringComparison.Ordinal)), offered.Length);
        Assert.All(offered, tag => Assert.Contains("\n" + tag + " - ", crowded.Instructions));
        Assert.DoesNotContain("{combo_99}", crowded.Instructions);
    }

    [Fact]
    public void A_combo_tag_sets_off_its_parts_that_are_on_and_its_off_tag_turns_off_the_lingering_ones()
    {
        var catalog = Catalog(Flustered, Agree);
        Assert.Equal(Flustered.Parts, catalog.For("{flustered}").Select(s => s.Id));
        Assert.Equal(Flustered.Parts, catalog.For("{Flustered}").Select(s => s.Id));
        Assert.Equal("expression:Glasses", Assert.Single(catalog.Off("{/flustered}")).Id);
        // Nothing of {agree} lingers, so its off tag turns nothing off and never plays it.
        Assert.Empty(catalog.Off("{/agree}"));
        Assert.Empty(catalog.For("{/agree}"));
        Assert.Empty(catalog.For("{/flustered}"));
        Assert.Equal("flustered", catalog.Combo("{flustered}")!.Tag);
        Assert.Equal("flustered", catalog.Combo("{/flustered}")!.Tag);
        Assert.Null(catalog.Combo("{glasses}"));
        Assert.Null(catalog.Combo("[laugh]"));
        // An emote's own tags still mean the emote.
        Assert.Equal("expression:Glasses", Assert.Single(catalog.Off("{/glasses}")).Id);
        Assert.Equal("expression:Glasses", Assert.Single(catalog.For("{glasses}")).Id);

        // A part turned off is skipped; a combo turned off sets off nothing.
        Assert.Equal(new[] { "expression:Smile", "expression:Glasses" }, Without(catalog, "motion:Wave").For("{flustered}").Select(s => s.Id));
        Assert.Empty(Without(catalog, "expression:Glasses").Off("{/flustered}"));
        var off = Catalog(Flustered with { Enabled = false });
        Assert.Empty(off.For("{flustered}"));
        Assert.Empty(off.Off("{/flustered}"));
        Assert.Null(off.Combo("{flustered}"));
    }

    private sealed class Renderer : IAvatarRenderer
    {
        internal System.Collections.Concurrent.ConcurrentQueue<RendererAction> Actions { get; } = new();
        public RendererCapabilities? Capabilities { get; private set; }
        public bool HasExited { get; private set; }
        public Task Exited => Task.CompletedTask;
        public event Action<string>? Requested { add { } remove { } }
        private readonly Guid activation = Guid.NewGuid();
        public Task StartAsync(AvatarProfile profile, string revision, RendererPlacement? placement, bool voiceMuted, CancellationToken token)
        {
            HasExited = false;
            Capabilities = new(revision.ToLowerInvariant(), [new("Jaw", -10, 10, 0, ["Mouth"])]);
            return Task.CompletedTask;
        }
        public Task<RendererMessage> SendAsync<T>(string kind, T data, CancellationToken token, TimeSpan? timeout = null)
        {
            if (data is RendererAction action) Actions.Enqueue(action);
            return Task.FromResult(RendererProtocol.Message("ok", activation, new { started = true }));
        }
        public ValueTask DisposeAsync() { HasExited = true; return ValueTask.CompletedTask; }
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    [Fact]
    public async Task A_reply_combo_plays_every_part_and_its_off_tag_turns_off_the_lingering_ones()
    {
        using var scope = new AvatarHostingTests.Scope();
        var renderer = new Renderer();
        await using var avatar = new AvatarController(createRenderer: () => renderer, allowControlledClock: true);
        var catalog = Catalog(Flustered, Agree);
        avatar.UseActions(_ => catalog);
        await avatar.ShowAsync(scope.Profile() with { LipSync = AvatarLipSync.Loudness }, default);

        avatar.Cues.Post([new CharacterCue("{flustered}", TimeSpan.Zero)], Task.CompletedTask);
        await Until(() => avatar.LastAction?.StartsWith("Combo {flustered}", StringComparison.Ordinal) == true);
        Assert.Equal(new[] { new RendererAction("expression", "Smile"), new RendererAction("expression", "Glasses", true, true), new RendererAction("motion", "Wave") },
            renderer.Actions.Take(3));
        Assert.True(avatar.Held.Holds("expression:Glasses"));
        Assert.Matches("^Combo \\{flustered\\} for a reply at .+: turned on \"Glasses\"; played \"Smile\" and \"Wave\"\\.$", avatar.LastAction);

        // Again: the glasses already show and stay as they are.
        renderer.Actions.Clear();
        avatar.Cues.Post([new CharacterCue("{flustered}", TimeSpan.Zero)], Task.CompletedTask);
        await Until(() => avatar.LastAction?.Contains("kept", StringComparison.Ordinal) == true);
        Assert.Contains("kept \"Glasses\" on; played \"Smile\" and \"Wave\".", avatar.LastAction);
        Assert.DoesNotContain(renderer.Actions, a => a.Name == "Glasses");

        avatar.Cues.Post([new CharacterCue("{/flustered}", TimeSpan.Zero)], Task.CompletedTask);
        await Until(() => avatar.LastAction?.StartsWith("Combo {/flustered}", StringComparison.Ordinal) == true);
        Assert.False(avatar.Held.Holds("expression:Glasses"));
        Assert.Contains(new RendererAction("expression", "Glasses", false, true), renderer.Actions);
        Assert.EndsWith(": turned off \"Glasses\".", avatar.LastAction);

        // Try (Companion › Emotes and motions › Combos) plays them the same way, for a try.
        Assert.Equal(2, await avatar.PlayComboAsync("agree", [(Source("expression:Smile"), false), (Source("motion:Wave"), false)], "a try",
            null, default));
        Assert.StartsWith("Combo {agree} for a try at ", avatar.LastAction);
        Assert.EndsWith(": played \"Smile\" and \"Wave\".", avatar.LastAction);
        Assert.Equal(1, await avatar.PlayComboAsync("flustered", [(Source("expression:Glasses"), true)], "a try", null, default));
        Assert.Equal(1, await avatar.StopComboAsync("flustered", [Source("expression:Glasses")], "a try", default));
        Assert.Equal(0, await avatar.StopComboAsync("flustered", [Source("expression:Glasses")], "a try", default));

        // Hidden, nothing plays.
        await avatar.StopAsync();
        Assert.Equal(0, await avatar.PlayComboAsync("agree", [(Source("expression:Smile"), false)], "a try", null, default));
    }
}
