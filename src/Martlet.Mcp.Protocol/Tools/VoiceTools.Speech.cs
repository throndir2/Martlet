using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Installation;
using Martlet.Doctor;
using Martlet.Mcp.Client;

namespace Martlet.Mcp;

internal sealed partial class McpServer
{
    /// <summary>F5's included reference voices, each checked, and the data directory's F5 voice list (the "f5-voices" store
    /// Martlet.Desktop keeps). Own voices are counted, never named; a starter voice is recognized by its clip's SHA-256.</summary>
    private static object VoiceTagsCheck(JsonElement arguments)
    {
        var text = OptionalString(arguments, "text") ?? throw new ArgumentException("'text' is required.");
        var key = OptionalString(arguments, "engine");
        var engine = key is "none" ? null : key is null ? Martlet.Core.Settings.SpeechEngines.Default
            : Martlet.Core.Settings.SpeechEngines.ForKey(key) ?? throw new ArgumentException($"Unknown voice engine '{key}'.");
        var settings = SavedSettings(arguments);
        var prompts = settings?.Prompts;
        var characterTags = OptionalStrings(arguments, "characterTags");
        var breaks = SpeechBreaksFrom(arguments, settings, out var persona);
        var preview = Martlet.Conversation.SpeechTextPreview.For(text, engine, characterTags, breaks);
        return new
        {
            engine = engine?.Key, name = engine?.Name, supportsTags = engine?.SupportsTags ?? false,
            tags = (engine?.Tags ?? []).Select(tag => tag.Text).ToArray(),
            sounds = (engine?.Tags ?? []).Where(tag => tag.Kind == Martlet.Core.Settings.VoiceTagKind.Sound).Select(tag => tag.Text).ToArray(),
            tones = (engine?.Tags ?? []).Where(tag => tag.Kind == Martlet.Core.Settings.VoiceTagKind.Emotion).Select(tag => tag.Text).ToArray(),
            cues = (engine?.Tags ?? []).Select(tag => new { tag = tag.Text, cue = tag.Cue }).ToArray(),
            synonyms = (engine?.Tags ?? []).Where(tag => Martlet.Core.Settings.VoiceTags.Synonyms.ContainsKey(tag.Cue))
                .Select(tag => new { tag = tag.Text, words = Martlet.Core.Settings.VoiceTags.Synonyms[tag.Cue] }).ToArray(),
            prompt = Martlet.Core.Settings.VoiceTags.Instructions(engine, prompts),
            persona = persona?.Name, breaks = Breaks(breaks),
            spoken = preview.Spoken, suppressedPieces = preview.SuppressedPieces, shown = preview.Shown,
            quiet = Martlet.Conversation.StayQuiet.IsQuiet(preview.Shown),
            characterCues = preview.Cues.Select(c => new { piece = c.Piece, tag = c.Tag, offset = c.Offset }).ToArray(),
            acted = preview.Acted.Select(tag => new { tag = tag.Tag, kind = tag.Kind.ToString(), name = tag.Name, written = tag.Written }).ToArray(),
            note = preview.Note
        };
    }

    /// <summary>A data directory's settings.json, or null without a dataDirectory or before anything was saved there.</summary>
    private static Martlet.Core.Settings.AppSettings? SavedSettings(JsonElement arguments) =>
        OptionalString(arguments, "dataDirectory") is { } directory && File.Exists(Path.Combine(directory, "settings.json"))
            ? Martlet.Core.Settings.SettingsJson.Read(File.ReadAllBytes(Path.Combine(directory, "settings.json"))) : null;

