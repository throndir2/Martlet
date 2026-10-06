using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Martlet.Discord;

/// <summary>How the reply engine bounds Discord history and how readily it speaks up on its own.</summary>
public sealed record DiscordReplyOptions
{
    /// <summary>Lines kept per place (others' messages and Martlet's replies).</summary>
    public int HistoryLines { get; init; } = 40;
    /// <summary>Places whose history is kept; the least recently used is forgotten first.</summary>
    public int MaxPlaces { get; init; } = 200;
    /// <summary>Characters kept of one line (longer messages are cut).</summary>
    public int MaxLineCharacters { get; init; } = 1_500;
    /// <summary>Thinking requests at once across every place (each place is one at a time).</summary>
    public int MaxConcurrent { get; init; } = 2;
    /// <summary>The least time between Martlet's last message in a place and an unprompted one there.</summary>
    public TimeSpan AmbientCooldown { get; init; } = TimeSpan.FromSeconds(90);
    /// <summary>The same for a voice call, where conversation moves faster.</summary>
    public TimeSpan VoiceAmbientCooldown { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Unprompted replies per place per hour.</summary>
    public int AmbientPerHour { get; init; } = 6;
    /// <summary>Other people's lines needed since Martlet's last unprompted reply before another (never twice in a row).</summary>
    public int AmbientLinesBetween { get; init; } = 3;
    /// <summary>The chance an ambient turn is put to the model at all when nothing in it concerns Martlet.</summary>
    public double AmbientChance { get; init; } = 0.3;
}

/// <summary>A failure (or a deliberate skip) while answering a Discord turn: a short, non-secret code.</summary>
public sealed class DiscordReplyException(string code, bool skipped = false) : Exception($"Discord reply: {code}")
{
    public string Code { get; } = code;
    /// <summary>The turn was deliberately left unanswered (such as the local conversation needing the model), not a failure.</summary>
    public bool Skipped { get; } = skipped;
}

/// <summary>What the engine has done since Martlet started: counts, times and the last problem (codes only, never what was
/// said or who said it).</summary>
public sealed record DiscordReplyStats(int Replies, int Passes, int Skipped, int Failures, int Places, int Running,
    DateTimeOffset? LastReplyAt, DateTimeOffset? LastPassAt, long? LastLatencyMs, string? LastSkip, string? LastError,
    DateTimeOffset? LastErrorAt);

/// <summary>A message of the prompt's earlier conversation: Martlet's own reply, or other people's lines (each "Name: text").</summary>
public sealed record DiscordPromptMessage(bool FromMartlet, string Text);

/// <summary>A Discord turn shaped for the Thinking model: the instructions that stay the same in this place (after the persona),
/// the earlier conversation in alternating messages starting with other people's, the message (everything said since
/// Martlet's last reply, each line "Name: text") and a note for this message only.</summary>
public sealed record DiscordPrompt(string Instructions, IReadOnlyList<DiscordPromptMessage> History, string Message, string? Note,
    bool MayPass, DiscordTurnSource Source);

/// <summary>What the engine hands the Thinking side for one turn: the turn, the earlier lines in its place (oldest first, without
/// the turn itself) and the owner's Discord name when known.</summary>
public sealed record DiscordTurnContext(DiscordTurn Turn, IReadOnlyList<DiscordLine> Earlier, string? OwnerName);

/// <summary>Asks the Thinking model about one turn: its raw answer, or null when it has nothing to add. Throws
/// <see cref="DiscordReplyException"/> on failure.</summary>
public delegate Task<string?> DiscordThink(DiscordTurnContext context, CancellationToken token);

/// <summary>Per-place conversation history, bounded in lines per place, characters per line and places, in memory only.</summary>
public sealed class DiscordHistory(DiscordReplyOptions? options = null)
{
    private readonly DiscordReplyOptions options = options ?? new();
    private readonly Lock gate = new();
    private readonly Dictionary<string, LinkedList<DiscordLine>> places = new(StringComparer.Ordinal);
    private readonly LinkedList<string> recency = new();

    public int Places { get { lock (gate) return places.Count; } }

    /// <summary>Keeps <paramref name="line"/> (bounded) as the place's newest; returns the line as kept.</summary>
    public DiscordLine Add(string place, DiscordLine line)
    {
        line = Bound(line);
        lock (gate)
        {
            var lines = Touch(place);
            lines.AddLast(line);
            while (lines.Count > options.HistoryLines) lines.RemoveFirst();
        }
        return line;
    }

