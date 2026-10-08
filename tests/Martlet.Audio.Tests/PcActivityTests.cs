namespace Martlet.Audio.Tests;

public sealed class PcActivityTests
{
    [Theory]
    [InlineData("Plex", null, "Plex", false, false, null, "ShowOrMovie", "a show or movie in Plex")]
    [InlineData("chrome", null, "(3) Lofi beats to relax to - YouTube - Google Chrome", false, false, null, "Video", "a YouTube video in Chrome")]
    [InlineData("msedge", null, "Song • Artist - YouTube Music - Personal - Microsoft\u200b Edge", false, false, null, "Music", "music on YouTube Music in Edge")]
    [InlineData("firefox", null, "Watching YouTube videos - Twitch \u2014 Mozilla Firefox", false, false, null, "Stream", "a Twitch stream in Firefox")]
    [InlineData("chrome", null, "xqc | Kick - Google Chrome", false, false, null, "Stream", "a Kick stream in Chrome")]
    [InlineData("chrome", null, "Netflix - Google Chrome", true, true, 12.0, "ShowOrMovie", "a show or movie on Netflix in Chrome")]
    [InlineData("chrome", null, "Watch The Show | Disney+ - Google Chrome", false, false, null, "ShowOrMovie", "a show or movie on Disney+ in Chrome")]
    [InlineData("chrome", null, "Meet - abc-defg-hij - Google Chrome", false, false, null, "Call", "a call on Google Meet in Chrome")]
    [InlineData("chrome", null, "#general | Friends - Discord - Google Chrome", false, false, null, "VoiceChat", "a voice chat on Discord in Chrome")]
    [InlineData("chrome", null, "Video - YouTube - Google Chrome (Incognito)", false, false, null, "Browser", "something playing in Chrome")]
    [InlineData("chrome", null, "Docs - Google Chrome", true, true, 30.0, "Browser", "something playing in Chrome")]
    [InlineData("Discord", null, null, false, false, null, "VoiceChat", "a voice chat in Discord")]
    [InlineData("ts3client_win64", null, null, false, false, null, "VoiceChat", "a voice chat in TeamSpeak")]
    [InlineData("Zoom.exe", null, null, false, false, null, "Call", "a call in Zoom")]
    [InlineData("ms-teams", null, null, false, false, null, "Call", "a call in Teams")]
    [InlineData("Spotify", null, "Artist - Song", false, false, null, "Music", "music in Spotify")]
    [InlineData("vlc", null, "Movie.2019.1080p.mkv - VLC media player", false, false, null, "ShowOrMovie", "a show or movie in VLC")]
    [InlineData("vlc", null, "01 - Song.flac - VLC media player", false, false, null, "Music", "music in VLC")]
    [InlineData("eldenring", @"D:\SteamLibrary\steamapps\common\ELDEN RING\Game\eldenring.exe", "ELDEN RING\u2122", false, false, 0.0, "Game", "a game (ELDEN RING)")]
    [InlineData("Starfall", @"C:\Games\Starfall\Starfall.exe", "Starfall: Reborn", true, true, 62.0, "Game", "a game (Starfall: Reborn)")]
    [InlineData("Racer", null, "Racer", false, false, 48.0, "Game", "a game (Racer)")]
    [InlineData("notepad", null, "notes.txt - Notepad", true, true, 1.0, "Other", "something playing in notepad")]
    [InlineData("SomeTool", null, "Some Tool", false, false, 20.0, "Other", "something playing in SomeTool")]
    [InlineData("obs64", null, "OBS 31.0.0", true, true, 80.0, "Other", "something playing in OBS")]
    [InlineData("steamwebhelper", null, "Steam", true, true, 40.0, "Other", "something playing in Steam")]
    [InlineData("Windows sounds", null, null, false, false, null, "Other", "something playing in Windows sounds")]
    public void Classify_tells_apart_videos_shows_games_voice_chats_and_music(string process, string? path, string? title, bool foreground,
        bool fullScreen, double? gpu, string kind, string label)
    {
        var found = PcActivity.Classify(new(process, path, title is null ? null : [title], foreground, fullScreen, gpu));
        Assert.Equal(kind, found.Kind.ToString());
        Assert.Equal(label, found.Label);
    }

    [Fact]
    public void A_game_in_exclusive_full_screen_counts_even_without_the_graphics_card_reading()
    {
        Assert.Equal(PcActivityKind.Game, PcActivity.Classify(new("Unknown", Titles: ["Old Game"], Foreground: true, FullScreen: true,
            ExclusiveFullScreen: true)).Kind);
        Assert.Equal("a game (Old Game)", PcActivity.Classify(new("Unknown", Titles: ["Old Game"], ExclusiveFullScreen: true)).Label);
    }

