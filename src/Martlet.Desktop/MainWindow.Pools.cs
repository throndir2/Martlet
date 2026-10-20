using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>What an area's page gives the shared pool list (<see cref="MainWindow.PoolListCard"/>).</summary>
internal sealed record PoolListOptions
{
    public required PoolArea Area { get; init; }
    public string Heading { get; init; } = "Where it runs";
    /// <summary>Text under the heading: the page shows its first sentence, and all of it behind a "?".</summary>
    public string? Intro { get; init; }
    /// <summary>The wiki page and heading ("Singing#set-it-up") with the full instructions; null: no link. The page shows only the
    /// <see cref="Intro"/>'s first sentence, with a "?" (Help-Pool-&lt;area&gt;-Intro) that has all of it and links there.</summary>
    public string? Wiki { get; init; }
    /// <summary>The member's state in plain words ("Runs Chatterbox", "Not reachable"...); null: nothing to add.</summary>
    public Func<PoolMember, string?>? Status { get; init; }
    /// <summary>The area's settings editor for one member (engine, model, workflow...); null: the member has none. Shown under the
    /// member's row when its Settings button is pressed.</summary>
    public Func<PoolMember, UIElement?>? Settings { get; init; }
    /// <summary>Whether a paired computer can be added (it can run the area's engine); null: every paired computer.</summary>
    public Func<PairedHost, bool>? CanAdd { get; init; }
    /// <summary>The settings a member gets when it is added (engine, model...); null: none.</summary>
    public Func<PoolMember, PoolMember>? NewMember { get; init; }
    /// <summary>The area's own way to add a cloud provider (provider, model, key and the owner's agreement), when it takes cloud
    /// members. It adds the member itself.</summary>
    public Func<UIElement>? AddCloud { get; init; }
    /// <summary>Lines under the members about models outside this list that do the area's work too (Vision: the Thinking pool's
    /// models that see); null: none.</summary>
    public Func<IEnumerable<UIElement>>? Also { get; init; }
    /// <summary>What happens while nothing in the list is on, from the area's own decision (Vision and Hearing:
    /// <see cref="MainWindow.SenseInstead"/>); null: the area's fallback.</summary>
    public Func<string?>? Instead { get; init; }
    /// <summary>Runs after the list was saved, with the list before and after (the area follows it: its route, its backend).</summary>
    public Func<PoolList, PoolList, Task>? Saved { get; init; }
    /// <summary>Draws the page again after a change; null: the Companion page.</summary>
    public Action? Rerender { get; init; }
}

/// <summary>Pools on each area's page (docs/CLUSTER.md#pools-one-ordered-list-of-members-per-area): the shared list control,
/// the first lists made from Devices › Sharing work, and Speaking, Listening and Thinking following their lists.</summary>
public partial class MainWindow
{
    /// <summary>The members whose settings are shown, by area and member key.</summary>
    private readonly HashSet<string> poolExpanded = new(StringComparer.Ordinal);
    /// <summary>The route member each area saw last, so a route chosen on its page (Use it) moves to the front of its list.</summary>
    private readonly Dictionary<string, string?> poolRouteSeen = new(StringComparer.Ordinal);

    private static readonly (PoolArea Area, SetupRole Role, string Job)[] RoutePools =
    [
        (PoolAreas.Speaking, SetupRole.Tts, WorkSharingJobs.Speaking),
        (PoolAreas.Listening, SetupRole.Stt, WorkSharingJobs.Listening),
        (PoolAreas.Thinking, SetupRole.Llm, WorkSharingJobs.Thinking)
    ];

    // ---------- the shared list control ----------

    /// <summary>One area's pool as an ordered list (automation IDs <c>Pool-&lt;area&gt;-...</c>): a summary of what the list
    /// means now; for each member its name and state, On, Up, Down, Remove, Settings (the area's editor) and which companion PCs
    /// may use it; then Add for this PC, each paired computer, each graphics card, an address and a cloud provider. An empty
    /// list (no member on) is off, or the area's fallback on this PC.</summary>
    internal Border PoolListCard(PoolListOptions options)
    {
        var area = options.Area;
        var id = "Pool-" + area.Id;
        var list = store is null ? new PoolList { Area = area.Id } : WorkSharingRoster.Shared.Pool(store.DataDirectory, area) ?? new PoolList { Area = area.Id };
        var stack = new List<UIElement> { Heading(options.Heading) };
        if (options.Intro is not null)
            stack.Add(HelpTip.Explain(options.Intro, new Thickness(0, 0, 0, 8), id + "-Intro", options.Heading, options.Wiki));
        var summary = Note(PoolListSummary.Describe(area, list, list.NoneOn ? options.Instead?.Invoke() : null), new Thickness(0, 0, 0, 8));
        AutomationProperties.SetAutomationId(summary, id + "-Summary");
        stack.Add(summary);
        var companions = SharingCompanions();
        for (var i = 0; i < list.Members.Count; i++) stack.Add(PoolRow(options, list, i, companions));
        if (options.Also?.Invoke() is { } also) stack.AddRange(also);
        stack.Add(PoolAdd(options, list));
        return Card([.. stack]);
    }

