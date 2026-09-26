using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

public sealed class GitHubReleaseClientTests
{
    [Fact]
    public void UpdateChecksDefaultOffAndAreStoredOutsideProfile()
    {
        var root = Path.Combine(Path.GetTempPath(), "Martlet.UpdateCheck." + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.False(UpdateCheckPreferences.Load(root));
            Assert.False(Directory.Exists(root));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "settings.json"), "profile sentinel");
            UpdateCheckPreferences.Save(root, true);
            Assert.True(UpdateCheckPreferences.Load(root));
            UpdateCheckPreferences.Save(root, false);
            Assert.False(UpdateCheckPreferences.Load(root));
            Assert.Equal("profile sentinel", File.ReadAllText(Path.Combine(root, "settings.json")));
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
            File.WriteAllText(Path.Combine(root, "update-checks.txt"), new string('x', 100));
            Assert.Throws<InvalidDataException>(() => UpdateCheckPreferences.Load(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task NewerStableReleaseOnlyOffersExactVersionedInstaller()
    {
        var bytes = "installer fixture"u8.ToArray();
        using var http = new HttpClient(new ScriptedHandler(request =>
        {
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            Assert.Equal("/repos/throndir2/Martlet/releases/latest", request.RequestUri.AbsolutePath);
            Assert.Null(request.Headers.Authorization);
            return JsonResponse(Release("v0.2.0", "Martlet-0.2.0-win-x64.exe", bytes));
        }));
        var client = new GitHubReleaseClient(http);
        var update = await client.CheckAsync(new Version(0, 1, 0, 0), CancellationToken.None);
        Assert.NotNull(update);
        Assert.Equal(new Version(0, 2, 0, 0), update.Version);
        Assert.Equal("Martlet-0.2.0-win-x64.exe", update.AssetName);
        Assert.Equal(new Uri("https://github.com/throndir2/Martlet/releases/tag/v0.2.0"), update.ReleasePage);
        Assert.Null(await client.CheckAsync(new Version(0, 2, 0, 0), CancellationToken.None));
    }

    [Theory]
    [InlineData("v0.2.0-beta", "Martlet-0.2.0-beta-win-x64.exe")]
    [InlineData("v0.02.0", "Martlet-0.02.0-win-x64.exe")]
    [InlineData("v0.2.0", "Martlet-0.2.0-win-arm64.exe")]
    public async Task RejectsUnsupportedTagsAndMissingInstaller(string tag, string asset)
    {
        using var http = new HttpClient(new ScriptedHandler(_ =>
            JsonResponse(Release(tag, asset, "installer"u8.ToArray()))));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new GitHubReleaseClient(http).CheckAsync(new Version(0, 1, 0, 0), CancellationToken.None));
    }

    [Fact]
    public async Task RejectsUnsignedOrUnpublishedMetadata()
    {
        var bytes = "installer"u8.ToArray();
        foreach (var json in new[]
        {
            Release("v0.2.0", "Martlet-0.2.0-win-x64.exe", bytes, digest: null),
            Release("v0.2.0", "Martlet-0.2.0-win-x64.exe", bytes, prerelease: true),
            Release("v0.2.0", "Martlet-0.2.0-win-x64.exe", bytes, draft: true),
            Release("v0.2.0", "Martlet-0.2.0-win-x64.exe", bytes, size: 600L * 1024 * 1024)
        })
        {
            using var http = new HttpClient(new ScriptedHandler(_ => JsonResponse(json)));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new GitHubReleaseClient(http).CheckAsync(new Version(0, 1, 0, 0), CancellationToken.None));
        }
    }

