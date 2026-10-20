using Martlet.Conversation;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Companion › Check-ins' decisions and settings, without WPF: what the Now line and each check-in's status line say,
/// what a card's controls hold (<see cref="CheckInEdit"/>) and how they become check-ins.json, and the changes the page saves
/// (Copy as your own, Use built-in settings, Add a check-in, Remove). The fixed choices and help texts are in
/// CheckInsPageModel.Choices.cs. <see cref="CheckInsPage"/> draws it, and <see cref="ICheckInsPageServices"/> is all it reads and
/// changes in the app. It reads check-ins.json from the runner at each use, because each save changes it.</summary>
internal sealed partial class CheckInsPageModel(ICheckInsPageServices services)
{
    /// <summary>check-ins.json as the runner uses it now.</summary>
    internal CheckInSettings Choices => services.Choices;

    /// <summary>Every check-in: the built-in ones, then your own.</summary>
    internal IReadOnlyList<CheckIn> All => CheckIns.All(Choices);

    /// <summary>What a check-in's shared controls hold: what it gets to know, when it waits, its answer, what its model must
    /// handle (as chosen; a screenshot and a recording add theirs on their own), what it takes with each run and its script.</summary>
    internal sealed record CheckInEdit(CheckInFacts Facts, CheckInConditions Conditions, CheckInOutcome Outcome, ThinkingCapability Needs,
        bool Screenshot, CheckInRecording Recording, int RecordingSeconds, string Script, IReadOnlyList<string> ToolSets)
    {
        public CheckInTriggers Triggers { get; init; }
        public int FromHour { get; init; } = CheckIns.DefaultFromHour;
        public int UntilHour { get; init; } = CheckIns.DefaultUntilHour;
        public int MostPerHour { get; init; }
        /// <summary>Where it runs in the Thinking pool; null: like other check-ins (the check-in kind's Runs on).</summary>
        public ThinkingRunsOn? RunsOn { get; init; }
    }

    /// <summary>An Every choice in words: "1 minute", "5 minutes", "1 hour", "2 hours".</summary>
    internal static string EveryText(int minutes) => minutes == 1 ? "1 minute" : minutes < 60 ? $"{minutes} minutes"
        : minutes == 60 ? "1 hour" : $"{minutes / 60} hours";

    // ---------- what the page says ----------

    /// <summary>The page's Now line: how many check-ins are on and where they run, or why they can't.</summary>
    internal string NowText()
    {
        var on = All.Count(c => c.On);
        var count = $"{on} check-in{(on == 1 ? " is" : "s are")} on";
        if (!services.IsCompanion) return $"{count}, but check-ins run only on a companion PC, and this PC is a Martlet host.";
        if (!services.CanTalk) return $"{count}, but they can't run: Martlet can't talk on this PC.";
        if (services.UsesFixture) return $"{count}. FIXTURE - NOT AI: {MainWindow.CheckInFixtureVariable} answers them, not the Thinking pool.";
        var pool = services.Pool!;
        if (pool.Find(ThinkingJobKind.CheckIn) is not { } member)
            return $"{count}, but they wait: the Thinking pool has no member that can take them. Add one on Thinking pool.";
        var unable = All.Where(c => c.On && !pool.CanRun(ThinkingJobKind.CheckIn, c.Needs, c.RunsOn)).ToArray();
        return $"{count}. They run on the Thinking pool ({member.Name}{(member.Model is { } model ? ", " + model : "")} first), never on the " +
            "conversation's own Thinking model." + (unable.Length == 0 ? ""
                : $" {string.Join(", ", unable.Select(c => c.Name))} {(unable.Length == 1 ? "waits" : "wait")} for a member that can take " +
                  $"{(unable.Length == 1 ? CheckIns.Describe(unable[0].Needs) : "what they need")}.");
    }

    /// <summary>The Now card's second line (CheckInsLast): the last check-in that ran since Martlet started.</summary>
    internal string LastText() => services.LastRun is { } done
        ? $"Last check-in: {done.Name} at {done.Run.At.ToLocalTime():t}{(done.Run.Member is { } member ? " on " + member : "")}: {done.Run.Result}."
        : "No check-in has run since Martlet started.";

