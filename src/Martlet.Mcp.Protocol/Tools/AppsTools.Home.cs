using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Installation;
using Martlet.Doctor;
using Martlet.Mcp.Client;

namespace Martlet.Mcp;

internal sealed partial class McpServer
{
    /// <summary>One Home Assistant's first-run state (no sign-in needed), as Smart home checks it before offering Set up.</summary>
    private static async Task<object> HomeAssistantProbeAsync(JsonElement arguments, CancellationToken cancellation)
    {
        Uri address;
        try { address = Martlet.Home.HomeAssistantEndpoint.Normalize(RequiredString(arguments, "address")); }
        catch (Martlet.Home.HomeAssistantException error) { throw new ArgumentException(error.Message); }
        using var client = new Martlet.Home.HomeAssistantClient();
        try
        {
            var state = await client.OnboardingAsync(address, cancellation);
            return new
            {
                address = Martlet.Home.HomeAssistantEndpoint.Display(address), homeAssistant = true,
                onboarding = new { owner = state.Owner, coreConfig = state.CoreConfig, analytics = state.Analytics, integration = state.Integration, done = state.Done },
                next = state.NeedsOwner ? "Set up (Martlet can create its owner account)" : "Sign in with Home Assistant"
            };
        }
        catch (Martlet.Home.HomeAssistantException error)
        {
            return new { address = Martlet.Home.HomeAssistantEndpoint.Display(address), homeAssistant = false, failure = error.Failure.ToString(), problem = error.Message };
        }
    }

    private static async Task<object> HomeAssistantFindAsync(JsonElement arguments, CancellationToken cancellation)
    {
        var seconds = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("seconds", out var value) &&
            value.ValueKind == JsonValueKind.Number ? Math.Clamp(value.GetDouble(), 1, 10) : 2.5;
        var found = await Martlet.Home.HomeAssistantDiscovery.FindAsync(TimeSpan.FromSeconds(seconds), cancellation);
        return new
        {
            service = Martlet.Home.HomeAssistantDiscovery.ServiceType, listenedSeconds = seconds, count = found.Count,
            found = found.Select(f => new { name = f.Name, address = Martlet.Home.HomeAssistantEndpoint.Display(f.Address), version = f.Version })
        };
    }

    /// <summary>smart-home.json in a data directory, read through its store (Martlet.Home.HomePreferences). The token lives in
    /// Windows Credential Manager and is never read here.</summary>
    private static object SmartHomeStatus(JsonElement arguments)
    {
        var directory = DataDirectory(arguments);
        if (!File.Exists(Path.Combine(directory, Martlet.Home.HomePreferences.FileName))) return new { state = "none", connected = false };
        try
        {
            var saved = Martlet.Home.HomePreferences.Read(directory);
            var tokenSaved = saved.CredentialId != Guid.Empty;
            return new
            {
                state = "loaded", connected = saved.Address.Length > 0 && tokenSaved, address = saved.Address, name = saved.LocationName,
                version = saved.Version, tokenSaved, control = saved.Control, allowSensitive = saved.AllowSensitive, modelTools = saved.ModelTools,
                shared = saved.FollowShare, sharedBy = saved.SharedBy, sharedRevision = saved.SharedRevision
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new { state = "unreadable", problem = error.Message };
        }
    }

    /// <summary>Martlet's own cloudflared download in the data directory's tools folder: its version, whether it still matches
    /// the size and SHA-256 checked against GitHub's release at download, and whether Windows confirms Cloudflare's signature
    /// on it (Martlet runs it only when both hold).</summary>
    private static object CloudflaredStatus(string directory)
    {
        var own = Martlet.Messaging.CloudflareQuickTunnel.Installed(Path.Combine(directory, "tools"));
        if (own is null) return new { downloaded = false, version = (string?)null, verified = false, signed = false, signatureProblem = (string?)null };
        var problem = Martlet.Messaging.CloudflaredSignature.Problem(own.Path);
        return new { downloaded = true, version = own.Version, verified = own.Verified, signed = problem is null, signatureProblem = problem };
    }

    /// <summary>messaging.json in a data directory, read through its store (Martlet.Messaging.MessagingPreferences). The bot
    /// token lives in Windows Credential Manager and is never read here; paired chats are only counted.</summary>
    private static object MessagingStatus(JsonElement arguments)
    {
        var directory = DataDirectory(arguments);
        object NoTelegram() => new { connected = false, enabled = false, chats = 0 };
        object NoWhatsApp() => new { connected = false, enabled = false, chats = 0 };
        if (!File.Exists(Path.Combine(directory, Martlet.Messaging.MessagingPreferences.FileName)))
            return new { state = "none", telegram = NoTelegram(), whatsApp = NoWhatsApp(), cloudflared = CloudflaredStatus(directory) };
        try
        {
            var saved = Martlet.Messaging.MessagingPreferences.Read(directory);
            var telegram = saved.Telegram;
            var whatsApp = saved.WhatsApp;
            return new
            {
                state = "loaded",
                telegram = new
                {
                    connected = telegram.Connected, enabled = telegram.Enabled, bot = telegram.BotUsername, botName = telegram.BotName,
                    tokenSaved = telegram.CredentialId != Guid.Empty, chats = telegram.Chats.Count, speakReplies = telegram.SpeakReplies
                },
                whatsApp = new
                {
                    connected = whatsApp.Connected, enabled = whatsApp.Enabled, number = whatsApp.Number, name = whatsApp.Name, appId = whatsApp.AppId,
                    businessAccountId = whatsApp.BusinessAccountId, phoneNumberId = whatsApp.PhoneNumberId, port = whatsApp.Port,
                    publicAddress = whatsApp.PublicAddress, quickTunnel = whatsApp.PublicAddress.Length == 0,
                    secretsSaved = whatsApp.CredentialId != Guid.Empty, chats = whatsApp.Chats.Count, speakReplies = whatsApp.SpeakReplies
                },
                cloudflared = CloudflaredStatus(directory)
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new { state = "unreadable", problem = error.Message };
        }
    }
}
