using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Conversation;

namespace Martlet.Desktop.Tests;

public sealed class CharacterReactionChangeTests
{
    private static readonly Guid Persona = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 18, 0, 0, TimeSpan.Zero);
    private static readonly CharacterTouchZone HeadPat = new() { Id = "top_of_head", Box = new(0, 0, 1, 1) };
    private static readonly CharacterTouchZone Hand = new() { Id = "hand_left", Box = new(0, 0, 1, 1) };
    private static readonly CharacterTouchZone Stomach = new() { Id = "stomach", Box = new(0, 0, 1, 1) };
    private static readonly CharacterActionSource Angry = new("expression:Angry", CharacterActionKind.Expression, "Angry", "brows");

    private static CharacterActionCatalog Catalog()
    {
        var gestures = CharacterActionInventory.AllGestures
            .Where(g => g.Name is "tilt" or "nod" or "blush" or "smile" or "anger" or "flinch" or "look_away" or "pout" or "lean_in" or "hearts")
            .Select(g => new CharacterActionSource(g.Id, CharacterActionKind.Gesture, g.Name, g.Does));
        var inventory = new CharacterActionInventory("model-1", AvatarRenderer.Live2D, [Angry, .. gestures]);
        return new(inventory, CharacterActions.Merge(inventory, null));
    }

    private static CharacterTouchTemperament Temperament(string answer) =>
        CharacterTouchTemperaments.Parse(answer, Persona, "digest", CharacterTouchTemperament.ByThinking, Now)!;

    private static ReactionToolContext Context(CharacterTouchTemperament? temperament = null, DateTimeOffset? now = null, DateTimeOffset? run = null) => new()
    {
        PersonaId = Persona, Who = "Mira", Temperament = temperament, Catalog = Catalog(), CheckInId = "reactions",
        Zones = new() { ModelId = "model-1", Zones = [HeadPat, Hand, Stomach] }, Run = run ?? now ?? Now, Now = now ?? Now
    };

    private static (ReactionToolAnswer Answer, IReadOnlyList<CharacterReactionChange> Saved) Call(string tool, string arguments,
        IReadOnlyList<CharacterReactionChange> saved, ReactionToolContext context)
    {
        var answer = CharacterReactionTools.Call(tool, arguments, context, saved);
        return (answer, answer.Changes ?? saved);
    }

    [Fact]
    public void TheToolSetOffersEveryToolTheRunnerRuns()
    {
        Assert.Equal(CharacterReactionTools.Names, TouchReactions.Tools.Select(t => t.Name));
        Assert.Equal("touch-reactions", TouchReactions.SetId);
    }

    [Fact]
    public void AFeelingChangesTheCategorysAttitudeAndWhatItsZonesPlay()
    {
        var owner = Temperament("{\"groups\":{\"head\":{\"attitude\":1,\"reactions\":[\"smile\"],\"look\":2}}}");
        var context = Context(owner);
        var (answer, saved) = Call(CharacterReactionTools.Feel, "{\"target\":\"head\",\"feeling\":\"dislikes\",\"why\":\"The user teased me.\"}", [], context);

        Assert.False(answer.Failed, answer.Result);
        Assert.StartsWith("Head and face: dislikes, for 6 hours (change r", answer.Result);
        Assert.DoesNotContain("teased", answer.Result);
        var change = Assert.Single(saved);
        Assert.Equal((CharacterReactionChange.KindFeeling, "head", -1, "The user teased me."), (change.Kind, change.Target, change.Attitude, change.Why));
        Assert.Equal(Now.AddHours(6), change.Until);

        var active = CharacterReactionChanges.Active(saved, Persona, Now);
        var felt = CharacterReactionChanges.Temperament(owner, active, Persona)!;
        Assert.Equal(-1, felt.Groups["head"].Attitude);
        Assert.Null(felt.Groups["head"].Reactions);
        Assert.Equal(2, felt.Groups["head"].LookSeconds);
        // The owner's temperament is never changed.
        Assert.Equal(1, owner.Groups["head"].Attitude);
        // A head zone plays what dislikes plays on the model; a zone of another category keeps its own list.
        var pat = CharacterReactionChanges.Zone(HeadPat, owner, active, context.Catalog);
        Assert.Equal(["gesture:pout"], pat.Reaction.Actions);
        Assert.Equal("dislikes", CharacterTouchZones.React(pat, context.Catalog, felt, 1).Attitude);
        Assert.Same(Hand, CharacterReactionChanges.Zone(Hand, owner, active, context.Catalog));
        // Reaction words the character chose play instead of the feeling's own; none plays nothing.
        (_, saved) = Call(CharacterReactionTools.Feel, "{\"target\":\"top_of_head\",\"feeling\":\"dislikes\",\"reactions\":[\"anger\",\"look away\"],\"why\":\"Still cross.\"}", saved, context);
        Assert.Equal(["expression:Angry", "gesture:look_away"], CharacterReactionChanges.Zone(HeadPat, owner, CharacterReactionChanges.Active(saved, Persona, Now), context.Catalog).Reaction.Actions);
        (_, saved) = Call(CharacterReactionTools.Feel, "{\"target\":\"top_of_head\",\"feeling\":\"neutral\",\"reactions\":[\"none\"],\"why\":\"Ignoring it.\"}", saved, context);
        Assert.Equal([], CharacterReactionChanges.Zone(HeadPat, owner, CharacterReactionChanges.Active(saved, Persona, Now), context.Catalog).Reaction.Actions);
        // The newer feeling about the same zone replaced the older one.
        Assert.Equal(CharacterReactionChange.ByReplaced, saved.Single(c => c.Why == "Still cross.").EndedBy);
    }

    [Fact]
    public void AFeelingMovesAtMostTwoStepsFromTheOwners()
    {
        var owner = Temperament("{\"groups\":{\"head\":{\"attitude\":2}}}");
        var (answer, saved) = Call(CharacterReactionTools.Feel, "{\"target\":\"head\",\"feeling\":\"hates\",\"why\":\"Furious.\"}", [], Context(owner));
        Assert.False(answer.Failed, answer.Result);
        Assert.Contains("neutral is as far from the owner's loves as a change goes", answer.Result);
        Assert.Equal(0, Assert.Single(saved).Attitude);
        // With no temperament (the built-in reactions) the owner's is neutral.
        (_, saved) = Call(CharacterReactionTools.Feel, "{\"target\":\"arms\",\"feeling\":\"craves\",\"why\":\"Warm.\"}", [], Context());
        Assert.Equal(2, Assert.Single(saved).Attitude);
        var felt = CharacterReactionChanges.Temperament(null, CharacterReactionChanges.Active(saved, Persona, Now), Persona)!;
        Assert.Equal(["arms"], felt.Groups.Keys);
        Assert.Equal(CharacterReactionChange.ByCharacter, felt.Source);
    }

    [Fact]
    public void AMoodShiftsEveryTouchAndKeepsListsItDoesNotChange()
    {
        var owner = Temperament("{\"groups\":{\"head\":{\"attitude\":2},\"torso\":{\"attitude\":-2}},\"zones\":{\"hand_left\":{\"attitude\":1}}}");
        var context = Context(owner);
        var (answer, saved) = Call(CharacterReactionTools.Mood, "{\"shift\":-1,\"hours\":2,\"why\":\"Angry with the user.\"}", [], context);
        Assert.False(answer.Failed, answer.Result);
        Assert.StartsWith("Every touch: one step less liked, for 2 hours", answer.Result);
        var active = CharacterReactionChanges.Active(saved, Persona, Now);
        var felt = CharacterReactionChanges.Temperament(owner, active, Persona)!;
        Assert.Equal(1, felt.Groups["head"].Attitude);
        Assert.Equal(-2, felt.Groups["torso"].Attitude);
        Assert.Equal(-1, felt.Groups["arms"].Attitude);
        Assert.Equal(0, felt.Zones["hand_left"].Attitude);
        // An intimate category the temperament leaves out still follows its body group.
        Assert.False(felt.Groups.ContainsKey(CharacterTouchTemperaments.IntimateId));
        // The stomach is hated already: a mood can't make that worse, so its list stays the owner's.
        var stomach = Stomach with { Reaction = new() { Actions = ["gesture:nod"] } };
        Assert.Same(stomach, CharacterReactionChanges.Zone(stomach, owner, active, context.Catalog));
        Assert.Equal(["gesture:tilt"], CharacterReactionChanges.Zone(Hand, owner, active, context.Catalog).Reaction.Actions);

        Assert.True(Call(CharacterReactionTools.Mood, "{\"shift\":0,\"why\":\"Meh.\"}", saved, context).Answer.Failed);
        (_, saved) = Call(CharacterReactionTools.Mood, "{\"shift\":\"-5\",\"why\":\"Very angry.\"}", saved, context);
        Assert.Equal(-2, CharacterReactionChanges.Active(saved, Persona, Now).Single().Shift);
    }

    [Fact]
    public void AZoneListTakesIdsTagsNamesAndSoundsOfTheShownModelOnly()
    {
        var context = Context();
        var (answer, saved) = Call(CharacterReactionTools.React, "{\"zone\":\"left hand\",\"plays\":[\"Angry\",\"{pout}\",\"gesture:nod\",\"sound:Laugh\"],\"why\":\"Hands off.\"}", [], context);
        Assert.False(answer.Failed, answer.Result);
        var change = Assert.Single(saved);
        Assert.Equal("hand_left", change.Target);
        Assert.Equal(["expression:Angry", "gesture:pout", "gesture:nod"], change.Reactions);
        Assert.Contains("a zone plays at most", answer.Result);
        Assert.Equal(change.Reactions, CharacterReactionChanges.Zone(Hand, null, CharacterReactionChanges.Active(saved, Persona, Now), context.Catalog).Reaction.Actions);

        Assert.Equal(["sound:laugh"], Call(CharacterReactionTools.React, "{\"zone\":\"hand_left\",\"plays\":[\"sound:laugh\"],\"why\":\"Ha.\"}", [], context).Saved.Single().Reactions);
        var unknown = Call(CharacterReactionTools.React, "{\"zone\":\"hand_left\",\"plays\":[\"dance\"],\"why\":\"x\"}", [], context).Answer;
        Assert.True(unknown.Failed);
        Assert.Contains("can't play: dance", unknown.Result);
        // Only zones in use on the shown model.
        Assert.True(Call(CharacterReactionTools.React, "{\"zone\":\"tail\",\"plays\":[],\"why\":\"x\"}", [], context).Answer.Failed);
        Assert.True(Call(CharacterReactionTools.Feel, "{\"target\":\"wings\",\"feeling\":\"likes\",\"why\":\"x\"}", [], context).Answer.Failed);
    }

    [Fact]
    public void EveryChangeIsBounded()
    {
        var context = Context();
        IReadOnlyList<CharacterReactionChange> saved = [];
        // A reason is required, and the hours are kept within their range.
        Assert.Contains("Say why", Call(CharacterReactionTools.Mood, "{\"shift\":1}", saved, context).Answer.Result);
        Assert.True(Call(CharacterReactionTools.Mood, "not json", saved, context).Answer.Failed);
        var (longest, _) = Call(CharacterReactionTools.Mood, "{\"shift\":1,\"hours\":500,\"why\":\"Happy.\"}", saved, context);
        Assert.Contains("for 72 hours (a change lasts 15 minutes to 72 hours)", longest.Result);

        // At most 4 changes in one run.
        string[] targets = ["head", "torso", "arms", "lower_body", "extras"];
        foreach (var target in targets.Take(CharacterReactionChanges.MaximumPerRun))
            (_, saved) = Call(CharacterReactionTools.Feel, $"{{\"target\":\"{target}\",\"feeling\":\"likes\",\"why\":\"Nice.\"}}", saved, context);
        var fifth = Call(CharacterReactionTools.Feel, "{\"target\":\"extras\",\"feeling\":\"likes\",\"why\":\"Nice.\"}", saved, context).Answer;
        Assert.True(fifth.Failed);
        Assert.Contains("already made 4 changes in this check", fifth.Result);
        // At most 8 in effect at once (a change of the same target replaces its older one and still fits).
        var later = Context(now: Now.AddMinutes(10));
        foreach (var zone in new[] { "top_of_head", "hand_left", "stomach", "intimate" })
            (_, saved) = Call(CharacterReactionTools.Feel, $"{{\"target\":\"{zone}\",\"feeling\":\"likes\",\"why\":\"Nice.\"}}", saved, later);
        Assert.Equal(CharacterReactionChanges.MaximumActive, CharacterReactionChanges.Active(saved, Persona, later.Now).Count);
        var even = Context(now: Now.AddMinutes(20));
        Assert.Contains("Undo one first", Call(CharacterReactionTools.Mood, "{\"shift\":1,\"why\":\"x\"}", saved, even).Answer.Result);
        Assert.False(Call(CharacterReactionTools.Feel, "{\"target\":\"head\",\"feeling\":\"loves\",\"why\":\"x\"}", saved, even).Answer.Failed);
        // At most 12 in a day: after undoing all, the day's count still holds.
        (_, saved) = Call(CharacterReactionTools.Undo, "{\"change\":\"all\",\"why\":\"Calm now.\"}", saved, even);
        Assert.Empty(CharacterReactionChanges.Active(saved, Persona, even.Now));
        for (var n = 0; n < 4; n++)
            (_, saved) = Call(CharacterReactionTools.Feel, $"{{\"target\":\"{targets[n]}\",\"feeling\":\"likes\",\"why\":\"Again.\"}}", saved, Context(now: Now.AddMinutes(30)));
        var thirteenth = Call(CharacterReactionTools.Feel, "{\"target\":\"extras\",\"feeling\":\"likes\",\"why\":\"x\"}", saved, Context(now: Now.AddMinutes(40)));
        Assert.Contains("already made 12 changes today", thirteenth.Answer.Result);
        // A change ends on its own.
        Assert.Empty(CharacterReactionChanges.Active(saved, Persona, Now.AddDays(4)));
    }

    [Fact]
    public void TheCharacterAndTheOwnerUndoChanges()
    {
        var context = Context();
        var (_, saved) = Call(CharacterReactionTools.Mood, "{\"shift\":-1,\"why\":\"Hurt.\"}", [], context);
        (_, saved) = Call(CharacterReactionTools.Feel, "{\"target\":\"stomach\",\"feeling\":\"dislikes\",\"why\":\"Ticklish today.\"}", saved, context);
        var mood = saved.Single(c => c.Kind == CharacterReactionChange.KindMood);
        var (undone, after) = Call(CharacterReactionTools.Undo, $"{{\"change\":\"{mood.Id}\",\"why\":\"Forgave them.\"}}", saved, context);
        Assert.Equal($"Undid Every touch: one step less liked (change {mood.Id}).", undone.Result);
        Assert.Equal(CharacterReactionChange.ByCharacter, after.Single(c => c.Id == mood.Id).EndedBy);
        Assert.True(Call(CharacterReactionTools.Undo, "{\"change\":\"r000000\",\"why\":\"x\"}", after, context).Answer.Failed);

        var owner = CharacterReactionChanges.End(after, null, CharacterReactionChange.ByOwner, Now);
        Assert.Empty(CharacterReactionChanges.Active(owner, Persona, Now));
        Assert.Equal(CharacterReactionChange.ByOwner, owner.Single(c => c.Kind == CharacterReactionChange.KindFeeling).EndedBy);
        // Another persona's changes are its own.
        Assert.Empty(CharacterReactionChanges.Active(saved, Guid.NewGuid(), Now));
    }

    [Fact]
    public void ReadingDescribesFeelingsZonesChangesAndLimits()
    {
        var owner = Temperament("{\"groups\":{\"head\":{\"attitude\":1}}}");
        var context = Context(owner);
        var (_, saved) = Call(CharacterReactionTools.Feel, "{\"target\":\"head\",\"feeling\":\"dislikes\",\"why\":\"Teased.\"}", [], context);
        var text = CharacterReactionTools.Call(CharacterReactionTools.Read, "{}", context, saved).Result;
        Assert.StartsWith("How Mira reacts to touches now", text);
        Assert.Contains("- head: Head and face - dislikes (the owner's: likes)", text);
        Assert.Contains("- top_of_head: Top of head, head - dislikes; plays pout", text);
        Assert.Contains("- expression:Angry - Angry, expression", text);
        Assert.Contains(": Head and face: dislikes, until ", text);
        Assert.Contains("; Teased.", text);
        Assert.Contains("Limits: 3 more changes this check, 11 more today and 7 more in effect at once.", text);
        Assert.Contains("There is no tool", CharacterReactionTools.Call("delete_everything", "{}", context, saved).Result);
    }

    [Fact]
    public async Task SavesAndReadsTheChangesAndRejectsBadOnes()
    {
        var directory = Directory.CreateTempSubdirectory("martlet-reaction-changes-").FullName;
        try
        {
            Assert.Empty(CharacterReactionChanges.Load(directory));
            var (_, made) = Call(CharacterReactionTools.Mood, "{\"shift\":2,\"why\":\"So happy.\"}", [], Context(now: DateTimeOffset.UtcNow));
            await CharacterReactionChanges.UpdateAsync(directory, _ => made);
            var loaded = CharacterReactionChanges.Load(directory);
            Assert.Equal(made, loaded);
            await Assert.ThrowsAsync<Martlet.Core.Contracts.ContractException>(() =>
                CharacterReactionChanges.UpdateAsync(directory, now => [.. now, now[0] with { Id = "r2", Why = "two\nlines" }]));
            await Assert.ThrowsAsync<Martlet.Core.Contracts.ContractException>(() =>
                CharacterReactionChanges.UpdateAsync(directory, now => [.. now, now[0] with { Id = "r3", Until = now[0].At.AddDays(10) }]));
            Assert.Equal(made, CharacterReactionChanges.Load(directory));
            // Nothing returned: nothing is written.
            Assert.Equal(made, await CharacterReactionChanges.UpdateAsync(directory, _ => null));

            var service = new CharacterReactionChangeService(directory);
            Assert.Single(service.Saved);
            Assert.Null(await service.EndAsync(null, null, CharacterReactionChange.ByReset, CancellationToken.None));
            Assert.Equal(CharacterReactionChange.ByReset, CharacterReactionChanges.Load(directory).Single().EndedBy);
            var answer = await service.CallAsync(CharacterReactionTools.Feel, "{\"target\":\"head\",\"feeling\":\"likes\",\"why\":\"Better.\"}",
                Context(now: DateTimeOffset.UtcNow), CancellationToken.None);
            Assert.False(answer.Failed, answer.Result);
            Assert.Single(service.Active(Persona));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
