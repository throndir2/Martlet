using System.Text;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Conversation;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop.Tests;

public sealed class LingeringEmoteTests
{
    private static readonly byte[] Expression = Encoding.UTF8.GetBytes(
        "{\"Type\":\"Live2D Expression\",\"Parameters\":[{\"Id\":\"ParamCheek\",\"Value\":1}]}");

    // A Live2D model with a smile, glasses and a VTube Studio toggle named "Star" (not a state word), and one motion.
    private static CharacterActionInventory Inventory()
    {
        var model = Encoding.UTF8.GetBytes("{\"Version\":3,\"FileReferences\":{\"Moc\":\"m.moc3\",\"Textures\":[]," +
            "\"Expressions\":[{\"Name\":\"Smile\",\"File\":\"smile.exp3.json\"},{\"Name\":\"Glasses\",\"File\":\"glasses.exp3.json\"}]," +
            "\"Motions\":{\"Wave\":[{\"File\":\"wave.motion3.json\"}]}}}");
        var vts = Encoding.UTF8.GetBytes("{\"FileReferences\":{\"Model\":\"m.model3.json\"},\"Hotkeys\":[" +
            "{\"Name\":\"Star\",\"Action\":\"ToggleExpression\",\"File\":\"star.exp3.json\"}]}");
        var motion = Encoding.UTF8.GetBytes("{\"Version\":3,\"Meta\":{\"Duration\":1},\"Curves\":[]}");
        return CharacterActionInventory.From(AvatarRenderer.Live2D, "m.model3.json",
        [
            new("m.model3.json", model, "application/json"), new("m.moc3", new byte[64], "application/octet-stream"),
            new("smile.exp3.json", Expression, "application/json"), new("glasses.exp3.json", Expression, "application/json"),
            new("star.exp3.json", Expression, "application/json"), new("m.vtube.json", vts, "application/json"),
            new("wave.motion3.json", motion, "application/json")
        ]);
    }

    private static CharacterActionCatalog Catalog() => new(Inventory(), CharacterActions.Merge(Inventory(), null));

    private static CharacterActionSource Source(string id) => Inventory().Find(id)!;

    [Fact]
    public void Toggles_and_looks_that_stay_linger_by_default_and_the_rest_is_brief()
    {
        var catalog = Catalog();
        Assert.True(Source("expression:Star").Toggle);
        Assert.False(Source("expression:Glasses").Toggle);
        Assert.True(catalog.Lingers(Source("expression:Star")));
        Assert.True(catalog.Lingers(Source("expression:Glasses")));
        Assert.False(catalog.Lingers(Source("expression:Smile")));
        Assert.False(catalog.Lingers(Source("motion:Wave")));
        Assert.Equal(CharacterActions.Lingering, CharacterActions.DefaultMode(Source("expression:Smile"), "blush"));
        Assert.Equal(CharacterActions.Brief, CharacterActions.DefaultMode(Source("motion:Wave"), "angry"));
        // A turned-off emote doesn't linger.
        var settings = catalog.Settings with
        {
            Actions = catalog.Settings.Actions.Select(a => a.Id == "expression:Glasses" ? a with { Enabled = false } : a).ToArray()
        };
        Assert.False((catalog with { Settings = settings }).Lingers(Source("expression:Glasses")));
    }

    [Fact]
    public void An_off_tag_names_a_lingering_emote_and_a_plain_tag_never_turns_one_off()
    {
        var catalog = Catalog();
        Assert.Equal("glasses", CharacterActions.OffTagName("{/glasses}"));
        Assert.Null(CharacterActions.OffTagName("{glasses}"));
        Assert.Null(CharacterActions.OffTagName("{/}"));
        Assert.Null(CharacterActions.OffTagName("[/glasses]"));
        Assert.Equal("expression:Glasses", catalog.Off("{/glasses}")!.Id);
        Assert.Equal("expression:Glasses", catalog.Off("{/Glasses}")!.Id);
        Assert.Null(catalog.Off("{/smile}"));
        Assert.Null(catalog.Off("{/nothing}"));
        Assert.Empty(catalog.For("{/glasses}"));
        Assert.Equal("expression:Glasses", Assert.Single(catalog.For("{glasses}")).Id);
    }

