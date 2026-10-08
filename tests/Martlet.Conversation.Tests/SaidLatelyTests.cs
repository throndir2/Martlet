using Martlet.Core.Settings;

namespace Martlet.Conversation.Tests;

public sealed class SaidLatelyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 22, 17, 0, TimeSpan.FromHours(-7));

    [Fact]
    public void NotesWhatMartletSaidOnOneLineButNeverAPass()
    {
        var said = new SaidLately();
        Assert.True(said.Add(Now.AddMinutes(-30), "Ooh, that boss is almost down!"));
        Assert.False(said.Add(Now.AddMinutes(-29), "[pass]"));
        Assert.False(said.Add(Now.AddMinutes(-28), " (pass) "));
        Assert.False(said.Add(Now.AddMinutes(-27), "  "));
        Assert.False(said.Add(Now.AddMinutes(-26), null));
        Assert.True(said.Add(Now.AddMinutes(-25), "Nice dodge!\n\nThat was  close."));
        Assert.True(said.Add(Now.AddMinutes(-3), new string('a', 400)));
        var recent = said.Recent(Now);
        Assert.Equal(3, recent.Count);
        Assert.Equal("Nice dodge! That was close.", recent[1].Text);
        Assert.Equal(SaidLately.MaximumCharacters + 1, recent[2].Text.Length);
        Assert.EndsWith("…", recent[2].Text);

        said.Clear();
        Assert.Equal(0, said.Count);
        Assert.Empty(said.Recent(Now));
    }

    [Fact]
    public void KeepsTheNewestWithinTheHourOldestFirst()
    {
        var said = new SaidLately();
        said.Add(Now.AddMinutes(-61), "Too long ago.");
        for (var i = 1; i <= 12; i++) said.Add(Now.AddMinutes(-13 + i), "Remark " + i);
        Assert.Equal(SaidLately.MaximumSayings, said.Count);
        var recent = said.Recent(Now);
        Assert.Equal(Enumerable.Range(3, 10).Select(i => "Remark " + i), recent.Select(s => s.Text));

        var old = new SaidLately();
        old.Add(Now.AddMinutes(-61), "Too long ago.");
        old.Add(Now.AddMinutes(-59), "Still lately.");
        Assert.Equal(["Still lately."], old.Recent(Now).Select(s => s.Text));
        Assert.Empty(old.Recent(Now.AddHours(2)));
    }

    [Fact]
    public void LinesSayWhenEachWasSaidAndTheNoteAsksWhetherItIsWorthSayingAgain()
    {
        Saying[] said =
        [
            new(Now.AddMinutes(-30), "Ooh, that boss is almost down!"),
            new(Now.AddMinutes(-12), "Ooh, that boss is almost down!"),
            new(Now.AddSeconds(-40).ToUniversalTime(), "Nice dodge!")
        ];
        var lines = SaidLately.Lines(said, Now);
        Assert.Equal("- 9:47 PM (30 min ago): \"Ooh, that boss is almost down!\"\n" +
            "- 10:05 PM (12 min ago): \"Ooh, that boss is almost down!\"\n" +
            "- 10:16 PM (40 s ago): \"Nice dodge!\"", lines);

        var note = SaidLately.Note(null, said, Now, "pass")!;
        Assert.StartsWith("What you said lately, oldest first (it is 10:17 PM now):\n" + lines + "\n", note);
        Assert.Contains("not even in other words", note);
        Assert.Contains("worth saying again", note);
        Assert.EndsWith("reply [pass].", note);
        Assert.Null(SaidLately.Note(null, [], Now, "pass"));
        Assert.Null(SaidLately.Note(new PromptSettings { Overrides = new Dictionary<string, string> { [PromptCatalog.SaidLately] = "" } },
            said, Now, "pass"));

        var prompt = PromptCatalog.Find(PromptCatalog.SaidLately)!;
        Assert.Equal(PromptCatalog.ConversationGroup, prompt.Group);
        Assert.False(PromptCatalog.Required(PromptCatalog.SaidLately));
        foreach (var placeholder in prompt.Placeholders) Assert.Contains("{" + placeholder + "}", prompt.Default);
    }

    [Fact]
    public void OnlyWhatMartletSaysOnItsOwnCarriesIt()
    {
        Assert.True(SaidLately.Carries(MomentTrigger.Look));
        Assert.True(SaidLately.Carries(MomentTrigger.Report));
        Assert.True(SaidLately.Carries(MomentTrigger.PcAudio));
        // A reply to the user's words or touches stays as it was, so its first words never wait.
        Assert.False(SaidLately.Carries(MomentTrigger.User));
        Assert.False(SaidLately.Carries(MomentTrigger.Touch));

        Assert.Equal("the picture and 1 thing Martlet said lately", MomentTurn.Describe(false, 0, true, null, 0, said: 1));
        Assert.Equal("1 line this PC played, 1 context note and 4 things Martlet said lately",
            MomentTurn.Describe(false, 1, false, null, 0, contextNotes: 1, said: 4));
        Assert.Equal("your words", MomentTurn.Describe(true, 0, false, null, 0));
    }
}
