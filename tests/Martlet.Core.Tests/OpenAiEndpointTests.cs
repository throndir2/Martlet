using Martlet.Core.Access;
using Martlet.Core.Contracts;

namespace Martlet.Core.Tests;

/// <summary>The OpenAI-compatible endpoint's keys and shared settings (docs/OPENAI_ENDPOINT.md).</summary>
public sealed class OpenAiEndpointTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_keys_priority_changes_everywhere_and_keeps_its_secret()
    {
        var (list, issued) = ApiKeyList.Empty.Create("Ana", OpenAiEndpoint.Scopes(false), null, "desktop-a", Now);
        Assert.Equal([ApiKeyScopes.OpenAi], issued.Key.Scopes);
        Assert.StartsWith("Your Martlet first", OpenAiEndpoint.Priority(issued.Key), StringComparison.Ordinal);

        var changed = list.ChangeScopes(issued.Key.Id, OpenAiEndpoint.WithPriority(issued.Key, equalPriority: true), "desktop-b", Now.AddMinutes(1));
        var key = changed.Find(issued.Key.Id)!;
        Assert.Equal([ApiKeyScopes.EqualPriority, ApiKeyScopes.OpenAi], key.Scopes);
        Assert.Equal("desktop-b", key.UpdatedBy);
        Assert.True(key.Revision > issued.Key.Revision);
        // The same secret still works, so the app that uses the key needs nothing new.
        Assert.Same(key, changed.Match(issued.Token, Now.AddMinutes(2)).Key);
        // Another computer's older copy merges to the change; a revocation still wins over it.
        Assert.Equal(key.Scopes, ApiKeyList.Merge(list, changed).Find(key.Id)!.Scopes);
        var revoked = list.Revoke(key.Id, "desktop-a", Now.AddMinutes(3));
        Assert.True(ApiKeyList.Merge(revoked, changed).Find(key.Id)!.Revoked);
        Assert.Throws<ContractException>(() => revoked.ChangeScopes(key.Id, [ApiKeyScopes.OpenAi], "desktop-a", Now.AddMinutes(4)));
        Assert.Same(changed, changed.ChangeScopes(key.Id, key.Scopes, "desktop-a", Now.AddMinutes(4)));
        Assert.Equal([ApiKeyScopes.OpenAi], OpenAiEndpoint.WithPriority(key, equalPriority: false));
    }

    [Theory]
    [InlineData("llm.example.com", "https://llm.example.com/v1")]
    [InlineData("https://LLM.Example.com", "https://llm.example.com/v1")]
    [InlineData("https://llm.example.com/v1/", "https://llm.example.com/v1")]
    [InlineData("https://llm.example.com:8443/v1", "https://llm.example.com:8443/v1")]
    [InlineData("http://llm.example.com/v1", null)]
    [InlineData("https://llm.example.com/api", null)]
    [InlineData("https://user@llm.example.com/v1", null)]
    [InlineData("", null)]
    public void The_public_address_is_an_https_base_url_ending_in_v1(string typed, string? expected)
    {
        Assert.Equal(expected, OpenAiEndpointSettings.NormalizeAddress(typed, out var problem));
        Assert.Equal(expected is null, problem is not null);
    }

    [Fact]
    public void The_settings_travel_to_another_computer_unchanged()
    {
        var first = Directory.CreateTempSubdirectory("martlet-openai-a-");
        var second = Directory.CreateTempSubdirectory("martlet-openai-b-");
        try
        {
            Assert.True(OpenAiEndpointSettings.Load(first.FullName).IsDefault);
            var chosen = new OpenAiEndpointSettings { PublicAddress = "https://llm.example.com/v1", FrontDoorHostId = "gpu-box" };
            Assert.True(chosen.Save(first.FullName));
            var shared = OpenAiEndpointSettings.Load(first.FullName).Share();
            Assert.True(OpenAiEndpointSettings.Parse(shared)!.Save(second.FullName));
            Assert.Equal(chosen, OpenAiEndpointSettings.Load(second.FullName));
            Assert.Equal(shared, OpenAiEndpointSettings.Load(second.FullName).Share());
            Assert.Null(OpenAiEndpointSettings.Parse("""{"schema_version":1,"public_address":"http://plain.example.com/v1"}"""));
            Assert.Null(OpenAiEndpointSettings.Parse("""{"schema_version":2}"""));
        }
        finally
        {
            first.Delete(recursive: true);
            second.Delete(recursive: true);
        }
    }

    [Fact]
    public void The_setup_texts_send_the_whole_site_to_the_front_door()
    {
        // The whole site goes to the front door (its own port), which serves only its page and /v1; never the gateway's 9443.
        var nginx = OpenAiEndpoint.NginxConfig("llm.example.com", "https://192.168.1.20:9445");
        Assert.Contains("location / {", nginx, StringComparison.Ordinal);
        Assert.Contains("proxy_pass https://192.168.1.20:9445;", nginx, StringComparison.Ordinal);
        Assert.Contains("proxy_set_header Host $host;", nginx, StringComparison.Ordinal);
        Assert.Contains("proxy_buffering off;", nginx, StringComparison.Ordinal);
        Assert.DoesNotContain("9443", nginx, StringComparison.Ordinal);
        var caddy = OpenAiEndpoint.CaddyConfig("llm.example.com", "https://192.168.1.20:9445");
        Assert.Contains("reverse_proxy https://192.168.1.20:9445", caddy, StringComparison.Ordinal);
        Assert.Contains("flush_interval -1", caddy, StringComparison.Ordinal);
        var manager = OpenAiEndpoint.ProxyManagerSteps("llm.example.com", "192.168.1.20", 9445);
        Assert.Contains("Forward Hostname / IP:  192.168.1.20", manager, StringComparison.Ordinal);
        Assert.Contains("Forward Port:           9445", manager, StringComparison.Ordinal);
        Assert.Contains("proxy_buffering off;", manager, StringComparison.Ordinal);
        Assert.Equal("tailscale funnel --bg https+insecure://127.0.0.1:9445", OpenAiEndpoint.TailscaleCommand(9445));
        var tunnel = OpenAiEndpoint.CloudflareTunnelConfig("llm.example.com", "https://192.168.1.20:9445");
        Assert.Contains("hostname: llm.example.com", tunnel, StringComparison.Ordinal);
        Assert.Contains("noTLSVerify: true", tunnel, StringComparison.Ordinal);
        Assert.Contains("service: http_status:404", tunnel, StringComparison.Ordinal);
        var invite = OpenAiEndpoint.Invite("Ana", "https://llm.example.com/v1", "martlet_x", ["qwen3:8b"]);
        Assert.Contains("Base URL: https://llm.example.com/v1", invite, StringComparison.Ordinal);
        Assert.Contains("\"model\":\"qwen3:8b\"", invite, StringComparison.Ordinal);
    }
}
