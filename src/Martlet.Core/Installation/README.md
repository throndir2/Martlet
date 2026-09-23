# H08a installation topology planning

`InstallationPlanner.Create(InstallationRequest)` is a pure, synchronous,
deterministic Core library boundary. It returns **review data**, not an
installation executor, authorization grant, consent receipt, qualified preset
or readiness report. It has no ambient filesystem, network, clock, process,
device, inference, credential-store or host-probe access.

This implements the first foundation of the
[installation execution order](../../../docs/DELIVERY.md#installation-program-execution-order-2026-09-22).
It does not change `AppSettings`, `SetupSettings`, Desktop, provider registration,
the existing OpenAI path, package graphs or host services. H08d owns versioned
persistence/migration; H08b/c own checks and coordinator integration. No new
engine or future product becomes executable through these contracts.

## Input and identity

The sealed input records use immutable arrays, with no retained mutable
collections. Call `Create` (which calls `Validate`) before consuming any result.
Malformed, missing, ambiguous, duplicate, cyclic or over-limit input throws the
existing `ContractException` with `ErrorCode.InvalidContract`. This is an
in-memory planning API, not a new JSON wire or persistent settings format.

* `Features` is the current explicit experience selection.
* `Destinations` contains saved role choices, including inactive choices. A
  role has at most one destination: there is no fallback order. A destination
  binds an exact opaque adapter/model/device `DefinitionId` to a runtime and
  caller-supplied capability/eligibility facts. These are not verified facts.
* `Runtimes` separates an instance, engine ID, exact definition ID and ownership
  from its host and role destinations. An external API has no physical host,
  local dependency resources or managed lifecycle.
* `Hosts` identifies physical machines by nonempty stable UUID, never by display
  label, hostname, address or discovery. A client may also host inference.
  The caller must map containers and WSL/VM instances to their shared physical
  host budget; a virtual machine is not additional physical capacity.
* `Resources` declares the runtime's own demand plus shared dependency demand,
  transitive dependencies, explicit occupancy and owner. Every hosted runtime
  must reference at least one resource; unmeasured demand is null, not zero.
  A resource UUID means the **same** dependency, counted once. A canonical,
  ordinal `Slot` identifies a mutually exclusive claim within one physical
  machine (for example an exact engine/context/port or incompatible package
  slot). Different resource IDs claiming that slot conflict. Slot names and
  definition IDs are bounded opaque identifiers, not parsed commands or URLs.

The caller must provide complete, correctly normalized resource and occupancy
facts, exact definitions and reviewed capability/eligibility facts for the
selected tuple. The planner cannot discover omitted devices, infer free disk,
authenticate identity, detect fabricated facts, resolve real OS paths/ports,
infer a runtime's memory footprint, or qualify a new engine. An `Eligible`
fact, including a `LocalApproval` fact, is only a planning assertion. Actual
execution/consent and scoped live permissions require separate owning systems.
Do not place keys, tokens, endpoints, command scripts or private content in IDs
or labels.

## Feature projection and prerequisites

| Selected feature | Required roles |
| --- | --- |
| None or fixture | None |
| Typed conversation | LLM |
| Microphone input | LLM, STT, capture |
| Spoken replies | LLM, TTS, output |
| Perception | Screen capture, perception; always unavailable |
| Memory | Memory; always unavailable |
| Avatar | Avatar; always unavailable |

Microphone and spoken replies imply their conversation LLM without requiring
the typed feature checkbox too. Combinations union the dependencies. Capture
and output must be native client-host destinations. Future feature dependencies
are visible but never resolve destinations or create host/resource operations,
even if supplied facts claim support. No continuous listening is implemented.

Inactive saved configuration is validated structurally but contributes no
prerequisites, resource demand, keys, host group, probe or operation. Disabling
does not erase it, stop a service, remove shared dependencies or migrate data.
An explicit `InUse` resource is different: it represents real occupied capacity
and its transitive dependencies, even if its old role is no longer selected.
It affects a selected physical host's budget/conflicts without granting any
lifecycle operation. Hosts with no selected implemented role are omitted.

Required prerequisites are emitted even if facts are missing (as `Unknown`):

* LLM/STT/TTS require destination data-consent facts. External APIs additionally
  require a credential-binding fact per selected role, never a key value.
* Capture requires capture-device and data-consent facts; output requires its
  output-device fact.
* Hosted runtimes require host-qualification and runtime-compatibility facts.
  Another physical host additionally requires a pairing fact.
* Guided-managed runtimes additionally require local-approval,
  artifact-eligibility and license-review facts.

Additional explicitly supplied prerequisites (such as reboot) also gate the
owning runtime or role. Host facts cannot be passed as role facts and credential/
data/device facts cannot be shared through a runtime. Required checks here are
planning items, not probes or requests to access devices/keys. Artifact bytes,
download sizes/pins, engine/context verification and actual rights/approval
evidence remain owned by HostArtifacts and later lifecycle/qualification work.

## Output, budgets and ownership

The output has per-feature and per-role states, structured reason/detail IDs,
exact destination/runtime IDs, and per-machine runtime/resource/prerequisite
groups. External APIs form a separate null-host group. Unknown, blocked and
unavailable causes are retained individually; summary precedence is
`Unavailable > Blocked > Unknown > Eligible`. `Eligible` means only that no
supplied planning gate failed. It must never be rendered as "installed",
"qualified", "fully local", "connection checked" or "ready for conversation".

CPU milli-cores, RAM MiB, VRAM MiB and disk MiB are summed per physical machine
over selected dependencies plus explicit in-use dependencies, counted once by
resource ID. Null demand/capacity remains unknown. A zero demand needs no
capacity evidence for that dimension. Equal demand/capacity fits; exceeding any
dimension blocks roles on that machine. This is a conservative simultaneous
resource plan, not a scheduler or a performance guarantee. Unknown/unsupported
selected roles do not silently free their desired resource reservations.
Slot conflicts block their dependent roles. Other hosts/API routes and
independent feature outcomes remain separate; there is no rerouting.

Guided-managed, user-managed Compose and existing-service ownership remain
distinct. Owned dependencies must match a managed runtime's owner identity;
another owner's managed resource is an explicit conflict, never adopted.
A managed runtime may use an external dependency without gaining ownership.
External resources and occupied unrelated resources receive no managed
operation. Shared dependencies remain once with their selected consumer roles.

Only roles with `Eligible` results appear in review operations:
`ReviewRoleConfiguration`, `ReviewManagedRuntime`, `ReviewExternalConnection`
or `ReviewManagedResource`. Shared operations list only eligible selected
consumers. They contain no executable payload, raw script, download, probe,
service start, update, delete, adoption or approval token. Blocked/unknown/
unavailable roles have reasons and prerequisite review data instead.

Output arrays are immutable and canonically ordered (enum order, UUID comparison,
ordinal identifiers); input enumeration order does not change output. Bounds:
7 feature selections, 9 destinations, 16 hosts, 32 runtimes, 128 resources,
16 dependencies per resource/runtime and 11 prerequisite facts per owner.
Resource quantities are 0 through 1,000,000,000 or null; bounded sums use `long`.
Transitive cycles, dangling and cross-physical-host dependency references fail.
These caps bound traversal and output; they are not supported machine-count or
hardware claims.

## Local evidence and remaining gates

`tests/Martlet.Core.Tests/InstallationPlannerTests.cs` exercises the production
planner directly: all 128 feature subsets, typed/microphone/spoken hybrids,
co-location, shared transitive resources, external ownership, occupied budget
and slot conflicts, exact capacity limits, unknown facts, inactive saved
choices, future-product refusal, malformed/default/null/duplicate/bounded
inputs and byte-identical serialized results under input permutation.

Use the pinned SDK 10.0.401 with `CI=true` (locked restore):

```powershell
dotnet test tests\Martlet.Core.Tests\Martlet.Core.Tests.csproj -c Release `
  --filter FullyQualifiedName~InstallationPlannerTests --artifacts-path <session-C-artifacts>
dotnet test tests\Martlet.Core.Tests\Martlet.Core.Tests.csproj -c Release `
  --artifacts-path <session-C-artifacts>
dotnet test Martlet.slnx -c Release --artifacts-path <session-C-artifacts>
```

Initial local developer-host checkpoint (2026-09-22, base
`a748860301f7f7991477be9f655674ce03725b6b`): 92 planner cases, 375 complete Core
cases, and 2,376 integrated solution cases passed, with no skips or failures.
The targeted/Core runs are subsets/repeats, not additional distinct cases.
Independent read-only code review reported no significant issues. SDK
10.0.401, `CI=true`, locked dependency restore and one isolated session-local
`C:` artifacts/results tree were used; no hosted CI was invoked.

No live host, physical audio, provider/account, Ubuntu/WSL, GPU, novice setup,
installation, pairing transport or release qualification is established.
H01's existing native-evidence hold and all AC-18/19/20 integration/real-machine
gates remain separate. This slice does not claim persistence migration.
