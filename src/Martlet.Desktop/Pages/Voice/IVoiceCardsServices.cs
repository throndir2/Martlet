using System.Windows.Controls;
using Martlet.Core.Installation;
using Martlet.Core.Settings;
using Martlet.Core.Voices;
using Martlet.F5;

namespace Martlet.Desktop;

/// <summary>Everything the cards of Companion › Voice (<see cref="VoiceCards"/>: Voice engine, Voices and Chatterbox Original
/// style) read and start in the app. MainWindow gives it to the cards when it makes them (MainWindow.VoiceCards.cs). MainWindow
/// keeps the work the cards start (rule 4 of Pages\README.md): setting an engine up, switching and stopping engines
/// (MainWindow.VoiceEngines.cs), and adding, recording, playing, removing and choosing voices (MainWindow.Voices.cs).</summary>
internal interface IVoiceCardsServices
{
    // ---------- what the cards read ----------

    /// <summary>The saved settings, or null before they are loaded.</summary>
    AppSettings? Settings { get; }

    /// <summary>What Martlet read of this PC's hardware (<see cref="MachineInfo.Unknown"/> before it is read).</summary>
    MachineInfo Machine { get; }

    /// <summary>Your paired computers, this PC's own host service too.</summary>
    IReadOnlyList<PairedHost> Hosts();

    /// <summary>The last check of the paired host <paramref name="hostId"/>, or null when it isn't checked yet.</summary>
    HostCheck? HostCheck(string hostId);

    /// <summary>The saved hardware of the paired host <paramref name="hostId"/>, or null.</summary>
    HostHardware? Hardware(string hostId);

    /// <summary>The host Speaking is being set up on now, or null.</summary>
    string? PendingSpeakingHost { get; }

    /// <summary>Why this PC can't run <paramref name="engine"/>, or null.</summary>
    string? ThisPcCannot(SpeechEngine engine);

    /// <summary>Why <paramref name="hostId"/> can't run the role <paramref name="roleKind"/> for <paramref name="job"/>, or null.</summary>
    string? CannotHand(string hostId, string roleKind, string job);

    /// <summary>Where this PC keeps its voices when no computer speaks.</summary>
    string F5Destination { get; }

    /// <summary>Your voice list, or null before Martlet is set up. Throws <see cref="F5Exception"/>,
    /// <see cref="System.IO.IOException"/> or <see cref="UnauthorizedAccessException"/> when it can't be read.</summary>
    SpeakingVoiceLibrary? VoiceLibrary();

    /// <summary>This PC's recordings for <paramref name="destination"/> and the voice applied here (only after Martlet is set up).
    /// Throws as <see cref="VoiceLibrary"/> does.</summary>
    (IReadOnlyDictionary<string, F5ReferenceSnapshot> Local, F5ReferenceSnapshot? Applied) LocalVoices(string destination);

    /// <summary>What the last share of your voices with your paired computers did.</summary>
    string SharedVoicesStatus { get; }

    /// <summary>The saved Chatterbox Original style (<see cref="ChatterboxStyle.Default"/> before Martlet is set up).</summary>
    ChatterboxStyle LoadChatterboxStyle();

    /// <summary>Saves <paramref name="style"/>; false (nothing saved) before Martlet is set up. Throws when the file can't be
    /// written.</summary>
    bool SaveChatterboxStyle(ChatterboxStyle style);

    // ---------- builders ----------

    /// <summary>The option picker without its card (MainWindow.OptionPicker.cs).</summary>
    StackPanel OptionPickerBody(string id, IReadOnlyList<PickerOption> options);

    // ---------- what the cards start ----------

    /// <summary>Runs a Devices action (Add a computer, Check hosts), on <paramref name="argument"/> when it names a host.</summary>
    void RunNodeAction(NodeAction action, string? argument = null);

    /// <summary>Opens Devices.</summary>
    void OpenDevices();

    /// <summary>Draws the open Companion page again.</summary>
    void Redraw();

    /// <summary>Sets <paramref name="engine"/> up on <paramref name="host"/> (null: this PC) when needed and speaks with it.</summary>
    void UseVoiceEngine(SpeechEngine engine, PairedHost? host);

    /// <summary>Stops the voice engines <paramref name="hostId"/> runs besides <paramref name="speaking"/>, after a confirmation.</summary>
    void StopIdleVoiceEngines(string hostId, SpeechEngine speaking);

    /// <summary>Plays a voice's recording.</summary>
    Task PlayVoice(VoiceItem item);

    /// <summary>Plays the voice applied here that is no longer in the list.</summary>
    Task PlayApplied(F5ReferenceSnapshot? applied, string name);

    /// <summary>Makes a voice the one Martlet speaks with on all your computers.</summary>
    Task UseVoice(string voiceId, string destination);

    /// <summary>Removes a voice from all your computers, after a confirmation.</summary>
    Task RemoveVoice(VoiceItem item, string destination);

    /// <summary>Add a voice: adds one from a recording, then uses it.</summary>
    void AddVoice(string destination);

    /// <summary>Record your own voice: records the owner, adds that voice, then uses it.</summary>
    void RecordVoice(string destination);
}
