using System.IO;
using System.Text.Json;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;
using Martlet.Messaging;

namespace Martlet.Desktop;

/// <summary>Companion › Messaging's Telegram choices on this PC. The bot token lives in Windows Credential Manager, never here.
/// Only one program may read a bot's messages, so this stays on this PC (it is not synced to the other computers).</summary>
internal sealed record TelegramPreferences
{
    /// <summary>Martlet answers the bot's paired chats while it runs on this PC.</summary>
    public bool Enabled { get; init; }
    public string BotName { get; init; } = "";
    public string BotUsername { get; init; } = "";
    /// <summary>Which Windows Credential Manager entry holds this bot's token (Martlet/v3/messaging/telegram/&lt;id&gt;).</summary>
    public Guid CredentialId { get; init; }
    /// <summary>The chats paired with a code: the only chats Martlet answers.</summary>
    public IReadOnlyList<MessagingChat> Chats { get; init; } = [];
    /// <summary>Also say replies to messages aloud on this PC (off: you are usually away from it).</summary>
    public bool SpeakReplies { get; init; }
}

internal sealed record MessagingPreferences
{
    internal const string FileName = "messaging.json";
    public TelegramPreferences Telegram { get; init; } = new();

    internal static MessagingPreferences Load(string? directory)
    {
        if (directory is null) return new();
        try
        {
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path)) return new();
            var loaded = JsonSerializer.Deserialize<MessagingPreferences>(File.ReadAllText(path)) ?? new();
            var telegram = loaded.Telegram ?? new();
            return new()
            {
                Telegram = telegram with
                {
                    BotName = telegram.BotName ?? "", BotUsername = telegram.BotUsername ?? "",
                    Chats = [.. (telegram.Chats ?? []).Where(chat => chat is { Id.Length: > 0 }).Select(chat => chat with { Name = chat.Name ?? chat.Id })]
                }
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
            var temporary = Path.Combine(directory, $"messaging.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
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

/// <summary>Martlet in messaging apps: runs the Telegram bot (<see cref="MessagingBridge"/>) while it is turned on and its
/// token is saved, keeps the paired chats in messaging.json and hands each message to <see cref="Answer"/> (the conversation).</summary>
internal sealed class MessagingService : IDisposable
{
    internal const string TelegramKey = "telegram";
    private readonly string? directory;
    private readonly WindowsCredentialStore vault;
    private readonly Func<string, IMessagingTransport> transports;
    private readonly object gate = new();
    private MessagingBridge? bridge;
    private CancellationTokenSource? running;
    private MessagingPreferences preferences;

    internal MessagingService(string? directory, WindowsCredentialStore? vault = null, Func<string, IMessagingTransport>? transports = null)
    {
        this.directory = directory;
        this.vault = vault ?? new WindowsCredentialStore();
        this.transports = transports ?? (token => new TelegramTransport(token, api: FixtureApi()));
        preferences = MessagingPreferences.Load(directory);
    }

    /// <summary>MARTLET_TELEGRAM_API: a local fake Telegram Bot API (http://127.0.0.1 only) for MCP verification; never a real
    /// server.</summary>
    private static Uri? FixtureApi() =>
        Uri.TryCreate(Environment.GetEnvironmentVariable("MARTLET_TELEGRAM_API"), UriKind.Absolute, out var api) && api.IsLoopback ? api : null;

    /// <summary>Answers one message the way the conversation does; null while Martlet can't talk.</summary>
    internal Func<InboundMessage, CancellationToken, Task<string>>? Answer { get; set; }
    /// <summary>Raised (on any thread) when the preferences, the bot's status or the pairing code changed.</summary>
    internal event Action? Changed;

    internal MessagingPreferences Preferences { get { lock (gate) return preferences; } }
    internal MessagingStatus Status { get { lock (gate) return bridge?.Status ?? new(MessagingState.Off); } }
    internal PairingCode? Pairing { get { lock (gate) return bridge?.Pairing; } }
    internal bool Running { get { lock (gate) return running is not null; } }
    internal bool TokenSaved => ReadToken() is not null;

    private string? ReadToken()
    {
        var id = Preferences.Telegram.CredentialId;
        return id != Guid.Empty && vault.ReadMessagingToken(TelegramKey, id, out var token) == CredentialError.None ? token : null;
    }

    /// <summary>Checks a bot token with Telegram, saves it in Windows Credential Manager, turns the bot on and starts it.</summary>
    internal async Task<BotIdentity> ConnectTelegramAsync(string token, CancellationToken cancellation)
    {
        token = token.Trim();
        if (!TelegramTransport.IsToken(token))
            throw new ArgumentException("That doesn't look like a bot token. BotFather gives one like 123456789:AAH...");
        BotIdentity bot;
        using (var probe = transports(token)) bot = await probe.ConnectAsync(cancellation).ConfigureAwait(false);
        Stop();
        var fresh = Guid.NewGuid();
        var error = vault.WriteMessagingToken(TelegramKey, fresh, token);
        if (error != CredentialError.None) throw new InvalidOperationException($"Couldn't save the token in Windows Credential Manager ({error}).");
        var previous = Preferences.Telegram.CredentialId;
        if (previous != Guid.Empty) vault.DeleteMessagingToken(TelegramKey, previous);
        Update(saved => saved with
        {
            Telegram = saved.Telegram with { Enabled = true, BotName = bot.Name, BotUsername = bot.Username, CredentialId = fresh }
        });
        Start();
        return bot;
    }

    /// <summary>Answer Telegram on this PC: on starts the bot (with a saved token), off stops it and keeps the token.</summary>
    internal void SetEnabled(bool enabled)
    {
        Update(saved => saved with { Telegram = saved.Telegram with { Enabled = enabled } });
        if (enabled) Start();
        else Stop();
    }

    internal void SetSpeakReplies(bool speak) => Update(saved => saved with { Telegram = saved.Telegram with { SpeakReplies = speak } });

    /// <summary>Starts the bot when it is turned on and its token is saved; does nothing when it already runs.</summary>
    internal bool Start()
    {
        var saved = Preferences.Telegram;
        if (!saved.Enabled) return false;
        if (ReadToken() is not { } token) return false;
        lock (gate)
        {
            if (running is not null) return true;
            IMessagingTransport transport;
            try { transport = transports(token); }
            catch (ArgumentException) { return false; }
            var next = new MessagingBridge(transport, saved.Chats, AnswerAsync);
            next.StatusChanged += _ => Changed?.Invoke();
            next.Paired += chat =>
            {
                Update(current => current with
                {
                    Telegram = current.Telegram with { Chats = [.. current.Telegram.Chats.Where(c => c.Id != chat.Id), chat] }
                });
                ErrorLog.Info("Telegram: a new chat paired with Martlet.");
            };
            bridge = next;
            var stop = running = new CancellationTokenSource();
            Task.Run(async () =>
            {
                try { await next.RunAsync(stop.Token).ConfigureAwait(false); }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    ErrorLog.Warn($"Telegram stopped: {error.GetType().Name}.");
                }
                finally
                {
                    transport.Dispose();
                    lock (gate)
                        if (ReferenceEquals(running, stop))
                        {
                            running = null;
                            stop.Dispose();
                        }
                    Changed?.Invoke();
                }
            });
        }
        ErrorLog.Info($"Telegram: Martlet answers @{saved.BotUsername} on this PC ({saved.Chats.Count} paired chat{(saved.Chats.Count == 1 ? "" : "s")}).");
        Changed?.Invoke();
        return true;
    }

    internal void Stop()
    {
        CancellationTokenSource? stop;
        lock (gate)
        {
            stop = running;
            running = null;
        }
        if (stop is null) return;
        stop.Cancel();
        Changed?.Invoke();
    }

    /// <summary>Stops the bot, deletes its token and forgets the bot and its paired chats.</summary>
    internal void Disconnect()
    {
        Stop();
        lock (gate) bridge = null;
        var id = Preferences.Telegram.CredentialId;
        if (id != Guid.Empty) vault.DeleteMessagingToken(TelegramKey, id);
        Update(saved => saved with { Telegram = new() });
    }

    internal PairingCode? StartPairing()
    {
        PairingCode? code;
        lock (gate) code = running is null ? null : bridge?.StartPairing();
        Changed?.Invoke();
        return code;
    }

    internal void CancelPairing()
    {
        lock (gate) bridge?.CancelPairing();
        Changed?.Invoke();
    }

    internal void RemoveChat(string id)
    {
        lock (gate) bridge?.Forget(id);
        Update(saved => saved with { Telegram = saved.Telegram with { Chats = [.. saved.Telegram.Chats.Where(chat => chat.Id != id)] } });
    }

    private Task<string> AnswerAsync(InboundMessage message, CancellationToken cancellation) =>
        Answer is { } answer ? answer(message, cancellation) : Task.FromResult("Martlet can't talk right now. Try again once it's running.");

    private void Update(Func<MessagingPreferences, MessagingPreferences> change)
    {
        lock (gate)
        {
            preferences = change(preferences);
            if (!preferences.Save(directory) && directory is not null) ErrorLog.Warn("Couldn't save messaging.json.");
        }
        Changed?.Invoke();
    }

    public void Dispose() => Stop();
}
