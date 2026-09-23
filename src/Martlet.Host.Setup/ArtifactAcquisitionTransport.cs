using System.Net;
using System.Net.Http.Headers;
using Martlet.HostArtifacts;

namespace Martlet.Host.Setup;

internal sealed record ArtifactDownloadRequest(
    ArtifactAcquisitionSource Source,
    long Offset,
    string? EntityTag,
    DateTimeOffset? LastModifiedUtc,
    Func<CancellationToken, ValueTask> BeforeRequestAsync);

internal sealed class ArtifactDownloadResponse(
    HttpResponseMessage response,
    ArtifactAcquisitionSource source,
    string terminalOrigin,
    string? entityTag,
    DateTimeOffset? lastModifiedUtc) : IDisposable
{
    internal HttpResponseMessage Response { get; } = response;
    internal string SourceIdentityFingerprint { get; } = source.IdentityFingerprint;
    internal string LogicalSourceUrl { get; } = source.LogicalSourceUrl;
    internal string TerminalOrigin { get; } = terminalOrigin;
    internal string? EntityTag { get; } = entityTag;
    internal DateTimeOffset? LastModifiedUtc { get; } = lastModifiedUtc;

    public void Dispose() => Response.Dispose();
}

internal interface IArtifactDownloadTransport
{
    ValueTask<ArtifactDownloadResponse> SendAsync(
        ArtifactDownloadRequest request,
        CancellationToken cancellationToken);
}

internal sealed class RefusingArtifactDownloadTransport : IArtifactDownloadTransport
{
    public ValueTask<ArtifactDownloadResponse> SendAsync(
        ArtifactDownloadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.NetworkRefused);
    }
}

internal sealed class HttpsArtifactDownloadTransport : IArtifactDownloadTransport, IDisposable
{
    internal const string ApiOrigin = "https://api.github.com";
    internal const string AssetOrigin = "https://release-assets.githubusercontent.com";
    private readonly DirectHandler handler;

    internal HttpsArtifactDownloadTransport() : this(CreateProductionHandler()) { }

    internal HttpsArtifactDownloadTransport(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        this.handler = new DirectHandler(handler);
    }

