using System.IO;
using System.Text.Json;
using Martlet.Conversation;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;
using Martlet.Messaging;

namespace Martlet.Desktop;

/// <summary>Martlet in messaging apps: runs each app's <see cref="MessagingBridge"/> (Telegram's bot, WhatsApp's business
/// number) while it is turned on and its secrets are saved, keeps the paired chats in messaging.json and hands each message to
/// <see cref="Answer"/> (the conversation).</summary>
internal sealed class MessagingService : IDisposable
{
    internal const string TelegramKey = "telegram";
    internal const string WhatsAppKey = "whatsapp";
    private readonly string? directory;
    private readonly WindowsCredentialStore vault;
    private readonly Func<string, IMessagingTransport> telegram;
    private readonly Func<WhatsAppSecrets, WhatsAppPreferences, IMessagingTransport> whatsApp;
    private readonly Func<WhatsAppSecrets, WhatsAppCloud> cloud;
    private readonly object gate = new();
    private readonly Dictionary<MessagingApp, (MessagingBridge Bridge, CancellationTokenSource? Running)> channels = [];
    private MessagingPreferences preferences;

    internal MessagingService(string? directory, WindowsCredentialStore? vault = null, Func<string, IMessagingTransport>? transports = null,
        Func<WhatsAppSecrets, WhatsAppPreferences, IMessagingTransport>? whatsApp = null, Func<WhatsAppSecrets, WhatsAppCloud>? cloud = null)
    {
        this.directory = directory;
        this.vault = vault ?? new WindowsCredentialStore();
        telegram = transports ?? (token => new TelegramTransport(token, api: FixtureApi("MARTLET_TELEGRAM_API")));
        this.cloud = cloud ?? (secrets => new WhatsAppCloud(secrets, api: FixtureApi("MARTLET_WHATSAPP_API")));
        this.whatsApp = whatsApp ?? DefaultWhatsApp;
        preferences = MessagingPreferences.Load(directory);
    }

    /// <summary>MARTLET_TELEGRAM_API / MARTLET_WHATSAPP_API: a local fake Bot or Graph API (http://127.0.0.1 only) for MCP
    /// verification; never a real server.</summary>
    private static Uri? FixtureApi(string variable) =>
        Uri.TryCreate(Environment.GetEnvironmentVariable(variable), UriKind.Absolute, out var api) && api.IsLoopback ? api : null;

    /// <summary>Where Martlet keeps cloudflared when it downloads it.</summary>
    internal string? ToolsDirectory => directory is null ? null : Path.Combine(directory, "tools");

    internal string? Cloudflared => CloudflareQuickTunnel.Find(ToolsDirectory);

    /// <summary>Martlet's own cloudflared download: its version and whether it still matches the SHA-256 checked at download.</summary>
    internal CloudflaredInstall? OwnCloudflared => ToolsDirectory is { } tools ? CloudflareQuickTunnel.Installed(tools) : null;

    private IMessagingTransport DefaultWhatsApp(WhatsAppSecrets secrets, WhatsAppPreferences saved)
    {
        IPublicAddress address;
        if (saved.PublicAddress.Length > 0) address = new FixedPublicAddress(new Uri(saved.PublicAddress));
        else address = new CloudflareQuickTunnel(Cloudflared ?? throw new ArgumentException("cloudflared isn't on this PC."));
        return new WhatsAppTransport(secrets, new(saved.AppId, saved.BusinessAccountId, saved.PhoneNumberId, saved.Number, saved.Name),
            saved.Port, address, api: FixtureApi("MARTLET_WHATSAPP_API"));
    }

    /// <summary>Answers one message the way the conversation does; null while Martlet can't talk.</summary>
    internal Func<MessagingApp, InboundMessage, CancellationToken, Task<string>>? Answer { get; set; }
    /// <summary>The record of conversations: replies' message IDs go to their exchanges, and while the bot runs, deleting or
    /// editing a Telegram exchange there deletes or edits it in Telegram too.</summary>
    internal DesktopConversationHistory? History { get; set; }
    /// <summary>Raised (on any thread) when the preferences, a bridge's status or a pairing code changed.</summary>
    internal event Action? Changed;

    internal MessagingPreferences Preferences { get { lock (gate) return preferences; } }

    internal MessagingStatus Status(MessagingApp app)
    {
        lock (gate) return channels.TryGetValue(app, out var channel) ? channel.Bridge.Status : new(MessagingState.Off);
    }

