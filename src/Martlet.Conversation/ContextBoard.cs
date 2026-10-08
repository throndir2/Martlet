using System.Text;

namespace Martlet.Conversation;

/// <summary>One note on the <see cref="ContextBoard"/>: the newest short text a source posted, when it posted it, how long it
/// stays fresh and whether it goes with only one request (<see cref="Consume"/>). <see cref="Version"/> tells a note apart from
/// a later one of the same source. <see cref="Kept"/> is an optional short line the conversation keeps at the end of the
/// user's message once the request that carried the note is sent (the note itself is never kept).</summary>
public sealed record ContextNote(string Source, string Text, DateTimeOffset At, TimeSpan MaxAge, bool Consume, long Version,
    string? Kept = null)
{
    /// <summary>Whether the note is still fresh at <paramref name="now"/>.</summary>
    public bool FreshAt(DateTimeOffset now) => now - At <= MaxAge;
}

/// <summary>The fresh notes of the <see cref="ContextBoard"/> at one moment, in the board's stable source order.</summary>
public sealed record ContextBoardSnapshot(IReadOnlyList<ContextNote> Notes, DateTimeOffset At)
{
    public static ContextBoardSnapshot Empty { get; } = new([], DateTimeOffset.MinValue);

    /// <summary>The notes as one text, one note to a line, or null when there are none.</summary>
    public string? Text => Notes.Count == 0 ? null : string.Join("\n", Notes.Select(n => n.Text));

    /// <summary>The sources of the notes, in order.</summary>
    public IReadOnlyList<string> Sources => [.. Notes.Select(n => n.Source)];

    /// <summary>The notes' <see cref="ContextNote.Kept"/> lines, one to a line, or null when none has one.</summary>
    public string? KeptText => Notes.Where(n => n.Kept is not null).Select(n => n.Kept!).ToArray() is { Length: > 0 } kept
        ? string.Join("\n", kept) : null;

    /// <summary>The UTF-8 bytes of <see cref="Text"/>.</summary>
    public int Utf8Bytes => Text is { } text ? Encoding.UTF8.GetByteCount(text) : 0;
}

/// <summary>The context board: where background sources (the character's lingering emotes, a digest of the screen, the
/// sounds this PC plays, touches on the character...) each keep their newest short note for the live conversation. A reply
/// takes a <see cref="Snapshot"/> as it builds its request and never waits for a source: what is fresh on the board then
/// goes in the notes after the user's words, after Martlet's other notes and never in the conversation's history, so the
/// start of every request stays the same and prompt caches keep working. Thread-safe.</summary>
/// <remarks>A source posts with <see cref="Post"/> (a new note of the same source replaces the one before) and removes its
/// note with <see cref="Clear"/>. A note older than its maximum age is skipped and dropped. A <c>consume</c> note goes with
/// exactly one request: <see cref="MarkSent"/> removes it once the request that carried it is sent, unless the source posted
/// a newer note since. Each note is at most <see cref="MaximumNoteUtf8Bytes"/> (longer text is cut), and a snapshot holds at
/// most <see cref="MaximumUtf8Bytes"/>: notes later in the order that don't fit are left out.</remarks>
public sealed class ContextBoard
{
    /// <summary>The lingering emotes the desktop character shows now (Martlet posts it as it builds each request).</summary>
    public const string Character = "character";
    /// <summary>Where the desktop character's eyes are while a reply's choice holds them (Martlet posts it as it builds each
    /// request, and clears it while the eyes do their usual).</summary>
    public const string Gaze = "gaze";
    /// <summary>A digest of the last seconds of the screen.</summary>
    public const string Screen = "screen";
    /// <summary>A line about the sounds this PC plays (music, game sounds, laughter).</summary>
    public const string Sound = "sound";
    /// <summary>What the user seems to be doing on this PC (which apps play sound, what fills the screen), while Martlet hears it.</summary>
    public const string Activity = "activity";
    /// <summary>Touches on the character (taps, pats, strokes, drags, zoom); usually posted with <c>consume</c>.</summary>
    public const string Touch = "touch";

    /// <summary>The most UTF-8 bytes of one note; longer text is cut at a character and ends with "...".</summary>
    public const int MaximumNoteUtf8Bytes = 600;
    /// <summary>The most UTF-8 bytes of a snapshot's notes together (with the line breaks between them).</summary>
    public const int MaximumUtf8Bytes = 2_048;
    /// <summary>The most sources the board keeps at once.</summary>
    public const int MaximumSources = 16;
    /// <summary>The longest a source name may be: lower-case ASCII letters, digits and '-'.</summary>
    public const int MaximumSourceLength = 32;
    /// <summary>The longest a note may stay fresh.</summary>
    public static readonly TimeSpan MaximumAge = TimeSpan.FromHours(1);

    // The known sources come first, in this order; others follow by name.
    private static readonly string[] Order = [Character, Gaze, Screen, Sound, Activity, Touch];

    private readonly object gate = new();
    private readonly Dictionary<string, ContextNote> notes = new(StringComparer.Ordinal);
    private long version;
    private ContextBoardSnapshot lastSent = ContextBoardSnapshot.Empty;

    /// <summary>Raised after a note is posted, cleared, consumed or sent (on the caller's thread).</summary>
    public event Action? Changed;

    /// <summary>Raised once for each request that was actually sent, with the notes it carried (on the caller's thread). A
    /// source finds its note by <see cref="ContextNote.Version"/>: there, it was delivered. A turn stopped before its request
    /// was sent raises nothing, and its notes stay on the board for the next request.</summary>
    public event Action<ContextBoardSnapshot>? Sent;

