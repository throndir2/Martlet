using System.Collections.Concurrent;
using System.Security.Cryptography;
using Martlet.Core.Text;

namespace Martlet.Messaging;

public enum MessagingState { Off, Connecting, Running, Retrying, Failed }

/// <summary>How the bridge is doing: its state, the bot it runs as, what went wrong (never the token), when it last answered
/// and how many messages it answered since it started.</summary>
public sealed record MessagingStatus(MessagingState State, BotIdentity? Bot = null, string? Problem = null,
    DateTimeOffset? LastAnswered = null, int Answered = 0);

/// <summary>The code the owner sends the bot to pair a chat, and until when it works.</summary>
public sealed record PairingCode(string Code, DateTimeOffset Expires);

/// <summary>Connects one messaging app's bot to Martlet's conversation: it waits for messages, answers only chats the owner
/// paired (one-to-one chats, never groups), lets a new chat pair by sending the code Martlet shows, and sends each text
/// message to <c>answer</c> one at a time, showing "typing" until the reply goes back (split to the app's longest message).
/// A failing connection is retried with a growing pause; a rejected token stops it.</summary>
public sealed class MessagingBridge
{
    /// <summary>How long a pairing code works.</summary>
    public static readonly TimeSpan PairingLifetime = TimeSpan.FromMinutes(10);
    /// <summary>Wrong codes one chat may send before the pairing code stops working for that chat.</summary>
    public const int PairingAttempts = 5;
    /// <summary>Wrong codes from all chats together before the pairing code stops working for everyone.</summary>
    public const int PairingAttemptsInAll = 50;
    /// <summary>Unpaired chats told how to pair while Martlet runs; more strangers get no answer.</summary>
    public const int MaximumToldChats = 1000;
    /// <summary>The longest Martlet takes to answer one message.</summary>
    public static readonly TimeSpan AnswerLimit = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan TypingEvery = TimeSpan.FromSeconds(4);

    private readonly IMessagingTransport transport;
    private readonly Func<InboundMessage, CancellationToken, Task<string>> answer;
    private readonly TimeProvider clock;
    private readonly ConcurrentDictionary<string, string> allowed = new(StringComparer.Ordinal);
    private readonly HashSet<string> told = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private PairingCode? pairing;
    private readonly Dictionary<string, int> wrongCodes = new(StringComparer.Ordinal);
    private int wrongCodesInAll;
    private MessagingStatus status = new(MessagingState.Off);

    public MessagingBridge(IMessagingTransport transport, IEnumerable<MessagingChat> chats,
        Func<InboundMessage, CancellationToken, Task<string>> answer, TimeProvider? clock = null)
    {
        this.transport = transport;
        this.answer = answer;
        this.clock = clock ?? TimeProvider.System;
        foreach (var chat in chats) allowed[chat.Id] = chat.Name;
    }

    /// <summary>Raised (off the UI thread) whenever <see cref="Status"/> changes.</summary>
    public event Action<MessagingStatus>? StatusChanged;
    /// <summary>Raised when a chat paired with the code; the owner's list of chats should keep it.</summary>
    public event Action<MessagingChat>? Paired;
    /// <summary>Raised once a reply went back to a paired chat: the message it answered and the app's IDs of the reply's pieces
    /// (empty when the app doesn't give them), so the record of conversations can find them there again.</summary>
    public event Action<InboundMessage, IReadOnlyList<string>>? Replied;
    /// <summary>Raised for an unexpected error that the bridge survived (it retries or goes on with the next message), so the
    /// app can log it.</summary>
    public event Action<Exception>? Failed;

    public MessagingStatus Status { get { lock (gate) return status; } }
    public IReadOnlyList<MessagingChat> Chats => [.. allowed.Select(pair => new MessagingChat(pair.Key, pair.Value)).OrderBy(chat => chat.Name)];

    /// <summary>The pairing code that works now, if any.</summary>
    public PairingCode? Pairing
    {
        get
        {
            lock (gate) return pairing is { } code && code.Expires > clock.GetUtcNow() ? code : null;
        }
    }

