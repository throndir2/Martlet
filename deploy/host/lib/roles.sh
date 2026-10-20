# shellcheck shell=bash
# martlet-host module (deploy/host/lib/roles.sh), sourced by deploy/host/martlet-host; it is not run by itself.
# The role catalog: reads roles/<role>/role.conf (choices, variants, exclusive groups) and the
# installed role records, and the read-only commands roles and describe.

# ------------------------------------------------------------------ role catalog (data only)
role_dir() {
  [[ "$1" =~ ^[a-z0-9][a-z0-9-]{0,31}$ && -f "${ROLES_DIR}/$1/role.conf" ]] || fail "Unknown role '$1'. See: martlet-host roles"
  printf '%s' "${ROLES_DIR}/$1"
}

# Choices made for the role being added (VAR -> value). In role.conf, entries between a "[VAR=value]" line and "[end]"
# apply only when that choice has that value (for example the Audio2Face engine's own secret and assets).
declare -A ROLE_CHOSEN=()

# Prints "<condition>\t<key>=<value>" for every entry of a key=value file (comments and CR stripped); the condition
# is "*" outside a [VAR=value] section.
conf_lines() {
  local file="$1" line section="*"
  while IFS= read -r line || [[ -n "${line}" ]]; do
    line="${line%$'\r'}"
    [[ -z "${line}" || "${line}" == \#* ]] && continue
    if [[ "${line}" =~ ^\[([A-Za-z0-9_]+=[A-Za-z0-9._-]+)\]$ ]]; then section="${BASH_REMATCH[1]}"; continue; fi
    if [[ "${line}" == "[end]" ]]; then section="*"; continue; fi
    printf '%s\t%s\n' "${section}" "${line}"
  done < "${file}"
  return 0
}
applies() { [[ "$1" == "*" || "${ROLE_CHOSEN[${1%%=*}]:-}" == "${1#*=}" ]]; }

# Prints every value of KEY that applies (list keys may repeat).
conf_values() {
  local file="$1" key="$2" condition line
  while IFS=$'\t' read -r condition line; do
    applies "${condition}" || continue
    if [[ "${line%%=*}" == "${key}" ]]; then printf '%s\n' "${line#*=}"; fi
  done < <(conf_lines "${file}")
  return 0
}
conf_value() {
  local file="$1" key="$2" condition line
  while IFS=$'\t' read -r condition line; do
    applies "${condition}" || continue
    if [[ "${line%%=*}" == "${key}" ]]; then printf '%s' "${line#*=}"; return 0; fi
  done < <(conf_lines "${file}")
  return 0
}
# The first value of KEY in any section, whatever was chosen (for example the GPU overlay of a role whose GPU option
# belongs to one variant, such as the stt role's whisper engine).
conf_any_value() {
  local file="$1" key="$2" condition line
  while IFS=$'\t' read -r condition line; do
    if [[ "${line%%=*}" == "${key}" ]]; then printf '%s' "${line#*=}"; return 0; fi
  done < <(conf_lines "${file}")
  return 0
}

# Whether $1 is a model name an open choice (role.conf choice_open=VAR) takes: an Ollama tag ("llama3.3:70b") or a Hugging
# Face GGUF ("hf.co/unsloth/Qwen3-32B-GGUF:Q4_K_M"). The same pattern as the gateway's role records: letters, digits and
# . _ : / -, at most 128 characters, so it is safe in the role's commands.
open_value() {
  [[ "$1" =~ ^[A-Za-z0-9][A-Za-z0-9._:/-]*$ && ${#1} -le 128 && "$1" != *..* ]]
}

# Asks role choice $1 ("VAR|label|options|default"; $2, when it is one of the options, is the default instead: what an
# installed role runs with now, or the suggestion) for cmd_add unless Martlet answered it, and records the answer in
# cmd_add's chosen and env_lines and in ROLE_CHOSEN.
ask_choice() {
  local var label options default value open=0 hint=""
  IFS='|' read -r var label options default <<< "$1"
  # cmd_add's role.conf: choice_open=VAR lets the choice also take any model name (open_value), not only the options listed.
  if [[ " $(conf_values "${conf:-/dev/null}" choice_open | tr '\n' ' ') " == *" ${var} "* ]]; then open=1; hint=" or another model name"; fi
  if [[ -n "${2:-}" ]] && { [[ " ${options} " == *" ${2} "* ]] || { (( open )) && open_value "${2}"; }; }; then default="$2"; fi
  value="${INPUT[choice.${var}]:-}"
  if [[ -z "${value}" ]]; then
    if unattended; then value="${default}"
    else read -r -p "${label} [${options// //}${hint}] (default ${default}): " value; fi
  fi
  value="${value:-${default}}"
  if (( open )); then
    [[ " ${options} " == *" ${value} "* ]] || open_value "${value}" ||
      fail "Choose one of: ${options}, or a model name (letters, digits and . _ : / -, 128 characters at most)."
  else
    [[ " ${options} " == *" ${value} "* ]] || fail "Choose one of: ${options}"
  fi
  note "${label}: ${value}"
  chosen["${var}"]="${value}"
  ROLE_CHOSEN["${var}"]="${value}"
  env_lines+=("${var}=${value}")
}

installed_file() { printf '%s' "${CONF}/roles/$1.role"; }

# role.conf min_vram=<MiB>|<why> (inside a [VAR=value] section it applies to that choice only): the least memory one NVIDIA
# card needs for role $2's model. Prints why the role can't run on a card with $3 MiB (0: none found), or nothing when it can
# or has no minimum; returns 1 when the entry is invalid.
min_vram_refusal() {
  local conf="$1" role="$2" mib="$3" entry need why
  entry="$(conf_value "${conf}" min_vram)"
  [[ -n "${entry}" ]] || return 0
  need="${entry%%|*}"; why=""; [[ "${entry}" == *"|"* ]] && why="${entry#*|}"
  [[ "${need}" =~ ^[0-9]+$ ]] || return 1
  (( mib >= need )) && return 0
  if (( mib > 0 )); then
    printf '%s needs an NVIDIA graphics card with at least %s MiB of memory%s; this host'"'"'s card has %s MiB.' \
      "${role}" "${need}" "${why:+ (${why})}" "${mib}"
  else
    printf '%s needs an NVIDIA graphics card with at least %s MiB of memory%s; no NVIDIA card usable by containers was found.' \
      "${role}" "${need}" "${why:+ (${why})}"
  fi
}

# The variants (Compose profiles) of the role whose role.conf is $1, one per line: the options of its profile_from choice
# (the stt role's whisper and parakeet). Each of the role's services belongs to one of them.
role_variants() {
  local conf="$1" from condition line var label options default
  from="$(conf_value "${conf}" profile_from)"
  [[ -n "${from}" ]] || return 0
  while IFS=$'\t' read -r condition line; do
    [[ "${condition}" == "*" && "${line%%=*}" == choice ]] || continue
    IFS='|' read -r var label options default <<< "${line#*=}"
    # shellcheck disable=SC2086
    if [[ "${var}" == "${from}" ]]; then printf '%s\n' ${options}; fi
  done < <(conf_lines "${conf}")
  return 0
}

# Installed roles (other than $1) in the same role.conf exclusive=<group> as $1, one per line: the voice engines, which
# each keep a model in the graphics card's memory, so a host runs one at a time and adding one stops the others.
exclusive_others() {
  local role="$1" group file other
  group="$(conf_value "${ROLES_DIR}/${role}/role.conf" exclusive)"
  [[ -n "${group}" ]] || return 0
  [[ "${group}" =~ ^[a-z0-9-]{1,32}$ ]] || fail "Invalid exclusive in role.conf."
  for file in "${CONF}/roles"/*.role; do
    [[ -e "${file}" ]] || continue
    other="$(basename "${file}" .role)"
    [[ "${other}" != "${role}" && -f "${ROLES_DIR}/${other}/role.conf" ]] || continue
    [[ "$(conf_value "${ROLES_DIR}/${other}/role.conf" exclusive)" == "${group}" ]] && printf '%s\n' "${other}"
  done
  return 0
}

cmd_roles() {
  local dir name mark
  for dir in "${ROLES_DIR}"/*/; do
    name="$(basename "${dir}")"
    [[ -f "${dir}/role.conf" ]] || continue
    mark="available"; [[ -f "$(installed_file "${name}")" ]] && mark="installed"
    printf '  %-14s %-10s %s\n' "${name}" "${mark}" "$(conf_value "${dir}/role.conf" title)"
    printf '  %-14s %-10s requires: %s\n' "" "" "$(conf_value "${dir}/role.conf" requires)"
  done
}

# Machine-readable role inputs for Martlet desktop (never prints secret values, only whether one is stored).
# Entries in a [VAR=value] section are listed with that condition (role.secret_when, role.terms_when, role.choice_when,
# role.suggested_when, role.gpu_when) so Martlet asks for them, and shows their terms, only when that choice is made.
cmd_describe() {
  local role="${1:-}"; [[ -n "${role}" ]] || fail "Usage: martlet-host describe <role>"
  local dir conf line name state requires network feature condition min_vram="" least_vram=""
  dir="$(role_dir "${role}")"; conf="${dir}/role.conf"
  requires="$(conf_value "${conf}" requires)"; network="$(conf_value "${conf}" network)"; feature="$(conf_value "${conf}" feature)"
  printf 'role.title=%s\n' "$(conf_value "${conf}" title)"
  printf 'role.requires=%s\n' "${requires}"
  [[ -n "${network}" ]] && printf 'role.network=%s\n' "${network}"
  [[ -n "${feature}" ]] && printf 'role.feature=%s\n' "${feature}"
  if [[ " ${requires} " == *" docker-engine "* && "${MODE}" == docker ]] && ! docker_engine_supported; then
    printf 'role.unavailable=%s\n' "${DOCKER_ENGINE_FAILURE}"
  fi
  if [[ " ${requires} " == *" x86_64 "* && "$(host_arch)" == arm64 ]]; then
    printf 'role.unavailable=%s\n' "${X86_64_FAILURE}"
  fi
  printf 'role.terms=%s\n' "$(conf_value "${conf}" terms)"
  state=no; [[ -f "$(installed_file "${role}")" ]] && state=yes
  printf 'role.installed=%s\n' "${state}"
  # Installed roles that adding this one stops first (role.conf exclusive=<group>), space-separated.
  local stops; stops="$(exclusive_others "${role}" | tr '\n' ' ')"; stops="${stops% }"
  [[ -n "${stops}" ]] && printf 'role.stops=%s\n' "${stops}"
  while IFS=$'\t' read -r condition line; do
    case "${line%%=*}" in
      secret)
        line="${line#*=}"; name="${line%%|*}"
        state=missing; [[ -s "${CONF}/secrets/${name}" ]] && state=stored
        printf 'role.secret=%s|%s\n' "${line}" "${state}"
        [[ "${condition}" == "*" ]] || printf 'role.secret_when=%s|%s\n' "${name}" "${condition}" ;;
      terms)
        [[ "${condition}" == "*" ]] || printf 'role.terms_when=%s|%s\n' "${condition}" "${line#*=}" ;;
      # A variant's own choices (its model list), suggestions and GPU or CPU option.
      choice)
        [[ "${condition}" == "*" ]] || printf 'role.choice_when=%s|%s\n' "${condition}" "${line#*=}" ;;
      choice_by_vram)
        line="${line#*=}"
        [[ "${condition}" == "*" ]] || printf 'role.suggested_when=%s|%s\n' "${condition}" "${line%%|*}" ;;
      gpu)
        [[ "${condition}" == "*" ]] || printf 'role.gpu_when=%s\n' "${condition}" ;;
      # The least memory one card needs, for the role or for one of its choices (its model).
      min_vram)
        if [[ "${condition}" == "*" ]]; then printf 'role.min_vram=%s\n' "${line#*=}"; else printf 'role.min_vram_when=%s|%s\n' "${condition}" "${line#*=}"; fi
        min_vram="${line#*=}"; min_vram="${min_vram%%|*}"
        if [[ "${min_vram}" =~ ^[0-9]+$ ]] && [[ -z "${least_vram}" || ${min_vram} -lt ${least_vram} ]]; then least_vram=${min_vram}; fi ;;
    esac
  done < <(conf_lines "${conf}")
  while IFS= read -r line; do
    [[ -n "${line}" ]] && printf 'role.choice=%s\n' "${line}"
  done < <(conf_values "${conf}" choice)
  # Choices that also take any model name (an Ollama tag or hf.co/... GGUF), not only the options listed.
  while IFS= read -r line; do
    [[ "${line}" =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]] && printf 'role.choice_open=%s\n' "${line}"
  done < <(conf_values "${conf}" choice_open)
  # Choices the host suggests by its GPU memory, and whether it can run on the GPU or the CPU (the host decides unless told).
  while IFS= read -r line; do
    [[ -n "${line}" ]] && printf 'role.suggested=%s\n' "${line%%|*}"
  done < <(conf_values "${conf}" choice_by_vram)
  # ARM64 hosts run roles with an arm64 overlay on the CPU, so their GPU option is not offered there.
  [[ "$(conf_any_value "${conf}" gpu)" == optional\|* && ( "$(host_arch)" != arm64 || -z "$(conf_any_value "${conf}" arm64)" ) ]] &&
    printf 'role.accelerator=gpu cpu\n'
  # What an installed role runs with now, so Martlet shows it when you change the role's settings (adding it again keeps
  # what isn't answered): role.choice_current=<VAR>|<value> and role.accelerator_current=gpu|cpu.
  local kind value
  while IFS='|' read -r kind value; do
    [[ "${kind}" == choice ]] && printf 'role.choice_current=%s\n' "${value}"
    [[ "${kind}" == accelerator ]] && printf 'role.accelerator_current=%s\n' "${value}"
  done < <(role_current "${role}")
  # On a host with several NVIDIA cards, which card it runs on: role.gpu=<uuid>|<name>|<MiB>|<other roles pinned to it>,
  # and the card it runs on now (role.gpu_current, "all" when it uses every card).
  if [[ -n "$(conf_any_value "${conf}" gpu)" ]]; then
    detect_nvidia
    # A card smaller than the least any of its models needs: the role can't run here (martlet-host add refuses it too).
    if [[ -n "${least_vram}" ]] && (( NVIDIA_MB > 0 && NVIDIA_MB < least_vram )); then
      printf 'role.unavailable=%s\n' "${role} needs an NVIDIA graphics card with at least ${least_vram} MiB of memory; this host's largest has ${NVIDIA_MB} MiB."
    fi
    if (( ${#NVIDIA_IDS[@]} > 1 )); then
      local i current
      for i in "${!NVIDIA_IDS[@]}"; do
        printf 'role.gpu=%s|%s|%s|%s\n' "${NVIDIA_IDS[i]}" "${NVIDIA_NAMES[i]}" "${NVIDIA_MBS[i]}" "$(gpu_users "${role}" "${NVIDIA_IDS[i]}")"
      done
      if [[ -f "${PROJECTS}/${role}/compose.gpu.yaml" ]]; then
        current="$(role_gpu "${role}")"; printf 'role.gpu_current=%s\n' "${current:-all}"
      fi
    fi
  fi
  return 0
}
