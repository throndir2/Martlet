using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Audio2Face.Remote;
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
                problems[host.HostId] = error is Audio2FaceHostException { Code: "request.invalid" or "signin.unsupported" or "signin.unavailable" }
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
            catch (Audio2FaceHostException error) when (error.Code is "request.invalid" or "signin.unsupported") { }
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
            catch (Audio2FaceHostException error) when (error.Code == "signin.link_removed")
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
                catch (Exception error) when (error is Audio2FaceHostException or InvalidOperationException or IOException or ContractException or
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
