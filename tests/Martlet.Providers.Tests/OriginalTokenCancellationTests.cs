using System.Net;
using System.Text;
using Martlet.Core.Contracts;

namespace Martlet.Providers.Tests;

public sealed class OriginalTokenCancellationTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var boundary in new[] { "credentials", "serialization", "body" })
            foreach (var role in new[] { ProviderRole.Stt, ProviderRole.Llm, ProviderRole.Tts })
            {
                yield return new object[] { role, false, boundary };
                if (role != ProviderRole.Stt) yield return new object[] { role, true, boundary };
            }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Original_sources_win_over_blocked_newer_callbacks(ProviderRole role, bool enumeratorToken, string boundary)
    {
        using var caller = new CancellationTokenSource();
        using var callbackRelease = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new FixtureClock { DeferTimerCallbacks = true };
        var credentials = new FixtureCredentials();
        if (boundary == "credentials")
            credentials.Resolve = async (binding, _) =>
            {
                entered.TrySetResult();
                await resume.Task;
                return new(binding, ProviderFixtures.Secret);
            };
        var handler = new TextRecordingHandler();
        if (boundary == "serialization")
            handler.BeforeSerialization = () =>
            {
                entered.TrySetResult();
                resume.Task.GetAwaiter().GetResult();
            };
        byte[] bytes = role switch
        {
            ProviderRole.Stt => Encoding.UTF8.GetBytes("""{"text":"Canceled fixture text."}"""),
            ProviderRole.Llm => Encoding.UTF8.GetBytes(string.Concat(TextFixtures.Trace())),
            _ => SpeechFixtures.Audio()
        };
        var body = new FragmentedTextBody(bytes) { IgnoreCancellation = true };
        if (boundary == "body")
            body.BeforeRead = async _ => { entered.TrySetResult(); await resume.Task; };
        handler.Respond = (_, _) =>
        {
            if (role == ProviderRole.Llm) return Task.FromResult(TextRecordingHandler.Sse(body));
            if (role == ProviderRole.Tts) return Task.FromResult(SpeechFixtures.Pcm(body));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
            response.Content.Headers.ContentType = new("application/json");
            return Task.FromResult(response);
        };
        async Task<(bool Canceled, int Content)> RunAsync()
        {
            var context = ProviderFixtures.Context();
            if (role == ProviderRole.Stt)
            {
                using var adapter = OpenAiTranscriptionAdapter.CreateForFixture(handler, credentials, clock);
                var limits = new TranscriptionLimits();
                var result = await adapter.TranscribeAsync(context, "gpt-transcribe", BoundedWaveAudio.FromWave(ProviderFixtures.Wave()),
                    limits, ProviderFixtures.Authorize(context, limits), caller.Token);
                return (result.Outcome == TranscriptionOutcome.Canceled, result.Text?.Length ?? 0);
            }
            if (role == ProviderRole.Llm)
            {
                using var adapter = OpenAiTextGenerationAdapter.CreateForFixture(handler, credentials, clock);
                var limits = new TextGenerationLimits();
                var stream = adapter.Stream(context, TextFixtures.Selection, new("Explicit fixture input."), limits,
                    TextFixtures.Authorize(context, limits), enumeratorToken ? default : caller.Token);
                int content = 0;
                await foreach (var item in stream.WithCancellation(enumeratorToken ? caller.Token : default))
                    if (item.Kind == ProviderEventKind.TextDelta) content += item.Text!.Length;
                return (stream.Result!.Outcome == TextGenerationOutcome.Canceled, content);
            }
            else
            {
                using var adapter = OpenAiSpeechSynthesisAdapter.CreateForFixture(handler, credentials, clock);
                var limits = new SpeechSynthesisLimits();
                var input = new BoundedSpeechInput("Explicit fixture segment.");
                var stream = adapter.Stream(context, SpeechFixtures.Selection, input, limits,
                    SpeechFixtures.Authorize(context, input, limits), enumeratorToken ? default : caller.Token);
                int content = 0;
                await foreach (var frame in stream.WithCancellation(enumeratorToken ? caller.Token : default))
                    content += frame.Data.Length;
                return (stream.Result!.Outcome == SpeechSynthesisOutcome.Canceled, content);
            }
        }
        var operation = Task.Run(RunAsync);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        using var registration = caller.Token.Register(() =>
        {
            callbackEntered.TrySetResult();
            callbackRelease.Wait();
        });
        var cancellation = caller.CancelAsync();
        try
        {
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            resume.TrySetResult();
            var result = await operation.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(boundary == "credentials" ? 0 : 1, handler.Calls);
            if (boundary == "serialization") Assert.Empty(handler.Body);
            Assert.True(result.Canceled);
            Assert.Equal(0, result.Content);
            Assert.False(cancellation.IsCompleted);
        }
        finally
        {
            callbackRelease.Set();
            resume.TrySetResult();
            await cancellation;
            await operation.WaitAsync(TimeSpan.FromSeconds(20));
        }
    }
}
