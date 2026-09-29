# Martlet host: one install flow for every role and every machine

Extra machines (a GPU PC or server on your network, or this Windows PC through
Docker Desktop) run Martlet **roles** that the desktop uses. Every host runs the
same engine, `martlet-host`, with the same commands; the installation **method**
only decides how that engine reaches the machine. Nothing about a role lives in
the tool itself.

```text
Windows desktop (Martlet) --pinned TLS, paired once--> host: Martlet gateway :9443 (private LAN address only)
                                                         | one gateway route per installed role
                                                         v
                                                       role services on the host's 127.0.0.1 only
                                                       (Ollama thinking, whisper listening, F5 speaking and Audio2Face lip-sync today; more roles plug in the same way)
```

## Methods

| Method | Host needs | How to run it | Gateway runs as |
| --- | --- | --- | --- |
| **Desktop: this PC with Docker Desktop** | Windows + Docker Desktop (WSL 2) | Martlet > **Martlet hosts** > *This PC* | containers `martlet-host-net` + `martlet-host-gateway` |
| **Desktop: another computer over SSH, Docker** | SSH server + Docker (the one prerequisite; Linux x86_64; for another Windows PC install Martlet there and use *This PC*) | Martlet hosts > *over SSH, using Docker* > **Add this computer** (runs in Martlet) | same containers on that host |
| **Desktop: another computer over SSH, native** | Ubuntu 24.04 x86_64 with SSH | Martlet hosts > *over SSH, native Ubuntu* > **Add this computer** (runs in Martlet) | systemd user service `martlet-host-gateway` |
| **On the host, Docker** | any Docker host | `docker run ... martlet-host <command>` (below) | containers |
| **On the host, native** | Ubuntu 24.04 x86_64 | `./deploy/host/martlet-host <command>` (below) | systemd user service |

