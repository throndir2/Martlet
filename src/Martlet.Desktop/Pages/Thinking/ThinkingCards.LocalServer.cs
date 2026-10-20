using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Presentation;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>Companion › Thinking › This PC › Your model app on this PC (the looking, asking, testing and saving stay in
/// MainWindow.LocalServers.cs).</summary>
internal sealed partial class ThinkingCards
{
    /// <summary>The address typed under Another address, kept while the page shows again.</summary>
    private string? localServerAddress;

    /// <summary>A model app the owner already runs on this PC: pick a found one (or type its address), pick its model, test it,
    /// use it. Only a key the app asks for is entered, and it is kept in Windows Credential Manager like any route's key.</summary>
    internal Border LocalServer(SetupRoute? route)
    {
        var current = MainWindow.IsLocalServer(route) ? route : null;
        var found = services.OtherLocalServers();
        var summary = MainWindow.LocalServersSummary(services.LocalServers, services.LookingForLocalServers, services.OllamaModels is not null);

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), FontWeight = FontWeights.SemiBold,
            Text = current is null ? summary
                : $"Thinking uses {current.ModelId} in {services.LocalServerName(current.Origin)}; " + MainWindow.Lowered(summary) };
        AutomationProperties.SetAutomationId(status, "LocalServersStatus");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);

        var picked = PickedLocalServer(route);
        var address = new TextBox { MaxLength = 256, Width = 420, HorizontalAlignment = HorizontalAlignment.Left,
            Text = localServerAddress ?? (current is not null && found.All(s => s.ChatCompletionsBaseUrl != current.Origin) ? current.Origin : null) ?? "" };
        AutomationProperties.SetName(address, "Address on this PC");
        AutomationProperties.SetAutomationId(address, "LocalServerAddress");
        InputHint.SetText(address, "http://127.0.0.1:8080/v1");
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
        InputHint.SetText(key, "The key you set in the app, if it asks for one");
        var keyStatus = Note("", new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(keyStatus, "LocalServerKeyStatus");
        var hint = Note("", new Thickness(0, 6, 0, 0));
        AutomationProperties.SetAutomationId(hint, "LocalServerHint");
        var privacy = Note("", new Thickness(0, 6, 0, 0));
        AutomationProperties.SetAutomationId(privacy, "LocalServerPrivacy");

        LocalModelServer? Picked() => picked;
        string? BaseUrl(out string problem)
        {
            problem = "";
            return Picked()?.ChatCompletionsBaseUrl ?? LocalModelServers.Normalize(address.Text, out problem);
        }
        string ModelId() => (model.Text ?? "").Trim();
        bool HasKey(string? baseUrl) => baseUrl is not null && (current?.Origin == baseUrl && current.CredentialId is not null ||
            SetupSettings.SetAsideCredentials(services.Settings, SetupRole.Llm, baseUrl).Count > 0);

        var tested = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        AutomationProperties.SetAutomationId(tested, "LocalServerTestResult");
        AutomationProperties.SetLiveSetting(tested, AutomationLiveSetting.Polite);
        void ShowTest()
        {
            var baseUrl = BaseUrl(out _);
            var last = services.LocalServerTest is { } outcome && outcome.Model == ModelId() && outcome.BaseUrl == baseUrl ? outcome : null;
            tested.Text = last?.Text ?? (ModelId().Length == 0 || baseUrl is null ? "" : $"{ModelId()} isn't tested yet, so test it before you use it.");
            tested.SetResourceReference(TextBlock.ForegroundProperty, last is null ? "MutedBrush" : last.Passed && !last.Warning ? "SuccessBrush"
                : "WarningBrush");
        }
        services.ShowLocalServerTest = ShowTest;

        var use = PageButton("Use this app", () => services.SaveLocalServer(BaseUrl(out var problem), problem, ModelId(), key),
            primary: true, id: "LocalServerUse");
        var find = PageButton("Find models", () => services.AskLocalServer(address.Text, key), id: "LocalServerFind");

        void Refresh(bool keepModel)
        {
            var server = Picked();
            addressPanel.Visibility = server is null ? Visibility.Visible : Visibility.Collapsed;
            find.Visibility = server is null ? Visibility.Visible : Visibility.Collapsed;
            var baseUrl = BaseUrl(out _);
            var asked = server is null && services.LocalServerAsked is { } a && a.BaseUrl == baseUrl ? a.Answer : null;
            IReadOnlyList<string> list = server?.Models ?? asked?.Models ?? [];
            IReadOnlyList<string> unusable = server?.Unusable ?? asked?.Unusable ?? [];
            if (!keepModel)
            {
                var keep = ModelId();
                model.ItemsSource = list;
                model.Text = current?.Origin == baseUrl && current is not null ? current.ModelId
                    : list.Contains(keep, StringComparer.Ordinal) ? keep : list.FirstOrDefault() ?? keep;
            }
            var listed = server?.NeedsKey == true || asked?.Kind == LocalServerAnswerKind.NeedsKey
                    ? "It asks for an API key before it lists its models: enter the key you set in the app, then choose Find models or Test model"
                    : asked is { Kind: not LocalServerAnswerKind.Models } ? (asked.Problem ?? "").TrimEnd('.')
                    : list.Count == 0 && (server is not null || asked is not null) ? "It lists no models yet, so load one in the app or type its name"
                    : list.Count > 0 ? $"It lists {list.Count} {(list.Count == 1 ? "model" : "models")}" : "";
            var cannot = unusable.Count > 0 ? $"Martlet can't use {string.Join(", ", unusable.Take(3))}: give it a name of letters, digits, " +
                "dots, dashes, underscores, slashes or colons in the app" : "";
            models.Text = listed.Length > 0 && cannot.Length > 0 ? $"{listed}; {MainWindow.Lowered(cannot)}." : listed.Length + cannot.Length > 0 ? listed + cannot + "." : "";
            var saved = HasKey(baseUrl);
            keyStatus.Text = saved ? "Your key for this app is saved; leave this empty to keep it or enter a new key."
                : "Leave this empty unless the app asks for a key (Martlet keeps a key in Windows Credential Manager).";
            var name = baseUrl is null ? "the app" : services.LocalServerName(baseUrl);
            // An app that already answers needs no start steps; one typed by address may not be running yet.
            var start = server is not null ? "" : (baseUrl is null ? null : LocalModelServers.AppAt(baseUrl)?.HowToStart) ??
                "Start your app's local server (it may be called API server, OpenAI-compatible server or Developer server), then " +
                "choose Look again or enter its address.";
            hint.Text = start;
            hint.Visibility = start.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            privacy.Text = $"Your messages go to {name} on this PC, and that app decides what it keeps and whether anything leaves this PC.";
            use.Content = baseUrl is null ? "Use this app" : $"Use {services.LocalServerName(baseUrl)}";
            AutomationProperties.SetName(use, (string)use.Content);
            ShowTest();
        }
        Refresh(keepModel: false);
        address.TextChanged += (_, _) =>
        {
            services.MarkEdited();
            localServerAddress = address.Text;
            Refresh(keepModel: true);
        };
        model.SelectionChanged += (_, _) => { services.MarkEdited(); model.Dispatcher.BeginInvoke(new Action(ShowTest)); };
        model.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => ShowTest()));
        key.PasswordChanged += (_, _) => services.MarkEdited();

        return Card(Heading("Your model app on this PC"),
            HelpTip.Explain("Martlet thinks with a model your app serves on this PC, with no per-request cost. Start the app's local " +
                "server, then choose it here.", new Thickness(0, 0, 0, 8), "LocalServerAbout", "your model app",
                wiki: "Thinking#use-a-model-app-you-already-run"),
            status,
            addressPanel,
            new Label { Content = "M_odel", Target = model, Padding = new Thickness(0, 8, 0, 4) },
            model,
            models,
            new Label { Content = "API _key (only if the app asks for one)", Target = key, Padding = new Thickness(0, 8, 0, 4) },
            key,
            keyStatus,
            hint,
            privacy,
            Row(PageButton("Look again", services.LookForLocalServers, id: "LocalServersScan"),
                find,
                PageButton("Test model", () => services.TestLocalServer(BaseUrl(out var problem), problem, ModelId(), key), id: "LocalServerTest"),
                use),
            tested);
    }
}
