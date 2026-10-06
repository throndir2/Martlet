using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Speakers;

/// <summary>Where a name for a voice came from: typed by the owner, or picked up by the Thinking model from what was said.</summary>
public enum VoiceNameSource { User, Conversation }

/// <summary>One name a voice goes by, how often it was used and when last.</summary>
public sealed record VoiceName
{
    public required string Text { get; init; }
    public required VoiceNameSource Source { get; init; }
    public int Uses { get; init; } = 1;
    public required DateTimeOffset LastUsedAt { get; init; }
}

/// <summary>One voice Martlet has heard: its voiceprint (a normalized speaker embedding centroid plus a few diverse samples,
/// never audio), the names it goes by and how often it was heard. <see cref="Removed"/> is a tombstone (forgotten, or merged
/// into <see cref="MergedInto"/>), so the entry does not return from an older copy.</summary>
public sealed record KnownVoice
{
    public required string Id { get; init; }
    public required int Number { get; init; }
    /// <summary>The name the owner chose; it always wins over names picked up in conversation.</summary>
    public string? Name { get; init; }
    public IReadOnlyList<VoiceName> Names { get; init; } = [];
    /// <summary>The owner marked this voice as their own.</summary>
    public bool Owner { get; init; }
    public string? Centroid { get; init; }
    public IReadOnlyList<string> Samples { get; init; } = [];
    /// <summary>How many other voices were merged into this one; such voices are matched by their closest sample.</summary>
    public int MergedVoices { get; init; }
    public int Heard { get; init; }
    public double SpeechSeconds { get; init; }
    public DateTimeOffset FirstHeardAt { get; init; }
    public DateTimeOffset LastHeardAt { get; init; }
    public bool Removed { get; init; }
    public string? MergedInto { get; init; }
    public required long Revision { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public required string UpdatedBy { get; init; }

    /// <summary>The owner's name, else the name used most in conversation (newest on a tie), else another name the owner typed,
    /// else "Voice N".</summary>
    [JsonIgnore]
    public string DisplayName => Name ?? Names.Where(n => n.Source == VoiceNameSource.Conversation)
        .OrderByDescending(n => n.Uses).ThenByDescending(n => n.LastUsedAt).Select(n => n.Text).FirstOrDefault() ??
        Names.Select(n => n.Text).FirstOrDefault() ?? $"Voice {Number}";

    /// <summary>Whether the voice goes by any name yet.</summary>
    [JsonIgnore]
    public bool Named => Name is not null || Names.Count > 0;

    /// <summary>A short tag the Thinking model uses to refer to this voice (V3).</summary>
    [JsonIgnore]
    public string Tag => "V" + Number;

    /// <summary>Every other name this voice goes by, the owner's first, then the most used.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> OtherNames => Names.OrderByDescending(n => n.Source == VoiceNameSource.User).ThenByDescending(n => n.Uses)
        .Select(n => n.Text).Where(n => !string.Equals(n, DisplayName, StringComparison.OrdinalIgnoreCase)).ToArray();

    [JsonIgnore]
    internal float[]? CentroidVector => Centroid is null ? null : VoicePrints.Decode(Centroid);

    [JsonIgnore]
    internal IReadOnlyList<float[]> SampleVectors => Samples.Select(VoicePrints.Decode).ToArray();

    internal string Content => JsonSerializer.Serialize(this with { Revision = 0, UpdatedAt = default, UpdatedBy = "" }, VoiceRoster.Json);
}

/// <summary>How a heard voiceprint compares with the voices Martlet knows.</summary>
public enum VoiceMatchKind { Known, New, Unsure }

/// <summary><paramref name="Voice"/> is the confident match (Known) or the closest voice (Unsure); null when nothing is known.</summary>
public sealed record VoiceMatch(VoiceMatchKind Kind, KnownVoice? Voice, double Score);

/// <summary>The voices Martlet knows, shared by every paired Martlet host and every desktop that keeps them in sync, so any of
/// the owner's computers can recognize the same people. It holds one last-writer-wins entry per voice; <see cref="Merge"/> is
/// commutative, associative and idempotent. Revisions are hybrid clocks like the cluster plan's. Voiceprints are numbers
/// derived from speech (never audio) and stay on the owner's own computers.</summary>
public sealed record VoiceRoster
{
    public const int SchemaVersion1 = 1;
    /// <summary>The speaker embedding model the voiceprints come from (WeSpeaker ResNet34-LM, 256 numbers).</summary>
    public const string EmbeddingModel = "wespeaker-resnet34-lm-e9848563";
    public const int MaximumBytes = 1_048_576;
    public const int MaximumVoices = 64;
    public const int MaximumTombstones = 64;
    /// <summary>The most names one voice goes by: as many as still keep a full list (every voice at its most) within
    /// <see cref="MaximumBytes"/>.</summary>
    public const int MaximumNames = 40;
    public const int MaximumNameLength = 48;
    public const int MaximumSamples = 5;
    private const long MaximumRevision = long.MaxValue / 4;

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
    public required string Model { get; init; }
    public required IReadOnlyList<KnownVoice> Voices { get; init; }

