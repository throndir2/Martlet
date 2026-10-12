using System.IO;
using System.Net.Http;

namespace Martlet.Conversation;

/// <summary>A web page that may have a character's model or voice recordings, found by <see cref="CharacterWebFinder"/>:
/// its title, address, site and the search engine's snippet. <see cref="SearchPage"/>: a search on that site for the
/// character, not a found page.</summary>
public sealed record CharacterWebLink(string Kind, string Title, string Url, string Site, string? Snippet, bool SearchPage);

/// <summary>What <see cref="CharacterWebFinder.FindAsync"/> found: pages that may have a model, pages that may have voice
/// recordings, searches on the sites that have them, and notes (a search that didn't work).</summary>
public sealed record CharacterWebFinds(IReadOnlyList<CharacterWebLink> Models, IReadOnlyList<CharacterWebLink> Voices,
    IReadOnlyList<CharacterWebLink> Searches, IReadOnlyList<string> Notes);

/// <summary>The backup finder of Find a character (docs/AVATARS.md#when-nothing-is-found): when Martlet can't find a model or a
/// voice it can use by itself, it searches the web for pages that may have a model of the character (VRM, Live2D, MMD or
/// VRChat; on Booth, VRoid Hub, nizima, Sketchfab, DeviantArt...) or recordings of its voice (voice lines on a wiki, clips),
/// and gives the links. The owner opens them, checks the creator's terms, downloads what they may use and adds it to Martlet
/// themselves (Add a character, Add a voice). Martlet downloads nothing from these pages.</summary>
public static class CharacterWebFinder
{
    public const string ModelKind = "model", VoiceKind = "voice";
    public const int MaximumLinks = 12;

    /// <summary>Sites where character models are shared or sold, best first.</summary>
    private static readonly string[] ModelSites =
        ["booth.pm", "hub.vroid.com", "nizima.com", "sketchfab.com", "deviantart.com", "bowlroll.net", "gumroad.com", "jinxxy.com",
         "payhip.com", "itch.io", "github.com", "seiga.nicovideo.jp", "3d.nicovideo.jp", "vrcmods.com", "cults3d.com"];

    /// <summary>Sites where voice lines and clips are kept, best first.</summary>
    private static readonly string[] VoiceSites =
        ["fandom.com", "youtube.com", "sounds-resource.com", "voicy.network", "myinstants.com", "wiki.gg", "genshin-impact.fandom.com",
         "soundcloud.com", "archive.org", "bilibili.com", "nicovideo.jp"];

    /// <summary>Hosts never offered: shops for unrelated things, social feeds and AI voice sellers.</summary>
    private static readonly string[] Unwanted =
        ["amazon.", "ebay.", "aliexpress.", "temu.", "pinterest.", "facebook.com", "instagram.com", "tiktok.com", "x.com", "twitter.com",
         "elevenlabs.io", "fakeyou.com", "uberduck.ai", "voicemod.net", "topmediai.com", "play.ht", "notevibes.com", "voice.ai", "lovo.ai"];

    /// <summary>Words of a page that makes a voice with AI instead of keeping recordings of it.</summary>
    private static readonly string[] Generators = ["voice generator", "ai voice", "text to speech", "text-to-speech", "voice changer", "tts"];

    /// <summary>The web searches Martlet runs for a character: one for models and one for voices (DuckDuckGo limits fast
    /// automated searches, so there are as few as possible).</summary>
    public static IReadOnlyList<string> Queries(string name, bool models, bool voices)
    {
        var queries = new List<string>();
        if (models) queries.Add($"{name.Trim()} VRM OR Live2D OR MMD model download");
        if (voices) queries.Add($"{name.Trim()} voice lines");
        return queries;
    }

    /// <summary>Searches on the sites themselves that the owner can open in the browser: always offered, also when the web
    /// search is refused.</summary>
    public static IReadOnlyList<CharacterWebLink> SearchPages(string name, bool models, bool voices)
    {
        var q = Uri.EscapeDataString(name.Trim());
        var links = new List<CharacterWebLink>();
        if (models)
        {
            links.Add(new(ModelKind, $"VRoid Hub: models of {name}", $"https://hub.vroid.com/en/search/{q}", "hub.vroid.com", null, true));
            links.Add(new(ModelKind, $"Booth: {name} VRM", $"https://booth.pm/en/search/{Uri.EscapeDataString(name.Trim() + " VRM")}", "booth.pm", null, true));
            links.Add(new(ModelKind, $"Booth: {name} Live2D", $"https://booth.pm/en/search/{Uri.EscapeDataString(name.Trim() + " Live2D")}", "booth.pm", null, true));
            links.Add(new(ModelKind, $"Sketchfab: downloadable models of {name}",
                $"https://sketchfab.com/search?features=downloadable&q={q}&type=models", "sketchfab.com", null, true));
            links.Add(new(ModelKind, $"nizima: Live2D models of {name}", Search($"site:nizima.com {Quote(name)}"), "nizima.com", null, true));
            links.Add(new(ModelKind, $"DeviantArt: MMD models of {name}", Search($"site:deviantart.com {Quote(name)} MMD"), "deviantart.com", null, true));
        }
        if (voices)
        {
            links.Add(new(VoiceKind, $"Wikis: voice lines of {name}", Search($"site:fandom.com OR site:wiki.gg {Quote(name)} voice lines"), "fandom.com", null, true));
            links.Add(new(VoiceKind, $"YouTube: {name} voice lines", $"https://www.youtube.com/results?search_query={Uri.EscapeDataString(name.Trim() + " voice lines")}",
                "youtube.com", null, true));
            links.Add(new(VoiceKind, $"The Sounds Resource: {name}", $"https://www.sounds-resource.com/search/?q={q}", "sounds-resource.com", null, true));
        }
        return links;
    }

