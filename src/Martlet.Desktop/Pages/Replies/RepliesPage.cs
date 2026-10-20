using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Settings;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>Companion › Replies, the UI only: how the Thinking model answers. Now (the route and the settings in one sentence),
/// Thinking longer (a link to Thinking pool) and Reply settings: Thinking steps, Short first sentence, Adult content, Personal
/// content and the generation (sampling) settings, which apply to every reply, screen glance and camera look. Each setting the
/// Thinking route can't use stays visible, marked unused. There is no Save button: a valid change saves a moment after typing
/// stops. What each line says is in <see cref="RepliesPageModel"/>; what the page needs from the app is
/// <see cref="IRepliesPageServices"/>.</summary>
internal sealed class RepliesPage(IRepliesPageServices services)
{
    /// <summary>Draws the page's cards into <paramref name="page"/>.</summary>
    internal void Render(Panel page)
    {
        var model = new RepliesPageModel(services);
        var route = model.Route;
        var saved = model.Saved;
        var place = model.Place;

        var described = Note(RepliesPageModel.DescribeGeneration(saved, route), new Thickness(0, 2, 0, 0));
        AutomationProperties.SetAutomationId(described, "RepliesNow");
        var now = new List<UIElement>
        {
            Heading("Now"),
            new TextBlock { Text = model.RouteText, FontSize = 15, TextWrapping = TextWrapping.Wrap },
            described
        };
        if (route is null)
            now.Add(Row(PageButton("Set up thinking", () => services.OpenCompanion(CompanionTab.Thinking), primary: true, id: "RepliesSetUpThinking")));
        page.Children.Add(Card([.. now]));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var boxes = new Dictionary<GenerationSetting, TextBox>();
        var thinking = Choices(96, RepliesPageModel.ThinkingChoices.Select(c => c.Name), RepliesPageModel.ThinkingIndex(saved?.Reasoning),
            "RepliesThinking", RepliesPageModel.ThinkingRange, RepliesPageModel.ThinkingHelp);
        var shortFirst = Choices(96, RepliesPageModel.ShortFirstChoices.Select(c => c.Name), RepliesPageModel.ShortFirstIndex(saved?.ShortFirstSentence),
            "RepliesShortFirstSentence", RepliesPageModel.ShortFirstRange, RepliesPageModel.ShortFirstHelp);
        var adult = Choices(96, RepliesPageModel.AdultChoices.Select(c => c.Name), RepliesPageModel.AdultIndex(saved?.AdultContent),
            "RepliesAdultContent", RepliesPageModel.AdultRange, RepliesPageModel.AdultHelp);
        var personal = Choices(140, RepliesPageModel.PersonalChoices.Select(c => c.Name), RepliesPageModel.PersonalIndex(saved?.PersonalContent),
            "RepliesPersonalContent", RepliesPageModel.PersonalRange, RepliesPageModel.PersonalHelp);
        // There is no Save button: a valid change saves a moment after typing stops, into the newest saved settings.
        var autoSave = new AutoSave(() => SaveAsync(boxes, thinking, shortFirst, adult, personal, generation =>
        {
            described.Text = RepliesPageModel.DescribeGeneration(generation, route);
            services.RefreshContextStatus();
        }));
        services.UseAutoSave(autoSave);
        page.Children.Add(Card(Heading("Thinking longer"),
            Note("Martlet can think a hard task through in the background while you keep talking (Thinking pool sets where and how).",
                new Thickness(0, 0, 0, 0)),
            Row(PageButton("Open Thinking pool", () => services.OpenCompanion(CompanionTab.DeepThinking), link: true, id: "RepliesOpenDeepThinking"))));
        thinking.SelectionChanged += (_, _) => { services.MarkEdited(); autoSave.Changed(); };
        AddRow(grid, new Label { Content = "T_hinking steps", Target = thinking, Padding = new Thickness(0, 8, 8, 0), VerticalAlignment = VerticalAlignment.Top },
            thinking, RepliesPageModel.ThinkingRange, RepliesPageModel.ThinkingHelp,
            route is null ? null : (RepliesPageModel.ThinkingUseText(route, place!), "RepliesThinkingStatus", model.Use(GenerationSetting.Reasoning)));
        shortFirst.SelectionChanged += (_, _) => { services.MarkEdited(); autoSave.Changed(); };
        AddRow(grid, new Label { Content = "_Short first sentence", Target = shortFirst, Padding = new Thickness(0, 8, 8, 0), VerticalAlignment = VerticalAlignment.Top },
            shortFirst, RepliesPageModel.ShortFirstRange, RepliesPageModel.ShortFirstHelp, null);
        adult.SelectionChanged += (_, _) => { services.MarkEdited(); autoSave.Changed(); };
        AddRow(grid, new Label { Content = "_Adult content", Target = adult, Padding = new Thickness(0, 8, 8, 0), VerticalAlignment = VerticalAlignment.Top },
            adult, RepliesPageModel.AdultRange, RepliesPageModel.AdultHelp, null);
        personal.SelectionChanged += (_, _) => { services.MarkEdited(); autoSave.Changed(); };
        AddRow(grid, new Label { Content = "_Personal content", Target = personal, Padding = new Thickness(0, 8, 8, 0), VerticalAlignment = VerticalAlignment.Top },
            personal, RepliesPageModel.PersonalRange, RepliesPageModel.PersonalHelp, null);
        foreach (var setting in RepliesPageModel.ReplySettings)
        {
            var use = model.Use(setting.Setting);
            var box = new TextBox
            {
                MaxLength = 12, Width = 96, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 6, 0, 0),
                Text = setting.Read(saved) is { } value ? setting.Format(value) : ""
            };
            AutomationProperties.SetAutomationId(box, setting.Id);
            // A blank box uses the default; the range beside it says which default on this route and what to type.
            InputHint.SetText(box, "Default");
            var range = RepliesPageModel.RangeText(setting, route);
            AutomationProperties.SetHelpText(box, range + ". " + setting.Help);
            box.TextChanged += (_, _) => { services.MarkEdited(); autoSave.Changed(); };
            boxes[setting.Setting] = box;

            var label = new Label { Content = setting.Label, Target = box, Padding = new Thickness(0, 8, 8, 0), VerticalAlignment = VerticalAlignment.Top };
            if (use == GenerationSettingUse.Unused) label.Opacity = 0.6;
            var about = new StackPanel { Margin = new Thickness(0, 8, 0, 10) };
            var rangeText = new TextBlock { Text = range, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetAutomationId(rangeText, setting.Id + "Range");
            about.Children.Add(rangeText);
            about.Children.Add(HelpTip.Explain(setting.Help, new Thickness(0, 2, 0, 0), setting.Id, "this setting",
                wiki: "Thinking#reply-settings"));
            if (route is not null)
            {
                var context = setting.Setting == GenerationSetting.ContextTokens;
                var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
                void ShowStatus() => status.Text = context ? services.ContextStatus(route, place!)
                    : RepliesPageModel.UseText(use, setting.Setting, route, place!);
                ShowStatus();
                status.SetResourceReference(TextBlock.ForegroundProperty, Brush(use));
                about.Children.Add(status);
                if (context)
                {
                    AutomationProperties.SetAutomationId(status, "RepliesContextStatus");
                    AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
                    services.ShowContextStatusWith(ShowStatus);
                    if (MainWindow.CanCheckContext(route))
                        about.Children.Add(Row(PageButton("Check model limit", services.CheckContext, link: true, id: "RepliesCheckContext")));
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
            shortFirst.SelectedIndex = 0;
            services.MarkEdited();
            autoSave.SaveNowAsync().Forget();
        }, id: "RepliesDefaults");
        page.Children.Add(Card(Heading("Reply settings"),
            Note("Leave a field blank to use the model default; changes save as you type and apply when you reload an open conversation.",
                new Thickness(0, 0, 0, 4)),
            grid,
            Row(defaults)));
    }

    private static ComboBox Choices(double width, IEnumerable<string> names, int selected, string id, string range, string help)
    {
        var choice = new ComboBox
        {
            Width = width, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 0, 0), ItemsSource = names.ToArray(), SelectedIndex = selected
        };
        AutomationProperties.SetAutomationId(choice, id);
        AutomationProperties.SetHelpText(choice, range + ". " + help);
        return choice;
    }