    public static VoiceRoster Empty { get; } = new() { SchemaVersion = SchemaVersion1, Model = EmbeddingModel, Voices = [] };

    /// <summary>The voices that are not forgotten or merged, most recently heard first.</summary>
    [JsonIgnore]
    public IReadOnlyList<KnownVoice> Live => Voices.Where(v => !v.Removed).OrderByDescending(v => v.LastHeardAt).ThenBy(v => v.Number).ToArray();

    [JsonIgnore]
    public long Revision => Voices.Select(v => v.Revision).DefaultIfEmpty(0).Max();

    public long NextRevision(DateTimeOffset now) => Math.Max(Revision + 1, now.ToUnixTimeMilliseconds());

    public KnownVoice? Find(string id) => Voices.FirstOrDefault(v => v.Id == id);

    /// <summary>The live voice an ID now stands for, following merges; null when it was forgotten.</summary>
    public KnownVoice? Resolve(string id)
    {
        for (var hops = 0; hops <= Voices.Count; hops++)
        {
            var voice = Find(id);
            if (voice is null) return null;
            if (!voice.Removed) return voice;
            if (voice.MergedInto is not { } next) return null;
            id = next;
        }
        return null;
    }

    /// <summary>Compares a voiceprint with every known voice: Known when the best clears the match threshold and the runner-up
    /// margin, New when nothing is even close, otherwise Unsure (never forced either way).</summary>
    public VoiceMatch Identify(float[] probe, VoiceMatchOptions? options = null)
    {
        options ??= VoiceMatchOptions.Default;
        var vector = VoicePrints.Normalize(probe);
        var ranked = Live.Select(voice => (Voice: voice, Score: VoicePrints.Score(voice, vector)))
            .Where(item => item.Score is not null).Select(item => (item.Voice, Score: item.Score!.Value))
            .OrderByDescending(item => item.Score).ThenBy(item => item.Voice.Number).ToArray();
        if (ranked.Length == 0) return new(VoiceMatchKind.New, null, 0);
        var best = ranked[0];
        var runnerUp = ranked.Length > 1 ? ranked[1].Score : -1;
        if (best.Score >= options.MatchThreshold && best.Score - runnerUp >= options.RunnerUpMargin)
            return new(VoiceMatchKind.Known, best.Voice, best.Score);
        return best.Score < options.NewVoiceThreshold
            ? new(VoiceMatchKind.New, best.Voice, best.Score)
            : new(VoiceMatchKind.Unsure, best.Voice, best.Score);
    }

