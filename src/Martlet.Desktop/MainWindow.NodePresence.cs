using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Core.Cluster;

namespace Martlet.Desktop;

/// <summary>When one of your other computers goes away or comes back. The desktop's one presence record
/// (<see cref="HostPresence"/>, fed by device sync's checks and by Thinking pool jobs) says which computers answer now;
/// <see cref="PresenceWatch"/> adds patience on top: a computer is missing after 30 seconds without an answer (one flaky
/// miss says nothing), back after it answered for 30 seconds, and stayed away after the minutes chosen in Settings › Your
/// other computers. A companion PC's Home then says "Working with less" with what that computer did for you, or that it is
/// back (for 10 minutes, or until dismissed); the Devices map says how long it hasn't answered; and
/// <see cref="PresenceChanged"/> tells the recommended setup. This PC's own host service is left out: Home has its own item
/// for it. Nothing here runs on the reply path: a 5-second timer on the UI thread, which does nothing while every computer
/// answers.</summary>
public partial class MainWindow
{
    private readonly PresenceWatch presenceWatch = new();
    private readonly DispatcherTimer presenceTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
    /// <summary>The states last written to the presence report.</summary>
    private string? presenceState;
    /// <summary>The states and minutes Home and the map last showed.</summary>
    private string? presenceShown;
    private bool presenceStarted;

    /// <summary>Raised on the UI thread, on a companion PC only, when another computer went missing, stayed away for the
    /// minutes chosen in Settings, or came back after it went missing. Each is raised once per absence.</summary>
    internal event Action<PresenceChange>? PresenceChanged;

    /// <summary>How long <paramref name="hostId"/> hasn't answered (from its first missed check, through short answers while it
    /// flaps), or null while it answers.</summary>
    internal TimeSpan? OfflineFor(string hostId)
    {
        if (!HostPresence.IsOffline(hostId)) return null;
        var since = presenceWatch.Since(hostId) ?? HostPresence.OfflineSince(hostId);
        return since is { } start ? Max(TimeSpan.Zero, HostPresence.Clock.GetUtcNow() - start) : null;
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private void InitializeNodePresence()
    {
        presenceTimer.Tick += (_, _) => ObservePresence();
        if (store is null)
        {
            PresenceAwayChoice.IsEnabled = false;
            return;
        }
        presenceWatch.AwayAfter = TimeSpan.FromMinutes(NodePresenceSettings.AwayMinutes(store.DataDirectory));
        ShowPresenceChoice();
    }

    private void StartNodePresence()
    {
        if (presenceStarted || closing) return;
        presenceStarted = true;
        HostPresence.Changed += HostPresenceChanged;
        presenceTimer.Start();
        // A new run starts with every computer answering.
        WritePresenceReport(presenceWatch.Hosts(HostPresence.Clock.GetUtcNow()));
        ObservePresence();
    }

    private void StopNodePresence()
    {
        HostPresence.Changed -= HostPresenceChanged;
        presenceTimer.Stop();
    }

    // HostPresence raises on the thread that noted the change (a Thinking pool job's thread too).
    private void HostPresenceChanged(string hostId, bool online) => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
    {
        if (!closing) ObservePresence();
    });

    private void ObservePresence() => ObservePresence(HostPresence.Clock.GetUtcNow(), null);

    /// <summary>Brings the notices up to date with <see cref="HostPresence"/> at <paramref name="now"/>, raises
    /// <see cref="PresenceChanged"/> for each change and returns them. <paramref name="paired"/> are the computers to watch;
    /// null: this PC's paired hosts other than its own host service.</summary>
    internal IReadOnlyList<PresenceChange> ObservePresence(DateTimeOffset now, IReadOnlyCollection<string>? paired)
    {
        if (closing) return [];
        var changes = new List<PresenceChange>();
        var watched = HostPresence.Offline.Union(presenceWatch.Tracked, StringComparer.Ordinal).ToList();
        if (watched.Count > 0)
        {
            paired ??= PresenceHosts();
            Dictionary<string, string>? names = null;
            foreach (var id in watched)
            {
                if (!paired.Contains(id))
                {
                    presenceWatch.Forget(id);
                    continue;
                }
                var name = (names ??= PresenceNames()).GetValueOrDefault(id, id);
                changes.AddRange(presenceWatch.Observe(id, name, HostPresence.OfflineSince(id), now));
            }
        }
        foreach (var change in changes)
        {
            ErrorLog.Info(PresenceLine(change));
            if (Role == DeviceRole.Companion) RaisePresenceChanged(change);
        }
        var hosts = presenceWatch.Hosts(now);
        var state = string.Join("|", hosts.Select(h => $"{h.HostId}={h.State}"));
        var shown = state + "#" + string.Join("|", hosts.Select(h => (int)(h.AwayFor?.TotalMinutes ?? 0)));
        if (shown != presenceShown)
        {
            presenceShown = shown;
            RenderHealth();
            if (DevicesPage.IsVisible) RenderMap();
        }
        if (state != presenceState) WritePresenceReport(hosts);
        return changes;
    }

