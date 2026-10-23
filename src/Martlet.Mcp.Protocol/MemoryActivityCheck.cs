using System.IO;
using Martlet.Memory;

namespace Martlet.Mcp;

/// <summary>memory_activity_check: memories kept for the game or other activity they come from (docs/MEMORY.md, "Memories for
/// a game"). It rehearses the production store and recall filter (Martlet.Memory: <see cref="MemoryActivity"/>,
/// <see cref="MemoryActivityFilter"/>, the store's search and a snapshot's) on synthetic facts in a disposable folder: a fact made
/// while playing Palworld is recalled while playing Palworld or when the words name it, never in another game or outside games;
/// a fact for any game in every game; a fact for everywhere always. It never opens the owner's memory. Nothing leaves this PC.</summary>
internal static class MemoryActivityCheck
{
    private const string Palworld = "gaming/Palworld";
    private const string EldenRing = "gaming/ELDEN RING";

    private static readonly (string Key, string Content, string? Activity)[] SampleFacts =
    [
        ("chillet", "In Palworld, the user's favorite pal is Chillet.", Palworld),
        ("base", "In Palworld, the user's base is by the lake near the starting area.", Palworld),
        ("mage", "In Elden Ring, the user's favorite weapon is the moonveil katana.", EldenRing),
        ("coop", "The user likes co-op games with friends on weekends.", MemoryActivity.Gaming),
        ("cat", "The user's cat is called Miso and is their favorite.", null)
    ];

