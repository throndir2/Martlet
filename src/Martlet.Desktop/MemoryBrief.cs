using System.Globalization;
using System.IO;
using System.Text;
using Martlet.Core.Settings;
using Martlet.Memory;
using Martlet.Providers;

// Also built into Martlet's MCP server (memory_brief_check), in its own namespace.
#if MARTLET_MCP
namespace Martlet.Mcp.Shared;
#else
namespace Martlet.Desktop;
#endif

/// <summary>The facts a memory brief is written from: the facts of every memory space recall reads, the name of each person a
/// fact belongs to (by voice ID; a fact of a voice not named here is left out), the names the user goes by (their own voices),
/// the prompts in use and whether every space was read (<paramref name="Complete"/>: false while another space's copy is still
/// loading, so a brief is never judged on part of the facts).</summary>
internal sealed record MemoryBriefFacts(IReadOnlyList<MemoryFact> Facts, IReadOnlyDictionary<string, string> People,
    IReadOnlyList<string> Owner, PromptSettings? Prompts, bool Complete = true)
{
    public override string ToString() => $"{nameof(MemoryBriefFacts)} {{ Facts = {Facts.Count}, People = {People.Count}, Complete = {Complete} }}";
}

/// <summary>How the facts changed since a brief was written: facts it doesn't have yet (<paramref name="New"/>), facts it was
/// written from that are gone or were changed (<paramref name="Lost"/>) and whether a person in it has another name now.</summary>
internal readonly record struct MemoryBriefChange(int New, int Lost, bool Renamed);

/// <summary>The memory brief (docs/MEMORY.md, "The memory brief"): which facts it is written from, the text the model gets, the
/// answer as a brief and the notes that carry it with the first message of each conversation.</summary>
internal static class MemoryBriefWriter
{
    internal const string Label = "[MARTLET_MEMORY_BRIEF]";
    internal const string EndLabel = "[/MARTLET_MEMORY_BRIEF]";
    /// <summary>A brief is first written once there are this many facts.</summary>
    internal const int MinimumFacts = 3;
    /// <summary>A brief is written again once this many facts are new since it was written.</summary>
    internal const int NewFactsToWriteAgain = 3;
    /// <summary>The most bytes of facts the model gets (small pool members have small context windows).</summary>
    internal const int InputBytes = 9_000;
    /// <summary>The most facts the model gets.</summary>
    internal const int MaximumFacts = 300;
    /// <summary>The most people a basis names.</summary>
    internal const int MaximumPeople = 90;
    internal const int MaximumFactCharacters = 400;
    /// <summary>A shorter answer isn't a brief.</summary>
    internal const int MinimumCharacters = 40;
    internal const string Purpose = "Writing the memory brief";

    /// <summary>Whether a fact may go in a brief now: it hasn't expired. A fact another fact replaced (history) never does.</summary>
    internal static bool Current(MemoryFact fact, DateTimeOffset now) => !fact.IsHistory && !fact.Retention.HasExpired(now);

    /// <summary>Every fact a brief may use, each once: current, about no one in particular or about a person named in
    /// <see cref="MemoryBriefFacts.People"/> (a forgotten voice's facts are left out).</summary>
    internal static IReadOnlyList<MemoryFact> Usable(MemoryBriefFacts facts, DateTimeOffset now)
    {
        var seen = new HashSet<Guid>();
        return [.. facts.Facts.Where(fact => Current(fact, now) && (fact.VoiceId is null || facts.People.ContainsKey(fact.VoiceId)) && seen.Add(fact.Id))];
    }

    /// <summary>The facts the model gets: those the user saved first, then the newest, while they fit <see cref="InputBytes"/>
    /// and <see cref="MaximumFacts"/>.</summary>
    internal static IReadOnlyList<MemoryFact> Select(MemoryBriefFacts facts, DateTimeOffset now)
    {
        var picked = new List<MemoryFact>();
        var bytes = 0;
        foreach (var fact in Usable(facts, now).OrderByDescending(f => f.LastModifiedBy.SourceKind != MemorySourceKind.Conversation)
                     .ThenByDescending(f => f.UpdatedAtUtc).ThenBy(f => f.Id))
        {
            if (picked.Count >= MaximumFacts) break;
            var size = Encoding.UTF8.GetByteCount(Line(fact, facts.People)) + 3;
            if (bytes + size > InputBytes) continue;
            picked.Add(fact);
            bytes += size;
        }
        return picked;
    }

