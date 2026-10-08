using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Core.Settings;

namespace Martlet.Desktop.Tests;

public sealed class CharacterTouchTemperamentTests
{
    private static readonly Guid Persona = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly CharacterTouchZone HeadPat = new() { Id = "top_of_head", Box = new(0, 0, 1, 1) };

    private static CharacterActionCatalog Catalog(params CharacterActionSource[] extra)
    {
        var gestures = CharacterActionInventory.AllGestures
            .Where(g => g.Name is "tilt" or "nod" or "blush" or "smile" or "anger" or "flinch" or "look_away" or "pout" or "lean_in" or "hearts")
            .Select(g => new CharacterActionSource(g.Id, CharacterActionKind.Gesture, g.Name, g.Does));
        var inventory = new CharacterActionInventory("model-1", AvatarRenderer.Live2D, [.. extra, .. gestures]);
        return new(inventory, CharacterActions.Merge(inventory, null));
    }

    private static CharacterTouchTemperament Temperament(string answer) =>
        CharacterTouchTemperaments.Parse(answer, Persona, "digest", CharacterTouchTemperament.ByThinking, DateTimeOffset.Now)!;

    [Fact]
    public void VocabularyIsMartletsOwnGestures()
    {
        foreach (var word in CharacterTouchTemperaments.Vocabulary)
            Assert.Contains(CharacterActionInventory.AllGestures, g => g.Name == word);
        foreach (var attitude in Enumerable.Range(CharacterTouchTemperaments.MinimumAttitude, 6))
            Assert.All(CharacterTouchTemperaments.DefaultReactions(attitude), r => Assert.Contains(r, CharacterTouchTemperaments.Vocabulary));
    }

    [Fact]
    public void ParsesTheAnswerDropsUnknownActionsAndClamps()
    {
        var parsed = Temperament("Sure!\n```json\n{\"groups\":{\"head\":{\"attitude\":7,\"reactions\":[\"Hearts\",\"dance\",\"lean in\",\"blush\",\"smile\"],\"linger\":99}," +
            "\"Lower Body\":{\"attitude\":\"hates\",\"reactions\":[\"teleport\"]},\"tentacles\":{\"attitude\":1}},\"zones\":{\"belly\":{\"attitude\":-5}," +
            "\"elbow_spikes\":{\"attitude\":2},\"hair\":{\"reactions\":[\"smile\"]}},\"escalation\":{\"after\":99,\"disliked\":[\"mad\",\"nope\"],\"loved\":[]}}\n```");

        Assert.Equal(["head", "lower_body"], parsed.Groups.Keys.Order());
        var head = parsed.Groups["head"];
        Assert.Equal(CharacterTouchTemperaments.MaximumAttitude, head.Attitude);
        Assert.Equal(["hearts", "lean_in", "blush"], head.Reactions);
        Assert.Equal(CharacterTouchTemperaments.MaximumLinger, head.LingerSeconds);
        // Every action unknown: the attitude's own reactions.
        Assert.Equal(-2, parsed.Groups["lower_body"].Attitude);
        Assert.Null(parsed.Groups["lower_body"].Reactions);
        // Known zone names only (belly is the stomach), clamped; a zone without an attitude is left out.
        Assert.Equal(["stomach"], parsed.Zones.Keys);
        Assert.Equal(-2, parsed.Zones["stomach"].Attitude);
        Assert.Equal(CharacterTouchTemperaments.MaximumAfter, parsed.Escalation.After);
        Assert.Equal(["anger"], parsed.Escalation.Disliked);
        Assert.Equal(new TouchEscalation().Loved, parsed.Escalation.Loved);
        Assert.Null(CharacterTouchTemperaments.Problem(parsed));

        Assert.Null(CharacterTouchTemperaments.Parse("I can't decide that.", Persona, null, CharacterTouchTemperament.ByThinking, DateTimeOffset.Now));
        Assert.Null(CharacterTouchTemperaments.Parse("{\"groups\":{\"wheels\":{\"attitude\":1}}}", Persona, null, CharacterTouchTemperament.ByThinking, DateTimeOffset.Now));
    }

