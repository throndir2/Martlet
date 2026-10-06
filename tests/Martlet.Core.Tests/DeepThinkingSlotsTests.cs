using Martlet.Core.Installation;
using Martlet.Core.Settings;

namespace Martlet.Core.Tests;

public sealed class DeepThinkingSlotsTests
{
    [Fact]
    public void Slots_are_the_thinks_whose_contexts_fit_beside_the_hosts_other_models()
    {
        (string, double)[] thinking = [("Thinking's gemma4:e4b", 8)];
        // 24 GB less Thinking (8), the reserve (0.8) and the model (5.7) leaves 9.5 GB: two contexts of about 4.3 GB.
        var (two, why) = DeepThinkingSlots.Fit("qwen3:8b", 5.7, 24, thinking);
        Assert.Equal(2, two);
        Assert.Contains("2 thinks' contexts", why);
        Assert.Contains("beside Thinking's gemma4:e4b (about 8 GB)", why);
        // Nothing left beside Thinking: one, as before, and it says so.
        var (one, full) = DeepThinkingSlots.Fit("qwen3:8b", 5.7, 12, thinking);
        Assert.Equal(1, one);
        Assert.Contains("already fills", full);
        // A big card alone stops at the bound; no card thinks one at a time on the processor.
        Assert.Equal(SelfHostSetup.DeepThinkingMaximumSlots, DeepThinkingSlots.Fit("qwen3:8b", 5.7, 48, []).Slots);
        Assert.Equal(1, DeepThinkingSlots.Fit("qwen3:8b", 5.7, null, []).Slots);
    }

    [Fact]
    public void A_think_context_grows_with_the_model_and_its_context_size()
    {
        Assert.True(DeepThinkingSlots.ContextGb(17) > DeepThinkingSlots.ContextGb(5.7));
        Assert.Equal(DeepThinkingSlots.ContextGb(8) / 4, DeepThinkingSlots.ContextGb(8, GenerationSettings.MaximumHostContextTokens / 4), 3);
        Assert.True(DeepThinkingSlots.ContextGb(0.1, 1_024) > 0);
    }
}
