using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Martlet.Desktop;

internal sealed record GitHubUpdate(
    Version Version, string Tag, string AssetName, long AssetId, long Bytes, string Sha256, Uri ReleasePage);

internal sealed class UpdateCleanupException(string path, Exception inner)
    : IOException($"Could not remove incomplete update file {path}. Resolve file access and remove only that file before retrying.", inner)
{ }

internal sealed class GitHubReleaseClient(HttpClient client)
{
    private const long MaximumInstallerBytes = 512L * 1024 * 1024;
    private const int MaximumMetadataBytes = 256 * 1024;
    private static readonly Uri LatestRelease = new("https://api.github.com/repos/throndir2/Martlet/releases/latest");

    internal static HttpClient CreateHttpClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        Credentials = null,
        UseProxy = true,
        AutomaticDecompression = DecompressionMethods.None,
        MaxResponseHeadersLength = 16,
        ConnectTimeout = TimeSpan.FromSeconds(15)
    }) { Timeout = TimeSpan.FromMinutes(10) };

    internal async Task<GitHubUpdate?> CheckAsync(Version installed, CancellationToken token)
    {
        using var request = Request(LatestRelease, "application/vnd.github+json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidDataException("No public stable Martlet Release is available. The repository or release may still be private.");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumMetadataBytes)
            throw new InvalidDataException("GitHub Release metadata exceeds the supported size.");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        var bytes = new byte[MaximumMetadataBytes + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count), timeout.Token);
            if (read == 0) break;
            count += read;
        }
        if (count > MaximumMetadataBytes)
            throw new InvalidDataException("GitHub Release metadata exceeds the supported size.");
        try
        {
            using var document = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 12 });
            return Parse(document.RootElement, installed);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("GitHub Release metadata is malformed.");
        }
    }

    internal async Task DownloadAsync(GitHubUpdate update, string destination, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        if (!Path.IsPathFullyQualified(destination) ||
            !string.Equals(Path.GetFileName(destination), update.AssetName, StringComparison.Ordinal))
            throw new ArgumentException("Select the original installer filename in an absolute destination directory.", nameof(destination));
        var path = Path.GetFullPath(destination);
        if (File.Exists(path)) throw new IOException("An installer already exists at that location. Choose a new directory.");
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{update.AssetName}.{Guid.NewGuid():N}.part");
        var assetUri = new Uri($"https://api.github.com/repos/throndir2/Martlet/releases/assets/{update.AssetId}");
        try
        {
            using var request = Request(assetUri, "application/octet-stream");
            using var first = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            HttpResponseMessage? redirected = null;
            try
            {
                if (first.StatusCode == HttpStatusCode.Redirect)
                {
                    if (first.Headers.Location is not { IsAbsoluteUri: true } location ||
                        !IsReleaseAssetUri(location))
                        throw new InvalidDataException("GitHub did not return a trusted release-asset redirect.");
                    using var downloadRequest = Request(location, "application/octet-stream");
                    redirected = await client.SendAsync(downloadRequest, HttpCompletionOption.ResponseHeadersRead, token);
                }
                var response = redirected ?? first;
                response.EnsureSuccessStatusCode();
                if (response.StatusCode != HttpStatusCode.OK ||
                    response.Content.Headers.ContentLength is { } length && length != update.Bytes)
                    throw new InvalidDataException("Release asset size differs from its GitHub metadata.");
                await using var input = await response.Content.ReadAsStreamAsync(token);
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[81920];
                    long total = 0;
                    while (true)
                    {
                        var read = await input.ReadAsync(buffer, token);
                        if (read == 0) break;
                        total += read;
                        if (total > update.Bytes || total > MaximumInstallerBytes)
                            throw new InvalidDataException("Release asset exceeds its declared size.");
                        digest.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), token);
                    }
                    if (total != update.Bytes ||
                        !Convert.ToHexString(digest.GetHashAndReset()).Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Release asset is incomplete or differs from its GitHub SHA-256 digest.");
                    output.Flush(flushToDisk: true);
                }
            }
            finally
            {
                redirected?.Dispose();
            }
            File.Move(temporary, path);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new UpdateCleanupException(temporary, error);
            }
        }
    }

    private static GitHubUpdate? Parse(JsonElement root, Version installed)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            ReadBoolean(root, "draft") || ReadBoolean(root, "prerelease"))
            throw new InvalidDataException("Only a published stable GitHub Release can be offered.");
        var tag = ReadString(root, "tag_name");
        if (!TryParseVersion(tag, out var releaseVersion))
            throw new InvalidDataException("GitHub Release tag is not a supported numeric app version.");
        var current = new Version(installed.Major, installed.Minor,
            Math.Max(installed.Build, 0), Math.Max(installed.Revision, 0));
        if (releaseVersion <= current) return null;

        var expectedName = $"Martlet-{tag.TrimStart('v')}-win-x64.exe";
        var assets = ReadProperty(root, "assets");
        if (assets.ValueKind != JsonValueKind.Array || assets.GetArrayLength() > 64)
            throw new InvalidDataException("GitHub Release has an invalid asset list.");
        GitHubUpdate? found = null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object || ReadString(asset, "name") != expectedName) continue;
            if (found is not null) throw new InvalidDataException("GitHub Release has duplicate installer assets.");
            if (ReadString(asset, "state") != "uploaded")
                throw new InvalidDataException("GitHub Release installer is not ready.");
            var id = ReadInt64(asset, "id");
            var size = ReadInt64(asset, "size");
            var digest = ReadString(asset, "digest");
            if (id <= 0 || size is <= 0 or > MaximumInstallerBytes ||
                !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
                digest.Length != 71 || !digest.AsSpan(7).ToArray().All(IsLowerHex))
                throw new InvalidDataException("GitHub Release installer lacks a valid bounded SHA-256 digest.");
            found = new(releaseVersion, tag, expectedName, id, size, digest[7..],
                new Uri($"https://github.com/throndir2/Martlet/releases/tag/{tag}"));
        }
        return found ?? throw new InvalidDataException("Newer stable Release has no matching win-x64 installer asset.");
    }

    private static bool TryParseVersion(string tag, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (tag.Length is < 5 or > 40) return false;
        var value = tag.StartsWith('v') ? tag[1..] : tag;
        var parts = value.Split('.');
        if (parts.Length is not (3 or 4)) return false;
        var numbers = new int[4];
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length is < 1 or > 9 || parts[i].Length > 1 && parts[i][0] == '0' ||
                !parts[i].All(char.IsAsciiDigit) || !int.TryParse(parts[i], out numbers[i]))
                return false;
        }
        version = new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
        return true;
    }

    private static HttpRequestMessage Request(Uri uri, string accept)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("Martlet-Desktop/0.1");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        if (uri.Host == "api.github.com")
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    private static bool IsReleaseAssetUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.Host == "release-assets.githubusercontent.com" &&
        uri.Port == 443 && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 &&
        uri.OriginalString.Length <= 8192;

    private static JsonElement ReadProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value :
            throw new InvalidDataException($"GitHub Release metadata is missing {name}.");

    private static string ReadString(JsonElement element, string name)
    {
        var value = ReadProperty(element, name);
        return value.ValueKind == JsonValueKind.String && value.GetString() is { } text ? text :
            throw new InvalidDataException($"GitHub Release metadata has invalid {name}.");
    }

    private static bool ReadBoolean(JsonElement element, string name)
    {
        var value = ReadProperty(element, name);
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() :
            throw new InvalidDataException($"GitHub Release metadata has invalid {name}.");
    }

    private static long ReadInt64(JsonElement element, string name)
    {
        var value = ReadProperty(element, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number :
            throw new InvalidDataException($"GitHub Release metadata has invalid {name}.");
    }

    private static bool IsLowerHex(char value) => value is >= '0' and <= '9' or >= 'a' and <= 'f';
}
