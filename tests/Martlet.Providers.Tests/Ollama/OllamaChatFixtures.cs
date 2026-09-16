using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Martlet.Core.Contracts;
using Martlet.Providers.Ollama;

namespace Martlet.Providers.Tests.Ollama;

internal static class OllamaFixtures
{
    internal static OllamaLoopbackOrigin Origin => new("http://127.0.0.1:12345");
    internal static OllamaChatModelSelection Model => new("fixture", "fixture-model:v1");
    internal static OllamaChatOptions Options => new(0.25);
    internal static string Json(object value) => JsonSerializer.Serialize(value,
        new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    internal static string Frame(string text = "Hello fixture.", bool done = false, string? reason = null,
        bool usage = false)
    {
        var fields = new Dictionary<string, object?>
        {
            ["model"] = Model.RequestModel, ["created_at"] = "2026-09-16T06:40:00.123456789Z",
            ["message"] = new { role = "assistant", content = text }, ["done"] = done
        };
        if (reason is not null) fields["done_reason"] = reason;
        if (usage)
        {
            fields["prompt_eval_count"] = 12;
            fields["prompt_eval_cached_count"] = 0;
            fields["eval_count"] = 3;
            fields["total_duration"] = 100;
            fields["load_duration"] = 10;
            fields["prompt_eval_duration"] = 20;
            fields["eval_duration"] = 70;
        }
        return Json(fields) + "\n";
    }
    internal static string Trace => Frame() + Frame("", true, "stop", usage: true);
    internal static HttpResponseMessage Response(Stream body, int status = 200, long? length = null)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StreamContent(body) };
        response.Content.Headers.ContentType = new("application/x-ndjson");
        if (length is { } value) response.Content.Headers.ContentLength = value;
        return response;
    }
    internal static TextRecordingHandler Handler(string? trace = null, int fragment = 4096) => new()
    {
        Respond = (_, _) => Task.FromResult(Response(new FragmentedTextBody(Encoding.UTF8.GetBytes(trace ?? Trace), fragment)))
    };
    internal static OllamaChatStream Stream(OllamaChatAdapter adapter, TextGenerationLimits? limits = null,
        CancellationToken caller = default, CancellationToken operation = default,
        ProviderRequestContext? context = null, BoundedTextInput? input = null) =>
        adapter.Stream(context ?? ProviderFixtures.Context(), Origin, Model, input ?? new("Fixture input."),
            Options, limits ?? new(), caller, operation);

    internal static async Task<(List<ProviderEvent> Events, OllamaChatResult Result)> Collect(OllamaChatStream stream,
        CancellationToken token = default)
    {
        var events = new List<ProviderEvent>();
        await foreach (var item in stream.WithCancellation(token))
        {
            var copy = ContractJson.Read<ProviderEvent>(ContractJson.Write(item));
            Assert.Equal(item, copy);
            events.Add(item);
        }
        await stream.OwnershipRelease;
        Assert.NotNull(stream.Result);
        stream.Result.ToTurnResult().Validate();
        Assert.Equal(EvidenceProvenance.Fixture, stream.Result.Provenance);
        Assert.Equal(Enumerable.Range(0, events.Count).Select(i => (long)i), events.Select(e => e.Sequence));
        Assert.Single(events, e => e.IsTerminal);
        return (events, stream.Result);
    }

    internal static async Task<(List<ProviderEvent> Events, OllamaChatResult Result)> Run(string trace,
        TextGenerationLimits? limits = null, int fragment = 4096)
    {
        await using var adapter = OllamaChatAdapter.CreateForFixture(Handler(trace, fragment), new OllamaAuthority(), new FixtureClock());
        return await Collect(Stream(adapter, limits));
    }
}

internal sealed class OllamaAuthority : IOllamaChatAuthorizationSource
{
    internal int Calls;
    internal OllamaChatAction? LastAction;
    internal CountingLease Lease { get; } = new();
    internal Func<OllamaChatAction, CancellationToken, ValueTask<OllamaChatAuthorization?>>? Authorize;
    public ValueTask<OllamaChatAuthorization?> AuthorizeAsync(OllamaChatAction action, CancellationToken token)
    {
        Interlocked.Increment(ref Calls);
        LastAction = action;
        return Authorize is { } callback ? callback(action, token) :
            ValueTask.FromResult<OllamaChatAuthorization?>(new(action, action.EffectiveDeadline, Lease));
    }
}

internal sealed class CountingLease : IAsyncDisposable
{
    internal int Calls;
    internal bool Released;
    internal Func<ValueTask>? OnDispose;
    public async ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref Calls);
        if (OnDispose is { } callback) await callback();
        Released = true;
    }
}

internal sealed class DeferredContent(Func<CancellationToken, Task<Stream>> open) : HttpContent
{
    protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken token) => open(token);
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
}

internal sealed class CleanupBody(byte[] bytes) : MemoryStream(bytes)
{
    internal Func<ValueTask>? BeforeDispose;
    internal bool CleanupFinished;
    internal bool Fail;
    public override async ValueTask DisposeAsync()
    {
        if (BeforeDispose is { } callback) await callback();
        if (Fail) throw new IOException(ProviderFixtures.ContentCanary);
        CleanupFinished = true;
        await base.DisposeAsync();
    }
}
