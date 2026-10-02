using System.Diagnostics;
using System.Text;

namespace Martlet.Core.Installation;

/// <summary>A Windows optional feature as Windows reports it to a standard user (Win32_OptionalFeature).</summary>
public enum WindowsFeatureState { Unknown, Enabled, Disabled, Absent }

/// <summary>What Docker Desktop's WSL 2 engine needs from Windows, read without administrator rights
/// (<see cref="ProbeAsync"/>): virtualization turned on in the firmware (UEFI/BIOS), the Virtual Machine Platform and
/// Windows Subsystem for Linux features, the Windows hypervisor running and WSL <see cref="MinimumWsl"/> or later.
/// Null and <see cref="WindowsFeatureState.Unknown"/> mean Windows did not say; they never count as a problem.</summary>
public sealed record WindowsVirtualization(bool? Firmware, bool? Hypervisor, WindowsFeatureState MachinePlatform,
    WindowsFeatureState Subsystem, string? Wsl, bool VirtualMachine)
{
    /// <summary>The oldest WSL Docker Desktop supports.</summary>
    public static readonly Version MinimumWsl = new(2, 1, 5);

    /// <summary><see cref="Wsl"/> when wsl.exe is missing or does not report a version (no current WSL installed).</summary>
    public const string NoWsl = "none";

    public static WindowsVirtualization Unknown { get; } =
        new(null, null, WindowsFeatureState.Unknown, WindowsFeatureState.Unknown, null, false);

    /// <summary>Virtualization is off in the firmware, or this processor has none: Windows can't turn it on.</summary>
    public bool FirmwareOff => Firmware == false && Hypervisor == false;

    public bool FeaturesOff => IsOff(MachinePlatform) || IsOff(Subsystem);

    public bool WslMissing => Wsl == NoWsl || (Wsl is not null && (!Version.TryParse(Wsl, out var version) || version < MinimumWsl));

    /// <summary>Virtualization is available but the Windows hypervisor isn't running: a restart is still pending after
    /// turning features on, or Windows' boot configuration keeps the hypervisor off.</summary>
    public bool HypervisorOff => Hypervisor == false && !FirmwareOff;

    /// <summary>Something Windows itself can fix (with one administrator approval, and usually a restart).</summary>
    public bool NeedsChanges => !FirmwareOff && (FeaturesOff || WslMissing || HypervisorOff);

    public bool Ready => Hypervisor == true && MachinePlatform == WindowsFeatureState.Enabled &&
        Subsystem == WindowsFeatureState.Enabled && Wsl is not null && !WslMissing;

    private static bool IsOff(WindowsFeatureState state) => state is WindowsFeatureState.Disabled or WindowsFeatureState.Absent;

    /// <summary>What stops Docker Desktop, in plain words; empty when nothing known does.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (FirmwareOff)
        {
            problems.Add("virtualization is turned off in this PC's firmware (UEFI/BIOS)");
            return problems;
        }
        if (IsOff(MachinePlatform)) problems.Add("Virtual Machine Platform is off");
        if (IsOff(Subsystem)) problems.Add("Windows Subsystem for Linux is off");
        if (WslMissing) problems.Add(Wsl == NoWsl ? "WSL isn't installed" : $"WSL {Wsl} is older than {MinimumWsl}");
        if (HypervisorOff && !FeaturesOff) problems.Add("the Windows hypervisor isn't running yet");
        return problems;
    }

    /// <summary>One line for a run window: every fact, including the ones Windows did not report.</summary>
    public string Describe()
    {
        static string Feature(WindowsFeatureState state) => state switch
        {
            WindowsFeatureState.Enabled => "on",
            WindowsFeatureState.Disabled => "off",
            WindowsFeatureState.Absent => "not available",
            _ => "unknown"
        };
        var firmware = Hypervisor == true ? "available" : Firmware switch { true => "on in firmware", false => "off in firmware", null => "unknown" };
        var hypervisor = Hypervisor switch { true => "running", false => "not running", null => "unknown" };
        var wsl = Wsl switch { null => "unknown", NoWsl => "not installed", var version => version };
        return $"virtualization {firmware}; Windows hypervisor {hypervisor}; Virtual Machine Platform {Feature(MachinePlatform)}; " +
            $"Windows Subsystem for Linux {Feature(Subsystem)}; WSL {wsl}" + (VirtualMachine ? "; this PC is a virtual machine" : "");
    }

    /// <summary>Reads the facts as a standard user: Win32_OptionalFeature, Win32_ComputerSystem and Win32_Processor through
    /// CIM, and <c>wsl --version</c>. Prints one line: <c>firmware=..|hypervisor=..|vmp=..|wsl=..|wslversion=..|vm=..</c>.</summary>
    public const string ProbeScript =
        "$ErrorActionPreference='SilentlyContinue';$ProgressPreference='SilentlyContinue';" +
        "$f=@{};Get-CimInstance Win32_OptionalFeature -Filter \"Name='VirtualMachinePlatform' OR Name='Microsoft-Windows-Subsystem-Linux'\"|" +
        "ForEach-Object{$f[$_.Name]=$_.InstallState};" +
        "$c=Get-CimInstance Win32_ComputerSystem;$p=Get-CimInstance Win32_Processor|Select-Object -First 1;" +
        "$w='none';$x=Join-Path $env:SystemRoot 'System32\\wsl.exe';" +
        "if(Test-Path -LiteralPath $x){$env:WSL_UTF8='1';$o=(& $x --version 2>$null|Out-String) -replace \"`0\",'';" +
        "if($LASTEXITCODE -eq 0 -and $o -match '(\\d+\\.\\d+\\.\\d+)'){$w=$Matches[1]}};" +
        "$vm=[bool](\"$($c.Manufacturer) $($c.Model)\" -match 'Virtual Machine|VMware|VirtualBox|KVM|QEMU|Parallels|Xen');" +
        "\"firmware=$($p.VirtualizationFirmwareEnabled)|hypervisor=$($c.HypervisorPresent)|vmp=$($f['VirtualMachinePlatform'])|" +
        "wsl=$($f['Microsoft-Windows-Subsystem-Linux'])|wslversion=$w|vm=$vm\"";

    /// <summary>Reads <see cref="ProbeScript"/>'s line; anything missing or unreadable stays unknown.</summary>
    public static WindowsVirtualization Parse(string? output)
    {
        var line = (output ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => l.StartsWith("firmware=", StringComparison.Ordinal));
        if (line is null) return Unknown;
        var values = line.Split('|').Select(part => part.Split('=', 2)).Where(pair => pair.Length == 2)
            .GroupBy(pair => pair[0], StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last()[1].Trim(), StringComparer.Ordinal);
        bool? Flag(string key) => values.GetValueOrDefault(key) switch
        {
            var v when string.Equals(v, "True", StringComparison.OrdinalIgnoreCase) => true,
            var v when string.Equals(v, "False", StringComparison.OrdinalIgnoreCase) => false,
            _ => null
        };
        WindowsFeatureState Feature(string key) => values.GetValueOrDefault(key) switch
        {
            "1" => WindowsFeatureState.Enabled,
            "2" => WindowsFeatureState.Disabled,
            "3" => WindowsFeatureState.Absent,
            _ => WindowsFeatureState.Unknown
        };
        var wsl = values.GetValueOrDefault("wslversion") switch
        {
            null or "" => null,
            NoWsl => NoWsl,
            var version when Version.TryParse(version, out _) => version,
            _ => null
        };
        return new(Flag("firmware"), Flag("hypervisor"), Feature("vmp"), Feature("wsl"), wsl, Flag("vm") == true);
    }

    /// <summary>Runs <see cref="ProbeScript"/> in a hidden Windows PowerShell (no administrator rights, no network).
    /// Returns <see cref="Unknown"/> off Windows, when PowerShell can't start or after a minute without an answer.</summary>
    public static async Task<WindowsVirtualization> ProbeAsync(CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) return Unknown;
        var start = new ProcessStartInfo(PowerShellPath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Encode(ProbeScript) })
            start.ArgumentList.Add(argument);
        Process? process;
        try { process = Process.Start(start); }
        catch (System.ComponentModel.Win32Exception) { return Unknown; }
        if (process is null) return Unknown;
        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(1));
            try
            {
                var errors = process.StandardError.ReadToEndAsync(timeout.Token);
                var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                await errors;
                return Parse(output);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                if (token.IsCancellationRequested) throw;
                return Unknown;
            }
        }
    }

    /// <summary>Exit code of <see cref="FixScript"/> when Windows must restart to finish.</summary>
    public const int RestartExitCode = 3010;

    /// <summary>The administrator step (one UAC prompt): turns on Virtual Machine Platform and Windows Subsystem for Linux
    /// (with their parent features), sets the Windows hypervisor to start with Windows when its boot entry turned it off,
    /// and installs or updates WSL from Microsoft when it is missing or older than <see cref="MinimumWsl"/>. Each step is
    /// written to <paramref name="log"/>. Exits 0 when done, <see cref="RestartExitCode"/> when Windows must restart,
    /// 2 when WSL could not be installed and 1 on another failure. Nothing it changes needs a decision.</summary>
    public static string FixScript(string log) =>
        "$ErrorActionPreference='Continue';$ProgressPreference='SilentlyContinue';" +
        $"$log='{log.Replace("'", "''", StringComparison.Ordinal)}';" +
        "function Say([string]$t){if($t){try{Add-Content -LiteralPath $log -Value $t -Encoding UTF8}catch{}}};" +
        "$restart=$false;" +
        "try{" +
        "foreach($n in @('VirtualMachinePlatform','Microsoft-Windows-Subsystem-Linux')){" +
        "$f=Get-WindowsOptionalFeature -Online -FeatureName $n -ErrorAction Stop;" +
        "if(-not $f){Say \"This edition of Windows has no $n feature.\";continue};" +
        "$s=[string]$f.State;" +
        "if($s -eq 'Enabled'){Say \"$n is already on.\";continue};" +
        "if($s -eq 'EnablePending'){Say \"$n turns on when Windows restarts.\";$restart=$true;continue};" +
        "Say \"Turning on $n...\";" +
        "$r=Enable-WindowsOptionalFeature -Online -FeatureName $n -All -NoRestart -ErrorAction Stop;" +
        "if($r.RestartNeeded){$restart=$true;Say \"$n turns on when Windows restarts.\"}else{Say \"$n is on.\"}};" +
        "$b=(& bcdedit.exe /enum '{current}' 2>$null|Out-String);" +
        "if($b -match 'hypervisorlaunchtype\\s+Off'){& bcdedit.exe /set '{current}' hypervisorlaunchtype auto|Out-Null;$restart=$true;" +
        "Say 'The Windows hypervisor was set not to start; it now starts with Windows.'};" +
        "$x=Join-Path $env:SystemRoot 'System32\\wsl.exe';$env:WSL_UTF8='1';" +
        "function V{if(Test-Path -LiteralPath $x){$o=(& $x --version 2>$null|Out-String) -replace \"`0\",'';" +
        "if($LASTEXITCODE -eq 0 -and $o -match '(\\d+\\.\\d+\\.\\d+)'){[version]$Matches[1]}}};" +
        "$v=V;" +
        "if(-not $v -or $v -lt [version]'" + MinimumWsl + "'){" +
        "Say 'Installing the current WSL from Microsoft (wsl --update). This can take a few minutes...';" +
        "Say (((& $x --update 2>&1|Out-String) -replace \"`0\",'').Trim());$v=V;" +
        "if(-not $v){Say 'Installing WSL (wsl --install --no-distribution)...';" +
        "Say (((& $x --install --no-distribution 2>&1|Out-String) -replace \"`0\",'').Trim());$v=V};" +
        "if($v){Say \"WSL $v is installed.\"}elseif(-not $restart){Say 'WSL could not be installed.';exit 2}" +
        "else{Say 'WSL finishes installing after the restart.'}}" +
        "else{Say \"WSL $v is installed.\"}" +
        "}catch{Say ('Stopped: '+$_.Exception.Message);exit 1};" +
        "if($restart){exit " + RestartExitCode + "};exit 0";

    public static string Encode(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    public static string PowerShellPath => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
}

