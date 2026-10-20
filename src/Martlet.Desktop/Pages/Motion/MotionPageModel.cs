using Martlet.Avatars;
using Martlet.Core.Cluster;
using Martlet.Core.Motion;
using Martlet.HostClient;

namespace Martlet.Desktop;

/// <summary>Companion › Motion's decisions, without WPF. The page makes one each time it draws: it reads the Motion list, the
/// computers motions go to and the older choice once, and says what Now, the character line, each member's line and each
/// member's Settings show. <see cref="MotionPage"/> draws it, and <see cref="IMotionPageServices"/> is all it reads in the app.</summary>
internal sealed class MotionPageModel
{
    private readonly IMotionPageServices services;

    internal MotionPageModel(IMotionPageServices services)
    {
        this.services = services;
        var directory = services.DataDirectory;
        var list = directory is null ? null : WorkSharingRoster.Shared.Pool(directory, PoolAreas.Motion);
        var members = directory is null ? [] : MotionClient.Members(directory);
        var older = directory is null ? new MotionSettings() : MotionClient.Settings(directory);
        var thisPc = services.ThisPcHost()?.HostId;
        string Name(string hostId) => hostId == thisPc ? "this PC" : hostId;
        NowText = Now(list is not null, list?.NoneOn ?? false, older, [.. members.Select(m => Name(m.HostId)).Distinct()], MotionClient.Fixture);
        var renderer = services.ShownRenderer;
        CharacterText = Character(renderer);
        CharacterWarning = renderer is not null && renderer != AvatarRenderer.Vrm;
    }

    /// <summary>Now's first line (MotionNow).</summary>
    internal string NowText { get; }
    /// <summary>Whether the character shown can play body motions, in words (MotionCharacter).</summary>
    internal string CharacterText { get; }
    /// <summary>Whether the character line is a warning: the character shown isn't a VRM.</summary>
    internal bool CharacterWarning { get; }

    /// <summary>What the Motion role is, in a few sentences (role.conf's facts and terms in short).</summary>
    internal const string About =
        "Motion runs Tencent's HY-Motion 1.0 on an NVIDIA graphics card with 24 GB or more, and its license doesn't let you use it in " +
        "the European Union, the United Kingdom or South Korea. It runs in Docker; the standard model needs 26 GB and the computer " +
        "about 32 GB of memory. It downloads about 20 GB once, loads when the first motion is made and frees the card three minutes " +
        "after the last one, so the voice and thinking get it back. Only the short English description of the movement goes to that " +
        "computer, and it's free. Its license is Tencent's HY-Motion 1.0 Community License.";

    /// <summary>Companion › Motion's Now line (MotionNow). <paramref name="where"/>: the computers motions go to, in order (the
    /// list's members that are on and paired here; before the list, the older choice's). <paramref name="listed"/>: the list
    /// exists; <paramref name="noneOn"/>: no member in it is on.</summary>
    internal static string Now(bool listed, bool noneOn, MotionSettings older, IReadOnlyList<string> where, bool fixture)
    {
        if (fixture) return "Martlet makes motions with the FIXTURE - NOT AI test maker on this PC (MARTLET_MOTION_FIXTURE=1).";
        if (listed && noneOn) return $"Off. {OptionalExtras.OffMeans(CompanionTab.Motion)}. No computer in the list is on.";
        if (!listed && older.Place == MotionPlace.Off) return $"Off. {OptionalExtras.OffMeans(CompanionTab.Motion)}.";
        if (where.Count == 0)
            return listed
                ? "No computer in the list can make motions for this PC: pair one on Devices, or add one below."
                : "Not set up yet: add one of your computers with a 24 GB NVIDIA graphics card to the list below and set Motion up there.";
        var place = where.Count == 1 ? where[0] : $"{where[0]} (or the first free one of {where.Count} computers)";
        return $"Martlet makes body motions on {place}.";
    }

    /// <summary>Whether the character shown (else the chosen one) can play body motions: only a VRM can.</summary>
    internal static string Character(AvatarRenderer? renderer) => renderer switch
    {
        AvatarRenderer.Vrm => "Your character is a VRM, so replies can make and play body motions.",
        null => "Body motions play only on VRM characters; choose one on Companion › Character.",
        _ => "Your character is a Live2D model and body motions play only on VRM characters, so replies don't make or play them now " +
            "(Live2D characters keep their own model's motions)."
    };

