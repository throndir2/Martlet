using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;
using Martlet.Core.Settings;
using Martlet.Core.Tests;
using Martlet.Mcp;

namespace Martlet.Desktop.Tests;

/// <summary>The presence notices on top of <see cref="HostPresence"/>: Home's "Working with less" and "is back" items, the
/// events for the recommended setup, the Devices map's "Not answering for N min", the per-PC away time and the MCP tools.
/// HostPresence is static; Desktop.Tests runs serially, and each test resets it.</summary>
public sealed class NodePresenceDesktopTests
{
    private static AvatarRemoteHost Remote(string id, string ip) => new()
    {
        Origin = $"https://{ip}:9443", HostId = id, SpkiFingerprint = "sha256:" + new string('a', 64),
        DeviceId = "desktop-test", CredentialId = new string('B', 22)
    };

    [Fact]
    public void The_map_says_how_long_a_computer_hasnt_answered()
    {
        var hosts = new[] { new PairedHost { Pairing = Remote("gpu-box", "192.168.1.40") } };
        var checks = new Dictionary<string, HostCheck> { ["gpu-box"] = new(false, "Didn't respond in time.") };
        string Health(IReadOnlyDictionary<string, TimeSpan>? away) => NetworkMap.Build(new(MachineInfo.Unknown, DeviceRole.Companion, null, null,
            false, checks, Hosts: hosts, HostAway: away)).Single(n => n.Id == "host:gpu-box").HealthText;

        Assert.Equal("Not reachable", Health(null));
        Assert.Equal("Not reachable", Health(new Dictionary<string, TimeSpan> { ["gpu-box"] = TimeSpan.FromSeconds(40) }));
        Assert.Equal("Not answering for 3 min", Health(new Dictionary<string, TimeSpan> { ["gpu-box"] = TimeSpan.FromMinutes(3.2) }));
    }

