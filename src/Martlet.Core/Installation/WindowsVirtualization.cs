using System.Diagnostics;
using System.Text;

namespace Martlet.Core.Installation;

/// <summary>A Windows optional feature as Windows reports it to a standard user (Win32_OptionalFeature).</summary>
public enum WindowsFeatureState { Unknown, Enabled, Disabled, Absent }

public enum WindowsServiceState { Unknown, Running, Stopped, Disabled, Absent }

/// <summary>What wsl.exe --status reports, not a test that starts a Linux VM. WSL can report unavailable WSL 2 with exit 0.</summary>
public enum WslStatusState { Unknown, Available, Unavailable, RestartRequired, Failed }

/// <summary>What Docker Desktop's WSL 2 engine needs from Windows, read without administrator rights
/// (<see cref="ProbeAsync"/>): virtualization turned on in the firmware (UEFI/BIOS), the Virtual Machine Platform and
/// Windows Subsystem for Linux features, their host services, the Windows hypervisor and WSL <see cref="MinimumWsl"/> or later.
/// Unknown facts never count as a known blocker, but cannot establish readiness.</summary>
public sealed record WindowsVirtualization(bool? Firmware, bool? Hypervisor, WindowsFeatureState MachinePlatform,
    WindowsFeatureState Subsystem, string? Wsl, bool VirtualMachine)
{
    /// <summary>The oldest WSL Docker Desktop supports.</summary>
    public static readonly Version MinimumWsl = new(2, 1, 5);

    /// <summary><see cref="Wsl"/> when wsl.exe is missing or does not report a version (no current WSL installed).</summary>
    public const string NoWsl = "none";

    public static WindowsVirtualization Unknown { get; } =
        new(null, null, WindowsFeatureState.Unknown, WindowsFeatureState.Unknown, null, false);

    public WindowsServiceState ComputeService { get; init; }
    public WindowsServiceState NetworkService { get; init; }
    public WslStatusState WslStatus { get; init; }
    public int? WslStatusExitCode { get; init; }
    public bool? RestartPending { get; init; }
    public IReadOnlyList<string> ProbeIssues { get; init; } = [];

    /// <summary>Virtualization is off in the firmware, or this processor has none: Windows can't turn it on.</summary>
    public bool FirmwareOff => Firmware == false && Hypervisor == false;

    public bool FeaturesOff => IsOff(MachinePlatform) || IsOff(Subsystem);

    public bool WslMissing => Wsl == NoWsl || (Wsl is not null && (!Version.TryParse(Wsl, out var version) || version < MinimumWsl));

    /// <summary>Virtualization is available but the Windows hypervisor isn't running: a restart is still pending after
    /// turning features on, or Windows' boot configuration keeps the hypervisor off.</summary>
    public bool HypervisorOff => Hypervisor == false && !FirmwareOff;

    public bool RuntimeUnavailable => ServiceUnavailable(ComputeService) || ServiceUnavailable(NetworkService) ||
        WslStatus is WslStatusState.Unavailable or WslStatusState.RestartRequired or WslStatusState.Failed;

    /// <summary>CIM can say features are enabled before their services exist. A pending restart plus an unavailable
    /// runtime is not a reason to reinstall them. An unrelated pending update alone does not block working WSL.</summary>
    public bool RestartRequired => !FirmwareOff && (WslStatus == WslStatusState.RestartRequired ||
        RestartPending == true && MachinePlatform == WindowsFeatureState.Enabled && Subsystem == WindowsFeatureState.Enabled &&
        (HypervisorOff || WslMissing || RuntimeUnavailable));

    /// <summary>Something Windows itself can fix (with one administrator approval, and usually a restart).</summary>
    public bool NeedsChanges => !FirmwareOff && !RestartRequired && (FeaturesOff || WslMissing || HypervisorOff);

    public bool Blocked => FirmwareOff || NeedsChanges || RestartRequired || RuntimeUnavailable;

    public bool Ready => Hypervisor == true && MachinePlatform == WindowsFeatureState.Enabled &&
        Subsystem == WindowsFeatureState.Enabled && Wsl is not null && !WslMissing &&
        ServiceAvailable(ComputeService) && ServiceAvailable(NetworkService) && WslStatus == WslStatusState.Available;

    private static bool IsOff(WindowsFeatureState state) => state is WindowsFeatureState.Disabled or WindowsFeatureState.Absent;
    private static bool ServiceUnavailable(WindowsServiceState state) => state is WindowsServiceState.Disabled or WindowsServiceState.Absent;
    private static bool ServiceAvailable(WindowsServiceState state) => state is WindowsServiceState.Running or WindowsServiceState.Stopped;

    public string Recovery => FirmwareOff
        ? VirtualMachine
            ? "Ask the administrator of this virtual machine's host to enable nested virtualization, then check again."
            : "Turn on Intel VT-x or AMD SVM in this PC's firmware settings (UEFI/BIOS), then check again."
        : RestartRequired
            ? "Restart Windows to finish setting up virtualization, then let Martlet continue setup. Restarting Docker Desktop alone cannot finish this Windows change."
        : NeedsChanges
            ? "Let Martlet set up the Windows features and WSL that Docker Desktop needs. Windows asks for administrator approval and may need a restart."
        : RuntimeUnavailable
            ? "Restart Windows, then check again. If WSL 2 or its Windows services are still unavailable, repair Virtual Machine Platform and WSL through Windows setup; do not reset Docker's data."
        : Ready
            ? "Windows prerequisites are available. Start Docker Desktop and wait for its engine to answer."
            : "Some Windows checks are unavailable. Check Docker Desktop's own error before changing Windows or firmware settings.";

    /// <summary>What stops Docker Desktop, in plain words; empty when nothing known does.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (FirmwareOff)
        {
            problems.Add(VirtualMachine ? "hardware virtualization is not exposed to this virtual machine"
                : "virtualization is turned off in this PC's firmware (UEFI/BIOS)");
            return problems;
        }
        if (RestartRequired) problems.Add("Windows must restart to finish setting up virtualization");
        if (IsOff(MachinePlatform)) problems.Add("Virtual Machine Platform is off");
        if (IsOff(Subsystem)) problems.Add("Windows Subsystem for Linux is off");
        if (WslMissing) problems.Add(Wsl == NoWsl ? "WSL isn't installed" : $"WSL {Wsl} is older than {MinimumWsl}");
        if (HypervisorOff && !FeaturesOff) problems.Add("the Windows hypervisor isn't running yet");
        if (ServiceUnavailable(ComputeService))
            problems.Add($"Host Compute Service (vmcompute) is {(ComputeService == WindowsServiceState.Absent ? "not installed" : "disabled")}");
        if (ServiceUnavailable(NetworkService))
            problems.Add($"Host Network Service (hns) is {(NetworkService == WindowsServiceState.Absent ? "not installed" : "disabled")}");
        if (WslStatus == WslStatusState.Unavailable) problems.Add("WSL reports that WSL 2 cannot start");
        if (WslStatus == WslStatusState.Failed)
            problems.Add($"WSL status failed{(WslStatusExitCode is { } exit ? $" (exit {exit})" : "")}");
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
        var status = WslStatus switch
        {
            WslStatusState.Available => "available",
            WslStatusState.Unavailable => "cannot start",
            WslStatusState.RestartRequired => "needs a Windows restart",
            WslStatusState.Failed => "status failed",
            _ => "status unknown"
        };
        var restart = RestartPending switch { true => "pending", false => "not pending", null => "unknown" };
        return $"virtualization {firmware}; Windows hypervisor {hypervisor}; Virtual Machine Platform {Feature(MachinePlatform)}; " +
            $"Windows Subsystem for Linux {Feature(Subsystem)}; WSL {wsl}; WSL 2 {status}; " +
            $"Host Compute Service {ComputeService.ToString().ToLowerInvariant()}; Host Network Service {NetworkService.ToString().ToLowerInvariant()}; " +
            $"Windows restart {restart}" + (VirtualMachine ? "; this PC is a virtual machine" : "") +
            (ProbeIssues.Count > 0 ? "; checks unavailable: " + string.Join(", ", ProbeIssues) : "");
    }

    /// <summary>Reads the facts as a standard user: Win32_OptionalFeature, Win32_ComputerSystem and Win32_Processor through
    /// CIM, the host services, pending-restart registry markers, and <c>wsl --version</c> / <c>wsl --status</c>.
    /// Only fixed status fields are printed; WSL output can contain the owner's distribution name and is not returned.</summary>
    public const string ProbeScript = """
        $ErrorActionPreference='Continue';$ProgressPreference='SilentlyContinue'
        $issues=[System.Collections.Generic.List[string]]::new()
        $f=@{};$c=$null;$p=$null;$services=@{};$restart=''
        try {
            Get-CimInstance Win32_OptionalFeature -Filter "Name='VirtualMachinePlatform' OR Name='Microsoft-Windows-Subsystem-Linux'" -ErrorAction Stop |
                ForEach-Object {$f[$_.Name]=$_.InstallState}
        } catch {$issues.Add('optional features')}
        try {$c=Get-CimInstance Win32_ComputerSystem -ErrorAction Stop} catch {$issues.Add('hypervisor')}
        try {$p=Get-CimInstance Win32_Processor -ErrorAction Stop | Select-Object -First 1} catch {$issues.Add('firmware')}
        try {
            $services['vmcompute']='Absent';$services['hns']='Absent'
            Get-CimInstance Win32_Service -Filter "Name='vmcompute' OR Name='hns'" -ErrorAction Stop | ForEach-Object {
                $services[$_.Name]=if ($_.StartMode -eq 'Disabled') {'Disabled'}
                    elseif ($_.State -eq 'Running') {'Running'}
                    elseif ($_.State -eq 'Stopped') {'Stopped'} else {'Unknown'}
            }
        } catch {$services=@{};$issues.Add('host services')}
        try {
            $restart=(Test-Path -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending' -ErrorAction Stop) -or
                (Test-Path -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired' -ErrorAction Stop)
        } catch {$issues.Add('restart status')}
        $w='none';$status='Unknown';$code=''
        $x=Join-Path $env:SystemRoot 'System32\wsl.exe'
        if (Test-Path -LiteralPath $x) {
            $env:WSL_UTF8='1'
            $o=(& $x --version 2>$null | Out-String) -replace "`0",''
            if ($LASTEXITCODE -eq 0 -and $o -match '(\d+\.\d+\.\d+)') {$w=$Matches[1]}
            $LASTEXITCODE=$null
            $o=(& $x --status 2>&1 | ForEach-Object {"$_"} | Out-String) -replace "`0",''
            $code=$LASTEXITCODE
            if ($null -eq $code) {$issues.Add('WSL status')}
            elseif ($code -in 3010,1641,-2147021886 -or $o -match 'until the system is rebooted') {$status='RestartRequired'}
            elseif ($o -match '(?im)^\s*WSL\s*2\b.*(?:not supported|unable to start|not available|not enabled)|HCS_E_HYPERV_NOT_INSTALLED|0x80370102') {$status='Unavailable'}
            elseif ($code -ne 0) {$status='Failed'}
            else {$status='Available'}
        }
        $vm=[bool]("$($c.Manufacturer) $($c.Model)" -match 'Virtual Machine|VMware|VirtualBox|KVM|QEMU|Parallels|Xen')
        "firmware=$($p.VirtualizationFirmwareEnabled)|hypervisor=$($c.HypervisorPresent)|vmp=$($f['VirtualMachinePlatform'])|wsl=$($f['Microsoft-Windows-Subsystem-Linux'])|wslversion=$w|vm=$vm|compute=$($services['vmcompute'])|network=$($services['hns'])|wslstatus=$status|wslexit=$code|restart=$restart|issues=$($issues -join ',')"
        """;

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
        WindowsServiceState Service(string key) =>
            Enum.TryParse<WindowsServiceState>(values.GetValueOrDefault(key), out var state) && Enum.IsDefined(state)
                ? state : WindowsServiceState.Unknown;
        return new(Flag("firmware"), Flag("hypervisor"), Feature("vmp"), Feature("wsl"), wsl, Flag("vm") == true)
        {
            ComputeService = Service("compute"),
            NetworkService = Service("network"),
            WslStatus = Enum.TryParse<WslStatusState>(values.GetValueOrDefault("wslstatus"), out var status) && Enum.IsDefined(status)
                ? status : WslStatusState.Unknown,
            WslStatusExitCode = int.TryParse(values.GetValueOrDefault("wslexit"), out var exit) ? exit : null,
            RestartPending = Flag("restart"),
            ProbeIssues = (values.GetValueOrDefault("issues") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
        };
    }

    /// <summary>Runs <see cref="ProbeScript"/> in a hidden Windows PowerShell (no administrator rights, no network).
    /// Returns <see cref="Unknown"/> off Windows, when PowerShell can't start or after a minute without an answer.</summary>
    public static async Task<WindowsVirtualization> ProbeAsync(CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) return Unknown with { ProbeIssues = ["requires Windows"] };
        var start = new ProcessStartInfo(PowerShellPath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Encode(ProbeScript) })
            start.ArgumentList.Add(argument);
        Process? process;
        try { process = Process.Start(start); }
        catch (System.ComponentModel.Win32Exception) { return Unknown with { ProbeIssues = ["PowerShell could not start"] }; }
        if (process is null) return Unknown with { ProbeIssues = ["PowerShell could not start"] };
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
                var state = Parse(output);
                return ReferenceEquals(state, Unknown)
                    ? Unknown with { ProbeIssues = ["Windows prerequisite probe returned no status"] } : state;
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                if (token.IsCancellationRequested) throw;
                return Unknown with { ProbeIssues = ["Windows prerequisite probe timed out after one minute"] };
            }
        }
    }

    /// <summary>Exit code of <see cref="FixScript"/> when Windows must restart to finish.</summary>
    public const int RestartExitCode = 3010;

    /// <summary>The administrator step (one UAC prompt): turns on Virtual Machine Platform and Windows Subsystem for Linux
    /// (with their parent features), sets the Windows hypervisor to start with Windows when its boot entry turned it off,
    /// and installs WSL from Microsoft when it is missing (<c>wsl --install --no-distribution</c>, then <c>wsl --update</c>)
    /// or updates it when older than <see cref="MinimumWsl"/>. wsl.exe's help text (an option this wsl.exe lacks) is not
    /// logged, and when wsl.exe says Windows must restart (exit 3010, 1641 or Windows' restart message) the script asks for
    /// the restart. Each step is written to <paramref name="log"/>. Exits 0 when done, <see cref="RestartExitCode"/> when
    /// Windows must restart, 2 when WSL could not be installed and 1 on another failure. Nothing it changes needs a decision.</summary>
    public static string FixScript(string log) =>
        "$ErrorActionPreference='Continue';$ProgressPreference='SilentlyContinue';" +
        $"$log='{log.Replace("'", "''", StringComparison.Ordinal)}';" +
        "function Say([string]$t){if($t){try{Add-Content -LiteralPath $log -Value $t -Encoding UTF8}catch{}}};" +
        "$restart=$false;$wr=$false;" +
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
        "if($LASTEXITCODE -ne 0){throw 'The Windows boot configuration could not be read.'};" +
        "if($b -match 'hypervisorlaunchtype\\s+Off'){& bcdedit.exe /set '{current}' hypervisorlaunchtype auto|Out-Null;" +
        "if($LASTEXITCODE -ne 0){throw 'The Windows hypervisor boot setting could not be changed.'};$restart=$true;" +
        "Say 'The Windows hypervisor was set not to start; it now starts with Windows.'};" +
        "$x=Join-Path $env:SystemRoot 'System32\\wsl.exe';$env:WSL_UTF8='1';" +
        "function V{if(Test-Path -LiteralPath $x){$o=(& $x --version 2>$null|Out-String) -replace \"`0\",'';" +
        "if($LASTEXITCODE -eq 0 -and $o -match '(\\d+\\.\\d+\\.\\d+)'){[version]$Matches[1]}}};" +
        // W runs wsl.exe and logs its output as plain text; true when wsl.exe says Windows must restart to finish.
        "function W([string[]]$a){if(-not(Test-Path -LiteralPath $x)){Say 'wsl.exe is missing.';return $false};" +
        "$o=((& $x @a 2>&1|ForEach-Object{\"$_\"}|Out-String) -replace \"`0\",'').Trim();$c=$LASTEXITCODE;" +
        "if($o -match '(?m)^\\s*Usage:'){Say \"This wsl.exe doesn't support wsl $($a -join ' ').\"}else{Say $o};" +
        "return ($c -in 3010,1641,-2147021886 -or $o -match 'until the system is rebooted')};" +
        "$m=[version]'" + MinimumWsl + "';$v=V;" +
        "if(-not $v -or $v -lt $m){" +
        "if(-not $v){Say 'Installing WSL from Microsoft (wsl --install --no-distribution). This can take a few minutes...';" +
        "if(W @('--install','--no-distribution')){$restart=$true;$wr=$true};$v=V};" +
        "if(-not $v -or $v -lt $m){Say 'Updating WSL from Microsoft (wsl --update). This can take a few minutes...';" +
        "if(W @('--update')){$restart=$true;$wr=$true};$v=V};" +
        "if($v -and $v -ge $m){Say \"WSL $v is installed.\"};" +
        "if($wr){Say 'WSL says Windows must restart to finish installing it.'}" +
        "elseif(-not $v -or $v -lt $m){if($restart){Say 'WSL finishes installing after Windows restarts.'}" +
        "elseif($v){Say \"WSL $v is older than $m and couldn't be updated.\";exit 2}else{Say 'WSL could not be installed.';exit 2}}}" +
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
