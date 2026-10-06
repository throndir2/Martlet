using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Martlet.Discord;

/// <summary>One Discord text message as the text chat sees it (no NetCord types, so the pipeline runs with a fake transport).
/// <paramref name="MentionsMartlet"/>: the message @mentions the bot or its own role; <paramref name="RepliesToMartlet"/>: it
/// is a reply to one of Martlet's messages; <paramref name="FromBot"/>: another bot (or a webhook) wrote it.</summary>
public sealed record DiscordIncoming(ulong MessageId, DiscordPlace Place, DiscordSpeaker Speaker, string Text,
    bool MentionsMartlet = false, bool RepliesToMartlet = false, bool FromBot = false);

public enum DiscordTextOutcome { IgnoredBot, Empty, NotConsidered, NoEngine, Passed, Answered, Dropped, Failed }

public sealed record DiscordTextResult(DiscordTextOutcome Outcome, bool Addressed, DiscordChatMode Mode,
    IReadOnlyList<string> Sent, string? Problem = null);

/// <summary>What the text chat did since Martlet started (no message text, names or IDs): for the Discord page, MCP and the log.
/// <paramref name="LastPlaceKind"/> is "DM" or "server" for the last reply sent.</summary>
public sealed record DiscordTextStats(int Seen, int Considered, int Answered, int Passed, int Dropped, int Failed,
    string? LastPlaceKind, string? LastError)
{
    public static DiscordTextStats Empty { get; } = new(0, 0, 0, 0, 0, 0, null, null);
}

public sealed record DiscordTextOptions
{
    /// <summary>Lines of each place's recent conversation kept for <see cref="DiscordTurn.Recent"/>.</summary>
    public int RecentLines { get; init; } = 20;
    /// <summary>Places whose recent lines are kept (the least recently used is forgotten first).</summary>
    public int RecentPlaces { get; init; } = 200;
    /// <summary>Least time between two of Martlet's messages in one channel (Discord also rate-limits; NetCord waits out 429s).</summary>
    public TimeSpan MinimumInterval { get; init; } = TimeSpan.FromSeconds(1.5);
    /// <summary>Skip a turn when a newer message in the same place arrives before its reply is sent; the newer turn answers
    /// with both in its recent lines.</summary>
    public bool DropStaleTurns { get; init; } = true;
    /// <summary>After this many dropped turns in a row in one place the next reply is sent anyway, so a busy channel still gets one.</summary>
    public int MaximumDropsInARow { get; init; } = 2;
    public TimeSpan ReplyTimeout { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>Sends Martlet's text to Discord. The desktop's NetCord transport sends replies with no mentions allowed except the
/// replied-to person; tests and MCP use a fake.</summary>
public interface IDiscordTextTransport
{
    /// <summary>Shows Martlet typing in <paramref name="place"/> until the result is disposed.</summary>
    IDisposable Typing(DiscordPlace place);

    /// <summary>Sends one message (at most <see cref="DiscordTextFormat.Limit"/> characters), as a reply to
    /// <paramref name="replyTo"/> when given.</summary>
    Task SendAsync(DiscordPlace place, string text, ulong? replyTo, CancellationToken token);
}

/// <summary>Whether a message was meant for Martlet.</summary>
public static partial class DiscordAddressing
{
    /// <summary>A DM, an @mention of the bot or its role, a reply to Martlet or one of its names said as a word.</summary>
    public static bool IsAddressed(DiscordIncoming message, IEnumerable<string> names) =>
        message.Place.Direct || message.MentionsMartlet || message.RepliesToMartlet || NameSaid(message.Text, names);

    /// <summary>Whether <paramref name="text"/> says one of <paramref name="names"/> (or a word of one of at least three letters)
    /// as a whole word, ignoring case.</summary>
    public static bool NameSaid(string text, IEnumerable<string> names)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var said = new HashSet<string>(Words(text), StringComparer.OrdinalIgnoreCase);
        var joined = " " + string.Join(' ', Words(text)) + " ";
        foreach (var name in names)
        {
            var parts = Words(name);
            if (parts.Length == 0) continue;
            if (joined.Contains(" " + string.Join(' ', parts) + " ", StringComparison.OrdinalIgnoreCase)) return true;
            if (parts.Any(part => part.Length >= 3 && said.Contains(part))) return true;
        }
        return false;
    }

    /// <summary>The text without mentions of the bot (&lt;@id&gt; or &lt;@!id&gt;), for the reply engine.</summary>
    public static string WithoutMention(string text, ulong botId) => botId == 0 ? text.Trim()
        : Regex.Replace(text, $@"<@!?{botId.ToString(CultureInfo.InvariantCulture)}>", "").Replace("  ", " ").Trim();

    private static string[] Words(string? text) => text is null ? [] : WordPattern().Matches(text).Select(match => match.Value).ToArray();

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}'_-]*")]
    private static partial Regex WordPattern();
}

