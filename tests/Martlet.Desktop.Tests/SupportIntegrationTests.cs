using System.IO.Compression;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Desktop;
using Martlet.Diagnostics;
using Martlet.Sessions;
using Martlet.Support;

namespace Martlet.Desktop.Tests;

public sealed class SupportIntegrationTests
{
    private const string Canary = "PRIVATE-AUTHORED-TRANSCRIPT-KEY-CANARY";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ShownOwnerCloseRetiresOwnedSupportEvenWithoutChildClosing(bool blocked) => OnDispatcher(async () =>
    {
        using var scope = new Scope();
        using var release = new ManualResetEventSlim();
        var fs = new FaultFiles();
        var controller = await Ready(scope, new Backend(fs));
        var owner = new Window { ShowActivated = false, ShowInTaskbar = false };
        owner.Show();
        var window = new TroubleshootingWindow(controller) { Owner = owner, ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        TroubleshootingWindow? reopened = null;
        try
        {
            Click(window, "FreezeButton");
            await Until(() => Field<TabControl>(window, "PreviewTabs").Items.Count == 5);
            await Done(controller.StartRecording());
            if (blocked)
            {
                fs.WriteRelease = release;
                controller.Record([Event()]);
                await fs.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            var closingRaised = false;
            window.Closing += (_, _) => closingRaised = true;
            owner.Close();
            Assert.False(window.IsVisible);
            Assert.False(closingRaised); // Actual WPF owned-window closure bypasses Closing.
            Assert.False(controller.Recording);
            Assert.False(window.IsObserving);
            Assert.Empty(Field<TabControl>(window, "PreviewTabs").Items);
            Assert.Null(controller.Preview);
            if (blocked)
            {
                reopened = new(controller);
                Assert.True(controller.IsBusy);
                Assert.False(Field<Button>(reopened, "RecordButton").IsEnabled);
            }
            release.Set();
            await Until(() => !controller.HasResources);
        }
        finally
        {
            release.Set();
            reopened?.Close(); window.Close(); owner.Close();
            controller.CancelAndClose();
            await Until(() => !controller.HasResources);
        }
    });

    [Theory]
    [InlineData("SetupButton", "SetupTroubleshooting", false)]
    [InlineData("AudioSetupButton", "AudioTroubleshooting", false)]
    [InlineData("ConversationButton", "LiveTroubleshooting", false)]
    [InlineData("SetupButton", "SetupTroubleshooting", true)]
    [InlineData("AudioSetupButton", "AudioTroubleshooting", true)]
    [InlineData("ConversationButton", "LiveTroubleshooting", true)]
    public Task MainSupportCanBePresentedInsideEachShownModalWorkflow(string workflowButton, string supportButton, bool blocked) => OnDispatcher(async () =>
    {
        using var scope = new Scope();
        using var release = new ManualResetEventSlim();
        var fs = new FaultFiles();
        var controller = new SupportController(scope.Data, new Backend(fs));
        var main = new MainWindow(new SettingsStore(scope.Data), null, controller) { ShowActivated = false, ShowInTaskbar = false };
        main.Show();
        TroubleshootingWindow? original = null;
        Window? modal = null;
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await Until(() => Field<Button>(main, workflowButton).IsEnabled);
            ButtonById(main, "OpenTroubleshooting").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            original = main.OwnedWindows.OfType<TroubleshootingWindow>().Single();
            Click(original, "RecordButton");
            await Until(() => Field<TextBox>(original, "WorkText").Text.Contains("Recording: ON", StringComparison.Ordinal) &&
                !controller.IsBusy && Field<Button>(main, workflowButton).IsEnabled);
            if (blocked)
            {
                fs.WriteRelease = release;
                controller.Record([Event()]);
                await fs.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
            {
                try
                {
                    modal = main.OwnedWindows.Cast<Window>().Single(w => w is SetupWindow or AudioSetupWindow or LiveConversationWindow);
                    Assert.True(modal.IsVisible);
                    ButtonById(modal, supportButton).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var current = modal.OwnedWindows.OfType<TroubleshootingWindow>().SingleOrDefault() ??
                        main.OwnedWindows.OfType<TroubleshootingWindow>().Single();
                    Assert.True(current.IsEnabled);
                    Assert.True(IsWindowEnabled(new WindowInteropHelper(current).Handle));
                    Assert.NotSame(original, current);
                    Assert.False(original.IsObserving);
                    Assert.True(controller.Recording);
                    Assert.True(Field<Button>(current, "StopButton").IsEnabled);
                    if (blocked)
                    {
                        Assert.True(controller.IsBusy);
                        Assert.False(Field<Button>(current, "FreezeButton").IsEnabled);
                    }
                    Click(current, "StopButton");
                    Assert.False(controller.Recording);
                    if (blocked) Assert.True(controller.IsBusy);
                    release.Set();
                    await Until(() => Field<TextBox>(current, "WorkText").Text.Contains("Recording: OFF", StringComparison.Ordinal) &&
                        Field<Button>(current, "FreezeButton").IsEnabled);
                    Click(current, "FreezeButton");
                    await Until(() => Field<TabControl>(current, "PreviewTabs").Items.Count == 5);
                    Assert.True(modal.IsVisible);
                    current.Close();
                    observed.TrySetResult();
                }
                catch (Exception error) { observed.TrySetException(error); }
                finally { modal?.Close(); }
            });
            Click(main, workflowButton); // Enters the real WPF modal dispatcher frame.
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            release.Set();
            modal?.Close(); original?.Close();
            foreach (var child in main.OwnedWindows.Cast<Window>().ToArray()) child.Close();
            controller.CancelAndClose();
            await Until(() => !controller.HasResources);
            main.Close();
            await Until(() => !main.IsVisible);
        }
    });

    [Fact]
    public Task PassiveReportOnlyUiRequiresExactDefaultNoConfirmation() => OnDispatcher(async () =>
    {
        using var scope = new Scope();
        var controller = new SupportController(scope.Data);
        controller.ObserveReport(await new FoundationStatusService(new SettingsStore(scope.Data)).GetReportAsync());
        var approved = false;
        string? confirmation = null;
        var window = new TroubleshootingWindow(controller, confirm: text => { confirmation = text; return approved; });
        try
        {
            Assert.False(Directory.Exists(scope.Data));
            Assert.False(controller.Recording);
            Assert.False(controller.HasResources);
            Assert.False(Field<CheckBox>(window, "LogsChoice").IsChecked == true);
            Assert.False(Field<Button>(window, "ExportButton").IsEnabled);
            Click(window, "FreezeButton");
            await Until(() => controller.Preview is not null && Field<Button>(window, "ClearButton").IsEnabled);
            Assert.False(Directory.Exists(scope.Data));
            var preview = controller.Preview!;
            Assert.Equal(5, Field<TabControl>(window, "PreviewTabs").Items.Count);
            foreach (var info in preview.Files)
            {
                Assert.Contains(info.Name, Field<TextBox>(window, "InventoryText").Text);
                Assert.Contains(info.Sha256, Field<TextBox>(window, "InventoryText").Text);
            }
            Field<TextBox>(window, "DestinationText").Text = scope.Output;
            Click(window, "ExportButton");
            Assert.Contains("No export approved", Field<TextBox>(window, "ResultText").Text);
            Assert.False(File.Exists(scope.Output));
            Assert.Contains("Default is No", confirmation);
            Assert.Contains(preview.Id.ToString(), confirmation);
            Assert.Contains(preview.Digest, confirmation);
            Assert.Contains(scope.Output, confirmation);
            approved = true;
            Click(window, "ExportButton");
            await Until(() => File.Exists(scope.Output) && !controller.IsBusy);
            await Until(() => Field<TextBox>(window, "ResultText").Text.Contains("Exported locally", StringComparison.Ordinal));
            Assert.Contains("NOT sent", Field<TextBox>(window, "ResultText").Text);
            AssertArchive(preview, scope.Output);
            Assert.False(Directory.Exists(scope.Data));
        }
        finally { window.Close(); await Until(() => !controller.HasResources); }
        Assert.Empty(Field<TabControl>(window, "PreviewTabs").Items);
        Assert.Null(controller.Preview);
        Assert.False(new SupportController(scope.Data).Recording);
    });

    [Fact]
    public async Task ActualMalformedSettingsFixtureFailureAndFrozenJournalKeepCanariesOut()
    {
        using var scope = new Scope();
        Directory.CreateDirectory(scope.Data);
        var config = Path.Combine(scope.Data, "settings.json");
        await File.WriteAllTextAsync(config, "{ malformed " + Canary);
        var original = await File.ReadAllBytesAsync(config);
        var controller = new SupportController(scope.Data);
        var report = await new ProbeExecutor(ProbeRegistry.Local(new SettingsStore(scope.Data))).RunAsync(["settings.load"]);
        Assert.Equal(3, report.ExitCode);
        report = report with { Probes = [report.Probes[0] with
        {
            Summary = Canary, Remedy = Canary, Error = report.Probes[0].Error! with { Summary = Canary }
        }] };
        controller.ObserveReport(report);
        Assert.Null((await Done(controller.StartRecording())).Failure);
        controller.ObserveReport(report, record: true);
        await Until(() => !controller.IsBusy);
        await using var fixture = new FixtureSession();
        var failed = FixtureDiagnostics.Report(await fixture.RunAsync("failed"));
        controller.ObserveReport(failed, fixture: true, record: true);
        await Until(() => !controller.IsBusy);
        Assert.Null((await Done(controller.Freeze(true, true, Range()))).Failure);
        var preview = controller.Preview!;
        Assert.Contains("fixture.failed", string.Join("", preview.Contents));
        Assert.Contains("settings.malformed", string.Join("", preview.Contents));
        Assert.DoesNotContain(Canary, string.Join("", preview.Contents));
        controller.Record([Event()]);
        await Until(() => !controller.IsBusy);
        Assert.Same(preview, controller.Preview);
        Assert.Null((await Done(controller.Export(preview.Id, preview.Digest, scope.Output, true))).Failure);
        AssertArchive(preview, scope.Output);
        Assert.Equal(original, await File.ReadAllBytesAsync(config));
        controller.CancelAndClose();
        await Until(() => !controller.HasResources);
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("oversize")]
    [InlineData("locked")]
    public async Task SelectedBadJournalNeverFallsBackToAnEmptySuccess(string failure)
    {
        using var scope = new Scope();
        var controller = await Ready(scope);
        var path = controller.Location!;
        var journal = DiagnosticJournal.Start(path);
        journal.Append(Event());
        if (failure != "locked")
        {
            journal.Close();
            var segment = Directory.GetFiles(path, "*.jsonl").Single();
            if (failure == "corrupt") File.WriteAllText(segment, Canary);
            else { using var stream = new FileStream(segment, FileMode.Open, FileAccess.Write); stream.SetLength(1024 * 1024 + 1); }
        }
        try
        {
            var result = await Done(controller.Freeze(false, true, Range()));
            Assert.NotNull(result.Failure);
            Assert.Null(controller.Preview);
            Assert.False(File.Exists(scope.Output));
            Assert.DoesNotContain(Canary, controller.Status);
        }
        finally { journal.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExportIoFailureAndFailedCloseRemainOwnedUntilExplicitCleanup(bool failedClose)
    {
        using var scope = new Scope();
        var fs = new FaultFiles { FailWrite = !failedClose, FailClose = failedClose };
        var backend = new Backend(fs);
        var controller = await Ready(scope, backend);
        await Done(controller.Freeze(false, false, Range()));
        var preview = controller.Preview!;
        var sentinel = Path.Combine(scope.Root, "unrelated.keep");
        File.WriteAllText(sentinel, Canary);
        var result = await Done(controller.Export(preview.Id, preview.Digest, scope.Output, true));
        Assert.Equal(SupportFailure.IoFailure, result.Failure);
        Assert.False(File.Exists(scope.Output));
        Assert.Equal(failedClose, backend.Snapshot!.HasPendingCleanup);
        Assert.True(controller.NeedsCleanup);
        Assert.Null(controller.StartRecording());
        Assert.DoesNotContain(Canary, controller.Status);
        fs.FailWrite = fs.FailClose = false;
        Assert.Null((await Done(controller.RetryCleanup())).Failure);
        Assert.False(controller.HasResources);
        Assert.Empty(Directory.GetFiles(scope.Root, "*.partial"));
        Assert.Equal(Canary, File.ReadAllText(sentinel));
    }

    [Fact]
    public async Task WrongDigestUnapprovedAndExistingDestinationPreserveTargets()
    {
        using var scope = new Scope();
        var controller = await Ready(scope);
        foreach (var mode in new[] { "no", "digest", "exists" })
        {
            await Done(controller.Freeze(false, false, Range()));
            var preview = controller.Preview!;
            if (mode == "exists") File.WriteAllText(scope.Output, Canary);
            var result = await Done(controller.Export(preview.Id, mode == "digest" ? "wrong" : preview.Digest, scope.Output, mode != "no"));
            Assert.Equal(mode == "exists" ? SupportFailure.DestinationExists : SupportFailure.ConsentMismatch, result.Failure);
            if (mode == "exists") Assert.Equal(Canary, File.ReadAllText(scope.Output));
            else Assert.False(File.Exists(scope.Output));
            await Done(controller.RetryCleanup());
        }
    }

    [Fact]
    public async Task RecordingAccessFailureAndUnsupportedNetworkScopeNeverCreateAlternateStorage()
    {
        using var scope = new Scope();
        var fs = new FaultFiles { DenyWrite = true };
        var controller = await Ready(scope, new Backend(fs));
        Assert.Equal(SupportFailure.AccessDenied, (await Done(controller.StartRecording())).Failure);
        Assert.False(controller.Recording);
        Assert.False(controller.HasResources);
        Assert.DoesNotContain(Canary, controller.Status);
        var network = new SupportController(@"\\untrusted.invalid\share\Martlet");
        Assert.Equal(SupportFailure.InvalidPath, (await Done(network.StartRecording())).Failure);
        Assert.False(network.HasResources);
        Assert.Contains("local directory", network.Status);
    }

    [Fact]
    public async Task FaultedSnapshotDeleteKeepsExactPartialAndRequiresFreshPreviewAfterCleanup()
    {
        using var scope = new Scope();
        var fs = new FaultFiles { FailWrite = true, FailDelete = true };
        var backend = new Backend(fs);
        var controller = await Ready(scope, backend);
        await Done(controller.Freeze(false, false, Range()));
        var preview = controller.Preview!;
        Assert.Equal(SupportFailure.AccessDenied, (await Done(controller.Export(preview.Id, preview.Digest, scope.Output, true))).Failure);
        Assert.True(backend.Snapshot!.HasPendingCleanup);
        Assert.Single(Directory.GetFiles(scope.Root, "*.partial"));
        controller.CancelAndClose();
        await Until(() => !controller.IsBusy);
        Assert.True(controller.NeedsCleanup);
        Assert.True(controller.HasResources);
        Assert.Null(controller.StartRecording());
        fs.FailDelete = fs.FailWrite = false;
        await Done(controller.RetryCleanup());
        Assert.False(controller.HasResources);
        Assert.Null(controller.Preview);
        Assert.Empty(Directory.GetFiles(scope.Root, "*.partial"));
    }

    [Theory]
    [InlineData("destination")]
    [InlineData("source")]
    [InlineData("selection")]
    public Task DestinationAndSelectionChangesDuringConfirmationInvalidateApproval(string change) => OnDispatcher(async () =>
    {
        using var scope = new Scope();
        var controller = await Ready(scope);
        TroubleshootingWindow? window = null;
        window = new(controller, confirm: _ =>
        {
            if (change == "destination") Field<TextBox>(window!, "DestinationText").Text = Path.Combine(scope.Root, "changed.zip");
            else if (change == "selection") Field<ComboBox>(window!, "SourceChoice").SelectedIndex = 1;
            else controller.ObserveReport(controller.Report! with { CreatedAt = controller.Report!.CreatedAt.AddSeconds(1) });
            return true;
        });
        try
        {
            Click(window, "FreezeButton");
            await Until(() => Field<Button>(window, "ClearButton").IsEnabled);
            Field<TextBox>(window, "DestinationText").Text = scope.Output;
            Click(window, "ExportButton");
            Assert.Contains("changed during confirmation", Field<TextBox>(window, "ResultText").Text);
            Assert.False(File.Exists(scope.Output));
            Assert.False(File.Exists(Path.Combine(scope.Root, "changed.zip")));
            Field<CheckBox>(window, "LogsChoice").IsChecked = true;
            Assert.False(Field<Button>(window, "ExportButton").IsEnabled);
        }
        finally { window.Close(); await Until(() => !controller.HasResources); }
    });

    [Fact]
    public Task BlockedExportAndCancellationCallbacksKeepOwnerAcrossCloseAndReopen() => OnDispatcher(async () =>
    {
        using var scope = new Scope();
        using var io = new ManualResetEventSlim();
        using var callbacks = new ManualResetEventSlim();
        var fs = new FaultFiles { WriteRelease = io };
        var backend = new Backend(fs) { CancellationRelease = callbacks };
        var controller = await Ready(scope, backend);
        var window = new TroubleshootingWindow(controller, confirm: _ => true);
        TroubleshootingWindow? reopened = null;
        try
        {
            Click(window, "FreezeButton");
            await Until(() => Field<Button>(window, "ClearButton").IsEnabled);
            Field<TextBox>(window, "DestinationText").Text = scope.Output;
            Click(window, "ExportButton");
            await fs.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotEqual(Environment.CurrentManagedThreadId, fs.Thread);
            await Heartbeat();
            Assert.False(Field<Button>(window, "FreezeButton").IsEnabled);
            window.Close();
            await backend.CancelEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            reopened = new(controller);
            Assert.False(Field<Button>(reopened, "RecordButton").IsEnabled);
            Assert.True(controller.IsBusy);
            io.Set();
            await backend.ExportReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Heartbeat();
            Assert.True(controller.IsBusy);
            Assert.False(File.Exists(scope.Output));
            Assert.Empty(Field<TabControl>(window, "PreviewTabs").Items);
            callbacks.Set();
            await Until(() => !controller.HasResources);
            Assert.False(File.Exists(scope.Output));
            Assert.Empty(Directory.GetFiles(scope.Root, "*.partial"));
        }
        finally
        {
            io.Set(); callbacks.Set();
            reopened?.Close(); window.Close();
            await Until(() => !controller.HasResources);
        }
    });

    [Fact]
    public async Task CancellationInsideCommittedRenameRetainsRealLocalReceiptAfterObserverCloses()
    {
        using var scope = new Scope();
        var fs = new FaultFiles();
        var backend = new Backend(fs);
        var controller = await Ready(scope, backend);
        await Done(controller.Freeze(false, false, Range()));
        var preview = controller.Preview!;
        fs.BeforeMove = controller.CancelAndClose;
        var result = await Done(controller.Export(preview.Id, preview.Digest, scope.Output, true));
        await Until(() => !controller.HasResources);
        Assert.True(result.Exported);
        Assert.True(File.Exists(scope.Output));
        Assert.Contains("Exported locally to " + scope.Output, controller.Status);
        AssertArchive(preview, scope.Output);
    }

    [Fact]
    public Task ObservationTimeoutIsResponsiveAndCannotRetireNativeIoEarly() => OnDispatcher(async () =>
    {
        using var scope = new Scope();
        using var io = new ManualResetEventSlim();
        var fs = new FaultFiles { WriteRelease = io };
        var controller = await Ready(scope, new Backend(fs));
        var window = new TroubleshootingWindow(controller, confirm: _ => true, observationTimeout: TimeSpan.FromMilliseconds(80));
        try
        {
            Click(window, "FreezeButton");
            await Until(() => Field<Button>(window, "ClearButton").IsEnabled);
            Field<TextBox>(window, "DestinationText").Text = scope.Output;
            Click(window, "ExportButton");
            await fs.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Until(() => Field<TextBox>(window, "ResultText").Text.Contains("timed out", StringComparison.Ordinal));
            await Heartbeat();
            Assert.True(controller.IsBusy);
            Assert.Null(controller.StartRecording());
            io.Set();
            await Until(() => !controller.HasResources);
            Assert.False(File.Exists(scope.Output));
        }
        finally { io.Set(); window.Close(); await Until(() => !controller.HasResources); }
    });

    [Fact]
    public async Task OptionalLoggingHasNoQueueAndCannotOccupyConversationSetupSlot()
    {
        using var scope = new Scope();
        using var io = new ManualResetEventSlim();
        var fs = new FaultFiles();
        var controller = await Ready(scope, new Backend(fs));
        await Done(controller.StartRecording());
        fs.WriteRelease = io;
        controller.Record([Event()]);
        await fs.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            for (var i = 0; i < 100; i++) controller.Record([Event()]);
            Assert.Equal(100, controller.Dropped);
            var effects = new SetupOperationRunner();
            Assert.Equal(SetupWorkOutcome.Completed, (await effects.TryStart(_ =>
                Task.FromResult(new SetupWorkResult(SetupWorkOutcome.Completed)))!.Completion).Outcome);
            controller.CancelAndClose();
            Assert.True(controller.IsBusy);
        }
        finally { io.Set(); await Until(() => !controller.HasResources); }
    }

    [Fact]
    public Task ActualConversationUiProjectsMetadataWithoutInputResponseKeysOrDevices() => OnDispatcher(async () =>
    {
        using var scope = new Scope();
        await using var fixture = await LiveFixture.Create();
        fixture.Answer("PRIVATE-GENERATED-CANARY.");
        using var response = new HeldResponse(fixture);
        var controller = new SupportController(scope.Data);
        controller.ObserveReport(await new FoundationStatusService(fixture.Store).GetReportAsync());
        var window = new LiveConversationWindow(fixture.Settings, fixture.Runner, fixture.Controller, fixture.Events, clock: fixture.Clock)
            { Support = controller, ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        try
        {
            await Until(() => Field<TextBlock>(window, "ResultText").Text.Contains("Choices loaded", StringComparison.Ordinal));
            fixture.NoEffects();
            Field<TextBox>(window, "InputText").Text = Canary;
            Field<CheckBox>(window, "AcceptAction").IsChecked = true;
            Click(window, "SendButton");
            await response.WaitForGenerating(fixture, window);
            // Earlier states were actually sampled with recording OFF. The held response keeps
            // that state stable until the journal owner finishes starting; no earlier append exists.
            Assert.False(controller.Recording);
            Assert.False(controller.IsBusy);
            await Done(controller.StartRecording());
            response.Release.TrySetResult();
            await fixture.Finish();
            await Until(() => controller.LiveStatus.Contains("conversation.completed", StringComparison.Ordinal));
            await Until(() => !controller.IsBusy);
            Assert.Null((await Done(controller.Freeze(false, true, Range()))).Failure);
            var preview = controller.Preview!;
            var content = string.Join("\n", preview.Contents);
            Assert.Contains("conversation.completed", content);
            Assert.Contains("\"provenance\": \"fixture\"", content);
            foreach (var privateValue in new[] { Canary, "PRIVATE-GENERATED", LiveFixture.Secret, "private-input-id", "private-output-id", "api.openai.com" })
                Assert.DoesNotContain(privateValue, content);
            Assert.Equal(1, fixture.Llm.Calls);
            Assert.Equal(0, fixture.Stt.Calls);
            Assert.Equal(0, fixture.Tts.Calls);
            Assert.Equal(0, fixture.Output.Opens);
            Assert.Equal(0, fixture.Capture.Opens);
            Assert.Null((await Done(controller.Export(preview.Id, preview.Digest, scope.Output, true))).Failure);
            AssertArchive(preview, scope.Output);
        }
        finally { window.Close(); controller.CancelAndClose(); await Until(() => !controller.HasResources); }
    });

    [Fact]
    public Task BusyTerminalObservationIsCountedDroppedAndNeverInventedInArchive() => OnDispatcher(async () =>
    {
        using var scope = new Scope();
        await using var fixture = await LiveFixture.Create();
        fixture.Answer("PRIVATE-GENERATED-CANARY.");
        using var response = new HeldResponse(fixture);
        using var backend = new HeldAppend();
        var controller = new SupportController(scope.Data, backend);
        controller.ObserveReport(await new FoundationStatusService(fixture.Store).GetReportAsync());
        await Done(controller.StartRecording());
        var window = new LiveConversationWindow(fixture.Settings, fixture.Runner, fixture.Controller, fixture.Events, clock: fixture.Clock)
            { Support = controller, ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        try
        {
            await Until(() => Field<TextBlock>(window, "ResultText").Text.Contains("Choices loaded", StringComparison.Ordinal));
            fixture.NoEffects();
            Field<TextBox>(window, "InputText").Text = Canary;
            Field<CheckBox>(window, "AcceptAction").IsChecked = true;
            Click(window, "SendButton");
            await response.WaitForGenerating(fixture, window);
            await backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(controller.IsBusy);
            Assert.Empty(backend.Completed);
            var earlierDrops = controller.Dropped;
            response.Release.TrySetResult();
            await fixture.Finish();
            await Until(() => controller.LiveStatus.Contains("conversation.completed", StringComparison.Ordinal));
            Assert.False(fixture.Runner.IsRunning); // Optional journal IO cannot block the live action.
            Assert.True(controller.IsBusy);
            Assert.True(controller.Dropped > earlierDrops);
            Assert.Contains($"Dropped (busy/transition bound): {controller.Dropped}", controller.Status);
            Assert.Single(backend.Offered);
            Assert.Empty(backend.Completed);
            Assert.DoesNotContain("conversation.completed", backend.Offered);
            backend.Release.Set();
            await Until(() => !controller.IsBusy);
            Assert.Single(backend.Completed);
            Assert.Null((await Done(controller.Freeze(false, true, Range()))).Failure);
            var content = string.Join("\n", controller.Preview!.Contents);
            Assert.Contains("conversation.running", content);
            Assert.DoesNotContain("conversation.completed", content);
            foreach (var privateValue in new[] { Canary, "PRIVATE-GENERATED", LiveFixture.Secret, "private-input-id", "private-output-id", "api.openai.com" })
                Assert.DoesNotContain(privateValue, content);
            Assert.Equal(1, fixture.Llm.Calls);
            Assert.Equal(0, fixture.Stt.Calls);
            Assert.Equal(0, fixture.Tts.Calls);
            Assert.Equal(0, fixture.Output.Opens);
            Assert.Equal(0, fixture.Capture.Opens);
        }
        finally
        {
            backend.Release.Set(); response.Release.TrySetResult(); window.Close();
            controller.CancelAndClose();
            await Until(() => !controller.HasResources);
        }
    });

    private sealed class HeldResponse : IDisposable
    {
        private TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal HeldResponse(LiveFixture fixture)
        {
            var respond = fixture.Llm.Respond;
            fixture.Llm.Respond = async (request, token) =>
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(token);
                return await respond(request, token);
            };
        }
        internal async Task WaitForGenerating(LiveFixture fixture, Window window)
        {
            await Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Until(() =>
            {
                // Advance the existing controlled runtime observer clock, as LiveFixture.Finish does.
                fixture.Clock.Advance(TimeSpan.FromMilliseconds(5));
                return Field<TextBox>(window, "StatusText").Text.Contains("runtime.Generating", StringComparison.Ordinal);
            });
        }
        public void Dispose() => Release.TrySetResult();
    }

    private sealed class HeldAppend : SupportBackend, IDisposable
    {
        internal ManualResetEventSlim Release { get; } = new();
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ConcurrentQueue<string> Offered { get; } = new();
        internal ConcurrentQueue<string> Completed { get; } = new();
        private int count;
        internal override void Append(DiagnosticJournal journal, DiagnosticEvent value, CancellationToken token)
        {
            Offered.Enqueue(value.Code);
            if (Interlocked.Increment(ref count) == 1)
            {
                Entered.TrySetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The controlled journal append was not released.");
            }
            base.Append(journal, value, token);
            Completed.Enqueue(value.Code);
        }
        public void Dispose() => Release.Dispose();
    }

    private static async Task<SupportController> Ready(Scope scope, SupportBackend? backend = null)
    {
        var controller = new SupportController(scope.Data, backend);
        controller.ObserveReport(await new ProbeExecutor(ProbeRegistry.Local(new SettingsStore(scope.Data))).RunAsync());
        return controller;
    }
    private static LogRange Range() => new(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    private static DiagnosticEvent Event() => new()
    {
        SchemaVersion = 1, TimestampUtc = DateTimeOffset.UtcNow, Severity = DiagnosticSeverity.Information,
        Component = SupportComponent.Conversation, Stage = Stage.Generation, Code = "conversation.completed",
        ActionId = "conversation.review", Provenance = EvidenceProvenance.Fixture, Freshness = EvidenceFreshness.Current,
        Adapter = AdapterAlias.Fixture, TraceId = Guid.NewGuid(), TurnId = Guid.NewGuid(), State = MetadataState.Completed
    };
    private static async Task<SupportResult> Done(SupportWork? work) => await Assert.IsType<SupportWork>(work).Completion.WaitAsync(TimeSpan.FromSeconds(10));
    private static void AssertArchive(SupportPreview preview, string path)
    {
        using var zip = ZipFile.OpenRead(path);
        Assert.Equal(preview.Files.Select(f => f.Name), zip.Entries.Select(f => f.FullName));
        for (var i = 0; i < preview.Files.Count; i++)
        {
            using var stream = zip.Entries[i].Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            var bytes = copy.ToArray();
            Assert.Equal(Encoding.UTF8.GetBytes(preview.Contents[i]), bytes);
            Assert.Equal(preview.Files[i].Bytes, bytes.Length);
            Assert.Equal(preview.Files[i].Sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            Assert.DoesNotContain(Canary, Encoding.UTF8.GetString(bytes));
        }
    }
    private static T Field<T>(Window window, string name) where T : FrameworkElement => Assert.IsType<T>(window.FindName(name));
    private static Button ButtonById(DependencyObject root, string id)
    {
        if (root is Button button && AutomationProperties.GetAutomationId(button) == id) return button;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            var found = Find(child);
            if (found is not null) return found;
        }
        throw new InvalidOperationException("Required authored button missing: " + id);
        Button? Find(DependencyObject current)
        {
            if (current is Button candidate && AutomationProperties.GetAutomationId(candidate) == id) return candidate;
            foreach (var child in LogicalTreeHelper.GetChildren(current).OfType<DependencyObject>())
                if (Find(child) is { } found) return found;
            return null;
        }
    }
    private static void Click(Window window, string name) => Field<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(condition());
    }
    private static async Task Heartbeat() => await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(nint window);
    private static async Task OnDispatcher(Func<Task> action)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.UnhandledException += (_, args) => { args.Handled = true; finished.TrySetException(args.Exception); dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); };
            _ = dispatcher.BeginInvoke(async () =>
            {
                try { await action(); finished.TrySetResult(); }
                catch (Exception error) { finished.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }
    private sealed class Scope : IDisposable
    {
        internal string Root { get; } = Path.Combine(AppContext.BaseDirectory, "support-integration", Guid.NewGuid().ToString("N"));
        internal string Data => Path.Combine(Root, "app-data");
        internal string Output => Path.Combine(Root, "reviewed.zip");
        internal Scope() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, true);
    }
    private sealed class Backend(FaultFiles files) : SupportBackend
    {
        internal SupportSnapshot? Snapshot;
        internal ManualResetEventSlim? CancellationRelease;
        internal TaskCompletionSource CancelEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ExportReturned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal override DiagnosticJournal Start(string path, CancellationToken token) =>
            DiagnosticJournal.Start(path, new(), TimeProvider.System, files, token);
        internal override SupportSnapshot Freeze(SettingsSummary settings, DoctorReport report, JournalSelection logs, CancellationToken token) =>
            Snapshot = base.Freeze(settings, report, logs, token);
        internal override ExportReceipt Export(SupportSnapshot snapshot, ExportConsent consent, string destination, CancellationToken token)
        {
            // Registration remains owned by the operation token until the controller retires callbacks.
            if (CancellationRelease is { } release)
                token.Register(() => { CancelEntered.TrySetResult(); release.Wait(TimeSpan.FromSeconds(20)); });
            try { return snapshot.Export(consent, destination, files, token); }
            finally { ExportReturned.TrySetResult(); }
        }
    }
    private sealed class FaultFiles : SupportFileSystem
    {
        internal bool FailWrite, FailClose, DenyWrite, FailDelete;
        internal ManualResetEventSlim? WriteRelease;
        internal Action? BeforeMove;
        internal int Thread;
        internal TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal override void Write(Stream stream, ReadOnlySpan<byte> bytes)
        {
            if (WriteRelease is { } release)
            {
                Thread = Environment.CurrentManagedThreadId;
                Entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(20))) throw new IOException("test IO deadline");
            }
            if (DenyWrite) throw new UnauthorizedAccessException(Canary);
            if (FailWrite) throw new IOException(Canary);
            base.Write(stream, bytes);
        }
        internal override void Close(Stream stream)
        {
            if (FailClose) throw new IOException(Canary);
            base.Close(stream);
        }
        internal override void Delete(string path)
        {
            if (FailDelete) throw new UnauthorizedAccessException(Canary);
            base.Delete(path);
        }
        internal override void Move(string source, string destination, bool overwrite)
        {
            BeforeMove?.Invoke();
            base.Move(source, destination, overwrite);
        }
    }
}
