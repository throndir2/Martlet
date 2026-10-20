using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using Martlet.Core.Sync;

namespace Martlet.Messaging;

/// <summary>Where a computer keeps a messaging app's secrets (Windows Credential Manager on the desktop): read, write and delete
/// by the app and the credential ID that messaging.json names.</summary>
public interface IMessagingSecretVault
{
    string? Read(MessagingApp app, Guid credentialId);
    bool Write(MessagingApp app, Guid credentialId, string secret);
    void Delete(MessagingApp app, Guid credentialId);
}

/// <summary>The part of Companion › Messaging that is the same on all the owner's computers (the household setting
/// <see cref="MessagingSharing.SharedKey"/>): for Telegram the bot and its token, for WhatsApp the Cloud API account and its
/// secrets (the setting's secret), and for both the paired chats and whether replies are also said aloud. Each computer keeps
/// whether Martlet answers the app there (only one program may read a bot's or a number's messages), where it keeps the
/// secrets, and WhatsApp's webhook port and own public address (they forward to that computer).</summary>
public sealed record MessagingShared
{
    public const int SchemaVersion1 = 1;

    public sealed record Telegram(string BotName, string BotUsername, IReadOnlyList<MessagingChat> Chats, bool SpeakReplies);

    public sealed record WhatsApp(string Name, string Number, string AppId, string BusinessAccountId, string PhoneNumberId,
        IReadOnlyList<MessagingChat> Chats, bool SpeakReplies);

    public int Version { get; init; } = SchemaVersion1;
    public required Telegram TelegramBot { get; init; }
    public required WhatsApp WhatsAppNumber { get; init; }
}

/// <summary>Carrying the shared part of messaging.json (<see cref="MessagingShared"/>) and the apps' secrets to the owner's other
/// computers and taking them there.</summary>
public static class MessagingSharing
{
    public const string SharedKey = "messaging";

