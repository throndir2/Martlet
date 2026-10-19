using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Martlet.Messaging;

/// <summary>A public HTTPS address that reaches a port on this PC, so Meta can deliver WhatsApp messages to Martlet.</summary>
public interface IPublicAddress : IDisposable
{
    /// <summary>Opens the address for <paramref name="port"/> (or returns the open one) and gives its base URL.</summary>
    Task<Uri> OpenAsync(int port, CancellationToken cancellation);
    /// <summary>The address was open and stopped working (a tunnel ended); opening again makes a new one.</summary>
    bool Lost { get; }
}

/// <summary>The owner's own public address (a named Cloudflare tunnel, a reverse proxy) that forwards to Martlet's port.</summary>
public sealed class FixedPublicAddress(Uri address) : IPublicAddress
{
    public Task<Uri> OpenAsync(int port, CancellationToken cancellation) => Task.FromResult(address);
    public bool Lost => false;
    public void Dispose() { }
}

/// <summary>A Cloudflare quick tunnel (https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/do-more-with-tunnels/trycloudflare/):
/// Cloudflare's free cloudflared gives a random https://….trycloudflare.com address that reaches localhost, with no account,
/// open port or router change. The address changes each time it starts, so Martlet points the webhook at it again.</summary>
public sealed partial class CloudflareQuickTunnel(string cloudflared) : IPublicAddress
{
    /// <summary>Where Cloudflare publishes its official Windows build of cloudflared (its GitHub releases).</summary>
    public static readonly Uri Download = new("https://github.com/cloudflare/cloudflared/releases/latest");
    /// <summary>GitHub's description of the newest cloudflared release: its version and each file's size and SHA-256.</summary>
    public static readonly Uri LatestRelease = new("https://api.github.com/repos/cloudflare/cloudflared/releases/latest");
    /// <summary>The Windows x64 file in each cloudflared release.</summary>
    public const string AssetName = "cloudflared-windows-amd64.exe";
    /// <summary>Next to Martlet's own cloudflared.exe: the version, size and SHA-256 it had when Martlet checked the download.</summary>
    public const string RecordName = "cloudflared.json";
    private const long MaximumBytes = 256L * 1024 * 1024;
    public static readonly Uri About = new("https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/do-more-with-tunnels/trycloudflare/");
    private Process? process;
    private Uri? address;
    private int openPort;

    public bool Lost => address is not null && process is { HasExited: true };

    [GeneratedRegex(@"https://[a-z0-9-]+\.trycloudflare\.com", RegexOptions.IgnoreCase)]
    private static partial Regex AddressPattern();

    public async Task<Uri> OpenAsync(int port, CancellationToken cancellation)
    {
        if (address is not null && process is { HasExited: false } && openPort == port) return address;
        Close();
        if (CloudflaredSignature.Problem(cloudflared) is { } unsigned)
            throw new MessagingException(MessagingFailure.Network, $"Martlet doesn't run {cloudflared}: {unsigned}.");
        var start = new ProcessStartInfo(cloudflared)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (var argument in new[] { "tunnel", "--no-autoupdate", "--url", $"http://localhost:{port}", "--http-host-header", "localhost" })
            start.ArgumentList.Add(argument);
        var found = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var last = "";
        void Read(object _, DataReceivedEventArgs line)
        {
            if (line.Data is not { } text) return;
            if (AddressPattern().Match(text) is { Success: true } match && !match.Value.Contains("api.trycloudflare", StringComparison.OrdinalIgnoreCase))
                found.TrySetResult(new Uri(match.Value + "/"));
            else if (text.Contains(" ERR ", StringComparison.Ordinal) || text.Contains("error", StringComparison.OrdinalIgnoreCase)) last = text;
        }
        var next = new Process { StartInfo = start, EnableRaisingEvents = true };
        next.ErrorDataReceived += Read;
        next.OutputDataReceived += Read;
        next.Exited += (_, _) => found.TrySetException(new MessagingException(MessagingFailure.Network,
            "Cloudflare's tunnel stopped before it opened" + (last.Length > 0 ? $": {Clip(last)}" : ".")));
        try { next.Start(); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            next.Dispose();
            throw new MessagingException(MessagingFailure.Network, $"Couldn't start cloudflared ({error.Message}).");
        }
        process = next;
        next.BeginErrorReadLine();
        next.BeginOutputReadLine();
        try
        {
            address = await found.Task.WaitAsync(TimeSpan.FromSeconds(45), cancellation).ConfigureAwait(false);
            openPort = port;
        }
        catch (TimeoutException)
        {
            Close();
            throw new MessagingException(MessagingFailure.Network, "Cloudflare's tunnel didn't open in time.");
        }
        catch
        {
            Close();
            throw;
        }
        return address;
    }