    [Fact]
    public void Only_videos_shows_games_and_music_are_media_and_lines_say_where_they_came_from()
    {
        var youtube = new PcSource(PcActivityKind.Video, "Chrome", "YouTube");
        var game = new PcSource(PcActivityKind.Game, "ELDEN RING");
        var discord = new PcSource(PcActivityKind.VoiceChat, "Discord");
        Assert.True(PcActivity.MediaOnly([youtube, game]));
        Assert.False(PcActivity.MediaOnly([youtube, discord]));
        Assert.False(PcActivity.MediaOnly([new PcSource(PcActivityKind.Browser, "Chrome")]));
        Assert.False(PcActivity.MediaOnly([]));
        Assert.False(PcActivity.MediaOnly(null));
        Assert.Equal("From a voice chat in Discord or a game (ELDEN RING)", PcActivity.From([discord, game]));
        Assert.Null(PcActivity.From([]));
        Assert.Equal("watching a YouTube video in Chrome", youtube.Doing);
        Assert.Equal("in a voice chat in Discord", discord.Doing);
        Assert.Equal("playing a game (ELDEN RING)", game.Doing);
    }

    [Fact]
    public void The_note_puts_what_fills_the_screen_first_and_merges_alike_apps()
    {
        var entries = PcActivity.Order(
        [
            new(new PcSource(PcActivityKind.VoiceChat, "Discord"), Audible: true, FullScreen: false),
            new(new PcSource(PcActivityKind.Music, "Spotify"), Audible: true, FullScreen: false),
            new(new PcSource(PcActivityKind.VoiceChat, "Discord"), Audible: false, FullScreen: false),
            new(new PcSource(PcActivityKind.Video, "Chrome", "YouTube"), Audible: false, FullScreen: true),
            new(new PcSource(PcActivityKind.Game, "ELDEN RING"), Audible: true, FullScreen: false)
        ]);
        Assert.Equal("watching a YouTube video in Chrome, full screen, no sound right now; playing a game (ELDEN RING); " +
            "in a voice chat in Discord; listening to music in Spotify", PcActivity.Summary(entries));
        Assert.StartsWith("What the user seems to be doing on this PC now (a guess from which apps play sound and which window fills " +
            "the screen): watching", PcActivity.Note(entries));
        Assert.Null(PcActivity.Note([]));
    }

    [Fact]
    public void The_monitor_says_which_apps_were_loud_while_a_line_was_heard()
    {
        var time = new ManualTime();
        time.Advance(TimeSpan.FromHours(1));
        var source = new ScriptedSource(time);
        using var monitor = new PcActivityMonitor(() => source, time, manual: true);
        var updates = new List<PcActivityState>();
        monitor.Updated += updates.Add;
        // Off, it reads nothing.
        monitor.Tick();
        Assert.Equal(0, source.LevelReads);
        monitor.On = true;
        var start = time.GetTimestamp();
        for (var i = 0; i < 90; i++)
        {
            monitor.Tick();
            time.Advance(TimeSpan.FromMilliseconds(100));
        }
        long At(double seconds) => start + TimeSpan.FromSeconds(seconds).Ticks;
        // A voice chat over a game for 3 s, the game alone for 3 s, then a YouTube video.
        var both = monitor.Between(At(0.5), At(2.5));
        Assert.Equal(["a voice chat in Discord", "a game (ELDEN RING)"], both.Named.Select(s => s.Label));
        Assert.False(both.MediaOnly);
        var game = monitor.Between(At(3.5), At(5.5));
        Assert.Equal(["a game (ELDEN RING)"], game.Named.Select(s => s.Label));
        Assert.True(game.MediaOnly);
        Assert.Equal(["a YouTube video in Chrome"], monitor.Between(At(6.5), At(8.5)).Named.Select(s => s.Label));
        // An unknown start looks at the 5 seconds before the end.
        Assert.Equal(["a YouTube video in Chrome"], monitor.Between(0, At(12)).Named.Select(s => s.Label));
        Assert.Same(PcHeardFrom.None, monitor.Between(At(20), At(21)));
        Assert.Equal("playing a game (ELDEN RING); watching a YouTube video in Chrome; in a voice chat in Discord", monitor.Now.Summary);
        Assert.Equal(5, updates.Count);
        // Facts are read about every two seconds, levels every tick.
        Assert.Equal(90, source.LevelReads);
        Assert.Equal(5, source.FactReads);
        // Turned off, it forgets everything and says so.
        monitor.On = false;
        Assert.Same(PcActivityState.Empty, monitor.Now);
        Assert.Same(PcActivityState.Empty, updates[^1]);
        Assert.Same(PcHeardFrom.None, monitor.Between(At(6.5), At(8.5)));
        Assert.True(source.Disposed);
    }

