using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using Martlet.Core.Cluster;
using Martlet.Core.Installation;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>The cards of Companion › Voice, the UI only: Voice engine (this file), Voices (VoiceCards.Voices.cs) and Chatterbox
/// Original style (VoiceCards.ChatterboxStyle.cs). What they need from the app is <see cref="IVoiceCardsServices"/>. MainWindow
/// keeps one for the window's life: it keeps the computer the engine list shows.</summary>
internal sealed partial class VoiceCards(IVoiceCardsServices services)
{
    /// <summary>The computer Another of your computers' engine list sets up (null: the one that speaks, else the first).</summary>
    private string? voiceEngineHost;

    /// <summary>Companion › Voice › Voice engine: one option picker (MainWindow.OptionPicker.cs) of every way Martlet can speak
    /// on the chosen computer (this PC, or another paired computer). Each row says where the engine runs, its graphics memory
    /// and how soon it speaks; the chosen engine's details add what it can do, its other needs as chips, its license and one
    /// button that sets it up there and switches Speaking to it (MainWindow.VoiceEngines.cs does the switch). Rows read as
    /// <c>Picker-VoiceEngine-&lt;key&gt;</c> and <c>PickerFacts-VoiceEngine-&lt;key&gt;</c>; the shown engine's details as
    /// <c>PickerDetail-VoiceEngine</c>, <c>PickerFact-VoiceEngine-&lt;fact&gt;</c>, <c>PickerState-VoiceEngine</c>,
    /// <c>VoiceEngineAbilities-&lt;key&gt;</c>, <c>VoiceEngineFeatures-&lt;key&gt;</c> and its button
    /// <c>VoiceEngineUse-&lt;key&gt;</c>; <c>VoiceEngineNone</c> says when no engine can run on the shown computer. Under Another
    /// of your computers, <c>SpeakingHost-&lt;host&gt;</c> picks the computer.</summary>
    internal Border VoiceEngines(SetupRoute? route, PairedHost? thisPc, bool onThisPc)
    {
        var stack = new List<UIElement> { Heading("Voice engine") };
        var speakingHost = route is { RouteType: SetupRouteType.GatewayF5 } ? route.Gateway?.HostId : null;
        PairedHost? target = thisPc;
        if (!onThisPc)
        {
            var hosts = services.Hosts().Where(h => h.HostId != thisPc?.HostId).ToArray();
            if (hosts.Length == 0)
            {
                var none = Note("No other computers are paired yet.", new Thickness(0, 0, 0, 4));
                AutomationProperties.SetAutomationId(none, "HostChoices-speaking");
                stack.Add(none);
                stack.Add(Row(PageButton("Add a computer", () => services.RunNodeAction(NodeAction.AddComputer), primary: true, id: "SetupAddComputer-speaking")));
                return Card([.. stack]);
            }
            target = hosts.FirstOrDefault(h => h.HostId == voiceEngineHost) ?? hosts.FirstOrDefault(h => h.HostId == speakingHost)
                ?? hosts.FirstOrDefault(h => services.HostCheck(h.HostId)?.Offers?.Keys.Any(HostRoles.Speaks) == true) ?? hosts[0];
            stack.Add(HelpTip.Explain("Pick a computer, then an engine. Martlet sets the engine up there and switches to it.",
                new Thickness(0, 0, 0, 6), "SpeakingHostPick", "voice engines on your computers", wiki: "Voices#set-up-an-engine"));
            stack.Add(SpeakingHostPills(hosts, target.HostId, speakingHost));
            if (services.HostCheck(target.HostId) is { Reachable: false })
            {
                var unreachable = Note($"{target.HostId} isn't reachable right now; Martlet checks it again when you pick an engine.",
                    new Thickness(0, 4, 0, 0));
                unreachable.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
                AutomationProperties.SetAutomationId(unreachable, "SpeakingHostStatus");
                stack.Add(unreachable);
            }
        }
        else
        {
            stack.Add(HelpTip.Explain("Choose one to see what it needs and can do. Its button sets it up and switches to it; the " +
                "engine it replaces stops.", new Thickness(0, 0, 0, 6), "VoiceEngines", "voice engines", wiki: "Voices#set-up-an-engine"));
            var machine = services.Machine;
            if (thisPc is null && !machine.DockerRunning)
                stack.Add(Note(machine.DockerInstalled ? "Engines run in Docker Desktop; Martlet starts it when needed."
                    : "Engines run in Docker Desktop; Martlet installs it on the first setup.", new Thickness(0, 0, 0, 2)));
        }

        var where = onThisPc ? "this PC" : target!.HostId;
        var spots = SpeechEngines.All.Select(engine => (Engine: engine, Spot: Spot(engine, target, onThisPc, route, where))).ToList();
        var anyInUse = spots.Any(s => s.Spot.InUse);
        // The first engine for the graphics card that can run here; on a computer without one, Chatterbox Nano on its processor.
        var recommended = spots.FirstOrDefault(s => s.Spot.Cannot is null && s.Engine.NeedsGpu).Engine ??
            spots.FirstOrDefault(s => s.Spot.Cannot is null && s.Engine == SpeechEngines.ChatterboxNano).Engine;

        if (!anyInUse && spots.All(s => s.Spot.Cannot is not null))
        {
            var none = Note($"No voice engine can run on {where}, so pick another of your computers " +
                "or a cloud voice under A cloud provider.", new Thickness(0, 4, 0, 6));
            none.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            AutomationProperties.SetAutomationId(none, "VoiceEngineNone");
            stack.Add(none);
        }
        var options = JobOptions.VoiceEngines(spots, anyInUse ? null : recommended).Select(option =>
        {
            var (engine, spot) = spots.First(s => s.Engine.Key == option.Key);
            return option with { Details = () => EngineDetails(engine), Action = () => EngineButton(engine, spot, target, onThisPc) };
        }).ToList();
        stack.Add(services.OptionPickerBody("VoiceEngine", options));

        // A host set up before one engine per computer can still run engines Speaking no longer uses, each holding its model in
        // the graphics card's memory (the engine that speaks may then fail to load).
        var speaking = SpeechEngines.ForRoute(route?.GatewaySnapshot?.RouteId);
        if (target is not null && speaking is not null && speakingHost == target.HostId &&
            HostRoles.OtherVoiceEngines(services.HostCheck(target.HostId)?.Offers, speaking.HostRoleKind) is { Count: > 0 } others)
        {
            var names = HostRoles.Names(others.Select(o => o.Kind));
            var idle = Note($"{(onThisPc ? "This PC" : where)} also runs {names}, using graphics memory {speaking.Name} needs.", new Thickness(0, 10, 0, 0));
            idle.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            AutomationProperties.SetAutomationId(idle, "SpeakingEngineOthers");
            stack.Add(idle);
            stack.Add(Row(PageButton($"Stop {names}", () => services.StopIdleVoiceEngines(target.HostId, speaking), id: "SpeakingEngineRelease")));
        }

        // A Windows computer whose graphics card also does other work: Windows moves memory out instead of failing when the
        // card fills, and the voice falls behind.
        if (target is not null)
        {
            var check = services.HostCheck(target.HostId);
            var here = target.HostId == thisPc?.HostId;
            var voiceKind = speakingHost == target.HostId && speaking is not null ? speaking.HostRoleKind
                : check?.Offers?.Keys.FirstOrDefault(HostRoles.Speaks);
            var thinkingHere = here && MainWindow.IsLocalOllama(services.Settings.ThinkingRoute());
            if (SharedGpu.Warning(here ? "This PC" : target.HostId, SharedGpu.OnWindows(here, services.Hardware(target.HostId)),
                    voiceKind is null ? null : SpeechEngines.ForRoleKind(voiceKind)?.Name, SharedGpu.Neighbours(check?.Offers?.Keys, thinkingHere, check?.Offers))
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
                PageButton("Add a computer", () => services.RunNodeAction(NodeAction.AddComputer), id: "SetupAddComputer-speaking"),
                PageButton("Check hosts", () => services.RunNodeAction(NodeAction.CheckHost), id: "SetupCheckHosts-speaking"),
                PageButton("Open Devices", services.OpenDevices, id: "SetupOpenMap-speaking")));
        else if (thisPc is not null)
            stack.Add(Row(PageButton("Check this PC", () => services.RunNodeAction(NodeAction.CheckHost, thisPc.HostId), link: true, id: "SetupCheckLocal-speaking")));
        return Card([.. stack]);
    }

