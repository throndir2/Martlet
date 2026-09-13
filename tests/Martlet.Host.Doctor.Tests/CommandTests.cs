using System.Text.Json;
using Martlet.Host.Doctor;

namespace Martlet.Host.Doctor.Tests;

public sealed class CommandTests
{
    [Theory]
    [InlineData("")]
    [InlineData("--help")]
    [InlineData("doctor --help")]
    [InlineData("fixtures")]
    public async Task Help_and_fixture_listing_never_construct_a_live_source(string command)
    {
        var cli = new DoctorCommand(() => throw new InvalidOperationException("No host effects allowed."));
        Assert.Equal(0, await cli.RunAsync(command.Split(' ', StringSplitOptions.RemoveEmptyEntries), new StringWriter(), new StringWriter()));
    }

    [Theory]
    [InlineData("doctor --port 0")]
    [InlineData("doctor --port 65536")]
    [InlineData("doctor --port -1")]
    [InlineData("doctor --port +7443")]
    [InlineData("doctor --port 7443;evil")]
    [InlineData("doctor --port")]
    [InlineData("doctor --json --json")]
    [InlineData("doctor --fixture ../../evil")]
    [InlineData("doctor --scope ready")]
    [InlineData("doctor --install")]
    [InlineData("doctor --output /etc/group")]
    [InlineData("doctor --nvidia-smi /tmp/evil")]
    public async Task Invalid_invocation_never_starts_probes_or_echoes_input(string command)
    {
        var cli = new DoctorCommand(() => throw new InvalidOperationException("No host effects allowed."));
        var output = new StringWriter(); var error = new StringWriter();
        Assert.Equal(3, await cli.RunAsync(command.Split(' '), output, error));
        Assert.Empty(output.ToString());
        Assert.DoesNotContain("evil", error.ToString());
        Assert.Contains("No probes were started", error.ToString());
    }

    [Theory]
    [InlineData("inventory", "inventory", 0)]
    [InlineData("missing-tools", "prerequisites", 1)]
    [InlineData("prerequisites", "prerequisites", 2)]
    [InlineData("windows", "inventory", 3)]
    public async Task Json_and_human_cli_are_projections_of_the_same_result(string fixture, string scope, int exit)
    {
        var cli = new DoctorCommand(() => throw new InvalidOperationException("Fixture must not touch host."));
        var human = new StringWriter(); var json = new StringWriter();
        var args = new[] { "doctor", "--fixture", fixture, "--scope", scope };
        Assert.Equal(exit, await cli.RunAsync(args, human, new StringWriter()));
        Assert.Equal(exit, await cli.RunAsync([.. args, "--json"], json, new StringWriter()));
        using var document = JsonDocument.Parse(json.ToString());
        Assert.Equal(exit, document.RootElement.GetProperty("exitCode").GetInt32());
        foreach (var probe in document.RootElement.GetProperty("probes").EnumerateArray())
        {
            Assert.Contains(probe.GetProperty("code").GetString()!, human.ToString());
            Assert.Contains(probe.GetProperty("remedy").GetProperty("instruction").GetString()!, human.ToString());
        }
    }

    [Fact]
    public async Task Windows_execution_is_explicitly_unsupported_without_commands()
    {
        if (!OperatingSystem.IsWindows()) return;
        var source = new LocalHostSource(new ForbiddenRunner());
        var json = new StringWriter();
        Assert.Equal(3, await new DoctorCommand(() => source).RunAsync(["doctor", "--json"], json, new StringWriter()));
        Assert.Contains("HOST_UNSUPPORTED_EXECUTION", json.ToString());
        Assert.Contains("LiveLocal", json.ToString());
        Assert.DoesNotContain("AuthoredFixture", json.ToString());
        Assert.DoesNotContain("24.04-generic", json.ToString());
    }

    [Fact]
    public async Task Original_cancellation_prevents_fixture_acceptance_even_with_delayed_callbacks()
    {
        using var cancel = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        using var registration = cancel.Token.Register(() => { entered.Set(); release.Wait(); });
        var cancelTask = cancel.CancelAsync();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
        try
        {
            var output = new StringWriter();
            var exit = await new DoctorCommand().RunAsync(["doctor", "--fixture", "inventory", "--scope", "inventory", "--json"],
                output, new StringWriter(), cancel.Token);
            Assert.Equal(2, exit);
            Assert.Contains("HOST_CANCELED", output.ToString());
        }
        finally { release.Set(); await cancelTask; }
    }

    [Fact]
    public void Production_command_policy_has_no_shell_docker_plugins_contexts_or_environment_inheritance()
    {
        foreach (var kind in Enum.GetValues<CommandKind>())
        {
            var start = BoundedCommandRunner.ApprovedStartInfo(kind);
            Assert.False(start.UseShellExecute);
            Assert.Empty(start.Arguments);
            Assert.Equal(4, start.Environment.Count);
            Assert.DoesNotContain("DOCKER_HOST", start.Environment.Keys);
            Assert.DoesNotContain("LD_PRELOAD", start.Environment.Keys);
            Assert.DoesNotContain("CUDA_VISIBLE_DEVICES", start.Environment.Keys);
            Assert.StartsWith("/usr/bin/", start.FileName);
            Assert.DoesNotContain("docker", Path.GetFileName(start.FileName));
            Assert.DoesNotContain(start.ArgumentList, a => a.Contains("--set", StringComparison.Ordinal) || a.Contains("--gpu-reset", StringComparison.Ordinal));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => BoundedCommandRunner.ApprovedStartInfo((CommandKind)999));
    }

    private sealed class ForbiddenRunner : ICommandRunner
    {
        public Task<CommandResult> RunAsync(CommandKind command, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A physical host command must not run in this test.");
    }
}
