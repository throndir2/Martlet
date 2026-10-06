using System.Collections.Concurrent;

namespace Martlet.Messaging;

/// <summary>WhatsApp through Meta's WhatsApp Cloud API. Meta delivers messages to a webhook, so the transport runs
/// <see cref="WhatsAppWebhook"/> on a local port, opens a public address for it (<see cref="IPublicAddress"/>, a Cloudflare
/// quick tunnel by default) and points the app's webhook there each time that address changes. Replies go out through the
/// Graph API.</summary>
public sealed class WhatsAppTransport : IMessagingTransport
{
    private static readonly TimeSpan WaitForMessages = TimeSpan.FromSeconds(50);
    private readonly WhatsAppCloud cloud;
    private readonly WhatsAppAccount account;
    private readonly IPublicAddress publicAddress;
    private readonly WhatsAppWebhook webhook;
    private readonly ConcurrentDictionary<string, string> waiting = new(StringComparer.Ordinal);
    private readonly TimeSpan verifyPause;
    private Uri? registered;

    public WhatsAppTransport(WhatsAppSecrets secrets, WhatsAppAccount account, int port, IPublicAddress publicAddress,
        HttpMessageHandler? handler = null, Uri? api = null, TimeSpan? verifyPause = null)
    {
        cloud = new WhatsAppCloud(secrets, handler, api);
        this.account = account;
        this.publicAddress = publicAddress;
        this.verifyPause = verifyPause ?? TimeSpan.FromSeconds(5);
        webhook = new WhatsAppWebhook(port, secrets.AppSecret.Trim(), account.PhoneNumberId);
    }

    public MessagingApp App => MessagingApp.WhatsApp;
    public int MaximumMessageLength => 4096;
    /// <summary>The address Meta delivers messages to now, once registered.</summary>
    public Uri? Webhook => registered;
    internal WhatsAppWebhook Listener => webhook;

    public async Task<BotIdentity> ConnectAsync(CancellationToken cancellation)
    {
        webhook.Start();
        await RegisterAsync(cancellation).ConfigureAwait(false);
        return new(account.Name, account.Number);
    }

    // Opens the public address and points the app's webhook at it. A fresh tunnel's name can take a few seconds to resolve
    // for Meta, so its check is tried again a few times.
    private async Task RegisterAsync(CancellationToken cancellation)
    {
        var address = await publicAddress.OpenAsync(webhook.Port, cancellation).ConfigureAwait(false);
        var callback = new Uri(address.AbsoluteUri.TrimEnd('/') + webhook.Path);
        if (callback == registered) return;
        registered = null;
        await WaitUntilReachableAsync(callback, cancellation).ConfigureAwait(false);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await cloud.SubscribeAsync(account, callback, webhook.VerifyToken, cancellation).ConfigureAwait(false);
                registered = callback;
                return;
            }
            catch (MessagingException error) when (error.Failure == MessagingFailure.Network && attempt < 8)
            {
                await Task.Delay(verifyPause, cancellation).ConfigureAwait(false);
            }
        }
    }

    // A new quick tunnel's name takes a few seconds to resolve; Meta checks the webhook once, so Martlet waits until its own
    // check through the public address reaches it (up to a minute) before asking Meta.
    private async Task WaitUntilReachableAsync(Uri callback, CancellationToken cancellation)
    {
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var check = new Uri($"{callback.AbsoluteUri}?hub.mode=subscribe&hub.verify_token={webhook.VerifyToken}&hub.challenge=martlet");
        var until = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        while (true)
        {
            try
            {
                using var response = await probe.GetAsync(check, cancellation).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return;
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException && !cancellation.IsCancellationRequested) { }
            if (DateTimeOffset.UtcNow >= until)
                throw new MessagingException(MessagingFailure.Network, $"Martlet's public address ({callback.Host}) doesn't reach this PC yet.");
            await Task.Delay(verifyPause, cancellation).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<InboundMessage>> ReceiveAsync(CancellationToken cancellation)
    {
        if (publicAddress.Lost || registered is null) await RegisterAsync(cancellation).ConfigureAwait(false);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        wait.CancelAfter(WaitForMessages);
        var messages = new List<InboundMessage>();
        try
        {
            if (!await webhook.Messages.WaitToReadAsync(wait.Token).ConfigureAwait(false))
                throw new MessagingException(MessagingFailure.Network, "Martlet's WhatsApp webhook stopped.");
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return messages; }
        while (webhook.Messages.TryRead(out var next))
        {
            waiting[next.Message.ChatId] = next.MessageId;
            messages.Add(next.Message);
        }
        return messages;
    }

    public async Task SendAsync(string chatId, string text, CancellationToken cancellation)
    {
        waiting.TryRemove(chatId, out _);
        await cloud.SendTextAsync(account.PhoneNumberId, chatId, text, cancellation).ConfigureAwait(false);
    }

    // WhatsApp shows "typing" for one message until the reply or 25 seconds, so it is shown once per message.
    public async Task TypingAsync(string chatId, CancellationToken cancellation)
    {
        if (waiting.TryRemove(chatId, out var messageId))
            await cloud.TypingAsync(account.PhoneNumberId, messageId, cancellation).ConfigureAwait(false);
    }

    public void Dispose()
    {
        webhook.Dispose();
        publicAddress.Dispose();
        cloud.Dispose();
    }
}
