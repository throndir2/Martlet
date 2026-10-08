using System.IO;
using System.Text.Json.Nodes;
using Martlet.Core.Cluster;

namespace Martlet.Mcp;

/// <summary>node_presence_status and node_presence_check: when one of your other computers goes away or comes back. The
/// status reads a data directory: the per-PC away time (node-presence.txt), the report the desktop writes when a computer's
/// state changes (node-presence.json: each computer it knows about and the notices Home shows) and the paired hosts
/// (hosts.json). The check rehearses the production rules (<see cref="PresenceWatch"/>, <see cref="NodePresenceNotices"/>,
/// <see cref="NodePresenceSettings"/>, <see cref="NodePresenceReport"/>) on scripted timelines: device sync's check every 15
/// seconds and the desktop's presence rule (HostPresence: offline from the first failed check until one answers), with a
/// tick every 5 seconds as the desktop's timer. Nothing leaves the process; the check writes only a temporary folder.</summary>
internal static class NodePresenceCheck
{
    // ---------- node_presence_status ----------

    internal static object Status(string dataDirectory)
    {
        var now = DateTimeOffset.UtcNow;
        var report = NodePresenceReport.Load(dataDirectory);
        var (paired, own) = Paired(dataDirectory);
        var watched = paired.Where(id => id != own).Union(report?.Hosts.Select(h => h.HostId) ?? [], StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        return new
        {
            awayMinutes = NodePresenceSettings.AwayMinutes(dataDirectory),
            awayMinutesSaved = File.Exists(Path.Combine(dataDirectory, NodePresenceSettings.FileName)),
            defaultAwayMinutes = NodePresenceSettings.DefaultAwayMinutes,
            missingAfterSeconds = PresenceWatch.MissingAfter.TotalSeconds,
            backAfterSeconds = PresenceWatch.BackAfter.TotalSeconds,
            backShownForMinutes = PresenceWatch.BackShownFor.TotalMinutes,
            report = report is null ? "none: the desktop writes node-presence.json when it starts and when a computer's state changes" : "loaded",
            updatedAt = report?.UpdatedAt,
            companion = report?.Companion,
            ownHost = own,
            hosts = watched.Select(id =>
            {
                var known = report?.Hosts.FirstOrDefault(h => h.HostId == id);
                return new
                {
                    hostId = id, name = known?.Name ?? id, paired = paired.Contains(id),
                    state = (known?.State ?? NodePresenceState.Answering).ToString(),
                    since = known?.Since, backAt = known?.BackAt,
                    // Away so far (still away) or in all (back); from the report's times, as of now.
                    awayForMinutes = known is { Since: { } since } ? Math.Round((now - since).TotalMinutes, 1)
                        : known?.AwayFor is { } span ? Math.Round(span.TotalMinutes, 1) : (double?)null
                };
            }),
            notices = (report?.Notices ?? []).Select(n => new { id = n.Id, level = n.Level, title = n.Title, detail = n.Detail })
        };
    }

