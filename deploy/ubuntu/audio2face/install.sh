#!/usr/bin/env bash
# Martlet Audio2Face host installer for Ubuntu 24.04 x86_64 with an NVIDIA GPU.
#
# Sets up, with an explicit "yes" before every system change:
#   1. NVIDIA driver check, Docker Engine + Compose, NVIDIA Container Toolkit
#   2. NVIDIA Audio2Face-3D NIM (Docker Compose, loopback 127.0.0.1:52000 only)
#   3. Martlet gateway (pinned TLS on your LAN address) with its Audio2Face relay
#   4. Gateway identity + pairing with your Martlet desktop, then a boot-time user service
#
# Run from a Martlet source checkout as your normal user (not root):
#   ./deploy/ubuntu/audio2face/install.sh
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
SAMPLES_COMMIT="a2d0150043be7dc15db2fad8193a78b660e1100f"
SAMPLES_RAW="https://raw.githubusercontent.com/NVIDIA/Audio2Face-3D-Samples/${SAMPLES_COMMIT}/configs"
DOTNET_SDK="10.0.401"
A2F_DIR="${HOME}/.local/share/martlet/audio2face"
A2F_KEY_DIR="${HOME}/.config/martlet/audio2face"
GW_APP="${HOME}/.local/share/martlet/gateway/app"
GW_STATE_PARENT="${HOME}/.local/share/martlet/gateway/private"
GW_CONFIG_DIR="${HOME}/.config/martlet/gateway"
DOTNET="${HOME}/.dotnet/dotnet"

say()  { printf '\n\033[1m%s\033[0m\n' "$*"; }
fail() { printf '\n\033[31mStopped: %s\033[0m\n' "$*" >&2; exit 1; }
ask()  { local answer; read -r -p "$1 Type yes to continue: " answer; [[ "$answer" == "yes" ]]; }

[[ $EUID -ne 0 ]] || fail "Run as your normal user; the script asks before using sudo."
[[ "$(uname -m)" == "x86_64" ]] || fail "Only x86_64 is supported."
grep -q 'ID=ubuntu' /etc/os-release || fail "Only Ubuntu is supported by this installer."
[[ -f "${REPO}/src/Martlet.Gateway.Host.Linux/Martlet.Gateway.Host.Linux.csproj" ]] || fail "Run from a Martlet source checkout."

# ---------------------------------------------------------------- 1. prerequisites
say "1/6 NVIDIA driver"
if ! nvidia-smi >/dev/null 2>&1; then
  echo "No working NVIDIA driver was found (nvidia-smi failed)."
  if ask "Install the recommended Ubuntu NVIDIA driver with 'sudo ubuntu-drivers install'? A reboot is required afterwards."; then
    sudo ubuntu-drivers install
    fail "Driver installed. Reboot, then run this script again."
  fi
  fail "An NVIDIA GPU with a working driver is required for Audio2Face."
fi
nvidia-smi --query-gpu=name,memory.total --format=csv,noheader

say "2/6 Docker Engine, Compose and NVIDIA Container Toolkit"
if ! command -v docker >/dev/null 2>&1 || ! docker compose version >/dev/null 2>&1; then
  if ask "Install Ubuntu's Docker Engine and Compose (sudo apt-get install docker.io docker-compose-v2)?"; then
    sudo apt-get update && sudo apt-get install -y docker.io docker-compose-v2
    sudo systemctl enable --now docker
  else
    fail "Docker Engine with the compose plugin is required."
  fi
fi
DOCKER=(docker)
if ! docker info >/dev/null 2>&1; then
  if ask "Add ${USER} to the docker group so Martlet's services can run without sudo? (You must log out and back in once afterwards.)"; then
    sudo usermod -aG docker "${USER}"
  fi
  DOCKER=(sudo docker)
fi
if ! command -v nvidia-ctk >/dev/null 2>&1; then
  if ask "Install the NVIDIA Container Toolkit from NVIDIA's apt repository and configure Docker for GPUs?"; then
    curl -fsSL https://nvidia.github.io/libnvidia-container/gpgkey |
      sudo gpg --dearmor -o /usr/share/keyrings/nvidia-container-toolkit-keyring.gpg
    curl -fsSL https://nvidia.github.io/libnvidia-container/stable/deb/nvidia-container-toolkit.list |
      sed 's#deb https://#deb [signed-by=/usr/share/keyrings/nvidia-container-toolkit-keyring.gpg] https://#g' |
      sudo tee /etc/apt/sources.list.d/nvidia-container-toolkit.list >/dev/null
    sudo apt-get update && sudo apt-get install -y nvidia-container-toolkit
    sudo nvidia-ctk runtime configure --runtime=docker
    sudo systemctl restart docker
  else
    fail "The NVIDIA Container Toolkit is required for GPU containers."
  fi
