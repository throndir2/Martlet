using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using System.Windows.Threading;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

public partial class SetupWindow : Window
{
    internal Action<Window>? Troubleshooting { get; init; }
    internal Action<Window>? ConfigurationRecovery { get; init; }
    private void Troubleshooting_Click(object sender, RoutedEventArgs e) => Troubleshooting?.Invoke(this);
    private void Recovery_Click(object sender, RoutedEventArgs e)
    {
        ConfigurationRecovery?.Invoke(this);
        if (ConfigurationRecovery is null) return;
        needsReload = true;
        draft = null;
        revision = null;
        KeyInput.Clear();
        ResultText.Text = "Recovery closed. Reload the saved checkpoint before editing; no unsaved setup draft can authorize a restore or subsequent key action.";
        RenderStatus();
        RenderOperationState();
    }
    private readonly ISetupService service;
    private readonly SetupOperationRunner operations;
    private readonly Func<string, bool>? confirm;
    private readonly TimeProvider clock;
    private readonly TimeSpan observationTimeout;
    private readonly DispatcherTimer operationTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private CancellationTokenSource? observationStop;
    private long generation;
    private bool closed;
    private bool needsReload = true;
    private AppSettings? draft;
    private string? revision;
    private bool rendering;
    private bool busy;
    private bool routeDirty;
    private SetupRole Role => RoleChoice.SelectedItem is SetupRole role ? role : SetupRole.Stt;

    public SetupWindow(ISetupService service, SetupOperationRunner operations,
        Func<string, bool>? confirm = null, TimeProvider? clock = null, TimeSpan? observationTimeout = null)
    {
        this.service = service;
        this.operations = operations;
        this.confirm = confirm;
        this.clock = clock ?? TimeProvider.System;
        this.observationTimeout = observationTimeout ?? TimeSpan.FromSeconds(5);
        if (this.observationTimeout <= TimeSpan.Zero || this.observationTimeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(observationTimeout));
        InitializeComponent();
        rendering = true;
        RoleChoice.ItemsSource = Enum.GetValues<SetupRole>();
        RoleChoice.SelectedIndex = 0;
        DisclosureText.Text = OpenAiSetup.Disclosure;
        rendering = false;
        operationTimer.Tick += (_, _) => RenderOperationState();
        operationTimer.Start();
        RenderOperationState();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        if (!MayStart()) return;
        KeyInput.Clear();
        var backend = service;
        var operation = operations.TryStart(LoadWork(backend));
        await ObserveAsync(operation, result =>
        {
            var loaded = result.Loaded!;
            revision = loaded.Revision;
            draft = loaded.Error is null ? SetupSettings.Begin(loaded.Settings) : null;
            needsReload = loaded.Error is not null;
            ResultText.Text = loaded.Error?.Summary ?? (loaded.Settings?.SchemaVersion == 1
                ? "Version 1 loaded unchanged. Explicit Save migrates it and atomically snapshots the original; profile ID and legacy references are preserved."
                : "Checkpoint loaded. No secret lookup, device or network action was performed.");
            if (draft is not null)
            {
                rendering = true;
                FixtureChoice.IsChecked = draft.Profile.Kind is ProfileKind.Fixture or ProfileKind.NotConfigured;
                ApiChoice.IsChecked = draft.Profile.Kind == ProfileKind.Api;
                Steps.SelectedIndex = (int)draft.Setup!.Checkpoint;
                rendering = false;
                RenderRole();
            }
            RenderStatus();
        });
    }

    private bool MayStart()
    {
        if (closed) return false;
        if (!busy && !operations.IsRunning) return true;
        ResultText.Text = "An earlier setup action still owns its worker. No overlapping action was started. Cancel or close observation; reload only after it releases.";
        return false;
    }

    private void RenderOperationState()
    {
        if (closed) return;
        var active = operations.IsRunning;
        EditorPanel.IsEnabled = !busy && !active && !needsReload && draft is not null;
        ReloadButton.IsEnabled = !busy && !active;
        CancelButton.IsEnabled = active;
        OperationActivity.Text = active
            ? "Setup worker still active. Native or filesystem work may not stop immediately. Cancel and Close remain available; timeout is NOT rollback or completion. Other setup actions stay blocked."
            : needsReload
                ? "No setup worker active. Reload the saved checkpoint and review pending key recovery before editing."
                : "No setup worker active. Saved configuration is not voice readiness or permission for paid requests.";
    }

