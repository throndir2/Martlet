using System.Diagnostics;
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
    /// <summary>Cloudflare's official Windows build of cloudflared, from its GitHub releases.</summary>
    public static readonly Uri Download = new("https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe");
    public static readonly Uri About = new("https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/do-more-with-tunnels/trycloudflare/");
    private Process? process;
    private Uri? address;

    public bool Lost => address is not null && process is { HasExited: true };

    [GeneratedRegex(@"https://[a-z0-9-]+\.trycloudflare\.com", RegexOptions.IgnoreCase)]
    private static partial Regex AddressPattern();

    public async Task<Uri> OpenAsync(int port, CancellationToken cancellation)
    {
        if (address is not null && process is { HasExited: false }) return address;
        Close();
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

    /// <summary>cloudflared.exe on this PC: Martlet's own download, then PATH and the places its installer and winget use.</summary>
    public static string? Find(string? toolsDirectory)
    {
        var candidates = new List<string>();
        if (toolsDirectory is not null) candidates.Add(Path.Combine(toolsDirectory, "cloudflared.exe"));
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(Path.Combine(folder.Trim('"'), "cloudflared.exe"));
        foreach (var root in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
            candidates.Add(Path.Combine(Environment.GetFolderPath(root), "cloudflared", "cloudflared.exe"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "cloudflared.exe"));
        return candidates.FirstOrDefault(path =>
        {
            try { return Path.IsPathFullyQualified(path) && File.Exists(path); }
            catch (ArgumentException) { return false; }
        });
    }

    /// <summary>Downloads Cloudflare's official cloudflared.exe into <paramref name="toolsDirectory"/> and gives its path.</summary>
    public static async Task<string> DownloadAsync(string toolsDirectory, IProgress<double>? progress, CancellationToken cancellation,
        HttpMessageHandler? handler = null, Uri? from = null)
    {
        Directory.CreateDirectory(toolsDirectory);
        var path = Path.Combine(toolsDirectory, "cloudflared.exe");
        var partial = path + ".download";
        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromMinutes(10);
        try
        {
            using (var response = await http.GetAsync(from ?? Download, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                await using var source = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
                await using var target = File.Create(partial);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
                    done += read;
                    if (total > 0) progress?.Report((double)done / total.Value);
                }
            }
            using (var check = File.OpenRead(partial))
            {
                var header = new byte[2];
                if (check.Read(header) != 2 || header[0] != (byte)'M' || header[1] != (byte)'Z')
                    throw new InvalidDataException("The download isn't a Windows program.");
            }
            File.Move(partial, path, overwrite: true);
            return path;
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

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
