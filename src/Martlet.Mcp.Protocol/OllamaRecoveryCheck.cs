using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Martlet.Core.Installation;

namespace Martlet.Mcp;

/// <summary>ollama_recovery: why Ollama on this PC doesn't answer, as the desktop finds it (the production
/// <see cref="OllamaRecovery"/> diagnosis of its logs, processes, app database and models folder), read-only; and with
/// fixture=true the production diagnosis and repair rehearsed on <see cref="OllamaRecoveryFixture"/> folders (a junction made
/// with mklink /J, a db.sqlite made with Windows' SQLite, Ollama's log lines and a stand-in for its processes). It never stops,
/// starts or changes the real Ollama, its models folder, its settings or OLLAMA_MODELS. Paths under the user's folders read as
/// %LOCALAPPDATA% and %USERPROFILE%.</summary>
internal static class OllamaRecoveryCheck
{
    internal static async Task<object> RunAsync(bool fixture, CancellationToken cancellation)
    {
        var diagnosis = await OllamaRecovery.DiagnoseAsync(new ReadOnlyControl(), DateTimeOffset.Now, cancellation);
        return new
        {
            thisPc = Describe(diagnosis, Hide),
            fixture = fixture ? await FixtureAsync(cancellation) : null
        };
    }

    private static object Describe(OllamaDiagnosis diagnosis, Func<string?, string?> path)
    {
        var facts = diagnosis.Facts;
        return new
        {
            kind = diagnosis.Kind.ToString(),
            ollamaError = path(diagnosis.OllamaError),
            knownCause = diagnosis.KnownCause,
            canRepair = diagnosis.CanRepair,
            message = path(diagnosis.Message),
            guidance = path(diagnosis.Guidance),
            modelsPath = path(diagnosis.ModelsPath),
            linkPath = path(diagnosis.LinkPath),
            realPath = path(diagnosis.RealPath),
            installed = facts?.Installed,
            appInstalled = facts?.AppInstalled,
            appRunning = facts?.AppRunning,
            serverRunning = facts?.ServerRunning,
            answers = facts?.Answers,
            log = facts is null ? null : new
            {
                facts.Log.Starts, facts.Log.FailedStarts, facts.Log.LastStartFailed, facts.Log.ListeningVersion,
                modelsPath = path(facts.Log.ModelsPath), lastError = path(facts.Log.LastError), appReason = path(facts.Log.AppReason),
                writtenSecondsAgo = facts.Log.Written is { } written ? (int?)(facts.Now - written).TotalSeconds : null
            },
            app = facts?.App is { } app ? new { app.Found, app.KnownShape, models = path(app.Models), problem = path(app.Problem) } : null,
            userModelsVariable = path(facts?.UserModelsVariable),
            modelsFolders = facts?.ModelsFolders.Select(f => new
            {
                path = path(f.Path), f.Linked, linkPath = path(f.LinkPath), linkTarget = path(f.LinkTarget), realPath = path(f.RealPath),
                f.RealPathUsable
            }).ToArray()
        };
    }