    /// <summary>One fact as the model reads it: whose it is ("[Sam] "), its words on one line and when it last changed.</summary>
    internal static string Line(MemoryFact fact, IReadOnlyDictionary<string, string> people)
    {
        var words = Plain(fact.Content);
        if (words.Length > MaximumFactCharacters) words = words[..MaximumFactCharacters].TrimEnd() + "…";
        var whose = fact.VoiceId is { } id && people.TryGetValue(id, out var name) ? $"[{Plain(name)}] " : "";
        return $"{whose}{words} ({fact.UpdatedAtUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)})";
    }

    /// <summary>The text the model gets: the names the user goes by, then the facts, newest first.</summary>
    internal static string Input(MemoryBriefFacts facts, IReadOnlyList<MemoryFact> selected)
    {
        var text = new StringBuilder();
        var owner = facts.Owner.Select(Plain).Where(name => name.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        if (owner.Length > 0)
            text.Append("The user goes by ").Append(string.Join(" or ", owner.Select(name => $"[{name}]")))
                .Append(": facts with that name are about the user.\n");
        text.Append("Facts, newest first:\n");
        foreach (var fact in selected.OrderByDescending(f => f.UpdatedAtUtc).ThenBy(f => f.Id))
            text.Append("- ").Append(Line(fact, facts.People)).Append('\n');
        return text.ToString().TrimEnd();
    }

    /// <summary>What a brief written from <paramref name="selected"/> is based on: each fact's <see cref="Key"/>, and the name of
    /// each person it names, in one order.</summary>
    internal static IReadOnlyList<string> Basis(IReadOnlyList<MemoryFact> selected, IReadOnlyDictionary<string, string> people) =>
        [.. selected.Select(Key).Order(StringComparer.Ordinal),
         .. selected.Select(f => f.VoiceId).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
             .Take(MaximumPeople).Select(id => Person(id, people.GetValueOrDefault(id) ?? ""))];

    /// <summary>A fact as a brief knows it: its ID and a digest of what the brief reads (its words and whose it is), so a change
    /// that leaves those alone (search words from tidying memory, a new revision with the same text) keeps the brief.</summary>
    internal static string Key(MemoryFact fact) => fact.Id.ToString("N") + ":" + Convert.ToHexStringLower(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(fact.Content + "\n" + fact.VoiceId)))[..16];

    private static string Person(string id, string name)
    {
        var plain = Plain(name);
        return "~" + id + "=" + (plain.Length > 40 ? plain[..40] : plain);
    }

    /// <summary>How <paramref name="usable"/> (every fact a brief may use now) and <paramref name="basis"/> (what a brief written
    /// now would be based on) differ from what <paramref name="brief"/> was written from.</summary>
    internal static MemoryBriefChange Compare(MemoryBriefFile brief, IReadOnlyList<string> basis, IReadOnlyList<MemoryFact> usable)
    {
        var now = usable.Select(Key).ToHashSet(StringComparer.Ordinal);
        var old = brief.Basis.Where(entry => !entry.StartsWith('~')).ToHashSet(StringComparer.Ordinal);
        var lost = old.Count(entry => !now.Contains(entry));
        var added = basis.Count(entry => !entry.StartsWith('~') && !old.Contains(entry));
        static Dictionary<string, string> Names(IEnumerable<string> entries) => entries.Where(e => e.StartsWith('~') && e.Contains('='))
            .Select(e => (Id: e[1..e.IndexOf('=')], Name: e[(e.IndexOf('=') + 1)..])).GroupBy(p => p.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);
        var before = Names(brief.Basis);
        var renamed = Names(basis).Any(person => before.TryGetValue(person.Key, out var name) && name != person.Value);
        return new(added, lost, renamed);
    }

