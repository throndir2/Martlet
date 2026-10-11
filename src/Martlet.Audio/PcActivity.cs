using System.Text;
using System.Text.RegularExpressions;

namespace Martlet.Audio;

/// <summary>What kind of thing makes sound on this PC, as far as Martlet can tell from the app, the titles of its windows, where
/// its program is and how busy it keeps the graphics card.</summary>
public enum PcActivityKind
{
    /// <summary>An app Martlet doesn't know (or one that is never a game, such as OBS or a launcher).</summary>
    Other,
    /// <summary>A web browser on a site Martlet doesn't know.</summary>
    Browser,
    /// <summary>A video site such as YouTube: someone talking to their viewers.</summary>
    Video,
    /// <summary>A live stream such as Twitch.</summary>
    Stream,
    /// <summary>A show or movie: Plex, Netflix, a video player.</summary>
    ShowOrMovie,
    /// <summary>Music: Spotify, a music player.</summary>
    Music,
    /// <summary>A game.</summary>
    Game,
    /// <summary>A voice chat with other people: Discord, TeamSpeak, Mumble.</summary>
    VoiceChat,
    /// <summary>A call or meeting: Zoom, Teams, Google Meet.</summary>
    Call
}

/// <summary>Where sound on this PC comes from: the kind of thing (<see cref="Kind"/>), the app people know it by
/// (<see cref="App"/>; for a game, the game's name) and, for a site in a browser, which site (<see cref="Site"/>).</summary>
public sealed record PcSource(PcActivityKind Kind, string App, string? Site = null)
{
    /// <summary>A short name for it that goes after "From" on each line Martlet heard from it: "a YouTube video in Chrome",
    /// "a game (Elden Ring)", "a voice chat in Discord".</summary>
    public string Label => Kind switch
    {
        PcActivityKind.Video => Site is null ? $"a video in {App}" : $"a {Site} video in {App}",
        PcActivityKind.Stream => Site is null ? $"a live stream in {App}" : $"a {Site} stream in {App}",
        PcActivityKind.ShowOrMovie => Site is null ? $"a show or movie in {App}" : $"a show or movie on {Site} in {App}",
        PcActivityKind.Music => Site is null ? $"music in {App}" : $"music on {Site} in {App}",
        PcActivityKind.Game => $"a game ({App})",
        PcActivityKind.VoiceChat => Site is null ? $"a voice chat in {App}" : $"a voice chat on {Site} in {App}",
        PcActivityKind.Call => Site is null ? $"a call in {App}" : $"a call on {Site} in {App}",
        _ => $"something playing in {App}"
    };

    /// <summary>What the user seems to be doing with it: "watching a YouTube video in Chrome", "playing a game (Elden Ring)".</summary>
    public string Doing => Kind switch
    {
        PcActivityKind.Video or PcActivityKind.Stream or PcActivityKind.ShowOrMovie => "watching " + Label,
        PcActivityKind.Music => "listening to " + Label,
        PcActivityKind.Game => "playing " + Label,
        PcActivityKind.VoiceChat or PcActivityKind.Call => "in " + Label,
        _ => $"{App} plays sound"
    };

    /// <summary>A video, stream, show, movie, music or a game: it never plays the user's own voice back.</summary>
    public bool Media => Kind is PcActivityKind.Video or PcActivityKind.Stream or PcActivityKind.ShowOrMovie or
        PcActivityKind.Music or PcActivityKind.Game;

    /// <summary>Other people talking with the user: a voice chat or a call.</summary>
    public bool People => Kind is PcActivityKind.VoiceChat or PcActivityKind.Call;
}

/// <summary>What Martlet knows about one app on this PC: its process name (without .exe), where its program is, the titles of its
/// visible windows (the one in front first), whether its window is in front and fills its screen, whether Windows says a game
/// runs in exclusive full screen there, and how busy it keeps the graphics card's 3D engine (percent; null when Windows didn't
/// say).</summary>
public sealed record PcAppFacts(string Process, string? Path = null, IReadOnlyList<string>? Titles = null, bool Foreground = false,
    bool FullScreen = false, double? Gpu = null, bool ExclusiveFullScreen = false);

/// <summary>How loud one app's sound is right now: the peak of its audio sessions (0 to 1) as the Windows volume mixer meters
/// it. <see cref="App"/> is its process name.</summary>
public readonly record struct PcAppLevel(string App, float Peak);

/// <summary>One thing the user seems to be doing on this PC: where it comes from, whether it made sound lately and whether its
/// window is in front and fills the screen.</summary>
public sealed record PcActivityEntry(PcSource Source, bool Audible, bool FullScreen);

/// <summary>What the user seems to be doing on this PC now, in order: what fills the screen first, then games, shows, videos,
/// calls, voice chats, music and the rest.</summary>
public sealed record PcActivityState(IReadOnlyList<PcActivityEntry> Activities)
{
    public static PcActivityState Empty { get; } = new([]);

    /// <summary>"playing a game (Elden Ring), full screen; in a voice chat in Discord", or null when nothing is known.</summary>
    public string? Summary => PcActivity.Summary(Activities);

