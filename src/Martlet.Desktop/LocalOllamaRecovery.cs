using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using Martlet.Core.Installation;

namespace Martlet.Desktop;

/// <summary>Why Ollama on this PC doesn't answer, shared by Home, Companion › Thinking, the run windows and the talk window,
/// and Martlet's repair of the one known cause (see <see cref="OllamaRecovery"/>): when Ollama keeps stopping because its
/// models folder is a link it refuses, Martlet points it at the real folder by itself, once for each cause in a run, and
/// records each change in the local log. Everything here reads or changes only this PC.</summary>
internal static class LocalOllamaRecovery
{
    private static readonly object gate = new();
    private static readonly HashSet<string> triedAutomatically = new(StringComparer.OrdinalIgnoreCase);
    private static IOllamaControl? production;
    private static Task<OllamaRepairResult>? repairing;
    private static string? repairingKey;
    private static Task? noticing;

    /// <summary>Ollama on this PC, or the FIXTURE stand-in while <see cref="SimulatedOllamaCrashLoop"/> is active.</summary>
    internal static IOllamaControl Control => SimulatedOllamaCrashLoop.Active ? SimulatedOllamaCrashLoop.Control
        : production ??= new LocalOllamaControl();

    /// <summary>The last diagnosis while Ollama didn't answer; null once it answers.</summary>
    internal static OllamaDiagnosis? Last { get; private set; }

    /// <summary>The last repair Martlet made or tried in this run.</summary>
    internal static OllamaRepairResult? LastRepair { get; private set; }

    internal static bool Repairing => repairing is { IsCompleted: false };

    /// <summary>Raised (on any thread) when <see cref="Last"/>, <see cref="LastRepair"/> or <see cref="Repairing"/> changed.</summary>
    internal static event Action? Changed;

    /// <summary>Reads why Ollama doesn't answer (its logs, processes, app settings and models folder) off the UI thread and keeps
    /// it as <see cref="Last"/>.</summary>
    internal static async Task<OllamaDiagnosis> DiagnoseAsync(CancellationToken token)
    {
        var diagnosis = await Task.Run(() => OllamaRecovery.DiagnoseAsync(Control, DateTimeOffset.Now, token), token).ConfigureAwait(false);
        var known = diagnosis.Kind == OllamaTroubleKind.Answering ? null : diagnosis;
        // The same problem again (its count of failed starts aside) is no news.
        var changed = Key(known) != Key(Last);
        Last = known;
        if (changed)
        {
            if (known is { Stops: true })
                Martlet.Logging.ErrorLog.Warn($"Ollama on this PC doesn't answer. {known.Message}" + (known.CanRepair ? "" : $" {known.Guidance}"));
            Changed?.Invoke();
        }
        return diagnosis;
    }

    private static string? Key(OllamaDiagnosis? diagnosis) =>
        diagnosis is null ? null : $"{diagnosis.Kind}|{diagnosis.OllamaError}|{diagnosis.CanRepair}|{diagnosis.RepairKey}";

    /// <summary>Ollama answered: forgets the last problem.</summary>
    internal static void Answered()
    {
        if (Last is null) return;
        Last = null;
        Changed?.Invoke();
    }

    /// <summary>Whether Martlet repairs <paramref name="diagnosis"/> by itself now: the known cause, which it can repair, while
    /// Ollama stops, and not tried by itself yet in this run (or being repaired now, which the caller then joins).</summary>
    internal static bool RepairsAutomatically(OllamaDiagnosis? diagnosis)
    {
        if (diagnosis is not { Stops: true, CanRepair: true }) return false;
        lock (gate)
            return repairing is { IsCompleted: false } && repairingKey == diagnosis.RepairKey ||
                !triedAutomatically.Contains(diagnosis.RepairKey);
    }

    /// <summary>Waits for a repair that is running now (it stops and starts Ollama itself), so nothing starts Ollama meanwhile.</summary>
    internal static async Task WaitForRepairAsync(CancellationToken token)
    {
        Task<OllamaRepairResult>? running;
        lock (gate) running = repairing is { IsCompleted: false } busy ? busy : null;
        if (running is not null) await running.WaitAsync(token).ConfigureAwait(false);
    }

