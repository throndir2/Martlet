using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using Martlet.Avatar.Hosting;
using Microsoft.Win32.SafeHandles;

namespace Martlet.Desktop;

internal interface IAvatarRenderer : IAsyncDisposable
{
    RendererCapabilities? Capabilities { get; }
    bool HasExited { get; }
    Task Exited { get; }
    /// <summary>A choice from the overlay's menu for Martlet to carry out (one of <see cref="RendererRequest.Actions"/>),
    /// raised off the UI thread.</summary>
    event Action<string>? Requested;
    Task StartAsync(AvatarProfile profile, string revision, RendererPlacement? placement, CancellationToken token);
    Task<RendererMessage> SendAsync<T>(string kind, T data, CancellationToken token, TimeSpan? timeout = null);
    /// <summary>The renderer's process while it runs (its windows are the character's own on screen), or null.</summary>
    int? ProcessId => null;
}

internal sealed class AvatarRendererProcess : IAvatarRenderer
{
    private readonly AnonymousPipeServerStream commands = new(PipeDirection.Out, HandleInheritability.Inheritable);
    private readonly AnonymousPipeServerStream replies = new(PipeDirection.In, HandleInheritability.Inheritable);
    // Unprompted menu choices from the overlay, kept apart from command replies so they never interleave.
    private readonly AnonymousPipeServerStream requests = new(PipeDirection.In, HandleInheritability.Inheritable);
    private readonly SemaphoreSlim exchange = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private Process? process;
    private SafeFileHandle? job;
    private bool disposed;
    private Task? disposal;
    private readonly object disposeGate = new();
    internal Guid Activation { get; } = Guid.NewGuid();
    public RendererCapabilities? Capabilities { get; private set; }
    public bool HasExited
    {
        get
        {
            if (disposed || process is null) return true;
            try { return process.HasExited; }
            catch (InvalidOperationException) { return true; }
        }
    }
    public Task Exited { get; private set; } = Task.CompletedTask;
    public event Action<string>? Requested;

    public int? ProcessId
    {
        get
        {
            try { return disposed || process is not { HasExited: false } running ? null : running.Id; }
            catch (InvalidOperationException) { return null; }
        }
    }

