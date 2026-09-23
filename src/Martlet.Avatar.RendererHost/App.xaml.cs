using System.IO.Pipes;
using System.Windows;

namespace Martlet.Avatar.RendererHost;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args is not ["--private-pipes", var input, var output])
        {
            Shutdown(2);
            return;
        }
        MainWindow = new RendererWindow(new AnonymousPipeClientStream(PipeDirection.In, input),
            new AnonymousPipeClientStream(PipeDirection.Out, output));
        MainWindow.Show();
    }
}
