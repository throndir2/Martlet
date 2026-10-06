# Developer Guide

## Prerequisites

Windows with .NET SDK **10.0.401** from `global.json`, PowerShell 7 and Node.js/npm. Worker tests use Python 3.12. Docker Desktop in Linux container mode can run Linux-only tests locally.

## Build

```powershell
npm ci --prefix src\Martlet.Avatar.Vrm --no-audit --no-fund
dotnet restore Martlet.slnx --locked-mode
dotnet build Martlet.slnx --no-restore -c Release "-p:NodeExecutable=$((Get-Command node).Source)"
```

## Developer profile

```powershell
.\scripts\Test-Martlet.ps1 -InitProfile
```

The profile is local to the developer and lists SDK/Python locations and validation hosts. It is never committed.

## Targeted validation

```powershell
.\scripts\Test-Martlet.ps1 -List
.\scripts\Test-Martlet.ps1 -Project Martlet.Core.Tests -Filter 'FullyQualifiedName~Settings'
```

Run only tests you changed and the suite directly covering the changed code. Name test projects exactly with `.Tests`.

## Disposable data directory

```powershell
$data = Join-Path $env:TEMP ('Martlet.Dev.' + [guid]::NewGuid().ToString('N'))
dotnet run --project src\Martlet.Desktop --no-build -c Release -- --data-directory $data
dotnet run --project src\Martlet.Doctor -f net10.0-windows --no-build -c Release -- status --json --data-directory $data
```

## Doctor exit codes

| Exit | Meaning |
| --- | --- |
| 0 | Selected checks passed, or help/version succeeded. |
| 1 | Reported failure or callback fault. |
| 2 | Incomplete, warning, unknown, skipped, not configured, timed out, catalog-only or no required checks. |
| 3 | Invalid invocation or invalid/newer/inaccessible selected settings. |

Doctor status is read-only and does not open devices or provider connections.

## MCP verification

```powershell
.\scripts\Invoke-MartletMcp.ps1 -Build -Calls '[{"name":"doctor_status"}]'
.\scripts\Invoke-MartletMcp.ps1 -Build -Desktop -Calls '[{"name":"ui_click","arguments":{"id":"TourSkip"}},{"name":"ui_click","arguments":{"id":"NavSettings"}},{"name":"ui_snapshot","until":"AutomaticUpdateCheck"}]'
```

Use disposable data. `-AllowUiEffects` never authorizes spending, real provider requests, credential handling, audio capture/playback or data disclosure.

More detail: [Contributing](https://github.com/throndir2/Martlet/blob/main/CONTRIBUTING.md), [Validation](https://github.com/throndir2/Martlet/blob/main/docs/VALIDATION.md), [Diagnostics](https://github.com/throndir2/Martlet/blob/main/docs/DIAGNOSTICS.md), [MCP](https://github.com/throndir2/Martlet/blob/main/docs/MCP.md).
