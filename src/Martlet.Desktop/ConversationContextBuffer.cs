using System.Text;
using Martlet.Providers;

// Also built into Martlet's MCP server (straight_voice_check), in its own namespace.
#if MARTLET_MCP
namespace Martlet.Mcp.Shared;
#else
namespace Martlet.Desktop;
#endif

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
    // Sent: the user's message exactly as a Thinking model on this PC got it (the words and Martlet's notes), or null. Filling: a
    // message that went straight to Thinking as the recording alone, whose words (the transcript) replace what stands in for them
    // once speech-to-text has them (Fill).
    private sealed class Entry(string user, string assistant, string? sent)
    {
        internal string User { get; set; } = user;
        internal string Assistant { get; set; } = assistant;
        internal string? Sent { get; set; } = sent;
        internal int Utf8Bytes { get; set; } = Bytes(user, assistant, sent);
        internal Task? Filling { get; set; }
        // A look Martlet took on its own and passed on (VisionHistory): the next one it passes on takes its place.
        internal bool PassedLook { get; set; }
    }

    private Entry? newest;

    private static int Bytes(string user, string assistant, string? sent) =>
        checked(Encoding.UTF8.GetByteCount(sent ?? user) + Encoding.UTF8.GetByteCount(assistant));

    internal int Count => entries.Count;

    /// <summary>The UTF-8 bytes of the kept exchanges.</summary>
    internal long Utf8Bytes => utf8Bytes;

    /// <summary>The position of the oldest kept exchange in the whole conversation (exchanges let go or cleared before it),
    /// so a snapshot's indices can be matched with the buffer later.</summary>
    internal long Start => removed;

    /// <summary>Keeps an exchange. <paramref name="sent"/> is the user's message as the Thinking model got it (the words and
    /// Martlet's notes, <see cref="Martlet.Providers.BoundedTextInput.KeptUserText"/>; the context board's notes are left out, so
    /// it is the start of what was sent): the next replies send it again as it was (see <see cref="Snapshot"/>), so each request starts like
    /// the one before and the provider's prompt cache (or Ollama's, which reuses only a request that starts with a whole earlier
    /// one) holds it. Null for a paired host, which gets the notes with its instructions. Returns the exchange, for
    /// <see cref="Fill"/>.</summary>
    internal object Add(string user, string assistant, string? sent = null)
    {
        var entry = new Entry(user, assistant, sent);
        entries.Enqueue(entry);
        newest = entry;
        utf8Bytes += entry.Utf8Bytes;
        while (entries.Count > MaximumTurns || utf8Bytes > MaximumUtf8Bytes)
            RemoveOldest();
        return entry;
    }

    /// <summary>Keeps a look Martlet took on its own (a screen glance or camera look, <see cref="VisionHistory"/>): what it
    /// looked at and saw, then its remark or [pass]. Passes don't pile up: a look it <paramref name="passed"/> on takes the place
    /// of the newest exchange when that is also a passed look, so only the last of a quiet stretch stays (only the end of
    /// the next request changes, and that end is new anyway). Returns whether it replaced one.</summary>
    internal bool AddLook(string user, string assistant, bool passed)
    {
        if (passed && newest is { PassedLook: true } last && entries.Count > 0)
        {
            utf8Bytes -= last.Utf8Bytes;
            last.User = user;
            last.Assistant = assistant;
            last.Sent = null;
            last.Utf8Bytes = Bytes(user, assistant, null);
            utf8Bytes += last.Utf8Bytes;
            while (utf8Bytes > MaximumUtf8Bytes && entries.Count > 0)
                RemoveOldest();
            return true;
        }
        ((Entry)Add(user, assistant)).PassedLook = passed;
        return false;
    }

    /// <summary>Marks a kept exchange whose words are on their way: <paramref name="filling"/> ends once <see cref="Fill"/> has
    /// them (or they never came).</summary>
    internal static void Pending(object exchange, Task filling) => ((Entry)exchange).Filling = filling;

    /// <summary>The words of a message that went straight to Thinking as the recording alone, in place of what stood in for them,
    /// so the next replies carry the transcript. False when the exchange was already let go.</summary>
    internal bool Fill(object exchange, string user, string? sent)
    {
        var entry = (Entry)exchange;
        if (!entries.Contains(entry)) return false;
        utf8Bytes -= entry.Utf8Bytes;
        entry.User = user;
        entry.Sent = sent;
        entry.Utf8Bytes = Bytes(user, entry.Assistant, sent);
        utf8Bytes += entry.Utf8Bytes;
        while (utf8Bytes > MaximumUtf8Bytes && entries.Count > 0)
            RemoveOldest();
        return true;
    }

    /// <summary>The kept exchanges whose words are still on their way (speech-to-text beside a reply).</summary>
    internal Task[] Filling() => [.. entries.Select(entry => entry.Filling).OfType<Task>().Where(task => !task.IsCompleted)];

    /// <summary>The kept exchanges: what the user said, or with <paramref name="sent"/> each message as it went to the Thinking
    /// model (with its notes) where Martlet kept that. Lore, memory and learning names read the plain ones.</summary>
    internal IReadOnlyList<TextHistoryMessage> Snapshot(bool sent = false) => entries.SelectMany(entry => new[]
    {
        new TextHistoryMessage(TextHistoryRole.User, sent ? entry.Sent ?? entry.User : entry.User),
        new TextHistoryMessage(TextHistoryRole.Assistant, entry.Assistant)
    }).ToArray();

    internal void Clear()
    {
        removed += entries.Count;
        entries.Clear();
        newest = null;
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
        if (entries.Count == 0) newest = null;
    }
}
