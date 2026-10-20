using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.HostClient;
using Martlet.Core.Contracts;
using Martlet.Core.Network;

namespace Martlet.Desktop;

/// <summary>
/// Sharing with a link (docs/NETWORK.md, "Sharing with a link"). On your side, Share › Friends › Make a share link makes one
/// link for a friend that covers the hosts you choose, or all of your hosts (also those you add later): the link's verifier goes
/// to each host, its secret stays in Windows Credential Manager so you can copy the link again, and each time the Friends card
/// reads your hosts it adds the link to hosts that miss it. On a friend's side, clicking the link (martlet://share/...) or pasting
/// it signs in to each host it names as a friend and keeps them under Hosts shared with this PC; later checks ask a host which
/// hosts the link covers now and sign in to new ones. API keys never travel: a friend's Martlet uses its own.
/// </summary>
public partial class MainWindow
{
    private ShareLinksDocument shareLinks = new();
    private DateTimeOffset? shareLinksDiscoveredAt;
    private bool shareLinksBusy;

    private void LoadShareLinks()
    {
        if (store is not null) shareLinks = ShareLinks.Load(store.DataDirectory);
    }

    private void SaveShareLinks(ShareLinksDocument next)
    {
        shareLinks = next;
        if (store is null) return;
        try { ShareLinks.Save(store.DataDirectory, next); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Warn("Share links: couldn't save share-links.json: " + error.Message);
        }
    }

    // ---------- your side: make, copy and stop share links ----------

    private void FriendsMakeShareLink_Click(object sender, RoutedEventArgs e) => MakeShareLinkAsync().Forget();

    /// <summary>Asks for the friend's name and the hosts to share (all of them by default, also those added later), keeps the
    /// link's verifier on each of those hosts and shows the link to copy.</summary>
    private async Task MakeShareLinkAsync()
    {
        if (store is null || closing) return;
        var hosts = FriendsHosts();
        if (hosts.Count == 0) { ActionText.Text = "Pair a host first: a share link shares your hosts' engines."; return; }
        if (shareLinks.Made.Count >= ShareLinks.MaximumMade) { ActionText.Text = $"You have {ShareLinks.MaximumMade} share links: stop one first."; return; }
        var dialog = new HostInputDialog("Share link", "Share your hosts with a friend",
            "Anyone who has the link can use your chosen hosts' engines, but never your settings, memories or API keys, so send it only to your friend.",
            "_Make link");
        dialog.AddText("name", "Your friend's name", "", "Shown here and to your friend, for example Ana.", maxLength: 64);
        dialog.AddText("from", "Your name, as your friend sees it", Environment.UserName, optional: true, maxLength: 64);
        dialog.AddCheck("all", "All my hosts, also the ones I add later", true);
        dialog.AddNote("ShareLinkHostsNote", "Or clear the box above and choose the hosts:");
        foreach (var host in hosts) dialog.AddCheck("host:" + host.HostId, HostLabel(host), false);
        if (dialog.Ask(this) is not { } values) return;
        var all = values["all"] == "yes";
        var chosen = all ? hosts : hosts.Where(h => values.GetValueOrDefault("host:" + h.HostId) == "yes").ToArray();
        if (chosen.Count == 0) { ActionText.Text = "Choose at least one host to share, or All my hosts."; return; }
        var made = new MadeShareLink
        {
            Id = ShareLink.NewId(), Label = ShareLink.Clean(values["name"]), From = ShareLink.Clean(values.GetValueOrDefault("from")),
            Hosts = all ? null : chosen.Select(h => h.HostId).ToList(), CreatedAt = DateTimeOffset.Now
        };
        var secret = ShareLink.NewSecret();
        try { ShareLinks.KeepSecret(made.Id, secret); }
        catch (InvalidOperationException error) { ActionText.Text = error.Message; return; }
        SaveShareLinks(shareLinks with { Made = [.. shareLinks.Made, made] });
        ActionText.Text = $"Adding the share link for {made.Name} to {chosen.Count} host(s)...";
        var problems = await PushShareLinkAsync(made, secret, chosen);
        ErrorLog.Info($"Share links: made a link for {made.Name} covering {(all ? "all hosts" : string.Join(", ", made.Hosts!))}; " +
            $"{chosen.Count - problems.Count} host(s) took it" + (problems.Count > 0 ? $", {string.Join("; ", problems.Select(p => $"{p.Key}: {p.Value}"))}." : "."));
        ShowShareLink(made, secret, problems);
        ReadFriendsAsync().Forget();
    }

    private static string HostLabel(PairedHost host) => host.Method == HostSetupMethod.ThisPcDocker ? $"{host.HostId} (this PC)" : host.HostId;

    private NetworkInvite Invite(PairedHost host) =>
        ShareLinks.Invite(host, networkState.Roster, networkViews.GetValueOrDefault(host.HostId)?.AdvertisedAddresses);

    /// <summary>Sends <paramref name="made"/>'s verifier to each host; returns the hosts that didn't take it, with why.</summary>
    private async Task<Dictionary<string, string>> PushShareLinkAsync(MadeShareLink made, string secret, IReadOnlyList<PairedHost> hosts)
    {
        var problems = new Dictionary<string, string>(StringComparer.Ordinal);
        await Task.WhenAll(hosts.Select(async host =>
        {
            try
            {
                using var connection = ClusterSync.Connect(host.Pairing);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                var settings = await connection.ChangeSignInSettingsAsync(ShareLinks.AddChange(made, secret), timeout.Token);
                if (!closing) friendsRead[host.HostId] = settings;
            }
            catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { problems[host.HostId] = "didn't answer in time"; }
            catch (Exception error) when (ClusterSync.IsHostFailure(error))
            {
                problems[host.HostId] = error is HostConnectionException { Code: "request.invalid" or "signin.unsupported" or "signin.unavailable" }
                    ? "runs an older Martlet without share links; update it" : error.Message;
            }
        }));
        return problems;
    }

