using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>Companion › Thinking › This PC: which app runs the model. Ollama is the one Martlet sets up and manages; any other
/// model app with an OpenAI-compatible server on this PC (LM Studio, llama.cpp's server, KoboldCpp, Jan, vLLM, Lemonade, GPT4All,
/// Docker Model Runner and others) is found on its loopback port, its models are listed, and one is tested and used through the
/// same Chat Completions route. Looking only asks 127.0.0.1; nothing is installed or started.</summary>
public partial class MainWindow
{
    /// <summary>The app that runs Thinking's model on this PC.</summary>
    internal enum LocalApp { Ollama, Other }

    private const string AnotherAddress = "Another address on this PC";

    private LocalApp? localApp;
    private IReadOnlyList<LocalModelServer>? localServers;
    private bool lookingForLocalServers;
    /// <summary>The model app picked in the list (its base URL), or <see cref="AnotherAddress"/>; kept while the page shows again.</summary>
    private string? localServerPicked;
    private string? localServerAddress;
    /// <summary>What the address typed under Another address said when asked for its models.</summary>
    private (string BaseUrl, LocalServerAnswer Answer)? localServerAsked;
    private LocalServerTestOutcome? localServerTest;
    private Action? showLocalServerTest;
    private bool testingLocalServer;

    /// <summary>The last test of a model in a model app on this PC: where, which model, what it found and whether it worked.</summary>
    private sealed record LocalServerTestOutcome(string BaseUrl, string Model, string Text, bool Passed, bool Warning);

    /// <summary>Thinking uses a model app on this PC other than Martlet's Ollama: a Chat Completions route to a loopback address.</summary>
    internal static bool IsLocalServer(SetupRoute? route) =>
        route?.RouteType == SetupRouteType.ChatCompletions && !IsLocalOllama(route) && LocalModelServers.IsOnThisComputer(route.Origin);

    /// <summary>The app chosen on This PC when the owner hasn't picked one this time: the one Thinking uses; otherwise another app
    /// already running here when Ollama isn't installed; otherwise Ollama.</summary>
    internal static LocalApp DefaultLocalApp(SetupRoute? route, bool ollamaInstalled, int otherAppsFound) =>
        IsLocalOllama(route) ? LocalApp.Ollama
        : IsLocalServer(route) ? LocalApp.Other
        : !ollamaInstalled && otherAppsFound > 0 ? LocalApp.Other
        : LocalApp.Ollama;

    /// <summary>The model apps found on this PC other than Ollama (which has its own choice).</summary>
    private IReadOnlyList<LocalModelServer> OtherLocalServers() =>
        [.. (localServers ?? []).Where(s => s.Id != "ollama" && s.ChatCompletionsBaseUrl != LocalOllamaBaseUrl)];

    /// <summary>The name of the model app at a base URL on this PC, for a sentence: what looking found there when it knows the
    /// app, else what its port says ("the model app on port 8080").</summary>
    internal string LocalServerName(string baseUrl) =>
        (localServers ?? []).FirstOrDefault(s => s.ChatCompletionsBaseUrl == baseUrl && s.HowToStart is not null)?.Name ??
        LocalModelServers.Name(baseUrl);

    /// <summary>A found app in the list: "LM Studio · http://127.0.0.1:1234/v1 · 3 models".</summary>
    internal static string LocalServerItem(LocalModelServer server) =>
        $"{server.Name} · {server.ChatCompletionsBaseUrl} · " +
        (server.NeedsKey ? "asks for a key" : server.Models.Count == 1 ? "1 model" : $"{server.Models.Count} models");

    /// <summary>What looking found, in words, for the status line.</summary>
    internal static string LocalServersSummary(IReadOnlyList<LocalModelServer>? found, bool looking, bool ollamaRunning)
    {
        if (looking && found is null) return "Looking for model apps on this PC...";
        if (found is null) return "Martlet hasn't looked for model apps on this PC yet.";
        var others = found.Where(s => s.Id != "ollama").ToList();
        var ollama = ollamaRunning || found.Any(s => s.Id == "ollama") ? " Ollama is running too; choose Ollama above to use it." : "";
        if (others.Count == 0)
            return "No other model app answers on this PC. Start your app's local server, then choose Look again, or enter its address." + ollama;
        return "Found on this PC: " + string.Join("; ", others.Select(s => $"{s.Name} at {s.ChatCompletionsBaseUrl} (" +
            (s.NeedsKey ? "asks for a key" : s.Models.Count == 1 ? "1 model" : $"{s.Models.Count} models") + ")")) + "." + ollama;
    }

