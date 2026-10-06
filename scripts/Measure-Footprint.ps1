<#
.SYNOPSIS
    Measures what one Martlet component uses while it runs: graphics memory, system memory, processor and disk.

.DESCRIPTION
    Samples a process tree (started with -Command, or attached with -ProcessId / -ProcessName) and optionally a Docker
    container every -IntervalMs until it exits or -Seconds pass, then prints peak and steady (median of the second half)
    figures as JSON for docs/RESOURCE_FOOTPRINTS.md:

      - vramGb: the tree's own memory from nvidia-smi --query-compute-apps (Linux/WSL containers don't show their PIDs
        on Windows, so -WholeCard instead records the card's used memory less what it used before the run)
      - ramGb: working set of the whole process tree (or the container's memory from docker stats)
      - cpuThreads: processor time used per wall-clock second, i.e. how many hardware threads were busy
      - diskGb: size of -DiskPath (a model folder), when given

    Read-only: it starts nothing but -Command and never changes the component. See docs/RESOURCE_FOOTPRINTS.md
    ("How to re-measure").

.EXAMPLE
    .\scripts\Measure-Footprint.ps1 -Name parakeet-110m -Command python -Arguments 'bench.py','110m' -DiskPath $models
.EXAMPLE
    .\scripts\Measure-Footprint.ps1 -Name ollama-gemma4-e2b -ProcessName ollama -Seconds 60 -WholeCard
.EXAMPLE
    .\scripts\Measure-Footprint.ps1 -Name chatterbox -Container martlet-chatterbox-chatterbox-1 -Seconds 120 -WholeCard
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Name,
    [string]$Command,
    [string[]]$Arguments = @(),
    [int]$ProcessId,
    [string]$ProcessName,
    [string]$Container,
    [int]$Seconds = 600,
    [int]$IntervalMs = 250,
    [switch]$WholeCard,
    [string]$DiskPath,
    [string]$OutFile,
    [switch]$IncludeSamples
)

$ErrorActionPreference = 'Stop'
$smi = Join-Path $env:SystemRoot 'System32\nvidia-smi.exe'
if (-not (Test-Path $smi)) { $smi = (Get-Command nvidia-smi -ErrorAction SilentlyContinue)?.Source }

function Get-CardUsedMb {
    if (-not $smi) { return $null }
    $out = & $smi --query-gpu=memory.used --format=csv,noheader,nounits 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $out) { return $null }
    ($out | ForEach-Object { [double]$_ } | Measure-Object -Sum).Sum
}

function Get-AppVramMb([int[]]$Pids) {
    if (-not $smi) { return $null }
    $out = & $smi --query-compute-apps=pid,used_memory --format=csv,noheader,nounits 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    $sum = 0.0
    foreach ($line in $out) {
        $parts = $line -split ',\s*'
        if ($parts.Count -ge 2 -and $Pids -contains [int]$parts[0] -and $parts[1] -match '^\d') { $sum += [double]$parts[1] }
    }
    $sum
}

function Get-Tree([int]$Root) {
    $all = Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId
    $ids = [System.Collections.Generic.List[int]]::new(); $ids.Add($Root)
    for ($i = 0; $i -lt $ids.Count; $i++) {
        foreach ($p in $all) { if ($p.ParentProcessId -eq $ids[$i] -and -not $ids.Contains([int]$p.ProcessId)) { $ids.Add([int]$p.ProcessId) } }
    }
    $ids.ToArray()
}

function Get-ContainerSample {
    $line = docker stats --no-stream --format '{{.CPUPerc}}|{{.MemUsage}}' $Container 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $line) { return $null }
    $cpu, $mem = $line -split '\|'
    $used = ($mem -split '/')[0].Trim()
    $gb = if ($used -match '([\d.]+)\s*GiB') { [double]$Matches[1] } elseif ($used -match '([\d.]+)\s*MiB') { [double]$Matches[1] / 1024 } else { 0 }
    [pscustomobject]@{ Cpu = [double]($cpu.TrimEnd('%')) / 100; RamGb = $gb }
}

