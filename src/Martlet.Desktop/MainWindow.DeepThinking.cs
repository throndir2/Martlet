using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>Companion › Deep thinking: Thinking answers you; Deep thinking works out what Martlet hands it in the background
/// (think_longer) while the conversation carries on. Whether Martlet may think longer, how hard, for how long, how often and
/// when it shares the result (saved with the reply settings, so your computers share them), and where it thinks (this PC's own
/// choice, deep-thinking.json): the Thinking model, another of your computers, Ollama on this PC or a cloud provider. On another
/// machine a think runs alongside the conversation; on the hardware the conversation uses it waits for quiet moments.</summary>
public partial class MainWindow
{
    private enum DeepPlace { Same, Computer, ThisPc, Cloud }

    private DeepPlace? deepPlaceShown;
    private static readonly ThinkEffort[] ThinkEfforts = [ThinkEffort.Medium, ThinkEffort.High];
    private static readonly ThinkDelivery[] ThinkDeliveries = [ThinkDelivery.WhenFree, ThinkDelivery.NextMessage];

    private static readonly IReadOnlyList<CloudProvider> DeepThinkingProviders =
    [
        .. ChatCompletionsEndpointCatalog.NamedEndpoints.Select(e => new CloudProvider(e.Name, e.BaseUrl, true, e.DefaultModelId, true)),
        new("OpenAI", OpenAiChatBaseUrl, true, OpenAiTextGenerationCatalog.DefaultModelId, true),
        CustomCloud
    ];

    private static DeepPlace PlaceOf(DeepThinkingSettings deep) => deep.Place switch
    {
        DeepThinkingPlace.Host => DeepPlace.Computer,
        DeepThinkingPlace.Endpoint when deep.OnThisPc => DeepPlace.ThisPc,
        DeepThinkingPlace.Endpoint => DeepPlace.Cloud,
        _ => DeepPlace.Same
    };

