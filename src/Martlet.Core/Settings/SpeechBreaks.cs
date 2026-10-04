using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

/// <summary>Where a persona's spoken replies may break between pieces (Personality › Where the voice pauses). Martlet speaks a
/// reply a piece at a time while it is written, so the voice starts sooner; each piece is said on its own, so a break in the
/// wrong place sounds awkward. Only sentence ends can break a reply: commas, semicolons and dashes never do. A stop that is off
/// never ends a piece until the piece has grown long, so a piece always stays short enough for the voice to say at once. A
/// short ending of up to <see cref="ShortEndingWords"/> words (". Cutie!") is said together with the piece before it instead
/// of on its own.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SpeechBreaks : IContract
{
    public const int DefaultShortEndingWords = 2;
    public const int MaximumShortEndingWords = 5;

    /// <summary>Every stop on, and short endings of up to two words said with what comes before them.</summary>
    public static SpeechBreaks Default { get; } = new();

    public bool Periods { get; init; } = true;
    public bool QuestionMarks { get; init; } = true;
    public bool ExclamationMarks { get; init; } = true;
    /// <summary>A piece of at most this many words joins the piece before it; 0 never joins pieces.</summary>
    public int ShortEndingWords { get; init; } = DefaultShortEndingWords;

    // Earlier settings saved a commas stop; commas never break a reply now, so a saved one still loads, is ignored and is not
    // written again.
    [JsonInclude, JsonPropertyName("commas"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    private bool? RetiredCommas { get => null; init { } }

    [JsonIgnore]
    public bool IsDefault => this == Default;

    /// <summary>Null for the defaults, so an untouched persona keeps its original saved shape.</summary>
    public static SpeechBreaks? Normalize(SpeechBreaks? breaks) =>
        breaks is null || breaks.IsDefault ? null : breaks;

    public void Validate() =>
        ContractRules.Require(ShortEndingWords is >= 0 and <= MaximumShortEndingWords,
            $"A short ending is 0 through {MaximumShortEndingWords} words.");
}
