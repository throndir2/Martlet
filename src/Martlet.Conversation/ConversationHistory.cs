using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Martlet.Conversation;

/// <summary>How one recorded exchange started: something the user typed, something they said, or Martlet bringing up finished
/// background work on its own.</summary>
public enum HistoryInputKind
{
    Typed,
    Spoken,
    Report
}

/// <summary>The apps an exchange can come from: this PC's talk window, or a chat in a messaging app.</summary>
public static class HistoryApps
{
    public const string Pc = "pc", Telegram = "telegram", Discord = "discord", WhatsApp = "whatsapp";

    /// <summary>The app of <paramref name="source"/> (<see cref="Pc"/> for none).</summary>
    public static string Of(HistorySource? source) => source?.App is { Length: > 0 } app ? app : Pc;

    /// <summary>The app's name as people know it.</summary>
    public static string Name(string? app) => app switch
    {
        Telegram => "Telegram",
        Discord => "Discord",
        WhatsApp => "WhatsApp",
        null or "" or Pc => "This PC",
        _ => app
    };
}

/// <summary>Where an exchange happened when it wasn't this PC's talk window: the app (<see cref="HistoryApps"/>), the chat or
/// channel there (<paramref name="Chat"/>, with its server for a Discord server channel), its name, and the app's IDs of the
/// person's message and of Martlet's reply (one per piece a long reply was split into), so deleting or editing it here can do
/// the same there.</summary>
public sealed record HistorySource(string App, string? Chat = null, string? Server = null, string? ChatName = null,
    IReadOnlyList<string>? UserMessages = null, IReadOnlyList<string>? ReplyMessages = null)
{
    public override string ToString() => $"{nameof(HistorySource)} {App}";
}

/// <summary>One side of an exchange: what the person said, or Martlet's reply.</summary>
public enum HistorySide
{
    User,
    Reply
}

/// <summary>One recorded exchange: what the user said (empty for a <see cref="HistoryInputKind.Report"/>), who said it when
/// Martlet recognized the voice (or the person in a messaging app), Martlet's reply, where it happened (null: this PC) and
/// when it was last edited.</summary>
public sealed record HistoryExchange(Guid Id, Guid ConversationId, DateTimeOffset At, HistoryInputKind Kind, string User, string Reply,
    string? Speaker, HistorySource? Source = null, DateTimeOffset? Edited = null)
{
    /// <summary>The app it happened in (<see cref="HistoryApps.Pc"/> for this PC).</summary>
    public string App => HistoryApps.Of(Source);

    public override string ToString() => $"{nameof(HistoryExchange)} {Id} ({Kind}, {At:yyyy-MM-dd HH:mm})";
}

/// <summary>One recorded conversation: from the talk window opening (or Refresh context) to its end, or one chat or channel in a
/// messaging app. <paramref name="Apps"/> are the apps its exchanges came from; <paramref name="ChatName"/> the chat's name
/// when one did.</summary>
public sealed record HistoryConversation(Guid Id, DateTimeOffset Started, DateTimeOffset Ended, int Exchanges, string Preview,
    IReadOnlyList<string>? Apps = null, string? ChatName = null)
{
    public override string ToString() => $"{nameof(HistoryConversation)} {Id} ({Exchanges} exchanges)";
}

public sealed record HistoryHit(HistoryExchange Exchange, double Score, int MatchedTerms);

/// <summary>Counts only, never content: what the record holds and what couldn't be read. <paramref name="Apps"/> counts
/// exchanges per app.</summary>
public sealed record HistoryStats(int Conversations, int Exchanges, long Bytes, int Files, int Skipped, int NotIndexed,
    DateTimeOffset? Oldest, DateTimeOffset? Newest, IReadOnlyDictionary<string, int>? Apps = null);