    [Fact]
    public void The_prompt_offers_off_tags_and_what_shows_goes_in_a_note_never_the_instructions()
    {
        var catalog = Catalog();
        var now = new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
        var plain = catalog.Prompt(null, null)!;
        Assert.Contains("{glasses} - the character's emote named \"Glasses\" (stays on until you write {/glasses})", plain.Instructions);
        Assert.DoesNotContain("{/smile}", plain.Instructions);
        Assert.Contains("{/glasses}", plain.Tags);
        Assert.Contains("{/star}", plain.Tags);
        Assert.DoesNotContain("{/smile}", plain.Tags);
        Assert.Null(plain.Showing);

        HeldEmote[] held = [new(Source("expression:Glasses"), "m", now.AddMinutes(-12)), new(Source("expression:Star"), "m", now.AddSeconds(-20))];
        var showing = catalog.Prompt(null, null, held, now)!;
        Assert.Equal(plain.Instructions, showing.Instructions);
        Assert.Equal("Your character is showing {glasses} (12 min), {star} (just now). Each stays on until you write its off tag, " +
            "such as {/glasses}; turn one off when it no longer fits, otherwise leave it on.", showing.Showing);
        Assert.Null(catalog.Prompt(null, new PromptSettings { Overrides = new Dictionary<string, string> { [PromptCatalog.CharacterShowing] = "" } },
            held, now)!.Showing);
        Assert.Equal("2 h 5 min", CharacterActions.Age(TimeSpan.FromMinutes(125)));
        Assert.Equal("1 h", CharacterActions.Age(TimeSpan.FromMinutes(60)));
    }

    [Fact]
    public void A_reply_strips_off_tags_and_passes_them_on_as_cues()
    {
        var tags = Catalog().Prompt(null, null)!.Tags;
        var preview = SpeechTextPreview.For("Okay, glasses off {/glasses} now.", null, tags, SpeechBreaks.Default);
        Assert.Equal("Okay, glasses off now.", Assert.Single(preview.Spoken));
        Assert.Equal("{/glasses}", Assert.Single(preview.Cues).Tag);
    }