    /// <summary>The model's answer as a brief (plain lines without Markdown or control characters, at most
    /// <see cref="MemoryBriefFile.MaximumCharacters"/>), or null when it isn't one.</summary>
    internal static string? Clean(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer)) return null;
        var text = answer.Replace("\r", "", StringComparison.Ordinal);
        // A model that thinks out loud first: only what follows its thinking.
        var thought = text.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (thought >= 0) text = text[(thought + "</think>".Length)..];
        var lines = text.Split('\n')
            .Select(line => new string([.. line.Select(c => c == '\t' ? ' ' : c).Where(c => !char.IsControl(c))]).Trim()
                .TrimStart('#', '*', ' ').Replace("**", "", StringComparison.Ordinal).Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("```", StringComparison.Ordinal));
        text = Strip(string.Join('\n', lines)).Trim();
        if (text.Length > MemoryBriefFile.MaximumCharacters)
        {
            var cut = text[..MemoryBriefFile.MaximumCharacters];
            var end = cut.LastIndexOf('\n');
            text = end > MemoryBriefFile.MaximumCharacters / 2 ? cut[..end] : cut[..(MemoryBriefFile.MaximumCharacters - 1)].TrimEnd() + "…";
        }
        return text.Length < MinimumCharacters || text.Equals("NONE", StringComparison.OrdinalIgnoreCase) ? null : text;
    }

    /// <summary>The owner's own text as a brief: plain lines without control characters, or null when it is empty; a problem
    /// when it is too long.</summary>
    internal static (string? Text, string? Problem) Own(string? text)
    {
        var lines = (text ?? "").Replace("\r", "", StringComparison.Ordinal).Split('\n')
            .Select(line => Strip(new string([.. line.Select(c => c == '\t' ? ' ' : c).Where(c => !char.IsControl(c))])).TrimEnd());
        var own = string.Join('\n', lines).Trim();
        if (own.Length == 0) return (null, null);
        return own.Length > MemoryBriefFile.MaximumCharacters
            ? (null, $"Keep the brief under {MemoryBriefFile.MaximumCharacters:N0} characters (it has {own.Length:N0}).")
            : (own, null);
    }

    /// <summary>The notes that carry <paramref name="brief"/>: between <see cref="Label"/> lines, opened by Companion › Prompts ›
    /// Memory brief, with your message. Null when there is no brief or the owner emptied the prompt.</summary>
    internal static string? Notes(string? brief, PromptSettings? prompts)
    {
        if (string.IsNullOrWhiteSpace(brief)) return null;
        if (PromptSettings.Fill(prompts, PromptCatalog.MemoryBrief) is not { } opening || opening.Trim().Length == 0) return null;
        return new StringBuilder(Label).Append('\n').Append(opening.Trim()).Append('\n').Append(brief.Trim()).Append('\n')
            .Append(EndLabel).ToString();
    }

    /// <summary>Whether a message of the conversation sent already carries a brief.</summary>
    internal static bool Carried(IReadOnlyList<TextHistoryMessage> sent) =>
        sent.Any(message => message.Role == TextHistoryRole.User && message.Text.Contains(Label, StringComparison.Ordinal));

    // Martlet's own labels can't open or close blocks in a brief.
    private static string Strip(string text) => text
        .Replace(Label, "", StringComparison.Ordinal).Replace(EndLabel, "", StringComparison.Ordinal)
        .Replace("MARTLET_NOTES", "notes", StringComparison.OrdinalIgnoreCase)
        .Replace("MARTLET_LOCAL_MEMORY", "memory", StringComparison.OrdinalIgnoreCase);

    private static string Plain(string text) =>
        Strip(string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))).Replace("[", "(", StringComparison.Ordinal)
            .Replace("]", ")", StringComparison.Ordinal);

    /// <summary>"just now", "5 minutes ago", "3 hours ago" or the date.</summary>
    internal static string Ago(DateTimeOffset at, DateTimeOffset now)
    {
        var age = now - at;
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} minute{((int)age.TotalMinutes == 1 ? "" : "s")} ago";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours} hour{((int)age.TotalHours == 1 ? "" : "s")} ago";
        return "on " + at.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}

