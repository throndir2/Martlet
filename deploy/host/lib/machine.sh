# shellcheck shell=bash
# martlet-host module (deploy/host/lib/machine.sh), sourced by deploy/host/martlet-host; it is not run by itself.
# The machine report (what this host is like: GPUs, CPU, memory, features, downloads) and
# this host's address.

# ------------------------------------------------------------------ machine report (what this host is like)
# Written next to host.json (not inside it, so hardware changes never invalidate the service approval) and served
# read-only to paired desktops at /martlet/v1/machine. Desktops use it to plan which computer runs which role.
MACHINE_FILE="${GW_CONF_DIR}/machine.json"
json_text() { printf '"%s"' "$(printf '%s' "$1" | tr -c 'A-Za-z0-9 ._()+/@:,#-' ' ' | tr -s ' ' | sed 's/^ //; s/ $//' | cut -c1-120)"; }
json_opt() { if [[ -n "$1" ]]; then json_text "$1"; else printf 'null'; fi; }
json_num() { if [[ "$1" =~ ^[0-9]+(\.[0-9]+)?$ ]]; then printf '%s' "$1"; else printf 'null'; fi; }
json_string_array() { local value first=1; for value in "$@"; do [[ -n "${value}" ]] || continue; [[ ${first} == 1 ]] || printf ','; json_text "${value}"; first=0; done; }

# The one rule for a new host ID (Martlet.Core.Network.HostIds.ForComputer): the computer's name in lowercase, spaces as "-",
# only letters, digits, '.', '_' and '-', without a leading '-', '_' or '.', at most 58 characters, then "-host"
# (Nahida: nahida-host). A host that has an ID keeps it (host.env).
host_id_for() {
  local id
  id="$(printf '%s' "$1" | LC_ALL=C tr '[:upper:]' '[:lower:]' | LC_ALL=C tr '[:space:]' '-' | LC_ALL=C tr -cd 'a-z0-9._-' |
    sed 's/^[-_.]*//' | cut -c1-58)"
  printf '%s-host' "${id:-martlet}"
}

# The name this computer gives itself (Nahida), for the machine report: desktops show it instead of the host ID. Natively the
# host name; in Docker the Docker host's name, except Docker Desktop, which names its engine docker-desktop on every computer:
# then the name Martlet passed at setup (MARTLET_COMPUTER_NAME, kept in host.env). Empty when nothing names it.
computer_name() {
  local name=""
  if [[ "${MODE}" == docker ]]; then name="$(docker info --format '{{.Name}}' 2>/dev/null </dev/null || true)"
  else name="$(hostname -s 2>/dev/null || hostname 2>/dev/null || true)"; fi
  name="${name%%.*}"
  [[ "${name,,}" != docker-desktop ]] || name=""
  [[ -n "${name}" ]] || name="${MARTLET_COMPUTER_NAME:-}"
  [[ -n "${name}" || ! -f "${CONF}/host.env" ]] || name="$(settings_value computer_name)"
  printf '%s' "${name}" | LC_ALL=C tr -cd 'A-Za-z0-9._-' | cut -c1-63
}

