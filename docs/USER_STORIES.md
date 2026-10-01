# User stories and target flows

Martlet's flows grew one feature at a time, and it shows. To change one
thing, a user clicks a button that opens a page, then a button on that page
that opens a window, then a console that asks them to type `yes`. This
document is the UX specification for replacing that. It covers:

1. the problems in today's UI, measured;
2. the rules every flow must follow;
3. the target navigation (where everything lives);
4. reusable interaction patterns;
5. every user story, each with **all** of its entry points, the exact screens
   it passes through, click counts, edge cases and acceptance criteria;
6. a glossary of labels, what happens to each current window, and a build order;
7. the minimum a user has to provide, and every default, detection and
   automatic install that covers the rest (section 11).

[UI design](UI_DESIGN.md) describes what ships today. This document is the
target. Where the two differ, this one wins for new work.

---

## 1. Audit: what is wrong today

Measured from the current code (labels quoted exactly).

### 1.1 Too many ways to do one thing, and they don't agree

| Goal | Distinct paths today | Surfaces involved |
| --- | ---: | --- |
| Change how Martlet thinks | 8 | Home hero, Companion page, setup pages, legacy Setup window, Devices tiles, Devices node panel, Hosts window, Settings › Prerequisites |
| Change the voice (route or voice) | 10 | Setup *Its voice*, Devices tiles, Voice Library window, F5 voice dialog, legacy Setup window, Audio setup window |
| Change listening (route, mic, mode) | 11 | Setup *How it listens*, Devices tiles, Audio setup window, legacy Setup window, Conversation window (mode, Voice ID) |
| Change character or lip-sync | 13 | Home hero, Companion page, Character setup page, Avatar window, Hosts window, Devices |
| Add another computer | 8 | Devices, Settings › *Martlet hosts*, every job page, Character page, Avatar window, host-mode Home, advisor |

Worse, **the same words lead to different places**:

- Companion › **How it thinks** opens the legacy four-tab Setup window, while
  Home › **Choose how Martlet thinks** opens the new in-window Thinking page.
- The character is called **Show character**, **Character**, **Character /
  STOP avatar**, **Hide character / STOP avatar (Escape)**, **Choose and
  customize** and **Activate reviewed avatar** in different places.
- Voice is called **Its voice**, **Speaking**, **Voice** and **Speak the
  reply with the AI-generated voice**.
- Machines are called **computers**, **hosts**, **Martlet hosts**, **devices**
  and **nodes**.

### 1.2 Finishing one job means leaving its page

From the Voice page, choosing speakers opens the *Audio setup* window,
choosing a cloned voice opens the *F5 voice dialog*, and the voice list lives
in the *Voice Library* window. The Character page opens the *Avatar* window,
which opens the *Hosts* window, which opens a *run* window. Each hop is a
new window the user has to find, finish and close.

### 1.3 Consoles and typed commands

| Action | What the user sees today |
| --- | --- |
| Thinking › **Download model** | A visible `cmd.exe /k` window running `ollama pull` |
| Welcome tour / Settings › **Prerequisites (check / install)** | A visible PowerShell window |
| Hosts › **Install Docker Desktop** | Agreement dialog, then a visible console, then UAC, possibly a restart |
| Host-mode Home › **Open host status console** | A console |
| Hosts › *I'll type the commands on the host myself* | Open console, type `start`, run `pair --device-id …`, copy a `martlet-pair-v1.…` code back, type `stop` |

### 1.4 Agreements in the middle of using it

Before the **first spoken message** a user must, inside the conversation
window, tick three long checkboxes:

1. *I authorize my next Send / PTT action … including potential API charges…*
2. *For microphone input: I permit bounded LOCAL microphone capture…*
3. *For microphone input: I separately permit uploading this audio to the
   named STT destination…*

Typing needs one tick. Screen watching, memory, Voice ID, avatar inspection
and Audio2Face analysis each have their own runtime checkbox. Docker's
agreement and host role terms appear at install time, scattered across
windows. Speaking aloud is **off by default** and re-enabled per conversation.

### 1.5 Overlapping starting points

The welcome tour, *Get a recommendation* (setup advisor), the four setup
pages and the legacy Setup window all try to answer "how do I set this up?".
None of them finishes the job alone.

### 1.6 No network

Every desktop pairs with every host on its own. A second PC cannot "join";
it repeats setup, pairing and keys. The shared *cluster plan* syncs only
routing and is off by default (Devices › *Keep who does what in sync on all
my computers*).

---

## 2. Rules every flow follows

These are acceptance criteria for every screen.

| # | Rule | Test |
| --- | --- | --- |
| R1 | **One home per thing.** Every setting has exactly one canonical place. Shortcuts elsewhere jump to that place; they never open a different UI for the same setting. | For any setting, all entry points land on the same control. |
| R2 | **Finish where you start.** Everything needed to change a job's *Now* line happens on that job's page, inline. | No job page opens a window, dialog or console to complete its job. |
| R3 | **No consoles, no typed commands.** Installs, downloads, host setup, pairing and updates run in Martlet with progress, a one-line status and *Cancel*. Raw output is under *Details*. | No visible `cmd`/PowerShell window in any normal flow. Nobody types `yes`, `start`, `stop` or a pairing code. |
| R4 | **Agree up front.** Licences, EULAs and data destinations are agreed during setup, before anything installs. Runtime never asks. | Starting a conversation, showing the character or talking never shows an agreement. |
| R5 | **Recommended is preselected.** Every choice opens with the best option for this machine selected and labelled *Recommended*, with a one-line reason. | Accepting defaults needs only the primary button. |
| R6 | **Choosing does the whole outcome.** Picking an option installs, downloads, verifies and switches by itself (P2). Where an explicit button remains, it does the whole outcome and says what: *Set up Qwen 3 8B (5 GB)*. | No "now click X, then go back and click Y" instructions; no *Apply*, *Save* or *Activate* after a choice. |
| R13 | **Ask only for what Martlet can't know.** The user supplies choices with a real trade-off (already preselected), API keys, agreement, and what Windows itself demands (UAC, reboot). Everything else is defaulted, detected or installed (section 11). | Every input field is justified in section 11. |
| R7 | **Undo over confirm.** Reversible changes apply immediately with an *Undo* toast. Only destructive, irreversible actions get one default-No confirmation. | Switching model, voice, machine or character has no confirmation dialog. |
| R8 | **Status is always visible.** Each job shows a *Now* line with a status dot. Long tasks continue in the background and show on Home. | The user can leave a page during an install and find its progress on Home. |
| R9 | **Problems come with their fix.** A broken job says what it means for the user and offers one *Fix* button. | Every error state has a button. |
| R10 | **One word per concept.** Labels follow the glossary (section 8). | No synonyms for the same thing in the UI. |
| R11 | **Elevate once.** Admin rights (Docker, WSL, firewall) are batched into one explained UAC prompt per setup run. | Never more than one UAC prompt per run. |
| R12 | **Safety stays.** Keys stay in Windows Credential Manager. Every standing permission is visible while active and can be switched off in one click. | Each active permission shows an indicator that turns it off. |

---

## 3. Target navigation

### 3.1 Main window

```text
┌──────────┬──────────────────────────────────────────────────────────┐
│ Home     │  Status, Start talking, Now lines, problems, progress    │
│ Companion│  Tabs: Thinking · Voice · Listening · Character · Memory │
│ Devices  │  Network map, machines, who does what                    │
│ Settings │  General · Privacy · Network · Updates · Help            │
└──────────┴──────────────────────────────────────────────────────────┘
```

- **Home**: hero (*Start talking*, *Show character*), four **Now lines**
  (one per job, each a link to its tab), the *Fix* card when something is
  broken, and any running task's progress.
- **Companion**: the four job pages plus Memory, as tabs. Personality lives
  on the Character tab. This replaces the Companion card grid, the setup
  section pages, the legacy Setup window, Audio setup, Avatar, Companion
  (persona), Voice Library, Voice ID, F5 voice dialog and Memory windows.
- **Devices**: the map. Selecting a machine opens its panel (what it runs,
  what it can run, hardware, actions). *Add a computer* lives here.
- **Settings**:
  - *General*: appearance, start with Windows, talk hotkey, tray.
  - *Privacy*: standing permissions, agreed terms, data destinations.
  - *Network*: network name, this PC's role, join/leave, members.
  - *Updates*: automatic updates for this PC and the network.
  - *Help*: problem report, logs, backup/restore, offline demo, reset.

