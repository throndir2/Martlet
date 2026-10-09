using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Conversation.Tests;

// The signals check-ins wait for and know about: came back, a call, what the user does, hours, an unanswered question, a flashing
// taskbar button or a notification, a song that ended, a script whose output changed, someone else's voice, and the per-hour cap.
public sealed partial class CheckInsTests
{
    private static CheckIn Signal(CheckInConditions when, string script = "") =>
        CheckIns.Of(new CustomCheckIn { Id = "c2", Name = "Signal", On = true, EveryMinutes = 1, Task = "Check.", Conditions = when, Script = script });

    private static CheckInState Signals() => State() with
    {
        CameBackAt = Now.AddMinutes(-1), WasAway = TimeSpan.FromMinutes(25), Activity = "in a call in Discord", OnCall = true,
        ActivityChangedAt = Now.AddMinutes(-1), ActivityBefore = "playing a game (Elden Ring), full screen",
        AttentionAt = Now.AddMinutes(-1), SongEndedAt = Now.AddMinutes(-1),
        Voices = [new("Sam", true, Now.AddMinutes(-2)) { Id = "v1" }, new(null, false, Now.AddMinutes(-1)) { Id = "v2" }],
        OwnerVoiceKnown = true, WhileAway = ["Thinking longer finished: a trip plan", "A reminder came due at 10:00 PM: the oven"]
    };

    [Fact]
    public void SignalConditionsWaitForTheirSignalSinceTheLastRun()
    {
        var state = Signals();
        var last = new CheckInRun(Now.AddMinutes(-3), 4, "nothing", false);

        Assert.Null(CheckIns.Wait(Signal(CheckInConditions.CameBack), state, null));
        Assert.Equal("it waits for you to come back after 10 min or more away",
            CheckIns.Wait(Signal(CheckInConditions.CameBack), state with { CameBackAt = Now.AddMinutes(-4) }, last));
        Assert.NotNull(CheckIns.Wait(Signal(CheckInConditions.CameBack), state with { CameBackAt = Now.AddMinutes(-6) }, null));
        Assert.NotNull(CheckIns.Wait(Signal(CheckInConditions.CameBack), state with { CameBackAt = null }, null));

        Assert.Equal("you're on a call", CheckIns.Wait(Signal(CheckInConditions.NotOnCall), state, null));
        Assert.Equal("you're on a call", CheckIns.Wait(Signal(CheckInConditions.NotOnCall), state, null, now: true));
        Assert.Null(CheckIns.Wait(Signal(CheckInConditions.NotOnCall), state with { OnCall = false }, null));

        Assert.Null(CheckIns.Wait(Signal(CheckInConditions.ActivityChanged), state, last));
        Assert.Equal("what you do hasn't changed since the last check",
            CheckIns.Wait(Signal(CheckInConditions.ActivityChanged), state with { ActivityChangedAt = Now.AddMinutes(-4) }, last));
        Assert.StartsWith("Martlet doesn't know what you're doing",
            CheckIns.Wait(Signal(CheckInConditions.ActivityChanged), state with { Activity = null, ActivityChangedAt = null }, null));

        Assert.Null(CheckIns.Wait(Signal(CheckInConditions.Attention), state, last));
        Assert.NotNull(CheckIns.Wait(Signal(CheckInConditions.Attention), state with { AttentionAt = Now.AddMinutes(-4) }, last));
        Assert.Null(CheckIns.Wait(Signal(CheckInConditions.SongEnded), state, last));
        Assert.Equal("no song played to its end since the last check",
            CheckIns.Wait(Signal(CheckInConditions.SongEnded), state with { SongEndedAt = null }, last));
        // Check now runs without the new signal, but not without something to check.
        Assert.Null(CheckIns.Wait(Signal(CheckInConditions.SongEnded), state with { SongEndedAt = null }, last, now: true));
    }

