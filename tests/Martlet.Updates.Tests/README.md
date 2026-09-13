# Local V07b-a/b fixtures and gates

`SignedPackageFixture.cs` creates private, uniquely named temporary directories,
real ZIP files, exact internal unsigned manifests/checksums, and external
RSA-PSS-SHA256 envelopes. `SigningKeys` generates ephemeral RSA-3072 approved and
unapproved keys in memory for each test class lifetime and disposes them.
No private key, package signer identity or purported production certificate is
committed. Fixtures are created at runtime, not downloaded.

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

The direct Core graph additionally runs existing `ConfigurationRecoveryTests`
and the new `ConfigurationSnapshotInspectionTests` (52 cases together), including
actual snapshot creation, bounded pure inspection, immutable scalar metadata,
malformed/future/tampered envelopes and uppercase revision preservation.
Inspection is not a new settings parser or a restore authorization boundary.

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
$artifacts = 'C:\Users\mozar\.copilot\session-state\5144146e-e46e-49b9-87a2-b608994d7985\files\ci-artifacts'
$project = 'tests\Martlet.Updates.Tests\Martlet.Updates.Tests.csproj'
dotnet restore $project --locked-mode -p:CI=true --artifacts-path $artifacts
dotnet build $project --no-restore -p:CI=true --artifacts-path $artifacts
dotnet test $project --no-build -p:CI=true --artifacts-path $artifacts --results-directory "$artifacts\TestResults"
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

Run only local gates. No workflow creation, dispatch, retries, pushes, PRs or
release publication belong to these commands. A passing fixture suite is not
clean-machine, physical-disk-full/power-loss, hostile-filesystem, non-Windows,
Authenticode, signed-release, application activation or rollback qualification.
The single Windows sharing-mode assertion is conditional on Windows; a run on
another OS must not report that Windows ownership evidence as executed.
