using System.Text;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>The exchanges of the open conversation, kept in memory only for the next replies. Each reply sends as many of the
/// newest as fit its context size (Companion › Replies), so the buffer only bounds memory: at most
/// <see cref="MaximumTurns"/> exchanges and <see cref="MaximumUtf8Bytes"/>, the oldest going first. It is cleared by Refresh
/// context, pause, lock, a settings change and closing the talk window; nothing is written to disk.</summary>
internal sealed class ConversationContextBuffer
{
    internal const int MaximumTurns = BoundedTextInput.HardMaxHistoryMessages / 2;
    internal const int MaximumUtf8Bytes = BoundedTextInput.HardMaxInputUtf8Bytes;

    private readonly Queue<Entry> entries = [];
    private long utf8Bytes;
    private sealed record Entry(string User, string Assistant, int Utf8Bytes);

    internal int Count => entries.Count;

    /// <summary>The UTF-8 bytes of the kept exchanges.</summary>
    internal long Utf8Bytes => utf8Bytes;

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
        entries.Clear();
        utf8Bytes = 0;
    }

    private void RemoveOldest()
    {
        if (!entries.TryDequeue(out var removed))
            return;
        utf8Bytes -= removed.Utf8Bytes;
    }
}
