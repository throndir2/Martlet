using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Motion;

namespace Martlet.Desktop;

/// <summary>
/// Companion › Motion (an optional extra, docs/MOTION_HOST.md#the-motion-pool): where Martlet makes new body motions for VRM
/// characters, on the <c>motion</c> host role (Tencent's HY-Motion 1.0). In the standard order: Now (whether and where Martlet
/// makes motions, and whether the character shown is a VRM, the only kind they play on); the motion pool (the shared list
/// control: the computers that make motions, in order, with no separate on/off switch: with none on, motion is off); each
/// member's Settings say where Motion stands there and set the role up with the same martlet-host flow as every role (its own
/// dialog shows the model and Tencent's license terms); then what Motion is. Kept motions and Make a motion stay on Companion ›
/// Emotes and motions. Automation IDs: <c>MotionNow</c>, <c>MotionCharacter</c>, <c>MotionOpenEmotes</c>, the list's
/// <c>Pool-motion-...</c> (and per member <c>Pool-motion-State-&lt;key&gt;</c> and <c>Pool-motion-SetUp-&lt;key&gt;</c>) and
/// <c>MotionAbout</c>.
/// </summary>
public partial class MainWindow
{
    internal const string MotionThisPc = "this-pc";

    /// <summary>The computer Motion is being set up on now (a host ID, or <see cref="MotionThisPc"/>), or null.</summary>
    private string? motionPendingHost;
    /// <summary>Why the last setup failed, by computer.</summary>
    private readonly Dictionary<string, string> motionFailures = new(StringComparer.Ordinal);

    private void RenderMotionTab(Panel page)
    {
        EnsureMotionPool();
        var list = store is null ? null : WorkSharingRoster.Shared.Pool(store.DataDirectory, PoolAreas.Motion);
        var listed = list is not null;
        var members = store is null ? [] : MotionClient.Members(store.DataDirectory);
        var older = store is null ? new MotionSettings() : MotionClient.Settings(store.DataDirectory);
        var thisPc = ThisPcHost()?.HostId;
        string Name(string hostId) => hostId == thisPc ? "this PC" : hostId;
        var shown = avatar.IsShowing ? avatar.InspectedProfile : homeAvatar;

        // ---------- Now ----------
        var nowText = new TextBlock
        {
            Text = MotionNow(listed, list?.NoneOn ?? false, older, [.. members.Select(m => Name(m.HostId)).Distinct()], MotionClient.Fixture),
            FontSize = 15, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6)
        };
        AutomationProperties.SetAutomationId(nowText, "MotionNow");
        var character = Note(MotionCharacter(shown?.Renderer), new Thickness(0, 0, 0, 4));
        if (shown is not null && shown.Renderer != AvatarRenderer.Vrm) character.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        AutomationProperties.SetAutomationId(character, "MotionCharacter");
        var emotes = PageButton("Kept motions and Make a motion", () => OpenCompanion(CompanionTab.Emotes), link: true, id: "MotionOpenEmotes");
        page.Children.Add(Card(Heading("Now"), nowText,
            HelpTip.Explain("While you talk, Martlet can move a VRM character's whole body: cheer, clap, bow deeply or any movement it " +
                "thinks of. It makes a new motion in the background and plays it when it's ready, and your words never wait for it. It " +
                "keeps each motion, so your VRM characters do it again at once later, on all your computers.", new Thickness(0, 0, 0, 4),
                "MotionWhat", "body motions", wiki: "The-Character#body-motions"),
            character, Row(emotes)));

