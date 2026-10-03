using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

#if MARTLET_RENDERER
namespace Martlet.Avatar.RendererHost.Logging;
#else
namespace Martlet.Logging;
#endif

/// <summary>
/// Local, append-only error log for crashes and unexpected failures. Never uploaded. Each process writes its own
/// bounded, rotated file under <c>&lt;data directory&gt;\logs</c>. Logging must never throw or crash Martlet.
/// </summary>
internal static class ErrorLog
{
    internal const string DirectoryEnvironmentVariable = "MARTLET_LOG_DIRECTORY";
    private const long MaximumFileBytes = 2 * 1024 * 1024;
    private const int RetainedFiles = 3;
    private static readonly object gate = new();
    private static string? directory;
    private static string component = "martlet";
    private static string? sessionMarker;
    private static bool shownDialog;
    private static DateTimeOffset lastDialog = DateTimeOffset.MinValue;
    private static int errorCount;
    private static (DateTimeOffset At, string Message)? lastError;
    private static string? previousCrash;
    // Windows Error Reporting can write its record a few seconds after the crash; a quick relaunch looks once more.
    private static readonly TimeSpan CrashRecordRetryDelay = TimeSpan.FromSeconds(20);

    internal static string? Directory => directory;
    /// <summary>ERROR and FATAL entries written since this process started.</summary>
    internal static int ErrorCount { get { lock (gate) return errorCount; } }
    /// <summary>The latest ERROR or FATAL entry's time and one-line message (the exception's type and message, no stack).</summary>
    internal static (DateTimeOffset At, string Message)? LastError { get { lock (gate) return lastError; } }
    /// <summary>What Windows recorded about the latest unclean previous run, for example "an access violation (0xc0000005)
    /// in MMDevApi.dll"; null until looked up after startup, or when Windows has no crash record for it.</summary>
    internal static string? PreviousCrash { get { lock (gate) return previousCrash; } }
    /// <summary>Raised on the writing thread after an ERROR or FATAL entry.</summary>
    internal static event Action? ErrorRecorded;
    /// <summary>Raised on a background thread once Windows' crash records for unclean previous runs were looked up.</summary>
    internal static event Action? PreviousRunDescribed;
    internal static string? CurrentFile => directory is null ? null : Path.Combine(directory, component + ".log");

