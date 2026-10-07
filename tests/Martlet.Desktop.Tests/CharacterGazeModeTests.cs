using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Avatar.Hosting;
using Martlet.Avatar.RendererHost;
using Martlet.Avatars;
using Martlet.Conversation;
using Martlet.Core.Settings;

namespace Martlet.Desktop.Tests;

/// <summary>The character's usual gaze (the mouse, the mouse when near, straight ahead, the window you're using): your choice,
/// the persona's, or the character's own in a reply, and a touch that turns its eyes to the mouse.</summary>
public sealed class CharacterGazeModeTests
{
    // The character's frame near the lower-right corner of a 1920×1080 screen.
    private static readonly ScreenRect Frame = new(1476, 476, 420, 580);
    private static readonly ScreenPoint Far = new(300, 300), Near = new(1400, 700);

    [Fact]
    public void Each_gaze_aims_where_it_says()
    {
        Assert.Equal(("mouse", Far), Aim(GazeMode.Mouse, Far));
        Assert.Equal(("ahead", null), Aim(GazeMode.Near, Far));
        Assert.Equal(("mouse", Near), Aim(GazeMode.Near, Near));
        Assert.Equal(("ahead", null), Aim(GazeMode.Ahead, Near));
        Assert.Equal(("window", new ScreenPoint(700, 500)), CharacterGaze.Aim(GazeMode.Window, null, false, Far, Frame, new ScreenRect(100, 100, 1200, 800)));
        Assert.Equal(("ahead", null), CharacterGaze.Aim(GazeMode.Window, null, false, Far, Frame, null));
        // A touch turns the eyes to the mouse whatever the gaze; a point Martlet asked for wins over both.
        Assert.Equal(("mouse", Far), CharacterGaze.Aim(GazeMode.Ahead, null, true, Far, Frame, null));
        Assert.Equal(("point", new ScreenPoint(5, 6)), CharacterGaze.Aim(GazeMode.Ahead, new ScreenPoint(5, 6), true, Far, Frame, null));
        // An unreadable mouse leaves the eyes where they are.
        Assert.Equal(("mouse", null), Aim(GazeMode.Mouse, null));
        Assert.True(CharacterGaze.IsNear(Frame, new ScreenPoint(Frame.Left - Frame.Width * CharacterGaze.NearMargin + 1, Frame.Top)));
        Assert.False(CharacterGaze.IsNear(Frame, new ScreenPoint(Frame.Left - Frame.Width * CharacterGaze.NearMargin - 1, Frame.Top)));
        Assert.False(CharacterGaze.IsNear(new ScreenRect(0, 0, 0, 0), Near));

        static (string, ScreenPoint?) Aim(GazeMode mode, ScreenPoint? mouse) => CharacterGaze.Aim(mode, null, false, mouse, Frame, null);
    }

    [Fact]
    public void Your_choice_wins_then_the_personality_then_the_mouse_and_a_reply_changes_it_only_while_it_may()
    {
        Assert.Equal((GazeMode.Mouse, GazeSettings.FromDefault), (new GazeSettings().Mode, new GazeSettings().UsualFrom));
        var personality = new GazeSettings(Personality: GazeMode.Ahead);
        Assert.Equal((GazeMode.Ahead, GazeSettings.FromPersonality), (personality.Mode, personality.UsualFrom));
        var owner = personality with { Owner = GazeMode.Near };
        Assert.Equal((GazeMode.Near, GazeSettings.FromOwner), (owner.Mode, owner.UsualFrom));
        var chosen = owner with { Chosen = GazeMode.Window };
        Assert.Equal((GazeMode.Window, GazeMode.Near, true), (chosen.Mode, chosen.Usual, chosen.Changed));
        Assert.Equal((GazeMode.Near, false), ((chosen with { Free = false }).Mode, (chosen with { Free = false }).Changed));
        Assert.False((owner with { Chosen = GazeMode.Near }).Changed);
    }