    [Fact]
    public void ResolvesAbstractReactionsToTheModelsOwnEmotesFirst()
    {
        var angry = new CharacterActionSource("expression:Angry", CharacterActionKind.Expression, "Angry", "brows");
        var love = new CharacterActionSource("motion:Love", CharacterActionKind.Motion, "Love", "hugs itself");
        var catalog = Catalog(angry, love);

        Assert.Equal(["Angry", "Love", "blush"], CharacterTouchTemperaments.Resolve(["anger", "hearts", "blush"], catalog).Select(s => s.Name));
        // Without the model's own: Martlet's gestures and overlays; a word the model has nothing for is left out.
        Assert.Equal(["anger", "flinch"], CharacterTouchTemperaments.Resolve(["anger", "sparkles", "flinch"], Catalog()).Select(s => s.Name));
        Assert.Empty(CharacterTouchTemperaments.Resolve(["anger"], null));
    }

    [Fact]
    public void RepeatedTouchesEscalateDislikedAndLovedZonesOnly()
    {
        var temperament = Temperament("{\"groups\":{\"head\":{\"attitude\":2,\"reactions\":[\"smile\"]},\"torso\":{\"attitude\":-1}," +
            "\"arms\":{\"attitude\":0}},\"escalation\":{\"after\":3,\"disliked\":[\"anger\"],\"loved\":[\"hearts\"]}}");
        var loved = temperament.Groups["head"];
        Assert.Equal(("smile", false), Of(CharacterTouchTemperaments.Words(temperament, loved, 2)));
        Assert.Equal(("hearts smile", true), Of(CharacterTouchTemperaments.Words(temperament, loved, 3)));
        Assert.Equal(("anger pout sweat", true), Of(CharacterTouchTemperaments.Words(temperament, temperament.Groups["torso"], 5)));
        Assert.Equal(("tilt", false), Of(CharacterTouchTemperaments.Words(temperament, temperament.Groups["arms"], 9)));
    }

    private static (string, bool) Of((IReadOnlyList<string> Words, bool Escalated) result) => (string.Join(" ", result.Words), result.Escalated);

    [Fact]
    public void OwnerPickThenZoneThenGroupThenBuiltIn()
    {
        var catalog = Catalog();
        var loves = Temperament("{\"groups\":{\"head\":{\"attitude\":2,\"linger\":3}},\"zones\":{\"cheek_left\":{\"attitude\":-2}}}");
        var hates = Temperament("{\"groups\":{\"head\":{\"attitude\":-2}}}");

        var pat = CharacterTouchZones.React(HeadPat, catalog, loves, 1);
        Assert.Equal(["hearts", "blush", "lean_in"], pat.Actions.Select(s => s.Name));
        Assert.Equal((TouchReactionPlan.FromTemperament, "loves", 3.0), (pat.From, pat.Attitude, pat.LingerSeconds));
        Assert.Equal(["anger", "flinch", "look_away"], CharacterTouchZones.React(HeadPat, catalog, hates, 1).Actions.Select(s => s.Name));
        // The zone's own entry wins over its group.
        var cheek = new CharacterTouchZone { Id = "cheek_left", Box = new(0, 0, 1, 1) };
        Assert.Equal("hates", CharacterTouchZones.React(cheek, catalog, loves, 1).Attitude);
        // A group the temperament leaves out keeps the built-in reaction, as with no temperament at all.
        var hand = new CharacterTouchZone { Id = "hand_left", Box = new(0, 0, 1, 1) };
        Assert.Equal(TouchReactionPlan.FromDefault, CharacterTouchZones.React(hand, catalog, loves, 1).From);
        Assert.Equal(["lean_in", "smile"], CharacterTouchZones.React(HeadPat, catalog, null, 1).Actions.Select(s => s.Name));
        Assert.Equal(["lean_in", "smile"], CharacterTouchZones.Plan(HeadPat, catalog).Select(s => s.Name));
        // The owner's pick for the zone wins over the temperament.
        var picked = CharacterTouchZones.React(HeadPat with { Reaction = new() { Actions = ["gesture:nod"] } }, catalog, hates, 4);
        Assert.Equal((TouchReactionPlan.FromOwner, "hates"), (picked.From, picked.Attitude));
        Assert.Equal(["nod"], picked.Actions.Select(s => s.Name));
        Assert.Equal("loves", CharacterTouchTemperaments.Attitude(loves, "hair"));
        Assert.Null(CharacterTouchTemperaments.Attitude(loves, "foot_left"));
    }

