# shellcheck shell=bash
# martlet-host module (deploy/host/lib/docker.sh), sourced by deploy/host/martlet-host; it is not run by itself.
# The Docker method: the network holder (martlet-host-net), the keeper that follows a new home
# address, the engine sessions (docker_launch) and the commands hold, keep, home and serve.

# Docker method: the engine sessions (docker_launch's "martlet-host <command>" containers) running in the network
# holder's namespace now, one per line: name, then what it does ("installing chatterbox").
holder_engines() {
  local holder id name mode inner cmd words
  holder="$(docker container inspect -f '{{.Id}}' "${NET}" 2>/dev/null)" || return 0
  while read -r id name; do
    [[ -n "${id}" ]] || continue
    read -r mode inner < <(docker container inspect -f \
      '{{.HostConfig.NetworkMode}} {{range .Config.Env}}{{if eq . "MARTLET_ENGINE=inner"}}inner{{end}}{{end}}' "${id}" 2>/dev/null || true) || continue
    [[ "${inner:-}" == inner && ( "${mode}" == "container:${holder}" || "${mode}" == "container:${NET}" ) ]] || continue
    cmd="$(docker container inspect -f '{{join .Config.Cmd " "}}' "${id}" 2>/dev/null || true)"
    read -r -a words <<< "${cmd#--yes }"
    printf '%s\t%s\n' "${name}" "$(change_words "${words[@]}")"
  done < <(docker ps --no-trunc --filter status=running --filter volume=martlet-host-config --format '{{.ID}} {{.Names}}' 2>/dev/null || true)
}