    [Fact]
    public void Mode_tags_and_words_are_read_and_never_collide_with_the_glance_tags()
    {
        Assert.Equal(["{look mouse}", "{look near}", "{look ahead}", "{look window}", "{look usual}"], CharacterGaze.ModeTags);
        Assert.Empty(CharacterGaze.ModeTags.Intersect(CharacterGaze.Tags));
        Assert.True(CharacterGaze.TryMode("{Look Ahead}", out var ahead));
        Assert.Equal(GazeMode.Ahead, ahead);
        Assert.True(CharacterGaze.TryMode("{look usual}", out var usual));
        Assert.Null(usual);
        Assert.False(CharacterGaze.TryMode("{look top}", out _));
        Assert.True(CharacterGaze.IsLookTag("{look top}") && CharacterGaze.IsLookTag("{look near}"));
        Assert.False(CharacterGaze.IsLookTag("{blush}"));
        Assert.Equal(GazeMode.Ahead, CharacterGaze.ModeOf("Straight ahead"));
        Assert.Equal(GazeMode.Ahead, CharacterGaze.ModeOf("neutral"));
        Assert.Equal(GazeMode.Mouse, CharacterGaze.ModeOf("cursor"));
        Assert.Equal(GazeMode.Near, CharacterGaze.ModeOf("Follow your mouse when it's near"));
        Assert.Equal(GazeMode.Window, CharacterGaze.ModeOf("active window"));
        Assert.Null(CharacterGaze.ModeOf("sideways"));
        Assert.Null(CharacterGaze.ModeOf(null));
        // The Eyes menu's choices and their requests.
        Assert.Equal(["personality", "mouse", "near", "ahead", "window"], RendererGaze.Choices);
        Assert.Contains("look-personality", RendererRequest.Actions);
        Assert.Contains("look-window", RendererRequest.Actions);
        Assert.Contains(RendererRequest.FreeOn, RendererRequest.Actions);
        Assert.Contains(RendererRequest.FreeOff, RendererRequest.Actions);
        Assert.Equal("personality", RendererGaze.ChoiceOf(null));
        Assert.Equal("near", RendererGaze.ChoiceOf(GazeMode.Near));
    }

    [Fact]
    public void Replies_are_told_the_usual_gaze_in_the_instructions_and_a_change_in_the_notes()
    {
        var prompt = CharacterGaze.ReplyPrompt(null, GazeMode.Near)!;
        Assert.Contains("Your character's eyes usually look at the user's mouse pointer only while it is near you", prompt.Instructions);
        Assert.Contains("{look ahead} - look straight ahead and ignore the pointer", prompt.Instructions);
        Assert.Contains("{look usual} - go back to your usual gaze", prompt.Instructions);
        Assert.Equal(CharacterGaze.ModeTags, prompt.Tags);
        Assert.Null(CharacterGaze.ReplyPrompt(new PromptSettings { Overrides = new Dictionary<string, string> { [PromptCatalog.CharacterGaze] = "" } }, GazeMode.Mouse));
        var settings = new GazeSettings(GazeMode.Near, Chosen: GazeMode.Ahead);
        Assert.Equal("Right now your eyes look straight ahead and ignore the pointer: you changed that 3 min ago. Write {look usual} to go back, or leave it.",
            CharacterGaze.Note(null, settings, TimeSpan.FromMinutes(3)));
        Assert.Null(CharacterGaze.Note(null, settings with { Chosen = null }));
        Assert.Null(CharacterGaze.Note(null, settings with { Free = false }));

        // Joined with the emotes: both instructions, the emote tags first; left out when the tags wouldn't fit.
        var emotes = new CharacterActionPrompt("Emote tags.", ["{blush}"], "Showing glasses.");
        var joined = CharacterGaze.Join(emotes, prompt, "Looking away.")!;
        Assert.Equal(["{blush}", .. CharacterGaze.ModeTags], joined.Tags);
        Assert.StartsWith("Emote tags.\n\nYour character's eyes usually", joined.Instructions);
        Assert.Equal(("Showing glasses.", "Looking away."), (joined.Showing, joined.Looking));
        Assert.Equal(prompt with { Looking = "Looking away." }, CharacterGaze.Join(null, prompt, "Looking away."));
        Assert.Same(emotes, CharacterGaze.Join(emotes, null, null));
        var full = new CharacterActionPrompt("Many.", [.. Enumerable.Range(0, CharacterActionCatalog.MaximumTags - 2).Select(i => $"{{e{i}}}")]);
        Assert.Same(full, CharacterGaze.Join(full, prompt, null));
    }

