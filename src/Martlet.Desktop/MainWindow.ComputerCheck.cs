using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Planning;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Diagnostics › Check your computers (<see cref="ComputerCheck"/>) and Home's warning when Thinking got slow
/// (<see cref="ThinkingSpeed"/>): the newest replies against the usual time, and a host's model that isn't all on its graphics card.</summary>
public partial class MainWindow
{
    private bool computerCheckBusy;
    private (DateTime Speeds, DateTime Memory, MeasuredFirstWords SpeedFile, MeasuredModelMemory MemoryFile)? speedFiles;

    /// <summary>Home's warning for the Thinking route <paramref name="llm"/>, or null when it isn't slow. Reads model-speed.json and
    /// model-memory.json again only when they changed.</summary>
    private (string Title, string Detail)? ThinkingSlowNow(SetupRoute llm)
    {
        if (store?.DataDirectory is not { } directory) return null;
        var (speeds, memory) = SpeedFiles(directory);
        var where = llm.Gateway?.HostId ?? (MeasuredFirstWords.HostKey(llm.Origin) is { } key && key.Contains("127.0.0.1", StringComparison.Ordinal)
            ? "this PC" : llm.Origin);
        var placed = llm.Gateway is null ? null : ThinkingSpeed.Placed(memory, llm.Origin, llm.ModelId);
        var off = placed is not null && ThinkingSpeed.Placement(placed.Bytes, placed.GraphicsBytes) != ModelPlacement.GraphicsCard
            ? ThinkingSpeed.DescribePlacement(placed.Model, where, placed.Bytes, placed.GraphicsBytes) : null;
        var slow = ThinkingSpeed.Slowdown(speeds.Find(llm.Origin, llm.ModelId))?.Describe(where);
        if (slow is null && off is null) return null;
        return (slow is not null && off is null && ThinkingSpeed.Slowdown(speeds.Find(llm.Origin, llm.ModelId))!.SlowerThanUsual
                ? "Martlet replies much slower than usual" : "Martlet replies slowly",
            string.Join(" ", new[] { slow, off }.Where(t => t is not null)) + " Check your computers finds what makes it slow.");
    }

    private (MeasuredFirstWords, MeasuredModelMemory) SpeedFiles(string directory)
    {
        DateTime Written(string file)
        {
            try { return File.GetLastWriteTimeUtc(Path.Combine(directory, file)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return default; }
        }
        var speeds = Written(MeasuredFirstWords.FileName);
        var memory = Written(MeasuredModelMemory.FileName);
        if (speedFiles is not { } kept || kept.Speeds != speeds || kept.Memory != memory)
            speedFiles = kept = (speeds, memory, MeasuredFirstWords.Load(directory), MeasuredModelMemory.Load(directory));
        return (kept.SpeedFile, kept.MemoryFile);
    }

    /// <summary>Home's Check your computers fix: Diagnostics, with the check started.</summary>
    private void OpenComputerCheck()
    {
        Navigate(NavDiagnostics);
        CheckComputersAsync().Forget();
    }

    private void ComputerCheck_Click(object sender, RoutedEventArgs e) => CheckComputersAsync().Forget();

    /// <summary>Checks every paired host of the owner's own (never a friend's) one after another, showing each finding as it comes.</summary>
    private async Task CheckComputersAsync()
    {
        if (computerCheckBusy || closing || store?.DataDirectory is not { } directory) return;
        var hosts = WorkSharingRoster.Shared.Hosts(directory).Where(h => !h.Shared).ToArray();
        ComputerCheckResults.Children.Clear();
        if (hosts.Length == 0)
        {
            ComputerCheckStatus.Text = "You have no other computers to check; add one in Devices › Add a computer.";
            return;
        }
        computerCheckBusy = true;
        ComputerCheckButton.IsEnabled = false;
        ComputerCheckStatus.Text = $"Checking {hosts.Length} computer{(hosts.Length == 1 ? "" : "s")}...";
        ErrorLog.Info($"Check your computers: started for {string.Join(", ", hosts.Select(h => h.HostId))}.");
        var problems = 0;
        var shown = 0;
        try
        {
            foreach (var host in hosts)
            {
                var lines = await Task.Run(() => ComputerCheck.RunAsync(host, directory,
                    line => Dispatcher.BeginInvoke(() => ShowComputerCheckLine(line, shown++)), lifetime.Token));
                problems += lines.Count(l => l.Problem);
                if (closing) return;
            }
            ComputerCheckStatus.Text = problems == 0
                ? $"Checked at {DateTime.Now:t}: your computers answer and their models are fast."
                : $"Checked at {DateTime.Now:t}: {problems} problem{(problems == 1 ? "" : "s")} found. Each one is also in the log.";
            ErrorLog.Info($"Check your computers: done, {problems} problem{(problems == 1 ? "" : "s")}.");
        }
        catch (OperationCanceledException) { }
        finally
        {
            computerCheckBusy = false;
            if (!closing) ComputerCheckButton.IsEnabled = true;
        }
        if (!closing) RenderHealth();
    }

    private void ShowComputerCheckLine(ComputerCheckLine line, int index)
    {
        if (closing) return;
        var text = new TextBlock { Text = (line.Problem ? "⚠ " : "✓ ") + line.Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        if (!line.Problem) text.SetResourceReference(StyleProperty, "Muted");
        else text.FontWeight = FontWeights.SemiBold;
        AutomationProperties.SetAutomationId(text, $"ComputerCheckLine-{index}");
        AutomationProperties.SetName(text, (line.Problem ? "Problem: " : "Fine: ") + line.Text);
        ComputerCheckResults.Children.Add(text);
    }
}
