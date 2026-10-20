# shellcheck shell=bash
# martlet-host module (deploy/host/lib/requirements.sh), sourced by deploy/host/martlet-host; it is not run by itself.
# What a change needs on this computer: Docker, packages, the CPU architecture, the
# NVIDIA GPU and its container toolkit, a role's GPU or CPU place, the .NET SDK and the gateway build.

# ------------------------------------------------------------------ shared requirements
DOCKER=(docker)
DOCKER_ENGINE_FAILURE="Home Assistant needs Docker Engine on a Linux computer; Docker Desktop (Windows or macOS) can't put it on your home network. Install it on a Linux host, or connect an existing Home Assistant in Martlet's Smart home page."

docker_engine_supported() {
  local info os operating
  info="$("${DOCKER[@]}" info --format '{{.OSType}}|{{.OperatingSystem}}' 2>/dev/null || true)"
  [[ -n "${info}" ]] || return 1
  os="${info%%|*}"; operating="${info#*|}"
  [[ "${os}" == linux && "${operating,,}" != *"docker desktop"* ]]
}

require_docker_engine() {
  require_docker
  docker_engine_supported || fail "${DOCKER_ENGINE_FAILURE}"
}

require() {
  local item
  for item in "$@"; do
    case "${item}" in
      gpu) require_gpu ;;
      docker) require_docker ;;
      docker-engine) require_docker_engine ;;
      nvidia-toolkit) require_nvidia_toolkit ;;
      x86_64) require_x86_64 ;;
      *) fail "Unknown requirement '${item}' in role.conf." ;;
    esac
  done
}

# The processor the roles' containers run on: x64 or arm64 (Docker's engine when it answers, else this computer).
HOST_ARCH=""
host_arch() {
  if [[ -z "${HOST_ARCH}" ]]; then
    local arch=""
    # Docker method: the Docker engine's machine (this container may be emulated); natively, this computer.
    [[ "${MODE}" == docker ]] && arch="$(docker info --format '{{.Architecture}}' 2>/dev/null </dev/null || true)"
    [[ -n "${arch}" ]] || arch="$(uname -m)"
    case "${arch}" in x86_64|amd64) HOST_ARCH=x64 ;; aarch64|arm64) HOST_ARCH=arm64 ;; *) HOST_ARCH="${arch}" ;; esac
  fi
  printf '%s' "${HOST_ARCH}"
}

X86_64_FAILURE="This role's container is built only for 64-bit Intel or AMD (x86_64) computers (its pinned NVIDIA CUDA packages have no ARM64 build); this host is ARM64. Install it on an x86_64 host with an NVIDIA GPU."

# role.conf requires=x86_64: the role's image pins x86_64-only packages (the NVIDIA CUDA roles).
require_x86_64() {
  [[ "$(host_arch)" != arm64 ]] || fail "${X86_64_FAILURE}"
}

# role.conf arm64=<overlay>: on ARM64 hosts the role (or the variant whose section names it) runs that Compose overlay
# instead, a CPU build for ARM64 (for example whisper.cpp's official arm64 image), so its GPU option is not offered there.
arm64_overlay() {
  local overlay
  [[ "$(host_arch)" == arm64 ]] || return 0
  overlay="$(conf_value "$1" arm64)"
  [[ -z "${overlay}" ]] && return 0
  [[ "${overlay}" =~ ^[a-z0-9.-]+\.yaml$ && -f "$(dirname "$1")/${overlay}" ]] || return 1
  printf '%s' "${overlay}"
}

