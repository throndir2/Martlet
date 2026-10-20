using Martlet.Avatar.Audio2Face;
using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;

namespace Martlet.Desktop;

/// <summary>Companion › Lip sync's decisions and words, without WPF. The page makes one each time it draws, with lip-sync's list as
/// the page read it: what the Now line says, each list member's state and settings lines, and what the Audio2Face on this PC card
/// says. <see cref="LipSyncPage"/> draws it, and <see cref="ILipSyncPageServices"/> is all it reads in the app.</summary>
internal sealed class LipSyncPageModel
{
    private readonly ILipSyncPageServices services;

    internal LipSyncPageModel(ILipSyncPageServices services, PoolList? list)
    {
        this.services = services;
        List = list;
        NowText = Now();
    }

    /// <summary>Lip-sync's list as the page read it, or null while there is none.</summary>
    internal PoolList? List { get; }

    /// <summary>The Now line (LipSyncNow): what moves the mouth for the next sentence.</summary>
    internal string NowText { get; }

    private string Now()
    {
        if (services.CharacterLipSync == AvatarLipSync.Audio2Face)
            return $"Your own Audio2Face service on this PC ({services.OwnServiceEndpoint().Authority}), with Audio2Face-only lip-sync activated " +
                "in the character's settings.";
        var on = List?.Members.Where(m => !m.Off).ToArray() ?? [];
        if (on.Length == 0 || services.CharacterLipSync == AvatarLipSync.Loudness)
            return "Voice loudness: the mouth opens and closes with the voice, and nothing in the list below is on.";
        var lead = on[0];
        var first = lead.Kind == PoolMemberKind.ThisPc
            ? $"Your own Audio2Face service at {services.OwnServiceEndpoint().Authority}" + (services.OwnServiceAnswers == false ? ", which isn't answering now" : "")
            : $"Audio2Face on {lead.Name}" + (lead.HostId == services.ThisPcHost()?.HostId ? " (this PC)" : "") +
                (services.Reachable(lead.HostId!) == false ? ", which isn't reachable now"
                    : services.RunsAudio2Face(lead.HostId!) == false ? ", which doesn't run it yet" : "");
        return first + (on.Length == 1 ? ""
                : lead.Kind == PoolMemberKind.ThisPc ? $", or the next of {on.Length - 1} more when it doesn't answer"
                : $", or the next of {on.Length - 1} more when it is busy") +
            "; when none can, the mouth follows the voice's loudness.";
    }

    /// <summary>Whether the list can add <paramref name="host"/>: your own computer that offers Audio2Face or can run it.</summary>
    internal bool CanAdd(PairedHost host) => !host.Shared && (services.CheckOf(host.HostId)?.Offers?.ContainsKey(HostRoles.Audio2Face) == true ||
        Cannot(host.HostId) is null);

    /// <summary>A list member's state in plain words, under its row.</summary>
    internal string? MemberStatus(PoolMember member)
    {
        if (member.Kind == PoolMemberKind.ThisPc)
            return $"your own Audio2Face service at {services.OwnServiceEndpoint(member).Authority}" + services.OwnServiceAnswers switch
            {
                true => "; answering",
                false => "; not answering now",
                _ => ""
            };
        var hostId = member.HostId!;
        var parts = new List<string>();
        if (hostId == services.ThisPcHost()?.HostId) parts.Add("this PC's host service");
        if (services.FindHost(hostId) is null) parts.Add("not paired on this PC, so this PC skips it");
        else
        {
            parts.Add(services.Reachable(hostId) == false ? "not reachable right now"
                : services.RunsAudio2Face(hostId) switch
                {
                    true => "runs Audio2Face" + (OfferedModel(hostId) is { } model ? $" ({model})" : ""),
                    false => "doesn't run Audio2Face yet",
                    _ => "not checked yet"
                });
        }
        return string.Join("; ", parts);
    }

    /// <summary>The Audio2Face model the last check of <paramref name="hostId"/> saw, or null.</summary>
    internal string? OfferedModel(string hostId) => services.CheckOf(hostId)?.Offers?.GetValueOrDefault(HostRoles.Audio2Face);

    /// <summary>Why <paramref name="hostId"/> can't run Audio2Face for lip-sync, or null.</summary>
    internal string? Cannot(string hostId) => services.CannotHand(hostId, HostRoles.Audio2Face, ClusterJobs.LipSync);

