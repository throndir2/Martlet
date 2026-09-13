using Microsoft.Win32;

namespace Martlet.Desktop;

public interface IAudioSessionEvents : IDisposable
{
    event Action<bool>? LockedChanged;
}

public sealed class WindowsAudioSessionEvents : IAudioSessionEvents
{
    public event Action<bool>? LockedChanged;
    public WindowsAudioSessionEvents() => SystemEvents.SessionSwitch += OnSwitch;
    private void OnSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionUnlock)
            LockedChanged?.Invoke(e.Reason == SessionSwitchReason.SessionLock);
    }
    public void Dispose() => SystemEvents.SessionSwitch -= OnSwitch;
}
