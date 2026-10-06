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

    [Fact]
    public void Gemma_4_uses_the_footprint_catalog_whose_sliding_window_contexts_are_small()
    {
        // The catalog's 32,768-token context, not three quarters of the model (about 14 GB for gemma4:26b before).
        Assert.Equal(0.9, DeepThinkingSlots.ContextGb("gemma4:26b", 19.2), 3);
        Assert.Equal(0.7 / 4, DeepThinkingSlots.ContextGb("gemma4:e4b", 7.1, GenerationSettings.DefaultHostContextTokens), 3);
        Assert.Equal(DeepThinkingSlots.ContextGb(5.7), DeepThinkingSlots.ContextGb("qwen3:8b", 5.7));
        Assert.Equal(4.9, DeepThinkingSlots.ThinkingGb("gemma4:e4b", 7.1), 3);
        Assert.Equal(18.5, DeepThinkingSlots.ModelGb("gemma4:26b", 19.2), 3);
        Assert.Equal(4.2, DeepThinkingSlots.RoleGb("chatterbox")!.Value, 3);
        Assert.Null(DeepThinkingSlots.RoleGb("no-such-role"));

        // 24 GB less Thinking's gemma4:e2b (3.3), the reserve (0.8) and gemma4:26b (18.5) leaves 1.4 GB: one 0.9 GB context.
        var (one, why) = DeepThinkingSlots.Fit("gemma4:26b", 19.2, 24, [("Thinking's gemma4:e2b", 3.3)]);
        Assert.Equal(1, one);
        Assert.Contains("gemma4:26b (about 18.5 GB) plus 1 think's context (about 0.9 GB each)", why);
        Assert.Equal(SelfHostSetup.DeepThinkingMaximumSlots, DeepThinkingSlots.Fit("gemma4:12b", 8.5, 24, [("Thinking's gemma4:e4b", 4.9)]).Slots);
    }
}
