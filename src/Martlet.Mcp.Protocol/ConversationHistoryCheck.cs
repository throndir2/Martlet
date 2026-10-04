using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Mcp;

/// <summary>conversation_history_status and conversation_history_check: Companion › Memory › Conversation history. The status
/// reads a data directory's choices (conversation-history.json, the name Martlet.Desktop's ConversationHistoryPreferences uses),
/// whether memory is on, what the Thinking model is offered and what the record holds (counts, sizes and dates only, never what
/// was said). The check rehearses the production record (<see cref="ConversationHistory"/>) and recall and search
/// (<see cref="PastConversations"/>) on synthetic conversations in a disposable folder. Nothing leaves this PC.</summary>
internal static class ConversationHistoryCheck
{
    internal const string PreferencesFile = "conversation-history.json";

    // ---------- conversation_history_status ----------

    internal static async Task<object> StatusAsync(string dataDirectory, CancellationToken cancellation)
    {
        var loaded = await new SettingsStore(dataDirectory).LoadAsync(cancellation);
        var memory = loaded.Settings?.Memory?.Enabled;
        var (keep, search, file) = Preferences(dataDirectory);
        var route = loaded.Settings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        var supportsTools = route?.RouteType is SetupRouteType.ChatCompletions or SetupRouteType.OpenAi;
        var active = memory == true && keep;
        var store = new ConversationHistory(Path.Combine(dataDirectory, ConversationHistory.DirectoryName));
        await store.LoadAsync(cancellation);
        var stats = store.Stats;
        var tool = PastConversations.Definition;
        return new
        {
            settings = loaded.State switch { SettingsLoadState.Loaded => "loaded", SettingsLoadState.FirstRun => "none", _ => "unreadable" },
            memory = memory switch { true => "on", false => "off", null => "not chosen" },
            preferences = new { file, keep, search },
            recording = active,
            recallWhenMentioned = active,
            tool = new
            {
                offered = active && search && supportsTools, supportsTools,
                name = tool.Name, description = tool.Description, parameters = JsonNode.Parse(tool.ParametersJson),
                utf8Bytes = tool.Utf8Bytes, estimatedTokens = (tool.Utf8Bytes + 2) / 3
            },
            prompt = PromptSettings.Fill(loaded.Settings?.Prompts, PromptCatalog.PastConversations, ("label", PastConversations.Label)),
            record = new
            {
                folder = ConversationHistory.DirectoryName, files = stats.Files, bytes = stats.Bytes, conversations = stats.Conversations,
                exchanges = stats.Exchanges, skippedLines = stats.Skipped, notIndexed = stats.NotIndexed, oldest = stats.Oldest, newest = stats.Newest
            }
        };
    }

