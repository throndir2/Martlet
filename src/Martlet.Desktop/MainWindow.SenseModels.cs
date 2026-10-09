using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;
using Martlet.Logging;
using Martlet.Providers;
using Martlet.Providers.Ollama;

namespace Martlet.Desktop;

/// <summary>Companion › Vision's and Companion › Hearing's main choice (docs/SENSE_MODELS.md): which model takes pictures and
/// which takes recordings, or Off. Thinking, the text model, always writes the reply, and by default it also takes both itself
/// (an omni model). A model of its own (Ollama on this PC, a cloud provider or server, or for pictures one of your computers)
/// instead puts what it sees or hears into words for Thinking, and each kind may use the same model as the other. The choice is
/// this PC's own (sense-models.json); a model's own key is in Windows Credential Manager. Choosing a model asks its server what it
/// takes, and Test vision and Test hearing find out (MainWindow.ModelAbilities.cs). Companion › Thinking says where pictures and
/// recordings go. Each page shows the lines of <see cref="SenseNowLines"/> in its Now card and <see cref="SenseChoiceCard"/> as
/// its main choice: an option picker (<c>Picker-Vision-&lt;choice&gt;</c>, <c>Picker-Hearing-&lt;choice&gt;</c>) whose details show
/// the chosen option's panel.</summary>
public partial class MainWindow
{
    /// <summary>A main choice's options after Off ("Picker-Vision-ThisPc"): choosing one only shows its panel, whose own button saves.</summary>
    internal enum SenseChoice { Thinking, OtherSense, ThisPc, Cloud, Computer }

    /// <summary>The model picked under Ollama on this PC on each card.</summary>
    private readonly Dictionary<SenseKind, string> senseLocalPicked = [];
    /// <summary>The last check of whether a model fits beside Thinking's in Ollama on this PC.</summary>
    private (string Thinking, string Model, SideBySideFit Fit)? senseLocalFit;
    /// <summary>The models in Ollama on this PC that Martlet asked about this session (once each).</summary>
    private readonly HashSet<string> senseAsked = new(StringComparer.Ordinal);

    private static string SenseId(SenseKind kind) => kind == SenseKind.Image ? "ImageModel" : "AudioModel";
    internal static string SenseWord(SenseKind kind) => kind == SenseKind.Image ? "image" : "audio";
    private static string SenseInputs(SenseKind kind) => kind == SenseKind.Image ? "pictures" : "recordings";

    /// <summary>The Now card's lines about the model that takes <paramref name="kind"/>: what takes it now, where it goes and
    /// why, what the model is known to do, what is sent where, and Test vision or Test hearing.</summary>
    private List<UIElement> SenseNowLines(SenseKind kind)
    {
        var id = SenseId(kind);
        var (senses, state) = SenseModels.Read(store?.DataDirectory);
        var thinking = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        var abilities = SavedModelAbilities();
        var route = SenseRouting.For(kind, senses, thinking, abilities);
        var place = senses.Place(kind);
        var own = place is not null && !SenseRouting.IsThinking(place, thinking) ? place : null;
        var lines = new List<UIElement>();
        void Line(string lineId, string text, bool problem = false)
        {
            var line = problem ? Warning(text) : Note(text, new Thickness(0, 0, 0, 6));
            AutomationProperties.SetAutomationId(line, lineId);
            lines.Add(line);
        }
        Line(id + "Now", SenseNow(kind, senses, thinking));
        Line(id + "Route", route.Why, problem: route.Path == SensePath.None && thinking is not null);
        if (SenseKnown(kind, senses, thinking, abilities) is { } known) Line(id + "Known", known);
        // Only a model that takes this kind gets anything sent to it.
        if (own is not null && route.Described) Line(id + "Sent", SenseSent(kind, own));
        if (state == "unreadable")
            lines.Add(Warning("Martlet can't read sense-models.json (a newer Martlet's, or a damaged file), so the text model takes " +
                "pictures and recordings. Choosing a model here replaces the file."));
        lines.AddRange(SenseTestControls(kind, own, thinking, route));
        return lines;
    }