    internal PairingCode? Pairing(MessagingApp app)
    {
        lock (gate) return channels.TryGetValue(app, out var channel) ? channel.Bridge.Pairing : null;
    }

    internal bool Running(MessagingApp app)
    {
        lock (gate) return channels.TryGetValue(app, out var channel) && channel.Running is not null;
    }

    internal bool SecretsSaved(MessagingApp app) => ReadSecret(app) is not null;

    /// <summary>The saved WhatsApp access token and app secret (to reconnect without pasting them again).</summary>
    internal WhatsAppSecrets? SavedWhatsAppSecrets() => WhatsAppSecrets.Unpack(ReadSecret(MessagingApp.WhatsApp));

    private static string Key(MessagingApp app) => app == MessagingApp.WhatsApp ? WhatsAppKey : TelegramKey;

    internal static string Name(MessagingApp app) => app == MessagingApp.WhatsApp ? "WhatsApp" : "Telegram";

    private string? ReadSecret(MessagingApp app)
    {
        var id = Preferences[app].CredentialId;
        return id != Guid.Empty && vault.ReadMessagingToken(Key(app), id, out var secret) == CredentialError.None ? secret : null;
    }

    /// <summary>Checks a bot token with Telegram, saves it in Windows Credential Manager, turns the bot on and starts it.</summary>
    internal async Task<BotIdentity> ConnectTelegramAsync(string token, CancellationToken cancellation)
    {
        token = token.Trim();
        if (!TelegramTransport.IsToken(token))
            throw new ArgumentException("That doesn't look like a bot token: BotFather gives one like 123456789:AAH...");
        BotIdentity bot;
        using (var probe = telegram(token)) bot = await probe.ConnectAsync(cancellation).ConfigureAwait(false);
        Save(MessagingApp.Telegram, token, saved => ((TelegramPreferences)saved) with { BotName = bot.Name, BotUsername = bot.Username });
        return bot;
    }

    /// <summary>Checks a WhatsApp Cloud API access token and app secret with Meta, finds the business account and phone number
    /// (the given IDs, or the token's own when empty), saves the secrets in Windows Credential Manager, turns WhatsApp on and
    /// starts it: the webhook, its public address and the app's webhook registration.</summary>
    internal async Task<WhatsAppAccount> ConnectWhatsAppAsync(string accessToken, string appSecret, string? phoneNumberId, string? businessAccountId,
        string? publicAddress, CancellationToken cancellation)
    {
        var secrets = new WhatsAppSecrets(accessToken.Trim(), appSecret.Trim());
        publicAddress = publicAddress?.Trim() ?? "";
        if (publicAddress.Length > 0 && (!Uri.TryCreate(publicAddress, UriKind.Absolute, out var address) ||
                !(address.Scheme == Uri.UriSchemeHttps || address.IsLoopback && FixtureApi("MARTLET_WHATSAPP_API") is not null)))
            throw new ArgumentException("The public address must be an https:// address that forwards to this PC.");
        if (publicAddress.Length == 0 && Cloudflared is null)
            throw new InvalidOperationException("Martlet needs Cloudflare's free cloudflared to give WhatsApp an address, so press Get cloudflared first.");
        WhatsAppAccount account;
        using (var probe = cloud(secrets)) account = await probe.DescribeAsync(phoneNumberId, businessAccountId, cancellation).ConfigureAwait(false);
        var port = Preferences.WhatsApp.Port is > 0 and var kept ? kept : WhatsAppWebhook.FreePort();
        Save(MessagingApp.WhatsApp, secrets.Pack(), saved => ((WhatsAppPreferences)saved) with
        {
            Name = account.Name, Number = account.Number, AppId = account.AppId, BusinessAccountId = account.BusinessAccountId,
            PhoneNumberId = account.PhoneNumberId, Port = port, PublicAddress = publicAddress
        });
        return account;
    }

    private void Save(MessagingApp app, string secret, Func<ChannelPreferences, ChannelPreferences> describe)
    {
        Stop(app);
        var fresh = Guid.NewGuid();
        var error = vault.WriteMessagingToken(Key(app), fresh, secret);
        if (error != CredentialError.None) throw new InvalidOperationException($"Couldn't save the secrets in Windows Credential Manager ({error}).");
        var previous = Preferences[app].CredentialId;
        if (previous != Guid.Empty) vault.DeleteMessagingToken(Key(app), previous);
        Update(saved => saved.With(app, channel => describe(channel) with { Enabled = true, CredentialId = fresh }));
        Start(app);
    }

