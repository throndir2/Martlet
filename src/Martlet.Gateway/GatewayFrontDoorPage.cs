using System.Net;
using System.Security.Cryptography;
using System.Text;
using Martlet.Core.Access;
using Microsoft.AspNetCore.Http;

namespace Martlet.Gateway;

/// <summary>The front door's web page (<see cref="InvokeFrontDoorAsync"/>): server-made HTML with no scripts. Every value is
/// HTML-encoded, the only style is the fixed sheet below (allowed by its hash), and forms carry a token tied to the browser.</summary>
internal sealed partial class GatewayHttpApplication
{
    private const string FrontDoorStyle = """
        :root{color-scheme:light dark;--bg:#f6f4fb;--card:#fff;--ink:#1f1b2e;--muted:#5f5a70;--accent:#7a4fd6;--line:#e3dff0;--good:#1f8a4c}
        @media (prefers-color-scheme:dark){:root{--bg:#15131c;--card:#1f1c29;--ink:#eeeaf8;--muted:#a9a3bd;--accent:#b192ff;--line:#2f2b3d;--good:#4cc287}}
        *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--ink);font:16px/1.5 system-ui,-apple-system,"Segoe UI",sans-serif}
        main{max-width:760px;margin:0 auto;padding:40px 20px}h1{font-size:28px;margin:0 0 4px}h2{font-size:18px;margin:0 0 10px}
        .muted{color:var(--muted)}.card{background:var(--card);border:1px solid var(--line);border-radius:16px;padding:20px 22px;margin:18px 0}
        .status{display:inline-block;padding:2px 10px;border-radius:99px;border:1px solid var(--good);color:var(--good);font-size:14px}
        label{display:block;font-weight:600;margin:0 0 6px}input[type=password]{width:100%;padding:10px 12px;border-radius:10px;border:1px solid var(--line);background:var(--bg);color:var(--ink);font:inherit}
        button{margin-top:12px;padding:9px 18px;border-radius:10px;border:0;background:var(--accent);color:#fff;font:inherit;font-weight:600;cursor:pointer}
        button.quiet{background:transparent;color:var(--accent);border:1px solid var(--line);margin:0}
        pre{background:var(--bg);border:1px solid var(--line);border-radius:10px;padding:12px;overflow:auto;font-size:13px;white-space:pre-wrap;word-break:break-all}
        code{font-family:ui-monospace,Consolas,monospace}ul{padding-left:20px;margin:6px 0}.message{border-color:var(--accent)}
        .top{display:flex;justify-content:space-between;align-items:center;gap:12px;flex-wrap:wrap}footer{margin-top:28px;font-size:13px}a{color:var(--accent)}
        """;

    private static readonly string FrontDoorPolicy =
        "default-src 'none'; style-src 'sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(FrontDoorStyle))) + "'; " +
        "img-src 'self' data:; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";

