using Martlet.Core.Cluster;

namespace Martlet.Core.Tests;

public sealed class NodePresenceTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int seconds) => Start.AddSeconds(seconds);

    /// <summary>Feeds gpu-box's offline time (null while it answers) to the watch every 5 seconds, as the desktop's timer does.</summary>
    private static List<PresenceChange> Run(PresenceWatch watch, Func<int, DateTimeOffset?> offlineSince, int from, int to)
    {
        var changes = new List<PresenceChange>();
        for (var second = from; second <= to; second += 5)
            changes.AddRange(watch.Observe("gpu-box", "GPU-BOX", offlineSince(second), At(second)));
        return changes;
    }

    [Fact]
    public void One_missed_check_says_nothing()
    {
        var watch = new PresenceWatch();
        var changes = Run(watch, second => second is >= 15 and < 30 ? At(15) : null, 0, 120);
        Assert.Empty(changes);
        Assert.Empty(watch.Tracked);
        Assert.Empty(watch.Hosts(At(120)));
    }

    [Fact]
    public void Missing_after_30_seconds_then_away_once_then_back_after_30_seconds_of_answers()
    {
        var watch = new PresenceWatch();
        DateTimeOffset? Offline(int second) => second is >= 15 and < 1215 ? At(15) : null;

        Assert.Empty(Run(watch, Offline, 0, 40));
        Assert.Equal(NodePresenceState.NotAnswering, watch.Hosts(At(40)).Single().State);
        var missing = Assert.Single(Run(watch, Offline, 45, 60));
        Assert.Equal(new PresenceChange("gpu-box", "GPU-BOX", PresenceChangeKind.WentMissing, At(15)), missing);
        Assert.Equal(At(15), watch.Since("gpu-box"));

        var away = Assert.Single(Run(watch, Offline, 65, 1210));
        Assert.Equal(PresenceChangeKind.StayedAway, away.Kind);
        Assert.Equal(At(615), away.At);
        Assert.Equal(NodePresenceState.Away, watch.Hosts(At(1210)).Single().State);

        Assert.Empty(Run(watch, Offline, 1215, 1240));
        Assert.Equal(NodePresenceState.Returning, watch.Hosts(At(1240)).Single().State);
        var back = Assert.Single(Run(watch, Offline, 1245, 1300));
        Assert.Equal(new PresenceChange("gpu-box", "GPU-BOX", PresenceChangeKind.CameBack, At(1215)), back);
        var host = watch.Hosts(At(1300)).Single();
        Assert.Equal(NodePresenceState.Back, host.State);
        Assert.Equal(TimeSpan.FromMinutes(20), host.AwayFor);
        Assert.Null(watch.Since("gpu-box"));

        Assert.Empty(watch.Hosts(At(1215).Add(PresenceWatch.BackShownFor)));
    }

    [Fact]
    public void A_flapping_computer_stays_one_absence_until_it_answers_for_30_seconds()
    {
        var watch = new PresenceWatch();
        // Checks every 15 seconds: silent from 15, then it answers every other check, then from 900 on.
        DateTimeOffset? since = null;
        DateTimeOffset? Offline(int second)
        {
            if (second % 15 == 0)
            {
                var answers = second < 15 || second >= 75 && second < 900 && second % 30 == 0 || second >= 900;
                since = answers ? null : since ?? At(second);
            }
            return since;
        }

        var changes = Run(watch, Offline, 0, 1000);
        Assert.Equal([PresenceChangeKind.WentMissing, PresenceChangeKind.StayedAway, PresenceChangeKind.CameBack], changes.Select(c => c.Kind));
        Assert.Equal(At(15), changes[0].At);
        Assert.Equal(At(615), changes[1].At);
        Assert.Equal(At(900), changes[2].At);
    }

    [Fact]
    public void Back_before_the_away_time_never_stays_away_and_the_back_notice_can_be_dismissed()
    {
        var watch = new PresenceWatch { AwayAfter = TimeSpan.FromMinutes(5) };
        var changes = Run(watch, second => second is >= 15 and < 180 ? At(15) : null, 0, 400);
        Assert.Equal([PresenceChangeKind.WentMissing, PresenceChangeKind.CameBack], changes.Select(c => c.Kind));
        Assert.True(watch.DismissBack("gpu-box"));
        Assert.False(watch.DismissBack("gpu-box"));
        Assert.Empty(watch.Hosts(At(400)));
    }

    [Fact]
    public void The_away_time_follows_the_choice_and_a_forgotten_computer_says_nothing_more()
    {
        var watch = new PresenceWatch { AwayAfter = TimeSpan.FromMinutes(2) };
        var changes = Run(watch, second => second >= 15 ? At(15) : null, 0, 200);
        Assert.Equal([PresenceChangeKind.WentMissing, PresenceChangeKind.StayedAway], changes.Select(c => c.Kind));
        Assert.Equal(At(135), changes[1].At);
        watch.Forget("gpu-box");
        Assert.Empty(watch.Tracked);
        Assert.Empty(watch.Observe("gpu-box", "GPU-BOX", null, At(300)));
    }

    [Fact]
    public void A_long_gap_raises_missing_before_stayed_away()
    {
        var watch = new PresenceWatch();
        var changes = watch.Observe("gpu-box", "GPU-BOX", At(0), At(3600));
        Assert.Equal([PresenceChangeKind.WentMissing, PresenceChangeKind.StayedAway], changes.Select(c => c.Kind));
        Assert.Throws<ArgumentException>(() => watch.Observe(" ", "x", null, At(0)));
    }

    [Fact]
    public void Settings_keep_whole_minutes_from_1_to_240_with_10_by_default()
    {
        var directory = Path.Combine(Path.GetTempPath(), "martlet-presence-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal(10, NodePresenceSettings.AwayMinutes(directory));
            NodePresenceSettings.SaveAwayMinutes(directory, 30);
            Assert.Equal(30, NodePresenceSettings.AwayMinutes(directory));
            File.WriteAllText(Path.Combine(directory, NodePresenceSettings.FileName), "lots");
            Assert.Equal(10, NodePresenceSettings.AwayMinutes(directory));
            Assert.Throws<ArgumentOutOfRangeException>(() => NodePresenceSettings.SaveAwayMinutes(directory, 0));
            Assert.Equal([5, 30, null, null, null, null, null], new[] { "5", " 30 ", "0", "241", "-3", "1.5", null }.Select(NodePresenceSettings.Parse));
            Assert.Contains(NodePresenceSettings.DefaultAwayMinutes, NodePresenceSettings.Choices);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static ClusterNodeRole Role(string kind) => new() { Kind = kind, Model = "m" };

    private static ClusterPlan Plan() => ClusterPlan.Empty
        .Assign(ClusterJobs.Thinking, "gpu-box", false, false, null, "desk-1", Start)
        .Assign(ClusterJobs.Speaking, "desk-host", false, true, "gpu-box", "desk-1", Start)
        .Assign(ClusterJobs.Listening, "m3", false, false, null, "desk-1", Start)
        .Observe("gpu-box", "https://192.168.1.40:9443", [Role("ollama"), Role("deep-thinking"), Role("chatterbox"), Role("stt"), Role("pictures")],
            false, "desk-1", Start)
        .Observe("desk-host", "https://192.168.1.41:9443", [Role("f5"), Role("deep-thinking")], false, "desk-1", Start)
        .Observe("m3", "https://192.168.1.42:9443", [Role("stt")], false, "desk-1", Start);

    [Fact]
    public void The_missing_notice_says_what_the_computer_did_for_you()
    {
        Assert.Equal("presence-missing-gpu-box", NodePresenceNotices.MissingId("gpu-box"));
        Assert.Equal("Working with less: gpu-box isn't answering", NodePresenceNotices.MissingTitle("gpu-box"));
        Assert.Equal(
            "It hasn't answered for 3 minutes. Martlet moved Speaking to desk-host. Thinking stays with it until it is back or you move it " +
            "on the Devices page. Your Speaking pool and Thinking pool go on with your other computers. Your Listening pool has no other " +
            "computer now. No other computer does Pictures.",
            NodePresenceNotices.MissingDetail("gpu-box", Plan(), TimeSpan.FromMinutes(3.5), ["m3"]));
        Assert.Contains("Your Speaking pool, Thinking pool and Listening pool go on",
            NodePresenceNotices.MissingDetail("gpu-box", Plan(), TimeSpan.FromMinutes(3)), StringComparison.Ordinal);
        Assert.Equal("It stopped answering less than a minute ago. Nothing depended on it.",
            NodePresenceNotices.MissingDetail("spare", Plan(), TimeSpan.FromSeconds(40)));
        Assert.Equal("It stopped answering less than a minute ago. Turn on Keep Martlet the same on all my computers to see what it did for you.",
            NodePresenceNotices.MissingDetail("gpu-box", null, TimeSpan.FromSeconds(40)));
    }

    [Fact]
    public void The_back_notice_says_how_long_it_was_away_and_what_stayed_moved()
    {
        Assert.Equal("presence-back-gpu-box", NodePresenceNotices.BackId("gpu-box"));
        Assert.Equal("gpu-box is back", NodePresenceNotices.BackTitle("gpu-box"));
        Assert.Equal("It answers again after 12 minutes away. Speaking is still on desk-host. Move it back on the Devices page if you want.",
            NodePresenceNotices.BackDetail("gpu-box", Plan(), TimeSpan.FromMinutes(12.4)));
        Assert.Equal("It answers again after 1 minute away.", NodePresenceNotices.BackDetail("m3", Plan(), TimeSpan.FromSeconds(75)));
        Assert.Equal("3 hours", NodePresenceNotices.Duration(TimeSpan.FromMinutes(200)));
        Assert.Null(NodePresenceNotices.MapText(TimeSpan.FromSeconds(59)));
        Assert.Equal("Not answering for 7 min", NodePresenceNotices.MapText(TimeSpan.FromMinutes(7.9)));
    }

    [Fact]
    public void The_report_reads_back_the_same()
    {
        var directory = Path.Combine(Path.GetTempPath(), "martlet-presence-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Null(NodePresenceReport.Load(directory));
            var hosts = new[]
            {
                new NodePresenceHost("gpu-box", "GPU-BOX", NodePresenceState.Away, At(15), null, TimeSpan.FromMinutes(11)),
                new NodePresenceHost("m3", "m3", NodePresenceState.Back, null, At(30), TimeSpan.FromMinutes(2))
            };
            new NodePresenceReport
            {
                UpdatedAt = At(700), AwayMinutes = 10, Companion = true, Hosts = hosts,
                Notices = [new("presence-missing-gpu-box", "Warning", "Working with less: GPU-BOX isn't answering", "It hasn't answered for 11 minutes.")]
            }.Save(directory);
            Assert.Contains("\"state\": \"away\"", File.ReadAllText(Path.Combine(directory, NodePresenceReport.FileName)), StringComparison.Ordinal);
            var read = NodePresenceReport.Load(directory)!;
            Assert.Equal(hosts, read.Hosts);
            Assert.Equal(At(700), read.UpdatedAt);
            Assert.Equal("presence-missing-gpu-box", Assert.Single(read.Notices).Id);
            File.WriteAllText(Path.Combine(directory, NodePresenceReport.FileName), "{ not json");
            Assert.Null(NodePresenceReport.Load(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
