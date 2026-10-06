using System.ComponentModel;
using System.Windows;

namespace Martlet.Desktop;

/// <summary>Keeps this PC awake while its own host service serves your other computers (<see cref="HostingWake"/>,
/// <see cref="PowerRequest"/>), as Martlet on a Mac does while it hosts. Decided again after every network sync (every 20
/// seconds while Martlet runs, its window shown or not) and every read of the host dashboard; the desktop log records each
/// change and Settings › Startup and closing says which applies (<c>StayAwakeStatus</c>).</summary>
public partial class MainWindow
{
    private readonly HostingWake hostingWake = new();
    private readonly PowerRequest awake = new();
    private string? awakeOwnHost;
    private string? awakeProblem;

    private void UpdateStayAwake()
    {
        if (closing) return;
        var local = Role == DeviceRole.Host ? hostState : null;
        var own = ThisPcHost()?.HostId ?? local?.HostId;
        awakeOwnHost = own;
        var served = own is null ? [] : HostingWake.ServedBy(networkViews.GetValueOrDefault(own), networkState.Roster,
            local?.HostId == own ? local : null, IsThisDevice);
        if (hostingWake.Observe(own, served, DateTimeOffset.UtcNow))
        {
            try
            {
                awake.Hold(hostingWake.Reason);
                awakeProblem = null;
                ErrorLog.Info(hostingWake.HostId is { } id
                    ? $"This PC stays awake while its host service {id} serves {HostingWake.Names(hostingWake.Served)}: Windows doesn't " +
                      "put it to sleep when it is left idle (the screen can still turn off)."
                    : "This PC can sleep again when it is left idle: its host service serves none of your other computers now.");
            }
            catch (Win32Exception error)
            {
                awakeProblem = error.Message;
                ErrorLog.Warn("Windows didn't let Martlet keep this PC awake for its host service", error);
            }
        }
        RenderStayAwake();
    }

    private void RenderStayAwake()
    {
        if (awakeOwnHost is null && !RunsOwnHostService)
        {
            StayAwakeText.Visibility = Visibility.Collapsed;
            return;
        }
        StayAwakeText.Text = awakeProblem is { } problem
            ? $"Windows didn't let Martlet keep this PC awake for its host service ({problem}). It can sleep when it is left idle, and " +
              "your other computers then lose its jobs until it wakes."
            : hostingWake.HostId is { } id
                ? $"This PC stays awake while its host service ({id}) serves {HostingWake.Names(hostingWake.Served)}: Windows doesn't put " +
                  "it to sleep when it is left idle. The screen can still turn off, and Sleep and shutting down still work. Martlet keeps " +
                  "it awake only while Martlet runs."
                : "This PC can sleep when it is left idle: its host service serves none of your other computers right now. While it " +
                  "does, Martlet keeps this PC awake.";
        StayAwakeText.Visibility = Visibility.Visible;
    }
}
