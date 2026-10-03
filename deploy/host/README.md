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
| **Desktop: another computer over SSH, Docker** | SSH server + Docker (the one prerequisite; Linux x86_64; for another Windows PC install Martlet there and use *This PC*) | Martlet hosts > *over SSH, using Docker* > **Add this computer** (runs in Martlet) | same containers on that host |
| **Desktop: another computer over SSH, native** | Ubuntu 24.04 x86_64 with SSH | Martlet hosts > *over SSH, native Ubuntu* > **Add this computer** (runs in Martlet) | systemd user service `martlet-host-gateway` |
| **On the host, Docker** | any Docker host | `docker run ... martlet-host <command>` (below) | containers |
| **On the host, native** | Ubuntu 24.04 x86_64 | `./deploy/host/martlet-host <command>` (below) | systemd user service |

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
                    Martlet (Devices > Add a computer > Enter a pairing code); waits up to five minutes (repeat per desktop)
pair --device-id <id> --name <name>
                    pair that desktop without a console: prints one "pairing-code: martlet-pair-v1..." line,
                    waits up to five minutes for it to be redeemed, then restarts the gateway (Martlet uses this itself)
console             the gateway console: list paired desktops and revoke one
network-reset       leave this host's Martlet network (pairings stay); the next desktop that pairs adds it to its own network
roles               what this host can run
describe <role>     a role's terms, secrets (stored or missing, never values), choices, GPU/CPU option and route-less feature, machine-readable
add <role>          install a role, e.g. add ollama, add stt, add f5, add xtts, add audio2face or add home-assistant (same flow for every role)
remove <role>       stop a role and unpublish it (keeps its data)
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
desktop. **Update host** runs `update` through the same route as every other
command:

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
  run window. Martlet on a host PC also keeps its own host service on its
  version by itself. See [Commands between your computers](../../docs/CLUSTER.md#commands-between-your-computers).

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
`--yes`), for this PC in Docker Desktop. A host that needs a password, a new host
key, sudo or an approval fails that run without changing anything and keeps
*Update host*.

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

The report also has a `features` array (up to 16 lowercase tokens). The host
uses it for route-less and smart-home capabilities: `host-network` means this
method can run LAN host-network roles (native Ubuntu, or Docker on Linux Engine
but not Docker Desktop); installed route-less roles add their `feature=` value
such as `home-assistant`; probes add `ha-existing` (something else on port
8123), `mqtt-broker` (port 1883), smart-home container names (`zigbee2mqtt`,
`zwave-js`, `frigate`, `go2rtc`, `esphome`, `node-red`, `matter-server`,
`music-assistant`), USB radio hints (`zigbee-radio`, `zwave-radio`,
`serial-radio`) and `bluetooth`. Failed probes are ignored.

The gateway also keeps a copy of the shared **who does what** plan in
`cluster.json` beside `host.json`, written by the gateway when a paired desktop
syncs (Devices > Settings for all devices > *Keep who does what in sync*). It names which
host does each job, which jobs fail over and which roles each host runs; it
holds no keys and the host never acts on it. See
[Shared who does what and failover](../../docs/CLUSTER.md). Beside them it keeps
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

**Pair once for all your computers.** Every desktop and host belongs to the
owner's [Martlet network](../../docs/NETWORK.md). The first desktop you pair a
host with adds that host to its network, and every other computer in the
network then pairs with it by itself within a minute (no code, no console, no
SSH login of its own). A new desktop joins by pairing with any one host of the
network and being allowed on one of your other computers (check number on both
screens). So for a Linux machine you set up over SSH from one PC, the whole
network can use it right away.

**SSH hosts** pair by themselves: **Add this computer** (or **Pair over SSH** on
the Pair step) runs `martlet-host --yes pair --device-id <this PC>
--name <this PC>` there. The gateway starts its listener, prints the one-use
code on one line and waits; Martlet reads the code from the output (it is never
shown or logged; Docker runs the engine with `--log-driver none`), redeems it
and stores the device secret in Windows Credential Manager, and the host
restarts its gateway. If redeeming fails, Martlet sends `cancel` so the host
stops waiting at once.

