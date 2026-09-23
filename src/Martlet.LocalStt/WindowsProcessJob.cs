using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Martlet.LocalStt;

internal sealed class WindowsProcessJob : IDisposable
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private readonly SafeFileHandle handle;
    private int disposed;

    private WindowsProcessJob(SafeFileHandle handle)
    {
        this.handle = handle;
    }

    internal static WindowsProcessJob Create()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        var raw = CreateJobObjectW(IntPtr.Zero, null);
        if (raw == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var handle = new SafeFileHandle(raw, ownsHandle: true);
        try
        {
            var limits = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose
                }
            };
            if (!SetInformationJobObject(
                    handle,
                    JobObjectInformationClass.ExtendedLimitInformation,
                    ref limits,
                    (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return new(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal void Assign(Process process)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        if (!AssignProcessToJobObject(handle, process.Handle))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal bool IsEmpty
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            if (!QueryInformationJobObject(
                    handle,
                    JobObjectInformationClass.BasicAccountingInformation,
                    out JobObjectBasicAccountingInformation information,
                    (uint)Marshal.SizeOf<JobObjectBasicAccountingInformation>(),
                    out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return information.ActiveProcesses == 0;
        }
    }

    internal async ValueTask<bool> TerminateAndWaitAsync(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > LocalSttPackageManifest.ProcessCleanupTimeout)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        if (IsEmpty)
            return true;
        if (!TerminateJobObject(handle, 1))
            return false;
        return await WaitForEmptyAsync(timeout).ConfigureAwait(false);
    }

    internal async ValueTask<bool> WaitForEmptyAsync(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > LocalSttPackageManifest.ProcessCleanupTimeout)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (IsEmpty)
                return true;
            await Task.Delay(TimeSpan.FromMilliseconds(10)).ConfigureAwait(false);
        }
        return IsEmpty;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
            handle.Dispose();
    }

    private enum JobObjectInformationClass
    {
        BasicAccountingInformation = 1,
        ExtendedLimitInformation = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicAccountingInformation
    {
        internal long TotalUserTime;
        internal long TotalKernelTime;
        internal long ThisPeriodTotalUserTime;
        internal long ThisPeriodTotalKernelTime;
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
    private static extern IntPtr CreateJobObjectW(IntPtr jobAttributes, string? name);

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
        IntPtr process);

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
}
