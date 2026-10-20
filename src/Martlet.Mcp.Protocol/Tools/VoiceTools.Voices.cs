using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Installation;
using Martlet.Doctor;
using Martlet.Mcp.Client;

namespace Martlet.Mcp;

internal sealed partial class McpServer
{
    /// <summary>Voice recognition (Companion › People) and Parakeet as the desktop keeps them in a data directory, read through
    /// their stores (Martlet.Core.Speakers.VoiceChoices and VoiceRoster). Counts only: names and voiceprints are personal and
    /// never returned.</summary>
    private static object VoicesStatus(JsonElement arguments)
    {
        var directory = DataDirectory(arguments);
        // "on", "off", or "on (default)" when the owner never chose (both choices are the household's, the same on every computer).
        string Choice(string file) => Martlet.Core.Speakers.VoiceChoices.Read(directory, file) switch
        {
            true => "on", false => "off", null => "on (default)"
        };
        object roster;
        var path = Path.Combine(directory, Martlet.Core.Speakers.VoiceChoices.RosterFile);
        if (!File.Exists(path)) roster = new { state = "none" };
        else
        {
            try
            {
                var list = Martlet.Core.Speakers.VoiceRoster.Parse(File.ReadAllBytes(path));
                Martlet.Core.Speakers.CompanionNames? companion;
                try { companion = VoiceNamingCheck.SavedCompanion(directory).Names; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException)
                {
                    companion = null;
                }
                companion ??= Martlet.Core.Speakers.CompanionNames.Martlet;
                roster = new
                {
                    state = "loaded", voices = list.Live.Count, named = list.Live.Count(v => v.Named), owner = list.Live.Count(v => v.Owner),
                    // Voices linked to a person's account (docs/ACCOUNTS.md, Voices), how many accounts they link to, and how many
                    // are the account signed in on this PC (accounts\session.json); null before accounts.
                    linked = list.Live.Count(v => v.Account is not null),
                    accounts = list.Live.Select(v => v.Account).OfType<Guid>().Distinct().Count(),
                    yours = SignedInAccount(directory) is { } signedIn ? list.Live.Count(v => v.Account == signedIn) : (int?)null,
                    withLearnedNames = list.Live.Count(v => v.Names.Any(n => n.Source == Martlet.Core.Speakers.VoiceNameSource.Conversation)),
                    // Voices that learned one of the companion's own names; Martlet drops it when it next hears them.
                    withCompanionName = list.Live.Count(v => v.Names.Any(n => n.Source == Martlet.Core.Speakers.VoiceNameSource.Conversation &&
                        companion.Matches(n.Text))),
                    withPlaceholderName = list.Live.Count(v => v.Names.Any(n => n.Source == Martlet.Core.Speakers.VoiceNameSource.Conversation &&
                        Martlet.Core.Speakers.VoiceUpdates.IsNotName(n.Text))),
                    mostNames = list.Live.Select(v => v.Names.Count).DefaultIfEmpty(0).Max(),
                    merged = list.Live.Sum(v => v.MergedVoices), tombstones = list.Voices.Count(v => v.Removed)
                };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException)
            {
                roster = new { state = "unreadable" };
            }
        }
        var speech = Path.Combine(directory, "speech");
        var martlet = MartletDirectory(arguments);
        return new
        {
            recognition = Choice(Martlet.Core.Speakers.VoiceChoices.RecognitionFile),
            // People are always shared with the household: the voice list travels to every paired host of yours whatever
            // "Keep Martlet the same on all my computers" says, and never to a host a friend shares.
            sharing = VoiceSharing(directory),
            included = new
            {
                found = File.Exists(Path.Combine(martlet, "Martlet.Desktop.exe")),
                runtime = Martlet.Sherpa.SherpaComponents.RuntimeDirectory(martlet) is not null,
                voiceModels = Martlet.Sherpa.SherpaComponents.VoiceRecognitionIncluded(martlet)
            },
            parakeet = Martlet.Sherpa.SherpaComponents.InstalledParakeetModels(speech).Count > 0,
            parakeetModels = ParakeetCheck.Status(directory, speech),
            // Companion › People › Where it runs: the Voice recognition list, the places this PC tries and the running desktop's
            // own state (voice-recognition-state.json: this PC's engine, the last recognition, the computers tried).
            recognitionWhere = VoiceRecognitionCheck.Status(directory),
            roster,
            clips = Clips()
        };

