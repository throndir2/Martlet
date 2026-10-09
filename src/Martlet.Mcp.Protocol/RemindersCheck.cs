using System.IO;
using System.Text.Json.Nodes;
using Martlet.Conversation;
using Martlet.Core.Sync;

namespace Martlet.Mcp;

/// <summary>reminders_status and reminders_check: Martlet's reminders (the reminders tool; docs/CONVERSATION.md#reminders). The
/// status reads a data directory's shared-settings.json: every computer's reminders entry, each reminder's state and what each
/// computer did about it (offered, took, said, canceled, let go). The check rehearses the production code end to end on two
/// simulated companion PCs whose entries merge through <see cref="SharedSettings"/>: setting one with the tool, the other
/// listing and canceling, who says a due one (the PC used most recently, after both offered), a PC alone saying it at once,
/// one far too late let go, the read-only list_reminders the reply gets while a check-in takes the tool over, and the
/// conversation's wording through <see cref="BackgroundJobs"/>: on its own as soon as Martlet
/// is free, or in the notes of the next message. No model, network or credential is used.</summary>
internal static class RemindersCheck
{
    internal static Task<object> StatusAsync(string dataDirectory, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!File.Exists(Path.Combine(dataDirectory, SharedSettingsState.FileName)))
            return Task.FromResult<object>(new { state = "none", why = "No shared-settings.json in this data directory yet.", handOff = HandOff() });
        var (document, _) = SharedSettingsState.Load(dataDirectory);
        var entries = new Dictionary<string, ReminderEntry>(StringComparer.Ordinal);
        var unreadable = new List<string>();
        foreach (var setting in document.Settings.Where(s => s.Key.StartsWith(SharedSettings.RemindersPrefix, StringComparison.Ordinal)))
            if (ReminderEntry.Read(setting.Value) is { } entry) entries[setting.UpdatedBy] = entry;
            else unreadable.Add(setting.Key);
        var board = new ReminderBoard(entries);
        var now = DateTimeOffset.UtcNow;
        return Task.FromResult<object>(new
        {
            state = "loaded",
            computers = entries.Keys.ToArray(),
            unreadable,
            pending = board.Pending.Count,
            reminders = board.All.Select(r => new
            {
                id = r.Reminder.Id, text = r.Reminder.Text, due = r.Reminder.Due, set = r.Reminder.Set, setOn = r.SetOn,
                state = r.State.ToString(), settledBy = r.SettledBy, settledAt = r.SettledAt,
                dueIn = r.Settled ? null : r.Reminder.Due > now ? Reminders.Span(r.Reminder.Due - now) : "due now",
                marks = board.Marks(r.Reminder.Id).Select(m => new { kind = m.Mark.Kind.ToString(), by = m.By, at = m.Mark.At, idleSeconds = m.Mark.Idle })
            }).ToArray(),
            tool = new { name = Reminders.ToolName, description = Reminders.Description, parameters = JsonNode.Parse(Reminders.ParametersJson) },
            handOff = HandOff()
        });
    }

    /// <summary>The check-in tool set that takes the reminders tool over and the read-only tool the reply gets instead.</summary>
    private static object HandOff() => new
    {
        checkInToolSet = CheckInToolSets.ReminderSet.Id, replaces = CheckInToolSets.ReminderSet.Replaces,
        replyTool = new
        {
            name = Reminders.ListToolName, description = Reminders.ListDescription,
            parameters = JsonNode.Parse(Reminders.ListDefinition.ParametersJson)
        }
    };

    internal static async Task<object> RunAsync(CancellationToken cancellation)
    {
        var zone = TimeZoneInfo.Utc;
        var now = new DateTimeOffset(2026, 10, 6, 15, 12, 0, TimeSpan.Zero);
        var steps = new List<object>();
        var ok = true;
        void Step(string name, bool passed, object? detail = null)
        {
            ok &= passed;
            steps.Add(new { name, passed, detail });
        }

        // Two companion PCs share their entries through the shared settings, as the desktop does.
        var shared = SharedSettings.Empty;
        ReminderBoard Board()
        {
            var entries = new Dictionary<string, ReminderEntry>(StringComparer.Ordinal);
            foreach (var setting in shared.Settings.Where(s => s.Key.StartsWith(SharedSettings.RemindersPrefix, StringComparison.Ordinal)))
                if (ReminderEntry.Read(setting.Value) is { } entry) entries[setting.UpdatedBy] = entry;
            return new(entries);
        }
        ReminderEntry Own(string device) => Board().Entries.GetValueOrDefault(device) ?? ReminderEntry.Empty;
        void Write(string device, ReminderEntry entry, DateTimeOffset at) =>
            shared = SharedSettings.Merge(shared, SharedSettings.Empty.Put(SharedSettings.RemindersPrefix + device, entry.Write(), null, device, at));

        var set = Reminders.Run("""{"action":"set","text":"do the dishes","in_minutes":60}""", Board(), "desktop-a", Own("desktop-a"), now, zone,
            () => "abc123");
        Step("set on desktop-a", !set.Result.IsError && set.Own is not null, set.Result.Output);
        Write("desktop-a", set.Own!, now);
        var at = Reminders.Run("""{"action":"set","text":"call mom","at":"tomorrow 9:00"}""", Board(), "desktop-a", Own("desktop-a"), now, zone,
            () => "def456");
        Step("set at a local time", !at.Result.IsError && at.Own!.Reminders.Any(r => r.Due == new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero)),
            at.Result.Output);
        Write("desktop-a", at.Own!, now.AddSeconds(1));
        var listed = Reminders.Run("""{"action":"list"}""", Board(), "desktop-b", Own("desktop-b"), now.AddSeconds(2), zone);
        Step("listed on desktop-b", listed.Result.Output.Contains("abc123") && listed.Result.Output.Contains("def456"), listed.Result.Output);
        var canceled = Reminders.Run("""{"action":"cancel","id":"def456"}""", Board(), "desktop-b", Own("desktop-b"), now.AddSeconds(3), zone);
        Write("desktop-b", canceled.Own!, now.AddSeconds(3));
        Step("canceled on desktop-b", Board().Find("def456")?.State == ReminderState.Canceled, canceled.Result.Output);
        var readOnly = Reminders.Run(Reminders.ListArgumentsJson, Board(), "desktop-b", Own("desktop-b"), now.AddSeconds(4), zone);
        Step("the reply's list_reminders only lists", readOnly.Own is null && readOnly.Result.Output.Contains("abc123") &&
            !readOnly.Result.Output.Contains("def456") && CheckInToolSets.ReminderSet.Replaces.Contains(Reminders.ToolName),
            new { replaces = CheckInToolSets.ReminderSet.Replaces, outcome = readOnly.Outcome });
        Step("bad call refused", Reminders.Run("""{"action":"set","text":"x","at":"someday"}""", Board(), "desktop-a", Own("desktop-a"), now, zone).Result.IsError);

        // Due: both offer, desktop-b (used 5 s ago) beats desktop-a (idle 10 minutes) and says it; desktop-a stays quiet.
        var due = now.AddHours(1);
        var bidA = Reminders.Decide(Board(), "desktop-a", due, alone: false);
        var bidB = Reminders.Decide(Board(), "desktop-b", due, alone: false);
        Step("both offer when due", bidA.Any(d => d.Step == ReminderStep.Bid) && bidB.Any(d => d.Step == ReminderStep.Bid));
        Write("desktop-a", Own("desktop-a").Mark("abc123", ReminderMarkKind.Bid, due, 600), due);
        Write("desktop-b", Own("desktop-b").Mark("abc123", ReminderMarkKind.Bid, due.AddSeconds(1), 5), due.AddSeconds(1));
        var decided = due + Reminders.DecideAfter + TimeSpan.FromSeconds(2);
        var a = Reminders.Decide(Board(), "desktop-a", decided, false);
        var b = Reminders.Decide(Board(), "desktop-b", decided, false);
        Step("the PC used most recently takes it", a.Count == 0 && b.SingleOrDefault()?.Step == ReminderStep.Claim,
            new { desktopA = a.Select(d => d.Step.ToString()), desktopB = b.Select(d => d.Step.ToString()) });
        Write("desktop-b", Own("desktop-b").Mark("abc123", ReminderMarkKind.Claim, decided), decided);
        Step("the other stays quiet", Reminders.Decide(Board(), "desktop-a", decided.AddSeconds(2), false).Count == 0);

        // desktop-b's conversation brings it up: on its own as soon as Martlet is free, or with the next message.
        using var jobs = new BackgroundJobs();
        var reminder = Board().Find("abc123")!.Reminder;
        var job = jobs.Start(Reminders.Kind, reminder.Text, (_, _) => Task.FromResult(BackgroundJobOutcome.Done(Reminders.Due(reminder, decided, zone))))
            .Job!;
        for (var i = 0; i < 300 && !job.Finished; i++) await Task.Delay(10, cancellation);
        var notes = BackgroundJobs.ReportNotes(null, jobs.Undelivered);
        var delivery = jobs.Take(onItsOwn: true, noticesOnly: true);
        var message = delivery is null ? null : BackgroundJobs.ReportMessage(null, delivery.Jobs).UserText;
        delivery?.Complete();
        Step("brought up on its own", message is not null && message.Contains("do the dishes") && message.Contains("a reminder"), message);
        Step("or with the next message", notes is not null && notes.Contains("Answer what the user just said first"), notes);
        Step("said once", job.Delivery == BackgroundDeliveryState.Delivered && !jobs.HasNotice);
        Write("desktop-b", Own("desktop-b").Mark("abc123", ReminderMarkKind.Done, decided.AddSeconds(5)), decided.AddSeconds(5));
        Step("settled everywhere", Board().Find("abc123")?.State == ReminderState.Done && Board().Pending.Count == 0);

        // A PC alone says it at once; one far too late is let go.
        var lone = ReminderEntry.Empty.With(new() { Id = "r1", Text = "stretch", Due = now, Set = now.AddMinutes(-5) });
        var loneBoard = new ReminderBoard(new Dictionary<string, ReminderEntry> { ["desktop-c"] = lone });
        Step("alone: takes it at once", Reminders.Decide(loneBoard, "desktop-c", now, alone: true).SingleOrDefault()?.Step == ReminderStep.Claim);
        Step("far too late: let go", Reminders.Decide(loneBoard, "desktop-c", now + Reminders.LatestLate + TimeSpan.FromMinutes(1), true)
            .SingleOrDefault()?.Step == ReminderStep.Miss);

        return new { passed = ok, steps };
    }
}
