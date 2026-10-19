using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Martlet.Messaging;

namespace Martlet.Desktop.Tests;

public sealed class CloudflaredDownloadTests : IDisposable
{
    private static readonly Uri Release = new("http://127.0.0.1:9/repos/cloudflare/cloudflared/releases/latest");
    private static readonly Uri Asset = new("http://127.0.0.1:9/cloudflared-windows-amd64.exe");
    private readonly string tools = Path.Combine(Path.GetTempPath(), "Martlet.Cloudflared." + Guid.NewGuid().ToString("N"));
    private readonly byte[] program = [(byte)'M', (byte)'Z', .. RandomNumberGenerator.GetBytes(200_000)];

    public void Dispose()
    {
        if (Directory.Exists(tools)) Directory.Delete(tools, recursive: true);
    }

    [Fact]
    public async Task TheDownloadMustMatchGitHubsSizeAndSha256AndIsCheckedAgainBeforeItRuns()
    {
        var path = await CloudflareQuickTunnel.DownloadAsync(tools, null, default, new Fake(program, Digest(program)), Release);
        Assert.Equal(Path.Combine(tools, "cloudflared.exe"), path);
        Assert.Equal(new CloudflaredInstall(path, "2026.10.1", true), CloudflareQuickTunnel.Installed(tools));
        Assert.Equal(path, CloudflareQuickTunnel.Find(tools));

        // A changed copy is not run: Find skips it and the status says it isn't verified.
        File.WriteAllBytes(path, [(byte)'M', (byte)'Z', .. new byte[program.Length - 2]]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        Assert.False(CloudflareQuickTunnel.Installed(tools)!.Verified);
        Assert.NotEqual(path, CloudflareQuickTunnel.Find(tools));

        // A copy from an older Martlet (no record) isn't verified either.
        File.Delete(Path.Combine(tools, CloudflareQuickTunnel.RecordName));
        Assert.Equal(new CloudflaredInstall(path, null, false), CloudflareQuickTunnel.Installed(tools));
    }

    [Fact]
    public void Only_a_copy_signed_by_Cloudflare_passes_the_signature_check()
    {
        Directory.CreateDirectory(tools);
        var unsigned = Path.Combine(tools, "cloudflared.exe");
        File.WriteAllBytes(unsigned, program);
        Assert.Contains("no valid Windows signature", CloudflaredSignature.Problem(unsigned));

        // A file with a valid signature from someone else (Microsoft's runtime) is refused by name.
        var microsoft = Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "coreclr.dll");
        Assert.Contains("not Cloudflare, Inc.", CloudflaredSignature.Problem(microsoft));
    }

    [Fact]
    public async Task An_unsigned_cloudflared_is_not_started()
    {
        Directory.CreateDirectory(tools);
        var unsigned = Path.Combine(tools, "cloudflared.exe");
        File.WriteAllBytes(unsigned, program);
        using var tunnel = new CloudflareQuickTunnel(unsigned);
        var error = await Assert.ThrowsAsync<MessagingException>(() => tunnel.OpenAsync(8080, CancellationToken.None));
        Assert.Contains("doesn't run", error.Message);
    }

    [Fact]
    public async Task AWrongDigestOrACutOffDownloadIsRefusedAndNothingIsKept()
    {
        var wrong = await Assert.ThrowsAsync<InvalidDataException>(() =>
            CloudflareQuickTunnel.DownloadAsync(tools, null, default, new Fake(program, new string('0', 64)), Release));
        Assert.Contains("SHA-256", wrong.Message);
        Assert.False(File.Exists(Path.Combine(tools, "cloudflared.exe")));

        var cut = await Assert.ThrowsAsync<InvalidDataException>(() =>
            CloudflareQuickTunnel.DownloadAsync(tools, null, default, new Fake(program[..1000], Digest(program), program.Length), Release));
        Assert.Contains("stopped before the end", cut.Message);

        var noDigest = await Assert.ThrowsAsync<InvalidDataException>(() =>
            CloudflareQuickTunnel.DownloadAsync(tools, null, default, new Fake(program, null), Release));
        Assert.Contains("no SHA-256", noDigest.Message);
        Assert.Empty(Directory.GetFiles(tools));
    }

    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    // GitHub's release API and the file, on loopback. A cut-off download sends fewer bytes without a Content-Length.
    private sealed class Fake(byte[] body, string? sha256, long? announced = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri == Release)
            {
                var asset = new JsonObject
                {
                    ["name"] = CloudflareQuickTunnel.AssetName, ["state"] = "uploaded", ["size"] = announced ?? body.Length,
                    ["browser_download_url"] = Asset.AbsoluteUri
                };
                if (sha256 is not null) asset["digest"] = "sha256:" + sha256;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(new JsonObject { ["tag_name"] = "2026.10.1", ["assets"] = new JsonArray(asset) }.ToJsonString())
                });
            }
            Assert.Equal(Asset, request.RequestUri);
            var content = new StreamContent(new MemoryStream(body));
            if (announced is not null) content.Headers.ContentLength = null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
