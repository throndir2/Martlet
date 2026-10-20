using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>What Companion's job pages decide without WPF: the place a job runs now, the Now line and a computer's line in Your
/// computers. <see cref="JobPage"/> shows them.</summary>
internal static class JobPageModel
{
    /// <summary>Where a job runs now: this PC (also for a route that needs no host service, and for Listening or Voice on this PC's
    /// own host service), another of your computers or a cloud provider. A computer that hasn't chosen yet shows what your Martlet
    /// network does (<paramref name="networkHost"/>), not Martlet's default.</summary>
    internal static MainWindow.JobPlace PlaceInUse(SetupRole role, SetupRoute? route, string? networkHost, string? thisPcHostId) =>
        route is null ? networkHost is not null && networkHost != thisPcHostId ? MainWindow.JobPlace.Computer : MainWindow.JobPlace.ThisPc
        : MainWindow.RunsHereWithoutHost(route) ? MainWindow.JobPlace.ThisPc
        : SelfHostSetup.IsGateway(route.RouteType)
            ? route.Gateway?.HostId == thisPcHostId && role != SetupRole.Llm ? MainWindow.JobPlace.ThisPc : MainWindow.JobPlace.Computer
            : MainWindow.JobPlace.Cloud;

    /// <summary>The Now line: where the job runs with its model and voice, and whether it is off or not confirmed; or that it
    /// isn't set up on this PC yet (when your Martlet network does it) or isn't chosen yet.</summary>
    internal static string NowStatus(CompanionTab section, SetupRoute? route, string? routeName, bool networkChose) =>
        route is null
            ? networkChose
                ? "Not set up on this PC yet."
                : section == CompanionTab.Voice
                ? "Not chosen yet: pick a voice engine below."
                : "Not chosen yet: this PC is recommended below."
            : routeName +
                MainWindow.VoiceSuffix(route) +
                (route.Enabled == false ? " (turned off)" : route.Consent is null ? " (not confirmed yet)" : "");

    /// <summary>A paired computer's line in Your computers: what it runs, why it can't, or whether Martlet can set it up there.</summary>
    internal static string ComputerDetail(bool owner, string ownerDetail, string? cannot, string? model, string engine, bool? reachable) =>
        owner ? ownerDetail
        : cannot is not null ? $"Unavailable: {cannot}"
        : model is not null ? $"Runs {engine} ({HostInputDialog.OptionText(model)})."
        : reachable == true ? $"{engine} isn't installed there yet, but Martlet can set it up."
        : reachable == false ? "Not reachable right now." : "Not checked yet.";

    /// <summary>A host a friend shares with this PC, in Your computers.</summary>
    internal static string SharedDetail(bool owner, string ownerDetail, bool offers, string engine, bool? reachable) =>
        owner ? ownerDetail.TrimEnd('.') + "; a friend shares it with this PC."
        : offers ? $"A friend shares it with this PC, and it offers {engine}."
        : reachable == true ? $"A friend shares it with this PC, but it doesn't offer {engine}, and only its owner can add it."
        : reachable == false ? "A friend shares it with this PC, but it isn't reachable right now." : "A friend shares it with this PC.";
}
