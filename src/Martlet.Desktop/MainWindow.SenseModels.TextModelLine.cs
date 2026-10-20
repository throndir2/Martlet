using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Conversation;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;
using Martlet.Logging;
using Martlet.Providers;
using Martlet.Providers.Ollama;

namespace Martlet.Desktop;

/// <summary>More of MainWindow.SenseModels.cs, in a file of its own so that each file stays under 20 KB.</summary>
public partial class MainWindow
{
    /// <summary>Companion › Thinking's Now card: the text model writes every reply, and where pictures and recordings go, with links
    /// to the image and audio models.</summary>
    private StackPanel TextModelLine(SetupRoute? thinking)
    {
        var line = Note(TextModelText(SavedSenses(), thinking, SavedModelAbilities(), ThinkingPoolSettings.Load(store?.DataDirectory)),
            new Thickness(0, 6, 0, 0));
        AutomationProperties.SetAutomationId(line, "ThinkingSenses");
        var links = Row(PageButton("Image model", () => OpenCompanion(CompanionTab.Vision), link: true, id: "ThinkingOpenImageModel"),
            PageButton("Audio model", () => OpenCompanion(CompanionTab.Hearing), link: true, id: "ThinkingOpenAudioModel"));
        links.Margin = new Thickness(0, 2, 0, 0);
        return new StackPanel { Children = { line, links } };
    }

    // ---------- Test vision and Test hearing ----------

