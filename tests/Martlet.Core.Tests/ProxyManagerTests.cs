using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Access;

namespace Martlet.Core.Tests;

public sealed class ProxyManagerTests
{
    /// <summary>FIXTURE: Nginx Proxy Manager's API with one account, its sites and certificates, recording each change.</summary>
    internal sealed class FakeManager : HttpMessageHandler
    {
        internal readonly JsonArray Sites = [];
        internal readonly JsonArray Certificates = [];
        internal readonly List<(string Method, string Path, JsonObject? Body)> Changes = [];
        private const string Token = "fixture-token";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? null : await request.Content.ReadFromJsonAsync<JsonObject>(cancellationToken);
            if (path == "/api/" ) return Json(new JsonObject { ["status"] = "OK" });
            if (path == "/api/tokens")
                return body?["identity"]?.GetValue<string>() == "me@example.com" && body["secret"]?.GetValue<string>() == "right"
                    ? Json(new JsonObject { ["token"] = Token })
                    : Json(new JsonObject { ["error"] = new JsonObject { ["code"] = 401, ["message"] = "Invalid email or password" } }, HttpStatusCode.Unauthorized);
            if (request.Headers.Authorization?.Parameter != Token) return Json(new JsonObject(), HttpStatusCode.Unauthorized);
            if (request.Method == HttpMethod.Get && path == "/api/nginx/proxy-hosts") return Json(Sites.DeepClone());
            if (request.Method == HttpMethod.Get && path == "/api/nginx/certificates") return Json(Certificates.DeepClone());
            Changes.Add((request.Method.Method, path, body));
            if (request.Method == HttpMethod.Post && path == "/api/nginx/proxy-hosts")
            {
                var site = body!.DeepClone().AsObject();
                site["id"] = Sites.Count + 1;
                Sites.Add(site);
                return Json(site.DeepClone(), HttpStatusCode.Created);
            }
            if (request.Method == HttpMethod.Put && path.StartsWith("/api/nginx/proxy-hosts/", StringComparison.Ordinal))
            {
                var id = int.Parse(path[(path.LastIndexOf('/') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
                var site = Sites.Select(s => s!.AsObject()).Single(s => s["id"]!.GetValue<int>() == id);
                foreach (var (key, value) in body!) site[key] = value?.DeepClone();
                return Json(site.DeepClone());
            }
            return Json(new JsonObject(), HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(JsonNode node, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = JsonContent.Create(node) };
    }

    private static readonly Uri Address = new("http://192.168.50.9:81/");

    private static (ProxyManager Manager, FakeManager Fake, HttpClient Http) Start()
    {
        var fake = new FakeManager();
        var http = new HttpClient(fake);
        return (new ProxyManager(http, Address), fake, http);
    }

    [Fact]
    public async Task A_site_for_the_name_is_changed_to_the_front_door_once_and_keeps_its_certificate()
    {
        var (manager, fake, http) = Start();
        using var _ = http;
        fake.Certificates.Add(new JsonObject { ["id"] = 3, ["domain_names"] = new JsonArray("*.throndir.com", "throndir.com"), ["expires_on"] = "2099-01-01 00:00:00" });
        fake.Sites.Add(new JsonObject
        {
            ["id"] = 11, ["domain_names"] = new JsonArray("martlet.throndir.com"), ["forward_scheme"] = "https", ["forward_host"] = "192.168.50.200",
            ["forward_port"] = 9445, ["certificate_id"] = 3, ["ssl_forced"] = 1, ["advanced_config"] = "", ["enabled"] = true,
            ["meta"] = new JsonObject { ["nginx_online"] = true }
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.SignInAsync("me@example.com", "wrong", CancellationToken.None));
        await manager.SignInAsync("me@example.com", "right", CancellationToken.None);

        var plan = await manager.PlanAsync("Martlet.Throndir.com", "192.168.50.9", 9445, "nahida-host", CancellationToken.None);
        Assert.True(plan.Changes);
        Assert.Equal(11, plan.Site);
        Assert.Equal(3, plan.Certificate);
        Assert.StartsWith("martlet.throndir.com goes to https://192.168.50.200:9445 now (proxy host 11 in Nginx Proxy Manager): " +
            "Martlet changes it to https://192.168.50.9:9445, the front door on nahida-host", plan.Text);
        Assert.EndsWith("keeps its certificate (*.throndir.com, throndir.com).", plan.Text);

        var done = await manager.ApplyAsync(plan, "192.168.50.9", 9445, "me@example.com", CancellationToken.None);
        Assert.Equal("Changed martlet.throndir.com (proxy host 11) in Nginx Proxy Manager: it goes to https://192.168.50.9:9445 now instead of " +
            "https://192.168.50.200:9445.", done);
        var (method, path, body) = Assert.Single(fake.Changes);
        Assert.Equal(("PUT", "/api/nginx/proxy-hosts/11"), (method, path));
        Assert.Equal("192.168.50.9", body!["forward_host"]!.GetValue<string>());
        Assert.Equal(3, body["certificate_id"]!.GetValue<int>());
        Assert.Equal(ProxyManager.AdvancedConfig, body["advanced_config"]!.GetValue<string>());
        Assert.False(body.ContainsKey("meta"));
        Assert.False(body.ContainsKey("domain_names"));

        var again = await manager.PlanAsync("martlet.throndir.com", "192.168.50.9", 9445, "nahida-host", CancellationToken.None);
        Assert.False(again.Changes);
        Assert.EndsWith("; nothing to change.", again.Text);
        Assert.Equal(again.Text, await manager.ApplyAsync(again, "192.168.50.9", 9445, "me@example.com", CancellationToken.None));
        Assert.Single(fake.Changes);
    }

    [Fact]
    public async Task A_new_site_uses_a_wildcard_certificate_or_asks_lets_encrypt()
    {
        var (manager, fake, http) = Start();
        using var _ = http;
        fake.Certificates.Add(new JsonObject { ["id"] = 1, ["domain_names"] = new JsonArray("*.old.example"), ["expires_on"] = "2001-01-01 00:00:00" });
        fake.Certificates.Add(new JsonObject { ["id"] = 2, ["domain_names"] = new JsonArray("*.example.net") });
        await manager.SignInAsync("me@example.com", "right", CancellationToken.None);

        var covered = await manager.PlanAsync("llm.example.net", "192.168.1.20", 9445, "pi-host", CancellationToken.None);
        Assert.Equal((null, 2), (covered.Site, covered.Certificate));
        Assert.Equal("Added llm.example.net to Nginx Proxy Manager: it goes to https://192.168.1.20:9445, with your certificate *.example.net.",
            await manager.ApplyAsync(covered, "192.168.1.20", 9445, "me@example.com", CancellationToken.None));
        var added = fake.Changes[^1].Body!;
        Assert.Equal("llm.example.net", added["domain_names"]![0]!.GetValue<string>());
        Assert.False(added["meta"]!["letsencrypt_agree"]!.GetValue<bool>());

        var expired = await manager.PlanAsync("martlet.old.example", "192.168.1.20", 9445, "pi-host", CancellationToken.None);
        Assert.Null(expired.Certificate);
        Assert.EndsWith("asks Let's Encrypt for a certificate, so martlet.old.example must reach this proxy on port 80.", expired.Text);
        await manager.ApplyAsync(expired, "192.168.1.20", 9445, "me@example.com", CancellationToken.None);
        var asked = fake.Changes[^1].Body!;
        Assert.Equal("new", asked["certificate_id"]!.GetValue<string>());
        Assert.True(asked["meta"]!["letsencrypt_agree"]!.GetValue<bool>());
        Assert.Equal("me@example.com", asked["meta"]!["letsencrypt_email"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("192.168.50.9:81", "http://192.168.50.9:81/")]
    [InlineData(" http://npm.lan:81/ ", "http://npm.lan:81/")]
    [InlineData("https://npm.example.com", "https://npm.example.com/")]
    [InlineData("", null)]
    [InlineData("ftp://npm.lan", null)]
    [InlineData("http://npm.lan:81/admin", null)]
    public void Address_reads_what_the_owner_typed(string text, string? expected) =>
        Assert.Equal(expected, ProxyManager.Address(text)?.ToString());

    [Fact]
    public async Task Answers_only_when_nginx_proxy_manager_says_ok()
    {
        var (_, _, http) = Start();
        using var _ = http;
        Assert.True(await ProxyManager.AnswersAsync(http, Address, CancellationToken.None));
        using var nothing = new HttpClient(new FakeDown());
        Assert.False(await ProxyManager.AnswersAsync(nothing, Address, CancellationToken.None));
    }

    private sealed class FakeDown : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }
}
