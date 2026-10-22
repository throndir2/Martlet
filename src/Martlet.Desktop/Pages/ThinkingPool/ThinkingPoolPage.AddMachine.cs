using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Presentation;
using Martlet.Providers;
using Martlet.Providers.Ollama;

namespace Martlet.Desktop;

/// <summary>Companion › Background thinking's add forms: Add a cloud provider (in Where it runs), and under Advanced another model
/// in Ollama on this PC and another of your computers. What each form adds shows in Where it runs, ticked.</summary>
internal sealed partial class ThinkingPoolPage
{
    /// <summary>Another of your computers (Advanced): a paired computer with a Thinking model joins the list by itself, and what
    /// goes there. Each paired computer is in Where it runs with its own box.</summary>
    private List<UIElement> DeepComputersPanel(ThinkingPoolPageModel model)
    {
        var stack = new List<UIElement>();
        var automatic = HelpTip.Explain(model.AutoJoinText + " " + ThinkingPoolPageModel.AutoJoinMore, new Thickness(0, 0, 0, 8),
            "ThinkingPoolAutoJoin", "computers that join by themselves", wiki: "Thinking#add-a-machine");
        AutomationProperties.SetAutomationId(automatic, "DeepThinkingAutoJoin");
        stack.Add(automatic);
        if (model.Hosts.Count == 0)
        {
            var none = Note("No computer is paired yet: pair one on Devices, and it shows in Where it runs.", new Thickness(0, 0, 0, 4));
            AutomationProperties.SetAutomationId(none, "DeepThinkingHosts");
            stack.Add(none);
        }
        stack.Add(HelpTip.Explain("A job's text goes to the machine that runs it, and Thinking, the voice and listening keep working here " +
            "at full speed. For a think, the text is the conversation so far that fits in 16 KiB and the task; a paired computer gets " +
            "it through its paired, pinned connection. The Thinking pool role is an Ollama of its own, so it works even on the " +
            "computer that does Thinking (they share its graphics card, and replies may start later); without it, a computer that " +
            "also does Thinking for the conversation can't join. Ticking a computer in Where it runs sets up the role with the " +
            "model chosen there; its model list changes it later. A computer's model can only work as long as its Martlet allows: update it to this " +
            "version for jobs over a minute.", new Thickness(0, 8, 0, 0), "ThinkingPoolRole", "the Thinking pool role",
            wiki: "Thinking#add-a-machine"));
        return stack;
    }