host_timezone() {
  local tz=""
  if [[ "${MODE}" == docker ]]; then
    # The Docker daemon resolves this bind mount on the Docker host, not inside this engine container.
    tz="$(docker run --rm -v /etc:/host-etc:ro "${HELPER_IMAGE}" sh -c '
      if [ -r /host-etc/timezone ]; then head -n 1 /host-etc/timezone; exit 0; fi
      target=$(readlink /host-etc/localtime 2>/dev/null || true)
      case "$target" in *zoneinfo/*) printf "%s\n" "${target##*zoneinfo/}" ;; esac
    ' 2>/dev/null || true)"
  else
    tz="$(timedatectl show -p Timezone --value 2>/dev/null | head -n1 || true)"
    [[ -n "${tz}" ]] || tz="$(head -n1 /etc/timezone 2>/dev/null || true)"
  fi
  tz="${tz%%$'\r'*}"; tz="${tz%%$'\n'*}"
  [[ "${tz}" =~ ^[A-Za-z0-9_+./-]{1,64}$ ]] || tz=UTC
  printf '%s' "${tz}"
}

feature_add() {
  local -n out=$1
  local token="$2" existing
  [[ "${token}" =~ ^[a-z0-9-]{1,32}$ ]] || return 0
  for existing in "${out[@]:-}"; do [[ "${existing}" == "${token}" ]] && return 0; done
  (( ${#out[@]} < 16 )) || return 0
  out+=("${token}")
}

host_network_capable() {
  [[ "${MODE}" == native ]] && return 0
  docker_engine_supported
}

host_port_open() {
  local port="$1"
  [[ "${port}" =~ ^[0-9]{1,5}$ ]] || return 1
  if [[ "${MODE}" == docker ]]; then
    # shellcheck disable=SC2016
    docker run --rm --network host --entrypoint bash "${IMAGE}" -c 'port="$1"; timeout 2 bash -c ":</dev/tcp/127.0.0.1/${port}"' _ "${port}" >/dev/null 2>&1
  else
    timeout 2 bash -c ":</dev/tcp/127.0.0.1/${port}" >/dev/null 2>&1
  fi
}

# Whether a role's containers can look up internet names. Docker Desktop's DNS for containers (192.168.65.7) can stop
# answering while Docker itself, which pulls images through the host, still resolves; downloads inside a role then fail.
role_dns_works() {
  local role_network="$1" name=registry-1.docker.io
  if [[ "${MODE}" != docker ]]; then
    timeout 10 getent ahosts "${name}" >/dev/null 2>&1
  elif [[ "${role_network}" == host ]]; then
    timeout 20 docker run --rm --network host --entrypoint getent "${IMAGE}" ahosts "${name}" >/dev/null 2>&1
  else
    timeout 20 docker run --rm --network "container:${NET}" --entrypoint getent "${IMAGE}" ahosts "${name}" >/dev/null 2>&1
  fi
}

# After a role step failed: when name lookups fail inside the role's network, waits a few minutes for them to recover
# (returns 0: running the step again may work) or stops with what to fix. Returns 1 when names resolve (another cause).
recover_role_dns() {
  local role="$1" role_network="$2" attempt
  role_dns_works "${role_network}" && return 1
  note "Containers on this computer can't look up internet names right now (Docker's DNS doesn't answer them). Waiting a few minutes for it to recover..."
  for attempt in $(seq 1 8); do
    sleep 15
    if role_dns_works "${role_network}"; then note "Name lookups work again; retrying."; return 0; fi
  done
  fail "Containers on this computer can't look up internet names (Docker's DNS for containers doesn't answer), although Docker itself can. Restart Docker Desktop (or Docker), and check that no VPN, firewall or antivirus blocks its DNS, then run 'martlet-host add ${role}' again: what it already downloaded is kept."
}

role_container_running() {
  local role="$1"
  "${DOCKER[@]}" ps --filter "label=com.docker.compose.project=martlet-${role}" --filter status=running --format '{{.ID}}' 2>/dev/null | grep -q .
}

role_ready_address() {
  if [[ "$1" == host && "${MODE}" == docker ]]; then settings_value lan_ip; else printf '127.0.0.1'; fi
}

ensure_role_timezone_env() {
  local project="$1" tz
  [[ -f "${project}/.env" ]] || return 0
  tz="$(host_timezone)"
  ( umask 077; grep -v '^MARTLET_TZ=' "${project}/.env" > "${project}/.env.next" || true
    printf 'MARTLET_TZ=%s\n' "${tz}" >> "${project}/.env.next" )
  mv -f "${project}/.env.next" "${project}/.env"
}

native_host_probe_facts() {
  local item base lower zigbee=0 zwave=0 unknown_serial=0
  timeout 2 bash -c ':</dev/tcp/127.0.0.1/8123' >/dev/null 2>&1 && printf '%s\n' port-8123
  timeout 2 bash -c ':</dev/tcp/127.0.0.1/1883' >/dev/null 2>&1 && printf '%s\n' port-1883
  shopt -s nullglob
  for item in /dev/serial/by-id/*; do
    [[ -e "${item}" ]] || continue
    base="$(basename "${item}")"; lower="${base,,}"
    if [[ "${lower}" =~ zigbee|sonoff|conbee|skyconnect|zbt|ezsp|cc2652|slzb|tubeszb ]]; then zigbee=1
    elif [[ "${lower}" =~ zwave|z-wave|aeotec|zooz|zst|800z|700z ]]; then zwave=1
    else unknown_serial=1; fi
  done
  shopt -u nullglob
  (( zigbee == 1 )) && printf '%s\n' zigbee-radio
  (( zwave == 1 )) && printf '%s\n' zwave-radio
  (( unknown_serial == 1 && zigbee == 0 && zwave == 0 )) && printf '%s\n' serial-radio
  for item in /sys/class/bluetooth/hci*; do [[ -e "${item}" ]] && { printf '%s\n' bluetooth; break; }; done
}

docker_host_probe_facts() {
  # shellcheck disable=SC2016
  docker run --rm --network host --entrypoint bash \
    -v /dev:/host-dev:ro -v /sys/class:/host-class:ro "${IMAGE}" -c '
      set +e
      timeout 2 bash -c ":</dev/tcp/127.0.0.1/8123" >/dev/null 2>&1 && printf "%s\n" port-8123
      timeout 2 bash -c ":</dev/tcp/127.0.0.1/1883" >/dev/null 2>&1 && printf "%s\n" port-1883
      shopt -s nullglob
      zigbee=0; zwave=0; unknown_serial=0
      for item in /host-dev/serial/by-id/*; do
        [ -e "$item" ] || continue
        base="${item##*/}"; lower="${base,,}"
        if [[ "$lower" =~ zigbee|sonoff|conbee|skyconnect|zbt|ezsp|cc2652|slzb|tubeszb ]]; then zigbee=1
        elif [[ "$lower" =~ zwave|z-wave|aeotec|zooz|zst|800z|700z ]]; then zwave=1
        else unknown_serial=1; fi
      done
      (( zigbee == 1 )) && printf "%s\n" zigbee-radio
      (( zwave == 1 )) && printf "%s\n" zwave-radio
      (( unknown_serial == 1 && zigbee == 0 && zwave == 0 )) && printf "%s\n" serial-radio
      for item in /host-class/bluetooth/hci*; do [ -e "$item" ] && { printf "%s\n" bluetooth; break; }; done
    ' 2>/dev/null || true
}