    /// <summary>Answer the app on this PC: on starts it (with saved secrets), off stops it and keeps them.</summary>
    internal void SetEnabled(MessagingApp app, bool enabled)
    {
        Update(saved => saved.With(app, channel => channel with { Enabled = enabled }));
        if (enabled) Start(app);
        else Stop(app);
    }

    internal void SetSpeakReplies(MessagingApp app, bool speak) => Update(saved => saved.With(app, channel => channel with { SpeakReplies = speak }));

    /// <summary>Starts every app that is turned on and has its secrets saved.</summary>
    internal void Start()
    {
        foreach (var app in Enum.GetValues<MessagingApp>()) Start(app);
    }

    internal void Stop()
    {
        foreach (var app in Enum.GetValues<MessagingApp>()) Stop(app);
    }

    /// <summary>Starts the app when it is turned on and its secrets are saved; does nothing when it already runs.</summary>
    internal bool Start(MessagingApp app)
    {
        var saved = Preferences[app];
        if (!saved.Enabled || !saved.Connected) return false;
        if (ReadSecret(app) is not { } secret) return false;
        var name = Name(app);
        lock (gate)
        {
            if (channels.TryGetValue(app, out var current) && current.Running is not null) return true;
            IMessagingTransport transport;
            try
            {
                if (app == MessagingApp.WhatsApp)
                {
                    var whatsAppSaved = (WhatsAppPreferences)saved;
                    if (whatsAppSaved.Port == 0)
                    {
                        whatsAppSaved = whatsAppSaved with { Port = WhatsAppWebhook.FreePort() };
                        preferences = preferences with { WhatsApp = whatsAppSaved };
                        preferences.Save(directory);
                    }
                    transport = whatsApp(WhatsAppSecrets.Unpack(secret) ?? throw new ArgumentException("The saved WhatsApp secrets are unreadable."), whatsAppSaved);
                }
                else transport = telegram(secret);
            }
            catch (ArgumentException error)
            {
                ErrorLog.Warn($"{name} can't start: {error.Message}");
                return false;
            }
            var next = new MessagingBridge(transport, saved.Chats, (message, token) => AnswerAsync(app, message, token));
            next.StatusChanged += _ => Changed?.Invoke();
            next.Failed += error => ErrorLog.Warn($"{name}: {error.GetType().Name}; Martlet goes on.");
            if (transport is WhatsAppTransport moving)
                moving.PortChanged += port =>
                {
                    ErrorLog.Info($"WhatsApp: the saved port was in use; the webhook moved to port {port}.");
                    Update(current => current with { WhatsApp = current.WhatsApp with { Port = port } });
                };
            next.Replied += (message, ids) =>
            {
                if (message.MessageId is { } id && ids.Count > 0)
                    History?.AttachReplies(message.App.ToString().ToLowerInvariant(), message.ChatId, id, ids);
            };
            var platform = transport is IMessagingMessageControl control ? new MessagingPlatform(transport.App, control) : null;
            if (platform is not null) History?.Platforms.Connect(platform);
            next.Paired += chat =>
            {
                Update(current => current.With(app, channel => channel with { Chats = [.. channel.Chats.Where(c => c.Id != chat.Id), chat] }));
                ErrorLog.Info($"{name}: a new chat paired with Martlet.");
            };
            var stop = new CancellationTokenSource();
            channels[app] = (next, stop);
            Task.Run(async () =>
            {
                try { await next.RunAsync(stop.Token).ConfigureAwait(false); }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    ErrorLog.Warn($"{name} stopped: {error.GetType().Name}.");
                }
                finally
                {
                    if (platform is not null) History?.Platforms.Disconnect(platform);
                    transport.Dispose();
                    lock (gate)
                        if (channels.TryGetValue(app, out var still) && ReferenceEquals(still.Running, stop))
                            channels[app] = (next, null);
                    stop.Dispose();
                    Changed?.Invoke();
                }
            });
        }
        ErrorLog.Info($"{name}: Martlet answers {saved.Handle} on this PC ({saved.Chats.Count} paired chat{(saved.Chats.Count == 1 ? "" : "s")}).");
        Changed?.Invoke();
        return true;
    }

    internal void Stop(MessagingApp app)
    {
        CancellationTokenSource? stop = null;
        lock (gate)
            if (channels.TryGetValue(app, out var channel) && channel.Running is not null)
            {
                stop = channel.Running;
                channels[app] = (channel.Bridge, null);
            }
        if (stop is null) return;
        try { stop.Cancel(); }
        catch (ObjectDisposedException) { }
        Changed?.Invoke();
    }

    /// <summary>Stops the app, deletes its secrets and forgets its account and paired chats.</summary>
    internal void Disconnect(MessagingApp app)
    {
        Stop(app);
        lock (gate) channels.Remove(app);
        var id = Preferences[app].CredentialId;
        if (id != Guid.Empty) vault.DeleteMessagingToken(Key(app), id);
        Update(saved => app == MessagingApp.WhatsApp ? saved with { WhatsApp = new() } : saved with { Telegram = new() });
    }

    internal PairingCode? StartPairing(MessagingApp app)
    {
        PairingCode? code;
        lock (gate) code = channels.TryGetValue(app, out var channel) && channel.Running is not null ? channel.Bridge.StartPairing() : null;
        Changed?.Invoke();
        return code;
    }

    internal void CancelPairing(MessagingApp app)
    {
        lock (gate) if (channels.TryGetValue(app, out var channel)) channel.Bridge.CancelPairing();
        Changed?.Invoke();
    }

    internal void RemoveChat(MessagingApp app, string id)
    {
        lock (gate) if (channels.TryGetValue(app, out var channel)) channel.Bridge.Forget(id);
        Update(saved => saved.With(app, channel => channel with { Chats = [.. channel.Chats.Where(chat => chat.Id != id)] }));
    }

    private Task<string> AnswerAsync(MessagingApp app, InboundMessage message, CancellationToken cancellation) =>
        Answer is { } answer ? answer(app, message, cancellation) : Task.FromResult("Martlet can't talk right now, so try again once it's running.");

    private void Update(Func<MessagingPreferences, MessagingPreferences> change)
    {
        lock (gate)
        {
            preferences = change(preferences);
            if (!preferences.Save(directory) && directory is not null) ErrorLog.Warn("Couldn't save messaging.json.");
        }
        Changed?.Invoke();
    }

    /// <summary>Takes another computer's messaging setup (<see cref="MessagingSharing"/>): saves it, and starts again an app that
    /// runs here and whose account, secrets or paired chats changed (or stops one that is no longer connected). False when it
    /// couldn't be saved.</summary>
    internal bool Follow(Func<MessagingPreferences, MessagingPreferences> change)
    {
        MessagingPreferences before, after;
        lock (gate)
        {
            before = preferences;
            after = change(preferences);
            if (!after.Save(directory)) return false;
            preferences = after;
        }
        foreach (var app in Enum.GetValues<MessagingApp>())
            if (before[app].CredentialId != after[app].CredentialId || !before[app].Chats.SequenceEqual(after[app].Chats) ||
                before[app].Handle != after[app].Handle)
            {
                var running = Running(app);
                Stop(app);
                lock (gate) channels.Remove(app);
                if (running) Start(app);
            }
        Changed?.Invoke();
        return true;
    }

    public void Dispose() => Stop();
}

