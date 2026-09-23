# Host setup review and artifact acquisition (H05a/H05b)

## Verified artifact acquisition (H05b)

The bounded acquisition engine is reused from
`81bf855b3b342563f2dbf2eb512672e717b41932` ("Add verified host artifact
acquisition"), not from its integration ancestry. It references current H05a
format-2 `LocalReview` history without changing it into external completion.
The source dependencies were `0de03054768cd4122ff4af8d612fcc832b1fc324`,
`288ef22a66f840434ff2e547d45c04dc63e4b9d9` and
`29fbe43ba01c7bec72101b230068276f568606a0`; their current canonical owners,
DOS-path safeguards, provenance behavior and `InspectRoles` remain authoritative.

`ArtifactManifest.DescribeAcquisition` returns the exact selected-role union,
source identities, pins, platform intent and shared inventory from HostArtifacts.
An immutable provider recipe is derived from that validated identity, not from a
caller URL or eligibility flag. `ArtifactRightsReview` requires a separate
default-No declaration, exact evidence revision/hash, selection and artifact.
Unknown component-license claims require explicit per-claim reviewed terms and
evidence; an aggregate hash or generic approval does not resolve them. These are
unverified local user declarations, not automatic legal clearance or replacement
catalog facts. Private terms are not diagnostic output.

Use the selection-bound overload, for example:

```csharp
var selection = manifest.DescribeAcquisition(
    ["ollama-llm"], "ubuntu-24.04-x64", manifest.FormatVersion == 2 ? "linux/amd64" : null);
var rights = new ArtifactRightsReview(selection, artifactId, reviewRevision,
    reviewedEvidenceSha256, explicitlyReviewedClaims).Authorize(ArtifactRightsDecision.Approve);
using var acquisition = new ArtifactAcquisitionCoordinator(reviewJournal,
    new LocalArtifactAcquisitionStorage(explicitPrivateStagingDirectory));
var preview = await acquisition.PreviewAsync(selection, artifactId, setupPlan, recordedReview, rights);
// Only after the user separately approves this exact displayed acquisition:
var approval = preview.Approve(ArtifactAcquisitionDecision.Approve, preview.Plan.RequiredConsentScopes);
var result = await acquisition.RunAsync(preview.Plan, approval);
```

The variables above are explicit application/user inputs, not built-in approved
catalog facts. Rights authorization expires after ten minutes; renew it and the
local review as necessary. The approval fingerprint freezes the exact plan,
including byte count/reserve and any proposed corrupt-owned-final recovery.

The public `ArtifactAcquisitionCoordinator` owns the production transport and
accepts an already-existing private local staging directory. Preview is passive:
it reads the exact current LocalReview journal and local file/disk facts without
network or payload writes. A fresh default-No one-use acquisition approval binds
source, content hash/bytes, selected roles/platform, rights, staging destination,
disk reserve, action and journal versions. It grants only acquisition of those
bytes. Proposed Unix deployment paths are not automatically adopted as local
staging destinations.

Supported network acquisition is **public GitHub release assets**: an exact
repository/numeric asset API identity, either a direct 200 or one manually
validated 302 to `release-assets.githubusercontent.com`, followed by 200 or an
exact 206 resume. This follows the
[release asset API](https://docs.github.com/en/rest/releases/assets#get-a-release-asset)
and GitHub's [documented release download domain](https://docs.github.com/en/actions/reference/runners/self-hosted-runners#accessible-domains-by-function).
No wildcard CDN origins, arbitrary URLs, credentials, proxies, cookies, automatic
redirects or retries are supported. Signed CDN query URLs are transient
transport secrets, not journal/log/progress/receipt fields. Resume returns to the
same logical API identity to obtain a fresh location and checks the stable content
validator, exact range and total size; changing URL signatures do not change the
artifact. Changed content never silently appends or restarts.

**Hugging Face and OCI acquisition are unsupported before network.** HF's
documented LFS/Xet bridge needs a separately reviewed origin policy; native Xet
token/reconstruction behavior is outside this slice. OCI descriptions remain
metadata-only, with distinct compressed/expanded/staging unknowns and owner-owned
deduplication. No F5 model download, full runtime closure, extraction, registry
pull, execution or installation is implied.

Acquisition has its own strict format-2 `ArtifactAcquisition` journal. Legacy v1,
wrong-purpose, corrupt and foreign files are preserved and refused. The state
machine retains bounded streaming, explicit interruptions, fresh-consent resume,
read-back SHA-256 and atomic non-overwrite finalization. Ownership, file identity
and journal CAS govern cleanup. Corrupt owned finals require an explicitly
reviewed quarantine/reacquisition action; foreign or replaced files are not
overwritten. The private-directory contract still excludes malicious concurrent
replacement of filesystem namespaces.
Recovery hashes at most the declared payload plus configured reserve; a larger
unexpected final is preserved as a conflict rather than scanned without bounds.
One retained quarantine sibling is permitted. Repeated corruption while it is
occupied requires operator review; acquisition never silently purges it.

The source bounds remain 64 KiB streaming buffers, 8 MiB checkpoints, 16 KiB
response headers, 30-second connection timeout, 30-minute default transfer
deadline and 64 MiB default free-space reserve. Bytes are never decompressed.
Acquired compressed/archive bytes are not expanded installation or peak disk.
Payload verification and owned finalization do not set runtime-enabled,
host-ready, GPU-fit or execution-authorized status. Permanent device pairing
is unrelated to review/download expiry and remains unchanged.

The focused regression suite uses authored inert bytes, controlled local HTTP
and actual private journal/partial/final files. No test fetches model, runtime,
driver, engine or registry payloads. Windows directory-commit injection is not
evidence of Linux fsync, power-loss durability, Ubuntu/native behavior or actual
provider qualification. Those gates and real hardware/inference remain unrun.

## H05a local review scope

**Inert planning and real, bounded journal IO, not an installer.** This is the
canonical H05a reuse from `0de03054768cd4122ff4af8d612fcc832b1fc324` ("Add reviewed
host setup journal"), adapted to current shared metadata and installation owners.
It is not the paused `Martlet.HostSetup` draft. Only this module, its tests and
focused deployment documentation are ported; no historical integration ancestry,
Host.Doctor collector/evaluator/native CLI, packaging or workflow is imported.

## Supported boundary

`SetupPlanBuilder` consumes `Martlet.Host.Inventory.HostReport` and
`Martlet.HostArtifacts` inspection. The Inventory project is the sole report,
validation and codec owner. **All reports accepted by this builder are supplied,
unauthenticated metadata**, even after decoding `LiveLocal`, `Observed` or a
recent timestamp. No trusted collector/attestation composition exists here.
Those tags are not actual observations, host ownership, runtime qualification,
pairing readiness, or executable consent. A future authenticated observation
integration needs its own separately reviewed boundary, not a caller boolean.

Every generated plan is blocked for effects; reported-positive host rows become
`Unknown`, and actual available disk stays null. `ExecutionAuthorized` is always
false. Artifact v1/v2 input-document SHA-256, immutable pins and byte subtotals are
metadata, not acquired content verification or license acceptance. The provisional
two-times listed-byte reservation is not a complete install/expanded/peak estimate.
No probe, process, shell, network/download, daemon, model, driver, group, service,
firewall, credential or host-directory operation is performed.

The public coordinator supports exactly:

```csharp
var coordinator = new SetupCoordinator(journalFileSystem, clock);
var preview = await coordinator.PreviewReviewAsync(plan, cancellationToken);
var refusedByDefault = preview.Approve(); // No writes, default No.
// Only after a separate, explicit local decision to record this exact review:
var approval = preview.Approve(
    SetupApprovalDecision.Approve, [SetupConsentScope.LocalJournal]);
var recorded = await coordinator.RecordReviewAsync(plan, approval, cancellationToken);
```

`CanApprove` means **can approve recording review**, including a blocked proposal.
Only the exact `LocalJournal` scope with `SetupPrivilege.None` is accepted.
The plan's proposed future scopes are not requested or granted by this API.
Approval is in-memory, one-use and bound to the exact plan, configuration,
host-report/artifact fingerprints, destination and previewed journal version.
It grants only those review writes, not any proposed external action.

`SetupReviewResult.ReviewRecorded` and `ReviewedSteps` describe historical local
review progress. They never imply external completion. Reopened previews report
`ReviewRecorded`/`JournaledReview`, while every observation stays `Unknown` and
`JournaledComplete` stays false. No stored record is an authenticity or permission
token; even a correctly resealed file is untrusted history.

## Shared planner and frozen adapter

The legacy `Build(configuration, hostReport, inspectionReport)` API is retained
for review and later source reuse. It carries an explicit
`InstallationBindingRequired` blocker: it has no physical-host/owner selection.

The bound overload accepts `(configuration, InstallationRequest, targetHostId,
hostReport, ArtifactManifest)`. It calls `InstallationPlanner.Create` for feature
requirements, active roles, physical-machine grouping, ownership, conflicts and
shared-resource closure. `InstallationMachine` exposes that exact shared projection;
H05a does not implement another general dependency planner or duplicate resource
budget. The entire validated request (including host/runtime/resource/owner IDs,
definitions, feature choices and supplied facts), target host and artifact
inspection identity are frozen into the immutable desired-state fingerprint.
Planner `Eligible` remains a caller-supplied planning conclusion, never authority.

This **frozen Llm/Tts adapter** accepts only the exact active guided-managed roles
on the selected host, with the existing `ollama-llm` / `f5-tts` definition IDs.
Mismatched legacy role configuration, unsupported managed roles and attempts to
adopt existing-service/user-managed runtimes fail explicitly with
`InvalidConfiguration`; they are not silently mapped to defaults. Disabled saved
roles are omitted. Mixed external and managed choices are not adopted. Invalid
Core requests retain the Core validation error; invalid manifests retain the
HostArtifacts reader/inspector error.

Artifact selection uses the owner-owned `ArtifactInspector.InspectRoles` with the
exact selected IDs, before shared closure/unique-byte accounting. Optional extra
catalog roles remain usable but unselected; missing exact IDs fail explicitly.
No artifact schema/validator, role graph, download
inventory or alternate license store is copied here. All source requests remain
blocked even when their shared planner reports no supplied blockers.

The original six bounded steps remain descriptions: review host claims, review
artifact metadata, prepare filesystem, reconcile runtime prerequisites, provision
artifacts, configure gateway service. The last four are deferred, contain no
commands and create/delete nothing. Collocated roles share those proposed steps;
the shared planner projects common runtime/resource identities once. Expected
paths, ports and services are proposed values, not existing or owned resources.

## Durable local review and resume

`LocalSetupFileSystem` touches only one caller-selected absolute `.json` path and
its exact `.pending` sibling in an already existing directory. Network/Windows
device/alternate-stream paths and visible reparse ancestors are rejected.
Directories are rechecked at each IO boundary; this is **not** a handle-relative,
hostile-filesystem symlink-race defense. The caller must select a private local
directory not replaceable by an untrusted actor. No permissions are changed.

Writes own a bounded copy of the input bytes, create the pending file exclusively,
flush file content, compare the expected content version again, replace in the
same directory, and call the directory-commit boundary. CAS and exclusive pending
creation protect cooperating journal writers; arbitrary noncooperating writers
are outside the private-directory contract. IO/CAS/cancellation failures are
explicit. Existing foreign/stale pending files are preserved and block writes.
Only this operation's pending file may be removed on cancellation or a detected
pre-rename version conflict; uncertain IO leaves it for operator review.

Journal **format 2** requires a strict `LocalReview` or `UnqualifiedExecution`
purpose. Public review rejects execution-purpose journals; the internal execution
seam rejects review-purpose journals. Old v1 journals, including `Completed`,
are rejected and preserved byte-for-byte, with no automatic migration,
reinterpretation, deletion, resumption of host actions or ownership inference.
Local review records have zero attempts, no observation/completion fields and
dedicated review fingerprints/timestamps. External completion fields cannot be
populated in that purpose.

The shared codec limits files to 256 KiB/depth 16, rejects unknown/duplicate
members, enum aliases, inconsistent states/bounds and invalid integrity seals.
SHA-256 detects accidental corruption, not malicious replacement or authorship.
There are at most 32 steps, 64 prerequisites/resources and 16 supersessions.
Preview is read-only. Each review step is atomically recorded. Failed/canceled
writes or restart leave only the actually committed historical progress.
Resume requires a new preview and one-use local approval, skips identical
already-recorded review entries and never replays a host effect.

Plan review windows default to 10 minutes, bounded by the supplied required
report timestamps' 15-minute window when available. This limits stale UI review;
it does not authenticate observations. Fresh plan/host metadata can roll forward
only an identical desired state, with new approval and a recorded supersession.
Every step is reviewed again for the new plan fingerprint. Changed configuration,
artifacts, request/host/owner identities or step definitions require a separate
journal; they cannot overwrite existing history. Expiry is checked before each
review entry and before final completion.

**Device pairing is permanent until explicitly revoked**; review-window expiry
is unrelated. No paired readiness, pairing expiry, reboot reset or gateway
credential is stored or issued here. The permanentGateway protocol 2 owner and
its separate trust/credential boundaries are unchanged.

## Preserved reconciliation seam and later ports

The source state machine still supports injected observations, scoped privilege
checks, pre/post-step journaling, partial completion, external-state reconciliation,
conflict refusal and compatible roll-forward. It is **internal/test-only**, not
a production execution API. Dependency injection by itself was not the authority
bug: the missing reviewed collector/executor composition and any interpretation
of imported reports as authority are the boundary to resolve.

Exact adaptations for subsequent reuse:

| Source surface | Current surface / required later change |
| --- | --- |
| Public injected `SetupCoordinator` constructor, `PreviewAsync`, `RunAsync` | Internal; same injected state machine preserved. Public constructor takes filesystem/clock only. Do not expose this seam until separately reviewed observation, ownership and executable-consent composition exists. |
| Public `SetupPreview` / `SetupApproval` | Retained; `IsLocalReview`, exact local scope, cross-purpose refusal added. `JournalVersion` exposes the existing CAS hash (the addition used by `81bf855b3b342563f2dbf2eb512672e717b41932`). |
| Setup contracts, guards, fingerprints, scopes, filesystem/committer interfaces | Retained in the same namespace for acquisition/supervision reuse. No new standalone journal owner. Desired-state hash domain is v2 and includes the bound request/host. |
| Acquisition `81bf855...` setup-journal preconditions | Must explicitly distinguish review metadata from acquisition authorization, revalidate current source identities, and separately approve real acquisition/rights. Review approval is never download consent. |
| Supervision `016f1131e899d1aaa03c7e130534d6d7c5cf108d` requiring `Reviewable` + `Completed` | Cannot substitute `ReviewRecorded` as runtime readiness or deployment authority. Supply a separately reviewed qualified-facts/ownership boundary; no permanent-pairing expiry workaround. |
| Old v1 `Completed` journals | No automatic migration. Preserve as untrusted legacy history; fresh supported review uses format 2 and a new selected destination. |
| `Martlet.Host.Doctor` imports/references | Replace with shared `Martlet.Host.Inventory`; do not restore duplicate report/validation/codec types. |

## Local checks and remaining gates

Use pinned SDK 10.0.401, set `DOTNET_ROOT`, `CI=true`,
`DOTNET_GENERATE_ASPNET_CERTIFICATE=false` and
`DOTNET_SKIP_FIRST_TIME_EXPERIENCE=true` before SDK startup, and select private
artifacts/temp/results directories. Direct projects need no solution/root graph change:

```powershell
dotnet restore tests\Martlet.Host.Setup.Tests\Martlet.Host.Setup.Tests.csproj --locked-mode --artifacts-path $artifacts
dotnet test tests\Martlet.Host.Setup.Tests\Martlet.Host.Setup.Tests.csproj -c Release --no-restore --artifacts-path $artifacts
```

Tests use the canonical Inventory fixture documents (read, not a copied evaluator),
current v1/v2 artifact manifests, the real planner, real temporary journal IO,
fault/cancellation/restart/CAS/concurrent-writer cases and the preserved internal
state-machine suite. They are **Windows managed/temp-file evidence** only.
The production directory committer remains Linux-only (`fsync`); Windows tests
inject a committer to observe that boundary, not to simulate successful native
durability. Linux symlink races, power-loss/fsync, exact-native execution,
hardware, runtime/driver compatibility, inference, boot and reboot qualification
are unrun. H01 PR #21's native qualification/publication hold is unchanged.
No remote CI or host/system installation is part of this slice.
