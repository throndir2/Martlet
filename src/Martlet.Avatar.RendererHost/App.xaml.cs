using System.IO.Pipes;
using System.Windows;
using Martlet.Avatar.RendererHost.Logging;

namespace Martlet.Avatar.RendererHost;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ErrorLog.Initialize(Environment.GetEnvironmentVariable(ErrorLog.DirectoryEnvironmentVariable) is { Length: > 0 } logs
            ? logs : ErrorLog.DefaultDirectory(null), "avatar-renderer");
        // Desktop owns the user-facing error experience; the renderer logs and keeps drawing.
        ErrorLog.AttachDispatcher(this, "Martlet avatar", showDialog: false);
        if (e.Args is not ["--private-pipes", var input, var output, var requests, .. var rest] || rest is not ([] or ["--still"]))
        {
            ErrorLog.Error("Renderer started without the private pipe arguments.");
            Shutdown(2);
            return;
        }
        // --still: Martlet's touch zones picture, drawn by a renderer that never shows on screen and never animates.
        MainWindow = new RendererWindow(new AnonymousPipeClientStream(PipeDirection.In, input),
            new AnonymousPipeClientStream(PipeDirection.Out, output), new AnonymousPipeClientStream(PipeDirection.Out, requests),
            still: rest is ["--still"]);
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ErrorLog.MarkCleanExit();
        base.OnExit(e);
    }
}
