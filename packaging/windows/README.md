# Windows packaging (internal and unsigned public-release lanes)

**Internal lane: INTERNAL DEVELOPMENT ONLY - UNSIGNED. Not a supported installer or a completed
F02/AC-02/G1 gate.** This packages V04b real explicit typed / push-to-talk app
integration alongside the Desktop status experience and Doctor status command.
Opening the app or conversation window does not authorize requests, credential
lookup or device access. Saved configuration is not live API/account/model readiness.
Desktop V07a adds explicit same-profile configuration backup/previewed restore,
not a diagnostic ZIP or installer updater. Transactional binary upgrades,
rollback and signed distribution remain unimplemented.
Internal builds have no end-user license grant; the narrow grant for official
Release binaries is separate.
The existing foundation/planning documents retain their broader future gates.

The desktop executable, its Start menu shortcut and the installer use the original
pink Martlet bird icon. The shared WPF window style also sets it explicitly, so
development launches through `dotnet` do not show the generic runtime icon.
Artwork and favicon-ready SVG/PNG/ICO assets live in
`src\Martlet.Desktop\Assets`; regenerate the checked-in raster assets with
`.\scripts\Generate-AppIcon.ps1` before building if the SVG changes.

## Public release build (manual dispatch)

`.github\workflows\windows-release.yml` has **only** `workflow_dispatch`; it
cannot run on a push, PR, tag or schedule. There is **no hosted build-only
mode**, Actions artifact or upload/download handoff. Build-only work stays
local, using the command below. The single Windows runner runs no test, lint,
scan, smoke, repeat-publish or qualification job.

Agents and the owner may dispatch a release without further approval:

```powershell
gh workflow run windows-release.yml --repo throndir2/Martlet --ref main -f version=0.1.0
```

The `version` input must equal `<Version>` in `Directory.Build.props` on `main`
and must not already have a `v<version>` tag; bump the version in a normal PR
before each new release. The job runs only from `main` of the public repository
and on the first attempt. Before restoring dependencies it checks the version,
the dispatched `main` commit, tag absence and the binary-use grant. The runner
then uses the exact SDK, Node/npm lock and reviewed Inno compiler to restore,
compile and package once, checks the clean installer receipt and SHA-256,
creates `v<version>` at the still-current `main` commit, uploads assets to a
**draft** GitHub release and publishes it as the latest normal release, titled
`Martlet <version>`, only if the uploads succeed, GitHub reports the expected
asset SHA-256, and the tag still points at the built commit. If a step fails
after tag creation, inspect and delete the tag/draft before dispatching again;
never move a tag. A release build does not establish clean-machine
install/upgrade/rollback, device or provider qualification.

To exercise the public packaging path **locally** without uploading, run from
the repository root on Windows x64 with PowerShell 7:

```powershell
$sdk = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe' # Exact 10.0.401 SDK.
$run = Join-Path $PWD ("artifacts\windows-public-" + [guid]::NewGuid().ToString('N'))
.\packaging\windows\Build-PublicRelease.ps1 -Version 0.1.0 -DotnetPath $sdk -OutputDirectory $run
```

This builds `package\installer\Martlet-<version>-win-x64.exe` with a distinct
public AppId, fixed `%LocalAppData%\Programs\Martlet` installation directory,
public help/terms, explicit public payload channel and separate SBOM/provenance
identity. It is not a renamed internal installer. The installer is not
code-signed; signing is not a release requirement for this personal project.
The package inventories and hashes `help\LICENSE.txt` and complete upstream
notices; Inno shows the owner's binary-use terms before installation. The
release page links to the exact source-commit LICENSE and third-party dependency
inventory, without claiming ownership of third-party components.
The manifest's full assembly file version is four-part (`0.1.0.0`); the public
installer and tag use the three-part release version (`0.1.0`). The Desktop
updater reads the release list and offers the highest numbered normal
(non-draft, non-prerelease) version. The GitHub asset digest is produced on
upload; `SHA256SUMS.txt` binds the local installer and receipt but is not
publisher authentication.

The GitHub updater downloads only after user consent and verifies a same-origin
digest; it does not install the download. Windows may warn about an unknown
publisher. The existing Updates library's separate signed ZIP/envelope remains
unsupported by this installer, and rollback qualification has not been performed.

## Quick installer, first-run setup and the prerequisites tool

The installer asks no setup questions: the public channel shows the license and
release notice, then installs (no Ready page) and offers **Start Martlet and
finish setting up** on the Finished page. Its only `[Run]` entry is that
`postinstall nowait skipifsilent` launch, so silent installs and in-app updates
never start it; `Test-Packaging.ps1` enforces this and rejects a `[Tasks]`
section.