    /// <summary>The note a reply reads (the context board's "activity" note), or null when nothing is known.</summary>
    public string? Note => PcActivity.Note(Activities);
}

/// <summary>Where a line heard from this PC came from: <see cref="Named"/> are the apps loud enough during it to name on the
/// line (the loudest, and a second one only when it was nearly as loud), and <see cref="Audible"/> every app that made any sound
/// then, loudest first.</summary>
public sealed record PcHeardFrom(IReadOnlyList<PcSource> Named, IReadOnlyList<PcSource> Audible)
{
    public static PcHeardFrom None { get; } = new([], []);

    /// <summary>Every app that made any sound during the line was a video, show, game or music: nothing that might have played
    /// the user's own voice back (a voice chat, a call, a voice changer, an unknown app or Windows itself).</summary>
    public bool MediaOnly => PcActivity.MediaOnly(Audible);
}

/// <summary>A line Hear what this PC plays heard: its words, where it came from and when its voice began and its recording ended
/// (timestamps on the conversation's clock; 0 when unknown).</summary>
public sealed record PcPlayedLine(string Text, PcHeardFrom From, long Start, long End);

/// <summary>Where Martlet reads what this PC is doing (Windows: the apps' audio sessions, their windows and the graphics card).
/// Used from one thread at a time.</summary>
public interface IPcActivitySource : IDisposable
{
    /// <summary>The sound level of every app's audio session right now, Martlet's own left out. Called about ten times a second, so
    /// it must be cheap.</summary>
    IReadOnlyList<PcAppLevel> Levels();

    /// <summary>What is known about <paramref name="apps"/> (process names) and the app whose window is in front. Called about every
    /// two seconds.</summary>
    IReadOnlyList<PcAppFacts> Facts(IReadOnlyCollection<string> apps);
}

/// <summary>Tells apart what makes sound on this PC (Hear what this PC plays): a YouTube video in a browser, a show or movie in
/// Plex, a game, a voice chat in Discord, music in Spotify. Deterministic and local: the app's process name, the site its
/// browser window shows, where its program is installed (a game library), whether it fills the screen in front and how busy it
/// keeps the graphics card. Window titles only decide the kind (and name a game); a private window's title is never read.</summary>
public static class PcActivity
{
    /// <summary>A window in front that fills its screen while the app keeps the 3D engine at least this busy (percent) is a game.</summary>
    public const double FullScreenGameGpu = 10;
    /// <summary>An app that keeps the 3D engine at least this busy (percent) while it plays sound is a game, even in a window.</summary>
    public const double WindowedGameGpu = 35;
    /// <summary>At most this many things go in the note.</summary>
    public const int MaximumActivities = 5;
    private const int MaximumName = 40;

    /// <summary>What <paramref name="app"/> is: a site in a browser, a known player or app, a game (in a game library, in
    /// exclusive full screen, full screen with the graphics card busy, or the graphics card very busy), or something else.</summary>
    public static PcSource Classify(PcAppFacts app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var key = Key(app.Process);
        var titles = (app.Titles ?? []).Where(title => !string.IsNullOrWhiteSpace(title) && !Private(title)).ToArray();
        if (Browsers.TryGetValue(key, out var browser))
        {
            var sites = titles.Select(SiteIn).Where(site => site is not null).Select(site => site!.Value).ToList();
            // A call or voice chat in any of its windows wins: the browser's sound may be that call, so it never counts as only a
            // video. Otherwise the first window (the one in front) with a site Martlet knows.
            var people = sites.Where(site => site.Kind is PcActivityKind.Call or PcActivityKind.VoiceChat).ToList();
            if (people.Count > 0) return new(people[0].Kind, browser, people[0].Name);
            return sites.Count > 0 ? new(sites[0].Kind, browser, sites[0].Name) : new(PcActivityKind.Browser, browser);
        }
        if (Players.TryGetValue(key, out var player))
            return new(titles.Any(MusicFile.IsMatch) ? PcActivityKind.Music : PcActivityKind.ShowOrMovie, player);
        if (Apps.TryGetValue(key, out var known)) return new(known.Kind, known.Name);
        if (Named.TryGetValue(key, out var named)) return new(PcActivityKind.Other, named);
        if (InGameLibrary(app.Path) || app.ExclusiveFullScreen || app.FullScreen && app.Gpu >= FullScreenGameGpu ||
            app.Gpu >= WindowedGameGpu)
            return new(PcActivityKind.Game, GameName(titles, app.Process));
        return new(PcActivityKind.Other, Clean(app.Process) is { Length: > 0 } name ? name : "an app");
    }

    /// <summary>Every source is a video, stream, show, movie, music or game (never a voice chat, a call, a browser on a site
    /// Martlet doesn't know or an unknown app, which may play the user's own voice back).</summary>
    public static bool MediaOnly(IReadOnlyCollection<PcSource>? sources) => sources is { Count: > 0 } && sources.All(source => source.Media);

    /// <summary>What goes after the [PC audio] marker on a line heard from <paramref name="sources"/>: "From a voice chat in
    /// Discord or a game (Elden Ring)", or null when Martlet can't tell.</summary>
    public static string? From(IReadOnlyCollection<PcSource>? sources) => Where(sources) is { } where ? "From " + where : null;