### 3.2 Conversation window

```text
┌───────────────────────────────────────────────────────────────────┐
│ Martlet   [Thinking: Qwen 3 8B ▾] [🔊] [🎤] [🖥] [🧠]  [☺] [■ Stop] │
├───────────────────────────────────────────────────────────────────┤
│  chat bubbles                                                      │
├───────────────────────────────────────────────────────────────────┤
│ [ Type a message…                         ] [🎤 Hold] [Send]       │
└───────────────────────────────────────────────────────────────────┘
```

- Header chips show what is active (voice out, microphone, screen, memory)
  and toggle it. The Thinking chip switches between ready models.
- ☺ shows or hides the character. ⚙ (overflow) deep-links to the matching
  Companion tab in the main window.

### 3.3 Tray icon (new)

Right-click: *Start talking*, *Show/Hide character*, *Mute microphone*,
*Open Martlet*, *Quit*. Closing the main window keeps Martlet in the tray
(Settings › General can change this).

### 3.4 Canonical homes and allowed shortcuts

| Setting | Canonical home | Shortcuts (all jump to the home) |
| --- | --- | --- |
| Where thinking runs, model, key | Companion › Thinking | Home Now line, Devices tile, Fix card, conversation Thinking chip ▾ › *Change…* |
| Where voice runs, voice, speakers | Companion › Voice | Home Now line, Devices tile, Fix card, conversation 🔊 › *Voice settings* |
| Where listening runs, mic, talk mode, Voice ID | Companion › Listening | Home Now line, Devices tile, Fix card, conversation 🎤 › *Listening settings* |
| Character, personality, lip-sync | Companion › Character | Home Now line, Devices tile, ☺ long-press/right-click, tray |
| Memory | Companion › Memory | conversation 🧠 › *Manage memory* |
| Permissions | Settings › Privacy | every header chip's menu |
| Machines and roles | Devices | Machine pickers on job pages (*Manage on Devices*) |
| Network membership | Settings › Network | Devices › *Add a computer* |

---

## 4. Interaction patterns

### P1. Now line

`Thinking · Qwen 3 8B on This PC · ● Working` — status dot (green working,
amber checking or installing, red broken, grey not set). Click opens the job
tab. On Home, a broken line shows its *Fix* button inline.

### P2. "Where it runs" chooser

Three cards in a row: **This PC**, **Another of my computers**, **A cloud
provider**. Cards that can't work show why and are disabled (*This PC: needs
a 6 GB NVIDIA GPU*). The recommended card has a *Recommended* badge.
Selecting a card **expands its options below in place** with the
recommended option already selected, and **choosing is committing**:

- **This PC**: setup of the recommended option starts immediately in the
  inline runner (P5). Picking another option in the list while it runs
  switches the target. If an agreement for it isn't on file, a one-row terms
  sheet (P6) appears first and ticking it starts setup.
- **Another of my computers**: the best *ready* computer is used immediately;
  if none is ready, the best capable computer starts setting it up.
- **A cloud provider**: the API key is the only input. Pasting a key tests it
  and, if it works, switches. If a key for that provider is already saved,
  selecting the card switches immediately.

Switching is **make-before-break**: the current choice keeps working until
the new one has passed its test, then *Now* changes and an undo toast
appears (P8). A failed setup leaves the old choice running. The previously
active choice keeps a *Previous* badge with *Switch back*.

### P3. Option list

A list of models or voices with: name, size, speed (*fast/OK/slow on this
PC*), quality hint, licence badge, and *Recommended* with a reason (*fits
your 12 GB GPU*). Installed items show *Ready*. Voices have *Preview ▶*.

### P4. Machine picker

Each network member with its status for this job: *Ready with Llama 3 70B*,
*Can install (5 GB, ~4 min)*, *Can't: 4 GB GPU*, *Offline*. Best one
preselected. Footer: *Add a computer* and *Manage on Devices*.

### P5. Inline task runner

Replaces every console and run window. A card with a step checklist
(*Install Ollama ✓ · Download Qwen 3 8B 43% · Test*), a progress bar, time
left, *Cancel*, and *Details* (live output). It keeps running if the user
leaves; Home shows a compact copy. On failure: the reason in plain words,
*Try again*, and the next best alternative (*Use OpenRouter instead*).

### P6. Terms sheet

A list of exactly what will be installed or sent somewhere, built from the
plan. Each row: name, what it is, *Read terms ↗*, and for cloud rows *what
is sent* and *may cost money*. One checkbox at the bottom: *I agree to these
terms*, and the primary button. Agreements are stored with version. Later,
a new item adds a **single-row** terms sheet inline above the job page's
primary button (still setup, not runtime).

### P7. Fix card

`Martlet can't hear you — the microphone was unplugged. You can still
type.` Buttons: the single best fix (*Use Headset Mic*) and *More options*
(opens the job tab).

### P8. Undo toast

`Thinking switched to GPT-5 mini (OpenRouter). Undo` — 8 seconds. Undo
restores the previous route without re-entering keys.

### P9. Permission chip

Header chip in the conversation window and tray tooltip. Lit when the
permission is on *and* in use. Click toggles; menu has *Settings*.

---

## 5. Personas and machines

| Persona | Setup | Cares about |
| --- | --- | --- |
| **Sam**, casual | One laptop, no GPU | Talking in minutes, no jargon, low cost |
| **Alex**, gamer | Desktop with RTX 4070 plus an old PC with a 3090 | Speed, keeping games smooth, privacy |
| **Jordan**, tinkerer | Desktop, a headless Ubuntu GPU box, cloud keys | Control, choosing exact models |
| **Riley**, returning | Already set up Martlet months ago | Changing one thing quickly without relearning |

Machine roles: a machine can **talk** (has the companion UI and the user sits
at it), **help** (runs jobs for the network), or both.

---

## 6. User stories

Each story lists: **Entry points** (every way in), **Flow** (screens and
clicks), **Clicks** (from the stated start; typing not counted), **Edge
cases**, **Done when** (acceptance criteria) and **Today** (what it replaces).

### A. First run

#### A1. Quick start on a first PC

*As Sam, on my first PC, I want Martlet to set itself up with good defaults
so I can talk to it in minutes.*

- **Entry points**: first launch (no saved role); Settings › Help › *Reset
  Martlet on this PC*.