Both installer channels ship `prerequisites\Install-Prerequisites.ps1` (copied by
`Publish-Windows.ps1`, inventoried like every payload file) and a Start menu
shortcut, **Martlet prerequisites**, that runs it with Windows PowerShell. All
setup questions live in the Desktop app. On first run the welcome tour asks only
what the PC is for and installs nothing. Missing items are detected from the
registry and files without side effects (`Prerequisites.cs`): WebView2 runtime,
blocked microphone access, Windows speech for the display language, Ollama and
WSL 2 + Docker Desktop. The setup advisor's plan adds an **Install on this PC**
button for the items its layout runs here, and Thinking's *This PC* installs
Ollama with its model. Each passes the needed IDs to the tool with
`-NoPrompt -Install ...`, hidden, and shows its output in a Martlet run window
(no console; elevated steps stay hidden too); **Prerequisites (check / install)**
on Settings › Tools shows the full checklist in Martlet. Martlet stays a
per-user app; only the speech and WSL steps ask for UAC, and winget's Docker
installer asks on its own.
The full list of what is bundled, offered or user-supplied is in
[docs/PREREQUISITES.md](../../docs/PREREQUISITES.md).

**Local qualification is optional for this prototype** (owner policy,
2026-09-26). This host once saw a pinned `esbuild.exe` crash (`0xc0000005`);
a locked reinstall resolved it. Never skip the real renderer build, and report
unrun clean-Windows install/upgrade/rollback checks as NOT RUN.

## Local internal development build

Keep an internal candidate separate from the public release lane. On an
authorized local Windows x64 host with PowerShell 7, SDK 10.0.401,
Node 20.11.1 and npm 10.2.4:

```powershell
$sdk = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe' # Or another verified 10.0.401 SDK.
$node = (Get-Command node).Source
$run = Join-Path $PWD ("artifacts\windows-internal-" + [guid]::NewGuid().ToString('N'))
npm ci --prefix src\Martlet.Avatar.Vrm --ignore-scripts --no-audit --no-fund
if ($LASTEXITCODE -ne 0) { throw "npm ci failed ($LASTEXITCODE)." }
.\packaging\windows\Publish-Windows.ps1 -DotnetPath $sdk -NodePath $node -CliHome (Join-Path $run 'cli-home') -OutputDirectory (Join-Path $run 'publish')
.\packaging\windows\Get-InnoSetup.ps1 -Destination (Join-Path $run 'inno')
.\packaging\windows\Build-Installer.ps1 -PayloadRoot (Join-Path $run 'publish\payload') -BuilderDirectory (Join-Path $run 'inno') -OutputDirectory (Join-Path $run 'package')
```

The local `package\installer` directory persists with
`Martlet-<four-part-version>-win-x64-INTERNAL-UNSIGNED.exe` (initially
`Martlet-0.1.0.0-win-x64-INTERNAL-UNSIGNED.exe`), `installer-manifest.json` and
`SHA256SUMS.txt`. These commands build/package only: they do not run the
separate local repeat-publish, test, smoke or clean-machine gates below. They do
not upload or publish anything. Do not distribute the internal candidate.

The internal AppId, name/channel and unsigned manifest remain unchanged.
Never upload this internal build as the public release artifact. Its file version
is four-part, its application identity is separate, and its help explicitly
labels it internal. Public releases use the separate release lane above, not
a relabeled development build.

## Developer commands

