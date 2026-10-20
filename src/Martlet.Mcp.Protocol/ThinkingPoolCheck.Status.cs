using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Conversation;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Mcp;

internal static partial class ThinkingPoolCheck
{
    internal static async Task<object> StatusAsync(string dataDirectory, CancellationToken cancellation)
    {
        var loaded = await new SettingsStore(dataDirectory).LoadAsync(cancellation);
        var routes = loaded.Settings?.Setup?.Routes ?? [];
        var (pool, state) = ThinkingPoolSettings.Read(dataDirectory, save: false);
        var abilities = ModelAbilities.Load(dataDirectory);
        var plan = pool.Plan(routes);
        var places = ThinkLonger.Places(plan, null, member => ThinkingPoolCapabilities.For(member, abilities), choices: pool)
            .Where(p => p.Id != "thinking").ToArray();
        var members = places.Select(p => new ThinkingPoolMemberStatus(p.Id, p.Name, p.Slots, 0, p.Can, p.Rank) { Media = p.Media }).ToArray();
        var slots = members.Sum(m => m.Slots);
        var (desktop, file) = DesktopStatus(dataDirectory);
        // Which members' computers answer now: only the desktop knows (its host checks), through thinking-pool-status.json.
        JsonNode? Seen(string key) => (file?["members"] as JsonArray)?.FirstOrDefault(m => m?["id"]?.GetValue<string>() == key);
        return new
        {
            file = state,
            useConversationModelWhenEmpty = pool.UseConversationModelWhenEmpty,
            // The paired computers the owner keeps out (host IDs only): they never join the pool by themselves.
            leftByOwner = pool.LeftByOwner,
            // The list is the pool: with no member on, it is off.
            poolOff = pool.Members.Count == 0,
            // The machines the owner turned off: in the list with their boxes, but they take no jobs; a paired computer's card
            // here never joins again by itself.
            offMembers = pool.OffMembers.Select(m => new
            {
                key = m.Key, where = m.Describe(), place = m.Place.ToString(), model = m.ModelId, hostId = m.HostId, hostRole = m.OnHostRole,
                card = m.Place == DeepThinkingPlace.Host ? m.Card : (int?)null, slots = m.ThinksAtOnce,
                quickJobs = pool.TakesQuickJobs(m.Key), longJobs = pool.TakesLongJobs(m.Key), answersForConversation = pool.Answers(m.Key)
            }),
            migratedAt = pool.MigratedAt,
            members = pool.Members.Select(m =>
            {
                var spot = plan.Find(m.Key);
                var can = ThinkingPoolCapabilities.For(m, abilities);
                var seen = Seen(m.Key);
                return new
                {
                    key = m.Key, where = m.Describe(), place = m.Place.ToString(), model = m.ModelId, hostId = m.HostId,
                    hostRole = m.OnHostRole, slots = m.ThinksAtOnce, ownKey = m.CredentialId is not null,
                    // A paired computer's graphics card (1, or 2-4 for a Thinking pool model on an extra card, a member of its own)
                    // and the route it thinks on.
                    card = m.Place == DeepThinkingPlace.Host ? m.Card : (int?)null, route = m.Place == DeepThinkingPlace.Host ? m.HostRoute : null,
                    text = true, vision = can.HasFlag(ThinkingCapability.Vision), audio = can.HasFlag(ThinkingCapability.Audio),
                    tools = can.HasFlag(ThinkingCapability.Tools),
                    available = spot?.Plan.Available ?? false, rank = spot?.Plan.Rank, why = spot?.Plan.Why,
                    // Whether its computer answers now (null: the desktop hasn't said), and since when it doesn't.
                    online = seen?["online"]?.GetValue<bool>(), offlineSince = seen?["offlineSince"]?.GetValue<DateTimeOffset>(),
                    // Backup for slow replies (Backup Thinking), off by default; a paid cloud member only when ticked.
                    answersForConversation = pool.Answers(m.Key), paid = ThinkingBackupMembers.Paid(m),
                    // The Quick jobs and Long jobs boxes: the judges and summaries, and every other kind.
                    quickJobs = pool.TakesQuickJobs(m.Key), longJobs = pool.TakesLongJobs(m.Key),
                    // May receive pictures and recordings: an external member (an endpoint not on this PC) only when ticked.
                    external = ThinkingPoolSettings.IsExternal(m), mayReceiveMedia = pool.MayReceiveMedia(m),
                    // How smart its model is (Runs on uses it): the owner's choice, else Martlet's guess from the model name.
                    smarts = pool.SmartsOf(m).ToString(), smartsGuess = ThinkingSmartsGuess.From(m.ModelId).ToString(),
                    smartsChosen = pool.Smarts.ContainsKey(m.Key)
                };
            }),
            // Runs on for each kind of job, and the members each kind may use now (by name).
            runsOn = ThinkingJobKinds.All.ToDictionary(ThinkingJobKinds.Name, kind =>
            {
                var where = pool.RunsOn(ThinkingJobKinds.Name(kind));
                return new
                {
                    mode = where.Name, members = where.Members,
                    may = places.Where(p => p.Takes(kind) && ThinkingRunsOnRules.Allows(p, where)).Select(p => p.Name).ToArray()
                };
            }),
            // Backup Thinking: its choices and who it would ask now for a plain reply that is taken (the production choice, on
            // what the conversation's routes run on).
            backup = new
            {
                on = pool.BackupThinking, delayMs = pool.BackupDelayMs, automaticDelay = pool.BackupDelayMs is null,
                minimumAutomaticDelayMs = FirstWordTimes.Minimum.TotalMilliseconds, startingDelayMs = FirstWordTimes.Starting.TotalMilliseconds,
                wouldAsk = ThinkingBackupMembers.Choose(pool, plan, places, LiveResources.For(routes), new BoundedTextInput("status"), held: false) is var choice
                    ? new { member = choice.Spot?.Settings.Describe(), why = choice.Why } : null
            },
            // The line's rules from thinking-pool.json: stops for priority, the raise after stops and the retries after a failure.
            line = ThinkingPoolPolicy.From(pool) is var policy
                ? new { preemptLowerPriority = policy.PreemptLowerPriority, raiseAfterStops = policy.RaiseAfterStops, retries = policy.Retries } : null,
            usable = places.Length, slots, keepsFastSlot = slots >= 2,
            conversationModel = pool.Members.Count == 0 && pool.UseConversationModelWhenEmpty
                ? new { used = true, available = plan.Plan.Available, why = plan.Plan.Why } : null,
            // Per kind, only the members whose Quick jobs or Long jobs box lets them take it.
            canRun = ThinkingJobKinds.All.ToDictionary(ThinkingJobKinds.Name, kind => new
            {
                text = places.Any(p => p.Takes(kind)), vision = places.Any(p => p.Takes(kind) && p.Media && p.Can.HasFlag(ThinkingCapability.Vision)),
                audio = places.Any(p => p.Takes(kind) && p.Media && p.Can.HasFlag(ThinkingCapability.Audio)),
                tools = places.Any(p => p.Takes(kind) && p.Can.HasFlag(ThinkingCapability.Tools)),
                priority = (int)ThinkingJobKinds.Priority(kind), fast = ThinkingJobKinds.IsFast(kind)
            }),
            // The one table of job kinds (ThinkingJobKinds.Rows, in the Runs on card's order): each kind's saved key, the Runs on
            // label (all the work it controls), its type on the Thinking requests page, its priority, whether it is fast, the most
            // its jobs need, what the live floor does to it and the helper jobs that run as it.
            kinds = ThinkingJobKinds.Rows.Select(row => new
            {
                key = row.Key, label = row.Label, type = row.TypeName, priority = (int)row.Priority, fast = row.Fast,
                needs = row.Needs.ToString(), whileLive = row.WhileLive.ToString(),
                helpers = row.Helpers.Select(helper => new
                {
                    kind = helper.Kind.ToString(), key = helper.Key, priority = (int)(helper.Priority ?? row.Priority), ownerWaits = helper.OwnerWaits
                })
            }),
            guidance = ThinkingJobBoard.Guidance(members),
            warnings = ThinkingPoolWarnings.For(plan, routes),
            // The pool now, as the desktop last wrote it: slots of the members that answer, of every member, and who is offline.
            presence = file is null ? null : new
            {
                slots = file["slots"]?.GetValue<int>(), free = file["free"]?.GetValue<int>(), configuredSlots = file["configuredSlots"]?.GetValue<int>(),
                offline = (file["members"] as JsonArray)?.Where(m => m?["online"]?.GetValue<bool>() == false).Select(m => m!["name"]?.GetValue<string>()).ToArray() ?? [],
                conversationModelStandsIn = file["conversationModelStandsIn"]?.GetValue<bool>(), updated = file["updated"]?.GetValue<DateTimeOffset>(),
                // Members whose providers limit requests: in plain words, when each tries again and how many jobs it runs at once.
                limiting = (file["cooling"] as JsonArray)?.Select(c => c?["says"]?.GetValue<string>()).ToArray() ?? []
            },
            desktop
        };
    }