/// <summary>Deleting and editing messages in a messaging app for the record of conversations (<see cref="PlatformChanges"/>),
/// through the running bot's transport. Telegram allows about one change a second per chat, so changes go at most two a
/// second; its "slow down" answers are waited out.</summary>
internal sealed class MessagingPlatform(MessagingApp app, IMessagingMessageControl control) : IPlatformMessages
{
    public string App { get; } = app.ToString().ToLowerInvariant();
    public TimeSpan Interval => TimeSpan.FromMilliseconds(500);

    public async Task ApplyAsync(PlatformChange change, CancellationToken token)
    {
        try
        {
            if (change.Kind == PlatformChangeKind.Edit)
                await control.EditMessageAsync(change.Chat, change.Message, change.Text ?? "", token).ConfigureAwait(false);
            else await control.DeleteMessageAsync(change.Chat, change.Message, token).ConfigureAwait(false);
        }
        catch (MessagingException error)
        {
            throw error.Failure switch
            {
                MessagingFailure.RateLimited => new PlatformChangeException(PlatformFailure.RateLimited, error.Message, error.RetryAfter),
                MessagingFailure.Protocol => new PlatformChangeException(PlatformFailure.Refused, error.Message),
                _ => new PlatformChangeException(PlatformFailure.Unavailable, error.Message)
            };
        }
    }
}
