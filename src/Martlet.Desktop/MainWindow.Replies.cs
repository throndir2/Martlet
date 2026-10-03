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
            $"{GenerationSettings.MinimumContextTokens}-{GenerationSettings.MaximumContextTokens} tokens, blank = {GenerationSettings.DefaultHostContextTokens}",
            "How much conversation the model can keep in mind. Larger sizes use more memory.", true)
    ];

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
            box.TextChanged += (_, _) => { if (box.IsKeyboardFocusWithin) tabEdited = true; };
            boxes[setting.Setting] = box;

            var label = new Label { Content = setting.Label, Target = box, Padding = new Thickness(0, 8, 8, 0), VerticalAlignment = VerticalAlignment.Top };
            if (use == GenerationSettingUse.Unused) label.Opacity = 0.6;
            var about = new StackPanel { Margin = new Thickness(0, 8, 0, 10) };
            about.Children.Add(new TextBlock { Text = range, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            about.Children.Add(Note(setting.Help, new Thickness(0, 2, 0, 0)));
            if (route is not null)
            {
                var status = new TextBlock { Text = UseText(use, setting.Setting, route, place!), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
                status.SetResourceReference(TextBlock.ForegroundProperty, use switch
                {
                    GenerationSettingUse.Used => "SuccessBrush",
                    GenerationSettingUse.ServerDependent => "WarningBrush",
                    _ => "MutedBrush"
                });
                about.Children.Add(status);
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

        var save = PageButton("Save", () => SaveRepliesFrom(boxes), primary: true, id: "RepliesSave");
        var defaults = PageButton("Use model defaults", () =>
        {
            foreach (var box in boxes.Values) box.Text = "";
            tabEdited = true;
            ActionText.Text = "Reply settings cleared. Save to use the model defaults.";
        }, id: "RepliesDefaults");
        page.Children.Add(Card(Heading("Reply settings"),
            Note("Leave a field blank to use the model default. Reload an open conversation to use saved changes.",
                new Thickness(0, 0, 0, 4)),
            grid,
            Row(save, defaults)));
    }

    private static string UseText(GenerationSettingUse use, GenerationSetting setting, SetupRoute route, string place) => use switch
    {
        GenerationSettingUse.Used => $"Used by {place}.",
        GenerationSettingUse.ServerDependent => $"May not work with {place}. If replies fail, clear this field.",
        _ when setting == GenerationSetting.ContextTokens && IsLocalOllama(route) =>
            $"Not used by {place}. Change Ollama's context setting outside Martlet.",
        _ when setting == GenerationSetting.ContextTokens => $"Not used by {place}. The provider sets the context size.",
        _ when IsLocalOllama(route) => $"Not used by {place}.",
        _ => $"Not supported by {place}."
    };

    /// <summary>A setting's range; Ollama on this PC has no reply length limit unless one is set, and a Chat Completions route
    /// leaves room for hidden reasoning.</summary>
    private static string RangeText(ReplySetting setting, SetupRoute? route) =>
        setting.Setting != GenerationSetting.MaxReplyTokens ? setting.Range
        : IsLocalOllama(route) ? $"{GenerationSettings.MinimumReplyTokens}-{GenerationSettings.MaximumReplyTokens} tokens, blank = no limit"
        : $"{GenerationSettings.MinimumReplyTokens}-{GenerationSettings.MaximumReplyTokens} tokens, blank = " +
            GenerationSupport.DefaultReplyTokens(route?.RouteType);

    internal static string DescribeGeneration(GenerationSettings? settings, SetupRoute? route = null)
    {
        const string brief = "Replies are brief";
        // Ollama on this PC gets no reply token budget unless a max reply length is set.
        var stop = settings?.MaxReplyTokens is null && IsLocalOllama(route) ? "No maximum length is set"
            : $"Maximum length is {GenerationSupport.ReplyTokens(route?.RouteType, settings)} tokens" +
              (GenerationSupport.BudgetIncludesThinking(route?.RouteType) ? ", including any hidden thinking" : "");
        if (settings is null)
            return $"{brief}. {stop}. Other settings use the model default.";
        var parts = new List<string>();
        foreach (var setting in ReplySettings.Skip(1))
            if (setting.Read(settings) is { } value)
                parts.Add($"{char.ToLower(setting.Name[0], CultureInfo.CurrentCulture)}{setting.Name[1..]} {setting.Format(value)}");
        return $"{brief}. {stop}" +
            (parts.Count == 0 ? ". Other settings use the model default." : ". " + string.Join(", ", parts) + ".");
    }

    private void SaveRepliesFrom(IReadOnlyDictionary<GenerationSetting, TextBox> boxes)
    {
        var values = new Dictionary<GenerationSetting, double?>();
        foreach (var setting in ReplySettings)
        {
            var text = boxes[setting.Setting].Text.Trim();
            if (text.Length == 0) { values[setting.Setting] = null; continue; }
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) &&
                !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                setting.Whole && (value != Math.Floor(value) || Math.Abs(value) > int.MaxValue))
            {
                ActionText.Text = $"{setting.Name}: enter {(setting.Whole ? "a whole number" : "a number")} in the shown range, or leave it blank.";
                boxes[setting.Setting].Focus();
                return;
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
            ContextTokens = Whole(GenerationSetting.ContextTokens)
        };
        try { generation.Validate(); }
        catch (ContractException error)
        {
            ActionText.Text = error.Message;
            return;
        }
        SaveRepliesAsync(GenerationSettings.Normalize(generation)).Forget();
    }

    private async Task SaveRepliesAsync(GenerationSettings? generation)
    {
        if (store is null || setupService is null || closing) return;
        if (savingTab || assigningRole || setupOperations.IsRunning)
        {
            ActionText.Text = "Another change is still finishing. Try again in a moment.";
            return;
        }
        savingTab = true;
        var token = lifetime.Token;
        try
        {
            var loaded = await setupService.LoadAsync(token);
            if (loaded.Error is not null) throw new InvalidOperationException(loaded.Error.Summary);
            var updated = SetupSettings.Begin(loaded.Settings) with { Generation = generation };
            updated.Validate();
            var saved = await setupService.SaveAsync(updated, loaded.Revision, token);
            if (!saved.Save.Saved) throw new InvalidOperationException(saved.Summary);
            homeSettings = updated;
            ActionText.Text = "Reply settings saved. Reload an open conversation to use them.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or JsonException)
        {
            ActionText.Text = error.Message;
        }
        finally
        {
            savingTab = false;
            if (!closing)
            {
                tabEdited = false;
                RenderHome();
            }
        }
    }
}