        object Clips()
        {
            var root = Path.Combine(directory, "voice-clips");
            int[] counts;
            try { counts = Directory.Exists(root) ? Directory.GetDirectories(root).Select(d => Directory.GetFiles(d, "*.wav").Length).Where(n => n > 0).ToArray() : []; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { counts = []; }
            return new { keep = Choice(Martlet.Core.Speakers.VoiceChoices.ClipsFile), voices = counts.Length, clips = counts.Sum() };
        }
    }

    /// <summary>voices_people: the People check-in tools on the data directory's saved voice list, as a check-in gets them.</summary>
    private static object VoicesPeople(JsonElement arguments)
    {
        var directory = DataDirectory(arguments);
        var tool = OptionalString(arguments, "tool") ?? Martlet.Conversation.PeopleTools.List;
        if (tool is not (Martlet.Conversation.PeopleTools.List or Martlet.Conversation.PeopleTools.Lately))
            throw new ArgumentException($"tool must be {Martlet.Conversation.PeopleTools.List} or {Martlet.Conversation.PeopleTools.Lately}.");
        var path = Path.Combine(directory, Martlet.Core.Speakers.VoiceChoices.RosterFile);
        Martlet.Core.Speakers.VoiceRoster list;
        string state;
        try
        {
            (list, state) = File.Exists(path) ? (Martlet.Core.Speakers.VoiceRoster.Parse(File.ReadAllBytes(path)), "loaded")
                : (Martlet.Core.Speakers.VoiceRoster.Empty, "none");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException)
        {
            return new { state = "unreadable", tool };
        }
        var call = new JsonObject();
        if (OptionalString(arguments, "query") is { } query) call["query"] = query;
        if (OptionalInt(arguments, "minutes") is { } minutes) call["minutes"] = minutes;
        var signedIn = SignedInAccount(directory);
        // As the desktop's LocalVoices.IsYours: linked to the signed-in account, or marked as the owner's before accounts.
        var answer = Martlet.Conversation.PeopleTools.Run(new Martlet.Providers.TextToolCall("mcp", tool, call.ToJsonString()), list,
            voice => signedIn is { } account ? voice.Account == account : voice.Owner, DateTimeOffset.UtcNow);
        return new
        {
            state, tool, signedIn = signedIn is not null, isError = answer.Result.IsError,
            firstLine = answer.Result.Output.Split('\n', 2)[0], shown = answer.Shown.Count, text = answer.Result.Output
        };
    }

    /// <summary>The account signed in on this device (accounts\session.json), or null before accounts or when unreadable.</summary>
    private static Guid? SignedInAccount(string directory)
    {
        try { return Martlet.Core.Accounts.AccountSessionState.Load(directory)?.Current; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException) { return null; }
    }

