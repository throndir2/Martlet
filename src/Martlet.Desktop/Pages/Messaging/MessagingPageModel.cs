using Martlet.Messaging;

namespace Martlet.Desktop;

/// <summary>Companion › Messaging's decisions, without WPF. The page makes one each time it draws: it reads the saved messaging
/// setup and each app's state once, and says what Now and each app's status lines show. <see cref="MessagingPage"/> draws it, and
/// <see cref="IMessagingPageServices"/> is all it reads in the app.</summary>
internal sealed class MessagingPageModel
{
    /// <summary>The apps in the order the page shows them.</summary>
    internal static readonly MessagingApp[] AppsInOrder = [MessagingApp.Telegram, MessagingApp.WhatsApp];

    internal MessagingPageModel(IMessagingPageServices services)
    {
        Saved = services.Preferences;
        Apps = [.. AppsInOrder.Select(app => (MessagingService.Name(app), Saved[app], services.Status(app), services.Running(app)))];
    }

    /// <summary>The saved messaging setup (messaging.json).</summary>
    internal MessagingPreferences Saved { get; }

    /// <summary>Each app with its saved setup, its bridge's state and whether it runs on this PC.</summary>
    internal IReadOnlyList<(string Name, ChannelPreferences Saved, MessagingStatus Status, bool Running)> Apps { get; }

    /// <summary>Now's line (MessagingNow).</summary>
    internal string NowText => Now(Apps);

    /// <summary>Now's problem line (MessagingNowProblem), or null.</summary>
    internal string? ProblemText => Problem(Apps);

    /// <summary>The status line of one app's card (MessagingStatus, MessagingWhatsAppStatus).</summary>
    internal string StatusLine(MessagingApp app)
    {
        var (_, saved, status, running) = Apps[Array.IndexOf(AppsInOrder, app)];
        return StatusText(saved, status, running);
    }

    /// <summary>Companion › Messaging: what Martlet does with each messaging app on this PC.</summary>
    internal static string Now(IReadOnlyList<(string Name, ChannelPreferences Saved, MessagingStatus Status, bool Running)> apps)
    {
        if (!apps.Any(a => a.Saved is { Connected: true, Enabled: true })) return "Off: Martlet answers no messaging app.";
        static string State(ChannelPreferences saved, MessagingStatus status, bool running)
        {
            if (!saved.Connected) return "not set up";
            if (!saved.Enabled) return "off";
            var chats = saved.Chats.Count == 0 ? "no chats paired yet" : saved.Chats.Count == 1 ? "1 paired chat" : $"{saved.Chats.Count} paired chats";
            return status.State switch
            {
                MessagingState.Running => $"answering {saved.Handle} ({chats})",
                MessagingState.Connecting => $"connecting {saved.Handle}",
                MessagingState.Retrying => $"can't reach {saved.Handle} right now",
                MessagingState.Failed => $"{saved.Handle} stopped",
                _ when !running => "not running on this PC",
                _ => $"starting {saved.Handle}"
            };
        }
        return string.Join("; ", apps.Select(a => $"{a.Name}: {State(a.Saved, a.Status, a.Running)}")) + ".";
    }

    /// <summary>The first messaging app that is on but can't answer, and why, or null.</summary>
    internal static string? Problem(IReadOnlyList<(string Name, ChannelPreferences Saved, MessagingStatus Status, bool Running)> apps) =>
        apps.FirstOrDefault(a => a.Saved is { Connected: true, Enabled: true } && a.Status.State is MessagingState.Retrying or MessagingState.Failed) is
            { Name: not null } failing ? $"{failing.Name}: {failing.Status.Problem ?? "it stopped."}" : null;

