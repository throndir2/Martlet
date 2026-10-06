using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Core.Tests;

public sealed class CharacterProfileTests
{
    [Fact]
    public void ProfilesAddUpdateSelectAndRemove()
    {
        var companion = CompanionSettings.Create();
        var first = companion.ActivePersonaId;
        companion = companion.Add("Aria");
        var aria = companion.ActivePersonaId;

        companion = companion.AddCharacter("Hiyori", first, CharacterProfile.BuiltInModel, "voice-1", out var hiyori)
            .AddCharacter("Aria", aria, "model-2", null, out var ariaProfile);
        Assert.Equal(2, companion.CharacterList.Count);
        Assert.Null(companion.ActiveCharacterId);
        Assert.Throws<ContractException>(() => companion.AddCharacter("aria", first, null, null, out _));
        Assert.Throws<ContractException>(() => companion.AddCharacter("Ghost", Guid.NewGuid(), null, null, out _));

        var selected = companion.SelectCharacter(hiyori.Id);
        Assert.Equal(first, selected.ActivePersonaId);
        Assert.Equal(hiyori.Id, selected.ActiveCharacterId);

        Assert.Same(companion, companion.UpdateCharacter(hiyori.Id, hiyori.Name, hiyori.PersonaId, hiyori.ModelId, hiyori.VoiceId));
        var renamed = selected.UpdateCharacter(hiyori.Id, "Hiyori 2", first, null, "voice-3");
        Assert.Equal("Hiyori 2", renamed.CharacterList[0].Name);
        Assert.Null(renamed.CharacterList[0].ModelId);

        var removed = renamed.RemoveCharacter(hiyori.Id);
        Assert.Single(removed.CharacterList);
        Assert.Null(removed.ActiveCharacterId);
        Assert.Null(removed.RemoveCharacter(ariaProfile.Id).Characters);
    }

    [Fact]
    public void RemovingAPersonaRemovesItsProfiles()
    {
        var companion = CompanionSettings.Create();
        var first = companion.ActivePersonaId;
        companion = companion.Add("Aria");
        var aria = companion.ActivePersonaId;
        companion = companion.AddCharacter("A", aria, null, null, out var a).AddCharacter("B", first, null, null, out var b).SelectCharacter(a.Id);

        var updated = companion.Remove(aria);
        Assert.Equal(b.Id, Assert.Single(updated.CharacterList).Id);
        Assert.Null(updated.ActiveCharacterId);
    }

    [Fact]
    public void CurrentCharacterMatchesWhatMartletUsesAndPrefersTheLastSwitched()
    {
        var companion = CompanionSettings.Create();
        var persona = companion.ActivePersonaId;
        companion = companion.AddCharacter("Keeps everything", persona, null, null, out var loose)
            .AddCharacter("Full", persona, "model-1", "voice-1", out var full);

        Assert.Equal(loose.Id, companion.CurrentCharacter("model-1", "voice-1")!.Id);
        Assert.Equal(full.Id, companion.SelectCharacter(full.Id).CurrentCharacter("model-1", "VOICE-1")!.Id);
        Assert.Equal(loose.Id, companion.SelectCharacter(full.Id).CurrentCharacter("model-2", "voice-1")!.Id);

        var strict = CompanionSettings.Create();
        strict = strict.AddCharacter("Only", strict.ActivePersonaId, CharacterProfile.BuiltInModel, "voice-1", out _);
        Assert.Null(strict.CurrentCharacter(null, "voice-1"));
        Assert.Null(strict.CurrentCharacter(CharacterProfile.BuiltInModel, null));
        Assert.Null(strict.Add("Other").CurrentCharacter(CharacterProfile.BuiltInModel, "voice-1"));
    }

    [Fact]
    public void ProfilesRoundTripThroughSettingsAndRejectBadValues()
    {
        var settings = CompanionSettings.Begin(null);
        var companion = settings.Companion!.AddCharacter("Hiyori", settings.Companion.ActivePersonaId, CharacterProfile.BuiltInModel, "voice-1", out var hiyori)
            .SelectCharacter(hiyori.Id);
        var json = ContractJson.Write(settings with { Companion = companion });
        Assert.Contains("\"characters\"", System.Text.Encoding.UTF8.GetString(json));
        var read = SettingsJson.Read(json).Companion!;
        Assert.Equal(hiyori, Assert.Single(read.CharacterList));
        Assert.Equal(hiyori.Id, read.ActiveCharacterId);

        var plain = ContractJson.Write(settings);
        Assert.DoesNotContain("characters", System.Text.Encoding.UTF8.GetString(plain));
        Assert.Null(SettingsJson.Read(plain).Companion!.Characters);

        Assert.Throws<ContractException>(() => companion.AddCharacter(" padded", hiyori.PersonaId, null, null, out _));
        Assert.Throws<ContractException>(() => companion.AddCharacter("Bad look", hiyori.PersonaId, "has space", null, out _));
        Assert.Throws<ContractException>(() => (companion with { ActiveCharacterId = Guid.NewGuid() }).Validate());
        Assert.Throws<ContractException>(() => (companion with { Characters = [] }).Validate());
    }
}
