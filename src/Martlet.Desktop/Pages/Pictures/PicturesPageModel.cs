using Martlet.Core.Cluster;
using Martlet.Core.Pictures;
using Martlet.Core.Settings;
using Martlet.HostClient;
using Martlet.Providers.Pictures;

namespace Martlet.Desktop;

/// <summary>Companion › Picture making's decisions, without WPF. The page makes one each time it draws: it reads the Picture making
/// list and the order a picture tries its places once, and says what Now, each place's state and its key line show.
/// <see cref="PicturesPage"/> draws it, and <see cref="IPicturesPageServices"/> is all it reads in the app.</summary>
internal sealed class PicturesPageModel
{
    private readonly IPicturesPageServices services;

    internal PicturesPageModel(IPicturesPageServices services)
    {
        this.services = services;
        var directory = services.DataDirectory;
        List = directory is null ? new PoolList { Area = PoolAreas.PictureMaking.Id } : PictureClient.List(directory);
        Order = directory is null ? [] : PictureClient.Order(directory).Members;
        On = PictureClient.Fixture || Order.Count > 0;
    }

    /// <summary>The Picture making list, as saved.</summary>
    internal PoolList List { get; }
    /// <summary>The places a picture tries now, in order (on and usable on this PC).</summary>
    internal IReadOnlyList<PoolMember> Order { get; }
    /// <summary>Whether pictures are on: a place can draw (or the test picture maker stands in).</summary>
    internal bool On { get; }

    /// <summary>Now's first line (PicturesNow).</summary>
    internal string NowText => Now(List, Order);

    /// <summary>Now's check line (PicturesTestState).</summary>
    internal string CheckText => services.Checking ? services.Testing ? "Drawing a test picture…" : "Checking…"
        : services.CheckResult ?? (On ? "Press Check to see whether each place can draw now." : "");

    /// <summary>Where Martlet draws, in words.</summary>
    internal static string Now(PoolList list, IReadOnlyList<PoolMember> order)
    {
        if (PictureClient.Fixture) return "FIXTURE - NOT AI: pictures come from the test picture maker (MARTLET_PICTURES_FIXTURE).";
        if (order.Count == 0)
            return list.Members.Count == 0 ? $"Off: {OptionalExtras.OffMeans(CompanionTab.Pictures)} until you add a place below."
                : "Off: no place in the Picture making list is on and usable on this PC.";
        var first = $"Martlet draws on {PictureMakingPoolMembers.Describe(order[0])}.";
        return order.Count == 1 ? first
            : first.TrimEnd('.') + $", or {string.Join(" or ", order.Skip(1).Select(PictureMakingPoolMembers.Describe))} when it is busy (in this order).";
    }

    /// <summary>A place's position in the saved list, for its settings' automation IDs.</summary>
    internal int MemberIndex(PoolMember member) =>
        services.DataDirectory is not { } directory ? -1 : PictureClient.List(directory).Members.ToList().FindIndex(m => m.Key == member.Key);

    /// <summary>A place's line in the list: its role's state, its workflow, or (a cloud provider) its cost and key.</summary>
    internal string? MemberStatus(PoolMember member)
    {
        var parts = new List<string>();
        if (member.Kind is PoolMemberKind.ThisPc or PoolMemberKind.Computer) parts.Add(RoleState(member).Text);
        if (PictureMakingPoolMembers.WorkflowText(member) is { } workflow) parts.Add("draws with " + workflow);
        if (member.Kind == PoolMemberKind.Cloud) parts.Add("each picture may cost money; " + KeyText(member));
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    /// <summary>Where Martlet's Picture making role stands for a this-PC or computer place: its words, whether it is ready, the paired
    /// host and why it can't run there (null when it can, or Martlet can't tell yet).</summary>
    internal (string Text, bool Ready, PairedHost? Host, string? Cannot) RoleState(PoolMember member)
    {
        var thisPc = member.Kind == PoolMemberKind.ThisPc;
        var host = thisPc ? services.ThisPcHost() : services.FindHost(member.HostId);
        var where = thisPc ? "this PC" : member.HostId!;
        var key = thisPc ? MainWindow.PicturesThisPc : member.HostId;
        if (services.Pending == key) return ($"setting up on {where}...", false, host, null);
        if (!thisPc && host is null) return ($"{where} isn't paired with this PC", false, null, $"{where} isn't paired with this PC.");
        if (host is not null && services.Offers(host, HostRoles.Pictures)) return ("Picture making role ready", true, host, null);
        var cannot = thisPc ? services.ThisPcCannotDraw() : services.CannotHand(member.HostId!, HostRoles.Pictures, "pictures");
        if (cannot is not null) return ("can't draw there: " + cannot.TrimEnd('.'), false, host, cannot);
        if (services.Failure is { } failed && services.FailedOn == key) return ("setup failed: " + failed.TrimEnd('.'), false, host, null);
        if (host is null) return ("not set up yet (open Settings to set it up)", false, null, null);
        return (services.Reachable(host.HostId) == false ? "not reachable right now"
            : services.Checked(host.HostId) ? "Picture making role not set up yet (open Settings to set it up)" : "not checked yet", false, host, null);
    }

    /// <summary>Which key a cloud place uses: its own, Thinking's for the same provider, or none yet.</summary>
    internal string KeyText(PoolMember member)
    {
        var name = PictureMakingPoolMembers.ProviderName(member.Provider);
        var place = services.DataDirectory is not { } directory ? null : PictureClient.CloudPlace(directory, member);
        var thinking = services.Settings.ThinkingRoute();
        return place?.CredentialId is not null ? $"its own {name} key is saved"
            : place?.UsesThinkingKey(thinking) == true ? $"it uses Thinking's {name} key"
            : $"no {name} key yet (add one in its settings)";
    }
}