    /// <summary>voices_status's sharing: the voice list syncs with every paired host of yours (hosts.json entries that aren't
    /// a friend's), whatever cluster-sync.txt says; hosts a friend shares are counted but never used.</summary>
    private static object VoiceSharing(string directory)
    {
        var path = Path.Combine(directory, "hosts.json");
        int hosts = 0, friendHosts = 0;
        if (File.Exists(path))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(path));
                foreach (var host in document.RootElement.GetProperty("hosts").EnumerateArray())
                    if (IsSharedHost(host)) friendHosts++;
                    else hosts++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                return new { state = "hosts unreadable", always = true };
            }
        }
        return new { state = hosts > 0 ? "on" : "no paired hosts yet", always = true, hosts, friendHostsNeverUsed = friendHosts };
    }

    /// <summary>Runs the voice recognition engine bundled in a Martlet folder the way the desktop does (Martlet.Sherpa's
    /// SpeakerEngine with that folder's runtime and models): a generated test tone proves both models load and run, and optional
    /// WAV files show how many voices each has and how alike their main voices are. Scores and counts only.</summary>
    private static object VoicesEngineCheck(JsonElement arguments)
    {
        var martlet = MartletDirectory(arguments);
        var files = new List<string>();
        if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("wavFiles", out var given) && given.ValueKind != JsonValueKind.Null)
        {
            if (given.ValueKind != JsonValueKind.Array || given.GetArrayLength() > 16) throw new ArgumentException("wavFiles must be at most 16 paths.");
            foreach (var item in given.EnumerateArray())
            {
                var path = item.ValueKind == JsonValueKind.String ? item.GetString()! : throw new ArgumentException("wavFiles must be strings.");
                if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Each WAV file must be an absolute path.");
                files.Add(path);
            }
        }
        if (!Martlet.Sherpa.SherpaComponents.VoiceRecognitionIncluded(martlet))
            return new { included = false, runtime = Martlet.Sherpa.SherpaComponents.RuntimeDirectory(martlet) is not null };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var engine = new Martlet.Sherpa.SpeakerEngine(martlet);
        // Three seconds of a gliding harmonic tone: not speech, but enough for both models to load and produce a voiceprint.
        var tone = new float[3 * Martlet.Sherpa.SpeakerEngine.SampleRate];
        for (var i = 0; i < tone.Length; i++)
        {
            var t = i / (double)Martlet.Sherpa.SpeakerEngine.SampleRate;
            var f = 140 + 40 * Math.Sin(2 * Math.PI * 0.5 * t);
            tone[i] = (float)(0.2 * Math.Sin(2 * Math.PI * f * t) + 0.1 * Math.Sin(4 * Math.PI * f * t) + 0.05 * Math.Sin(6 * Math.PI * f * t));
        }
        var probe = engine.Voiceprint(tone);
        var toneAnalysis = engine.Analyze(tone);
        var loadMs = clock.ElapsedMilliseconds;
        var prints = new List<float[]?>();
        var results = new List<object>();
        foreach (var path in files)
        {
            var wave = Martlet.Providers.BoundedWaveAudio.FromWave(File.ReadAllBytes(path));
            if (wave.Format.SampleRate != Martlet.Sherpa.SpeakerEngine.SampleRate) throw new ArgumentException("Each WAV file must be 16 kHz.");
            var bytes = File.ReadAllBytes(path).AsSpan(44);
            var samples = new float[bytes.Length / 2];
            for (var i = 0; i < samples.Length; i++)
                samples[i] = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(i * 2, 2)) / 32768f;
            var started = clock.ElapsedMilliseconds;
            var analysis = engine.Analyze(samples);
            Array.Clear(samples);
            prints.Add(analysis.Speakers.FirstOrDefault()?.Voiceprint ?? analysis.Whole);
            results.Add(new
            {
                file = Path.GetFileName(path), seconds = Math.Round(wave.Duration.TotalSeconds, 2), voicesHeard = analysis.Voices,
                recognizable = analysis.Speakers.Select(s => new { cleanSeconds = s.CleanSeconds, start = s.Start, end = s.End }),
                overlap = analysis.Overlap, speechSeconds = analysis.SpeechSeconds, wholeVoiceprint = analysis.Whole is not null,
                ms = clock.ElapsedMilliseconds - started
            });
        }
        static double? Cosine(float[]? a, float[]? b) => a is null || b is null ? null : Math.Round(a.Zip(b, (x, y) => (double)x * y).Sum(), 3);
        return new
        {
            included = true, loaded = true, voiceprintDimensions = probe?.Length, toneVoices = toneAnalysis.Voices, loadMs,
            files = results,
            similarity = prints.Select(a => prints.Select(b => Cosine(a, b)).ToArray()).ToArray()
        };
    }

    /// <summary>voice_engine_check: Martlet.NodeLinkCheck's voice-engine mode against a live voice service on loopback. A model
    /// that is still loading can take minutes, so it gets longer than the rehearsals.</summary>
    private static async Task<object> VoiceEngineCheckAsync(JsonElement arguments, CancellationToken cancellation)
    {
        var engine = OptionalString(arguments, "engine") ?? "chatterbox";
        if (!VoiceEnginePorts.TryGetValue(engine, out var port))
            throw new ArgumentException($"engine must be one of {string.Join(", ", VoiceEnginePorts.Keys)}.");
        var endpoint = OptionalString(arguments, "endpoint") ?? $"http://127.0.0.1:{port}/";
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp ||
            !System.Net.IPAddress.TryParse(uri.Host, out var address) || !System.Net.IPAddress.IsLoopback(address))
            throw new ArgumentException("endpoint must be a numeric loopback address such as http://127.0.0.1:50083/.");
        var text = OptionalString(arguments, "text") is { Length: > 0 } said ? said : "-";
        var styleDirectory = OptionalString(arguments, "dataDirectory") is { Length: > 0 } data ? data : null;
        if (styleDirectory is not null && !Path.IsPathFullyQualified(styleDirectory))
            throw new ArgumentException("dataDirectory must be an absolute path.");
        string[] command = styleDirectory is null
            ? ["voice-engine", engine, uri.GetLeftPart(UriPartial.Authority) + "/", text]
            : ["voice-engine", engine, uri.GetLeftPart(UriPartial.Authority) + "/", text, styleDirectory];
        return await NodeLinkCheckAsync(TimeSpan.FromMinutes(6), cancellation, command);
    }
}
