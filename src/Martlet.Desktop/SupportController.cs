using System.IO;
using System.Security.Cryptography;
using System.Text;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Diagnostics;
using Martlet.Support;

namespace Martlet.Desktop;

internal sealed record SupportPreview(Guid Id, string Digest, DateTimeOffset FrozenAt, LogRange Range,
    IReadOnlyList<PreviewFile> Files, IReadOnlyList<string> Contents, string Scope);
internal sealed record SupportResult(string Message, SupportFailure? Failure = null, bool Exported = false);

// The only replaceable boundary is for controlled tests. Production delegates to the existing engine.
internal class SupportBackend
{
    internal virtual DiagnosticJournal Start(string path, CancellationToken token) =>
        DiagnosticJournal.Start(path, cancellationToken: token);
    internal virtual void Append(DiagnosticJournal journal, DiagnosticEvent value, CancellationToken token) =>
        journal.Append(value, token);
    internal virtual JournalSelection Select(DiagnosticJournal? journal, string path, LogRange range, CancellationToken token) =>
        journal is null ? DiagnosticJournal.ReadClosed(path, range, cancellationToken: token) : journal.Select(range, token);
    internal virtual SupportSnapshot Freeze(SettingsSummary settings, DoctorReport report, JournalSelection logs, CancellationToken token) =>
        SupportSnapshot.Freeze(settings, report, Build(), logs, cancellationToken: token);
    internal virtual ExportReceipt Export(SupportSnapshot snapshot, ExportConsent consent, string destination, CancellationToken token) =>
        snapshot.Export(consent, destination, token);

    private static BuildMetadata Build()
    {
        // One fixed installed manifest, never an inventory walk. This read occurs only on explicit Freeze.
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "manifest.json"));
        if (!string.Equals(new DirectoryInfo(AppContext.BaseDirectory).Name, "Desktop", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(path))
            return BuildMetadata.FromExecutingAssemblies();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > ContractRules.MaxJsonBytes) throw new SupportException(SupportFailure.LimitExceeded);
        var bytes = new byte[ContractRules.MaxJsonBytes + 1];
        try
        {
            var count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            return BuildMetadata.FromPayloadManifest(bytes.AsMemory(0, count));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}

