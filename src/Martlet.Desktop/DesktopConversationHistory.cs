using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Martlet.Conversation;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>This PC's choices for the record of conversations (Companion › Memory › Conversation history), in
/// conversation-history.json in the data folder: whether Martlet keeps it (<see cref="Keep"/>, on by default) and whether the
/// Thinking model may search it on its own with the search_conversations tool (<see cref="Search"/>, off by default: the tool
/// makes every request a little longer).</summary>
internal sealed record ConversationHistoryPreferences(bool Keep = true, bool Search = false)
{
    internal const string FileName = "conversation-history.json";

    internal static ConversationHistoryPreferences Load(string? directory)
    {
        if (directory is null) return new();
        try
        {
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path) || new FileInfo(path).Length > 4_096) return new();
            return JsonSerializer.Deserialize<ConversationHistoryPreferences>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    internal bool Save(string? directory)
    {
        if (directory is null) return false;
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            var temporary = Path.Combine(directory, $"conversation-history.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(this));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}

/// <summary>What deleting or editing in the record did: exchanges that went, changes queued for messaging apps, why some of it
/// stays in an app (one line each) and whether an edited reply had to be cut there.</summary>
internal sealed record HistoryChange(int Removed, int Queued, IReadOnlyList<string> KeptThere, bool Truncated);

/// <summary>Martlet's record of every conversation on this PC (<see cref="ConversationHistory"/> in the data folder's
/// <c>conversations</c> folder) while memory is on and <see cref="ConversationHistoryPreferences.Keep"/> is chosen: each finished
/// exchange is appended in the background, after its reply, so the next reply never waits for the disk. A message that refers to
/// an earlier conversation gets the best matching exchanges in its notes (<see cref="RecallNotes"/>), read from memory only, so a
/// reply never waits for the record either; while it is still being read, the message goes without. The Thinking model may also
/// search it (<see cref="SearchAsync"/>) when the owner allows. Screen glances and what the PC plays are never recorded.</summary>
internal sealed class DesktopConversationHistory
{
    /// <summary>How long a search_conversations call waits for the record to be read the first time.</summary>
    internal static TimeSpan LoadWait => TimeSpan.FromSeconds(5);

    private readonly object gate = new();
    private readonly string? dataDirectory;
    private readonly TimeProvider clock;
    private readonly TimeZoneInfo zone;
    private ConversationHistoryPreferences preferences;
    private Task tail = Task.CompletedTask;
    private int pending;
    private bool failing;

    internal DesktopConversationHistory(string? dataDirectory, TimeProvider? clock = null, TimeZoneInfo? zone = null, string? directory = null)
    {
        this.dataDirectory = dataDirectory;
        this.clock = clock ?? TimeProvider.System;
        this.zone = zone ?? TimeZoneInfo.Local;
        preferences = ConversationHistoryPreferences.Load(dataDirectory);
        Store = new(directory ?? Path.Combine(dataDirectory ?? Path.GetTempPath(), ConversationHistory.DirectoryName), this.clock);
        Platforms = new(dataDirectory is null && directory is null ? null : Path.Combine(Store.Directory, PlatformChanges.FileName), this.clock);
    }

    internal ConversationHistory Store { get; }
    /// <summary>Deletions and edits waiting for Telegram and Discord (made in the background once <see cref="PlatformChanges.Start"/>
    /// runs, while the app is connected).</summary>
    internal PlatformChanges Platforms { get; }
    internal TimeZoneInfo Zone => zone;
    internal TimeProvider Clock => clock;

    /// <summary>Raised off the dispatcher after an exchange was recorded or conversations were deleted.</summary>
    internal event Action? Changed;

    internal ConversationHistoryPreferences Preferences { get { lock (gate) return preferences; } }

    /// <summary>Saves this PC's choices; false when the file couldn't be written (the choice still applies until Martlet restarts).</summary>
    internal bool SetPreferences(ConversationHistoryPreferences next)
    {
        lock (gate) preferences = next;
        if (next.Keep) Warm();
        return next.Save(dataDirectory);
    }

    /// <summary>Whether conversations are recorded and recalled: memory is on and the owner keeps the record.</summary>
    internal bool Active(MemorySettings? memory) => memory is { Enabled: true } && Preferences.Keep;

    /// <summary>Whether replies are offered search_conversations (on a route that does function calling).</summary>
    internal bool Searchable(MemorySettings? memory) => Active(memory) && Preferences.Search;

    /// <summary>Exchanges waiting to be written.</summary>
    internal int Pending => Volatile.Read(ref pending);

    /// <summary>Completes once every exchange recorded so far is written (or failed).</summary>
    internal Task Idle { get { lock (gate) return tail; } }

    /// <summary>Starts reading the record in the background (once), so recall can answer from memory.</summary>
    internal void Warm()
    {
        if (Store.Loaded) return;
        _ = Store.LoadAsync().ContinueWith(task =>
        {
            if (task.Exception is { } error) ErrorLog.Warn("Conversation history: couldn't read the record.", error.GetBaseException());
            else if (Store.Stats is { Skipped: > 0 } stats)
                ErrorLog.Warn($"Conversation history: {stats.Skipped} line{(stats.Skipped == 1 ? "" : "s")} of the record couldn't be read and were skipped.");
        }, TaskScheduler.Default);
    }

    /// <summary>Records one finished exchange in the background, after the ones before it. Never waits for the disk.
    /// <paramref name="source"/> is where it happened when it wasn't this PC's talk window.</summary>
    internal void Record(Guid conversation, HistoryInputKind kind, string user, string reply, string? speaker, HistorySource? source = null)
    {
        Interlocked.Increment(ref pending);
        lock (gate) tail = AppendAsync(tail, conversation, kind, user, reply, speaker, source);
    }

    /// <summary>The app's IDs of a reply that went back to a messaging chat: they join the exchange recorded for the message
    /// they answer (found by the message's ID), after the exchanges recorded before, so deleting or editing it here can do the
    /// same there.</summary>
    internal void AttachReplies(string app, string chat, string userMessage, IReadOnlyList<string> replies)
    {
        if (replies.Count == 0) return;
        Interlocked.Increment(ref pending);
        lock (gate) tail = AttachAsync(tail, app, chat, userMessage, replies.ToArray());
    }

    private async Task AttachAsync(Task previous, string app, string chat, string userMessage, string[] replies)
    {
        await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ForceYielding);
        try
        {
            await Store.LoadAsync().ConfigureAwait(false);
            if (Store.FindMessage(app, chat, userMessage) is { } exchange)
                await Store.ChangeAsync(exchange.Id, found => found with { Source = found.Source! with { ReplyMessages = replies } }).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Warn($"Conversation history: couldn't note where a reply went ({error.GetType().Name}).");
        }
        finally
        {
            Interlocked.Decrement(ref pending);
        }
    }

    private async Task AppendAsync(Task previous, Guid conversation, HistoryInputKind kind, string user, string reply, string? speaker,
        HistorySource? source)
    {
        await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ForceYielding);
        try
        {
            await Store.AppendAsync(conversation, kind, user, reply, speaker, source).ConfigureAwait(false);
            if (failing) ErrorLog.Info("Conversation history: recording works again.");
            failing = false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Said once until it works again; never the exchange itself.
            if (!failing) ErrorLog.Warn($"Conversation history: couldn't record an exchange ({error.GetType().Name}).");
            failing = true;
        }
        finally
        {
            Interlocked.Decrement(ref pending);
        }
        Changed?.Invoke();
    }

    /// <summary>The notes for a message that refers to an earlier conversation: the best matching exchanges of other
    /// conversations not already in the notes of <paramref name="sent"/> (the earlier messages this request carries), or null,
    /// with the replies under <paramref name="companion"/>'s name (the persona's). Reads only what is in memory: while the record
    /// is still being read, the message goes without (and reading starts).</summary>
    internal string? RecallNotes(string words, Guid conversation, IReadOnlyList<TextHistoryMessage> sent, PromptSettings? prompts, out int recalled,
        string? companion = null)
    {
        recalled = 0;
        if (!PastConversations.RefersToPast(words)) return null;
        if (!Store.Loaded)
        {
            Warm();
            ErrorLog.Info("Past conversations: your message referred to an earlier conversation, but the record is still being read, so it went without.");
            return null;
        }
        var started = Stopwatch.GetTimestamp();
        var now = clock.GetUtcNow();
        var earlier = string.Join("\n", sent.Where(message => message.Role == TextHistoryRole.User &&
            message.Text.Contains(PastConversations.Label, StringComparison.Ordinal)).Select(message => message.Text));
        var found = PastConversations.Recall(Store, words, now, zone, conversation, PastConversations.MaximumRecalled,
            exchange => earlier.Length > 0 && earlier.Contains(
                PastConversations.Line(exchange, zone, PastConversations.RecallUserCharacters, PastConversations.RecallReplyCharacters, companion),
                StringComparison.Ordinal)).ToArray();
        var took = Stopwatch.GetElapsedTime(started);
        ErrorLog.Info(found.Length == 0
            ? $"Past conversations: your message referred to an earlier conversation; nothing new matched ({took.TotalMilliseconds:0.#} ms)."
            : $"Past conversations: {found.Length} earlier exchange{(found.Length == 1 ? "" : "s")} went with your message ({took.TotalMilliseconds:0.#} ms).");
        if (found.Length == 0) return null;
        recalled = found.Length;
        return PastConversations.Notes(found, now, zone, prompts, companion);
    }

    /// <summary>search_conversations: what the model asked for in the record (other conversations than
    /// <paramref name="conversation"/>), with the replies under <paramref name="companion"/>'s name, or what was wrong with the
    /// call.</summary>
    internal async ValueTask<(ConversationToolResult Result, string Outcome)> SearchAsync(TextToolCall call, Guid conversation,
        CancellationToken token, string? companion = null)
    {
        var now = clock.GetUtcNow();
        var (request, problem) = PastConversations.Parse(call.ArgumentsJson, now, zone);
        if (request is null) return (new(problem!, true), "invalid arguments");
        try
        {
            await Store.LoadAsync(token).WaitAsync(LoadWait, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return (new(PastConversations.Unavailable, true), "the record couldn't be read");
        }
        var found = PastConversations.Find(Store, request, conversation);
        ErrorLog.Info($"Past conversations: the Thinking model searched the record and found {found.Count} exchange{(found.Count == 1 ? "" : "s")}.");
        return (new(PastConversations.Result(found, request, now, zone, companion)), $"found {found.Count}");
    }

    /// <summary>Deletes one recorded conversation; with <paramref name="there"/>, its messages in Telegram and Discord too (as
    /// far as each app allows), queued.</summary>
    internal async Task<HistoryChange> DeleteAsync(Guid conversation, bool there = false, CancellationToken token = default)
    {
        await Idle.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await Store.LoadAsync(token).ConfigureAwait(false);
        var plans = there ? Store.Exchanges(conversation).Select(exchange => HistoryPlatforms.Delete(exchange, null, clock.GetUtcNow())).ToArray() : [];
        var removed = await Store.DeleteAsync(conversation, token).ConfigureAwait(false);
        var change = Queue(removed, plans);
        ErrorLog.Info($"Conversation history: you deleted a conversation ({removed} exchange{(removed == 1 ? "" : "s")}" +
            $"{(change.Queued > 0 ? $"; {change.Queued} message{(change.Queued == 1 ? "" : "s")} to delete in its app" : "")}).");
        Changed?.Invoke();
        return change;
    }

    /// <summary>Deletes the whole record; with <paramref name="there"/>, every recorded message in Telegram and Discord too (as
    /// far as each app allows), queued.</summary>
    internal async Task<HistoryChange> DeleteAllAsync(bool there = false, CancellationToken token = default)
    {
        await Idle.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await Store.LoadAsync(token).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        var plans = there ? Store.Where(exchange => exchange.Source is not null).Select(exchange => HistoryPlatforms.Delete(exchange, null, now)).ToArray() : [];
        var exchanges = Store.Stats.Exchanges;
        await Store.DeleteAllAsync(token).ConfigureAwait(false);
        var change = Queue(exchanges, plans);
        ErrorLog.Info("Conversation history: you deleted the whole record" +
            (change.Queued > 0 ? $" ({change.Queued} message{(change.Queued == 1 ? "" : "s")} to delete in their apps)." : "."));
        Changed?.Invoke();
        return change;
    }

    /// <summary>Deletes one message of an exchange (<paramref name="side"/>; both for null): the exchange goes once nothing of it
    /// is left. With <paramref name="there"/>, the message goes in its app too (as far as the app allows), queued.</summary>
    internal async Task<HistoryChange> DeleteMessageAsync(Guid exchange, HistorySide? side, bool there = false, CancellationToken token = default)
    {
        await Idle.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        PlatformPlan? plan = null;
        var now = clock.GetUtcNow();
        var (before, after) = await Store.ChangeAsync(exchange, found =>
        {
            plan = HistoryPlatforms.Delete(found, side, now);
            var user = side is null or HistorySide.User ? "" : found.User;
            var reply = side is null or HistorySide.Reply ? "" : found.Reply;
            return user.Length == 0 && reply.Length == 0 ? null : found with { User = user, Reply = reply, Source = plan.Source, Edited = now };
        }, token).ConfigureAwait(false);
        if (before is null) return new(0, 0, [], false);
        var change = Queue(after is null ? 1 : 0, there && plan is not null ? [plan] : []);
        ErrorLog.Info($"Conversation history: you deleted {(side is null ? "an exchange" : side == HistorySide.User ? "a message" : "a reply")}" +
            $"{(change.Queued > 0 ? $" ({change.Queued} message{(change.Queued == 1 ? "" : "s")} to delete in {HistoryApps.Name(before.App)})" : "")}.");
        Changed?.Invoke();
        return change;
    }

    /// <summary>Edits one message of an exchange to <paramref name="text"/> (empty deletes it). With <paramref name="there"/>, an
    /// edited reply is edited in its app too (as far as the app allows), queued; the person's own messages are never edited
    /// there (no app lets a bot).</summary>
    internal async Task<HistoryChange> EditAsync(Guid exchange, HistorySide side, string text, bool there = false, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return await DeleteMessageAsync(exchange, side, there, token).ConfigureAwait(false);
        await Idle.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        PlatformPlan? plan = null;
        var now = clock.GetUtcNow();
        text = text.Trim();
        var (before, _) = await Store.ChangeAsync(exchange, found =>
        {
            plan = side == HistorySide.Reply ? HistoryPlatforms.Edit(found, text, now)
                : found.Source is { UserMessages.Count: > 0 } source
                    ? new([], [HistoryPlatforms.WhyNotEdit(source, HistorySide.User) + ", so your message there stays as it was."], source)
                    : PlatformPlan.None(found.Source);
            return side == HistorySide.Reply
                ? found with { Reply = text, Edited = now, Source = there ? plan.Source : found.Source }
                : found with { User = text, Edited = now };
        }, token).ConfigureAwait(false);
        if (before is null) return new(0, 0, [], false);
        var change = Queue(0, there && plan is not null ? [plan] : []);
        ErrorLog.Info($"Conversation history: you edited {(side == HistorySide.User ? "a message" : "a reply")}" +
            $"{(change.Queued > 0 ? $" ({change.Queued} change{(change.Queued == 1 ? "" : "s")} to make in {HistoryApps.Name(before.App)})" : "")}.");
        Changed?.Invoke();
        return change;
    }

    private HistoryChange Queue(int removed, IReadOnlyList<PlatformPlan> plans)
    {
        var changes = plans.SelectMany(plan => plan.Changes).ToArray();
        Platforms.Enqueue(changes);
        return new(removed, changes.Length, plans.SelectMany(plan => plan.KeptThere).Distinct().ToArray(), plans.Any(plan => plan.Truncated));
    }

    /// <summary>One line on what the record holds and whether Martlet keeps it (never content).</summary>
    internal string Describe(MemorySettings? memory)
    {
        if (!Store.Loaded) Warm();
        var stats = Store.Loaded ? Store.Stats : null;
        var held = stats is null ? "" : stats.Exchanges == 0 ? " Nothing is recorded yet."
            : $" It holds {stats.Conversations} conversation{(stats.Conversations == 1 ? "" : "s")} ({stats.Exchanges} exchange" +
              $"{(stats.Exchanges == 1 ? "" : "s")}) since {PastConversations.When(stats.Oldest!.Value, zone)[..^6]}.";
        if (memory is not { Enabled: true })
            return "Off while memory is off: nothing is recorded or recalled." + held;
        if (!Preferences.Keep)
            return "Martlet doesn't keep a record of new conversations." + held;
        return "Martlet keeps a record of your conversations on this PC and brings up what you talked about when you mention an " +
            "earlier conversation." + (Preferences.Search ? " It may also search the record on its own." : "") + held;
    }
}