    private async Task<bool> ObserveAsync(SetupOperation? operation, Action<SetupWorkResult> apply)
    {
        if (operation is null) { MayStart(); return false; }
        var current = ++generation;
        using var stop = new CancellationTokenSource();
        observationStop = stop;
        busy = true;
        RenderOperationState();
        var timeout = Task.Delay(observationTimeout, clock, stop.Token);
        try
        {
            var finished = await Task.WhenAny(operation.Completion, timeout);
            if (closed || current != generation) return false;
            if (stop.IsCancellationRequested || finished != operation.Completion)
            {
                operation.RequestCancellation();
                needsReload = true;
                ResultText.Text = stop.IsCancellationRequested
                    ? "Cancellation requested; observation stopped. Native work may still be active, not rolled back. Close is available. After the worker releases, reload and review saved settings and pending key removals."
                    : "Setup observation timed out; cancellation requested. Native work may still be active, not rolled back. Close is available. After the worker releases, reload and review saved settings and pending key removals.";
                return false;
            }
            var result = await operation.Completion;
            if (closed || current != generation) return false;
            if (result.Outcome != SetupWorkOutcome.Completed)
            {
                needsReload = true;
                ResultText.Text = result.Outcome == SetupWorkOutcome.Canceled
                    ? "Setup action canceled. Reload and review saved settings and pending key removals; cancellation is not proof of rollback."
                    : "Setup action failed. Reload and review the role configuration and pending key removals; do not assume rollback. Raw exception details and secrets are not shown.";
                return false;
            }
            apply(result);
            return true;
        }
        finally
        {
            stop.Cancel();
            if (ReferenceEquals(observationStop, stop)) observationStop = null;
            busy = false;
            if (!closed) RenderOperationState();
        }
    }

    private void RenderStatus()
    {
        SetupStatus.Text = draft is null ? "Setup blocked. Original settings preserved; see the remedy below." : SetupSettings.Describe(draft);
        CredentialScope.Text = $"Selected role: {Role}; internal alias: {OpenAiSetup.Alias(Role)}; origin: {OpenAiSetup.Origin}. Select another role on Destinations.";
        RemovalChoice.ItemsSource = draft?.Setup?.PendingRemovals;
        RemovalChoice.SelectedIndex = 0;
    }

    private void RenderRole()
    {
        rendering = true;
        var route = draft?.Setup?.Routes.SingleOrDefault(r => r.Role == Role);
        BoundaryText.Text = $"{OpenAiSetup.Boundary(Role)} Internal alias: {OpenAiSetup.Alias(Role)}. Destination: {OpenAiSetup.Origin}.";
        ModelId.Text = route?.ModelId ?? "";
        VoiceId.Text = route?.VoiceId ?? "";
        VoiceId.IsEnabled = Role == SetupRole.Tts;
        ConsentChoice.IsChecked = route?.Consent is not null;
        KeyInput.Clear();
        routeDirty = false;
        rendering = false;
        RenderStatus();
    }

    private void Choice_Changed(object sender, RoutedEventArgs e)
    {
        if (rendering || draft is null) return;
        draft = draft with { Profile = draft.Profile with { Kind = ApiChoice.IsChecked == true ? ProfileKind.Api : ProfileKind.Fixture } };
        RenderStatus();
    }

