using System.Text.Json;

namespace Martlet.Conversation;

public enum PlatformChangeKind
{
    Delete,
    Edit
}

/// <summary>One change to make in a messaging app after the owner changed its record here: delete one message there, or edit
/// one of Martlet's (<paramref name="Text"/>, the new text). <paramref name="Own"/>: the message is Martlet's, not the person's.
/// <paramref name="Attempts"/> counts tries the app couldn't be reached for.</summary>
public sealed record PlatformChange(Guid Id, string App, string Chat, string? Server, string Message, PlatformChangeKind Kind, bool Own,
    string? Text, DateTimeOffset Queued, int Attempts = 0)
{
    public override string ToString() => $"{nameof(PlatformChange)} {Kind} on {App}";
}

public enum PlatformFailure
{
    /// <summary>The app asked Martlet to slow down; try again after <see cref="PlatformChangeException.RetryAfter"/>.</summary>
    RateLimited,
    /// <summary>The app couldn't be reached (or isn't connected right now); try again later.</summary>
    Unavailable,
    /// <summary>The app refused this change for good (too old, not allowed, already gone).</summary>
    Refused
}

/// <summary>A messaging app didn't make a change. The message never carries a token or what was said.</summary>
public sealed class PlatformChangeException(PlatformFailure failure, string message, TimeSpan? retryAfter = null) : Exception(message)
{
    public PlatformFailure Failure { get; } = failure;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>One messaging app's side of deleting and editing messages, while it is connected.</summary>
public interface IPlatformMessages
{
    /// <summary>The app (<see cref="HistoryApps"/>).</summary>
    string App { get; }
    /// <summary>The least time between two changes, under the app's rate limits.</summary>
    TimeSpan Interval { get; }
    /// <summary>Makes one change; throws <see cref="PlatformChangeException"/> when the app didn't.</summary>
    Task ApplyAsync(PlatformChange change, CancellationToken token);
}

/// <summary>What changing an exchange here means in its app: the changes to queue there, why some of it stays there
/// (<see cref="KeptThere"/>, one line each), the exchange's source afterwards and whether an edited reply had to be cut to fit
/// the messages it already had.</summary>
public sealed record PlatformPlan(IReadOnlyList<PlatformChange> Changes, IReadOnlyList<string> KeptThere, HistorySource? Source,
    bool Truncated = false)
{
    public static PlatformPlan None(HistorySource? source) => new([], [], source);
}

/// <summary>What each messaging app lets Martlet's bot do with messages it recorded, and the changes deleting or editing an
/// exchange here asks of the app. Telegram: a bot deletes messages in a private chat (yours and its own) for 48 hours, and edits
/// its own. Discord: a bot deletes and edits its own messages, and deletes yours only in a server (with Manage Messages), never in
/// a DM. WhatsApp's Cloud API deletes and edits nothing. This PC's talk window has no app.</summary>
public static class HistoryPlatforms
{
    public static readonly TimeSpan TelegramWindow = TimeSpan.FromHours(48);

    /// <summary>The longest single message the app takes (a longer reply was sent in pieces).</summary>
    public static int MessageLimit(string app) => app == HistoryApps.Discord ? 2000 : 4096;

    /// <summary>Why the app won't delete this message (null: it may).</summary>
    public static string? WhyNotDelete(HistorySource source, bool own, DateTimeOffset sent, DateTimeOffset now) => source.App switch
    {
        HistoryApps.Telegram when now - sent > TelegramWindow => "Telegram lets bots delete messages for 48 hours only",
        HistoryApps.Telegram => null,
        HistoryApps.Discord when !own && source.Server is null => "Discord doesn't let bots delete your messages in a DM",
        HistoryApps.Discord => null,
        _ => $"{HistoryApps.Name(source.App)} doesn't let Martlet delete messages"
    };

    /// <summary>Why the app won't edit this side (null: it may).</summary>
    public static string? WhyNotEdit(HistorySource source, HistorySide side) => source.App switch
    {
        _ when side == HistorySide.User => $"{HistoryApps.Name(source.App)} doesn't let Martlet edit your messages",
        HistoryApps.Telegram or HistoryApps.Discord => null,
        _ => $"{HistoryApps.Name(source.App)} doesn't let Martlet edit messages"
    };

