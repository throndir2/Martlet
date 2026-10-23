# shellcheck shell=bash
# martlet-host module (deploy/host/lib/pairing.sh), sourced by deploy/host/martlet-host; it is not run by itself.
# Setting up the host and pairing desktops: the commands setup, pair, network-reset,
# exposure and console.

cmd_setup() {
  [[ $EUID -ne 0 ]] || fail "Run as your normal user; martlet-host asks before using sudo."
  if [[ "${MODE}" == native ]]; then
    [[ "$(host_arch)" == x64 || "$(host_arch)" == arm64 ]] ||
      fail "Only 64-bit Intel/AMD (x86_64) and ARM (aarch64) hosts are supported; this computer is $(uname -m)."
    command -v systemctl >/dev/null 2>&1 ||
      fail "Native setup needs a Linux with systemd (it runs the gateway as a user service); use the Docker method elsewhere."
    [[ -f "${REPO}/src/Martlet.Gateway.Host.Linux/Martlet.Gateway.Host.Linux.csproj" ]] || fail "Run from a Martlet source checkout."
    say "1/4 Martlet gateway"
    # Everything a bare-metal minimal Ubuntu may lack, checked up front so setup asks at most once. Supplied (offline)
    # hosts download nothing and may have no apt mirror, so they only need what the steps below check for themselves.
    [[ -n "${SUPPLY}" ]] || require_packages "this host's setup" curl:curl "${CA_BUNDLE}:ca-certificates" lspci:pciutils
    require_dotnet
    publish_gateway "${GW_APP}"
    mkdir -p "${DATA}/private"; chmod 700 "${DATA}" "${DATA}/private"
    secure_data_parents
  else
    say "1/4 Martlet gateway (container image ${IMAGE})"
  fi

  say "2/4 Network address"
  mkdir -p "${CONF}/roles" "${PROJECTS}"; chmod 700 "${CONF}"
  [[ -f "${CONF}/host.env" && -z "${MARTLET_HOST_ADDRESS:-}" ]] || choose_address
  note "Desktops will connect to https://$(settings_value lan_ip):$(settings_value port)"
  write_config
  if [[ "${MODE}" == native ]]; then
    local lan; lan="$(settings_value lan_ip | cut -d. -f1-3).0/24"
    if systemctl is-active --quiet ufw 2>/dev/null &&
       ask "The ufw firewall is active. Allow TCP $(settings_value port) (the gateway) and UDP 9444 (so your computers find it after its address changes) from your private LAN?"; then
      sudo ufw allow proto tcp from "${lan}" to any port "$(settings_value port)"
      sudo ufw allow proto udp from "${lan}" to any port 9444
    elif systemctl is-active --quiet firewalld 2>/dev/null &&
       ask "The firewalld firewall is active. Allow TCP $(settings_value port) (the gateway) and UDP 9444 (so your computers find it after its address changes) from your private LAN?"; then
      local rule="rule family=ipv4 source address=${lan} port port=$(settings_value port) protocol=tcp accept"
      local find_rule="rule family=ipv4 source address=${lan} port port=9444 protocol=udp accept"
      sudo firewall-cmd --permanent --add-rich-rule="${rule}" && sudo firewall-cmd --add-rich-rule="${rule}"
      sudo firewall-cmd --permanent --add-rich-rule="${find_rule}" && sudo firewall-cmd --add-rich-rule="${find_rule}"
    fi
    mkdir -p "${HOME}/.local/bin"
    ln -sf "${HERE}/martlet-host" "${HOME}/.local/bin/martlet-host"
  fi

  say "3/4 Gateway identity"
  if [[ ! -d "${GW_STATE}" ]]; then
    service_stop
    if assume_yes; then
      note "Creating this host's permanent gateway identity and approving its unattended start."
      owner_operation owner-init --config "${GW_CONFIG}" || fail "The gateway identity was not created."
    else
      cat <<'TEXT'
The gateway console opens to create this host's identity. Confirm creation with: yes
Then type:  approve-service  (confirm: yes), then  stop  (confirm: yes)
TEXT
      gateway init --config "${GW_CONFIG}"
    fi
    CONFIG_CHANGED=0
  fi

  say "4/4 Start automatically"
  if [[ "${MODE}" == native ]]; then
    mkdir -p "${HOME}/.config/systemd/user"
    cat > "${HOME}/.config/systemd/user/${SERVICE}.service" <<UNIT
[Unit]
Description=Martlet host gateway
After=network-online.target

[Service]
Environment=DOTNET_ROOT=${HOME}/.dotnet
Environment=DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=${DOTNET_SYSTEM_GLOBALIZATION_INVARIANT:-0}
ExecStart=${DOTNET} ${GW_APP}/Martlet.Gateway.Host.Linux.dll serve --config ${GW_CONFIG}
Restart=on-failure
RestartSec=10

[Install]
WantedBy=default.target
UNIT
    systemctl --user daemon-reload
    systemctl --user enable "${SERVICE}.service" >/dev/null
    if ! loginctl show-user "${USER}" -p Linger 2>/dev/null | grep -q 'yes' &&
       ask "Keep the gateway running after reboot without logging in (sudo loginctl enable-linger ${USER})?"; then
      sudo loginctl enable-linger "${USER}"
    fi
    tpm_native_access
  else
    note "The gateway container restarts with Docker (restart policy unless-stopped)."
  fi
  note "Collecting this machine's hardware for paired desktops..."
  collect_machine
  show_machine
  refresh_gateway
  reconnect_roles
  say "Host ready. Next: 'pair' to connect a desktop, and 'add <role>' (see 'roles')."
}