    /// <summary>A check-in's status line: running, off, why it waits or that it runs at the next chance, then its last run.</summary>
    internal string Line(string id)
    {
        if (All.FirstOrDefault(c => c.Id == id) is not { } checkIn) return "";
        var state = services.RunState(id);
        var now = state.Running ? "Checking now."
            : !checkIn.On ? "Off."
            : state.Wait is { Length: > 0 } wait ? $"Waits: {wait}."
            : "Runs at the next chance.";
        if (state.Fired is { } fired && !state.TriggeredRunning)
            now += $" A trigger fired at {fired.At.ToLocalTime():t} ({fired.What}).";
        if (state.Last is not { } last) return now + " It hasn't run since Martlet started.";
        return $"{now} Last at {last.At.ToLocalTime():t}{(last.Member is { } member ? " on " + member : "")}" +
            $"{(last.Trigger is { } trigger ? $" (started by {trigger})" : "")}: {last.Result}. " +
            (last.Tools.Count > 0 ? $"Tools it called: {CheckInPrompts.ToolsText(last.Tools)}. " : "") +
            $"{state.Runs} run{(state.Runs == 1 ? "" : "s")} since Martlet started, {state.Acted} acted on.";
    }

    // ---------- what a card holds ----------

    /// <summary>What a built-in check-in's card starts with.</summary>
    internal CheckInEdit EditOf(CheckIn checkIn) => new(checkIn.Facts, checkIn.Conditions, checkIn.Outcome,
        Choices.Choice(checkIn.Id)?.Needs ?? ThinkingCapability.Text, checkIn.Screenshot, checkIn.Recording, checkIn.RecordingSeconds,
        checkIn.Script ?? "", checkIn.ToolSets)
    {
        Triggers = checkIn.Triggers, FromHour = checkIn.FromHour, UntilHour = checkIn.UntilHour, MostPerHour = checkIn.MostPerHour,
        RunsOn = checkIn.RunsOn
    };

    /// <summary>What one of your own check-ins' rows starts with.</summary>
    internal static CheckInEdit EditOf(CustomCheckIn custom) => new(custom.Facts, custom.Conditions, custom.Outcome,
        custom.Needs, custom.Screenshot, custom.Recording, custom.RecordingSeconds, custom.Script, custom.ToolSets)
    {
        Triggers = custom.Triggers, FromHour = custom.FromHour, UntilHour = custom.UntilHour, MostPerHour = custom.MostPerHour,
        RunsOn = custom.RunsOn
    };

    /// <summary>A built-in check-in's card as check-ins.json keeps it: on, its Every (<paramref name="everyIndex"/> in
    /// <see cref="CheckIns.EveryChoices"/>) and only what differs from Martlet's own, so a later Martlet can improve the rest.</summary>
    internal static CheckInChoice BuiltInChoice(string id, bool on, int everyIndex, CheckInEdit edit)
    {
        var standard = CheckIns.BuiltIn.First(b => b.Id == id);
        return new CheckInChoice(on, CheckIns.EveryChoices[Math.Max(0, everyIndex)])
        {
            Facts = edit.Facts == standard.Facts ? null : edit.Facts,
            Conditions = edit.Conditions == standard.Conditions ? null : edit.Conditions,
            Outcome = edit.Outcome == standard.Outcome ? null : edit.Outcome,
            Needs = edit.Needs == ThinkingCapability.Text ? null : edit.Needs,
            Screenshot = edit.Screenshot == standard.Screenshot ? null : edit.Screenshot,
            Recording = edit.Recording == standard.Recording ? null : edit.Recording,
            RecordingSeconds = edit.RecordingSeconds == standard.RecordingSeconds ? null : edit.RecordingSeconds,
            Script = edit.Script == (standard.Script ?? "") ? null : edit.Script,
            Triggers = edit.Triggers == standard.Triggers ? null : edit.Triggers,
            FromHour = edit.FromHour == standard.FromHour ? null : edit.FromHour,
            UntilHour = edit.UntilHour == standard.UntilHour ? null : edit.UntilHour,
            MostPerHour = edit.MostPerHour == standard.MostPerHour ? null : edit.MostPerHour,
            ToolSets = edit.ToolSets.SequenceEqual(standard.ToolSets) ? null : edit.ToolSets,
            RunsOn = edit.RunsOn
        };
    }

    /// <summary>One of your own check-ins as its row holds it; an empty name is "My check-in".</summary>
    internal static CustomCheckIn CustomOf(string id, string name, bool on, int everyIndex, string task, CheckInEdit edit) => new()
    {
        Id = id, Name = name.Trim() is { Length: > 0 } named ? named : "My check-in", On = on,
        EveryMinutes = CheckIns.EveryChoices[Math.Max(0, everyIndex)],
        Task = task.Replace("\r\n", "\n", StringComparison.Ordinal), Facts = edit.Facts, Conditions = edit.Conditions,
        Outcome = edit.Outcome, Needs = edit.Needs, Screenshot = edit.Screenshot, Recording = edit.Recording,
        RecordingSeconds = edit.RecordingSeconds, Script = edit.Script, Triggers = edit.Triggers, FromHour = edit.FromHour, UntilHour = edit.UntilHour,
        MostPerHour = edit.MostPerHour, ToolSets = edit.ToolSets, RunsOn = edit.RunsOn
    };

