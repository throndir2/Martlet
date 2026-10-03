using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Voices;

/// <summary>Why the owner may have Martlet speak with a recording: it is their own voice, its speaker gave explicit
/// permission, or it is a published recording anyone may use (Martlet's starter voices).</summary>
public enum SpeakingVoiceRights { OwnVoice, ExplicitPermission, PublishedSample }

/// <summary>One voice Martlet can speak with: a name, a short recording (identified by its SHA-256; the bytes travel
/// separately) and its exact transcript. <see cref="Id"/> is the reference revision, SHA-256 of the recording's SHA-256
/// followed by the transcript's, so the same recording and words are the same voice on every computer. <see cref="Removed"/>
/// is a tombstone, so the voice does not return from an older copy. A voice made from several recordings keeps them joined
/// with a short pause as its one recording (and their transcripts joined with spaces as its transcript), with
/// <see cref="Clips"/> saying where each lies, so engines that learn from several recordings get each one.</summary>
public sealed record SpeakingVoice
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public string? Transcript { get; init; }
    public string? AudioSha256 { get; init; }
    public int DurationMilliseconds { get; init; }
    public SpeakingVoiceRights? Rights { get; init; }
    /// <summary>Where the recording comes from, for voices that say (the starter voices).</summary>
    public string? Note { get; init; }
    /// <summary>The recordings the voice was made from, in order, when there are several (null for one recording).</summary>
    public IReadOnlyList<SpeakingVoiceClip>? Clips { get; init; }
    /// <summary>The joined recording's sample rate, which places <see cref="Clips"/>; only with clips.</summary>
    public int? SampleRate { get; init; }
    /// <summary>When the voice joined the list; the list shows voices in this order.</summary>
    public DateTimeOffset AddedAt { get; init; }
    public bool Removed { get; init; }
    public required long Revision { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public required string UpdatedBy { get; init; }

    /// <summary>How long each of <see cref="Clips"/> is, or null for a voice of one recording.</summary>
    [JsonIgnore]
    public IReadOnlyList<int>? ClipMilliseconds => Clips is { } clips && SampleRate is int rate && rate > 0
        ? clips.Select(c => (int)Math.Ceiling(c.SampleCount * 1000d / rate)).ToArray()
        : null;

    internal string Content => JsonSerializer.Serialize(this with { Revision = 0, UpdatedAt = default, UpdatedBy = "" }, SpeakingVoiceLibrary.Json);
}

/// <summary>One of the recordings a voice was made from: where it lies in the voice's joined recording, in samples at the
/// voice's <see cref="SpeakingVoice.SampleRate"/>, and its exact words.</summary>
public sealed record SpeakingVoiceClip
{
    public required string Transcript { get; init; }
    public required int StartSample { get; init; }
    public required int SampleCount { get; init; }
}

