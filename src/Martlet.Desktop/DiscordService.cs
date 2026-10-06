using Martlet.Core.Settings;
using Martlet.Credentials.Windows;
using Martlet.Discord;

namespace Martlet.Desktop;

/// <summary>Martlet's Discord presence: the saved setup (<see cref="DiscordPreferences"/>), the bot token in Windows Credential
/// Manager and the bot connection hosted in this process. Text chat, voice and companion features live in their own partial
/// files (DiscordService.Text.cs, DiscordService.Voice.cs, DiscordService.Companion.cs) and attach to <see cref="Bot"/>.</summary>
internal sealed partial class DiscordService : IAsyncDisposable
{
    private readonly Lock gate = new();
    private readonly string? directory;
    private readonly WindowsCredentialStore vault;
    private DiscordPreferences preferences;

    internal DiscordService(string? directory, WindowsCredentialStore vault)
    {
        this.directory = directory;
        this.vault = vault;
        preferences = DiscordPreferences.Load(directory);
        Bot.Changed += _ => Changed?.Invoke();
        // Each feature attaches its handlers and adds its slash commands to Bot.Commands (one shared registration).
        AttachText();
        WatchAuthors();
        InitializeVoice();
    }

    internal DiscordBot Bot { get; } = new();
    internal DiscordPreferences Preferences { get { lock (gate) return preferences; } }
    internal DiscordBotStatus Status => Bot.Status;

    /// <summary>Answers Discord turns with Martlet's persona and Thinking route; null until the reply engine is wired.</summary>
    internal IDiscordReplyEngine? Replies { get; set; }

    /// <summary>Raised off the UI thread when the setup or the bot's status change.</summary>
    internal event Action? Changed;

    internal bool Save(Func<DiscordPreferences, DiscordPreferences> update)
    {
        DiscordPreferences next;
        lock (gate)
        {
            next = update(preferences);
            if (!next.Save(directory)) return false;
            preferences = next;
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>Saves a bot token (replacing any earlier one) and the application ID it belongs to.</summary>
    internal CredentialError SaveToken(SecretLease token)
    {
        ulong applicationId = 0;
        token.Use(value => applicationId = DiscordInvite.ApplicationIdFromToken(value));
        if (applicationId == 0) return CredentialError.InvalidInput;
        var credentialId = Guid.NewGuid();
        var error = vault.WriteDiscordBotToken(credentialId, token);
        if (error != CredentialError.None) return error;
        var old = Preferences.CredentialId;
        if (!Save(saved => saved with { ApplicationId = applicationId, CredentialId = credentialId }))
        {
            vault.DeleteDiscordBotToken(credentialId);
            return CredentialError.Unavailable;
        }
        if (old != Guid.Empty) vault.DeleteDiscordBotToken(old);
        return CredentialError.None;
    }

    /// <summary>Disconnects and forgets the bot token and application (people and chat rules stay).</summary>
    internal async Task ForgetAsync()
    {
        await Bot.StopAsync().ConfigureAwait(false);
        var old = Preferences.CredentialId;
        Save(saved => saved with { ApplicationId = 0, CredentialId = Guid.Empty, Enabled = false });
        if (old != Guid.Empty) vault.DeleteDiscordBotToken(old);
    }

    /// <summary>Connects the bot with the saved token. Returns a problem to show, or null once it is connecting.</summary>
    internal async Task<string?> StartAsync(CancellationToken cancellation = default)
    {
        var saved = Preferences;
        if (!saved.Configured) return "Paste the bot token from the Discord Developer Portal first.";
        using var read = vault.ReadDiscordBotToken(saved.CredentialId);
        if (read.Error != CredentialError.None || read.Secret is null) return CredentialMessages.Describe(read.Error);
        string? token = null;
        read.Secret.Use(value => token = value.ToString());
        await Bot.StartAsync(token!, cancellation).ConfigureAwait(false);
        return Bot.Status.Problem;
    }

    internal Task StopAsync() => Bot.StopAsync();

    /// <summary>Connects at startup when the owner turned the bot on.</summary>
    internal async Task StartIfEnabledAsync(CancellationToken cancellation)
    {
        if (!Preferences.Enabled || !Preferences.Configured) return;
        var problem = await StartAsync(cancellation).ConfigureAwait(false);
        if (problem is not null) ErrorLog.Warn($"Discord bot did not start: {problem}");
    }

    public async ValueTask DisposeAsync()
    {
        DisposeCompanion();
        await Bot.DisposeAsync().ConfigureAwait(false);
    }
}