    private void Step_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (rendering || draft is null || e.Source != Steps) return;
        draft = draft with { Setup = draft.Setup! with { Checkpoint = (SetupStep)Steps.SelectedIndex } };
        RenderStatus();
    }

    private void Role_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!rendering && draft is not null)
        {
            if (routeDirty) ResultText.Text = "Unapplied route fields were discarded when changing role. Apply the selected role before leaving it.";
            RenderRole();
        }
    }

    private void Route_Changed(object sender, TextChangedEventArgs e)
    {
        if (rendering || draft is null) return;
        ConsentChoice.IsChecked = false;
        routeDirty = true;
        ResultText.Text = "Route fields changed. Apply the route and explicitly review its destination consent again before saving.";
    }

    private void ApplyRoute_Click(object sender, RoutedEventArgs e)
    {
        if (draft is null) return;
        try
        {
            var updated = SetupSettings.SelectRoute(draft, Role, ModelId.Text, Role == SetupRole.Tts ? VoiceId.Text : null);
            var route = updated.Setup!.Routes.Single(r => r.Role == Role);
            draft = SetupSettings.ReplaceRoute(updated, route with { Consent = ConsentChoice.IsChecked == true ? route.Selection() : null });
            routeDirty = false;
            ResultText.Text = "Route applied to the working checkpoint. Save to persist it. Model availability, credential validity, cost and quota remain unknown.";
            RenderStatus();
        }
        catch (ContractException ex) { ResultText.Text = ex.Message; }
    }

    private void Consent_Changed(object sender, RoutedEventArgs e)
    {
        if (!rendering && draft is not null) routeDirty = true;
    }

    private AppSettings Checkpoint()
    {
        if (draft is null || routeDirty)
            throw new ContractException(ErrorCode.InvalidContract, "Apply edited route fields before saving, or reload the saved checkpoint.");
        if (draft.Profile.Kind == ProfileKind.NotConfigured)
            draft = draft with { Profile = draft.Profile with { Kind = ProfileKind.Fixture } };
        draft.Validate();
        return draft;
    }

    private async Task<bool> PerformAsync(Func<CancellationToken, Task<SetupSaveResult>> action, SecretLease? secret = null)
    {
        var operation = operations.TryStart(SaveWork(action), secret);
        var saved = false;
        await ObserveAsync(operation, completed =>
        {
            var result = completed.Saved!;
            ResultText.Text = result.Summary;
            needsReload = !result.Save.Saved;
            if (result.Save.Saved)
            {
                saved = true;
                draft = result.Settings;
                revision = result.Save.Revision;
                RenderRole();
            }
            RenderStatus();
        });
        return saved;
    }

    // Factories keep worker closures separate from the callbacks that capture this window.
    private static Func<CancellationToken, Task<SetupWorkResult>> LoadWork(ISetupService backend) =>
        async token => new(SetupWorkOutcome.Completed, Loaded: await backend.LoadAsync(token).ConfigureAwait(false));

    private static Func<CancellationToken, Task<SetupWorkResult>> SaveWork(Func<CancellationToken, Task<SetupSaveResult>> action) =>
        async token => new(SetupWorkOutcome.Completed, Saved: await action(token).ConfigureAwait(false));

    private static Func<CancellationToken, Task<SetupWorkResult>> ReadWork(ISetupService backend, AppSettings snapshot, SetupRole role) =>
        token =>
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new SetupWorkResult(SetupWorkOutcome.Completed, Credential: backend.CheckCredential(snapshot, role)));
        };

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync(exit: false);

    private async void SaveExit_Click(object sender, RoutedEventArgs e) => await SaveAsync(exit: true);

    private async Task SaveAsync(bool exit)
    {
        if (!MayStart()) return;
        try
        {
            var snapshot = Checkpoint();
            var expectedRevision = revision;
            var backend = service;
            if (await PerformAsync(token => backend.SaveAsync(snapshot, expectedRevision, token)) && exit && !closed) Close();
        }
        catch (ContractException ex) { if (!closed) ResultText.Text = ex.Message; }
    }

    private bool Confirm(string text) => confirm?.Invoke(text) ?? MessageBox.Show(this, text, "Explicit scoped action",
        MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    private async void StoreKey_Click(object sender, RoutedEventArgs e)
    {
        if (!MayStart() || !Confirm($"Store a new key for this profile's {Role} route at {OpenAiSetup.Origin} and save this checkpoint? This invalidates that role's consent. Any previous key is detached, not deleted; remove it explicitly below. No provider access will be tested.")) return;
        try
        {
            var snapshot = Checkpoint();
            var expectedRevision = revision;
            var role = Role;
            var backend = service;
            var secret = TakeKey();
            // The runner, not this UI observation, owns the lease until native work actually ends.
            await PerformAsync(token => backend.ReplaceCredentialAsync(snapshot, expectedRevision, role, secret, token), secret);
        }
        catch (ContractException ex) { if (!closed) ResultText.Text = ex.Message; }
    }

    private SecretLease TakeKey()
    {
        using var secure = KeyInput.SecurePassword;
        var chars = new char[secure.Length];
        var pointer = Marshal.SecureStringToGlobalAllocUnicode(secure);
        try
        {
            Marshal.Copy(pointer, chars, 0, chars.Length);
            return new SecretLease(chars);
        }
        finally
        {
            KeyInput.Clear();
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(chars.AsSpan()));
            Marshal.ZeroFreeGlobalAllocUnicode(pointer);
        }
    }

    private async void ReadKey_Click(object sender, RoutedEventArgs e)
    {
        if (!MayStart() || !Confirm($"Read only this profile's selected {Role} key from Windows to check local presence? It will be immediately discarded, never revealed or sent to a provider.")) return;
        try
        {
            var snapshot = Checkpoint();
            var role = Role;
            var backend = service;
            var operation = operations.TryStart(ReadWork(backend, snapshot, role));
            await ObserveAsync(operation, result => ResultText.Text = CredentialMessages.Describe(result.Credential!.Value));
        }
        catch (ContractException ex) { if (!closed) ResultText.Text = ex.Message; }
    }

    private async void DetachKey_Click(object sender, RoutedEventArgs e)
    {
        if (!MayStart() || !Confirm($"Detach only this profile's {Role} credential and save? Consent is invalidated. The OS key remains listed for explicit removal; other roles and credentials are unchanged.")) return;
        try
        {
            var snapshot = Checkpoint();
            var expectedRevision = revision;
            var role = Role;
            var backend = service;
            await PerformAsync(token => backend.DetachCredentialAsync(snapshot, expectedRevision, role, token));
        }
        catch (ContractException ex) { if (!closed) ResultText.Text = ex.Message; }
    }

    private async void RemoveKey_Click(object sender, RoutedEventArgs e)
    {
        if (!MayStart()) return;
        if (RemovalChoice.SelectedItem is not PendingCredentialRemoval removal)
        {
            ResultText.Text = "No detached credential is selected. Detach a role first; unrelated Windows credentials cannot be listed or removed.";
            return;
        }
        if (!Confirm($"Permanently remove detached {removal.Role} reference {removal.CredentialId} for this profile only? An old settings snapshot cannot restore this key.")) return;
        try
        {
            var snapshot = Checkpoint();
            var expectedRevision = revision;
            var backend = service;
            await PerformAsync(token => backend.RemoveDetachedAsync(snapshot, expectedRevision, removal, token));
        }
        catch (ContractException ex) { if (!closed) ResultText.Text = ex.Message; }
    }

    private async void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (MayStart() && Confirm("Discard unsaved configuration edits and reload the saved checkpoint? No credential is read or deleted.")) await LoadAsync();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        operations.RequestCancellation();
        observationStop?.Cancel();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => Steps.SelectedIndex = Math.Max(0, Steps.SelectedIndex - 1);
    private void Next_Click(object sender, RoutedEventArgs e) => Steps.SelectedIndex = Math.Min(3, Steps.SelectedIndex + 1);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        closed = true;
        generation++;
        operationTimer.Stop();
        operations.RequestCancellation();
        observationStop?.Cancel();
        KeyInput.Clear();
    }

    private void Link_Click(object sender, RequestNavigateEventArgs e)
    {
        if (e.Uri.AbsoluteUri is not (OpenAiSetup.Pricing or OpenAiSetup.Retention)) return;
        if (Confirm("Open the official OpenAI page in your browser? This contacts the website, not the inference API."))
        {
            try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Win32Exception) { ResultText.Text = "The browser could not open. Use the displayed official link manually; no provider call was made."; }
        }
        e.Handled = true;
    }
}