- **Flow**:
  1. **Welcome**: "Hi, I'm Martlet." Two big cards: **Set up Martlet on this
     PC** (selected) and **Join my Martlet network**. Link: *Just try the
     offline demo*.
  2. **Your plan** (one screen): Martlet reads hardware locally and shows
     four rows, each with the recommended choice and a reason:
     - Thinking — *This PC · Qwen 3 8B · fits your 12 GB GPU* (or *Cloud ·
       OpenRouter · your PC can't run a useful model*).
     - Voice — *This PC · Windows voice* / *This PC · F5* / *Cloud*.
     - Listening — *This PC · Whisper small* / *Windows speech* / *Cloud*.
     - Character — *Hiyori · lip-sync by voice loudness*.
     Each row has *Change* (expands P2 in place). Total download size and
     time shown at the bottom. Primary: **Continue**.
  3. **Terms** (P6) for exactly that plan; cloud rows ask for their API key
     here (with *Where do I get one? ↗*). Checkbox, primary: **Agree and set
     up**.
  4. **What Martlet may do** (standing permissions, see G3), defaults shown,
     primary: **Start setup**.
  5. **Setting up** (P5): one checklist for everything, one UAC prompt if
     needed. The user may click **Explore while I wait**; progress moves to
     Home.
  6. **Ready**: "Ready when you are." **Start talking**. The character
     appears if chosen. A network containing this PC is created silently.
- **Clicks**: 4 (Set up › Continue › Agree and set up › Start setup), plus
  typing an API key only for cloud rows.
- **Edge cases**:
  - Reboot required (WSL/Docker): the checklist says so before starting;
    after reboot Martlet reopens on *Setting up* and continues.
  - No internet: local-only items that are already present proceed; others
    show *Waiting for internet* and continue when online.
  - Download fails: P5 failure state with *Try again* and the next-best
    alternative.
  - Weak hardware: the plan recommends cloud for Thinking and Windows
    speech for Voice/Listening, with the reason.
  - User closes the app mid-setup: setup resumes on next launch.
- **Done when**: a new PC reaches a working first reply with ≤4 clicks
  using defaults; no console, no runtime agreement, ≤1 UAC prompt.
- **Today**: welcome tour → role → prerequisites (PowerShell console) →
  *How would you like to start?* → advisor or Setup → per-job pages →
  console model download → back to page → *Use Ollama on this PC*.

#### A2. Custom first setup

*As Jordan, I want to pick each job myself during first setup.*

- **Entry points**: A1 step 2, *Change* on any row.
- **Flow**: *Change* expands that row into the P2 chooser in place (same
  component as the job tabs). Picking another option updates the plan's
  size/time totals and the terms list.
- **Clicks**: +2 per changed row (card, option).
- **Done when**: the plan screen and the job tabs use the identical chooser.

#### A3. Try before setting up

*As Sam, I want to see what Martlet is like before installing anything.*

- **Entry points**: Welcome › *Just try the offline demo*; Settings › Help ›
  *Offline demo*.
- **Flow**: opens the conversation window in demo mode with a banner *Demo:
  scripted replies. Set up Martlet* → returns to A1 step 2.
- **Clicks**: 1.
- **Today**: Settings › *Try fixture (audio OFF)*, *Completed fixture +
  confirm 200 ms tone*.

#### A4. Skip setup

- **Entry points**: Welcome › *Not now* (small link).
- **Flow**: Home shows the hero "Let's set up your companion" with the plan
  button. Nothing else nags.

### B. Joining a network

#### B1. Set up a new PC by joining my network

*As Alex, adding my second PC, I want it to take my network's setup so I
don't redo everything, and tell the network what this PC can do.*

- **Entry points**: Welcome › **Join my Martlet network**; Settings ›
  Network › **Join a network** (B3); Devices › *Join a network* (shown only
  when standalone).
- **Flow**:
  1. **Find your network**: Martlet lists networks it finds on the local
     network (*Alex's Martlet · 2 computers · DESKTOP-01*). Below: *Not
     listed? Enter a join code*.
  2. Picking a network shows "Waiting for approval on DESKTOP-01…" with the
     6-digit check number (e.g. **482 913**).
  3. On every member that is open, a notification appears (B2). Once
     approved, this PC receives the network's plan and trust.
  4. **What's this PC for?** Two toggles, both preselected by hardware:
     **Talk to Martlet here** and **Help the network** (*Your RTX 3090 can
     run Thinking and Voice*).
  5. If *Help* is on: **What it can take** shows each job this PC can run,
     with the recommended model and whether that would be faster than the
     current machine (*Thinking would be ~2× faster here*). Toggles per
     job, defaulting to on where faster. Terms (P6) for anything to install.
  6. If *Talk* is on: **Bring your cloud keys?** (on by default). Keys are
     sent encrypted machine-to-machine and stored in Credential Manager.
  7. **Setting up** (P5), then **Ready**. Every member's Devices map shows
     the new PC.
- **Clicks**: 4 with defaults (Join › network › Continue › Agree and set
  up), plus 1 approval on the other PC.
- **Edge cases**:
  - No member is open to approve: "Open Martlet on any of your computers to
    approve." Join code path works with a member open later (code valid 10
    minutes).
  - Not on the same LAN (VPN, other subnet): join code path.
  - Network found but versions differ: offer *Update this PC first* inline.
  - Same machine name already in the network: B5 (replace).
- **Done when**: a second PC becomes a working member with the network's
  configuration and its abilities advertised, without copying codes,
  consoles or per-host pairing.
- **Today**: not possible. Each desktop pairs each host separately via the
  Hosts wizard; cloud keys are re-entered; sync is a separate opt-in.

#### B2. Approve a joining machine

*As a member, I want to let a new machine in safely with one click.*

- **Entry points**: notification in the main window and tray; Devices shows a
  pending ghost node with the same actions.
- **Flow**: "*GAMING-PC wants to join. RTX 3090 (24 GB), 64 GB RAM. Check
  number 482 913.*" Buttons **Allow** and **Deny**.
- **Clicks**: 1.
- **Edge cases**: request expires after 10 minutes; a denied machine can ask
  again; three denials in a row block it for an hour.
- **Done when**: approval is one click and shows the check number that
  matches the joining screen.

#### B3. Join a network after setting up alone

*As Riley, with a PC already set up on its own, I want to connect it to my
network and keep what it already does.*

- **Entry points**: Settings › Network › **Join a network**; Devices
  (standalone) › *Join a network*.
- **Flow**: B1 steps 1–3, then **Combine setups**: *Keep this PC's setup and
  share what it can do* (default) or *Use the network's setup on this PC*.
  Then B1 steps 4–7 (only items not already installed).
- **Clicks**: 4.
- **Done when**: existing local engines and models are announced to the
  network immediately and appear in other members' machine pickers.

#### B4. Machine abilities stay current

*As the network, I want to know what each machine can run, so pickers only
offer machines that work.*

- **Entry points**: automatic. Manual: Devices › machine › *Refresh*.
- **Behaviour**: a machine sends its profile (CPU, RAM, GPU/VRAM, OS, free
  disk, installed engines and models, jobs it is willing to take) on join,
  on start, every few minutes while online, and right after installing or
  removing anything.
- **Done when**: installing a model on one PC makes it show *Ready* in every
  member's picker within a minute.
- **Today**: hosts advertise routes; hardware only for hosts; desktops don't
  advertise; no picker uses it except failover.

#### B5. Replace or reinstall a machine

- **Entry points**: B1 when a member with the same name exists.
- **Flow**: "*DESKTOP-01 is already in this network (offline 3 days).
  Replace it?*" **Replace** (default) restores its role and jobs, and
  reinstalls its engines and models; *Add as a new computer* keeps both.
- **Clicks**: +1.

#### B6. Add a Linux or headless machine

*As Jordan, I want to add a headless Ubuntu GPU box without a console.*

- **Entry points**: Devices › **Add a computer**; machine pickers › *Add a
  computer*.
- **Flow**:
  1. **Add a computer**: three cards: **A Windows PC** (shows: "Install
     Martlet on it and choose *Join my Martlet network*", with a *Copy
     download link* button), **A Linux machine I can sign in to (SSH)**,
     **Something else** (a command to paste).
  2. SSH: fields *Address* and *User*; **Add it**.
  3. Sign-in, host-key trust and sudo prompts appear as Martlet dialogs
     (password once; Martlet installs its own key).
  4. P5 runner: check machine › install Docker or native host › join
     network › read abilities.
  5. Lands on the machine's Devices panel with *What it can take* (as B1
     step 5).
- **Clicks**: 2 + sign-in.
- **Edge cases**: unsupported architecture (ARM) shows the reason before
  installing; no sudo shows what the owner must run.
- **Done when**: no console on either machine; the pairing code is never
  shown.
- **Today**: Hosts wizard 4 steps (Where it runs › Install › Pair › Roles),
  plus PrepareHostWindow for Ubuntu prerequisites with a separate checklist
  and *Run selected*.

#### B7. Remove a machine or leave the network

- **Entry points**: Devices › machine › **Remove from network**; on the
  machine itself: Settings › Network › **Leave network**.
- **Flow**: one default-No confirmation listing what moves (*Thinking will
  move to DESKTOP-01*). Trust is revoked on every member.
- **Clicks**: 2.
- **Edge cases**: removing the only machine that can run a job moves the job
  to its fallback (cloud if configured, else *not set*) and Home says so.

### C. Thinking

#### C1. Change how Martlet thinks (overview of every way)

*As any user, I want to change which model Martlet thinks with and where it
runs.*

All entry points land on **Companion › Thinking** (R1):

| Entry point | Clicks to Thinking tab |
| --- | ---: |
| Home › Thinking Now line | 1 |
| Companion › Thinking tab | 2 |
| Home › Fix card › *More options* (when broken) | 1 |
| Devices › *Who does what* › Thinking tile | 2 |
| Devices › machine panel › Thinking › *Change* | 3 |
| Conversation › Thinking chip ▾ › *Change…* | 2 |

Quick switch without leaving the conversation: Thinking chip ▾ lists every
**ready** model on the network and cloud routes with saved keys (C5).

The Thinking tab:

```text
Now: Qwen 3 8B on This PC · ● Working

Where it runs
[ This PC  Current ]  [ Another of my computers ]  [ A cloud provider ]

(expanded options for the selected card)

Advanced ▸  temperature, context length, custom endpoint, stored keys
```

The sub-stories C2–C4 cover each card.

- **Today**: 8 paths, two of which (*How it thinks*, *Advanced setup*) open
  the legacy Setup window with tabs Choice › Jobs › Credentials › Review.

#### C2. Think on this PC

*As Alex, I want Martlet to think on my PC with the best model it can run.*

- **Start**: Thinking tab.
- **Flow**:
  1. Click **This PC**. Expands: an option list (P3) of models that fit this
     PC, sorted by recommendation: *Qwen 3 8B · 5 GB · fast · Recommended:
     fits your 12 GB GPU*, *Llama 3.1 8B · 5 GB · fast*, *Qwen 3 14B · 9 GB ·
     OK, uses most of your GPU*, … Models that don't fit are collapsed under
     *Won't run well on this PC (3)*.
  2. Setup of the recommended model **starts immediately** (P2) in the P5
     runner on the page: *Install Ollama › Download › Load › Test*. If
     Ollama's terms aren't on file yet, a one-row terms sheet appears first
     and ticking it starts setup. Picking another model in the list while it
     runs switches the target.
  3. The previous choice keeps working until the test passes; then *Now*
     updates and an undo toast appears.
- **Clicks**: 1 (This PC). If an agreement is needed: 2.
- **Edge cases**: low disk space shows before starting with *Free up space*
  (C7); Ollama already installed by the user is detected and reused; the
  download continues if the user leaves the page.
- **Done when**: no console; the user never sees *Install Ollama*, *Download
  model*, *Check Ollama*, *Use Ollama on this PC* as separate buttons.
- **Today**: four separate buttons, a console window, then returning to the
  page to click *Use Ollama on this PC*.

#### C3. Think on another of my computers

*As Alex, I want Martlet on my laptop to think using my gaming PC.*

- **Start**: Thinking tab.
- **Flow**:
  1. Click **Another of my computers**. Expands: machine picker (P4). The
     best *ready* computer is used immediately (*GAMING-PC · Ready with Qwen
     3 32B*); undo toast.
  2. Under the selected machine, its model list (P3) with what it already
     has marked *Ready* and what it could install. Picking one that isn't
     installed starts setting it up there (P5) while the old choice keeps
     working.
- **Clicks**: 1 when a computer is ready; 2 to pick another computer or
  model.
- **Edge cases**: machine offline → shown *Offline · last seen 2 h ago* and
  not selectable, with *Wake it* if Wake-on-LAN is known; no other machines
  → the card shows *Add a computer* inline.
- **Done when**: the picker lists only network members and their real
  abilities; installing on another machine never opens the Hosts window.

#### C4. Think with a cloud provider

*As Sam, I want to use a cloud model because my laptop has no GPU.*

- **Start**: Thinking tab.
- **Flow**:
  1. Click **A cloud provider**. Expands: provider list (OpenRouter
     *Recommended: many models, one key*, OpenAI, NVIDIA Build, *Custom
     (OpenAI-compatible)*). Each shows *what is sent* and *pricing ↗*.
  2. Model list (P3) for that provider, recommended preselected.
  3. **API key** field: the only input. Shows *Saved ✓* if a key exists
     (with *Replace*), else empty with *Get a key ↗*. A key saved for the
     same provider by another job (or brought from the network) is reused.
  4. One-row terms sheet for the provider if not agreed.
  5. Pasting the key saves it, sends one tiny test request and switches
     *Now* (no *Save* button). With a key already saved, choosing the
     provider switches immediately.
- **Clicks**: 2 (card, agree) + paste key; 1 if the key and agreement are
  already on file. Changing just the model on the current provider: 1.
- **Edge cases**: invalid key → inline under the field *OpenRouter rejected
  this key* with *Get a key ↗*; no credit → *Your OpenRouter account has no
  credit*; custom endpoint shows URL and model ID fields.
- **Done when**: no separate Credentials step; the key is entered next to
  the provider that uses it.
- **Today**: cloud card *Save*, or legacy Setup › Jobs › Apply › Credentials
  › Store/replace key › Save and exit setup.

#### C5. Quick-switch model during a conversation

*As Jordan, I want to try another model mid-conversation without leaving
it.*

- **Entry points**: conversation › Thinking chip ▾.
- **Flow**: menu lists ready choices (*Qwen 3 8B · This PC ✓*, *Qwen 3 32B ·
  GAMING-PC*, *GPT-5 mini · OpenRouter*), then *Change…*. Picking one
  switches for the next message; undo toast.
- **Clicks**: 2.
- **Done when**: only ready choices are listed; nothing needs setup here.

#### C6. Go back to what worked