For SSH hosts the desktop does everything itself (see [Driving Linux hosts from
Windows](#driving-linux-hosts-from-windows-over-ssh)): you enter `user@computer`
and the password once, and never need to log in to that computer. The owner's
click in Martlet, over the owner's own SSH session, is the confirmation for each
change. For *This PC* a console window runs the command shown in Martlet hosts,
and on the host itself every change is confirmed by typing `yes`.

Commands are the same everywhere:

```text
setup               gateway, identity and start at boot (once per host)
pair                pair a desktop in the gateway console; shows a one-use pairing code (repeat per desktop)
pair --device-id <id> --name <name>
                    pair that desktop without a console: prints one "pairing-code: martlet-pair-v1..." line,
                    waits up to five minutes for it to be redeemed, then restarts the gateway
roles               what this host can run
describe <role>     a role's terms, secrets (stored or missing, never values), choices and GPU/CPU option, machine-readable
add <role>          install a role, e.g. add ollama, add stt, add f5 or add audio2face (same flow for every role)
remove <role>       stop a role and unpublish it (keeps its data)
machine             report this machine's hardware to paired desktops (also done by setup, pair, add and remove)
update              update the gateway to this engine's Martlet version (identity, pairings, roles and data stay)
status | config     show the gateway and roles | print the generated host.json
```

`--yes` before any command (or `MARTLET_ASSUME_YES=1`) runs it without questions:
confirmations are taken as given, the gateway identity and service approval are
handled by the gateway's owner commands, and answers come from stdin as
`KEY=VALUE` lines ended by `end` (`secret.<name>=...`, `choice.<VAR>=...`), never
from arguments or the environment. Martlet desktop always uses it for SSH hosts;
a human on the host normally does not.

### Updating a host

Hosts follow the desktop's Martlet version. The gateway reports the release it
was built from (`martlet_version` on `GET /martlet/v1/machine`), and the
desktop's Devices map shows *Update available* when a host is older than the
desktop. **Update host** runs `update` through the same route as every other
command:

- **Docker**: builds `martlet-host:<desktop version>` from the `v<version>` tag
  (falling back to `main`) when it is not there yet, then runs `update` from it.
  The gateway container is recreated from the new image; older unused
  `martlet-host` images are removed (the network holder keeps its own).
- **Native**: fetches and checks out the `v<version>` tag in `~/Martlet`
  (falling back to `main`), publishes the gateway beside the running one, then
  swaps it in and restarts the service.

`update` asks nothing unless the new version changes `host.json`; that renews
the service approval (in the gateway console, or with `--yes` through
`owner-approve`), like any configuration change. For SSH hosts **Update host**
runs `--yes update` in Martlet, so the click is that approval.
With *Keep my Martlet hosts on this PC's version* (Settings > App updates) the
desktop runs `update` in the background for older hosts every check interval,
without asking anything: for SSH hosts through Martlet's SSH runner (its own key,
the pinned host key and a sudo password only if you chose to remember one; no
`--yes`), for this PC in Docker Desktop. A host that needs a password, a new host
key, sudo or an approval fails that run without changing anything and keeps
*Update host*.

### What the host tells Martlet

`setup`, `pair`, `add`, `remove` and `machine` collect what the machine is like
into `machine.json` beside `host.json`: OS and kernel, CPU and thread count,
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
  shared-namespace containers.
- Every command runs in a fresh engine container as the unprivileged `martlet`
  user (uid 1000) inside that network. `-u 0` only lets the launcher reach the
  Docker socket.
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

### Native Ubuntu

On Ubuntu 24.04 x86_64, as your normal user:

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
Missing Docker, NVIDIA driver or NVIDIA Container Toolkit are installed only after
a `yes`.

## Pairing

**SSH hosts** pair by themselves: **Add this computer** (or **Pair automatically
over SSH** on the Pair step) runs `martlet-host --yes pair --device-id <this PC>
--name <this PC>` there. The gateway starts its listener, prints the one-use
code on one line and waits; Martlet reads the code from the output (it is never
shown or logged; Docker runs the engine with `--log-driver none`), redeems it with
the same pairing client as the **Pair with host** button and stores the device
secret in Windows Credential Manager, and the host restarts its gateway. If
redeeming fails, Martlet sends `cancel` so the host stops waiting at once.

**This PC or by hand:** in Martlet > **Martlet hosts**, copy *This PC's device
ID* and press **Open pairing console**. In the host console confirm opening with
`yes`, type `start` (`yes`), then `pair` with that device ID, a name and role
`voice` (`yes`). The host shows a pairing code `martlet-pair-v1....` (origin,
host ID, TLS pin, one-use pairing ID and token). Paste it into Martlet hosts and
press **Pair with host** while the console is open; then press a key, `list`
confirms, and `stop` (`yes`) restarts the service. On a host you can also run
`martlet-host pair --device-id <id> --name <name>` and paste the printed code.
**Check host** shows which roles the host offers and the hardware it reported.

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

Afterwards the map's per-host actions (add or remove a role, show status) run the
same way. Adding a role first runs `describe <role>` there and shows its
requirements, terms, secrets and choices in Martlet; the **Install** click is the
confirmation, and the secrets (for example the NGC API key) go to the host over
stdin and are stored there in the host's private config (0600).

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
| Requirements | `requires` | Shared checks/installs: `gpu` (NVIDIA driver), `docker` (Engine + Compose), `nvidia-toolkit` (native method) |
| Terms | `terms` | Shown; continue only on `yes` (or shown in Martlet, whose Install click confirms) |
| GPU or CPU | `gpu=optional\|<overlay>.yaml`, `gpu=required\|<overlay>.yaml` | Detects NVIDIA GPU memory usable by containers. `optional` asks `gpu` or `cpu` (default: GPU when present; Martlet sends `choice.accelerator` or lets the host decide); `required` always uses the GPU. `gpu` adds the role's Compose overlay (and the NVIDIA requirements); `cpu` runs without it |
| Secrets | `secret=name\|prompt` | Asked once (or sent by Martlet on stdin), stored in the host config `secrets/<name>` (0600), passed to Compose as environment secret `<NAME>` |
| Choices | `choice=VAR\|label\|options\|default`, `choice_by_vram=VAR\|<MiB>@<value> ...` | Asked each time (or chosen in Martlet, where *Automatic* keeps the suggestion), written to the role's `.env`; `choice_by_vram` suggests the default by GPU memory (ascending thresholds, `0` = CPU) |
| Registry | `registry=host\|user\|secret` | `docker login` with the stored secret |
| Assets | `asset=url\|path` | Pinned HTTPS downloads (`{VAR}` uses a choice), copied into the role's `martlet-<role>-configs` volume |
| Loopback | `rewrite=path\|sed`, `expect=path\|text` | Rewrite configs to 127.0.0.1 and verify it |
| Service | `compose.yaml` | `docker compose up -d` with `network_mode: ${MARTLET_ROLE_NETWORK}` (host natively, the gateway's namespace in Docker); a role can build its image from Martlet's sources under `${MARTLET_SOURCE}` (the checkout natively, `/opt/martlet/source` in the host image) |
| Readiness | `port`, `ready_timeout_minutes` | Wait until 127.0.0.1:`port` accepts connections |
| Post-start | `post_start=<service>\|<command>` | Runs each command inside that service in order (`docker compose exec`; `{VAR}` uses a choice; plain words only), for example downloading a model; its progress streams to Martlet's run window |
| Publish | `gateway_kind`, `model_from` | Add `{kind, endpoint, model}` to the gateway's `host.json` `roles` list; renew the service approval (gateway console, or `owner-approve` with `--yes`); restart |

## Adding a new role

1. Add `roles/<name>/role.conf` and `roles/<name>/compose.yaml` (services listen on
   127.0.0.1 and use `network_mode: ${MARTLET_ROLE_NETWORK}`; files come from
   the external `${MARTLET_ROLE_CONFIGS_VOLUME}` volume; an optional GPU overlay
   goes next to them).
2. Add the gateway relay worker for its `gateway_kind` (see
   `Martlet.Gateway.Ollama`, `Martlet.Gateway.Stt`, `Martlet.Gateway.F5` or `Martlet.Gateway.Audio2Face`) and register the kind in
   `Martlet.Gateway.Host.Linux` (`HostConfiguration.RoleKinds`, `NativeHostPlatform.RoleWorker`).
3. Add one entry to `HostRoles` in `src/Martlet.Desktop/HostControl.cs` (kind, name, needs and the
   gateway route ID it advertises). The Devices map, the host dashboard and Martlet hosts then offer
   to install, remove and check it on any paired host; teach the desktop to use the route for its job
   (a conversation job is one `HostJob` entry in `src/Martlet.Desktop/MainWindow.HostJobs.cs`).

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
- **Listening** (speech-to-text) moves the same way between the Setup choice (OpenAI,
  Windows speech or whisper.cpp on this PC) and any paired host that runs `stt`, saved
  as a gateway speech-to-text route; the previous route is kept in
  `listening-previous.json`. Push-to-talk and hands-free utterances then go only to
  that host over its pinned TLS gateway and are transcribed there in memory.
- **Speaking** moves the same way between the Setup voice (OpenAI or Windows speech)
  and any paired host that runs `f5`, saved as a gateway F5 route; the previous route
  is kept in `speaking-previous.json`. Martlet first asks which voice to clone: one
  from its F5 voice list on this PC (`f5-voices`, Martlet.F5's reference preset store)
  or a new mono 16-bit WAV of 1-30 s with its exact transcript and your confirmation
  that the voice is yours or used with its speaker's permission (`voice-rights-v1`).
  Each reply segment's text and that reference recording then go only to that host;
  its 24 kHz mono PCM16 plays like any other voice. Keep the original recording where
  you chose it (the store re-checks it before each use).
- Handing a job to a host detaches the replaced cloud key (it is listed for removal
  in Setup, never silently deleted); handing the job back reattaches it. Jobs on the
  same host share that host's one pairing.
- **Lip-sync** moves between *This PC*, any paired host and *nobody* (voice
  loudness) instantly, without restarting the character.

From each host's details the desktop runs `add <role>`, `remove <role>` and
`status` on that host through the route it was paired with (SSH with Docker, SSH
native, or this PC's Docker Desktop), so moving a job from one GPU PC to another
is: hand it to the new host (Martlet offers to install the role there), then
remove the role from the old one. SSH hosts run these in Martlet (the click
confirms); this PC's Docker Desktop opens a console where you confirm each step.

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
Ubuntu 22.04 and 24.04 on x86_64 are supported; other systems are refused with
a clear reason.

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
| `ollama` | Docker; an NVIDIA GPU (NVIDIA Container Toolkit) makes replies fast, otherwise the CPU; official `ollama/ollama:0.34.4`, model `llama3.2:3b`, `qwen2.5:7b`, `llama3.1:8b` or `qwen2.5:14b` (suggested by GPU memory), kept in volume `martlet-ollama-models` and kept loaded | Thinking: the conversation model when the desktop hands thinking to this host (Devices > Who does what). Relay `Martlet.Gateway.Ollama` streams loopback `/api/chat` (persona, recent history, message) with an 8,192-token context |
| `stt` | Docker; an NVIDIA GPU (NVIDIA Container Toolkit, driver 580+ for CUDA 13) makes it fast, otherwise the CPU; official `ghcr.io/ggml-org/whisper.cpp` release 1.9.4 (CPU or CUDA build), model `base`, `small`, `medium` or `large-v3-turbo` (suggested by GPU memory: `small` on the CPU, `large-v3-turbo` from 4 GB), downloaded on first start from Hugging Face at a pinned revision with its SHA-256 checked into volume `martlet-stt-models`; listens on 127.0.0.1:8178 | Listening: speech-to-text when the desktop hands listening to this host (Devices > Who does what). Relay `Martlet.Gateway.Stt` sends each utterance (16 kHz mono, at most 30 s) to loopback `/inference` and returns its text without non-speech tags; audio stays in memory |
| `f5` | NVIDIA GPU (6 GB+) with the NVIDIA Container Toolkit (`gpu=required`); image built on the host from `workers/f5/host/Dockerfile` (`python:3.12.10`, hash-locked PyTorch 2.6.0 CUDA 12.4, `f5-tts` 1.1.22, `vocos` 0.1.0, the bounded `martlet_f5_worker` and its loopback front `martlet_f5_host.py` on 127.0.0.1:50080); `martlet-f5 provision` downloads and verifies the pinned `F5TTS_v1_Base` (CC-BY-NC-4.0) and Vocos (MIT) files into volume `martlet-f5-models`, `martlet-f5 warm` loads them | Speaking: replies in a voice cloned from your reference recording when the desktop hands speaking to this host (Devices > Who does what). Relay `Martlet.Gateway.F5` streams the worker's contiguous 24 kHz mono PCM16 frames and chunk completions; cancellation is discard-only |
| `audio2face` | NVIDIA GPU (4 GB+), free NVIDIA account with an [NGC API key](https://org.ngc.nvidia.com/setup/api-key); NIM `nvcr.io/nim/nvidia/audio2face-3d:1.3`, models `claire`/`mark`/`james` | Automatic lip-sync uses it when the desktop hands lip-sync to this host (Devices > Who does what) |

On a native host the `ollama` role listens on the host's own 127.0.0.1:11434, so
stop any Ollama already installed there first; `stt` uses 127.0.0.1:8178.

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
- Not yet run: a real NVIDIA GPU with the Audio2Face NIM.
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