/// <summary>The record of every conversation Martlet has had on this PC: each finished exchange, appended as one JSON line to a
/// month's file (<c>history-2026-10.jsonl</c>) in its own folder, and kept in a lexical (BM25) index in memory for searching.
/// Appending never rewrites earlier lines, and a line cut short by a crash is skipped when the record is read again. Only
/// deleting and editing rewrite a file (through a temporary file and an atomic replace). The index is loaded once
/// (<see cref="LoadAsync"/>), off any reply's path; searches never touch the disk. No logger: errors carry no content.</summary>
public sealed partial class ConversationHistory
{
    public const string DirectoryName = "conversations";
    public const int MaximumUserCharacters = 8_192;
    public const int MaximumReplyCharacters = 16_384;
    public const int MaximumSpeakerCharacters = 64;
    /// <summary>The newest exchanges indexed when the record is read; older ones stay in their files and count as not indexed.</summary>
    public const int MaximumIndexedExchanges = 100_000;
    public const int MaximumQueryTerms = 24;
    public const int MaximumResults = 50;
    // Worst case for the longest exchange: every character escaped (lines written before relaxed escaping) plus the fields.
    private const int MaximumLineBytes = 262_144;
    private const long MaximumFileBytes = 512L * 1024 * 1024;
    private const int MaximumTokenRunes = 64;
    private const int Schema = 1;
    private const double K1 = 1.2, B = 0.75;
    // Relaxed escaping keeps what was said readable UTF-8 (not \uXXXX), so a line stays well within its bound in any language.
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly object gate = new();
    // Appends, loading and deleting take the files in turn, so an exchange appended while the record loads is read exactly once
    // and the index only changes while they are held. A whole index is built outside the gate and swapped in, so nothing that
    // reads the record (a reply's recall, the window) waits while one is built.
    private readonly SemaphoreSlim files = new(1, 1);
    private readonly TimeProvider clock;
    private Index index = new();
    private Task? loading;
    private volatile bool loaded;
    private int skipped, notIndexed;

    private sealed record Entry(HistoryExchange Exchange, int Length);
    private readonly record struct Posting(int Entry, int Count);

    private sealed class Index
    {
        internal List<Entry> Entries { get; } = [];
        internal Dictionary<string, List<Posting>> Postings { get; } = new(StringComparer.Ordinal);
        internal long TotalLength { get; private set; }

        internal static Index Of(IEnumerable<HistoryExchange> exchanges)
        {
            var built = new Index();
            foreach (var exchange in exchanges) built.Add(exchange);
            return built;
        }

        internal void Add(HistoryExchange exchange)
        {
            var at = Entries.Count;
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var length = 0;
            foreach (var term in Tokenize(exchange.User).Concat(Tokenize(exchange.Reply)))
            {
                counts[term] = counts.GetValueOrDefault(term) + 1;
                length++;
            }
            foreach (var (term, count) in counts)
            {
                if (!Postings.TryGetValue(term, out var list)) Postings[term] = list = [];
                list.Add(new(at, count));
            }
            Entries.Add(new(exchange, length));
            TotalLength += length;
        }
    }

    public ConversationHistory(string directory, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = Path.GetFullPath(directory);
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>The folder holding the month files.</summary>
    public string Directory { get; }

    /// <summary>The record has been read and indexed; searches answer from memory from now on. Never waits.</summary>
    public bool Loaded => loaded;

    /// <summary>Reads the record once and indexes it; later calls wait for the same load (a failed load is tried again). A file
    /// or line that can't be read is skipped and counted.</summary>
    public Task LoadAsync(CancellationToken token = default)
    {
        Task load;
        lock (gate)
        {
            if (loading is null || loading.IsFaulted || loading.IsCanceled)
                loading = Task.Run(() => LoadCoreAsync(CancellationToken.None), CancellationToken.None);
            load = loading;
        }
        return load.WaitAsync(token);
    }

    private async Task LoadCoreAsync(CancellationToken token)
    {
        await files.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var read = new List<HistoryExchange>();
            var bad = 0;
            foreach (var path in MonthFiles(Directory))
            {
                try
                {
                    if (new FileInfo(path).Length > MaximumFileBytes) { bad++; continue; }
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream, new UTF8Encoding(false, false));
                    while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        if (Parse(line) is { } exchange) read.Add(exchange);
                        else bad++;
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { bad++; }
            }
            // Duplicate ids (a copied file) keep the first; the newest exchanges are indexed when there are too many.
            var unique = read.GroupBy(exchange => exchange.Id).Select(group => group.First())
                .OrderBy(exchange => exchange.At).ThenBy(exchange => exchange.Id).ToList();
            var dropped = Math.Max(0, unique.Count - MaximumIndexedExchanges);
            var built = Index.Of(unique.Skip(dropped));
            lock (gate)
            {
                index = built;
                skipped = bad;
                notIndexed = dropped;
                loaded = true;
            }
        }
        finally
        {
            files.Release();
        }
    }

