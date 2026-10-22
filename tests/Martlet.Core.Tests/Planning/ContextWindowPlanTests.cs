using Martlet.Core.Planning;
using Martlet.Core.Settings;

namespace Martlet.Core.Tests.Planning;

public sealed class ContextWindowPlanTests
{
    [Fact]
    public void A_24_GB_card_gives_gemma4_12b_the_largest_window()
    {
        // Martruby's RTX 3090: Gemma 4 12B takes about 9 GB at 8,192 tokens; 32,768 tokens add about 0.7 GB.
        var plan = ContextWindowPlan.Fit("gemma4:12b", 9.0, 24, [], cardName: "the RTX 3090");
        Assert.Equal(GenerationSettings.PlannedContextTokens, plan.Tokens);
        Assert.Equal(0.7, plan.ExtraGb, 2);
        Assert.Contains("fits the RTX 3090's 24 GB", plan.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_full_card_keeps_the_usual_window_and_a_little_room_gives_a_step_up()
    {
        Assert.Equal(8_192, ContextWindowPlan.Fit("gemma4:12b", 9.0, 10, []).Tokens);
        Assert.Equal(8_192, ContextWindowPlan.Fit("gemma4:12b", 9.0, 11, []).Tokens);
        Assert.Equal(16_384, ContextWindowPlan.Fit("gemma4:12b", 9.0, 11.1, []).Tokens);
        // Another model on the card takes its room first.
        Assert.Equal(8_192, ContextWindowPlan.Fit("gemma4:12b", 9.0, 24, [("the F5 voice", 13.5)]).Tokens);
    }

    [Fact]
    public void An_unknown_model_is_guessed_large_and_the_models_limit_and_an_unknown_card_cap_it()
    {
        // A full-attention model's cache is about three quarters of its size at 32,768 tokens.
        Assert.Equal(32_768, ContextWindowPlan.Fit("someone/model:9b", 9.0, 16, []).Tokens);
        Assert.Equal(16_384, ContextWindowPlan.Fit("someone/model:9b", 9.0, 14, []).Tokens);
        Assert.Equal(16_384, ContextWindowPlan.Fit("gemma4:12b", 9.0, 24, [], modelMaximum: 16_384).Tokens);
        var unknown = ContextWindowPlan.Fit("gemma4:12b", 9.0, null, []);
        Assert.Equal((8_192, 0.0), (unknown.Tokens, unknown.ExtraGb));
    }
}
