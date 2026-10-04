using System.Buffers.Binary;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Core.Voices;
using Martlet.Providers;
using Martlet.Sherpa;

namespace Martlet.Desktop;

/// <summary>Fills in what a voice recording says, so adding a voice only needs its words checked. It uses speech-to-text the
/// owner already has: Listening's own model when it is Parakeet on this PC (nothing is sent anywhere) or a paired Martlet
/// host's whisper (the recording goes only to that computer, which gets the voice anyway), otherwise Parakeet when it is
/// downloaded. A cloud Listening route is never used, since it would upload the recording and may cost money.</summary>
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

    /// <summary>Who fills in the words, as the dialog says it: "Parakeet on this PC" or "Whisper on gpu-pc".</summary>
    internal string Name { get; }

    /// <summary>The longest recording it can transcribe at once.</summary>
    internal int MaximumMilliseconds { get; }

    /// <summary>The speech-to-text to fill in a recording's words with, or null when none is available without the cloud.
    /// <paramref name="listener"/> is the conversation's Parakeet (already loaded when Listening uses it); otherwise a
    /// downloaded Parakeet in <paramref name="speechRoot"/> loads just for this and unloads when this is disposed.</summary>
    internal static RecordingTranscriber? Choose(IEnumerable<SetupRoute>? routes, ParakeetListener? listener, string? speechRoot)
    {
        var stt = routes?.FirstOrDefault(r => r.Role == SetupRole.Stt);
        if (stt?.RouteType == SetupRouteType.LocalParakeet && listener is { Installed: true })
            return Parakeet(listener, owned: null);
        if (stt is { RouteType: SetupRouteType.GatewayStt, Enabled: true, GatewaySnapshot: not null } && stt.Consent == stt.Selection() &&
            LiveConversationConfiguration.Target(stt, SetupRouteType.GatewayStt) is { } host)
            return Host(host, stt.ModelId);
        if (speechRoot is not null && ParakeetEngine.Installed(speechRoot))
        {
            var own = new ParakeetListener(speechRoot);
            return Parakeet(own, own);
        }
        return null;
    }

    private static RecordingTranscriber Parakeet(ParakeetListener listener, IDisposable? owned) =>
        new("Parakeet on this PC", ParakeetEngine.MaximumSeconds * 1000,
            async (pcm, token) => (await listener.TranscribeAsync(SherpaComponents.ParakeetModelId, pcm, token).ConfigureAwait(false)).Text, owned);

    private static RecordingTranscriber Host(HostTextTarget target, string modelId)
    {
        var client = new HostTranscriptionClient();
        return new($"Whisper on {target.HostId}", 30_000, (pcm, token) =>
        {
            var ids = new CorrelationIds { SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() };
            return client.TranscribeAsync(target, modelId, pcm, ids, 0, DateTimeOffset.UtcNow.AddSeconds(30), token);
        });
    }

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
