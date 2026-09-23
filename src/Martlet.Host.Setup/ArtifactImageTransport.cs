using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Martlet.HostArtifacts;

namespace Martlet.Host.Setup;

internal sealed class ArtifactImageRequestBudget(ArtifactImageAcquisitionPlan plan)
{
    internal const int TokenBodyLimit = 65_536;
    private int requests = plan.MaximumRequests;
    private int tokenExchanges = plan.MaximumTokenExchanges;
    private int redirects = plan.MaximumRedirects;
    private long contentBytes = plan.MaximumContentResponseBytes;
    private long controlBytes = plan.MaximumControlResponseBytes;

    internal void BeforeRequest()
    {
        if (--requests < 0)
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.ImageBudgetExceeded);
    }

    internal void BeforeToken()
    {
        if (--tokenExchanges < 0)
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.ImageBudgetExceeded);
    }

    internal void BeforeRedirect()
    {
        if (--redirects < 0)
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.ImageBudgetExceeded);
    }

    internal int ContentWindow(int requested) => Window(requested, contentBytes);
    internal int ControlWindow(int requested) => Window(requested, controlBytes);
    internal void ReadContent(int count) => Consume(ref contentBytes, count);
    internal void ReadControl(int count) => Consume(ref controlBytes, count);

    private static int Window(int requested, long remaining)
    {
        if (remaining <= 0) throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.ImageBudgetExceeded);
        return (int)Math.Min(requested, remaining);
    }

    private static void Consume(ref long remaining, int count)
    {
        if (count < 0 || count > remaining)
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.ImageBudgetExceeded);
        remaining -= count;
    }
}

internal sealed class ArtifactImageDownloadResponse(HttpResponseMessage response, string? mediaType) : IDisposable
{
    internal HttpResponseMessage Response { get; } = response;
    internal string? MediaType { get; } = mediaType;
    public void Dispose() => Response.Dispose();
}

internal sealed class HttpsArtifactImageTransport : IDisposable
{
    internal const string OciIndex = "application/vnd.oci.image.index.v1+json";
    internal const string OciManifest = "application/vnd.oci.image.manifest.v1+json";
    internal const string DockerIndex = "application/vnd.docker.distribution.manifest.list.v2+json";
    internal const string DockerManifest = "application/vnd.docker.distribution.manifest.v2+json";
    private static readonly HttpRequestOptionsKey<ArtifactImageAcquisitionSource> SourceKey = new("Martlet.ImageSource");
    private readonly HttpsArtifactDownloadTransport.DirectHandler handler;
    private readonly TimeProvider clock;

    internal HttpsArtifactImageTransport() : this(CreateProductionHandler()) { }

    internal HttpsArtifactImageTransport(HttpMessageHandler handler, TimeProvider? clock = null)
    {
        this.handler = new HttpsArtifactDownloadTransport.DirectHandler(handler);
        this.clock = clock ?? TimeProvider.System;
    }

    internal static SocketsHttpHandler CreateProductionHandler()
    {
        var result = HttpsArtifactDownloadTransport.CreateProductionHandler();
        result.ConnectCallback = (context, token) =>
        {
            if (!context.InitialRequestMessage.Options.TryGetValue(SourceKey, out var source))
                throw Failure(ArtifactAcquisitionFailure.ProviderUnsupported);
            return ConnectAsync(source, context.DnsEndPoint, token,
                static (host, cancellation) => Dns.GetHostAddressesAsync(host, cancellation),
                ArtifactDownloadConnectionPolicy.ConnectAddressAsync);
        };
        return result;
    }

    internal static ValueTask<Stream> ConnectAsync(ArtifactImageAcquisitionSource source,
        System.Net.DnsEndPoint endpoint, CancellationToken cancellationToken,
        Func<string, CancellationToken, Task<IPAddress[]>> resolve,
        Func<IPAddress, int, CancellationToken, ValueTask<Stream>> connect)
    {
        if (source.Provider == ArtifactImageAcquisitionProvider.Unsupported ||
            endpoint.Port != 443 ||
            !new[] { source.Registry, new Uri(source.TokenRealm!).Host, new Uri(source.CdnOrigin!).Host }
                .Contains(endpoint.Host, StringComparer.Ordinal))
            throw Failure(ArtifactAcquisitionFailure.RedirectRejected);
        return ArtifactDownloadConnectionPolicy.ConnectVettedEndpointAsync(
            endpoint, cancellationToken, resolve, connect);
    }

