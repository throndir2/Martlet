using Martlet.Core.Settings;
using Martlet.Desktop;
using Martlet.Providers;
using Martlet.Sherpa;

namespace Martlet.Desktop.Tests;

public sealed class JobOptionsTests
{
    public JobOptionsTests() =>
        // MainWindow's statics include pack:// resources: register the scheme as a WPF app would before using them.
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;

    [Fact]
    public void OllamaModelsListTheSuggestionsTheDownloadedModelsAndAnyModelWithFactsThatTellThemApart()
    {
        var options = JobOptions.OllamaModels(MainWindow.LocalChatModels, ["gemma4:e2b", "llama3.2:3b"], "llama3.2:3b", cardGb: 11.6);

        Assert.Equal([.. MainWindow.LocalChatModels.Select(m => m.Id), "llama3.2:3b", JobOptions.OtherOllamaModel], options.Select(o => o.Key));
        var e2b = options[0];
        Assert.Equal("Gemma 4 E2B", e2b.Name);
        Assert.Equal("recommended", e2b.Badge);
        Assert.Equal("downloaded", e2b.Facts.Single(f => f.Key == "here").Short);
        Assert.Contains(e2b.Facts, f => f.Key == "tools");
        Assert.Contains(e2b.Facts, f => f.Key == "hears" && f.Short == "hears");
        var own = options.Single(o => o.Key == "llama3.2:3b");
        Assert.True(own.InUse);
        Assert.Equal("in use", own.Badge);
        // A model too big for this PC's card says so first, so its compact row warns.
        Assert.Equal("too big here", options.Single(o => o.Key == "gemma4:26b").Facts[0].Short);
        Assert.StartsWith("not downloaded yet", options.Single(o => o.Key == "gemma4:12b").Facts.Single(f => f.Key == "here").Value, StringComparison.Ordinal);
        // Before Ollama was asked, nothing claims to be downloaded or not.
        Assert.DoesNotContain(JobOptions.OllamaModels(MainWindow.LocalChatModels, null, null, 8)[0].Facts, f => f.Key == "here");
    }

    [Fact]
    public void VoiceEnginesCarryWhereEachStandsAndWhyOneCantRun()
    {
        var spots = SpeechEngines.All.Select(engine => (engine, engine == SpeechEngines.Dia
            ? new MainWindow.EngineSpot(false, false, false, "Needs an NVIDIA graphics card with 8 GB+.", "Needs an NVIDIA graphics card with 8 GB+.")
            : engine == SpeechEngines.F5 ? new MainWindow.EngineSpot(true, true, false, null, "Speaking on this PC.")
            : new MainWindow.EngineSpot(false, false, false, null, null))).ToList();

        var options = JobOptions.VoiceEngines(spots, SpeechEngines.Chatterbox).ToDictionary(o => o.Key);

        Assert.Equal(SpeechEngines.All.Count, options.Count);
        Assert.Equal("recommended", options["chatterbox"].Badge);
        Assert.True(options["f5"].InUse);
        Assert.Equal("Speaking on this PC.", options["f5"].State);
        Assert.Equal("Needs an NVIDIA graphics card with 8 GB+.", options["dia"].Unavailable);
        Assert.Null(options["dia"].State);
        Assert.Contains(options["chatterbox"].Facts, f => f.Key == "license");
        Assert.Equal("NVIDIA GPU", options["chatterbox"].Facts[0].Short);

        var cloud = JobOptions.CloudVoices(openAiInUse: false, elevenLabsInUse: true);
        Assert.Equal(["openai", "elevenlabs"], cloud.Select(o => o.Key));
        Assert.True(cloud[1].InUse);
        Assert.Equal("no", cloud[0].Facts.Single(f => f.Key == "cloning").Value);
    }

    [Fact]
    public void RecognizersListParakeetThenWhisperAndSayWhatEachNeeds()
    {
        var state = new JobOptions.RecognizerState(null, ParakeetModels.Tdt110mEnglishId, id => id == ParakeetModels.V3Id, null, null,
            RuntimeReady: true, new ListeningAdvice(false, "the card is busy", "small", "small", "Not enough free graphics memory.", ""),
            GpuInUse: false, CpuInUse: true, Threads: 16);

        var options = JobOptions.Recognizers(state);

        Assert.Equal([.. ParakeetModels.All.Select(m => m.Id), JobOptions.WhisperGpu, JobOptions.WhisperCpu], options.Select(o => o.Key));
        Assert.Equal("recommended", options[0].Badge);
        Assert.Equal("English", options[0].Facts.Single(f => f.Key == "languages").Short);
        Assert.Equal("25 languages", options[2].Facts.Single(f => f.Key == "languages").Short);
        Assert.Equal("Downloaded.", options[2].State);
        Assert.StartsWith("Downloads once", options[0].State, StringComparison.Ordinal);
        Assert.Equal("Not enough free graphics memory.", options[3].Unavailable);
        Assert.True(options[4].InUse);
        Assert.Equal("Processor (in Martlet)", options[0].Facts[0].Short);

        var noRuntime = JobOptions.Recognizers(state with { RuntimeReady = false, Downloading = ParakeetModels.V2EnglishId, Progress = "Downloading: 40%" });
        Assert.NotNull(noRuntime[0].Unavailable);
        Assert.Equal("downloading", noRuntime[1].Badge);
        Assert.Equal("Downloading: 40%", noRuntime[1].State);
    }