    [Fact]
    public async Task Settings_without_modes_still_load_and_modes_round_trip_and_are_checked()
    {
        var directory = Directory.CreateTempSubdirectory("martlet-lingering-").FullName;
        try
        {
            var inventory = Inventory();
            var id = inventory.ModelId;
            File.WriteAllText(CharacterActions.Path(directory), "{\"version\":1,\"models\":[{\"model_id\":\"" + id + "\",\"detected_by\":\"names\"," +
                "\"updated_at\":\"2026-01-01T00:00:00+00:00\",\"actions\":[{\"id\":\"expression:Glasses\",\"tag\":\"glasses\",\"enabled\":true}]}]}");
            var old = CharacterActions.Load(directory, id)!;
            Assert.Null(old.Find("expression:Glasses")!.Mode);
            Assert.True(new CharacterActionCatalog(inventory, CharacterActions.Merge(inventory, old)).Lingers(inventory.Find("expression:Glasses")!));

            var changed = CharacterActions.Merge(inventory, old) with
            {
                Actions = CharacterActions.Merge(inventory, old).Actions.Select(a => a.Id == "expression:Glasses"
                    ? a with { Mode = CharacterActions.Brief } : a.Id == "expression:Smile" ? a with { Mode = CharacterActions.Lingering } : a).ToArray()
            };
            await CharacterActions.SaveAsync(directory, changed, DateTimeOffset.Now);
            Assert.Contains("\"mode\": \"lingering\"", File.ReadAllText(CharacterActions.Path(directory)));
            var loaded = new CharacterActionCatalog(inventory, CharacterActions.Load(directory, id)!);
            Assert.False(loaded.Lingers(inventory.Find("expression:Glasses")!));
            Assert.True(loaded.Lingers(inventory.Find("expression:Smile")!));
            Assert.NotNull(CharacterActions.Problem(changed with
            {
                Actions = [.. changed.Actions.Select(a => a with { Mode = "forever" })]
            }));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void The_thinking_model_can_say_the_mode_and_answers_without_one_keep_it()
    {
        var inventory = Inventory();
        var current = CharacterActions.Merge(inventory, null);
        // Nameable order: Smile, Glasses, Star, Wave.
        var named = CharacterActions.Parse("1: grin | - | stays | when pleased with yourself\n2: specs | - | when focused\n" +
            "3: star_eyes | - | brief | when amazed\n4: wave | - | once | hello", inventory, current, DateTimeOffset.Now)!;
        Assert.Equal(CharacterActions.Lingering, named.Find("expression:Smile")!.Mode);
        Assert.Equal("when pleased with yourself", named.Find("expression:Smile")!.Use);
        Assert.Null(named.Find("expression:Glasses")!.Mode);
        Assert.Equal("when focused", named.Find("expression:Glasses")!.Use);
        Assert.Equal(CharacterActions.Brief, named.Find("expression:Star")!.Mode);
        Assert.Equal("hello", named.Find("motion:Wave")!.Use);
        var catalog = new CharacterActionCatalog(inventory, named);
        Assert.True(catalog.Lingers(inventory.Find("expression:Smile")!));
        Assert.False(catalog.Lingers(inventory.Find("expression:Star")!));
        Assert.Contains("<mode>", CharacterActions.NamingPrompt(inventory, null)!.Value.Instructions);
    }

    [Fact]
    public void Held_emotes_are_tracked_once_each_up_to_the_limit()
    {
        var held = new HeldEmotes();
        var changes = 0;
        held.Changed += () => changes++;
        var now = DateTimeOffset.Now;
        var glasses = Source("expression:Glasses");
        Assert.Null(held.Add(glasses, "m", now));
        Assert.Null(held.Add(glasses, "m", now.AddMinutes(5)));
        Assert.Equal(now, Assert.Single(held.Current).Since);
        Assert.True(held.Holds(glasses.Id));
        Assert.True(held.Remove(glasses.Id));
        Assert.False(held.Remove(glasses.Id));
        for (var i = 0; i < HeldEmotes.Maximum; i++) held.Add(glasses with { Id = $"expression:{i}" }, "m", now);
        var dropped = held.Add(glasses, "m", now);
        Assert.Equal("expression:0", dropped!.Source.Id);
        Assert.Equal(HeldEmotes.Maximum, held.Current.Count);
        Assert.Equal(HeldEmotes.Maximum, held.Clear().Count);
        Assert.Empty(held.Current);
        Assert.Empty(held.Clear());
        Assert.Equal(2 + HeldEmotes.Maximum + 2, changes);
    }

    [Fact]
    public async Task What_shows_goes_with_the_newest_message_and_the_instructions_stay_the_same()
    {
        await using var fixture = await LiveFixture.Create();
        var configuration = LiveConversationConfiguration.From(await fixture.Store.LoadAsync())!;
        ConversationRequest Ask(string? showing) => configuration.Request(new("Hi."), false, ResponseStyle.Helpful, [], null, null,
            out _, out _, out _, characterActions: (_, _) => new CharacterActionPrompt("Emote tags.", ["{glasses}", "{/glasses}"], showing));
        var without = Ask(null);
        var with = Ask("Your character is showing {glasses} (12 min).");
        Assert.Equal(without.Input.Personality, with.Input.Personality);
        Assert.DoesNotContain("showing {glasses}", with.Input.Personality);
        Assert.Contains("Your character is showing {glasses} (12 min).", with.Input.Notes);
        Assert.DoesNotContain("showing {glasses}", without.Input.Notes ?? "");
        Assert.Equal(["{glasses}", "{/glasses}"], with.CharacterTags);
        fixture.NoEffects();
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
    public async Task A_lingering_emote_stays_across_replies_until_its_off_tag_comes_back_on_the_same_model_and_clears()
    {
        using var scope = new AvatarHostingTests.Scope();
        var renderer = new Renderer();
        await using var avatar = new AvatarController(createRenderer: () => renderer, allowControlledClock: true);
        CharacterActionCatalog? catalog = Catalog();
        avatar.UseActions(_ => catalog);
        var profile = scope.Profile() with { LipSync = AvatarLipSync.Loudness };
        await avatar.ShowAsync(profile, default);

        avatar.Cues.Post([new CharacterCue("{glasses}", TimeSpan.Zero)], Task.CompletedTask);
        await Until(() => avatar.Held.Holds("expression:Glasses"));
        Assert.Equal(new RendererAction("expression", "Glasses", true, true), Assert.Single(renderer.Actions));
        // Already on: the next reply's {glasses} changes nothing; a brief motion plays beside it.
        avatar.Cues.Post([new CharacterCue("{glasses}", TimeSpan.Zero), new CharacterCue("{wave}", TimeSpan.Zero)], Task.CompletedTask);
        await Until(() => renderer.Actions.Count == 2);
        await Task.Delay(50);
        Assert.Equal(new RendererAction("motion", "Wave"), renderer.Actions.Last());
        Assert.Equal(2, renderer.Actions.Count);

        avatar.Cues.Post([new CharacterCue("{/glasses}", TimeSpan.Zero)], Task.CompletedTask);
        await Until(() => !avatar.Held.Holds("expression:Glasses"));
        Assert.Equal(new RendererAction("expression", "Glasses", false, true), renderer.Actions.Last());
        Assert.Contains("Turned off", avatar.LastAction);

        // Shown again, the same model gets its lingering emotes back.
        Assert.True(await avatar.PlayActionAsync(Source("expression:Star"), "a try", null, default, hold: true));
        await avatar.StopAsync();
        Assert.True(avatar.Held.Holds("expression:Star"));
        renderer.Actions.Clear();
        await avatar.ShowAsync(profile, default);
        Assert.Equal(new RendererAction("expression", "Star", true, true), Assert.Single(renderer.Actions));

        Assert.Equal(1, await avatar.ClearHeldAsync("Clear emotes", default));
        Assert.Empty(avatar.Held.Current);
        Assert.Equal(new RendererAction("expression", "Star", false, true), renderer.Actions.Last());

        // Another model (no catalog for it) forgets them.
        await avatar.PlayActionAsync(Source("expression:Star"), "a try", null, default, hold: true);
        await avatar.StopAsync();
        catalog = null;
        await avatar.ShowAsync(profile, default);
        Assert.Empty(avatar.Held.Current);
    }
}
