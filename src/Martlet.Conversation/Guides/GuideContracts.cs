namespace Martlet.Conversation.Guides;

// App guides (docs/APP_GUIDES.md): what Martlet reads about a game or app the user runs, kept on this PC and searched while the user
// asks about it. These are the shared types of the guide store, the guide index, the wiki reader and the desktop.

/// <summary>A page read for an app guide: where it ended up (after redirects), its title, its readable text and the bytes
/// downloaded. In <see cref="Text"/> each block is on a line of its own and each heading is a Markdown line ("# ", "## ", "### "),
/// so the chunker can keep sections together.</summary>
public sealed record GuidePage(string Url, string Title, string Text, long Bytes);

/// <summary>One searchable piece of a guide: the page it comes from (its link and title), its section (the headings above it
/// joined with " › ", empty at the top of a page) and its text.</summary>
public sealed record GuideChunk(string Url, string Page, string Section, string Text);

/// <summary>A page a guide was made from.</summary>
public sealed record GuideSource(string Url, string Title, long Bytes);

/// <summary>One app's guide as kept on this PC: the app's key and name, the sites it was read from, when, its pages and its
/// chunks.</summary>
public sealed record AppGuide(string Key, string Name, IReadOnlyList<string> Sites, DateTimeOffset BuiltAt,
    IReadOnlyList<GuideSource> Sources, IReadOnlyList<GuideChunk> Chunks);

/// <summary>One app in the guide library (Companion › App guides), without its chunks.</summary>
public sealed record AppGuideEntry
{
    /// <summary>The app's key (<see cref="AppGuideKeys.Of"/>): its guide's file name.</summary>
    public required string Key { get; init; }
    /// <summary>The app's name as the user sees it ("Elden Ring", "Adobe Photoshop").</summary>
    public required string Name { get; init; }
    /// <summary>Program names that mean this app (as Windows names the program in front: its file description, product name or
    /// file name), matched ignoring case. Empty: <see cref="Name"/> itself.</summary>
    public IReadOnlyList<string> Programs { get; init; } = [];
    /// <summary>Pages the owner gave to start reading from (a wiki's address). Empty: Martlet finds the app's wiki itself.</summary>
    public IReadOnlyList<string> Sites { get; init; } = [];
    /// <summary>The owner doesn't want Martlet to read up on it on its own (they said no, or stopped it).</summary>
    public bool Declined { get; init; }
    /// <summary>When its guide was last made, or null when it has none yet.</summary>
    public DateTimeOffset? BuiltAt { get; init; }
    public int Pages { get; init; }
    public int Chunks { get; init; }
    public long Bytes { get; init; }
    /// <summary>Why the last try to make its guide failed, in a few plain words, or null.</summary>
    public string? Problem { get; init; }
    /// <summary>The short brief a Thinking pool member wrote from the guide (what it is, its core loop, main mechanics, early
    /// tips, common mistakes; <see cref="GuideBrief"/>), or null. The old brief stays until a new one replaces it.</summary>
    public string? Brief { get; init; }
    /// <summary>When <see cref="Brief"/> was written.</summary>
    public DateTimeOffset? BriefAt { get; init; }
    /// <summary>The areas of the game or app Martlet researched on the web (from what was on the screen, <see cref="GuideAreaResearch"/>),
    /// newest last, at most <see cref="GuideAreaResearch.MaximumAreas"/>.</summary>
    public IReadOnlyList<GuideArea> Areas { get; init; } = [];
}

/// <summary>An area of a game or app Martlet researched on the web: its name (a few words, such as "Frost Caves"), when, and how
/// many sections it added to the guide.</summary>
public sealed record GuideArea(string Name, DateTimeOffset At, int Sections);

/// <summary>The guide library's own settings and its apps, kept with the guides (guides\library.json), like lorebooks keep
/// theirs. App guides and Read up when I start a game or app are on by default (a library.json without "on" is on too); a saved
/// "on": false stays off. With the defaults, Martlet sends a game's name to a search engine and reads its wiki on its own when
/// the game starts; the page says so and how to stop it. Martlet 0.70 and earlier had App guides off by default and wrote
/// library.json only after the owner did something with App guides, so a saved off there may be a choice.</summary>
public sealed record AppGuideLibrary
{
    public const bool DefaultOn = true;
    public const bool DefaultReadUpWhenStarted = true;

