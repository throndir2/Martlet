using System.Text.Json;
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
                    CharacterTouchTemperaments.ReplaceAllAsync(elsewhere, "{\"version\":3,\"personas\":[]}"));
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
            var done = await service.DecideAsync(persona, (_, instructions, text, _) =>
            {
                asked = text;
                return Task.FromResult<(string?, string?)>(("{\"groups\":{\"head\":{\"attitude\":3}}}", null));
            }, CancellationToken.None);
            Assert.Contains("Mira adores head pats.", asked);
            // Only that it worked: the card's table shows what was decided.
            Assert.StartsWith("Decided how Mira reacts to touch at ", done);
            Assert.DoesNotContain("head craves", done);
            var decided = service.For(Persona)!;
            Assert.Equal(CharacterTouchTemperament.ByThinking, decided.Source);
            Assert.Equal(CharacterTouchTemperaments.Digest(persona.Text), decided.PersonalityDigest);

            var status = await service.DecideAsync(persona, (_, _, _, _) => Task.FromResult<(string?, string?)>((null, "runtime.failed")), CancellationToken.None);
            Assert.Contains("previous", status);
            Assert.Equal(3, CharacterTouchTemperaments.Load(directory, Persona)!.Groups["head"].Attitude);

            // The owner's own choices stay when the personality changes; only Re-decide replaces them.
            await service.SaveAsync(decided with { Source = CharacterTouchTemperament.ByOwner }, CancellationToken.None);
            Assert.Null(service.Status);
            var calls = 0;
            service.PersonalitySaved(persona with { Text = "Mira hates being touched." }, () => false,
                (_, _, _, _) => { calls++; return Task.FromResult<(string?, string?)>((null, null)); }, CancellationToken.None);
            await Task.Delay(CharacterTemperamentService.Settle + TimeSpan.FromSeconds(1));
            Assert.Equal(0, calls);
            Assert.Contains("Your own touch choices stay", service.Status);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void EveryZoneKindIsInExactlyOneCategoryAndIntimatePartsHoldTheBreastsAndGroin()
    {
        Assert.Equal(["head", "torso", "arms", "lower_body", "extras", CharacterTouchTemperaments.IntimateId], CharacterTouchTemperaments.GroupIds.Select(g => g.Id));
        Assert.Equal("Intimate parts", CharacterTouchTemperaments.GroupIds[^1].Label);
        foreach (var kind in CharacterTouchZones.Kinds)
            Assert.Single(CharacterTouchTemperaments.GroupIds, g => CharacterTouchTemperaments.KindsIn(g.Id).Contains(kind));
        var intimate = CharacterTouchTemperaments.KindsIn(CharacterTouchTemperaments.IntimateId).Select(k => k.Id).ToArray();
        Assert.Equal(CharacterTouchZones.Kinds.Where(k => k.Intimate).Select(k => k.Id), intimate);
        Assert.Contains("breast_left", intimate);
        Assert.Contains("breast_right", intimate);
        Assert.Contains("groin", intimate);
        Assert.All(CharacterTouchTemperaments.GroupIds.Where(g => g.Group is not null),
            g => Assert.DoesNotContain(CharacterTouchTemperaments.KindsIn(g.Id), k => k.Intimate));

        // The Thinking model is asked for all six groups, each zone under its category, and for actions only.
        Assert.Contains("all six groups", CharacterTouchTemperaments.DecisionInstructions);
        Assert.Contains("\"intimate\":{...}", CharacterTouchTemperaments.DecisionInstructions);
        Assert.Contains("ACTIONS ONLY", CharacterTouchTemperaments.DecisionInstructions);
        var lines = CharacterTouchTemperaments.DecisionRequest("Shy.").Split('\n');
        var intimateLine = Assert.Single(lines, l => l.StartsWith("intimate: ", StringComparison.Ordinal));
        Assert.Contains("breast_left (left breast)", intimateLine);
        Assert.Contains("groin (groin)", intimateLine);
        Assert.DoesNotContain("breast_left", Assert.Single(lines, l => l.StartsWith("torso: ", StringComparison.Ordinal)));
        Assert.DoesNotContain("groin", Assert.Single(lines, l => l.StartsWith("lower_body: ", StringComparison.Ordinal)));
    }

    [Fact]
    public void TheIntimateCategoryCoversIntimatePartsAndAnOlderTemperamentKeepsTheirBodyGroups()
    {
        var decided = Temperament("{\"groups\":{\"torso\":{\"attitude\":1},\"lower_body\":{\"attitude\":0},\"intimate\":{\"attitude\":-2," +
            "\"reactions\":[\"anger\"],\"look\":2}},\"zones\":{\"breast_right\":{\"attitude\":3}}}");
        Assert.Equal("hates", CharacterTouchTemperaments.Attitude(decided, "breast_left"));
        Assert.Equal("likes", CharacterTouchTemperaments.Attitude(decided, "stomach"));
        // The part's own line still wins.
        Assert.Equal("craves", CharacterTouchTemperaments.Attitude(decided, "breast_right"));
        var groin = CharacterTouchZones.React(new CharacterTouchZone { Id = "groin", Box = new(0, 0, 1, 1) }, Catalog(), decided, 1);
        Assert.Equal((TouchReactionPlan.FromTemperament, "hates", 2.0), (groin.From, groin.Attitude, groin.LookSeconds));
        Assert.Equal(["anger"], groin.Actions.Select(s => s.Name));
        Assert.Contains("intimate hates", CharacterTouchTemperaments.Summary(decided));

        // Saved before the intimate category: its intimate parts react as their body group, as they did then.
        var older = Temperament("{\"groups\":{\"torso\":{\"attitude\":1},\"lower_body\":{\"attitude\":-1}}}");
        Assert.Equal("likes", CharacterTouchTemperaments.Attitude(older, "breast_left"));
        Assert.Equal("dislikes", CharacterTouchTemperaments.Attitude(older, "groin"));
        Assert.Contains("intimate as body groups", CharacterTouchTemperaments.Summary(older));
    }

    [Fact]
    public void AZoneSpecialToTheCharacterFeelsAsItsExtrasDo()
    {
        // A zone of its own (a hair bow the vision model found) is covered by the extras category.
        var decided = Temperament("{\"groups\":{\"extras\":{\"attitude\":2,\"reactions\":[\"hearts\"]}}}");
        Assert.Equal("loves", CharacterTouchTemperaments.Attitude(decided, "hair_bow"));
        var plan = CharacterTouchZones.React(new CharacterTouchZone { Id = "hair_bow", Label = "Hair bow", Box = new(0, 0, 1, 1) }, Catalog(), decided, 1);
        Assert.Equal((TouchReactionPlan.FromTemperament, "loves"), (plan.From, plan.Attitude));
        Assert.Equal(["hearts"], plan.Actions.Select(s => s.Name));
        Assert.Null(CharacterTouchTemperaments.Attitude(Temperament("{\"groups\":{\"head\":{\"attitude\":2}}}"), "hair_bow"));
    }

    [Fact]
    public void TheTouchLineSaysHowThePersonaFeelsAboutWhereItWasTouched()
    {
        CharacterTouchZone Zone(string id) => new() { Id = id, Box = new(0, 0, 1, 1) };
        var decided = Temperament("{\"groups\":{\"head\":{\"attitude\":0},\"torso\":{\"attitude\":2},\"intimate\":{\"attitude\":-2}}}");
        Assert.Equal("you love being touched there", CharacterTouchTemperaments.Feeling(decided, [Zone("stomach")]));
        Assert.Equal("you hate being touched there", CharacterTouchTemperaments.Feeling(decided, [Zone("groin")]));
        // Neutral, or not decided: nothing to say.
        Assert.Null(CharacterTouchTemperaments.Feeling(decided, [Zone("nose")]));
        Assert.Null(CharacterTouchTemperaments.Feeling(null, [Zone("stomach")]));
        // A stroke across zones it feels differently about says each, in the order they were crossed.
        Assert.Equal("you love it on your stomach and your navel, and hate it on your groin",
            CharacterTouchTemperaments.Feeling(decided, [Zone("stomach"), Zone("navel"), Zone("nose"), Zone("groin")]));
        Assert.Equal("you love being touched there", CharacterTouchTemperaments.Feeling(decided, [Zone("stomach"), Zone("navel")]));
    }

    [Theory]
    [InlineData("intimate")]
    [InlineData("intimate_parts")]
    [InlineData("Intimate parts")]
    [InlineData("erogenous")]
    [InlineData("erogenous_zones")]
    [InlineData("private")]
    [InlineData("sensitive")]
    public void ReadsTheIntimateCategoryByItsCommonNames(string name)
    {
        var parsed = Temperament($"{{\"groups\":{{\"{name}\":{{\"attitude\":2}},\"Shoulders and torso\":{{\"attitude\":0}}}}}}");
        Assert.Equal(2, parsed.Groups[CharacterTouchTemperaments.IntimateId].Attitude);
        Assert.Equal(0, parsed.Groups["torso"].Attitude);
    }

    [Fact]
    public async Task CustomTemperamentsAreMadeChosenSharedRenamedAndDeleted()
    {
        var directory = Directory.CreateTempSubdirectory("martlet-temperament-").FullName;
        try
        {
            var service = new CharacterTemperamentService(directory);
            var other = Guid.NewGuid();
            Assert.Null(await service.SaveAsync(Temperament("{\"groups\":{\"head\":{\"attitude\":2}}}"), CancellationToken.None));
            Assert.Null(await service.CreateCustomAsync(Persona, " Shy cat ", CancellationToken.None));
            var custom = Assert.Single(service.Saved.Custom);
            Assert.Equal("Shy cat", custom.Name);
            // It starts as a copy of what the persona used, and the persona now uses it.
            var used = service.For(Persona)!;
            Assert.Equal((CharacterTouchTemperament.ByCustom, "Shy cat", 2), (used.Source, used.Custom?.Name, used.Groups["head"].Attitude));
            Assert.Equal(CharacterTouchTemperament.ByThinking, service.Own(Persona)!.Source);
            // Names are short, one of a kind (ignoring case) and not one of the other choices.
            Assert.NotNull(await service.CreateCustomAsync(Persona, "shy CAT", CancellationToken.None));
            Assert.NotNull(await service.CreateCustomAsync(Persona, CharacterTouchTemperaments.BuiltInLabel, CancellationToken.None));
            Assert.NotNull(await service.CreateCustomAsync(Persona, new string('x', CharacterTouchTemperaments.MaximumNameLength + 1), CancellationToken.None));
            Assert.NotNull(await service.CreateCustomAsync(Persona, "  ", CancellationToken.None));
            Assert.Single(service.Saved.Custom);

            // Another persona uses it too, and changing it changes it for both.
            Assert.Null(await service.ChooseAsync(other, custom.Id.ToString(), CancellationToken.None));
            Assert.Null(await service.SaveCustomAsync(service.Saved.Custom[0] with
            {
                Groups = new Dictionary<string, TouchTemperamentEntry> { [CharacterTouchTemperaments.IntimateId] = new() { Attitude = -2 } }
            }, CancellationToken.None));
            Assert.Equal("hates", CharacterTouchTemperaments.Attitude(service.For(Persona), "groin"));
            Assert.Equal("hates", CharacterTouchTemperaments.Attitude(service.For(other), "breast_left"));
            Assert.Equal(new[] { Persona, other }.Order(), service.Saved.UsedBy(custom.Id));

            // The built-in reactions keep the persona's own temperament for later.
            Assert.Null(await service.ChooseAsync(Persona, CharacterTouchTemperaments.BuiltIn, CancellationToken.None));
            Assert.Null(service.For(Persona));
            Assert.NotNull(service.Own(Persona));

            Assert.Null(await service.RenameCustomAsync(custom.Id, "Tsundere", CancellationToken.None));
            Assert.Equal("Tsundere", service.For(other)!.Custom!.Name);

            // The owner's other computers get them all, with who uses which, in a version 2 file.
            Assert.Contains("\"version\": 2", File.ReadAllText(CharacterTouchTemperaments.Path(directory)));
            var elsewhere = Directory.CreateTempSubdirectory("martlet-temperament-").FullName;
            try
            {
                await CharacterTouchTemperaments.ReplaceAllAsync(elsewhere, CharacterTouchTemperaments.Share(directory));
                var there = CharacterTouchTemperaments.LoadSet(elsewhere);
                Assert.Equal("Tsundere", there.CustomOf(other)!.Name);
                Assert.True(there.UsesBuiltIn(Persona));
            }
            finally { Directory.Delete(elsewhere, true); }

            // Deleted: the persona that used it uses its own again (it has none, so the built-in reactions).
            Assert.Null(await service.DeleteCustomAsync(custom.Id, CancellationToken.None));
            Assert.Empty(service.Saved.Custom);
            Assert.False(service.Saved.Uses.ContainsKey(other));
            Assert.Null(service.For(other));
            Assert.True(service.Saved.UsesBuiltIn(Persona));

            // Re-decide from personality sends only the personality, and the persona then uses its own decided temperament.
            var persona = new PersonaProfile { Id = Persona, ConfigurationRevision = Guid.NewGuid(), Name = "Mira", Text = "Mira adores head pats." };
            string? sent = null;
            await service.DecideAsync(persona, (_, instructions, text, _) =>
            {
                sent = instructions + text;
                return Task.FromResult<(string?, string?)>(("{\"groups\":{\"head\":{\"attitude\":3},\"intimate\":{\"attitude\":-1}}}", null));
            }, CancellationToken.None, use: true);
            Assert.DoesNotContain("Tsundere", sent);
            Assert.Equal(CharacterTouchTemperament.ByThinking, service.For(Persona)!.Source);
            Assert.Equal("dislikes", CharacterTouchTemperaments.Attitude(service.For(Persona), "groin"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task AVersion1FileStillLoadsAndAFileFromANewerMartletIsReported()
    {
        var directory = Directory.CreateTempSubdirectory("martlet-temperament-").FullName;
        try
        {
            File.WriteAllText(CharacterTouchTemperaments.Path(directory), "{\"version\":1,\"personas\":[{\"persona_id\":\"" + Persona +
                "\",\"source\":\"owner\",\"groups\":{\"torso\":{\"attitude\":-1}},\"zones\":{}}]}");
            var set = CharacterTouchTemperaments.LoadSet(directory);
            Assert.Equal(CharacterTouchTemperament.ByOwner, set.Own(Persona)!.Source);
            Assert.Empty(set.Custom);
            Assert.Equal("dislikes", CharacterTouchTemperaments.Attitude(set.For(Persona), "breast_left"));

            // A choice needs version 2, and what was there stays.
            await CharacterTouchTemperaments.UpdateAsync(directory, s => s.Choose(Guid.NewGuid(), CharacterTouchTemperaments.BuiltIn));
            Assert.Contains("\"version\": 2", File.ReadAllText(CharacterTouchTemperaments.Path(directory)));
            Assert.Equal(CharacterTouchTemperament.ByOwner, CharacterTouchTemperaments.Load(directory, Persona)!.Source);

            // A persona can't use a custom temperament that isn't there, and a newer Martlet's temperaments are reported, not dropped.
            await Assert.ThrowsAsync<Martlet.Core.Contracts.ContractException>(() =>
                CharacterTouchTemperaments.UpdateAsync(directory, s => s.Choose(Persona, Guid.NewGuid().ToString())));
            await Assert.ThrowsAsync<Martlet.Core.Contracts.ContractException>(() =>
                CharacterTouchTemperaments.ReplaceAllAsync(directory, "{\"version\":3,\"personas\":[]}"));
            Assert.Equal(CharacterTouchTemperament.ByOwner, CharacterTouchTemperaments.Load(directory, Persona)!.Source);

            // Another computer's temperaments replace all of this PC's, so both then share the same text: the newest change wins.
            var made = CustomTouchTemperament.Copy("Shy cat", null, DateTimeOffset.Now);
            await CharacterTouchTemperaments.UpdateAsync(directory, s => s.WithCustom(made).Choose(Persona, made.Id.ToString()));
            var elsewhere = Directory.CreateTempSubdirectory("martlet-temperament-").FullName;
            try
            {
                await CharacterTouchTemperaments.SaveAsync(elsewhere, Temperament("{\"groups\":{\"head\":{\"attitude\":3}}}"), DateTimeOffset.Now);
                var older = CharacterTouchTemperaments.Share(elsewhere);
                await CharacterTouchTemperaments.ReplaceAllAsync(directory, older);
                var replaced = CharacterTouchTemperaments.LoadSet(directory);
                Assert.Equal(CharacterTouchTemperament.ByThinking, replaced.Own(Persona)!.Source);
                Assert.Empty(replaced.Custom);
                Assert.Equal(older, CharacterTouchTemperaments.Share(directory));
            }
            finally { Directory.Delete(elsewhere, true); }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task TheSharedTextStaysVersion1UntilSomethingNeedsVersion2()
    {
        var directory = Directory.CreateTempSubdirectory("martlet-temperament-").FullName;
        try
        {
            // A temperament as older Martlets saved it shares the same text as before, so updating Martlet changes nothing to sync.
            await CharacterTouchTemperaments.SaveAsync(directory, Temperament("{\"groups\":{\"head\":{\"attitude\":2}}}"), DateTimeOffset.Now);
            var shared = CharacterTouchTemperaments.Share(directory);
            Assert.StartsWith("{\"version\":1,\"personas\":[{", shared);
            Assert.DoesNotContain("\"custom\"", shared);
            Assert.DoesNotContain("\"uses\"", shared);
            Assert.False(CharacterTouchTemperaments.NeedsVersion2(CharacterTouchTemperaments.LoadSet(directory)));

            // An intimate line needs version 2: an older Martlet would drop a temperament with a category it doesn't know.
            await CharacterTouchTemperaments.SaveAsync(directory, Temperament("{\"groups\":{\"intimate\":{\"attitude\":1}}}"), DateTimeOffset.Now);
            Assert.StartsWith("{\"version\":2,", CharacterTouchTemperaments.Share(directory));
            await CharacterTouchTemperaments.SaveAsync(directory, Temperament("{\"groups\":{\"head\":{\"attitude\":2}}}"), DateTimeOffset.Now);
            Assert.StartsWith("{\"version\":1,", CharacterTouchTemperaments.Share(directory));

            // So does a persona that uses the built-in reactions or a custom temperament.
            await CharacterTouchTemperaments.UpdateAsync(directory, s => s.Choose(Persona, CharacterTouchTemperaments.BuiltIn));
            Assert.StartsWith("{\"version\":2,", CharacterTouchTemperaments.Share(directory));
            await CharacterTouchTemperaments.UpdateAsync(directory, s => s.Choose(Persona, null));
            Assert.StartsWith("{\"version\":1,", CharacterTouchTemperaments.Share(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task TheMcpCheckPlaysTheCustomTemperamentOnIntimatePartsAndShowsWhoUsesIt()
    {
        var directory = Directory.CreateTempSubdirectory("martlet-temperament-").FullName;
        try
        {
            var made = new CustomTouchTemperament
            {
                Id = Guid.NewGuid(), Name = "Shy cat",
                Groups = new Dictionary<string, TouchTemperamentEntry>
                {
                    ["torso"] = new() { Attitude = 1 },
                    [CharacterTouchTemperaments.IntimateId] = new() { Attitude = -2, Reactions = ["anger"], LookSeconds = 3 }
                }
            };
            await CharacterTouchTemperaments.UpdateAsync(directory, s => s.WithCustom(made).Choose(Persona, made.Id.ToString()));
            const string answer = "{\"zones\":[{\"id\":\"breast_left\",\"box\":[0.55,0.3,0.65,0.38]},{\"id\":\"groin\",\"box\":[0.45,0.55,0.55,0.62]}," +
                "{\"id\":\"stomach\",\"box\":[0.4,0.4,0.6,0.5]}]}";
            async Task<JsonElement> TouchAsync(string point) => JsonSerializer.SerializeToElement(await Martlet.Mcp.TouchZonesCheck.RunAsync(directory, true,
                null, "model-1", answer, 400, 800, null, null, "{" + point + ",\"hitAreas\":[],\"drawables\":[]}", false, null, null, CancellationToken.None,
                personaId: Persona.ToString()));

            var breast = await TouchAsync("\"x\":0.6,\"y\":0.34");
            var match = breast.GetProperty("match");
            var reaction = match.GetProperty("reaction");
            Assert.Equal(("breast_left", "temperament", "hates", 3.0), (match.GetProperty("zone").GetString(), reaction.GetProperty("from").GetString(),
                reaction.GetProperty("attitude").GetString(), reaction.GetProperty("look").GetDouble()));
            var groin = (await TouchAsync("\"x\":0.5,\"y\":0.58")).GetProperty("match");
            Assert.Equal(("groin", "hates"), (groin.GetProperty("zone").GetString(), groin.GetProperty("reaction").GetProperty("attitude").GetString()));
            Assert.Equal("likes", (await TouchAsync("\"x\":0.42,\"y\":0.45")).GetProperty("match").GetProperty("reaction").GetProperty("attitude").GetString());

            var temperament = breast.GetProperty("temperament");
            Assert.Equal(("custom", "Shy cat"), (temperament.GetProperty("used").GetProperty("Source").GetString(),
                temperament.GetProperty("used").GetProperty("custom").GetString()));
            var persona = Assert.Single(temperament.GetProperty("personas").EnumerateArray());
            Assert.Equal((Persona.ToString(), "custom", "Shy cat"), (persona.GetProperty("personaId").GetString(), persona.GetProperty("uses").GetString(),
                persona.GetProperty("custom").GetString()));
            var custom = Assert.Single(temperament.GetProperty("custom").EnumerateArray());
            Assert.Equal(Persona.ToString(), Assert.Single(custom.GetProperty("usedBy").EnumerateArray()).GetString());
            var intimate = Assert.Single(temperament.GetProperty("categories").EnumerateArray(),
                c => c.GetProperty("Id").GetString() == CharacterTouchTemperaments.IntimateId);
            Assert.Equal("Intimate parts", intimate.GetProperty("Label").GetString());
            Assert.Contains("groin", intimate.GetProperty("parts").EnumerateArray().Select(p => p.GetString()));
        }
        finally { Directory.Delete(directory, true); }
    }}