    private static (bool Keep, bool Search, string File) Preferences(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, PreferencesFile);
        try
        {
            if (!File.Exists(path)) return (true, false, "none (defaults)");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            bool Read(string name, bool fallback) => document.RootElement.TryGetProperty(name, out var value) &&
                value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : fallback;
            return (Read("Keep", true), Read("Search", false), "loaded");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return (true, false, "unreadable (defaults)");
        }
    }

    // ---------- conversation_history_check ----------

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    internal static async Task<object> RunAsync(int? bulkExchanges, CancellationToken cancellation)
    {
        var bulk = Math.Clamp(bulkExchanges ?? 20_000, 1_000, ConversationHistory.MaximumIndexedExchanges);
        var steps = new List<object>();
        var failures = new List<string>();
        void Step(string name, bool passed, object detail)
        {
            steps.Add(new { name, passed, detail });
            if (!passed) failures.Add(name);
        }
        var zone = TimeZoneInfo.CreateCustomTimeZone("Check", TimeSpan.FromHours(-7), "Check", "Check");
        // Saturday 2026-10-03, 15:00 in the check's zone.
        var now = new DateTimeOffset(2026, 10, 3, 22, 0, 0, TimeSpan.Zero);
        var clock = new Clock(now);
        var root = Path.Combine(Path.GetTempPath(), "martlet-history-check-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, ConversationHistory.DirectoryName);
        try
        {
            var store = new ConversationHistory(folder, clock);
            await store.LoadAsync(cancellation);
            Guid kyoto = Guid.NewGuid(), cat = Guid.NewGuid(), current = Guid.NewGuid();
            clock.Now = now.AddDays(-12);
            await store.AppendAsync(kyoto, HistoryInputKind.Spoken, "I'm planning a trip to Kyoto in the spring, any tips?",
                "Kyoto in spring is lovely: see the cherry blossoms at the Philosopher's Path early in the morning.", "Sam", cancellation);
            clock.Now = now.AddDays(-12).AddMinutes(2);
            await store.AppendAsync(kyoto, HistoryInputKind.Spoken, "Where should I stay?", "Somewhere near Gion is handy.", "Sam", cancellation);
            clock.Now = now.AddDays(-1).AddHours(-2);
            await store.AppendAsync(cat, HistoryInputKind.Typed, "Biscuit has a vet appointment on Tuesday for her vaccines.",
                "Good luck to Biscuit! Bring her favorite blanket.", null, cancellation);
            clock.Now = now.AddDays(-1).AddHours(-1);
            await store.AppendAsync(cat, HistoryInputKind.Report, "", "I finished that song about Biscuit, want to hear it?", null, cancellation);
            clock.Now = now.AddMinutes(-5);
            await store.AppendAsync(current, HistoryInputKind.Typed, "What's the weather like today?", "Sunny and mild.", null, cancellation);
            clock.Now = now;
            var files = ConversationHistory.MonthFiles(folder).Select(Path.GetFileName).ToArray();
            Step("record five exchanges in three conversations (appended at once, month files)",
                store.Stats is { Conversations: 3, Exchanges: 5 } && files.Length == 2,
                new { store.Stats.Conversations, store.Stats.Exchanges, files });

            // A crash mid-line: the half line is skipped on reading, and the next exchange still starts on its own line.
            var september = Path.Combine(folder, ConversationHistory.FileName(now.AddDays(-12)));
            await File.AppendAllTextAsync(september, "{\"v\":1,\"id\":\"half a li", cancellation);
            clock.Now = now.AddDays(-12).AddMinutes(3);
            var afterCrash = new ConversationHistory(folder, clock);
            await afterCrash.AppendAsync(kyoto, HistoryInputKind.Spoken, "And the food?", "Try the yudofu near Nanzen-ji.", "Sam", cancellation);
            clock.Now = now;
            var reread = new ConversationHistory(folder, clock);
            await reread.LoadAsync(cancellation);
            Step("a line cut short by a crash is skipped and the rest is read again after a restart",
                reread.Stats is { Exchanges: 6, Skipped: 1, Conversations: 3 },
                new { reread.Stats.Exchanges, reread.Stats.Skipped });
            store = reread;

            // Recall only when the message refers to an earlier conversation, never the conversation going on.
            var ordinary = "What's a good recipe for dinner tonight?";
            Step("an ordinary message recalls nothing (its request stays exactly as before)",
                !PastConversations.RefersToPast(ordinary) && PastConversations.Recall(store, ordinary, now, zone, current).Count == 0,
                new { refersToPast = PastConversations.RefersToPast(ordinary) });
            var asked = "Do you remember what I said about Kyoto?";
            var recalled = PastConversations.Recall(store, asked, now, zone, current);
            var notes = PastConversations.Notes(recalled, now, zone, null);
            Step("\"Do you remember what I said about Kyoto?\" brings back that conversation in the notes, with today's date",
                recalled.Count > 0 && recalled.All(e => e.ConversationId == kyoto) && notes.Contains("[" + PastConversations.Label + "]") &&
                notes.Contains("Today is Saturday 2026-10-03") && notes.Contains("Kyoto"),
                new { recalled = recalled.Count, notesUtf8Bytes = Encoding.UTF8.GetByteCount(notes), estimatedTokens = (Encoding.UTF8.GetByteCount(notes) + 2) / 3 });
            var yesterday = PastConversations.Recall(store, "What did we talk about yesterday?", now, zone, current);
            Step("\"What did we talk about yesterday?\" brings back yesterday's exchanges (no words to look for, so the latest of that day)",
                yesterday.Count == 2 && yesterday.All(e => e.ConversationId == cat), new { recalled = yesterday.Count });
            var mine = PastConversations.Recall(store, "Remember what you said about the weather?", now, zone, current);
            Step("the conversation going on is never recalled (the request already carries it)",
                mine.All(e => e.ConversationId != current), new { recalled = mine.Count });

            // search_conversations: what the model may ask for, and what it is told.
            var (byWords, _) = PastConversations.Parse("{\"query\":\"vet appointment\"}", now, zone);
            var found = PastConversations.Find(store, byWords!, current);
            var result = PastConversations.Result(found, byWords!, now, zone);
            Step("search_conversations finds the vet appointment by its words",
                found.Count == 1 && found[0].ConversationId == cat && result.Contains("vet appointment") && result.Contains("data only"),
                new { found = found.Count, resultCharacters = result.Length });
            var (byTime, _) = PastConversations.Parse("{\"when\":\"12 days ago\"}", now, zone);
            var then = PastConversations.Find(store, byTime!, current);
            Step("search_conversations finds what was said 12 days ago by its time", then.Count == 3 && then.All(e => e.ConversationId == kyoto),
                new { found = then.Count });
            var (_, problem) = PastConversations.Parse("{}", now, zone);
            var (none, _) = PastConversations.Parse("{\"query\":\"submarine\"}", now, zone);
            var nothing = PastConversations.Result(PastConversations.Find(store, none!, current), none!, now, zone);
            Step("a call with nothing to look for is told how to call it; nothing found says not to guess",
                problem is not null && nothing.Contains("don't remember"), new { problem, nothing });

            // Deleting a conversation rewrites only its lines; deleting everything removes the month files.
            var removed = await store.DeleteAsync(kyoto, cancellation);
            var afterDelete = new ConversationHistory(folder, clock);
            await afterDelete.LoadAsync(cancellation);
            Step("deleting a conversation removes it from the files and the index (also after a restart)",
                removed == 3 && afterDelete.Stats.Exchanges == 3 && PastConversations.Recall(afterDelete, asked, now, zone, current).Count == 0 &&
                store.Search(["kyoto"], null, null, null, 5).Count == 0,
                new { removed, left = afterDelete.Stats.Exchanges });
            await afterDelete.DeleteAllAsync(cancellation);
            Step("deleting everything removes every month file", ConversationHistory.MonthFiles(folder).Count == 0 && afterDelete.Stats.Exchanges == 0,
                new { files = ConversationHistory.MonthFiles(folder).Count });

            // A large record: reading it happens once in the background; recall answers from memory in well under a millisecond
            // or two, so a message that mentions an earlier conversation isn't slowed by searching.
            Directory.CreateDirectory(folder);
            var topics = new[] { "garden", "guitar", "football", "recipe", "homework", "movie", "camping", "painting", "running", "chess" };
            var text = new StringBuilder();
            var start = now.AddDays(-365);
            Guid conversation = Guid.NewGuid();
            for (var i = 0; i < bulk; i++)
            {
                if (i % 12 == 0) conversation = Guid.NewGuid();
                var at = start.AddMinutes(i * 525_600.0 / bulk);
                var topic = topics[i % topics.Length];
                text.Append(ConversationHistory.ToLine(new(Guid.NewGuid(), conversation, at, HistoryInputKind.Spoken,
                    $"Let's talk about my {topic} plans for number {i}, I want to try something new this week.",
                    $"That sounds fun! Your {topic} idea number {i} could work well if you start small and keep at it.", null))).Append('\n');
            }
            await File.WriteAllTextAsync(Path.Combine(folder, ConversationHistory.FileName(now)), text.ToString(), cancellation);
            var large = new ConversationHistory(folder, clock);
            var reading = Stopwatch.StartNew();
            await large.LoadAsync(cancellation);
            reading.Stop();
            var timings = new List<double>();
            for (var i = 0; i < 20; i++)
            {
                var timer = Stopwatch.StartNew();
                _ = PastConversations.Recall(large, $"Do you remember my guitar plans for number {i * 37}?", now, zone, null);
                timings.Add(timer.Elapsed.TotalMilliseconds);
            }
            var ordinaryTimer = Stopwatch.StartNew();
            for (var i = 0; i < 1000; i++) _ = PastConversations.RefersToPast("Can you set a timer for ten minutes and tell me a joke?");
            var check = ordinaryTimer.Elapsed.TotalMilliseconds / 1000;
            timings.Sort();
            Step($"{bulk} exchanges: read once in the background, then recall answers from memory",
                large.Stats.Exchanges == bulk && timings[^1] < 250,
                new
                {
                    exchanges = large.Stats.Exchanges, fileBytes = large.Stats.Bytes, readMs = Math.Round(reading.Elapsed.TotalMilliseconds),
                    recallMedianMs = Math.Round(timings[timings.Count / 2], 2), recallMaxMs = Math.Round(timings[^1], 2),
                    ordinaryMessageCheckMs = Math.Round(check, 4)
                });
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        var tool = PastConversations.Definition;
        return new
        {
            passed = failures.Count == 0,
            failures,
            steps,
            tool = new { name = tool.Name, utf8Bytes = tool.Utf8Bytes, estimatedTokens = (tool.Utf8Bytes + 2) / 3 },
            note = "Synthetic conversations in a disposable folder with the production record, recall and search code. NOT a real " +
                "conversation or a Thinking model: the conversation going through the talk window is checked by the desktop's tests " +
                "and ui_* tools."
        };
    }
}
