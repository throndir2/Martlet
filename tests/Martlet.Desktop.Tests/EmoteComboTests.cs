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
            var merged = CharacterActions.Merge(inventory, old);
            Assert.Null(merged.Combos);
            Assert.Empty(new CharacterActionCatalog(inventory, merged).Combos);
            await CharacterActions.SaveAsync(directory, merged, DateTimeOffset.Now);
            Assert.DoesNotContain("combos", File.ReadAllText(CharacterActions.Path(directory)));

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

        // Try (Companion › Character › Combos) plays them the same way, for a try.
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
