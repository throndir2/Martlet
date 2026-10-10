using Martlet.Core.Cluster;

namespace Martlet.Core.Reading;

/// <summary>One place a screen read can go (<see cref="ReadingPool.Targets"/>): Windows OCR on this PC (<see cref="HostId"/> null) or a
/// paired computer's Reading role. <see cref="Key"/> is its "computer" in <see cref="WorkQueue"/>: "this-pc" for Windows OCR,
/// "host:&lt;id&gt;" for a computer (each computer once, whichever member named it). <see cref="Member"/> is the list's member.</summary>
public sealed record ReadingTarget(string Key, string? HostId, PoolMember Member);

/// <summary>Companion › Reading's list (<see cref="PoolAreas.Reading"/>, docs/READING.md#the-reading-pool): the places that read the
/// text on the screen, in order. <see cref="PoolMemberKind.ThisPc"/> reads with Windows OCR on this PC (setting
/// <c>engine</c> = <see cref="WindowsOcr"/>, the default) or with Martlet's Reading role on this PC's own host service
/// (<c>engine</c> = <see cref="Role"/>); a computer or one of its cards reads with its Reading role (setting <c>model</c>: the
/// model set up there). An empty list, or one with nothing on, means reading is off. Pure: no files, no network.</summary>
public static class ReadingPool
{
    public const string WindowsOcr = "windows-ocr";
    public const string Role = "ocr";

    public static PoolArea Area => PoolAreas.Reading;

    /// <summary>This PC, reading with Windows OCR.</summary>
    public static PoolMember Windows() => PoolMember.ThisPc().WithSetting(PoolSettingKeys.Engine, WindowsOcr);

    /// <summary>This PC, reading with Martlet's Reading role on its own host service.</summary>
    public static PoolMember ThisPcRole() => PoolMember.ThisPc().WithSetting(PoolSettingKeys.Engine, Role);

    /// <summary>Whether <paramref name="member"/> reads with Windows OCR on this PC.</summary>
    public static bool UsesWindows(PoolMember member) =>
        member.Kind == PoolMemberKind.ThisPc && (member.Setting(PoolSettingKeys.Engine) ?? WindowsOcr) == WindowsOcr;

    /// <summary>The paired computer whose Reading role <paramref name="member"/> reads with: its own, or for this PC with the
    /// role, <paramref name="ownHost"/> (this PC's own host service). Null for Windows OCR, or this PC without a host service.</summary>
    public static string? HostOf(PoolMember member, string? ownHost) => member.Kind switch
    {
        PoolMemberKind.ThisPc => UsesWindows(member) ? null : ownHost,
        PoolMemberKind.Computer or PoolMemberKind.Gpu => member.HostId,
        _ => null
    };

    /// <summary>Where <paramref name="member"/> reads, in words: "Windows OCR on this PC", "the Reading role on this PC" or
    /// "gpu-pc's Reading role".</summary>
    public static string Describe(PoolMember member) => member.Kind switch
    {
        PoolMemberKind.ThisPc => UsesWindows(member) ? "Windows OCR on this PC" : "the Reading role on this PC",
        _ => $"{member.HostId}'s Reading role"
    };

    /// <summary>The list made once from the older choice (reading.json): empty when reading was off; This PC with Windows OCR for
    /// Windows OCR (also when nothing was chosen); for the Reading role, the computer chosen first (this PC's own host service
    /// as This PC with the role), then <paramref name="others"/> (the other computers that run the role, in the order a read
    /// tried them).</summary>
    public static PoolList FromChoice(ReadingSettings choice, string? ownHost, IEnumerable<string> others)
    {
        ArgumentNullException.ThrowIfNull(choice);
        ArgumentNullException.ThrowIfNull(others);
        List<PoolMember> members = [];
        void Add(string host)
        {
            var member = host == ownHost ? ThisPcRole() : PoolMember.Computer(host);
            if (members.All(m => m.Key != member.Key)) members.Add(member);
        }
        switch (choice.Place)
        {
            case ReadingPlace.Off:
                break;
            case ReadingPlace.Host:
                if (choice.HostId is { } chosen) Add(chosen);
                foreach (var host in others) Add(host);
                break;
            default:
                members.Add(Windows());
                break;
        }
        return new() { Area = Area.Id, Members = members };
    }

    /// <summary>The list's first member that is on as the older choice, for Home's recommended setup: Windows OCR on this PC,
    /// the Reading role on a computer (this PC's own host service for This PC with the role) or off.</summary>
    public static ReadingSettings Choice(PoolList list, string? ownHost)
    {
        ArgumentNullException.ThrowIfNull(list);
        return list.Members.FirstOrDefault(m => !m.Off) switch
        {
            null => new() { Place = ReadingPlace.Off },
            var first when UsesWindows(first) => new() { Place = ReadingPlace.ThisPc },
            var first => new() { Place = ReadingPlace.Host, HostId = HostOf(first, ownHost) }
        };
    }

    /// <summary>The places companion PC <paramref name="device"/> tries for a read, first to last (<see cref="PoolRouting.Order"/>):
    /// the members that are on and kept for it, Windows OCR as This PC, and each computer once that is paired here
    /// (<paramref name="paired"/>); This PC with the role is <paramref name="ownHost"/>.</summary>
    public static IReadOnlyList<ReadingTarget> Targets(PoolList list, string device, string? ownHost, Func<string, bool> paired)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(paired);
        bool Usable(PoolMember member) => UsesWindows(member) || HostOf(member, ownHost) is { } host && paired(host);
        List<ReadingTarget> targets = [];
        foreach (var member in PoolRouting.Order(Area, list, device, Usable).Members)
        {
            var host = HostOf(member, ownHost);
            var key = host is null ? PoolMember.ThisPcKey : "host:" + host;
            if (targets.All(t => t.Key != key)) targets.Add(new(key, host, member));
        }
        return targets;
    }
}