    private async ValueTask WriteFrontDoorPageAsync(HttpContext context, string? message)
    {
        var now = clock.GetUtcNow();
        var formToken = context.Request.Cookies[GatewayFrontDoorSessions.FormCookie];
        if (formToken is not { Length: 43 })
        {
            formToken = GatewayFrontDoorSessions.NewToken();
            context.Response.Cookies.Append(GatewayFrontDoorSessions.FormCookie, formToken, new CookieOptions
            {
                Path = "/", Secure = true, HttpOnly = true, SameSite = SameSiteMode.Strict, IsEssential = true
            });
        }
        var session = FrontDoorSessions.Find(context.Request.Cookies[GatewayFrontDoorSessions.SessionCookie], ApiKeys.Current, now);
        var html = new StringBuilder();
        html.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">")
            .Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta name=\"robots\" content=\"noindex\">")
            .Append("<title>Martlet</title><style>").Append(FrontDoorStyle).Append("</style></head><body><main>");
        if (session is not null && ApiKeys.Current.Find(session.KeyId) is { } key)
            await SignedInPageAsync(html, context, session, key, message, now).ConfigureAwait(false);
        else
            PublicPage(html, formToken, message);
        html.Append("<footer class=\"muted\">Runs on <a href=\"https://github.com/throndir2/Martlet\" rel=\"noreferrer\">Martlet</a>, ")
            .Append("a desktop companion that its owner shares their computers' language models from.</footer></main></body></html>");
        var bytes = Encoding.UTF8.GetBytes(html.ToString());
        context.Response.StatusCode = message is null ? 200 : 400;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.ContentSecurityPolicy = FrontDoorPolicy;
        context.Response.ContentLength = bytes.Length;
        if (context.Request.Method != HttpMethods.Head)
            await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }

    private static string E(string? text) => WebUtility.HtmlEncode(text ?? "");

    private static void Message(StringBuilder html, string? message)
    {
        if (message is not null) html.Append("<div class=\"card message\" role=\"alert\">").Append(E(message)).Append("</div>");
    }

    // Anyone: that this is a private Martlet, that it is online, and the sign-in form. No models, hosts, addresses or counts.
    private static void PublicPage(StringBuilder html, string formToken, string? message)
    {
        html.Append("<h1>Martlet</h1><p class=\"muted\">A private Martlet. Its owner shares the language models of their computers ")
            .Append("with people they give a key to.</p><p><span class=\"status\">Online</span></p>");
        Message(html, message);
        html.Append("<div class=\"card\"><h2>Sign in with your key</h2>")
            .Append("<form method=\"post\" action=\"").Append(FrontDoorSignInPath).Append("\" autocomplete=\"off\">")
            .Append("<input type=\"hidden\" name=\"form\" value=\"").Append(E(formToken)).Append("\">")
            .Append("<label for=\"key\">Your key (it starts with martlet_)</label>")
            .Append("<input type=\"password\" id=\"key\" name=\"key\" required maxlength=\"128\" spellcheck=\"false\">")
            .Append("<button type=\"submit\">Sign in</button></form>")
            .Append("<p class=\"muted\">Signed in, you see the models you may use and how to connect your app. ")
            .Append("You stay signed in for 8 hours of use. Martlet keeps your key only in this computer's memory.</p></div>")
            .Append("<div class=\"card\"><h2>No key?</h2><p class=\"muted\">Ask the person who runs this Martlet. ")
            .Append("They make one on their Share page with <em>Give someone access</em>.</p></div>");
    }

    // Signed in: the models the key may use (this host's and the network's), how to connect an app, and the key's own use.
    private async ValueTask SignedInPageAsync(StringBuilder html, HttpContext context, GatewayFrontDoorSession session, ApiKey key,
        string? message, DateTimeOffset now)
    {
        var models = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var model in inference.OpenAiRelays().Select(r => r.Relay.OpenAiModel).Distinct(StringComparer.Ordinal))
            models[model] = 1;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            foreach (var (_, list) in await PeerModelsAsync("Bearer " + session.Token, session.KeyId, limit.Token).ConfigureAwait(false))
                foreach (var model in list) models[model] = models.GetValueOrDefault(model) + 1;
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested) { }
        var baseUrl = "https://" + context.Request.Host.Value + OpenAiEndpoint.BasePath;
        var first = models.Keys.FirstOrDefault() ?? "<model>";
        html.Append("<div class=\"top\"><div><h1>Martlet</h1><p class=\"muted\">Signed in with the key <strong>")
            .Append(E(key.Name)).Append("</strong>.</p></div>")
            .Append("<form method=\"post\" action=\"").Append(FrontDoorSignOutPath).Append("\">")
            .Append("<input type=\"hidden\" name=\"form\" value=\"").Append(E(session.FormToken)).Append("\">")
            .Append("<button class=\"quiet\" type=\"submit\">Sign out</button></form></div>");
        Message(html, message);

        html.Append("<div class=\"card\"><h2>Models you can use</h2>");
        if (models.Count == 0)
            html.Append("<p class=\"muted\">No model is on right now. The computers that run them may be off; try again later.</p>");
        else
        {
            html.Append("<ul>");
            foreach (var (model, computers) in models)
                html.Append("<li><code>").Append(E(model)).Append("</code> <span class=\"muted\">(on ").Append(computers)
                    .Append(computers == 1 ? " computer" : " computers").Append(")</span></li>");
            html.Append("</ul>");
        }
        html.Append("</div>");

        html.Append("<div class=\"card\"><h2>Connect your app</h2><p>Any app that works with the OpenAI API works here: ")
            .Append("put in the base URL, your key and a model.</p><p><strong>Base URL:</strong> <code>").Append(E(baseUrl))
            .Append("</code></p><pre><code>").Append(E(OpenAiEndpoint.CurlExample(baseUrl, first))).Append("</code></pre>")
            .Append("<pre><code>").Append(E(OpenAiEndpoint.PythonExample(baseUrl, first))).Append("</code></pre></div>");

        var used = ApiKeys.Used.TryGetValue(key.Id, out var at) ? $"last used {at:yyyy-MM-dd HH:mm} UTC" : "not used on this computer since it started";
        var expires = key.ExpiresAt is { } end ? $"It expires {end:yyyy-MM-dd}." : "It doesn't expire.";
        html.Append("<div class=\"card\"><h2>Your key</h2><ul><li>").Append(E(OpenAiEndpoint.Priority(key))).Append(".</li><li>")
            .Append(E(expires)).Append("</li><li>").Append(FrontDoorRequestsOf(key.Id)).Append(" requests through this address since it started; ")
            .Append(E(used)).Append(".</li></ul><p class=\"muted\">The owner's own work comes first. When a model is busy, your app ")
            .Append("gets 429 (try again in a moment) or 503 (the owner needed it). Keep your key private; the owner can revoke it at any time.</p></div>");
    }
}
