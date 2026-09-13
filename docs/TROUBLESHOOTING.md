# Desktop troubleshooting and local support (V06b)

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