    [Fact]
    public async Task A_reply_carries_the_gaze_tags_its_note_goes_on_the_board_and_the_tags_are_never_spoken()
    {
        await using var fixture = await LiveFixture.Create();
        var configuration = LiveConversationConfiguration.From(await fixture.Store.LoadAsync())!;
        var gaze = CharacterGaze.ReplyPrompt(configuration.Prompts, GazeMode.Mouse);
        ConversationRequest Ask(string? looking) => configuration.Request(new("Hi."), false, ResponseStyle.Helpful, [], null, null,
            out _, out _, out _, characterActions: (_, _) => CharacterGaze.Join(new CharacterActionPrompt("Emote tags.", ["{blush}"]), gaze, looking),
            board: looking);
        var plain = Ask(null);
        var changed = Ask("Right now your eyes look straight ahead.");
        Assert.Equal(["{blush}", .. CharacterGaze.ModeTags], plain.CharacterTags);
        Assert.Contains("Your character's eyes usually follow the user's mouse pointer wherever it goes", plain.Input.Personality);
        // The note never changes the instructions, so the start of the request stays the same.
        Assert.Equal(plain.Input.Personality, changed.Input.Personality);
        Assert.Contains("Right now your eyes look straight ahead.", changed.Input.Context);
        Assert.DoesNotContain("Right now your eyes", changed.Input.KeptUserText);

        var preview = SpeechTextPreview.For("{look ahead} Hmph. {look usual} Fine.", null, plain.CharacterTags, SpeechBreaks.Default,
            LiveConversationConfiguration.SilentReply);
        Assert.Equal("Hmph. Fine.", string.Join(" ", preview.Spoken));
        Assert.Equal(["{look ahead}", "{look usual}"], preview.Cues.Select(c => c.Tag));
        fixture.NoEffects();
    }

    [Fact]
    public async Task The_character_follows_your_choice_the_personality_its_own_replies_and_touches()
    {
        using var scope = new AvatarHostingTests.Scope();
        var renderer = new Renderer();
        await using var avatar = new AvatarController(createRenderer: () => renderer, allowControlledClock: true);
        var gaze = avatar.Gaze;
        GazeMode? personality = null;
        gaze.Personality = () => personality;
        var changes = 0;
        gaze.Changed += () => Interlocked.Increment(ref changes);

        // Chosen while hidden: the character starts with it when it shows.
        gaze.Configure(GazeMode.Ahead, true);
        Assert.Empty(renderer.Gazes);
        Assert.Contains("looks straight ahead, as you chose", gaze.Looking);
        await avatar.ShowAsync(scope.Profile() with { LipSync = AvatarLipSync.Loudness }, default);
        Assert.Equal(new RendererGaze(Mode: GazeMode.Ahead, Choice: "ahead", Free: true), Assert.Single(renderer.Gazes));

        // As the personality decides: the persona's gaze.
        personality = GazeMode.Near;
        gaze.Configure(null, true);
        await Until(() => renderer.Gazes.Length == 2);
        Assert.Equal(new RendererGaze(Mode: GazeMode.Near, Choice: "personality", Free: true), renderer.Gazes[1]);
        Assert.Contains("as its personality decided", gaze.Looking);
        Assert.Null(gaze.Prompt(null)!.Looking);

        // A reply changes it until a reply changes it back; the note says so meanwhile.
        avatar.Cues.Post([new CharacterCue("{look window}", TimeSpan.Zero)], Task.CompletedTask);
        await Until(() => renderer.Gazes.Length == 3);
        Assert.Equal(GazeMode.Window, renderer.Gazes[2].Mode);
        Assert.True(gaze.Settings.Changed);
        Assert.Contains("watch the window the user is working in", gaze.Prompt(null)!.Looking);
        Assert.Contains("Usually it follows your mouse when it's near", gaze.Looking);
        avatar.Cues.Post([new CharacterCue("{look usual}", TimeSpan.Zero)], Task.CompletedTask);
        await Until(() => renderer.Gazes.Length == 4);
        Assert.Equal(GazeMode.Near, renderer.Gazes[3].Mode);
        Assert.Null(gaze.Prompt(null)!.Looking);

        // A touch turns the eyes to the mouse for a while.
        gaze.Attend(3, "a touch on groin");
        await Until(() => renderer.Gazes.Length == 5);
        Assert.Equal(new RendererGaze(Seconds: 3, Mouse: true), renderer.Gazes[4]);
        Assert.Contains("Right now it looks at your mouse after a touch on groin.", gaze.Looking);
        gaze.Attend(0, "nothing");
        gaze.Attend(double.NaN, "nothing");

        // Taken away: the character keeps its usual gaze, replies aren't told about it and their tags do nothing.
        avatar.Cues.Post([new CharacterCue("{look ahead}", TimeSpan.Zero)], Task.CompletedTask);
        await Until(() => renderer.Gazes.Length == 6);
        gaze.Configure(null, false);
        await Until(() => renderer.Gazes.Length == 7);
        Assert.Equal(new RendererGaze(Mode: GazeMode.Near, Choice: "personality", Free: false), renderer.Gazes[6]);
        Assert.Null(gaze.Prompt(null));
        Assert.False(gaze.Settings.Changed);
        avatar.Cues.Post([new CharacterCue("{look mouse}", TimeSpan.Zero)], Task.CompletedTask);
        await Task.Delay(100);
        Assert.Equal(7, renderer.Gazes.Length);
        Assert.Contains("It keeps this; it can't change where it looks.", gaze.Looking);
        Assert.True(changes > 0);
        Assert.Contains("usual gaze near", gaze.LastLookText);
        await avatar.StopAsync();
    }

