# Audio2Face on a separate Ubuntu host

Run NVIDIA Audio2Face-3D on another computer with an NVIDIA GPU and let Martlet on
your Windows desktop use it for lip-sync. This is the Audio2Face role of Martlet's
host setup: the host runs the NIM container on loopback only, and the existing
Martlet gateway (pinned TLS, one-use pairing, signed requests) relays Martlet's
generated voice to it. Nothing else on the host is reachable from the LAN.

```text
Windows desktop (Martlet)  --HTTPS, pinned, paired-->  Ubuntu host: Martlet gateway :9443
                                                          |  Audio2Face relay route
                                                          v
                                                        Audio2Face-3D NIM 127.0.0.1:52000 (GPU)
```

## Requirements

- Ubuntu 24.04 x86_64 with an NVIDIA GPU (4 GB+ video memory) and a working driver
  (the installer offers `ubuntu-drivers install`).
- A free NVIDIA account and an NGC API key: <https://org.ngc.nvidia.com/setup/api-key>.
  Pulling and using the NIM and its models means accepting NVIDIA's terms.
- Both computers on the same private network; the desktop reaches the host's
  private IP on TCP 9443.
- A Martlet source checkout on the host (`git clone https://github.com/throndir2/Martlet`).

## Install

```sh
cd Martlet
./deploy/ubuntu/audio2face/install.sh
```

The script asks `yes` before every system change and then:

1. checks the NVIDIA driver; installs Docker Engine + Compose (`docker.io`,
   `docker-compose-v2`) and the NVIDIA Container Toolkit if missing;
2. stores your NGC key in `~/.config/martlet/audio2face/ngc_api_key` (0600, plaintext),
   logs Docker in to `nvcr.io`, downloads NVIDIA's pinned sample configs, rewrites
   them to listen on 127.0.0.1 only, and starts [compose.yaml](compose.yaml)
   (`nvcr.io/nim/nvidia/audio2face-3d:1.3`, model `claire`, `mark` or `james`).
   The first start builds TensorRT engines and can take 10+ minutes;
3. installs the .NET SDK 10.0.401 into `~/.dotnet` (Microsoft's `dotnet-install.sh`,
   no sudo) and publishes the [Linux gateway](../../../src/Martlet.Gateway.Host.Linux/README.md)
   with an `audio2face` relay section in `~/.config/martlet/gateway/host.json`;
4. opens the gateway console for `init`, `start`, `pair`, `approve-service`, `stop`;
5. installs a systemd user service (`martlet-gateway`) and optionally enables
   lingering so it starts at boot without a login.

## Pair the desktop

In Martlet: **Character settings > Audio2Face on another computer**. Use the shown
device ID when the host console's `pair` asks for one, then copy the host address,
host ID, fingerprint, pairing ID and token into the form and press **Pair with host**
while the console is still open. The device secret is saved in Windows Credential
Manager; the pairing is permanent until you **Forget host** (and `revoke` on the host).

With lip-sync set to **Automatic** (the default), each sentence Martlet speaks uses
a local Audio2Face service if one runs on the desktop, otherwise the paired host,
otherwise voice loudness. The desktop sends the sentence in short chunks (0.5 s,
then 1 s, each with 0.5 s of context) so animation starts while Martlet is still
speaking; frames that arrive too late are skipped and loudness fills in.

## Operate

| Task | Command on the host |
| --- | --- |
| Gateway status / logs | `systemctl --user status martlet-gateway`, `journalctl --user -u martlet-gateway` |
| Audio2Face logs | `cd ~/.local/share/martlet/audio2face && docker compose logs -f` |
| Stop / start Audio2Face | `docker compose stop` / `docker compose up -d` in that directory |
| Pair another desktop or revoke | `systemctl --user stop martlet-gateway`, then `dotnet ~/.local/share/martlet/gateway/app/Martlet.Gateway.Host.Linux.dll admin --config ~/.config/martlet/gateway/host.json` |

Changing `host.json` invalidates the service approval: run `admin` and
`approve-service` again. The gateway state under `~/.local/share/martlet/gateway/private`
holds the host identity and pairings (plaintext at rest, owner-only permissions).

## Status

The relay route, pairing and signed streaming are covered by local tests against a
controlled Audio2Face gRPC fixture, and the desktop path by integration tests. This
installer and a real NIM on a GPU have **not** been run on an Ubuntu machine by the
project yet; please report issues. Windows hosts are not supported by this script
(the Windows gateway is loopback-only); Docker Desktop + WSL2 could run the same
compose file, but the LAN gateway is Linux-only.
