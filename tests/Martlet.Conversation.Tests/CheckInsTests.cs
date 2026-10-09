using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;

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
        new CheckInSettings().With(new CustomCheckIn
        {
            Id = "c1", Name = "Emotes", Outcome = CheckInOutcome.EmotesOff, Conditions = CheckInConditions.CharacterShows | CheckInConditions.EmoteShown,
            Facts = CheckInFacts.Character | CheckInFacts.Said | CheckInFacts.Replies
        }).Validate();
        Assert.Throws<ContractException>(() =>
            new CheckInSettings().With(new CustomCheckIn { Id = "c1", Name = "Bad", Conditions = (CheckInConditions)4096 }).Validate());
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
        // Built-in check-ins need text only.
        Assert.All(CheckIns.All(null), c => Assert.Equal(ThinkingCapability.Text, c.Needs));
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

    public static TheoryData<string> BuiltInIds => [CheckIns.Emotes, CheckIns.Gaze, CheckIns.Promises, CheckIns.Character, CheckIns.Repeats];

    [Theory]
    [MemberData(nameof(BuiltInIds))]
    public void EveryBuiltInCheckInCanBeRecreatedAsYourOwn(string id)
    {
        var builtIn = Built(id);
        var copy = CheckIns.Of(new CustomCheckIn
        {
            Id = "c1", Name = builtIn.Name + " (copy)", On = true, EveryMinutes = builtIn.EveryMinutes, Task = CheckIns.Template(builtIn, null)!,
            Facts = builtIn.Facts, Conditions = builtIn.Conditions, Outcome = builtIn.Outcome
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
        Assert.Throws<ContractException>(() => new CheckInSettings().With(new CustomCheckIn { Id = "c1", Name = "Bad", Facts = (CheckInFacts)1024 }).Validate());
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
}