    [Fact]
    public void Every_app_that_made_sound_decides_whether_a_line_is_only_media()
    {
        var time = new ManualTime();
        time.Advance(TimeSpan.FromHours(1));
        // A loud game and a voice changer playing your voice back at a third of its level: the line is named after the game,
        // but it isn't only media.
        var source = new LevelsSource([new("eldenring", 0.6f), new("VoicemodDesktop", 0.2f)]);
        using var monitor = new PcActivityMonitor(() => source, time, manual: true) { On = true };
        var start = time.GetTimestamp();
        for (var i = 0; i < 30; i++)
        {
            monitor.Tick();
            time.Advance(TimeSpan.FromMilliseconds(100));
        }
        var heard = monitor.Between(start + TimeSpan.FromSeconds(0.5).Ticks, start + TimeSpan.FromSeconds(2.5).Ticks);
        Assert.Equal(["a game (ELDEN RING)"], heard.Named.Select(s => s.Label));
        Assert.Equal(["a game (ELDEN RING)", "something playing in Voicemod"], heard.Audible.Select(s => s.Label));
        Assert.False(heard.MediaOnly);
    }

    [Fact]
    public void A_browser_with_a_call_in_any_window_counts_as_the_call()
    {
        var found = PcActivity.Classify(new("chrome", Titles: ["Lofi beats - YouTube - Google Chrome", "Meet - abc-defg-hij - Google Chrome"]));
        Assert.Equal("a call on Google Meet in Chrome", found.Label);
        Assert.False(found.Media);
    }

    [Fact]
    public void A_full_screen_app_counts_without_sound_and_a_quiet_app_drops_out()
    {
        var time = new ManualTime();
        time.Advance(TimeSpan.FromHours(1));
        var source = new ScriptedSource(time) { FullScreenGame = true };
        using var monitor = new PcActivityMonitor(() => source, time, manual: true) { On = true };
        for (var i = 0; i < 200; i++)
        {
            monitor.Tick();
            time.Advance(TimeSpan.FromMilliseconds(100));
        }
        // At the last look, 18 s in: the video stopped 9 s ago and the game 12 s ago, but the game fills the screen; the voice chat
        // still counts (people pause).
        Assert.Equal("playing a game (ELDEN RING), full screen, no sound right now; in a voice chat in Discord", monitor.Now.Summary);
    }

    [Theory]
    [InlineData("and now the weather for the weekend", "And now the weather for the weekend, with rain.", "Video", true)]
    [InlineData("you have fallen my friend", "You have fallen, my friend. Rise again.", "Game", true)]
    [InlineData("today we're going to look at the new graphic card from", "Today we're going to look at the new graphics card from Nvidia.", "Video", true)]
    [InlineData("Hello Jane, how are you?", "Hello, Jane, how are you?", "Other", false)]
    [InlineData("push left now he's low", "Push left now, he's low!", "VoiceChat", false)]
    [InlineData("I don't know.", "Honestly guys, I don't know what happened there.", "Video", false)]
    [InlineData("I think we should go", "I really think the plan was fine. We went to the store, and then we should probably talk about where to go next.", "Video", false)]
    [InlineData("Ha, he said rain all week.", "Expect rain all week across the region.", "Video", false)]
    [InlineData("Can you pause the video?", "And now the weather for the weekend.", "Video", false)]
    public void The_speakers_are_what_a_video_show_game_or_music_played_at_that_moment(string heard, string played, string kind, bool speakers)
    {
        var source = new PcSource(Enum.Parse<PcActivityKind>(kind), "App");
        Assert.Equal(speakers, PcEcho.Speakers(heard, At(1), At(3.5), [Line(played, source, 0.9, 3.4)], Second) is not null);
    }

    [Theory]
    // What a video said ten seconds before is never the speakers, however alike (the reported phrases).
    [InlineData("What do you think?", "Anyway, let me know what you think in the comments below.")]
    [InlineData("Did you see that?", "Oh my god, did you see that jump?")]
    [InlineData("Is that a bug?", "Is that a bug or a feature?")]
    public void A_phrase_the_video_said_earlier_never_counts(string heard, string played)
    {
        var video = new PcSource(PcActivityKind.Video, "Chrome", "YouTube");
        Assert.Null(PcEcho.Speakers(heard, At(12), At(13.5), [Line(played, video, 0, 2.5)], Second));
        Assert.NotNull(PcEcho.Speakers(heard, At(0.4), At(2.6), [Line(played, video, 0, 2.5)], Second));
    }