    internal static async Task<object> RunAsync(string? activity, string? query, CancellationToken cancellation)
    {
        var steps = new List<object>();
        var failures = new List<string>();
        void Step(string name, bool passed, object detail)
        {
            steps.Add(new { name, passed, detail });
            if (!passed) failures.Add(name);
        }
        object? asked = null;
        var root = Path.Combine(Path.GetTempPath(), "martlet-memory-activity-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            using var store = Open(root);
            var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
            foreach (var (key, content, where) in SampleFacts)
                ids[key] = (await store.SaveAsync(new()
                {
                    Content = content,
                    Provenance = MemoryProvenance.Conversation(Guid.NewGuid(), DateTimeOffset.UtcNow),
                    Retention = MemoryRetention.UntilDeleted(),
                    Activity = where
                }, cancellation)).Fact.Id;
            string Key(Guid id) => ids.First(pair => pair.Value == id).Key;
            async Task<string[]> Recall(string text, string? now, bool filter = true)
            {
                if (MemoryQuery.TryFromBoundedSource(text, 10) is not { } bounded) return [];
                if (filter) bounded = bounded with { Activity = new(now) };
                return [.. (await store.RetrieveAsync(bounded, cancellation)).Hits.Select(hit => Key(hit.Fact.Id))];
            }

            // The caller's words, against the sample facts as saved.
            if (query is not null)
            {
                var recalled = await Recall(query, activity is null ? null : MemoryActivity.Game(activity) ?? activity);
                asked = new { query, activity = activity is null ? "none" : MemoryActivity.Game(activity) ?? activity, recalled };
            }
            var question = "Which one is my favorite?";
            var inPalworld = await Recall(question, Palworld);
            Step("playing Palworld: its favorite (Chillet) and the cat (for everywhere) are recalled, not another game's facts",
                inPalworld.Contains("chillet") && inPalworld.Contains("cat") && !inPalworld.Contains("mage"), new { recalled = inPalworld });
            var inElden = await Recall(question, EldenRing);
            Step("playing another game (Elden Ring): Palworld's favorite is never recalled; the cat still is",
                !inElden.Contains("chillet") && inElden.Contains("cat"), new { recalled = inElden });
            var outside = await Recall(question, null);
            Step("no game played: only facts for everywhere are recalled", outside.SequenceEqual(["cat"]), new { recalled = outside });
            var named = await Recall("What is my favorite Palworld pal?", null);
            Step("asking about Palworld by name recalls its facts while no game is played",
                named.Contains("chillet") && !named.Contains("mage"), new { recalled = named });
            var namedElsewhere = await Recall("What did I build in palworld?", EldenRing);
            Step("naming Palworld while playing another game recalls Palworld's facts too (case and spacing aside)",
                namedElsewhere.Contains("base"), new { recalled = namedElsewhere });
            var coop = await Recall("Do I like co-op games?", "gaming/Stardew Valley");
            var coopOutside = await Recall("Do I like co-op games?", null);
            Step("a fact for any game is recalled in every game, not outside games",
                coop.Contains("coop") && !coopOutside.Contains("coop"), new { inAGame = coop, outside = coopOutside });
            var unfiltered = await Recall(question, null, filter: false);
            Step("a search without an activity filter (the Memory window, manage_memories find) still finds every fact",
                unfiltered.Contains("chillet") && unfiltered.Contains("mage") && unfiltered.Contains("cat"), new { recalled = unfiltered });

            var snapshot = await store.SnapshotAsync(cancellation);
            var bounded = MemoryQuery.TryFromBoundedSource(question, 10)! with { Activity = new(EldenRing) };
            var fromStore = (await store.RetrieveAsync(bounded, cancellation)).Hits.Select(h => h.Fact.Id).ToArray();
            var fromSnapshot = snapshot.Search(bounded, DateTimeOffset.UtcNow, cancellation).Select(h => h.Fact.Id).ToArray();
            Step("another memory space's snapshot leaves out the same facts as the store", fromStore.SequenceEqual(fromSnapshot),
                new { store = fromStore.Select(Key), snapshot = fromSnapshot.Select(Key) });

            var facts = (await store.InspectAsync(cancellation)).Facts;
            var chillet = facts.Single(f => f.Id == ids["chillet"]);
            var cat = facts.Single(f => f.Id == ids["cat"]);
            var chilletJson = MemoryFactJson.Write(chillet);
            var catJson = MemoryFactJson.Write(cat);
            Step("a game's fact travels with its activity; a fact for everywhere is written exactly as before (no activity field)",
                chilletJson.Contains("\"activity\":\"gaming/Palworld\"", StringComparison.Ordinal) &&
                !catJson.Contains("\"activity\"", StringComparison.Ordinal) && MemoryFactJson.Read(chilletJson).Activity == Palworld,
                new { gameFactHasActivity = true });

            var edited = await store.EditAsync(new()
            {
                Id = chillet.Id, ExpectedRevision = chillet.Revision, Content = "In Palworld, the user's favorite pal is Anubis.",
                Provenance = MemoryProvenance.Conversation(Guid.NewGuid(), DateTimeOffset.UtcNow),
                Retention = chillet.Retention, VoiceId = null, KeepHistory = true
            }, cancellation);
            var moved = await store.EditAsync(new()
            {
                Id = cat.Id, ExpectedRevision = cat.Revision, Content = cat.Content,
                Provenance = MemoryProvenance.UserEntry(Guid.NewGuid(), DateTimeOffset.UtcNow),
                Retention = cat.Retention, VoiceId = null, SetActivity = true, Activity = EldenRing
            }, cancellation);
            Step("a correction keeps where the fact matters; choosing it on purpose moves the fact",
                edited.Fact.Activity == Palworld && moved.Fact.Activity == EldenRing,
                new { corrected = edited.Fact.Activity, moved = moved.Fact.Activity });

            var refused = 0;
            foreach (var bad in new[] { "Gaming/Palworld", "gaming/ Palworld", "gaming/", "game play", "gaming/\nx" })
            {
                try
                {
                    await store.SaveAsync(new()
                    {
                        Content = "A fact with a broken activity.", Provenance = MemoryProvenance.UserEntry(Guid.NewGuid(), DateTimeOffset.UtcNow),
                        Retention = MemoryRetention.UntilDeleted(), Activity = bad
                    }, cancellation);
                }
                catch (MemoryException) { refused++; }
            }
            Step("an activity of the wrong form is refused (a lowercase kind, then '/' and a trimmed one-line name)", refused == 5,
                new { refused, of = 5 });
            // Prompts for a game (docs/MEMORY.md, "Memories for a game"): the While you play prompt and lorebook entries for the game.
            var lore = Martlet.Core.Lorebooks.LorebookLibrary.Create() with
            {
                ScanDepth = 2,
                Books = [new Martlet.Core.Lorebooks.Lorebook
                {
                    Id = Guid.NewGuid(), Name = "Games",
                    Entries = [new() { Uid = 0, Content = "Pals can be caught with spheres.", Keys = ["Palworld"] }]
                }]
            };
            var quiet = Martlet.Core.Lorebooks.LorebookScanner.Scan(lore, new("What should I do next?", []), _ => 0).Included.Count;
            var played = Martlet.Core.Lorebooks.LorebookScanner.Scan(lore, new("What should I do next?", [], Activity: "Palworld"), _ => 0).Included;
            Step("a lorebook entry keyed by the game played now joins in, though the words don't name it",
                quiet == 0 && played.Count == 1 && played[0].Trigger == "Palworld (played now)", new { withoutGame = quiet, playing = played.Select(h => h.Trigger) });
            var whilePlaying = Martlet.Core.Settings.SkillSettings.Guidance(null, Martlet.Core.Settings.SkillCatalog.WhilePlaying, ("game", "Palworld"));
            Step("the While you play skill (Companion › Skills) names the game played now",
                whilePlaying?.StartsWith("The user is playing Palworld now", StringComparison.Ordinal) == true, new { prompt = whilePlaying });

            Step("game names compare without regard to case or spacing, and are made from what the game watch saw",
                MemoryActivity.Same("gaming/ELDEN RING", "gaming/Elden  ring") && MemoryActivity.Game("  Palworld \t") == Palworld &&
                MemoryActivity.Describe(Palworld) == "while playing Palworld" && MemoryActivity.Describe(null) == "everywhere",
                new { describe = MemoryActivity.Describe(Palworld) });

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
            asked,
            sampleFacts = SampleFacts.Select(f => new { key = f.Key, fact = f.Content, where = MemoryActivity.Describe(f.Activity) }),
            note = "Synthetic facts in a disposable store with the production memory code (Martlet.Memory). It never opens your " +
                "memory. Which game is played now (the game watch), remembering's REMEMBER HERE/GAMES/EVERYWHERE lines and the " +
                "talk window's recall are checked by the desktop's tests (MemoryActivityTests)."
        };
    }

    private static MemoryStore Open(string directory)
    {
        var preview = MemoryStoreActivationPreview.Create(directory);
        return MemoryStore.Open(preview, preview.Authorize(MemoryConsentDecision.Allow));
    }
}