    [Theory]
    [InlineData(8, 22, 22, false)]
    [InlineData(8, 22, 8, true)]
    [InlineData(8, 22, 7, false)]
    [InlineData(22, 6, 23, true)]
    [InlineData(22, 6, 3, true)]
    [InlineData(22, 6, 6, false)]
    [InlineData(22, 6, 12, false)]
    public void OnlyBetweenTheseHoursFollowsTheClock(int from, int until, int hour, bool runs)
    {
        Assert.Equal(runs, CheckIns.InHours(from, until, hour));
        var checkIn = Signal(CheckInConditions.Between) with { FromHour = from, UntilHour = until };
        var at = new DateTimeOffset(2026, 10, 7, hour, 30, 0, Now.Offset);
        var wait = CheckIns.Wait(checkIn, Signals() with { Now = at, Said = [], Quiet = TimeSpan.FromMinutes(2) }, null);
        Assert.Equal(runs ? null : $"it runs only between {CheckIns.Clock(from)} and {CheckIns.Clock(until)}", wait);
        Assert.Null(CheckIns.Wait(checkIn, Signals() with { Now = at }, null, now: true));
    }

    [Theory]
    [InlineData("So, which boss is next?", true)]
    [InlineData("{smile} Want to talk it through? {wave}", true)]
    [InlineData("\"Ready for round two?\"", true)]
    [InlineData("Ooh, that boss is almost down!", false)]
    [InlineData("Is it? I think so.", false)]
    public void TellsWhetherMartletsLastRemarkAskedSomething(string said, bool question)
    {
        var state = State() with { Said = [new(Now.AddMinutes(-5), "Hello?"), new(Now.AddMinutes(-3), said)] };
        Assert.Equal(question, CheckIns.LastQuestion(state) is not null);
    }

    [Fact]
    public void AnUnansweredQuestionGetsOneFollowUpWhileYouAreHere()
    {
        var checkIn = Built(CheckIns.Unanswered) with { On = true };
        var asked = State() with { Said = [new(Now.AddMinutes(-4), "So, which boss is next?")], Quiet = TimeSpan.FromMinutes(4) };
        Assert.Null(CheckIns.Wait(checkIn, asked, null));
        Assert.Equal("it gives you 2 min to answer",
            CheckIns.Wait(checkIn, asked with { Said = [new(Now.AddMinutes(-1), "Which boss?")], Quiet = TimeSpan.FromMinutes(1) }, null));
        Assert.Equal("the conversation went on after the question", CheckIns.Wait(checkIn, asked with { Quiet = TimeSpan.FromMinutes(1) }, null));
        Assert.Equal("you aren't at the PC", CheckIns.Wait(checkIn, asked with { Away = TimeSpan.FromMinutes(5) }, null));
        Assert.Equal("Martlet's last remark didn't ask anything", CheckIns.Wait(checkIn, State(), null));
        Assert.Equal("it already followed up on Martlet's last question",
            CheckIns.Wait(checkIn, asked with { Said = [new(Now.AddMinutes(-9), "Which boss?")], Quiet = TimeSpan.FromMinutes(9) },
                new CheckInRun(Now.AddMinutes(-6), 4, "Martlet brings it up", true)));
        Assert.Equal("you're on a call", CheckIns.Wait(checkIn, asked with { OnCall = true }, null));
        Assert.Equal("Martlet's last remark didn't ask anything", CheckIns.Wait(checkIn, asked with { Said = [new(Now.AddMinutes(-45), "Which boss?")] }, null));
    }

    [Fact]
    public void SomeoneElseIsAVoiceThatIsntTheUsers()
    {
        var checkIn = Built(CheckIns.Others) with { On = true };
        var state = Signals();
        Assert.Null(CheckIns.Wait(checkIn, state, null));
        Assert.Equal("Martlet heard nobody else lately", CheckIns.Wait(checkIn, state with { Voices = [state.Voices[0]] }, null));
        // Without the user's own voice marked, one voice is the user's and two different ones mean someone else is here.
        Assert.Equal("Martlet heard nobody else lately",
            CheckIns.Wait(checkIn, state with { OwnerVoiceKnown = false, Voices = [new(null, false, Now.AddMinutes(-1)) { Id = "v2" }] }, null));
        Assert.Null(CheckIns.Wait(checkIn, state with { OwnerVoiceKnown = false }, null));
        Assert.Equal("Martlet heard nobody else lately",
            CheckIns.Wait(checkIn, state with { Voices = [.. state.Voices.Select(v => v with { At = Now.AddMinutes(-11) })] }, null));
        Assert.Equal("nobody else spoke since the last check", CheckIns.Wait(checkIn with { EveryMinutes = 1 },
            state with { Voices = [state.Voices[0], state.Voices[1] with { At = Now.AddMinutes(-3) }] }, new CheckInRun(Now.AddMinutes(-2), 4, "nothing", false)));
    }