    // The paired host IDs in hosts.json and the one saved as this PC's own (Docker Desktop here). A host a friend shares with this PC
    // isn't watched (as the desktop's PresenceHosts).
    private static (string[] Hosts, string? Own) Paired(string dataDirectory)
    {
        try
        {
            var path = Path.Combine(dataDirectory, "hosts.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 65_536) return ([], null);
            var hosts = (JsonNode.Parse(File.ReadAllText(path))?["hosts"] as JsonArray ?? []).OfType<JsonObject>()
                .Where(h => h["access"]?.GetValue<string>() != "friend").ToArray();
            return ([.. hosts.Select(h => h["pairing"]?["hostId"]?.GetValue<string>()).OfType<string>()],
                hosts.FirstOrDefault(h => h["method"]?.GetValue<string>() == "ThisPcDocker")?["pairing"]?["hostId"]?.GetValue<string>());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return ([], null);
        }
    }

    // ---------- node_presence_check ----------

    private static readonly DateTimeOffset Start = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private const string Gpu = "gpu-box";

    /// <summary>The desktop's presence rule (HostPresence): offline from the first failed check until a check reaches it.</summary>
    private sealed class Presence
    {
        private readonly Dictionary<string, DateTimeOffset> offline = new(StringComparer.Ordinal);
        public void Note(string host, bool reachable, DateTimeOffset at)
        {
            if (reachable) offline.Remove(host);
            else offline.TryAdd(host, at);
        }
        public DateTimeOffset? OfflineSince(string host) => offline.TryGetValue(host, out var since) ? since : null;
    }

    private sealed record Seen(string Kind, double AtSecond, double NoticedSecond);

    /// <summary>Runs seconds <paramref name="from"/> to <paramref name="to"/>: a check every 15 seconds (<paramref name="answers"/>
    /// says whether gpu-box answers it) and a tick every 5 seconds.</summary>
    private static List<Seen> Run(PresenceWatch watch, Presence presence, Func<int, bool> answers, int from, int to)
    {
        var seen = new List<Seen>();
        for (var second = from; second <= to; second += 5)
        {
            var now = Start.AddSeconds(second);
            if (second % 15 == 0) presence.Note(Gpu, answers(second), now);
            foreach (var change in watch.Observe(Gpu, "GPU-BOX", presence.OfflineSince(Gpu), now))
                seen.Add(new(change.Kind.ToString(), (change.At - Start).TotalSeconds, second));
        }
        return seen;
    }

    private static NodePresenceState StateAt(PresenceWatch watch, int second) =>
        watch.Hosts(Start.AddSeconds(second)).FirstOrDefault(h => h.HostId == Gpu)?.State ?? NodePresenceState.Answering;

    /// <summary>The owner's kind of network: gpu-box did Thinking and was in the Speaking, Listening and Thinking pools; a
    /// failover already moved Speaking to desk-host, which runs a voice and the Thinking pool too.</summary>
    private static ClusterPlan Plan()
    {
        var at = Start.AddDays(-1);
        return ClusterPlan.Empty
            .Assign(ClusterJobs.Thinking, Gpu, false, false, null, "desk-1", at)
            .Assign(ClusterJobs.Speaking, "desk-host", false, true, Gpu, "desk-1", at)
            .Observe(Gpu, "https://192.168.1.40:9443", [Role("ollama"), Role("deep-thinking"), Role("chatterbox"), Role("stt"), Role("pictures")], false, "desk-1", at)
            .Observe("desk-host", "https://192.168.1.41:9443", [Role("chatterbox"), Role("deep-thinking")], false, "desk-1", at);
    }

    private static ClusterNodeRole Role(string kind) => new() { Kind = kind, Model = kind + "-model" };

    internal static object Run()
    {
        List<object> steps = [];
        var ok = true;
        void Step(string name, bool passed, object detail)
        {
            ok &= passed;
            steps.Add(new { name, passed, detail });
        }

        // 1. One missed check (a flaky moment) says nothing.
        var flaky = new PresenceWatch();
        var flakyPresence = new Presence();
        var flakySeen = Run(flaky, flakyPresence, second => second != 15, 0, 120);
        Step("One missed check says nothing: no event, no notice", flakySeen.Count == 0 && flaky.Tracked.Count == 0, new { events = flakySeen });

        // 2. Missing: silent for 30 seconds (two checks), once.
        var watch = new PresenceWatch();
        var presence = new Presence();
        Func<int, bool> away = second => second < 15 || second >= 1215;
        var missing = Run(watch, presence, away, 0, 60);
        var detail = NodePresenceNotices.MissingDetail(Gpu, Plan(), Start.AddSeconds(60) - Start.AddSeconds(15));
        Step("A computer silent for 30 seconds goes missing once, dated when it stopped answering",
            missing is [{ Kind: "WentMissing", AtSecond: 15, NoticedSecond: 45 }] && StateAt(watch, 60) == NodePresenceState.Missing,
            new { events = missing });
        Step("The degraded notice says what it did for you: the failover move, the job that waits, the pools",
            NodePresenceNotices.MissingTitle("gpu-box") == "Working with less: gpu-box isn't answering" &&
            NodePresenceNotices.MissingId(Gpu) == "presence-missing-gpu-box" &&
            detail.Contains("Martlet moved Speaking to desk-host.", StringComparison.Ordinal) &&
            detail.Contains("Thinking stays with it until it is back", StringComparison.Ordinal) &&
            detail.Contains("Your Speaking pool and Thinking pool go on with your other computers.", StringComparison.Ordinal) &&
            detail.Contains("Your Listening pool has no other computer now.", StringComparison.Ordinal) &&
            detail.Contains("No other computer does Pictures.", StringComparison.Ordinal),
            new { title = NodePresenceNotices.MissingTitle("gpu-box"), detail });

        // 3. Stayed away: once, after the time chosen (10 minutes by default).
        var stayed = Run(watch, presence, away, 65, 1210);
        Step("Still missing after 10 minutes: stayed away once, never again in that absence",
            stayed is [{ Kind: "StayedAway", NoticedSecond: 615 }] && StateAt(watch, 1210) == NodePresenceState.Away,
            new { events = stayed, map = NodePresenceNotices.MapText(TimeSpan.FromSeconds(1210 - 15)) });

        // 4. Back: it answers again, for 30 seconds.
        var returning = Run(watch, presence, away, 1215, 1240);
        var stateReturning = StateAt(watch, 1240);
        var back = Run(watch, presence, away, 1245, 1300);
        var host = watch.Hosts(Start.AddSeconds(1300)).FirstOrDefault(h => h.HostId == Gpu);
        var backDetail = NodePresenceNotices.BackDetail(Gpu, Plan(), host?.AwayFor ?? TimeSpan.Zero);
        Step("Back once it answered for 30 seconds, dated when it answered again; the notice says how long it was away",
            returning.Count == 0 && stateReturning == NodePresenceState.Returning &&
            back is [{ Kind: "CameBack", AtSecond: 1215, NoticedSecond: 1245 }] && host?.State == NodePresenceState.Back &&
            NodePresenceNotices.BackTitle("gpu-box") == "gpu-box is back" &&
            backDetail.StartsWith("It answers again after 20 minutes away.", StringComparison.Ordinal) &&
            backDetail.Contains("Speaking is still on desk-host", StringComparison.Ordinal),
            new { events = back, whileConfirming = stateReturning.ToString(), title = NodePresenceNotices.BackTitle("gpu-box"), detail = backDetail });
        var reportHosts = watch.Hosts(Start.AddSeconds(1300));
        var expired = watch.Hosts(Start.AddSeconds(1215).Add(PresenceWatch.BackShownFor)).Count == 0;
        Step("The back notice clears by itself after 10 minutes", expired, new { shownForMinutes = PresenceWatch.BackShownFor.TotalMinutes });

        // 5. Missing, then back before the away time: no stayed-away event.
        var brief = new PresenceWatch();
        var briefSeen = Run(brief, new Presence(), second => second < 15 || second >= 180, 0, 300);
        Step("Back before the away time: missing and back, never stayed away",
            briefSeen.Select(s => s.Kind).SequenceEqual(["WentMissing", "CameBack"]), new { events = briefSeen });
        Step("A back notice can be dismissed", brief.DismissBack(Gpu) && brief.Hosts(Start.AddSeconds(300)).Count == 0, new { });

        // 6. Flapping: it answers now and then, never for 30 seconds: one absence.
        var flapping = new PresenceWatch();
        var flapSeen = Run(flapping, new Presence(), second => second < 15 || second >= 75 && second < 900 && second % 30 == 0 || second >= 900, 0, 1000);
        Step("A computer that flaps stays one absence: missing once, stayed away once, back once when it stays",
            flapSeen.Select(s => s.Kind).SequenceEqual(["WentMissing", "StayedAway", "CameBack"]) &&
            flapSeen[0].AtSecond == 15 && flapSeen[1].NoticedSecond == 615 && flapSeen[2].AtSecond == 900 && flapSeen[2].NoticedSecond == 930,
            new { events = flapSeen });

        // 7. The away time is the per-PC choice.
        var directory = Path.Combine(Path.GetTempPath(), "martlet-node-presence-" + Guid.NewGuid().ToString("N"));
        try
        {
            var before = NodePresenceSettings.AwayMinutes(directory);
            NodePresenceSettings.SaveAwayMinutes(directory, 2);
            var saved = NodePresenceSettings.AwayMinutes(directory);
            var parsed = new[] { "5", " 30 ", "0", "241", "ten", "" }.Select(NodePresenceSettings.Parse).ToArray();
            var quick = new PresenceWatch { AwayAfter = TimeSpan.FromMinutes(saved) };
            var quickSeen = Run(quick, new Presence(), second => second < 15, 0, 200);
            Step("Away after the minutes chosen in Settings (default 10, 1 to 240, saved per PC)",
                before == 10 && saved == 2 && parsed.SequenceEqual(new int?[] { 5, 30, null, null, null, null }) &&
                quickSeen.Select(s => s.Kind).SequenceEqual(["WentMissing", "StayedAway"]) && quickSeen[1].NoticedSecond == 135,
                new { before, saved, parsed, events = quickSeen });

            // 8. The report node_presence_status reads.
            new NodePresenceReport
            {
                UpdatedAt = Start.AddSeconds(1300), AwayMinutes = 10, Companion = true, Hosts = reportHosts,
                Notices = [new(NodePresenceNotices.BackId(Gpu), "Notice", NodePresenceNotices.BackTitle("GPU-BOX"), backDetail)]
            }.Save(directory);
            var read = NodePresenceReport.Load(directory);
            Step("The desktop's report reads back the same",
                reportHosts.Count == 1 && read is { AwayMinutes: 10, Companion: true } && read.Hosts.SequenceEqual(reportHosts) &&
                read.Notices.Single().Id == "presence-back-gpu-box",
                new { hosts = read?.Hosts.Select(h => new { h.HostId, state = h.State.ToString() }) });
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }

        // 9. A computer that is no longer paired is forgotten.
        var forgotten = new PresenceWatch();
        Run(forgotten, new Presence(), second => second < 15, 0, 60);
        forgotten.Forget(Gpu);
        Step("A computer no longer paired is forgotten", forgotten.Tracked.Count == 0, new { });

        return new { ok, fixture = "scripted checks every 15 seconds with the desktop's presence rule (NOT real hosts)", steps };
    }
}
