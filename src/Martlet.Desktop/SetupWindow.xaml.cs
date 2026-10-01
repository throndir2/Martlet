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
using Martlet.Providers;

namespace Martlet.Desktop;

public partial class SetupWindow : ThemedWindow
{
    internal Action<Window>? Troubleshooting { get; init; }
    internal Action<Window>? ConfigurationRecovery { get; init; }
    private void Troubleshooting_Click(object sender, RoutedEventArgs e) => Troubleshooting?.Invoke(this);
    private void Recovery_Click(object sender, RoutedEventArgs e)
    {
        if (ConfigurationRecovery is not { } openRecovery)
        {
            ResultText.Text = "Configuration recovery is unavailable in this setup window. Close it and open Configuration backup / restore from the main window.";
            return;
        }
        openRecovery(this);
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

    private sealed record LlmProvider(string Name, string? BaseUrl, bool Chat, string? DefaultModelId = null)
    {
        public override string ToString() => Name;
    }
    private static readonly LlmProvider OpenAiProvider = new("OpenAI (https://api.openai.com, Responses API)", null, false);
    private static readonly LlmProvider CustomProvider = new("Custom OpenAI-compatible endpoint (Chat Completions)", null, true);
    private static readonly IReadOnlyList<LlmProvider> LlmProviders =
    [
        OpenAiProvider,
        .. ChatCompletionsEndpointCatalog.NamedEndpoints.Select(endpoint =>
            new LlmProvider($"{endpoint.Name} ({endpoint.BaseUrl}, Chat Completions)", endpoint.BaseUrl, true, endpoint.DefaultModelId)),
        CustomProvider
    ];
    private static readonly SetupRole[] Jobs = [SetupRole.Llm, SetupRole.Stt, SetupRole.Tts];
    private LlmProvider Provider => Role == SetupRole.Llm && ProviderChoice.SelectedItem is LlmProvider provider
        ? provider : OpenAiProvider;

    /// <summary>The job to show first; otherwise the first job with a saved choice, else Thinking.</summary>
    internal SetupRole? InitialRole { get; init; }

    /// <summary>The recommended model a provider prefills for a job; null when Martlet cannot know one (a custom server).</summary>
    internal static string? DefaultModel(SetupRole role, bool chat, string? chatDefault) => chat ? chatDefault : role switch
    {
        SetupRole.Llm => OpenAiTextGenerationCatalog.DefaultModelId,
        SetupRole.Stt => OpenAiTranscriptionCatalog.DefaultModelId,
        SetupRole.Tts => OpenAiSpeechSynthesisCatalog.DefaultModelId,
        _ => null
    };

    private static bool IsAnyDefault(string model) =>
        model.Length == 0 || Jobs.Any(job => DefaultModel(job, false, null) == model)
        || LlmProviders.Any(provider => provider.DefaultModelId == model);

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
        RoleChoice.ItemsSource = Jobs;
        RoleChoice.SelectedIndex = 0;
        ProviderChoice.ItemsSource = LlmProviders;
        ProviderChoice.SelectedItem = OpenAiProvider;
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
            ResultText.Text = loaded.Error?.Summary ?? (loaded.Settings?.SchemaVersion is 1 or 2
                ? $"Version {loaded.Settings.SchemaVersion} loaded unchanged. Explicit Save migrates it and atomically snapshots the original; profile ID, routes and legacy references are preserved."
                : "Checkpoint loaded. No secret lookup, device or network action was performed.");
            if (draft is not null)
            {
                rendering = true;

                Steps.SelectedIndex = (int)draft.Setup!.Checkpoint;
                var routes = draft.Setup.Routes;
                RoleChoice.SelectedItem = InitialRole
                    ?? Jobs.Cast<SetupRole?>().FirstOrDefault(job => routes.Any(route => route.Role == job))
                    ?? SetupRole.Llm;
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
        var saved = draft?.Setup?.Routes.SingleOrDefault(r => r.Role == Role);
        var job = SetupJobNameConverter.Name(Role);
        CredentialScope.Text = saved?.RouteType == SetupRouteType.ChatCompletions
            ? $"Selected job: {job}; internal alias: {saved.ProviderAlias}; destination: {LiveConversationConfiguration.LlmDestinationName(saved)}. The key is bound to this exact base URL and is optional for local servers. Select another job on Jobs."
            : $"Selected job: {job}; internal alias: {OpenAiSetup.Alias(Role)}; origin: {OpenAiSetup.Origin}. Select another job on Jobs.";
        RemovalChoice.ItemsSource = draft?.Setup?.PendingRemovals;
        RemovalChoice.SelectedIndex = 0;
    }

    private void RenderRole()
    {
        rendering = true;
        var route = draft?.Setup?.Routes.SingleOrDefault(r => r.Role == Role);
        var chat = Role == SetupRole.Llm && route?.RouteType == SetupRouteType.ChatCompletions;
        ProviderChoice.IsEnabled = Role == SetupRole.Llm;
        ProviderChoice.SelectedItem = !chat ? OpenAiProvider
            : LlmProviders.FirstOrDefault(provider => provider.BaseUrl == route!.Origin) ?? CustomProvider;
        BaseUrl.Text = chat ? route!.Origin : "";
        RenderProvider();
        var catalog = chat ? Array.Empty<string>() : Catalog(Role);
        ModelCatalogChoice.SelectedItem = route is not null && catalog.Contains(route.ModelId, StringComparer.Ordinal)
            ? route.ModelId : null;
        var prefilled = string.IsNullOrEmpty(route?.ModelId);
        ModelId.Text = prefilled ? DefaultModel(Role, Provider.Chat, Provider.DefaultModelId) ?? "" : route!.ModelId;
        VoiceId.Text = route?.VoiceId ?? (Role == SetupRole.Tts ? OpenAiSpeechSynthesisCatalog.DefaultVoice : "");
        VoiceId.IsEnabled = Role == SetupRole.Tts;
        VoicePanel.Visibility = Role == SetupRole.Tts ? Visibility.Visible : Visibility.Collapsed;
        VoiceHint.Text = $"Voices this adapter supports: {string.Join(", ", OpenAiSpeechSynthesisCatalog.SupportedVoices)}. A Martlet host voice (F5) is chosen on the Devices map.";
        JobText.Text = Role switch
        {
            SetupRole.Llm => "Thinking is the conversation model that writes Martlet's replies. Pick a cloud provider here, or hand thinking to a paired Martlet host on the Devices map.",
            SetupRole.Stt => "Listening turns what you say into text. It needs a microphone (Microphone and speakers page). Without it you can still type.",
            _ => "Speaking turns replies into a voice played on your speakers (Microphone and speakers page). Without it replies stay as text."
        };
        ConsentChoice.IsChecked = route?.Consent is not null;
        KeyInput.Clear();
        routeDirty = false;
        rendering = false;
        RenderStatus();
    }

    // Only presentation; the caller owns dirtiness and the rendering guard.
    private void RenderProvider()
    {
        var provider = Provider;
        BaseUrl.IsEnabled = provider.Chat;
        BaseUrl.IsReadOnly = provider.BaseUrl is not null;
        if (provider.BaseUrl is not null) BaseUrl.Text = provider.BaseUrl;
        else if (!provider.Chat) BaseUrl.Text = "";
        var catalog = provider.Chat ? Array.Empty<string>() : Catalog(Role);
        ModelCatalogChoice.ItemsSource = catalog;
        ModelCatalogChoice.IsEnabled = catalog.Count > 0;
        ProviderPanel.Visibility = Role == SetupRole.Llm ? Visibility.Visible : Visibility.Collapsed;
        BaseUrlPanel.Visibility = provider.Chat ? Visibility.Visible : Visibility.Collapsed;
        CatalogPanel.Visibility = catalog.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        const string reasoning = " Prefer instruct/chat models: each reply is capped at 256 tokens and reasoning/thinking models spend part of that on hidden thinking.";
        ProviderHint.Text = Role != SetupRole.Llm
            ? $"{SetupJobNameConverter.Name(Role).Split(' ')[0]} uses OpenAI in the cloud (https://api.openai.com) and its own API key. The recommended model is prefilled."
            : provider.BaseUrl == ChatCompletionsEndpointCatalog.OpenRouterBaseUrl
                ? $"Recommended: {provider.DefaultModelId} (prefilled). You can enter any exact OpenRouter model ID instead, for example openai/gpt-4o-mini (':free' variants use OpenRouter's free tier). Store your OpenRouter API key on Credentials. OpenRouter chooses the upstream provider; fallback to other providers is disabled." + reasoning
                : provider.BaseUrl == ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl
                    ? $"Recommended: {provider.DefaultModelId} (prefilled). You can enter any exact model ID shown on build.nvidia.com instead. Store your NVIDIA API key (nvapi-...) on Credentials." + reasoning
                    : provider.Chat
                        ? "Enter the API base URL without /chat/completions, for example https://api.groq.com/openai/v1, https://api.together.xyz/v1, or a local server such as http://127.0.0.1:1234/v1 (LM Studio), http://127.0.0.1:8080/v1 (llama.cpp) or http://127.0.0.1:11434/v1 (Ollama), and the model ID that server serves. HTTP is allowed only for a literal loopback IP. A key is optional; store one on Credentials if the server requires it." + reasoning
                        : $"Recommended: {OpenAiTextGenerationCatalog.DefaultModelId} (prefilled, fast and inexpensive). Pick the larger model below if you prefer.";
        BoundaryText.Text = provider.Chat
            ? $"{OpenAiSetup.Boundary(Role)} Internal alias: {ChatCompletionsSetup.Alias}. Destination: {(provider.BaseUrl ?? "the exact base URL entered below")}."
            : $"{OpenAiSetup.Boundary(Role)} Internal alias: {OpenAiSetup.Alias(Role)}. Destination: {OpenAiSetup.Origin}.";
    }

    private void Provider_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (rendering || draft is null) return;
        rendering = true;
        if (Provider == CustomProvider && ChatCompletionsEndpointCatalog.Named(BaseUrl.Text) is not null) BaseUrl.Text = "";
        RenderProvider();
        var prefill = IsAnyDefault(ModelId.Text.Trim());
        if (prefill) ModelId.Text = DefaultModel(Role, Provider.Chat, Provider.DefaultModelId) ?? "";
        ConsentChoice.IsChecked = false;
        rendering = false;
        routeDirty = true;
        ResultText.Text = prefill && ModelId.Text.Length > 0
            ? "Provider changed and its recommended model prefilled. Review it, apply the route and explicitly review its destination consent again before saving."
            : "Provider changed. Enter the exact model ID, apply the route and explicitly review its destination consent again before saving.";
    }


