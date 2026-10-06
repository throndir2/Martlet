using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Credentials.Windows;
using Martlet.Messaging;

namespace Martlet.Desktop.Tests;

public sealed class MessagingTests
{
    private const string Token = "123456789:AAH_abcdefghijklmnopqrstuvwxyz0123";

    [Fact]
    public async Task UnpairedChatsAreToldOnceAndPairWithTheCodeThenGetAnswers()
    {
        var transport = new FakeTransport();
        var asked = new List<string>();
        var bridge = new MessagingBridge(transport, [], (message, _) =>
        {
            asked.Add(message.Text!);
            return Task.FromResult("Hi from Martlet.");
        });
        MessagingChat? paired = null;
        bridge.Paired += chat => paired = chat;

        await bridge.HandleAsync(new("42", "Sam", "hello", Private: true), default);
        await bridge.HandleAsync(new("42", "Sam", "hello again", Private: true), default);
        Assert.Single(transport.Sent);
        Assert.Contains("Pair a chat", transport.Sent[0].Text);
        Assert.Empty(asked);

        var code = bridge.StartPairing();
        await bridge.HandleAsync(new("42", "Sam", "/start " + code.Code, Private: true), default);
        Assert.Equal(new MessagingChat("42", "Sam"), paired);
        Assert.Null(bridge.Pairing);
        Assert.StartsWith("Paired", transport.Sent[^1].Text);

        await bridge.HandleAsync(new("42", "Sam", "how are you?", Private: true), default);
        Assert.Equal(["how are you?"], asked);
        Assert.Equal(("42", "Hi from Martlet."), transport.Sent[^1]);
        Assert.Equal(1, bridge.Status.Answered);
        Assert.Contains("42", transport.Typing);
    }

