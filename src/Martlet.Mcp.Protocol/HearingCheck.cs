using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Martlet.Core.Audio;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Mcp;

/// <summary>hearing_check: whether the Thinking model (the saved one, or modelId) can hear the user's recording, whether
/// Companion › Listening › Let Thinking hear my voice is on, and a rehearsal of the production Chat Completions adapter against
/// a fixture endpoint on 127.0.0.1 (canned reply, NOT AI): a synthesized speech-like clip (never microphone audio, nothing
/// played) goes out as an input_audio WAV part beside the transcript, is refused without its own audio permission before any
/// request is sent, and is left out of a transcript-only retry. Nothing leaves loopback.</summary>
internal static partial class HearingCheck
{
    private const string LocalOllama = "http://127.0.0.1:11434/v1";

    internal static async Task<object> RunAsync(string? modelId, string dataDirectory, CancellationToken cancellation)
    {
        if (modelId is not null)
        {
            try { ChatCompletionsSetup.ModelId(modelId); }
            catch (ContractException error) { throw new ArgumentException(error.Message); }
        }
        var loaded = await new SettingsStore(dataDirectory).LoadAsync(cancellation);
        var thinking = loaded.Settings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        var model = modelId ?? thinking?.ModelId ?? "gemini-2.5-flash";
        var modelHearing = HearingModelCatalog.Classify(model);
        // The production decision (HearingModelCatalog.ForRoute): only Chat Completions routes take audio (OpenAI's Responses
        // route and a host's Ollama don't), then what model-abilities.json says, then the name.
        var abilities = ModelAbilities.Load(dataDirectory);
        var saved = thinking is null || modelId is not null ? null : abilities.Find(thinking.Origin, thinking.ModelId);
        var routeHearing = modelId is not null || thinking is null ? (HearingSupport?)null
            : HearingModelCatalog.ForRoute(thinking.RouteType, thinking.Origin, thinking.ModelId, abilities,
                thinking.RouteType == SetupRouteType.ChatCompletions && ChatCompletionsEndpointCatalog.RetiredOn(thinking.Origin, thinking.ModelId) is not null);
        var choice = HearVoiceChoice(dataDirectory);
        var staysOnThisPc = modelId is null && thinking is not null && HearingModelCatalog.StaysOnThisPc(thinking.RouteType, thinking.Origin, thinking.ModelId);
        // Let Thinking hear my voice as replies use it (Martlet.Desktop's TalkPreferences.HearVoiceFor): your own choice wins;
        // never chosen, it is on only while the recording stays on this PC.
        var hearVoice = choice ?? staysOnThisPc;
        var transcribeFirst = TalkChoice(dataDirectory, "TranscribeFirst");
        return new
        {
            model,
            source = modelId is not null ? "argument" : thinking is not null ? "settings" : "default",
            settings = loaded.State switch { SettingsLoadState.Loaded => "loaded", SettingsLoadState.FirstRun => "none", _ => "unreadable" },
            routeType = modelId is null ? thinking?.RouteType?.ToString() : null,
            localOllama = thinking is not null && modelId is null && thinking.Origin == LocalOllama,
            modelHearing = modelHearing.ToString(),
            routeHearing = routeHearing?.ToString(),
            savedAbility = saved is null ? null : new { saved.Hears, saved.Sees, saved.Source, saved.CheckedAt },
            hearVoice,
            hearVoiceChoice = choice switch { true => "on", false => "off", _ => "unset" },
            staysOnThisPc,
            hearVoiceWhy = choice switch
            {
                true => "you turned it on",
                false => "you turned it off",
                _ when staysOnThisPc => "never chosen: on because the recording stays on this PC",
                _ => "never chosen: off because Thinking isn't Ollama on this PC, so the recording would or could leave it (tick it to allow)"
            },
            // Companion › Listening › When Thinking can hear you (shown while Thinking hears): straight (the default) or transcribe
            // first. Straight applies to always listening when the route hears; push-to-talk and messages with what the PC
            // played, said over Martlet or for Home Assistant's Assist are transcribed first.
            voicePath = transcribeFirst ? "transcribeFirst" : "straight",
            straightApplies = hearVoice && !transcribeFirst && routeHearing == HearingSupport.Supported,
            lastTurn = LastTurn(dataDirectory),
            fixture = await FixtureAsync(model, cancellation)
        };
    }

