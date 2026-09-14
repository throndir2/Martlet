. "$PSScriptRoot\Packaging.Common.ps1"

function Get-VerifiedInnoCompiler([string]$BuilderDirectory) {
    $pin = (Get-PackagingPins).inno
    $receiptPath = Join-Path $BuilderDirectory 'builder-receipt.json'
    Assert-Sha256 $receiptPath $pin.receiptSha256
    $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    $directory = Join-Path $BuilderDirectory 'compiler'
    $items = @((Get-Item -LiteralPath $directory)) + @(Get-ChildItem -LiteralPath $directory -Recurse -Force)
    if (@($items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count -gt 0) {
        throw 'Reparse points are not allowed in the pinned compiler directory.'
    }
    if (@($items | Where-Object { -not $_.PSIsContainer }).Count -ne $receipt.files.Count) {
        throw 'Compiler files differ from the pinned portable extraction. Reacquire into a new directory.'
    }
    foreach ($file in $receipt.files) { Assert-Sha256 (Join-Path $directory $file.path) $file.sha256 }
    $compiler = Join-Path $directory 'ISCC.exe'
    Assert-X64Pe $compiler
    $version = Invoke-BoundedProcess $compiler @('--version')
    if ($version.ExitCode -ne 0 -or $version.Stdout.Trim() -cne $pin.version) {
        throw "Wrong Inno compiler version; expected $($pin.version)."
    }
    return $compiler
}

function Write-InstallerFileList([string]$PayloadRoot, [string]$Destination) {
    $manifest = Test-PayloadManifest $PayloadRoot -RequireCurrentSource
    $paths = @($manifest.files.path) + @('manifest.json', 'SHA256SUMS.txt')
    $lines = foreach ($path in $paths) {
        $source = Join-Path $PayloadRoot $path
        if ($source -match '["{}\r\n]') { throw 'Inno source paths cannot contain quotes, braces or line breaks.' }
        $parent = [IO.Path]::GetDirectoryName($path)
        $target = if ($parent) { "{app}\$parent" } else { '{app}' }
        "Source: `"$source`"; DestDir: `"$target`"; Flags: ignoreversion"
    }
    [IO.File]::WriteAllText($Destination, ($lines -join "`n") + "`n", [Text.UTF8Encoding]::new($true))
    return $manifest
}