    /// <summary>Records one finished exchange: appended to this month's file at once (flushed to disk), and to the index when it
    /// is loaded. Text over the bounds is cut; control characters other than line breaks and tabs become spaces.</summary>
    public Task<HistoryExchange> AppendAsync(Guid conversation, HistoryInputKind kind, string user, string reply, string? speaker,
        CancellationToken token = default) => AppendAsync(conversation, kind, user, reply, speaker, null, null, token);

    /// <summary>Records one finished exchange that happened in <paramref name="source"/> (null: this PC), under
    /// <paramref name="id"/> when given (so the one recording it can find it again).</summary>
    public async Task<HistoryExchange> AppendAsync(Guid conversation, HistoryInputKind kind, string user, string reply, string? speaker,
        HistorySource? source, Guid? id = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(reply);
        if (conversation == Guid.Empty) throw new ArgumentException("A conversation id is required.", nameof(conversation));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        var name = speaker is null ? null : Bound(speaker, MaximumSpeakerCharacters).Trim();
        var exchange = new HistoryExchange(id is { } chosen && chosen != Guid.Empty ? chosen : Guid.NewGuid(), conversation, clock.GetUtcNow(),
            kind, Bound(user, MaximumUserCharacters), Bound(reply, MaximumReplyCharacters), string.IsNullOrEmpty(name) ? null : name,
            Clean(source));
        var line = JsonSerializer.Serialize(Line.From(exchange), Json);
        await files.WaitAsync(token).ConfigureAwait(false);
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var path = Path.Combine(Directory, FileName(exchange.At));
            // Another program (a virus scanner, a backup) may hold the file for a moment: try again briefly before giving up.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await WriteLineAsync(path, line).ConfigureAwait(false);
                    break;
                }
                catch (IOException) when (attempt < AppendAttempts && File.Exists(path))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), CancellationToken.None).ConfigureAwait(false);
                }
            }
            lock (gate)
                if (loaded) index.Add(exchange);
        }
        finally
        {
            files.Release();
        }
        return exchange;
    }

    private const int AppendAttempts = 4;

    private static async Task WriteLineAsync(string path, string line)
    {
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        // A line a crash cut short is ended first, so the new line is read on its own.
        var bytes = new List<byte>(Encoding.UTF8.GetByteCount(line) + 2);
        if (stream.Length > 0)
        {
            stream.Seek(-1, SeekOrigin.End);
            if (stream.ReadByte() != '\n') bytes.Add((byte)'\n');
        }
        stream.Seek(0, SeekOrigin.End);
        bytes.AddRange(Encoding.UTF8.GetBytes(line));
        bytes.Add((byte)'\n');
        await stream.WriteAsync(bytes.ToArray(), CancellationToken.None).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>The newest conversations first, each with its first words, the apps it happened in and its chat's name.</summary>
    public IReadOnlyList<HistoryConversation> Conversations(int limit = 500)
    {
        lock (gate)
            return index.Entries.GroupBy(entry => entry.Exchange.ConversationId)
                .Select(group =>
                {
                    var first = group.First().Exchange;
                    var preview = group.Select(entry => entry.Exchange).FirstOrDefault(e => e.User.Length > 0)?.User ?? first.Reply;
                    var apps = group.Select(entry => entry.Exchange.App).Distinct(StringComparer.Ordinal).ToArray();
                    var chat = group.Select(entry => entry.Exchange.Source?.ChatName).LastOrDefault(name => !string.IsNullOrEmpty(name));
                    return new HistoryConversation(group.Key, first.At, group.Last().Exchange.At, group.Count(), Preview(preview, 80), apps, chat);
                })
                .OrderByDescending(conversation => conversation.Ended).ThenBy(conversation => conversation.Id)
                .Take(Math.Clamp(limit, 1, 10_000)).ToArray();
    }

    /// <summary>One conversation's exchanges, oldest first.</summary>
    public IReadOnlyList<HistoryExchange> Exchanges(Guid conversation)
    {
        lock (gate)
            return index.Entries.Where(entry => entry.Exchange.ConversationId == conversation).Select(entry => entry.Exchange).ToArray();
    }

    /// <summary>Every exchange <paramref name="predicate"/> picks, oldest first.</summary>
    public IReadOnlyList<HistoryExchange> Where(Func<HistoryExchange, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        lock (gate) return index.Entries.Select(entry => entry.Exchange).Where(predicate).ToArray();
    }

    /// <summary>The exchange recorded as <paramref name="id"/>, or null.</summary>
    public HistoryExchange? Exchange(Guid id)
    {
        lock (gate)
        {
            var entries = index.Entries;
            for (var at = entries.Count - 1; at >= 0; at--)
                if (entries[at].Exchange.Id == id) return entries[at].Exchange;
            return null;
        }
    }

    /// <summary>The newest exchange of <paramref name="app"/>'s chat <paramref name="chat"/> whose person's message was
    /// <paramref name="message"/> there, or null.</summary>
    public HistoryExchange? FindMessage(string app, string chat, string message)
    {
        lock (gate)
        {
            var entries = index.Entries;
            for (var at = entries.Count - 1; at >= 0; at--)
                if (entries[at].Exchange.Source is { } source && source.App == app && source.Chat == chat &&
                    source.UserMessages?.Contains(message, StringComparer.Ordinal) == true)
                    return entries[at].Exchange;
            return null;
        }
    }

    /// <summary>The newest <paramref name="limit"/> exchanges at or after <paramref name="from"/> and before <paramref name="to"/>,
    /// oldest first, leaving out the <paramref name="exclude"/> conversation (and those <paramref name="include"/> leaves out).</summary>
    public IReadOnlyList<HistoryExchange> Between(DateTimeOffset from, DateTimeOffset to, Guid? exclude, int limit,
        Func<HistoryExchange, bool>? include = null)
    {
        limit = Math.Clamp(limit, 1, MaximumResults);
        lock (gate)
            return index.Entries.Select(entry => entry.Exchange)
                .Where(exchange => exchange.At >= from && exchange.At < to && exchange.ConversationId != exclude && include?.Invoke(exchange) != false)
                .TakeLast(limit).ToArray();
    }

    /// <summary>The exchanges that best match <paramref name="terms"/> (BM25 over what the user and Martlet said), those matching
    /// the most terms first, then by score, within the optional time window, leaving out the <paramref name="exclude"/>
    /// conversation (and those <paramref name="include"/> leaves out). Ties go to the newer exchange.</summary>
    public IReadOnlyList<HistoryHit> Search(IReadOnlyCollection<string> terms, DateTimeOffset? from, DateTimeOffset? to, Guid? exclude,
        int limit, Func<HistoryExchange, bool>? include = null)
    {
        ArgumentNullException.ThrowIfNull(terms);
        limit = Math.Clamp(limit, 1, MaximumResults);
        var query = terms.Select(term => term.ToLowerInvariant()).Distinct(StringComparer.Ordinal).Take(MaximumQueryTerms).ToArray();
        lock (gate)
        {
            var entries = index.Entries;
            if (query.Length == 0 || entries.Count == 0) return [];
            var count = entries.Count;
            var average = (double)index.TotalLength / count;
            var scores = new Dictionary<int, (double Score, int Matched)>();
            foreach (var term in query)
            {
                if (!index.Postings.TryGetValue(term, out var list)) continue;
                var idf = Math.Log(1 + (count - list.Count + 0.5) / (list.Count + 0.5));
                foreach (var posting in list)
                {
                    var exchange = entries[posting.Entry].Exchange;
                    if (exchange.ConversationId == exclude || from is { } start && exchange.At < start || to is { } end && exchange.At >= end ||
                        include?.Invoke(exchange) == false)
                        continue;
                    var length = entries[posting.Entry].Length;
                    var weight = idf * posting.Count * (K1 + 1) / (posting.Count + K1 * (1 - B + B * length / Math.Max(1, average)));
                    var (score, matched) = scores.GetValueOrDefault(posting.Entry);
                    scores[posting.Entry] = (score + weight, matched + 1);
                }
            }
            return scores.OrderByDescending(pair => pair.Value.Matched).ThenByDescending(pair => pair.Value.Score)
                .ThenByDescending(pair => pair.Key)
                .Take(limit).Select(pair => new HistoryHit(entries[pair.Key].Exchange, pair.Value.Score, pair.Value.Matched)).ToArray();
        }
    }

    /// <summary>Deletes one conversation from its files and the index, on the thread pool. Returns how many exchanges went.</summary>
    public Task<int> DeleteAsync(Guid conversation, CancellationToken token = default) =>
        Task.Run(() => DeleteCoreAsync(conversation, token), token);

    private async Task<int> DeleteCoreAsync(Guid conversation, CancellationToken token)
    {
        await LoadAsync(token).ConfigureAwait(false);
        await files.WaitAsync(token).ConfigureAwait(false);
        try
        {
            HistoryExchange[] kept;
            lock (gate)
                kept = index.Entries.Where(entry => entry.Exchange.ConversationId != conversation).Select(entry => entry.Exchange).ToArray();
            // Every month file is checked, so a line of it that couldn't be read when the record loaded goes too (found by its id).
            var named = "\"conversation\":\"" + conversation.ToString("D") + "\"";
            var removed = 0;
            foreach (var path in MonthFiles(Directory))
                removed += Rewrite(path, (exchange, line) =>
                    (exchange is not null ? exchange.ConversationId == conversation : line.Contains(named, StringComparison.OrdinalIgnoreCase))
                        ? null : line);
            var rebuilt = Index.Of(kept);
            lock (gate) index = rebuilt;
            return removed;
        }
        finally
        {
            files.Release();
        }
    }

    /// <summary>Deletes every month file of the record and empties the index, on the thread pool. Returns how many files went.</summary>
    public Task<int> DeleteAllAsync(CancellationToken token = default) => Task.Run(() => DeleteAllCoreAsync(token), token);

    /// <summary>Changes one exchange (<paramref name="change"/> returns its new form, or null to delete it) in its file and the
    /// index, on the thread pool: its id, conversation and time stay; text over the bounds is cut. Returns the exchange before and
    /// after (both null when there is no such exchange).</summary>
    public Task<(HistoryExchange? Before, HistoryExchange? After)> ChangeAsync(Guid id, Func<HistoryExchange, HistoryExchange?> change,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        return Task.Run(() => ChangeCoreAsync(id, change, token), token);
    }

    private async Task<(HistoryExchange? Before, HistoryExchange? After)> ChangeCoreAsync(Guid id, Func<HistoryExchange, HistoryExchange?> change,
        CancellationToken token)
    {
        await LoadAsync(token).ConfigureAwait(false);
        await files.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (Exchange(id) is not { } before) return (null, null);
            var after = change(before) is { } changed
                ? changed with
                {
                    Id = before.Id, ConversationId = before.ConversationId, At = before.At, User = Bound(changed.User ?? "", MaximumUserCharacters),
                    Reply = Bound(changed.Reply ?? "", MaximumReplyCharacters), Source = Clean(changed.Source)
                }
                : null;
            var line = after is null ? null : ToLine(after);
            string? Map(HistoryExchange? exchange, string text) => exchange?.Id == id ? line : text;
            // Its own month's file first; another file only when a copied line put it there.
            var own = Path.Combine(Directory, FileName(before.At));
            if (Rewrite(own, Map) == 0)
                foreach (var path in MonthFiles(Directory).Where(path => !string.Equals(path, own, StringComparison.OrdinalIgnoreCase)))
                    if (Rewrite(path, Map) > 0) break;
            List<HistoryExchange>? rebuild = null;
            lock (gate)
            {
                var entries = index.Entries;
                var at = entries.FindLastIndex(entry => entry.Exchange.Id == id);
                if (at >= 0 && after is not null && after.User == before.User && after.Reply == before.Reply)
                    entries[at] = entries[at] with { Exchange = after };
                else if (at >= 0)
                {
                    rebuild = entries.Select(entry => entry.Exchange).ToList();
                    if (after is null) rebuild.RemoveAt(at);
                    else rebuild[at] = after;
                }
            }
            if (rebuild is not null)
            {
                var built = Index.Of(rebuild);
                lock (gate) index = built;
            }
            return (before, after);
        }
        finally
        {
            files.Release();
        }
    }

    private async Task<int> DeleteAllCoreAsync(CancellationToken token)
    {
        await LoadAsync(token).ConfigureAwait(false);
        await files.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var deleted = 0;
            foreach (var path in MonthFiles(Directory))
            {
                File.Delete(path);
                deleted++;
            }
            lock (gate)
            {
                index = new();
                skipped = notIndexed = 0;
            }
            return deleted;
        }
        finally
        {
            files.Release();
        }
    }

    /// <summary>What the record holds: counted from the index, plus the files' size on disk.</summary>
    public HistoryStats Stats
    {
        get
        {
            long bytes = 0;
            var count = 0;
            foreach (var path in MonthFiles(Directory))
            {
                try { bytes += new FileInfo(path).Length; count++; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            lock (gate)
            {
                var entries = index.Entries;
                var apps = entries.GroupBy(entry => entry.Exchange.App, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
                return new(entries.Select(entry => entry.Exchange.ConversationId).Distinct().Count(), entries.Count, bytes, count, skipped,
                    notIndexed, entries.Count == 0 ? null : entries[0].Exchange.At, entries.Count == 0 ? null : entries[^1].Exchange.At, apps);
            }
        }
    }

    /// <summary>Lowercased letter-and-digit words of <paramref name="text"/>, each once, in order (words over 64 letters are
    /// left out), the same way the index reads what was said.</summary>
    public static IReadOnlyList<string> Terms(string text) => Tokenize(text).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>The month files in <paramref name="directory"/> (none when it doesn't exist).</summary>
    public static IReadOnlyList<string> MonthFiles(string directory)
    {
        try
        {
            if (!System.IO.Directory.Exists(directory)) return [];
            return System.IO.Directory.EnumerateFiles(directory, "history-*.jsonl")
                .Where(path => MonthFile().IsMatch(Path.GetFileName(path))).Order(StringComparer.Ordinal).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>Collapses whitespace and cuts <paramref name="text"/> to <paramref name="characters"/>, ending with an ellipsis.</summary>
    public static string Preview(string text, int characters)
    {
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= characters ? line : line[..Math.Max(1, characters - 1)].TrimEnd() + "…";
    }

    /// <summary>One exchange as its line in a month file (without the line break), for tools that prepare a record in bulk.</summary>
    public static string ToLine(HistoryExchange exchange)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        return JsonSerializer.Serialize(Line.From(exchange), Json);
    }

    /// <summary>The month file an exchange made at <paramref name="at"/> goes to.</summary>
    public static string FileName(DateTimeOffset at) =>
        "history-" + at.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture) + ".jsonl";

    [GeneratedRegex(@"^history-\d{4}-\d{2}\.jsonl$", RegexOptions.CultureInvariant)]
    private static partial Regex MonthFile();

    // Rewrites one month file with each line as <paramref name="map"/> gives it (the line itself to keep it, another to change it,
    // null to drop it; the exchange is null for a line that can't be read), through a temporary file in the same folder and an
    // atomic replace. Returns how many lines changed or went.
    private static int Rewrite(string path, Func<HistoryExchange?, string, string?> map)
    {
        if (!File.Exists(path)) return 0;
        var kept = new StringBuilder();
        var changed = 0;
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var next = map(Parse(line), line);
            if (!ReferenceEquals(next, line)) changed++;
            if (next is not null) kept.Append(next).Append('\n');
        }
        if (changed == 0) return 0;
        if (kept.Length == 0)
        {
            File.Delete(path);
            return changed;
        }
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(Encoding.UTF8.GetBytes(kept.ToString()));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return changed;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var token = new StringBuilder();
        var runes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                if (runes < MaximumTokenRunes) token.Append(Rune.ToLowerInvariant(rune).ToString());
                runes++;
                continue;
            }
            if (token.Length > 0 && runes <= MaximumTokenRunes) yield return token.ToString();
            token.Clear();
            runes = 0;
        }
        if (token.Length > 0 && runes <= MaximumTokenRunes) yield return token.ToString();
    }

    private static string Bound(string text, int characters)
    {
        var clean = new string(text.Select(c => char.IsControl(c) && c is not '\n' and not '\t' ? ' ' : c).ToArray());
        if (clean.Length <= characters) return clean;
        var cut = clean[..characters];
        return char.IsHighSurrogate(cut[^1]) ? cut[..^1] : cut;
    }

    private static HistoryExchange? Parse(string line)
    {
        if (Encoding.UTF8.GetByteCount(line) > MaximumLineBytes) return null;
        try
        {
            var read = JsonSerializer.Deserialize<Line>(line, Json);
            if (read is not { V: Schema } || read.Id == Guid.Empty || read.Conversation == Guid.Empty || read.At == default || read.User is null ||
                read.Reply is null || read.User.Length > MaximumUserCharacters || read.Reply.Length > MaximumReplyCharacters ||
                read.Speaker is { Length: > MaximumSpeakerCharacters } || Kind(read.Kind) is not { } kind)
                return null;
            return new(read.Id, read.Conversation, read.At.ToUniversalTime(), kind, read.User, read.Reply,
                string.IsNullOrWhiteSpace(read.Speaker) ? null : read.Speaker, Clean(read.Source?.ToSource()), read.Edited?.ToUniversalTime());
        }
        catch (JsonException) { return null; }
    }

    private const int MaximumSourceText = 200, MaximumMessageIds = 64;

    // A source as it is kept: no empty app, short names and IDs, a bounded number of message IDs; this PC's talk window is none.
    private static HistorySource? Clean(HistorySource? source)
    {
        if (source is null || string.IsNullOrWhiteSpace(source.App) || source.App == HistoryApps.Pc) return null;
        static string? Short(string? text) => string.IsNullOrWhiteSpace(text) ? null : Bound(text.Trim(), MaximumSourceText);
        static IReadOnlyList<string>? Ids(IReadOnlyList<string>? ids)
        {
            var kept = ids?.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => Bound(id.Trim(), MaximumSourceText))
                .Take(MaximumMessageIds).ToArray();
            return kept is { Length: > 0 } ? kept : null;
        }
        return new(Bound(source.App.Trim().ToLowerInvariant(), 32), Short(source.Chat), Short(source.Server), Short(source.ChatName),
            Ids(source.UserMessages), Ids(source.ReplyMessages));
    }

    private static HistoryInputKind? Kind(string? kind) => kind switch
    {
        "typed" => HistoryInputKind.Typed,
        "spoken" => HistoryInputKind.Spoken,
        "report" => HistoryInputKind.Report,
        _ => null
    };

    private sealed class Line
    {
        [JsonPropertyName("v")] public int V { get; set; }
        [JsonPropertyName("id")] public Guid Id { get; set; }
        [JsonPropertyName("conversation")] public Guid Conversation { get; set; }
        [JsonPropertyName("at")] public DateTimeOffset At { get; set; }
        [JsonPropertyName("kind")] public string? Kind { get; set; }
        [JsonPropertyName("speaker")] public string? Speaker { get; set; }
        [JsonPropertyName("user")] public string? User { get; set; }
        [JsonPropertyName("reply")] public string? Reply { get; set; }
        [JsonPropertyName("source")] public SourceLine? Source { get; set; }
        [JsonPropertyName("edited")] public DateTimeOffset? Edited { get; set; }

        internal static Line From(HistoryExchange exchange) => new()
        {
            V = Schema, Id = exchange.Id, Conversation = exchange.ConversationId, At = exchange.At,
            Kind = exchange.Kind switch { HistoryInputKind.Spoken => "spoken", HistoryInputKind.Report => "report", _ => "typed" },
            Speaker = exchange.Speaker, User = exchange.User, Reply = exchange.Reply, Source = SourceLine.From(exchange.Source),
            Edited = exchange.Edited
        };
    }

    private sealed class SourceLine
    {
        [JsonPropertyName("app")] public string? App { get; set; }
        [JsonPropertyName("chat")] public string? Chat { get; set; }
        [JsonPropertyName("server")] public string? Server { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("userIds")] public string[]? UserIds { get; set; }
        [JsonPropertyName("replyIds")] public string[]? ReplyIds { get; set; }

        internal static SourceLine? From(HistorySource? source) => source is null ? null : new()
        {
            App = source.App, Chat = source.Chat, Server = source.Server, Name = source.ChatName,
            UserIds = source.UserMessages?.ToArray(), ReplyIds = source.ReplyMessages?.ToArray()
        };

        internal HistorySource? ToSource() => string.IsNullOrWhiteSpace(App) ? null : new(App, Chat, Server, Name, UserIds, ReplyIds);
    }
}