require_gpu() {
  if [[ "${MODE}" == docker ]]; then
    # The Docker host provides the GPU; verify it is visible to containers.
    docker info --format '{{json .Runtimes}}' | grep -q nvidia ||
      docker info 2>/dev/null | grep -qi 'nvidia' ||
      note "Could not confirm an NVIDIA runtime on this Docker host (Docker Desktop reports GPUs per container). Continuing; the role fails to start without a GPU."
    return
  fi
  if nvidia-smi >/dev/null 2>&1; then
    nvidia-smi --query-gpu=name,memory.total --format=csv,noheader
    return
  fi
  note "No working NVIDIA driver was found (nvidia-smi failed)."
  if [[ " $(. /etc/os-release 2>/dev/null; printf '%s %s' "${ID:-}" "${ID_LIKE:-}") " == *" ubuntu "* ]] &&
     ask "Install the recommended NVIDIA driver with 'sudo ubuntu-drivers install'? A reboot is required afterwards."; then
    require_packages "the NVIDIA driver" ubuntu-drivers:ubuntu-drivers-common
    sudo ubuntu-drivers install
    fail "Driver installed. Reboot, then run the same martlet-host command again."
  fi
  fail "This role needs an NVIDIA GPU with a working driver. Install your distribution's NVIDIA driver, reboot, then try again."
}

# The system package manager (any distribution): apt-get, dnf, yum, zypper or pacman; empty when there is none of them.
package_manager() {
  local tool
  for tool in apt-get dnf yum zypper pacman; do
    command -v "${tool}" >/dev/null 2>&1 && { printf '%s' "${tool}"; return 0; }
  done
  return 0
}

# Docker Engine with the compose plugin: the distribution's own packages where they include compose v2, otherwise
# Docker's install script (https://get.docker.com: Debian, Fedora, CentOS, RHEL and others).
install_docker() {
  local pm script
  pm="$(package_manager)"
  case "${pm}" in
    apt-get)
      sudo apt-get update
      if apt-cache show docker-compose-v2 >/dev/null 2>&1; then sudo apt-get install -y docker.io docker-compose-v2; return; fi ;;
    pacman) sudo pacman -Sy --noconfirm --needed docker docker-compose; return ;;
    zypper) sudo zypper --non-interactive install docker docker-compose; return ;;
  esac
  require_curl "Docker's install script"
  script="$(mktemp)"
  curl -fsSL https://get.docker.com -o "${script}" || { rm -f "${script}"; fail "Could not download Docker's install script."; }
  sudo sh "${script}" || { rm -f "${script}"; fail "Docker's install script failed; install Docker Engine with compose, then try again."; }
  rm -f "${script}"
}

require_docker() {
  if [[ "${MODE}" == docker ]]; then
    docker info >/dev/null 2>&1 || fail "The Docker socket is not available to this session."
    return
  fi
  if ! command -v docker >/dev/null 2>&1 || ! docker compose version >/dev/null 2>&1; then
    ask "Install Docker Engine and Compose with the system package manager (or Docker's install script)?" ||
      fail "Docker Engine with the compose plugin is required."
    install_docker
    sudo systemctl enable --now docker
  fi
  if docker info >/dev/null 2>&1; then DOCKER=(docker); return; fi
  if ! id -nG "${USER}" | grep -qw docker &&
     ask "Add ${USER} to the docker group so role services can be managed without sudo (takes effect after logging in again)?"; then
    sudo usermod -aG docker "${USER}"
  fi
  DOCKER=(sudo docker)
}