    /// <summary>A sense page's main choice: Off (<paramref name="turnOff"/>, shown while the part is <paramref name="on"/>), then
    /// what takes <paramref name="kind"/> (<see cref="OptionalExtras.SenseChoices"/>), each option's details showing its panel. The
    /// saved option, while the part is off, offers <paramref name="turnOn"/>; <paramref name="offState"/> says why it is off.</summary>
    private Border SenseChoiceCard(SenseKind kind, bool on, Func<Button> turnOff, Func<Button?> turnOn, string offState)
    {
        var image = kind == SenseKind.Image;
        var senses = SenseModels.Load(store?.DataDirectory);
        var thinking = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        var abilities = SavedModelAbilities();
        var saved = ChoiceOf(senses.For(kind));
        var options = OptionalExtras.SenseChoices(kind, senses, thinking, abilities, on, thinking is null ? null : PlaceName(thinking), offState)
            .Select(Wire).ToList();
        PickerOption Wire(PickerOption option)
        {
            if (option.IsOff) return option with { Action = on ? turnOff : null };
            var choice = Enum.Parse<SenseChoice>(option.Key);
            return option with
            {
                Details = () => [choice switch
                {
                    SenseChoice.Thinking or SenseChoice.OtherSense => SameModelPanel(kind, choice, choice == saved, senses, thinking),
                    SenseChoice.ThisPc => SenseLocalPanel(kind, senses, thinking),
                    SenseChoice.Cloud => SenseCloudPanel(kind, senses, thinking),
                    _ => SenseComputersPanel(senses, abilities)
                }],
                Action = choice == saved && !on ? turnOn : null
            };
        }
        return OptionPicker(OptionalExtras.SensePickerId(kind), image ? "How Martlet sees" : "How Martlet hears your tone",
            image
                ? "Off, or the model that looks at the pictures of your screen and camera: the text model itself (an omni model such as " +
                  "Gemma 4 E2B sees them), or an image model of its own that describes them in words for Thinking. A reply never waits " +
                  "for the image model."
                : "Off, or the model that hears the recordings of your voice and of what this PC plays: the text model itself (an omni " +
                  "model such as Gemma 4 E2B hears them), or an audio model of its own that describes them in words for Thinking. " +
                  "Speech-to-text on Listening still writes down what you say, and a reply never waits for the audio model.",
            options);
    }

    /// <summary>The saved choice as a card's option.</summary>
    internal static SenseChoice ChoiceOf(SenseModel model) => model switch
    {
        { Source: SenseSource.OtherSense } => SenseChoice.OtherSense,
        { Source: SenseSource.Own, Own.Place: DeepThinkingPlace.Host } => SenseChoice.Computer,
        { Source: SenseSource.Own, Own: { } own } when ContextBudget.IsLocalOllama(SetupRouteType.ChatCompletions, own.Origin) => SenseChoice.ThisPc,
        { Source: SenseSource.Own } => SenseChoice.Cloud,
        _ => SenseChoice.Thinking
    };
    // ---------- the status lines (MCP SafeValues) ----------

    /// <summary>The card's Now line, the choice in words ("ImageModelNow").</summary>
    internal static string SenseNow(SenseKind kind, SenseModels senses, SetupRoute? thinking)
    {
        var chosen = senses.For(kind);
        return chosen switch
        {
            { Source: SenseSource.OtherSense } => $"Use the same model as the {SenseWord(SenseModels.Other(kind))} model, now {OtherNow(kind, senses)}.",
            { Source: SenseSource.Own, Own: { } own } => $"A model of its own: {own.Describe()}" +
                (chosen.ChosenAt is { } at ? $", chosen on {at.LocalDateTime:d}." : "."),
            _ => thinking is null ? "Use the same model as the text model (Thinking isn't set up yet)."
                : $"Use the same model as the text model (Thinking: {thinking.ModelId})."
        };
    }

    /// <summary>What the other kind's model is now, for "Use the same model as the other kind's model".</summary>
    internal static string OtherNow(SenseKind kind, SenseModels senses) => senses.For(SenseModels.Other(kind)) switch
    {
        { Source: SenseSource.Own, Own: { } own } => own.Describe(),
        { Source: SenseSource.OtherSense } => "the text model, because it uses the same model as this one",
        _ => "the text model"
    };

    /// <summary>What the model that takes <paramref name="kind"/> is known to do ("ImageModelKnown"): what Martlet found out about
    /// it (where and when), else what its name says; null before Thinking is set up.</summary>
    internal static string? SenseKnown(SenseKind kind, SenseModels senses, SetupRoute? thinking, ModelAbilities abilities)
    {
        var image = kind == SenseKind.Image;
        if (senses.Place(kind) is { ModelId: { } model } own && !SenseRouting.IsThinking(own, thinking))
        {
            var origin = own.Place == DeepThinkingPlace.Host ? own.HostOrigin : own.Origin;
            return ModelKnown(kind, model, abilities.Find(origin, model), image ? Said(SenseRouting.Sees(own, abilities))
                : Said(SenseRouting.Hears(own, abilities)));
        }
        if (thinking is null) return null;
        if (!image && !HearingModelCatalog.CarriesAudio(thinking.RouteType))
            return $"{thinking.ModelId} gets no recordings on this route: only an OpenAI-compatible endpoint or one of your computers takes them.";
        return ModelKnown(kind, thinking.ModelId, abilities.Find(thinking.Origin, thinking.ModelId),
            image ? Said(SenseRouting.ThinkingSees(thinking, abilities)) : Said(SenseRouting.ThinkingHears(thinking, abilities)));
    }