    private void RenderDeepThinkingTab(Panel page)
    {
        var route = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        var deep = store is null ? new DeepThinkingSettings() : DeepThinkingSettings.Load(store.DataDirectory);
        var routes = homeSettings?.Setup?.Routes ?? [];
        var plan = DeepThinkingPlan.For(deep, routes);
        var current = PlaceOf(deep);
        var place = deepPlaceShown ?? current;

        var now = new TextBlock { Text = DeepThinkingNow(deep, route, plan), FontSize = 15, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(now, "DeepThinkingNow");
        var why = Note(plan.Why, new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(why, "DeepThinkingParallel");
        page.Children.Add(Card(Heading("Now"), now, why));

        page.Children.Add(ThinkLongerCard(homeSettings?.Generation?.ThinkLonger, route));

        var where = new StackPanel();
        where.Children.Add(Heading("Where it thinks"));
        where.Children.Add(Note("A think on another machine runs alongside the conversation, so Martlet keeps talking at full speed. " +
            "One on the hardware the conversation uses waits for quiet moments and stops the moment you talk. This choice stays " +
            "on this PC.", new Thickness(0, 0, 0, 10)));
        foreach (var (value, label, detail) in new (DeepPlace, string, string)[]
        {
            (DeepPlace.Same, "Same as Thinking", "Thinking's own model thinks it through. Simple, and it shares Thinking's prompt cache."),
            (DeepPlace.Computer, "Another of your computers", "A paired computer's Ollama thinks while this PC talks: local, private and parallel."),
            (DeepPlace.ThisPc, "Ollama on this PC", "A model of its own on this PC. It shares this PC's graphics card with the conversation."),
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
            DeepPlace.Computer => DeepComputersCard(deep),
            DeepPlace.ThisPc => DeepLocalCard(deep),
            DeepPlace.Cloud => DeepCloudCard(deep, route),
            _ => Card(Heading("Same as Thinking"),
                Note(route is null ? "Set up Thinking first." : $"Thinks with {route.ModelId}, Thinking's model, with Thinking steps on.",
                    new Thickness(0, 0, 0, 0)),
                Row(current == DeepPlace.Same ? null
                    : PageButton("Think with the Thinking model", () => SaveDeepThinkingAsync(new(), null, "Deep thinking now uses the Thinking model.").Forget(),
                        primary: true, id: "DeepThinkingUseSame")))
        });
    }

    /// <summary>The Now card's line: where a think goes and whether Martlet may think longer at all.</summary>
    private string DeepThinkingNow(DeepThinkingSettings deep, SetupRoute? route, DeepThinkingPlan plan)
    {
        var on = ThinkLongerSettings.Of(homeSettings?.Generation).On;
        var where = deep.Separate ? deep.Describe() : route is null ? "the Thinking model (not set up yet)" : $"the Thinking model ({route.ModelId})";
        return (on ? "Thinks on " : "Off. When on, it thinks on ") + where + (plan.Parallel ? ", in parallel with the conversation." : ", in quiet moments.");
    }

    // ---------- Thinking longer (saved with the reply settings) ----------

    /// <summary>Whether Martlet may decide, sparingly, to think a task through in the background (on by default), how hard, for
    /// how long, how often and when it shares the result. It saves a moment after each change, with the reply settings.</summary>
    private Border ThinkLongerCard(ThinkLongerSettings? saved, SetupRoute? route)
    {
        var current = saved ?? new ThinkLongerSettings();
        var on = new CheckBox { Content = "Let Martlet _think longer when it needs to", IsChecked = current.On, Margin = new Thickness(0, 0, 0, 4) };
        AutomationProperties.SetAutomationId(on, "ThinkLongerOn");
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
        var minutes = ThinkLongerSettings.MinuteChoices;
        var time = Choice("ThinkLongerTime", "Time limit", minutes.Select(m =>
            $"{m} minutes{(m == ThinkLongerSettings.DefaultMinutes ? " (default)" : "")}"), minutes.ToList().IndexOf((int)current.TimeLimit.TotalMinutes));
        var hourly = ThinkLongerSettings.PerHourChoices;
        var perHour = Choice("ThinkLongerPerHour", "At most this many an hour", hourly.Select(n =>
            $"{n} an hour{(n == ThinkLongerSettings.DefaultPerHour ? " (default)" : "")}"), hourly.ToList().IndexOf(current.Hourly));
        var when = Choice("ThinkLongerDelivery", "When Martlet shares the result",
            ["As soon as Martlet is free (default)", "When I talk next"], Array.IndexOf(ThinkDeliveries, current.When));
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        AutomationProperties.SetAutomationId(status, "ThinkLongerStatus");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        void Show(ThinkLongerSettings? settings)
        {
            var (text, problem) = ThinkLongerStatus(settings ?? new(), route);
            status.Text = text;
            status.SetResourceReference(TextBlock.ForegroundProperty, problem ? "WarningBrush" : "MutedBrush");
            foreach (var control in new Control[] { effort, time, perHour, when }) control.IsEnabled = settings?.On ?? true;
        }
        Show(saved);
        ThinkLongerSettings Read() => new()
        {
            Enabled = on.IsChecked == true,
            Effort = ThinkEfforts[Math.Max(0, effort.SelectedIndex)],
            Minutes = minutes[Math.Max(0, time.SelectedIndex)],
            PerHour = hourly[Math.Max(0, perHour.SelectedIndex)],
            Delivery = ThinkDeliveries[Math.Max(0, when.SelectedIndex)]
        };
        // There is no Save button: a change saves a moment later, into the newest saved reply settings.
        var autoSave = new AutoSave(() =>
        {
            var next = ThinkLongerSettings.Normalize(Read());
            return SaveRepliesAsync(loaded => GenerationSettings.Normalize((loaded ?? new()) with { ThinkLonger = next }), generation =>
            {
                Show(generation?.ThinkLonger);
                ErrorLog.Info($"Thinking longer: {(next?.On ?? true ? "on" : "off")}, {(next ?? new()).HowHard} effort, " +
                    $"{BackgroundJobs.Duration((next ?? new()).TimeLimit)} limit, {(next ?? new()).Hourly} an hour, shares " +
                    ((next ?? new()).When == ThinkDelivery.WhenFree ? "as soon as Martlet is free." : "when you talk next."));
            }, "Thinking longer saved. Reload an open conversation to use it.");
        });
        tabAutoSave = autoSave;
        changed = () => { tabEdited = true; autoSave.Changed(); };
        on.Checked += (_, _) => { foreach (var control in new Control[] { effort, time, perHour, when }) control.IsEnabled = true; changed(); };
        on.Unchecked += (_, _) => { foreach (var control in new Control[] { effort, time, perHour, when }) control.IsEnabled = false; changed(); };
        return Card(Heading("Thinking longer"),
            Note("Replies answer right away (Thinking steps are off by default). When a task really needs thought, such as writing " +
                "song lyrics, a story or a plan, or tricky math or code, Martlet can say it'll think it over and work on it in the " +
                "background while you keep talking, then bring it up when it's done.", new Thickness(0, 0, 0, 8)),
            on, status,
            TerminalRow("How hard", effort), TerminalRow("Time limit", time), TerminalRow("How often", perHour),
            TerminalRow("Share it", when),
            Note("It thinks with Thinking steps on, whatever replies use; one think runs at a time. Stop (Esc) doesn't end it: its " +
                "Cancel in the talk window, closing the conversation or the time limit do. A paid provider may charge for its thinking.",
                new Thickness(0, 8, 0, 0)));
    }

    /// <summary>Whether Martlet can think longer at all: Thinking hands the task off with a tool, so it needs a route that does
    /// function calling.</summary>
    private (string Text, bool Problem) ThinkLongerStatus(ThinkLongerSettings settings, SetupRoute? route)
    {
        if (!settings.On) return ("Off. Martlet answers everything right away and never thinks in the background.", false);
        if (route is null) return ("On. Set up Thinking so Martlet can use it.", true);
        if (route.RouteType is not (SetupRouteType.OpenAi or SetupRouteType.ChatCompletions))
            return ("On, but your Thinking model can't use tools here (a Martlet host's model), so it can't hand a task off. Use " +
                "OpenAI or a Chat Completions endpoint (such as Ollama on this PC) for Thinking.", true);
        if (mcpTools.IsUnsupported(McpToolService.ModelKey($"{route.RouteType}", route.Origin, route.ModelId)))
            return ($"On, but {route.ModelId} turned down tools, so it can't hand a task off. Choose a Thinking model that can use tools.", true);
        return ($"On. When a task needs it, Martlet says it'll think it over and works on it in the background ({settings.HowHard} " +
            $"effort, up to {BackgroundJobs.Duration(settings.TimeLimit)}, at most {settings.Hourly} an hour) while you keep talking, " +
            (settings.When == ThinkDelivery.WhenFree ? "then brings it up as soon as it's free." : "then brings it up when you talk next."), false);
    }

    // ---------- where it thinks ----------

    /// <summary>Paired computers whose Ollama can think: each with what it offers and Use it.</summary>
    private Border DeepComputersCard(DeepThinkingSettings deep)
    {
        var stack = new List<UIElement> { Heading("Another of your computers") };
        var hosts = NetworkMap.Hosts(Inputs());
        if (hosts.Count == 0)
        {
            var none = Note("No computer is paired yet. Add one on Devices, with the Thinking role (Ollama).", new Thickness(0, 0, 0, 4));
            AutomationProperties.SetAutomationId(none, "DeepThinkingHosts");
            stack.Add(none);
        }
        foreach (var host in hosts)
        {
            var check = hostChecks.GetValueOrDefault(host.HostId);
            var model = check?.Offers?.GetValueOrDefault(HostRoles.Ollama);
            var inUse = deep.Place == DeepThinkingPlace.Host && deep.HostId == host.HostId;
            var detail = inUse ? $"Thinks here ({deep.ModelId})."
                : model is not null ? $"Ollama runs {model}."
                : check?.Reachable == true ? "Ollama isn't installed there. Add the Thinking role on Devices."
                : check?.Reachable == false ? "Not reachable right now." : "Not checked yet.";
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = host.HostId, FontSize = 15, FontWeight = FontWeights.SemiBold });
            var line = Note(detail, new Thickness(0, 2, 0, 0));
            AutomationProperties.SetName(line, $"{host.HostId}: {detail}");
            AutomationProperties.SetAutomationId(line, "DeepThinkingHost-" + host.HostId);
            text.Children.Add(line);
            var use = PageButton(inUse ? "In use" : "Use it", () => UseDeepHostAsync(host).Forget(), primary: !inUse && model is not null,
                id: "DeepThinkingUseHost-" + host.HostId);
            use.IsEnabled = !inUse;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            DockPanel.SetDock(use, Dock.Right);
            row.Children.Add(use);
            row.Children.Add(text);
            stack.Add(row);
        }
        stack.Add(Row(hosts.Count == 0 ? PageButton("Add a computer", () => RunNodeAction(NodeAction.AddComputer), primary: true, id: "DeepThinkingAddComputer")
            : PageButton("Check computers", () => RunNodeAction(NodeAction.CheckHost), id: "DeepThinkingCheckHosts")));
        stack.Add(Note("The conversation so far (what fits in its 16 KiB) and the task go to that computer through its paired, pinned " +
            "connection. While it thinks there, Thinking, the voice and listening keep working here at full speed; if that computer " +
            "also does one of them, a think waits for quiet moments. A computer's model can only think as long as its Martlet allows: " +
            "update it to this version for thinks over a minute.", new Thickness(0, 8, 0, 0)));
        return Card([.. stack]);
    }