    [Fact]
    public void In_a_long_line_the_words_must_be_where_the_microphone_heard_them()
    {
        var video = new PcSource(PcActivityKind.Video, "Chrome", "YouTube");
        // 40 words over 20 s: the phrase is at the start, about 0-2 s in.
        var said = "is that a bug or a feature " + string.Join(" ", Enumerable.Repeat("and then the camera pans over the hills", 4)) +
            " while the music plays";
        var line = Line(said, video, 0, 20);
        Assert.NotNull(PcEcho.Speakers("is that a bug", At(0.2), At(2), [line], Second));
        Assert.Null(PcEcho.Speakers("is that a bug", At(17), At(19), [line], Second));
        // An unknown time never counts.
        Assert.Null(PcEcho.Speakers("is that a bug", 0, At(2), [line], Second));
        Assert.Null(PcEcho.Speakers("is that a bug", At(0.2), At(2), [line with { Start = 0 }], Second));
    }

    [Fact]
    public void The_speakers_check_returns_the_newest_matching_line_and_needs_only_media()
    {
        var older = new PcSource(PcActivityKind.Video, "Chrome", "YouTube");
        var newer = new PcSource(PcActivityKind.ShowOrMovie, "Plex");
        var found = PcEcho.Speakers("we have to go back now", At(1), At(3),
            [Line("We have to go back now!", older, 0.8, 3), Line("We have to go back, now.", newer, 1, 3.2)], Second);
        Assert.Equal([newer], found!.Named);
        // A voice changer that made any sound during the line (even quieter than the video) means it isn't only media.
        var mixed = new PcPlayedLine("We have to go back now!", new([older], [older, new PcSource(PcActivityKind.Other, "Voicemod")]),
            At(0.8), At(3));
        Assert.Null(PcEcho.Speakers("we have to go back now", At(1), At(3), [mixed], Second));
    }

    private const long Second = TimeSpan.TicksPerSecond;
    // Timestamps a thousand seconds in, so none is 0 (unknown).
    private static long At(double seconds) => 1000 * Second + (long)(seconds * Second);
    private static PcPlayedLine Line(string text, PcSource source, double start, double end) =>
        new(text, new([source], [source]), At(start), At(end));

    // The same levels on every reading.
    private sealed class LevelsSource(PcAppLevel[] levels) : IPcActivitySource
    {
        public IReadOnlyList<PcAppLevel> Levels() => levels;
        public IReadOnlyList<PcAppFacts> Facts(IReadOnlyCollection<string> apps) =>
        [
            new("eldenring", @"D:\SteamLibrary\steamapps\common\ELDEN RING\Game\eldenring.exe", ["ELDEN RING\u2122"]),
            new("VoicemodDesktop", Titles: ["Voicemod"])
        ];
        public void Dispose() { }
    }

    // A voice chat over a game for 3 s, the game alone until 6 s, then a YouTube video until 9 s.
    private sealed class ScriptedSource(ManualTime time) : IPcActivitySource
    {
        private readonly long start = time.GetTimestamp();
        internal int LevelReads, FactReads;
        internal bool Disposed, FullScreenGame;

        public IReadOnlyList<PcAppLevel> Levels()
        {
            LevelReads++;
            var second = (time.GetTimestamp() - start) / (double)TimeSpan.TicksPerSecond;
            return
            [
                new("Discord", second < 3 ? 0.3f : 0f),
                new("eldenring", second < 6 ? 0.2f : 0f),
                new("chrome", second is >= 6 and < 9 ? 0.25f : 0f),
                new("chrome", 0f)
            ];
        }

        public IReadOnlyList<PcAppFacts> Facts(IReadOnlyCollection<string> apps)
        {
            FactReads++;
            return
            [
                new("Discord", Titles: ["#general | Friends - Discord"]),
                new("eldenring", @"D:\SteamLibrary\steamapps\common\ELDEN RING\Game\eldenring.exe", ["ELDEN RING\u2122"],
                    Foreground: FullScreenGame, FullScreen: FullScreenGame),
                new("chrome", Titles: ["Lofi beats - YouTube - Google Chrome"])
            ];
        }

        public void Dispose() => Disposed = true;
    }
}