    private sealed record Secrets(string? Telegram, string? WhatsApp);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 8 };

    public static MessagingShared From(MessagingPreferences preferences) => new()
    {
        TelegramBot = new(preferences.Telegram.BotName, preferences.Telegram.BotUsername, preferences.Telegram.Chats, preferences.Telegram.SpeakReplies),
        WhatsAppNumber = new(preferences.WhatsApp.Name, preferences.WhatsApp.Number, preferences.WhatsApp.AppId, preferences.WhatsApp.BusinessAccountId,
            preferences.WhatsApp.PhoneNumberId, preferences.WhatsApp.Chats, preferences.WhatsApp.SpeakReplies)
    };

    /// <summary>This computer's choices with the shared part taken from <paramref name="shared"/> and its secrets in the credentials
    /// <paramref name="telegram"/> and <paramref name="whatsApp"/>. An app no longer connected is off here too.</summary>
    public static MessagingPreferences Follow(MessagingPreferences local, MessagingShared shared, Guid telegram, Guid whatsApp)
    {
        var t = shared.TelegramBot;
        var w = shared.WhatsAppNumber;
        var nextTelegram = local.Telegram with { BotName = t.BotName, BotUsername = t.BotUsername, Chats = t.Chats, SpeakReplies = t.SpeakReplies };
        var nextWhatsApp = local.WhatsApp with
        {
            Name = w.Name, Number = w.Number, AppId = w.AppId, BusinessAccountId = w.BusinessAccountId, PhoneNumberId = w.PhoneNumberId,
            Chats = w.Chats, SpeakReplies = w.SpeakReplies
        };
        return local with
        {
            Telegram = nextTelegram with { CredentialId = nextTelegram.Connected ? telegram : Guid.Empty, Enabled = nextTelegram.Connected && local.Telegram.Enabled },
            WhatsApp = nextWhatsApp with { CredentialId = nextWhatsApp.Connected ? whatsApp : Guid.Empty, Enabled = nextWhatsApp.Connected && local.WhatsApp.Enabled }
        };
    }

    public static string Share(MessagingShared shared) => JsonSerializer.Serialize(shared, Json);

    /// <summary>A travelled setup, or null when it is a newer Martlet's or can't be read.</summary>
    public static MessagingShared? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var shared = JsonSerializer.Deserialize<MessagingShared>(value, Json);
            if (shared is not { Version: MessagingShared.SchemaVersion1, TelegramBot: { } t, WhatsAppNumber: { } w }) return null;
            if (t.BotName is null || t.BotUsername is null || t.Chats is null || w.Name is null || w.Number is null || w.AppId is null ||
                w.BusinessAccountId is null || w.PhoneNumberId is null || w.Chats is null || t.Chats.Concat(w.Chats).Any(c => c is not { Id.Length: > 0, Name: not null }))
                return null;
            return shared;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException) { return null; }
    }

    /// <summary>The apps' secrets as a shared setting's secret may carry them (one base64url text), and back.</summary>
    public static string? Pack(string? telegram, string? whatsApp)
    {
        if (telegram is null && whatsApp is null) return null;
        var packed = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Secrets(telegram, whatsApp), Json)));
        return packed.Length <= SharedSettings.MaximumSecretLength ? packed : null;
    }

    public static (string? Telegram, string? WhatsApp) Unpack(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return (null, null);
        try
        {
            var secrets = JsonSerializer.Deserialize<Secrets>(Encoding.UTF8.GetString(Base64Url.DecodeFromChars(secret)), Json);
            return (secrets?.Telegram, secrets?.WhatsApp);
        }
        catch (Exception error) when (error is FormatException or JsonException) { return (null, null); }
    }

    /// <summary>The setting backed by messaging.json in <paramref name="directory"/>, with the secrets in <paramref name="vault"/>.
    /// <paramref name="save"/> saves a change the way this computer does (the desktop's messaging service, which starts again an
    /// app that runs here and whose account, secrets or chats changed); without it the file is written.</summary>
    public static ISharedSection Section(string directory, IMessagingSecretVault vault,
        Func<Func<MessagingPreferences, MessagingPreferences>, bool>? save = null)
    {
        save ??= change => change(MessagingPreferences.Load(directory)).Save(directory);
        var path = Path.Combine(directory, MessagingPreferences.FileName);
        string? Secret(ChannelPreferences channel, MessagingApp app) =>
            channel.Connected && channel.CredentialId != Guid.Empty ? vault.Read(app, channel.CredentialId) : null;
        return new SecretSection(SharedKey, "Messaging apps", _ =>
        {
            var preferences = MessagingPreferences.Load(directory);
            var telegram = Secret(preferences.Telegram, MessagingApp.Telegram);
            var whatsApp = Secret(preferences.WhatsApp, MessagingApp.WhatsApp);
            // An app whose secrets this computer can't read isn't shared from here (they would be lost elsewhere).
            if (preferences.Telegram.Connected && telegram is null || preferences.WhatsApp.Connected && whatsApp is null)
                return Task.FromResult<SharedLocal?>(null);
            var packed = Pack(telegram, whatsApp);
            if (packed is null && (telegram is not null || whatsApp is not null)) return Task.FromResult<SharedLocal?>(null);
            DateTimeOffset? changed = File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) : null;
            return Task.FromResult<SharedLocal?>(new(Share(From(preferences)), packed, !File.Exists(path), changed));
        }, (setting, secret, _) =>
        {
            if (Parse(setting.Value) is not { } shared)
                return Task.FromResult(SharedApply.Waiting("It was set up on a newer Martlet; update this PC to use it."));
            var (telegram, whatsApp) = Unpack(secret);
            var wantTelegram = shared.TelegramBot.BotUsername.Length > 0;
            var wantWhatsApp = shared.WhatsAppNumber.PhoneNumberId.Length > 0;
            if (wantTelegram && telegram is null || wantWhatsApp && whatsApp is null)
                return Task.FromResult(SharedApply.Waiting("Its secrets haven't reached this PC yet."));
            var local = MessagingPreferences.Load(directory);
            List<(MessagingApp App, Guid Id)> written = [], gone = [];
            Guid Keep(MessagingApp app, bool wanted, string? value)
            {
                var id = local[app].CredentialId;
                if (!wanted)
                {
                    if (id != Guid.Empty) gone.Add((app, id));
                    return Guid.Empty;
                }
                if (id != Guid.Empty && vault.Read(app, id) == value) return id;
                var fresh = Guid.NewGuid();
                if (!vault.Write(app, fresh, value!)) throw new IOException($"{app}'s secrets couldn't be saved on this PC.");
                written.Add((app, fresh));
                if (id != Guid.Empty) gone.Add((app, id));
                return fresh;
            }
            try
            {
                var telegramId = Keep(MessagingApp.Telegram, wantTelegram, telegram);
                var whatsAppId = Keep(MessagingApp.WhatsApp, wantWhatsApp, whatsApp);
                if (!save(current => Follow(current, shared, telegramId, whatsAppId))) throw new IOException("It couldn't be saved on this PC.");
            }
            catch (IOException error)
            {
                foreach (var (app, id) in written) vault.Delete(app, id);
                return Task.FromResult(SharedApply.Waiting(error.Message));
            }
            foreach (var (app, id) in gone) vault.Delete(app, id);
            return Task.FromResult(SharedApply.Done);
        });
    }
}
