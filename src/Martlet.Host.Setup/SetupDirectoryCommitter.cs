using System.Runtime.InteropServices;

namespace Martlet.Host.Setup;

public interface ISetupDirectoryCommitter
{
    void Commit(string absoluteDirectory);
}

public sealed class PlatformSetupDirectoryCommitter : ISetupDirectoryCommitter
{
    private const int LinuxOpenReadOnly = 0;
    private const int LinuxOpenDirectory = 0x10000;
    private const int LinuxOpenCloseOnExec = 0x80000;

    public void Commit(string absoluteDirectory)
    {
        SetupGuard.Text(absoluteDirectory, 1024, SetupFailure.InvalidConfiguration);
        if (!Path.IsPathFullyQualified(absoluteDirectory))
            throw new SetupException(SetupFailure.InvalidConfiguration);
        if (!OperatingSystem.IsLinux())
            throw new SetupException(SetupFailure.InvalidConfiguration);
        CommitLinux(absoluteDirectory);
    }

    private static void CommitLinux(string directory)
    {
        var descriptor = Open(directory, LinuxOpenReadOnly | LinuxOpenDirectory | LinuxOpenCloseOnExec);
        if (descriptor < 0) throw new SetupException(SetupFailure.JournalIoFailure);
        var failure = false;
        try
        {
            failure = Fsync(descriptor) != 0;
        }
        finally
        {
            if (Close(descriptor) != 0) failure = true;
        }
        if (failure) throw new SetupException(SetupFailure.JournalIoFailure);
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int descriptor);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int descriptor);

}
