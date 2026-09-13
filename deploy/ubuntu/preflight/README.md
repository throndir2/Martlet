# Martlet Ubuntu host doctor (H01, internal build)

**Read-only prerequisite discovery, not a self-host installer or a qualified
deployment.** The Windows application/API route remains the current product.
There are **zero qualified GPU/driver/image/model tuples**. H01 implements only
the discovery part of AC-12; it does not pass G3 or enable a self-host preset.

The initial diagnostic target is **Ubuntu 24.04 LTS x86_64**. Ubuntu 22.04/26.04
and other Linux distributions are **unqualified**, not automatically binary
incompatible. WSL/container observations do not qualify a physical Ubuntu host.
Integer OS versions such as Debian 13 / Fedora 44 remain sanitized, observed
version strings with `HOST_UNQUALIFIED_PLATFORM` / `ReviewTarget`, not malformed
tool versions. NVIDIA/package numeric version validation is separate.
Windows/non-x64 live execution returns unsupported (3); help and authored
fixtures work in developer builds on Windows without Linux or a GPU. A Linux
ELF cannot itself start on Windows/ARM; use the matching developer build there.

## Novice quickstart

This is an **unsigned internal engineering package**, not a publicly released,
signed novice installer. Obtain the exact internal package and expected SHA-256
from your trusted build operator; do not bypass publisher/security policy.
An independently reviewed release channel and rights/notices are still gates.
Do not run with `sudo`.

After the trusted operator extracts the package into a folder you choose, open
a terminal **in that folder**:

```sh
./martlet-host --help
./martlet-host doctor --scope inventory
./martlet-host doctor
```

The real packaged `martlet-host` is a self-contained native Linux ELF, not a
shell script named like a binary. It bundles its .NET implementation; **no .NET,
SDK, Git, Python, or Docker is needed simply to read the report**. It relies on
ordinary Ubuntu 24.04 system libraries. An older/missing system library can
prevent ELF startup before a report is possible; do not install random libraries
to work around that unsupported target.

`inventory` reads only fixed local files and root-filesystem capacity. The
default `prerequisites` scope also queries the local package database and
NVIDIA driver visibility. Neither scope installs, repairs, downloads, starts
services, changes groups/permissions, opens ports, or runs containers/models.
`--no-gpu-query` explicitly leaves the NVIDIA check **NOT RUN**.

Read each **code**, **Observed**, and **Next** line. An observed tool version
does not mean that an AI worker works. Review the version-appropriate official
guide with your administrator before any later manual change. Preserve existing
Docker, driver, Secure Boot and firewall policies. Package/driver changes can
need administrator privileges, downloads and an owner-scheduled reboot; this
tool performs none. No reinstall-everything or curl-pipe-root remedy is offered.
Unknown exact versions/download sizes remain unknown until H02 selects them.

For an explicit local export using your shell (choose a **new** filename):

```sh
./martlet-host doctor --json > martlet-host-report.json
```

The CLI itself writes only stdout/stderr, not a report file or configuration.
Shell `>` may overwrite an existing file, so choose carefully. The JSON omits
hostname, IP/interface names, user/home paths, GPU UUID/serial, environment,
native stderr, process lists and arbitrary tool output. It includes GPU model
and driver numeric version, capacity counts, UTC times and diagnostic metadata.
Review before sharing. Nothing is uploaded; no local-identifier opt-in exists.

The proposed gateway port is 7443. To observe another agreed port:

```sh
./martlet-host doctor --scope inventory --port 7444
```

This reads local socket tables without binding or connecting. **No collision
observed is not gateway available**, not firewall isolation, not pairing.
Occupancy can include an unrelated listener or a locally bound connection in
this namespace. Do not stop another service just to clear the result.

