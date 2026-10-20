using Martlet.Avatar.Hosting;

namespace Martlet.Desktop.Tests;

/// <summary>Companion › Touch › Touch zones' page model (Pages\TouchZones), without WPF.</summary>
public sealed class TouchZonesPageModelTests
{
    [Fact]
    public void TheStatusLineSaysHowManyZonesAreInUseAndWhoPlacedThem()
    {
        Assert.StartsWith("No zones found yet for this model", TouchZonesPageModel.StatusOf(null), StringComparison.Ordinal);
        Assert.StartsWith("No zones found yet for this model", TouchZonesPageModel.StatusOf(new CharacterTouchZoneSettings { ModelId = "m" }),
            StringComparison.Ordinal);
        var zones = new CharacterTouchZoneSettings
        {
            ModelId = "m", DetectedBy = CharacterTouchZoneSettings.ByOwner,
            Zones = [new CharacterTouchZone { Id = "hair", Box = new(0.2, 0.1, 0.5, 0.2) },
                new CharacterTouchZone { Id = "left_hand", Box = new(0.1, 0.5, 0.1, 0.1), Enabled = false }]
        };
        Assert.Equal("2 zones, 1 in use, made by you.", TouchZonesPageModel.StatusOf(zones));
        Assert.Equal("2 zones, 1 in use, a first guess Martlet placed with no AI (Detect zones has your Thinking model find them).",
            TouchZonesPageModel.StatusOf(zones with { DetectedBy = CharacterTouchZoneSettings.ByEstimate }));
    }
}
