using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Avatar.Hosting;

/// <summary>One picture the vision model was sent while finding zones: its file in the sent folder, the step, what it asked
/// (Parts, Zones, Special or Check), its size in pixels and bytes, and its media type.</summary>
public sealed record TouchZoneSentPicture(string File, string Step, string Kind, int Width, int Height, int Bytes, string MediaType);

/// <summary>Where a detection's snapshot sat on the renderer page (fractions of the page) and what the renderer's probe told of the
/// model then: its drawables (with their parts), bones and own parts (character-touch-zones\&lt;model&gt;-sent\probe.json).</summary>
public sealed record TouchZoneProbeFile(TouchZoneBox Crop, RendererZoneProbe Probe);

/// <summary>What a detection sent to the vision model: when, whether a FIXTURE stood in for it, how many requests it made, each
/// picture, one line per step and whether it saved zones (so its pictures belong to the snapshot under the boxes)
/// (character-touch-zones\&lt;model&gt;-sent\sent.json).</summary>
public sealed record TouchZoneSent(DateTimeOffset At, bool Fixture, int Requests, IReadOnlyList<TouchZoneSentPicture> Pictures, IReadOnlyList<string> Steps,
    bool Saved = false)
{
    /// <summary>One plain line: how many pictures, how large, and what they showed.</summary>
    public string Describe()
    {
        var largest = Pictures.Count == 0 ? 0 : Pictures.Max(p => Math.Max(p.Width, p.Height));
        var closeUps = Pictures.Where(p => p.Kind == nameof(ZoneAskKind.Zones) && p.Step != TouchZoneDetection.MissingStep)
            .Select(p => p.Step.Replace('_', ' ')).Distinct().ToArray();
        var checks = Pictures.Count(p => p.Kind == nameof(ZoneAskKind.Check));
        var again = Pictures.Any(p => p.Step == TouchZoneDetection.MissingStep);
        var special = Pictures.Any(p => p.Kind == nameof(ZoneAskKind.Special));
        var parts = closeUps.Length switch
        {
            0 => "",
            1 => closeUps[0],
            _ => string.Join(", ", closeUps[..^1]) + " and " + closeUps[^1]
        };
        return (Fixture ? "FIXTURE - NOT AI answered these. " : "") +
            $"Thinking saw {Pictures.Count} picture{(Pictures.Count == 1 ? "" : "s")} on {At.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}, " +
            $"up to {largest} pixels on the longer side, each on a plain backdrop with a grid of tenths" +
            (parts.Length > 0 ? $": the whole character, then close-ups of the {parts}" : "") +
            (checks > 0 ? $", with its boxes drawn and numbered for {checks} check{(checks == 1 ? "" : "s")}" : "") +
            (again ? ", and the whole character again for the zones the close-ups missed" : "") +
            (special ? again ? " and for anything special to it" : ", and the whole character again for anything special to it" : "") + ".";
    }
}