    /// <summary>The notes the newest sent request carried.</summary>
    public ContextBoardSnapshot LastSent { get { lock (gate) return lastSent; } }

    /// <summary>Whether <paramref name="source"/> is a valid source name: 1 to <see cref="MaximumSourceLength"/> lower-case
    /// ASCII letters, digits and '-'.</summary>
    public static bool IsSource(string? source) =>
        source is { Length: > 0 and <= MaximumSourceLength } && source.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    /// <summary>Keeps <paramref name="text"/> as the newest note of <paramref name="source"/>, posted at <paramref name="at"/>
    /// and fresh for <paramref name="maxAge"/> (at most <see cref="MaximumAge"/>). It replaces the source's note before. Text
    /// is made one line (white space becomes single spaces) and cut to <see cref="MaximumNoteUtf8Bytes"/>; empty text clears
    /// the source. A <paramref name="consume"/> note goes with only the next request sent. <paramref name="kept"/> (made one
    /// line and cut the same way) is kept at the end of the user's message in the conversation once a request carrying the
    /// note is sent. Returns the note, or null when it cleared the source.</summary>
    public ContextNote? Post(string source, string? text, DateTimeOffset at, TimeSpan maxAge, bool consume = false,
        string? kept = null)
    {
        if (!IsSource(source)) throw new ArgumentException("A source is 1-32 lower-case letters, digits or '-'.", nameof(source));
        if (maxAge <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maxAge), "A note must stay fresh for some time.");
        var line = OneLine(text);
        if (line is null)
        {
            Clear(source);
            return null;
        }
        ContextNote note;
        lock (gate)
        {
            if (!notes.ContainsKey(source) && notes.Count >= MaximumSources)
                throw new InvalidOperationException($"The context board keeps at most {MaximumSources} sources.");
            note = new(source, line, at, maxAge < MaximumAge ? maxAge : MaximumAge, consume, ++version, OneLine(kept));
            notes[source] = note;
        }
        Changed?.Invoke();
        return note;
    }

    /// <summary>Removes the note of <paramref name="source"/>. Returns whether there was one.</summary>
    public bool Clear(string source)
    {
        bool removed;
        lock (gate) removed = notes.Remove(source);
        if (removed) Changed?.Invoke();
        return removed;
    }

    /// <summary>The fresh notes at <paramref name="now"/>, in the stable source order (<see cref="Character"/>, <see cref="Gaze"/>,
    /// <see cref="Screen"/>, <see cref="Sound"/>, <see cref="Activity"/>, <see cref="Touch"/>, then others by name), within
    /// <see cref="MaximumUtf8Bytes"/>. Stale notes are dropped. Nothing is consumed: <see cref="MarkSent"/> does that once the
    /// request is sent.</summary>
    public ContextBoardSnapshot Snapshot(DateTimeOffset now)
    {
        var fresh = new List<ContextNote>();
        lock (gate)
        {
            foreach (var stale in notes.Values.Where(n => !n.FreshAt(now)).Select(n => n.Source).ToArray()) notes.Remove(stale);
            var bytes = 0;
            foreach (var note in notes.Values.OrderBy(n => Rank(n.Source)).ThenBy(n => n.Source, StringComparer.Ordinal))
            {
                var size = Encoding.UTF8.GetByteCount(note.Text) + (fresh.Count == 0 ? 0 : 1);
                if (bytes + size > MaximumUtf8Bytes) continue;
                bytes += size;
                fresh.Add(note);
            }
        }
        return new(fresh, now);
    }

    /// <summary>Records that a request carrying <paramref name="sent"/> was sent: it becomes <see cref="LastSent"/>, and each
    /// <c>consume</c> note in it is removed unless its source posted a newer note since. Call it only when the request is
    /// actually sent, never for a turn stopped before. Raises <see cref="Sent"/>. Returns how many notes were consumed.</summary>
    public int MarkSent(ContextBoardSnapshot sent)
    {
        ArgumentNullException.ThrowIfNull(sent);
        var consumed = 0;
        lock (gate)
        {
            lastSent = sent;
            foreach (var note in sent.Notes.Where(n => n.Consume))
                if (notes.TryGetValue(note.Source, out var current) && current.Version == note.Version && notes.Remove(note.Source))
                    consumed++;
        }
        Changed?.Invoke();
        Sent?.Invoke(sent);
        return consumed;
    }

    private static int Rank(string source)
    {
        var index = Array.IndexOf(Order, source);
        return index < 0 ? Order.Length : index;
    }

    private static string? OneLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var builder = new StringBuilder(Math.Min(text.Length, MaximumNoteUtf8Bytes));
        var space = false;
        foreach (var c in text.Trim())
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c)) space = true;
            else
            {
                if (space && builder.Length > 0) builder.Append(' ');
                space = false;
                builder.Append(c);
            }
        }
        var line = builder.ToString();
        if (Encoding.UTF8.GetByteCount(line) <= MaximumNoteUtf8Bytes) return line;
        const string cut = "...";
        var budget = MaximumNoteUtf8Bytes - cut.Length;
        var end = 0;
        var used = 0;
        while (end < line.Length)
        {
            var width = char.IsHighSurrogate(line[end]) && end + 1 < line.Length ? 2 : 1;
            var size = Encoding.UTF8.GetByteCount(line.AsSpan(end, width));
            if (used + size > budget) break;
            used += size;
            end += width;
        }
        return line[..end].TrimEnd() + cut;
    }
}
