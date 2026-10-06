using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;
using Martlet.Providers;
using Martlet.Providers.Ollama;

namespace Martlet.Desktop;

/// <summary>Companion › Deep thinking: Thinking answers you; Deep thinking works out what Martlet hands it in the background
/// (think_longer) while the conversation carries on. Whether Martlet may think longer at all (Off, saved with the reply settings,
/// so your computers share it), how hard, for how long, how often and when it shares the result, and where it thinks (this PC's
/// own choice, deep-thinking.json): the Thinking model, another of your computers (its Deep thinking role), a second model in
/// Ollama on this PC or a cloud provider. A think always runs alongside the conversation, so it needs a model of its own
/// (<see cref="DeepThinkingPlan"/>).</summary>
public partial class MainWindow
{
    private enum DeepPlace { Off, Same, Computer, ThisPc, Cloud }

    private DeepPlace? deepPlaceShown;
    private SideBySideFit? deepLocalFit;
    private string? deepLocalFitModel;
    private static readonly ThinkEffort[] ThinkEfforts = [ThinkEffort.Medium, ThinkEffort.High];
    private static readonly ThinkDelivery[] ThinkDeliveries = [ThinkDelivery.WhenFree, ThinkDelivery.NextMessage];

    private static readonly IReadOnlyList<CloudProvider> DeepThinkingProviders =
    [
        .. ChatCompletionsEndpointCatalog.NamedEndpoints.Select(e => new CloudProvider(e.Name, e.BaseUrl, true, e.DefaultModelId, true)),
        new("OpenAI", OpenAiChatBaseUrl, true, OpenAiTextGenerationCatalog.DefaultModelId, true),
        CustomCloud
    ];

    private static DeepPlace PlaceOf(DeepThinkingSettings deep, bool on) => !on ? DeepPlace.Off : deep.Place switch
    {
        DeepThinkingPlace.Host => DeepPlace.Computer,
        DeepThinkingPlace.Endpoint when deep.Origin == LocalOllamaBaseUrl => DeepPlace.ThisPc,
        DeepThinkingPlace.Endpoint => DeepPlace.Cloud,
        _ => DeepPlace.Same
    };