        // ---------- the motion pool: the computers that make motions, in order ----------
        page.Children.Add(PoolListCard(new PoolListOptions
        {
            Area = PoolAreas.Motion,
            Heading = "Where Martlet makes motions",
            Intro = "The computers that make body motions, in order. A motion goes to the first one that isn't making another motion; " +
                "when all are busy it waits for the first to finish. With none on, Martlet makes no new motions; the motions it kept " +
                "still play. Open a computer's Settings to set Motion up there. Your other computers follow this list.",
            Wiki = "The-Character#body-motions",
            Status = MotionMemberStatus,
            Settings = MotionMemberSettings,
            CanAdd = host => !host.Shared && HostCan(host.HostId, HostRoles.Motion) is not { Allowed: false },
            Saved = (before, after) =>
            {
                // A computer added that doesn't run Motion yet shows its Settings, where Set up is.
                foreach (var added in after.Members.Where(m => before.Find(m.Key) is null))
                    if (MotionMemberHost(added) is not { } host || !Offers(host, HostRoles.Motion))
                        poolExpanded.Add(PoolAreas.Motion.Id + " " + added.Key);
                return Task.CompletedTask;
            }
        }));

        // ---------- what Motion is ----------
        var about = HelpTip.Explain(MotionAbout, new Thickness(0, 0, 0, 0), "MotionAbout", "the Motion role", wiki: "The-Character#body-motions");
        AutomationProperties.SetAutomationId(about, "MotionAbout");
        page.Children.Add(Card(Heading("What Motion is"), about));
    }

    /// <summary>What the Motion role is, in a few sentences (role.conf's facts and terms in short).</summary>
    internal const string MotionAbout =
        "Motion runs Tencent's HY-Motion 1.0 on an NVIDIA graphics card with 24 GB or more, and its license doesn't let you use it in " +
        "the European Union, the United Kingdom or South Korea. It runs in Docker; the standard model needs 26 GB and the computer " +
        "about 32 GB of memory. It downloads about 20 GB once, loads when the first motion is made and frees the card three minutes " +
        "after the last one, so the voice and thinking get it back. Only the short English description of the movement goes to that " +
        "computer, and it's free. Its license is Tencent's HY-Motion 1.0 Community License.";

    /// <summary>Companion › Motion's Now line (MotionNow). <paramref name="where"/>: the computers motions go to, in order (the
    /// list's members that are on and paired here; before the list, the older choice's). <paramref name="listed"/>: the list
    /// exists; <paramref name="noneOn"/>: no member in it is on.</summary>
    internal static string MotionNow(bool listed, bool noneOn, MotionSettings older, IReadOnlyList<string> where, bool fixture)
    {
        if (fixture) return "Martlet makes motions with the FIXTURE - NOT AI test maker on this PC (MARTLET_MOTION_FIXTURE=1).";
        if (listed && noneOn) return $"Off. {OptionalExtras.OffMeans(CompanionTab.Motion)}. No computer in the list is on.";
        if (!listed && older.Place == MotionPlace.Off) return $"Off. {OptionalExtras.OffMeans(CompanionTab.Motion)}.";
        if (where.Count == 0)
            return listed
                ? "No computer in the list can make motions for this PC: pair one on Devices, or add one below."
                : "Not set up yet: add one of your computers with a 24 GB NVIDIA graphics card to the list below and set Motion up there.";
        var place = where.Count == 1 ? where[0] : $"{where[0]} (or the first free one of {where.Count} computers)";
        return $"Martlet makes body motions on {place}.";
    }

    /// <summary>Whether the character shown (else the chosen one) can play body motions: only a VRM can.</summary>
    internal static string MotionCharacter(AvatarRenderer? renderer) => renderer switch
    {
        AvatarRenderer.Vrm => "Your character is a VRM, so replies can make and play body motions.",
        null => "Body motions play only on VRM characters; choose one on Companion › Character.",
        _ => "Your character is a Live2D model and body motions play only on VRM characters, so replies don't make or play them now " +
            "(Live2D characters keep their own model's motions)."
    };

    /// <summary>Makes the motion list once from the older choice (motion.json: one computer, automatic or off) and the computers
    /// that run Motion (every one in the shared plan, and those this PC's checks saw), so the choice isn't lost. The list is
    /// shared, so it takes every companion PC's motion computers, not only this PC's pairings, and each PC then uses the ones it is
    /// paired with. Hosts a friend shares with this PC are left out. Automatic while no computer runs Motion makes no list, so
    /// adding the role somewhere still turns motion on.</summary>
    private void EnsureMotionPool()
    {
        if (store is null || WorkSharingRoster.Shared.Pool(store.DataDirectory, PoolAreas.Motion) is not null) return;
        var paired = MotionClient.Paired(store.DataDirectory).Select(h => h.HostId).ToArray();
        var runners = MotionPool.Runners(ClusterSync.LoadPlan(store.DataDirectory), null, ThisPcHost()?.HostId)
            .Concat(paired.Where(id => hostChecks.GetValueOrDefault(id)?.Offers?.ContainsKey(HostRoles.Motion) == true))
            .Where(id => FindSharedHost(id) is null)
            .Distinct(StringComparer.Ordinal).ToArray();
        var older = MotionClient.Settings(store.DataDirectory);
        if (MotionPool.FirstList(older, runners) is not { } list || !PoolSettings.SaveFor(store.DataDirectory, PoolAreas.Motion, list)) return;
        QueueSettingsSync();
        ErrorLog.Info($"Pools: the Motion list was made from the motion choice ({older.Describe()}): " +
            (list.Members.Count == 0 ? "empty (off)." : string.Join(", ", list.Members.Select(m => m.Name)) + (list.NoneOn ? " (all off: motion was off)." : ".")));
    }

    /// <summary>After the motion role was added on <paramref name="hostId"/> from anywhere (Devices, a fix card), that computer
    /// joins the Motion list at its end when the list exists and doesn't have it, so adding the role turns motion on there.</summary>
    private void JoinMotionPool(string hostId)
    {
        if (store is null || WorkSharingRoster.Shared.Pool(store.DataDirectory, PoolAreas.Motion) is not { } list) return;
        var own = ThisPcHost()?.HostId;
        if (list.Members.Any(m => m.HostId == hostId || m.Kind == PoolMemberKind.ThisPc && hostId == own)) return;
        if (!PoolSettings.SaveFor(store.DataDirectory, PoolAreas.Motion, list.With(PoolMember.Computer(hostId)))) return;
        QueueSettingsSync();
        ErrorLog.Info($"Pools: {hostId} runs Motion now, so it joined the Motion list.");
        if (!closing && openTab == CompanionTab.Motion) Dispatcher.InvokeAsync(RenderTab);
    }

    /// <summary>The paired computer a motion pool member makes motions on: this PC's own host service for This PC, else the
    /// computer; null when it isn't paired here (or this PC has no host service yet). Never a host a friend shares.</summary>
    private PairedHost? MotionMemberHost(PoolMember member) =>
        member.Kind == PoolMemberKind.ThisPc ? ThisPcHost() : FindHost(member.HostId) is { Shared: false } host ? host : null;

    /// <summary>The key a setup on <paramref name="member"/> is followed by: <see cref="MotionThisPc"/> for this PC, else the host ID.</summary>
    private string MotionSetupKey(PoolMember member) =>
        member.Kind == PoolMemberKind.ThisPc || member.HostId is { } id && id == ThisPcHost()?.HostId ? MotionThisPc : member.HostId ?? MotionThisPc;

    /// <summary>The model a computer's Motion role runs, in words, or null.</summary>
    private string? MotionModel(PairedHost host) => hostChecks.GetValueOrDefault(host.HostId)?.Offers?.GetValueOrDefault(HostRoles.Motion) switch
    {
        "hy-motion-1.0-lite" => "HY-Motion-1.0-Lite",
        "hy-motion-1.0" => "HY-Motion-1.0",
        { Length: > 0 } other => other,
        _ => null
    };

    /// <summary>A motion pool member's line in the list: whether Motion runs there (and its model), is being set up, failed, or
    /// why it can't make motions.</summary>
    private string? MotionMemberStatus(PoolMember member)
    {
        var key = MotionSetupKey(member);
        if (motionPendingHost == key) return "setting Motion up...";
        var host = MotionMemberHost(member);
        var parts = new List<string>();
        if (host is not null && Offers(host, HostRoles.Motion))
            parts.Add(MotionModel(host) is { } model ? $"runs Motion with {model}" : "runs Motion");
        else if (host is null)
            parts.Add(member.Kind == PoolMemberKind.ThisPc ? ThisPcCannotMove() ?? "doesn't run Motion yet; Settings sets it up" : "not paired with this PC");
        else
        {
            var check = hostChecks.GetValueOrDefault(host.HostId);
            parts.Add(check?.Reachable == false ? "not reachable right now"
                : check?.Reachable == true ? "doesn't run Motion yet; Settings sets it up" : "not checked yet");
        }
        if (motionFailures.GetValueOrDefault(key) is { } failed) parts.Add("setup failed: " + failed.TrimEnd('.'));
        return string.Join("; ", parts);
    }

    /// <summary>A motion pool member's Settings (automation IDs <c>Pool-motion-&lt;part&gt;-&lt;member key&gt;</c>): where Motion
    /// stands there (State) and Set up Motion here (SetUp) where it doesn't run yet.</summary>
    private UIElement? MotionMemberSettings(PoolMember member)
    {
        var id = $"Pool-{PoolAreas.Motion.Id}-";
        var host = MotionMemberHost(member);
        var onThisPc = member.Kind == PoolMemberKind.ThisPc || host is not null && host.HostId == ThisPcHost()?.HostId;
        var key = MotionSetupKey(member);
        var pending = motionPendingHost == key;
        var ready = host is not null && Offers(host, HostRoles.Motion);
        var cannot = ready ? null : onThisPc ? ThisPcCannotMove()
            : host is null ? "It isn't paired with this PC."
            : hostChecks.GetValueOrDefault(host.HostId)?.Reachable == false ? "It isn't reachable right now."
            : CannotHand(host.HostId, HostRoles.Motion, "motion");
        var state = pending ? "Setting Motion up here..."
            : ready ? (MotionModel(host!) is { } model ? $"Motion is ready here with {model}." : "Motion is ready here.")
            : cannot ?? (motionFailures.GetValueOrDefault(key) is { } why ? $"Setting Motion up failed: {why}" : "Motion isn't set up here yet.");
        var panel = new StackPanel();
        var line = Note(state, new Thickness(0, 0, 0, 4));
        if (cannot is not null || !pending && !ready && motionFailures.ContainsKey(key))
            line.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        AutomationProperties.SetAutomationId(line, id + "State-" + member.Key);
        panel.Children.Add(line);
        if (ready) return panel;
        var setUp = PageButton(pending ? "Setting up..." : "Set up Motion here", () => SetUpMotionAsync(onThisPc ? null : host, key).Forget(),
            primary: true, id: id + "SetUp-" + member.Key);
        setUp.IsEnabled = !pending && motionPendingHost is null && cannot is null;
        if (cannot is not null)
        {
            setUp.ToolTip = cannot;
            ToolTipService.SetShowOnDisabled(setUp, true);
            AutomationProperties.SetHelpText(setUp, cannot);
        }
        panel.Children.Add(Row(setUp));
        return panel;
    }

    /// <summary>Why this PC can't make motions: no NVIDIA graphics card, or one with less than 24 GB; null when it can or Martlet
    /// hasn't read this PC's hardware yet.</summary>
    private string? ThisPcCannotMove()
    {
        if (ReferenceEquals(machine, MachineInfo.Unknown)) return null;
        if (machine.ArmRefusal("motion") is { } arm) return arm;
        var nvidia = machine.Gpus.Where(g => g.IsNvidia).OrderByDescending(g => g.MemoryGb ?? 0).FirstOrDefault();
        if (nvidia is null)
            return $"Needs an NVIDIA graphics card with 24 GB+; this PC has {(machine.Gpus.Count == 0 ? "none" : string.Join(", ", machine.Gpus.Select(g => g.Describe())))}.";
        return nvidia.MemoryGb is { } gb && gb < 22.5 ? $"Needs an NVIDIA graphics card with 24 GB+; this PC has {nvidia.Describe()}." : null;
    }

    /// <summary>Sets the motion role up on <paramref name="host"/> (null: this PC) through the same martlet-host add as every role:
    /// its own dialog shows the model (Lite, or the standard model on a card with 26 GB+) and Tencent's license terms, and the
    /// run window shows the progress. This PC without its host service sets that up first, in the same window.</summary>
    private async Task SetUpMotionAsync(PairedHost? host, string key)
    {
        if (store is null || setupService is null || closing) return;
        if (motionPendingHost is not null)
        {
            ActionText.Text = $"Motion is still being set up on {(motionPendingHost == MotionThisPc ? "this PC" : motionPendingHost)}; " +
                "its window shows progress, so set up the next one when it's done.";
            return;
        }
        var thisPc = ThisPcHost();
        var target = host ?? thisPc;
        var where = host is null ? "this PC" : host.HostId;
        motionFailures.Remove(key);
        motionPendingHost = key;
        RenderTab();
        try
        {
            if (target is not null)
            {
                var done = await RunHostActionAsync(target, HostRoles.Get(HostRoles.Motion).Add);
                if (closing) return;
                if (done is null) motionFailures[key] = $"Setting it up on {where} stopped; its run window has details.";
                else if (await WaitForMotionAsync(target, lifetime.Token) is { } why)
                    motionFailures[key] = $"Setup finished, but Martlet can't see Motion on {where} yet ({why}); check it in a minute.";
                else ActionText.Text = $"Motion is ready on {where}, and Martlet makes body motions there for VRM characters.";
                return;
            }
            string? stopped = null;
            async Task<string> Continue(HostRunWindow run, PairedHost pc)
            {
                await InstallRoleHereAsync(run, pc, new(HostRoles.Motion, "Motion", new Dictionary<string, string>(),
                    "Installing Motion on this PC (about 20 GB of downloads)...",
                    exit => $"Installing Motion stopped{(stopped is null ? $" (exit {exit})" : ": " + stopped)}. The output has details.")
                    { Questions = LocalRoleQuestions.Role, Stopped = why => stopped ??= why });
                run.Status("Checking Motion on this PC...");
                if (await WaitForMotionAsync(pc, run.Token) is { } why)
                    throw new InvalidOperationException(stopped = $"Setup finished, but Martlet can't see Motion yet ({why}); check this PC in a minute.");
                return "Motion is ready on this PC.";
            }
            // No host service on this PC yet (with one, the branch above set the role up): it is set up first, in the same window.
            var status = await RunRoleSetupHereAsync(null, "Set up Motion on this PC", "Set up Motion on this PC", Continue,
                () => "Motion wasn't set up; the run window has details.");
            if (!closing && status is not null)
            {
                ActionText.Text = status;
                if (!status.StartsWith("Motion is ready", StringComparison.Ordinal)) motionFailures[key] = status;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or
            JsonException or ArgumentException or Audio2FaceHostException || ClusterSync.IsHostFailure(error))
        {
            motionFailures[key] = error.Message;
            ActionText.Text = error.Message;
        }
        finally
        {
            motionPendingHost = null;
            if (!closing && openTab is not null) RenderTab();
        }
    }

    /// <summary>After martlet-host added the role, checks <paramref name="host"/> (about a minute) until its gateway offers the
    /// motion route and its service says it is provisioned. Returns null once it does, else what it saw last.</summary>
    private Task<string?> WaitForMotionAsync(PairedHost host, CancellationToken token) => WaitForRoleAsync(host, async check =>
    {
        if (check.Routes?.Any(r => r.RouteId == Audio2FaceHostConnection.MotionRouteId) != true || store is null) return new(check.Text);
        using var maker = new HostMotionMaker(store.DataDirectory, host.HostId);
        var availability = await maker.GetAvailabilityAsync(token);
        return availability.Available ? null : new(availability.Reason ?? check.Text);
    }, token);
}