    public async Task StartAsync(AvatarProfile profile, string revision, RendererPlacement? placement, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var executable = Path.Combine(AppContext.BaseDirectory, "AvatarRenderer", "Martlet.Avatar.RendererHost.exe");
        LocalAvatarFiles.CheckAncestors(executable);
        if (!File.Exists(executable)) throw new FileNotFoundException("The character renderer is missing. Reinstall Martlet or choose another character.");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
        info.Environment.Clear();
        foreach (var name in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP", "LOCALAPPDATA", "DOTNET_ROOT" })
            if (Environment.GetEnvironmentVariable(name) is { } value) info.Environment[name] = value;
        if (ErrorLog.Directory is { } logs) info.Environment[ErrorLog.DirectoryEnvironmentVariable] = logs;
        info.ArgumentList.Add("--private-pipes");
        info.ArgumentList.Add(commands.GetClientHandleAsString());
        info.ArgumentList.Add(replies.GetClientHandleAsString());
        info.ArgumentList.Add(requests.GetClientHandleAsString());
        job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var limits = new JobLimits { Basic = new() { Flags = 0x2000 } };
        if (!SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<JobLimits>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        process = Process.Start(info) ?? throw new IOException("The character renderer didn't start.");
        Exited = process.WaitForExitAsync();
        var started = process;
        _ = Exited.ContinueWith(_ =>
        {
            if (disposed) ErrorLog.Info("Avatar renderer stopped by Martlet.");
            else ErrorLog.Error($"Avatar renderer exited unexpectedly with code {SafeExitCode(started)}. See avatar-renderer.log.");
        }, TaskScheduler.Default);
        commands.DisposeLocalCopyOfClientHandle();
        replies.DisposeLocalCopyOfClientHandle();
        requests.DisposeLocalCopyOfClientHandle();
        if (!AssignProcessToJobObject(job, process.Handle))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        // No browser is initialized until this handshake; the child is already job-owned.
        try
        {
            var response = await SendAsync("load", new RendererLoad(profile, revision,
                Application.Current is App app && app.SelectedTheme.IsDark(), placement is { Locked: true, IsValid: true } ? placement : null,
                (Application.Current as App)?.ThemeColors),
                token, TimeSpan.FromSeconds(45));
            if (response.Kind != "capabilities") throw new InvalidDataException("The character renderer didn't report its controls.");
            Capabilities = RendererProtocol.Data<RendererCapabilities>(response);
        }
        catch (Exception error) when (error is InvalidDataException or JsonException)
        {
            throw new InvalidOperationException("The character renderer couldn't load this model. Details are in the avatar-renderer log.", error);
        }
        if (Capabilities.Parameters.Length > 512 || Capabilities.Parameters.Any(p =>
            string.IsNullOrWhiteSpace(p.Id) || p.Id.Length > 128 || !double.IsFinite(p.Minimum) ||
            !double.IsFinite(p.Maximum) || !double.IsFinite(p.Neutral) || p.Minimum >= p.Maximum ||
            p.Neutral < p.Minimum || p.Neutral > p.Maximum))
            throw new InvalidOperationException("The character renderer reported unsupported model controls.");
        if (Capabilities.Model is { } model && (model.Textures is < 1 or > 16 || model.TextureDivisor is not (1 or 2 or 4) ||
            model.Expressions is < 0 or > 128 || model.MotionGroups.Length > 96 || model.EyeBlink.Length > 64 || model.LipSync.Length > 64 ||
            model.EyeBlink.Concat(model.LipSync).Concat(model.MotionGroups).Any(name => name.Length is 0 or > 256 || name.Any(char.IsControl))))
            throw new InvalidOperationException("The character renderer reported an unsupported model summary.");
        ErrorLog.Info($"Character model loaded: {Describe(Capabilities)}");
        _ = Task.Run(RelayRequestsAsync);
    }

    /// <summary>What the loaded model drives, in words: controls, textures (and any downscaling), blinking, mouth,
    /// motions, expressions and physics. Parameter IDs and motion group names only, never paths.</summary>
    internal static string Describe(RendererCapabilities capabilities)
    {
        var controls = $"{capabilities.Parameters.Length} controls";
        if (capabilities.Model is not { } model) return controls + ".";
        static string List(string[] names) => string.Join(", ", names.Take(4)) + (names.Length > 4 ? $" and {names.Length - 4} more" : "");
        var parts = new List<string>
        {
            controls,
            (model.Textures == 1 ? "1 texture" : $"{model.Textures} textures") +
                (model.TextureDivisor > 1 ? $" shown at 1/{model.TextureDivisor} size to fit the graphics budget" : ""),
            model.EyeBlink.Length > 0 ? $"blinks with {List(model.EyeBlink)}" : "doesn't blink",
            model.LipSync.Length > 0 ? $"mouth moves {List(model.LipSync)}" : "no mouth control for lip-sync",
            model.MotionGroups.Length > 0 ? $"motions {List(model.MotionGroups)}" : "no idle motions",
            model.Expressions == 1 ? "1 expression" : $"{model.Expressions} expressions",
            model.Physics ? "physics on" : "no physics"
        };
        return string.Join("; ", parts) + ".";
    }

    /// <summary>Raises <see cref="Requested"/> for each menu choice the overlay sends until it closes. Anything but a known
    /// choice for this activation ends the relay: the overlay keeps drawing, its menu just no longer reaches Martlet.</summary>
    private async Task RelayRequestsAsync()
    {
        try
        {
            while (!disposed)
            {
                var message = await RendererProtocol.ReadAsync(requests, CancellationToken.None);
                var action = message.Activation == Activation && message.Kind == "request"
                    ? RendererProtocol.Data<RendererRequest>(message).Action : null;
                if (action is null || !RendererRequest.Actions.Contains(action))
                {
                    ErrorLog.Warn("The character renderer sent an unexpected request; its menu no longer reaches Martlet.");
                    return;
                }
                if (!disposed) Requested?.Invoke(action);
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidDataException or JsonException) { }
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
            if (response.Activation == Activation && response.Kind == "error" && response.Data.ValueKind == JsonValueKind.Object &&
                response.Data.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String &&
                code.GetString() == "avatar.model_rejected" && response.Data.TryGetProperty("message", out var reason) &&
                reason.ValueKind == JsonValueKind.String && reason.GetString() is { Length: > 0 and <= 400 } rejected)
                throw new InvalidOperationException(rejected);
            if (response.Activation != Activation || response.Kind == "error")
                throw new InvalidDataException("The character renderer couldn't apply those controls.");
            return response;
        }
        finally { exchange.Release(); }
    }

    private static string SafeExitCode(Process process)
    {
        try { return $"0x{process.ExitCode:X8}"; }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException) { return "unknown"; }
    }

    /// <summary>Ends the renderer and its descendants. A failed attempt is not final: the next call tries again.</summary>
    public ValueTask DisposeAsync()
    {
        lock (disposeGate)
        {
            if (disposal is null || disposal.IsFaulted || disposal.IsCanceled) disposal = DisposeCoreAsync();
            return new(disposal);
        }
    }

    private async Task DisposeCoreAsync()
    {
        disposed = true;
        if (!lifetime.IsCancellationRequested) await lifetime.CancelAsync();
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
                if (deadline.Elapsed > TimeSpan.FromSeconds(3)) throw new TimeoutException("The character renderer is still closing. Try again in a moment.");
                await Task.Delay(10);
            }
            job.Dispose();
        }
        if (process is { } running)
        {
            try { if (!running.HasExited) running.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await running.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            running.Dispose();
            process = null;
        }
        // Only now: the relay's read is synchronous on this pipe and returns once the renderer (its writer) has ended.
        requests.Dispose();
        await DeleteCacheAsync(Path.Combine(Path.GetTempPath(), "Martlet.Avatar", Activation.ToString("N")));
        lifetime.Dispose();
    }

    /// <summary>
    /// Removes the activation's WebView2 cache. Windows (or an antivirus scan) can hold its files for a moment after the
    /// renderer has ended, so this retries in the background instead of failing the stop: a leftover temporary cache
    /// must never keep the character, an update or exiting Martlet stuck.
    /// </summary>
    private static async Task DeleteCacheAsync(string cache)
    {
        if (await TryDeleteCacheAsync(cache, TimeSpan.FromSeconds(1), log: false)) return;
        _ = Task.Run(() => TryDeleteCacheAsync(cache, TimeSpan.FromSeconds(60), log: true));
    }

    private static async Task<bool> TryDeleteCacheAsync(string cache, TimeSpan patience, bool log)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                if (!Directory.Exists(cache)) return true;
                LocalAvatarFiles.CheckAncestors(Path.Combine(cache, "_"));
                Directory.Delete(cache, recursive: true);
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                Martlet.Core.Contracts.ContractException)
            {
                if (error is Martlet.Core.Contracts.ContractException || deadline.Elapsed >= patience)
                {
                    if (log) ErrorLog.Warn("The character renderer's temporary WebView2 cache could not be removed; it is left in the temp folder.", error);
                    return false;
                }
            }
            await Task.Delay(100);
        }
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
