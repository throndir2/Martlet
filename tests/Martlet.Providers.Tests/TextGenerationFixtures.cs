using System.Net;
using System.Text;
using System.Text.Json;
using Martlet.Core.Contracts;
using Martlet.Core.Streaming;

namespace Martlet.Providers.Tests;

internal static class TextFixtures
{
    public const string Model = "gpt-4.1-mini-2025-04-14";
    public static TextModelSelection Selection => new("conversation", Model);
    public static ProviderCredentialBinding Binding => new(OpenAiTranscriptionCatalog.Origin, ProviderRole.Llm, Model);
    public static TextDisclosureAuthorization Authorize(ProviderRequestContext context, TextGenerationLimits limits,
        DateTimeOffset? expiresAt = null, ProviderCredentialBinding? binding = null, TextModelSelection? model = null,
        CorrelationIds? ids = null, long? epoch = null, bool text = true, bool charges = true) =>
        new(binding ?? Binding, model ?? Selection, ids ?? context.Ids, epoch ?? context.Epoch,
            limits, expiresAt ?? ProviderFixtures.Now.AddMinutes(1), text, charges);

    public static string Event(string type, int sequence, object fields)
    {
        var properties = JsonSerializer.SerializeToElement(fields).EnumerateObject()
            .ToDictionary(x => x.Name, x => (object?)x.Value);
        properties.Add("type", type);
        properties.Add("sequence_number", sequence);
        return $"event: {type}\ndata: {JsonSerializer.Serialize(properties)}\n\n";
    }

    public static object Part(string text, bool refusal = false) => refusal
        ? new { type = "refusal", refusal = text }
        : (object)new { type = "output_text", text, annotations = Array.Empty<object>(), logprobs = Array.Empty<object>() };

    public static object Item(string text, string status = "completed", bool refusal = false) =>
        new { id = "msg_fixture", type = "message", role = "assistant", status, content = new[] { Part(text, refusal) } };

    public static object Response(string text = "", string status = "in_progress", bool refusal = false, object? usage = null) => new
    {
        id = "resp_fixture", @object = "response", model = Model, status, store = false, background = false,
        output = status == "in_progress" ? Array.Empty<object>() : new[] { Item(text, status == "incomplete" ? "incomplete" : "completed", refusal) },
        usage, error = (object?)null, incomplete_details = (object?)null
    };

    public static List<string> Trace(string text = "Hello fixture.", bool refusal = false)
    {
        string prefix = refusal ? "response.refusal" : "response.output_text";
        var trace = new List<string>
        {
            Event("response.created", 0, new { response = Response() }),
            Event("response.in_progress", 1, new { response = Response() }),
            Event("response.output_item.added", 2, new { output_index = 0,
                item = new { id = "msg_fixture", type = "message", role = "assistant", status = "in_progress", content = Array.Empty<object>() } }),
            Event("response.content_part.added", 3, new { output_index = 0, content_index = 0, item_id = "msg_fixture", part = Part("", refusal) }),
            Event(prefix + ".delta", 4, new { output_index = 0, content_index = 0, item_id = "msg_fixture", delta = text }),
            Event(prefix + ".done", 5, refusal
                ? new { output_index = 0, content_index = 0, item_id = "msg_fixture", refusal = text }
                : (object)new { output_index = 0, content_index = 0, item_id = "msg_fixture", text }),
            Event("response.content_part.done", 6, new { output_index = 0, content_index = 0, item_id = "msg_fixture", part = Part(text, refusal) }),
            Event("response.output_item.done", 7, new { output_index = 0, item = Item(text, refusal: refusal) }),
            Event("response.completed", 8, new { response = Response(text, "completed", refusal,
                new { input_tokens = 13, output_tokens = 4, total_tokens = 17,
                    input_tokens_details = new { cached_tokens = 0 }, output_tokens_details = new { reasoning_tokens = 0 } }) })
        };
        return trace;
    }

    public static async Task<(List<ProviderEvent> Events, TextGenerationResult Result)> Collect(TextGenerationStream stream,
        CancellationToken token = default)
    {
        var result = new List<ProviderEvent>();
        using var validator = new ProviderSequenceValidator(new()
        {
            Ids = ProviderFixtures.Context().Ids, Epoch = ProviderFixtures.Context().Epoch, Capabilities = stream.Capabilities
        });
        await foreach (var item in stream.WithCancellation(token))
        {
            item.Validate();
            var update = validator.Accept(item);
            Assert.True(update.Decision is SequenceDecision.Accepted or SequenceDecision.Terminal, update.ToString());
            Assert.NotEqual(SequenceIssue.InvalidOrder, update.Snapshot.Issue);
            while (validator.TryReadText(out _)) { }
            result.Add(item);
        }
        Assert.NotNull(stream.Result);
        stream.Result.ToTurnResult().Validate();
        Assert.Equal(stream.Result.ToTurnResult().Outcome, validator.Snapshot.Result!.Outcome);
        return (result, stream.Result);
    }
}

internal sealed class TextRecordingHandler : HttpMessageHandler
{
    public int Calls;
    public bool Disposed { get; private set; }
    public byte[] Body { get; private set; } = [];
    public Action? BeforeSerialization { get; set; }
    public Action<HttpRequestMessage>? Inspect { get; set; }
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
        (_, _) => Task.FromResult(Sse(string.Concat(TextFixtures.Trace())));

    public static HttpResponseMessage Sse(string text, int status = 200, int fragment = 4096) =>
        Sse(new FragmentedTextBody(Encoding.UTF8.GetBytes(text), fragment), status);
    public static HttpResponseMessage Sse(Stream stream, int status = 200)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentType = new("text/event-stream");
        return response;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Interlocked.Increment(ref Calls);
        Inspect?.Invoke(request);
        using var output = new MemoryStream();
        BeforeSerialization?.Invoke();
        try { await request.Content!.CopyToAsync(output, token); }
        finally { Body = output.ToArray(); }
        return await Respond(request, token);
    }

    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
}

internal sealed class FragmentedTextBody(byte[] bytes, int fragment = 1) : Stream
{
    public int BytesRead { get; private set; }
    public bool Disposed { get; private set; }
    public Action? OnRead { get; set; }
    public Func<CancellationToken, Task>? BeforeRead { get; set; }
    public bool IgnoreCancellation { get; set; }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        OnRead?.Invoke();
        if (BeforeRead is not null)
            await BeforeRead(token);
        if (!IgnoreCancellation) token.ThrowIfCancellationRequested();
        int count = Math.Min(Math.Min(fragment, buffer.Length), bytes.Length - BytesRead);
        bytes.AsMemory(BytesRead, count).CopyTo(buffer);
        BytesRead += count;
        return count;
    }
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
