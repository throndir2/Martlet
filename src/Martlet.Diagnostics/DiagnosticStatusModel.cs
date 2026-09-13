using System.ComponentModel;
using System.Runtime.CompilerServices;
using Martlet.Core.Settings;

namespace Martlet.Diagnostics;

public sealed record PipelineStatus(string Label, ProbeResult? Probe)
{
    public string Description => Probe is { } probe
        ? $"{Label}: {probe.Outcome}; {probe.Provenance}; {probe.Freshness}; age {ReportFormatter.Milliseconds(probe.AgeMilliseconds)}. {probe.Summary} Next: {probe.ActionId}."
        : $"{Label}: Unknown; NotRun. No observation available.";
}

// UI-independent presentation model, called on the owner's UI context. No devices or provider adapters.
public sealed class DiagnosticStatusModel : INotifyPropertyChanged
{
    private CancellationTokenSource? cancellation;
    private bool closed;
    private long generation;
    public ProbeExecutor Executor { get; }
    public DoctorReport Report { get; private set; }
    public bool IsRunning { get; private set; }
    public bool CanRefresh => !closed && !IsRunning && Executor.ActiveOperationCount == 0;
    public bool CanCreateProfile => CanRefresh && Report.SettingsState == SettingsLoadState.FirstRun;
    public string Text => ReportFormatter.Human(Report);
    public string Activity => IsRunning ? "Running local diagnostics. Stop is available."
        : Executor.ActiveOperationCount != 0 ? "A check is still active after cancellation. Close Martlet if it does not finish; refresh is blocked."
        : "Diagnostics idle. No capture, playback, network or provider work is running.";
    public IReadOnlyList<PipelineStatus> Pipeline =>
    [
        Node("Mic", "audio.input"), Node("VAD", "pipeline.vad"), Node("STT", "pipeline.stt"),
        Node("Policy", "pipeline.policy"), Node("LLM", "pipeline.llm"), Node("TTS", "pipeline.tts"),
        Node("Playback", "audio.playback"), Node("Optional hosts / GPU", "host.connection")
    ];

    public DiagnosticStatusModel(ProbeExecutor executor)
    {
        Executor = executor;
        Report = executor.Catalog();
    }

    public async Task<bool> RefreshAsync()
    {
        if (!CanRefresh)
            return false;
        var current = ++generation;
        using var source = new CancellationTokenSource();
        cancellation = source;
        IsRunning = true;
        Report = Executor.Pending();
        Changed();
        try
        {
            var report = await Executor.RunAsync(cancellationToken: source.Token);
            if (!closed && generation == current)
                Report = report;
        }
        finally
        {
            if (generation == current)
            {
                cancellation = null;
                IsRunning = false;
                Changed();
            }
        }
        return true;
    }

    public void Stop()
    {
        // This token only signals the executor, never runs adapter cancellation handlers on the UI thread.
        cancellation?.Cancel();
    }

    public void UpdateAge()
    {
        if (closed || IsRunning)
            return;
        Report = Executor.RefreshAge(Report);
        Changed();
    }

    public async Task<bool> CloseAsync()
    {
        closed = true;
        Stop();
        ++generation;
        cancellation = null;
        return await Executor.WaitForIdleAsync(TimeSpan.FromMilliseconds(250));
    }

    private PipelineStatus Node(string label, string id) => new(label, Report.Probes.FirstOrDefault(probe => probe.Id == id));
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}
