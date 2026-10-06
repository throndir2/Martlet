# The desktop app: updates, notification area, exiting and appearance

How the Windows app itself behaves: app and host updates, closing to the
notification area, exiting, palettes and the Martlet bird icon. Moved here from
the README so the README can stay short.

## App updates

Updates come from Martlet's public GitHub Releases (**Settings › App updates**).
Checks are **ON by default**: Martlet checks at launch and then every 15 minutes
to 24 hours (default: every hour) while it runs. Turning off *Check GitHub for
new versions automatically* stops all automatic requests, and **Check for
updates now** makes an explicit request instead. When a check finds a new
version, Martlet asks once per version whether to update now.

Only normal (non-draft, non-prerelease) releases with the exact
`Martlet-<version>-win-x64.exe` asset are offered. **Update now** (or
**Install**) downloads it (at most 512 MiB) into `updates\` in the local data
directory, verifies the exact bytes against GitHub's SHA-256 asset digest,
closes Martlet, runs the installer with its progress window (`/SILENT`, no
optional prerequisite tasks) and starts Martlet again; the next launch reports
the result.

With *Download and install updates automatically* this happens by itself as
soon as the update is downloaded, whether or not you are at the PC, Martlet's
window is in front or the character is showing. It waits only for a reply or
something you are saying, a question waiting for your answer, or work that
exiting would cut short (a setup task, a host update, a command from another
computer, backup and restore, a troubleshooting report, a download), and Status
in **Settings › App updates** says which. Martlet restarts minimized (or in the
notification area when it was there) and shows the character and listens again
when they were on as it closed.

An automatic install, and one another of your computers asks for
(`martlet.update`, see [CLUSTER](CLUSTER.md#commands-between-your-computers)),
shows no installer window at all (`/VERYSILENT`): Martlet downloads, closes,
installs and restarts by itself. Every step still goes to the log: the helper's
steps (`updates\update.log`) and, after a failed install, the end of the
installer's own `updates\install.log` are copied into Martlet's log (the
**Diagnostics** page) when it starts again.

Choices live in `update-checks.txt` and `updates.json`, separately from profile
settings and configuration backup; a choice saved while checks were opt-in
resets to on, and unreadable preferences fall back to checks ON (installs and
host updates OFF) with a visible error.

Releases are normal GitHub releases. Code signing is not a requirement for this
personal project, so the installer is unsigned. The digest detects a damaged
download; it does not prove who published it. Do not run an internal build as
an update.

## Host updates

Paired **Martlet hosts** follow the desktop's version: the gateway reports its
release, the Devices map shows *Update available* for older hosts, and clicking
it (or **Update host**) rebuilds that host's gateway from this version in a
Martlet run window (`martlet-host update`; identity, pairings and roles stay).

*Keep my Martlet hosts on this PC's version* does the same in the background
every interval for hosts Martlet reaches over an SSH key or this PC's Docker
Desktop; a host that needs a password, sudo or an approval keeps the Update host
route. This PC's **own host service** follows its version whatever that setting
says: Martlet installs its own update and is back moments later (the update
helper starts the installer within about a second of Martlet's exit), then
updates its host service in the background while you use it, and checks again
every minute until it runs the same version.

The [Windows packaging guide](../packaging/windows/README.md) distinguishes local
internal builds from the manually dispatched release workflow, which has no
push, PR, tag or scheduled trigger.

## Closing to the notification area

Closing Martlet's window keeps it running in the notification area by the clock
(on by default), so the character, sync, updates and commands from your other
computers carry on. Click the icon to open Martlet; right-click it to talk to
Martlet (or show the talk window), pause or resume Martlet (a reply, listening
and vision stop until you resume), end the conversation, show or hide the
character, change *Keep running when closed* and *Start with Windows*, or **Exit
Martlet**, which closes it completely (as does Exit Martlet in Settings).

**Settings › Startup and closing** holds the same choices plus *Start in the
notification area* for a start at sign-in (the per-user Run entry `Martlet`, off
by default; Windows' own Startup apps switch is respected, and the uninstaller
removes the entry). It can also show the character and start listening (and
watching) as Martlet starts. Starting Martlet again while it runs shows the
running window instead of a second copy (per data folder). Choices live in
`background.json`.

## Exiting

If an exit would cut work short (an update download, an install in a run
window, backup and restore, a troubleshooting report, a download or a command
from another computer), Martlet lists what it is still doing and asks: *Exit
anyway* interrupts it, *Keep Martlet open* doesn't. While it closes, its window
says what it is finishing (the notification-area tooltip too); if that takes
more than a few seconds the window shows with **Exit now**, which says what
exiting without waiting interrupts and asks once more. A part that fails to
close is logged and skipped, so Martlet never stays stuck closing. An unattended
update's exit (automatic, or asked for by another computer) never asks or shows
the window: it starts only when nothing would be cut short (and otherwise waits
for it), and a slow close stays out of sight.

## Appearance

**Settings › Appearance › Your palette** switches between **Pink light** (blush,
cream and berry), **Rose dark** (deep plum and soft rose) and the **Character
light/dark** palettes, which take their colors from the character you show.
Rounded controls, matching form fields, confirmation prompts and the companion
home screen share the palette across every window. The transparent avatar
overlay keeps its canvas clear while its controls and speech bubbles follow the
current palette and Windows high contrast. Windows high contrast overrides the
decorative colors; Windows file and folder pickers keep their system
appearance.

Pink light is the first-launch default. Changing the palette saves only
`appearance.txt` in the selected data directory, separately from profile
settings, credentials and consent. It is not part of configuration
backup/restore. Launch reads this preference without creating files;
inaccessible or malformed preferences are reported on the home screen. An
unsavable choice still applies for the current session. Appearance changes
never start a conversation, network or audio action.

## The Martlet bird

The original **Martlet bird** icon matches the palettes: a cream bird with rosy
cheeks and a berry heart on a pink badge. It is embedded in the desktop
executable, window/taskbar icons and installer; the Start menu shortcut uses the
desktop executable's icon. `src\Martlet.Desktop\Assets\Martlet.svg` is the
editable vector source, alongside a transparent 256 px PNG and a multi-size ICO
(16, 20, 24, 32, 40, 48, 64, 128 and 256 px). Regenerate the PNG/ICO locally
after artwork changes with `.\scripts\Generate-AppIcon.ps1` on Windows with
PowerShell 7; no downloads or third-party image tooling are needed.

The same bird is the in-app **Martlet mascot**: the navigation rail logo, the
Home hero, the welcome tour, and the Talk header and reply avatar draw it as a
round badge from the `MascotImage` vector in
`src\Martlet.Desktop\Themes\Controls.xaml`. Copy bird artwork changes from the
SVG into that resource too.
