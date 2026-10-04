# Shared by the developer scripts (dot-source it): the local developer profile and the pinned .NET SDK.
# See docs/VALIDATION.md#validation-hosts-and-the-developer-profile.

function Get-MartletDevHome {
    if ($env:MARTLET_DEV_HOME) { $env:MARTLET_DEV_HOME } else { Join-Path $HOME '.martlet-dev' }
}

function Get-MartletDevProfile {
    $path = Join-Path (Get-MartletDevHome) 'validation.json'
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    try { Get-Content -Raw -LiteralPath $path | ConvertFrom-Json }
    catch { throw "Cannot read the developer profile ${path}: $($_.Exception.Message)" }
}

# The dotnet that has the SDK pinned in global.json: the profile's "dotnet", then PATH, DOTNET_ROOT and the
# usual per-user and machine install locations.
function Find-MartletDotnet([string]$Root, $DevProfile) {
    $sdkVersion = (Get-Content -Raw -LiteralPath (Join-Path $Root 'global.json') | ConvertFrom-Json).sdk.version
    $exe = if ($IsWindows) { 'dotnet.exe' } else { 'dotnet' }
    $candidates = [Collections.Generic.List[string]]::new()
    if ($DevProfile -and $DevProfile.PSObject.Properties['dotnet'] -and $DevProfile.dotnet) { $candidates.Add($DevProfile.dotnet) }
    $onPath = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($onPath) { $candidates.Add($onPath.Source) }
    if ($env:DOTNET_ROOT) { $candidates.Add((Join-Path $env:DOTNET_ROOT $exe)) }
    if ($IsWindows) {
        $candidates.Add((Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'))
        $candidates.Add((Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'))
    }
    $candidates.Add((Join-Path $HOME '.dotnet' $exe))
    if (-not $IsWindows) { $candidates.Add('/usr/share/dotnet/dotnet'); $candidates.Add('/usr/lib/dotnet/dotnet') }
    foreach ($candidate in $candidates | Select-Object -Unique) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        $sdks = & $candidate --list-sdks 2>$null
        if ($sdks -match "^$([regex]::Escape($sdkVersion)) ") { return (Resolve-Path -LiteralPath $candidate).Path }
    }
    throw "No dotnet with the .NET SDK $sdkVersion pinned in global.json. Install it, or set `"dotnet`" in $(Join-Path (Get-MartletDevHome) 'validation.json')."
}