    internal static string DefaultDirectory(string? dataDirectory)
    {
        var root = dataDirectory;
        if (string.IsNullOrWhiteSpace(root))
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            root = string.IsNullOrWhiteSpace(local) ? Path.GetTempPath() : Path.Combine(local, "Martlet");
        }
        return Path.Combine(root, "logs");
    }

    /// <summary>Starts logging and installs process-wide handlers. Returns true when the previous run did not exit cleanly.</summary>
    internal static bool Initialize(string logDirectory, string componentName)
    {
        var uncleanPreviousExit = false;
        var uncleanRuns = new List<(int ProcessId, DateTimeOffset Started)>();
        lock (gate)
        {
            component = componentName;
            try
            {
                System.IO.Directory.CreateDirectory(logDirectory);
                directory = logDirectory;
                // One marker per process: a marker whose process is gone means that run never reached a clean exit.
                foreach (var stale in System.IO.Directory.EnumerateFiles(logDirectory, componentName + ".*.running"))
                {
                    var name = Path.GetFileNameWithoutExtension(stale);
                    if (int.TryParse(name[(componentName.Length + 1)..], out var pid) && IsRunning(pid)) continue;
                    uncleanPreviousExit = true;
                    if (pid > 0) uncleanRuns.Add((pid, MarkerStarted(stale)));
                    File.Delete(stale);
                }
                sessionMarker = Path.Combine(logDirectory, $"{componentName}.{Environment.ProcessId}.running");
                File.WriteAllText(sessionMarker, DateTimeOffset.Now.ToString("O"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                directory = null;
                sessionMarker = null;
            }
        }
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("FATAL", $"Unhandled exception (terminating: {e.IsTerminating})", e.ExceptionObject as Exception,
                e.ExceptionObject is Exception ? null : e.ExceptionObject?.ToString());
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("ERROR", "Unobserved background task exception", e.Exception);
            e.SetObserved();
        };
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
        Info($"{componentName} started. Version {version}; .NET {Environment.Version}; {Environment.OSVersion}; pid {Environment.ProcessId}." +
            (uncleanPreviousExit ? " The previous run did not exit cleanly (crash, kill or power loss)." : ""));
        // Native crashes (for example an access violation inside a Windows DLL on one of its own threads) never reach
        // the handlers above, so read how Windows recorded the run's end instead.
        if (uncleanRuns.Count > 0) Task.Run(() => DescribeUncleanRunsAsync(uncleanRuns));
        return uncleanPreviousExit;
    }

    /// <summary>Installs the WPF UI-thread handler: log, tell the user once, and keep running when possible.
    /// <paramref name="showReport"/> (title, heading, report) shows the report with a Copy button and returns whether it could;
    /// otherwise, or when it couldn't, a plain message box tells the user.</summary>
    internal static void AttachDispatcher(Application application, string productName, bool showDialog = true,
        Func<string, string, string, bool>? showReport = null)
    {
        application.DispatcherUnhandledException += (_, e) =>
        {
            Error("Unhandled UI exception", e.Exception);
            if (IsFatal(e.Exception)) return;
            e.Handled = true;
            // A repeating failure (e.g. every frame) must not bury the user in dialogs.
            if (!showDialog || shownDialog || DateTimeOffset.UtcNow - lastDialog < TimeSpan.FromSeconds(30)) return;
            shownDialog = true;
            try
            {
                var log = CurrentFile ?? "(log unavailable)";
                var report = "The last action may not have finished.\n\n" +
                    $"Error: {e.Exception.Message}\n\nDetails were saved locally to:\n{log}\n\n{e.Exception}";
                if (showReport?.Invoke($"{productName} - Unexpected error", $"{productName} recovered from an unexpected error", report) != true)
                {
                    var choice = MessageBox.Show(
                        $"{productName} recovered from an unexpected error. The last action may not have finished.\n\n" +
                        $"Error: {e.Exception.Message}\n\n" +
                        $"Details were saved locally to:\n{log}\n\n" +
                        "Open the logs folder now? (Ctrl+C copies this message.)",
                        $"{productName} - Unexpected error", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (choice == MessageBoxResult.Yes) OpenFolder();
                }
            }
            catch (Exception ex) when (!IsFatal(ex)) { Error("Error dialog failed", ex); }
            finally { shownDialog = false; lastDialog = DateTimeOffset.UtcNow; }
        };
    }

    internal static void MarkCleanExit()
    {
        Info($"{component} exited cleanly.");
        lock (gate)
        {
            try { if (sessionMarker is not null) File.Delete(sessionMarker); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static void Info(string message) => Write("INFO", message, null);
    internal static void Warn(string message, Exception? exception = null) => Write("WARN", message, exception);
    internal static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    /// <summary>Attaches to a fire-and-forget task so a failure is logged rather than silently dropped.</summary>
    internal static void Observe(Task task, string operation)
    {
        task.ContinueWith(t => Error($"{operation} failed", t.Exception?.GetBaseException()),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    internal static bool OpenFolder()
    {
        if (directory is null) return false;
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { directory }, UseShellExecute = false })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Error("Could not open logs folder", ex);
            return false;
        }
    }

    internal static string? ReadRecent(int maximumChars = 64 * 1024)
    {
        var path = CurrentFile;
        if (path is null || !File.Exists(path)) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - maximumChars);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException or BadImageFormatException;

    private static DateTimeOffset MarkerStarted(string marker)
    {
        try
        {
            return DateTimeOffset.TryParse(File.ReadAllText(marker).Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var started)
                ? started : new DateTimeOffset(File.GetCreationTimeUtc(marker), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTimeOffset.MinValue; }
    }

    private static async Task DescribeUncleanRunsAsync(IReadOnlyList<(int ProcessId, DateTimeOffset Started)> runs)
    {
        try
        {
            var pending = runs.ToList();
            var newest = DateTimeOffset.MinValue;
            string? newestCrash = null;
            for (var attempt = 0; pending.Count > 0; attempt++)
            {
                if (attempt > 0) await Task.Delay(CrashRecordRetryDelay).ConfigureAwait(false);
                foreach (var run in pending.ToArray())
                {
                    var crash = FindWindowsCrash(run.ProcessId, run.Started);
                    if (crash is null && attempt == 0) continue;
                    pending.Remove(run);
                    var which = $"the previous run (pid {run.ProcessId}" +
                        (run.Started == DateTimeOffset.MinValue ? ")" : $", started {run.Started.LocalDateTime:yyyy-MM-dd HH:mm:ss})");
                    if (crash is { } found)
                    {
                        Warn($"Windows recorded how {which} ended: {found.Detail}");
                        if (run.Started >= newest) (newest, newestCrash) = (run.Started, found.Summary);
                    }
                    else Info($"Windows recorded no crash for {which}. It was probably ended by Task Manager, a sign-out, " +
                        "a shutdown or power loss, or Windows Error Reporting is off.");
                }
                lock (gate) previousCrash = newestCrash;
                if (newestCrash is not null) RaisePreviousRunDescribed();
            }
        }
        catch (Exception ex) when (!IsFatal(ex)) { Warn("Could not read Windows' crash records for the previous run", ex); }
    }

    private static void RaisePreviousRunDescribed()
    {
        try { PreviousRunDescribed?.Invoke(); }
        catch (Exception ex) when (!IsFatal(ex)) { }
    }

    /// <summary>Finds the Application log's crash entries for one process: Windows Error Reporting's "Application Error"
    /// (faulting module, exception code and offset) and the .NET runtime's own entry (exception and managed stack).</summary>
    private static (string Summary, string Detail)? FindWindowsCrash(int processId, DateTimeOffset started)
    {
        var since = (started == DateTimeOffset.MinValue ? DateTime.UtcNow.AddDays(-30) : started.UtcDateTime.AddSeconds(-2))
            .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var query = new EventLogQuery("Application", PathType.LogName,
            "*[System[((Provider[@Name='Application Error'] and EventID=1000) or (Provider[@Name='.NET Runtime'] and EventID=1026))" +
            $" and TimeCreated[@SystemTime>='{since}']]]");
        string? summary = null, fault = null, runtime = null;
        using (var reader = new EventLogReader(query))
        {
            for (var record = reader.ReadEvent(); record is not null; record = reader.ReadEvent())
                using (record)
                {
                    var data = record.Properties;
                    if (record.Id == 1000 && fault is null && data.Count > 12 && ProcessIdOf(data[8].Value) == processId)
                    {
                        var code = Hex(data[6].Value);
                        var module = Text(data[3].Value);
                        summary = $"{DescribeExceptionCode(code)} (0x{code}) in " +
                            (module.Length == 0 || module.Equals("unknown", StringComparison.OrdinalIgnoreCase) ? "unknown code" : module);
                        fault = $"{Text(data[0].Value)} {Text(data[1].Value)}: {summary} {Text(data[4].Value)} at offset 0x{Hex(data[7].Value)}" +
                            $" (Windows report {Text(data[12].Value)}).";
                    }
                    else if (record.Id == 1026 && runtime is null && record.ProcessId == processId && data.Count > 0)
                        runtime = Text(data[0].Value);
                }
        }
        if (fault is null && runtime is null) return null;
        if (summary is null)
        {
            var exception = runtime!.Split('\n').FirstOrDefault(line => line.StartsWith("Exception Info:", StringComparison.Ordinal));
            summary = exception is null ? "an unhandled exception" : exception["Exception Info:".Length..].Trim();
            if (summary.Length > 200) summary = summary[..200] + "…";
        }
        if (runtime is { Length: > 8000 }) runtime = runtime[..8000] + "…";
        return (summary, fault is null ? runtime! : runtime is null ? fault : fault + Environment.NewLine + runtime);
    }

    private static string DescribeExceptionCode(string code) => code switch
    {
        "c0000005" => "an access violation",
        "c00000fd" => "a stack overflow",
        "c0000409" => "a fail-fast or stack buffer overrun",
        "c0000374" => "heap corruption",
        "e0434352" => "an unhandled .NET exception",
        "80000003" => "a breakpoint",
        _ => "an exception"
    };

    private static string Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? "";

    private static string Hex(object? value)
    {
        var text = value switch
        {
            uint number => number.ToString("x8", CultureInfo.InvariantCulture),
            int number => number.ToString("x8", CultureInfo.InvariantCulture),
            ulong number => number.ToString("x", CultureInfo.InvariantCulture),
            long number => number.ToString("x", CultureInfo.InvariantCulture),
            _ => Text(value).ToLowerInvariant()
        };
        if (text.StartsWith("0x", StringComparison.Ordinal)) text = text[2..];
        // Fault offsets come zero-padded to 16 digits; exception codes keep their 8.
        return text.Length > 8 ? text.TrimStart('0').PadLeft(1, '0') : text;
    }

    private static long ProcessIdOf(object? value) => value switch
    {
        uint number => number,
        int number => number,
        ulong number => (long)number,
        long number => number,
        string text when text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) => hex,
        string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
        _ => -1
    };

    private static bool IsRunning(int pid)
    {
        if (pid == Environment.ProcessId) return true;
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && process.ProcessName.StartsWith("Martlet", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    private static void Write(string level, string message, Exception? exception, string? detail = null)
    {
        var text = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz")).Append(' ')
            .Append(level).Append(" [").Append(Environment.CurrentManagedThreadId).Append("] ")
            .Append(message);
        if (detail is not null) text.Append(": ").Append(detail);
        if (exception is not null) text.AppendLine().Append(exception);
        text.AppendLine();
        Debug.Write(text.ToString());
        var serious = level is "ERROR" or "FATAL";
        lock (gate)
        {
            if (serious)
            {
                var summary = exception is null ? message : $"{message}: {exception.GetType().Name}: {exception.Message}";
                lastError = (DateTimeOffset.Now, summary.ReplaceLineEndings(" "));
                errorCount++;
            }
            if (directory is not null)
            {
                try
                {
                    var path = Path.Combine(directory, component + ".log");
                    if (File.Exists(path) && new FileInfo(path).Length > MaximumFileBytes) Rotate(path);
                    using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    var bytes = Encoding.UTF8.GetBytes(text.ToString());
                    stream.Write(bytes);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
            }
        }
        if (!serious) return;
        try { ErrorRecorded?.Invoke(); }
        catch (Exception ex) when (!IsFatal(ex)) { }
    }

    private static void Rotate(string path)
    {
        var stem = Path.Combine(directory!, component);
        File.Delete($"{stem}.{RetainedFiles}.log");
        for (var index = RetainedFiles - 1; index >= 1; index--)
            if (File.Exists($"{stem}.{index}.log")) File.Move($"{stem}.{index}.log", $"{stem}.{index + 1}.log");
        File.Move(path, $"{stem}.1.log");
    }
}

internal static class TaskLogging
{
    /// <summary>Fire-and-forget that records a failure in the local error log instead of silently dropping it.</summary>
    internal static void Forget(this Task task, [CallerArgumentExpression(nameof(task))] string? operation = null) =>
        ErrorLog.Observe(task, operation ?? "Background operation");

    internal static void Forget(this ValueTask task, [CallerArgumentExpression(nameof(task))] string? operation = null) =>
        ErrorLog.Observe(task.AsTask(), operation ?? "Background operation");
}
