using System.Buffers.Binary;
using System.Globalization;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Core.Voices;
using Martlet.Providers;
using Martlet.Sherpa;

namespace Martlet.Desktop;

/// <summary>Fills in what a voice recording says, so adding a voice only needs its words checked. It uses speech-to-text the
/// owner already has: Listening's own model when it is Parakeet on this PC (nothing is sent anywhere) or a paired Martlet
/// host's whisper or Parakeet (the recording goes only to that computer, which gets the voice anyway), otherwise a Parakeet model that is
/// downloaded (the most accurate for Windows' display language). A cloud Listening route is never used, since it would upload
/// the recording and may cost money.</summary>
internal sealed class RecordingTranscriber : IDisposable
{
    private const int SampleRate = 16_000;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, Task<string>> transcribe;
    private readonly IDisposable? owned;

    private RecordingTranscriber(string name, int maximumMilliseconds,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task<string>> transcribe, IDisposable? owned = null)
    {
        Name = name;
        MaximumMilliseconds = maximumMilliseconds;
        this.transcribe = transcribe;
        this.owned = owned;
    }

    /// <summary>Who fills in the words, as the dialog says it: "Parakeet on this PC", "Whisper on gpu-pc" or "Parakeet on gpu-pc".</summary>
    internal string Name { get; }

    /// <summary>The longest recording it can transcribe at once.</summary>
    internal int MaximumMilliseconds { get; }

    /// <summary>The Parakeet model this uses, when it is Parakeet.</summary>
    internal string? ParakeetModel { get; private init; }

    /// <summary>The speech-to-text to fill in a recording's words with, or null when none is available without the cloud.
    /// <paramref name="listener"/> is the conversation's Parakeet (already loaded when Listening uses it); otherwise a
    /// downloaded Parakeet in <paramref name="speechRoot"/> loads just for this and unloads when this is disposed.</summary>
    internal static RecordingTranscriber? Choose(IEnumerable<SetupRoute>? routes, ParakeetListener? listener, string? speechRoot,
        CultureInfo? displayLanguage = null)
    {
        var stt = routes?.FirstOrDefault(r => r.Role == SetupRole.Stt);
        if (stt?.RouteType == SetupRouteType.LocalParakeet && listener is not null && listener.Installed(stt.ModelId))
            return Parakeet(listener, stt.ModelId, owned: null);
        if (stt is { RouteType: SetupRouteType.GatewayStt, Enabled: true, GatewaySnapshot: not null } && stt.Consent == stt.Selection() &&
            LiveConversationConfiguration.Target(stt, SetupRouteType.GatewayStt) is { } host)
            return Host(host, stt.ModelId);
        if (speechRoot is not null && SherpaComponents.RuntimeDirectory() is not null &&
            ForRecordings(SherpaComponents.InstalledParakeetModels(speechRoot), displayLanguage ?? CultureInfo.CurrentUICulture) is { } model)
        {
            var own = new ParakeetListener(speechRoot);
            return Parakeet(own, model.Id, own);
        }
        return null;
    }

    /// <summary>The downloaded Parakeet model that fills in a recording's words best: accuracy matters more than speed here, so
    /// v2 for an English display language, otherwise v3 (25 languages); 110M only when it is the one downloaded.</summary>
    internal static ParakeetModel? ForRecordings(IReadOnlyList<ParakeetModel> installed, CultureInfo displayLanguage)
    {
        ParakeetModel[] order = displayLanguage.TwoLetterISOLanguageName == "en"
            ? [ParakeetModels.V2English, ParakeetModels.V3, ParakeetModels.Tdt110mEnglish]
            : [ParakeetModels.V3, ParakeetModels.V2English, ParakeetModels.Tdt110mEnglish];
        return order.FirstOrDefault(installed.Contains);
    }

    private static RecordingTranscriber Parakeet(ParakeetListener listener, string modelId, IDisposable? owned) =>
        new("Parakeet on this PC", ParakeetEngine.MaximumSeconds * 1000,
            async (pcm, token) => (await listener.TranscribeAsync(modelId, pcm, token).ConfigureAwait(false)).Text, owned)
        {
            ParakeetModel = modelId
        };

    private static RecordingTranscriber Host(HostTextTarget target, string modelId)
    {
        var client = new HostTranscriptionClient();
        return new($"{(LocalSpeechSetup.IsParakeetModel(modelId) ? "Parakeet" : "Whisper")} on {target.HostId}", 30_000, (pcm, token) =>
        {
            var ids = new CorrelationIds { SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() };
            return client.TranscribeAsync(target, modelId, pcm, ids, 0, DateTimeOffset.UtcNow.AddSeconds(30), token);
        });
    }

    /// <summary>What 16 kHz mono PCM16 says, trimmed (Discord voice hands over its utterances already converted).</summary>
    internal async Task<string> TranscribePcmAsync(ReadOnlyMemory<byte> pcm16kMono, CancellationToken token) =>
        (await transcribe(pcm16kMono, token).ConfigureAwait(false)).Trim();

    /// <summary>What <paramref name="wave"/> (a mono 16-bit PCM WAV) says, trimmed; empty when no words were heard.</summary>
    internal async Task<string> TranscribeAsync(ReadOnlyMemory<byte> wave, CancellationToken token)
    {
        var pcm = await Task.Run(() => ToSpeech(wave), token).ConfigureAwait(false);
        try { return (await transcribe(pcm, token).ConfigureAwait(false)).Trim(); }
        finally { Array.Clear(pcm); }
    }

    /// <summary>A recording as 16 kHz mono PCM16, the rate both speech-to-text models take.</summary>
    internal static byte[] ToSpeech(ReadOnlyMemory<byte> wave)
    {
        var samples = SpeakingVoiceRecordings.Resample(
            PcmWaveInfo.Samples(wave.Span, SpeakingVoiceLibrary.MaximumAudioBytes, out var info), info.SampleRate, SampleRate);
        var pcm = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
        return pcm;
    }

    /// <summary>Why it couldn't fill in the words, in a few words for the dialog.</summary>
    internal static string Reason(Exception error) => error switch
    {
        HostTextException { Code: ProviderFailureCode.Network } => "the host didn't answer",
        HostTextException { Code: ProviderFailureCode.ModelNotFound } => "listening isn't ready on the host",
        HostTextException { Code: ProviderFailureCode.Authentication or ProviderFailureCode.PermissionDenied } => "the host refused it",
        HostTextException { Code: ProviderFailureCode.InputLimit } => "the recording is too long for it",
        HostTextException { Code: ProviderFailureCode.DeadlineExceeded } => "it took too long",
        HostTextException => "the host couldn't transcribe it",
        ContractException => "the recording isn't a WAV Martlet can read",
        _ => "the speech model couldn't run"
    };

    public void Dispose() => owned?.Dispose();
}