| Exit | Meaning |
| --- | --- |
| 0 | Every required check in the requested scope has a valid observation. **Not deployment readiness**, including for an authored fixture. |
| 1 | A required prerequisite is missing or the chosen port is occupied. |
| 2 | Required checks are incomplete, unknown, degraded, canceled or not run. Full prerequisites remains incomplete even when packages are installed: daemon access and H02 tuple are unverified. |
| 3 | Invalid/duplicate arguments, incompatible report/configuration, or unsupported live execution architecture/OS family. No fake Ubuntu-ready fallback. |

Informational reboot/qualification/not-requested rows remain visible without
changing the requested inventory scope into an inference test. Errors take
precedence over incomplete checks (unsupported execution first, then required
missing/conflict, then incomplete). No `Ready`/`AI working` badge exists.

## Offline example: not this machine, not AI

```sh
./martlet-host fixtures
./martlet-host doctor --fixture inventory --scope inventory
./martlet-host doctor --fixture missing-tools --json
```

The inventory fixture describes **invented**, deliberately modest 2 logical
CPUs, 8 GiB total RAM / 4 GiB available, 64 GiB available on `/`, and a local
port without an observed collision. It is not the hardware owner's inventory.
Representative fields from its deterministic report:

```json
{
  "schemaVersion": 1,
  "applicationVersion": "0.1.0",
  "manifestId": "ubuntu-24.04-x64-h01-candidate",
  "scope": "Inventory",
  "provenance": "AuthoredFixture",
  "createdAt": "2026-09-13T00:00:10+00:00",
  "deploymentQualified": false,
  "exitCode": 0
}
```

This excerpt is not a full input document. Full reports include typed evidence,
requiredness, stable finding/action codes, remedies/official URLs, observation
UTC, age/duration in milliseconds, fixed source descriptions, and the embedded
candidate requirements/unknown tuple. Built-in fixtures exercise the production
parsers/evaluator; they cannot select executables or supply shell commands.
All current finding codes have fixture/remedy coverage.

## Exact read-only boundaries

| Area | What is actually observed | What is not established |
| --- | --- | --- |
| Platform/context | Runtime OS/process architecture/.NET version; selected `/etc/os-release` data; numeric kernel/known flavor; fixed container markers and `/proc/1/cgroup` indicators | Physical-host proof, supported deployment on another distro/version, Secure Boot/module policy |
| CPU/memory | Logical processor count and intersection of four selected x86 flags; MemTotal/MemAvailable/SwapTotal/SwapFree, strict KiB-to-byte conversion | Selected native wheel instruction-set needs; 4 cores / 32 GiB are **planning recommendations**, not invented failure thresholds |
| Disk | Caller-available bytes and total bytes on `/` | Free inodes, another mount/model directory, selected artifact size/staging/rollback budget |
| NVIDIA | Fixed `nvidia-smi` name/driver_version/memory.total CSV query; up to 16 recognizable model names; MiB units, not free VRAM | UUID/serial, compute capability, CUDA kernels, PyTorch, container GPU, image compatibility, model fit/load/inference |
| Engine/plugin/toolkit | Approved local `dpkg-query` package states and numeric version cores, plus known system-file metadata | Exact distro build pin, manually installed or user-home plugin versions, Engine liveness, credential helpers, Docker contexts, remote `DOCKER_HOST` |
| Docker privilege/access | Effective-root / docker-GID membership from fixed local files, standard socket presence, explicit root-equivalence warning | Effective daemon access: **UNKNOWN**. No socket connection, group grant, or permission change |
| Network/port/clock | Current-namespace interface/address-shape counts from proc files; bounded TCP/UDP port occupancy; local UTC | Addresses in the report, LAN discovery/reachability/DNS, TLS/clock accuracy, router/NAT/firewall configuration or unauthorized-source tests |
| Reboot | `/run/reboot-required` marker | Successful prior reboot, absent driver-specific reboot need, service boot behavior |

The only child commands in production, after explicit `doctor` with
prerequisites scope, are:

