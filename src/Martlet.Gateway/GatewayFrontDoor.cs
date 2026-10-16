using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Martlet.Core.Access;
using Martlet.Core.Logs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Martlet.Gateway;

/// <summary>
/// The front door (docs/OPENAI_ENDPOINT.md): a host's public listener on its own port, for a reverse proxy, Tailscale Funnel
/// or a tunnel to send people to. It has its own short list of routes and never reaches the gateway's private ones:
/// <list type="bullet">
/// <item><c>/v1/...</c>: the OpenAI-compatible API, with an API key only (never a paired computer's signed request).</item>
/// <item><c>/</c>: Martlet's web page. Without a session it says only that this is a private Martlet and that it is online;
/// signed in with a key it shows the models that key may use, how to connect an app, and the key's own use.</item>
/// <item><c>/portal/sign-in</c>, <c>/portal/sign-out</c> (POST forms) and <c>/health/live</c>.</item>
/// </list>
/// Everything else answers 404, so nothing of Martlet's private API is visible. Nothing here locks out an address: everyone
/// comes through one proxy, and keys can't be guessed. The page has no scripts; its sessions live in memory only.
/// </summary>
internal sealed partial class GatewayHttpApplication
{
    internal const string FrontDoorSignInPath = "/portal/sign-in";
    internal const string FrontDoorSignOutPath = "/portal/sign-out";
    private const int MaximumFormBytes = 4_096;
    private readonly ConcurrentDictionary<string, long> frontDoorRequests = new(StringComparer.Ordinal);

    /// <summary>The front door's web sessions (signed in with an API key).</summary>
    internal GatewayFrontDoorSessions FrontDoorSessions { get; } = new();

    /// <summary>Requests that went through this front door for each API key since the gateway started (key ID → count).</summary>
    internal long FrontDoorRequestsOf(string keyId) => frontDoorRequests.GetValueOrDefault(keyId);

    /// <summary>Handles every request on the front door's port (<see cref="GatewayExposure.FrontDoorPort"/>).</summary>
    internal async Task InvokeFrontDoorAsync(HttpContext context)
    {
        var traceId = TraceId();
        try
        {
            GatewayRules.Require(context.Request.IsHttps, "binding.unsafe");
            var rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
            GatewayRules.Require(rawTarget is { Length: > 0 and <= 256 } && rawTarget[0] == '/', "request.invalid");
            var path = rawTarget!.Split('?', 2)[0];
            FrontDoorHeaders(context);
            if (OpenAiEndpoint.IsPath(path))
            {
                if (!GatewayApiKeyStore.IsBearer(context.Request))
                {
                    Guard.FrontDoor(context, "failure", "missing_api_key");
                    await WriteOpenAiFailureAsync(context, traceId, 401,
                        "Send your Martlet API key in the Authorization header: the word Bearer, a space, then the key. " +
                        "The person who runs this Martlet gives you one.",
                        OpenAiEndpoint.FrontDoorMissingKey, null, null).ConfigureAwait(false);
                    return;
                }
                await InvokeOpenAiAsync(context, rawTarget, traceId).ConfigureAwait(false);
                if (context.Request.Method == HttpMethods.Post && context.Response.StatusCode == StatusCodes.Status200OK &&
                    ApiKeyToken.TryParse(BearerToken(context.Request), out var id, out var secret))
                {
                    CryptographicOperations.ZeroMemory(secret);
                    frontDoorRequests.AddOrUpdate(id, 1, static (_, count) => count + 1);
                }
                return;
            }
            if (path == "/health/live" && context.Request.Method == HttpMethods.Get)
            {
                EnsureEmptyRequest(context.Request);
                await WriteJsonAsync(context, 200, new LivenessDocument { Status = "live" }).ConfigureAwait(false);
                return;
            }
            if (path == "/" && context.Request.Method is "GET" or "HEAD")
            {
                await WriteFrontDoorPageAsync(context, null).ConfigureAwait(false);
                return;
            }
            if (path == FrontDoorSignInPath && context.Request.Method == HttpMethods.Post)
            {
                await FrontDoorSignInAsync(context).ConfigureAwait(false);
                return;
            }
            if (path == FrontDoorSignOutPath && context.Request.Method == HttpMethods.Post)
            {
                await FrontDoorSignOutAsync(context).ConfigureAwait(false);
                return;
            }
            await WriteFrontDoorTextAsync(context, StatusCodes.Status404NotFound, "Not found.").ConfigureAwait(false);
        }
        catch (GatewayProtocolException error)
        {
            Guard.FrontDoor(context, "failure", error.Failure.Code);
            if (!context.Response.HasStarted)
                await WriteFrontDoorTextAsync(context, error.Failure.HttpStatus is 413 ? 413 : 400, "This request can't be served.").ConfigureAwait(false);
            else context.Abort();
        }
        catch (BadHttpRequestException)
        {
            if (!context.Response.HasStarted) await WriteFrontDoorTextAsync(context, 400, "This request can't be served.").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            context.Abort();
        }
        catch (Exception)
        {
            if (!context.Response.HasStarted) await WriteFrontDoorTextAsync(context, 500, "Something went wrong here.").ConfigureAwait(false);
            else context.Abort();
        }
    }


