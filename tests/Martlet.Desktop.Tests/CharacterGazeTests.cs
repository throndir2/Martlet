using System.Collections.Concurrent;
using System.IO;
using System.Windows.Threading;
using Martlet.Avatar.Hosting;
using Martlet.Avatar.RendererHost;
using Martlet.Conversation;
using Martlet.Core.Settings;
using Martlet.Desktop;
using Martlet.Providers;

namespace Martlet.Desktop.Tests;

public sealed class CharacterGazeTests
{
    private static readonly ScreenRect Screen = new(0, 0, 1920, 1080);
    private static readonly ScreenPoint Mouse = new(300, 300);
    // Where the character stands by default: near the lower-right corner, where notifications pop up.
    private static readonly ScreenRect Overlay = new(1266, 476, 840, 580);

    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public void Advance(double seconds) => ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }

    // A change grid (60 px cells on the 1920×1080 screen) with the given cells changed by this much.
    private static byte[] Changes(int amount, params (int Column, int Row)[] cells)
    {
        var grid = new byte[CharacterGaze.Columns * CharacterGaze.Rows];
        foreach (var (column, row) in cells) grid[row * CharacterGaze.Columns + column] = (byte)amount;
        return grid;
    }

    private static (int, int)[] Block(int left, int top, int right, int bottom) =>
        [.. from row in Enumerable.Range(top, bottom - top + 1) from column in Enumerable.Range(left, right - left + 1) select (column, row)];

    [Fact]
    public void Something_new_in_one_place_draws_a_short_glance_and_a_spot_that_keeps_changing_gets_boring()
    {
        var clock = new Clock();
        var director = new GazeDirector(clock);
        var toast = Changes(120, Block(26, 15, 31, 17));
        var glance = director.Decide(toast, Screen, Mouse, []);
        Assert.Equal(GazeVerdict.Glance, glance.Verdict);
        Assert.Equal(("bottom right", GazeReason.Change, CharacterGaze.GlanceHold), (glance.Spot!.Place, glance.Spot.Reason, glance.Spot.Hold));
        Assert.InRange(glance.Spot.X, 1560, 1920);
        Assert.InRange(glance.Spot.Y, 900, 1080);

        clock.Advance(3);
        var elsewhere = Changes(120, Block(2, 15, 6, 17));
        Assert.Equal(GazeVerdict.TooSoon, director.Decide(elsewhere, Screen, Mouse, []).Verdict);
        clock.Advance(4);
        Assert.Equal(GazeVerdict.Seen, director.Decide(toast, Screen, Mouse, []).Verdict);
        clock.Advance(14);
        Assert.Equal(GazeVerdict.Glance, director.Decide(toast, Screen, Mouse, []).Verdict);
        // Looked there twice: the next look waits 40 seconds, but something new elsewhere still draws the eyes.
        clock.Advance(21);
        Assert.Equal(GazeVerdict.Seen, director.Decide(toast, Screen, Mouse, []).Verdict);
        var other = director.Decide(elsewhere, Screen, Mouse, []);
        Assert.Equal((GazeVerdict.Glance, "bottom left"), (other.Verdict, other.Spot!.Place));
        director.Reset();
        clock.Advance(1);
        Assert.Equal(GazeVerdict.Glance, director.Decide(toast, Screen, Mouse, []).Verdict);
    }

    [Fact]
    public void The_eyes_stay_on_the_mouse_without_one_clear_change_away_from_it()
    {
        var director = new GazeDirector(new Clock());
        Assert.Equal(GazeVerdict.Still, director.Decide(Changes(0), Screen, Mouse, []).Verdict);
        Assert.Equal(GazeVerdict.Still, director.Decide(Changes(CharacterGaze.ChangeThreshold - 1, Block(10, 5, 14, 8)), Screen, Mouse, []).Verdict);
        Assert.Equal(GazeVerdict.Everywhere, director.Decide(Changes(80, Block(0, 0, 31, 17)), Screen, Mouse, []).Verdict);
        Assert.Equal(GazeVerdict.ByMouse, director.Decide(Changes(80, Block(4, 4, 5, 5)), Screen, Mouse, []).Verdict);
        Assert.Equal(GazeVerdict.Scattered, director.Decide(Changes(80, (3, 15), (10, 2), (17, 9), (24, 16), (30, 3), (12, 13)),
            Screen, Mouse, []).Verdict);
        Assert.Equal(GazeVerdict.NoPicture, director.Decide(null, Screen, Mouse, []).Verdict);
        Assert.Equal(GazeVerdict.NoPicture, director.Decide(new byte[5], Screen, Mouse, []).Verdict);
        Assert.Equal(GazeVerdict.NoPicture, director.Decide(Changes(80, (3, 3)), new ScreenRect(0, 0, 0, 0), Mouse, []).Verdict);
    }

    [Fact]
    public void The_characters_own_motion_and_bubble_never_draw_its_eyes_but_a_notification_behind_it_does()
    {
        var director = new GazeDirector(new Clock());
        Assert.Equal(GazeVerdict.OnlyCharacter, director.Decide(Changes(30, Block(25, 10, 28, 14)), Screen, Mouse, [Overlay]).Verdict);
        Assert.Equal(GazeVerdict.OnlyCharacter, director.Decide(Changes(200, Block(17, 6, 21, 7)), Screen, Mouse, [Overlay],
            [new ScreenRect(990, 370, 320, 110)]).Verdict);
        var behind = director.Decide(Changes(120, Block(26, 15, 31, 17)), Screen, Mouse, [Overlay]);
        Assert.Equal((GazeVerdict.Glance, "bottom right"), (behind.Verdict, behind.Spot!.Place));
    }

    [Fact]
    public void Grids_compare_screenshots_of_the_same_area_cell_by_cell()
    {
        const int width = 64, height = 36;
        var dark = new byte[width * height * 4];
        var lit = (byte[])dark.Clone();
        for (var y = 0; y < 2; y++)
        for (var x = 62; x < 64; x++) lit[(y * width + x) * 4] = lit[(y * width + x) * 4 + 1] = lit[(y * width + x) * 4 + 2] = 200;
        var changes = CharacterGaze.Changes(CharacterGaze.Grid(dark, width, height), CharacterGaze.Grid(lit, width, height))!;
        Assert.Equal(CharacterGaze.Columns * CharacterGaze.Rows, changes.Length);
        Assert.Equal(200, changes[CharacterGaze.Columns - 1]);
        Assert.Equal(1, changes.Count(change => change > 0));
        Assert.Null(CharacterGaze.Changes(null, changes));
        Assert.Null(CharacterGaze.Changes(new byte[3], changes));
    }

    [Fact]
    public void Look_tags_point_at_a_ninth_of_the_screen_the_glance_showed()
    {
        Assert.Equal(9, CharacterGaze.Tags.Count);
        // Emote tags are slugs without spaces, so a look tag never collides with one.
        Assert.All(CharacterGaze.Tags, tag => Assert.Contains(' ', tag));
        Assert.True(CharacterGaze.IsTag("{Look Top Right}"));
        Assert.False(CharacterGaze.IsTag("{look up}"));
        Assert.False(CharacterGaze.IsTag("{blush}"));
        var spot = CharacterGaze.Chosen("{look top right}", Screen)!;
        Assert.Equal((1600, 180, "top right", GazeReason.Thinking, CharacterGaze.ChosenHold), (spot.X, spot.Y, spot.Place, spot.Reason, spot.Hold));
        var twoScreens = CharacterGaze.Chosen("{look bottom left}", new ScreenRect(-1920, 0, 3840, 1080))!;
        Assert.Equal((-1280, 900), (twoScreens.X, twoScreens.Y));
        Assert.Null(CharacterGaze.Chosen("{blush}", Screen));
        Assert.Null(CharacterGaze.Chosen("{look center}", new ScreenRect(0, 0, 0, 0)));
        Assert.Equal("center", CharacterGaze.Place(Screen, 960, 540));
        Assert.Equal("bottom left", CharacterGaze.Place(Screen, 0, 1079));
    }

    [Fact]
    public void The_look_prompt_lists_every_tag_and_can_be_emptied()
    {
        var prompt = CharacterGaze.Prompt(null, LiveConversationConfiguration.SilentReply)!;
        Assert.Equal(CharacterGaze.Tags, prompt.Tags);
        Assert.Contains("{look bottom right} - the bottom-right corner", prompt.Instructions);
        Assert.Contains("[pass]", prompt.Instructions);
        Assert.Null(CharacterGaze.Prompt(new PromptSettings { Overrides = new Dictionary<string, string> { [PromptCatalog.GlanceLook] = "" } },
            LiveConversationConfiguration.SilentReply));
        // A glance that only looks stays quiet, and its look cue still reaches the character.
        var preview = SpeechTextPreview.For("{look bottom right} [pass]", null, CharacterGaze.Tags, SpeechBreaks.Default, LiveConversationConfiguration.SilentReply);
        Assert.Empty(preview.Spoken);
        Assert.True(LiveConversationController.IsSilentReply(preview.Shown));
        Assert.Equal("{look bottom right}", Assert.Single(preview.Cues).Tag);
        var remark = SpeechTextPreview.For("{look top right} Sam just messaged you.", null, CharacterGaze.Tags, SpeechBreaks.Default,
            LiveConversationConfiguration.SilentReply);
        Assert.Equal("Sam just messaged you.", Assert.Single(remark.Spoken));
        Assert.Equal("Sam just messaged you.", remark.Shown.Trim());
    }

    [Fact]
    public async Task A_screen_glance_offers_the_look_tags_beside_the_emotes_only_when_they_fit()
    {
        await using var fixture = await LiveFixture.Create();
        var configuration = LiveConversationConfiguration.From(await fixture.Store.LoadAsync())!;
        var image = new BoundedImage([0xFF, 0xD8, 0xFF, .. new byte[32]], ImageMediaType.Jpeg, 4, 4);
        var gaze = CharacterGaze.Prompt(configuration.Prompts, LiveConversationConfiguration.SilentReply);
        ConversationRequest Glance(int emotes, CharacterActionPrompt? look) => configuration.Request(new("(Screen glance.)"), false,
            ResponseStyle.Helpful, [], null, null, out _, out _, out _, image, LiveConversationConfiguration.CommentaryInstructions(Chattiness.Normal),
            LiveConversationConfiguration.SilentReply,
            characterActions: (_, _) => new CharacterActionPrompt("Emote tags.", [.. Enumerable.Range(0, emotes).Select(i => $"{{emote_{i}}}")]),
            gaze: look);
        var glance = Glance(2, gaze);
        string[] expected = ["{emote_0}", "{emote_1}", .. CharacterGaze.Tags];
        Assert.Equal(expected, glance.CharacterTags);
        var instructions = glance.Input.Personality!;
        Assert.Contains("{look top right} - the top-right corner", instructions);
        Assert.True(instructions.IndexOf("Real friends stay quiet", StringComparison.Ordinal) <
            instructions.IndexOf("{look top left}", StringComparison.Ordinal));
        var plain = Glance(2, null);
        Assert.Equal(expected[..2], plain.CharacterTags);
        Assert.DoesNotContain("{look", plain.Input.Personality);
        var full = Glance(ConversationRequest.MaximumCharacterTags - 2, gaze);
        Assert.DoesNotContain(full.CharacterTags, CharacterGaze.IsTag);
        Assert.DoesNotContain("{look", full.Input.Personality);
        fixture.NoEffects();
    }

    [Fact]
    public void The_mcp_gaze_rehearsal_reads_the_saved_choice_and_every_scenario_holds()
    {
        var directory = Directory.CreateTempSubdirectory("martlet-gaze-").FullName;
        try
        {
            var result = System.Text.Json.JsonSerializer.SerializeToElement(Martlet.Mcp.GazeCheck.Run(directory, null));
            Assert.Equal("mouse", result.GetProperty("saved").GetString());
            Assert.True(result.GetProperty("ok").GetBoolean(), result.GetProperty("scenarios").ToString());
            Assert.Equal(9, result.GetProperty("tags").GetArrayLength());
            File.WriteAllText(Path.Combine(directory, "talk-preferences.json"), "{\"DecideGaze\":true}");
            Assert.True(TalkPreferences.Load(directory).DecideGaze);
            Assert.False(TalkPreferences.Load(null).DecideGaze);
            Assert.Equal("martlet decides", System.Text.Json.JsonSerializer.SerializeToElement(Martlet.Mcp.GazeCheck.Run(directory, "{look top} Whoa."))
                .GetProperty("saved").GetString());
        }
        finally { Directory.Delete(directory, recursive: true); }
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
        public Task StartAsync(AvatarProfile profile, string revision, RendererPlacement? placement, bool voiceMuted,
            CancellationToken token)
        {
            Capabilities = new(revision.ToLowerInvariant(), [new("Jaw", -10, 10, 0, ["Mouth"])]);
            return Task.CompletedTask;
        }
        public Task<RendererMessage> SendAsync<T>(string kind, T data, CancellationToken token, TimeSpan? timeout = null)
        {
            Messages.Enqueue(RendererProtocol.Message(kind, activation, data));
            return Task.FromResult(kind == "gaze"
                ? RendererProtocol.Message("look", activation, new RendererLook("point", 0.5, -0.25))
                : RendererProtocol.Message("ok", activation, new { }));
        }
        public ValueTask DisposeAsync() { HasExited = true; exited.TrySetResult(); return ValueTask.CompletedTask; }
        internal RendererGaze[] Gazes => [.. Messages.Where(m => m.Kind == "gaze").Select(RendererProtocol.Data<RendererGaze>)];
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    [Fact]
    public async Task Martlet_decides_from_each_screenshot_and_a_glances_look_tag_then_the_mouse_again_when_turned_off()
    {
        using var scope = new AvatarHostingTests.Scope();
        var renderer = new Renderer();
        await using var avatar = new AvatarController(createRenderer: () => renderer, allowControlledClock: true, gazeClock: new Clock());
        var gaze = avatar.Gaze;
        gaze.ReadDesktop = _ => (Mouse, [Overlay], []);
        var screenshot = new ScreenFrame(new byte[16], 2, 2, "", 0.1, Screen, Changes(120, Block(2, 15, 6, 17)));
        Assert.Null(gaze.Observe(screenshot));
        Assert.Equal("The character follows your mouse.", gaze.Status);

        gaze.Decides = true;
        Assert.Null(gaze.Observe(screenshot));
        Assert.Contains("once the character shows", gaze.Status);
        await avatar.ShowAsync(scope.Profile() with { LipSync = AvatarLipSync.Loudness }, default);
        Assert.Contains("while vision watches your screen", gaze.Status);

        Assert.Equal(GazeVerdict.Glance, gaze.Observe(screenshot)!.Verdict);
        await Until(() => renderer.Gazes.Length == 1 && gaze.LastLook is not null);
        var glance = renderer.Gazes[0];
        Assert.InRange(glance.X!.Value, 0, 640);
        Assert.InRange(glance.Y!.Value, 900, 1080);
        Assert.Equal(CharacterGaze.GlanceHold.TotalSeconds, glance.Seconds);
        Assert.Equal(new RendererLook("point", 0.5, -0.25), gaze.LastLook);
        Assert.Equal("Glancing at something new at the bottom left of your screen.", gaze.Status);
        // A camera picture has no place on the desktop.
        Assert.Null(gaze.Observe(new ScreenFrame(new byte[16], 2, 2, "", 0.1)));

        // The Thinking model starts a glance's answer with a look tag: the cue turns the eyes to that part of the screen.
        Assert.True(gaze.Offer(screenshot));
        avatar.Cues.Post([new CharacterCue("{look top right}", TimeSpan.Zero)], Task.CompletedTask);
        await Until(() => renderer.Gazes.Length == 2);
        Assert.Equal((1600d, 180d, CharacterGaze.ChosenHold.TotalSeconds), (renderer.Gazes[1].X, renderer.Gazes[1].Y, renderer.Gazes[1].Seconds));
        Assert.Equal("Looking at the top right of your screen, where Martlet chose to look.", gaze.Status);
        // While that look holds, changes on screen don't take the eyes away.
        Assert.Equal(GazeVerdict.TooSoon, gaze.Observe(screenshot)!.Verdict);
        Assert.Equal(2, renderer.Gazes.Length);

        // Turned off while looking away: the eyes go back to the mouse at once.
        gaze.Decides = false;
        await Until(() => renderer.Gazes.Length == 3);
        Assert.Equal((null, null), (renderer.Gazes[2].X, renderer.Gazes[2].Y));
        Assert.False(gaze.Offer(screenshot));
        avatar.Cues.Post([new CharacterCue("{look top left}", TimeSpan.Zero)], Task.CompletedTask);
        await Task.Delay(100);
        Assert.Equal(3, renderer.Gazes.Length);
        await avatar.StopAsync();
    }

    [Fact]
    public Task The_overlay_looks_at_a_point_for_a_while_and_back_at_the_mouse_on_request() => OnDispatcher(async () =>
    {
        using var input = new PendingInput();
        using var output = new MemoryStream();
        var window = new RendererWindow(input, output);
        window.Show();
        try
        {
            await Dispatcher.Yield();
            // Far up and to the right of the character's face: as far up-right as it turns.
            Assert.Equal(new RendererLook("point", 1, 1), window.Gaze(new RendererGaze(100_000, -100_000, 5)));
            Assert.Equal(new RendererLook("point", -1, -1), window.Gaze(new RendererGaze(-100_000, 100_000, 5)));
            Assert.Equal("mouse", window.Gaze(new RendererGaze()).Target);
            Assert.Throws<InvalidDataException>(() => window.Gaze(new RendererGaze(double.NaN, 3, 5)));
            Assert.Throws<InvalidDataException>(() => window.Gaze(new RendererGaze(3, null, 5)));
            var message = RendererProtocol.Message("gaze", Guid.NewGuid(), new RendererGaze(12.5, -4, 2.5));
            Assert.Equal(new RendererGaze(12.5, -4, 2.5), RendererProtocol.Data<RendererGaze>(message));
        }
        finally { window.Close(); }
    });

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
