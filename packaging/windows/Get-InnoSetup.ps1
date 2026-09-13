#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Destination,
    [string]$ArchivePath
)
. "$PSScriptRoot\Packaging.Common.ps1"
Assert-PackagingHost
$pin = (Get-PackagingPins).inno
New-OutputDirectory $Destination
if (-not $ArchivePath) {
    $ArchivePath = Join-Path $Destination "innosetup-$($pin.version)-x64.exe"
    Invoke-WebRequest -Uri $pin.url -OutFile $ArchivePath
}
Assert-Sha256 $ArchivePath $pin.sha256
$signature = Get-AuthenticodeSignature -LiteralPath $ArchivePath
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -cne $pin.publisher) {
    throw 'Pinned Inno archive signature/publisher cannot be verified. Do not bypass Windows trust checks.'
}
$compilerDirectory = Join-Path $Destination 'compiler'
$log = Join-Path $Destination 'portable-extraction.log'
$result = Invoke-BoundedProcess $ArchivePath @('/CURRENTUSER', '/PORTABLE=1', '/VERYSILENT',
    '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/NOICONS', '/TASKS=', "/DIR=$compilerDirectory", "/LOG=$log") 120
if ($result.ExitCode -ne 0) { throw "Inno portable extraction failed (exit $($result.ExitCode)); inspect $log." }
$compiler = (Get-RequiredFile (Join-Path $compilerDirectory 'ISCC.exe')).FullName
Assert-X64Pe $compiler
$version = Invoke-BoundedProcess $compiler @('--version')
if ($version.ExitCode -ne 0 -or $version.Stdout.Trim() -cne $pin.version) { throw "Extracted Inno compiler version is incorrect: $($version.Stdout)" }
$paths = [Collections.Generic.List[string]]::new()
foreach ($file in Get-ChildItem -LiteralPath $compilerDirectory -File -Recurse) {
    $paths.Add([IO.Path]::GetRelativePath($compilerDirectory, $file.FullName))
}
$paths.Sort([StringComparer]::Ordinal)
$files = @($paths | ForEach-Object {
    [ordered]@{ path = $_; sha256 = (Get-FileHash -LiteralPath (Join-Path $compilerDirectory $_) -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$receipt = [ordered]@{ archiveUrl = $pin.url; archiveSha256 = $pin.sha256; version = $pin.version; publisher = $pin.publisher; files = $files }
[IO.File]::WriteAllText((Join-Path $Destination 'builder-receipt.json'), ($receipt | ConvertTo-Json -Depth 6) + "`n")
Assert-Sha256 (Join-Path $Destination 'builder-receipt.json') $pin.receiptSha256
Write-Output "Verified portable Inno $($pin.version) compiler: $compiler"
