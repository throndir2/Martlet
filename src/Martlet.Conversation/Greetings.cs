using System.Globalization;
using Martlet.Core.Settings;

namespace Martlet.Conversation;

/// <summary>One app the user has open on this PC: its name ("Google Chrome") and whether its window is in front.</summary>
public sealed record GreetingApp(string Name, bool Front);

/// <summary>What Martlet knows when it greets the user after it starts (docs/CONVERSATION.md#greeting-when-martlet-starts): the
/// time, the apps open on this PC, the end of the last conversation on this PC (null: Martlet doesn't keep or can't read the
/// record; empty: there is none yet), the reminders coming up and the character's name. It is private: it goes only to the
/// Thinking model with the greeting, never to logs or status files.</summary>
public sealed record GreetingFacts
{
    public required DateTimeOffset Now { get; init; }
    public TimeZoneInfo Zone { get; init; } = TimeZoneInfo.Local;
    public IReadOnlyList<GreetingApp> Apps { get; init; } = [];
    public IReadOnlyList<HistoryExchange>? LastConversation { get; init; }
    public IReadOnlyList<Reminder> Reminders { get; init; } = [];
    /// <summary>The name of the persona Martlet uses, or null (Martlet).</summary>
    public string? Companion { get; init; }
}

/// <summary>The greeting Martlet says on its own once it has warmed up after it starts: a notice job (<see cref="Kind"/>) whose
/// result is the facts (<see cref="Facts"/>), brought up as soon as Martlet is free with Companion › Prompts › Greeting, or in
/// the notes of the user's first message with Greeting, with your message. Once per start of Martlet; Settings › Startup and
/// closing turns it off.</summary>
public static class Greetings
{
    public const string KindName = "greeting";
    /// <summary>At most this many apps, the one in front first.</summary>
    public const int MaximumApps = 8;
    /// <summary>At most this many exchanges from the end of the last conversation.</summary>
    public const int MaximumExchanges = 3;
    public const int ExchangeCharacters = 200;
    public const int MaximumReminders = 5;

    /// <summary>Reminders due within this time count as coming up.</summary>
    public static TimeSpan RemindersAhead => TimeSpan.FromHours(12);

    /// <summary>The background job kind of the greeting (greeting-1): a notice with its own prompts.</summary>
    public static BackgroundJobKind Kind { get; } = new(KindName, 1, 6, TimeSpan.FromMinutes(1), Doing: "Greeting", Notice: true)
    {
        Wording = new(PromptCatalog.Greeting, PromptCatalog.GreetingNotes, "facts")
    };

    /// <summary>The facts as the greeting's note reads them, one per line; <see cref="BackgroundJobs.Notices"/> puts "- " before
    /// the first.</summary>
    public static string Facts(GreetingFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var zone = facts.Zone;
        var now = facts.Now;
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var lines = new List<string>
        {
            $"It's {local.ToString("dddd, MMMM d, h:mm tt", CultureInfo.InvariantCulture)}: {PartOfDay(local.Hour)}."
        };
        if (facts.LastConversation is { } last)
        {
            if (last.Count == 0) lines.Add("There is no earlier conversation with the user on record: this may be your first.");
            else
            {
                var ended = last[^1].At;
                lines.Add($"You last talked {Reminders.Span(now - ended)} ago. The end of that conversation:\n" + string.Join("\n",
                    last.Select(e => "  " + PastConversations.Line(e, zone, ExchangeCharacters, ExchangeCharacters, facts.Companion))));
            }
        }
        var coming = Coming(facts.Reminders, now);
        if (coming.Count > 0)
            lines.Add("Reminders coming up: " + string.Join("; ", coming.Select(r =>
                $"\"{r.Text}\" at {Reminders.When(r.Due, now, zone)} (in {Reminders.Span(r.Due - now)})")) + ".");
        var apps = Apps(facts.Apps);
        if (apps.Count > 0)
            lines.Add("Apps open on this PC: " + Join([.. apps.Select(a => a.Front ? a.Name + " (in front)" : a.Name)]) + ".");
        return string.Join("\n- ", lines);
    }

    /// <summary>What the greeting knew, in counts only, for the desktop log and status: "the time of day, your last conversation, 1
    /// reminder coming up and 4 open apps".</summary>
    public static string Describe(GreetingFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var parts = new List<string> { "the time of day" };
        if (facts.LastConversation is { Count: > 0 }) parts.Add("your last conversation");
        else if (facts.LastConversation is not null) parts.Add("that you haven't talked before");
        var coming = Coming(facts.Reminders, facts.Now).Count;
        if (coming > 0) parts.Add(coming == 1 ? "1 reminder coming up" : $"{coming} reminders coming up");
        var apps = Apps(facts.Apps).Count;
        if (apps > 0) parts.Add(apps == 1 ? "1 open app" : $"{apps} open apps");
        return Join(parts);
    }

    /// <summary>The end of the newest conversation on this PC (messaging apps left out), at most <paramref name="count"/>
    /// exchanges, oldest first; empty when there is none.</summary>
    public static IReadOnlyList<HistoryExchange> LastConversation(ConversationHistory history, int count = MaximumExchanges)
    {
        ArgumentNullException.ThrowIfNull(history);
        var newest = history.Conversations(50).FirstOrDefault(c => c.Apps is null || c.Apps.Contains(HistoryApps.Pc));
        if (newest is null) return [];
        var exchanges = history.Exchanges(newest.Id).Where(e => e.App == HistoryApps.Pc).ToArray();
        return exchanges[Math.Max(0, exchanges.Length - count)..];
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