All validation below runs locally under the
[repository policy](../../README.md#local-only-validation-policy). Keep every
package assertion, repeat-publish comparison and smoke gate. The retained
`Test-WorkflowExit.ps1` exercises the former GitHub pwsh wrapper semantics as a
local regression; it neither invokes Actions nor requires a workflow definition.

Run from the repository root on Windows x64 with PowerShell 7 and the exact
SDK in `global.json` (10.0.401). The scripts accept an explicit, read-only
`-DotnetPath` and set `DOTNET_ROOT`, process PATH, telemetry opt-out and certificate
generation only in the calling process. Supply your own `-CliHome`; never use a
shared SDK directory as a CLI home. No SDK/driver/runtime installer is run.
Avatar browser builds additionally require the reviewed existing Node 20.11.1,
npm 10.2.4 and locked esbuild 0.25.12. Restore the VRM module's committed npm
lock locally with `npm ci --prefix src\Martlet.Avatar.Vrm --ignore-scripts
--no-audit --no-fund`; the pinned Windows esbuild package supplies its executable.
No global Node/npm installation is performed. `Publish-Windows.ps1 -NodePath`
selects the exact Node executable for both the normal host build and evidence
collection. Use a short process-local PATH if a machine's inherited PATH exceeds
Windows cmd limits; never replace the machine PATH.

Each bounded native child receives a validated filesystem working directory.
Build/maintenance wrappers explicitly use their repository root; other calls
use PowerShell's current filesystem location, not the process-wide OS cwd.
Starting PowerShell elsewhere and then using `Set-Location`/`Push-Location`
therefore preserves project, SDK and NuGet configuration resolution. No command
changes `[Environment]::CurrentDirectory`. Native commands from non-filesystem
locations (such as `Env:`) fail with an actionable directory requirement.

```powershell
$sdk = (Get-Command dotnet).Source # Or an explicit verified SDK dotnet.exe.
$node = (Get-Command node).Source # Existing reviewed Node 20.11.1.
$run = Join-Path $PWD ("artifacts\windows-" + [guid]::NewGuid().ToString('N'))
$cliHome = Join-Path $run 'cli-home'
$first = Join-Path $run ("publish one " + [char]0x00E9)
$second = Join-Path $run ("publish two " + [char]0x00E9)
$builder = Join-Path $run 'inno'

.\packaging\windows\Publish-Windows.ps1 -DotnetPath $sdk -NodePath $node -CliHome $cliHome -OutputDirectory $first
.\packaging\windows\Publish-Windows.ps1 -DotnetPath $sdk -NodePath $node -CliHome $cliHome -OutputDirectory $second
.\packaging\windows\Get-InnoSetup.ps1 -Destination $builder
.\packaging\windows\Test-Packaging.ps1 -DotnetPath $sdk -NodePath $node -CliHome $cliHome -PayloadRoot "$first\payload" -ComparePayloadRoot "$second\payload" -PublishDirectory $first -BuilderDirectory $builder -WorkDirectory "$run\tests"
.\packaging\windows\Smoke-Package.ps1 -PayloadRoot "$first\payload" -InteractiveDesktop
.\packaging\windows\Build-Installer.ps1 -PayloadRoot "$first\payload" -BuilderDirectory $builder -OutputDirectory "$run\package"
# Also exercise the actual GitHub pwsh exit wrapper (includes assertions, Doctor and compiler).
.\packaging\windows\Test-WorkflowExit.ps1 -DotnetPath $sdk -CliHome $cliHome -PayloadRoot "$first\payload" -ComparePayloadRoot "$second\payload" -BuilderDirectory $builder -WorkDirectory "$run\workflow-tests" -InteractiveDesktop
```

These are build commands, **not novice installation instructions**. Initial
restore downloads packages from the root NuGet sources. Publishing also obtains
small checksum-pinned notices from official source tags. Compiler acquisition is
an explicit, separate ~14 MB download from the official Inno GitHub release.
No cloud/model calls, release uploads, or billable services are involved.

All output/work directories must be new absolute paths. Existing paths are
rejected rather than recursively cleaned/reused. Use the already ignored
`artifacts` directory or a session-owned temporary location; never commit binary
outputs. A failed publish leaves diagnostic `build`/`staging` files but **no
completed `payload`**. Compilation similarly promotes `staging` to `installer`
only after successful compilation and checks. Do not distribute staging files.

Repository paths and publish/lock-maintenance/test output paths containing a
comma (`,`), semicolon (`;`), percent (`%`) or equals sign (`=`) are **not
supported**. These are delimiters/escape syntax in MSBuild property values or
the compiler's nested PathMap grammar. The wrappers reject them before creating
output, rather than silently interpreting a different path. This includes
escape-looking names such as `%2C` or `%3B`. Spaces and Unicode are supported.

`Smoke-Package.ps1` always launches the actual native Doctor apphost. The
`-InteractiveDesktop` switch additionally launches the native WPF apphost, reads
first-run status and exercises the accessible close path.
It is deliberately opt-in on interactive developer desktops; without the switch,
the local smoke runs Doctor only, not a simulated interactive or clean-OS claim.
Both programs receive a unique temporary Unicode data path. The smoke checks read-only launch and
existing/malformed settings preservation, snapshots real settings without
displaying their contents, and fails on unexpected writes.
Interactive packaging requires both the original general Desktop scenario and
an additional `Smoke-Desktop.ps1 -CompanionOnly` scenario, each in a fresh process
with isolated data and the unchanged 180-second child deadline. The companion
scenario repeats real no-key setup/migration/route checks and verifies editor
save/restart and read-only opening, plus passive memory management with every
fact/export permission OFF and no store creation. This avoids accumulating fixed Windows UIA
RPC latency in the original general scenario; neither scenario's assertions or
deadline is relaxed. Use `-Verbose` on the direct Desktop script for control-level
timings.
The workflow-exit regression also exercises nonzero exits and actual 180-second
timeouts for each controlled native scenario, requiring the combined gate to fail
in all four cases. Those controlled failures are wrapper tests, not app passes;
the interactive command separately requires both actual Desktop scenarios.

## One payload and installation layout

```text
payload\
  Desktop\Martlet.Desktop.exe, *.dll, *.deps.json, *.runtimeconfig.json, ...
  Desktop\AvatarRenderer\Martlet.Avatar.RendererHost.exe, complete private runtime, ...
  Desktop\AvatarRenderer\web\app.js, app.js.LEGAL.txt, index.html, THIRD-PARTY-NOTICES.txt
  Doctor\Martlet.Doctor.exe, *.dll, *.deps.json, *.runtimeconfig.json, ...
  help\INTERNAL.txt
  help\TROUBLESHOOTING.md
  notices\DEPENDENCIES.txt, upstream licenses/notices
  sbom.cdx.json
  manifest.json
  SHA256SUMS.txt
```

Each entry point, including the private renderer host, owns its complete runtime directory. There is **no merge** of
Desktop and Doctor publish files and no dependence on their relative DLL search
paths. This duplicates some runtime bytes deliberately: the installed layout is
simple and avoids conflicting WPF/Core runtime facades. No trimming, single-file
extraction or ReadyToRun compilation is used. The SDK is not shipped.

The exact authored assembly inventories are checked both on disk and in each
application's `.deps.json` (including each project's runtime asset):

