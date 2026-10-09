namespace Martlet.Core.Installation;

/// <summary>Runs and changes Ollama on this PC for <see cref="OllamaRecovery"/>: the production one drives the real processes and
/// loopback API, a fixture (<see cref="OllamaRecoveryFixture"/>) stands in for them.</summary>
public interface IOllamaControl
{
    OllamaPlaces Places { get; }

    /// <summary>Whether <c>ollama.exe</c> is installed.</summary>
    bool Installed { get; }

    /// <summary>Whether Ollama's tray app (<c>ollama app.exe</c>) is installed; it starts and restarts the server itself.</summary>
    bool AppInstalled { get; }

    /// <summary>Whether the tray app and an <c>ollama.exe</c> process run now.</summary>
    (bool App, bool Server) Running();

    /// <summary>Whether Ollama answers <c>/api/version</c> on 127.0.0.1:11434 now.</summary>
    Task<bool> AnswersAsync(CancellationToken token);

    /// <summary>The models Ollama lists now, or null when it doesn't answer.</summary>
    Task<IReadOnlyList<string>?> ModelsAsync(CancellationToken token);

    /// <summary>Stops the tray app, then every Ollama server, and waits until they are gone. Throws
    /// <see cref="InvalidOperationException"/> when they don't stop.</summary>
    Task StopAsync(CancellationToken token);

    /// <summary>Starts Ollama (the tray app, or a hidden server without it) with OLLAMA_MODELS set to <paramref name="models"/>
    /// (null: as this process has it). Throws <see cref="InvalidOperationException"/> when it can't start.</summary>
    void Start(string? models);

    /// <summary>The current user's saved OLLAMA_MODELS, or null when it isn't set.</summary>
    string? UserModelsVariable { get; }

    /// <summary>Saves the current user's OLLAMA_MODELS.</summary>
    void SetUserModelsVariable(string value);
}

/// <summary>What Martlet knows about Ollama on this PC when it doesn't answer.</summary>
public sealed record OllamaFacts(bool Installed, bool AppInstalled, bool AppRunning, bool ServerRunning, bool Answers,
    OllamaLogReading Log, OllamaAppSettings? App, string? UserModelsVariable, IReadOnlyList<OllamaModelsFolder> ModelsFolders,
    DateTimeOffset Now);

public enum OllamaTroubleKind
{
    /// <summary>Ollama answers.</summary>
    Answering,
    NotInstalled,
    /// <summary>Nothing answers and Ollama's logs show no failed start.</summary>
    NotRunning,
    /// <summary>Ollama's last start stopped with an error (<see cref="OllamaDiagnosis.OllamaError"/>).</summary>
    StopsOnStart,
    /// <summary>Ollama keeps starting and stopping: its tray app runs (or it failed to start again and again just now), the
    /// last start stopped with an error and nothing answers.</summary>
    CrashLoop
}

/// <summary>Why Ollama on this PC doesn't answer, in Ollama's own words where its logs have them. <see cref="KnownCause"/>: its
/// models folder (<see cref="ModelsPath"/>) is reached through a junction or symbolic link (<see cref="LinkPath"/>, pointing at
/// <see cref="LinkTarget"/>) that this Ollama version refuses; <see cref="CanRepair"/>: Martlet can point Ollama at the real
/// folder (<see cref="RealPath"/>) instead.</summary>
public sealed record OllamaDiagnosis(OllamaTroubleKind Kind, string? OllamaError, bool KnownCause, bool CanRepair,
    string? ModelsPath, string? LinkPath, string? LinkTarget, string? RealPath, string Message, string? Guidance, OllamaFacts? Facts)
{
    /// <summary>Ollama's own words and the cause Martlet found, without the opening sentence or what Martlet can do (empty
    /// unless Ollama <see cref="Stops"/>).</summary>
    public string Explanation { get; init; } = "";

    /// <summary>Ollama is installed but stops when it starts.</summary>
    public bool Stops => Kind is OllamaTroubleKind.StopsOnStart or OllamaTroubleKind.CrashLoop;

    /// <summary>What a repair would change, so one automatic try is made for each cause.</summary>
    public string RepairKey => $"{ModelsPath}|{RealPath}";
}

/// <summary>What a repair did: <paramref name="Repaired"/> when Ollama answers afterwards; <paramref name="Changes"/> each
/// change made (old and new value); <paramref name="Backup"/> the folder with Ollama's app database as it was.</summary>
public sealed record OllamaRepairResult(bool Repaired, string Summary, IReadOnlyList<string> Changes, string? Backup, int? Models,
    OllamaDiagnosis? After);