    // ---------- the changes the page saves ----------

    /// <summary>Saves what the cards hold (<paramref name="builtIns"/> and your own <paramref name="rows"/>) into check-ins.json,
    /// when it differs from what is saved.</summary>
    internal void SaveEdits(IEnumerable<(string Id, Func<CheckInChoice> Read)> builtIns, IEnumerable<Func<CustomCheckIn>> rows)
    {
        var saved = Choices;
        var next = saved;
        foreach (var (id, read) in builtIns)
        {
            var choice = read();
            var standard = CheckIns.BuiltIn.First(b => b.Id == id);
            if (choice != (next.Choice(id) ?? new CheckInChoice(standard.On, standard.EveryMinutes))) next = next.With(id, choice);
        }
        CustomCheckIn[] custom = [.. rows.Select(read => read())];
        if (!custom.SequenceEqual(next.Custom)) next = next with { Custom = custom };
        if (!ReferenceEquals(next, saved)) services.Save(next, null);
    }

    /// <summary>Use built-in settings: puts Martlet's own facts, conditions, answer, inputs and tools for
    /// <paramref name="checkIn"/> back (On and Every stay), then draws the page again.</summary>
    internal void UseBuiltIn(CheckIn checkIn)
    {
        var id = checkIn.Id;
        var saved = Choices;
        var kept = saved.Choice(id) is { } choice ? new CheckInChoice(choice.On, choice.EveryMinutes) : null;
        var next = kept is null ? saved : saved.With(id, kept);
        if (ReferenceEquals(next, saved) || services.Save(next, null))
        {
            services.ShowStatus($"{checkIn.Name} uses Martlet's own settings again.");
            services.Redraw();
        }
    }

    /// <summary>Copy as your own: a check-in of your own with <paramref name="id"/>'s prompt (as it is now, built-in or edited) and
    /// every choice, off until you turn it on.</summary>
    internal void Copy(string id)
    {
        var saved = Choices;
        if (CheckIns.All(saved).FirstOrDefault(c => c.Id == id) is not { } current) return;
        if (saved.Custom.Count >= CheckIns.MaximumCustom || saved.NewId() is not { } newId)
        {
            services.ShowStatus($"{current.Name} not copied: you have {CheckIns.MaximumCustom} check-ins of your own, the most Martlet " +
                "keeps. Remove one first.");
            return;
        }
        var name = current.Name + " (copy)";
        var task = CheckInPrompts.Template(current, services.Prompts) ?? "";
        var copy = new CustomCheckIn
        {
            Id = newId, Name = name.Length > CheckIns.MaximumNameCharacters ? name[..CheckIns.MaximumNameCharacters] : name, On = false,
            EveryMinutes = current.EveryMinutes,
            Task = task.Length > CheckIns.MaximumTaskCharacters ? task[..CheckIns.MaximumTaskCharacters] : task,
            Facts = current.Facts, Conditions = current.Conditions, Outcome = current.Outcome,
            Needs = current.Custom ? saved.Custom.First(c => c.Id == id).Needs : saved.Choice(id)?.Needs ?? ThinkingCapability.Text,
            Screenshot = current.Screenshot, Recording = current.Recording, RecordingSeconds = current.RecordingSeconds,
            Script = current.Script ?? "", Triggers = current.Triggers, FromHour = current.FromHour, UntilHour = current.UntilHour,
            MostPerHour = current.MostPerHour, ToolSets = current.ToolSets, RunsOn = current.RunsOn
        };
        if (services.Save(saved.With(copy), $"Copied {current.Name} as {copy.Name}, a check-in of your own. It's off until you turn it on."))
            services.Redraw();
    }

    /// <summary>Add a check-in: a new check-in of your own, off, then draws the page again.</summary>
    internal void AddOwn()
    {
        var saved = Choices;
        if (saved.Custom.Count >= CheckIns.MaximumCustom || saved.NewId() is not { } id) return;
        var custom = new CustomCheckIn
        {
            Id = id, Name = "My check-in", On = false, EveryMinutes = 30, Task = "",
            Facts = CheckInFacts.Conversation | CheckInFacts.Persona, Outcome = CheckInOutcome.Note
        };
        if (services.Save(saved.With(custom), "Added a check-in. Write what it checks, then turn it on.")) services.Redraw();
    }

    /// <summary>Remove: takes your own check-in <paramref name="id"/> out, forgets its runs, then draws the page again.</summary>
    internal void Remove(string id)
    {
        if (!services.Save(Choices.Without(id), "Removed the check-in.")) return;
        // A new check-in may get its ID again; it starts with no runs.
        services.Forget(id);
        services.Redraw();
    }
}
