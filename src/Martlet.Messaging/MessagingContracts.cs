namespace Martlet.Messaging;

/// <summary>A messaging app Martlet can be reached through. Telegram is the first; each new app adds a transport and a value.</summary>
public enum MessagingApp { Telegram }

/// <summary>A message someone sent the bot. <paramref name="Text"/> is null for anything that isn't text (a photo, a voice
/// note, a sticker); <paramref name="Private"/> is a one-to-one chat with the bot (Martlet answers only those).</summary>
public sealed record InboundMessage(string ChatId, string ChatName, string? Text, bool Private);

/// <summary>The bot account a transport signed in as.</summary>
public sealed record BotIdentity(string Name, string Username);

/// <summary>A chat the owner paired: the only chats Martlet answers.</summary>
public sealed record MessagingChat(string Id, string Name);

public enum MessagingFailure { Unauthorized, Conflict, RateLimited, Network, Protocol }

/// <summary>A messaging app's request failed. The message never contains the bot token.</summary>
public sealed class MessagingException(MessagingFailure failure, string message, TimeSpan? retryAfter = null) : Exception(message)
{
    public MessagingFailure Failure { get; } = failure;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>One messaging app's bot API: who the bot is, the next messages (waiting for them a while), sending a reply and
/// showing "typing".</summary>
public interface IMessagingTransport : IDisposable
{
    MessagingApp App { get; }
    /// <summary>The longest single message the app takes; longer replies are split.</summary>
    int MaximumMessageLength { get; }
    Task<BotIdentity> ConnectAsync(CancellationToken cancellation);
    Task<IReadOnlyList<InboundMessage>> ReceiveAsync(CancellationToken cancellation);
    Task SendAsync(string chatId, string text, CancellationToken cancellation);
    Task TypingAsync(string chatId, CancellationToken cancellation);
}
