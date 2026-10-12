using System.Collections.Concurrent;
using System.Diagnostics;
using Martlet.Core.Settings;

namespace Martlet.Conversation.Guides;

/// <summary>What one try to read up on an app came to: its key and name, the pages, sections and bytes kept, the sites read, the
/// pages that couldn't be read and how long it took; or the <see cref="Problem"/> in a few plain words (<see cref="Busy"/>: another
/// app is being read up on now). <see cref="Choice"/>: how a wiki's pages were chosen; <see cref="More"/>: what is left to read
/// more of the wiki (<see cref="AppGuideService.ReadMoreAsync"/>).</summary>
public sealed record AppGuideBuild(string Key, string Name, int Pages, int Chunks, long Bytes, IReadOnlyList<string> Sites, int Failed,
    long Downloaded, TimeSpan Took, string? Problem, bool Busy = false, GuidePageChoice? Choice = null, GuideMore? More = null)
{
    public bool Built => Problem is null;
}

/// <summary>The program in front, as the desktop sees it: its name (the program file's description, else its file name), its file
/// name without .exe, whether it is a game (<c>PcActivity.Classify</c>) and whether it fills its screen, and the library entry it
/// matches (by key, name or program names; null when none does).</summary>
public sealed record AppGuideFront(string Program, string File, bool Game, bool FullScreen, AppGuideEntry? Entry);

/// <summary>An app Martlet may read up on, on its own, now: its name (readable: "Abiotic Factor" for the program
/// AbioticFactor-Win64-Shipping) and key, whether it is a game (else an app on the list) and the names Windows gives its program,
/// which join its entry so its guide is used while it is in front.</summary>
public sealed record AppGuideOfferCandidate(string Name, string Key, bool Game, IReadOnlyList<string>? Programs = null);

/// <summary>What a guide had for one message: the app, the notes (null when no section matched well enough), how many sections
/// matched at all and went, the best relevance, how long the search took and whether the guide's index was ready (it is never
/// waited for).</summary>
public sealed record AppGuideRecall(string App, string? Notes, int Matched, int Used, double Best, TimeSpan Took, bool Ready, string? Brief = null);

/// <summary>The guide library of this PC (Companion › App guides, docs/APP_GUIDES.md): the store's library kept in memory, a
/// searchable index only for the guides that can be used now (the app in front, an app named in the conversation, the guide
/// being read), built off the reply path (<see cref="Warm"/>) and never waited for on it, and dropped again when not used for
/// <see cref="IndexIdleTime"/> (<see cref="Trim"/>); reading up one app at a time, the program in front and the rule for reading
/// up on a game in front on its own (<see cref="Offer"/>). Thread-safe; <see cref="Changed"/> is raised on any thread.</summary>
public sealed class AppGuideService : IDisposable
{
    /// <summary>How many times Martlet starts on one app in a session at most, when its start was dropped before it could.</summary>
    public const int MaximumOffers = 3;