    /// <summary>Companion › Messaging's status line: whether Martlet answers the app on this PC now, or why not.</summary>
    internal static string StatusText(ChannelPreferences saved, MessagingStatus status, bool running)
    {
        if (!saved.Connected)
            return saved is WhatsAppPreferences
                ? "Not connected: follow the steps below to give Martlet a WhatsApp number."
                : "Not connected: make a Telegram bot and paste its token below.";
        var handle = saved.Handle;
        if (!saved.Enabled) return $"Connected to {handle}, but turned off on this PC.";
        var chats = saved.Chats.Count == 0 ? "no chats paired yet" : saved.Chats.Count == 1 ? "1 paired chat" : $"{saved.Chats.Count} paired chats";
        var last = status.LastAnswered is { } at ? $", last answered at {at.ToLocalTime():t}" : "";
        return status.State switch
        {
            MessagingState.Running => $"Answering {handle} on this PC ({chats}{last}).",
            MessagingState.Connecting when saved is WhatsAppPreferences => $"Connecting {handle}: opening its public address and pointing the webhook at it...",
            MessagingState.Connecting => $"Connecting to {handle}...",
            MessagingState.Retrying => $"Can't reach {handle} right now ({status.Problem?.TrimEnd('.')}), but Martlet keeps trying.",
            MessagingState.Failed => $"{handle} stopped: {status.Problem}",
            _ when !running => $"{handle} isn't running on this PC; it starts with Martlet on your companion PC.",
            _ => $"Starting {handle}..."
        };
    }

    /// <summary>WhatsApp's setup in short; the numbered steps are in the wiki (docs/wiki/Telegram.md, "Set up WhatsApp").</summary>
    internal const string WhatsAppSteps =
        "Make a Meta app with WhatsApp, then paste its access token and app secret here and press Connect. " +
        "Meta gives you a free test number, Martlet finds it and sets up the webhook itself, and then you pair your chat with a code.";

    /// <summary>Where Meta delivers WhatsApp messages: the owner's own address or a Cloudflare quick tunnel (and whether
    /// cloudflared is here).</summary>
    internal static string WhatsAppAddressText(WhatsAppPreferences saved, string? cloudflared, CloudflaredInstall? own = null)
    {
        var port = saved.Port > 0 ? $"http://localhost:{saved.Port}" : "Martlet's local port (shown once connected)";
        if (saved.PublicAddress.Length > 0)
            return $"Meta delivers messages to {saved.PublicAddress}, which must forward to {port} (with Host: localhost).";
        if (cloudflared is null)
            return "Leave the address empty and Martlet opens a free Cloudflare quick tunnel (no account, port or router change), but it needs Cloudflare's " +
                (own is { Verified: false }
                    ? "cloudflared, and Martlet doesn't run its copy because it doesn't match the SHA-256 checked at download (or is from an older Martlet): " +
                      "Get cloudflared downloads and checks the official build again (about 60 MB)."
                    : "cloudflared, which isn't on this PC yet: Get cloudflared downloads the official build from GitHub (about 60 MB) and checks its SHA-256.");
        if (CloudflaredSignature.Problem(cloudflared) is { } unsigned)
            return $"Martlet doesn't run {cloudflared} ({unsigned}), so Get cloudflared downloads Cloudflare's signed build.";
        var which = own is { Verified: true } && string.Equals(own.Path, cloudflared, StringComparison.OrdinalIgnoreCase)
            ? $"cloudflared {own.Version}, SHA-256 and Cloudflare's signature checked ({cloudflared})"
            : $"cloudflared, Cloudflare's signature checked ({cloudflared})";
        return $"Leave the address empty and Martlet opens a free Cloudflare quick tunnel to {port} with {which}; " +
            "use your own https address only if you already have one that forwards there.";
    }

    /// <summary>The automation ID prefix of an app's chats card.</summary>
    internal static string Prefix(MessagingApp app) => app == MessagingApp.WhatsApp ? "MessagingWhatsApp" : "Messaging";

    /// <summary>The paired-chats card's first line (MessagingChats, MessagingWhatsAppChats).</summary>
    internal static string ChatsText(ChannelPreferences saved) => saved.Chats.Count == 0
        ? "No chats paired yet: pair yours so Martlet answers it."
        : $"Martlet answers {(saved.Chats.Count == 1 ? "this chat" : $"these {saved.Chats.Count} chats")}:";

    /// <summary>The link that opens the pairing code in the app on a phone.</summary>
    internal static Uri PairingLink(ChannelPreferences saved, PairingCode code) => saved is WhatsAppPreferences whatsApp
        ? new Uri($"https://wa.me/{whatsApp.Digits}?text={code.Code}")
        : new Uri($"https://t.me/{((TelegramPreferences)saved).BotUsername}?start={code.Code}");
}
