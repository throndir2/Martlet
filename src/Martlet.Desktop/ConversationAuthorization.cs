using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;

internal sealed class LiveActionException(string code) : Exception("The live action is no longer authorized.")
{
    internal string Code { get; } = code;
}

// One human action, not persisted consent. Each exact runtime demand consumes an independent reservation.
internal sealed class ConversationAuthorization : IConversationAuthorizationSource, ICredentialAuthority
{
    private readonly object gate = new();
    private readonly TimeProvider clock;
    private readonly Func<bool> current;
    private readonly Func<CancellationToken, Task<SettingsLoadResult>> load;
    private readonly ICredentialStore vault;
    private readonly CancellationToken caller;
    private CancellationToken worker;
    private readonly long started;
    private readonly DateTimeOffset acceptedAt;
    private readonly Dictionary<ProviderRole, int> credentialTickets = [];
    private readonly HashSet<Guid> requests = [];
    private int revoked, textRequests, sttRequests, speechRequests, speechBytes, fallbackTickets, standIns;
    private long speechSamples;
    private BoundedTextInput? exactInput;
    private int maxTextRequests = 1;
    internal LiveConversationConfiguration Configuration { get; }
    /// <summary>The bounds every text request of this action must have: the configuration's, or a background think's own.</summary>
    internal TextGenerationLimits TextLimits { get; }
    /// <summary>How long this action may last: <see cref="LiveConversationConfiguration.ActionLifetime"/>, or a background
    /// think's time left.</summary>
    internal TimeSpan Lifetime { get; }
    internal bool Voice { get; }
    internal bool Microphone { get; }
    /// <summary>This action may send its one attached picture with the text: a screen glance, or the user's screen sent along
    /// with their words while vision is on.</summary>
    internal bool Screen { get; }
    /// <summary>The user let Thinking hear their voice: this action may send their recording with the transcript.</summary>
    internal bool Hear { get; }
    internal int ReservedRequests { get { lock (gate) return textRequests + sttRequests + speechRequests; } }
    internal CredentialError? CredentialFailure { get; private set; }

    internal ConversationAuthorization(LiveConversationConfiguration configuration, bool voice, bool microphone,
        TimeProvider clock, Func<bool> current, Func<CancellationToken, Task<SettingsLoadResult>> load,
        ICredentialStore vault, CancellationToken caller, bool screen = false, bool hear = false,
        TextGenerationLimits? textLimits = null, TimeSpan? lifetime = null)
    {
        Configuration = configuration;
        TextLimits = textLimits ?? configuration.TextLimits;
        Lifetime = lifetime ?? LiveConversationConfiguration.ActionLifetime;
        Voice = voice;
        Microphone = microphone;
        Screen = screen;
        Hear = hear;
        this.clock = clock;
        this.current = current;
        this.load = load;
        this.vault = vault;
        this.caller = caller;
        started = clock.GetTimestamp();
        acceptedAt = clock.GetUtcNow();
    }

    internal void BindWorker(CancellationToken token) => worker = token;
    // A reply that offers tools may make one request per tool round plus the final answer (or one retry without tools when the
    // model rejects them); each continues this exact input.
    internal void BindInput(BoundedTextInput input, int toolRounds = 0, bool imageOptional = false)
    {
        lock (gate)
        {
            exactInput = input;
            // A Thinking fallback may ask once more, and once more without tools if it rejects them; a model that rejects the
            // recording is asked once more with the transcript only, and one that rejects the screen picture sent with the
            // user's words once more without it.
            maxTextRequests = (input.Tools.Count > 0 ? Math.Max(toolRounds + 1, 2) : 1) +
                (Configuration.Fallback is null ? 0 : 2) + (input.Audio is null ? 0 : 1) + (imageOptional && input.Image is not null ? 1 : 0);
        }
    }
    internal void Revoke() => Interlocked.Exchange(ref revoked, 1);
    internal bool IsCanceled => Volatile.Read(ref revoked) != 0 || caller.IsCancellationRequested ||
        worker.IsCancellationRequested || !current();