    [Fact]
    public Task The_overlay_looks_ahead_at_the_mouse_after_a_touch_and_offers_an_eyes_menu() => OnDispatcher(async () =>
    {
        using var input = new PendingInput();
        using var output = new MemoryStream();
        var window = new RendererWindow(input, output);
        window.Show();
        try
        {
            await Dispatcher.Yield();
            Assert.Equal(new RendererLook("ahead", 0, 0, GazeMode.Ahead), window.Gaze(new RendererGaze(Mode: GazeMode.Ahead, Choice: "ahead")));
            Assert.Equal("mouse", window.Gaze(new RendererGaze(Seconds: 5, Mouse: true)).Target);
            Assert.Equal(new RendererLook("ahead", 0, 0, GazeMode.Ahead), window.Gaze(new RendererGaze()));
            Assert.Equal("point", window.Gaze(new RendererGaze(10, 10, 5)).Target);
            Assert.Equal(GazeMode.Near, window.Gaze(new RendererGaze(Mode: GazeMode.Near)).Mode);
            Assert.Throws<InvalidDataException>(() => window.Gaze(new RendererGaze(1, 2, 5, Mouse: true)));
            Assert.Throws<InvalidDataException>(() => window.Gaze(new RendererGaze(Choice: "sideways")));
            Assert.Throws<InvalidDataException>(() => window.Gaze(new RendererGaze(Mode: (GazeMode)42)));

            var menu = ((Grid)window.Content).ContextMenu!;
            var eyes = menu.Items.OfType<MenuItem>().Single(item => AutomationProperties.GetAutomationId(item) == "CharacterEyes");
            Assert.Equal(["CharacterEyes-personality", "CharacterEyes-mouse", "CharacterEyes-near", "CharacterEyes-ahead", "CharacterEyes-window",
                "CharacterEyes-free"], eyes.Items.OfType<MenuItem>().Select(AutomationProperties.GetAutomationId));
            Assert.All(eyes.Items.OfType<MenuItem>(), item => Assert.True(item.IsCheckable));
            // The gaze message travels with the usual gaze as a word.
            var message = RendererProtocol.Message("gaze", Guid.NewGuid(), new RendererGaze(Mode: GazeMode.Near, Choice: "near", Free: false));
            Assert.Contains("\"mode\":\"near\"", message.Data.GetRawText());
            Assert.Equal(new RendererGaze(Mode: GazeMode.Near, Choice: "near", Free: false), RendererProtocol.Data<RendererGaze>(message));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Your_gaze_choices_are_saved_on_this_pc()
    {
        var directory = Directory.CreateTempSubdirectory("martlet-gaze-").FullName;
        try
        {
            Assert.Equal((null, true), (TalkPreferences.Load(directory).GazeUsual, TalkPreferences.Load(directory).GazeFree));
            Assert.True((TalkPreferences.Load(directory) with { GazeUsual = GazeMode.Near, GazeFree = false }).Save(directory));
            Assert.Contains("\"GazeUsual\":\"near\"", File.ReadAllText(Path.Combine(directory, "talk-preferences.json")));
            var loaded = TalkPreferences.Load(directory);
            Assert.Equal((GazeMode.Near, false), (loaded.GazeUsual, loaded.GazeFree));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void The_personality_decides_the_usual_gaze_and_which_touches_draw_the_eyes_or_nothing_at_all()
    {
        var persona = Guid.NewGuid();
        var temperament = CharacterTouchTemperaments.Parse("{\"gaze\":\"Straight ahead\",\"groups\":{\"head\":{\"attitude\":0,\"reactions\":[\"none\"]}," +
            "\"torso\":{\"attitude\":0,\"reactions\":[]},\"lower_body\":{\"attitude\":-1,\"reactions\":[\"pout\"]}},\"zones\":{\"groin\":{\"attitude\":2," +
            "\"reactions\":[\"blush\",\"surprise\"],\"look\":4},\"chest\":{\"attitude\":0,\"reactions\":\"ignore\",\"look\":99}}}",
            persona, "digest", CharacterTouchTemperament.ByThinking, DateTimeOffset.Now)!;
        Assert.Equal(GazeMode.Ahead, temperament.Gaze);
        Assert.Empty(temperament.Groups["head"].Reactions!);
        Assert.Empty(temperament.Groups["torso"].Reactions!);
        Assert.Equal((4, 2), (temperament.Zones["groin"].LookSeconds, temperament.Zones["groin"].Attitude));
        Assert.Equal(CharacterTouchTemperaments.MaximumLook, temperament.Zones["chest"].LookSeconds);
        Assert.Empty(temperament.Zones["chest"].Reactions!);
        Assert.Null(CharacterTouchTemperaments.Problem(temperament));
        Assert.Contains("head neutral (no reaction)", CharacterTouchTemperaments.Summary(temperament));
        Assert.Contains("eyes: look straight ahead", CharacterTouchTemperaments.Summary(temperament));
        Assert.Contains("looks at your mouse after touches on chest, groin", CharacterTouchTemperaments.Summary(temperament));
        Assert.Contains("\"gaze\":\"mouse\"", CharacterTouchTemperaments.DecisionInstructions);

        // A head pat plays nothing; a touch on the groin blushes and looks at the mouse; the owner's pick keeps the look.
        var catalog = Catalog();
        var pat = CharacterTouchZones.React(new CharacterTouchZone { Id = "top_of_head", Box = new(0, 0, 1, 1) }, catalog, temperament, 1);
        Assert.Equal((TouchReactionPlan.FromTemperament, 0, 0d), (pat.From, pat.Actions.Count, pat.LookSeconds));
        var groin = new CharacterTouchZone { Id = "groin", Box = new(0, 0, 1, 1) };
        var touched = CharacterTouchZones.React(groin, catalog, temperament, 1);
        Assert.Equal(["blush", "surprise"], touched.Actions.Select(s => s.Name));
        Assert.Equal(4, touched.LookSeconds);
        Assert.Equal(4, CharacterTouchZones.React(groin with { Reaction = new() { Actions = ["gesture:nod"] } }, catalog, temperament, 1).LookSeconds);
        Assert.Equal(0, CharacterTouchZones.React(groin, catalog, null, 1).LookSeconds);
        Assert.Contains("0 to 15",
            CharacterTouchTemperaments.Problem(temperament with { Zones = new Dictionary<string, TouchTemperamentEntry> { ["groin"] = new() { LookSeconds = 99 } } }));
        Assert.NotNull(CharacterTouchTemperaments.Problem(temperament with { Gaze = (GazeMode)42 }));
        // Answers from before gazes existed still read.
        Assert.Null(CharacterTouchTemperaments.Parse("{\"groups\":{\"head\":{\"attitude\":1}}}", persona, null, CharacterTouchTemperament.ByThinking,
            DateTimeOffset.Now)!.Gaze);
    }

    [Fact]
    public async Task A_temperament_with_a_gaze_saves_and_shares()
    {
        var directory = Directory.CreateTempSubdirectory("martlet-gaze-").FullName;
        try
        {
            var persona = Guid.NewGuid();
            var temperament = CharacterTouchTemperaments.Parse("{\"gaze\":\"near\",\"groups\":{\"head\":{\"attitude\":1,\"look\":2}}}", persona, null,
                CharacterTouchTemperament.ByThinking, DateTimeOffset.Now)!;
            await CharacterTouchTemperaments.SaveAsync(directory, temperament, DateTimeOffset.Now);
            var text = File.ReadAllText(CharacterTouchTemperaments.Path(directory));
            Assert.Contains("\"gaze\": \"near\"", text);
            Assert.Contains("\"look_seconds\": 2", text);
            var loaded = CharacterTouchTemperaments.Load(directory, persona)!;
            Assert.Equal((GazeMode.Near, 2d), (loaded.Gaze, loaded.Groups["head"].LookSeconds));
            Assert.Contains("\"gaze\":\"near\"", CharacterTouchTemperaments.Share(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void A_touch_turns_the_eyes_to_the_mouse_when_its_reaction_says_so_but_not_while_the_zone_rests()
    {
        var service = new CharacterTouchZoneService(null);
        var looks = new List<(double Seconds, string Why)>();
        var touch = new CharacterTouch(0.5, 0.1, [], [], "head", null, false, null, null);
        TouchReactionPlan Plan(CharacterTouchZone zone, int repeats) => new([], 0, "loves", TouchReactionPlan.FromTemperament, false, 3);
        Assert.NotNull(service.React(touch, Plan, (_, _, _) => Task.CompletedTask, _ => { }, look: (seconds, why) => looks.Add((seconds, why))));
        Assert.Equal(3, Assert.Single(looks).Seconds);
        Assert.StartsWith("a touch on ", looks[0].Why);
        Assert.Contains("looks at your mouse for 3 s", service.LastMatch);
        service.React(touch, Plan, (_, _, _) => Task.CompletedTask, _ => { }, look: (seconds, why) => looks.Add((seconds, why)));
        Assert.Single(looks);
        Assert.Contains("resting", service.LastMatch);
    }

    [Fact]
    public void The_board_keeps_the_gaze_note_right_after_the_characters_own()
    {
        var board = new ContextBoard();
        var now = DateTimeOffset.Now;
        board.Post(ContextBoard.Screen, "A browser.", now, TimeSpan.FromMinutes(1));
        board.Post(ContextBoard.Gaze, "Looking away.", now, TimeSpan.FromMinutes(1));
        board.Post(ContextBoard.Character, "Showing glasses.", now, TimeSpan.FromMinutes(1));
        Assert.Equal([ContextBoard.Character, ContextBoard.Gaze, ContextBoard.Screen], board.Snapshot(now).Sources);
    }

    [Fact]
    public async Task The_mcp_gaze_check_reads_the_saved_gaze_and_the_personas_and_every_aim_holds()
    {
        var directory = Directory.CreateTempSubdirectory("martlet-gaze-").FullName;
        try
        {
            var fresh = JsonSerializer.SerializeToElement(Martlet.Mcp.GazeCheck.Run(directory, null));
            Assert.True(fresh.GetProperty("ok").GetBoolean(), fresh.GetProperty("aim").ToString());
            Assert.Equal("usual gaze", fresh.GetProperty("saved").GetString());
            var usual = fresh.GetProperty("usual");
            Assert.Equal(("personality", "mouse", "default", true), (usual.GetProperty("choice").GetString(), usual.GetProperty("gaze").GetString(),
                usual.GetProperty("from").GetString(), usual.GetProperty("free").GetBoolean()));
            Assert.Contains("{look usual}", usual.GetProperty("prompt").GetProperty("instructions").GetString());
            Assert.StartsWith("Right now your eyes", usual.GetProperty("noteWhenChanged").GetString());
            var hmph = fresh.GetProperty("replies").EnumerateArray().Single(r => r.GetProperty("answer").GetString() == "{look ahead} Hmph. Whatever.");
            Assert.Equal("ahead", hmph.GetProperty("gazes")[0].GetProperty("gaze").GetString());
            Assert.Equal("Hmph. Whatever.", string.Join(" ", hmph.GetProperty("spoken").EnumerateArray().Select(s => s.GetString())));
            Assert.Equal(8, fresh.GetProperty("aim").GetArrayLength());

            var persona = Guid.NewGuid();
            await CharacterTouchTemperaments.SaveAsync(directory, CharacterTouchTemperaments.Parse("{\"gaze\":\"ahead\",\"groups\":{\"head\":{\"attitude\":0}}}",
                persona, null, CharacterTouchTemperament.ByThinking, DateTimeOffset.Now)!, DateTimeOffset.Now);
            var decided = JsonSerializer.SerializeToElement(Martlet.Mcp.GazeCheck.Run(directory, null, persona.ToString())).GetProperty("usual");
            Assert.Equal(("ahead", "ahead", "personality"), (decided.GetProperty("personality").GetString(), decided.GetProperty("gaze").GetString(),
                decided.GetProperty("from").GetString()));

            File.WriteAllText(Path.Combine(directory, "talk-preferences.json"), "{\"GazeUsual\":\"window\",\"GazeFree\":false}");
            var chosen = JsonSerializer.SerializeToElement(Martlet.Mcp.GazeCheck.Run(directory, null, persona.ToString())).GetProperty("usual");
            Assert.Equal(("window", "window", "owner", false), (chosen.GetProperty("choice").GetString(), chosen.GetProperty("gaze").GetString(),
                chosen.GetProperty("from").GetString(), chosen.GetProperty("free").GetBoolean()));
            Assert.Equal(JsonValueKind.Null, chosen.GetProperty("prompt").ValueKind);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task A_gaze_the_overlay_did_not_take_is_sent_again_with_the_next_refresh()
    {
        using var scope = new AvatarHostingTests.Scope();
        var renderer = new Renderer { FailGazes = 1 };
        await using var avatar = new AvatarController(createRenderer: () => renderer, allowControlledClock: true);
        await avatar.ShowAsync(scope.Profile() with { LipSync = AvatarLipSync.Loudness }, default);
        avatar.Gaze.Configure(GazeMode.Ahead, true);
        await Until(() => renderer.Gazes.Length == 1);
        // The first one failed: a refresh with nothing changed still sends it again, and then only once.
        await Task.Delay(100);
        avatar.Gaze.Refresh();
        await Until(() => renderer.Gazes.Length == 2);
        Assert.Equal(GazeMode.Ahead, renderer.Gazes[1].Mode);
        avatar.Gaze.Refresh();
        await Task.Delay(100);
        Assert.Equal(2, renderer.Gazes.Length);
        Assert.Contains("usual gaze ahead", avatar.Gaze.LastLookText);
        await avatar.StopAsync();
    }

    private static CharacterActionCatalog Catalog()
    {
        var gestures = CharacterActionInventory.AllGestures
            .Where(g => g.Name is "blush" or "surprise" or "nod" or "pout")
            .Select(g => new CharacterActionSource(g.Id, CharacterActionKind.Gesture, g.Name, g.Does));
        var inventory = new CharacterActionInventory("model-1", AvatarRenderer.Live2D, [.. gestures]);
        return new(inventory, CharacterActions.Merge(inventory, null));
    }

    private sealed class Renderer : IAvatarRenderer
    {
        internal ConcurrentQueue<RendererMessage> Messages { get; } = new();
        public RendererCapabilities? Capabilities { get; private set; }
        public bool HasExited { get; private set; }
        private readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Exited => exited.Task;
        public event Action<string>? Requested { add { } remove { } }
        private readonly Guid activation = Guid.NewGuid();
        public Task StartAsync(AvatarProfile profile, string revision, RendererPlacement? placement, bool voiceMuted, CancellationToken token)
        {
            Capabilities = new(revision.ToLowerInvariant(), [new("Jaw", -10, 10, 0, ["Mouth"])]);
            return Task.CompletedTask;
        }
        public Task<RendererMessage> SendAsync<T>(string kind, T data, CancellationToken token, TimeSpan? timeout = null)
        {
            var message = RendererProtocol.Message(kind, activation, data);
            Messages.Enqueue(message);
            if (kind == "gaze" && Interlocked.Decrement(ref FailGazes) >= 0) throw new IOException("The overlay didn't answer.");
            return Task.FromResult(kind == "gaze"
                ? RendererProtocol.Message("look", activation, new RendererLook("mouse", 0.1, 0.2,
                    RendererProtocol.Data<RendererGaze>(message).Mode ?? GazeMode.Near))
                : RendererProtocol.Message("ok", activation, new { }));
        }
        // How many gazes fail (as an overlay that doesn't answer) before they go through.
        internal int FailGazes;
        public ValueTask DisposeAsync() { HasExited = true; exited.TrySetResult(); return ValueTask.CompletedTask; }
        internal RendererGaze[] Gazes => [.. Messages.Where(m => m.Kind == "gaze").Select(RendererProtocol.Data<RendererGaze>)];
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    private sealed class PendingInput : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    private static async Task OnDispatcher(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.UnhandledException += (_, e) =>
            {
                e.Handled = true;
                completion.TrySetException(e.Exception);
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            };
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); await Dispatcher.Yield(); completion.TrySetResult(); }
                catch (Exception e) { completion.TrySetException(e); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(thread.Join(TimeSpan.FromSeconds(3)));
    }
}
