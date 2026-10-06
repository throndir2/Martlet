# Packaging

Windows packaging builds a self-contained payload and an unsigned per-user installer.

## Public lane

`.github\workflows\windows-release.yml` is manual dispatch only. It has no push, PR, tag or schedule trigger and runs no tests. Public releases use `Martlet-<version>-win-x64.exe`, a public AppId, `%LocalAppData%\Programs\Martlet`, public notices and an unsigned installer.

## Local public path

```powershell
$sdk = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
$run = Join-Path $PWD ("artifacts\windows-public-" + [guid]::NewGuid().ToString('N'))
.\packaging\windows\Build-PublicRelease.ps1 -Version 0.1.0 -DotnetPath $sdk -OutputDirectory $run
```

## Internal lane

Internal development builds are separate and must not be uploaded as public release artifacts. They use `Publish-Windows.ps1`, `Get-InnoSetup.ps1`, `Build-Installer.ps1`, packaging tests and smoke scripts.

## Payload

The payload has separate runtime directories for Desktop, Doctor and AvatarRenderer, plus help, notices, SBOM, manifest and SHA256SUMS. The SDK is not shipped. No trimming, single-file extraction or ReadyToRun is used.

## Prerequisites tool

Both channels ship `prerequisites\Install-Prerequisites.ps1` and the **Martlet prerequisites** shortcut. The installer itself asks no setup questions.

## Evidence

Packaging produces manifests, dependency notices, provenance and CycloneDX SBOM. These are not publisher signatures or vulnerability clearance.

More detail: [Windows packaging](https://github.com/throndir2/Martlet/blob/main/packaging/windows/README.md), [Prerequisites](https://github.com/throndir2/Martlet/blob/main/docs/PREREQUISITES.md), [Delivery](https://github.com/throndir2/Martlet/blob/main/docs/DELIVERY.md).
