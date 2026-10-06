using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Conversation;

/// <summary>One reminder the user asked for ("remind me to do the dishes in an hour"): what to remind them of and when.</summary>
public sealed record Reminder
{
    public required string Id { get; init; }
    public required string Text { get; init; }
    public required DateTimeOffset Due { get; init; }
    public required DateTimeOffset Set { get; init; }
}

/// <summary>What a computer did about a reminder: offered to say it (<see cref="ReminderMarkKind.Bid"/>, with how long this PC's
/// user has been idle), took it (<see cref="ReminderMarkKind.Claim"/>), said it (<see cref="ReminderMarkKind.Done"/>), canceled
/// it, or let it go because it came too late (<see cref="ReminderMarkKind.Missed"/>).</summary>
public enum ReminderMarkKind { Bid, Claim, Done, Cancel, Missed }

public sealed record ReminderMark
{
    public required string Id { get; init; }
    public required ReminderMarkKind Kind { get; init; }
    public required DateTimeOffset At { get; init; }
    /// <summary>A bid's seconds since someone last used this PC (keyboard, mouse or talking to Martlet).</summary>
    public int? Idle { get; init; }
}

/// <summary>One computer's reminders entry in the shared settings ("reminders.desktop-a"): the reminders set on it and its marks
/// on anyone's. Only that computer writes it, so it never conflicts; every computer reads them all (<see cref="ReminderBoard"/>).</summary>
public sealed record ReminderEntry
{
    public const int MaximumReminders = 64, MaximumMarks = 512;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) }
    };

    public IReadOnlyList<Reminder> Reminders { get; init; } = [];
    public IReadOnlyList<ReminderMark> Marks { get; init; } = [];

    public static ReminderEntry Empty { get; } = new();

    /// <summary>An entry's JSON, or null when it can't be read (a newer Martlet wrote it).</summary>
    public static ReminderEntry? Read(string value)
    {
        try
        {
            var entry = JsonSerializer.Deserialize<ReminderEntry>(value, Json);
            return entry is { Reminders: not null, Marks: not null } &&
                entry.Reminders.All(r => r is { Id.Length: > 0 and <= 16, Text.Length: > 0 and <= Martlet.Conversation.Reminders.MaximumTextCharacters }) &&
                entry.Marks.All(m => m is { Id.Length: > 0 and <= 16 }) ? entry : null;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException) { return null; }
    }

    public string Write() => JsonSerializer.Serialize(this, Json);

    public ReminderEntry With(Reminder reminder) => this with { Reminders = [.. Reminders.Where(r => r.Id != reminder.Id), reminder] };

    /// <summary>This entry with <paramref name="kind"/> on reminder <paramref name="id"/> (replacing an earlier one of that kind).</summary>
    public ReminderEntry Mark(string id, ReminderMarkKind kind, DateTimeOffset at, int? idle = null) => this with
    {
        Marks = [.. Marks.Where(m => m.Id != id || m.Kind != kind), new ReminderMark { Id = id, Kind = kind, At = at.ToUniversalTime(), Idle = idle }]
    };

    /// <summary>Lets go of what nobody needs any more: reminders settled more than <see cref="Reminders.KeepSettled"/> ago, marks
    /// on reminders no computer lists, and bids older than an hour; then the newest within the limits.</summary>
    public ReminderEntry Pruned(ReminderBoard board, DateTimeOffset now)
    {
        bool Gone(Reminder r) => board.Find(r.Id) is { Settled: true } found && now - (found.SettledAt ?? now) > Martlet.Conversation.Reminders.KeepSettled;
        var reminders = Reminders.Where(r => !Gone(r)).OrderByDescending(r => r.Set).Take(MaximumReminders).OrderBy(r => r.Set).ToArray();
        var marks = Marks.Where(m =>
                !(m.Kind == ReminderMarkKind.Bid && now - m.At > TimeSpan.FromHours(1)) &&
                (board.Find(m.Id) is { } found ? !(found.Settled && now - (found.SettledAt ?? now) > Martlet.Conversation.Reminders.KeepSettled)
                    : now - m.At <= Martlet.Conversation.Reminders.KeepSettled))
            .OrderByDescending(m => m.At).Take(MaximumMarks).OrderBy(m => m.At).ToArray();
        return new() { Reminders = reminders, Marks = marks };
    }
}

