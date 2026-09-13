using System.Text;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Diagnostics;

namespace Martlet.Doctor;

public static class DoctorCommand
{
    public const string Usage = "Usage: Martlet.Doctor [status] [--json] [--data-directory ABSOLUTE_PATH]\n       Martlet.Doctor --help | --version\nStatus only reads local settings. It does not run audio, network or AI probes.";

    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken cancellationToken = default)
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
        SettingsStore? store = null;
        DoctorReport? report = null;
        try
        {
            string? directory = null;
            var hasJson = false;
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "status" when index == 0:
                        break;
                    case "--json" when !hasJson:
                        hasJson = true;
                        break;
                    case "--data-directory" when directory is null && index + 1 < args.Length:
                        directory = args[++index];
                        break;
                    default:
                        throw new ArgumentException("Invalid invocation.");
                }
            }
            store = new SettingsStore(directory ?? SettingsStore.DefaultDataDirectory());
        }
        catch (ArgumentException) { report = InvalidInvocation(); }
        catch (NotSupportedException) { report = InvalidInvocation(); }
        catch (PathTooLongException) { report = InvalidInvocation(); }
        catch (InvalidOperationException) { report = InvalidInvocation(); }

        report ??= await new FoundationStatusService(store!).GetReportAsync(cancellationToken);
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