    /// <summary>Adds a newly heard voice ("Voice N"). When the list is full, the longest-unheard voice that has no name and is
    /// not the owner's is forgotten to make room; the voice is null when none can be.</summary>
    public (VoiceRoster Roster, KnownVoice? Voice) Add(float[] probe, double seconds, string by, DateTimeOffset now)
    {
        var roster = this;
        if (Live.Count >= MaximumVoices)
        {
            var stale = Live.Where(v => !v.Named && !v.Owner).OrderBy(v => v.LastHeardAt).FirstOrDefault();
            if (stale is null) return (this, null);
            roster = roster.Forget(stale.Id, by, now);
        }
        var print = VoicePrints.Encode(VoicePrints.Normalize(probe));
        var voice = new KnownVoice
        {
            Id = Guid.NewGuid().ToString("N"), Number = roster.Voices.Select(v => v.Number).DefaultIfEmpty(0).Max() + 1,
            Centroid = print, Samples = [print], Heard = 1, SpeechSeconds = Math.Round(seconds, 1),
            FirstHeardAt = now.ToUniversalTime(), LastHeardAt = now.ToUniversalTime(),
            Revision = roster.NextRevision(now), UpdatedAt = now.ToUniversalTime(), UpdatedBy = by
        };
        return (roster.Put(voice), voice);
    }

    /// <summary>Folds a confidently matched voiceprint into the voice: the centroid moves a little towards it and it may
    /// replace a less diverse sample.</summary>
    public VoiceRoster Learn(string id, float[] probe, double seconds, string by, DateTimeOffset now)
    {
        if (Resolve(id) is not { } voice || voice.CentroidVector is not { } centroid) return this;
        var vector = VoicePrints.Normalize(probe);
        var weight = Math.Min(Math.Max(voice.Heard, 1), 20);
        var moved = VoicePrints.Normalize(centroid.Select((value, i) => value * weight + vector[i]).ToArray());
        return Update(voice with
        {
            Centroid = VoicePrints.Encode(moved),
            Samples = VoicePrints.SelectRepresentatives(voice.SampleVectors.Append(vector), MaximumSamples).Select(VoicePrints.Encode).ToArray(),
            Heard = Math.Min(voice.Heard + 1, 1_000_000), SpeechSeconds = Math.Min(Math.Round(voice.SpeechSeconds + seconds, 1), 1e7),
            LastHeardAt = now.ToUniversalTime()
        }, by, now);
    }

    /// <summary>Notes that a voice was heard again without learning from it (the speech was too short to trust).</summary>
    public VoiceRoster Heard(string id, string by, DateTimeOffset now) =>
        Resolve(id) is { } voice ? Update(voice with { Heard = Math.Min(voice.Heard + 1, 1_000_000), LastHeardAt = now.ToUniversalTime() }, by, now) : this;

    /// <summary>Records a name the Thinking model picked up for a voice from what was said. <paramref name="prefer"/> (the
    /// person asked to be called it) makes it the learned name shown; a name the owner typed still wins.</summary>
    public VoiceRoster AddHeardName(string id, string name, string by, DateTimeOffset now, bool prefer = false)
    {
        if (Resolve(id) is not { } voice || CleanName(name) is not { } text) return this;
        var existing = voice.Names.FirstOrDefault(n => string.Equals(n.Text, text, StringComparison.OrdinalIgnoreCase));
        // Preferring a name gives it one use more than any other learned name, so it is the one shown.
        var top = voice.Names.Where(n => n.Source == VoiceNameSource.Conversation && !ReferenceEquals(n, existing)).Select(n => n.Uses)
            .DefaultIfEmpty(0).Max();
        int Uses(int current) => Math.Min(prefer ? Math.Max(current, top) + 1 : current + 1, 1_000_000);
        var names = existing is not null
            ? voice.Names.Select(n => n == existing ? n with { Uses = Uses(n.Uses), LastUsedAt = now.ToUniversalTime() } : n)
            : voice.Names.Append(new VoiceName
            {
                Text = text, Source = VoiceNameSource.Conversation, Uses = Uses(0), LastUsedAt = now.ToUniversalTime()
            });
        return Update(voice with { Names = TrimNames(names) }, by, now);
    }