    /// <summary>cloudflared.exe on this PC: Martlet's own download (only while it still matches the SHA-256 checked at download),
    /// then PATH and the places its installer and winget use. A copy signed by Cloudflare comes first; when none is, the first
    /// copy found is given, and <see cref="OpenAsync"/> says why it doesn't run.</summary>
    public static string? Find(string? toolsDirectory)
    {
        var candidates = new List<string>();
        if (toolsDirectory is not null && Installed(toolsDirectory) is { Verified: true } own) candidates.Add(own.Path);
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(Path.Combine(folder.Trim('"'), "cloudflared.exe"));
        foreach (var root in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
            candidates.Add(Path.Combine(Environment.GetFolderPath(root), "cloudflared", "cloudflared.exe"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "cloudflared.exe"));
        var found = candidates.Where(path =>
        {
            try { return Path.IsPathFullyQualified(path) && File.Exists(path); }
            catch (ArgumentException) { return false; }
        }).ToList();
        return found.FirstOrDefault(path => CloudflaredSignature.Problem(path) is null) ?? found.FirstOrDefault();
    }

    private static readonly Dictionary<string, (long Length, DateTime Written, CloudflaredInstall Install)> checkedCopies = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Martlet's own cloudflared copy in <paramref name="toolsDirectory"/> and whether it still matches the version,
    /// size and SHA-256 recorded when Martlet checked the download; null when there is no copy. A copy without that record
    /// (from an older Martlet) or that changed is not verified and is not run.</summary>
    public static CloudflaredInstall? Installed(string toolsDirectory)
    {
        var path = Path.Combine(toolsDirectory, "cloudflared.exe");
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return null;
            CloudflaredRecord? record = null;
            try { record = JsonSerializer.Deserialize<CloudflaredRecord>(File.ReadAllBytes(Path.Combine(toolsDirectory, RecordName))); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
            if (record is null || record.Bytes != file.Length || !IsSha256(record.Sha256)) return new(path, record?.Version, false);
            lock (checkedCopies)
                if (checkedCopies.TryGetValue(path, out var seen) && seen.Length == file.Length && seen.Written == file.LastWriteTimeUtc &&
                    seen.Install.Version == record.Version)
                    return seen.Install;
            string digest;
            using (var stream = File.OpenRead(path)) digest = Convert.ToHexStringLower(SHA256.HashData(stream));
            var install = new CloudflaredInstall(path, record.Version, digest == record.Sha256);
            lock (checkedCopies) checkedCopies[path] = (file.Length, file.LastWriteTimeUtc, install);
            return install;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(path, null, false);
        }
    }

    /// <summary>The newest cloudflared release from GitHub: its version and the Windows file's size, SHA-256 and address.</summary>
    public static async Task<CloudflaredRelease> LatestAsync(HttpClient http, CancellationToken cancellation, Uri? release = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, release ?? LatestRelease);
        request.Headers.UserAgent.ParseAdd("Martlet-Desktop/0.1");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 4 * 1024 * 1024) throw new InvalidDataException("GitHub's release information is larger than expected.");
        var text = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            var version = root.TryGetProperty("tag_name", out var tag) && tag.ValueKind == JsonValueKind.String ? tag.GetString() ?? "" : "";
            if (version.Length is 0 or > 40 || !root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("GitHub's release information for cloudflared is incomplete.");
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object || !asset.TryGetProperty("name", out var name) || name.GetString() != AssetName) continue;
                var digest = asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
                var size = asset.TryGetProperty("size", out var z) && z.TryGetInt64(out var bytes) ? bytes : 0;
                var url = asset.TryGetProperty("browser_download_url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                var state = asset.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
                if (state != "uploaded" || size is <= 0 or > MaximumBytes || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
                    !IsSha256(digest[7..]) || !Uri.TryCreate(url, UriKind.Absolute, out var from) ||
                    !(from.Scheme == Uri.UriSchemeHttps && from.Host == "github.com" || release is not null && from.IsLoopback))
                    throw new InvalidDataException("GitHub's release information for cloudflared has no SHA-256 to check the download with.");
                return new(version, size, digest[7..], from);
            }
            throw new InvalidDataException($"The newest cloudflared release ({version}) has no {AssetName}.");
        }
        catch (JsonException)
        {
            throw new InvalidDataException("GitHub's release information for cloudflared couldn't be read.");
        }
    }

    /// <summary>Downloads Cloudflare's official cloudflared.exe (the newest release) into <paramref name="toolsDirectory"/>,
    /// checks its size and the SHA-256 GitHub publishes for it, records them in <see cref="RecordName"/> and gives its path.
    /// <paramref name="release"/> replaces GitHub's release address (a loopback fake, for tests).</summary>
    public static async Task<string> DownloadAsync(string toolsDirectory, IProgress<double>? progress, CancellationToken cancellation,
        HttpMessageHandler? handler = null, Uri? release = null)
    {
        Directory.CreateDirectory(toolsDirectory);
        var path = Path.Combine(toolsDirectory, "cloudflared.exe");
        var partial = path + ".download";
        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromMinutes(10);
        try
        {
            var latest = await LatestAsync(http, cancellation, release).ConfigureAwait(false);
            using (var request = new HttpRequestMessage(HttpMethod.Get, latest.Download))
            {
                request.Headers.UserAgent.ParseAdd("Martlet-Desktop/0.1");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is { } length && length != latest.Bytes)
                    throw new InvalidDataException("The cloudflared download doesn't have the size GitHub gives for it.");
                await using var source = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
                await using var target = File.Create(partial);
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
                {
                    done += read;
                    if (done > latest.Bytes) throw new InvalidDataException("The cloudflared download is larger than GitHub says.");
                    digest.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
                    progress?.Report((double)done / latest.Bytes);
                }
                if (done != latest.Bytes) throw new InvalidDataException("The cloudflared download stopped before the end. Try again.");
                if (Convert.ToHexStringLower(digest.GetHashAndReset()) != latest.Sha256)
                    throw new InvalidDataException("The cloudflared download doesn't match the SHA-256 GitHub publishes for it.");
            }
            using (var check = File.OpenRead(partial))
            {
                var header = new byte[2];
                if (check.Read(header) != 2 || header[0] != (byte)'M' || header[1] != (byte)'Z')
                    throw new InvalidDataException("The download isn't a Windows program.");
            }
            var record = Path.Combine(toolsDirectory, RecordName);
            File.Delete(record);
            File.Move(partial, path, overwrite: true);
            File.WriteAllBytes(record, JsonSerializer.SerializeToUtf8Bytes(new CloudflaredRecord(latest.Version, latest.Bytes, latest.Sha256)));
            return path;
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');

    private void Close()
    {
        address = null;
        if (process is not { } running) return;
        process = null;
        try { if (!running.HasExited) running.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        running.Dispose();
    }

    private static string Clip(string text) => text.Length <= 200 ? text : text[..200] + "…";

    public void Dispose() => Close();
}

/// <summary>A cloudflared release on GitHub: its version and the Windows file's size, SHA-256 (lower-case hex) and address.</summary>
public sealed record CloudflaredRelease(string Version, long Bytes, string Sha256, Uri Download);

/// <summary>Martlet's own cloudflared copy: its path, the version recorded at download and whether it still matches the
/// recorded size and SHA-256.</summary>
public sealed record CloudflaredInstall(string Path, string? Version, bool Verified);

/// <summary>What <see cref="CloudflareQuickTunnel.RecordName"/> holds.</summary>
public sealed record CloudflaredRecord(string Version, long Bytes, string Sha256);