    private UIElement PoolRow(PoolListOptions options, PoolList list, int i, IReadOnlyList<(string Device, string Name)> companions)
    {
        var area = options.Area;
        var id = "Pool-" + area.Id;
        var member = list.Members[i];
        var key = member.Key;
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        var row = NetworkRowFrame();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var onBox = new CheckBox { Content = "On", IsChecked = !member.Off, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetAutomationId(onBox, $"{id}-On-{i}");
        AutomationProperties.SetName(onBox, $"Use {member.Name} for {area.Title}");
        onBox.Checked += (_, _) => SavePoolList(options, list, list.With(member with { Off = false }), $"{area.Title} uses {member.Name} again.");
        onBox.Unchecked += (_, _) => SavePoolList(options, list, list.With(member with { Off = true }), $"{area.Title} no longer uses {member.Name}, but its settings are kept.");
        buttons.Children.Add(onBox);
        Button Small(string text, string automation, string name, bool enabled, Action run)
        {
            var button = new Button { Content = text, Margin = new Thickness(0, 0, 6, 0), IsEnabled = enabled, MinWidth = 0, Padding = new Thickness(8, 2, 8, 2) };
            AutomationProperties.SetAutomationId(button, automation);
            AutomationProperties.SetName(button, name);
            button.Click += (_, _) => run();
            return button;
        }
        buttons.Children.Add(Small("Up", $"{id}-Up-{i}", $"Try {member.Name} earlier for {area.Title}", i > 0,
            () => SavePoolList(options, list, list.Move(key, -1), $"{area.Title} now tries {member.Name} earlier.")));
        buttons.Children.Add(Small("Down", $"{id}-Down-{i}", $"Try {member.Name} later for {area.Title}", i < list.Members.Count - 1,
            () => SavePoolList(options, list, list.Move(key, 1), $"{area.Title} now tries {member.Name} later.")));
        var settings = options.Settings?.Invoke(member);
        var expanded = poolExpanded.Contains(area.Id + " " + key);
        if (settings is not null)
            buttons.Children.Add(Small(expanded ? "Hide settings" : "Settings", $"{id}-Settings-{i}", $"Settings of {member.Name} for {area.Title}", true, () =>
            {
                if (!poolExpanded.Remove(area.Id + " " + key)) poolExpanded.Add(area.Id + " " + key);
                (options.Rerender ?? RenderTab)();
            }));
        buttons.Children.Add(Small("Remove", $"{id}-Remove-{i}", $"Remove {member.Name} from {area.Title}", true,
            () => SavePoolList(options, list, list.Without(key), $"{member.Name} is no longer in the {area.Title} list.")));
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        var facts = new List<string>();
        if (member.Off) facts.Add("off; its settings are kept");
        else if (!member.Consented(area.Id)) facts.Add("waits for your agreement to send requests there");
        if (options.Status?.Invoke(member) is { Length: > 0 } status) facts.Add(status);
        if (member.OnlyFor.Count > 0) facts.Add("kept for " + string.Join(" and ", member.OnlyFor));
        var kind = member.Kind switch
        {
            PoolMemberKind.ThisPc => "each companion PC itself",
            PoolMemberKind.Computer => "a paired computer",
            PoolMemberKind.Gpu => "one graphics card",
            PoolMemberKind.Address => "a service you run",
            _ => "a cloud provider"
        };
        var detail = Capitalized(kind) + (facts.Count == 0 ? "." : "; " + string.Join("; ", facts) + ".");
        var rowText = NetworkRowText($"{id}-Member-{i}", $"{i + 1}. {member.Name}", detail);
        // Screen readers and MCP read the row as one sentence ("1: This PC: Each companion PC itself."), not "1. This PC. ...".
        AutomationProperties.SetName(((Panel)rowText).Children[0], $"{i + 1}: {member.Name}: {detail}");
        row.Children.Add(rowText);
        panel.Children.Add(row);
        if (companions.Count > 1 || member.OnlyFor.Count > 0)
        {
            var only = new ComboBox { MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
            AutomationProperties.SetAutomationId(only, $"{id}-OnlyFor-{i}");
            AutomationProperties.SetName(only, $"Which companion PCs may use {member.Name} for {area.Title}");
            only.Items.Add(new ComboBoxItem { Content = "Every companion PC", Tag = "" });
            foreach (var (device, name) in companions) only.Items.Add(new ComboBoxItem { Content = "Only " + name, Tag = device });
            foreach (var device in member.OnlyFor.Where(d => companions.All(c => c.Device != d)))
                only.Items.Add(new ComboBoxItem { Content = "Only " + device, Tag = device });
            only.SelectedIndex = member.OnlyFor.Count == 0 ? 0
                : Math.Max(0, only.Items.Cast<ComboBoxItem>().ToList().FindIndex(item => (string)item.Tag == member.OnlyFor[0]));
            only.SelectionChanged += (_, _) =>
            {
                if (only.SelectedItem is not ComboBoxItem { Tag: string device }) return;
                if (device.Length == 0 ? member.OnlyFor.Count == 0 : member.OnlyFor.Count == 1 && member.OnlyFor[0] == device) return;
                SavePoolList(options, list, list.With(member with { OnlyFor = device.Length == 0 ? [] : [device] }),
                    device.Length == 0 ? $"Every companion PC may use {member.Name} for {area.Title} now." : $"{member.Name} is kept for {device} now.");
            };
            panel.Children.Add(only);
        }
        if (expanded && settings is not null)
        {
            var box = new Border { Child = settings, Margin = new Thickness(24, 6, 0, 0) };
            panel.Children.Add(box);
        }
        return panel;
    }

    private UIElement PoolAdd(PoolListOptions options, PoolList list)
    {
        var area = options.Area;
        var id = "Pool-" + area.Id;
        var add = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        add.Children.Add(Heading("Add to the list"));
        PoolMember New(PoolMember member) => options.NewMember?.Invoke(member) ?? member;
        void Add(PoolMember member) => SavePoolList(options, list, list.With(New(member)), $"{member.Name} is now in the {area.Title} list.");
        var buttons = new List<Button>();
        if (area.Takes(PoolMemberKind.ThisPc) && list.Find(PoolMember.ThisPcKey) is null)
            buttons.Add(PageButton("This PC", () => Add(PoolMember.ThisPc()), id: id + "-AddThisPc"));
        var hosts = NetworkMap.Hosts(Inputs()).Where(h => options.CanAdd?.Invoke(h) ?? true).ToArray();
        if (area.Takes(PoolMemberKind.Computer))
            foreach (var host in hosts.Where(h => list.Find(PoolMember.Computer(h.HostId).Key) is null))
                buttons.Add(PageButton(host.HostId, () => Add(PoolMember.Computer(host.HostId)), id: $"{id}-AddHost-{host.HostId}"));
        if (area.Takes(PoolMemberKind.Gpu))
            foreach (var host in hosts)
            {
                var cards = HostRoles.NvidiaCards(HardwareStore?.Find(host.HostId)) ?? 0;
                if (cards < 2) continue;
                foreach (var card in Enumerable.Range(1, Math.Min(cards, PoolMember.MaximumCard)).Where(c => list.Find(PoolMember.Gpu(host.HostId, c).Key) is null))
                    buttons.Add(PageButton($"{host.HostId}, card {card}", () => Add(PoolMember.Gpu(host.HostId, card)), id: $"{id}-AddGpu-{host.HostId}-{card}"));
            }
        if (buttons.Count == 0 && !area.Takes(PoolMemberKind.Address) && !area.Takes(PoolMemberKind.Cloud))
            add.Children.Add(Note("Every machine that can do it is in the list already; pair another computer on Devices to add more.", new Thickness(0, 0, 0, 0)));
        add.Children.Add(Row([.. buttons]));
        if (area.Takes(PoolMemberKind.Address))
        {
            var address = new TextBox { MinWidth = 260, Margin = new Thickness(0, 0, 8, 0) };
            AutomationProperties.SetAutomationId(address, id + "-Address");
            AutomationProperties.SetName(address, $"Address of a service you run for {area.Title}");
            InputHint.SetText(address, area.Id == PoolAreas.PictureMaking.Id ? "http://192.168.1.20:8188" : "http://192.168.1.20:11434/v1");
            var addAddress = PageButton("Add address", () =>
            {
                var text = address.Text.Trim();
                if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                {
                    ActionText.Text = "Enter an address that starts with http:// or https://.";
                    return;
                }
                Add(PoolMember.Service(uri.AbsoluteUri));
            }, id: id + "-AddAddress");
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            line.Children.Add(address);
            line.Children.Add(addAddress);
            add.Children.Add(line);
        }
        if (area.Takes(PoolMemberKind.Cloud) && options.AddCloud is { } cloud) add.Children.Add(cloud());
        return add;
    }
}
