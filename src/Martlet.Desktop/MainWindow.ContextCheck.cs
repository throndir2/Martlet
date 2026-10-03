using Martlet.Core.Settings;
using Martlet.Credentials.Windows;
using Martlet.Logging;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>Finding out how much context the Thinking model takes (Companion › Replies › Check model limit, and right after a
/// model is chosen or tested): the server's model list, Ollama's API on this PC, or OpenAI's documented windows. What it finds
/// is kept in model-limits.json, so the context size stays within the model's own from the next conversation on.</summary>
public partial class MainWindow
{
    private bool checkingContext;
    // Shows the Replies page's context line again after a check.
    private Action? showContextStatus;

    internal static Uri LocalOllamaOrigin => LocalOllama.OriginUri;

    private ModelLimits SavedModelLimits() => ModelLimits.Load(store?.DataDirectory);

    /// <summary>Whether Martlet can ask the route's server about its model: Chat Completions servers and Ollama on this PC. OpenAI's
    /// windows are known; a paired host's Ollama loads the size Martlet sends.</summary>
    internal static bool CanCheckContext(SetupRoute? route) => route?.RouteType == SetupRouteType.ChatCompletions;

    /// <summary>Asks the route's server how much context its model takes and keeps what it says. <paramref name="load"/> lets
    /// Ollama on this PC load a model that isn't loaded, so the context it gives the model is known exactly. Null when the route
    /// can't be asked.</summary>
    private async Task<ModelContextReport?> CheckModelContextAsync(SetupRoute route, bool load, CancellationToken token)
    {
        if (!CanCheckContext(route)) return null;
        ModelContextReport report;
        if (IsLocalOllama(route))
        {
            using var client = ModelContextProbe.CreateClient(loopback: true);
            report = await ModelContextProbe.OllamaAsync(client, LocalOllamaOrigin, route.ModelId, load, token);
        }
        else
        {
            var baseUri = ChatCompletionsSetup.BaseUri(route.Origin);
            var key = await Task.Run(() => RouteKey(route), token);
            using var client = ModelContextProbe.CreateClient(ModelContextProbe.IsLoopback(baseUri));
            report = await ModelContextProbe.ChatCompletionsAsync(client, route.Origin, route.ModelId, key, ContextServerName(route), token);
        }
        if (report.Reached) RecordModelLimit(route.Origin, route.ModelId, report);
        return report;
    }

    private void RecordModelLimit(string origin, string modelId, ModelContextReport report)
    {
        var directory = store?.DataDirectory;
        if (directory is null) return;
        var limits = ModelLimits.Load(directory);
        var before = limits.Find(origin, modelId);
        if (!limits.With(new()
            {
                Origin = origin, ModelId = modelId, ContextTokens = report.ContextTokens, ModelMaximum = report.ModelMaximum,
                Source = report.Source.Length <= 200 ? report.Source : report.Source[..200], CheckedAt = DateTimeOffset.UtcNow
            }).Save(directory))
            ErrorLog.Warn("Martlet couldn't save what it found about the Thinking model's context (model-limits.json).");
        else if (before?.ContextTokens != report.ContextTokens)
            ErrorLog.Info($"Context of {modelId}: {(report.ContextTokens is { } tokens ? $"{tokens:N0} tokens" : "not reported")} ({report.Source}).");
    }

    /// <summary>The route's saved key, for its own base URL only; null when it has none or it can't be read.</summary>
    private string? RouteKey(SetupRoute route)
    {
        if (route.CredentialId is not { } id || homeSettings is null) return null;
        using var read = new WindowsCredentialStore().Read(CredentialBinding.For(homeSettings.Profile.Id, route, id));
        if (read.Error != CredentialError.None || read.Secret is null) return null;
        string? key = null;
        read.Secret.Use(secret => key = new string(secret));
        return key;
    }

    private static string ContextServerName(SetupRoute route) =>
        ChatCompletionsEndpointCatalog.Named(route.Origin)?.Name ??
        (Uri.TryCreate(route.Origin, UriKind.Absolute, out var uri) && ModelContextProbe.IsLoopback(uri) ? "the server on this PC"
            : Uri.TryCreate(route.Origin, UriKind.Absolute, out var other) ? other.Host : "the server");

    /// <summary>Companion › Replies' Check model limit: asks, says what it found and shows the context line again.</summary>
    private async Task CheckContextFromRepliesAsync()
    {
        var route = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        if (route is null || !CanCheckContext(route)) return;
        if (checkingContext)
        {
            ActionText.Text = "Martlet is already checking the model's context.";
            return;
        }
        checkingContext = true;
        ActionText.Text = IsLocalOllama(route)
            ? $"Asking Ollama on this PC about {route.ModelId}. It loads the model first if it isn't loaded..."
            : $"Asking {ContextServerName(route)} about {route.ModelId}...";
        try
        {
            var report = await CheckModelContextAsync(route, load: true, lifetime.Token);
            if (closing || report is null) return;
            ActionText.Text = report.Summary + (report.Reached && report.ContextTokens is not null ? " Reload an open conversation to use it." : "");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is InvalidOperationException or Martlet.Core.Contracts.ContractException)
        {
            ActionText.Text = "Couldn't check the model's context: " + error.Message;
        }
        finally
        {
            checkingContext = false;
            if (!closing) showContextStatus?.Invoke();
        }
    }

    /// <summary>After a Thinking model is chosen or tested: asks quietly in the background (never loading a model) and adds what it
    /// found to the status line when nothing newer replaced it.</summary>
    private async Task CheckNewModelContextAsync(bool load = false)
    {
        var route = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        if (route is null || !CanCheckContext(route) || checkingContext) return;
        checkingContext = true;
        var shown = ActionText.Text;
        try
        {
            var report = await CheckModelContextAsync(route, load, lifetime.Token);
            if (!closing && report is { Reached: true, ContextTokens: not null } && ActionText.Text == shown)
                ActionText.Text = (shown + " " + report.Summary).Trim();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is InvalidOperationException or Martlet.Core.Contracts.ContractException)
        {
            ErrorLog.Info("Couldn't check the Thinking model's context: " + error.Message);
        }
        finally
        {
            checkingContext = false;
            if (!closing) showContextStatus?.Invoke();
        }
    }

    /// <summary>The Replies page's context line: the size Martlet uses, where it comes from and what's known of the model's own.</summary>
    internal static string ContextStatus(SetupRoute route, GenerationSettings? saved, ModelLimits limits, string place)
    {
        var budget = ContextBudget.For(route, saved, limits);
        var found = limits.Find(route.Origin, route.ModelId);
        var model = route.ModelId;
        var uses = $"Martlet uses {budget.Describe()}";
        if (route.RouteType == SetupRouteType.GatewayOllama)
            return $"{uses}. {place} loads it, up to {GenerationSettings.MaximumHostContextTokens:N0}.";
        var most = found?.ModelMaximum is { } maximum ? $"; the model holds up to {maximum:N0}" : "";
        if (IsLocalOllama(route))
            return found?.ContextTokens is { } given
                ? $"{uses}. Ollama on this PC gives {model} {given:N0} tokens{most}. To give it more, raise Ollama's context length setting, then check again."
                : $"{uses}. Check model limit loads {model} and asks Ollama how much it gives it{most}.";
        var known = found?.ContextTokens ?? ModelContextCatalog.Catalog(route.RouteType, model);
        if (known is { } limit)
            return $"{uses}. {model} takes up to {limit:N0} tokens " +
                (found?.ContextTokens is not null ? $"({found.Source}, checked {found.CheckedAt.LocalDateTime:d})." : "(OpenAI's model page).");
        return found is not null
            ? $"{uses}. {Capitalized(found.Source)} doesn't say how much {model} takes (checked {found.CheckedAt.LocalDateTime:d}). If replies fail on long conversations, lower the context size."
            : CanCheckContext(route) ? $"{uses}. Martlet doesn't know {model}'s own limit yet. Check model limit asks {place}."
            : $"{uses}.";
    }
}
