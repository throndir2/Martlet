# shellcheck shell=bash
# martlet-host module (deploy/host/lib/role-changes.sh), sourced by deploy/host/martlet-host; it is not run by itself.
# Adding and removing roles: a role's Compose project, its installed record, its route in
# the gateway and the commands add and remove.

# Copies a role's prepared files into its Martlet-managed named volume (works for local and remote Docker).
fill_volume() {
  local volume="$1" source="$2"
  "${DOCKER[@]}" volume create "${volume}" >/dev/null
  tar -C "${source}" -cf - . | "${DOCKER[@]}" run --rm -i -v "${volume}:/dst" "${HELPER_IMAGE}" tar -xf - -C /dst
}

role_compose() {
  local role="$1"; shift
  local project="${PROJECTS}/${role}" secret_env=() name condition line files=(-f compose.yaml)
  # Every stored secret the role declares, in any variant: Compose resolves only those its running services use.
  while IFS=$'\t' read -r condition line <&3; do
    [[ "${line%%=*}" == secret ]] || continue
    name="${line#*=}"; name="${name%%|*}"
    if [[ -s "${CONF}/secrets/${name}" ]]; then secret_env+=("${name^^}=$(cat "${CONF}/secrets/${name}")"); fi
  done 3< <(conf_lines "$(role_dir "${role}")/role.conf")
  # The variant this role was added with (Compose reads it only from the environment), or ROLE_PROFILES when given (the
  # previous variant, stopped when another is chosen). Installs from before the role had variants ran the one role.conf
  # names in profile_legacy (Audio2Face: nim; stt: whisper).
  local profiles="${ROLE_PROFILES:-}"
  [[ -n "${profiles}" ]] || profiles="$(conf_value "${project}/.env" COMPOSE_PROFILES)"
  [[ -n "${profiles}" ]] || profiles="$(conf_value "$(role_dir "${role}")/role.conf" profile_legacy)"
  secret_env+=("COMPOSE_PROFILES=${profiles}")
  # Present only when the role was added on an ARM64 host and has an ARM64 build (role.conf arm64=<overlay>).
  if [[ -f "${project}/compose.arch.yaml" ]]; then files+=(-f compose.arch.yaml); fi
  # Present only when the role was added to run on the NVIDIA GPU (role.conf gpu=optional|<overlay>).
  if [[ -f "${project}/compose.gpu.yaml" ]]; then files+=(-f compose.gpu.yaml); fi
  # Through sudo, name the variables to keep: sudo-rs (Ubuntu 25.10's sudo) ignores -E.
  local docker=("${DOCKER[@]}") names=() pair
  if [[ "${docker[0]}" == sudo ]]; then
    for pair in "${secret_env[@]}"; do names+=("${pair%%=*}"); done
    docker=(sudo "--preserve-env=$(IFS=,; printf '%s' "${names[*]}")" "${docker[@]:1}")
  fi
  ( cd "${project}" && env "${secret_env[@]}" "${docker[@]}" compose -p "martlet-${role}" --env-file .env "${files[@]}" "$@" )
}

