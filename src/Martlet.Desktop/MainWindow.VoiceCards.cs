using System.Windows.Controls;
using Martlet.Core.Installation;
using Martlet.Core.Settings;
using Martlet.Core.Voices;
using Martlet.F5;

namespace Martlet.Desktop;

/// <summary>The services MainWindow gives the cards of Companion › Voice (<see cref="VoiceCards"/> in Pages\Voice). The work the
/// cards start stays in the window (MainWindow.VoiceEngines.cs and MainWindow.Voices.cs). The job page (MainWindow.JobPages.cs)
/// still calls the cards by their old names, which forward to the cards.</summary>
public partial class MainWindow : IVoiceCardsServices
{
    private VoiceCards? voiceCards;

    /// <summary>The Voice cards, made once: they keep the computer the engine list shows while the window lives.</summary>
    private VoiceCards VoiceCards => voiceCards ??= new(this);

    private Border VoiceEnginesCard(SetupRoute? route, PairedHost? thisPc, bool onThisPc) => VoiceCards.VoiceEngines(route, thisPc, onThisPc);
    private Border VoicesCard(SetupRoute? route) => VoiceCards.Voices(route);
    private Border ChatterboxStyleCard() => VoiceCards.ChatterboxStyleCard();
    internal static string StyleState(ChatterboxStyle style) => VoiceCards.StyleState(style);
    private static List<VoiceItem> VoiceItems(SpeakingVoiceLibrary library, IReadOnlyDictionary<string, F5ReferenceSnapshot> local) =>
        VoiceItem.List(library, local);

    AppSettings? IVoiceCardsServices.Settings => homeSettings;
    MachineInfo IVoiceCardsServices.Machine => machine;
    IReadOnlyList<PairedHost> IVoiceCardsServices.Hosts() => NetworkMap.Hosts(Inputs());
    HostCheck? IVoiceCardsServices.HostCheck(string hostId) => hostChecks.GetValueOrDefault(hostId);
    HostHardware? IVoiceCardsServices.Hardware(string hostId) => HardwareStore?.Find(hostId);
    string? IVoiceCardsServices.PendingSpeakingHost => pendingJobHosts.GetValueOrDefault(SetupRole.Tts);
    string? IVoiceCardsServices.ThisPcCannot(SpeechEngine engine) => ThisPcCannot(engine);
    string? IVoiceCardsServices.CannotHand(string hostId, string roleKind, string job) => CannotHand(hostId, roleKind, job);
    string IVoiceCardsServices.F5Destination => F5Destination;
    SpeakingVoiceLibrary? IVoiceCardsServices.VoiceLibrary() => store is null ? null : F5Voices.View(store.DataDirectory);
    (IReadOnlyDictionary<string, F5ReferenceSnapshot> Local, F5ReferenceSnapshot? Applied) IVoiceCardsServices.LocalVoices(string destination) =>
        F5Voices.Local(store!.DataDirectory, destination);
    string IVoiceCardsServices.SharedVoicesStatus => speakingVoiceStatus;
    ChatterboxStyle IVoiceCardsServices.LoadChatterboxStyle() =>
        store?.DataDirectory is { } directory ? ChatterboxStyle.Load(directory) : ChatterboxStyle.Default;

    bool IVoiceCardsServices.SaveChatterboxStyle(ChatterboxStyle style)
    {
        if (store?.DataDirectory is not { } directory) return false;
        style.Save(directory);
        return true;
    }

    StackPanel IVoiceCardsServices.OptionPickerBody(string id, IReadOnlyList<PickerOption> options) => OptionPickerBody(id, options);

    void IVoiceCardsServices.RunNodeAction(NodeAction action, string? argument) => RunNodeAction(action, argument);
    void IVoiceCardsServices.OpenDevices() => Navigate(NavDevices);
    void IVoiceCardsServices.Redraw() => RenderTab();
    void IVoiceCardsServices.UseVoiceEngine(SpeechEngine engine, PairedHost? host) => UseVoiceEngineAsync(engine, host).Forget();
    void IVoiceCardsServices.StopIdleVoiceEngines(string hostId, SpeechEngine speaking) => StopIdleVoiceEnginesAsync(hostId, speaking).Forget();
    Task IVoiceCardsServices.PlayVoice(VoiceItem item) => PlayVoiceAsync(item);
    Task IVoiceCardsServices.PlayApplied(F5ReferenceSnapshot? applied, string name) => PlayAppliedAsync(applied, name);
    Task IVoiceCardsServices.UseVoice(string voiceId, string destination) => UseVoiceAsync(voiceId, destination);
    Task IVoiceCardsServices.RemoveVoice(VoiceItem item, string destination) => RemoveVoiceAsync(item, destination);
    void IVoiceCardsServices.AddVoice(string destination) => AddVoiceAsync(destination).Forget();
    void IVoiceCardsServices.RecordVoice(string destination) => RecordVoiceAsync(destination).Forget();
}