    private void Step_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (rendering || draft is null || e.Source != Steps) return;
        draft = draft with { Setup = draft.Setup! with { Checkpoint = (SetupStep)Steps.SelectedIndex } };
        RenderStatus();
        if (Steps.SelectedContent is UIElement page) Motion.Enter(page, dx: 24, dy: 0);
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

    private void UseCatalogModel_Click(object sender, RoutedEventArgs e)
    {
        if (draft is null || ModelCatalogChoice.SelectedItem is not string selected) return;
        ModelId.Text = selected;
        ResultText.Text = "Compatible model ID copied into the unsaved route. Review the exact model and destination, then apply and consent again.";
    }

    private void ApplyRoute_Click(object sender, RoutedEventArgs e)
    {
        if (draft is null) return;
        try
        {
            var old = draft.Setup?.Routes.SingleOrDefault(r => r.Role == Role);
            var provider = Provider;
            var baseUrl = BaseUrl.Text.Trim();
            var modelId = ModelId.Text.Trim();
            var sameDestination = old is null || (provider.Chat
                ? old.RouteType == SetupRouteType.ChatCompletions && old.Origin == baseUrl
                : old.RouteType is null or SetupRouteType.OpenAi);
            if (!sameDestination && draft.Setup!.PendingRemovals.Any(removal => removal.Role == Role))
                throw new ContractException(ErrorCode.InvalidContract,
                    "Remove this role's detached key on Credentials before switching its destination again; the prior route was not replaced.");
            AppSettings updated;
            if (provider.Chat)
                updated = ChatCompletionsSetup.SelectRoute(draft, baseUrl, modelId);
            else
            {
                if (!Catalog(Role).Contains(modelId, StringComparer.Ordinal))
                    throw new ContractException(ErrorCode.ProviderCapability,
                        "This named OpenAI adapter does not support that model ID. Select an exact compatible catalog entry; the prior route was not replaced.");
                if (Role == SetupRole.Tts && !OpenAiSpeechSynthesisCatalog.SupportsVoice(VoiceId.Text))
                    throw new ContractException(ErrorCode.ProviderCapability,
                        "This named OpenAI TTS adapter does not support that voice ID. Review the displayed compatible voices; the prior route was not replaced.");
                updated = SetupSettings.SelectRoute(draft, Role, modelId, Role == SetupRole.Tts ? VoiceId.Text : null);
            }
            var pending = updated.Setup!.PendingRemovals.Count;
            updated = SetupSettings.QueueReplacedCredential(updated, old);
            var detached = updated.Setup!.PendingRemovals.Count != pending;
            var route = updated.Setup!.Routes.Single(r => r.Role == Role);
            draft = SetupSettings.ReplaceRoute(updated, route with { Consent = ConsentChoice.IsChecked == true ? route.Selection() : null });
            routeDirty = false;
            ResultText.Text = "Route applied to the working checkpoint. Save to persist it. Model availability, credential validity, cost and quota remain unknown." +
                (detached ? " The previous destination's key was detached and listed for explicit removal on Credentials; store a key for the new destination if it needs one." : "");
            RenderStatus();
        }
        catch (ContractException ex) { ResultText.Text = ex.Message; }
    }

