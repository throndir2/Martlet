# Martlet host: one install flow for every role and every machine

Extra machines (a GPU PC or server on your network, or this Windows PC through
Docker Desktop) run Martlet **roles** that the desktop uses. Every host runs the
same engine, `martlet-host`, with the same commands; the installation **method**
only decides how that engine reaches the machine. Nothing about a role lives in
the tool itself.

```text
Windows desktop (Martlet) --pinned TLS, paired once--> host: Martlet gateway :9443 (private LAN address only)
                                                         | one gateway route per installed route role
                                                         v
                                                       role services on the host's 127.0.0.1 only
                                                       (network=host roles instead report machine features and are reached directly on the LAN)
```

## Methods

| Method | Host needs | How to run it | Gateway runs as |
| --- | --- | --- | --- |
| **Desktop: this PC with Docker Desktop** | Windows + Docker Desktop (WSL 2) | Martlet > **Martlet hosts** > *This PC* | containers `martlet-host-net` + `martlet-host-gateway` |
| **Desktop: another Windows PC that runs Martlet** | Martlet there (*Use as a Martlet host*) + Docker Desktop | Martlet on that PC sets itself up; pair the main PC with the code its host dashboard shows. The main PC then updates it and installs roles there [through Martlet on that PC](../../docs/CLUSTER.md#commands-between-your-computers) | same containers on that PC |
| **Desktop: another computer over SSH, Docker** | SSH server + Docker (the one prerequisite; Linux x86_64 or ARM64; for another Windows PC install Martlet there and use *This PC*) | Martlet hosts > *over SSH, using Docker* > **Add this computer** (runs in Martlet) | same containers on that host |
| **Desktop: another computer over SSH, native** | Any x86_64 or ARM64 (aarch64) Linux with systemd, SSH and sudo (Ubuntu, Debian, Fedora, openSUSE, Arch, Raspberry Pi OS, ...; no version check) | Martlet hosts > *over SSH, native* > **Add this computer** (runs in Martlet) | systemd user service `martlet-host-gateway` |
| **On the host, Docker** | any Docker host | `docker run ... martlet-host <command>` (below) | containers |
| **On the host, native** | Any x86_64 or ARM64 Linux with systemd | `./deploy/host/martlet-host <command>` (below) | systemd user service |
| **On a Mac, native** | macOS 14+, Apple silicon or Intel; Ollama and/or whisper.cpp installed | the Mac host's `macos-setup` ([Mac](#mac)) | launchd agent `io.github.throndir2.martlet.host` |

For SSH hosts the desktop does everything itself (see [Driving Linux hosts from
Windows](#driving-linux-hosts-from-windows-over-ssh)): you enter `user@computer`
and the password once, and never need to log in to that computer. The owner's
click in Martlet, over the owner's own SSH session, is the confirmation for each
change. For *This PC* Set up host does the same with Docker Desktop: Martlet
starts Docker, builds the host image, runs `setup` unattended and pairs itself,
with no console. Role changes, status, updates and pairing on this PC run the same
way in a Martlet run window (never a console window); choices such as whisper on
the GPU or the CPU are made in Martlet first. On the host itself every change is
confirmed by typing `yes`.

Commands are the same everywhere:

```text
setup               gateway, identity and start at boot (once per host)
pair                pair a desktop: shows this host's address and a short one-use code (like K7QM-4XPA) to type in
                    Martlet (Devices > Add a computer > Enter a pairing code); waits until a desktop uses it or you type
                    cancel, with no deadline; the host's roles pause meanwhile (repeat per desktop)
pair --device-id <id> --name <name>
                    pair that desktop without a console: prints one "pairing-code: martlet-pair-v1..." line,
                    waits up to five minutes for it to be redeemed, then restarts the gateway (Martlet uses this itself)
console             the gateway console: list paired desktops and revoke one
network-reset       leave this host's Martlet network (pairings stay); the next desktop that pairs adds it to its own network
exposure            how this host is reached from outside home: [--outside <name:port>]... [--clear-outside]
                    [--allow-pairing-outside-home yes|no] [--treat-all-as-outside yes|no]; no options prints it (docs/NETWORK.md)
roles               what this host can run
describe <role>     a role's terms, secrets (stored or missing, never values), choices, GPU/CPU option and route-less feature, and what an installed one runs with now, machine-readable
add <role>          install a role, e.g. add ollama, add deep-thinking, add stt, add f5, add xtts, add chatterbox, add chatterbox-original, add chatterbox-nano, add gpt-sovits, add dia, add singing, add pictures, add audio2face or add home-assistant (same flow for every role); for an installed role it changes what is answered (its model...) and keeps the rest
remove <role>       turn a role off: stop it and unpublish it (keeps its downloads and data; adding it again downloads nothing)
machine             report this machine's hardware to paired desktops (also done by setup, pair, add and remove)
update              update the gateway to this engine's Martlet version (identity, pairings, roles and data stay)
status | config     show the gateway and roles | print the generated host.json
```

`--yes` before any command (or `MARTLET_ASSUME_YES=1`) runs it without questions:
confirmations are taken as given, the gateway identity and service approval are
handled by the gateway's owner commands, and answers come from stdin as
`KEY=VALUE` lines ended by `end` (`secret.<name>=...`, `choice.<VAR>=...`), never
from arguments or the environment. Martlet desktop always uses it (for SSH hosts
and for this PC's Docker Desktop console, where the button click is the
confirmation and only secrets or choices are typed); a human on the host
normally does not.

### Updating a host

Hosts follow the desktop's Martlet version. The gateway reports the release it
was built from (`martlet_version` on `GET /martlet/v1/machine`), and the
desktop's Devices map shows *Update available* when a host is older than the
desktop. Clicking it, or **Update host**, runs `update` through the same route
as every other command:

- **Docker**: builds `martlet-host:<desktop version>` from the `v<version>` tag
  (falling back to `main`) when it is not there yet, then runs `update` from it.
  The gateway container is recreated from the new image; older unused
  `martlet-host` images are removed (the network holder keeps its own).
- **Native**: fetches and checks out the `v<version>` tag in `~/Martlet`
  (falling back to `main`), publishes the gateway beside the running one, then
  swaps it in and restarts the service.
- **Through Martlet on that computer** (another Windows PC with Martlet): the
  desktop sends a `martlet.update` command through the host's gateway; Martlet
  there updates itself from its GitHub Release when it is older, then runs
  `update` on its own Docker Desktop, and its output streams into the desktop's
  run window. Martlet on a host PC (or any PC running its own host service)
  also keeps that host service on its version by itself, in the background
  right after Martlet updates itself. See [Commands between your computers](../../docs/CLUSTER.md#commands-between-your-computers).

`update` asks nothing unless the new version changes `host.json`; that renews
the service approval (in the gateway console, or with `--yes` through
`owner-approve`), like any configuration change. For SSH hosts **Update host**
runs `--yes update` in Martlet, so the click is that approval.
After the gateway is updated, `update` also re-copies changed installed role
Compose files (and their GPU overlays when used) from the new Martlet version
and runs `docker compose up -d`, keeping data volumes. This is how pinned role
image bumps, including Home Assistant, reach existing hosts.
With *Keep my Martlet hosts on this PC's version* (Settings > App updates) the
desktop runs `update` in the background for older hosts every check interval,
without asking anything: for SSH hosts through Martlet's SSH runner (its own key,
the pinned host key and a sudo password only if you chose to remember one; no
`--yes`), for this PC in Docker Desktop. This PC's own host service doesn't need
that setting: Martlet always brings it to its own version in the background
after it updates itself. A host that needs a password, a new host
key, sudo or an approval fails that run without changing anything and keeps
*Update host*. A host busy with other changes (below) is not interrupted: the
background `update` stops at once without changing anything, its Devices card
says what the host is busy with, and Martlet tries it again every three minutes
until it is free. *Update hosts now* runs the same `update` but waits its turn:
it sets `MARTLET_LOCK_WAIT` (30 minutes) so it queues behind that change and then
updates. Martlet never races itself: a host it is already updating by another
route (an *Update host* run window, a command from another computer, keeping
this PC's own host service current) is left to that run, so its own update is
never reported as "busy", and once a host is found up to date an earlier
"waiting to update" note on its card says it is updated.

### Changes side by side

A host runs several changes at once, whoever asks for them: a console on the
host, a desktop over SSH, Martlet on that computer for paired desktops
(commands between computers), several desktops at once, another desktop's
automatic update, or this PC's own. Installing deep thinking, singing and
Audio2Face on one host from three run windows runs all three together. Only
what truly collides waits, through kernel locks (`flock`) in the config volume
(natively `~/.config/martlet/host/`):

| Command | Holds | So it waits for |
| --- | --- | --- |
| `add <role>`, `remove <role>` | `engine.lock` shared, `locks/role-<role>.lock` (or `locks/group-<group>.lock` for a role.conf `exclusive=<group>`, such as the voice engines, since adding one stops the others), and `locks/gateway.lock` only while it publishes the change (role record, machine report, `host.json`, the gateway's approval and restart) | another change to the same role or group; a setup or update; another change publishing at that moment |
| `pair`, `console`, `network-reset`, `exposure`, `machine` | `engine.lock` shared and `locks/gateway.lock` for their whole run | a setup or update; another of these; an add or remove publishing at that moment |
| `setup`, `update` | `engine.lock` exclusively | every change running now; changes asked for after them wait for them (a waiting setup or update holds `engine.gate`, which every change passes first, so a stream of installs never starves it) |
| `warm` | `engine.lock` shared, and each role's `locks/role-<role>.lock` (or group lock) only while it starts and warms that role | a setup or update; a role another change holds right now is skipped (that change starts it) |
| a native `add` that first installs Docker Engine or the NVIDIA Container Toolkit | `engine.lock` exclusively, like `update` | every change running now, because installing them restarts Docker under the other changes |

The kernel drops a lock when its command ends however it ends (finished,
killed, a dropped SSH connection), so no stale lock is ever left behind. Each
change records what it does in `engine.holders/` (its scope and whether it runs
or waits); a record stays locked while its change runs, and readers drop the
records of changes that ended. `engine.holder` keeps the latest one for engines
from before changes ran side by side, which hold `engine.lock` exclusively for
every change, so older and newer engines on one host never overlap. Secrets a
change saves are written whole (a temporary file renamed into place), since
another role's change may read the same one. Read-only commands (`roles`,
`describe`, `status`, `config`) never wait.

- A command that collides with one running **waits** for it and says so:
  `This host is busy: installing ollama (12 min so far; martlet-host-add-..., from Martlet). Waiting for it to finish before updating this host...`,
  then a line every minute, then `That finished. Continuing with ...`. A
  setup or update waiting its turn shows as `updating this host (waiting for
  the changes running now; ...)`. Martlet's run windows (and, for commands
  between computers, the computer that sent the command) show these lines. It
  waits up to `MARTLET_LOCK_WAIT` seconds in all (default 7200). An add or
  remove that already changed its role and only waits to publish that (a
  pairing or console holds the gateway) keeps waiting however long it takes:
  stopping then would leave a role running but unpublished, or stopped but
  still published, so it never claims "nothing was changed".
- A **background** run that nobody confirmed and nobody watches (no terminal
  and no `--yes`, as Martlet's automatic host updates run) does not queue
  behind a long install: it stops at once, changes nothing and exits **75**
  with one line `MARTLET-BUSY <what is running>`, unless it sets
  `MARTLET_LOCK_WAIT` (as *Update hosts now* does): then it waits like an
  attended run. A run that waited `MARTLET_LOCK_WAIT` seconds ends the same
  way. Martlet reads that line and tries again later instead of reporting a
  failure.
- `status` shows `Busy now: ...` with every change running and `Waiting to
  start: ...` with those waiting their turn. When a holder is a console session
  open 10+ minutes, the waiting lines name the command that stops it (a closed
  console window can leave its engine waiting for input). An owner operation
  that finds the gateway state busy stops only an abandoned session running a
  gateway process, never a change running alongside it (a long download holds
  no gateway state).
- `engine.log` records each wait (`busy, waiting`, `lock free after waiting`)
  and each stop (`busy, stopped without changing anything`).
- In the Docker method, `setup` replaces the network holder (`martlet-host-net`)
  when its image or address changes. Every engine session runs in the holder's
  network namespace, so `setup` first **waits for the engine sessions running
  in it** (for example a long `add`), with the same patience and the same
  `MARTLET-BUSY` stop for background runs, before it touches the holder.
  Replacing it under a running `add` used to strand that add on a dead loopback:
  its role started and listened in the new namespace while the add waited for it
  in the old one until `did not start`. An engine session that still finds
  itself in a replaced holder's namespace (one started by an older launcher)
  stops at once and says so; run the command again.

MCP's `host_engine_check` runs this engine in a disposable `ubuntu:24.04`
container and checks all of the above ([MCP](../../docs/MCP.md#local-mcp-control-windows)).

### What the host tells Martlet

`setup`, `pair`, `add`, `remove` and `machine` collect what the machine is like
into `machine.json` beside `host.json`: OS and kernel, the platform its roles run
on (`linux`) and its architecture (`x64` or `arm64`), CPU and thread count,
memory, container runtime, whether containers can use NVIDIA GPUs, the newest
CUDA version the NVIDIA driver supports, and each GPU (name, vendor, memory,
driver, and for NVIDIA its power limit, default limit and persistence mode). Natively it reads `nvidia-smi`, `/proc` and
sysfs (AMD GPUs with 2 GiB+ of VRAM); with Docker it asks the Docker host
(`docker info`) and runs `nvidia-smi` in a throwaway `--gpus all` container, so
Docker Desktop reports its WSL 2 VM's memory. The gateway serves it read-only
to paired desktops at `GET /martlet/v1/machine`. Martlet fetches it right after
pairing and on **Check connection**, keeps it in `host-hardware.json`, shows it
on the Devices map and fills the setup advisor's computers step with it. It is
host-reported information, not a measurement, and it grants no authority.
Martlet also uses it to keep impossible roles off its menus (for example F5 on a
host without an NVIDIA GPU with 6 GB+) and says why; a host without a report is
allowed with a note. See [Platforms](../../docs/PLATFORMS.md).

`martlet-host status` also says where each role runs (on graphics card
`GPU-...`, on every card, or on the processor) and, when the `deep-thinking` role
shares a card with a live role (thinking, listening, the voice), that live turns
go first there and how to give it a card of its own: pin each Ollama server to
its own GPU (`CUDA_VISIBLE_DEVICES`) by adding `deep-thinking` again with a card
no live role uses. On a host with two or more cards, add `deep-thinking-2` (and
`deep-thinking-3`, `deep-thinking-4`) for one Thinking pool model per card: each
listens on its own port (11436 to 11438), keeps its models in its own volume,
needs that many NVIDIA cards (`min_gpus`) and is a Thinking pool member of its
own on paired desktops. The gateway logs the same GPU map and warning when it starts
and serves it at `GET /martlet/v1/priority`
([GPU priority](../../src/Martlet.Gateway/README.md#gpu-priority-live-turn-first)).

The report also has a `features` array (up to 16 lowercase tokens). The host
uses it for route-less and smart-home capabilities: `host-network` means this
method can run LAN host-network roles (native Linux, or Docker on Linux Engine
but not Docker Desktop); installed route-less roles add their `feature=` value
such as `home-assistant`; probes add `ha-existing` (something else on port
8123), `mqtt-broker` (port 1883), smart-home container names (`zigbee2mqtt`,
`zwave-js`, `frigate`, `go2rtc`, `esphome`, `node-red`, `matter-server`,
`music-assistant`), USB radio hints (`zigbee-radio`, `zwave-radio`,
`serial-radio`) and `bluetooth`. Failed probes are ignored.

The gateway also keeps a copy of the shared **who does what** plan in
`cluster.json` beside `host.json`, written by the gateway when a paired desktop
syncs (Devices > Settings for all devices > *Keep Martlet the same on all my computers*). It names which
host does each job, which jobs fail over and which roles each host runs; it
holds no keys and the host never acts on it. See
[Shared who does what and failover](../../docs/CLUSTER.md). Beside it,
`shared-settings.json` holds the settings the owner's computers share
(how Martlet thinks, listens and speaks with their cloud API keys, its
character and personality; see
[One Martlet on every computer](../../docs/CLUSTER.md#one-martlet-on-every-computer))
and `memories.json` everything Martlet remembers (see
[One memory on every computer](../../docs/MEMORY.md#one-memory-on-every-computer)),
both given only to paired devices. Beside them it keeps
`commands.json` (commands paired computers sent, never their secrets) and
`agent.token` (written fresh at each start; only Martlet on the host computer
reads it, to take those commands). See
[Commands between your computers](../../docs/CLUSTER.md#commands-between-your-computers).
`network.json` holds the [Martlet network](../../docs/NETWORK.md) roster the
host accepted (member desktops' public keys and the network's hosts); the host
uses it to let member desktops pair by themselves and to revoke removed ones.
`martlet-host network-reset` removes it. `api-keys.json` holds the network's
[API keys](../../docs/API.md) for other apps and scripts (names, scopes and
SHA-256 verifiers, never a usable key), synced by paired desktops.

### Docker (any Docker host, including Windows)

Build the image once from this repository (Martlet hosts does this on the host
from the desktop's version tag, falling back to `main`), then run the launcher
with the Docker socket and the host's private LAN address:

```sh
docker build -t martlet-host -f deploy/host/Dockerfile https://github.com/throndir2/Martlet.git#main
docker run --rm -it -u 0 -v /var/run/docker.sock:/var/run/docker.sock \
  -e MARTLET_HOST_ADDRESS=192.168.1.20 martlet-host setup
docker run --rm -it -u 0 -v /var/run/docker.sock:/var/run/docker.sock martlet-host pair
docker run --rm -it -u 0 -v /var/run/docker.sock:/var/run/docker.sock martlet-host add audio2face
```

The same commands work against a remote Docker host with `docker -H ssh://user@host`
or over `ssh -t user@host`. How it fits together:

- `setup` creates **`martlet-host-net`**, a small long-lived container that
  publishes `ADDRESS:9443` on the Docker host and owns the network namespace that
  the gateway and every role container share, so role services still listen on
  `127.0.0.1` only. When it (re)starts, for example after a reboot, it restarts
  the containers that joined it, because Docker does not order restarts of
  shared-namespace containers. Docker pins each role container to the holder's
  container ID, so when `setup` replaces the holder (a new image or address)
  the roles would stay on the old, unreachable network and desktops would get
  `worker.unavailable`. `setup` and `update` recreate any installed role that
  is missing or still on an old holder (its data volumes are kept). `status`
  flags such roles.
- Every command runs in a fresh engine container as the unprivileged `martlet`
  user (uid 1000) inside that network, named `martlet-host-<command>-<UTC time>-<n>`.
  `-u 0` only lets the launcher reach the Docker socket.
- The gateway state allows one process at a time. A console window closed while
  its engine waits for input leaves that engine running (its terminal never
  ends), holding the state for good. When an owner operation or the gateway
  service finds the state busy, the engine lists every container using it and
  stops engine sessions running 10+ minutes (after `yes`, or confirmed in
  Martlet); newer ones are only named. `status` lists them too.
- Each `setup`, `add`, `remove`, `pair`, `machine` and `update` run appends its
  start, its end (exit code or the reason it stopped) and any busy-state findings
  to `logs/engine.log` in the config volume (natively `~/.config/martlet/host/`);
  `status` shows the last lines. A start without an exit line means that run was
  killed or abandoned. Docker itself does not log engines (`--log-driver none`).
- The gateway runs in **`martlet-host-gateway`** (no Docker socket) with binding
  mode `published`: it listens on the container's wildcard address while its
  certificate and clients use the host's LAN origin.
- State lives in Docker volumes `martlet-host-config` and `martlet-host-private`
  (0700, uid 1000, ext4 in Docker's data root); role files are copied into
  per-role named volumes, so nothing is bind-mounted from the host filesystem.
- GPU roles need Docker's NVIDIA support on that host (NVIDIA Container Toolkit
  on Linux, WSL 2 GPU support in Docker Desktop).
- Docker Desktop on Windows: Martlet hosts > *This PC* starts Docker Desktop when
  it is not running and, when needed, asks Windows for administrator approval once
  (UAC) to add the inbound rule `Martlet-Host-Gateway`: TCP 9443, Private/Domain
  networks, local subnet only. If the PC's network is Public it offers to mark it
  Private in the same step. The Martlet installer itself stays per-user; only
  optional prerequisites you tick there (Windows speech, WSL 2 + Docker Desktop)
  ask for approval. By hand, in an administrator PowerShell:

  ```powershell
  New-NetFirewallRule -Name Martlet-Host-Gateway -DisplayName 'Martlet host gateway (TCP 9443)' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 9443 -Profile Private,Domain -RemoteAddress LocalSubnet
  Remove-NetFirewallRule -Name Martlet-Host-Gateway   # to close it again
  ```

### Native Linux

On any x86_64 or ARM64 (aarch64) Linux with systemd (any distribution and release), as your normal user
(ARM64: see [ARM64 hosts](#arm64-hosts) for which roles run there):

```sh
git clone https://github.com/throndir2/Martlet ~/Martlet
MARTLET_HOST_ADDRESS=192.168.1.20 ~/Martlet/deploy/host/martlet-host setup
~/Martlet/deploy/host/martlet-host pair
~/Martlet/deploy/host/martlet-host add audio2face
```

`setup` installs the .NET SDK into `~/.dotnet` (no sudo), publishes the
[Linux gateway](../../src/Martlet.Gateway.Host.Linux/README.md), asks which
private LAN address and port desktops use, creates the host identity, installs a
systemd user service (`martlet-host-gateway`), optionally enables lingering so it
runs at boot without a login, and links `martlet-host` into `~/.local/bin`.
Setup first checks for curl, CA certificates and pciutils, which a bare-metal
minimal install may lack, and installs only the missing ones after a single `yes`.
Missing Docker, NVIDIA driver (with `ubuntu-drivers-common`, on Ubuntu) or NVIDIA Container
Toolkit are likewise checked first and installed only after a `yes`, with the
system package manager (`apt-get`, `dnf`/`yum`, `zypper` or `pacman`; Docker
falls back to Docker's install script where the distribution has no Compose v2
package). sudo-rs (Ubuntu 25.10's default `sudo`) works too. On a minimal system without ICU (`libicu`), the build and the gateway
run in .NET's invariant globalization mode.

### Mac

A Mac is not a native Linux host (`martlet-host` stops on macOS and says so).
Use the **Mac host** instead: Martlet's gateway as a launchd agent, relaying to
Ollama and whisper.cpp running natively on the Mac's GPU (Metal) on Apple
silicon, or on the CPU on Intel. With Ollama installed
([ollama.com](https://ollama.com/download)) and, for listening,
`brew install whisper-cpp`:

```sh
"/Applications/Martlet.app/Contents/Resources/host/Martlet.Gateway.Host.Linux" macos-setup
"/Applications/Martlet.app/Contents/Resources/host/Martlet.Gateway.Host.Linux" macos-pair
```

It offers only what is installed and never F5 (PyTorch), Audio2Face or the
other NVIDIA roles. Details, files and limits:
[macOS: the Mac host](../../docs/MACOS.md#the-mac-host-dx04). Not yet run on a
Mac.

The **Docker method** also works on Docker Desktop for Mac, CPU only (Docker
gives containers no Mac GPU): the host image builds for Apple silicon (arm64)
as well as x86_64, and it is an [ARM64 host](#arm64-hosts): Ollama and
whisper.cpp (its official arm64 image) on the CPU, no NVIDIA roles. The native
Mac host is the faster listening and thinking host (Metal). Not yet run on a
Mac.

### ARM64 hosts

Raspberry Pi 5 class boards, Ampere/Graviton servers, NVIDIA DGX Spark and
Jetson, Docker Desktop on Windows on Arm (Snapdragon X) and Docker Desktop on
Apple silicon are ARM64 (aarch64) hosts. Every method works on them: the host
image builds for arm64, native setup installs the arm64 .NET SDK (or Martlet
sends it, [without internet](#computers-without-internet)), and the machine
report says `architecture: arm64`, which Martlet uses to offer only what runs
there:

| Role | On ARM64 |
| --- | --- |
| `ollama`, `deep-thinking` | Yes: Ollama's official image is multi-architecture. CPU on a Raspberry Pi (1-3B models; 8 GB+ board); NVIDIA GPU on DGX Spark/Jetson-class machines with the NVIDIA Container Toolkit |
| `stt` (whisper) | Yes, on the CPU: `martlet-host` adds `roles/stt/compose.arm64.yaml` (role.conf `arm64=`), whisper.cpp's official arm64 image of the same pinned commit; the GPU option is not offered (no arm64 CUDA build). `base` or `small` on a Raspberry Pi 5 |
| `stt` (Parakeet) | Yes, on the CPU: its hash-pinned aarch64 wheels |
| `home-assistant` | Yes (Docker Engine on Linux, as on x86_64) |
| `f5`, `chatterbox`, `chatterbox-original`, `chatterbox-nano`, `xtts`, `gpt-sovits`, `dia`, `singing`, `pictures`, `audio2face` | No: their images pin x86_64 CUDA packages (`requires=x86_64`). `add` and `describe` say so, and Martlet's Devices map and menus show them disabled with that reason |

Docker Desktop on Windows on Arm and on Macs gives containers no GPU, so those
hosts are CPU-only. Not yet run on a real ARM64 host (NOT RUN: no ARM64
hardware here); the engine's ARM64 decisions are checked by
[`host_engine_check`](../../docs/MCP.md) and the arm64 whisper.cpp image was run
under emulation.

### Computers without internet

Hosts normally download what they need themselves. A computer that is
deliberately kept off the internet (or whose DNS or route out is broken) and
that only this PC reaches over the LAN still gets set up: before **setup** or
**update** over SSH, Martlet checks whether the host opens a connection to
GitHub within 8 seconds (`HostCheckout.InternetProbe`). When it can't, Martlet
falls back to sending the files from this PC (native method):

1. This PC downloads Martlet's source for its version (the `v<version>` tag,
   else `main`), the .NET SDK the engine pins (`DOTNET_SDK`, checked against
   the SHA-512 Microsoft publishes) and the NuGet packages in the lock files of
   the gateway's projects. They are kept in `host-supply` in Martlet's data
   directory for next time (about 270 MB the first time).
2. It sends only what the host lacks over the same SSH connection as one tar
   stream into `~/.cache/martlet/supply`, then checks each file arrived intact
   (SHA-512), unpacks the source into `~/Martlet` (a previous `~/Martlet` moves
   to `~/.cache/martlet/source.previous`) and removes what is no longer needed,
   such as the SDK archive once that SDK is installed.
3. The engine runs with `MARTLET_SUPPLY`: it installs that SDK into `~/.dotnet`
   and restores the gateway's packages only from the files sent
   (`-p:RestoreConfigFile=...`, `-p:NuGetAudit=false`; NuGet still checks them
   against the lock files), touching neither git nor the network.

Pairing, status and roles run as usual. When the host is online again, the next
setup or update replaces the copy with a git checkout. Martlet can't send Docker
images or models yet: on a computer without internet access, **Add a role** and
the Docker method stop with a plain explanation instead of failing halfway.
Without internet access, the online command never runs an engine that isn't
there either: if git can't be installed or Martlet can't be cloned it stops with
`Stopped: ...`. `host_supply_check` in [Martlet's MCP server](../../docs/MCP.md)
checks the whole route against a container on an internal network.

## Pairing

**Pair once for all your computers.** Every desktop and host belongs to the
owner's [Martlet network](../../docs/NETWORK.md). The first desktop you pair a
host with adds that host to its network, and every other computer in the
network then pairs with it by itself within a minute (no code, no console, no
SSH login of its own). A new desktop joins by pairing with any one host of the
network and being allowed on one of your other computers (check number on both
screens). So for a Linux machine you set up over SSH from one PC, the whole
network can use it right away.

**SSH hosts** pair by themselves: **Set up over SSH** in Devices > Add a computer
(Martlet runs the host in Docker when that account can use Docker, otherwise
natively on any Linux with systemd) runs `martlet-host --yes pair --device-id <this PC>
--name <this PC>` there. The gateway starts its listener, prints the one-use
code on one line and waits; Martlet reads the code from the output (it is never
shown or logged; Docker runs the engine with `--log-driver none`), redeems it
and stores the device secret in Windows Credential Manager, and the host
restarts its gateway. If redeeming fails, Martlet sends `cancel` so the host
stops waiting at once.

**This PC:** **Set up this PC** runs the same unattended pairing on this PC's
Docker Desktop. To pair *another* desktop with this PC's host, the host
dashboard's **Show a pairing code** shows this PC's address and a short code in
large type (never logged) with a **Copy code** button; type both on the other
desktop. The code doesn't expire: it works until the other desktop uses it or
you cancel the window, and this PC's host roles pause until then.

**By hand (any host):** on the host run `martlet-host pair`. It shows:

```text
Pair a Martlet desktop with gpu-pc-host
  In Martlet on the desktop: Devices > Add a computer > Enter a pairing code, then type
    Address:  192.168.1.20
    Code:     K7QM-4XPA
  The code works once and doesn't expire: it stays valid until a desktop uses it or you type cancel
  (or press Ctrl+C). Until then this host's jobs are paused.
```

Withdraw a code you no longer need with `cancel` or Ctrl+C rather than by
closing the window. Over SSH or in a local terminal, closing it also withdraws
the code, but a closed Docker console (`docker run -it`) keeps waiting with the
host's roles paused, like an abandoned `martlet-host console`: the next
`martlet-host` command names it as busy and says to stop it with
`docker stop <name>`.

A host that Martlet on another of your computers runs or reaches over SSH usually needs none of this:
Martlet lists that computer under *Martlet on your network* and pairs after you press Allow there. Otherwise, in
Martlet choose **Devices > Add a computer > Enter a pairing code**, type the
address and code, and press **Pair with host**. The host finishes and restarts
its gateway by itself; there is no console and no device ID to copy. Codes use
the digits 2-9 and letters other than `I` and `O`, ignore case, spaces and
dashes, and close after five wrong tries. The desktop checks that the computer
at that address really shows this code before it pins its TLS key (see
[short typed codes](../../src/Martlet.Gateway/README.md#short-typed-codes)).
An older host whose `pair` opens the gateway console still shows a long
`martlet-pair-v1....` code; paste that into the same Code box (no address
needed), or update the host first. `martlet-host console` lists paired desktops
and revokes one. **Check host** shows which roles the host offers and the
hardware it reported.

## Driving Linux hosts from Windows over SSH

The goal: put Docker on a Linux computer, give Martlet its SSH login, and do the
rest from Windows. **Add this computer** in Martlet hosts:

1. Connects with Martlet's own SSH key. The first time it asks for the account
   password once (masked, never stored), appends Martlet's public key to
   `~/.ssh/authorized_keys`, and from then on signs in with the key only.
2. Shows the computer's SSH host key fingerprint on first contact and pins it;
   any later change is refused (**Reset SSH trust** forgets it after a reinstall).
3. Checks the computer: Docker present (the one prerequisite; if it is missing
   Martlet says so and stops), whether this account can use Docker directly,
   sudo, OS, architecture and its LAN address.
4. Runs `setup` and `pair`, then reads the machine report, streaming every line
   into a run window with **Cancel**. The host then appears on the Devices map.
   A computer that can't reach the internet gets what setup needs from this PC
   ([Computers without internet](#computers-without-internet)).

Afterwards the map's per-host actions (add a role, change its settings, remove it, show status) run the
same way. Adding a role first runs `describe <role>` there and shows its
requirements, terms, secrets and choices in Martlet; the **Install** click is the
confirmation, and the secrets (for example the NGC API key of the Audio2Face NIM engine) go to the host over
stdin and are stored there in the host's private config (0600). Changing a role's settings (*Change ... settings* on its
row, *Change model* in Companion > Deep thinking and on Thinking's and Listening's computers) shows the same dialog, titled
*Change*, with what the role runs with now selected; **Apply** sends every choice, so only what you changed changes.

Trust model:

- Martlet's key is `ssh\martlet_ecdsa` (ECDSA P-256) in Martlet's data directory
  (`%LOCALAPPDATA%\Martlet`), created with an ACL granting only your Windows user.
  Pinned host keys are in `ssh\known_hosts.json` there and with each paired host
  in `hosts.json`.
- Runs use no terminal (so nothing on the host can prompt), `sh -c` as that
  account, and `martlet-host --yes`. Your authenticated SSH session, started by
  your click in Martlet, is the owner's local channel: it replaces typing `yes`
  in a host console, including the gateway's identity creation, service approval
  and pairing (`owner-init`, `owner-approve`, `owner-pair` in the
  [Linux gateway](../../src/Martlet.Gateway.Host.Linux/README.md)).
- When a step needs sudo (Docker for an account outside the `docker` group,
  native package installs, lingering), Martlet asks for the sudo password in a
  masked dialog, checks it with `sudo -S`, and passes it on the SSH channel's
  stdin to a private one-run askpass helper (a 0600 file in a 0700 temporary
  directory, removed when the run ends), so plain `sudo` works non-interactively.
  It never appears in arguments, the environment or output. You can let Martlet
  remember it per computer in Windows Credential Manager.
- The transport is reusable: `HostShell.RunAsync(target, command, output, token)`
  in `src/Martlet.Desktop/HostShell.cs` (stdin input, optional sudo, pinned host
  key) and `HostRunWindow` to show a run with Cancel.

## The uniform role flow

`martlet-host add <role>` always runs these steps, driven only by
`roles/<role>/role.conf` and `roles/<role>/compose.yaml`:

| Step | `role.conf` key | What happens |
| --- | --- | --- |
| Requirements | `requires` | Shared checks/installs: `gpu` (NVIDIA driver), `docker` (Engine + Compose), `docker-engine` (Linux Docker Engine, not Docker Desktop, for LAN host-network roles), `nvidia-toolkit` (native method) |
| GPU or CPU | `gpu=optional\|<overlay>.yaml`, `gpu=required\|<overlay>.yaml` | Detects NVIDIA GPU memory usable by containers. `optional` asks `gpu` or `cpu` (default: GPU when present, or where an installed role runs now while that is still possible; Martlet sends `choice.accelerator` or lets the host decide); `required` always uses the GPU. `gpu` adds the role's Compose overlay (and the NVIDIA requirements); `cpu` runs without it. `describe` reports an installed role's as `role.accelerator_current`. Inside a `[VAR=value]` section only that variant has it (the `stt` role's whisper engine; Parakeet runs on the CPU), and `describe` adds `role.gpu_when` |
| Graphics card | (any role with a `gpu` entry) | Only on a host with two or more NVIDIA cards: `choice.gpu` is a card's UUID (`describe` lists them as `role.gpu=<uuid>\|<name>\|<MiB>\|<other roles on it>`, plus `role.gpu_current` once installed) or `all`. A chosen card is written to `.env` as `MARTLET_GPU` and the overlay sets `CUDA_VISIBLE_DEVICES` to its UUID in the role's services, so each role (thinking, deep thinking, listening, the voice, singing) can have a card of its own. Without a choice it keeps the card it runs on (or every card, when it runs on all of them), else takes a card no other role uses, else (for a Thinking model: `ollama`, `deep-thinking`, `deep-thinking-2`...) a card no other Thinking model uses, preferring the most memory free now. `choice_by_vram` then counts that card's memory (every card's for `all`). One-card hosts are unchanged. The gateway gives live turns first claim on each card ([GPU priority](../../src/Martlet.Gateway/README.md#gpu-priority-live-turn-first)): pin `deep-thinking` to a card no live role uses (thinking, listening, the voice), so its thinks never stop for a reply. For each further card, add `deep-thinking-2`, `deep-thinking-3` or `deep-thinking-4`: one more Thinking pool model, on its own card and its own route, that paired desktops use as a Thinking pool member of its own |
| Card count | `min_gpus=N` | Stops before anything is installed unless the host has at least N NVIDIA cards usable by containers. The `deep-thinking-N` roles use it, so a card-N Thinking pool model never shares a card with another Thinking model on a host with fewer cards |
| Choices | `choice=VAR\|label\|options\|default`, `choice_by_vram=VAR\|<MiB>@<value> ...`, `profile_from=VAR` | Asked each time (or chosen in Martlet, where *Automatic* keeps the suggestion), written to the role's `.env`; `choice_by_vram` suggests the default by GPU memory (ascending thresholds, `0` = CPU); `profile_from` makes that choice the role's Compose profile (`COMPOSE_PROFILES`), so one role can offer variants such as the stt or Audio2Face engine (`profile_legacy` names the variant installs from before the role had variants run). The choices that pick a variant (outside any section and not suggested by GPU memory) are asked first, before GPU or CPU; then that variant's own choices. For an installed role the default is what it runs with now (`describe` lists it as `role.choice_current=VAR\|value`; a variant's own choice only while that variant runs), so a re-add changes only the choices answered. Changing the variant on a re-add prepares the chosen one (its image built, its prepare steps run) while the previous one keeps running, and stops the previous one only then (its volumes are kept). Every other variant is stopped before the chosen one starts, so a switch that stopped part-way and is run again still stops the previous one; each service of a role with variants belongs to one variant (profile) |
| Variants | `[VAR=value]` ... `[end]` | Entries between these lines (terms, secrets, registry, assets, loopback rewrites, the GPU option, choices and their suggestions, prepare steps) apply only when choice `VAR` is `value`; `describe` lists them as `role.terms_when`, `role.secret_when`, `role.choice_when`, `role.suggested_when` and `role.gpu_when`, so Martlet shows them only for that choice. Two variants may each have their own choice of the same `VAR`, such as the stt role's `STT_MODEL` (whisper's or Parakeet's models) |
| Terms | `terms` | Shown (every one that applies to the choices made); continue only on `yes` (or shown in Martlet, whose Install click confirms) |
| Secrets | `secret=name\|prompt` | Asked once (or sent by Martlet on stdin), stored in the host config `secrets/<name>` (0600), passed to Compose as environment secret `<NAME>` |
| Registry | `registry=host\|user\|secret` | `docker login` with the stored secret |
| Assets | `asset=url\|path` | Pinned HTTPS downloads (`{VAR}` uses a choice), copied into the role's `martlet-<role>-configs` volume |
| Loopback | `rewrite=path\|sed`, `expect=path\|text` | Rewrite configs to 127.0.0.1 and verify it |
| Prepare | `prepare=<service>` | Before the service is (re)created, runs that service once with `MARTLET_PREPARE=1` (`docker compose run --rm --no-deps`) so it fetches what the chosen options need and exits, while the running one keeps serving. `stt` uses it to download a newly chosen whisper or Parakeet model (and to build the Parakeet image), so changing the model, the engine, or GPU and CPU only restarts the server instead of leaving the host deaf for the download |
| One per host | `exclusive=<group>` | Roles in the same group replace each other: the voice engines (`chatterbox`, `chatterbox-original`, `chatterbox-nano`, `f5`, `xtts`, `gpt-sovits`, `dia`) declare `exclusive=voice` because each keeps its model in the graphics card's memory. `describe` lists the installed ones adding this role stops as `role.stops` (Martlet's Install dialog names them). `add` asks once (or Martlet already did), builds the new role's image while the old engine still works (`docker compose up --no-start`), then stops the others (`docker compose down`, keeping their volumes and images), removes their records and republishes the gateway without them before the new role starts, so its model never competes with theirs. Re-adding one later reuses its downloads |
| Service | `compose.yaml`, `network=host` | `docker compose up -d` with `network_mode: ${MARTLET_ROLE_NETWORK}` (host natively, the gateway's namespace in Docker), unless the role declares `network=host` and its Compose file intentionally uses the host network on both methods; a role can build its image from Martlet's sources under `${MARTLET_SOURCE}` (the checkout natively, `/opt/martlet/source` in the host image, so a role that builds from `workers/<role>` needs its `COPY workers/<role>` line in `deploy/host/Dockerfile`; bump the role image's tag in `compose.yaml` when those sources change, so `martlet-host update` rebuilds it). The Python role images first install a hash-pinned pip 26.2.1 with a 60-second timeout, so a package download that stalls or drops resumes (up to 10 times) instead of failing the build; if building or starting still fails, `add` stops and says to run it again, which reuses the build steps already finished |
| Readiness | `port`, `ready_timeout_minutes` | Wait until the service accepts connections (127.0.0.1:`port`, or the host LAN address for Docker `network=host` roles) |
| Post-start | `post_start=<service>\|<command>` | Runs each command inside that service in order (`docker compose exec`; `{VAR}` uses a choice; plain words only), for example downloading a model; its progress streams to Martlet's run window. When a prepare or post-start step fails and the role's containers can't look up internet names (Docker Desktop's container DNS, 192.168.65.7, can stop answering while Docker itself still pulls images), `add` waits a few minutes for lookups to recover and runs the step again (at most twice); if they don't recover it stops and says to restart Docker and check VPN, firewall or antivirus DNS blocking |
| Warm | `warm=<service>\|<command>` | Runs after the post-start steps, like them, and loads the role's model into memory (`ollama run {OLLAMA_MODEL} Say ready`, `martlet-f5 warm`, ...). `martlet-host warm` runs these again without installing or downloading anything: it starts the gateway and every installed role (`docker compose up -d --no-build`), waits until each listens and warms it, so the first request after Docker or the computer started doesn't wait for the model. Martlet runs it on this PC's host service when this PC becomes a host PC or starts as one, after starting Docker Desktop |
| Publish | `gateway_kind`, `model_from`, `slots_from`, `feature` | Route roles add `{kind, endpoint, model}` to the gateway's `host.json` `roles` list (plus `slots`, from the `slots_from` choice, when a role runs several requests at once: the deep-thinking role's thinks at once); when `host.json` changed, renew the service approval (gateway console, or `owner-approve` with `--yes`) and restart. Beside it, `gpus.json` (0600, not part of the approval) says where each route role runs, for the gateway's [GPU priority](../../src/Martlet.Gateway/README.md#gpu-priority-live-turn-first): `{"schemaVersion":1,"roles":{"<kind>":["<card UUID>" or "cpu"]}}` for a role pinned to one card or added to run on the processor; a role on every card is left out, which the gateway counts as the whole host. A changed `gpus.json` restarts the gateway without a new approval, so automatic updates keep running. Route-less roles declare `feature=<token>` instead, write an installed role record with `feature`, `port` and `network=host`, collect `machine.json` and restart the gateway without changing `host.json`. Either way the gateway restarts only when something it opens changed (below) |
| Retire | `retire=<service>\|<command>` | Once the gateway relays a newly chosen model, runs the command inside that service for the model it replaced (`{PREVIOUS}`); `ollama` uses `ollama stop {PREVIOUS}` so the old model leaves the graphics card's memory (it stays downloaded). A failure is only noted |

### Reusing what is already there

Adding a role that is already installed is a reconfiguration, not a reinstall:

- Only what is answered changes: an unanswered choice, and whether it runs on the
  GPU or the CPU, stay as the role runs now (a fresh install takes the defaults and
  suggestions). Martlet's *Change ... settings* shows them and sends them all.
- Images, downloads and data volumes are kept (also by `remove` and by an
  `exclusive` replacement), so adding a role again, or switching back to one,
  downloads nothing new. `remove` turns a role off; it deletes nothing.
  Compose recreates a container only when its configuration changed; an
  unchanged one keeps running.
- The machine report lists the kept downloads (`downloads`: each role and
  model that ran on this host, 16 at most for each role, from
  `downloads/<role>` in the host config). A role whose data volumes were
  deleted (`docker volume rm`) is left out. Martlet's recommended setup uses the
  list: turning a role back on, or switching back to a model, says *already
  downloaded* and counts no download.
- The gateway (the single entry point every role on the host goes through)
  restarts only when something it opens changed: `host.json`, the machine report
  (apart from when it was collected), `gpus.json` (where each role runs) or the
  gateway itself (the engine image in Docker, the published assemblies natively).
  After each healthy start `martlet-host` records a stamp of them in
  `gateway.served` in the host config; when it still matches, the approval matches
  and the gateway answers its health check, `add`, `remove` and `setup` leave it
  running, so the other roles' traffic is never interrupted by a change that
  doesn't concern it (adding a role again with the same model and card, running
  setup again). Moving a role to another card or between the GPU and the CPU
  changes `gpus.json`, so the gateway restarts with it, without a new approval.
  `machine` always collects a fresh report and restarts it.
- Switching a role's model keeps the old one answering until the new one is
  ready: Ollama pulls and loads the new model while the gateway still relays the
  old one, then the gateway switches and the old model is unloaded (`retire`);
  whisper downloads the new model in a one-off container (`prepare`) before its
  server is replaced. Only the voice engines (`exclusive=voice`) stop the old one
  before the new one starts, because both models would not fit in the graphics
  card's memory; the new engine's image is built first, so that gap is only its
  start and warm-up. Paired desktops then follow the new model on their next
  check: a job or Deep thinking that stays on this host keeps using it (a route
  names its model, and the gateway answers only for the one it serves).

## Adding a new role

1. Add `roles/<name>/role.conf` and `roles/<name>/compose.yaml`. Gateway-routed
   services listen on 127.0.0.1 and use `network_mode: ${MARTLET_ROLE_NETWORK}`;
   files come from the external `${MARTLET_ROLE_CONFIGS_VOLUME}` volume; an
   optional GPU overlay goes next to them. Route-less LAN roles use
   `network=host`, `feature=<token>` and hard-code host networking in Compose.
2. Add the gateway relay worker for its `gateway_kind` (see
   `Martlet.Gateway.Ollama`, `Martlet.Gateway.Stt`, `Martlet.Gateway.F5`, `Martlet.Gateway.Xtts`, `Martlet.Gateway.GptSovits`, `Martlet.Gateway.Dia`, `Martlet.Gateway.Singing`, `Martlet.Gateway.Pictures`, `Martlet.Gateway.Ocr` or `Martlet.Gateway.Audio2Face`) and register the kind in
   `Martlet.Gateway.Host.Linux` (`HostConfiguration.RoleKinds`, `NativeHostPlatform.RoleWorker`). Skip this for route-less `feature=` roles.
3. Add one entry to `HostRoles` in `src/Martlet.Desktop/HostControl.cs` (kind, name, needs and the
   gateway route ID it advertises). The Devices map, the host dashboard and Martlet hosts then offer
   to install, remove and check it on any paired host; teach the desktop to use the route for its job
   (a conversation job is one `HostJob` entry in `src/Martlet.Desktop/MainWindow.HostJobs.cs`).
   Route-less features instead teach the owning desktop page to read the machine
   feature and direct service port.

No new install script and no new method work: every method runs the same engine.
Next in line, with its own relay worker and catalog entry: screen understanding (perception).

## Switching which computer does what

A desktop can pair with any number of hosts. On its **Devices** page, *Who does
what* shows which computer handles each job:

- **Thinking** moves between the cloud choice from Setup and any paired host that
  runs `ollama`. Handing it to a host saves a gateway Ollama route (pinned origin,
  this PC's pairing, the host's advertised route and your recorded selection);
  the previous cloud route is kept in `thinking-cloud.json` so *Cloud* restores it
  without re-entering a key. A host without Ollama is offered the install; Martlet
  keeps thinking where it does until the model is ready and then switches over.
  Conversation text then goes only to that host, over its pinned TLS gateway.
- **Deep thinking** is this PC's own choice (Companion > Deep thinking > Another of
  your computers), not a shared job: a host that runs `deep-thinking` thinks things
  over there in the background with its own model, beside the host's `ollama` when it
  has one, so it can do both Thinking and Deep thinking. A host without the role is
  offered the install (which asks for its model), and Martlet switches Deep thinking to
  it once it is ready; *Change model* there switches the model later. A
  host that only runs `ollama` and doesn't do Thinking can think with that model too.
- **Listening** (speech-to-text) moves the same way between the Setup choice (OpenAI,
  Windows speech or whisper.cpp on this PC) and any paired host that runs `stt` (with
  whisper or Parakeet, chosen when the role is added: Martlet suggests whisper on a host
  with an NVIDIA graphics card and Parakeet on one without, when Parakeet understands
  Windows' display language), saved
  as a gateway speech-to-text route; the previous route is kept in
  `listening-previous.json`. Push-to-talk and hands-free utterances then go only to
  that host over its pinned TLS gateway and are transcribed there in memory.
- **Speaking** moves the same way between the Setup voice (OpenAI or Windows speech)
  and any paired host that runs a voice engine (`chatterbox`, `chatterbox-original`, `chatterbox-nano`, `f5`, `xtts`, `gpt-sovits` or `dia`,
  each set up and used with one button on Companion > Voice > Voice engine), saved as a gateway
  F5 route; the previous route is kept in `speaking-previous.json`. A host runs one voice engine
  at a time: choosing another engine there installs it with `exclusive=voice` (above), or, when
  it is already installed, switches Speaking to it and then removes the engines it no longer uses
  (`martlet-host remove`, downloads kept), so the old model frees the graphics card's memory.
  When Speaking leaves a host's engine for another computer or a cloud voice,
  Martlet removes that engine there the same way once Speaking has moved (unless failover keeps
  the same engine on it as a backup). The confirmation names them; Speaking on that host pauses
  while a new engine installs. The Voice engine card's *Stop* button removes leftovers on a host
  set up before this rule that still runs several engines. Martlet uses the voice chosen on all your computers
  (or the first in your voice list): every voice, with its recording, is shared with your
  paired hosts (`speaking-voices.json` and `speaking-voice-<sha256>.wav` beside `host.json`;
  each desktop keeps its copy in `f5-voices`, Martlet.F5's reference preset store, see
  [shared speaking voices](../../docs/CLUSTER.md#the-shared-speaking-voices)). Adding a voice takes
  a mono 16-bit WAV of 1-30 s with its exact transcript and your confirmation
  that the voice is yours or used with its speaker's permission (`voice-rights-v1`).
  Each reply segment's text then goes only to that host, naming the recording it already
  holds by SHA-256 (the recording itself only when the host lacks it); its 24 kHz mono
  PCM16 plays like any other voice. The original recording may be moved or deleted after
  adding it.
- Hosts also keep a copy of the character models you add (`character-models.json` and
  `character-model-chunk-<sha256>.bin` beside `host.json`), only so each of your Martlet
  desktops can copy them; a host shows no character. See
  [shared character models](../../docs/CLUSTER.md#the-shared-character-models).
- Hosts keep a copy of everything Martlet makes, such as songs and pictures (`creations.json` and
  `creation-chunk-<sha256>.bin` beside `host.json`), so each of your Martlet desktops can copy
  them, including one that was off when it was made; a host performs nothing. See
  [Creations](../../docs/CREATIONS.md).
- Handing a job to a host detaches the replaced cloud key (it is listed for removal
  in Setup, never silently deleted); handing the job back reattaches it. Jobs on the
  same host share that host's one pairing.
- **Lip-sync** moves between *This PC*, any paired host and *nobody* (voice
  loudness) instantly, without restarting the character.

From each host's details the desktop runs `add <role>`, `remove <role>` and
`status` on that host through the route it was paired with (SSH with Docker, SSH
native, this PC's Docker Desktop, or Martlet on that computer through its paired
connection), so moving a job from one GPU PC to another
is: hand it to the new host (Martlet offers to install the role there), then
remove the role from the old one. Every route runs in Martlet (the click
confirms) with its output in a run window; none asks you to type a command on the host.

## Preparing a computer

The goal is that you never sign in to your Linux machines: Martlet prepares
them from Windows. On the **Devices** map, a paired host's details offer
**Prepare this computer** (the *Add a computer* card offers **Prepare a Linux
computer over SSH** for one that is not paired yet). The window reads the
computer's state, shows a checklist, and runs only what you tick; ticking an
item and pressing **Run selected** (then confirming) is your consent for that
change. **Tick what is missing** ticks everything not yet in place except the
virtual display and GPU power, which are preferences.

It uses [`martlet-prepare`](martlet-prepare), a standalone, idempotent bash
script that runs directly on the computer as your SSH user with sudo (not
inside the `martlet-host` container), so it works for Docker-method and native
hosts alike, and before Martlet is installed there at all. The desktop sends the
script over the SSH connection, so the computer needs no Martlet checkout.
Ubuntu (any release) and Ubuntu-based distributions on x86_64 or ARM64 (aarch64;
NVIDIA's `sbsa` CUDA repository there) are supported;
other systems are refused with a clear reason (they can still be set up natively).

| Item | What it does |
| --- | --- |
| `updates` | `apt-get update` and `upgrade --with-new-pkgs`, noninteractive, keeping existing config files |
| `docker` | Docker Engine and Compose (`docker.io`, `docker-compose-v2`; keeps an existing Docker CE), started at boot, you in the `docker` group |
| `nvidia-driver` | Installs or updates the driver `ubuntu-drivers` recommends; flags that a restart is needed |
| `nvidia-toolkit` | NVIDIA Container Toolkit from NVIDIA's repository, `nvidia-ctk runtime configure --runtime=docker`, Docker restart, then checks `docker run --rm --gpus all ubuntu:24.04 nvidia-smi` |
| `headless` | SSH server on at boot; sleep, suspend, hibernate and hybrid sleep masked; optionally start in text mode (`multi-user.target`, frees GPU memory) or back to the desktop (`graphical.target`) |
| `virtual-display` | A virtual monitor for a GPU with no screen: NVIDIA gets an Xorg config with `AllowEmptyInitialConfiguration`, a virtual resolution and `ConnectedMonitor`; other GPUs use `xserver-xorg-video-dummy`. Switches GDM to Xorg. `--virtual-display-off` removes it |
| `gpu-power` | Persistence mode on, and per-GPU power limits in watts checked against each GPU's min/max, saved in `/etc/martlet/gpu-power.conf` and reapplied at every boot by `martlet-gpu-power.service`. `--power-reset` returns every GPU to its default |
| `tools` | Any of: CUDA toolkit (NVIDIA's repository, matched to the driver, pinned so it never replaces the Ubuntu driver), Python 3 with pip and venv, Node.js LTS (NodeSource), git, build-essential, htop, nvtop, curl, tmux |
| `wol` | Wake-on-LAN (magic packet) on the wired card with the default route, kept across restarts by `martlet-wol.service` (and NetworkManager when it manages the card); reports the MAC |
| `reboot`, `shutdown` | Restart or power off a few seconds later |

Output is plain progress (`==> item: ...`), one `MARTLET-ITEM <item> <ok|changed|skipped|failed> <message>`
line per item, and a final `MARTLET-RESULT {json}` line. `status --json` prints
one JSON line: OS and kernel, sudo, Docker and Compose versions and docker
group, NVIDIA driver and recommended driver, CUDA, container toolkit, each
GPU's persistence mode and power limit (current, default, min, max), sleep
masking, boot target, virtual display, tool versions, Wake-on-LAN card and MAC,
and what still needs a restart.

When a restart is needed the window says why and offers **Restart it**: Martlet
restarts the computer, waits until SSH stops and then answers again, and reads
it again. **Shut it down** and **Wake it up** are there too; Wake sends a
Wake-on-LAN magic packet from Windows to the MAC the computer reported, which
Martlet saves with the host in `hosts.json` (the map then offers *Wake it up*
as well). Wake-on-LAN also has to be allowed in the computer's BIOS/UEFI.

How it connects: through Martlet's in-app SSH runner, like every other SSH
action (see [Driving Linux hosts from Windows](#driving-linux-hosts-from-windows-over-ssh)).
The script goes to `bash -s` on stdin; the first connection asks for the
account password once to install Martlet's key and shows the host key to pin,
and runs that need sudo ask for the sudo password in Martlet (masked, optionally
remembered). No console or SSH window opens. By hand, on or against the computer:

```sh
ssh me@gpu-pc 'bash -s -- status' < deploy/host/martlet-prepare
scp deploy/host/martlet-prepare me@gpu-pc: && ssh -t me@gpu-pc bash martlet-prepare docker nvidia-driver headless --boot text
ssh -t me@gpu-pc bash martlet-prepare gpu-power --power 0=250 tools --tools python,node,nvtop
```

## Roles

| Role | Needs | Desktop use |
| --- | --- | --- |
| `ollama` | Docker; an NVIDIA GPU (NVIDIA Container Toolkit) makes replies fast, otherwise the CPU; official `ollama/ollama:0.34.4`, model `gemma4:e2b`, `gemma4:e4b`, `qwen3-vl:8b`, `gemma4:12b` or `gemma4:26b` (suggested by GPU memory; these also see images, so the desktop can watch the screen with them, and call tools), the vision-only `gemma3:4b`, `qwen2.5vl:7b`, `gemma3:12b` or `gemma3:27b`, or the text-only `llama3.2:3b`, `qwen2.5:7b`, `llama3.1:8b` or `qwen2.5:14b`, kept in volume `martlet-ollama-models` and kept loaded | Thinking: the conversation model when the desktop hands thinking to this host (Devices > Who does what). Relay `Martlet.Gateway.Ollama` streams loopback `/api/chat` (persona, recent history, message and, for a screen glance, one image) with an 8,192-token context |
| `deep-thinking` | Docker; an NVIDIA GPU (NVIDIA Container Toolkit) makes thinks fast, otherwise the CPU; a second official `ollama/ollama:0.34.4` server of its own on 127.0.0.1:11435, model: the same list, default and suggestions by GPU memory as `ollama`'s conversation model (`gemma4:e2b` on the CPU, `gemma4:e4b` from 7 GB, `gemma4:12b` from 11 GB, `gemma4:26b` from 22 GB; a model without Thinking steps is asked again without them), kept in volume `martlet-deep-thinking-models` and kept loaded. *Thinks at once* (`OLLAMA_NUM_PARALLEL`, 1 to 4, default 1; `slots_from` publishes it as the role record's `slots`): one card runs that many thinks at the same time, each slot holding one think's context from when the model loads (Martlet recommends what fits beside the host's other roles). Not exclusive: it runs beside `ollama` and shares the graphics card | [Deep thinking](../../docs/CONVERSATION.md#thinking-longer-and-background-work): Martlet thinks things over in the background on this host (Companion > Deep thinking > Another of your computers) while the conversation carries on, even when this host also does Thinking: each model has its own server, so a think never waits for a reply. Relay `OllamaRelayWorker.DeepThinking` serves route `martlet.gateway.deep-thinking-chat.v1` with the `ollama` route's contract and bounds (Thinking steps on, up to fifteen minutes a think), admitting as many thinks at once as its slots and advertising them as the route's `maximum_concurrency` |
| `stt` | Docker; engine choice `STT_ENGINE`. **`whisper`** (default): an NVIDIA GPU (NVIDIA Container Toolkit, driver 580+ for CUDA 13) makes it fast, otherwise the CPU; official `ghcr.io/ggml-org/whisper.cpp` release 1.9.4 (CPU or CUDA build), model `base`, `small`, `medium` or `large-v3-turbo` (suggested by GPU memory: `small` on the CPU, `large-v3-turbo` from 4 GB), downloaded on first start from Hugging Face at a pinned revision with its SHA-256 checked into volume `martlet-stt-models`. **`parakeet`**: NVIDIA Parakeet on the CPU with sherpa-onnx 1.13.8, image built on the host from [`workers/parakeet`](../../workers/parakeet/README.md) (`python:3.12.10`, hash-locked packages), model `parakeet-tdt-110m-en` (English, the fastest; default), `parakeet-tdt-0.6b-v2-int8` (English, the most accurate) or `parakeet-tdt-0.6b-v3-int8` (25 European languages): the same models as Parakeet on the desktop, downloaded from Hugging Face at pinned revisions (each file's size and SHA-256 checked) into volume `martlet-stt-parakeet-models`. Both listen on 127.0.0.1:8178 | Listening: speech-to-text when the desktop hands listening to this host (Devices > the Listening row's *Done by*). Relay `Martlet.Gateway.Stt` sends each utterance (16 kHz mono, at most 30 s) to loopback `/inference` and returns its text without non-speech tags; the route's model revision names the engine (`whisper.cpp-1.9.4` or `sherpa-onnx-1.13.8`); audio stays in memory |
| `f5` | NVIDIA GPU (6 GB+) with the NVIDIA Container Toolkit (`gpu=required`); image built on the host from `workers/f5/host/Dockerfile` (`python:3.12.10`, hash-locked PyTorch 2.6.0 CUDA 12.4, `f5-tts` 1.1.22, `vocos` 0.1.0, the bounded `martlet_f5_worker` and its loopback front `martlet_f5_host.py` on 127.0.0.1:50080); `martlet-f5 provision` downloads and verifies the pinned `F5TTS_v1_Base` (CC-BY-NC-4.0) and Vocos (MIT) files into volume `martlet-f5-models`, `martlet-f5 warm` loads them | Speaking: replies in a voice cloned from your reference recording when the desktop hands speaking to this host (Devices > the Speaking row's *Done by*). Relay `Martlet.Gateway.F5` streams the worker's contiguous 24 kHz mono PCM16 frames and chunk completions; cancellation is discard-only |
| `xtts` | NVIDIA GPU (4 GB+) with the NVIDIA Container Toolkit (`gpu=required`); image built on the host from `workers/xtts/host/Dockerfile` (`python:3.12.10`, hash-locked PyTorch 2.6.0 CUDA 12.4, `coqui-tts` 0.27.5 from Idiap's maintained fork, its loopback service `martlet_xtts_host.py` on 127.0.0.1:50081); `martlet-xtts provision` downloads and verifies the pinned XTTS-v2 checkpoint, config, tokenizer and speaker files (Coqui Public Model License 1.0.0, non-commercial only) into volume `martlet-xtts-models`, `martlet-xtts warm` loads them; choices `XTTS_MODEL` (`xtts-v2`) and `XTTS_LANGUAGE` (`en` default) | Speaking with [XTTS-v2](../../docs/XTTS_VOICE.md): replies in a voice cloned from your reference recording, streamed while they are generated, when the desktop hands speaking to this host with the XTTS-v2 engine (Companion > Voice > Voice engine). Relay `Martlet.Gateway.Xtts` (the F5 relay on route `martlet.gateway.xtts-synthesis.v1`) streams contiguous 24 kHz mono PCM16 frames; a killed model process, or a model that failed to load (for example while another engine held the GPU's memory), restarts for the next reply |
| `chatterbox` | NVIDIA GPU (6 GB+, compute capability 7.0+: GTX 16 / RTX 20 series or newer) with the NVIDIA Container Toolkit (`gpu=required`); Martlet's default voice-cloning engine (F5 stays selectable). Image built on the host from `workers/chatterbox/host/Dockerfile` (`python:3.12.10`, hash-locked PyTorch 2.8.0 CUDA 12.8 (GeForce RTX 50 series included), `chatterbox-tts` 0.1.7, `transformers` 5.2.0 and `martlet_chatterbox_host.py` on 127.0.0.1:50083); `martlet-chatterbox provision` downloads and verifies the pinned Chatterbox-Turbo model (MIT) into volume `martlet-chatterbox-models`, `martlet-chatterbox warm` loads it | Speaking: replies in a voice cloned from a >5 s reference recording with native sound tags (`[laugh]`, `[chuckle]`, `[sigh]`, `[gasp]`, `[cough]`, `[clear throat]`, `[groan]`, `[sniff]`, `[shush]`) and tone tags (`[whispering]`, which the model itself whispers only now and then, as the service adds no whisper of its own; `[happy]`, `[sarcastic]`, `[surprised]`, `[angry]`, `[fear]`, `[crying]` and `[dramatic]` are read but measurably not performed); every reply preserves Resemble AI's Perth watermark. Relay `Martlet.Gateway.F5.ChatterboxRelay` serves the chatterbox route `martlet.gateway.chatterbox-synthesis.v1` and streams contiguous 24 kHz mono PCM16 frames and chunk completions; cancellation is discard-only |
| `chatterbox-original` | NVIDIA GPU (6 GB+, compute capability 7.0+) with the NVIDIA Container Toolkit (`gpu=required`); the chatterbox role's image (`martlet-chatterbox:12`, the same `workers/chatterbox` service on 127.0.0.1:50089); `martlet-chatterbox provision` downloads and verifies the pinned original English Chatterbox (`ResembleAI/chatterbox` revision `5bb1f6e`: `t3_cfg`, `s3gen`, voice encoder and tokenizer, 3.2 GB, MIT; not its multilingual weights or built-in voice) into volume `martlet-chatterbox-original-models`, `martlet-chatterbox warm` loads it; choice `CHATTERBOX_MODEL` (`chatterbox-original`) | Speaking with [Chatterbox Original](../../docs/CHATTERBOX_VOICE.md#chatterbox-original-general-and-expressive): replies in a voice cloned from a >5 s reference recording, each sentence General or, when the reply starts it with `[expressive]`, Expressive, with the exaggeration and CFG weight the desktop sends (`voice_style`); no whispering (the model can't, and the service adds no whisper of its own) and no sound tags. Relay `Martlet.Gateway.F5.ChatterboxRelay` on route `martlet.gateway.chatterbox-original-synthesis.v1`; whole pieces, contiguous 24 kHz mono PCM16; every reply keeps the Perth watermark |
| `chatterbox-nano` | x86_64 Docker; an NVIDIA GPU (4 GB+) when the host has one, otherwise the CPU (`gpu=optional`, asked GPU or CPU); the chatterbox role's image (`martlet-chatterbox:12`, whose CUDA PyTorch also runs on the CPU; the service on 127.0.0.1:50088 with `MARTLET_CHATTERBOX_DEVICE=auto`); `martlet-chatterbox provision` downloads and verifies the pinned Chatterbox Nano (`ResembleAI/chatterbox-nano` revision `71ccd1d`, 1.9 GB, MIT) into volume `martlet-chatterbox-nano-models`, `martlet-chatterbox warm` loads it; choice `CHATTERBOX_MODEL` (`chatterbox-nano`) | Speaking with [Chatterbox Nano](../../docs/CHATTERBOX_VOICE.md#chatterbox-nano): Turbo's tags from a smaller model; on the GPU it streams like Turbo, on the CPU it streams each piece on its own schedule (a first chunk after 55 speech tokens, or more for a long piece, then chunks only as playback needs them, each keeping back up to 8 speech tokens for the next decoding so the joins sound like a whole piece; a short piece is spoken whole) with at most 8 threads (on native Linux one on each performance core of a hybrid Intel CPU; Docker Desktop can't pin) and 1 decoder step (on an i7-13700K, measured outside the container: whole pieces at about 0.5x real time pinned and 0.65x not pinned; streamed, 3.3-4.1 s sentences start after a median 1.77-2.07 s instead of 2.63-2.85 s, and 13 s pieces after 1.86 s instead of 8.34 s; slower when the CPU is busy). Relay `Martlet.Gateway.F5.ChatterboxRelay` on route `martlet.gateway.chatterbox-nano-synthesis.v1`; every reply keeps the Perth watermark |
| `gpt-sovits` | NVIDIA GPU (4 GB+) with the NVIDIA Container Toolkit (`gpu=required`); image built on the host from `workers/gpt-sovits/host/Dockerfile` (`python:3.11.13`, GPT-SoVITS `20250606v2pro` by commit and SHA-256 in its own environment, hash-locked PyTorch 2.6.0 CUDA 12.4, NLTK English data and the OpenJTalk dictionary, its worker and loopback service `martlet_gpt_sovits` on 127.0.0.1:50082); `martlet-gpt-sovits provision` downloads and verifies the pinned v2Pro pair (GPT `s1v3.ckpt`, SoVITS `s2Gv2Pro.pth`) with its speaker-verification, HuBERT and RoBERTa models from `lj1995/GPT-SoVITS` (MIT model card) and fastText `lid.176.bin` (CC-BY-SA-3.0) into volume `martlet-gpt-sovits-models`, `martlet-gpt-sovits warm` loads them and checks both weight identities; choice `GPT_SOVITS_MODEL` (`gpt-sovits-v2pro`) | Speaking with [GPT-SoVITS](../../docs/GPT_SOVITS_VOICE.md): anime-style voices cloned from a 3-10 second recording, each sentence sent as soon as it is generated, when the desktop hands speaking to this host with the GPT-SoVITS engine (Companion > Voice > Voice engine). Relay `Martlet.Gateway.GptSovits` (the F5 relay on route `martlet.gateway.gpt-sovits-synthesis.v1`, also checking the GPT weights) streams contiguous 24 kHz mono PCM16 frames; a dead worker restarts for the next reply |
| `dia` | NVIDIA GPU (8 GB+) with the NVIDIA Container Toolkit (`gpu=required`); image built on the host from `workers/dia/host/Dockerfile` (`python:3.12.10`, hash-locked PyTorch 2.6.0 CUDA 12.4 and descript-audio-codec 1.0.0, Nari Labs' Dia source fetched by SHA-256 at commit `876125e`, its loopback service `martlet_dia.host` on 127.0.0.1:50084); `martlet-dia provision` downloads and verifies the pinned Dia-1.6B-0626 weights and config (Apache-2.0, revision `ef2795f`) and the DAC 44 kHz codec (MIT) into volume `martlet-dia-models`, `martlet-dia warm` loads them; choice `DIA_MODEL` (`dia-1.6b-0626`) | Speaking with [Dia](../../docs/DIA_VOICE.md): replies in a voice cloned from your reference recording, with nonverbal cues such as `(laughs)` or `(sighs)` performed (English only), when the desktop hands speaking to this host with the Dia engine (Companion > Voice > Voice engine). Relay `Martlet.Gateway.Dia` (the F5 relay on route `martlet.gateway.dia-synthesis.v1`) sends contiguous 24 kHz mono PCM16 frames resampled from Dia's 44.1 kHz output; a busy model makes the next reply wait and a killed model process restarts for it |
| `singing` | NVIDIA GPU (6 GB+) with the NVIDIA Container Toolkit (`gpu=required`); image built on the host from `workers/singing/host/Dockerfile` (`python:3.11`, hash-locked PyTorch 2.10 CUDA 12.8 and ACE-Step's own locked dependencies, ACE-Step 1.5 at commit `ca1e85f`, SoulX-Singer at `81aeb3a` and Amphion at `26f6883` fetched and checked by commit, Demucs 4.0.1, its loopback service `martlet_singing.host` on 127.0.0.1:50085); `martlet-singing provision` downloads and verifies about 16 GB of pinned models (ACE-Step 1.5 turbo, SFT, VAE, text encoder and 0.6B planner, MIT; SoulX-Singer-SVC and RMVPE, Apache-2.0; Whisper base, Apache-2.0; the Demucs vocals model, MIT) into volume `martlet-singing-models`; choices `SINGING_MODEL` (`ace-step-v15-soulx-svc`) and `SINGING_VOICE_MATCHES` (`soulx`, or `soulx-vevosing` to add VevoSing's Vevo1.5 models, CC-BY-NC-ND-4.0, and Whisper medium, with their terms). Not a voice engine (no `exclusive=voice`): it runs beside one and its worker exits after five idle minutes, freeing the GPU | [Singing](../../docs/SINGING.md): songs written by ACE-Step from lyrics and a style and sung in a voice of the shared voice list (SoulX-Singer-SVC zero-shot), made as background jobs (Companion > Voice > Singing, and the conversation). Relay `Martlet.Gateway.Singing` serves route `martlet.gateway.song.v1`: start, status, result pages (48 kHz PCM16 mix, vocals and backing) and cancel; the gateway hands the service the voice's recording from its shared speaking-voice list |
| `pictures` | NVIDIA GPU (8 GB+) with the NVIDIA Container Toolkit (`gpu=required`); image built on the host from `workers/pictures/host/Dockerfile` (`python:3.12`, pinned PyTorch 2.10 CUDA 12.8, ComfyUI v0.39.0 at commit `b0b7435`, loopback ComfyUI on 127.0.0.1:50086); `martlet-pictures provision` downloads and verifies about 19.3 GiB (20.7 GB) of Apache-2.0 Z-Image Turbo files from `Comfy-Org/z_image_turbo` revision `6fc90a3` into volume `martlet-pictures-models`: diffusion model `z_image_turbo_bf16.safetensors`, text encoder `qwen_3_4b.safetensors` and VAE `ae.safetensors`; choice `PICTURES_MODEL` (`z-image-turbo`). Not a voice engine: it runs beside one; Martlet calls ComfyUI's free-memory API after idle picture work so it frees the graphics card for voice and thinking roles | [Pictures host](../../docs/PICTURES_HOST.md): Martlet sends ComfyUI API-format workflows through relay `Martlet.Gateway.Pictures` on route `martlet.gateway.picture.v1`: status, prompt, history, queue, paged image view, cancel and free. Only prompt IDs and ComfyUI output names are accepted; no client path or URL reaches the host |
| `ocr` | Docker; loopback service on 127.0.0.1:50087. Engine choice `OCR_ENGINE`: **`rapidocr`** (default): image built on the host from `workers/ocr/host/Dockerfile` (`python:3.12.10`, hash-pinned RapidOCR 1.4.4, ONNX Runtime 1.30.0, OpenCV headless 5.0.0.93 and NumPy 2.5.3) with the PaddleOCR PP-OCRv4 ONNX models inside the RapidOCR package, on the processor; choice `OCR_MODEL` (`rapidocr-ppocrv4`). **`ppocrv5`**: image built on the host from `workers/ocr/host/Dockerfile.ppocrv5` (hash-pinned RapidOCR 3.10.0 and ONNX Runtime 1.31.0) with PaddleOCR's PP-OCRv5 mobile and server ONNX models (Apache-2.0, SHA-256 checked) baked in; choice `OCR_MODEL` (`ppocrv5-mobile`, about 2 s a screenshot on the processor, or `ppocrv5-server`, for an NVIDIA graphics card only) and `accelerator` (`gpu=optional`: the CUDA 13 build, `compose.gpu.yaml`, needs NVIDIA driver 580 or newer) | [OCR host](../../docs/OCR_HOST.md): Martlet sends one bounded PNG or JPEG screen image through relay `Martlet.Gateway.Ocr` on route `martlet.gateway.ocr.v1`. The worker returns recognized text lines with scores and boxes. Images and text stay in memory and are not stored |
| `audio2face` | NVIDIA GPU (4 GB+; RTX 20 series or newer for the local engine) with the NVIDIA Container Toolkit (`gpu`, `docker`, `nvidia-toolkit`); models `claire`/`mark`/`james`. Engine choice `A2F_ENGINE`: **`local`** (default, no NVIDIA account or key): image built on the host from [`workers/audio2face`](../../workers/audio2face/README.md), NVIDIA's open-source Audio2Face-3D SDK (MIT) with CUDA 12.8 and TensorRT 10.9 behind Martlet's gRPC front; on first start it downloads the chosen model from Hugging Face at a pinned revision (SHA-256 checked, NVIDIA Open Model License) into volume `martlet-audio2face-models` and builds its TensorRT engine for that GPU there (driver 570+). **`nim`**: NVIDIA's NIM `nvcr.io/nim/nvidia/audio2face-3d:1.3` with your free NVIDIA account's [NGC API key](https://org.ngc.nvidia.com/setup/api-key) (development and testing use; NVIDIA lists that release as end of support). Both listen on 127.0.0.1:52000 | Automatic lip-sync uses it when the desktop hands lip-sync to this host (Devices > the Lip-sync row's *Done by*). Relay `Martlet.Gateway.Audio2Face` streams each speech chunk to the engine's `ProcessAudioStream` and returns ARKit blendshape frames |
| `home-assistant` | Linux Docker Engine (not Docker Desktop); official `ghcr.io/home-assistant/home-assistant:2026.9.4`; host networking on port 8123, privileged USB/Bluetooth access, `/run/dbus:/run/dbus:ro`, config in volume `martlet-home-assistant-config` | Smart home: Martlet installs and onboards Home Assistant through HA's own HTTP/WebSocket API at `http://<host LAN address>:8123`. It is a route-less `feature=home-assistant` role, not relayed through the Martlet gateway |

On a native host the `ollama` role listens on the host's own 127.0.0.1:11434 and
`deep-thinking` on 127.0.0.1:11435, so stop any Ollama already installed there
first; `stt` uses 127.0.0.1:8178. Home
Assistant uses the host LAN port 8123, so connect an existing HA instance in the
Smart home page instead of installing another one.

## Status

- **Docker method**: run end to end on Windows with Docker Desktop (WSL 2): image
  build, `setup` (identity, approval, gateway container), health over the LAN
  address, `pair` with the desktop's pairing-code client, `add` of a GPU-free
  stand-in role and the desktop seeing its Audio2Face route, a simulated reboot
  (network holder restart) and `remove`.
- **Native method**: exercised in a Linux container with stubbed system commands.
- **In-app SSH runner** (`HostShell`): run against a local OpenSSH server container
  (Alpine, an account without Docker access, sudo with a password) backed by
  Docker Desktop's engine: first-contact password and key install, host key
  pinning and mismatch refusal, stdin input, streaming, Cancel, sudo through the
  askpass helper, `--yes setup` (owner-init), automatic `pair` (code read from the
  output, redeemed, gateway restarted), the machine report, `describe`, `status`,
  and `add`/`remove` of a GPU-free stand-in role with a secret and a choice over
  stdin (owner-approve re-approval, route visible to the desktop). The native
  Ubuntu method over SSH (systemd user service, apt, lingering) and a real remote
  Linux machine are not yet run.
- **Windows Firewall step**: the non-admin probe and the rule script (as `-WhatIf`)
  were run; the elevated change itself has not been applied on a test machine.
- **Audio2Face role, `local` engine**: on Windows with Docker Desktop and an NVIDIA
  RTX 4070 (driver 610.88), the image built from `workers/audio2face` (SDK, CUDA
  12.8, TensorRT 10.9); the role's `compose.yaml` with profile `local` ran in a
  shared network namespace like the Docker method's (disposable volumes): its first
  start downloaded the pinned `mark` files (SHA-256 checked), built the FP16
  TensorRT engine and listened only on 127.0.0.1:52000; a restart reused both and
  answered within seconds. MCP's `audio2face_check` animated 1-4 s test signals
  at 24, 44.1 and 48 kHz through the production Audio2Face client (30 frames per
  second, 52 shared channels, well under a second per clip); `claire` passed the
  same way. `martlet-host add`'s variant handling (choices before terms, the NIM's
  secret, registry login and assets only for `nim`, the profile switch stopping
  the other engine) was run with Docker, the gateway and the network stubbed. Not
  yet run: `martlet-host add audio2face` end to end on a host, a Linux host, the
  gateway relay against this engine (it uses the same client), the `nim` engine
  on a real GPU, or a live conversation.
- **Ollama role**: on Windows with Docker Desktop (CPU, no GPU), the role's
  `compose.yaml` ran in a shared network namespace like the Docker method's, listened
  only on loopback, and its post-start `ollama pull` worked (with `qwen2.5:0.5b`); a real
  `ollama/ollama:0.34.4` container then answered a desktop client's chat request (persona
  and history included) through the pinned gateway and `Martlet.Gateway.Ollama`. The
  GPU/CPU and model-suggestion logic of `martlet-host add` was checked in bash on sample
  values. Not yet run: `martlet-host add ollama` end to end, a Linux host, or an NVIDIA GPU
  with the GPU overlay.
- **Speech-to-text role** (`stt`): on Windows with Docker Desktop (CPU, no GPU), the role's
  `compose.yaml` ran in a shared network namespace like the Docker method's with the `base`
  model: it downloaded the model at the pinned revision, verified its SHA-256, listened only
  on 127.0.0.1:8178 and transcribed whisper.cpp's `jfk.wav` sample; a desktop client then
  transcribed that sample through the pinned gateway and `Martlet.Gateway.Stt` (2.2 s on the
  CPU). Not yet run: `martlet-host add stt` end to end, a Linux host, the CUDA build on an
  NVIDIA GPU, or a live conversation turn against a real host.
  Its `parakeet` engine: on Windows with Docker Desktop (CPU), the image built from
  `workers/parakeet` (also built for linux/arm64 under emulation, where its packages
  import); the role's `compose.yaml` with profile `parakeet` ran in a shared network
  namespace like the Docker method's (disposable project): its prepare run downloaded
  `parakeet-tdt-110m-en` at the pinned revision with every file's size and SHA-256 checked,
  then the service loaded it in about 3 s, listened only on 127.0.0.1:8178 and
  answered `/status`. MCP's `listening_engine_check` transcribed four synthesized phrases
  through the pinned gateway, `Martlet.Gateway.Stt` and the desktop's paired client
  (5.6% word errors, about 0.1 s per phrase). `martlet-host add`'s engine choice (the
  variant's own models, GPU option and prepare step, and a switch that stops the previous
  engine only once the chosen one is prepared) was run with Docker stubbed. Not yet run:
  `martlet-host add stt` with Parakeet end to end on a host, a real aarch64 host, or a
  live conversation turn.
- **F5 voice role** (`f5`): on Windows with Docker Desktop (CPU, no GPU), the role's
  `compose.yaml` built its image from `workers/f5` (hash-locked install, runtime inventory
  generated at build time) and ran as its unprivileged user; `martlet-f5 provision`
  downloaded all four pinned model files and verified their SHA-256; the worker's own
  runtime-inventory, artifact and imported-origin checks passed and the pinned F5/Torch/Vocos
  imports raised no audit-hook denial; the worker's production engine loaded
  `F5TTS_v1_Base` and synthesized 3.2 s of 24 kHz PCM on the CPU (a direct engine call, since
  the worker config only allows `cuda:N`, so `warm` itself stops at the missing GPU). With the
  deterministic fixture engine (FIXTURE - NOT AI), a desktop client spoke through the pinned
  gateway, `Martlet.Gateway.F5`, `martlet_f5_host.py` and the real worker. Not yet run:
  `martlet-host add f5` end to end, a Linux host, an NVIDIA GPU (warmup, latency, VRAM),
  real voice quality, or a live conversation turn against a real host.
- **Update**: the engine script passes `bash -n` and the desktop's update
  commands are covered by unit tests. `update` itself has not yet been run
  against a real Docker or native host.
- **Machine report**: the collector was run natively in an Ubuntu 24.04 container
  and in Docker mode against Docker Desktop without GPU support (`gpus: []`,
  `nvidia_containers: "no"`); `nvidia-smi` output parsing was checked with sample
  lines. Not yet run against a host with a working NVIDIA or AMD GPU.
- **Preparing a computer**: `martlet-prepare` was run in Ubuntu 24.04 containers
  (one with systemd): `status`, `updates`, `docker`, `headless` (both boot
  modes), the dummy `virtual-display` and its removal, `tools` (Python, Node.js
  LTS, git, build-essential, htop, nvtop, curl, tmux), `reboot`, and `gpu-power`
  (limits, validation, boot service, reset) against a stand-in `nvidia-smi`.
  The desktop's runner (`SshHostShell`, the in-app SSH runner behind
  `IHostShell`) ran `status --json` with the script on stdin against the local
  OpenSSH test container; the prepare window itself was built but not driven end
  to end. Not yet run: a real NVIDIA GPU (driver install, container toolkit,
  NVIDIA virtual display, CUDA toolkit), a real Wake-on-LAN card and wake-up, and
  a reboot of a real computer.