/// <summary>Martlet's reply as Discord messages: mass mentions neutralized and split under Discord's 2,000-character limit.</summary>
public static partial class DiscordTextFormat
{
    public const int Limit = 2000;

    /// <summary>Puts a zero-width space in @everyone, @here and role mentions so they show as text even where mentions would be
    /// allowed (the transport also sends with no mentions allowed).</summary>
    public static string Sanitize(string text) =>
        RoleMention().Replace(MassMention().Replace(text, "@\u200b$1"), "<@\u200b&$1>");

    /// <summary>Splits <paramref name="text"/> into messages of at most <paramref name="limit"/> characters, at a paragraph, line,
    /// sentence or word break when there is one in the second half of a piece.</summary>
    public static IReadOnlyList<string> Split(string text, int limit = Limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 2);
        List<string> pieces = [];
        var rest = text.Trim();
        while (rest.Length > limit)
        {
            var cut = Break(rest, limit);
            var piece = rest[..cut].TrimEnd();
            if (piece.Length > 0) pieces.Add(piece);
            rest = rest[cut..].TrimStart();
        }
        if (rest.Length > 0) pieces.Add(rest);
        return pieces;
    }

    private static int Break(string text, int limit)
    {
        var window = text[..limit];
        var least = limit / 2;
        foreach (var separator in new[] { "\n\n", "\n", ". ", "! ", "? ", " " })
        {
            var at = window.LastIndexOf(separator, StringComparison.Ordinal);
            if (at >= least) return at + separator.Length;
        }
        // Never cut a surrogate pair in half.
        return char.IsHighSurrogate(text[limit - 1]) ? limit - 1 : limit;
    }

    [GeneratedRegex("@(everyone|here)", RegexOptions.IgnoreCase)]
    private static partial Regex MassMention();

    [GeneratedRegex(@"<@&(\d+)>")]
    private static partial Regex RoleMention();
}

/// <summary>The last few lines of each Discord place, Martlet's own included, for <see cref="DiscordTurn.Recent"/>. Bounded per
/// place and in the number of places. Thread-safe.</summary>
public sealed class DiscordRecentLines(int perPlace = 20, int places = 200)
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, LinkedList<(long Id, DiscordLine Line)>> lines = new(StringComparer.Ordinal);
    private readonly LinkedList<string> used = new();
    private long next;

    /// <summary>Adds a line and returns its ID (increasing), for <see cref="Before"/>.</summary>
    public long Add(DiscordPlace place, DiscordLine line)
    {
        lock (gate)
        {
            if (!lines.TryGetValue(place.Key, out var kept))
            {
                lines[place.Key] = kept = new();
                while (lines.Count > Math.Max(1, places) && used.Last is { } oldest)
                {
                    lines.Remove(oldest.Value);
                    used.RemoveLast();
                }
            }
            else used.Remove(place.Key);
            used.AddFirst(place.Key);
            kept.AddLast((++next, line));
            while (kept.Count > Math.Max(1, perPlace)) kept.RemoveFirst();
            return next;
        }
    }

    /// <summary>The place's kept lines older than line <paramref name="id"/> (all of them for <see cref="long.MaxValue"/>).</summary>
    public IReadOnlyList<DiscordLine> Before(DiscordPlace place, long id = long.MaxValue)
    {
        lock (gate)
            return lines.TryGetValue(place.Key, out var kept)
                ? kept.Where(entry => entry.Id < id).Select(entry => entry.Line).ToArray()
                : [];
    }

    public int Places { get { lock (gate) return lines.Count; } }
}