# Replacing the network holder while another engine session runs in its namespace strands that session on a loopback
# nothing listens on any more: an add then starts its role in the new namespace and waits for it in the old one until it
# times out ("did not start"). So setup waits for those sessions first, with the engine lock's patience (MARTLET_LOCK_WAIT;
# a background run stops at once with MARTLET-BUSY).
wait_for_holder_engines() {
  local engines=() patience waited=0 step=5 line what
  patience="$(lock_patience)"
  while :; do
    mapfile -t engines < <(holder_engines)
    if [[ ${#engines[@]} -eq 0 ]]; then
      (( waited == 0 )) || note "That finished. Continuing with setting up this host."
      return 0
    fi
    what=""
    for line in "${engines[@]}"; do what+="${what:+; }${line#*$'\t'} (${line%%$'\t'*})"; done
    (( waited < patience )) || busy_stop "${what}"
    if (( waited == 0 )); then
      note "This host is busy: ${what}. Waiting for it to finish before replacing the network holder (setting up this host)..."
    elif (( waited % 60 == 0 )); then
      note "Still waiting: ${what}. Waited $(( waited / 60 )) min."
    fi
    sleep "${step}"; waited=$(( waited + step ))
  done
}

# Docker method: whether this engine session runs outside the current network holder's namespace (a setup replaced the
# holder while it ran or waited), so its loopback no longer reaches the gateway or the roles.
engine_stranded() {
  [[ "${MODE}" == docker && -n "${MARTLET_ENGINE_NAME:-}" ]] || return 1
  local holder mode
  holder="$(docker container inspect -f '{{.Id}}' "${NET}" 2>/dev/null)" || return 1
  mode="$(docker container inspect -f '{{.HostConfig.NetworkMode}}' "${MARTLET_ENGINE_NAME}" 2>/dev/null)" || return 1
  [[ "${mode}" == container:* && "${mode}" != "container:${holder}" && "${mode}" != "container:${NET}" ]]
}

stranded_text() {
  printf "This host's network holder (%s) was replaced while this ran (a setup ran meanwhile), so this session can no longer reach the gateway or roles." "${NET}"
}

# Docker method: where the network holder publishes the gateway port: "<lan>:<port>" (the Docker host's home address
# only), ":<port>" (every address of the Docker host, once the owner set outside addresses) or empty without a holder.
holder_binding() {
  local entries port entry
  entries="$(docker container inspect -f '{{range $p, $b := .HostConfig.PortBindings}}{{range $b}}{{$p}}={{.HostIp}}:{{.HostPort}} {{end}}{{end}}' \
    "${NET}" 2>/dev/null || true)"
  # Only the gateway port's binding: a holder that also publishes the front door names the gateway's port in its label.
  port="$(docker container inspect -f '{{index .Config.Labels "martlet.home"}}' "${NET}" 2>/dev/null || true)"; port="${port##*:}"
  for entry in ${entries}; do
    if [[ ! "${port}" =~ ^[0-9]+$ || "${entry%%=*}" == "${port}/tcp" ]]; then printf '%s' "${entry#*=}"; return 0; fi
  done
}

# Docker method: the front door port the network holder publishes (docs/OPENAI_ENDPOINT.md), or empty when it publishes none.
holder_front() {
  local front
  front="$(docker container inspect -f '{{index .Config.Labels "martlet.front-door"}}' "${NET}" 2>/dev/null || true)"
  [[ "${front}" =~ ^[0-9]{4,5}$ ]] && printf '%s' "${front}"
  return 0
}

# Docker method: the holder's home address "<lan>:<port>": its martlet.home label, or what an older holder (without the
# label) publishes on.
holder_home() {
  local home binding
  home="$(docker container inspect -f '{{index .Config.Labels "martlet.home"}}' "${NET}" 2>/dev/null || true)"
  binding="$(holder_binding)"
  if [[ "${home}" =~ ^[0-9.]+:[0-9]+$ ]]; then printf '%s' "${home}"
  elif [[ "${binding}" =~ ^[0-9.]+:[0-9]+$ ]]; then printf '%s' "${binding}"; fi
}

# Docker method: makes the network holder publish the gateway port on the Docker host's home address <lan>, or on every
# address of the Docker host when <wide> is 1. An overlay network such as Tailscale reaches the Docker host on its own
# overlay address, and Docker Desktop can't publish on an address that comes and goes (the holder would not start while
# the overlay is off). Replaces the holder, which restarts the gateway and roles in its network, only when the published
# address or the image changed.
# <front> is the front door's port (docs/OPENAI_ENDPOINT.md), or empty for none: published on the home address and on this
# computer's loopback (for a proxy or Tailscale Funnel on the same computer), or on every address with <wide>.
ensure_holder() {
  local lan="$1" port="$2" wide="$3" socket_group="$4" front="${5:-}" publish=(-p "$1:$2:$2") want="$1:$2" current image waited
  if [[ "${wide}" == 1 ]]; then publish=(-p "${port}:${port}"); want=":${port}"; fi
  if [[ -n "${front}" ]]; then
    [[ "${front}" =~ ^[0-9]{4,5}$ && "${front}" != "${port}" ]] || fail "The front door needs a port of its own (for example 9445)."
    if [[ "${wide}" == 1 ]]; then publish+=(-p "${front}:${front}")
    else publish+=(-p "${lan}:${front}:${front}" -p "127.0.0.1:${front}:${front}"); fi
  fi
  image="$(docker container inspect -f '{{.Config.Image}}' "${NET}" 2>/dev/null || true)"
  current=""; [[ -z "${image}" ]] || current="$(holder_binding) $(holder_front) ${image}"
  [[ "${current}" != "${want} ${front} ${IMAGE}" ]] || return 0
  if [[ -n "${current}" ]]; then
    wait_for_holder_engines
    docker rm -f "${NET}" >/dev/null
  fi
  docker run -d --name "${NET}" --restart unless-stopped "${publish[@]}" --label "martlet.home=${lan}:${port}" \
    --label "martlet.front-door=${front}" --user 1000:1000 \
    --group-add "${socket_group}" -v /var/run/docker.sock:/var/run/docker.sock "${IMAGE}" hold >/dev/null
  [[ -z "${front}" ]] || note "Front door port ${front} is published by ${NET} too (for your proxy, or Tailscale on this computer)."
  if [[ "${wide}" == 1 ]]; then
    note "Gateway port ${port} is published by ${NET} on every address of this Docker host (home address ${lan}:${port}), so your overlay network reaches it too."
  else
    note "Gateway address ${lan}:${port} is published by ${NET}."
  fi
  # The new holder first restarts the gateway and roles in its network; let it finish so it cannot restart
  # the gateway while setup has it stopped for an owner operation.
  for waited in {1..90}; do
    sleep 2
    [[ "$(docker logs "${NET}" 2>&1 || true)" == *"${HOLD_READY}"* ]] && break
  done
}

# Docker method: starts the network holder when it isn't running and waits until it has restarted the gateway and roles in
# its network, so this command's engine can join that network. The holder publishes the gateway port on the home address
# only; when this computer starts, Docker may start the holder before that address exists. That start fails and Docker's
# restart policy never tries again, so the gateway and every role stay down ("cannot join network namespace of a non
# running container") until something starts the holder: the keeper (ensure_keeper) or any command here.
start_holder() {
  [[ "$(docker container inspect -f '{{.State.Running}}' "${NET}" 2>/dev/null || true)" != true ]] || return 0
  local since waited error
  since="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  note "This host's network holder (${NET}) isn't running; starting it, which starts the gateway and roles again..."
  if ! docker start "${NET}" >/dev/null 2>&1; then
    # The keeper publishes the gateway on this computer's new home address within seconds when the old one is gone.
    for waited in {1..15}; do
      sleep 2
      [[ "$(docker container inspect -f '{{.State.Running}}' "${NET}" 2>/dev/null || true)" != true ]] || break
    done
    if [[ "$(docker container inspect -f '{{.State.Running}}' "${NET}" 2>/dev/null || true)" != true ]]; then
      error="$(docker container inspect -f '{{.State.Error}}' "${NET}" 2>/dev/null || true)"
      fail "${NET} doesn't start${error:+ (${error})}. Check that this computer still has the address the gateway is published on ($(holder_home)), or run setup with its address now."
    fi
  fi
  for waited in {1..60}; do
    [[ "$(docker logs --since "${since}" "${NET}" 2>&1 || true)" == *"${HOLD_READY}"* ]] && return 0
    sleep 2
  done
}

# Docker method: the keeper (martlet-host-keeper) starts the network holder again when Docker couldn't start it (see
# start_holder), so a host comes back by itself after its computer restarts. It has no published port, so Docker always
# starts it; it reaches Docker through the socket. On a Linux Docker engine it runs in the Docker host's network: there it
# also follows the computer to a new home address (follow_home) and answers desktops looking for this host (answer-finds,
# UDP 9444). Docker Desktop's host network is its own virtual machine, so there it has no network and Martlet on that
# computer follows the address instead (martlet-host home). Made by setup and update, again when the image or that changed.
ensure_keeper() {
  local socket_group="$1" current network=host
  docker_desktop && network=none
  current="$(docker container inspect -f '{{.Config.Image}} {{.HostConfig.NetworkMode}}' "${KEEPER}" 2>/dev/null || true)"
  [[ "${current}" != "${IMAGE} ${network}" ]] || return 0
  [[ -z "${current}" ]] || docker rm -f "${KEEPER}" >/dev/null 2>&1 || true
  docker run -d --name "${KEEPER}" --restart unless-stopped --network "${network}" --user 1000:1000 --group-add "${socket_group}" \
    -v /var/run/docker.sock:/var/run/docker.sock -v martlet-host-config:/var/lib/martlet/config:ro -e MARTLET_HOST_IMAGE="${IMAGE}" \
    "${IMAGE}" keep >/dev/null ||
    note "Couldn't start ${KEEPER}. After this computer restarts, run Update host if Martlet can't reach it."
}

# Whether Docker here is Docker Desktop (Windows, Mac or Linux), whose containers run in its own virtual machine.
docker_desktop() { [[ "$(docker info --format '{{.OperatingSystem}}' 2>/dev/null || true)" == *"Docker Desktop"* ]]; }

# This computer's home address now, seen from the Docker host's network: the private IPv4 it reaches the internet from, or
# else its first private IPv4 that isn't a container bridge, VPN or other virtual adapter.
home_address() {
  local address
  address="$(ip -4 route get 1.1.1.1 2>/dev/null | sed -n 's/.* src \([0-9.]*\).*/\1/p' | head -n 1)"
  if [[ ! "${address}" =~ ^(10\.|192\.168\.|172\.(1[6-9]|2[0-9]|3[01])\.)[0-9.]+$ ]]; then
    address="$(ip -4 -o addr show scope global 2>/dev/null |
      awk '$2 !~ /^(docker|br-|veth|virbr|cni|flannel|cali|vxlan|tailscale|wg|zt|tun|tap)/ {print $4}' | cut -d/ -f1 |
      grep -E '^(10\.|192\.168\.|172\.(1[6-9]|2[0-9]|3[01])\.)' | head -n 1 || true)"
  fi
  printf '%s' "${address}"
}

# Keeper, in the Docker host's network: when this computer no longer has the home address the network holder publishes the
# gateway on (a restart or a new DHCP lease gave it another one), publish the gateway on the address it has now. Only the
# holder changes: the gateway's configuration, name (its paired address), key, approval and pairings stay, and desktops
# find the host at the new address by themselves (docs/NETWORK.md, "When a computer gets a new address").
follow_home() {
  local binding current port home
  binding="$(holder_binding)"
  # No holder, or one that publishes on every address (outside access): nothing to follow.
  [[ "${binding}" =~ ^([0-9.]+):([0-9]+)$ ]] || return 0
  current="${BASH_REMATCH[1]}"; port="${BASH_REMATCH[2]}"
  ip -4 -o addr show 2>/dev/null | awk '{print $4}' | cut -d/ -f1 | grep -qxF "${current}" && return 0
  home="$(home_address)"
  [[ -n "${home}" && "${home}" != "${current}" ]] || return 0
  note "This computer's home address is ${home} now (it was ${current}), so the gateway is published there; pairings stay."
  ( ensure_holder "${home}" "${port}" 0 "$(stat -c %g /var/run/docker.sock)" "$(holder_front)" ) ||
    note "Couldn't publish the gateway on ${home} yet; trying again in 10 seconds."
}

# Docker method launcher: runs with the Docker socket (docker run -u 0 -v /var/run/docker.sock:...).
# "setup" first creates the network holder (martlet-host-net), which publishes the gateway port on the Docker
# host's LAN address and owns the loopback shared by the gateway and every role container. Every command then
# runs in a fresh engine container as the unprivileged martlet user inside that network.
docker_launch() {
  docker info >/dev/null 2>&1 ||
    fail "The Docker socket is not available. Run: docker run --rm -it -u 0 -v /var/run/docker.sock:/var/run/docker.sock ... martlet-host $1"
  local socket_group; socket_group="$(stat -c %g /var/run/docker.sock)"
  IMAGE="${MARTLET_HOST_IMAGE:-$(docker container inspect -f '{{.Config.Image}}' "$(hostname)" 2>/dev/null || printf 'martlet-host:latest')}"
  if [[ -z "${MARTLET_HOST_ID:-}" ]]; then
    MARTLET_HOST_ID="$(host_id_for "$(computer_name)")"
    export MARTLET_HOST_ID
  fi
  if [[ "$1" == setup ]]; then
    local lan="${MARTLET_HOST_ADDRESS:-}" port="${MARTLET_HOST_PORT:-9443}" wide=0
    [[ "${lan}" =~ ^(10\.|192\.168\.|172\.(1[6-9]|2[0-9]|3[01])\.)[0-9.]+$ && "${port}" =~ ^[0-9]{2,5}$ ]] ||
      fail "Add -e MARTLET_HOST_ADDRESS=<the Docker host's private LAN IPv4> (and optionally -e MARTLET_HOST_PORT=9443)."
    # A holder that publishes on every address (the owner set outside addresses) keeps doing so.
    [[ "$(holder_binding)" == :* ]] && wide=1
    ensure_holder "${lan}" "${port}" "${wide}" "${socket_group}" "$(holder_front)"
    docker volume create martlet-host-config >/dev/null
    docker volume create martlet-host-private >/dev/null
  elif [[ "$1" == exposure && $# -gt 1 ]] && docker container inspect "${NET}" >/dev/null 2>&1; then
    # Outside addresses make the holder publish the gateway on every address of the Docker host, so an overlay network such
    # as Tailscale reaches it on the Docker host's own overlay address; clearing them publishes on the home address only.
    local home wide arg front previous=""
    home="$(holder_home)"
    [[ -n "${home}" ]] || fail "This host's network holder publishes no address. Run 'setup' first."
    wide=0; [[ "$(holder_binding)" == :* ]] && wide=1
    front="$(holder_front)"
    for arg in "${@:2}"; do
      case "${arg}" in
        --outside) wide=1 ;;
        --clear-outside) wide=0 ;;
      esac
      # The front door follows --front-door <port>|off (docs/OPENAI_ENDPOINT.md).
      if [[ "${previous}" == --front-door ]]; then
        if [[ "${arg}" == off ]]; then front=""
        elif [[ "${arg}" =~ ^[0-9]{4,5}$ ]]; then front="${arg}"
        else fail "--front-door takes a port (for example 9445) or off."; fi
      fi
      previous="${arg}"
    done
    ensure_holder "${home%:*}" "${home##*:}" "${wide}" "${socket_group}" "${front}"
  elif [[ "$1" == home ]]; then
    # Martlet on a Docker Desktop host moves the published gateway to the computer's new home address (there the keeper
    # can't see the computer's addresses). Only the holder changes; the gateway's name, key and approval stay.
    local lan="${MARTLET_HOST_ADDRESS:-}" home
    [[ "${lan}" =~ ^(10\.|192\.168\.|172\.(1[6-9]|2[0-9]|3[01])\.)[0-9.]+$ ]] ||
      fail "Add -e MARTLET_HOST_ADDRESS=<this computer's private LAN IPv4>."
    home="$(holder_home)"
    [[ -n "${home}" ]] || fail "This host's network holder publishes no address. Run 'setup' first."
    if [[ "$(holder_binding)" == :* ]]; then
      note "The gateway is published on every address of this computer (outside access), so it already answers at ${lan}."
    elif [[ "${home%:*}" == "${lan}" ]]; then
      note "The gateway is published on ${lan} already."
    else
      note "This computer's home address is ${lan} now (it was ${home%:*}), so the gateway is published there; pairings stay."
      ensure_holder "${lan}" "${home##*:}" 0 "${socket_group}" "$(holder_front)"
    fi
  fi
  docker container inspect "${NET}" >/dev/null 2>&1 || fail "Run 'setup' first."
  start_holder
  [[ "$1" != setup && "$1" != update ]] || ensure_keeper "${socket_group}"
  local tty=(-i); [[ -t 0 && -t 1 ]] && tty=(-it)
  # A recognizable name (in docker ps, and so the engine can tell itself from other sessions using the gateway state).
  local engine_name; engine_name="martlet-host-$1-$(date -u +%Y%m%d-%H%M%S)-${RANDOM}"
  # --log-driver none: engine output (which can include a one-use pairing code) is streamed, never logged by Docker.
  exec docker run --rm "${tty[@]}" --name "${engine_name}" --log-driver none --user 1000:1000 --group-add "${socket_group}" --network "container:${NET}" \
    -v /var/run/docker.sock:/var/run/docker.sock \
    -v martlet-host-config:/var/lib/martlet/config -v martlet-host-private:/var/lib/martlet/private \
    -e MARTLET_ENGINE=inner -e MARTLET_ENGINE_NAME="${engine_name}" -e MARTLET_HOST_IMAGE="${IMAGE}" -e MARTLET_HOST_ID -e MARTLET_COMPUTER_NAME -e MARTLET_ASSUME_YES \
    -e MARTLET_LOCK_WAIT -e MARTLET_HOST_ADDRESS -e MARTLET_HOST_PORT "${IMAGE}" "$@"
}

# The network holder's process. Containers that share a network namespace are not restarted in order after a
# reboot, so whenever the holder starts it restarts the gateway and role containers that joined it.
cmd_hold() {
  [[ "${MODE}" == docker ]] || fail "hold is only used by the Docker method."
  trap 'exit 0' TERM INT
  sleep 3
  local id="${HOSTNAME}" container mode
  while IFS= read -r container; do
    mode="$(docker container inspect -f '{{.HostConfig.NetworkMode}}' "${container}" 2>/dev/null || true)"
    if [[ "${mode}" == "container:${NET}" || ( -n "${id}" && "${mode}" == "container:${id}"* ) ]]; then
      docker restart "${container}" >/dev/null 2>&1 && note "Restarted ${container} in the gateway network." || true
    fi
  done < <(docker ps -aq 2>/dev/null || true)
  note "${HOLD_READY}"
  while true; do sleep 3600 & wait $!; done
}

# The keeper's process (ensure_keeper): every 10 seconds, starts the network holder when it exists but isn't running. Docker
# Engine records why its start failed (.State.Error); Docker Desktop records nothing when the address isn't there, so any
# holder that isn't running counts. Martlet never stops the holder (setup replaces it); to keep this host stopped, stop
# martlet-host-keeper first. The holder then restarts the gateway and roles in its network.
cmd_keep() {
  [[ "${MODE}" == docker ]] || fail "keep is only used by the Docker method."
  local state why said="" answering="" answer_started=-60 host_id follow=0
  trap '[[ -z "${answering}" ]] || kill "${answering}" 2>/dev/null; exit 0' TERM INT
  # In the Docker host's network (a Linux Docker engine; see ensure_keeper) it also follows a new home address and answers
  # desktops looking for this host.
  [[ "$(docker container inspect -f '{{.HostConfig.NetworkMode}}' "${KEEPER}" 2>/dev/null || true)" == host ]] && follow=1
  while true; do
    state="$(docker container inspect -f '{{.State.Status}}|{{.State.Error}}' "${NET}" 2>/dev/null || true)"
    if [[ "${state}" == exited\|* || "${state}" == created\|* ]]; then
      why="${state#*|}"
      if docker start "${NET}" >/dev/null 2>&1; then
        if [[ -n "${why}" ]]; then note "Started ${NET} again; Docker couldn't start it: ${why}"
        else note "Started ${NET} again; it wasn't running."; fi
        said=""
      elif [[ "${said}" != "${state}" ]]; then
        why="$(docker container inspect -f '{{.State.Error}}' "${NET}" 2>/dev/null || true)"
        note "${NET} doesn't start yet${why:+ (${why})}; trying again every 10 seconds."
        said="${state}"
      fi
    fi
    if [[ ${follow} == 1 ]]; then
      follow_home
      if { [[ -z "${answering}" ]] || ! kill -0 "${answering}" 2>/dev/null; } && (( SECONDS - answer_started >= 60 )); then
        host_id="$(settings_value host_id 2>/dev/null || true)"
        if [[ "${host_id}" =~ ^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$ ]]; then
          "${DOTNET}" "${GW_APP}/Martlet.Gateway.Host.Linux.dll" answer-finds --host-id "${host_id}" &
          answering=$!
          answer_started=${SECONDS}
        fi
      fi
    fi
    sleep 10 & wait $!
  done
}

# Docker method, inside the network holder's namespace after the launcher moved it (docker_launch): says where it is now.
cmd_home() {
  [[ "${MODE}" == docker ]] || fail "home is only used by the Docker method: a native gateway follows its computer's address by itself."
  note "The gateway is published on $(holder_home) (home address). Your computers find it there by themselves; pairings stay."
}

cmd_serve() {
  [[ "${MODE}" == docker ]] || fail "serve is only used by the Docker method's gateway container."
  exec dotnet "${GW_APP}/Martlet.Gateway.Host.Linux.dll" serve --config "${GW_CONFIG}"
}