    [Fact]
    public async Task GroupsNonTextAndHelpNeverReachTheConversation()
    {
        var transport = new FakeTransport();
        var asked = 0;
        var bridge = new MessagingBridge(transport, [new("7", "Me")], (_, _) => { asked++; return Task.FromResult("x"); });

        await bridge.HandleAsync(new("-100", "A group", "hello", Private: false), default);
        Assert.Empty(transport.Sent);
        await bridge.HandleAsync(new("7", "Me", null, Private: true), default);
        Assert.Contains("text messages only", transport.Sent[^1].Text);
        await bridge.HandleAsync(new("7", "Me", "/help", Private: true), default);
        Assert.Contains("paired", transport.Sent[^1].Text);
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task WrongCodesEndThePairingAndExpiredCodesNeverPair()
    {
        var clock = new Martlet.Core.Tests.ManualClock();
        var transport = new FakeTransport();
        var bridge = new MessagingBridge(transport, [], (_, _) => Task.FromResult("x"), clock);
        var code = bridge.StartPairing();
        var wrong = code.Code == "000000" ? "111111" : "000000";
        for (var attempt = 0; attempt < MessagingBridge.PairingAttempts; attempt++)
            await bridge.HandleAsync(new("9", "Stranger", wrong, Private: true), default);
        Assert.Null(bridge.Pairing);
        await bridge.HandleAsync(new("9", "Stranger", code.Code, Private: true), default);
        Assert.DoesNotContain(bridge.Chats, chat => chat.Id == "9");

        code = bridge.StartPairing();
        clock.Advance(MessagingBridge.PairingLifetime + TimeSpan.FromSeconds(1));
        await bridge.HandleAsync(new("9", "Stranger", code.Code, Private: true), default);
        Assert.DoesNotContain(bridge.Chats, chat => chat.Id == "9");
    }

    [Fact]
    public void LongRepliesSplitAtBreaksWithinTheLimit()
    {
        var text = string.Join("\n\n", Enumerable.Range(0, 30).Select(i => $"Paragraph {i} " + new string('x', 300)));
        var parts = MessagingBridge.Split(text, 4096);
        Assert.True(parts.Count > 1);
        Assert.All(parts, part => Assert.InRange(part.Length, 1, 4096));
        Assert.Equal(text.Replace("\n", "").Replace(" ", ""), string.Concat(parts).Replace("\n", "").Replace(" ", ""));
        Assert.Equal(["abcd", "efgh", "ij"], MessagingBridge.Split("abcdefghij", 4));
    }

    [Fact]
    public async Task RunStopsWhenTheTokenIsRejectedAndRetriesOtherFailures()
    {
        var transport = new FakeTransport { ConnectFailure = new(MessagingFailure.Unauthorized, "Telegram didn't accept this bot token.") };
        var bridge = new MessagingBridge(transport, [], (_, _) => Task.FromResult("x"));
        await bridge.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MessagingState.Failed, bridge.Status.State);

        using var stop = new CancellationTokenSource();
        var flaky = new FakeTransport { ReceiveFailures = 1 };
        flaky.Incoming.Enqueue([new("7", "Me", "ping", Private: true)]);
        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = new MessagingBridge(flaky, [new("7", "Me")], (_, _) => { answered.TrySetResult(); return Task.FromResult("pong"); });
        var run = running.RunAsync(stop.Token);
        await answered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(MessagingState.Running, running.Status.State);
        Assert.NotNull(running.Status.Bot);
        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MessagingState.Off, running.Status.State);
    }

    [Fact]
    public async Task TelegramTransportReadsUpdatesAdvancesTheOffsetAndKeepsTheTokenOutOfErrors()
    {
        var handler = new StubHandler();
        handler.Answers.Enqueue(Ok(new JsonObject { ["id"] = 1, ["is_bot"] = true, ["first_name"] = "Martlet", ["username"] = "my_martlet_bot" }));
        handler.Answers.Enqueue(Ok(new JsonArray(
            new JsonObject
            {
                ["update_id"] = 10,
                ["message"] = new JsonObject
                {
                    ["message_id"] = 1, ["text"] = "hello",
                    ["chat"] = new JsonObject { ["id"] = 42, ["type"] = "private", ["first_name"] = "Sam", ["last_name"] = "Lee" }
                }
            },
            new JsonObject
            {
                ["update_id"] = 11,
                ["message"] = new JsonObject { ["message_id"] = 2, ["chat"] = new JsonObject { ["id"] = -100, ["type"] = "group", ["title"] = "Team" } }
            })));
        handler.Answers.Enqueue(Ok(new JsonArray()));
        handler.Answers.Enqueue(Ok(new JsonObject { ["message_id"] = 3 }));
        handler.Answers.Enqueue(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"ok":false,"error_code":401,"description":"Unauthorized"}""")
        });
        using var transport = new TelegramTransport(Token, handler, pollSeconds: 0);

        Assert.Equal(new BotIdentity("Martlet", "my_martlet_bot"), await transport.ConnectAsync(default));
        var messages = await transport.ReceiveAsync(default);
        Assert.Equal([new InboundMessage("42", "Sam Lee", "hello", true), new InboundMessage("-100", "Team", null, false)], messages);
        await transport.ReceiveAsync(default);
        Assert.Equal(12, JsonNode.Parse(handler.Bodies[2])!["offset"]!.GetValue<long>());
        await transport.SendAsync("42", "hi", default);
        Assert.Equal(42, JsonNode.Parse(handler.Bodies[3])!["chat_id"]!.GetValue<long>());
        Assert.EndsWith("/sendMessage", handler.Paths[3]);
        var error = await Assert.ThrowsAsync<MessagingException>(() => transport.SendAsync("42", "hi", default));
        Assert.Equal(MessagingFailure.Unauthorized, error.Failure);
        Assert.DoesNotContain("AAH_", error.Message);
        Assert.All(handler.Paths, path => Assert.StartsWith($"/bot{Token}/", path));
        Assert.Throws<ArgumentException>(() => new TelegramTransport("not a token"));
    }

    [Fact]
    public async Task ServiceSavesTheTokenInTheVaultAndKeepsPairedChats()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Martlet.Messaging." + Guid.NewGuid().ToString("N"));
        var native = new MemoryNative();
        var transport = new FakeTransport();
        try
        {
            using var service = new MessagingService(directory, new WindowsCredentialStore(native), token =>
            {
                Assert.Equal(Token, token);
                return transport;
            })
            { Answer = (_, _) => Task.FromResult("pong") };
            await Assert.ThrowsAsync<ArgumentException>(() => service.ConnectTelegramAsync("nope", default));
            var bot = await service.ConnectTelegramAsync(" " + Token + " ", default);
            Assert.Equal("my_martlet_bot", bot.Username);
            Assert.Single(native.Secrets);
            Assert.DoesNotContain(Token, native.Secrets.Values.Single());
            Assert.DoesNotContain("AAH_", File.ReadAllText(Path.Combine(directory, MessagingPreferences.FileName)));
            Assert.True(service.TokenSaved);
            await Until(() => service.Status.State == MessagingState.Running);

            var code = service.StartPairing()!;
            transport.Incoming.Enqueue([new("42", "Sam", code.Code, Private: true)]);
            await Until(() => service.Preferences.Telegram.Chats.Count == 1);
            Assert.Equal(new MessagingChat("42", "Sam"), MessagingPreferences.Load(directory).Telegram.Chats.Single());
            Assert.True(MessagingPreferences.Load(directory).Telegram.Enabled);

            service.RemoveChat("42");
            Assert.Empty(MessagingPreferences.Load(directory).Telegram.Chats);
            service.Disconnect();
            Assert.Empty(native.Secrets);
            Assert.Equal("", MessagingPreferences.Load(directory).Telegram.BotUsername);
            Assert.False(service.TokenSaved);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void StatusLineSaysWhetherMartletAnswersAndWhyNot()
    {
        var saved = new TelegramPreferences { Enabled = true, BotUsername = "my_martlet_bot", Chats = [new("1", "Me")] };
        Assert.StartsWith("Not connected", MainWindow.TelegramStatusText(new(), new(MessagingState.Off), connected: false, running: false));
        Assert.Equal("Answering @my_martlet_bot on this PC (1 paired chat).",
            MainWindow.TelegramStatusText(saved, new(MessagingState.Running), connected: true, running: true));
        Assert.Contains("turned off", MainWindow.TelegramStatusText(saved with { Enabled = false }, new(MessagingState.Off), true, false));
        Assert.Contains("keeps trying", MainWindow.TelegramStatusText(saved, new(MessagingState.Retrying, Problem: "Couldn't reach Telegram."), true, true));
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition());
    }

    private static HttpResponseMessage Ok(JsonNode result) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(new JsonObject { ["ok"] = true, ["result"] = result }.ToJsonString(), Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler : HttpMessageHandler
    {
        internal Queue<HttpResponseMessage> Answers { get; } = new();
        internal List<string> Paths { get; } = [];
        internal List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return Answers.Dequeue();
        }
    }

    private sealed class FakeTransport : IMessagingTransport
    {
        internal MessagingException? ConnectFailure { get; init; }
        internal int ReceiveFailures { get; set; }
        internal ConcurrentQueue<IReadOnlyList<InboundMessage>> Incoming { get; } = new();
        internal List<(string Chat, string Text)> Sent { get; } = [];
        internal ConcurrentBag<string> Typing { get; } = [];
        public MessagingApp App => MessagingApp.Telegram;
        public int MaximumMessageLength => 4096;

        public Task<BotIdentity> ConnectAsync(CancellationToken cancellation) =>
            ConnectFailure is { } failure ? Task.FromException<BotIdentity>(failure) : Task.FromResult(new BotIdentity("Martlet", "my_martlet_bot"));

        public async Task<IReadOnlyList<InboundMessage>> ReceiveAsync(CancellationToken cancellation)
        {
            if (ReceiveFailures > 0)
            {
                ReceiveFailures--;
                throw new MessagingException(MessagingFailure.Network, "Couldn't reach Telegram.", TimeSpan.FromMilliseconds(10));
            }
            if (Incoming.TryDequeue(out var next)) return next;
            await Task.Delay(20, cancellation);
            return [];
        }

        public Task SendAsync(string chatId, string text, CancellationToken cancellation)
        {
            lock (Sent) Sent.Add((chatId, text));
            return Task.CompletedTask;
        }

        public Task TypingAsync(string chatId, CancellationToken cancellation)
        {
            Typing.Add(chatId);
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }

    private sealed class MemoryNative : ICredentialNative
    {
        internal ConcurrentDictionary<string, string> Secrets { get; } = new();
        public bool IsSupported => true;

        public int Write(string target, ReadOnlySpan<char> secret)
        {
            Secrets[target] = secret.ToString();
            return 0;
        }

        public int Read(string target, out Martlet.Core.Settings.SecretLease? secret)
        {
            secret = Secrets.TryGetValue(target, out var value) ? new(value) : null;
            return secret is null ? 1168 : 0;
        }

        public int Delete(string target) => Secrets.TryRemove(target, out _) ? 0 : 1168;
    }
}