```text
/usr/bin/dpkg-query --admindir=/var/lib/dpkg --show --showformat=${binary:Package}\t${Version}\t${db:Status-Status}\n docker-ce docker.io docker-compose-plugin docker-compose-v2 nvidia-container-toolkit nvidia-container-toolkit-base
/usr/bin/nvidia-smi --query-gpu=name,driver_version,memory.total --format=csv,noheader,nounits
```

These are argument-list forms, **not shell snippets to source**. No PATH search,
shell, report-provided executable, inherited process environment, Docker CLI,
plugin/credential helper execution or remote daemon/context is used. Children
get only fixed `PATH`, `LANG`, `LC_ALL`, and `HOME=/`. Package absence differs
from permission denied, unsupported query, nonzero exit, timeout, malformed
data and truncation. Package numeric version cores deliberately omit arbitrary
distro suffixes; **they are not exact package build locks**. Unrecognized GPU
name shapes are reported malformed rather than dumping possibly identifying
text or pretending every future GPU is qualified.

An unavailable proc table (including disabled IPv6) means incomplete evidence,
not a demand to enable IPv6. If `dpkg-query` itself is absent, package state/
version is unknown; it does not prove Engine, Compose, or toolkit absent.

One child at a time, at most two per run; 5 seconds each, 32 KiB stdout / 8 KiB
stderr independently while reading. Limit/timeout/cancel discards partial
output and stops only the owned spawned tree, awaiting actual termination.
Original cancellation tokens are checked before spawn and acceptance, not just
linked callbacks. Raw native errors never reach the report.

Fixed file registry: OS release 16 KiB, kernel 512 bytes, cpuinfo 2 MiB (4096
logical processors), meminfo/cgroup/status/network-dev/IPv6 32 KiB each,
groups 64 KiB, fib/tcp/tcp6/udp/udp6 256 KiB each. Text reads have a 2-second
cooperative deadline and an explicit byte cap; Linux nonblocking opens prevent
FIFO-open hangs. At most 64 interfaces, 256 distinct addresses and 4096 rows
per socket table. No recursive home/filesystem search or process/environment
dump. Standard local filesystem/proc reads still depend on the kernel returning;
this is not an OS/hung-kernel watchdog. Ordinary filesystem read-atime behavior
is not an application write. Report validation rejects unknown schemas/enums,
oversized input and inconsistent typed evidence.

## Contracts and ownership

Public entry points are `DoctorCommand`, `IHostSource` / immutable
`HostSnapshot`, `ICommandRunner`, `HostEvaluator.Evaluate`, `HostReport`,
`ProbeResult` / closed typed `Evidence`, `CandidateManifest.Current`,
`RemedyCatalog`, `FixtureCatalog`, `HostJson.Serialize`, and `ReportFormatter`.
`HostSnapshot` is bounded source input, **not a shareable raw log**; only the
validated projection is exported. Sources and commands are injectable for
contract tests; the CLI accepts no external source/manifest/command paths.

This does **not** duplicate Windows foundation/settings diagnosis. Existing
`Martlet.Diagnostics` reports/executor are coupled to desktop setup, sessions,
shared stages and a different catalog. Reusing them would require changes to
shared schema ownership and misrepresent host results. H01 instead has one
host-owned pure evaluator shared by human/JSON projections and one bounded
command runner, with no UI/core/provider/settings changes. A future gateway can
reference these portable host contracts without pretending FoundationStatusService
is a Linux detector. Any eventual shared-schema integration requires review.

The typed [candidate manifest](candidate-requirements.json) records Ubuntu 24.04
x64 discovery and candidate tools/roles. Only the Compose v2 **family** is a known
constraint; exact driver/CUDA/engine/toolkit/image/model pins are unknown.
`qualifiedTuple` keeps OS/kernel, CPU features, GPU/VRAM, driver, Docker/Compose/
toolkit, CUDA/framework, image digests, model revisions, precision, context,
concurrency, RAM/disk peaks, latency and rights fields **null**.