    /// <summary>Looks for model apps on this PC (loopback only, about 1.5 s), then shows the page again. <paramref name="quiet"/>:
    /// the page's own look when it opens, which leaves the status line and fields being typed in alone.</summary>
    private async Task LookForLocalServersAsync(bool quiet = false)
    {
        if (lookingForLocalServers) return;
        lookingForLocalServers = true;
        if (!quiet) ActionText.Text = "Looking for model apps on this PC...";
        if (!quiet && openTab == CompanionTab.Thinking) RenderTab();
        try
        {
            localServers = await LocalModelServers.DetectAsync(cancellationToken: lifetime.Token);
            var others = OtherLocalServers();
            if (!quiet)
                ActionText.Text = others.Count == 0 ? "No other model app answers on this PC."
                    : $"Found {string.Join(", ", others.Select(s => s.Name))} on this PC.";
        }
        catch (OperationCanceledException) { return; }
        finally { lookingForLocalServers = false; }
        if (!closing && openTab == CompanionTab.Thinking && !(quiet && tabEdited)) RenderTab();
    }

    /// <summary>The model app found on this PC that the Model app picker shows for <see cref="LocalApp.Other"/>: the one picked,
    /// else the one Thinking uses, else the first found; null for an app typed by its address.</summary>
    private LocalModelServer? PickedLocalServer(SetupRoute? route)
    {
        var found = OtherLocalServers();
        var wanted = localServerPicked ?? (IsLocalServer(route) ? route!.Origin : null);
        if (wanted == AnotherAddress) return null;
        return found.FirstOrDefault(s => s.ChatCompletionsBaseUrl == wanted) ?? (wanted is null ? found.FirstOrDefault() : null);
    }

