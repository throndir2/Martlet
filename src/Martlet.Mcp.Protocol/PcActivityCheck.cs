using System.Diagnostics;
using Martlet.Audio;
using Martlet.Audio.Windows;
using Martlet.Core.Settings;

namespace Martlet.Mcp;

/// <summary>pc_activity_check: what Martlet tells apart about the sound this PC plays while it hears it (Hear what this PC
/// plays): which app plays it and what kind of thing that is (a YouTube video in a browser, a show or movie in Plex, a game, a
/// voice chat in Discord, music), deterministically from the app, its windows, the window in front, full screen and the graphics
/// card. First a live look at this PC through the production monitor and Windows source (the volume mixer's session meters and
/// window titles only: nothing is recorded, played, kept or sent; raw titles are never returned), then a FIXTURE rehearsal of the
/// production classifier, where fixture lines came from with the [PC audio] line labels, the note a reply reads, the speakers
/// check that keeps a video heard by the microphone from counting as the user, and the built-in prompts.</summary>
internal static class PcActivityCheck
{
    private const string Marker = "[PC audio]";

    internal static async Task<object> RunAsync(int? seconds, CancellationToken cancellation)
    {
        var live = await LiveAsync(Math.Clamp(seconds ?? 3, 1, 20), cancellation);
        var classify = Classify();
        var lines = Lines();
        var speakers = Speakers();
        var prompts = Prompts();
        var ok = live.Ok && classify.All(sample => sample.Ok) && lines.Ok && speakers.All(sample => sample.Ok) && prompts.Ok;
        return new
        {
            ok,
            live = live.Report,
            rehearsal = new
            {
                scene = "FIXTURE apps, levels and lines on a simulated clock (no real app, sound or model)",
                classify = classify.Select(sample => new
                {
                    scene = sample.Scene, kind = sample.Kind, label = sample.Label, expected = sample.Expected, ok = sample.Ok
                }).ToArray(),
                lines = lines.Report,
                speakers = speakers.Select(sample => new
                {
                    scene = sample.Scene, heard = sample.Heard, played = sample.Played, playedFrom = sample.From,
                    expected = sample.Expected, fromSpeakers = sample.Found, ok = sample.Ok
                }).ToArray(),
                prompts = prompts.Report
            }
        };
    }