H02 selects the rights-reviewed exact tuple/artifact budgets; H03 provides
paired gateway/TLS/clock/network evidence; H05 owns reviewed setup/lifecycle;
H06 needs witnessed real owner hardware, combined load/reboot and unauthorized
network-source evidence. Container GPU and real inference need explicit future
download/execution consent. Optional GPU power policy and Sunshine remain
disabled administrative extensions, never prerequisites here.

## Internal reproducible build and evidence

Use pinned SDK **10.0.401**, the repository NuGet source/current test pins,
`CI=true`, an isolated CLI home, certificate generation off, telemetry opt-out
and an absolute owned temporary artifact directory. Build the new test project
directly, not the shared Windows solution. Initial manifest changes generate
only the new projects' lock files; ordinary runs use locked restore:

```powershell
dotnet restore tests/Martlet.Host.Doctor.Tests --locked-mode --artifacts-path $artifacts
dotnet build tests/Martlet.Host.Doctor.Tests --no-restore -c Release --artifacts-path $artifacts
dotnet test tests/Martlet.Host.Doctor.Tests --no-build -c Release --artifacts-path $artifacts
```

On the Ubuntu 24.04 build runner, with **preinstalled** clang/linker, gzip/tar,
PowerShell and strace:

```powershell
./deploy/ubuntu/preflight/publish.ps1 -ArtifactsPath $nativeArtifacts -Destination $newPackageDirectory
$traceVerifier = Join-Path $artifacts 'bin/Martlet.Host.Doctor.Tests/release/Martlet.Host.Doctor.Tests.dll'
./deploy/ubuntu/preflight/verify-wrapper.ps1 -Package $newPackageDirectory -ArtifactsPath $newNegativeDirectory -TraceVerifier $traceVerifier
./deploy/ubuntu/preflight/verify-linux.ps1 -Package $newPackageDirectory -ArtifactsPath $newVerificationDirectory -TraceVerifier $traceVerifier
```

Native AOT is package-only (`HostNativeAot=true`) with EventPipe disabled. A
normal CoreCLR apphost enables diagnostic IPC before Main and cannot turn it
off via runtimeconfig, so merely setting a test environment variable would
hide a shipped read-only violation. The native package removes that component
rather than requiring novices to launch a script/set environment variables.
Build-only ILCompiler/ILLink packs are SDK-selected and locked in
`native-packages.lock.json`. The native restore graph declares linux-x64 and
win-x64 explicitly so Windows lock generation and Linux builds agree; only
linux-x64 is published. No model, container image, or inference SDK is acquired.
Package-only compilation maps its independently chosen artifact root to
`/_/host-artifacts` in generated document/debug metadata. Otherwise generated
source paths change the managed DLL/PDB inputs and downstream ELF build IDs/
debug-link CRCs across build directories. The full ELF and archive remain in
the byte-equality gate; no differing payload is excluded or post-hoc stripped.

`publish.ps1` accepts a new output directory only, confirms ELF magic, records
SDK/source/ILCompiler/ILLink and clang/linker/objcopy/tar/gzip/strace/PowerShell
versions, emits per-file `SHA256SUMS`, and builds a
normalized timestamp/owner/sort-order tar.gz plus checksum. CI repeats the
publish/archive and compares bytes **on that same SDK/native toolchain**;
cross-toolchain bit-for-bit reproducibility is not claimed. Signing, release
publication, public artifact upload and installation are absent.