/// <summary>Writes the memory brief in the background and gives it to the first message of each conversation (docs/MEMORY.md,
/// "The memory brief"). After facts change (and settle), it reads the facts once the conversation is quiet, checks whether the
/// saved brief still holds (a brief whose fact was forgotten or changed is not sent until it is written again) and, when enough
/// changed, asks a model to write it again, within limits. The reply's path only reads what is in memory here.</summary>
internal sealed class MemoryBriefService : IDisposable
{
    /// <summary>How long after a change a check waits, so a burst of changes (a sync, several facts) makes one check.</summary>
    internal static TimeSpan Settle { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>The first check after Martlet starts, once the memory spaces had time to load.</summary>
    internal static TimeSpan StartDelay { get; set; } = TimeSpan.FromSeconds(45);
    /// <summary>A brief taken from another computer isn't written again here sooner (the facts it names may still be on their
    /// way through the memory sync).</summary>
    internal static readonly TimeSpan AfterFollowing = TimeSpan.FromMinutes(2);
    /// <summary>The least time between two briefs written here on their own; after a fact it used was lost, the shorter one.</summary>
    internal static readonly TimeSpan Gap = TimeSpan.FromMinutes(10), GapAfterLoss = TimeSpan.FromMinutes(1);
    /// <summary>At most this many tries an hour on this computer.</summary>
    internal const int PerHour = 6;
    internal static readonly TimeSpan BusyRetry = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan LoadingRetry = TimeSpan.FromSeconds(30);

    private readonly string directory;
    private readonly Func<CancellationToken, Task<MemoryBriefFacts?>> read;
    private readonly Func<string, string, CancellationToken, Task<(string? Answer, string? Failure)>> write;
    private readonly Func<bool> busy;
    private readonly TimeProvider clock;
    private readonly object gate = new();
    private readonly SemaphoreSlim running = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Queue<DateTimeOffset> tries = new();
    private MemoryBriefFile current;
    private bool usable;
    private bool writing;
    private string? problem;
    private int? factsNow;
    private DateTimeOffset? lastTry, followedAt, due;
    private TaskCompletionSource wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? runner;
    private int sent;
    private bool disposed;

    /// <param name="read">The facts now, or null while memory is off or can't be read.</param>
    /// <param name="write">Asks a model (a Thinking pool member, never the conversation's own model while it answers) with the
    /// instructions and the facts; the answer, or null with why not.</param>
    /// <param name="busy">Whether the conversation is busy now (a reply runs or speaks, or you talk): facts are read only while
    /// it is quiet.</param>
    internal MemoryBriefService(string directory, Func<CancellationToken, Task<MemoryBriefFacts?>> read,
        Func<string, string, CancellationToken, Task<(string? Answer, string? Failure)>> write, Func<bool> busy,
        TimeProvider? clock = null, bool start = true)
    {
        this.directory = directory;
        this.read = read;
        this.write = write;
        this.busy = busy;
        this.clock = clock ?? TimeProvider.System;
        current = MemoryBriefFile.Load(directory);
        // A saved brief is trusted from the start; the first check confirms that its facts still hold.
        usable = current.Text is not null;
        if (start) Poke(StartDelay);
    }

    /// <summary>The log's lines (counts and times only, never a fact or the brief).</summary>
    internal Action<string>? Log { get; init; }

    /// <summary>Raised off the window's thread when the brief, its state or a problem changed.</summary>
    internal event Action? Changed;

    internal MemoryBriefFile Current { get { lock (gate) return current; } }
    /// <summary>Whether the brief goes with messages now: it is the owner's, or no fact it was written from was lost.</summary>
    internal bool Usable { get { lock (gate) return usable && current.On && current.Text is not null; } }
    internal bool Writing { get { lock (gate) return writing; } }
    internal string? Problem { get { lock (gate) return problem; } }
    /// <summary>How many facts a brief could use at the last check.</summary>
    internal int? FactsNow { get { lock (gate) return factsNow; } }
    /// <summary>How many messages carried the brief since Martlet started.</summary>
    internal int Sent { get { lock (gate) return sent; } }

    /// <summary>The notes for a message (the reply's path: memory only, nothing is read or waited for): the brief, when it is on,
    /// written, still holds and the conversation <paramref name="sent"/> doesn't carry one yet. Null otherwise.</summary>
    internal string? Notes(IReadOnlyList<TextHistoryMessage> sent, PromptSettings? prompts)
    {
        string text;
        lock (gate)
        {
            if (!usable || !current.On || current.Text is not { } brief) return null;
            text = brief;
        }
        if (MemoryBriefWriter.Carried(sent) || MemoryBriefWriter.Notes(text, prompts) is not { } notes) return null;
        lock (gate) this.sent++;
        return notes;
    }

    /// <summary>Checks the brief after <paramref name="after"/> (default <see cref="Settle"/>); an earlier check already due
    /// stays.</summary>
    internal void Poke(TimeSpan? after = null)
    {
        lock (gate)
        {
            if (disposed) return;
            var at = clock.GetUtcNow() + (after ?? Settle);
            if (due is { } already && already <= at && runner is { IsCompleted: false }) return;
            due = due is { } earlier && earlier < at ? earlier : at;
            if (runner is { IsCompleted: false })
            {
                wake.TrySetResult();
                return;
            }
            runner = Task.Run(RunAsync);
        }
    }

    /// <summary>The facts changed (remembering, the Memory window, the memory sync...).</summary>
    internal void FactsChanged() => Poke();

    /// <summary>memory-brief.json changed from elsewhere: another computer's brief, or another account's on a switch. It is read
    /// again and goes with messages as it is; it isn't written again here for a while.</summary>
    internal void Followed()
    {
        lock (gate)
        {
            current = MemoryBriefFile.Load(directory);
            usable = current.Text is not null;
            followedAt = clock.GetUtcNow();
            problem = null;
        }
        Changed?.Invoke();
        Poke();
    }

    /// <summary>Turns the brief on or off (Companion › Memory › Memory brief). Off, it is neither written nor sent.</summary>
    internal string? SetOn(bool on)
    {
        MemoryBriefFile next;
        lock (gate)
        {
            current = MemoryBriefFile.Load(directory);
            if (current.On == on) return null;
            next = current with { On = on };
        }
        if (Save(next) is { } failed) return failed;
        Log?.Invoke($"Memory brief: turned {(on ? "on" : "off")}.");
        if (on) Poke(TimeSpan.FromSeconds(1));
        return null;
    }

    /// <summary>Saves the owner's own text as the brief (Martlet then never changes it on its own); empty text lets Martlet write
    /// it again from the facts. Null, or what went wrong.</summary>
    internal string? SaveOwn(string? text)
    {
        var (own, invalid) = MemoryBriefWriter.Own(text);
        if (invalid is not null) return invalid;
        MemoryBriefFile next;
        lock (gate)
        {
            current = MemoryBriefFile.Load(directory);
            if (own == current.Text) return null;
            next = own is null
                ? current with { Text = null, By = null, WrittenAt = clock.GetUtcNow(), Facts = 0, Basis = [] }
                : current with { Text = own, By = MemoryBriefFile.ByOwner, WrittenAt = clock.GetUtcNow() };
        }
        if (Save(next, usableNow: own is not null) is { } failed) return failed;
        Log?.Invoke(own is null ? "Memory brief: you cleared it; Martlet writes it again from your memories." : $"Memory brief: you saved your own ({own.Length} characters).");
        if (own is null) Poke(TimeSpan.FromSeconds(1));
        return null;
    }

    /// <summary>Write it again: writes the brief from the facts now, also over the owner's own text, without the limits.</summary>
    internal Task<string> WriteNowAsync(CancellationToken token = default) => CheckAsync(force: true, token);

    /// <summary>One check: reads the facts, decides whether the brief still holds and, when it is due (or
    /// <paramref name="force"/>), writes it again. Returns what it did, in a few words.</summary>
    internal async Task<string> CheckAsync(bool force, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        token = linked.Token;
        await running.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var brief = MemoryBriefFile.Load(directory);
            lock (gate) current = brief;
            if (!brief.On)
            {
                Note(force ? "Turn the memory brief on first." : null);
                return "off";
            }
            if (!force && busy())
            {
                Poke(BusyRetry);
                return "busy";
            }
            var facts = await read(token).ConfigureAwait(false);
            if (facts is null)
            {
                Note(force ? "Memory is off or can't be read now." : null);
                return "memory off";
            }
            if (!facts.Complete && !force)
            {
                Poke(LoadingRetry);
                return "loading";
            }
            var now = clock.GetUtcNow();
            var all = MemoryBriefWriter.Usable(facts, now);
            var selected = MemoryBriefWriter.Select(facts, now);
            var basis = MemoryBriefWriter.Basis(selected, facts.People);
            var change = MemoryBriefWriter.Compare(brief, basis, all);
            bool was;
            lock (gate)
            {
                factsNow = all.Count;
                was = usable;
                usable = brief.Text is not null && (brief.Owned || change.Lost == 0);
            }
            Changed?.Invoke();
            if (was && change.Lost > 0 && !brief.Owned && brief.Text is not null)
                Log?.Invoke($"Memory brief: {change.Lost} fact{(change.Lost == 1 ? "" : "s")} it was written from changed or went, so it isn't sent until it is written again.");
            if (brief.Owned && !force) return "the owner's";
            if (selected.Count == 0)
            {
                if (brief.Text is not null)
                {
                    if (!force && Wait(true, now) is { } hold && hold > TimeSpan.Zero)
                    {
                        Poke(hold);
                        return "waiting";
                    }
                    lock (gate)
                    {
                        lastTry = now;
                        tries.Enqueue(now);
                    }
                    Save(brief with { Text = null, By = null, WrittenAt = now, Facts = 0, Basis = [] });
                    Log?.Invoke("Memory brief: no memories are left to write it from, so it was cleared.");
                    return "cleared";
                }
                Note(force ? "There are no memories to write it from yet." : null);
                return "no facts";
            }
            var writeNow = force || brief.Text is null && selected.Count >= MemoryBriefWriter.MinimumFacts || change.Lost > 0 ||
                change.New >= MemoryBriefWriter.NewFactsToWriteAgain || change.Renamed;
            if (!writeNow) return brief.Text is null ? "too few facts" : "up to date";
            if (!force && Wait(change.Lost > 0, now) is { } wait && wait > TimeSpan.Zero)
            {
                Poke(wait);
                return "waiting";
            }
            if (PromptSettings.Fill(facts.Prompts, PromptCatalog.MemoryBriefWriting) is not { } instructions || instructions.Trim().Length == 0)
            {
                Note("Companion › Prompts › Writing the memory brief is empty, so Martlet writes no brief.");
                return "no prompt";
            }
            var input = MemoryBriefWriter.Input(facts, selected);
            lock (gate)
            {
                writing = true;
                problem = null;
                lastTry = now;
                tries.Enqueue(now);
            }
            Changed?.Invoke();
            (string? Answer, string? Failure) answer;
            try { answer = await write(instructions, input, token).ConfigureAwait(false); }
            finally
            {
                lock (gate) writing = false;
            }
            var text = MemoryBriefWriter.Clean(answer.Answer);
            if (text is null)
            {
                Note(answer.Failure is null ? "The model's answer wasn't a profile, so the brief stays as it was."
                    : $"Couldn't write it: {answer.Failure}.");
                Log?.Invoke($"Memory brief: not written ({(answer.Failure is null ? "no usable answer" : "failed")}).");
                if (!force) Poke(Gap);
                return "failed";
            }
            var latest = MemoryBriefFile.Load(directory);
            if (!force && latest.Write() != brief.Write())
            {
                // The owner (or another computer) changed it meanwhile: theirs stays, and the next check decides again.
                Poke();
                return "changed meanwhile";
            }
            if (!latest.On) return "off";
            Save(latest with { Text = text, By = MemoryBriefFile.ByThinking, WrittenAt = now, Facts = selected.Count, Basis = basis }, usableNow: true);
            Log?.Invoke($"Memory brief: written from {selected.Count} fact{(selected.Count == 1 ? "" : "s")} ({text.Length} characters).");
            return "written";
        }
        finally
        {
            running.Release();
        }
    }