    internal void Check(CancellationToken token = default)
    {
        caller.ThrowIfCancellationRequested();
        worker.ThrowIfCancellationRequested();
        token.ThrowIfCancellationRequested();
        if (IsCanceled) throw new LiveActionException("conversation.revoked");
        if (clock.GetUtcNow() >= acceptedAt + Lifetime || clock.GetElapsedTime(started) >= Lifetime)
            throw new LiveActionException("conversation.expired");
    }

    internal DateTimeOffset Deadline(TimeSpan maximum, bool fromAcceptance = false)
    {
        Check();
        var elapsed = clock.GetElapsedTime(started);
        var remaining = (fromAcceptance ? maximum : Lifetime) - elapsed;
        if (remaining <= TimeSpan.Zero) throw new LiveActionException("conversation.expired");
        var absolute = acceptedAt + (fromAcceptance ? maximum : Lifetime);
        var clamped = clock.GetUtcNow() + (remaining < maximum ? remaining : maximum);
        return clamped < absolute ? clamped : absolute;
    }

    internal async Task ValidateSettingsAsync(CancellationToken token)
    {
        Check(token);
        var loaded = await load(token).ConfigureAwait(false);
        Check(token);
        var latest = LiveConversationConfiguration.From(loaded);
        if (latest is null || latest.Revision != Configuration.Revision || latest.Profile != Configuration.Profile ||
            latest.Unavailable(Voice, Microphone) is not null ||
            !latest.Routes.SequenceEqual(Configuration.Routes) || latest.Audio != Configuration.Audio ||
            latest.Persona != Configuration.Persona || latest.Memory != Configuration.Memory ||
            latest.Generation != Configuration.Generation || latest.Fallback != Configuration.Fallback)
        {
            Revoke();
            throw new LiveActionException("conversation.configuration_changed");
        }
    }

    internal AudioUploadAuthorization AuthorizeAudio(ProviderRequestContext context)
    {
        Check();
        lock (gate)
        {
            if (!Microphone || sttRequests != 0) throw new LiveActionException("conversation.audio_not_authorized");
            sttRequests++;
            Ticket(ProviderRole.Stt);
        }
        return new(Binding(SetupRole.Stt), context.Ids, context.Epoch, LiveConversationConfiguration.TranscriptionLimits,
            Min(context.Deadline, Deadline(TimeSpan.FromSeconds(30))), true, true);
    }

    /// <summary>The permission for Parakeet <paramref name="modelId"/> on this PC to hear the utterance when Listening's own route
    /// (a paired host or OpenAI) failed it, or in the route's place while the route failed moments ago: nothing is sent anywhere
    /// and nothing costs money. Once per action, after at most the route's own request, and never when Listening already runs on
    /// this PC.</summary>
    internal AudioUploadAuthorization AuthorizeStandIn(ProviderRequestContext context, string modelId)
    {
        Check();
        lock (gate)
        {
            if (!Microphone || sttRequests > 1 || standIns != 0 || Configuration.LocalStt())
                throw new LiveActionException("conversation.audio_not_authorized");
            standIns++;
        }
        return new(LocalTranscriptionAdapter.Binding(modelId), context.Ids, context.Epoch, LiveConversationConfiguration.TranscriptionLimits,
            Min(context.Deadline, Deadline(TimeSpan.FromSeconds(30))), true, false);
    }

