# Host setup review, acquisition and configuration publication

## Selected-role configuration publication (H05c-A)

This slice implements actual bounded local configuration publication, **not
service execution or a runnable Compose preset**. It reuses the deterministic
generation and owned-publication approach from
`016f1131e899d1aaa03c7e130534d6d7c5cf108d`, originally authored by throndir with
Copilot App co-authorship. The dependency-lock follow-up
`97a76e94011d851b6ee6a9b5c8b2f4751551c9e9` was inspected, not copied.
Old Doctor imports, setup `Completed` prerequisites, caller `LiveLocal` authority,
timed Gateway v1 assumptions, invented health commands and default-refusing
supervisors are not imported.

`HostDeploymentDefinition` binds the current bound H05a plan/configuration to
the exact HostArtifacts selection and explicit host/deployment/owner IDs.
Only the frozen managed Compose Llm/Tts adapter is accepted; LLM-only requires
neither F5 nor STT. Existing Core `InstallationMachine` owns shared resources,
co-location and demand. Its capacity/eligibility and all Inventory reports
remain unauthenticated planning claims. Unsupported managed roles, another
owner/host or a mismatched artifact target fail explicitly.

`ArtifactAcquisitionCoordinator.ObservePublishedImagesAsync` reads only existing
acquisition state. It reuses native identity/lease and bounded read-back hashing,
validates current v3 journal binding, metadata/configuration graph, selected root
index and layout marker. No HTTP request, download, journal update, repair,
quarantine, extraction or engine operation is performed. Missing owned
publication is explicitly unverified; corrupt, changed, pending or foreign
state is refused. The lease must actually be acquired. Its existing marker is
never created by observation. Detached observations prove only inspected bytes,
not publisher authenticity, legal clearance, Docker image availability or
executable permission.

```csharp
var identity = new HostDeploymentIdentity(targetHostId, deploymentId, ownerId);
var definition = new HostDeploymentDefinition(configuration, setupPlan, selection, identity);
var publisher = new HostDeploymentCoordinator(
    new LocalHostDeploymentStore(existingPrivateConfigurationRoot),
    localReviewStore, localArtifactStorage);
var preview = await publisher.PreviewAsync(definition, recordedReview);
// Display the exact files, destination, scopes and unresolved runtime findings.
var permit = preview.Approve(
    HostDeploymentConfigurationDecision.Publish,
    [HostDeploymentConfigurationScope.LocalConfigurationFiles]);
var result = await publisher.PublishAsync(definition, preview, permit);
```

`Approve()` without arguments is No. The decision is one-use, valid for at most
ten minutes and no longer than the current LocalReview plan window. It binds the
exact bundle, root/file observations, journal versions and selected identities.
It grants **only local configuration-file publication**, not human-authenticated
approval, runtime rights, a listener, model use or host changes. LocalReview is
history, not setup completion. Preview does not create files. Publication holds
the existing acquisition lease through its critical section and revalidates
content before returning success.

The deterministic output is `deployment.json` plus, when selected image bytes
are actually verified, `ollama-llm.compose-fragment.json`. The latter is an
individual **partial service object**, not a top-level Compose document. It
records immutable image/platform, no-pull and proposed isolation constraints,
not a compatible non-root recipe, model mount, gateway, ports, command or health
executable. F5's upstream image is not the Martlet stdio worker host: it produces
explicit missing-recipe findings, not a fabricated runnable fragment. Missing
bytes produce a report-only bundle. All current tuples retain explicit
gateway/service/model/provenance/rights/engine/qualification blockers and
`RunnableComposeAvailable`, `ExecutionAuthorized`, `RuntimeEnabled` and
`HostReady` remain false.

The stable project name derives from the deployment ID, not changing content
hashes; native local ownership also binds the explicit host and owner IDs.
Configuration revisions occupy distinct immutable directories. The manifest's
proposed Ubuntu identity path is independent of revision and remains only a
proposal. Actual Windows staging paths are never converted into Ubuntu mounts.
No secret, credential, certificate or identity store is read or written here.
Permanent Gateway protocol 2 pairing stays permanent until deliberate revocation.

Publication has its own strict format-2 `HostDeploymentConfiguration` journal
and a held cooperative native lease, separate from LocalReview v2, file
acquisition v2 and OCI v3. It retains at most 16 revisions; each bundle is at
most 32 files/1 MiB, each file and journal at most 256 KiB and JSON depth 16.
Duplicate/unknown/noncanonical/corrupt/legacy-purpose state fails closed.
Old deployment v1 is preserved, not migrated. No acquisition `Published` or
`Finalized` state becomes setup `Completed`.

The existing private local parent is validated and pinned. New markers,
directories and files are exclusive creations; only journal-recorded native
identities and verified matching prefixes can resume. Identical foreign bytes,
orphan directories, replacements, links, DOS aliases and unexpected children
are not adopted. Writes use CAS, flush/readback and non-overwrite same-volume
rename. A post-rename interruption reconciles the exact recorded identity,
including retrying directory durability. Creation before durable ownership
can leave a preserved bootstrap conflict requiring operator review.
Unknown pending writes and corrupt state are never silently removed.
No previous revision, gateway identity or unrelated service/data is deleted.

