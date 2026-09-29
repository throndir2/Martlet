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
internal sealed class ConversationAuthorization : IConversationAuthorizationSource
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
    private int revoked, textRequests, sttRequests, speechRequests, speechBytes;
    private long speechSamples;
    private BoundedTextInput? exactInput;
    internal LiveConversationConfiguration Configuration { get; }
    internal bool Voice { get; }
    internal bool Microphone { get; }
    internal int ReservedRequests { get { lock (gate) return textRequests + sttRequests + speechRequests; } }
    internal CredentialError? CredentialFailure { get; private set; }

    internal ConversationAuthorization(LiveConversationConfiguration configuration, bool voice, bool microphone,
        TimeProvider clock, Func<bool> current, Func<CancellationToken, Task<SettingsLoadResult>> load,
        ICredentialStore vault, CancellationToken caller)
    {
        Configuration = configuration;
        Voice = voice;
        Microphone = microphone;
        this.clock = clock;
        this.current = current;
        this.load = load;
        this.vault = vault;
        this.caller = caller;
        started = clock.GetTimestamp();
        acceptedAt = clock.GetUtcNow();
    }

    internal void BindWorker(CancellationToken token) => worker = token;
    internal void BindInput(BoundedTextInput input) => exactInput = input;
    internal void Revoke() => Interlocked.Exchange(ref revoked, 1);
    internal bool IsCanceled => Volatile.Read(ref revoked) != 0 || caller.IsCancellationRequested ||
        worker.IsCancellationRequested || !current();

    internal void Check(CancellationToken token = default)
    {
        caller.ThrowIfCancellationRequested();
        worker.ThrowIfCancellationRequested();
        token.ThrowIfCancellationRequested();
        if (IsCanceled) throw new LiveActionException("conversation.revoked");
        if (clock.GetUtcNow() >= acceptedAt + LiveConversationConfiguration.ActionLifetime ||
            clock.GetElapsedTime(started) >= LiveConversationConfiguration.ActionLifetime)
            throw new LiveActionException("conversation.expired");
    }

    internal DateTimeOffset Deadline(TimeSpan maximum, bool fromAcceptance = false)
    {
        Check();
        var elapsed = clock.GetElapsedTime(started);
        var remaining = (fromAcceptance ? maximum : LiveConversationConfiguration.ActionLifetime) - elapsed;
        if (remaining <= TimeSpan.Zero) throw new LiveActionException("conversation.expired");
        var absolute = acceptedAt + (fromAcceptance ? maximum : LiveConversationConfiguration.ActionLifetime);
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
            latest.Persona != Configuration.Persona || latest.Memory != Configuration.Memory)
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

    public async ValueTask<AuthorizedTextOperation?> AuthorizeTextAsync(TextAuthorizationAction action, CancellationToken token)
    {
        await ValidateSettingsAsync(token).ConfigureAwait(false);
        var selection = Configuration.TextSelection();
        var expected = new OperationBudget(action.Context.Ids, action.Context.Epoch, ProviderRole.Llm, 1,
            action.Input.Utf8Bytes, action.Input.InputTokenReservation, LiveConversationConfiguration.TextLimits.MaxOutputTokens, 0);
        var expiry = Min(action.Context.Deadline, Deadline(TimeSpan.FromSeconds(45)));
        lock (gate)
        {
            Check(token);
            if (!ReferenceEquals(action.Input, exactInput) || action.Model != selection ||
                action.Limits != LiveConversationConfiguration.TextLimits || action.Budget != expected ||
                textRequests != 0 || !requests.Add(action.Context.Ids.RequestId)) return null;
            textRequests++;
            Ticket(ProviderRole.Llm);
        }
        return new(new(Binding(SetupRole.Llm), action.Model, action.Context.Ids, action.Context.Epoch,
            action.Limits, expiry, true, true), new(action.Budget, expiry));
    }

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
        if (role == SetupRole.Tts && Configuration.HostSpeechTarget() is { } voiceHost)
            return HostSpeechSynthesisStream.Binding(voiceHost);
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

    public override string ToString() => nameof(ConversationAuthorization);
}

internal sealed class ConversationCredentialSource(Func<ConversationAuthorization?> active) : IProviderCredentialSource
{
    public ValueTask<BoundProviderCredential?> ResolveAsync(ProviderCredentialBinding binding, CancellationToken token)
    {
        var action = active() ?? throw new CredentialUnavailableException();
        // Even a synchronous native boundary or settings loader prefix must never run on the dispatcher.
        return new(Task.Run(() => action.ResolveAsync(binding, token)));
    }
}