    internal async ValueTask<ArtifactImageDownloadResponse> SendAsync(ArtifactImageContent content,
        long offset, ArtifactImageRequestBudget budget,
        Func<CancellationToken, ValueTask> beforeRequest, CancellationToken cancellationToken)
    {
        var source = content.Source;
        if (source.Provider == ArtifactImageAcquisitionProvider.Unsupported)
            throw Failure(ArtifactAcquisitionFailure.ProviderUnsupported);
        if (content.ExpectedBytes is not { } bytes || offset < 0 || offset >= bytes)
            throw Failure(ArtifactAcquisitionFailure.InvalidPlan);
        var metadata = content.Kind is ArtifactImageContentKind.Index or ArtifactImageContentKind.Manifest;
        if (metadata && offset != 0)
            throw Failure(ArtifactAcquisitionFailure.ResumeStateMismatch);
        var uri = new Uri($"{source.RegistryOrigin}/v2/{source.Repository}/" +
            $"{(metadata ? "manifests" : "blobs")}/{content.Digest}");
        string? bearer = null;
        var authenticated = false;
        var redirected = false;
        try
        {
            while (true)
            {
                var response = await SendRequestAsync(uri, source, content.Kind, offset, bearer,
                    budget, beforeRequest, cancellationToken).ConfigureAwait(false);
                try
                {
                    if (response.StatusCode == HttpStatusCode.Unauthorized && !redirected && !authenticated)
                    {
                        ValidateChallenge(response, source);
                        response.Dispose();
                        bearer = await ReadTokenAsync(source, budget, beforeRequest, cancellationToken).ConfigureAwait(false);
                        authenticated = true;
                        continue;
                    }
                    if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                        throw Failure(ArtifactAcquisitionFailure.RegistryAuthenticationRejected);
                    if ((int)response.StatusCode is >= 300 and <= 399)
                    {
                        if (metadata || redirected ||
                            response.StatusCode is not (HttpStatusCode.Redirect or HttpStatusCode.TemporaryRedirect))
                            throw Failure(ArtifactAcquisitionFailure.RedirectRejected);
                        uri = ReadRedirect(response, source.CdnOrigin!);
                        budget.BeforeRedirect();
                        bearer = null;
                        redirected = true;
                        response.Dispose();
                        continue;
                    }
                    var mediaType = ValidateContentResponse(response, content, offset);
                    Sanitize(response);
                    return new ArtifactImageDownloadResponse(response, mediaType);
                }
                catch
                {
                    response.Dispose();
                    throw;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw new OperationCanceledException("Image transfer canceled.", cancellationToken);
        }
        catch (ArtifactAcquisitionException error)
        {
            throw Failure(error.Failure);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or FormatException or
            InvalidOperationException or ArgumentException or JsonException)
        {
            throw Failure(ArtifactAcquisitionFailure.TransportFailed);
        }
    }

    private async Task<HttpResponseMessage> SendRequestAsync(Uri uri, ArtifactImageAcquisitionSource source,
        ArtifactImageContentKind? kind, long offset, string? bearer, ArtifactImageRequestBudget budget,
        Func<CancellationToken, ValueTask> beforeRequest, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, uri)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        message.Options.Set(SourceKey, source);
        message.Headers.UserAgent.ParseAdd("Martlet-ImageAcquisition/1.0");
        message.Headers.AcceptEncoding.ParseAdd("identity");
        message.Headers.ConnectionClose = true;
        message.Headers.Accept.ParseAdd(kind switch
        {
            ArtifactImageContentKind.Index => $"{OciIndex}, {DockerIndex}",
            ArtifactImageContentKind.Manifest => $"{OciManifest}, {DockerManifest}",
            null => "application/json",
            _ => "application/octet-stream"
        });
        if (offset > 0) message.Headers.Range = new RangeHeaderValue(offset, null);
        if (bearer is not null)
        {
            if (!HttpsArtifactDownloadTransport.HasExactOrigin(uri, source.RegistryOrigin) ||
                !uri.AbsolutePath.StartsWith($"/v2/{source.Repository}/", StringComparison.Ordinal))
                throw Failure(ArtifactAcquisitionFailure.RegistryAuthenticationRejected);
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }
        await beforeRequest(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        budget.BeforeRequest();
        var response = await handler.SendDirectAsync(message, cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (response.RequestMessage?.RequestUri is { } effective && effective.OriginalString != uri.OriginalString)
                throw Failure(ArtifactAcquisitionFailure.RedirectRejected);
            HttpsArtifactDownloadTransport.CheckHeaderBudget(response);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
        finally
        {
            message.Headers.Authorization = null;
        }
    }

    internal static void ValidateChallenge(HttpResponseMessage response, ArtifactImageAcquisitionSource source)
    {
        var raw = One(response.Headers, "WWW-Authenticate", required: true)!;
        if (!AuthenticationHeaderValue.TryParse(raw, out var challenge) ||
            !challenge.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) || challenge.Parameter is null)
            throw Failure(ArtifactAcquisitionFailure.RegistryAuthenticationRejected);
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in challenge.Parameter.Split(','))
        {
            var equals = item.IndexOf('=');
            if (equals <= 0) throw Failure(ArtifactAcquisitionFailure.RegistryAuthenticationRejected);
            var name = item[..equals].Trim();
            var value = item[(equals + 1)..].Trim();
            if (value.Length < 2 || value[0] != '"' || value[^1] != '"' ||
                value[1..^1].Any(c => c <= ' ' || c >= '\x7f' || c is '\\' or '"') ||
                !parameters.TryAdd(name, value[1..^1]))
                throw Failure(ArtifactAcquisitionFailure.RegistryAuthenticationRejected);
        }
        if (parameters.Count != 3 ||
            !parameters.TryGetValue("realm", out var realm) || realm != source.TokenRealm ||
            !parameters.TryGetValue("service", out var service) || service != source.TokenService ||
            !parameters.TryGetValue("scope", out var scope) || scope != source.PullScope)
            throw Failure(ArtifactAcquisitionFailure.RegistryAuthenticationRejected);
    }

    private async ValueTask<string> ReadTokenAsync(ArtifactImageAcquisitionSource source,
        ArtifactImageRequestBudget budget, Func<CancellationToken, ValueTask> beforeRequest,
        CancellationToken cancellationToken)
    {
        budget.BeforeToken();
        var uri = new Uri(source.TokenRealm + "?service=" + Uri.EscapeDataString(source.TokenService!) +
            "&scope=" + Uri.EscapeDataString(source.PullScope));
        using var response = await SendRequestAsync(uri, source, null, 0, null,
            budget, beforeRequest, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK ||
            MediaType(response.Content.Headers) != "application/json")
            throw Failure(ArtifactAcquisitionFailure.RegistryAuthenticationRejected);
        ValidateFraming(response);
        if (response.Content.Headers.ContentLength > ArtifactImageRequestBudget.TokenBodyLimit)
            throw Failure(ArtifactAcquisitionFailure.RegistryAuthenticationRejected);
        Sanitize(response);
        var bytes = await ReadBoundedAsync(response.Content, ArtifactImageRequestBudget.TokenBodyLimit,
            beforeRequest, cancellationToken, budget.ControlWindow, budget.ReadControl).ConfigureAwait(false);
        try
        {
            if (response.TrailingHeaders.Any()) throw Failure(ArtifactAcquisitionFailure.RegistryAuthenticationRejected);
            using var json = ArtifactImageJson.Parse(bytes);
            var root = json.RootElement;
            ArtifactImageJson.Properties(root, ["token", "access_token", "expires_in", "issued_at"], []);
            var token = OptionalString(root, "token");
            var accessToken = OptionalString(root, "access_token");
            if (token is not null && accessToken is not null && token != accessToken)
                throw Failure(ArtifactAcquisitionFailure.RegistryAuthenticationRejected);
            token ??= accessToken;
            if (token is not { Length: > 0 and <= 8192 } ||
                token.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '.' or '_' or '~' or '+' or '/' or '=')) ||
                token.TrimEnd('=').Contains('='))
                throw Failure(ArtifactAcquisitionFailure.RegistryAuthenticationRejected);
            var lifetime = 60;
            if (root.TryGetProperty("expires_in", out var expires) &&
                (!expires.TryGetInt32(out lifetime) || lifetime is <= 0 or > 86_400))
                throw Failure(ArtifactAcquisitionFailure.RegistryAuthenticationRejected);
            var now = clock.GetUtcNow();
            var issued = now;
            if (OptionalString(root, "issued_at") is { } issuedText &&
                (!DateTimeOffset.TryParseExact(issuedText,
                    ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
                     "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"],
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out issued) || issued > now.AddMinutes(1)))
                throw Failure(ArtifactAcquisitionFailure.RegistryAuthenticationRejected);
            if (issued.AddSeconds(lifetime) <= now)
                throw Failure(ArtifactAcquisitionFailure.RegistryAuthenticationRejected);
            return token;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or
            ArgumentException or ArtifactAcquisitionException)
        {
            throw Failure(ArtifactAcquisitionFailure.RegistryAuthenticationRejected);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal static async ValueTask<byte[]> ReadBoundedAsync(HttpContent content, int maximum,
        Func<CancellationToken, ValueTask> checkCurrent, CancellationToken cancellationToken,
        Func<int, int>? readWindow = null, Action<int>? bytesRead = null)
    {
        using var result = new MemoryStream();
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[Math.Min(maximum + 1, 65_536)];
        while (true)
        {
            await checkCurrent(cancellationToken).ConfigureAwait(false);
            var requested = (int)Math.Min(buffer.Length, maximum - result.Length + 1);
            var count = await stream.ReadAsync(buffer.AsMemory(0, readWindow?.Invoke(requested) ?? requested),
                cancellationToken).ConfigureAwait(false);
            bytesRead?.Invoke(count);
            if (count == 0) return result.ToArray();
            if (result.Length + count > maximum) throw Failure(ArtifactAcquisitionFailure.SizeExceeded);
            result.Write(buffer, 0, count);
        }
    }

    private static string? ValidateContentResponse(HttpResponseMessage response, ArtifactImageContent content, long offset)
    {
        ValidateFraming(response);
        var expected = content.ExpectedBytes!.Value;
        if (offset > 0 && response.StatusCode == HttpStatusCode.OK)
            throw Failure(ArtifactAcquisitionFailure.RangeUnsupported);
        if (response.StatusCode != (offset == 0 ? HttpStatusCode.OK : HttpStatusCode.PartialContent))
            throw Failure(ArtifactAcquisitionFailure.ResponseInvalid);
        var rangeText = One(response.Content.Headers, "Content-Range");
        if (offset == 0 && rangeText is not null)
            throw Failure(ArtifactAcquisitionFailure.ResponseInvalid);
        if (offset > 0 && (!ContentRangeHeaderValue.TryParse(rangeText, out var range) ||
            range.Unit != "bytes" || range.From != offset || range.To != expected - 1 || range.Length != expected))
            throw Failure(ArtifactAcquisitionFailure.ResumeStateMismatch);
        var length = response.Content.Headers.ContentLength;
        if (length is not null && length != expected - offset ||
            length is null && (content.Kind == ArtifactImageContentKind.Layer || offset > 0))
            throw Failure(ArtifactAcquisitionFailure.ContentLengthMismatch);
        var digest = One(response.Headers, "Docker-Content-Digest");
        if (digest is not null && digest != content.Digest)
            throw Failure(ArtifactAcquisitionFailure.IntegrityMismatch);
        var mediaType = MediaType(response.Content.Headers);
        if (content.Kind == ArtifactImageContentKind.Index && mediaType is not (OciIndex or DockerIndex) ||
            content.Kind == ArtifactImageContentKind.Manifest && mediaType is not (OciManifest or DockerManifest))
            throw Failure(ArtifactAcquisitionFailure.ImageMetadataInvalid);
        return mediaType;
    }

    private static void ValidateFraming(HttpResponseMessage response)
    {
        var length = One(response.Content.Headers, "Content-Length");
        var transfer = One(response.Headers, "Transfer-Encoding");
        var encoding = One(response.Content.Headers, "Content-Encoding");
        if (encoding is not (null or "identity") ||
            transfer is not (null or "chunked") || transfer is not null && length is not null ||
            response.Headers.NonValidated.Contains("Trailer") || response.TrailingHeaders.Any() ||
            length is not null && (!long.TryParse(length, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 0))
            throw Failure(ArtifactAcquisitionFailure.ResponseInvalid);
    }

    internal static Uri ReadRedirect(HttpResponseMessage response, string origin)
    {
        var raw = One(response.Headers, "Location", required: true)!;
        if (raw.Length > 8192 || raw.Any(c => c <= ' ' || c >= '\x7f' || c == '\\') ||
            !Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            !HttpsArtifactDownloadTransport.HasExactOrigin(uri, origin))
            throw Failure(ArtifactAcquisitionFailure.RedirectRejected);
        var query = raw.IndexOf('?');
        var path = raw[raw.IndexOf('/', "https://".Length)..(query < 0 ? raw.Length : query)];
        if (path.Contains('%') || path.Contains("//", StringComparison.Ordinal) ||
            path.Split('/').Any(part => part is "." or ".."))
            throw Failure(ArtifactAcquisitionFailure.RedirectRejected);
        return new Uri(raw, new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
    }

    private static string? One(HttpHeaders headers, string name, bool required = false)
    {
        if (!headers.NonValidated.TryGetValues(name, out var values))
        {
            if (required) throw Failure(ArtifactAcquisitionFailure.ResponseInvalid);
            return null;
        }
        if (values.Count != 1) throw Failure(ArtifactAcquisitionFailure.ResponseInvalid);
        return values.Single();
    }

    private static string? MediaType(HttpContentHeaders headers)
    {
        var value = One(headers, "Content-Type");
        if (value is null) return null;
        if (!MediaTypeHeaderValue.TryParse(value, out var type))
            throw Failure(ArtifactAcquisitionFailure.ResponseInvalid);
        return type.MediaType;
    }

    private static string? OptionalString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String)
            throw Failure(ArtifactAcquisitionFailure.RegistryAuthenticationRejected);
        return value.GetString();
    }

    private static void Sanitize(HttpResponseMessage response)
    {
        var length = response.Content.Headers.ContentLength;
        var range = response.Content.Headers.ContentRange;
        var media = MediaType(response.Content.Headers);
        response.RequestMessage = null;
        response.ReasonPhrase = null;
        response.Headers.Clear();
        response.Content.Headers.Clear();
        response.Content.Headers.ContentLength = length;
        response.Content.Headers.ContentRange = range;
        response.Content.Headers.ContentType = media is OciIndex or DockerIndex or OciManifest or DockerManifest or
            "application/json" or "application/octet-stream" ? new MediaTypeHeaderValue(media) : null;
        response.Content = new ArtifactDownloadContent(response.Content);
    }

    private static ArtifactAcquisitionException Failure(ArtifactAcquisitionFailure failure) => new(failure);
    public void Dispose() => handler.Dispose();
}

internal static class ArtifactImageJson
{
    internal static JsonDocument Parse(ReadOnlyMemory<byte> bytes, int maximumDepth = 32)
    {
        try
        {
            _ = new UTF8Encoding(false, true).GetCharCount(bytes.Span);
            var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = maximumDepth });
            try { Validate(document.RootElement); return document; }
            catch { document.Dispose(); throw; }
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or InvalidOperationException)
        {
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.ImageMetadataInvalid);
        }
    }

    private static void Validate(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.ImageMetadataInvalid);
                Validate(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var value in element.EnumerateArray()) Validate(value);
        else if (element.ValueKind == JsonValueKind.String)
            _ = element.GetString();
    }

    internal static void Properties(JsonElement element, string[] allowed, string[] required)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            element.EnumerateObject().Any(property => !allowed.Contains(property.Name, StringComparer.Ordinal)) ||
            required.Any(name => !element.TryGetProperty(name, out _)))
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.ImageMetadataInvalid);
    }
}
