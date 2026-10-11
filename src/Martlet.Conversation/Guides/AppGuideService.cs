using System.Collections.Concurrent;
using System.Diagnostics;
using Martlet.Core.Settings;

namespace Martlet.Conversation.Guides;

/// <summary>What one try to read up on an app came to: its key and name, the pages, sections and bytes kept, the sites read, the
/// pages that couldn't be read and how long it took; or the <see cref="Problem"/> in a few plain words (<see cref="Busy"/>: another
/// app is being read up on now).</summary>
public sealed record AppGuideBuild(string Key, string Name, int Pages, int Chunks, long Bytes, IReadOnlyList<string> Sites, int Failed,
    long Downloaded, TimeSpan Took, string? Problem, bool Busy = false)
{
    public bool Built => Problem is null;
}

/// <summary>The program in front, as the desktop sees it: its name (the program file's description, else its file name), its file
/// name without .exe, whether it is a game (<c>PcActivity.Classify</c>) and whether it fills its screen, and the library entry it
/// matches (by key, name or program names; null when none does).</summary>
public sealed record AppGuideFront(string Program, string File, bool Game, bool FullScreen, AppGuideEntry? Entry);

/// <summary>An app Martlet may offer to read up on now: its name and key, and whether it is a game (else an app on the list).</summary>
public sealed record AppGuideOfferCandidate(string Name, string Key, bool Game);

/// <summary>What a guide had for one message: the app, the notes (null when no section matched well enough), how many sections
/// matched at all and went, the best relevance, how long the search took and whether the guide's index was ready (it is never
/// waited for).</summary>
public sealed record AppGuideRecall(string App, string? Notes, int Matched, int Used, double Best, TimeSpan Took, bool Ready);

/// <summary>The guide library of this PC (Companion › App guides, docs/APP_GUIDES.md): the store's library kept in memory, one
/// searchable index per app with a guide, built off the reply path (at <see cref="LoadAsync"/>, when the app comes to the front and
/// after a guide is made) and never waited for on it, reading up one app at a time, the program in front and the offer rule.
/// Thread-safe; <see cref="Changed"/> is raised on any thread.</summary>
public sealed class AppGuideService : IDisposable
{
    /// <summary>How many times Martlet offers one app in a session at most, when its offers were dropped before it could ask.</summary>
    public const int MaximumOffers = 3;

