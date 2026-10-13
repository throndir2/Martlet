using System.IO;
using Martlet.Conversation;
using Martlet.Core.Settings;
using Martlet.Core.Sync;
using Martlet.Mcp.Shared;
using Martlet.Memory;
using Martlet.Providers;

namespace Martlet.Mcp;

/// <summary>memory_brief_status and memory_brief_check (docs/MEMORY.md, "The memory brief"). The status reads memory-brief.json
/// of a data directory: on or off, whether a brief is written, by whom, when, from how many facts and people, and its length;
/// never the brief. The check rehearses the desktop's production brief (<see cref="MemoryBriefService"/>, written through the
/// production helper-job router <see cref="HelperJobs"/> with a fixture Thinking pool member, FIXTURE - NOT AI) on a disposable
/// memory store, and shares the brief between two simulated computers with the production shared-settings section.</summary>
internal static class MemoryBriefCheck
{
    internal static object Status(string dataDirectory)
    {
        var limits = new
        {
            maximumCharacters = MemoryBriefFile.MaximumCharacters, minimumFacts = MemoryBriefWriter.MinimumFacts,
            newFactsToWriteAgain = MemoryBriefWriter.NewFactsToWriteAgain, triesPerHour = MemoryBriefService.PerHour,
            gapMinutes = MemoryBriefService.Gap.TotalMinutes, gapAfterLossMinutes = MemoryBriefService.GapAfterLoss.TotalMinutes
        };
        if (!File.Exists(MemoryBriefFile.PathOf(dataDirectory)))
            return new { state = "none", on = true, written = false, why = "No memory brief was written with this data directory yet.", limits };
        if (MemoryBriefFile.TryLoad(dataDirectory) is not { } brief)
            return new { state = "unreadable", why = $"{MemoryBriefFile.FileName} can't be read or was written by a newer Martlet.", limits };
        return new
        {
            state = "loaded", on = brief.On, written = brief.Text is not null, by = brief.By, writtenAt = brief.WrittenAt,
            characters = brief.Text?.Length ?? 0, lines = brief.Text?.Split('\n').Length ?? 0, facts = brief.Facts,
            people = brief.Basis.Count(entry => entry.StartsWith('~')), limits
        };
    }

