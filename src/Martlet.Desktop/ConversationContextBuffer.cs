using System.Text;
using Martlet.Providers;

namespace Martlet.Desktop;

internal sealed class ConversationContextBuffer(TimeProvider clock)
{
    internal const int MaximumTurns = 8;
    internal const int MaximumUtf8Bytes = BoundedTextInput.HardMaxUtf8Bytes;
    internal static TimeSpan MaximumAge => TimeSpan.FromMinutes(2);

    private readonly Queue<Entry> entries = [];
    private int utf8Bytes;
    private sealed record Entry(long Timestamp, string User, string Assistant, int Utf8Bytes);

    internal int Count
    {
        get
        {
            RemoveExpired();
            return entries.Count;
        }
    }

    internal void Add(string user, string assistant)
    {
        var bytes = checked(Encoding.UTF8.GetByteCount(user) + Encoding.UTF8.GetByteCount(assistant));
        entries.Enqueue(new(clock.GetTimestamp(), user, assistant, bytes));
        utf8Bytes = checked(utf8Bytes + bytes);
        RemoveExpired();
        while (entries.Count > MaximumTurns || utf8Bytes > MaximumUtf8Bytes)
            RemoveOldest();
    }

    internal IReadOnlyList<TextHistoryMessage> Snapshot()
    {
        RemoveExpired();
        return entries.SelectMany(entry => new[]
        {
            new TextHistoryMessage(TextHistoryRole.User, entry.User),
            new TextHistoryMessage(TextHistoryRole.Assistant, entry.Assistant)
        }).ToArray();
    }

    internal void Clear()
    {
        entries.Clear();
        utf8Bytes = 0;
    }

    private void RemoveExpired()
    {
        while (entries.TryPeek(out var entry) && clock.GetElapsedTime(entry.Timestamp) >= MaximumAge)
            RemoveOldest();
    }

    private void RemoveOldest()
    {
        if (!entries.TryDequeue(out var removed))
            return;
        utf8Bytes -= removed.Utf8Bytes;
    }
}
