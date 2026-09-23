# Local host control CLI

`Martlet.Gateway.Host` is a standalone Windows local-console executable over
the canonical permanent-pairing `Martlet.Gateway.Persistence.DurableGatewayHost`.
It has no engines registered: it advertises **no installed models or inference
availability**. It is not an installer, service, remote admin API or GUI.
No dependency on `Gateway.Trust`, Docker, a browser, provider, microphone or
model is introduced. No machine configuration is changed.

## Deliberate local control

Run the built `Martlet.Gateway.Host.exe` from an interactive Windows console.
No arguments, `help`, `--help` and `-h` print passive usage without storage,
key, DPAPI, authority or listener creation. Arguments accept only `init` or
`open` and exactly these three non-secret options, in any order:

```powershell
.\Martlet.Gateway.Host.exe init --state C:\ChosenExistingParent\PrivateMartletState --host-id my-host --origin https://127.0.0.1:9443
.\Martlet.Gateway.Host.exe open --state C:\ChosenExistingParent\PrivateMartletState --host-id my-host --origin https://127.0.0.1:9443
```

These are examples, not default paths. Select a private, absent child path on a
fixed local NTFS volume for `init`; its parent must already exist. The existing
owner creates and protects only that selected state directory. `open` requires
existing valid state and the expected host ID, never creates missing state,
never guesses a different identity and never repairs ACLs. An ID mismatch
closes the loaded owner rather than starting it. Loading may perform the
canonical owner's authenticated recovery or same-key certificate renewal.

Both actions show the selected path, host ID and canonical loopback HTTPS
origin and require the exact lower-case word `yes`; Enter, EOF or any other
bounded answer means **No**. No unattended approval switch or environment
variable exists. Redirected stdin, stdout **or stderr** is refused before
opening an owner and checked again at each input/disclosure boundary.
Input fields are printable ASCII with bounded lengths (path 240, identifiers
and display names 64); invalid/control/oversized input terminates with a
sanitized error and closes any owned resources. Argument values are never
echoed on parse errors. No configuration files or silent path defaults exist.

Opening does not start a socket. The local console retains the sole
`DurableGatewayHost`; no request handler or DI container receives its control
capability. The command loop offers:

| Command | Behavior |
| --- | --- |
| `start` | Separate default-No confirmation; start only the selected canonical loopback TLS listener. A repeated start does not create another listener. |
| `pair` | Requires a started listener. Read exact device ID, display name and comma-separated roles (`voice`, `perception`, `memory`); show them with host ID and SPKI pin for review, then require default-No approval before invitation creation and disclosure. |
| `list` | Show non-secret paired registrations and roles; **not** connected-session status. No connection tracking or inference readiness is invented. |
| `revoke` | Read an exact device ID and require default-No confirmation; durably revoke its credentials and cancel its outstanding invitations. No reset-all action exists. |
| `stop` | Require default-No confirmation, then drain and close the owner without unpairing devices. |
| `help` | Show bounded usage. |

The invitation has **one use and five minutes**; that is not device lifetime.
After the pairing client's successful protocol-2 exchange, device authority is
**permanent until deliberate revocation**, including ordinary restarts, boots,
offline periods and supported-format updates. Use `list` to confirm that the
exchange actually produced a paired registration. Closing an invitation screen
does not consume/cancel its invitation: it remains available until its deadline,
use, device revocation or owner shutdown.

### Invitation disclosure

Disclose only with screen sharing/recording disabled and observers excluded.
The approved invitation is shown using `WriteConsoleW` into a newly owned
native console screen buffer, **not** stdout/stderr, a log, URL, clipboard or
command argument. The original screen buffer is restored and the temporary
buffer erased and closed after a key, deadline or cancellation. The native
console must support screen-buffer switching and have a buffer at least
80 columns by 20 rows; unsupported/redirected consoles fail closed. No fallback
prints the token. Never use shell transcripts or terminal recording.

