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

/// <summary>Companion › Vision's and Companion › Hearing's models (docs/SENSE_MODELS.md): which models take pictures and which take
/// recordings. Thinking, the text model, always writes the reply, and with an empty list it also takes both itself (an omni
/// model). Each page has a list of models of their own (MainWindow.SensePools.cs, pools-local.json): Ollama on this PC, one of your
/// computers or one of its graphics cards, a server you run, or a cloud provider. The first one that takes the kind puts what it sees or
/// hears into words for Thinking; the next ones take a job when it is busy. A cloud model's key is in Windows Credential Manager
/// (pool-keys.json says which). Choosing a model asks its server what it takes, and Test vision and Test hearing find out
/// (MainWindow.ModelAbilities.cs). Companion › Thinking says where pictures and recordings go. Each page shows the lines of
/// <see cref="SenseNowLines"/> in its Now card.</summary>
public partial class MainWindow
{
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
        var setup = SavedSenseSetup();
        var senses = setup.Senses;
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
        Line(id + "Now", SenseNow(kind, senses, setup.For(kind).Count, thinking));
        Line(id + "Route", route.Why, problem: route.Path == SensePath.None && thinking is not null);
        if (SenseKnown(kind, senses, thinking, abilities) is { } known) Line(id + "Known", known);
        // Only a model that takes this kind gets anything sent to it.
        if (own is not null && route.Described) Line(id + "Sent", SenseSent(kind, own));
        if (setup.State == "unreadable")
            lines.Add(Warning($"Martlet can't read {Martlet.Core.Cluster.PoolSettings.LocalFileName} (a newer Martlet's, or a damaged file), so " +
                "the text model takes pictures and recordings. Changing the list below replaces the file."));
        lines.AddRange(SenseTestControls(kind, own, thinking, route));
        return lines;
    }

    // ---------- the status lines (MCP SafeValues) ----------

    /// <summary>The card's Now line, the list in words ("ImageModelNow"): the first model of its own and how many more take a job
    /// when it is busy, or the text model with an empty list. <paramref name="members"/>: the models in the list this PC can use.</summary>
    internal static string SenseNow(SenseKind kind, SenseModels senses, int members, SetupRoute? thinking) => senses.Place(kind) switch
    {
        { } own => $"{own.Describe()} is first in the list" +
            (members > 1 ? $"; {members - 1} more {(members == 2 ? "takes" : "take")} a job when it is busy." : "."),
        _ => thinking is null ? $"Nothing in the list: the text model takes the {SenseInputs(kind)} (Thinking isn't set up yet)."
            : $"Nothing in the list: the text model takes the {SenseInputs(kind)} itself (Thinking: {thinking.ModelId})."
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
        var line = Note(TextModelText(SavedSenses(), thinking, SavedModelAbilities()), new Thickness(0, 6, 0, 0));
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
                _ => [Note($"Test hearing asks an OpenAI-compatible endpoint. {own.Describe()} is tried as it is, and Martlet remembers a " +
                    "refused recording.", new Thickness(0, 0, 0, 0))]
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
        panel.Children.Add(HelpTip.Explain($"A second model in Ollama on this PC describes the {SenseInputs(kind)} for Thinking. Ollama runs each model in " +
            $"its own process, so it works while Thinking answers, but only while both fit on the graphics card: Martlet checks before each " +
            $"job, and a reply never waits for it. {(image ? "Pictures" : "Recordings")} stay on this PC.", new Thickness(0, 0, 0, 8), "SenseModel", "the second model"));
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
}