This is a cooperative private/non-hostile namespace contract, not protection
against every hostile ancestor/mount/same-user replacement race. The production
committer remains Linux-only. Windows tests exercise real native identities,
locks, files, JSON and rename with an internal committer fault boundary;
they are not Linux fsync/power-loss or service qualification.

See [the Compose boundary and next executable path](../../deploy/ubuntu/compose/README.md).
No root project/package graph, new package, Docker command, host install,
driver/firewall/group change, live provider or remote CI is added.

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

**Hugging Face acquisition is unsupported before network.** HF's
documented LFS/Xet bridge needs a separately reviewed origin policy; native Xet
token/reconstruction behavior is outside this slice. The separate image API
below acquires only the current named public OCI/Docker image candidates.
Catalog descriptions remain metadata, with distinct compressed/expanded/staging
unknowns and owner-owned deduplication. No F5 model download, full runtime
closure, extraction, Docker pull/run, execution or installation is implied.

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

## Verified selected image acquisition

`PreviewImagesAsync` / `RunImagesAsync` extend the existing coordinator with
actual content acquisition for the **exact selected-role image batch**. The
public production path owns the transport and local IO; there is no public
HTTP handler, arbitrary URL, registry plugin or caller eligibility switch.
HostArtifacts supplies immutable `ImageCandidates` and `ImageContentInventory`
through `DescribeAcquisition`. It remains the only catalog, role-closure,
descriptor-fact and unique-byte accounting owner. The older `Images` and file
candidate APIs and both catalog/inspection wire formats are unchanged.

```csharp
var selection = manifest.DescribeAcquisition(
    selectedRoleIds, "ubuntu-24.04-x64", "linux/amd64");
// Create a selection-bound ArtifactRightsReview for EACH ImageCandidates entry,
// using its ArtifactId, explicit reviewed terms and current evidence.
var preview = await acquisition.PreviewImagesAsync(
    selection, setupPlan, recordedReview, imageRights);
// Only after separate user approval of this exact image batch and destination:
var permit = preview.Approve(
    ArtifactAcquisitionDecision.Approve, preview.Plan.RequiredConsentScopes);
var result = await acquisition.RunImagesAsync(preview.Plan, permit);
```

Rights are separate default-No local declarations, not publisher attestations
or automatic legal clearance. Unknown bundled-component claims need explicit
per-claim reviewed terms and evidence. Extra/missing/duplicate/wrong-selection
authorizations are refused. Approval freezes current LocalReview, selected
roles, catalog/source/content identities, all applicable rights, destination,
local observations, byte/reserve budget, recovery action and journal versions.
It is one-use and expires with the earliest relevant review window.

### Exact public registry profiles

| Image | Registry/repository | Anonymous token realm / service | Blob CDN |
| --- | --- | --- | --- |
| Ollama | `registry-1.docker.io/ollama/ollama` | `https://auth.docker.io/token` / `registry.docker.io` | `production.cloudfront.docker.com` |
| F5 | `ghcr.io/swivid/f5-tts` | `https://ghcr.io/token` / `ghcr.io` | `pkg-containers.githubusercontent.com` |

Only `repository:<exact-repository>:pull` is requested. A single bounded
Bearer challenge must agree with that exact realm, service and resource scope.
The token exchange is anonymous, read-only and in-memory; no personal Docker
configuration, credential helper, login, refresh token or paired-device
credential is used. Private images and other registries/repositories/platforms
are explicitly unsupported. Tokens are not reusable authorization receipts.

The implementation supports direct registry content and one manually validated
302/307 blob/config redirect to the profile's exact CDN. It never forwards the
bearer to the CDN/token endpoint or follows token/manifest redirects. Signed
query text is preserved only for that request, not journaled or emitted.
Each hop rechecks current review/permit/CAS/lease; DNS must resolve exclusively
to vetted public addresses, the socket connects to an already-vetted address,
and ordinary hostname TLS verification remains enabled. No proxy, cookies,
automatic redirects/decompression/retry or cross-provider fallback.