/// <summary>Finds why Ollama on this PC keeps stopping, from its own logs, its processes and its models folder, and repairs
/// the one known cause: Ollama 0.40.1 and later refuse a models folder reached through a junction or symbolic link (a folder
/// moved to another drive and linked back), so the server stops about once a second with "mkdir ...: ensure path elements are
/// traversable" and its tray app logs "The path cannot be traversed because it contains an untrusted mount point". The repair
/// points Ollama at the real folder: it backs up the app's database and changes it only while Ollama's processes are stopped,
/// and changes only values that point at the link.</summary>
public static class OllamaRecovery
{
    /// <summary>How recent the server log must be for a crash loop.</summary>
    public static readonly TimeSpan Recent = TimeSpan.FromMinutes(10);

    /// <summary>How long a repair waits for Ollama to answer after starting it.</summary>
    public static readonly TimeSpan StartLimit = TimeSpan.FromSeconds(30);

    /// <summary>Reads what Martlet needs to know from <paramref name="control"/>, its logs, app database and models folders.
    /// Reads only; when Ollama answers it reads nothing else.</summary>
    public static async Task<OllamaFacts> ReadFactsAsync(IOllamaControl control, DateTimeOffset now, CancellationToken token)
    {
        var answers = await control.AnswersAsync(token).ConfigureAwait(false);
        if (answers || !control.Installed)
            return new(control.Installed, control.AppInstalled, false, false, answers, OllamaLogReading.None, null, null, [], now);
        var (app, server) = control.Running();
        var log = OllamaLogs.Read(control.Places);
        var settings = control.AppInstalled ? OllamaAppDatabase.Read(control.Places.Database) : null;
        var variable = control.UserModelsVariable;
        var folders = new List<OllamaModelsFolder>();
        foreach (var path in new[] { log.ModelsPath, settings?.Models, variable, control.Places.DefaultModels })
        {
            if (string.IsNullOrWhiteSpace(path) || folders.Any(f => OllamaModelsFolder.SamePath(f.Path, path))) continue;
            folders.Add(OllamaModelsFolder.Inspect(path));
        }
        return new(true, control.AppInstalled, app, server, false, log, settings, variable, folders, now);
    }

    /// <summary>Reads the facts (<see cref="ReadFactsAsync"/>) and diagnoses them.</summary>
    public static async Task<OllamaDiagnosis> DiagnoseAsync(IOllamaControl control, DateTimeOffset now, CancellationToken token) =>
        Diagnose(await ReadFactsAsync(control, now, token).ConfigureAwait(false), control.Places);

