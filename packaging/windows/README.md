# Internal Windows packaging (F02 skeleton, V07c evidence foundation)

**INTERNAL DEVELOPMENT ONLY - UNSIGNED. Not a supported installer or a completed
F02/AC-02/G1 gate.** This packages V04b real explicit typed / push-to-talk app
integration alongside the offline WPF fixture/status experience and Doctor
self-test, including the bounded optional synthetic-tone sink. Opening the app
or conversation window does not authorize requests, credential lookup or device
access. Saved configuration is not live API/account/model readiness.
Desktop V07a adds explicit same-profile configuration backup/previewed restore,
not a diagnostic ZIP or installer updater. Transactional binary upgrades,
rollback and signed distribution remain unimplemented.
No project code/asset license is granted.
The existing foundation/planning documents retain their broader future gates.

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
$cliHome = Join-Path $run 'cli-home'
$first = Join-Path $run ("publish one " + [char]0x00E9)
$second = Join-Path $run ("publish two " + [char]0x00E9)
$builder = Join-Path $run 'inno'

.\packaging\windows\Publish-Windows.ps1 -DotnetPath $sdk -CliHome $cliHome -OutputDirectory $first
.\packaging\windows\Publish-Windows.ps1 -DotnetPath $sdk -CliHome $cliHome -OutputDirectory $second
.\packaging\windows\Get-InnoSetup.ps1 -Destination $builder
.\packaging\windows\Test-Packaging.ps1 -DotnetPath $sdk -CliHome $cliHome -PayloadRoot "$first\payload" -ComparePayloadRoot "$second\payload" -PublishDirectory $first -BuilderDirectory $builder -WorkDirectory "$run\tests"
.\packaging\windows\Smoke-Package.ps1 -PayloadRoot "$first\payload" -InteractiveDesktop
.\packaging\windows\Build-Installer.ps1 -PayloadRoot "$first\payload" -BuilderDirectory $builder -OutputDirectory "$run\package"
# Also exercise the actual GitHub pwsh exit wrapper (includes assertions, Doctor and compiler).
.\packaging\windows\Test-WorkflowExit.ps1 -DotnetPath $sdk -CliHome $cliHome -PayloadRoot "$first\payload" -ComparePayloadRoot "$second\payload" -BuilderDirectory $builder -WorkDirectory "$run\workflow-tests"
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
first-run status and exercises the accessible fixture controls/Stop/retry/close.
It is deliberately opt-in on interactive developer desktops; without the switch,
the local smoke runs Doctor only, not a simulated interactive or clean-OS claim.
Both programs receive a unique temporary Unicode data path. The smoke checks read-only launch and
existing/malformed settings preservation, snapshots real settings without
displaying their contents, and fails on unexpected writes.

## One payload and installation layout

```text
payload\
  Desktop\Martlet.Desktop.exe, *.dll, *.deps.json, *.runtimeconfig.json, ...
  Doctor\Martlet.Doctor.exe, *.dll, *.deps.json, *.runtimeconfig.json, ...
  help\INTERNAL.txt
  help\TROUBLESHOOTING.md
  notices\DEPENDENCIES.txt, upstream licenses/notices
  sbom.cdx.json
  manifest.json
  SHA256SUMS.txt
```

Each entry point owns its complete runtime directory. There is **no merge** of
Desktop and Doctor publish files and no dependence on their relative DLL search
paths. This duplicates some runtime bytes deliberately: the installed layout is
simple and avoids conflicting WPF/Core runtime facades. No trimming, single-file
extraction or ReadyToRun compilation is used. The SDK is not shipped.

The exact authored assembly inventories are checked both on disk and in each
application's `.deps.json` (including each project's runtime asset):

- Both: `Martlet.Core`, `Martlet.Audio`, `Martlet.Fixtures`, `Martlet.Sessions`,
  `Martlet.Diagnostics`.
- Desktop only: `Martlet.Desktop`, `Martlet.Credentials.Windows`,
  `Martlet.Conversation`, `Martlet.Providers`, `Martlet.Participation`,
  `Martlet.Support`.
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
`CustomBeforeMicrosoftCommonCrossTargetingTargets` imports set **only**
`NuGetLockFilePath` to `packaging\windows\locks\<project>.packages.lock.json` for
every project in both graphs, including multi-target Audio/Doctor outer builds.
Both applications publish explicitly with `--framework net10.0-windows`;
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

Schema-v2 payload manifests contain a sorted complete relative file inventory,
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
option. This is an **unsigned internal observation**, not publisher
attestation, a SLSA level, a release signature, license clearance or a
vulnerability assessment. Signing and distribution decisions remain deferred.

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

The twelve RID locks cover Core, Audio, Fixtures, Sessions, Diagnostics,
Credentials.Windows, Conversation, Providers, Participation, Support, Desktop
and Doctor.
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
the runtime's own notices. No project license grant or distribution approval
is implied. Missing new assemblies/notices, wrong dependency versions and
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
the SDK. Both the native Doctor's ten self-test choices and, when explicitly
interactive, WPF's real fixture controls run with **audio OFF**. Permissioned
tone controls are never invoked by automation. Fixture success does not turn
the ordinary status report green; profiles and corrupted originals are not
silently replaced. The installed `help\INTERNAL.txt` describes exact commands,
exits, synthetic text/refusal, the separate 200 ms tone permission and the bounded
real-conversation controls. Smoke and packaging validation must never authorize
live requests, vault effects, microphone capture or generated-voice playback.

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

Required for each changed app graph on the existing Windows developer host:
two actual locked self-contained publishes, native CLI/WPF smoke,
integrity/negative coverage, repeat-publish equality and real Inno compilation.
Lock refresh or read-only compiler receipt verification alone does **not**
complete those payload gates. The former dedicated hosted lane is removed;
run the commands above locally, including the retained wrapper regression.
These developer commands neither install Martlet nor upload releases/artifacts.
The minimal remote build/release exception does not authorize moving these
validation gates into an Action or creating a replacement workflow.

**Not run:** clean standard-user Windows with no SDK/preinstalled .NET; actual
install/uninstall/reinstall/repair; Start menu/registered uninstall operation;
long consumer account paths; disk-full/cancel/locked-file scenarios;
interrupted upgrades or rollback; SmartScreen/reputation/signing; novice or
screen-reader qualification; denied-egress or live provider/audio qualification.
For V04b, **real API/account or vault effects, physical microphone/speaker
trials, clean-Windows installation, live first trial, signing and release are
NOT RUN**. Deterministic offline fixtures and fake-port tests are not those
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