# A minimal system (Ubuntu Server "minimized", a bare-metal install of any distribution) can lack tools the engine
# uses. Each argument is <command or absolute path>:<package>; only the packages whose command or file is missing are
# installed with the system package manager, after one yes.
require_packages() {
  local reason="$1" entry check missing=()
  shift
  for entry in "$@"; do
    check="${entry%%:*}"
    if [[ "${check}" == /* ]]; then [[ -e "${check}" ]] && continue
    else command -v "${check}" >/dev/null 2>&1 && continue; fi
    [[ " ${missing[*]:-} " == *" ${entry#*:} "* ]] || missing+=("${entry#*:}")
  done
  [[ ${#missing[@]} -eq 0 ]] && return
  local pm; pm="$(package_manager)"
  [[ -n "${pm}" ]] || fail "${missing[*]} is required for ${reason}; install it, then try again."
  ask "Install missing packages for ${reason} (sudo ${pm} install ${missing[*]})?" ||
    fail "${missing[*]} is required for ${reason}."
  install_packages "${missing[@]}" || fail "Could not install ${missing[*]}."
}

install_packages() {
  case "$(package_manager)" in
    apt-get) sudo apt-get update && sudo apt-get install -y "$@" ;;
    dnf) sudo dnf install -y "$@" ;;
    yum) sudo yum install -y "$@" ;;
    zypper) sudo zypper --non-interactive install "$@" ;;
    pacman) sudo pacman -Sy --noconfirm --needed "$@" ;;
    *) return 1 ;;
  esac
}

# The CA bundle where this distribution keeps it (Debian/Ubuntu/Arch, Fedora/RHEL, openSUSE).
CA_BUNDLE=/etc/ssl/certs/ca-certificates.crt
for _bundle in /etc/pki/tls/certs/ca-bundle.crt /etc/ssl/ca-bundle.pem; do
  [[ -e "${CA_BUNDLE}" || ! -e "${_bundle}" ]] || CA_BUNDLE="${_bundle}"
done
require_curl() { require_packages "${1:-downloads}" curl:curl "${CA_BUNDLE}:ca-certificates"; }

require_nvidia_toolkit() {
  [[ "${MODE}" == docker ]] && return
  command -v nvidia-ctk >/dev/null 2>&1 && return
  ask "Install the NVIDIA Container Toolkit from NVIDIA's package repository and configure Docker for GPUs?" ||
    fail "The NVIDIA Container Toolkit is required for GPU containers."
  local rpm=https://nvidia.github.io/libnvidia-container/stable/rpm/nvidia-container-toolkit.repo
  require_curl "the NVIDIA Container Toolkit"
  case "$(package_manager)" in
    apt-get)
      require_packages "the NVIDIA Container Toolkit" gpg:gnupg
      curl -fsSL https://nvidia.github.io/libnvidia-container/gpgkey |
        sudo gpg --dearmor --yes -o /usr/share/keyrings/nvidia-container-toolkit-keyring.gpg
      curl -fsSL https://nvidia.github.io/libnvidia-container/stable/deb/nvidia-container-toolkit.list |
        sed 's#deb https://#deb [signed-by=/usr/share/keyrings/nvidia-container-toolkit-keyring.gpg] https://#g' |
        sudo tee /etc/apt/sources.list.d/nvidia-container-toolkit.list >/dev/null
      sudo apt-get update && sudo apt-get install -y nvidia-container-toolkit ;;
    dnf|yum)
      curl -fsSL "${rpm}" | sudo tee /etc/yum.repos.d/nvidia-container-toolkit.repo >/dev/null
      sudo "$(package_manager)" install -y nvidia-container-toolkit ;;
    zypper)
      sudo zypper --non-interactive ar -f "${rpm}" nvidia-container-toolkit 2>/dev/null || true
      sudo zypper --non-interactive --gpg-auto-import-keys install nvidia-container-toolkit ;;
    pacman) sudo pacman -Sy --noconfirm --needed nvidia-container-toolkit ;;
    *) fail "Install the NVIDIA Container Toolkit (https://docs.nvidia.com/datacenter/cloud-native/container-toolkit/), then try again." ;;
  esac
  sudo nvidia-ctk runtime configure --runtime=docker
  sudo systemctl restart docker
}

# Largest NVIDIA GPU memory (MiB) that role containers can use here, or 0. Natively a working driver is enough (the
# NVIDIA Container Toolkit is installed on demand); with Docker it asks the Docker host through a GPU container.
# Each card is also listed (NVIDIA_IDS: UUIDs in nvidia-smi order, with NVIDIA_NAMES, NVIDIA_MBS and NVIDIA_USED in MiB),
# so a host with several cards can put each role on its own card.
NVIDIA_MB=0
NVIDIA_IDS=(); NVIDIA_NAMES=(); NVIDIA_MBS=(); NVIDIA_USED=()
detect_nvidia() {
  local query=(--query-gpu=uuid,name,memory.total,memory.used --format=csv,noheader,nounits) lines=() line uuid name total used
  if [[ "${MODE}" == docker ]]; then
    mapfile -t lines < <(timeout 120 docker run --rm --gpus all --entrypoint nvidia-smi "${IMAGE}" "${query[@]}" 2>/dev/null || true)
  elif command -v nvidia-smi >/dev/null 2>&1; then
    mapfile -t lines < <(timeout 30 nvidia-smi "${query[@]}" 2>/dev/null || true)
  fi
  NVIDIA_MB=0; NVIDIA_IDS=(); NVIDIA_NAMES=(); NVIDIA_MBS=(); NVIDIA_USED=()
  for line in "${lines[@]}"; do
    IFS=',' read -r uuid name total used <<< "${line}"
    uuid="${uuid// /}"; name="$(printf '%s' "${name}" | sed 's/^ *//; s/ *$//; s/[|=]//g')"
    total="${total//[^0-9.]/}"; total="${total%%.*}"; used="${used//[^0-9.]/}"; used="${used%%.*}"
    [[ "${uuid}" =~ ^GPU-[A-Za-z0-9-]+$ && "${total}" =~ ^[0-9]+$ ]] || continue
    NVIDIA_IDS+=("${uuid}"); NVIDIA_NAMES+=("${name:-NVIDIA GPU}"); NVIDIA_MBS+=("${total}")
    [[ "${used}" =~ ^[0-9]+$ ]] || used=0
    NVIDIA_USED+=("${used}")
    if (( total > NVIDIA_MB )); then NVIDIA_MB=${total}; fi
  done
}

# Whether $1 is one card's UUID or a list of them ("GPU-a,GPU-b": one model split across those cards, tensor split).
gpu_list() { [[ "$1" =~ ^GPU-[A-Za-z0-9-]+(,GPU-[A-Za-z0-9-]+){0,7}$ ]]; }

# Whether every card in list $1 is on this host (detect_nvidia first), each once.
known_gpus() {
  local id i seen="," found
  gpu_list "$1" || return 1
  for id in ${1//,/ }; do
    [[ "${seen}" != *",${id},"* ]] || return 1
    seen+="${id},"; found=0
    for i in "${!NVIDIA_IDS[@]}"; do [[ "${NVIDIA_IDS[i]}" == "${id}" ]] && found=1; done
    (( found )) || return 1
  done
}

# The graphics card (UUID) an installed role was pinned to, a comma-separated list of cards for a model split across them, or
# nothing when it uses every card (or the CPU).
role_gpu() {
  local project="${PROJECTS}/$1"
  [[ -f "${project}/compose.gpu.yaml" ]] || return 0
  conf_value "${project}/.env" MARTLET_GPU 2>/dev/null || true
}

# Where installed role $1 runs: the card it was pinned to (its UUID), "cpu" when it was added to run on the processor, "all"
# when it runs on every card, or nothing when this host doesn't say (the gateway then counts the whole host).
role_place() {
  local project="${PROJECTS}/$1" gpu accelerator
  gpu="$(role_gpu "$1")"
  if gpu_list "${gpu}"; then printf '%s' "${gpu}"; return 0; fi
  accelerator="$(conf_value "${project}/.env" MARTLET_ACCELERATOR 2>/dev/null || true)"
  if [[ "${accelerator}" == cpu ]]; then printf 'cpu'
  elif [[ -f "${project}/compose.gpu.yaml" ]]; then printf 'all'; fi
  return 0
}

# Whether role $1 can use a graphics card at all: role.conf requires a GPU or offers a GPU overlay for what it runs now. An
# overlay inside a variant counts only while that variant runs (Reading's ppocrv5 engine, not rapidocr; stt's whisper, not
# parakeet); Home Assistant never does.
role_uses_gpu() {
  local conf="${ROLES_DIR}/$1/role.conf" file="${PROJECTS}/$1/.env" condition line from legacy when
  [[ -f "${conf}" ]] || return 0
  [[ " $(conf_value "${conf}" requires) " == *" gpu "* ]] && return 0
  from="$(conf_value "${conf}" profile_from)"; legacy="$(conf_value "${conf}" profile_legacy)"
  while IFS=$'\t' read -r condition line; do
    [[ "${line%%=*}" == gpu ]] || continue
    [[ "${condition}" == "*" || ! -f "${file}" ]] && return 0
    when="$(conf_value "${file}" "${condition%%=*}")"
    if [[ -z "${when}" && "${condition%%=*}" == "${from}" ]]; then when="${legacy}"; fi
    [[ "${when}" == "${condition#*=}" ]] && return 0
  done < <(conf_lines "${conf}")
  return 1
}

# For martlet-host status: the gateway gives live turns first claim on a graphics card and stops Deep thinking's work on a card
# that a live role uses. Says which roles each Thinking pool role (deep-thinking, and deep-thinking-2... on extra cards) shares
# a card with, and how to give it one of its own.
gpu_sharing_note() {
  local pool deep other place shared said=0
  for pool in deep-thinking deep-thinking-2 deep-thinking-3 deep-thinking-4; do
    [[ -f "$(installed_file "${pool}")" ]] || continue
    shared=()
    deep="$(role_place "${pool}")"
    while IFS= read -r other; do
      # The Thinking pool roles are all pool work: they never stop each other.
      [[ -n "${other}" && ! "${other}" =~ ^deep-thinking(-[0-9])?$ ]] || continue
      role_uses_gpu "${other}" || continue
      place="$(role_place "${other}")"
      [[ "${place}" != cpu && "${deep}" != cpu ]] || continue
      if [[ -z "${deep}" || "${deep}" == all || -z "${place}" || "${place}" == all || "${place}" == "${deep}" ]]; then shared+=("${other}"); fi
    done < <(installed_roles)
    (( ${#shared[@]} > 0 )) || continue
    (( said )) || say "Graphics card sharing"
    said=1
    note "${pool} shares a graphics card with ${shared[*]}. Live turns go first there: a live request or a desktop's hold stops its thinks, and Martlet runs them on another computer or later."
  done
  (( said )) || return 0
  note "On a host with two or more NVIDIA cards, pin each Ollama server to its own GPU (CUDA_VISIBLE_DEVICES): change deep-thinking in Martlet and choose a card that no live role uses, and add deep-thinking-2 (one Thinking pool model per card) for each further card."
}

# What installed role $1 runs with now, from its .env: "choice|<VAR>|<value>" for each role.conf choice that holds one of
# its options (a variant's own choice only when that variant runs; installs from before the role had variants run its
# profile_legacy), and "accelerator|gpu" or "accelerator|cpu" for a role with a gpu entry. Nothing when it isn't installed.
role_current() {
  local role="$1" file="${PROJECTS}/$1/.env" conf condition line var label options default value from legacy when open
  [[ -f "$(installed_file "${role}")" && -f "${file}" ]] || return 0
  conf="$(role_dir "${role}")/role.conf"
  from="$(conf_value "${conf}" profile_from)"; legacy="$(conf_value "${conf}" profile_legacy)"
  open=" $(conf_values "${conf}" choice_open | tr '\n' ' ') "
  while IFS=$'\t' read -r condition line; do
    [[ "${line%%=*}" == choice ]] || continue
    IFS='|' read -r var label options default <<< "${line#*=}"
    [[ "${var}" =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]] || continue
    if [[ "${condition}" != "*" ]]; then
      when="$(conf_value "${file}" "${condition%%=*}")"
      if [[ -z "${when}" && "${condition%%=*}" == "${from}" ]]; then when="${legacy}"; fi
      [[ "${when}" == "${condition#*=}" ]] || continue
    fi
    value="$(conf_value "${file}" "${var}")"
    if [[ -z "${value}" && -n "${from}" && "${var}" == "${from}" ]]; then value="${legacy}"; fi
    if [[ -n "${value}" ]] && { [[ " ${options} " == *" ${value} "* ]] || { [[ "${open}" == *" ${var} "* ]] && open_value "${value}"; }; }; then
      printf 'choice|%s|%s\n' "${var}" "${value}"
    fi
  done < <(conf_lines "${conf}")
  value="$(conf_value "${file}" MARTLET_ACCELERATOR)"
  if [[ "${value}" == gpu || "${value}" == cpu ]]; then printf 'accelerator|%s\n' "${value}"; fi
  return 0
}

# The installed roles other than $1 pinned to card $2, space-separated.
gpu_users() {
  local self="$1" uuid="$2" other users=()
  while IFS= read -r other; do
    [[ -n "${other}" && "${other}" != "${self}" && ",$(role_gpu "${other}")," == *",${uuid},"* ]] && users+=("${other}")
  done < <(installed_roles)
  printf '%s' "${users[*]:-}"
}

# The card for role $1 when nothing was chosen: the one it already runs on, else a card no other role is pinned to, else (for
# a Thinking model: ollama, deep-thinking...) a card no other Thinking model is pinned to, else any card, preferring the most
# memory free right now.
automatic_gpu() {
  local role="$1" current i best="" best_free=-1 free pass user thinker
  current="$(role_gpu "${role}")"
  # Installed on the GPU without a card of its own: it runs on every card and keeps doing so.
  if [[ -z "${current}" && -f "$(installed_file "${role}")" && -f "${PROJECTS}/${role}/compose.gpu.yaml" ]]; then printf 'all'; return; fi
  for i in "${!NVIDIA_IDS[@]}"; do [[ "${NVIDIA_IDS[i]}" == "${current}" ]] && { printf '%s' "${current}"; return; }; done
  # Split across several cards: it keeps them while every one is still on this host.
  if [[ "${current}" == *,* ]] && known_gpus "${current}"; then printf '%s' "${current}"; return; fi
  for pass in free nothink any; do
    [[ "${pass}" != nothink || "${role}" =~ ^(ollama|deep-thinking(-[0-9])?)$ ]] || continue
    for i in "${!NVIDIA_IDS[@]}"; do
      if [[ "${pass}" == free ]]; then [[ -z "$(gpu_users "${role}" "${NVIDIA_IDS[i]}")" ]] || continue; fi
      if [[ "${pass}" == nothink ]]; then
        thinker=0
        for user in $(gpu_users "${role}" "${NVIDIA_IDS[i]}"); do [[ "${user}" =~ ^(ollama|deep-thinking(-[0-9])?)$ ]] && thinker=1; done
        (( ! thinker )) || continue
      fi
      free=$(( NVIDIA_MBS[i] - NVIDIA_USED[i] ))
      if (( free > best_free )); then best_free=${free}; best="${NVIDIA_IDS[i]}"; fi
    done
    [[ -n "${best}" ]] && { printf '%s' "${best}"; return; }
  done
}

# Installs a role's GPU overlay from $1 to $2. With card $3 (a UUID) it is the only one CUDA sees in the role's services
# (CUDA_VISIBLE_DEVICES takes UUIDs, so this also holds where Docker exposes every card, as WSL 2 does).
install_gpu_overlay() {
  local source="$1" target="$2" gpu="${3:-}"
  if ! gpu_list "${gpu}"; then cp "${source}" "${target}"; return; fi
  awk -v gpu="${gpu}" '
    /^[^ #]/ { section = ($0 ~ /^services:/) }
    { print }
    section && /^  [a-z0-9_-]+:[ \t]*$/ { print "    environment:"; print "      CUDA_VISIBLE_DEVICES: \"" gpu "\"" }
  ' "${source}" > "${target}"
}

require_dotnet() {
  if [[ -x "${DOTNET}" ]] && "${DOTNET}" --list-sdks | grep -q "^${DOTNET_SDK} "; then return; fi
  if [[ -n "${SUPPLY}" ]]; then
    local sdk="${SUPPLY}/dotnet-sdk-${DOTNET_SDK}-linux-$(host_arch).tar.gz"
    [[ -f "${sdk}" ]] || fail "Martlet did not send the .NET SDK ${DOTNET_SDK} this computer needs. Set it up again from Martlet."
    ask "Install the .NET SDK ${DOTNET_SDK} that Martlet sent from your PC into ~/.dotnet (no sudo)?" ||
      fail "The .NET SDK is required to build the Martlet gateway."
    mkdir -p "${HOME}/.dotnet"
    tar -xzf "${sdk}" -C "${HOME}/.dotnet"
    "${DOTNET}" --list-sdks | grep -q "^${DOTNET_SDK} " || fail "The .NET SDK ${DOTNET_SDK} from your PC did not install."
    return
  fi
  ask "Install the .NET SDK ${DOTNET_SDK} into ~/.dotnet with Microsoft's dotnet-install script (no sudo)?" ||
    fail "The .NET SDK is required to build the Martlet gateway."
  require_curl "the .NET SDK"
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/martlet-dotnet-install.sh
  bash /tmp/martlet-dotnet-install.sh --version "${DOTNET_SDK}" --install-dir "${HOME}/.dotnet"
}

# The gateway keeps its keys only under folders no one else can change: every folder above its state must be without
# group and other write access. Ubuntu's default umask (002, with a private group per user) leaves ~/.local and
# ~/.local/share group-writable, so take that off this account's own folders between ${HOME} and ${DATA}.
secure_data_parents() {
  local dir="${DATA}"
  while [[ "${dir}" == "${HOME}" || "${dir}" == "${HOME}/"* ]]; do
    if [[ -d "${dir}" && -O "${dir}" && -n "$(find "${dir}" -maxdepth 0 -perm /022 2>/dev/null)" ]]; then
      chmod go-w "${dir}"
      note "Removed group and other write access from ${dir}: the gateway keeps its keys only under folders no one else can change."
    fi
    [[ "${dir}" != "${HOME}" ]] || break
    dir="$(dirname "${dir}")"
  done
}

# Builds the native gateway into $1. With supplied files the packages come only from ${SUPPLY}/nuget (no network).
# No build servers or reusable MSBuild nodes outlive the build: they would keep the SSH session's output open (so Martlet's
# run never ends) and, through 4>&- 5>&- 6>&- 7>&- 9>&-, must not inherit the engine's locks either.
publish_gateway() {
  local offline=()
  if [[ -n "${SUPPLY}" ]]; then
    [[ -d "${SUPPLY}/nuget" ]] || fail "Martlet did not send the gateway's packages. Set this computer up again from Martlet."
    cat > "${SUPPLY}/NuGet.config" <<XML
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="martlet-supply" value="${SUPPLY}/nuget" />
  </packageSources>
</configuration>
XML
    offline=("-p:RestoreConfigFile=${SUPPLY}/NuGet.config" -p:NuGetAudit=false)
  fi
  DOTNET_ROOT="${HOME}/.dotnet" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=1 \
    DOTNET_CLI_USE_MSBUILD_SERVER=0 MSBUILDDISABLENODEREUSE=1 \
    "${DOTNET}" publish "${REPO}/src/Martlet.Gateway.Host.Linux/Martlet.Gateway.Host.Linux.csproj" \
    -c Release -o "$1" -p:UseAppHost=false -nodeReuse:false -p:UseSharedCompilation=false "${offline[@]}" 4>&- 5>&- 6>&- 7>&- 9>&-
}