    // talk-preferences.json (Martlet.Desktop's TalkPreferences): TranscribeFirst is off unless saved on.
    private static bool TalkChoice(string directory, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "talk-preferences.json")));
            return document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    // talk-preferences.json's HearVoice as TalkPreferences.Load reads it: true or false as chosen, null when never chosen (missing,
    // null, or a false saved before version 4, when off was only the default).
    private static bool? HearVoiceChoice(string directory)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "talk-preferences.json")));
            var root = document.RootElement;
            var version = root.TryGetProperty("Version", out var v) && v.TryGetInt32(out var number) ? number : 0;
            if (!root.TryGetProperty("HearVoice", out var value)) return null;
            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False when version >= 4 => false,
                _ => null
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>Which way the newest spoken reply's message went to Thinking, from the desktop log (never what was said): its
    /// "Voice path:" line, and for a straight one the "Background transcript" line (how long after the reply started the words
    /// were ready, how long speech-to-text took) and the "Straight to Thinking:" line (where the words went).</summary>
    internal static object? LastTurn(string dataDirectory)
    {
        var directory = Martlet.Diagnostics.LocalLogs.Directory(dataDirectory);
        if (!Directory.Exists(directory)) return null;
        var lines = Martlet.Diagnostics.LocalLogs.Read(directory, Martlet.Diagnostics.LocalLogs.ThisDeviceId())
            .Where(r => r.Component == "desktop").OrderBy(r => r.At).ToArray();
        var path = lines.LastOrDefault(r => r.Message.StartsWith("Voice path: ", StringComparison.Ordinal));
        if (path is null) return null;
        var after = lines.Where(r => r.At >= path.At).ToArray();
        var transcript = after.FirstOrDefault(r => r.Message.StartsWith("Background transcript", StringComparison.Ordinal));
        var kept = after.FirstOrDefault(r => r.Message.StartsWith("Straight to Thinking: ", StringComparison.Ordinal));
        // The quick check of something short (Not words: the reply was dropped before it played; Not words, too late: labeled).
        var quick = after.FirstOrDefault(r => r.Message.StartsWith("Not words", StringComparison.Ordinal));
        var timing = transcript is null ? null : TranscriptTiming().Match(transcript.Message);
        double? Ms(string group) => timing is { Success: true } && timing.Groups[group].Success &&
            double.TryParse(timing.Groups[group].Value, System.Globalization.CultureInfo.InvariantCulture, out var ms) ? ms : null;
        var before = timing is { Success: true } && timing.Groups["when"].Value == "before";
        return new
        {
            at = path.At,
            path = path.Message.StartsWith("Voice path: straight", StringComparison.Ordinal) ? "straight" : "transcribeFirst",
            line = path.Message,
            transcriptReadyAfterReplyStartMs = Ms("after") is { } readyMs ? before ? -readyMs : readyMs : (double?)null,
            speechToTextMs = Ms("stt"),
            transcriptLine = transcript?.Message,
            wordsLine = kept?.Message,
            notWords = quick is null ? null : quick.Message.StartsWith("Not words, too late", StringComparison.Ordinal) ? "tooLate" : "dropped",
            notWordsLine = quick?.Message
        };
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"ready (?<after>\d+) ms (?<when>after|before) the reply started \(speech-to-text (?<stt>\d+) ms")]
    private static partial System.Text.RegularExpressions.Regex TranscriptTiming();

    private static async Task<object> FixtureAsync(string model, CancellationToken cancellation)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var baseUrl = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1";
        var requests = new List<byte[]>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var serving = ServeAsync(listener, requests, stop.Token);
        try
        {
            using var adapter = ChatCompletionsTextGenerationAdapter.Create(baseUrl);
            var clip = Clip();
            var input = new BoundedTextInput("What did I just say, and how did I sound?", "Fixture check.", audio: clip);
            var heard = await AskAsync(adapter, baseUrl, model, input, true, cancellation);
            var sent = Count(requests);
            var refused = await AskAsync(adapter, baseUrl, model, input, false, cancellation);
            var sentWithoutConsent = Count(requests) - sent;
            var transcript = await AskAsync(adapter, baseUrl, model, input.WithoutAudio(), false, cancellation);
            byte[][] bodies;
            lock (requests) bodies = [.. requests];
            var withRecording = bodies.Length > 0 ? Describe(bodies[0]) : null;
            var transcriptOnly = bodies.Length > 1 ? Describe(bodies[^1]) : null;
            var ok = heard.Outcome == "Completed" && withRecording is { HasText: true, HasAudio: true, WavValid: true } &&
                refused.Failure == "ConsentMissing" && sentWithoutConsent == 0 &&
                transcript.Outcome == "Completed" && transcriptOnly is { HasAudio: false, PlainText: true };
            return new
            {
                ok,
                endpoint = baseUrl,
                clipSeconds = Math.Round(clip.Duration.TotalSeconds, 2),
                withRecording = new
                {
                    heard.Outcome, heard.Failure, heard.Reply, contentParts = withRecording?.Parts, audioFormat = withRecording?.Format,
                    wavValid = withRecording?.WavValid, audioSeconds = withRecording?.Seconds, audioBytes = withRecording?.Bytes,
                    transcriptIncluded = withRecording?.HasText
                },
                withoutAudioPermission = new { refused.Outcome, refused.Failure, requestsSent = sentWithoutConsent },
                transcriptOnly = new { transcript.Outcome, transcript.Failure, plainText = transcriptOnly?.PlainText, audio = transcriptOnly?.HasAudio },
                requests = bodies.Length
            };
        }
        finally
        {
            stop.Cancel();
            listener.Stop();
            try { await serving; } catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException or IOException) { }
        }
    }

    private static int Count(List<byte[]> requests) { lock (requests) return requests.Count; }

    private sealed record Asked(string? Outcome, string? Failure, string Reply);

    private static async Task<Asked> AskAsync(ChatCompletionsTextGenerationAdapter adapter, string baseUrl, string model,
        BoundedTextInput input, bool allowAudio, CancellationToken cancellation)
    {
        var ids = new CorrelationIds { SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() };
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        var limits = new TextGenerationLimits();
        var selection = new TextModelSelection(ChatCompletionsSetup.Alias, model);
        var authorization = new TextDisclosureAuthorization(new(ChatCompletionsSetup.BaseUri(baseUrl), ProviderRole.Llm, model),
            selection, ids, 1, limits, deadline, true, true, allowAudioDisclosure: allowAudio);
        var stream = adapter.Stream(new() { Ids = ids, Epoch = 1, Deadline = deadline }, selection, input, limits, authorization, cancellation);
        var reply = new StringBuilder();
        await foreach (var item in stream.WithCancellation(cancellation))
            if (item.Kind == ProviderEventKind.TextDelta) reply.Append(item.Text);
        return new(stream.Result?.Outcome.ToString(), stream.Result?.Failure?.Code.ToString(), reply.ToString());
    }

    private sealed record Sent(string[] Parts, bool HasText, bool HasAudio, bool PlainText, string? Format, bool WavValid,
        double? Seconds, int? Bytes);

    // The user message of a captured request: its content parts and the attached WAV's header.
    private static Sent Describe(byte[] body)
    {
        using var document = JsonDocument.Parse(body);
        var messages = document.RootElement.GetProperty("messages");
        var content = messages[messages.GetArrayLength() - 1].GetProperty("content");
        if (content.ValueKind == JsonValueKind.String) return new(["text"], true, false, true, null, false, null, null);
        var parts = content.EnumerateArray().Select(p => p.GetProperty("type").GetString() ?? "").ToArray();
        string? format = null;
        byte[]? wave = null;
        foreach (var part in content.EnumerateArray())
            if (part.GetProperty("type").GetString() == "input_audio")
            {
                var audio = part.GetProperty("input_audio");
                format = audio.GetProperty("format").GetString();
                wave = Convert.FromBase64String(audio.GetProperty("data").GetString() ?? "");
            }
        var valid = wave is { Length: > 44 } && wave.AsSpan(0, 4).SequenceEqual("RIFF"u8) && wave.AsSpan(8, 4).SequenceEqual("WAVE"u8);
        double? seconds = valid ? Math.Round((wave!.Length - 44) / (2.0 * BitConverter.ToInt32(wave, 24)), 2) : null;
        return new(parts, parts.Contains("text"), wave is not null, false, format, valid, seconds, wave?.Length);
    }

    // A minimal HTTP/1.1 endpoint: records each request body and streams one canned Chat Completions reply.
    private static async Task ServeAsync(TcpListener listener, List<byte[]> requests, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellation);
            await using var stream = client.GetStream();
            var body = await ReadRequestAsync(stream, cancellation);
            lock (requests) requests.Add(body);
            const string chunk = "{\"id\":\"fixture\",\"object\":\"chat.completion.chunk\",\"model\":\"fixture\",\"choices\":[{\"index\":0,";
            var events = "data: " + chunk + "\"delta\":{\"role\":\"assistant\",\"content\":\"Fixture reply (not AI).\"},\"finish_reason\":null}]}\n\n" +
                "data: " + chunk + "\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
            var payload = Encoding.UTF8.GetBytes(events);
            var head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\n" +
                $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head, cancellation);
            await stream.WriteAsync(payload, cancellation);
            await stream.FlushAsync(cancellation);
        }
    }

    internal static async Task<byte[]> ReadRequestAsync(NetworkStream stream, CancellationToken cancellation)
    {
        var buffer = new MemoryStream();
        var one = new byte[8192];
        int headerEnd;
        while ((headerEnd = IndexOf(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), "\r\n\r\n"u8)) < 0)
        {
            var read = await stream.ReadAsync(one, cancellation);
            if (read == 0) throw new IOException("The request ended early.");
            buffer.Write(one, 0, read);
        }
        var headers = Encoding.ASCII.GetString(buffer.GetBuffer(), 0, headerEnd).Split("\r\n");
        var length = headers.Select(h => h.Split(':', 2)).Where(h => h.Length == 2 &&
            h[0].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)).Select(h => int.Parse(h[1].Trim())).FirstOrDefault(-1);
        var chunked = headers.Any(h => h.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase) && h.Contains("chunked"));
        var rest = new MemoryStream();
        rest.Write(buffer.GetBuffer(), headerEnd + 4, (int)buffer.Length - headerEnd - 4);
        async Task<bool> More()
        {
            var read = await stream.ReadAsync(one, cancellation);
            if (read == 0) return false;
            rest.Write(one, 0, read);
            return true;
        }
        if (!chunked)
        {
            while (rest.Length < length) if (!await More()) break;
            return rest.ToArray()[..Math.Max(0, Math.Min((int)rest.Length, length))];
        }
        // Chunked: size lines and data until the zero-size chunk.
        var body = new MemoryStream();
        var at = 0;
        while (true)
        {
            int line;
            while ((line = IndexOf(rest.GetBuffer().AsSpan(at, (int)rest.Length - at), "\r\n"u8)) < 0) if (!await More()) return body.ToArray();
            var size = Convert.ToInt32(Encoding.ASCII.GetString(rest.GetBuffer(), at, line).Split(';')[0].Trim(), 16);
            at += line + 2;
            if (size == 0) return body.ToArray();
            while (rest.Length - at < size + 2) if (!await More()) return body.ToArray();
            body.Write(rest.GetBuffer(), at, size);
            at += size + 2;
        }
    }

    private static int IndexOf(ReadOnlySpan<byte> data, ReadOnlySpan<byte> value) => data.IndexOf(value);

    // 1.5 seconds of a 16 kHz speech-like signal: a 140 Hz pulse train shaped by vowel formants, three "syllables".
    private static BoundedWaveAudio Clip()
    {
        const int rate = 16_000;
        double[][] vowels = [[730, 1090, 2440], [270, 2290, 3010], [570, 840, 2410]];
        var pcm = new byte[rate * 3 / 2 * 2];
        for (var i = 0; i < pcm.Length / 2; i++)
        {
            var t = (double)i / rate;
            var syllable = (int)(t * 2);
            var phase = t * 2 - syllable;
            var envelope = phase < 0.8 ? Math.Sin(Math.PI * phase / 0.8) : 0;
            var vowel = vowels[syllable % vowels.Length];
            double value = 0;
            for (var harmonic = 1; harmonic * 140 < 4000; harmonic++)
                value += vowel.Sum(f => 1 / (1 + Math.Pow((harmonic * 140.0 - f) / 90, 2))) / harmonic * Math.Sin(2 * Math.PI * harmonic * 140 * t);
            BitConverter.TryWriteBytes(pcm.AsSpan(i * 2), (short)Math.Clamp(value * envelope * 6000, short.MinValue, short.MaxValue));
        }
        return BoundedWaveAudio.FromPcm(new PcmFormat { SampleRate = rate, Channels = 1, Encoding = PcmEncoding.Signed16LittleEndian }, pcm);
    }
}
