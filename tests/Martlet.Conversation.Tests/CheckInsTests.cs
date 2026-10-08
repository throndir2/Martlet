using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Conversation.Tests;

public sealed class CheckInsTests
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
        Assert.Equal([CheckIns.Emotes, CheckIns.Gaze, CheckIns.Promises, CheckIns.Character, CheckIns.Repeats], defaults.Select(c => c.Id));
        Assert.All(defaults, c => Assert.True(c.On));
        Assert.Equal(15, defaults.Single(c => c.Id == CheckIns.Character).EveryMinutes);
        Assert.Equal((10, CheckInOutcome.Note, PromptCatalog.CheckInRepeats),
            defaults.Single(c => c.Id == CheckIns.Repeats) is var repeats ? (repeats.EveryMinutes, repeats.Outcome, repeats.PromptId) : default);

        var settings = new CheckInSettings().With(CheckIns.Gaze, false, 30)
            .With(new CustomCheckIn { Id = "c1", Name = "Breaks", On = true, EveryMinutes = 60, Task = "Suggest a break.", Outcome = CheckInOutcome.Say });
        var all = CheckIns.All(settings);
        Assert.Equal(6, all.Count);
        Assert.Equal((false, 30), (all[1].On, all[1].EveryMinutes));
        Assert.True(all[5].Custom);
        Assert.Equal(CheckInOutcome.Say, all[5].Outcome);
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
        Assert.Null(CheckIns.Message(Built(CheckIns.Repeats), state with { Said = [] }, null));

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
        Assert.Null(CheckIns.Prepare(Built(CheckIns.Gaze), state with { Gaze = null }, null));
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
            Assert.All(CheckIns.All(fallback), c => Assert.True(c.On));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }

        Assert.Throws<ContractException>(() => new CheckInSettings().With(CheckIns.Emotes, true, 7).Validate());
        Assert.Throws<ContractException>(() => new CheckInSettings().With("nope", true, 5).Validate());
        Assert.Throws<ContractException>(() => new CheckInSettings().With(new CustomCheckIn { Id = "x1", Name = "Bad id" }).Validate());
        Assert.Throws<ContractException>(() => new CheckInSettings().With(new CustomCheckIn { Id = "c1", Name = " " }).Validate());
        Assert.Throws<ContractException>(() =>
            new CheckInSettings().With(new CustomCheckIn { Id = "c1", Name = "Emotes", Outcome = CheckInOutcome.EmotesOff }).Validate());
        var full = Enumerable.Range(1, CheckIns.MaximumCustom + 1)
            .Aggregate(new CheckInSettings(), (all, n) => all.With(new CustomCheckIn { Id = "c" + n, Name = "Check " + n }));
        Assert.Throws<ContractException>(full.Validate);
    }

    [Fact]
    public void CheckInPromptsAreInTheirOwnGroupAndTheBroughtUpMessageCantBeEmptied()
    {
        string[] ids = [PromptCatalog.CheckIn, PromptCatalog.CheckInEmotes, PromptCatalog.CheckInGaze, PromptCatalog.CheckInPromises,
            PromptCatalog.CheckInCharacter, PromptCatalog.CheckInRepeats, PromptCatalog.CheckInCustom, PromptCatalog.CheckInNote,
            PromptCatalog.CheckInDue, PromptCatalog.CheckInDueNotes];
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
}
