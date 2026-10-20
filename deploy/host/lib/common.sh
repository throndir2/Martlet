# shellcheck shell=bash
# martlet-host module (deploy/host/lib/common.sh), sourced by deploy/host/martlet-host; it is not run by itself.
# Output, the engine journal, how this run was started (--yes, a terminal or neither),
# changes side by side (the engine's locks and holder records) and the answers of --yes runs.

say()  { printf '\n\033[1m%s\033[0m\n' "$*"; }
note() { printf '%s\n' "$*"; }
fail() { journal "stopped: $*"; printf '\n\033[31mStopped: %s\033[0m\n' "$*" >&2; exit 1; }

# Engine journal on this host (Docker runs engines with --log-driver none, so nothing else records them): when each
# command started and how it ended, plus who held the gateway state when it was busy. Never secrets or pairing codes.
# A "started" line without an "exit" line means that run was killed or abandoned.
JOURNAL="${CONF}/logs/engine.log"
JOURNAL_ON=0
JOURNAL_SESSION=""
journal() {
  [[ ${JOURNAL_ON} == 1 ]] || return 0
  { ( umask 077
      mkdir -p "${CONF}/logs"
      if [[ -f "${JOURNAL}" && $(stat -c %s "${JOURNAL}") -gt 1048576 ]]; then mv -f "${JOURNAL}" "${JOURNAL}.1"; fi
      printf '%s %s %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "${JOURNAL_SESSION}" "$*" >> "${JOURNAL}" ); } 2>/dev/null || true
}
assume_yes() { [[ "${MARTLET_ASSUME_YES:-0}" == 1 ]]; }
# Unattended: --yes with answers on stdin (Martlet desktop), not a human at a terminal.
unattended() { assume_yes && [[ ! -t 0 ]]; }
# Background: nobody confirmed this run and nobody watches it (Martlet's automatic host updates: no terminal, no --yes).
background() { ! assume_yes && [[ ! -t 0 ]]; }

# ------------------------------------------------------------------ changes side by side
# Several changes run on this host at once, whoever started them (a console on this host, a desktop over SSH, Martlet on
# this computer for a paired desktop, an automatic update); only what truly collides waits:
# - add and remove hold ${CONF}/engine.lock shared, so other roles change alongside them, plus their role's lock
#   (locks/role-<role>.lock, or locks/group-<group>.lock for a role.conf exclusive=<group>, since adding one engine of a
#   group stops the others), so two changes to one role (or group) take turns. They hold locks/gateway.lock only while
#   they publish the change (role record, machine report, host.json, the gateway's approval and restart).
# - pair, console, network-reset and machine hold engine.lock shared and locks/gateway.lock for their whole run.
# - setup and update hold engine.lock exclusively: they wait for the changes running now, and changes asked for after them
#   wait for them (a waiting setup or update holds engine.gate, which every change passes through first). So does a native
#   add that first installs Docker Engine or the NVIDIA Container Toolkit, which restarts Docker under the other changes.
# The kernel drops every lock when its holder ends, however it ends (finished, killed, a dropped SSH connection), so a stale
# lock is never left behind. Each change records what it does in engine.holders/ (a record stays locked while its change
# runs; unlocked ones are dropped), and engine.holder keeps the latest one for engines from before changes ran side by side
# (those hold engine.lock exclusively, so they and newer engines never overlap). A change that must wait says what it waits
# for, up to MARTLET_LOCK_WAIT seconds in all (default 7200). Background runs default to MARTLET_LOCK_WAIT=0: they do not
# queue behind a long install, they stop at once, change nothing and exit 75 with one line "MARTLET-BUSY <what is running>",
# and Martlet tries again a few minutes later.
BUSY_EXIT=75
ENGINE_LOCK="${CONF}/engine.lock"
ENGINE_GATE="${CONF}/engine.gate"
ENGINE_HOLDER="${CONF}/engine.holder"
HOLDERS="${CONF}/engine.holders"
LOCKS="${CONF}/locks"
LOCKING=0
GATEWAY_HELD=0
LOCK_WAITED=0
HOLDER_ID=""
HOLDER_START=""
CHANGE="another martlet-host change"
CHANGE_SCOPE=""

change_words() {
  local role="${2:-}"; role="${role//[^a-z0-9-]/}"; role="${role:0:32}"
  case "$1" in
    add) printf 'installing %s' "${role:-a role}" ;;
    remove) printf 'removing %s' "${role:-a role}" ;;
    update) printf 'updating this host' ;;
    setup) printf 'setting up this host' ;;
    pair) printf 'pairing a desktop' ;;
    console) printf 'the gateway console' ;;
    network-reset) printf 'leaving the Martlet network' ;;
    exposure) printf 'changing how this host is reached from outside home' ;;
    machine) printf 'collecting the hardware report' ;;
    warm) printf 'starting and warming up the roles' ;;
    *) printf 'another martlet-host change' ;;
  esac
}

