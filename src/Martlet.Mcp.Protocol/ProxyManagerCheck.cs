using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Access;

namespace Martlet.Mcp;

/// <summary>proxy_manager_check: runs the production <see cref="ProxyManager"/> (Share › Show setup › Set it up in Nginx Proxy
/// Manager) against a FIXTURE Nginx Proxy Manager API in memory: a site that already has the name and goes to the old front
/// door, a wildcard certificate and an expired one. Contacts nothing.</summary>
internal static class ProxyManagerCheck
{
    internal static async Task<object> RunAsync(CancellationToken cancellation)
    {
        var steps = new List<object>();
        var passed = true;
        void Step(string name, bool ok, string detail)
        {
            steps.Add(new { name, ok, detail });
            passed &= ok;
        }

        var fake = new Fixture();
        fake.Certificates.Add(new JsonObject { ["id"] = 3, ["domain_names"] = new JsonArray("*.example.net"), ["expires_on"] = "2099-01-01 00:00:00" });
        fake.Certificates.Add(new JsonObject { ["id"] = 4, ["domain_names"] = new JsonArray("*.old.example"), ["expires_on"] = "2001-01-01 00:00:00" });
        fake.Sites.Add(new JsonObject
        {
            ["id"] = 11, ["domain_names"] = new JsonArray("martlet.example.net"), ["forward_scheme"] = "https", ["forward_host"] = "192.168.1.30",
            ["forward_port"] = 9445, ["certificate_id"] = 3, ["ssl_forced"] = true, ["advanced_config"] = "", ["enabled"] = true
        });
        using var http = new HttpClient(fake);
        var manager = new ProxyManager(http, ProxyManager.Address("192.168.1.20:81")!);

        Step("finds-nginx-proxy-manager", await ProxyManager.AnswersAsync(http, ProxyManager.Address("192.168.1.20:81")!, cancellation),
            "its API at http://192.168.1.20:81/api/ answers status OK");
        try
        {
            await manager.SignInAsync("me@example.net", "wrong", cancellation);
            Step("wrong-password-refused", false, "signed in with a wrong password");
        }
        catch (InvalidOperationException error) { Step("wrong-password-refused", fake.Changes.Count == 0, error.Message); }
        await manager.SignInAsync("me@example.net", "right", cancellation);

        var plan = await manager.PlanAsync("martlet.example.net", "192.168.1.20", 9445, "nas-host", cancellation);
        Step("plans-to-change-the-site-that-is-there", plan is { Changes: true, Site: 11, Certificate: 3 }, plan.Text);
        var done = await manager.ApplyAsync(plan, "192.168.1.20", 9445, "me@example.net", cancellation);
        var put = fake.Changes.LastOrDefault();
        Step("changes-it-and-keeps-its-certificate", fake.Changes.Count == 1 && put.Method == "PUT" && put.Path == "/api/nginx/proxy-hosts/11" &&
            put.Body?["forward_host"]?.GetValue<string>() == "192.168.1.20" && put.Body["certificate_id"]?.GetValue<int>() == 3 &&
            put.Body["advanced_config"]?.GetValue<string>() == ProxyManager.AdvancedConfig && !put.Body.ContainsKey("meta"), done);

        var again = await manager.PlanAsync("martlet.example.net", "192.168.1.20", 9445, "nas-host", cancellation);
        await manager.ApplyAsync(again, "192.168.1.20", 9445, "me@example.net", cancellation);
        Step("set-up-already-changes-nothing", !again.Changes && fake.Changes.Count == 1, again.Text);

        var covered = await manager.PlanAsync("llm.example.net", "192.168.1.20", 9445, "nas-host", cancellation);
        var added = await manager.ApplyAsync(covered, "192.168.1.20", 9445, "me@example.net", cancellation);
        Step("adds-a-site-with-a-certificate-that-covers-it", covered is { Site: null, Certificate: 3 } && fake.Changes.Count == 2 &&
            fake.Changes[^1].Method == "POST", added);

        var expired = await manager.PlanAsync("martlet.old.example", "192.168.1.20", 9445, "nas-host", cancellation);
        await manager.ApplyAsync(expired, "192.168.1.20", 9445, "me@example.net", cancellation);
        var asked = fake.Changes[^1].Body;
        Step("asks-lets-encrypt-when-no-certificate-covers-it", expired.Certificate is null &&
            asked?["certificate_id"]?.GetValue<string>() == "new" && asked["meta"]?["letsencrypt_agree"]?.GetValue<bool>() == true, expired.Text);

        Step("password-only-in-sign-in", fake.PasswordSeen == 2 && fake.Changes.All(c => c.Body?.ToJsonString().Contains("right") != true),
            "the password went only to /api/tokens (twice: one wrong, one right), never with a change");
        return new
        {
            exitCode = passed ? 0 : 1,
            report = new { passed, total = steps.Count, steps },
            notCovered = new[] { "a real Nginx Proxy Manager, its nginx reload and Let's Encrypt; the desktop's dialog (drive it with ui_* tools)" }
        };
    }

    /// <summary>FIXTURE: Nginx Proxy Manager's API with one account, its sites and certificates, recording each change.</summary>
    private sealed class Fixture : HttpMessageHandler
    {
        internal readonly JsonArray Sites = [];
        internal readonly JsonArray Certificates = [];
        internal readonly List<(string Method, string Path, JsonObject? Body)> Changes = [];
        internal int PasswordSeen;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? null : await request.Content.ReadFromJsonAsync<JsonObject>(cancellationToken);
            if (path == "/api/") return Json(new JsonObject { ["status"] = "OK" });
            if (path == "/api/tokens")
            {
                PasswordSeen++;
                return body?["secret"]?.GetValue<string>() == "right"
                    ? Json(new JsonObject { ["token"] = "fixture-token" })
                    : Json(new JsonObject { ["error"] = new JsonObject { ["message"] = "Invalid email or password" } }, HttpStatusCode.Unauthorized);
            }
            if (request.Headers.Authorization?.Parameter != "fixture-token") return Json(new JsonObject(), HttpStatusCode.Unauthorized);
            if (request.Method == HttpMethod.Get) return Json(path.EndsWith("certificates", StringComparison.Ordinal) ? Certificates.DeepClone() : Sites.DeepClone());
            Changes.Add((request.Method.Method, path, body));
            if (request.Method == HttpMethod.Post)
            {
                var site = body!.DeepClone().AsObject();
                site["id"] = Sites.Count + 20;
                Sites.Add(site);
                return Json(site.DeepClone(), HttpStatusCode.Created);
            }
            var id = int.Parse(path[(path.LastIndexOf('/') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            var existing = Sites.Select(s => s!.AsObject()).Single(s => s["id"]!.GetValue<int>() == id);
            foreach (var (key, value) in body!) existing[key] = value?.DeepClone();
            return Json(existing.DeepClone());
        }

        private static HttpResponseMessage Json(JsonNode node, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = JsonContent.Create(node) };
    }
}