    private void RenderDeepThinkingTab(Panel page)
    {
        var route = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        var deep = store is null ? new DeepThinkingSettings() : DeepThinkingSettings.Load(store.DataDirectory);
        var routes = homeSettings?.Setup?.Routes ?? [];
        var plan = DeepThinkingPool.For(deep, routes).Plan;
        var on = ThinkLongerSettings.Of(homeSettings?.Generation).On;
        var current = PlaceOf(deep, on);
        var place = deepPlaceShown ?? current;

        var now = new TextBlock { Text = DeepThinkingNow(deep, route, plan, on), FontSize = 15, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(now, "DeepThinkingNow");
        var why = Note(on ? plan.Why : "Turn it on by choosing where it thinks below.", new Thickness(0, 4, 0, 0));
        if (on && !plan.Available) why.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        AutomationProperties.SetAutomationId(why, "DeepThinkingParallel");
        page.Children.Add(Card(Heading("Now"), now, why));

        page.Children.Add(ThinkLongerCard(homeSettings?.Generation?.ThinkLonger, route, plan));

        var where = new StackPanel();
        where.Children.Add(Heading("Where it thinks"));
        where.Children.Add(Note("A think always runs alongside the conversation, so Martlet keeps talking at full speed. That needs a " +
            "model of its own: another of your computers, a cloud provider, a second model on this PC, or Thinking's own model when " +
            "its provider answers several requests at once. Off applies to all your computers; where it thinks stays on this PC.",
            new Thickness(0, 0, 0, 10)));
        foreach (var (value, label, detail) in new (DeepPlace, string, string)[]
        {
            (DeepPlace.Off, "Off", "Martlet answers everything right away and never thinks in the background."),
            (DeepPlace.Same, "Same as Thinking", "Thinking's own model thinks it through, when its provider answers several requests at once."),
            (DeepPlace.Computer, "Another of your computers", "A paired computer's Deep thinking role thinks while this PC talks: local, private and parallel."),
            (DeepPlace.ThisPc, "Ollama on this PC", "A second model of its own beside Thinking's, when both fit on the graphics card."),
            (DeepPlace.Cloud, "A cloud provider or server", "OpenRouter, OpenAI, NVIDIA Build or another compatible server. Requests may cost money.")
        })
        {
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = label + (value == current ? "  \u00b7  in use" : ""), FontSize = 15,
                FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            text.Children.Add(Note(detail, new Thickness(0, 2, 0, 0)));
            var option = new RadioButton { Content = text, GroupName = "DeepPlace", IsChecked = value == place, Margin = new Thickness(0, 0, 0, 10) };
            AutomationProperties.SetName(option, $"{label}: {detail}");
            AutomationProperties.SetAutomationId(option, "DeepPlace-" + value);
            option.Checked += (_, _) =>
            {
                deepPlaceShown = value;
                RenderTab();
            };
            where.Children.Add(option);
        }
        page.Children.Add(Card(where));

        page.Children.Add(place switch
        {
            DeepPlace.Off => Card(Heading("Off"),
                Note("Martlet answers everything right away and never offers to think something over in the background. Choose " +
                    "this when no other computer or provider is free to think; your computers share it.", new Thickness(0, 0, 0, 0)),
                Row(current == DeepPlace.Off ? null
                    : PageButton("Turn Deep thinking off", () => TurnDeepThinkingAsync(false, "Deep thinking is off.").Forget(),
                        primary: true, id: "DeepThinkingTurnOff"))),
            DeepPlace.Computer => DeepComputersCard(deep, on),
            DeepPlace.ThisPc => DeepLocalCard(deep, route),
            DeepPlace.Cloud => DeepCloudCard(deep, route),
            _ => DeepSameCard(route, routes, current)
        });
    }

    /// <summary>Same as Thinking: Thinking's own model, which can think alongside the conversation only when its provider answers
    /// several requests at once (not a model on this PC or a paired computer).</summary>
    private Border DeepSameCard(SetupRoute? route, IReadOnlyList<SetupRoute> routes, DeepPlace current)
    {
        var same = DeepThinkingPlan.For(new(), routes);
        var note = Note(route is null ? "Set up Thinking first."
            : same.Available ? $"Thinks with {route.ModelId}, Thinking's model, with Thinking steps on." : same.Why, new Thickness(0, 0, 0, 0));
        AutomationProperties.SetAutomationId(note, "DeepThinkingSameStatus");
        return Card(Heading("Same as Thinking"), note,
            Row(current == DeepPlace.Same || !same.Available ? null
                : PageButton("Think with the Thinking model", () => SaveDeepThinkingAsync(new(), null, "Deep thinking now uses the Thinking model.").Forget(),
                    primary: true, id: "DeepThinkingUseSame")));
    }

    /// <summary>The Now card's line: whether Martlet may think longer, where a think goes and whether it can run there.</summary>
    private static string DeepThinkingNow(DeepThinkingSettings deep, SetupRoute? route, DeepThinkingPlan plan, bool on)
    {
        if (!on) return "Off. Martlet answers everything right away and never thinks in the background.";
        var where = deep.Pool is { Count: > 0 } ? deep.DescribeAll().Replace("the Thinking model", route is null ? "the Thinking model" : $"the Thinking model ({route.ModelId})")
            : deep.Separate ? deep.Describe() : route is null ? "the Thinking model (not set up yet)" : $"the Thinking model ({route.ModelId})";
        return plan.Available ? $"Thinks on {where}, in parallel with the conversation."
            : $"On, but it can't think on {where}, so Martlet doesn't offer to think things over. Choose another place below.";
    }

    // ---------- Thinking longer (saved with the reply settings) ----------

    /// <summary>How hard Martlet thinks something over and when it shares the result (a think has no time or hourly limit).
    /// Whether it may
    /// at all is Where it thinks › Off. It saves a moment after each change, with the reply settings.</summary>
    private Border ThinkLongerCard(ThinkLongerSettings? saved, SetupRoute? route, DeepThinkingPlan plan)
    {
        var current = saved ?? new ThinkLongerSettings();
        var changed = (Action)(() => { });
        ComboBox Choice(string id, string name, IEnumerable<string> items, int selected)
        {
            var box = new ComboBox { Width = 260, ItemsSource = items.ToArray(), SelectedIndex = selected, HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetAutomationId(box, id);
            AutomationProperties.SetName(box, name);
            box.SelectionChanged += (_, _) => changed();
            return box;
        }
        var effort = Choice("ThinkLongerEffort", "How hard it thinks", ["Medium (default)", "High"], Array.IndexOf(ThinkEfforts, current.HowHard));
        var when = Choice("ThinkLongerDelivery", "When Martlet shares the result",
            ["As soon as Martlet is free (default)", "When I talk next"], Array.IndexOf(ThinkDeliveries, current.When));
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        AutomationProperties.SetAutomationId(status, "ThinkLongerStatus");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        void Show(ThinkLongerSettings? settings)
        {
            var (text, problem) = ThinkLongerStatus(settings ?? new(), route, plan);
            status.Text = text;
            status.SetResourceReference(TextBlock.ForegroundProperty, problem ? "WarningBrush" : "MutedBrush");
            foreach (var control in new Control[] { effort, when }) control.IsEnabled = settings?.On ?? true;
        }
        Show(saved);
        ThinkLongerSettings Read(ThinkLongerSettings? loaded) => new()
        {
            Enabled = loaded?.Enabled,
            Effort = ThinkEfforts[Math.Max(0, effort.SelectedIndex)],
            Delivery = ThinkDeliveries[Math.Max(0, when.SelectedIndex)]
        };
        // There is no Save button: a change saves a moment later, into the newest saved reply settings (whether it is on stays
        // as saved: Where it thinks › Off turns it off).
        var autoSave = new AutoSave(() =>
        {
            ThinkLongerSettings? next = null;
            return SaveRepliesAsync(loaded =>
            {
                next = ThinkLongerSettings.Normalize(Read(loaded?.ThinkLonger));
                return GenerationSettings.Normalize((loaded ?? new()) with { ThinkLonger = next });
            }, generation =>
            {
                Show(generation?.ThinkLonger);
                var now = next ?? new();
                ErrorLog.Info($"Thinking longer: {now.HowHard} effort, no time or hourly limit, " +
                    $"shares {(now.When == ThinkDelivery.WhenFree ? "as soon as Martlet is free." : "when you talk next.")}");
            }, "Thinking longer saved. Reload an open conversation to use it.");
        });
        tabAutoSave = autoSave;
        changed = () => { tabEdited = true; autoSave.Changed(); };
        return Card(Heading("Thinking longer"),
            Note("Replies answer right away (Thinking steps are off by default). When a task really needs thought, such as writing " +
                "song lyrics, a story or a plan, or tricky math or code, Martlet can say it'll think it over and work on it in the " +
                "background while you keep talking, then bring it up when it's done.", new Thickness(0, 0, 0, 8)),
            status,
            TerminalRow("How hard", effort), TerminalRow("Share it", when),
            Note("It thinks with Thinking steps on, whatever replies use; one think runs at a time on each place it thinks on (tick " +
                "Think here too on more of your computers to think about several things at once), with no time limit and no limit " +
                "on how many. Stop (Esc) doesn't end it: its Cancel in the talk window or closing the conversation do. A paid " +
                "provider may charge for its thinking.",
                new Thickness(0, 8, 0, 0)));
    }

    /// <summary>Whether Martlet can think longer at all: Thinking hands the task off with a tool, so it needs a route that does
    /// function calling, and Deep thinking needs a model of its own to think on.</summary>
    private (string Text, bool Problem) ThinkLongerStatus(ThinkLongerSettings settings, SetupRoute? route, DeepThinkingPlan plan)
    {
        if (!settings.On) return ("Off. Martlet answers everything right away and never thinks in the background.", false);
        if (route is null) return ("On. Set up Thinking so Martlet can use it.", true);
        if (route.RouteType is not (SetupRouteType.OpenAi or SetupRouteType.ChatCompletions))
            return ("On, but your Thinking model can't use tools here (a Martlet host's model), so it can't hand a task off. Use " +
                "OpenAI or a Chat Completions endpoint (such as Ollama on this PC) for Thinking.", true);
        if (mcpTools.IsUnsupported(McpToolService.ModelKey($"{route.RouteType}", route.Origin, route.ModelId)))
            return ($"On, but {route.ModelId} turned down tools, so it can't hand a task off. Choose a Thinking model that can use tools.", true);
        if (!plan.Available)
            return ($"On, but Deep thinking has nowhere to think in parallel, so Martlet doesn't offer to think things over. {plan.Why}", true);
        return ($"On. When a task needs it, Martlet says it'll think it over and works on it in the background ({settings.HowHard} " +
            "effort, no time limit, no limit on how many) while you keep talking, " +
            (settings.When == ThinkDelivery.WhenFree ? "then brings it up as soon as it's free." : "then brings it up when you talk next."), false);
    }

    // ---------- where it thinks ----------

    /// <summary>Paired computers that can think: each with what it offers and Use it. A computer's Deep thinking role (its own
    /// Ollama) thinks beside its Thinking; one without it is offered Add Deep thinking, and Martlet thinks there once it runs.</summary>
    private Border DeepComputersCard(DeepThinkingSettings deep, bool on)
    {
        var stack = new List<UIElement> { Heading("Another of your computers") };
        var hosts = NetworkMap.Hosts(Inputs());
        if (hosts.Count == 0)
        {
            var none = Note("No computer is paired yet. Add one on Devices, then add the Deep thinking role there.", new Thickness(0, 0, 0, 4));
            AutomationProperties.SetAutomationId(none, "DeepThinkingHosts");
            stack.Add(none);
        }
        foreach (var host in hosts)
        {
            var check = hostChecks.GetValueOrDefault(host.HostId);
            var own = check?.Offers?.GetValueOrDefault(HostRoles.DeepThinking);
            var ollama = check?.Offers?.GetValueOrDefault(HostRoles.Ollama);
            var thinksThere = NetworkMap.ThinkingHost(homeSettings) == host.HostId;
            var usable = own is not null || ollama is not null && !thinksThere;
            // In use only on the route Use it would pick: a computer still thought on through its Ollama offers its new role.
            var inUse = on && deep.Place == DeepThinkingPlace.Host && deep.HostId == host.HostId && (deep.OnHostRole || own is null);
            var detail = inUse ? $"Thinks here ({deep.ModelId}{(deep.OnHostRole ? ", its Deep thinking role" : "")})."
                : own is not null ? $"Its Deep thinking role runs {own}."
                : ollama is not null && thinksThere ? $"Its Ollama ({ollama}) does Thinking for the conversation. Add the Deep thinking role " +
                    "there to think beside it."
                : ollama is not null ? $"Ollama runs {ollama}. Add the Deep thinking role to give Deep thinking a model of its own."
                : check?.Reachable == true ? "Add the Deep thinking role there to think on it."
                : check?.Reachable == false ? "Not reachable right now." : "Not checked yet.";
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = host.HostId, FontSize = 15, FontWeight = FontWeights.SemiBold });
            var line = Note(detail, new Thickness(0, 2, 0, 0));
            AutomationProperties.SetName(line, $"{host.HostId}: {detail}");
            AutomationProperties.SetAutomationId(line, "DeepThinkingHost-" + host.HostId);
            text.Children.Add(line);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (own is null && check?.Reachable == true && host.CanLaunch && CannotHand(host.HostId, HostRoles.DeepThinking, "deep thinking") is null)
            {
                var add = PageButton("Add Deep thinking", () => AddDeepRoleAsync(host).Forget(), primary: !usable && !inUse,
                    id: "DeepThinkingAddRole-" + host.HostId);
                AutomationProperties.SetName(add, $"Add Deep thinking on {host.HostId}");
                add.Margin = new Thickness(0, 0, 8, 0);
                buttons.Children.Add(add);
            }
            // Its Deep thinking role's model (and GPU or CPU, graphics card): the role's settings, showing what it runs now.
            if (own is not null && check?.Reachable == true && ChangesRolesOn(host))
            {
                var change = PageButton("Change model", () => LaunchOnHost(host, HostAction.Change(HostRoles.DeepThinking)),
                    id: "DeepThinkingChangeModel-" + host.HostId);
                AutomationProperties.SetName(change, $"Change the Deep thinking model on {host.HostId} (now {own})");
                change.Margin = new Thickness(0, 0, 8, 0);
                buttons.Children.Add(change);
            }
            var use = PageButton(inUse ? "In use" : "Use it", () => UseDeepHostAsync(host).Forget(), primary: !inUse && usable,
                id: "DeepThinkingUseHost-" + host.HostId);
            use.IsEnabled = !inUse;
            buttons.Children.Add(use);
            // Several computers think at once, one think each: this one besides the place chosen above.
            var pooled = deep.Places.Any(p => p.Place == DeepThinkingPlace.Host && p.HostId == host.HostId);
            var also = new CheckBox
            {
                Content = "Think here too", IsChecked = pooled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
                IsEnabled = pooled || usable && check?.Reachable == true,
                ToolTip = "Deep thinking also thinks on this computer, so several thinks run at once, one on each computer."
            };
            AutomationProperties.SetName(also, $"Think on {host.HostId} too");
            AutomationProperties.SetAutomationId(also, "DeepThinkingPool-" + host.HostId);
            also.Click += (_, _) =>
            {
                if (also.IsChecked == true) UseDeepHostAsync(host, alsoHere: true).Forget();
                else RemoveDeepHostAsync(host.HostId).Forget();
            };
            buttons.Children.Add(also);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            DockPanel.SetDock(buttons, Dock.Right);
            row.Children.Add(buttons);
            row.Children.Add(text);
            stack.Add(row);
        }
        var places = deep.Places.Count;
        var pool = Note(!on ? "Deep thinking is off."
            : places == 1 ? $"Thinks on one place ({deep.Describe()}): one think at a time. Tick Think here too on more computers to think about several things at once."
            : $"Thinks on {places} places at once ({deep.DescribeAll()}): each new think goes to a free one, the one sharing least with the conversation first.",
            new Thickness(0, 0, 0, 8));
        AutomationProperties.SetAutomationId(pool, "DeepThinkingPoolStatus");
        stack.Insert(1, pool);
        stack.Add(Row(hosts.Count == 0 ? PageButton("Add a computer", () => RunNodeAction(NodeAction.AddComputer), primary: true, id: "DeepThinkingAddComputer")
            : PageButton("Check computers", () => RunNodeAction(NodeAction.CheckHost), id: "DeepThinkingCheckHosts")));
        stack.Add(Note("The conversation so far (what fits in its 16 KiB) and the task go to that computer through its paired, pinned " +
            "connection. While it thinks there, Thinking, the voice and listening keep working here at full speed. The Deep thinking " +
            "role is an Ollama of its own, so it thinks even on the computer that does Thinking (they share its graphics card); " +
            "without it, a computer that also does Thinking for the conversation can't think alongside it. Adding the role asks which " +
            "model it runs; Change model switches it later: the current model keeps thinking until the new one is downloaded and " +
            "loaded, then Martlet thinks with the new one. A computer's model can " +
            "only think as long as its Martlet allows: update it to this version for thinks over a minute.", new Thickness(0, 8, 0, 0)));
        return Card([.. stack]);
    }

