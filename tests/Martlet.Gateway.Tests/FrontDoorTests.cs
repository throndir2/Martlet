using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Martlet.Core.Access;
using Martlet.Gateway.Ollama;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Martlet.Gateway.Tests;

// NOT AI: a controlled HTTP fixture stands in for the host's loopback Ollama server. The tests check that the front door
// (a second, public port) serves only the web page and the OpenAI-compatible API, and that its sign-in is safe.
public sealed partial class FrontDoorTests
{
    /// <summary>A gateway whose front door is open on its own loopback port, one model (gemma4:e4b) on a fixture Ollama, an
    /// openai key and a voice-only key. <see cref="Send"/> talks to the front door like a browser or an app would.</summary>
    private sealed class Lab : IAsyncDisposable
    {
        private WebApplication ollama = null!;
        private OllamaRelayWorker worker = null!;
        private HttpClient client = null!;
        internal GatewayTestHost Host { get; private set; } = null!;
        internal int Port { get; private set; }
        internal string Origin => $"https://127.0.0.1:{Port}";
        internal IssuedApiKey Key { get; private set; } = null!;
        internal IssuedApiKey VoiceOnly { get; private set; } = null!;
        internal ApiKeyList Keys { get; private set; } = ApiKeyList.Empty;

        internal static async Task<Lab> StartAsync()
        {
            var lab = new Lab();
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            lab.ollama = builder.Build();
            lab.ollama.MapPost("/api/chat", async context =>
            {
                using var document = await JsonDocument.ParseAsync(context.Request.Body);
                await context.Response.WriteAsJsonAsync(new
                {
                    model = document.RootElement.GetProperty("model").GetString(),
                    message = new { role = "assistant", content = "Hello through the front door." },
                    done = true, done_reason = "stop", prompt_eval_count = 5, eval_count = 4
                });
            });
            await lab.ollama.StartAsync();
            lab.worker = new OllamaRelayWorker(new Uri(lab.ollama.Urls.First() + "/"), "gemma4:e4b");
            lab.Port = GatewayTestHost.ReserveLoopbackPort();
            lab.Host = await GatewayTestHost.StartAsync(inferenceWorkers: [lab.worker], exposure: new() { FrontDoorPort = lab.Port });
            var now = DateTimeOffset.UtcNow;
            var list = ApiKeyList.Empty;
            IssuedApiKey key, voice;
            (list, key) = list.Create("Ana's laptop", OpenAiEndpoint.Scopes(false), null, "desktop-test", now);
            (list, voice) = list.Create("Home Assistant", [ApiKeyScopes.Voice], null, "desktop-test", now);
            (lab.Key, lab.VoiceOnly) = (key, voice);
            lab.Keys = lab.Host.Server.MergeApiKeys(list);
            var certificate = lab.Host.Certificate.GetCertHashString();
            lab.client = new HttpClient(new HttpClientHandler
            {
                UseCookies = false, AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback = (_, presented, _, _) => presented?.GetCertHashString() == certificate
            });
            return lab;
        }

        internal void Revoke(string id) => Keys = Host.Server.MergeApiKeys(Keys.Revoke(id, "desktop-test", DateTimeOffset.UtcNow));

        internal Task<HttpResponseMessage> Send(HttpMethod method, string path, string? key = null, HttpContent? content = null,
            string? cookies = null, string? origin = null)
        {
            var request = new HttpRequestMessage(method, Origin + path) { Content = content };
            if (key is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            if (cookies is not null) request.Headers.Add("Cookie", cookies);
            if (origin is not null) request.Headers.Add("Origin", origin);
            return client.SendAsync(request);
        }

        /// <summary>Opens the page as a new browser: its form cookie and the form token in the page.</summary>
        internal async Task<(string Cookie, string Form)> OpenPageAsync()
        {
            using var page = await Send(HttpMethod.Get, "/");
            var html = await page.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            var cookie = Cookie(page, GatewayFrontDoorSessions.FormCookie)!;
            return (cookie, FormToken(html));
        }

        /// <summary>Signs in with <paramref name="key"/> as a new browser; the cookies to send afterwards.</summary>
        internal async Task<string> SignInAsync(string key)
        {
            var (formCookie, form) = await OpenPageAsync();
            using var signIn = await Send(HttpMethod.Post, FrontDoorPaths.SignIn, content: Form(form, key),
                cookies: $"{GatewayFrontDoorSessions.FormCookie}={formCookie}", origin: Origin);
            Assert.Equal(HttpStatusCode.SeeOther, signIn.StatusCode);
            Assert.Equal("/", signIn.Headers.Location?.OriginalString);
            var session = Cookie(signIn, GatewayFrontDoorSessions.SessionCookie);
            Assert.NotNull(session);
            return $"{GatewayFrontDoorSessions.FormCookie}={formCookie}; {GatewayFrontDoorSessions.SessionCookie}={session}";
        }

        public async ValueTask DisposeAsync()
        {
            client?.Dispose();
            if (Host is not null) await Host.DisposeAsync();
            if (worker is not null) await worker.DisposeAsync();
            if (ollama is not null) await ollama.DisposeAsync();
        }
    }

