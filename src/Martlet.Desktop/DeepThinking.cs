using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>Who may hand out a provider key for a request: a conversation action (<see cref="ConversationAuthorization"/>) or a
/// background think on its own destination (<see cref="DeepThinkAuthorization"/>).</summary>
internal interface ICredentialAuthority
{
    Task<BoundProviderCredential?> ResolveAsync(ProviderCredentialBinding binding, CancellationToken token);
}

/// <summary>Where a background think's request goes when Deep thinking has a destination of its own (Companion › Deep
/// thinking): an OpenAI-compatible endpoint (a cloud provider, another server, or Ollama on this PC with its own model) or a
/// paired computer's Deep thinking role (or its Ollama) through its pinned gateway. It builds the request with that destination's bounds; the think's
/// message is fitted to them (<see cref="ThinkLonger.Fit"/>) and carries no tools.</summary>
internal sealed class DeepThinkTarget
{
    /// <summary>The context a paired computer's Ollama loads for a think: its gateway's largest, so long hidden thinking fits.</summary>
    internal const int HostContextTokens = GenerationSettings.MaximumHostContextTokens;
    /// <summary>The longest one request may run on a paired computer's gateway (its route's bound).</summary>
    internal static TimeSpan HostRequestTime { get; } = TimeSpan.FromSeconds(SelfHostSetup.MaximumGatewayRouteDurationSeconds);

    private DeepThinkTarget(DeepThinkingSettings settings, TextModelSelection model, ChatCompletionsTarget? chat, HostTextTarget? host,
        TextGenerationLimits input, ThinkBounds bounds)
    {
        Settings = settings;
        Model = model;
        Chat = chat;
        Host = host;
        Input = input;
        Bounds = bounds;
    }

    internal DeepThinkingSettings Settings { get; }
    internal TextModelSelection Model { get; }
    internal ChatCompletionsTarget? Chat { get; }
    internal HostTextTarget? Host { get; }
    /// <summary>The input bounds the think's limits start from (<see cref="ThinkLonger.Limits"/>).</summary>
    internal TextGenerationLimits Input { get; }
    internal ThinkBounds Bounds { get; }

    /// <summary>The destination of <paramref name="settings"/> (not Same as Thinking), with what this PC knows of the endpoint
    /// model's context (<paramref name="limits"/>) and whether its key comes from Thinking (<paramref name="thinking"/>).</summary>
    internal static DeepThinkTarget For(DeepThinkingSettings settings, ThinkEffort effort, SetupRoute? thinking, ModelLimits? limits)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        if (settings.Place == DeepThinkingPlace.Host)
        {
            var host = new HostTextTarget(settings.HostOrigin!, settings.HostId!, settings.HostSpkiFingerprint!, settings.HostDeviceId!,
                settings.HostCredentialId!.Value, settings.HostRoute) { Background = true };
            var bounds = ThinkLonger.HostBounds(effort);
            var hostLimits = LiveConversationConfiguration.ChatTextLimits with
            {
                MaxInputBytes = bounds.MaxInputBytes, MaxHistoryMessages = bounds.MaxHistoryMessages,
                MaxInputTokens = bounds.MaxInputTokens, MaxContextTokens = HostContextTokens
            };
            return new(settings, new(SelfHostSetup.GatewayOllamaAlias, settings.ModelId!), null, host, hostLimits, bounds);
        }
        var known = limits?.Find(settings.Origin, settings.ModelId)?.ContextTokens;
        var budget = ContextBudget.For(SetupRouteType.ChatCompletions, settings.Origin, null, known);
        var endpointLimits = LiveConversationConfiguration.ChatTextLimits with
        {
            MaxInputBytes = BoundedTextInput.HardMaxInputUtf8Bytes, MaxHistoryMessages = BoundedTextInput.HardMaxHistoryMessages,
            MaxInputTokens = budget.InputTokens, MaxContextTokens = budget.InputTokens + LiveConversationConfiguration.ChatTextLimits.MaxOutputTokens
        };
        var keyless = settings.CredentialId is null && !settings.UsesThinkingKey(thinking);
        return new(settings, new(ChatCompletionsSetup.Alias, settings.ModelId!), new(settings.Origin!, keyless), null, endpointLimits,
            new(BoundedTextInput.HardMaxInputUtf8Bytes, BoundedTextInput.HardMaxHistoryMessages, budget.InputTokens, Tools: false));
    }

    /// <summary>The think's request: Thinking steps On at <paramref name="effort"/> (a paired computer's Ollama gets its own
    /// <c>think</c> and its largest context), its own output budget and the <paramref name="time"/> left (a paired computer's
    /// gateway takes one request for at most its route's longest job, so a think there gets at most that). A request that
    /// <paramref name="continuing"/> an answer the live floor stopped writes on without thinking first: the thinking was done
    /// before the answer began.</summary>
    internal ConversationRequest Request(BoundedTextInput input, ThinkEffort effort, TimeSpan time, bool continuing = false)
    {
        if (Host is not null && time > HostRequestTime) time = HostRequestTime;
        var generation = Host is not null
            ? new GenerationSettings { Reasoning = !continuing, ContextTokens = HostContextTokens }
            : new GenerationSettings
            {
                Reasoning = !continuing,
                ReasoningEffort = continuing ? null
                    : effort == ThinkEffort.High ? GenerationSupport.ReasoningEffortHigh : GenerationSupport.ReasoningEffortOn
            };
        return new(input, Model, ThinkLonger.Limits(Input, effort, time), ThinkLonger.TurnLimits(time), chat: Chat, host: Host,
            generation: generation);
    }

    public override string ToString() => nameof(DeepThinkTarget);

    /// <summary>A Thinking pool job's request (<see cref="ThinkingPool"/>): its own output budget and the time it may take,
    /// Thinking steps as the job asks (null: the model's own default), no tools.</summary>
    internal ConversationRequest OneShot(BoundedTextInput input, int maxOutputTokens, bool? reasoning, TimeSpan time)
    {
        if (Host is not null && time > HostRequestTime) time = HostRequestTime;
        var generation = Host is not null
            ? new GenerationSettings { Reasoning = reasoning, ContextTokens = HostContextTokens }
            : new GenerationSettings { Reasoning = reasoning, ReasoningEffort = reasoning == true ? GenerationSupport.ReasoningEffortOn : null };
        var output = Math.Min(maxOutputTokens, ThinkLonger.MediumOutputTokens);
        var limits = ThinkLonger.Limits(Input, ThinkEffort.Medium, time) with
        {
            MaxOutputTokens = output, MaxContextTokens = Input.MaxInputTokens + output
        };
        return new(input, Model, limits, ThinkLonger.TurnLimits(time), chat: Chat, host: Host, generation: generation);
    }
}

