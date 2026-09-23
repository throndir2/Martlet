using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Martlet.Avatar.Hosting;
using Microsoft.Win32.SafeHandles;

namespace Martlet.Desktop;

internal interface IAvatarRenderer : IAsyncDisposable
{
    RendererCapabilities? Capabilities { get; }
    bool HasExited { get; }
    Task Exited { get; }
    Task StartAsync(AvatarProfile profile, string revision, CancellationToken token);
    Task<RendererMessage> SendAsync<T>(string kind, T data, CancellationToken token, TimeSpan? timeout = null);
}

internal sealed class AvatarRendererProcess : IAvatarRenderer
{
    private readonly AnonymousPipeServerStream commands = new(PipeDirection.Out, HandleInheritability.Inheritable);
    private readonly AnonymousPipeServerStream replies = new(PipeDirection.In, HandleInheritability.Inheritable);
    private readonly SemaphoreSlim exchange = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private Process? process;
    private SafeFileHandle? job;
    private bool disposed;
    private Task? disposal;
    private readonly object disposeGate = new();
    internal Guid Activation { get; } = Guid.NewGuid();
    public RendererCapabilities? Capabilities { get; private set; }
    public bool HasExited => process is null || process.HasExited;
    public Task Exited { get; private set; } = Task.CompletedTask;

    public async Task StartAsync(AvatarProfile profile, string revision, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var executable = Path.Combine(AppContext.BaseDirectory, "AvatarRenderer", "Martlet.Avatar.RendererHost.exe");
        LocalAvatarFiles.CheckAncestors(executable);
        if (!File.Exists(executable)) throw new FileNotFoundException("Build the private avatar renderer before activation.");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
        info.Environment.Clear();
        foreach (var name in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP", "LOCALAPPDATA", "DOTNET_ROOT" })
            if (Environment.GetEnvironmentVariable(name) is { } value) info.Environment[name] = value;
        info.ArgumentList.Add("--private-pipes");
        info.ArgumentList.Add(commands.GetClientHandleAsString());
        info.ArgumentList.Add(replies.GetClientHandleAsString());
        job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var limits = new JobLimits { Basic = new() { Flags = 0x2000 } };
        if (!SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<JobLimits>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        process = Process.Start(info) ?? throw new IOException("Renderer process did not start.");
        Exited = process.WaitForExitAsync();
        commands.DisposeLocalCopyOfClientHandle();
        replies.DisposeLocalCopyOfClientHandle();
        if (!AssignProcessToJobObject(job, process.Handle))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        // No browser is initialized until this handshake; the child is already job-owned.
        var response = await SendAsync("load", new RendererLoad(profile, revision), token, TimeSpan.FromSeconds(45));
        if (response.Kind != "capabilities") throw new InvalidDataException("Renderer capability response missing.");
        Capabilities = RendererProtocol.Data<RendererCapabilities>(response);
        if (Capabilities.Parameters.Length > 512 || Capabilities.Parameters.Any(p =>
            string.IsNullOrWhiteSpace(p.Id) || p.Id.Length > 128 || !double.IsFinite(p.Minimum) ||
            !double.IsFinite(p.Maximum) || !double.IsFinite(p.Neutral) || p.Minimum >= p.Maximum ||
            p.Neutral < p.Minimum || p.Neutral > p.Maximum))
            throw new InvalidDataException("Renderer returned unsupported capability metadata.");
    }

    public async Task<RendererMessage> SendAsync<T>(string kind, T data, CancellationToken token,
        TimeSpan? timeout = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        request.CancelAfter(timeout ?? TimeSpan.FromSeconds(2));
        await exchange.WaitAsync(request.Token);
        try
        {
            await RendererProtocol.WriteAsync(commands, RendererProtocol.Message(kind, Activation, data), request.Token);
            var response = await RendererProtocol.ReadAsync(replies, request.Token);
            if (response.Activation != Activation || response.Kind == "error")
                throw new InvalidDataException("Renderer rejected resources/controls or lost its activation binding.");
            return response;
        }
        finally { exchange.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        lock (disposeGate) return new(disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        disposed = true;
        await lifetime.CancelAsync();
        commands.Dispose();
        replies.Dispose();
        if (job is { IsInvalid: false, IsClosed: false })
        {
            if (!TerminateJobObject(job, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var deadline = Stopwatch.StartNew();
            while (true)
            {
                if (!QueryInformationJobObject(job, 1, out var accounting, Marshal.SizeOf<JobAccounting>(), IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (accounting.ActiveProcesses == 0) break;
                if (deadline.Elapsed > TimeSpan.FromSeconds(3)) throw new TimeoutException("Renderer descendants have not exited; activation remains blocked.");
                await Task.Delay(10);
            }
            job.Dispose();
        }
        if (process is not null)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            process.Dispose();
        }
        var cache = Path.Combine(Path.GetTempPath(), "Martlet.Avatar", Activation.ToString("N"));
        if (Directory.Exists(cache))
        {
            LocalAvatarFiles.CheckAncestors(Path.Combine(cache, "_"));
            Directory.Delete(cache, recursive: true);
        }
        lifetime.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        internal long ProcessTime, JobTime;
        internal uint Flags;
        internal UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        internal uint ActiveProcesses;
        internal UIntPtr Affinity;
        internal uint Priority, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct JobLimits
    {
        internal BasicLimits Basic;
        internal IoCounters Io;
        internal UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct JobAccounting
    {
        internal long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime;
        internal uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref JobLimits limits, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int infoClass,
        out JobAccounting accounting, int size, IntPtr returned);
}
