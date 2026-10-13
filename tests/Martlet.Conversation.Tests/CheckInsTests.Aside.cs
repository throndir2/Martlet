using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Conversation.Tests;

// Talking to someone else: the built-in check-in that starts when always listening hears the user, decides from what they said
// and the sounds around them whether they talk to someone else, and holds Martlet back while that lasts.
public sealed partial class CheckInsTests
{
    private static CheckInState OnThePhone() => State() with
    {
        Exchanges = [.. State().Exchanges, new("Hello? Oh, hi Mom.", "[pass]"), new("Yeah, uh-huh. No, I already ate.", "Ooh, what did you have?")],
        Exchanged = 6, Activity = "", Voices = [new("Sam", true, Now.AddSeconds(-20)) { Id = "v1" }], OwnerVoiceKnown = true,
        Heard =
        [
            new(Now.AddMinutes(-11), "Too old to count.", true),
            new(Now.AddSeconds(-50), "Hello? Oh, hi Mom.", false) { Speaker = "Sam", Recognized = true, Owner = true },
            new(Now.AddSeconds(-20), "Yeah, uh-huh. No, I already ate.", true)
            {
                Recognized = true, OtherVoices = 1, Overlap = true, PcPlayed = true
            }
        ]
    };

    [Fact]
    public void TalkingToSomeoneElseIsOnAndStartsWhenYouSaySomething()
    {
        var aside = Built(CheckIns.Aside);
        Assert.Equal((true, 1, CheckInOutcome.HoldBack, PromptCatalog.CheckInAside, CheckInTriggers.Spoke, CheckInConditions.SomethingNew),
            (aside.On, aside.EveryMinutes, aside.Outcome, aside.PromptId, aside.Triggers, aside.Conditions));
        Assert.Equal(CheckInFacts.Heard | CheckInFacts.Conversation | CheckInFacts.Activity | CheckInFacts.People, aside.Facts);
        Assert.Equal(aside.Facts, CheckIns.Placed(PromptSettings.Text(null, PromptCatalog.CheckInAside)));
        Assert.Equal(ThinkingCapability.Text, aside.Needs);

        var state = OnThePhone();
        var spoke = CheckIns.SpokeTrigger(Now.AddSeconds(-1), answered: false);
        Assert.Equal("you said something and Martlet stayed quiet", spoke.What);
        Assert.Equal("it waits for you to say something", CheckIns.Wait(aside, state, null));
        Assert.Equal("it waits for you to say something", CheckIns.Wait(aside, state, null, fired: CheckIns.ExchangeTrigger(Now)));
        // The reply that is still busy never holds it up; its pace is its cooldown, and a trigger waits TriggerAge for it.
        Assert.Null(CheckIns.Wait(aside, state with { Quiet = TimeSpan.Zero }, null, fired: spoke));
        Assert.Equal("next in 40 s", CheckIns.Wait(aside, state, new(Now.AddSeconds(-20), 5, "nothing", false), fired: spoke));
        Assert.Equal(CheckIns.TriggerAge, CheckIns.Age(spoke));
        Assert.Equal("you to say something", CheckIns.TriggerWords(CheckInTriggers.Spoke));
    }

    [Fact]
    public void ItsMessageCarriesWhatWasHeardAndTheSoundsAround()
    {
        var state = OnThePhone();
        var heard = CheckIns.Heard(state);
        Assert.Equal("What always listening heard in the last 10 minutes, oldest first (on a phone Martlet can't see, it hears only " +
            "the user's side):\n" +
            "- 10:16 PM (50 s ago), Sam (the user's own voice): \"Hello? Oh, hi Mom.\" (Mira stayed quiet)\n" +
            "- 10:16 PM (20 s ago), a voice Martlet doesn't know, 1 other voice too, people talked over each other, with lines this " +
            "PC played: \"Yeah, uh-huh. No, I already ate.\" (Mira answered)\n" +
            "Mira doesn't hold back now.", heard);
        Assert.StartsWith("Always listening heard nothing from the user in the last 10 minutes.", CheckIns.Heard(State()));

        var message = CheckIns.Message(Built(CheckIns.Aside), state, null)!;
        Assert.Contains(heard, message);
        Assert.Contains("User: Yeah, uh-huh. No, I already ate.", message);
        Assert.Contains("The user doesn't seem to be doing anything with sound on this PC now.", message);
        Assert.Contains("Only the user seems to be here.", message);
        Assert.Contains("a one-sided conversation, as on a phone", message);
        Assert.Contains("starts with ASIDE:", message);
        Assert.Contains("write only: BACK", message);
        Assert.DoesNotContain("{heard}", message);
        Assert.Equal(1, message.Split("What always listening heard").Length - 1);

        var holding = CheckIns.HoldBack(null, "on the phone with their mom", CheckIns.Aside, Now.AddMinutes(-2));
        Assert.EndsWith("Mira holds back now (since 10:15 PM, 2 min ago), because the user seems to be talking to someone else: " +
            "on the phone with their mom.", CheckIns.Heard(state with { Aside = holding }));
        Assert.EndsWith("Mira doesn't hold back now.", CheckIns.Heard(state with { Aside = holding with { Heard = Now.AddMinutes(-4) } }));
    }

