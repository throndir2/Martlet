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
}
