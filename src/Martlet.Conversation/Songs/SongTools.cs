using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Settings;
using Martlet.Core.Singing;
using Martlet.Providers;

namespace Martlet.Conversation;

/// <summary>What sing_song asked for: what the song is about, its lyrics (or null to have them written), its style (or null),
/// and how long it lasts.</summary>
public sealed record SingArguments(string About, string? Lyrics, string? Style, int Seconds);

/// <summary>A song's words and music as written by the lyrics step: its title, style, tempo, key and lyrics with section tags.</summary>
public sealed record WrittenSong(string Title, string Style, int? Bpm, string? Key, string Lyrics);

/// <summary>Martlet sings in conversation: sing_song makes a song in the background (a <see cref="KindName"/> job: lyrics by a
/// background think when none are given, then the song maker with the chosen voice), play_song plays a finished song from
/// anywhere with a musical lead-in, and stop_singing stops it musically. The three tools are offered together, always the same
/// way while singing is offered, so the start of every request stays the same.</summary>
public static class SongTools
{
    public const string SingName = "sing_song", PlayName = "play_song", StopName = "stop_singing";
    /// <summary>The background job kind: song-1, song-2...</summary>
    public const string KindName = "song";
    public const int MaxAboutCharacters = 500;
    /// <summary>How many songs may start in any hour.</summary>
    public const int PerHour = 4;

    /// <summary>One song at a time, <see cref="PerHour"/> an hour, at most 15 minutes each; offered when done.</summary>
    public static BackgroundJobKind Kind { get; } = new(KindName, 1, PerHour, TimeSpan.FromMinutes(15), Offer: true, Doing: "Making a song");

    public const string SingParametersJson =
        """{"type":"object","properties":{"about":{"type":"string","description":"What it's about, with the user's details."},"lyrics":{"type":"string","description":"Only if the user gave the words: lines with [verse]/[chorus] tags."},"style":{"type":"string","description":"Optional genre and mood."},"duration":{"type":"integer","minimum":15,"maximum":180,"description":"Seconds, default 60."}},"required":["about"],"additionalProperties":false}""";

    public const string PlayParametersJson =
        """{"type":"object","properties":{"song_id":{"type":"string","description":"From the note, like 3fa2c19b0d71."},"from":{"type":"string","description":"start, resume, a section, line:N or a time like 1:05."}},"required":["song_id"],"additionalProperties":false}""";

    public const string StopParametersJson =
        """{"type":"object","properties":{"reason":{"type":"string"}},"additionalProperties":false}""";

    public const string SingDescription = "Make a song sung in your voice, in the background (minutes). Tell the user first. One at a time.";
    public const string PlayDescription = "Sing a finished song now, from any point (the band leads in). Only after the user said yes.";
    public const string StopDescription = "Stop singing, musically.";

    /// <summary>sing_song, play_song and stop_singing, always in that order.</summary>
    public static IReadOnlyList<TextToolDefinition> Definitions { get; } =
    [
        new(SingName, SingDescription, SingParametersJson),
        new(PlayName, PlayDescription, PlayParametersJson),
        new(StopName, StopDescription, StopParametersJson)
    ];

    /// <summary>The prompt added to a reply's instructions while the song tools are offered.</summary>
    public static string? Instructions(PromptSettings? prompts) =>
        PromptSettings.Fill(prompts, PromptCatalog.Singing, ("silent", StayQuiet.Marker));

    /// <summary>The notes of what was heard while Martlet sings: it keeps singing and answers only when addressed.</summary>
    public static string? WhileSinging(PromptSettings? prompts, string title, string where) =>
        PromptSettings.Fill(prompts, PromptCatalog.WhileSinging, ("song", title), ("where", where), ("silent", StayQuiet.Marker));

    public static (SingArguments? Arguments, string? Problem) ParseSing(string argumentsJson)
    {
        var arguments = Object(argumentsJson);
        var about = Read(arguments, "about");
        if (arguments is null || string.IsNullOrWhiteSpace(about))
            return (null, "Pass one JSON object with what the song is about, like {\"about\": \"a cheerful song about the user's cat Biscuit\"}.");
        if (about.Length > MaxAboutCharacters) about = about[..MaxAboutCharacters];
        var lyrics = Read(arguments, "lyrics");
        if (string.IsNullOrWhiteSpace(lyrics)) lyrics = null;
        else if (lyrics.Length > SongRequest.MaximumLyricsCharacters)
            return (null, $"The lyrics are too long; keep them under {SongRequest.MaximumLyricsCharacters} characters.");
        else if (SongLyrics.Parse(lyrics).Count < 2) return (null, "Give the lyrics one sung line per line, at least two lines.");
        var style = Read(arguments, "style");
        if (string.IsNullOrWhiteSpace(style)) style = null;
        else if (style.Length > 200) style = style[..200];
        var seconds = SongRequest.DefaultDurationSeconds;
        if (arguments["duration"] is JsonValue duration && duration.TryGetValue<double>(out var value) && double.IsFinite(value))
            seconds = (int)Math.Clamp(Math.Round(value), SongRequest.MinimumDurationSeconds, SongRequest.MaximumDurationSeconds);
        return (new(Clean(about), lyrics is null ? null : CleanLines(lyrics), style is null ? null : Clean(style), seconds), null);
    }

