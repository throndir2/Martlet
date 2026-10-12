using System.IO;
using Martlet.Conversation;

namespace Martlet.Mcp;

/// <summary>greeting_check: Martlet's greeting once it has warmed up after it starts (docs/CONVERSATION.md#greeting-when-martlet-starts).
/// It rehearses the production code with fixture facts: the time of day, the end of the newest conversation on this PC read from
/// a real conversation record in a temporary folder (a newer messaging-app chat left out), the reminders coming up (later and
/// past ones left out), the apps open with the one in front first, the counts-only line for the log and status, and the
/// greeting notice through <see cref="BackgroundJobs"/>: on its own as soon as Martlet is free, or in the notes of the user's
/// first message, and let go after waiting too long. No model, network or credential is used.</summary>
internal static class GreetingCheck
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    internal static async Task<object> RunAsync(CancellationToken cancellation)
    {
        var zone = TimeZoneInfo.Utc;
        var now = new DateTimeOffset(2026, 10, 11, 17, 8, 0, TimeSpan.Zero);
        var steps = new List<object>();
        var ok = true;
        void Step(string name, bool passed, object? detail = null)
        {
            ok &= passed;
            steps.Add(new { name, passed, detail });
        }

        // The conversation record: yesterday's talk on this PC, then a newer Telegram chat that the greeting leaves out.
        var folder = Path.Combine(Path.GetTempPath(), "martlet-greeting-check-" + Guid.NewGuid().ToString("N"));
        IReadOnlyList<HistoryExchange> last;
        IReadOnlyList<HistoryExchange> none;
        try
        {
            var clock = new Clock(now.AddHours(-18));
            var history = new ConversationHistory(folder, clock);
            await history.LoadAsync(cancellation);
            none = Greetings.LastConversation(history);
            var pc = Guid.NewGuid();
            await history.AppendAsync(pc, HistoryInputKind.Spoken, "I finally beat the second boss!", "No way, that's huge! (FIXTURE)", null, cancellation);
            clock.Now = clock.Now.AddMinutes(5);
            await history.AppendAsync(pc, HistoryInputKind.Spoken, "Going to bed, night!", "Sleep well, champ! (FIXTURE)", null, cancellation);
            clock.Now = now.AddHours(-2);
            await history.AppendAsync(Guid.NewGuid(), HistoryInputKind.Typed, "from my phone", "hi from Telegram (FIXTURE)", null,
                new HistorySource(HistoryApps.Telegram, "chat-1"), null, cancellation);
            last = Greetings.LastConversation(history);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        Step("no earlier conversation: none", none.Count == 0);
        Step("the newest conversation on this PC", last.Count == 2 && last[^1].User == "Going to bed, night!" &&
            last.All(e => e.App == HistoryApps.Pc), last.Select(e => e.User));

        var facts = new GreetingFacts
        {
            Now = now, Zone = zone, Companion = "Ivy", LastConversation = last,
            Apps = [new("Discord", false), new("Google Chrome", true), new("Steam", false), new("discord", false)],
            Reminders =
            [
                new() { Id = "r1", Text = "call mom", Due = now.AddMinutes(52), Set = now.AddHours(-3) },
                new() { Id = "r2", Text = "dentist", Due = now.AddDays(2), Set = now.AddHours(-3) },
                new() { Id = "r3", Text = "already due", Due = now.AddMinutes(-5), Set = now.AddHours(-3) }
            ]
        };
        var text = Greetings.Facts(facts);
        Step("the time of day", text.StartsWith("It's Sunday, October 11, 5:08 PM: evening.", StringComparison.Ordinal), text);
        Step("the end of the last conversation", text.Contains("You last talked 17 h 55 min ago", StringComparison.Ordinal) &&
            text.Contains("Going to bed, night!", StringComparison.Ordinal) && text.Contains("Ivy:", StringComparison.Ordinal) &&
            !text.Contains("Telegram", StringComparison.Ordinal));
        Step("only the reminders coming up", text.Contains("\"call mom\" at 6:00 PM (in 52 min)", StringComparison.Ordinal) &&
            !text.Contains("dentist", StringComparison.Ordinal) && !text.Contains("already due", StringComparison.Ordinal));
        Step("the apps, the one in front first, each once", text.Contains("Apps open on this PC: Google Chrome (in front), Discord and Steam.",
            StringComparison.Ordinal));
        var knew = Greetings.Describe(facts);
        Step("the log and status line has counts only", knew == "the time of day, your last conversation, 1 reminder coming up and 3 open apps" &&
            !knew.Contains("Chrome", StringComparison.Ordinal), knew);
        Step("the first conversation", Greetings.Facts(facts with { LastConversation = [] }).Contains("this may be your first", StringComparison.Ordinal));
        Step("parts of the day", Greetings.PartOfDay(8) == "morning" && Greetings.PartOfDay(13) == "afternoon" &&
            Greetings.PartOfDay(22) == "night" && Greetings.PartOfDay(2) == "late at night");

        using var jobs = new BackgroundJobs();
        var job = jobs.Start(Greetings.Kind, "Greeting", (_, _) => Task.FromResult(BackgroundJobOutcome.Done(text))).Job!;
        for (var i = 0; i < 300 && !job.Finished; i++) await Task.Delay(10, cancellation);
        var notes = BackgroundJobs.ReportNotes(null, jobs.Undelivered);
        var delivery = jobs.Take(onItsOwn: true, noticesOnly: true);
        var message = delivery is null ? null : BackgroundJobs.ReportMessage(null, delivery.Jobs).UserText;
        delivery?.Complete();
        Step("said on its own as soon as Martlet is free", message is not null &&
            message.StartsWith("(Martlet's note, not said by the user: Martlet just started", StringComparison.Ordinal) &&
            message.Contains("- It's Sunday", StringComparison.Ordinal), message);
        Step("or with the user's first message", notes is not null && notes.Contains("Answer what the user just said first", StringComparison.Ordinal), notes);
        Step("said once", job.Delivery == BackgroundDeliveryState.Delivered && !jobs.HasNotice);
        var check = UnpromptedSpeech.Recheck(job, job.FinishedUtc!.Value + TimeSpan.FromMinutes(11), null);
        Step("let go after waiting too long", check.Action == NoticeAction.Drop && check.Code == "too_old", check.Why);

        return new { passed = ok, steps };
    }
}