    private static (object Status, JsonNode? File) DesktopStatus(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "thinking-pool-status.json");
        try
        {
            if (!File.Exists(path)) return (new { state = "none", why = "The desktop hasn't run a conversation with this data directory." }, null);
            if (new FileInfo(path).Length > 262_144) return (new { state = "unreadable", why = "thinking-pool-status.json is too large." }, null);
            var file = JsonNode.Parse(File.ReadAllText(path));
            return (new { state = "loaded", file }, file);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return (new { state = "unreadable", why = error.GetType().Name }, null);
        }
    }

    /// <summary>thinking_requests: the desktop's thinking-requests.json (every Thinking request kept, with its timings, and the
    /// totals by kind; never a request's text, answer or topic), filtered by state and kind, newest last-ended first.</summary>
    internal static object Requests(string dataDirectory, string? state, string? kind, int? limit)
    {
        state = state?.Trim().ToLowerInvariant();
        kind = kind?.Trim().ToLowerInvariant();
        if (state is not null && !RequestStates.Contains(state))
            throw new ArgumentException($"state must be one of {string.Join(", ", RequestStates)}.");
        if (kind is not null && !RequestKinds.Contains(kind))
            throw new ArgumentException($"kind must be one of {string.Join(", ", RequestKinds)}.");
        var take = Math.Clamp(limit ?? 50, 1, 200);
        var path = Path.Combine(dataDirectory, RequestsFile);
        JsonNode? file;
        try
        {
            if (!File.Exists(path)) return new { state = "none", why = "The desktop hasn't written thinking-requests.json in this data directory (no request yet)." };
            if (new FileInfo(path).Length > RequestsFileLimit) return new { state = "unreadable", why = $"{RequestsFile} is larger than 4 MiB." };
            file = JsonNode.Parse(File.ReadAllText(path));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new { state = "unreadable", why = error.GetType().Name };
        }
        if (file is not JsonObject root || root["requests"] is not JsonArray all)
            return new { state = "unreadable", why = $"{RequestsFile} has no requests list." };

        static string? Text(JsonNode? node, string name) => node?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        static int Number(JsonNode? node, string name) => node?[name] is JsonValue value && value.TryGetValue<int>(out var n) ? n : 0;
        bool Matches(JsonNode? request)
        {
            if (kind is not null && Text(request, "kind") != kind) return false;
            if (state is null) return true;
            var now = Text(request, "state")?.ToLowerInvariant();
            var active = now is "waiting" or "running" or "paused";
            return state switch
            {
                "active" => active,
                "done" => !active,
                "problems" => !active && now is not ("succeeded" or "canceled") || Number(request, "retries") > 0 || Number(request, "preemptions") > 0,
                _ => now == state
            };
        }
        var matched = all.Where(Matches).ToArray();
        return new
        {
            state = "loaded", file = path, schemaVersion = root["schemaVersion"]?.DeepClone(), updated = root["updated"]?.DeepClone(),
            active = root["active"]?.DeepClone(), kept = root["kept"]?.DeepClone(), totals = root["totals"]?.DeepClone(),
            filter = new { state, kind, limit = take }, matched = matched.Length, shown = Math.Min(take, matched.Length),
            requests = new JsonArray([.. matched.Take(take).Select(request => request?.DeepClone())]),
            note = "Each request's type, task, companion, member, tries and timings, and its input's and answer's lengths; never its text, input, answer or topic."
        };
    }
}
