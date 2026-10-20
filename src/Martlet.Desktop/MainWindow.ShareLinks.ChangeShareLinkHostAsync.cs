using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.HostClient;
using Martlet.Core.Contracts;
using Martlet.Core.Network;

namespace Martlet.Desktop;

/// <summary>More of MainWindow.ShareLinks.cs, in a file of its own so that each file stays under 20 KB.</summary>
public partial class MainWindow
{
    /// <summary>Adds a host to a link that names its hosts, or takes one out of it; the next check sends the change to every host.</summary>
    private async Task ChangeShareLinkHostAsync(FriendView friend, string hostId, bool share)
    {
        if (shareLinks.Made.FirstOrDefault(l => l.Id == friend.Subject) is not { Hosts: { } listed } made) return;
        var question = share
            ? $"Share {hostId} with {friend.Name} too?"
            : $"Stop sharing {hostId} with {friend.Name}? Their computers lose access to it at once.";
        if (!ConfirmationDialog.Confirm(this, question, share ? $"Share {hostId}" : "Stop sharing")) return;
        var hosts = share ? listed.Append(hostId).Distinct(StringComparer.Ordinal).ToList() : listed.Where(h => h != hostId).ToList();
        if (hosts.Count == 0)
        {
            await StopShareLinkAsync(friend);
            return;
        }
        SaveShareLinks(shareLinks with { Made = shareLinks.Made.Select(l => l.Id == made.Id ? l with { Hosts = hosts } : l).ToList() });
        await SyncShareLinksAsync();
        ActionText.Text = share ? $"{hostId} is shared with {friend.Name} too." : $"Stopped sharing {hostId} with {friend.Name}.";
        RenderFriends();
    }

    /// <summary>After the Friends card read your hosts: sends each host the share links this PC made that should be there (a link
    /// for all hosts reaches a host added later this way) and takes away those that shouldn't.</summary>
    private async Task SyncShareLinksAsync()
    {
        if (closing || shareLinks.Made.Count == 0 && shareLinks.Stopped.Count == 0) return;
        // A link stopped on another of your computers (a host remembers it) is stopped here too.
        if (ShareLinks.StoppedElsewhere(shareLinks.Made, friendsRead) is { Count: > 0 } elsewhere)
        {
            SaveShareLinks(shareLinks with
            {
                Made = shareLinks.Made.Where(l => !elsewhere.Contains(l)).ToList(),
                Stopped = shareLinks.Stopped.Concat(elsewhere.Select(l => l.Id)).Distinct(StringComparer.Ordinal).TakeLast(ShareLinks.MaximumStopped).ToList()
            });
            foreach (var link in elsewhere)
            {
                ShareLinks.ForgetSecret(link.Id);
                ErrorLog.Info($"Share links: the link for {link.Name} was stopped on another of your computers; it is stopped here too.");
            }
        }
        var changes = ShareLinks.Plan(shareLinks.Made, friendsRead, shareLinks.Stopped);
        if (changes.Count == 0) return;
        foreach (var change in changes)
        {
            if (FriendsHosts().FirstOrDefault(h => h.HostId == change.HostId) is not { } host) continue;
            var secret = change.Add ? ShareLinks.Secret(change.Link.Id) : null;
            if (change.Add && secret is null)
            {
                ErrorLog.Warn($"Share links: the secret of the link for {change.Link.Name} is gone from Windows Credential Manager, so Martlet can't add it to {change.HostId}.");
                continue;
            }
            try
            {
                using var connection = ClusterSync.Connect(host.Pairing);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                friendsRead[change.HostId] = await connection.ChangeSignInSettingsAsync(
                    change.Add ? ShareLinks.AddChange(change.Link, secret!) : ShareLinks.RemoveChange(change.Link.Id), timeout.Token);
                ErrorLog.Info($"Share links: {(change.Add ? "added" : "removed")} the link for {change.Link.Name} {(change.Add ? "to" : "from")} {change.HostId}.");
            }
            catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { }
            catch (HostConnectionException error) when (error.Code == "signin.link_removed")
            {
                // Stopped on another of your computers after this PC's last read: stop it here too.
                SaveShareLinks(shareLinks with
                {
                    Made = shareLinks.Made.Where(l => l.Id != change.Link.Id).ToList(),
                    Stopped = shareLinks.Stopped.Append(change.Link.Id).Distinct(StringComparer.Ordinal).TakeLast(ShareLinks.MaximumStopped).ToList()
                });
                ShareLinks.ForgetSecret(change.Link.Id);
                ErrorLog.Info($"Share links: {change.HostId} says the link for {change.Link.Name} was stopped; it is stopped here too.");
            }
            catch (Exception error) when (ClusterSync.IsHostFailure(error))
            {
                ErrorLog.Warn($"Share links: couldn't {(change.Add ? "add" : "remove")} the link for {change.Link.Name} on {change.HostId}: {error.Message}");
            }
        }
        if (closing) return;
        SaveFriendsSummary();
        RenderFriends();
    }