    private readonly IGuideBuilder builder;
    private readonly IDisposable? resources;
    private readonly TimeProvider clock;
    private readonly GuideBuildLimits? limits;
    private readonly object gate = new();
    private readonly SemaphoreSlim saving = new(1, 1);
    private readonly SemaphoreSlim reading = new(1, 1);
    private readonly ConcurrentDictionary<string, IGuideIndex> indexes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> warming = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Times, Func<bool>? Dropped)> offered = new(StringComparer.Ordinal);
    private AppGuideLibrary library = new();
    private AppGuideFront? front;
    private (string Key, string Name, string? Progress)? building;
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

    /// <summary>The library as last read or saved (an empty, default one until <see cref="LoadAsync"/>).</summary>
    public AppGuideLibrary Library { get { lock (gate) return library; } }

    /// <summary>Goes up by one each time the library changes (the page draws its list again only then).</summary>
    public int Revision { get { lock (gate) return revision; } }

    public bool Loaded { get { lock (gate) return loaded; } }

    /// <summary>Whether App guides are on (Companion › App guides; off by default).</summary>
    public bool On => Library.On;

    /// <summary>The program in front, or null before the first look (or while App guides are off).</summary>
    public AppGuideFront? Front { get { lock (gate) return front; } }

    /// <summary>The app being read up on now (its key and name) and what it is doing, or null.</summary>
    public (string Key, string Name, string? Progress)? Building { get { lock (gate) return building; } }

    /// <summary>How many indexes are ready to search.</summary>
    public int ReadyIndexes => indexes.Count;

    /// <summary>Whether <paramref name="key"/>'s index is ready to search.</summary>
    public bool IsReady(string key) => indexes.ContainsKey(key);

    /// <summary>Reads the library and starts building the index of every app with a guide (off the caller's thread).</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var read = await Store.LoadLibraryAsync(cancellationToken).ConfigureAwait(false);
        lock (gate)
        {
            library = read;
            loaded = true;
            revision++;
        }
        Changed?.Invoke();
        foreach (var entry in read.Apps.Where(a => a.BuiltAt is not null)) Warm(entry.Key);
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

    public Task SetAskAsync(bool ask, CancellationToken cancellationToken = default) =>
        UpdateAsync(l => l with { AskWhenStarted = ask }, cancellationToken);

    /// <summary>Adds an app to the list (or changes the one with the same key): its name, program names and start pages.</summary>
    public async Task<AppGuideEntry> AddAsync(string name, IReadOnlyList<string>? programs, IReadOnlyList<string>? sites,
        CancellationToken cancellationToken = default)
    {
        var clean = AppGuideTools.CleanName(name);
        if (clean.Length == 0) throw new ArgumentException("An app needs a name with a letter or digit.", nameof(name));
        var key = AppGuideKeys.Of(clean);
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
            Keep(await Store.LoadLibraryAsync(cancellationToken).ConfigureAwait(false));
        }
        finally { saving.Release(); }
        return true;
    }

    /// <summary>The user said no to the offer: Martlet never offers <paramref name="name"/> again (the entry is added when the
    /// library doesn't have it yet).</summary>
    public async Task<AppGuideEntry> DeclineAsync(string name, CancellationToken cancellationToken = default)
    {
        var clean = AppGuideTools.CleanName(name);
        if (clean.Length == 0) throw new ArgumentException("An app needs a name with a letter or digit.", nameof(name));
        AppGuideEntry? declined = null;
        await UpdateAsync(l =>
        {
            var old = Match(l, [clean]);
            declined = (old ?? new AppGuideEntry { Key = AppGuideKeys.Of(clean), Name = clean }) with { Declined = true };
            return l with { Apps = [.. l.Apps.Where(a => a.Key != declined.Key), declined] };
        }, cancellationToken).ConfigureAwait(false);
        return declined!;
    }

    /// <summary>Ask again: Martlet may offer <paramref name="key"/> again (this session too).</summary>
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
    }

    /// <summary>Forgets the app in front (App guides turned off, or the watch stopped).</summary>
    public void Forget()
    {
        lock (gate)
        {
            if (front is null) return;
            front = null;
        }
        Changed?.Invoke();
    }

    /// <summary>The app Martlet may offer to read up on now, or null: App guides and Ask when I start a game or app are on, a game
    /// (or an app on the list) is in front with no guide, it isn't being read up on, the user never said no to it, and Martlet
    /// hasn't asked about it this session (an offer dropped before Martlet could say it counts only after
    /// <see cref="MaximumOffers"/>).</summary>
    public AppGuideOfferCandidate? Offer()
    {
        lock (gate)
        {
            if (!library.On || !library.AskWhenStarted || front is not { } seen) return null;
            if (seen.Entry is null && !seen.Game) return null;
            var name = seen.Entry?.Name ?? (seen.Program.Length > 0 ? seen.Program : seen.File);
            var clean = AppGuideTools.CleanName(name);
            if (clean.Length == 0) return null;
            var entry = seen.Entry ?? Match(library, [clean]);
            var key = entry?.Key ?? AppGuideKeys.Of(clean);
            if (entry is { } known && (known.BuiltAt is not null || known.Declined)) return null;
            if (building?.Key == key) return null;
            if (offered.TryGetValue(key, out var asked) && (asked.Dropped?.Invoke() != true || asked.Times >= MaximumOffers)) return null;
            return new(entry?.Name ?? clean, key, seen.Game);
        }
    }

    /// <summary>Martlet asked about <paramref name="key"/> (<paramref name="dropped"/> says, later, whether the offer was dropped
    /// before Martlet could say it, so it may try again).</summary>
    public void Offered(string key, Func<bool>? dropped = null)
    {
        lock (gate) offered[key] = (offered.GetValueOrDefault(key).Times + 1, dropped);
    }

    // ---------- reading up ----------

    /// <summary>Reads up on <paramref name="name"/> (one app at a time): reads its pages from <paramref name="sites"/> (empty: the
    /// entry's own, else Martlet finds its wiki), cuts them into sections, keeps the guide and builds its index. A failure is kept
    /// on the entry as its problem. <paramref name="progress"/> gets a few plain words now and then.</summary>
    public async Task<AppGuideBuild> BuildAsync(string name, IReadOnlyList<string>? sites, IProgress<string>? progress,
        CancellationToken cancellationToken = default)
    {
        var clean = AppGuideTools.CleanName(name);
        var started = clock.GetTimestamp();
        if (clean.Length == 0) return new(AppGuideKeys.Of(name ?? ""), name ?? "", 0, 0, 0, [], 0, 0, TimeSpan.Zero, "it has no name");
        var known = Match(Library, [clean]);
        var key = known?.Key ?? AppGuideKeys.Of(clean);
        var shown = known?.Name ?? clean;
        if (!await reading.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return new(key, shown, 0, 0, 0, [], 0, 0, TimeSpan.Zero, $"Martlet is already reading up on {Building?.Name ?? "another app"}", Busy: true);
        try
        {
            lock (gate) building = (key, shown, "Starting");
            Changed?.Invoke();
            var given = (sites ?? []).Select(AppGuideTools.Site).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(AppGuideTools.MaxSites).ToArray();
            // The entry is on the list from the start (with the pages the owner gave), so the page shows what is being read.
            var entry = (await UpdateAsync(l =>
            {
                var old = l.Apps.FirstOrDefault(a => a.Key == key);
                var next = (old ?? new AppGuideEntry { Key = key, Name = shown }) with
                {
                    Sites = given.Length > 0 ? given : old?.Sites ?? [], Declined = false
                };
                return l with { Apps = [.. l.Apps.Where(a => a.Key != key), next] };
            }, cancellationToken).ConfigureAwait(false)).Apps.First(a => a.Key == key);
            var said = new Said(text =>
            {
                lock (gate) if (building?.Key == key) building = (key, shown, text);
                progress?.Report(text);
                Changed?.Invoke();
            });
            GuideBuildOutcome outcome;
            try
            {
                outcome = await builder.BuildAsync(new(shown, entry.Sites, limits), said, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                outcome = new([], [], 0, 0, "reading up failed on this PC (" + error.GetType().Name + ")");
            }
            var guide = outcome.Pages.Count == 0 ? null : GuideChunker.Guide(key, shown, outcome, clock.GetUtcNow());
            var problem = outcome.Pages.Count == 0 ? outcome.Problem ?? "no page about it could be read"
                : guide!.Chunks.Count == 0 ? "the pages it read had no text to keep" : null;
            if (problem is not null)
            {
                await UpdateAsync(l => l with { Apps = [.. l.Apps.Select(a => a.Key == key ? a with { Problem = problem } : a)] },
                    CancellationToken.None).ConfigureAwait(false);
                return new(key, shown, 0, 0, 0, outcome.Sites, outcome.Failed, outcome.Bytes, clock.GetElapsedTime(started), problem);
            }
            said.Report("Saving the guide");
            await saving.WaitAsync(cancellationToken).ConfigureAwait(false);
            AppGuideEntry saved;
            try
            {
                await Store.SaveGuideAsync(guide!, cancellationToken).ConfigureAwait(false);
                indexes[key] = WarmedUp(GuideIndex.Build(guide!.Chunks));
                var read = await Store.LoadLibraryAsync(cancellationToken).ConfigureAwait(false);
                Keep(read);
                saved = read.Apps.FirstOrDefault(a => a.Key == key) ?? entry;
            }
            finally { saving.Release(); }
            return new(key, shown, guide.Sources.Count, guide.Chunks.Count, saved.Bytes, guide.Sites, outcome.Failed, outcome.Bytes,
                clock.GetElapsedTime(started), null);
        }
        finally
        {
            lock (gate) building = null;
            reading.Release();
            Changed?.Invoke();
        }
    }

    // ---------- searching ----------

    /// <summary>Starts building <paramref name="key"/>'s index off the caller's thread when it isn't ready or being built.</summary>
    public void Warm(string key)
    {
        if (indexes.ContainsKey(key) || disposed) return;
        warming.GetOrAdd(key, k => Task.Run(async () =>
        {
            try
            {
                var guide = await Store.LoadGuideAsync(k, CancellationToken.None).ConfigureAwait(false);
                if (guide is not null) indexes[k] = WarmedUp(GuideIndex.Build(guide.Chunks));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException) { }
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
    /// ready (it starts building).</summary>
    public IReadOnlyList<GuideHit>? Search(string key, string question, int max)
    {
        if (indexes.TryGetValue(key, out var index)) return index.Search(question, max);
        Warm(key);
        return null;
    }

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
        if (entry is not { BuiltAt: not null }) return null;
        var started = Stopwatch.GetTimestamp();
        if (!indexes.TryGetValue(entry.Key, out var index))
        {
            Warm(entry.Key);
            return new(entry.Name, null, 0, 0, 0, Stopwatch.GetElapsedTime(started), false);
        }
        var hits = index.Search(words, GuideRecall.MaximumChunks * 3);
        var fresh = hits.Where(h => earlier is null || !earlier.Contains(Opening(h.Chunk.Text), StringComparison.Ordinal)).ToArray();
        var notes = GuideRecall.Notes(entry.Name, fresh, prompts: prompts);
        var used = notes is null ? 0 : Math.Min(GuideRecall.MaximumChunks, fresh.Count(h => h.Relevance >= GuideRecall.MinimumRelevance));
        return new(entry.Name, notes, hits.Count, used, hits.Count == 0 ? 0 : hits.Max(h => h.Relevance), Stopwatch.GetElapsedTime(started), true);
    }

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
