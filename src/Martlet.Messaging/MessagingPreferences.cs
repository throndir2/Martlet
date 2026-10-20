using System.Text.Json;
using System.Text.Json.Serialization;

namespace Martlet.Messaging;

/// <summary>One messaging app's choices (messaging.json): whether Martlet answers it on this PC, which Credential Manager entry
/// keeps its secrets on this PC, the paired chats and whether replies are also said aloud. The account, its secrets, the paired
/// chats and speaking replies are the same on all the owner's computers (<see cref="MessagingSharing"/>); whether Martlet answers
/// the app on this PC stays with this PC, because only one program may read a bot's or a number's messages.</summary>
public abstract record ChannelPreferences
{
    /// <summary>Martlet answers the app's paired chats while it runs on this PC.</summary>
    public bool Enabled { get; init; }
    /// <summary>Which Windows Credential Manager entry holds the secrets (Martlet/v3/messaging/&lt;app&gt;/&lt;id&gt;).</summary>
    public Guid CredentialId { get; init; }
    /// <summary>The chats paired with a code: the only chats Martlet answers.</summary>
    public IReadOnlyList<MessagingChat> Chats { get; init; } = [];
    /// <summary>Also say replies to messages aloud on this PC (off: you are usually away from it).</summary>
    public bool SpeakReplies { get; init; }
    /// <summary>Set up (a bot or a number is connected).</summary>
    [JsonIgnore] public abstract bool Connected { get; }
    /// <summary>How the owner's chats find it: @bot for Telegram, the number for WhatsApp.</summary>
    [JsonIgnore] public abstract string Handle { get; }
}

/// <summary>Companion › Messaging's Telegram choices. The bot token lives in Windows Credential Manager, never here.</summary>
public sealed record TelegramPreferences : ChannelPreferences
{
    public string BotName { get; init; } = "";
    public string BotUsername { get; init; } = "";
    [JsonIgnore] public override bool Connected => BotUsername.Length > 0;
    [JsonIgnore] public override string Handle => "@" + BotUsername;
}

/// <summary>Companion › Messaging's WhatsApp choices: the WhatsApp Cloud API account (IDs only; the access token and app secret
/// live in Windows Credential Manager), and on this PC the local webhook port and an optional public address of the owner's own
/// (empty: a Cloudflare quick tunnel). Meta delivers each number's messages to one webhook, so the port and address stay on this PC.</summary>
public sealed record WhatsAppPreferences : ChannelPreferences
{
    public string Name { get; init; } = "";
    /// <summary>The business phone number as WhatsApp shows it (+1 555-0100).</summary>
    public string Number { get; init; } = "";
    public string AppId { get; init; } = "";
    public string BusinessAccountId { get; init; } = "";
    public string PhoneNumberId { get; init; } = "";
    /// <summary>The localhost port Martlet's webhook listens on (kept so an own public address can forward to it).</summary>
    public int Port { get; init; }
    /// <summary>The owner's own public HTTPS address forwarding to <see cref="Port"/>; empty uses a Cloudflare quick tunnel.</summary>
    public string PublicAddress { get; init; } = "";
    [JsonIgnore] public override bool Connected => PhoneNumberId.Length > 0;
    [JsonIgnore] public override string Handle => Number;
    /// <summary>The number's digits, as wa.me links take them.</summary>
    [JsonIgnore] public string Digits => new([.. Number.Where(char.IsAsciiDigit)]);
}

public sealed record MessagingPreferences
{
    public const string FileName = "messaging.json";
    public TelegramPreferences Telegram { get; init; } = new();
    public WhatsAppPreferences WhatsApp { get; init; } = new();

    public ChannelPreferences this[MessagingApp app] => app == MessagingApp.WhatsApp ? WhatsApp : Telegram;

    public MessagingPreferences With(MessagingApp app, Func<ChannelPreferences, ChannelPreferences> change) => app == MessagingApp.WhatsApp
        ? this with { WhatsApp = (WhatsAppPreferences)change(WhatsApp) }
        : this with { Telegram = (TelegramPreferences)change(Telegram) };

    private static IReadOnlyList<MessagingChat> Clean(IReadOnlyList<MessagingChat>? chats) =>
        [.. (chats ?? []).Where(chat => chat is { Id.Length: > 0 }).Select(chat => chat with { Name = chat.Name ?? chat.Id })];

    /// <summary>The saved choices, or the defaults when there are none or they can't be read.</summary>
    public static MessagingPreferences Load(string? directory)
    {
        if (directory is null) return new();
        try { return Read(directory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    /// <summary>The saved choices, or the defaults when there are none; throws <see cref="IOException"/>,
    /// <see cref="UnauthorizedAccessException"/> or <see cref="JsonException"/> when the file can't be read.</summary>
    public static MessagingPreferences Read(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path)) return new();
        var loaded = JsonSerializer.Deserialize<MessagingPreferences>(File.ReadAllText(path)) ?? new();
        var telegram = loaded.Telegram ?? new();
        var whatsApp = loaded.WhatsApp ?? new();
        return new()
        {
            Telegram = telegram with { BotName = telegram.BotName ?? "", BotUsername = telegram.BotUsername ?? "", Chats = Clean(telegram.Chats) },
            WhatsApp = whatsApp with
            {
                Name = whatsApp.Name ?? "", Number = whatsApp.Number ?? "", AppId = whatsApp.AppId ?? "",
                BusinessAccountId = whatsApp.BusinessAccountId ?? "", PhoneNumberId = whatsApp.PhoneNumberId ?? "",
                PublicAddress = whatsApp.PublicAddress ?? "", Port = whatsApp.Port is > 0 and < 65536 ? whatsApp.Port : 0,
                Chats = Clean(whatsApp.Chats)
            }
        };
    }

    public bool Save(string? directory)
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
