using Martlet.Core.Settings;
using Martlet.Desktop;
using Martlet.Memory;
using Martlet.Providers;
using Xunit;

namespace Martlet.Desktop.Tests;

/// <summary>Search words and history (docs/MEMORY.md, "Search words, history and tidying"): remembering's WORDS lines, the
/// background upkeep's plan, answer and store request, and remembering's corrections kept as history.</summary>
public sealed class MemoryUpkeepTests
{
    private sealed class SteppedClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan by) => now += by;
    }

    private sealed class Scope : IDisposable
    {
        internal string Root { get; } = Path.Combine(AppContext.BaseDirectory, "memory-upkeep", Guid.NewGuid().ToString("N"));
        internal string Data => Path.Combine(Root, "data");

        internal MemoryStore Open(string name, TimeProvider clock)
        {
            var preview = MemoryStoreActivationPreview.Create(Path.Combine(Root, name));
            return MemoryStore.Open(preview, preview.Authorize(MemoryConsentDecision.Allow), clock);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    [Fact]
    public void AWordsLineGivesTheFactAboveItSearchWords()
    {
        var operations = MemoryCapture.Parse(
            "WORDS: before, anything\n" +
            "REMEMBER: The user is vegetarian.\n" +
            "WORDS: Diet, food, MEAT, diet, cooking.\n" +
            "UPDATE 1: The user lives in Seattle.\n\n" +
            "**Words:** home, city\n" +
            "FORGET 2\n" +
            "WORDS: forget, has, none\n" +
            "REMEMBER: A fourth line is too many.\n" +
            "WORDS: ignored", 2);
        Assert.Equal(3, operations.Count);
        Assert.Equal("diet, food, meat, cooking", operations[0].SearchWords);
        Assert.Equal((MemoryCaptureKind.Update, "home, city"), (operations[1].Kind, operations[1].SearchWords));
        Assert.Equal((MemoryCaptureKind.Forget, (string?)null), (operations[2].Kind, operations[2].SearchWords));

        // A REMEMBER line with nothing usable adds nothing, and the WORDS line after it belongs to nothing.
        var skipped = MemoryCapture.Parse("REMEMBER: ok\nWORDS: lost\nREMEMBER: The user has a cat.\nWORDS: pets, animals", 0);
        Assert.Equal("pets, animals", Assert.Single(skipped).SearchWords);
        // The third operation's WORDS line still counts.
        var three = MemoryCapture.Parse("REMEMBER: Fact one here.\nREMEMBER: Fact two here.\nREMEMBER: Fact three here.\nWORDS: third", 0);
        Assert.Equal("third", three[2].SearchWords);
    }

    [Fact]
    public void SearchWordsAreCleanedAndBounded()
    {
        Assert.Null(MemorySearchWords.Normalize("  ,  ;  "));
        Assert.Equal("diet, food, home cooking", MemorySearchWords.Normalize("\"Diet\", food; food,  home   cooking ."));
        Assert.Equal(MemorySearchWords.MaximumWords,
            MemorySearchWords.Normalize(string.Join(",", Enumerable.Range(0, 40).Select(i => "w" + i)))!.Split(", ").Length);
        Assert.DoesNotContain("x", MemorySearchWords.Normalize(new string('x', 41) + ", ok")!);
        Assert.Equal("a, b, c", MemorySearchWords.Union("a, b", null, "b, c"));
        Assert.Contains("WORDS:", PromptCatalog.Default(PromptCatalog.MemoryCapture));
        Assert.Contains("{nothing}", PromptCatalog.Default(PromptCatalog.MemoryUpkeep));
        Assert.Equal(["nothing"], PromptCatalog.All.Single(p => p.Id == PromptCatalog.MemoryUpkeep).Placeholders);
    }

    [Fact]
    public async Task UpkeepGroupsOnlyOnePersonsRelatedFactsAndAsksWordsForTheRest()
    {
        using var scope = new Scope();
        var clock = new SteppedClock(new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        using var store = scope.Open("store", clock);
        async Task<MemoryFact> Save(string content, string? words = null, string? voice = null)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            return (await store.SaveAsync(new()
            {
                Content = content, Provenance = MemoryProvenance.Conversation(Guid.NewGuid(), clock.GetUtcNow()),
                Retention = MemoryRetention.UntilDeleted(), SearchWords = words, VoiceId = voice
            })).Fact;
        }
        var veg = await Save("The user is vegetarian.", "diet, food, meat, cooking");
        var meat = await Save("The user doesn't eat meat.", "diet, food, vegetarian, cooking");
        var alexMeat = await Save("Alex doesn't eat meat.", "diet, food, vegetarian, cooking", "alex");
        var plain = await Save("The user's sister is called Ana.");
        var snapshot = await store.SnapshotAsync();

        var plan = MemoryUpkeep.Plan(snapshot, clock.GetUtcNow(), null);
        var group = Assert.Single(plan.Groups);
        Assert.Equal(new[] { veg.Id, meat.Id }.Order(), group.Select(n => plan[n].Id).Order());
        Assert.DoesNotContain(plan.Facts, f => f.Id == alexMeat.Id && plan.Groups.Any(g => g.Any(n => plan[n].Id == f.Id)));
        Assert.Equal(plain.Id, plan[Assert.Single(plan.NeedWords)].Id);
        var text = MemoryUpkeep.Text(plan, fact => fact.VoiceId is null ? "" : "[Alex] ");
        Assert.Contains("Group 1:", text);
        Assert.Contains("Facts that need search words:", text);
        Assert.Contains("(2026-10-01; needs words)", text);
        Assert.Equal(4, plan.Examined);
        Assert.Equal((alexMeat.UpdatedAtUtc > plain.UpdatedAtUtc ? alexMeat : plain).Id, plan.CheckedThrough!.Value.Id);

        // Checked through the newest fact, nothing is grouped again; a fact asked about already isn't asked again.
        var later = MemoryUpkeep.Plan(snapshot, clock.GetUtcNow(), plan.CheckedThrough, fact => fact.Id == plain.Id);
        Assert.False(later.HasWork);
        Assert.Equal(0, later.Examined);
    }

    [Fact]
    public async Task UpkeepAnswersBecomeOneStoreRequestForTheExactRevisionsListed()
    {
        using var scope = new Scope();
        var clock = new SteppedClock(new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        using var store = scope.Open("store", clock);
        async Task<MemoryFact> Save(string content, string? words)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            return (await store.SaveAsync(new()
            {
                Content = content, Provenance = MemoryProvenance.UserEntry(Guid.NewGuid(), clock.GetUtcNow()),
                Retention = MemoryRetention.UntilDeleted(), SearchWords = words
            })).Fact;
        }
        var portland = await Save("The user lives in Portland.", "home, city, address, residence");
        var seattle = await Save("The user moved to Seattle.", "home, city, address, relocation");
        var veg = await Save("The user is vegetarian.", "diet, food, meat, cooking");
        var meat = await Save("The user doesn't eat meat.", null);
        var plan = MemoryUpkeep.Plan(await store.SnapshotAsync(), clock.GetUtcNow(), null);
        int N(MemoryFact fact) => plan.Facts.ToList().FindIndex(f => f.Id == fact.Id) + 1;
        Assert.Contains(N(meat), plan.NeedWords);

        var answer = MemoryUpkeep.Parse(
            $"OUTDATED {N(portland)} BY {N(seattle)}\n" +
            $"- **SAME {N(veg)}, {N(meat)}:** The user is vegetarian and doesn't eat meat.\n" +
            $"WORDS {N(meat)}: diet, never\n" +
            $"OUTDATED {N(seattle)} BY {N(portland)}\n" +
            "NOTHING", plan);
        Assert.Equal((N(portland), N(seattle)), Assert.Single(answer.Outdated));
        var same = Assert.Single(answer.Same);
        Assert.Equal((N(veg), "The user is vegetarian and doesn't eat meat."), (same.Keep, same.Content));
        // Words for a fact folded into another are left out.
        Assert.Empty(answer.Words);

        var request = MemoryUpkeep.Request(plan, answer);
        var merge = Assert.Single(request.Merges);
        Assert.Equal((veg.Id, veg.Revision), (merge.KeepId, merge.KeepRevision));
        Assert.Equal("diet, food, meat, cooking", merge.SearchWords);
        var receipt = await store.TidyAsync(request);
        Assert.Equal((1, 1, 0), (receipt.Retired, receipt.Merged, receipt.Worded));
        var facts = (await store.InspectAsync()).Facts;
        Assert.True(facts.Single(f => f.Id == portland.Id).IsHistory);
        Assert.Equal("The user is vegetarian and doesn't eat meat.", facts.Single(f => f.Id == veg.Id).Content);
        // The same answer again changes nothing: every revision it names is gone or changed.
        var again = await store.TidyAsync(request);
        Assert.Equal((0, 0, 0), (again.Retired, again.Merged, again.Worded));
    }

    [Fact]
    public async Task RememberingSavesSearchWordsAndKeepsTheFactAnUpdateReplacesAsHistory()
    {
        using var scope = new Scope();
        var clock = new SteppedClock(DateTimeOffset.UtcNow);
        var store = new SettingsStore(scope.Data);
        var initial = SetupSettings.Begin(null);
        var saved = await store.SaveAsync(initial, null);
        using var memory = new DesktopMemoryService(store, clock,
            (preview, approval, token) => MemoryStore.Open(preview, approval, clock, null, token));
        var configured = await memory.SaveConfigurationAsync(initial, saved.Revision, true, MemoryStoragePolicy.AppLocalData, null);
        var settings = configured.Settings.Memory!;

        clock.Advance(TimeSpan.FromMinutes(1));
        await memory.RememberAsync(settings.ConfigurationRevision, [],
            [new(MemoryCaptureKind.Remember, Content: "The user lives in Portland.", SearchWords: "home, city, address")]);
        var fact = Assert.Single((await memory.InspectAsync(settings.ConfigurationRevision)).Facts);
        Assert.Equal("home, city, address", fact.SearchWords);
        Assert.Equal(fact.Id, (await memory.RecallAsync(settings, "Which city is home?")).Facts[0].Id);

        clock.Advance(TimeSpan.FromDays(2));
        var shown = (await memory.KnownFactsAsync(settings, "Where do I live?", 10)).Facts;
        var changes = await memory.RememberAsync(settings.ConfigurationRevision, shown,
            [new(MemoryCaptureKind.Update, 1, "The user lives in Seattle.", SearchWords: "home, city, relocation")]);
        Assert.Equal(MemoryCaptureKind.Update, Assert.Single(changes).Kind);
        var facts = (await memory.InspectAsync(settings.ConfigurationRevision)).Facts;
        var current = Assert.Single(facts, f => !f.IsHistory);
        var earlier = Assert.Single(facts, f => f.IsHistory);
        Assert.Equal(("The user lives in Seattle.", "home, city, relocation"), (current.Content, current.SearchWords));
        Assert.Equal(("The user lives in Portland.", fact.Id, clock.GetUtcNow()), (earlier.Content, earlier.ReplacedBy, earlier.ReplacedAtUtc));

        // Recall and remembering never see the replaced fact again, even when nothing else matches.
        var recalled = await memory.RecallAsync(settings, "Portland");
        Assert.Equal(current.Id, Assert.Single(recalled.Facts).Id);
        Assert.Equal(current.Id, Assert.Single((await memory.KnownFactsAsync(settings, "Portland", 10)).Facts).Id);
        // Saying the old fact again is new: history doesn't count as already remembered.
        Assert.Single(await memory.RememberAsync(settings.ConfigurationRevision, [],
            [new(MemoryCaptureKind.Remember, Content: "The user lives in Portland.")]));
    }
}