    /// <summary>Shows the link with the hosts it carries now (their current outside addresses) to copy.</summary>
    private void ShowShareLink(MadeShareLink made, string secret, IReadOnlyDictionary<string, string>? problems = null)
    {
        var hosts = FriendsHosts().Where(h => made.Covers(h.HostId) && problems?.ContainsKey(h.HostId) != true &&
            (!friendsRead.TryGetValue(h.HostId, out var read) || read.Links?.Any(l => l.Id == made.Id) == true)).Take(ShareLink.MaximumHosts).ToArray();
        if (hosts.Length == 0)
        {
            ActionText.Text = $"None of your hosts keeps the share link for {made.Name} yet" +
                (problems is { Count: > 0 } ? $" ({string.Join("; ", problems.Select(p => $"{p.Key} {p.Value}"))})" : "") +
                "; Martlet tries again each time the Friends card checks your hosts, so copy the link later.";
            return;
        }
        var link = new ShareLink
        {
            Id = made.Id, Secret = secret, NetworkId = networkState.Roster?.NetworkId, From = made.From ?? ShareLink.Clean(Environment.UserName),
            Label = made.Label, Hosts = hosts.Select(Invite).ToArray()
        };
        var unreachable = ShareLinks.Unreachable(link);
        var warnings = new List<string>();
        if (unreachable.Count > 0)
            warnings.Add($"{string.Join(", ", unreachable)} {(unreachable.Count == 1 ? "has" : "have")} no outside address, so your friend can " +
                "reach " + (unreachable.Count == 1 ? "it" : "them") + " only on your home network; set up Outside access on the Devices map, then copy the link again");
        if (problems is { Count: > 0 })
            warnings.Add((warnings.Count > 0 ? "not" : "Not") + " in the link yet: " + string.Join(", ", problems.Select(p => $"{p.Key} {p.Value}")));
        new ShareLinkWindow($"Share link for {made.Name}",
            $"It shares {(made.AllHosts ? "all of your hosts, also the ones you add later" : string.Join(", ", made.Hosts!))}; send it to " +
            $"{made.Name} in a private message, and they click it or paste it into their Martlet.", link.Url(),
            warnings.Count > 0 ? string.Join("; ", warnings) + "." : null)
        { Owner = this }.ShowDialog();
    }

    private void CopyShareLink(FriendView friend)
    {
        if (shareLinks.Made.FirstOrDefault(l => l.Id == friend.Subject) is not { } made || ShareLinks.Secret(made.Id) is not { } secret)
        {
            ActionText.Text = $"This PC doesn't have the link for {friend.Name} because another computer made it: copy it there, or stop it and make a new one.";
            return;
        }
        ShowShareLink(made, secret);
    }

    /// <summary>Stops a share link everywhere: this PC marks it stopped at once (so it is never added again, also by a later
    /// check), every host removes it (and revokes the computers that signed in with it at once) and remembers that it was
    /// stopped, so none of your other computers adds it again either. A host that doesn't answer now is told on the next check.</summary>
    private async Task StopShareLinkAsync(FriendView friend)
    {
        if (store is null || closing) return;
        if (!ConfirmationDialog.Confirm(this, $"Stop the share link for {friend.Name}? Their computers lose access to all your hosts at once, " +
                "and the link stops working everywhere.", "Stop share link"))
            return;
        SaveShareLinks(shareLinks with
        {
            Made = shareLinks.Made.Where(l => l.Id != friend.Subject).ToList(),
            Stopped = shareLinks.Stopped.Where(id => id != friend.Subject).Append(friend.Subject).TakeLast(ShareLinks.MaximumStopped).ToList()
        });
        ShareLinks.ForgetSecret(friend.Subject);
        var problems = new List<string>();
        // Every host, whatever this PC last read there: removing a link a host doesn't keep changes nothing but remembers it as stopped.
        await Task.WhenAll(FriendsHosts().Select(async host =>
        {
            try
            {
                using var connection = ClusterSync.Connect(host.Pairing);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                var settings = await connection.ChangeSignInSettingsAsync(ShareLinks.RemoveChange(friend.Subject), timeout.Token);
                lock (problems) friendsRead[host.HostId] = settings;
            }
            catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { lock (problems) problems.Add($"{host.HostId} didn't answer"); }
            // Hosts older than share links never kept it.
            catch (HostConnectionException error) when (error.Code is "request.invalid" or "signin.unsupported") { }
            catch (Exception error) when (ClusterSync.IsHostFailure(error)) { lock (problems) problems.Add($"{host.HostId}: {error.Message}"); }
        }));
        if (closing) return;
        ActionText.Text = problems.Count == 0
            ? $"Stopped the share link for {friend.Name}; their computers lost access to your hosts."
            : $"Stopped the share link for {friend.Name} where your hosts answered; Martlet removes it from the others when they answer " +
              $"({string.Join("; ", problems)}).";
        ErrorLog.Info($"Share links: stopped the link for {friend.Name}" + (problems.Count > 0 ? $"; not yet on {string.Join("; ", problems)}." : "."));
        SaveFriendsSummary();
        RenderFriends();
    }
}
