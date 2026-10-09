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

    [Fact]
    public void AThinkingModelSaysWhetherItFitsThisCardCallsToolsAndLeadsWithItsFit()
    {
        var gemma12 = Option("gemma4:12b");
        var roomy = OptionFacts.ThinkingModel(gemma12, cardGb: 16, comfortableGb: 16, callsTools: true).ToDictionary(f => f.Key);
        Assert.StartsWith("yes", roomy["fits"].Value, StringComparison.Ordinal);
        Assert.Null(roomy["fits"].Short);
        Assert.StartsWith("yes", roomy["tools"].Value, StringComparison.Ordinal);
        Assert.Contains("8,192 tokens", roomy["context"].Value, StringComparison.Ordinal);

        var tight = OptionFacts.ThinkingModel(gemma12, cardGb: 11.6, comfortableGb: 16, callsTools: true);
        Assert.Equal("fits", tight[0].Key);
        Assert.Equal("tight here", tight[0].Short);
        var tooBig = OptionFacts.ThinkingModel(Option("gemma4:26b"), cardGb: 8, comfortableGb: 24, callsTools: true);
        Assert.Equal("too big here", tooBig[0].Short);
        Assert.Equal("no card here", OptionFacts.ThinkingModel(gemma12, null, 16, true)[0].Short);
        Assert.Equal("evidence", tooBig[^1].Key);
    }

    [Fact]
    public void AVoiceEngineSaysWhatItCanDoItsLanguagesAndWhetherItsLicenseAllowsCommercialUse()
    {
        var turbo = OptionFacts.VoiceEngine(Martlet.Core.Settings.SpeechEngines.Chatterbox).ToDictionary(f => f.Key);
        Assert.Equal("yes", turbo["cloning"].Value);
        Assert.Equal("laughs", turbo["sounds"].Short);
        Assert.Equal("whispering only", turbo["emotions"].Value);
        Assert.Equal("MIT: commercial use allowed", turbo["license"].Value);
        Assert.Equal("4.2 GB VRAM", turbo["vram"].Short);

        var f5 = OptionFacts.VoiceEngine(Martlet.Core.Settings.SpeechEngines.F5).ToDictionary(f => f.Key);
        Assert.EndsWith("non-commercial use only", f5["license"].Value, StringComparison.Ordinal);
        Assert.Equal("English, Chinese", f5["languages"].Value);
        Assert.StartsWith("yes", OptionFacts.VoiceEngine(Martlet.Core.Settings.SpeechEngines.Xtts).Single(f => f.Key == "streams").Value, StringComparison.Ordinal);

        var nano = OptionFacts.VoiceEngine(Martlet.Core.Settings.SpeechEngines.ChatterboxNano).ToDictionary(f => f.Key);
        Assert.Equal("NVIDIA GPU or processor", nano["runs-on"].Short);

        var eleven = OptionFacts.VoiceEngine(Martlet.Core.Settings.SpeechEngines.ElevenLabs).ToDictionary(f => f.Key);
        Assert.Equal("Online", eleven["runs-on"].Short);
        Assert.Equal("paid", eleven["cost"].Short);
        Assert.DoesNotContain("vram", eleven.Keys);
    }

    [Fact]
    public void ASpeechRecognizerLeadsWithWhereItRunsHowSoonAndItsLanguages()
    {
        var facts = OptionFacts.SpeechRecognizer(Option("parakeet-tdt-0.6b-v3-cpu"), "25 European languages", "very good", streams: false);
        Assert.Equal(["runs-on", "speed", "languages"], facts.Take(3).Select(f => f.Key));
        Assert.Equal("Processor (in Martlet)", facts[0].Short);
        Assert.Contains(facts, f => f.Key == "cpu" && f.Value.StartsWith("2.6 threads", StringComparison.Ordinal));
        Assert.Contains(facts, f => f.Key == "accuracy" && f.Value == "very good");
        Assert.Equal("evidence", facts[^1].Key);
    }

    [Fact]
    public void AHostedProviderSaysWhatItCostsWhatItNeedsAndWhatLeavesThisPc()
    {
        var nvidia = OptionFacts.Hosted("NVIDIA Build", Option("hosted:nvidia-build"), free: true, keyNeeded: true, "your messages");
        Assert.Equal(["runs-on", "cost", "speed"], nvidia.Take(3).Select(f => f.Key));
        var facts = nvidia.ToDictionary(f => f.Key);
        Assert.Equal("free tier", facts["cost"].Short);
        Assert.Equal("your own key", facts["key"].Value);
        Assert.Equal("sent to NVIDIA Build", facts["data"].Value);
        Assert.StartsWith("NVIDIA Build gets your messages", facts["data"].Help, StringComparison.Ordinal);
        Assert.Contains("reliability", facts.Keys);

        var custom = OptionFacts.Hosted("the server", null, null, keyNeeded: false, "your messages").ToDictionary(f => f.Key);
        Assert.Equal("depends on the server", custom["cost"].Value);
        Assert.Equal("only if the server asks", custom["key"].Value);
        Assert.Null(custom["cost"].Short);
        Assert.DoesNotContain("evidence", custom.Keys);
        Assert.DoesNotContain("evidence", facts.Keys);
        var gemini = OptionFacts.Hosted("Google Gemini", null, true, true, "your messages", hears: true, sees: true).ToDictionary(f => f.Key);
        Assert.Equal("hears", gemini["hears"].Short);
    }

    [Fact]
    public void LeadMovesTheChosenFactsFirstAndKeepsTheRest()
    {
        var facts = OptionFacts.Of(Option("chatterbox-turbo"));
        var led = OptionFacts.Lead(facts, "speed", "missing", "runs-on");
        Assert.Equal(["speed", "runs-on"], led.Take(2).Select(f => f.Key));
        Assert.Equal(facts.Count, led.Count);
    }

    [Fact]
    public void ThinkingsOwnModelTakesNothingAndIsAsGoodAsThinking()
    {
        var facts = OptionFacts.Of(Option("vision:thinking")).ToDictionary(f => f.Key);

        Assert.Equal("With Thinking", facts["runs-on"].Short);
        Assert.Equal("the same as your Thinking model", facts["quality"].Value);
        Assert.DoesNotContain("vram", facts.Keys);
        Assert.DoesNotContain("ram", facts.Keys);
        Assert.DoesNotContain("download", facts.Keys);
        Assert.Contains("runs-on", OptionFacts.CompareKeys([Option("vision:thinking"), Option("vision:qwen2.5vl:7b")]));
        Assert.Equal("GPU \u00b7 7.5 GB VRAM", OptionFacts.Short(Option("vision:qwen2.5vl:7b")));
    }

    [Fact]
    public void ReadingAndSmartHomeSayTheyRunOnAProcessorAndWhatTheyNeed()
    {
        var windows = OptionFacts.Of(Option("reading:windows-ocr")).ToDictionary(f => f.Key);
        Assert.Equal("Processor (in Martlet)", windows["runs-on"].Short);
        Assert.Equal("Read time", windows["speed"].Label);
        Assert.EndsWith("to read the screen", windows["speed"].Short, StringComparison.Ordinal);
        Assert.DoesNotContain("needs", windows.Keys);

        var rapid = OptionFacts.Of(Option("reading:rapidocr")).ToDictionary(f => f.Key);
        Assert.Equal("Processor", rapid["runs-on"].Short);
        Assert.Contains("Docker", rapid["needs"].Value, StringComparison.Ordinal);
        Assert.Equal("4 threads", rapid["cpu"].Short);
        Assert.Equal("0.9 s to read the screen", rapid["speed"].Short);

        var home = OptionFacts.Of(Option("smart-home:home-assistant")).ToDictionary(f => f.Key);
        Assert.Equal("Linux", home["needs"].Short);
        Assert.StartsWith("Processor \u00b7 Linux", OptionFacts.Short(Option("smart-home:home-assistant")), StringComparison.Ordinal);
    }

    [Fact]
    public void AnOnlineAudioModelSaysItIsFreeAndNeedsAnAccount()
    {
        var facts = OptionFacts.Of(Option("hosted:nvidia-build-hearing")).ToDictionary(f => f.Key);
        Assert.Equal("Online", facts["runs-on"].Short);
        Assert.Equal("free tier, needs a free account and key", facts["cost"].Value);
    }
}