    private static bool? Said(VisionSupport support) => support switch { VisionSupport.Supported => true, VisionSupport.Unsupported => false, _ => null };
    private static bool? Said(HearingSupport support) => support switch { HearingSupport.Supported => true, HearingSupport.Unsupported => false, _ => null };

    /// <summary>What <paramref name="model"/> is known to do for <paramref name="kind"/>: what Martlet found out
    /// (<paramref name="found"/>, with its last check: model-abilities.json keeps one source and date for each model), else
    /// <paramref name="byName"/> (its name), else that Martlet doesn't know yet.</summary>
    internal static string ModelKnown(SenseKind kind, string model, ModelAbility? found, bool? byName)
    {
        var image = kind == SenseKind.Image;
        var test = image ? "Test vision" : "Test hearing";
        string Does(bool yes) => image ? yes ? "sees pictures" : "doesn't see pictures" : yes ? "hears recordings" : "doesn't hear recordings";
        if ((image ? found?.Sees : found?.Hears) is { } known)
            return $"{model} {Does(known)}, as Martlet found out (last check on {found!.CheckedAt.LocalDateTime:d}: {found.Source}).";
        return byName is { } yes ? $"By its name, {model} {Does(yes)}. {test} makes sure."
            : $"Martlet doesn't know yet whether {model} {(image ? "sees pictures" : "hears recordings")}. It tries, and remembers a refusal; " +
                $"{test} finds out.";
    }

    /// <summary>What goes to a model of its own, and where ("ImageModelSent").</summary>
    internal static string SenseSent(SenseKind kind, DeepThinkingSettings own)
    {
        var image = kind == SenseKind.Image;
        var what = image ? "Pictures of your screen or camera and a few lines of the conversation"
            : "Recordings of your voice and of what this PC plays, with a few lines of the conversation,";
        if (own.Place == DeepThinkingPlace.Host) return $"{what} go to {own.HostId} through its paired, pinned connection.";
        if (ContextBudget.IsLocalOllama(SetupRouteType.ChatCompletions, own.Origin))
            return $"{(image ? "Pictures" : "Recordings")} stay on this PC: Ollama describes them here.";
        if (own.Origin is { } origin && OnThisPc(origin)) return $"{what} go to {LocalModelServers.Name(origin)} on this PC, an app that may pass them on.";
        return $"{what} go to {(Uri.TryCreate(own.Origin, UriKind.Absolute, out var uri) ? uri.IdnHost : own.Origin)}, and requests may cost money.";
    }

    /// <summary>Companion › Thinking's line on the image and audio models ("ThinkingSenses"): where pictures and recordings go now.</summary>
    internal static string TextModelText(SenseModels senses, SetupRoute? thinking, ModelAbilities? abilities)
    {
        string Goes(SenseKind kind) => SenseRouting.For(kind, senses, thinking, abilities) switch
        {
            { Described: true, Model: { } model } => $"the {SenseWord(kind)} model, {model.Describe()}",
            { Path: SensePath.Thinking } => "Thinking itself",
            _ => kind == SenseKind.Image ? "no model, so Martlet can't see" : "no model, so Thinking gets the transcript only"
        };
        return $"Thinking is the text model: it writes every reply. Pictures go to {Goes(SenseKind.Image)} (Companion › Vision), and " +
            $"recordings go to {Goes(SenseKind.Audio)} (Companion › Hearing).";
    }

