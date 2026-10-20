using System.Windows.Controls;
using Martlet.Audio;
using Martlet.Core.Settings;
using Martlet.Sherpa;

namespace Martlet.Desktop;

/// <summary>Everything the Companion › Listening cards (<see cref="ListeningCards"/>) read and start in the app. MainWindow gives
/// it to the cards (MainWindow.ListeningCards.cs). MainWindow keeps the work the cards show (rule 4 of Pages\README.md): saving
/// the talk choices, the graphics card check, the Parakeet download, the Whisper setup and Voice ID. The status lines that
/// follow the conversation go back to the window, so it can update them while the page shows.</summary>
internal interface IListeningCardsServices
{
    // ---------- the talk choices (talk-preferences.json) ----------

    /// <summary>The saved talk choices.</summary>
    TalkPreferences Talk { get; }

    /// <summary>Saves <paramref name="next"/>, and with <paramref name="render"/> draws the page again.</summary>
    void SaveTalk(TalkPreferences next, bool render = false);

    // ---------- How you talk ----------

    /// <summary>The conversation's end-of-turn judge status, or null without a conversation.</summary>
    (string? Judge, bool Available, string? Problem, TimeSpan? LoadTime, Martlet.Conversation.EndOfTurnDecision[] Decisions)? TurnJudgeStatus { get; }

    /// <summary>Keeps the judge status line, so it follows each decision.</summary>
    void KeepTurnJudgeLine(TextBlock line);

    /// <summary>The conversation's configuration, or null.</summary>
    LiveConversationConfiguration? ConversationConfiguration { get; }

    /// <summary>The newest replies started early (no words or audio), or none without a conversation.</summary>
    IReadOnlyList<Martlet.Conversation.EarlyReplyRecord> EarlyReplyRecords { get; }

    /// <summary>Keeps the Start replies early status line, so it follows each reply started early.</summary>
    void KeepEarlyRepliesLine(TextBlock line);

    /// <summary>The enrolled Voice ID, or null.</summary>
    Voiceprint? VoiceIdEnrolled { get; }

    /// <summary>Why Voice ID couldn't be read, or null.</summary>
    string? VoiceIdLoadError { get; }

    /// <summary>Opens the Voice ID setup.</summary>
    void SetUpVoiceId();

    // ---------- Speakers and echo, Watch along ----------

    /// <summary>How the last listen reduced echo, or null.</summary>
    EchoReductionReport? EchoReport { get; }

    /// <summary>Whether the conversation can hear what this PC plays (null: no conversation).</summary>
    bool? CanHearPc { get; }

    /// <summary>Whether the conversation hears this PC without Martlet's own voice (null: not known).</summary>
    bool? PcWithoutMartlet { get; }

    /// <summary>The output the conversation hears, or null.</summary>
    string? PcOutput { get; }

    /// <summary>The PC sound descriptions' status, or null.</summary>
    SoundDigestStatus? SoundDigest { get; }

    /// <summary>The sound judge that the saved settings choose.</summary>
    (string? Name, SoundJudgeKind? Kind) SavedSoundJudge();

    /// <summary>The conversation's clock, or the system clock.</summary>
    TimeProvider Clock { get; }

    /// <summary>How chatty Martlet decided to be, or null.</summary>
    Chattiness? DecidedChattiness { get; }

    // ---------- How it listens on this PC ----------

    /// <summary>What Martlet knows about this PC.</summary>
    MachineInfo Machine { get; }

    /// <summary>Whether the graphics card check is done.</summary>
    bool GpuProbed { get; }

    /// <summary>Starts the graphics card check; the page draws again when it is done.</summary>
    void ProbeGpu();

    /// <summary>Checks the graphics card again and draws the page again.</summary>
    void RecheckGpu();

    /// <summary>Where Whisper suits this PC best (after the graphics card check).</summary>
    ListeningAdvice ListeningAdvice();

    /// <summary>How Whisper in this PC's host service runs ("gpu" or "cpu"), or null.</summary>
    string? SttAccelerator { get; }

    /// <summary>Whether Parakeet can run inside Martlet here.</summary>
    bool HasParakeet { get; }

    /// <summary>Whether the Parakeet model <paramref name="modelId"/> is downloaded.</summary>
    bool ParakeetInstalled(string modelId);

    /// <summary>The Parakeet model downloading now, or null.</summary>
    string? InstallingParakeet { get; }

    /// <summary>How far the Parakeet download is, or null.</summary>
    string? ParakeetProgress { get; }

    /// <summary>Keeps the downloading model's progress line (null: none shows), so the download updates it.</summary>
    void KeepParakeetLine(TextBlock? line);

    /// <summary>Downloads (after a confirmation, when needed) and listens with <paramref name="model"/>.</summary>
    void UseParakeet(ParakeetModel model);

    /// <summary>Sets up Whisper on this PC's graphics card or processor, after a confirmation.</summary>
    void UseListeningHere(bool gpu);

    /// <summary>Checks the paired host <paramref name="hostId"/>.</summary>
    void CheckHost(string hostId);

    /// <summary>The option picker (<c>Picker-&lt;id&gt;</c>) without its card.</summary>
    StackPanel OptionPickerBody(string id, IReadOnlyList<PickerOption> options);
}
