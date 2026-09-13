using System.Globalization;

namespace Martlet.Participation.Tests;

public sealed class AddressCorpusTests
{
    // Authored synthetic AC-09 cases, not recordings or a claim of real-world language accuracy.
    public static TheoryData<string, string, PolicyReason> NameCorpus => new()
    {
        { "N01 canonical vocative", "Martlet, can you help?", PolicyReason.NameAddressed },
        { "N02 greeting and case", "hEy mArTlEt, please help.", PolicyReason.NameAddressed },
        { "N03 hello", "Hello Martlet, how do we start?", PolicyReason.NameAddressed },
        { "N04 configured alias", "Bird, what is next?", PolicyReason.NameAddressed },
        { "N05 multiword alias", "Little Bird, where do we go?", PolicyReason.NameAddressed },
        { "N06 compatibility normalization", "\uff2d\uff41\uff52\uff54\uff4c\uff45\uff54, can you help?", PolicyReason.NameAddressed },
        { "N07 canonical accent", "E\u0301lan, could you help?", PolicyReason.NameAddressed },
        { "N08 English contraction", "Martlet, what's next?", PolicyReason.NameAddressed },
        { "N09 outer whitespace", "  Martlet, please help.  ", PolicyReason.NameAddressed },
        { "N10 embedded substring", "Supermartlet, can you help?", PolicyReason.NotAddressed },
        { "N11 similar name", "Martlett, can you help?", PolicyReason.NotAddressed },
        { "N12 possessive", "Martlet's advice could help.", PolicyReason.NotAddressed },
        { "N13 mentioned not addressed", "I think Martlet can help.", PolicyReason.NotAddressed },
        { "N14 reported speech", "Martlet said we should leave.", PolicyReason.NotAddressed },
        { "N15 report after comma", "Martlet, she said, can you help?", PolicyReason.NotAddressed },
        { "N16 double quotation", "\"Martlet, can you help?\"", PolicyReason.NotAddressed },
        { "N17 single quotation", "'Martlet, can you help?'", PolicyReason.NotAddressed },
        { "N18 curly quotation", "\u201cMartlet, can you help?\u201d", PolicyReason.NotAddressed },
        { "N19 guillemets", "\u00abMartlet, can you help?\u00bb", PolicyReason.NotAddressed },
        { "N20 nested punctuation", "([\"Martlet, can you help?\"])", PolicyReason.NotAddressed },
        { "N21 inline code", "`Martlet, can you help?`", PolicyReason.NotAddressed },
        { "N22 fenced code", "```text\nMartlet, can you help?\n```", PolicyReason.NotAddressed },
        { "N23 unclosed fence", "~~~Martlet, can you help?", PolicyReason.NotAddressed },
        { "N24 block quote", "> Martlet, can you help?", PolicyReason.NotAddressed },
        { "N25 quoted request inside address", "Martlet, can you repeat \"hello\"?", PolicyReason.NotAddressed },
        { "N26 trailing address unsupported", "What do you think, Martlet?", PolicyReason.NotAddressed },
        { "N27 missing comma unsupported", "Martlet can you help?", PolicyReason.NotAddressed },
        { "N28 name only unsupported", "Martlet", PolicyReason.NotAddressed },
        { "N29 cue without payload", "Martlet, can you ?", PolicyReason.NotAddressed },
        { "N30 newline before name", "Someone said:\nMartlet, can you help?", PolicyReason.NotAddressed },
        { "N31 punctuation between greeting/name", "Hey! Martlet, can you help?", PolicyReason.NotAddressed },
        { "N32 invisible word joiner", "Mart\u200blet, can you help?", PolicyReason.NotAddressed },
        { "N33 homoglyph not transliterated", "M\u0430rtlet, can you help?", PolicyReason.NotAddressed },
        { "N34 prefix boundary", "Little Birdsong, can you help?", PolicyReason.NotAddressed },
        { "N35 apostrophe as closing quote", "Martlet, can you help?'", PolicyReason.NotAddressed },
        { "N36 nested report at start", "She said (Martlet, can you help?)", PolicyReason.NotAddressed },
        { "N37 fullwidth quotation", "\uff02Martlet, can you help?\uff02", PolicyReason.NotAddressed },
        { "N38 unsupported colon vocative", "Martlet: can you help?", PolicyReason.NotAddressed },
        { "N39 ordinary question", "How do we start?", PolicyReason.NotAddressed },
        { "N40 postposed reported speech", "Martlet, can you help, she asked?", PolicyReason.NotAddressed },
        { "N41 postposed report statement", "Martlet, can you help, he said.", PolicyReason.NotAddressed },
        { "N42 deliberate reporting-word false negative", "Martlet, what does the sign say that he wrote?", PolicyReason.NotAddressed },
        { "N43 curly single quotation", "\u2018Martlet, can you help?\u2019", PolicyReason.NotAddressed }
    };