These origins follow the current
[Docker allowlist](https://docs.docker.com/desktop/setup/allow-list/) and
[GitHub package domains](https://docs.github.com/en/actions/reference/runners/self-hosted-runners#accessible-domains-by-function).
Docker [removed its previous Cloudflare/R2 domains](https://github.com/docker/docs/commit/466c7e537900a5efbf6a8bf30efc2b93d6b0908d);
they are not retained as permissive fallbacks. Authentication follows
[Distribution token auth](https://distribution.github.io/distribution/spec/auth/token/).
[GitHub documents anonymous public access](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-container-registry);
its [public source also shows the GHCR token/service recipe](https://github.com/github/gh-aw/blob/c35393777e5604a63721d09512263b1383301d4f/.github/workflows/cli-version-checker.md).
Actual future challenges must still match the frozen policy.

### Verified closure, not runtime qualification

The [Distribution 1.1.1 digest endpoints](https://github.com/opencontainers/distribution-spec/blob/v1.1.1/spec.md#pull)
are requested without tag discovery. Index, selected manifest and configuration
bytes are exact-length/SHA-256 checked before parsing. An optional
`Docker-Content-Digest` header must agree; it never substitutes for hashing.
Supported HTTP and body media types must match. Metadata bodies are bounded
to 1 MiB, token JSON to 64 KiB, bearer values to 8 KiB and headers to 16 KiB.
Duplicate/unknown graph-bearing fields, invalid UTF-8, ambiguous platform
selection, unsupported algorithms/media, extra/missing/swapped descriptors,
foreign `urls`, embedded `data`, nondistributable layers, plugins and artifact
envelopes fail explicitly. This is a curated image profile, not a claim of
general OCI conformance.

All selected small metadata/configuration is validated before any large layer
request. Actual manifest descriptor occurrences must exactly match the
owner-projected catalog, including order and multiplicity; a repeated layer
is downloaded/stored once but retains every ordered occurrence. Config
OS/architecture/variant and rootfs/DiffID declarations are checked structurally.
No config command, health check, label, history entry or archive is executed.
Compressed digests do **not** verify uncompressed DiffIDs. No decompressor,
tar extraction or engine is involved.

The plan exposes known unique encoded content bytes, remaining content bytes,
separate maximum application-read content/control response bytes (including
overflow-detection probes), finite request/token/redirect counts and new-storage
allowance/reserve. The response ceilings are enforced before body reads. These are
not total network wire costs, complete model/runtime downloads, expanded
storage or installation peak disk. Expanded/peak fields remain unknown.
Current authority, owned identities and available reserve are checked during
streaming and bounded read-back hashing, not only before the first request.

### Atomic batch layout and recovery

After approval only, an owned staging sibling is created under the explicitly
selected existing private local parent. Full SHA-256 CAS filenames are used
under `blobs/sha256`. Verified metadata and layers are flushed/read back before
non-overwrite partial finalization. An
[OCI layout](https://github.com/opencontainers/image-spec/blob/v1.1.1/image-layout.md)
marker (`1.0.0`) and generated `index.json` are written inside staging only
after the whole selected closure is verified. A single non-overwrite,
same-volume directory rename publishes the entire batch, followed by native
identity/content checks and journal completion. No partial image is exposed
through the published index or a successful result.

The root index references **only selected image manifests**. Ollama's original
multi-platform index remains an unreferenced verified evidence blob; arm64 is
not fetched or advertised as acquired. F5's original
[Docker schema-2 bytes/media types](https://distribution.github.io/distribution/spec/manifest-v2-2/)
are preserved, not rewritten into a different digest. A downstream consumer
must understand those media types; no Docker interoperability was exercised.

Deduplication is within this exact batch only. Separate role selections may
repeat downloads and disk storage. No global cache savings, hardlinking,
Docker cache adoption or automatic reuse of foreign layouts is claimed.

Image acquisition has a separate strict **format-3 `OciImageAcquisition`**
journal (512 KiB/depth 16). File acquisition remains format 2, LocalReview
remains format 2, and cross-purpose/legacy journals are refused and preserved.
The image lease combines a journal-bound marker identity with an explicitly
successful held cooperative native lock; unsupported locking is an error,
not best-effort acceptance. Resume reacquires only the exact owned marker
after the former lock is released, under a fresh preview/permit and CAS.
Unknown bootstrap markers and pending/foreign writes require operator review.

Checkpoints record actual durable prefix length/SHA-256. Resume verifies the
prefix before any append/request, returns to the logical registry/repository/
digest, obtains fresh ephemeral transport credentials and requires an exact
206 range/total. OCI resume is not tied to a signed URL or CDN ETag.
No full 200 response is silently appended, no unexpected tail is truncated,
and no retry silently increases the approved budget.

Owned corrupt partial/published batches or unusable ranges require a **new
reviewed whole-layout quarantine/reacquisition action** and full replacement
budget. One quarantine sibling is retained; it is never overwritten/purged.
Replaced identities, unknown children, links and excessive unexpected sizes
are preserved as conflicts. Recovery preflights all observed file lengths
against one aggregate tree hash allowance before reading payload bytes.
Reacquisition does not silently adopt retained
quarantine bytes. Exact pre/post-move identities allow recovery after a move
but before journal completion, still under a full replacement budget.
Interrupted revalidation preserves a published layout's location state.
Ambiguous publication never returns success.

Native handles, exact directory/file identities and CAS support the existing
private, non-hostile, non-replaceable namespace contract; they do not defeat
every ancestor, mount or hostile same-user replacement race. Windows tests
exercise the actual exclusive lease and directory-handle behavior, but the
injected committer is not Linux fsync/power-loss evidence. Linux native
locking/durability, live registry availability/rate limits, real payloads,
engine/runtime/GPU compatibility, inference and installation remain unrun.

The result reports only verified selected image bytes/publication. Runtime
enabled, host ready and execution authorized remain false; HostArtifacts
inspection and permanent device pairing are unchanged. This library is not
yet an end-user Ubuntu installer or a completed F5/model-provisioning flow.

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
