# Internal Windows packaging (F02 skeleton)

**INTERNAL DEVELOPMENT ONLY - UNSIGNED. Not a supported installer or a completed
F02/AC-02/G1 gate.** This packages the current offline WPF status shell and Doctor.
It does not implement AI, audio, onboarding, providers, transactional upgrades,
rollback or signed distribution. No project code/asset license is granted.
The existing foundation/planning documents retain their broader future gates.

## Developer commands

Run from the repository root on Windows x64 with PowerShell 7 and the exact
SDK in `global.json` (10.0.401). The scripts accept an explicit, read-only
`-DotnetPath` and set `DOTNET_ROOT`, process PATH, telemetry opt-out and certificate
generation only in the calling process. Supply your own `-CliHome`; never use a
shared SDK directory as a CLI home. No SDK/driver/runtime installer is run.
Each bounded native child receives a validated filesystem working directory.
Build/maintenance wrappers explicitly use their repository root; other calls
use PowerShell's current filesystem location, not the process-wide OS cwd.
Starting PowerShell elsewhere and then using `Set-Location`/`Push-Location`
therefore preserves project, SDK and NuGet configuration resolution. No command
changes `[Environment]::CurrentDirectory`. Native commands from non-filesystem
locations (such as `Env:`) fail with an actionable directory requirement.

```powershell
$sdk = (Get-Command dotnet).Source # Or an explicit verified SDK dotnet.exe.
$run = Join-Path $PWD ("artifacts\windows-" + [guid]::NewGuid().ToString('N'))
$home = Join-Path $run 'cli-home'
$first = Join-Path $run ("publish one " + [char]0x00E9)
$second = Join-Path $run ("publish two " + [char]0x00E9)
$builder = Join-Path $run 'inno'

.\packaging\windows\Publish-Windows.ps1 -DotnetPath $sdk -CliHome $home -OutputDirectory $first
.\packaging\windows\Publish-Windows.ps1 -DotnetPath $sdk -CliHome $home -OutputDirectory $second
.\packaging\windows\Get-InnoSetup.ps1 -Destination $builder
.\packaging\windows\Test-Packaging.ps1 -DotnetPath $sdk -CliHome $home -PayloadRoot "$first\payload" -ComparePayloadRoot "$second\payload" -BuilderDirectory $builder -WorkDirectory "$run\tests"
.\packaging\windows\Smoke-Package.ps1 -PayloadRoot "$first\payload" -InteractiveDesktop
.\packaging\windows\Build-Installer.ps1 -PayloadRoot "$first\payload" -BuilderDirectory $builder -OutputDirectory "$run\package"
# Also exercise the actual GitHub pwsh exit wrapper (includes assertions, Doctor and compiler).
.\packaging\windows\Test-WorkflowExit.ps1 -DotnetPath $sdk -CliHome $home -PayloadRoot "$first\payload" -ComparePayloadRoot "$second\payload" -BuilderDirectory $builder -WorkDirectory "$run\workflow-tests"
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
the accessible first-run status and closes the window with bounded deadlines.
It is deliberately opt-in on interactive developer desktops; hosted CI runs
Doctor only, not a simulated interactive or clean-OS claim. Both programs receive
a unique temporary Unicode data path. The smoke checks read-only launch and
existing/malformed settings preservation, snapshots real settings without
displaying their contents, and fails on unexpected writes.

## One payload and installation layout

```text
payload\
  Desktop\Martlet.Desktop.exe, *.dll, *.deps.json, *.runtimeconfig.json, ...
  Doctor\Martlet.Doctor.exe, *.dll, *.deps.json, *.runtimeconfig.json, ...
  help\INTERNAL.txt
  notices\DEPENDENCIES.txt, upstream licenses/notices
  manifest.json
  SHA256SUMS.txt
