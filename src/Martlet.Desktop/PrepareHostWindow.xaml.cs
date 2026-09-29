using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Martlet.Desktop;

internal enum PrepareStart { Status, Reboot, Shutdown, Wake }

/// <summary>Prepare this computer: reads what a Linux computer has (martlet-prepare status over SSH), lets the owner tick
/// what to set up, runs exactly that with streamed output, and restarts, shuts down or wakes the computer. Ticking an
/// item and pressing Run selected is the owner's consent for that change; nothing runs on its own.</summary>
public partial class PrepareHostWindow : ThemedWindow
{
    private const int MaximumLog = 400_000;
    private static readonly Regex Escapes =
        new(@"\x1B(?:\[[0-9;?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[78=>]|[()][0-9A-Za-z])", RegexOptions.Compiled);

    // Things Tick what is missing leaves alone: they are preferences, not gaps.
    private static readonly HashSet<string> Preferences = ["virtual-display", "gpu-power"];

    private static readonly Dictionary<string, string> Titles = new(StringComparer.Ordinal)
    {
        ["updates"] = "Install system updates (apt upgrade)",
        ["docker"] = "Docker Engine and Compose",
        ["nvidia-driver"] = "NVIDIA driver (the one Ubuntu recommends)",
        ["nvidia-toolkit"] = "NVIDIA Container Toolkit (GPUs inside Docker)",
        ["headless"] = "Run without a screen: SSH on, never sleep",
        ["virtual-display"] = "Virtual display (screen spoofing)",
        ["gpu-power"] = "GPU persistence mode and power limits",
        ["tools"] = "Developer tools",
        ["wol"] = "Wake-on-LAN (start it from this PC)",
        ["reboot"] = "Restart",
        ["shutdown"] = "Shut down"
    };

    private PairedHost? host;
    private readonly HostPairings? pairings;
    private readonly IHostShell shell;
    private readonly PrepareStart start;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? work;
    private PrepareStatus? status;
    private readonly List<string> pendingReasons = [];
    private string? wakeMac;
    private bool busy, closed;

    private readonly Dictionary<string, CheckBox> items = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> missing = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CheckBox> toolChoices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Slider> powerChoices = new(StringComparer.Ordinal);
    private ComboBox? bootChoice, resolutionChoice;
    private CheckBox? removeDisplay, resetPower;

    internal PrepareHostWindow(PairedHost? host, HostPairings? pairings, PrepareStart start, IHostShell? shell = null)
    {
        InitializeComponent();
        this.host = host;
        this.pairings = pairings;
        this.start = start;
        this.shell = shell ?? HostShells.Current;
        wakeMac = host?.WakeMac;
        HeadingText.Text = host is null ? "Prepare a Linux computer" : $"Prepare {host.HostId}";
        Title = "Martlet - " + HeadingText.Text;
        TargetText.Text = host?.SshTarget ?? (host is null ? "" : "user@" + host.Address);
        RenderChecklist();
        UpdateButtons();
        Loaded += async (_, _) => await BeginAsync();
    }

    private async Task BeginAsync()
    {
        switch (start)
        {
            case PrepareStart.Wake: await WorkAsync(WakeAsync); break;
            case PrepareStart.Reboot: await PowerAsync(reboot: true); break;
            case PrepareStart.Shutdown: await PowerAsync(reboot: false); break;
            default:
                if (host?.SshTarget is not null) await WorkAsync(ReadStatusAsync);
                else StatusText.Text = "Enter how to reach it over SSH as user@computer, then press Read status.";
                break;
        }
    }

    private string Target()
    {
        var target = TargetText.Text.Trim();
        ConsoleSshShell.Validate(target, []);
        if (!target.Contains('@') || target.StartsWith("user@", StringComparison.Ordinal))
            throw new InvalidOperationException("Enter the SSH target as user@computer, with your Linux user name (for example me@192.168.1.20).");
        return target;
    }