    /// <summary>The buttons of a friend you share with a link: Copy link, Stop link, and, for a link that names its hosts, Share or
    /// Stop sharing each of your hosts.</summary>
    private void AddShareLinkButtons(FriendView friend, WrapPanel buttons)
    {
        void Add(string id, string label, string name, Action run, bool primary = false)
        {
            var button = new Button { Content = label, Margin = new Thickness(8, 4, 0, 0) };
            if (primary) button.SetResourceReference(StyleProperty, "PrimaryButton");
            AutomationProperties.SetAutomationId(button, id);
            AutomationProperties.SetName(button, name);
            button.Click += (_, _) => run();
            buttons.Children.Add(button);
        }
        var made = shareLinks.Made.FirstOrDefault(l => l.Id == friend.Subject);
        if (made is not null) Add("ShareLinkCopy-" + friend.Key, "Copy link", $"Copy the share link for {friend.Name}", () => CopyShareLink(friend));
        if (made is { Hosts: not null })
            foreach (var place in friend.Hosts.Where(h => h.ProviderReady))
            {
                var share = !made.Covers(place.HostId);
                Add((share ? "FriendShare-" : "FriendStop-") + place.HostId + "-" + friend.Key, share ? $"Share {place.HostId}" : $"Stop sharing {place.HostId}",
                    share ? $"Share {place.HostId} with {friend.Name}" : $"Stop sharing {place.HostId} with {friend.Name}",
                    () => ChangeShareLinkHostAsync(friend, place.HostId, share).Forget());
            }
        Add("ShareLinkStop-" + friend.Key, "Stop link", $"Stop the share link for {friend.Name}", () => StopShareLinkAsync(friend).Forget());
    }

    /// <summary>What a link friend's row says: which hosts the link covers and where it isn't yet (no period at the end).</summary>
    private string ShareLinkDetail(FriendView friend)
    {
        var made = shareLinks.Made.FirstOrDefault(l => l.Id == friend.Subject);
        var all = made?.AllHosts ?? friendsRead.Values.SelectMany(s => s.Links ?? []).FirstOrDefault(l => l.Id == friend.Subject)?.AllHosts ?? false;
        var parts = new List<string> { all ? "Share link for all of your hosts, also the ones you add later" : "Share link for " +
            (made?.Hosts is { } hosts ? string.Join(", ", hosts) : string.Join(", ", friend.SharedOn)) };
        var missing = friend.Hosts.Where(h => h.State != FriendHostState.Shared && (made?.Covers(h.HostId) ?? all)).ToArray();
        if (missing.Any(h => h.ProviderReady)) parts.Add($"not on {string.Join(", ", missing.Where(h => h.ProviderReady).Select(h => h.HostId))} yet, Martlet adds it there");
        if (missing.Any(h => !h.ProviderReady)) parts.Add($"update {string.Join(", ", missing.Where(h => !h.ProviderReady).Select(h => h.HostId))} to share {(missing.Count(h => !h.ProviderReady) == 1 ? "it" : "them")} too");
        if (made is null) parts.Add("made on another of your computers: copy it there");
        return string.Join(" · ", parts);
    }

    // ---------- a friend's side: accept a link, and find hosts it covers later ----------

