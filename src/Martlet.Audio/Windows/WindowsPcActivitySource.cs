using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Martlet.Audio.Windows;

/// <summary>What makes sound on this PC, read from Windows for <see cref="PcActivityMonitor"/>: the peak meters of every app's
/// audio sessions on each active output (the volume mixer's meters; never the sound itself), the program behind each session,
/// the titles of those apps' visible windows, the window in front and whether it fills its screen, whether Windows says a game
/// runs in exclusive full screen, and how busy each app keeps the graphics card's 3D engine (Windows' GPU Engine counters).
/// Martlet's own processes are left out. Used from one thread at a time; nothing is recorded, kept or sent.</summary>
public sealed class WindowsPcActivitySource(int? martletProcessId = null) : IPcActivitySource
{
    /// <summary>What the Windows system sounds session (notification sounds) is called.</summary>
    public const string SystemSounds = "Windows sounds";
    private const long SessionsEvery = 2_000, ImagesEvery = 60_000;
    private readonly int martlet = martletProcessId ?? Environment.ProcessId;
    private readonly List<MMDevice> devices = [];
    private readonly List<(AudioSessionControl Session, string App, int Process)> sessions = [];
    private readonly Dictionary<int, string?> images = [];
    private MMDeviceEnumerator? enumerator;
    private GpuCounters? gpu;
    private long sessionsAt = -1, imagesAt;
    private bool gpuFailed, disposed;