    /// <summary>The paired computer a motion pool member makes motions on: this PC's own host service for This PC, else the
    /// computer; null when it isn't paired here (or this PC has no host service yet). Never a host a friend shares.</summary>
    internal PairedHost? MemberHost(PoolMember member) =>
        member.Kind == PoolMemberKind.ThisPc ? services.ThisPcHost() : services.FindHost(member.HostId) is { Shared: false } host ? host : null;

    /// <summary>The key a setup on <paramref name="member"/> is followed by: <see cref="MainWindow.MotionThisPc"/> for this PC, else
    /// the host ID.</summary>
    internal string SetupKey(PoolMember member) =>
        member.Kind == PoolMemberKind.ThisPc || member.HostId is { } id && id == services.ThisPcHost()?.HostId
            ? MainWindow.MotionThisPc : member.HostId ?? MainWindow.MotionThisPc;

    /// <summary>The model a computer's Motion role runs, in words, or null.</summary>
    internal string? Model(PairedHost host) => services.OfferedModel(host.HostId) switch
    {
        "hy-motion-1.0-lite" => "HY-Motion-1.0-Lite",
        "hy-motion-1.0" => "HY-Motion-1.0",
        { Length: > 0 } other => other,
        _ => null
    };

    /// <summary>A motion pool member's line in the list: whether Motion runs there (and its model), is being set up, failed, or
    /// why it can't make motions.</summary>
    internal string? MemberStatus(PoolMember member)
    {
        var key = SetupKey(member);
        if (services.Pending == key) return "setting Motion up...";
        var host = MemberHost(member);
        var parts = new List<string>();
        if (host is not null && services.Offers(host, HostRoles.Motion))
            parts.Add(Model(host) is { } model ? $"runs Motion with {model}" : "runs Motion");
        else if (host is null)
            parts.Add(member.Kind == PoolMemberKind.ThisPc ? services.ThisPcCannotMove() ?? "doesn't run Motion yet; Settings sets it up" : "not paired with this PC");
        else
        {
            var reachable = services.Reachable(host.HostId);
            parts.Add(reachable == false ? "not reachable right now"
                : reachable == true ? "doesn't run Motion yet; Settings sets it up" : "not checked yet");
        }
        if (services.Failure(key) is { } failed) parts.Add("setup failed: " + failed.TrimEnd('.'));
        return string.Join("; ", parts);
    }

    /// <summary>What a motion pool member's Settings show: where Motion stands there (<see cref="MemberSettings.Text"/>, a warning
    /// when it can't run there or its setup failed) and whether Set up Motion here is shown and can be pressed.</summary>
    internal MemberSettings Settings(PoolMember member)
    {
        var host = MemberHost(member);
        var onThisPc = member.Kind == PoolMemberKind.ThisPc || host is not null && host.HostId == services.ThisPcHost()?.HostId;
        var key = SetupKey(member);
        var pending = services.Pending == key;
        var ready = host is not null && services.Offers(host, HostRoles.Motion);
        var cannot = ready ? null : onThisPc ? services.ThisPcCannotMove()
            : host is null ? "It isn't paired with this PC."
            : services.Reachable(host.HostId) == false ? "It isn't reachable right now."
            : services.CannotHand(host.HostId, HostRoles.Motion, "motion");
        var failure = services.Failure(key);
        var state = pending ? "Setting Motion up here..."
            : ready ? (Model(host!) is { } model ? $"Motion is ready here with {model}." : "Motion is ready here.")
            : cannot ?? (failure is { } why ? $"Setting Motion up failed: {why}" : "Motion isn't set up here yet.");
        return new(state, cannot is not null || !pending && !ready && failure is not null, ready, pending, cannot,
            onThisPc ? null : host, key, !pending && services.Pending is null && cannot is null);
    }

    /// <summary>A member's Settings: its state line and whether it is a warning; <paramref name="Ready"/>: Motion runs there (no
    /// Set up button); <paramref name="Cannot"/>: why it can't be set up there; <paramref name="SetUpOn"/> and
    /// <paramref name="Key"/>: what Set up Motion here sets up (null: this PC).</summary>
    internal sealed record MemberSettings(string Text, bool Warning, bool Ready, bool Pending, string? Cannot, PairedHost? SetUpOn,
        string Key, bool CanSetUp);
}