- Both: `Martlet.Core`, `Martlet.Audio`, `Martlet.Diagnostics`.
- Desktop only: `Martlet.Desktop`, `Martlet.Credentials.Windows`,
  `Martlet.Conversation`, `Martlet.Providers`, `Martlet.Participation`,
  `Martlet.Support`, `Martlet.Memory`.
- Desktop additionally: `Martlet.Avatars`, `Martlet.Avatar.Hosting` and
  `Martlet.Avatar.Audio2Face`. The Audio2Face client brings only its reviewed
  protobuf/gRPC managed runtime closure; Grpc.Tools is a separate verified
  restore-only build input.
- Private renderer: `Martlet.Avatar.RendererHost`, `Martlet.Avatar.Hosting`,
  `Martlet.Avatars`, `Martlet.Core` and the exact WebView2 SDK assets. It has no
  conversation, credential or Audio2Face reference. The parent application's
  ordinary publish target owns this subtree; packaging never substitutes a
  different application.
- The composition renderer alone targets `net10.0-windows10.0.19041.0` and owns
  `Microsoft.Windows.SDK.NET.dll` plus `WinRT.Runtime.dll` from the exact
  `Microsoft.Windows.SDK.NET.Ref/10.0.19041.57` archive (`lib/net8.0`).
  Restore evidence retains that precise SDK framework download independently of
  .NET runtime 10.0.12. The runtime dependency target remains
  `.NETCoreApp,Version=v10.0/win-x64`; Desktop/Doctor retain their original TFMs.
  The packaging import scopes the renderer's runtime pins to framework items
  instead of applying the .NET runtime version to SDK projection profiles.
  The SDK archive contains no embedded license: the official nuspec license URL's
  RTF redirect destination is separately pinned and copied unmodified.
  Update readers retain legacy schema 1/2 and pre-composition schema 3 acceptance;
  only this renderer may use the versioned restore target and SDK runtime pack.
- Doctor only: `Martlet.Doctor`. Doctor's graph and offline semantics are
  unchanged; no conversation, provider, participation or vault assembly is
  included there.

V06b already introduced `Martlet.Support` for explicit local troubleshooting;
V07a recovery stays inside existing Core/Desktop, with no new assembly or
dependency. Support bundles remain deliberately lossy and cannot restore
configuration. Installed help explains the separate recovery workflow.
Conversation, Providers and Participation are authored BCL-only integration
code; they add no production NuGet package, provider SDK, native binary or
redistributed model. Existing audio/runtime pins and complete upstream notices
remain unchanged. Missing project DLLs or dependency/runtime entries and
unexpected Martlet assemblies fail packaging even before checksum validation.

The internal installer uses stable AppId
`{CDFDFAB4-DAF1-4A6D-8823-A55E0A12CD86}` and fixed
`%LocalAppData%\Programs\Martlet Internal`. This identity is reserved for the
internal channel. The distinct unsigned public channel uses AppId
`{7EA5CC4A-8BF4-4412-ABE1-90819303FEAB}` and fixed
`%LocalAppData%\Programs\Martlet`. No registration is created by merely
compiling either installer.

Only native x64 Windows, build 19041 (Windows 10 2004) or later, is allowed by
authoring. That floor matches the avatar renderer's Windows SDK target so any
current Windows 10 or 11 installs; Windows 11 25H2 remains the main test target.
It is **not** an assurance about future Windows versions or a passed OS support
matrix. ARM64 emulation is excluded.
`PrivilegesRequired=lowest` has no elevation override. Command-line `/DIR`
overrides and previous installer locations cannot redirect the installation.

Start menu entries expose Desktop, a persistent command window for Doctor,
local help, and the normal registered uninstaller. There is no automatic app
launch after installation, login startup, firewall exception, service, driver,
provider configuration, credential/model download, or registry authoring beyond
Inno's normal per-user uninstall/installation bookkeeping.

Mutable data remains `%LocalAppData%\Martlet`; it is neither an installer input
nor a destination. The file list is generated **only** from the verified payload,
one explicit `[Files]` entry per file, without globs/optional-source flags. There
are no `[InstallDelete]` or `[UninstallDelete]` sections and no recursive deletion
code. Inno uninstalls its recorded files and removes empty owned directories,
not unrecorded user files. No data purge option is provided.

Same-version reinstall replaces the same owned files and shortcuts; close both
apps first. This is an idempotent **skeleton**, not an application-aware repair
engine. No installer-driven schema migration, backup, version activation, stale-file pruning,
downgrade, interrupted-upgrade recovery or transaction rollback is claimed.
Those are V07 gates and must not be inferred from successful compilation.

## Pins, locks, provenance and integrity