    private static string Brush(GenerationSettingUse use) => use switch
    {
        GenerationSettingUse.Used => "SuccessBrush",
        GenerationSettingUse.ServerDependent => "WarningBrush",
        _ => "MutedBrush"
    };

    /// <summary>One row of the Replies grid that isn't a number box: its label, control and what it does, with how the current
    /// Thinking route treats it.</summary>
    private static void AddRow(Grid grid, Label label, Control control, string range, string help,
        (string Text, string Id, GenerationSettingUse Use)? status)
    {
        if (status?.Use == GenerationSettingUse.Unused) label.Opacity = 0.6;
        var about = new StackPanel { Margin = new Thickness(0, 8, 0, 10) };
        about.Children.Add(new TextBlock { Text = range, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        about.Children.Add(HelpTip.Explain(help, new Thickness(0, 2, 0, 0), AutomationProperties.GetAutomationId(control), "this setting",
            wiki: "Thinking#reply-settings"));
        if (status is { } shown)
        {
            var line = new TextBlock { Text = shown.Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            line.SetResourceReference(TextBlock.ForegroundProperty, Brush(shown.Use));
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

    /// <summary>The auto-save: reads the fields and, when every one is valid, saves them into the newest saved settings. A field
    /// that isn't a number in range says so (on the status line) and nothing is saved until it is fixed. Returns false to be tried
    /// again shortly while another change holds the settings.</summary>
    private async Task<bool> SaveAsync(IReadOnlyDictionary<GenerationSetting, TextBox> boxes, ComboBox thinking, ComboBox shortFirst,
        ComboBox adult, ComboBox personal, Action<GenerationSettings?> saved)
    {
        var (generation, problem) = RepliesPageModel.Read(boxes.ToDictionary(pair => pair.Key, pair => pair.Value.Text),
            thinking.SelectedIndex, shortFirst.SelectedIndex, adult.SelectedIndex, personal.SelectedIndex);
        if (generation is null)
        {
            services.Say(problem!);
            return true;
        }
        return await services.Save(generation, saved);
    }
}
