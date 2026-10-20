using System.Windows;
using System.Windows.Controls;
using Martlet.Core.Platforms;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>The services MainWindow gives Companion's job pages (<see cref="JobPage"/> in Pages\Jobs). The work the page shows
/// stays in the window (saving a route and a cloud provider in MainWindow.JobPages.cs, handing a job to a computer in
/// MainWindow.HostJobs.cs), and so do the cards that keep state in the window or that other pages show too.</summary>
public partial class MainWindow : IJobPageServices
{
    private JobPage? jobPage;

    private JobPage JobPage => jobPage ??= new(this);

    AppSettings? IJobPageServices.Settings => homeSettings;
    PairedHost? IJobPageServices.ThisPcHost() => ThisPcHost();
    string? IJobPageServices.NetworkHost(string job) => clusterEnabled ? clusterPlan.For(job)?.HostId : null;
    JobPlace? IJobPageServices.ShownPlace(CompanionTab section) => tabPlace.TryGetValue(section, out var chosen) ? chosen : null;
    JobCoverage? IJobPageServices.Problem(string job) => coverage.FirstOrDefault(c => c.Job == job && c.IsProblem);

    string IJobPageServices.RouteName(SetupRoute route) =>
        route.RouteType == SetupRouteType.LocalParakeet ? $"{PlaceName(route)}: {ParakeetName(route.ModelId)}"
        : route.RouteType == SetupRouteType.LocalWhisper ? PlaceName(route)
        : $"{PlaceName(route)}: {HostInputDialog.OptionText(route.ModelId)}";

    string? IJobPageServices.NetworkJobNote(string job) => NetworkJobNote(job);
    bool IJobPageServices.AudioMissing(bool output) => AudioMissing(output);
    bool IJobPageServices.VoiceRecognitionOn => localVoices.Active;
    bool IJobPageServices.OffersFreeKey => FreeKeyPrompt.Shows(ConfiguredProviders());
    bool IJobPageServices.CannotReply => recommendedNotice is { } notice && notice.Recommendation.CannotReply;
    bool IJobPageServices.FocusFreeKey => freeKeyFocus && freeKeyPreset == FreeKeyUse.Thinking;

    LocalApp IJobPageServices.ModelApp(SetupRoute? route)
    {
        // Which model apps answer here decides what This PC offers, so look once without being asked (loopback only).
        if (localServers is null && !lookingForLocalServers) LookForLocalServersAsync(quiet: true).Forget();
        return localApp ?? DefaultLocalApp(route, !Prerequisites.IsMissing(Prerequisites.Ollama), OtherLocalServers().Count);
    }

    IReadOnlyList<PairedHost> IJobPageServices.PairedHosts() => NetworkMap.Hosts(Inputs());
    IReadOnlyList<PairedHost> IJobPageServices.SharedHosts => sharedHosts;
    HostCheck? IJobPageServices.Check(string hostId) => hostChecks.GetValueOrDefault(hostId);
    PlatformCheck? IJobPageServices.HostCan(string hostId, string roleKind) => HostCan(hostId, roleKind);
    string? IJobPageServices.CannotHand(string hostId, string roleKind, string job) => CannotHand(hostId, roleKind, job);
    bool IJobPageServices.ChangesRolesOn(PairedHost host) => ChangesRolesOn(host);
    ModelAbilities IJobPageServices.SavedModelAbilities() => SavedModelAbilities();

    UIElement IJobPageServices.JobPoolCard(CompanionTab section, SetupRole role, SetupRoute? route) => JobPoolCard(section, role, route);
    UIElement? IJobPageServices.SituationFixtureCard() => ThinkingCards.SituationFixture();
    UIElement IJobPageServices.LocalAppCard(SetupRoute? route, LocalApp app) => ThinkingCards.LocalApp(route, app);
    UIElement IJobPageServices.LocalThinkingCard(SetupRoute? route) => ThinkingCards.LocalThinking(route);
    UIElement IJobPageServices.LocalServerCard(SetupRoute? route) => ThinkingCards.LocalServer(route);
    UIElement IJobPageServices.VoiceEnginesCard(SetupRoute? route, PairedHost? thisPc, bool onThisPc) => VoiceCards.VoiceEngines(route, thisPc, onThisPc);
    UIElement IJobPageServices.LocalListeningCard(SetupRoute? route, PairedHost? thisPc) => ListeningCards.LocalListening(route, thisPc);
    UIElement IJobPageServices.ChatterboxStyleCard() => VoiceCards.ChatterboxStyleCard();
    UIElement IJobPageServices.FallbackCard() => ThinkingCards.Fallback();
    UIElement IJobPageServices.VoicesCard(SetupRoute? route) => VoiceCards.Voices(route);
    UIElement IJobPageServices.SpeakRepliesCard() => TalkCards.SpeakReplies();
    UIElement IJobPageServices.QuickSoundsCard() => TalkCards.QuickSounds();
    UIElement IJobPageServices.TalkModeCard() => ListeningCards.TalkMode();
    UIElement IJobPageServices.EchoCard() => ListeningCards.Echo();
    UIElement IJobPageServices.PcAudioCard() => ListeningCards.PcAudio();
    UIElement? IJobPageServices.OldKeysCard(CompanionTab section, SetupRole role) => OldKeysCard(section, role);
    UIElement IJobPageServices.TextModelLine(SetupRoute? route) => TextModelLine(route);
    IEnumerable<UIElement> IJobPageServices.ToolsTestControls(SetupRoute? route) => ToolsTestControls(route);
    UIElement IJobPageServices.SituationNowLine() => SituationNowLine();
    UIElement? IJobPageServices.StandInLine(SetupRoute? route) => StandInLine(route);
    IEnumerable<UIElement> IJobPageServices.ElevenLabsFields(SetupRoute? route) => ElevenLabsFields(route);

    UIElement IJobPageServices.OptionPicker(string id, string heading, IReadOnlyList<PickerOption> options, int? rows) =>
        rows is { } shown ? OptionPicker(id, heading, null, options, rows: shown) : OptionPicker(id, heading, null, options);

    void IJobPageServices.ShowPlace(CompanionTab section, JobPlace place)
    {
        tabPlace[section] = place;
        RenderTab();
    }

    void IJobPageServices.ShowPickerOption(string pickerId, string key) => pickerShown[pickerId] = key;
    void IJobPageServices.FreeKeyFocused() => freeKeyFocus = false;
    void IJobPageServices.OpenCompanion(CompanionTab tab) => OpenCompanion(tab);
    void IJobPageServices.OpenDevices() => Navigate(NavDevices);
    void IJobPageServices.RunNodeAction(NodeAction action) => RunNodeAction(action);
    void IJobPageServices.ChangeRoleOn(PairedHost host, string roleKind) => LaunchOnHost(host, HostAction.Change(roleKind));
    void IJobPageServices.OpenFreeKey(bool nobody) => OpenFreeKey(FreeKeyPrompt.Use(true, nobody), fromReview: false);
    void IJobPageServices.OpenKeyPage(TextBlock? status) => OpenKeyPageFrom(status);
    void IJobPageServices.FocusWhenShown(Control box) => FocusWhenShown(box);
    Task IJobPageServices.AssignJob(HostJob job, string key) => AssignJobAsync(job, key);

    void IJobPageServices.SaveCloud(HostJob job, CloudProvider provider, string baseUrl, string model, string? voice, PasswordBox key,
        bool consent) => SaveCloudAsync(job, provider, baseUrl, model, voice, key, consent).Forget();

    void IJobPageServices.MarkEdited() => tabEdited = true;
}
