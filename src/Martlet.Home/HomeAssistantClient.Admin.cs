using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Settings;

namespace Martlet.Home;

/// <summary>What Martlet shows about the Home Assistant itself on the Smart home page.</summary>
public sealed record HomeSystem(string LocationName, string Version, bool Supervised, IReadOnlyList<string> Integrations)
{
    /// <summary>Home Assistant OS (or Supervised) manages its own updates, backups and apps; a container does not.</summary>
    public string Installation => Supervised ? "Home Assistant OS" : "Home Assistant Container";
}

/// <summary>A device or service Home Assistant found on the network and is waiting to set up (an open discovery flow).</summary>
public sealed record HomeDiscovery(string FlowId, string Domain, string Name, string Title, string Source);

public enum HomeStepKind { Form, Done, Aborted, External, Progress, Menu }

/// <summary>Where a setup flow stands after Martlet moved it on. Fields lists the inputs a form still needs (Martlet only
/// finishes forms that need none and sends the rest to Home Assistant's own page).</summary>
public sealed record HomeFlowStep(HomeStepKind Kind, string FlowId, string? StepId, IReadOnlyList<string> Fields,
    IReadOnlyList<string> Errors, string? Reason, string? Title)
{
    public bool NeedsInput => Kind == HomeStepKind.Form && Fields.Count > 0;
}

public sealed record HomeUpdate(string EntityId, string Title, string? Installed, string? Latest, bool Installing);

public sealed record HomeBackups(int Count, DateTimeOffset? Latest);