    /// <summary>Installs the Deep thinking role on a paired computer (its own run window, with the model choice) and, once it
    /// runs, thinks there.</summary>
    private async Task AddDeepRoleAsync(PairedHost host)
    {
        if (closing) return;
        if (CannotHand(host.HostId, HostRoles.DeepThinking, "deep thinking") is { } cannot)
        {
            ActionText.Text = $"Deep thinking can't be set up on {host.HostId}: {cannot}";
            return;
        }
        if (await RunHostActionAsync(host, HostRoles.Get(HostRoles.DeepThinking).Add) is null || closing) return;
        await UseDeepHostAsync(host);
    }

    /// <summary>Thinks on <paramref name="host"/>: in place of where it thinks now, or with <paramref name="alsoHere"/> as one more
    /// place, so several thinks run at once (where it thinks now becomes this computer when it can't think there).</summary>
    private async Task UseDeepHostAsync(PairedHost host, bool alsoHere = false)
    {
        if (closing) return;
        ActionText.Text = $"Checking {host.HostId}...";
        try
        {
            var check = await HostControl.CheckAsync(host.Pairing, HardwareStore, lifetime.Token);
            hostChecks[host.HostId] = check;
            if (check.Reachable != true)
            {
                ActionText.Text = $"Deep thinking wasn't moved. {host.HostId} didn't respond ({check.Text}).";
                RenderTab();
                return;
            }
            // Its own Deep thinking role first; otherwise its Ollama, when that doesn't do Thinking for the conversation.
            var route = check.Routes?.FirstOrDefault(r => r.RouteId == HostRoute.DeepThinkingRouteId) ??
                check.Routes?.FirstOrDefault(r => r.RouteId == HostJob.Thinking.RouteId);
            if (route is null)
            {
                ActionText.Text = $"{host.HostId} doesn't run Deep thinking yet. Add the Deep thinking role there, then choose it here.";
                RenderTab();
                return;
            }
            var own = route.RouteId == HostRoute.DeepThinkingRouteId;
            var next = new DeepThinkingSettings
            {
                Place = DeepThinkingPlace.Host, ModelId = route.ModelId, HostId = host.HostId, HostOrigin = host.Pairing.Origin,
                HostSpkiFingerprint = host.Pairing.SpkiFingerprint, HostDeviceId = host.Pairing.DeviceId,
                HostCredentialId = HostPairingCredential.ToGuid(host.Pairing.CredentialId), HostRouteId = own ? route.RouteId : null,
                ChosenAt = DateTimeOffset.Now
            };
            if (alsoHere)
            {
                var current = DeepThinkingSettings.Load(store!.DataDirectory);
                // Same as Thinking where it can't think is no place to keep: this computer takes its place.
                var keep = current.Separate || DeepThinkingPlan.For(current.Single, homeSettings?.Setup?.Routes ?? []).Available;
                var pooled = keep ? current.WithPool([.. current.Pool ?? [], next]) : next.WithPool(current.Pool ?? []);
                await SaveDeepThinkingAsync(pooled, null, $"Deep thinking now thinks on {host.HostId} too ({route.ModelId}): " +
                    $"{pooled.Places.Count} place{(pooled.Places.Count == 1 ? "" : "s")} think at once.", keepPool: false);
                return;
            }
            await SaveDeepThinkingAsync(next, null, own ? $"Deep thinking now runs on {host.HostId}'s Deep thinking role ({route.ModelId})."
                : $"Deep thinking now runs on {host.HostId} ({route.ModelId}).");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or InvalidOperationException or ContractException or Martlet.Avatar.Audio2Face.Remote.Audio2FaceHostException)
        {
            ActionText.Text = error.Message;
        }
    }