    [Theory]
    [MemberData(nameof(NameCorpus))]
    public void Authored_name_cases(string label, string text, PolicyReason expected)
    {
        Assert.NotEmpty(label);
        var clock = new ManualClock();
        var config = new ParticipationConfiguration(aliases: ["Bird", "Little Bird", "\u00c9lan"],
            mode: ParticipationMode.NameAddressed);
        var policy = new ParticipationPolicy(Guid.NewGuid(), config, PolicyFixtures.Consented, clock);
        clock.Advance(TimeSpan.FromSeconds(1));
        var decision = policy.Decision(PolicyFixtures.Ambient(text));
        Assert.Equal(expected, decision.Reason);
        Assert.Equal(expected == PolicyReason.NameAddressed ? DecisionKind.Allow : DecisionKind.Suppress, decision.Kind);
    }

    public static TheoryData<string, string, PolicyReason> GroupCorpus => new()
    {
        { "G01 open group knowledge question", "Does anyone know the next move?", PolicyReason.GroupInvitation },
        { "G02 open explanation question", "Can anyone explain the puzzle?", PolicyReason.GroupInvitation },
        { "G03 ordinary question not invitation", "Where is the exit?", PolicyReason.NotAddressed },
        { "G04 report", "He asked does anyone know the next move?", PolicyReason.NotAddressed },
        { "G05 quoted invitation", "\"Does anyone know the next move?\"", PolicyReason.NotAddressed },
        { "G06 trailing statement", "Does anyone know the next move.", PolicyReason.NotAddressed },
        { "G07 payload required", "Can anyone explain ?", PolicyReason.NotAddressed },
        { "G08 code", "```Does anyone know the next move?```", PolicyReason.NotAddressed },
        { "G09 incidental game noise", "Watch out!", PolicyReason.NotAddressed },
        { "G10 postposed report", "Does anyone know the next move, she asked?", PolicyReason.NotAddressed }
    };

    [Theory]
    [MemberData(nameof(GroupCorpus))]
    public void Authored_group_cases(string label, string text, PolicyReason expected)
    {
        Assert.NotEmpty(label);
        var clock = new ManualClock();
        var policy = PolicyFixtures.Policy(clock);
        clock.Advance(TimeSpan.FromSeconds(2));
        var decision = policy.Decision(PolicyFixtures.Ambient(text));
        Assert.Equal(expected, decision.Reason);
        Assert.Equal(expected == PolicyReason.GroupInvitation, decision.Unsolicited);
    }

    [Fact]
    public void English_policy_is_independent_of_host_Turkish_casing()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var clock = new ManualClock();
            var policy = new ParticipationPolicy(Guid.NewGuid(),
                new("Indigo", mode: ParticipationMode.NameAddressed), PolicyFixtures.Consented, clock);
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(PolicyReason.NameAddressed, policy.Decision(PolicyFixtures.Ambient("indigo, can you help?")).Reason);
            Assert.Equal(PolicyReason.NotAddressed, policy.Decision(PolicyFixtures.Ambient("\u0130ndigo, can you help?")).Reason);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
