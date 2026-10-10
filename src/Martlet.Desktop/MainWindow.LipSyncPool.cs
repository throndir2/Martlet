using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Audio2Face;
using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;

namespace Martlet.Desktop;

/// <summary>Companion › Lip-sync (docs/AVATARS.md#the-lip-sync-pool): lip-sync's pool as the shared list control, this PC's own
/// Audio2Face set-up, and the list and the older "who does lip-sync" choice (the character's profile and the shared plan's
/// lip-sync job, which Home, Devices and the coverage checks show) kept in step both ways.</summary>
public partial class MainWindow
{
    // ---------- the page ----------

    /// <summary>Who moves the character's mouth: the places in lip-sync's list, in order, each with its own settings. The
    /// first one that is on and free moves the mouth; with none on, the mouth follows the voice's loudness.</summary>
    private void RenderLipSyncTab(Panel page)
    {
        var list = EnsureLipSyncPool();
        page.Children.Add(PageNowCard(LipSyncNowText(list), coverage.FirstOrDefault(c => c.Job == ClusterJobs.LipSync && c.IsProblem), "LipSyncNow"));
        PoolListOptions options = null!;
        options = new()
        {
            Area = PoolAreas.LipSync,
            Intro = "The places that move the mouth with Audio2Face, in order. Each sentence goes to the first one that is free; " +
                "when all are busy, the mouth follows the voice's loudness for that moment. With none on, it always does. " +
                "Only Martlet's generated voice is sent to a computer in the list.",
            Status = LipSyncMemberStatus,
            Settings = member => LipSyncMemberSettings(member, options),
            CanAdd = host => !host.Shared && (hostChecks.GetValueOrDefault(host.HostId)?.Offers?.ContainsKey(HostRoles.Audio2Face) == true ||
                CannotHand(host.HostId, HostRoles.Audio2Face, ClusterJobs.LipSync) is null),
            Saved = (_, after) => FollowLipSyncListAsync(after)
        };
        page.Children.Add(PoolListCard(options));
        page.Children.Add(LipSyncThisPcCard(list));
    }

    /// <summary>The Now line: what moves the mouth for the next sentence.</summary>
    private string LipSyncNowText(PoolList? list)
    {
        if (homeAvatar?.LipSync == AvatarLipSync.Audio2Face)
            return $"Your own Audio2Face service on this PC ({LipSyncThisPcEndpoint().Authority}), with Audio2Face-only lip-sync activated " +
                "in the character's settings.";
        var on = list?.Members.Where(m => !m.Off).ToArray() ?? [];
        if (on.Length == 0 || homeAvatar?.LipSync == AvatarLipSync.Loudness)
            return "Voice loudness: the mouth opens and closes with the voice. Nothing in the list below is on.";
        var lead = on[0];
        var first = lead.Kind == PoolMemberKind.ThisPc
            ? $"Your own Audio2Face service at {LipSyncThisPcEndpoint().Authority}" + (ownLipSyncAnswers == false ? ", which isn't answering now" : "")
            : $"Audio2Face on {lead.Name}" + (lead.HostId == ThisPcHost()?.HostId ? " (this PC)" : "") +
                (LipSyncHostReachable(lead.HostId!) == false ? ", which isn't reachable now"
                    : HostServes(lead.HostId!, ClusterJobs.LipSync, HostRoles.Get(HostRoles.Audio2Face).RouteId) == false ? ", which doesn't run it yet" : "");
        return first + (on.Length == 1 ? ". "
                : lead.Kind == PoolMemberKind.ThisPc ? $". When it doesn't answer, the next of {on.Length - 1} more takes the sentence. "
                : $". When it is busy, the next of {on.Length - 1} more takes the sentence. ") +
            "When none can, the mouth follows the voice's loudness.";
    }

    private string? LipSyncMemberStatus(PoolMember member)
    {
        if (member.Kind == PoolMemberKind.ThisPc)
            return $"your own Audio2Face service at {LipSyncThisPcEndpoint(member).Authority}" + ownLipSyncAnswers switch
            {
                true => "; answering",
                false => "; not answering now",
                _ => ""
            };
        var hostId = member.HostId!;
        var parts = new List<string>();
        if (hostId == ThisPcHost()?.HostId) parts.Add("this PC's host service");
        if (FindHost(hostId) is null) parts.Add("not paired on this PC, so this PC skips it");
        else
        {
            var check = hostChecks.GetValueOrDefault(hostId);
            parts.Add(LipSyncHostReachable(hostId) == false ? "not reachable right now"
                : HostServes(hostId, ClusterJobs.LipSync, HostRoles.Get(HostRoles.Audio2Face).RouteId) switch
                {
                    true => "runs Audio2Face" + (check?.Offers?.GetValueOrDefault(HostRoles.Audio2Face) is { } model ? $" ({model})" : ""),
                    false => "doesn't run Audio2Face yet",
                    _ => "not checked yet"
                });
        }
        if (member.Kind == PoolMemberKind.Gpu)
            parts.Add(LipSyncPool.Serves(member) ? "its computer runs one Audio2Face for all its cards" : "its Audio2Face runs on another card, so this PC skips it");
        return string.Join("; ", parts);
    }