    /// <summary>Deleting <paramref name="side"/> of an exchange (both for null): its messages to delete in the app.</summary>
    public static PlatformPlan Delete(HistoryExchange exchange, HistorySide? side, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        if (exchange.Source is not { Chat: { Length: > 0 } chat } source) return PlatformPlan.None(exchange.Source);
        var changes = new List<PlatformChange>();
        var kept = new List<string>();
        var after = source;
        void Side(IReadOnlyList<string>? ids, bool own)
        {
            if (ids is not { Count: > 0 }) return;
            if (WhyNotDelete(source, own, exchange.At, now) is { } why)
            {
                kept.Add($"{why}, so {(own ? "Martlet's reply" : "your message")} stays there.");
                return;
            }
            changes.AddRange(ids.Select(id => new PlatformChange(Guid.NewGuid(), source.App, chat, source.Server, id, PlatformChangeKind.Delete,
                own, null, now)));
        }
        if (side is null or HistorySide.User)
        {
            Side(source.UserMessages, own: false);
            after = after with { UserMessages = null };
        }
        if (side is null or HistorySide.Reply)
        {
            Side(source.ReplyMessages, own: true);
            after = after with { ReplyMessages = null };
        }
        return new(changes, kept, after);
    }

    /// <summary>Editing Martlet's reply to <paramref name="reply"/>: its messages in the app get the new text (split the way it was
    /// sent), extra messages are deleted, and a reply that now needs more messages than it had is cut to fit them.</summary>
    public static PlatformPlan Edit(HistoryExchange exchange, string reply, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        ArgumentNullException.ThrowIfNull(reply);
        if (exchange.Source is not { Chat: { Length: > 0 } chat, ReplyMessages.Count: > 0 } source) return PlatformPlan.None(exchange.Source);
        if (string.IsNullOrWhiteSpace(reply)) return Delete(exchange, HistorySide.Reply, now);
        if (WhyNotEdit(source, HistorySide.Reply) is { } why) return new([], [$"{why}, so the reply there stays as it was."], source);
        var ids = source.ReplyMessages!;
        var limit = MessageLimit(source.App);
        var pieces = Split(reply, limit).ToList();
        var truncated = false;
        if (pieces.Count > ids.Count)
        {
            var rest = string.Join("\n", pieces.Skip(ids.Count - 1));
            pieces.RemoveRange(ids.Count - 1, pieces.Count - ids.Count + 1);
            pieces.Add(rest.Length <= limit ? rest : rest[..(limit - 1)].TrimEnd() + "…");
            truncated = rest.Length > limit;
        }
        var changes = new List<PlatformChange>();
        for (var at = 0; at < ids.Count; at++)
            changes.Add(at < pieces.Count
                ? new(Guid.NewGuid(), source.App, chat, source.Server, ids[at], PlatformChangeKind.Edit, true, pieces[at], now)
                : new(Guid.NewGuid(), source.App, chat, source.Server, ids[at], PlatformChangeKind.Delete, true, null, now));
        var kept = truncated ? new[] { $"The new reply is longer than the {ids.Count} message{(ids.Count == 1 ? "" : "s")} it had there, so its end is cut there." } : [];
        return new(changes, kept, source with { ReplyMessages = ids.Take(pieces.Count).ToArray() }, truncated);
    }

    /// <summary>Splits <paramref name="text"/> into pieces of at most <paramref name="limit"/> characters, at a paragraph, line or
    /// word break in the second half of a piece when there is one.</summary>
    public static IReadOnlyList<string> Split(string text, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 2);
        var parts = new List<string>();
        var rest = text.Trim();
        while (rest.Length > limit)
        {
            var cut = -1;
            foreach (var separator in new[] { "\n\n", "\n", " " })
            {
                cut = rest.LastIndexOf(separator, limit - 1, StringComparison.Ordinal);
                if (cut >= limit / 2) break;
                cut = -1;
            }
            if (cut < 0) cut = char.IsHighSurrogate(rest[limit - 1]) ? limit - 1 : limit;
            parts.Add(rest[..cut].TrimEnd());
            rest = rest[cut..].TrimStart();
        }
        if (rest.Length > 0) parts.Add(rest);
        return parts;
    }
}

/// <summary>How the queue of changes for messaging apps is doing (counts, apps and one problem line; never what was said).</summary>
public sealed record PlatformChangesStatus(int Pending, int Done, int Refused, int GaveUp, string? LastProblem,
    IReadOnlyDictionary<string, int> PendingByApp, IReadOnlyList<string> Connected, DateTimeOffset? NextAttempt)
{
    /// <summary>One line for the history window and MCP.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (Pending == 0) parts.Add("No changes are waiting for Telegram or Discord.");
        else
            parts.Add(string.Join("; ", PendingByApp.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
                $"{pair.Value} change{(pair.Value == 1 ? "" : "s")} waiting for {HistoryApps.Name(pair.Key)}" +
                (Connected.Contains(pair.Key) ? "" : " (not connected; they go once it is)"))) + ".");
        if (Done + Refused + GaveUp > 0)
            parts.Add($"Since then: {Done} made" + (Refused > 0 ? $", {Refused} refused" : "") + (GaveUp > 0 ? $", {GaveUp} given up" : "") + ".");
        if (LastProblem is { } problem) parts.Add("Last problem: " + problem);
        return string.Join(" ", parts);
    }
}

