using Martlet.Core.Cluster;
using Martlet.HostClient;

namespace Martlet.Desktop;

/// <summary>The services MainWindow gives Companion › Reading (<see cref="ReadingPage"/> in Pages\Reading). The work the page
/// shows stays in the window (MainWindow.Reading.cs).</summary>
public partial class MainWindow : IReadingPageServices
{
    private ReadingPage? readingPage;

    /// <summary>Companion › Reading, made once for the window's life.</summary>
    private ReadingPage ReadingPage => readingPage ??= new(this, PoolListCard);

    string? IReadingPageServices.DataDirectory => store?.DataDirectory;
    TalkPreferences IReadingPageServices.Talk => Talk;
    bool? IReadingPageServices.WindowsReads => readingWindows;
    bool IReadingPageServices.WindowsChecking => readingWindowsBusy;
    string? IReadingPageServices.Pending => readingPending;
    string? IReadingPageServices.Failure(string place) => readingFailures.GetValueOrDefault(place);
    string? IReadingPageServices.PickedModel(string place) => readingModels.GetValueOrDefault(place);
    bool IReadingPageServices.Testing => readingTesting;
    string? IReadingPageServices.TestState => readingTestState;
    string? IReadingPageServices.TestText => readingTestText;
    bool IReadingPageServices.Comparing(string id) => pickerCompare.Contains(id);

    PairedHost? IReadingPageServices.ThisPcHost() => ThisPcHost();
    PairedHost? IReadingPageServices.FindHost(string? hostId) => FindHost(hostId);
    PairedHost? IReadingPageServices.FindSharedHost(string? hostId) => FindSharedHost(hostId);
    HostCheck? IReadingPageServices.Check(string hostId) => hostChecks.GetValueOrDefault(hostId);
    bool IReadingPageServices.Offers(PairedHost host, string kind) => Offers(host, kind);
    string? IReadingPageServices.CannotHand(string hostId, string roleKind, string job) => CannotHand(hostId, roleKind, job);
    bool? IReadingPageServices.HostHasNvidia(string hostId) => HardwareStore?.Find(hostId) is { } report
        ? report.Gpus.Any(g => g.IsNvidia) && report.NvidiaContainers != "no"
        : null;
    bool? IReadingPageServices.ThisPcHasNvidia => ReferenceEquals(machine, MachineInfo.Unknown) ? null : machine.Gpus.Any(g => g.IsNvidia);

    void IReadingPageServices.CheckWindows() => CheckWindowsReadingAsync().Forget();
    void IReadingPageServices.SaveTalk(TalkPreferences next, bool render) => SaveTalk(next, render);
    void IReadingPageServices.SaveMember(PoolMember next, string done) => SaveReadingMember(next, done);
    void IReadingPageServices.PickModel(string place, string modelKey) => readingModels[place] = modelKey;
    void IReadingPageServices.ToggleCompare(string id)
    {
        if (!pickerCompare.Remove(id)) pickerCompare.Add(id);
    }
    void IReadingPageServices.SetUp(PairedHost? host, OptionalExtras.ReadingRoleModel model, bool changing) =>
        SetUpReadingAsync(host, model, changing).Forget();
    void IReadingPageServices.ReadScreenNow() => ReadScreenNowAsync().Forget();
    void IReadingPageServices.Redraw() => RenderTab();
}