    private readonly IGuideBuilder builder;
    private readonly IDisposable? resources;
    private readonly TimeProvider clock;
    private readonly GuideBuildLimits? limits;
    private readonly object gate = new();
    private readonly SemaphoreSlim saving = new(1, 1);
    private readonly SemaphoreSlim reading = new(1, 1);
    private readonly ConcurrentDictionary<string, IGuideIndex> indexes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> lastUsed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> warming = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Times, Func<bool>? Dropped)> offered = new(StringComparer.Ordinal);
    // Off until LoadAsync reads the saved choice, so no reply gets the guide tools before Martlet knows the owner wants them.
    private AppGuideLibrary library = new() { On = false };
    private AppGuideFront? front;
    private (string Key, string Name, string? Progress)? building;
    private CancellationTokenSource? stopping;
    private readonly ConcurrentDictionary<string, string> unreadable = new(StringComparer.Ordinal);
    private string? loadProblem;
    private bool loaded, disposed;
    private int revision;

    /// <param name="builder">Reads up on an app (<see cref="WebGuideBuilder"/>; checks use fixtures).</param>
    /// <param name="resources">Disposed with the service (the web client the builder uses).</param>
    /// <param name="limits">The caps of reading up (null: <see cref="GuideBuildLimits"/>' defaults).</param>
    public AppGuideService(IAppGuideStore store, IGuideBuilder builder, IDisposable? resources = null, TimeProvider? clock = null,
        GuideBuildLimits? limits = null)
    {
        Store = store ?? throw new ArgumentNullException(nameof(store));
        this.builder = builder ?? throw new ArgumentNullException(nameof(builder));
        this.resources = resources;
        this.clock = clock ?? TimeProvider.System;
        this.limits = limits;
    }

    public IAppGuideStore Store { get; }

    /// <summary>Raised on any thread when the library, the app in front, reading up or an index changes.</summary>
    public event Action? Changed;

    /// <summary>A guide was read and kept (reading up or Read again). Raised on the build's thread; handlers must return quickly.</summary>
    public event Action<AppGuideBuild>? Built;

    /// <summary>The library as last read or saved (an empty one, with App guides off, until <see cref="LoadAsync"/>).</summary>
    public AppGuideLibrary Library { get { lock (gate) return library; } }

    /// <summary>Goes up by one each time the library changes (the page draws its list again only then).</summary>
    public int Revision { get { lock (gate) return revision; } }

    public bool Loaded { get { lock (gate) return loaded; } }

    /// <summary>Whether App guides are on (Companion › App guides; on by default, off until <see cref="LoadAsync"/>).</summary>
    public bool On => Library.On;

    /// <summary>The program in front, or null before the first look (or while App guides are off).</summary>
    public AppGuideFront? Front { get { lock (gate) return front; } }

    /// <summary>The app being read up on now (its key and name) and what it is doing, or null.</summary>
    public (string Key, string Name, string? Progress)? Building { get { lock (gate) return building; } }

    /// <summary>Why the library couldn't be read at <see cref="LoadAsync"/> (a few plain words), or null.</summary>
    public string? LoadProblem { get { lock (gate) return loadProblem; } }

    /// <summary>Why <paramref name="key"/>'s guide file couldn't be read (a few plain words), or null.</summary>
    public string? GuideProblem(string key) => unreadable.TryGetValue(key, out var why) ? why : null;

    /// <summary>How many indexes are ready to search.</summary>
    public int ReadyIndexes => indexes.Count;

    /// <summary>Whether <paramref name="key"/>'s index is ready to search.</summary>
    public bool IsReady(string key) => indexes.ContainsKey(key);

    /// <summary>Reads the library and starts building the index of the app in front, if it has a guide (off the caller's thread).
    /// Other guides are loaded only when they can be used (<see cref="Warm"/>).</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        AppGuideLibrary read;
        string? problem = null;
        try { read = await Store.LoadLibraryAsync(cancellationToken).ConfigureAwait(false); }
        catch (AppGuideFileException error)
        {
            // The store never resets it, and Martlet can't read the owner's choice: App guides stay off until the owner turns them
            // on again (a new library keeps a copy of the old one, and a newer Martlet's library is never replaced).
            read = new() { On = false };
            problem = Describe(error);
        }
        lock (gate)
        {
            library = read;
            loaded = true;
            loadProblem = problem;
            revision++;
        }
        Changed?.Invoke();
        if (Front?.Entry is { } seen && read.Apps.FirstOrDefault(a => a.Key == seen.Key) is { BuiltAt: not null } inFront) Warm(inFront.Key);
    }

    // ---------- the library ----------

    /// <summary>Changes the library one change at a time, from what the store holds now, and keeps the result.</summary>
    public async Task<AppGuideLibrary> UpdateAsync(Func<AppGuideLibrary, AppGuideLibrary> change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        await saving.WaitAsync(cancellationToken).ConfigureAwait(false);
        AppGuideLibrary next;
        try
        {
            next = change(await Store.LoadLibraryAsync(cancellationToken).ConfigureAwait(false));
            await Store.SaveLibraryAsync(next, cancellationToken).ConfigureAwait(false);
            Keep(next);
        }
        finally { saving.Release(); }
        return next;
    }

    public Task SetOnAsync(bool on, CancellationToken cancellationToken = default) =>
        UpdateAsync(l => l with { On = on }, cancellationToken);

    public Task SetReadUpWhenStartedAsync(bool read, CancellationToken cancellationToken = default) =>
        UpdateAsync(l => l with { ReadUpWhenStarted = read }, cancellationToken);

    /// <summary>Adds an app to the list (or changes the one with the same key): its name, program names and start pages.</summary>
    public async Task<AppGuideEntry> AddAsync(string name, IReadOnlyList<string>? programs, IReadOnlyList<string>? sites,
        CancellationToken cancellationToken = default)
    {
        var clean = AppGuideTools.CleanName(name);
        if (clean.Length == 0) throw new ArgumentException("An app needs a name with a letter or digit.", nameof(name));
        var key = KeyOf(clean);
        var names = (programs ?? []).Select(AppGuideTools.CleanName).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray();
        var starts = (sites ?? []).Select(AppGuideTools.Site).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(AppGuideTools.MaxSites).ToArray();
        AppGuideEntry? added = null;
        await UpdateAsync(l =>
        {
            var old = l.Apps.FirstOrDefault(a => a.Key == key);
            added = (old ?? new AppGuideEntry { Key = key, Name = clean }) with { Name = clean, Programs = names, Sites = starts };
            return l with { Apps = [.. l.Apps.Where(a => a.Key != key), added] };
        }, cancellationToken).ConfigureAwait(false);
        return added!;
    }

    /// <summary>Deletes an app's guide and its entry. False while it is being read up on.</summary>
    public async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        if (Building?.Key == key) return false;
        await saving.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Store.DeleteGuideAsync(key, cancellationToken).ConfigureAwait(false);
            indexes.TryRemove(key, out _);
            unreadable.TryRemove(key, out _);
            Keep(await Store.LoadLibraryAsync(cancellationToken).ConfigureAwait(false));
        }
        finally { saving.Release(); }
        return true;
    }

    /// <summary>The user doesn't want Martlet to read up on <paramref name="name"/>: it never reads up on it on its own again
    /// (the entry is added when the library doesn't have it yet).</summary>
    public async Task<AppGuideEntry> DeclineAsync(string name, CancellationToken cancellationToken = default)
    {
        var clean = AppGuideTools.CleanName(name);
        if (clean.Length == 0) throw new ArgumentException("An app needs a name with a letter or digit.", nameof(name));
        AppGuideEntry? declined = null;
        await UpdateAsync(l =>
        {
            var old = Match(l, [clean]);
            declined = (old ?? WithOffered(new AppGuideEntry { Key = KeyOf(clean), Name = clean })) with { Declined = true };
            return l with { Apps = [.. l.Apps.Where(a => a.Key != declined.Key), declined] };
        }, cancellationToken).ConfigureAwait(false);
        return declined!;
    }

    /// <summary>Allow again: Martlet may read up on <paramref name="key"/> on its own again (this session too).</summary>
    public async Task AskAgainAsync(string key, CancellationToken cancellationToken = default)
    {
        await UpdateAsync(l => l with { Apps = [.. l.Apps.Select(a => a.Key == key ? a with { Declined = false } : a)] }, cancellationToken)
            .ConfigureAwait(false);
        lock (gate) offered.Remove(key);
    }

    /// <summary>The entry <paramref name="names"/> (program or app names) mean: the same key, or the same words as its name or one
    /// of its program names, ignoring case and punctuation ("ELDEN RING™" is "Elden Ring").</summary>
    public static AppGuideEntry? Match(AppGuideLibrary library, IEnumerable<string?> names)
    {
        ArgumentNullException.ThrowIfNull(library);
        var wanted = names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => Slug(n!)).Where(s => s.Length > 0).ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0) return null;
        return library.Apps.FirstOrDefault(a => wanted.Contains(a.Key) || wanted.Contains(Slug(a.Name)) ||
            a.Programs.Any(p => wanted.Contains(Slug(p))));
    }

    /// <summary>The entry with a guide whose name (or a program name) the user's <paramref name="words"/> say, as whole words; the
    /// longest name wins.</summary>
    public static AppGuideEntry? Named(AppGuideLibrary library, string words)
    {
        ArgumentNullException.ThrowIfNull(library);
        var said = "-" + Slug(words) + "-";
        if (said.Length <= 2) return null;
        return library.Apps.Where(a => a.BuiltAt is not null)
            .Select(a => (Entry: a, Length: new[] { a.Name }.Concat(a.Programs).Select(Slug)
                .Where(s => s.Length >= 3 && said.Contains("-" + s + "-", StringComparison.Ordinal)).DefaultIfEmpty("").Max(s => s!.Length)))
            .Where(m => m.Length > 0).OrderByDescending(m => m.Length).Select(m => m.Entry).FirstOrDefault();
    }

    /// <summary>Lower-case letters and digits joined with '-' (no length cap): how names are compared.</summary>
    public static string Slug(string text)
    {
        var slug = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c)) slug.Append(char.ToLowerInvariant(c));
            else if (slug.Length > 0 && slug[^1] != '-') slug.Append('-');
        }
        return slug.ToString().Trim('-');
    }

    /// <summary>The key of an app named <paramref name="name"/>: <see cref="AppGuideKeys.Of"/>, plus a short hash of its words
    /// when the name has letters or digits outside ASCII ("原神", "Ведьмак 3"), so such apps don't share a key.</summary>
    public static string KeyOf(string name)
    {
        var key = AppGuideKeys.Of(name);
        if ((name ?? "").All(c => !char.IsLetterOrDigit(c) || char.IsAscii(c))) return key;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Slug(name!))))[..8]
            .ToLowerInvariant();
        return key[..Math.Min(key.Length, AppGuideKeys.MaximumLength - 9)].TrimEnd('-') + "-" + hash;
    }

    /// <summary>A name to read up on from the name Windows gives a program: build words a game's program file carries are left
    /// out ("AbioticFactor-Win64-Shipping" is "AbioticFactor"), '_' is a space, and a name in one word is split where its
    /// capitals start words ("AbioticFactor" is "Abiotic Factor", "DOOMEternal" is "DOOM Eternal"). Empty without a letter or
    /// digit.</summary>
    public static string Readable(string? program)
    {
        var name = AppGuideTools.CleanName(program);
        if (name.Length == 0) return "";
        var kept = name.Replace('_', ' ');
        for (var at = kept.LastIndexOfAny(['-', '.', ' ']); at > 0 && BuildWords.Contains(kept[(at + 1)..]); at = kept.LastIndexOfAny(['-', '.', ' ']))
            kept = kept[..at].TrimEnd(' ', '-', '.');
        if (!kept.Contains(' ', StringComparison.Ordinal) && kept.Any(char.IsLower))
        {
            var split = new System.Text.StringBuilder(kept.Length + 8);
            for (var i = 0; i < kept.Length; i++)
            {
                var c = kept[i];
                if (i > 0 && char.IsUpper(c) && (char.IsLower(kept[i - 1]) || char.IsDigit(kept[i - 1]) ||
                    char.IsUpper(kept[i - 1]) && i + 1 < kept.Length && char.IsLower(kept[i + 1])))
                    split.Append(' ');
                split.Append(c);
            }
            kept = split.ToString();
        }
        return AppGuideTools.CleanName(kept) is { Length: > 0 } readable ? readable : name;
    }

    // Words a game's program file name carries about how it was built, never part of the game's name.
    private static readonly HashSet<string> BuildWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "shipping", "win64", "win32", "wingdk", "wingrts", "x64", "x86", "dx11", "dx12", "d3d11", "d3d12", "vulkan", "exe"
    };

    // What Martlet calls the program in front that matches no entry: the readable form of its name (else its file name).
    private static string FrontName(AppGuideFront seen) => Readable(seen.Program.Length > 0 ? seen.Program : seen.File);

    // The names Windows gives the program in front, which join the entry of an app read up on while it is in front.
    private static IReadOnlyList<string> FrontPrograms(AppGuideFront seen) =>
        [.. new[] { seen.Program, seen.File }.Select(AppGuideTools.CleanName).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];

    // A new entry for the app Martlet read up on while it is in front and matches no entry: the user's words may name it in their
    // own way ("Elden Ring" for the program eldenring), so the program's names join the entry and it is recognized from then on.
    private AppGuideEntry WithOffered(AppGuideEntry entry)
    {
        lock (gate)
        {
            if (front is not { Entry: null } seen || !offered.ContainsKey(FrontKey(seen))) return entry;
            return entry with { Programs = Merged(entry.Programs, FrontPrograms(seen)) };
        }
    }

    private static IReadOnlyList<string> Merged(IReadOnlyList<string> programs, IEnumerable<string>? more) =>
        [.. programs.Concat(more ?? []).Select(AppGuideTools.CleanName).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(8)];

    private static string FrontKey(AppGuideFront seen) => KeyOf(FrontName(seen));

    // ---------- the app in front and the offer ----------

    /// <summary>What the desktop saw in front (its program's name and file, whether it is a game and fills its screen; null when
    /// nothing is). The index of a matching app's guide starts building when it isn't ready.</summary>
    public void See(string? program, string? file, bool game, bool fullScreen)
    {
        AppGuideFront? now;
        bool changed;
        lock (gate)
        {
            var before = front;
            now = string.IsNullOrWhiteSpace(program) && string.IsNullOrWhiteSpace(file) ? null
                : new AppGuideFront(program?.Trim() ?? "", file?.Trim() ?? "", game, fullScreen, Match(library, [program, file]));
            front = now;
            changed = before != now;
        }
        if (now?.Entry is { BuiltAt: not null } entry) Warm(entry.Key);
        if (changed) Changed?.Invoke();
        Trim();
    }

    /// <summary>Forgets the app in front (App guides turned off, or the watch stopped) and drops every index but the guide being
    /// read: nothing can use them now.</summary>
    public void Forget()
    {
        lock (gate)
        {
            if (front is null && indexes.IsEmpty) return;
            front = null;
        }
        Trim(all: true);
        Changed?.Invoke();
    }

    /// <summary>How long an index that isn't the app in front's or the guide being read's stays loaded after its last use.</summary>
    public static TimeSpan IndexIdleTime { get; } = TimeSpan.FromMinutes(10);

    /// <summary>The most indexes kept loaded besides the app in front's and the guide being read's (the most recently used).</summary>
    public const int MaximumOtherIndexes = 2;

    /// <summary>Drops the indexes that can't be used now, so a whole-wiki guide (up to about 0.9 GB with its index) doesn't stay
    /// in memory while a game runs: every index but the app in front's and the guide being read's that wasn't used for
    /// <see cref="IndexIdleTime"/>, and beyond the <see cref="MaximumOtherIndexes"/> most recently used (all of them with
    /// <paramref name="all"/>). The App guides watch calls it with each look (every few seconds), off the reply path; a dropped
    /// index is built again (<see cref="Warm"/>) when its app comes to the front or is named.</summary>
    public void Trim(bool all = false)
    {
        string? inFront, read;
        lock (gate) (inFront, read) = (front?.Entry?.Key, building?.Key);
        var now = clock.GetUtcNow();
        var others = indexes.Keys.Where(k => k != read && (all || k != inFront))
            .Select(k => (Key: k, At: lastUsed.GetValueOrDefault(k))).OrderByDescending(k => k.At).ToList();
        var dropped = 0;
        for (var i = 0; i < others.Count; i++)
        {
            if (!all && i < MaximumOtherIndexes && now - others[i].At < IndexIdleTime) continue;
            if (indexes.TryRemove(others[i].Key, out var index))
            {
                dropped += index.Count;
                lastUsed.TryRemove(others[i].Key, out _);
            }
        }
        if (dropped == 0) return;
        // Ask for the memory back now (a background collection), not at some later collection: a game may need it.
        if (dropped >= 10_000) GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
        Changed?.Invoke();
    }

    private void Touch(string key) => lastUsed[key] = clock.GetUtcNow();

    /// <summary>The app Martlet may read up on, on its own, now, or null: App guides and Read up when I start a game or app are
    /// on, a game (or an app on the list) is in front with no guide, nothing is being read up on, the user never said no to it,
    /// its last try didn't fail (Read up now on the page tries again) and Martlet hasn't started on it this session (one whose
    /// start was dropped may come again, at most <see cref="MaximumOffers"/> times).</summary>
    public AppGuideOfferCandidate? Offer()
    {
        lock (gate)
        {
            if (!library.On || !library.ReadUpWhenStarted || front is not { } seen || building is not null) return null;
            if (seen.Entry is null && !seen.Game) return null;
            var name = seen.Entry?.Name ?? FrontName(seen);
            if (name.Length == 0) return null;
            var entry = seen.Entry ?? Match(library, [name]);
            var key = entry?.Key ?? KeyOf(name);
            if (entry is { } known && (known.BuiltAt is not null || known.Declined || known.Problem is not null)) return null;
            if (offered.TryGetValue(key, out var asked) && (asked.Dropped?.Invoke() != true || asked.Times >= MaximumOffers)) return null;
            return new(entry?.Name ?? name, key, seen.Game, FrontPrograms(seen));
        }
    }

    /// <summary>Martlet started on <paramref name="key"/> (<paramref name="dropped"/> says, later, whether that was dropped before
    /// it could, so it may try again).</summary>
    public void Offered(string key, Func<bool>? dropped = null)
    {
        lock (gate) offered[key] = (offered.GetValueOrDefault(key).Times + 1, dropped);
    }

    // ---------- reading up ----------

    /// <summary>Reads up on <paramref name="name"/> (one app at a time): reads its pages from <paramref name="sites"/> (empty: the
    /// entry's own, else Martlet finds its wiki), cuts them into sections, keeps the guide and builds its index. The app is on the
    /// list from the start, with <paramref name="programs"/> (the names Windows gives its program) among its program names. A
    /// failure is kept on the entry as its problem. <paramref name="progress"/> gets a few plain words now and then.</summary>
    public async Task<AppGuideBuild> BuildAsync(string name, IReadOnlyList<string>? sites, IProgress<string>? progress,
        CancellationToken cancellationToken = default, IReadOnlyList<string>? programs = null)
    {
        var clean = AppGuideTools.CleanName(name);
        var started = clock.GetTimestamp();
        if (clean.Length == 0) return new(KeyOf(name ?? ""), name ?? "", 0, 0, 0, [], 0, 0, TimeSpan.Zero, "it has no name");
        var known = Match(Library, [clean]);
        var key = known?.Key ?? KeyOf(clean);
        var shown = known?.Name ?? clean;
        if (!await reading.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return new(key, shown, 0, 0, 0, [], 0, 0, TimeSpan.Zero, $"Martlet is already reading up on {Building?.Name ?? "another app"}", Busy: true);
        try
        {
            // The page draws its buttons again when reading up starts and ends (Revision).
            lock (gate)
            {
                building = (key, shown, "Starting");
                revision++;
            }
            Changed?.Invoke();
            var given = (sites ?? []).Select(AppGuideTools.Site).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(AppGuideTools.MaxSites).ToArray();
            // The entry is on the list from the start (with the pages the owner gave), so the page shows what is being read.
            AppGuideEntry entry;
            try
            {
                entry = (await UpdateAsync(l =>
                {
                    var old = l.Apps.FirstOrDefault(a => a.Key == key);
                    var next = (old ?? WithOffered(new AppGuideEntry { Key = key, Name = shown })) with
                    {
                        Sites = given.Length > 0 ? given : old?.Sites ?? [], Declined = false
                    };
                    if (programs is { Count: > 0 }) next = next with { Programs = Merged(next.Programs, programs) };
                    return l with { Apps = [.. l.Apps.Where(a => a.Key != key), next] };
                }, cancellationToken).ConfigureAwait(false)).Apps.First(a => a.Key == key);
            }
            catch (Exception error) when (Refusal(error) is { } why)
            {
                return new(key, shown, 0, 0, 0, [], 0, 0, clock.GetElapsedTime(started), why);
            }
            var said = new Said(text =>
            {
                lock (gate) if (building?.Key == key) building = (key, shown, text);
                progress?.Report(text);
                Changed?.Invoke();
            });
            // What research added to the old guide (GuideAreaResearch) is kept when the wiki is read again.
            IReadOnlyList<GuideChunk> researched = [];
            if (entry.BuiltAt is not null)
            {
                try
                {
                    researched = [.. ((await Store.LoadGuideAsync(key, cancellationToken).ConfigureAwait(false))?.Chunks ?? [])
                        .Where(GuideAreaResearch.IsResearched)];
                }
                catch (Exception error) when (error is not OperationCanceledException && error is not OutOfMemoryException) { }
            }
            using var stop = new CancellationTokenSource();
            lock (gate) stopping = stop;
            // A long read keeps what it read every few minutes: the guide can be searched as it grows, and an end before the last
            // page (a canceled job, Martlet closed) keeps it. The guide store full ends reading.
            async Task<bool> KeepSoFarAsync(GuideBuildOutcome sofar, CancellationToken token)
            {
                said.Report("Saving what it read so far");
                var kept = await KeepGuideAsync(key, shown, sofar, researched, token).ConfigureAwait(false);
                return kept.Refused is null && !kept.Full;
            }
            GuideBuildOutcome outcome;
            try
            {
                outcome = await builder.BuildAsync(new(shown, entry.Sites, limits) { Keep = KeepSoFarAsync, KeepEvery = KeepEvery, Stop = stop.Token },
                    said, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                outcome = new([], [], 0, 0, "reading up failed on this PC (" + error.GetType().Name + ")");
            }
            finally
            {
                lock (gate) stopping = null;
            }
            var problem = outcome.Pages.Count == 0 ? outcome.Problem ?? "no page about it could be read" : null;
            (AppGuide? Guide, AppGuideEntry? Entry, bool Full, string? Refused) saved = default;
            if (problem is null)
            {
                said.Report("Saving the guide");
                saved = await KeepGuideAsync(key, shown, outcome, researched, cancellationToken).ConfigureAwait(false);
                problem = saved.Refused ?? (saved.Guide is not { Chunks.Count: > 0 } ? "the pages it read had no text to keep" : null);
            }
            if (problem is not null)
            {
                await KeepProblemAsync(key, problem).ConfigureAwait(false);
                return new(key, shown, 0, 0, 0, outcome.Sites, outcome.Failed, outcome.Bytes, clock.GetElapsedTime(started), problem, Choice: outcome.Choice);
            }
            var guide = saved.Guide!;
            var built = new AppGuideBuild(key, shown, guide.Sources.Count, guide.Chunks.Count, saved.Entry?.Bytes ?? 0, guide.Sites, outcome.Failed,
                outcome.Bytes, clock.GetElapsedTime(started), null, Choice: outcome.Choice, More: outcome.More);
            try { Built?.Invoke(built); }
            catch (Exception error) when (error is not OutOfMemoryException) { }
            return built;
        }
        finally
        {
            lock (gate)
            {
                building = null;
                revision++;
            }
            reading.Release();
            Changed?.Invoke();
            Trim();
        }
    }

    /// <summary>How often a read keeps what it read so far, by default.</summary>
    public static TimeSpan DefaultKeepEvery { get; } = TimeSpan.FromMinutes(3);

    /// <summary>How often a read keeps what it read so far (checks pass a shorter time).</summary>
    public TimeSpan KeepEvery { get; init; } = DefaultKeepEvery;

    /// <summary>The caps of reading more (<see cref="GuideBuildLimits.More"/>; checks pass a shorter pause).</summary>
    public GuideBuildLimits MoreLimits { get; init; } = GuideBuildLimits.More;

    /// <summary>Reads more of <paramref name="key"/>'s wiki after its first read: the pages its pages link to that
    /// <paramref name="choose"/> (a Thinking pool member) chooses, and the links of those, within <see cref="MoreLimits"/>, added to
    /// its guide. It waits a little for a read that runs to end; the page shows it like a read, and Stop reading ends it and keeps
    /// what it read. The result's counts are what it added.</summary>
    public async Task<AppGuideBuild> ReadMoreAsync(string key, GuideMore more, Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<string>?>> choose,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(more);
        ArgumentNullException.ThrowIfNull(choose);
        var started = clock.GetTimestamp();
        var entry = Library.Apps.FirstOrDefault(a => a.Key == key);
        if (entry is not { BuiltAt: not null }) return new(key, entry?.Name ?? key, 0, 0, 0, [], 0, 0, TimeSpan.Zero, "there's no guide about it yet");
        // The read that found the links lets go just after it says it is done.
        if (!await reading.WaitAsync(TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false))
            return new(key, entry.Name, 0, 0, 0, [], 0, 0, TimeSpan.Zero, $"Martlet is reading up on {Building?.Name ?? "another app"}", Busy: true);
        try
        {
            lock (gate)
            {
                building = (key, entry.Name, "Choosing more pages to read");
                revision++;
            }
            Changed?.Invoke();
            var said = new Said(text =>
            {
                lock (gate) if (building?.Key == key) building = (key, entry.Name, "Reading more: " + text);
                progress?.Report(text);
                Changed?.Invoke();
            });
            using var stop = new CancellationTokenSource();
            lock (gate) stopping = stop;
            GuideBuildOutcome outcome;
            try
            {
                outcome = await builder.BuildAsync(new(entry.Name, [], MoreLimits) { More = more, Choose = choose, Stop = stop.Token }, said,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                outcome = new([], [], 0, 0, "reading more failed on this PC (" + error.GetType().Name + ")");
            }
            finally
            {
                lock (gate) stopping = null;
            }
            if (outcome.Pages.Count == 0)
                return new(key, entry.Name, 0, 0, 0, outcome.Sites, outcome.Failed, outcome.Bytes, clock.GetElapsedTime(started),
                    outcome.Problem ?? "no more pages were worth reading", Choice: outcome.Choice);
            said.Report("Saving the guide");
            var (pages, chunks, bytes, refused) = await AddPagesAsync(key, entry.Name, outcome, cancellationToken).ConfigureAwait(false);
            return new(key, entry.Name, pages, chunks, bytes, outcome.Sites, outcome.Failed, outcome.Bytes, clock.GetElapsedTime(started),
                refused ?? (pages == 0 ? "the pages it read were in the guide already" : null), Choice: outcome.Choice);
        }
        finally
        {
            lock (gate)
            {
                building = null;
                revision++;
            }
            reading.Release();
            Changed?.Invoke();
            Trim();
        }
    }

    // Adds the pages read more to the app's guide (pages it has already are left out, as are pages past the store's bounds),
    // keeps it and builds its index again: the pages and sections added and the guide's size, or why the store refused it.
    private async Task<(int Pages, int Chunks, long Bytes, string? Refused)> AddPagesAsync(string key, string name, GuideBuildOutcome outcome,
        CancellationToken token)
    {
        AppGuide? old;
        try { old = await Store.LoadGuideAsync(key, token).ConfigureAwait(false); }
        catch (Exception error) when (Refusal(error) is { } why) { return (0, 0, 0, why); }
        if (old is null) return (0, 0, 0, "it has no guide");
        var have = old.Sources.Select(s => s.Url).ToHashSet(StringComparer.Ordinal);
        var fresh = outcome with { Pages = [.. outcome.Pages.Where(p => !have.Contains(p.Url))] };
        var research = old.Chunks.Where(GuideAreaResearch.IsResearched).ToList();
        var wiki = old.Chunks.Where(c => !GuideAreaResearch.IsResearched(c)).ToList();
        var pages = FileAppGuideStore.MaximumSources - old.Sources.Count;
        while (true)
        {
            var added = GuideChunker.Guide(key, name, fresh, clock.GetUtcNow(), Math.Max(0, pages),
                Math.Max(0, FileAppGuideStore.MaximumChunks - old.Chunks.Count));
            if (added.Sources.Count == 0) return (0, 0, 0, null);
            // The research sections stay last, as a read keeps them.
            var next = old with
            {
                Sites = [.. old.Sites.Union(outcome.Sites, StringComparer.OrdinalIgnoreCase)], Sources = [.. old.Sources, .. added.Sources],
                Chunks = [.. wiki, .. added.Chunks, .. research]
            };
            AppGuideLibrary read;
            await saving.WaitAsync(token).ConfigureAwait(false);
            try
            {
                try { await Store.SaveGuideAsync(next, token).ConfigureAwait(false); }
                catch (AppGuideTooLargeException) when (added.Sources.Count > 1)
                {
                    pages = added.Sources.Count * 4 / 5;
                    continue;
                }
                catch (Exception error) when (Refusal(error) is { } why) { return (0, 0, 0, why); }
                read = await Store.LoadLibraryAsync(token).ConfigureAwait(false);
            }
            finally { saving.Release(); }
            var saved = read.Apps.FirstOrDefault(a => a.Key == key);
            Touch(key);
            indexes[key] = WarmedUp(GuideIndex.Build(next.Chunks, [name, .. saved?.Programs ?? []]));
            Keep(read);
            return (added.Sources.Count, added.Chunks.Count, saved?.Bytes ?? 0, null);
        }
    }

    /// <summary>Ends reading up now and keeps what was read (Stop reading on the App guides page). False when Martlet isn't
    /// reading up.</summary>
    public bool StopReading()
    {
        CancellationTokenSource? source;
        lock (gate) source = stopping;
        if (source is null) return false;
        try { source.Cancel(); }
        catch (ObjectDisposedException) { return false; }
        return true;
    }

    // Keeps what was read as the app's guide, the pages read first as far as the store holds them (Full: pages were left out),
    // and makes its index. Refused: why the store didn't keep it. A guide with no chunks isn't kept.
    private async Task<(AppGuide? Guide, AppGuideEntry? Entry, bool Full, string? Refused)> KeepGuideAsync(string key, string name,
        GuideBuildOutcome outcome, IReadOnlyList<GuideChunk> researched, CancellationToken token)
    {
        var pages = FileAppGuideStore.MaximumSources;
        while (true)
        {
            var guide = GuideChunker.Guide(key, name, outcome, clock.GetUtcNow(), pages, FileAppGuideStore.MaximumChunks - researched.Count);
            var full = guide.Sources.Count < outcome.Pages.Count;
            if (guide.Chunks.Count == 0) return (guide, null, full, null);
            if (researched.Count > 0) guide = guide with { Chunks = [.. guide.Chunks, .. researched] };
            AppGuideLibrary read;
            await saving.WaitAsync(token).ConfigureAwait(false);
            try
            {
                try { await Store.SaveGuideAsync(guide, token).ConfigureAwait(false); }
                // A file larger than the store keeps: fewer pages, the ones read last left out.
                catch (AppGuideTooLargeException) when (guide.Sources.Count > 1)
                {
                    pages = guide.Sources.Count * 4 / 5;
                    continue;
                }
                // The store's bounds (a full library) or a file it can't read: the guide isn't kept.
                catch (Exception error) when (Refusal(error) is { } why) { return (null, null, full, why); }
                read = await Store.LoadLibraryAsync(token).ConfigureAwait(false);
            }
            finally { saving.Release(); }
            var saved = read.Apps.FirstOrDefault(a => a.Key == key);
            unreadable.TryRemove(key, out _);
            Touch(key);
            indexes[key] = WarmedUp(GuideIndex.Build(guide.Chunks, [name, .. saved?.Programs ?? []]));
            Keep(read);
            return (guide, saved, full, null);
        }
    }

    // ---------- searching ----------

    /// <summary>Starts building <paramref name="key"/>'s index off the caller's thread when it isn't ready or being built, and
    /// counts it as used now.</summary>
    public void Warm(string key)
    {
        if (unreadable.ContainsKey(key) || disposed) return;
        Touch(key);
        if (indexes.ContainsKey(key)) return;
        warming.GetOrAdd(key, k => Task.Run(async () =>
        {
            try
            {
                var guide = await Store.LoadGuideAsync(k, CancellationToken.None).ConfigureAwait(false);
                var programs = Library.Apps.FirstOrDefault(a => a.Key == k)?.Programs ?? [];
                if (guide is not null) indexes[k] = WarmedUp(GuideIndex.Build(guide.Chunks, [guide.Name, .. programs]));
            }
            catch (AppGuideFileException error) { unreadable[k] = Describe(error); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
            {
                unreadable[k] = "its guide file can't be read (" + error.GetType().Name + ")";
            }
            finally { warming.TryRemove(k, out _); }
            Changed?.Invoke();
        }));
    }

    /// <summary>Waits until <paramref name="key"/>'s index is built (for checks and the search tool); false when it has no guide.</summary>
    public async Task<bool> ReadyAsync(string key, CancellationToken cancellationToken = default)
    {
        Warm(key);
        if (warming.TryGetValue(key, out var running)) await running.WaitAsync(cancellationToken).ConfigureAwait(false);
        return indexes.ContainsKey(key);
    }

    /// <summary>The best sections of <paramref name="key"/>'s guide for <paramref name="question"/>, or null while its index isn't
    /// ready (it starts building). <paramref name="similar"/>: sections an embedding search found for the question (their number
    /// in <see cref="Chunks"/> and their meaning), fused in.</summary>
    public IReadOnlyList<GuideHit>? Search(string key, string question, int max, IReadOnlyList<(int Chunk, double Meaning)>? similar = null)
    {
        Touch(key);
        if (indexes.TryGetValue(key, out var index)) return index.Search(question, max, similar);
        Warm(key);
        return null;
    }

    /// <summary>Every section of <paramref name="key"/>'s guide in page order, or null while its index isn't ready.</summary>
    public IReadOnlyList<GuideChunk>? Chunks(string key) => indexes.TryGetValue(key, out var index) ? index.Chunks : null;

    /// <summary>The first <paramref name="max"/> sections of <paramref name="key"/>'s guide in page order (empty while its index
    /// isn't ready).</summary>
    public IReadOnlyList<GuideChunk> First(string key, int max) =>
        indexes.TryGetValue(key, out var index) && index is LexicalGuideIndex lexical ? [.. lexical.Chunks.Take(Math.Max(0, max))] : [];

    /// <summary>The guide notes for the user's <paramref name="words"/>, from memory only: the guide of an app the words name, else
    /// of the app in front. Null when App guides are off or neither has a guide; <see cref="AppGuideRecall.Ready"/> false (and no
    /// notes) while its index is still being built. Sections already in <paramref name="earlier"/> (the notes the conversation
    /// carries) are left out.</summary>
    public AppGuideRecall? Recall(string words, string? earlier = null, PromptSettings? prompts = null)
    {
        if (string.IsNullOrWhiteSpace(words)) return null;
        AppGuideLibrary now;
        AppGuideFront? seen;
        lock (gate) (now, seen) = (library, front);
        if (!now.On) return null;
        var entry = Named(now, words) ?? (seen?.Entry is { } inFront ? now.Apps.FirstOrDefault(a => a.Key == inFront.Key) : null);
        if (entry is not { BuiltAt: not null } || unreadable.ContainsKey(entry.Key)) return null;
        var started = Stopwatch.GetTimestamp();
        Touch(entry.Key);
        // The brief goes once per conversation: not when the notes the conversation carries have it already.
        var brief = entry.Brief is { Length: > 0 } b && (earlier is null || !earlier.Contains(GuideRecall.BriefOpening(b), StringComparison.Ordinal)) ? b : null;
        if (!indexes.TryGetValue(entry.Key, out var index))
        {
            Warm(entry.Key);
            return new(entry.Name, null, 0, 0, 0, Stopwatch.GetElapsedTime(started), false, brief);
        }
        var hits = index.Search(words, GuideRecall.MaximumChunks * 3);
        var fresh = hits.Where(h => earlier is null || !earlier.Contains(Opening(h.Chunk.Text), StringComparison.Ordinal)).ToArray();
        var notes = GuideRecall.Notes(entry.Name, fresh, prompts: prompts);
        var used = notes is null ? 0 : Math.Min(GuideRecall.MaximumChunks, fresh.Count(h => h.Relevance >= GuideRecall.MinimumRelevance));
        return new(entry.Name, notes, hits.Count, used, hits.Count == 0 ? 0 : hits.Max(h => h.Relevance), Stopwatch.GetElapsedTime(started), true,
            brief);
    }

    /// <summary>Keeps <paramref name="brief"/> as the brief of <paramref name="key"/>'s app (when it still has a guide); false when
    /// it has none or the store refused it.</summary>
    public async Task<bool> KeepBriefAsync(string key, string brief, CancellationToken cancellationToken = default)
    {
        var clean = GuideBrief.Clean(brief);
        if (clean is null) return false;
        var now = clock.GetUtcNow();
        try
        {
            var next = await UpdateAsync(l => l with
            {
                Apps = [.. l.Apps.Select(a => a.Key == key && a.BuiltAt is not null ? a with { Brief = clean, BriefAt = now } : a)]
            }, cancellationToken).ConfigureAwait(false);
            return next.Apps.Any(a => a.Key == key && a.Brief == clean);
        }
        catch (Exception error) when (Refusal(error) is not null) { return false; }
    }

    /// <summary>Notes that <paramref name="area"/> of <paramref name="key"/>'s app was looked into now, with the
    /// <paramref name="sections"/> research added (0: the guide had enough on it), newest last.</summary>
    public async Task<bool> KeepAreaAsync(string key, string area, int sections, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        try
        {
            await UpdateAsync(l => l with
            {
                Apps = [.. l.Apps.Select(a => a.Key == key ? a with { Areas = WithArea(a.Areas, new(area, now, sections)) } : a)]
            }, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (Refusal(error) is not null) { return false; }
    }

    private static IReadOnlyList<GuideArea> WithArea(IReadOnlyList<GuideArea> areas, GuideArea area)
    {
        var kept = areas.Where(a => !a.Name.Equals(area.Name, StringComparison.OrdinalIgnoreCase)).Append(area).ToList();
        return kept.Count <= GuideAreaResearch.MaximumAreas ? kept : kept[^GuideAreaResearch.MaximumAreas..];
    }

    /// <summary>Adds what research found on <paramref name="area"/> to <paramref name="key"/>'s guide as sections (replacing what an
    /// earlier research on that area added; the oldest research goes first past <see cref="GuideAreaResearch.MaximumSections"/>),
    /// notes the area and builds the guide's index again. Null when it was kept, else why not in a few plain words (Martlet is
    /// reading up, the app has no guide, the store refused it).</summary>
    public async Task<string?> AddResearchAsync(string key, string area, IReadOnlyList<GuideChunk> sections, CancellationToken cancellationToken = default)
    {
        if (!await reading.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return "Martlet is reading up";
        try
        {
            AppGuide? guide;
            try { guide = await Store.LoadGuideAsync(key, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (Refusal(error) is { } why) { return why; }
            if (guide is null) return "it has no guide";
            var page = GuideAreaResearch.Page(area);
            var wiki = guide.Chunks.Where(c => !GuideAreaResearch.IsResearched(c)).ToList();
            var research = guide.Chunks.Where(c => GuideAreaResearch.IsResearched(c) && c.Page != page).ToList();
            research.AddRange(sections);
            // The oldest research (first in the guide) goes first.
            var room = Math.Min(GuideAreaResearch.MaximumSections, FileAppGuideStore.MaximumChunks - wiki.Count);
            if (research.Count > room) research = research[^Math.Max(0, room)..];
            var next = guide with { Chunks = [.. wiki, .. research] };
            await saving.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { await Store.SaveGuideAsync(next, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (Refusal(error) is { } why) { return why; }
            finally { saving.Release(); }
            await KeepAreaAsync(key, area, sections.Count, cancellationToken).ConfigureAwait(false);
            var programs = Library.Apps.FirstOrDefault(a => a.Key == key)?.Programs ?? [];
            indexes[key] = GuideIndex.Build(next.Chunks, [next.Name, .. programs]);
            Touch(key);
            Changed?.Invoke();
            return null;
        }
        finally { reading.Release(); }
    }

    // Keeps why reading up failed on the app's entry; when even that can't be saved, the build's result still says it.
    private async Task KeepProblemAsync(string key, string problem)
    {
        try
        {
            await UpdateAsync(l => l with { Apps = [.. l.Apps.Select(a => a.Key == key ? a with { Problem = problem } : a)] },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (Refusal(error) is not null) { }
    }

    /// <summary>Why the store refused a change, in a few plain words (a file it can't read or a newer Martlet's, a guide too
    /// large, a full library), or null for an error that isn't the store's refusal.</summary>
    public static string? Refusal(Exception error) => error switch
    {
        AppGuideFileException file => Describe(file),
        ArgumentException or InvalidOperationException => "it couldn't be kept: " + error.Message.TrimEnd('.'),
        IOException or UnauthorizedAccessException => "the guides folder couldn't be written (" + error.GetType().Name + ")",
        _ => null
    };

    /// <summary>A store file the store refused, in a few plain words (its file name and why; never its contents).</summary>
    public static string Describe(AppGuideFileException error) =>
        $"{Path.GetFileName(error.File)} " + (error.IsNewer ? "was written by a newer Martlet" : "can't be read: " + error.Reason.TrimEnd('.'));

    // The start of a section as the notes carry it (one line), to tell one already sent.
    private static string Opening(string text)
    {
        var line = text.Replace('\n', ' ');
        return line.Length <= 60 ? line : line[..60];
    }

    // Runs the search and notes code once off the reply's path, so the first message about an app doesn't wait for it to load.
    private static IGuideIndex WarmedUp(IGuideIndex index)
    {
        var hits = index.Search("where do I find it", GuideRecall.MaximumChunks);
        _ = Named(new AppGuideLibrary(), "where do I find it");
        _ = GuideRecall.Notes("an app", [.. hits.Where(h => h.Relevance >= 0), new GuideHit(new("", "page", "section", "text"), 1, 1)]);
        _ = Opening("text");
        return index;
    }

    private void Keep(AppGuideLibrary next)
    {
        lock (gate)
        {
            library = next;
            loaded = true;
            revision++;
            if (front is { } seen) front = seen with { Entry = Match(next, [seen.Program, seen.File]) };
        }
        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        resources?.Dispose();
    }

    private sealed class Said(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
