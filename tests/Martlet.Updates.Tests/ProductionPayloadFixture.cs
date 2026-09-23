using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Martlet.Updates.Tests;

public sealed class ProductionPayloadFixture : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Martlet.ProductionPayload-" + Guid.NewGuid().ToString("N"));
    private string[] files = [];
    private string[] directories = [];

    public ProductionPayloadFixture() : this(2) { }

    internal ProductionPayloadFixture(int formatVersion)
    {
        Produce(root, formatVersion: formatVersion).GetAwaiter().GetResult();
        files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
        directories = Directory.GetDirectories(root, "*", SearchOption.AllDirectories);
    }

    internal Dictionary<string, byte[]> Load(string version) =>
        Directory.GetFiles(Path.Combine(root, version), "*", SearchOption.AllDirectories).ToDictionary(
            p => Path.GetRelativePath(Path.Combine(root, version), p).Replace('\\', '/'),
            File.ReadAllBytes, StringComparer.Ordinal);

    internal void AssertNotExecuted() => Assert.False(File.Exists(Path.Combine(root, "PAYLOAD-EXECUTED")));

    internal byte[][] CanonicalVectors() => Directory.GetFiles(root, "canonical-*.json")
        .Order(StringComparer.Ordinal).Select(File.ReadAllBytes).ToArray();

    private static string Script([CallerFilePath] string source = "")
    {
        var local = Path.Combine(Path.GetDirectoryName(source)!, "ProductionPayloadFixture.ps1");
        if (File.Exists(local)) return local;
        var root = Environment.GetEnvironmentVariable("MARTLET_UPDATES_SOURCE_ROOT") ?? Directory.GetCurrentDirectory();
        var explicitPath = Path.Combine(root, "tests", "Martlet.Updates.Tests", "ProductionPayloadFixture.ps1");
        if (Path.IsPathFullyQualified(explicitPath) && File.Exists(explicitPath)) return explicitPath;
        throw new InvalidOperationException("Set MARTLET_UPDATES_SOURCE_ROOT to this checkout when deterministic source mapping hides its path.");
    }

    internal static async Task Produce(string root, string mode = "Payload", TimeSpan? deadline = null,
        Action<int>? onRetired = null, int formatVersion = 2)
    {
        var timeout = deadline ?? TimeSpan.FromSeconds(60);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(60)) throw new ArgumentOutOfRangeException(nameof(deadline));
        using var process = new Process
        {
            StartInfo = new(OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(Script())!
            }
        };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-File", Script(), "-OutputDirectory", root, "-Mode", mode,
            "-FormatVersion", formatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            process.StartInfo.ArgumentList.Add(arg);
        if (!process.Start()) throw new InvalidOperationException("Fixture producer did not start.");
        var overflow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long outputBytes = 0;
        async Task<string> Drain(Stream stream)
        {
            using var output = new MemoryStream();
            var buffer = new byte[4096];
            int count;
            while ((count = await stream.ReadAsync(buffer).ConfigureAwait(false)) != 0)
            {
                if (Interlocked.Add(ref outputBytes, count) <= 65536) output.Write(buffer, 0, count);
                else overflow.TrySetResult();
            }
            return System.Text.Encoding.UTF8.GetString(output.ToArray());
        }
        var stdout = Drain(process.StandardOutput.BaseStream);
        var stderr = Drain(process.StandardError.BaseStream);
        var exit = process.WaitForExitAsync();
        var retired = Task.WhenAll(exit, stdout, stderr);
        try
        {
            var completed = await Task.WhenAny(retired, overflow.Task, Task.Delay(timeout)).ConfigureAwait(false);
            if (completed != retired || overflow.Task.IsCompleted)
                throw new InvalidOperationException("Fixture producer exceeded its deadline or output bound.");
            await retired.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Fixture producer exited {process.ExitCode}: {await stderr.ConfigureAwait(false)}");
            Assert.Contains("PASS: inert fixtures", await stdout.ConfigureAwait(false));
        }
        finally
        {
            // Retain the unique root if termination or either pipe cannot retire; never clean an active child's files.
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { }
            catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
            await retired.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            onRetired?.Invoke(process.Id);
        }
    }

    public void Dispose()
    {
        AssertNotExecuted();
        Assert.Equal(files.Order(StringComparer.Ordinal),
            Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
        Assert.Equal(directories.Order(StringComparer.Ordinal),
            Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
        foreach (var file in files) File.Delete(file);
        foreach (var directory in directories.OrderByDescending(p => p.Length)) Directory.Delete(directory);
        Directory.Delete(root);
    }
}
