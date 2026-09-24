using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Martlet.Gateway.Host.Linux;
using Martlet.Gateway.Persistence;
using Microsoft.AspNetCore.Http;

namespace Martlet.Gateway.Host.Linux.Tests;

public sealed class ProbeExecutableTests
{
    private const string Ready = """{"schema_version":1,"scope":"listener-auth-admission","listener":"listening","auth_admission":"open","model_readiness":"not-probed"}""";

    [Theory]
    [InlineData("ready", true)]
    [InlineData("large", false)]
    [InlineData("live", false)]
    [InlineData("wrong-pin", false)]
    [InlineData("redirect", false)]
    [InlineData("deadline", false)]
    public async Task Actual_pinned_probe_is_bounded_and_fails_closed(string scenario, bool expected)
    {
        var origin = FixturePlatform.FreeOrigin();
        using var generated = HostCertificate.Create(DateTimeOffset.UtcNow.AddSeconds(-1));
        var encoded = generated.Export(X509ContentType.Pkcs12);
        using var certificate = HostCertificate.Load(encoded);
        CryptographicOperations.ZeroMemory(encoded);
        var identity = GatewayHostIdentity.FromCertificate("fixture-host", certificate);
        await using var listener = await new KestrelGatewayListenerFactory().StartAsync(
            new(origin, identity, certificate), async context =>
            {
                Assert.Equal("/health/ready", context.Request.Path.Value);
                Assert.DoesNotContain(context.Request.Headers, pair => pair.Key.StartsWith("X-Martlet", StringComparison.OrdinalIgnoreCase));
                context.Response.ContentType = "application/json";
                if (scenario == "redirect")
                {
                    context.Response.StatusCode = 302;
                    context.Response.Headers.Location = origin.CanonicalOrigin + "/not-allowed";
                    return;
                }
                if (scenario == "deadline")
                {
                    await context.Response.StartAsync(context.RequestAborted);
                    await Task.Delay(TimeSpan.FromSeconds(10), context.RequestAborted);
                }
                else await context.Response.WriteAsync(scenario switch
                {
                    "large" => new string(' ', 2049),
                    "live" => "{\"status\":\"live\"}",
                    _ => Ready
                }, context.RequestAborted);
            }, CancellationToken.None);
        var config = HostConfiguration.Parse(ConfigurationTests.Config(origin.CanonicalOrigin));
        var approval = ServiceApproval.Parse(ServiceApproval.Create(config, identity));
        if (scenario == "wrong-pin") approval = approval with { SpkiFingerprint = "sha256:" + new string('b', 64) };
        var ready = false;
        var timer = Stopwatch.StartNew();
        try { ready = await HostHealth.ProbeAsync(config, approval, CancellationToken.None); }
        catch (HostInputException) { }
        catch (GatewayClientException) { }
        catch (OperationCanceledException) { }
        Assert.Equal(expected, ready);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(6));
    }

    [Fact]
    public async Task Actual_executable_help_and_invalid_arguments_are_passive_without_secret_echo()
    {
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        Assert.False(string.IsNullOrEmpty(dotnetRoot));
        foreach (var args in new[] { Array.Empty<string>(), new[] { "--help" }, new[] { "--token", "DO-NOT-ECHO" } })
        {
            var start = new ProcessStartInfo(Path.Combine(dotnetRoot!, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"))
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add(typeof(HostApplication).Assembly.Location);
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            try
            {
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(args.Length == 2 ? 2 : 0, process.ExitCode);
                Assert.DoesNotContain("DO-NOT-ECHO", await output);
                Assert.Empty(await error);
            }
            finally
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            }
        }
    }
}
