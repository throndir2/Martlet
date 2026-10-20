using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Cluster;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>
/// Companion › Lip sync (docs/AVATARS.md#the-lip-sync-pool), the UI only: who moves the character's mouth. In order: Now (what
/// moves the mouth for the next sentence, and the coverage check's problem), lip-sync's pool as the shared list control (which
/// MainWindow builds: <paramref name="poolList"/>; the places in the list, in order, each with its own settings: the first one that
/// is on and free moves the mouth, and with none on the mouth follows the voice's loudness), then this PC's own Audio2Face set-up.
/// What each line says is in <see cref="LipSyncPageModel"/>; what the page needs from the app is <see cref="ILipSyncPageServices"/>.
/// MainWindow keeps the list and the older "who does lip-sync" choice (the character's profile and the shared plan's lip-sync job,
/// which Home, Devices and the coverage checks show) in step both ways (MainWindow.LipSyncPool.cs); a member's settings save through
/// the shared list's own save (<paramref name="savePoolList"/>), so the list follows it. Automation IDs: <c>LipSyncNow</c>, the
/// list's <c>Pool-lip-sync-...</c> (and per member <c>Pool-lip-sync-Endpoint</c>, <c>-SaveEndpoint</c>, <c>-Change-&lt;key&gt;</c>,
/// <c>-Install-&lt;host&gt;</c> and <c>-Check-&lt;host&gt;</c>), <c>LipSyncDockerAbout</c>, <c>SetupLipSyncHostThisPc</c>,
/// <c>SetupLipSyncUseLocal</c> and <c>SetupLipSyncCheckLocal</c>.
/// </summary>
internal sealed class LipSyncPage(ILipSyncPageServices services, Func<PoolListOptions, UIElement> poolList,
    Func<PoolListOptions, PoolList, PoolList, string, bool> savePoolList)
{
    /// <summary>Draws the page's cards into <paramref name="page"/>.</summary>
    internal void Render(Panel page)
    {
        var model = new LipSyncPageModel(services, services.EnsurePool());
        page.Children.Add(PageNowCard(model.NowText, services.Problem, "LipSyncNow"));
        PoolListOptions options = null!;
        options = new()
        {
            Area = PoolAreas.LipSync,
            Intro = "The places that move the mouth with Audio2Face, in order. Each sentence goes to the first one that is free; " +
                "when all are busy, the mouth follows the voice's loudness for that moment. With none on, it always does. " +
                "Only Martlet's generated voice is sent to a computer in the list.",
            Wiki = "The-Character#lip-sync",
            Status = model.MemberStatus,
            Settings = member => MemberSettings(model, member, options),
            CanAdd = model.CanAdd,
            Saved = (_, after) => services.FollowListAsync(after)
        };
        page.Children.Add(poolList(options));
        page.Children.Add(ThisPcCard(model));
    }

    /// <summary>A member's settings under its row: this PC's own service address, or a computer's Audio2Face (install it, change
    /// its model, check it).</summary>
    private UIElement? MemberSettings(LipSyncPageModel model, PoolMember member, PoolListOptions options)
    {
        var id = "Pool-" + PoolAreas.LipSync.Id;
        var stack = new StackPanel();
        if (member.Kind == PoolMemberKind.ThisPc)
        {
            stack.Children.Add(HelpTip.Explain("An Audio2Face service you run yourself on each companion PC (NVIDIA's NIM or the " +
                "open-source engine), at a loopback address. Martlet looks for it before each sentence.", new Thickness(0, 0, 0, 6),
                "LipSyncOwnService", "your own Audio2Face service", wiki: "The-Character#lip-sync"));
            var address = new TextBox { MinWidth = 260, Margin = new Thickness(0, 0, 8, 0), Text = services.OwnServiceEndpoint(member).AbsoluteUri };
            AutomationProperties.SetAutomationId(address, id + "-Endpoint");
            AutomationProperties.SetName(address, "Address of your own Audio2Face service");
            InputHint.SetText(address, "http://127.0.0.1:52000/");
            var save = PageButton("Save address", () =>
            {
                if (LipSyncPageModel.ParseEndpoint(address.Text) is not { } endpoint)
                {
                    services.ShowStatus("Enter a loopback address with a port, such as http://127.0.0.1:52000/.");
                    return;
                }
                if (model.WithEndpoint(member.Key, endpoint) is not ({ } before, { } after)) return;
                savePoolList(options, before, after, $"Lip-sync looks for your own Audio2Face service at {endpoint.Authority} now.");
                services.CheckOwnService();
            }, id: id + "-SaveEndpoint");
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(address);
            line.Children.Add(save);
            stack.Children.Add(line);
            return stack;
        }
        var hostId = member.HostId!;
        if (services.FindHost(hostId) is not { } host)
        {
            stack.Children.Add(Note($"{hostId} isn't paired on this PC; pair it on Devices to use it here.", new Thickness(0, 0, 0, 0)));
            return stack;
        }
        var runs = model.Runs(hostId);
        var buttons = new List<Button>();
        if (services.ChangesRolesOn(host) && services.CheckOf(hostId)?.Reachable == true)
        {
            if (runs)
            {
                var change = PageButton("Change model", () => services.LaunchOnHost(host, HostAction.Change(HostRoles.Audio2Face)), id: $"{id}-Change-{member.Key}");
                AutomationProperties.SetName(change, $"Change Audio2Face on {hostId}");
                buttons.Add(change);
            }
            else if (model.Cannot(hostId) is null)
                buttons.Add(PageButton("Install Audio2Face", () => services.LaunchOnHost(host, HostRoles.Get(HostRoles.Audio2Face).Add),
                    primary: true, id: $"{id}-Install-{hostId}"));
        }
        buttons.Add(PageButton("Check it", () => services.CheckHost(hostId), id: $"{id}-Check-{hostId}"));
        stack.Children.Add(Note(model.ComputerText(hostId), new Thickness(0, 0, 0, 6)));
        stack.Children.Add(Row([.. buttons]));
        return stack;
    }

    /// <summary>Audio2Face on this PC: what its graphics card means for it, and setting it up in Martlet's host service here
    /// (which then goes first in the list).</summary>
    private Border ThisPcCard(LipSyncPageModel model)
    {
        var thisPc = services.ThisPcHost();
        var fits = model.ThisPcFits;
        var arm = model.ThisPcRefusal;
        var stack = new List<UIElement> { Heading("Audio2Face on this PC") };
        var about = Note(model.ThisPcAbout, new Thickness(0, 0, 0, 6));
        if (!fits || arm is not null) about.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        AutomationProperties.SetAutomationId(about, "LipSyncDockerAbout");
        stack.Add(about);
        if (thisPc is null)
        {
            stack.Add(HelpTip.Explain(model.ThisPcSetUpText, new Thickness(0, 0, 0, 6), "LipSyncThisPcSetUp", "setting up Audio2Face here",
                wiki: "The-Character#lip-sync"));
            var setUp = PageButton("Set up Audio2Face with Docker", services.SetUpThisPc, primary: fits, id: "SetupLipSyncHostThisPc");
            setUp.IsEnabled = arm is null;
            stack.Add(Row(setUp));
            return Card([.. stack]);
        }
        stack.Add(Note(model.ThisPcHostText(thisPc), new Thickness(0, 0, 0, 6)));
        var buttons = new List<Button>();
        if (!model.Runs(thisPc.HostId))
        {
            var use = PageButton("Install Audio2Face on this PC", () => services.UseHost(thisPc.HostId), primary: fits, id: "SetupLipSyncUseLocal");
            use.IsEnabled = arm is null;
            buttons.Add(use);
        }
        buttons.Add(PageButton("Check it", () => services.CheckHost(thisPc.HostId), id: "SetupLipSyncCheckLocal"));
        stack.Add(Row([.. buttons]));
        return Card([.. stack]);
    }
}