    /// <summary>Repairs <paramref name="diagnosis"/>, or joins a repair already running. <paramref name="automatic"/>: Martlet
    /// found it by itself, so it repairs only once for each cause in a run (<see cref="RepairsAutomatically"/>) and returns null
    /// when it already tried. The repair itself runs to its end (it never leaves Ollama stopped); <paramref name="token"/>
    /// only stops this caller's wait. Each step goes to <paramref name="output"/> and the local log.</summary>
    internal static async Task<OllamaRepairResult?> RepairAsync(OllamaDiagnosis diagnosis, bool automatic, CancellationToken token,
        IProgress<string>? output = null)
    {
        Task<OllamaRepairResult> run;
        lock (gate)
        {
            if (repairing is { IsCompleted: false } running) run = running;
            else
            {
                if (automatic && !triedAutomatically.Add(diagnosis.RepairKey)) return null;
                triedAutomatically.Add(diagnosis.RepairKey);
                repairingKey = diagnosis.RepairKey;
                run = repairing = Task.Run(() => RunRepairAsync(diagnosis, automatic, output));
                // Once it has finished (Repairing is false), even when every caller stopped waiting.
                _ = run.ContinueWith(_ => Changed?.Invoke(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        Changed?.Invoke();
        return await run.WaitAsync(token).ConfigureAwait(false);
    }

    private static async Task<OllamaRepairResult> RunRepairAsync(OllamaDiagnosis diagnosis, bool automatic, IProgress<string>? output)
    {
        var log = Martlet.Logging.ErrorLog.Info;
        log($"{(automatic ? "Martlet found by itself" : "You asked Martlet")} to fix Ollama on this PC: {diagnosis.Message}");
        var steps = new Progress<string>(line =>
        {
            log("Ollama repair: " + line);
            output?.Report(line);
        });
        OllamaRepairResult result;
        // No caller's token: once Ollama is stopped, the repair must finish and start it again (its own steps have time limits).
        try { result = await OllamaRecovery.RepairAsync(diagnosis, Control, steps, () => DateTimeOffset.Now, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or HttpRequestException
                                          or Win32Exception)
        {
            result = new(false, $"Martlet couldn't fix Ollama: {error.Message.TrimEnd('.')}. {diagnosis.Guidance}", [], null, null, diagnosis);
        }
        foreach (var change in result.Changes) log("Ollama repair changed " + change);
        if (result.Backup is { } backup) log($"Ollama repair: Ollama's app database as it was is in {backup}.");
        if (result.Repaired)
        {
            log("Ollama repair: " + result.Summary);
            Last = null;
        }
        else
        {
            Martlet.Logging.ErrorLog.Warn("Ollama repair didn't work: " + result.Summary);
            if (result.After is { Kind: not OllamaTroubleKind.Answering } after) Last = after;
        }
        LastRepair = result;
        return result;
    }

    /// <summary>Something found Ollama not answering (the talk window's warm-up): reads why in the background and repairs the
    /// known cause by itself, once. Nothing waits for it.</summary>
    internal static void NoticeNotAnswering()
    {
        lock (gate)
        {
            if (noticing is { IsCompleted: false }) return;
            noticing = Task.Run(async () =>
            {
                try
                {
                    var diagnosis = await DiagnoseAsync(CancellationToken.None).ConfigureAwait(false);
                    if (RepairsAutomatically(diagnosis)) await RepairAsync(diagnosis, automatic: true, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    Martlet.Logging.ErrorLog.Warn("Martlet couldn't check why Ollama on this PC doesn't answer.", error);
                }
            });
        }
    }

    /// <summary>One line for a status: Ollama's own words when it stops, what Martlet does about it, or null when nothing is known.</summary>
    internal static string? Short()
    {
        if (Repairing) return "Ollama keeps stopping because of its linked models folder; Martlet is fixing it…";
        return Last is { Stops: true, OllamaError: { } said } ? $"Ollama keeps stopping: \"{said.TrimEnd('.')}\". Home shows what to do." : null;
    }

    /// <summary>Opens the folder with Ollama's own logs (server.log, app.log). False when it can't be opened.</summary>
    internal static bool OpenLogs()
    {
        var folder = Control.Places.DataDirectory;
        if (!Directory.Exists(folder)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException) { return false; }
    }
}

/// <summary>Ollama for Windows on this PC: its tray app and servers (only those from its install folder), its loopback API and
/// the current user's OLLAMA_MODELS.</summary>
internal sealed class LocalOllamaControl : IOllamaControl
{
    private const string Variable = "OLLAMA_MODELS";
    private static readonly string[] Names = ["ollama app", "ollama"];

    public OllamaPlaces Places => OllamaPlaces.ThisPc();
    public bool Installed => Places.Server is { } server && File.Exists(server);
    public bool AppInstalled => Places.App is { } app && File.Exists(app);

    public (bool App, bool Server) Running() => (Any(app: true, Places.InstallDirectory), Any(app: false, Places.InstallDirectory));

    public async Task<bool> AnswersAsync(CancellationToken token)
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await client.GetAsync(LocalOllama.OriginUri + "api/version", limit.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
        catch (HttpRequestException) { return false; }
    }

    public Task<IReadOnlyList<string>?> ModelsAsync(CancellationToken token) => LocalOllama.ServedModelsAsync(TimeSpan.FromSeconds(5), token);

    public async Task StopAsync(CancellationToken token)
    {
        var install = Places.InstallDirectory;
        // The tray app first: it starts the server again a second after it stops. Then the servers it or Martlet started, with
        // the model runners they started; never `ollama run`, `ollama launch` or other command-line sessions.
        foreach (var app in new[] { true, false })
            foreach (var process in Find(app, install))
                using (process)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException) { }
                }
        for (var waited = 0; waited < 40; waited++)
        {
            if (!Any(app: true, install) && !Any(app: false, install)) return;
            await Task.Delay(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false);
        }
        throw new InvalidOperationException("Ollama's processes are still running");
    }
    public void Start(string? models)
    {
        var places = Places;
        var start = places.App is { } app && File.Exists(app)
            ? new ProcessStartInfo(app) { UseShellExecute = false, WorkingDirectory = places.InstallDirectory! }
            : places.Server is { } server && File.Exists(server)
                ? new ProcessStartInfo(server, "serve") { UseShellExecute = false, CreateNoWindow = true }
                : throw new InvalidOperationException("Ollama isn't installed on this PC");
        if (models is not null) start.Environment[Variable] = models;
        try { Process.Start(start)?.Dispose(); }
        catch (Win32Exception error) { throw new InvalidOperationException(error.Message); }
    }

    public string? UserModelsVariable => Environment.GetEnvironmentVariable(Variable, EnvironmentVariableTarget.User);

    public void SetUserModelsVariable(string value)
    {
        Environment.SetEnvironmentVariable(Variable, value, EnvironmentVariableTarget.User);
        // Ollama started from Martlet later gets the same value.
        Environment.SetEnvironmentVariable(Variable, value);
    }

    private static bool Any(bool app, string? install)
    {
        var found = Find(app, install);
        foreach (var process in found) process.Dispose();
        return found.Count > 0;
    }

    private static readonly int Session = CurrentSession();

    private static int CurrentSession()
    {
        using var me = Process.GetCurrentProcess();
        return me.SessionId;
    }

    /// <summary>Ollama's tray app (<paramref name="app"/>), or its servers (<c>ollama.exe serve</c>, as
    /// <see cref="OllamaRecovery.RunsServer"/> tells them from command-line sessions), running in this Windows session from its
    /// install folder. A server whose command line can't be read isn't included. The caller disposes them.</summary>
    private static List<Process> Find(bool app, string? install)
    {
        var found = new List<Process>();
        foreach (var process in Process.GetProcessesByName(app ? Names[0] : Names[1]))
        {
            bool ours;
            try { ours = process.SessionId == Session && FromInstall(process, install) && (app || OllamaRecovery.RunsServer(CommandLine(process.Id))); }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException) { ours = false; }
            if (ours) found.Add(process);
            else process.Dispose();
        }
        return found;
    }

    /// <summary>Whether <paramref name="process"/> runs from Ollama's install folder (a process whose path can't be read counts).</summary>
    private static bool FromInstall(Process process, string? install)
    {
        if (install is null) return true;
        try
        {
            return process.MainModule?.FileName is not { } path ||
                path.StartsWith(Path.TrimEndingDirectorySeparator(install) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException) { return true; }
    }

    /// <summary>The command line of process <paramref name="id"/> (NtQueryInformationProcess, ProcessCommandLineInformation),
    /// or null when it can't be read.</summary>
    private static string? CommandLine(int id)
    {
        using var handle = OpenProcess(QueryLimitedInformation, false, id);
        if (handle.IsInvalid) return null;
        NtQueryInformationProcess(handle, CommandLineInformation, IntPtr.Zero, 0, out var length);
        if (length <= 0 || length > 1 << 20) return null;
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (NtQueryInformationProcess(handle, CommandLineInformation, buffer, length, out _) != 0) return null;
            // A UNICODE_STRING: its length in bytes (ushort), then (after padding) a pointer to the text.
            var bytes = (ushort)Marshal.ReadInt16(buffer);
            var text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
            return text == IntPtr.Zero ? null : Marshal.PtrToStringUni(text, bytes / 2);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private const uint QueryLimitedInformation = 0x1000;
    private const int CommandLineInformation = 60;

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern Microsoft.Win32.SafeHandles.SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);

    [DllImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NtQueryInformationProcess(Microsoft.Win32.SafeHandles.SafeProcessHandle process, int informationClass,
        IntPtr information, int length, out int returnLength);
}
/// <summary>FIXTURE for checking through MCP what Martlet shows and does when Ollama on this PC keeps stopping, without touching
/// the real Ollama: with <see cref="Variable"/> set before Martlet starts to <c>linked</c> (the known cause: a models folder
/// that is a junction), <c>unknown-settings</c> (the same, with an Ollama app database Martlet doesn't know) or
/// <c>other-error</c> (its port is taken), Martlet uses an <see cref="OllamaRecoveryFixture"/> in its data folder's
/// <c>ollama-fixture</c> for Ollama: its logs, app database, models folder, processes and OLLAMA_MODELS. Martlet's checks,
/// diagnosis and repair run unchanged on it; Test, Load and Download stop with a FIXTURE message instead of asking the real
/// Ollama, and the talk window's warm-up asks the fixture.</summary>
internal static class SimulatedOllamaCrashLoop
{
    internal const string Variable = "MARTLET_SIMULATE_OLLAMA_CRASH_LOOP";

    private static readonly string? value = Environment.GetEnvironmentVariable(Variable)?.Trim().ToLowerInvariant();
    private static readonly OllamaFixtureScenario? scenario = value switch
    {
        "linked" => OllamaFixtureScenario.Linked,
        "unknown-settings" => OllamaFixtureScenario.UnknownSettings,
        "other-error" => OllamaFixtureScenario.OtherError,
        _ => null
    };
    private static readonly Lazy<IOllamaControl> control = new(Create);

    internal static bool Active => scenario is not null;

    /// <summary>Martlet's data folder; the fixture goes in its <c>ollama-fixture</c> (else in the temporary folder).</summary>
    internal static string? DataDirectory { get; set; }

    internal static IOllamaControl Control => control.Value;

    /// <summary>Stops a request that would go to the real Ollama.</summary>
    internal static void StopRequest()
    {
        if (Active)
            throw new InvalidOperationException($"FIXTURE ({Variable}): Ollama on this PC is simulated, so Martlet sends no request to it.");
    }

    private static IOllamaControl Create()
    {
        var folder = Path.Combine(DataDirectory ?? Path.Combine(Path.GetTempPath(), $"martlet-{Environment.ProcessId}"), "ollama-fixture");
        try
        {
            var fixture = OllamaRecoveryFixture.Create(folder, scenario!.Value, DateTimeOffset.Now);
            Martlet.Logging.ErrorLog.Info($"FIXTURE ({Variable}={value}): Ollama on this PC is simulated in {folder}. The real Ollama, its " +
                "models folder, its settings and your OLLAMA_MODELS aren't touched.");
            return fixture.Control;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException)
        {
            Martlet.Logging.ErrorLog.Warn($"FIXTURE ({Variable}): the simulated Ollama couldn't be made in {folder}.", error);
            return new Absent(new(folder, Path.Combine(folder, "models"), null));
        }
    }

    /// <summary>Stands in when the fixture couldn't be made: Ollama reads as not installed, and nothing real is touched.</summary>
    private sealed class Absent(OllamaPlaces places) : IOllamaControl
    {
        public OllamaPlaces Places { get; } = places;
        public bool Installed => false;
        public bool AppInstalled => false;
        public (bool App, bool Server) Running() => (false, false);
        public Task<bool> AnswersAsync(CancellationToken token) => Task.FromResult(false);
        public Task<IReadOnlyList<string>?> ModelsAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<string>?>(null);
        public Task StopAsync(CancellationToken token) => Task.CompletedTask;
        public void Start(string? models) => throw new InvalidOperationException($"FIXTURE ({Variable}): the simulated Ollama isn't there");
        public string? UserModelsVariable => null;
        public void SetUserModelsVariable(string value) { }
    }
}
