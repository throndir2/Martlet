using Martlet.Avatar.Hosting;
using Martlet.Core.Settings;

namespace Martlet.Desktop.Tests;

public sealed class CharacterTouchResetTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "martlet-touch-reset-" + Guid.NewGuid().ToString("N"));
    private static readonly PersonaProfile Mira = new() { Id = Guid.NewGuid(), ConfigurationRevision = Guid.NewGuid(), Name = "Mira", Text = "Shy and kind." };
    private static readonly Guid Aki = Guid.NewGuid();

    public CharacterTouchResetTests() => Directory.CreateDirectory(directory);

    public void Dispose()
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
    }

    private static CharacterTouchZone Zone(string id, CharacterTouchReaction? reaction = null, bool added = false) =>
        new() { Id = id, Box = new(0.4, 0.1, 0.2, 0.1), Reaction = reaction ?? new(), Added = added, Enabled = id != "tail" };

    private static CharacterTouchZoneSettings Model(string id, params CharacterTouchZone[] zones) =>
        new() { ModelId = id, DetectedBy = CharacterTouchZoneSettings.ByVision, Zones = zones };

    private static CharacterTouchTemperament Own(Guid persona, string source = CharacterTouchTemperament.ByThinking) => new()
    {
        PersonaId = persona, Source = source, UpdatedAt = DateTimeOffset.UtcNow,
        Groups = new Dictionary<string, TouchTemperamentEntry> { ["head"] = new() { Attitude = 2 } }
    };

    private (CharacterTouchZoneService Zones, CharacterTemperamentService Temperaments, List<PersonaProfile> Redecided, TouchResetTarget Target) Target(
        string modelId, PersonaProfile? persona)
    {
        var zones = new CharacterTouchZoneService(directory);
        zones.Follow(modelId);
        var temperaments = new CharacterTemperamentService(directory);
        var redecided = new List<PersonaProfile>();
        return (zones, temperaments, redecided, new(zones, temperaments, persona, _ => new CharacterTouchReaction(), redecided.Add));
    }

    [Fact]
    public void LevelsAreOneListAndEverythingRunsTheLevelsNoOtherCovers()
    {
        var ids = CharacterTouchReset.Levels.Select(l => l.Id).ToArray();
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.Equal(CharacterTouchReset.EverythingId, ids[^1]);
        // Removing the zones removes their reactions too, so Everything forgets the zones and the temperament, and ends the
        // character's own reaction changes.
        Assert.Equal([CharacterTouchReset.ZonesId, CharacterTouchReset.TemperamentId, CharacterTouchReset.ReactionChangesId],
            CharacterTouchReset.Parts().Select(l => l.Id));
        Assert.Same(CharacterTouchReset.Levels[0], CharacterTouchReset.Level("no such level"));
    }

    [Fact]
    public async Task ReactionsGoBackToAFreshZonesAndTheZonesStay()
    {
        await CharacterTouchZones.SaveAsync(directory, Model("model-1",
            Zone("hair", new() { Actions = ["gesture:blush"], Narration = "my favorite spot" }),
            Zone("cheek_left", new() { Notices = false, CooldownSeconds = 30 }),
            Zone("tail"),
            Zone("chest", new() { Actions = [] })), DateTimeOffset.Now);
        var (zones, _, _, target) = Target("model-1", Mira);
        var level = CharacterTouchReset.Level(CharacterTouchReset.ReactionsId);

        var loses = level.Loses(target);
        Assert.Equal([
            "the reactions you chose for 2 zones: Hair and Chest",
            "Martlet notices turned off for 1 zone: Left cheek",
            "your own words for 1 zone: Hair",
            "the rest you changed for 1 zone: Left cheek"], loses);
        var question = CharacterTouchReset.Question(level, target, loses);
        Assert.Contains("\u2022 The reactions you chose for 2 zones: Hair and Chest.", question);
        Assert.Contains("You can't undo this.", question);

        Assert.Null(await level.Run(target, CancellationToken.None));
        var reset = CharacterTouchZones.Load(directory, "model-1")!;
        Assert.Equal(["hair", "cheek_left", "tail", "chest"], reset.Zones.Select(z => z.Id));
        Assert.All(reset.Zones, z => Assert.True(CharacterTouchZones.IsFresh(z, new())));
        Assert.False(reset.Zones.Single(z => z.Id == "tail").Enabled);
        Assert.Empty(level.Loses(target));
    }

    [Fact]
    public async Task ReactionsGetTheFreshListAndAListLikeItIsNothingToLose()
    {
        await CharacterTouchZones.SaveAsync(directory, Model("model-1",
            Zone("hair", new() { Actions = ["gesture:nod", "gesture:blush"] }),
            Zone("tail", new() { Actions = ["gesture:blush"] })), DateTimeOffset.Now);
        var zones = new CharacterTouchZoneService(directory);
        zones.Follow("model-1");
        IReadOnlyList<string> Seed(CharacterTouchZone zone) => zone.Id == "hair" ? ["gesture:nod", "gesture:blush"] : ["sound:laugh"];
        var target = new TouchResetTarget(zones, new CharacterTemperamentService(directory), Mira, z => new() { Actions = Seed(z) }, _ => { });
        var level = CharacterTouchReset.Level(CharacterTouchReset.ReactionsId);

        Assert.Equal(["the reactions you chose for 1 zone: Tail"], level.Loses(target));
        Assert.Null(await level.Run(target, CancellationToken.None));
        var reset = CharacterTouchZones.Load(directory, "model-1")!;
        Assert.Equal(["gesture:nod", "gesture:blush"], reset.Zones[0].Reaction.Actions);
        Assert.Equal(["sound:laugh"], reset.Zones[1].Reaction.Actions);
        Assert.Empty(level.Loses(target));
    }

    [Fact]
    public async Task ZonesForgetOnlyThisModelAndItsPictures()
    {
        await CharacterTouchZones.SaveAsync(directory, Model("model-1", Zone("hair"), Zone("halo", added: true) with { Label = "Halo" }) with { IncludeIntimate = false }, DateTimeOffset.Now);
        await CharacterTouchZones.SaveAsync(directory, Model("model-2", Zone("hair")), DateTimeOffset.Now);
        await CharacterTouchZones.SaveSnapshotAsync(directory, "model-1", [1, 2, 3]);
        await CharacterTouchZones.SaveSnapshotAsync(directory, "model-2", [1, 2, 3]);
        Directory.CreateDirectory(CharacterTouchZones.SentFolder(directory, "model-1"));
        await File.WriteAllTextAsync(Path.Combine(CharacterTouchZones.SentFolder(directory, "model-1"), "1-parts.png"), "x");
        var (zones, _, _, target) = Target("model-1", Mira);
        var level = CharacterTouchReset.Level(CharacterTouchReset.ZonesId);

        Assert.Equal([
            "this model's 2 zones (found by your Thinking model), with their boxes, names, on and off choices and reactions",
            "the 1 zone you added: Halo (Detect zones no longer looks for it)",
            "your choice to leave out intimate zones",
            "the picture of the character the zones were found in",
            "the pictures the last Detect zones sent to your Thinking model"], level.Loses(target));

        Assert.Null(await level.Run(target, CancellationToken.None));
        Assert.Null(CharacterTouchZones.Load(directory, "model-1"));
        Assert.NotNull(CharacterTouchZones.Load(directory, "model-2"));
        Assert.False(File.Exists(CharacterTouchZones.SnapshotPath(directory, "model-1")));
        Assert.True(File.Exists(CharacterTouchZones.SnapshotPath(directory, "model-2")));
        Assert.False(Directory.Exists(CharacterTouchZones.SentFolder(directory, "model-1")));
        Assert.Null(zones.Current);
        // The page places a first guess again, as for a model it never saw.
        Assert.True(zones.NeedsFirstGuess);
        Assert.Empty(level.Loses(target));
    }

    [Fact]
    public async Task TemperamentForgetsOnlyThisPersonasAndDecidesItAgain()
    {
        await CharacterTouchTemperaments.UpdateAsync(directory, set => set.WithOwn(Own(Mira.Id, CharacterTouchTemperament.ByOwner)).WithOwn(Own(Aki))
            .Choose(Mira.Id, CharacterTouchTemperaments.BuiltIn).Choose(Aki, CharacterTouchTemperaments.BuiltIn));
        var (_, temperaments, redecided, target) = Target("model-1", Mira);
        var level = CharacterTouchReset.Level(CharacterTouchReset.TemperamentId);

        Assert.Equal(["Mira's own touch temperament (with your own changes)", "Mira's choice to use the built-in reactions"], level.Loses(target));
        Assert.Contains("decides how Mira reacts to touch again from its personality", level.After(target));

        Assert.Null(await level.Run(target, CancellationToken.None));
        var saved = CharacterTouchTemperaments.LoadSet(directory);
        Assert.Null(saved.Own(Mira.Id));
        Assert.False(saved.Uses.ContainsKey(Mira.Id));
        Assert.NotNull(saved.Own(Aki));
        Assert.True(saved.UsesBuiltIn(Aki));
        Assert.Equal([Mira], redecided);
        Assert.Null(temperaments.Own(Mira.Id));
        Assert.Empty(level.Loses(target));
    }

    [Fact]
    public async Task EverythingResetsTheZonesAndTheTemperamentAndNothingElse()
    {
        await CharacterTouchZones.SaveAsync(directory, Model("model-1", Zone("hair", new() { Actions = ["gesture:blush"] })), DateTimeOffset.Now);
        await CharacterTouchZones.SaveAsync(directory, Model("model-2", Zone("hair", new() { Actions = ["gesture:blush"] })), DateTimeOffset.Now);
        await CharacterTouchTemperaments.UpdateAsync(directory, set => set.WithOwn(Own(Mira.Id)).WithOwn(Own(Aki)));
        var (_, _, redecided, target) = Target("model-1", Mira);
        var level = CharacterTouchReset.Level(CharacterTouchReset.EverythingId);

        var loses = level.Loses(target);
        Assert.Equal(2, loses.Count);
        Assert.StartsWith("this model's 1 zone", loses[0]);
        Assert.StartsWith("Mira's own touch temperament (decided by your Thinking model", loses[1]);
        Assert.Contains("first guess", level.After(target));
        Assert.Contains("Mira", level.After(target));

        Assert.Null(await level.Run(target, CancellationToken.None));
        Assert.Null(CharacterTouchZones.Load(directory, "model-1"));
        Assert.NotNull(CharacterTouchZones.Load(directory, "model-2"));
        Assert.Null(CharacterTouchTemperaments.LoadSet(directory).Own(Mira.Id));
        Assert.NotNull(CharacterTouchTemperaments.LoadSet(directory).Own(Aki));
        Assert.Equal([Mira], redecided);
        Assert.Empty(level.Loses(target));
    }

    [Fact]
    public void AFreshCharacterHasNothingToLose()
    {
        var (_, _, _, target) = Target("model-1", Mira);
        Assert.All(CharacterTouchReset.Levels, level => Assert.Empty(level.Loses(target)));
        var (_, _, _, nobody) = Target("model-1", null);
        Assert.Empty(CharacterTouchReset.Level(CharacterTouchReset.TemperamentId).Loses(nobody));
    }
}