`toolchain.json` pins SDK 10.0.401, runtime 10.0.12, RID, native runtime version,
SHA-512 of both exact runtime NuGet archives, and SHA-256 of external notices.
Ordinary packages remain centrally pinned by the existing root files. During
publishing, runtime payload bytes are compared against the pinned archives
using the owning runtime pack from each application's actual dependency graph.
WindowsDesktop can legitimately replace Core facade assemblies such as
`WindowsBase.dll`; files are never accepted merely because two names match.

The opt-in absolute `CustomBeforeMicrosoftCommonTargets` and
`CustomBeforeMicrosoftCommonCrossTargetingTargets` imports set
`NuGetLockFilePath` to `packaging\windows\locks\<project>.packages.lock.json` for
every project in both graphs, including multi-target Audio/Doctor outer builds,
and scope the renderer's .NET and Windows SDK runtime versions independently.
Doctor publishes explicitly with `--framework net10.0-windows`; single-targeted
Desktop uses its authored TFM so a global TFM cannot override its renderer child.
portable Doctor remains an SDK text-only target, never a fake audio backend.
RID restore needs a different target graph from
the platform-neutral foundation, so there are two intentional lock sets.
Normal foundation commands/imports/locks are unchanged. Before ordinary locked
restore, the wrapper generates the real restore graph and requires every RID
lock to exist. Missing locks cannot silently become newly generated locks.
Stale locks fail NuGet's locked-mode validation.

After a deliberate production dependency or runtime change, the owning engineer
must coordinate central pins, refresh/review the normal locks and use:

```powershell
.\packaging\windows\Update-PublishLocks.ps1 -DotnetPath $sdk -CliHome $cliHome -WorkDirectory "$run\lock-maintenance"
```

Review/commit all affected RID locks and any new notices/native inventory, then
rerun ordinary locked publishing and the local foundation checks. Only this
explicit maintenance command uses `--force-evaluate`; ordinary
publish/validation never regenerates locks. Framework runtime archives are also
pinned separately because NuGet
framework downloads are not represented as ordinary package lock dependencies.

Schema-v3 payload manifests contain a sorted complete relative file inventory,
lengths, SHA-256s, versions, source HEAD/dirty state and the unsigned build
evidence described below, with no timestamp or absolute build path.
Generated C# file-local type paths are mapped to stable paths so repeated clean
output directories can be compared without incidental source-directory names.
`SHA256SUMS.txt` covers all payload files **and** the manifest. Installer output
has its own manifest/checksums including the payload manifest hash and toolchain
pins. Checksums detect accidental corruption; they are not signatures. A dirty
source flag is honest local evidence, not proof that HEAD contains those inputs.
Neither whole-build reproducibility nor byte-identical installers across
machines/times are established by deterministic metadata.

### Offline provenance and CycloneDX SBOM (V07c foundation)

Every new payload requires `sbom.cdx.json`, a **CycloneDX 1.6 JSON** software
bill of materials, plus the manifest's `provenance` object. Legacy schema-v1
payloads lack this evidence and must be rebuilt; there is no allow-missing
option. Each channel records an **unsigned build observation** with distinct
channel/assurance/SBOM identity; neither is publisher attestation, a SLSA
level, a release signature, license clearance or a vulnerability assessment.
Code signing is not a release requirement for this personal project; public
releases ship an unsigned installer without any authentication claim.

The avatar producer explicitly selects manifest 3 / provenance 2. Its coordinated
[signed-candidate metadata reader](../../src/Martlet.Updates/README.md) must support
that pair before integration; older readers reject manifest 3 as incompatible.
Legacy manifest 1 and manifest 2 / provenance 1 remain explicit reader/history
paths, and legacy pure constructors preserve their historical bytes. None is an
allow-missing path for the current producer. Candidate signing, activation,
settings compatibility and trust policy do not change. The reader's depth-16
and restricted ZIP/layout budgets remain narrower than the producer's general
bounded JSON reader.

The three application descriptors bind exact root project, directory and private
parent. Each application's reviewed project/package graph is separate; no global
union is accepted as an application's closure. `buildOnlyLibraries` is required
empty in all three contexts: the host is a build project reference, not a hidden
Desktop resolved-library omission. Its three actual WebView2 SDK-injected
`reference` identities are explicitly mapped to the single verified SDK NuGet
archive, not invented package IDs. DLL/XML pairs and the root/nested x64 loader
copies have eight fixed paths and archive origins. Extra architectures, copies,
XML or binaries cannot be relabeled as project output.

The normal browser build retains its source/output receipt and raw esbuild
metafile in private-host intermediate output. Packaging independently verifies
the exact npm lock's tarball SHA-512, consumed package-relative input bytes,
package metadata and complete runtime license files; it verifies all four
published browser files against the actual build receipt. The closed 17-package
graph distinguishes 15 runtime packages (including type-only packages with
notices but no bundle bytes) from esbuild and its Windows build executable.
The two build-script package inputs are exactly esbuild's `lib/main.js` and
the Windows package's `esbuild.exe`; the latter's archive-verified hash and
length must equal the observed tool fingerprint. No runtime package can be
relabeled as a build script to evade bundle ownership.
Source materializations under the two exact module node_modules directories and
the host's exact web/dist directory are excluded only with this compensating
input/output evidence. Arbitrary vendor/dist trees are not ignored.