/// <summary>The voice Martlet speaks with on every computer, as a last-writer-wins register.</summary>
public sealed record SpeakingVoiceChoice
{
    public required string VoiceId { get; init; }
    public required long Revision { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public required string UpdatedBy { get; init; }
}

/// <summary>The voices Martlet speaks with, shared by every paired Martlet host and desktop so each computer has every
/// recording before it is needed: a host speaks without receiving the recording with each reply, and any computer can be the
/// companion with the same voices. There are no built-in voices: a new list starts with the starter voices at revision 1,
/// so removing one anywhere wins everywhere, and they are chosen and removed like any other. One last-writer-wins entry per
/// voice and one for the chosen voice; <see cref="Merge"/> is commutative, associative and idempotent. Revisions are hybrid
/// clocks like the cluster plan's.</summary>
public sealed record SpeakingVoiceLibrary
{
    public const int SchemaVersion1 = 1;
    public const int MaximumBytes = 1_048_576;
    public const int MaximumVoices = 32;
    public const int MaximumTombstones = 64;
    public const int MaximumNameLength = 80;
    public const int MaximumNameUtf8Bytes = 160;
    public const int MaximumTranscriptLength = 4096;
    public const int MaximumTranscriptUtf8Bytes = 8192;
    public const int MaximumNoteLength = 240;
    public const int MinimumDurationMilliseconds = 1_000;
    public const int MaximumDurationMilliseconds = 30_000;
    /// <summary>The largest recording a voice may have (a mono 16-bit PCM WAV).</summary>
    public const int MaximumAudioBytes = 4 * 1024 * 1024;
    /// <summary>The most recordings one voice may be made from, the shortest each may be, and the pause between them in
    /// the joined recording.</summary>
    public const int MaximumClips = 10;
    public const int MinimumClipMilliseconds = 500;
    public const int ClipPauseMilliseconds = 500;
    public static readonly IReadOnlyList<int> SampleRates = [16_000, 22_050, 24_000, 44_100, 48_000];
    /// <summary>The writer and revision of starter entries: every computer writes the same entry, and any change wins.</summary>
    public const string StarterWriter = "martlet";
    public const long StarterRevision = 1;
    private const long MaximumRevision = long.MaxValue / 4;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) },
        MaxDepth = 8
    };

    public required int SchemaVersion { get; init; }
    public required IReadOnlyList<SpeakingVoice> Voices { get; init; }
    public SpeakingVoiceChoice? Chosen { get; init; }

    public static SpeakingVoiceLibrary Empty { get; } = new() { SchemaVersion = SchemaVersion1, Voices = [] };

    /// <summary>The voices that are not removed, in the order they joined the list.</summary>
    [JsonIgnore]
    public IReadOnlyList<SpeakingVoice> Live => Voices.Where(v => !v.Removed).OrderBy(v => v.AddedAt).ThenBy(v => v.Id, StringComparer.Ordinal).ToArray();

    [JsonIgnore]
    public long Revision => Math.Max(Voices.Select(v => v.Revision).DefaultIfEmpty(0).Max(), Chosen?.Revision ?? 0);

    /// <summary>The live voice Martlet speaks with on every computer; null when none was chosen or it was removed.</summary>
    [JsonIgnore]
    public SpeakingVoice? ChosenVoice => Chosen is { } chosen && Find(chosen.VoiceId) is { Removed: false } voice ? voice : null;

    public long NextRevision(DateTimeOffset now) => Math.Max(Revision + 1, now.ToUnixTimeMilliseconds());

    public SpeakingVoice? Find(string id) => Voices.FirstOrDefault(v => v.Id == id);

    /// <summary>A recording's voice ID (its reference revision): SHA-256 of the recording's SHA-256 bytes followed by the
    /// SHA-256 of the transcript's UTF-8 bytes, lower-case hex.</summary>
    public static string ReferenceId(string audioSha256, string transcript)
    {
        ContractRules.Require(IsSha256(audioSha256), "A recording's SHA-256 is invalid.");
        Span<byte> material = stackalloc byte[64];
        Convert.FromHexString(audioSha256).CopyTo(material);
        SHA256.HashData(StrictUtf8.GetBytes(transcript)).CopyTo(material[32..]);
        return Convert.ToHexStringLower(SHA256.HashData(material));
    }

    /// <summary>A live voice ready to add: its ID follows from the recording and transcript. A voice made from several
    /// recordings passes <paramref name="clips"/> and the joined recording's <paramref name="sampleRate"/>.</summary>
    public static SpeakingVoice Voice(string name, string transcript, string audioSha256, int durationMilliseconds,
        SpeakingVoiceRights rights, string? note, DateTimeOffset addedAt, long revision, string by, DateTimeOffset updatedAt,
        IReadOnlyList<SpeakingVoiceClip>? clips = null, int? sampleRate = null) => new()
    {
        Id = ReferenceId(audioSha256, transcript), Name = name, Transcript = transcript, AudioSha256 = audioSha256.ToLowerInvariant(),
        DurationMilliseconds = durationMilliseconds, Rights = rights, Note = note, AddedAt = addedAt.ToUniversalTime(),
        Clips = clips is { Count: > 0 } ? clips.ToArray() : null, SampleRate = clips is { Count: > 0 } ? sampleRate : null,
        Revision = revision, UpdatedAt = updatedAt.ToUniversalTime(), UpdatedBy = by
    };

    /// <summary>The transcript of a voice made from several recordings: theirs, in order, joined with spaces.</summary>
    public static string JoinedTranscript(IEnumerable<SpeakingVoiceClip> clips) => string.Join(" ", clips.Select(c => c.Transcript));

    /// <summary>Adds a voice (or renames the live voice it already is). Throws <see cref="ContractException"/> with
    /// <see cref="ErrorCode.PayloadTooLarge"/> when the list already has <see cref="MaximumVoices"/> voices.</summary>
    public SpeakingVoiceLibrary Add(string name, string transcript, string audioSha256, int durationMilliseconds,
        SpeakingVoiceRights rights, string by, DateTimeOffset now, string? note = null,
        IReadOnlyList<SpeakingVoiceClip>? clips = null, int? sampleRate = null)
    {
        ContractRules.Require(IsName(name), "Give the voice a name of at most 80 characters on one line.");
        ContractRules.Require(IsTranscript(transcript), "Type exactly what the recording says (at most 4,096 characters).");
        var id = ReferenceId(audioSha256, transcript);
        if (Find(id) is { Removed: false } existing)
        {
            // The same recording and words: rename it, and give it its recordings' places if a copy without them (such as
            // this PC's store joining the list before the places were saved) got there first.
            var placing = existing.Clips is null && clips is { Count: > 0 };
            if (existing.Name == name && !placing) return this;
            var updated = existing with { Name = name, Revision = NextRevision(now), UpdatedAt = now.ToUniversalTime(), UpdatedBy = by };
            if (placing)
            {
                updated = updated with { Clips = clips!.ToArray(), SampleRate = sampleRate };
                Validate(updated);
            }
            return Put(updated);
        }
        ContractRules.Require(Live.Count < MaximumVoices, "The voice list is full. Remove a voice first.", ErrorCode.PayloadTooLarge);
        var voice = Voice(name, transcript, audioSha256, durationMilliseconds, rights, note, now, NextRevision(now), by, now, clips, sampleRate);
        Validate(voice);
        return Put(voice);
    }

    /// <summary>Adds each starter voice the list has never had (live or removed) as revision <see cref="StarterRevision"/>
    /// by <see cref="StarterWriter"/>, listed first in the given order. Every computer writes identical entries, so they
    /// merge as one, and the owner's removal of one (a newer revision) wins on every computer.</summary>
    public SpeakingVoiceLibrary Seed(IEnumerable<(string Name, string Transcript, string AudioSha256, int DurationMilliseconds, string? Note)> starters)
    {
        var library = this;
        var index = 0;
        foreach (var (name, transcript, sha256, duration, note) in starters)
        {
            var at = DateTimeOffset.UnixEpoch.AddSeconds(index++);
            var voice = Voice(name, transcript, sha256, duration, SpeakingVoiceRights.PublishedSample, note, at, StarterRevision, StarterWriter, at);
            if (library.Find(voice.Id) is not null || library.Live.Count >= MaximumVoices) continue;
            Validate(voice);
            library = library.Put(voice);
        }
        return library;
    }

    /// <summary>Removes a voice everywhere: only a tombstone remains.</summary>
    public SpeakingVoiceLibrary Remove(string id, string by, DateTimeOffset now) => Find(id) is { Removed: false } voice
        ? Put(new SpeakingVoice { Id = voice.Id, Removed = true, Revision = NextRevision(now), UpdatedAt = now.ToUniversalTime(), UpdatedBy = by })
        : this;

    /// <summary>Makes a live voice the one Martlet speaks with on every computer.</summary>
    public SpeakingVoiceLibrary Choose(string id, string by, DateTimeOffset now)
    {
        if (Find(id) is not { Removed: false } || Chosen?.VoiceId == id) return this;
        return this with { Chosen = new() { VoiceId = id, Revision = NextRevision(now), UpdatedAt = now.ToUniversalTime(), UpdatedBy = by } };
    }

    private SpeakingVoiceLibrary Put(SpeakingVoice voice) => this with { Voices = Bounded(Voices.Where(v => v.Id != voice.Id).Append(voice)) };

    /// <summary>Joins two copies: per voice, and for the chosen voice, the entry with the newest (revision, writer, content)
    /// wins.</summary>
    public static SpeakingVoiceLibrary Merge(SpeakingVoiceLibrary left, SpeakingVoiceLibrary right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var voices = left.Voices.Concat(right.Voices).GroupBy(v => v.Id, StringComparer.Ordinal)
            .Select(group => group.Aggregate((a, b) => Newer(a, b) ? a : b));
        var chosen = (left.Chosen, right.Chosen) switch
        {
            (null, var b) => b,
            (var a, null) => a,
            var (a, b) => NewerChoice(a!, b!) ? a : b
        };
        return new() { SchemaVersion = SchemaVersion1, Voices = Bounded(voices), Chosen = chosen };
    }

    private static bool Newer(SpeakingVoice a, SpeakingVoice b) =>
        a.Revision != b.Revision ? a.Revision > b.Revision
        : a.UpdatedBy != b.UpdatedBy ? string.CompareOrdinal(a.UpdatedBy, b.UpdatedBy) > 0
        : string.CompareOrdinal(a.Content, b.Content) >= 0;

    private static bool NewerChoice(SpeakingVoiceChoice a, SpeakingVoiceChoice b) =>
        a.Revision != b.Revision ? a.Revision > b.Revision
        : a.UpdatedBy != b.UpdatedBy ? string.CompareOrdinal(a.UpdatedBy, b.UpdatedBy) > 0
        : string.CompareOrdinal(a.VoiceId, b.VoiceId) >= 0;

    // The most recently added live voices, then the newest tombstones; sorted by ID so equal content writes equal bytes.
    private static SpeakingVoice[] Bounded(IEnumerable<SpeakingVoice> voices)
    {
        var all = voices.ToArray();
        return all.Where(v => !v.Removed).OrderByDescending(v => v.AddedAt).ThenBy(v => v.Id, StringComparer.Ordinal).Take(MaximumVoices)
            .Concat(all.Where(v => v.Removed).OrderByDescending(v => v.Revision).ThenBy(v => v.Id, StringComparer.Ordinal).Take(MaximumTombstones))
            .OrderBy(v => v.Id, StringComparer.Ordinal).ToArray();
    }

    public static bool IsSha256(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>A name as it may be stored: one line, trimmed, 1 to <see cref="MaximumNameLength"/> characters.</summary>
    public static bool IsName(string? value) => value is { Length: > 0 and <= MaximumNameLength } && value == value.Trim() &&
        !value.Any(char.IsControl) && Utf8Bytes(value) is > 0 and <= MaximumNameUtf8Bytes;

    /// <summary>A transcript as it may be stored: 1 to <see cref="MaximumTranscriptLength"/> characters, line breaks and tabs
    /// allowed, not only white space.</summary>
    public static bool IsTranscript(string? value) => value is { Length: > 0 and <= MaximumTranscriptLength } && !string.IsNullOrWhiteSpace(value) &&
        value.All(c => !char.IsControl(c) || c is '\r' or '\n' or '\t') && Utf8Bytes(value) is > 0 and <= MaximumTranscriptUtf8Bytes;

    private static int Utf8Bytes(string value)
    {
        try { return StrictUtf8.GetByteCount(value); }
        catch (EncoderFallbackException) { return -1; }
    }

    private static void Validate(SpeakingVoice voice)
    {
        ContractRules.Require(IsSha256(voice.Id), "A voice ID is invalid.");
        ContractRules.Require(voice.Revision is > 0 and <= MaximumRevision, "A voice revision is out of range.");
        ContractRules.Identifier(voice.UpdatedBy);
        if (voice.Removed)
        {
            ContractRules.Require(voice.Name is null && voice.Transcript is null && voice.AudioSha256 is null && voice.Rights is null &&
                voice.Note is null && voice.Clips is null && voice.SampleRate is null && voice.DurationMilliseconds == 0,
                "A removed voice still holds data.");
            return;
        }
        ContractRules.Require(IsName(voice.Name), "A voice name is invalid.");
        ContractRules.Require(IsTranscript(voice.Transcript), "A voice transcript is invalid.");
        ContractRules.Require(IsSha256(voice.AudioSha256) && ReferenceId(voice.AudioSha256!, voice.Transcript!) == voice.Id,
            "A voice ID does not match its recording and transcript.");
        ContractRules.Require(voice.DurationMilliseconds is >= MinimumDurationMilliseconds and <= MaximumDurationMilliseconds,
            "A voice recording must be 1 to 30 seconds long.");
        ContractRules.Require(voice.Rights is { } rights && Enum.IsDefined(rights), "A voice must say why it may be used.");
        ContractRules.Require(voice.Note is null || voice.Note.Length <= MaximumNoteLength && !voice.Note.Any(char.IsControl), "A voice note is invalid.");
        ValidateClips(voice);
    }

    // A voice of several recordings: 2 to MaximumClips of them, in order without overlapping, each at least
    // MinimumClipMilliseconds long and within the joined recording, whose transcript is theirs joined with spaces.
    private static void ValidateClips(SpeakingVoice voice)
    {
        if (voice.Clips is null)
        {
            ContractRules.Require(voice.SampleRate is null, "A voice of one recording has no sample rate.");
            return;
        }
        ContractRules.Require(voice.Clips.Count is >= 2 and <= MaximumClips && voice.Clips.All(c => c is not null) &&
            voice.SampleRate is int rate && SampleRates.Contains(rate), "A voice's recordings are invalid.");
        var end = 0L;
        foreach (var clip in voice.Clips)
        {
            ContractRules.Require(IsTranscript(clip.Transcript) && clip.StartSample >= end && clip.SampleCount > 0 &&
                Math.Ceiling(clip.SampleCount * 1000d / voice.SampleRate!.Value) >= MinimumClipMilliseconds, "A voice's recordings are invalid.");
            end = (long)clip.StartSample + clip.SampleCount;
        }
        ContractRules.Require(Math.Ceiling(end * 1000d / voice.SampleRate!.Value) <= voice.DurationMilliseconds &&
            voice.Transcript == JoinedTranscript(voice.Clips), "A voice's recordings don't match its recording and transcript.");
    }

    public void Validate()
    {
        ContractRules.Require(SchemaVersion == SchemaVersion1, "This voice list was written by a newer Martlet.", ErrorCode.UnsupportedVersion);
        ContractRules.Require(Voices is not null && Voices.All(v => v is not null) &&
            Voices.Count(v => !v.Removed) <= MaximumVoices && Voices.Count(v => v.Removed) <= MaximumTombstones,
            "The voice list has too many voices.");
        ContractRules.Require(Voices!.Select(v => v.Id).Distinct(StringComparer.Ordinal).Count() == Voices!.Count, "The voice list lists a voice twice.");
        foreach (var voice in Voices) Validate(voice);
        if (Chosen is { } chosen)
        {
            ContractRules.Require(IsSha256(chosen.VoiceId) && chosen.Revision is > 0 and <= MaximumRevision, "The chosen voice is invalid.");
            ContractRules.Identifier(chosen.UpdatedBy);
        }
    }

    public byte[] Write()
    {
        Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this with { Voices = Bounded(Voices) }, Json);
        ContractRules.Require(bytes.Length <= MaximumBytes, "The voice list is too large.", ErrorCode.PayloadTooLarge);
        return bytes;
    }

    public static SpeakingVoiceLibrary Parse(ReadOnlySpan<byte> bytes)
    {
        ContractRules.Require(bytes.Length is > 0 and <= MaximumBytes, "The voice list is empty or too large.", ErrorCode.PayloadTooLarge);
        SpeakingVoiceLibrary? library;
        try { library = JsonSerializer.Deserialize<SpeakingVoiceLibrary>(bytes, Json); }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException or FormatException)
        {
            throw new ContractException(ErrorCode.InvalidContract, "The voice list is malformed.");
        }
        ContractRules.Require(library is not null, "The voice list is empty.");
        library!.Validate();
        return library with { Voices = Bounded(library.Voices) };
    }

    /// <summary>Identifies the list's content, to tell whether a copy is current.</summary>
    public string Digest() => Convert.ToHexStringLower(SHA256.HashData(Write()));

    public override string ToString() => $"Speaking voice library r{Revision} ({Live.Count} voices)";
}
