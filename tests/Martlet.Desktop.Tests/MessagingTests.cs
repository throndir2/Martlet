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
        Assert.Equal([new InboundMessage("42", "Sam Lee", "hello", true, "1"), new InboundMessage("-100", "Team", null, false, "2")], messages);
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
    public async Task TelegramTransportSendsDeletesAndEditsByMessageId()
    {
        var handler = new StubHandler();
        handler.Answers.Enqueue(Ok(new JsonObject { ["message_id"] = 77 }));
        handler.Answers.Enqueue(Ok(JsonValue.Create(true)));
        handler.Answers.Enqueue(Ok(new JsonObject { ["message_id"] = 77 }));
        handler.Answers.Enqueue(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"ok":false,"error_code":400,"description":"Bad Request: message can't be deleted for everyone"}""")
        });
        using var transport = new TelegramTransport(Token, handler, pollSeconds: 0);

        Assert.Equal("77", await transport.SendMessageAsync("42", "hi", default));
        await transport.DeleteMessageAsync("42", "77", default);
        Assert.EndsWith("/deleteMessage", handler.Paths[1]);
        Assert.Equal(77, JsonNode.Parse(handler.Bodies[1])!["message_id"]!.GetValue<long>());
        await transport.EditMessageAsync("42", "77", "edited", default);
        Assert.EndsWith("/editMessageText", handler.Paths[2]);
        Assert.Equal("edited", JsonNode.Parse(handler.Bodies[2])!["text"]!.GetValue<string>());
        var refused = await Assert.ThrowsAsync<MessagingException>(() => transport.DeleteMessageAsync("42", "5", default));
        Assert.Equal(MessagingFailure.Protocol, refused.Failure);

        // The platform side of the record turns Telegram's answers into what the queue does next.
        var platform = new MessagingPlatform(MessagingApp.Telegram, new RefusingControl(new(MessagingFailure.RateLimited, "slow", TimeSpan.FromSeconds(9))));
        Assert.Equal("telegram", platform.App);
        var change = new Martlet.Conversation.PlatformChange(Guid.NewGuid(), "telegram", "42", null, "5", Martlet.Conversation.PlatformChangeKind.Delete,
            true, null, DateTimeOffset.UtcNow);
        var slowed = await Assert.ThrowsAsync<Martlet.Conversation.PlatformChangeException>(() => platform.ApplyAsync(change, default));
        Assert.Equal((Martlet.Conversation.PlatformFailure.RateLimited, TimeSpan.FromSeconds(9)), (slowed.Failure, slowed.RetryAfter));
        var gone = await Assert.ThrowsAsync<Martlet.Conversation.PlatformChangeException>(() =>
            new MessagingPlatform(MessagingApp.Telegram, new RefusingControl(refused)).ApplyAsync(change, default));
        Assert.Equal(Martlet.Conversation.PlatformFailure.Refused, gone.Failure);
    }

    [Fact]
    public async Task AnsweredMessagesTellWhichMessagesTheReplyWentAs()
    {
        var transport = new ControlTransport();
        var bridge = new MessagingBridge(transport, [new("7", "Me")], (_, _) => Task.FromResult("pong"));
        (InboundMessage Message, IReadOnlyList<string> Ids)? replied = null;
        bridge.Replied += (message, ids) => replied = (message, ids);
        await bridge.HandleAsync(new("7", "Me", "ping", Private: true, MessageId: "500"), default);
        Assert.NotNull(replied);
        Assert.Equal("500", replied.Value.Message.MessageId);
        Assert.Equal(MessagingApp.Telegram, replied.Value.Message.App);
        Assert.Equal(["1"], replied.Value.Ids);
    }

    private sealed class RefusingControl(MessagingException error) : IMessagingMessageControl
    {
        public Task<string> SendMessageAsync(string chatId, string text, CancellationToken cancellation) => throw error;
        public Task DeleteMessageAsync(string chatId, string messageId, CancellationToken cancellation) => throw error;
        public Task EditMessageAsync(string chatId, string messageId, string text, CancellationToken cancellation) => throw error;
    }

    private sealed class ControlTransport : IMessagingTransport, IMessagingMessageControl
    {
        private int sent;
        public MessagingApp App => MessagingApp.Telegram;
        public int MaximumMessageLength => 4096;
        public Task<BotIdentity> ConnectAsync(CancellationToken cancellation) => Task.FromResult(new BotIdentity("Martlet", "bot"));
        public Task<IReadOnlyList<InboundMessage>> ReceiveAsync(CancellationToken cancellation) => Task.FromResult<IReadOnlyList<InboundMessage>>([]);
        public Task SendAsync(string chatId, string text, CancellationToken cancellation) => SendMessageAsync(chatId, text, cancellation);
        public Task TypingAsync(string chatId, CancellationToken cancellation) => Task.CompletedTask;
        public Task<string> SendMessageAsync(string chatId, string text, CancellationToken cancellation) =>
            Task.FromResult(Interlocked.Increment(ref sent).ToString(System.Globalization.CultureInfo.InvariantCulture));
        public Task DeleteMessageAsync(string chatId, string messageId, CancellationToken cancellation) => Task.CompletedTask;
        public Task EditMessageAsync(string chatId, string messageId, string text, CancellationToken cancellation) => Task.CompletedTask;
        public void Dispose() { }
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
            { Answer = (_, _, _) => Task.FromResult("pong") };
            await Assert.ThrowsAsync<ArgumentException>(() => service.ConnectTelegramAsync("nope", default));
            var bot = await service.ConnectTelegramAsync(" " + Token + " ", default);
            Assert.Equal("my_martlet_bot", bot.Username);
            Assert.Single(native.Secrets);
            Assert.DoesNotContain(Token, native.Secrets.Values.Single());
            Assert.DoesNotContain("AAH_", File.ReadAllText(Path.Combine(directory, MessagingPreferences.FileName)));
            Assert.True(service.SecretsSaved(MessagingApp.Telegram));
            await Until(() => service.Status(MessagingApp.Telegram).State == MessagingState.Running);

            var code = service.StartPairing(MessagingApp.Telegram)!;
            transport.Incoming.Enqueue([new("42", "Sam", code.Code, Private: true)]);
            await Until(() => service.Preferences.Telegram.Chats.Count == 1);
            Assert.Equal(new MessagingChat("42", "Sam"), MessagingPreferences.Load(directory).Telegram.Chats.Single());
            Assert.True(MessagingPreferences.Load(directory).Telegram.Enabled);

            service.RemoveChat(MessagingApp.Telegram, "42");
            Assert.Empty(MessagingPreferences.Load(directory).Telegram.Chats);
            service.Disconnect(MessagingApp.Telegram);
            Assert.Empty(native.Secrets);
            Assert.Equal("", MessagingPreferences.Load(directory).Telegram.BotUsername);
            Assert.False(service.SecretsSaved(MessagingApp.Telegram));
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
        Assert.StartsWith("Not connected", MainWindow.MessagingStatusText(new TelegramPreferences(), new(MessagingState.Off), running: false));
        Assert.Equal("Answering @my_martlet_bot on this PC (1 paired chat).",
            MainWindow.MessagingStatusText(saved, new(MessagingState.Running), running: true));
        Assert.Contains("turned off", MainWindow.MessagingStatusText(saved with { Enabled = false }, new(MessagingState.Off), false));
        Assert.Contains("keeps trying", MainWindow.MessagingStatusText(saved, new(MessagingState.Retrying, Problem: "Couldn't reach Telegram."), true));
    }

    private const string AccessToken = "EAAGm0PX4ZCpsBAKZBcanaryTokenValue0123456789abcdefghijk";
    private const string AppSecret = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task WhatsAppCloudFindsTheAccountFromTheTokenAndChecksTheAppSecret()
    {
        var graph = new GraphStub(request => request.Path switch
        {
            var path when path.EndsWith("/debug_token") => new JsonObject
            {
                ["data"] = new JsonObject
                {
                    ["app_id"] = "670843887433847", ["is_valid"] = true,
                    ["granular_scopes"] = new JsonArray(new JsonObject { ["scope"] = "whatsapp_business_management", ["target_ids"] = new JsonArray("102290129340398") })
                }
            },
            var path when path.EndsWith("/670843887433847/subscriptions") => new JsonObject { ["data"] = new JsonArray() },
            var path when path.EndsWith("/102290129340398/phone_numbers") => new JsonObject { ["data"] = new JsonArray(new JsonObject { ["id"] = "106540352242922" }) },
            var path when path.EndsWith("/106540352242922") => new JsonObject { ["display_phone_number"] = "+1 555-0100", ["verified_name"] = "Martlet test" },
            _ => null
        });
        using var cloud = new WhatsAppCloud(new(AccessToken, AppSecret), graph);
        var account = await cloud.DescribeAsync(null, " ", default);
        Assert.Equal(new WhatsAppAccount("670843887433847", "102290129340398", "106540352242922", "+1 555-0100", "Martlet test"), account);
        Assert.Equal($"Bearer 670843887433847|{AppSecret}", graph.Requests.Single(r => r.Path.EndsWith("/subscriptions")).Authorization);
        Assert.All(graph.Requests.Where(r => !r.Path.EndsWith("/subscriptions")), r => Assert.Equal("Bearer " + AccessToken, r.Authorization));

        var wrongSecret = new GraphStub(request => request.Path.EndsWith("/debug_token")
            ? new JsonObject { ["data"] = new JsonObject { ["app_id"] = "670843887433847", ["is_valid"] = true } }
            : new JsonObject { ["error"] = new JsonObject { ["message"] = "Invalid OAuth access token signature.", ["code"] = 190 } });
        using var rejected = new WhatsAppCloud(new(AccessToken, AppSecret), wrongSecret);
        var error = await Assert.ThrowsAsync<MessagingException>(() => rejected.DescribeAsync("106540352242922", "102290129340398", default));
        Assert.Equal(MessagingFailure.Unauthorized, error.Failure);
        Assert.Contains("app secret", error.Message);
        Assert.DoesNotContain(AppSecret, error.Message);
        Assert.Throws<ArgumentException>(() => new WhatsAppCloud(new("short", AppSecret)));
        Assert.Throws<ArgumentException>(() => new WhatsAppCloud(new(AccessToken, "not a secret")));
    }

    [Fact]
    public void WhatsAppDeliveriesNeedTheAppSecretsSignatureAndKeepOnlyTheirNumbersMessages()
    {
        var body = Delivery("106540352242922", ("15550199", "wamid.1", "text", "hello"), ("15550199", "wamid.2", "image", null));
        Assert.True(WhatsAppWebhook.SignatureValid(Encoding.UTF8.GetBytes(AppSecret), body, Sign(body)));
        Assert.False(WhatsAppWebhook.SignatureValid(Encoding.UTF8.GetBytes(AppSecret), body, "sha256=00"));
        Assert.False(WhatsAppWebhook.SignatureValid(Encoding.UTF8.GetBytes(AppSecret), body, null));
        var messages = WhatsAppWebhook.Parse(body, "106540352242922");
        Assert.Equal([new WhatsAppMessage(new("15550199", "Sam", "hello", true, "wamid.1"), "wamid.1"), new WhatsAppMessage(new("15550199", "Sam", null, true, "wamid.2"), "wamid.2")], messages);
        Assert.Empty(WhatsAppWebhook.Parse(body, "999999999"));
    }

    [Fact]
    public async Task WhatsAppTransportRegistersItsWebhookReceivesSignedMessagesAndReplies()
    {
        var port = WhatsAppWebhook.FreePort();
        using var local = new HttpClient();
        var graph = new GraphStub(request =>
        {
            if (request.Path.EndsWith("/670843887433847/subscriptions"))
            {
                // Meta checks the webhook while it saves it.
                var body = JsonNode.Parse(request.Body)!;
                var check = $"{body["callback_url"]}?hub.mode=subscribe&hub.verify_token={body["verify_token"]}&hub.challenge=4242";
                var answer = local.GetStringAsync(check).GetAwaiter().GetResult();
                return answer == "4242" ? new JsonObject { ["success"] = true }
                    : new JsonObject { ["error"] = new JsonObject { ["message"] = "Callback verification failed", ["code"] = 2200 } };
            }
            return new JsonObject { ["success"] = true, ["messages"] = new JsonArray(new JsonObject { ["id"] = "wamid.out" }) };
        });
        var account = new WhatsAppAccount("670843887433847", "102290129340398", "106540352242922", "+1 555-0100", "Martlet test");
        using var transport = new WhatsAppTransport(new(AccessToken, AppSecret), account, port,
            new FixedPublicAddress(new Uri($"http://127.0.0.1:{port}/")), graph, verifyPause: TimeSpan.FromMilliseconds(10));

        Assert.Equal(new BotIdentity("Martlet test", "+1 555-0100"), await transport.ConnectAsync(default));
        Assert.True(transport.Listener.Verified);
        Assert.StartsWith($"http://127.0.0.1:{port}/whatsapp/", transport.Webhook!.AbsoluteUri);
        Assert.Contains(graph.Requests, r => r.Path.EndsWith("/102290129340398/subscribed_apps"));

        var delivery = Delivery("106540352242922", ("15550199", "wamid.1", "text", "hello"));
        using (var unsigned = await local.PostAsync(transport.Webhook, new ByteArrayContent(delivery)))
            Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
        using (var wrongPath = await local.PostAsync($"http://127.0.0.1:{port}/whatsapp/guess", new ByteArrayContent(delivery)))
            Assert.Equal(HttpStatusCode.NotFound, wrongPath.StatusCode);
        for (var i = 0; i < 2; i++)
        {
            var signed = new ByteArrayContent(delivery);
            signed.Headers.Add("X-Hub-Signature-256", Sign(delivery));
            using var accepted = await local.PostAsync(transport.Webhook, signed);
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }
        var received = await transport.ReceiveAsync(default);
        Assert.Equal([new InboundMessage("15550199", "Sam", "hello", true, "wamid.1")], received);

        await transport.TypingAsync("15550199", default);
        await transport.TypingAsync("15550199", default);
        var typing = graph.Requests.Where(r => r.Path.EndsWith("/106540352242922/messages")).ToList();
        Assert.Single(typing);
        Assert.Equal("wamid.1", JsonNode.Parse(typing[0].Body)!["message_id"]!.GetValue<string>());
        await transport.SendAsync("15550199", "hi there", default);
        var sent = JsonNode.Parse(graph.Requests.Last().Body)!;
        Assert.Equal("15550199", sent["to"]!.GetValue<string>());
        Assert.Equal("hi there", sent["text"]!["body"]!.GetValue<string>());
    }

    [Fact]
    public async Task ServiceConnectsWhatsAppKeepsItsSecretsInTheVaultAndPairsChats()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Martlet.Messaging." + Guid.NewGuid().ToString("N"));
        var native = new MemoryNative();
        var transport = new FakeTransport(MessagingApp.WhatsApp);
        WhatsAppPreferences? started = null;
        var graph = new GraphStub(request => request.Path switch
        {
            var path when path.EndsWith("/debug_token") => new JsonObject { ["data"] = new JsonObject { ["app_id"] = "670843887433847", ["is_valid"] = true } },
            var path when path.EndsWith("/subscriptions") => new JsonObject { ["data"] = new JsonArray() },
            _ => new JsonObject { ["display_phone_number"] = "+1 555-0100", ["verified_name"] = "Martlet test" }
        });
        try
        {
            using var service = new MessagingService(directory, new WindowsCredentialStore(native), whatsApp: (secrets, saved) =>
            {
                Assert.Equal(new WhatsAppSecrets(AccessToken, AppSecret), secrets);
                started = saved;
                return transport;
            }, cloud: secrets => new WhatsAppCloud(secrets, graph));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                service.ConnectWhatsAppAsync(AccessToken, AppSecret, null, null, "http://example.com/", default));
            var account = await service.ConnectWhatsAppAsync(" " + AccessToken, AppSecret, "106540352242922", "102290129340398",
                "https://martlet.example.com/", default);
            Assert.Equal("+1 555-0100", account.Number);
            Assert.Equal(new WhatsAppSecrets(AccessToken, AppSecret), service.SavedWhatsAppSecrets());
            var file = File.ReadAllText(Path.Combine(directory, MessagingPreferences.FileName));
            Assert.DoesNotContain("EAAG", file);
            Assert.DoesNotContain(AppSecret, file);
            Assert.DoesNotContain(AccessToken, native.Secrets.Values.Single());
            await Until(() => service.Status(MessagingApp.WhatsApp).State == MessagingState.Running);
            Assert.Equal("https://martlet.example.com/", started!.PublicAddress);
            Assert.InRange(started.Port, 1, 65535);

            var code = service.StartPairing(MessagingApp.WhatsApp)!;
            transport.Incoming.Enqueue([new("15550199", "Sam", code.Code, Private: true)]);
            await Until(() => service.Preferences.WhatsApp.Chats.Count == 1);
            Assert.Equal(new MessagingChat("15550199", "Sam"), MessagingPreferences.Load(directory).WhatsApp.Chats.Single());
            Assert.Empty(MessagingPreferences.Load(directory).Telegram.Chats);
            Assert.Contains("Answering +1 555-0100", MainWindow.MessagingStatusText(service.Preferences.WhatsApp, service.Status(MessagingApp.WhatsApp), true));

            service.Disconnect(MessagingApp.WhatsApp);
            Assert.Empty(native.Secrets);
            Assert.False(MessagingPreferences.Load(directory).WhatsApp.Connected);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static byte[] Delivery(string phoneNumberId, params (string From, string Id, string Type, string? Text)[] messages) =>
        Encoding.UTF8.GetBytes(new JsonObject
        {
            ["object"] = "whatsapp_business_account",
            ["entry"] = new JsonArray(new JsonObject
            {
                ["id"] = "102290129340398",
                ["changes"] = new JsonArray(new JsonObject
                {
                    ["field"] = "messages",
                    ["value"] = new JsonObject
                    {
                        ["messaging_product"] = "whatsapp",
                        ["metadata"] = new JsonObject { ["display_phone_number"] = "15550100", ["phone_number_id"] = phoneNumberId },
                        ["contacts"] = new JsonArray(new JsonObject { ["profile"] = new JsonObject { ["name"] = "Sam" }, ["wa_id"] = "15550199" }),
                        ["messages"] = new JsonArray([.. messages.Select(m => (JsonNode)new JsonObject
                        {
                            ["from"] = m.From, ["id"] = m.Id, ["type"] = m.Type,
                            ["text"] = m.Text is null ? null : new JsonObject { ["body"] = m.Text }
                        })])
                    }
                })
            })
        }.ToJsonString());

    private static string Sign(byte[] body) =>
        "sha256=" + Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(Encoding.UTF8.GetBytes(AppSecret), body));

    private sealed record GraphRequest(string Method, string Path, string Authorization, string Body);

    private sealed class GraphStub(Func<GraphRequest, JsonObject?> answer) : HttpMessageHandler
    {
        internal List<GraphRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var seen = new GraphRequest(request.Method.Method, request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString() ?? "",
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            lock (Requests) Requests.Add(seen);
            var result = await Task.Run(() => answer(seen), cancellationToken);
            var failed = result?["error"] is not null || result is null;
            return new(failed ? HttpStatusCode.BadRequest : HttpStatusCode.OK)
            {
                Content = new StringContent((result ?? new JsonObject { ["error"] = new JsonObject { ["message"] = "unknown", ["code"] = 100 } }).ToJsonString(),
                    Encoding.UTF8, "application/json")
            };
        }
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

    private sealed class FakeTransport(MessagingApp app = MessagingApp.Telegram) : IMessagingTransport
    {
        internal MessagingException? ConnectFailure { get; init; }
        internal int ReceiveFailures { get; set; }
        internal ConcurrentQueue<IReadOnlyList<InboundMessage>> Incoming { get; } = new();
        internal List<(string Chat, string Text)> Sent { get; } = [];
        internal ConcurrentBag<string> Typing { get; } = [];
        public MessagingApp App => app;
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
