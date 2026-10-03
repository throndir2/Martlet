using System.Text;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>The exchanges of the open conversation, kept in memory only for the next replies. Each reply sends as many of the
/// newest as fit its context size (Companion › Replies), so the buffer only bounds memory: at most
/// <see cref="MaximumTurns"/> exchanges and <see cref="MaximumUtf8Bytes"/>, the oldest going first. Once the conversation
/// outgrows the context, the exchanges a reply left out are let go (<see cref="LetGoBefore"/>), so the next replies start at
/// the same exchange. It is cleared by Refresh context, pause, lock, a settings change and closing the talk window; nothing
/// is written to disk.</summary>
internal sealed class ConversationContextBuffer
{
    internal const int MaximumTurns = BoundedTextInput.HardMaxHistoryMessages / 2;
    internal const int MaximumUtf8Bytes = BoundedTextInput.HardMaxInputUtf8Bytes;

    private readonly Queue<Entry> entries = [];
    private long utf8Bytes;
    private long removed;
    // Sent: the user's message exactly as a Thinking model on this PC got it (the words and Martlet's notes), or null.
    private sealed record Entry(string User, string Assistant, int Utf8Bytes, string? Sent);

    internal int Count => entries.Count;

    /// <summary>The UTF-8 bytes of the kept exchanges.</summary>
    internal long Utf8Bytes => utf8Bytes;

    /// <summary>The position of the oldest kept exchange in the whole conversation (exchanges let go or cleared before it),
    /// so a snapshot's indices can be matched with the buffer later.</summary>
    internal long Start => removed;

    internal void Add(string user, string assistant)
    {
        var bytes = checked(Encoding.UTF8.GetByteCount(user) + Encoding.UTF8.GetByteCount(assistant));
        entries.Enqueue(new(user, assistant, bytes));
        utf8Bytes += bytes;
        while (entries.Count > MaximumTurns || utf8Bytes > MaximumUtf8Bytes)
            RemoveOldest();
    }

    internal IReadOnlyList<TextHistoryMessage> Snapshot() => entries.SelectMany(entry => new[]
    {
        new TextHistoryMessage(TextHistoryRole.User, entry.User),
        new TextHistoryMessage(TextHistoryRole.Assistant, entry.Assistant)
    }).ToArray();

    internal void Clear()
    {
        removed += entries.Count;
        entries.Clear();
        utf8Bytes = 0;
    }

    /// <summary>Lets go of the exchanges before <paramref name="start"/> (a position as <see cref="Start"/> counts them): a
    /// reply that had to leave them out never sends them again, so the following replies begin with the same exchange and
    /// the provider's prompt cache (or Ollama's) still holds the start of their requests.</summary>
    internal void LetGoBefore(long start)
    {
        while (removed < start && entries.Count > 0)
            RemoveOldest();
    }

    private void RemoveOldest()
    {
        if (!entries.TryDequeue(out var removedEntry))
            return;
        removed++;
        utf8Bytes -= removedEntry.Utf8Bytes;
    }
}
