using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Core.Tests;

public sealed class SenseModelsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "martlet-senses-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private static readonly DeepThinkingSettings Eyes = new()
    {
        Place = DeepThinkingPlace.Endpoint, Origin = GenerationSupport.LocalOllamaChatBaseUrl, ModelId = "qwen2.5vl:7b"
    };

    private static readonly DeepThinkingSettings Ears = new()
    {
        Place = DeepThinkingPlace.Endpoint, Origin = "https://generativelanguage.googleapis.com/v1beta/openai", ModelId = "gemini-2.5-flash-lite"
    };

    private static readonly DeepThinkingSettings Diva = new()
    {
        Place = DeepThinkingPlace.Host, ModelId = "qwen2.5vl:7b", HostId = "diva", HostOrigin = "https://diva.local:9443",
        HostSpkiFingerprint = "sha256:" + new string('0', 64), HostDeviceId = "device", HostCredentialId = Guid.NewGuid()
    };

    private static SenseModel Own(DeepThinkingSettings model) => new() { Source = SenseSource.Own, Own = model };
    private static readonly SenseModel SameAsOther = new() { Source = SenseSource.OtherSense };

    [Fact]
    public void By_default_the_text_model_takes_pictures_and_recordings()
    {
        var senses = new SenseModels();
        Assert.Null(senses.Place(SenseKind.Image));
        Assert.Null(senses.Place(SenseKind.Audio));
        Assert.True(senses.AllThinking);
        Assert.False(senses.OneModel);
        senses.Validate();
    }

    [Fact]
    public void The_same_model_as_the_other_kind_follows_it_and_both_ways_reads_as_the_text_model()
    {
        var follows = new SenseModels { Image = Own(Eyes), Audio = SameAsOther };
        Assert.Equal(Eyes.Key, follows.Place(SenseKind.Audio)!.Key);
        Assert.True(follows.OneModel);
        Assert.False(follows.AllThinking);

        var separate = new SenseModels { Image = Own(Eyes), Audio = Own(Ears) };
        Assert.False(separate.OneModel);
        Assert.Equal(Ears.Key, separate.Place(SenseKind.Audio)!.Key);

        var circle = new SenseModels { Image = SameAsOther, Audio = SameAsOther };
        Assert.Null(circle.Place(SenseKind.Image));
        Assert.Null(circle.Place(SenseKind.Audio));
        Assert.True(circle.AllThinking);
    }

    [Fact]
    public void A_paired_computer_can_be_the_image_model_but_never_the_audio_model()
    {
        new SenseModels { Image = Own(Diva) }.Validate();
        var refused = Assert.Throws<ContractException>(() => new SenseModels { Audio = Own(Diva) }.Validate());
        Assert.Contains("takes no recordings", refused.Message);
    }

    [Fact]
    public void Only_a_model_of_its_own_keeps_a_destination()
    {
        Assert.Throws<ContractException>(() => new SenseModels { Image = new() { Source = SenseSource.Thinking, Own = Eyes } }.Validate());
        Assert.Throws<ContractException>(() => new SenseModels { Image = new() { Source = SenseSource.Own } }.Validate());
        Assert.Throws<ContractException>(() =>
            new SenseModels { Image = new() { Source = SenseSource.Own, Own = new DeepThinkingSettings() } }.Validate());
    }

    [Fact]
    public void The_choices_are_saved_in_sense_models_json_and_a_broken_file_reads_as_the_text_model()
    {
        var chosen = new SenseModels { Image = Own(Eyes) with { ChosenAt = DateTimeOffset.UnixEpoch }, Audio = SameAsOther };
        Assert.True(chosen.Save(directory));
        var path = Path.Combine(directory, SenseModels.FileName);
        Assert.Contains("\"Source\": \"Own\"", File.ReadAllText(path));

        var (loaded, state) = SenseModels.Read(directory);
        Assert.Equal("loaded", state);
        Assert.Equal(SenseSource.Own, loaded.Image.Source);
        Assert.Equal(Eyes.Key, loaded.Place(SenseKind.Audio)!.Key);
        Assert.Equal(DateTimeOffset.UnixEpoch, loaded.Image.ChosenAt);

        File.WriteAllText(path, "{ \"SchemaVersion\": 2 }");
        Assert.Equal("unreadable", SenseModels.Read(directory).State);
        File.WriteAllText(path, "{ not json");
        var (broken, brokenState) = SenseModels.Read(directory);
        Assert.Equal("unreadable", brokenState);
        Assert.True(broken.AllThinking);
        Assert.Equal("none", SenseModels.Read(Path.Combine(directory, "missing")).State);
    }

    [Fact]
    public void An_audio_model_on_a_paired_computer_is_never_saved()
    {
        Assert.Throws<ContractException>(() => new SenseModels { Audio = Own(Diva) }.Save(directory));
        Assert.False(File.Exists(Path.Combine(directory, SenseModels.FileName)));
    }
}