    public bool On { get; init; } = DefaultOn;
    /// <summary>Whether Martlet reads up on its own on a game (or an app on the list) the user starts that has no guide yet, and
    /// tells them in character. Kept as askWhenStarted: before, Martlet asked first, and a no to asking stays a no to reading.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("askWhenStarted")]
    public bool ReadUpWhenStarted { get; init; } = DefaultReadUpWhenStarted;
    public IReadOnlyList<AppGuideEntry> Apps { get; init; } = [];
}

/// <summary>Keeps the guide library and each app's guide on this PC. Every method is safe to call from any thread; writes are
/// atomic (a failed write leaves the earlier file).</summary>
public interface IAppGuideStore
{
    /// <summary>The library (an empty, default one when there is none yet).</summary>
    Task<AppGuideLibrary> LoadLibraryAsync(CancellationToken cancellationToken);
    /// <summary>Replaces the library's settings and app list.</summary>
    Task SaveLibraryAsync(AppGuideLibrary library, CancellationToken cancellationToken);
    /// <summary>The app's guide, or null when it has none.</summary>
    Task<AppGuide?> LoadGuideAsync(string key, CancellationToken cancellationToken);
    /// <summary>Keeps <paramref name="guide"/> (replacing the app's earlier guide) and updates its entry's counts and time, adding
    /// the entry when the library doesn't have it yet and clearing its <see cref="AppGuideEntry.Problem"/>.</summary>
    Task SaveGuideAsync(AppGuide guide, CancellationToken cancellationToken);
    /// <summary>Deletes the app's guide and its entry.</summary>
    Task DeleteGuideAsync(string key, CancellationToken cancellationToken);
}

/// <summary>A chunk found for a question: its score (for ordering only) and how sure the match is, from 0 (nothing in common) to
/// 1 (every word of the question matched closely), which callers compare with a threshold.</summary>
public sealed record GuideHit(GuideChunk Chunk, double Score, double Relevance);

/// <summary>A searchable guide, built once from its chunks (<see cref="GuideIndex.Build"/>) and then read-only and thread-safe.
/// A search takes well under a millisecond for a few thousand chunks, so it can run on a reply's path.</summary>
public interface IGuideIndex
{
    int Count { get; }
    /// <summary>The best chunks for <paramref name="query"/>, best first, at most <paramref name="max"/>; empty when nothing
    /// matches.</summary>
    IReadOnlyList<GuideHit> Search(string query, int max);

    /// <summary>The chunks, in order (what an embedding index of the guide is made from).</summary>
    IReadOnlyList<GuideChunk> Chunks => [];

    /// <summary>Like <see cref="Search(string, int)"/>, fused with <paramref name="similar"/>: chunks an embedding search found for
    /// the same question (their number in <see cref="Chunks"/> and their <see cref="Martlet.Core.Search.SearchNeighbor.Meaning"/>),
    /// best first (docs/SEARCH.md, "Finding by meaning"). Without them it is the same search.</summary>
    IReadOnlyList<GuideHit> Search(string query, int max, IReadOnlyList<(int Chunk, double Meaning)>? similar) => Search(query, max);
}

/// <summary>The caps of making one guide: pages read, bytes downloaded, sites, the wait between two requests to one site and the
/// time it may take. By default the first read takes the <see cref="DefaultPages"/> best pages, those whose title names this
/// game first: on a big wiki about 5 minutes and 10 MB (a page takes about 1.4 s and 52 KB). Then a Thinking pool member chooses
/// more pages from the links of what was read (<see cref="More"/>). A whole series wiki (Borderlands: 10,700 articles) would take
/// hours and hundreds of MB, and fill the guide with other games. Checks pass small caps.</summary>
public sealed record GuideBuildLimits
{
    public const int DefaultPages = 200;
    public const long DefaultBytes = 25_000_000;
    public static TimeSpan DefaultTime { get; } = TimeSpan.FromMinutes(15);

    /// <summary>The caps of reading more after the first read (<see cref="GuideBuildRequest.More"/>): at most 300 more pages that
    /// a Thinking pool member chose, 25 MB and 20 minutes, from the same wiki.</summary>
    public static GuideBuildLimits More { get; } = new() { Pages = 300, Bytes = 25_000_000, Time = TimeSpan.FromMinutes(20), Sites = 1 };