    internal static async Task<object> RunAsync(CancellationToken cancellation)
    {
        var root = Path.Combine(Path.GetTempPath(), "martlet-memory-brief-check-" + Guid.NewGuid().ToString("N"));
        var steps = new List<object>();
        void Step(string name, bool passed, object? detail = null) => steps.Add(new { step = name, passed, detail });
        var clock = new Clock(DateTimeOffset.UtcNow);
        var dataA = Path.Combine(root, "computer-a");
        var dataB = Path.Combine(root, "computer-b");
        Directory.CreateDirectory(dataA);
        Directory.CreateDirectory(dataB);
        var people = new Dictionary<string, string>(StringComparer.Ordinal) { ["voice-sam"] = "Sam", ["voice-alex"] = "Alex" };
        var pool = new FixturePool();
        var helpers = new HelperJobs(() => pool, () => false, dataA);
        var inputs = new List<string>();
        MemoryStore? store = null;
        try
        {
            var preview = MemoryStoreActivationPreview.Create(Path.Combine(dataA, "memory"));
            store = MemoryStore.Open(preview, preview.Authorize(MemoryConsentDecision.Allow), cancellation);
            async Task<MemoryFact> Remember(string content, string? voice) => (await store.SaveAsync(new SaveFactRequest
            {
                Content = content, Provenance = MemoryProvenance.Conversation(Guid.NewGuid(), DateTimeOffset.UtcNow),
                Retention = MemoryRetention.UntilDeleted(), VoiceId = voice
            }, cancellation)).Fact;
            async Task<MemoryBriefFacts?> Read(CancellationToken token) =>
                new((await store.InspectAsync(token)).Facts, people, ["Sam"], null);
            async Task<(string?, string?)> Write(string instructions, string text, CancellationToken token)
            {
                inputs.Add(text);
                var (answer, failure, _) = await helpers.RunAsync(HelperJobKind.MemoryBrief, MemoryBriefWriter.Purpose, HelperCapability.Text,
                    () => new BoundedTextInput(text, instructions), _ => Task.FromResult<(string?, string?)>((null, "no pool member (fixture)")), token);
                return (answer, failure);
            }
            using var brief = new MemoryBriefService(dataA, Read, Write, () => false, clock, start: false);

            await Remember("Likes green tea in the morning.", "voice-sam");
            await Remember("Plays the cello in an orchestra.", "voice-alex");
            var first = await brief.CheckAsync(false, cancellation);
            Step("with two facts no brief is written yet", first == "too few facts" && brief.Notes([], null) is null, new { outcome = first });

            var dog = await Remember("Has a dog named Rex.", null);
            var written = await brief.CheckAsync(false, cancellation);
            var route = helpers.Last.FirstOrDefault(r => r.Kind == HelperJobKind.MemoryBrief);
            var saved = MemoryBriefFile.Load(dataA);
            Step("a third fact: a Thinking pool member writes the brief at the lowest priority, from each fact with whose it is",
                written == "written" && route is { Pooled: true } && saved is { By: MemoryBriefFile.ByThinking, Facts: 3 } &&
                inputs[^1].Contains("- [Sam] Likes green tea", StringComparison.Ordinal) &&
                inputs[^1].Contains("- [Alex] Plays the cello", StringComparison.Ordinal) && inputs[^1].Contains("- Has a dog", StringComparison.Ordinal) &&
                HelperJobs.PriorityOf(HelperJobKind.MemoryBrief) == HelperJobPriority.Low,
                new
                {
                    outcome = written, route = route?.Route, member = route?.Member, kind = HelperJobs.Name(HelperJobKind.MemoryBrief),
                    priority = HelperJobs.PriorityOf(HelperJobKind.MemoryBrief).ToString(), status = Status(dataA)
                });

            var notes = brief.Notes([], null);
            var carried = notes is null ? null : brief.Notes(
                [new(TextHistoryRole.User, "Hi!\n\n[MARTLET_NOTES]\n" + notes + "\n[/MARTLET_NOTES]"), new(TextHistoryRole.Assistant, "Hello!")], null);
            Step("the first message of a conversation carries it in its notes, and the next messages don't",
                notes is not null && notes.StartsWith(MemoryBriefWriter.Label, StringComparison.Ordinal) && carried is null,
                new { characters = notes?.Length ?? 0 });

            var current = (await store.InspectAsync(cancellation)).Facts.Single(f => f.Id == dog.Id);
            await store.DeleteAsync(new() { Id = current.Id, ExpectedRevision = current.Revision, ConsentId = Guid.NewGuid() }, cancellation);
            var held = await brief.CheckAsync(false, cancellation);
            Step("a forgotten fact holds the brief back until it is written again", held == "waiting" && !brief.Usable && brief.Notes([], null) is null,
                new { outcome = held });

            await Remember("Is learning Spanish.", "voice-sam");
            clock.Now += MemoryBriefService.GapAfterLoss;
            var again = await brief.CheckAsync(false, cancellation);
            Step("a minute later it is written again without the forgotten fact and goes with messages again",
                again == "written" && brief.Usable && !inputs[^1].Contains("Rex", StringComparison.Ordinal) && brief.Notes([], null) is not null,
                new { outcome = again });

            var own = brief.SaveOwn("I'm Sam. I love green tea and I'm learning Spanish.");
            await Remember("Works as a nurse.", "voice-sam");
            await Remember("Lives near the sea.", "voice-sam");
            await Remember("Has two sisters.", "voice-sam");
            clock.Now += MemoryBriefService.Gap;
            var kept = await brief.CheckAsync(false, cancellation);
            Step("your own text stays: Martlet doesn't write over it on its own",
                own is null && kept == "the owner's" && MemoryBriefFile.Load(dataA).Owned &&
                brief.Notes([], null)?.Contains("I love green tea", StringComparison.Ordinal) == true, new { outcome = kept });

            brief.SetOn(false);
            var off = brief.Notes([], null) is null && await brief.CheckAsync(false, cancellation) == "off";
            brief.SetOn(true);
            Step("off: nothing is written or sent", off);

            var rewrite = await brief.WriteNowAsync(cancellation);
            Step("Write it again replaces your text with one written from the memories",
                rewrite == "written" && MemoryBriefFile.Load(dataA).By == MemoryBriefFile.ByThinking, new { outcome = rewrite });

            // Two computers: A's brief reaches B through the shared settings, and B sends it at once.
            using var onB = new MemoryBriefService(dataB, _ => Task.FromResult<MemoryBriefFacts?>(null),
                (_, _, _) => Task.FromResult<(string?, string?)>((null, "never asked")), () => false, clock, start: false);
            var nodeA = new SharedSettingsNode(dataA, "desktop-a", [MemoryBriefFile.Section(dataA)]);
            var nodeB = new SharedSettingsNode(dataB, "desktop-b", [MemoryBriefFile.Section(dataB, onB.Followed)]);
            var now = DateTimeOffset.UtcNow;
            await nodeB.SyncAsync([], true, now, cancellation);
            await nodeA.SyncAsync([nodeB.Document], true, now.AddSeconds(15), cancellation);
            var followed = await nodeB.SyncAsync([nodeA.Document], true, now.AddSeconds(30), cancellation);
            Step("another computer takes the same brief through the shared settings and sends it",
                followed.Applied.Any(c => c.Key == MemoryBriefFile.SharedKey) && MemoryBriefFile.Load(dataB).Write() == MemoryBriefFile.Load(dataA).Write() &&
                onB.Notes([], null) is not null && SettingScopes.IsAccountKey(MemoryBriefFile.SharedKey),
                new { applied = followed.Applied.Count, scope = SettingScopes.Of(MemoryBriefFile.SharedKey) });

            return new
            {
                passed = steps.All(step => (bool)step.GetType().GetProperty("passed")!.GetValue(step)!),
                steps,
                note = "Synthetic facts in a disposable memory store with the production brief, helper-job router and shared-settings " +
                    "section. The brief's text is the fixture pool member's (FIXTURE - NOT AI); no model, network or credentials. It " +
                    "never opens your memory."
            };
        }
        finally
        {
            store?.Dispose();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    // A free text member that answers with a plain profile made of the facts it got (FIXTURE - NOT AI).
    private sealed class FixturePool : IHelperJobPool
    {
        public bool Has(HelperCapability capability) => capability == HelperCapability.Text;

        public Task<HelperPoolAnswer?> TryRunAsync(HelperJob job, CancellationToken token)
        {
            var lines = job.Input.UserText.Split('\n').Where(line => line.StartsWith("- ", StringComparison.Ordinal))
                .Select(line => line[2..line.LastIndexOf(" (", StringComparison.Ordinal)]);
            return Task.FromResult<HelperPoolAnswer?>(new("fixture-text-member", "FIXTURE - NOT AI profile:\n" + string.Join('\n', lines), null));
        }
    }
}