    /// <summary>A member's settings under its row: this PC's own service address, or a computer's Audio2Face (install it, change
    /// its model, check it).</summary>
    private UIElement? LipSyncMemberSettings(PoolMember member, PoolListOptions options)
    {
        var id = "Pool-" + PoolAreas.LipSync.Id;
        var stack = new StackPanel();
        if (member.Kind == PoolMemberKind.ThisPc)
        {
            stack.Children.Add(Note("An Audio2Face service you run yourself on each companion PC (NVIDIA's NIM or the open-source " +
                "engine), at a loopback address. Martlet looks for it before each sentence.", new Thickness(0, 0, 0, 6)));
            var address = new TextBox { MinWidth = 260, Margin = new Thickness(0, 0, 8, 0), Text = LipSyncThisPcEndpoint(member).AbsoluteUri };
            AutomationProperties.SetAutomationId(address, id + "-Endpoint");
            AutomationProperties.SetName(address, "Address of your own Audio2Face service");
            var save = PageButton("Save address", () =>
            {
                Uri endpoint;
                try
                {
                    endpoint = new Uri(address.Text.Trim());
                    new Audio2FaceOptions { Endpoint = endpoint }.Validate();
                }
                catch (Exception error) when (error is UriFormatException or ArgumentException)
                {
                    ActionText.Text = "Enter a loopback address with a port, such as http://127.0.0.1:52000/.";
                    return;
                }
                if (store is null || WorkSharingRoster.Pool(store.DataDirectory, PoolAreas.LipSync) is not { } list ||
                    list.Find(member.Key) is not { } current) return;
                var value = endpoint.AbsoluteUri == OwnLipSyncEndpoint().AbsoluteUri ? null : endpoint.AbsoluteUri;
                SavePoolList(options, list, list.With(current.WithSetting(PoolSettingKeys.Endpoint, value)),
                    $"Lip-sync looks for your own Audio2Face service at {endpoint.Authority} now.");
                ownLipSyncAnswers = null;
                CheckOwnLipSyncAsync().Forget();
            }, id: id + "-SaveEndpoint");
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(address);
            line.Children.Add(save);
            stack.Children.Add(line);
            return stack;
        }
        var hostId = member.HostId!;
        if (FindHost(hostId) is not { } host)
        {
            stack.Children.Add(Note($"{hostId} isn't paired on this PC. Pair it on Devices to use it here.", new Thickness(0, 0, 0, 0)));
            return stack;
        }
        var check = hostChecks.GetValueOrDefault(hostId);
        var runs = HostServes(hostId, ClusterJobs.LipSync, HostRoles.Get(HostRoles.Audio2Face).RouteId) == true;
        var buttons = new List<Button>();
        if (ChangesRolesOn(host) && check?.Reachable == true)
        {
            if (runs)
            {
                var change = PageButton("Change model", () => LaunchOnHost(host, HostAction.Change(HostRoles.Audio2Face)), id: $"{id}-Change-{member.Key}");
                AutomationProperties.SetName(change, $"Change Audio2Face on {hostId}");
                buttons.Add(change);
            }
            else if (CannotHand(hostId, HostRoles.Audio2Face, ClusterJobs.LipSync) is null)
                buttons.Add(PageButton("Install Audio2Face", () => LaunchOnHost(host, HostRoles.Get(HostRoles.Audio2Face).Add),
                    primary: true, id: $"{id}-Install-{hostId}"));
        }
        buttons.Add(PageButton("Check it", () => RunNodeAction(NodeAction.CheckHost, hostId), id: $"{id}-Check-{hostId}"));
        stack.Children.Add(Note(runs ? $"Audio2Face{(check?.Offers?.GetValueOrDefault(HostRoles.Audio2Face) is { } model ? $" {model}" : "")} runs on {hostId}."
            : CannotHand(hostId, HostRoles.Audio2Face, ClusterJobs.LipSync) is { } cannot ? $"Audio2Face can't run there: {cannot}"
            : $"Audio2Face needs {HostRoles.Get(HostRoles.Audio2Face).Needs}. Until it runs there, this PC skips {hostId}.", new Thickness(0, 0, 0, 6)));
        stack.Children.Add(Row([.. buttons]));
        return stack;
    }