    /// <summary>Which app runs the model on this PC, as an option picker (<c>Picker-LocalApp-&lt;key&gt;</c>): Ollama, each other
    /// model app found here, and an app typed by its address. Choosing one shows its card below.</summary>
    private Border LocalAppCard(SetupRoute? route, LocalApp chosen)
    {
        var others = OtherLocalServers();
        var options = JobOptions.LocalApps(others, lookingForLocalServers && localServers is null, !Prerequisites.IsMissing(Prerequisites.Ollama),
            IsLocalOllama(route), IsLocalServer(route) ? route!.Origin : null);
        var shown = chosen == LocalApp.Ollama ? JobOptions.OllamaApp
            : PickedLocalServer(route) is { } server ? JobOptions.AppKey(server) : JobOptions.AddressApp;
        // The chosen app's own card below is its details.
        return OptionPicker("LocalApp", "Model app", null, options, shown, key =>
        {
            localApp = key == JobOptions.OllamaApp ? LocalApp.Ollama : LocalApp.Other;
            if (key == JobOptions.AddressApp) localServerPicked = AnotherAddress;
            else if (others.FirstOrDefault(s => JobOptions.AppKey(s) == key) is { } picked) localServerPicked = picked.ChatCompletionsBaseUrl;
        }, details: false);
    }
    /// <summary>A model app the owner already runs on this PC: pick a found one (or type its address), pick its model, test it,
    /// use it. Only a key the app asks for is entered, and it is kept in Windows Credential Manager like any route's key.</summary>
    private Border LocalServerCard(SetupRoute? route)
    {
        var current = IsLocalServer(route) ? route : null;
        var found = OtherLocalServers();

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), FontWeight = FontWeights.SemiBold,
            Text = LocalServersSummary(localServers, lookingForLocalServers, ollamaModels is not null) +
                (current is null ? "" : $" Thinking uses {current.ModelId} in {LocalServerName(current.Origin)}.") };
        AutomationProperties.SetAutomationId(status, "LocalServersStatus");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);

        var picked = PickedLocalServer(route);
        var address = new TextBox { MaxLength = 256, Width = 420, HorizontalAlignment = HorizontalAlignment.Left,
            Text = localServerAddress ?? (current is not null && found.All(s => s.ChatCompletionsBaseUrl != current.Origin) ? current.Origin : null) ?? "" };
        AutomationProperties.SetName(address, "Address on this PC");
        AutomationProperties.SetAutomationId(address, "LocalServerAddress");
        var addressPanel = new StackPanel { Children =
        {
            new Label { Content = "_Address on this PC", Target = address, Padding = new Thickness(0, 8, 0, 4) },
            address,
            Note("The address your app shows for its server, for example localhost:1234 or http://127.0.0.1:8080/v1.", new Thickness(0, 4, 0, 0))
        } };

        var model = new ComboBox { IsEditable = true, MinHeight = 30, MaxWidth = 420, MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(model, "Model");
        AutomationProperties.SetAutomationId(model, "LocalServerModel");
        var models = Note("", new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(models, "LocalServerModels");

        var key = new PasswordBox { MaxLength = SecretLease.MaximumLength, Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(key, "API key, only if the app asks for one");
        AutomationProperties.SetAutomationId(key, "LocalServerKey");
        var keyStatus = Note("", new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(keyStatus, "LocalServerKeyStatus");
        var hint = Note("", new Thickness(0, 6, 0, 0));
        AutomationProperties.SetAutomationId(hint, "LocalServerHint");

        LocalModelServer? Picked() => picked;
        string? BaseUrl(out string problem)
        {
            problem = "";
            return Picked()?.ChatCompletionsBaseUrl ?? LocalModelServers.Normalize(address.Text, out problem);
        }
        string ModelId() => (model.Text ?? "").Trim();
        bool HasKey(string? baseUrl) => baseUrl is not null && (current?.Origin == baseUrl && current.CredentialId is not null ||
            SetupSettings.SetAsideCredentials(homeSettings, SetupRole.Llm, baseUrl).Count > 0);

        var tested = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        AutomationProperties.SetAutomationId(tested, "LocalServerTestResult");
        AutomationProperties.SetLiveSetting(tested, AutomationLiveSetting.Polite);
        void ShowTest()
        {
            var baseUrl = BaseUrl(out _);
            var last = localServerTest is { } outcome && outcome.Model == ModelId() && outcome.BaseUrl == baseUrl ? outcome : null;
            tested.Text = last?.Text ?? (ModelId().Length == 0 || baseUrl is null ? "" : $"{ModelId()} isn't tested yet. Test it before using it.");
            tested.SetResourceReference(TextBlock.ForegroundProperty, last is null ? "MutedBrush" : last.Passed && !last.Warning ? "SuccessBrush"
                : "WarningBrush");
        }
        showLocalServerTest = ShowTest;

        var use = PageButton("Use this app", () => SaveLocalServerAsync(BaseUrl(out var problem), problem, ModelId(), key).Forget(),
            primary: true, id: "LocalServerUse");
        var find = PageButton("Find models", () => AskLocalServerAsync(address.Text, key).Forget(), id: "LocalServerFind");

        void Refresh(bool keepModel)
        {
            var server = Picked();
            addressPanel.Visibility = server is null ? Visibility.Visible : Visibility.Collapsed;
            find.Visibility = server is null ? Visibility.Visible : Visibility.Collapsed;
            var baseUrl = BaseUrl(out _);
            var asked = server is null && localServerAsked is { } a && a.BaseUrl == baseUrl ? a.Answer : null;
            IReadOnlyList<string> list = server?.Models ?? asked?.Models ?? [];
            IReadOnlyList<string> unusable = server?.Unusable ?? asked?.Unusable ?? [];
            if (!keepModel)
            {
                var keep = ModelId();
                model.ItemsSource = list;
                model.Text = current?.Origin == baseUrl && current is not null ? current.ModelId
                    : list.Contains(keep, StringComparer.Ordinal) ? keep : list.FirstOrDefault() ?? keep;
            }
            models.Text = ((server?.NeedsKey == true || asked?.Kind == LocalServerAnswerKind.NeedsKey
                    ? "It asks for an API key before it lists its models. Enter the key you set in the app, then choose Find models or Test model. "
                    : asked is { Kind: not LocalServerAnswerKind.Models } ? asked.Problem + " "
                    : list.Count == 0 && (server is not null || asked is not null) ? "It lists no models yet. Load one in the app, or type its name. "
                    : list.Count > 0 ? $"It lists {list.Count} {(list.Count == 1 ? "model" : "models")}. " : "") +
                (unusable.Count > 0 ? $"Martlet can't use {string.Join(", ", unusable.Take(3))}: give it a name of letters, digits, dots, " +
                    "dashes, underscores, slashes or colons in the app. " : "")).Trim();
            var saved = HasKey(baseUrl);
            keyStatus.Text = saved ? "Your key for this app is saved. Leave this empty to keep it, or enter a new key."
                : "Leave this empty unless the app asks for a key. Martlet keeps a key in Windows Credential Manager.";
            var name = baseUrl is null ? "the app" : LocalServerName(baseUrl);
            // An app that already answers needs no start steps; one typed by address may not be running yet.
            var start = server is not null ? "" : (baseUrl is null ? null : LocalModelServers.AppAt(baseUrl)?.HowToStart) ??
                "Start your app's local server (it may be called API server, OpenAI-compatible server or Developer server), then " +
                "choose Look again or enter its address.";
            hint.Text = (start + $" Your messages go to {name} on this PC. That app decides what it keeps, and whether anything " +
                "leaves this PC.").Trim();
            use.Content = baseUrl is null ? "Use this app" : $"Use {LocalServerName(baseUrl)}";
            AutomationProperties.SetName(use, (string)use.Content);
            ShowTest();
        }
        Refresh(keepModel: false);
        address.TextChanged += (_, _) =>
        {
            tabEdited = true;
            localServerAddress = address.Text;
            Refresh(keepModel: true);
        };
        model.SelectionChanged += (_, _) => { tabEdited = true; Dispatcher.BeginInvoke(new Action(ShowTest)); };
        model.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => ShowTest()));
        key.PasswordChanged += (_, _) => tabEdited = true;

        return Card(Heading("Your model app on this PC"),
            Note("Martlet thinks with a model your app serves on this PC, with no per-request cost. Start the app's local server, " +
                "then choose it here.", new Thickness(0, 0, 0, 8)),
            status,
            addressPanel,
            new Label { Content = "M_odel", Target = model, Padding = new Thickness(0, 8, 0, 4) },
            model,
            models,
            new Label { Content = "API _key (only if the app asks for one)", Target = key, Padding = new Thickness(0, 8, 0, 4) },
            key,
            keyStatus,
            hint,
            Row(PageButton("Look again", () => LookForLocalServersAsync().Forget(), id: "LocalServersScan"),
                find,
                PageButton("Test model", () => TestLocalServerAsync(BaseUrl(out var problem), problem, ModelId(), key).Forget(), id: "LocalServerTest"),
                use),
            tested);
    }

    /// <summary>The key typed in <paramref name="box"/>, read for one request to the app on this PC, else the saved key for that
    /// address (Thinking's own, when it uses it). The box keeps what was typed, so Use can still save it.</summary>
    private string? LocalServerKey(PasswordBox box, string baseUrl)
    {
        using (var secure = box.SecurePassword)
            if (secure.Length > 0)
            {
                var typed = box.Password.Trim();
                // The same check saving it makes, so a key that can't be saved isn't sent either.
                using (new SecretLease(typed.AsSpan())) return typed;
            }
        var route = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        return route is not null && route.Origin == baseUrl ? RouteKey(route) : null;
    }

    /// <summary>Find models: asks the address typed under Another address which models it has (loopback only).</summary>
    private async Task AskLocalServerAsync(string typed, PasswordBox keyBox)
    {
        var baseUrl = LocalModelServers.Normalize(typed, out var problem);
        if (baseUrl is null)
        {
            ActionText.Text = problem;
            return;
        }
        ActionText.Text = $"Asking {baseUrl} for its models...";
        string? key;
        try { key = LocalServerKey(keyBox, baseUrl); }
        catch (ContractException error) { ActionText.Text = error.Message; return; }
        var answer = await LocalModelServers.AskAsync(baseUrl, key, cancellationToken: lifetime.Token);
        if (closing) return;
        localServerAsked = (baseUrl, answer);
        ActionText.Text = answer.Kind == LocalServerAnswerKind.Models
            ? $"{Capitalized(LocalServerName(baseUrl))} at {baseUrl} lists {answer.Models.Count} {(answer.Models.Count == 1 ? "model" : "models")}."
            : $"{baseUrl}: {answer.Problem}";
        tabEdited = false;
        if (openTab == CompanionTab.Thinking) RenderTab();
    }

    /// <summary>Test model: asks the model for a short streamed reply the way replies do (in a run window), so a model or app
    /// that won't work shows up now rather than on the first message. Loopback only.</summary>
    private async Task TestLocalServerAsync(string? baseUrl, string problem, string model, PasswordBox keyBox)
    {
        if (baseUrl is null) { ActionText.Text = problem; return; }
        try { ChatCompletionsSetup.ModelId(model); }
        catch (ContractException error) { ActionText.Text = error.Message; return; }
        if (testingLocalServer)
        {
            ActionText.Text = "A model test is already running.";
            return;
        }
        string? key;
        try { key = LocalServerKey(keyBox, baseUrl); }
        catch (ContractException error) { ActionText.Text = error.Message; return; }
        testingLocalServer = true;
        var name = LocalServerName(baseUrl);
        ActionText.Text = $"Testing {model} in {name}...";
        var generation = homeSettings?.Generation;
        LocalServerTestResult? result = null;
        string? failure = null;
        try
        {
            await HostRunWindow.RunAsync(this, $"Test {model}", async run =>
            {
                try
                {
                    result = await LocalModelServers.TestAsync(baseUrl, model, key,
                        GenerationSupport.ReplyTokens(SetupRouteType.ChatCompletions, generation), GenerationSupport.ChatReasoning(baseUrl),
                        GenerationSettings.ThinkingSteps(generation), LiveConversationConfiguration.LocalOllamaTextLimits.FirstDeltaTimeout,
                        run.Output, cancellationToken: run.Token);
                    return result.Summary;
                }
                catch (InvalidOperationException error)
                {
                    failure = error.Message;
                    throw;
                }
            });
        }
        finally { testingLocalServer = false; }
        if (closing) return;
        if (result is not null)
        {
            // The app's model list (and LM Studio's or llama.cpp's own API) says how much context the model takes and whether it sees.
            string context = "";
            try
            {
                using var client = ModelContextProbe.CreateClient(loopback: true);
                var report = await ModelContextProbe.ChatCompletionsAsync(client, baseUrl, model, key, name, lifetime.Token);
                if (report.Reached) RecordModelLimit(baseUrl, model, report);
                if (report.ContextTokens is not null) context = " " + report.Summary;
            }
            catch (OperationCanceledException) { return; }
            if (closing) return;
            localServerTest = new(baseUrl, model, result.Summary + context, true, result.Warning);
            var inUse = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm) is { } route &&
                route.Origin == baseUrl && route.ModelId == model;
            ActionText.Text = localServerTest.Text + (inUse ? "" : $" Choose Use {name} to use it.");
            showContextStatus?.Invoke();
        }
        else if (failure is not null)
        {
            localServerTest = new(baseUrl, model, $"Test failed: {failure}", false, false);
            ActionText.Text = localServerTest.Text;
        }
        else ActionText.Text = $"Testing {model} stopped before it finished.";
        showLocalServerTest?.Invoke();
    }

    /// <summary>Use: checks that the app answers and has the model (asking once when it doesn't list it), then switches Thinking
    /// to it, with the key when one was typed. The current Thinking keeps answering until then. Ollama's own address goes through
    /// Ollama's choice, which gets the model ready first.</summary>
    private async Task SaveLocalServerAsync(string? baseUrl, string problem, string model, PasswordBox keyBox)
    {
        if (baseUrl is null) { ActionText.Text = problem; return; }
        try { ChatCompletionsSetup.ModelId(model); }
        catch (ContractException error) { ActionText.Text = error.Message; return; }
        if (baseUrl == LocalOllamaBaseUrl)
        {
            localApp = LocalApp.Ollama;
            await SaveLocalThinkingAsync(model);
            return;
        }
        var name = LocalServerName(baseUrl);
        string? key;
        try { key = LocalServerKey(keyBox, baseUrl); }
        catch (ContractException error) { ActionText.Text = error.Message; return; }
        ActionText.Text = $"Checking {name} at {baseUrl}...";
        var answer = await LocalModelServers.AskAsync(baseUrl, key, cancellationToken: lifetime.Token);
        if (closing) return;
        if (answer.Kind != LocalServerAnswerKind.Models)
        {
            ActionText.Text = $"{Capitalized(name)} at {baseUrl}: {answer.Problem} Thinking didn't change.";
            return;
        }
        if (answer.Models.Count > 0 && !answer.Models.Contains(model, StringComparer.Ordinal) &&
            !ConfirmationDialog.Confirm(this, $"{Capitalized(name)} doesn't list {model}. It lists {string.Join(", ", answer.Models.Take(6))}" +
                $"{(answer.Models.Count > 6 ? " and more" : "")}. Some apps load a model only when asked. Switch Thinking to {model} anyway?",
                "Use a model the app doesn't list", "Switch anyway", questionId: "LocalServerModelQuestion"))
        {
            ActionText.Text = "Thinking didn't change.";
            return;
        }
        SecretLease? lease = null;
        try
        {
            using (var typed = keyBox.SecurePassword)
                if (typed.Length > 0) lease = TakeKey(keyBox);
            localServerPicked = baseUrl;
            if (!await SaveSectionRouteAsync(HostJob.Thinking, settings => ChatCompletionsSetup.SelectRoute(settings, baseUrl, model), lease,
                    $"Martlet now thinks with {model} in {name} on this PC.{(lease is null ? "" : " The key is saved in Windows Credential Manager.")}"))
                return;
            localApp = null;
            if (!closing) CheckNewModelContextAsync().Forget();
        }
        catch (ContractException error) { ActionText.Text = error.Message; }
        finally { lease?.Dispose(); }
    }
}