    /// <summary>The computers the engine list can set up, as pills; choosing one only shows its engines.</summary>
    private WrapPanel SpeakingHostPills(IReadOnlyList<PairedHost> hosts, string shown, string? speakingHost)
    {
        var pills = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
        foreach (var host in hosts)
        {
            var check = services.HostCheck(host.HostId);
            var label = host.HostId + (host.HostId == speakingHost ? " · speaking" : check?.Reachable == false ? " · not reachable" : "");
            var pill = new RadioButton { Content = label, GroupName = "SpeakingHost", IsChecked = host.HostId == shown };
            pill.SetResourceReference(FrameworkElement.StyleProperty, "FilterPill");
            AutomationProperties.SetName(pill, label);
            AutomationProperties.SetAutomationId(pill, "SpeakingHost-" + host.HostId);
            var id = host.HostId;
            pill.Checked += (_, _) =>
            {
                if (voiceEngineHost == id) return;
                voiceEngineHost = id;
                services.Redraw();
            };
            pills.Children.Add(pill);
        }
        return pills;
    }

    /// <summary>Where <paramref name="engine"/> stands on the shown computer (<see cref="MainWindow.EngineSpot"/>).</summary>
    private MainWindow.EngineSpot Spot(SpeechEngine engine, PairedHost? target, bool onThisPc, SetupRoute? route, string where)
    {
        var hostId = target?.HostId;
        var inUse = hostId is not null && route is { RouteType: SetupRouteType.GatewayF5 } && route.Gateway?.HostId == hostId &&
            SpeechEngines.ForRoute(route.GatewaySnapshot?.RouteId) == engine;
        var check = hostId is null ? null : services.HostCheck(hostId);
        var ready = check?.Offers?.ContainsKey(engine.HostRoleKind) == true;
        var pending = !inUse && hostId is not null && services.PendingSpeakingHost == hostId && SpeakingEngineChoice.Current == engine;
        var cannot = inUse || ready ? null : onThisPc ? services.ThisPcCannot(engine) : services.CannotHand(hostId!, engine.HostRoleKind, "speaking");
        var state = inUse ? $"Speaking on {where}."
            : pending ? $"Setting up on {where}..."
            : cannot ?? (ready ? $"Ready on {where}." : null);
        return new(inUse, ready, pending, cannot, state);
    }

