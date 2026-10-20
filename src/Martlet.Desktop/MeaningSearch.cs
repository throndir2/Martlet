using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Martlet.Conversation.Guides;
using Martlet.Core.Search;
using Martlet.Core.Settings;
using Martlet.Memory;
using Martlet.Providers.Ollama;

namespace Martlet.Desktop;

/// <summary>Whether an embedding model can be used now: why not (null when it can), and whether that is because the model isn't
/// downloaded (<see cref="Missing"/>) or because loading it now could push another model out (<see cref="Hold"/>).</summary>
internal sealed record MeaningReadiness(string? Problem, bool Missing = false, bool Hold = false);

/// <summary>An embedding model and how to tell whether it can be used now.</summary>
internal sealed record MeaningModel(ITextEmbedder Embedder, Func<CancellationToken, Task<MeaningReadiness>> Check, string Where);

/// <summary>Finding by meaning (docs/SEARCH.md, "Finding by meaning"): an embedding model, used only away from the reply's first
/// word, in three places: when remembering and tidying memory find related facts, in the memory find tools (manage_memories find,
/// memory_find, find_memories) and in search_guide (guides and research reports). Its results are fused with the lexical search
/// through the search engine, so names and items found by their words stay strong. Automatic recall, guide notes and
/// past-conversation recall on the reply's path don't use it.
/// <para>The model is chosen in meaning-search.json (shared between the owner's computers); by default it is nomic-embed-text in
/// Ollama on this PC, on the processor only (<see cref="OllamaEmbedder"/>), and it is never downloaded without asking. Vectors are
/// derived data made on each computer: memory's and the reports' are kept in memory only, so a deleted fact or report is gone from
/// them at the next refresh; a guide's (public web text) are kept in meaning\guides beside the guides and deleted with the guide.
/// Documents are embedded in the background, one batch at a time, and wait while a reply runs. Without a usable model everything
/// works as before (words only), and <see cref="Status"/> says why.</para></summary>
internal sealed class MeaningSearch : IDisposable
{
    /// <summary>The most facts found by meaning that join a find or remembering's related facts, and the least meaning they need.</summary>
    internal const int MostRelatedFacts = 8;
    internal const double RelatedMeaning = 0.15;
    /// <summary>Tidying memory groups a fact with another found only by meaning when they are at least this close: they may say
    /// the same thing or contradict each other.</summary>
    internal const double SameMeaning = 0.5;
    /// <summary>The most guide or report sections found by meaning that join a search_guide, and the least meaning they need.</summary>
    internal const int MostSections = 20;
    internal const double GuideMeaning = 0.2;
    /// <summary>A document is embedded from at most this many characters (a guide section's heading and the start of its text).</summary>
    internal const int MaximumDocumentCharacters = 1_500;
    /// <summary>The longest a question waits for its vector; after that the search uses words only.</summary>
    internal static readonly TimeSpan QueryLimit = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ReadyFor = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(2);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private readonly string? dataDirectory;
    private readonly Func<bool> busy;
    private readonly Func<MeaningSearchFile, MeaningModel?> place;
    private readonly object gate = new();
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentDictionary<string, VectorSet> guides = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, object> verified = new(StringComparer.Ordinal);
    private readonly HashSet<string> queued = new(StringComparer.Ordinal);
    private Task tail = Task.CompletedTask;
    private MeaningSearchFile choice;
    private MeaningModel? model;
    private VectorSet memory, reports;
    private MeaningReadiness? readiness;
    private DateTimeOffset checkedAt;
    private (string What, int Done, int Total)? filling;
    private string? failure;
    private bool disposed;

    /// <param name="busy">Whether a reply runs or speaks now (background embedding waits).</param>
    /// <param name="place">The model a choice means (null: Off); by default Ollama on this PC. Checks use a fake one.</param>
    internal MeaningSearch(string? dataDirectory, Func<bool> busy, Func<MeaningSearchFile, MeaningModel?>? place = null)
    {
        this.dataDirectory = dataDirectory;
        this.busy = busy ?? (() => false);
        this.place = place ?? OllamaHere;
        choice = dataDirectory is null ? new() : MeaningSearchFile.Load(dataDirectory);
        model = choice.On ? this.place(choice) : null;
        var name = model?.Embedder.Model ?? "off";
        memory = new(name);
        reports = new(name);
    }

    /// <summary>Raised on any thread when the state line changes.</summary>
    internal event Action? Changed;