The dedicated `host-preflight.yml` runs Windows and Ubuntu **synthetic**
contracts, then executes the actual ELF on ephemeral GitHub Ubuntu 24.04:
help, authored fixtures, and read-only local prerequisites **with NVIDIA
query disabled**. `strace -f -q -yy -s 2048 -e trace=all` traces only the spawned
native process tree, including descriptor-only calls. Read-buffer, directory,
entropy, uname and symlink-result payloads are rendered raw (not dumped as text).
The build-only `TracePolicy` in the existing test project parses bounded records
and reconstructs PID-specific unfinished/resumed calls. It fails closed on
unknown/unparseable calls/events, unmatched resumes, unowned PIDs or extra execs.
An explicit permitted-operation policy admits local reads, constrained
read-only opens, private memory/thread/signal/descriptor bookkeeping and
original output destinations or traced coordination-pipe/eventfd writes.
Descriptor origins follow open/close/dup/fcntl, clone copies/shared tables and
close-on-exec. An unknown inherited descriptor cannot become an authorized
destination merely by being duplicated onto fd 1/2. Shared **file** mappings
are rejected even when initially read-only, preventing later write promotion.
It rejects all other
operations, including timestamp/permission/truncation changes, socket/network
calls, unexpected ioctl/control operations and
signals outside the owned tree. Expected binary/argv forms are compared exactly.
Signal destinations must still be live-owned at the call's start; an exited
numeric TID is never trusted as permanently owned.
Ownership starts at successful clone completion or an earlier positively
attributed child record, never retroactively at clone entry. Child descriptor
tables are admitted only then; ambiguous copied-table changes during an
unobserved creation interval fail closed.
Explicit terminal records and matching exit/exit-group intent are required for
the root and every spawned PID/thread; root status must match the measured
doctor exit. Clean-boundary truncation is not completion. `-q`, unlike `-qq`,
retains those terminal records. The strace 6.8 clone3 input/output structure
`{...} => {parent_tid=[...]}` is recognized only in its defined position/fields,
including split resume records; arbitrary arrows and unknown fields fail.
Noncanonical arrow spacing is rejected. Clone/group/descriptor-sharing semantics
come only from the once-validated input flags, never from output-structure text.
This is scoped syscall evidence for the recorded Ubuntu modes/tool versions,
not proof about all kernel behavior, other versions, or unexecuted NVIDIA paths.
No weakening by `DOTNET_EnableDiagnostics=0` or a native-trace skip occurs.
The native fixtures assert measured exits 0/1/2/3 against their JSON schema,
provenance, scope and expected findings. A separate wrapper-level negative
launches the verifier as a process against a missing ELF and a checksum-corrupt
owned package copy; both must exit nonzero for their expected assertion.
Only after all positive assertions does the verifier return 0, rather than
propagating a deliberately accepted doctor's nonzero exit to CI.
Only its sanitized product report goes to logs; raw syscall traces stay in
runner temp, not uploaded. This is CPU-runner package/inventory evidence,
not owner hardware or GPU/daemon/container/firewall/setup qualification.

**Windows managed build/tests are not Linux execution evidence.** Linux gate
is pending until the dedicated workflow for the reviewed head actually passes.
The PR/CI run records the exact commit/outcome; no fixture or cross-compile
can replace that evidence. No WSL, VM, Docker or system packages are installed
to manufacture local Linux evidence.

Official boundaries: [Ubuntu NVIDIA drivers](https://documentation.ubuntu.com/server/how-to/graphics/install-nvidia-drivers/),
[Docker Ubuntu installation](https://docs.docker.com/engine/install/ubuntu/),
[Compose plugin](https://docs.docker.com/compose/install/linux/),
[NVIDIA toolkit](https://docs.nvidia.com/datacenter/cloud-native/container-toolkit/latest/install-guide.html),
[Docker root-equivalence](https://docs.docker.com/engine/install/linux-postinstall/),
[CUDA compatibility](https://docs.nvidia.com/deploy/cuda-compatibility/minor-version-compatibility.html),
[NVIDIA SMI](https://docs.nvidia.com/deploy/nvidia-smi/index.html),
[.NET diagnostics configuration](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/debugging-profiling),
[Native AOT diagnostics](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/diagnostics),
[strace 6.8 clone3 formatting](https://github.com/strace/strace/blob/v6.8/src/clone.c).
These are review references, not approval to run their installation commands.
