using System.Globalization;
using Martlet.Core.Settings;

namespace Martlet.Conversation;

/// <summary>One app the user has open on this PC: its name ("Google Chrome") and whether its window is in front.</summary>
public sealed record GreetingApp(string Name, bool Front);

/// <summary>What Martlet knows when it greets the user after it starts (docs/CONVERSATION.md#greeting-when-martlet-starts): the
/// time, the user's name (only one that looks like a real name, <see cref="Greetings.PersonName"/>), the end of the last
/// conversation on this PC (null: Martlet doesn't keep or can't read the record; empty: there is none yet) and of a newer chat
/// in a messaging app, the reminders that passed while Martlet wasn't running and those coming up, the Martlet version it ran
/// before an update, the apps open on this PC and the character's name. It is private: it goes only to the Thinking model with
/// the greeting, never to logs or status files.</summary>
public sealed record GreetingFacts
{
    public required DateTimeOffset Now { get; init; }
    public TimeZoneInfo Zone { get; init; } = TimeZoneInfo.Local;
    public string? UserName { get; init; }
    public IReadOnlyList<GreetingApp> Apps { get; init; } = [];
    public IReadOnlyList<HistoryExchange>? LastConversation { get; init; }
    /// <summary>The end of the newest Telegram or WhatsApp chat when it ended after <see cref="LastConversation"/>; empty: none.</summary>
    public IReadOnlyList<HistoryExchange> LastMessaging { get; init; } = [];
    public IReadOnlyList<Reminder> Reminders { get; init; } = [];
    /// <summary>Reminders that came and went while no companion PC ran (too late to say), since Martlet last ran here.</summary>
    public IReadOnlyList<Reminder> Missed { get; init; } = [];
    /// <summary>The Martlet version that ran here before, when it was updated since; null otherwise.</summary>
    public string? UpdatedFrom { get; init; }
    public string? Version { get; init; }
    /// <summary>The name of the persona Martlet uses, or null (Martlet).</summary>
    public string? Companion { get; init; }
}

/// <summary>The greeting Martlet says on its own once it has warmed up after it starts: a notice job (<see cref="Kind"/>) whose
/// result is the facts (<see cref="Facts"/>), brought up as soon as Martlet is free with Companion › Prompts › Greeting, or in
/// the notes of the user's first message with Greeting, with your message. The character greets only the way it would, or
/// stays quiet ([pass]). Once per start of Martlet; Settings › Startup and closing turns it off.</summary>
public static class Greetings
{
    public const string KindName = "greeting";
    /// <summary>At most this many apps, the one in front first.</summary>
    public const int MaximumApps = 8;
    /// <summary>At most this many exchanges from the end of the last conversation.</summary>
    public const int MaximumExchanges = 3;
    /// <summary>At most this many exchanges from the end of a newer messaging-app chat.</summary>
    public const int MaximumMessagingExchanges = 2;
    public const int ExchangeCharacters = 200;
    public const int MaximumReminders = 5;
    public const int MaximumMissed = 3;

    /// <summary>Reminders due within this time count as coming up.</summary>
    public static TimeSpan RemindersAhead => TimeSpan.FromHours(12);

    /// <summary>The background job kind of the greeting (greeting-1): a notice with its own prompts, which may stay quiet.</summary>
    public static BackgroundJobKind Kind { get; } = new(KindName, 1, 6, TimeSpan.FromMinutes(1), Doing: "Greeting", Notice: true)
    {
        Wording = new(PromptCatalog.Greeting, PromptCatalog.GreetingNotes, "facts") { Silent = true }
    };