    [Fact]
    public async Task SavesSharesAndKeepsTheOldFileReadable()
    {
        var directory = Directory.CreateTempSubdirectory("martlet-temperament-").FullName;
        try
        {
            Assert.Empty(CharacterTouchTemperaments.LoadAll(directory));
            var decided = Temperament("{\"groups\":{\"head\":{\"attitude\":3,\"reactions\":[\"hearts\",\"shy\"]}}}");
            await CharacterTouchTemperaments.SaveAsync(directory, decided, DateTimeOffset.Now);
            var other = Guid.NewGuid();
            await CharacterTouchTemperaments.SaveAsync(directory, decided with { PersonaId = other, Source = CharacterTouchTemperament.ByOwner }, DateTimeOffset.Now);

            var loaded = CharacterTouchTemperaments.Load(directory, Persona)!;
            Assert.Equal(["hearts", "shy"], loaded.Groups["head"].Reactions);
            Assert.Equal("digest", loaded.PersonalityDigest);
            Assert.Equal(2, CharacterTouchTemperaments.LoadAll(directory).Count);

            var shared = CharacterTouchTemperaments.Share(directory);
            var elsewhere = Directory.CreateTempSubdirectory("martlet-temperament-").FullName;
            try
            {
                await CharacterTouchTemperaments.ReplaceAllAsync(elsewhere, shared);
                Assert.Equal(CharacterTouchTemperament.ByOwner, CharacterTouchTemperaments.Load(elsewhere, other)!.Source);
                await Assert.ThrowsAsync<Martlet.Core.Contracts.ContractException>(() =>
                    CharacterTouchTemperaments.ReplaceAllAsync(elsewhere, "{\"version\":2,\"personas\":[]}"));
            }
            finally { Directory.Delete(elsewhere, true); }

            await CharacterTouchTemperaments.RemoveAsync(directory, other);
            Assert.Null(CharacterTouchTemperaments.Load(directory, other));
            await Assert.ThrowsAsync<Martlet.Core.Contracts.ContractException>(() => CharacterTouchTemperaments.SaveAsync(directory,
                decided with { Groups = new Dictionary<string, TouchTemperamentEntry> { ["head"] = new() { Reactions = ["dance"] } } }, DateTimeOffset.Now));

            File.WriteAllText(CharacterTouchTemperaments.Path(directory), "{ not json");
            Assert.Empty(CharacterTouchTemperaments.LoadAll(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void OnlyAMeaningfulChangeChangesTheDigest()
    {
        Assert.Equal(CharacterTouchTemperaments.Digest("Shy, gentle  girl.\nLoves head pats!"), CharacterTouchTemperaments.Digest("shy gentle girl loves head pats"));
        Assert.NotEqual(CharacterTouchTemperaments.Digest("Loves head pats"), CharacterTouchTemperaments.Digest("Hates head pats"));
    }

    [Fact]
    public async Task DecidingSavesTheAnswerAndAFailureKeepsThePreviousTemperament()
    {
        var directory = Directory.CreateTempSubdirectory("martlet-temperament-").FullName;
        try
        {
            var service = new CharacterTemperamentService(directory);
            var persona = new PersonaProfile
            {
                Id = Persona, ConfigurationRevision = Guid.NewGuid(), Name = "Mira", Text = "Mira adores head pats."
            };
            string? asked = null;
            await service.DecideAsync(persona, (_, instructions, text, _) =>
            {
                asked = text;
                return Task.FromResult<(string?, string?)>(("{\"groups\":{\"head\":{\"attitude\":3}}}", null));
            }, CancellationToken.None);
            Assert.Contains("Mira adores head pats.", asked);
            var decided = service.For(Persona)!;
            Assert.Equal(CharacterTouchTemperament.ByThinking, decided.Source);
            Assert.Equal(CharacterTouchTemperaments.Digest(persona.Text), decided.PersonalityDigest);

            var status = await service.DecideAsync(persona, (_, _, _, _) => Task.FromResult<(string?, string?)>((null, "runtime.failed")), CancellationToken.None);
            Assert.Contains("previous", status);
            Assert.Equal(3, CharacterTouchTemperaments.Load(directory, Persona)!.Groups["head"].Attitude);

            // The owner's own choices stay when the personality changes; only Re-decide replaces them.
            await service.SaveAsync(decided with { Source = CharacterTouchTemperament.ByOwner }, CancellationToken.None);
            var calls = 0;
            service.PersonalitySaved(persona with { Text = "Mira hates being touched." }, () => false,
                (_, _, _, _) => { calls++; return Task.FromResult<(string?, string?)>((null, null)); }, CancellationToken.None);
            await Task.Delay(CharacterTemperamentService.Settle + TimeSpan.FromSeconds(1));
            Assert.Equal(0, calls);
            Assert.Contains("Your own touch choices stay", service.Status);
        }
        finally { Directory.Delete(directory, true); }
    }
}