fi

# ---------------------------------------------------------------- 2. Audio2Face NIM
say "3/6 NVIDIA Audio2Face-3D service"
echo "Audio2Face runs as NVIDIA's NIM container from nvcr.io. You need a free NVIDIA account and an NGC API key"
echo "(https://org.ngc.nvidia.com/setup/api-key). Using the NIM and its models means accepting NVIDIA's terms."
mkdir -p "${A2F_DIR}/configs" "${A2F_DIR}/a2f-3d-init-data" "${A2F_KEY_DIR}"
chmod 700 "${A2F_KEY_DIR}"
KEY_FILE="${A2F_KEY_DIR}/ngc_api_key"
if [[ ! -s "${KEY_FILE}" ]]; then
  read -r -s -p "Paste your NGC API key (input hidden): " NGC_KEY; echo
  [[ -n "${NGC_KEY}" ]] || fail "An NGC API key is required."
  ( umask 077; printf '%s' "${NGC_KEY}" > "${KEY_FILE}" )
  unset NGC_KEY
fi
"${DOCKER[@]}" login nvcr.io --username '$oauthtoken' --password-stdin < "${KEY_FILE}"

MODEL="${A2F_MODEL:-}"
if [[ -z "${MODEL}" ]]; then
  read -r -p "Audio2Face face model [claire/mark/james] (default claire): " MODEL
  MODEL="${MODEL:-claire}"
fi
[[ "${MODEL}" =~ ^(claire|mark|james)$ ]] || fail "Choose claire, mark or james."

for file in advanced_config.yaml deployment_config.yaml "${MODEL}_stylization_config.yaml"; do
  curl -fsSL "${SAMPLES_RAW}/${file}" -o "${A2F_DIR}/configs/${file}"
done
# Listen only on loopback; the Martlet gateway is the sole LAN entry point.
sed -i 's#0\.0\.0\.0:#127.0.0.1:#g' "${A2F_DIR}/configs/deployment_config.yaml"
grep -q 'url: 127.0.0.1:52000' "${A2F_DIR}/configs/deployment_config.yaml" || fail "Could not restrict Audio2Face to loopback."
cp "${REPO}/deploy/ubuntu/audio2face/compose.yaml" "${A2F_DIR}/compose.yaml"
(
  cd "${A2F_DIR}"
  export A2F_3D_MODEL_NAME="${MODEL}" NGC_API_KEY_FILE="${KEY_FILE}"
  "${DOCKER[@]}" compose up -d
)
echo "Waiting for Audio2Face on 127.0.0.1:52000 (the first start builds TensorRT engines and can take 10+ minutes)..."
for _ in $(seq 1 180); do
  if (exec 3<>/dev/tcp/127.0.0.1/52000) 2>/dev/null; then READY=1; break; fi
  sleep 10
done
[[ "${READY:-0}" == 1 ]] || fail "Audio2Face did not start. Inspect: (cd ${A2F_DIR} && ${DOCKER[*]} compose logs)"
echo "Audio2Face is listening on 127.0.0.1:52000."

# ---------------------------------------------------------------- 3. gateway
say "4/6 Martlet gateway"
if [[ ! -x "${DOTNET}" ]] || ! "${DOTNET}" --list-sdks | grep -q "^${DOTNET_SDK} "; then
  if ask "Install the .NET SDK ${DOTNET_SDK} into ~/.dotnet with Microsoft's dotnet-install script (no sudo)?"; then
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
    bash /tmp/dotnet-install.sh --version "${DOTNET_SDK}" --install-dir "${HOME}/.dotnet"
  else
    fail "The .NET SDK is required to build the gateway."
  fi
fi
export DOTNET_ROOT="${HOME}/.dotnet" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
"${DOTNET}" publish "${REPO}/src/Martlet.Gateway.Host.Linux/Martlet.Gateway.Host.Linux.csproj" -c Release -o "${GW_APP}" -p:UseAppHost=false