    [Fact]
    public void TheSignalFactsAreWordedForTheCheck()
    {
        var state = Signals();
        Assert.Equal("What the user seems to be doing on this PC now (a guess from which apps play sound and which window fills the screen): " +
            "in a call in Discord. They are in a call or a voice chat. That changed 1 min ago; before, they were playing a game (Elden Ring), full screen.",
            CheckIns.Activity(state));
        Assert.StartsWith("Martlet doesn't know what the user is doing", CheckIns.Activity(state with { Activity = null, OnCall = false, ActivityChangedAt = null }));
        Assert.Equal("The user doesn't seem to be doing anything with sound on this PC now.",
            CheckIns.Activity(state with { Activity = "", OnCall = false, ActivityChangedAt = null }));
        Assert.Equal("Voices Martlet heard in the last 10 minutes, newest first:\n- a voice Martlet doesn't know, 1 min ago\n" +
            "- Sam (the user's own voice), 2 min ago\nSomeone other than the user seems to be here.", CheckIns.People(state));
        Assert.StartsWith("Martlet heard no voices", CheckIns.People(state with { Voices = [] }));
        var away = CheckIns.Away(state);
        Assert.StartsWith("The user came back to this PC 1 min ago, after 25 min away (from 9:51 PM to 10:16 PM).", away);
        Assert.Contains("While they were away:\n- Thinking longer finished: a trip plan\n- A reminder came due at 10:00 PM: the oven", away);
        Assert.Equal("The user hasn't been away from this PC lately.", CheckIns.Away(State()));

        Assert.Equal(CheckInFacts.Activity | CheckInFacts.People | CheckInFacts.WhileAway, CheckIns.Placed("{activity}, {people}, {away}"));
        var message = CheckIns.Message(CheckIns.Of(new CustomCheckIn
        {
            Id = "c1", Name = "Signals", Task = "Who: {people}", Facts = CheckInFacts.People | CheckInFacts.Activity | CheckInFacts.WhileAway
        }), state, null)!;
        Assert.StartsWith("Who: Voices Martlet heard", message);
        Assert.Contains("in a call in Discord", message);
        Assert.Contains("While they were away:", message);
        Assert.Equal(1, message.Split("Voices Martlet heard").Length - 1);
    }

    [Fact]
    public void TheSignalCheckInsAskWithTheirFactsAndReadTheirAnswers()
    {
        var state = Signals();
        var welcome = CheckIns.Message(Built(CheckIns.Welcome), state, null)!;
        Assert.Contains("after 25 min away", welcome);
        Assert.Contains("starts with SAY:", welcome);
        Assert.Contains("Mira is playful", welcome);
        var call = CheckIns.Message(Built(CheckIns.Call), state, null)!;
        Assert.Contains("If a call or voice chat just started", call);
        Assert.Contains("starts with REMIND:", call);
        var others = CheckIns.Message(Built(CheckIns.Others), state, null)!;
        Assert.Contains("- Sam (the user's own voice), 2 min ago", others);
        Assert.Contains("not to share private things", others);
        Assert.Contains("follow up once", CheckIns.Message(Built(CheckIns.Unanswered), state, null)!);
        Assert.Equal("Welcome back!", CheckIns.Read(Built(CheckIns.Welcome), "SAY: Welcome back!", state).Text);
        Assert.False(CheckIns.Read(Built(CheckIns.Unanswered), "OK", state).Act);
        Assert.Equal("Stay quiet.", CheckIns.Read(Built(CheckIns.Call), "REMIND: Stay quiet.", state).Text);
        Assert.All(new[] { CheckIns.Welcome, CheckIns.Unanswered, CheckIns.Call, CheckIns.Others },
            id => Assert.Equal(PromptCatalog.CheckInGroup, PromptCatalog.Find(Built(id).PromptId!)!.Group));
    }