    /// <summary>Whether <paramref name="error"/> (the server's or the tray app's) is Ollama refusing a linked models folder.</summary>
    public static bool RefusesLink(string? error) => error is not null &&
        (error.Contains("untrusted mount point", StringComparison.OrdinalIgnoreCase) ||
         error.Contains("mkdir", StringComparison.OrdinalIgnoreCase) &&
         error.Contains("ensure path elements are traversable", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether <paramref name="commandLine"/> runs Ollama's server (<c>ollama serve</c> or <c>ollama start</c>, flags
    /// aside), the way Ollama's own app tells its servers from command-line sessions such as <c>ollama run</c> or
    /// <c>ollama launch</c>, which a repair must never stop.</summary>
    public static bool RunsServer(string? commandLine)
    {
        var arguments = Arguments(commandLine);
        if (arguments.Count < 2 || Path.GetFileName(arguments[0]) is not { } program ||
            !program.Equals("ollama", StringComparison.OrdinalIgnoreCase) && !program.Equals("ollama.exe", StringComparison.OrdinalIgnoreCase))
            return false;
        var command = arguments.Skip(1).FirstOrDefault(argument => !argument.StartsWith('-'));
        return command is "serve" or "start";
    }

    /// <summary>A Windows command line split into its arguments: spaces separate them except inside double quotes, and the
    /// quotes are removed.</summary>
    private static List<string> Arguments(string? commandLine)
    {
        var arguments = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine)) return arguments;
        var current = new System.Text.StringBuilder();
        bool quoted = false, any = false;
        foreach (var c in commandLine)
        {
            if (c == '"') { quoted = !quoted; any = true; }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any) arguments.Add(current.ToString());
                current.Clear();
                any = false;
            }
            else { current.Append(c); any = true; }
        }
        if (any) arguments.Add(current.ToString());
        return arguments;
    }

    public static OllamaDiagnosis Diagnose(OllamaFacts facts, OllamaPlaces places)
    {
        if (facts.Answers) return new(OllamaTroubleKind.Answering, null, false, false, null, null, null, null, "Ollama answers on this PC.", null, facts);
        if (!facts.Installed)
            return new(OllamaTroubleKind.NotInstalled, null, false, false, null, null, null, null, "Ollama isn't installed on this PC.",
                "Install Ollama, then try again.", facts);
        var log = facts.Log;
        var error = log.LastError ?? log.AppReason;
        if (!log.LastStartFailed || error is null)
            return new(OllamaTroubleKind.NotRunning, null, false, false, null, null, null, null, "Ollama isn't running on this PC.",
                "Start Ollama from the Start menu, then try again.", facts);

        var recent = log.Written is { } written && facts.Now - written <= Recent;
        var kind = recent && (facts.AppRunning || facts.ServerRunning || log.FailedStarts >= 2)
            ? OllamaTroubleKind.CrashLoop : OllamaTroubleKind.StopsOnStart;
        var said = error;
        var words = $"Ollama says: \"{said.TrimEnd('.')}\"." +
            (log.AppReason is { } reason && log.LastError is { } last && !last.Contains(reason.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)
                ? $" Its app says: \"{reason.TrimEnd('.')}\"." : "");
        var opening = (kind == OllamaTroubleKind.CrashLoop
            ? $"Ollama keeps stopping on this PC{(log.FailedStarts > 1 ? $" ({log.FailedStarts} failed starts in its log)" : "")}."
            : "Ollama stopped the last time it started.") + " " + words;

        var linked = RefusesLink(log.LastError) || RefusesLink(log.AppReason)
            ? facts.ModelsFolders.FirstOrDefault(f => f.Linked) : null;
        if (linked is null)
            return new(kind, said, false, false, null, null, null, null, opening,
                $"Fix what Ollama says, or reinstall Ollama, then start it again. Its logs are in {places.DataDirectory}.", facts)
            { Explanation = words };

        var cause = $" Its models folder {linked.LinkPath} is a link to {linked.LinkTarget}, and this Ollama version refuses to " +
            "use models through a link.";
        if (!linked.RealPathUsable)
            return new(kind, said, true, false, linked.Path, linked.LinkPath, linked.LinkTarget, linked.RealPath, opening + cause,
                $"Martlet can't find Ollama's models in {linked.RealPath ?? linked.LinkTarget}. Move the models folder back to " +
                $"{linked.LinkPath}, or set Ollama's model location (in Ollama's settings) to the folder that holds your models.", facts)
            { Explanation = words + cause };
        if (facts.AppInstalled && facts.App is not { KnownShape: true })
            return new(kind, said, true, false, linked.Path, linked.LinkPath, linked.LinkTarget, linked.RealPath, opening + cause,
                $"Open Ollama's settings, set its model location to {linked.RealPath}, then start Ollama again." +
                (facts.App?.Problem is { } problem ? $" (Martlet can't change it itself: {problem.TrimEnd('.')}.)" : ""), facts)
            { Explanation = words + cause };
        return new(kind, said, true, true, linked.Path, linked.LinkPath, linked.LinkTarget, linked.RealPath,
            opening + cause + $" Martlet can point Ollama at {linked.RealPath} directly.",
            $"Open Ollama's settings, set its model location to {linked.RealPath}, then start Ollama again.", facts)
            { Explanation = words + cause };
    }

    /// <summary>Points Ollama at the real models folder of <paramref name="diagnosis"/> (see <see cref="OllamaRecovery"/>):
    /// checks again that Ollama doesn't answer, stops it, backs up its app database, sets its Model location when it is empty
    /// or the link, sets the user's OLLAMA_MODELS when it isn't set or is the link, starts Ollama with the real folder and
    /// waits for it to answer. Changes stay when Ollama still doesn't answer (the real folder is no worse than the refused
    /// link); the backup undoes them.</summary>
    public static async Task<OllamaRepairResult> RepairAsync(OllamaDiagnosis diagnosis, IOllamaControl control, IProgress<string> output,
        Func<DateTimeOffset> now, CancellationToken token, TimeSpan? startLimit = null)
    {
        var changes = new List<string>();
        if (!diagnosis.CanRepair || diagnosis.RealPath is not { } real || diagnosis.ModelsPath is not { } models || diagnosis.LinkPath is not { } link)
            return new(false, diagnosis.Guidance ?? diagnosis.Message, changes, null, null, diagnosis);
        if (await control.AnswersAsync(token).ConfigureAwait(false))
            return new(true, "Ollama answers on this PC now, so Martlet changed nothing.", changes, null, null, null);
        if (OllamaModelsFolder.Inspect(models) is not { Linked: true, RealPathUsable: true } folder || !OllamaModelsFolder.SamePath(folder.RealPath, real))
            return new(false, $"Ollama's models folder changed since Martlet looked ({models}), so Martlet changed nothing.",
                changes, null, null, diagnosis);

        output.Report("Stopping Ollama so its settings can be changed safely...");
        var started = false;
        bool StartAgain(string? with)
        {
            started = true;
            return Restart(control, with, output);
        }
        try
        {
            try { await control.StopAsync(token).ConfigureAwait(false); }
            catch (InvalidOperationException error)
            {
                return new(false, $"Martlet couldn't stop Ollama ({error.Message.TrimEnd('.')}), so it changed nothing. " +
                    (diagnosis.Guidance ?? ""), changes, null, null, diagnosis);
            }

            string? backup = null;
            if (control.AppInstalled)
            {
                var settings = OllamaAppDatabase.Read(control.Places.Database);
                if (!settings.KnownShape)
                {
                    StartAgain(null);
                    return new(false, $"{settings.Problem} Martlet changed nothing. Open Ollama's settings, set its model location to " +
                        $"{real}, then start Ollama again.", changes, null, null, diagnosis);
                }
                var saved = settings.Models ?? "";
                if (saved.Length == 0 || PointsAt(saved, models, real))
                {
                    try
                    {
                        backup = OllamaAppDatabase.Backup(control.Places.Database, now());
                        output.Report($"Backed up Ollama's app database to {backup}.");
                        OllamaAppDatabase.WriteModels(control.Places.Database, real);
                        changes.Add($"Ollama's model location (its app database, settings.models): \"{saved}\" -> \"{real}\"");
                        output.Report($"Set Ollama's model location to {real}.");
                    }
                    catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException)
                    {
                        StartAgain(null);
                        return new(false, $"Martlet couldn't change Ollama's model location ({error.Message.TrimEnd('.')}). Open Ollama's " +
                            $"settings, set its model location to {real}, then start Ollama again.", changes, backup, null, diagnosis);
                    }
                }
            }

            var variable = control.UserModelsVariable;
            if (string.IsNullOrWhiteSpace(variable) || PointsAt(variable, models, real))
            {
                try
                {
                    control.SetUserModelsVariable(real);
                    changes.Add($"Your OLLAMA_MODELS setting: {(string.IsNullOrWhiteSpace(variable) ? "not set" : $"\"{variable}\"")} -> \"{real}\"");
                    output.Report($"Set OLLAMA_MODELS to {real}.");
                }
                catch (Exception error) when (error is InvalidOperationException or System.Security.SecurityException or UnauthorizedAccessException)
                {
                    output.Report($"Couldn't set OLLAMA_MODELS ({error.Message.TrimEnd('.')}); Ollama's own setting is enough for its app.");
                }
            }

            if (!StartAgain(real))
                return new(false, "Martlet pointed Ollama at its models but couldn't start it again. Start Ollama from the Start menu.",
                    changes, backup, null, diagnosis);
            var limit = startLimit ?? StartLimit;
            var waited = System.Diagnostics.Stopwatch.StartNew();
            var answers = false;
            while (!(answers = await control.AnswersAsync(token).ConfigureAwait(false)) && waited.Elapsed < limit)
                await Task.Delay(TimeSpan.FromMilliseconds(500), token).ConfigureAwait(false);
            if (!answers)
            {
                var after = await DiagnoseAsync(control, now(), token).ConfigureAwait(false);
                return new(false, $"Martlet pointed Ollama at {real}, but Ollama still doesn't answer. " +
                    (after.OllamaError is { } said ? $"Ollama says: \"{said.TrimEnd('.')}\". " : "") +
                    (backup is null ? "" : $"Ollama's app database as it was is in {backup}."), changes, backup, null, after);
            }
            var listed = await control.ModelsAsync(token).ConfigureAwait(false);
            output.Report($"Ollama answers again and lists {listed?.Count ?? 0} {(listed?.Count == 1 ? "model" : "models")}.");
            return new(true, $"Ollama couldn't use its models through the link at {link}, so Martlet pointed it at {real}. Ollama answers " +
                $"again and lists {listed?.Count ?? 0} {(listed?.Count == 1 ? "model" : "models")}." +
                (backup is null ? "" : $" Ollama's app database as it was is in {backup}."), changes, backup, listed?.Count, null);
        }
        finally
        {
            // Ollama never stays stopped, whatever went wrong after it was stopped (a cancellation included).
            if (!started) Restart(control, null, output);
        }
    }

    /// <summary>Whether <paramref name="value"/> is the linked models folder, or another path that leads through a link to the
    /// same real folder.</summary>
    private static bool PointsAt(string value, string models, string real) =>
        OllamaModelsFolder.SamePath(value, models) ||
        OllamaModelsFolder.Inspect(value) is { Linked: true } through && OllamaModelsFolder.SamePath(through.RealPath, real);

    private static bool Restart(IOllamaControl control, string? models, IProgress<string> output)
    {
        try
        {
            control.Start(models);
            output.Report("Started Ollama again.");
            return true;
        }
        catch (InvalidOperationException error)
        {
            output.Report($"Couldn't start Ollama again: {error.Message}");
            return false;
        }
    }
}