public enum ReminderState { Pending, Done, Canceled, Missed }

/// <summary>A reminder as all computers see it: who set it, whether it was said, canceled or missed, and by whom.</summary>
public sealed record BoardReminder(Reminder Reminder, string SetOn, ReminderState State, string? SettledBy, DateTimeOffset? SettledAt)
{
    public bool Settled => State != ReminderState.Pending;
}

/// <summary>Every computer's reminders entry joined: the reminders and what each computer did about them.</summary>
public sealed class ReminderBoard
{
    private readonly Dictionary<string, BoardReminder> reminders = new(StringComparer.Ordinal);

    public ReminderBoard(IReadOnlyDictionary<string, ReminderEntry> entries)
    {
        Entries = entries;
        foreach (var (device, entry) in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
            foreach (var reminder in entry.Reminders)
                if (!reminders.ContainsKey(reminder.Id)) reminders[reminder.Id] = Settle(reminder, device);
    }

    public static ReminderBoard Empty { get; } = new(new Dictionary<string, ReminderEntry>());

    /// <summary>Each computer's entry, by device ID.</summary>
    public IReadOnlyDictionary<string, ReminderEntry> Entries { get; }

    public IReadOnlyList<BoardReminder> All => [.. reminders.Values.OrderBy(r => r.Reminder.Due)];

    public IReadOnlyList<BoardReminder> Pending => [.. All.Where(r => !r.Settled)];

    public BoardReminder? Find(string id) => reminders.GetValueOrDefault(id);

    /// <summary>Every computer's marks on reminder <paramref name="id"/>, with who made them.</summary>
    public IReadOnlyList<(ReminderMark Mark, string By)> Marks(string id) =>
        [.. Entries.SelectMany(e => e.Value.Marks.Where(m => m.Id == id).Select(m => (m, e.Key))).OrderBy(m => m.m.At)];

    private BoardReminder Settle(Reminder reminder, string device)
    {
        // Canceling wins over saying it (a cancel is the user's wish), and saying it over letting it go.
        foreach (var kind in new[] { ReminderMarkKind.Cancel, ReminderMarkKind.Done, ReminderMarkKind.Missed })
            if (Marks(reminder.Id).Where(m => m.Mark.Kind == kind).OrderBy(m => m.Mark.At).FirstOrDefault() is { By: not null } found)
                return new(reminder, device, kind switch
                {
                    ReminderMarkKind.Cancel => ReminderState.Canceled,
                    ReminderMarkKind.Done => ReminderState.Done,
                    _ => ReminderState.Missed
                }, found.By, found.Mark.At);
        return new(reminder, device, ReminderState.Pending, null, null);
    }
}

/// <summary>What a computer should do next about a due reminder.</summary>
public enum ReminderStep { Bid, Claim, Deliver, Miss }

public sealed record ReminderDecision(ReminderStep Step, BoardReminder Reminder);

/// <summary>Reminders: the user asks Martlet to remind them of something later ("remind me to do the dishes in an hour") and,
/// when it is due, Martlet brings it up on its own as soon as it is free (or with what the user says next, so it fits in
/// naturally: "nice job on that boss, and hey, the dishes"). Reminders travel with the shared settings, one entry per computer,
/// so they reach all the owner's companion PCs. When several could say one, they settle who does: each running companion PC
/// bids how long its user has been idle, and after <see cref="DecideAfter"/> the one used most recently says it (ties: the
/// lowest device ID); the others stay quiet. One alone says it at once.</summary>
public static class Reminders
{
    public const string ToolName = "reminders";
    public const string KindName = "reminder";
    public const int MaximumTextCharacters = 200;
    public const int MaximumPending = 50;
    /// <summary>How long companion PCs gather bids before the one used most recently says a due reminder.</summary>
    public static TimeSpan DecideAfter => TimeSpan.FromSeconds(6);
    /// <summary>A bid older than this no longer counts (that computer stopped answering); a fresh one is made.</summary>
    public static TimeSpan BidLife => TimeSpan.FromMinutes(2);
    /// <summary>A computer that took a reminder and didn't say it within this long lets the others try again.</summary>
    public static TimeSpan ClaimLife => TimeSpan.FromMinutes(10);
    /// <summary>A reminder more than this late (no companion PC ran when it was due) is let go instead of said.</summary>
    public static TimeSpan LatestLate => TimeSpan.FromHours(12);
    /// <summary>How long a settled reminder (said, canceled or missed) stays in the entries.</summary>
    public static TimeSpan KeepSettled => TimeSpan.FromDays(2);
    public static TimeSpan Longest => TimeSpan.FromDays(366);