/// <summary>The changes waiting for messaging apps, made one at a time per app at the app's pace
/// (<see cref="IPlatformMessages.Interval"/>): an app that asks Martlet to slow down is waited for as long as it asks, an app
/// that can't be reached is tried again with a growing pause (and given up on after <see cref="MaximumAttempts"/> tries), and a
/// change the app refuses is dropped and said. Changes for an app that isn't connected wait until it is. The queue is kept in
/// <see cref="FileName"/> (beside the record) so it survives a restart.</summary>
public sealed class PlatformChanges : IDisposable
{
    public const string FileName = "platform-changes.json";
    public const int MaximumAttempts = 8;
    public static readonly TimeSpan ChangeLimit = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string? path;
    private readonly TimeProvider clock;
    private readonly object gate = new();
    private readonly List<PlatformChange> pending = [];
    private readonly Dictionary<string, IPlatformMessages> apps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> nextAt = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim wake = new(0);
    private CancellationTokenSource? running;
    private int done, refused, gaveUp;
    private string? lastProblem;

    private sealed record Saved(List<PlatformChange>? Pending, int Done, int Refused, int GaveUp, string? LastProblem);

    public PlatformChanges(string? path, TimeProvider? clock = null)
    {
        this.path = path;
        this.clock = clock ?? TimeProvider.System;
        Load();
    }

    /// <summary>Raised (on any thread) when the queue or its counts changed.</summary>
    public event Action? Changed;