    [Fact]
    public async Task DownloadFollowsOnlyReleaseAssetsAndRetainsMatchingBytes()
    {
        var bytes = "installer fixture"u8.ToArray();
        var calls = 0;
        using var http = new HttpClient(new ScriptedHandler(request =>
        {
            calls++;
            if (calls == 1) return JsonResponse(Release("v0.2.0", "Martlet-0.2.0-win-x64.exe", bytes));
            if (calls == 2)
            {
                Assert.Equal("/repos/throndir2/Martlet/releases/assets/42", request.RequestUri!.AbsolutePath);
                Assert.Equal("application/octet-stream", request.Headers.Accept.Single().MediaType);
                return new HttpResponseMessage(HttpStatusCode.Redirect)
                {
                    Headers = { Location = new Uri("https://release-assets.githubusercontent.com/fixture?token=opaque") }
                };
            }
            Assert.Equal("release-assets.githubusercontent.com", request.RequestUri!.Host);
            Assert.Null(request.Headers.Authorization);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }));
        var client = new GitHubReleaseClient(http);
        var update = (await client.CheckAsync(new Version(0, 1, 0, 0), CancellationToken.None))!;
        var root = Directory.CreateTempSubdirectory("Martlet.UpdateDownload.").FullName;
        try
        {
            var destination = Path.Combine(root, update.AssetName);
            await client.DownloadAsync(update, destination, CancellationToken.None);
            Assert.Equal(bytes, File.ReadAllBytes(destination));
            Assert.Equal(3, calls);
            Assert.Empty(Directory.GetFiles(root, "*.part"));
            await Assert.ThrowsAsync<IOException>(() => client.DownloadAsync(update, destination, CancellationToken.None));
            Assert.Equal(bytes, File.ReadAllBytes(destination));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task DownloadAcceptsDirectBinaryResponseFromReleaseAssetApi()
    {
        var bytes = "direct installer"u8.ToArray();
        var calls = 0;
        using var http = new HttpClient(new ScriptedHandler(_ =>
        {
            calls++;
            return calls == 1
                ? JsonResponse(Release("v0.2.0", "Martlet-0.2.0-win-x64.exe", bytes))
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }));
        var client = new GitHubReleaseClient(http);
        var update = (await client.CheckAsync(new Version(0, 1, 0, 0), CancellationToken.None))!;
        var root = Directory.CreateTempSubdirectory("Martlet.UpdateDirect.").FullName;
        try
        {
            var destination = Path.Combine(root, update.AssetName);
            await client.DownloadAsync(update, destination, CancellationToken.None);
            Assert.Equal(bytes, File.ReadAllBytes(destination));
            Assert.Equal(2, calls);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("https://evil.example/installer")]
    [InlineData("http://release-assets.githubusercontent.com/installer")]
    [InlineData("https://release-assets.githubusercontent.com.evil.example/installer")]
    public async Task RejectsUntrustedRedirectWithoutRetainingInstaller(string redirect)
    {
        await RejectDownloadAsync(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri(redirect) }
        }, bytes => new ByteArrayContent(bytes));
    }

    [Fact]
    public async Task RejectsMismatchedPayloadAndRemovesPartial()
    {
        await RejectDownloadAsync(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://release-assets.githubusercontent.com/fixture") }
        }, bytes => new ByteArrayContent(bytes.Select(value => (byte)(value ^ 1)).ToArray()));
    }

    private static async Task RejectDownloadAsync(
        Func<HttpRequestMessage, HttpResponseMessage> redirect, Func<byte[], HttpContent> content)
    {
        var bytes = "installer fixture"u8.ToArray();
        var calls = 0;
        using var http = new HttpClient(new ScriptedHandler(request =>
        {
            calls++;
            return calls switch
            {
                1 => JsonResponse(Release("v0.2.0", "Martlet-0.2.0-win-x64.exe", bytes)),
                2 => redirect(request),
                _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content(bytes) }
            };
        }));
        var client = new GitHubReleaseClient(http);
        var update = (await client.CheckAsync(new Version(0, 1, 0, 0), CancellationToken.None))!;
        var root = Directory.CreateTempSubdirectory("Martlet.UpdateReject.").FullName;
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                client.DownloadAsync(update, Path.Combine(root, update.AssetName), CancellationToken.None));
            Assert.Empty(Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string Release(string tag, string name, byte[] bytes, string? digest = "valid",
        bool draft = false, bool prerelease = false, long? size = null) =>
        JsonSerializer.Serialize(new
        {
            tag_name = tag,
            draft,
            prerelease,
            assets = new[]
            {
                new
                {
                    name,
                    state = "uploaded",
                    id = 42,
                    size = size ?? bytes.Length,
                    digest = digest is null ? null : "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
                }
            }
        });

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(reply(request));
    }
}