    public async ValueTask<AuthorizedTextOperation?> AuthorizeTextAsync(TextAuthorizationAction action, CancellationToken token)
    {
        await ValidateSettingsAsync(token).ConfigureAwait(false);
        var selection = Configuration.TextSelection();
        var fallback = Configuration.FallbackSelection() is { } second && action.Model == second;
        var expected = new OperationBudget(action.Context.Ids, action.Context.Epoch, ProviderRole.Llm, 1,
            action.Input.Utf8Bytes, action.Input.InputTokenReservation, TextLimits.MaxOutputTokens, 0);
        var expiry = Min(action.Context.Deadline, Deadline(TextLimits.MaxRequestTime));
        lock (gate)
        {
            Check(token);
            // Tool rounds, a retry without tools and one without the recording all continue this exact input.
            var continues = exactInput is not null && ReferenceEquals(action.Input.Origin, exactInput);
            if (!ReferenceEquals(action.Input, exactInput) && !continues || action.Model != selection && !fallback ||
                action.Limits != TextLimits || action.Budget != expected ||
                textRequests >= maxTextRequests || !requests.Add(action.Context.Ids.RequestId)) return null;
            textRequests++;
            if (fallback) fallbackTickets++;
            else Ticket(ProviderRole.Llm);
        }
        return new(new(fallback ? FallbackBinding() : Binding(SetupRole.Llm), action.Model, action.Context.Ids, action.Context.Epoch,
            action.Limits, expiry, true, true, allowImageDisclosure: Screen && action.Input.Image is not null,
            allowAudioDisclosure: Hear && !fallback && action.Input.Audio is not null), new(action.Budget, expiry));
    }

    // The fallback's key is bound to its exact base URL and model, like a Chat Completions Thinking key.
    private ProviderCredentialBinding FallbackBinding() =>
        new(ChatCompletionsSetup.BaseUri(Configuration.Fallback!.Origin), ProviderRole.Llm, Configuration.Fallback.ModelId);

    public async ValueTask<AuthorizedSpeechOperation?> AuthorizeSpeechAsync(SpeechAuthorizationAction action, CancellationToken token)
    {
        await ValidateSettingsAsync(token).ConfigureAwait(false);
        if (!Voice) return null;
        var selection = Configuration.SpeechSelection();
        var expected = new OperationBudget(action.Context.Ids, action.Context.Epoch, ProviderRole.Tts, 1,
            action.Input.Utf8Bytes, 0, 0, LiveConversationConfiguration.SpeechLimits.MaxSamples);
        var expiry = Min(action.Context.Deadline, Deadline(TimeSpan.FromSeconds(20)));
        lock (gate)
        {
            Check(token);
            if (action.Selection != selection || action.Limits != LiveConversationConfiguration.SpeechLimits ||
                action.Budget != expected || speechRequests >= 8 || action.Segment != speechRequests + 1 ||
                speechBytes + action.Input.Utf8Bytes > 12_288 || speechSamples + expected.OutputSamples > 1_920_000 ||
                !requests.Add(action.Context.Ids.RequestId)) return null;
            speechRequests++;
            speechBytes += action.Input.Utf8Bytes;
            speechSamples += expected.OutputSamples;
            Ticket(ProviderRole.Tts);
        }
        return new(new(Binding(SetupRole.Tts), action.Selection, action.Input, action.Context.Ids, action.Context.Epoch,
            action.Limits, expiry, true, true, true), new(action.Budget, expiry));
    }

    private ProviderCredentialBinding Binding(SetupRole role)
    {
        var route = Configuration.Route(role);
        // A paired Martlet host is bound to its exact pinned gateway origin and model.
        if (role == SetupRole.Llm && Configuration.HostTarget() is { } host)
            return HostTextGenerationStream.Binding(host, Configuration.TextSelection());
        if (role == SetupRole.Stt && Configuration.SttHostTarget() is { } listener)
            return HostTranscriptionAdapter.Binding(listener, route.ModelId);
        if (role == SetupRole.Stt && Configuration.LocalStt())
            return LocalTranscriptionAdapter.Binding(route.ModelId);
        if (role == SetupRole.Tts && Configuration.HostSpeechTarget() is { } voiceHost)
            return HostSpeechSynthesisStream.Binding(voiceHost);
        // An ElevenLabs key is bound to api.elevenlabs.io and the chosen model.
        if (role == SetupRole.Tts && Configuration.ElevenLabsVoiceTarget() is { } cloned)
            return ElevenLabsSpeechSynthesisStream.Binding(cloned);
        // A Chat Completions key is bound to the exact saved API base URL, never to api.openai.com.
        var origin = route.RouteType == SetupRouteType.ChatCompletions
            ? ChatCompletionsSetup.BaseUri(route.Origin) : OpenAiTranscriptionCatalog.Origin;
        return new(origin, role switch { SetupRole.Stt => ProviderRole.Stt, SetupRole.Llm => ProviderRole.Llm, _ => ProviderRole.Tts },
            route.ModelId);
    }