    /// <summary>Runs <see cref="Queries"/> through <paramref name="search"/> and keeps the pages that name the character, from
    /// the sites that have models or voices first; at most <see cref="MaximumLinks"/> of each. A search that fails is named in
    /// the notes; the site searches (<see cref="SearchPages"/>) always come back.</summary>
    public static async Task<CharacterWebFinds> FindAsync(IWebSearch search, string name, bool models, bool voices, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(search);
        name = name.Trim();
        if (name.Length is 0 or > 100) throw new ArgumentException("Name the character in 1 to 100 characters.", nameof(name));
        var notes = new List<string>();
        var modelLinks = new List<(CharacterWebLink Link, int Score)>();
        var voiceLinks = new List<(CharacterWebLink Link, int Score)>();
        var words = Words(name);
        var queries = Queries(name, models, voices);
        for (var number = 0; number < queries.Count; number++)
        {
            var query = queries[number];
            var kind = query.EndsWith("voice lines", StringComparison.Ordinal) ? VoiceKind : ModelKind;
            // A short pause between searches, so the search engine doesn't take them for a flood.
            if (number > 0) await Task.Delay(Pause, token).ConfigureAwait(false);
            IReadOnlyList<WebSearchResult> results;
            try { results = await search.SearchAsync(query, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is WebResearchException or HttpRequestException or OperationCanceledException or IOException)
            {
                var why = $"The web search for {(kind == ModelKind ? "models" : "voice recordings")} didn't work ({error.Message}); the site searches below still work.";
                if (!notes.Contains(why)) notes.Add(why);
                continue;
            }
            if (results.Count == 0)
                notes.Add($"The web search for {(kind == ModelKind ? "models" : "voice recordings")} gave no results (the search engine may limit " +
                    "automated searches for a while); the site searches below still work.");
            foreach (var result in results.Take(30))
            {
                if (!Uri.TryCreate(result.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps) continue;
                var host = url.Host.ToLowerInvariant();
                if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
                if (Unwanted.Any(u => host.Contains(u, StringComparison.Ordinal))) continue;
                if (kind == VoiceKind && Generators.Any(g => (" " + result.Title + " " + result.Snippet + " ").Contains(
                        g.Length <= 3 ? " " + g + " " : g, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var text = Plain(result.Title + " " + result.Snippet + " " + Uri.UnescapeDataString(url.AbsolutePath));
                // The page names the character: its first word, and at least half of its words.
                if (words.Length > 0 && (!text.Contains(words[0], StringComparison.Ordinal) ||
                    words.Count(w => text.Contains(w, StringComparison.Ordinal)) * 2 < words.Length))
                    continue;
                var sites = kind == ModelKind ? ModelSites : VoiceSites;
                var rank = Array.FindIndex(sites, s => host == s || host.EndsWith("." + s, StringComparison.Ordinal));
                var score = (rank >= 0 ? 100 - rank : 0) + Hints(kind, result.Title + " " + result.Snippet) * 5;
                if (score <= 0) continue;
                var link = new CharacterWebLink(kind, Clip(result.Title, 120), url.AbsoluteUri, host, Clip(result.Snippet, 240), false);
                (kind == ModelKind ? modelLinks : voiceLinks).Add((link, score));
            }
        }
        static IReadOnlyList<CharacterWebLink> Best(List<(CharacterWebLink Link, int Score)> found) =>
            found.GroupBy(f => f.Link.Url, StringComparer.OrdinalIgnoreCase).Select(g => g.OrderByDescending(f => f.Score).First())
                .OrderByDescending(f => f.Score).Select(f => f.Link).Take(MaximumLinks).ToArray();
        return new(Best(modelLinks), Best(voiceLinks), SearchPages(name, models, voices), notes);
    }

    // Words in a page's title or snippet that say it has what was searched for.
    private static int Hints(string kind, string text)
    {
        var hints = kind == ModelKind
            ? new[] { "vrm", "vroid", "live2d", "mmd", "pmx", "vrchat", "3d model", "avatar", "model3", "vtuber model", "download" }
            : ["voice line", "voice-over", "voice over", "quotes", "voice clip", "audio", "sound", "voice", "lines"];
        return hints.Count(h => text.Contains(h, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A name's words as pages are matched: lowercase letters and digits only ("Re:Zero" is "rezero").</summary>
    private static string[] Words(string name) => name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .Select(Plain).Where(w => w.Length > 1).Take(4).ToArray();

    /// <summary>Text as names are matched in it: lowercase letters and digits only, so "Re:Zero", "Re Zero" and "ReZero" match.</summary>
    private static string Plain(string text) => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>How long Martlet waits between two web searches.</summary>
    public static TimeSpan Pause { get; set; } = TimeSpan.FromSeconds(1.5);

    private static string Quote(string name) => "\"" + name.Trim().Replace("\"", "", StringComparison.Ordinal) + "\"";

    private static string Search(string query) => "https://duckduckgo.com/?q=" + Uri.EscapeDataString(query);

    private static string Clip(string? text, int maximum)
    {
        text = (text ?? "").Trim();
        return text.Length <= maximum ? text : text[..(maximum - 1)].TrimEnd() + "…";
    }
}
