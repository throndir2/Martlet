using Martlet.Core.Cluster;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Conversation.Tests;

// Reading the Vision and Hearing lists (SenseSetup): made once from sense-models.json, read back without writing, a damaged list
// file read as the text model, and the first model that may take each kind chosen.
public sealed class SenseSetupTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "martlet-sense-setup-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private static DeepThinkingSettings Endpoint(string model, string origin = GenerationSupport.LocalOllamaChatBaseUrl) =>
        new() { Place = DeepThinkingPlace.Endpoint, Origin = origin, ModelId = model };

    [Fact]
    public void The_lists_are_made_once_from_sense_models_json_and_then_win()
    {
        Assert.True(new SenseModels { Image = new() { Source = SenseSource.Own, Own = Endpoint("qwen2.5vl:7b") } }.Save(directory));

        // MCP reads without writing: no list yet, so sense-models.json.
        var read = SenseSetup.Load(directory, "pc", null, migrate: false);
        Assert.Equal(("sense-models", "qwen2.5vl:7b"), (read.State, read.Senses.Place(SenseKind.Image)?.ModelId));
        Assert.False(PoolSettings.Saved(directory, shared: false));

        var made = SenseSetup.Load(directory, "pc", null, migrate: true);
        Assert.Equal("lists", made.State);
        Assert.Equal(["this-pc"], made.ImageList!.Members.Select(m => m.Key));
        Assert.Empty(made.AudioList!.Members);
        Assert.Equal(Endpoint("qwen2.5vl:7b").Key, made.Senses.Place(SenseKind.Image)?.Key);
        Assert.Null(made.Senses.Place(SenseKind.Audio));

        // A later change to sense-models.json changes nothing: the lists win.
        Assert.True(new SenseModels().Save(directory));
        Assert.Equal(Endpoint("qwen2.5vl:7b").Key, SenseSetup.Load(directory, "pc", null, migrate: true).Senses.Place(SenseKind.Image)?.Key);
        Assert.Equal(("lists", true), (SenseSetup.Read(directory).State, SenseSetup.Read(directory).Senses.Place(SenseKind.Image) is not null));
    }

    [Fact]
    public void A_damaged_list_file_reads_as_the_text_model_and_is_never_replaced_by_a_new_list()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, PoolSettings.LocalFileName);
        File.WriteAllText(path, "{ not json");
        Assert.True(new SenseModels { Image = new() { Source = SenseSource.Own, Own = Endpoint("qwen2.5vl:7b") } }.Save(directory));

        var setup = SenseSetup.Load(directory, "pc", null, migrate: true);
        Assert.Equal(("unreadable", true), (setup.State, setup.Senses.AllThinking));
        Assert.Equal("{ not json", File.ReadAllText(path));
    }

    [Fact]
    public void The_first_model_that_may_take_each_kind_is_chosen()
    {
        var textOnly = Endpoint("qwen3:8b");
        var eyes = Endpoint("qwen2.5vl:7b");
        var senses = SensePool.Senses([textOnly, eyes], [textOnly], null);
        Assert.Equal(eyes.Key, senses.Place(SenseKind.Image)?.Key);
        // Only models that don't hear: the first, so its route says why.
        Assert.Equal(textOnly.Key, senses.Place(SenseKind.Audio)?.Key);
        Assert.Equal(SensePath.None, SenseRouting.For(SenseKind.Audio, senses, null, null).Path);
        Assert.True(SensePool.Senses([], [], null).AllThinking);
    }
}
