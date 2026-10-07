using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using Martlet.Core.Cluster;
using Martlet.Core.Installation;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>Companion › Voice › Voice engine: one list of every way Martlet can speak on the chosen computer (this PC, or
/// another paired computer), each with what it needs and can do as short chips and one button that sets it up there and
/// switches Speaking to it. Switching cleans up: a computer runs one voice engine at a time (martlet-host stops the others
/// when one is added, and Martlet stops leftovers when it switches to one already installed), and the engine Speaking
/// leaves on another computer stops there once Speaking has moved (<see cref="LeavingEngine"/>), unless failover keeps
/// the same engine as a backup. Rows read as <c>VoiceEngine-&lt;key&gt;</c> (title and badge),
/// <c>VoiceEngineFeatures-&lt;key&gt;</c>, <c>VoiceEngineState-&lt;key&gt;</c> and <c>VoiceEngineUse-&lt;key&gt;</c>
/// (key "windows" for a Windows voice); under Another of your computers, <c>SpeakingHost-&lt;host&gt;</c> picks the computer.</summary>
public partial class MainWindow
{
    internal const string WindowsVoiceKey = "windows";

    internal static readonly IReadOnlyList<string> WindowsVoiceFeatures =
        ["No Docker or download", "Built-in Windows voices"];

    /// <summary>The computer Another of your computers' engine list sets up (null: the one that speaks, else the first).</summary>
    private string? voiceEngineHost;

    /// <summary>Where one engine stands on the shown computer: speaking there, installed, being set up, why it can't run
    /// there, and that in words (null when the button already says it: not set up yet).</summary>
    private sealed record EngineSpot(bool InUse, bool Ready, bool Pending, string? Cannot, string? State);

    private Border VoiceEnginesCard(SetupRoute? route, PairedHost? thisPc, bool onThisPc)
    {
        var stack = new List<UIElement> { Heading("Voice engine") };
        var speakingHost = route is { RouteType: SetupRouteType.GatewayF5 } ? route.Gateway?.HostId : null;
        PairedHost? target = thisPc;
        if (!onThisPc)
        {
            var hosts = NetworkMap.Hosts(Inputs()).Where(h => h.HostId != thisPc?.HostId).ToArray();
            if (hosts.Length == 0)
            {
                var none = Note("No other computers are paired yet. Add one to speak there.", new Thickness(0, 0, 0, 4));
                AutomationProperties.SetAutomationId(none, "HostChoices-speaking");
                stack.Add(none);
                stack.Add(Row(PageButton("Add a computer", () => RunNodeAction(NodeAction.AddComputer), primary: true, id: "SetupAddComputer-speaking")));
                return Card([.. stack]);
            }
            target = hosts.FirstOrDefault(h => h.HostId == voiceEngineHost) ?? hosts.FirstOrDefault(h => h.HostId == speakingHost)
                ?? hosts.FirstOrDefault(h => hostChecks.GetValueOrDefault(h.HostId)?.Offers?.Keys.Any(HostRoles.Speaks) == true) ?? hosts[0];
            stack.Add(Note("Pick a computer, then an engine. Martlet sets it up there and switches to it.", new Thickness(0, 0, 0, 6)));
            stack.Add(SpeakingHostPills(hosts, target.HostId, speakingHost));
            if (hostChecks.GetValueOrDefault(target.HostId) is { Reachable: false })
            {
                var unreachable = Note($"{target.HostId} isn't reachable right now. Martlet checks it again when you pick an engine.",
                    new Thickness(0, 4, 0, 0));
                unreachable.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
                AutomationProperties.SetAutomationId(unreachable, "SpeakingHostStatus");
                stack.Add(unreachable);
            }
        }
        else
        {
            stack.Add(Note("Pick one. Martlet sets it up and switches to it; the engine it replaces stops.", new Thickness(0, 0, 0, 2)));
            if (thisPc is null && !machine.DockerRunning)
                stack.Add(Note(machine.DockerInstalled ? "Engines run in Docker Desktop; Martlet starts it when needed."
                    : "Engines run in Docker Desktop; Martlet installs it on the first setup.", new Thickness(0, 0, 0, 2)));
        }

        var where = onThisPc ? "this PC" : target!.HostId;
        var spots = SpeechEngines.All.Select(engine => (Engine: engine, Spot: Spot(engine, target, onThisPc, route, where))).ToList();
        var windowsInUse = onThisPc && route?.RouteType == SetupRouteType.LocalWindowsTts;
        var anyInUse = windowsInUse || spots.Any(s => s.Spot.InUse);
        // The first engine for the graphics card that can run here; on a computer without one a Windows voice stays the
        // suggestion, though Chatterbox Nano can run on its processor.
        var recommended = spots.FirstOrDefault(s => s.Spot.Cannot is null && s.Engine.NeedsGpu).Engine;

        var rows = new List<UIElement>();
        foreach (var (engine, spot) in spots.Where(s => s.Spot.Cannot is null))
            rows.Add(EngineRow(engine, spot, target, onThisPc, !anyInUse && engine == recommended));
        if (onThisPc) rows.Add(WindowsVoiceRow(route, windowsInUse, recommend: !anyInUse && recommended is null));
        foreach (var (engine, spot) in spots.Where(s => s.Spot.Cannot is not null))
            rows.Add(EngineRow(engine, spot, target, onThisPc, recommend: false));
        stack.AddRange(rows);

        // A host set up before one engine per computer can still run engines Speaking no longer uses, each holding its model in
        // the graphics card's memory (the engine that speaks may then fail to load).
        var speaking = SpeechEngines.ForRoute(route?.GatewaySnapshot?.RouteId);
        if (target is not null && speaking is not null && speakingHost == target.HostId &&
            HostRoles.OtherVoiceEngines(hostChecks.GetValueOrDefault(target.HostId)?.Offers, speaking.HostRoleKind) is { Count: > 0 } others)
        {
            var names = HostRoles.Names(others.Select(o => o.Kind));
            var idle = Note($"{(onThisPc ? "This PC" : where)} also runs {names}, using graphics memory {speaking.Name} needs.", new Thickness(0, 10, 0, 0));
            idle.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            AutomationProperties.SetAutomationId(idle, "SpeakingEngineOthers");
            stack.Add(idle);
            stack.Add(Row(PageButton($"Stop {names}", () => StopIdleVoiceEnginesAsync(target.HostId, speaking).Forget(), id: "SpeakingEngineRelease")));
        }

        // A Windows computer whose graphics card also does other work: Windows moves memory out instead of failing when the
        // card fills, and the voice falls behind.
        if (target is not null)
        {
            var check = hostChecks.GetValueOrDefault(target.HostId);
            var here = target.HostId == thisPc?.HostId;
            var voiceKind = speakingHost == target.HostId && speaking is not null ? speaking.HostRoleKind
                : check?.Offers?.Keys.FirstOrDefault(HostRoles.Speaks);
            var thinkingHere = here && IsLocalOllama(homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm));
            if (SharedGpu.Warning(here ? "This PC" : target.HostId, SharedGpu.OnWindows(here, HardwareStore?.Find(target.HostId)),
                    voiceKind is null ? null : SpeechEngines.ForRoleKind(voiceKind)?.Name, SharedGpu.Neighbours(check?.Offers?.Keys, thinkingHere))
                is { } shared)
            {
                var note = Note(shared, new Thickness(0, 10, 0, 0));
                note.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
                AutomationProperties.SetAutomationId(note, "SpeakingEngineSharedGpu");
                stack.Add(note);
            }
        }

        if (!onThisPc)
            stack.Add(Row(
                PageButton("Add a computer", () => RunNodeAction(NodeAction.AddComputer), id: "SetupAddComputer-speaking"),
                PageButton("Check hosts", () => RunNodeAction(NodeAction.CheckHost), id: "SetupCheckHosts-speaking"),
                PageButton("Open Devices", () => Navigate(NavDevices), id: "SetupOpenMap-speaking")));
        else if (thisPc is not null)
            stack.Add(Row(PageButton("Check this PC", () => RunNodeAction(NodeAction.CheckHost, thisPc.HostId), link: true, id: "SetupCheckLocal-speaking")));
        return Card([.. stack]);
    }

    /// <summary>The computers the engine list can set up, as pills; choosing one only shows its engines.</summary>
    private WrapPanel SpeakingHostPills(IReadOnlyList<PairedHost> hosts, string shown, string? speakingHost)
    {
        var pills = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
        foreach (var host in hosts)
        {
            var check = hostChecks.GetValueOrDefault(host.HostId);
            var label = host.HostId + (host.HostId == speakingHost ? " · speaking" : check?.Reachable == false ? " · not reachable" : "");
            var pill = new RadioButton { Content = label, GroupName = "SpeakingHost", IsChecked = host.HostId == shown };
            pill.SetResourceReference(StyleProperty, "FilterPill");
            AutomationProperties.SetName(pill, label);
            AutomationProperties.SetAutomationId(pill, "SpeakingHost-" + host.HostId);
            var id = host.HostId;
            pill.Checked += (_, _) =>
            {
                if (voiceEngineHost == id) return;
                voiceEngineHost = id;
                RenderTab();
            };
            pills.Children.Add(pill);
        }
        return pills;
    }

    private EngineSpot Spot(SpeechEngine engine, PairedHost? target, bool onThisPc, SetupRoute? route, string where)
    {
        var hostId = target?.HostId;
        var inUse = hostId is not null && route is { RouteType: SetupRouteType.GatewayF5 } && route.Gateway?.HostId == hostId &&
            SpeechEngines.ForRoute(route.GatewaySnapshot?.RouteId) == engine;
        var check = hostId is null ? null : hostChecks.GetValueOrDefault(hostId);
        var ready = check?.Offers?.ContainsKey(engine.HostRoleKind) == true;
        var pending = !inUse && hostId is not null && pendingJobHosts.GetValueOrDefault(SetupRole.Tts) == hostId && SpeakingEngineChoice.Current == engine;
        var cannot = inUse || ready ? null : onThisPc ? ThisPcCannot(engine) : CannotHand(hostId!, engine.HostRoleKind, "speaking");
        var state = inUse ? $"Speaking on {where}."
            : pending ? $"Setting up on {where}..."
            : cannot ?? (ready ? $"Ready on {where}." : null);
        return new(inUse, ready, pending, cannot, state);
    }

    /// <summary>Why this PC can't run <paramref name="engine"/>: an ARM64 processor, no NVIDIA graphics card, or one with too
    /// little memory (an engine that runs on the processor too only needs the first); null when it can or Martlet hasn't read
    /// this PC's hardware yet.</summary>
    private string? ThisPcCannot(SpeechEngine engine)
    {
        if (ReferenceEquals(machine, MachineInfo.Unknown)) return null;
        if (machine.ArmRefusal(Martlet.Core.Platforms.PlatformCatalog.EngineForHostRole(engine.HostRoleKind)) is { } arm) return arm;
        if (!engine.NeedsGpu) return null;
        var nvidia = machine.Gpus.Where(g => g.IsNvidia).OrderByDescending(g => g.MemoryGb ?? 0).FirstOrDefault();
        if (nvidia is null)
            return $"Needs an NVIDIA graphics card; this PC has {(machine.Gpus.Count == 0 ? "none" : string.Join(", ", machine.Gpus.Select(g => g.Describe())))}.";
        return nvidia.MemoryGb is { } gb && gb < engine.MinimumGpuMemoryGb - 0.25
            ? $"Needs an NVIDIA graphics card with {engine.MinimumGpuMemoryGb} GB+; this PC has {nvidia.Describe()}."
            : null;
    }

    private Border EngineRow(SpeechEngine engine, EngineSpot spot, PairedHost? target, bool onThisPc, bool recommend)
    {
        var label = spot.InUse ? "In use" : spot.Pending ? "Setting up..." : spot.Ready ? "Use" : "Set up and use";
        var button = PageButton(label, () => UseVoiceEngineAsync(engine, onThisPc ? null : target).Forget(),
            primary: recommend, id: "VoiceEngineUse-" + engine.Key);
        AutomationProperties.SetName(button, $"{label} {engine.Name}");
        button.IsEnabled = !spot.InUse && !spot.Pending && spot.Cannot is null;
        if (spot.Cannot is not null)
        {
            button.ToolTip = spot.Cannot;
            ToolTipService.SetShowOnDisabled(button, true);
            AutomationProperties.SetHelpText(button, spot.Cannot);
        }
        string? Tip(string feature) => feature switch
        {
            "Streams" => "Starts speaking before a sentence is finished.",
            _ => null
        };
        return EngineRow(engine.Key, engine.Name, engine.Summary, engine.Abilities, engine.Tags, engine.RunsOn, engine.Features, Tip,
            spot.InUse ? "in use" : recommend ? "recommended" : null, spot.State, warn: spot.Cannot is not null, spot.InUse, button);
    }

    private Border WindowsVoiceRow(SetupRoute? route, bool inUse, bool recommend)
    {
        var none = windowsVoices is { Count: 0 };
        var state = inUse && windowsVoices is null ? $"Speaking with {WindowsVoices.DisplayName(route!.VoiceId!)}."
            : none ? "No voices are installed in Windows. Add a language with its voice in Windows Settings."
            : null;
        var label = inUse ? "In use" : none ? "Try again" : "Use";
        var button = PageButton(label, () => UseWindowsVoiceAsync(null).Forget(), primary: recommend, id: "VoiceEngineUse-" + WindowsVoiceKey);
        AutomationProperties.SetName(button, $"{label} Windows voice");
        button.IsEnabled = !inUse;
        var extra = new List<UIElement>();
        if (none) extra.Add(Row(PageButton("Open Windows speech settings", OpenWindowsSpeechSettings, id: "SetupWindowsSpeechSettings")));
        else if (inUse && windowsVoices is null)
            extra.Add(Row(
                PageButton("Change Windows voice", () => FindWindowsVoicesAsync().Forget(), id: "SetupChangeWindowsVoice"),
                PageButton("Hear it", () => PreviewWindowsVoiceAsync(route!.VoiceId!).Forget(), id: "SetupHearWindowsVoice")));
        else if (inUse)
        {
            var voiceId = route!.VoiceId!;
            var choice = new ComboBox { ItemsSource = windowsVoices, MinHeight = 30, MaxWidth = 420, MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Left,
                SelectedItem = windowsVoices!.FirstOrDefault(v => v.Id == voiceId), Margin = new Thickness(0, 8, 0, 0) };
            AutomationProperties.SetName(choice, "Windows voice");
            AutomationProperties.SetAutomationId(choice, "SetupWindowsVoice");
            choice.SelectionChanged += (_, _) =>
            {
                if (choice.SelectedItem is WindowsVoice picked && picked.Id != voiceId) UseWindowsVoiceAsync(picked.Id).Forget();
            };
            extra.Add(choice);
            extra.Add(Row(PageButton("Hear it", () => PreviewWindowsVoiceAsync((choice.SelectedItem as WindowsVoice)?.Id ?? voiceId).Forget(),
                id: "SetupHearWindowsVoice")));
        }
        return EngineRow(WindowsVoiceKey, "Windows voice", "Simple and light; nothing to set up.", VoiceAbilities.WindowsVoice, [],
            RunsOnText(Martlet.Core.Planning.FootprintCatalog.WindowsVoiceId), WindowsVoiceFeatures, null, inUse ? "in use" : recommend ? "recommended" : null,
            state, warn: none, inUse, button, extra);
    }

    /// <summary>Where a voice that isn't an engine runs, from its footprint (<see cref="Martlet.Core.Planning.FootprintCatalog.WindowsVoiceId"/>,
    /// <see cref="Martlet.Core.Planning.FootprintCatalog.OpenAiVoiceId"/>).</summary>
    internal static string RunsOnText(string footprintId) => Martlet.Core.Planning.FootprintCatalog.Default.Find(footprintId)?.WhereItRuns ?? "";

    /// <summary>One engine: its name with a badge, its strength in a few words, the rundown of what it can do
    /// (<see cref="AbilitiesLine"/>) and where it runs (<see cref="RunsOnLine"/>), its other needs as chips, where it stands
    /// on the shown computer (when the button doesn't already say it), and its button on the right.</summary>
    private Border EngineRow(string key, string name, string summary, VoiceAbilities abilities, IReadOnlyList<VoiceTag> tags,
        string runsOn, IReadOnlyList<string> features, Func<string, string?>? tip, string? badge, string? state, bool warn, bool inUse,
        Button button, IEnumerable<UIElement>? extra = null)
    {
        var title = OptionTitle(name, badge, 15);
        AutomationProperties.SetName(title, name + (badge is null ? "" : " · " + badge));
        AutomationProperties.SetAutomationId(title, "VoiceEngine-" + key);
        var text = new StackPanel();
        text.Children.Add(title);
        text.Children.Add(Note(summary, new Thickness(0, 2, 0, 0)));
        text.Children.Add(AbilitiesLine(key, abilities, tags));
        text.Children.Add(RunsOnLine(key, runsOn));
        text.Children.Add(Chips(key, features, tip));
        if (state is not null)
        {
            var stateLine = Note(state, new Thickness(0, 4, 0, 0));
            if (warn) stateLine.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            AutomationProperties.SetAutomationId(stateLine, "VoiceEngineState-" + key);
            text.Children.Add(stateLine);
        }
        foreach (var element in extra ?? []) text.Children.Add(element);
        button.VerticalAlignment = VerticalAlignment.Top;
        button.Margin = new Thickness(12, 0, 0, 0);
        var row = new DockPanel();
        DockPanel.SetDock(button, Dock.Right);
        row.Children.Add(button);
        row.Children.Add(text);
        var option = new Border { Child = row, BorderThickness = new Thickness(inUse ? 2 : 1), CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 8, 0, 0) };
        option.SetResourceReference(Border.BorderBrushProperty, inUse ? "AccentBrush" : "BorderBrush");
        return option;
    }

    /// <summary>The quick rundown of what a voice can do, in one wrapping line: "✓ Voice cloning   ✓ Laughs &amp; sighs
    /// ◐ Emotions: whispering only" (✓ yes, ◐ partly, ✕ no). It reads as <see cref="VoiceAbilities.Describe"/>
    /// (<c>VoiceEngineAbilities-&lt;key&gt;</c>: "Voice cloning: yes. Laughs &amp; sighs: yes. Emotions: whispering only.").
    /// Each item's tooltip says what it means and, when the voice has it, which of its <paramref name="tags"/> the reply
    /// writes for it.</summary>
    internal static TextBlock AbilitiesLine(string key, VoiceAbilities abilities, IReadOnlyList<VoiceTag> tags)
    {
        var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var item in abilities.Items)
        {
            var (mark, brush) = item.Level switch
            {
                AbilityLevel.Yes => ("\u2713", "SuccessBrush"),
                AbilityLevel.Partly => ("\u25D0", "AccentBrush"),
                _ => ("\u2715", "MutedBrush")
            };
            var glyph = new Run(mark + "\u00A0");
            glyph.SetResourceReference(TextElement.ForegroundProperty, brush);
            var words = new Run(item.Note is null ? item.Name : $"{item.Name}: {item.Note}");
            if (item.Level == AbilityLevel.No) words.SetResourceReference(TextElement.ForegroundProperty, "MutedBrush");
            var spelled = string.Join(" ", abilities.TagsFor(item, tags).Select(t => t.Text));
            var span = new Span(glyph) { ToolTip = item.Help + (spelled.Length > 0 ? " " + spelled : "") };
            span.Inlines.Add(words);
            if (line.Inlines.Count > 0) line.Inlines.Add(new Run("     "));
            line.Inlines.Add(span);
        }
        AutomationProperties.SetName(line, abilities.Describe());
        AutomationProperties.SetAutomationId(line, "VoiceEngineAbilities-" + key);
        return line;
    }

    /// <summary>Where a voice runs and how much graphics memory it takes, one muted line (<c>VoiceEngineRunsOn-&lt;key&gt;</c>):
    /// "Runs on an NVIDIA GPU: about 3.7 GB of graphics memory, up to 4.2 GB (6 GB+ card).", "Runs on the CPU: no graphics
    /// card needed." or "Runs online: nothing runs on your computers."</summary>
    internal static TextBlock RunsOnLine(string key, string runsOn)
    {
        // A number stays with its unit ("4.2 GB", "6 GB+ card") when the line wraps; screen readers and MCP get plain spaces.
        var shown = runsOn.Replace(" GB", "\u00A0GB", StringComparison.Ordinal).Replace("+ card", "+\u00A0card", StringComparison.Ordinal);
        var line = Note(shown, new Thickness(0, 4, 0, 0));
        AutomationProperties.SetName(line, runsOn);
        AutomationProperties.SetAutomationId(line, "VoiceEngineRunsOn-" + key);
        return line;
    }

    /// <summary>Short chips in one wrapping line, read as one comma-separated list (<c>VoiceEngineFeatures-&lt;key&gt;</c>).</summary>
    private static TextBlock Chips(string key, IReadOnlyList<string> features, Func<string, string?>? tip)
    {
        var chips = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var feature in features)
        {
            var label = new TextBlock { Text = feature };
            label.SetResourceReference(StyleProperty, "ChipText");
            var chip = new Border { Child = label };
            chip.SetResourceReference(StyleProperty, "Chip");
            if (tip?.Invoke(feature) is { Length: > 0 } help) chip.ToolTip = help;
            chips.Inlines.Add(new InlineUIContainer(chip) { BaselineAlignment = BaselineAlignment.Center });
        }
        AutomationProperties.SetName(chips, string.Join(", ", features));
        AutomationProperties.SetAutomationId(chips, "VoiceEngineFeatures-" + key);
        return chips;
    }

    // ---------- switching ----------

    /// <summary>One click: Speaking uses <paramref name="engine"/> on <paramref name="host"/> (null: this PC), set up there
    /// first when it isn't yet. The engine choice follows only when Speaking moved (or is moving) to it.</summary>
    private async Task UseVoiceEngineAsync(SpeechEngine engine, PairedHost? host, bool confirmed = false)
    {
        if (store is null || setupService is null || closing) return;
        var before = SpeakingEngineChoice.Current;
        try
        {
            SpeakingEngineChoice.Save(store.DataDirectory, engine);
            if (host is null) await UseEngineHereAsync(engine, confirmed);
            else await AssignJobAsync(HostJob.SpeakingFor(engine), "host:" + host.HostId);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ActionText.Text = $"Couldn't save the voice engine: {error.Message}";
        }
        finally
        {
            if (!closing)
            {
                KeepEngineChoice(engine, before);
                RenderHome();
            }
        }
    }

    /// <summary>The choice stays with <paramref name="engine"/> when it speaks or is being set up for Speaking; otherwise it
    /// goes back to <paramref name="before"/>, or to the engine a host speaks with. A choice made since (another engine
    /// switched to meanwhile) is left alone.</summary>
    private void KeepEngineChoice(SpeechEngine engine, SpeechEngine before)
    {
        if (store is null || SpeakingEngineChoice.Current != engine) return;
        var route = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Tts);
        if (pendingJobHosts.ContainsKey(SetupRole.Tts) ||
            route?.RouteType == SetupRouteType.GatewayF5 && SpeechEngines.ForRoute(route.GatewaySnapshot?.RouteId) == engine)
            return;
        try
        {
            SpeakingEngineChoice.Save(store.DataDirectory, before);
            SpeakingEngineChoice.Sync(store.DataDirectory, homeSettings);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { ErrorLog.Warn($"Couldn't restore the voice engine choice: {error.Message}"); }
    }

    /// <summary>Speaking with <paramref name="engine"/> on this PC: hands Speaking over when this PC's host service already runs
    /// it, otherwise one confirmation (what it installs, what it replaces and its model's terms) and one run window that sets
    /// up the host service when needed, installs the engine and switches over.</summary>
    private async Task UseEngineHereAsync(SpeechEngine engine, bool confirmed = false)
    {
        var job = HostJob.SpeakingFor(engine);
        var thisPc = ThisPcHost();
        var offers = thisPc is null ? null : await ThisPcOffersAsync(thisPc);
        if (closing) return;
        if (thisPc is not null && offers?.ContainsKey(engine.HostRoleKind) == true)
        {
            await AssignJobAsync(job, "host:" + thisPc.HostId);
            return;
        }
        if (ThisPcCannot(engine) is { } cannot)
        {
            ActionText.Text = $"{engine.Name} can't run on this PC. {cannot}";
            return;
        }
        var leaving = LeavingEngine(thisPc?.HostId, engine);
        var replaced = HostRoles.OtherVoiceEngines(offers, engine.HostRoleKind);
        if (!confirmed && !ConfirmationDialog.Confirm(this,
                $"Set up {engine.Name} on this PC and speak with it?\n\n" +
                (thisPc is not null ? ""
                    : machine.DockerInstalled ? "Martlet first sets up its host service in Docker Desktop. "
                    : "Martlet first installs Docker Desktop (it asks for its own terms) and sets up its host service. ") +
                $"{engine.Name} is a large download; Martlet shows the progress and switches Speaking to it when it's ready." +
                (replaced.Count > 0
                    ? $" Only one voice engine runs on a computer, so {HostRoles.Names(replaced.Select(r => r.Kind))} on this PC stops " +
                      "(its download is kept, so switching back is quick)."
                    : "") +
                LeavingNote(leaving) + "\n\n" + EngineTerms(engine) + " Reply text and your voice sample stay on this PC.",
                $"Set up {engine.Name}"))
            return;
        var switched = await SetUpJobHereAsync(job, new Dictionary<string, string>(StringComparer.Ordinal), $"Speak with {engine.Name} on this PC");
        if (!switched || closing || leaving is null) return;
        ActionText.Text += await StopLeftVoiceEngineAsync(leaving);
    }

    /// <summary>The model's terms in a sentence or two: its licence, any restriction, and the voice-rights reminder.</summary>
    private static string EngineTerms(SpeechEngine engine) =>
        $"{engine.Name}'s model licence: {engine.WeightsLicense}{(engine.NonCommercial ? ", non-commercial use only" : "")}." +
        (SpeechEngines.IsChatterbox(engine) ? " Every reply carries Resemble AI's inaudible watermark." : "") +
        (engine == SpeechEngines.Dia ? " Nari Labs forbids using it to imitate real people without their permission." : "") +
        " Only use voices that are yours or that you have permission to use.";

    /// <summary>The engine Speaking uses on a paired host now, when switching Speaking to <paramref name="newHostId"/> (null:
    /// not a paired host, such as a Windows or cloud voice) with <paramref name="newEngine"/> leaves it idle there. Null when
    /// Speaking stays on that host (martlet-host already runs one voice engine at a time there) or when failover keeps a
    /// host that runs the same engine as a backup.</summary>
    private (PairedHost Host, SpeechEngine Engine)? LeavingEngine(string? newHostId, SpeechEngine? newEngine)
    {
        var route = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Tts);
        if (route is not { RouteType: SetupRouteType.GatewayF5 } || route.Gateway?.HostId is not { } hostId || hostId == newHostId) return null;
        if (SpeechEngines.ForRoute(route.GatewaySnapshot?.RouteId) is not { } engine || FindHost(hostId) is not { } host) return null;
        if (engine == newEngine && clusterPlan.For(ClusterJobs.Speaking)?.Failover == true) return null;
        return (host, engine);
    }

    /// <summary>What a switch does to <paramref name="leaving"/>, for its confirmation (starting with a space), or nothing.</summary>
    private string LeavingNote((PairedHost Host, SpeechEngine Engine)? leaving)
    {
        if (leaving is not { } left) return "";
        var shared = !clusterEnabled && PairedWith(left.Host.HostId)?.Any(device => !IsThisDevice(device.DeviceId)) == true;
        return $" {left.Engine.Name} then stops on {HostWhere(left.Host)} to free its graphics card's memory (its download is kept, " +
            "so switching back is quick)." + (shared ? " Other computers that speak with it there stop speaking until they choose another engine." : "");
    }

    private string HostWhere(PairedHost host) => host.HostId == ThisPcHost()?.HostId ? "this PC" : host.HostId;

    /// <summary>Once Speaking has left <paramref name="leaving"/> (the owner already agreed), stops that engine on its host so
    /// its model leaves the graphics card's memory. Returns the outcome in words, starting with a space, or nothing when
    /// Speaking didn't move or the engine no longer runs there.</summary>
    private async Task<string> StopLeftVoiceEngineAsync((PairedHost Host, SpeechEngine Engine)? leaving)
    {
        if (leaving is not { } left || closing) return "";
        var route = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Tts);
        if (route?.RouteType == SetupRouteType.GatewayF5 && route.Gateway?.HostId == left.Host.HostId) return "";
        if (hostChecks.GetValueOrDefault(left.Host.HostId)?.Offers is { } offers && !offers.ContainsKey(left.Engine.HostRoleKind)) return "";
        return await ReleaseVoiceEnginesAsync(left.Host, [HostRoles.Get(left.Engine.HostRoleKind)]);
    }

    /// <summary>Stops the voice engines <paramref name="hostId"/> runs besides the one that speaks there, after saying what
    /// that does, so their models leave its graphics card's memory. Host runs: other changes go ahead meanwhile.</summary>
    private async Task StopIdleVoiceEnginesAsync(string hostId, SpeechEngine speaking)
    {
        if (store is null || closing) return;
        if (FindHost(hostId) is not { } host) return;
        var others = HostRoles.OtherVoiceEngines(hostChecks.GetValueOrDefault(hostId)?.Offers, speaking.HostRoleKind);
        if (others.Count == 0) return;
        var names = HostRoles.Names(others.Select(o => o.Kind));
        if (!ConfirmationDialog.Confirm(this,
                $"Stop {names} on {hostId}? That frees graphics card memory there for {speaking.Name}, which keeps speaking. Downloads are kept, so " +
                "switching back is quick. Any other computer that still speaks with them there stops speaking until it chooses another engine.",
                "Stop them"))
            return;
        try { ActionText.Text = (await ReleaseVoiceEnginesAsync(host, others)).TrimStart(); }
        finally
        {
            if (!closing) RenderHome();
        }
    }
}