    /// <summary>The background job kind that brings a due reminder into the conversation (reminder-1...): always brought up as
    /// soon as Martlet is free, whatever Thinking longer's delivery says.</summary>
    public static BackgroundJobKind Kind { get; } = new(KindName, 8, 60, TimeSpan.FromMinutes(1), Doing: "Reminder", Notice: true);

    public const string Description =
        "Reminders for the user. set: remind them of something later (text, and in_minutes or at); when it's due you'll be " +
        "told and remind them yourself. list: the reminders waiting. cancel: stop one by id. After set, confirm briefly in your " +
        "own words with the time it returns.";

    public const string ParametersJson =
        """{"type":"object","properties":{"action":{"type":"string","enum":["set","list","cancel"]},"text":{"type":"string","description":"set: what to remind them of, short, like \"do the dishes\"."},"in_minutes":{"type":"number","description":"set: minutes from now."},"at":{"type":"string","description":"set, instead of in_minutes: local time like \"17:30\", \"5:30 pm\", \"tomorrow 9:00\" or \"2026-12-24 18:00\"."},"id":{"type":"string","description":"cancel: the id from set or list."}},"required":["action"],"additionalProperties":false}""";

    public static TextToolDefinition Definition { get; } = new(ToolName, Description, ParametersJson);

    // ---------- deciding who says a due reminder ----------

    /// <summary>What <paramref name="me"/> should do about each due reminder on <paramref name="board"/> at <paramref name="now"/>:
    /// let it go when far too late, say it when it took it, take it when it is <paramref name="alone"/> (no other companion PC
    /// could say it) or its bid won after <see cref="DecideAfter"/>, or bid. Nothing while another computer has it.</summary>
    public static IReadOnlyList<ReminderDecision> Decide(ReminderBoard board, string me, DateTimeOffset now, bool alone)
    {
        var decisions = new List<ReminderDecision>();
        foreach (var due in board.Pending.Where(r => r.Reminder.Due <= now))
        {
            var marks = board.Marks(due.Reminder.Id);
            var claims = marks.Where(m => m.Mark.Kind == ReminderMarkKind.Claim && now - m.Mark.At < ClaimLife).ToArray();
            if (claims.Any(c => c.By == me))
            {
                // Two computers that took it at once: the earlier claim (then the lower device ID) says it.
                var first = claims.OrderBy(c => c.Mark.At).ThenBy(c => c.By, StringComparer.Ordinal).First();
                if (first.By == me) decisions.Add(new(ReminderStep.Deliver, due));
                continue;
            }
            if (claims.Length > 0) continue;
            if (now - due.Reminder.Due > LatestLate)
            {
                decisions.Add(new(ReminderStep.Miss, due));
                continue;
            }
            if (alone)
            {
                decisions.Add(new(ReminderStep.Claim, due));
                continue;
            }
            var bids = marks.Where(m => m.Mark.Kind == ReminderMarkKind.Bid && now - m.Mark.At < BidLife && m.Mark.At >= due.Reminder.Due - DecideAfter)
                .ToArray();
            if (bids.FirstOrDefault(b => b.By == me) is not { By: not null } mine)
            {
                decisions.Add(new(ReminderStep.Bid, due));
                continue;
            }
            if (now - mine.Mark.At < DecideAfter) continue;
            var winner = bids.OrderBy(b => b.Mark.Idle ?? int.MaxValue).ThenBy(b => b.By, StringComparer.Ordinal).First();
            if (winner.By == me) decisions.Add(new(ReminderStep.Claim, due));
        }
        return decisions;
    }

    // ---------- the reminders tool ----------

    /// <summary>What one reminders call did: the answer for the model, a short outcome for the tool log (never the reminder's
    /// text) and this computer's entry when it changed.</summary>
    public sealed record ToolOutcome(ConversationToolResult Result, string Outcome, ReminderEntry? Own);

