using System.IO;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;
using Martlet.Home;
using Martlet.Mcp.Client;

namespace Martlet.Desktop;

/// <summary>The saved smart home connection. The access token lives in Windows Credential Manager, never here.
/// <paramref name="SharedRevision"/> is the newest connection shared through the paired hosts this PC has used or declined;
/// <paramref name="FollowShare"/> is on while this PC's connection is the shared one (it shared it, or took it from a host
/// in <paramref name="SharedBy"/>), so a newer share replaces it and stopping sharing elsewhere disconnects it.</summary>
internal sealed record HomePreferences(string Address = "", Guid CredentialId = default, bool Control = false,
    bool AllowSensitive = false, string LocationName = "", string Version = "", bool ModelTools = false,
    long SharedRevision = 0, bool FollowShare = false, string SharedBy = "")
{
    private const string FileName = "smart-home.json";

    internal static HomePreferences Load(string? directory)
    {
        if (directory is null) return new();
        try
        {
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path)) return new();
            var loaded = JsonSerializer.Deserialize<HomePreferences>(File.ReadAllText(path)) ?? new();
            return loaded with
            {
                Address = loaded.Address ?? "", LocationName = loaded.LocationName ?? "", Version = loaded.Version ?? "",
                SharedBy = loaded.SharedBy ?? ""
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    internal bool Save(string? directory)
    {
        if (directory is null) return false;
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            var temporary = Path.Combine(directory, $"smart-home.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(this));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}

internal enum HomeTurnKind { Skipped, Handled, NotRecognized, Blocked, Declined, Unreachable, Tools }

/// <summary>The smart home step of one user turn: what the persona is told, and a one-line summary for the talk window.</summary>
internal sealed record HomeTurn(HomeTurnKind Kind, string? Instructions, string? Summary)
{
    internal static HomeTurn Skipped { get; } = new(HomeTurnKind.Skipped, null, null);
    internal string Code => "home." + Kind.ToString().ToLowerInvariant();
}

internal sealed record HomeAction(DateTimeOffset At, string Summary);

/// <summary>Martlet's Home Assistant connection. On each turn the user starts (typed, push-to-talk or hands-free, never a
/// screen or camera look) and only while control is on, the user's words go to Home Assistant's built-in Assist, which
/// acts only on sentences it recognizes and only on entities exposed to voice assistants. Requests about locks, doors,
/// garages, gates, alarms and valves are blocked unless allowed, and then each needs a click. What happened is told to
/// the persona, shown in the talk window and kept in a short in-memory action list.</summary>
internal sealed class SmartHome : IDisposable
{
    internal static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(30);
    /// <summary>The name of Home Assistant's MCP server among the conversation's tool servers.</summary>
    internal const string ServerName = "home-assistant";
    private static readonly TimeSpan SafetyRefresh = TimeSpan.FromMinutes(5);
    private const int MaximumActions = 20;
    private readonly Lock gate = new();
    private readonly string? directory;
    private readonly WindowsCredentialStore vault;
    private readonly HomeAssistantClient client;
    private readonly Queue<HomeAction> actions = new();
    private readonly TimeProvider clock;
    private HomePreferences preferences;
    private McpToolService? tools;
    private IReadOnlyCollection<string>? sensitiveNames;
    private long sensitiveNamesAt, safetyAttemptAt;
    private string? registered;

    internal SmartHome(string? directory, WindowsCredentialStore? vault = null, HomeAssistantClient? client = null, TimeProvider? clock = null)
    {
        this.directory = directory;
        this.vault = vault ?? new WindowsCredentialStore();
        this.client = client ?? new HomeAssistantClient();
        this.clock = clock ?? TimeProvider.System;
        preferences = HomePreferences.Load(directory);
    }

    internal HomePreferences Preferences { get { lock (gate) return preferences; } }
    internal bool Connected => Preferences is { Address.Length: > 0 } saved && saved.CredentialId != Guid.Empty;
    internal bool ControlEnabled => Connected && Preferences.Control;
    /// <summary>The Thinking model may also use Home Assistant's own tools (its MCP server) on tool-capable routes.</summary>
    internal bool ModelToolsEnabled => ControlEnabled && Preferences.ModelTools;
    internal McpToolService? Tools { get { lock (gate) return tools; } }
    internal IReadOnlyList<HomeAction> RecentActions { get { lock (gate) return actions.Reverse().ToArray(); } }

    /// <summary>Asks the user in the talk window whether to send a sensitive request; false or cancellation means no.</summary>
    internal Func<string, CancellationToken, Task<bool>>? Confirm { get; set; }

    /// <summary>Raised off the UI thread when the connection, settings or action list change.</summary>
    internal event Action? Changed;

    /// <summary>Checks the address and token against Home Assistant and saves them. Without a new token the saved one is
    /// reused, but only for the same address, so a token is never sent somewhere new without being entered again.</summary>
    internal async Task<HomeAssistantInfo> ConnectAsync(string address, SecretLease? token, CancellationToken cancellationToken)
    {
        var baseUri = HomeAssistantEndpoint.Normalize(address);
        var saved = Preferences;
        CredentialReadResult? read = null;
        try
        {
            var lease = token;
            if (lease is null)
            {
                if (saved.CredentialId == Guid.Empty || !SameAddress(saved.Address, baseUri))
                    throw new HomeAssistantException(HomeAssistantFailure.Unauthorized,
                        "Enter a Home Assistant long-lived access token first.");
                read = vault.ReadHomeAssistantToken(saved.CredentialId);
                lease = read.Secret ?? throw new HomeAssistantException(HomeAssistantFailure.Unauthorized,
                    "The saved access token couldn't be read. Enter it again.");
            }
            var info = await client.GetInfoAsync(baseUri, lease, cancellationToken).ConfigureAwait(false);
            var credentialId = saved.CredentialId;
            if (token is not null)
            {
                var fresh = Guid.NewGuid();
                var error = vault.WriteHomeAssistantToken(fresh, token);
                if (error != CredentialError.None)
                    throw new HomeAssistantException(HomeAssistantFailure.Unauthorized,
                        $"Home Assistant accepted the token, but Martlet couldn't save it ({error}).");
                if (credentialId != Guid.Empty) vault.DeleteHomeAssistantToken(credentialId);
                credentialId = fresh;
            }
            // A token entered here is this PC's own connection; it is shared again only when the owner chooses to.
            Update(current => current with
            {
                Address = HomeAssistantEndpoint.Display(baseUri), CredentialId = credentialId,
                LocationName = info.LocationName, Version = info.Version,
                FollowShare = token is null && current.FollowShare
            });
            return info;
        }
        finally { read?.Dispose(); }
    }

    /// <summary>Where a Home Assistant is in its first-run setup (needs no sign-in).</summary>
    internal Task<HomeOnboarding> OnboardingAsync(string address, CancellationToken cancellationToken) =>
        client.OnboardingAsync(HomeAssistantEndpoint.Normalize(address), cancellationToken);

    /// <summary>Sets up a Home Assistant nobody has set up yet: creates its owner account (an administrator) with the name
    /// and password the user chose, gives it Windows' time zone, country, currency, units and language, leaves analytics
    /// off, mints Martlet's own long-lived token and connects. The password goes only to that Home Assistant.</summary>
    internal async Task<HomeAssistantInfo> SetUpAsync(string address, string name, string username, ReadOnlyMemory<char> password,
        bool control, CancellationToken cancellationToken)
    {
        var baseUri = HomeAssistantEndpoint.Normalize(address);
        var state = await client.OnboardingAsync(baseUri, cancellationToken).ConfigureAwait(false);
        if (!state.NeedsOwner)
            throw new HomeAssistantException(HomeAssistantFailure.Unauthorized,
                "This Home Assistant already has an owner. Sign in with Home Assistant instead.");
        var region = HomeRegion.FromSystem();
        var session = await client.CreateOwnerAsync(baseUri, name, username, password, region.Language, cancellationToken).ConfigureAwait(false);
        await client.FinishOnboardingAsync(baseUri, session, region, cancellationToken).ConfigureAwait(false);
        using var token = await client.MintTokenAsync(baseUri, session, ClientName(), cancellationToken).ConfigureAwait(false);
        var info = await StoreAsync(baseUri, token, followShare: false, sharedBy: "", cancellationToken).ConfigureAwait(false);
        if (control) SetControl(true, false, Preferences.ModelTools);
        return info;
    }

    /// <summary>"Sign in with Home Assistant": Home Assistant's own sign-in page opens in the browser; Martlet receives the
    /// one-time code on this PC's loopback, mints its own long-lived token and connects.</summary>
    internal async Task<HomeAssistantInfo> SignInAsync(string address, Action<Uri> openBrowser, CancellationToken cancellationToken)
    {
        var baseUri = HomeAssistantEndpoint.Normalize(address);
        var state = await client.OnboardingAsync(baseUri, cancellationToken).ConfigureAwait(false);
        if (state.NeedsOwner)
            throw new HomeAssistantException(HomeAssistantFailure.Unauthorized,
                "Nobody has set up this Home Assistant yet. Use Set up below to create its owner account.");
        using var token = await HomeAssistantSignIn.SignInAsync(client, baseUri, ClientName(), openBrowser, cancellationToken)
            .ConfigureAwait(false);
        return await StoreAsync(baseUri, token, followShare: false, sharedBy: "", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Finds Home Assistant on the local network (one multicast DNS question, answers listed for the user).</summary>
    internal static Task<IReadOnlyList<FoundHomeAssistant>> FindAsync(CancellationToken cancellationToken) =>
        HomeAssistantDiscovery.FindAsync(TimeSpan.FromSeconds(2.5), cancellationToken);

    // Checks a fresh token, keeps it in the vault in place of the old one and saves the connection.
    private async Task<HomeAssistantInfo> StoreAsync(Uri baseUri, SecretLease token, bool followShare, string sharedBy,
        CancellationToken cancellationToken, long? revision = null)
    {
        var info = await client.GetInfoAsync(baseUri, token, cancellationToken).ConfigureAwait(false);
        var fresh = Guid.NewGuid();
        var error = vault.WriteHomeAssistantToken(fresh, token);
        if (error != CredentialError.None)
            throw new HomeAssistantException(HomeAssistantFailure.Unauthorized, $"Martlet couldn't save the Home Assistant token ({error}).");
        var previous = Preferences.CredentialId;
        if (previous != Guid.Empty) vault.DeleteHomeAssistantToken(previous);
        Update(current => current with
        {
            Address = HomeAssistantEndpoint.Display(baseUri), CredentialId = fresh, LocationName = info.LocationName, Version = info.Version,
            FollowShare = followShare, SharedBy = sharedBy, SharedRevision = revision is { } r ? Math.Max(r, current.SharedRevision) : current.SharedRevision
        });
        return info;
    }

    private static string ClientName() =>
        $"Martlet on {new string(Environment.MachineName.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').Take(24).ToArray())} " +
        $"({DateTime.Now:yyyy-MM-dd HH:mm:ss})";

    // ---------- managing Home Assistant (owner clicks only; never offered to the Thinking model) ----------

    internal Task<HomeSystem> SystemAsync(CancellationToken token) => WithTokenAsync((uri, lease) => client.SystemAsync(uri, lease, token));
    internal Task<IReadOnlyList<HomeDiscovery>> DiscoveriesAsync(CancellationToken token) =>
        WithTokenAsync((uri, lease) => client.DiscoveriesAsync(uri, lease, token));
    internal Task<IReadOnlyList<HomeUpdate>> UpdatesAsync(CancellationToken token) => WithTokenAsync((uri, lease) => client.UpdatesAsync(uri, lease, token));
    internal Task<HomeBackups> BackupsAsync(CancellationToken token) => WithTokenAsync((uri, lease) => client.BackupsAsync(uri, lease, token));

    internal async Task<HomeFlowStep> AddDiscoveredAsync(HomeDiscovery discovery, CancellationToken token)
    {
        var step = await WithTokenAsync((uri, lease) => client.ContinueFlowAsync(uri, lease, discovery.FlowId, token)).ConfigureAwait(false);
        if (step.Kind == HomeStepKind.Done) Record($"Added {Label(discovery)} to Home Assistant.");
        return step;
    }

    internal async Task IgnoreDiscoveredAsync(HomeDiscovery discovery, CancellationToken token)
    {
        await WithTokenAsync(async (uri, lease) => { await client.IgnoreFlowAsync(uri, lease, discovery, token).ConfigureAwait(false); return 0; })
            .ConfigureAwait(false);
        Record($"Home Assistant will stop offering {Label(discovery)}.");
    }

    /// <summary>Adds Home Assistant's MQTT integration pointed at a broker Martlet found on its host (Zigbee2MQTT, Frigate,
    /// Shelly and Tasmota devices then appear through it). Tries the broker's local address, then the host's LAN address.</summary>
    internal async Task<HomeFlowStep> AddMqttAsync(string hostAddress, CancellationToken token)
    {
        HomeFlowStep? step = null;
        foreach (var broker in new[] { "127.0.0.1", hostAddress }.Distinct())
        {
            step = await WithTokenAsync((uri, lease) => client.StartFlowAsync(uri, lease, "mqtt",
                new JsonObject { ["broker"] = broker, ["port"] = 1883 }, token)).ConfigureAwait(false);
            if (step.Kind == HomeStepKind.Done)
            {
                Record($"Added MQTT ({broker}:1883) to Home Assistant.");
                break;
            }
            if (step.Kind != HomeStepKind.Form) break;
        }
        return step!;
    }

    internal async Task InstallUpdateAsync(HomeUpdate update, CancellationToken token)
    {
        await WithTokenAsync(async (uri, lease) => { await client.InstallUpdateAsync(uri, lease, update.EntityId, token).ConfigureAwait(false); return 0; })
            .ConfigureAwait(false);
        Record($"Started updating {update.Title}{(update.Latest is { } latest ? " to " + latest : "")}.");
    }

    internal async Task RestartAsync(CancellationToken token)
    {
        await WithTokenAsync(async (uri, lease) => { await client.RestartAsync(uri, lease, token).ConfigureAwait(false); return 0; }).ConfigureAwait(false);
        Record("Restarted Home Assistant.");
    }

    internal async Task BackupAsync(CancellationToken token)
    {
        await WithTokenAsync(async (uri, lease) => { await client.BackupAsync(uri, lease, token).ConfigureAwait(false); return 0; }).ConfigureAwait(false);
        Record("Started a Home Assistant backup.");
    }

    private static string Label(HomeDiscovery discovery) => discovery.Title.Length > 0 ? $"{discovery.Name} ({discovery.Title})" : discovery.Name;

    private async Task<T> WithTokenAsync<T>(Func<Uri, SecretLease, Task<T>> action)
    {
        var saved = Preferences;
        if (!Connected) throw new HomeAssistantException(HomeAssistantFailure.Unauthorized, "Connect Home Assistant first.");
        var baseUri = HomeAssistantEndpoint.Normalize(saved.Address);
        using var read = vault.ReadHomeAssistantToken(saved.CredentialId);
        var token = read.Secret ?? throw new HomeAssistantException(HomeAssistantFailure.Unauthorized,
            "The saved access token couldn't be read. Connect again in Smart home.");
        return await action(baseUri, token).ConfigureAwait(false);
    }

    // ---------- sharing the connection through the paired hosts ----------

    /// <summary>This PC's connection as the shared value with <paramref name="revision"/>, or null when not connected.</summary>
    internal SharedHomeAssistant? ShareValue(long revision)
    {
        var saved = Preferences;
        if (!Connected) return null;
        using var read = vault.ReadHomeAssistantToken(saved.CredentialId);
        string? bearer = null;
        read.Secret?.Use(value => bearer = new string(value));
        return bearer is null ? null : new SharedHomeAssistant(revision, saved.Address, bearer,
            saved.LocationName.Length > 0 ? saved.LocationName : null, saved.Version.Length > 0 ? saved.Version : null);
    }

    /// <summary>This PC now shares its connection (it follows the shared value from here on).</summary>
    internal void MarkShared(long revision) =>
        Update(current => current with { FollowShare = true, SharedBy = "", SharedRevision = Math.Max(revision, current.SharedRevision) });

    /// <summary>Sharing stopped from this PC: it keeps its own connection, no longer tied to the shared one.</summary>
    internal void MarkUnshared(long revision) =>
        Update(current => current with { FollowShare = false, SharedBy = "", SharedRevision = Math.Max(revision, current.SharedRevision) });

    /// <summary>Applies a newer connection shared through <paramref name="hostId"/>: takes it when this PC has no connection
    /// or follows the shared one (turning on "Use Home Assistant when I ask" the first time), or disconnects a followed
    /// connection when sharing stopped. A connection the owner made on this PC alone is never replaced. Returns what changed,
    /// or null for nothing.</summary>
    internal async Task<string?> AdoptAsync(SharedHomeAssistant shared, string hostId, CancellationToken cancellationToken, bool replace = false)
    {
        var saved = Preferences;
        if (!replace && (shared.Revision <= saved.SharedRevision || Connected && !saved.FollowShare)) return null;
        if (shared.Address is null || shared.Token is null)
        {
            if (!Connected) { Update(current => current with { SharedRevision = shared.Revision }); return null; }
            Disconnect();
            Update(current => current with { SharedRevision = shared.Revision });
            return "Your other computers stopped sharing Home Assistant, so this PC disconnected from it.";
        }
        var baseUri = HomeAssistantEndpoint.Normalize(shared.Address);
        if (Connected && SameAddress(saved.Address, baseUri) && SameToken(saved.CredentialId, shared.Token))
        {
            Update(current => current with { SharedRevision = Math.Max(shared.Revision, current.SharedRevision), FollowShare = true });
            return null;
        }
        var first = !Connected;
        using (var token = new SecretLease(shared.Token))
            await StoreAsync(baseUri, token, followShare: true, sharedBy: hostId, cancellationToken, shared.Revision).ConfigureAwait(false);
        if (first) SetControl(true, false, false);
        Record($"Connected to Home Assistant at {HomeAssistantEndpoint.Display(baseUri)}, shared through {hostId}.");
        return $"Connected to Home Assistant at {HomeAssistantEndpoint.Display(baseUri)}, shared by your other computers through {hostId}.";
    }

    /// <summary>Forgets the token and turns control off; the address stays filled in.</summary>
    internal void Disconnect()
    {
        var saved = Preferences;
        if (saved.CredentialId != Guid.Empty) vault.DeleteHomeAssistantToken(saved.CredentialId);
        Update(current => current with
        {
            CredentialId = Guid.Empty, Control = false, AllowSensitive = false, ModelTools = false, LocationName = "", Version = "",
            FollowShare = false, SharedBy = ""
        });
        lock (gate) actions.Clear();
    }

    internal bool SetControl(bool control, bool allowSensitive, bool modelTools)
    {
        var connected = Connected;
        return Update(current => current with
        {
            Control = control && connected, AllowSensitive = allowSensitive && control && connected,
            ModelTools = modelTools && control && connected
        });
    }

    /// <summary>Joins the conversation's MCP tool servers: while "use Home Assistant's tools" is on, Home Assistant's MCP
    /// server (<c>/api/mcp</c>) is a Smart home-managed server whose every call passes <see cref="Policy"/>.</summary>
    internal void Attach(McpToolService service)
    {
        lock (gate) tools = service;
        Register();
    }

    /// <summary>A tool-capable turn that offers Home Assistant's tools skips the Assist step, so nothing runs twice.</summary>
    internal HomeTurn ToolsTurn() => new(HomeTurnKind.Tools, HomeAssistantContext.ToolsOffered(Preferences.AllowSensitive), null);

    /// <summary>Reads which of Home Assistant's entities can open the home (at most every 5 minutes) so tool calls that
    /// name one are held back. A failure keeps the previous list; with none, calls that address devices by name ask first.</summary>
    internal async Task RefreshSafetyAsync(CancellationToken cancellationToken)
    {
        var saved = Preferences;
        if (!ModelToolsEnabled) return;
        lock (gate)
        {
            if (sensitiveNames is not null && clock.GetElapsedTime(sensitiveNamesAt) < SafetyRefresh ||
                safetyAttemptAt != 0 && clock.GetElapsedTime(safetyAttemptAt) < TimeSpan.FromMinutes(1))
                return;
            safetyAttemptAt = clock.GetTimestamp();
        }
        try
        {
            var baseUri = HomeAssistantEndpoint.Normalize(saved.Address);
            using var read = vault.ReadHomeAssistantToken(saved.CredentialId);
            if (read.Secret is not { } token) return;
            var names = await client.SensitiveNamesAsync(baseUri, token, cancellationToken).ConfigureAwait(false);
            lock (gate)
            {
                sensitiveNames = names;
                sensitiveNamesAt = clock.GetTimestamp();
            }
        }
        catch (HomeAssistantException) { }
    }

    /// <summary>How one Home Assistant tool call is approved: status tools and ordinary devices run; locks, doors, garages,
    /// gates, alarms and valves ask every time when allowed and are blocked otherwise; anything else (such as an exposed
    /// script) asks every time.</summary>
    internal ToolApprovalPolicy Policy(string tool, JsonObject arguments)
    {
        var saved = Preferences;
        if (!ModelToolsEnabled) return ToolApprovalPolicy.Deny;
        IReadOnlyCollection<string>? names;
        lock (gate) names = sensitiveNames;
        var risk = HomeCommandGuard.AssessTool(tool, arguments, names);
        var policy = risk switch
        {
            HomeToolRisk.ReadOnly or HomeToolRisk.Comfort => ToolApprovalPolicy.AutoApprove,
            HomeToolRisk.Sensitive => saved.AllowSensitive ? ToolApprovalPolicy.AskEveryTime : ToolApprovalPolicy.Deny,
            _ => ToolApprovalPolicy.AskEveryTime
        };
        if (policy == ToolApprovalPolicy.Deny)
            Record($"Blocked {tool}: smart-home settings don't allow locks, doors, garage doors, gates, alarms or valves.");
        else if (risk != HomeToolRisk.ReadOnly)
            Record($"Home Assistant tool requested: {tool}{(policy == ToolApprovalPolicy.AskEveryTime ? " (asked first)" : "")}.");
        return policy;
    }

    // Adds, replaces or removes Home Assistant's MCP server; a new token or address restarts it.
    private void Register()
    {
        McpToolService? service;
        lock (gate) service = tools;
        if (service is null) return;
        var saved = Preferences;
        McpServerDefinition? definition = null;
        string? key = null;
        if (ModelToolsEnabled)
        {
            using var read = vault.ReadHomeAssistantToken(saved.CredentialId);
            string? bearer = null;
            read.Secret?.Use(value => bearer = new string(value));
            if (bearer is not null)
            {
                definition = new McpServerDefinition
                {
                    Name = ServerName, ManagedBy = "Smart home", Transport = McpTransportKind.Http,
                    Url = HomeAssistantClient.McpEndpoint(HomeAssistantEndpoint.Normalize(saved.Address)),
                    Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + bearer }
                };
                key = saved.Address + "|" + saved.CredentialId.ToString("N");
            }
        }
        lock (gate)
        {
            if (key == registered) return;
            registered = key;
            sensitiveNames = null;
            safetyAttemptAt = 0;
        }
        service.SetManagedServer(ServerName, definition, definition is null ? null : Policy);
    }

    /// <summary>The smart home step for one user-started turn. Never throws for Home Assistant problems: the reply goes
    /// ahead and the persona is told nothing changed.</summary>
    internal async Task<HomeTurn> HandleAsync(string text, CancellationToken cancellationToken)
    {
        var saved = Preferences;
        if (!ControlEnabled || string.IsNullOrWhiteSpace(text)) return HomeTurn.Skipped;
        var risk = HomeCommandGuard.Assess(text);
        if (risk == HomeCommandRisk.NotACommand) return new(HomeTurnKind.NotRecognized, HomeAssistantContext.NotRecognized(), null);
        var confirmed = false;
        if (risk == HomeCommandRisk.Sensitive)
        {
            if (!saved.AllowSensitive)
            {
                Record("Not sent: smart-home settings don't allow locks, doors, garage doors, gates, alarms or valves.");
                return new(HomeTurnKind.Blocked, HomeAssistantContext.Blocked(),
                    "Not sent to Home Assistant: locks, doors, garages, gates, alarms and valves are off in Smart home settings.");
            }
            confirmed = await AskAsync(text, cancellationToken).ConfigureAwait(false);
            if (!confirmed)
            {
                Record("Not sent: you declined a request about a lock, door, garage, gate, alarm or valve.");
                return new(HomeTurnKind.Declined, HomeAssistantContext.Declined(), "Not sent to Home Assistant.");
            }
        }
        try
        {
            var baseUri = HomeAssistantEndpoint.Normalize(saved.Address);
            using var read = vault.ReadHomeAssistantToken(saved.CredentialId);
            var token = read.Secret ?? throw new HomeAssistantException(HomeAssistantFailure.Unauthorized,
                "The saved access token couldn't be read. Connect again in Smart home.");
            var result = await client.ProcessAsync(baseUri, token, text, cancellationToken).ConfigureAwait(false);
            if (!result.Recognized) return new(HomeTurnKind.NotRecognized, HomeAssistantContext.NotRecognized(), null);
            var summary = Describe(result);
            if (result.Kind == HomeResponseKind.ActionDone && !confirmed && result.Succeeded.Any(HomeCommandGuard.IsSensitive))
                summary += " It affected a lock, door, garage, gate, alarm or valve. Hide that device from Home Assistant voice assistants to block it.";
            if (result.Kind != HomeResponseKind.QueryAnswer) Record(summary);
            return new(HomeTurnKind.Handled, HomeAssistantContext.Handled(result), "Home Assistant: " + summary);
        }
        catch (HomeAssistantException error)
        {
            Record("Couldn't reach Home Assistant: " + error.Message);
            return new(HomeTurnKind.Unreachable, HomeAssistantContext.Unreachable(), "Home Assistant: " + error.Message);
        }
    }

    /// <summary>The token for a Home Assistant camera snapshot address (<c>/api/camera_proxy/camera.*</c>) on the connected
    /// Home Assistant, so Watch can look at its cameras. Any other address gets nothing.</summary>
    internal AuthenticationHeaderValue? CameraAuthorization(Uri address)
    {
        var saved = Preferences;
        if (!Connected || !Uri.TryCreate(saved.Address + "/", UriKind.Absolute, out var baseUri) ||
            !HomeAssistantEndpoint.Contains(baseUri, address) ||
            !address.AbsolutePath[baseUri.AbsolutePath.Length..].StartsWith("api/camera_proxy/camera.", StringComparison.Ordinal))
            return null;
        using var read = vault.ReadHomeAssistantToken(saved.CredentialId);
        AuthenticationHeaderValue? header = null;
        read.Secret?.Use(value => header = new("Bearer", new string(value)));
        return header;
    }

    /// <summary>Home Assistant's cameras with the snapshot address Watch can use for each.</summary>
    internal async Task<IReadOnlyList<(HomeCamera Camera, Uri Snapshot)>> CamerasAsync(CancellationToken cancellationToken)
    {
        var saved = Preferences;
        if (!Connected) throw new HomeAssistantException(HomeAssistantFailure.Unauthorized, "Connect Home Assistant in Smart home first.");
        var baseUri = HomeAssistantEndpoint.Normalize(saved.Address);
        using var read = vault.ReadHomeAssistantToken(saved.CredentialId);
        var token = read.Secret ?? throw new HomeAssistantException(HomeAssistantFailure.Unauthorized,
            "The saved access token couldn't be read. Connect again in Smart home.");
        var cameras = await client.CamerasAsync(baseUri, token, cancellationToken).ConfigureAwait(false);
        return cameras.Select(camera => (camera, HomeAssistantClient.CameraSnapshot(baseUri, camera.EntityId))).ToArray();
    }

    private async Task<bool> AskAsync(string text, CancellationToken cancellationToken)
    {
        if (Confirm is not { } ask) return false;
        var request = text.Trim();
        if (request.Length > 200) request = request[..200] + "...";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConfirmTimeout);
        try
        {
            return await ask($"Send this to Home Assistant?\n\n\"{request}\"\n\nIt mentions a lock, door, garage, gate, alarm or valve. " +
                "It may run right away if Home Assistant recognizes it. No answer within " +
                $"{ConfirmTimeout.TotalSeconds:0} seconds means No.", timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
    }

    private static string Describe(HomeCommandResult result)
    {
        var said = result.Speech.Length > 0 ? result.Speech : result.Kind switch
        {
            HomeResponseKind.ActionDone => "Done.",
            HomeResponseKind.QueryAnswer => "Answered.",
            _ => "Couldn't do that."
        };
        var failed = result.Failed.Count > 0
            ? " Failed: " + string.Join(", ", result.Failed.Select(t => t.Name).Where(n => n.Length > 0).Distinct().Take(6)) + "."
            : "";
        return said + failed;
    }

    private void Record(string summary)
    {
        lock (gate)
        {
            actions.Enqueue(new(clock.GetLocalNow(), summary));
            while (actions.Count > MaximumActions) actions.Dequeue();
        }
        Changed?.Invoke();
    }

    private bool Update(Func<HomePreferences, HomePreferences> change)
    {
        bool saved;
        lock (gate)
        {
            preferences = change(preferences);
            saved = preferences.Save(directory);
        }
        Register();
        Changed?.Invoke();
        return saved;
    }

    private static bool SameAddress(string saved, Uri baseUri) =>
        saved.Length > 0 && string.Equals(saved, HomeAssistantEndpoint.Display(baseUri), StringComparison.OrdinalIgnoreCase);

    private bool SameToken(Guid credentialId, string token)
    {
        using var read = vault.ReadHomeAssistantToken(credentialId);
        var same = false;
        read.Secret?.Use(value => same = value.SequenceEqual(token.AsSpan()));
        return same;
    }

    public void Dispose() => client.Dispose();
}
