using System.Net;
using Martlet.Core.Audio;
using Martlet.Core.Contracts;

namespace Martlet.Providers.Tests;

internal static class SpeechFixtures
{
    public const string Model = "gpt-4o-mini-tts-2025-12-15";
    public static SpeechSynthesisSelection Selection => new("speech", Model, "alloy", SpeechOutputFormat.Pcm24KhzMono16Le);
    public static ProviderCredentialBinding Binding => new(OpenAiSpeechSynthesisCatalog.Origin, ProviderRole.Tts, Model);

    public static SpeechDisclosureAuthorization Authorize(ProviderRequestContext context, BoundedSpeechInput input,
        SpeechSynthesisLimits limits, DateTimeOffset? expiresAt = null, ProviderCredentialBinding? binding = null,
        SpeechSynthesisSelection? selection = null, CorrelationIds? ids = null, long? epoch = null,
        bool text = true, bool charges = true, bool disclosure = true) =>
        new(binding ?? Binding, selection ?? Selection, input, ids ?? context.Ids, epoch ?? context.Epoch,
            limits, expiresAt ?? ProviderFixtures.Now.AddMinutes(2), text, charges, disclosure);

    public static byte[] Audio(int samples = 1001) =>
        Enumerable.Range(0, samples * 2).Select(i => (byte)(i % 251)).ToArray();

    public static HttpResponseMessage Pcm(Stream body, int status = 200, long? length = null,
        string? media = "application/octet-stream")
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StreamContent(body) };
        if (media is not null)
            response.Content.Headers.TryAddWithoutValidation("Content-Type", media);
        if (length is not null)
            response.Content.Headers.ContentLength = length;
        return response;
    }

    public static TextRecordingHandler Handler(byte[]? bytes = null, int fragment = 4096) => new()
    {
        Respond = (_, _) => Task.FromResult(Pcm(new FragmentedTextBody(bytes ?? Audio(), fragment)))
    };

    public static async Task<(List<PcmFrame> Frames, SpeechSynthesisResult Result)> Collect(SpeechSynthesisStream stream,
        CancellationToken token = default)
    {
        var frames = new List<PcmFrame>();
        stream.StartedEvent.Validate();
        stream.Capabilities.Validate();
        long samples = 0;
        await foreach (var frame in stream.WithCancellation(token))
        {
            Assert.Equal(stream.StartedEvent.Ids, frame.Ids);
            Assert.Equal(stream.StartedEvent.Epoch, frame.Epoch);
            Assert.Equal(frames.Count, frame.Sequence);
            Assert.Equal(samples, frame.SampleOffset);
            Assert.Equal(stream.Format, frame.Format);
            // Reconstruct through the actual Core boundary, not a test-side substitute validator.
            var validated = new PcmFrame(frame.Ids, frame.Epoch, frame.Sequence, frame.SampleOffset, frame.Format, frame.Data.Span);
            Assert.Equal(frame.SamplesPerChannel, validated.SamplesPerChannel);
            samples += frame.SamplesPerChannel;
            frames.Add(frame);
        }
        var result = Assert.IsType<SpeechSynthesisResult>(stream.Result);
        result.ToTerminalEvent().Validate();
        Assert.Equal(samples, result.DeliveredSampleCount);
        Assert.Null(result.EstimatedCost);
        Assert.Null(result.BilledInputTokens);
        Assert.Null(result.BilledOutputTokens);
        if (result.Failure is { } failure)
        {
            Assert.Equal(Stage.Synthesis, failure.Error.Stage);
            Assert.False(failure.Error.Retryable);
        }
        return (frames, result);
    }

    public static async Task<(List<PcmFrame> Frames, SpeechSynthesisResult Result)> Run(TextRecordingHandler handler,
        SpeechSynthesisLimits? limits = null)
    {
        using var adapter = OpenAiSpeechSynthesisAdapter.CreateForFixture(handler, new FixtureCredentials(), new FixtureClock());
        var context = ProviderFixtures.Context();
        var input = new BoundedSpeechInput("An authored fixture segment.");
        limits ??= new();
        return await Collect(adapter.Stream(context, Selection, input, limits, Authorize(context, input, limits)));
    }
}
