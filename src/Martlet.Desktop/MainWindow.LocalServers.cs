using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Presentation;
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

    private LocalApp? localApp;
    private IReadOnlyList<LocalModelServer>? localServers;
    private bool lookingForLocalServers;
    /// <summary>The model app picked in the list (its base URL), or <see cref="ThinkingCards.AnotherAddress"/>; kept while the page shows again.</summary>
    private string? localServerPicked;
    /// <summary>What the address typed under Another address said when asked for its models.</summary>
    private (string BaseUrl, LocalServerAnswer Answer)? localServerAsked;
    private LocalServerTestOutcome? localServerTest;
    private Action? showLocalServerTest;
    private bool testingLocalServer;

    /// <summary>The last test of a model in a model app on this PC: where, which model, what it found and whether it worked.</summary>
    internal sealed record LocalServerTestOutcome(string BaseUrl, string Model, string Text, bool Passed, bool Warning);

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
        var ollama = ollamaRunning || found.Any(s => s.Id == "ollama") ? ", and Ollama is running too (choose Ollama above to use it)." : ".";
        if (others.Count == 0)
            return "No other model app answers on this PC, so start your app's local server, then choose Look again or enter its address" + ollama;
        return "Found on this PC: " + string.Join("; ", others.Select(s => $"{s.Name} at {s.ChatCompletionsBaseUrl} (" +
            (s.NeedsKey ? "asks for a key" : s.Models.Count == 1 ? "1 model" : $"{s.Models.Count} models") + ")")) + ollama;
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
        var route = homeSettings.ThinkingRoute();
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
            var inUse = homeSettings.ThinkingRoute() is { } route &&
                route.Origin == baseUrl && route.ModelId == model;
            ActionText.Text = inUse ? localServerTest.Text : localServerTest.Text.TrimEnd('.') + $"; choose Use {name} to use it.";
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
            ActionText.Text = $"{Capitalized(name)} at {baseUrl}: {(answer.Problem ?? "").TrimEnd('.')}, so Thinking didn't change.";
            return;
        }
        if (answer.Models.Count > 0 && !answer.Models.Contains(model, StringComparer.Ordinal) &&
            !ConfirmationDialog.Confirm(this, $"{Capitalized(name)} doesn't list {model} (it lists {string.Join(", ", answer.Models.Take(6))}" +
                $"{(answer.Models.Count > 6 ? " and more" : "")}), but some apps load a model only when asked: switch Thinking to {model} anyway?",
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
                    $"Martlet now thinks with {model} in {name} on this PC{(lease is null ? "" : ", and the key is saved in Windows Credential Manager")}."))
                return;
            localApp = null;
            ForgetRetired(baseUrl, model, "you chose it again");
            if (!closing) CheckNewModelContextAsync().Forget();
        }
        catch (ContractException error) { ActionText.Text = error.Message; }
        finally { lease?.Dispose(); }
    }
}