    /// <summary>The facts as the greeting's note reads them, one per line; <see cref="BackgroundJobs.Notices"/> puts "- " before
    /// the first.</summary>
    public static string Facts(GreetingFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var zone = facts.Zone;
        var now = facts.Now;
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var weekend = local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        var lines = new List<string>
        {
            $"It's {local.ToString("dddd, MMMM d, h:mm tt", CultureInfo.InvariantCulture)}: {PartOfDay(local.Hour)}" +
            (weekend ? ", on the weekend." : ".") + (SpecialDay(local) is { } day ? $" It's {day}." : "")
        };
        if (facts.UserName is { } name) lines.Add($"The user's name is {name}.");
        var talkedHere = facts.LastConversation is { Count: > 0 };
        if (facts.LastConversation is { } last)
        {
            if (last.Count == 0 && facts.LastMessaging.Count == 0)
                lines.Add("There is no earlier conversation with the user on record: this may be your first.");
            else if (last.Count > 0)
                lines.Add($"You last talked on this PC {Reminders.Span(now - last[^1].At)} ago. The end of that conversation:\n" +
                    Lines(last, zone, facts.Companion));
        }
        if (facts.LastMessaging is [.., var newest] chat)
            lines.Add((talkedHere ? "Since then, you also talked on " : "You last talked on ") + HistoryApps.Name(newest.App) +
                $" {Reminders.Span(now - newest.At)} ago. The end of that chat:\n" + Lines(chat, zone, facts.Companion));
        if (facts.Missed.Count > 0)
            lines.Add("Reminders that passed while Martlet wasn't running, so you couldn't remind them: " + string.Join("; ",
                facts.Missed.Take(MaximumMissed).Select(r => $"\"{r.Text}\" (for {Reminders.When(r.Due, now, zone)})")) + ".");
        var coming = Coming(facts.Reminders, now);
        if (coming.Count > 0)
            lines.Add("Reminders coming up: " + string.Join("; ", coming.Select(r =>
                $"\"{r.Text}\" at {Reminders.When(r.Due, now, zone)} (in {Reminders.Span(r.Due - now)})")) + ".");
        if (facts.UpdatedFrom is { } from && facts.Version is { } version)
            lines.Add($"Martlet, the app you run in, was updated from {from} to {version} since it last ran here.");
        var apps = Apps(facts.Apps);
        if (apps.Count > 0)
            lines.Add("Apps open on this PC: " + Join([.. apps.Select(a => a.Front ? a.Name + " (in front)" : a.Name)]) + ".");
        return string.Join("\n- ", lines);
    }

    /// <summary>What the greeting knew, in counts only, for the desktop log and status: "the time of day, your name, your last
    /// conversation, 1 reminder coming up and 4 open apps". Never the name, a reminder, an app or what was said.</summary>
    public static string Describe(GreetingFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var parts = new List<string> { "the time of day" };
        if (SpecialDay(TimeZoneInfo.ConvertTime(facts.Now, facts.Zone)) is not null) parts.Add("a special day");
        if (facts.UserName is not null) parts.Add("your name");
        if (facts.LastConversation is { Count: > 0 }) parts.Add("your last conversation");
        else if (facts.LastConversation is not null && facts.LastMessaging.Count == 0) parts.Add("that you haven't talked before");
        if (facts.LastMessaging.Count > 0) parts.Add("your last chat in a messaging app");
        var missed = Math.Min(facts.Missed.Count, MaximumMissed);
        if (missed > 0) parts.Add(missed == 1 ? "1 missed reminder" : $"{missed} missed reminders");
        var coming = Coming(facts.Reminders, facts.Now).Count;
        if (coming > 0) parts.Add(coming == 1 ? "1 reminder coming up" : $"{coming} reminders coming up");
        if (facts.UpdatedFrom is not null && facts.Version is not null) parts.Add("the update");
        var apps = Apps(facts.Apps).Count;
        if (apps > 0) parts.Add(apps == 1 ? "1 open app" : $"{apps} open apps");
        return Join(parts);
    }

    /// <summary>The end of the newest conversation on this PC (messaging apps left out), at most <paramref name="count"/>
    /// exchanges, oldest first; empty when there is none.</summary>
    public static IReadOnlyList<HistoryExchange> LastConversation(ConversationHistory history, int count = MaximumExchanges)
    {
        ArgumentNullException.ThrowIfNull(history);
        return Newest(history, app => app == HistoryApps.Pc, count);
    }

    /// <summary>The end of the newest Telegram or WhatsApp chat (Discord is a group, so it is left out), at most
    /// <paramref name="count"/> exchanges, oldest first, when it ended after <paramref name="after"/>; empty otherwise.</summary>
    public static IReadOnlyList<HistoryExchange> LastMessaging(ConversationHistory history, DateTimeOffset? after,
        int count = MaximumMessagingExchanges)
    {
        ArgumentNullException.ThrowIfNull(history);
        var chat = Newest(history, app => app is HistoryApps.Telegram or HistoryApps.WhatsApp, count);
        return chat.Count > 0 && (after is null || chat[^1].At > after) ? chat : [];
    }

