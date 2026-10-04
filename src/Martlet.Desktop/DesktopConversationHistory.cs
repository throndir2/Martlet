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
    }

    internal ConversationHistory Store { get; }
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

    /// <summary>Records one finished exchange in the background, after the ones before it. Never waits for the disk.</summary>
    internal void Record(Guid conversation, HistoryInputKind kind, string user, string reply, string? speaker)
    {
        Interlocked.Increment(ref pending);
        lock (gate) tail = AppendAsync(tail, conversation, kind, user, reply, speaker);
    }

    private async Task AppendAsync(Task previous, Guid conversation, HistoryInputKind kind, string user, string reply, string? speaker)
    {
        await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ForceYielding);
        try
        {
            await Store.AppendAsync(conversation, kind, user, reply, speaker).ConfigureAwait(false);
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
    /// conversations not already in the notes of <paramref name="sent"/> (the earlier messages this request carries), or null.
    /// Reads only what is in memory: while the record is still being read, the message goes without (and reading starts).</summary>
    internal string? RecallNotes(string words, Guid conversation, IReadOnlyList<TextHistoryMessage> sent, PromptSettings? prompts, out int recalled)
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
                PastConversations.Line(exchange, zone, PastConversations.RecallUserCharacters, PastConversations.RecallReplyCharacters),
                StringComparison.Ordinal)).ToArray();
        var took = Stopwatch.GetElapsedTime(started);
        ErrorLog.Info(found.Length == 0
            ? $"Past conversations: your message referred to an earlier conversation; nothing new matched ({took.TotalMilliseconds:0.#} ms)."
            : $"Past conversations: {found.Length} earlier exchange{(found.Length == 1 ? "" : "s")} went with your message ({took.TotalMilliseconds:0.#} ms).");
        if (found.Length == 0) return null;
        recalled = found.Length;
        return PastConversations.Notes(found, now, zone, prompts);
    }

    /// <summary>search_conversations: what the model asked for in the record (other conversations than
    /// <paramref name="conversation"/>), or what was wrong with the call.</summary>
    internal async ValueTask<(ConversationToolResult Result, string Outcome)> SearchAsync(TextToolCall call, Guid conversation,
        CancellationToken token)
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
        return (new(PastConversations.Result(found, request, now, zone)), $"found {found.Count}");
    }

    /// <summary>Deletes one recorded conversation.</summary>
    internal async Task<int> DeleteAsync(Guid conversation, CancellationToken token = default)
    {
        await Idle.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        var removed = await Store.DeleteAsync(conversation, token).ConfigureAwait(false);
        ErrorLog.Info($"Conversation history: you deleted a conversation ({removed} exchange{(removed == 1 ? "" : "s")}).");
        Changed?.Invoke();
        return removed;
    }

    /// <summary>Deletes the whole record.</summary>
    internal async Task<int> DeleteAllAsync(CancellationToken token = default)
    {
        await Idle.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        var files = await Store.DeleteAllAsync(token).ConfigureAwait(false);
        ErrorLog.Info("Conversation history: you deleted the whole record.");
        Changed?.Invoke();
        return files;
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
