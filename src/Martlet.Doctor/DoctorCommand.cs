using System.Text;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Diagnostics;
using Martlet.Audio;
using Martlet.Sessions;
#if WINDOWS
using Martlet.Audio.Windows;
#endif

namespace Martlet.Doctor;

public static class DoctorCommand
{
    public const string Usage = "Usage: Martlet.Doctor [status] [--json] [--data-directory ABSOLUTE_PATH]\n       Martlet.Doctor list [--json] [--data-directory ABSOLUTE_PATH]\n       Martlet.Doctor run PROBE_ID [PROBE_ID ...] [--json] [--data-directory ABSOLUTE_PATH]\n       Martlet.Doctor self-test [--scenario NAME] [--play-tone] [--json] [--data-directory ABSOLUTE_PATH]\n       Martlet.Doctor --help | --version\nStatus/run execute only local read-only checks. List is unrun catalog metadata (exit 2). Self-test is FIXTURE - NOT AI; no settings inspection/writes, network, capture, keys or GPU. Scenarios: complete (default), streaming, refused, refused-after-partial, no-speech, not-addressed, canceled, truncated, slow, failed. Synthetic time is accelerated. Exit 0: requested fixture completed; 1: reported failure; 2: silence/refusal/canceled/incomplete; 3: invalid invocation/configuration. Windows target only: --play-tone explicitly permits a 200 ms synthetic tone (NOT speech) after completed text, on the default output fixed at start; 5 s deadline, no fallback. Check audience/output/volume before passing it. Default is audio OFF. No live readiness is implied.";

    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken cancellationToken = default,
        ProbeExecutor? executor = null)
    {
        if (args is ["--help"] or ["-h"])
        {
            await output.WriteLineAsync(Usage + "\n\n" + SupportHelp.Text);
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
        var scenario = "complete";
        var playTone = false;
        FixtureSession? fixtureSession = null;
        DoctorReport? report = null;
        try
        {
            string? directory = null;
            var hasJson = false;
            var hasDirectory = false;
            var hasScenario = false;
            if (args.Length > 132)
                throw new ArgumentException("Too many arguments.");
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "status" when index == 0:
                        break;
                    case "list" or "run" or "self-test" when index == 0:
                        command = args[index];
                        break;
                    case "--json" when !hasJson:
                        hasJson = true;
                        break;
                    case "--data-directory" when !hasDirectory && index + 1 < args.Length:
                        hasDirectory = true;
                        directory = args[++index];
                        break;
                    case "--scenario" when command == "self-test" && !hasScenario && index + 1 < args.Length:
                        hasScenario = true;
                        scenario = args[++index];
                        break;
                    case "--play-tone" when command == "self-test" && !playTone:
                        playTone = true;
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
            if (command == "self-test")
            {
                if (!FixtureSession.Scenarios.Contains(scenario, StringComparer.Ordinal))
                    throw new ArgumentException("Unknown fixture.");
#if WINDOWS
                fixtureSession = new(playTone ? new PcmPlaybackSink(new WasapiDeviceFactory()) : null);
#else
                if (playTone)
                    throw new NotSupportedException("Tone playback requires the Windows executable.");
                fixtureSession = new();
#endif
            }
        }
        catch (ArgumentException) { report = InvalidInvocation(); }
        catch (NotSupportedException) { report = InvalidInvocation(); }
        catch (PathTooLongException) { report = InvalidInvocation(); }
        catch (InvalidOperationException) { report = InvalidInvocation(); }

        if (fixtureSession is not null)
        {
            await using (fixtureSession)
                report = FixtureDiagnostics.Report(await fixtureSession.RunAsync(scenario,
                    playTone ? new(OutputPolicy.DefaultAtStart) : null, cancellationToken));
        }
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
