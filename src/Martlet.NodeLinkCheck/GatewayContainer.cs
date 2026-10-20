using System.Diagnostics;
using Martlet.Core.Processes;

namespace Martlet.NodeLinkCheck;

/// <summary>Docker control for the live check's disposable Linux gateway container, through the shared
/// <see cref="ProcessRunner"/>: reads a file in it the way Martlet on a host PC does (docker exec ... cat) and restarts it.</summary>
internal sealed class GatewayContainer(string container)
{
    /// <summary>The file's text without surrounding white space, or null when docker exec could not read it.</summary>
    internal async Task<string?> ReadAsync(string path)
    {
        var result = await DockerAsync(TimeSpan.FromSeconds(30), "exec", container, "cat", path);
        return result.ExitCode == 0 ? result.StandardOutput.Trim() : null;
    }

    /// <summary>Runs docker restart and waits for it to end. Like before, the check does not look at its exit code: the
    /// steps after it show whether the gateway came back.</summary>
    internal Task RestartAsync() => DockerAsync(TimeSpan.FromMinutes(2), "restart", container);

    private static Task<ProcessRunResult> DockerAsync(TimeSpan timeout, params string[] arguments)
    {
        var start = new ProcessStartInfo("docker");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return ProcessRunner.RunAsync(start, timeout);
    }
}
