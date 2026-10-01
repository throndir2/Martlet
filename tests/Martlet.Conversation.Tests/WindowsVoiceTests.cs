using System.Runtime.CompilerServices;
using Martlet.Audio;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;
using Martlet.Providers.Tests;

namespace Martlet.Conversation.Tests;

// NOT AI: a controlled client stands in for an installed Windows voice.
public sealed class WindowsVoiceTests
{
    private sealed class FakeVoice(params int[] samples) : IWindowsVoiceClient
    {
        internal int Calls { get; private set; }
        internal string? Text { get; private set; }

        public async IAsyncEnumerable<byte[]> StreamAsync(WindowsVoiceTarget target, BoundedSpeechInput input, SpeechSynthesisLimits limits,
            DateTimeOffset deadline, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls++;
            Text = input.Text;
            foreach (var count in samples)
            {
                await Task.Yield();
                yield return new byte[count * 2];
            }
        }
    }

    private static readonly WindowsVoiceTarget Target = new("TTS_MS_EN-US_ZIRA_11.0");

    private static async Task<ConversationSnapshot> SpeakAsync(Harness h, Uri origin)
    {
        h.Answer("Hello from Windows.");
        h.Permissions.Speech = (action, _) =>
        {
            var until = h.Clock.GetUtcNow().AddSeconds(30);
            return ValueTask.FromResult<AuthorizedSpeechOperation?>(new(new(new(origin, ProviderRole.Tts, WindowsSpeechSetup.TtsModelId),
                action.Selection, action.Input, action.Context.Ids, action.Context.Epoch, action.Limits, until, true, true, true),
                new(action.Budget, until)));
        };
        var request = new ConversationRequest(new BoundedTextInput("Say hello."), TextFixtures.Selection, new(), new(),
            new(WindowsVoiceSynthesisStream.Selection(Target), new(OutputPolicy.DefaultAtStart), Harness.SpeechLimits), windowsVoice: Target);
        return await Harness.Finish(h.Start(request));
    }

    [Fact]
    public async Task Windows_voice_speaks_the_reply_through_the_playback_sink()
    {
        var voice = new FakeVoice(1_000, 2_000);
        await using var h = new Harness(windowsVoice: voice);
        var result = await SpeakAsync(h, new Uri(SelfHostSetup.LocalOrigin));
        Assert.Equal(ConversationState.Completed, result.State);
        Assert.Equal(3_000, result.AcceptedSamples);
        Assert.Equal("Hello from Windows.", voice.Text);
        Assert.Equal(0, h.Tts.Calls);
    }

    [Fact]
    public async Task Windows_voice_refuses_an_authorization_for_another_destination()
    {
        var voice = new FakeVoice(1_000);
        await using var h = new Harness(windowsVoice: voice);
        var result = await SpeakAsync(h, new Uri("https://api.openai.com"));
        Assert.Equal(ConversationState.Partial, result.State);
        Assert.Equal(ProviderFailureCode.OriginRejected, result.ProviderFailure);
        Assert.Equal(0, voice.Calls);
    }
}
