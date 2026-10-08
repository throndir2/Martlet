#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PayloadRoot,
    [string]$ProtectedDataDirectory
)
. "$PSScriptRoot\Packaging.Common.ps1"
. "$PSScriptRoot\ProtectedData.Common.ps1"
Assert-PackagingHost
$manifest = Test-PayloadManifest $PayloadRoot
$data = Join-Path ([IO.Path]::GetTempPath()) ("Martlet Packaging $([char]0x00E9) " + [guid]::NewGuid().ToString('N'))
$protectedOverride = $PSBoundParameters.ContainsKey('ProtectedDataDirectory')
$realData = if ($protectedOverride) {
    Resolve-ProtectedDataDirectory $ProtectedDataDirectory @($PayloadRoot, (Split-Path $PayloadRoot), $data)
} else { Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Martlet' }
function Get-RealSettingsSnapshot {
    if ($protectedOverride) { return Get-ProtectedDataSnapshot $realData }
    if (-not (Test-Path -LiteralPath $realData)) { return 'absent' }
    $snapshot = @(Get-ChildItem -LiteralPath $realData -Force | Sort-Object Name | ForEach-Object {
        "$($_.Name)|$($_.LastWriteTimeUtc.Ticks)|$($_.Attributes)"
    })
    $settings = Join-Path $realData 'settings.json'
    if (Test-Path -LiteralPath $settings -PathType Leaf) { $snapshot += (Get-FileHash -LiteralPath $settings -Algorithm SHA256).Hash }
    return $snapshot -join "`n"
}
$before = Get-RealSettingsSnapshot
$environmentBefore = @{}
try {
    # These child processes cannot use a developer SDK/runtime through environment discovery.
    foreach ($key in @('DOTNET_ROOT', 'DOTNET_ROOT_X64', 'DOTNET_MULTILEVEL_LOOKUP')) {
        $environmentBefore[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
        [Environment]::SetEnvironmentVariable($key, $(if ($key -eq 'DOTNET_MULTILEVEL_LOOKUP') { '0' } else { $data }), 'Process')
    }
    $doctor = Join-Path $PayloadRoot 'Doctor\Martlet.Doctor.exe'
    $version = Invoke-BoundedProcess $doctor @('--version')
    if ($version.ExitCode -ne 0 -or $version.Stdout -notlike "*$($manifest.applicationVersion.Substring(0, $manifest.applicationVersion.LastIndexOf('.')))*") {
        throw 'Published Doctor version command did not report the packaged version.'
    }
    $help = Invoke-BoundedProcess $doctor @('--help')
    if ($help.ExitCode -ne 0 -or $help.Stdout -notlike '*--data-directory*') { throw 'Bundled Doctor help is unavailable.' }
    $status = Invoke-BoundedProcess $doctor @('status', '--json', '--data-directory', $data)
    $report = $status.Stdout | ConvertFrom-Json
    if ($status.ExitCode -ne 2 -or $report.exit_code -ne 2 -or $report.ready -ne $false -or $report.settings_state -ne 'first_run') {
        throw 'Published Doctor did not preserve first-run/incomplete semantics.'
    }
    if (Test-Path -LiteralPath $data) { throw 'Doctor wrote data during read-only first-run launch.' }
    $scripts = Join-Path (Split-Path (Split-Path $PSScriptRoot)) 'scripts'
    Invoke-ExecutableSmoke "$scripts\Smoke-Doctor.ps1" $doctor

    [IO.Directory]::CreateDirectory($data) | Out-Null
    $file = Join-Path $data 'settings.json'
    foreach ($case in @(
        @{ Bytes = [IO.File]::ReadAllBytes((Join-Path (Split-Path (Split-Path $PSScriptRoot)) 'contracts\golden\settings-v1.json')); ExitCode = 2; State = 'loaded' },
        @{ Bytes = [Text.Encoding]::UTF8.GetBytes('{"malformed":true}'); ExitCode = 3; State = 'invalid' }
    )) {
        [IO.File]::WriteAllBytes($file, $case.Bytes)
        $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
        $status = Invoke-BoundedProcess $doctor @('status', '--json', '--data-directory', $data)
        $report = $status.Stdout | ConvertFrom-Json
        if ($status.ExitCode -ne $case.ExitCode -or $report.exit_code -ne $case.ExitCode -or
            $report.settings_state -cne $case.State -or $report.ready -ne $false) {
            throw "Doctor did not report $($case.State) settings with exit $($case.ExitCode)."
        }
        Assert-Sha256 $file $hash
    }
}
finally {
    foreach ($key in $environmentBefore.Keys) { [Environment]::SetEnvironmentVariable($key, $environmentBefore[$key], 'Process') }
    if (Test-Path -LiteralPath $data) {
        $settings = Join-Path $data 'settings.json'
        if (Test-Path -LiteralPath $settings) { [IO.File]::Delete($settings) }
        # Nonrecursive cleanup deliberately fails if the application unexpectedly wrote other files.
        [IO.Directory]::Delete($data)
    }
    if ($protectedOverride) { Assert-ProtectedDataUnchanged $realData $before }
    elseif ((Get-RealSettingsSnapshot) -cne $before) { throw 'Real user settings changed during smoke. Stop and investigate; no automatic restore attempted.' }
}
$preservation = if ($protectedOverride) { 'selected protected scope unchanged; ordinary profile not inspected' } else { 'real user settings unchanged' }
Write-Output "PASS: published Doctor version/help/JSON/data preservation; audio OFF; isolated data paths; $preservation."
