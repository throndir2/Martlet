# Linux gateway executable candidate

`Martlet.Gateway.Host.Linux.dll` is an actual standalone foreground executable
over the canonical protocol-2 `DurableGatewayHost`, not a new server or a
completed Ubuntu/Docker installation. Permanent pairings, durable replay
admission, same-key TLS renewal and the existing Kestrel/pinned-client stack
are reused. The Windows `Martlet.Gateway.Host` CLI is unchanged.

**No workers are registered** unless `host.json` lists host **roles**. Every
role is declared the same way and maps to one gateway relay worker for a service
on this host's own numeric HTTP loopback (`ollama`, `stt`, `f5`, `xtts`, `gpt-sovits`, `chatterbox` and `audio2face` exist today):

```json
"roles": [ { "kind": "ollama", "endpoint": "http://127.0.0.1:11434/", "model": "llama3.2:3b" },
           { "kind": "stt", "endpoint": "http://127.0.0.1:8178/", "model": "small" },
           { "kind": "f5", "endpoint": "http://127.0.0.1:50080/", "model": "f5tts-v1-base" },
           { "kind": "audio2face", "endpoint": "http://127.0.0.1:52000/", "model": "claire" } ]
```

Unknown kinds, duplicates, non-loopback endpoints and more than eight roles are
rejected. Listing a role is the host owner's standing permission for paired
`voice` devices to use it. [`martlet-host`](../../deploy/host/README.md)
generates this list; changing it requires `approve-service` again.
Otherwise capabilities contain empty worker/route lists.
Neither a running process nor health means installed models, available
inference, per-action permission or end-to-end installation readiness.
There is no service installer, GUI, network administration API, privileged IPC,
Docker socket, model download, arbitrary executable path or firewall change.

## Actual output and prerequisites

Build with the repository's SDK 10.0.401. Use an owned local artifacts/publish
directory, `CI=true`, `DOTNET_ROOT`/PATH pointing at that SDK,
`DOTNET_GENERATE_ASPNET_CERTIFICATE=false`,
`DOTNET_SKIP_FIRST_TIME_EXPERIENCE=true` and telemetry opt-out.

```powershell
dotnet restore src\Martlet.Gateway.Host.Linux\Martlet.Gateway.Host.Linux.csproj --locked-mode --artifacts-path C:\ChosenArtifacts
dotnet publish src\Martlet.Gateway.Host.Linux\Martlet.Gateway.Host.Linux.csproj -c Release --no-restore --artifacts-path C:\ChosenArtifacts -p:UseAppHost=false -o C:\ChosenPublish
dotnet C:\ChosenPublish\Martlet.Gateway.Host.Linux.dll --help
```

Ship the **entire publish directory**, including
`Martlet.Gateway.Host.Linux.dll`, `.deps.json`, `.runtimeconfig.json` and all
transitive assemblies. This is framework-dependent output, not a self-contained
RID apphost. The actual Linux entrypoint is:

```sh
dotnet /chosen/publish/Martlet.Gateway.Host.Linux.dll serve --config /chosen/private/host.json
```

An operator-supplied Linux x86_64/glibc runtime with compatible .NET 10 and
`Microsoft.AspNetCore.App` 10 is required. The first native custody candidate
targets Ubuntu 24.04 and persistent local ext4. The process must use a stable
non-root UID and nonzero GID, matching real/effective UID and real/effective
GID. Runtime/account/service installation is not performed by these commands.

**Native Linux qualification has NOT RUN on the Windows development host.**
Overlay/tmpfs/NFS/SMB/FUSE and arbitrary Docker volumes/user-namespace mappings
are not supported by the existing custody candidate. A future image must use
this real output/runtime contract and separately establish stable service
identity and admitted mount topology. This is not permission to publish ports,
relax filesystem protection, or enable the currently blocked Compose recipes.

## Strict nonsecret configuration

