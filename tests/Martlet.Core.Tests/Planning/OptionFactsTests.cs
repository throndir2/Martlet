using Martlet.Core.Planning;

namespace Martlet.Core.Tests.Planning;

public sealed class OptionFactsTests
{
    private static ComponentOption Option(string id) => FootprintCatalog.Default.Find(id)!;

    [Fact]
    public void AGraphicsCardVoiceSaysWhereItRunsItsMemoryAndHowSoonItSpeaks()
    {
        var facts = OptionFacts.Of(Option("chatterbox-turbo")).ToDictionary(f => f.Key);

        Assert.Equal("an NVIDIA graphics card", facts["runs-on"].Value);
        Assert.Equal("NVIDIA GPU", facts["runs-on"].Short);
        Assert.StartsWith("about 3.7 GB, up to 4.2 GB", facts["vram"].Value, StringComparison.Ordinal);
        Assert.Equal("4.2 GB VRAM", facts["vram"].Short);
        Assert.Equal("First audio", facts["speed"].Label);
        Assert.Equal("0.45 s to first audio", facts["speed"].Short);
        Assert.Equal("measured on Martlet hardware", facts["evidence"].Value);
        Assert.DoesNotContain("hears", facts.Keys);
        Assert.Equal("NVIDIA GPU \u00b7 4.2 GB VRAM \u00b7 0.45 s to first audio", OptionFacts.Short(Option("chatterbox-turbo")));
    }

    [Fact]
    public void AProcessorOptionShowsMemoryAndThreadsAndAThinkingModelSaysWhetherItHears()
    {
        var nano = OptionFacts.Of(Option("chatterbox-nano-cpu")).ToDictionary(f => f.Key);
        Assert.Equal("Processor", nano["runs-on"].Short);
        Assert.DoesNotContain("vram", nano.Keys);
        Assert.Equal("8 threads", nano["cpu"].Short);

        var gemma = OptionFacts.Of(Option("gemma4:e2b")).ToDictionary(f => f.Key);
        Assert.Equal("First sentence", gemma["speed"].Label);
        Assert.Equal("hears", gemma["hears"].Short);
        Assert.StartsWith("yes", gemma["hears"].Value, StringComparison.Ordinal);
        Assert.Equal("GPU", gemma["runs-on"].Short);
    }

    [Fact]
    public void AnOnlineOptionSaysWhatItCostsAndUsesNothingLocal()
    {
        var facts = OptionFacts.Of(Option("hosted:nvidia-build")).ToDictionary(f => f.Key);
        Assert.Equal("Online", facts["runs-on"].Short);
        Assert.DoesNotContain("vram", facts.Keys);
        Assert.DoesNotContain("ram", facts.Keys);
        Assert.Equal("free", facts["cost"].Short);
    }

    [Fact]
    public void TheCompareKeysAreTheFactsThatDiffer()
    {
        var keys = OptionFacts.CompareKeys([Option("gemma4:e2b"), Option("gemma4:e4b")]);
        Assert.Contains("vram", keys);
        Assert.Contains("speed", keys);
        Assert.DoesNotContain("runs-on", keys);
        Assert.DoesNotContain("hears", keys);
        Assert.Equal("best", OptionFacts.QualityWord(9));
    }
}