    private async Task UseDeepHostAsync(PairedHost host)
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
            var route = check.Routes?.FirstOrDefault(r => r.RouteId == HostJob.Thinking.RouteId);
            if (route is null)
            {
                ActionText.Text = $"{host.HostId} doesn't run Ollama yet. Add the Thinking role there on Devices, then choose it here.";
                RenderTab();
                return;
            }
            var next = new DeepThinkingSettings
            {
                Place = DeepThinkingPlace.Host, ModelId = route.ModelId, HostId = host.HostId, HostOrigin = host.Pairing.Origin,
                HostSpkiFingerprint = host.Pairing.SpkiFingerprint, HostDeviceId = host.Pairing.DeviceId,
                HostCredentialId = HostPairingCredential.ToGuid(host.Pairing.CredentialId), ChosenAt = DateTimeOffset.Now
            };
            await SaveDeepThinkingAsync(next, null, $"Deep thinking now runs on {host.HostId} ({route.ModelId}).");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or InvalidOperationException or ContractException or Martlet.Avatar.Audio2Face.Remote.Audio2FaceHostException)
        {
            ActionText.Text = error.Message;
        }
    }

    /// <summary>Ollama on this PC with a model of its own for thinking.</summary>
    private Border DeepLocalCard(DeepThinkingSettings deep)
    {
        var model = new ComboBox { IsEditable = true, Width = 300, HorizontalAlignment = HorizontalAlignment.Left,
            ItemsSource = (ollamaModels ?? []).Concat(LocalChatModels.Select(m => m.Id)).Distinct(StringComparer.Ordinal).ToArray(),
            Text = deep.OnThisPc ? deep.ModelId ?? "" : homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm) is { } thinking &&
                IsLocalOllama(thinking) ? thinking.ModelId : LocalChatModels[0].Id };
        AutomationProperties.SetName(model, "Model for Deep thinking");
        AutomationProperties.SetAutomationId(model, "DeepThinkingLocalModel");
        var state = Note(ollamaModels is null ? "Check Ollama to see which models are downloaded."
            : ollamaModels.Count == 0 ? "Ollama is running, but no model is downloaded yet."
            : $"Downloaded: {string.Join(", ", ollamaModels)}.", new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(state, "DeepThinkingLocalStatus");
        return Card(Heading("Ollama on this PC"),
            Note("A larger model can think here while a small, fast one answers you. Both share this PC's graphics card, so while " +
                "Thinking or the voice runs on this PC a think waits for quiet moments; with Thinking elsewhere it runs alongside.",
                new Thickness(0, 0, 0, 8)),
            new Label { Content = "_Model", Target = model, Padding = new Thickness(0, 0, 0, 4) }, model, state,
            Row(PageButton("Use Ollama on this PC", () =>
                {
                    var id = model.Text.Trim();
                    try { ChatCompletionsSetup.ModelId(id); }
                    catch (ContractException error) { ActionText.Text = error.Message; return; }
                    SaveDeepThinkingAsync(new() { Place = DeepThinkingPlace.Endpoint, Origin = LocalOllamaBaseUrl, ModelId = id, ChosenAt = DateTimeOffset.Now },
                        null, $"Deep thinking now uses {id} in Ollama on this PC." +
                        (ollamaModels is { } known && !known.Contains(id, StringComparer.Ordinal) ? $" Download {id} on Thinking to use it." : "")).Forget();
                }, primary: true, id: "DeepThinkingUseLocal"),
                PageButton("Check Ollama", () => CheckOllamaAsync().Forget(), id: "DeepThinkingCheckOllama")));
    }

    /// <summary>A cloud provider or any OpenAI-compatible server (HTTPS, or a server on this PC), with its own key or Thinking's.</summary>
    private Border DeepCloudCard(DeepThinkingSettings deep, SetupRoute? thinking)
    {
        var saved = deep.Place == DeepThinkingPlace.Endpoint && !deep.OnThisPc ? deep : null;
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
    private async Task SaveDeepThinkingAsync(DeepThinkingSettings next, SecretLease? key, string done)
    {
        if (store is null || closing) return;
        var profile = homeSettings?.Profile.Id ?? Guid.Empty;
        var vault = new WindowsCredentialStore();
        var old = DeepThinkingSettings.Load(store.DataDirectory);
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
            if (old.CredentialId is { } oldKey && oldKey != next.CredentialId && profile != Guid.Empty)
            {
                var oldBinding = old.Binding(profile, oldKey);
                await Task.Run(() => vault.Delete(oldBinding), CancellationToken.None);
            }
            deepPlaceShown = null;
            var plan = DeepThinkingPlan.For(next, homeSettings?.Setup?.Routes ?? []);
            ErrorLog.Info($"Deep thinking: now on {next.Describe()}; {(plan.Parallel ? "in parallel with the conversation" : "in quiet moments")}.");
            ActionText.Text = done + (plan.Parallel ? " It thinks alongside the conversation." : " It thinks in quiet moments.");
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
}