    [Fact]
    public Task A_companion_pc_tells_home_and_the_recommended_setup_when_a_computer_goes_and_comes_back() => OnDispatcher(() =>
    {
        var clock = new ManualClock();
        HostPresence.Reset();
        HostPresence.Clock = clock;
        var root = Path.Combine(Path.GetTempPath(), "martlet-presence-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(data);
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        // The main window stays unshown, so nothing it starts once shown (sync, the presence timer) runs; the test drives it.
        var main = new MainWindow(new SettingsStore(data), null) { ShowActivated = false, ShowInTaskbar = false };
        var seen = new List<PresenceChange>();
        main.PresenceChanged += seen.Add;
        string[] paired = ["gpu-box"];
        DateTimeOffset Now() => clock.GetUtcNow();
        try
        {
            // Settings › Your other computers: 10 minutes by default; another choice is saved for this PC.
            var choice = Assert.IsType<ComboBox>(main.FindName("PresenceAwayChoice"));
            Assert.Equal("10", Assert.IsType<ComboBoxItem>(choice.SelectedItem).Content);
            choice.SelectedItem = choice.Items.OfType<ComboBoxItem>().Single(i => (string)i.Content == "5");
            Assert.Equal(5, NodePresenceSettings.AwayMinutes(data));

            // One missed check: nothing yet.
            HostPresence.Note("gpu-box", false);
            Assert.Empty(main.ObservePresence(Now(), paired));
            Assert.Equal(TimeSpan.Zero, main.OfflineFor("gpu-box"));
            Assert.Empty(main.PresenceIssues());

            // 30 seconds without an answer: missing, once.
            clock.Advance(TimeSpan.FromSeconds(30));
            var missing = Assert.Single(main.ObservePresence(Now(), paired));
            Assert.Equal(new PresenceChange("gpu-box", "gpu-box", PresenceChangeKind.WentMissing, Now().AddSeconds(-30)), missing);
            Assert.Equal([missing], seen);
            var warning = Assert.Single(main.PresenceIssues());
            Assert.Equal("presence-missing-gpu-box", warning.Id);
            Assert.Equal(HealthLevel.Warning, warning.Level);
            Assert.Equal("Working with less: gpu-box isn't answering", warning.Title);
            Assert.Equal(["check", "show"], warning.Fixes.Select(f => f.Id));
            Assert.Equal(TimeSpan.FromSeconds(30), main.OfflineFor("gpu-box"));
            Assert.Empty(main.ObservePresence(Now(), paired));
            var report = NodePresenceReport.Load(data);
            Assert.NotNull(report);
            Assert.Equal(NodePresenceState.Missing, Assert.Single(report.Hosts).State);
            Assert.Equal("presence-missing-gpu-box", Assert.Single(report.Notices).Id);
            Assert.Equal(5, report.AwayMinutes);

            // Still missing after the 5 minutes chosen: stayed away, once.
            clock.Advance(TimeSpan.FromMinutes(5));
            Assert.Equal(PresenceChangeKind.StayedAway, Assert.Single(main.ObservePresence(Now(), paired)).Kind);
            clock.Advance(TimeSpan.FromMinutes(5));
            Assert.Empty(main.ObservePresence(Now(), paired));

            // It answers again: back after 30 seconds of answers.
            HostPresence.Note("gpu-box", true);
            Assert.Empty(main.ObservePresence(Now(), paired));
            Assert.Null(main.OfflineFor("gpu-box"));
            Assert.EndsWith("It answers again; Martlet checks that it stays.", Assert.Single(main.PresenceIssues()).Detail, StringComparison.Ordinal);
            clock.Advance(TimeSpan.FromSeconds(30));
            Assert.Equal(PresenceChangeKind.CameBack, Assert.Single(main.ObservePresence(Now(), paired)).Kind);
            var notice = Assert.Single(main.PresenceIssues());
            Assert.Equal("presence-back-gpu-box", notice.Id);
            Assert.Equal(HealthLevel.Notice, notice.Level);
            Assert.Equal("gpu-box is back", notice.Title);
            Assert.StartsWith("It answers again after 10 minutes away.", notice.Detail, StringComparison.Ordinal);
            Assert.Equal([PresenceChangeKind.WentMissing, PresenceChangeKind.StayedAway, PresenceChangeKind.CameBack], seen.Select(c => c.Kind));

            notice.Fixes.Single(f => f.Id == "dismiss").Run();
            Assert.Empty(main.PresenceIssues());

            // A computer that is no longer paired says nothing.
            HostPresence.Note("gpu-box", false);
            clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Empty(main.ObservePresence(Now(), []));
            Assert.Empty(main.PresenceIssues());
            Assert.Equal(3, seen.Count);
        }
        finally
        {
            main.Close();
            HostPresence.Clock = TimeProvider.System;
            HostPresence.Reset();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
        return Task.CompletedTask;
    });

    [Fact]
    public void The_mcp_check_passes_every_step()
    {
        var result = JsonSerializer.SerializeToElement(NodePresenceCheck.Run());
        var failed = result.GetProperty("steps").EnumerateArray().Where(s => !s.GetProperty("passed").GetBoolean())
            .Select(s => s.GetProperty("name").GetString() + ": " + s.GetProperty("detail").GetRawText()).ToArray();
        Assert.Empty(failed);
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.True(result.GetProperty("steps").GetArrayLength() >= 10);
    }

    [Fact]
    public void The_mcp_status_reads_the_away_time_the_report_and_the_pairings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "martlet-presence-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var empty = JsonSerializer.SerializeToElement(NodePresenceCheck.Status(directory));
            Assert.Equal(10, empty.GetProperty("awayMinutes").GetInt32());
            Assert.False(empty.GetProperty("awayMinutesSaved").GetBoolean());
            Assert.StartsWith("none", empty.GetProperty("report").GetString(), StringComparison.Ordinal);

            NodePresenceSettings.SaveAwayMinutes(directory, 15);
            File.WriteAllText(Path.Combine(directory, "hosts.json"), """
                {"hosts":[{"pairing":{"hostId":"gpu-box"},"method":"OnHost"},{"pairing":{"hostId":"own-host"},"method":"ThisPcDocker"},
                {"pairing":{"hostId":"m3"},"method":"OnHost"}]}
                """);
            var since = DateTimeOffset.UtcNow.AddMinutes(-12);
            new NodePresenceReport
            {
                UpdatedAt = DateTimeOffset.UtcNow, AwayMinutes = 15, Companion = true,
                Hosts = [new("gpu-box", "GPU-BOX", NodePresenceState.Missing, since, null, TimeSpan.FromMinutes(1))],
                Notices = [new("presence-missing-gpu-box", "Warning", "Working with less: GPU-BOX isn't answering", "It hasn't answered for 1 minute.")]
            }.Save(directory);

            var status = JsonSerializer.SerializeToElement(NodePresenceCheck.Status(directory));
            Assert.Equal(15, status.GetProperty("awayMinutes").GetInt32());
            Assert.Equal("loaded", status.GetProperty("report").GetString());
            Assert.Equal("own-host", status.GetProperty("ownHost").GetString());
            var hosts = status.GetProperty("hosts").EnumerateArray().ToArray();
            Assert.Equal(["gpu-box", "m3"], hosts.Select(h => h.GetProperty("hostId").GetString()));
            Assert.Equal("Missing", hosts[0].GetProperty("state").GetString());
            Assert.Equal("GPU-BOX", hosts[0].GetProperty("name").GetString());
            Assert.InRange(hosts[0].GetProperty("awayForMinutes").GetDouble(), 11.9, 12.5);
            Assert.Equal("Answering", hosts[1].GetProperty("state").GetString());
            Assert.Equal("presence-missing-gpu-box", status.GetProperty("notices")[0].GetProperty("id").GetString());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static async Task OnDispatcher(Func<Task> action)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.UnhandledException += (_, args) =>
            {
                args.Handled = true;
                finished.TrySetException(args.Exception);
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            };
            _ = dispatcher.BeginInvoke(async () =>
            {
                try { await action(); finished.TrySetResult(); }
                catch (Exception error) { finished.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }
}
