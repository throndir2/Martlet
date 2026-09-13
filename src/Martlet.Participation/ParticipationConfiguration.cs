using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Martlet.Participation;

public sealed class ParticipationConfiguration
{
    public ParticipationMode Mode { get; }
    public PolicyLanguage Language { get; }
    [JsonIgnore] public string CompanionName { get; }
    [JsonIgnore] public ReadOnlyCollection<string> Aliases { get; }
    public TimeSpan AddressedGap { get; }
    public TimeSpan UnsolicitedGap { get; }
    public TimeSpan AutomaticCooldown { get; }
    public TimeSpan IntentLifetime { get; }
    public int UnsolicitedTurnsPerMinute { get; }
    public double MinimumConfidence { get; }
    internal string[] NormalizedNames { get; }

    public ParticipationConfiguration(string companionName = "Martlet", IEnumerable<string>? aliases = null,
        ParticipationMode mode = ParticipationMode.PushToTalkOnly, PolicyLanguage language = PolicyLanguage.English,
        TimeSpan? addressedGap = null, TimeSpan? unsolicitedGap = null, TimeSpan? automaticCooldown = null,
        TimeSpan? intentLifetime = null, int unsolicitedTurnsPerMinute = 2, double minimumConfidence = 0.7)
    {
        Mode = mode;
        Language = language;
        AddressedGap = addressedGap ?? TimeSpan.FromMilliseconds(600);
        UnsolicitedGap = unsolicitedGap ?? TimeSpan.FromMilliseconds(1200);
        AutomaticCooldown = automaticCooldown ?? TimeSpan.FromSeconds(8);
        IntentLifetime = intentLifetime ?? TimeSpan.FromSeconds(5);
        UnsolicitedTurnsPerMinute = unsolicitedTurnsPerMinute;
        MinimumConfidence = minimumConfidence;
        PolicyChecks.Require(Enum.IsDefined(mode) && language == PolicyLanguage.English &&
            AddressedGap >= TimeSpan.FromMilliseconds(100) && AddressedGap <= TimeSpan.FromSeconds(3) &&
            UnsolicitedGap >= TimeSpan.FromMilliseconds(1200) && UnsolicitedGap <= TimeSpan.FromSeconds(3) &&
            AutomaticCooldown >= TimeSpan.FromSeconds(1) && AutomaticCooldown <= TimeSpan.FromMinutes(1) &&
            IntentLifetime >= TimeSpan.FromSeconds(1) && IntentLifetime <= TimeSpan.FromSeconds(5) &&
            IntentLifetime > AddressedGap && IntentLifetime > UnsolicitedGap &&
            unsolicitedTurnsPerMinute is >= 0 and <= 2 &&
            double.IsFinite(minimumConfidence) && minimumConfidence is >= 0 and <= 1,
            PolicyValidationCode.InvalidConfiguration);
        CompanionName = TextRules.ValidateName(companionName);
        var copied = new List<string>();
        if (aliases is not null)
        {
            // Stop enumerating at the bound, including a caller-supplied unbounded sequence.
            foreach (var alias in aliases)
            {
                PolicyChecks.Require(copied.Count < 8, PolicyValidationCode.InvalidConfiguration);
                copied.Add(TextRules.ValidateName(alias));
            }
        }
        Aliases = copied.AsReadOnly();
        NormalizedNames = new[] { CompanionName }.Concat(copied).Select(TextRules.Normalize).ToArray();
        PolicyChecks.Require(NormalizedNames.Distinct(StringComparer.Ordinal).Count() == NormalizedNames.Length,
            PolicyValidationCode.InvalidConfiguration);
    }

    public override string ToString() => $"{nameof(ParticipationConfiguration)}: {Mode}, {Language}";
}