    /// <summary>Drops the names a voice picked up in conversation that <paramref name="drop"/> picks; names the owner typed stay.</summary>
    public VoiceRoster DropHeardNames(string id, Func<string, bool> drop, string by, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(drop);
        if (Resolve(id) is not { } voice) return this;
        var kept = voice.Names.Where(n => n.Source != VoiceNameSource.Conversation || !drop(n.Text)).ToArray();
        return kept.Length == voice.Names.Count ? this : Update(voice with { Names = kept }, by, now);
    }

    /// <summary>The owner's names for a voice: <paramref name="name"/> becomes its name (null keeps the learned one) and the
    /// voice goes by exactly <paramref name="name"/> plus <paramref name="others"/>; learned names the owner removed are dropped.</summary>
    public VoiceRoster SetNames(string id, string? name, IEnumerable<string> others, string by, DateTimeOffset now)
    {
        if (Resolve(id) is not { } voice) return this;
        var primary = CleanName(name);
        var wanted = new List<string>();
        foreach (var text in (primary is null ? Array.Empty<string>() : [primary]).Concat(others.Select(CleanName).OfType<string>()))
            if (!wanted.Contains(text, StringComparer.OrdinalIgnoreCase)) wanted.Add(text);
        var names = wanted.Select(text => voice.Names.FirstOrDefault(n => string.Equals(n.Text, text, StringComparison.OrdinalIgnoreCase)) is { } kept
            ? kept with { Text = text, Source = text == primary ? VoiceNameSource.User : kept.Source }
            : new VoiceName { Text = text, Source = VoiceNameSource.User, LastUsedAt = now.ToUniversalTime() });
        return Update(voice with { Name = primary, Names = TrimNames(names) }, by, now);
    }

    public VoiceRoster SetOwner(string id, bool owner, string by, DateTimeOffset now) =>
        Resolve(id) is { } voice && voice.Owner != owner ? Update(voice with { Owner = owner }, by, now) : this;

    /// <summary>Joins <paramref name="fromId"/> into <paramref name="intoId"/> (the owner says they are the same person): the
    /// names, samples and counts combine and the merged voice leaves a tombstone pointing at the kept one.</summary>
    public VoiceRoster Join(string fromId, string intoId, string by, DateTimeOffset now)
    {
        if (Resolve(fromId) is not { } from || Resolve(intoId) is not { } into || from.Id == into.Id) return this;
        var names = into.Names.ToList();
        foreach (var name in from.Names)
        {
            var same = names.FindIndex(n => string.Equals(n.Text, name.Text, StringComparison.OrdinalIgnoreCase));
            if (same < 0) names.Add(name);
            else names[same] = names[same] with
            {
                Uses = Math.Min(names[same].Uses + name.Uses, 1_000_000),
                LastUsedAt = names[same].LastUsedAt > name.LastUsedAt ? names[same].LastUsedAt : name.LastUsedAt,
                Source = names[same].Source == VoiceNameSource.User || name.Source == VoiceNameSource.User ? VoiceNameSource.User : VoiceNameSource.Conversation
            };
        }
        if (from.Name is { } fromName && !names.Any(n => string.Equals(n.Text, fromName, StringComparison.OrdinalIgnoreCase)))
            names.Add(new VoiceName { Text = fromName, Source = VoiceNameSource.User, LastUsedAt = now.ToUniversalTime() });
        var centroid = into.Centroid ?? from.Centroid;
        if (into.CentroidVector is { } a && from.CentroidVector is { } b)
        {
            double wa = Math.Max(1, into.Heard), wb = Math.Max(1, from.Heard);
            centroid = VoicePrints.Encode(VoicePrints.Normalize(a.Select((value, i) => (float)(value * wa + b[i] * wb)).ToArray()));
        }
        var kept = into with
        {
            // The kept voice keeps its own name (typed or learned); the merged voice's typed name becomes another name.
            Name = into.Name ?? (into.Named ? null : from.Name), Names = TrimNames(names), Owner = into.Owner || from.Owner, Centroid = centroid,
            Samples = VoicePrints.SelectRepresentatives(into.SampleVectors.Concat(from.SampleVectors), MaximumSamples).Select(VoicePrints.Encode).ToArray(),
            MergedVoices = Math.Min(into.MergedVoices + from.MergedVoices + 1, 1000),
            Heard = Math.Min(into.Heard + from.Heard, 1_000_000), SpeechSeconds = Math.Min(into.SpeechSeconds + from.SpeechSeconds, 1e7),
            FirstHeardAt = into.FirstHeardAt < from.FirstHeardAt ? into.FirstHeardAt : from.FirstHeardAt,
            LastHeardAt = into.LastHeardAt > from.LastHeardAt ? into.LastHeardAt : from.LastHeardAt
        };
        var roster = Update(kept, by, now);
        return roster.Put(Tombstone(from, into.Id, roster.NextRevision(now), by, now));
    }