    // The production monitor on this PC for a few seconds, and every app with an audio session as the Windows source sees it.
    private static async Task<(bool Ok, object Report)> LiveAsync(int seconds, CancellationToken cancellation)
    {
        try
        {
            using var monitor = new PcActivityMonitor(() => new WindowsPcActivitySource(), manual: true);
            monitor.On = true;
            var ticks = 0;
            var spent = Stopwatch.StartNew();
            spent.Stop();
            var until = Stopwatch.StartNew();
            while (until.Elapsed < TimeSpan.FromSeconds(seconds))
            {
                spent.Start();
                monitor.Tick();
                spent.Stop();
                ticks++;
                await Task.Delay(PcActivityMonitor.Every, cancellation);
            }
            var state = monitor.Now;
            using var source = new WindowsPcActivitySource();
            var peaks = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < 5; i++)
            {
                foreach (var level in source.Levels()) peaks[level.App] = Math.Max(peaks.GetValueOrDefault(level.App), level.Peak);
                await Task.Delay(PcActivityMonitor.Every, cancellation);
            }
            source.Facts(peaks.Keys.ToArray());
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellation);
            var apps = source.Facts(peaks.Keys.ToArray()).Select(app =>
            {
                var found = PcActivity.Classify(app);
                return new
                {
                    app = app.Process, kind = found.Kind.ToString(), label = found.Label, peak = Math.Round(peaks.GetValueOrDefault(app.Process), 3),
                    audible = peaks.GetValueOrDefault(app.Process) >= PcActivityMonitor.Floor, foreground = app.Foreground,
                    fullScreen = app.FullScreen, exclusiveFullScreen = app.ExclusiveFullScreen, gpuPercent = app.Gpu is { } gpu ? Math.Round(gpu, 1) : (double?)null,
                    programKnown = app.Path is not null, windows = app.Titles?.Count ?? 0
                };
            }).OrderByDescending(app => app.peak).ToArray();
            return (monitor.Problem is null, new
            {
                recorded = false,
                seconds,
                ticks,
                averageTickMs = ticks == 0 ? 0 : Math.Round(spent.Elapsed.TotalMilliseconds / ticks, 2),
                problem = monitor.Problem,
                summary = state.Summary,
                note = state.Note,
                apps
            });
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        {
            return (false, new { recorded = false, problem = error.GetType().Name + ": " + error.Message });
        }
    }

    private sealed record ClassifySample(string Scene, string Kind, string Label, string Expected, bool Ok);

    private static ClassifySample[] Classify() =>
    [
        Sample("Plex full screen", new("Plex", Titles: ["Plex"], Foreground: true, FullScreen: true, Gpu: 4), "a show or movie in Plex"),
        Sample("a YouTube video in Chrome", new("chrome", Titles: ["(3) Lofi beats to relax to - YouTube - Google Chrome"]), "a YouTube video in Chrome"),
        Sample("YouTube Music in Edge", new("msedge", Titles: ["Song • Artist - YouTube Music - Personal - Microsoft\u200b Edge"]),
            "music on YouTube Music in Edge"),
        Sample("a Twitch stream whose title names YouTube", new("firefox", Titles: ["Watching YouTube videos - Twitch \u2014 Mozilla Firefox"]),
            "a Twitch stream in Firefox"),
        Sample("Netflix in a browser", new("chrome", Titles: ["Netflix - Google Chrome"], Foreground: true, FullScreen: true, Gpu: 12),
            "a show or movie on Netflix in Chrome"),
        Sample("a private window's title is never read", new("chrome", Titles: ["Video - YouTube - Google Chrome (Incognito)"]),
            "something playing in Chrome"),
        Sample("Discord", new("Discord"), "a voice chat in Discord"),
        Sample("Zoom", new("Zoom"), "a call in Zoom"),
        Sample("Google Meet in a browser", new("chrome", Titles: ["Meet - abc-defg-hij - Google Chrome"]), "a call on Google Meet in Chrome"),
        Sample("Spotify", new("Spotify", Titles: ["Artist - Song"]), "music in Spotify"),
        Sample("a game in a Steam library", new("eldenring", @"D:\SteamLibrary\steamapps\common\ELDEN RING\Game\eldenring.exe",
            ["ELDEN RING\u2122"]), "a game (ELDEN RING)"),
        Sample("an unknown app full screen with the graphics card busy", new("Starfall", @"C:\Games\Starfall\Starfall.exe",
            ["Starfall: Reborn"], Foreground: true, FullScreen: true, Gpu: 62), "a game (Starfall: Reborn)"),
        Sample("an unknown app in a window with the graphics card very busy", new("Racer", Titles: ["Racer"], Gpu: 48), "a game (Racer)"),
        Sample("an unknown app full screen, graphics card idle", new("notepad", Titles: ["notes.txt - Notepad"], Foreground: true,
            FullScreen: true, Gpu: 1), "something playing in notepad"),
        Sample("OBS is never a game", new("obs64", Titles: ["OBS 31.0.0 - Profile: Untitled"], Foreground: true, FullScreen: true, Gpu: 80),
            "something playing in OBS"),
        Sample("VLC playing a movie file", new("vlc", Titles: ["Movie.2019.1080p.mkv - VLC media player"]), "a show or movie in VLC"),
        Sample("VLC playing a song file", new("vlc", Titles: ["01 - Song.flac - VLC media player"]), "music in VLC")
    ];

    private static ClassifySample Sample(string scene, PcAppFacts app, string expected)
    {
        var found = PcActivity.Classify(app);
        return new(scene, found.Kind.ToString(), found.Label, expected, found.Label == expected);
    }

    // A fixture monitor on a simulated clock: a voice chat over a game for 3 s, the game alone for 3 s, then a YouTube video.
    private static (bool Ok, object Report) Lines()
    {
        var clock = new PcAudioCheck.SimulatedClock();
        var source = new FixtureSource(clock);
        using var monitor = new PcActivityMonitor(() => source, clock, manual: true) { On = true };
        var start = clock.Now;
        for (var tick = 0; tick < 90; tick++)
        {
            monitor.Tick();
            clock.Now += TimeSpan.FromMilliseconds(100).Ticks;
        }
        long At(double second) => start + (long)(second * TimeSpan.TicksPerSecond);
        (string Scene, double From, double To, string Words, string Expected)[] heard =
        [
            ("someone in the voice chat while the game plays", 0.5, 2.5, "Push left, he's low!",
                $"{Marker} From a voice chat in Discord or a game (ELDEN RING): Push left, he's low!"),
            ("the game alone", 3.5, 5.5, "You have fallen.", $"{Marker} From a game (ELDEN RING): You have fallen."),
            ("a YouTube video", 6.5, 8.5, "Welcome back to the channel!", $"{Marker} From a YouTube video in Chrome: Welcome back to the channel!"),
            ("nothing played then", 9.5, 9.9, "Hello?", $"{Marker} Hello?")
        ];
        var results = heard.Select(line =>
        {
            var from = monitor.Between(At(line.From), At(line.To));
            var text = $"{Marker} {(PcActivity.From(from.Named) is { } named ? named + ": " : "")}{line.Words}";
            return new { scene = line.Scene, expected = line.Expected, line = text, mediaOnly = from.MediaOnly, ok = text == line.Expected };
        }).ToArray();
        var note = monitor.Now.Note;
        const string expectedNote = "What the user seems to be doing on this PC now (a guess from which apps play sound and which window " +
            "fills the screen): playing a game (ELDEN RING); watching a YouTube video in Chrome; in a voice chat in Discord.";
        return (results.All(result => result.ok) && note == expectedNote, new { lines = results, note, expectedNote, noteOk = note == expectedNote });
    }

    private sealed class FixtureSource(PcAudioCheck.SimulatedClock clock) : IPcActivitySource
    {
        private readonly long start = clock.Now;

        public IReadOnlyList<PcAppLevel> Levels()
        {
            var second = (clock.Now - start) / (double)TimeSpan.TicksPerSecond;
            return
            [
                new("Discord", second < 3 ? 0.3f : 0f),
                new("eldenring", second < 6 ? 0.2f : 0f),
                new("chrome", second is >= 6 and < 9 ? 0.25f : 0f)
            ];
        }

        public IReadOnlyList<PcAppFacts> Facts(IReadOnlyCollection<string> apps) =>
        [
            new("Discord", Titles: ["#general | Friends - Discord"]),
            new("eldenring", @"D:\SteamLibrary\steamapps\common\ELDEN RING\Game\eldenring.exe", ["ELDEN RING\u2122"]),
            new("chrome", Titles: ["Lofi beats - YouTube - Google Chrome"])
        ];

        public void Dispose() { }
    }

    private sealed record SpeakersSample(string Scene, string Heard, string Played, string From, bool Expected, bool Found, bool Ok);

    // The production speakers check (PcEcho.Speakers): what the microphone heard against what the PC played lately, on a
    // simulated clock (seconds).
    private static SpeakersSample[] Speakers()
    {
        var youtube = new PcSource(PcActivityKind.Video, "Chrome", "YouTube");
        var voicemod = new PcSource(PcActivityKind.Other, "Voicemod");
        var discord = new PcSource(PcActivityKind.VoiceChat, "Discord");
        var game = new PcSource(PcActivityKind.Game, "ELDEN RING");
        return
        [
            Check("the speakers playing a video at that moment", "and now the weather for the weekend", 1, 3.5,
                "And now the weather for the weekend, with rain.", [youtube], 0.9, 3.4, true),
            Check("a game's line heard by the microphone at that moment", "you have fallen my friend", 1, 3,
                "You have fallen, my friend. Rise again.", [game], 0.8, 3.2, true),
            Check("the same words, but the video said them 10 seconds before", "Did you see that?", 12, 13.5,
                "Oh my god, did you see that jump?", [youtube], 0, 2.5, false),
            Check("your own voice played back by a voice changer (as before)", "Hello Jane, how are you?", 1, 3,
                "Hello, Jane, how are you?", [voicemod], 1.1, 3.1, false),
            Check("a game while a voice changer also played (not only media)", "you have fallen my friend", 1, 3,
                "You have fallen, my friend.", [game, voicemod], 0.8, 3.2, false),
            Check("a voice chat repeating your words (as before)", "push left now he's low", 1, 3, "Push left now, he's low!", [discord], 1.2, 3.2, false),
            Check("a short answer", "I don't know.", 1, 2, "Honestly guys, I don't know what happened there.", [youtube], 0.5, 3, false),
            Check("common words scattered through a long video", "I think we should go", 1, 3,
                "I really think the plan was fine. We went to the store, and then we should probably talk about where to go next.",
                [youtube], 0, 6, false),
            Check("you quoting the video", "Ha, he said rain all week.", 2, 4, "Expect rain all week across the region.", [youtube], 0, 2.5, false),
            Check("you talking over a video", "Can you pause the video?", 1, 3, "And now the weather for the weekend.", [youtube], 0.5, 3, false)
        ];

        static SpeakersSample Check(string scene, string heard, double heardFrom, double heardTo, string played, PcSource[] from,
            double playedFrom, double playedTo, bool expected)
        {
            const long second = TimeSpan.TicksPerSecond;
            static long At(double seconds) => 1000 * second + (long)(seconds * second);
            var line = new PcPlayedLine(played, new([from[0]], from), At(playedFrom), At(playedTo));
            var found = PcEcho.Speakers(heard, At(heardFrom), At(heardTo), [line], second) is not null;
            return new(scene, heard, played, string.Join(" and ", from.Select(source => source.Label)), expected, found, found == expected);
        }
    }

    // The built-in prompts tell the Thinking model what each kind of line is.
    private static (bool Ok, object Report) Prompts()
    {
        var pc = PromptSettings.Fill(null, PromptCatalog.PcAudio, ("marker", Marker), ("silent", "pass"))!;
        var listening = PromptSettings.Fill(null, PromptCatalog.Listening, ("silent", "pass"))!;
        string[] pcNeeds = [$"{Marker} From a YouTube video in Chrome", "a show or movie", "a game", "voice chat", "music", "never the user"];
        string[] listeningNeeds = ["speakers", "voice chat", "what the user is doing on their PC"];
        var missing = pcNeeds.Where(text => !pc.Contains(text, StringComparison.Ordinal))
            .Concat(listeningNeeds.Where(text => !listening.Contains(text, StringComparison.Ordinal))).ToArray();
        return (missing.Length == 0, new { whatThisPcPlays = pc, alwaysListening = listening, missing });
    }
}
