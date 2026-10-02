using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace Martlet.Desktop;

/// <summary>Asks the owner of this PC whether another computer that found it on the network may use its hosts. It shows the
/// check number the asking computer shows too; Deny is the default, closing denies and the request expires after
/// <see cref="Nearby.DecisionTimeout"/>, or as soon as the asking computer gives up.</summary>
public partial class JoinRequestWindow : ThemedWindow
{
    private readonly TaskCompletionSource<bool> decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime expires;

    internal JoinRequestWindow(string name, string from, string number, IReadOnlyList<string> hosts)
    {
        InitializeComponent();
        Title = $"Martlet - {name} wants to use your hosts";
        HeadingText.Text = $"{name} wants to use this PC's hosts";
        DetailText.Text = $"Martlet on {name} ({from}) found this PC on your network and asks to pair with " +
            $"{string.Join(", ", hosts)}.";
        NumberHelpText.Text = $"Allow only if {name} shows this check number:";
        NumberText.Text = number;
        timer.Tick += (_, _) => Tick();
    }

    /// <summary>Shows the request and completes with the owner's choice; false when denied, closed, expired or when
    /// <paramref name="withdrawn"/> completes first (the asking computer hung up).</summary>
    internal Task<bool> AskAsync(Task withdrawn, TimeSpan timeout)
    {
        expires = DateTime.UtcNow + timeout;
        Tick();
        timer.Start();
        Show();
        Activate();
        withdrawn.ContinueWith(_ => Dispatcher.InvokeAsync(() => Finish(false)), TaskScheduler.Default);
        return decision.Task;
    }

    private void Tick()
    {
        var left = expires - DateTime.UtcNow;
        if (left <= TimeSpan.Zero) { Finish(false); return; }
        ExpiryText.Text = $"Expires in {(int)left.TotalMinutes}:{left.Seconds:00}";
    }

    private void Finish(bool allowed)
    {
        if (!decision.TrySetResult(allowed)) return;
        timer.Stop();
        if (IsVisible) Close();
    }

    private void Allow_Click(object sender, RoutedEventArgs e) => Finish(true);
    private void Deny_Click(object sender, RoutedEventArgs e) => Finish(false);

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        timer.Stop();
        decision.TrySetResult(false);
    }
}