# How long this run waits for changes it collides with, in seconds (MARTLET_LOCK_WAIT; background runs: 0).
lock_patience() {
  local patience="${MARTLET_LOCK_WAIT:-}"
  if [[ ! "${patience}" =~ ^[0-9]{1,6}$ ]]; then if background; then patience=0; else patience=7200; fi; fi
  printf '%s' "${patience}"
}

# The lock a change to role $1 takes: its own, or its exclusive=<group>'s (adding one engine of a group stops the others).
role_slot() {
  local role="${1:-}" group=""
  role="${role//[^a-z0-9-]/}"; role="${role:0:32}"
  [[ -n "${role}" && -f "${ROLES_DIR}/${role}/role.conf" ]] && group="$(conf_value "${ROLES_DIR}/${role}/role.conf" exclusive)"
  if [[ "${group}" =~ ^[a-z0-9-]{1,32}$ ]]; then printf 'group-%s' "${group}"; else printf 'role-%s' "${role:-unknown}"; fi
}

# Natively, adding role $1 first installs Docker Engine or the NVIDIA Container Toolkit when they are missing, and that
# restarts Docker (and so the other changes' containers and downloads): such an add runs alone.
prepares_host() {
  [[ "${MODE}" == native ]] || return 1
  local role="${1:-}" conf gpu
  role="${role//[^a-z0-9-]/}"; conf="${ROLES_DIR}/${role}/role.conf"
  [[ -n "${role}" && -f "${conf}" ]] || return 1
  command -v docker >/dev/null 2>&1 && docker compose version >/dev/null 2>&1 || return 0
  command -v nvidia-ctk >/dev/null 2>&1 && return 1
  gpu="$(conf_any_value "${conf}" gpu)"
  [[ "${gpu}" == required\|* || " $(conf_value "${conf}" requires) " == *" nvidia-toolkit "* ]] && return 0
  [[ "${gpu}" == optional\|* ]] && command -v nvidia-smi >/dev/null 2>&1
}

# The changes recorded now (other than this one), one per line, tab-separated: scope (host, gateway, role:<slot>; a change
# publishing its role adds gateway), state (waiting or running), the change in words ("installing ollama (12 min so far;
# martlet-host-add-20261003-021239-9052, from Martlet)"), how it was started, when, and its session. A record whose change
# ended, however it ended, is no longer locked and goes.
holders() {
  local file started words session how scope state minutes now
  [[ -d "${HOLDERS}" ]] || return 0
  now="$(date -u +%s)"
  for file in "${HOLDERS}"/*; do
    [[ -f "${file}" && "${file##*/}" != "${HOLDER_ID}" ]] || continue
    if ( exec 8<"${file}" && flock -n 8 ) 2>/dev/null; then rm -f "${file}"; continue; fi
    started=""; words=""; session=""; how=""; scope=""; state=""
    { IFS= read -r started; IFS= read -r words; IFS= read -r session; IFS= read -r how; IFS= read -r scope; IFS= read -r state; } < "${file}" 2>/dev/null || true
    [[ "${started}" =~ ^[0-9]{1,12}$ && -n "${words}" ]] || continue
    minutes=$(( (now - started) / 60 )); (( minutes >= 0 )) || minutes=0
    if [[ "${state}" == waiting ]]; then words+=" (waiting for the changes running now; ${session}, ${how})"
    else words+=" (${minutes} min so far; ${session}, ${how})"; fi
    printf '%s\t%s\t%s\t%s\t%s\t%s\n' "${scope}" "${state}" "${words}" "${how}" "${started}" "${session}"
  done
  return 0
}

