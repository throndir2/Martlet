using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>The Companion page's Replies tab: how the Thinking model answers. Generation (sampling) settings apply to every
/// reply, screen glance and camera look. Each setting the current Thinking route can't use stays visible, marked unused.</summary>
public partial class MainWindow
{
    private sealed record ReplySetting(GenerationSetting Setting, string Id, string Label, string Range, string Help, bool Whole)
    {
        /// <summary>The label without its access key, for messages: "Top P".</summary>
        internal string Name => Label.Replace("_", "", StringComparison.Ordinal);

        internal string Format(double value) => value.ToString(Whole ? "0" : "0.###", CultureInfo.CurrentCulture);
        internal double? Read(GenerationSettings? settings) => Setting switch
        {
            GenerationSetting.MaxReplyTokens => settings?.MaxReplyTokens,
            GenerationSetting.Temperature => settings?.Temperature,
            GenerationSetting.TopP => settings?.TopP,
            GenerationSetting.TopK => settings?.TopK,
            GenerationSetting.MinP => settings?.MinP,
            GenerationSetting.RepeatPenalty => settings?.RepeatPenalty,
            GenerationSetting.FrequencyPenalty => settings?.FrequencyPenalty,
            GenerationSetting.PresencePenalty => settings?.PresencePenalty,
            _ => settings?.ContextTokens
        };
    }

    private static readonly IReadOnlyList<ReplySetting> ReplySettings =
    [
        new(GenerationSetting.MaxReplyTokens, "RepliesMaxReplyTokens", "_Max reply length",
            $"{GenerationSettings.MinimumReplyTokens}-{GenerationSettings.MaximumReplyTokens} tokens, blank = {GenerationSettings.DefaultMaxReplyTokens}",
            "Stops unusually long replies. If a reply hits the limit, it may end early.", true),
        new(GenerationSetting.Temperature, "RepliesTemperature", "_Temperature", "0-2",
            "Higher is more varied. Lower is more predictable.", false),
        new(GenerationSetting.TopP, "RepliesTopP", "Top _P", "above 0, at most 1",
            "Controls how many likely words the model considers. 1 turns it off.", false),
        new(GenerationSetting.TopK, "RepliesTopK", "Top _K", $"1-{GenerationSettings.MaximumTopK}",
            "Limits the model to this many likely words.", true),
        new(GenerationSetting.MinP, "RepliesMinP", "M_in P", "0-1",
            "Drops very unlikely words.", false),
        new(GenerationSetting.RepeatPenalty, "RepliesRepeatPenalty", "_Repeat penalty", "0-2",
            "Above 1 discourages repeated words and phrases.", false),
        new(GenerationSetting.FrequencyPenalty, "RepliesFrequencyPenalty", "_Frequency penalty", "-2 to 2",
            "Positive values reduce words that already appear often.", false),
        new(GenerationSetting.PresencePenalty, "RepliesPresencePenalty", "P_resence penalty", "-2 to 2",
            "Positive values nudge replies toward new topics.", false),
        new(GenerationSetting.ContextTokens, "RepliesContextTokens", "_Context size",
            $"{GenerationSettings.MinimumContextTokens}-{GenerationSettings.MaximumContextTokens} tokens, blank = {GenerationSettings.DefaultContextTokens}",
            "How much the model keeps in mind: persona, lore, memory, the conversation so far and the reply. Older exchanges are " +
            "left out once a conversation outgrows it. Larger sizes send more with each reply, which costs more on paid providers.", true)
    ];

    /// <summary>Companion › Replies › Thinking steps: each choice and what it saves as <see cref="GenerationSettings.Reasoning"/>.
    /// Off is the default: it saves nothing (null), and a saved false from earlier versions also reads as Off.</summary>
    private static readonly IReadOnlyList<(string Name, bool? Value)> ThinkingChoices = [("Off", null), ("On", true)];
    private const string ThinkingRange = "Off (default) or On";
    private const string ThinkingHelp = "Reasoning models think step by step before they answer. Off skips that, so replies start " +
        "sooner. On asks for it, which can help with hard questions but waits seconds before the first word. A model that always " +
        "thinks refuses Off; Martlet then uses the model's own default.";

    private static int ThinkingIndex(bool? reasoning) => GenerationSettings.ThinkingSteps(new() { Reasoning = reasoning }) ? 1 : 0;