    public PlatformChangesStatus Status
    {
        get
        {
            lock (gate)
            {
                var byApp = pending.GroupBy(change => change.App, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
                DateTimeOffset? next = byApp.Keys.Where(apps.ContainsKey).Select(app => nextAt.GetValueOrDefault(app))
                    .Cast<DateTimeOffset?>().Min();
                return new(pending.Count, done, refused, gaveUp, lastProblem, byApp, apps.Keys.Order(StringComparer.Ordinal).ToArray(), next);
            }
        }
    }

    /// <summary>The changes waiting, oldest first.</summary>
    public IReadOnlyList<PlatformChange> Pending { get { lock (gate) return pending.ToArray(); } }

    public void Enqueue(IEnumerable<PlatformChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        lock (gate)
        {
            var before = pending.Count;
            pending.AddRange(changes);
            if (pending.Count == before) return;
            Save();
        }
        Signal();
    }

    /// <summary>Drops every change still waiting (they stay as they are in the apps).</summary>
    public int Clear()
    {
        int cleared;
        lock (gate)
        {
            cleared = pending.Count;
            pending.Clear();
            Save();
        }
        Signal();
        return cleared;
    }

    /// <summary>The app is connected: its waiting changes go now.</summary>
    public void Connect(IPlatformMessages messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        lock (gate) apps[messages.App] = messages;
        Signal();
    }

    /// <summary>The app went away (only when <paramref name="messages"/> is still the one connected): its changes wait.</summary>
    public void Disconnect(IPlatformMessages messages)
    {
        lock (gate)
            if (apps.TryGetValue(messages.App, out var current) && ReferenceEquals(current, messages)) apps.Remove(messages.App);
        Changed?.Invoke();
    }

    /// <summary>Makes the changes in the background until disposed.</summary>
    public void Start()
    {
        CancellationTokenSource stop;
        lock (gate)
        {
            if (running is not null) return;
            stop = running = new();
        }
        _ = Task.Run(() => LoopAsync(stop.Token));
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TimeSpan? wait;
            try { wait = await RunDueAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (wait is { } due && due <= TimeSpan.Zero) continue;
            try { await wake.WaitAsync(wait is { } until ? until.Add(TimeSpan.FromMilliseconds(10)) : Timeout.InfiniteTimeSpan, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Makes the next due change of each connected app (one each, at the app's pace) and returns how long until the next
    /// is due (null: nothing waits for a connected app).</summary>
    public async Task<TimeSpan?> RunDueAsync(CancellationToken token = default)
    {
        List<(PlatformChange Change, IPlatformMessages Messages)> due = [];
        lock (gate)
        {
            var now = clock.GetUtcNow();
            foreach (var (app, messages) in apps)
                if (nextAt.GetValueOrDefault(app) <= now && pending.FirstOrDefault(change => change.App == app) is { } change)
                    due.Add((change, messages));
        }
        foreach (var (change, messages) in due) await ApplyAsync(change, messages, token).ConfigureAwait(false);
        lock (gate)
        {
            var now = clock.GetUtcNow();
            var waiting = apps.Keys.Where(app => pending.Any(change => change.App == app)).ToArray();
            if (waiting.Length == 0) return null;
            var next = waiting.Min(app => nextAt.GetValueOrDefault(app));
            return next <= now ? TimeSpan.Zero : next - now;
        }
    }

    private async Task ApplyAsync(PlatformChange change, IPlatformMessages messages, CancellationToken token)
    {
        PlatformChangeException? failure = null;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(ChangeLimit);
            await messages.ApplyAsync(change, limit.Token).ConfigureAwait(false);
        }
        catch (PlatformChangeException error) { failure = error; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            failure = new(PlatformFailure.Unavailable, $"{HistoryApps.Name(change.App)} didn't answer in time.");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            failure = new(PlatformFailure.Unavailable, $"{HistoryApps.Name(change.App)} couldn't be reached ({error.GetType().Name}).");
        }
        lock (gate)
        {
            var now = clock.GetUtcNow();
            var at = pending.FindIndex(waiting => waiting.Id == change.Id);
            switch (failure?.Failure)
            {
                case null:
                    if (at >= 0) pending.RemoveAt(at);
                    done++;
                    nextAt[change.App] = now + messages.Interval;
                    break;
                case PlatformFailure.RateLimited:
                    nextAt[change.App] = now + (failure.RetryAfter is { } after && after > TimeSpan.Zero ? after : TimeSpan.FromSeconds(5));
                    lastProblem = $"{HistoryApps.Name(change.App)} asked Martlet to slow down; it waits and goes on.";
                    break;
                case PlatformFailure.Refused:
                    if (at >= 0) pending.RemoveAt(at);
                    refused++;
                    nextAt[change.App] = now + messages.Interval;
                    lastProblem = $"{HistoryApps.Name(change.App)} refused to {(change.Kind == PlatformChangeKind.Edit ? "edit" : "delete")} a message: {failure.Message}";
                    break;
                default:
                    var attempts = change.Attempts + 1;
                    if (attempts >= MaximumAttempts)
                    {
                        if (at >= 0) pending.RemoveAt(at);
                        gaveUp++;
                        lastProblem = $"Gave up on a change for {HistoryApps.Name(change.App)} after {attempts} tries: {failure.Message}";
                    }
                    else
                    {
                        if (at >= 0) pending[at] = change with { Attempts = attempts };
                        lastProblem = failure.Message + " Martlet tries again.";
                    }
                    nextAt[change.App] = now + TimeSpan.FromSeconds(Math.Min(1800, 5 * Math.Pow(2, attempts - 1)));
                    break;
            }
            Save();
        }
        Changed?.Invoke();
    }

    private void Signal()
    {
        if (wake.CurrentCount == 0) wake.Release();
        Changed?.Invoke();
    }

    private void Load()
    {
        if (path is null || !File.Exists(path)) return;
        try
        {
            var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(path), Json);
            if (saved is null) return;
            pending.AddRange((saved.Pending ?? []).Where(change => change is { App.Length: > 0, Chat.Length: > 0, Message.Length: > 0 } &&
                change.Id != Guid.Empty && Enum.IsDefined(change.Kind)));
            (done, refused, gaveUp, lastProblem) = (saved.Done, saved.Refused, saved.GaveUp, saved.LastProblem);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { }
    }

    // Called under the gate: the queue as it is, through a temporary file and an atomic replace.
    private void Save()
    {
        if (path is null) return;
        try
        {
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(new Saved(pending, done, refused, gaveUp, lastProblem), Json));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        CancellationTokenSource? stop;
        lock (gate)
        {
            stop = running;
            running = null;
        }
        stop?.Cancel();
    }
}
