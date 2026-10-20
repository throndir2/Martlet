using System.Windows.Controls;
using Martlet.Audio;
using Martlet.Core.Settings;
using Martlet.HostClient;
using Martlet.Sherpa;

namespace Martlet.Desktop;

/// <summary>The services MainWindow gives the Companion › Listening cards (<see cref="ListeningCards"/> in Pages\Listening) and
/// the Companion › Voice talk cards (<see cref="TalkCards"/> in Pages\Talk). The work the cards show stays in the window
/// (MainWindow.Talk.cs and MainWindow.Listening.cs).</summary>
public partial class MainWindow : IListeningCardsServices, ITalkCardsServices
{
    private ListeningCards? listeningCards;
    private TalkCards? talkCards;

    private ListeningCards ListeningCards => listeningCards ??= new(this);
    private TalkCards TalkCards => talkCards ??= new(this);

    // The job pages (MainWindow.JobPages.cs) call the cards by these names.

    // ---------- IListeningCardsServices ----------

    TalkPreferences IListeningCardsServices.Talk => Talk;
    void IListeningCardsServices.SaveTalk(TalkPreferences next, bool render) => SaveTalk(next, render);

    (string? Judge, bool Available, string? Problem, TimeSpan? LoadTime, Martlet.Conversation.EndOfTurnDecision[] Decisions)?
        IListeningCardsServices.TurnJudgeStatus => conversation?.TurnJudgeStatus;
    void IListeningCardsServices.KeepTurnJudgeLine(TextBlock line) => turnJudgeStatus = line;
    LiveConversationConfiguration? IListeningCardsServices.ConversationConfiguration => conversation?.Configuration;
    IReadOnlyList<Martlet.Conversation.EarlyReplyRecord> IListeningCardsServices.EarlyReplyRecords => conversation?.EarlyReplies ?? [];
    void IListeningCardsServices.KeepEarlyRepliesLine(TextBlock line) => earlyRepliesStatus = line;
    Voiceprint? IListeningCardsServices.VoiceIdEnrolled => voiceIdentity.Current;
    string? IListeningCardsServices.VoiceIdLoadError => voiceIdentity.LoadError;
    void IListeningCardsServices.SetUpVoiceId() => SetUpVoiceId();

    EchoReductionReport? IListeningCardsServices.EchoReport => conversation?.EchoReport;
    bool? IListeningCardsServices.CanHearPc => conversation?.CanHearPc;
    bool? IListeningCardsServices.PcWithoutMartlet => conversation?.PcWithoutMartlet;
    string? IListeningCardsServices.PcOutput => conversation?.PcOutput;
    SoundDigestStatus? IListeningCardsServices.SoundDigest => conversation?.SoundDigest?.Status;
    (string? Name, SoundJudgeKind? Kind) IListeningCardsServices.SavedSoundJudge() => SavedSoundJudge();
    TimeProvider IListeningCardsServices.Clock => conversation?.Clock ?? TimeProvider.System;
    Chattiness? IListeningCardsServices.DecidedChattiness => conversation?.DecidedChattiness;

    MachineInfo IListeningCardsServices.Machine => machine;
    bool IListeningCardsServices.GpuProbed => gpuProbe is not null;
    void IListeningCardsServices.ProbeGpu() => ProbeGpuAsync().Forget();
    void IListeningCardsServices.RecheckGpu()
    {
        gpuProbe = null;
        ProbeGpuAsync().Forget();
        RenderTab();
    }
    ListeningAdvice IListeningCardsServices.ListeningAdvice() => ListeningAdviceNow();
    string? IListeningCardsServices.SttAccelerator => gpuProbe?.SttAccelerator;
    bool IListeningCardsServices.HasParakeet => parakeet is not null;
    bool IListeningCardsServices.ParakeetInstalled(string modelId) => parakeet?.Installed(modelId) == true;
    string? IListeningCardsServices.InstallingParakeet => installingParakeet;
    string? IListeningCardsServices.ParakeetProgress => parakeetProgress;
    void IListeningCardsServices.KeepParakeetLine(TextBlock? line) => parakeetState = line;
    void IListeningCardsServices.UseParakeet(ParakeetModel model) => UseParakeetAsync(model).Forget();
    void IListeningCardsServices.UseListeningHere(bool gpu) => UseListeningHereAsync(gpu).Forget();
    void IListeningCardsServices.CheckHost(string hostId) => RunNodeAction(NodeAction.CheckHost, hostId);
    StackPanel IListeningCardsServices.OptionPickerBody(string id, IReadOnlyList<PickerOption> options) => OptionPickerBody(id, options);

    // ---------- ITalkCardsServices ----------

    TalkPreferences ITalkCardsServices.Talk => Talk;
    void ITalkCardsServices.SaveTalk(TalkPreferences next, bool render) => SaveTalk(next, render);
    void ITalkCardsServices.KeepSpeakRepliesCheck(CheckBox check) => speakRepliesCheck = check;
    void ITalkCardsServices.KeepQuickSoundsLine(TextBlock status, Button make)
    {
        quickSoundsStatus = status;
        quickSoundsMake = make;
        ShowQuickSounds();
    }
    void ITalkCardsServices.MakeQuickSounds()
    {
        if (conversation?.MakeQuickSounds() != true) ActionText.Text = "Quick sounds need a voice: choose one above first.";
    }
}
