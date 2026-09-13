using System.Net;
using Martlet.Core.Contracts;

namespace Martlet.Providers;

internal static class OpenAiTransport
{
    public static Uri Origin { get; } = new("https://api.openai.com");

    public static bool IsApprovedOrigin(Uri? origin) => origin is
    {
        IsAbsoluteUri: true, Scheme: "https", Port: 443, AbsolutePath: "/", Query: "", Fragment: "", UserInfo: ""
    } && string.Equals(origin.IdnHost, "api.openai.com", StringComparison.Ordinal);

    public static SocketsHttpHandler CreateProductionHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        Credentials = null,
        DefaultProxyCredentials = null,
        MaxResponseHeadersLength = 16,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    };

    public static async Task<(ReadOnlyMemory<byte> Bytes, ProviderFailureCode? Failure)> ReadBoundedAsync(
        HttpContent content, int maximum, CancellationToken cancellationToken)
    {
        long? expected = content.Headers.ContentLength;
        if (expected > maximum)
            return (ReadOnlyMemory<byte>.Empty, ProviderFailureCode.ResponseTooLarge);
        using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        byte[] buffer = new byte[maximum + 1];
        int length = 0;
        while (length < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            length += read;
        }
        if (length > maximum)
            return (ReadOnlyMemory<byte>.Empty, ProviderFailureCode.ResponseTooLarge);
        if (expected is not null && expected != length)
            return (ReadOnlyMemory<byte>.Empty, ProviderFailureCode.ResponseTruncated);
        return (buffer.AsMemory(0, length), null);
    }

    public static TimeSpan? RetryAdvice(HttpResponseMessage response, TimeProvider clock)
    {
        var header = response.Headers.RetryAfter;
        var delay = header?.Delta ?? (header?.Date - clock.GetUtcNow());
        return delay is null ? null : TimeSpan.FromSeconds(Math.Clamp(delay.Value.TotalSeconds, 0, 300));
    }
}

// Guards both timer-driven cancellation and deferred callbacks/clock changes at trust boundaries.
internal sealed class ProviderRequestWindow : IDisposable
{
    private readonly TimeProvider clock;
    private readonly long startedAt;
    private readonly TimeSpan requestWindow;
    private readonly TimeSpan consentWindow;
    private readonly DateTimeOffset expiresAt;
    private readonly CancellationToken sourceToken;
    private readonly CancellationToken operationToken;
    private readonly CancellationTokenSource deadline;
    private readonly CancellationTokenSource linked;
    public CancellationToken Token => linked.Token;
    public bool DeadlineCanceled => deadline.IsCancellationRequested;

    public ProviderRequestWindow(TimeProvider clock, long startedAt, DateTimeOffset startedUtc,
        DateTimeOffset requestDeadline, TimeSpan maximum, DateTimeOffset expiresAt, CancellationToken token)
        : this(clock, startedAt, startedUtc, requestDeadline, maximum, expiresAt, token, CancellationToken.None)
    {
    }

    internal ProviderRequestWindow(TimeProvider clock, long startedAt, DateTimeOffset startedUtc,
        DateTimeOffset requestDeadline, TimeSpan maximum, DateTimeOffset expiresAt, CancellationToken token,
        CancellationToken operationToken)
    {
        this.clock = clock;
        this.startedAt = startedAt;
        this.expiresAt = expiresAt;
        sourceToken = token;
        this.operationToken = operationToken;
        var remaining = requestDeadline - startedUtc;
        requestWindow = remaining < maximum ? remaining : maximum;
        consentWindow = expiresAt - startedUtc;
        var timeout = (requestWindow < consentWindow ? requestWindow : consentWindow) - clock.GetElapsedTime(startedAt);
        if (timeout <= TimeSpan.Zero)
            throw new RequestCutoffException(requestWindow <= consentWindow
                ? ProviderFailureCode.DeadlineExceeded : ProviderFailureCode.ConsentExpired);
        deadline = new CancellationTokenSource(timeout, clock);
        linked = CancellationTokenSource.CreateLinkedTokenSource(token, operationToken, deadline.Token);
    }

    public void EnsureActive()
    {
        sourceToken.ThrowIfCancellationRequested();
        operationToken.ThrowIfCancellationRequested();
        Token.ThrowIfCancellationRequested();
        var elapsed = clock.GetElapsedTime(startedAt);
        if (elapsed >= requestWindow)
            throw new RequestCutoffException(ProviderFailureCode.DeadlineExceeded);
        if (clock.GetUtcNow() >= expiresAt || elapsed >= consentWindow)
            throw new RequestCutoffException(ProviderFailureCode.ConsentExpired);
    }

    public void Dispose()
    {
        linked.Dispose();
        deadline.Dispose();
    }
}

internal sealed class RequestCutoffException(ProviderFailureCode code) : Exception("The provider request window ended.")
{
    public ProviderFailureCode Code { get; } = code;
}

// A handler cannot silently replay an authorized body, including after its synchronous cutoff.
internal sealed class SingleSendContent : HttpContent
{
    private readonly HttpContent inner;
    private readonly Action ensureActive;
    private int sent;

    public SingleSendContent(HttpContent inner, Action ensureActive)
    {
        this.inner = inner;
        this.ensureActive = ensureActive;
        foreach (var header in inner.Headers)
            Headers.TryAddWithoutValidation(header.Key, header.Value);
    }

    protected override bool TryComputeLength(out long length)
    {
        length = inner.Headers.ContentLength ?? 0;
        return inner.Headers.ContentLength is not null;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        ensureActive();
        if (Interlocked.Exchange(ref sent, 1) != 0)
            throw new HttpRequestException("A provider request body cannot be replayed.");
        return inner.CopyToAsync(stream, context, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }
}