    /// <summary>The card's test: Test vision asks the model that takes pictures (Thinking on an OpenAI-compatible endpoint, the
    /// image model's endpoint, or through its gateway the image model on a paired computer); Test hearing asks an audio model of its
    /// own (Thinking's is under Hear how you say it).</summary>
    private UIElement[] SenseTestControls(SenseKind kind, DeepThinkingSettings? own, SetupRoute? thinking, SenseRoute route)
    {
        var id = SenseId(kind);
        if (kind == SenseKind.Audio)
            return own switch
            {
                null => [Note("Test hearing under Hear how you say it tests the text model.", new Thickness(0, 0, 0, 0))],
                { Place: DeepThinkingPlace.Endpoint } => ModelTestControls(kind, OwnProbe(own, thinking), id + "Test", id + "TestStatus", ""),
                _ => [HelpTip.Explain($"Test hearing works only with a model on an OpenAI-compatible endpoint. {own.Describe()} is " +
                    "tried as it is, and Martlet remembers a refused recording.", new Thickness(0, 0, 0, 0), "SenseHearingTest",
                    "Test hearing", wiki: "Vision#test-vision-and-test-hearing")]
            };
        if (own is { Place: DeepThinkingPlace.Host, HostOrigin: { } origin, ModelId: { } model })
        {
            var test = TestOf(SenseKind.Image, origin, model);
            var running = modelTestsRunning.Contains(test);
            var button = new Button
            {
                Content = running ? "Testing vision..." : "Test vision", HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 6), IsEnabled = conversation is not null && !running
            };
            AutomationProperties.SetAutomationId(button, id + "Test");
            button.Click += (_, _) => TestHostVisionAsync(own).Forget();
            var line = Note(modelTestResults.GetValueOrDefault(test) ?? (conversation is null
                ? "Test vision can't reach a paired computer's model from this PC now."
                : $"Sends {own.Describe()} one picture of a single word, drawn on this PC (never your screen), through its paired, pinned " +
                    $"connection, and asks which word it shows (it waits while a reply needs {own.HostId})."), new Thickness(0, 0, 0, 6));
            AutomationProperties.SetAutomationId(line, id + "TestStatus");
            return [button, line];
        }
        return ModelTestControls(kind, own is null ? ThinkingProbe(thinking) : OwnProbe(own, thinking), id + "Test", id + "TestStatus",
            thinking is null ? "Set up Thinking first."
            : $"Test vision can't reach {thinking.ModelId} on " +
                $"{(SelfHostSetup.IsGateway(thinking.RouteType ?? SetupRouteType.OpenAi) ? thinking.Gateway?.HostId ?? "a paired computer" : "OpenAI")}, " +
                "so Martlet goes by its name and remembers a refused picture.");
    }

    // ---------- the panels ----------

    /// <summary>The settings of this PC (Ollama on this PC) in a Vision or Hearing list: pick one Ollama has (with what it is known to
    /// do), see whether it fits beside Thinking's on the graphics card, download it, and <paramref name="save"/> it as the member's
    /// model.</summary>
    private StackPanel SenseLocalPanel(SenseKind kind, PoolMember member, SetupRoute? thinking, Action<string> save)
    {
        var id = SenseId(kind);
        var image = kind == SenseKind.Image;
        var installed = !Prerequisites.IsMissing(Prerequisites.Ollama);
        // What Ollama has decides what to pick, so look once without being asked (loopback only).
        if (installed && ollamaModels is null && !ollamaAutoChecked)
        {
            ollamaAutoChecked = true;
            CheckSenseOllamaAsync(quiet: true).Forget();
        }
        var beside = IsLocalOllama(thinking) ? thinking!.ModelId : null;
        var abilities = SavedModelAbilities();
        var saved = member.Setting(PoolSettingKeys.Model);
        var suggested = image ? VisionModelCatalog.LocalRecommendations.Select(m => m.Tag) : LocalChatModels.Where(m => m.Hears).Select(m => m.Id);
        var choices = (ollamaModels ?? []).Concat(suggested).Distinct(StringComparer.Ordinal).ToArray();
        var first = senseLocalPicked.GetValueOrDefault(kind) ?? saved ??
            choices.FirstOrDefault(m => !OllamaSideBySide.Same(m, beside) && LocalTakes(kind, m, abilities) == true) ?? choices.FirstOrDefault() ?? "";
        var model = new ComboBox { IsEditable = true, Width = 360, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = choices, Text = first };
        AutomationProperties.SetName(model, $"The {SenseWord(kind)} model in Ollama on this PC");
        AutomationProperties.SetAutomationId(model, id + "LocalModel");
        var listed = Note(ollamaModels is null
                ? installed ? "Check Ollama to see which models are downloaded."
                    : "Ollama isn't installed on this PC yet; set it up on Companion › Thinking › This PC."
                : ollamaModels.Count == 0 ? "Ollama is running, but no model is downloaded yet."
                : "Downloaded: " + string.Join(", ", ollamaModels.Select(m => $"{m} ({LocalShort(kind, LocalTakes(kind, m, abilities))})")) + ".",
            new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(listed, id + "LocalStatus");
        var known = Note("", new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(known, id + "LocalKnown");
        AutomationProperties.SetLiveSetting(known, AutomationLiveSetting.Polite);
        var fit = Note("", new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(fit, id + "LocalFit");
        AutomationProperties.SetLiveSetting(fit, AutomationLiveSetting.Polite);
        string Picked() => (model.Text ?? "").Trim();
        void ShowFit(string text, bool problem)
        {
            fit.Text = text;
            fit.SetResourceReference(TextBlock.ForegroundProperty, problem ? "WarningBrush" : "MutedBrush");
        }
        void Show(string picked)
        {
            var now = SavedModelAbilities();
            var said = picked.Length == 0 ? "Choose a model."
                : ModelKnown(kind, picked, now.Find(LocalOllamaBaseUrl, picked), LocalTakes(kind, picked, now));
            known.Text = picked.Length > 0 && ollamaModels is { } downloaded && !LocalOllama.Serves(downloaded, picked)
                ? said.TrimEnd('.') + "; it isn't downloaded yet." : said;
            if (picked.Length == 0) ShowFit("", false);
            else if (beside is null) ShowFit($"Thinking doesn't use Ollama on this PC, so {picked} has it to itself.", false);
            else if (OllamaSideBySide.Same(picked, beside))
                ShowFit($"{picked} is Thinking's own model, so the text model takes the {SenseInputs(kind)} itself.", false);
            else if (senseLocalFit is { } last && last.Thinking == beside && last.Model == picked)
                ShowFit((last.Fit.Fits ? "Fits: " : "Doesn't fit: ") + last.Fit.Why, !last.Fit.Fits);
            else
            {
                ShowFit($"Checking whether {picked} fits beside {beside} on the graphics card...", false);
                CheckSenseFitAsync(beside, picked, ShowFit).Forget();
            }
        }
        void Choose(string picked)
        {
            if (picked == senseLocalPicked.GetValueOrDefault(kind)) return;
            tabEdited = true;
            senseLocalPicked[kind] = picked;
            Show(picked);
            AskOllamaAboutAsync(picked, () => Show(Picked())).Forget();
        }
        Show(first);
        if (first.Length > 0) AskOllamaAboutAsync(first, () => Show(Picked())).Forget();
        model.SelectionChanged += (_, _) => { if (model.SelectedItem is string chosen) Choose(chosen); };
        model.LostKeyboardFocus += (_, _) => Choose(Picked());

        var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
        panel.Children.Add(HelpTip.Explain($"A second model in Ollama on this PC describes the {SenseInputs(kind)} for Thinking. Ollama runs each model in " +
            $"its own process, so it works while Thinking answers, but only while both fit on the graphics card: Martlet checks before each " +
            $"job, and a reply never waits for it. {(image ? "Pictures" : "Recordings")} stay on this PC.", new Thickness(0, 0, 0, 8), "SenseModel", "the second model",
            wiki: "Vision#image-models"));
        panel.Children.Add(new Label { Content = "_Model", Target = model, Padding = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(model);
        panel.Children.Add(listed);
        panel.Children.Add(known);
        panel.Children.Add(fit);
        panel.Children.Add(Row(
            PageButton("Use this model", () =>
            {
                try { ChatCompletionsSetup.ModelId(Picked()); }
                catch (ContractException error)
                {
                    ActionText.Text = error.Message;
                    return;
                }
                tabEdited = false;
                save(Picked());
            }, primary: true, id: id + "UseLocal"),
            installed ? PageButton("Download model", () => PullSenseModelAsync(Picked()).Forget(), id: id + "PullModel") : null,
            PageButton("Check Ollama", () =>
            {
                senseLocalFit = null;
                CheckSenseOllamaAsync(quiet: false).Forget();
            }, id: id + "CheckOllama")));
        return panel;
    }

    /// <summary>Whether a model in Ollama on this PC takes <paramref name="kind"/> (<see cref="ModelSenses"/>, as an endpoint on this
    /// PC): what Ollama said (model-abilities.json), else its name; null when neither says.</summary>
    private static bool? LocalTakes(SenseKind kind, string model, ModelAbilities abilities) =>
        ModelSenses.For(SetupRouteType.ChatCompletions, LocalOllamaBaseUrl, model, abilities).Known(kind);

    private static string LocalShort(SenseKind kind, bool? takes) => takes switch
    {
        true => kind == SenseKind.Image ? "sees" : "hears",
        false => kind == SenseKind.Image ? "text only" : "doesn't hear",
        _ => "not known"
    };

    /// <summary>Asks Ollama on this PC what a downloaded model takes (its <c>/api/show</c> capabilities; nothing is loaded) when
    /// Martlet doesn't know yet, keeps what it says, then calls <paramref name="then"/>.</summary>
    private async Task AskOllamaAboutAsync(string model, Action then)
    {
        if (model.Length == 0 || ollamaModels is not { } downloaded || !LocalOllama.Serves(downloaded, model) ||
            SavedModelAbilities().Find(LocalOllamaBaseUrl, model) is not null || !senseAsked.Add(model))
            return;
        try
        {
            using var client = ModelContextProbe.CreateClient(loopback: true);
            var report = await ModelContextProbe.OllamaAsync(client, LocalOllamaOrigin, model, load: false, lifetime.Token);
            if (report.Reached) RecordModelLimit(LocalOllamaBaseUrl, model, report);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception error) when (error is InvalidOperationException or ContractException) { return; }
        if (!closing) then();
    }

    private async Task CheckSenseFitAsync(string thinking, string model, Action<string, bool> show)
    {
        try
        {
            var fit = await LocalDeepThinking.CheckAsync(thinking, model, loadThinking: false, lifetime.Token);
            senseLocalFit = (thinking, model, fit);
            if (!closing) show((fit.Fits ? "Fits: " : "Doesn't fit: ") + fit.Why, !fit.Fits);
        }
        catch (OperationCanceledException) { }
    }

    private async Task CheckSenseOllamaAsync(bool quiet)
    {
        await CheckOllamaAsync(quiet);
        if (!closing && openTab is CompanionTab.Vision or CompanionTab.Hearing && !(quiet && tabEdited)) RenderTab();
    }

    private async Task PullSenseModelAsync(string model)
    {
        await PullOllamaModelAsync(model);
        if (!closing && openTab is CompanionTab.Vision or CompanionTab.Hearing && !tabEdited) RenderTab();
    }
}