    /// <summary>Audio2Face on this PC: what its graphics card means for it, and setting it up in Martlet's host service here
    /// (which then goes first in the list).</summary>
    private Border LipSyncThisPcCard(PoolList? list)
    {
        var thisPc = ThisPcHost();
        var gpu = machine.BestGpu;
        var fits = gpu is { IsNvidia: true } && (gpu.MemoryGb ?? 0) >= 4;
        var arm = machine.ArmRefusal("audio2face");
        var stack = new List<UIElement> { Heading("Audio2Face on this PC") };
        var about = Note(arm ?? (gpu is null ? "No dedicated graphics card was found on this PC; Audio2Face needs an NVIDIA graphics card with 4 GB or more."
            : $"This PC has {gpu.Describe()}." + (fits ? "" : " Audio2Face needs an NVIDIA graphics card with 4 GB or more, so another " +
                "computer or the voice's loudness suits this PC better.")), new Thickness(0, 0, 0, 6));
        if (!fits || arm is not null) about.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        AutomationProperties.SetAutomationId(about, "LipSyncDockerAbout");
        stack.Add(about);
        if (thisPc is null)
        {
            stack.Add(Note((machine.DockerRunning ? "Docker Desktop is running. "
                    : machine.DockerInstalled ? "Docker Desktop is installed. Martlet can start it when needed. "
                    : "Docker Desktop isn't installed yet. Martlet installs it first. ") +
                "Setting this up installs Martlet's host service and Audio2Face with its open-source engine (no NVIDIA account), then " +
                "puts this PC first in the list.", new Thickness(0, 0, 0, 6)));
            var setUp = PageButton("Set up Audio2Face with Docker", () => SetUpThisPcHostAsync(AssignLipSyncAsync).Forget(),
                primary: fits, id: "SetupLipSyncHostThisPc");
            setUp.IsEnabled = arm is null;
            stack.Add(Row(setUp));
            return Card([.. stack]);
        }
        var runs = HostServes(thisPc.HostId, ClusterJobs.LipSync, HostRoles.Get(HostRoles.Audio2Face).RouteId) == true;
        var listed = list?.Members.Any(m => m.HostId == thisPc.HostId) == true;
        stack.Add(Note(runs ? $"Audio2Face runs in this PC's host service ({thisPc.HostId})." + (listed ? "" : " Add it to the list above to use it.")
            : $"Audio2Face isn't installed in this PC's host service ({thisPc.HostId}) yet. Installing it puts this PC first in the list.",
            new Thickness(0, 0, 0, 6)));
        var buttons = new List<Button>();
        if (!runs)
        {
            var use = PageButton("Install Audio2Face on this PC", () => AssignLipSyncAsync("host:" + thisPc.HostId).Forget(), primary: fits,
                id: "SetupLipSyncUseLocal");
            use.IsEnabled = arm is null;
            buttons.Add(use);
        }
        buttons.Add(PageButton("Check it", () => RunNodeAction(NodeAction.CheckHost, thisPc.HostId), id: "SetupLipSyncCheckLocal"));
        stack.Add(Row([.. buttons]));
        return Card([.. stack]);
    }

    // ---------- the list and the older choice, in step ----------

    /// <summary>Whether a host answered its last check (the background sync check when sync is on, else Check connection);
    /// null before it was checked.</summary>
    private bool? LipSyncHostReachable(string hostId) =>
        (clusterEnabled ? clusterProbes.GetValueOrDefault(hostId)?.Reachable : null) ?? hostChecks.GetValueOrDefault(hostId)?.Reachable;

    /// <summary>This PC's own Audio2Face service address: the list's This PC setting, or the character's endpoint.</summary>
    private Uri LipSyncThisPcEndpoint(PoolMember? member = null)
    {
        member ??= store is null ? null : WorkSharingRoster.Pool(store.DataDirectory, PoolAreas.LipSync)?.Find(PoolMember.ThisPcKey);
        return member?.Setting(PoolSettingKeys.Endpoint) is { } text && Uri.TryCreate(text, UriKind.Absolute, out var uri) ? uri : OwnLipSyncEndpoint();
    }

    /// <summary>Whether this PC's own Audio2Face service is in use or on in the list, so its port is worth a look.</summary>
    private bool LipSyncLooksHere() => NetworkMap.LipSync(homeAvatar) == LipSyncHandler.ThisPc ||
        store is not null && WorkSharingRoster.Pool(store.DataDirectory, PoolAreas.LipSync)?.Find(PoolMember.ThisPcKey) is { Off: false };

