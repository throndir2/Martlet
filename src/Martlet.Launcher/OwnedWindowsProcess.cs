using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Martlet.Launcher;

internal enum OwnedProcessStartPoint
{
    AfterCreateBeforeAssignment
}

internal sealed class OwnedWindowsProcess : IDisposable
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint JobObjectLimitActiveProcess = 0x00000008;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const uint StillActive = 259;
    private readonly SafeProcessHandle process;
    private readonly SafeFileHandle job;
    private readonly EventWaitHandle waitHandle;
    private readonly RegisteredWaitHandle registeredWait;
    private readonly CancellationTokenSource exited = new();
    private readonly TaskCompletionSource<int> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object lifetime = new();
    private int disposed;

    internal int ProcessId { get; }
    internal CancellationToken ExitToken => exited.Token;

    private OwnedWindowsProcess(SafeProcessHandle process, SafeFileHandle job, int processId)
    {
        this.process = process;
        this.job = job;
        ProcessId = processId;
        waitHandle = new EventWaitHandle(false, EventResetMode.ManualReset);
        waitHandle.SafeWaitHandle = new SafeWaitHandle(
            process.DangerousGetHandle(), ownsHandle: false);
        registeredWait = ThreadPool.RegisterWaitForSingleObject(
            waitHandle,
            static (state, _) => ((OwnedWindowsProcess)state!).ObserveExit(),
            this,
            Timeout.Infinite,
            executeOnlyOnce: true);
    }

    internal static OwnedWindowsProcess Start(
        string executablePath,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        Action<OwnedProcessStartPoint, int>? io = null,
        Action? requireAuthorization = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new LauncherException(LauncherFailure.PlatformNotSupported);
        executablePath = Path.GetFullPath(executablePath);
        workingDirectory = Path.GetFullPath(workingDirectory);
        if (!Path.IsPathFullyQualified(executablePath) ||
            !Directory.Exists(workingDirectory) ||
            !File.Exists(executablePath))
            throw new LauncherException(LauncherFailure.ProcessStartFailed);

        SafeFileHandle? job = null;
        SafeProcessHandle? process = null;
        SafeWaitHandle? thread = null;
        OwnedWindowsProcess? owned = null;
        var environmentBlock = IntPtr.Zero;
        var rawCleanupFailed = false;
        try
        {
            job = CreateJob();
            environmentBlock = Marshal.StringToHGlobalUni(
                BuildEnvironmentBlock(environment));
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
            var commandLine = new StringBuilder(Quote(executablePath));
            requireAuthorization?.Invoke();
            if (!CreateProcessW(
                    executablePath,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    inheritHandles: false,
                    CreateSuspended | CreateUnicodeEnvironment,
                    environmentBlock,
                    workingDirectory,
                    ref startup,
                    out var information))
                throw new LauncherException(LauncherFailure.ProcessStartFailed);
            process = new SafeProcessHandle(information.Process, ownsHandle: true);
            thread = new SafeWaitHandle(information.Thread, ownsHandle: true);
            io?.Invoke(
                OwnedProcessStartPoint.AfterCreateBeforeAssignment,
                checked((int)information.ProcessId));
            if (!SamePath(QueryPath(process), executablePath))
                throw new LauncherException(LauncherFailure.ProcessPathMismatch);
            if (!AssignProcessToJobObject(job, process))
                throw new LauncherException(LauncherFailure.ProcessStartFailed);
            owned = new(process, job, checked((int)information.ProcessId));
            process = null;
            job = null;
            requireAuthorization?.Invoke();
            if (ResumeThread(thread) == uint.MaxValue)
                throw new LauncherException(LauncherFailure.ProcessStartFailed);
            return owned;
        }
        catch (LauncherException)
        {
            owned?.Dispose();
            throw;
        }
        catch (OperationCanceledException)
        {
            owned?.Dispose();
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            ArgumentException or OverflowException)
        {
            owned?.Dispose();
            throw new LauncherException(LauncherFailure.ProcessStartFailed);
        }
        finally
        {
            thread?.Dispose();
            if (process is not null)
                rawCleanupFailed = !TerminateRawProcess(process);
            process?.Dispose();
            job?.Dispose();
            if (environmentBlock != IntPtr.Zero)
                Marshal.FreeHGlobal(environmentBlock);
            if (rawCleanupFailed)
                throw new LauncherException(LauncherFailure.CleanupFailed);
        }
    }

    internal Task<int> WaitForExitAsync(CancellationToken token) =>
        completion.Task.WaitAsync(token);

    internal int? ReadExitCode()
    {
        if (completion.Task.IsCompletedSuccessfully)
            return completion.Task.Result;
        return WaitForSingleObject(process, 0) == 0 && GetExitCodeProcess(process, out var code)
            ? unchecked((int)code)
            : null;
    }

    internal async Task<bool> TerminateAndWaitAsync(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero ||
            timeout > LauncherSupport.ProcessCleanupTimeout)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (IsCompletelyExited())
            return true;
        var requested = TerminateJobObject(job, 1);
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (started.Elapsed < timeout)
        {
            if (IsCompletelyExited())
                return requested;
            await Task.Delay(TimeSpan.FromMilliseconds(10)).ConfigureAwait(false);
        }
        // A missed cleanup budget is a failure, not permission to release verification leases.
        while (!IsCompletelyExited())
        {
            TerminateJobObject(job, 1);
            await Task.Delay(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
        }
        return false;
    }

    private bool IsCompletelyExited() =>
        IsEmpty() && ReadExitCode() is not null;

    private bool IsEmpty()
    {
        if (!QueryInformationJobObject(
                job,
                JobObjectInformationClass.BasicAccountingInformation,
                out JobObjectBasicAccountingInformation information,
                (uint)Marshal.SizeOf<JobObjectBasicAccountingInformation>(),
                out _))
            return false;
        return information.ActiveProcesses == 0;
    }

    private void ObserveExit()
    {
        if (Volatile.Read(ref disposed) != 0)
            return;
        lock (lifetime)
        {
            if (Volatile.Read(ref disposed) != 0)
                return;
            var code = GetExitCodeProcess(process, out var raw)
                ? unchecked((int)raw)
                : -1;
            completion.TrySetResult(code);
            exited.Cancel();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        var clean = TerminateAndWaitAsync(LauncherSupport.ProcessCleanupTimeout).GetAwaiter().GetResult();
        job.Dispose();
        registeredWait.Unregister(null);
        lock (lifetime)
        {
            waitHandle.Dispose();
            process.Dispose();
            exited.Dispose();
        }
        if (!clean)
            throw new LauncherException(LauncherFailure.CleanupFailed);
    }

    private static SafeFileHandle CreateJob()
    {
        var raw = CreateJobObjectW(IntPtr.Zero, null);
        if (raw == IntPtr.Zero)
            throw new LauncherException(LauncherFailure.ProcessStartFailed);
        var handle = new SafeFileHandle(raw, ownsHandle: true);
        try
        {
            var limits = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags =
                        JobObjectLimitKillOnJobClose | JobObjectLimitActiveProcess,
                    ActiveProcessLimit = 32
                }
            };
            if (!SetInformationJobObject(
                    handle,
                    JobObjectInformationClass.ExtendedLimitInformation,
                    ref limits,
                    (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
                throw new LauncherException(LauncherFailure.ProcessStartFailed);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static string BuildEnvironmentBlock(
        IReadOnlyDictionary<string, string> environment)
    {
        if (environment.Count is 0 or > 32)
            throw new LauncherException(LauncherFailure.ProcessStartFailed);
        var entries = new List<string>(environment.Count);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in environment.OrderBy(pair => pair.Key,
                     StringComparer.OrdinalIgnoreCase))
        {
            if (pair.Key is not { Length: > 0 and <= 128 } ||
                pair.Key.Contains('=') || pair.Key.Contains('\0') ||
                pair.Value is null || pair.Value.Contains('\0') ||
                !names.Add(pair.Key))
                throw new LauncherException(LauncherFailure.ProcessStartFailed);
            entries.Add(pair.Key + "=" + pair.Value);
        }
        var block = string.Join('\0', entries) + "\0\0";
        if (block.Length > 32767)
            throw new LauncherException(LauncherFailure.ProcessStartFailed);
        return block;
    }

    private static string QueryPath(SafeProcessHandle process)
    {
        var capacity = 32768;
        var path = new StringBuilder(capacity);
        if (!QueryFullProcessImageNameW(process, 0, path, ref capacity) ||
            capacity == 0)
            throw new LauncherException(LauncherFailure.ProcessPathMismatch);
        return Path.GetFullPath(path.ToString());
    }

    private static bool TerminateRawProcess(SafeProcessHandle process)
    {
        if (WaitForSingleObject(process, 0) == 0)
            return true;
        var terminated = TerminateProcess(process, 1);
        if (WaitForSingleObject(process, 5000) == 0)
            return terminated;
        while (WaitForSingleObject(process, 50) != 0)
            TerminateProcess(process, 1);
        return false;
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    private static string Quote(string value) =>
        "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private enum JobObjectInformationClass
    {
        BasicAccountingInformation = 1,
        ExtendedLimitInformation = 9
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        internal int Size;
        internal string? Reserved;
        internal string? Desktop;
        internal string? Title;
        internal uint X;
        internal uint Y;
        internal uint XSize;
        internal uint YSize;
        internal uint XCountChars;
        internal uint YCountChars;
        internal uint FillAttribute;
        internal uint Flags;
        internal short ShowWindow;
        internal short Reserved2Size;
        internal IntPtr Reserved2;
        internal IntPtr StandardInput;
        internal IntPtr StandardOutput;
        internal IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        internal IntPtr Process;
        internal IntPtr Thread;
        internal uint ProcessId;
        internal uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicAccountingInformation
    {
        internal long TotalUserTime;
        internal long TotalKernelTime;
        internal long ThisPeriodTotalUserTime;
        internal long ThisPeriodKernelTime;
        internal uint TotalPageFaultCount;
        internal uint TotalProcesses;
        internal uint ActiveProcesses;
        internal uint TotalTerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        internal long PerProcessUserTimeLimit;
        internal long PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal UIntPtr MinimumWorkingSetSize;
        internal UIntPtr MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal UIntPtr Affinity;
        internal uint PriorityClass;
        internal uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        internal ulong ReadOperationCount;
        internal ulong WriteOperationCount;
        internal ulong OtherOperationCount;
        internal ulong ReadTransferCount;
        internal ulong WriteTransferCount;
        internal ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        internal JobObjectBasicLimitInformation BasicLimitInformation;
        internal IoCounters IoInfo;
        internal UIntPtr ProcessMemoryLimit;
        internal UIntPtr JobMemoryLimit;
        internal UIntPtr PeakProcessMemoryUsed;
        internal UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(
        IntPtr jobAttributes,
        string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job,
        JobObjectInformationClass informationClass,
        ref JobObjectExtendedLimitInformation information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(
        SafeFileHandle job,
        SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(
        SafeFileHandle job,
        JobObjectInformationClass informationClass,
        out JobObjectBasicAccountingInformation information,
        uint informationLength,
        out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(
        SafeFileHandle job,
        uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeWaitHandle thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(
        SafeProcessHandle process,
        out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(
        SafeProcessHandle process,
        uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(
        SafeProcessHandle handle,
        uint milliseconds);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(
        SafeProcessHandle process,
        uint flags,
        StringBuilder executableName,
        ref int size);
}