    /// <summary>A fresh six-digit pairing code, working for <see cref="PairingLifetime"/> or until a chat uses it.</summary>
    public PairingCode StartPairing()
    {
        lock (gate)
        {
            wrongCodes.Clear();
            wrongCodesInAll = 0;
            return pairing = new(RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6"), clock.GetUtcNow() + PairingLifetime);
        }
    }

    public void CancelPairing()
    {
        lock (gate) pairing = null;
    }

    public void Forget(string chatId) => allowed.TryRemove(chatId, out _);

    /// <summary>Runs until <paramref name="cancellation"/>, or until the app rejects the token.</summary>
    public async Task RunAsync(CancellationToken cancellation)
    {
        var pause = TimeSpan.FromSeconds(2);
        BotIdentity? bot = null;
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    if (bot is null)
                    {
                        Publish(Status with { State = MessagingState.Connecting });
                        bot = await transport.ConnectAsync(cancellation).ConfigureAwait(false);
                        Publish(Status with { State = MessagingState.Running, Bot = bot, Problem = null });
                    }
                    var messages = await transport.ReceiveAsync(cancellation).ConfigureAwait(false);
                    pause = TimeSpan.FromSeconds(2);
                    if (Status.State != MessagingState.Running) Publish(Status with { State = MessagingState.Running, Problem = null });
                    foreach (var message in messages)
                    {
                        // One message that fails must not lose the rest of the batch: Telegram already moved past them.
                        try { await HandleAsync(message, cancellation).ConfigureAwait(false); }
                        catch (Exception error) when (error is not MessagingException && error is not OperationCanceledException)
                        {
                            Failed?.Invoke(error);
                        }
                    }
                }
                catch (MessagingException error) when (error.Failure == MessagingFailure.Unauthorized)
                {
                    Publish(Status with { State = MessagingState.Failed, Problem = error.Message });
                    return;
                }
                catch (MessagingException error)
                {
                    var wait = error.RetryAfter ?? pause;
                    Publish(Status with { State = MessagingState.Retrying, Problem = error.Message });
                    await Task.Delay(wait, clock, cancellation).ConfigureAwait(false);
                    pause = TimeSpan.FromSeconds(Math.Min(pause.TotalSeconds * 2, 60));
                }
                catch (Exception error) when (!cancellation.IsCancellationRequested)
                {
                    // Anything else (a bug, a transport error that wasn't wrapped) is retried too, never a silent stop.
                    Failed?.Invoke(error);
                    Publish(Status with { State = MessagingState.Retrying, Problem = $"Something went wrong ({error.GetType().Name}); trying again." });
                    await Task.Delay(pause, clock, cancellation).ConfigureAwait(false);
                    pause = TimeSpan.FromSeconds(Math.Min(pause.TotalSeconds * 2, 60));
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            if (Status.State != MessagingState.Failed) Publish(Status with { State = MessagingState.Off });
        }
    }

