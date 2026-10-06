using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

/// <summary>A character profile (Companion › Profiles): one name that switches who Martlet is all at once: its personality
/// (a persona), its look (one of your character models, or the built-in one) and its voice (one of your voices). A profile
/// without a look or a voice keeps whatever Martlet uses now for that part. It travels with the shared settings, and the look
/// and voice are named by their shared IDs, so every computer switches to its own copy of the same character.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CharacterProfile : IContract
{
    public const int MaximumNameCharacters = 64;
    public const int MaximumReferenceCharacters = 128;
    /// <summary>The look that is Martlet's built-in character.</summary>
    public const string BuiltInModel = "builtin";

    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required Guid PersonaId { get; init; }
    /// <summary>The shared character model's ID, <see cref="BuiltInModel"/>, or null to keep the look Martlet shows.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModelId { get; init; }
    /// <summary>The shared speaking voice's ID, or null to keep the voice Martlet speaks with.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VoiceId { get; init; }

    /// <summary>A short, stable key for automation IDs: the first 8 hex digits of its ID.</summary>
    [JsonIgnore]
    public string Key => Id.ToString("N")[..8];

    public void Validate()
    {
        ContractRules.Require(Id != Guid.Empty && PersonaId != Guid.Empty, "A character profile requires an identity and a persona.");
        ContractRules.Require(Name is { Length: > 0 and <= MaximumNameCharacters } && Name == Name.Trim() && !Name.Any(char.IsControl),
            "Character profile names must be 1-64 visible characters without leading or trailing whitespace.");
        foreach (var reference in new[] { ModelId, VoiceId })
            ContractRules.Require(reference is null || (reference.Length is > 0 and <= MaximumReferenceCharacters && !reference.Any(char.IsControl) &&
                    !reference.Any(char.IsWhiteSpace)),
                "A character profile's look and voice must be saved IDs.");
    }

    /// <summary>Whether Martlet is this character now: its personality is the active one, and its look and voice (when it sets
    /// them) are the ones in use. <paramref name="modelId"/> is the look shown (a shared model's ID or
    /// <see cref="BuiltInModel"/>; null when it is a model file outside your list) and <paramref name="voiceId"/> the voice
    /// chosen (null when none is).</summary>
    public bool Matches(Guid activePersonaId, string? modelId, string? voiceId) =>
        PersonaId == activePersonaId &&
        (ModelId is null || string.Equals(ModelId, modelId, StringComparison.OrdinalIgnoreCase)) &&
        (VoiceId is null || string.Equals(VoiceId, voiceId, StringComparison.OrdinalIgnoreCase));
}
