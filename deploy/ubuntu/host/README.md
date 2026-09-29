# Martlet host: one install flow for every role

Extra machines (a GPU PC or server on your network) run Martlet **roles** that the
desktop uses. Every role is installed the same way by one tool, `martlet-host`;
nothing about a role lives in the tool itself.

```text
Windows desktop (Martlet) --pinned TLS, paired once--> Ubuntu host: Martlet gateway :9443
                                                         | one gateway route per installed role
                                                         v
                                                       role services on 127.0.0.1 only
                                                       (Audio2Face today; more roles plug in the same way)
```

## Install a host

On Ubuntu 24.04 x86_64, as your normal user, from a Martlet source checkout:

```sh
git clone https://github.com/throndir2/Martlet && cd Martlet
./deploy/ubuntu/host/martlet-host setup     # gateway, identity, boot-time service (once per machine)
martlet-host pair                           # pair a Martlet desktop (repeat per desktop)
martlet-host roles                          # what this host can run
martlet-host add audio2face                 # install a role; same command for every role
```

`setup` installs the .NET SDK into `~/.dotnet` (no sudo), publishes the
[Linux gateway](../../../src/Martlet.Gateway.Host.Linux/README.md), asks which
private LAN address and port desktops use, creates the host identity, installs a
systemd user service (`martlet-host-gateway`), optionally enables lingering so it
runs at boot without a login, and links `martlet-host` into `~/.local/bin`.

Every system change asks for an explicit `yes`. Gateway approvals happen in the
gateway's own local console, as the host security model requires; the tool
prints exactly what to type.

## The uniform role flow

`martlet-host add <role>` always runs these steps, driven only by
`roles/<role>/role.conf` and `roles/<role>/compose.yaml`:

| Step | `role.conf` key | What happens |
| --- | --- | --- |
| Requirements | `requires` | Shared checks/installs: `gpu` (NVIDIA driver), `docker` (Engine + Compose), `nvidia-toolkit` |
| Terms | `terms` | Shown; continue only on `yes` |
| Secrets | `secret=name\|prompt` | Asked once, stored in `~/.config/martlet/host/secrets/<name>` (0600), passed to Compose as `<NAME>_FILE` |
| Choices | `choice=VAR\|label\|options\|default` | Asked each time, written to the role's `.env` |
| Registry | `registry=host\|user\|secret` | `docker login` with the stored secret |
| Assets | `asset=url\|path` | Pinned HTTPS downloads into the role directory (`{VAR}` uses a choice) |
| Loopback | `rewrite=path\|sed`, `expect=path\|text` | Rewrite configs to 127.0.0.1 and verify it |
| Service | `compose.yaml` | `docker compose up -d` in `~/.local/share/martlet/host/roles/<role>` |
| Readiness | `port`, `ready_timeout_minutes` | Wait until 127.0.0.1:`port` accepts connections |
| Publish | `gateway_kind`, `model_from` | Add `{kind, endpoint, model}` to the gateway's `host.json` `roles` list; renew the service approval; restart |

`martlet-host remove <role>` stops the service (keeping its data) and unpublishes it.
`martlet-host status` shows the gateway and each role. `martlet-host config`
prints the generated `host.json`.

## Adding a new role

1. Add `roles/<name>/role.conf` and `roles/<name>/compose.yaml` (services bound to
   127.0.0.1 or host networking with loopback configs).
2. Add the gateway relay worker for its `gateway_kind` (see
   `Martlet.Gateway.Audio2Face`) and register the kind in
   `Martlet.Gateway.Host.Linux` (`HostConfiguration.RoleKinds`, `NativeHostPlatform.RoleWorker`).
3. Teach the desktop to use that route when a paired host advertises it.

No new install script. Roles without a gateway relay worker yet (Ollama LLM, F5
voice, screen understanding, speech-to-text) are not listed.

## Roles

| Role | Needs | Desktop use |
| --- | --- | --- |
| `audio2face` | NVIDIA GPU (4 GB+), free NVIDIA account with an [NGC API key](https://org.ngc.nvidia.com/setup/api-key); NIM `nvcr.io/nim/nvidia/audio2face-3d:1.3`, models `claire`/`mark`/`james` | Character settings > **Audio2Face on another computer**; Automatic lip-sync uses it when this PC has no local Audio2Face |

## Status

The flow was exercised locally in a Linux container with stubbed system commands
(add, idempotent re-add, remove, unknown role), and the generated `host.json` is
covered by gateway parser tests. It has **not** yet been run on a real Ubuntu GPU
host. Windows machines as hosts are not supported (the Windows gateway accepts
local connections only).