    public static (string? SongId, string? From, string? Problem) ParsePlay(string argumentsJson)
    {
        var arguments = Object(argumentsJson);
        var id = Read(arguments, "song_id");
        if (arguments is null || string.IsNullOrWhiteSpace(id))
            return (null, null, "Pass the song's ID, like {\"song_id\": \"3fa2c19b0d71\", \"from\": \"start\"}.");
        var from = Read(arguments, "from");
        return (id.Trim().ToLowerInvariant(), string.IsNullOrWhiteSpace(from) ? null : Clean(from.Length > 40 ? from[..40] : from), null);
    }

    public static string? ParseStop(string argumentsJson)
    {
        var reason = Read(Object(argumentsJson), "reason");
        return string.IsNullOrWhiteSpace(reason) ? null : Clean(reason.Length > 120 ? reason[..120] : reason);
    }

    private static JsonObject? Object(string json)
    {
        try { return JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static string? Read(JsonObject? arguments, string name) =>
        arguments?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : null;

    private static string Clean(string text) =>
        new string([.. text.Select(c => char.IsControl(c) ? ' ' : c)]).Trim();

    private static string CleanLines(string text) =>
        string.Join('\n', text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Select(line => new string([.. line.Where(c => !char.IsControl(c) || c == '\t')]).Trim())).Trim();

    /// <summary>A few words about the song for the talk window: what it's about, at most 60 characters.</summary>
    public static string Label(string about)
    {
        var line = string.Join(' ', about.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= 60 ? line : line[..57].TrimEnd() + "…";
    }

    /// <summary>What the model is told when the song started: its job, and to tell the user now unless it already did.</summary>
    public static string Started(BackgroundJob job, bool toldUser, bool writing) =>
        JsonSerializer.Serialize(new { status = "started", id = job.Id, time_limit = BackgroundJobs.Duration(job.Kind.TimeLimit) }) + "\n" +
        (writing ? "The lyrics and music are being made now." : "The music is being made for those lyrics now.") +
        (toldUser
            ? " You already told the user, so add nothing more, or at most a few words."
            : " Tell the user now, in one short sentence in character, that you'll sing them a song and need a few minutes to " +
              "work out the lyrics and the beat.") +
        " Keep talking normally meanwhile; a note tells you when it's ready. Don't sing or make up lyrics now.";

    public static string Refused(BackgroundJobStart start) => start.Refusal switch
    {
        "busy" => $"Not started: {start.Message} Tell the user you're still working on the other song; they'll get to hear it soon.",
        "hourly_limit" => $"Not started: {start.Message} Tell the user lightly you need a little break from songwriting.",
        _ => $"Not started: {start.Message ?? "it isn't available right now."} Tell the user you can't make a song right now."
    };

    /// <summary>What the model is told when no song can be made (singing isn't set up, or not reachable now).</summary>
    public static string Unavailable(string reason) =>
        $"Not started: {reason} Tell the user, in character, that you can't sing right now and why, in a few words.";

    /// <summary>What the model is told when the lyrics can't be written in the background (no Deep thinking place).</summary>
    public static string WriteLyricsYourself(string why) =>
        $"Not started: the lyrics can't be written in the background here ({why.TrimEnd('.')}). Write them yourself now, short and " +
        "singable, tagged [verse] and [chorus] with one sung line per line, and call sing_song again with them as lyrics. Don't " +
        "show or say them.";

    /// <summary>The lyrics step's task: what the background think writes.</summary>
    public static string WritingTask(PromptSettings? prompts, SingArguments arguments) =>
        PromptSettings.Fill(prompts, PromptCatalog.SongLyrics, ("about", arguments.About),
            ("style", arguments.Style is { } style ? "\nStyle asked for: " + style : ""),
            ("seconds", arguments.Seconds.ToString(CultureInfo.InvariantCulture)),
            ("lines", Math.Clamp(arguments.Seconds / 5, 4, 32).ToString(CultureInfo.InvariantCulture))) ??
        PromptCatalog.Default(PromptCatalog.SongLyrics);

    /// <summary>Reads the lyrics step's answer (its TITLE, STYLE, BPM, KEY and LYRICS lines; a bare tagged song works too), or
    /// says what was wrong.</summary>
    public static (WrittenSong? Song, string? Problem) ParseWritten(string text, SingArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(text);
        string? title = null, style = null, key = null;
        int? bpm = null;
        var lyrics = new StringBuilder();
        var inLyrics = false;
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim().Trim('*', '`').Trim();
            if (line.StartsWith("```", StringComparison.Ordinal)) continue;
            string? Field(string name) => line.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase) ? line[(name.Length + 1)..].Trim() : null;
            if (Field("TITLE") is { } t) { title = t.Trim('"', '“', '”'); inLyrics = false; continue; }
            if (Field("STYLE") is { } s) { style = s; inLyrics = false; continue; }
            if (Field("BPM") is { } b)
            {
                var digits = new string([.. b.TakeWhile(char.IsAsciiDigit)]);
                if (int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var tempo) &&
                    tempo is >= SongRequest.MinimumBpm and <= SongRequest.MaximumBpm) bpm = tempo;
                inLyrics = false;
                continue;
            }
            if (Field("KEY") is { } k) { key = k; inLyrics = false; continue; }
            if (Field("LYRICS") is { } rest)
            {
                inLyrics = true;
                if (rest.Length > 0) lyrics.Append(rest).Append('\n');
                continue;
            }
            // Without a LYRICS line, the first section tag starts the lyrics.
            if (!inLyrics && line.Length > 2 && line[0] == '[' && line[^1] == ']') inLyrics = true;
            if (inLyrics) lyrics.Append(line).Append('\n');
        }
        var words = CleanLines(lyrics.ToString());
        if (words.Length > SongRequest.MaximumLyricsCharacters) words = words[..SongRequest.MaximumLyricsCharacters];
        var sung = SongLyrics.Parse(words);
        if (sung.Count < 2) return (null, "the lyrics didn't come out right");
        key = key is { Length: > 0 and <= 16 } && key.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '#' or '♯' or '♭') ? key : null;
        style = string.IsNullOrWhiteSpace(style) ? arguments.Style ?? "warm pop, clear vocals" : Clean(style);
        if (style.Length > SongRequest.MaximumStyleCharacters) style = style[..SongRequest.MaximumStyleCharacters];
        var chorus = sung.Where(l => l.Section.StartsWith("chorus", StringComparison.Ordinal)).Select(l => l.Text).FirstOrDefault();
        title = string.IsNullOrWhiteSpace(title) ? chorus ?? sung[0].Text : Clean(title);
        if (title.Length > 80) title = title[..80].TrimEnd();
        return (new(title, style, bpm, key, words), null);
    }

    /// <summary>A finished song as the conversation hears of it: its ID, title, length and its map (sections and lines with
    /// their start times), and to offer it before playing.</summary>
    public static string Ready(StoredSong song)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"Song ready: {song.Id}, \"{song.Title}\", {SongClock.Of(TimeSpan.FromSeconds(song.DurationSeconds))} long");
        if (song.Bpm is { } bpm) text.Append(CultureInfo.InvariantCulture, $", {Math.Round(bpm)} BPM");
        if (song.Fixture) text.Append(" (a FIXTURE test tone, NOT AI music)");
        text.Append(".\nMap:");
        var section = (string?)null;
        for (var i = 0; i < song.Lines.Count; i++)
        {
            var line = song.Lines[i];
            if (line.Section != section)
            {
                section = line.Section;
                text.Append("\n[").Append(section.Length > 0 ? section : "song").Append(']');
            }
            text.Append(CultureInfo.InvariantCulture, $"\n{SongClock.Of(TimeSpan.FromSeconds(line.Start))} line {i + 1}: {line.Text}");
        }
        text.Append("\nOffer it to the user and ask if they want to hear it; play it with play_song (song_id ").Append(song.Id)
            .Append(") only once they say yes.");
        return text.ToString();
    }

    /// <summary>What the model is told when a song starts playing.</summary>
    public static string Playing(StoredSong song, SongStartPlan plan, bool speaking) =>
        JsonSerializer.Serialize(new { status = "playing", song_id = song.Id, from = plan.Target.Describe }) + "\n" +
        (plan.Top
            ? $"\"{song.Title}\" is playing from the top now."
            : $"\"{song.Title}\" starts with {(plan.LeadInBars == 1 ? "a bar" : $"{plan.LeadInBars} bars")} of the band, then you sing " +
              $"{plan.Target.Describe}{(speaking ? " as soon as you finish talking" : "")}.") +
        " Say nothing more now, or at most a few words; you're singing. A note will tell you where it stopped.";

    /// <summary>What the model is told when it stopped the song.</summary>
    public static string Stopped(SongStopRecord? record) => record is null
        ? "You aren't singing anything right now, so there was nothing to stop."
        : JsonSerializer.Serialize(new { status = "stopping", song_id = record.SongId }) + "\n" + record.Note() +
          " It ends musically in a moment. Say a few words if it fits.";
}
