using System.Diagnostics;
using System.Globalization;

namespace Martlet.Core.Installation;

/// <summary>Which state <see cref="OllamaRecoveryFixture"/> sets up.</summary>
public enum OllamaFixtureScenario
{
    /// <summary>The known cause: the models folder is a junction to the real folder, Ollama's app database points at the
    /// junction, and the server stops about once a second with Ollama 0.40.1's error.</summary>
    Linked,
    /// <summary>As <see cref="Linked"/>, but the app database has no <c>models</c> column (an Ollama Martlet doesn't know).</summary>
    UnknownSettings,
    /// <summary>The server stops with another error (its port is taken); the models folder isn't a link.</summary>
    OtherError,
    /// <summary>Ollama answers.</summary>
    Answering
}

/// <summary>FIXTURE, never the real Ollama: a folder laid out like Ollama for Windows' data (server.log, app.log and a
/// db.sqlite made with Windows' SQLite), a models folder that is a junction (made with <c>mklink /J</c>) to a real folder with
/// <c>blobs</c> and <c>manifests</c>, and a stand-in for Ollama's processes (<see cref="Control"/>). Tests, the
/// <c>ollama_recovery</c> MCP check and the desktop's MARTLET_SIMULATE_OLLAMA_CRASH_LOOP use it to run the production
/// diagnosis and repair without touching the real Ollama, its junction, its database or the user's environment.</summary>
public sealed class OllamaRecoveryFixture
{
    public const string FixtureModel = "fixture-model:1b";

    private OllamaRecoveryFixture(string folder, OllamaPlaces places, string modelsPath, string realPath, FixtureOllamaControl control)
    {
        Folder = folder;
        Places = places;
        ModelsPath = modelsPath;
        RealPath = realPath;
        Control = control;
    }

    public string Folder { get; }
    public OllamaPlaces Places { get; }

    /// <summary>The models folder Ollama is told to use: a junction in every scenario but <see cref="OllamaFixtureScenario.OtherError"/>.</summary>
    public string ModelsPath { get; }

    /// <summary>The real folder that holds the fixture models.</summary>
    public string RealPath { get; }

    public FixtureOllamaControl Control { get; }

    /// <summary>The app database's Model location now (null when it can't be read).</summary>
    public string? SavedModels => OllamaAppDatabase.Read(Places.Database).Models;

    /// <summary>Makes the fixture in <paramref name="folder"/>, replacing a fixture made there before. Windows only.</summary>
    public static OllamaRecoveryFixture Create(string folder, OllamaFixtureScenario scenario, DateTimeOffset now)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The Ollama recovery fixture needs Windows.");
        folder = Path.GetFullPath(folder);
        var data = Path.Combine(folder, "LocalAppData", "Ollama");
        var install = Path.Combine(folder, "LocalAppData", "Programs", "Ollama");
        var models = Path.Combine(folder, "UserProfile", ".ollama", "models");
        var real = Path.Combine(folder, "OtherDrive", "Ollama", "models");
        Remove(folder, models);
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(Path.Combine(real, "blobs"));
        var manifest = Path.Combine(real, "manifests", "registry.ollama.ai", "library", "fixture-model");
        Directory.CreateDirectory(manifest);
        File.WriteAllText(Path.Combine(manifest, "1b"), "{\"FIXTURE\":\"not a model\"}");
        File.WriteAllText(Path.Combine(real, "blobs", "sha256-fixture"), "FIXTURE - not a model");
        Directory.CreateDirectory(Path.GetDirectoryName(models)!);
        if (scenario == OllamaFixtureScenario.OtherError) Directory.CreateDirectory(models);
        else Junction(models, real);