# The changes this one waits for, in words ("; "-separated): every running one (all), the running ones of one scope (host,
# gateway, role:<slot>), any setup or update including one still waiting its turn (host-any), or the waiting ones (waiting).
holder_text() {
  local filter="${1:-all}" scope state words how started session joined=""
  while IFS=$'\t' read -r scope state words how started session; do
    case "${filter}" in
      all) [[ "${state}" != waiting ]] || continue ;;
      waiting) [[ "${state}" == waiting ]] || continue ;;
      host-any) [[ " ${scope} " == *" host "* ]] || continue ;;
      *) [[ "${state}" != waiting && " ${scope} " == *" ${filter} "* ]] || continue ;;
    esac
    joined+="${joined:+; }${words}"
  done < <(holders)
  if [[ -z "${joined}" ]]; then
    case "${filter}" in
      waiting) ;;
      all|host|host-any) joined="$(legacy_holder_text)" ;;
      gateway) joined="another change updating the gateway" ;;
      *) joined="another change to this host" ;;
    esac
  fi
  printf '%s' "${joined}"
}

# engine.holder: the latest change that took engine.lock (all an engine from before changes ran side by side records).
legacy_holder_text() {
  local started="" words="" session="" how="" minutes
  { IFS= read -r started; IFS= read -r words; IFS= read -r session; IFS= read -r how; } < "${ENGINE_HOLDER}" 2>/dev/null || true
  if [[ ! "${started}" =~ ^[0-9]{1,12}$ || -z "${words}" ]]; then printf 'another martlet-host change'; return; fi
  minutes=$(( ($(date -u +%s) - started) / 60 )); (( minutes >= 0 )) || minutes=0
  printf '%s (%s min so far; %s, %s)' "${words}" "${minutes}" "${session}" "${how}"
}

# A console window closed while its engine waits for input leaves that engine running and holding its locks for good.
holder_hint() {
  local scope state words how started session found=0
  while IFS=$'\t' read -r scope state words how started session; do
    found=1
    console_hint "${how}" "${started}" "${session}"
  done < <(holders)
  [[ ${found} == 0 ]] || return 0
  { IFS= read -r started; IFS= read -r words; IFS= read -r session; IFS= read -r how; } < "${ENGINE_HOLDER}" 2>/dev/null || return 0
  console_hint "${how}" "${started}" "${session}"
}

console_hint() {
  [[ "$1" == "in a console" && "$2" =~ ^[0-9]{1,12}$ ]] || return 0
  (( $(date -u +%s) - $2 >= STALE_MINUTES * 60 )) || return 0
  if [[ "${MODE}" == docker ]]; then note "If that console was closed without finishing, stop it with: docker stop $3"
  else note "If that console was closed without finishing, stop it with: kill ${3#process }"; fi
}

busy_stop() {
  journal "busy, stopped without changing anything: $1"
  printf 'MARTLET-BUSY %s\n' "$1"
  printf '\n\033[33mThis host is busy: %s. Nothing was changed; try again when it finishes.\033[0m\n' "$1" >&2
  exit ${BUSY_EXIT}
}

# Records this change in engine.holders/ with its scope and state (waiting, then running), and in engine.holder once it
# runs. The record is locked (fd 7) while this run lives, so readers drop it once it ends, however it ends.
record_change() {
  local scope="$1" state="$2" session="process $$" how="automatic" id
  [[ "${MODE}" == docker ]] && session="${MARTLET_ENGINE_NAME:-engine}"
  if [[ -t 0 ]]; then how="in a console"; elif assume_yes; then how="from Martlet"; fi
  if [[ -z "${HOLDER_ID}" ]]; then
    HOLDER_START="$(date -u +%s)"
    id="$(printf '%s' "${session}" | tr -c 'A-Za-z0-9.-' '-')-${RANDOM}${RANDOM}"
    ( umask 077; mkdir -p "${HOLDERS}" && : >> "${HOLDERS}/.${id}" ) 2>/dev/null || return 0
    # Locked before it gets its name, so no reader finds it unlocked and drops it.
    if exec 7>>"${HOLDERS}/.${id}" && flock -n 7 && mv -f "${HOLDERS}/.${id}" "${HOLDERS}/${id}"; then HOLDER_ID="${id}"
    else rm -f "${HOLDERS}/.${id}"; return 0; fi
  fi
  printf '%s\n%s\n%s\n%s\n%s\n%s\n' "${HOLDER_START}" "${CHANGE}" "${session}" "${how}" "${scope}" "${state}" > "${HOLDERS}/${HOLDER_ID}" 2>/dev/null || true
  if [[ "${state}" == running ]]; then
    ( umask 077; printf '%s\n%s\n%s\n%s\n' "${HOLDER_START}" "${CHANGE}" "${session}" "${how}" > "${ENGINE_HOLDER}" ) 2>/dev/null || true
  fi
}