    /// <summary>The older choice as a list key: "off" (voice loudness), "this-pc" (this PC's own service) or "host:&lt;id&gt;";
    /// null for a host a friend shares with this PC, which never goes in your shared list.</summary>
    private string? LipSyncChoiceKey() => NetworkMap.LipSync(homeAvatar) switch
    {
        LipSyncHandler.Loudness => "off",
        LipSyncHandler.Host when FindHost(homeAvatar!.RemoteHost!.HostId) is null => null,
        LipSyncHandler.Host => "host:" + homeAvatar!.RemoteHost!.HostId,
        _ => "this-pc"
    };

    /// <summary>Lip-sync's list; made once from the older choices when there is none yet (and saved and shared, unless it would
    /// be empty: a computer never shares an empty list over your other computers' choices). Null while there is none.</summary>
    private PoolList? EnsureLipSyncPool()
    {
        if (store is null) return null;
        if (WorkSharingRoster.Pool(store.DataDirectory, PoolAreas.LipSync) is { } list) return list;
        var made = LipSyncPool.Migrate(homeAvatar, store.DataDirectory);
        // A host a friend shares with this PC stays this PC's own choice.
        made = made with { Members = [.. made.Members.Where(m => m.HostId is null || FindHost(m.HostId) is not null)] };
        if (made.Members.Count == 0 || !PoolSettings.SaveFor(store.DataDirectory, PoolAreas.LipSync, made)) return null;
        WorkSharingRoster.Forget();
        QueueSettingsSync();
        ErrorLog.Info($"Pools: made the Lip-sync list from the older choice: {string.Join(", ", made.Members.Select(m => m.Key))}.");
        return made;
    }

    /// <summary>A lip-sync choice made outside the list (Home, Devices, a coverage fix, setup) puts that place first in the list
    /// and turns it on; voice loudness turns every member off (each keeps its settings). A host a friend shares stays out.</summary>
    private void FollowLipSyncChoiceIntoPool(string key)
    {
        if (store is null) return;
        if (WorkSharingRoster.Pool(store.DataDirectory, PoolAreas.LipSync) is not { } list)
        {
            EnsureLipSyncPool();
            return;
        }
        PoolList next;
        if (key == "off")
        {
            if (list.NoneOn) return;
            next = list with { Members = [.. list.Members.Select(m => m with { Off = true })] };
        }
        else
        {
            var member = key == "this-pc" ? PoolMember.ThisPc() : PoolMember.Computer(key[5..]);
            if (member.HostId is { } id && FindHost(id) is null) return;
            var lead = list.Members.FirstOrDefault(m => !m.Off);
            if (lead is not null && (lead.Key == member.Key || member.HostId is not null && lead.HostId == member.HostId)) return;
            next = list.With((list.Find(member.Key) ?? member) with { Off = false }).Move(member.Key, -PoolSettings.MaximumMembers);
        }
        if (!PoolSettings.SaveFor(store.DataDirectory, PoolAreas.LipSync, next)) return;
        WorkSharingRoster.Forget();
        QueueSettingsSync();
        ErrorLog.Info($"Pools: lip-sync's list follows the choice {key}: {string.Join(", ", next.Members.Select(m => m.Key + (m.Off ? " (off)" : "")))}.");
    }

    /// <summary>After the list changed on this page: the older choice follows its first member that is on and paired here
    /// (so Home, Devices, the shared plan and a showing character agree), or voice loudness when none is on. A host a friend
    /// shares, chosen on this PC, stays while the list has a member on.</summary>
    private async Task FollowLipSyncListAsync(PoolList after)
    {
        if (store is null || setupService is null || closing) return;
        var lead = after.Members.FirstOrDefault(m => !m.Off && (m.Kind == PoolMemberKind.ThisPc || FindHost(m.HostId) is not null));
        var choice = lead is null ? "off" : lead.Kind == PoolMemberKind.ThisPc ? "this-pc" : "host:" + lead.HostId;
        var now = LipSyncChoiceKey();
        if (choice == now || now is null && lead is not null) return;
        using var turn = await ChangeTurnAsync();
        await ApplyLipSyncAsync(lead?.Kind == PoolMemberKind.ThisPc ? null : FindHost(lead?.HostId), lead is null);
        RecordClusterJob(ClusterJobs.LipSync, ClusterSync.Local(ClusterJobs.LipSync, homeSettings, homeAvatar, shared: SharedHostIds()));
        ErrorLog.Info($"Pools: lip-sync follows its list: {choice}.");
        if (closing) return;
        UpdateCharacterButton();
        RenderHome();
        if (DevicesPage.IsVisible) RenderMap();
        CheckOwnLipSyncAsync().Forget();
    }
}
