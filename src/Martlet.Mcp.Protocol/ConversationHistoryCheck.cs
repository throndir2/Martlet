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
        var platforms = new PlatformChanges(Path.Combine(store.Directory, PlatformChanges.FileName)).Status;
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
                exchanges = stats.Exchanges, skippedLines = stats.Skipped, notIndexed = stats.NotIndexed, oldest = stats.Oldest, newest = stats.Newest,
                apps = stats.Apps
            },
            // Deletions and edits waiting for Telegram and Discord (platform-changes.json): counts and one problem line only.
            platformChanges = new
            {
                pending = platforms.Pending, pendingByApp = platforms.PendingByApp, done = platforms.Done, refused = platforms.Refused,
                gaveUp = platforms.GaveUp, lastProblem = platforms.LastProblem
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

    // ---------- messaging apps: sources, single messages, edits and the queue of changes for the apps ----------

    private sealed class FixturePlatform(string app, Clock clock) : IPlatformMessages
    {
        internal List<(DateTimeOffset At, PlatformChange Change)> Made { get; } = [];
        internal int SlowDowns { get; set; } = 1;
        public string App => app;
        public TimeSpan Interval => TimeSpan.FromSeconds(1);

        public Task ApplyAsync(PlatformChange change, CancellationToken token)
        {
            if (SlowDowns-- > 0) throw new PlatformChangeException(PlatformFailure.RateLimited, "Fixture: slow down.", TimeSpan.FromSeconds(3));
            if (change.Message == "gone") throw new PlatformChangeException(PlatformFailure.Refused, "Fixture: unknown message.");
            Made.Add((clock.Now, change));
            return Task.CompletedTask;
        }
    }

    private static async Task AppsAsync(string folder, Clock clock, DateTimeOffset now, TimeZoneInfo zone, Action<string, bool, object> Step,
        CancellationToken cancellation)
    {
        var store = new ConversationHistory(folder, clock);
        await store.LoadAsync(cancellation);
        Guid pc = Guid.NewGuid(), server = Guid.NewGuid(), dm = Guid.NewGuid();
        clock.Now = now.AddHours(-2);
        await store.AppendAsync(pc, HistoryInputKind.Typed, "Remind me about the dentist on Friday.", "I'll remember the dentist on Friday.",
            "Sam", new HistorySource(HistoryApps.Telegram, "42", null, "Sam", ["101"]), null, cancellation);
        var lighthouse = await store.AppendAsync(server, HistoryInputKind.Typed, "Who painted the lighthouse mural?", "Mira did, last summer.",
            "Ana", new HistorySource(HistoryApps.Discord, "900", "800", "#art in Fixture Server", ["5001"], ["5002", "5003"]), null, cancellation);
        clock.Now = now.AddDays(-3);
        var old = await store.AppendAsync(pc, HistoryInputKind.Typed, "What's a good name for a goldfish?", "Bubbles!", "Sam",
            new HistorySource(HistoryApps.Telegram, "42", null, "Sam", ["90"], ["91"]), null, cancellation);
        clock.Now = now.AddHours(-1);
        var direct = await store.AppendAsync(dm, HistoryInputKind.Typed, "Thanks for the help!", "Any time.", "Ana",
            new HistorySource(HistoryApps.Discord, "700", null, "DM with Ana", ["6001"], ["6002"]), null, cancellation);
        await store.AppendAsync(pc, HistoryInputKind.Typed, "Good night.", "Sleep well!", null, cancellation);
        clock.Now = now;
        var telegram = store.FindMessage(HistoryApps.Telegram, "42", "101");
        if (telegram is not null)
            await store.ChangeAsync(telegram.Id, found => found with { Source = found.Source! with { ReplyMessages = ["102"] } }, cancellation);
        var reread = new ConversationHistory(folder, clock);
        await reread.LoadAsync(cancellation);
        var stats = reread.Stats;
        var conversations = reread.Conversations();
        var attached = telegram is null ? null : reread.Exchange(telegram.Id);
        Step("exchanges keep where they happened (Telegram, a Discord server channel and DM, this PC), with the apps' message IDs, after a restart",
            stats is { Exchanges: 5, Conversations: 3 } && stats.Apps?.GetValueOrDefault(HistoryApps.Telegram) == 2 &&
            stats.Apps.GetValueOrDefault(HistoryApps.Discord) == 2 && stats.Apps.GetValueOrDefault(HistoryApps.Pc) == 1 &&
            attached?.Source?.ReplyMessages?.SequenceEqual(["102"]) == true &&
            conversations.Single(c => c.Id == pc).Apps!.Order().SequenceEqual([HistoryApps.Pc, HistoryApps.Telegram]) &&
            conversations.Single(c => c.Id == server).ChatName == "#art in Fixture Server",
            new { stats.Exchanges, apps = stats.Apps, replyIdsAttached = attached?.Source?.ReplyMessages?.Count ?? 0 });

        var recalled = PastConversations.Recall(reread, "Do you remember what I said about the dentist?", now, zone, null);
        var notDiscord = PastConversations.Recall(reread, "Do you remember who painted the lighthouse mural?", now, zone, null);
        Step("the talk window recalls Telegram exchanges but never Discord's (other people's chatter; Discord keeps its own history)",
            recalled.Count == 1 && recalled[0].App == HistoryApps.Telegram && notDiscord.Count == 0,
            new { telegram = recalled.Count, discord = notDiscord.Count });

        // What deleting or editing asks of each app.
        var replyGone = HistoryPlatforms.Delete(lighthouse, HistorySide.Reply, now);
        var serverUser = HistoryPlatforms.Delete(lighthouse, HistorySide.User, now);
        var dmBoth = HistoryPlatforms.Delete(direct, null, now);
        var tooOld = HistoryPlatforms.Delete(old, null, now);
        Step("deleting asks each app only for what it allows: Discord deletes Martlet's pieces and a server message, never your DM; Telegram only for 48 hours",
            replyGone.Changes.Select(c => c.Message).SequenceEqual(["5002", "5003"]) && replyGone.Changes.All(c => c.Own && c.Kind == PlatformChangeKind.Delete) &&
            serverUser.Changes.Single() is { Message: "5001", Own: false, Server: "800" } &&
            dmBoth.Changes.Single() is { Message: "6002", Own: true } && dmBoth.KeptThere.Single().Contains("DM", StringComparison.Ordinal) &&
            tooOld.Changes.Count == 0 && tooOld.KeptThere.Count == 2,
            new { replyPieces = replyGone.Changes.Count, serverMessage = serverUser.Changes.Count, dm = dmBoth.Changes.Count, dmKept = dmBoth.KeptThere,
                  telegramAfter48h = tooOld.KeptThere });
        var shorter = HistoryPlatforms.Edit(lighthouse, "Mira painted it.", now);
        var longer = HistoryPlatforms.Edit(direct, new string('x', 2500), now);
        var user = HistoryPlatforms.WhyNotEdit(lighthouse.Source!, HistorySide.User);
        Step("editing Martlet's reply edits its pieces there (extra pieces deleted; a longer reply is cut to the pieces it had); your messages are never edited there",
            shorter.Changes.Select(c => (c.Kind, c.Message)).SequenceEqual([(PlatformChangeKind.Edit, "5002"), (PlatformChangeKind.Delete, "5003")]) &&
            shorter.Source?.ReplyMessages?.SequenceEqual(["5002"]) == true &&
            longer.Truncated && longer.Changes.Single() is { Kind: PlatformChangeKind.Edit, Text.Length: <= 2000 } && user is not null,
            new { shorter = shorter.Changes.Count, longerTruncated = longer.Truncated, userEdit = user });

        // One message, an edit and an exchange in the record itself.
        var (_, edited) = await reread.ChangeAsync(lighthouse.Id, found => found with { Reply = "Mira painted it.", Edited = now }, cancellation);
        var (_, halved) = await reread.ChangeAsync(direct.Id, found => found with { User = "", Source = found.Source! with { UserMessages = null } }, cancellation);
        var (_, gone) = await reread.ChangeAsync(old.Id, _ => null, cancellation);
        var after = new ConversationHistory(folder, clock);
        await after.LoadAsync(cancellation);
        Step("editing a reply, deleting one message and deleting an exchange change only those lines (also after a restart; search follows)",
            edited is not null && halved is not null && gone is null && after.Stats.Exchanges == 4 &&
            after.Exchange(lighthouse.Id) is { Reply: "Mira painted it.", Edited: not null } &&
            after.Exchange(direct.Id) is { User: "", Reply: "Any time." } && after.Exchange(old.Id) is null &&
            after.Search(["goldfish"], null, null, null, 5).Count == 0 && after.Search(["painted"], null, null, null, 5).Count == 1,
            new { left = after.Stats.Exchanges });

        // The queue: each app at its pace, its slow-downs waited out, refusals said, unconnected apps waiting, kept over a restart.
        var queueFile = Path.Combine(folder, PlatformChanges.FileName);
        var queue = new PlatformChanges(queueFile, clock);
        var discord = new FixturePlatform(HistoryApps.Discord, clock);
        PlatformChange Change(string app, string message) =>
            new(Guid.NewGuid(), app, "900", "800", message, PlatformChangeKind.Delete, true, null, now);
        queue.Enqueue([Change(HistoryApps.Discord, "5002"), Change(HistoryApps.Discord, "gone"), Change(HistoryApps.Discord, "5003"),
            Change(HistoryApps.Telegram, "102")]);
        queue.Connect(discord);
        var start = clock.Now;
        for (var round = 0; round < 50; round++)
        {
            var wait = await queue.RunDueAsync(cancellation);
            if (wait is null) break;
            clock.Now += wait.Value > TimeSpan.Zero ? wait.Value : TimeSpan.Zero;
        }
        var status = queue.Status;
        var times = discord.Made.Select(made => (made.At - start).TotalSeconds).ToArray();
        var restarted = new PlatformChanges(queueFile, clock).Status;
        Step("changes go one at a time at each app's pace: a slow-down is waited out, a refusal is dropped and said, an app not connected keeps its changes (also after a restart)",
            discord.Made.Select(made => made.Change.Message).SequenceEqual(["5002", "5003"]) && times.Length == 2 && times[0] >= 3 &&
            times[1] - times[0] >= 2 && status is { Pending: 1, Done: 2, Refused: 1 } && status.PendingByApp.ContainsKey(HistoryApps.Telegram) &&
            restarted is { Pending: 1, Done: 2, Refused: 1 } && status.Describe().Contains("not connected", StringComparison.Ordinal),
            new { madeAtSeconds = times, status.Pending, status.Done, status.Refused, line = status.Describe() });
        queue.Clear();
        Step("Stop waiting changes drops what still waits", new PlatformChanges(queueFile, clock).Status.Pending == 0, new { });
        clock.Now = now;
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
            // The replies are the character's, by the name of the persona it is (Companion › Personality), never "Martlet".
            var named = PastConversations.Notes(yesterday, now, zone, null, "Ivy");
            var unnamed = PastConversations.Notes(yesterday, now, zone, null);
            Step("recalled replies carry the persona's name (\"Ivy: ...\", \"Ivy, on its own: ...\"; Martlet without a persona)",
                named.Contains("Ivy: \"Good luck to Biscuit!", StringComparison.Ordinal) &&
                named.Contains("Ivy, on its own: \"I finished that song", StringComparison.Ordinal) &&
                !named.Contains("Martlet:", StringComparison.Ordinal) && !named.Contains("Martlet,", StringComparison.Ordinal) &&
                unnamed.Contains("Martlet: \"Good luck to Biscuit!", StringComparison.Ordinal),
                new { lines = named.Split('\n').Where(line => line.StartsWith("- ", StringComparison.Ordinal)).ToArray() });
            var mine = PastConversations.Recall(store, "Remember what you said about the weather?", now, zone, current);
            Step("the conversation going on is never recalled (the request already carries it)",
                mine.All(e => e.ConversationId != current), new { recalled = mine.Count });

            // search_conversations: what the model may ask for, and what it is told.
            var (byWords, _) = PastConversations.Parse("{\"query\":\"vet appointment\"}", now, zone);
            var found = PastConversations.Find(store, byWords!, current);
            var result = PastConversations.Result(found, byWords!, now, zone, "Ivy");
            Step("search_conversations finds the vet appointment by its words (the reply under the persona's name)",
                found.Count == 1 && found[0].ConversationId == cat && result.Contains("vet appointment") && result.Contains("data only") &&
                result.Contains("Ivy: \"Good luck to Biscuit!", StringComparison.Ordinal),
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

            await AppsAsync(Path.Combine(root, "apps"), clock, now, zone, Step, cancellation);

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