    /// <summary>How long to wait before writing on its own: after a brief from another computer, after the last try here, and
    /// within <see cref="PerHour"/> tries an hour.</summary>
    private TimeSpan? Wait(bool lost, DateTimeOffset now)
    {
        lock (gate)
        {
            while (tries.Count > 0 && now - tries.Peek() >= TimeSpan.FromHours(1)) tries.Dequeue();
            var waits = new List<TimeSpan>();
            if (followedAt is { } followed) waits.Add(followed + AfterFollowing - now);
            if (lastTry is { } tried) waits.Add(tried + (lost ? GapAfterLoss : Gap) - now);
            if (tries.Count >= PerHour) waits.Add(tries.Peek() + TimeSpan.FromHours(1) - now);
            return waits.Count == 0 ? null : waits.Max();
        }
    }

    private string? Save(MemoryBriefFile next, bool? usableNow = null)
    {
        try { MemoryBriefFile.Save(directory, next); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Note("Couldn't save the memory brief on this PC.");
            return "Couldn't save the memory brief on this PC.";
        }
        lock (gate)
        {
            current = next;
            if (usableNow is { } now) usable = now;
            if (next.Text is null) usable = false;
            problem = null;
        }
        Changed?.Invoke();
        return null;
    }

    private void Note(string? text)
    {
        lock (gate) problem = text;
        Changed?.Invoke();
    }