    /// <summary>"a voice chat in Discord or a game (Elden Ring)", or null for no sources.</summary>
    public static string? Where(IReadOnlyCollection<PcSource>? sources) =>
        sources is not { Count: > 0 } ? null : string.Join(" or ", sources.Select(source => source.Label));

    /// <summary>The activities in the order the note gives them, alike ones merged, at most <see cref="MaximumActivities"/>.</summary>
    public static IReadOnlyList<PcActivityEntry> Order(IEnumerable<PcActivityEntry> activities) =>
        [.. activities.GroupBy(entry => entry.Source)
            .Select(same => new PcActivityEntry(same.Key, same.Any(e => e.Audible), same.Any(e => e.FullScreen)))
            .OrderByDescending(entry => entry.FullScreen).ThenBy(entry => Rank(entry.Source.Kind))
            .ThenBy(entry => entry.Source.App, StringComparer.OrdinalIgnoreCase).Take(MaximumActivities)];

    /// <summary>"playing a game (Elden Ring), full screen; in a voice chat in Discord", or null for nothing.</summary>
    public static string? Summary(IReadOnlyList<PcActivityEntry> activities)
    {
        ArgumentNullException.ThrowIfNull(activities);
        if (activities.Count == 0) return null;
        return string.Join("; ", activities.Select(entry => entry.Source.Doing + (entry.FullScreen ? ", full screen" : "") +
            (entry.Audible ? "" : ", no sound right now")));
    }

    /// <summary>The note a reply reads about what the user is doing on this PC, or null for nothing.</summary>
    public static string? Note(IReadOnlyList<PcActivityEntry> activities) => Summary(activities) is not { } summary ? null
        : "What the user seems to be doing on this PC now (a guess from which apps play sound and which window fills the screen): " +
          summary + ".";