    /// <summary>The place's lines, oldest first, merged with <paramref name="recent"/> (lines the caller saw that never reached the
    /// engine, such as chatter in a Mentions channel): the same speaker and text within a minute count once.</summary>
    public IReadOnlyList<DiscordLine> Lines(string place, IEnumerable<DiscordLine>? recent = null)
    {
        List<DiscordLine> lines;
        lock (gate) lines = places.TryGetValue(place, out var kept) ? [.. kept] : [];
        if (recent is null) return lines;
        foreach (var line in recent.Select(Bound))
            if (!lines.Any(seen => seen.FromMartlet == line.FromMartlet && seen.Speaker == line.Speaker && seen.Text == line.Text &&
                    (seen.At - line.At).Duration() < TimeSpan.FromMinutes(1)))
                lines.Add(line);
        return [.. lines.OrderBy(line => line.At).TakeLast(options.HistoryLines)];
    }

    public void Forget(string place)
    {
        lock (gate)
        {
            places.Remove(place);
            recency.Remove(place);
        }
    }

    private LinkedList<DiscordLine> Touch(string place)
    {
        recency.Remove(place);
        recency.AddLast(place);
        if (!places.TryGetValue(place, out var lines)) places[place] = lines = new();
        while (places.Count > options.MaxPlaces && recency.First is { } oldest)
        {
            places.Remove(oldest.Value);
            recency.RemoveFirst();
        }
        return lines;
    }

    private DiscordLine Bound(DiscordLine line) => line with
    {
        Speaker = DiscordPrompts.Name(line.Speaker),
        Text = line.Text.Length > options.MaxLineCharacters ? line.Text[..options.MaxLineCharacters] + "…" : line.Text
    };
}

/// <summary>Shapes a Discord turn for the Thinking model: who is talking, where, and how to answer there.</summary>
public static class DiscordPrompts
{
    /// <summary>The note on an ambient turn: Martlet may stay quiet by answering exactly [pass].</summary>
    public const string PassMarker = "[pass]";

    /// <summary>The instructions for a place. They stay the same for every turn there (the turn's own note carries whether it
    /// may pass), so a provider's prompt cache keeps the conversation.</summary>
    public static string Instructions(DiscordPlace place, DiscordTurnSource source, string? ownerName)
    {
        var where = source == DiscordTurnSource.Voice
            ? $"in a Discord voice call (the \"{Name(place.Name)}\" voice channel)"
            : place.Direct ? place.GuildId is null && place.Name.Length > 0 ? $"in a Discord direct message with {Name(place.Name)}" : "in a Discord direct message"
            : $"in the #{Name(place.Name)} channel of a Discord server";
        var text = new StringBuilder();
        text.Append($"You are chatting {where}, as yourself, through your own Discord bot account. ");
        text.Append("Several people may talk here: each of their messages starts with the speaker's Discord name and a colon, " +
            "and a message may hold a few lines said since you last spoke. ");
        if (ownerName is { Length: > 0 })
            text.Append($"{Name(ownerName)} is your owner, the person you live with on their PC; the others are people they or you know on Discord. ");
        text.Append("Answer only as yourself: never write other people's lines and never start with your own name. ");
        text.Append("Don't share private things you remember about your owner with other people. ");
        text.Append(source == DiscordTurnSource.Voice
            ? "Your reply is spoken aloud in the call: one or two short spoken sentences, with no markdown, emojis, lists or links."
            : $"Keep replies short and chat-like, usually a sentence or three. Discord markdown is fine; never write more than " +
              $"{DiscordReplyText.TextLimit} characters.");
        return text.ToString();
    }

    /// <summary>The note on a turn Martlet may pass over: it wasn't addressed, so it answers only with something worth adding.</summary>
    public static string PassNote =>
        $"This message wasn't addressed to you. Answer only if you have something worth adding to the conversation; otherwise " +
        $"answer exactly {PassMarker} and nothing else.";