# Takes flock $2 (-s shared, -x exclusive) on fd $1. While other changes hold it, waits with this run's patience (or stops
# at once with MARTLET-BUSY) and says what it waits for: the changes holder_text $3 names. With $4 = changed, this run has
# already changed the host (an add or remove waiting to publish what it did), so stopping now would leave a role running
# but unpublished, or stopped but still published, and MARTLET-BUSY ("nothing was changed") would be untrue: it waits as
# long as it takes instead, saying so every minute.
acquire() {
  local fd="$1" mode="$2" filter="$3" changed="${4:-}" patience step what status=0
  flock -n "${mode}" -E ${BUSY_EXIT} "${fd}" || status=$?
  [[ ${status} != 0 ]] || return 0
  if (( status != BUSY_EXIT )); then journal "flock failed (${status}); running without that lock"; return 0; fi
  patience="$(lock_patience)"
  what="$(holder_text "${filter}")"
  [[ -n "${changed}" ]] || (( LOCK_WAITED < patience )) || busy_stop "${what}"
  journal "busy, waiting: ${what}"
  if [[ -n "${changed}" ]]; then
    note "This host is busy: ${what}. The rest of ${CHANGE} (publishing it through the gateway) waits for it to finish, however long it takes..."
  else
    note "This host is busy: ${what}. Waiting for it to finish before ${CHANGE}..."
  fi
  holder_hint
  while :; do
    step=60
    [[ -n "${changed}" ]] || { step=$(( patience - LOCK_WAITED )); (( step <= 60 )) || step=60; }
    status=0
    flock -w "${step}" "${mode}" -E ${BUSY_EXIT} "${fd}" || status=$?
    [[ ${status} != 0 ]] || break
    if (( status != BUSY_EXIT )); then
      journal "flock failed (${status}) while waiting"
      [[ -n "${changed}" ]] && return 0
      busy_stop "$(holder_text "${filter}")"
    fi
    LOCK_WAITED=$(( LOCK_WAITED + step ))
    what="$(holder_text "${filter}")"
    [[ -n "${changed}" ]] || (( LOCK_WAITED < patience )) || busy_stop "${what}"
    note "Still waiting: ${what}. Waited $(( LOCK_WAITED / 60 )) min."
    holder_hint
  done
  journal "lock free after waiting about ${LOCK_WAITED} s"
  note "That finished. Continuing with ${CHANGE}."
}

