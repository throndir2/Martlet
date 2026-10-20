using System.Globalization;
using Martlet.Avatar.Hosting;
using Martlet.Conversation;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Companion › Touch › Touch zones' decisions, without WPF. The page makes one each time it draws: it reads the shown
/// model's emotes, its zones, the active persona's touch temperament and which model can see the pictures once, and says what
/// the status line, the vision line and Detect zones show. <see cref="TouchZonesPage"/> draws it, and
/// <see cref="ITouchZonesPageServices"/> is all it reads in the app.</summary>
internal sealed class TouchZonesPageModel
{
    /// <summary>What the noticed line says while no touch waits for the next reply.</summary>
    internal const string NoticedIdle = "Nothing waits for Martlet.";

    internal TouchZonesPageModel(ITouchZonesPageServices services)
    {
        Catalog = services.Catalog;
        Settings = services.Zones.Current;
        Temperament = services.Temperament;
        // From the saved setup, so it is right before the talk window opens.
        Sight = SightOf(services.SenseDecision(SenseJobKind.HelperPicture), services.Settings.ThinkingRoute(), services.SavedModelAbilities(),
            Environment.GetEnvironmentVariable(CharacterTouchZoneService.FixtureVariable) is { Length: > 0 });
        Busy = services.Detecting || services.Zones.Busy;
        estimating = services.Zones.Estimating;
        canDetect = services.CanDetect;
    }

    private readonly bool estimating, canDetect;

    /// <summary>The shown model's emotes and motions, or null while they are read.</summary>
    internal CharacterActionCatalog? Catalog { get; }
    /// <summary>The zones of the model the page follows, as saved, or null.</summary>
    internal CharacterTouchZoneSettings? Settings { get; }
    internal CharacterTouchTemperament? Temperament { get; }
    /// <summary>Whether Detect zones has a model that can see, the line that says which and why it is off.</summary>
    internal (bool CanSee, string Line, string? Off) Sight { get; }
    /// <summary>Whether Detect zones or the first guess runs.</summary>
    internal bool Busy { get; }

    /// <summary>The status line (TouchZonesStatus).</summary>
    internal string StatusText => Catalog is null ? "Reading the character..." : StatusOf(Settings);

    /// <summary>Detect zones' label: Detecting... while it runs, Detect again once zones were found by more than a first guess.</summary>
    internal string DetectLabel => Busy && !estimating ? "Detecting..."
        : Settings is { Zones.Count: > 0 } && Settings.DetectedBy != CharacterTouchZoneSettings.ByEstimate ? "Detect again" : "Detect zones";

    /// <summary>Only a missing model that can see turns Detect zones off (the picture is drawn off screen, so the character needn't
    /// show), and the note says so.</summary>
    internal bool DetectEnabled => !Busy && canDetect && Catalog is not null && Sight.CanSee;

    /// <summary>The zones Add zone can add: those the model has no zone for yet.</summary>
    internal TouchZoneKind[] Missing => [.. CharacterTouchZones.Kinds.Where(k => Settings?.Zones.Any(z => z.Id == k.Id) != true)];

    /// <summary>What Add a reaction lists: the model's emotes, gestures and motions in use, then the sounds the voice makes alone.</summary>
    internal List<(string Id, string Label)> ReactionItems(IEnumerable<(string Id, string Label)> voiceSounds)
    {
        var items = new List<(string Id, string Label)>();
        foreach (var (source, _) in Catalog!.Entries.Where(e => e.Action.Enabled))
            items.Add((source.Id, $"{source.Name}  \u00b7  " + source.Kind switch
            {
                CharacterActionKind.Expression => "emote", CharacterActionKind.Motion => "motion", _ => "gesture"
            }));
        items.AddRange(voiceSounds);
        return items;
    }

    /// <summary>The status line for <paramref name="settings"/>: how many zones, how many in use and who placed them.</summary>
    internal static string StatusOf(CharacterTouchZoneSettings? settings) =>
        settings is null || settings.Zones.Count == 0
            ? "No zones found yet for this model, so a click reacts to the rough part (head, face, body, arm, hand, leg)."
            : $"{settings.Zones.Count} zone{(settings.Zones.Count == 1 ? "" : "s")}, {settings.Zones.Count(settings.Active)} in use" +
              (settings.DetectedBy == CharacterTouchZoneSettings.ByVision && settings.DetectedAt is { } at
                ? $", found by the Thinking model on {at.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}."
                : settings.DetectedBy == CharacterTouchZoneSettings.ByEstimate
                    ? ", a first guess Martlet placed with no AI (Detect zones has your Thinking model find them)."
                    : ", made by you.");

    /// <summary>Whether Detect zones has a model that can see its pictures, the line that says which (<c>TouchZonesVision</c>)
    /// and, when none can, why the button is off (<c>TouchZonesDetectNote</c>), from <see cref="SenseJobRouting"/>'s decision for
    /// <see cref="SenseJobKind.HelperPicture"/> (<paramref name="decision"/>): a Thinking pool member that can see takes the pictures
    /// first, else the image model of its own while pictures go to it (Companion › Vision), else the Thinking model
    /// (<paramref name="thinking"/>) unless it is known to be text-only. With <paramref name="fixture"/> a FIXTURE - NOT AI stand-in
    /// answers instead.</summary>
    internal static (bool CanSee, string Line, string? Off) SightOf(SenseDecision decision, SetupRoute? thinking, ModelAbilities? abilities,
        bool fixture)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (fixture) return (true, "FIXTURE - NOT AI: a stand-in answers Detect zones from a file, so no picture is sent.", null);
        var advice = LiveConversationConfiguration.VisionAdvice(thinking, abilities);
        return decision.Taker switch
        {
            SenseTaker.PoolMember => (true, $"{decision.Name} in your Thinking pool can see pictures, so it finds the zones first." +
                (decision.Then is { Taker: SenseTaker.SenseModel } then ? $" Otherwise your image model, {then.Name}, does."
                    : thinking is null ? "" : " " + advice), null),
            SenseTaker.SenseModel =>
                (true, $"Your image model, {decision.Name} (Companion › Vision), finds the zones: pictures go to it, not to the Thinking model.", null),
            SenseTaker.ThinkingModel => (true, advice, null),
            _ => (false, advice, thinking is null
                ? "Detect zones is off: no model that can see pictures is set up; set up a vision-capable model in Companion › Thinking or Companion › Thinking pool."
                : "Detect zones is off: the Thinking model is text-only and no Thinking pool member can see pictures; " +
                  "choose a vision-capable model in Companion › Thinking or Companion › Thinking pool.")
        };
    }
}
