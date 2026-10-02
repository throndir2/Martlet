using System.IO;
using System.Net.Http.Headers;
using System.Text.Json;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;
using Martlet.Home;

namespace Martlet.Desktop;

/// <summary>The saved smart home connection. The access token lives in Windows Credential Manager, never here.</summary>
internal sealed record HomePreferences(string Address = "", Guid CredentialId = default, bool Control = false,
    bool AllowSensitive = false, string LocationName = "", string Version = "")
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
                Address = loaded.Address ?? "", LocationName = loaded.LocationName ?? "", Version = loaded.Version ?? ""
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

internal enum HomeTurnKind { Skipped, Handled, NotRecognized, Blocked, Declined, Unreachable }

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
    private const int MaximumActions = 20;
    private readonly Lock gate = new();
    private readonly string? directory;
    private readonly WindowsCredentialStore vault;
    private readonly HomeAssistantClient client;
    private readonly Queue<HomeAction> actions = new();
    private readonly TimeProvider clock;
    private HomePreferences preferences;

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
    internal IReadOnlyList<HomeAction> RecentActions { get { lock (gate) return actions.Reverse().ToArray(); } }

    /// <summary>Asks the user (on screen) whether to send a sensitive request; false or cancellation means no.</summary>
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
                        "Paste a long-lived access token from Home Assistant (your profile, then Security) first.");
                read = vault.ReadHomeAssistantToken(saved.CredentialId);
                lease = read.Secret ?? throw new HomeAssistantException(HomeAssistantFailure.Unauthorized,
                    "The saved access token couldn't be read from Windows Credential Manager. Paste it again.");
            }
            var info = await client.GetInfoAsync(baseUri, lease, cancellationToken).ConfigureAwait(false);
            var credentialId = saved.CredentialId;
            if (token is not null)
            {
                var fresh = Guid.NewGuid();
                var error = vault.WriteHomeAssistantToken(fresh, token);
                if (error != CredentialError.None)
                    throw new HomeAssistantException(HomeAssistantFailure.Unauthorized,
                        $"Home Assistant accepted the token, but Windows Credential Manager couldn't save it ({error}). Nothing was changed.");
                if (credentialId != Guid.Empty) vault.DeleteHomeAssistantToken(credentialId);
                credentialId = fresh;
            }
            Update(current => current with
            {
                Address = HomeAssistantEndpoint.Display(baseUri), CredentialId = credentialId,
                LocationName = info.LocationName, Version = info.Version
            });
            return info;
        }
        finally { read?.Dispose(); }
    }

    /// <summary>Forgets the token and turns control off; the address stays filled in.</summary>
    internal void Disconnect()
    {
        var saved = Preferences;
        if (saved.CredentialId != Guid.Empty) vault.DeleteHomeAssistantToken(saved.CredentialId);
        Update(current => current with { CredentialId = Guid.Empty, Control = false, AllowSensitive = false, LocationName = "", Version = "" });
        lock (gate) actions.Clear();
    }

    internal bool SetControl(bool control, bool allowSensitive)
    {
        var connected = Connected;
        return Update(current => current with { Control = control && connected, AllowSensitive = allowSensitive && control && connected });
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
                Record("Not sent: it mentioned a lock, door, garage, gate, alarm or valve, which Smart home settings don't allow.");
                return new(HomeTurnKind.Blocked, HomeAssistantContext.Blocked(),
                    "Not sent to Home Assistant: locks, doors, garages, gates, alarms and valves are turned off in Smart home settings.");
            }
            confirmed = await AskAsync(text, cancellationToken).ConfigureAwait(false);
            if (!confirmed)
            {
                Record("Not sent: you declined a request about a lock, door, garage, gate, alarm or valve.");
                return new(HomeTurnKind.Declined, HomeAssistantContext.Declined(), "Not sent to Home Assistant: you said no.");
            }
        }
        try
        {
            var baseUri = HomeAssistantEndpoint.Normalize(saved.Address);
            using var read = vault.ReadHomeAssistantToken(saved.CredentialId);
            var token = read.Secret ?? throw new HomeAssistantException(HomeAssistantFailure.Unauthorized,
                "The saved access token couldn't be read from Windows Credential Manager. Connect again in Smart home.");
            var result = await client.ProcessAsync(baseUri, token, text, cancellationToken).ConfigureAwait(false);
            if (!result.Recognized) return new(HomeTurnKind.NotRecognized, HomeAssistantContext.NotRecognized(), null);
            var summary = Describe(result);
            if (result.Kind == HomeResponseKind.ActionDone && !confirmed && result.Succeeded.Any(HomeCommandGuard.IsSensitive))
                summary += " It operated a lock, door, garage, gate, alarm or valve that Martlet's word check didn't catch; " +
                    "stop exposing it to voice assistants in Home Assistant to prevent that.";
            if (result.Kind != HomeResponseKind.QueryAnswer) Record(summary);
            return new(HomeTurnKind.Handled, HomeAssistantContext.Handled(result), "Home Assistant: " + summary);
        }
        catch (HomeAssistantException error)
        {
            Record("Couldn't reach Home Assistant: " + error.Message);
            return new(HomeTurnKind.Unreachable, HomeAssistantContext.Unreachable(), "Home Assistant: " + error.Message);
        }
    }

    /// <summary>What the talk window's envelope says about the smart home step, or null while control is off.</summary>
    internal string? Disclosure()
    {
        if (!ControlEnabled) return null;
        var saved = Preferences;
        return $"Smart home is ON (change it in Companion > Smart home): before each reply to what you say or type, your words go to your " +
            $"Home Assistant at {saved.Address}. Its built-in Assist (sentence matching on that computer, no AI model) acts only on commands it " +
            "recognizes and only on devices you exposed to voice assistants there; what it did or answered goes to the Thinking model with your " +
            "message so Martlet can reply. " +
            (saved.AllowSensitive
                ? "Requests that mention a lock, door, garage, gate, alarm or valve are sent only after you click Yes. "
                : "Requests that mention a lock, door, garage, gate, alarm or valve are never sent. ") +
            "Screen and camera looks never reach Home Assistant. Nothing is saved; the last few actions are listed in Smart home until you close Martlet.";
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
            "The saved access token couldn't be read from Windows Credential Manager. Connect again in Smart home.");
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
                "If Home Assistant recognizes it as a command, it carries it out right away. Martlet asks every time; " +
                $"no answer within {ConfirmTimeout.TotalSeconds:0} seconds means no.", timeout.Token).ConfigureAwait(false);
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
        Changed?.Invoke();
        return saved;
    }

    private static bool SameAddress(string saved, Uri baseUri) =>
        saved.Length > 0 && string.Equals(saved, HomeAssistantEndpoint.Display(baseUri), StringComparison.OrdinalIgnoreCase);

    public void Dispose() => client.Dispose();
}