    /// <summary>Stops thinking on <paramref name="hostId"/> (Think here too unticked): the next place takes its turn when it was
    /// the first, and Same as Thinking when it was the only one.</summary>
    private async Task RemoveDeepHostAsync(string hostId)
    {
        if (store is null || closing) return;
        var current = DeepThinkingSettings.Load(store.DataDirectory);
        var left = current.Places.Where(p => !(p.Place == DeepThinkingPlace.Host && p.HostId == hostId)).ToArray();
        var next = left.Length == 0 ? new DeepThinkingSettings { ChosenAt = DateTimeOffset.Now } : left[0].WithPool(left.Skip(1));
        await SaveDeepThinkingAsync(next, null, $"Deep thinking no longer thinks on {hostId}.", keepPool: false);
    }

    /// <summary>A second model in Ollama on this PC, beside Thinking's: Ollama runs each model in its own process, so it thinks in
    /// parallel with the conversation while both fit on the graphics card (checked here, and before each think).</summary>
    private Border DeepLocalCard(DeepThinkingSettings deep, SetupRoute? thinking)
    {
        var beside = IsLocalOllama(thinking) ? thinking!.ModelId : null;
        var choices = (ollamaModels ?? []).Concat(LocalChatModels.Select(m => m.Id)).Distinct(StringComparer.Ordinal).ToArray();
        var model = new ComboBox { IsEditable = true, Width = 300, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = choices,
            Text = deep.Place == DeepThinkingPlace.Endpoint && deep.Origin == LocalOllamaBaseUrl ? deep.ModelId ?? ""
                : (ollamaModels ?? []).Concat(LocalChatModels.Select(m => m.Id)).FirstOrDefault(id => !OllamaSideBySide.Same(id, beside)) ?? "" };
        AutomationProperties.SetName(model, "Model for Deep thinking");
        AutomationProperties.SetAutomationId(model, "DeepThinkingLocalModel");
        var state = Note(ollamaModels is null ? "Check Ollama to see which models are downloaded."
            : ollamaModels.Count == 0 ? "Ollama is running, but no model is downloaded yet."
            : $"Downloaded: {string.Join(", ", ollamaModels)}.", new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(state, "DeepThinkingLocalStatus");
        var fit = Note("", new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(fit, "DeepThinkingLocalFit");
        AutomationProperties.SetLiveSetting(fit, AutomationLiveSetting.Polite);
        void ShowFit(string text, bool problem)
        {
            fit.Text = text;
            fit.SetResourceReference(TextBlock.ForegroundProperty, problem ? "WarningBrush" : "MutedBrush");
        }
        void Check(string id)
        {
            if (beside is null) ShowFit($"Thinking doesn't use Ollama on this PC, so {(id.Length > 0 ? id : "this model")} has it to " +
                "itself and thinks alongside the conversation.", false);
            else if (id.Length == 0) ShowFit("Choose a model.", true);
            else if (OllamaSideBySide.Same(id, beside))
                ShowFit($"{id} is Thinking's own model, which can't think something over while it answers you. Choose another model.", true);
            else if (deepLocalFitModel == id && deepLocalFit is { } known) ShowFit((known.Fits ? "Fits: " : "Doesn't fit: ") + known.Why, !known.Fits);
            else
            {
                ShowFit($"Checking whether {id} fits beside {beside} on the graphics card...", false);
                CheckDeepFitAsync(beside, id, ShowFit).Forget();
            }
        }
        Check(model.Text.Trim());
        model.SelectionChanged += (_, _) => { if (model.SelectedItem is string chosen) Check(chosen); };
        model.LostKeyboardFocus += (_, _) => Check(model.Text.Trim());
        return Card(Heading("Ollama on this PC"),
            Note("A second model can think here while Thinking's answers you, such as a larger one beside a small, fast one. Ollama runs " +
                "each model in its own process, so they answer at the same time, but only while both fit on the graphics card: Martlet " +
                "checks before each think and doesn't think it over when they don't. They share the graphics card, so replies may start " +
                "a little later while it thinks.", new Thickness(0, 0, 0, 8)),
            new Label { Content = "_Model", Target = model, Padding = new Thickness(0, 0, 0, 4) }, model, state, fit,
            Row(PageButton("Use Ollama on this PC", () =>
                {
                    var id = model.Text.Trim();
                    try { ChatCompletionsSetup.ModelId(id); }
                    catch (ContractException error) { ActionText.Text = error.Message; return; }
                    if (beside is not null && OllamaSideBySide.Same(id, beside))
                    {
                        ActionText.Text = $"{id} is Thinking's own model, which can't think something over while it answers you. Choose another model.";
                        return;
                    }
                    SaveDeepThinkingAsync(new() { Place = DeepThinkingPlace.Endpoint, Origin = LocalOllamaBaseUrl, ModelId = id, ChosenAt = DateTimeOffset.Now },
                        null, $"Deep thinking now uses {id} in Ollama on this PC." +
                        (ollamaModels is { } known && !LocalOllama.Serves(known, id) ? $" Download {id} on Thinking to use it." : "")).Forget();
                }, primary: true, id: "DeepThinkingUseLocal"),
                PageButton("Check Ollama", () =>
                {
                    deepLocalFitModel = null;
                    CheckOllamaAsync().Forget();
                }, id: "DeepThinkingCheckOllama")));
    }

    /// <summary>Checks whether <paramref name="deep"/> fits beside Thinking's <paramref name="thinking"/> in Ollama on this PC
    /// (read-only: it loads nothing) and shows the result.</summary>
    private async Task CheckDeepFitAsync(string thinking, string deep, Action<string, bool> show)
    {
        try
        {
            var fit = await LocalDeepThinking.CheckAsync(thinking, deep, loadThinking: false, lifetime.Token);
            deepLocalFit = fit;
            deepLocalFitModel = deep;
            ErrorLog.Info($"Deep thinking: {deep} {(fit.Fits ? "fits" : "doesn't fit")} beside {thinking} in Ollama on this PC. {fit.Why}");
            if (!closing) show((fit.Fits ? "Fits: " : "Doesn't fit: ") + fit.Why, !fit.Fits);
        }
        catch (OperationCanceledException) { }
    }
    /// <summary>A cloud provider or any OpenAI-compatible server (HTTPS, or a server on this PC), with its own key or Thinking's.</summary>
    private Border DeepCloudCard(DeepThinkingSettings deep, SetupRoute? thinking)
    {
        var saved = deep.Place == DeepThinkingPlace.Endpoint && deep.Origin != LocalOllamaBaseUrl ? deep : null;
        var provider = new ComboBox { ItemsSource = DeepThinkingProviders, MinHeight = 30, MaxWidth = 420, MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(provider, "Deep thinking provider");
        AutomationProperties.SetAutomationId(provider, "DeepThinkingProvider");
        provider.SelectedItem = saved is null ? DeepThinkingProviders[0] : DeepThinkingProviders.FirstOrDefault(p => p.BaseUrl == saved.Origin) ?? CustomCloud;
        var baseUrl = new TextBox { MaxLength = 2048, Width = 420, HorizontalAlignment = HorizontalAlignment.Left, Text = saved?.Origin ?? "" };
        AutomationProperties.SetName(baseUrl, "Deep thinking API base URL");
        AutomationProperties.SetAutomationId(baseUrl, "DeepThinkingBaseUrl");
        var baseUrlPanel = new StackPanel { Children = { new Label { Content = "API base URL (without /chat/completions)", Target = baseUrl, Padding = new Thickness(0, 8, 0, 4) }, baseUrl } };
        var model = new TextBox { MaxLength = 128, Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(model, "Deep thinking model ID");
        AutomationProperties.SetAutomationId(model, "DeepThinkingModel");
        var key = new PasswordBox { MaxLength = SecretLease.MaximumLength, Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(key, "Deep thinking API key");
        AutomationProperties.SetAutomationId(key, "DeepThinkingKey");
        var keyStatus = Note("", new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(keyStatus, "DeepThinkingKeyStatus");
        var consent = new CheckBox { Margin = new Thickness(0, 12, 0, 8) };
        AutomationProperties.SetAutomationId(consent, "DeepThinkingConsent");
        var consentText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        consent.Content = consentText;
        CloudProvider Selected() => provider.SelectedItem as CloudProvider ?? CustomCloud;
        string Url() => Selected().BaseUrl is { Length: > 0 } fixedUrl ? fixedUrl : baseUrl.Text.Trim();
        void Refresh(bool keepModel)
        {
            var p = Selected();
            baseUrlPanel.Visibility = p == CustomCloud ? Visibility.Visible : Visibility.Collapsed;
            if (!keepModel) model.Text = saved is not null && saved.Origin == Url() ? saved.ModelId ?? "" : p.DefaultModel ?? "";
            var url = Url();
            keyStatus.Text = saved?.CredentialId is not null && saved.Origin == url
                    ? $"Its {p.Name} key is saved. Leave this empty to keep it, or paste a new key."
                : thinking is { RouteType: SetupRouteType.ChatCompletions, CredentialId: not null } && thinking.Origin == url
                    ? "Leave this empty to use Thinking's key for the same provider, or paste another key."
                : p.NeedsKey ? $"Paste your {p.Name} API key. Martlet saves it in Windows Credential Manager."
                : "Add a key only if your server needs one.";
            consentText.Text = $"I choose {p.Name} for Deep thinking. When Martlet thinks something over, the recent conversation and " +
                "the task go there, and requests (with their hidden thinking) may cost money.";
            consent.IsChecked = saved is not null && saved.Origin == url && keepModel;
        }
        Refresh(keepModel: saved is not null);
        provider.SelectionChanged += (_, _) => { tabEdited = true; Refresh(keepModel: false); };
        baseUrl.TextChanged += (_, _) => { if (!baseUrl.IsKeyboardFocusWithin) return; tabEdited = true; consent.IsChecked = false; };
        model.TextChanged += (_, _) => { if (!model.IsKeyboardFocusWithin) return; tabEdited = true; consent.IsChecked = false; };
        key.PasswordChanged += (_, _) => tabEdited = true;
        return Card(Heading("A cloud provider or server"),
            Note("A strong reasoning model (for example on OpenRouter) thinks while your Thinking model keeps talking, even one on this " +
                "PC: they never wait for each other.", new Thickness(0, 0, 0, 8)),
            new Label { Content = "_Provider", Target = provider, Padding = new Thickness(0, 0, 0, 4) }, provider, baseUrlPanel,
            new Label { Content = "_Model ID", Target = model, Padding = new Thickness(0, 8, 0, 4) }, model,
            new Label { Content = "API _key", Target = key, Padding = new Thickness(0, 8, 0, 4) }, key, keyStatus, consent,
            Row(PageButton("Use for Deep thinking", () => SaveDeepCloudAsync(Selected(), Url(), model.Text.Trim(), key, consent.IsChecked == true,
                saved, thinking).Forget(), primary: true, id: "DeepThinkingSaveCloud")));
    }

    private async Task SaveDeepCloudAsync(CloudProvider provider, string url, string model, PasswordBox keyBox, bool consent,
        DeepThinkingSettings? saved, SetupRoute? thinking)
    {
        if (!consent)
        {
            ActionText.Text = $"Tick the box to confirm {provider.Name} for Deep thinking, then press Use for Deep thinking.";
            return;
        }
        SecretLease? key = null;
        try
        {
            _ = ChatCompletionsSetup.BaseUri(url);
            ChatCompletionsSetup.ModelId(model);
            using (var entered = keyBox.SecurePassword)
                if (entered.Length > 0) key = TakeKey(keyBox);
            var keep = saved?.Origin == url ? saved.CredentialId : null;
            var next = new DeepThinkingSettings
            {
                Place = DeepThinkingPlace.Endpoint, Origin = url, ModelId = model, CredentialId = keep, ChosenAt = DateTimeOffset.Now
            };
            if (key is null && provider.NeedsKey && keep is null && !next.UsesThinkingKey(thinking))
                throw new ContractException(ErrorCode.InvalidContract, $"Paste your {provider.Name} API key first.");
            await SaveDeepThinkingAsync(next, key, $"Deep thinking now uses {provider.Name} ({model})." +
                (key is null ? "" : " Its API key is saved in Windows Credential Manager."));
        }
        catch (Exception error) when (error is ContractException or InvalidOperationException)
        {
            ActionText.Text = error.Message;
        }
        finally { key?.Dispose(); }
    }

    /// <summary>Saves where Deep thinking runs on this PC (deep-thinking.json). A new key is written to Windows Credential Manager
    /// first and removed again if the file can't be saved; the key it replaces is removed after. The next think uses it.</summary>
    private async Task SaveDeepThinkingAsync(DeepThinkingSettings next, SecretLease? key, string done, bool keepPool = true)
    {
        if (store is null || closing) return;
        var profile = homeSettings?.Profile.Id ?? Guid.Empty;
        var vault = new WindowsCredentialStore();
        var old = DeepThinkingSettings.Load(store.DataDirectory);
        // A new place to think keeps the other computers ticked Think here too.
        if (keepPool) next = next.WithPool(old.Pool ?? []);
        CredentialBinding? written = null;
        try
        {
            if (key is not null)
            {
                if (profile == Guid.Empty) throw new InvalidOperationException("Set up Martlet first, then add the key.");
                next = next with { CredentialId = Guid.NewGuid() };
                var binding = next.Binding(profile, next.CredentialId!.Value);
                var lease = key;
                var error = await Task.Run(() => vault.Write(binding, lease), lifetime.Token);
                if (error != CredentialError.None) throw new InvalidOperationException(CredentialMessages.Describe(error));
                written = binding;
            }
            if (!next.Save(store.DataDirectory)) throw new InvalidOperationException("Couldn't save Deep thinking. Check access to Martlet's data folder.");
            written = null;
            if (old.CredentialId is { } oldKey && next.Places.All(p => p.CredentialId != oldKey) && profile != Guid.Empty)
            {
                var oldBinding = old.Binding(profile, oldKey);
                await Task.Run(() => vault.Delete(oldBinding), CancellationToken.None);
            }
            deepPlaceShown = null;
            conversation?.ReloadDeepThinking();
            var plan = DeepThinkingPool.For(next, homeSettings?.Setup?.Routes ?? []).Plan;
            ErrorLog.Info($"Deep thinking: now on {next.DescribeAll()}; {(plan.Available ? "in parallel with the conversation" : "can't run there")}. {plan.Why}");
            var turnedOn = !ThinkLongerSettings.Of(homeSettings?.Generation).On && await SetThinkLongerAsync(true);
            ActionText.Text = done + (turnedOn ? " Deep thinking is on again." : "") +
                (plan.Available ? " It thinks alongside the conversation." : " " + plan.Why);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or JsonException)
        {
            ActionText.Text = error.Message;
        }
        finally
        {
            if (written is { } orphan) vault.Delete(orphan);
            if (!closing && openTab == CompanionTab.DeepThinking) RenderTab();
        }
    }

    /// <summary>Where it thinks › Off, or back on: Thinking longer's on/off, saved with the reply settings (your computers share
    /// it). Shows <paramref name="done"/> once saved.</summary>
    private async Task TurnDeepThinkingAsync(bool on, string done)
    {
        if (closing) return;
        if (await SetThinkLongerAsync(on))
        {
            deepPlaceShown = null;
            ErrorLog.Info($"Deep thinking: turned {(on ? "on" : "off")}.");
            ActionText.Text = done + " Reload an open conversation to use it.";
        }
        if (!closing && openTab == CompanionTab.DeepThinking) RenderTab();
    }

    /// <summary>Saves whether Martlet may think longer into the newest reply settings, trying again shortly while another change
    /// holds them. True once it is saved that way.</summary>
    private async Task<bool> SetThinkLongerAsync(bool on)
    {
        for (var attempt = 0; attempt < 20 && !closing; attempt++)
        {
            if (await SaveRepliesAsync(loaded => GenerationSettings.Normalize((loaded ?? new()) with
                {
                    ThinkLonger = ThinkLongerSettings.Normalize((loaded?.ThinkLonger ?? new()) with { Enabled = on })
                }), _ => { }, on ? "Deep thinking is on." : "Deep thinking is off."))
                return ThinkLongerSettings.Of(homeSettings?.Generation).On == on;
            try { await Task.Delay(TimeSpan.FromMilliseconds(250), lifetime.Token); }
            catch (OperationCanceledException) { return false; }
        }
        return false;
    }
}