    /// <summary>A second model in Ollama on this PC, beside Thinking's: Ollama runs each model in its own process, so it thinks in
    /// parallel with the conversation while both fit on the graphics card (checked here, and before each think). The model box
    /// starts with one that isn't Thinking's and isn't in the list yet.</summary>
    private List<UIElement> DeepLocalPanel(ThinkingPoolPageModel model)
    {
        var beside = model.LocalBeside;
        var choices = model.LocalChoices;
        var modelBox = new ComboBox { IsEditable = true, Width = 300, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = choices,
            Text = model.LocalStart(choices) };
        AutomationProperties.SetName(modelBox, "Model for the Thinking pool");
        AutomationProperties.SetAutomationId(modelBox, "DeepThinkingLocalModel");
        var state = Note(model.LocalState, new Thickness(0, 4, 0, 0));
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
                ShowFit($"{id} is Thinking's own model, which can't think something over while it answers you; choose another model.", true);
            else if (localFitModel == id && localFit is { } known) ShowFit((known.Fits ? "Fits: " : "Doesn't fit: ") + known.Why, !known.Fits);
            else
            {
                ShowFit($"Checking whether {id} fits beside {beside} on the graphics card...", false);
                CheckLocalFitAsync(model, beside, id, ShowFit).Forget();
            }
        }
        Check(modelBox.Text.Trim());
        modelBox.SelectionChanged += (_, _) => { if (modelBox.SelectedItem is string chosen) Check(chosen); };
        modelBox.LostKeyboardFocus += (_, _) => Check(modelBox.Text.Trim());
        var sharedText = model.LocalShare;
        var shared = sharedText.Length > 0
            ? HelpTip.Explain(sharedText + " " + DeepThinkingFit.SharedCardMore, new Thickness(0, 4, 0, 0), "SharedCard-ThisPc",
                "sharing a graphics card", wiki: "Thinking#add-a-machine")
            : new TextBlock { Visibility = Visibility.Collapsed };
        shared.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        AutomationProperties.SetAutomationId(shared, "DeepThinkingLocalShare");
        return [
            HelpTip.Explain("A second model can think here while Thinking's answers you, such as a larger one beside a small, fast one. Ollama runs " +
                "each model in its own process, so they answer at the same time, but only while both fit on the graphics card: Martlet " +
                "checks before each think and doesn't think it over when they don't. They share the graphics card, so replies may start " +
                "a little later while it thinks.", new Thickness(0, 0, 0, 8), "SecondThinking", "a second model", wiki: "Thinking#add-a-machine"),
            new Label { Content = "_Model", Target = modelBox, Padding = new Thickness(0, 0, 0, 4) }, modelBox, state, fit, shared,
            Row(PageButton("Add to the Thinking pool", () =>
                {
                    var id = modelBox.Text.Trim();
                    if (CanAddLocal(model, id)) model.AddLocalAsync(id).Forget();
                }, primary: true, id: "DeepThinkingUseLocal"),
                PageButton("Check Ollama", () =>
                {
                    localFitModel = null;
                    services.CheckOllamaAsync().Forget();
                }, id: "DeepThinkingCheckOllama"))];
    }

    /// <summary>Whether <paramref name="id"/> may join the pool from Ollama on this PC: a valid model ID that isn't Thinking's own
    /// model, and, when it would share the graphics card with Thinking's, the owner says so. Says why not in the status line.</summary>
    private bool CanAddLocal(ThinkingPoolPageModel model, string id)
    {
        try { ChatCompletionsSetup.ModelId(id); }
        catch (ContractException error) { services.ShowStatus(error.Message); return false; }
        if (model.LocalBeside is { } beside && OllamaSideBySide.Same(id, beside))
        {
            services.ShowStatus($"{id} is Thinking's own model, which can't think something over while it answers you; choose another model.");
            return false;
        }
        var sharedText = model.LocalShare;
        return sharedText.Length == 0 || services.Confirm(sharedText.TrimEnd('.') + "; use it anyway?",
            "The Thinking pool shares a graphics card", yes: "_Use anyway", no: "_Cancel", questionId: "DeepThinkingShareQuestion");
    }

    /// <summary>Checks whether <paramref name="deep"/> fits beside Thinking's <paramref name="thinking"/> in Ollama on this PC
    /// (read-only: it loads nothing), keeps the result for the next drawing and shows it.</summary>
    private async Task CheckLocalFitAsync(ThinkingPoolPageModel model, string thinking, string deep, Action<string, bool> show)
    {
        if (await model.CheckLocalFitAsync(thinking, deep) is not { } fit) return;
        localFit = fit;
        localFitModel = deep;
        if (!services.Closing) show((fit.Fits ? "Fits: " : "Doesn't fit: ") + fit.Why, !fit.Fits);
    }

    /// <summary>Adds a cloud provider or any OpenAI-compatible server (HTTPS, or a server on this PC) to the list, with its own key
    /// or Thinking's. A provider already in the list (on or off) with a saved key uses that key again unless a new one is pasted,
    /// so a second model there needs no key.</summary>
    private List<UIElement> DeepCloudPanel(ThinkingPoolPageModel model)
    {
        var providers = MainWindow.DeepThinkingProviders;
        var provider = new ComboBox { ItemsSource = providers, MinHeight = 30, MaxWidth = 420, MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(provider, "Thinking pool provider");
        AutomationProperties.SetAutomationId(provider, "DeepThinkingProvider");
        provider.SelectedItem = MainWindow.DeepThinkingProviders[0];
        var baseUrl = new TextBox { MaxLength = 2048, Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(baseUrl, "Thinking pool API base URL");
        AutomationProperties.SetAutomationId(baseUrl, "DeepThinkingBaseUrl");
        InputHint.SetText(baseUrl, "https://llm.example.com/v1");
        var baseUrlPanel = new StackPanel { Children = { new Label { Content = "API base URL (without /chat/completions)", Target = baseUrl, Padding = new Thickness(0, 8, 0, 4) }, baseUrl } };
        var modelId = new ComboBox { IsEditable = true, Width = 420, HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = "The models Martlet knows at this provider, free and smartest first. Type another model ID if you like." };
        AutomationProperties.SetName(modelId, "Thinking pool model ID");
        AutomationProperties.SetAutomationId(modelId, "DeepThinkingModel");
        var key = new PasswordBox { MaxLength = SecretLease.MaximumLength, Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(key, "Thinking pool API key");
        AutomationProperties.SetAutomationId(key, "DeepThinkingKey");
        var keyStatus = Note("", new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(keyStatus, "DeepThinkingKeyStatus");
        var consent = new CheckBox { Margin = new Thickness(0, 12, 0, 8) };
        AutomationProperties.SetAutomationId(consent, "DeepThinkingConsent");
        var consentText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        consent.Content = consentText;
        MainWindow.CloudProvider Selected() => provider.SelectedItem as MainWindow.CloudProvider ?? MainWindow.CustomCloud;
        string Url() => Selected().BaseUrl is { Length: > 0 } fixedUrl ? fixedUrl : baseUrl.Text.Trim();
        void Refresh(bool keepModel)
        {
            var p = Selected();
            baseUrlPanel.Visibility = p == MainWindow.CustomCloud ? Visibility.Visible : Visibility.Collapsed;
            if (!keepModel)
            {
                modelId.ItemsSource = p.BaseUrl is { Length: > 0 } url ? ThinkingPoolPageModel.CloudModels(url, p.DefaultModel) : Array.Empty<string>();
                modelId.Text = p.DefaultModel ?? "";
            }
            InputHint.SetText(modelId, p.DefaultModel ?? "gemma4:e4b");
            keyStatus.Text = model.KeyStatus(p, Url());
            InputHint.SetText(key, p.BaseUrl == ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl ? "Paste your NVIDIA Build key (nvapi-…)"
                : p.BaseUrl == ChatCompletionsEndpointCatalog.OpenRouterBaseUrl ? "Paste your OpenRouter key (sk-or-v1-…)"
                : p == MainWindow.CustomCloud ? "Paste the server's key, if it needs one"
                : $"Paste your {p.Name} API key");
            consentText.Text = ThinkingPoolPageModel.CloudConsent(p);
            consent.IsChecked = false;
        }
        Refresh(keepModel: false);
        provider.SelectionChanged += (_, _) => { services.MarkEdited(); Refresh(keepModel: false); };
        baseUrl.TextChanged += (_, _) => { if (!baseUrl.IsKeyboardFocusWithin) return; services.MarkEdited(); consent.IsChecked = false; };
        modelId.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) =>
        {
            if (!modelId.IsKeyboardFocusWithin) return;
            services.MarkEdited();
            consent.IsChecked = false;
        }));
        key.PasswordChanged += (_, _) => services.MarkEdited();
        SecretLease? TakeTypedKey()
        {
            using var entered = key.SecurePassword;
            return entered.Length > 0 ? TakeKey(key) : null;
        }
        return [
            Note("Choose the provider, paste your API key and choose the model. A strong reasoning model (for example on OpenRouter) " +
                "thinks while your Thinking model keeps talking: they never wait for each other.", new Thickness(0, 0, 0, 8)),
            new Label { Content = "_Provider", Target = provider, Padding = new Thickness(0, 0, 0, 4) }, provider, baseUrlPanel,
            new Label { Content = "API _key", Target = key, Padding = new Thickness(0, 8, 0, 4) }, key, keyStatus,
            new Label { Content = "_Model", Target = modelId, Padding = new Thickness(0, 8, 0, 4) }, modelId, consent,
            Row(PageButton("Add", () => model.SaveDeepCloudAsync(Selected(), Url(), modelId.Text.Trim(), TakeTypedKey,
                consent.IsChecked == true, model.Pool.All, model.Route).Forget(), primary: true, id: "DeepThinkingSaveCloud"))];
    }
}