host_probe_facts() {
  if [[ "${MODE}" == docker ]]; then docker_host_probe_facts; else native_host_probe_facts; fi
}

installed_record_features() {
  local file value
  for file in "${CONF}/roles"/*.role; do
    [[ -e "${file}" ]] || continue
    value="$(conf_value "${file}" feature)"
    [[ -n "${value}" ]] && printf '%s\n' "${value}"
  done
}

container_image_features() {
  "${DOCKER[@]}" ps --format '{{.Image}}\t{{.Label "com.docker.compose.project"}}' 2>/dev/null | while IFS=$'\t' read -r image project; do
    [[ -n "${image}" && "${project}" != martlet-* ]] || continue
    printf '%s\n' "${image,,}"
  done
}

machine_features() {
  local features=() installed=() facts=() images=() item installed_ha=0 ha_port=0 mqtt_port=0 bluetooth=0
  local zigbee_radio=0 zwave_radio=0 serial_radio=0 container_ha=0 zigbee2mqtt=0 zwave_js=0 frigate=0 go2rtc=0 esphome=0 node_red=0 matter_server=0 music_assistant=0
  mapfile -t installed < <(installed_record_features)
  mapfile -t facts < <(host_probe_facts)
  mapfile -t images < <(container_image_features)
  for item in "${installed[@]}"; do [[ "${item}" == home-assistant ]] && installed_ha=1; done
  for item in "${facts[@]}"; do
    case "${item}" in
      port-8123) ha_port=1 ;; port-1883) mqtt_port=1 ;; zigbee-radio) zigbee_radio=1 ;; zwave-radio) zwave_radio=1 ;; serial-radio) serial_radio=1 ;; bluetooth) bluetooth=1 ;;
    esac
  done
  for item in "${images[@]}"; do
    [[ "${item}" == *zigbee2mqtt* ]] && zigbee2mqtt=1
    [[ "${item}" == *zwave-js* || "${item}" == *zwavejs2mqtt* ]] && zwave_js=1
    [[ "${item}" == *frigate* ]] && frigate=1
    [[ "${item}" == *go2rtc* ]] && go2rtc=1
    [[ "${item}" == *esphome* ]] && esphome=1
    [[ "${item}" == *nodered/node-red* || "${item}" == *node-red* ]] && node_red=1
    [[ "${item}" == *matter-server* || "${item}" == *matterjs-server* ]] && matter_server=1
    [[ "${item}" == *music-assistant* ]] && music_assistant=1
    [[ "${item}" == *home-assistant/home-assistant* || "${item}" == *homeassistant* ]] && container_ha=1
  done
  host_network_capable && feature_add features host-network
  for item in "${installed[@]}"; do feature_add features "${item}"; done
  (( (ha_port == 1 && installed_ha == 0) || container_ha == 1 )) && feature_add features ha-existing
  (( mqtt_port == 1 )) && feature_add features mqtt-broker
  (( zigbee2mqtt == 1 )) && feature_add features zigbee2mqtt
  (( zwave_js == 1 )) && feature_add features zwave-js
  (( frigate == 1 )) && feature_add features frigate
  (( go2rtc == 1 )) && feature_add features go2rtc
  (( esphome == 1 )) && feature_add features esphome
  (( node_red == 1 )) && feature_add features node-red
  (( matter_server == 1 )) && feature_add features matter-server
  (( music_assistant == 1 )) && feature_add features music-assistant
  (( zigbee_radio == 1 )) && feature_add features zigbee-radio
  (( zwave_radio == 1 )) && feature_add features zwave-radio
  (( serial_radio == 1 )) && feature_add features serial-radio
  (( bluetooth == 1 )) && feature_add features bluetooth
  # printf with no values still prints one empty line, which became "features":[""] and made the gateway ignore the report.
  if (( ${#features[@]} > 0 )); then printf '%s\n' "${features[@]}"; fi
}

feature_words() {
  case "$1" in
    host-network) printf 'host networking' ;; home-assistant) printf 'Martlet Home Assistant' ;; ha-existing) printf 'an existing Home Assistant' ;;
    mqtt-broker) printf 'MQTT broker on port 1883' ;; zigbee2mqtt) printf 'Zigbee2MQTT container' ;; zwave-js) printf 'Z-Wave JS container' ;;
    frigate) printf 'Frigate container' ;; go2rtc) printf 'go2rtc container' ;; esphome) printf 'ESPHome container' ;; node-red) printf 'Node-RED container' ;;
    matter-server) printf 'Matter server container' ;; music-assistant) printf 'Music Assistant container' ;; zigbee-radio) printf 'Zigbee USB radio' ;;
    zwave-radio) printf 'Z-Wave USB radio' ;; serial-radio) printf 'USB serial radio' ;; bluetooth) printf 'Bluetooth adapter' ;;
  esac
}

print_feature_summary() {
  local token word joined=""
  for token in "$@"; do
    word="$(feature_words "${token}")"
    [[ -n "${word}" ]] || continue
    [[ -z "${joined}" ]] || joined+=", "
    joined+="${word}"
  done
  note "Found on this computer: ${joined:-no Home Assistant helpers detected yet}."
}

# Prints one JSON object per NVIDIA GPU from nvidia-smi CSV lines "name, memory MiB, driver, persistence, power limit W,
# default power limit W" (the last three are what martlet-prepare's gpu-power sets).
nvidia_gpus_json() {
  local name memory driver persistence limit standard flag
  while IFS=, read -r name memory driver persistence limit standard; do
    [[ -n "${name// /}" ]] || continue
    memory="${memory// /}"; driver="${driver# }"; persistence="${persistence// /}"; limit="${limit// /}"; standard="${standard// /}"
    case "${persistence}" in Enabled) flag=true ;; Disabled) flag=false ;; *) flag=null ;; esac
    printf '{"name":%s,"vendor":"nvidia","memory_mb":%s,"driver":%s,"persistence":%s,"power_limit_w":%s,"power_default_w":%s}\n' \
      "$(json_text "${name}")" "$(json_num "${memory%%.*}")" "$(json_opt "${driver}")" "${flag}" "$(json_num "${limit}")" "$(json_num "${standard}")"
  done
}

# Kept downloads: the models each route role ran on this host, one file for each role, newest last (16 at most).
# Removing a role or switching its model keeps its downloads (compose down keeps the data volumes), so the machine report
# lists them and Martlet's recommended setup turns a role back on, or switches back, without downloading it again.
DOWNLOADS_DIR="${CONF}/downloads"
DOWNLOAD_MODEL='^[A-Za-z0-9][A-Za-z0-9._:/-]{0,127}$'
DOWNLOAD_ROLE='^[a-z0-9][a-z0-9-]{0,31}$'

record_downloads() {
  local role record model file
  while IFS= read -r role; do
    [[ "${role}" =~ ${DOWNLOAD_ROLE} ]] || continue
    record="$(installed_file "${role}")"
    model="$(conf_value "${record}" model)"
    [[ -n "$(conf_value "${record}" kind)" && "${model}" =~ ${DOWNLOAD_MODEL} ]] || continue
    # The owner's own model app keeps its models; Martlet keeps no download for a relay to it.
    [[ -z "$(conf_value "${record}" api)" ]] || continue
    file="${DOWNLOADS_DIR}/${role}"
    [[ "$(tail -n 1 "${file}" 2>/dev/null || true)" != "${model}" ]] || continue
    mkdir -p "${DOWNLOADS_DIR}"
    ( umask 077
      { grep -vxF -- "${model}" "${file}" 2>/dev/null || true; printf '%s\n' "${model}"; } | tail -n 16 > "${file}.tmp"
      mv -f "${file}.tmp" "${file}" )
  done < <(installed_roles)
}

# The kept downloads as JSON objects ({"role":...,"model":...}, 64 at most). A role whose data volumes were all deleted
# (docker volume rm) has nothing kept; when Docker can't list volumes (no access without a password), the record is used.
downloads_json() {
  [[ -d "${DOWNLOADS_DIR}" ]] || return 0
  local docker=("${DOCKER[@]}") volumes="" checked=0 file role wanted volume have model count=0
  [[ "${docker[0]}" != sudo ]] || docker=(sudo -n "${docker[@]:1}")
  if volumes="$(timeout 20 "${docker[@]}" volume ls -q 2>/dev/null)"; then checked=1; fi
  for file in "${DOWNLOADS_DIR}"/*; do
    role="$(basename "${file}")"
    [[ -f "${file}" && "${role}" =~ ${DOWNLOAD_ROLE} ]] || continue
    if [[ ${checked} == 1 ]]; then
      wanted="$(sed -n 's/^[[:space:]]*name:[[:space:]]*\(martlet-[A-Za-z0-9._-]*\)[[:space:]]*$/\1/p' "${ROLES_DIR}/${role}/compose.yaml" 2>/dev/null || true)"
      if [[ -n "${wanted}" ]]; then
        have=0
        while IFS= read -r volume; do
          if grep -qxF -- "${volume}" <<< "${volumes}"; then have=1; break; fi
        done <<< "${wanted}"
        [[ ${have} == 1 ]] || continue
      fi
    fi
    while IFS= read -r model; do
      [[ "${model}" =~ ${DOWNLOAD_MODEL} && ${count} -lt 64 ]] || continue
      [[ ${count} == 0 ]] || printf ','
      printf '{"role":"%s","model":"%s"}' "${role}" "${model}"
      count=$((count + 1))
    done < "${file}"
  done
}

collect_machine() {
  local os="" kernel="" cpu="" threads="" memory="" runtime="" nvidia_containers="unknown" cuda="" arch="" gpus=() features=() line
  local query=(--query-gpu=name,memory.total,driver_version,persistence_mode,power.limit,power.default_limit --format=csv,noheader,nounits)
  local cuda_pattern='CUDA Version: *[0-9]+(\.[0-9]+)+'
  cpu="$(grep -m1 'model name' /proc/cpuinfo 2>/dev/null | cut -d: -f2- || true)"
  if [[ "${MODE}" == docker ]]; then
    arch="$(docker info --format '{{.Architecture}}' 2>/dev/null || true)"
    os="$(docker info --format '{{.OperatingSystem}}' 2>/dev/null || true)"
    kernel="$(docker info --format '{{.KernelVersion}}' 2>/dev/null || true)"
    threads="$(docker info --format '{{.NCPU}}' 2>/dev/null || true)"
    memory="$(docker info --format '{{.MemTotal}}' 2>/dev/null | awk '{ if ($1 > 0) printf "%.1f", $1 / 1073741824 }' || true)"
    runtime="Docker $(docker version --format '{{.Server.Version}}' 2>/dev/null || printf 'unknown')"
    # The NVIDIA runtime injects nvidia-smi into GPU containers, so this sees the Docker host's GPUs.
    mapfile -t gpus < <(timeout 120 docker run --rm --gpus all --entrypoint nvidia-smi "${IMAGE}" "${query[@]}" 2>/dev/null | nvidia_gpus_json || true)
    if [[ ${#gpus[@]} -gt 0 ]]; then
      nvidia_containers="yes"
      cuda="$(timeout 60 docker run --rm --gpus all --entrypoint nvidia-smi "${IMAGE}" 2>/dev/null | grep -oE "${cuda_pattern}" | grep -oE '[0-9.]+$' | head -n1 || true)"
    else nvidia_containers="no"; fi
  else
    arch="$(uname -m)"
    os="$(. /etc/os-release 2>/dev/null && printf '%s' "${PRETTY_NAME:-Linux}" || printf 'Linux')"
    kernel="$(uname -r)"
    threads="$(nproc 2>/dev/null || true)"
    memory="$(awk '/^MemTotal:/ { printf "%.1f", $2 / 1048576 }' /proc/meminfo 2>/dev/null || true)"
    if command -v docker >/dev/null 2>&1; then
      runtime="Docker $(docker version --format '{{.Server.Version}}' 2>/dev/null || printf '(not running)')"
      if command -v nvidia-ctk >/dev/null 2>&1; then nvidia_containers="yes"; else nvidia_containers="no"; fi
    fi
    if command -v nvidia-smi >/dev/null 2>&1; then
      mapfile -t gpus < <(timeout 30 nvidia-smi "${query[@]}" 2>/dev/null | nvidia_gpus_json || true)
      cuda="$(timeout 30 nvidia-smi 2>/dev/null | grep -oE "${cuda_pattern}" | grep -oE '[0-9.]+$' | head -n1 || true)"
    fi
    # AMD GPUs through sysfs (amdgpu reports dedicated VRAM; APUs with under 2 GiB are integrated and skipped).
    local card vendor bytes slot name nvidia_seen=${#gpus[@]}
    for card in /sys/class/drm/card[0-9]*; do
      [[ -f "${card}/device/vendor" && ! "${card}" =~ - ]] || continue
      vendor="$(cat "${card}/device/vendor" 2>/dev/null || true)"
      if [[ "${vendor}" == 0x10de && ${nvidia_seen} -eq 0 ]]; then
        nvidia_seen=1
        gpus+=('{"name":"NVIDIA GPU (driver not working; nvidia-smi failed)","vendor":"nvidia","memory_mb":null,"driver":null}')
      elif [[ "${vendor}" == 0x1002 ]]; then
        bytes="$(cat "${card}/device/mem_info_vram_total" 2>/dev/null || printf '0')"
        [[ "${bytes}" =~ ^[0-9]+$ && ${bytes} -ge 2147483648 ]] || continue
        slot="$(basename "$(readlink -f "${card}/device")")"
        name="$(lspci -s "${slot}" 2>/dev/null | cut -d: -f3- || true)"
        gpus+=("{\"name\":$(json_text "${name:-AMD GPU}"),\"vendor\":\"amd\",\"memory_mb\":$(( bytes / 1048576 )),\"driver\":\"amdgpu\"}")
      fi
    done
  fi
  local joined features_joined downloads_joined
  joined="$(IFS=,; printf '%s' "${gpus[*]:-}")"
  mapfile -t features < <(machine_features)
  features_joined="$(json_string_array "${features[@]}")"
  record_downloads
  downloads_joined="$(downloads_json)"
  # The roles always run on Linux (natively or in Docker containers), whatever computer Docker runs on.
  case "${arch}" in x86_64|amd64) arch="x64" ;; aarch64|arm64) arch="arm64" ;; *) arch="" ;; esac
  mkdir -p "${GW_CONF_DIR}"; chmod 700 "${GW_CONF_DIR}"
  ( umask 077
    printf '{"collected_at":"%s","method":"%s","computer_name":%s,"platform":"linux","architecture":%s,"operating_system":%s,"kernel":%s,"processor":%s,"processor_threads":%s,"memory_gb":%s,"container_runtime":%s,"nvidia_containers":"%s","cuda":%s,"gpus":[%s],"features":[%s],"downloads":[%s]}\n' \
      "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "${MODE}" "$(json_opt "$(computer_name)")" "$(json_opt "${arch}")" "$(json_text "${os:-Linux}")" "$(json_opt "${kernel}")" "$(json_opt "${cpu}")" \
      "$(json_num "${threads}")" "$(json_num "${memory}")" "$(json_opt "${runtime}")" "${nvidia_containers}" "$(json_opt "${cuda}")" "${joined}" "${features_joined}" \
      "${downloads_joined}" > "${MACHINE_FILE}.tmp" )
  # The same machine as last time keeps its report (and the time it was collected), so the gateway serving it needn't restart;
  # "fresh" (martlet-host machine) always takes the new one.
  if [[ "${1:-}" != fresh && -f "${MACHINE_FILE}" ]] &&
     [[ "$(sed 's/"collected_at":"[^"]*",//' "${MACHINE_FILE}")" == "$(sed 's/"collected_at":"[^"]*",//' "${MACHINE_FILE}.tmp")" ]]; then
    rm -f "${MACHINE_FILE}.tmp"
  else
    mv -f "${MACHINE_FILE}.tmp" "${MACHINE_FILE}"
  fi
}

show_machine() {
  [[ -f "${MACHINE_FILE}" ]] || { note "No machine report yet. Run: martlet-host machine"; return 0; }
  note "Reported to paired desktops ($(basename "${MACHINE_FILE}")):"
  sed -e 's/,"/,\n  "/g' -e 's/^{/{\n  /' "${MACHINE_FILE}"
}

apply_config() {
  write_config
  refresh_gateway
}

restart_gateway_for_machine_report() {
  note "Restarting the gateway so paired desktops see this host's machine report."
  if approval_matches; then service_start; else note "The gateway needs approval first; run: martlet-host setup"; fi
}

# After a route-less role changed: the gateway restarts only when the machine report it serves changed.
publish_machine_report() {
  if approval_matches && gateway_current; then note "Paired desktops already see this machine report; the gateway keeps running."
  else restart_gateway_for_machine_report; fi
}

require_setup() {
  [[ -f "${CONF}/host.env" && -d "${GW_STATE}" && -f "${GW_APP}/Martlet.Gateway.Host.Linux.dll" ]] ||
    fail "Run 'martlet-host setup' first."
}

private_addresses() {
  ip -4 -o addr show scope global 2>/dev/null | awk '{print $4}' | cut -d/ -f1 |
    grep -E '^(10\.|192\.168\.|172\.(1[6-9]|2[0-9]|3[01])\.)' || true
}

choose_address() {
  local lan port host_id
  if [[ "${MODE}" == docker ]]; then
    # Inside a container only the Docker host knows its LAN address; the launcher passes it in.
    lan="${MARTLET_HOST_ADDRESS:-}"
    [[ "${lan}" =~ ^(10\.|192\.168\.|172\.(1[6-9]|2[0-9]|3[01])\.)[0-9.]+$ ]] ||
      fail "Set MARTLET_HOST_ADDRESS to the Docker host's private LAN address (the Martlet desktop does this)."
    port="${MARTLET_HOST_PORT:-9443}"
  else
    local addresses=()
    mapfile -t addresses < <(private_addresses)
    [[ ${#addresses[@]} -gt 0 ]] || fail "No private LAN IPv4 address found on this host."
    note "Private LAN addresses on this host:"; printf '  %s\n' "${addresses[@]}"
    if assume_yes; then
      lan="${MARTLET_HOST_ADDRESS:-${addresses[0]}}"
      port="${MARTLET_HOST_PORT:-9443}"
    else
      read -r -p "Address your desktops will use (default ${MARTLET_HOST_ADDRESS:-${addresses[0]}}): " lan
      lan="${lan:-${MARTLET_HOST_ADDRESS:-${addresses[0]}}}"
      read -r -p "Gateway port (default ${MARTLET_HOST_PORT:-9443}): " port
      port="${port:-${MARTLET_HOST_PORT:-9443}}"
    fi
    [[ " ${addresses[*]} " == *" ${lan} "* ]] || fail "${lan} is not a private address of this host."
  fi
  [[ "${port}" =~ ^[0-9]{2,5}$ ]] || fail "Invalid port."
  # The host ID is permanent once the gateway identity exists; a new one follows the one rule (host_id_for).
  local name; name="$(computer_name)"
  [[ -f "${CONF}/host.env" ]] && host_id="$(settings_value host_id)"
  host_id="${host_id:-${MARTLET_HOST_ID:-$(host_id_for "${name}")}}"
  [[ "${host_id}" =~ ^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$ ]] || fail "Invalid host ID."
  ( umask 077; printf 'host_id=%s\nlan_ip=%s\nport=%s\n' "${host_id}" "${lan}" "${port}" > "${CONF}/host.env"
    [[ -z "${name}" ]] || printf 'computer_name=%s\n' "${name}" >> "${CONF}/host.env" )
}
