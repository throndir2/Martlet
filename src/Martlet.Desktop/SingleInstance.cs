using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Martlet.Desktop;

/// <summary>One Martlet per data folder in this Windows session. Starting it again (the Start menu while it runs in the
/// notification area, a second sign-in entry) shows the running Martlet instead of opening another. Martlet started with a
/// different --data-directory (a disposable verification folder) runs beside it.</summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex mutex;
    private readonly EventWaitHandle show;
    private RegisteredWaitHandle? waiting;

    private SingleInstance(Mutex mutex, EventWaitHandle show)
    {
        this.mutex = mutex;
        this.show = show;
    }

    /// <summary>This process now owns the data folder, or null when another Martlet already runs with it.</summary>
    internal static SingleInstance? TryAcquire(string dataDirectory)
    {
        var key = Key(dataDirectory);
        var mutex = new Mutex(false, @"Local\Martlet.Desktop." + key);
        bool owned;
        try { owned = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned)
        {
            mutex.Dispose();
            return null;
        }
        return new(mutex, new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Martlet.Desktop.Show." + key));
    }

    /// <summary>Asks the Martlet already running with this data folder to show its window, and lets it take the foreground.</summary>
    internal static void ShowRunning(string dataDirectory)
    {
        AllowSetForegroundWindow(ASFW_ANY);
        try
        {
            using var show = EventWaitHandle.OpenExisting(@"Local\Martlet.Desktop.Show." + Key(dataDirectory));
            show.Set();
        }
        catch (Exception error) when (error is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException) { }
    }

    /// <summary>Runs <paramref name="onShow"/> (on a pool thread) whenever another start asks this Martlet to show itself.</summary>
    internal void Listen(Action onShow) =>
        waiting = ThreadPool.RegisterWaitForSingleObject(show, (_, _) => onShow(), null, Timeout.Infinite, executeOnlyOnce: false);

    private static string Key(string dataDirectory)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory)).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..32];
    }

    public void Dispose()
    {
        waiting?.Unregister(null);
        show.Dispose();
        try { mutex.ReleaseMutex(); }
        catch (ApplicationException) { }
        mutex.Dispose();
    }

    private const int ASFW_ANY = -1;
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AllowSetForegroundWindow(int processId);
}