    /// <summary>The turn as the model gets it: earlier lines as alternating messages (other people's grouped, Martlet's own as its
    /// replies, starting with other people's) and the message, everything said since Martlet's last reply.</summary>
    public static DiscordPrompt Shape(DiscordTurnContext context)
    {
        var turn = context.Turn;
        var messages = new List<DiscordPromptMessage>();
        var pending = new List<string>();
        foreach (var line in context.Earlier)
        {
            if (line.FromMartlet)
            {
                if (pending.Count > 0)
                {
                    messages.Add(new(false, string.Join("\n", pending)));
                    pending.Clear();
                }
                // Martlet's own lines with nothing before them in the window are left out: the history starts with other people.
                if (messages.Count == 0) continue;
                if (messages[^1].FromMartlet) messages[^1] = messages[^1] with { Text = messages[^1].Text + "\n" + line.Text };
                else messages.Add(new(true, line.Text));
            }
            else pending.Add(Line(line.Speaker, line.Text));
        }
        pending.Add(Line(turn.Speaker.Name, turn.Text));
        return new(Instructions(turn.Place, turn.Source, context.OwnerName), messages, string.Join("\n", pending),
            turn.MayPass ? PassNote : null, turn.MayPass, turn.Source);
    }

    /// <summary>One line as the model reads it: "Name: text".</summary>
    public static string Line(string speaker, string text) => $"{Name(speaker)}: {text}";

    /// <summary>A display name without control characters or line breaks, at most 64 characters ("Someone" when empty).</summary>
    public static string Name(string name)
    {
        var clean = new string(name.Where(c => !char.IsControl(c)).Take(64).ToArray()).Trim();
        return clean.Length == 0 ? "Someone" : clean;
    }
}

/// <summary>Turns the model's answer into what Martlet posts or says in Discord.</summary>
public static partial class DiscordReplyText
{
    /// <summary>Discord's limit on one message.</summary>
    public const int TextLimit = 2_000;
    /// <summary>The most Martlet says at once in a voice call.</summary>
    public const int VoiceLimit = 600;

    /// <summary>The answer is the [pass] marker (or empty): Martlet stays quiet. Same rule as the local conversation's.</summary>
    public static bool IsPass(string? text)
    {
        var trimmed = text?.Trim() ?? "";
        return trimmed.Length == 0 || trimmed.StartsWith("[pass", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed.Trim('[', ']', '(', ')', '<', '>', '*', '"', '\'', '.', '!', ' '), "pass", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The reply to post or say: without a leading "Name:" the model may copy from the transcript, plain sentences
    /// for voice, and within <see cref="TextLimit"/> (text) or <see cref="VoiceLimit"/> (voice). Null when nothing is left.</summary>
    public static string? Clean(string? raw, DiscordTurnSource source, IEnumerable<string> ownNames)
    {
        if (raw is null) return null;
        var text = raw.Trim();
        foreach (var name in ownNames.Where(name => name.Length > 0))
            if (text.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
            {
                text = text[(name.Length + 1)..].TrimStart();
                break;
            }
        if (source == DiscordTurnSource.Voice) text = Spoken(text);
        text = Limit(text, source == DiscordTurnSource.Voice ? VoiceLimit : TextLimit);
        return text.Length == 0 ? null : text;
    }

    /// <summary><paramref name="text"/> within <paramref name="limit"/> characters: cut at the last sentence end (or else word)
    /// that fits, with an ellipsis.</summary>
    public static string Limit(string text, int limit)
    {
        text = text.Trim();
        if (text.Length <= limit) return text;
        var room = text[..(limit - 1)];
        var sentence = room.LastIndexOfAny(['.', '!', '?', '\n']);
        if (sentence >= limit / 2) return room[..(sentence + 1)].TrimEnd();
        var word = room.LastIndexOf(' ');
        return (word >= limit / 2 ? room[..word] : room).TrimEnd() + "…";
    }

    /// <summary>Text to say aloud: markdown, links, custom emoji and emoji removed, lines joined.</summary>
    public static string Spoken(string text)
    {
        text = CodeBlock().Replace(text, " ");
        text = Link().Replace(text, "$1");
        text = CustomEmoji().Replace(text, " ");
        text = Url().Replace(text, " ");
        text = text.Replace("**", "").Replace("__", "").Replace("~~", "").Replace("`", "").Replace("||", "");
        text = ListMark().Replace(text, "");
        var kept = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.OtherSymbol or UnicodeCategory.Surrogate || rune.Value is 0xFE0F or 0x200D) continue;
            kept.Append(rune.ToString());
        }
        return Spaces().Replace(kept.ToString().Replace('*', ' ').Replace('#', ' '), " ").Trim();
    }

    [GeneratedRegex(@"```[\s\S]*?```")] private static partial Regex CodeBlock();
    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")] private static partial Regex Link();
    [GeneratedRegex(@"<a?:\w+:\d+>")] private static partial Regex CustomEmoji();
    [GeneratedRegex(@"https?://\S+")] private static partial Regex Url();
    [GeneratedRegex(@"(?m)^\s*(?:[-*+>]|\d+[.)])\s+")] private static partial Regex ListMark();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
}

/// <summary>Whether an ambient turn (one Martlet may pass over) is worth asking the model about at all, so Martlet speaks up only
/// once in a while in a busy chat: not within a cooldown of its last message in the place, at most a few unprompted replies an
/// hour, never two unprompted replies without others talking in between, and otherwise only sometimes unless the turn
/// mentions something Martlet cares about (its names). Like the local conversation's participation policy, the limits apply
/// to unprompted turns only; addressed turns always go through.</summary>
public sealed class DiscordAmbientGate(DiscordReplyOptions? options = null, TimeProvider? clock = null, Func<double>? random = null)
{
    private readonly DiscordReplyOptions options = options ?? new();
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly Func<double> random = random ?? Random.Shared.NextDouble;
    private readonly Lock gate = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> unprompted = new(StringComparer.Ordinal);