    [Fact]
    public void ProvidersSayWhatEachCostsAndWhatLeavesThisPc()
    {
        var thinking = JobOptions.Providers(MainWindow.ThinkingProviders, Martlet.Core.Planning.PlanComponent.Thinking, "openrouter",
            "your messages", ChatCompletionsEndpointCatalog.NvidiaBuildId).ToDictionary(o => o.Key);

        Assert.Equal(["openai", "openrouter", "nvidia-build", "google-gemini", "custom"], thinking.Keys);
        Assert.Equal("in use", thinking["openrouter"].Badge);
        Assert.Equal("recommended", thinking["nvidia-build"].Badge);
        Assert.Equal("free tier", thinking["nvidia-build"].Facts.Single(f => f.Key == "cost").Short);
        Assert.Equal("paid", thinking["openai"].Facts.Single(f => f.Key == "cost").Short);
        Assert.Equal("sent to OpenAI", thinking["openai"].Facts.Single(f => f.Key == "data").Value);
        Assert.Equal("hears", thinking["google-gemini"].Facts.Single(f => f.Key == "hears").Short);

        var fallback = JobOptions.Providers(MainWindow.FallbackProviders, Martlet.Core.Planning.PlanComponent.Thinking, null, "your messages")
            .ToDictionary(o => o.Key);
        Assert.Equal("stays on this PC", fallback["ollama"].Facts.Single(f => f.Key == "data").Value);
        Assert.Contains("openai", fallback.Keys);

        var listening = JobOptions.Providers([MainWindow.ThinkingProviders[0]], Martlet.Core.Planning.PlanComponent.Listening, null, "your recorded speech");
        Assert.Equal("Transcript", listening.Single().Facts.Single(f => f.Key == "speed").Label);
    }

    [Fact]
    public void LocalAppsListOllamaEachAppFoundAndOneTypedByAddress()
    {
        LocalModelServer[] found =
        [
            new("lm-studio", "LM Studio", "http://127.0.0.1:1234/v1", ["gemma", "qwen"]),
            new("llama-cpp", "llama.cpp server", "http://127.0.0.1:8080/v1", []) { NeedsKey = true }
        ];

        var options = JobOptions.LocalApps(found, looking: false, ollamaInstalled: true, ollamaInUse: false, inUseBaseUrl: "http://127.0.0.1:1234/v1");

        Assert.Equal([JobOptions.OllamaApp, "lm-studio-1234", "llama-cpp-8080", JobOptions.AddressApp], options.Select(o => o.Key));
        Assert.Equal("recommended", options[0].Badge);
        Assert.True(options[1].InUse);
        Assert.Equal("2 models", options[1].Facts.Single(f => f.Key == "models").Short);
        Assert.Equal("asks for an API key first", options[2].Facts.Single(f => f.Key == "models").Value);
        Assert.False(options[3].InUse);
        Assert.True(JobOptions.LocalApps([], false, false, false, "http://127.0.0.1:9999/v1")[^1].InUse);
    }

    [Fact]
    public void APickerLeadsWithTheOneInUseTheRecommendedOneAndTheShownOneAndListsWhatCantRunLast()
    {
        PickerOption[] options =
        [
            new("a", "A", "") { Unavailable = "no card" },
            new("b", "B", ""),
            new("c", "C", "") { Badge = "recommended" },
            new("d", "D", ""),
            new("e", "E", "") { InUse = true, Badge = "in use" },
            new("f", "F", "")
        ];

        var (ordered, pinned) = MainWindow.PickerOrder(options, "f");
        Assert.Equal(["e", "c", "f", "b", "d", "a"], ordered.Select(o => o.Key));
        Assert.Equal(3, pinned);

        var (same, one) = MainWindow.PickerOrder(options, "e");
        Assert.Equal(["e", "c", "b", "d", "f", "a"], same.Select(o => o.Key));
        Assert.Equal(2, one);
    }
}