    private static string? BearerToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        return header.StartsWith(GatewayApiKeyStore.Scheme + " ", StringComparison.OrdinalIgnoreCase)
            ? header[(GatewayApiKeyStore.Scheme.Length + 1)..].Trim() : null;
    }

    private static void FrontDoorHeaders(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), interest-cohort=()";
        headers.CacheControl = "no-store";
    }

    private static async ValueTask WriteFrontDoorTextAsync(HttpContext context, int status, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text + "\n");
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.ContentLength = bytes.Length;
        if (context.Request.Method != HttpMethods.Head)
            await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }

    // ---------- sign-in ----------

    private async ValueTask FrontDoorSignInAsync(HttpContext context)
    {
        var form = await ReadFrontDoorFormAsync(context).ConfigureAwait(false);
        if (form is null || !SameOrigin(context.Request) ||
            !FixedEquals(form.GetValueOrDefault("form"), context.Request.Cookies[GatewayFrontDoorSessions.FormCookie]))
        {
            Guard.FrontDoor(context, "refused", "form.invalid");
            await WriteFrontDoorPageAsync(context, "That form expired. Try again.").ConfigureAwait(false);
            return;
        }
        var token = form.GetValueOrDefault("key")?.Trim();
        GatewayPrincipal principal;
        try { principal = ApiKeys.AuthenticateOpenAi(token ?? ""); }
        catch (GatewayProtocolException error)
        {
            Guard.FrontDoor(context, "failure", error.Failure.Code);
            await WriteFrontDoorPageAsync(context, error.Failure.Code switch
            {
                "key.revoked" => "That key was revoked. Ask the person who runs this Martlet for a new one.",
                "key.expired" => "That key expired. Ask the person who runs this Martlet for a new one.",
                "key.scope" => "That key isn't for language models. Ask the person who runs this Martlet for a key that is.",
                _ => "That key doesn't work here. Check that you pasted all of it (it starts with martlet_)."
            }).ConfigureAwait(false);
            return;
        }
        var session = FrontDoorSessions.Create(principal.Key!, token!, clock.GetUtcNow());
        Guard.FrontDoor(context, "success", "signed-in", principal.Key!.Id);
        Logs.Own(LogLevels.Info, $"Front door: {principal.Caller} signed in to the web page.", "front-door.signin");
        context.Response.Cookies.Append(GatewayFrontDoorSessions.SessionCookie, session, SessionCookie(GatewayFrontDoorSessions.Lifetime));
        Redirect(context);
    }

    private async ValueTask FrontDoorSignOutAsync(HttpContext context)
    {
        var form = await ReadFrontDoorFormAsync(context).ConfigureAwait(false);
        var id = context.Request.Cookies[GatewayFrontDoorSessions.SessionCookie];
        if (form is not null && SameOrigin(context.Request) && FrontDoorSessions.Find(id, ApiKeys.Current, clock.GetUtcNow()) is { } session &&
            FixedEquals(form.GetValueOrDefault("form"), session.FormToken))
            FrontDoorSessions.Remove(id!);
        context.Response.Cookies.Delete(GatewayFrontDoorSessions.SessionCookie, SessionCookie(TimeSpan.Zero));
        Redirect(context);
    }

    private static void Redirect(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status303SeeOther;
        context.Response.Headers.Location = "/";
        context.Response.ContentLength = 0;
    }

    private static CookieOptions SessionCookie(TimeSpan lifetime) => new()
    {
        Path = "/", Secure = true, HttpOnly = true, SameSite = SameSiteMode.Strict, IsEssential = true,
        MaxAge = lifetime > TimeSpan.Zero ? lifetime : null
    };

    /// <summary>A browser's form post must come from this same address (the Origin header, when it sends one).</summary>
    private static bool SameOrigin(HttpRequest request) =>
        !request.Headers.TryGetValue("Origin", out var origin) ||
        string.Equals(origin.ToString(), "https://" + request.Host.Value, StringComparison.OrdinalIgnoreCase);

    private static bool FixedEquals(string? a, string? b) =>
        a is { Length: > 0 } && b is { Length: > 0 } &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    /// <summary>An application/x-www-form-urlencoded body of at most 4 KB; null when it isn't one.</summary>
    private static async ValueTask<Dictionary<string, string>?> ReadFrontDoorFormAsync(HttpContext context)
    {
        var request = context.Request;
        if (request.ContentType is not { } type || !type.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) ||
            request.ContentLength > MaximumFormBytes)
            return null;
        using var output = new MemoryStream();
        var buffer = new byte[1024];
        int read;
        while ((read = await request.Body.ReadAsync(buffer, context.RequestAborted).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > MaximumFormBytes) return null;
            output.Write(buffer, 0, read);
        }
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in Encoding.UTF8.GetString(output.ToArray()).Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var name = WebUtility.UrlDecode(parts[0]);
            if (name is not ("form" or "key") || fields.ContainsKey(name)) return null;
            fields[name] = WebUtility.UrlDecode(parts.Length > 1 ? parts[1] : "");
        }
        return fields;
    }
}