    internal async Task HandleAsync(InboundMessage message, CancellationToken cancellation)
    {
        message = message with { App = transport.App };
        // Martlet is one person's companion: group chats are never answered.
        if (!message.Private) return;
        var text = message.Text?.Trim();
        if (!allowed.ContainsKey(message.ChatId))
        {
            if (TryPair(message.ChatId, text))
            {
                allowed[message.ChatId] = message.ChatName;
                Paired?.Invoke(new(message.ChatId, message.ChatName));
                await SendAsync(message.ChatId, "Paired: you can talk to Martlet here now, and it answers while Martlet runs on your PC.", cancellation).ConfigureAwait(false);
                return;
            }
            // Say how to pair once per chat while Martlet runs, so a stranger can't make the bot talk on and on.
            bool first;
            lock (gate) first = told.Count < MaximumToldChats && told.Add(message.ChatId);
            if (first)
                await SendAsync(message.ChatId, "This Martlet answers only chats its owner paired: to pair this chat, open Martlet on your PC, " +
                    "go to Companion › Messaging, press Pair a chat and send the code here.", cancellation).ConfigureAwait(false);
            return;
        }
        if (text is null)
        {
            await SendAsync(message.ChatId, "Martlet reads text messages only for now.", cancellation).ConfigureAwait(false);
            return;
        }
        if (text.Length == 0) return;
        if (text.StartsWith("/start", StringComparison.OrdinalIgnoreCase) || text.Equals("/help", StringComparison.OrdinalIgnoreCase))
        {
            await SendAsync(message.ChatId, "This chat is paired with Martlet, so just write: Martlet answers the way it does on your PC, " +
                "with the same memory and personality.", cancellation).ConfigureAwait(false);
            return;
        }
        string reply;
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
        {
            limit.CancelAfter(AnswerLimit);
            var typing = KeepTypingAsync(message.ChatId, limit.Token);
            try { reply = await answer(message, limit.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { reply = "Martlet took too long to answer, so try again in a moment."; }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Failed?.Invoke(error);
                reply = "Martlet couldn't answer that, so try again in a moment.";
            }
            finally
            {
                await limit.CancelAsync().ConfigureAwait(false);
                await typing.ConfigureAwait(false);
            }
        }
        if (string.IsNullOrWhiteSpace(reply)) reply = "(Martlet had nothing to say.)";
        var sent = new List<string>();
        foreach (var part in MessageSplitter.Split(reply, transport.MaximumMessageLength))
            if (await SendAsync(message.ChatId, part, cancellation).ConfigureAwait(false) is { } id) sent.Add(id);
        Publish(Status with { LastAnswered = clock.GetUtcNow(), Answered = Status.Answered + 1 });
        Replied?.Invoke(message, sent);
    }

    // The code works alone or after /start (a t.me link with ?start=CODE sends "/start CODE").
    private bool TryPair(string chatId, string? text)
    {
        if (text is null) return false;
        var candidate = text.StartsWith("/start", StringComparison.OrdinalIgnoreCase) ? text[6..].Trim() : text;
        if (candidate.Length != 6 || !candidate.All(char.IsAsciiDigit)) return false;
        lock (gate)
        {
            if (pairing is not { } code || code.Expires <= clock.GetUtcNow()) return false;
            if (wrongCodes.GetValueOrDefault(chatId) >= PairingAttempts) return false;
            if (CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(candidate), System.Text.Encoding.ASCII.GetBytes(code.Code)))
            {
                pairing = null;
                return true;
            }
            // Count per chat, so a stranger can't use up the owner's code; a ceiling for all chats stops guessing from many chats.
            wrongCodes[chatId] = wrongCodes.GetValueOrDefault(chatId) + 1;
            if (++wrongCodesInAll >= PairingAttemptsInAll) pairing = null;
            return false;
        }
    }

    private async Task KeepTypingAsync(string chatId, CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                // "typing" is only a courtesy: no error from it may stop the answer.
                try { await transport.TypingAsync(chatId, cancellation).ConfigureAwait(false); }
                catch (Exception error) when (error is not OperationCanceledException) { }
                await Task.Delay(TypingEvery, clock, cancellation).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    // The message's ID in the app when the transport gives one; null when it doesn't or sending failed.
    private async Task<string?> SendAsync(string chatId, string text, CancellationToken cancellation)
    {
        try
        {
            if (transport is IMessagingMessageControl control) return await control.SendMessageAsync(chatId, text, cancellation).ConfigureAwait(false);
            await transport.SendAsync(chatId, text, cancellation).ConfigureAwait(false);
        }
        catch (MessagingException error) when (error.Failure != MessagingFailure.Unauthorized)
        {
            Publish(Status with { Problem = "Couldn't send a reply: " + error.Message });
        }
        return null;
    }

    private void Publish(MessagingStatus next)
    {
        lock (gate)
        {
            if (status == next) return;
            status = next;
        }
        StatusChanged?.Invoke(next);
    }
}
