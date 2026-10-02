using System.Runtime.CompilerServices;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;
using Martlet.Providers.Tests;

namespace Martlet.Conversation.Tests;

// NOT AI: a controlled host client stands in for a paired Martlet host's Ollama.
public sealed class HostTextTests
{
    private sealed class FakeHost(params string[] deltas) : IHostTextClient
    {
        internal BoundedTextInput? Input { get; private set; }
        internal int Calls { get; private set; }

        public async IAsyncEnumerable<string> StreamAsync(HostTextTarget target, TextModelSelection model, BoundedTextInput input,
            TextGenerationLimits limits, CorrelationIds ids, long epoch, DateTimeOffset deadline, GenerationSettings? generation,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls++;
            Input = input;
            foreach (var delta in deltas)
            {
                await Task.Yield();
                yield return delta;
            }
        }
    }

    private static readonly HostTextTarget Target = new("https://192.168.1.20:9443", "gpu-host",
        "sha256:" + new string('a', 64), "desktop-test", Guid.NewGuid());
    private static readonly TextModelSelection Model = new(SelfHostSetup.GatewayOllamaAlias, "llama3.2-3b");

    private static async Task<(ConversationSnapshot Snapshot, ConversationTurn Turn)> RunAsync(FakeHost host, Uri origin)
    {
        var clock = new RuntimeClock();
        var permissions = new FixturePermissions(clock)
        {
            Text = (action, _) =>
            {
                var until = clock.GetUtcNow().AddSeconds(30);
                return ValueTask.FromResult<AuthorizedTextOperation?>(new(new(new(origin, ProviderRole.Llm, Model.UpstreamModelId),
                    action.Model, action.Context.Ids, action.Context.Epoch, action.Limits, until, true, true), new(action.Budget, until)));
            }
        };
        await using var runtime = ConversationRuntime.ForFixture(
            OpenAiTextGenerationAdapter.CreateForFixture(new TextRecordingHandler(), new FixtureCredentials(), clock),
            null, null, new(), clock, hostText: host);
        var turn = runtime.Start(new ConversationRequest(new BoundedTextInput("Hi", "Be brief."), Model, new(), new(), host: Target),
            permissions);
        return (await Harness.Finish(turn, clock), turn);
    }

    [Fact]
    public async Task Host_route_streams_the_reply_from_the_paired_hosts_model()
    {
        var host = new FakeHost("Hello", " from your host.");
        var (snapshot, turn) = await RunAsync(host, new Uri(Target.Origin));
        Assert.Equal(ConversationState.Completed, snapshot.State);
        Assert.Equal("Hello from your host.", turn.Content.Text);
        Assert.Equal("Be brief.", host.Input!.Personality);
    }

    [Fact]
    public async Task Host_route_refuses_an_authorization_for_another_destination()
    {
        var host = new FakeHost("Never sent.");
        var (snapshot, _) = await RunAsync(host, new Uri("https://api.openai.com"));
        Assert.Equal(ConversationState.Failed, snapshot.State);
        Assert.Equal(ProviderFailureCode.OriginRejected, snapshot.ProviderFailure);
        Assert.Equal(0, host.Calls);
    }
}
