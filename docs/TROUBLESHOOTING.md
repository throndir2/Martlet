# Desktop troubleshooting and local support (V06b)

## Crashes and unexpected errors (error log)

Martlet always keeps a small local error log; it is never uploaded.

- Location: `%LOCALAPPDATA%\Martlet\logs\` (or `<--data-directory>\logs\`).
  `desktop.log` is the app; `avatar-renderer.log` is the character process;
  `host-runs.log` is the output of every Martlet host run window (set up this
  PC, add a host, pair, add/remove a role), exactly as shown, plus each change
  of the window's status line (`status: ...`), with pairing codes
  masked (rotates at 2 MiB, keeps one older copy). `desktop.log` also records
  each host run's start and result. Martlet never opens a console window.
  `desktop.log` and `avatar-renderer.log` rotate at 2 MiB and keep 3 older copies.
- Contents: startup/exit lines plus every unhandled exception, failed background
  task and unexpected avatar-renderer exit, with full exception type, message
  and stack trace. Exception messages can include local paths or provider error
  text; review a log before sharing it.
- Failed provider requests (Thinking, Listening, Voice, screen glances and
  Memory, cloud or Martlet host) add a `WARN` line with the endpoint, model, HTTP
  status and the provider's own short explanation (at most 400 characters,
  key-like strings redacted), then a line such as `Reply failed (...)`,
  `Transcription failed (...)` or `Remembering failed (...)` naming the route
  and failure codes. Request content, headers, keys and what you said are never
  logged. For example a model the provider retired shows `HTTP 410 Gone ...
  has reached its end of life`; the talk window then says to choose another
  model in Companion › Thinking.
- Behaviour: an unexpected error on the UI thread is logged and shown once in a
  dialog, and Martlet keeps running instead of closing. The dialog shows the
  error, where the log is and the full exception; its *Copy* button takes the
  whole report (with the Martlet version) to paste into a chat when asking for
  help, and *Open logs folder* opens the logs. If Martlet can't start, the same
  dialog says why before it closes. Failures that cannot be
  recovered (out of memory, stack overflow, native access violations) are
  logged where the runtime allows, then the process ends.
- Copy buttons: every read-only text box (run window output, *Prepare this
  computer* output, setup, troubleshooting and diagnostics status, recovery
  previews) has a *Copy* button above its top-right corner that takes all of its text, and
  confirmation and install dialogs have a *Copy* button for their message. A
  run window's Copy puts the Martlet version, the run's title and its status
  line above the output. The button reads *Copied* for a few seconds. Review
  what you paste: output can include local paths, computer names and
  provider error text.
- After a crash, kill or power loss, the next launch's Home says Martlet
  closed unexpectedly last time, with *Open Diagnostics* and *Open the logs
  folder*. Unexpected errors while Martlet runs also show on Home with the
  latest one and the same buttons.
- Native crashes that bypass .NET (for example an access violation inside a
  Windows DLL on one of its own threads, which leaves no managed stack) can't
  be logged by the crashing process. On the next launch Martlet reads how
  Windows recorded that run's end from the Application event log (`Application
  Error` and `.NET Runtime` entries for the run's process ID) and writes a
  `WARN` line such as `Windows recorded how the previous run (pid 43136,
  started ...) ended: Martlet.Desktop.exe 0.17.0.0: an access violation
  (0xc0000005) in MMDevApi.dll 10.0.22621.5547 at offset 0x20103 (Windows
  report ...).`, followed by the runtime's own text (with the managed stack
  when there is one). Home's crash notice then names the exception and module.
  When Windows has no record, an `INFO` line says the run was probably ended by
  Task Manager, a sign-out, a shutdown or power loss, or that Windows Error
  Reporting is off. Each process (`desktop`, `avatar-renderer`) checks its own
  previous run. Nothing is uploaded.
- Read every log in one list, filtered by level, part, computer and text, on
  the **Diagnostics** page; it shows the logs of all your computers and hosts,
  which share them with each other. To send them to someone helping you,
  **Save logs to share** there makes one ZIP with everything. See
  [Diagnostics page and shared logs](DIAGNOSTICS.md#diagnostics-page-and-shared-logs).
  `desktop.log` also records each message shown in the main window's status
  line (`Status: ...`) and hosts that stop or start answering again.
- Open the folder any time from **Troubleshooting > Open crash / error logs**.

Native crashes are also listed in Windows Event Viewer > Windows Logs >
Application (sources `.NET Runtime` and `Application Error`).

## Missing prerequisites (character, microphone, Windows speech, local LLM, Docker)

If the character does not appear, push-to-talk hears nothing, Windows speech
finds no recognizer or voice, or Martlet hosts cannot find Docker Desktop, run
Start > **Martlet prerequisites** (or **Prerequisites (check / install)** on the
home screen). It lists the WebView2 runtime, microphone access for desktop apps,
Windows speech for your language, Ollama, the NVIDIA driver and WSL 2 + Docker
Desktop with their status, and installs or opens the setting for the item you
choose. Nothing changes until you pick an item. `-Check` prints the same status
from a command line.

When a run window (for example *Speak with F5 on this PC*) waits for Docker
Desktop, its output shows each engine check (`docker info`, cut off after 15
seconds because Docker Desktop holds requests while its engine starts), what
Docker Desktop reports (`docker desktop status`) and the warnings and errors of
its current run (`docker desktop logs`), every 30 seconds while it waits. When
Docker Desktop answers that it is unable to start, or that its start check
failed (*Virtualization support not detected*), Martlet first checks whether
Windows is the reason (below); if Windows is ready, it restarts Docker Desktop
once, and stops with Docker's words when it fails again instead of waiting;
otherwise it gives up after ten minutes. All of it is also in `host-runs.log`.

## Docker Desktop is unable to start (virtualization, WSL 2)

Docker Desktop's WSL 2 engine needs hardware virtualization turned on in the
PC's firmware (UEFI/BIOS), the **Virtual Machine Platform** and **Windows
Subsystem for Linux** features, the Windows hypervisor running and WSL 2.1.5 or
later, with Windows' **Host Compute Service** (`vmcompute`) and **Host Network
Service** (`hns`) installed and not disabled. Before Martlet starts Docker
Desktop for any host step on this PC (and
after it installs Docker Desktop), it checks these without administrator rights
and reads `wsl --status` and Windows' pending-restart markers, showing the result
in the run window (`Windows: ...`). WSL can report that WSL 2 cannot start even
when its status command exits successfully; a version number alone is not
readiness. A stopped service is normal when it starts on demand, and WSL 1 being
unavailable does not prevent Docker's WSL 2 engine. When something is off:

- **Features or WSL**: one administrator prompt turns on both features (with
  their parent features), sets the Windows hypervisor to start with Windows when
  its boot entry turned it off, and installs WSL from Microsoft
  (`wsl --install --no-distribution`, falling back to `wsl --update`) or
  updates it. When WSL itself says Windows must restart to finish installing,
  Martlet treats it like any other restart below.
- **Restart already pending**: Windows can report both optional features as
  *on*, a current WSL version and a running hypervisor while the services WSL 2
  needs have not been installed into the running Windows session. Docker then
  says *Virtual Machine Platform not enabled* / *No virtualization available*.
  When those runtime checks fail and Windows has a restart pending, Martlet
  offers **Restart Windows** immediately, before another administrator step,
  Docker restart or ten-minute wait. Restarting Docker alone cannot finish this
  Windows change. An unrelated pending update does not block working WSL.
- **Restart**: when Windows must restart, Martlet asks first (save your work in
  other apps), then restarts. It leaves `continue-setup.json` in its data folder
  and a one-time sign-in entry (`MartletContinueSetup` under the current user's
  `RunOnce` key), so after you sign in Martlet opens by itself and continues:
  this PC's host service and pairing, the host dashboard's host service, or
  starting Docker Desktop so you can repeat the step that needed it. Choosing
  *No* keeps the same continuation for your next restart.
- **Firmware**: Windows can't turn firmware virtualization on. Martlet offers
  to restart straight into the firmware settings (administrator approval), where
  you turn on *Intel Virtualization Technology (VT-x)* or *SVM Mode* (AMD), save
  and exit; the setup then continues after you sign in. Until then Docker
  Desktop says *Virtualization support not detected*. On a fresh PC, where the
  Windows features and WSL are off too, Martlet turns those on first (one
  administrator prompt; they don't need virtualization), so that one restart
  into the firmware settings finishes both.
- **Virtual machine**: enable nested virtualization on its host, not in the
  guest's firmware. Martlet explains this rather than restarting the guest into
  firmware settings.
- **Services or WSL still unavailable**: Martlet stops with the specific problem
  instead of claiming Windows is ready. Restart Windows and check again; if the
  services remain missing/disabled, repair the Windows Virtual Machine Platform
  and WSL installation with administrator help. Martlet does not toggle features
  off, delete distributions or reset Docker's data.

The host dashboard's Docker Desktop step says the same thing, including
**Restart Windows**, even when Docker Desktop's window is already open. **Check
again** refreshes its Windows diagnosis when the engine is down; a window or
background process alone never ticks the Docker step as running. A Docker
Desktop that was already open
while Martlet changed Windows doesn't notice the new WSL by itself, so Martlet
restarts it. Martlet also restarts it once when it is open but keeps reporting
its engine `stopped` (its log says *backend is not running*), for example
because it started before WSL was installed.

Docker Desktop checks Windows only when it starts, and keeps showing a failed
check (*Virtualization support not detected*, *Virtual Machine Platform not
enabled*; its log says *checking preconditions: ...*) until it starts again.
So if you opened it before Windows restarted, it still says so afterwards. When
Windows is ready, Martlet reads that failed check (or *unable to start*) from
Docker Desktop and restarts it once instead of waiting. If it says the same
again after that, the run stops with its words; restart Windows and try again,
and check that Task Manager › Performance › CPU shows *Virtualization:
Enabled*. If Docker Desktop never gets past `stopped`, restart Windows or use
Docker Desktop's *Troubleshoot > Restart*, and try again. Do not use *Reset to
factory defaults* as a virtualization repair: it deletes Docker data and does
not enable Windows virtualization.

## A local model doesn't answer (Ollama on this PC)

On **Companion › Thinking › This PC**, choose **Test model**. It starts Ollama
if it is installed but not running, checks the model is downloaded, loads it
and asks it for a short streamed reply through the same endpoint and reply
budget as Martlet's replies, all over loopback. The run window shows each step
(Ollama's version, the model's parameters and whether it sees images and uses tools,
how long loading took, whether it sits on the graphics card or the processor,
and the reply), with Ollama's own error text when something fails (for example
not enough memory, or a model that needs a newer Ollama). The result stays under
the buttons. A model that loads more slowly than Martlet waits for a reply to
start, or answers too slowly once loaded, passes with a warning. *Install Ollama
and use it* runs the same test at the end.

Ollama unloads a model after five idle minutes and loading it again takes from
about 15 seconds to a couple of minutes; it gives up the load if the request
that started it stops waiting. So with Ollama on this PC, Martlet waits up to
two minutes for a reply to start, and the talk window has Ollama load the model
as it opens and again when you start typing or talking after a quiet spell; its
status line says *Ollama is loading <model> on this PC…* meanwhile, or why
Ollama can't (not running, model not downloaded, Ollama's own error). Ollama on
this PC has no reply token budget unless you set **Max reply length** on
**Companion › Replies**, so thinking models can reason before they answer.
If replies take a few seconds to start because the model thinks first (Test
model says *... is thinking first...*, or the desktop log's *Reply latency*
line shows *hidden reasoning*), check that **Thinking steps** on **Companion ›
Replies** is **Off** (the default): the model answers straight away. A model that always
thinks refuses Off; the log then says *refused Thinking steps Off* and Martlet
uses the model's default for it (choose a model that can skip thinking for the
fastest replies). See [Voice latency](VOICE_LATENCY.md).

### Martlet forgets the start of a long conversation

Each reply sends the newest exchanges of the open talk window that fit the
**Context size** on **Companion › Replies** (its line says how big the context
is and why; the talk window says how much of it the conversation uses). Blank is
100,000 tokens for a cloud model, or the model's own limit when that is smaller
and Martlet knows it (**Check model limit** asks the server). Ollama on this PC
holds what its own context length setting gives the model (4,096 tokens on a
graphics card under 24 GB unless raised): raise it in the Ollama app's settings
or with `OLLAMA_CONTEXT_LENGTH`, then check again on **Replies**, test the model
or reopen the talk window, which records it for the next conversation. A larger
context uses more graphics memory. A paired host's Ollama loads up to 32,768.
If a cloud model fails on long conversations ("context length exceeded"), set a
smaller context size.

### "Gemma4Assistant requires ctx_other to be set … error loading model: vector"

Ollama (and its own chat window) shows this as *500 Internal Server Error:
llama-server process has terminated: exit status 1* when a Gemma 4 model
doesn't fit in the graphics memory that is free. The *ctx_other* line is a
harmless warning. Gemma 4 models bring a small draft model for faster replies;
once the main model fills the card (a game, Martlet's character and other
programs share it), Windows reports no free graphics memory and Ollama's
llama.cpp fails to load the draft model with *error loading model: vector*
(llama.cpp issues [27440](https://github.com/ggml-org/llama.cpp/issues/27440)
and [28964](https://github.com/ggml-org/llama.cpp/issues/28964)). Martlet's
*Test model* and talk window then turn the draft model off for that model
(`draft_num_predict 0`, which then also applies in Ollama's own chat window) and load it
again. A model that still overfills the card is paged out to system memory and
replies stall for minutes; *Test model* says it doesn't fit or didn't answer
and names a smaller model. Choose a smaller one (for example `gemma4:e4b`
instead of `gemma4:12b` on a 12 GB card while gaming), or close programs that
use the graphics card.

## Local configuration backup / restore (V07a)

**Configuration backups are NOT support bundles.** The support ZIP described
below contains a deliberately lossy, redacted projection and cannot restore
settings. Use **Configuration backup / restore (local only)** from the main
window, or **Local configuration backup / restore** in Setup / resume
(including Setup opened from real conversation). Each path uses the same
app-lifetime recovery owner and shared effect slot; nested recovery is owned
by the active Setup dialog, not a disabled parent window.
Opening this screen is passive: no backup read/scan/create, vault, diagnostics,
device, network or upload action. Saved settings and device identifiers can be
personal. Configuration envelopes are LOCAL and **NOT encrypted or sanitized**.

1. Review scope. Only actual validated settings/profile, route/model/voice and
   device preferences, opaque credential references and cleanup metadata are
   captured. No secret values, vault, environment, conversation/audio, arbitrary
   files, crash dumps, models or optional support journal/logs are included.
   The separately owned local-memory facts/store/exports are excluded; only
   memory enable/path settings are included.
2. Choose a new explicit local `.martlet-config` output in an existing parent
   directory. **Create local configuration snapshot** asks for confirmation.
   Output is create-only; an existing file is never overwritten. No cloud
   storage or account sync is provided.
3. For restore, browse a selected file, then explicitly **Read exact restore
   preview**. Browsing is not importing or approval. Bounded parsing checks the
   application's versioned envelope, exact settings schema/known fields and
   integrity digests. Review compatibility, source digest, destination/profile,
   current revision, changed roles/devices and the **entire exact candidate JSON**.
4. **Review and confirm this exact restore** defaults to **No**. Approval applies
   once to this source digest, destination profile/store and current revision.
   A source/selection/revision change invalidates it; read a fresh preview.
   Restore uses only the frozen candidate bytes, never unseen replacement input.
5. Reopen/reload Setup to review destinations, enter keys explicitly and retest
   devices. Preferences are recovered, but every imported destination
   acknowledgment, live key binding and audio checkpoint is invalidated. Capture
   and logging remain OFF. Current owned keys are detached into tracked cleanup
   metadata, **not deleted**. Current legacy references/pending removals survive;
   obsolete imported IDs do not authorize key use or deletion.

**V07a supports only an existing valid same-profile v1-v4 destination.** Memory
is always forced OFF with a fresh revision, without opening or copying its store.
A missing,
foreign-profile, corrupt or future-version destination is refused; this is not
portable profile import or corrupt-store repair. Preserve originals and use
compatible manual recovery, not a reset or an older executable. If current
active keys plus pending removals exceed sixteen, explicitly clean up selected
detached keys in Setup before previewing again. No automatic vault deletion.

Every replacement first preserves exact current raw bytes in a new
`settings.recovery.<uuid>.bak` beside settings. Prior recovery and historical
v1 migration snapshots are never overwritten/pruned. These internal raw files
are recovery evidence, not directly importable `.martlet-config` envelopes.
On access denied/disk-space/locked-writer errors, preserve all originals, check
the location/free space and other Martlet processes, then retry. Do not elevate,
disable protection or delete evidence to force success.

Stop/Pause/close live actions and **stop/close Troubleshooting**, including its
cleanup, before recovery. The same app effect slot excludes setup/vault/live/
audio work. Slow IO may outlast the five-second observation deadline. Stop or
Close requests cancellation but never claims rollback, releases a live writer
early or automatically resumes it. Reopen to inspect its actual result/receipt.
Use **Retry owned staging cleanup** if indicated; only that exact temporary
file is targeted. Main Exit waits for this owner. After a process crash, preserve
any temporary/raw snapshot evidence for explicit manual inspection; startup
does not scan or resume recovery. Atomic rename/replace and flushed files are
tested with controlled faults, not qualified against physical power loss.

No binary switching, installer upgrade/uninstall, account sync or automatic
update is implemented by V07a. Clean-VM N-1/N rollback, OS-vault roundtrip,
physical devices, novice trials, signing, rights and G2 remain NOT RUN / NOT
PASSED. No old executable is launched against a new schema.

## Local support workflow

**Internal functionality, not a support service or a passed release gate.**
Open **Troubleshooting** from the main window, Setup / resume, Audio setup or
real conversation. It is available before a profile exists and when settings
are malformed. Opening the window only displays existing shared report/state
observations: no journal start/read, settings write, directory sweep, vault
access, device enumeration/capture/playback, HTTP, inference or upload. The
shared local status includes actual provenance, freshness, codes and authored
remedies, not another readiness evaluator. Refresh explicitly requests the same
read-only local Doctor checks. Fixture evidence remains NOT inference.

The window is nonmodal so the existing app actions remain usable while metadata
recording is explicitly enabled. Existing live-window deactivation/permission
revocation still applies when switching windows; troubleshooting does not grant
permission or relax conversation safeguards. Audio setup remains the place for
specific local microphone/output tests and remedies.

Opening Troubleshooting inside an active modal setup/audio/conversation workflow
transfers its presentation into that workflow. The previous disabled window's
timer/content bindings retire; the same support controller, recording state and
outstanding IO remain owned. It does not start or restart recording, and no
preview confirmation transfers. Closing Troubleshooting or its owner retires
the presentation and requests Stop even when WPF skips the child's Closing event.

## Record only when needed

**Record troubleshooting metadata** is OFF at every launch. There is no saved
opt-in, startup collector or telemetry permission. It starts the existing
Support journal in the selected settings data directory's `support-v1` child.
The default settings location is LocalApplicationData/Martlet; an explicit
`--data-directory` selects its own child, including isolated test locations.
Opening the window displays this proposed path but does not claim it is writable.

Only an explicit Start validates/creates that trusted user-owned local scope.
Network/UNC/mapped-network/reparse/alternate-stream paths are rejected by the
engine, never silently redirected. Fix the location through an explicit valid
local launch data directory; do not elevate, disable protection, delete evidence
or reset invalid settings to make recording work.

Recorded observations are completed shared local refresh reports, terminal
fixture reports and sampled changes to the existing conversation UI's typed
stage/status. Conversation mapping accepts only known states, enum metadata,
bounded counters and correlation UUIDs. It records neither input/transcript/
answer/refusal nor arbitrary error/provider text. The existing engine handles
allowlisting and per-bundle pseudonymization. No second logger/redactor exists.
Pre-provider local stages describe local action observations, not provider
readiness; actual runtime/STT provenance remains live/fixture/not-run as supplied.

There is one support worker, independent of the setup/conversation effect slot,
and **no pending append queue**. At most one batch of 64 probe events runs; a busy
batch is dropped with a visible count. Conversation state sampling is not a
lossless event collector: unchanged ticks are ignored, at most 32 changed
observations per action are offered while recording, and excess changes are
counted. Unprojectable or stale-pass metadata is visibly omitted, never logged
as raw fallback or a successful record. The window shows dropped/omitted counts;
these app-session counters are not an extra bundle file. Running/terminal states
that occur between UI samples can be missed.

**Stop / cancel support work** or closing Troubleshooting turns recording off
and requests owned cleanup. Start again only explicitly. Slow native IO cannot
be forcibly canceled: five seconds ends UI observation, not worker ownership or
rollback. The original cancellation token reaches engine approval/export.
The worker remains reserved through actual IO and cancellation callbacks.
Reopened windows show the same busy/cleanup owner; no replacement writer starts.
Failed cleanup retains the exact snapshot/partial or journal for **Retry owned
cleanup**. Main-window Exit waits rather than discarding a support owner: finish
or retry cleanup, then Exit again. No background tray process is launched.

## Preview first, destination separately

1. Select the shared local Doctor report or last fixture report. Select **Include
   journal records** only deliberately; otherwise the report-only preview uses
   the engine's validated empty selection and does not create/read a journal.
2. Enter an inclusive receipt-UTC range. Default is the last seven days ending
   at window opening; adjust Through for records collected afterwards. The
   range is bounded to 2,048 records / 2 MiB; overflow fails instead of truncating.
   A selected corrupt/inaccessible/active-other-writer journal is an error,
   never an empty-success fallback.
3. **Freeze exact preview** copies the engine's immutable five files. Review
   every content tab, name, uncompressed size, source and SHA-256, snapshot
   identifier/digest, frozen time, requested/actual range (in `manifest.json`),
   provenance and omissions. Files are `build.json`, `settings.json`,
   `doctor.json`, `events.json` and `manifest.json`. Build data uses the bounded
   fixed installed payload manifest when available, otherwise executing assembly
   metadata with unavailable fields, not guessed SDK or signature qualification.
4. Choose a separate absolute local `.zip` path with an existing parent.
   The picker suggests a fresh UUID filename. Its selection writes no archive.
   Network/reparse locations and existing outputs are rejected. Selecting a
   destination is not approval.
5. **Review and confirm local export** shows the exact snapshot/digest,
   all file sizes/hashes and normalized destination. Default is **No**. Selection,
   source or destination changes during confirmation reject that confirmation.
   Changing selection requires another Freeze; changing destination requires a
   new confirmation. Source mutation while a preview is open cannot change its
   frozen bytes, and export never silently rereads or regenerates them.

The engine consumes one exact-destination approval per attempt, creates its own
partial and performs a create-only atomic rename. Existing/racing targets and
unrelated files are preserved. A failed attempt does not claim a successful ZIP;
retry needs cleanup when indicated, another preview if cleared, and new explicit
confirmation. A cancellation arriving inside an already committed rename
cannot undo that export; the app retains the actual local receipt even after
the observing window closes. Closing preview clears UI content bindings and
releases engine arrays after cleanup. Managed immutable strings/OS copies and
user-created archives are not guaranteed securely erased.

**Exported locally to ... means local archive only, not sent or received by a
maintainer. No support contact/upload channel is configured.** No email address,
automatic issue, data-bearing network link, clipboard action, arbitrary shell
command or open-folder launcher is added. Do not post unreviewed bundles publicly.
Doctor `--help` uses the same authored defaults/bounds/help; normal JSON/exit
semantics are unchanged. **CLI export is not implemented.**

## Bounds, recovery and privacy limits

Engine defaults remain <=50 MiB owned storage, 32 segments, <=8 KiB per record,
seven-day retention, <=4 MiB frozen uncompressed snapshot, and bounded operation
deadlines. Retention removes whole segments on explicit Start/Append, not an
exact idle timer. Size/slot pressure can remove recent evidence; later records
in an old segment can disappear before seven days. Idle/offline data remains
until the next explicit maintenance action. Recovery may truncate a validated
unfinished active tail and reports the count; corrupt finalized evidence is
preserved with an error. Exports and abandoned partials are outside journal
retention; cleanup never sweeps an output directory.

Bundles omit raw settings, keys, credential references, model/voice IDs, device/
host/user identities and paths, environment, audio, conversations, screens and
memory. Accepted UUIDs receive fresh per-bundle correlated pseudonyms. Times,
counts, versions and correlations still can identify an incident. This is
allowlisting/pseudonymization, not universal anonymity or protection against a
hostile same-user process. No power-loss durability promise is made.

Controlled actual-engine/service/WPF tests use private directories and synthetic
file/native/HTTP boundaries, not personal profiles, microphones, outputs, accounts
or vaults. Native apphost smoke opens Troubleshooting passively with all effects
off. Physical audio, real API/account quality/cost, real vault roundtrip, clean-VM
install/upgrade/uninstall, signing, rights, novice trials and G2 remain **NOT RUN /
NOT PASSED**. Repository readers can also consult [delivery](DELIVERY.md) and
the [engine contract](../src/Martlet.Support/README.md); those additional
repository documents are not part of the installed help folder.