    /// <summary>The speech breaks a check uses (Personality › Where the voice pauses): the saved persona named "persona", else the
    /// one Martlet uses, else the defaults; then any stop set in "breaks" on top.</summary>
    private static Martlet.Core.Settings.SpeechBreaks SpeechBreaksFrom(JsonElement arguments,
        Martlet.Core.Settings.AppSettings? settings, out Martlet.Core.Settings.PersonaProfile? persona)
    {
        persona = null;
        var named = OptionalString(arguments, "persona");
        if (settings?.Companion is { } companion)
            persona = named is null ? companion.ActivePersona
                : companion.Personas.FirstOrDefault(p => string.Equals(p.Name, named, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"No persona named '{named}' is saved there.");
        else if (named is not null)
            throw new ArgumentException("'persona' needs a dataDirectory with saved settings.");
        var breaks = persona?.SpokenBreaks ?? Martlet.Core.Settings.SpeechBreaks.Default;
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("breaks", out var set) ||
            set.ValueKind != JsonValueKind.Object)
            return breaks;
        bool Stop(string stop, bool current) =>
            set.TryGetProperty(stop, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : current;
        breaks = breaks with
        {
            Periods = Stop("periods", breaks.Periods),
            QuestionMarks = Stop("questionMarks", breaks.QuestionMarks), ExclamationMarks = Stop("exclamationMarks", breaks.ExclamationMarks),
            ShortEndingWords = set.TryGetProperty("shortEndingWords", out var words) && words.TryGetInt32(out var count) ? count : breaks.ShortEndingWords
        };
        try { breaks.Validate(); }
        catch (Martlet.Core.Contracts.ContractException error) { throw new ArgumentException(error.Message); }
        return breaks;
    }

    private static object BreaksSchema() => new
    {
        type = "object",
        properties = new
        {
            periods = new { type = "boolean" }, questionMarks = new { type = "boolean" },
            exclamationMarks = new { type = "boolean" },
            shortEndingWords = new { type = "integer", minimum = 0, maximum = Martlet.Core.Settings.SpeechBreaks.MaximumShortEndingWords }
        }
    };

    /// <summary>voice_recording_check: Add a voice's conversion of one file, through the production
    /// <see cref="Martlet.Audio.Windows.VoiceRecordingImport"/>, then F5's own reference rules on the WAV it would keep.</summary>
    private static async Task<object> VoiceRecordingCheckAsync(string path, CancellationToken cancellation)
    {
        try
        {
            var ready = await Task.Run(() => Martlet.Audio.Windows.VoiceRecordingImport.Prepare(path, cancellation), cancellation);
            // F5's reference rules decide whether the recording can be a voice by itself (one of several may be shorter).
            string? aloneProblem = null;
            try { Martlet.F5.F5ReferenceAudioFormat.Parse(ready.Wave); }
            catch (Martlet.F5.F5Exception failure) { aloneProblem = failure.Failure.ToString(); }
            // How loud the WAV that Play plays is, so a decode is seen to give real sound; never the audio itself.
            var samples = Martlet.Core.Voices.PcmWaveInfo.Samples(ready.Wave, Martlet.Core.Voices.SpeakingVoiceLibrary.MaximumAudioBytes, out _);
            double peak = 0, energy = 0;
            foreach (var sample in samples)
            {
                var value = sample / 32768d;
                peak = Math.Max(peak, Math.Abs(value));
                energy += value * value;
            }
            static double Dbfs(double level) => level <= 0 ? -96 : Math.Round(Math.Max(-96, 20 * Math.Log10(level)), 1);
            return new
            {
                usable = true, sourceFormat = ready.SourceFormat, sourceChannels = ready.SourceChannels, sourceSampleRate = ready.SourceSampleRate,
                converted = ready.Converted, sampleRate = ready.SampleRate, channels = BitConverter.ToInt16(ready.Wave, 22),
                bitsPerSample = BitConverter.ToInt16(ready.Wave, 34), durationMs = ready.DurationMilliseconds, bytes = ready.Wave.Length,
                sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(ready.Wave)),
                peakDbfs = Dbfs(peak), rmsDbfs = Dbfs(samples.Length == 0 ? 0 : Math.Sqrt(energy / samples.Length)),
                voiceAlone = aloneProblem is null, voiceAloneProblem = aloneProblem,
                engines = Martlet.Core.Settings.SpeechEngines.All
                    .Where(engine => Martlet.Core.Settings.SpeechEngines.ReferenceProblem(engine, ready.DurationMilliseconds) is null)
                    .Select(engine => engine.Key).ToArray(),
                shown = ready.Describe()
            };
        }
        catch (Martlet.Audio.Windows.VoiceRecordingException failure)
        {
            return new { usable = false, problem = failure.Message };
        }
    }
}