    private void RenderRepliesTab(Panel page)
    {
        var route = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        var saved = homeSettings?.Generation;
        var place = route is null ? null : PlaceName(route);

        var described = Note(DescribeGeneration(saved, route), new Thickness(0, 2, 0, 0));
        AutomationProperties.SetAutomationId(described, "RepliesNow");
        var now = new List<UIElement>
        {
            Heading("Now"),
            new TextBlock
            {
                Text = route is null ? "Thinking isn't set up yet." : $"Using {route.ModelId} from {place}.",
                FontSize = 15, TextWrapping = TextWrapping.Wrap
            },
            described
        };
        if (route is null) now.Add(Row(PageButton("Set up thinking", () => OpenCompanion(CompanionTab.Thinking), primary: true, id: "RepliesSetUpThinking")));
        page.Children.Add(Card([.. now]));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var boxes = new Dictionary<GenerationSetting, TextBox>();
        var thinking = new ComboBox
        {
            Width = 96, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 0, 0), ItemsSource = ThinkingChoices.Select(c => c.Name).ToArray(),
            SelectedIndex = ThinkingIndex(saved?.Reasoning)
        };
        AutomationProperties.SetAutomationId(thinking, "RepliesThinking");
        AutomationProperties.SetHelpText(thinking, ThinkingRange + ". " + ThinkingHelp);
        // Thinking longer's controls save with the reply settings (one auto-save), so they are read when it saves.
        Func<ThinkLongerSettings?> readThinkLonger = () => saved?.ThinkLonger;
        // There is no Save button: a valid change saves a moment after typing stops, into the newest saved settings.
        var autoSave = new AutoSave(() => SaveRepliesFromAsync(boxes, thinking, readThinkLonger(), generation =>
        {
            described.Text = DescribeGeneration(generation, route);
            showContextStatus?.Invoke();
            showThinkLongerStatus?.Invoke(generation?.ThinkLonger);
        }));
        tabAutoSave = autoSave;
        page.Children.Add(ThinkLongerCard(saved?.ThinkLonger, route, () => { tabEdited = true; autoSave.Changed(); }, out readThinkLonger));
        thinking.SelectionChanged += (_, _) => { tabEdited = true; autoSave.Changed(); };
        AddRepliesRow(grid, new Label { Content = "T_hinking steps", Target = thinking, Padding = new Thickness(0, 8, 8, 0), VerticalAlignment = VerticalAlignment.Top },
            thinking, ThinkingRange, ThinkingHelp, route is null ? null : (ThinkingUseText(route, place!), "RepliesThinkingStatus",
                GenerationSupport.Use(route.RouteType, route.Origin, GenerationSetting.Reasoning)));
        foreach (var setting in ReplySettings)
        {
            var use = route is null ? GenerationSettingUse.Used : GenerationSupport.Use(route.RouteType, route.Origin, setting.Setting);
            var box = new TextBox
            {
                MaxLength = 12, Width = 96, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 6, 0, 0),
                Text = setting.Read(saved) is { } value ? setting.Format(value) : ""
            };
            AutomationProperties.SetAutomationId(box, setting.Id);
            var range = RangeText(setting, route);
            AutomationProperties.SetHelpText(box, range + ". " + setting.Help);
            box.TextChanged += (_, _) => { tabEdited = true; autoSave.Changed(); };
            boxes[setting.Setting] = box;

            var label = new Label { Content = setting.Label, Target = box, Padding = new Thickness(0, 8, 8, 0), VerticalAlignment = VerticalAlignment.Top };
            if (use == GenerationSettingUse.Unused) label.Opacity = 0.6;
            var about = new StackPanel { Margin = new Thickness(0, 8, 0, 10) };
            about.Children.Add(new TextBlock { Text = range, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            about.Children.Add(Note(setting.Help, new Thickness(0, 2, 0, 0)));
            if (route is not null)
            {
                var context = setting.Setting == GenerationSetting.ContextTokens;
                var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
                void ShowStatus() => status.Text = context ? ContextStatus(route, homeSettings?.Generation, SavedModelLimits(), place!)
                    : UseText(use, setting.Setting, route, place!);
                ShowStatus();
                status.SetResourceReference(TextBlock.ForegroundProperty, use switch
                {
                    GenerationSettingUse.Used => "SuccessBrush",
                    GenerationSettingUse.ServerDependent => "WarningBrush",
                    _ => "MutedBrush"
                });
                about.Children.Add(status);
                if (context)
                {
                    AutomationProperties.SetAutomationId(status, "RepliesContextStatus");
                    AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
                    showContextStatus = ShowStatus;
                    if (CanCheckContext(route))
                        about.Children.Add(Row(PageButton("Check model limit", () => CheckContextFromRepliesAsync().Forget(),
                            link: true, id: "RepliesCheckContext")));
                }
            }

            var row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(label, row);
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);
            Grid.SetRow(about, row);
            Grid.SetColumn(about, 2);
            grid.Children.Add(label);
            grid.Children.Add(box);
            grid.Children.Add(about);
        }

