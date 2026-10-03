namespace Martlet.Avatar.Audio2Face.Remote;

/// <summary>What a host's announced Martlet release (<see cref="HostNetworkView.MartletVersion"/>) means to a computer that
/// last knew <see cref="Before"/> (null when it knew none): whether it <see cref="Changed"/>, whether the host now runs at
/// least this computer's release (<see cref="Current"/>: nothing should ask to update it) and whether it was just
/// <see cref="Updated"/> from an older one, here, on another computer or by Martlet on that host itself.</summary>
public readonly record struct HostReleaseChange(string HostId, string? Before, string Now, bool Changed, bool Current, bool Updated);

/// <summary>Martlet releases as hosts report them (major.minor.patch).</summary>
public static class HostRelease
{
    /// <summary>The release in Martlet's form, or null when <paramref name="value"/> isn't one.</summary>
    public static string? Normalize(string? value) =>
        Version.TryParse(value, out var parsed) && parsed.Build >= 0 ? parsed.ToString(3) : null;

    /// <summary>Whether a host reporting <paramref name="reported"/> runs an older Martlet than <paramref name="target"/>;
    /// hosts from 0.2.0 and earlier report nothing and count as older.</summary>
    public static bool IsOlder(string? reported, string target) =>
        !Version.TryParse(target, out var wanted) || reported is null || !Version.TryParse(reported, out var have) || have < wanted;

    /// <summary>Compares what a computer running <paramref name="thisPc"/> knew of a host with what the host announces now.</summary>
    public static HostReleaseChange Compare(string hostId, string? before, string now, string thisPc)
    {
        var changed = before != now;
        var current = !IsOlder(now, thisPc);
        return new(hostId, before, now, changed, current, changed && before is not null && current && IsOlder(before, thisPc));
    }
}