installed_roles() {
  local file
  for file in "${CONF}/roles"/*.role; do
    [[ -e "${file}" ]] && basename "${file}" .role
  done
  return 0
}

# Docker method: whether an installed role's containers are missing or outside the current network holder's namespace.
# Docker pins network_mode container:martlet-host-net to the holder's container ID when it creates a container, so after
# setup replaces the holder (a new image or address) the role keeps running in the old namespace, which the gateway can
# no longer reach (desktops then get worker.unavailable), and the new holder's restart pass does not touch it.
role_detached() {
  local role="$1" holder container mode found=0
  [[ "$(conf_value "$(installed_file "${role}")" network)" == host ]] && return 1
  # A relay to the owner's own model app runs no containers.
  [[ -n "$(conf_value "$(installed_file "${role}")" api)" ]] && return 1
  holder="$(docker container inspect -f '{{.Id}}' "${NET}" 2>/dev/null)" || return 1
  while IFS= read -r container; do
    [[ -n "${container}" ]] || continue
    found=1
    mode="$(docker container inspect -f '{{.HostConfig.NetworkMode}}' "${container}" 2>/dev/null || true)"
    [[ "${mode}" == "container:${holder}" || "${mode}" == "container:${NET}" ]] || return 0
  done < <(docker ps -aq --filter "label=com.docker.compose.project=martlet-${role}" 2>/dev/null || true)
  [[ ${found} == 0 ]]
}

# Recreates installed roles that role_detached finds outside the gateway's network (their data volumes are kept).
reconnect_roles() {
  [[ "${MODE}" == docker ]] || return 0
  local role
  while IFS= read -r role; do
    [[ -n "${role}" ]] && role_detached "${role}" || continue
    note "Reconnecting ${role} to the gateway network (it was left on the network an earlier setup replaced)..."
    journal "reconnecting ${role} to the current network holder"
    ensure_role_timezone_env "${PROJECTS}/${role}"
    if role_compose "${role}" up -d --force-recreate --no-build >/dev/null 2>&1 ||
       role_compose "${role}" up -d --force-recreate; then
      note "Reconnected ${role}; it is starting again."
    else
      journal "could not reconnect ${role}"
      note "Could not reconnect ${role}. Add it again: martlet-host add ${role}"
    fi
  done < <(installed_roles)
}

refresh_installed_role_files() {
  local role record kind feature dir project changed gpu_entry overlay arch_overlay
  while IFS= read -r role; do
    [[ -n "${role}" ]] || continue
    record="$(installed_file "${role}")"
    kind="$(conf_value "${record}" kind)"; feature="$(conf_value "${record}" feature)"
    [[ -n "${kind}" || -n "${feature}" ]] || continue
    # A relay to the owner's own model app has no role files to refresh.
    [[ -z "$(conf_value "${record}" api)" ]] || continue
    dir="${ROLES_DIR}/${role}"; project="${PROJECTS}/${role}"; changed=0
    [[ -f "${dir}/role.conf" && -f "${dir}/compose.yaml" ]] ||
      { note "Role files for ${role} are no longer present; leaving its running containers unchanged."; continue; }
    mkdir -p "${project}"
    if [[ ! -f "${project}/compose.yaml" ]] || ! cmp -s "${dir}/compose.yaml" "${project}/compose.yaml"; then
      cp "${dir}/compose.yaml" "${project}/compose.yaml"; changed=1
    fi
    gpu_entry="$(conf_any_value "${dir}/role.conf" gpu)"; overlay="${gpu_entry#*|}"
    arch_overlay="$(conf_any_value "${dir}/role.conf" arm64)"
    if [[ -f "${project}/compose.arch.yaml" && -n "${arch_overlay}" && -f "${dir}/${arch_overlay}" ]] &&
       ! cmp -s "${dir}/${arch_overlay}" "${project}/compose.arch.yaml"; then
      cp "${dir}/${arch_overlay}" "${project}/compose.arch.yaml"; changed=1
    fi
    if [[ -f "${project}/compose.gpu.yaml" && -f "${dir}/${overlay}" ]]; then
      install_gpu_overlay "${dir}/${overlay}" "${project}/.compose.gpu.yaml.new" "$(role_gpu "${role}")"
      if cmp -s "${project}/.compose.gpu.yaml.new" "${project}/compose.gpu.yaml"; then rm -f "${project}/.compose.gpu.yaml.new"
      else mv -f "${project}/.compose.gpu.yaml.new" "${project}/compose.gpu.yaml"; changed=1; fi
    fi
    if [[ ${changed} == 1 ]]; then
      note "Refreshing ${role} role files from this Martlet version and restarting its containers."
      ensure_role_timezone_env "${project}"
      role_compose "${role}" up -d
    fi
  done < <(installed_roles)
}

# Retire steps: once the gateway relays a newly chosen model, retire=<service>|<command> runs inside that service for the
# model it replaced ({PREVIOUS}), for example unloading it from the graphics card's memory. Its download is kept, and a
# step that fails only leaves the old model as it was.
retire_previous() {
  local role="$1" conf="$2" previous="$3" model="$4" same="${5:-}" line service step words safe='^[A-Za-z0-9 ._:/=-]+$'
  # $5=same: the model is chosen again (a reload), so it is unloaded before its warm steps load it fresh.
  [[ -n "${previous}" && ( "${previous}" != "${model}" || "${same}" == same ) && "${previous}" =~ ^[A-Za-z0-9][A-Za-z0-9._:/-]*$ ]] || return 0
  while IFS= read -r line <&3; do
    [[ -n "${line}" ]] || continue
    service="${line%%|*}"; step="${line#*|}"; step="${step//\{PREVIOUS\}/${previous}}"
    [[ "${service}" =~ ^[a-z0-9][a-z0-9_-]*$ && "${step}" =~ ${safe} ]] || fail "Unsafe retire entry in role.conf."
    read -r -a words <<< "${step}"
    say "Running in ${service}: ${step}"
    role_compose "${role}" exec -T "${service}" "${words[@]}" || note "'${step}' didn't finish; ${previous} is left as it was."
  done 3< <(conf_values "${conf}" retire)
}

# Stops the roles that role $1 replaces (exclusive_others) and unpublishes them at once, so desktops never hand a job to
# an engine that no longer runs. Their data volumes and images are kept, so adding one again is quick.
release_roles() {
  local role="$1" other; shift
  for other in "$@"; do
    note "Stopping ${other} so its model leaves the graphics card's memory..."
    role_compose "${other}" down || true
  done
  with_gateway unpublish_roles "${role}" "$@"
  note "Stopped $*. Add one again to switch back; its downloads are kept."
}

unpublish_roles() {
  local role="$1" other; shift
  record_downloads
  for other in "$@"; do
    rm -f "$(installed_file "${other}")"
    journal "stopped ${other} for ${role} (one engine of its group per host)"
  done
  collect_machine
  apply_config
}

# Records role $1 as installed and publishes it: a gateway route (kind $2 on loopback port $3, model $4) or, for a
# route-less role, its feature $5 in the machine report. Runs holding the gateway lock (with_gateway).
publish_role() {
  local role="$1" kind="$2" port="$3" model="$4" feature="$5" slots="${6:-1}" api="${7:-}" path="${8:-}"
  if [[ -n "${kind}" ]]; then
    ( umask 077; printf 'kind=%s\nendpoint=http://127.0.0.1:%s/\nmodel=%s\n' "${kind}" "${port}" "${model}" > "$(installed_file "${role}")"
      # A role that runs several requests at once (the deep-thinking role's Ollama slots) says how many.
      if [[ "${slots}" != 1 ]]; then printf 'slots=%s\n' "${slots}" >> "$(installed_file "${role}")"; fi
      # A role that relays to the owner's own model app (external=openai) names the app's API and path.
      if [[ -n "${api}" ]]; then printf 'api=%s\npath=%s\n' "${api}" "${path}" >> "$(installed_file "${role}")"; fi )
    collect_machine
    apply_config
  else
    ( umask 077; printf 'feature=%s\nport=%s\nnetwork=host\n' "${feature}" "${port}" > "$(installed_file "${role}")" )
    collect_machine
    publish_machine_report
  fi
}

# Unpublishes role $1 (its gateway route when it had kind $2, otherwise its feature). Runs holding the gateway lock.
# Its downloads stay listed in the machine report (record_downloads), so adding it again downloads nothing new.
unpublish_role() {
  record_downloads
  rm -f "$(installed_file "$1")"
  collect_machine
  if [[ -n "$2" ]]; then apply_config; else publish_machine_report; fi
}

# A role Martlet runs nothing for (role.conf external=openai: the deep-thinking-app role). The gateway relays its route to the
# owner's own model app on this host's 127.0.0.1, at the port, API path and model its port_from, path_from and model_from
# choices name. The host service must run natively, so the gateway reaches the app. The app must answer and list the model;
# then the roles of the same exclusive group stop and the route is published.
add_external_role() {
  local role="$1" conf="$2" kind="$3" api project line port path model listing current_kind current_value
  api="$(conf_value "${conf}" external)"
  [[ "${api}" == openai && "${kind}" =~ ^[a-z0-9-]+$ ]] || fail "Invalid external entry in role.conf."
  [[ "${MODE}" == native ]] ||
    fail "${role} needs this host's Martlet service installed natively, not in Docker: in Docker the gateway can't reach the apps on this computer."
  project="${PROJECTS}/${role}"
  declare -A current_choice=() chosen=()
  local env_lines=()
  while IFS='|' read -r current_kind current_value; do
    [[ "${current_kind}" == choice ]] && current_choice["${current_value%%|*}"]="${current_value#*|}"
  done < <(role_current "${role}")
  while IFS= read -r line <&3; do
    [[ -n "${line}" ]] && ask_choice "${line}" "${current_choice[${line%%|*}]:-}"
  done 3< <(conf_values "${conf}" choice)
  port="${chosen[$(conf_value "${conf}" port_from)]:-}"; path="${chosen[$(conf_value "${conf}" path_from)]:-}"
  model="${chosen[$(conf_value "${conf}" model_from)]:-}"
  [[ "${port}" =~ ^[0-9]{2,5}$ && "${path}" =~ ^(/v1|/api/v1|/engines/v1)$ ]] && open_value "${model}" ||
    fail "Invalid port, API path or model for ${role}."

  say "Checking your model app at 127.0.0.1:${port}${path}"
  # Plain http on 127.0.0.1: curl alone, no certificates.
  command -v curl >/dev/null 2>&1 || require_curl "the model app check"
  listing="$(curl -fsS --max-time 10 "http://127.0.0.1:${port}${path}/models" 2>/dev/null)" ||
    fail "Nothing answers at http://127.0.0.1:${port}${path}/models. Start your model app's server, then add ${role} again."
  grep -qF "\"${model}\"" <<< "${listing}" ||
    fail "Your model app at port ${port} doesn't list ${model}. Load it there (or choose a model it lists), then add ${role} again."
  note "Your model app lists ${model}."

  local terms; terms="$(conf_value "${conf}" terms)"
  if [[ -n "${terms}" ]]; then
    note "${terms}"
    ask "Continue with these terms?" || fail "Role not installed."
  fi
  local stops=()
  mapfile -t stops < <(exclusive_others "${role}")
  if [[ ${#stops[@]} -gt 0 ]]; then
    note "One Thinking pool model runs on card 1 of a host; ${role} replaces ${stops[*]} here (its downloads are kept, so adding it again is quick)."
    ask "Stop ${stops[*]} when ${role} starts?" || fail "Role not installed."
    release_roles "${role}" "${stops[@]}"
  fi
  mkdir -p "${project}"; chmod 700 "${project}"
  ( umask 077; printf '%s\n' "${env_lines[@]}" > "${project}/.env" )
  with_gateway publish_role "${role}" "${kind}" "${port}" "${model}" "" 1 "${api}" "${path}"
  say "Role ${role} relays the Thinking pool to ${model} in your app. Paired desktops pick it up automatically."
}

cmd_add() {
  local role="${1:-}"; [[ -n "${role}" ]] || fail "Usage: martlet-host add <role>"
  load_input
  require_setup
  local dir conf project role_network role_feature kind
  dir="$(role_dir "${role}")"; conf="${dir}/role.conf"; project="${PROJECTS}/${role}"
  role_network="$(conf_value "${conf}" network)"; role_feature="$(conf_value "${conf}" feature)"; kind="$(conf_value "${conf}" gateway_kind)"
  [[ -z "${role_network}" || "${role_network}" == host ]] || fail "Invalid network in role.conf."
  [[ -z "${role_feature}" || "${role_feature}" =~ ^[a-z0-9-]{1,32}$ ]] || fail "Invalid feature in role.conf."
  say "Adding role ${role}: $(conf_value "${conf}" title)"
  if [[ -n "$(conf_value "${conf}" external)" ]]; then add_external_role "${role}" "${conf}" "${kind}"; return; fi

  say "Requirements"
  require_docker
  # shellcheck disable=SC2046
  require $(conf_value "${conf}" requires)

  rm -rf "${project}/files"
  mkdir -p "${project}/files" "${CONF}/secrets"; chmod 700 "${project}" "${CONF}/secrets"
  local env_lines=("COMPOSE_PROJECT_NAME=martlet-${role}" "MARTLET_ROLE_CONFIGS_VOLUME=martlet-${role}-configs")
  if [[ "${MODE}" == docker ]]; then env_lines+=("MARTLET_ROLE_NETWORK=container:${NET}"); else env_lines+=("MARTLET_ROLE_NETWORK=host"); fi
  env_lines+=("MARTLET_TZ=$(host_timezone)")
  # Martlet sources a role may build its image from (compose build contexts under ${MARTLET_SOURCE}/workers).
  env_lines+=("MARTLET_SOURCE=${SOURCE}")
  local line name prompt var label options default value condition

  # Adding an installed role again changes only what is answered: unanswered choices and GPU or CPU stay as they are now.
  declare -A current_choice=()
  local current_accelerator="" current_kind current_value
  while IFS='|' read -r current_kind current_value; do
    case "${current_kind}" in
      choice) current_choice["${current_value%%|*}"]="${current_value#*|}" ;;
      accelerator) current_accelerator="${current_value}" ;;
    esac
  done < <(role_current "${role}")

  # Choices that pick a variant come first: those outside any [VAR=value] section and not suggested by GPU memory (the
  # stt engine, the Audio2Face engine). Then the variant's own entries apply: its GPU or CPU option, its choices and their
  # suggestions, terms, secrets, assets and prepare steps.
  declare -A chosen=()
  local by_vram=" " first=()
  while IFS=$'\t' read -r condition line; do
    [[ "${line%%=*}" == choice_by_vram ]] || continue
    line="${line#*=}"; by_vram+="${line%%|*} "
  done < <(conf_lines "${conf}")
  while IFS=$'\t' read -r condition line; do
    [[ "${condition}" == "*" && "${line%%=*}" == choice ]] || continue
    line="${line#*=}"
    [[ "${by_vram}" == *" ${line%%|*} "* ]] || first+=("${line}")
  done < <(conf_lines "${conf}")
  for line in "${first[@]}"; do ask_choice "${line}" "${current_choice[${line%%|*}]:-}"; done

  # GPU or CPU: roles with an optional GPU overlay run on the NVIDIA GPU when this host can use one; gpu=required
  # roles always add their overlay (they cannot run on the CPU). Inside a [VAR=value] section only that variant has it.
  local gpu_entry gpu_mode overlay="" accelerator="" vram=0 gpu_choice="" arch_overlay
  gpu_entry="$(conf_value "${conf}" gpu)"
  arch_overlay="$(arm64_overlay "${conf}")" || fail "Invalid arm64 entry in role.conf."
  if [[ -n "${arch_overlay}" && -n "${gpu_entry}" ]]; then
    say "GPU or CPU"
    note "This host is ARM64: ${role} runs its ARM64 build on the CPU."
    gpu_entry=""; accelerator=cpu
    env_lines+=("MARTLET_ACCELERATOR=cpu")
  fi
  if [[ -n "${gpu_entry}" ]]; then
    gpu_mode="${gpu_entry%%|*}"; overlay="${gpu_entry#*|}"
    [[ "${gpu_mode}" =~ ^(optional|required)$ && "${overlay}" =~ ^[a-z0-9.-]+\.yaml$ && -f "${dir}/${overlay}" ]] ||
      fail "Invalid gpu entry in role.conf."
    say "GPU or CPU"
    detect_nvidia
    local suggested=cpu
    if (( NVIDIA_MB > 0 )); then
      suggested=gpu; note "NVIDIA GPU usable by containers: ${NVIDIA_MB} MiB of memory."
    elif [[ "${gpu_mode}" == required ]]; then
      note "No NVIDIA GPU usable by containers was found yet; ${role} needs one (installing the NVIDIA requirements next)."
    else
      note "No NVIDIA GPU usable by containers was found; ${role} runs on the CPU (slower)."
    fi
    if [[ "${gpu_mode}" == required ]]; then
      accelerator=gpu
    else
      # Installed already: it stays where it runs now (on the GPU only while this host still has one).
      if [[ "${current_accelerator}" == cpu || ( "${current_accelerator}" == gpu && ${NVIDIA_MB} -gt 0 ) ]]; then
        suggested="${current_accelerator}"
      fi
      accelerator="${INPUT[choice.accelerator]:-}"
      if [[ -z "${accelerator}" ]] && ! unattended; then
        read -r -p "Run ${role} on [gpu/cpu] (default ${suggested}): " accelerator
      fi
    fi
    accelerator="${accelerator:-${suggested}}"
    note "Runs on: ${accelerator}"
    [[ "${accelerator}" == gpu || "${accelerator}" == cpu ]] || fail "Choose gpu or cpu."
    if [[ "${accelerator}" == gpu ]]; then
      require gpu nvidia-toolkit
      (( NVIDIA_MB > 0 )) || detect_nvidia
      vram=${NVIDIA_MB}
      # Several cards: which one this role runs on (each role on its own card, or "all" to spread a model over every card).
      if (( ${#NVIDIA_IDS[@]} > 1 )); then
        local i card_list="" known=0
        for i in "${!NVIDIA_IDS[@]}"; do card_list+=" ${NVIDIA_IDS[i]} (${NVIDIA_NAMES[i]}, ${NVIDIA_MBS[i]} MiB)"; done
        note "Graphics cards here:${card_list}"
        gpu_choice="${INPUT[choice.gpu]:-}"
        if [[ -z "${gpu_choice}" ]] && ! unattended; then
          read -r -p "Graphics card for ${role} [a card's GPU-... id, several ids joined by commas, or all] (default automatic): " gpu_choice
        fi
        [[ -z "${gpu_choice}" || "${gpu_choice}" == automatic ]] && gpu_choice="$(automatic_gpu "${role}")"
        if [[ "${gpu_choice}" == all ]]; then
          vram=0; for i in "${!NVIDIA_MBS[@]}"; do vram=$(( vram + NVIDIA_MBS[i] )); done
          note "Graphics card: all ${#NVIDIA_IDS[@]} cards (${vram} MiB together)"
        elif [[ "${gpu_choice}" == *,* ]]; then
          # Several cards (GPU-a,GPU-b): one model split across them (tensor split), for a model no single card has room for.
          known_gpus "${gpu_choice}" || fail "Graphics cards '${gpu_choice}' aren't all on this host (each once). Check the host again in Martlet."
          vram=0
          for i in "${!NVIDIA_IDS[@]}"; do [[ ",${gpu_choice}," == *",${NVIDIA_IDS[i]},"* ]] && vram=$(( vram + NVIDIA_MBS[i] )); done
          note "Graphics cards: ${gpu_choice//,/, } (${vram} MiB together; one model split across them)"
          env_lines+=("MARTLET_GPU=${gpu_choice}")
        else
          for i in "${!NVIDIA_IDS[@]}"; do
            if [[ "${NVIDIA_IDS[i]}" == "${gpu_choice}" ]]; then known=1; vram=${NVIDIA_MBS[i]}; note "Graphics card: ${NVIDIA_NAMES[i]} (${gpu_choice}, ${vram} MiB)"; fi
          done
          if (( ! known )); then
            # A card that left this host (or one named from another view of it): place the role automatically instead.
            note "Graphics card '${gpu_choice}' isn't on this host; choosing one automatically."
            gpu_choice="$(automatic_gpu "${role}")"
            for i in "${!NVIDIA_IDS[@]}"; do
              if [[ "${NVIDIA_IDS[i]}" == "${gpu_choice}" ]]; then vram=${NVIDIA_MBS[i]}; note "Graphics card: ${NVIDIA_NAMES[i]} (${gpu_choice}, ${vram} MiB)"; fi
            done
          fi
          env_lines+=("MARTLET_GPU=${gpu_choice}")
        fi
      fi
    fi
    env_lines+=("MARTLET_ACCELERATOR=${accelerator}")
  fi

  # min_gpus=N: a role that needs N NVIDIA graphics cards on this host (the Thinking pool model for card N, deep-thinking-N,
  # which would otherwise share a card that a Thinking model uses already).
  local min_gpus; min_gpus="$(conf_value "${conf}" min_gpus)"
  if [[ -n "${min_gpus}" ]]; then
    [[ "${min_gpus}" =~ ^[1-8]$ ]] || fail "Invalid min_gpus in role.conf."
    (( ${#NVIDIA_IDS[@]} > 0 )) || detect_nvidia
    (( ${#NVIDIA_IDS[@]} >= min_gpus )) ||
      fail "${role} needs a host with at least ${min_gpus} NVIDIA graphics cards usable by containers; this host has ${#NVIDIA_IDS[@]}."
  fi

  # Suggested choice values by GPU memory: choice_by_vram=VAR|<MiB>@<value> ... (ascending, 0 = CPU).
  declare -A suggested_choice=()
  local entry pick
  while IFS= read -r line <&3; do
    [[ -n "${line}" ]] || continue
    pick=""
    for entry in ${line#*|}; do
      [[ "${entry%%@*}" =~ ^[0-9]+$ ]] || fail "Invalid choice_by_vram entry in role.conf."
      if (( vram >= ${entry%%@*} )); then pick="${entry#*@}"; fi
    done
    if [[ -n "${pick}" ]]; then suggested_choice["${line%%|*}"]="${pick}"; fi
  done 3< <(conf_values "${conf}" choice_by_vram)

  local suggestion
  while IFS= read -r line <&3; do
    [[ -n "${line}" && -z "${chosen[${line%%|*}]+x}" ]] || continue
    IFS='|' read -r var label options default <<< "${line}"
    # Installed already: an unanswered choice keeps what the role runs with now (when the chosen variant offers it).
    suggestion="${current_choice[${var}]:-}"
    if [[ -z "${suggestion}" || " ${options} " != *" ${suggestion} "* ]]; then
      suggestion="${suggested_choice[${var}]:-}"
      if [[ -n "${suggestion}" && "${accelerator}" == gpu ]]; then note "Suggested for ${vram} MiB of GPU memory: ${suggestion}"; fi
    fi
    ask_choice "${line}" "${suggestion}"
  done 3< <(conf_values "${conf}" choice)
  # A role with variants runs the Compose profile its choice names (profile_from=VAR), for example one Audio2Face engine.
  local profiles="" profile_from
  profile_from="$(conf_value "${conf}" profile_from)"
  if [[ -n "${profile_from}" ]]; then
    profiles="${chosen[${profile_from}]:-}"
    [[ "${profiles}" =~ ^[a-z0-9_-]+$ ]] || fail "Invalid profile_from in role.conf."
    env_lines+=("COMPOSE_PROFILES=${profiles}")
  fi

  # A role whose model must fit on one card (role.conf min_vram=<MiB>|<why>, in a [VAR=value] section for one model): it stops
  # here, before terms, downloads or containers, on a card with less memory. "all" cards still load the model on the first.
  local card_mib=${vram} refusal
  if [[ "${gpu_choice}" == all ]] && (( ${#NVIDIA_MBS[@]} > 0 )); then card_mib=${NVIDIA_MBS[0]}; fi
  refusal="$(min_vram_refusal "${conf}" "${role}" "${card_mib}")" || fail "Invalid min_vram in role.conf."
  [[ -z "${refusal}" ]] || fail "${refusal}"

  # Terms for the chosen variant (shown in Martlet before Install, whose click confirms them).
  local terms
  while IFS= read -r terms <&3; do
    [[ -n "${terms}" ]] && note "${terms}"
  done 3< <(conf_values "${conf}" terms)
  if [[ -n "$(conf_value "${conf}" terms)" ]]; then
    ask "Continue with these terms?" || fail "Role not installed."
  fi
  # One engine of an exclusive group (the voice engines) per host: the others installed here stop before this one starts,
  # so the model they keep in the graphics card's memory never starves it. Their volumes and images are kept.
  local stops=()
  mapfile -t stops < <(exclusive_others "${role}")
  if [[ ${#stops[@]} -gt 0 ]]; then
    note "Only one $(conf_value "${conf}" exclusive) engine runs on a host at a time; ${role} replaces ${stops[*]} here (their downloads are kept, so adding one again is quick)."
    ask "Stop ${stops[*]} when ${role} starts?" || fail "Role not installed."
  fi

  while IFS= read -r line <&3; do
    [[ -n "${line}" ]] || continue
    name="${line%%|*}"; prompt="${line#*|}"
    [[ "${name}" =~ ^[a-z0-9_]+$ ]] || fail "Invalid secret name in role.conf."
    value="${INPUT[secret.${name}]:-}"
    if [[ -z "${value}" && ! -s "${CONF}/secrets/${name}" ]]; then
      unattended && fail "${prompt} is required; enter it in Martlet."
      read -r -s -p "${prompt} (input hidden): " value; echo
      [[ -n "${value}" ]] || fail "${name} is required."
    fi
    if [[ -n "${value}" ]]; then
      # Whole or not at all: another role's change running alongside may read the same secret.
      ( umask 077; printf '%s' "${value}" > "${CONF}/secrets/.${name}.$$" && mv -f "${CONF}/secrets/.${name}.$$" "${CONF}/secrets/${name}" )
      note "Saved ${name} on this host (0600)."
    fi
    unset value
  done 3< <(conf_values "${conf}" secret)

  local registry user secret
  while IFS= read -r line <&3; do
    [[ -n "${line}" ]] || continue
    IFS='|' read -r registry user secret <<< "${line}"
    "${DOCKER[@]}" login "${registry}" --username "${user}" --password-stdin < "${CONF}/secrets/${secret}"
  done 3< <(conf_values "${conf}" registry)

  say "Fetching pinned role assets"
  local url target key
  while IFS= read -r line <&3; do
    [[ -n "${line}" ]] || continue
    url="${line%%|*}"; target="${line#*|}"
    for key in "${!chosen[@]}"; do url="${url//\{${key}\}/${chosen[${key}]}}"; target="${target//\{${key}\}/${chosen[${key}]}}"; done
    [[ "${url}" == https://* && "${target}" == configs/* && "${target}" != *..* ]] || fail "Unsafe asset entry in role.conf."
    mkdir -p "${project}/files/$(dirname "${target#configs/}")"
    require_curl "role assets"
    curl -fsSL "${url}" -o "${project}/files/${target#configs/}"
  done 3< <(conf_values "${conf}" asset)
  while IFS= read -r line <&3; do
    [[ -n "${line}" ]] || continue
    sed -i "${line#*|}" "${project}/files/${line%%|*}"
  done 3< <(conf_values "${conf}" rewrite | sed 's#^configs/##')
  while IFS= read -r line <&3; do
    [[ -n "${line}" ]] || continue
    grep -qF "${line#*|}" "${project}/files/${line%%|*}" || fail "Role asset check failed: ${line%%|*} lacks '${line#*|}'."
  done 3< <(conf_values "${conf}" expect | sed 's#^configs/##')

  # Switching a role's variant (its Compose profile, such as the stt or Audio2Face engine) stops the previous one, but only
  # once the chosen one is prepared (its image built, its model downloaded), so the role keeps working until then; its data
  # volumes are kept. Installs from before the role had variants ran role.conf's profile_legacy.
  local previous_profiles="" switching=0 existed=0
  if [[ -f "${project}/compose.yaml" ]]; then
    existed=1
    previous_profiles="$(conf_value "${project}/.env" COMPOSE_PROFILES 2>/dev/null)"
    [[ -n "${previous_profiles}" ]] || previous_profiles="$(conf_value "${conf}" profile_legacy)"
    if [[ "${previous_profiles}" != "${profiles}" ]]; then switching=1; fi
  fi
  cp "${dir}/compose.yaml" "${project}/compose.yaml"
  rm -f "${project}/compose.arch.yaml"
  if [[ -n "${arch_overlay}" ]]; then cp "${dir}/${arch_overlay}" "${project}/compose.arch.yaml"; fi
  rm -f "${project}/compose.gpu.yaml"
  if [[ "${accelerator}" == gpu ]]; then install_gpu_overlay "${dir}/${overlay}" "${project}/compose.gpu.yaml" "${gpu_choice}"; fi
  ( umask 077; printf '%s\n' "${env_lines[@]}" > "${project}/.env" )
  fill_volume "martlet-${role}-configs" "${project}/files"

  local port minutes ready_address tries detected_features=()
  port="$(conf_value "${conf}" port)"; minutes="$(conf_value "${conf}" ready_timeout_minutes)"
  [[ "${port}" =~ ^[0-9]{2,5}$ ]] || fail "Invalid port in role.conf."
  if [[ "${role_network}" == host ]]; then
    mapfile -t detected_features < <(machine_features)
    print_feature_summary "${detected_features[@]}"
    if host_port_open "${port}" && ! role_container_running "${role}"; then
      fail "Something else already uses port ${port} on this computer (probably another Home Assistant). Connect to it in Martlet's Smart home page instead."
    fi
  fi

  # Prepare steps: prepare=<service> runs that service once with MARTLET_PREPARE=1, so it fetches what the chosen options need
  # (such as a newly chosen model) and exits, while the role's running service keeps serving. Switching then only restarts it.
  while IFS= read -r line <&3; do
    [[ -n "${line}" ]] || continue
    [[ "${line}" =~ ^[a-z0-9][a-z0-9_-]*$ ]] || fail "Invalid prepare entry in role.conf."
    say "Preparing ${role} (${line})$(role_container_running "${role}" && printf ' while the running one keeps working')"
    tries=0
    until role_compose "${role}" run --rm --no-deps -T -e MARTLET_PREPARE=1 "${line}"; do
      (( ++tries < 3 )) && recover_role_dns "${role}" "${role_network}" ||
        fail "Preparing ${role} failed; the running one is unchanged. Check the host's disk space and network, then run 'martlet-host add ${role}' again."
    done
  done 3< <(conf_values "${conf}" prepare)

  local start_failed="Building or starting ${role} failed (the output above shows why). Check the host's disk space and network, then run 'martlet-host add ${role}' again: the build steps it finished are reused."
  if (( switching )); then
    # Build (or pull) the chosen variant's image while the previous one still works.
    say "Preparing ${role} (${profiles}) while ${previous_profiles:-the previous one} still runs"
    role_compose "${role}" up --no-start || fail "${start_failed}"
  fi
  # Then every other variant stops, so none competes for the role's port: not only the one .env named, since an earlier
  # attempt at this switch that stopped after rewriting .env (a failed download or build) left the previous one running.
  if (( existed )) && [[ -n "${profiles}" ]]; then
    local variant
    while IFS= read -r variant <&3; do
      [[ -n "${variant}" && "${variant}" != "${profiles}" ]] || continue
      if [[ "${variant}" == "${previous_profiles}" ]]; then
        note "Stopping the previous ${role} service (${variant}) before starting the chosen one..."
      fi
      ROLE_PROFILES="${variant}" role_compose "${role}" down || true
    done 3< <({ role_variants "${conf}"; printf '%s\n' "${previous_profiles}"; } | awk '!seen[$0]++')
  fi
  if [[ ${#stops[@]} -gt 0 ]]; then
    # Build (or pull) this role's image while the engine it replaces still works, then free the graphics card for it.
    say "Preparing ${role} while ${stops[*]} still runs"
    role_compose "${role}" up --no-start || fail "${start_failed}"
    release_roles "${role}" "${stops[@]}"
  fi

  say "Starting ${role}"
  role_compose "${role}" up -d || fail "${start_failed}"
  ready_address="$(role_ready_address "${role_network}")"
  note "Waiting for ${role} on ${ready_address}:${port} (up to ${minutes:-10} minutes; first starts can be slow)..."
  local ready=0 attempt
  for attempt in $(seq 1 $(( ${minutes:-10} * 6 ))); do
    if (exec 3<>"/dev/tcp/${ready_address}/${port}") 2>/dev/null; then ready=1; break; fi
    engine_stranded && fail "$(stranded_text) ${role} itself is starting; add it again to finish (martlet-host add ${role}; its image is already built)."
    sleep 10
  done
  [[ ${ready} == 1 ]] || fail "${role} did not start. Inspect: martlet-host status (or docker compose -p martlet-${role} logs)"

  local model_from model="${role}" previous="" slots_from slots=1 retired=0
  model_from="$(conf_value "${conf}" model_from)"
  if [[ -n "${kind}" ]]; then
    if [[ -n "${model_from}" ]]; then model="${chosen[${model_from}]:-${role}}"; fi
    if [[ -f "$(installed_file "${role}")" ]]; then previous="$(conf_value "$(installed_file "${role}")" model)"; fi
  fi

  # Post-start steps: post_start=<service>|<command> runs inside that service once it listens ({VAR} uses a choice), then the
  # warm steps (warm=<service>|<command>, loading the model), which "martlet-host warm" runs again after a restart.
  # A model switch keeps the previous model loaded until the new one answers. When both don't fit in the graphics card's
  # memory, loading the new one can fail (Ollama's gemma4:12b does when one layer doesn't fit), so a failed warm step
  # retires the previous model first and runs once more.
  local service step words section safe='^[A-Za-z0-9 ._:/=-]+$'
  # The same model chosen again is a reload: it leaves the graphics card's memory first, so the warm step loads it fresh (a
  # model that got slow, for example only partly on the graphics card, comes back as a new load). Owners do this to "refresh".
  local reload=0
  if [[ -n "${previous}" && "${previous}" == "${model}" && -n "$(conf_values "${conf}" retire)" && -n "$(conf_values "${conf}" warm)" ]]; then
    reload=1
  fi
  while IFS='|' read -r section line <&3; do
    if [[ "${section}" == warm ]] && (( reload )); then
      say "${model} is chosen again: unloading it so it loads fresh."
      retire_previous "${role}" "${conf}" "${previous}" "${model}" same
      reload=0
    fi
    [[ -n "${line}" ]] || continue
    service="${line%%|*}"; step="${line#*|}"
    for key in "${!chosen[@]}"; do step="${step//\{${key}\}/${chosen[${key}]}}"; done
    [[ "${service}" =~ ^[a-z0-9][a-z0-9_-]*$ && "${step}" =~ ${safe} ]] || fail "Unsafe ${section} entry in role.conf."
    read -r -a words <<< "${step}"
    say "Running in ${service}: ${step}"
    tries=0
    until role_compose "${role}" exec -T "${service}" "${words[@]}"; do
      if [[ "${section}" == warm ]]; then
        if (( ! retired )) && [[ -n "${previous}" && "${previous}" != "${model}" && -n "$(conf_values "${conf}" retire)" ]]; then
          note "Loading ${model} failed while ${previous} still uses the graphics card's memory. Unloading ${previous} and trying again..."
          retire_previous "${role}" "${conf}" "${previous}" "${model}"
          retired=1
          continue
        fi
        fail "'${step}' failed in ${role}: the model didn't load (the output above shows why). Choose a smaller model or close programs that use the graphics card, then run 'martlet-host add ${role}' again."
      fi
      (( ++tries < 3 )) && recover_role_dns "${role}" "${role_network}" ||
        fail "'${step}' failed in ${role}. Check the host's disk space and network, then run 'martlet-host add ${role}' again."
    done
  done 3< <(conf_values "${conf}" post_start | sed 's/^/post_start|/'; conf_values "${conf}" warm | sed 's/^/warm|/')

  # slots_from=VAR: the choice saying how many requests the role runs at once (the deep-thinking role's thinks at once).
  slots_from="$(conf_value "${conf}" slots_from)"
  if [[ -n "${slots_from}" ]]; then
    slots="${chosen[${slots_from}]:-1}"
    [[ "${slots}" =~ ^[1-9]$ ]] || fail "Invalid ${slots_from}."
  fi
  if [[ -n "${kind}" ]]; then
    with_gateway publish_role "${role}" "${kind}" "${port}" "${model}" "" "${slots}"
    if (( ! retired )); then retire_previous "${role}" "${conf}" "${previous}" "${model}"; fi
    say "Role ${role} is running and published through the gateway. Paired desktops pick it up automatically."
  else
    [[ -n "${role_feature}" ]] || fail "role.conf declares no gateway_kind or feature."
    [[ "${role_network}" == host ]] || fail "Route-less roles must declare network=host."
    with_gateway publish_role "${role}" "" "${port}" "" "${role_feature}"
    say "Role ${role} is running. Paired desktops see its ${role_feature} feature on Check connection."
  fi
}

cmd_remove() {
  local role="${1:-}"; [[ -n "${role}" ]] || fail "Usage: martlet-host remove <role>"
  require_setup
  role_dir "${role}" >/dev/null
  local record; record="$(installed_file "${role}")"
  [[ -f "${record}" ]] || fail "Role ${role} is not installed."
  local had_kind; had_kind="$(conf_value "${record}" kind)"
  ask "Turn off ${role}: stop it and remove it from the gateway (its downloads and data volumes are kept)?" || exit 0
  if [[ -n "$(conf_value "${record}" api)" ]]; then
    # A relay to the owner's own model app: nothing runs, so only its route goes. The app and its model stay as they are.
    with_gateway unpublish_role "${role}" "${had_kind}"
    say "Role ${role} is off. Your model app keeps running as it is."
    return
  fi
  require_docker
  role_compose "${role}" down || true
  # Every variant, also one an interrupted switch left running.
  local variant
  while IFS= read -r variant <&3; do
    if [[ -n "${variant}" ]]; then ROLE_PROFILES="${variant}" role_compose "${role}" down >/dev/null 2>&1 || true; fi
  done 3< <(role_variants "$(role_dir "${role}")/role.conf")
  with_gateway unpublish_role "${role}" "${had_kind}"
  say "Role ${role} is off. Its downloads are kept, so 'martlet-host add ${role}' turns it back on without downloading them again."
}