    [Fact]
    public void AScriptWhoseOutputDidntChangeIsntAskedAndTheCapHoldsRunsPerHour()
    {
        var scripted = Signal(CheckInConditions.ScriptChanged, "Get-Date -Format yyyy");
        var hash = CheckIns.ScriptHash("2026\n");
        Assert.Equal(16, hash.Length);
        Assert.Equal(hash, CheckIns.ScriptHash("2026\n"));
        Assert.NotEqual(hash, CheckIns.ScriptHash("2027\n"));
        var last = new CheckInRun(Now.AddMinutes(-2), 4, "nothing", false) { ScriptHash = hash };
        Assert.True(CheckIns.ScriptUnchanged(scripted, hash, last, now: false));
        Assert.False(CheckIns.ScriptUnchanged(scripted, CheckIns.ScriptHash("2027\n"), last, now: false));
        Assert.False(CheckIns.ScriptUnchanged(scripted, hash, last, now: true));
        Assert.False(CheckIns.ScriptUnchanged(scripted, hash, null, now: false));
        Assert.False(CheckIns.ScriptUnchanged(Signal(CheckInConditions.None, "Get-Date"), hash, last, now: false));
        Assert.Null(CheckIns.Wait(scripted, State(), last));
        Assert.Equal("it has no script whose output could change", CheckIns.Wait(Signal(CheckInConditions.ScriptChanged), State(), null));

        var capped = Signal(CheckInConditions.None) with { MostPerHour = 2 };
        var recent = CheckIns.Recent(new CheckInRun(Now.AddMinutes(-70), 4, "nothing", false) { Recent = [Now.AddMinutes(-90), Now.AddMinutes(-70)] }, Now.AddMinutes(-30));
        Assert.Equal([Now.AddMinutes(-70), Now.AddMinutes(-30)], recent);
        var twice = new CheckInRun(Now.AddMinutes(-2), 4, "nothing", false) { Recent = [Now.AddMinutes(-30), Now.AddMinutes(-2)] };
        Assert.Equal("it ran 2 times in the last hour, its most", CheckIns.Wait(capped, State(), twice));
        Assert.Null(CheckIns.Wait(capped, State() with { Now = Now.AddMinutes(31) }, twice));
        Assert.Null(CheckIns.Wait(capped, State(), twice, now: true));
        Assert.Null(CheckIns.Wait(capped with { MostPerHour = 0 }, State(), twice));
    }

    [Fact]
    public void HoursAndTheCapSaveReadBackAndRefuseWhatMartletCantRun()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Martlet.CheckIns." + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new CheckInSettings()
                .With(CheckIns.Welcome, new CheckInChoice(true, 30) { FromHour = 7, UntilHour = 23, MostPerHour = 2 })
                .With(new CustomCheckIn
                {
                    Id = "c1", Name = "Night", Conditions = CheckInConditions.Between | CheckInConditions.Unanswered | CheckInConditions.SomeoneElse,
                    Facts = CheckInFacts.WhileAway | CheckInFacts.People, FromHour = 22, UntilHour = 6, MostPerHour = 12
                });
            Assert.True(settings.Save(folder));
            var (read, state) = CheckInSettings.Read(folder);
            Assert.Equal("loaded", state);
            var all = CheckIns.All(read);
            Assert.Equal((true, 7, 23, 2), all.Single(c => c.Id == CheckIns.Welcome) is var w ? (w.On, w.FromHour, w.UntilHour, w.MostPerHour) : default);
            Assert.Equal((22, 6, 12), all.Single(c => c.Id == "c1") is var c ? (c.FromHour, c.UntilHour, c.MostPerHour) : default);
            Assert.Equal(CheckInConditions.Between | CheckInConditions.Unanswered | CheckInConditions.SomeoneElse, all.Single(c => c.Id == "c1").Conditions);
            Assert.Equal((CheckIns.DefaultFromHour, CheckIns.DefaultUntilHour, 0), Built(CheckIns.Emotes) is var e ? (e.FromHour, e.UntilHour, e.MostPerHour) : default);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        Assert.Throws<ContractException>(() => new CheckInSettings().With(new CustomCheckIn { Id = "c1", Name = "Bad", FromHour = 9, UntilHour = 9 }).Validate());
        Assert.Throws<ContractException>(() => new CheckInSettings().With(new CustomCheckIn { Id = "c1", Name = "Bad", UntilHour = 24 }).Validate());
        Assert.Throws<ContractException>(() => new CheckInSettings().With(new CustomCheckIn { Id = "c1", Name = "Bad", MostPerHour = 5 }).Validate());
        Assert.Throws<ContractException>(() => new CheckInSettings().With(CheckIns.Call, new CheckInChoice(true, 2) { MostPerHour = -1 }).Validate());
        new CheckInSettings().With(new CustomCheckIn { Id = "c1", Name = "All", Facts = CheckIns.KnownFacts, Conditions = CheckIns.KnownConditions }).Validate();
    }
}
