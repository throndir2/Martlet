# Verified launcher authority

This Windows x64 library owns an exact signed staged apphost, not an installer or
an application update UI. It reuses `833923e84e093a56917b5608e04090fa55847d09`
and the fixture output correction `051b642de58c135c51965de1e4f90258e80dc7cd`,
adapted to the current Updates history, schema-4 settings and non-runnable
activation receipts. No root solution, Desktop or packaging integration is included.

## Explicit host policy and consent

`LauncherActivationComposition.Create` installs the closed process/readiness
adapter. Its optional `publisherPolicy` provider must return a current
`LauncherPublisherPolicy` provisioned by the trusted host **independently of
candidate metadata**. Missing/empty policy refuses execution. The library does
not authenticate the provisioning provenance of caller-supplied policy bytes.
Never derive grants from a candidate, trust on first use, or accept a test
signer's key as a production publisher.

`UpdateTrustPolicy` still performs the existing RSA-PSS verification; no second
signature verifier is introduced. A staging key alone cannot authorize execution.
Each execution grant additionally names exact signer ID, four-part version,
archive/manifest/executable SHA-256, source commit and dirty bit, and one purpose
(`ActivationReadiness` or `DesktopLaunch`). There are at most 32 grants, a bounded
case-preserved policy revision and an expiry. Source facts are read from the
bounded, signed-inventory-verified payload manifest, not an unsigned sidecar.
Current policy is sampled again at use and before final acceptance/publication.
A changed policy digest requires fresh review. The host must not return stale
cached authority after revocation.
The sampled immutable expiry is also checked without callbacks immediately
before native creation/resumption and final pointer rename, after expensive
evidence reads. Expected policy-provider IO, parsing, timeout and configuration
errors become redacted `PublisherUnavailable`; caller cancellation retains its
original token. No failed policy read falls back to previous policy.

The supported fence is native Windows x64, `win-x64`, canonical versions
`0.1.0.0` through (excluding) `1.0.0.0`, schemas 1-4 within signed reader bounds,
and only `Desktop/Martlet.Desktop.exe`. There is no arbitrary executable argument.

Activation approval binds its existing transaction/revision/settings fields plus
the exact execution-policy scope. Desktop launch has its own default-No review:

```csharp
var plan = launcher.PrepareLaunch(expectedActivationRevision, cancellationToken);
// Present the exact plan and effects. Do not call Approve on dismissal/default No.
var approval = plan.Approve(plan.OperationId, plan.PlanDigest);
var result = await launcher.LaunchActiveAsync(plan, approval, cancellationToken);
```

Plans and approvals have no public constructor. They are instance-bound and
one-use; a newer plan, changed settings, changed current pointer/policy, or two
minutes of monotonic/UTC age invalidates launch consent. All synchronous
preparation/activation operations must be offloaded and awaited by a UI host.
Do not abandon their task on cancellation.

**Source API changes:** the old revision-only `LaunchActiveAsync(long, ...)`
and public callback-based `WithVerifiedActive` are not exposed. Later host code
must provision publisher policy and explicitly review `DesktopLaunchPlan`.
The test-only extension that automatically approves exists only in the test assembly.

## Process, readiness and lifetime

The exact file is SHA-256 checked and pinned. `CreateProcessW` uses an absolute
application name, no shell, zero inherited handles and an explicit Unicode
environment. The child starts suspended, its OS image path is checked, it joins
an unnamed kill-on-close Job (at most 32 processes), then its thread resumes.
This reuses the source's existing native suspended start; no LocalStt dependency
or duplicate helper library is added.

Only Windows system roots, an attempt-owned private runtime directory for
USERPROFILE/LOCALAPPDATA/APPDATA/TEMP/TMP, diagnostics-disable flags and the private
readiness binding enter the environment. PATH, COMSPEC, credentials, SDK override
variables and real personal data roots are not inherited. This is environment
isolation, **not an OS sandbox**. Execution is allowed only under the trusted
host's exact grant.

The one-use current-user pipe verifies nonce, purpose, version, profile,
case-preserved settings revision, archive/executable hashes, OS client PID and
image path. Its canonical frame is bounded to 4096 bytes and at most 30 seconds,
using both monotonic and UTC clocks. The archive hash transitively binds signed
source identity. Protocol/message version 1 is retained; unsupported versions,
duplicate/unknown fields and noncanonical JSON are rejected.

The activation probe always stops its owned tree before returning. Its profile
lease remains held through final pointer publication. A successful probe does
not transform a persisted pointer or receipt into launch permission:
`ActivationReceipt.Readiness` remains `MissingReadiness`, `IsRunnable` remains
false, including fresh results, reopen and recovery. Actual Desktop launch
requires fresh policy, consent and a new process handshake.

For Desktop launch, activation/selection history, current settings writer scope,
signed staging files, executable pin, profile lease and Job remain held until
the **entire owned tree has ended**. This deliberately differs from the source,
which released state/settings pins after readiness. A future Desktop host must
resolve its settings-write integration without weakening this ownership boundary.
Main-process exit, cancellation and rejected readiness stop only this Job.
A five-second cleanup budget failure remains a failure, but ownership is not
released until actual retirement; a stuck OS can therefore delay completion.
PID 259 is treated as a valid exit code when the process handle is signaled.

## Evidence, rollback and limitations

The profile lock is in the authoritative activation root. Changing the
launcher-evidence root cannot allow a second cooperating owner. Create-only
`intent-v1.json`, `readiness-v1.json`, `terminal-v1.json` documents reside in
`profile-<id>\attempt-<id>` under a separate existing private root. Each document
is at most 16 KiB, with at most 64 attempts per profile and 64 root children.
Nonce, pipe name and absolute private paths are not persisted. Abrupt loss can
leave incomplete evidence; restart is a new attempt, not resumed consent.
There is no automatic deletion, retry or fallback.

`DeterministicRollbackOrchestrator` exposes only explicit retained N-1 selection,
separately approved real configuration restore/acknowledgment, then separately
approved activation. Its retained selection can recover the active N-1 even
when selection is ahead of activation. Rollback uses the latest compatible
snapshot from the authoritative lineage, not merely the snapshot taken when the
version was first selected. Historical schema-1-through-4 receipts retain
their meaning; a new restore uses Core's current schema-4 privacy review policy.
The launcher never reads Gateway vault/pairing state and never expires, resets
or restores permanent device pairing.

Tests execute only the authored inert compiled fixture in test-owned C: paths,
with an explicitly test-owned signer/policy. These are not publisher credentials,
release qualification, actual Desktop initialization or installer evidence.
Same-user/admin replacement of all private history, malicious authorized code,
OS denial of service and power-loss durability are outside this boundary.

Deferred: real Desktop initialization/settings coordination, publisher policy
provisioning and key lifecycle, Authenticode, installer/shortcut/package wiring,
clean standard-user Windows qualification, physical failure tests and release
approval. This library performs no network, signing, installation, permission
changes, service/registry registration or production deployment.