# Takes the locks a change needs ("changes side by side" above) before it changes anything.
hold_engine_locks() {
  [[ "${MARTLET_ENGINE_LOCKED:-}" != 1 ]] || return 0
  command -v flock >/dev/null 2>&1 || { journal "flock is missing; running without the engine lock"; return 0; }
  CHANGE="$(change_words "$@")"
  case "$1" in
    setup|update) CHANGE_SCOPE=host ;;
    add) if prepares_host "${2:-}"; then CHANGE_SCOPE=host; else CHANGE_SCOPE="role:$(role_slot "${2:-}")"; fi ;;
    remove) CHANGE_SCOPE="role:$(role_slot "${2:-}")" ;;
    # Waits for a setup or update; each role's own lock is taken (or the role skipped) as warm reaches it.
    warm) CHANGE_SCOPE=warm ;;
    *) CHANGE_SCOPE=gateway ;;
  esac
  ( umask 077; mkdir -p "${CONF}" "${LOCKS}" && : >> "${ENGINE_LOCK}" && : >> "${ENGINE_GATE}" ) 2>/dev/null || true
  exec 9>>"${ENGINE_LOCK}" || fail "Could not open ${ENGINE_LOCK}."
  exec 4>>"${ENGINE_GATE}" || fail "Could not open ${ENGINE_GATE}."
  LOCKING=1
  record_change "${CHANGE_SCOPE}" waiting
  if [[ "${CHANGE_SCOPE}" == host ]]; then
    acquire 4 -x host-any
    acquire 9 -x all
  else
    acquire 4 -s host-any
    acquire 9 -s host
  fi
  exec 4>&-
  case "${CHANGE_SCOPE}" in
    role:*)
      ( umask 077; : >> "${LOCKS}/${CHANGE_SCOPE#role:}.lock" ) 2>/dev/null || true
      exec 6>>"${LOCKS}/${CHANGE_SCOPE#role:}.lock" || fail "Could not open ${LOCKS}/${CHANGE_SCOPE#role:}.lock."
      acquire 6 -x "${CHANGE_SCOPE}" ;;
    gateway)
      ( umask 077; : >> "${LOCKS}/gateway.lock" ) 2>/dev/null || true
      exec 5>>"${LOCKS}/gateway.lock" || fail "Could not open ${LOCKS}/gateway.lock."
      acquire 5 -x gateway
      GATEWAY_HELD=1 ;;
  esac
  record_change "${CHANGE_SCOPE}" running
  export MARTLET_ENGINE_LOCKED=1
}

# Runs "$@" holding locks/gateway.lock: one change at a time writes the role records, the machine report and host.json and
# renews or restarts the gateway. pair, console, network-reset and machine hold it for their whole run already. An add or
# remove gets here after it changed the host, so it waits for the lock without giving up (acquire ... changed).
with_gateway() {
  if [[ ${LOCKING} != 1 || ${GATEWAY_HELD} == 1 ]]; then "$@"; return; fi
  ( umask 077; : >> "${LOCKS}/gateway.lock" ) 2>/dev/null || true
  exec 5>>"${LOCKS}/gateway.lock" || fail "Could not open ${LOCKS}/gateway.lock."
  acquire 5 -x gateway changed
  GATEWAY_HELD=1
  record_change "${CHANGE_SCOPE} gateway" running
  "$@"
  record_change "${CHANGE_SCOPE}" running
  exec 5>&-
  GATEWAY_HELD=0
}

# Removes this run's record as it ends (one killed outright is dropped by the next reader: its record is no longer locked).
finish_change() {
  [[ -z "${HOLDER_ID}" ]] || rm -f "${HOLDERS}/${HOLDER_ID}" 2>/dev/null || true
}

# Whether any change holds the engine lock right now (for status; takes nothing).
engine_busy() {
  [[ -f "${ENGINE_LOCK}" ]] && command -v flock >/dev/null 2>&1 || return 1
  local status=0
  ( exec 8>>"${ENGINE_LOCK}" || exit 0; flock -n -E ${BUSY_EXIT} 8 ) 2>/dev/null || status=$?
  [[ ${status} == "${BUSY_EXIT}" ]]
}
ask() {
  if assume_yes; then note "$1 Confirmed in Martlet."; return 0; fi
  local answer; read -r -p "$1 Type yes to continue: " answer; [[ "${answer}" == "yes" ]]
}

# Answers for --yes runs: KEY=VALUE lines on stdin up to a line "end" (read byte by byte, so later readers such as
# the gateway still see what follows). Only secret.<name> and choice.<VAR> keys are kept.
declare -A INPUT=()
INPUT_LOADED=0
load_input() {
  [[ ${INPUT_LOADED} == 0 ]] && unattended || return 0
  INPUT_LOADED=1
  local line
  while IFS= read -r line; do
    line="${line%$'\r'}"
    [[ "${line}" == end ]] && break
    [[ "${line}" =~ ^(secret|choice)\.[A-Za-z0-9_]+= ]] && INPUT["${line%%=*}"]="${line#*=}"
  done
  return 0
}
