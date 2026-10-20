using Martlet.Providers.Ollama;

namespace Martlet.Desktop.Tests;

public sealed class CompanionWarmupTests
{
    private static readonly OllamaLoadedModel OnCard = new("gemma4:e4b", 4_900_000_000, 4_900_000_000);
    private static readonly OllamaLoadedModel Split = new("qwen3.5:9b", 6_600_000_000, 4_000_000_000);

    [Fact]
    public void Warmup_shows_only_parts_that_take_a_second_or_more()
    {
        var items = CompanionWarmup.Showing([
            new(WarmupPart.Conversation, null, TimeSpan.FromMilliseconds(300)),
            new(WarmupPart.Thinking, "gemma4:e4b", TimeSpan.FromSeconds(3))]);

        Assert.Equal([WarmupPart.Thinking], items.Select(i => i.Part));
        Assert.Equal("", CompanionWarmup.Line([]));
    }

    [Fact]
    public void Warmup_line_names_each_part_with_its_time()
    {
        var line = CompanionWarmup.Line([
            new(WarmupPart.Thinking, "gemma4:e4b", TimeSpan.FromSeconds(12.6), TimeSpan.FromSeconds(20)),
            new(WarmupPart.Listening, "Parakeet TDT 0.6B v3", TimeSpan.FromSeconds(3)),
            new(WarmupPart.Character, null, TimeSpan.FromSeconds(2))]);

        Assert.Equal("Warming up: loading gemma4:e4b on this PC (12 s of about 20 s), loading Parakeet TDT 0.6B v3 for listening " +
            "(3 s so far) and starting the character.", line);
        Assert.Equal("Warming up: getting the conversation ready.",
            CompanionWarmup.Line([new(WarmupPart.Conversation, null, TimeSpan.FromSeconds(2))]));
        // Under the talk window's "Warming up" title the line starts at what loads.
        Assert.Equal("Loading gemma4:e4b on this PC (3 s so far) and starting the character.", CompanionWarmup.Line([
            new(WarmupPart.Thinking, "gemma4:e4b", TimeSpan.FromSeconds(3)), new(WarmupPart.Character, null, TimeSpan.FromSeconds(2))], lead: false));
        // A load that takes longer than last time counts on instead of saying "of about".
        Assert.Equal("Warming up: loading gemma4:e4b on this PC (25 s so far).",
            CompanionWarmup.Line([new(WarmupPart.Thinking, "gemma4:e4b", TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(20))]));
    }

    [Fact]
    public void Warmup_advice_and_progress_follow_the_model()
    {
        IReadOnlyList<WarmupItem> thinking = [new(WarmupPart.Thinking, "gemma4:e4b", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20))];
        Assert.Equal("You can talk or type now. The first reply starts once the model has loaded.", CompanionWarmup.Advice(thinking, true));
        Assert.Equal("Martlet answers once the model has loaded.", CompanionWarmup.Advice(thinking, false));
        Assert.Equal(0.5, CompanionWarmup.Progress(thinking));
        Assert.Equal(0.95, CompanionWarmup.Progress([new(WarmupPart.Thinking, "m", TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(20))]));
        Assert.Null(CompanionWarmup.Progress([new(WarmupPart.Thinking, "m", TimeSpan.FromSeconds(5))]));
        Assert.Equal("Martlet hears you once it has loaded.",
            CompanionWarmup.Advice([new(WarmupPart.Listening, "Parakeet", TimeSpan.FromSeconds(2))], false));
        Assert.Equal("Warmed up in 18 s. Martlet is ready to talk.", CompanionWarmup.Done(TimeSpan.FromSeconds(18.4)));
    }

    [Fact]
    public void Holdings_say_what_Martlet_uses_this_PC_for()
    {
        Assert.Equal("Martlet holds no models on this PC now, so the character and the conversation are off.",
            CompanionWarmup.HoldingsLine(PcHoldings.Nothing));
        Assert.False(PcHoldings.Nothing.Any);

        var held = new PcHoldings([OnCard, Split], "Parakeet TDT 0.6B v3", true, true, true, true, false);
        Assert.True(held.Any);
        Assert.Equal("Martlet uses this PC for gemma4:e4b (4.9 GB of graphics memory), qwen3.5:9b (4.0 GB of graphics memory and " +
            "2.6 GB of memory), Parakeet TDT 0.6B v3 for listening, voice recognition, the character and the conversation (listening).",
            CompanionWarmup.HoldingsLine(held));
        Assert.Equal("Martlet uses this PC for the conversation (listening and watching).",
            CompanionWarmup.HoldingsLine(PcHoldings.Nothing with { Conversation = true, Hearing = true, Watching = true }));
        Assert.Equal("Martlet uses this PC for cpu-model (6.0 GB of memory).",
            CompanionWarmup.HoldingsLine(PcHoldings.Nothing with { Models = [new("cpu-model", 6_000_000_000, 0)] }));
    }

    [Fact]
    public void Free_up_says_what_it_did_and_what_failed()
    {
        var result = new FreeUpResult(true, true, ["gemma4:e4b", "Parakeet TDT 0.6B v3"], 4_900_000_000, 0, []);
        Assert.Equal("Martlet ended the conversation, hid the character and unloaded gemma4:e4b and Parakeet TDT 0.6B v3 " +
            "(4.9 GB of graphics memory).", CompanionWarmup.FreedLine(result));
        Assert.Equal("Martlet ended the conversation, hid the character and unloaded gemma4:e4b and Parakeet TDT 0.6B v3 " +
            "(4.9 GB of graphics memory). Start talking or Start listening warms it up again.", CompanionWarmup.RestingLine(result));

        var nothing = new FreeUpResult(false, false, [], 0, 0, ["Ollama on this PC didn't unload big:70b."]);
        Assert.False(nothing.AnyDone);
        Assert.Equal("Martlet held nothing to free on this PC. Ollama on this PC didn't unload big:70b.", CompanionWarmup.FreedLine(nothing));
    }

    [Fact]
    public void Split_puts_a_model_partly_on_the_processor_in_both_memories()
    {
        Assert.Equal((4_900_000_000L, 0L), CompanionWarmup.Split(OnCard));
        Assert.Equal((4_000_000_000L, 2_600_000_000L), CompanionWarmup.Split(Split));
        Assert.Equal((0L, 6_000_000_000L), CompanionWarmup.Split(new("cpu", 6_000_000_000, 0)));
    }
}