        var defaults = PageButton("Use model defaults", () =>
        {
            foreach (var box in boxes.Values) box.Text = "";
            thinking.SelectedIndex = 0;
            tabEdited = true;
            autoSave.SaveNowAsync().Forget();
        }, id: "RepliesDefaults");
        page.Children.Add(Card(Heading("Reply settings"),
            Note("Leave a field blank to use the model default. Changes save as you type; reload an open conversation to use them.",
                new Thickness(0, 0, 0, 4)),
            grid,
            Row(defaults)));
    }

    // ---------- Thinking longer ----------

    private Action<ThinkLongerSettings?>? showThinkLongerStatus;
    private static readonly ThinkEffort[] ThinkEfforts = [ThinkEffort.Medium, ThinkEffort.High];
    private static readonly ThinkDelivery[] ThinkDeliveries = [ThinkDelivery.WhenFree, ThinkDelivery.NextMessage];

    /// <summary>Companion › Replies › Thinking longer, beside Thinking steps: whether Martlet may decide, sparingly, to think a
    /// task through in the background (on by default), how hard, for how long, how often and when it shares the result. It
    /// saves with the reply settings; <paramref name="read"/> gives what the controls show.</summary>
    private Border ThinkLongerCard(ThinkLongerSettings? saved, SetupRoute? route, Action changed, out Func<ThinkLongerSettings?> read)
    {
        var current = saved ?? new ThinkLongerSettings();
        var on = new CheckBox { Content = "Let Martlet _think longer when it needs to", IsChecked = current.On, Margin = new Thickness(0, 0, 0, 4) };
        AutomationProperties.SetAutomationId(on, "RepliesThinkLonger");
        ComboBox Choice(string id, string name, IEnumerable<string> items, int selected)
        {
            var box = new ComboBox { Width = 260, ItemsSource = items.ToArray(), SelectedIndex = selected, HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetAutomationId(box, id);
            AutomationProperties.SetName(box, name);
            box.SelectionChanged += (_, _) => changed();
            return box;
        }
        var effort = Choice("RepliesThinkLongerEffort", "How hard it thinks", ["Medium (default)", "High"],
            Array.IndexOf(ThinkEfforts, current.HowHard));
        var minutes = ThinkLongerSettings.MinuteChoices;
        var time = Choice("RepliesThinkLongerTime", "Time limit", minutes.Select(m =>
            $"{m} minutes{(m == ThinkLongerSettings.DefaultMinutes ? " (default)" : "")}"), minutes.ToList().IndexOf((int)current.TimeLimit.TotalMinutes));
        var hourly = ThinkLongerSettings.PerHourChoices;
        var perHour = Choice("RepliesThinkLongerPerHour", "At most this many an hour", hourly.Select(n =>
            $"{n} an hour{(n == ThinkLongerSettings.DefaultPerHour ? " (default)" : "")}"), hourly.ToList().IndexOf(current.Hourly));
        var when = Choice("RepliesThinkLongerDelivery", "When Martlet shares the result",
            ["As soon as Martlet is free (default)", "When I talk next"], Array.IndexOf(ThinkDeliveries, current.When));
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        AutomationProperties.SetAutomationId(status, "RepliesThinkLongerStatus");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        void Show(ThinkLongerSettings? settings)
        {
            var (text, problem) = ThinkLongerStatus(settings ?? new(), route);
            status.Text = text;
            status.SetResourceReference(TextBlock.ForegroundProperty, problem ? "WarningBrush" : "MutedBrush");
            foreach (var control in new Control[] { effort, time, perHour, when }) control.IsEnabled = settings?.On ?? true;
        }
        Show(saved);
        showThinkLongerStatus = Show;
        on.Checked += (_, _) => { foreach (var control in new Control[] { effort, time, perHour, when }) control.IsEnabled = true; changed(); };
        on.Unchecked += (_, _) => { foreach (var control in new Control[] { effort, time, perHour, when }) control.IsEnabled = false; changed(); };
        read = () => new ThinkLongerSettings
        {
            Enabled = on.IsChecked == true,
            Effort = ThinkEfforts[Math.Max(0, effort.SelectedIndex)],
            Minutes = minutes[Math.Max(0, time.SelectedIndex)],
            PerHour = hourly[Math.Max(0, perHour.SelectedIndex)],
            Delivery = ThinkDeliveries[Math.Max(0, when.SelectedIndex)]
        };
        return Card(Heading("Thinking longer"),
            Note("Replies answer right away (Thinking steps are off by default). When a task really needs thought, such as writing " +
                "song lyrics, a story or a plan, or tricky math or code, Martlet can say it'll think it over and work on it in the " +
                "background while you keep talking, then bring it up when it's done.", new Thickness(0, 0, 0, 8)),
            on, status,
            TerminalRow("How hard", effort), TerminalRow("Time limit", time), TerminalRow("How often", perHour),
            TerminalRow("Share it", when),
            Note("It thinks with Thinking steps on, on your Thinking model, whatever replies use; one think runs at a time. Stop " +
                "(Esc) doesn't end it: its Cancel in the talk window, closing the conversation or the time limit do. A paid provider " +
                "may charge for its thinking.", new Thickness(0, 8, 0, 0)));
    }

    /// <summary>What Thinking longer does on the current Thinking route, and whether something keeps it from working.</summary>
    private (string Text, bool Problem) ThinkLongerStatus(ThinkLongerSettings settings, SetupRoute? route)
    {
        if (!settings.On) return ("Off. Martlet answers everything right away and never thinks in the background.", false);
        if (route is null) return ("On. Set up Thinking so Martlet can use it.", true);
        if (route.RouteType is not (SetupRouteType.OpenAi or SetupRouteType.ChatCompletions))
            return ("On, but your Thinking model can't use tools here (a Martlet host's model), so Martlet can't think in the " +
                "background. Use OpenAI or a Chat Completions endpoint (such as Ollama on this PC) in Companion › Thinking.", true);
        if (mcpTools.IsUnsupported(McpToolService.ModelKey($"{route.RouteType}", route.Origin, route.ModelId)))
            return ($"On, but {route.ModelId} turned down tools, so it can't think in the background. Choose a model that can use tools.", true);
        var local = IsLocalOllama(route) || route.RouteType == SetupRouteType.ChatCompletions &&
            Uri.TryCreate(route.Origin, UriKind.Absolute, out var origin) && origin.IsLoopback;
        var reasons = GenerationSupport.Use(route.RouteType, route.Origin, GenerationSetting.Reasoning) != GenerationSettingUse.Unused;
        return ($"On. When a task needs it, Martlet says it'll think it over and works on it in the background " +
            (reasons ? $"({settings.HowHard} effort" : "(this model has no thinking steps, so it just writes it out") +
            $", up to {Martlet.Conversation.BackgroundJobs.Duration(settings.TimeLimit)}, at most {settings.Hourly} an hour) while you keep talking, then " +
            (settings.When == ThinkDelivery.WhenFree ? "brings it up as soon as it's free." : "brings it up when you talk next.") +
            (local ? " Its model is on this PC, so it thinks only while you aren't talking and pauses for every reply." : ""), false);
    }

    /// <summary>One row of the Replies grid that isn't a number box: its label, control and what it does, with how the current
    /// Thinking route treats it.</summary>
    private static void AddRepliesRow(Grid grid, Label label, Control control, string range, string help,
        (string Text, string Id, GenerationSettingUse Use)? status)
    {
        if (status?.Use == GenerationSettingUse.Unused) label.Opacity = 0.6;
        var about = new StackPanel { Margin = new Thickness(0, 8, 0, 10) };
        about.Children.Add(new TextBlock { Text = range, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        about.Children.Add(Note(help, new Thickness(0, 2, 0, 0)));
        if (status is { } shown)
        {
            var line = new TextBlock { Text = shown.Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            line.SetResourceReference(TextBlock.ForegroundProperty, shown.Use switch
            {
                GenerationSettingUse.Used => "SuccessBrush",
                GenerationSettingUse.ServerDependent => "WarningBrush",
                _ => "MutedBrush"
            });
            AutomationProperties.SetAutomationId(line, shown.Id);
            about.Children.Add(line);
        }
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(label, row);
        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        Grid.SetRow(about, row);
        Grid.SetColumn(about, 2);
        grid.Children.Add(label);
        grid.Children.Add(control);
        grid.Children.Add(about);
    }

    /// <summary>How the Thinking route takes Thinking steps; see <see cref="GenerationSupport.Reasoning"/>.</summary>
    private static string ThinkingUseText(SetupRoute route, string place) =>
        GenerationSupport.Use(route.RouteType, route.Origin, GenerationSetting.Reasoning) switch
        {
            GenerationSettingUse.Used when route.RouteType == SetupRouteType.GatewayOllama =>
                $"Used by {place}. If replies fail after changing it, update the host to this Martlet version.",
            GenerationSettingUse.Used => $"Used by {place}.",
            GenerationSettingUse.ServerDependent =>
                $"Depends on the model at {place}: a model that can't turn thinking off or on may ignore it or refuse it; Martlet " +
                "then uses the model's own default.",
            _ => $"Not used: {place}'s models here answer without thinking first."
        };

    private static string UseText(GenerationSettingUse use, GenerationSetting setting, SetupRoute route, string place) => use switch
    {
        GenerationSettingUse.Used => $"Used by {place}.",
        GenerationSettingUse.ServerDependent => $"May not work with {place}. If replies fail, clear this field.",
        _ when IsLocalOllama(route) => $"Not used by {place}.",
        _ => $"Not supported by {place}."
    };

    /// <summary>A setting's range; Ollama on this PC has no reply length limit unless one is set, a Chat Completions route
    /// leaves room for hidden reasoning, and the context size depends on where the model runs.</summary>
    private static string RangeText(ReplySetting setting, SetupRoute? route) => setting.Setting switch
    {
        GenerationSetting.MaxReplyTokens when IsLocalOllama(route) =>
            $"{GenerationSettings.MinimumReplyTokens}-{GenerationSettings.MaximumReplyTokens} tokens, blank = no limit",
        GenerationSetting.MaxReplyTokens =>
            $"{GenerationSettings.MinimumReplyTokens}-{GenerationSettings.MaximumReplyTokens} tokens, blank = " +
            GenerationSupport.DefaultReplyTokens(route?.RouteType),
        GenerationSetting.ContextTokens when route?.RouteType == SetupRouteType.GatewayOllama =>
            $"{GenerationSettings.MinimumContextTokens}-{GenerationSettings.MaximumHostContextTokens} tokens on a host, blank = {GenerationSettings.DefaultHostContextTokens}",
        GenerationSetting.ContextTokens when IsLocalOllama(route) =>
            $"{GenerationSettings.MinimumContextTokens}-{GenerationSettings.MaximumContextTokens} tokens, blank = Ollama's context length",
        GenerationSetting.ContextTokens =>
            $"{GenerationSettings.MinimumContextTokens}-{GenerationSettings.MaximumContextTokens} tokens, blank = {GenerationSettings.DefaultContextTokens} or the model's limit",
        _ => setting.Range
    };

    internal static string DescribeGeneration(GenerationSettings? settings, SetupRoute? route = null)
    {
        const string brief = "Replies are brief";
        // Ollama on this PC gets no reply token budget unless a max reply length is set.
        var stop = settings?.MaxReplyTokens is null && IsLocalOllama(route) ? "No maximum length is set"
            : $"Maximum length is {GenerationSupport.ReplyTokens(route?.RouteType, settings)} tokens" +
              (GenerationSupport.BudgetIncludesThinking(route?.RouteType) ? ", including any hidden thinking" : "");
        // Thinking steps is Off unless On is chosen; routes whose models don't reason don't mention it.
        var thinking = route is not null && GenerationSupport.Use(route.RouteType, route.Origin, GenerationSetting.Reasoning) ==
            GenerationSettingUse.Unused ? "" : GenerationSettings.ThinkingSteps(settings) ? "Thinking steps are on. " : "Thinking steps are off. ";
        if (settings is null)
            return $"{brief}. {stop}. {thinking}Other settings use the model default.";
        var parts = new List<string>();
        foreach (var setting in ReplySettings.Skip(1))
            if (setting.Read(settings) is { } value)
                parts.Add($"{char.ToLower(setting.Name[0], CultureInfo.CurrentCulture)}{setting.Name[1..]} {setting.Format(value)}");
        return $"{brief}. {stop}. {thinking}" +
            (parts.Count == 0 ? "Other settings use the model default." : string.Join(", ", parts) + ".");
    }

    /// <summary>The Companion page's Replies tab auto-save: reads the fields and, when every one is valid, writes them into the
    /// newest saved settings. A field that isn't a number in range says so (on the status line) and nothing is saved until it is
    /// fixed. Returns false to be tried again shortly while another change holds the settings.</summary>
    private async Task<bool> SaveRepliesFromAsync(IReadOnlyDictionary<GenerationSetting, TextBox> boxes, ComboBox thinking,
        ThinkLongerSettings? thinkLonger, Action<GenerationSettings?> saved)
    {
        var values = new Dictionary<GenerationSetting, double?>();
        foreach (var setting in ReplySettings)
        {
            var text = boxes[setting.Setting].Text.Trim();
            if (text.Length == 0) { values[setting.Setting] = null; continue; }
            // Whole numbers may be typed with thousands separators ("100,000"), as the ranges show them.
            var styles = setting.Whole ? NumberStyles.Float | NumberStyles.AllowThousands : NumberStyles.Float;
            if (!double.TryParse(text, styles, CultureInfo.CurrentCulture, out var value) &&
                !double.TryParse(text, styles, CultureInfo.InvariantCulture, out value) ||
                setting.Whole && (value != Math.Floor(value) || Math.Abs(value) > int.MaxValue))
            {
                ActionText.Text = $"Reply settings not saved yet: {setting.Name}: enter {(setting.Whole ? "a whole number" : "a number")} in the shown range, or leave it blank.";
                return true;
            }
            values[setting.Setting] = value;
        }
        int? Whole(GenerationSetting setting) => values[setting] is { } value ? (int)value : null;
        var generation = new GenerationSettings
        {
            MaxReplyTokens = Whole(GenerationSetting.MaxReplyTokens),
            Temperature = values[GenerationSetting.Temperature],
            TopP = values[GenerationSetting.TopP],
            TopK = Whole(GenerationSetting.TopK),
            MinP = values[GenerationSetting.MinP],
            RepeatPenalty = values[GenerationSetting.RepeatPenalty],
            FrequencyPenalty = values[GenerationSetting.FrequencyPenalty],
            PresencePenalty = values[GenerationSetting.PresencePenalty],
            ContextTokens = Whole(GenerationSetting.ContextTokens),
            Reasoning = ThinkingChoices[Math.Max(0, thinking.SelectedIndex)].Value,
            ThinkLonger = ThinkLongerSettings.Normalize(thinkLonger)
        };
        try { generation.Validate(); }
        catch (ContractException error)
        {
            ActionText.Text = "Reply settings not saved yet: " + error.Message;
            return true;
        }
        return await SaveRepliesAsync(GenerationSettings.Normalize(generation), saved);
    }

    private async Task<bool> SaveRepliesAsync(GenerationSettings? generation, Action<GenerationSettings?> saved)
    {
        if (store is null || setupService is null || closing) return true;
        if (savingTab || assigningRole || setupOperations.IsRunning) return false;
        savingTab = true;
        var token = lifetime.Token;
        try
        {
            var loaded = await setupService.LoadAsync(token);
            if (loaded.Error is not null) throw new InvalidOperationException(loaded.Error.Summary);
            if (Equals(loaded.Settings?.Generation, generation)) return true;
            var updated = SetupSettings.Begin(loaded.Settings) with { Generation = generation };
            updated.Validate();
            var result = await setupService.SaveAsync(updated, loaded.Revision, token);
            if (!result.Save.Saved)
            {
                if (result.Save.Error?.Code == ErrorCode.SettingsConflict) return false;
                throw new InvalidOperationException(result.Summary);
            }
            homeSettings = updated;
            saved(generation);
            ActionText.Text = "Reply settings saved. Reload an open conversation to use them.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or JsonException)
        {
            ActionText.Text = "Reply settings not saved: " + error.Message;
        }
        finally
        {
            savingTab = false;
            // The page stays as typed (tabEdited); only Home and the other summaries refresh.
            if (!closing) RenderHome();
        }
        return true;
    }
}
