using System.Runtime.CompilerServices;
using Martlet.Audio;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;
using Martlet.Providers.Tests;

namespace Martlet.Conversation.Tests;

// NOT AI: a controlled host speech client stands in for a paired Martlet host's F5 voice.
public sealed class HostSpeechTests
{
    private sealed class FakeVoice(params int[] samples) : IHostSpeechClient
    {
        internal int Calls { get; private set; }
        internal string? Text { get; private set; }

        public async IAsyncEnumerable<byte[]> StreamAsync(HostSpeechTarget target, BoundedSpeechInput input, CorrelationIds ids,
            long epoch, DateTimeOffset deadline, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls++;
            Text = input.Text;
            foreach (var count in samples)
            {
                await Task.Yield();
                yield return Enumerable.Range(0, count * 2).Select(i => (byte)i).ToArray();
            }
        }
    }

    private static readonly Guid Preset = Guid.NewGuid();
    private static readonly HostSpeechTarget Target = new("https://192.168.1.20:9443", "gpu-host",
        "sha256:" + new string('a', 64), "desktop-test", Guid.NewGuid(), "f5tts-v1-base", Preset, new string('b', 64));
    private static readonly SpeechSynthesisSelection Selection = new(SelfHostSetup.GatewayF5Alias, "f5tts-v1-base",
        Preset.ToString("N"), SpeechOutputFormat.Pcm24KhzMono16Le);

    private static async Task<ConversationSnapshot> SpeakAsync(Harness h, Uri origin)
    {
        h.Answer("Hello from the host.");
        h.Permissions.Speech = (action, _) =>
        {
            var until = h.Clock.GetUtcNow().AddSeconds(30);
            return ValueTask.FromResult<AuthorizedSpeechOperation?>(new(new(new(origin, ProviderRole.Tts, Target.ModelId),
                action.Selection, action.Input, action.Context.Ids, action.Context.Epoch, action.Limits, until, true, true, true),
                new(action.Budget, until)));
        };
        var request = new ConversationRequest(new BoundedTextInput("Say hello."), TextFixtures.Selection, new(), new(),
            new(Selection, new(OutputPolicy.DefaultAtStart), Harness.SpeechLimits), hostSpeech: Target);
        return await Harness.Finish(h.Start(request));
    }

    [Fact]
    public async Task Host_voice_speaks_the_reply_in_20_ms_frames_through_the_playback_sink()
    {
        var voice = new FakeVoice(1_000, 2_000);
        await using var h = new Harness(hostSpeech: voice);
        var result = await SpeakAsync(h, new Uri(Target.Origin));
        Assert.Equal(ConversationState.Completed, result.State);
        Assert.Equal(3_000, result.AcceptedSamples);
        Assert.Equal("Hello from the host.", voice.Text);
        Assert.Equal(0, h.Tts.Calls);
    }

    [Fact]
    public async Task Host_voice_refuses_an_authorization_for_another_destination()
    {
        var voice = new FakeVoice(1_000);
        await using var h = new Harness(hostSpeech: voice);
        var result = await SpeakAsync(h, new Uri("https://api.openai.com"));
        // The reply text stands; only its voice was refused.
        Assert.Equal(ConversationState.Completed, result.State);
        Assert.True(result.SpeechFailed);
        Assert.Equal(ProviderFailureCode.OriginRejected, result.ProviderFailure);
        Assert.Equal(0, voice.Calls);
    }
}