/// <summary>Martlet's Discord text chat: decides whether a message is for it (chat mode and addressing), keeps each place's
/// recent lines, asks the reply engine one turn at a time per place, shows typing while it thinks, drops a turn a newer message
/// made stale and sends the reply (a Discord reply when it was addressed, split, mass mentions neutralized, at most one message
/// per <see cref="DiscordTextOptions.MinimumInterval"/> per channel). The transport is NetCord in the desktop and a fake in tests
/// and MCP.</summary>
public sealed class DiscordTextChat
{
    private readonly IDiscordTextTransport transport;
    private readonly Func<IDiscordReplyEngine?> engine;
    private readonly Func<DiscordIncoming, DiscordChatMode> modeOf;
    private readonly Func<IEnumerable<string>> names;
    private readonly DiscordTextOptions options;
    private readonly TimeProvider time;
    private readonly Lock gate = new();
    private readonly Dictionary<string, PlaceState> placeStates = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, DateTimeOffset> lastSent = [];
    private DiscordTextStats stats = DiscordTextStats.Empty;

    public DiscordTextChat(IDiscordTextTransport transport, Func<IDiscordReplyEngine?> engine,
        Func<DiscordIncoming, DiscordChatMode> mode, Func<IEnumerable<string>> names, DiscordTextOptions? options = null,
        TimeProvider? time = null)
    {
        this.transport = transport;
        this.engine = engine;
        modeOf = mode;
        this.names = names;
        this.options = options ?? new();
        this.time = time ?? TimeProvider.System;
        Recent = new(this.options.RecentLines, this.options.RecentPlaces);
    }

    public DiscordRecentLines Recent { get; }
    public DiscordTextStats Stats { get { lock (gate) return stats; } }

    /// <summary>Raised (off the UI thread) whenever <see cref="Stats"/> change.</summary>
    public event Action<DiscordTextStats>? Changed;

    /// <summary>A message seen in a channel, thread or DM.</summary>
    public Task<DiscordTextResult> HandleAsync(DiscordIncoming message, CancellationToken token = default) =>
        RunAsync(message, command: null, token);

    /// <summary>A slash command (/martlet): always addressed, never dropped as stale, delivered by <paramref name="deliver"/>
    /// (the interaction's follow-up) instead of the channel.</summary>
    public Task<DiscordTextResult> CommandAsync(DiscordIncoming message, Func<IReadOnlyList<string>, Task> deliver,
        CancellationToken token = default) => RunAsync(message, deliver, token);

    private async Task<DiscordTextResult> RunAsync(DiscordIncoming message, Func<IReadOnlyList<string>, Task>? command,
        CancellationToken token)
    {
        Count(s => s with { Seen = s.Seen + 1 });
        var mode = DiscordChatMode.Off;
        var addressed = command is not null;
        if (message.FromBot)
        {
            Recent.Add(message.Place, Line(message));
            return new(DiscordTextOutcome.IgnoredBot, false, mode, []);
        }
        if (string.IsNullOrWhiteSpace(message.Text)) return new(DiscordTextOutcome.Empty, false, mode, []);
        var lineId = Recent.Add(message.Place, Line(message));
        mode = modeOf(message);
        addressed |= DiscordAddressing.IsAddressed(message, names());
        if (!DiscordChatRules.Considers(mode, addressed)) return new(DiscordTextOutcome.NotConsidered, addressed, mode, []);
        Count(s => s with { Considered = s.Considered + 1 });

        var state = State(message.Place);
        long ticket;
        lock (state) ticket = command is null ? ++state.Latest : state.Latest;
        await state.Turn.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (command is null && Stale(state, ticket, addressed, message.MessageId))
                return Dropped(addressed, mode);
            // A turn dropped as stale hands its "addressed" (and the message to reply to) to the one that answers.
            var replyTo = message.MessageId;
            if (command is null)
                lock (state)
                {
                    if (state.CarriedAddressed) { addressed = true; replyTo = state.CarriedReplyTo ?? replyTo; }
                    state.CarriedAddressed = false;
                    state.CarriedReplyTo = null;
                }
            if (engine() is not { } replies) return Fail(addressed, mode, "The reply engine isn't ready yet.");

            DiscordReply? reply;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(options.ReplyTimeout);
                using var typing = command is null ? transport.Typing(message.Place) : null;
                var turn = new DiscordTurn(message.Place, message.Speaker, message.Text, DiscordTurnSource.Text, addressed,
                    Recent.Before(message.Place, lineId), mode);
                try { reply = await replies.ReplyAsync(turn, timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    return Fail(addressed, mode, "The reply took too long.");
                }
            }
            if (command is null && Stale(state, ticket, addressed, replyTo)) return Dropped(addressed, mode);
            lock (state) state.DroppedInARow = 0;
            var text = reply?.Text?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                Count(s => s with { Passed = s.Passed + 1 });
                return new(DiscordTextOutcome.Passed, addressed, mode, []);
            }

