using Martlet.Providers;

namespace Martlet.Conversation.Tests;

public sealed class ModelBargeInJudgeTests
{
    private static BargeInJudgeInput Input(string heard) => new(heard, "The best part is the view from the top.",
        "We hiked for hours. The best part is the view from the top.",
        new UtteranceContext { Voiced = TimeSpan.FromMilliseconds(900), Names = ["Robin"] }, ListeningSensitivity.Normal, 0.82);

    [Theory]
    [InlineData("INTERRUPT", BargeInVerdict.Interrupt)]
    [InlineData("interrupt: asks a new question", BargeInVerdict.Interrupt)]
    [InlineData("**NOTFORME**", BargeInVerdict.NotForMe)]
    [InlineData("Not for me: talking to someone else", BargeInVerdict.NotForMe)]
    [InlineData("NOT_FOR_ME", BargeInVerdict.NotForMe)]
    [InlineData("  \"NotForMe.\"", BargeInVerdict.NotForMe)]
    public void The_verdict_is_read_from_the_first_words(string answer, BargeInVerdict expected) =>
        Assert.Equal(expected, ModelBargeInJudge.Parse(answer)!.Verdict);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("I am not sure what you mean.")]
    public void An_answer_without_a_verdict_names_none(string? answer) => Assert.Null(ModelBargeInJudge.Parse(answer));

    [Fact]
    public void The_reason_is_short_and_says_the_model_judged()
    {
        Assert.Equal("the model judge: asks a new question", ModelBargeInJudge.Parse("INTERRUPT: asks a new question")!.Reason);
        Assert.Equal("the model judge: not for Martlet", ModelBargeInJudge.Parse("NOTFORME")!.Reason);
    }

    [Fact]
    public void The_prompt_has_the_names_the_sentence_the_reply_the_words_and_the_confidence()
    {
        var prompt = ModelBargeInJudge.Prompt(Input("Sorry, I was talking to my brother."));
        Assert.Contains("Martlet or Robin", prompt);
        Assert.Contains("The assistant is saying: \"The best part is the view from the top.\"", prompt);
        Assert.Contains("The end of its reply so far: \"We hiked for hours.", prompt);
        Assert.Contains("Heard over it: \"Sorry, I was talking to my brother.\"", prompt);
        Assert.Contains("Speech-to-text confidence: 0.82", prompt);
    }

    [Fact]
    public async Task A_model_verdict_in_time_is_used()
    {
        string? asked = null;
        var judge = new ModelBargeInJudge((instructions, text, _) =>
        {
            asked = instructions + "|" + text;
            return Task.FromResult<string?>("NOTFORME: talking to someone else");
        }, "Thinking pool");
        var ruling = await BargeInJudging.RuleAsync(judge, Input("What about the weather tomorrow?"), TimeProvider.System,
            TimeSpan.FromSeconds(30));
        Assert.Equal(BargeInVerdict.NotForMe, ruling.Verdict);
        Assert.Equal(BargeInSource.Judge, ruling.Source);
        Assert.Equal("Thinking pool", ruling.Judge);
        Assert.StartsWith(ModelBargeInJudge.Instructions, asked);
    }

    [Fact]
    public async Task No_model_available_lets_the_rules_decide_at_once_without_waiting_for_the_deadline()
    {
        var clock = new RuntimeClock();
        var judge = new ModelBargeInJudge((_, _, _) => Task.FromResult<string?>(null), "Thinking pool");
        var ruling = await BargeInJudging.RuleAsync(judge, Input("What about the weather tomorrow?"), clock)
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(BargeInSource.Timeout, ruling.Source);
        Assert.Equal("rules", ruling.Judge);
        Assert.Equal(BargeInVerdict.Interrupt, ruling.Verdict);
        Assert.Contains("no Thinking pool judge was available", ruling.Reason);
        // The simulated clock never moved: nothing waited for the 400 ms deadline.
        Assert.Equal(TimeSpan.Zero, ruling.JudgeTime);
    }

    [Fact]
    public async Task An_answer_without_a_verdict_lets_the_rules_decide()
    {
        var judge = new ModelBargeInJudge((_, _, _) => Task.FromResult<string?>("Hmm, hard to say."));
        var ruling = await BargeInJudging.RuleAsync(judge, Input("Yeah that's so true."), TimeProvider.System, TimeSpan.FromSeconds(30));
        Assert.Equal(BargeInSource.Timeout, ruling.Source);
        Assert.Equal(BargeInVerdict.NotForMe, ruling.Verdict);
        Assert.Contains("judge failed", ruling.Reason);
    }
}