    /// <summary>An engine's button in its details: set it up and use it on the shown computer, use it when it is ready there,
    /// or say it is in use or being set up (then it is off). <c>VoiceEngineUse-&lt;key&gt;</c>.</summary>
    private Button EngineButton(SpeechEngine engine, MainWindow.EngineSpot spot, PairedHost? target, bool onThisPc)
    {
        var label = spot.InUse ? "In use" : spot.Pending ? "Setting up..." : spot.Ready ? $"Use {engine.Name}" : $"Set up and use {engine.Name}";
        var button = PageButton(label, () => services.UseVoiceEngine(engine, onThisPc ? null : target),
            primary: !spot.InUse && !spot.Pending, id: "VoiceEngineUse-" + engine.Key);
        AutomationProperties.SetName(button, spot.InUse || spot.Pending ? $"{label} {engine.Name}" : label);
        button.IsEnabled = !spot.InUse && !spot.Pending && spot.Cannot is null;
        return button;
    }

    /// <summary>An engine's own lines in its details: the rundown of what it can do (<see cref="MainWindow.AbilitiesLine"/>,
    /// whose tooltips name its tags) and its other needs as chips (<c>VoiceEngineFeatures-&lt;key&gt;</c>).</summary>
    private static IEnumerable<UIElement> EngineDetails(SpeechEngine engine)
    {
        string? Tip(string feature) => feature switch
        {
            "Streams" => "Starts speaking before a sentence is finished.",
            _ => null
        };
        yield return MainWindow.AbilitiesLine(engine.Key, engine.Abilities, engine.Tags);
        var chips = Chips(engine.Key, engine.Features, Tip);
        chips.Margin = new Thickness(0, 4, 0, 6);
        yield return chips;
    }

    /// <summary>Short chips in one wrapping line, read as one comma-separated list (<c>VoiceEngineFeatures-&lt;key&gt;</c>).</summary>
    private static TextBlock Chips(string key, IReadOnlyList<string> features, Func<string, string?>? tip)
    {
        var chips = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var feature in features)
        {
            var label = new TextBlock { Text = feature };
            label.SetResourceReference(FrameworkElement.StyleProperty, "ChipText");
            var chip = new Border { Child = label };
            chip.SetResourceReference(FrameworkElement.StyleProperty, "Chip");
            if (tip?.Invoke(feature) is { Length: > 0 } help) chip.ToolTip = help;
            chips.Inlines.Add(new InlineUIContainer(chip) { BaselineAlignment = BaselineAlignment.Center });
        }
        AutomationProperties.SetName(chips, string.Join(", ", features));
        AutomationProperties.SetAutomationId(chips, "VoiceEngineFeatures-" + key);
        return chips;
    }
}
