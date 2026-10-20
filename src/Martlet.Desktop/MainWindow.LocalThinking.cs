using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Presentation;
using Martlet.Providers;
using Martlet.Providers.Ollama;

namespace Martlet.Desktop;

/// <summary>Companion › Thinking › This PC: Ollama for Thinking (install it, choose, download and test a model).</summary>
public partial class MainWindow
{
    // ---------- this PC: Ollama for thinking ----------

    /// <summary>The model typed under Another Ollama model, kept while the page draws again.</summary>
    private string? ollamaTyped;

    /// <summary>Thinking › This PC › Ollama: Ollama's state, then its models as an option picker (<c>Picker-OllamaModel-&lt;model&gt;</c>):
    /// the suggestions with their facts (fit on this PC's card, graphics memory, how soon they answer, whether they hear),
    /// the models Ollama already has, and Another Ollama model for typing any name (<c>SetupLocalModel</c>). The shown
    /// model's details hold Download model, Check Ollama, Test model and Use Ollama on this PC, and the last test.</summary>
    private Border LocalThinkingCard(SetupRoute? route)
    {
        var installed = !Prerequisites.IsMissing(Prerequisites.Ollama);
        // What Ollama already has decides whether switching needs a download, so look once without being asked (loopback only).
        if (installed && ollamaModels is null && !ollamaAutoChecked)
        {
            ollamaAutoChecked = true;
            CheckOllamaAsync(quiet: true).Forget();
        }
        var inUse = IsLocalOllama(route) ? route!.ModelId : null;
        var options = JobOptions.OllamaModels(LocalChatModels, ollamaModels, inUse, machine.BestGpu?.MemoryGb)
            .Select(option => option with { Details = () => OllamaModelControls(option.Key == JobOptions.OtherOllamaModel ? null : option.Key, installed) })
            .ToList();

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), FontWeight = FontWeights.SemiBold,
            Text = (!installed ? "Ollama isn't installed on this PC yet"
                : ollamaModels is null && LocalOllamaRecovery.Last is { Stops: true, OllamaError: { } said }
                    ? $"Ollama is installed, but it stops when it starts (Ollama says: \"{said.TrimEnd('.')}\"); Check Ollama shows what to do"
                : ollamaModels is null ? "Ollama is installed; choose Check Ollama to see which models are downloaded"
                : ollamaModels.Count == 0 ? "Ollama is running, but no model is downloaded yet"
                : $"Ollama is running with: {string.Join(", ", ollamaModels)}") +
                (inUse is not null ? $"; Thinking uses {inUse}." : ".") };
        AutomationProperties.SetAutomationId(status, "SetupOllamaStatus");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);

        var recommended = RecommendedLocalModel(machine.BestGpu?.MemoryGb);
        var smartest = LargestLocalModel(machine.BestGpu?.MemoryGb);
        var suggestion = HelpTip.Explain($"Recommended for this PC: {recommended.Id}, the fastest. " +
            (machine.BestGpu is { } best ? $"This PC has {best.Describe()}" : "No dedicated graphics card was found, so models run on the processor") +
            (smartest.Id != recommended.Id ? $": it fits up to {smartest.Id} with room for a game and Martlet's character. " : ". ") +
            "Bigger models are smarter but slower.", new Thickness(0, 0, 0, 8), "LocalRecommendation", "the recommended model",
            wiki: "Thinking#ollama-on-this-pc");
        AutomationProperties.SetAutomationId(suggestion, "SetupLocalRecommendation");

        // Use a model file you downloaded: a GGUF from Hugging Face, LM Studio's folder or anywhere else becomes an Ollama model.
        var addFile = Row(PageButton("Use a model file you downloaded...", () => AddOllamaModelFileAsync().Forget(), id: "SetupAddModelFile"));
        var addFileNote = HelpTip.Explain("Choose a GGUF model file on this PC, and Martlet adds it to Ollama and thinks with it. " +
            "An mmproj file in the same folder comes too, so the model can see. Ollama keeps its own copy, so you can delete your " +
            "file afterwards.", new Thickness(0, 4, 0, 8), "AddModelFile", "using a model file", wiki: "Thinking#ollama-on-this-pc");
        AutomationProperties.SetAutomationId(addFileNote, "SetupAddModelFileNote");

        return Card(Heading("Ollama on this PC"),
            HelpTip.Explain("Ollama runs a local conversation model. Your messages stay on this PC, with no API key or per-request cost.",
                new Thickness(0, 0, 0, 8), "OllamaThisPc", "Ollama on this PC", wiki: "Thinking#ollama-on-this-pc"),
            status,
            suggestion,
            addFile,
            addFileNote,
            OptionPickerBody("OllamaModel", options));
    }

    /// <summary>Use a model file you downloaded: the owner picks GGUF files, Martlet puts them into this PC's Ollama under a name
    /// made from the file name (<see cref="OllamaModelImport"/>) in a run window, then switches Thinking to that model the way
    /// Use Ollama on this PC does (it loads first). Nothing is saved about the file: the model lives in Ollama on this PC.</summary>
    private async Task AddOllamaModelFileAsync()
    {
        if (Prerequisites.IsMissing(Prerequisites.Ollama))
        {
            ActionText.Text = "Install Ollama first (choose a model below, then Install Ollama and use it), then add your model file.";
            return;
        }
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Use a model file you downloaded", Filter = "GGUF model files (*.gguf)|*.gguf", CheckFileExists = true, Multiselect = true
        };
        if (dialog.ShowDialog(this) != true) return;
        IReadOnlyList<string> files;
        string? projector;
        try { files = OllamaModelImport.Files(dialog.FileNames, out projector); }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        {
            ActionText.Text = error.Message;
            return;
        }
        var model = OllamaModelImport.NameFor(files[0]);
        try { ChatCompletionsSetup.ModelId(model); }
        catch (ContractException error) { ActionText.Text = error.Message; return; }
        var gb = OllamaModelImport.Bytes(files) / 1e9;
        var names = string.Join(", ", files.Select(Path.GetFileName));
        if (!ConfirmationDialog.Confirm(this,
                $"Add {names}{(projector is null ? "" : $" (with {Path.GetFileName(projector)}, so the model can see)")} to Ollama on this " +
                $"PC as {model} and switch Thinking to it (Ollama keeps its own copy, about {gb:0.0} GB more; the model's own license applies)?",
                "Add and switch", questionId: "AddModelFileQuestion"))
        {
            ActionText.Text = "Nothing changed.";
            return;
        }
        ErrorLog.Info($"Thinking: adding {files.Count} model file(s) to Ollama on this PC as {model}.");
        ActionText.Text = $"Adding {model} to Ollama...";
        var done = await HostRunWindow.RunAsync(this, $"Add {model} to Ollama", async run =>
        {
            await LocalOllama.ImportAsync(model, files, run.Status, run.Output, run.Token);
            return $"{model} is in Ollama on this PC.";
        }, join: true);
        if (closing) return;
        if (done is null)
        {
            ActionText.Text = $"{model} wasn't added to Ollama; the run window says why.";
            return;
        }
        await CheckOllamaAsync(quiet: true);
        if (closing) return;
        if (await SaveLocalThinkingAsync(model, confirmed: true) && !closing) await TestLocalModelAsync(model);
    }

    /// <summary>The shown Ollama model's controls: for Another Ollama model (<paramref name="fixedModel"/> null) the name box,
    /// then Ollama's buttons for that model and the last test of it.</summary>
    private IEnumerable<UIElement> OllamaModelControls(string? fixedModel, bool installed)
    {
        TextBox? model = null;
        if (fixedModel is null)
        {
            model = new TextBox { MaxLength = 128, Width = 420, HorizontalAlignment = HorizontalAlignment.Left, Text = ollamaTyped ?? "" };
            AutomationProperties.SetName(model, "Local model");
            AutomationProperties.SetAutomationId(model, "SetupLocalModel");
            InputHint.SetText(model, "gemma4:e4b");
            model.TextChanged += (_, _) => { tabEdited = true; ollamaTyped = model.Text; };
            yield return new Label { Content = "Ollama _model", Target = model, Padding = new Thickness(0, 4, 0, 4) };
            yield return model;
            var own = Note("Download model gets it; models already downloaded are listed above.", new Thickness(0, 4, 0, 0));
            AutomationProperties.SetAutomationId(own, "SetupLocalOwnModels");
            yield return own;
        }

        string ModelId() => fixedModel ?? (model!.Text ?? "").Trim();
        var tested = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        AutomationProperties.SetAutomationId(tested, "SetupLocalModelTest");
        AutomationProperties.SetLiveSetting(tested, AutomationLiveSetting.Polite);
        void ShowTest()
        {
            var last = localModelTest is { } outcome && outcome.Model == ModelId() ? outcome : null;
            tested.Text = last?.Text ?? (ModelId().Length == 0 ? "" : $"{ModelId()} isn't tested yet; test it before you use it.");
            tested.SetResourceReference(TextBlock.ForegroundProperty, last is null ? "MutedBrush" : last.Passed && !last.Warning ? "SuccessBrush"
                : "WarningBrush");
        }
        ShowTest();
        showLocalTest = ShowTest;
        if (model is not null) model.TextChanged += (_, _) => ShowTest();
        var test = PageButton("Test model", () => TestLocalModelAsync(ModelId()).Forget(), id: "SetupTestLocalModel");
        // Until Ollama is installed, installing it (with the chosen model, then switching to it) is the only step that does anything.
        var buttons = installed
            ? Row(
                PageButton("Download model", () => PullOllamaModelAsync(ModelId()).Forget(), id: "SetupPullModel"),
                PageButton("Check Ollama", () => CheckOllamaAsync().Forget(), id: "SetupCheckOllama"),
                test,
                PageButton("Use Ollama on this PC", () => SaveLocalThinkingAsync(ModelId()).Forget(), primary: true, id: "SetupUseLocalThinking"))
            : Row(
                PageButton("Install Ollama and use it", () => InstallOllamaAsync(ModelId()).Forget(), primary: true, id: "SetupInstallOllama"),
                test,
                PageButton("Use Ollama on this PC", () => SaveLocalThinkingAsync(ModelId()).Forget(), id: "SetupUseLocalThinking"));
        buttons.Margin = new Thickness(0, 8, 0, 0);
        yield return buttons;
        yield return tested;
    }
    /// <summary>The last model test on this PC's Ollama: which model, what it found, and whether it worked.</summary>
    private sealed record LocalModelTestOutcome(string Model, string Text, bool Passed, bool Warning);

    /// <summary>Loads <paramref name="model"/> in this PC's Ollama and asks it for a short streamed reply, the way Martlet's
    /// replies do, so a model that won't run here shows up while it is being set up rather than on the first message.</summary>
    private async Task TestLocalModelAsync(string model)
    {
        try { ChatCompletionsSetup.ModelId(model); }
        catch (ContractException error) { ActionText.Text = error.Message; return; }
        if (testingLocalModel)
        {
            ActionText.Text = "A model test is already running.";
            return;
        }
        testingLocalModel = true;
        ActionText.Text = $"Testing {model} in Ollama...";
        // Like a reply on this route: no reply budget unless a max reply length is set, and the local route's first-answer wait.
        int? replyTokens = GenerationSupport.SendsReplyBudget(GenerationSupport.LocalOllamaChatBaseUrl, homeSettings?.Generation)
            ? homeSettings!.Generation!.ReplyTokens : null;
        LocalModelTestResult? result = null;
        string? failure = null;
        try
        {
            await HostRunWindow.RunAsync(this, $"Test {model}", async run =>
            {
                try
                {
                    result = await LocalOllama.TestAsync(model, replyTokens, LiveConversationConfiguration.LocalOllamaTextLimits.FirstDeltaTimeout,
                        run.Status, run.Output, run.Token, GenerationSettings.ThinkingSteps(homeSettings?.Generation), store?.DataDirectory);
                    return result.Summary;
                }
                catch (InvalidOperationException error)
                {
                    failure = error.Message;
                    throw;
                }
            });
        }
        finally { testingLocalModel = false; }
        if (closing) return;
        var route = homeSettings.ThinkingRoute();
        var inUse = IsLocalOllama(route) && route!.ModelId == model;
        if (result is not null)
        {
            // The test loaded the model, so Ollama says exactly how much context it gives it.
            var context = await CheckLocalModelContextAsync(model);
            var summary = result.Summary.TrimEnd('.') + (context?.ContextTokens is { } given
                ? $"; Ollama gives it {given:N0} tokens of context{(context.ModelMaximum is { } most && most > given ? $" of the {most:N0} it holds (raise Ollama's context length setting to give it more)" : "")}."
                : ".");
            if (closing) return;
            localModelTest = new(model, summary, true, result.Warning);
            ActionText.Text = inUse ? summary : summary.TrimEnd('.') + "; choose Use Ollama on this PC to use it.";
            showContextStatus?.Invoke();
        }
        else if (failure is not null)
        {
            localModelTest = new(model, $"Test failed: {failure}", false, false);
            ActionText.Text = localModelTest.Text;
        }
        else ActionText.Text = $"Testing {model} stopped before it finished.";
        showLocalTest?.Invoke();
    }

    /// <summary>Downloads a model into this PC's Ollama in a run window (no console), then refreshes what Ollama has.</summary>
    private async Task PullOllamaModelAsync(string model)
    {
        try { ChatCompletionsSetup.ModelId(model); }
        catch (ContractException error) { ActionText.Text = error.Message; return; }
        ActionText.Text = $"Downloading {model} with Ollama...";
        var done = await HostRunWindow.RunAsync(this, $"Download {model}", async run =>
        {
            await LocalOllama.PullAsync(model, run.Status, run.Output, run.Token);
            return $"{model} is downloaded; test it, then choose Use Ollama on this PC.";
        }, join: true);
        if (closing) return;
        ActionText.Text = done ?? $"{model} was not downloaded.";
        if (done is not null) await CheckOllamaAsync();
    }

    /// <summary>One click: installs Ollama with the chosen model (no console), thinks with it once it is there, then tests it so
    /// a model that won't run on this PC shows up now rather than on the first message.</summary>
    private async Task InstallOllamaAsync(string model)
    {
        try { ChatCompletionsSetup.ModelId(model); }
        catch (ContractException error) { ActionText.Text = error.Message; return; }
        await InstallPrerequisitesAsync([Prerequisites.Ollama], model);
        if (closing) return;
        if (Prerequisites.IsMissing(Prerequisites.Ollama))
        {
            if (openTab == CompanionTab.Thinking) RenderTab();
            return;
        }
        await CheckOllamaAsync();
        if (closing) return;
        if (ollamaModels?.Contains(model, StringComparer.Ordinal) != true)
        {
            ActionText.Text = $"Ollama is installed; download {model} to use it.";
            return;
        }
        await SaveLocalThinkingAsync(model);
        if (!closing) await TestLocalModelAsync(model);
    }

    /// <summary>Asks the local Ollama (loopback only) which models it has. <paramref name="quiet"/>: the Thinking tab's own
    /// check when it opens, which leaves the status line and a model being typed alone. Otherwise, when Ollama doesn't answer,
    /// the status line says why in Ollama's own words, after Martlet repaired the known cause (<see cref="OllamaNotAnsweringAsync"/>).</summary>
    private async Task CheckOllamaAsync(bool quiet = false)
    {
        IReadOnlyList<string>? models;
        try { models = await LocalOllama.ModelsAsync(TimeSpan.FromSeconds(4), lifetime.Token); }
        catch (OperationCanceledException) { return; }
        if (closing) return;
        string? said = null;
        if (models is null && !quiet && !Prerequisites.IsMissing(Prerequisites.Ollama))
        {
            var (text, repaired) = await OllamaNotAnsweringAsync("check again");
            if (closing) return;
            said = text;
            if (repaired)
            {
                try { models = await LocalOllama.ModelsAsync(TimeSpan.FromSeconds(4), lifetime.Token); }
                catch (OperationCanceledException) { return; }
            }
        }
        ollamaModels = models?.Take(50).ToArray();
        // After a repair, its summary already says that Ollama answers again and how many models it lists.
        if (!quiet)
            ActionText.Text = ollamaModels is null
                ? said ?? "Ollama isn't installed on this PC yet; install it first."
                : said is { Length: > 0 } ? said
                : ollamaModels.Count == 0 ? "Ollama is running on this PC, with no model downloaded yet."
                : $"Ollama is running on this PC with {ollamaModels.Count} {(ollamaModels.Count == 1 ? "model" : "models")}.";
        if (!closing && openTab == CompanionTab.Thinking && !(quiet && tabEdited)) RenderTab();
    }

    /// <summary>Use Ollama on this PC. With Ollama installed, the model in the box is downloaded first when it isn't here yet
    /// (after one confirmation) and loaded, in a run window, and only then does Thinking switch to it: the current Thinking
    /// keeps answering until then, the first reply doesn't wait for the load, and a model that won't download or load leaves
    /// Thinking as it was.</summary>
    private async Task<bool> SaveLocalThinkingAsync(string model, bool confirmed = false)
    {
        try { ChatCompletionsSetup.ModelId(model); }
        catch (ContractException error) { ActionText.Text = error.Message; return false; }
        if (!Prerequisites.IsMissing(Prerequisites.Ollama) && !await PrepareLocalThinkingAsync(model, confirmed)) return false;
        if (closing) return false;
        var saved = await SaveSectionRouteAsync(HostJob.Thinking,
            settings => ChatCompletionsSetup.SelectRoute(settings, LocalOllamaBaseUrl, model), key: null,
            $"Martlet now uses {model} in Ollama on this PC" +
            (ollamaModels is { } known && !LocalOllama.Serves(known, model) ? $"; download {model} to use it." : "."));
        // Ollama says how much context it gives the model once it has loaded it (Test model does); nothing leaves this PC.
        if (!closing && IsLocalOllama(homeSettings.ThinkingRoute()))
            CheckNewModelContextAsync().Forget();
        return saved;
    }

    /// <summary>The model chosen last with Use Ollama on this PC. Several can download and load side by side; one chosen
    /// earlier that gets ready after it doesn't switch Thinking.</summary>
    private string? localThinkingWanted;

    /// <summary>Gets <paramref name="model"/> ready in this PC's Ollama before Thinking switches to it: starts Ollama when it
    /// isn't answering, downloads the model when it isn't here (the owner confirms the download, unless
    /// <paramref name="confirmed"/> already did), then loads it. Returns
    /// whether Thinking may switch now; otherwise the status line says why it didn't. Choosing another model meanwhile
    /// doesn't wait for this one: the newest choice is the one Thinking switches to.</summary>
    private async Task<bool> PrepareLocalThinkingAsync(string model, bool confirmed = false)
    {
        try
        {
            var token = lifetime.Token;
            var models = await LocalOllama.ModelsAsync(TimeSpan.FromSeconds(3), token);
            if (models is null && LocalOllama.Start())
            {
                ActionText.Text = "Starting Ollama on this PC...";
                for (var attempt = 0; models is null && attempt < 15 && !closing; attempt++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), token);
                    models = await LocalOllama.ModelsAsync(TimeSpan.FromSeconds(2), token);
                }
            }
            if (closing) return false;
            if (models is null)
            {
                var (text, repaired) = await OllamaNotAnsweringAsync("try again");
                if (closing) return false;
                if (repaired) models = await LocalOllama.ModelsAsync(TimeSpan.FromSeconds(3), token);
                if (closing) return false;
                if (models is null)
                {
                    ActionText.Text = "Thinking didn't change: " + (text.Length > 0 ? text : "Ollama didn't answer on this PC.");
                    return false;
                }
            }
            ollamaModels = models;
            var thinking = homeSettings.ThinkingRoute();
            var keeps = thinking is null || IsLocalOllama(thinking) && thinking.ModelId == model ? ""
                : $" Thinking keeps using {(IsLocalOllama(thinking) ? thinking.ModelId : NetworkMap.ProviderName(thinking))} until it's ready.";
            var download = !LocalOllama.Serves(models, model);
            if (download)
            {
                var size = LocalChatModels.FirstOrDefault(m => m.Id == model)?.Size;
                if (!confirmed && !ConfirmationDialog.Confirm(this,
                        $"Download {model} with Ollama{(size is null ? "" : $" ({size})")}, load it and switch Thinking to it (the model's own " +
                        "license applies)?",
                        "Download and switch", questionId: "LocalModelDownloadQuestion"))
                {
                    ActionText.Text = "Thinking didn't change.";
                    return false;
                }
            }
            localThinkingWanted = model;
            var done = await HostRunWindow.RunAsync(this, $"Switch Thinking to {model}", async run =>
            {
                if (keeps.Length > 0) run.Output.Report(keeps.Trim());
                if (download) await LocalOllama.PullAsync(model, run.Status, run.Output, run.Token);
                var loaded = await LocalOllama.LoadAsync(model, run.Status, run.Output, run.Token, store?.DataDirectory);
                return $"{model} is {(download ? "downloaded and " : "")}loaded{(loaded >= TimeSpan.FromSeconds(1) ? $" ({loaded.TotalSeconds:0.0} s)" : "")}; " +
                    (localThinkingWanted == model ? "Thinking switches to it now." : $"you chose {localThinkingWanted} since, so Thinking doesn't switch to it.");
            }, join: true);
            if (closing) return false;
            if (download) await CheckOllamaAsync(quiet: true);
            if (done is null)
            {
                ActionText.Text = $"Thinking didn't change: {model} isn't ready (the run window says why).";
                return false;
            }
            if (localThinkingWanted != model)
            {
                ActionText.Text = $"{model} is ready, but you chose {localThinkingWanted} since; Thinking switches to that one when it's ready.";
                return false;
            }
            return true;
        }
        catch (OperationCanceledException) { return false; }
    }

    /// <summary>What Ollama on this PC says about <paramref name="model"/>'s context, kept for replies; loopback only, and the
    /// model isn't loaded for it.</summary>
    private async Task<ModelContextReport?> CheckLocalModelContextAsync(string model)
    {
        try
        {
            using var client = ModelContextProbe.CreateClient(loopback: true);
            var report = await ModelContextProbe.OllamaAsync(client, LocalOllamaOrigin, model, load: false, lifetime.Token);
            if (report.Reached) RecordModelLimit(LocalOllamaBaseUrl, model, report);
            return report;
        }
        catch (OperationCanceledException) { return null; }
    }
}
