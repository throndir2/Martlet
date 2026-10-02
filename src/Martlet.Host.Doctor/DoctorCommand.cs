using System.Globalization;

namespace Martlet.Host.Doctor;

public sealed class DoctorCommand
{
    public const string Help = """
        Martlet host doctor - INTERNAL read-only inventory, NOT a self-host installer.
        Initial live target: Ubuntu 24.04 LTS x86_64. No .NET/Git/Python/Docker needed to read the packaged report.

        martlet-host --help
        martlet-host doctor [--scope inventory|prerequisites] [--port 7443] [--no-gpu-query] [--json]
        martlet-host doctor --fixture NAME [--scope inventory|prerequisites] [--port 7443] [--json]
        martlet-host fixtures

        Default scope: prerequisites. Default output: human-readable stdout; --json emits versioned sanitized JSON.
        inventory: fixed local files, root-filesystem free space, clock and local address/port shape only; no child commands.
        prerequisites: additionally fixed local dpkg-query metadata and a bounded read-only nvidia-smi query.
        --no-gpu-query leaves GPU visibility NOT RUN; no CUDA/container/model qualification is implied.
        Fixture observations are authored, NOT this machine and NOT AI. Help/fixtures do not probe the host.
        No Docker CLI/plugins/contexts, daemon connections, DNS/HTTP/LAN probes, pulls, installs or remediation.
        No host writes. To export explicitly, redirect stdout to a new local file you choose and review it before sharing.
        Exit: 0 requested required observations passed (not deployment); 1 missing/conflicting required prerequisite;
              2 incomplete/unknown/degraded required checks; 3 invalid invocation/configuration or unsupported execution.
        Exact model/driver/runtime pins, pairing/firewall/TLS evidence and real inference await H02/H03/H06.
        """;
    private readonly Func<IHostSource> sourceFactory;
    public DoctorCommand(Func<IHostSource>? sourceFactory = null) => this.sourceFactory = sourceFactory ?? (() => new LocalHostSource());

    public async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken = default)
    {
        if (args.Length == 0 || args is ["--help"] or ["help"] or ["doctor", "--help"])
        {
            await stdout.WriteLineAsync(Help).ConfigureAwait(false);
            return 0;
        }
        if (args is ["fixtures"])
        {
            await stdout.WriteLineAsync("AUTHORED ONLY - NOT THIS HOST:\n" + string.Join('\n', FixtureCatalog.Names)).ConfigureAwait(false);
            return 0;
        }
        if (!TryParse(args, out var options))
        {
            await stderr.WriteLineAsync("HOST_INVOCATION: Invalid or duplicate option. Run martlet-host --help. No probes were started.").ConfigureAwait(false);
            return 3;
        }
        try
        {
            HostSnapshot snapshot;
            DateTimeOffset now;
            if (options.Fixture is { } fixture)
            {
                snapshot = FixtureCatalog.Create(fixture);
                if (options.NoGpuQuery)
                    snapshot = snapshot with { Commands = snapshot.Commands.Remove(CommandKind.NvidiaQuery) };
                now = FixtureCatalog.Timestamp.AddSeconds(10);
            }
            else
            {
                snapshot = await sourceFactory().CaptureAsync(options.Scope, !options.NoGpuQuery, cancellationToken).ConfigureAwait(false);
                now = DateTimeOffset.UtcNow;
            }
            var report = HostEvaluator.Evaluate(snapshot, options.Scope, options.Port, now, cancellationToken.IsCancellationRequested);
            await stdout.WriteLineAsync(options.Json ? HostJson.Serialize(report) : ReportFormatter.Human(report)).ConfigureAwait(false);
            return report.ExitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await stderr.WriteLineAsync("HOST_CANCELED: Read-only inventory canceled. No successful report was accepted.").ConfigureAwait(false);
            return 2;
        }
        catch (InvalidDataException)
        {
            await stderr.WriteLineAsync("HOST_CONTRACT: Unsupported or invalid host contract/configuration. No readiness result was accepted.").ConfigureAwait(false);
            return 3;
        }
        catch (IOException)
        {
            await stderr.WriteLineAsync("HOST_IO_ERROR: Cannot complete local report I/O. Native details are omitted; no readiness result was accepted.").ConfigureAwait(false);
            return 2;
        }
    }

    private sealed record Options(DoctorScope Scope, int Port, bool Json, bool NoGpuQuery, string? Fixture);
    private static bool TryParse(string[] args, out Options options)
    {
        options = new(DoctorScope.Prerequisites, 7443, false, false, null);
        if (args.Length is < 1 or > 10 || args[0] != "doctor" || args.Any(a => a.Length > 128)) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i++)
        {
            if (!seen.Add(args[i])) return false;
            switch (args[i])
            {
                case "--json": options = options with { Json = true }; break;
                case "--no-gpu-query": options = options with { NoGpuQuery = true }; break;
                case "--scope":
                    if (++i >= args.Length || args[i] is not ("inventory" or "prerequisites")) return false;
                    options = options with { Scope = args[i] == "inventory" ? DoctorScope.Inventory : DoctorScope.Prerequisites };
                    break;
                case "--port":
                    if (++i >= args.Length || !int.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
                        port is < 1 or > 65535) return false;
                    options = options with { Port = port };
                    break;
                case "--fixture":
                    if (++i >= args.Length || !FixtureCatalog.Names.Contains(args[i], StringComparer.Ordinal)) return false;
                    options = options with { Fixture = args[i] };
                    break;
                default: return false;
            }
        }
        return true;
    }
}