            var pieces = DiscordTextFormat.Split(DiscordTextFormat.Sanitize(text));
            if (command is not null) await command(pieces).ConfigureAwait(false);
            else
                for (var index = 0; index < pieces.Count; index++)
                {
                    await PaceAsync(message.Place.ChannelId, token).ConfigureAwait(false);
                    // A reply in a channel or thread quotes the message it answers; a DM is a one-to-one conversation.
                    var quote = index == 0 && addressed && !message.Place.Direct;
                    await transport.SendAsync(message.Place, pieces[index], quote ? replyTo : null, token)
                        .ConfigureAwait(false);
                }
            Recent.Add(message.Place, new(CompanionName(), text, time.GetUtcNow(), true));
            var kind = message.Place.GuildId is null ? "DM" : "server";
            Count(s => s with { Answered = s.Answered + 1, LastPlaceKind = kind });
            return new(DiscordTextOutcome.Answered, addressed, mode, pieces);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return Fail(addressed, mode, error.Message);
        }
        finally
        {
            state.Turn.Release();
        }
    }

    private bool Stale(PlaceState state, long ticket, bool addressed, ulong replyTo)
    {
        if (!options.DropStaleTurns) return false;
        lock (state)
        {
            if (state.Latest == ticket || state.DroppedInARow >= options.MaximumDropsInARow) return false;
            state.DroppedInARow++;
            if (addressed && !state.CarriedAddressed) { state.CarriedAddressed = true; state.CarriedReplyTo = replyTo; }
            return true;
        }
    }

    private DiscordTextResult Dropped(bool addressed, DiscordChatMode mode)
    {
        Count(s => s with { Dropped = s.Dropped + 1 });
        return new(DiscordTextOutcome.Dropped, addressed, mode, []);
    }

    private DiscordTextResult Fail(bool addressed, DiscordChatMode mode, string problem)
    {
        var shown = problem.Length > 200 ? problem[..200] : problem;
        Count(s => s with { Failed = s.Failed + 1, LastError = shown });
        return new(engine() is null ? DiscordTextOutcome.NoEngine : DiscordTextOutcome.Failed, addressed, mode, [], shown);
    }

    private async Task PaceAsync(ulong channelId, CancellationToken token)
    {
        TimeSpan wait;
        lock (gate)
        {
            var now = time.GetUtcNow();
            var at = lastSent.TryGetValue(channelId, out var last) ? last + options.MinimumInterval : now;
            if (at < now) at = now;
            lastSent[channelId] = at;
            wait = at - now;
        }
        if (wait > TimeSpan.Zero) await Task.Delay(wait, time, token).ConfigureAwait(false);
    }

    private PlaceState State(DiscordPlace place)
    {
        lock (gate)
        {
            if (!placeStates.TryGetValue(place.Key, out var state)) placeStates[place.Key] = state = new();
            return state;
        }
    }

    private string CompanionName() => names().FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? "Martlet";

    private DiscordLine Line(DiscordIncoming message) => new(message.Speaker.Name, message.Text, time.GetUtcNow(), false);

    private void Count(Func<DiscordTextStats, DiscordTextStats> change)
    {
        DiscordTextStats next;
        lock (gate) next = stats = change(stats);
        Changed?.Invoke(next);
    }

    private sealed class PlaceState
    {
        public readonly SemaphoreSlim Turn = new(1, 1);
        public long Latest;
        public int DroppedInARow;
        public bool CarriedAddressed;
        public ulong? CarriedReplyTo;
    }

    /// <summary>A short, non-secret line for the desktop log.</summary>
    public static string Describe(DiscordTextResult result) => new StringBuilder("Discord text: ")
        .Append(result.Outcome).Append(" (").Append(result.Mode).Append(result.Addressed ? ", addressed" : "").Append(')')
        .Append(result.Sent.Count > 0 ? $", {result.Sent.Count} message(s)" : "")
        .Append(result.Problem is { } problem ? $": {problem}" : "").ToString();
}
