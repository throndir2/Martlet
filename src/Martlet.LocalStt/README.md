# Optional local whisper.cpp STT foundation (H07a)

For the separate installed-Windows offline recognition route, see
[`Martlet.Stt.Windows`](../Martlet.Stt.Windows/README.md). It does not use,
enable, download or qualify this whisper.cpp candidate.

**Standalone disabled candidate, not a shipped or qualified local-STT route.**
`Martlet.LocalStt` and its direct test project intentionally remain outside
`Martlet.slnx`, Desktop, Core settings, conversation composition and Windows
packaging. The projects add no setup choice, downloader, provider key, model
discovery, firewall/service change, background listener or automatic enablement.
Ordinary validation uses only inert bytes and test processes; it does not
download or execute whisper.cpp or a Whisper model.

## Reuse provenance and compatibility

The source module, embedded manifest and direct tests were mechanically imported
from `3a7f4389fe391473d2e004ce8c5e7183560c9748` ("Add optional local STT
foundation", original author throndir), without its central documentation or
frozen-branch ancestry. This optional CPU candidate requires **no Docker**.
It does not change the installation plan or claim a working speech provider.

Fresh review hardened the imported boundary. The offline acquisition slice
from `abea6f69d61ecf0549e9e5eb4b80f75b82dd867a` (original author throndir)
is now ported as a delta onto `4d94a435bc4f5e1e8840fe2f0653883d7bc8eb1a`,
without its source ancestry, old verifier/runner, or Desktop friend access.
It preserves these boundaries:

- The public adapter no longer accepts a process runner. Process launch
  requests/runners are internal; only the friend test assembly exercises their
  inert-fixture execution seam. Every public transcription request returns
  `PackageUnqualified` (or precancellation), even with exact package hashes and
  an injected auditor. There is no enabled public route or qualification switch.
- Authorization now requires `RequestDeadline` and `AllowProcessLaunch` in
  addition to the original audio/file/egress/rights permissions. A changed
  request deadline does not rebind an existing permit.
- Native Windows launch creates a **suspended** root, assigns its private Job,
  then resumes it. Failure before assignment cannot run fixture/runtime code.
  The only inherited handles are the explicitly listed stdin/stdout/stderr pipes.
- Package/workspace directories are pinned against rename, opened file handles
  are checked for exact final paths, reparse attributes and hard links, and
  ephemeral files are deleted through checked handles.
- Cancellation does not abandon a pending egress admission or a returned
  session. The adapter retains its owner until that call/session and private
  cleanup complete; disposal cancels and joins admitted work. A future trusted
  auditor must honor cancellation and complete cleanup, not detach live leases.

One tightly coupled native fix strengthens the shared `WindowsDirectoryLease`:
directory handles now request `FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES`
while still denying delete sharing. An attributes-only handle did not block
empty-root/ancestor rename on the validation host; earlier package tests held
files as well and did not isolate that invariant. The new directory-only
regression covers both root and ancestor renames, and the original verifier,
workspace and adapter regressions still apply. There is no parallel weaker
importer-only directory guard.

## Exact candidate identities

The embedded [`whisper-package.v1.json`](whisper-package.v1.json) is the only
production manifest accepted by the public physical verifier. It is
`disabled_pending_qualification` and binds:

| Artifact | Exact candidate identity | Evidence status |
| --- | --- | --- |
| Source/runtime | `ggml-org/whisper.cpp` tag `v1.9.2`, commit `306c88f4d1286aec1bf96e544632897886af5501` | Tag/commit metadata only; source MIT |
| Windows x64 archive | release `364972418`, asset `501504923`, `whisper-bin-x64.zip`, 8,194,445 bytes, SHA-256 `49dcc16de826f20bd53d44f947a1ae49dfa81f86cad67a64d80820cb192d674a` | GitHub release metadata; bytes/signature not locally acquired or authenticated |
| English model | HF `ggerganov/whisper.cpp` revision `80da2d8bfee42b0e836fc3a9890373e5defc00a6`, `ggml-base.en.bin`, 147,964,211 bytes, SHA-256/LFS OID `a03779c86df3323075f5e796cb2ce5029f00ec8869eee3fdfb897afe36c6d002` | Hugging Face LFS metadata; payload/conversion chain not locally verified |
| Intended extracted runtime subset | `Release/whisper-cli.exe`, `whisper.dll`, `ggml.dll`, `ggml-base.dll`, `ggml-cpu.dll`, `SDL2.dll` | Derived from the pinned release workflow; actual ZIP layout/import closure is explicitly unverified |

Direct immutable locators are in the manifest. Source evidence:

- [v1.9.2 release](https://api.github.com/repos/ggml-org/whisper.cpp/releases/364972418),
  [asset metadata](https://api.github.com/repos/ggml-org/whisper.cpp/releases/assets/501504923),
  [commit](https://github.com/ggml-org/whisper.cpp/commit/306c88f4d1286aec1bf96e544632897886af5501)
  and [MIT license](https://github.com/ggml-org/whisper.cpp/blob/306c88f4d1286aec1bf96e544632897886af5501/LICENSE).
- [Pinned model metadata](https://huggingface.co/api/models/ggerganov/whisper.cpp/revision/80da2d8bfee42b0e836fc3a9890373e5defc00a6?blobs=true),
  [conversion/source description](https://github.com/ggml-org/whisper.cpp/blob/306c88f4d1286aec1bf96e544632897886af5501/models/README.md)
  and [OpenAI Whisper MIT declaration](https://github.com/openai/whisper/blob/86098128c0b4f24f0e2aa2994de830614b474227/LICENSE).
- [Pinned Windows release workflow](https://github.com/ggml-org/whisper.cpp/blob/306c88f4d1286aec1bf96e544632897886af5501/.github/workflows/release.yml)
  for the shared-library/SDL2 build and archive shape.

The bounded plan permits exactly 156,158,656 candidate download bytes, at most
64 archive entries, 256 MiB expanded runtime bytes and 424,594,112 staging
bytes. These are admission ceilings, not an implemented downloader or a disk
forecast for rollback, crash recovery or another model. URLs are inert data;
no code in this project resolves them.

## Offline import and inspection

[`Martlet.LocalStt.PackageTool`](../Martlet.LocalStt.PackageTool/README.md)
is a separate explicit CLI, outside the root solution and shipped graphs.
It accepts only caller-supplied absolute local archive, model, notice and
evidence paths. There is no URL fetch, build execution or runtime launch.
Filesystem operations require Windows x64; unsupported hosts fail explicitly.
Help and command parsing do not touch the package filesystem.

`PhysicalLocalSttPackageImporter.Preview` returns an immutable, generation-bound
plan. `plan.Authorize(ApproveExactRuntimeModelAndNoticeRights)` creates a one-use
authorization for that importer/plan only; the default decision is no.
The CLI additionally requires `--approve-plan` with the exact preview fingerprint.
File IDs, hashes, byte lengths and write times bind the supplied inputs; their
validated read handles and ancestor-directory leases stay held through copying,
read-back verification and finalization. Destination creation never overwrites.
Windows cannot rename a directory with descendant file handles open, so the
importer keeps the stage locked during source revalidation, retains exact file
identities across the checked native rename, then pins and re-verifies the
destination before returning success. A rename alone is not a verified receipt.
Cancellation or interference before that final acceptance cleans the owned
stage/destination or explicitly reports `CleanupPending` with the retained path
and original failure. No canceled operation returns a success receipt.

The installed envelope deliberately differs from the original source importer:

```text
destination/
  package-owner.v1.json
  payload/
    downloads/
    runtime/
    models/
  notices/
  metadata/
    whisper-package.v1.json
    import-evidence.v1.json
    sbom.cdx.json
    package-receipt.v1.json
```

**Future H07 callers must pass `LocalSttPackageInspection.PayloadPath`, not
`DestinationPath`, to `PhysicalLocalSttPackageVerifier`.** The unchanged verifier
still demands exactly `downloads`, `runtime`, and `models`. Evidence notice
coverage uses logical `runtime/...` and `models/...` paths; receipt/SBOM paths
include the physical `payload/` prefix. Inspection reports `CanLaunch = false`,
`RuntimeQualified = false`, `RightsQualified = false` and the disabled candidate
status even when `PackageVerifierStatus` is `Verified`.

The importer checks ZIP central/local headers before allocating entry objects,
entry/path/expansion/compression-ratio bounds, duplicate/case/prefix collisions,
special entries, PE32+ x64 section and RVA bounds, regular/delay import descriptors,
thunks and names, and the selected files' declared dependency graph. Missing
non-system DLL declarations and selected-file cycles are rejected. The explicit
Windows system/API-set names are **declarations, not host availability evidence**.
This is import-table inspection only: dynamic loads, forwarded exports,
Authenticode, actual OS resolution and full runtime/transitive closure remain
unqualified. Hashes and notice coverage are not legal clearance, egress
evidence, CPU/GPU compatibility, accuracy or runtime qualification.

The new acquisition provenance fields leave all runtime/model pins and disabled
status unchanged. Legacy v1 documents without that block retain safe
offline-only defaults and their exact original-byte hashes. Present blocks are
strictly validated; import evidence and receipts must match the actual current
manifest hash (the additional metadata changes that hash, not the model version).
Receipt integrity hashes detect inconsistency; they are not signatures or proof
of origin.

Cleanup is bounded and restartable, not an implicit resume or overwrite.
It accepts only exact staging names and matching canonical owner markers,
including the declared notice names, preflights the bounded layout, pins
directories and deletes through checked identity handles. A process-wide CAS
and session-local Windows mutex prevent competing importer cleanup. A retained
same-instance directory ID and notice inventory allow retry even if the first
owner-marker write failed; replacement roots are never adopted. Unknown,
malformed or foreign paths are preserved rather than recursively deleted.
These markers are an ownership convention, not protection against a malicious
same-user process that fabricates a whole matching package. Use a private
caller-owned parent. Power loss before the first durable owner marker can leave
an unrecognized empty/partial stage; unattended crash recovery and hostile
same-user interference remain qualification gates.

`PhysicalLocalSttPackageVerifier` accepts one canonical local package root with
exact `downloads`, `runtime` and `models` contents. It rejects UNC/device/ADS,
noncanonical paths, symlink/reparse/device entries, undeclared runtime files,
changed archive/model bytes, unsafe ZIP entries and extracted bytes that differ
from the exact pinned archive entries. Read handles and pinned ancestor
directories deny replacement through the owned action on Windows. Hostile
same-user interference before initial directory acquisition, Authenticode,
binary imports, archive-component notices/SBOM and source reproducibility
still need separate qualification.

## One-use audio and process ownership

`CanonicalWaveAudio` copies and owns one nonempty canonical 44-byte-header RIFF
WAV: mono, 16 kHz, signed PCM16, at most 800,044 bytes / 25 seconds. Disposal
zeroes the managed copy. A trusted caller must provide one
`LocalAudioAuthorization` bound to the exact operation, manifest fingerprint,
package/model/language, audio SHA-256/length and original deadline. It must
explicitly allow local processing, process launch, the ephemeral file, denied
egress and the caller's candidate-rights review. The original request deadline
is separately bound even when permit expiry is earlier. The permit is
atomically consumed once and is never persisted or renewed.

The current v1.9.2 CLI source supports file or stdin audio, but this candidate
deliberately uses only an adapter-owned file:

| Data | Owner and lifetime |
| --- | --- |
| Source capture/canonical conversion | Calling audio path; outside this standalone project |
| Managed WAV | `CanonicalWaveAudio`; copied before validation, hash-bound, independently snapshotted at admission and zeroed on dispose |
| `input.wav` | One operation directory under the current-user temp root; process-read-only and explicitly removed after owned tree exit |
| Process stdin | Redirected and closed before inference; no audio or control input |
| `transcript.txt` | Exact operation-owned output basename; bounded read after successful exit, then deleted |
| stdout/stderr | Drained concurrently at 4/8 KiB; stdout must be empty/whitespace, stderr is UTF-8 checked but never returned or logged |

After fresh artifact and authorization checks, `WhisperCliPolicy` constructs
only this argument list (paths come from verified/owned objects, never a
request):

```text
--model MODEL --file INPUT --language en --threads 4 --processors 1
--no-gpu --no-timestamps --output-txt --output-file OUTPUT --no-prints
```

The internal `SystemLocalSttProcessRunner` has a side-effect-free constructor.
Its inert fixture path uses native `CreateProcessW` with no shell, individually
quoted fixed arguments, closed stdin, redirected pipes, no window, the verified
runtime working directory and an environment cleared to `PATH`, `SYSTEMROOT`,
owned `TEMP`/`TMP`, `LANG`, `LC_ALL` and `OMP_NUM_THREADS`. The root is created
with `CREATE_SUSPENDED` and assigned to a private kill-on-close Windows Job
**before** `ResumeThread`; completion is accepted only when the Job is empty,
so a root that exits before an owned child is still cleaned as a tree. The
public adapter has no arbitrary executable, model, path, flags, provider secret,
retry, alternate model/provider or download fallback.

The adapter has one nonqueueing owner. The original deadline includes package
verification. Original cancellation, authorization expiry or the 30-second
deadline kills only the spawned process tree and
discards late output. Pipe overflow, malformed output and nonzero exit accept no
transcript. Failure to stop the tree or delete private files quarantines the
adapter; it cannot evade cleanup with a replacement process. Successful output
is also discarded if cancellation/expiry occurs during final cleanup. Managed
transcript and pipe buffers are cleared once consumed and disposed.

## Denied-egress evidence

**A Windows Job owns lifetime, not network egress.** Process source inspection
is not proof of offline behavior and whisper-cli has no no-network flag.
Every launch therefore requires an injected
`ILocalSttEgressAuditor` session that establishes denial **before** start, binds
the exact PID tree and observes through tree exit. The committed policy is
`no_network`: both loopback and nonloopback attempt counts must be zero; system
policy mutation and retained raw endpoint data are forbidden. A missing,
unbound, incomplete or violating report discards an otherwise valid transcript.

This project intentionally supplies no production egress auditor and changes no
firewall, service or machine policy. Tests use inert audit fixtures. Selecting
a real OS isolation/observation mechanism, proving denial on the intended
machine and reviewing its privileges/lifecycle remain privacy gates.

## Direct local validation

Use the exact SDK in `global.json`, locked project restores and a private
artifacts directory. These commands compile/run only managed code and inert
fixture subprocesses:

```powershell
$env:CI = 'true'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = 'true'
dotnet restore src\Martlet.LocalStt.PackageTool\Martlet.LocalStt.PackageTool.csproj --locked-mode --artifacts-path $artifacts
dotnet build src\Martlet.LocalStt.PackageTool\Martlet.LocalStt.PackageTool.csproj -c Release --no-restore --artifacts-path $artifacts
$env:MARTLET_PACKAGE_TOOL = Join-Path $artifacts 'bin\Martlet.LocalStt.PackageTool\release\martlet-local-stt-package.dll'
dotnet restore tests\Martlet.LocalStt.Tests\Martlet.LocalStt.Tests.csproj --locked-mode --artifacts-path $artifacts
dotnet build tests\Martlet.LocalStt.Tests\Martlet.LocalStt.Tests.csproj -c Release --no-restore --artifacts-path $artifacts
dotnet test tests\Martlet.LocalStt.Tests\Martlet.LocalStt.Tests.csproj -c Release --no-build --no-restore --artifacts-path $artifacts
```

Coverage includes strict manifest/audio shapes; one-use/mismatched/expired
authorization; changed archive/executable/dependency/model; missing, extra,
access-denied and reparse paths; exact arguments/environment; malformed,
oversized, no-speech and nonzero results; stderr redaction; egress violations;
hung/canceled/late children; suspended-start assignment failure, immediate
root/child exit races, exact native argument quoting and actual inert
process-tree termination; real private junction/hard-link rejection, held
directory identity, late egress-session cleanup and adapter disposal; disk/access
and private-file cleanup quarantine. No root-solution/package smoke is
applicable because this slice does not join either graph.

Additional coverage uses only synthetic ZIP/model/PE/notice bytes: source
identity substitution, hard links, reserved names, stale/reused approval,
input leases, partial writes, cancellation, retained cleanup, finalization
tampering, malformed PE import tables and immutable build-plan collections.
Actual PackageTool subprocesses exercise help, readable errors, synthetic-pin
rejection, inspection rejection and nonexecuting build-plan JSON. Synthetic
successful imports use only the existing internal test seam; there is no public
manifest override to make a synthetic package pass production pins.

## Remaining gates

The following are **NOT RUN / NOT PASSED** and block an enabled or supported
route:

- acquire under explicit consent and recompute both hashes; inspect exact ZIP
  members, imports, signatures, notices/SBOM and all runtime/model/conversion
  rights before redistribution;
- select and review a production no-network process-tree guard/auditor without
  persistent firewall/service side effects, then prove denied egress on the
  declared host;
- qualify CPU architecture/features, thread choice, RAM/disk peaks, thermal and
  game-load impact on each advertised PC;
- exercise the actual microphone/canonical-WAV path and measure English/gaming
  accuracy, no-speech behavior, names/terms, cold/warm latency and cancellation;
- qualify private crash/power-loss residue cleanup and supported install,
  update, repair and uninstall ownership;
- integrate only through a future reviewed Desktop/conversation/settings slice
  with fresh route consent and no automatic cloud/local fallback;
- complete H07b's independent native VAD/feedback-protected speech-interruption
  work before advertising automatic barge-in.