    /// <summary>Null when the model should be asked; otherwise why not (a short code).</summary>
    public string? Check(DiscordTurn turn, IReadOnlyList<DiscordLine> earlier, IEnumerable<string> interests)
    {
        if (!turn.MayPass) return null;
        var now = clock.GetUtcNow();
        var lastOwn = earlier.Select((line, index) => (line, index)).LastOrDefault(item => item.line.FromMartlet);
        var cooldown = turn.Source == DiscordTurnSource.Voice ? options.VoiceAmbientCooldown : options.AmbientCooldown;
        if (lastOwn.line is { } own && now - own.At < cooldown) return "cooldown";
        lock (gate)
        {
            if (unprompted.TryGetValue(turn.Place.Key, out var times))
            {
                while (times.Count > 0 && now - times.Peek() >= TimeSpan.FromHours(1)) times.Dequeue();
                if (times.Count >= options.AmbientPerHour) return "hourly_limit";
                // Martlet's last message here was unprompted: others must talk for a while before it speaks up again.
                if (lastOwn.line is { } last && times.Count > 0 && (times.Last() - last.At).Duration() < TimeSpan.FromSeconds(5) &&
                    earlier.Count - 1 - lastOwn.index + 1 < options.AmbientLinesBetween)
                    return "not_twice_in_a_row";
            }
        }
        if (Mentions(turn.Text, interests)) return null;
        return random() < options.AmbientChance ? null : "chance";
    }

    /// <summary>Martlet answered an unprompted turn in this place.</summary>
    public void Spoke(DiscordTurn turn)
    {
        if (!turn.MayPass) return;
        lock (gate)
        {
            if (!unprompted.TryGetValue(turn.Place.Key, out var times))
            {
                if (unprompted.Count >= options.MaxPlaces) unprompted.Remove(unprompted.Keys.First());
                unprompted[turn.Place.Key] = times = new();
            }
            times.Enqueue(clock.GetUtcNow());
            while (times.Count > options.AmbientPerHour) times.Dequeue();
        }
    }

    /// <summary>The text names one of <paramref name="interests"/> as a whole word (case-insensitive).</summary>
    public static bool Mentions(string text, IEnumerable<string> interests) =>
        interests.Where(word => word.Trim().Length >= 3).Any(word =>
            Regex.IsMatch(text, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(word.Trim())}(?![\p{{L}}\p{{N}}])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)));
}

/// <summary>The reply engine's Discord side: keeps each place's history, decides whether an ambient turn is worth the model's
/// time, runs one request per place at a time and at most <see cref="DiscordReplyOptions.MaxConcurrent"/> at once, and turns
/// the model's answer into a Discord reply (or quiet). The Thinking side (<see cref="DiscordThink"/>) is the host's.</summary>
public sealed class DiscordReplier : IDiscordReplyEngine
{
    private readonly DiscordThink think;
    private readonly DiscordReplyOptions options;
    private readonly TimeProvider clock;
    private readonly Func<IReadOnlyCollection<string>> names;
    private readonly SemaphoreSlim concurrency;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> places = new(StringComparer.Ordinal);
    private readonly Lock gate = new();
    private int replies, passes, skipped, failures, running;
    private DateTimeOffset? lastReplyAt, lastPassAt, lastErrorAt;
    private long? lastLatencyMs;
    private string? lastSkip, lastError, ownerName;

