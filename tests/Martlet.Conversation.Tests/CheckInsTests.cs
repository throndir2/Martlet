using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Conversation.Tests;

public sealed partial class CheckInsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 22, 17, 0, TimeSpan.FromHours(-7));

    private static CheckIn Built(string id) => CheckIns.All(null).Single(c => c.Id == id);

    private static CheckInState State() => new()
    {
        Now = Now, Name = "Mira", Persona = "Mira is playful and teasing.", Conversation = true,
        Exchanges =
        [
            new("I beat the boss!", "{blush} No way, you did it!"),
            new("Remind me to check the oven in 10 minutes.", "Sure, I'll remind you in 10 minutes!"),
            new("Back to work.", "[pass]"),
            new("This bug is driving me crazy.", "Ooh, want to talk it through?")
        ],
        Exchanged = 4, Quiet = TimeSpan.FromMinutes(2), Away = TimeSpan.FromSeconds(30), CharacterShows = true,
        Emotes = [new("glasses", "when reading", TimeSpan.FromMinutes(9)), new("blush", "when flattered", TimeSpan.FromMinutes(14)),
            new("smile", "when happy", TimeSpan.FromSeconds(30))],
        Gaze = new("look straight ahead and ignore the pointer", "follow the user's mouse pointer wherever it goes", TimeSpan.FromMinutes(12)),
        Work = ["Thinking longer (running): a trip plan"],
        Said =
        [
            new(Now.AddMinutes(-75), "Good evening!"),
            new(Now.AddMinutes(-30), "Ooh, that boss is almost down!"),
            new(Now.AddMinutes(-12), "Ooh, that boss is almost down!"),
            new(Now.AddMinutes(-2), "Ooh, that boss is almost down!")
        ]
    };

    [Fact]
    public void BuiltInCheckInsAreOnByDefaultAndFollowTheOwnersChoices()
    {
        var defaults = CheckIns.All(null);
        Assert.Equal([CheckIns.Emotes, CheckIns.Gaze, CheckIns.Promises, CheckIns.Character, CheckIns.Repeats, CheckIns.Reactions,
            CheckIns.Welcome, CheckIns.Unanswered, CheckIns.Call, CheckIns.Others, CheckIns.DescribeTouches, CheckIns.Actions], defaults.Select(c => c.Id));
        // The check-ins that wait for a signal are off until the owner turns them on.
        Assert.Equal([true, true, true, true, true, true, false, false, false, false, true, true], defaults.Select(c => c.On));
        Assert.Equal(15, defaults.Single(c => c.Id == CheckIns.Character).EveryMinutes);
        Assert.Equal((10, CheckInOutcome.Note, PromptCatalog.CheckInRepeats),
            defaults.Single(c => c.Id == CheckIns.Repeats) is var repeats ? (repeats.EveryMinutes, repeats.Outcome, repeats.PromptId) : default);
        // How I react runs after touches (at most every 5 minutes) and acts only through its Touch reactions tools.
        var reactions = defaults.Single(c => c.Id == CheckIns.Reactions);
        Assert.Equal((5, CheckInOutcome.Tools, PromptCatalog.CheckInReactions, CheckIns.ByTouches),
            (reactions.EveryMinutes, reactions.Outcome, reactions.PromptId, reactions.Triggers));
        Assert.Equal([TouchReactions.SetId], reactions.ToolSets);
        Assert.Equal(CheckInFacts.Persona | CheckInFacts.Conversation | CheckInFacts.Touches,
            reactions.Facts | CheckIns.Placed(PromptSettings.Text(null, PromptCatalog.CheckInReactions)));
        Assert.Same(TouchReactions.Set, CheckInToolSets.Find(TouchReactions.SetId));
        Assert.Same(MemoryToolSet.Set, CheckInToolSets.Find(MemoryToolSet.Id));
        Assert.Equal(["manage_memories"], MemoryToolSet.Set.Replaces);
        var settings = new CheckInSettings().With(CheckIns.Gaze, false, 30)
            .With(new CustomCheckIn { Id = "c1", Name = "Breaks", On = true, EveryMinutes = 60, Task = "Suggest a break.", Outcome = CheckInOutcome.Say });
        var all = CheckIns.All(settings);
        Assert.Equal(13, all.Count);
        Assert.Equal((false, 30), (all[1].On, all[1].EveryMinutes));
        Assert.True(all[12].Custom);
        Assert.Equal(CheckInOutcome.Say, all[12].Outcome);
        Assert.Equal("check-in-promises", CheckIns.Source(CheckIns.Promises));
        Assert.True(ContextBoard.IsSource(CheckIns.Source(CheckIns.Character)));
        Assert.True(ContextBoard.IsSource(CheckIns.Source(CheckIns.Repeats)));
        new CheckInSettings().With(CheckIns.Repeats, false, 30).Validate();
    }

    [Fact]
    public void CheckInJobsRunAtTheHelpersPriorityAndYieldToTheConversation()
    {
        Assert.Equal("check-in", ThinkingJobKinds.Name(ThinkingJobKind.CheckIn));
        Assert.Equal(ThinkingPriority.Helper, ThinkingJobKinds.Priority(ThinkingJobKind.CheckIn));
        Assert.False(ThinkingJobKinds.IsFast(ThinkingJobKind.CheckIn));
        Assert.True(LiveFloorRules.Stops(ThinkingJobKind.CheckIn));
        Assert.False(LiveFloorRules.ServesTheTurn(ThinkingJobKind.CheckIn));

        var job = CheckIns.Prepare(Built(CheckIns.Emotes), CheckIns.Focus(Built(CheckIns.Emotes), State()), null)!;
        job.Validate();
        Assert.Equal(ThinkingJobKind.CheckIn, job.Kind);
        Assert.Equal(ThinkingCapability.Text, job.Needs);
        Assert.True(job.DropWhenStale);
        Assert.False(job.Reasoning);
        Assert.Equal(PromptCatalog.DefaultCheckInInstructions, job.Instructions);
    }

    [Fact]
    public void WaitsUntilACheckInHasSomethingToCheck()
    {
        var state = State();
        Assert.Null(CheckIns.Wait(Built(CheckIns.Emotes), state, null));
        Assert.Equal("it's off", CheckIns.Wait(Built(CheckIns.Emotes) with { On = false }, state, null));
        Assert.Equal("next in 3 min", CheckIns.Wait(Built(CheckIns.Emotes), state, new(Now.AddMinutes(-2), 4, "every emote still fits", false)));
        Assert.Equal("the character isn't showing", CheckIns.Wait(Built(CheckIns.Emotes), state with { CharacterShows = false }, null));
        Assert.Equal("no emote a reply turned on is showing", CheckIns.Wait(Built(CheckIns.Emotes), state with { Emotes = [] }, null));
        Assert.Contains("3 min", CheckIns.Wait(Built(CheckIns.Emotes), state with { Emotes = [state.Emotes[2]] }, null));
        Assert.Equal("the conversation is busy", CheckIns.Wait(Built(CheckIns.Emotes), state with { Quiet = TimeSpan.FromSeconds(3) }, null));
        Assert.Equal("nobody used this PC for 10 min", CheckIns.Wait(Built(CheckIns.Emotes), state with { Away = TimeSpan.FromMinutes(11) }, null));

        Assert.Null(CheckIns.Wait(Built(CheckIns.Gaze), state, null));
        Assert.Equal("the eyes do their usual", CheckIns.Wait(Built(CheckIns.Gaze), state with { Gaze = null }, null));
        Assert.NotNull(CheckIns.Wait(Built(CheckIns.Gaze), state with { Gaze = state.Gaze! with { Since = TimeSpan.FromMinutes(1) } }, null));

        Assert.Null(CheckIns.Wait(Built(CheckIns.Promises), state, new(Now.AddMinutes(-10), 3, "nothing to remind Martlet of", false)));
        Assert.Equal("nothing new was said since the last check",
            CheckIns.Wait(Built(CheckIns.Promises), state, new(Now.AddMinutes(-10), 4, "nothing to remind Martlet of", false)));
        Assert.NotNull(CheckIns.Wait(Built(CheckIns.Promises), state with { Quiet = TimeSpan.FromHours(1) }, null));
        Assert.NotNull(CheckIns.Wait(Built(CheckIns.Promises), state with { Exchanges = [] }, null));

        Assert.Null(CheckIns.Wait(Built(CheckIns.Character), state, null));
        Assert.Equal("it waits for 4 new replies",
            CheckIns.Wait(Built(CheckIns.Character), state with { Exchanged = 6 }, new(Now.AddMinutes(-20), 4, "nothing to remind Martlet of", false)));
        Assert.NotNull(CheckIns.Wait(Built(CheckIns.Character), state with { Persona = " " }, null));

        // Saying the same things reads what was said in the last hour, once something new was said.
        Assert.Null(CheckIns.Wait(Built(CheckIns.Repeats), state, null));
        Assert.Equal("next in 4 min", CheckIns.Wait(Built(CheckIns.Repeats), state with { Exchanged = 6 },
            new(Now.AddMinutes(-6), 4, "nothing to remind Martlet of", false)));
        Assert.Null(CheckIns.Wait(Built(CheckIns.Repeats), state with { Exchanged = 6 }, new(Now.AddMinutes(-11), 4, "nothing to remind Martlet of", false)));
        Assert.Equal("nothing new was said since the last check",
            CheckIns.Wait(Built(CheckIns.Repeats), state, new(Now.AddMinutes(-11), 4, "nothing to remind Martlet of", false)));
        Assert.Equal("it needs at least 3 things Martlet said in the last hour",
            CheckIns.Wait(Built(CheckIns.Repeats), state with { Said = [.. state.Said.Take(3)] }, null));
        Assert.Equal("the conversation is busy", CheckIns.Wait(Built(CheckIns.Repeats), state with { Quiet = TimeSpan.Zero }, null));

        var own = CheckIns.Of(new() { Id = "c1", Name = "Breaks", Task = "" });
        Assert.Equal("its prompt is empty", CheckIns.Wait(own, state, null, now: true));
        // Check now runs a check-in that is off, not due and young, as long as it has something to check.
        var recent = new CheckInRun(Now.AddSeconds(-30), 4, "every emote still fits", false);
        Assert.Null(CheckIns.Wait(Built(CheckIns.Emotes) with { On = false }, state with { Emotes = [state.Emotes[2]], Quiet = TimeSpan.Zero }, recent, now: true));

        // After an answer that kept everything, with nothing new said, the emotes and the gaze wait three times their pace.
        var kept = new CheckInRun(Now.AddMinutes(-6), 4, "every emote still fits", false) { Kept = true };
        Assert.Equal("next in 9 min", CheckIns.Wait(Built(CheckIns.Emotes), state, kept));
        Assert.Equal(TimeSpan.FromMinutes(15), CheckIns.Pace(Built(CheckIns.Gaze), kept, 4));
        Assert.Null(CheckIns.Wait(Built(CheckIns.Emotes), state with { Exchanged = 5 }, kept));
        Assert.Null(CheckIns.Wait(Built(CheckIns.Emotes), state, kept with { Kept = false }));
        Assert.Equal(TimeSpan.FromMinutes(5), CheckIns.Pace(Built(CheckIns.Promises), kept, 4));
    }

    [Fact]
    public void MessagesCarryOnlyTheFactsEachCheckNeeds()
    {
        var state = State();
        var emotes = CheckIns.Focus(Built(CheckIns.Emotes), state);
        Assert.Equal(["blush", "glasses"], emotes.Emotes.Select(e => e.Tag));
        var text = CheckIns.Message(Built(CheckIns.Emotes), emotes, null)!;
        Assert.Contains("{blush} - when flattered (on for 14 min)", text);
        Assert.Contains("OFF {blush}", text);
        Assert.DoesNotContain("{smile}", text);
        Assert.Contains("Mira: {blush} No way, you did it!", text);
        Assert.Contains("It has been quiet for 2 min since.", text);
        Assert.Contains("Wednesday, October 7, 10:17 PM", text);
        Assert.DoesNotContain("\n\n\n", text);

        var character = CheckIns.Message(Built(CheckIns.Character), state, null)!;
        Assert.Contains("Mira is playful and teasing.", character);
        Assert.Contains("3. Ooh, want to talk it through?", character);
        Assert.DoesNotContain("[pass]", character);

        var promises = CheckIns.Message(Built(CheckIns.Promises), state, null)!;
        Assert.Contains("- Thinking longer (running): a trip plan", promises);
        Assert.Contains("check the oven", promises);

        // Saying the same things gets what was said in the last hour, each with when, and nothing older.
        var repeats = CheckIns.Message(Built(CheckIns.Repeats), state, null)!;
        Assert.Contains("Mira, the user's desktop companion, said these things lately", repeats);
        Assert.Contains("- 9:47 PM (30 min ago): \"Ooh, that boss is almost down!\"\n- 10:05 PM (12 min ago): \"Ooh, that boss is almost down!\"\n" +
            "- 10:15 PM (2 min ago): \"Ooh, that boss is almost down!\"", repeats);
        Assert.DoesNotContain("Good evening!", repeats);
        Assert.Contains("It is Wednesday, October 7, 10:17 PM.", repeats);
        Assert.Contains("REMIND:", repeats);
        Assert.DoesNotContain("I beat the boss", repeats);
        Assert.Equal("it needs at least 3 things Martlet said in the last hour", CheckIns.Wait(Built(CheckIns.Repeats), state with { Said = [] }, null));

        var own = CheckIns.Of(new()
        {
            Id = "c1", Name = "Breaks", Task = "Suggest a break when it's late.", Facts = CheckInFacts.Presence | CheckInFacts.Screen, Outcome = CheckInOutcome.Say
        });
        var custom = CheckIns.Message(own, state with { Screen = "A code editor, then a browser." }, null)!;
        Assert.StartsWith("Suggest a break when it's late.", custom);
        Assert.Contains("The user is using this PC now.", custom);
        Assert.Contains("A code editor, then a browser.", custom);
        Assert.Contains("SAY:", custom);
        Assert.DoesNotContain("I beat the boss", custom);

        // An emptied prompt sends nothing, so the check-in doesn't run.
        var emptied = new PromptSettings { Overrides = new Dictionary<string, string> { [PromptCatalog.CheckInEmotes] = "" } };
        Assert.Null(CheckIns.Prepare(Built(CheckIns.Emotes), emotes, emptied));
        Assert.Equal("the eyes do their usual", CheckIns.Wait(Built(CheckIns.Gaze), state with { Gaze = null }, null, now: true));
    }

    [Theory]
    [InlineData("OFF {blush}", "blush")]
    [InlineData("off: blush, glasses.", "blush,glasses")]
    [InlineData("- **OFF {/glasses}**\nOFF {blush}", "glasses,blush")]
    [InlineData("<think>OFF {glasses} maybe? No.</think>\nOFF {blush}", "blush")]
    [InlineData("OFF {smile}", "")]
    [InlineData("KEEP", "")]
    public void ReadsWhichEmotesToTurnOff(string answer, string expected)
    {
        var state = CheckIns.Focus(Built(CheckIns.Emotes), State());
        var verdict = CheckIns.Read(Built(CheckIns.Emotes), answer, state);
        Assert.True(verdict.Readable);
        Assert.Equal(expected.Length > 0, verdict.Act);
        Assert.Equal(expected.Split(',', StringSplitOptions.RemoveEmptyEntries), verdict.Tags);
    }

    [Theory]
    [InlineData("USUAL", true, true)]
    [InlineData("Usually they follow the mouse, but...\nKEEP", false, true)]
    [InlineData("Keep in mind the bug.\n`USUAL`", true, true)]
    [InlineData("I think the eyes are fine.", false, false)]
    public void ReadsWhetherTheEyesGoBack(string answer, bool act, bool readable)
    {
        var verdict = CheckIns.Read(Built(CheckIns.Gaze), answer, State());
        Assert.Equal((act, readable), (verdict.Act, verdict.Readable));
    }

    [Theory]
    [InlineData("REMIND: Set the oven reminder now.", "Set the oven reminder now.")]
    [InlineData("Reminder - \"Set the oven reminder now.\"", "Set the oven reminder now.")]
    [InlineData("OK", null)]
    [InlineData("REMIND: nothing.", null)]
    [InlineData("OK so let me look.\nREMIND: Keep it short.", "Keep it short.")]
    public void ReadsAReminderForTheNextReply(string answer, string? expected)
    {
        var verdict = CheckIns.Read(Built(CheckIns.Promises), answer, State());
        Assert.True(verdict.Readable);
        Assert.Equal(expected, verdict.Text);
        Assert.Equal(expected is not null, verdict.Act);
        Assert.False(CheckIns.Read(Built(CheckIns.Promises), "Everything looks fine to me!", State()).Readable);

        var say = CheckIns.Of(new() { Id = "c1", Name = "Breaks", Task = "x", Outcome = CheckInOutcome.Say });
        Assert.Equal("Time for a stretch.", CheckIns.Read(say, "SAY: Time for a stretch.", State()).Text);
        Assert.False(CheckIns.Read(say, "REMIND: Time for a stretch.", State()).Act);
    }

    [Fact]
    public void ACheckInsReminderGoesWithOneRequestOnly()
    {
        var board = new ContextBoard();
        var note = CheckIns.Note(null, "Set the oven reminder now.")!;
        Assert.Contains("A reminder from your own check-in, for you only: Set the oven reminder now.", note);
        board.Post(CheckIns.Source(CheckIns.Promises), note, Now, CheckIns.NoteAge, consume: true);
        var sent = board.Snapshot(Now.AddMinutes(1));
        Assert.Contains("check-in-promises", sent.Sources);
        board.MarkSent(sent);
        Assert.DoesNotContain("check-in-promises", board.Snapshot(Now.AddMinutes(2)).Sources);
        Assert.Null(CheckIns.Note(new PromptSettings { Overrides = new Dictionary<string, string> { [PromptCatalog.CheckInNote] = "" } }, "x"));
    }

    [Theory]
    [InlineData("KNOW: Her tail is still curled from the long stroke down her back.", "Her tail is still curled from the long stroke down her back.")]
    [InlineData("<think>Touches on the tail and hips.</think>\n- **KNOW:** \"She leans into the touch.\"", "She leans into the touch.")]
    [InlineData("OK", null)]
    [InlineData("KNOW: nothing to add.", null)]
    [InlineData("KNOW: nothing", null)]
    public void ReadsWhatACheckInAddsToWhatMartletKnows(string answer, string? expected)
    {
        var describes = CheckIns.Of(new() { Id = "c1", Name = "Describes", Task = "Describe the moment.", Outcome = CheckInOutcome.Context });
        var verdict = CheckIns.Read(describes, answer, State());
        Assert.True(verdict.Readable);
        Assert.Equal(expected, verdict.Text);
        Assert.Equal(expected is not null, verdict.Act);
        Assert.False(CheckIns.Read(describes, "REMIND: Be gentle.", State()).Act);
        Assert.False(CheckIns.Read(describes, "It all looks calm.", State()).Readable);
        Assert.False(CheckIns.Read(Built(CheckIns.Promises), "KNOW: She leans into the touch.", State()).Act);
    }

    [Fact]
    public void ACheckInsContextAsksForAKnowLineAndGoesWithOneFreshRequestOnly()
    {
        var describes = CheckIns.Of(new() { Id = "c1", Name = "Describes", Task = "Describe the moment.", Outcome = CheckInOutcome.Context });
        Assert.Contains("adds to what Martlet knows", describes.Does);
        var message = CheckIns.Message(describes, State(), null)!;
        Assert.Contains("If there is nothing worth adding, write only: OK", message);
        Assert.Contains("one line that starts with KNOW: and describes what is happening, briefly and vividly, as background Mira", message);
        Assert.DoesNotContain("REMIND:", message);

        var context = CheckIns.Context(null, "She leans into the touch.")!;
        Assert.Contains("What is happening now, from your own check-in, for you only: She leans into the touch.", context);
        Assert.Contains("not a reminder to follow", context);
        var board = new ContextBoard();
        board.Post(CheckIns.Source("c1"), context, Now, CheckIns.ContextAge, consume: true);
        var sent = board.Snapshot(Now.AddMinutes(1));
        Assert.Contains("check-in-c1", sent.Sources);
        board.MarkSent(sent);
        Assert.DoesNotContain("check-in-c1", board.Snapshot(Now.AddMinutes(1.5)).Sources);
        board.Post(CheckIns.Source("c1"), context, Now, CheckIns.ContextAge, consume: true);
        Assert.DoesNotContain("check-in-c1", board.Snapshot(Now + CheckIns.ContextAge + TimeSpan.FromSeconds(1)).Sources);
        Assert.True(CheckIns.ContextAge < CheckIns.NoteAge);
        Assert.Null(CheckIns.Context(new PromptSettings { Overrides = new Dictionary<string, string> { [PromptCatalog.CheckInContext] = "" } }, "x"));
        new CheckInSettings().With(CheckIns.Promises, new CheckInChoice(true, 5) { Outcome = CheckInOutcome.Context })
            .With(new CustomCheckIn { Id = "c1", Name = "Describes", Outcome = CheckInOutcome.Context }).Validate();
        Assert.Equal(4, (int)CheckInOutcome.Context);
    }

    [Fact]
    public async Task WhatACheckInBringsUpIsWordedAsItsOwnBesideADueReminder()
    {
        using var jobs = new BackgroundJobs();
        var said = jobs.Start(CheckIns.SayKind, "Breaks", (_, _) => Task.FromResult(BackgroundJobOutcome.Done("Time for a stretch."))).Job!;
        var due = jobs.Start(Reminders.Kind, "oven", (_, _) => Task.FromResult(BackgroundJobOutcome.Done("check the oven"))).Job!;
        for (var i = 0; i < 300 && !(said.Finished && due.Finished); i++) await Task.Delay(10);
        Assert.True(jobs.HasNotice);

        var notes = BackgroundJobs.ReportNotes(null, jobs.Undelivered)!;
        Assert.Contains("A reminder the user asked you for is due now:\n- check the oven", notes);
        Assert.Contains("Your own check-in came up with something to bring up:\n- Time for a stretch.", notes);

        var message = BackgroundJobs.ReportMessage(null, jobs.Undelivered).UserText;
        Assert.Contains("a reminder they asked you for is due now.)\n- check the oven", message);
        Assert.Contains("your own check-in came up with something to bring up.)\n- Time for a stretch.", message);
        Assert.True(message.IndexOf("check the oven", StringComparison.Ordinal) < message.IndexOf("Time for a stretch.", StringComparison.Ordinal));

        // Only a reminder: worded exactly as before.
        using var alone = new BackgroundJobs();
        var reminder = alone.Start(Reminders.Kind, "oven", (_, _) => Task.FromResult(BackgroundJobOutcome.Done("check the oven"))).Job!;
        for (var i = 0; i < 300 && !reminder.Finished; i++) await Task.Delay(10);
        Assert.Equal(PromptSettings.Fill(null, PromptCatalog.ReminderDue, ("reminders", "- check the oven")),
            BackgroundJobs.ReportMessage(null, alone.Undelivered).UserText);
    }

    [Fact]
    public void SettingsSaveReadBackAndRefuseWhatMartletCantRun()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Martlet.CheckInsTests." + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal("none", CheckInSettings.Read(folder).State);
            var settings = new CheckInSettings().With(CheckIns.Emotes, false, 2)
                .With(new CustomCheckIn
                {
                    Id = "c1", Name = "Breaks", On = true, EveryMinutes = 120, Task = "Line one\nline two",
                    Facts = CheckInFacts.Conversation | CheckInFacts.Presence, Outcome = CheckInOutcome.Say
                });
            Assert.True(settings.Save(folder));
            var (read, state) = CheckInSettings.Read(folder);
            Assert.Equal("loaded", state);
            Assert.Equal(new CheckInChoice(false, 2), read.Choice(CheckIns.Emotes));
            var own = Assert.Single(read.Custom);
            Assert.Equal(("Breaks", 120, "Line one\nline two", CheckInFacts.Conversation | CheckInFacts.Presence, CheckInOutcome.Say),
                (own.Name, own.EveryMinutes, own.Task, own.Facts, own.Outcome));
            Assert.Equal("c2", read.NewId());
            Assert.Empty(read.Without("c1").Custom);
            Assert.Equal("Renamed", read.With(own with { Name = "Renamed" }).Custom.Single().Name);

            File.WriteAllText(Path.Combine(folder, CheckInSettings.FileName), "{ not json");
            var (fallback, unreadable) = CheckInSettings.Read(folder);
            Assert.Equal("unreadable", unreadable);
            Assert.Equal(CheckIns.All(null).Select(c => c.On), CheckIns.All(fallback).Select(c => c.On));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }

        Assert.Throws<ContractException>(() => new CheckInSettings().With(CheckIns.Emotes, true, 7).Validate());
        Assert.Throws<ContractException>(() => new CheckInSettings().With("nope", true, 5).Validate());
        Assert.Throws<ContractException>(() => new CheckInSettings().With(new CustomCheckIn { Id = "x1", Name = "Bad id" }).Validate());
        Assert.Throws<ContractException>(() => new CheckInSettings().With(new CustomCheckIn { Id = "c1", Name = " " }).Validate());
        new CheckInSettings().With(new CustomCheckIn
        {
            Id = "c1", Name = "Emotes", Outcome = CheckInOutcome.EmotesOff, Conditions = CheckInConditions.CharacterShows | CheckInConditions.EmoteShown,
            Facts = CheckInFacts.Character | CheckInFacts.Said | CheckInFacts.Replies
        }).Validate();
        Assert.Throws<ContractException>(() =>
            new CheckInSettings().With(new CustomCheckIn { Id = "c1", Name = "Bad", Conditions = (CheckInConditions)(1 << 20) }).Validate());
        Assert.Throws<ContractException>(() =>
            new CheckInSettings().With(CheckIns.Emotes, new CheckInChoice(true, 5) { Outcome = (CheckInOutcome)9 }).Validate());
        Assert.Throws<ContractException>(() =>
            new CheckInSettings().With(CheckIns.Emotes, new CheckInChoice(true, 5) { RecordingSeconds = 7 }).Validate());
        var full = Enumerable.Range(1, CheckIns.MaximumCustom + 1)
            .Aggregate(new CheckInSettings(), (all, n) => all.With(new CustomCheckIn { Id = "c" + n, Name = "Check " + n }));
        Assert.Throws<ContractException>(full.Validate);
    }

    [Fact]
    public void CheckInPromptsAreInTheirOwnGroupAndTheBroughtUpMessageCantBeEmptied()
    {
        string[] ids = [PromptCatalog.CheckIn, PromptCatalog.CheckInEmotes, PromptCatalog.CheckInGaze, PromptCatalog.CheckInPromises,
            PromptCatalog.CheckInCharacter, PromptCatalog.CheckInRepeats, PromptCatalog.CheckInWelcome, PromptCatalog.CheckInUnanswered,
            PromptCatalog.CheckInCall, PromptCatalog.CheckInOthers, PromptCatalog.CheckInCustom, PromptCatalog.CheckInNote,
            PromptCatalog.CheckInContext, PromptCatalog.CheckInDue, PromptCatalog.CheckInDueNotes, PromptCatalog.CheckInAdultOn,
            PromptCatalog.CheckInAdultOff, PromptCatalog.CheckInTouches];
        foreach (var id in ids)
        {
            var prompt = PromptCatalog.Find(id)!;
            Assert.Equal(PromptCatalog.CheckInGroup, prompt.Group);
            foreach (var placeholder in prompt.Placeholders) Assert.Contains("{" + placeholder + "}", prompt.Default);
        }
        Assert.True(PromptCatalog.Required(PromptCatalog.CheckInDue));
        Assert.Throws<ContractException>(() =>
            new PromptSettings { Overrides = new Dictionary<string, string> { [PromptCatalog.CheckInDue] = " " } }.Validate());
    }

    private static CustomCheckIn Inputs() => new()
    {
        Id = "c1", Name = "Inputs", On = true, EveryMinutes = 30, Task = "Check what the user does.", Facts = CheckInFacts.None,
        Needs = ThinkingCapability.Text | ThinkingCapability.Audio, Screenshot = true, Recording = CheckInRecording.Microphone,
        RecordingSeconds = 15, Script = "Get-Process | Select-Object -First 3"
    };

    [Fact]
    public void AnOwnCheckInNeedsWhatItsInputsNeed()
    {
        Assert.Equal(ThinkingCapability.Text, CheckIns.Of(new CustomCheckIn { Id = "c1", Name = "Plain" }).Needs);
        Assert.Equal(ThinkingCapability.Text | ThinkingCapability.Vision | ThinkingCapability.Audio, CheckIns.Of(Inputs()).Needs);
        // The owner's choice alone, and a screenshot alone.
        Assert.Equal(ThinkingCapability.Text | ThinkingCapability.Audio,
            CheckIns.Of(Inputs() with { Screenshot = false, Recording = CheckInRecording.None }).Needs);
        Assert.Equal(ThinkingCapability.Text | ThinkingCapability.Vision,
            CheckIns.Of(Inputs() with { Needs = ThinkingCapability.Text, Recording = CheckInRecording.None }).Needs);
        Assert.Equal("text", CheckIns.Describe(ThinkingCapability.Text));
        Assert.Equal("text and pictures", CheckIns.Describe(ThinkingCapability.Text | ThinkingCapability.Vision));
        Assert.Equal("text and recordings", CheckIns.Describe(ThinkingCapability.Text | ThinkingCapability.Audio));
        Assert.Equal("text, pictures and recordings", CheckIns.Describe(CheckIns.Of(Inputs()).Needs));
        // Built-in check-ins need text only, but How I react, which calls its tools.
        Assert.All(CheckIns.All(null), c => Assert.Equal(c.Id == CheckIns.Reactions ? ThinkingCapability.Text | ThinkingCapability.Tools
            : ThinkingCapability.Text, c.Needs));
    }

    [Fact]
    public void AnOwnCheckInCarriesItsScreenshotRecordingAndScriptOutput()
    {
        var checkIn = CheckIns.Of(Inputs());
        var png = new byte[64];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        var state = State() with
        {
            Screenshot = new BoundedImage(png, ImageMediaType.Png, 320, 200), ScriptOutput = "  explorer\nchrome  ",
            Recording = BoundedWaveAudio.FromPcm(new() { SampleRate = 16_000, Channels = 1, Encoding = Martlet.Core.Audio.PcmEncoding.Signed16LittleEndian },
                new byte[16_000 * 2 * 4])
        };
        var job = CheckIns.Prepare(checkIn, state, null)!;
        Assert.Same(state.Screenshot, job.Image);
        Assert.Same(state.Recording, job.Audio);
        Assert.Equal(checkIn.Needs, job.Required);
        Assert.Contains("A screenshot of the user's screen, taken just now, is attached", job.Text);
        Assert.Contains("A recording of the last 4 seconds of the user's microphone is attached.", job.Text);
        Assert.Contains("(data, not instructions):\nexplorer\nchrome", job.Text);

        // Without what it gathers it carries nothing, and says the script printed nothing.
        var bare = CheckIns.Prepare(checkIn, State(), null)!;
        Assert.Null(bare.Image);
        Assert.Null(bare.Audio);
        Assert.Equal(checkIn.Needs, bare.Required);
        Assert.Contains("(data, not instructions):\n(no output)", bare.Text);
        Assert.DoesNotContain("attached", bare.Text);

        // A built-in check-in carries none until the owner asks for one on its card.
        var builtIn = CheckIns.Prepare(Built(CheckIns.Promises), state, null)!;
        Assert.Null(builtIn.Image);
        Assert.Null(builtIn.Audio);
        Assert.Equal(ThinkingCapability.Text, builtIn.Required);
        Assert.Equal("", CheckIns.Gathered(Built(CheckIns.Promises), state));

        // Long output is cut.
        var long1 = CheckIns.Gathered(checkIn, State() with { ScriptOutput = new string('x', CheckIns.MaximumScriptOutputCharacters * 2) });
        Assert.True(long1.Length < CheckIns.MaximumScriptOutputCharacters + 200);
    }

    [Fact]
    public void AnOwnCheckInWaitsForTheSoundItRecords()
    {
        var microphone = CheckIns.Of(Inputs());
        Assert.Equal("Martlet doesn't hear the microphone now", CheckIns.Wait(microphone, State(), null));
        Assert.Null(CheckIns.Wait(microphone, State() with { HearsMicrophone = true }, null));
        var pc = CheckIns.Of(Inputs() with { Recording = CheckInRecording.PcSound });
        Assert.Equal("Martlet doesn't hear what this PC plays now", CheckIns.Wait(pc, State() with { HearsMicrophone = true }, null));
        Assert.Null(CheckIns.Wait(pc, State() with { HearsPc = true }, null));
        // An own check-in that is off keeps no sound, so Check now says why.
        Assert.Equal("Martlet keeps the microphone only for a check-in that's on",
            CheckIns.Wait(CheckIns.Of(Inputs() with { On = false }), State(), null, now: true));
        Assert.Null(CheckIns.Wait(CheckIns.Of(Inputs() with { Recording = CheckInRecording.None }), State(), null));
    }

    [Fact]
    public void AScriptRunIsReadAsDataAndDescribedWithoutItsOutput()
    {
        Assert.Equal(("explorer", "a script (exit code 0, 0.4 s, 8 characters)"),
            CheckIns.ScriptRan(null, false, 0, "explorer", TimeSpan.FromMilliseconds(420)));
        Assert.Equal(("(the script ended with exit code 1)\noops", "a script (exit code 1, 1.0 s, 4 characters)"),
            CheckIns.ScriptRan(null, false, 1, "oops", TimeSpan.FromSeconds(1)));
        Assert.Equal(("(the script was stopped after 20 seconds)\npartial", "a script stopped after 20 s"),
            CheckIns.ScriptRan(null, true, null, "partial", CheckIns.ScriptTimeout));
        Assert.Equal(("(the script didn't run: no shell)", "a script that didn't run"),
            CheckIns.ScriptRan("no shell", false, null, "", TimeSpan.Zero));
    }

    [Fact]
    public void OwnCheckInInputsSaveReadBackAndRefuseWhatMartletCantRun()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Martlet.CheckInInputs." + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True(new CheckInSettings().With(Inputs()).Save(folder));
            var own = Assert.Single(CheckInSettings.Read(folder).Settings.Custom);
            Assert.Equal(Inputs(), own);

            // An older file without the new choices reads as text only, with no inputs.
            File.WriteAllText(Path.Combine(folder, CheckInSettings.FileName),
                """{ "Custom": [ { "Id": "c1", "Name": "Old", "Task": "Check." } ] }""");
            var old = Assert.Single(CheckInSettings.Read(folder).Settings.Custom);
            Assert.Equal((ThinkingCapability.Text, false, CheckInRecording.None, 10, ""),
                (old.Needs, old.Screenshot, old.Recording, old.RecordingSeconds, old.Script));
            Assert.False(CheckIns.Of(old).RunsScript);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }

        void Refused(CustomCheckIn bad) => Assert.Throws<ContractException>(() => new CheckInSettings().With(bad).Validate());
        Refused(Inputs() with { RecordingSeconds = 7 });
        Refused(Inputs() with { Recording = (CheckInRecording)9 });
        Refused(Inputs() with { Needs = (ThinkingCapability)8 });
        Refused(Inputs() with { Script = "Get-Date\0" });
        Refused(Inputs() with { Script = new string('x', CheckIns.MaximumScriptCharacters + 1) });
        Refused(Inputs() with { Script = null! });
        new CheckInSettings().With(Inputs() with { Needs = ThinkingCapability.None }).Validate();
    }

    [Fact]
    public void ACheckInCanRunEveryMinuteAndRecordAWholeMinute()
    {
        Assert.Equal(1, CheckIns.EveryChoices[0]);
        Assert.Equal(60, CheckIns.RecordingChoices[^1]);
        new CheckInSettings().With(CheckIns.Emotes, true, 1).Validate();
        new CheckInSettings().With(Inputs() with { EveryMinutes = 1, RecordingSeconds = 60 }).Validate();
        var slow = Assert.Throws<ContractException>(() => new CheckInSettings().With(Inputs() with { EveryMinutes = 3 }).Validate());
        Assert.Contains("every 1, 2, 5, 10, 15, 30, 60 or 120 minutes", slow.Message);
        var longer = Assert.Throws<ContractException>(() => new CheckInSettings().With(Inputs() with { RecordingSeconds = 90 }).Validate());
        Assert.Contains("5, 10, 15, 30 or 60 seconds", longer.Message);

        // A minute of the microphone fits a request; a conversation message still carries at most 30 seconds.
        var format = new Martlet.Core.Audio.PcmFormat { SampleRate = 16_000, Channels = 1, Encoding = Martlet.Core.Audio.PcmEncoding.Signed16LittleEndian };
        var minute = BoundedWaveAudio.FromPcm(format, new byte[16_000 * 2 * 60]);
        var job = CheckIns.Prepare(CheckIns.Of(Inputs() with { RecordingSeconds = 60 }), State() with { Recording = minute }, null)!;
        Assert.Contains("A recording of the last 60 seconds of the user's microphone is attached.", job.Text);
        Assert.Same(minute, new BoundedTextInput(job.Text, job.Instructions, audio: job.Audio).Audio);
        Assert.Throws<ContractException>(() =>
            new BoundedTextInput("x", audio: BoundedWaveAudio.FromPcm(format, new byte[16_000 * 2 * 61])));
        Assert.Equal(30, BoundedTextInput.MessageAudioSeconds);
    }

    public static TheoryData<string> BuiltInIds => [CheckIns.Emotes, CheckIns.Gaze, CheckIns.Promises, CheckIns.Character, CheckIns.Repeats,
        CheckIns.Reactions, CheckIns.Welcome, CheckIns.Unanswered, CheckIns.Call, CheckIns.Others, CheckIns.DescribeTouches];

    [Theory]
    [MemberData(nameof(BuiltInIds))]
    public void EveryBuiltInCheckInCanBeRecreatedAsYourOwn(string id)
    {
        var builtIn = Built(id) with { On = true };
        var copy = CheckIns.Of(new CustomCheckIn
        {
            Id = "c1", Name = builtIn.Name + " (copy)", On = true, EveryMinutes = builtIn.EveryMinutes, Task = CheckIns.Template(builtIn, null)!,
            Facts = builtIn.Facts, Conditions = builtIn.Conditions, Outcome = builtIn.Outcome, Triggers = builtIn.Triggers, ToolSets = builtIn.ToolSets
        });
        var state = State();
        Assert.Equal(CheckIns.Message(builtIn, CheckIns.Focus(builtIn, state), null), CheckIns.Message(copy, CheckIns.Focus(copy, state), null));
        var last = new CheckInRun(Now.AddMinutes(-6), 4, "nothing", false) { Kept = true };
        foreach (var s in new[]
        {
            state, state with { CharacterShows = false }, state with { Persona = null }, state with { Said = [] }, state with { Gaze = null },
            state with { Emotes = [] }, state with { Exchanges = [], Exchanged = 0 }, state with { Quiet = TimeSpan.FromMinutes(45) }
        })
        {
            Assert.Equal(CheckIns.Wait(builtIn, s, null), CheckIns.Wait(copy, s, null));
            Assert.Equal(CheckIns.Wait(builtIn, s, last), CheckIns.Wait(copy, s, last));
            Assert.Equal(CheckIns.Wait(builtIn, s, last, now: true), CheckIns.Wait(copy, s, last, now: true));
        }
        foreach (var answer in new[] { "OK", "KEEP", "USUAL", "OFF {blush}", "REMIND: Say something new.", "SAY: Take a break.", "chatter" })
            Assert.Equal(CheckIns.Read(builtIn, answer, CheckIns.Focus(builtIn, state)) is var a ? (a.Act, a.Readable, string.Join(",", a.Tags), a.Text) : default,
                CheckIns.Read(copy, answer, CheckIns.Focus(copy, state)) is var b ? (b.Act, b.Readable, string.Join(",", b.Tags), b.Text) : default);
        Assert.Equal(CheckIns.Prepare(builtIn, state, null)!.Needs, CheckIns.Prepare(copy, state, null)!.Needs);
    }

    [Fact]
    public void YourOwnCheckInCanKnowWhatMartletSaidInTheLastHourAndItsLastReplies()
    {
        var own = CheckIns.Of(new CustomCheckIn
        {
            Id = "c1", Name = "Repeats", On = true, Task = "Check whether Martlet repeats itself.", Facts = CheckInFacts.Said | CheckInFacts.Replies,
            Conditions = CheckInConditions.Sayings
        });
        var message = CheckIns.Message(own, State(), null)!;
        Assert.StartsWith("Check whether Martlet repeats itself.", message);
        Assert.Contains("What Mira said in the last hour, oldest first, each with when:", message);
        Assert.Contains("- 10:05 PM (12 min ago): \"Ooh, that boss is almost down!\"", message);
        Assert.DoesNotContain("Good evening!", message);
        Assert.Contains("Mira's last replies, oldest first:", message);
        Assert.Contains("3. Ooh, want to talk it through?", message);
        Assert.Contains("REMIND:", message);
        Assert.Equal("it needs at least 3 things Martlet said in the last hour", CheckIns.Wait(own, State() with { Said = [] }, null));

        // A placeholder puts the fact where the prompt names it, and it isn't added again after the prompt.
        var placed = CheckIns.Message(CheckIns.Of(new CustomCheckIn
        {
            Id = "c2", Name = "Placed", Task = "Lately {name} said:\n{said}\nIs that too much?", Facts = CheckInFacts.Said
        }), State(), null)!;
        Assert.StartsWith("Lately Mira said:\n- ", placed);
        Assert.Contains("- 10:05 PM (12 min ago): \"Ooh, that boss is almost down!\"\n", placed);
        Assert.Contains("\nIs that too much?", placed);
        Assert.DoesNotContain("What Mira said in the last hour", placed);
    }

    // The Touches fact: what the user did to the character lately, with when, which touches were intimate, how the persona feels
    // and where they keep coming back to, in the words the replies hear; read from the ledger without taking it.
    [Fact]
    public void YourOwnCheckInCanKnowHowTheUserTouchedTheCharacter()
    {
        var ledger = new TouchLedger();
        var at = TimeSpan.FromMinutes(30);
        PhysicalEvent Stroke(TimeSpan when) => new(PhysicalKind.Stroke, when, "down from your tail to your groin", "tail → groin", "slowly",
            Zones: ["your tail", "your groin"], Intimate: true, Feeling: "you love being touched there");
        ledger.Record(new(PhysicalKind.Pat, at - TimeSpan.FromMinutes(6), "the top of your head", "top of head"));
        ledger.Drain(at - TimeSpan.FromMinutes(6));
        for (var i = 0; i < 5; i++) ledger.Record(Stroke(at - TimeSpan.FromSeconds(30)));
        var waiting = ledger.Peek(at)!.Line;
        var state = State() with { Touches = ledger.History(at) };
        Assert.Equal(waiting, ledger.Peek(at)!.Line);

        var own = CheckIns.Of(new CustomCheckIn { Id = "c1", Name = "Touches", Task = "Describe the touches.", Facts = CheckInFacts.Touches });
        var message = CheckIns.Message(own, state, null)!;
        Assert.Contains("What the user did to Mira's character on the desktop in the last 10 minutes, oldest first, each with when, " +
            "in the words Mira hears (\"you\" is Mira):\n" +
            "- 10:11 PM (6 min ago): They patted the top of your head once.\n" +
            "- 10:16 PM (30 s ago, intimate): They slowly stroked down from your tail to your groin 5 times (you love being touched there).\n" +
            "They keep coming back to your tail (5 times) and your groin (5 times) in the last minute.", message);
        Assert.DoesNotContain("The end of the conversation", message);

        var placed = CheckIns.Message(CheckIns.Of(new CustomCheckIn
        {
            Id = "c2", Name = "Placed", Task = "Lately:\n{touches}\nDescribe it.", Facts = CheckInFacts.Touches
        }), state, null)!;
        Assert.StartsWith("Lately:\nWhat the user did to Mira's character", placed);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(placed, "What the user did to"));
        Assert.Equal(CheckInFacts.Touches, CheckIns.Placed("{touches}"));
        Assert.Equal("The user didn't touch Mira's character on the desktop in the last 10 minutes.", CheckIns.Touches(State()));

        // Reading it never takes what the next reply gets.
        Assert.Equal(waiting, ledger.Drain(at)!.Line);
        new CheckInSettings().With(new CustomCheckIn { Id = "c1", Name = "Touches", Facts = CheckInFacts.Touches }).Validate();
        Assert.Throws<ContractException>(() => new CheckInSettings().With(new CustomCheckIn { Id = "c1", Name = "Bad", Facts = (CheckInFacts)(1 << 20) }).Validate());
    }

    [Fact]
    public void ACheckInKnowsWhetherAdultContentIsOn()
    {
        var own = CheckIns.Of(new CustomCheckIn { Id = "c1", Name = "Adult", Task = "Describe what happened. {adult}" });
        var off = CheckIns.Message(own, State(), null)!;
        Assert.Contains("Describe what happened. " + PromptCatalog.DefaultCheckInAdultOffInstructions, off);
        Assert.DoesNotContain(PromptCatalog.DefaultCheckInAdultOnInstructions, off);
        var on = CheckIns.Message(own, State() with { Adult = true }, null)!;
        Assert.Contains("Describe what happened. " + PromptCatalog.DefaultCheckInAdultOnInstructions, on);
        Assert.Contains("under 18", on);

        var edited = new PromptSettings { Overrides = new Dictionary<string, string> { [PromptCatalog.CheckInAdultOn] = "FIXTURE: be explicit." } };
        Assert.Contains("Describe what happened. FIXTURE: be explicit.", CheckIns.Message(own, State() with { Adult = true }, edited));
        var emptied = new PromptSettings { Overrides = new Dictionary<string, string> { [PromptCatalog.CheckInAdultOff] = "" } };
        var nothing = CheckIns.Message(own, State(), emptied)!;
        Assert.DoesNotContain("{adult}", nothing);
        Assert.DoesNotContain("Adult content", nothing);
    }

    [Fact]
    public void ABuiltInCheckInKeepsWhatTheOwnerChangedOnItsCard()
    {
        var settings = new CheckInSettings().With(CheckIns.Emotes, new CheckInChoice(true, 1)
        {
            Outcome = CheckInOutcome.Say, Facts = CheckInFacts.Said, Conditions = CheckInConditions.None, Screenshot = true,
            Recording = CheckInRecording.Microphone, RecordingSeconds = 60, Script = "Get-Date"
        });
        settings.Validate();
        var folder = Path.Combine(Path.GetTempPath(), "Martlet.CheckIns.Tests." + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True(settings.Save(folder));
            var (read, state) = CheckInSettings.Read(folder);
            Assert.Equal("loaded", state);
            var emotes = CheckIns.All(read).Single(c => c.Id == CheckIns.Emotes);
            Assert.Equal((1, CheckInOutcome.Say, CheckInFacts.Said, CheckInConditions.None, true, CheckInRecording.Microphone, 60, "Get-Date"),
                (emotes.EveryMinutes, emotes.Outcome, emotes.Facts, emotes.Conditions, emotes.Screenshot, emotes.Recording, emotes.RecordingSeconds, emotes.Script));
            Assert.Equal(ThinkingCapability.Text | ThinkingCapability.Vision | ThinkingCapability.Audio, emotes.Needs);
            Assert.Equal(PromptCatalog.CheckInEmotes, emotes.PromptId);
            // Only the On and Every choice: the rest stays Martlet's own, and nothing else is written.
            var plain = new CheckInSettings().With(CheckIns.Gaze, false, 10);
            Assert.True(plain.Save(folder));
            Assert.DoesNotContain("Outcome", File.ReadAllText(Path.Combine(folder, CheckInSettings.FileName)));
            Assert.Equal(Built(CheckIns.Gaze) with { On = false, EveryMinutes = 10 }, CheckIns.All(CheckInSettings.Read(folder).Settings).Single(c => c.Id == CheckIns.Gaze));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    private static CheckInToolHost Host(CheckIn checkIn, IReadOnlyDictionary<string, CheckInToolHandler> handlers) =>
        new(checkIn.ToolSets, new(checkIn.Id, checkIn.Name, null, Now), handlers);

    private static CustomCheckIn WithTools(params string[] sets) => new()
    {
        Id = "c4", Name = "Tools", On = true, EveryMinutes = 30, Task = "If the user seems upset, remind Martlet to be gentle.",
        Facts = CheckInFacts.Conversation, Outcome = CheckInOutcome.Tools, ToolSets = sets
    };

    [Fact]
    public void ACheckInWithToolSetsNeedsAMemberThatCallsToolsAndOffersOnlyItsSetsTools()
    {
        var checkIn = CheckIns.Of(WithTools(CheckInToolSets.NextReplyId, CheckInToolSets.RemindersId));
        Assert.Equal(ThinkingCapability.Text | ThinkingCapability.Tools, checkIn.Needs);
        Assert.Equal("text and tool calls", CheckIns.Describe(checkIn.Needs));
        // Reminders has no handler here, so only the next reply's tools are offered.
        var host = Host(checkIn, new Dictionary<string, CheckInToolHandler>
        {
            [CheckInToolSets.NextReplyId] = (_, _, _) => ValueTask.FromResult(new ConversationToolResult("Done."))
        });
        Assert.Equal(new[] { CheckInToolSets.RemindNextReply, CheckInToolSets.BringUp }, host.Tools.Select(t => t.Name));
        var job = CheckIns.Prepare(checkIn, CheckIns.Focus(checkIn, State()), null, host)!;
        Assert.Same(host, job.ToolHost);
        Assert.Equal(CheckIns.MaximumToolRounds, job.MaxToolRounds);
        Assert.True(job.Required.HasFlag(ThinkingCapability.Tools));
        job.Validate();
        // Without handlers it offers nothing, and a job with no tools has no host and no rounds.
        var bare = CheckIns.Prepare(checkIn, CheckIns.Focus(checkIn, State()), null, Host(checkIn, new Dictionary<string, CheckInToolHandler>()))!;
        Assert.Empty(bare.Tools);
        Assert.Null(bare.ToolHost);
        Assert.Equal(0, bare.MaxToolRounds);
        Assert.Equal(ThinkingCapability.Text, CheckIns.Of(WithTools()).Needs);
        Assert.Throws<ContractException>(() => (job with { ToolHost = null }).Validate());
    }

    [Fact]
    public async Task TheToolHostKeepsEachCallAndStopsAtTheMostCallsARunMayMake()
    {
        var checkIn = CheckIns.Of(WithTools(CheckInToolSets.NextReplyId, CheckInToolSets.CharacterId));
        var seen = new List<(string Tool, string? Text, CheckInToolContext Context)>();
        var host = Host(checkIn, new Dictionary<string, CheckInToolHandler>
        {
            [CheckInToolSets.NextReplyId] = (call, context, _) =>
            {
                seen.Add((call.Name, CheckInToolSets.Argument(call, "text"), context));
                return ValueTask.FromResult(new ConversationToolResult(new string('x', 200) + "\nprivate second line"));
            },
            [CheckInToolSets.CharacterId] = (_, _, _) => throw new InvalidOperationException("boom")
        });
        var said = await host.CallAsync(new("1", CheckInToolSets.RemindNextReply, """{"text":"  Be gentle.  "}"""), CancellationToken.None);
        var unknown = await host.CallAsync(new("2", "delete_files", "{}"), CancellationToken.None);
        var failed = await host.CallAsync(new("3", CheckInToolSets.LookUsual, "{}"), CancellationToken.None);
        for (var i = 3; i < CheckIns.MaximumToolCalls; i++) await host.CallAsync(new("x" + i, CheckInToolSets.BringUp, "{}"), CancellationToken.None);
        var over = await host.CallAsync(new("9", CheckInToolSets.RemindNextReply, "{}"), CancellationToken.None);

        Assert.False(said.IsError);
        Assert.Equal(("remind_next_reply", "Be gentle."), (seen[0].Tool, seen[0].Text));
        Assert.Equal(("c4", "Tools", Now), (seen[0].Context.CheckInId, seen[0].Context.CheckInName, seen[0].Context.Now));
        Assert.Equal(("This check-in has no tool called delete_files.", true), (unknown.Output, unknown.IsError));
        Assert.Equal(("The tool failed.", true), (failed.Output, failed.IsError));
        Assert.True(over.IsError);
        Assert.StartsWith("This check-in already made 8 tool calls", over.Output);
        var uses = host.Uses;
        Assert.Equal(CheckIns.MaximumToolCalls + 1, uses.Count);
        Assert.Equal(new CheckInToolUse(CheckInToolSets.NextReplyId, CheckInToolSets.RemindNextReply, new string('x', 117) + "...", false), uses[0]);
        Assert.Equal(new CheckInToolUse("", "delete_files", "This check-in has no tool called delete_files.", true), uses[1]);
        Assert.Equal(new CheckInToolUse(CheckInToolSets.CharacterId, CheckInToolSets.LookUsual, "The tool failed.", true), uses[2]);
        Assert.DoesNotContain(uses, u => u.Result.Contains("private", StringComparison.Ordinal));
        Assert.Equal(CheckIns.MaximumToolCalls - 2, seen.Count);
        Assert.StartsWith("remind_next_reply: xxx", CheckIns.ToolsText(uses));
        Assert.Contains("; delete_files (failed): This check-in has no tool called delete_files;", CheckIns.ToolsText(uses));
    }

    [Fact]
    public void AToolsCheckInsToolCallsAreTheActionAndOthersMayCallToolsFirst()
    {
        var checkIn = CheckIns.Of(WithTools(CheckInToolSets.NextReplyId));
        var message = CheckIns.Message(checkIn, CheckIns.Focus(checkIn, State()), null)!;
        Assert.Contains("Use your tools for what needs doing.", message);
        Assert.DoesNotContain("REMIND:", message);
        Assert.Same(CheckInVerdict.Nothing, CheckIns.Read(checkIn, "REMIND: be gentle", State()));
        Assert.Equal("it has no tools to use", CheckIns.Wait(CheckIns.Of(WithTools()), State(), null));
        var note = CheckIns.Of(WithTools(CheckInToolSets.RemindersId) with { Outcome = CheckInOutcome.Note });
        var noteMessage = CheckIns.Message(note, CheckIns.Focus(note, State()), null)!;
        Assert.Contains("You may call your tools first if they help.\nIf nothing needs doing now, write only: OK", noteMessage);
        Assert.True(CheckIns.Read(note, "REMIND: be gentle", State()).Act);
        Assert.DoesNotContain("You may call your tools", CheckIns.Message(CheckIns.Of(WithTools() with { Outcome = CheckInOutcome.Note }), State(), null)!);
    }

    [Fact]
    public void ToolSetsSaveAndReadBackByValueAndUnknownSetsAreRefused()
    {
        var custom = WithTools(CheckInToolSets.NextReplyId, CheckInToolSets.CharacterId);
        var settings = new CheckInSettings().With(custom)
            .With(CheckIns.Promises, new CheckInChoice(true, 30) { ToolSets = [CheckInToolSets.RemindersId] });
        settings.Validate();
        var folder = Path.Combine(Path.GetTempPath(), "Martlet.CheckIns.Tests." + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True(settings.Save(folder));
            var (read, state) = CheckInSettings.Read(folder);
            Assert.Equal("loaded", state);
            Assert.Equal(custom, read.Custom.Single());
            Assert.Equal(settings.Choice(CheckIns.Promises), read.Choice(CheckIns.Promises));
            var promises = CheckIns.All(read).Single(c => c.Id == CheckIns.Promises);
            Assert.Equal(new[] { CheckInToolSets.RemindersId }, promises.ToolSets);
            Assert.True(promises.Needs.HasFlag(ThinkingCapability.Tools));
            // A choice that keeps Martlet's own tool sets writes none.
            Assert.True(new CheckInSettings().With(CheckIns.Gaze, false, 10).Save(folder));
            Assert.DoesNotContain("ToolSets", File.ReadAllText(Path.Combine(folder, CheckInSettings.FileName)));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        Assert.Throws<ContractException>(() => new CheckInSettings().With(WithTools("delete-everything")).Validate());
        Assert.Throws<ContractException>(() => new CheckInSettings().With(WithTools(CheckInToolSets.NextReplyId, CheckInToolSets.NextReplyId)).Validate());
        Assert.Throws<ContractException>(() => new CheckInSettings()
            .With(CheckIns.Emotes, new CheckInChoice(true, 5) { ToolSets = ["nope"] }).Validate());
        Assert.Equal(new CheckInToolSetIds(["a", "b"]), new CheckInToolSetIds(["a", "b"]));
        Assert.NotEqual(new CheckInToolSetIds(["a", "b"]), new CheckInToolSetIds(["b", "a"]));
        Assert.All(CheckInToolSets.All, s => Assert.True(CheckInToolSets.IsId(s.Id)));
        Assert.Equal(CheckInToolSets.All.SelectMany(s => s.Tools).Count(), CheckInToolSets.All.SelectMany(s => s.Tools).Select(t => t.Name).Distinct().Count());
        // A reply tool is taken over by one set at most.
        Assert.Equal(CheckInToolSets.All.SelectMany(s => s.Replaces).Count(), CheckInToolSets.All.SelectMany(s => s.Replaces).Distinct().Count());
        Assert.Empty(new CheckInToolSet("x", "X", "Does x.", []).Replaces);
    }

    [Fact]
    public void TouchesFireTheirTriggersOnceTheySettleAndNeverTakeFromTheLedger()
    {
        var ledger = new TouchLedger();
        var watch = new TouchTriggers();
        var at = TimeSpan.FromMinutes(20);
        for (var i = 0; i < 5; i++) ledger.Record(new(PhysicalKind.Tap, at + TimeSpan.FromSeconds(i * 10), "your groin", "groin", Zones: ["your groin"], Intimate: true));
        ledger.Drain(at + TimeSpan.FromSeconds(60));
        PhysicalEvent[] burst =
        [
            new(PhysicalKind.Pat, at + TimeSpan.FromSeconds(70), "the top of your head", "top of head", Zones: ["the top of your head"]),
            new(PhysicalKind.Moved, at + TimeSpan.FromSeconds(71), Detail: "to another monitor"),
            new(PhysicalKind.Stroke, at + TimeSpan.FromSeconds(72), Label: "tail, buttocks and groin", Detail: "slowly",
                Zones: ["your tail", "your buttocks", "your groin"], Intimate: true)
        ];
        foreach (var physical in burst)
        {
            ledger.Record(physical);
            watch.Touched(physical, ledger.Peek(physical.At)?.Often);
        }
        Assert.True(watch.Waiting);
        var fired = watch.Settle(Now)!;
        Assert.Equal(CheckIns.ByTouches, fired.Fired);
        Assert.Equal(Now, fired.At);
        Assert.Equal("2 touches, an intimate one, a stroke across 3 zones, one place touched 6 times lately", fired.What);
        Assert.False(watch.Waiting);
        Assert.Equal(3, ledger.Peek(at + TimeSpan.FromSeconds(73))!.Touches);

        // A stroke across 2 zones, a plain poke, and moving the character alone.
        watch.Touched(new(PhysicalKind.Stroke, at + TimeSpan.FromSeconds(90), Zones: ["your chest", "your stomach"]));
        Assert.Equal(CheckInTriggers.TouchesEnded, watch.Settle(Now)!.Fired);
        watch.Touched(new(PhysicalKind.Tap, at + TimeSpan.FromSeconds(95), "your left cheek", "left cheek"));
        Assert.Equal(("1 touch", CheckInTriggers.TouchesEnded), watch.Settle(Now) is { } poke ? (poke.What, poke.Fired) : default);
        watch.Touched(new(PhysicalKind.Moved, at + TimeSpan.FromSeconds(99)));
        Assert.False(watch.Waiting);
        Assert.Null(watch.Settle(Now));
    }

    [Fact]
    public void ACheckInWithTriggersRunsOnlyWhenOneFiresAtMostOncePerItsPace()
    {
        var checkIn = CheckIns.Of(new CustomCheckIn
        {
            Id = "c1", Name = "Touches", On = true, EveryMinutes = 5, Task = "Describe the touches.",
            Triggers = CheckInTriggers.IntimateTouch | CheckInTriggers.KeepsComingBack
        });
        var busy = State() with { Quiet = TimeSpan.Zero };
        var fired = new CheckInTrigger(CheckInTriggers.TouchesEnded | CheckInTriggers.IntimateTouch, Now.AddSeconds(-1), "3 touches, an intimate one");
        Assert.Equal("it waits for an intimate touch or you to keep coming back to one place", CheckIns.Wait(checkIn, State(), null));
        Assert.Equal(CheckIns.Wait(checkIn, State(), null),
            CheckIns.Wait(checkIn, State(), null, fired: fired with { Fired = CheckInTriggers.TouchesEnded }));
        // The touches and the reply to them never hold it up; the pace is its cooldown.
        Assert.Null(CheckIns.Wait(checkIn, busy, null, fired: fired));
        Assert.Equal("next in 4 min", CheckIns.Wait(checkIn, State(), new(Now.AddMinutes(-1), 4, "nothing to remind Martlet of", false), fired: fired));
        Assert.Equal(CheckIns.Wait(checkIn, State(), null), CheckIns.Wait(checkIn, State(), null, fired: fired with { At = Now - CheckIns.TriggerAge - TimeSpan.FromSeconds(1) }));
        Assert.StartsWith("nobody used this PC", CheckIns.Wait(checkIn, State() with { Away = TimeSpan.FromMinutes(20) }, null, fired: fired));
        Assert.Equal("it's off", CheckIns.Wait(checkIn with { On = false }, State(), null, fired: fired));
        Assert.Equal("the character isn't showing",
            CheckIns.Wait(checkIn with { Conditions = CheckInConditions.CharacterShows }, State() with { CharacterShows = false }, null, fired: fired));
        Assert.Null(CheckIns.Wait(checkIn, busy, null, now: true));
        // A check-in with no triggers runs as before.
        Assert.Equal("the conversation is busy", CheckIns.Wait(checkIn with { Triggers = CheckInTriggers.None }, busy, null, fired: fired));
        Assert.Equal("nothing", CheckIns.TriggerWords(CheckInTriggers.None));
        Assert.Equal("your touches to end, an intimate touch, a stroke across 3 zones or you to keep coming back to one place",
            CheckIns.TriggerWords(CheckIns.ByTouches));
    }

    [Fact]
    public void TriggersSaveReadBackAndRefuseWhatMartletDoesntOffer()
    {
        var settings = new CheckInSettings()
            .With(new CustomCheckIn { Id = "c1", Name = "Touches", Task = "Describe them.", Triggers = CheckInTriggers.TouchesEnded | CheckInTriggers.StrokeAcrossZones })
            .With(CheckIns.Gaze, new CheckInChoice(true, 5) { Triggers = CheckInTriggers.IntimateTouch });
        settings.Validate();
        // Only Describe touches and How I react start on touches by default.
        Assert.All(CheckIns.All(null), c => Assert.Equal(c.Id == CheckIns.DescribeTouches ? CheckInTriggers.TouchesEnded
            : c.Id == CheckIns.Reactions ? CheckIns.ByTouches : c.Id == CheckIns.Actions ? CheckInTriggers.ExchangeEnded : CheckInTriggers.None, c.Triggers));
        Assert.Throws<ContractException>(() => settings.With(new CustomCheckIn { Id = "c2", Name = "Bad", Triggers = (CheckInTriggers)32 }).Validate());
        Assert.Throws<ContractException>(() => settings.With(CheckIns.Emotes, new CheckInChoice(true, 5) { Triggers = (CheckInTriggers)64 }).Validate());
        var folder = Path.Combine(Path.GetTempPath(), "Martlet.CheckIns.Tests." + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True(settings.Save(folder));
            var (read, state) = CheckInSettings.Read(folder);
            Assert.Equal("loaded", state);
            var all = CheckIns.All(read);
            Assert.Equal(CheckInTriggers.TouchesEnded | CheckInTriggers.StrokeAcrossZones, all.Single(c => c.Id == "c1").Triggers);
            Assert.Equal(CheckInTriggers.IntimateTouch, all.Single(c => c.Id == CheckIns.Gaze).Triggers);
            Assert.Contains("\"TouchesEnded, StrokeAcrossZones\"", File.ReadAllText(Path.Combine(folder, CheckInSettings.FileName)));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void DescribeTouchesStartsRightAfterTouchesAndAddsADescriptionForTheNextReply()
    {
        var describe = Built(CheckIns.DescribeTouches);
        Assert.Equal((true, 1, CheckInOutcome.Context, CheckInTriggers.TouchesEnded, PromptCatalog.CheckInTouches),
            (describe.On, describe.EveryMinutes, describe.Outcome, describe.Triggers, describe.PromptId));
        Assert.Equal(CheckInFacts.Touches | CheckInFacts.Conversation | CheckInFacts.Persona | CheckInFacts.Character, describe.Facts);

        var ledger = new TouchLedger();
        var clock = TimeSpan.FromMinutes(30);
        for (var i = 0; i < 2; i++)
            ledger.Record(new(PhysicalKind.Stroke, clock - TimeSpan.FromSeconds(20 - i), "down from your tail over your buttocks to your groin",
                "tail → buttocks → groin", "slowly", Zones: ["your tail", "your buttocks", "your groin"], Intimate: true));
        var state = State() with { Touches = ledger.History(clock) };
        var fired = new CheckInTrigger(CheckInTriggers.TouchesEnded, Now.AddSeconds(-2), "2 touches");

        // Only touches start it, never its pace alone, and the touch reply that keeps the conversation busy never holds it up.
        Assert.NotNull(CheckIns.Wait(describe, state, null));
        Assert.Null(CheckIns.Wait(describe, state with { Quiet = TimeSpan.Zero }, null, fired: fired));
        Assert.Equal("next in 50 s", CheckIns.Wait(describe, state, new(Now.AddSeconds(-10), 4, "nothing to add to what Martlet knows", false), fired: fired));
        Assert.Equal("the character isn't showing", CheckIns.Wait(describe, state with { CharacterShows = false }, null, fired: fired));

        var message = CheckIns.Message(describe, CheckIns.Focus(describe, state), null)!;
        Assert.StartsWith("Mira is the user's desktop character, and the user has been touching it.", message);
        Assert.Contains("They slowly stroked down from your tail over your buttocks to your groin twice", message);
        Assert.Contains("I beat the boss!", message);
        Assert.Contains("Mira is playful and teasing.", message);
        Assert.Contains("{glasses} - when reading", message);
        Assert.Contains(PromptCatalog.DefaultCheckInAdultOffInstructions, message);
        Assert.Contains("KNOW:", message);
        Assert.DoesNotContain("{touches}", message);
        Assert.DoesNotContain("{adult}", message);
        // Each fact it places in its prompt goes there only, not again after it.
        Assert.Equal(message.IndexOf("They slowly stroked", StringComparison.Ordinal), message.LastIndexOf("They slowly stroked", StringComparison.Ordinal));
        Assert.Contains(PromptCatalog.DefaultCheckInAdultOnInstructions, CheckIns.Message(describe, CheckIns.Focus(describe, state with { Adult = true }), null));

        var verdict = CheckIns.Read(describe, "KNOW: Their slow strokes keep sliding from your tail down to your groin.", state);
        Assert.Equal((true, "Their slow strokes keep sliding from your tail down to your groin."), (verdict.Act, verdict.Text));
        Assert.Contains("Their slow strokes", CheckIns.Context(null, verdict.Text!));
        Assert.False(CheckIns.Read(describe, "KNOW: nothing to add", state).Act);
    }
}