```

Each entry point owns its complete runtime directory. There is **no merge** of
Desktop and Doctor publish files and no dependence on their relative DLL search
paths. This duplicates some runtime bytes deliberately: the installed layout is
simple and avoids conflicting WPF/Core runtime facades. No trimming, single-file
extraction or ReadyToRun compilation is used. The SDK is not shipped.

The internal installer uses stable AppId
`{CDFDFAB4-DAF1-4A6D-8823-A55E0A12CD86}` and fixed
`%LocalAppData%\Programs\Martlet Internal`. This identity is reserved for the
internal channel; deciding a future public channel/identity remains release
work. No registration is created by merely compiling the installer.

Only native x64 Windows, build 26200 (Windows 11 25H2) or later, is allowed by
authoring. That minimum is a target/preflight, **not** an assurance about future
Windows versions or a passed OS support matrix. ARM64 emulation is excluded.
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
engine. No schema migration, backup, version activation, stale-file pruning,
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

The opt-in absolute `CustomBeforeMicrosoftCommonTargets` import sets **only**
`NuGetLockFilePath` to `packaging\windows\locks\<project>.packages.lock.json` for
every project in both graphs. RID restore needs a different target graph from
the platform-neutral foundation, so there are two intentional lock sets.
Normal foundation commands/imports/locks are unchanged. Before ordinary locked
restore, the wrapper generates the real restore graph and requires every RID
lock to exist. Missing locks cannot silently become newly generated locks.
Stale locks fail NuGet's locked-mode validation.

After a deliberate production dependency or runtime change, the owning engineer
must coordinate central pins, refresh/review the normal locks and use:

```powershell
.\packaging\windows\Update-PublishLocks.ps1 -DotnetPath $sdk -CliHome $home -WorkDirectory "$run\lock-maintenance"
```

Review/commit all affected RID locks and any new notices/native inventory, then
rerun ordinary locked publishing and the foundation lane. Only this explicit
maintenance command uses `--force-evaluate`; ordinary publish/CI never regenerates
locks. Framework runtime archives are also pinned separately because NuGet
framework downloads are not represented as ordinary package lock dependencies.

Payload manifests contain a sorted complete relative file inventory, lengths,
SHA-256s, versions, source HEAD and a dirty-worktree flag, with no timestamp or
absolute build path. Generated C# file-local type paths are mapped to stable
paths so repeated clean output directories produce identical application bytes.
`SHA256SUMS.txt` covers all payload files **and** the manifest. Installer output
has its own manifest/checksums including the payload manifest hash and toolchain
pins. Checksums detect accidental corruption; they are not signatures. A dirty
source flag is honest local evidence, not proof that HEAD contains those inputs.
Byte-identical installers across machines/times are not claimed.

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
It checks the resulting locks, pinned SDK/global.json and repository NuGet
configuration, rejects non-filesystem directories, and asserts caller/process
working directories are unchanged.

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

On the existing Windows developer host: actual locked self-contained publishing,
native CLI/WPF launch, integrity/negative coverage, repeat-publish equality and
real Inno compilation are exercised. The dedicated pinned/read-only CI lane
performs two publishes, assertions, native Doctor smoke and compilation only.
It neither installs Martlet nor uploads releases/artifacts.

**Not run:** clean standard-user Windows with no SDK/preinstalled .NET; actual
install/uninstall/reinstall/repair; Start menu/registered uninstall operation;
long consumer account paths; disk-full/cancel/locked-file scenarios;
interrupted upgrades or rollback; SmartScreen/reputation/signing; novice or
screen-reader qualification; denied-egress, audio/provider or AI tests.
No isolated consumer VM was supplied. Never use this shared developer host as
a disposable install test or install into its real app/data locations.

In a separately authorized disposable Windows 11 25H2 x64 VM, record exact OS,
installer/hash and account; exercise clean standard-user install, launch via
both shortcuts, profile creation, same-version reinstall and registered
uninstall. Verify profile bytes survive and no unwanted startup/firewall/
privileged changes occur. Then add failure/long-path/version scenarios and
signed-release gates under V07. Until those pass, this remains an internal
skeleton regardless of green compilation.