    /// <summary>The most pages to read, or null for no cap.</summary>
    public int? Pages { get; init; } = DefaultPages;
    /// <summary>The most bytes to download, or null for no cap (each document is still at most 2 MB).</summary>
    public long? Bytes { get; init; } = DefaultBytes;
    public int Sites { get; init; } = 2;
    public TimeSpan Delay { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>The most time reading may take, or null for no cap.</summary>
    public TimeSpan? Time { get; init; } = DefaultTime;
}

/// <summary>What to read up on: the app's name and the pages to start from (empty: find the app's wiki).</summary>
public sealed record GuideBuildRequest(string Name, IReadOnlyList<string> Sites, GuideBuildLimits? Limits = null)
{
    /// <summary>Keeps the pages read so far while reading goes on, every <see cref="KeepEvery"/>, so a long read can be searched
    /// as it grows and isn't lost when it ends early. It returns false when the guide can hold no more, which ends reading.</summary>
    public Func<GuideBuildOutcome, CancellationToken, Task<bool>>? Keep { get; init; }
    public TimeSpan KeepEvery { get; init; } = TimeSpan.FromMinutes(3);
    /// <summary>Ends reading early, like a cap: the pages read so far are the outcome (Stop reading on the App guides page).</summary>
    public CancellationToken Stop { get; init; }
    /// <summary>Chooses the wiki page titles worth reading, in the order given (<see cref="GuidePageChooser"/>: a Thinking pool
    /// member), or null when it can't choose any more, which ends reading. Null: titles are only sorted by their brackets.</summary>
    public Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<string>?>>? Choose { get; init; }
    /// <summary>Reads more of a wiki read before instead: the pages its pages link to that weren't read, as <see cref="Choose"/>
    /// chooses them, and the links of those, with no search and no other site.</summary>
    public GuideMore? More { get; init; }
}

/// <summary>What a read of a MediaWiki site leaves to read more: the site, the titles its pages link to that weren't read (the
/// most linked first) and the titles it read or left out. Only the reader uses what is inside.</summary>
public sealed class GuideMore
{
    internal GuideMore(MediaWikiSite site, IReadOnlyList<(string Title, int Count)> related, IReadOnlySet<string> known)
    {
        Site = site;
        Related = related;
        Known = known;
    }

    internal MediaWikiSite Site { get; }
    internal IReadOnlyList<(string Title, int Count)> Related { get; }
    internal IReadOnlySet<string> Known { get; }

    /// <summary>How many linked titles weren't read.</summary>
    public int Count => Related.Count;
}

/// <summary>How a read chose its wiki pages: the titles it looked at, those left out because their brackets name another game
/// of the series, those it chose to read (it reads the first ones, up to its caps) and the batches a Thinking pool member
/// chose.</summary>
public sealed record GuidePageChoice(int Titles, int OtherGames, int Chosen, int Asked);

/// <summary>What reading up found: the pages read, the sites they came from, how many pages couldn't be read, the bytes
/// downloaded and, when nothing usable was read, the problem in a few plain words; how a wiki's pages were chosen, and what is
/// left to read more of it.</summary>
public sealed record GuideBuildOutcome(IReadOnlyList<GuidePage> Pages, IReadOnlyList<string> Sites, int Failed, long Bytes, string? Problem,
    GuidePageChoice? Choice = null, GuideMore? More = null);

/// <summary>Reads up on an app on the web (<see cref="WebGuideBuilder"/>; checks use fixtures).</summary>
public interface IGuideBuilder
{
    /// <summary>Reads the app's pages within the request's limits. Progress reports are a few plain words ("Reading page 3"),
    /// never a link.</summary>
    Task<GuideBuildOutcome> BuildAsync(GuideBuildRequest request, IProgress<string>? progress, CancellationToken cancellationToken);
}

/// <summary>An app's key: its name in lower-case letters and digits joined with '-', at most 60 characters ("Elden Ring" →
/// "elden-ring"); "app" when the name has no letter or digit.</summary>
public static class AppGuideKeys
{
    public const int MaximumLength = 60;

    public static string Of(string name)
    {
        var key = new System.Text.StringBuilder();
        foreach (var c in name ?? "")
        {
            if (key.Length >= MaximumLength) break;
            if (char.IsAsciiLetterOrDigit(c)) key.Append(char.ToLowerInvariant(c));
            else if (key.Length > 0 && key[^1] != '-') key.Append('-');
        }
        var made = key.ToString().Trim('-');
        return made.Length == 0 ? "app" : made;
    }
}
