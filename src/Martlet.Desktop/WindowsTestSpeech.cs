using System.Runtime.InteropServices;

namespace Martlet.Desktop;

/// <summary>Says a test word with Windows' own speech API (SAPI) so Test hearing can check whether a Thinking model hears.
/// It is never Martlet's voice: replies are spoken by a voice engine. Nothing is downloaded and nothing leaves this PC.</summary>
internal static class WindowsTestSpeech
{
    private const int SpeakAsync = 1, PurgeBeforeSpeak = 2, IsNotXml = 16;
    private const int Pcm24KhzMono16Bit = 26;
    private const int EnglishUnitedStates = 0x409;

    /// <summary>Speaks <paramref name="text"/> into memory as raw 24 kHz mono PCM16 with an installed English voice (the
    /// test words are English), else the default voice. Throws <see cref="InvalidOperationException"/> when Windows speech
    /// can't say it.</summary>
    internal static Task<byte[]> SayAsync(string text, CancellationToken token) => Task.Run(() =>
    {
        object? voice = null, stream = null, format = null;
        try
        {
            voice = Create("SAPI.SpVoice");
            stream = Create("SAPI.SpMemoryStream");
            format = Create("SAPI.SpAudioFormat");
            dynamic speaker = voice, memory = stream, pcm = format;
            pcm.Type = Pcm24KhzMono16Bit;
            memory.Format = format;
            speaker.AudioOutputStream = stream;
            if (English(voice) is { } english) speaker.Voice = english;
            token.ThrowIfCancellationRequested();
            speaker.Speak(text, SpeakAsync | IsNotXml);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (!(bool)speaker.WaitUntilDone(20))
            {
                if (!token.IsCancellationRequested && DateTimeOffset.UtcNow < deadline) continue;
                speaker.Speak(string.Empty, SpeakAsync | PurgeBeforeSpeak);
                speaker.WaitUntilDone(1000);
                token.ThrowIfCancellationRequested();
                throw new InvalidOperationException("Windows speech took too long to say the test word.");
            }
            return memory.GetData() as byte[] is { Length: > 0 } said
                ? said : throw new InvalidOperationException("Windows speech said nothing.");
        }
        catch (Exception error) when (error is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            throw new InvalidOperationException("Test hearing needs Windows speech to say the test word, and it didn't answer on this PC.", error);
        }
        finally
        {
            Release(voice);
            Release(stream);
            Release(format);
        }
    }, token);

    private static object? English(object speaker)
    {
        dynamic voice = speaker;
        dynamic tokens = voice.GetVoices(string.Empty, string.Empty);
        int count = tokens.Count;
        object? first = null;
        for (var i = 0; i < count; i++)
        {
            dynamic item = tokens.Item(i);
            var language = (item.GetAttribute("Language") as string)?.Split(';')[0];
            if (!int.TryParse(language, System.Globalization.NumberStyles.HexNumber, null, out var lcid)) continue;
            if (lcid == EnglishUnitedStates) return item;
            if ((lcid & 0x3FF) == 0x09) first ??= item;
        }
        return first;
    }

    private static object Create(string progId) =>
        Activator.CreateInstance(Type.GetTypeFromProgID(progId, throwOnError: true)!) ??
            throw new COMException($"{progId} is unavailable.");

    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com)) Marshal.FinalReleaseComObject(com);
    }
}