Enter the invitation only into a compatible protocol-2 pinned **local** client
after comparing the exact host ID and SPKI pin. Its client must keep its issued
device secret in its own protected credential ownership; this CLI never
receives or saves that secret. No companion client GUI is shipped here.
Do not install the certificate in a trust store or bypass pin validation.
Managed invitation strings cannot be reliably zeroed; this is not protection
against same-user code, a compromised terminal/OS, administrators, screenshots
or memory inspection. **Local OS-session authority is not proof of physical
presence.**

## Stop and recovery

EOF (`Ctrl+Z` in the Windows console), Ctrl+C and exceptions close the owned
listener and then call `CloseCleanlyAsync`; no cleanup path unpairs devices.
Ctrl+C cancels input/operations, not cleanup. The owner retains its existing
30-second listener-drain bound. If clean closure fails, the CLI reports it,
attempts one final disposal/drain, and keeps any still-owned object alive until
process termination. It does not print clean success or release the store
under an uncertain live listener. No global process kill is used.
Synchronous DPAPI and filesystem flush remain subject to the existing owner's
OS I/O behavior; no hard disk-I/O wall-clock guarantee is claimed.

Exit codes: `0` passive help or clean session completion (including EOF),
`2` invalid input/configuration, `3` initial refusal/noninteractive console,
`4` operational/platform/state failure, `5` uncertain clean closure, `130`
Ctrl+C cancellation after cleanup. Refusing an individual console command
leaves the interactive session open; it does not turn the refusal into an
authority-changing action. EOF at any prompt, including a confirmation,
closes the session immediately rather than waiting for another command.
Output failure cannot interrupt cleanup or be mistaken for failed listener
drain; when diagnostics cannot be written, the process still reports a
nonzero exit code.

Errors expose bounded canonical failure codes, readable explanations and
recovery guidance, not raw exception/argument/state contents. Preserve the
original state and reopen it after resolving storage/clock/ownership problems.
Missing/malformed/insecure state is never replaced. Known old timed formats
remain **MigrationRequired**: this is not a migration/reset utility.
The canonical owner automatically renews expiring or offline-expired
certificates with the same protected key/pin. There is no manual rotation,
trust-store installation or arbitrary maintenance command.

## Local validation and boundaries

Build/test these projects directly (they do not change root payload packaging
or the legacy solution list). Set `DOTNET_ROOT` and prepend the existing
10.0.401 SDK directory to `PATH`; set `CI=true`,
`DOTNET_GENERATE_ASPNET_CERTIFICATE=false`,
`DOTNET_SKIP_FIRST_TIME_EXPERIENCE=true` before invoking the SDK. Use a unique
local C: `ArtifactsPath`, results directory and `TEMP`/`TMP` directory.

```powershell
dotnet restore tests\Martlet.Gateway.Host.Tests\Martlet.Gateway.Host.Tests.csproj --locked-mode -p:ArtifactsPath=C:\ChosenBuildArtifacts
dotnet test tests\Martlet.Gateway.Host.Tests\Martlet.Gateway.Host.Tests.csproj -c Release --no-restore -p:ArtifactsPath=C:\ChosenBuildArtifacts --results-directory C:\ChosenTestResults
```

Tests invoke the real command parser/control loop via an **internal-only**
test-console binding, native Windows state and actual Kestrel plus the pinned
client. They cover passive/default-No/redirected paths, protocol-2 permanent
pairing, one-use invitations, empty metadata, replay across reopen, listing,
revocation, clean cancellation/error/EOF, malformed/old-state preservation,
real executable help/noargs/refusal subprocesses and an owned-PID interruption.
An isolated subprocess also allocates its own native console and verifies
actual private-screen disclosure, erasure and restoration plus bounded input.
All identities, credentials and state are generated in owned temporary paths.
There is no production test/approval bypass.

Actual OS reboot, Linux, graphical terminal compatibility across all terminal
hosts, two-host/LAN pairing, installation and updates are **not qualified**
here. Ubuntu's graphical wizard and host-journal integration are separate.
Tablets/other PCs cannot pair over this loopback-only listener; LAN/backend
qualification requires a separately reviewed deliberate change. No discovery,
port scan, firewall/service install, arbitrary shell, remote admin credentials,
engine worker or remote CI is added.