    /// <summary>Writes a line to the desktop log (counts and states, never a fact or a text).</summary>
    internal Action<string>? Log { get; init; }

    internal MeaningSearchFile Choice { get { lock (gate) return choice; } }

    /// <summary>Whether the model was found usable at the last check.</summary>
    internal bool Ready { get { lock (gate) return model is not null && readiness is { Problem: null }; } }

    /// <summary>Whether the chosen model isn't downloaded yet (the page offers Download).</summary>
    internal bool NeedsDownload { get { lock (gate) return model is not null && readiness is { Missing: true }; } }

    internal int MemoryVectors => Volatile.Read(ref memory).Count;
    internal int GuideVectors => guides.Values.Sum(set => set.Count);
    internal int ReportVectors => Volatile.Read(ref reports).Count;

    /// <summary>One line for Companion › Memory: off, why it can't be used now, or what it can find by meaning (counts only).</summary>
    internal string Status
    {
        get
        {
            MeaningSearchFile now;
            MeaningModel? current;
            MeaningReadiness? ready;
            (string What, int Done, int Total)? fill;
            string? failed;
            lock (gate) (now, current, ready, fill, failed) = (choice, model, readiness, filling, failure);
            if (current is null) return "Off: Martlet finds memories, guide sections and reports by their words only.";
            if (ready is null) return $"Checking {now.Model} {current.Where}...";
            if (ready.Missing)
                return $"Not ready: {now.Model} isn't downloaded in Ollama on this PC, so Martlet finds by words only; choose Download to get it" +
                    (now.Model == MeaningSearchFile.DefaultModel ? $" ({MeaningSearchFile.DefaultModelSize})" : "") + ".";
            if (ready.Hold) return $"Waiting: {ready.Problem}, so Martlet finds by words only until it can load {now.Model}.";
            if (ready.Problem is { } problem) return $"Not ready: {problem}, so Martlet finds by words only and checks again in a few minutes.";
            var line = $"On: {now.Model} {current.Where}, with {Count(MemoryVectors, "memory", "memories")}, " +
                $"{Count(GuideVectors, "guide section", "guide sections")} and {Count(ReportVectors, "report section", "report sections")} " +
                "found by meaning";
            if (fill is { } f) line += $"; reading {f.What}: {f.Done} of {f.Total}";
            if (failed is not null) line += $"; the last try failed ({failed}), so it tries again later";
            return line + ".";
        }
    }

    private static string Count(int count, string one, string many) => $"{count:N0} {(count == 1 ? one : many)}";

    /// <summary>Takes the choice saved in meaning-search.json (after the page or another computer changed it): a new model starts
    /// its vectors again, and Off drops them all (the guides' files too).</summary>
    internal void Reload()
    {
        if (dataDirectory is null) return;
        Apply(MeaningSearchFile.Load(dataDirectory));
    }

