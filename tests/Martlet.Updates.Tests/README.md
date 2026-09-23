# Local V07b-a/b fixtures and gates

`SignedPackageFixture.cs` creates private, uniquely named temporary directories,
real ZIP files, exact internal unsigned manifests/checksums, and external
RSA-PSS-SHA256 envelopes. `SigningKeys` generates ephemeral RSA-3072 approved and
unapproved keys in memory for each test class lifetime and disposes them.
No private key, package signer identity or purported production certificate is
committed. Fixtures are created at runtime, not downloaded.

`ProductionPayloadFixture.ps1` exercises the **actual production** pure
provenance/SBOM/manifest constructors, `ConvertTo-EvidenceJson`,
`Get-PayloadFiles` and `Get-ChecksumText` on inert synthetic leaves. The normal
producer retains all real layout/provenance/SBOM validation; this test does not
mock those checks or claim to have passed them. Existing tests default to legacy
v1; `PayloadCompatibilityTests` covers current v2 and mixed retained histories.
No checked-in parallel hand-authored v2 wire fixture is used.

`AvatarPayloadFixture.ps1` adds an explicit format-3 path through those same
production constructors; the original parameterless/legacy paths remain intact.
Its executables, npm materials, tool observations and source leaves are
deliberately inert synthetic data, not a successful native build or upstream
archive qualification. It exercises the three contexts with no tolerated restore
omissions, exact WebView2 reference/copy shape, restore-only Grpc.Tools, the
reviewed 17-package npm graph (including type-only dependencies), and all four
browser outputs. It never runs a bundled script or executable.
The default graph also includes the actual esbuild JavaScript/native build-input
shape and both authored package manifests. Native material matches the esbuild
tool fingerprint by length/hash. These records reproduced a real-producer
compatibility failure in the prior authored-only build-script rule; negative
cases reject missing, misidentified, retagged or fingerprint-mismatched inputs.

`AvatarPayloadCompatibilityTests` drives the real ephemeral-key signed preview,
staging, receipt reopening and mixed v1/v2/v3 retained-selection/rollback paths.
Rechecksummed/re-signed context, ownership, graph, source, notice, tool and browser
tampering remains invalid. Actual production-serializer vectors cover exact
canonical bytes including empty/singleton containers, required nulls, Unicode,
escape sequences and Int64 values beyond JavaScript precision. New subdocument
hashes are recomputed; existing source/restore hashes remain declarations.
The separate local old-reader probe must use the retained unchanged baseline
assembly against these actual new producer documents, not a simulated parser.
No fixture result authorizes activation or qualifies the updater rollout.

The test-only PowerShell 7 child runs with no profile and explicit source/output
paths, a 60-second deadline, concurrent pipes and a combined 64 KiB output cap.
Timeout/overflow/nonzero exit fail the fixture. The exact child must exit and
both pipes retire before successful completion; uncertain/failed output remains
in its unique temporary directory, never recursively cleaned by an active
child's owner. Controlled failure/overflow/wait cases exercise this boundary.
Normal fixture disposal refuses unknown children before deleting known outputs.
Missing PowerShell/source lookup is an error, not a skipped test or fallback
serializer. Deterministic `CI=true` maps compile-time paths to `/_`; for C:
artifact builds set `MARTLET_UPDATES_SOURCE_ROOT` explicitly to the source checkout.

Current coverage includes strict required/unknown/duplicate/null objects at
every emitted nested shape, version/source/SDK/graph/SBOM-file inconsistencies
despite rechecksumming and ephemeral re-signing, inclusive legacy 2 MiB/current
16 MiB limits, declared/observed overflow, unchanged external/depth budgets,
bounded descriptive Unicode source records, cancellation and exact cleanup
ownership. Large exact-byte-boundary inputs use signed legal JSON whitespace;
they are parser-boundary cases, not canonical production output. Retained mixed
v1/v2/v3 selection/rollback preserves historical bytes and remains non-runnable.

`HistoricalSettingsSchemaTests` adds authored pre-persona wire histories backed
by real signed/staged packages and v2 settings. It covers completed and interrupted
v2 restores, fresh-consent acknowledgment, unchanged old evidence after later
v3 settings migration, invalid recorded schemas and continued current-schema
admission for new rollback. Historical marker/ack schemas are never inferred from
`CurrentSchemaVersion`; this does not introduce a new journal format.
A valid two-persona Unicode snapshot larger than 128 KiB also traverses actual
selection, restore and acknowledgment with the 256 KiB snapshot-specific limit;
journal/control limits remain unchanged.

The real unchanged-reader regression fails at `Preview` with `UnsafeEntry` for
the current root SBOM; the fixed same staging/inspection test passes. Retain the
red TRX and original consumer assembly/source hashes separately from green
outputs. A prior fixture source-path setup failure is not old-reader evidence.
The separately run `-ReferenceMetadataRoot` mode reconstructs only the three
named, hash-pinned historical PR #25 metadata documents from declared inventories
and current pure constructors. It writes only a new output directory and reads
no historical binaries. Its exact-byte equality is historical serializer
conformance, not current-main build, package authenticity or provenance
qualification. Ordinary tests require no other session's artifacts.