/// <summary>What Martlet continues after Windows restarts to finish turning on virtualization: this PC's host service
/// and pairing (<see cref="ThisPc"/>), the host dashboard's host service (<see cref="HostService"/>), or only starting
/// Docker Desktop (<see cref="Docker"/>) for any other step, which the owner then repeats.</summary>
public enum ContinueSetupKind { Docker, ThisPc, HostService }

/// <summary>The note Martlet leaves in its data directory before Windows restarts for virtualization
/// (continue-setup.json): what to continue, which step asked for it and when. Martlet reads and deletes it once, at the
/// next start; it is ignored after <see cref="MaximumAge"/>.</summary>
public sealed record ContinueSetup(ContinueSetupKind Kind, string Task, DateTimeOffset Created)
{
    public const string FileName = "continue-setup.json";
    public static readonly TimeSpan MaximumAge = TimeSpan.FromDays(14);

    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase) }
    };

    public static string PathIn(string dataDirectory) => Path.Combine(dataDirectory, FileName);

    public void Save(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = PathIn(dataDirectory);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, System.Text.Json.JsonSerializer.Serialize(this, Json));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>The saved note, or null when there is none, it is unreadable or too old.</summary>
    public static ContinueSetup? Read(string dataDirectory, DateTimeOffset now)
    {
        try
        {
            var path = PathIn(dataDirectory);
            if (!File.Exists(path)) return null;
            var note = System.Text.Json.JsonSerializer.Deserialize<ContinueSetup>(File.ReadAllText(path), Json);
            if (note is null || string.IsNullOrWhiteSpace(note.Task) || !Enum.IsDefined(note.Kind)) return null;
            return now - note.Created > MaximumAge || note.Created - now > TimeSpan.FromDays(1) ? null : note;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or
            NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    public static void Delete(string dataDirectory)
    {
        try { File.Delete(PathIn(dataDirectory)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
    }
}