    private static string? Hide(string? text)
    {
        if (text is null) return null;
        foreach (var (folder, name) in new[]
                 {
                     (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%"),
                     (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%")
                 })
            if (folder.Length > 0) text = text.Replace(folder, name, StringComparison.OrdinalIgnoreCase);
        return text;
    }

    private static async Task<object> FixtureAsync(CancellationToken cancellation)
    {
        var root = Path.Combine(Path.GetTempPath(), "martlet-mcp-ollama-" + Guid.NewGuid().ToString("N")[..8]);
        var results = new List<object>();
        var passed = true;
        try
        {
            OllamaDiagnosis? linkedDiagnosis = null;
            foreach (var scenario in new[] { OllamaFixtureScenario.Linked, OllamaFixtureScenario.UnknownSettings, OllamaFixtureScenario.OtherError,
                         OllamaFixtureScenario.Answering })
            {
                var fixture = OllamaRecoveryFixture.Create(Path.Combine(root, scenario.ToString()), scenario, DateTimeOffset.Now);
                string? Show(string? text) => text?.Replace(fixture.Folder, "<fixture>", StringComparison.OrdinalIgnoreCase);
                var before = File.ReadAllBytes(fixture.Places.Database);
                var diagnosis = await OllamaRecovery.DiagnoseAsync(fixture.Control, DateTimeOffset.Now, cancellation);
                if (scenario == OllamaFixtureScenario.Linked) linkedDiagnosis = diagnosis;
                var lines = new List<string>();
                // The answering Ollama gets the linked scenario's repair, which must change nothing once Ollama answers.
                var repair = await OllamaRecovery.RepairAsync(scenario == OllamaFixtureScenario.Answering ? linkedDiagnosis! : diagnosis,
                    fixture.Control, new Collect(lines), () => DateTimeOffset.Now, cancellation, TimeSpan.FromSeconds(5));
                var events = fixture.Control.Events.ToArray();
                var saved = fixture.SavedModels;
                var backupModels = repair.Backup is { } backup ? OllamaAppDatabase.Read(Path.Combine(backup, "db.sqlite")).Models : null;
                var unchanged = before.AsSpan().SequenceEqual(File.ReadAllBytes(fixture.Places.Database));
                var real = diagnosis.RealPath;
                var ok = scenario switch
                {
                    OllamaFixtureScenario.Linked => diagnosis is { Kind: OllamaTroubleKind.CrashLoop, KnownCause: true, CanRepair: true } &&
                        OllamaModelsFolder.SamePath(real, fixture.RealPath) && repair.Repaired && repair.Models == 1 &&
                        events.SequenceEqual(["stop", $"set OLLAMA_MODELS={real}", $"start OLLAMA_MODELS={real}"]) &&
                        fixture.Control.ModelsWhenStopped == fixture.ModelsPath && saved == real && backupModels == fixture.ModelsPath &&
                        fixture.Control.UserModelsVariable == real,
                    OllamaFixtureScenario.UnknownSettings => diagnosis is { KnownCause: true, CanRepair: false } && !repair.Repaired &&
                        events.Length == 0 && unchanged && real is not null && diagnosis.Guidance?.Contains(real, StringComparison.Ordinal) == true,
                    OllamaFixtureScenario.OtherError => diagnosis is { Kind: OllamaTroubleKind.CrashLoop, KnownCause: false, CanRepair: false } &&
                        diagnosis.Message.Contains("Only one usage of each socket address", StringComparison.Ordinal) && events.Length == 0 && unchanged,
                    _ => diagnosis.Kind == OllamaTroubleKind.Answering && repair.Repaired && events.Length == 0 && unchanged
                };
                passed &= ok;
                results.Add(new
                {
                    scenario = scenario.ToString(), passed = ok, diagnosis = Describe(diagnosis, Show),
                    repair = new
                    {
                        repair.Repaired, summary = Show(repair.Summary), changes = repair.Changes.Select(Show).ToArray(),
                        backup = Show(repair.Backup), backupModels = Show(backupModels), repair.Models, steps = lines.Select(Show).ToArray()
                    },
                    events = events.Select(Show).ToArray(),
                    modelsWhenStopped = Show(fixture.Control.ModelsWhenStopped),
                    savedModels = Show(saved),
                    userModelsVariable = Show(fixture.Control.UserModelsVariable),
                    databaseUnchanged = unchanged
                });
                fixture.Delete();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException)
        {
            passed = false;
            results.Add(new { error = error.Message });
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return new { passed, note = "FIXTURE: fixture folders, a fixture junction and a stand-in for Ollama's processes; the real Ollama isn't touched.", scenarios = results };
    }

    private sealed class Collect(List<string> lines) : IProgress<string>
    {
        public void Report(string value) { lock (lines) lines.Add(value); }
    }

    /// <summary>Reads Ollama on this PC and never changes it: stopping, starting and saving OLLAMA_MODELS refuse.</summary>
    private sealed class ReadOnlyControl : IOllamaControl
    {
        public OllamaPlaces Places { get; } = OllamaPlaces.ThisPc();
        public bool Installed => Places.Server is { } server && File.Exists(server);
        public bool AppInstalled => Places.App is { } app && File.Exists(app);
        public (bool App, bool Server) Running() => (Any("ollama app"), Any("ollama"));
        public string? UserModelsVariable => Environment.GetEnvironmentVariable("OLLAMA_MODELS", EnvironmentVariableTarget.User);

        public async Task<bool> AnswersAsync(CancellationToken token)
        {
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
            try
            {
                using var response = await client.GetAsync("http://127.0.0.1:11434/api/version", token);
                return response.IsSuccessStatusCode;
            }
            catch (TaskCanceledException) when (!token.IsCancellationRequested) { return false; }
            catch (HttpRequestException) { return false; }
        }

        public Task<IReadOnlyList<string>?> ModelsAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<string>?>(null);
        public Task StopAsync(CancellationToken token) => throw new InvalidOperationException("ollama_recovery never stops Ollama.");
        public void Start(string? models) => throw new InvalidOperationException("ollama_recovery never starts Ollama.");
        public void SetUserModelsVariable(string value) => throw new InvalidOperationException("ollama_recovery never changes OLLAMA_MODELS.");

        private static bool Any(string name)
        {
            var processes = Process.GetProcessesByName(name);
            try { return processes.Length > 0; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
    }
}
