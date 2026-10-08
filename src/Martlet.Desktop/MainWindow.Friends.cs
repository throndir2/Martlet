using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Reading;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>
/// Sharing hosts with friends (docs/NETWORK.md). On your side, the Devices page's Friends card reads the sign-in settings of
/// each of your hosts (only when the page shows, at most every two minutes, or on Check now; never on a reply's path) and lists
/// each person with the hosts shared with them, with Share and Stop sharing per host. On a friend's side, Hosts shared with this
/// PC lists the hosts this PC signed in to as a friend: what they offer this PC, which jobs this PC uses them for, and Check and
/// Forget. Nothing between your own computers ever talks to a shared host; its engines are used only for this PC's jobs.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan FriendsStale = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SharedHostsStale = TimeSpan.FromMinutes(5);
    private Dictionary<string, HostSignInSettings> friendsRead = new(StringComparer.Ordinal);
    private Dictionary<string, string> friendsProblems = new(StringComparer.Ordinal);
    private DateTimeOffset? friendsCheckedAt;
    private bool friendsBusy;
    private DateTimeOffset? sharedHostsCheckedAt;
    private bool sharedHostsBusy;

    private void InitializeFriends()
    {
        // A host a friend shares turned this PC away for its owner's own work: say so once, in plain words.
        WorkSharingRoster.OwnerFirst += (hostId, job) => Dispatcher.InvokeAsync(() =>
        {
            if (!closing) ActionText.Text = WorkSharingRoster.OwnerFirstText(hostId, job);
        });
        RenderFriends();
        RenderSharedHosts();
    }

    /// <summary>Reads what the hosts friends share with this PC offer, once at start (their engines only).</summary>
    private void StartFriends()
    {
        if (sharedHosts.Count > 0 && sharedHostsCheckedAt is null) CheckSharedHostsAsync().Forget();
    }

    /// <summary>Called whenever the Devices page draws: reads again what is stale, in the background.</summary>
    private void RefreshFriendsWhenShown()
    {
        if (!DevicesPage.IsVisible || closing) return;
        if (homeHosts.Count > 0 && (friendsCheckedAt is not { } read || DateTimeOffset.Now - read > FriendsStale))
            ReadFriendsAsync().Forget();
        if (sharedHosts.Count > 0 && (sharedHostsCheckedAt is not { } checkedAt || DateTimeOffset.Now - checkedAt > SharedHostsStale))
            CheckSharedHostsAsync().Forget();
    }

    // ---------- your hosts shared with friends ----------

    private void FriendsCheck_Click(object sender, RoutedEventArgs e) => ReadFriendsAsync().Forget();

    /// <summary>Your own hosts whose sign-in settings this PC may read: every paired host of your network (a member desktop),
    /// or every paired host while this PC is in no network.</summary>
    private IReadOnlyList<PairedHost> FriendsHosts() => networkState.Roster is { } roster
        ? homeHosts.Where(h => roster.Host(h.HostId) is { Removed: false }).ToArray()
        : homeHosts;

    private async Task ReadFriendsAsync()
    {
        if (friendsBusy || closing || store is null) return;
        friendsBusy = true;
        FriendsStatusText.Text = "Reading who your hosts are shared with...";
        try
        {
            var hosts = FriendsHosts();
            var results = await Task.WhenAll(hosts.Select(async host =>
            {
                try
                {
                    using var connection = ClusterSync.Connect(host.Pairing);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(15));
                    return (host.HostId, Settings: await connection.ReadSignInSettingsAsync(timeout.Token), Problem: (string?)null);
                }
                catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
                {
                    return (host.HostId, Settings: (HostSignInSettings?)null, Problem: "didn't answer in time");
                }
                catch (Exception error) when (ClusterSync.IsHostFailure(error))
                {
                    return (host.HostId, Settings: null, Problem: error is Audio2FaceHostException { Code: "signin.unsupported" or "request.invalid" }
                        ? "runs an older Martlet without sign-in; update it" : error.Message);
                }
            }));
            if (closing) return;
            friendsRead = results.Where(r => r.Settings is not null).ToDictionary(r => r.HostId, r => r.Settings!, StringComparer.Ordinal);
            friendsProblems = results.Where(r => r.Problem is not null).ToDictionary(r => r.HostId, r => r.Problem!, StringComparer.Ordinal);
            friendsCheckedAt = DateTimeOffset.Now;
            SaveFriendsSummary();
            LogFriends();
        }
        catch (OperationCanceledException) { }
        finally
        {
            friendsBusy = false;
            if (!closing) RenderFriends();
        }
    }

    private void SaveFriendsSummary()
    {
        if (store is null) return;
        try { FriendsOverview.SaveSummary(store.DataDirectory, friendsRead, friendsProblems, friendsCheckedAt ?? DateTimeOffset.Now); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Warn("Friends: couldn't keep the summary for MCP: " + error.Message);
        }
    }

    private string? friendsLogged;

    /// <summary>Writes who each host is shared with to the desktop log when it changes (names and counts only).</summary>
    private void LogFriends()
    {
        var friends = FriendsOverview.Build(friendsRead);
        var picture = string.Join("; ", friends.Select(f => $"{f.Name} ({f.Provider}): " + (f.SharedOn.Any()
            ? "shares " + string.Join(", ", f.SharedOn) + $", {f.Hosts.Sum(h => h.Computers.Count)} computer(s) signed in"
            : "asked to use " + string.Join(", ", f.Hosts.Where(h => h.State == FriendHostState.Asked).Select(h => h.HostId)))));
        if (picture == friendsLogged) return;
        friendsLogged = picture;
        ErrorLog.Info("Friends: " + (picture.Length == 0 ? "no host is shared with a friend." : picture + "."));
    }

    private void RenderFriends()
    {
        var hosts = FriendsHosts();
        FriendsCard.Visibility = homeHosts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FriendsCheckButton.IsEnabled = hosts.Count > 0 && !friendsBusy;
        FriendsPanel.Children.Clear();
        if (friendsCheckedAt is null)
        {
            FriendsStatusText.Text = hosts.Count == 0 ? FriendsOverview.Status([], 0, 0) : "Not read yet.";
            return;
        }
        var friends = FriendsOverview.Build(friendsRead);
        FriendsStatusText.Text = FriendsOverview.Status(friends, friendsRead.Count, friendsRead.Count + friendsProblems.Count) +
            $" Checked {friendsCheckedAt:t}.";
        foreach (var friend in friends) FriendsPanel.Children.Add(FriendRow(friend));
        foreach (var (hostId, problem) in friendsProblems.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var row = NetworkRowFrame();
            row.Children.Add(NetworkRowText("FriendsHost-" + hostId, hostId, $"Couldn't read who {hostId} is shared with: {problem}."));
            FriendsPanel.Children.Add(NetworkRowCard(row, warning: false));
        }
    }

    /// <summary>One person: who they are, the hosts shared with them and their computers there, and Share or Stop sharing for
    /// each of your hosts where their provider is set up.</summary>
    private FrameworkElement FriendRow(FriendView friend)
    {
        var row = NetworkRowFrame();
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 360 };
        foreach (var place in friend.Hosts)
        {
            if (place.State == FriendHostState.Member || place.State != FriendHostState.Shared && !place.ProviderReady) continue;
            var share = place.State != FriendHostState.Shared;
            var button = new Button { Content = share ? $"Share {place.HostId}" : $"Stop sharing {place.HostId}", Margin = new Thickness(8, 4, 0, 0) };
            if (share && place.State == FriendHostState.Asked) button.SetResourceReference(StyleProperty, "PrimaryButton");
            AutomationProperties.SetAutomationId(button, (share ? "FriendShare-" : "FriendStop-") + place.HostId + "-" + friend.Key);
            AutomationProperties.SetName(button, share ? $"Share {place.HostId} with {friend.Name}" : $"Stop sharing {place.HostId} with {friend.Name}");
            button.Click += (_, _) => ShareWithFriendAsync(friend, place.HostId, share).Forget();
            buttons.Children.Add(button);
        }
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        var parts = new List<string>();
        if (friend.SharedOn.Any()) parts.Add($"Shares {string.Join(", ", friend.SharedOn)}: their engines only.");
        var computers = friend.Hosts.SelectMany(h => h.Computers.Select(c => $"{c.DeviceId} on {h.HostId} (signed in {c.EnrolledAt.ToLocalTime():g})")).ToArray();
        if (computers.Length > 0) parts.Add("Their computers: " + string.Join(", ", computers) + ".");
        else if (friend.SharedOn.Any()) parts.Add("No computer of theirs has signed in yet: send them the host's invite (Sign-in from outside).");
        foreach (var asked in friend.Hosts.Where(h => h.State == FriendHostState.Asked))
            parts.Add($"Signed in to {asked.HostId} {asked.AskedAt?.ToLocalTime():g} and waits for you to share it.");
        foreach (var member in friend.Hosts.Where(h => h.State == FriendHostState.Member))
            parts.Add($"One of your own computers' sign-ins on {member.HostId} (change it in that host's Sign-in from outside).");
        var unable = friend.Hosts.Where(h => h.State is FriendHostState.NotShared && !h.ProviderReady).Select(h => h.HostId).ToArray();
        if (unable.Length > 0) parts.Add($"Set up {friend.Provider} sign-in on {string.Join(", ", unable)} to share {(unable.Length == 1 ? "it" : "them")} too.");
        row.Children.Add(NetworkRowText("Friend-" + friend.Key, $"{friend.Name} ({friend.Provider})", string.Join(" ", parts)));
        return NetworkRowCard(row, warning: friend.Asking && !friend.SharedOn.Any());
    }

    /// <summary>Shares <paramref name="hostId"/> with a person (allows their identity there as a friend) or stops sharing it
    /// (removes it, so the host revokes their computers at once), after one confirmation.</summary>
    private async Task ShareWithFriendAsync(FriendView friend, string hostId, bool share)
    {
        if (store is null || closing || FriendsHosts().FirstOrDefault(h => h.HostId == hostId) is not { } host) return;
        var question = share
            ? $"Share {hostId} with {friend.Name} ({friend.Provider})? Once they sign in there with the host's invite, their Martlet may use " +
              $"{hostId}'s engines: thinking, listening, speaking, lip-sync and reading. They never join your Martlet network and never see " +
              "your settings, memories, voices, logs, API keys or other computers. Your own work always goes first: theirs stops when yours " +
              "needs the graphics card."
            : $"Stop sharing {hostId} with {friend.Name}? {hostId} revokes their computers at once, and a request of theirs that is running stops.";
        if (!ConfirmationDialog.Confirm(this, question, share ? $"Share {hostId}" : "Stop sharing")) return;
        JsonObject change = share
            ? new() { ["action"] = "allow", ["provider"] = friend.Provider, ["subject"] = friend.Subject, ["label"] = friend.Label, ["access"] = HostSignInAccess.Friend }
            : new() { ["action"] = "disallow", ["provider"] = friend.Provider, ["subject"] = friend.Subject };
        try
        {
            using var connection = ClusterSync.Connect(host.Pairing);
            friendsRead[hostId] = await connection.ChangeSignInSettingsAsync(change, lifetime.Token);
            friendsProblems.Remove(hostId);
            friendsCheckedAt = DateTimeOffset.Now;
            SaveFriendsSummary();
            LogFriends();
            ActionText.Text = share
                ? $"{hostId} is shared with {friend.Name}. Send them its invite (Sign-in from outside › Make invite) if they don't have it."
                : $"Stopped sharing {hostId} with {friend.Name}; their computers lost access there.";
            ErrorLog.Info($"Friends: {(share ? "shared" : "stopped sharing")} {hostId} {(share ? "with" : "with")} {friend.Name} ({friend.Provider}).");
            // The host's network answer marks (or no longer lists) their computers on the next sync.
            QueueNetworkSync();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (ClusterSync.IsHostFailure(error))
        {
            ActionText.Text = $"Couldn't change {hostId}'s sharing: {error.Message}";
        }
        finally { if (!closing) RenderFriends(); }
    }

    // ---------- hosts friends share with this PC ----------

    private void SharedHostsCheck_Click(object sender, RoutedEventArgs e) => CheckSharedHostsAsync().Forget();

    /// <summary>Reads what each host a friend shares offers this PC: its capabilities only (never its hardware, network or
    /// anything else of its owner's).</summary>
    private async Task CheckSharedHostsAsync()
    {
        if (sharedHostsBusy || closing || sharedHosts.Count == 0) return;
        sharedHostsBusy = true;
        var hosts = sharedHosts;
        foreach (var host in hosts) hostChecks[host.HostId] = hostChecks.GetValueOrDefault(host.HostId) ?? new(null, "Checking...");
        RenderSharedHosts();
        try
        {
            var results = await Task.WhenAll(hosts.Select(async h => (h.HostId, Check: await HostControl.CheckAsync(h.Pairing, null, lifetime.Token, shared: true))));
            if (closing) return;
            foreach (var (id, check) in results)
            {
                var before = hostChecks.GetValueOrDefault(id);
                hostChecks[id] = check;
                if (before?.Reachable != check.Reachable || before?.Code != check.Code)
                    ErrorLog.Info($"Shared hosts: {id} " + (check.Reachable == true ? $"answers; it offers this PC: {SharedOffers(check)}."
                        : check.Code == "auth.revoked" ? "says its owner stopped sharing it with this PC (auth.revoked)."
                        : $"didn't answer ({check.Text})."));
            }
            sharedHostsCheckedAt = DateTimeOffset.Now;
            // A model its owner changed there is followed, as for your own hosts (this PC's routes only; nothing is recorded).
            FollowHostModelsAsync(results.Where(r => r.Check.Reachable == true).Select(r => r.HostId).ToArray()).Forget();
        }
        catch (OperationCanceledException) { }
        finally
        {
            sharedHostsBusy = false;
            if (!closing)
            {
                RenderSharedHosts();
                if (DevicesPage.IsVisible) RenderMap();
            }
        }
    }

    /// <summary>The engines a shared host offers this PC, in words.</summary>
    private static string SharedOffers(HostCheck check) => check.Offers is not { Count: > 0 } offers ? "nothing yet"
        : HostRoles.Names(HostRoles.All.Where(r => offers.ContainsKey(r.Kind)).Select(r => r.Kind));

    /// <summary>The jobs this PC uses a shared host for, in words ("thinking and lip-sync").</summary>
    private IReadOnlyList<string> SharedUses(string hostId)
    {
        var uses = HostJob.All.Where(j => NetworkMap.JobHost(homeSettings, j.Role) == hostId).Select(j => j.Job).ToList();
        if (NetworkMap.LipSync(homeAvatar) == LipSyncHandler.Host && homeAvatar?.RemoteHost?.HostId == hostId) uses.Add("lip-sync");
        if (store is not null && ReadingSettings.Load(store.DataDirectory) is { Place: ReadingPlace.Host } reading && reading.HostId == hostId) uses.Add("reading");
        return uses;
    }

    private void RenderSharedHosts()
    {
        SharedHostsCard.Visibility = sharedHosts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SharedHostsCheckButton.IsEnabled = !sharedHostsBusy;
        SharedHostsPanel.Children.Clear();
        if (sharedHosts.Count == 0) return;
        SharedHostsStatusText.Text = (sharedHosts.Count == 1 ? "1 host a friend shares with this PC." : $"{sharedHosts.Count} hosts friends share with this PC.") +
            " Martlet uses them only on this PC and only for their engines; they never join your Martlet network, and their owners' own " +
            "work comes first." + (sharedHostsCheckedAt is { } at ? $" Checked {at:t}." : "");
        foreach (var host in sharedHosts) SharedHostsPanel.Children.Add(SharedHostRow(host));
    }

    private FrameworkElement SharedHostRow(PairedHost host)
    {
        var row = NetworkRowFrame();
        var check = hostChecks.GetValueOrDefault(host.HostId);
        var uses = SharedUses(host.HostId);
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 380 };
        void Add(string id, string label, Action run, bool primary = false)
        {
            var button = new Button { Content = label, Margin = new Thickness(8, 4, 0, 0) };
            if (primary) button.SetResourceReference(StyleProperty, "PrimaryButton");
            AutomationProperties.SetAutomationId(button, id);
            AutomationProperties.SetName(button, label + " (" + host.HostId + ")");
            button.Click += (_, _) => run();
            buttons.Children.Add(button);
        }
        if (check?.Reachable == true && check.Offers is { } offers)
        {
            // Use it for a job this PC doesn't use it for yet, among the engines its owner offers.
            if (offers.ContainsKey(HostRoles.Ollama) && !uses.Contains("thinking"))
                Add($"SharedHostUse-{host.HostId}-thinking", "Use for thinking", () => AssignJobAsync(HostJob.Thinking, "host:" + host.HostId).Forget());
            if (offers.ContainsKey(HostRoles.Stt) && !uses.Contains("listening"))
                Add($"SharedHostUse-{host.HostId}-listening", "Use for listening", () => AssignJobAsync(HostJob.Listening, "host:" + host.HostId).Forget());
            if (offers.Keys.Any(HostRoles.Speaks) && !uses.Contains("speaking"))
                Add($"SharedHostUse-{host.HostId}-speaking", "Use for speaking", () => AssignJobAsync(HostJob.Speaking, "host:" + host.HostId).Forget());
            if (offers.ContainsKey(HostRoles.Audio2Face) && !uses.Contains("lip-sync"))
                Add($"SharedHostUse-{host.HostId}-lip-sync", "Use for lip-sync", () => AssignLipSyncAsync("host:" + host.HostId).Forget());
            if (offers.ContainsKey(HostRoles.Ocr) && !uses.Contains("reading"))
                Add($"SharedHostUse-{host.HostId}-reading", "Use for reading", () => UseReadingHost(host.HostId, host.HostId));
        }
        Add("SharedHostCheck-" + host.HostId, "Check", () => CheckSharedHostsAsync().Forget());
        Add("SharedHostForget-" + host.HostId, "Forget", () => ForgetSharedHostAsync(host).Forget());
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        var state = check switch
        {
            null => "Not checked yet.",
            { Reachable: null } => "Checking...",
            { Reachable: true } found => $"Offers this PC: {SharedOffers(found)}.",
            { Code: "auth.revoked" } => "Its owner stopped sharing it with this PC. Forget it, or sign in again with a new invite from them.",
            _ => $"Not reachable right now ({check.Text})."
        };
        var detail = $"Shared by a friend{(host.SignedInAs is { } who ? $"; you signed in as {who}" : "")}. {state} " +
            (uses.Count > 0 ? $"This PC uses it for {string.Join(", ", uses)}." : "This PC doesn't use it for anything yet.") +
            (host.OutsideAddresses is { Count: > 0 } outside ? $" Reached at {outside.Count} outside address{(outside.Count == 1 ? "" : "es")} from the invite." : "");
        row.Children.Add(NetworkRowText("SharedHost-" + host.HostId, host.HostId, detail));
        return NetworkRowCard(row, warning: check is { Reachable: false });
    }

    /// <summary>Forgets a host a friend shares: this PC stops using it (its jobs go back to what they used before, without
    /// changing your shared plan) and its pairing goes. The host still lists this PC until its owner stops sharing it.</summary>
    private async Task ForgetSharedHostAsync(PairedHost host)
    {
        var uses = SharedUses(host.HostId);
        if (!ConfirmationDialog.Confirm(this,
                $"Forget {host.HostId}? This PC stops using it" + (uses.Count > 0 ? $" for {string.Join(", ", uses)}" : "") +
                " and removes its pairing. Its owner still lists this PC until they stop sharing it with you.", "Forget host"))
            return;
        await HostTaskAsync(async token =>
        {
            if (store is not null && ReadingSettings.Load(store.DataDirectory) is { Place: ReadingPlace.Host } reading && reading.HostId == host.HostId)
                SaveReading(new ReadingSettings { Place = ReadingPlace.ThisPc, ChosenAt = DateTimeOffset.Now },
                    "Martlet now reads the text on your screen with Windows OCR on this PC.");
            var stranded = await ForgetPairingAsync(host, token);
            ActionText.Text = $"Forgot {host.HostId}, the host a friend shared." +
                (stranded.Count > 0 ? $" Nobody handles {string.Join(" or ", stranded)} now. Choose another device in Companion or Devices." : "");
            ErrorLog.Info($"Shared hosts: forgot {host.HostId}.");
        });
        await RefreshHomeAsync();
    }
}
