using Martlet.Core.Settings;

namespace Martlet.Core.Tests;

public sealed class ModelAbilitiesTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 20, 0, 0, TimeSpan.FromHours(-7));

    [Fact]
    public void A_later_finding_that_says_nothing_keeps_what_was_known()
    {
        var abilities = new ModelAbilities()
            .With(new() { Origin = "http://127.0.0.1:8080/v1", ModelId = "voxtral", Sees = false, Source = "llama.cpp", CheckedAt = At })
            .With(new() { Origin = "http://127.0.0.1:8080/v1", ModelId = "voxtral", Hears = true, Source = "a test request", CheckedAt = At.AddMinutes(1) });

        var found = Assert.Single(abilities.Models);
        Assert.True(found.Hears);
        Assert.False(found.Sees);
        Assert.Equal("a test request", found.Source);
        Assert.Equal(TimeSpan.Zero, found.CheckedAt.Offset);
    }

    [Fact]
    public void Newest_model_comes_first_and_the_list_is_bounded()
    {
        var abilities = new ModelAbilities();
        for (var i = 0; i < ModelAbilities.MaximumModels + 5; i++)
            abilities = abilities.With(new() { Origin = "https://openrouter.ai/api/v1", ModelId = $"m{i}", Hears = true, Source = "list", CheckedAt = At });

        Assert.Equal(ModelAbilities.MaximumModels, abilities.Models.Count);
        Assert.Equal($"m{ModelAbilities.MaximumModels + 4}", abilities.Models[0].ModelId);
        Assert.Null(abilities.Find("https://openrouter.ai/api/v1", "m0"));
    }

    [Fact]
    public void The_shared_value_round_trips_to_the_same_text_on_any_computer()
    {
        var abilities = new ModelAbilities()
            .With(new() { Origin = "https://openrouter.ai/api/v1", ModelId = "google/gemini-2.5-flash", Hears = true, Sees = true, Source = "OpenRouter's model list", CheckedAt = At })
            .With(new() { Origin = "http://127.0.0.1:11434/v1", ModelId = "gemma4:e2b", Hears = true, Sees = true, Source = "Ollama on this PC", CheckedAt = At });
        var directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var shared = abilities.Share();
            var parsed = Assert.IsType<ModelAbilities>(ModelAbilities.Parse(shared));
            Assert.True(parsed.Save(directory));

            Assert.Equal(shared, ModelAbilities.Load(directory).Share());
            Assert.Equal(shared, new ModelAbilities { Models = [.. abilities.Models.Reverse()] }.Share());
            Assert.True(ModelAbilities.Load(directory).Find("http://127.0.0.1:11434/v1", "gemma4:e2b")!.Hears);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"SchemaVersion\":2,\"Models\":[]}")]
    [InlineData("{\"SchemaVersion\":1,\"Models\":[{\"Origin\":\"x\",\"ModelId\":\"m\",\"Source\":\"s\",\"CheckedAt\":\"2026-10-03T00:00:00+00:00\"}]}")]
    public void A_value_this_Martlet_cannot_read_is_refused(string json) => Assert.Null(ModelAbilities.Parse(json));

    [Fact]
    public void A_missing_or_broken_file_reads_as_nothing_known()
    {
        var directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            Assert.Empty(ModelAbilities.Load(directory).Models);
            File.WriteAllText(Path.Combine(directory, ModelAbilities.FileName), "{ broken");
            Assert.Empty(ModelAbilities.Load(directory).Models);
            Assert.Empty(ModelAbilities.Load(null).Models);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private const string Local = "http://127.0.0.1:11434/v1";

    [Fact]
    public void A_test_answer_outranks_later_metadata_and_each_fact_keeps_its_source()
    {
        var abilities = new ModelAbilities()
            .With(new() { Origin = Local, ModelId = "m", Tools = true, Hears = true, Source = ModelAbility.TestRequest, CheckedAt = At })
            .With(new() { Origin = Local, ModelId = "m", Hears = false, Sees = true, Video = false, Tools = false, Source = "Ollama on this PC",
                CheckedAt = At.AddDays(1) });

        var found = Assert.Single(abilities.Models);
        Assert.Equal((true, true, false, true), (found.Hears, found.Sees, found.Video, found.Tools));
        Assert.Equal(ModelAbility.TestRequest, found.SourceOf(ModelFact.Hears)!.Source);
        Assert.Equal(At, found.SourceOf(ModelFact.Tools)!.At);
        Assert.Equal("Ollama on this PC", found.SourceOf(ModelFact.Sees)!.Source);
        Assert.Equal(At.AddDays(1), found.SourceOf(ModelFact.Video)!.At);
        // Another test replaces a test's answer.
        var retested = abilities.With(new() { Origin = Local, ModelId = "m", Hears = false, Source = ModelAbility.RefusedRecording, CheckedAt = At.AddDays(2) });
        Assert.False(retested.Find(Local, "m")!.Hears);
        // Metadata that only repeats what a test found changes nothing.
        Assert.Same(abilities, abilities.With(new() { Origin = Local, ModelId = "m", Hears = false, Source = "Ollama on this PC", CheckedAt = At.AddDays(3) }));
    }

    [Fact]
    public void A_retired_model_keeps_the_first_date_and_an_answer_clears_it()
    {
        var abilities = new ModelAbilities()
            .With(new() { Origin = Local, ModelId = "old", Retired = At, Source = ModelAbility.GoneAnswer, CheckedAt = At })
            .With(new() { Origin = Local, ModelId = "old", Retired = At.AddDays(1), Source = ModelAbility.GoneAnswer, CheckedAt = At.AddDays(1) })
            .With(new() { Origin = Local, ModelId = "seen", Sees = true, Source = "list", CheckedAt = At })
            .With(new() { Origin = Local, ModelId = "seen", Retired = At, Source = ModelAbility.GoneAnswer, CheckedAt = At });

        Assert.Equal(At, abilities.Find(Local, "old")!.Retired);
        var answered = abilities.Answered(Local, "old").Answered(Local, "seen");
        // A route that then says nothing is dropped; one that still says something stays without the mark.
        Assert.Null(answered.Find(Local, "old"));
        Assert.Equal((true, (DateTimeOffset?)null), (answered.Find(Local, "seen")!.Sees, answered.Find(Local, "seen")!.Retired));
        Assert.Same(answered, answered.Answered(Local, "seen"));
    }

    [Fact]
    public void An_older_Martlet_still_reads_the_shared_routes_that_say_hearing_or_seeing()
    {
        var abilities = new ModelAbilities()
            .With(new() { Origin = Local, ModelId = "hears", Hears = true, Video = true, Source = "Ollama on this PC", CheckedAt = At })
            .With(new() { Origin = Local, ModelId = "tools", Tools = true, Source = "Ollama on this PC", CheckedAt = At })
            .With(new() { Origin = Local, ModelId = "gone", Retired = At, Source = ModelAbility.GoneAnswer, CheckedAt = At });
        var shared = abilities.Share();

        using (var document = System.Text.Json.JsonDocument.Parse(shared))
        {
            var models = document.RootElement.GetProperty("Models");
            Assert.Equal(1, models.GetArrayLength());
            Assert.True(models[0].GetProperty("Hears").GetBoolean());
            Assert.Equal(2, document.RootElement.GetProperty("MoreModels").GetArrayLength());
            Assert.Equal(1, document.RootElement.GetProperty("SchemaVersion").GetInt32());
        }
        var parsed = Assert.IsType<ModelAbilities>(ModelAbilities.Parse(shared));
        Assert.Equal(3, parsed.Models.Count);
        Assert.Null(parsed.MoreModels);
        Assert.Equal(At, parsed.Find(Local, "gone")!.Retired);
        Assert.True(parsed.Find(Local, "hears")!.Video);
        Assert.Equal(shared, parsed.Share());
    }

    [Fact]
    public void A_retired_model_found_on_a_route_comes_before_the_built_in_list()
    {
        const string nvidia = ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl;
        var found = new ModelAbilities().With(new() { Origin = nvidia, ModelId = "acme/gone", Retired = At, Source = ModelAbility.GoneAnswer, CheckedAt = At });

        var retired = ChatCompletionsEndpointCatalog.RetiredOn(nvidia, "acme/gone", found)!;
        Assert.Equal(("NVIDIA Build", ChatCompletionsEndpointCatalog.NvidiaBuildDefaultModelId, (DateTimeOffset?)At, ModelAbility.GoneAnswer),
            (retired.Server, retired.Suggestion, retired.Since, retired.Source));
        Assert.Contains(ChatCompletionsEndpointCatalog.NvidiaBuildDefaultModelId, retired.Remedy);
        // The built-in list stays as the fallback.
        Assert.Null(ChatCompletionsEndpointCatalog.RetiredOn(nvidia, "meta/llama-3.3-70b-instruct", found)!.Since);
        Assert.Null(ChatCompletionsEndpointCatalog.RetiredOn(nvidia, "acme/gone", found.Answered(nvidia, "acme/gone")));
        // Another server: its host names it, and there is no suggestion.
        var other = new ModelAbilities().With(new() { Origin = "https://llm.example/v1", ModelId = "m", Retired = At, Source = ModelAbility.GoneAnswer, CheckedAt = At });
        Assert.Equal(("llm.example", (string?)null), (ChatCompletionsEndpointCatalog.RetiredOn("https://llm.example/v1", "m", other)!.Server,
            ChatCompletionsEndpointCatalog.RetiredOn("https://llm.example/v1", "m", other)!.Suggestion));
    }
}
