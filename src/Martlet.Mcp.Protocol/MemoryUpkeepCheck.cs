using System.IO;
using Martlet.Mcp.Shared;
using Martlet.Memory;

namespace Martlet.Mcp;

/// <summary>memory_upkeep_check: search words, replaced facts kept as history and tidying memory (docs/MEMORY.md, "Search words,
/// history and tidying"), rehearsed with the production code on synthetic facts in a disposable store: Martlet.Memory's store and
/// recall, and the desktop's own upkeep (<see cref="MemoryUpkeep"/>: the plan, the prompt's fact list, parsing the answer and
/// the store request). The pool member's answers are fixtures (NOT AI): no model, network or credentials. It never opens the
/// owner's memory.</summary>
internal static class MemoryUpkeepCheck
{
    internal static async Task<object> RunAsync(CancellationToken cancellation)
    {
        var steps = new List<object>();
        var failures = new List<string>();
        void Step(string name, bool passed, object detail)
        {
            steps.Add(new { name, passed, detail });
            if (!passed) failures.Add(name);
        }
        var root = Path.Combine(Path.GetTempPath(), "martlet-memory-upkeep-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var preview = MemoryStoreActivationPreview.Create(Path.Combine(root, "store"));
            using var store = MemoryStore.Open(preview, preview.Authorize(MemoryConsentDecision.Allow));
            async Task<MemoryFact> Save(string content) =>
                (await store.SaveAsync(new()
                {
                    Content = content, Provenance = MemoryProvenance.Conversation(Guid.NewGuid(), DateTimeOffset.UtcNow),
                    Retention = MemoryRetention.UntilDeleted()
                }, cancellation)).Fact;
            async Task<IReadOnlyList<MemoryRetrievalHit>> Recall(string text) =>
                MemoryQuery.TryFromBoundedSource(text, 5) is { } query ? (await store.RetrieveAsync(query, cancellation)).Hits : [];
            async Task<MemoryFact?> Find(Guid id) => (await store.InspectAsync(cancellation)).Facts.FirstOrDefault(f => f.Id == id);

            var vegetarian = await Save("The user is vegetarian.");
            var meat = await Save("The user doesn't eat meat.");
            var portland = await Save("The user lives in Portland.");
            await Task.Delay(20, cancellation);
            var seattle = await Save("The user moved to Seattle.");
            var sister = await Save("The user's sister is called Ana.");
            var fixtureWords = new Dictionary<Guid, string>
            {
                [vegetarian.Id] = "diet, food, meat, cooking, meals, eating",
                [meat.Id] = "diet, food, vegetarian, cooking, meals",
                [portland.Id] = "home, city, address, location, residence",
                [seattle.Id] = "home, city, address, location, relocation",
                [sister.Id] = "family, sibling, relatives"
            };

            var before = await Recall("What should I cook tonight?");
            Step("without search words, \"What should I cook tonight?\" doesn't find \"The user is vegetarian.\"",
                before.All(hit => hit.Fact.Id != vegetarian.Id), new { results = before.Count });

            var snapshot = await store.SnapshotAsync(cancellation);
            var plan = MemoryUpkeep.Plan(snapshot, DateTimeOffset.UtcNow, null);
            Step("a pass asks for search words for every fact without them (newest first) and groups nothing yet",
                plan.NeedWords.Count == 5 && plan.Groups.Count == 0, new { listed = plan.Facts.Count, needWords = plan.NeedWords.Count, groups = plan.Groups.Count });
            var listed = MemoryUpkeep.Text(plan);
            Step("the pool member's list numbers each fact with its date and marks the facts that need words",
                listed.Contains("Facts that need search words:", StringComparison.Ordinal) && listed.Contains("needs words", StringComparison.Ordinal),
                new { lines = listed.Split('\n').Length });
            var answer = string.Join('\n', Enumerable.Range(1, plan.Facts.Count).Select(n => $"WORDS {n}: {fixtureWords[plan[n].Id]}"));
            var parsed = MemoryUpkeep.Parse(answer, plan);
            var worded = await store.TidyAsync(MemoryUpkeep.Request(plan, parsed), cancellation);
            var withWords = await Find(vegetarian.Id);
            Step("the WORDS lines (FIXTURE - NOT AI) give each fact hidden search words without changing when it last changed",
                worded.Worded == 5 && withWords?.SearchWords == fixtureWords[vegetarian.Id] && withWords.UpdatedAtUtc == vegetarian.UpdatedAtUtc,
                new { worded.Worded, revision = withWords?.Revision });

            var after = await Recall("What should I cook tonight?");
            Step("with search words, \"What should I cook tonight?\" finds \"The user is vegetarian.\" (document expansion)",
                after.Any(hit => hit.Fact.Id == vegetarian.Id), new { first = after.FirstOrDefault()?.Fact.Content, results = after.Count });

            snapshot = await store.SnapshotAsync(cancellation);
            plan = MemoryUpkeep.Plan(snapshot, DateTimeOffset.UtcNow, null);
            int Number(MemoryFact fact) => plan.Facts.Select((f, i) => (f, i)).FirstOrDefault(p => p.f.Id == fact.Id).i + 1;
            var groupsFound = plan.Groups.Any(g => g.Contains(Number(vegetarian)) && g.Contains(Number(meat))) &&
                plan.Groups.Any(g => g.Contains(Number(portland)) && g.Contains(Number(seattle)));
            Step("with search words, the next pass groups \"vegetarian\" with \"doesn't eat meat\" and \"lives in Portland\" with \"moved to Seattle\"",
                groupsFound && plan.NeedWords.Count == 0, new { groups = plan.Groups.Count, listed = plan.Facts.Count });

            var refused = MemoryUpkeep.Parse($"SAME {Number(vegetarian)} {Number(portland)}: Mixed.\nOUTDATED {Number(seattle)} BY {Number(portland)}\nSAME 1 99: Nope.", plan);
            Step("lines across groups, about unknown numbers or calling the newer fact outdated by the older are left out",
                refused.IsEmpty, new { same = refused.Same.Count, outdated = refused.Outdated.Count });

            answer = $"SAME {Number(vegetarian)} {Number(meat)}: The user is vegetarian and doesn't eat meat.\n" +
                $"OUTDATED {Number(portland)} BY {Number(seattle)}";
            parsed = MemoryUpkeep.Parse(answer, plan);
            var tidied = await store.TidyAsync(MemoryUpkeep.Request(plan, parsed), cancellation);
            var merged = await Find(vegetarian.Id);
            var old = await Find(portland.Id);
            Step("SAME (FIXTURE - NOT AI) merges the duplicates into the first fact, which keeps the words of both",
                tidied.Merged == 1 && await Find(meat.Id) is null && merged?.Content == "The user is vegetarian and doesn't eat meat." &&
                merged.SearchWords?.Contains("vegetarian", StringComparison.Ordinal) == true,
                new { tidied.Merged, words = merged?.SearchWords?.Split(", ").Length });
            Step("OUTDATED (FIXTURE - NOT AI) keeps \"lives in Portland\" as history, replaced by \"moved to Seattle\" when that was learned",
                tidied.Retired == 1 && old is { IsHistory: true } && old.ReplacedBy == seattle.Id && old.ReplacedAtUtc == seattle.UpdatedAtUtc,
                new { tidied.Retired, replacedAt = old?.ReplacedAtUtc });
            var home = await Recall("Where does the user live, which city?");
            Step("a replaced fact is never recalled as current; the newer one is",
                home.All(hit => hit.Fact.Id != portland.Id) && home.Any(hit => hit.Fact.Id == seattle.Id),
                new { results = home.Select(hit => hit.Fact.Content) });
            var live = (await store.SnapshotAsync(cancellation)).Live(DateTimeOffset.UtcNow).ToArray();
            Step("a snapshot's current facts (what recall fills with and the memory brief reads) leave history out",
                live.All(f => !f.IsHistory) && live.Length == 3, new { current = live.Length });

            var current = (await Find(seattle.Id))!;
            await store.EditAsync(new()
            {
                Id = current.Id, ExpectedRevision = current.Revision, Content = "The user lives in Tacoma.",
                Provenance = MemoryProvenance.Conversation(Guid.NewGuid(), DateTimeOffset.UtcNow), Retention = current.Retention,
                VoiceId = current.VoiceId, KeepHistory = true
            }, cancellation);
            var facts = (await store.InspectAsync(cancellation)).Facts;
            var earlier = facts.Where(f => f.ReplacedBy == seattle.Id).ToArray();
            var corrected = facts.Single(f => f.Id == seattle.Id);
            Step("a correction (remembering's UPDATE, manage_memories update) keeps the earlier version as history with the date it stopped being true, and drops the old search words",
                earlier.Any(f => f.Content == "The user moved to Seattle." && f.ReplacedAtUtc == corrected.UpdatedAtUtc) &&
                corrected.SearchWords is null && corrected.Content == "The user lives in Tacoma.",
                new { earlierVersions = earlier.Length });

            var json = MemoryFactJson.Write(earlier.First(f => f.Content == "The user moved to Seattle."));
            var plain = MemoryFactJson.Write(facts.Single(f => f.Id == sister.Id) with { SearchWords = null });
            Step("a fact with the new fields travels to other computers as JSON with them; one without them is written exactly as before",
                json.Contains("\"replaced_at_utc\"", StringComparison.Ordinal) && json.Contains("\"replaced_by\"", StringComparison.Ordinal) &&
                MemoryFactJson.Read(json).IsHistory && !plain.Contains("search_words", StringComparison.Ordinal) &&
                !plain.Contains("replaced", StringComparison.Ordinal),
                new { withFields = json.Length, without = plain.Length });

            await store.DeleteAsync(new() { Id = corrected.Id, ExpectedRevision = corrected.Revision, ConsentId = Guid.NewGuid() }, cancellation);
            facts = (await store.InspectAsync(cancellation)).Facts;
            Step("forgetting a fact forgets its earlier versions too, so nothing of it is left",
                facts.All(f => f.ReplacedBy != seattle.Id && f.Id != portland.Id), new { left = facts.Count });

            Step("search words from a model line are cleaned up: lower case, no repeats, at most 16",
                MemorySearchWords.Normalize("Diet, FOOD, diet; \"cooking\", , meals.") == "diet, food, cooking, meals" &&
                MemorySearchWords.Normalize(string.Join(", ", Enumerable.Range(0, 30).Select(i => "word" + i)))!.Split(", ").Length == 16,
                new { });
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return new
        {
            passed = failures.Count == 0,
            failures,
            steps,
            note = "Synthetic facts in a disposable store with the production memory code (Martlet.Memory) and the desktop's own " +
                "upkeep (MemoryUpkeep). The pool member's answers are fixtures (NOT AI). It never opens your memory; a real Thinking " +
                "pool member tidying a real store is NOT RUN here."
        };
    }
}
