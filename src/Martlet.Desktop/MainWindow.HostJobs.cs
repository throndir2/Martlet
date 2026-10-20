using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.HostClient;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.F5;

namespace Martlet.Desktop;

/// <summary>A conversation job (a Setup role) that the Devices page can hand to any paired host running
/// <paramref name="HostRoleKind"/>, or back to the route it used before (kept in <paramref name="SavedFile"/>).</summary>
internal sealed record HostJob(SetupRole Role, SetupRouteType RouteType, string HostRoleKind, string RouteId, string Job,
    string Engine, string Use, string SavedFile, string Sent, string Disclosure)
{
    internal static readonly HostJob Thinking = new(SetupRole.Llm, SetupRouteType.GatewayOllama, HostRoles.Ollama,
        HostRoute.OllamaChatRouteId, "thinking", "conversation model", "answer your conversations", "thinking-cloud.json",
        "Your messages and recent conversation",
        "Your messages and recent conversation go to that computer instead of a cloud provider, with no per-request charge.");

    internal static readonly HostJob Listening = new(SetupRole.Stt, SetupRouteType.GatewayStt, HostRoles.Stt,
        HostConnection.TranscriptionRouteId, "listening", "speech recognition", "turn what you say into text", "listening-previous.json",
        "Your recorded speech",
        "Your speech goes to that computer for transcription instead of a cloud provider; it isn't stored, and there is no per-request charge.");

    internal static HostJob Speaking => SpeakingFor(SpeakingEngineChoice.Current);

    private static readonly HostJob F5Speaking = new(SetupRole.Tts, SetupRouteType.GatewayF5, HostRoles.F5,
        HostRoute.F5RouteId, "speaking", "voice service", "speak your replies", "speaking-previous.json",
        "Reply text",
        "Reply text and the selected voice sample go to that computer instead of a cloud voice, with no per-request charge.");

    /// <summary>The Speaking job done by <paramref name="engine"/>'s host role, with its model licence in the disclosure.</summary>
    internal static HostJob SpeakingFor(SpeechEngine engine) => engine.Key == SpeechEngines.F5.Key ? F5Speaking : F5Speaking with
    {
        HostRoleKind = engine.HostRoleKind, RouteId = engine.RouteId,
        Disclosure = F5Speaking.Disclosure + (engine.Key == SpeechEngines.Xtts.Key
            ? " XTTS-v2's model (Coqui Public Model License) allows noncommercial use only."
            : $" {engine.Name}'s model licence: {engine.WeightsLicense}." +
              (SpeechEngines.IsChatterbox(engine) ? " Every reply carries Resemble AI's inaudible Perth watermark." : ""))
    };

    internal static IReadOnlyList<HostJob> All => [Thinking, Listening, Speaking];

    internal static HostJob? For(SetupRole role) => All.FirstOrDefault(job => job.Role == role);

    internal string Title => char.ToUpperInvariant(Job[0]) + Job[1..];
}

/// <summary>Which self-hosted voice engine (<see cref="SpeechEngines"/>) does the Speaking job, kept in speaking-engine.txt
/// (<see cref="SpeakingEngineFile"/>). A speaking route already saved on a host's engine wins over the file, so the choice
/// always matches what speaks.</summary>
internal static class SpeakingEngineChoice
{
    internal const string FileName = SpeakingEngineFile.FileName;

    internal static SpeechEngine Current { get; private set; } = SpeechEngines.Default;

    /// <summary>Reads the saved choice, then follows the engine the TTS route uses when Speaking is on a host.</summary>
    internal static void Sync(string directory, AppSettings? settings)
    {
        Current = SpeakingEngineFile.Read(directory) ?? SpeechEngines.Default;
        var route = settings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Tts);
        if (route is { RouteType: SetupRouteType.GatewayF5, GatewaySnapshot: { } snapshot } &&
            SpeechEngines.ForRoute(snapshot.RouteId) is { } used && used != Current)
            Save(directory, used);
    }

    internal static void Save(string directory, SpeechEngine engine)
    {
        SpeakingEngineFile.Save(directory, engine);
        Current = engine;
    }
}

/// <summary>Who does each job: hands a role's route to a paired host's gateway route, or back to the route chosen in Setup.
/// The host route is saved with the existing gateway settings model, this PC's pairing and an explicit selection consent.</summary>
public partial class MainWindow
{
    /// <summary>The processing destination the f5 host role advertises (Martlet.Gateway.F5.F5RelayWorker); voice-rights
    /// confirmations name it, so a voice chosen before the role is installed stays valid once it runs.</summary>
    private const string F5Destination = "f5-host";
    private readonly Dictionary<SetupRole, string> pendingJobHosts = [];
    private readonly Dictionary<SetupRole, F5ReferenceSnapshot> pendingJobVoices = [];
    /// <summary>Voice engines being stopped on a host right now (Speaking left them), by host and role kind. Speaking can't
    /// move to one until its stop finished: it then comes back through an install.</summary>
    private readonly Dictionary<(string HostId, string Kind), Task> releasingEngines = [];
}

/// <summary>The route a job used before it went to a host, kept verbatim (including its recorded selection) next to the
/// other local preferences (for example thinking-cloud.json), so the job can return to it without re-entering a key.</summary>
internal static class JobSavedRoute
{
    internal static void Save(string directory, string fileName, SetupRoute route)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, ContractJson.Write(route, 65_536));
        Martlet.Core.Settings.SettingsFiles.NotifySaved(path);
    }

    internal static SetupRoute? Load(string directory, string fileName)
    {
        try { return ContractJson.Read<SetupRoute>(File.ReadAllBytes(Path.Combine(directory, fileName))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ContractException) { return null; }
    }
}