    /// <summary>Runs one reminders call against <paramref name="board"/> (every computer's entries) for <paramref name="me"/>,
    /// whose entry is <paramref name="own"/>, at <paramref name="now"/> in <paramref name="zone"/>.</summary>
    public static ToolOutcome Run(string argumentsJson, ReminderBoard board, string me, ReminderEntry own, DateTimeOffset now,
        TimeZoneInfo zone, Func<string>? newId = null)
    {
        JsonObject? arguments;
        try { arguments = JsonNode.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson) as JsonObject; }
        catch (JsonException) { arguments = null; }
        if (arguments is null || Text(arguments, "action")?.ToLowerInvariant() is not { } action)
            return Refused("Pass one JSON object with an action, like {\"action\": \"set\", \"text\": \"do the dishes\", \"in_minutes\": 60}.");
        var clock = $"It's {When(now, now, zone)} now.";
        switch (action)
        {
            case "set":
            {
                var text = Text(arguments, "text")?.Trim('"', '\u201C', '\u201D').Trim();
                if (string.IsNullOrEmpty(text) || text.Length > MaximumTextCharacters || text.Any(char.IsControl))
                    return Refused($"Pass text: what to remind them of, up to {MaximumTextCharacters} characters.");
                var (due, problem) = DueTime(arguments, now, zone);
                if (due is null) return Refused(problem! + " " + clock);
                if (board.Pending.Count >= MaximumPending)
                    return Refused($"There are already {MaximumPending} reminders waiting. Tell the user to cancel some first.");
                var id = (newId ?? NewId)();
                var reminder = new Reminder { Id = id, Text = text, Due = due.Value.ToUniversalTime(), Set = now.ToUniversalTime() };
                return new(new($"Reminder {id} set for {When(due.Value, now, zone)} ({Span(due.Value - now)} from now): {text}. {clock} " +
                    "Confirm it briefly."), "set", own.With(reminder).Pruned(board, now));
            }
            case "list":
            {
                var listed = board.Pending.Take(MaximumPending).Select(r => new
                {
                    id = r.Reminder.Id, text = r.Reminder.Text, due = When(r.Reminder.Due, now, zone),
                    @in = r.Reminder.Due <= now ? "due now" : Span(r.Reminder.Due - now)
                }).ToArray();
                return new(new(JsonSerializer.Serialize(new { now = When(now, now, zone), reminders = listed }) +
                    (listed.Length == 0 ? "\nNo reminders are waiting." : "")), $"listed {listed.Length}", null);
            }
            case "cancel":
            {
                var key = (Text(arguments, "id") ?? Text(arguments, "text"))?.Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(key)) return Refused("Pass the id of the reminder to cancel, from set or list.");
                var found = board.Find(key) ?? board.Pending.SingleOrDefault(r => r.Reminder.Text.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (found is null) return Refused($"There is no reminder {Clip(key)}. Call list for the ids; don't guess.");
                if (found.Settled)
                    return new(new($"Reminder {found.Reminder.Id} was already {found.State.ToString().ToLowerInvariant()}."), "already settled", null);
                return new(new($"Canceled reminder {found.Reminder.Id}: {found.Reminder.Text}."), "canceled",
                    own.Mark(found.Reminder.Id, ReminderMarkKind.Cancel, now).Pruned(board, now));
            }
            default:
                return Refused("action must be set, list or cancel.");
        }
    }

