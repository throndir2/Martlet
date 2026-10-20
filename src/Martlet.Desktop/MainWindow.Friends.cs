using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.HostClient;
using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Reading;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>
/// Sharing hosts with friends (docs/NETWORK.md). On your side, the Share page's Friends card reads the sign-in settings of
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
        WorkSharingRoster.Shared.OwnerFirst += (hostId, job) => Dispatcher.InvokeAsync(() =>
        {
            if (!closing) ActionText.Text = WorkSharingRoster.OwnerFirstText(hostId, job);
        });
        LoadShareLinks();
        RenderFriends();
        RenderSharedHosts();
    }

    /// <summary>Reads what the hosts friends share with this PC offer, once at start (their engines only).</summary>
    private void StartFriends()
    {
        if (sharedHosts.Count > 0 && sharedHostsCheckedAt is null) CheckSharedHostsAsync().Forget();
    }

    /// <summary>Called whenever the Share page draws: reads again what is stale, in the background.</summary>
    private void RefreshFriendsWhenShown()
    {
        if (!SharePage.IsVisible || closing) return;
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
                    return (host.HostId, Settings: null, Problem: error is HostConnectionException { Code: "signin.unsupported" or "request.invalid" }
                        ? "runs an older Martlet without sign-in; update it" : error.Message);
                }
            }));
            if (closing) return;
            friendsRead = results.Where(r => r.Settings is not null).ToDictionary(r => r.HostId, r => r.Settings!, StringComparer.Ordinal);
            friendsProblems = results.Where(r => r.Problem is not null).ToDictionary(r => r.HostId, r => r.Problem!, StringComparer.Ordinal);
            friendsCheckedAt = DateTimeOffset.Now;
            SaveFriendsSummary();
            LogFriends();
            // The same read shows which hosts miss a household sign-in provider; add them in the background.
            SyncHouseholdSignInAsync(friendsRead, friendsProblems).Forget();
            // ...and which hosts miss a share link this PC made (a link for all hosts reaches hosts added later).
            SyncShareLinksAsync().Forget();
        }
        catch (OperationCanceledException) { }
        finally
        {
            friendsBusy = false;
            if (!closing) RenderFriends();
        }
    }
}