    private static class FrontDoorPaths
    {
        internal const string SignIn = GatewayFrontDoorRoutes.FrontDoorSignInPath;
        internal const string SignOut = GatewayFrontDoorRoutes.FrontDoorSignOutPath;
    }

    private static FormUrlEncodedContent Form(string form, string? key = null) =>
        new(key is null ? [new("form", form)] : [new("form", form), new("key", key)]);

    private static string? Cookie(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Where(v => v.StartsWith(name + "=", StringComparison.Ordinal)).Select(v => v[(name.Length + 1)..].Split(';')[0]).FirstOrDefault()
            : null;

    private static string FormToken(string html) => FormField().Match(html) is { Success: true } match
        ? WebUtility.HtmlDecode(match.Groups[1].Value) : throw new Xunit.Sdk.XunitException("The page has no form token: " + html);

    [GeneratedRegex("name=\"form\" value=\"([^\"]+)\"")]
    private static partial Regex FormField();

    [Fact]
    public async Task The_front_door_serves_only_its_page_health_and_the_openai_api()
    {
        await using var lab = await Lab.StartAsync();
        Assert.Contains($"127.0.0.1:{lab.Port}", lab.Host.Server.FrontDoorListeners);

        using var page = await lab.Send(HttpMethod.Get, "/");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Online", html);
        Assert.Contains("Sign in with your key", html);
        Assert.DoesNotContain("gemma4", html);
        Assert.DoesNotContain("fixture-host", html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("default-src 'none'", page.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("DENY", page.Headers.GetValues("X-Frame-Options").Single());
        Assert.NotNull(Cookie(page, GatewayFrontDoorSessions.FormCookie));
        Assert.Contains("httponly", page.Headers.GetValues("Set-Cookie").Single().ToLowerInvariant());

        using var live = await lab.Send(HttpMethod.Get, "/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);

        // Nothing of the gateway's private API is there, signed or not.
        foreach (var (method, path) in new[]
        {
            (HttpMethod.Get, "/martlet/v1/status"), (HttpMethod.Post, "/martlet/v1/pair"), (HttpMethod.Get, "/martlet/v1/security"),
            (HttpMethod.Get, "/health/ready"), (HttpMethod.Get, "/portal/sign-in"), (HttpMethod.Get, "/index.html")
        })
        {
            using var hidden = await lab.Send(method, path, lab.Key.Token, method == HttpMethod.Post ? new StringContent("{}") : null);
            Assert.True(hidden.StatusCode == HttpStatusCode.NotFound, $"{method} {path}: {hidden.StatusCode}");
            Assert.Equal("Not found.\n", await hidden.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task The_openai_api_on_the_front_door_needs_an_openai_key_and_never_locks_anyone_out()
    {
        await using var lab = await Lab.StartAsync();
        using var none = await lab.Send(HttpMethod.Get, "/v1/models");
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        var error = JsonDocument.Parse(await none.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
        Assert.Equal(OpenAiEndpoint.FrontDoorMissingKey, error.GetProperty("code").GetString());
        Assert.Equal("authentication_error", error.GetProperty("type").GetString());

        using var voice = await lab.Send(HttpMethod.Get, "/v1/models", lab.VoiceOnly.Token);
        Assert.Equal(HttpStatusCode.Forbidden, voice.StatusCode);

        // Everyone comes through one proxy: wrong keys answer 401 every time and never block the right one.
        for (var i = 0; i < 15; i++)
        {
            using var wrong = await lab.Send(HttpMethod.Get, "/v1/models", "martlet_" + new string((char)('a' + i % 26), 66));
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }
        using var models = await lab.Send(HttpMethod.Get, "/v1/models", lab.Key.Token);
        var list = JsonDocument.Parse(await models.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(HttpStatusCode.OK, models.StatusCode);
        Assert.Contains("gemma4:e4b", list.GetProperty("data").EnumerateArray().Select(m => m.GetProperty("id").GetString()));

        using var chat = await lab.Send(HttpMethod.Post, "/v1/chat/completions", lab.Key.Token, new StringContent(JsonSerializer.Serialize(new
        {
            model = "gemma4:e4b", messages = new[] { new { role = "user", content = "Hi!" } }
        }), Encoding.UTF8, "application/json"));
        var answer = JsonDocument.Parse(await chat.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(HttpStatusCode.OK, chat.StatusCode);
        Assert.Equal("Hello through the front door.", answer.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
    }

    [Fact]
    public async Task A_key_signs_in_to_the_page_which_then_shows_its_models_and_use_until_sign_out()
    {
        await using var lab = await Lab.StartAsync();
        using (var chat = await lab.Send(HttpMethod.Post, "/v1/chat/completions", lab.Key.Token, new StringContent(JsonSerializer.Serialize(new
        {
            model = "gemma4:e4b", messages = new[] { new { role = "user", content = "Hi!" } }
        }), Encoding.UTF8, "application/json")))
            Assert.Equal(HttpStatusCode.OK, chat.StatusCode);

        var cookies = await lab.SignInAsync(lab.Key.Token);
        Assert.Equal(1, lab.Host.Server.FrontDoorSessions);
        using var page = await lab.Send(HttpMethod.Get, "/", cookies: cookies);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Ana&#39;s laptop", html);
        Assert.Contains("<code>gemma4:e4b</code>", html);
        // gemma4:e4b is rated Fast: the page offers the network model names that fit it.
        Assert.Contains("<code>martlet@any</code>", html);
        Assert.Contains("<code>martlet@fast</code>", html);
        Assert.DoesNotContain("martlet@smart", html);
        Assert.Contains($"https://127.0.0.1:{lab.Port}/v1", html);
        Assert.Contains("1 requests through this address", html);
        Assert.DoesNotContain(lab.Key.Token, html);

        using var signOut = await lab.Send(HttpMethod.Post, FrontDoorPaths.SignOut, content: Form(FormToken(html)), cookies: cookies, origin: lab.Origin);
        Assert.Equal(HttpStatusCode.SeeOther, signOut.StatusCode);
        using var after = await lab.Send(HttpMethod.Get, "/", cookies: cookies);
        Assert.Contains("Sign in with your key", await after.Content.ReadAsStringAsync());
        Assert.Equal(0, lab.Host.Server.FrontDoorSessions);
    }

    [Fact]
    public async Task Sign_in_refuses_a_bad_key_a_forged_form_and_another_site_and_a_revoked_key_ends_the_session()
    {
        await using var lab = await Lab.StartAsync();
        var (formCookie, form) = await lab.OpenPageAsync();
        var cookie = $"{GatewayFrontDoorSessions.FormCookie}={formCookie}";

        async Task Refused(HttpContent content, string? cookies, string? origin, string text)
        {
            using var response = await lab.Send(HttpMethod.Post, FrontDoorPaths.SignIn, content: content, cookies: cookies, origin: origin);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains(text, html);
            Assert.Null(Cookie(response, GatewayFrontDoorSessions.SessionCookie));
        }
        await Refused(Form(form, "martlet_" + new string('a', 66)), cookie, lab.Origin, "doesn&#39;t work here");
        await Refused(Form(form, lab.VoiceOnly.Token), cookie, lab.Origin, "isn&#39;t for language models");
        await Refused(Form(GatewayFrontDoorSessions.NewToken(), lab.Key.Token), cookie, lab.Origin, "That form expired");
        await Refused(Form(form, lab.Key.Token), null, lab.Origin, "That form expired");
        await Refused(Form(form, lab.Key.Token), cookie, "https://evil.example", "That form expired");
        await Refused(new StringContent($"form={form}&key={lab.Key.Token}&extra=1", Encoding.UTF8, "application/x-www-form-urlencoded"),
            cookie, lab.Origin, "That form expired");
        Assert.Equal(0, lab.Host.Server.FrontDoorSessions);

        var cookies = await lab.SignInAsync(lab.Key.Token);
        lab.Revoke(lab.Key.Key.Id);
        using var page = await lab.Send(HttpMethod.Get, "/", cookies: cookies);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("Sign in with your key", html);
        Assert.DoesNotContain("gemma4", html);
        Assert.Equal(0, lab.Host.Server.FrontDoorSessions);
        using var revoked = await lab.Send(HttpMethod.Get, "/v1/models", lab.Key.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }
}
