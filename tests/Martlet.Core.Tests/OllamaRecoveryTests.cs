using Martlet.Core.Installation;

namespace Martlet.Core.Tests;

public sealed class OllamaRecoveryTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "martlet-ollama-recovery-" + Guid.NewGuid().ToString("N"));
    private OllamaRecoveryFixture? made;

    public void Dispose()
    {
        made?.Delete();
        if (!Directory.Exists(folder)) return;
        foreach (var link in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories)
                     .Where(d => new DirectoryInfo(d).LinkTarget is not null).ToList())
            Directory.Delete(link);
        Directory.Delete(folder, recursive: true);
    }

    // Lines as Ollama 0.40.2 for Windows writes them (paths shortened to a fictional user).
    private const string Config = "time=2026-10-08T21:10:44.491-07:00 level=INFO source=routes.go:2373 msg=\"server config\" " +
        "env=\"map[CUDA_VISIBLE_DEVICES: HTTPS_PROXY: OLLAMA_CONTEXT_LENGTH:8192 OLLAMA_HOST:http://127.0.0.1:11434 " +
        "OLLAMA_MODELS:C:\\\\Users\\\\owner\\\\.ollama\\\\models OLLAMA_NOHISTORY:false OLLAMA_ORIGINS:[http://localhost app://*] " +
        "OLLAMA_REMOTES:[ollama.com] ROCR_VISIBLE_DEVICES:]\"";
    private const string Cloud = "time=2026-10-08T21:10:44.492-07:00 level=INFO source=routes.go:2375 msg=\"Ollama cloud disabled: false\"";
    private const string LinkError = "Error: mkdir C:\\Users\\owner\\.ollama\\models: Cannot create a file when that file already exists.: " +
        "ensure path elements are traversable";
    private const string Listening = "time=2026-10-08T21:11:57.114-07:00 level=INFO source=routes.go:2435 msg=\"Listening on 127.0.0.1:11434 (version 0.40.2)\"";
    private const string AppWarning = "time=2026-10-08T21:06:29.708-07:00 level=WARN source=server.go:260 msg=\"models path not accessible, " +
        "using default\" path=C:\\Users\\owner\\.ollama\\models err=\"CreateFile C:\\\\Users\\\\owner\\\\.ollama\\\\models: The path cannot " +
        "be traversed because it contains an untrusted mount point.\"";

    [Fact]
    public void OllamaRecoveryReadsACrashLoopFromRealLogLines()
    {
        var reading = OllamaLogs.Parse([LinkError, Config, Cloud, LinkError, Config, Cloud, LinkError, Config, Cloud, LinkError], [AppWarning]);

        Assert.Equal(@"C:\Users\owner\.ollama\models", reading.ModelsPath);
        Assert.Equal(@"mkdir C:\Users\owner\.ollama\models: Cannot create a file when that file already exists.: ensure path elements are traversable",
            reading.LastError);
        Assert.Equal("The path cannot be traversed because it contains an untrusted mount point.", reading.AppReason);
        Assert.Equal(3, reading.Starts);
        Assert.Equal(3, reading.FailedStarts);
        Assert.True(reading.LastStartFailed);
        Assert.Null(reading.ListeningVersion);
    }

    [Fact]
    public void OllamaRecoveryReadsAGoodStartAfterFailedOnes()
    {
        var reading = OllamaLogs.Parse([Config, Cloud, LinkError, Config, Cloud, Listening], []);

        Assert.Equal(2, reading.Starts);
        Assert.Equal(1, reading.FailedStarts);
        Assert.False(reading.LastStartFailed);
        Assert.Equal("0.40.2", reading.ListeningVersion);
        Assert.Null(reading.AppReason);
    }

    [Fact]
    public void OllamaRecoveryReadsAModelsPathWithSpacesAndTheLastOneOfSeveralStarts()
    {
        var other = Config.Replace(@"C:\\Users\\owner\\.ollama\\models", @"D:\\AI Models\\ollama", StringComparison.Ordinal);
        Assert.Equal(@"D:\AI Models\ollama", OllamaLogs.Parse([Config, other], []).ModelsPath);
        Assert.Equal(@"D:\AI Models\ollama", OllamaLogs.Parse([other.Replace(" ROCR_VISIBLE_DEVICES:", "", StringComparison.Ordinal)], []).ModelsPath);
        Assert.Null(OllamaLogs.Parse([Config.Replace(@"C:\\Users\\owner\\.ollama\\models", "", StringComparison.Ordinal)], []).ModelsPath);
    }

    [Fact]
    public void OllamaRecoveryReadsOnlyWholeLinesFromTheEndOfALargeLog()
    {
        Directory.CreateDirectory(folder);
        var log = Path.Combine(folder, "server.log");
        File.WriteAllLines(log, Enumerable.Range(0, 2000).Select(i => $"line {i:D4} " + new string('x', 40)));

        var tail = OllamaLogs.Tail(log, maxBytes: 1000);

        Assert.NotEmpty(tail);
        Assert.All(tail, line => Assert.Matches(@"^line \d{4} x{40}$", line));
        Assert.StartsWith("line 1999", tail[^1], StringComparison.Ordinal);
        Assert.Empty(OllamaLogs.Tail(Path.Combine(folder, "missing.log")));
    }

    [Fact]
    public void OllamaRecoveryKnowsOnlyTheLinkErrors()
    {
        Assert.True(OllamaRecovery.RefusesLink(LinkError));
        Assert.True(OllamaRecovery.RefusesLink("The path cannot be traversed because it contains an untrusted mount point."));
        Assert.False(OllamaRecovery.RefusesLink("listen tcp 127.0.0.1:11434: bind: Only one usage of each socket address"));
        Assert.False(OllamaRecovery.RefusesLink("mkdir D:\\models: Access is denied."));
        Assert.False(OllamaRecovery.RefusesLink(null));
    }

    [Fact]
    public void OllamaRecoveryStopsOnlyOllamasServersNeverItsCommandLineSessions()
    {
        Assert.True(OllamaRecovery.RunsServer("\"C:\\Users\\Owner Name\\AppData\\Local\\Programs\\Ollama\\ollama.exe\" serve"));
        Assert.True(OllamaRecovery.RunsServer("C:\\Programs\\Ollama\\ollama.exe --verbose serve"));
        Assert.True(OllamaRecovery.RunsServer("ollama start"));
        Assert.False(OllamaRecovery.RunsServer("\"C:\\Programs\\Ollama\\ollama.exe\" run gemma4:e2b"));
        Assert.False(OllamaRecovery.RunsServer("ollama.exe launch claude"));
        Assert.False(OllamaRecovery.RunsServer("ollama.exe"));
        Assert.False(OllamaRecovery.RunsServer("C:\\Tools\\not-ollama.exe serve"));
        Assert.False(OllamaRecovery.RunsServer(null));
    }

    private static readonly OllamaPlaces Places = new(@"C:\Users\owner\AppData\Local\Ollama", @"C:\Users\owner\.ollama\models",
        @"C:\Users\owner\AppData\Local\Programs\Ollama");
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 21, 11, 0, TimeSpan.FromHours(-7));
    private static readonly OllamaModelsFolder LinkedFolder = new(@"C:\Users\owner\.ollama\models", @"C:\Users\owner\.ollama\models",
        @"E:\Ollama\models", @"E:\Ollama\models", RealPathUsable: true);

    private static OllamaFacts Facts(OllamaLogReading log, bool app = true, OllamaAppSettings? settings = null,
        OllamaModelsFolder? folder = null, bool answers = false, bool installed = true) =>
        new(installed, true, app, false, answers, log, settings ?? new(true, true, @"C:\Users\owner\.ollama\models", null), null,
            [folder ?? LinkedFolder], Now);

    private static OllamaLogReading Loop(DateTimeOffset written, string error = LinkError) =>
        OllamaLogs.Parse([Config, Cloud, error, Config, Cloud, error], [AppWarning], written);

    [Fact]
    public void OllamaRecoveryFindsTheCrashLoopAndCanRepairIt()
    {
        var diagnosis = OllamaRecovery.Diagnose(Facts(Loop(Now.AddSeconds(-2))), Places);

        Assert.Equal(OllamaTroubleKind.CrashLoop, diagnosis.Kind);
        Assert.True(diagnosis.KnownCause);
        Assert.True(diagnosis.CanRepair);
        Assert.Equal(@"E:\Ollama\models", diagnosis.RealPath);
        Assert.Contains("ensure path elements are traversable", diagnosis.OllamaError, StringComparison.Ordinal);
        Assert.Contains("untrusted mount point", diagnosis.Message, StringComparison.Ordinal);
        Assert.Contains("2 failed starts", diagnosis.Message, StringComparison.Ordinal);
        Assert.Contains(@"is a link to E:\Ollama\models", diagnosis.Message, StringComparison.Ordinal);
        Assert.StartsWith("Ollama says: \"mkdir", diagnosis.Explanation, StringComparison.Ordinal);
        Assert.DoesNotContain("Martlet can point", diagnosis.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void OllamaRecoveryTellsAnOldFailedStartFromACrashLoop()
    {
        var old = OllamaRecovery.Diagnose(Facts(Loop(Now.AddHours(-3)), app: false), Places);
        Assert.Equal(OllamaTroubleKind.StopsOnStart, old.Kind);
        Assert.StartsWith("Ollama stopped the last time it started.", old.Message, StringComparison.Ordinal);

        var quiet = OllamaRecovery.Diagnose(Facts(OllamaLogs.Parse([Config, Cloud, Listening], [], Now)), Places);
        Assert.Equal(OllamaTroubleKind.NotRunning, quiet.Kind);
        Assert.Null(quiet.OllamaError);

        Assert.Equal(OllamaTroubleKind.Answering, OllamaRecovery.Diagnose(Facts(OllamaLogReading.None, answers: true), Places).Kind);
        Assert.Equal(OllamaTroubleKind.NotInstalled, OllamaRecovery.Diagnose(Facts(OllamaLogReading.None, installed: false), Places).Kind);
    }

    [Fact]
    public void OllamaRecoveryShowsAnotherErrorInOllamasWordsWithoutRepairing()
    {
        const string taken = "Error: listen tcp 127.0.0.1:11434: bind: Only one usage of each socket address is normally permitted.";
        var log = OllamaLogs.Parse([Config, Cloud, taken, Config, Cloud, taken], [], Now);

        var diagnosis = OllamaRecovery.Diagnose(Facts(log), Places);

        Assert.Equal(OllamaTroubleKind.CrashLoop, diagnosis.Kind);
        Assert.False(diagnosis.KnownCause);
        Assert.False(diagnosis.CanRepair);
        Assert.Contains("Ollama says: \"listen tcp 127.0.0.1:11434: bind: Only one usage", diagnosis.Message, StringComparison.Ordinal);
        Assert.Contains(Places.DataDirectory, diagnosis.Guidance, StringComparison.Ordinal);
    }

    [Fact]
    public void OllamaRecoveryGivesGuidanceWhenItCantRepair()
    {
        var unknown = OllamaRecovery.Diagnose(Facts(Loop(Now), settings: new(true, false, null, "Ollama's app settings have no models column.")), Places);
        Assert.True(unknown.KnownCause);
        Assert.False(unknown.CanRepair);
        Assert.Contains(@"set its model location to E:\Ollama\models", unknown.Guidance, StringComparison.Ordinal);
        Assert.Contains("no models column", unknown.Guidance, StringComparison.Ordinal);

        var gone = OllamaRecovery.Diagnose(Facts(Loop(Now), folder: LinkedFolder with { RealPathUsable = false }), Places);
        Assert.True(gone.KnownCause);
        Assert.False(gone.CanRepair);
        Assert.Contains("Move the models folder back", gone.Guidance, StringComparison.Ordinal);

        var notLinked = OllamaRecovery.Diagnose(Facts(Loop(Now), folder: new(@"C:\Users\owner\.ollama\models", null, null,
            @"C:\Users\owner\.ollama\models", false)), Places);
        Assert.False(notLinked.KnownCause);
    }

    // ---------- on Windows: a real junction, Windows' SQLite and the repair, all on a fixture folder ----------

    private OllamaRecoveryFixture Fixture(OllamaFixtureScenario scenario) =>
        made = OllamaRecoveryFixture.Create(folder, scenario, DateTimeOffset.Now);

    private static readonly IProgress<string> Quiet = new Progress<string>();

    [Fact]
    public void OllamaRecoveryFollowsAJunctionAnywhereInThePath()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fixture = Fixture(OllamaFixtureScenario.Linked);

        var direct = OllamaModelsFolder.Inspect(fixture.ModelsPath);
        Assert.True(direct.Linked);
        Assert.Equal(fixture.ModelsPath, direct.LinkPath);
        Assert.True(OllamaModelsFolder.SamePath(fixture.RealPath, direct.RealPath));
        Assert.True(direct.RealPathUsable);

        var alias = Path.Combine(folder, "Alias");
        OllamaRecoveryFixture.Junction(alias, Path.GetDirectoryName(fixture.RealPath)!);
        var parent = OllamaModelsFolder.Inspect(Path.Combine(alias, "models"));
        Assert.Equal(alias, parent.LinkPath);
        Assert.True(OllamaModelsFolder.SamePath(fixture.RealPath, parent.RealPath));
        Assert.True(parent.RealPathUsable);

        var real = OllamaModelsFolder.Inspect(fixture.RealPath);
        Assert.False(real.Linked);
        Assert.True(real.RealPathUsable);
        Assert.False(OllamaModelsFolder.Inspect(Path.Combine(folder, "missing")).RealPathUsable);
    }

    [Fact]
    public void OllamaRecoveryReadsBacksUpAndWritesOllamasAppDatabase()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fixture = Fixture(OllamaFixtureScenario.Linked);
        var database = fixture.Places.Database;

        Assert.Equal(new OllamaAppSettings(true, true, fixture.ModelsPath, null), OllamaAppDatabase.Read(database));
        var backup = OllamaAppDatabase.Backup(database, DateTimeOffset.Now);
        OllamaAppDatabase.WriteModels(database, fixture.RealPath);

        Assert.Equal(fixture.RealPath, OllamaAppDatabase.Read(database).Models);
        Assert.Equal(fixture.ModelsPath, OllamaAppDatabase.Read(Path.Combine(backup, "db.sqlite")).Models);
        Assert.StartsWith("martlet-backup-", Path.GetFileName(backup), StringComparison.Ordinal);
        Assert.NotEqual(backup, OllamaAppDatabase.Backup(database, DateTimeOffset.Now));
        Assert.False(OllamaAppDatabase.Read(Path.Combine(folder, "missing.sqlite")).Found);
    }

    [Fact]
    public void OllamaRecoveryNeverWritesADatabaseOfAnotherShape()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fixture = Fixture(OllamaFixtureScenario.UnknownSettings);
        var before = File.ReadAllBytes(fixture.Places.Database);

        var read = OllamaAppDatabase.Read(fixture.Places.Database);
        Assert.True(read.Found);
        Assert.False(read.KnownShape);
        Assert.Contains("no models column", read.Problem, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => OllamaAppDatabase.WriteModels(fixture.Places.Database, fixture.RealPath));
        Assert.Equal(before, File.ReadAllBytes(fixture.Places.Database));
    }

    [Fact]
    public async Task OllamaRecoveryRepairsTheLinkedModelsFolderSafely()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fixture = Fixture(OllamaFixtureScenario.Linked);
        var control = fixture.Control;

        var diagnosis = await OllamaRecovery.DiagnoseAsync(control, DateTimeOffset.Now, CancellationToken.None);
        Assert.Equal(OllamaTroubleKind.CrashLoop, diagnosis.Kind);
        Assert.True(diagnosis.CanRepair, diagnosis.Guidance);
        Assert.Equal(5, diagnosis.Facts!.Log.FailedStarts);

        var result = await OllamaRecovery.RepairAsync(diagnosis, control, Quiet, () => DateTimeOffset.Now, CancellationToken.None,
            TimeSpan.FromSeconds(2));

        Assert.True(result.Repaired, result.Summary);
        Assert.Equal(["stop", $"set OLLAMA_MODELS={diagnosis.RealPath}", $"start OLLAMA_MODELS={diagnosis.RealPath}"], control.Events);
        Assert.Equal(fixture.ModelsPath, control.ModelsWhenStopped);
        Assert.Equal(diagnosis.RealPath, fixture.SavedModels);
        Assert.Equal(diagnosis.RealPath, control.UserModelsVariable);
        Assert.Equal(fixture.ModelsPath, OllamaAppDatabase.Read(Path.Combine(result.Backup!, "db.sqlite")).Models);
        Assert.Equal(2, result.Changes.Count);
        Assert.Equal(1, result.Models);
        Assert.Contains("answers again and lists 1 model.", result.Summary, StringComparison.Ordinal);
        Assert.Equal(OllamaTroubleKind.Answering, (await OllamaRecovery.DiagnoseAsync(control, DateTimeOffset.Now, CancellationToken.None)).Kind);
    }

    [Fact]
    public async Task OllamaRecoveryKeepsAnOllamaModelsSettingThatPointsElsewhere()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fixture = Fixture(OllamaFixtureScenario.Linked);
        fixture.Control.UserModelsVariable = @"Z:\Somewhere\else";
        var diagnosis = await OllamaRecovery.DiagnoseAsync(fixture.Control, DateTimeOffset.Now, CancellationToken.None);

        var result = await OllamaRecovery.RepairAsync(diagnosis, fixture.Control, Quiet, () => DateTimeOffset.Now, CancellationToken.None,
            TimeSpan.FromSeconds(2));

        Assert.True(result.Repaired, result.Summary);
        Assert.Equal(@"Z:\Somewhere\else", fixture.Control.UserModelsVariable);
        Assert.DoesNotContain(fixture.Control.Events, e => e.StartsWith("set ", StringComparison.Ordinal));
        Assert.Single(result.Changes);
    }

    [Fact]
    public async Task OllamaRecoveryStartsOllamaAgainWhenCancelledAfterStoppingIt()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fixture = Fixture(OllamaFixtureScenario.Linked);
        var diagnosis = await OllamaRecovery.DiagnoseAsync(fixture.Control, DateTimeOffset.Now, CancellationToken.None);
        using var cancel = new CancellationTokenSource();
        fixture.Control.AfterStop = cancel.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OllamaRecovery.RepairAsync(diagnosis, fixture.Control, Quiet, () => DateTimeOffset.Now, cancel.Token));

        Assert.Equal(["stop", "start OLLAMA_MODELS=(unchanged)"], fixture.Control.Events);
        Assert.Equal(fixture.ModelsPath, fixture.SavedModels);
    }

    [Fact]
    public async Task OllamaRecoveryChangesNothingWhenItCantOrNeedNot()
    {
        if (!OperatingSystem.IsWindows()) return;
        var unknown = Fixture(OllamaFixtureScenario.UnknownSettings);
        var guidance = await OllamaRecovery.DiagnoseAsync(unknown.Control, DateTimeOffset.Now, CancellationToken.None);
        Assert.True(guidance.KnownCause);
        Assert.False(guidance.CanRepair);
        var refused = await OllamaRecovery.RepairAsync(guidance, unknown.Control, Quiet, () => DateTimeOffset.Now, CancellationToken.None);
        Assert.False(refused.Repaired);
        Assert.Empty(unknown.Control.Events);
        unknown.Delete();

        var linked = Fixture(OllamaFixtureScenario.Linked);
        var diagnosis = await OllamaRecovery.DiagnoseAsync(linked.Control, DateTimeOffset.Now, CancellationToken.None);
        linked.Control.RefuseStop = true;
        var stuck = await OllamaRecovery.RepairAsync(diagnosis, linked.Control, Quiet, () => DateTimeOffset.Now, CancellationToken.None);
        Assert.False(stuck.Repaired);
        Assert.Contains("couldn't stop Ollama", stuck.Summary, StringComparison.Ordinal);
        Assert.Equal(linked.ModelsPath, linked.SavedModels);
        Assert.Empty(stuck.Changes);
        // Whatever stopped, Ollama is started again.
        Assert.Equal(["start OLLAMA_MODELS=(unchanged)"], linked.Control.Events);
        linked.Delete();

        var other = Fixture(OllamaFixtureScenario.OtherError);
        var said = await OllamaRecovery.DiagnoseAsync(other.Control, DateTimeOffset.Now, CancellationToken.None);
        Assert.Equal(OllamaTroubleKind.CrashLoop, said.Kind);
        Assert.False(said.KnownCause);
        Assert.Contains("Only one usage of each socket address", said.Message, StringComparison.Ordinal);
        other.Delete();

        var answering = Fixture(OllamaFixtureScenario.Answering);
        Assert.Equal(OllamaTroubleKind.Answering, (await OllamaRecovery.DiagnoseAsync(answering.Control, DateTimeOffset.Now, CancellationToken.None)).Kind);
        var already = await OllamaRecovery.RepairAsync(diagnosis, answering.Control, Quiet, () => DateTimeOffset.Now, CancellationToken.None);
        Assert.True(already.Repaired);
        Assert.Empty(answering.Control.Events);
    }
}
