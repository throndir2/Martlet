using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Settings;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>Companion › Thinking › This PC › Ollama on this PC (installing Ollama and downloading, testing and using a model stay
/// in MainWindow.LocalThinking.cs).</summary>
internal sealed partial class ThinkingCards
{
    /// <summary>The model typed under Another Ollama model, kept while the page draws again.</summary>
    private string? ollamaTyped;

    /// <summary>Thinking › This PC › Ollama: Ollama's state, then its models as an option picker (<c>Picker-OllamaModel-&lt;model&gt;</c>):
    /// the suggestions with their facts (fit on this PC's card, graphics memory, how soon they answer, whether they hear),
    /// the models Ollama already has, and Another Ollama model for typing any name (<c>SetupLocalModel</c>). The shown
    /// model's details hold Download model, Check Ollama, Test model and Use Ollama on this PC, and the last test.</summary>
    internal Border LocalThinking(SetupRoute? route)
    {
        var installed = !Prerequisites.IsMissing(Prerequisites.Ollama);
        // What Ollama already has decides whether switching needs a download, so look once without being asked (loopback only).
        if (installed && services.OllamaModels is null && !services.OllamaAutoChecked)
        {
            services.OllamaAutoChecked = true;
            services.CheckOllama(quiet: true);
        }
        var ollamaModels = services.OllamaModels;
        var machine = services.Machine;
        var inUse = MainWindow.IsLocalOllama(route) ? route!.ModelId : null;
        var options = JobOptions.OllamaModels(MainWindow.LocalChatModels, ollamaModels, inUse, machine.BestGpu?.MemoryGb)
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

        var recommended = MainWindow.RecommendedLocalModel(machine.BestGpu?.MemoryGb);
        var smartest = MainWindow.LargestLocalModel(machine.BestGpu?.MemoryGb);
        var suggestion = HelpTip.Explain($"Recommended for this PC: {recommended.Id}, the fastest. " +
            (machine.BestGpu is { } best ? $"This PC has {best.Describe()}" : "No dedicated graphics card was found, so models run on the processor") +
            (smartest.Id != recommended.Id ? $": it fits up to {smartest.Id} with room for a game and Martlet's character. " : ". ") +
            "Bigger models are smarter but slower.", new Thickness(0, 0, 0, 8), "LocalRecommendation", "the recommended model",
            wiki: "Thinking#ollama-on-this-pc");
        AutomationProperties.SetAutomationId(suggestion, "SetupLocalRecommendation");

        // Use a model file you downloaded: a GGUF from Hugging Face, LM Studio's folder or anywhere else becomes an Ollama model.
        var addFile = Row(PageButton("Use a model file you downloaded...", services.AddOllamaModelFile, id: "SetupAddModelFile"));
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
            services.OptionPickerBody("OllamaModel", options));
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
            model.TextChanged += (_, _) => { services.MarkEdited(); ollamaTyped = model.Text; };
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
            var last = services.LocalModelTest is { } outcome && outcome.Model == ModelId() ? outcome : null;
            tested.Text = last?.Text ?? (ModelId().Length == 0 ? "" : $"{ModelId()} isn't tested yet; test it before you use it.");
            tested.SetResourceReference(TextBlock.ForegroundProperty, last is null ? "MutedBrush" : last.Passed && !last.Warning ? "SuccessBrush"
                : "WarningBrush");
        }
        ShowTest();
        services.ShowLocalTest = ShowTest;
        if (model is not null) model.TextChanged += (_, _) => ShowTest();
        var test = PageButton("Test model", () => services.TestLocalModel(ModelId()), id: "SetupTestLocalModel");
        // Until Ollama is installed, installing it (with the chosen model, then switching to it) is the only step that does anything.
        var buttons = installed
            ? Row(
                PageButton("Download model", () => services.PullOllamaModel(ModelId()), id: "SetupPullModel"),
                PageButton("Check Ollama", () => services.CheckOllama(), id: "SetupCheckOllama"),
                test,
                PageButton("Use Ollama on this PC", () => services.SaveLocalThinking(ModelId()), primary: true, id: "SetupUseLocalThinking"))
            : Row(
                PageButton("Install Ollama and use it", () => services.InstallOllama(ModelId()), primary: true, id: "SetupInstallOllama"),
                test,
                PageButton("Use Ollama on this PC", () => services.SaveLocalThinking(ModelId()), id: "SetupUseLocalThinking"));
        buttons.Margin = new Thickness(0, 8, 0, 0);
        yield return buttons;
        yield return tested;
    }
}