    private void AcceptShareLink_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new HostInputDialog("Accept a share link", "Use a friend's hosts",
            "Paste the share link your friend sent (it starts with martlet://share/): this PC can then use their hosts, which see only the " +
            "requests it sends there.", "_Continue");
        dialog.AddText("link", "Share link", "", maxLength: 12_288);
        if (dialog.Ask(this) is not { } values) return;
        AcceptShareLinkAsync(values["link"]).Forget();
    }

    /// <summary>Accepts a share link (clicked, pasted or handed over by another start of Martlet): asks once, signs in to each host
    /// it names as a friend, keeps them under Hosts shared with this PC and remembers the link for hosts it covers later.</summary>
    internal async Task AcceptShareLinkAsync(string text)
    {
        if (store is null || setupService is null || closing) return;
        ShareLink link;
        try { link = ShareLink.Parse(text); }
        catch (ContractException error) { ActionText.Text = error.Message; return; }
        Navigate(NavShare);
        var names = string.Join(", ", link.Hosts.Select(h => h.Label is { } label ? $"{label} ({h.HostId})" : h.HostId));
        if (!ConfirmationDialog.Confirm(this,
                $"Use {(link.Hosts.Count == 1 ? "a host" : $"{link.Hosts.Count} hosts")} that {link.From ?? "a friend"} shares with you" +
                $"{(link.Label is { } name ? $" as {name}" : "")}: {names}? Their owner sees the requests this PC sends there, but never your " +
                "settings, memories or API keys.",
                "Accept"))
            return;
        await SignInWithShareLinkAsync(link, link.Hosts);
    }

    /// <summary>Signs in to <paramref name="hosts"/> with <paramref name="link"/> and keeps each as a host shared with this PC;
    /// remembers the link. Returns how many hosts this PC now uses through it.</summary>
    private async Task<int> SignInWithShareLinkAsync(ShareLink link, IReadOnlyList<NetworkInvite> hosts, bool quiet = false)
    {
        if (store is null) return 0;
        shareLinksBusy = true;
        var device = NetworkIdentity.ThisDevice;
        var kept = new List<string>();
        var problems = new List<string>();
        try
        {
            if (!quiet) ActionText.Text = $"Signing in to {string.Join(", ", hosts.Select(h => h.HostId))} with the share link...";
            foreach (var invite in hosts)
            {
                if (homeHosts.Any(h => h.HostId == invite.HostId))
                {
                    problems.Add($"{invite.HostId} is already one of your own hosts here, so Martlet can't keep a friend's host with that name");
                    continue;
                }
                try
                {
                    var (pairing, secret, who) = await HostSignInClient.SignInWithShareLinkAsync(invite, link, device, Martlet.Core.Network.ThisComputer.Name, lifetime.Token);
                    await HostsWindow.KeepPairingAsync(Pairings(), pairing, secret, HostSetupMethod.Agent, null, null, lifetime.Token, invite.Addresses,
                        HostSignInAccess.Friend, $"{link.Label ?? who.Label ?? "a friend"} (share link from {link.From ?? "a friend"})");
                    kept.Add(invite.HostId);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is HostConnectionException or InvalidOperationException or IOException or ContractException or
                    OperationCanceledException or System.Net.Sockets.SocketException)
                {
                    problems.Add($"{invite.HostId}: {error.Message}");
                }
            }
            var accepted = shareLinks.Accepted.FirstOrDefault(a => a.Id == link.Id);
            if (kept.Count > 0 || accepted is not null)
            {
                ShareLinks.KeepSecret(link.Id, link.Secret);
                var next = (accepted ?? new AcceptedShareLink { Id = link.Id, AcceptedAt = DateTimeOffset.Now }) with
                {
                    From = link.From ?? accepted?.From, Label = link.Label ?? accepted?.Label, NetworkId = link.NetworkId ?? accepted?.NetworkId,
                    HostIds = (accepted?.HostIds ?? []).Concat(kept).Distinct(StringComparer.Ordinal).ToList()
                };
                SaveShareLinks(shareLinks with { Accepted = shareLinks.Accepted.Where(a => a.Id != link.Id).Append(next).TakeLast(ShareLinks.MaximumAccepted).ToList() });
            }
            ErrorLog.Info($"Share links: signed in to {kept.Count} of {hosts.Count} host(s) with the link from {link.From ?? "a friend"}" +
                (kept.Count > 0 ? $" ({string.Join(", ", kept)})" : "") + (problems.Count > 0 ? $"; {string.Join("; ", problems)}." : "."));
            if (!quiet || kept.Count > 0)
                ActionText.Text = kept.Count == 0
                    ? $"Couldn't sign in with the share link: {string.Join("; ", problems)}."
                    : $"{string.Join(", ", kept)} {(kept.Count == 1 ? "is" : "are")} shared with this PC by {link.From ?? "a friend"}: choose " +
                      (kept.Count == 1 ? "it" : "them") + " for a job under Share › Hosts shared with this PC" +
                      (problems.Count > 0 ? $"; not yet: {string.Join("; ", problems)}." : ".");
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException error) { ActionText.Text = error.Message; }
        finally { shareLinksBusy = false; }
        if (kept.Count > 0)
        {
            await RefreshHomeAsync();
            // Hosts the owner added to the link after it was made.
            if (!quiet) await DiscoverShareLinkHostsAsync(force: true);
        }
        return kept.Count;
    }

    /// <summary>For each share link this PC accepted: asks one of its hosts which hosts the link covers now and signs in to new
    /// ones (at most every five minutes, with the Hosts shared with this PC check). Hosts you forgot stay forgotten.</summary>
    private async Task DiscoverShareLinkHostsAsync(bool force = false)
    {
        if (store is null || closing || shareLinksBusy || shareLinks.Accepted.Count == 0) return;
        if (!force && shareLinksDiscoveredAt is { } at && DateTimeOffset.Now - at < SharedHostsStale) return;
        shareLinksDiscoveredAt = DateTimeOffset.Now;
        foreach (var accepted in shareLinks.Accepted.ToArray())
        {
            if (ShareLinks.Secret(accepted.Id) is not { } secret) continue;
            foreach (var host in sharedHosts.Where(h => accepted.HostIds.Contains(h.HostId, StringComparer.Ordinal)))
            {
                HostSharedHosts answer;
                try
                {
                    using var connection = ConnectToHost(GatewayAvatarHostLink.Pairing(host.Pairing));
                    answer = await connection.ReadSharedHostsAsync(lifetime.Token);
                }
                catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { continue; }
                catch (Exception error) when (ClusterSync.IsHostFailure(error) || error is InvalidOperationException) { continue; }
                if (answer.LinkId != accepted.Id) continue;
                var fresh = answer.Hosts.Where(h => !sharedHosts.Any(s => s.HostId == h.HostId) && !homeHosts.Any(s => s.HostId == h.HostId) &&
                    !accepted.Skipped.Contains(h.HostId, StringComparer.Ordinal)).Take(ShareLink.MaximumHosts).ToArray();
                if (fresh.Length > 0)
                {
                    ErrorLog.Info($"Share links: {host.HostId} says the link from {accepted.Sender} also covers {string.Join(", ", fresh.Select(h => h.HostId))}; signing in there.");
                    var link = new ShareLink { Id = accepted.Id, Secret = secret, From = accepted.From, Label = accepted.Label, NetworkId = accepted.NetworkId, Hosts = fresh };
                    await SignInWithShareLinkAsync(link, fresh, quiet: true);
                }
                break;
            }
        }
    }

    /// <summary>When you forget a host a share link gave this PC, the link doesn't bring it back.</summary>
    private void SkipShareLinkHost(string hostId)
    {
        if (!shareLinks.Accepted.Any(a => a.HostIds.Contains(hostId, StringComparer.Ordinal))) return;
        SaveShareLinks(shareLinks with
        {
            Accepted = shareLinks.Accepted.Select(a => a.HostIds.Contains(hostId, StringComparer.Ordinal)
                ? a with { HostIds = a.HostIds.Where(h => h != hostId).ToList(), Skipped = a.Skipped.Append(hostId).Distinct(StringComparer.Ordinal).ToList() }
                : a).ToList()
        });
    }
}
