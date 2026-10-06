namespace Martlet.Messaging;

/// <summary>A messaging app Martlet can be reached through; each app adds a transport and a value.</summary>
public enum MessagingApp { Telegram, WhatsApp }

/// <summary>A message someone sent the bot. <paramref name="Text"/> is null for anything that isn't text (a photo, a voice
/// note, a sticker); <paramref name="Private"/> is a one-to-one chat with the bot (Martlet answers only those);
/// <paramref name="MessageId"/> is the app's ID of the message (so it can be deleted there later); <paramref name="App"/> is
/// the app it came from (the bridge sets it from its transport).</summary>
public sealed record InboundMessage(string ChatId, string ChatName, string? Text, bool Private, string? MessageId = null,
    MessagingApp App = MessagingApp.Telegram);

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

/// <summary>A transport whose app lets the bot find its messages again: sending returns the message's ID, and the bot may
/// delete messages in a chat (its own, and in some apps the person's) and edit its own. Martlet's conversation history uses it
/// to delete or edit there what the owner deletes or edits here.</summary>
public interface IMessagingMessageControl
{
    /// <summary>Sends one message and returns its ID in the app.</summary>
    Task<string> SendMessageAsync(string chatId, string text, CancellationToken cancellation);
    Task DeleteMessageAsync(string chatId, string messageId, CancellationToken cancellation);
    Task EditMessageAsync(string chatId, string messageId, string text, CancellationToken cancellation);
}
