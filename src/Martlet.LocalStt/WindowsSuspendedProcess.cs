using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Martlet.LocalStt;

internal sealed class WindowsSuspendedProcess : IDisposable
{
    internal Process Process { get; }
    internal Stream StandardOutput { get; }
    internal Stream StandardError { get; }
    private readonly SafeFileHandle thread;

    private WindowsSuspendedProcess(Process process, SafeFileHandle thread, Stream stdout, Stream stderr)
    {
        Process = process;
        this.thread = thread;
        StandardOutput = stdout;
        StandardError = stderr;
    }

    internal static WindowsSuspendedProcess Create(ProcessStartInfo info)
    {
        using var stdin = Pipe.Create();
        using var stdout = Pipe.Create();
        using var stderr = Pipe.Create();
        SetNotInherited(stdout.Read);
        SetNotInherited(stderr.Read);
        SetNotInherited(stdin.Write);
        IntPtr attributes = IntPtr.Zero;
        IntPtr handles = IntPtr.Zero;
        IntPtr environment = IntPtr.Zero;
        var initialized = false;
        try
        {
            nuint size = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(checked((int)size));
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            initialized = true;
            handles = Marshal.AllocHGlobal(3 * IntPtr.Size);
            Marshal.WriteIntPtr(handles, 0, stdin.Read.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, IntPtr.Size, stdout.Write.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, 2 * IntPtr.Size, stderr.Write.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attributes, 0, (nuint)0x20002,
                    handles, (nuint)(3 * IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(),
                    Flags = 0x100,
                    StandardInput = stdin.Read.DangerousGetHandle(),
                    StandardOutput = stdout.Write.DangerousGetHandle(),
                    StandardError = stderr.Write.DangerousGetHandle()
                },
                AttributeList = attributes
            };
            var block = string.Join('\0', info.Environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key}={pair.Value}")) + "\0\0";
            environment = Marshal.StringToHGlobalUni(block);
            var command = new StringBuilder(string.Join(' ',
                new[] { info.FileName }.Concat(info.ArgumentList).Select(Quote)));
            // No user code can run until the caller has assigned the suspended root to its Job.
            if (!CreateProcessW(info.FileName, command, IntPtr.Zero, IntPtr.Zero, true,
                    0x4 | 0x400 | 0x80000 | 0x08000000, environment, info.WorkingDirectory,
                    ref startup, out var created))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            using var processHandle = new SafeFileHandle(created.Process, true);
            var thread = new SafeFileHandle(created.Thread, true);
            Process? process = null;
            Stream? output = null;
            Stream? error = null;
            try
            {
                process = Process.GetProcessById(created.ProcessId);
                _ = process.Handle;
                output = new FileStream(stdout.TakeRead(), FileAccess.Read, 4096, isAsync: false);
                error = new FileStream(stderr.TakeRead(), FileAccess.Read, 4096, isAsync: false);
                return new(process, thread, output, error);
            }
            catch
            {
                var terminated = TerminateProcess(processHandle, 1);
                var exited = WaitForSingleObject(processHandle, 5000) == 0;
                output?.Dispose();
                error?.Dispose();
                process?.Dispose();
                thread.Dispose();
                if (!terminated || !exited)
                    throw new SuspendedProcessCleanupException();
                throw;
            }
        }
        finally
        {
            if (initialized)
                DeleteProcThreadAttributeList(attributes);
            if (attributes != IntPtr.Zero)
                Marshal.FreeHGlobal(attributes);
            if (handles != IntPtr.Zero)
                Marshal.FreeHGlobal(handles);
            if (environment != IntPtr.Zero)
                Marshal.FreeHGlobal(environment);
        }
    }

    internal void Resume()
    {
        if (ResumeThread(thread) == uint.MaxValue)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal static string Quote(string argument)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                slashes++;
                continue;
            }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            result.Append(c);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    public void Dispose()
    {
        thread.Dispose();
        StandardOutput.Dispose();
        StandardError.Dispose();
        Process.Dispose();
    }

    private static void SetNotInherited(SafeFileHandle handle)
    {
        if (!SetHandleInformation(handle, 1, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private sealed class Pipe : IDisposable
    {
        internal SafeFileHandle Read { get; private set; }
        internal SafeFileHandle Write { get; }
        private Pipe(SafeFileHandle read, SafeFileHandle write) => (Read, Write) = (read, write);
        internal static Pipe Create()
        {
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Inherit = 1 };
            if (!CreatePipe(out var read, out var write, ref security, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return new(read, write);
        }
        internal SafeFileHandle TakeRead()
        {
            var owned = Read;
            Read = new SafeFileHandle(IntPtr.Zero, true);
            return owned;
        }
        public void Dispose()
        {
            Read.Dispose();
            Write.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { internal int Length; internal IntPtr Descriptor; internal int Inherit; }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        internal int Size;
        internal IntPtr Reserved, Desktop, Title;
        internal int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        internal short ShowWindow, ReservedBytes;
        internal IntPtr ReservedPointer, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { internal StartupInfo StartupInfo; internal IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { internal IntPtr Process, Thread; internal int ProcessId, ThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes security, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr attributes, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr attributes, uint flags, nuint attribute,
        IntPtr value, nuint size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment,
        string directory, ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeFileHandle process, uint exitCode);
    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(SafeFileHandle process, uint milliseconds);
}

internal sealed class SuspendedProcessCleanupException : IOException;
