using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Avatar.Hosting;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Companion › Check-ins (docs/CONVERSATION.md#check-ins): every 15 seconds on a companion PC, the first check-in that is
/// due and has something to check runs on the Thinking pool (<see cref="ThinkingJobKind.CheckIn"/>, never the conversation's own
/// Thinking route), one at a time, with the facts it needs gathered here (<see cref="CheckInState"/>). Martlet acts on the
/// answer: it turns off the lingering emotes a reply left on that no longer fit, takes the eyes back to their usual gaze, puts a
/// reminder for the next reply on the context board (consumed by the next request, so prompt caches keep working) or has
/// Martlet bring something up on its own. The page sets each check-in on or off and how often it runs, adds the owner's own,
/// and says why each waits and what its last run did. check-ins-status.json says the same for MCP (never what was said or
/// answered).</summary>
public partial class MainWindow
{
    /// <summary>check-ins-status.json in the data directory: each check-in's choices, why it waits and its last run (never what
    /// was said or answered), which MCP's check_ins_status reads.</summary>
    internal const string CheckInStatusFile = "check-ins-status.json";
    /// <summary>A text file whose content answers every check-in instead of the Thinking pool (FIXTURE - NOT AI), for automated
    /// checks of the desktop (docs/MCP.md#check-ins). Read before each run, so a check can change it.</summary>
    internal const string CheckInFixtureVariable = "MARTLET_CHECK_INS_FIXTURE";
    private readonly DispatcherTimer checkInTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private CheckInSettings checkInSettings = new();
    private readonly Dictionary<string, CheckInRun> checkInRuns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> checkInWaits = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Runs, int Acted)> checkInCounts = new(StringComparer.Ordinal);
    private string? checkInRunning;
    // The conversation's exchange count at the last look, for when each check-in is due next.
    private long checkInExchanged;
    private (string Name, CheckInRun Run)? checkInLast;
    private string? checkInStatusWritten;
    // Refreshes the open Check-ins page's status lines.
    private Action? showCheckIns;

    private static readonly (CheckInFacts Fact, string Label)[] CheckInFactChoices =
    [
        (CheckInFacts.Conversation, "The conversation"),
        (CheckInFacts.Persona, "Its personality"),
        (CheckInFacts.Character, "Emotes and gaze"),
        (CheckInFacts.Work, "Reminders and background work"),
        (CheckInFacts.Screen, "What changed on screen"),
        (CheckInFacts.Sound, "What the PC plays"),
        (CheckInFacts.Presence, "Whether you're at the PC")
    ];

    private void InitializeCheckIns()
    {
        var (settings, state) = CheckInSettings.Read(store?.DataDirectory);
        checkInSettings = settings;
        if (state == "unreadable") ErrorLog.Warn($"Check-ins: {CheckInSettings.FileName} couldn't be read, so the check-ins use their defaults.");
        checkInTimer.Tick += (_, _) => FollowCheckInsAsync().Forget();
    }

    private void StartCheckIns()
    {
        if (closing) return;
        checkInTimer.Start();
        try { LookAtCheckIns(); }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or ContractException)
        {
            ErrorLog.Warn("Check-ins: couldn't look at the check-ins.", error);
        }
        WriteCheckInStatus();
    }

    /// <summary>Notes why each check-in waits now and returns the first one that may run (or the one the owner asked for
    /// <paramref name="now"/>) with the facts it would get. Nothing runs.</summary>
    private (CheckIn? Next, CheckInState? State) LookAtCheckIns(string? now = null)
    {
        var all = CheckIns.All(checkInSettings);
        var blocked = Role != DeviceRole.Companion ? "this PC is a Martlet host" : conversation is null ? "Martlet can't talk on this PC" : null;
        if (blocked is not null || now is null && !all.Any(c => c.On))
        {
            foreach (var checkIn in all) checkInWaits[checkIn.Id] = blocked is not null && (checkIn.On || checkIn.Id == now) ? blocked : "it's off";
            return (null, null);
        }
        conversation!.ReadThinkingPoolOnce();
        var state = CheckInStateNow();
        checkInExchanged = state.Exchanged;
        var member = CheckInFixture() is not null || conversation.ThinkingPool.CanRun(ThinkingJobKind.CheckIn);
        CheckIn? next = null;
        foreach (var checkIn in all)
        {
            var wait = CheckIns.Wait(checkIn, state, checkInRuns.GetValueOrDefault(checkIn.Id), checkIn.Id == now);
            if (wait is null && !member) wait = "no Thinking pool member can take it";
            checkInWaits[checkIn.Id] = wait ?? "";
            if (wait is null && next is null && (now is null || checkIn.Id == now)) next = checkIn;
        }
        return (next, state);
    }

    /// <summary>One look at the check-ins: each says why it waits, and the first one that may run (or the one the owner asked
    /// for <paramref name="now"/>) runs on the Thinking pool. Nothing while one runs.</summary>
    private async Task FollowCheckInsAsync(string? now = null)
    {
        if (checkInRunning is not null || closing) return;
        try
        {
            var (next, state) = LookAtCheckIns(now);
            if (next is not null) await RunCheckInAsync(next, state!, next.Id == now);
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or ContractException)
        {
            ErrorLog.Warn("Check-ins: couldn't look at the check-ins.", error);
        }
        finally
        {
            ShowCheckInsNow();
            WriteCheckInStatus();
        }
    }

    /// <summary>Runs <paramref name="checkIn"/> on the Thinking pool and acts on its answer; its run is kept for the page, the
    /// status file and when it is due next.</summary>
    private async Task RunCheckInAsync(CheckIn checkIn, CheckInState state, bool now)
    {
        var focused = CheckIns.Focus(checkIn, state, now);
        var prompts = homeSettings?.Prompts;
        if (CheckIns.Prepare(checkIn, focused, prompts) is not { } job)
        {
            checkInWaits[checkIn.Id] = checkIn.Custom ? "its prompt is empty" : "its prompt on Companion › Prompts is empty";
            return;
        }
        checkInRunning = checkIn.Id;
        ShowCheckInsNow();
        var began = Stopwatch.GetTimestamp();
        string result;
        bool acted = false, kept = false;
        string? member = null;
        try
        {
            var done = CheckInFixture() is { } fixture
                ? File.Exists(fixture)
                    ? new ThinkingJobResult(ThinkingJobOutcome.Succeeded, await File.ReadAllTextAsync(fixture, lifetime.Token), "fixture",
                        "FIXTURE - NOT AI", null, 1)
                    : new ThinkingJobResult(ThinkingJobOutcome.Failed, null, null, null, $"{CheckInFixtureVariable} names no file", 0)
                : await conversation!.ThinkingPool.RunAsync(job, lifetime.Token);
            member = done.Member is { } name ? done.Model is { } model ? $"{name} ({model})" : name : null;
            if (!done.Succeeded)
                result = done.Outcome == ThinkingJobOutcome.NoMember ? "no Thinking pool member could take it"
                    : $"the Thinking pool didn't answer ({done.Problem ?? done.Outcome.ToString()})";
            else
            {
                var verdict = CheckIns.Read(checkIn, done.Text, focused);
                kept = verdict is { Readable: true, Act: false };
                (result, acted) = await ActOnCheckInAsync(checkIn, verdict, prompts);
            }
        }
        catch (OperationCanceledException) { return; }
        finally { checkInRunning = null; }
        var took = Stopwatch.GetElapsedTime(began);
        var run = new CheckInRun(DateTimeOffset.Now, state.Exchanged, result, acted) { Member = member, Took = took, Kept = kept };
        checkInRuns[checkIn.Id] = run;
        checkInLast = (checkIn.Name, run);
        var counts = checkInCounts.GetValueOrDefault(checkIn.Id);
        checkInCounts[checkIn.Id] = (counts.Runs + 1, counts.Acted + (acted ? 1 : 0));
        checkInWaits[checkIn.Id] = "";
        ErrorLog.Info($"Check-ins: {checkIn.Name}{(member is null ? "" : " ran on " + member)} in {took.TotalSeconds:0.0} s: {result}.");
    }

    /// <summary>Does what the answer asks for and says what came of it in a few words (never the answer's own text).</summary>
    private async Task<(string Result, bool Acted)> ActOnCheckInAsync(CheckIn checkIn, CheckInVerdict verdict, PromptSettings? prompts)
    {
        if (!verdict.Readable) return ("its answer couldn't be read, so nothing changed", false);
        if (!verdict.Act)
            return (checkIn.Outcome switch
            {
                CheckInOutcome.EmotesOff => "every emote still fits",
                CheckInOutcome.GazeUsual => "the gaze still fits",
                CheckInOutcome.Say => "nothing to bring up",
                _ => "nothing to remind Martlet of"
            }, false);
        switch (checkIn.Outcome)
        {
            case CheckInOutcome.EmotesOff:
            {
                var catalog = characterActions.For(avatar.InspectedProfile?.ModelPath);
                var off = new List<string>();
                foreach (var held in avatar.Held.Current.Where(h => h.ByReply))
                {
                    var tag = catalog?.Entries.FirstOrDefault(e => e.Source.Id == held.Source.Id).Action?.Tag;
                    if (tag is null || !verdict.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) continue;
                    try
                    {
                        if (await avatar.StopActionAsync(held.Source, "a check-in", lifetime.Token)) off.Add("{" + tag + "}");
                    }
                    catch (Exception error) when (RendererFailures.Is(error, lifetime.Token))
                    {
                        RendererFailures.Log($"A check-in couldn't turn off {{{tag}}}", error);
                    }
                }
                return off.Count == 0 ? ("the emotes it named were already off", false) : ("turned off " + string.Join(" and ", off), true);
            }
            case CheckInOutcome.GazeUsual:
                return avatar.Gaze.BackToUsual("A check-in") ? ("took the eyes back to their usual gaze", true) : ("the eyes already did their usual", false);
            case CheckInOutcome.Note:
            {
                if (CheckIns.Note(prompts, verdict.Text!) is not { } note)
                    return ("the Check-in: reminder for the next reply prompt is empty, so nothing went to the conversation", false);
                try { contextBoard.Post(CheckIns.Source(checkIn.Id), note, DateTimeOffset.Now, CheckIns.NoteAge, consume: true); }
                catch (InvalidOperationException) { return ("the context board is full, so nothing went to the conversation", false); }
                return ($"a reminder waits for the next reply ({verdict.Text!.Length} characters)", true);
            }
            default:
                return ConversationSession()?.BringUp(checkIn.Name, verdict.Text!) is null
                    ? ("Martlet can't talk now, so it was dropped", false)
                    : ($"Martlet brings it up as soon as it's free ({verdict.Text!.Length} characters)", true);
        }
    }

    /// <summary>What the check-ins may know now (on the UI thread): the time, the personality, the conversation's newest
    /// exchanges, what Martlet said lately with when, how long it and this PC have been quiet, the lingering emotes a reply
    /// turned on, a gaze a reply chose, the reminders and background work, and the context board's screen and sound notes.</summary>
    private CheckInState CheckInStateNow()
    {
        var now = DateTimeOffset.Now;
        var persona = homeSettings?.Companion?.ActivePersona;
        var (exchanges, total) = conversation?.RecentExchanges(8) ?? ([], 0);
        var showing = avatar.IsShowing;
        var catalog = showing ? characterActions.For(avatar.InspectedProfile?.ModelPath) : null;
        var emotes = catalog is null ? [] : avatar.Held.Current.Where(h => h.ByReply)
            .Select(h => catalog.Entries.FirstOrDefault(e => e.Source.Id == h.Source.Id) is { Action.Tag: { Length: > 0 } tag } entry
                ? new CheckInEmote(tag, CharacterActions.Hint(entry.Source, entry.Action), now - h.Since) : null)
            .OfType<CheckInEmote>().ToArray();
        CheckInGaze? gaze = null;
        if (showing && avatar.Gaze.ChosenSince is { } since)
        {
            var settings = avatar.Gaze.Settings;
            gaze = new(CharacterGaze.Does(settings.Mode), CharacterGaze.Does(settings.Usual), now - since);
        }
        var board = contextBoard.Snapshot(now);
        return new()
        {
            Now = now, Name = persona?.Name, Persona = persona?.Text, Conversation = openConversation is not null, Exchanges = exchanges,
            Exchanged = total, Quiet = openConversation?.SinceActivity, Away = TimeSpan.FromSeconds(ReminderIdleSeconds()),
            CharacterShows = showing, Emotes = emotes, Gaze = gaze, Work = CheckInWork(now),
            Screen = board.Notes.FirstOrDefault(n => n.Source == ContextBoard.Screen)?.Text,
            Sound = board.Notes.FirstOrDefault(n => n.Source == ContextBoard.Sound)?.Text,
            Said = conversation?.RecentSayings(now) ?? []
        };
    }

    // The reminders waiting and this conversation's background work, one short line each.
    private IReadOnlyList<string> CheckInWork(DateTimeOffset now)
    {
        var lines = new List<string>();
        foreach (var due in RemindersNow().Pending.Take(8))
            lines.Add($"A reminder for {Reminders.When(due.Reminder.Due, now, TimeZoneInfo.Local)}: {due.Reminder.Text}");
        if (conversation is { } talk)
            foreach (var job in talk.Jobs.Active.Concat(talk.Jobs.Recent).Take(8))
                lines.Add($"{LiveConversationWindow.KindTitle(job.Kind)} ({(job.Finished ? job.State == BackgroundJobState.Succeeded ? "done" : "ended" : "running")}): {job.Label}");
        return lines;
    }

    /// <summary>Check now on the page: runs that check-in at once (whether it is on or due), when it has something to check and
    /// no other check-in runs, and says what came of it.</summary>
    private async Task CheckInNowAsync(string id)
    {
        if (CheckIns.All(checkInSettings).FirstOrDefault(c => c.Id == id) is not { } checkIn) return;
        if (checkInRunning is not null)
        {
            ActionText.Text = "Another check-in runs now. Try again in a moment.";
            return;
        }
        ActionText.Text = $"Checking {checkIn.Name}...";
        var before = checkInRuns.GetValueOrDefault(id);
        await FollowCheckInsAsync(id);
        ActionText.Text = checkInRuns.GetValueOrDefault(id) is { } after && !ReferenceEquals(before, after)
            ? $"{checkIn.Name}: {after.Result}."
            : $"{checkIn.Name} didn't run: {(checkInWaits.GetValueOrDefault(id) is { Length: > 0 } wait ? wait : "it couldn't start")}.";
    }

    // ---------- the page ----------

    private void RenderCheckInsTab(Panel page)
    {
        conversation?.ReadThinkingPoolOnce();
        var now = new TextBlock { FontSize = 15, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(now, "CheckInsNow");
        var last = Note("", new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(last, "CheckInsLast");
        AutomationProperties.SetLiveSetting(last, AutomationLiveSetting.Polite);
        page.Children.Add(Card(Heading("Now"), now, last,
            Row(PageButton("Open Thinking pool", () => OpenCompanion(CompanionTab.DeepThinking), link: true, id: "CheckInsOpenPool"),
                PageButton("Edit their prompts", () => OpenCompanion(CompanionTab.Prompts), link: true, id: "CheckInsOpenPrompts"))));
        page.Children.Add(Card(Heading("How check-ins work"),
            Note("A check-in is a short question that the Thinking pool answers for Martlet every few minutes, with only the facts " +
                "it needs: the end of the conversation, what Martlet said lately, the emotes that stay on, where the eyes look, " +
                "and the reminders and work Martlet started. Martlet then acts on the answer. It turns off an emote, takes the " +
                "eyes back to their usual gaze, or puts a short reminder in the notes of your next message, so its next reply " +
                "follows it.", new Thickness(0, 0, 0, 0)),
            Note("Check-ins run only on Thinking pool members, never on the conversation's own Thinking model, so replies never " +
                "wait for them. They wait while you talk and while nobody uses this PC, and they stay on this PC. Their prompts are " +
                "on Prompts, in Check-ins.", new Thickness(0, 6, 0, 0))));
        var lines = new List<Action>();
        foreach (var checkIn in CheckIns.All(checkInSettings).Where(c => !c.Custom)) page.Children.Add(BuiltInCheckInCard(checkIn, lines));
        page.Children.Add(OwnCheckInsCard(lines));
        showCheckIns = () =>
        {
            now.Text = CheckInsNowText();
            last.Text = checkInLast is { } done
                ? $"Last check-in: {done.Name} at {done.Run.At.ToLocalTime():t}{(done.Run.Member is { } member ? " on " + member : "")}: {done.Run.Result}."
                : "No check-in has run since Martlet started.";
            foreach (var show in lines) show();
        };
        // Each line says why its check-in waits right away, not only after the next look.
        if (checkInRunning is null)
            try { LookAtCheckIns(); }
            catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or ContractException)
            {
                ErrorLog.Warn("Check-ins: couldn't look at the check-ins.", error);
            }
        showCheckIns();
    }

    private void ShowCheckInsNow()
    {
        if (openTab == CompanionTab.CheckIns) showCheckIns?.Invoke();
    }

    /// <summary>The page's Now line: how many check-ins are on and where they run, or why they can't.</summary>
    private string CheckInsNowText()
    {
        var on = CheckIns.All(checkInSettings).Count(c => c.On);
        var count = $"{on} check-in{(on == 1 ? " is" : "s are")} on";
        if (Role != DeviceRole.Companion) return $"{count}, but check-ins run only on a companion PC, and this PC is a Martlet host.";
        if (conversation is null) return $"{count}, but they can't run: Martlet can't talk on this PC.";
        if (CheckInFixture() is not null) return $"{count}. FIXTURE - NOT AI: {CheckInFixtureVariable} answers them, not the Thinking pool.";
        if (conversation.ThinkingPool.Find(ThinkingJobKind.CheckIn) is not { } member)
            return $"{count}, but they wait: the Thinking pool has no member that can take them. Add one on Thinking pool.";
        return $"{count}. They run on the Thinking pool ({member.Name}{(member.Model is { } model ? ", " + model : "")} first), never on the " +
            "conversation's own Thinking model.";
    }

    private static string? CheckInFixture() => Environment.GetEnvironmentVariable(CheckInFixtureVariable) is { Length: > 0 } path ? path : null;

    /// <summary>A check-in's status line: running, off, why it waits or that it runs at the next chance, then its last run.</summary>
    private string CheckInLine(string id)
    {
        if (CheckIns.All(checkInSettings).FirstOrDefault(c => c.Id == id) is not { } checkIn) return "";
        var now = checkInRunning == id ? "Checking now."
            : !checkIn.On ? "Off."
            : checkInWaits.GetValueOrDefault(id) is { Length: > 0 } wait ? $"Waits: {wait}."
            : "Runs at the next chance.";
        if (checkInRuns.GetValueOrDefault(id) is not { } last) return now + " It hasn't run since Martlet started.";
        var counts = checkInCounts.GetValueOrDefault(id);
        return $"{now} Last at {last.At.ToLocalTime():t}{(last.Member is { } member ? " on " + member : "")}: {last.Result}. " +
            $"{counts.Runs} run{(counts.Runs == 1 ? "" : "s")} since Martlet started, {counts.Acted} acted on.";
    }

    private Border BuiltInCheckInCard(CheckIn checkIn, List<Action> lines)
    {
        var on = new CheckBox { Content = "On", IsChecked = checkIn.On, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(on, "CheckInOn-" + checkIn.Id);
        AutomationProperties.SetName(on, checkIn.Name + " on");
        var every = EveryChoice(checkIn.EveryMinutes);
        AutomationProperties.SetAutomationId(every, "CheckInEvery-" + checkIn.Id);
        AutomationProperties.SetName(every, checkIn.Name + ": how often");
        var status = Note("", new Thickness(0, 6, 0, 0));
        AutomationProperties.SetAutomationId(status, "CheckInStatus-" + checkIn.Id);
        lines.Add(() => status.Text = CheckInLine(checkIn.Id));
        void Save()
        {
            var minutes = CheckIns.EveryChoices[Math.Max(0, every.SelectedIndex)];
            SaveCheckIns(checkInSettings.With(checkIn.Id, on.IsChecked == true, minutes),
                $"{checkIn.Name} is {(on.IsChecked == true ? "on, every " + EveryText(minutes) : "off")}.");
        }
        on.Checked += (_, _) => Save();
        on.Unchecked += (_, _) => Save();
        every.SelectionChanged += (_, _) => Save();
        var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        row.Children.Add(RowGroup(on));
        row.Children.Add(RowGroup(RowLabel("Every", every), every));
        return Card(Heading(checkIn.Name), Note(checkIn.Does, new Thickness(0, 0, 0, 0)), row, status,
            Row(PageButton("Check now", () => CheckInNowAsync(checkIn.Id).Forget(), id: "CheckInRun-" + checkIn.Id),
                PageButton("Edit its prompt", () => OpenCompanion(CompanionTab.Prompts), link: true, id: "CheckInPrompt-" + checkIn.Id)));
    }

    /// <summary>Your own check-ins: a row for each and Add a check-in. Their edits save on their own (one save for all of them, at
    /// once for a switch or a choice and after a short pause for typing).</summary>
    private Border OwnCheckInsCard(List<Action> lines)
    {
        var rows = new List<Func<CustomCheckIn>>();
        var autoSave = new AutoSave(() =>
        {
            SaveCheckIns(checkInSettings with { Custom = [.. rows.Select(read => read())] }, null);
            return Task.FromResult(true);
        });
        tabAutoSave = autoSave;
        var stack = new List<UIElement>
        {
            Heading("Your own check-ins"),
            Note("Write what the Thinking pool should check, such as \"If the user has been at it for hours, suggest a short break\" or " +
                "\"If it's late at night, remind Martlet to talk more softly\". Choose what it gets to know (it always gets the day " +
                "and time), how often it runs and what happens with its answer: a reminder in Martlet's next reply, or Martlet " +
                "brings it up on its own as soon as it's free. A new check-in starts off.", new Thickness(0, 0, 0, 4))
        };
        foreach (var custom in checkInSettings.Custom) stack.Add(OwnCheckInRow(custom, lines, rows, autoSave));
        var add = PageButton("Add a check-in", AddOwnCheckIn, id: "CheckInAdd");
        add.IsEnabled = checkInSettings.Custom.Count < CheckIns.MaximumCustom;
        AutomationProperties.SetHelpText(add, $"Adds a check-in of your own, off until you turn it on (at most {CheckIns.MaximumCustom}).");
        stack.Add(Row(add));
        return Card([.. stack]);
    }

    private void AddOwnCheckIn()
    {
        if (tabAutoSave is { Pending: true } pending) pending.SaveNowAsync().Forget();
        if (checkInSettings.Custom.Count >= CheckIns.MaximumCustom || checkInSettings.NewId() is not { } id) return;
        var custom = new CustomCheckIn
        {
            Id = id, Name = "My check-in", On = false, EveryMinutes = 30, Task = "",
            Facts = CheckInFacts.Conversation | CheckInFacts.Persona, Outcome = CheckInOutcome.Note
        };
        if (SaveCheckIns(checkInSettings.With(custom), "Added a check-in. Write what it checks, then turn it on.")) RenderTab();
    }

    private StackPanel OwnCheckInRow(CustomCheckIn custom, List<Action> lines, List<Func<CustomCheckIn>> rows, AutoSave autoSave)
    {
        var id = custom.Id;
        var view = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        var name = Compact(new TextBox { Text = custom.Name, MaxLength = CheckIns.MaximumNameCharacters, Width = 220 });
        AutomationProperties.SetAutomationId(name, "CheckInName-" + id);
        AutomationProperties.SetName(name, "Check-in name");
        var on = new CheckBox { Content = "On", IsChecked = custom.On, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(on, "CheckInOn-" + id);
        AutomationProperties.SetName(on, "Check-in on");
        var every = EveryChoice(custom.EveryMinutes);
        AutomationProperties.SetAutomationId(every, "CheckInEvery-" + id);
        AutomationProperties.SetName(every, "How often it runs");
        var outcome = Compact(new ComboBox
        {
            ItemsSource = new[] { "Reminds Martlet in its next reply", "Martlet brings it up" },
            SelectedIndex = custom.Outcome == CheckInOutcome.Say ? 1 : 0, Width = 250
        });
        AutomationProperties.SetAutomationId(outcome, "CheckInOutcome-" + id);
        AutomationProperties.SetName(outcome, "What happens with its answer");
        var task = new TextBox
        {
            Text = custom.Task, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = CheckIns.MaximumTaskCharacters,
            MinHeight = 56, MaxHeight = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 4, 0, 0)
        };
        AutomationProperties.SetAutomationId(task, "CheckInTask-" + id);
        AutomationProperties.SetName(task, "What it checks");
        var facts = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        var boxes = new List<(CheckInFacts Fact, CheckBox Box)>();
        foreach (var (fact, label) in CheckInFactChoices)
        {
            var box = new CheckBox { Content = label, IsChecked = custom.Facts.HasFlag(fact), Margin = new Thickness(0, 0, 16, 6) };
            AutomationProperties.SetAutomationId(box, $"CheckInFact-{id}-{fact}");
            boxes.Add((fact, box));
            facts.Children.Add(box);
        }
        var status = Note("", new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(status, "CheckInStatus-" + id);
        lines.Add(() => status.Text = CheckInLine(id));
        rows.Add(() => new CustomCheckIn
        {
            Id = id, Name = name.Text.Trim() is { Length: > 0 } named ? named : "My check-in", On = on.IsChecked == true,
            EveryMinutes = CheckIns.EveryChoices[Math.Max(0, every.SelectedIndex)], Task = task.Text.Replace("\r\n", "\n", StringComparison.Ordinal),
            Facts = boxes.Where(b => b.Box.IsChecked == true).Aggregate(CheckInFacts.None, (all, b) => all | b.Fact),
            Outcome = outcome.SelectedIndex == 1 ? CheckInOutcome.Say : CheckInOutcome.Note
        });
        void Typed()
        {
            tabEdited = true;
            autoSave.Changed();
        }
        name.TextChanged += (_, _) => Typed();
        task.TextChanged += (_, _) => Typed();
        on.Checked += (_, _) => autoSave.SaveNowAsync().Forget();
        on.Unchecked += (_, _) => autoSave.SaveNowAsync().Forget();
        every.SelectionChanged += (_, _) => autoSave.SaveNowAsync().Forget();
        outcome.SelectionChanged += (_, _) => autoSave.SaveNowAsync().Forget();
        foreach (var (_, box) in boxes)
        {
            box.Checked += (_, _) => autoSave.SaveNowAsync().Forget();
            box.Unchecked += (_, _) => autoSave.SaveNowAsync().Forget();
        }
        var top = new WrapPanel();
        top.Children.Add(RowGroup(name));
        top.Children.Add(RowGroup(on));
        top.Children.Add(RowGroup(RowLabel("Every", every), every));
        top.Children.Add(RowGroup(RowLabel("Its answer", outcome), outcome));
        view.Children.Add(top);
        view.Children.Add(new Label { Content = "_What it checks", Target = task, Padding = new Thickness(0, 6, 0, 0) });
        view.Children.Add(task);
        view.Children.Add(new TextBlock { Text = "It gets to know", Margin = new Thickness(0, 8, 0, 0) });
        view.Children.Add(facts);
        view.Children.Add(status);
        async Task SaveThenCheckAsync()
        {
            await autoSave.SaveNowAsync();
            await CheckInNowAsync(id);
        }
        view.Children.Add(Row(PageButton("Check now", () => SaveThenCheckAsync().Forget(), id: "CheckInRun-" + id),
            PageButton("Remove", () =>
            {
                // The other rows' edits save first; the save is synchronous, so nothing brings the removed row back.
                autoSave.SaveNowAsync().Forget();
                if (!SaveCheckIns(checkInSettings.Without(id), "Removed the check-in.")) return;
                // A new check-in may get its ID again; it starts with no runs.
                checkInRuns.Remove(id);
                checkInCounts.Remove(id);
                checkInWaits.Remove(id);
                RenderTab();
            }, id: "CheckInRemove-" + id)));
        return view;
    }

    private static ComboBox EveryChoice(int minutes)
    {
        var index = 0;
        for (var i = 0; i < CheckIns.EveryChoices.Count; i++)
            if (CheckIns.EveryChoices[i] == minutes) index = i;
        return Compact(new ComboBox { ItemsSource = CheckIns.EveryChoices.Select(EveryText).ToArray(), SelectedIndex = index, Width = 130 });
    }

    private static string EveryText(int minutes) => minutes < 60 ? $"{minutes} minutes" : minutes == 60 ? "1 hour" : $"{minutes / 60} hours";

    /// <summary>Saves check-ins.json and uses it at once; says why when it can't.</summary>
    private bool SaveCheckIns(CheckInSettings next, string? said)
    {
        try { next.Validate(); }
        catch (ContractException error)
        {
            ActionText.Text = "Check-ins not saved: " + error.Message;
            return false;
        }
        if (!next.Save(store?.DataDirectory))
        {
            ActionText.Text = "Check-ins not saved: Martlet can't write to its data folder.";
            return false;
        }
        checkInSettings = next;
        if (said is not null) ActionText.Text = said;
        ShowCheckInsNow();
        WriteCheckInStatus();
        return true;
    }

    // ---------- check-ins-status.json ----------

    private void WriteCheckInStatus()
    {
        if (store?.DataDirectory is not { } directory) return;
        try
        {
            var body = CheckInStatusBody();
            if (body == checkInStatusWritten) return;
            var path = Path.Combine(directory, CheckInStatusFile);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, body);
            File.Move(temporary, path, overwrite: true);
            checkInStatusWritten = body;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    // What check-ins-status.json says: choices, waits and last runs in a few words. Never what was said, answered or reminded.
    private string CheckInStatusBody()
    {
        var member = Role == DeviceRole.Companion ? conversation?.ThinkingPool.Find(ThinkingJobKind.CheckIn) : null;
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            role = Role.ToString(),
            running = checkInRunning,
            pool = new { canRun = member is not null, member = member?.Name, model = member?.Model, fixture = CheckInFixture() is not null },
            checkIns = CheckIns.All(checkInSettings).Select(c =>
            {
                var last = checkInRuns.GetValueOrDefault(c.Id);
                var counts = checkInCounts.GetValueOrDefault(c.Id);
                return new
                {
                    id = c.Id, name = c.Name, custom = c.Custom, on = c.On, everyMinutes = c.EveryMinutes, outcome = c.Outcome.ToString(),
                    facts = c.Custom ? c.Facts.ToString() : null, prompt = c.PromptId,
                    waiting = checkInWaits.GetValueOrDefault(c.Id) is { Length: > 0 } wait ? wait : null,
                    nextAt = last is null ? (DateTimeOffset?)null : last.At + CheckIns.Pace(c, last, checkInExchanged),
                    runs = counts.Runs, acted = counts.Acted,
                    last = last is null ? null : new
                    {
                        at = last.At, result = last.Result, acted = last.Acted, member = last.Member,
                        ms = last.Took is { } took ? (long?)took.TotalMilliseconds : null
                    }
                };
            }).ToArray()
        }, new JsonSerializerOptions { WriteIndented = true });
    }
}
