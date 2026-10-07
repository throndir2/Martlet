using Martlet.Audio;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Conversation;

public sealed class ConversationRuntime : IAsyncDisposable
{
    internal object Sync { get; } = new();
    internal OpenAiTextGenerationAdapter Text { get; }
    internal IHostTextClient? HostText { get; private init; }
    internal IHostSpeechClient? HostSpeech { get; private init; }
    internal IWindowsVoiceClient? WindowsVoice { get; private init; }
    internal OpenAiSpeechSynthesisAdapter? Speech { get; }
    internal PcmPlaybackSink? Sink { get; }
    internal PlaybackOptions PlaybackOptions { get; }
    internal TimeProvider Clock { get; }
    internal GeneratedSpeechObserver? GeneratedSpeech { get; private init; }
    internal SpokenTextFeed? SpokenText { get; private init; }
    internal CharacterCueFeed? CharacterCues { get; private init; }
    private readonly Func<ChatCompletionsTarget, ChatCompletionsTextGenerationAdapter>? chatFactory;
    private readonly Dictionary<ChatCompletionsTarget, ChatCompletionsTextGenerationAdapter> chatAdapters = [];
    private bool chatClosed;
    private ConversationTurn? active;
    private long epoch, playbackEpoch;
    private bool disposed;
    private Task? disposal;

    public Guid SessionId { get; } = Guid.NewGuid();
    public long CurrentEpoch { get { lock (Sync) return epoch; } }
    public ConversationState State { get { lock (Sync) return active?.Snapshot.State ?? ConversationState.Idle; } }

    /// <summary>How loud Martlet's spoken replies play, 0 (silent) to 1 (full, the default); a reply playing now follows a
    /// change at once. A runtime without speakers ignores it.</summary>
    public double VoiceVolume
    {
        get => Sink?.Volume ?? PcmGain.Full;
        set { if (Sink is not null) Sink.Volume = value; }
    }

    private ConversationRuntime(OpenAiTextGenerationAdapter text, OpenAiSpeechSynthesisAdapter? speech,
        PcmPlaybackSink? sink, PlaybackOptions options, TimeProvider clock,
        Func<ChatCompletionsTarget, ChatCompletionsTextGenerationAdapter>? chatFactory)
    {
        Text = text;
        Speech = speech;
        PlaybackOptions = options;
        Clock = clock;
        Sink = sink;
        this.chatFactory = chatFactory;
    }

