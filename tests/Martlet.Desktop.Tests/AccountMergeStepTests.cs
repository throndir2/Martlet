using Martlet.Core.Settings;
using Martlet.Core.Speakers;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

public sealed class AccountMergeStepTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 20, 0, 0, TimeSpan.Zero);
    private readonly string data = Path.Combine(Path.GetTempPath(), "martlet-account-merge-steps-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(data)) Directory.Delete(data, recursive: true);
    }

    [Fact]
    public void The_merged_accounts_characters_join_with_their_ids_and_a_clashing_name_gets_a_number()
    {
        var mine = CompanionSettings.Create();
        var nova = new PersonaProfile { Id = Guid.NewGuid(), ConfigurationRevision = Guid.NewGuid(), Name = mine.Personas[0].Name, Text = "Nova's personality" };
        var character = new CharacterProfile { Id = Guid.NewGuid(), PersonaId = nova.Id, Name = "Nova" };
        var theirs = CompanionSettings.Create() with { Personas = [.. CompanionSettings.Create().Personas, nova], Characters = [character] };

        var merged = AccountCharactersMergeStep.AddCharacters(mine, theirs);
        // The other account's untouched default personality doesn't come along; Nova's does, numbered.
        Assert.Equal(2, merged.Personas.Count);
        Assert.Contains(merged.Personas, p => p.Id == nova.Id && p.Name == mine.Personas[0].Name + " 2" && p.Text == "Nova's personality");
        Assert.Contains(merged.CharacterList, c => c.Id == character.Id && c.Name == "Nova" && c.PersonaId == nova.Id);
        // Running it again adds nothing: the same IDs are there.
        Assert.Equal(merged.Personas.Count, AccountCharactersMergeStep.AddCharacters(merged, theirs).Personas.Count);
        Assert.Single(AccountCharactersMergeStep.AddCharacters(merged, theirs).CharacterList);
    }

    [Fact]
    public async Task Voices_linked_to_the_merged_account_link_to_the_kept_one()
    {
        Directory.CreateDirectory(data);
        using var voices = new LocalVoices(data, "desk-test");
        var from = Guid.NewGuid();
        var into = Guid.NewGuid();
        var roster = VoiceRoster.Empty;
        (roster, var alex) = roster.Add(Print(1), 3, "desk", Now);
        (roster, var other) = roster.Add(Print(2), 3, "desk", Now);
        roster = roster.SetAccount(alex!.Id, from, owner: false, "desk", Now);
        voices.Merge(roster);

        await new AccountVoiceMergeStep(voices).MergeAsync(into, from, CancellationToken.None);
        Assert.Empty(voices.Roster.LinkedTo(from));
        Assert.Equal([alex.Id], voices.Roster.LinkedTo(into).Select(v => v.Id));
        Assert.Null(voices.Roster.Resolve(other!.Id)!.Account);
    }

    private static float[] Print(int seed)
    {
        var random = new Random(seed);
        return VoicePrints.Normalize(Enumerable.Range(0, VoicePrints.Dimension).Select(_ => (float)(random.NextDouble() - 0.5)).ToArray());
    }
}
