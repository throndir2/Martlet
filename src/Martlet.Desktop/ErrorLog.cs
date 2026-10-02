using System.Diagnostics;
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

    internal static string? Directory => directory;
    /// <summary>ERROR and FATAL entries written since this process started.</summary>
    internal static int ErrorCount { get { lock (gate) return errorCount; } }
    /// <summary>The latest ERROR or FATAL entry's time and one-line message (the exception's type and message, no stack).</summary>
    internal static (DateTimeOffset At, string Message)? LastError { get { lock (gate) return lastError; } }
    /// <summary>Raised on the writing thread after an ERROR or FATAL entry.</summary>
    internal static event Action? ErrorRecorded;
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
        return uncleanPreviousExit;
    }

    /// <summary>Installs the WPF UI-thread handler: log, tell the user once, and keep running when possible.</summary>
    internal static void AttachDispatcher(Application application, string productName, bool showDialog = true)
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
                var choice = MessageBox.Show(
                    $"{productName} recovered from an unexpected error. The last action may not have finished.\n\n" +
                    $"Error: {e.Exception.Message}\n\n" +
                    $"Details were saved locally to:\n{CurrentFile ?? "(log unavailable)"}\n\n" +
                    "Open the logs folder now?",
                    $"{productName} - Unexpected error", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (choice == MessageBoxResult.Yes) OpenFolder();
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

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException or BadImageFormatException;

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
