# shellcheck shell=bash
# martlet-host module (deploy/host/lib/upkeep.sh), sourced by deploy/host/martlet-host; it is not run by itself.
# Keeping the host current: the commands machine, update, warm and status.

cmd_machine() {
  require_setup
  say "Collecting this machine's hardware for paired desktops"
  collect_machine fresh
  show_machine
  restart_gateway_for_machine_report
}

# Replaces the gateway with this engine's Martlet version (the image this session runs from, or this checkout natively),
# keeping the host identity, pairings, roles and their data. It asks nothing unless the new version changes host.json,
# which renews the service approval in the gateway console like any other configuration change. Without a terminal
# (Martlet's automatic host updates) that case stops before changing anything so the owner can run it in a console.
cmd_update() {
  require_setup
  if [[ "$(render_config)" != "$(cat "${GW_CONFIG}" 2>/dev/null)" ]] || ! approval_matches; then
    [[ -t 0 && -t 1 ]] || assume_yes || fail "This update changes the gateway configuration, which must be approved on this host. Run 'martlet-host update' in a console (Martlet: Update host on its Devices card)."
  fi
  if [[ "${MODE}" == native ]]; then
    [[ -f "${REPO}/src/Martlet.Gateway.Host.Linux/Martlet.Gateway.Host.Linux.csproj" ]] || fail "Run from a Martlet source checkout."
    say "Updating the gateway from ${REPO} ($(git -C "${REPO}" describe --tags --always 2>/dev/null || printf 'source checkout'))"
    require_dotnet
    secure_data_parents
    rm -rf "${GW_APP}.next"
    publish_gateway "${GW_APP}.next"
    service_stop
    rm -rf "${GW_APP}.previous"
    mv "${GW_APP}" "${GW_APP}.previous"
    mv "${GW_APP}.next" "${GW_APP}"
  else
    say "Updating the gateway to image ${IMAGE}"
  fi
  collect_machine
  apply_config
  refresh_installed_role_files
  reconnect_roles
  if [[ "${MODE}" == docker ]]; then
    # Older martlet-host images still in use (for example by the network holder) are kept.
    docker image ls martlet-host --format '{{.Repository}}:{{.Tag}}' 2>/dev/null | grep -vxF "${IMAGE}" |
      xargs -r docker image rm >/dev/null 2>&1 || true
  else
    rm -rf "${GW_APP}.previous"
  fi
  say "Host updated. Paired desktops see its new version on Check connection."
}