    private static IReadOnlyList<string> Catalog(SetupRole role) => role switch
    {
        SetupRole.Stt => OpenAiTranscriptionCatalog.SupportedModelIds,
        SetupRole.Llm => OpenAiTextGenerationCatalog.SupportedModelIds,
        SetupRole.Tts => OpenAiSpeechSynthesisCatalog.SupportedModelIds,
        _ => throw new ContractException(ErrorCode.InvalidContract, "Choose STT, LLM or TTS.")
    };

    private void Consent_Changed(object sender, RoutedEventArgs e)
    {
        if (!rendering && draft is not null) routeDirty = true;
    }

    private AppSettings Checkpoint()
    {
        if (draft is null || routeDirty)
            throw new ContractException(ErrorCode.InvalidContract, "Apply edited route fields before saving, or reload the saved checkpoint.");
        if (draft.Profile.Kind is ProfileKind.NotConfigured or ProfileKind.Fixture)
            draft = draft with { Profile = draft.Profile with { Kind = ProfileKind.Api } };
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

    private bool Confirm(string text) => confirm?.Invoke(text) ??
        ConfirmationDialog.Confirm(this, text, "Explicit scoped action");

    private async void StoreKey_Click(object sender, RoutedEventArgs e)
    {
        var saved = draft?.Setup?.Routes.SingleOrDefault(r => r.Role == Role);
        var destination = saved?.RouteType == SetupRouteType.ChatCompletions ? saved.Origin : OpenAiSetup.Origin;
        if (!MayStart() || !Confirm($"Store a new key for this profile's {Role} route at {destination} and save this checkpoint? This invalidates that role's consent. Any previous key is detached, not deleted; remove it explicitly below. No provider access will be tested.")) return;
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