    // Each listener in turn, so one that fails doesn't keep the others (or the notices) from going on.
    private void RaisePresenceChanged(PresenceChange change)
    {
        if (PresenceChanged is not { } changed) return;
        foreach (var listener in changed.GetInvocationList().Cast<Action<PresenceChange>>())
        {
            try { listener(change); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                ErrorLog.Warn($"Martlet couldn't act on {change.HostId} ({change.Kind}): {error.GetType().Name}.");
            }
        }
    }

    private string PresenceLine(PresenceChange change)
    {
        var who = change.Name == change.HostId ? change.HostId : $"{change.Name} ({change.HostId})";
        return change.Kind switch
        {
            PresenceChangeKind.WentMissing => $"{who} is missing: no answer since {change.At.ToLocalTime():T}. Home says Martlet works with less.",
            PresenceChangeKind.StayedAway => $"{who} stayed away for {NodePresenceNotices.Duration(presenceWatch.AwayAfter)}: " +
                "time to look for a better setup.",
            _ => $"{who} is back: it answers again since {change.At.ToLocalTime():T}."
        };
    }

    /// <summary>The paired hosts to watch: every pairing but this PC's own host service. Reads only what this PC holds in
    /// memory (not the hardware list the map reads), as it runs every 5 seconds while a computer doesn't answer.</summary>
    private HashSet<string> PresenceHosts()
    {
        var hosts = NetworkMap.Hosts(new NetworkInputs(machine, Role, null, homeAvatar, false, hostChecks, Hosts: homeHosts));
        var own = hosts.FirstOrDefault(h => h.Method == HostSetupMethod.ThisPcDocker)?.HostId
            ?? hosts.FirstOrDefault(h => machine.LanAddress is { } address && h.Address == address)?.HostId;
        return hosts.Select(h => h.HostId).Where(id => id != own).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Each host's computer name, when one of your Martlet computers runs it ("DIVA" for diva-host), as the map
    /// names it.</summary>
    private Dictionary<string, string> PresenceNames()
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var computer in OtherComputers())
            names.TryAdd(computer.HostId ?? HostSetupCommands.SuggestedHostId(computer.Name), computer.Name);
        return names;
    }

    /// <summary>Home's presence items: "Working with less: gpu-box isn't answering" (a warning, with what it did for you) and
    /// "gpu-box is back" (good to know, until dismissed or for 10 minutes).</summary>
    internal IReadOnlyList<HealthIssue> PresenceIssues()
    {
        var hosts = presenceWatch.Hosts(HostPresence.Clock.GetUtcNow());
        var plan = clusterEnabled ? clusterPlan : null;
        var away = hosts.Where(h => h.State is NodePresenceState.Missing or NodePresenceState.Away or NodePresenceState.Returning)
            .Select(h => h.HostId).ToList();
        var issues = new List<HealthIssue>();
        foreach (var host in hosts)
        {
            var id = host.HostId;
            var show = new HealthFix("show", "Show on the map", () => ShowDevice("host:" + id), Passive: true);
            switch (host.State)
            {
                case NodePresenceState.Missing or NodePresenceState.Away or NodePresenceState.Returning:
                    issues.Add(new(NodePresenceNotices.MissingId(id), HealthLevel.Warning, NodePresenceNotices.MissingTitle(host.Name),
                        NodePresenceNotices.MissingDetail(id, plan, host.AwayFor ?? TimeSpan.Zero, away.Where(a => a != id).ToList()) +
                        (host.State == NodePresenceState.Returning ? " It answers again; Martlet checks that it stays." : ""),
                        [new("check", $"Check {host.Name} now", () => CheckPresenceHost(id)), show]));
                    break;
                case NodePresenceState.Back:
                    issues.Add(new(NodePresenceNotices.BackId(id), HealthLevel.Notice, NodePresenceNotices.BackTitle(host.Name),
                        NodePresenceNotices.BackDetail(id, plan, host.AwayFor ?? TimeSpan.Zero),
                        [show, new("dismiss", "Dismiss", () => DismissPresenceBack(id), Passive: true)]));
                    break;
            }
        }
        return issues;
    }

    /// <summary>Whether the presence notices speak for <paramref name="hostId"/>, so Home doesn't add its plain "isn't
    /// answering" item too: while device sync's checks feed <see cref="HostPresence"/>, a missed check waits 30 seconds.</summary>
    private bool PresenceCovers(string hostId) => HostPresence.IsOffline(hostId) || presenceWatch.Since(hostId) is not null;

    private void CheckPresenceHost(string hostId)
    {
        if (NetworkMap.Hosts(Inputs()).FirstOrDefault(h => h.HostId == hostId) is { } host) CheckHostsAsync([host]).Forget();
    }

    private void DismissPresenceBack(string hostId)
    {
        if (!presenceWatch.DismissBack(hostId)) return;
        presenceShown = null;
        ObservePresence();
    }

    /// <summary>"Not answering for 3 min" on the Devices map, for each paired host that hasn't answered for a minute or more.</summary>
    private IReadOnlyDictionary<string, TimeSpan>? PresenceAway()
    {
        var offline = HostPresence.Offline;
        if (offline.Count == 0) return null;
        var away = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
        foreach (var id in offline)
            if (OfflineFor(id) is { } span) away[id] = span;
        return away;
    }

    /// <summary>Writes node-presence.json for node_presence_status. A failed write only costs that status.</summary>
    private void WritePresenceReport(IReadOnlyList<NodePresenceHost> hosts)
    {
        presenceState = string.Join("|", hosts.Select(h => $"{h.HostId}={h.State}"));
        if (store is null) return;
        try
        {
            new NodePresenceReport
            {
                UpdatedAt = HostPresence.Clock.GetUtcNow(), AwayMinutes = (int)presenceWatch.AwayAfter.TotalMinutes,
                Companion = Role == DeviceRole.Companion, Hosts = hosts,
                Notices = [.. PresenceIssues().Select(i => new NodePresenceNotice(i.Id, i.Level.ToString(), i.Title, i.Detail))]
            }.Save(store.DataDirectory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Warn($"Couldn't save {NodePresenceReport.FileName}: {error.Message}");
        }
    }

    // ---------- Settings › Your other computers ----------

    private void ShowPresenceChoice()
    {
        var minutes = (int)presenceWatch.AwayAfter.TotalMinutes;
        PresenceAwayChoice.SelectionChanged -= PresenceAway_Changed;
        PresenceAwayChoice.Items.Clear();
        foreach (var choice in NodePresenceSettings.Choices.Append(minutes).Distinct().Order())
            PresenceAwayChoice.Items.Add(new ComboBoxItem { Content = choice.ToString(System.Globalization.CultureInfo.InvariantCulture), Tag = choice });
        PresenceAwayChoice.SelectedItem = PresenceAwayChoice.Items.OfType<ComboBoxItem>().First(i => (int)i.Tag == minutes);
        PresenceAwayChoice.SelectionChanged += PresenceAway_Changed;
    }

    private void PresenceAway_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (store is null || PresenceAwayChoice.SelectedItem is not ComboBoxItem { Tag: int minutes } ||
            TimeSpan.FromMinutes(minutes) == presenceWatch.AwayAfter)
            return;
        try { NodePresenceSettings.SaveAwayMinutes(store.DataDirectory, minutes); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ActionText.Text = $"Couldn't save that choice: {error.Message}";
            ShowPresenceChoice();
            return;
        }
        presenceWatch.AwayAfter = TimeSpan.FromMinutes(minutes);
        ActionText.Text = $"Martlet looks for a better setup when a computer is away for {minutes} minutes.";
        presenceState = null;
        ObservePresence();
    }
}