    private string? Mac() => HostPower.NormalizeMac(status?.Wol.Mac) ?? wakeMac;

    private async Task WorkAsync(Func<CancellationToken, Task> action)
    {
        if (busy) { StatusText.Text = "Martlet is still working on this computer."; return; }
        busy = true;
        work = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        UpdateButtons();
        try { await action(work.Token); }
        catch (OperationCanceledException) { if (!closed) StatusText.Text = "Stopped."; }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or SocketException or ArgumentException)
        {
            if (!closed) StatusText.Text = error.Message;
        }
        finally
        {
            busy = false;
            work.Dispose();
            work = null;
            if (!closed) UpdateButtons();
        }
    }

    private void UpdateButtons()
    {
        var ready = status is { Supported: true };
        ReadButton.IsEnabled = TargetText.IsEnabled = !busy;
        RunButton.IsEnabled = MissingButton.IsEnabled = Checklist.IsEnabled = !busy && ready;
        RebootButton.IsEnabled = ShutdownButton.IsEnabled = !busy;
        WakeButton.IsEnabled = !busy && Mac() is not null;
        WakeButton.ToolTip = Mac() is { } mac ? $"Sends a Wake-on-LAN packet to {mac}" : "Read its status while it is on so Martlet learns its network card";
        StopButton.IsEnabled = busy;
        if (Reasons().Count > 0) RebootButton.SetResourceReference(StyleProperty, "PrimaryButton");
        else RebootButton.ClearValue(StyleProperty);
    }

    // ---------- output ----------

    private void Append(string chunk)
    {
        if (closed) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.InvokeAsync(() => Append(chunk)); return; }
        var clean = Escapes.Replace(chunk, "").Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        OutputText.AppendText(clean);
        if (OutputText.Text.Length > MaximumLog) OutputText.Text = OutputText.Text[^(MaximumLog / 2)..];
        OutputText.ScrollToEnd();
    }

    /// <summary>Passes output through as it arrives (so password prompts show at once) but drops the machine-readable
    /// status and result lines, which Martlet shows as the checklist and summary instead.</summary>
    private sealed class OutputFilter(Action<string> output)
    {
        private static readonly string[] Hidden = ["{\"martlet_prepare\":", PrepareOutcome.Marker];
        private readonly StringBuilder start = new();
        private readonly object gate = new();
        private bool emitting, hiding;

        internal void Write(string chunk)
        {
            var emit = new StringBuilder();
            lock (gate)
            {
                foreach (var c in chunk)
                {
                    if (hiding) { hiding = c != '\n'; continue; }
                    if (emitting) { emit.Append(c); emitting = c != '\n'; continue; }
                    start.Append(c);
                    var text = start.ToString();
                    if (Hidden.Any(h => text.StartsWith(h, StringComparison.Ordinal)))
                    {
                        start.Clear();
                        hiding = c != '\n';
                        continue;
                    }
                    if (c != '\n' && Hidden.Any(h => h.StartsWith(text, StringComparison.Ordinal))) continue;
                    emit.Append(text);
                    start.Clear();
                    emitting = c != '\n';
                }
            }
            if (emit.Length > 0) output(emit.ToString());
        }

        internal void Flush()
        {
            string rest;
            lock (gate) { rest = start.ToString(); start.Clear(); }
            if (rest.Length > 0) output(rest);
        }
    }

    private async Task<ShellRun> RunScriptAsync(string target, IReadOnlyList<string> arguments, bool sudo, CancellationToken token)
    {
        Append($"\n> martlet-prepare {string.Join(' ', arguments)}   ({target})\n");
        var filter = new OutputFilter(Append);
        var run = await shell.RunAsync(target, PrepareScript.Text, arguments, sudo, filter.Write, token);
        filter.Flush();
        return run;
    }

    // ---------- status ----------

    private async Task ReadStatusAsync(CancellationToken token)
    {
        var target = Target();
        StatusText.Text = $"Reading {target}...";
        var run = await RunScriptAsync(target, PrepareScript.Status, sudo: false, token);
        var found = PrepareStatus.Find(run.Output) ?? throw new InvalidOperationException(run.ExitCode == 255
            ? $"Could not sign in to {target} over SSH. Check the user and address and that its SSH server is on (see Output)."
            : $"{target} did not report its status (exit {run.ExitCode}); see Output.");
        await ShowStatusAsync(found, token);
        StatusText.Text = found.Supported
            ? $"Read {found.Hostname ?? target}. Tick what to set up (or Tick what is missing), then press Run selected."
            : found.Reason ?? "This computer is not supported.";
    }

    private async Task ShowStatusAsync(PrepareStatus found, CancellationToken token)
    {
        status = found;
        await RememberMacAsync(found.Wol.Mac, token);
        RenderChecklist();
        UpdateButtons();
    }

    private async Task RememberMacAsync(string? mac, CancellationToken token)
    {
        if (HostPower.NormalizeMac(mac) is not { } normalized) return;
        wakeMac = normalized;
        if (host is null || pairings is null || host.WakeMac == normalized) return;
        try { host = await pairings.SaveMacAsync(host.HostId, normalized, token) ?? host; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            Append($"(Could not save its MAC address for Wake it up: {error.Message})\n");
        }
    }

    private List<string> Reasons() => (status?.RebootReasons ?? []).Concat(pendingReasons).Distinct(StringComparer.Ordinal).ToList();

    // ---------- run ----------

    private async void Run_Click(object sender, RoutedEventArgs e) => await WorkAsync(async token =>
    {
        var request = Request();
        var arguments = PrepareScript.Arguments(request);
        var target = Target();
        var list = string.Join("\n", request.Items.Select(i => "\u2022 " + Describe(i, request)));
        if (!ConfirmationDialog.Confirm(this,
                $"Run these on {target} with administrator rights (sudo)?\n\n{list}\n\nInstalls can take several minutes; keep this window open " +
                "until it finishes.", "Prepare this computer"))
        {
            StatusText.Text = "Nothing was changed.";
            return;
        }
        await ApplyAsync(target, arguments, token);
    });

    private static string Describe(string item, PrepareRequest request) => item switch
    {
        "headless" when request.Boot is { } boot => Titles[item] + (boot == "text" ? "; start in text mode" : "; start the desktop"),
        "virtual-display" => request.RemoveVirtualDisplay ? "Remove the virtual display" : $"{Titles[item]}: {request.Resolution}",
        "gpu-power" => request.ResetPower ? "Return every GPU to its default power limit"
            : Titles[item] + (request.PowerLimits.Count == 0 ? "" : ": " + string.Join(", ", request.PowerLimits.Values.Select(w => $"{w} W"))),
        "tools" => $"{Titles[item]}: {string.Join(", ", request.Tools.Select(PrepareTools.Label))}",
        _ => Titles[item]
    };

    private async Task<PrepareOutcome?> ApplyAsync(string target, IReadOnlyList<string> arguments, CancellationToken token)
    {
        StatusText.Text = $"Working on {target}. Output streams on the right.";
        var run = await RunScriptAsync(target, arguments, sudo: true, token);
        var outcome = PrepareOutcome.Find(run.Output);
        if (outcome is not null)
        {
            pendingReasons.Clear();
            pendingReasons.AddRange(outcome.RebootReasons);
            if (outcome.WolMac is { } mac) await RememberMacAsync(mac, token);
        }
        if (PrepareStatus.Find(run.Output) is { } after) await ShowStatusAsync(after, token);
        else { RenderBanner(); UpdateButtons(); }
        if (outcome is null)
        {
            StatusText.Text = run.ExitCode == 255 ? $"Could not sign in to {target} over SSH (see Output)."
                : $"The run ended without a result (exit {run.ExitCode}); see Output.";
            return null;
        }
        StatusText.Text = Summary(outcome);
        return outcome;
    }

    private string Summary(PrepareOutcome outcome)
    {
        if (outcome.Error is { } error) return "Nothing was changed: " + error;
        var parts = new List<string>();
        void Count(string state, string label)
        {
            var n = outcome.Items.Count(i => i.State == state);
            if (n > 0) parts.Add($"{n} {label}");
        }
        Count("changed", "done");
        Count("ok", "already in place");
        Count("skipped", "skipped");
        Count("failed", "failed");
        var text = (outcome.Ok ? "Finished: " : "Finished with problems: ") + string.Join(", ", parts) + ".";
        foreach (var failed in outcome.Items.Where(i => i.State == "failed"))
            text += $" {Titles.GetValueOrDefault(failed.Item, failed.Item)}: {failed.Message}";
        if (outcome.RebootRequired) text += " Restart it to finish (Restart it).";
        return text;
    }

    private PrepareRequest Request()
    {
        var chosen = PrepareScript.Items.Where(i => items.TryGetValue(i, out var box) && box.IsChecked == true).ToList();
        var limits = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var gpu in status?.Gpus ?? [])
        {
            if (!powerChoices.TryGetValue(gpu.Uuid, out var slider)) continue;
            var watts = (int)Math.Round(slider.Value);
            if (gpu.Power.Saved is not null || gpu.Power.Current is not { } current || Math.Abs(current - watts) >= 1) limits[gpu.Uuid] = watts;
        }
        return new()
        {
            Items = chosen,
            Boot = (bootChoice?.SelectedItem as ComboBoxItem)?.Tag as string,
            Resolution = (resolutionChoice?.SelectedItem as ComboBoxItem)?.Tag as string ?? "1920x1080",
            RemoveVirtualDisplay = removeDisplay?.IsChecked == true,
            PowerLimits = limits,
            ResetPower = resetPower?.IsChecked == true,
            Tools = PrepareScript.Tools.Where(t => toolChoices.TryGetValue(t, out var box) && box.IsChecked == true).ToArray()
        };
    }

    // ---------- power ----------

    private Task PowerAsync(bool reboot) => WorkAsync(async token =>
    {
        var target = Target();
        if (!ConfirmationDialog.Confirm(this, reboot
                ? $"Restart {target} now? Anything running on it (including Martlet roles) stops until it is back, usually in one to three minutes."
                : $"Shut down {target} now? To start it again from here, Wake-on-LAN must be set up (Wake it up); otherwise press its power button.",
                reboot ? "Restart computer" : "Shut down computer"))
            return;
        var outcome = await ApplyAsync(target, [reboot ? "reboot" : "shutdown"], token);
        if (outcome?.Restarting is null) return;
        pendingReasons.Clear();
        RenderBanner();
        var address = HostPower.HostOf(target);
        StatusText.Text = reboot ? $"{target} is restarting. Waiting for it to go down..." : $"{target} is shutting down...";
        var down = await HostPower.WaitDownAsync(address, TimeSpan.FromMinutes(3), token);
        if (!reboot)
        {
            StatusText.Text = down ? $"{target} is off." + (Mac() is null ? "" : " Press Wake it up to start it again.")
                : $"{target} still answers SSH; it may be finishing work before it powers off.";
            return;
        }
        StatusText.Text = $"Waiting for {target} to come back (up to 10 minutes)...";
        if (!await HostPower.WaitUpAsync(address, TimeSpan.FromMinutes(10), token))
        {
            StatusText.Text = $"{target} has not answered SSH for 10 minutes. Check it has power and network, then press Read status.";
            return;
        }
        await Task.Delay(TimeSpan.FromSeconds(5), token);
        await ReadStatusAsync(token);
        StatusText.Text = "It is back after the restart. " + StatusText.Text;
    });

    private async Task WakeAsync(CancellationToken token)
    {
        var mac = Mac() ?? throw new InvalidOperationException(
            "Martlet does not know this computer's network card yet. While it is on, read its status (and set up Wake-on-LAN).");
        var sent = await HostPower.WakeAsync(mac, token);
        Append($"\n> Wake-on-LAN magic packet for {mac} ({sent} sent)\n");
        string target;
        try { target = Target(); }
        catch (InvalidOperationException) { StatusText.Text = $"Sent a wake-up packet to {mac}."; return; }
        StatusText.Text = $"Sent a wake-up packet to {mac}. Waiting for {target} to answer (up to 5 minutes)...";
        if (!await HostPower.WaitUpAsync(HostPower.HostOf(target), TimeSpan.FromMinutes(5), token))
        {
            StatusText.Text = $"{target} did not answer within 5 minutes. Wake-on-LAN must be allowed in its BIOS/UEFI and on in Linux " +
                "(tick Wake-on-LAN while it is on), and it must be on this network by cable.";
            return;
        }
        await Task.Delay(TimeSpan.FromSeconds(5), token);
        await ReadStatusAsync(token);
    }

    // ---------- checklist ----------

    private static TextBlock Text(string text, bool muted = false, Thickness? margin = null)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = margin ?? new Thickness(0) };
        if (muted) block.SetResourceReference(StyleProperty, "Muted");
        return block;
    }

    private StackPanel Block(string id, string state, bool available, bool ready)
    {
        var box = new CheckBox { Content = Titles[id], FontWeight = FontWeights.SemiBold, IsEnabled = available && status is not null };
        AutomationProperties.SetAutomationId(box, "PrepareItem-" + id);
        items[id] = box;
        missing[id] = available && !ready && !Preferences.Contains(id);
        var mark = new TextBlock
        {
            Text = status is null ? "" : ready ? "\u2713 In place" : available ? "Not yet" : "Not available",
            FontSize = 12, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center
        };
        mark.SetResourceReference(TextBlock.ForegroundProperty, ready ? "SuccessBrush" : available ? "WarningBrush" : "MutedBrush");
        DockPanel.SetDock(mark, Dock.Right);
        var header = new DockPanel();
        header.Children.Add(mark);
        header.Children.Add(box);
        var panel = new StackPanel();
        panel.Children.Add(header);
        panel.Children.Add(Text(state, muted: true, new Thickness(22, 2, 0, 0)));
        var border = new Border { Child = panel, HorizontalAlignment = HorizontalAlignment.Stretch };
        border.SetResourceReference(StyleProperty, "Chip");
        border.Padding = new Thickness(12, 10, 12, 10);
        border.Margin = new Thickness(0, 0, 0, 8);
        Checklist.Children.Add(border);
        return panel;
    }

    private static StackPanel Options(StackPanel block)
    {
        var options = new StackPanel { Margin = new Thickness(22, 8, 0, 0) };
        block.Children.Add(options);
        return options;
    }

    private static ComboBox Choice(string name, IEnumerable<(string Text, string? Tag)> choices, string? selected)
    {
        var combo = new ComboBox { Margin = new Thickness(0, 0, 0, 4), MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(combo, name);
        foreach (var (text, tag) in choices)
        {
            var item = new ComboBoxItem { Content = text, Tag = tag };
            combo.Items.Add(item);
            if (tag == selected) combo.SelectedItem = item;
        }
        if (combo.SelectedItem is null && combo.Items.Count > 0) combo.SelectedIndex = 0;
        return combo;
    }

    private void RenderBanner()
    {
        var reasons = Reasons();
        RebootBanner.Visibility = reasons.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RebootText.Text = $"Restart needed to finish: {string.Join(", ", reasons)}. Press Restart it; Martlet waits until it is back and reads it again.";
    }

    private static string Facts(PrepareStatus s)
    {
        var sudo = s.Sudo switch
        {
            "root" => "signed in as root",
            "passwordless" => "sudo needs no password",
            "password" => "sudo asks for the password",
            _ => "this user may not use sudo, so changes will fail"
        };
        var gpus = s.Gpus.Count switch { 0 => s.Nvidia.Present ? "NVIDIA GPU (driver not working)" : "no NVIDIA GPU", 1 => "1 NVIDIA GPU", var n => $"{n} NVIDIA GPUs" };
        return $"{s.Hostname}: {s.Os.Name}, kernel {s.Os.Kernel}; {gpus}; {s.User}, {sudo}.";
    }

    private void RenderChecklist()
    {
        Checklist.Children.Clear();
        items.Clear();
        missing.Clear();
        toolChoices.Clear();
        powerChoices.Clear();
        bootChoice = resolutionChoice = null;
        removeDisplay = resetPower = null;
        var s = status;
        FactsText.Text = s is null ? "Not read yet." : Facts(s);
        RenderBanner();
        if (s is { Supported: false })
        {
            Checklist.Children.Add(Text(s.Reason ?? "This computer is not supported.", margin: new Thickness(0, 0, 0, 8)));
            return;
        }
        const string pending = "Read its status first.";

        var upgradable = s?.Updates.Upgradable;
        Block("updates", s is null ? pending : upgradable switch
        {
            null => "Unknown.",
            0 => "No updates waiting as of its last package refresh (Run refreshes the lists first).",
            1 => "1 update waiting.",
            var n => $"{n} updates waiting."
        }, true, upgradable == 0);

        var docker = s?.Docker;
        Block("docker", s is null ? pending : !docker!.Installed ? "Not installed." :
            $"Docker {docker.Version}, Compose {docker.Compose ?? "missing"}; {(docker.Running ? "running" : "not running")}, " +
            $"{(docker.Enabled ? "starts at boot" : "not started at boot")}; {s.User} {(docker.UserInGroup ? "is" : "is not")} in the docker group.",
            true, docker is { Installed: true, Running: true, Enabled: true, UserInGroup: true, Compose: not null });

        var nvidia = s?.Nvidia;
        var driverText = s is null ? pending : !nvidia!.Present ? "No NVIDIA graphics card found."
            : nvidia.Working ? $"Driver {nvidia.Driver} ({nvidia.DriverPackage ?? "installed"}) is running, CUDA {nvidia.Cuda ?? "unknown"}. " +
                $"Ubuntu recommends {nvidia.Recommended ?? "(unknown until ubuntu-drivers is installed)"}."
            : nvidia.DriverPackage is { } package ? $"{package} is installed but not running; restart the computer."
            : $"Not installed. Ubuntu recommends {nvidia.Recommended ?? "(found when you run this)"}.";
        if (nvidia?.SecureBoot == true) driverText += " Secure Boot is on; if the driver does not load after a restart, turn Secure Boot off.";
        Block("nvidia-driver", driverText, nvidia?.Present != false,
            nvidia is { Present: true, Working: true } && (nvidia.Recommended is null || nvidia.Recommended == nvidia.DriverPackage) &&
            !Reasons().Contains("NVIDIA driver"));

        Block("nvidia-toolkit", s is null ? pending : !nvidia!.Present ? "Needs an NVIDIA GPU."
            : nvidia.Toolkit is null ? "Not installed. Martlet's GPU roles run in Docker and need it."
            : $"Installed ({nvidia.Toolkit}); Docker {(nvidia.ToolkitConfigured ? "uses it" : "is not set up to use it yet")}.",
            nvidia?.Present != false, nvidia is { Toolkit: not null, ToolkitConfigured: true });

        var headless = s?.Headless;
        var headlessBlock = Block("headless", s is null ? pending :
            $"SSH server {(headless!.SshServer ? headless.SshEnabled ? "on at boot" : "installed but not on at boot" : "not installed")}; " +
            $"sleep and suspend {(headless.SleepMasked ? "off" : "can still happen")}; starts " +
            $"{headless.DefaultTarget switch { "multi-user.target" => "in text mode (no desktop)", "graphical.target" => "the desktop", _ => "(unknown)" }}.",
            true, headless is { SshServer: true, SshEnabled: true, SleepMasked: true });
        var headlessOptions = Options(headlessBlock);
        bootChoice = Choice("How it starts", [("Keep how it starts", null), ("Text mode: no desktop, frees GPU memory", "text"),
            ("Start the desktop (graphical)", "graphical")], null);
        AutomationProperties.SetAutomationId(bootChoice, "PrepareBoot");
        headlessOptions.Children.Add(bootChoice);

        var display = s?.VirtualDisplay;
        var displayText = s is null ? pending : display!.Configured
            ? $"On: {display.Resolution} ({(display.Kind == "nvidia" ? "NVIDIA, reports a connected monitor" : "dummy video driver")})."
            : "None. For a GPU with no monitor attached when you still want a desktop, remote desktop or game streaming.";
        if (headless?.DefaultTarget == "multi-user.target") displayText += " It starts in text mode, so no desktop uses it until it starts the desktop.";
        var displayOptions = Options(Block("virtual-display", displayText, true, display?.Configured == true));
        resolutionChoice = Choice("Virtual display resolution", PrepareScript.Resolutions.Select(r => (r, (string?)r)),
            PrepareScript.Resolutions.Contains(display?.Resolution) ? display!.Resolution : "1920x1080");
        AutomationProperties.SetAutomationId(resolutionChoice, "PrepareResolution");
        removeDisplay = new CheckBox { Content = "Remove the virtual display instead", IsEnabled = display?.Configured == true };
        AutomationProperties.SetAutomationId(removeDisplay, "PrepareRemoveDisplay");
        displayOptions.Children.Add(resolutionChoice);
        displayOptions.Children.Add(removeDisplay);

        var gpus = s?.Gpus ?? [];
        var powerAvailable = nvidia is { Working: true } && gpus.Count > 0;
        var powerBlock = Block("gpu-power", s is null ? pending : !powerAvailable
                ? nvidia!.Present ? "Needs the NVIDIA driver running." : "Needs an NVIDIA GPU."
                : "Persistence mode keeps the driver loaded so GPU jobs start faster. A lower power limit cuts heat, noise and power for a " +
                  "small speed cost. Martlet reapplies both at every boot (martlet-gpu-power.service).",
            powerAvailable, powerAvailable && gpus.All(g => g.Persistence) && s!.GpuPower.Service);
        if (powerAvailable)
        {
            var powerOptions = Options(powerBlock);
            foreach (var gpu in gpus) powerOptions.Children.Add(PowerRow(gpu));
            resetPower = new CheckBox { Content = "Return every GPU to its default limit instead", Margin = new Thickness(0, 4, 0, 0) };
            AutomationProperties.SetAutomationId(resetPower, "PrepareResetPower");
            powerOptions.Children.Add(resetPower);
        }

        var tools = s?.Tools ?? new PrepareTools();
        bool Wanted(string tool) => tool != "cuda" || nvidia?.Present == true;
        var toolsBlock = Block("tools", s is null ? pending : "CUDA, Python, Node.js and handy command-line tools. Tick the ones you want:",
            true, s is not null && PrepareScript.Tools.Where(Wanted).All(t => tools.Of(t) is not null));
        var toolOptions = Options(toolsBlock);
        var toolGrid = new WrapPanel();
        foreach (var tool in PrepareScript.Tools)
        {
            var version = tools.Of(tool);
            var box = new CheckBox
            {
                Content = $"{PrepareTools.Label(tool)} ({(s is null ? "?" : version ?? "not installed")})",
                IsChecked = s is not null && version is null && Wanted(tool), IsEnabled = Wanted(tool) || s is null,
                Margin = new Thickness(0, 0, 16, 4), MinWidth = 250
            };
            if (tool == "cuda" && !Wanted(tool)) box.ToolTip = "Needs an NVIDIA GPU";
            AutomationProperties.SetAutomationId(box, "PrepareTool-" + tool);
            toolChoices[tool] = box;
            toolGrid.Children.Add(box);
        }
        toolOptions.Children.Add(toolGrid);

        var wol = s?.Wol;
        var wolAvailable = wol is { Interface: not null, Wireless: false };
        Block("wol", s is null ? pending : wol!.Interface is null ? "No network card with a default route was found."
            : wol.Wireless ? $"{wol.Interface} is Wi-Fi; Wake-on-LAN needs the computer on a network cable."
            : $"{wol.Interface} ({wol.Mac}): {(wol.Enabled ? "on" : "off")}{(wol.Persistent ? ", kept across restarts" : "")}; " +
              $"the card supports: {wol.Supports ?? "unknown until you run this"}. Its BIOS/UEFI setting must allow waking too.",
            wolAvailable || s is null, wol is { Enabled: true, Persistent: true });
    }

    private StackPanel PowerRow(PrepareGpu gpu)
    {
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        var power = gpu.Power;
        var name = $"GPU {gpu.Index}: {gpu.Name}" + (gpu.MemoryMb is { } mb ? $" ({mb / 1024.0:0.#} GB)" : "") +
            $", persistence {(gpu.Persistence ? "on" : "off")}";
        row.Children.Add(Text(name));
        if (!power.Adjustable)
        {
            row.Children.Add(Text("This GPU does not report an adjustable power limit.", muted: true));
            return row;
        }
        var value = new TextBlock { Width = 380, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        var slider = new Slider
        {
            Minimum = Math.Ceiling(power.Min!.Value), Maximum = Math.Floor(power.Max!.Value), SmallChange = 1, LargeChange = 10,
            TickFrequency = 5, IsSnapToTickEnabled = false, MinWidth = 220, Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        slider.Value = Math.Round(power.Saved ?? power.Current ?? power.Default ?? slider.Maximum);
        AutomationProperties.SetName(slider, $"Power limit for GPU {gpu.Index} in watts");
        AutomationProperties.SetAutomationId(slider, $"PreparePower-{gpu.Index}");
        void Show() => value.Text = $"{Math.Round(slider.Value):0} W   (now {power.Current:0} W, default {power.Default:0} W, " +
            $"allowed {power.Min:0}-{power.Max:0} W{(power.Saved is { } saved ? $", saved {saved:0} W" : "")})";
        slider.ValueChanged += (_, _) => Show();
        Show();
        powerChoices[gpu.Uuid] = slider;
        var line = new DockPanel();
        DockPanel.SetDock(value, Dock.Right);
        line.Children.Add(value);
        line.Children.Add(slider);
        row.Children.Add(line);
        return row;
    }

    // ---------- buttons ----------

    private async void Read_Click(object sender, RoutedEventArgs e) => await WorkAsync(ReadStatusAsync);

    private void Missing_Click(object sender, RoutedEventArgs e)
    {
        foreach (var (id, box) in items) box.IsChecked = box.IsEnabled && missing.GetValueOrDefault(id);
        StatusText.Text = items.Values.Any(b => b.IsChecked == true)
            ? "Ticked what is not set up yet. Review it, then press Run selected."
            : "Everything Martlet checks is already in place.";
    }

    private async void Reboot_Click(object sender, RoutedEventArgs e) => await PowerAsync(reboot: true);
    private async void Shutdown_Click(object sender, RoutedEventArgs e) => await PowerAsync(reboot: false);
    private async void Wake_Click(object sender, RoutedEventArgs e) => await WorkAsync(WakeAsync);

    private void Stop_Click(object sender, RoutedEventArgs e) => work?.Cancel();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (busy && !ConfirmationDialog.Confirm(this,
                "Martlet is still working on this computer. Stop and close? Stopping in the middle of an install can leave it half done; " +
                "running the same item again finishes it.", "Stop and close"))
        {
            e.Cancel = true;
            return;
        }
        closed = true;
        lifetime.Cancel();
    }
}
