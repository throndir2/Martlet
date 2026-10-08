namespace Martlet.Core.Settings;

/// <summary>What saving one choice on Companion › Vision › Image model or Companion › Listening › Audio model changes
/// (docs/SENSE_MODELS.md): the key a model of its own keeps, whether a key must still be typed, and the keys that no choice uses
/// after the save, which the desktop then deletes from Windows Credential Manager.</summary>
public static class SenseModelChoice
{
    /// <summary>The key that an endpoint chosen for <paramref name="kind"/> at <paramref name="origin"/> keeps when no new key is
    /// typed: this kind's own key for that base URL, else the other kind's (one key serves both), else null (Thinking's key for the
    /// same base URL, or none).</summary>
    public static Guid? KeptKey(SenseModels saved, SenseKind kind, string origin)
    {
        ArgumentNullException.ThrowIfNull(saved);
        return KeyOf(saved.For(kind), origin) ?? KeyOf(saved.For(SenseModels.Other(kind)), origin);
    }

    /// <summary>Whether <paramref name="own"/> still needs a key typed: its provider needs one, it keeps none, and Thinking's key
    /// isn't for the same base URL.</summary>
    public static bool NeedsKey(DeepThinkingSettings own, bool providerNeedsKey, SetupRoute? thinking)
    {
        ArgumentNullException.ThrowIfNull(own);
        return providerNeedsKey && own.CredentialId is null && !own.UsesThinkingKey(thinking);
    }

    /// <summary>The choices with <paramref name="next"/> for <paramref name="kind"/> (validated: throws when they aren't usable),
    /// and the models of their own whose key no choice uses any more, one for each key.</summary>
    public static (SenseModels Next, IReadOnlyList<DeepThinkingSettings> Released) Choose(SenseModels saved, SenseKind kind, SenseModel next)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(next);
        var after = saved.With(kind, next);
        after.Validate();
        var used = Keyed(after).Select(own => own.CredentialId!.Value).ToHashSet();
        return (after, [.. Keyed(saved).Where(own => !used.Contains(own.CredentialId!.Value)).DistinctBy(own => own.CredentialId)]);
    }

    private static Guid? KeyOf(SenseModel model, string origin) =>
        model is { Source: SenseSource.Own, Own: { Place: DeepThinkingPlace.Endpoint, CredentialId: { } key } own } &&
        string.Equals(own.Origin, origin, StringComparison.Ordinal) ? key : null;

    // The models of their own that have a key of their own.
    private static IEnumerable<DeepThinkingSettings> Keyed(SenseModels models) =>
        new[] { models.Image, models.Audio }.Where(m => m is { Source: SenseSource.Own, Own.CredentialId: not null }).Select(m => m.Own!);
}