    /// <summary>Forgets a voice: its voiceprint and names are deleted and only a tombstone remains.</summary>
    public VoiceRoster Forget(string id, string by, DateTimeOffset now) =>
        Resolve(id) is { } voice ? Put(Tombstone(voice, null, NextRevision(now), by, now)) : this;

    private static KnownVoice Tombstone(KnownVoice voice, string? into, long revision, string by, DateTimeOffset now) => new()
    {
        Id = voice.Id, Number = voice.Number, Removed = true, MergedInto = into,
        Revision = revision, UpdatedAt = now.ToUniversalTime(), UpdatedBy = by
    };

    private VoiceRoster Update(KnownVoice voice, string by, DateTimeOffset now) =>
        Put(voice with { Revision = NextRevision(now), UpdatedAt = now.ToUniversalTime(), UpdatedBy = by });

    private VoiceRoster Put(KnownVoice voice) => this with { Voices = Bounded(Voices.Where(v => v.Id != voice.Id).Append(voice)) };

    private static IReadOnlyList<VoiceName> TrimNames(IEnumerable<VoiceName> names) => names
        .OrderByDescending(n => n.Source == VoiceNameSource.User).ThenByDescending(n => n.Uses).ThenByDescending(n => n.LastUsedAt)
        .Take(MaximumNames).ToArray();

    /// <summary>A name as it may be stored: trimmed, one line, starting with a letter, at most
    /// <see cref="MaximumNameLength"/> characters; null when it isn't a usable name.</summary>
    public static string? CleanName(string? value)
    {
        if (value is null) return null;
        var text = string.Join(' ', value.Trim().Trim('"', '\'', '\u201C', '\u201D', '.', ',', '!', '?', ':', ';')
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length is > 0 and <= MaximumNameLength && char.IsLetter(text[0]) &&
            text.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '\'' or '.' or '\u2019')
            ? text : null;
    }

    /// <summary>Joins two copies: per voice the entry with the newest (revision, writer, content) wins.</summary>
    public static VoiceRoster Merge(VoiceRoster left, VoiceRoster right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var voices = left.Voices.Concat(right.Voices).GroupBy(v => v.Id, StringComparer.Ordinal)
            .Select(group => group.Aggregate((a, b) => Newer(a, b) ? a : b));
        return new() { SchemaVersion = SchemaVersion1, Model = EmbeddingModel, Voices = Bounded(voices) };
    }

    private static bool Newer(KnownVoice a, KnownVoice b) =>
        a.Revision != b.Revision ? a.Revision > b.Revision
        : a.UpdatedBy != b.UpdatedBy ? string.CompareOrdinal(a.UpdatedBy, b.UpdatedBy) > 0
        : string.CompareOrdinal(a.Content, b.Content) >= 0;