mapfile -t ADDRESSES < <(ip -4 -o addr show scope global | awk '{print $4}' | cut -d/ -f1 |
  grep -E '^(10\.|192\.168\.|172\.(1[6-9]|2[0-9]|3[01])\.)' || true)
[[ ${#ADDRESSES[@]} -gt 0 ]] || fail "No private LAN IPv4 address found on this host."
echo "Private LAN addresses on this host:"; printf '  %s\n' "${ADDRESSES[@]}"
read -r -p "Address your desktop will use (default ${ADDRESSES[0]}): " LAN_IP
LAN_IP="${LAN_IP:-${ADDRESSES[0]}}"
PORT="${GATEWAY_PORT:-9443}"
HOST_ID="${GATEWAY_HOST_ID:-$(hostname -s | tr -cd 'A-Za-z0-9._-' | cut -c1-40)-a2f}"

mkdir -p "${GW_CONFIG_DIR}" "${GW_STATE_PARENT}"
chmod 700 "${HOME}/.config/martlet" "${GW_CONFIG_DIR}" "${HOME}/.local/share/martlet/gateway" "${GW_STATE_PARENT}"
CONFIG="${GW_CONFIG_DIR}/host.json"
( umask 077; cat > "${CONFIG}" <<JSON
{"schemaVersion":1,"hostId":"${HOST_ID}","stateDirectory":"${GW_STATE_PARENT}/state","storageBackend":"linuxServicePermissions","binding":{"mode":"privateIp","origin":"https://${LAN_IP}:${PORT}"},"serviceUid":$(id -u),"serviceGid":$(id -g),"audio2face":{"endpoint":"http://127.0.0.1:52000/","model":"${MODEL}"}}
JSON
)
GATEWAY=("${DOTNET}" "${GW_APP}/Martlet.Gateway.Host.Linux.dll")
"${GATEWAY[@]}" validate --config "${CONFIG}"

if command -v ufw >/dev/null 2>&1 && sudo ufw status 2>/dev/null | grep -q 'Status: active'; then
  if ask "ufw is active. Allow TCP ${PORT} from your private LAN to the gateway?"; then
    sudo ufw allow proto tcp from "${LAN_IP%.*}.0/24" to any port "${PORT}"
  fi
fi

# ---------------------------------------------------------------- 4. identity and pairing
say "5/6 Gateway identity and pairing"
cat <<TEXT
The gateway administration console opens next. In Martlet on your desktop, open Character settings >
"Audio2Face on another computer" and note "This PC's device ID". Then in the console:
  1. Confirm creation with: yes
  2. start            (confirm: yes)
  3. pair             enter the desktop's device ID, any display name, role: voice (confirm: yes)
                      Copy the shown host address, host ID, fingerprint, pairing ID and token into
                      Martlet and press "Pair with host" while this console stays open.
  4. list             confirms the pairing
  5. approve-service  (confirm: yes) lets the gateway start unattended at boot
  6. stop             (confirm: yes)
TEXT
if [[ -d "${GW_STATE_PARENT}/state" ]]; then
  "${GATEWAY[@]}" admin --config "${CONFIG}"
else
  "${GATEWAY[@]}" init --config "${CONFIG}"
fi

# ---------------------------------------------------------------- 5. service
say "6/6 Boot-time service"
UNIT_DIR="${HOME}/.config/systemd/user"
mkdir -p "${UNIT_DIR}"
cat > "${UNIT_DIR}/martlet-gateway.service" <<UNIT
[Unit]
Description=Martlet gateway (Audio2Face relay)
After=network-online.target

[Service]
Environment=DOTNET_ROOT=${HOME}/.dotnet
ExecStart=${DOTNET} ${GW_APP}/Martlet.Gateway.Host.Linux.dll serve --config ${CONFIG}
Restart=on-failure
RestartSec=10

[Install]
WantedBy=default.target
UNIT
systemctl --user daemon-reload
systemctl --user enable --now martlet-gateway.service
if ask "Keep the gateway running after reboot even when nobody is logged in (sudo loginctl enable-linger ${USER})?"; then
  sudo loginctl enable-linger "${USER}"
fi
sleep 3
"${GATEWAY[@]}" health --config "${CONFIG}" && say "Done. Martlet on your desktop will use this host's Audio2Face automatically." ||
  fail "The gateway is not healthy yet. Check: journalctl --user -u martlet-gateway"