    // Merely constructing adapters/sink is passive. No credential resolution, HTTP or device enumeration.
    public static ConversationRuntime Create(IProviderCredentialSource credentials,
        IPlaybackDeviceFactory? devices = null, PlaybackOptions? playbackOptions = null, TimeProvider? clock = null,
        GeneratedSpeechObserver? generatedSpeech = null, IHostTextClient? hostText = null, IHostSpeechClient? hostSpeech = null,
        SpokenTextFeed? spokenText = null, IWindowsVoiceClient? windowsVoice = null, CharacterCueFeed? characterCues = null)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var time = clock ?? TimeProvider.System;
        var options = playbackOptions ?? new();
        var sink = devices is null ? null : new PcmPlaybackSink(devices, options, time);
        return new(OpenAiTextGenerationAdapter.Create(credentials, time),
            devices is null ? null : OpenAiSpeechSynthesisAdapter.Create(credentials, time), sink, options, time,
            target => ChatCompletionsTextGenerationAdapter.Create(target.BaseUrl, target.Keyless ? null : credentials, time))
            { GeneratedSpeech = generatedSpeech, HostText = hostText, HostSpeech = hostSpeech, SpokenText = spokenText, WindowsVoice = windowsVoice,
                CharacterCues = characterCues };
    }

    internal static ConversationRuntime ForFixture(OpenAiTextGenerationAdapter text,
        OpenAiSpeechSynthesisAdapter? speech, IPlaybackDeviceFactory? devices, PlaybackOptions options, TimeProvider clock,
        GeneratedSpeechObserver? generatedSpeech = null,
        Func<ChatCompletionsTarget, ChatCompletionsTextGenerationAdapter>? chat = null, IHostTextClient? hostText = null,
        IHostSpeechClient? hostSpeech = null, IWindowsVoiceClient? windowsVoice = null, SpokenTextFeed? spokenText = null,
        CharacterCueFeed? characterCues = null)
    {
        return new(text, speech, devices is null ? null : new(devices, options, clock), options, clock, chat)
            { GeneratedSpeech = generatedSpeech, HostText = hostText, HostSpeech = hostSpeech, WindowsVoice = windowsVoice,
                SpokenText = spokenText, CharacterCues = characterCues };
    }

    // The saved TTS destination: a paired Martlet host's F5 voice, an installed Windows voice or the OpenAI speech adapter.
    internal ISpeechSynthesisStream StreamSpeech(ProviderRequestContext context, ConversationRequest request,
        BoundedSpeechInput input, SpeechDisclosureAuthorization consent, CancellationToken caller)
    {
        var voice = request.Speech!;
        if (request.WindowsVoice is { } windows)
            return new WindowsVoiceSynthesisStream(WindowsVoice ?? throw new InvalidOperationException(
                "This runtime was not composed with a Windows voice client."), windows, context, voice.Selection,
                input, voice.Limits, consent, Clock, caller);
        if (request.HostSpeech is { } host)
            return new HostSpeechSynthesisStream(HostSpeech ?? throw new InvalidOperationException(
                "This runtime was not composed with a Martlet host speech client."), host, context, voice.Selection,
                input, voice.Limits, consent, Clock, caller);
        return Speech!.Stream(context, voice.Selection, input, voice.Limits, consent, caller);
    }

    // One passive adapter per exact destination; the turn's authorization still binds base URL, model and key.
    // Input is the request's input or a continuation of it after tool calls.
    internal ITextGenerationStream StreamText(ProviderRequestContext context, ConversationRequest request,
        BoundedTextInput input, TextDisclosureAuthorization consent, CancellationToken caller, bool fallback = false,
        bool withoutReasoning = false)
    {
        // A model or server that refused the Thinking steps choice is asked again with its own default.
        var generation = withoutReasoning ? GenerationSettings.WithoutReasoning(request.Generation) : request.Generation;
        if (fallback)
        {
            var second = request.Fallback ?? throw new InvalidOperationException("This request has no Thinking fallback.");
            return ChatAdapter(second.Chat).Stream(context, second.Model, input, request.TextLimits, consent, caller, generation);
        }
        if (request.Host is { } host)
            return new HostTextGenerationStream(HostText ?? throw new InvalidOperationException(
                "This runtime was not composed with a Martlet host text client."), host, context, request.Model,
                input, request.TextLimits, consent, Clock, caller, generation);
        if (request.Chat is not { } target)
            return Text.Stream(context, request.Model, input, request.TextLimits, consent, caller, generation);
        return ChatAdapter(target).Stream(context, request.Model, input, request.TextLimits, consent, caller, generation);
    }

    private ChatCompletionsTextGenerationAdapter ChatAdapter(ChatCompletionsTarget target)
    {
        ChatCompletionsTextGenerationAdapter? adapter;
        lock (Sync)
        {
            ObjectDisposedException.ThrowIf(chatClosed, this);
            if (!chatAdapters.TryGetValue(target, out adapter))
            {
                adapter = (chatFactory ?? throw new InvalidOperationException(
                    "This runtime was not composed with a Chat Completions adapter."))(target);
                chatAdapters[target] = adapter;
            }
        }
        return adapter;
    }

    // This is the explicit new-turn operation. It neither interrupts nor queues behind existing ownership.
    public ConversationTurn Start(ConversationRequest request, IConversationAuthorizationSource authorization,
        CancellationToken cancellationToken = default) => StartCore(request, authorization, null, false, cancellationToken);

    /// <summary>Starts a reply early, before the user's turn is confirmed (Companion › Listening › Start replies early): its
    /// Thinking request streams at once and, with <paramref name="prepareVoice"/>, its first spoken piece is synthesized, but
    /// nothing is shown in the captions, acted by the character, done by a tool or played until
    /// <see cref="ConversationTurn.Release"/> lets it go on as the reply. One that is never released is stopped like any
    /// other turn (<see cref="ConversationTurn.StopAsync"/>), with nothing said or done.</summary>
    public ConversationTurn StartEarly(ConversationRequest request, IConversationAuthorizationSource authorization,
        bool prepareVoice, CancellationToken cancellationToken = default) =>
        StartCore(request, authorization, null, false, cancellationToken, early: true, prepareVoice: prepareVoice);

    public ConversationTurn Retry(ConversationTurn previous, ConversationRequest request,
        IConversationAuthorizationSource authorization, bool acknowledgeEarlierSpeech,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(previous);
        lock (Sync)
        {
            ContractRules.Require(previous.Owner == this && previous.Completion.IsCompleted &&
                previous.OwnershipRelease.IsCompleted, "Retry requires a finished turn from this runtime.");
            ContractRules.Require(!previous.Snapshot.MayHavePlayed || acknowledgeEarlierSpeech,
                "A retry may repeat earlier speech. Explicit acknowledgment is required.");
            return StartCore(request, authorization, previous.TurnId, previous.Snapshot.MayHavePlayed, cancellationToken);
        }
    }

    private ConversationTurn StartCore(ConversationRequest request, IConversationAuthorizationSource authorization,
        Guid? retryOf, bool earlierSpeech, CancellationToken cancellationToken, bool early = false, bool prepareVoice = true)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(authorization);
        request.Validate();
        if (request.Speech is { } speech)
        {
            ContractRules.Require(Sink is not null && Speech is not null,
                "Voice output needs an explicitly composed playback device factory.");
            ContractRules.Require(speech.Limits.MaxInputBytes >= 4,
                "Speech segmentation requires room for one Unicode scalar.");
            ValidateSpeechPrebuffer();
        }
        cancellationToken.ThrowIfCancellationRequested();
        lock (Sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (active is not null && (!active.Completion.IsCompleted || !active.OwnershipRelease.IsCompleted || active.Snapshot.Quarantined))
                throw new InvalidOperationException("The previous turn still owns work or playback is quarantined.");
            ContractRules.Require(epoch < int.MaxValue - 1, "The conversation epoch range is exhausted.");
            active = new(this, request, authorization, ++epoch, retryOf, earlierSpeech, early, prepareVoice);
            active.Begin(cancellationToken);
            return active;
        }
    }

    public Task<ConversationSnapshot?> StopAsync()
    {
        ConversationTurn? captured;
        lock (Sync) captured = active;
        return StopCapturedAsync(captured);
    }

    private static async Task<ConversationSnapshot?> StopCapturedAsync(ConversationTurn? captured) =>
        captured is null ? null : await captured.StopAsync().ConfigureAwait(false);

    internal void InvalidateEpoch(ConversationTurn turn)
    {
        // All callers hold Sync. A delayed Stop on an old handle cannot invalidate a newer turn.
        if (active == turn && epoch == turn.Epoch) epoch++;
    }

    internal PlaybackRun StartPlayback(ConversationTurn turn, CorrelationIds ids, OutputSelection output,
        DateTimeOffset deadline, CancellationToken token)
    {
        lock (Sync)
        {
            turn.CheckActive();
            ContractRules.Require(playbackEpoch < int.MaxValue, "The playback epoch range is exhausted.");
            return Sink!.Start(new(ids, ++playbackEpoch, OpenAiSpeechSynthesisCatalog.PcmFormat, output, deadline)
                { ObserveDeviceClock = GeneratedSpeech?.IsEnabled == true }, token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        ConversationTurn? captured;
        lock (Sync)
        {
            disposed = true;
            captured = active;
        }
        if (captured is not null) await captured.StopAsync().ConfigureAwait(false);
        lock (Sync) disposal ??= DisposeOwnedAsync(captured);
        // Stop is bounded; a noncooperative dependency retains ownership until it actually exits.
        if (disposal.IsCompleted) await disposal.ConfigureAwait(false);
    }

    private async Task DisposeOwnedAsync(ConversationTurn? captured)
    {
        if (captured is not null) await captured.OwnershipRelease.ConfigureAwait(false);
        Text.Dispose();
        ChatCompletionsTextGenerationAdapter[] chat;
        lock (Sync)
        {
            chatClosed = true;
            chat = [.. chatAdapters.Values];
            chatAdapters.Clear();
        }
        foreach (var adapter in chat) adapter.Dispose();
        Speech?.Dispose();
        if (Sink is not null) await Sink.DisposeAsync().ConfigureAwait(false);
    }

    private void ValidateSpeechPrebuffer()
    {
        // The selected raw-PCM adapter emits full 20 ms frames (with a possible shorter final frame).
        var rate = OpenAiSpeechSynthesisCatalog.PcmFormat.SampleRate;
        var frameSamples = rate / 50;
        var prebufferSamples = (long)Math.Ceiling(rate * PlaybackOptions.Prebuffer.TotalSeconds);
        var frames = (prebufferSamples + frameSamples - 1) / frameSamples;
        var capacitySamples = (long)(rate * PlaybackOptions.Capacity.TotalSeconds);
        ContractRules.Require(frames <= PlaybackOptions.MaximumQueuedFrames && frames * frameSamples <= capacitySamples,
            "The playback queue and sample capacity must fit the prebuffer rounded up to full 20 ms speech frames.");
    }

}

internal sealed class ConversationException(ConversationFailure failure) : Exception("The conversation operation could not continue.")
{
    internal ConversationFailure Failure { get; } = failure;
}

internal sealed class MonotonicWindow
{
    private readonly TimeProvider clock;
    private readonly long start;
    private readonly DateTimeOffset startUtc, absolute;
    private readonly TimeSpan duration;
    internal ConversationFailure ExpiryFailure { get; }

    internal MonotonicWindow(TimeProvider clock, TimeSpan duration)
        : this(clock, clock.GetTimestamp(), clock.GetUtcNow(), duration, ConversationFailure.DeadlineExceeded) { }

    private MonotonicWindow(TimeProvider clock, long start, DateTimeOffset startUtc, TimeSpan duration, ConversationFailure failure)
    {
        this.clock = clock;
        this.start = start;
        this.startUtc = startUtc;
        this.duration = duration;
        absolute = startUtc + duration;
        ExpiryFailure = failure;
    }

    internal MonotonicWindow Restrict(DateTimeOffset expiry, ConversationFailure failure)
    {
        var permitted = expiry - startUtc;
        return permitted < duration ? new(clock, start, startUtc, permitted, failure) : this;
    }

    internal TimeSpan Remaining => duration - clock.GetElapsedTime(start);
    internal bool Expired => Remaining <= TimeSpan.Zero || clock.GetUtcNow() >= absolute;
    internal DateTimeOffset Deadline
    {
        get
        {
            var adjusted = clock.GetUtcNow() + Remaining;
            return adjusted < absolute ? adjusted : absolute;
        }
    }
}