    /// <summary>The user's name as Martlet may call them, from a name an account, a voice or Windows gives: the first name of
    /// something that looks like a person's name ("Sam Rivera" gives "Sam"), or null for what looks like a user name, a handle
    /// or a placeholder ("mozar", "xXDarkLordXx", "gamer_42", "sam@example.com", "Admin", "DEFAULTUSER"). Names in a script
    /// without upper and lower case (Chinese, Japanese, Korean...) are used as written.</summary>
    public static string? PersonName(string? text)
    {
        var name = text?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 40) return null;
        // Digits, underscores, @, dots in the middle of a word and other symbols belong to user names and handles.
        if (name.Any(c => !(char.IsLetter(c) || c is ' ' or '-' or '\'' or '\u2019' or '.'))) return null;
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 4 || words.Any(w => NotNames.Contains(w.TrimEnd('.')))) return null;
        var first = words[0];
        if (first.Contains('.') || first.Count(char.IsLetter) < 2) return null;
        if (!name.Any(c => char.IsUpper(c) || char.IsLower(c))) return words.Length == 1 && name.Length <= 12 ? name : null;
        if (!char.IsUpper(first[0]) || first.Count(char.IsLetter) > 1 && first.All(c => !char.IsLower(c))) return null;
        if (words.Skip(1).Any(w => char.IsLower(w[0]) && !Particles.Contains(w))) return null;
        // An upper-case letter after a lower-case one ("DarkLord") is a handle, except after a name prefix ("McKenzie", "DeShawn").
        var humps = Enumerable.Range(1, first.Length - 1).Where(i => char.IsUpper(first[i]) && char.IsLower(first[i - 1])).ToArray();
        if (humps.Length > 1 || humps is [var hump] && !NamePrefixes.Contains(first[..hump])) return null;
        return first.Length > 20 ? null : first;
    }

    /// <summary>"morning", "afternoon", "evening", "night" or "late at night" for an hour of the day (0-23).</summary>
    public static string PartOfDay(int hour) => hour switch
    {
        >= 5 and < 12 => "morning",
        >= 12 and < 17 => "afternoon",
        >= 17 and < 21 => "evening",
        >= 21 => "night",
        _ => "late at night"
    };

    /// <summary>A day many people mark on <paramref name="day"/> ("Halloween"), or null.</summary>
    public static string? SpecialDay(DateTimeOffset day) => (day.Month, day.Day) switch
    {
        (1, 1) => "New Year's Day",
        (2, 14) => "Valentine's Day",
        (10, 31) => "Halloween",
        (12, 24) => "Christmas Eve",
        (12, 25) => "Christmas Day",
        (12, 31) => "New Year's Eve",
        _ => null
    };

    private static readonly HashSet<string> NotNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "user", "users", "admin", "administrator", "owner", "guest", "default", "defaultuser", "me", "you", "pc", "home", "family",
        "test", "tester", "player", "gamer", "dev", "developer", "root", "desktop", "laptop", "computer", "windows", "microsoft",
        "martlet", "local", "account", "main", "office", "work", "school", "student", "public", "kids", "voice", "unknown", "anonymous"
    };

    private static readonly HashSet<string> Particles = new(StringComparer.Ordinal)
    {
        "van", "von", "de", "da", "di", "del", "della", "der", "den", "du", "la", "le", "bin", "bint", "al", "el", "y", "e", "dos", "das"
    };

    private static readonly HashSet<string> NamePrefixes = new(StringComparer.Ordinal) { "Mc", "Mac", "De", "Di", "Da", "La", "Le", "Fitz" };

    private static IReadOnlyList<HistoryExchange> Newest(ConversationHistory history, Func<string, bool> app, int count)
    {
        var newest = history.Conversations(50).FirstOrDefault(c => c.Apps is null ? app(HistoryApps.Pc) : c.Apps.Any(app));
        if (newest is null) return [];
        var exchanges = history.Exchanges(newest.Id).Where(e => app(e.App)).ToArray();
        return exchanges[Math.Max(0, exchanges.Length - count)..];
    }

    private static string Lines(IReadOnlyList<HistoryExchange> exchanges, TimeZoneInfo zone, string? companion) =>
        string.Join("\n", exchanges.Select(e => "  " + PastConversations.Line(e, zone, ExchangeCharacters, ExchangeCharacters, companion)));

    private static IReadOnlyList<Reminder> Coming(IReadOnlyList<Reminder> reminders, DateTimeOffset now) =>
        [.. reminders.Where(r => r.Due > now && r.Due - now <= RemindersAhead).OrderBy(r => r.Due).Take(MaximumReminders)];

    private static IReadOnlyList<GreetingApp> Apps(IReadOnlyList<GreetingApp> apps) =>
        [.. apps.Where(a => !string.IsNullOrWhiteSpace(a.Name)).DistinctBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(a => a.Front).Take(MaximumApps)];

    private static string Join(IReadOnlyList<string> parts) => parts.Count switch
    {
        0 => "",
        1 => parts[0],
        _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1]
    };
}