/// <summary>The one-use permission for a background think's requests to its own destination (Companion › Deep thinking): exactly
/// the think's request (and its retries: without Thinking steps when the model refuses them), bound to that endpoint and model
/// or paired computer, until the think's time is up. An endpoint's key is its own (Windows Credential Manager), Thinking's for
/// the same base URL, or none; a paired computer is reached with this PC's pairing, which the host client reads itself.</summary>
internal sealed class DeepThinkAuthorization(DeepThinkTarget target, ConversationRequest request, Guid profileId, SetupRoute? thinking,
    ICredentialStore vault, TimeProvider clock, DateTimeOffset expires, bool media = false) : IConversationAuthorizationSource, ICredentialAuthority
{
    private const int MaximumRequests = 3;
    private readonly object gate = new();
    private readonly HashSet<Guid> requests = [];
    private int tickets;

    private ProviderCredentialBinding Binding => target.Host is { } host
        ? HostTextGenerationStream.Binding(host, target.Model)
        : new(ChatCompletionsSetup.BaseUri(target.Settings.Origin!), ProviderRole.Llm, target.Model.UpstreamModelId);

    public ValueTask<AuthorizedTextOperation?> AuthorizeTextAsync(TextAuthorizationAction action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        var expected = new OperationBudget(action.Context.Ids, action.Context.Epoch, ProviderRole.Llm, 1,
            action.Input.Utf8Bytes, action.Input.InputTokenReservation, request.TextLimits.MaxOutputTokens, 0);
        var expiry = action.Context.Deadline < expires ? action.Context.Deadline : expires;
        lock (gate)
        {
            if (cancellationToken.IsCancellationRequested || clock.GetUtcNow() >= expires ||
                !ReferenceEquals(action.Input, request.Input) && !ReferenceEquals(action.Input.Origin, request.Input) ||
                action.Model != request.Model || action.Limits != request.TextLimits || action.Budget != expected ||
                requests.Count >= MaximumRequests || !requests.Add(action.Context.Ids.RequestId))
                return ValueTask.FromResult<AuthorizedTextOperation?>(null);
            tickets++;
        }
        return ValueTask.FromResult<AuthorizedTextOperation?>(new(new TextDisclosureAuthorization(Binding, action.Model,
            action.Context.Ids, action.Context.Epoch, action.Limits, expiry, true, true,
            media && request.Input.Image is not null, media && request.Input.Audio is not null), new(action.Budget, expiry)));
    }

    public ValueTask<AuthorizedSpeechOperation?> AuthorizeSpeechAsync(SpeechAuthorizationAction action, CancellationToken cancellationToken) =>
        ValueTask.FromResult<AuthorizedSpeechOperation?>(null);

    public async Task<BoundProviderCredential?> ResolveAsync(ProviderCredentialBinding binding, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (target.Host is not null || binding != Binding || tickets == 0) throw new CredentialUnavailableException();
            tickets--;
        }
        var settings = target.Settings;
        var scope = settings.CredentialId is { } own ? settings.Binding(profileId, own)
            : settings.UsesThinkingKey(thinking) ? CredentialBinding.For(profileId, thinking!, thinking!.CredentialId!.Value)
            : null;
        if (scope is null) return null;
        scope.Validate();
        return await Task.Run(() =>
        {
            using var result = vault.Read(scope);
            if (result.Error != CredentialError.None || result.Secret is null) throw new CredentialUnavailableException();
            BoundProviderCredential? credential = null;
            result.Secret.Use(secret => credential = new(binding, new string(secret)));
            return credential;
        }, token).ConfigureAwait(false);
    }

    public override string ToString() => nameof(DeepThinkAuthorization);
}