    public IReadOnlyList<PcAppLevel> Levels()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var now = Environment.TickCount64;
        if (sessionsAt < 0 || now - sessionsAt >= SessionsEvery) RefreshSessions(now);
        var levels = new List<PcAppLevel>(sessions.Count);
        foreach (var (session, app, _) in sessions) levels.Add(new(app, session.AudioMeterInformation?.MasterPeakValue ?? 0));
        return levels;
    }

    public IReadOnlyList<PcAppFacts> Facts(IReadOnlyCollection<string> apps)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(apps);
        var old = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            var front = GetForegroundWindow();
            GetWindowThreadProcessId(front, out var frontProcess);
            var frontApp = front == 0 || frontProcess == (uint)martlet ? null : Name((int)frontProcess);
            if (frontApp is not null && Own(frontApp)) frontApp = null;
            var filling = frontApp is not null && Fills(front);
            var exclusive = filling && SHQueryUserNotificationState(out var state) == 0 && state == QunsRunningD3dFullScreen;
            var titles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var app in apps) titles.TryAdd(app, []);
            if (frontApp is not null) titles.TryAdd(frontApp, []);
            var paths = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            if (frontApp is not null)
            {
                if (Title(front) is { Length: > 0 } title) titles[frontApp].Add(title);
                paths[frontApp] = Image((int)frontProcess);
            }
            EnumWindows((window, _) =>
            {
                if (window == front || !IsWindowVisible(window) || GetWindow(window, GwOwner) != 0 || Cloaked(window)) return true;
                GetWindowThreadProcessId(window, out var process);
                if (process == (uint)martlet || Name((int)process) is not { } app || !titles.TryGetValue(app, out var list)) return true;
                if (list.Count < 8 && Title(window) is { Length: > 0 } title) list.Add(title);
                if (!paths.ContainsKey(app)) paths[app] = Image((int)process);
                return true;
            }, 0);
            foreach (var (_, app, process) in sessions)
                if (titles.ContainsKey(app) && !paths.ContainsKey(app)) paths[app] = Image(process);
            var load = Gpu();
            return [.. titles.Select(app => new PcAppFacts(app.Key, paths.GetValueOrDefault(app.Key), app.Value,
                Foreground: app.Key.Equals(frontApp, StringComparison.OrdinalIgnoreCase),
                FullScreen: filling && app.Key.Equals(frontApp, StringComparison.OrdinalIgnoreCase),
                Gpu: load is null ? null : load.GetValueOrDefault(app.Key),
                ExclusiveFullScreen: exclusive && app.Key.Equals(frontApp, StringComparison.OrdinalIgnoreCase)))];
        }
        finally { SetThreadDpiAwarenessContext(old); }
    }

    // Every app's audio sessions on every active output, again every couple of seconds (apps start and stop playing).
    private void RefreshSessions(long now)
    {
        DropSessions();
        if (now - imagesAt >= ImagesEvery)
        {
            images.Clear();
            imagesAt = now;
        }
        enumerator ??= new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            devices.Add(device);
            var manager = device.AudioSessionManager;
            manager.RefreshSessions();
            var all = manager.Sessions;
            for (var i = 0; i < all.Count; i++)
            {
                var session = all[i];
                var kept = false;
                try
                {
                    if (session.State == AudioSessionState.AudioSessionStateExpired) continue;
                    if (session.IsSystemSoundsSession)
                    {
                        sessions.Add((session, SystemSounds, 0));
                        kept = true;
                        continue;
                    }
                    var process = (int)session.GetProcessID;
                    if (process == martlet || Name(process) is not { } app || Own(app)) continue;
                    sessions.Add((session, app, process));
                    kept = true;
                }
                finally
                {
                    if (!kept) session.Dispose();
                }
            }
        }
        sessionsAt = now;
    }

    private static bool Own(string app) => app.StartsWith("Martlet", StringComparison.OrdinalIgnoreCase);

    // How busy each app keeps the 3D engine (percent), from the GPU Engine counters; null until there are two readings, or when
    // Windows doesn't have them.
    private Dictionary<string, double>? Gpu()
    {
        if (gpuFailed) return null;
        if (gpu is null)
        {
            gpu = GpuCounters.Open();
            gpuFailed = gpu is null;
            return null;
        }
        if (gpu.Sample() is not { } load) return null;
        var byApp = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (process, percent) in load)
            if (Name(process) is { } app) byApp[app] = byApp.GetValueOrDefault(app) + percent;
        return byApp;
    }

    private string? Name(int process) => Image(process) is { } path ? Path.GetFileNameWithoutExtension(path) : null;

    // The program a process runs, asked with the least access Windows has (it works for most processes without elevation).
    private string? Image(int process)
    {
        if (process <= 0) return null;
        if (images.TryGetValue(process, out var known)) return known;
        if (images.Count > 1024) images.Clear();
        string? path = null;
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, process);
        if (handle != 0)
        {
            try
            {
                var buffer = new StringBuilder(1024);
                var size = buffer.Capacity;
                if (QueryFullProcessImageName(handle, 0, buffer, ref size)) path = buffer.ToString(0, size);
            }
            finally { CloseHandle(handle); }
        }
        return images[process] = path;
    }

    // The window covers its whole monitor (a full-screen game or video, borderless too), not an ordinary maximized window with a
    // title bar (the same rule as screen glances' ActiveApp).
    private static bool Fills(nint window)
    {
        if (window == 0 || window == GetShellWindow() || IsIconic(window)) return false;
        var name = new StringBuilder(32);
        if (GetClassName(window, name, name.Capacity) > 0 && name.ToString() is "Progman" or "WorkerW") return false;
        if (IsZoomed(window) && (GetWindowLongPtrW(window, GwlStyle) & WsCaption) == WsCaption) return false;
        if (!GetWindowRect(window, out var rect)) return false;
        var monitor = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(window, MonitorDefaultToNearest), ref monitor)) return false;
        return monitor.Monitor.Right > monitor.Monitor.Left && rect.Left <= monitor.Monitor.Left && rect.Top <= monitor.Monitor.Top &&
            rect.Right >= monitor.Monitor.Right && rect.Bottom >= monitor.Monitor.Bottom;
    }

    private static bool Cloaked(nint window) => DwmGetWindowAttribute(window, DwmCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static string Title(nint window)
    {
        var length = GetWindowTextLength(window);
        if (length <= 0) return "";
        var buffer = new StringBuilder(Math.Min(length, 512) + 1);
        GetWindowText(window, buffer, buffer.Capacity);
        return new string(buffer.ToString().Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
    }

    private void DropSessions()
    {
        foreach (var (session, _, _) in sessions)
            try { session.Dispose(); }
            catch (COMException) { }
        sessions.Clear();
        foreach (var device in devices)
            try { device.Dispose(); }
            catch (COMException) { }
        devices.Clear();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        DropSessions();
        enumerator?.Dispose();
        enumerator = null;
        gpu?.Dispose();
        gpu = null;
    }

    /// <summary>Windows' GPU Engine performance counters for every process's 3D engines (what Task Manager's GPU column reads).</summary>
    private sealed class GpuCounters : IDisposable
    {
        private const string Path = @"\GPU Engine(*engtype_3D)\Utilization Percentage";
        private const uint FormatDouble = 0x00000200, MoreData = 0x800007D2;
        private nint query, counter;

        internal static GpuCounters? Open()
        {
            if (PdhOpenQuery(null, 0, out var query) != 0) return null;
            if (PdhAddEnglishCounter(query, Path, 0, out var counter) != 0 || PdhCollectQueryData(query) != 0)
            {
                PdhCloseQuery(query);
                return null;
            }
            return new() { query = query, counter = counter };
        }

        // Percent per process since the reading before.
        internal Dictionary<int, double>? Sample()
        {
            if (query == 0 || PdhCollectQueryData(query) != 0) return null;
            uint size = 0, count = 0;
            var status = PdhGetFormattedCounterArray(counter, FormatDouble, ref size, ref count, 0);
            if (status == 0) return [];
            if (status != MoreData || size == 0) return null;
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArray(counter, FormatDouble, ref size, ref count, buffer) != 0) return null;
                var step = Marshal.SizeOf<CounterItem>();
                var load = new Dictionary<int, double>();
                for (var i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<CounterItem>(buffer + i * step);
                    // PDH_CSTATUS_VALID_DATA or PDH_CSTATUS_NEW_DATA.
                    if (item.Value.Status > 1 || Process(Marshal.PtrToStringUni(item.Name)) is not { } process) continue;
                    load[process] = load.GetValueOrDefault(process) + item.Value.Value;
                }
                return load;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        // "pid_1234_luid_0x00000000_0x0000D1D4_phys_0_eng_0_engtype_3D"
        private static int? Process(string? instance)
        {
            if (instance is null || !instance.StartsWith("pid_", StringComparison.OrdinalIgnoreCase)) return null;
            var end = instance.IndexOf('_', 4);
            return end > 4 && int.TryParse(instance.AsSpan(4, end - 4), out var process) ? process : null;
        }

        public void Dispose()
        {
            if (query != 0) PdhCloseQuery(query);
            query = counter = 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CounterValue { public uint Status; public double Value; }

        [StructLayout(LayoutKind.Sequential)]
        private struct CounterItem { public nint Name; public CounterValue Value; }

        [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW", CharSet = CharSet.Unicode)]
        private static extern uint PdhOpenQuery(string? source, nint userData, out nint query);
        [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)]
        private static extern uint PdhAddEnglishCounter(nint query, string path, nint userData, out nint counter);
        [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(nint query);
        [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW", CharSet = CharSet.Unicode)]
        private static extern uint PdhGetFormattedCounterArray(nint counter, uint format, ref uint size, ref uint count, nint items);
        [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(nint query);
    }

    private const uint ProcessQueryLimitedInformation = 0x1000, GwOwner = 4, MonitorDefaultToNearest = 2;
    private const int DwmCloaked = 14, QunsRunningD3dFullScreen = 3, GwlStyle = -16;
    private const long WsCaption = 0x00C00000;
    private static readonly nint PerMonitorAwareV2 = -4;

    private delegate bool EnumWindowsCallback(nint window, nint data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public uint Size; public Rect Monitor, Work; public uint Flags; }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, int process);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowsCallback callback, nint data);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsZoomed(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern long GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int count);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
}