    // Live voices first (most recently heard), then the newest tombstones; sorted by ID so equal content writes equal bytes.
    private static KnownVoice[] Bounded(IEnumerable<KnownVoice> voices)
    {
        var all = voices.ToArray();
        return all.Where(v => !v.Removed).OrderByDescending(v => v.LastHeardAt).ThenBy(v => v.Id, StringComparer.Ordinal).Take(MaximumVoices)
            .Concat(all.Where(v => v.Removed).OrderByDescending(v => v.Revision).ThenBy(v => v.Id, StringComparer.Ordinal).Take(MaximumTombstones))
            .OrderBy(v => v.Id, StringComparer.Ordinal).ToArray();
    }

    private static bool IsId(string? value) => value is { Length: 32 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public void Validate()
    {
        ContractRules.Require(SchemaVersion == SchemaVersion1, "This voice list was written by a newer Martlet.", ErrorCode.UnsupportedVersion);
        ContractRules.Require(Model == EmbeddingModel, "This voice list uses a different voice model.", ErrorCode.UnsupportedVersion);
        ContractRules.Require(Voices is not null && Voices.All(v => v is not null) &&
            Voices.Count(v => !v.Removed) <= MaximumVoices && Voices.Count(v => v.Removed) <= MaximumTombstones,
            "The voice list has too many voices.");
        ContractRules.Require(Voices!.Select(v => v.Id).Distinct(StringComparer.Ordinal).Count() == Voices!.Count, "The voice list lists a voice twice.");
        foreach (var voice in Voices)
        {
            ContractRules.Require(IsId(voice.Id) && voice.Number is > 0 and <= 1_000_000, "A voice ID is invalid.");
            ContractRules.Require(voice.Revision is > 0 and <= MaximumRevision, "A voice revision is out of range.");
            ContractRules.Identifier(voice.UpdatedBy);
            ContractRules.Require(voice.MergedInto is null || voice.Removed && voice.MergedInto != voice.Id && IsId(voice.MergedInto),
                "A merged voice must point at another voice.");
            if (voice.Removed)
            {
                ContractRules.Require(voice.Name is null && voice.Names.Count == 0 && voice.Centroid is null && voice.Samples.Count == 0,
                    "A forgotten voice still holds data.");
                continue;
            }
            ContractRules.Require(voice.Name is null || CleanName(voice.Name) == voice.Name, "A voice name is invalid.");
            ContractRules.Require(voice.Names is { Count: <= MaximumNames } && voice.Names.All(n => n is not null && CleanName(n.Text) == n.Text &&
                n.Uses is > 0 and <= 1_000_000 && Enum.IsDefined(n.Source)) &&
                voice.Names.Select(n => n.Text.ToUpperInvariant()).Distinct().Count() == voice.Names.Count, "A voice's names are invalid.");
            ContractRules.Require(voice.Centroid is not null && VoicePrints.IsValid(voice.Centroid) &&
                voice.Samples is { Count: > 0 and <= MaximumSamples } && voice.Samples.All(VoicePrints.IsValid), "A voiceprint is invalid.");
            ContractRules.Require(voice.Heard is >= 0 and <= 1_000_000 && voice.MergedVoices is >= 0 and <= 1000 &&
                double.IsFinite(voice.SpeechSeconds) && voice.SpeechSeconds is >= 0 and <= 1e7, "A voice's counts are out of range.");
        }
    }

    public byte[] Write()
    {
        Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this with { Voices = Bounded(Voices) }, Json);
        ContractRules.Require(bytes.Length <= MaximumBytes, "The voice list is too large.", ErrorCode.PayloadTooLarge);
        return bytes;
    }

    public static VoiceRoster Parse(ReadOnlySpan<byte> bytes)
    {
        ContractRules.Require(bytes.Length is > 0 and <= MaximumBytes, "The voice list is empty or too large.", ErrorCode.PayloadTooLarge);
        VoiceRoster? roster;
        try { roster = JsonSerializer.Deserialize<VoiceRoster>(bytes, Json); }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException or FormatException)
        {
            throw new ContractException(ErrorCode.InvalidContract, "The voice list is malformed.");
        }
        ContractRules.Require(roster is not null, "The voice list is empty.");
        roster!.Validate();
        return roster with { Voices = Bounded(roster.Voices) };
    }

