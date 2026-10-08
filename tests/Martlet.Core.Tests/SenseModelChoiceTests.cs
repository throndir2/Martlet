using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Core.Tests;

public sealed class SenseModelChoiceTests : IDisposable
{
    private const string OpenRouter = "https://openrouter.ai/api/v1";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "martlet-sense-choice-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private static DeepThinkingSettings Endpoint(string origin, string model, Guid? key = null) =>
        new() { Place = DeepThinkingPlace.Endpoint, Origin = origin, ModelId = model, CredentialId = key };

    private static SenseModel Own(DeepThinkingSettings model) => new() { Source = SenseSource.Own, Own = model };

    private static readonly DeepThinkingSettings Diva = new()
    {
        Place = DeepThinkingPlace.Host, ModelId = "qwen2.5vl:7b", HostId = "diva", HostOrigin = "https://diva.local:9443",
        HostSpkiFingerprint = "sha256:" + new string('0', 64), HostDeviceId = "device", HostCredentialId = Guid.NewGuid()
    };

    private static SetupRoute Thinking(string origin, Guid? key) => new()
    {
        Role = SetupRole.Llm, RouteType = SetupRouteType.ChatCompletions, ProviderAlias = ChatCompletionsSetup.Alias, Origin = origin,
        ModelId = "qwen3:8b", CredentialId = key, ConfigurationRevision = Guid.NewGuid()
    };

    [Fact]
    public void The_same_model_options_release_the_key_of_the_model_they_replace()
    {
        var key = Guid.NewGuid();
        var saved = new SenseModels { Image = Own(Endpoint(OpenRouter, "google/gemini-2.5-flash", key)) };

        var (text, released) = SenseModelChoice.Choose(saved, SenseKind.Image, new() { Source = SenseSource.Thinking });
        Assert.Null(text.Place(SenseKind.Image));
        Assert.Equal(key, Assert.Single(released).CredentialId);

        (var other, released) = SenseModelChoice.Choose(saved, SenseKind.Image, new() { Source = SenseSource.OtherSense });
        Assert.Equal(SenseSource.OtherSense, other.Image.Source);
        Assert.Null(other.Image.Own);
        Assert.Single(released);
    }

    [Fact]
    public void A_new_key_replaces_the_old_one_and_a_kept_key_stays()
    {
        var old = Guid.NewGuid();
        var saved = new SenseModels { Audio = Own(Endpoint(OpenRouter, "google/gemini-2.5-flash", old)) };

        Assert.Equal(old, SenseModelChoice.KeptKey(saved, SenseKind.Audio, OpenRouter));
        Assert.Null(SenseModelChoice.KeptKey(saved, SenseKind.Audio, "https://api.example.com/v1"));

        var (kept, released) = SenseModelChoice.Choose(saved, SenseKind.Audio, Own(Endpoint(OpenRouter, "google/gemini-2.5-pro", old)));
        Assert.Empty(released);
        Assert.Equal("google/gemini-2.5-pro", kept.Place(SenseKind.Audio)!.ModelId);

        var fresh = Guid.NewGuid();
        (_, released) = SenseModelChoice.Choose(saved, SenseKind.Audio, Own(Endpoint(OpenRouter, "google/gemini-2.5-pro", fresh)));
        Assert.Equal(old, Assert.Single(released).CredentialId);
    }

    [Fact]
    public void One_key_serves_both_kinds_and_stays_while_either_uses_it()
    {
        var key = Guid.NewGuid();
        var saved = new SenseModels { Image = Own(Endpoint(OpenRouter, "google/gemini-2.5-flash", key)) };
        // The audio model on the same base URL keeps the image model's key.
        Assert.Equal(key, SenseModelChoice.KeptKey(saved, SenseKind.Audio, OpenRouter));
        var (both, released) = SenseModelChoice.Choose(saved, SenseKind.Audio, Own(Endpoint(OpenRouter, "google/gemini-2.5-flash", key)));
        Assert.Empty(released);

        // The image model leaves OpenRouter: the audio model still uses the key, so it stays.
        (var after, released) = SenseModelChoice.Choose(both, SenseKind.Image, new() { Source = SenseSource.Thinking });
        Assert.Empty(released);
        Assert.Equal(key, after.Place(SenseKind.Audio)!.CredentialId);

        // Then the audio model leaves too: now nothing uses it.
        (_, released) = SenseModelChoice.Choose(after, SenseKind.Audio, new() { Source = SenseSource.Thinking });
        Assert.Equal(key, Assert.Single(released).CredentialId);
    }

    [Fact]
    public void A_key_is_needed_only_when_none_is_kept_and_Thinking_has_none_for_that_base_URL()
    {
        var own = Endpoint(OpenRouter, "google/gemini-2.5-flash");
        Assert.True(SenseModelChoice.NeedsKey(own, providerNeedsKey: true, thinking: null));
        Assert.False(SenseModelChoice.NeedsKey(own, providerNeedsKey: false, thinking: null));
        Assert.False(SenseModelChoice.NeedsKey(own with { CredentialId = Guid.NewGuid() }, providerNeedsKey: true, thinking: null));
        // Thinking's key for the same base URL serves it.
        Assert.False(SenseModelChoice.NeedsKey(own, providerNeedsKey: true, Thinking(OpenRouter, Guid.NewGuid())));
        Assert.True(SenseModelChoice.NeedsKey(own, providerNeedsKey: true, Thinking("https://api.example.com/v1", Guid.NewGuid())));
        Assert.True(SenseModelChoice.NeedsKey(own, providerNeedsKey: true, Thinking(OpenRouter, null)));
    }

    [Fact]
    public void A_paired_computer_takes_pictures_but_never_recordings()
    {
        var (pictures, released) = SenseModelChoice.Choose(new(), SenseKind.Image, Own(Diva));
        Assert.Equal("host:diva", pictures.Place(SenseKind.Image)!.Key);
        Assert.Empty(released);
        Assert.Throws<ContractException>(() => SenseModelChoice.Choose(new(), SenseKind.Audio, Own(Diva)));
    }

    [Fact]
    public void Saved_choices_load_again()
    {
        var key = Guid.NewGuid();
        var (choices, _) = SenseModelChoice.Choose(new(), SenseKind.Image,
            Own(Endpoint(GenerationSupport.LocalOllamaChatBaseUrl, "qwen2.5vl:7b")) with { ChosenAt = DateTimeOffset.UtcNow });
        (choices, _) = SenseModelChoice.Choose(choices, SenseKind.Audio, Own(Endpoint(OpenRouter, "google/gemini-2.5-flash", key)));
        Assert.True(choices.Save(directory));

        var (loaded, state) = SenseModels.Read(directory);
        Assert.Equal("loaded", state);
        Assert.Equal("qwen2.5vl:7b", loaded.Place(SenseKind.Image)!.ModelId);
        Assert.Equal(key, loaded.Place(SenseKind.Audio)!.CredentialId);
        Assert.NotNull(loaded.Image.ChosenAt);
        // The file keeps the key's reference only.
        Assert.DoesNotContain("sk-", File.ReadAllText(Path.Combine(directory, SenseModels.FileName)), StringComparison.Ordinal);
    }
}
