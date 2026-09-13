using Martlet.Core.Settings;

namespace Martlet.Diagnostics;

// Compatibility entry point; Desktop and Doctor use this same production registry.
public sealed class FoundationStatusService
{
    public static string ApplicationVersion => typeof(FoundationStatusService).Assembly.GetName().Version!.ToString(3);
    public ProbeExecutor Executor { get; }

    public FoundationStatusService(SettingsStore settingsStore, TimeProvider? clock = null) =>
        Executor = new(ProbeRegistry.Local(settingsStore), clock);

    public Task<DoctorReport> GetReportAsync(CancellationToken cancellationToken = default) =>
        Executor.RunAsync(cancellationToken: cancellationToken);
}