The tests invoke `LocalStagingEngine`, not a mock verifier. The internal IO seam
injects failures at actual write/flush/rename/cleanup boundaries and can pause a
real pending operation. It cannot supply a fake signature-verification result.
No package payload runs: `.exe` entries are inert bytes, the included script
would create a canary only if executed, and the canary must remain absent.
Installed executable, settings, vault and model sentinels remain locked with
`FileShare.None` throughout verification/staging on Windows and are checked
byte-for-byte afterward. No actual user profile, vault, audio or model is used.

Coverage includes trusted exact stage/reopened receipt, standard deflate and
signed data descriptors, empty entries, self-declared/absent/wrong trust,
tampering, noncanonical/duplicate/null JSON, PKCS#1 padding refusal, wrong
version/RID/schema bounds, traversal/ADS/reserved names/case collisions,
file-directory collisions, symlink/reparse/FIFO/hardlink-extension metadata,
encrypted/ZIP64/overlapping/bad local headers, CRC/digest/size mismatch,
truncation/short reads, compression bombs and bounded resource/disk limits.
It also covers frozen approval/source/installation facts, superseded/failed
preview, approval replay, destination races, denied access, copy corruption,
write/flush/rename failure, cancellation at each boundary, blocked cancellation
callbacks, retained cleanup, partially completed cleanup retry, foreign children
preserved by nonrecursive cleanup, and corrupt/forged/revoked-trust receipts.
Real `SettingsStore` fixture save/load revisions are passed unchanged into the
engine while that fixture settings file is held with `FileShare.None`. The
uppercase token survives approval, staging and receipt reopening exactly; a
later real store save or a caller changing only token case rejects stale
approval. Opaque settings tokens are not interpreted as candidate hashes.

`SelectionFixture`, `SelectionTests` and `SelectionInterruptionTests` use the
same actual signed-package stager and actual Core settings/snapshot APIs in
private temporary C: directories. Their 95 cases cover explicit unqualified
initialization; frozen selection/rollback approval and replay; verified current/
previous selection and compatible snapshot retention; pending V07a restore;
wrong origin/profile/store/revision/case; package/receipt/snapshot/trust/lineage
tamper; real filesystem symbolic links; bounded history and external paths;
cooperating locks and noncooperating write denial; disk-full/access fault
injection; every transaction create/write/flush/pending/final replacement boundary;
interrupted recovery and recovery twice; initialization leftovers; blocked
cancellation callbacks before/after finalization; and retained unknown children.
The 138 original staging regressions remain in the same suite.

`SelectionReviewRegressionTests` adds 64 owning-boundary cases. The first 18
failed against the pre-review implementation (only the initialization fault hook
was added): scratch overwrite/name substitution at all four atomic boundaries,
control-only rewind with stale approvals, and current/previous stage/snapshot
tamper during selection. The complete matrix also covers format-2 write-ahead
publication create/write/flush interruption, before/after-rename cancellation
with blocked callback delivery, missing/conflicting publication requirements,
format-1 refusal, unpublished-v2 versus legacy orphans, retained evidence during
recovery, and scratch substitution after publication intent. Ambiguous
receipt/pointer states must fail read-only twice with every byte retained.

The local review run retains `selection-review-before.trx` (18 failures) and
subsequent after/extended TRX files under the session's own artifact TestResults
directory, not in the repository. Passing these controlled tests does not
establish physical power-loss durability or hostile same-user rename immunity.

The direct Core graph additionally runs existing `ConfigurationRecoveryTests`
and the new `ConfigurationSnapshotInspectionTests` (52 cases together), including
actual snapshot creation, bounded pure inspection, immutable scalar metadata,
malformed/future/tampered envelopes and uppercase revision preservation.
Inspection is not a new settings parser or a restore authorization boundary.

`RollbackRestoreTests`, `RollbackRestoreInterruptionTests` and
`RollbackRestoreOwnershipTests` exercise the explicit configuration coordination
using actual signed stages, Core previews/restores and the actual shared
SetupOperationRunner in private fixtures. They cover distinct exact-bound
one-use consent, original monotonic/UTC expiry, actual source/writer/result pins,
blocked callback/observation lifetime, real Core before/after-commit errors,
v2-to-v3 fencing, all new journal/publication write boundaries, control rewind,
scratch substitution, and conservative repeated recovery. An exact matching
settings file alone must never clear pending restore. A separate Core scope
suite covers the common standalone/owned restore implementation and retirement.
These are configuration-only library tests, not a Desktop workflow, executed
candidate rollback or physical-crash qualification.

`RollbackRestoreAdmissionTests` gates the real runner's captured execution
context before its original cancellation check. A private, bounded, nonthrowing
context-entry gate reproduces accepted/pre-start-canceled preview supersession
without replacing the runner, disabling cancellation, changing thread-pool
settings or relying on timing. Coverage includes concurrent admission,
busy/wrong-runner refusal, fresh consent after retirement and engine ownership
through worker failures and cancellation callbacks. Refused work never
invalidates an already-running restore.