    /// <param name="names">The names Martlet goes by (persona names and "Martlet"): an ambient turn naming one is always
    /// considered, and a reply starting with one has it removed.</param>
    public DiscordReplier(DiscordThink think, Func<IReadOnlyCollection<string>> names, DiscordReplyOptions? options = null,
        TimeProvider? clock = null, Func<double>? random = null)
    {
        this.think = think;
        this.names = names;
        this.options = options ?? new();
        this.clock = clock ?? TimeProvider.System;
        History = new(this.options);
        Gate = new(this.options, this.clock, random);
        concurrency = new(Math.Max(1, this.options.MaxConcurrent));
    }

    public DiscordHistory History { get; }
    public DiscordAmbientGate Gate { get; }

    /// <summary>Raised (off the caller's thread of control) whenever <see cref="Stats"/> changed.</summary>
    public event Action<DiscordReplyStats>? Changed;

    public DiscordReplyStats Stats
    {
        get
        {
            lock (gate)
                return new(replies, passes, skipped, failures, History.Places, running, lastReplyAt, lastPassAt, lastLatencyMs,
                    lastSkip, lastError, lastErrorAt);
        }
    }

    public async Task<DiscordReply?> ReplyAsync(DiscordTurn turn, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(turn);
        if (string.IsNullOrWhiteSpace(turn.Text)) return null;
        var key = turn.Place.Key;
        if (turn.Speaker.IsOwner) lock (gate) ownerName = DiscordPrompts.Name(turn.Speaker.Name);
        var text = turn.Text.Trim();
        // The caller's recent lines may end with this very message.
        var recent = turn.Recent.Where(seen => seen.FromMartlet || seen.Speaker != turn.Speaker.Name || seen.Text.Trim() != text).ToArray();
        var earlier = History.Lines(key, recent);
        var line = History.Add(key, new(turn.Speaker.Name, text, clock.GetUtcNow(), FromMartlet: false));
        if (Gate.Check(turn, earlier, names()) is { } why) return Skip(why);
        var place = places.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        // An ambient turn never queues behind one being answered in the same place: by then the moment has passed.
        if (turn.MayPass)
        {
            if (!await place.WaitAsync(0, token).ConfigureAwait(false)) return Skip("place_busy");
        }
        else await place.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await concurrency.WaitAsync(token).ConfigureAwait(false);
            try
            {
                lock (gate) running++;
                // What was said here while this turn waited is context too.
                earlier = History.Lines(key, recent).Where(seen => !ReferenceEquals(seen, line)).ToList();
                string? owner;
                lock (gate) owner = ownerName;
                var started = clock.GetTimestamp();
                string? raw;
                try { raw = await think(new(turn, earlier, owner), token).ConfigureAwait(false); }
                catch (DiscordReplyException error) when (error.Skipped) { return Skip(error.Code); }
                catch (DiscordReplyException error) { return Fail(error.Code); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (OperationCanceledException) { return Fail("reply.timeout"); }
                catch (Exception) { return Fail("reply.failed"); }
                var latency = (long)clock.GetElapsedTime(started).TotalMilliseconds;
                if (DiscordReplyText.IsPass(raw) || DiscordReplyText.Clean(raw, turn.Source, names()) is not { } reply)
                {
                    lock (gate)
                    {
                        passes++;
                        lastPassAt = clock.GetUtcNow();
                        lastLatencyMs = latency;
                    }
                    Raise();
                    return null;
                }
                History.Add(key, new(names().FirstOrDefault() ?? "Martlet", reply, clock.GetUtcNow(), FromMartlet: true));
                Gate.Spoke(turn);
                lock (gate)
                {
                    replies++;
                    lastReplyAt = clock.GetUtcNow();
                    lastLatencyMs = latency;
                }
                Raise();
                return new(reply);
            }
            finally
            {
                lock (gate) running--;
                concurrency.Release();
            }
        }
        finally { place.Release(); }
    }

    private DiscordReply? Skip(string why)
    {
        lock (gate)
        {
            skipped++;
            lastSkip = why;
        }
        Raise();
        return null;
    }

    private DiscordReply? Fail(string code)
    {
        lock (gate)
        {
            failures++;
            lastError = code;
            lastErrorAt = clock.GetUtcNow();
        }
        Raise();
        return null;
    }

    private void Raise()
    {
        var changed = Changed;
        if (changed is null) return;
        var stats = Stats;
        try { changed(stats); }
        catch (Exception) { /* An observer's problem never fails a reply. */ }
    }
}