$baselineCardMb = Get-CardUsedMb
$started = $null
if ($Command) {
    $started = Start-Process -FilePath $Command -ArgumentList $Arguments -PassThru -NoNewWindow
    $ProcessId = $started.Id
} elseif ($ProcessName -and -not $ProcessId) {
    $ProcessId = (Get-Process -Name $ProcessName | Sort-Object WorkingSet64 -Descending | Select-Object -First 1).Id
}
if (-not $ProcessId -and -not $Container) { throw 'Give -Command, -ProcessId, -ProcessName or -Container.' }

$samples = [System.Collections.Generic.List[object]]::new()
$clock = [System.Diagnostics.Stopwatch]::StartNew()
$lastCpu = $null; $lastMs = 0
while ($clock.Elapsed.TotalSeconds -lt $Seconds) {
    $ramGb = $null; $threads = $null; $vramMb = $null
    if ($ProcessId) {
        $tree = Get-Tree $ProcessId
        $procs = @($tree | ForEach-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue })
        if ($procs.Count -eq 0) { break }
        $ramGb = ($procs | Measure-Object WorkingSet64 -Sum).Sum / 1GB
        $cpu = ($procs | Measure-Object { $_.TotalProcessorTime.TotalMilliseconds } -Sum).Sum
        $ms = $clock.Elapsed.TotalMilliseconds
        if ($null -ne $lastCpu -and $ms -gt $lastMs) { $threads = [math]::Max(0.0, ($cpu - $lastCpu) / ($ms - $lastMs)) }
        $lastCpu = $cpu; $lastMs = $ms
        if (-not $WholeCard) { $vramMb = Get-AppVramMb $tree }
    }
    if ($Container) {
        $c = Get-ContainerSample
        if ($c) { $ramGb = $c.RamGb; $threads = $c.Cpu }
    }
    if ($WholeCard -and $null -ne $baselineCardMb) { $vramMb = [math]::Max(0.0, (Get-CardUsedMb) - $baselineCardMb) }
    $samples.Add([pscustomobject]@{ T = [math]::Round($clock.Elapsed.TotalSeconds, 2); RamGb = $ramGb; CpuThreads = $threads; VramGb = if ($null -ne $vramMb) { $vramMb / 1024 } })
    Start-Sleep -Milliseconds $IntervalMs
    if ($started -and $started.HasExited) { break }
}

function Summary([string]$Field) {
    $values = @($samples | Where-Object { $null -ne $_.$Field } | ForEach-Object { [double]$_.$Field })
    if ($values.Count -eq 0) { return $null }
    $half = @($values | Select-Object -Skip ([int][math]::Floor($values.Count / 2)) | Sort-Object)
    [ordered]@{
        peak = [math]::Round(($values | Measure-Object -Maximum).Maximum, 2)
        steady = [math]::Round($half[[int][math]::Floor($half.Count / 2)], 2)
    }
}

$result = [ordered]@{
    name = $Name
    measuredAt = (Get-Date).ToString('s')
    machine = [ordered]@{
        cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name.Trim()
        logicalProcessors = [Environment]::ProcessorCount
        ramGb = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB, 1)
        gpu = if ($smi) { (& $smi --query-gpu=name,memory.total --format=csv,noheader 2>$null) -join '; ' } else { $null }
    }
    seconds = [math]::Round($clock.Elapsed.TotalSeconds, 1)
    samples = $samples.Count
    vramGb = Summary 'VramGb'
    vramMethod = if ($WholeCard) { 'card used memory less the baseline before the run' } else { 'nvidia-smi compute apps of the process tree' }
    ramGb = Summary 'RamGb'
    cpuThreads = Summary 'CpuThreads'
    diskGb = if ($DiskPath -and (Test-Path $DiskPath)) { [math]::Round((Get-ChildItem $DiskPath -Recurse -File | Measure-Object Length -Sum).Sum / 1GB, 3) }
    exitCode = if ($started) { $started.WaitForExit(); $started.ExitCode }
}
if ($IncludeSamples) { $result.series = $samples }
$json = $result | ConvertTo-Json -Depth 4
if ($OutFile) { $json | Set-Content -Path $OutFile -Encoding utf8 }
$json