    /// <summary>Identifies the list's content, to tell whether a copy is current.</summary>
    public string Digest() => Convert.ToHexStringLower(SHA256.HashData(Write()));

    public override string ToString() => $"Voice roster r{Revision} ({Live.Count} voices)";
}

/// <summary>Matching thresholds (cosine scores, not calibrated probabilities), the same conservative defaults as
/// AudioTranscriber's voice library.</summary>
public sealed record VoiceMatchOptions(double MatchThreshold = 0.70, double NewVoiceThreshold = 0.45, double RunnerUpMargin = 0.08)
{
    public static VoiceMatchOptions Default { get; } = new();
}

/// <summary>Voiceprint arithmetic: 256-number normalized speaker embeddings stored as base64 little-endian float32.</summary>
public static class VoicePrints
{
    public const int Dimension = 256;

    public static float[] Normalize(float[] vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        if (vector.Length != Dimension || vector.Any(x => !float.IsFinite(x))) throw new ArgumentException("Invalid voiceprint.", nameof(vector));
        var norm = Math.Sqrt(vector.Sum(x => (double)x * x));
        if (norm < 1e-8) throw new ArgumentException("Empty voiceprint.", nameof(vector));
        return vector.Select(x => (float)(x / norm)).ToArray();
    }

    public static double Cosine(float[] a, float[] b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length && i < b.Length; i++) sum += (double)a[i] * b[i];
        return Math.Clamp(sum, -1, 1);
    }

    /// <summary>Centroid agreement averaged with the closest sample; for voices the owner merged, the closest sample alone.</summary>
    public static double? Score(KnownVoice voice, float[] probe)
    {
        if (voice.CentroidVector is not { } centroid) return null;
        var samples = voice.SampleVectors;
        var best = samples.Count == 0 ? Cosine(centroid, probe) : samples.Max(sample => Cosine(sample, probe));
        return voice.MergedVoices > 0 ? best : (Cosine(centroid, probe) + best) / 2;
    }

    /// <summary>Up to <paramref name="maximum"/> mutually diverse vectors (near-duplicates dropped, then farthest-first,
    /// starting from the newest).</summary>
    public static IReadOnlyList<float[]> SelectRepresentatives(IEnumerable<float[]> candidates, int maximum)
    {
        var distinct = new List<float[]>();
        foreach (var candidate in candidates)
            if (!distinct.Any(e => Cosine(e, candidate) > 0.98)) distinct.Add(candidate.ToArray());
        if (distinct.Count <= maximum) return distinct;
        var selected = new List<float[]> { distinct[^1] };
        while (selected.Count < maximum)
            selected.Add(distinct.Where(e => !selected.Contains(e)).MinBy(e => selected.Max(s => Cosine(s, e)))!);
        return selected;
    }

    public static string Encode(float[] vector)
    {
        var bytes = new byte[vector.Length * 4];
        for (var i = 0; i < vector.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), vector[i]);
        return Convert.ToBase64String(bytes);
    }

    public static float[] Decode(string text)
    {
        var bytes = Convert.FromBase64String(text);
        if (bytes.Length != Dimension * 4) throw new FormatException("A voiceprint has the wrong size.");
        var vector = new float[Dimension];
        for (var i = 0; i < vector.Length; i++) vector[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * 4));
        return vector;
    }

    internal static bool IsValid(string text)
    {
        try
        {
            if (text.Length != (Dimension * 4 + 2) / 3 * 4) return false;
            var vector = Decode(text);
            if (vector.Any(x => !float.IsFinite(x))) return false;
            var norm = vector.Sum(x => (double)x * x);
            return norm is > 0.98 and < 1.02;
        }
        catch (FormatException) { return false; }
    }
}