internal sealed class SupportWork
{
    private readonly object gate = new();
    private readonly CancellationTokenSource stop = new();
    private Task callbacks = Task.CompletedTask;
    private bool retiring;
    internal Task<SupportResult> Completion { get; private set; } = null!;
    internal void Start(Func<CancellationToken, SupportResult> action, Action<SupportResult> release)
    {
        var task = Task.Run(() => action(stop.Token));
        Completion = task.ContinueWith(async completed =>
        {
            Task cancellation;
            lock (gate) { retiring = true; cancellation = callbacks; }
            await Task.WhenAny(cancellation).ConfigureAwait(false);
            _ = completed.Exception;
            _ = cancellation.Exception;
            var result = completed.IsCompletedSuccessfully ? completed.Result :
                new SupportResult("Support boundary failed. No successful operation is claimed; retry owned cleanup.",
                    SupportFailure.IoFailure);
            if (cancellation.IsFaulted)
                result = result with { Message = result.Message + "\nCancellation callbacks failed; retry owned cleanup. " +
                    "An already committed local export is not rolled back.", Failure = SupportFailure.IoFailure };
            stop.Dispose();
            release(result);
            return result;
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }
    internal void Cancel()
    {
        lock (gate)
            if (!retiring && !stop.IsCancellationRequested) callbacks = stop.CancelAsync();
    }
}

// One app-lifetime owner, separate from effectful setup/conversation. No queue and no startup IO.
internal sealed class SupportController
{
    private readonly object gate = new();
    private readonly SupportBackend backend;
    private SupportWork? active;
    private DiagnosticJournal? journal;
    private SupportSnapshot? snapshot;
    private SupportPreview? preview;
    private bool recording, retire, cleanupPending;
    private long dropped, omitted;
    private long revision;
    private DoctorReport? report, fixtureReport;
    private string message = "Recording OFF. No journal opened; no support files read.";
    private string liveStatus = "Conversation: NOT RUN / no app observation. No provider or device readiness implied.";
    private string lastExport = "";
    internal string? Location { get; }
    internal bool IsBusy { get { lock (gate) return active is not null; } }
    internal bool Recording { get { lock (gate) return recording; } }
    internal bool HasResources { get { lock (gate) return active is not null || journal is not null || snapshot is not null; } }
    internal bool NeedsCleanup { get { lock (gate) return cleanupPending; } }
    internal long Dropped => Interlocked.Read(ref dropped);
    internal long Omitted => Interlocked.Read(ref omitted);
    internal long Revision => Interlocked.Read(ref revision);
    internal void Drop() => Interlocked.Increment(ref dropped);
    internal string LiveStatus { get { lock (gate) return liveStatus; } }
    internal void ObserveConversation(DiagnosticEvent value)
    {
        var finding = DiagnosticCatalog.Finding(value.Code);
        lock (gate)
        {
            liveStatus = $"Last conversation observation: {value.TimestampUtc:O}; {value.Stage}; {value.State}; " +
                $"{value.Provenance}; freshness at observation: {value.Freshness}. This is historical stage evidence, not current readiness.\n" +
                $"{value.Code}: {finding.Summary}\n{value.ActionId}: {DiagnosticCatalog.Remedy(value.ActionId).Guidance}";
            Interlocked.Increment(ref revision);
        }
    }
    internal SupportPreview? Preview { get { lock (gate) return preview; } }
    internal DoctorReport? Report { get { lock (gate) return report; } }
    internal DoctorReport? FixtureReport { get { lock (gate) return fixtureReport; } }
    internal string Status { get { lock (gate) return $"{message}\nRecording: {(recording ? "ON (local metadata only)" : "OFF")}; " +
        $"worker: {(active is null ? "idle" : "IO/cancellation still owned")}; cleanup pending: {cleanupPending}.\n" +
        $"Dropped (busy/transition bound): {Dropped}; omitted (unsupported metadata): {Omitted}. No raw fallback or automatic retry.\n{lastExport}"; } }

    internal SupportController(string? dataDirectory, SupportBackend? backend = null)
    {
        Location = dataDirectory is null ? null : SupportHelp.Location(dataDirectory);
        this.backend = backend ?? new();
    }

    internal void ObserveReport(DoctorReport value, bool fixture = false, bool record = false)
    {
        // Never retain the fixture's scripted text, refusal or trace in the support owner.
        var metadata = value with { Fixture = null, Probes = value.Probes.ToArray() };
        lock (gate)
        {
            if ((fixture ? fixtureReport : report)?.CreatedAt != metadata.CreatedAt) Interlocked.Increment(ref revision);
            if (fixture) fixtureReport = metadata; else report = metadata;
        }
        if (!record || !Recording) return;
        var events = new List<DiagnosticEvent>();
        foreach (var probe in metadata.Probes)
        {
            try { events.Add(DiagnosticEvent.FromProbe(probe, DateTimeOffset.UtcNow, probe.Error?.TraceId)); }
            catch (SupportException) { Omit(); }
        }
        Record(events);
    }

    internal void Omit()
    {
        Interlocked.Increment(ref omitted);
        lock (gate) message = "Unsupported/stale metadata omitted. No raw content was recorded; inspect the shared stage status.";
    }

    internal void Record(IReadOnlyList<DiagnosticEvent> events)
    {
        if (events.Count == 0 || !Recording) return;
        if (events.Count > 64) { Omit(); return; }
        var copy = events.ToArray();
        if (Begin(token =>
        {
            var acknowledged = 0;
            try
            {
                foreach (var value in copy) { backend.Append(journal!, value, token); acknowledged++; }
                Interlocked.Increment(ref revision);
                return new($"Recorded {acknowledged} metadata observations. No conversation content.");
            }
            finally
            {
                if (acknowledged != copy.Length) Interlocked.Add(ref dropped, copy.Length - acknowledged);
            }
        }, requireRecording: true) is null)
            Interlocked.Add(ref dropped, copy.Length);
    }

    internal SupportWork? StartRecording() => Begin(token =>
    {
        if (Location is null) throw new SupportException(SupportFailure.InvalidPath);
        if (journal is not null) throw new SupportException(SupportFailure.Closed);
        var owned = backend.Start(Location, token);
        lock (gate) { journal = owned; recording = !retire; Interlocked.Increment(ref revision); }
        return new($"Local recording started. Recovery: {owned.Recovery.TruncatedTailBytes} truncated tail bytes; " +
            $"{owned.Recovery.AbandonedReservations} abandoned reservations. No earlier collection is implied.");
    });

    internal SupportWork? StopRecording()
    {
        lock (gate)
        {
            recording = false;
            if (active is not null) { CancelAndClose(); return active; }
        }
        return Begin(_ =>
        {
            journal?.Close();
            lock (gate) journal = null;
            return new("Recording stopped; the journal owner closed. Existing metadata remains under the stated retention policy.");
        });
    }

    internal SupportWork? Freeze(bool fixture, bool includeLogs, LogRange range)
    {
        DoctorReport? selected, settings;
        lock (gate) { selected = fixture ? fixtureReport : report; settings = report; }
        return Begin(token =>
        {
            if (selected is null || settings?.SettingsState is not { } state)
                throw new SupportException(SupportFailure.InvalidData);
            ClearSnapshot();
            var logs = includeLogs
                ? backend.Select(journal, Location ?? throw new SupportException(SupportFailure.InvalidPath), range, token)
                : JournalSelection.Empty(range);
            var frozen = backend.Freeze(SettingsSummary.FromStatus(state, settings.ProfileKind, settings.Setup), selected, logs, token);
            lock (gate) snapshot = frozen;
            var contents = new List<string>();
            foreach (var file in frozen.Files)
            {
                var bytes = frozen.Preview(file.Name);
                try { contents.Add(Encoding.UTF8.GetString(bytes)); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            lock (gate) preview = new(frozen.Id, frozen.Digest, frozen.FrozenAtUtc, frozen.SelectedRange, frozen.Files,
                contents.AsReadOnly(), (fixture ? "Last shared FIXTURE report (NOT inference)" : "Shared local Doctor report") +
                (includeLogs ? "; selected journal receipt range (mixed provenance retained)" : "; NO log records selected/collected for this snapshot"));
            return new("Exact snapshot frozen. Review ALL five files. No export or approval has occurred.");
        });
    }

    internal SupportWork? Export(Guid id, string digest, string destination, bool explicitlyApproved) => Begin(token =>
    {
        if (!explicitlyApproved || snapshot is null || preview is null)
            throw new SupportException(SupportFailure.ConsentMismatch);
        // The original owned token reaches both irreversible engine boundaries, not a linked substitute.
        var consent = snapshot.Approve(id, digest, destination, token);
        var receipt = backend.Export(snapshot, consent, destination, token);
        return new($"Exported locally to {Path.GetFullPath(destination)}\nSnapshot {receipt.SnapshotId}; SHA-256 {receipt.Digest}; " +
            $"{receipt.ZipBytes} archive bytes. NOT sent or received by a maintainer. No support contact/upload channel is configured.", Exported: true);
    });

    internal SupportWork? ClosePreview() => Begin(_ => { ClearSnapshot(); return new("Preview cleared. No export approval retained."); });
    internal void CancelAndClose()
    {
        SupportWork? work;
        lock (gate)
        {
            recording = false;
            retire = true;
            preview = null;
            work = active;
            message = "Cancellation/close requested. IO and callbacks must finish; no cleanup or rollback is assumed.";
            if (work is null) StartRetirement();
        }
        work?.Cancel();
    }
    internal SupportWork? RetryCleanup() => Begin(_ =>
    {
        CloseResources();
        return new("Owned cleanup finished. Recording remains OFF; explicitly start a new action.");
    }, cleanup: true);

    private void ClearSnapshot()
    {
        snapshot?.Dispose();
        lock (gate) { snapshot = null; preview = null; cleanupPending = false; }
    }
    private void CloseResources()
    {
        ClearSnapshot();
        journal?.Close();
        lock (gate) { journal = null; recording = false; cleanupPending = false; }
    }
    private void StartRetirement()
    {
        retire = false;
        _ = Begin(_ => { CloseResources(); return new("Troubleshooting closed; owned IO and cleanup finished. Recording OFF."); }, cleanup: true);
    }
    private SupportWork? Begin(Func<CancellationToken, SupportResult> action, bool requireRecording = false, bool cleanup = false)
    {
        lock (gate)
        {
            if (active is not null || cleanupPending && !cleanup || requireRecording && !recording)
            {
                if (!requireRecording) message = "Support is busy or awaiting cleanup. Nothing queued. Wait or retry owned cleanup.";
                return null;
            }
            var work = new SupportWork();
            active = work;
            work.Start(token =>
            {
                try { return action(token); }
                catch (SupportException error) { return new(error.Message, error.Failure); }
                catch (OperationCanceledException) { return new(new SupportException(SupportFailure.Canceled).Message, SupportFailure.Canceled); }
                catch (UnauthorizedAccessException) { return new(new SupportException(SupportFailure.AccessDenied).Message, SupportFailure.AccessDenied); }
                catch (IOException) { return new(new SupportException(SupportFailure.IoFailure).Message, SupportFailure.IoFailure); }
                finally
                {
                    lock (gate) cleanupPending = snapshot?.HasPendingCleanup == true;
                }
            }, result =>
            {
                lock (gate)
                {
                    message = result.Message;
                    if (result.Exported) lastExport = result.Message;
                    if (result.Failure is not null)
                    {
                        recording = false;
                        cleanupPending = journal is not null || snapshot is not null;
                    }
                    active = null;
                    if (retire) StartRetirement();
                }
            });
            return work;
        }
    }
}