    [Theory]
    [InlineData("ASIDE: on the phone with their mom", true, "on the phone with their mom")]
    [InlineData("<think>Only their voice.</think>\n- **ASIDE:** \"talking to Sam in the room\"", true, "talking to Sam in the room")]
    [InlineData("ASIDE:", true, "someone else")]
    [InlineData("They hung up.\nBACK", true, null)]
    [InlineData("OK", false, null)]
    [InlineData("ASIDE: nobody", false, null)]
    public void ItsAnswerStartsOrEndsTheHold(string answer, bool act, string? about)
    {
        var verdict = CheckIns.Read(Built(CheckIns.Aside), answer, OnThePhone());
        Assert.True(verdict.Readable);
        Assert.Equal((act, about), (verdict.Act, verdict.Text));
        Assert.False(CheckIns.Read(Built(CheckIns.Aside), "REMIND: Hush.", OnThePhone()).Readable);
    }

    [Fact]
    public void TheHoldLastsWhileYouKeepTalkingAndItsNoteGoesWithEveryRequest()
    {
        var hold = CheckIns.HoldBack(null, "on the phone with their mom", CheckIns.Aside, Now);
        Assert.Equal((Now, Now, Now + CheckIns.AsideQuiet), (hold.Since, hold.Heard, hold.Until));
        // The user keeps talking: it lasts from then. Quiet for AsideQuiet: it ends.
        var later = CheckIns.Spoke(hold, Now.AddMinutes(2))!;
        Assert.Equal((Now, Now.AddMinutes(2)), (later.Since, later.Heard));
        Assert.Null(CheckIns.Spoke(later, Now.AddMinutes(5).AddSeconds(1)));
        Assert.Null(CheckIns.Spoke(null, Now));
        // A new ASIDE: line keeps its start and who started it; one after it ended starts again.
        var renewed = CheckIns.HoldBack(later, "still on the phone", "c1", Now.AddMinutes(3));
        Assert.Equal((Now, CheckIns.Aside, "still on the phone"), (renewed.Since, renewed.By, renewed.About));
        Assert.Equal((Now.AddMinutes(9), "c1"), CheckIns.HoldBack(later, "again", "c1", Now.AddMinutes(9)) is var again ? (again.Since, again.By) : default);
        var long_ = CheckIns.HoldBack(null, new string('a', 500), "c1", Now).About;
        Assert.True(long_.Length <= CheckIns.MaximumAsideCharacters + 1 && long_.EndsWith('…'));

        var note = CheckIns.AsideNote(null, hold)!;
        Assert.Contains("talking to someone else now, not to you (on the phone with their mom)", note);
        Assert.Contains("reply only [pass]", note);
        var board = new ContextBoard();
        var source = CheckIns.Source(CheckIns.Aside);
        board.Post(source, note, hold.Heard, CheckIns.AsideQuiet);
        var first = board.Snapshot(Now.AddSeconds(5));
        board.MarkSent(first);
        Assert.Contains(source, first.Sources);
        Assert.Contains(source, board.Snapshot(Now.AddSeconds(30)).Sources);
        Assert.DoesNotContain(source, board.Snapshot(Now + CheckIns.AsideQuiet + TimeSpan.FromSeconds(1)).Sources);
        Assert.Null(CheckIns.AsideNote(new PromptSettings { Overrides = new Dictionary<string, string> { [PromptCatalog.CheckInAsideNote] = "" } }, hold));
    }

    [Fact]
    public void OnACallIsRetiredAndItsSavedChoicesAreDropped()
    {
        Assert.DoesNotContain(CheckIns.All(null), c => c.Id == "call");
        Assert.True(CheckIns.Retired("call"));
        Assert.True(PromptCatalog.Retired("check_in_call"));
        Assert.Null(PromptCatalog.Find("check_in_call"));
        var folder = Path.Combine(Path.GetTempPath(), "Martlet.CheckIns.Tests." + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, CheckInSettings.FileName),
                "{\"SchemaVersion\":1,\"BuiltIn\":{\"call\":{\"On\":true,\"EveryMinutes\":2},\"gaze\":{\"On\":false,\"EveryMinutes\":5}}}");
            var (read, state) = CheckInSettings.Read(folder);
            Assert.Equal("loaded", state);
            Assert.Null(read.Choice("call"));
            Assert.False(CheckIns.All(read).Single(c => c.Id == CheckIns.Gaze).On);
            var prompts = new PromptSettings { Overrides = new Dictionary<string, string> { ["check_in_call"] = "Old.", [PromptCatalog.CheckInAside] = "New." } };
            Assert.Equal(new[] { PromptCatalog.CheckInAside }, prompts.Overrides.Keys);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
