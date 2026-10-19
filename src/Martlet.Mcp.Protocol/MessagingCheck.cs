using System.IO;
using System.Net;
using System.Net.Http;
using Martlet.Home;
using Martlet.Messaging;

namespace Martlet.Mcp;

/// <summary>messaging_check rehearses Companion › Messaging's production <see cref="MessagingBridge"/> and
/// <see cref="TelegramTransport"/> with fakes on this PC: no Telegram, WhatsApp or Meta server is contacted. smart_home_guard
/// shows how <see cref="HomeCommandGuard"/> treats one request.</summary>
internal static class MessagingCheck
{
    internal static object Guard(string text) => new
    {
        text,
        risk = HomeCommandGuard.Assess(text).ToString(),
        mentionsSensitive = HomeCommandGuard.MentionsSensitive(text),
        meaning = HomeCommandGuard.Assess(text) switch
        {
            HomeCommandRisk.Sensitive => "Names a lock, door, garage, gate, alarm or valve and asks for an action: never sent unless the setting is on and the owner clicks Yes, send it.",
            HomeCommandRisk.NotACommand => "A longer sentence that mentions such a device: chat, not sent to Home Assistant.",
            _ => "Ordinary words or a status question: sent to Home Assistant's Assist."
        }
    };

    internal static async Task<object> RunAsync(CancellationToken cancellation)
    {
        var steps = new List<object>();
        var ok = true;
        void Step(string name, bool passed, object detail)
        {
            ok &= passed;
            steps.Add(new { name, passed, detail });
        }

        // 1. The connection breaks while Telegram's answer is read: a network failure the bridge retries, without the token.
        using (var transport = new TelegramTransport("123456789:AAH_abcdefghijklmnopqrstuvwxyz0123", new BrokenBodyHandler(), pollSeconds: 0))
        {
            try
            {
                await transport.ConnectAsync(cancellation);
                Step("telegramBodyReadBreaks", false, "no error");
            }
            catch (MessagingException error)
            {
                Step("telegramBodyReadBreaks", error.Failure == MessagingFailure.Network && !error.Message.Contains("AAH_", StringComparison.Ordinal),
                    new { failure = error.Failure.ToString(), message = error.Message });
            }
        }

        // 2. A receive throws an unexpected error, typing fails and the answer of one message fails: the bridge keeps running,
        // says it couldn't answer and answers the next message of the batch.
        var fake = new FakeTransport();
        fake.Batches.Enqueue([new("7", "Me", "first", true), new("7", "Me", "second", true)]);
        var states = new List<string>();
        var failures = new List<string>();
        var bridge = new MessagingBridge(fake, [new("7", "Me")], (message, _) =>
            message.Text == "first" ? throw new InvalidOperationException("fixture") : Task.FromResult("pong"));
        bridge.StatusChanged += status => { lock (states) states.Add(status.State.ToString()); };
        bridge.Failed += error => { lock (failures) failures.Add(error.GetType().Name); };
        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
        {
            var run = bridge.RunAsync(stop.Token);
            for (var i = 0; i < 200 && fake.SentCount < 2; i++) await Task.Delay(25, cancellation);
            await stop.CancelAsync();
            await run.WaitAsync(TimeSpan.FromSeconds(10), cancellation);
        }
        string[] seen;
        lock (states) seen = [.. states];
        Step("unexpectedErrorRetries", seen.Contains("Retrying") && seen.Contains("Running"), new { states = seen, failures });
        Step("failedAnswerKeepsTheBatch", fake.Sent.SequenceEqual(["Martlet couldn't answer that. Try again in a moment.", "pong"]),
            new { sent = fake.Sent, typingFailures = fake.TypingCalls });
        Step("stopsAsOff", bridge.Status.State == MessagingState.Off, bridge.Status.State.ToString());

        // 3. A stranger's wrong codes don't use up the owner's pairing code.
        var pairing = new MessagingBridge(new FakeTransport(), [], (_, _) => Task.FromResult("x"));
        var code = pairing.StartPairing();
        var wrong = code.Code == "000000" ? "111111" : "000000";
        for (var i = 0; i < MessagingBridge.PairingAttempts + 2; i++)
            await pairing.HandleAsync(new("9", "Stranger", wrong, true), cancellation);
        await pairing.HandleAsync(new("9", "Stranger", code.Code, true), cancellation);
        var strangerPaired = pairing.Chats.Any(chat => chat.Id == "9");
        await pairing.HandleAsync(new("42", "Owner", code.Code, true), cancellation);
        Step("pairingCodeSurvivesAStranger", !strangerPaired && pairing.Chats.Any(chat => chat.Id == "42"),
            new { strangerPaired, ownerPaired = pairing.Chats.Any(chat => chat.Id == "42") });

        return new { ok, steps };
    }

    private sealed class BrokenBodyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream()) });
    }

    private sealed class BrokenStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("The response ended prematurely.");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FakeTransport : IMessagingTransport
    {
        private int receives;
        internal Queue<IReadOnlyList<InboundMessage>> Batches { get; } = new();
        internal List<string> Sent { get; } = [];
        internal int SentCount { get { lock (Sent) return Sent.Count; } }
        internal int TypingCalls;
        public MessagingApp App => MessagingApp.Telegram;
        public int MaximumMessageLength => 4096;
        public Task<BotIdentity> ConnectAsync(CancellationToken cancellation) => Task.FromResult(new BotIdentity("Martlet", "fixture_bot"));

        public async Task<IReadOnlyList<InboundMessage>> ReceiveAsync(CancellationToken cancellation)
        {
            if (Interlocked.Increment(ref receives) == 1) throw new InvalidOperationException("fixture receive failure");
            lock (Batches) if (Batches.TryDequeue(out var next)) return next;
            await Task.Delay(20, cancellation);
            return [];
        }

        public Task SendAsync(string chatId, string text, CancellationToken cancellation)
        {
            lock (Sent) Sent.Add(text);
            return Task.CompletedTask;
        }

        public Task TypingAsync(string chatId, CancellationToken cancellation)
        {
            Interlocked.Increment(ref TypingCalls);
            throw new IOException("fixture typing failure");
        }

        public void Dispose() { }
    }
}