    private async Task RunAsync()
    {
        while (true)
        {
            DateTimeOffset at;
            Task woken;
            lock (gate)
            {
                if (disposed || due is null)
                {
                    runner = null;
                    return;
                }
                at = due.Value;
                if (wake.Task.IsCompleted) wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
                woken = wake.Task;
            }
            var wait = at - clock.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                try { await Task.WhenAny(Task.Delay(wait, clock, lifetime.Token), woken).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                continue;
            }
            lock (gate) due = null;
            try { await CheckAsync(force: false, lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception error)
            {
                Log?.Invoke($"Memory brief: the check failed ({error.GetType().Name}).");
            }
        }
    }

    /// <summary>The state in words for Companion › Memory › Memory brief (MemoryBriefStatus): never the brief or a fact.</summary>
    internal string Status(bool memoryOn)
    {
        MemoryBriefFile brief;
        bool fine, busyWriting;
        string? trouble;
        lock (gate) (brief, fine, busyWriting, trouble) = (current, usable, writing, problem);
        var now = clock.GetUtcNow();
        var text = !memoryOn ? "Memory is off, so Martlet writes and sends no brief."
            : !brief.On ? "Off: Martlet doesn't write or send a brief."
            : busyWriting ? "Writing the brief from your memories now."
            : brief.Text is null ? $"Not written yet. Martlet writes it once it remembers at least {MemoryBriefWriter.MinimumFacts} things."
            : brief.Owned ? "Your own text. Martlet sends it with the first message of each conversation and doesn't change it on its own."
            : !fine ? "Waiting: a memory it was written from changed or went, so Martlet doesn't send it until it writes it again."
            : $"Martlet sends it with the first message of each conversation. Written {MemoryBriefWriter.Ago(brief.WrittenAt ?? now, now)} " +
              $"from {brief.Facts} memor{(brief.Facts == 1 ? "y" : "ies")}.";
        return trouble is null || !memoryOn || !brief.On ? text : text + " " + trouble;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
        }
        lifetime.Cancel();
        wake.TrySetResult();
    }

    public override string ToString() => nameof(MemoryBriefService);
}
