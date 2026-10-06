using System.Text;
using System.Text.Json;
using Martlet.Conversation;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;
using Martlet.Discord;
using Martlet.Providers;
using Martlet.Providers.Tests;

namespace Martlet.Desktop.Tests;

public sealed class DiscordReplyEngineTests
{
    private static readonly DiscordPlace Channel = new(20, 10, "general", Direct: false);

    private static DiscordTurn Turn(string text, bool addressed = true) =>
        new(Channel, new(2, "Bo", IsOwner: false), text, DiscordTurnSource.Text, addressed,
            [new("Ana", "morning all", DateTimeOffset.UnixEpoch, false), new("Martlet", "Morning!", DateTimeOffset.UnixEpoch.AddSeconds(1), true)]);

    private static DiscordReplyEngine Engine(LiveFixture fixture, Func<bool> busy, bool chat = false) =>
        new(fixture.Settings, new WindowsCredentialStore(fixture.Native), fixture.DirectoryPath, busy, clock: fixture.Clock,
            runtimeFactory: (credentials, clock) => ConversationRuntime.ForFixture(
                OpenAiTextGenerationAdapter.CreateForFixture(fixture.Llm, credentials, clock), null, null, new(), clock,
                chat: target => ChatCompletionsTextGenerationAdapter.CreateForFixture(target.BaseUrl, fixture.Chat,
                    target.Keyless ? null : credentials, clock)),
            options: new() { AmbientChance = 1 }, random: () => 0);

    private static HttpResponseMessage ChatAnswer(string text) => TextRecordingHandler.Sse(
        "data: {\"id\":\"chat-fixture\",\"object\":\"chat.completion.chunk\",\"model\":\"server-model\",\"choices\":[{\"index\":0," +
        $"\"delta\":{{\"role\":\"assistant\",\"content\":\"{text}\"}},\"finish_reason\":\"stop\"}}]}}\n\ndata: [DONE]\n\n");

    // Thinking on this PC: a Chat Completions server on loopback, which the local conversation shares.
    private static async Task UseLocalThinking(LiveFixture fixture)
    {
        var loaded = await fixture.Store.LoadAsync();
        var old = loaded.Settings!.Setup!.Routes.Single(item => item.Role == SetupRole.Llm);
        var changed = SetupSettings.QueueReplacedCredential(
            ChatCompletionsSetup.SelectRoute(loaded.Settings!, "http://127.0.0.1:1234/v1", "local-model"), old);
        var route = changed.Setup!.Routes.Single(item => item.Role == SetupRole.Llm);
        await fixture.Save(SetupSettings.ReplaceRoute(changed, route with { Consent = route.Selection() }));
        Assert.True(fixture.Controller.Configuration!.NetworkThinking);
    }

    private static async Task<T> Advance<T>(LiveFixture fixture, Task<T> task)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!task.IsCompleted)
        {
            fixture.Clock.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(1, timeout.Token);
        }
        return await task;
    }

    [Fact]
    public async Task DiscordReplyEngineAnswersWithThePersonaAndDiscordFraming()
    {
        await using var fixture = await LiveFixture.Create();
        fixture.Answer("Hi Bo, welcome in!");
        await using var engine = Engine(fixture, () => true);

        // A cloud route never waits for the local conversation, even while it replies.
        var reply = await Advance(fixture, engine.ReplyAsync(Turn("hi there"), default));

        Assert.Equal("Hi Bo, welcome in!", reply?.Text);
        Assert.Equal(1, fixture.Llm.Calls);
        using var body = JsonDocument.Parse(fixture.Llm.Body);
        var instructions = body.RootElement.GetProperty("instructions").GetString()!;
        // The live conversation's persona first, then the Discord framing.
        Assert.Contains("Be a helpful conversational companion.", instructions);
        Assert.Contains("#general channel of a Discord server", instructions);
        Assert.True(instructions.IndexOf("Be a helpful", StringComparison.Ordinal) < instructions.IndexOf("Discord", StringComparison.Ordinal));
        Assert.True(!body.RootElement.TryGetProperty("tools", out var tools) || tools.GetArrayLength() == 0);
        var input = body.RootElement.GetProperty("input").EnumerateArray()
            .Select(item => (item.GetProperty("role").GetString(), item.GetProperty("content").GetString())).ToArray();
        Assert.Equal(("user", "Ana: morning all"), input[0]);
        Assert.Equal(("assistant", "Morning!"), input[1]);
        Assert.StartsWith("Bo: hi there", input[2].Item2);
        var status = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.DirectoryPath, DiscordReplyEngine.StatusFile))).RootElement;
        Assert.True(status.GetProperty("wired").GetBoolean());
        Assert.Equal(1, status.GetProperty("replies").GetInt32());
        Assert.Equal("OpenAi", status.GetProperty("route").GetProperty("routeType").GetString());
        Assert.DoesNotContain("hi there", File.ReadAllText(Path.Combine(fixture.DirectoryPath, DiscordReplyEngine.StatusFile)));
    }

    [Fact]
    public async Task DiscordReplyEngineLetsTheLocalConversationGoFirstOnASharedModel()
    {
        await using var fixture = await LiveFixture.Create();
        await UseLocalThinking(fixture);
        var busy = true;
        fixture.Chat.Respond = (_, _) => Task.FromResult(ChatAnswer("Hello from Discord."));
        await using var engine = Engine(fixture, () => Volatile.Read(ref busy));

        // An ambient turn while the local conversation replies is skipped without a request.
        Assert.Null(await engine.ReplyAsync(Turn("nice weather", addressed: false), default));
        Assert.Equal("local.busy", engine.Replier.Stats.LastSkip);
        Assert.Equal(0, fixture.Chat.Calls);

        // An addressed turn waits until the local conversation has been quiet for a while.
        var waiting = engine.ReplyAsync(Turn("are you there?"), default);
        for (var i = 0; i < 20; i++) fixture.Clock.Advance(TimeSpan.FromMilliseconds(250));
        await Task.Delay(20);
        Assert.False(waiting.IsCompleted);
        Assert.Equal(0, fixture.Chat.Calls);
        Volatile.Write(ref busy, false);
        Assert.Equal("Hello from Discord.", (await Advance(fixture, waiting))?.Text);
        Assert.Equal(1, fixture.Chat.Calls);
    }

    [Fact]
    public async Task DiscordReplyEngineStopsForALocalReplyAndAsksAgainOnceQuiet()
    {
        await using var fixture = await LiveFixture.Create();
        await UseLocalThinking(fixture);
        var busy = false;
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Chat.Respond = async (_, token) =>
        {
            if (fixture.Chat.Calls == 1)
            {
                first.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            return ChatAnswer("Second try.");
        };
        await using var engine = Engine(fixture, () => Volatile.Read(ref busy));

        var reply = engine.ReplyAsync(Turn("hello?"), default);
        await first.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Volatile.Write(ref busy, true);
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(200));
        await Task.Delay(20);
        Volatile.Write(ref busy, false);

        Assert.Equal("Second try.", (await Advance(fixture, reply))?.Text);
        Assert.Equal(2, fixture.Chat.Calls);
        var status = File.ReadAllText(Path.Combine(fixture.DirectoryPath, DiscordReplyEngine.StatusFile));
        Assert.Contains("\"preemptedByLocal\": 1", status);
    }
}