    /// <summary>Companion › Thinking's Now card: the text model writes every reply, and where pictures and recordings go, with links
    /// to the image and audio models.</summary>
    private StackPanel TextModelLine(SetupRoute? thinking)
    {
        var line = Note(TextModelText(SenseModels.Load(store?.DataDirectory), thinking, SavedModelAbilities()), new Thickness(0, 6, 0, 0));
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
                _ => [Note("A paired computer's gateway takes no recordings, so there is nothing to test.", new Thickness(0, 0, 0, 0))]
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
                    $"connection, and asks which word it shows. It waits while a reply needs {own.HostId}."), new Thickness(0, 0, 0, 6));
            AutomationProperties.SetAutomationId(line, id + "TestStatus");
            return [button, line];
        }
        return ModelTestControls(kind, own is null ? ThinkingProbe(thinking) : OwnProbe(own, thinking), id + "Test", id + "TestStatus",
            thinking is null ? "Set up Thinking first."
            : $"Test vision asks an OpenAI-compatible endpoint (Ollama on this PC included) directly. {thinking.ModelId} runs on " +
                $"{(SelfHostSetup.IsGateway(thinking.RouteType ?? SetupRouteType.OpenAi) ? thinking.Gateway?.HostId ?? "a paired computer" : "OpenAI")}, " +
                "so Martlet goes by its name and remembers a refused picture.");
    }

    // ---------- the panels ----------

    /// <summary>Use the same model as the text model, or as the other kind's model: nothing more to fill in.</summary>
    private StackPanel SameModelPanel(SenseKind kind, SenseChoice choice, bool inUse, SenseModels senses, SetupRoute? thinking)
    {
        var id = SenseId(kind);
        var other = SenseWord(SenseModels.Other(kind));
        var text = choice == SenseChoice.Thinking
            ? thinking is null ? $"Set up Thinking first. It then takes the {SenseInputs(kind)} itself."
                : $"Thinking ({thinking.ModelId}) takes the {SenseInputs(kind)} in its own request, as before. This is the default, and it " +
                    "adds no work."
            : $"The {SenseWord(kind)} model is the same as the {other} model, now {OtherNow(kind, senses)}. When both kinds use one model, " +
                "it does one job at a time.";
        var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
        panel.Children.Add(Note(text, new Thickness(0, 0, 0, 0)));
        if (!inUse)
            panel.Children.Add(Row(choice == SenseChoice.Thinking
                ? PageButton("Use the text model", () => SaveSenseModelAsync(kind, new() { Source = SenseSource.Thinking, ChosenAt = DateTimeOffset.Now },
                    null, $"The text model (Thinking) takes the {SenseInputs(kind)} again.").Forget(), primary: true, id: id + "UseThinking")
                : PageButton($"Use the {other} model", () => SaveSenseModelAsync(kind, new() { Source = SenseSource.OtherSense, ChosenAt = DateTimeOffset.Now },
                    null, $"The {SenseWord(kind)} model is now the same as the {other} model.").Forget(), primary: true, id: id + "UseOther")));
        return panel;
    }

    /// <summary>A second model in Ollama on this PC: pick one Ollama has (with what it is known to do), see whether it fits beside
    /// Thinking's on the graphics card, download it, use it.</summary>
    private StackPanel SenseLocalPanel(SenseKind kind, SenseModels senses, SetupRoute? thinking)
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
        var saved = senses.For(kind).Own is { Place: DeepThinkingPlace.Endpoint } own &&
            ContextBudget.IsLocalOllama(SetupRouteType.ChatCompletions, own.Origin) ? own.ModelId : null;
        var suggested = image ? VisionModelCatalog.LocalRecommendations.Select(m => m.Tag) : LocalChatModels.Where(m => m.Hears).Select(m => m.Id);
        var choices = (ollamaModels ?? []).Concat(suggested).Distinct(StringComparer.Ordinal).ToArray();
        var first = senseLocalPicked.GetValueOrDefault(kind) ?? saved ??
            choices.FirstOrDefault(m => !OllamaSideBySide.Same(m, beside) && LocalTakes(kind, m, abilities) == true) ?? choices.FirstOrDefault() ?? "";
        var model = new ComboBox { IsEditable = true, Width = 360, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = choices, Text = first };
        AutomationProperties.SetName(model, $"The {SenseWord(kind)} model in Ollama on this PC");
        AutomationProperties.SetAutomationId(model, id + "LocalModel");
        var listed = Note(ollamaModels is null
                ? installed ? "Check Ollama to see which models are downloaded."
                    : "Ollama isn't installed on this PC yet. Set it up on Companion › Thinking › This PC."
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
            known.Text = picked.Length == 0 ? "Choose a model."
                : ModelKnown(kind, picked, now.Find(LocalOllamaBaseUrl, picked), LocalTakes(kind, picked, now)) +
                (ollamaModels is { } downloaded && !LocalOllama.Serves(downloaded, picked) ? " It isn't downloaded yet." : "");
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
        panel.Children.Add(Note($"A second model in Ollama on this PC describes the {SenseInputs(kind)} for Thinking. Ollama runs each model in " +
            $"its own process, so it works while Thinking answers, but only while both fit on the graphics card: Martlet checks before each " +
            $"job, and a reply never waits for it. {(image ? "Pictures" : "Recordings")} stay on this PC.", new Thickness(0, 0, 0, 8)));
        panel.Children.Add(new Label { Content = "_Model", Target = model, Padding = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(model);
        panel.Children.Add(listed);
        panel.Children.Add(known);
        panel.Children.Add(fit);
        panel.Children.Add(Row(
            PageButton("Use this model", () => UseSenseLocal(kind, Picked()), primary: true, id: id + "UseLocal"),
            installed ? PageButton("Download model", () => PullSenseModelAsync(Picked()).Forget(), id: id + "PullModel") : null,
            PageButton("Check Ollama", () =>
            {
                senseLocalFit = null;
                CheckSenseOllamaAsync(quiet: false).Forget();
            }, id: id + "CheckOllama")));
        return panel;
    }

    /// <summary>Whether a model in Ollama on this PC takes <paramref name="kind"/>: what Ollama said (model-abilities.json), else
    /// its name; null when neither says.</summary>
    private static bool? LocalTakes(SenseKind kind, string model, ModelAbilities abilities) => kind == SenseKind.Image
        ? Said(VisionModelCatalog.ForRoute(LocalOllamaBaseUrl, model, abilities))
        : Said(HearingModelCatalog.ForRoute(SetupRouteType.ChatCompletions, LocalOllamaBaseUrl, model, abilities));

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

    private void UseSenseLocal(SenseKind kind, string model)
    {
        try { ChatCompletionsSetup.ModelId(model); }
        catch (ContractException error)
        {
            ActionText.Text = error.Message;
            return;
        }
        var thinking = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        var same = IsLocalOllama(thinking) && OllamaSideBySide.Same(model, thinking!.ModelId);
        var own = new DeepThinkingSettings { Place = DeepThinkingPlace.Endpoint, Origin = LocalOllamaBaseUrl, ModelId = model, ChosenAt = DateTimeOffset.Now };
        SaveSenseModelAsync(kind, new() { Source = SenseSource.Own, Own = own, ChosenAt = DateTimeOffset.Now }, null,
            same ? $"{model} is Thinking's own model, so the text model takes the {SenseInputs(kind)} itself."
            : $"The {SenseWord(kind)} model is now {model} in Ollama on this PC." +
                (ollamaModels is { } known && !LocalOllama.Serves(known, model) ? $" Download {model} to use it." : "")).Forget();
    }

    /// <summary>A cloud provider, a model app on this PC or any OpenAI-compatible server, with its own key, a key the other kind
    /// already uses for that base URL, or Thinking's (as Companion › Thinking › If Thinking fails).</summary>
    private StackPanel SenseCloudPanel(SenseKind kind, SenseModels senses, SetupRoute? thinking)
    {
        var id = SenseId(kind);
        var word = SenseWord(kind);
        // A model app on this PC is offered by name once Martlet has looked (loopback only).
        if (localServers is null && !lookingForLocalServers) LookForSenseServersAsync().Forget();
        IReadOnlyList<CloudProvider> providers =
        [
            .. DeepThinkingProviders.Where(p => p != CustomCloud),
            .. OtherLocalServers().Select(s => new CloudProvider($"{s.Name} on this PC", s.ChatCompletionsBaseUrl, true, s.Models.FirstOrDefault(), s.NeedsKey)),
            CustomCloud
        ];
        var saved = senses.For(kind).Own is { Place: DeepThinkingPlace.Endpoint } savedOwn &&
            !ContextBudget.IsLocalOllama(SetupRouteType.ChatCompletions, savedOwn.Origin) ? savedOwn : null;
        var provider = new ComboBox { ItemsSource = providers, MinHeight = 30, MaxWidth = 420, MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(provider, $"The {word} model's provider");
        AutomationProperties.SetAutomationId(provider, id + "Provider");
        provider.SelectedItem = saved is null ? providers[0] : providers.FirstOrDefault(p => p.BaseUrl == saved.Origin) ?? CustomCloud;
        var baseUrl = new TextBox { MaxLength = 2048, Width = 420, HorizontalAlignment = HorizontalAlignment.Left, Text = saved?.Origin ?? "" };
        AutomationProperties.SetName(baseUrl, $"The {word} model's API base URL");
        AutomationProperties.SetAutomationId(baseUrl, id + "BaseUrl");
        var baseUrlPanel = new StackPanel
        {
            Children = { new Label { Content = "API base URL (without /chat/completions)", Target = baseUrl, Padding = new Thickness(0, 8, 0, 4) }, baseUrl }
        };
        var model = new TextBox { MaxLength = 128, Width = 420, HorizontalAlignment = HorizontalAlignment.Left, Text = saved?.ModelId ?? "" };
        AutomationProperties.SetName(model, $"The {word} model's ID");
        AutomationProperties.SetAutomationId(model, id + "ModelId");
        var key = new PasswordBox { MaxLength = SecretLease.MaximumLength, Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(key, $"The {word} model's API key");
        AutomationProperties.SetAutomationId(key, id + "Key");
        var keyStatus = Note("", new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(keyStatus, id + "KeyStatus");
        var consent = new CheckBox { Margin = new Thickness(0, 12, 0, 8) };
        AutomationProperties.SetAutomationId(consent, id + "Consent");
        var consentText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        consent.Content = consentText;
        CloudProvider Selected() => provider.SelectedItem as CloudProvider ?? CustomCloud;
        string Url() => Selected().BaseUrl is { Length: > 0 } fixedUrl ? fixedUrl : baseUrl.Text.Trim();
        // What the key box will do for the base URL shown now. A key is kept only for its own base URL.
        void ShowKey()
        {
            var p = Selected();
            var url = Url();
            keyStatus.Text = SenseModelChoice.KeptKey(senses, kind, url) is not null
                    ? $"A key for {p.Name} is saved. Leave this empty to keep using it, or paste a new key."
                : thinking is { RouteType: SetupRouteType.ChatCompletions, CredentialId: not null } && thinking.Origin == url
                    ? "Leave this empty to use Thinking's key for the same provider, or paste another key."
                : (p.NeedsKey ? $"Paste your {p.Name} API key. Martlet saves it in Windows Credential Manager."
                    : "Add a key only if your server needs one.") +
                    (saved?.CredentialId is not null && saved.Origin != url
                        ? $" The key saved for {(Uri.TryCreate(saved.Origin, UriKind.Absolute, out var before) ? before.IdnHost : saved.Origin)} " +
                            "is never sent to another address."
                        : "");
        }
        void Refresh(bool keepModel)
        {
            var p = Selected();
            baseUrlPanel.Visibility = p == CustomCloud ? Visibility.Visible : Visibility.Collapsed;
            if (!keepModel) model.Text = saved is not null && saved.Origin == Url() ? saved.ModelId ?? "" : p.DefaultModel ?? "";
            var url = Url();
            ShowKey();
            consentText.Text = SenseConsent(kind, p.Name, url.Length > 0 && OnThisPc(url));
            consent.IsChecked = saved is not null && saved.Origin == url && keepModel;
        }
        Refresh(keepModel: saved is not null);
        provider.SelectionChanged += (_, _) => { tabEdited = true; Refresh(keepModel: false); };
        baseUrl.TextChanged += (_, _) =>
        {
            ShowKey();
            if (!baseUrl.IsKeyboardFocusWithin) return;
            tabEdited = true;
            consent.IsChecked = false;
        };
        model.TextChanged += (_, _) => { if (!model.IsKeyboardFocusWithin) return; tabEdited = true; consent.IsChecked = false; };
        key.PasswordChanged += (_, _) => tabEdited = true;

        var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
        panel.Children.Add(Note($"Choose a model that {(kind == SenseKind.Image ? "sees pictures" : "hears recordings")}. It describes them " +
            "in words for Thinking and never answers you itself.", new Thickness(0, 0, 0, 8)));
        panel.Children.Add(new Label { Content = "_Provider", Target = provider, Padding = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(provider);
        panel.Children.Add(baseUrlPanel);
        panel.Children.Add(new Label { Content = "_Model ID", Target = model, Padding = new Thickness(0, 8, 0, 4) });
        panel.Children.Add(model);
        panel.Children.Add(new Label { Content = "API _key", Target = key, Padding = new Thickness(0, 8, 0, 4) });
        panel.Children.Add(key);
        panel.Children.Add(keyStatus);
        panel.Children.Add(consent);
        panel.Children.Add(Row(PageButton("Use this model", () => SaveSenseCloudAsync(kind, Selected(), Url(), model.Text.Trim(), key,
            consent.IsChecked == true).Forget(), primary: true, id: id + "SaveCloud")));
        return panel;
    }

    /// <summary>The consent a cloud provider, a model app on this PC or another server needs: what is sent there.</summary>
    internal static string SenseConsent(SenseKind kind, string provider, bool onThisPc) => kind == SenseKind.Image
        ? $"I choose {provider} as the image model. Pictures of your screen or camera and a few lines of the conversation are sent there" +
            (onThisPc ? ", an app that may pass them on." : ", and requests may cost money.")
        : $"I choose {provider} as the audio model. Recordings of your voice and of what this PC plays, with a few lines of the conversation, " +
            "are sent there" + (onThisPc ? ", an app that may pass them on." : ", and requests may cost money.");

    private async Task LookForSenseServersAsync()
    {
        await LookForLocalServersAsync(quiet: true);
        if (!closing && openTab is CompanionTab.Vision or CompanionTab.Hearing && !tabEdited) RenderTab();
    }

    private async Task SaveSenseCloudAsync(SenseKind kind, CloudProvider provider, string url, string model, PasswordBox keyBox, bool consent)
    {
        if (!consent)
        {
            ActionText.Text = $"Tick the box to confirm {provider.Name} as the {SenseWord(kind)} model, then press Use this model.";
            return;
        }
        SecretLease? key = null;
        try
        {
            _ = ChatCompletionsSetup.BaseUri(url);
            ChatCompletionsSetup.ModelId(model);
            using (var entered = keyBox.SecurePassword)
                if (entered.Length > 0) key = TakeKey(keyBox);
            var thinking = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
            var own = new DeepThinkingSettings
            {
                Place = DeepThinkingPlace.Endpoint, Origin = url, ModelId = model, ChosenAt = DateTimeOffset.Now,
                CredentialId = key is null ? SenseModelChoice.KeptKey(SenseModels.Load(store?.DataDirectory), kind, url) : null
            };
            if (key is null && SenseModelChoice.NeedsKey(own, provider.NeedsKey, thinking))
                throw new ContractException(ErrorCode.InvalidContract, $"Paste your {provider.Name} API key first.");
            await SaveSenseModelAsync(kind, new() { Source = SenseSource.Own, Own = own, ChosenAt = DateTimeOffset.Now }, key,
                $"The {SenseWord(kind)} model is now {provider.Name} ({model})." + (key is null ? "" : " Its API key is saved in Windows Credential Manager."));
        }
        catch (Exception error) when (error is ContractException or InvalidOperationException)
        {
            ActionText.Text = error.Message;
        }
        finally { key?.Dispose(); }
    }

    /// <summary>One of your computers for pictures: a paired computer's Thinking pool role, or its Ollama when it doesn't do Thinking
    /// for this PC (the route the Thinking pool would use there).</summary>
    private StackPanel SenseComputersPanel(SenseModels senses, ModelAbilities abilities)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
        var hosts = NetworkMap.Hosts(Inputs());
        if (hosts.Count == 0)
        {
            var none = Note("No computer is paired yet. Add one on Devices.", new Thickness(0, 0, 0, 4));
            AutomationProperties.SetAutomationId(none, "ImageModelHosts");
            panel.Children.Add(none);
            panel.Children.Add(Row(PageButton("Add a computer", () => RunNodeAction(NodeAction.AddComputer), id: "ImageModelAddComputer")));
            return panel;
        }
        var thinkingHost = NetworkMap.ThinkingHost(homeSettings);
        var chosen = senses.Image.Own is { Place: DeepThinkingPlace.Host } own ? own : null;
        foreach (var host in hosts)
        {
            var check = hostChecks.GetValueOrDefault(host.HostId);
            var seen = check is { Reachable: true, Routes: { } routes } ? PoolHost(host, routes) : null;
            var offer = seen is null ? null : ThinkingPoolAutoJoin.Route(seen, thinkingHost);
            var detail = offer is not null
                ? $"{(offer.RouteId == SelfHostSetup.DeepThinkingRouteId ? "Its Thinking pool role runs" : "Its Ollama runs")} {offer.ModelId}: " +
                    (Said(VisionModelCatalog.ForRoute(host.Pairing.Origin, offer.ModelId, abilities)) switch
                    {
                        true => "it sees pictures.",
                        false => "it doesn't see pictures.",
                        _ => "Martlet can't tell yet whether it sees."
                    }) + (chosen?.HostId == host.HostId ? " In use." : "")
                : check?.Reachable == false ? "Not reachable right now."
                : check is null ? "Not checked yet. Check computers to see what it offers."
                : seen?.Offers.Any(o => o.RouteId == SelfHostSetup.OllamaRouteId) == true
                    ? "Its Ollama does Thinking for this PC. Add the Thinking pool role there to describe pictures beside it."
                : "It offers no model for this. Add the Thinking pool role there.";
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = host.HostId, FontSize = 15, FontWeight = FontWeights.SemiBold });
            var line = Note(detail, new Thickness(0, 2, 0, 0));
            AutomationProperties.SetName(line, $"{host.HostId}: {detail}");
            AutomationProperties.SetAutomationId(line, "ImageModelHost-" + host.HostId);
            text.Children.Add(line);
            var use = PageButton("Use for pictures", () => UseSenseHostAsync(host).Forget(), id: "ImageModelUseHost-" + host.HostId);
            AutomationProperties.SetName(use, $"Use {host.HostId} for pictures");
            use.IsEnabled = offer is not null;
            use.VerticalAlignment = VerticalAlignment.Center;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            DockPanel.SetDock(use, Dock.Right);
            row.Children.Add(use);
            row.Children.Add(text);
            panel.Children.Add(row);
        }
        panel.Children.Add(Row(PageButton("Check computers", () => RunNodeAction(NodeAction.CheckHost), id: "ImageModelCheckHosts")));
        panel.Children.Add(Note("Pictures and a few lines of the conversation go to that computer through its paired, pinned connection. Its " +
            "model describes them for Thinking one at a time, and waits while a reply needs that computer's graphics card.", new Thickness(0, 4, 0, 0)));
        return panel;
    }

    /// <summary>Uses <paramref name="host"/>'s model for pictures: checks the computer, then saves the route the Thinking pool would
    /// use there.</summary>
    private async Task UseSenseHostAsync(PairedHost host)
    {
        if (closing) return;
        ActionText.Text = $"Checking {host.HostId}...";
        try
        {
            var check = await HostControl.CheckAsync(host.Pairing, HardwareStore, lifetime.Token);
            hostChecks[host.HostId] = check;
            var seen = check.Reachable == true ? PoolHost(host, check.Routes ?? []) : null;
            if (seen is null || ThinkingPoolAutoJoin.Route(seen, NetworkMap.ThinkingHost(homeSettings)) is not { } offer)
            {
                ActionText.Text = seen is null ? $"{host.HostId} didn't answer ({check.Text})."
                    : $"{host.HostId} offers no model for pictures beside this PC's Thinking. Add the Thinking pool role there.";
                if (!closing && openTab == CompanionTab.Vision) RenderTab();
                return;
            }
            var own = ThinkingPoolAutoJoin.Member(seen, offer, DateTimeOffset.Now) with { Slots = null };
            await SaveSenseModelAsync(SenseKind.Image, new() { Source = SenseSource.Own, Own = own, ChosenAt = DateTimeOffset.Now }, null,
                $"The image model is now {own.Describe()}.");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or InvalidOperationException or ContractException or
            Martlet.Avatar.Audio2Face.Remote.Audio2FaceHostException)
        {
            ActionText.Text = error.Message;
        }
    }

    // ---------- saving ----------

    /// <summary>Saves <paramref name="next"/> for <paramref name="kind"/> in sense-models.json. A new key is written to Windows
    /// Credential Manager first and removed again if the file can't be saved; a key no choice uses any more is removed after. The
    /// running conversation follows at once, and the chosen model's server is asked what it takes.</summary>
    private async Task SaveSenseModelAsync(SenseKind kind, SenseModel next, SecretLease? key, string done)
    {
        if (store is null || closing) return;
        var directory = store.DataDirectory;
        var profile = homeSettings?.Profile.Id ?? Guid.Empty;
        var vault = new WindowsCredentialStore();
        CredentialBinding? written = null;
        try
        {
            if (key is not null && next.Own is { } withKey)
            {
                if (profile == Guid.Empty) throw new InvalidOperationException("Set up Martlet first, then add the key.");
                next = next with { Own = withKey with { CredentialId = Guid.NewGuid() } };
                var binding = next.Own!.Binding(profile, next.Own.CredentialId!.Value);
                var lease = key;
                var error = await Task.Run(() => vault.Write(binding, lease), lifetime.Token);
                if (error != CredentialError.None) throw new InvalidOperationException(CredentialMessages.Describe(error));
                written = binding;
            }
            var (after, released) = SenseModelChoice.Choose(SenseModels.Load(directory), kind, next);
            if (!after.Save(directory)) throw new InvalidOperationException("Couldn't save the choice. Check access to Martlet's data folder.");
            written = null;
            if (profile != Guid.Empty)
                foreach (var gone in released)
                {
                    var binding = gone.Binding(profile, gone.CredentialId!.Value);
                    await Task.Run(() => vault.Delete(binding), CancellationToken.None);
                }
            // The page keeps showing the option just saved (also while the part is off, where it offers to turn it on).
            pickerShown[OptionalExtras.SensePickerId(kind)] = ChoiceOf(after.For(kind)).ToString();
            conversation?.ReloadSenseModels();
            var thinking = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
            var route = SenseRouting.For(kind, after, thinking, SavedModelAbilities());
            ErrorLog.Info($"{(kind == SenseKind.Image ? "Image" : "Audio")} model: {SenseNow(kind, after, thinking)} {route.Why}");
            // Choosing a model on Vision or Hearing and pressing its button uses it: the part turns on again if it was off.
            // Hearing goes back to its usual rule (on while the recording stays on this PC, else until you tick it).
            var turnedOn = kind == SenseKind.Image ? !Talk.Watch : Talk.HearVoice == false;
            if (turnedOn) SaveTalk(kind == SenseKind.Image ? Talk with { Watch = true } : Talk with { HearVoice = null });
            ActionText.Text = $"{done} {route.Why}" + (!turnedOn ? "" : kind == SenseKind.Image ? " Vision is on." : " Hearing is on again.");
            if (after.For(kind).Own is { Place: DeepThinkingPlace.Endpoint } chosen) CheckChosenModelAsync(chosen, thinking).Forget();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or
            JsonException)
        {
            ActionText.Text = error.Message;
        }
        finally
        {
            if (written is { } orphan) vault.Delete(orphan);
            if (!closing && openTab is CompanionTab.Vision or CompanionTab.Hearing)
            {
                tabEdited = false;
                RenderTab();
            }
        }
    }
}