/// <summary>The front door's web sessions, in memory only: a random ID in an HttpOnly, Secure, SameSite=Strict cookie, for one
/// API key. A session ends when its key is revoked, expires or loses the openai scope, after 8 hours unused, after 7 days, on
/// sign-out, or when the gateway restarts.</summary>
internal sealed class GatewayFrontDoorSessions
{
    internal const string SessionCookie = "__Host-martlet";
    internal const string FormCookie = "__Host-martlet-form";
    internal static readonly TimeSpan Idle = TimeSpan.FromHours(8);
    internal static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);
    internal const int Maximum = 512;
    private readonly object gate = new();
    private readonly Dictionary<string, GatewayFrontDoorSession> sessions = new(StringComparer.Ordinal);

    internal int Count { get { lock (gate) return sessions.Count; } }

    internal string Create(ApiKey key, string token, DateTimeOffset now)
    {
        var id = NewToken();
        lock (gate)
        {
            foreach (var old in sessions.Where(s => !s.Value.Live(now)).Select(s => s.Key).ToArray()) sessions.Remove(old);
            if (sessions.Count >= Maximum)
                sessions.Remove(sessions.OrderBy(s => s.Value.LastSeen).First().Key);
            sessions[id] = new(key.Id, key.Verifier ?? "", token, NewToken(), now) { LastSeen = now };
        }
        return id;
    }

    /// <summary>The live session <paramref name="id"/> names, while its key is still live and allows openai; it is touched.</summary>
    internal GatewayFrontDoorSession? Find(string? id, ApiKeyList keys, DateTimeOffset now)
    {
        if (id is not { Length: 43 }) return null;
        lock (gate)
        {
            if (!sessions.TryGetValue(id, out var session)) return null;
            if (!session.Live(now) || keys.Find(session.KeyId) is not { } key || !key.Live(now) || key.Verifier != session.Verifier ||
                !key.Allows(ApiKeyScopes.OpenAi))
            {
                sessions.Remove(id);
                return null;
            }
            session.LastSeen = now;
            return session;
        }
    }

    internal void Remove(string id)
    {
        lock (gate) sessions.Remove(id);
    }

    internal static string NewToken() => Base64Url.Encode(RandomNumberGenerator.GetBytes(32));
}

/// <summary>One signed-in browser: the key it signed in with (its ID and verifier; the token, kept in memory only, asks the
/// network's other hosts for their models), its form token and when it was made and last used.</summary>
internal sealed record GatewayFrontDoorSession(string KeyId, string Verifier, string Token, string FormToken, DateTimeOffset Created)
{
    internal DateTimeOffset LastSeen { get; set; }

    internal bool Live(DateTimeOffset now) => now - LastSeen < GatewayFrontDoorSessions.Idle && now - Created < GatewayFrontDoorSessions.Lifetime;
}