public sealed partial class HomeAssistantClient
{
    /// <summary>Version, name, installation type and set-up integrations (<c>/api/config</c> and <c>config_entries/get</c>).</summary>
    public async Task<HomeSystem> SystemAsync(Uri baseUri, SecretLease token, CancellationToken cancellationToken)
    {
        string name, version;
        bool supervised;
        using (var document = await SendAsync(HttpMethod.Get, baseUri, "api/config", token, (byte[]?)null, MaximumSmallResponse,
            cancellationToken).ConfigureAwait(false))
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "version") is not { Length: > 0 } reported) throw NotHomeAssistant();
            version = Clean(reported, 32);
            name = Clean(Text(root, "location_name"), 64) is { Length: > 0 } n ? n : "Home";
            supervised = root.TryGetProperty("components", out var components) && components.ValueKind == JsonValueKind.Array &&
                components.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.String && c.GetString() == "hassio");
        }
        var integrations = new List<string>();
        await using (var socket = await HomeAssistantSocket.ConnectAsync(baseUri, token, cancellationToken).ConfigureAwait(false))
        {
            var entries = await socket.CommandAsync(new JsonObject { ["type"] = "config_entries/get" }, cancellationToken).ConfigureAwait(false);
            if (entries.ValueKind == JsonValueKind.Array)
                foreach (var entry in entries.EnumerateArray())
                    if (entry.ValueKind == JsonValueKind.Object && Text(entry, "source") is not "ignore" &&
                        Clean(Text(entry, "title"), 60) is { Length: > 0 } title)
                        integrations.Add(title);
        }
        return new(name, version, supervised, integrations.Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Order(StringComparer.CurrentCultureIgnoreCase).Take(64).ToArray());
    }

    /// <summary>Devices and services Home Assistant discovered and is waiting to set up, with each integration's name.</summary>
    public async Task<IReadOnlyList<HomeDiscovery>> DiscoveriesAsync(Uri baseUri, SecretLease token, CancellationToken cancellationToken)
    {
        await using var socket = await HomeAssistantSocket.ConnectAsync(baseUri, token, cancellationToken).ConfigureAwait(false);
        var flows = await socket.CommandAsync(new JsonObject { ["type"] = "config_entries/flow/progress" }, cancellationToken)
            .ConfigureAwait(false);
        var found = new List<(string Flow, string Domain, string Title, string Source)>();
        if (flows.ValueKind == JsonValueKind.Array)
            foreach (var flow in flows.EnumerateArray().Take(100))
            {
                if (flow.ValueKind != JsonValueKind.Object || Text(flow, "flow_id") is not { Length: > 0 and <= 64 } id ||
                    Text(flow, "handler") is not { Length: > 0 and <= 64 } domain)
                    continue;
                var context = flow.TryGetProperty("context", out var c) && c.ValueKind == JsonValueKind.Object ? c : default;
                var source = context.ValueKind == JsonValueKind.Object ? Clean(Text(context, "source"), 32) : "";
                var title = "";
                if (context.ValueKind == JsonValueKind.Object && context.TryGetProperty("title_placeholders", out var placeholders) &&
                    placeholders.ValueKind == JsonValueKind.Object)
                    title = Clean(Text(placeholders, "name"), 80) is { Length: > 0 } named ? named
                        : Clean(placeholders.EnumerateObject().Select(p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null)
                            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)), 80);
                found.Add((id, domain, title, source));
            }
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (found.Count > 0)
        {
            try
            {
                var domains = new JsonArray(found.Select(f => (JsonNode)f.Domain).Distinct().ToArray());
                var manifests = await socket.CommandAsync(new JsonObject { ["type"] = "manifest/list", ["integrations"] = domains },
                    cancellationToken).ConfigureAwait(false);
                if (manifests.ValueKind == JsonValueKind.Array)
                    foreach (var manifest in manifests.EnumerateArray())
                        if (manifest.ValueKind == JsonValueKind.Object && Text(manifest, "domain") is { } domain &&
                            Clean(Text(manifest, "name"), 60) is { Length: > 0 } name)
                            names[domain] = name;
            }
            // Names are a nicety; the domain still identifies the integration.
            catch (HomeAssistantException error) when (error.Failure == HomeAssistantFailure.BadResponse) { }
        }
        return found.Select(f => new HomeDiscovery(f.Flow, f.Domain, names.GetValueOrDefault(f.Domain) ?? Pretty(f.Domain), f.Title, f.Source))
            .ToArray();
    }

    /// <summary>Moves a discovered device's setup on: a confirmation step that needs no input is confirmed; a form that needs
    /// input (a code, a key, a choice) is left for Home Assistant's own page.</summary>
    public async Task<HomeFlowStep> ContinueFlowAsync(Uri baseUri, SecretLease token, string flowId, CancellationToken cancellationToken)
    {
        var path = "api/config/config_entries/flow/" + FlowPath(flowId);
        HomeFlowStep step;
        using (var current = await SendAsync(HttpMethod.Get, baseUri, path, token, (byte[]?)null, MaximumSmallResponse,
            cancellationToken).ConfigureAwait(false))
            step = ParseStep(current.RootElement, flowId);
        if (step.Kind != HomeStepKind.Form || step.NeedsInput) return step;
        using var next = await SendAsync(HttpMethod.Post, baseUri, path, token, "{}"u8.ToArray(), MaximumSmallResponse, cancellationToken,
            TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        return ParseStep(next.RootElement, flowId);
    }

    /// <summary>Starts setting up an integration by its domain with optional answers for its first form (for example a
    /// broker address). Returns where the flow stands.</summary>
    public async Task<HomeFlowStep> StartFlowAsync(Uri baseUri, SecretLease token, string domain, JsonObject? answers,
        CancellationToken cancellationToken)
    {
        if (domain is not { Length: > 0 and <= 64 } || !domain.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_'))
            throw new ArgumentException("Unknown integration.", nameof(domain));
        HomeFlowStep step;
        using (var started = await SendAsync(HttpMethod.Post, baseUri, "api/config/config_entries/flow", token,
            System.Text.Encoding.UTF8.GetBytes(new JsonObject { ["handler"] = domain }.ToJsonString()), MaximumSmallResponse,
            cancellationToken, TimeSpan.FromSeconds(60)).ConfigureAwait(false))
            step = ParseStep(started.RootElement, null);
        if (answers is null || step.Kind != HomeStepKind.Form) return step;
        using var next = await SendAsync(HttpMethod.Post, baseUri, "api/config/config_entries/flow/" + FlowPath(step.FlowId), token,
            System.Text.Encoding.UTF8.GetBytes(answers.ToJsonString()), MaximumSmallResponse, cancellationToken, TimeSpan.FromSeconds(60))
            .ConfigureAwait(false);
        return ParseStep(next.RootElement, step.FlowId);
    }

    /// <summary>Tells Home Assistant to stop offering a discovered device (it can be set up later from its Integrations page).</summary>
    public async Task IgnoreFlowAsync(Uri baseUri, SecretLease token, HomeDiscovery discovery, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        await using var socket = await HomeAssistantSocket.ConnectAsync(baseUri, token, cancellationToken).ConfigureAwait(false);
        await socket.CommandAsync(new JsonObject
        {
            ["type"] = "config_entries/ignore_flow", ["flow_id"] = discovery.FlowId,
            ["title"] = discovery.Title.Length > 0 ? discovery.Title : discovery.Name
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Updates Home Assistant offers (its update entities that are on): Home Assistant itself, its operating system and
    /// apps on Home Assistant OS, and integrations or devices that report updates.</summary>
    public async Task<IReadOnlyList<HomeUpdate>> UpdatesAsync(Uri baseUri, SecretLease token, CancellationToken cancellationToken)
    {
        using var document = await SendAsync(HttpMethod.Get, baseUri, "api/states", token, (byte[]?)null, MaximumStatesResponse,
            cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw NotHomeAssistant();
        var updates = new List<HomeUpdate>();
        foreach (var state in document.RootElement.EnumerateArray())
        {
            if (state.ValueKind != JsonValueKind.Object || Text(state, "entity_id") is not { Length: <= 255 } id ||
                !id.StartsWith("update.", StringComparison.Ordinal) || !id.All(IsEntityChar) || Text(state, "state") != "on")
                continue;
            var attributes = state.TryGetProperty("attributes", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
            string? Attribute(string name)
            {
                if (attributes.ValueKind != JsonValueKind.Object) return null;
                var value = Clean(Text(attributes, name), 80);
                return value.Length > 0 ? value : null;
            }
            var installing = attributes.ValueKind == JsonValueKind.Object && attributes.TryGetProperty("in_progress", out var progress) &&
                progress.ValueKind is JsonValueKind.True or JsonValueKind.Number;
            updates.Add(new(id, Attribute("title") ?? Attribute("friendly_name") ?? id["update.".Length..].Replace('_', ' '),
                Attribute("installed_version"), Attribute("latest_version"), installing));
        }
        return updates.OrderBy(u => u.EntityId.Contains("home_assistant", StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(u => u.Title, StringComparer.CurrentCultureIgnoreCase).Take(50).ToArray();
    }

    /// <summary>Installs one offered update (<c>update.install</c>). Home Assistant may restart to finish it.</summary>
    public async Task InstallUpdateAsync(Uri baseUri, SecretLease token, string entityId, CancellationToken cancellationToken)
    {
        if (!entityId.StartsWith("update.", StringComparison.Ordinal) || entityId.Length > 255 || !entityId.All(IsEntityChar))
            throw new ArgumentException("Not an update.", nameof(entityId));
        await ServiceAsync(baseUri, token, "update", "install", new JsonObject { ["entity_id"] = entityId }, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Restarts Home Assistant (<c>homeassistant.restart</c>); it is back after a minute or so.</summary>
    public Task RestartAsync(Uri baseUri, SecretLease token, CancellationToken cancellationToken) =>
        ServiceAsync(baseUri, token, "homeassistant", "restart", new JsonObject(), cancellationToken, restarting: true);

    /// <summary>How many backups Home Assistant keeps and when the newest was made.</summary>
    public async Task<HomeBackups> BackupsAsync(Uri baseUri, SecretLease token, CancellationToken cancellationToken)
    {
        await using var socket = await HomeAssistantSocket.ConnectAsync(baseUri, token, cancellationToken).ConfigureAwait(false);
        var info = await socket.CommandAsync(new JsonObject { ["type"] = "backup/info" }, cancellationToken).ConfigureAwait(false);
        var count = 0;
        DateTimeOffset? latest = null;
        if (info.ValueKind == JsonValueKind.Object && info.TryGetProperty("backups", out var backups) && backups.ValueKind == JsonValueKind.Array)
            foreach (var backup in backups.EnumerateArray())
            {
                count++;
                if (backup.ValueKind == JsonValueKind.Object && Text(backup, "date") is { } date &&
                    DateTimeOffset.TryParse(date, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at) &&
                    (latest is null || at > latest))
                    latest = at;
            }
        return new(count, latest);
    }

    /// <summary>Starts a full backup (configuration and history) on Home Assistant's own disk. Returns at once; the backup
    /// finishes in the background.</summary>
    public async Task BackupAsync(Uri baseUri, SecretLease token, CancellationToken cancellationToken)
    {
        await using var socket = await HomeAssistantSocket.ConnectAsync(baseUri, token, cancellationToken).ConfigureAwait(false);
        var agents = await socket.CommandAsync(new JsonObject { ["type"] = "backup/agents/info" }, cancellationToken).ConfigureAwait(false);
        var local = agents.ValueKind == JsonValueKind.Object && agents.TryGetProperty("agents", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(agent => agent.ValueKind == JsonValueKind.Object ? Text(agent, "agent_id") : null)
                .FirstOrDefault(id => id is not null && id.EndsWith(".local", StringComparison.Ordinal))
            : null;
        if (local is null)
            throw new HomeAssistantException(HomeAssistantFailure.BadResponse, "Home Assistant has no local backup location. Open its Backups page.");
        await socket.CommandAsync(new JsonObject
        {
            ["type"] = "backup/generate", ["agent_ids"] = new JsonArray(local), ["include_homeassistant"] = true,
            ["include_database"] = true
        }, cancellationToken, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
    }

    private async Task ServiceAsync(Uri baseUri, SecretLease token, string domain, string service, JsonObject data,
        CancellationToken cancellationToken, bool restarting = false)
    {
        try
        {
            using var document = await SendAsync(HttpMethod.Post, baseUri, $"api/services/{domain}/{service}", token,
                System.Text.Encoding.UTF8.GetBytes(data.ToJsonString()), MaximumStatesResponse, cancellationToken, TimeSpan.FromSeconds(60))
                .ConfigureAwait(false);
        }
        // A restart can close the connection before Home Assistant answers.
        catch (HomeAssistantException error) when (restarting && error.Failure is HomeAssistantFailure.Unreachable or HomeAssistantFailure.Timeout) { }
    }

    private Task<JsonDocument> SendAsync(HttpMethod method, Uri baseUri, string path, SecretLease token, byte[]? body, int maximumBytes,
        CancellationToken cancellationToken, TimeSpan limit) =>
        SendAsync(method, baseUri, path, token, body is null ? null : Json(body), maximumBytes, cancellationToken, limit: limit);

    internal static HomeFlowStep ParseStep(JsonElement root, string? flowId)
    {
        if (root.ValueKind != JsonValueKind.Object) throw NotHomeAssistant();
        var id = Text(root, "flow_id") is { Length: > 0 and <= 64 } found ? found : flowId ?? throw NotHomeAssistant();
        var kind = Text(root, "type") switch
        {
            "form" => HomeStepKind.Form,
            "create_entry" => HomeStepKind.Done,
            "abort" => HomeStepKind.Aborted,
            "external" or "external_done" => HomeStepKind.External,
            "progress" or "progress_done" => HomeStepKind.Progress,
            "menu" => HomeStepKind.Menu,
            _ => throw NotHomeAssistant()
        };
        var fields = new List<string>();
        if (root.TryGetProperty("data_schema", out var schema) && schema.ValueKind == JsonValueKind.Array)
            foreach (var field in schema.EnumerateArray().Take(32))
                if (field.ValueKind == JsonValueKind.Object && Clean(Text(field, "name"), 64) is { Length: > 0 } name) fields.Add(name);
        var errors = new List<string>();
        if (root.TryGetProperty("errors", out var problems) && problems.ValueKind == JsonValueKind.Object)
            foreach (var problem in problems.EnumerateObject().Take(8))
                if (problem.Value.ValueKind == JsonValueKind.String) errors.Add(Clean(problem.Value.GetString(), 64));
        return new(kind, id, Clean(Text(root, "step_id"), 64) is { Length: > 0 } stepId ? stepId : null, fields, errors,
            Clean(Text(root, "reason"), 64) is { Length: > 0 } reason ? reason : null,
            Clean(Text(root, "title"), 80) is { Length: > 0 } title ? title : null);
    }

    private static string FlowPath(string flowId) =>
        flowId is { Length: > 0 and <= 64 } && flowId.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
            ? flowId : throw new ArgumentException("Unknown setup flow.", nameof(flowId));

    private static string Pretty(string domain) =>
        string.Join(' ', domain.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
}