cmd_pair() {
  local device="" name="" roles="voice" usage="Usage: martlet-host pair [--roles voice] | pair --device-id <id> --name <display name> [--roles voice]"
  while [[ $# -gt 0 ]]; do
    [[ $# -ge 2 ]] || fail "${usage}"
    case "$1" in
      --device-id) device="$2" ;;
      --name) name="$2" ;;
      --roles) roles="$2" ;;
      *) fail "${usage}" ;;
    esac
    shift 2
  done
  [[ -z "${device}" || -n "${name}" ]] || fail "Add --name <display name> for that desktop."
  [[ -n "${device}" || -z "${name}" ]] || fail "Add --device-id <id> for that desktop, or leave out --name to show a code."
  require_setup
  collect_machine
  service_stop
  # However pairing ends (including a dropped SSH connection or Ctrl+C), the gateway service comes back.
  trap 'code=$?; trap - EXIT; service_start >/dev/null 2>&1 || true; finish_change; journal "exit ${code}"' EXIT
  trap '' PIPE
  local status=0
  if [[ -n "${device}" ]]; then
    note "Pairing ${device} (${name}). Martlet reads the one-use code below by itself."
    note "Type cancel (or close stdin in Martlet) to withdraw it; it expires after five minutes."
    gateway owner-pair --config "${GW_CONFIG}" --device-id "${device}" --name "${name}" --roles "${roles}" || status=$?
  else
    # Shows this host's address and a one-use XXXX-XXXX code to type on the desktop, then waits for it.
    gateway owner-pair --config "${GW_CONFIG}" --roles "${roles}" || status=$?
  fi
  trap 'code=$?; finish_change; journal "exit ${code}"' EXIT
  service_start
  [[ ${status} == 0 ]] || fail "Pairing did not complete."
  say "Paired. The gateway is serving again."
}

# Leaves this host's Martlet network (network.json); pairings stay. The next desktop that pairs adds the host to its own
# network. For a host whose network's desktops are all gone, or to move it to another network.
cmd_network_reset() {
  require_setup
  ask "Leave this host's Martlet network? Paired desktops keep their pairings (revoke them with martlet-host console)." ||
    fail "Nothing changed."
  local status=0
  owner_operation owner-network-reset --config "${GW_CONFIG}" || status=$?
  service_start
  [[ ${status} == 0 ]] || fail "The network was not reset."
  say "This host is in no Martlet network now."
}

# How this host is reached from outside home (exposure.json beside host.json; docs/NETWORK.md). No options prints the
# current choices; with options the gateway saves them and restarts to apply them. The gateway checks every option.
cmd_exposure() {
  require_setup
  if [[ $# -eq 0 ]]; then
    gateway owner-exposure --config "${GW_CONFIG}"
    return
  fi
  ask "Change how this host is reached from outside home? The gateway restarts to apply it." || fail "Nothing changed."
  local status=0
  owner_operation owner-exposure --config "${GW_CONFIG}" "$@" || status=$?
  service_start
  [[ ${status} == 0 ]] || fail "Nothing was changed (see above)."
  say "Saved. The gateway serves with it now."
}

# The gateway's interactive console: list and revoke paired desktops (and the older pasted-card pairing).
cmd_console() {
  require_setup
  service_stop
  cat <<'TEXT'
In the gateway console confirm opening with: yes, then type:
  list    shows the paired desktops
  revoke  enter a desktop's device ID to remove its pairing (confirm: yes)
  stop    (confirm: yes)
To pair a new desktop, use 'martlet-host pair' instead: it shows a short code to type in Martlet.
TEXT
  gateway admin --config "${GW_CONFIG}"
  service_start
}
