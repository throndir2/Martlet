using System.Diagnostics;

namespace Martlet.LocalStt.Tests;

internal static class ProcessFixture
{
    public static async Task<int> Main(string[] args)
    {
        if (args is not ["--process-fixture", var mode, .. var rest])
            return 3;
        switch (mode)
        {
            case "marker" when rest is [var marker]:
                await File.WriteAllTextAsync(marker, "fixture ran");
                await Task.Delay(TimeSpan.FromSeconds(30));
                return 0;
            case "arguments" when rest is [var expected]:
                await Console.Out.WriteAsync(expected);
                return 0;
            case "exact-limits":
                await Console.Out.WriteAsync(new string(' ', LocalSttPackageManifest.MaximumStandardOutputBytes));
                await Console.Error.WriteAsync(new string(' ', LocalSttPackageManifest.MaximumStandardErrorBytes));
                return 0;
            case "echo":
                await Console.Out.WriteAsync("synthetic stdout");
                await Console.Error.WriteAsync("synthetic stderr");
                return 0;
            case "flood-stdout":
                await Console.Out.WriteAsync(new string('x', LocalSttPackageManifest.MaximumStandardOutputBytes + 1024));
                await Task.Delay(TimeSpan.FromSeconds(30));
                return 0;
            case "fast-flood-stdout":
                await Console.Out.WriteAsync(new string('x', LocalSttPackageManifest.MaximumStandardOutputBytes + 1024));
                return 0;
            case "flood-stderr":
                await Console.Error.WriteAsync(new string('x', LocalSttPackageManifest.MaximumStandardErrorBytes + 1024));
                await Task.Delay(TimeSpan.FromSeconds(30));
                return 0;
            case "sleep":
                await Task.Delay(TimeSpan.FromSeconds(30));
                return 0;
            case "tree" when rest is [var pidFile]:
                using (var child = Process.Start(ChildStartInfo())!)
                {
                    await File.WriteAllTextAsync(pidFile,
                        child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    await Task.Delay(TimeSpan.FromSeconds(30));
                }
                return 0;
            case "orphan" when rest is [var orphanPidFile]:
                var orphan = Process.Start(ChildStartInfo())!;
                await File.WriteAllTextAsync(orphanPidFile,
                    orphan.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                orphan.Dispose();
                return 0;
            default:
                return 3;
        }
    }

    private static ProcessStartInfo ChildStartInfo()
    {
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT")
            ?? throw new InvalidOperationException("The inert fixture requires DOTNET_ROOT.");
        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        info.ArgumentList.Add(typeof(ProcessFixture).Assembly.Location);
        info.ArgumentList.Add("--process-fixture");
        info.ArgumentList.Add("sleep");
        return info;
    }
}