CycloneDX records actual npm identities and dependencies, source/notice hashes,
the transformed bundle's hashes and contributor links without duplicating
physical file ownership. Complete bundled JavaScript licenses and esbuild legal
comments remain distributed; npm metadata alone is not notice coverage.
Live2D Framework/Core overrides, user models/avatar assets, NIM and GPU drivers
remain external prerequisites and are never packaged. The WebView2 runtime is
never packaged either; the bundled prerequisites tool (also **Prerequisites** in
the Desktop's Settings › Tools) installs it from Microsoft only when it is missing and
ticked (see above).
No license clearance is inferred.

Retained npm archives and normalized build evidence reside outside the payload.
Their reuse by local regression probes verifies identical bytes, never silently
overwrites evidence or changes pins. A normal publish still requires a fresh
absolute output directory and exact committed packaging RID locks. The
development RID-lock fallback under obj is not packaging evidence; nested host
restores must honor the explicit packaging lock imports. Normal source locks
remain unchanged.

`New-PackageProvenanceDocument`, `New-PackageSbomDocument` and
`New-PayloadManifestDocument` are shared pure document constructors used by the
normal production functions and the inert cross-boundary test. They are not
validation entrypoints. `Get-PackageProvenance`, `Get-PackageSbom`,
`Write-PackageSbom`, `Write-PayloadManifest` and `Test-PayloadManifest` retain their
actual input/layout/provenance/SBOM checks; callers must not replace those
validated workflows with a constructor or test fixture. Constructor-only
conformance runs neither publish nor qualify executables, source/tool
observations or upstream rights.

The existing manifest is still the sole file inventory. The SBOM describes
each actual application/help/notice file and its SHA-256, alongside the actual
resolved project, managed-package and self-contained runtime graph. Desktop
and Doctor remain separate contexts: matching NuGet identities do not erase
different dependency edges or duplicate physical files. File containment and
archive-entry evidence are distinct from component dependency relationships.
Generated apphosts and dependency/runtime configuration are identified as
generated files, not falsely claimed to equal upstream archive entries.
WindowsDesktop facade replacements retain their actual owning pack.
Satellite resource assemblies absent from `.deps.json` are matched to exact
entries and bytes in the already pinned runtime archive.

The source receipt records commit/tree, dirty state and observed
repository-relative input paths, lengths and hashes, not source contents.
Publishing compares this snapshot before and after work; changes prevent
completed-payload promotion. Selected dotnet/MSBuild/compiler file fingerprints
record what was observed locally, not the integrity of an entire installed SDK
distribution. NuGet lock content hashes are recorded separately from raw
signed archive SHA-512s. Actual restore assets, committed RID locks, published
dependency metadata and verified archive entries must agree. Build-only tools
and references are not mislabeled as shipped packages.

Upstream license expressions are **declared**, not concluded or approved.
Missing or ambiguous declarations remain unknown/not assessed; a notice file
does not grant Martlet's project license. Optional BOM document licensing is
omitted. CycloneDX 1.6 was selected because it supports the required JSON
component/file/relationship facts without SPDX 2.3's mandatory CC0 metadata
declaration. No new project or asset rights are granted.

The SBOM excludes itself, `manifest.json` and `SHA256SUMS.txt` from its file
subjects to avoid circular hashes. The manifest inventories and hashes the
SBOM; `SHA256SUMS.txt` covers those files and the manifest. The existing
installer receipt's payload-manifest hash therefore also binds the SBOM.
Installer entries still enumerate every checked file exactly once.

`Test-PayloadManifest` checks retained evidence offline, without the original
SDK, NuGet cache or build directory. It reconstructs the canonical SBOM from
the validated evidence and actual payload and requires identical bytes.
Installer file-list/build handoff additionally requires a source receipt
matching the current checkout. Use an unchanged matching checkout to compile
an installer; rebuild after source changes. Copying an intact payload to a
different space/Unicode path does not invalidate portable inspection.

Malformed, oversized, duplicate, missing, extra, stale or inconsistent evidence
is an error with a rebuild remedy, not a partial successful inventory. Metadata
sorts object keys recursively with `StringComparer.Ordinal`, preserves array
order and uses UTF-8/LF with no random serial number or wall-clock timestamp.
The bounded JSON reader constructs ordered objects directly from the parsed
JSON tree rather than relying on `ConvertFrom-Json -AsHashtable` ordering
(which differs before PowerShell 7.3). Canonical serialization also normalizes
caller-supplied unordered dictionaries.
JSON member names such as `Keys`, `Values` and `Count` remain data: dictionary
introspection uses intrinsic members so parsing cannot silently discard fields.
PowerShell-reserved member names remain explicit errors in PSCustomObject mode;
dictionary mode retains them without treating them as collection metadata.
PowerShell 7.0 remains the declared
minimum; native 7.0/7.2 execution has not been qualified by current-host tests.
Earlier unpublished v2 receipts whose internal hashes used insertion-ordered
object keys require a fresh publish; their original evidence is not rewritten.
This is the internal format's ordering policy, not a claim of RFC 8785 numeric
canonicalization. Matching metadata from repeated local inputs is not proof of
hermetic compilation or reproducible binaries. Coordinated rewriting of an
unsigned payload and all its receipts cannot be authenticated by checksums.
The inventory covers actual packaged files and the resolved .NET graph, not a
complete decomposition of upstream vendored/native internals, the operating
system, models or remote services.

Every shipped `.dll`/`.exe`, regardless of extension or directory-name case,
must have verified archive or authored/generated application ownership. An
unowned binary cannot fall through to the SBOM's document classification.
Other published Desktop content must be authored, the pinned Live2D SDK files,
or one of the bundled voice recognition models in `toolchain.json`
`voiceModels`, whose exact bytes and SHA-256 the layout check requires.
Framework download identity/version sets must match the actual NuGet-generated
`build\obj\<project>\<project>.csproj.nuget.dgspec.json` for every project/TFM,
in addition to preflight graph/lock checks. The actual restore specification
contains SDK-injected downloads for referenced projects that may be absent
from the initial preflight graph. It is required evidence, never a fallback to
an empty set. Matching absent/empty declarations remain empty; null, duplicate,
missing or changed declarations fail. Each shipped runtime pack must also have
matching entry-point framework-download evidence.

Supply `-PublishDirectory` to `Test-Packaging.ps1` to additionally exercise
retained real restore assets/specifications against committed locks. Omitting it reports
those input-mutation cases as NOT RUN; it does not fabricate equivalent
coverage from the shipped graph. Package acceptance includes this parameter.
Schema conformance is checked locally against the published CycloneDX 1.6 JSON
schema, with its references resolved from retained local schema files, never a
remote validation service.

Production-path assertions cover omitted executables/framework/dependency/
notice files, changed bytes/checksums/RID/PE architecture, data inclusion,
explicit installer entries, pinned authoring invariants, output overwrite,
missing SDK/compiler, actual publish failure, missing/stale RID locks, Unicode/
spaced source and output paths, and repeat-publish manifest equality.
Reserved punctuation/percent-escape-looking output paths must fail preflight
without creating output. Native command status is captured per process and
verified explicitly; expected negative tests never modify `LASTEXITCODE`.
`Test-WorkflowExit.ps1` runs the real assertions and Doctor in a child process
with GitHub's exact pwsh exit wrapper, then compiles the installer only after
that step succeeds. A deliberately missing executable must still fail the
same child wrapper; no unconditional success exit masks a broken assertion.
The suite also starts a maintenance child outside a disposable source copy,
uses `Set-Location` into that copy, and regenerates both real lock graphs.
It checks the resulting locks, pinned SDK/global.json and effective repository
NuGet sources, rejects non-filesystem directories, and asserts caller/process
working directories are unchanged. Effective sources include SDK-declared
`RestoreAdditionalProjectSources` (for example, an installed workload's local
`library-packs` source), not just the XML source list. A second child exercises
that SDK behavior with an empty test-only folder; it never modifies the SDK or
the caller's environment. Source matching remains exact, with no wildcard
allowance for arbitrary local/network feeds.

## V04b integration, fixtures and dependency evidence

The RID locks cover Core, Audio, Diagnostics, Credentials.Windows,
Conversation, Providers, Participation, Support, Desktop and Doctor.
The three V04b library locks and Desktop's added project edges are generated
by the existing maintenance helper, not hand-authored placeholder locks.
Normal locks remain separate; maintenance regression checks hash
every normal source lock before/after regeneration of **all** copied RID locks.
The multi-target outer-build import is necessary: without it, NuGet can write
RID targets into normal Audio/Doctor locks. Both import modes are preflighted;
missing or stale locks remain errors, not automatically repaired by publishing.

`toolchain.json` additionally pins the raw official archives for the already
selected NAudio.Wasapi/Core 3.1.0 and System.Numerics.Tensors 9.0.0. Publishing
compares each app's actual DLL bytes with its pinned package asset and checks
the version in the actual dependency graph. No root package/SDK/source upgrade
is involved. Raw signed-archive SHA-512 is intentionally distinct from NuGet's
normal lock `contentHash`. The NAudio root MIT notice, bundled Core/Wasapi
third-party attributions, and complete Tensors 9.0.0 license/notices accompany
the runtime's own notices. The published binary-use grant is in `LICENSE`;
these upstream notices do not assess third-party redistribution rights.
Missing new assemblies/notices, wrong dependency versions and
corrupt archives have production-path negative coverage.

The real app path is explicitly separate from **FIXTURE - NOT AI**. Setup saves
configuration only. In **Real API conversation**, reload saved choices and
review the supported route IDs and one-action data/cost/output envelope.
**Send typed text** requires fresh action consent, not a saved-profile permission;
generated-voice output starts OFF. PTT additionally requires separate local
microphone and STT-upload permission; release sends the bounded recording
(accessible Invoke uses **Finish recording and send**). Audio may reach STT and
incur charges even when participation policy later suppresses an answer.
**Stop / revoke this action**, pause/mute, focus loss, session lock and close
revoke pending work. Cleanup may still own the slot; no overlapping action,
automatic paid retry or old-audio replay is promised. Retry needs new consent.
Typed fallback and retained response text remain available when the corresponding
microphone/STT or output path fails; refusal is not ordinary generated speech.

`Smoke-Package.ps1` reuses the canonical executable smokes in bounded child
PowerShell processes with runtime-discovery variables still pointed away from
the SDK. Smoke and packaging validation must never authorize live requests,
vault effects, microphone capture or generated-voice playback.

## Inno Setup provenance and terms

Verified 2026-09-12: official **Inno Setup 7.1.0 x64**, from the
[upstream release](https://github.com/jrsoftware/issrc/releases/tag/is-7_1_0),
linked by the [official downloads page](https://jrsoftware.org/isdl.php).
Archive SHA-256:
`0362a383ed217d4c4239b5933866dd96d3eb2102737da92f80f6057a4b40df2f`.
Windows Authenticode reported **Valid**, signer
`CN=Pyrsys B.V., O=Pyrsys B.V., S=Noord-Holland, C=NL`.

The upstream `setup.iss` / `isportable.iss` for that tag implement
`/CURRENTUSER /PORTABLE=1`: no uninstall registration, shortcuts or file
association. The wrapper uses a fresh session-local directory, no elevation,
silent/no-restart flags and no machine PATH edits. It verifies the archive hash
and Authenticode **before execution**. A pinned extraction receipt covers every
compiler file; builds verify the receipt and all bytes before running ISCC.
`ISCC --version` must report exactly 7.1.0 (its PE version resource is 0.0.0.0,
so it is not used as the compiler version). No substitute compiler/placeholder
is used. No compiler is installed with Martlet.

The [tagged license](https://github.com/jrsoftware/issrc/blob/is-7_1_0/license.txt)
permits use, including commercial use, subject to retained notices/origin terms.
The current [commercial FAQ](https://jrsoftware.org/isorder.php) requests
commercial licenses but says purchase is not strictly required and may wait
until production. No purchase or commercial-distribution decision is made here.
The original Inno copyright/about notices are unchanged, and its license is
included with the payload. Microsoft runtime licenses and full upstream notices
are also retained. This is a dependency inventory, not a release SBOM or a grant
over Martlet code.

## Evidence boundary and required VM follow-up

Full payload gates (two locked self-contained publishes, native CLI/WPF smoke,
integrity/negative coverage, repeat-publish equality and real Inno compilation)
are available on the Windows developer host but are not required per change
during the prototype phase. The former dedicated hosted lane is removed.
These developer commands neither install Martlet nor upload releases/artifacts.
A minimal remote build/release workflow may package and publish releases but
must not absorb these validation gates.

**Not run:** clean standard-user Windows with no SDK/preinstalled .NET; actual
install/uninstall/reinstall/repair; Start menu/registered uninstall operation;
long consumer account paths; disk-full/cancel/locked-file scenarios;
interrupted upgrades or rollback; SmartScreen/reputation/signing; novice or
screen-reader qualification; denied-egress or live provider/audio qualification.
For V04b, **real API/account or vault effects, physical microphone/speaker
trials, clean-Windows installation, live first trial, signing and release are
NOT RUN**. Deterministic fake-port tests are not those
external trials; implemented app controls are not evidence that they passed.
No isolated consumer VM was supplied. Never use this shared developer host as
a disposable install test or install into its real app/data locations.

In a separately authorized disposable Windows 11 25H2 x64 VM, record exact OS,
installer/hash and account; exercise clean standard-user install, launch via
both shortcuts, profile creation, same-version reinstall and registered
uninstall. Verify profile bytes survive and no unwanted startup/firewall/
privileged changes occur. Then add failure/long-path/version scenarios and
signed-release gates under V07. Until those pass, this remains an internal
skeleton regardless of green compilation.
# Explicit protected-data smoke scope (V06b)

`Smoke-Package.ps1` and `Test-WorkflowExit.ps1` accept an optional
`-ProtectedDataDirectory ABSOLUTE_LOCAL_DIRECTORY`. Use a separate test-owned
sentinel directory containing known settings bytes and unrelated ordinary files
when ordinary user-profile inspection is not authorized. The override validates
local non-root/no-reparse/disjoint paths before launching children and snapshots
only that scope (at most 32 files / 1 MiB each), never the ordinary profile.
The same final byte/metadata assertion fails on deliberate modification without
restoring it. Existing callers omitting the override retain the original
ordinary-profile preservation check. Receipts with the override mean **selected
protected scope unchanged; ordinary profile not inspected**, not proof that
the ordinary profile was inspected or preserved. All application launches still
use separate explicit unique disposable data directories. Do not put the
sentinel inside payload/build/CLI-home/output or app-data scopes.