        var settings = scenario == OllamaFixtureScenario.UnknownSettings
            ? "CREATE TABLE settings (id INTEGER PRIMARY KEY CHECK (id = 1), device_id TEXT NOT NULL DEFAULT '', model_dir TEXT NOT NULL DEFAULT '');" +
              $"INSERT INTO settings (id, model_dir) VALUES (1, '{Sql(models)}');"
            : "CREATE TABLE settings (id INTEGER PRIMARY KEY CHECK (id = 1), device_id TEXT NOT NULL DEFAULT '', " +
              "models TEXT NOT NULL DEFAULT '', auto_update_enabled BOOLEAN NOT NULL DEFAULT 1, schema_version INTEGER NOT NULL DEFAULT 19);" +
              $"INSERT INTO settings (id, models) VALUES (1, '{(scenario == OllamaFixtureScenario.OtherError ? "" : Sql(models))}');";
        OllamaAppDatabase.Create(Path.Combine(data, "db.sqlite"), settings);

        var places = new OllamaPlaces(data, models, install);
        var control = new FixtureOllamaControl(places, scenario == OllamaFixtureScenario.Answering)
        {
            UserModelsVariable = scenario == OllamaFixtureScenario.Linked ? models : null
        };
        var server = new List<string>();
        var app = new List<string>();
        for (var start = 5; start >= 1; start--)
        {
            var at = now - TimeSpan.FromSeconds(start * 1.1);
            if (scenario == OllamaFixtureScenario.Answering)
            {
                if (start > 1) continue;
                server.AddRange(FixtureOllamaControl.StartLines(at, models, null));
                break;
            }
            var error = scenario == OllamaFixtureScenario.OtherError
                ? "listen tcp 127.0.0.1:11434: bind: Only one usage of each socket address (protocol/network address/port) is normally permitted."
                : FixtureOllamaControl.LinkError(models);
            server.AddRange(FixtureOllamaControl.StartLines(at, models, error));
            if (scenario != OllamaFixtureScenario.OtherError)
                app.Add($"time={Time(at)} level=WARN source=server.go:260 msg=\"models path not accessible, using default\" path={models} " +
                    $"err=\"CreateFile {Quote(models)}: The path cannot be traversed because it contains an untrusted mount point.\"");
            app.Add($"time={Time(at.AddMilliseconds(80))} level=ERROR source=server.go:224 msg=\"ollama exited\" err=\"exit status 1\"");
        }
        File.WriteAllLines(places.ServerLog, server);
        File.WriteAllLines(places.AppLog, app);
        File.SetLastWriteTimeUtc(places.ServerLog, now.UtcDateTime);
        return new(folder, places, models, real, control);
    }

    /// <summary>Deletes the fixture: its junction first (only the link, never the folder it points at), then the rest.</summary>
    public void Delete()
    {
        try { Remove(Folder, ModelsPath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Removes a fixture made in <paramref name="folder"/> before: its junction first (only the link, never the folder it
    /// points at), then the rest.</summary>
    private static void Remove(string folder, string link)
    {
        if (!Directory.Exists(folder)) return;
        if (new DirectoryInfo(link) is { Exists: true, LinkTarget: not null }) Directory.Delete(link);
        Directory.Delete(folder, recursive: true);
    }

    /// <summary>Makes <paramref name="link"/> a directory junction to <paramref name="target"/> (<c>mklink /J</c>, no
    /// administrator rights needed).</summary>
    internal static void Junction(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "/d", "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("cmd.exe didn't start.");
        var errors = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || new DirectoryInfo(link).LinkTarget is null)
            throw new InvalidOperationException($"Couldn't make the fixture junction ({process.ExitCode}): {errors.Result.Trim()}");
    }

    private static string Sql(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    internal static string Quote(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    internal static string Time(DateTimeOffset at) => at.ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
}

/// <summary>FIXTURE stand-in for Ollama's processes and loopback API (see <see cref="OllamaRecoveryFixture"/>): its tray app
/// "runs" until stopped; started again it "answers" only when the models folder it would use (the app database's Model
/// location, else OLLAMA_MODELS) isn't reached through a link, and it writes the matching lines to the fixture server.log.
/// The user's OLLAMA_MODELS is kept in memory. <see cref="Events"/> records what was done, in order.</summary>
public sealed class FixtureOllamaControl(OllamaPlaces places, bool answers) : IOllamaControl
{
    private readonly object gate = new();
    private bool answering = answers, appRunning = true;

    public OllamaPlaces Places { get; } = places;
    public bool Installed => true;
    public bool AppInstalled => true;
    public string? UserModelsVariable { get; set; }

    /// <summary>What was done, in order: <c>stop</c>, <c>set OLLAMA_MODELS=...</c>, <c>start OLLAMA_MODELS=...</c>.</summary>
    public List<string> Events { get; } = [];

    /// <summary>The app database's Model location when Ollama was stopped (to check it changed only afterwards).</summary>
    public string? ModelsWhenStopped { get; private set; }

    /// <summary>Makes <see cref="StopAsync"/> fail, as when Ollama's processes can't be ended.</summary>
    public bool RefuseStop { get; set; }

    /// <summary>Runs once Ollama's processes are "stopped", before <see cref="StopAsync"/> checks its token (a test cancels it
    /// there, as a run window's Cancel would while the real one waits for the processes to end).</summary>
    public Action? AfterStop { get; set; }

    public (bool App, bool Server) Running()
    {
        lock (gate) return (appRunning, answering);
    }

    public Task<bool> AnswersAsync(CancellationToken token)
    {
        lock (gate) return Task.FromResult(answering);
    }

    public Task<IReadOnlyList<string>?> ModelsAsync(CancellationToken token)
    {
        lock (gate) return Task.FromResult<IReadOnlyList<string>?>(answering ? [OllamaRecoveryFixture.FixtureModel] : null);
    }

    public Task StopAsync(CancellationToken token)
    {
        lock (gate)
        {
            if (RefuseStop) throw new InvalidOperationException("FIXTURE: Ollama's processes didn't end");
            Events.Add("stop");
            ModelsWhenStopped = OllamaAppDatabase.Read(Places.Database).Models;
            appRunning = answering = false;
        }
        AfterStop?.Invoke();
        token.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public void Start(string? models)
    {
        lock (gate)
        {
            Events.Add($"start OLLAMA_MODELS={models ?? "(unchanged)"}");
            appRunning = true;
            var saved = OllamaAppDatabase.Read(Places.Database).Models;
            var used = !string.IsNullOrEmpty(saved) ? saved : models ?? UserModelsVariable ?? Places.DefaultModels;
            answering = Directory.Exists(used) && !OllamaModelsFolder.Inspect(used).Linked;
            File.AppendAllLines(Places.ServerLog, StartLines(DateTimeOffset.Now, used, answering ? null : LinkError(used)));
        }
    }

    public void SetUserModelsVariable(string value)
    {
        lock (gate)
        {
            Events.Add($"set OLLAMA_MODELS={value}");
            UserModelsVariable = value;
        }
    }

    internal static string LinkError(string models) =>
        $"mkdir {models}: Cannot create a file when that file already exists.: ensure path elements are traversable";

    /// <summary>The lines one server start writes: its config, then <paramref name="error"/> (it stopped) or that it listens.</summary>
    internal static IEnumerable<string> StartLines(DateTimeOffset at, string models, string? error)
    {
        yield return $"time={OllamaRecoveryFixture.Time(at)} level=INFO source=routes.go:2373 msg=\"server config\" env=\"map[CUDA_VISIBLE_DEVICES: " +
            $"HTTPS_PROXY: OLLAMA_CONTEXT_LENGTH:8192 OLLAMA_HOST:http://127.0.0.1:11434 OLLAMA_MODELS:{OllamaRecoveryFixture.Quote(models)} " +
            "OLLAMA_NUM_PARALLEL:1 OLLAMA_REMOTES:[ollama.com] ROCR_VISIBLE_DEVICES:]\"";
        yield return $"time={OllamaRecoveryFixture.Time(at.AddMilliseconds(1))} level=INFO source=routes.go:2375 msg=\"Ollama cloud disabled: false\"";
        yield return error is null
            ? $"time={OllamaRecoveryFixture.Time(at.AddMilliseconds(20))} level=INFO source=routes.go:2435 msg=\"Listening on 127.0.0.1:11434 (version 0.40.2)\""
            : "Error: " + error;
    }
}