    /// <summary>When a set call wants the reminder: in_minutes from now, or at a local time (the next time it comes when no day
    /// is given).</summary>
    public static (DateTimeOffset? Due, string? Problem) DueTime(JsonObject arguments, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (arguments["in_minutes"] is JsonValue minutesValue)
        {
            double minutes;
            if (minutesValue.TryGetValue<double>(out var number)) minutes = number;
            else if (minutesValue.TryGetValue<string>(out var written) &&
                double.TryParse(written, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) minutes = parsed;
            else return (null, "in_minutes must be a number.");
            if (double.IsNaN(minutes) || minutes < 0.1 || minutes > Longest.TotalMinutes)
                return (null, $"in_minutes must be between 0.1 and {(int)Longest.TotalMinutes}.");
            return (now + TimeSpan.FromMinutes(minutes), null);
        }
        if (Text(arguments, "at") is not { } at) return (null, "Pass in_minutes or at.");
        return At(at, now, zone);
    }

    private static readonly string[] DateFormats =
        ["yyyy-MM-dd H:mm", "yyyy-MM-dd HH:mm", "yyyy-MM-ddTH:mm", "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd h:mmtt", "yyyy-MM-dd htt"];
    private static readonly string[] TimeFormats = ["H:mm", "HH:mm", "H:mm:ss", "h:mmtt", "htt", "h:mm:sstt", "H.mm"];

    /// <summary>A local time the model wrote ("17:30", "5:30 pm", "tomorrow 9:00", "2026-12-24 18:00") as the next such moment.</summary>
    public static (DateTimeOffset? Due, string? Problem) At(string text, DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var written = text.Trim().ToLowerInvariant().Replace("a.m.", "am").Replace("p.m.", "pm").Replace("noon", "12:00pm")
            .Replace("midnight", "12:00am");
        int? day = null;
        foreach (var (word, offset) in new[] { ("today", 0), ("tonight", 0), ("tomorrow", 1) })
            if (written.StartsWith(word, StringComparison.Ordinal))
            {
                day = offset;
                written = written[word.Length..].Trim().TrimStart(',').Replace("at ", "").Trim();
                break;
            }
        written = written.Replace("at ", "").Trim();
        DateTime wanted;
        var compact = written.Replace(" am", "am").Replace(" pm", "pm").ToUpperInvariant();
        if (day is null && DateTime.TryParseExact(compact, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dated))
            wanted = dated;
        else if (DateTime.TryParseExact(compact, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            wanted = local.Date.AddDays(day ?? 0) + time.TimeOfDay;
            if (wanted <= local.DateTime)
            {
                if (day is not null) return (null, $"{text.Trim()} has already passed.");
                wanted = wanted.AddDays(1);
            }
        }
        else return (null, $"Couldn't read the time \"{Clip(text)}\". Use in_minutes, or at like \"17:30\", \"tomorrow 9:00\" or \"2026-12-24 18:00\".");
        DateTimeOffset due;
        try { due = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(wanted, DateTimeKind.Unspecified), zone), TimeSpan.Zero); }
        catch (ArgumentException) { return (null, $"{Clip(text)} doesn't exist on this day (the clocks change)."); }
        if (due <= now) return (null, $"{text.Trim()} has already passed.");
        if (due - now > Longest) return (null, $"Reminders go at most {(int)Longest.TotalDays} days ahead.");
        return (due, null);
    }

    // ---------- bringing a due reminder up ----------

    /// <summary>What the conversation is told about a due reminder: what to remind the user of, when they asked for it, and how
    /// late it is when Martlet couldn't say it on time.</summary>
    public static string Due(Reminder reminder, DateTimeOffset now, TimeZoneInfo zone)
    {
        var late = now - reminder.Due;
        return $"{reminder.Text} (they asked for it at {When(reminder.Set, now, zone)}, for {When(reminder.Due, now, zone)}" +
            (late > TimeSpan.FromMinutes(2) ? $"; it's {Span(late)} late because Martlet wasn't running then" : "") + ")";
    }

    /// <summary>A time as the model reads it: "4:12 PM" today, "Tue 4:12 PM" within a week, "Dec 24 6:00 PM" otherwise.</summary>
    public static string When(DateTimeOffset at, DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone);
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        var format = local.Date == today ? "h:mm tt" : Math.Abs((local.Date - today).TotalDays) < 7 ? "ddd h:mm tt" : "MMM d h:mm tt";
        return local.ToString(format, CultureInfo.InvariantCulture);
    }

    /// <summary>"45 s", "12 min", "1 h 5 min", "3 days 2 h".</summary>
    public static string Span(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalSeconds < 60) return $"{(int)Math.Round(span.TotalSeconds)} s";
        var minutes = (int)Math.Round(span.TotalMinutes);
        if (minutes < 60) return $"{minutes} min";
        if (span.TotalDays < 1) return minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes / 60} h {minutes % 60} min";
        return span.Hours == 0 ? $"{span.Days} day{(span.Days == 1 ? "" : "s")}" : $"{span.Days} day{(span.Days == 1 ? "" : "s")} {span.Hours} h";
    }

    /// <summary>A new reminder ID: six lowercase hex digits.</summary>
    public static string NewId() => Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(3));

    private static string Clip(string text) => text.Length > 40 ? text[..40] + "…" : text;

    private static string? Text(JsonObject arguments, string name) =>
        arguments[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    private static ToolOutcome Refused(string text) => new(new(text, true), "refused", null);
}
