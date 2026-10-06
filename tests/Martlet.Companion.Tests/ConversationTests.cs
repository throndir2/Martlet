using System.Net;
using System.Net.Sockets;
using System.Text;
using Martlet.Companion.Platform;

namespace Martlet.Companion.Tests;

public sealed class ConversationTests
{
    [Fact]
    public async Task LoopbackChatCompletionsServerAnswersAndTheExchangeJoinsHistory()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        string? requestBody = null;
        var serving = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            using (var reader = new StreamReader(context.Request.InputStream)) requestBody = await reader.ReadToEndAsync();
            context.Response.ContentType = "text/event-stream";
            const string chunk = "{\"id\":\"fixture\",\"object\":\"chat.completion.chunk\",\"model\":\"fixture-model\",";
            var events = "data: " + chunk + "\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Hello from \"},\"finish_reason\":null}]}\n\n" +
                "data: " + chunk + "\"choices\":[{\"index\":0,\"delta\":{\"content\":\"the fixture.\"},\"finish_reason\":\"stop\"}]}\n\n" +
                "data: [DONE]\n\n";
            var bytes = Encoding.UTF8.GetBytes(events);
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        });

        using var conversation = new CompanionConversation(new SessionCredentialStore());
        var settings = new CompanionSettings
        {
            Thinking = "chat-completions", ChatBaseUrl = $"http://127.0.0.1:{port}/v1", ChatModel = "fixture-model", Persona = "Fixture persona."
        };
        Assert.False(CompanionConversation.NeedsCloudConsent(settings, speaking: false));
        var reply = new StringBuilder();
        await foreach (var delta in conversation.ReplyAsync(settings, "Hi there", CancellationToken.None)) reply.Append(delta);
        await serving;

        Assert.Equal("Hello from the fixture.", reply.ToString());
        Assert.Equal(2, conversation.History.Count);
        Assert.Contains("Fixture persona.", requestBody, StringComparison.Ordinal);
        Assert.Contains("Hi there", requestBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization", requestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CloudRoutesNeedConsentAndAKeyBeforeAnythingIsSent()
    {
        using var conversation = new CompanionConversation(new SessionCredentialStore());
        var cloud = new CompanionSettings { Thinking = "openai-llm" };
        var error = await Assert.ThrowsAsync<CompanionException>(async () =>
        {
            await foreach (var _ in conversation.ReplyAsync(cloud, "Hi", CancellationToken.None)) { }
        });
        Assert.Contains("Send to the cloud provider", error.Message, StringComparison.Ordinal);
        var noKey = await Assert.ThrowsAsync<CompanionException>(async () =>
        {
            await foreach (var _ in conversation.ReplyAsync(cloud with { CloudConsent = true }, "Hi", CancellationToken.None)) { }
        });
        Assert.Contains("OpenAI API key", noKey.Message, StringComparison.Ordinal);
        Assert.True(CompanionConversation.NeedsCloudConsent(new CompanionSettings { Thinking = "chat-completions", ChatBaseUrl = "https://openrouter.ai/api/v1" }, false));
    }
}
