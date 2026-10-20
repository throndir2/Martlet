using System.Runtime.CompilerServices;

namespace Martlet.Desktop.Tests;

internal static class TestThreadPool
{
    /// <summary>The tests start many windows, controllers and fixture servers in one process, and some of their background work
    /// waits on the thread pool's threads. The pool adds a thread only about twice a second when all are busy, so on a busy PC the
    /// next test's own work (a timer, reading a record) waited tens of seconds for a thread and timed out (#1124). Starting with
    /// more threads lets that work start at once. Martlet itself runs one set of these, so it doesn't need this.</summary>
    [ModuleInitializer]
    internal static void MoreThreads()
    {
        ThreadPool.GetMinThreads(out var workers, out var ports);
        var wanted = Math.Max(64, Environment.ProcessorCount * 8);
        ThreadPool.SetMinThreads(Math.Max(workers, wanted), Math.Max(ports, wanted));
    }
}