    private void Ticket(ProviderRole role) => credentialTickets[role] = credentialTickets.GetValueOrDefault(role) + 1;
    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    internal async Task<BoundProviderCredential?> ResolveAsync(ProviderCredentialBinding binding, CancellationToken token)
    {
        Check(token);
        var role = binding.Role switch { ProviderRole.Stt => SetupRole.Stt, ProviderRole.Llm => SetupRole.Llm,
            ProviderRole.Tts => SetupRole.Tts, _ => throw new CredentialUnavailableException() };
        if (role == SetupRole.Stt && !Microphone || role == SetupRole.Tts && !Voice)
            throw new CredentialUnavailableException();
        if (role == SetupRole.Llm && Configuration.Fallback is { } fallback && binding == FallbackBinding() && binding != Binding(role))
            return await ResolveFallbackAsync(fallback, binding, token).ConfigureAwait(false);
        lock (gate)
        {
            if (binding != Binding(role) || credentialTickets.GetValueOrDefault(binding.Role) != 1)
                throw new CredentialUnavailableException();
            credentialTickets[binding.Role] = 0;
        }
        await ValidateSettingsAsync(token).ConfigureAwait(false);
        var route = Configuration.Route(role);
        if (route.CredentialId is not { } credentialId) throw new CredentialUnavailableException();
        var scope = CredentialBinding.For(Configuration.Profile, route, credentialId);
        return await ReadAsync(scope, binding, token).ConfigureAwait(false);
    }

    private async Task<BoundProviderCredential?> ResolveFallbackAsync(ThinkingFallbackSettings fallback,
        ProviderCredentialBinding binding, CancellationToken token)
    {
        lock (gate)
        {
            if (fallbackTickets != 1) throw new CredentialUnavailableException();
            fallbackTickets = 0;
        }
        await ValidateSettingsAsync(token).ConfigureAwait(false);
        var thinking = Configuration.Route(SetupRole.Llm);
        // Its own key, or the Thinking route's key when both use the same Chat Completions endpoint.
        var scope = fallback.CredentialId is { } own ? fallback.Binding(Configuration.Profile, own)
            : fallback.UsesThinkingKey(thinking) ? CredentialBinding.For(Configuration.Profile, thinking, thinking.CredentialId!.Value)
            : throw new CredentialUnavailableException();
        return await ReadAsync(scope, binding, token).ConfigureAwait(false);
    }

    private async Task<BoundProviderCredential?> ReadAsync(CredentialBinding scope, ProviderCredentialBinding binding,
        CancellationToken token)
    {
        scope.Validate();
        // Native work is owned here, not by a window or cancellation observer. Always clear its lease.
        using var result = vault.Read(scope);
        Check(token);
        await ValidateSettingsAsync(token).ConfigureAwait(false);
        if (result.Error != CredentialError.None || result.Secret is null)
        {
            CredentialFailure = result.Error == CredentialError.None ? CredentialError.Unavailable : result.Error;
            throw new CredentialUnavailableException();
        }
        BoundProviderCredential? credential = null;
        try
        {
            result.Secret.Use(secret => credential = new(binding, new string(secret)));
            Check(token);
            return credential;
        }
        catch
        {
            credential?.Dispose();
            throw;
        }
    }

    Task<BoundProviderCredential?> ICredentialAuthority.ResolveAsync(ProviderCredentialBinding binding, CancellationToken token) =>
        ResolveAsync(binding, token);

    public override string ToString() => nameof(ConversationAuthorization);
}

internal sealed class ConversationCredentialSource(Func<ICredentialAuthority?> active) : IProviderCredentialSource
{
    public ValueTask<BoundProviderCredential?> ResolveAsync(ProviderCredentialBinding binding, CancellationToken token)
    {
        var action = active() ?? throw new CredentialUnavailableException();
        // Even a synchronous native boundary or settings loader prefix must never run on the dispatcher.
        return new(Task.Run(() => action.ResolveAsync(binding, token)));
    }
}
