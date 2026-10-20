# shellcheck shell=bash
# martlet-host module (deploy/host/lib/service.sh), sourced by deploy/host/martlet-host; it is not run by itself.
# The gateway and its service (systemd user service or Docker container): its configuration,
# stop and start, the service approval and other martlet-host sessions.

# ------------------------------------------------------------------ gateway and its service (per method)
gateway() {
  if [[ "${MODE}" == docker ]]; then dotnet "${GW_APP}/Martlet.Gateway.Host.Linux.dll" "$@"
  else DOTNET_ROOT="${HOME}/.dotnet" "${DOTNET}" "${GW_APP}/Martlet.Gateway.Host.Linux.dll" "$@"; fi
}

settings_value() { conf_value "${CONF}/host.env" "$1"; }

# host.json is generated from the host settings plus one fragment per installed role.
render_config() {
  local roles=() file kind endpoint model slots binding
  for file in "${CONF}/roles"/*.role; do
    [[ -e "${file}" ]] || continue
    kind="$(conf_value "${file}" kind)"
    [[ -n "${kind}" ]] || continue
    endpoint="$(conf_value "${file}" endpoint)"; model="$(conf_value "${file}" model)"; slots="$(conf_value "${file}" slots)"
    [[ "${kind}" =~ ^[a-z0-9-]+$ && "${endpoint}" =~ ^http://127\.0\.0\.1:[0-9]{1,5}/$ && "${model}" =~ ^[A-Za-z0-9][A-Za-z0-9._:/-]*$ &&
       ( -z "${slots}" || "${slots}" =~ ^[1-9]$ ) ]] ||
      fail "Invalid role record ${file}."
    if [[ -n "${slots}" && "${slots}" != 1 ]]; then
      roles+=("{\"kind\":\"${kind}\",\"endpoint\":\"${endpoint}\",\"model\":\"${model}\",\"slots\":${slots}}")
    else
      roles+=("{\"kind\":\"${kind}\",\"endpoint\":\"${endpoint}\",\"model\":\"${model}\"}")
    fi
  done
  local joined; joined="$(IFS=,; printf '%s' "${roles[*]:-}")"
  # In Docker the port is published into the container's namespace, so the gateway listens there.
  binding="privateIp"; [[ "${MODE}" == docker ]] && binding="published"
  printf '{"schemaVersion":1,"hostId":"%s","stateDirectory":"%s","storageBackend":"linuxServicePermissions","binding":{"mode":"%s","origin":"https://%s:%s"},"serviceUid":%s,"serviceGid":%s,"roles":[%s]}\n' \
    "$(settings_value host_id)" "${GW_STATE}" "${binding}" "$(settings_value lan_ip)" "$(settings_value port)" "$(id -u)" "$(id -g)" "${joined}"
}

CONFIG_CHANGED=0
write_config() {
  mkdir -p "${GW_CONF_DIR}"; chmod 700 "${CONF}" "${GW_CONF_DIR}"
  write_gpus
  local next; next="$(render_config)"
  CONFIG_CHANGED=0
  if [[ -f "${GW_CONFIG}" && "$(cat "${GW_CONFIG}")" == "${next}" ]]; then return 0; fi
  ( umask 077; printf '%s\n' "${next}" > "${GW_CONFIG}.tmp" && mv -f "${GW_CONFIG}.tmp" "${GW_CONFIG}" )
  gateway validate --config "${GW_CONFIG}" || fail "The generated gateway configuration was rejected."
  CONFIG_CHANGED=1
}

# gpus.json beside host.json: which graphics card each installed route role runs on, for the gateway's GPU priority (live turn
# first): a role pinned to one card names its UUID, a role added to run on the processor says "cpu"; a role on every card is
# left out (the gateway counts it as the whole host). It is not part of the approved configuration, so moving a role to another
# card needs no new approval and Martlet's automatic updates keep running; the gateway restarts with it (served_stamp).
GPUS_FILE="${GW_CONF_DIR}/gpus.json"
render_gpus() {
  local entries=() file kind place
  for file in "${CONF}/roles"/*.role; do
    [[ -e "${file}" ]] || continue
    kind="$(conf_value "${file}" kind)"
    [[ "${kind}" =~ ^[a-z0-9-]+$ ]] || continue
    place="$(role_place "$(basename "${file}" .role)")"
    if [[ "${place}" =~ ^GPU-[A-Za-z0-9-]+$ || "${place}" == cpu ]]; then entries+=("\"${kind}\":[\"${place}\"]"); fi
  done
  local joined; joined="$(IFS=,; printf '%s' "${entries[*]:-}")"
  printf '{"schemaVersion":1,"roles":{%s}}\n' "${joined}"
}

write_gpus() {
  local next; next="$(render_gpus)"
  if [[ -f "${GPUS_FILE}" && "$(cat "${GPUS_FILE}")" == "${next}" ]]; then return 0; fi
  ( umask 077; printf '%s\n' "${next}" > "${GPUS_FILE}.tmp" && mv -f "${GPUS_FILE}.tmp" "${GPUS_FILE}" )
}

service_stop() {
  if [[ "${MODE}" == docker ]]; then
    # Something else (the network holder's restart pass, the restart policy) can start it again right after a stop.
    local attempt
    for attempt in 1 2 3 4 5 6 7 8 9 10; do
      docker stop "${SERVICE}" >/dev/null 2>&1 || true
      sleep 1
      [[ "$(docker container inspect -f '{{.State.Running}}' "${SERVICE}" 2>/dev/null || true)" == true ]] || return 0
    done
  else systemctl --user stop "${SERVICE}.service" 2>/dev/null || true; fi
}

# Running containers that use this host's gateway state (the martlet-host-private volume), except this session, one per
# line: name, minutes running, image and command, the gateway command inside (or none), and "service" or "session"
# (tab-separated). In the Docker method an engine whose console window was closed keeps running, because its terminal
# never ends, so it can hold the gateway state for good.
STALE_MINUTES=10
other_sessions() {
  [[ "${MODE}" == docker ]] || return 0
  local id name started now kind tool what
  now="$(date -u +%s)"
  while read -r id name; do
    [[ -n "${id}" && "${name}" != "${MARTLET_ENGINE_NAME:-}" ]] || continue
    started="$(docker container inspect -f '{{.State.StartedAt}}' "${id}" 2>/dev/null || true)"
    started="${started%%.*}"; started="$(date -u -d "${started%Z}Z" +%s 2>/dev/null || printf '%s' "${now}")"
    what="$(docker container inspect -f '{{.Config.Image}} {{join .Config.Cmd " "}}' "${id}" 2>/dev/null || true)"
    tool="$(docker top "${id}" 2>/dev/null | grep -m1 -oE 'Martlet\.Gateway\.Host\.Linux\.dll [a-z-]+' | cut -d' ' -f2 || true)"
    kind=session; [[ "${name}" == "${SERVICE}" ]] && kind=service
    printf '%s\t%s\t%s\t%s\t%s\n' "${name}" "$(( (now - started) / 60 ))" "${what//$'\t'/ }" "${tool:-none}" "${kind}"
  done < <(docker ps --no-trunc --filter volume=martlet-host-private --format '{{.ID}} {{.Names}}' 2>/dev/null || true)
}

# Names what else uses this host's gateway state when an owner operation finds it busy, and stops abandoned Martlet
# sessions (running ${STALE_MINUTES}+ minutes with a gateway process that holds the state) so this one can finish. Newer
# sessions may still be in use by someone and are only named, and so are changes running alongside this one (an add
# downloading for an hour holds no gateway state). Returns 0 when it stopped one.
release_gateway_state() {
  if [[ "${MODE}" != docker ]]; then
    note "Gateway processes on this host:"
    pgrep -af 'Martlet\.Gateway\.Host\.Linux\.dll' | sed 's/^/  /' || note "  (none found)"
    journal "gateway state busy; gateway processes: $(pgrep -f 'Martlet\.Gateway\.Host\.Linux\.dll' | tr '\n' ' ' || true)"
    return 1
  fi
  local sessions=() line name minutes what tool kind stale=()
  mapfile -t sessions < <(other_sessions)
  if [[ ${#sessions[@]} -eq 0 ]]; then
    note "No other container uses this host's gateway state now."
    journal "gateway state busy; no other container uses it"
    return 1
  fi
  note "Other containers using this host's gateway state:"
  for line in "${sessions[@]}"; do
    IFS=$'\t' read -r name minutes what tool kind <<< "${line}"
    note "  ${name} (${kind}): ${what}; running ${minutes} min; gateway process: ${tool}"
    journal "gateway state busy; ${name} (${kind}): ${what}; running ${minutes} min; gateway process: ${tool}"
    if [[ "${kind}" == session && "${tool}" != none ]] && (( minutes >= STALE_MINUTES )) && ! running_alongside "${name}"; then
      stale+=("${name}")
    fi
  done
  [[ ${#stale[@]} -gt 0 ]] || return 1
  note "Running ${STALE_MINUTES}+ minutes, ${stale[*]} is most likely left over from a closed console window and waits forever."
  if ! assume_yes && [[ ! -t 0 || ! -t 2 ]]; then note "Stop it with: docker stop ${stale[*]}"; return 1; fi
  ask "Stop ${stale[*]}?" || return 1
  docker stop -t 3 "${stale[@]}" >/dev/null 2>&1 || true
  journal "stopped abandoned sessions: ${stale[*]}"
  note "Stopped ${stale[*]}."
  return 0
}

# Whether engine session $1 is a change running alongside this one that nobody left open in a console.
running_alongside() {
  local scope state words how started session
  while IFS=$'\t' read -r scope state words how started session; do
    [[ "${session}" == "$1" && "${how}" != "in a console" ]] && return 0
  done < <(holders)
  return 1
}

# Runs a short owner operation (owner-init, owner-approve) with the service stopped. The gateway state allows one
# process at a time: while a just-stopped gateway is still letting go of it (StoreBusy) this waits and tries again, and
# it names (and stops, when abandoned) any other Martlet session that holds it.
owner_operation() {
  local attempt out status
  for attempt in 1 2 3 4 5 6; do
    service_stop
    status=0
    out="$(gateway "$@" 2>&1)" || status=$?
    printf '%s\n' "${out}"
    [[ ${status} != 0 && "${out}" == *StoreBusy:* ]] || return ${status}
    journal "$1: gateway state busy (attempt ${attempt})"
    if release_gateway_state; then continue; fi
    [[ ${attempt} -lt 6 ]] || break
    note "Another process still holds this host's gateway state; waiting for it to let go, then trying again..."
    sleep 5
  done
  if [[ "${MODE}" == docker ]]; then
    note "Something else still holds this host's gateway state (listed above). Finish or close that Martlet session, or stop it with: docker stop <name>"
  else
    note "Something else still holds this host's gateway state (listed above). Finish or close that martlet-host session, then try again."
  fi
  return ${status}
}

service_start() {
  local round attempt
  for round in 1 2; do
    if [[ "${MODE}" == docker ]]; then
      # Recreated each time so an updated image takes effect; state lives in the named volumes.
      docker rm -f "${SERVICE}" >/dev/null 2>&1 || true
      docker run -d --name "${SERVICE}" --restart unless-stopped --network "container:${NET}" --user 1000:1000 \
        -v martlet-host-config:/var/lib/martlet/config -v martlet-host-private:/var/lib/martlet/private \
        "${IMAGE}" serve >/dev/null
    else
      systemctl --user restart "${SERVICE}.service"
    fi
    for attempt in 1 2 3 4 5 6; do
      sleep 2
      if gateway health --config "${GW_CONFIG}" >/dev/null 2>&1; then
        ( umask 077; served_stamp > "${SERVED_FILE}" )
        note "Gateway healthy."
        return
      fi
    done
    # An abandoned session that holds the gateway state keeps the service from opening it.
    [[ ${round} == 1 ]] && release_gateway_state || break
  done
  if [[ "${MODE}" == docker ]]; then fail "The gateway is not healthy. Check: docker logs ${SERVICE}"
  else fail "The gateway is not healthy. Check: journalctl --user -u ${SERVICE}"; fi
}

# Changing host.json invalidates the unattended-start approval by design; the owner re-approves (console or --yes).
reapprove() {
  service_stop
  if assume_yes; then
    note "The gateway configuration changed; renewing its unattended-start approval for exactly this configuration."
    owner_operation owner-approve --config "${GW_CONFIG}" || fail "The gateway approval was not renewed."
  else
    cat <<'TEXT'

The gateway configuration changed, so its unattended-start approval must be renewed on this host.
In the gateway console confirm opening with: yes
Then type:  approve-service  (confirm: yes), then  stop  (confirm: yes)
TEXT
    gateway admin --config "${GW_CONFIG}"
  fi
  service_start
}

approval_matches() { [[ "$(gateway status --config "${GW_CONFIG}" 2>/dev/null || true)" == *'"serviceApproval":"matching"'* ]]; }

# What a gateway opens: host.json, the machine report (without the time it was collected), where each role runs (gpus.json)
# and the gateway itself (the engine image in Docker, the published Martlet assemblies natively). service_start records it once
# the gateway is healthy.
SERVED_FILE="${CONF}/gateway.served"
served_stamp() {
  {
    cat "${GW_CONFIG}" 2>/dev/null || true
    sed 's/"collected_at":"[^"]*",//' "${MACHINE_FILE}" 2>/dev/null || true
    cat "${GPUS_FILE}" 2>/dev/null || true
    if [[ "${MODE}" == docker ]]; then docker image inspect -f '{{.Id}}' "${IMAGE}" 2>/dev/null || true
    else cat "${GW_APP}"/Martlet.*.dll 2>/dev/null || true; fi
  } | sha256sum | cut -d' ' -f1
}

# Whether the running gateway already serves exactly what is on disk: approved, healthy and started from the same host.json,
# machine report, gpus.json and gateway. Then a change that touches none of them (adding a role again with the same model and
# the same card or processor, running setup again) leaves it running, so the other roles it relays never drop out. Moving a
# role to another card or between the GPU and the CPU changes gpus.json, so the gateway restarts with it (no new approval).
gateway_current() {
  [[ -s "${SERVED_FILE}" && "$(cat "${SERVED_FILE}")" == "$(served_stamp)" ]] || return 1
  if [[ "${MODE}" == docker ]]; then
    # Still in the current network holder's namespace (setup may have replaced the holder; see role_detached).
    local holder mode
    holder="$(docker container inspect -f '{{.Id}}' "${NET}" 2>/dev/null)" || return 1
    mode="$(docker container inspect -f '{{.HostConfig.NetworkMode}}' "${SERVICE}" 2>/dev/null)" || return 1
    [[ "${mode}" == "container:${holder}" || "${mode}" == "container:${NET}" ]] || return 1
  fi
  approval_matches || return 1
  gateway health --config "${GW_CONFIG}" >/dev/null 2>&1
}

# Brings the gateway in line with what is on disk: renews its approval when host.json changed (CONFIG_CHANGED from
# write_config), restarts it when anything else it opens changed, and otherwise leaves it running.
refresh_gateway() {
  if [[ ${CONFIG_CHANGED} == 1 ]] || ! approval_matches; then reapprove
  elif gateway_current; then note "The gateway already serves this; it keeps running (no interruption for the other roles)."
  else service_start; fi
}