    internal static SocketsHttpHandler CreateProductionHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        Proxy = null,
        UseCookies = false,
        Credentials = null,
        DefaultProxyCredentials = null,
        PreAuthenticate = false,
        AutomaticDecompression = DecompressionMethods.None,
        MaxAutomaticRedirections = 1,
        MaxResponseDrainSize = 0,
        ResponseDrainTimeout = TimeSpan.Zero,
        MaxResponseHeadersLength = 16,
        PooledConnectionLifetime = TimeSpan.Zero,
        MaxConnectionsPerServer = 1,
        ConnectTimeout = TimeSpan.FromSeconds(30),
        ActivityHeadersPropagator = null,
        ConnectCallback = ArtifactDownloadConnectionPolicy.ConnectAsync
    };

    public async ValueTask<ArtifactDownloadResponse> SendAsync(
        ArtifactDownloadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Source);
        ArgumentNullException.ThrowIfNull(request.BeforeRequestAsync);
        cancellationToken.ThrowIfCancellationRequested();
        AcquisitionGuard.Require(request.Offset >= 0, ArtifactAcquisitionFailure.InvalidPlan);
        if (request.Source.Provider != ArtifactAcquisitionProvider.GithubReleaseAsset)
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.ProviderUnsupported);
        var uri = request.Source.RequestUri;
        if (uri is null || !IsApiUri(uri))
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.InvalidPlan);

        try
        {
            for (var hop = 0; hop < 2; hop++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var message = CreateMessage(uri, request, hop == 0);
                await request.BeforeRequestAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var response = await handler.SendDirectAsync(message, cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (response.RequestMessage?.RequestUri is { } effective &&
                        !string.Equals(effective.OriginalString, uri.OriginalString, StringComparison.Ordinal))
                        throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.RedirectRejected);
                    CheckHeaderBudget(response);
                    if ((int)response.StatusCode is >= 300 and <= 399)
                    {
                        if (hop != 0 || response.StatusCode != HttpStatusCode.Redirect)
                            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.RedirectRejected);
                        uri = ReadRedirect(response);
                        response.Dispose();
                        continue;
                    }
                    if (response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.PartialContent)
                        throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.ResponseInvalid);
                    if (request.Offset == 0 && response.StatusCode != HttpStatusCode.OK)
                        throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.ResponseInvalid);
                    var (etag, modified) = ReadValidators(response);
                    SanitizeResponse(response, etag, modified);
                    return new ArtifactDownloadResponse(
                        response, request.Source, hop == 0 ? ApiOrigin : AssetOrigin, etag, modified);
                }
                catch
                {
                    response.Dispose();
                    throw;
                }
            }
        }
        catch (ArtifactAcquisitionException error)
        {
            throw new ArtifactAcquisitionException(error.Failure);
        }
        catch (OperationCanceledException)
        {
            // A handler exception can include its signed RequestUri, even on cancellation.
            throw new OperationCanceledException("Artifact transfer canceled.", cancellationToken);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or
            FormatException or InvalidOperationException or ArgumentException)
        {
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.TransportFailed);
        }
        throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.RedirectRejected);
    }

    private static HttpRequestMessage CreateMessage(Uri uri, ArtifactDownloadRequest request, bool api)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, uri)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        try
        {
            message.Headers.UserAgent.ParseAdd("Martlet-ArtifactAcquisition/1.0");
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            message.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
            message.Headers.ConnectionClose = true;
            if (api)
                message.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            if (request.Offset > 0)
            {
                message.Headers.Range = new RangeHeaderValue(request.Offset, null);
                if (request.EntityTag is { } etag)
                {
                    if (!IsSafeEntityTag(etag))
                        throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.ResumeStateMismatch);
                    message.Headers.IfRange = new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(etag));
                }
                else if (request.LastModifiedUtc is { } modified)
                    message.Headers.IfRange = new RangeConditionHeaderValue(modified);
                else
                    throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.ResumeStateMismatch);
            }
            return message;
        }
        catch
        {
            message.Dispose();
            throw;
        }
    }

    private static bool IsApiUri(Uri uri)
    {
        if (!HasExactOrigin(uri, ApiOrigin) || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return false;
        var parts = uri.AbsolutePath.Split('/');
        return parts.Length == 7 && parts[1] == "repos" &&
            IsRepositoryPart(parts[2]) && IsRepositoryPart(parts[3]) &&
            parts[4] == "releases" && parts[5] == "assets" &&
            parts[6].Length is > 0 and <= 20 && parts[6][0] != '0' &&
            parts[6].All(char.IsAsciiDigit);
    }

    private static bool IsRepositoryPart(string value) =>
        value.Length is > 0 and <= 100 && value is not "." and not ".." &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private static bool HasExactOrigin(Uri uri, string origin)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 ||
            uri.UserInfo.Length != 0 || uri.HostNameType != UriHostNameType.Dns ||
            uri.Fragment.Length != 0)
            return false;
        var raw = uri.OriginalString;
        var slash = raw.IndexOf('/', "https://".Length);
        if (slash < 0)
            return false;
        var authority = raw[..slash];
        return authority == origin || authority == origin + ":443";
    }

    private static Uri ReadRedirect(HttpResponseMessage response)
    {
        if (!response.Headers.NonValidated.TryGetValues("Location", out var values) ||
            values.Count != 1)
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.RedirectRejected);
        var raw = values.Single();
        if (raw.Length is 0 or > 8192 || raw.Any(c => c <= ' ' || c >= '\x7f' || c == '\\') ||
            !Uri.TryCreate(raw, UriKind.Absolute, out var parsed) ||
            !HasExactOrigin(parsed, AssetOrigin) || parsed.Fragment.Length != 0)
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.RedirectRejected);
        var query = raw.IndexOf('?');
        var path = raw[(raw.IndexOf('/', "https://".Length))..(query < 0 ? raw.Length : query)];
        if (path.Contains('%') || path.Contains("//", StringComparison.Ordinal) ||
            path.Split('/').Any(segment => segment is "." or ".."))
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.RedirectRejected);
        // Do not normalize/rebuild a signed query. Validation above applies to the raw authority/path.
        return new Uri(raw, new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
    }

    private static void CheckHeaderBudget(HttpResponseMessage response)
    {
        long size = 0;
        foreach (var header in response.Headers.NonValidated.Concat(response.Content.Headers.NonValidated))
        {
            foreach (var value in header.Value)
                size += header.Key.Length + value.Length + 4L;
            if (size > 16 * 1024)
                throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.ResponseInvalid);
        }
    }

    private static (string? EntityTag, DateTimeOffset? Modified) ReadValidators(HttpResponseMessage response)
    {
        string? etag = null;
        if (response.Headers.NonValidated.TryGetValues("ETag", out var tags))
        {
            if (tags.Count != 1 || !IsSafeEntityTag(tags.Single()))
                throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.ResponseInvalid);
            etag = tags.Single();
        }
        DateTimeOffset? modified = null;
        if (response.Content.Headers.NonValidated.TryGetValues("Last-Modified", out var dates))
        {
            if (dates.Count != 1 ||
                !DateTimeOffset.TryParseExact(dates.Single(), "r",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var value))
                throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.ResponseInvalid);
            modified = value;
        }
        return (etag, modified);
    }

    private static bool IsSafeEntityTag(string value) =>
        value.Length is >= 3 and <= 256 && value[0] == '"' && value[^1] == '"' &&
        value.AsSpan(1, value.Length - 2).IndexOfAnyExcept(
            "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_.") < 0;

    private static void SanitizeResponse(
        HttpResponseMessage response, string? etag, DateTimeOffset? modified)
    {
        var headers = response.Content.Headers;
        if (response.Headers.NonValidated.Contains("Transfer-Encoding") ||
            response.TrailingHeaders.Any() ||
            !headers.NonValidated.TryGetValues("Content-Length", out var lengths) ||
            lengths.Count != 1 ||
            !long.TryParse(lengths.Single(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var length) || length < 0)
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.ResponseInvalid);
        if (headers.NonValidated.TryGetValues("Content-Encoding", out var encodings) &&
            (encodings.Count != 1 || encodings.Single() != "identity"))
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.ResponseInvalid);
        ContentRangeHeaderValue? range = null;
        if (headers.NonValidated.TryGetValues("Content-Range", out var ranges) &&
            (ranges.Count != 1 || !ContentRangeHeaderValue.TryParse(ranges.Single(), out range) ||
                range.Unit != "bytes"))
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.ResponseInvalid);
        var byteRanges = response.Headers.NonValidated.TryGetValues("Accept-Ranges", out var advertised) &&
            advertised.Count == 1 && advertised.Single() == "bytes";
        response.RequestMessage = null;
        response.ReasonPhrase = null;
        response.Headers.Clear();
        if (etag is not null)
            response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        if (byteRanges)
            response.Headers.AcceptRanges.Add("bytes");
        headers.Clear();
        headers.ContentLength = length;
        headers.ContentRange = range;
        headers.LastModified = modified;
        response.Content = new ArtifactDownloadContent(response.Content);
    }

    public void Dispose() => handler.Dispose();

    // .NET 10 omits SocketsHttpHandler's DiagnosticsHandler when its propagator is null.
    // HttpClient AND HttpMessageInvoker add URI-bearing EventSource request events, so neither
    // may wrap the signed request. Forward directly through the protected handler API instead.
    private sealed class DirectHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        internal Task<HttpResponseMessage> SendDirectAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            base.SendAsync(request, cancellationToken);
    }
}
