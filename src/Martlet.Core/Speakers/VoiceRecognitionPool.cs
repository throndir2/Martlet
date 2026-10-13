using Martlet.Core.Cluster;

namespace Martlet.Core.Speakers;

/// <summary>One place that can recognize who speaks (<see cref="VoiceRecognitionPool.Targets"/>): this PC's processor
/// (<see cref="HostId"/> null) or a paired computer's Voice recognition role. <see cref="Key"/> is its "computer" in
/// <see cref="WorkQueue"/>: "this-pc", or "host:&lt;id&gt;" (each computer once, whichever member named it). <see cref="Fallback"/>:
/// this PC was not on in the list and is only there because recognition must keep working.</summary>
public sealed record VoiceRecognitionTarget(string Key, string? HostId, PoolMember Member, bool Fallback = false)
{
    /// <summary>This PC's own engine (its processor).</summary>
    public bool Here => HostId is null;
}

/// <summary>Companion › People's Voice recognition list (<see cref="PoolAreas.VoiceRecognition"/>, docs/VOICES.md#where-voice-recognition-runs):
/// the places that recognize who speaks, in order. <see cref="PoolMemberKind.ThisPc"/> is this PC's processor (sherpa-onnx in
/// Martlet); a computer or one of its cards runs the <c>voice-recognition</c> host role (the same models and steps, on a graphics
/// card or a processor there; a card is its computer, which picks the card). The list is shared by all the owner's computers.
/// Recognition must keep working: when nothing is on, the list is empty or no computer can take it, this PC's processor
/// recognizes, so this PC is always the last place tried. Pure: no files, no network.</summary>
public static class VoiceRecognitionPool
{
    public const string Role = "voice-recognition";
    public const string RouteId = "martlet.gateway.voice-recognition.v1";

    public static PoolArea Area => PoolAreas.VoiceRecognition;

    /// <summary>The first list: this PC's processor, where recognition always ran before the list.</summary>
    public static PoolList Default() => new() { Area = Area.Id, Members = [PoolMember.ThisPc()] };

    /// <summary>Where <paramref name="member"/> recognizes, in words: "this PC's processor" or "gpu-pc's Voice recognition role".</summary>
    public static string Describe(PoolMember member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return member.Kind == PoolMemberKind.ThisPc ? "this PC's processor" : $"{member.HostId}'s Voice recognition role";
    }

    /// <summary>The places companion PC <paramref name="device"/> tries, first to last (<see cref="PoolRouting.Order"/>): the members
    /// that are on, kept for it and paired here (<paramref name="paired"/>), each computer once, then this PC's processor when the
    /// list doesn't have it on (<see cref="VoiceRecognitionTarget.Fallback"/>). Null <paramref name="list"/>: <see cref="Default"/>.
    /// Never empty.</summary>
    public static IReadOnlyList<VoiceRecognitionTarget> Targets(PoolList? list, string device, Func<string, bool> paired)
    {
        ArgumentNullException.ThrowIfNull(paired);
        bool Usable(PoolMember member) => member.Kind == PoolMemberKind.ThisPc || member.HostId is { } host && paired(host);
        List<VoiceRecognitionTarget> targets = [];
        foreach (var member in PoolRouting.Order(Area, list ?? Default(), device, Usable).Members)
        {
            var key = member.Kind == PoolMemberKind.ThisPc ? PoolMember.ThisPcKey : "host:" + member.HostId;
            if (targets.All(t => t.Key != key)) targets.Add(new(key, member.Kind == PoolMemberKind.ThisPc ? null : member.HostId, member));
        }
        if (targets.All(t => !t.Here)) targets.Add(new(PoolMember.ThisPcKey, null, PoolMember.ThisPc(), Fallback: true));
        return targets;
    }

    /// <summary>What the list means now, in one line, for its page and Devices › Sharing work.</summary>
    public static string Summary(IReadOnlyList<VoiceRecognitionTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var hosts = targets.Where(t => !t.Here).Select(t => t.HostId!).ToArray();
        if (hosts.Length == 0) return "Martlet recognizes voices on this PC's processor.";
        var first = targets[0].Here ? "this PC's processor" : $"{targets[0].HostId}'s Voice recognition role";
        return $"Martlet recognizes voices with {first} first" + (targets.Count > 2 ? $", then the next of the {targets.Count} places in order" : "") +
            ". When no computer answers in time, this PC's processor recognizes them.";
    }
}
