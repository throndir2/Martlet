using Martlet.Avatar.Hosting;

namespace Martlet.Desktop.Tests;

/// <summary>A touched zone's voice sounds ("sound:&lt;cue&gt;" entries) go to the sound player with the zone, in list order, and
/// never while the zone rests.</summary>
public sealed class TouchVoiceSoundTests
{
    [Fact]
    public void A_touch_asks_for_the_zones_voice_sounds_but_not_while_the_zone_rests()
    {
        var service = new CharacterTouchZoneService(null);
        var asked = new List<(string Zone, IReadOnlyList<string> Cues, string Why)>();
        var touch = new CharacterTouch(0.5, 0.1, [], [], "head", null, false, null, null);
        TouchReactionPlan Plan(CharacterTouchZone zone, int repeats) => new([], Sounds: ["gasp", "laugh"]);
        Assert.NotNull(service.React(touch, Plan, (_, _, _) => Task.CompletedTask, _ => { },
            sound: (zone, cues, why) => asked.Add((zone.Id, cues, why))));
        var (_, cues, why) = Assert.Single(asked);
        Assert.Equal(["gasp", "laugh"], cues);
        Assert.StartsWith("a touch on ", why);
        Assert.Contains("voice sound gasp or laugh", service.LastMatch);
        service.React(touch, Plan, (_, _, _) => Task.CompletedTask, _ => { }, sound: (zone, cues, why) => asked.Add((zone.Id, cues, why)));
        Assert.Single(asked);
        Assert.Contains("resting", service.LastMatch);
    }

    [Fact]
    public void A_zone_without_voice_sounds_asks_for_none()
    {
        var service = new CharacterTouchZoneService(null);
        var asked = 0;
        var touch = new CharacterTouch(0.5, 0.1, [], [], "head", null, false, null, null);
        Assert.NotNull(service.React(touch, (_, _) => new TouchReactionPlan([]), (_, _, _) => Task.CompletedTask, _ => { },
            sound: (_, _, _) => asked++));
        Assert.Equal(0, asked);
        Assert.DoesNotContain("voice sound", service.LastMatch);
    }
}
