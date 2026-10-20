using System.Text.Json;
using Martlet.Audio;
using Martlet.Conversation;
using Martlet.Core.Audio;
using Martlet.Providers;
using Martlet.Sherpa;

namespace Martlet.Mcp;

/// <summary>long_talk_check: how long one turn of talking (and one spoken reply) may be, and a long recording heard in pieces the
/// way the conversation hears it. Phrases a Windows voice says (rendered to memory, never played) are put one after another into
/// a recording longer than one speech-to-text request, which the production splitter (<see cref="BoundedWaveAudio.Split"/>) cuts
/// at its pauses. With a Parakeet model downloaded in speechDirectory, each piece is also transcribed through the production
/// <see cref="ParakeetEngine"/> and the joined words compared with what was said. Nothing is downloaded, recorded or played;
/// nothing leaves this PC.</summary>
internal static class LongTalkCheck
{
    internal const int DefaultSeconds = 70;
    internal const int MaximumSeconds = 600;
    // A cut is in a pause when the 20 ms around it is at least this much quieter than the recording as a whole.
    private const double QuietCutDb = -20;
    private const int Rate = 16_000;

    internal static object Limits() => new
    {
        longestTurnSeconds = CaptureOptions.LongestTurn.TotalSeconds,
        captureHardMaximumSeconds = CaptureOptions.HardMaximumDuration.TotalSeconds,
        speechToTextPieceSeconds = TranscriptionLimits.ConversationPiece.TotalSeconds,
        recordingHardMaximumBytes = TranscriptionLimits.HardMaxAudioBytes,
        messageCharacters = BoundedTextInput.HardMaxMessageCharacters,
        replyLifetimeSeconds = ConversationLimits.ReplyLifetime.TotalSeconds,
        replySpokenSentences = new ConversationLimits().MaxSpeechSegments,
        replySpokenTextBytes = new ConversationLimits().MaxSpeechTextBytes,
        messageRecordingSeconds = BoundedTextInput.MessageAudioSeconds
    };

    internal static async Task<object> RunAsync(JsonElement arguments, string martletDirectory, string speechDirectory,
        CancellationToken cancellation)
    {
        var seconds = DefaultSeconds;
        if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("seconds", out var value) && value.ValueKind != JsonValueKind.Null &&
            (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out seconds)))
            throw new ArgumentException($"'seconds' must be a whole number, 30 to {MaximumSeconds}.");
        if (seconds is < 30 or > MaximumSeconds) throw new ArgumentException($"'seconds' must be 30 to {MaximumSeconds}.");
        var phrases = ParakeetCheck.Strings(arguments, "phrases", ParakeetCheck.MaximumPhrases, 200) ?? ParakeetCheck.Phrases;
        var clips = await Task.Run(() => phrases.Select(text => (Text: text, Pcm: UtteranceFilterCheck.Synthesize("speech:" + text))).ToArray(),
            cancellation);

        // The phrases one after another (each with its 0.3 s and 1 s of faint noise) until the recording is long enough.
        var pcm = new List<byte>(seconds * Rate * 2 + Rate * 10);
        var said = new List<string>();
        for (var i = 0; pcm.Count < seconds * Rate * 2; i++)
        {
            pcm.AddRange(clips[i % clips.Length].Pcm);
            said.Add(clips[i % clips.Length].Text);
        }
        var recording = BoundedWaveAudio.FromPcm(new() { SampleRate = Rate, Channels = 1, Encoding = PcmEncoding.Signed16LittleEndian },
            pcm.ToArray());
        var bytes = pcm.ToArray();
        var pieces = recording.Split(TranscriptionLimits.ConversationPiece);
        var whole = Rms(bytes, 0, bytes.Length / 2);
        var at = 0;
        var cuts = new List<object>();
        var covered = 0;
        var quiet = true;
        foreach (var piece in pieces)
        {
            if (at > 0)
            {
                var db = 20 * Math.Log10((Rms(bytes, Math.Max(0, at - Rate / 100), Rate / 50) + 1e-9) / (whole + 1e-9));
                quiet &= db <= QuietCutDb;
                cuts.Add(new { atSeconds = Math.Round(at / (double)Rate, 2), quieterThanRecordingDb = Math.Round(db, 1) });
            }
            at += piece.SamplesPerChannel;
            covered += piece.SamplesPerChannel;
        }
        var fits = pieces.All(p => p.Duration <= TranscriptionLimits.ConversationPiece);
        var everything = covered == recording.SamplesPerChannel;
        var transcribed = await TranscribeAsync(pieces, bytes, string.Join(" ", said), martletDirectory, speechDirectory, cancellation);
        var ok = pieces.Count > 1 && fits && everything && quiet && transcribed is not { Ok: false };
        return new
        {
            ok,
            limits = Limits(),
            recordingSeconds = Math.Round(recording.Duration.TotalSeconds, 2),
            phrasesSaid = said.Count,
            pieces = pieces.Select(p => new { seconds = Math.Round(p.Duration.TotalSeconds, 2) }),
            everyPieceFitsOneRequest = fits,
            piecesCoverEverySample = everything,
            everyCutInAPause = quiet,
            cuts,
            transcription = transcribed?.Report ?? (object)new
            {
                ran = false,
                reason = SherpaComponents.RuntimeDirectory(martletDirectory) is null
                    ? "the sherpa-onnx runtime isn't in martletDirectory (build Martlet.Desktop)"
                    : "no Parakeet model is downloaded in speechDirectory (Companion > Listening > Parakeet in Martlet)"
            }
        };
    }

    private static async Task<(bool Ok, object Report)?> TranscribeAsync(IReadOnlyList<BoundedWaveAudio> pieces, byte[] pcm, string said,
        string martletDirectory, string speechDirectory, CancellationToken cancellation)
    {
        var runtime = SherpaComponents.RuntimeDirectory(martletDirectory);
        var model = SherpaComponents.InstalledParakeetModels(speechDirectory).FirstOrDefault();
        if (runtime is null || model is null) return null;
        using var engine = new ParakeetEngine(speechDirectory, model.Id, runtimeDirectory: runtime);
        await Task.Run(engine.Warm, cancellation);
        var words = new List<string>();
        var at = 0;
        foreach (var piece in pieces)
        {
            var samples = UtteranceFilterCheck.ToFloats(pcm, at * 2, piece.SamplesPerChannel * 2);
            at += piece.SamplesPerChannel;
            var transcript = await Task.Run(() => engine.Transcribe(samples), cancellation);
            if (transcript.Text.Trim() is { Length: > 0 } text) words.Add(text);
        }
        var heard = string.Join(" ", words);
        var (errors, total) = ParakeetCheck.WordErrors(said, heard);
        var rate = total == 0 ? 1 : Math.Round(errors / (double)total, 3);
        var ok = rate <= ParakeetCheck.MaximumWordErrorRate;
        return (ok, new { ran = true, ok, model = model.Id, wordErrorRate = rate, words = total, transcript = heard });
    }

    private static double Rms(byte[] pcm, int firstSample, int samples)
    {
        double sum = 0;
        var end = Math.Min(pcm.Length / 2, firstSample + samples);
        for (var i = firstSample; i < end; i++)
        {
            double sample = BitConverter.ToInt16(pcm, i * 2);
            sum += sample * sample;
        }
        return Math.Sqrt(sum / Math.Max(1, end - firstSample));
    }
}
