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
    /// <summary>The owner said no when Martlet offered to read up on it: Martlet doesn't offer again.</summary>
    public bool Declined { get; init; }
    /// <summary>When its guide was last made, or null when it has none yet.</summary>
    public DateTimeOffset? BuiltAt { get; init; }
    public int Pages { get; init; }
    public int Chunks { get; init; }
    public long Bytes { get; init; }
    /// <summary>Why the last try to make its guide failed, in a few plain words, or null.</summary>
    public string? Problem { get; init; }
}

/// <summary>The guide library's own settings and its apps, kept with the guides (guides\library.json), like lorebooks keep
/// theirs. App guides are off until the owner turns them on: reading up on an app sends its name to a search engine, and the
/// sites it reads see the requests.</summary>
public sealed record AppGuideLibrary
{
    public const bool DefaultOn = false;
    public const bool DefaultAskWhenStarted = true;

    public bool On { get; init; } = DefaultOn;
    /// <summary>Whether Martlet offers to read up on a game or app when the user starts one that has no guide yet.</summary>
    public bool AskWhenStarted { get; init; } = DefaultAskWhenStarted;
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
}

/// <summary>The caps of making one guide: pages read, bytes downloaded, sites, the wait between two requests to one site and the
/// time it may take.</summary>
public sealed record GuideBuildLimits
{
    public int Pages { get; init; } = 60;
    public long Bytes { get; init; } = 12_000_000;
    public int Sites { get; init; } = 2;
    public TimeSpan Delay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan Time { get; init; } = TimeSpan.FromMinutes(15);
}

/// <summary>What to read up on: the app's name and the pages to start from (empty: find the app's wiki).</summary>
public sealed record GuideBuildRequest(string Name, IReadOnlyList<string> Sites, GuideBuildLimits? Limits = null);

/// <summary>What reading up found: the pages read, the sites they came from, how many pages couldn't be read, the bytes
/// downloaded and, when nothing usable was read, the problem in a few plain words.</summary>
public sealed record GuideBuildOutcome(IReadOnlyList<GuidePage> Pages, IReadOnlyList<string> Sites, int Failed, long Bytes, string? Problem);

/// <summary>Reads up on an app on the web (<see cref="WebGuideBuilder"/>; checks use fixtures).</summary>
public interface IGuideBuilder
{
    /// <summary>Reads the app's pages within the request's limits. Progress reports are a few plain words ("Reading pages (3 of
    /// at most 60)"), never a link.</summary>
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