# Brings this host's installed roles up and warm (Martlet runs it when this PC becomes a host PC or starts as one, after it
# starts Docker Desktop): starts the gateway and every installed role, waits until each listens, then runs its warm steps
# (role.conf warm=<service>|<command>), which load its model so the first request after a restart doesn't wait for it.
# It installs, downloads and reconfigures nothing. A role another change is working on now is left to that change; one
# that doesn't come up or warm up is reported while the others carry on, and the run then exits 1.
cmd_warm() {
  require_setup
  require_docker
  if [[ "${MODE}" == docker ]]; then
    if [[ "$(docker container inspect -f '{{.State.Running}}' "${SERVICE}" 2>/dev/null || true)" != true ]]; then
      note "Starting the gateway..."
      docker start "${SERVICE}" >/dev/null 2>&1 || note "The gateway didn't start. Check: docker logs ${SERVICE}"
    fi
  elif ! systemctl --user is-active --quiet "${SERVICE}.service" 2>/dev/null; then
    note "Starting the gateway..."
    systemctl --user start "${SERVICE}.service" 2>/dev/null || note "The gateway didn't start. Check: journalctl --user -u ${SERVICE}"
  fi
  local role slot count=0 warmed=() failed=()
  while IFS= read -r role <&3; do
    [[ -n "${role}" ]] || continue
    count=$(( count + 1 ))
    say "Role ${role}"
    if [[ ! -f "${ROLES_DIR}/${role}/role.conf" || ! -f "${PROJECTS}/${role}/compose.yaml" ]]; then
      note "This Martlet version has no files for ${role}; leaving it as it is."
      continue
    fi
    if [[ ${LOCKING} == 1 ]]; then
      slot="$(role_slot "${role}")"
      ( umask 077; : >> "${LOCKS}/${slot}.lock" ) 2>/dev/null || true
      if ! exec 6>>"${LOCKS}/${slot}.lock" || ! flock -n 6; then
        exec 6>&- 2>/dev/null || true
        note "Another change is working on ${role} now ($(holder_text "role:${slot}")); that change starts it."
        continue
      fi
    fi
    if warm_role "${role}"; then warmed+=("${role}"); else failed+=("${role}"); fi
    if [[ ${LOCKING} == 1 ]]; then exec 6>&-; fi
  done 3< <(installed_roles)
  if [[ ${count} == 0 ]]; then say "No roles are installed on this host; nothing to warm up."; return 0; fi
  if [[ ${#warmed[@]} -gt 0 ]]; then say "Running and warmed up: ${warmed[*]}."; fi
  if [[ ${#failed[@]} -gt 0 ]]; then fail "Not ready: ${failed[*]} (the output above shows why)."; fi
}

# Starts installed role $1 (its containers as it was added), waits until it listens and runs its warm steps. Returns 1 when
# it didn't come up or warm up.
warm_role() {
  local role="$1" conf record kind var value port network address minutes attempt ready=0 line service step key words
  local safe='^[A-Za-z0-9 ._:/=-]+$'
  conf="${ROLES_DIR}/${role}/role.conf"; record="$(installed_file "${role}")"
  # The choices it was added with pick its role.conf sections and fill {VAR} in its warm steps.
  ROLE_CHOSEN=()
  while IFS='|' read -r kind var value; do
    if [[ "${kind}" == choice ]]; then ROLE_CHOSEN["${var}"]="${value}"; fi
  done < <(role_current "${role}")
  note "Starting ${role} (containers already running are left as they are)..."
  if ! role_compose "${role}" up -d --no-build < /dev/null; then
    note "${role} didn't start (the output above shows why). Add it again in Martlet to repair it."
    return 1
  fi
  port="$(conf_value "${record}" port)"
  [[ -n "${port}" ]] || port="$(conf_value "${record}" endpoint | sed -E 's#.*:([0-9]+)/$#\1#')"
  network="$(conf_value "${record}" network)"; address="$(role_ready_address "${network}")"
  minutes="$(conf_value "${conf}" ready_timeout_minutes)"; [[ "${minutes}" =~ ^[0-9]{1,3}$ ]] || minutes=10
  if [[ "${port}" =~ ^[0-9]{1,5}$ ]]; then
    for attempt in $(seq 1 $(( minutes * 6 ))); do
      if (exec 3<>"/dev/tcp/${address}/${port}") 2>/dev/null; then ready=1; break; fi
      if (( attempt % 6 == 0 )); then note "Still waiting for ${role} on ${address}:${port} ($(( attempt / 6 )) min)..."; fi
      sleep 10
    done
    if [[ ${ready} != 1 ]]; then
      note "${role} didn't answer on ${address}:${port} within ${minutes} minutes. Inspect: martlet-host status"
      return 1
    fi
    note "${role} listens on ${address}:${port}."
  fi
  while IFS= read -r line <&3; do
    [[ -n "${line}" ]] || continue
    service="${line%%|*}"; step="${line#*|}"
    for key in "${!ROLE_CHOSEN[@]}"; do step="${step//\{${key}\}/${ROLE_CHOSEN[${key}]}}"; done
    if [[ ! "${service}" =~ ^[a-z0-9][a-z0-9_-]*$ || ! "${step}" =~ ${safe} ]]; then
      note "Skipping an unsafe warm entry in ${role}'s role.conf."
      continue
    fi
    read -r -a words <<< "${step}"
    note "Running in ${service}: ${step}"
    if ! role_compose "${role}" exec -T "${service}" "${words[@]}" < /dev/null; then
      note "'${step}' didn't finish in ${role}; its first request loads the model instead."
      return 1
    fi
  done 3< <(conf_values "${conf}" warm)
  return 0
}

cmd_status() {
  require_setup
  say "Gateway ($(settings_value host_id) at https://$(settings_value lan_ip):$(settings_value port), ${MODE} method)"
  if [[ "${MODE}" == docker ]]; then docker ps --filter "name=^${SERVICE}$" --filter "name=^${NET}$" --format '{{.Names}}: {{.Status}}' || true
  else systemctl --user is-active "${SERVICE}.service" || true; fi
  gateway health --config "${GW_CONFIG}" || true
  if engine_busy; then
    note "Busy now: $(holder_text all). Changes to other roles run alongside; one that collides with these (the same role, setup or update) waits, and Martlet's automatic updates try again later."
    holder_hint
  fi
  local waiting; waiting="$(holder_text waiting)"
  [[ -z "${waiting}" ]] || note "Waiting to start: ${waiting}."
  local name minutes what tool kind
  while IFS=$'\t' read -r name minutes what tool kind; do
    [[ "${kind}" == session ]] || continue
    note "Other martlet-host session: ${name}: ${what}; running ${minutes} min; gateway process: ${tool}"
    if (( minutes >= STALE_MINUTES )) && ! running_alongside "${name}"; then
      note "  Most likely left over from a closed console window; stop it with: docker stop ${name}"
    fi
  done < <(other_sessions)
  if [[ -f "${JOURNAL}" ]]; then
    note "Recent martlet-host runs on this host (${JOURNAL}):"
    tail -n 12 "${JOURNAL}" | sed 's/^/  /'
  fi
  show_machine
  local file role port network address place
  for file in "${CONF}/roles"/*.role; do
    [[ -e "${file}" ]] || continue
    role="$(basename "${file}" .role)"
    port="$(conf_value "${file}" port)"; network="$(conf_value "${file}" network)"
    [[ -n "${port}" ]] || port="$(conf_value "${file}" endpoint | sed -E 's#.*:([0-9]+)/$#\1#')"
    address="$(role_ready_address "${network}")"
    say "Role ${role}"
    if [[ "${port}" =~ ^[0-9]{1,5}$ ]] && (exec 3<>"/dev/tcp/${address}/${port}") 2>/dev/null; then
      note "listening on ${address}:${port}"
    else
      note "NOT listening on ${address}:${port:-unknown}"
    fi
    if [[ "${MODE}" == docker ]] && role_detached "${role}"; then
      note "Not in the gateway's network (missing, or left on the network an earlier setup replaced), so the gateway can't reach it. Reconnect it: martlet-host update (Update host in Martlet)"
    fi
    place="$(role_place "${role}")"
    case "${place}" in
      cpu) note "runs on the processor" ;;
      all) note "runs on every graphics card (not pinned to one)" ;;
      GPU-*,*) note "runs on graphics cards ${place//,/, } (one model split across them)" ;;
      GPU-*) note "runs on graphics card ${place}" ;;
    esac
    role_compose "${role}" ps 2>/dev/null || true
  done
  gpu_sharing_note
}