Only `--config <canonical-absolute-path>/host.json` is accepted. The basename is
literally `host.json`; approval uses the fixed sibling `service-approval.json`.
The optional sibling `machine.json` (written by `martlet-host`, same 0600 owner
custody) is read when the gateway opens and served at `GET /martlet/v1/machine`;
it is not part of the approved configuration, so hardware changes never require
re-approval. A missing or malformed file only means "not reported".
`serve` also keeps the shared [cluster plan](../../docs/CLUSTER.md) that paired
desktops sync through `/martlet/v1/cluster` in the sibling `cluster.json`
(same 0600 custody, replaced atomically through `cluster.staging`). It is not
approved configuration either; a missing or malformed copy starts empty and
desktops push theirs again. The shared voice list lives beside it in
`voices.json`; the shared Home Assistant connection, including its access token,
lives in `home-assistant.json` (at most 16 KiB, through
`home-assistant.staging`). Both are 0600 service-owner files and neither is part
of approval. Its log (own activity plus, as the owner's
[log host](../../docs/DIAGNOSTICS.md#diagnostics-page-and-the-log-host), every
computer's lines) is kept the same way in `logs.json` (at most 2 MiB, through
`logs.staging`); a missing or malformed log starts empty. A host with no roles
is valid and can serve purely as the log host.
The [Martlet network](../../docs/NETWORK.md) roster this host accepted is kept
in `network.json` (same 0600 custody, through `network.staging`); it is not
approved configuration either. Missing means the host is in no network; an
unreadable copy is ignored (with a warning in the host's log) until a paired
desktop binds the host again. `status` reports `network` (`unbound`, `bound` or
`removed` with the network ID and member counts; no keys or addresses).
The network's [API keys](../../docs/API.md) for other apps and scripts are kept
in `api-keys.json` (same 0600 custody, through `api-keys.staging`; names,
scopes and SHA-256 verifiers, never a usable key); a missing or malformed copy
starts empty and paired desktops push theirs again.
The parser rejects extra arguments, environment selectors, approval flags,
secrets and arbitrary command paths. No args, `help`, `--help` and `-h` are
passive and do not read files, create keys or start a listener.

Configuration is UTF-8 JSON, at most 8 KiB and depth four, with exact
case-sensitive required properties. Duplicate/unknown/null fields, comments,
trailing commas, string-encoded numbers and unsupported versions are rejected.
There are no implicit state paths, identities or ports:

```json
{
  "schemaVersion": 1,
  "hostId": "home-host",
  "stateDirectory": "/srv/martlet/private/gateway-identity",
  "storageBackend": "linuxServicePermissions",
  "binding": {
    "mode": "loopback",
    "origin": "https://127.0.0.1:9443"
  },
  "serviceUid": 1001,
  "serviceGid": 1001
}
```

The IDs and paths are examples, not provisioning instructions. Host IDs are
bounded ASCII identifiers; paths are canonical printable-ASCII absolute Linux
paths without dot segments, repeated separators, trailing separators or
backslashes. The new loopback binding admits only `127.0.0.1` and `::1`, matching
its certificate policy; other `127/8` addresses fail before state creation.
Private-LAN configuration must explicitly select `"mode":"privateIp"`
and one canonical HTTPS RFC1918/ULA literal origin, for example
`https://192.168.10.20:9443`. No public/wildcard/unspecified address, hostname,
discovery, link-local scope, automatic interface selection or fallback exists.
The selected address must actually exist on the host when starting.
Inside a container (`/.dockerenv` or `/run/.containerenv` present) the
`martlet-host` Docker method selects `"mode":"published"` instead: the same
private origin names the Docker host's published address for the certificate and
clients, while Kestrel listens on the container's wildcard address. `published`
is refused outside a container.

The existing config parent and selected state parent must be current-UID-owned
0700 directories on admitted ext4; ancestors are root/current-UID-owned, not
group/other writable, without ACLs or links. Config/approval must be regular,
same-UID 0600 files with one link. Descriptor-relative no-follow reads hold
and recheck ancestor/file identities, permissions, ACLs, mounts, bounded lengths
and config bytes. No permission repair, ancestor creation or chown occurs.
Keep config and approval **outside** the authority leaf, whose exact five-name
allowlist is unchanged. The CLI refuses a config path inside the state leaf.
State paths colliding with (or descending through) the three reserved control
filenames are also rejected before opening/creating authority.

`LinuxServicePermissions` is **plaintext at rest**, not DPAPI, a vault or
encryption. It does not protect against same-UID code, root, compromised OS,
offline disk/backup readers, memory inspection or complete-state rollback.
Operator-managed disk encryption is separate and is neither detected nor set up.

## Commands and local approval

| Command | Behavior |
| --- | --- |
| `validate` | Read selected config with native custody checks; report syntax/custody, not authority/network/model readiness. No state owner or write. Unsupported native environments fail explicitly. |
| `status` | Read config and any approval; report matching/absent approval and `runtime:"not-observed"`. Never infer liveness from a PID or running marker, open state, or probe the network. |
| `init` | Foreground TTY, exact default-No confirmation; create only the selected absent state leaf. Display the resulting host ID/SPKI and enter administration with listener stopped. |
| `admin` | Foreground TTY, exact default-No confirmation; open existing state only. The daemon must be stopped because the same sole-owner lock applies. |
| `rebind` | Foreground TTY; require the protected prior service receipt as an expected host/pin witness, review the new exact origin and confirm same-key rebind. The old receipt does not approve the new config. |
| `serve` | Read and recheck protected approval; open only the existing matching identity, start the exact approved listener and wait for cancellation. No stdin or password prompt; no state creation or model authority. |
| `health` | Explicit bounded pinned HTTPS observation using the approved nonsecret pin; not a passive command. |
| `owner-init` | Owner command, no console: create the selected absent state leaf (as `init`) and approve unattended serve of exactly this configuration and identity (as `approve-service`), then close. |
| `owner-approve` | Owner command, no console: open the existing identity (as `admin`, which also accepts an approval for an earlier config of the same host, UID/GID and pin) and approve unattended serve of exactly this configuration, then close. |
| `owner-pair` | Owner command, no console. `owner-pair --config <path> [--roles voice]`: open the existing identity, start the listener, open one five-minute short-code window and print the host's address and an `XXXX-XXXX` code for a person to type in Martlet (**Enter a pairing code**); whichever desktop proves the code names itself ([short typed codes](../Martlet.Gateway/README.md#short-typed-codes)). `owner-pair --config <path> --device-id <id> --name <display name> [--roles voice]`: create one five-minute invitation for exactly that device and print it as one `pairing-code: martlet-pair-v1...` line (Martlet reads it over SSH or on this PC). Either way, wait until a new credential registers (exit 0), the invitation expires or a `cancel` line arrives on stdin (exit 3), then close cleanly. The daemon must be stopped, as for `admin`. |
| `owner-network-reset` | Owner command, no console (`martlet-host network-reset`, daemon stopped): remove `network.json`, so the host is in no [Martlet network](../../docs/NETWORK.md) and the next desktop that pairs binds it to its own. Pairings stay (revoke them with `admin`). |

Administration commands are `start`, `pair`, `list`, `revoke`,
`approve-service`, `disable-service`, `stop`, `help`. Authority-changing actions
each require exactly lower-case `yes`; Enter/other bounded input means No.
Ctrl-D/EOF at any prompt closes the session. No console confirmation can be piped.

**Owner commands.** At the owner's explicit request, the host's own account can
also run the owner operations without a console: `owner-init`, `owner-approve`
and `owner-pair`. They exist for `martlet-host --yes`, which Martlet desktop runs
over the owner's authenticated SSH session after the owner clicks the action in
Martlet; that session is the trusted local owner channel and the click is the
confirmation. Running them requires the same UID, native custody and stopped
daemon as `admin`, so they grant nothing that same-UID code could not already do
with plaintext-at-rest state. `owner-pair` writes the one-use invitation to
stdout (for the desktop to read and redeem at once), not to the owned terminal's
alternate screen: treat that stdout as secret for its five-minute life.

`start` separately approves the selected listener. `pair` requires it already
started, reviews the exact device/name/roles and obtains a fresh approval before
creating a one-use five-minute invitation. **The interactive owner/listener
remains alive after disclosure**, so the client can redeem the invitation
while the operator keeps the session open. `list` confirms permanent paired
registrations, not connected sessions. Stop the admin session only after the
desired pairing completes; its pending invitation is intentionally not durable.
Later approved daemon restarts retain the identity and successful pairings.
`revoke` removes only the exact approved device and its invitations.

`approve-service` separately approves unattended restart for this exact
configuration and actual host identity. The private sibling receipt contains
version 1, scope `gateway-listener-start`, exact config-byte SHA-256, host
ID/SPKI and actual UID/GID. Its controlled write is atomic and directory-flushed;
an interrupted `service-approval.staging` is preserved and blocks another write
until explicitly reconciled. It is not an approval Boolean in public config
or an imported Setup receipt. Native custody is the boundary against other
UIDs; this is not a cryptographic attestation against malicious same-UID/root
code. No second private key, encryption-key copy or device credential exists.

`serve` rechecks config, receipt and actual identity before listener start and
again after start. Changed bytes (even whitespace), UID/GID, host/pin, backend,
origin, missing receipt or unsafe custody refuse unattended start. Config is
frozen for that process; no live reload. `disable-service` removes unattended
approval without changing pairings. Admin can reopen an unapproved existing
identity by its expected host ID under native custody, then display/review its
pin before granting new approval; this is not remote authentication.
Admin also opens when an approval exists for an earlier config of the same host,
UID/GID and pin (for example after a role was added), so `approve-service` can
renew it; `serve` still requires the exact config. Each console `pair` invitation
also prints one `martlet-pair-v1.<base64url JSON>` code carrying the origin, host
ID, pin, pairing ID and token for pasting into the desktop; `owner-pair` without
a device shows a short typed code instead (`martlet-host pair`).

## Binding changes, certificates and recovery

Existing durable factories remain loopback-only/default No. Explicit named
`CreateNewForBinding` / `OpenExistingForBinding` factories accept
`GatewayHostBinding`; named methods preserve existing null/default-No callers.
`OpenForLocalAdministration` and `RebindForLocalHost` remain local capabilities,
never passed to handlers/DI. The new API is explicitly backend-selected; it
does not silently switch Windows callers to Linux custody.

Legacy certificates retain the two canonical loopback SANs. Explicit private
certificates add **one** selected private-IP SAN; strict loading rejects other
forms. The listener still binds exactly one selected origin, not every SAN.
Normal open will not add a missing SAN. Automatic/forced renewal preserves the
same key/SPKI, admitted SANs, current credentials, revocations and replay state.

For a changed binding, stop the daemon, retain the protected old receipt,
deliberately edit config, then run `rebind`. It validates old receipt custody
and expected current identity/pin, prompts for the exact new scope, and durably
reissues the certificate with the same key before removing the old service
approval. Explicit `approve-service` is then required for the new config.
The prior private SAN is replaced, not accumulated. Rebinding to loopback
removes the private SAN. No clients are unpaired; their endpoint configuration
must be deliberately updated without forgetting their verified identity.
If there is no prior receipt, approve the currently valid configuration first.

An interruption/removal failure can leave the certificate committed and an old
receipt present. The new config still cannot use that old digest to start.
The executable reports `rebind.approval_cleanup_failed` if removal fails;
preserve the same state and reconcile/retry local rebind, then explicitly
reapprove. Do not delete identity files, fabricate receipts, restore stale
backups or create a replacement host. Missing/corrupt state, wrong backend,
legacy `MigrationRequired`, clock or permission errors remain data-preserving
blocks. Routine restarts, supported-format updates and recoverable crashes
retain permanent pairings; invitation/request freshness remains bounded.

## Linux terminal and process lifetime

The Linux adapter requires stdin/stdout/stderr to refer to the same current-UID
character terminal, controlling session and foreground process group. It holds
its own verified descriptor, uses bounded input with native polling, and
restores terminal modes. Invitation disclosure additionally requires a terminal
advertising `xterm` or `xterm-256color` and at least 80 columns by 20 rows.
Other terminals, redirection and unsupported native APIs fail with no fallback.

After explicit warning/approval excluding observers and recording, invitation
bytes go directly to the owned terminal's temporary alternate screen, not
stdout/stderr, a log/file, clipboard, arguments or environment. A key, deadline
or cancellation clears the temporary screen and restores modes/screen.
Owned-terminal writes are nonblocking, cancellation-aware and bounded to two
seconds per write (including uncanceled best-effort screen cleanup). Input
temporarily disables software flow control and resumes suspended output before
use, then restores the original modes. Backpressure/cleanup failure is an error,
not a promise that the screen was erased; it cannot hold the authority forever.
**POSIX has no universally secure display:** TERM is only a capability hint,
not permission or proof of privacy. This does not defeat terminal recording,
same-UID programs, screenshots, root, compromised terminals or memory reads.
Managed secret strings cannot be reliably erased. Actual Linux terminal
behavior is compiled but unqualified here; the native PTY target must be run
in an authorized environment before claiming support for that environment.

`serve` stays in the foreground; SIGTERM and Ctrl+C/SIGINT cancel through one
owner. Daemon stdin closure does not grant/withdraw approval or stop service.
Interactive EOF does close. Cleanup uses a fresh, uncanceled path: stop
admissions, drain, checkpoint, release. Existing 30-second listener-drain
ownership and one final disposal attempt are retained; uncertain owners stay
held until real cleanup or process exit. Never report clean success or release
state underneath an uncertain listener. No global process killing occurs.
Synchronous filesystem flushes have no claimed hard wall-clock bound.

## Exact health contract

The existing TLS listener preserves `GET /health/live` and its exact
`{"status":"live"}` document. New `GET /health/ready` has this **snake_case**
version-1 wire document:

```json
{"schema_version":1,"scope":"listener-auth-admission","listener":"listening","auth_admission":"open","model_readiness":"not-probed"}
```

HTTP 200 means this TLS handler answered and the existing authority's
lock-protected admission observation is open. HTTP 503 uses
`"auth_admission":"closed"` for known closure/stopping or observed clock/storage
failure. No IDs, paths, pairing counts, keys, credentials or detailed failures
are disclosed. The observation does not issue a credential, admit a nonce,
sweep/checkpoint state, probe workers or grant permission. It does not prove
future storage success, a signed request, connected clients or model readiness.
A closed authority may also refuse a new TLS handshake under the existing
certificate-selector policy. Normal TLS renewal keeps its existing behavior.

Both health routes have no extra port or admin capability. Authenticated,
role-scoped `/martlet/v1/status` is unchanged and is not anonymously aliased.
`health --config` uses the receipt's exact approved pin/origin and the existing
no-proxy/no-redirect client, with no credential, discovery or retry. The entire
operation including body read is bounded to three seconds and 2 KiB.
Generic liveness, wrong pin, non-200, invalid/unknown/duplicate schema, timeout
or oversized body is never reported healthy.

Exit codes: 0 successful passive/healthy command or clean completion; 2 invalid
input/config; 3 missing approval/refusal/TTY requirement; 4 platform/custody/
state/operational failure; 5 uncertain cleanup; 6 unavailable/invalid health
(including invalid health-command config); 130 cancellation after cleanup.
Output failure cannot turn failed cleanup into success. Error diagnostics are
bounded categories, never raw exception, request or secret contents.

## Evidence boundaries

Run `Martlet.Gateway.Host.Linux.Tests`, Gateway, Persistence portable/native
Windows and unchanged Windows Host suites locally with locked dependencies.
New tests exercise production parser/custody/lifecycle through internal-only
native models, plus actual pinned loopback Kestrel on Windows. They cover
default-No, exact private SAN/renewal/rebind, permanent pairing/redemption/
replay/revocation across admin-to-daemon restart, owner-init/owner-approve/
owner-pair (redeemed and canceled invitations), empty registries, approval
byte/identity binding, invalid custody, health distinctions and cleanup failures.
They are not Linux filesystem, terminal, LAN or model qualification.

`Martlet.Gateway.Host.Linux.Native.Tests` separately compiles an actual
ext4/UID/config/approval/foreground-process/SIGTERM test and an owned PTY
input/disclosure/restoration test using synthetic secrets. It requires an
explicit authorized `MARTLET_LINUX_TEST_PARENT` private ext4 parent and selected
`DOTNET_ROOT`; absent native prerequisites fail as **NOT RUN**, not passing
platform skips. The production executable has no test or approval-bypass flags.

Native Linux tests are **compiled, NOT RUN** here. Real Ubuntu installation,
service boot without login, OS reboot/power loss, graphical terminal matrix,
private-LAN/two-host TLS, IPv6 interface availability, Docker namespace/mount
mapping, clean-machine runtime, GUI and real engine/model qualification remain
NOT RUN/unqualified. No historical receipt or H01 collector substitutes.
