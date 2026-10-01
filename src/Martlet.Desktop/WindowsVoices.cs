using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>An installed Windows (SAPI) voice: its exact token <paramref name="Id"/> (for example TTS_MS_EN-US_ZIRA_11.0),
/// display name and language.</summary>
internal sealed record WindowsVoice(string Id, string Name, string Culture)
{
    public override string ToString() => $"{Name} ({Culture})";
}

/// <summary>The voices installed in Windows, reached through Windows' own speech API (SAPI) over COM. Nothing is downloaded,
/// nothing leaves this PC and there is no Docker or host service. Discovery and synthesis run off the dispatcher.</summary>
internal static partial class WindowsVoices
{
    private const int SpeakAsync = 1, PurgeBeforeSpeak = 2, IsNotXml = 16;
    private const int Pcm24KhzMono16Bit = 26;
    internal const string SampleText = "Hi, I'm Martlet. This is how I sound with this Windows voice.";

    /// <summary>The enabled voices installed in Windows, sorted by name. Empty when none is installed.</summary>
    internal static Task<IReadOnlyList<WindowsVoice>> ListAsync(CancellationToken token) => Task.Run<IReadOnlyList<WindowsVoice>>(() =>
    {
        object? voice = null;
        try
        {
            voice = Create("SAPI.SpVoice");
            var voices = new List<WindowsVoice>();
            foreach (dynamic item in Tokens(voice))
            {
                token.ThrowIfCancellationRequested();
                string id = ShortId((string)item.Id);
                string name = item.GetAttribute("Name") as string ?? id;
                if (id.Length == 0 || voices.Any(v => v.Id == id)) continue;
                voices.Add(new(id, name, Culture(item.GetAttribute("Language") as string)));
            }
            return voices.OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        catch (Exception error) when (error is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            throw new InvalidOperationException("Windows speech didn't answer on this PC.", error);
        }
        finally { Release(voice); }
    }, token);

    /// <summary>The voice to start with: one in this PC's display language, then English (United States), then the first.</summary>
    internal static WindowsVoice? Recommended(IReadOnlyList<WindowsVoice> voices) =>
        voices.FirstOrDefault(v => v.Culture.Equals(CultureInfo.CurrentUICulture.Name, StringComparison.OrdinalIgnoreCase))
        ?? voices.FirstOrDefault(v => v.Culture.Equals("en-US", StringComparison.OrdinalIgnoreCase))
        ?? voices.FirstOrDefault();

    /// <summary>A readable name for a saved voice ID without asking Windows: TTS_MS_EN-US_ZIRA_11.0 becomes "Zira (en-US)".</summary>
    internal static string DisplayName(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "not chosen";
        var match = MicrosoftVoiceId().Match(id);
        return match.Success
            ? $"{CultureInfo.InvariantCulture.TextInfo.ToTitleCase(match.Groups[2].Value.ToLowerInvariant())} ({match.Groups[1].Value[..3].ToLowerInvariant()}{match.Groups[1].Value[3..]})"
            : id;
    }

    /// <summary>Plays a short sample with <paramref name="voiceId"/> on Windows' default speakers, only on the owner's request.</summary>
    internal static Task PreviewAsync(string voiceId, CancellationToken token) => Task.Run(() =>
    {
        object? voice = null;
        try
        {
            voice = Create("SAPI.SpVoice");
            dynamic speaker = voice;
            speaker.Voice = Find(voice, voiceId);
            speaker.Speak(SampleText, SpeakAsync | IsNotXml);
            Wait(voice, token, DateTimeOffset.UtcNow.AddSeconds(20));
        }
        catch (Exception error) when (error is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            throw new InvalidOperationException("Windows couldn't play that voice.", error);
        }
        finally { Release(voice); }
    }, token);

    /// <summary>Speaks <paramref name="text"/> into memory as raw 24 kHz mono PCM16 (never to a device).</summary>
    internal static byte[] Synthesize(string voiceId, string text, DateTimeOffset deadline, CancellationToken token)
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
            speaker.Voice = Find(voice, voiceId);
            token.ThrowIfCancellationRequested();
            speaker.Speak(text, SpeakAsync | IsNotXml);
            Wait(voice, token, deadline);
            dynamic written = memory.Format;
            if ((int)written.Type != Pcm24KhzMono16Bit) throw new WindowsVoiceException(ProviderFailureCode.UnsupportedOutput);
            return memory.GetData() as byte[] ?? [];
        }
        catch (Exception error) when (error is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            throw new WindowsVoiceException(ProviderFailureCode.Server);
        }
        finally
        {
            Release(voice);
            Release(stream);
            Release(format);
        }
    }

    private static void Wait(object speaker, CancellationToken token, DateTimeOffset deadline)
    {
        dynamic voice = speaker;
        while (!(bool)voice.WaitUntilDone(20))
        {
            if (!token.IsCancellationRequested && DateTimeOffset.UtcNow < deadline) continue;
            voice.Speak(string.Empty, SpeakAsync | PurgeBeforeSpeak);
            voice.WaitUntilDone(1000);
            token.ThrowIfCancellationRequested();
            throw new WindowsVoiceException(ProviderFailureCode.DeadlineExceeded);
        }
    }

    private static object Find(object voice, string voiceId)
    {
        var matches = Tokens(voice).Where(t => ShortId((string)((dynamic)t).Id) == voiceId).ToArray();
        if (matches.Length != 1) throw new WindowsVoiceException(ProviderFailureCode.VoiceUnsupported);
        return matches[0];
    }

    private static List<object> Tokens(object speaker)
    {
        dynamic voice = speaker;
        dynamic tokens = voice.GetVoices(string.Empty, string.Empty);
        int count = tokens.Count;
        var list = new List<object>(count);
        for (var i = 0; i < count; i++) list.Add((object)tokens.Item(i));
        return list;
    }

    private static object Create(string progId) =>
        Activator.CreateInstance(Type.GetTypeFromProgID(progId, throwOnError: true)!) ??
            throw new COMException($"{progId} is unavailable.");

    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com)) Marshal.FinalReleaseComObject(com);
    }

    private static string ShortId(string tokenId) => tokenId[(tokenId.LastIndexOf('\\') + 1)..];

    private static string Culture(string? language)
    {
        var first = language?.Split(';')[0];
        try
        {
            return int.TryParse(first, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var lcid)
                ? CultureInfo.GetCultureInfo(lcid).Name : "";
        }
        catch (CultureNotFoundException) { return ""; }
    }

    [GeneratedRegex("^TTS_MS_([A-Z]{2}-[A-Z]{2})_([A-Z]+)_[0-9.]+$", RegexOptions.CultureInvariant)]
    private static partial Regex MicrosoftVoiceId();
}

/// <summary>Speaks reply segments with the installed Windows voice saved on Its voice, on a worker thread, into memory.</summary>
internal sealed class WindowsVoiceClient : IWindowsVoiceClient
{
    private const int ChunkBytes = 9_600;

    public async IAsyncEnumerable<byte[]> StreamAsync(WindowsVoiceTarget target, BoundedSpeechInput input, SpeechSynthesisLimits limits,
        DateTimeOffset deadline, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        WindowsSpeechSetup.InstalledId(target.VoiceId);
        var pcm = await Task.Run(() => WindowsVoices.Synthesize(target.VoiceId, input.Text, deadline, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        if (pcm.Length == 0) throw new WindowsVoiceException(ProviderFailureCode.EmptyAudio);
        if (pcm.Length / 2 > limits.MaxSamples) throw new WindowsVoiceException(ProviderFailureCode.OutputAudioLimit);
        for (var offset = 0; offset < pcm.Length; offset += ChunkBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return pcm.AsSpan(offset, Math.Min(ChunkBytes, pcm.Length - offset)).ToArray();
        }
    }
}