`RollbackAcknowledgmentTests`, `RollbackAcknowledgmentInterruptionTests` and
`RollbackAcknowledgmentOwnershipTests` produce the known-commit/pending state
by interrupting the actual existing Core-backed restore after its marker is
recorded. They do not fabricate successful commit markers or use a substitute
restorer. The new preview/consent acknowledges actual present settings without
entering any Core write/restore/original-creation path. Coverage checks old
settings/original/history byte preservation, new v4 ancestry and generation
reservations, rejected stale/missing/conflicting evidence, actual writer/stage/
history ownership, accepted pre-start cancellation, fixed original expiry,
publication faults, callback retirement and v4 continuation. Partial/full
unmatched intent remains read-only manual debt.

Core's `ConfigurationCurrentReadScopeTests` exercise the minimal actual
no-write current-settings ownership seam separately. Compatibility evidence
also needs actual retained pre-v4 assemblies, built from an unchanged baseline
in the current owner's private artifacts: exercise old operational entrypoints
at scratch-only, partial/full intent, terminal and control-only-rewind states.
An unsupported-version unit assertion or simulated old parser alone does not
establish that compatibility boundary. Preserve probe source, actual baseline
assembly hashes and results; never reuse another owner's artifacts.

Certificate expiry/chain tests are not applicable to this explicitly pinned
raw-public-key protocol; certificates and trust-store policy are not used.
Ephemeral signing exercises authenticity against the test policy only. Actual
publisher authorization and release signing are **NOT RUN**.

## Reproduce locally in this isolated worktree

Use the existing read-only SDK supplied by the owner; do not install or change
a shared SDK, cache, runner or global environment. For the original Windows
session the SDK was:

```powershell
$env:DOTNET_ROOT = 'C:\Users\mozar\.copilot\session-state\ccb47cf5-a780-443b-90f1-df2d44b89517\files\dotnet'
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
$env:DOTNET_CLI_HOME = 'C:\Users\mozar\.copilot\session-state\5144146e-e46e-49b9-87a2-b608994d7985\files\cli-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_NOLOGO = '1'
$env:MARTLET_UPDATES_SOURCE_ROOT = (Get-Location).ProviderPath
$artifacts = 'C:\Users\mozar\.copilot\session-state\5144146e-e46e-49b9-87a2-b608994d7985\files\ci-artifacts'
$env:NUGET_PACKAGES = "$artifacts\packages"
$env:NUGET_HTTP_CACHE_PATH = "$artifacts\http-cache"
$env:NUGET_PLUGINS_CACHE_PATH = "$artifacts\plugin-cache"
$env:NUGET_SCRATCH = "$artifacts\nuget-scratch"
$env:TEMP = "$artifacts\tmp"
$env:TMP = $env:TEMP
# Create these private directories before invoking the SDK.
$project = 'tests\Martlet.Updates.Tests\Martlet.Updates.Tests.csproj'
dotnet build $project --no-restore -c Release -p:CI=true -p:UseSharedCompilation=false --artifacts-path $artifacts
# Only a dependency-manifest change or actual missing-assets failure justifies restore.
# Use an approved private offline feed/config when new acquisition is not authorized.
dotnet restore $project --locked-mode -p:CI=true --artifacts-path $artifacts
dotnet build $project --no-restore -c Release -p:CI=true -p:UseSharedCompilation=false --artifacts-path $artifacts
dotnet test $project --no-build --no-restore -c Release -p:CI=true --artifacts-path $artifacts --results-directory "$artifacts\TestResults"
```

A new session must substitute **its own** CLI home and C: artifact directory,
not reuse or write the original session's outputs. Use the same `CI=true` and
artifact path on all calls: the D: pooled-drive testhost issue makes mixing
ordinary build output with the C: test assembly unreliable. These commands build
the new project graph directly (including the read-only Core dependency);
they do not modify or register projects in `Martlet.slnx`.
Only new dependency manifests/missing assets justify restore. Lock generation
for these new projects was followed by a locked restore using existing central
package pins. No test pin or gate was weakened.

The separately required Core configuration/runner filter uses the same local
build/locked-restore pattern. Its existing Diagnostics -> Sessions -> Audio
reference graph restores both declared Audio targets even though the selected
Core tests execute `net10.0` only. Its existing NAudio.Wasapi/NAudio.Core 3.1.0
and System.Numerics.Tensors 9.0.0 archives may therefore be needed in the approved
offline feed. Do not prune target declarations, rewrite locks or equate raw
signed-archive SHA-512 with NuGet lock content hashes to force a restore.
Restoring that graph is not authorization to run Audio/native/device tests.

Run only local gates. No workflow creation, dispatch, retries, pushes, PRs or
release publication belong to these commands. A passing fixture suite is not
clean-machine, physical-disk-full/power-loss, hostile-filesystem, non-Windows,
Authenticode, signed-release, application activation or rollback qualification.
The single Windows sharing-mode assertion is conditional on Windows; a run on
another OS must not report that Windows ownership evidence as executed.