    /// <summary>Saves <paramref name="next"/> and uses it; false when it couldn't be saved.</summary>
    internal bool Choose(MeaningSearchFile next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (dataDirectory is not null)
        {
            try { MeaningSearchFile.Save(dataDirectory, next); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        }
        Apply(next);
        return true;
    }

    private void Apply(MeaningSearchFile next)
    {
        bool same;
        lock (gate)
        {
            same = next == choice;
            if (!same)
            {
                choice = next;
                model = next.On ? place(next) : null;
                readiness = null;
                failure = null;
                var name = model?.Embedder.Model ?? "off";
                if (memory.Model != name) memory = new(name);
                if (reports.Model != name) reports = new(name);
            }
        }
        if (same) return;
        guides.Clear();
        verified.Clear();
        if (!next.On) DeleteGuideFiles(null);
        Log?.Invoke(next.On ? $"Finding by meaning: {next.Model} {Model?.Where}." : "Finding by meaning: off.");
        Changed?.Invoke();
        CheckAsync(stop.Token).Forget();
    }

    private MeaningModel? Model { get { lock (gate) return model; } }

    /// <summary>Checks whether the model can be used now (its last answer is kept for a minute, a problem for two).</summary>
    internal async Task<bool> CheckAsync(CancellationToken token, bool force = false)
    {
        MeaningModel? current;
        lock (gate)
        {
            current = model;
            if (current is null) return false;
            var age = DateTimeOffset.UtcNow - checkedAt;
            if (!force && readiness is { } known && age < (known.Problem is null ? ReadyFor : RetryAfter)) return known.Problem is null;
        }
        MeaningReadiness found;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token, stop.Token);
            limit.CancelAfter(TimeSpan.FromSeconds(8));
            found = await current.Check(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { found = new("Ollama on this PC didn't answer in time"); }
        bool changed;
        lock (gate)
        {
            if (!ReferenceEquals(current, model)) return false;
            changed = readiness != found;
            readiness = found;
            checkedAt = DateTimeOffset.UtcNow;
        }
        if (changed)
        {
            Log?.Invoke(found.Problem is null ? "Finding by meaning: ready." : $"Finding by meaning: not ready ({(found.Missing ? "model not downloaded" : found.Hold ? "held back" : "not available")}).");
            Changed?.Invoke();
        }
        return found.Problem is null;
    }

    /// <summary>The vector of a question, or null when no model can be used now or it took longer than <see cref="QueryLimit"/>
    /// (the search then uses words only).</summary>
    internal async Task<float[]?> QueryAsync(string text, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(text) || !await CheckAsync(token).ConfigureAwait(false) || Model is not { } current) return null;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token, stop.Token);
        limit.CancelAfter(QueryLimit);
        try
        {
            var vectors = await current.Embedder.EmbedAsync([Clip(text)], EmbeddingUse.Query, limit.Token).ConfigureAwait(false);
            return vectors is [{ Length: > 0 } vector] ? vector : null;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            Log?.Invoke($"Finding by meaning: a question took longer than {QueryLimit.TotalSeconds:0} s; searched by words only.");
            return null;
        }
        catch (Exception error) when (error is OllamaEmbedException or HttpRequestException or InvalidOperationException)
        {
            Failed(current, error);
            return null;
        }
    }

    private void Failed(MeaningModel current, Exception error)
    {
        lock (gate)
        {
            if (!ReferenceEquals(current, model)) return;
            failure = error.Message.Length <= 160 ? error.Message.TrimEnd('.') : error.Message[..160] + "…";
            if (error is OllamaEmbedException { Missing: true }) readiness = new($"{choice.Model} isn't downloaded", Missing: true);
            checkedAt = DateTimeOffset.UtcNow;
        }
        Log?.Invoke($"Finding by meaning: the embedding model failed ({error.GetType().Name}); words only until it works again.");
        Changed?.Invoke();
    }

    // ---------- memory ----------

    /// <summary>Brings memory's vectors in step with <paramref name="facts"/> (every current fact of the spaces recall reads) in
    /// the background: vectors of facts that are gone or changed are dropped, and new facts are embedded.</summary>
    internal void SyncMemory(Func<CancellationToken, Task<IReadOnlyList<MemoryFact>?>> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        Queue("memory", async (current, token) =>
        {
            var all = await facts(token).ConfigureAwait(false);
            var set = Volatile.Read(ref memory);
            if (all is null) return;
            var live = all.Where(f => !f.IsHistory).Select(f => (Id: Id(f.Id), Text: Clip(f.Content))).ToArray();
            set.Keep(live.Select(f => f.Id).ToHashSet(StringComparer.Ordinal));
            await Fill(current, set, "memories", live, token).ConfigureAwait(false);
        });
    }

    /// <summary>Drops memory's vectors (memory turned off).</summary>
    internal void ForgetMemory() => Volatile.Read(ref memory).Clear();

    /// <summary>The facts nearest to <paramref name="query"/> with at least <paramref name="minimum"/> meaning, best first.</summary>
    internal IReadOnlyList<Guid> Facts(float[] query, int want = MostRelatedFacts, double minimum = RelatedMeaning) =>
        [.. Volatile.Read(ref memory).Nearest(query, want).Where(n => n.Meaning >= minimum).Select(n => Guid.ParseExact(n.Id, "N"))];

    /// <summary>The facts nearest to <paramref name="fact"/>'s own vector with at least <paramref name="minimum"/> meaning (empty
    /// while it has none).</summary>
    internal IReadOnlyList<Guid> Neighbors(Guid fact, int want, double minimum = SameMeaning)
    {
        var set = Volatile.Read(ref memory);
        var id = Id(fact);
        return set.Vector(id) is { } vector
            ? [.. set.Nearest(vector, want, except: id).Where(n => n.Meaning >= minimum).Select(n => Guid.ParseExact(n.Id, "N"))]
            : [];
    }

    private static string Id(Guid fact) => fact.ToString("N");

    // ---------- guides and reports ----------

    /// <summary>The sections of <paramref name="key"/>'s guide nearest to <paramref name="query"/> (number in
    /// <paramref name="chunks"/> and meaning), best first; sections without a vector yet are embedded in the background (the guide's
    /// vectors are kept in meaning\guides).</summary>
    internal IReadOnlyList<(int Chunk, double Meaning)> GuideSections(string key, IReadOnlyList<GuideChunk> chunks, float[] query,
        int want = MostSections, double minimum = GuideMeaning)
    {
        if (Model is not { } current) return [];
        var set = guides.GetOrAdd(key, k => dataDirectory is null ? new(current.Embedder.Model) : VectorSet.Load(GuidePath(k), current.Embedder.Model));
        // The sections are compared with the vectors once per index (a guide read again gets a new one).
        if (!verified.TryGetValue(key, out var seen) || !ReferenceEquals(seen, chunks) || set.Count != chunks.Count)
        {
            var items = Sections(chunks, i => i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (set.Count != chunks.Count || set.Missing(items).Count > 0)
                Queue("guide " + key, async (embedder, token) =>
                {
                    set.Keep(items.Select(i => i.Id).ToHashSet(StringComparer.Ordinal));
                    try { await Fill(embedder, set, "a guide", items, token).ConfigureAwait(false); }
                    finally { SaveGuide(key, set); }
                });
            else verified[key] = chunks;
        }
        return [.. set.Nearest(query, want).Where(n => n.Meaning >= minimum)
            .Select(n => (int.Parse(n.Id, System.Globalization.CultureInfo.InvariantCulture), n.Meaning)).Where(n => n.Item1 < chunks.Count)];
    }

    /// <summary>The research report sections nearest to <paramref name="query"/>, by report ID; reports without vectors yet are
    /// embedded in the background, and vectors of reports that are gone are dropped.</summary>
    internal IReadOnlyDictionary<string, IReadOnlyList<(int Chunk, double Meaning)>> ReportSections(
        IReadOnlyList<(string Id, IReadOnlyList<GuideChunk> Sections)> documents, float[] query, int want = MostSections,
        double minimum = GuideMeaning)
    {
        var set = Volatile.Read(ref reports);
        var items = documents.SelectMany(d => Sections(d.Sections, i => d.Id + "#" + i.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        set.Keep(items.Select(i => i.Id).ToHashSet(StringComparer.Ordinal));
        if (set.Missing(items).Count > 0)
            Queue("reports", (embedder, token) => Fill(embedder, set, "reports", items, token));
        var found = new Dictionary<string, List<(int, double)>>(StringComparer.Ordinal);
        foreach (var near in set.Nearest(query, want).Where(n => n.Meaning >= minimum))
        {
            var at = near.Id.LastIndexOf('#');
            if (at <= 0 || !int.TryParse(near.Id.AsSpan(at + 1), System.Globalization.CultureInfo.InvariantCulture, out var chunk)) continue;
            var report = near.Id[..at];
            if (!found.TryGetValue(report, out var list)) found[report] = list = [];
            list.Add((chunk, near.Meaning));
        }
        return found.ToDictionary(f => f.Key, f => (IReadOnlyList<(int Chunk, double Meaning)>)f.Value, StringComparer.Ordinal);
    }

    /// <summary>Deletes the kept vectors of guides that aren't in <paramref name="keys"/> (deleted guides).</summary>
    internal void KeepGuides(IReadOnlyCollection<string> keys)
    {
        foreach (var gone in guides.Keys.Where(k => !keys.Contains(k)).ToArray())
        {
            guides.TryRemove(gone, out _);
            verified.TryRemove(gone, out _);
        }
        DeleteGuideFiles(keys);
    }

    /// <summary>The text a section is embedded from: where it is (page and headings), then the start of its text.</summary>
    internal static string Text(GuideChunk chunk)
    {
        var where = chunk.Section.Length == 0 ? chunk.Page
            : chunk.Section.StartsWith(chunk.Page, StringComparison.Ordinal) ? chunk.Section : chunk.Page + " › " + chunk.Section;
        return Clip(where + "\n" + chunk.Text);
    }

    private static (string Id, string Text)[] Sections(IReadOnlyList<GuideChunk> chunks, Func<int, string> id) =>
        [.. chunks.Select((chunk, i) => (id(i), Text(chunk)))];

    private string? GuidesFolder => dataDirectory is null ? null : Path.Combine(dataDirectory, "meaning", "guides");

    private string GuidePath(string key) =>
        Path.Combine(GuidesFolder!, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24].ToLowerInvariant() + ".vectors");

    private void SaveGuide(string key, VectorSet set)
    {
        if (dataDirectory is null || !set.Changed || Model?.Embedder.Model != set.Model || !guides.ContainsKey(key)) return;
        try { set.Save(GuidePath(key)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log?.Invoke($"Finding by meaning: a guide's vectors couldn't be saved ({error.GetType().Name}); they are made again next time.");
        }
    }

    private void DeleteGuideFiles(IReadOnlyCollection<string>? keep)
    {
        if (GuidesFolder is not { } folder || !Directory.Exists(folder)) return;
        var kept = keep?.Select(GuidePath).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.vectors").Where(f => !kept.Contains(f)).ToArray()) File.Delete(file);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    // ---------- background work ----------

    // One background job at a time, in order; a job already waiting isn't queued twice.
    private void Queue(string what, Func<ITextEmbedder, CancellationToken, Task> job)
    {
        lock (gate)
        {
            if (disposed || model is null || !queued.Add(what)) return;
            tail = tail.ContinueWith(_ => RunAsync(what, job), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
    }

    private async Task RunAsync(string what, Func<ITextEmbedder, CancellationToken, Task> job)
    {
        lock (gate) queued.Remove(what);
        var token = stop.Token;
        if (token.IsCancellationRequested || Model is not { } current) return;
        try
        {
            while (busy()) await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            if (!await CheckAsync(token).ConfigureAwait(false)) return;
            await job(current.Embedder, token).ConfigureAwait(false);
            lock (gate) if (ReferenceEquals(current, model)) failure = null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is OllamaEmbedException or HttpRequestException or InvalidOperationException or
            ArgumentException or IOException or MemoryException or DesktopMemoryException or OperationCanceledException)
        {
            Failed(current, error);
        }
        finally
        {
            lock (gate) filling = null;
            Changed?.Invoke();
        }
    }

    private async Task Fill(ITextEmbedder embedder, VectorSet set, string what, IReadOnlyList<(string Id, string Text)> items,
        CancellationToken token)
    {
        if (set.Model != embedder.Model) return;
        var started = DateTimeOffset.UtcNow;
        var done = await set.FillAsync(embedder, items, () => busy(), token, (count, total) =>
        {
            lock (gate) filling = (what, count, total);
            Changed?.Invoke();
        }).ConfigureAwait(false);
        if (done > 0)
            Log?.Invoke($"Finding by meaning: embedded {done} of {what} in {(DateTimeOffset.UtcNow - started).TotalSeconds:0.0} s.");
    }

    /// <summary>Waits until the background work queued so far is done (checks).</summary>
    internal Task IdleAsync()
    {
        lock (gate) return tail;
    }

    private static string Clip(string text)
    {
        var line = (text ?? "").Trim();
        if (line.Length <= MaximumDocumentCharacters) return line;
        var end = char.IsHighSurrogate(line[MaximumDocumentCharacters - 1]) ? MaximumDocumentCharacters - 1 : MaximumDocumentCharacters;
        return line[..end];
    }

    // nomic-embed-text (or the chosen model) in Ollama on this PC, on the processor only.
    private static MeaningModel? OllamaHere(MeaningSearchFile choice)
    {
        if (choice.Place != MeaningSearchFile.ThisPc) return null;
        var embedder = new OllamaEmbedder(Http, LocalOllama.OriginUri, choice.Model);
        return new(embedder, async token =>
        {
            var models = await LocalOllama.ServedModelsAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
            if (models is null)
                return new(LocalOllama.Executable() is null ? "Ollama isn't installed on this PC" : "Ollama isn't running on this PC");
            if (!LocalOllama.Serves(models, choice.Model)) return new($"{choice.Model} isn't downloaded", Missing: true);
            var loaded = await OllamaSideBySide.LoadedAsync(Http, LocalOllama.OriginUri, token).ConfigureAwait(false);
            if (loaded is null) return new("Ollama on this PC didn't say which models it has loaded");
            return OllamaEmbedder.HoldBack(loaded, choice.Model) is { } why ? new(why, Hold: true) : new(null);
        }, "in Ollama on this PC's processor");
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
        }
        stop.Cancel();
    }
}