**This PC:** **Pair automatically** runs the same unattended pairing on this PC's
Docker Desktop. To pair *another* desktop with this PC's host, the host
dashboard's **Show a pairing code** shows this PC's address and a short code in
large type (never logged); type both on the other desktop.

**By hand (any host):** on the host run `martlet-host pair`. It shows:

```text
Pair a Martlet desktop with gpu-pc-host
  In Martlet on the desktop: Devices > Add a computer > Enter a pairing code, then type
    Address:  192.168.1.20
    Code:     K7QM-4XPA
  The code works once and expires in five minutes. Type cancel to withdraw it.
```

In Martlet choose **Devices > Add a computer > Enter a pairing code**, type the
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

Afterwards the map's per-host actions (add or remove a role, show status) run the
same way. Adding a role first runs `describe <role>` there and shows its
requirements, terms, secrets and choices in Martlet; the **Install** click is the
confirmation, and the secrets (for example the NGC API key of the Audio2Face NIM engine) go to the host over
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
| Requirements | `requires` | Shared checks/installs: `gpu` (NVIDIA driver), `docker` (Engine + Compose), `docker-engine` (Linux Docker Engine, not Docker Desktop, for LAN host-network roles), `nvidia-toolkit` (native method) |
| GPU or CPU | `gpu=optional\|<overlay>.yaml`, `gpu=required\|<overlay>.yaml` | Detects NVIDIA GPU memory usable by containers. `optional` asks `gpu` or `cpu` (default: GPU when present; Martlet sends `choice.accelerator` or lets the host decide); `required` always uses the GPU. `gpu` adds the role's Compose overlay (and the NVIDIA requirements); `cpu` runs without it |
| Choices | `choice=VAR\|label\|options\|default`, `choice_by_vram=VAR\|<MiB>@<value> ...`, `profile_from=VAR` | Asked each time (or chosen in Martlet, where *Automatic* keeps the suggestion), written to the role's `.env`; `choice_by_vram` suggests the default by GPU memory (ascending thresholds, `0` = CPU); `profile_from` makes that choice the role's Compose profile (`COMPOSE_PROFILES`), so one role can offer variants such as the Audio2Face engine. Changing the variant on a re-add stops the previous one first (its volumes are kept) |
| Variants | `[VAR=value]` ... `[end]` | Entries between these lines (terms, secrets, registry, assets, loopback rewrites) apply only when choice `VAR` is `value`; `describe` lists them as `role.terms_when` and `role.secret_when`, so Martlet shows them only for that choice |
| Terms | `terms` | Shown (every one that applies to the choices made); continue only on `yes` (or shown in Martlet, whose Install click confirms) |
| Secrets | `secret=name\|prompt` | Asked once (or sent by Martlet on stdin), stored in the host config `secrets/<name>` (0600), passed to Compose as environment secret `<NAME>` |
| Registry | `registry=host\|user\|secret` | `docker login` with the stored secret |
| Assets | `asset=url\|path` | Pinned HTTPS downloads (`{VAR}` uses a choice), copied into the role's `martlet-<role>-configs` volume |
| Loopback | `rewrite=path\|sed`, `expect=path\|text` | Rewrite configs to 127.0.0.1 and verify it |
| Service | `compose.yaml`, `network=host` | `docker compose up -d` with `network_mode: ${MARTLET_ROLE_NETWORK}` (host natively, the gateway's namespace in Docker), unless the role declares `network=host` and its Compose file intentionally uses the host network on both methods; a role can build its image from Martlet's sources under `${MARTLET_SOURCE}` (the checkout natively, `/opt/martlet/source` in the host image) |
| Readiness | `port`, `ready_timeout_minutes` | Wait until the service accepts connections (127.0.0.1:`port`, or the host LAN address for Docker `network=host` roles) |
| Post-start | `post_start=<service>\|<command>` | Runs each command inside that service in order (`docker compose exec`; `{VAR}` uses a choice; plain words only), for example downloading a model; its progress streams to Martlet's run window |
| Publish | `gateway_kind`, `model_from`, `feature` | Route roles add `{kind, endpoint, model}` to the gateway's `host.json` `roles` list; renew the service approval (gateway console, or `owner-approve` with `--yes`); restart. Route-less roles declare `feature=<token>` instead, write an installed role record with `feature`, `port` and `network=host`, collect `machine.json` and restart the gateway without changing `host.json` |

## Adding a new role

1. Add `roles/<name>/role.conf` and `roles/<name>/compose.yaml`. Gateway-routed
   services listen on 127.0.0.1 and use `network_mode: ${MARTLET_ROLE_NETWORK}`;
   files come from the external `${MARTLET_ROLE_CONFIGS_VOLUME}` volume; an
   optional GPU overlay goes next to them. Route-less LAN roles use
   `network=host`, `feature=<token>` and hard-code host networking in Compose.
2. Add the gateway relay worker for its `gateway_kind` (see
   `Martlet.Gateway.Ollama`, `Martlet.Gateway.Stt`, `Martlet.Gateway.F5`, `Martlet.Gateway.Xtts` or `Martlet.Gateway.Audio2Face`) and register the kind in
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
- **Listening** (speech-to-text) moves the same way between the Setup choice (OpenAI,
  Windows speech or whisper.cpp on this PC) and any paired host that runs `stt`, saved
  as a gateway speech-to-text route; the previous route is kept in
  `listening-previous.json`. Push-to-talk and hands-free utterances then go only to
  that host over its pinned TLS gateway and are transcribed there in memory.
- **Speaking** moves the same way between the Setup voice (OpenAI or Windows speech)
  and any paired host that runs the chosen voice engine (`f5` or `xtts`, chosen on
  Companion > Voice > Voice engine), saved as a gateway F5 route; the previous route
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
| `ollama` | Docker; an NVIDIA GPU (NVIDIA Container Toolkit) makes replies fast, otherwise the CPU; official `ollama/ollama:0.34.4`, model `gemma4:e2b`, `gemma4:e4b`, `qwen3-vl:8b`, `gemma4:12b` or `gemma4:26b` (suggested by GPU memory; these also see images, so the desktop can watch the screen with them, and call tools), the vision-only `gemma3:4b`, `qwen2.5vl:7b`, `gemma3:12b` or `gemma3:27b`, or the text-only `llama3.2:3b`, `qwen2.5:7b`, `llama3.1:8b` or `qwen2.5:14b`, kept in volume `martlet-ollama-models` and kept loaded | Thinking: the conversation model when the desktop hands thinking to this host (Devices > Who does what). Relay `Martlet.Gateway.Ollama` streams loopback `/api/chat` (persona, recent history, message and, for a screen glance, one image) with an 8,192-token context |
| `stt` | Docker; an NVIDIA GPU (NVIDIA Container Toolkit, driver 580+ for CUDA 13) makes it fast, otherwise the CPU; official `ghcr.io/ggml-org/whisper.cpp` release 1.9.4 (CPU or CUDA build), model `base`, `small`, `medium` or `large-v3-turbo` (suggested by GPU memory: `small` on the CPU, `large-v3-turbo` from 4 GB), downloaded on first start from Hugging Face at a pinned revision with its SHA-256 checked into volume `martlet-stt-models`; listens on 127.0.0.1:8178 | Listening: speech-to-text when the desktop hands listening to this host (Devices > the Listening row's *Done by*). Relay `Martlet.Gateway.Stt` sends each utterance (16 kHz mono, at most 30 s) to loopback `/inference` and returns its text without non-speech tags; audio stays in memory |
| `f5` | NVIDIA GPU (6 GB+) with the NVIDIA Container Toolkit (`gpu=required`); image built on the host from `workers/f5/host/Dockerfile` (`python:3.12.10`, hash-locked PyTorch 2.6.0 CUDA 12.4, `f5-tts` 1.1.22, `vocos` 0.1.0, the bounded `martlet_f5_worker` and its loopback front `martlet_f5_host.py` on 127.0.0.1:50080); `martlet-f5 provision` downloads and verifies the pinned `F5TTS_v1_Base` (CC-BY-NC-4.0) and Vocos (MIT) files into volume `martlet-f5-models`, `martlet-f5 warm` loads them | Speaking: replies in a voice cloned from your reference recording when the desktop hands speaking to this host (Devices > the Speaking row's *Done by*). Relay `Martlet.Gateway.F5` streams the worker's contiguous 24 kHz mono PCM16 frames and chunk completions; cancellation is discard-only |
| `xtts` | NVIDIA GPU (4 GB+) with the NVIDIA Container Toolkit (`gpu=required`); image built on the host from `workers/xtts/host/Dockerfile` (`python:3.12.10`, hash-locked PyTorch 2.6.0 CUDA 12.4, `coqui-tts` 0.27.5 from Idiap's maintained fork, its loopback service `martlet_xtts_host.py` on 127.0.0.1:50081); `martlet-xtts provision` downloads and verifies the pinned XTTS-v2 checkpoint, config, tokenizer and speaker files (Coqui Public Model License 1.0.0, non-commercial only) into volume `martlet-xtts-models`, `martlet-xtts warm` loads them; choices `XTTS_MODEL` (`xtts-v2`) and `XTTS_LANGUAGE` (`en` default) | Speaking with [XTTS-v2](../../docs/XTTS_VOICE.md): replies in a voice cloned from your reference recording, streamed while they are generated, when the desktop hands speaking to this host with the XTTS-v2 engine (Companion > Voice > Voice engine). Relay `Martlet.Gateway.Xtts` (the F5 relay on route `martlet.gateway.xtts-synthesis.v1`) streams contiguous 24 kHz mono PCM16 frames; a killed model process restarts for the next reply |
| `audio2face` | NVIDIA GPU (4 GB+; RTX 20 series or newer for the local engine) with the NVIDIA Container Toolkit (`gpu`, `docker`, `nvidia-toolkit`); models `claire`/`mark`/`james`. Engine choice `A2F_ENGINE`: **`local`** (default, no NVIDIA account or key): image built on the host from [`workers/audio2face`](../../workers/audio2face/README.md), NVIDIA's open-source Audio2Face-3D SDK (MIT) with CUDA 12.8 and TensorRT 10.9 behind Martlet's gRPC front; on first start it downloads the chosen model from Hugging Face at a pinned revision (SHA-256 checked, NVIDIA Open Model License) into volume `martlet-audio2face-models` and builds its TensorRT engine for that GPU there (driver 570+). **`nim`**: NVIDIA's NIM `nvcr.io/nim/nvidia/audio2face-3d:1.3` with your free NVIDIA account's [NGC API key](https://org.ngc.nvidia.com/setup/api-key) (development and testing use; NVIDIA lists that release as end of support). Both listen on 127.0.0.1:52000 | Automatic lip-sync uses it when the desktop hands lip-sync to this host (Devices > the Lip-sync row's *Done by*). Relay `Martlet.Gateway.Audio2Face` streams each speech chunk to the engine's `ProcessAudioStream` and returns ARKit blendshape frames |
| `home-assistant` | Linux Docker Engine (not Docker Desktop); official `ghcr.io/home-assistant/home-assistant:2026.9.4`; host networking on port 8123, privileged USB/Bluetooth access, `/run/dbus:/run/dbus:ro`, config in volume `martlet-home-assistant-config` | Smart home: Martlet installs and onboards Home Assistant through HA's own HTTP/WebSocket API at `http://<host LAN address>:8123`. It is a route-less `feature=home-assistant` role, not relayed through the Martlet gateway |

On a native host the `ollama` role listens on the host's own 127.0.0.1:11434, so
stop any Ollama already installed there first; `stt` uses 127.0.0.1:8178. Home
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