- **Entry points**: undo toast; Thinking tab › *Current*/*Previous* badges.
- **Flow**: the previous choice keeps a *Previous* badge and a **Switch
  back** button.
- **Clicks**: 1.

#### C7. Free up space

- **Entry points**: Thinking tab › *Advanced* › *Downloaded models*; Devices
  › machine › *Models*; low-disk warning in C2.
- **Flow**: list with size and *last used*; *Remove* per model (default-No
  confirm, since re-downloading is slow).
- **Clicks**: 2.

### D. Voice

#### D1. Change Martlet's voice (overview of every way)

All entry points land on **Companion › Voice**:

| Entry point | Clicks to Voice tab |
| --- | ---: |
| Home › Voice Now line | 1 |
| Companion › Voice tab | 2 |
| Home › Fix card › *More options* | 1 |
| Devices › *Who does what* › Voice tile | 2 |
| Conversation › 🔊 chip ▾ › *Voice settings* | 2 |

The Voice tab, top to bottom:

```text
Now: "Aria" (F5) on GAMING-PC · ● Working          [■ Speak replies: On]

Voice      ( Aria ▶ ) ( My voice ▶ ) ( Windows: Zira ▶ ) ( + Make a new voice )
Where it runs   [ This PC ] [ Another of my computers  Current ] [ A cloud provider ]
Speakers   [ Headphones (Realtek) ▾ ]  [ Play test sound ]
Advanced ▸  speed, volume, voice library files
```

Voice comes first because users think "I want a different voice" before
"I want a different engine". Picking a voice that needs a different engine
updates *Where it runs* automatically (and offers to install it).

- **Today**: 10 paths; voice list in the Voice Library window, cloning in
  the F5 dialog, speakers in the Audio setup window, and speaking aloud is a
  per-conversation checkbox, off by default.

#### D2. Pick a different voice

- **Start**: Voice tab.
- **Flow**: click a voice's ▶ to preview a sample sentence; click the voice
  to select. It applies immediately with an undo toast.
- **Clicks**: 1 (2 with preview).
- **Edge cases**: a voice whose engine isn't installed shows *Needs F5 ·
  Set up*; selecting it opens D4's flow inline.

#### D3. Make a new voice from a recording

*As Alex, I want Martlet to speak in a voice I recorded.*

- **Start**: Voice tab › **+ Make a new voice**.
- **Flow** (expands inline):
  1. **Record** (reads a sentence on screen aloud, 10 s, level meter) or
     **Choose a file**.
  2. Transcript is filled in automatically by Listening and is editable.
  3. Name field (prefilled *My voice*).
  4. *Whose voice is this?* **Mine** / **Someone who gave me permission**.
     This is a statement about this recording, not a licence, so it lives
     here (R4 covers licences and data destinations).
  5. **Create voice**. It joins the list and is selected; preview plays.
- **Clicks**: 4 (Make, Record, Mine, Create).
- **Edge cases**: no voice-cloning engine anywhere → the button shows
  *Needs F5 on a computer with a 6 GB NVIDIA GPU* and *Set it up*.
- **Today**: Voice Library window (purpose, rights basis, engine, storage
  confirmation, Import), then separately the F5 dialog (file, name, exact
  transcript typed by hand, rights checkbox, Use this voice).

#### D4. Change where the voice runs

- **Start**: Voice tab › *Where it runs*.
- **Flow**: same P2 pattern as C2–C4 (This PC: Windows voice or F5; another
  computer: F5 host; cloud: OpenAI TTS etc.). The voice list filters to
  voices that engine can speak.
- **Clicks**: 2.

#### D5. Change speakers

- **Entry points**: Voice tab › Speakers; Fix card when the device vanishes.
- **Flow**: pick from the drop-down (applies immediately); **Play test
  sound**.
- **Clicks**: 2.
- **Today**: Microphone and speakers window: optional *Find devices*, pick
  (saves at once), *Play test sound* (confirm the beep), *Yes, I heard it*.

#### D6. Turn speaking on or off

- **Entry points**: Voice tab › *Speak replies* toggle; conversation 🔊
  chip; tray.
- **Behaviour**: a standing setting, on after setup when a voice is
  configured.
- **Clicks**: 1.
- **Today**: *Speak the reply with the AI-generated voice (OFF by default)*,
  per conversation.

### E. Listening

#### E1. Change how Martlet listens (overview of every way)

All entry points land on **Companion › Listening**:

| Entry point | Clicks to Listening tab |
| --- | ---: |
| Home › Listening Now line | 1 |
| Companion › Listening tab | 2 |
| Home › Fix card › *More options* | 1 |
| Devices › *Who does what* › Listening tile | 2 |
| Conversation › 🎤 chip ▾ › *Listening settings* | 2 |

The Listening tab:

```text
Now: Whisper small on This PC · ● Working

Microphone   [ Headset Mic ▾ ]  ▮▮▮▮▯▯  [ Say something to test ]  → "testing one two"
How I talk   (● Hold a key: Right Ctrl  [Change] )  ( ○ Hands-free  sensitivity ━━●━ )
Where it runs   [ This PC  Current ] [ Another of my computers ] [ A cloud provider ]
Only respond to my voice   [ Off ]  [ Teach it my voice ]
```

Microphone is first because it is the most common thing to change.

- **Today**: 11 paths; microphone in Audio setup, talk mode and Voice ID in
  the conversation window, route in the setup page or legacy Setup.

#### E2. Change microphone

- **Flow**: pick from the drop-down (applies immediately); the level meter
  moves live; **Say something to test** shows the transcript.
- **Clicks**: 2.
- **Edge cases**: Windows has blocked microphone access → Fix card with
  **Open Windows privacy settings** and an explanation.

#### E3. Choose push-to-talk or hands-free

- **Entry points**: Listening tab › *How I talk*; conversation 🎤 chip ▾.
- **Flow**: radio choice; *Change* on the hotkey records the next key
  pressed. The hotkey works globally (while gaming).
- **Clicks**: 1.

#### E4. Only respond to my voice

- **Start**: Listening tab › **Teach it my voice**.
- **Flow** (inline): read three short phrases shown on screen, each with a
  **Record** button and a tick when done; then the toggle turns on. *Test*
  and a sensitivity slider appear.
- **Clicks**: 4.
- **Today**: Voice ID window from the conversation, with its own consent
  checkbox.

#### E5. Change where listening runs

- Same P2 pattern as Thinking (This PC: Whisper or Windows speech; another
  computer; cloud).
- **Clicks**: 2.

#### E6. Mute the microphone right now

- **Entry points**: conversation 🎤 chip; tray › *Mute microphone*; global
  mute hotkey.
- **Clicks**: 1.

### F. Character

#### F1. Change the character (overview of every way)

All entry points land on **Companion › Character**:

| Entry point | Clicks |
| --- | ---: |
| Home › Character Now line | 1 |
| Companion › Character tab | 2 |
| Devices › *Who does what* › Lip-sync tile | 2 |
| Right-click the character on the desktop › *Character settings* | 2 |
| Conversation ☺ right-click › *Character settings* | 2 |

The Character tab:

```text
Now: Hiyori · showing · lip-sync on GAMING-PC (Audio2Face)   [ Hide character ]

Character   [Hiyori (built-in)] [My VRM]  [ + Import ]
Personality  Name [Martlet]  About  [ … ]   Style: helpful ━━●━ playful ━●━━ …   [Persona ▾]
Lip-sync     [ This PC ] [ Another of my computers  Current ] [ Voice loudness ]
On screen    Subtitles [On]   [ Reset position ]  [ Reset size ]
Advanced ▸   mapping, renderer, model files
```

- **Today**: 13 paths; Avatar window (with *Save choices*, *Show character*,
  *Activate reviewed avatar*, inspection and analysis checkboxes), Companion
  window for personality, Hosts window for lip-sync.

#### F2. Show or hide the character

- **Entry points**: Home hero; Character tab; conversation ☺; tray;
  right-click the character › *Hide*.
- **Labels**: always *Show character* / *Hide character*.
- **Clicks**: 1.

#### F3. Pick a different character

- **Flow**: click a thumbnail; the character swaps on the desktop
  immediately; undo toast.
- **Clicks**: 1.

#### F4. Import my own character

- **Flow**: **+ Import** › file picker › Martlet validates (inline result:
  *Works*, *Works without some expressions*, *Can't use: missing …*) ›
  **Use it**.
- **Clicks**: 3.
- **Edge cases**: partial support is explained in plain words with *Use it
  anyway*; no "accept reduced fidelity" checkbox.

#### F5. Move, resize, reset

- **Flow**: drag the character; scroll over it to resize; right-click ›
  *Reset position* / *Reset size*.
- **Clicks**: 2 for reset.

#### F6. Edit personality

- **Flow**: edit fields inline on the Character tab; changes save
  automatically (with undo). *Persona ▾* switches or adds personas;
  *Import/Export* under its menu.
- **Clicks**: 0 to edit, 2 to switch persona.
- **Today**: Companion window with *Apply edits to draft* and *Save companion
  settings* as separate steps.

#### F7. Change who does lip-sync

- **Flow**: P2 with **Voice loudness** as the third card instead of cloud.
  Choosing a machine without Audio2Face offers **Set up Audio2Face on
  GAMING-PC** inline (P5). The mouth follows voice loudness until ready.
- **Clicks**: 2.

### G. Agreements and permissions

#### G1. Agree to everything once, at setup

*As a user, I want to agree to terms during setup, not while I'm talking.*

- **Where**: A1 step 3, B1 step 5, and single-row sheets on job tabs when
  a later choice adds something new (P6).
- **Covers**: Docker Desktop, WSL, Ollama, each model's licence, F5,
  Whisper, Audio2Face (NVIDIA), Live2D, Windows components, and every cloud
  provider (what is sent, may cost money).
- **Done when**: the conversation window, the character and the desktop
  never show a licence, EULA, data-destination or cost agreement.

#### G2. Review what I agreed to

- **Entry points**: Settings › Privacy › *Agreements*.
- **Flow**: list with date, version, *Read ↗* and *Withdraw* (withdrawing
  stops using that component and shows which job is affected).

#### G3. Decide what Martlet may do

*As a user, I want to decide once what Martlet may do, and see when it does
it.*

- **Where**: setup (A1 step 4), and Settings › Privacy.
- **Switches**, with defaults:

  | Permission | Default | Indicator |
  | --- | --- | --- |
  | Use the microphone when I talk | On | 🎤 lit while capturing |
  | Send my voice to the cloud to transcribe it | On only when Listening is cloud | ☁ on the 🎤 chip |
  | Send messages to cloud models (may cost money) | On only when Thinking is cloud | ☁ on the Thinking chip |
  | Speak replies aloud | On when a voice is set | 🔊 |
  | Remember things between chats | Off | 🧠 lit when used |
  | Look at my screen or camera | Off | 🖥 lit while capturing |

- **Behaviour**: active permissions show as chips (P9); one click turns one
  off. Locking Windows, *Stop* or closing still stops all capture
  immediately.
- **Decision**: this replaces the per-action checkboxes in the conversation
  window. Consent becomes standing, visible and revocable instead of
  re-asked every message.
- **Today**: 3 ticks before the first spoken message, 1 before the first
  typed message, plus separate ticks for memory, screen, Voice ID, avatar
  inspection and analysis.

#### G4. Stop everything now

- **Entry points**: conversation **Stop** (Esc); tray › *Stop everything*;
  global hotkey.
- **Behaviour**: stops speech, listening, screen watching and the current
  reply. Doesn't change settings.
- **Clicks**: 1.

### H. Talking

#### H1. First typed message

- **Flow**: Home › **Start talking** › type › Enter.
- **Clicks**: 1 (+ Enter).
- **Today**: 1 click + 1 tick + *Send (one paid action)*.

#### H2. First spoken message

- **Flow**: Home › **Start talking** › hold the talk key or 🎤 › speak ›
  release.
- **Clicks**: 1 + hold.
- **Today**: 1 click + 3 ticks + hold, and *Finish and send* as a second
  send button.

#### H3. Talk while doing something else (gaming)

- **Flow**: hold the global talk key anywhere; the character (if shown)
  reacts; the reply is spoken. The conversation window does not need focus.
- **Clicks**: 0.

#### H4. Hands-free

- **Entry points**: conversation 🎤 chip ▾ › *Hands-free*; Listening tab.
- **Clicks**: 2.

#### H5. Interrupt

- **Flow**: start talking while Martlet speaks (hands-free) or press the talk
  key; speech stops and Martlet listens. Esc stops without talking.

#### H6. Let Martlet comment on my screen or camera

- **Entry points**: conversation 🖥 chip; Settings › Privacy.
- **Flow**: click 🖥 › menu: *My active window* (default), *My whole
  screen*, *A camera…*, and *How chatty: Quiet / Normal / Chatty*. If the
  permission is off, picking one turns it on (it's the user's explicit
  action) and the chip lights.
- **Clicks**: 2.
- **Today**: a long consent checkbox plus *Start watching* in the
  conversation's settings column.

#### H7. Memory

- **Entry points**: conversation 🧠 chip; Companion › Memory.
- **Flow**: when memory is on, Martlet uses relevant facts automatically and
  shows *Used 2 memories* under the reply (click to see which, with
  *Forget*). Say or type "remember that …" to add. Companion › Memory lists
  facts with search, edit and delete inline; *Export* is one button with a
  save dialog.
- **Clicks**: 1 to turn on; 2 to forget a fact.
- **Today**: Memory window (storage choice, enable checkbox, *Save memory
  configuration*, fact editor, export preview, export checkbox), plus a
  per-message memory checkbox in the conversation.

### I. Devices and the network

#### I1. See what each machine is doing

- **Entry points**: Devices; Home › Now lines (machine names link to their
  node).
- **Flow**: the map shows each machine with role chips and status. Select a
  machine: *Doing now*, *Can do* (from B4), *Hardware*, *Actions*.

#### I2. Move a job to another machine

- **Entry points**: Devices › *Who does what* tile › machine; Devices ›
  machine › *Can do* › **Move Thinking here**; job tabs › *Another of my
  computers*.
- **Flow**: picking a machine that is ready switches immediately with undo;
  if not ready, the P5 runner installs first.
- **Clicks**: 2.
- **Done when**: all three entry points use the same machine picker.

#### I3. Survive a machine going offline

- **Behaviour**: inside a network, sync is always on (no toggle) and
  failover is on by default for jobs another machine can run. Home shows
  "*Thinking moved from GAMING-PC to DESKTOP-01 (GAMING-PC stopped
  answering).* **Move back**". Without an alternative: Fix card.
- **Today**: sync and failover are separate opt-ins on Devices.

#### I4. Change what this PC is for

- **Entry points**: Settings › Network › *This PC*; Devices › This PC ›
  *Role*.
- **Flow**: toggles **Talk to Martlet here** and **Help the network**.
  Turning on *Help* shows *What it can take* (B1 step 5).
- **Clicks**: 1–3.
- **Today**: *Use as my companion PC* / *Use as a Martlet host* switch the
  whole Home into a different dashboard.

#### I5. Manage what a machine runs

- **Entry points**: Devices › machine › *Can do* / *Doing now*.
- **Flow**: each engine has **Set up** / **Remove** and its models; same P5
  runner. Power actions (*Wake*, *Restart*, *Shut down*) under *More*.
- **Today**: Hosts wizard *Roles* step, role install dialog, PrepareHost
  window, *Open host status console*.

### J. Keeping things working

#### J1. Something broke

- **Entry points**: Home Fix card; job tab *Now* line; tray balloon for
  failures while talking.
- **Flow**: the card names the impact in plain words and offers the single
  best fix (restart engine, re-download model, move to another machine,
  replace key, pick another microphone). *More options* opens the job tab.
- **Clicks**: 1.

#### J2. Update Martlet

- **Entry points**: automatic (default); Settings › Updates; Home banner
  *Update ready*.
- **Flow**: **Update** updates this PC, then every network member, with P5
  progress; machines that are off update when they next start.
- **Clicks**: 1.

#### J3. Report a problem

- **Entry points**: Settings › Help › **Save a problem report**; Fix card ›
  *Save a problem report*.
- **Flow**: shows what is included (summary list, *Show files*), then a save
  dialog. Recording, freezing and journal ranges move under *Advanced*.
- **Clicks**: 2.
- **Today**: Troubleshooting window: record › freeze exact preview ›
  choose destination › review and confirm local export.

#### J4. Back up and restore

- **Entry points**: Settings › Help.
- **Flow**: **Back up** › save dialog. **Restore** › file dialog › summary of
  what changes › one default-No confirmation.
- **Clicks**: 2 / 3.

#### J5. Start over

- **Entry points**: Settings › Help › **Reset Martlet on this PC**.
- **Flow**: default-No confirmation (choices: keep or remove downloaded
  models). Leaves the network and returns to Welcome.

---

## 7. Click budget summary

| Story | Today | Target |
| --- | --- | ---: |
| A1 First working reply on a new PC (defaults) | 10+ clicks, console, 3 consent ticks | 4 |
| B1 Second PC joins with the network's setup | not possible | 4 + 1 approval |
| C2 Think on this PC (not installed) | 4 buttons + console + return | 1 |
| C4 Switch cloud model | legacy Setup: 6+ | 1 |
| C5 Switch model mid-conversation | close conversation, go to Setup | 2 |
| D2 Pick another voice | Voice Library / F5 dialog | 1 |
| D3 Make a voice from a recording | 2 windows, ~10 fields | 4 |
| D5 Change speakers | Audio setup window, 5 steps | 2 |
| E2 Change microphone | Audio setup window, 4 steps | 2 |
| F3 Pick another character | Avatar window, choose, Save, Show | 1 |
| H1 First typed message | 1 + 1 tick + Send | 1 + Enter |
| H2 First spoken message | 1 + 3 ticks + hold | 1 + hold |
| J3 Save a problem report | 4 stages | 2 |

---

## 8. Glossary: one word per concept

| Concept | Say | Don't say |
| --- | --- | --- |
| The conversation model | **Thinking** | LLM, conversation model, *How it thinks* (as a link label) |
| Text-to-speech | **Voice** | Speaking, TTS, *Its voice*, AI-generated voice |
| Speech-to-text | **Listening** | STT, transcription, *How it listens* |
| Live2D/VRM figure | **Character** | avatar, renderer, model (for the character) |
| Mouth movement | **Lip-sync** | Audio2Face (except as the engine name), analysis |
| Any of the user's PCs | **computer** | host, node, device, gateway |
| All the user's computers together | **network** | cluster, plan |
| A computer that runs jobs for others | **helps the network** | host, Martlet host |
| Where a job runs | **Where it runs** | route, destination, placement |
| The current state of a job | **Now** | status, coverage |
| A cloud company | **cloud provider** | API, endpoint, destination |
| Showing the character | **Show character / Hide character** | Activate, STOP avatar, Inspect |
| Ending live activity | **Stop** | Mute / stop live work, Pause all live work, revoke |

Technical names (Ollama, Whisper, F5, Audio2Face, OpenRouter) appear as
option names and in *Details*, never as navigation labels.

---

## 9. What happens to each current surface

| Current | Fate |
| --- | --- |
| Welcome tour (4 cards) | Replaced by Welcome › Plan › Terms › Permissions › Setting up (A1/B1) |
| Setup advisor window | Removed; its logic becomes the *Recommended* preselection (R5) and the A1 plan |
| Legacy Setup window (Choice, Jobs, Credentials, Review) | Removed; keys are on job tabs, stored/detached keys under Thinking › *Advanced* |
| Setup section pages (Thinking, Its voice, How it listens, Character) | Become Companion tabs (done, with a Memory tab) |
| Companion card grid | Removed; Companion is the tab set (done) |
| Audio setup window | Split into Voice › Speakers and Listening › Microphone |
| Avatar window | Character tab; mapping/renderer under *Advanced* |
| Companion (persona) window | Character tab › Personality |
| Voice Library window | Voice tab voice list and *Make a new voice* |
| F5 voice dialog | Voice tab *Make a new voice* |
| Voice ID window | Listening › *Teach it my voice* |
| Memory window | Companion › Memory tab |
| Hosts window (4-step wizard) | Devices › *Add a computer* (B6) and machine panel (I5) |
| PrepareHost window | Folded into B6's runner |
| Host run window, consoles | Inline task runner (P5) |
| Host-mode Home dashboard | Removed; a helping PC's Home shows *This PC helps your network* with its jobs and status |
| Devices › *Keep who does what in sync* | Removed; always on inside a network |
| Troubleshooting window | Settings › Help › *Save a problem report* (+ *Advanced*) |
| Configuration recovery window | Settings › Help › *Back up* / *Restore* |
| Conversation consent checkboxes | Standing permissions (G3) and chips (P9) |
| Conversation *Finish and send* | Removed; releasing the talk key sends |
| Settings › *Prerequisites (check / install)* | Removed; prerequisites are part of each setup's runner |

---

## 10. Build order

Each slice ships on its own and is visible to users.

1. **Inline task runner (P5)** and removal of every visible console:
   Ollama download, prerequisites, Docker install, host setup, host status.
2. **Defaults and automatic setup (section 11)**: choosing commits (P2),
   *This PC* chains install › download › test › switch, Windows default audio
   devices, one key per provider reused across jobs, voice output on, and the
   Docker-free *This PC* engines (Windows speech, native whisper.cpp).
3. **Companion tabs own Now (R1, R2)**: fold Audio setup, Avatar,
   Companion, Voice Library, F5 dialog, Voice ID and Memory windows into the
   tabs; P2 chooser with expand-in-place and recommended preselection;
   retire the legacy Setup window and the advisor. Apply the glossary.
4. **Conversation window**: header chips, Thinking quick switch, single
   *Send*, global talk hotkey, tray icon.
5. **Standing permissions and terms up front (G1, G3)**: Plan › Terms ›
   Permissions in first run; remove runtime consent checkboxes.
6. **Network membership (B1–B7)**: network identity, LAN discovery and join
   code, approve-to-join with check number, network-wide trust replacing
   per-desktop host pairing, machine profiles, encrypted key transfer, sync
   and failover always on. Builds on the cluster plan in
   [CLUSTER.md](CLUSTER.md).
7. **Maintenance (J2–J5)**: network-wide update, two-click problem report,
   backup/restore, reset.

---

## 11. Minimum input and automatic setup

The goal: after a few preselected choices, the only thing a user types is an
API key, and only if they chose a cloud provider. Everything else has a
default, is detected, or is installed by Martlet.

### 11.1 What the user provides, and nothing else

| Always (first run) | When |
| --- | --- |
| Where each job runs | Preselected from this PC's hardware (11.3). Most users change nothing. |
| An API key per cloud provider chosen | Only for cloud choices. One key per provider, reused by every job that provider serves. |
| One agreement checkbox | Covers exactly what the plan installs or sends (P6). |
| What Windows itself demands | At most one UAC prompt per setup run; a restart only when WSL needs it, after which setup resumes by itself. |

| Only when the user opts in | Input |
| --- | --- |
| A custom voice | A 10-second recording and *whose voice is this* |
| Audio2Face lip-sync | A free NVIDIA NGC key (the only second key in the product) |
| A Linux machine over SSH | Address, user and password once |
| Their own character | A model file |
| Their own model server | URL and model ID under *Advanced* |

### 11.2 Minimum-input recipes from first launch

| Situation | User does | Martlet sets up |
| --- | --- | --- |
| Gaming PC (NVIDIA 12 GB+), no key | *Set up* › *Agree and set up* | Thinking: local model sized to the GPU (Ollama). Listening: whisper on this PC. Voice: F5 with the bundled default voice. Character: Hiyori, voice-loudness lip-sync. |
| Mid PC (NVIDIA 6–10 GB), no key | Same, 2 clicks | Thinking: 8B local model. Listening: whisper (CPU, so the GPU stays free for thinking). Voice: Windows voice. |
| Laptop, no GPU, OpenAI key | Pick *Cloud* for Thinking, paste key, agree | Thinking, Listening and Voice all on OpenAI with that one key. |
| Laptop, no GPU, OpenRouter or NVIDIA Build key | Pick *Cloud*, paste key, agree | Thinking on that provider. Listening and Voice on Windows speech (no install, no key). |
| Laptop, no GPU, no key | *Set up* › *Agree and set up* | Thinking: small CPU model (slow but works, Home suggests a key or another computer). Listening and Voice: Windows speech. Works with zero typing. |
| Second PC | *Join my Martlet network* › approve on the first PC | Takes the network's plan and keys; offers its abilities (B1). |

### 11.3 Default plan by hardware

The plan reads CPU, RAM, GPU and VRAM locally and budgets VRAM across jobs
that share one GPU (it never plans more than fits). Model names are examples;
sizes follow [Recommended setups](RECOMMENDED_SETUPS.md).

| This PC | Thinking | Listening | Voice | Lip-sync |
| --- | --- | --- | --- | --- |
| No NVIDIA GPU | Cloud if a key is given, else a ~3B CPU model | Windows speech | Windows voice | Voice loudness |
| NVIDIA 6–10 GB | 8B local | whisper small (CPU) | Windows voice | Voice loudness |
| NVIDIA 12–16 GB | 8B–14B local | whisper (GPU) | F5, bundled voice | Voice loudness (Audio2Face offered) |
| NVIDIA 24 GB+ | 14B–32B local | whisper (GPU) | F5, bundled voice | Voice loudness (Audio2Face offered) |
| Network has a stronger computer | That computer, for any job it does better | same | same | same |

Rule for keys: when the user adds a provider key, every job still on a
fallback (small CPU model, Windows speech) that the provider does better
moves to it. Jobs already running well locally stay local.

### 11.4 Every input today, and what replaces it

| Input today | Where | Target |
| --- | --- | --- |
| *Demo only* vs *Use AI models* (Demo preselected) | Setup › Choice | Removed. First run is real setup. |
| Job selector | Setup › Jobs | Removed; one tab per job. |
| Provider | Setup › Jobs, cloud card | Preselected (OpenRouter for Thinking; the provider whose key the user has). Asked only when *Cloud* is chosen. |
| API base URL | Setup › Jobs | Filled from the provider; only *Custom* under *Advanced* shows it. |
| Model ID (text) | Setup › Jobs, cloud card | Preselected recommended model from a list; after the key is entered, the list comes from the provider. Typed IDs only under *Advanced*. |
| TTS voice ID (text, `alloy`) | Setup › Jobs | Preselected; chosen from a list with *Preview*. |
| **API key** | Setup › Credentials, cloud card | **Asked.** Next to the provider, once per provider, reused across jobs and brought along when joining a network. |
| Route consent checkbox | Setup, cloud card | The terms sheet, once. |
| *Apply this job's choice*, *Save checkpoint*, *Save and exit setup*, cloud *Save* | Setup, cloud card | Removed; choosing commits (P2). |
| Store / read / detach / remove key | Setup › Credentials | Thinking › *Advanced* › *Keys*. |
| Microphone | Audio setup | Default: *Windows default (Headset Mic)*, following Windows when it changes. |
| Speakers | Audio setup | Default: *Windows default*, following Windows. |
| Microphone and output tests (gate "qualified") | Audio setup | Optional *Test* buttons; never a gate. |
| *Save audio choices and historical checkpoints* | Audio setup | Removed (done): picking and finished tests save automatically. |
| Character | Avatar window | Hiyori (already the default). |
| *Save choices*, *Activate reviewed avatar* | Avatar window | Removed; picking applies. |
| GPU inspection permission, mapping, reduced-fidelity acceptance, analysis permission | Avatar window › Advanced | Automatic: mapping from `Audio2FaceAutoMapping`, permission covered by the terms sheet. Manual mapping under *Advanced*. |
| Renderer endpoint, SDK path | Avatar window | Defaults; *Advanced* only. |
| Lip-sync | Avatar window, Devices | Voice loudness; Audio2Face used automatically when it's available. |
| Host method (Docker / SSH Docker / SSH Ubuntu / manual) | Hosts › Where it runs | Detected: *This PC* sets itself up; a Windows PC joins the network; SSH only for Linux. |
| SSH target | Hosts › Install | Asked, Linux path only. |
| Host LAN address | Hosts › Install | Detected and reported by the machine. |
| *Install Docker Desktop* | Hosts, welcome tour | Inside the runner when a job needs it (or not needed, 11.6). |
| Pairing code paste, *Pair with host*, *Check host* | Hosts › Pair | Removed; network trust (B1, B2). |
| Role options (GPU or CPU, model) | Role install dialog | *Automatic*, preselected by the machine's hardware. |
| NVIDIA NGC key | Audio2Face role | Asked only when the user turns on Audio2Face, with *Get a free key ↗*. |
| Talk mode, talk key | Conversation | Push-to-talk on a global key (preset), changeable on Listening. |
| Hands-free sensitivity, reply pause | Conversation | Defaults (0.5, 0.8 s); Listening › *Advanced*. |
| *Speak the reply …* (off) | Conversation | On whenever a voice is set. |
| Per-message consent checkboxes | Conversation | Standing permissions (G3). |
| Voice ID, screen watching | Conversation | Off; one switch each when wanted. |
| Memory: storage location, enable checkbox, *Save memory configuration* | Memory window | One switch; stored in Martlet's data folder. |
| Updates: check, interval, auto-install, host updates | Settings | All on by default: check daily, install when Martlet next closes, update the network too. |
| Persona name and text | Companion window | Default *Martlet* persona; editing optional. |
| *Keep who does what in sync*, failover | Devices | Always on inside a network. |
| Setup advisor questions (3 pages) | Advisor window | Replaced by hardware detection (11.3). |
| Prerequisite tick list | Welcome tour | Removed; each job installs what it needs (11.5). |
| Windows microphone privacy | Prerequisites | Detected. Windows doesn't let apps change it, so a Fix card opens the exact Settings page. |

### 11.5 Prerequisites Martlet installs by itself

All run in the inline runner (P5), covered by the one agreement and one UAC
prompt per run.

| Prerequisite | Needed when | Target |
| --- | --- | --- |
| WebView2 Runtime | Always (character) | Bundled with the installer; never a separate step. |
| Ollama | Thinking on this PC | Silent install, then the model download, load and test. |
| Model files (LLM, whisper, F5) | The job that uses them | Downloaded in the runner; resumable. |
| Windows speech languages | Windows speech chosen and the language is missing | Installed through Windows features in the runner. |
| WSL 2 | A job on this PC that needs the host service | `wsl --install --no-distribution` in the runner; if Windows needs a restart, Martlet resumes setup after sign-in. |
| Docker Desktop | Same | Silent install with its licence accepted on the user's behalf after the terms sheet; started automatically; no sign-in. |
| Firewall rule (TCP 9443) | Only when this PC helps other computers | Part of the same single elevation; no separate Public-to-Private dialog. |
| NVIDIA driver | GPU jobs | Detected. If missing or too old, a Fix card links to the driver download (drivers can't be installed silently and reliably). The plan falls back to CPU or cloud until then. |
| NVIDIA NGC key | Audio2Face only | Opt-in (11.1). |

### 11.6 Gaps that block "just a key"

These need building before the recipes in 11.2 work:

1. **Windows speech as a route.** Done for speaking: *Its voice › This PC ›
   Windows voice, no Docker* saves an installed Windows voice and
   conversations speak with it through SAPI on this PC (no System.Speech
   package in the release). `Martlet.Stt.Windows` (recognition) still isn't a
   choice in the app.
2. **Listening on this PC without Docker.** `Martlet.LocalStt` (native
   whisper.cpp, no Docker) exists as a disabled candidate. Wiring it in
   removes WSL, Docker Desktop and the host service from the most common
   *This PC* listening setup.
3. ~~A bundled default F5 voice~~: done in #145 (F5-TTS's MIT English
   sample is used when no voice is chosen; recording is optional).
4. **One key per provider.** Keys are bound per job today; an OpenAI key
   should serve Thinking, Listening and Voice without being entered three
   times.
5. **Live model lists.** After a key is entered, read the provider's models
   instead of a static list and typed IDs.
6. **A zero-key Thinking fallback**: a small CPU model in the plan for PCs
   with no GPU and no key.
7. **Resume after restart** for WSL installs.
8. **Evaluate dropping Docker Desktop on Windows**: run the host service in
   a Martlet-managed WSL distribution (the native Ubuntu host path) for the
   roles that don't need containers. Audio2Face still needs containers.

### 11.7 Current flows that need this treatment

Each flow below asks for a step Martlet could do itself.

| # | Flow today | Extra steps the user takes | Target | Code |
| --- | --- | --- | --- | --- |
| 1 | Thinking › This PC | *Install Ollama*, *Download model* (console), *Check Ollama*, *Use Ollama on this PC* | Selecting *This PC* runs the whole chain (C2) | `MainWindow.SetupPages.cs:314-345` |
| 2 | Voice / Listening › This PC | *Install Docker Desktop*, *Set up this PC's host service* (one click since #141/#142), then *Use F5/whisper on this PC*, *Check it* | Selecting *This PC* runs Docker › host service › role › model › test › switch as one chain; Listening uses native whisper.cpp with no Docker (11.6) | `MainWindow.SetupPages.cs:408-420` |
| 3 | Voice › F5 | Done in #145: the bundled sample voice is used without a picker; *Choose another voice* opens the picker | Keep; move the picker inline into the Voice list (D2, D3) | `F5VoiceDialog.cs`, `HostSpeech.cs` |
| 4 | Cloud card | Consent checkbox, key per job, *Save* | Paste key = switch; key reused across jobs (C4) | `MainWindow.SetupPages.cs:540-580` |
| 5 | Legacy Setup window | Demo preselected; *Apply this job's choice*, credentials tab, *Save checkpoint*, *Save and exit setup* | Removed | `SetupWindow.xaml` |
| 6 | Audio setup | Per-test confirmation; the microphone test gates *Working* (Save removed: picking applies) | Windows default devices, no gate | `AudioSetupWindow.xaml(.cs)` |
| 7 | Conversation | Voice output off each time; 1–3 consent ticks per message; *Finish and send* | Voice on; standing permissions; release sends | `LiveConversationWindow.xaml:105-237` |
| 8 | Character | *Save choices*, *Activate reviewed avatar*, inspection and analysis permissions, manual mapping | Picking applies; automatic mapping | `AvatarWindow.xaml(.cs)`, `Audio2FaceAutoMapping.cs` |
| 9 | Host role install | Role dialog with GPU/CPU choice and options | *Automatic* preselected; dialog only for the NGC key | `HostDialogs.cs:125-148` |
| 10 | Add a computer | Method choice, typed LAN address, *Install Docker Desktop*, pairing code, *Pair with host*, *Check host* | Join the network (B1); SSH only for Linux (B6) | `HostsWindow.xaml(.cs)` |
| 11 | Prepare a Linux host | Type target, *Read status*, *Tick what is missing*, *Run selected* | Part of B6's runner | `PrepareHostWindow.xaml(.cs)` |
| 12 | Prerequisites | Welcome-tour tick list, visible PowerShell; Ollama and Windows speech left unticked | Installed on demand by the job that needs them | `Prerequisites.cs`, `MainWindow.Shell.cs:231-260` |
| 13 | Firewall | Separate *make network Private* confirmation, then UAC | Inside the single elevation, only when helping others | `HostsWindow.xaml.cs:308-320`, `WindowsFirewall.cs` |
| 14 | Memory | Storage choice, enable checkbox, *Save memory configuration* | One switch | `MemoryWindow.xaml(.cs)` |
| 15 | Updates | Four separate preferences | All on by default | `MainWindow.Updates.cs`, `UpdateCheckPreferences.cs` |
| 16 | Devices sync and failover | Two opt-in toggles | Always on inside a network | `MainWindow.Cluster.cs` |
| 17 | Voice Library import | Purpose, engine, rights basis, storage confirmation, *Import* | *Make a new voice* (D3) | `VoiceLibraryWindow.xaml.cs` |
| 18 | Setup advisor | Three question pages, then a plan the user still carries out by hand | Hardware-based plan that sets itself up (A1) | `SetupAdvisorWindow.xaml(.cs)` |
