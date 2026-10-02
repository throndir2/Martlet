using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Martlet.Core.Installation;

/// <summary>Docker Desktop's own engine state as <c>docker desktop status --format json</c> reports it (for example
/// <c>running</c>, <c>starting</c> or <c>stopped</c>). Read locally from Docker Desktop; nothing is sent anywhere.</summary>
public static class DockerDesktopStatus
{
    /// <summary>The engine isn't running. While Docker Desktop itself stays open this way it does not recover by
    /// itself (for example when it started before WSL was installed).</summary>
    public const string Stopped = "stopped";

    public static string DockerPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "Docker", "Docker", "resources", "bin", "docker.exe");

    /// <summary>The <c>Status</c> value of <c>docker desktop status --format json</c>, or null when it isn't there.</summary>
    public static string? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("Status", out var value) &&
                value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Asks Docker Desktop for its engine state once, for at most 15 seconds. Null off Windows, when Docker
    /// Desktop isn't installed, doesn't answer or has no <c>docker desktop</c> command.</summary>
    public static async Task<string?> ReadAsync(CancellationToken token)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(DockerPath)) return null;
        var start = new ProcessStartInfo(DockerPath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "desktop", "status", "--format", "json" }) start.ArgumentList.Add(argument);
        Process? process;
        try { process = Process.Start(start); }
        catch (System.ComponentModel.Win32Exception) { return null; }
        if (process is null) return null;
        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                var errors = process.StandardError.ReadToEndAsync(timeout.Token);
                var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                await errors;
                return process.ExitCode == 0 ? Parse(output) : null;
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                if (token.IsCancellationRequested) throw;
                return null;
            }
        }
    }
}
