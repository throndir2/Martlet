using System.Text;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Diagnostics;

namespace Martlet.Doctor;

public static class DoctorCommand
{
    public const string Usage = "Usage: Martlet.Doctor [status] [--json] [--data-directory ABSOLUTE_PATH]\n       Martlet.Doctor list [--json] [--data-directory ABSOLUTE_PATH]\n       Martlet.Doctor run PROBE_ID [PROBE_ID ...] [--json] [--data-directory ABSOLUTE_PATH]\n       Martlet.Doctor --help | --version\nStatus runs only local read-only checks; unavailable stages remain not run. List is catalog metadata, not executed evidence (exit 2). Run checks only the exact selected IDs, never implied audio/GPU/cloud readiness. No device, network, credential-store or AI calls.";

    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken cancellationToken = default,
        ProbeExecutor? executor = null)
    {
        if (args is ["--help"] or ["-h"])
        {
            await output.WriteLineAsync(Usage);
            return 0;
        }
        if (args is ["--version"])
        {
            await output.WriteLineAsync($"Martlet {FoundationStatusService.ApplicationVersion} (foundation)");
            return 0;
        }

        var json = args.Contains("--json", StringComparer.Ordinal);
        var selection = new List<string>();
        var command = "status";
        DoctorReport? report = null;
        try
        {
            string? directory = null;
            var hasJson = false;
            var hasDirectory = false;
            if (args.Length > 132)
                throw new ArgumentException("Too many arguments.");
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "status" when index == 0:
                        break;
                    case "list" or "run" when index == 0:
                        command = args[index];
                        break;
                    case "--json" when !hasJson:
                        hasJson = true;
                        break;
                    case "--data-directory" when !hasDirectory && index + 1 < args.Length:
                        hasDirectory = true;
                        directory = args[++index];
                        break;
                    default:
                        if (command != "run" || args[index].StartsWith('-'))
                            throw new ArgumentException("Invalid invocation.");
                        selection.Add(args[index]);
                        break;
                }
            }
            var store = new SettingsStore(directory ?? SettingsStore.DefaultDataDirectory());
            executor ??= new FoundationStatusService(store).Executor;
            if (command == "run")
                executor.Registry.Select(selection);
        }
        catch (ArgumentException) { report = InvalidInvocation(); }
        catch (NotSupportedException) { report = InvalidInvocation(); }
        catch (PathTooLongException) { report = InvalidInvocation(); }
        catch (InvalidOperationException) { report = InvalidInvocation(); }

        report ??= command == "list" ? executor!.Catalog()
            : await executor!.RunAsync(command == "run" ? selection : null, cancellationToken);
        var text = json ? Encoding.UTF8.GetString(ContractJson.Write(report)) : ReportFormatter.Human(report);
        await output.WriteLineAsync(text);
        return report.ExitCode;
    }

    private static DoctorReport InvalidInvocation() => new()
    {
        Version = ContractVersion.Current,
        ApplicationVersion = FoundationStatusService.ApplicationVersion,
        CreatedAt = DateTimeOffset.UtcNow,
        Probes = [],
        InvocationError = new MartletError
        {
            Code = ErrorCode.InvalidContract, Stage = Stage.Application, Retryable = false,
            Summary = "Invalid command or data directory. Use --help and provide an accessible absolute --data-directory path. No settings were changed.",
            ActionId = "cli.help"
        }
    };
}