    /// <summary>Whether <paramref name="hostId"/> runs Audio2Face for lip-sync now.</summary>
    internal bool Runs(string hostId) => services.RunsAudio2Face(hostId) == true;

    /// <summary>A computer member's settings line: whether Audio2Face runs there, why it can't, or what it needs.</summary>
    internal string ComputerText(string hostId) =>
        Runs(hostId) ? $"Audio2Face{(OfferedModel(hostId) is { } model ? $" {model}" : "")} runs on {hostId}."
        : Cannot(hostId) is { } cannot ? $"Audio2Face can't run there: {cannot}"
        : $"Audio2Face needs {HostRoles.Get(HostRoles.Audio2Face).Needs}, and until it runs there this PC skips {hostId}.";

    /// <summary>The address typed for this PC's own Audio2Face service, checked (a loopback address with a port); null when it isn't
    /// one.</summary>
    internal static Uri? ParseEndpoint(string text)
    {
        try
        {
            var endpoint = new Uri(text.Trim());
            new Audio2FaceOptions { Endpoint = endpoint }.Validate();
            return endpoint;
        }
        catch (Exception error) when (error is UriFormatException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Lip-sync's saved list before and after This PC's member <paramref name="memberKey"/> gets the address
    /// <paramref name="endpoint"/> (no setting when it is the character's own address); null when there is no list or member.</summary>
    internal (PoolList Before, PoolList After)? WithEndpoint(string memberKey, Uri endpoint)
    {
        if (services.DataDirectory is not { } directory || WorkSharingRoster.Shared.Pool(directory, PoolAreas.LipSync) is not { } list ||
            list.Find(memberKey) is not { } current) return null;
        var value = endpoint.AbsoluteUri == services.CharacterEndpoint.AbsoluteUri ? null : endpoint.AbsoluteUri;
        return (list, list.With(current.WithSetting(PoolSettingKeys.Endpoint, value)));
    }

    // ---------- Audio2Face on this PC ----------

    /// <summary>Whether this PC's graphics card suits Audio2Face (NVIDIA with 4 GB or more).</summary>
    internal bool ThisPcFits => services.Machine.BestGpu is { IsNvidia: true } gpu && (gpu.MemoryGb ?? 0) >= 4;

    /// <summary>Why Audio2Face can't run on this PC's processor, or null.</summary>
    internal string? ThisPcRefusal => services.Machine.ArmRefusal("audio2face");

    /// <summary>What this PC's graphics card means for Audio2Face (LipSyncDockerAbout); a warning unless it fits.</summary>
    internal string ThisPcAbout
    {
        get
        {
            var gpu = services.Machine.BestGpu;
            return ThisPcRefusal ?? (gpu is null ? "No dedicated graphics card was found on this PC; Audio2Face needs an NVIDIA graphics card with 4 GB or more."
                : $"This PC has {gpu.Describe()}" + (ThisPcFits ? "." : ", but Audio2Face needs an NVIDIA graphics card with 4 GB or more, so " +
                    "another computer or the voice's loudness suits this PC better."));
        }
    }

    /// <summary>What setting Audio2Face up here does, by whether Docker Desktop runs (LipSyncThisPcSetUp).</summary>
    internal string ThisPcSetUpText => "Set up Audio2Face here to put this PC first in the list. " +
        (services.Machine.DockerRunning ? "Docker Desktop is running. "
            : services.Machine.DockerInstalled ? "Docker Desktop is installed, and Martlet starts it when needed. "
            : "Docker Desktop isn't installed yet, so Martlet installs it first. ") +
        "Setting this up installs Martlet's host service and Audio2Face with its open-source engine (no NVIDIA account).";

    /// <summary>Whether Audio2Face runs in this PC's host service <paramref name="thisPc"/>, in words.</summary>
    internal string ThisPcHostText(PairedHost thisPc)
    {
        var listed = List?.Members.Any(m => m.HostId == thisPc.HostId) == true;
        return Runs(thisPc.HostId) ? $"Audio2Face runs in this PC's host service ({thisPc.HostId})" + (listed ? "." : "; add it to the list above to use it.")
            : $"Audio2Face isn't installed in this PC's host service ({thisPc.HostId}) yet; installing it puts this PC first in the list.";
    }
}