    /// <summary>A window title Martlet never reads (passwords, banking, private browsing), as the screen glances skip.</summary>
    public static bool Private(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        return PrivateTitles.Any(marker => title.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static int Rank(PcActivityKind kind) => kind switch
    {
        PcActivityKind.Game => 0,
        PcActivityKind.ShowOrMovie => 1,
        PcActivityKind.Video => 2,
        PcActivityKind.Stream => 3,
        PcActivityKind.Call => 4,
        PcActivityKind.VoiceChat => 5,
        PcActivityKind.Music => 6,
        PcActivityKind.Browser => 7,
        _ => 8
    };

    private static string Key(string process)
    {
        var name = process.Trim().ToLowerInvariant();
        return name.EndsWith(".exe", StringComparison.Ordinal) ? name[..^4] : name;
    }

    // The site a browser window shows: the match that ends last in its title wins (sites put their name at the end, after the
    // page's own title), and of two that end together, the longer ("YouTube Music" over "YouTube").
    private static (PcActivityKind Kind, string Name)? SiteIn(string title)
    {
        (PcActivityKind Kind, string Name)? best = null;
        var end = -1;
        var length = 0;
        foreach (var (pattern, kind, name) in Sites)
            foreach (Match match in pattern.Matches(title))
            {
                var at = match.Index + match.Length;
                if (at < end || at == end && match.Length <= length) continue;
                (best, end, length) = ((kind, name), at, match.Length);
            }
        return best;
    }

    private static bool InGameLibrary(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var normal = path.Replace('/', '\\');
        return GameLibraries.Any(folder => normal.Contains(folder, StringComparison.OrdinalIgnoreCase));
    }

    // A game's name: the title of its window (trademark signs dropped), else its process name.
    private static string GameName(IEnumerable<string> titles, string process)
    {
        foreach (var title in titles)
            if (Clean(title) is { Length: > 0 } name) return name;
        return Clean(process) is { Length: > 0 } fallback ? fallback : "unknown";
    }

    private static string Clean(string text)
    {
        var builder = new StringBuilder(Math.Min(text.Length, MaximumName));
        var space = false;
        foreach (var c in text)
        {
            if (c is '\u2122' or '\u00AE' or '\u00A9' or '\u200B' or '\uFEFF') continue;
            if (char.IsWhiteSpace(c) || char.IsControl(c)) space = builder.Length > 0;
            else
            {
                if (space) builder.Append(' ');
                space = false;
                builder.Append(c);
            }
        }
        var name = builder.ToString();
        return name.Length <= MaximumName ? name : name[..(MaximumName - 1)].TrimEnd() + "\u2026";
    }

    private static Regex Word(string token) => new(@"(?<![\p{L}\p{N}])" + Regex.Escape(token) + @"(?![\p{L}\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly string[] PrivateTitles =
    [
        "password", "1password", "bitwarden", "keepass", "lastpass", "dashlane", "keeper", "credential manager",
        "inprivate", "incognito", "private browsing", "authenticator", "online banking"
    ];

    private static readonly Regex MusicFile = new(@"\.(mp3|flac|m4a|aac|ogg|oga|opus|wav|wma|alac|ape|aiff?|mka)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly (Regex Pattern, PcActivityKind Kind, string Name)[] Sites =
    [
        (Word("YouTube Music"), PcActivityKind.Music, "YouTube Music"),
        (Word("YouTube"), PcActivityKind.Video, "YouTube"),
        (Word("Twitch"), PcActivityKind.Stream, "Twitch"),
        (new(@"[|\-\u2013\u2014\u2022]\s*Kick(?![\p{L}\p{N}])", RegexOptions.CultureInvariant), PcActivityKind.Stream, "Kick"),
        (Word("Netflix"), PcActivityKind.ShowOrMovie, "Netflix"),
        (Word("Prime Video"), PcActivityKind.ShowOrMovie, "Prime Video"),
        (Word("Disney+"), PcActivityKind.ShowOrMovie, "Disney+"),
        (Word("Hulu"), PcActivityKind.ShowOrMovie, "Hulu"),
        (Word("HBO Max"), PcActivityKind.ShowOrMovie, "Max"),
        (Word("Crunchyroll"), PcActivityKind.ShowOrMovie, "Crunchyroll"),
        (Word("Paramount+"), PcActivityKind.ShowOrMovie, "Paramount+"),
        (Word("Peacock"), PcActivityKind.ShowOrMovie, "Peacock"),
        (Word("Apple TV"), PcActivityKind.ShowOrMovie, "Apple TV"),
        (Word("Plex"), PcActivityKind.ShowOrMovie, "Plex"),
        (Word("Jellyfin"), PcActivityKind.ShowOrMovie, "Jellyfin"),
        (Word("Emby"), PcActivityKind.ShowOrMovie, "Emby"),
        (Word("Tubi"), PcActivityKind.ShowOrMovie, "Tubi"),
        (Word("Pluto TV"), PcActivityKind.ShowOrMovie, "Pluto TV"),
        (Word("Vimeo"), PcActivityKind.Video, "Vimeo"),
        (Word("Dailymotion"), PcActivityKind.Video, "Dailymotion"),
        (Word("bilibili"), PcActivityKind.Video, "Bilibili"),
        (Word("niconico"), PcActivityKind.Video, "Niconico"),
        (new("\u30CB\u30B3\u30CB\u30B3", RegexOptions.CultureInvariant), PcActivityKind.Video, "Niconico"),
        (Word("Spotify"), PcActivityKind.Music, "Spotify"),
        (Word("SoundCloud"), PcActivityKind.Music, "SoundCloud"),
        (Word("Apple Music"), PcActivityKind.Music, "Apple Music"),
        (Word("Amazon Music"), PcActivityKind.Music, "Amazon Music"),
        (Word("Deezer"), PcActivityKind.Music, "Deezer"),
        (Word("Bandcamp"), PcActivityKind.Music, "Bandcamp"),
        (Word("Pandora"), PcActivityKind.Music, "Pandora"),
        (Word("Google Meet"), PcActivityKind.Call, "Google Meet"),
        (new(@"^Meet\s[-\u2013\u2014]\s", RegexOptions.CultureInvariant), PcActivityKind.Call, "Google Meet"),
        (Word("Microsoft Teams"), PcActivityKind.Call, "Teams"),
        (new(@"(?<![\p{L}\p{N}])Zoom (Meeting|Webinar|Workplace)(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            PcActivityKind.Call, "Zoom"),
        (Word("Discord"), PcActivityKind.VoiceChat, "Discord")
    ];

    private static readonly Dictionary<string, string> Browsers = new(StringComparer.Ordinal)
    {
        ["chrome"] = "Chrome", ["msedge"] = "Edge", ["firefox"] = "Firefox", ["opera"] = "Opera", ["opera_gx"] = "Opera GX",
        ["brave"] = "Brave", ["vivaldi"] = "Vivaldi", ["arc"] = "Arc", ["librewolf"] = "LibreWolf", ["waterfox"] = "Waterfox",
        ["floorp"] = "Floorp", ["zen"] = "Zen", ["chromium"] = "Chromium", ["thorium"] = "Thorium", ["iexplore"] = "Internet Explorer"
    };

    // Video players: music when a window's title names a music file, otherwise a show or movie.
    private static readonly Dictionary<string, string> Players = new(StringComparer.Ordinal)
    {
        ["vlc"] = "VLC", ["mpc-hc"] = "MPC-HC", ["mpc-hc64"] = "MPC-HC", ["mpc-be"] = "MPC-BE", ["mpc-be64"] = "MPC-BE",
        ["mpc-qt"] = "MPC-QT", ["mpv"] = "mpv", ["mpvnet"] = "mpv.net", ["potplayer"] = "PotPlayer", ["potplayer64"] = "PotPlayer",
        ["potplayermini"] = "PotPlayer", ["potplayermini64"] = "PotPlayer", ["kmplayer"] = "KMPlayer", ["kmplayer64x"] = "KMPlayer",
        ["gom"] = "GOM Player", ["smplayer"] = "SMPlayer", ["bsplayer"] = "BS.Player", ["wmplayer"] = "Windows Media Player",
        ["microsoft.media.player"] = "Media Player"
    };

    private static readonly Dictionary<string, (PcActivityKind Kind, string Name)> Apps = new(StringComparer.Ordinal)
    {
        ["plex"] = (PcActivityKind.ShowOrMovie, "Plex"), ["plex htpc"] = (PcActivityKind.ShowOrMovie, "Plex HTPC"),
        ["plexmediaplayer"] = (PcActivityKind.ShowOrMovie, "Plex"), ["netflix"] = (PcActivityKind.ShowOrMovie, "Netflix"),
        ["disneyplus"] = (PcActivityKind.ShowOrMovie, "Disney+"), ["primevideo"] = (PcActivityKind.ShowOrMovie, "Prime Video"),
        ["hulu"] = (PcActivityKind.ShowOrMovie, "Hulu"), ["crunchyroll"] = (PcActivityKind.ShowOrMovie, "Crunchyroll"),
        ["appletv"] = (PcActivityKind.ShowOrMovie, "Apple TV"), ["kodi"] = (PcActivityKind.ShowOrMovie, "Kodi"),
        ["jellyfin"] = (PcActivityKind.ShowOrMovie, "Jellyfin"), ["jellyfinmediaplayer"] = (PcActivityKind.ShowOrMovie, "Jellyfin"),
        ["jellyfin media player"] = (PcActivityKind.ShowOrMovie, "Jellyfin"), ["emby.theater"] = (PcActivityKind.ShowOrMovie, "Emby"),
        ["embytheater"] = (PcActivityKind.ShowOrMovie, "Emby"), ["stremio"] = (PcActivityKind.ShowOrMovie, "Stremio"),
        ["video.ui"] = (PcActivityKind.ShowOrMovie, "Movies & TV"),
        ["spotify"] = (PcActivityKind.Music, "Spotify"), ["applemusic"] = (PcActivityKind.Music, "Apple Music"),
        ["itunes"] = (PcActivityKind.Music, "iTunes"), ["tidal"] = (PcActivityKind.Music, "TIDAL"),
        ["deezer"] = (PcActivityKind.Music, "Deezer"), ["amazon music"] = (PcActivityKind.Music, "Amazon Music"),
        ["foobar2000"] = (PcActivityKind.Music, "foobar2000"), ["musicbee"] = (PcActivityKind.Music, "MusicBee"),
        ["aimp"] = (PcActivityKind.Music, "AIMP"), ["winamp"] = (PcActivityKind.Music, "Winamp"),
        ["plexamp"] = (PcActivityKind.Music, "Plexamp"), ["youtube music"] = (PcActivityKind.Music, "YouTube Music"),
        ["youtube-music-desktop-app"] = (PcActivityKind.Music, "YouTube Music"),
        ["youtube music desktop app"] = (PcActivityKind.Music, "YouTube Music"), ["pandora"] = (PcActivityKind.Music, "Pandora"),
        ["cider"] = (PcActivityKind.Music, "Cider"), ["dopamine"] = (PcActivityKind.Music, "Dopamine"),
        ["discord"] = (PcActivityKind.VoiceChat, "Discord"), ["discordptb"] = (PcActivityKind.VoiceChat, "Discord"),
        ["discordcanary"] = (PcActivityKind.VoiceChat, "Discord"), ["discorddevelopment"] = (PcActivityKind.VoiceChat, "Discord"),
        ["vesktop"] = (PcActivityKind.VoiceChat, "Vesktop"), ["legcord"] = (PcActivityKind.VoiceChat, "Legcord"),
        ["armcord"] = (PcActivityKind.VoiceChat, "ArmCord"), ["webcord"] = (PcActivityKind.VoiceChat, "WebCord"),
        ["ts3client_win64"] = (PcActivityKind.VoiceChat, "TeamSpeak"), ["ts3client_win32"] = (PcActivityKind.VoiceChat, "TeamSpeak"),
        ["teamspeak"] = (PcActivityKind.VoiceChat, "TeamSpeak"), ["mumble"] = (PcActivityKind.VoiceChat, "Mumble"),
        ["guilded"] = (PcActivityKind.VoiceChat, "Guilded"), ["revolt"] = (PcActivityKind.VoiceChat, "Revolt"),
        ["element"] = (PcActivityKind.VoiceChat, "Element"), ["ventrilo"] = (PcActivityKind.VoiceChat, "Ventrilo"),
        ["zoom"] = (PcActivityKind.Call, "Zoom"), ["ms-teams"] = (PcActivityKind.Call, "Teams"), ["teams"] = (PcActivityKind.Call, "Teams"),
        ["skype"] = (PcActivityKind.Call, "Skype"), ["slack"] = (PcActivityKind.Call, "Slack"), ["webex"] = (PcActivityKind.Call, "Webex"),
        ["ciscocollabhost"] = (PcActivityKind.Call, "Webex"), ["ciscowebexstart"] = (PcActivityKind.Call, "Webex"),
        ["ringcentral"] = (PcActivityKind.Call, "RingCentral"), ["whatsapp"] = (PcActivityKind.Call, "WhatsApp"),
        ["whatsapp.root"] = (PcActivityKind.Call, "WhatsApp"), ["signal"] = (PcActivityKind.Call, "Signal"),
        ["telegram"] = (PcActivityKind.Call, "Telegram"), ["viber"] = (PcActivityKind.Call, "Viber"), ["line"] = (PcActivityKind.Call, "LINE"),
        ["messenger"] = (PcActivityKind.Call, "Messenger"), ["g2mcomm"] = (PcActivityKind.Call, "GoTo Meeting"),
        ["gotomeeting"] = (PcActivityKind.Call, "GoTo Meeting"), ["goto"] = (PcActivityKind.Call, "GoTo")
    };

    // Apps with a name people know that are never games, even full screen with the graphics card busy: launchers, streaming and
    // voice tools, editors, Windows itself.
    private static readonly Dictionary<string, string> Named = new(StringComparer.Ordinal)
    {
        ["steam"] = "Steam", ["steamwebhelper"] = "Steam", ["epicgameslauncher"] = "Epic Games Launcher", ["battle.net"] = "Battle.net",
        ["riotclientservices"] = "Riot Client", ["riotclientux"] = "Riot Client", ["eadesktop"] = "EA app", ["upc"] = "Ubisoft Connect",
        ["ubisoftconnect"] = "Ubisoft Connect", ["galaxyclient"] = "GOG Galaxy", ["xboxpcapp"] = "Xbox app",
        ["playnite.desktopapp"] = "Playnite", ["playnite.fullscreenapp"] = "Playnite",
        ["obs64"] = "OBS", ["obs32"] = "OBS", ["obs"] = "OBS", ["voicemoddesktop"] = "Voicemod", ["voicemod"] = "Voicemod",
        ["nvidia broadcast"] = "NVIDIA Broadcast", ["nvidia overlay"] = "NVIDIA overlay", ["nvidia share"] = "NVIDIA overlay",
        ["steelseriesgg"] = "SteelSeries GG", ["steelseriessonar"] = "SteelSeries Sonar",
        ["wallpaper32"] = "Wallpaper Engine", ["wallpaper64"] = "Wallpaper Engine", ["lively"] = "Lively Wallpaper",
        ["msedgewebview2"] = "an app's web view", ["explorer"] = "File Explorer", ["windows sounds"] = "Windows sounds",
        ["audiodg"] = "Windows audio", ["svchost"] = "Windows", ["dwm"] = "Windows", ["systemsettings"] = "Settings",
        ["applicationframehost"] = "a Windows app", ["shellexperiencehost"] = "Windows", ["startmenuexperiencehost"] = "Windows",
        ["searchhost"] = "Windows search", ["textinputhost"] = "Windows", ["lockapp"] = "Windows",
        ["devenv"] = "Visual Studio", ["code"] = "Visual Studio Code", ["windowsterminal"] = "Terminal", ["blender"] = "Blender",
        ["resolve"] = "DaVinci Resolve", ["adobe premiere pro"] = "Premiere Pro", ["afterfx"] = "After Effects",
        ["photoshop"] = "Photoshop", ["powerpnt"] = "PowerPoint", ["winword"] = "Word", ["excel"] = "Excel", ["outlook"] = "Outlook",
        ["olk"] = "Outlook"
    };

    // Folders games are installed in by their stores and launchers.
    private static readonly string[] GameLibraries =
    [
        @"\steamapps\common\", @"\epic games\", @"\gog games\", @"\gog galaxy\games\", @"\xboxgames\", @"\riot games\",
        @"\ea games\", @"\origin games\", @"\ubisoft game launcher\games\", @"\rockstar games\", @"\itch\apps\",
        @"\amazon games\library\", @"\modifiablewindowsapps\", @"\battlestate games\", @"\wargaming.net\games\"
    ];
}

/// <summary>Martlet's guess for "Do you play games or use heavy apps on this PC?" (docs/RECOMMENDATION_DESIGN.md): a game
/// library with a game in it is on this PC. It looks, on each fixed drive, for the folders that Steam, Epic, GOG, Xbox, EA,
/// Ubisoft, Riot, Rockstar and Amazon install games in. Only folder checks: quick, and never on the reply path.</summary>
public static class GameLibraries
{
    // Relative to a drive's root.
    private static readonly string[] Folders =
    [
        @"Program Files (x86)\Steam\steamapps\common", @"Program Files\Steam\steamapps\common", @"SteamLibrary\steamapps\common",
        @"Steam\steamapps\common", @"Program Files\Epic Games", @"Epic Games", @"GOG Games", @"Program Files (x86)\GOG Galaxy\Games",
        @"XboxGames", @"Program Files\EA Games", @"Program Files (x86)\Origin Games", @"Program Files (x86)\Ubisoft\Ubisoft Game Launcher\games",
        @"Riot Games", @"Program Files\Rockstar Games", @"Amazon Games\Library"
    ];

    /// <summary>Whether a game library with a game in it is on this PC.</summary>
    public static bool Found()
    {
        try
        {
            return FoundIn(DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName),
                HasGames);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Whether <paramref name="hasGames"/> says a game library folder under one of <paramref name="roots"/> holds a game.</summary>
    public static bool FoundIn(IEnumerable<string> roots, Func<string, bool> hasGames) =>
        roots.Any(root => Folders.Any(folder => hasGames(Path.Combine(root, folder))));

    private static bool HasGames(string folder)
    {
        try { return Directory.Exists(folder) && Directory.EnumerateDirectories(folder).Any(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}

/// <summary>Follows what makes sound on this PC while Hear what this PC plays runs: about ten times a second it reads each app's
/// sound level (the volume mixer's meters, never the sound itself), and every two seconds it looks at the apps that played lately
/// and the window in front to tell what they are (<see cref="PcActivity.Classify"/>). <see cref="Now"/> is what the user seems
/// to be doing (for the reply's note), and <see cref="Between"/> says which apps were loud while a line was heard, so the line
/// can say where it came from. Runs on its own thread at below-normal priority while <see cref="On"/>; nothing is recorded, kept
/// on disk or sent.</summary>
public sealed class PcActivityMonitor : IDisposable
{
    /// <summary>How often the sound levels are read.</summary>
    public static TimeSpan Every { get; } = TimeSpan.FromMilliseconds(100);
    /// <summary>How often the apps and the window in front are looked at.</summary>
    public static TimeSpan FactsEvery { get; } = TimeSpan.FromSeconds(2);
    /// <summary>How long the sound levels are kept for telling where a line came from.</summary>
    public static TimeSpan Kept { get; } = TimeSpan.FromSeconds(30);
    /// <summary>How long after its last sound an app still counts as playing.</summary>
    public static TimeSpan AudibleFor { get; } = TimeSpan.FromSeconds(8);
    /// <summary>How long after someone last talked a voice chat or call still counts (people pause).</summary>
    public static TimeSpan PeopleFor { get; } = TimeSpan.FromSeconds(60);
    /// <summary>A peak at least this loud (about -40 dBFS) is sound.</summary>
    public const float Floor = 0.01f;
    /// <summary>An app must be loud in at least this share of a line's level readings to be one of its sources.</summary>
    public const double MinimumShare = 0.25;
    /// <summary>A second source is named only when it was at least this loud, compared with the loudest.</summary>
    public const double Rival = 0.4;

    private readonly Func<IPcActivitySource> sources;
    private readonly TimeProvider clock;
    private readonly bool manual;
    private readonly object gate = new();
    private readonly object raising = new();
    private readonly Queue<(long At, PcAppLevel[] Levels)> samples = new();
    private readonly Dictionary<string, long> heardAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PcSource> known = new(StringComparer.OrdinalIgnoreCase);
    private readonly ManualResetEventSlim wake = new(false);
    private IPcActivitySource? source;
    private PcActivityState state = PcActivityState.Empty;
    private Thread? thread;
    private long factsAt, version;
    private bool on, disposed;
    private int failures;
    private string? problem;

    /// <param name="sources">Opens the source each time the monitor turns on (it is disposed when it turns off).</param>
    /// <param name="manual">Never starts a thread: the owner calls <see cref="Tick"/> (tests and MCP's check).</param>
    public PcActivityMonitor(Func<IPcActivitySource> sources, TimeProvider? clock = null, bool manual = false)
    {
        this.sources = sources ?? throw new ArgumentNullException(nameof(sources));
        this.clock = clock ?? TimeProvider.System;
        this.manual = manual;
    }

    /// <summary>Raised after each look at the apps (about every two seconds) with what the user seems to be doing, and with
    /// <see cref="PcActivityState.Empty"/> when the monitor turns off. On the monitor's thread.</summary>
    public event Action<PcActivityState>? Updated;

    /// <summary>What the user seems to be doing on this PC now; empty while the monitor is off.</summary>
    public PcActivityState Now { get { lock (gate) return state; } }

    /// <summary>Why the last look failed (the monitor tries again), or null.</summary>
    public string? Problem { get { lock (gate) return problem; } }

    /// <summary>Whether it follows the PC now. Turning it off forgets everything it read.</summary>
    public bool On
    {
        get { lock (gate) return on; }
        set
        {
            long current;
            lock (gate)
            {
                if (disposed || on == value) return;
                on = value;
                current = ++version;
                if (!value) Forget();
                else if (!manual) thread ??= Start();
            }
            wake.Set();
            if (!value) Raise(PcActivityState.Empty, current);
        }
    }

    /// <summary>Where a line heard between <paramref name="from"/> and <paramref name="to"/> (timestamps on this monitor's clock;
    /// 0 for an unknown start: the 5 seconds before <paramref name="to"/>) came from: the apps to name on it (the loudest during
    /// it, and a second one only when it was nearly as loud) and every app that made any sound then. Nothing when no app played
    /// then (or the monitor wasn't on).</summary>
    public PcHeardFrom Between(long from, long to)
    {
        var start = from > 0 ? from - Ticks(TimeSpan.FromMilliseconds(300)) : to - Ticks(TimeSpan.FromSeconds(5));
        var end = to + Ticks(TimeSpan.FromMilliseconds(200));
        lock (gate)
        {
            var readings = 0;
            var heard = new Dictionary<string, (int Audible, double Sum)>(StringComparer.OrdinalIgnoreCase);
            foreach (var (at, levels) in samples)
            {
                if (at < start || at > end) continue;
                readings++;
                foreach (var level in levels)
                {
                    if (level.Peak < Floor) continue;
                    heard.TryGetValue(level.App, out var sum);
                    heard[level.App] = (sum.Audible + 1, sum.Sum + level.Peak);
                }
            }
            if (readings == 0 || heard.Count == 0) return PcHeardFrom.None;
            var ranked = heard.OrderByDescending(app => app.Value.Sum).ToList();
            var audible = ranked.Select(app => SourceOf(app.Key)).Distinct().ToArray();
            var steady = ranked.Where(app => app.Value.Audible >= Math.Max(1, readings * MinimumShare)).ToList();
            if (steady.Count == 0) return new([], audible);
            var loudest = steady[0].Value.Sum;
            return new([.. steady.Where(app => app.Value.Sum >= loudest * Rival).Select(app => SourceOf(app.Key)).Distinct().Take(2)], audible);
        }
    }

    /// <summary>One step: the sound levels now and, every <see cref="FactsEvery"/>, a look at the apps. The monitor's own thread
    /// calls it while <see cref="On"/>; a manual monitor's owner calls it. Does nothing while off.</summary>
    public void Tick()
    {
        IPcActivitySource current;
        lock (gate)
        {
            if (!on || disposed) return;
            current = source ??= sources();
        }
        var levels = current.Levels();
        var now = clock.GetTimestamp();
        var merged = levels.Where(level => !string.IsNullOrWhiteSpace(level.App))
            .GroupBy(level => level.App, StringComparer.OrdinalIgnoreCase)
            .Select(app => new PcAppLevel(app.Key, app.Max(level => level.Peak))).ToArray();
        string[] wanted;
        lock (gate)
        {
            if (!on) return;
            samples.Enqueue((now, merged));
            while (samples.Count > 0 && now - samples.Peek().At > Ticks(Kept)) samples.Dequeue();
            foreach (var level in merged)
                if (level.Peak >= Floor) heardAt[level.App] = now;
            if (factsAt != 0 && now - factsAt < Ticks(FactsEvery)) return;
            factsAt = now;
            foreach (var stale in heardAt.Where(app => now - app.Value > Ticks(PeopleFor)).Select(app => app.Key).ToArray())
                heardAt.Remove(stale);
            wanted = [.. heardAt.Keys];
        }
        var apps = current.Facts(wanted);
        PcActivityState updated;
        long seen;
        lock (gate)
        {
            if (!on) return;
            var entries = new List<PcActivityEntry>();
            foreach (var app in apps)
            {
                var found = PcActivity.Classify(app);
                known[app.Process] = found;
                var audible = heardAt.TryGetValue(app.Process, out var at) && now - at <= Ticks(found.People ? PeopleFor : AudibleFor);
                var filling = app.Foreground && app.FullScreen;
                if (audible || filling) entries.Add(new(found, audible, filling));
            }
            state = updated = new(PcActivity.Order(entries));
            seen = version;
            failures = 0;
            problem = null;
        }
        Raise(updated, seen);
    }

    private PcSource SourceOf(string app) => known.TryGetValue(app, out var found) ? found : PcActivity.Classify(new(app));

    private long Ticks(TimeSpan span) => (long)(span.TotalSeconds * clock.TimestampFrequency);

    // Turning off: what was read is forgotten (under the gate); the source is closed by the thread that uses it.
    private void Forget()
    {
        samples.Clear();
        heardAt.Clear();
        known.Clear();
        state = PcActivityState.Empty;
        factsAt = 0;
        if (manual) Release();
    }

    // Updates in order, never one from before the monitor turned off.
    private void Raise(PcActivityState update, long seen)
    {
        lock (raising)
        {
            lock (gate)
                if (seen != version) return;
            Updated?.Invoke(update);
        }
    }

    private Thread Start()
    {
        var started = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Martlet PC activity" };
        started.Start();
        return started;
    }

    private void Run()
    {
        while (true)
        {
            bool running;
            lock (gate)
            {
                if (disposed) break;
                running = on;
            }
            if (!running)
            {
                Release();
                wake.Wait();
                wake.Reset();
                continue;
            }
            var wait = Every;
            try { Tick(); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // A source that fails (an audio device removed, Windows refusing a call) is opened again, a little later each time.
                lock (gate)
                {
                    problem = error.GetType().Name + ": " + error.Message;
                    failures = Math.Min(failures + 1, 15);
                    wait = TimeSpan.FromSeconds(2 * failures);
                }
                Release();
            }
            wake.Wait(wait);
            wake.Reset();
        }
        Release();
    }

    private void Release()
    {
        IPcActivitySource? closing;
        lock (gate)
        {
            closing = source;
            source = null;
        }
        try { closing?.Dispose(); }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }

    public void Dispose()
    {
        Thread? running;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            on = false;
            version++;
            running = thread;
        }
        wake.Set();
        if (running is null || !running.Join(TimeSpan.FromSeconds(2))) Release();
    }
}
