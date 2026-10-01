# User stories and target flows

Martlet's flows grew one feature at a time, and it shows: a button opens a
page, a button on that page opens a window, that window opens a console, and
the console wants you to type `yes`. This document lists every user story we
can think of and the flow each **should** have. It is the target for the next
round of UI work; [UI design](UI_DESIGN.md) describes what ships today.

## Rules every flow follows

1. **One job, one page.** Everything needed to change a job's *Now* line
   (where it runs, engine, model, voice, key, devices, install, test) happens
   on that job's page. No separate windows to finish a job.
2. **No consoles, no typed confirmations.** Installs, downloads, host setup,
   pairing and updates run inside Martlet with an inline progress bar, a
   one-line status and *Cancel*. Raw output lives in a *Details* expander.
   Nobody types `yes`, `start`, `stop` or a pairing code.
3. **Terms up front.** Every licence, EULA and data-destination agreement the
   chosen setup needs is shown once, together, before anything installs. After
   setup, talking never stops to ask for an agreement.
4. **The default is the answer.** Every choice opens with the recommended
   option already selected for this machine. One click accepts it.
5. **One click per outcome.** A primary button does the whole outcome (install
   engine, download model, verify, switch *Now*) and reports when it is done.
6. **Elevate once.** If Windows needs admin rights (Docker, firewall, WSL),
   batch them into one UAC prompt per setup run, explained beforehand.
7. **Problems come with their fix.** Anything broken shows what it means and a
   single button that fixes it, on Home and on the job's page.
8. **Safety stays.** Keys stay in Windows Credential Manager, destructive
   actions still get one default-No dialog, and every standing permission is
   visible and can be switched off in one click.

## Pain points these stories replace

| Today | Target |
| --- | --- |
| Ollama model download opens a visible `cmd.exe /k` window | Inline download with progress on the Thinking page |
| Prerequisites (WebView2, WSL, Docker) install in a visible PowerShell console | Inline install list with per-item progress, one UAC prompt |
| Docker Desktop install opens a console after a separate agreement dialog | Docker's agreement is in the setup terms sheet; install runs inline |
| Manual host path: open console, type `start`, run `pair`, copy a code, type `stop` | Removed from the main path; the new machine joins the network itself |
| Speakers/microphone live in a separate Audio setup window | Device picker and test sit on the Voice and Listening pages |
| Character, lip-sync and Audio2Face span the Character page, Avatar window, Hosts window and a confirmation | All on the Character page |
| Three "voice" windows (Voice Library, F5 voice dialog, Voice ID) | One Voice page; Voice ID moves to Listening ("Recognise my voice") |
| Per-turn checkboxes in the conversation (provider, mic, upload, screen, memory) | Standing permissions chosen in setup, shown as indicators while talking |
| Legacy Setup window, setup advisor, welcome tour and setup pages overlap | One first-run flow and four job pages; the advisor becomes the default selection |
| Every desktop pairs every host on its own; nothing joins a "network" | A Martlet network that a machine creates or joins, now or later |

## Concepts

- **Network**: your Martlet machines (desktops and hosts) that trust each
  other and share one plan. A machine belongs to at most one network.
- **Machine profile**: what a machine can do, reported when it joins and kept
  current: CPU, RAM, GPU and VRAM, OS, installed engines and models, and the
  jobs it is willing to take ("can run LLMs up to ~14B", "can run F5").
- **Jobs**: Thinking, Voice, Listening, Character (lip-sync). Each job has a
  *Now* line: where it runs, what runs it, and whether it works.
- **Where it runs**: *This PC*, *Another of my computers* (a network member),
  or *A cloud provider*.

## A. First run and the network

### A1. Set up the first machine (create a network)

*As a new user on my first PC, I want Martlet to set itself up with sensible
defaults so I can talk to it in minutes.*

1. Welcome: "Hi, I'm Martlet." Two cards: **Set up Martlet on this PC** and
   **Join my existing Martlet network**.
2. **Set up** → Martlet reads this PC's hardware (local only) and shows a
   one-screen plan, recommended option preselected for each job:
   - Thinking: *This PC: Qwen 3 8B (fits your 12 GB GPU)* or *Cloud* when the
     PC can't run a useful model.
   - Voice, Listening: *This PC* engines when supported, otherwise Windows
     speech, otherwise cloud.
   - Character: the bundled character, lip-sync by voice loudness (or
     Audio2Face when the GPU supports it).
   Each line has *Change* which expands the choices in place (see B).
3. **Review terms**: one sheet listing everything the plan installs or sends
   data to (Docker, Ollama, model licences, F5, Audio2Face, Live2D, any cloud
   provider's data use), each with its link. One **Accept and set up** button.
   Cloud choices ask for their API key on this sheet.
4. Setup runs inline: a checklist with progress per item, a single UAC prompt
   if needed, and *Cancel*. The user can leave and come back; progress
   continues and is shown on Home.
5. Done: "Ready when you are" with **Start talking**. A network is created
   with this PC as its first member, silently.

### A2. Set up a new machine by joining an existing network

*As a user adding a second PC, I want it to take the network's configuration
so I don't set everything up again.*

1. Welcome → **Join my existing Martlet network**.
2. Martlet looks for networks on the LAN and lists them ("Martlet network on
   DESKTOP-01"). If none are found, it shows a field for a short join code.
3. The user picks the network. On any existing member, a notification says
   "*GAMING-PC wants to join. It has an RTX 4090 (24 GB).*" with **Allow** /
   **Deny**. (Alternatively the existing member shows a 6-digit code that the
   new machine enters; no copy/paste of long tokens.)
4. Once allowed, the new machine:
   - receives the network plan (who does what, hosts, cloud routes);
   - is trusted by every member, with no per-host pairing;
   - sends its machine profile;
   - asks: **Use this PC to talk to Martlet** (companion), **Lend this PC to
     the network** (host), or both.
5. If it will help run jobs, Martlet shows what it could take ("This PC can
   run Thinking faster than DESKTOP-01") with the recommended models and one
   terms sheet, then installs inline as in A1.
6. Cloud keys: "Copy cloud keys from the network?" (default yes, one click).
   Keys travel encrypted to the new machine's Credential Manager.
7. Done: the new machine appears on every member's Devices map.

### A3. Join a network after setting up standalone

*As a user who already set this PC up on its own, I want to connect it to my
network later and keep what it can do.*

1. Settings → **Network** → **Join a network** (also offered on Devices).
2. Same discovery/approval as A2.
3. Martlet asks how to merge: **Keep this PC's setup and offer its abilities
   to the network** (default) or **Use the network's setup**. Either way the
   PC's profile is announced: "This PC runs Ollama with Qwen 3 8B and can take
   Thinking."
4. Other members see it on Devices and in each job's *Another of my
   computers* list immediately.

### A4. Advertise and update a machine's abilities

*As the network, I want to know what each machine can run so choices only
show machines that work.*

- Profiles are sent on join, on startup, and when an engine or model is
  installed or removed. No user action.
- Each job's machine list shows *ready*, *can install* (with size and time),
  or *can't* (with the reason, e.g. "needs 6 GB NVIDIA GPU").

### A5. Remove a machine or leave the network

- From any member: Devices → machine → **Remove from network** (one default-No
  confirm). Its trust is revoked everywhere; jobs it ran move to the next best
  machine or back to their fallback, and Home says so.
- From the machine itself: Settings → Network → **Leave network**. It keeps
  its local setup and becomes standalone.

### A6. Replace or reinstall a machine

- Reinstalling Martlet and choosing **Join** with the same machine name offers
  "Restore this machine's previous role?" (default yes) and reinstalls its
  engines and models inline.

### A7. A non-Windows host (Linux, over SSH)

*As a user with a headless Linux GPU box, I want to add it without a console.*

1. Devices → **Add a computer** → **A Linux machine I can SSH into**.
2. Fields: `user@address`, password or key. **Add it**.
3. Martlet installs the host over SSH, joins it to the network and reads its
   profile, all inline. Host-key and sudo prompts appear as Martlet dialogs.
4. *I'll run the command myself* remains under *Other ways*, producing a
   one-line command that ends with the machine joining (no code to copy back).

## B. Setting up each job (Thinking, Voice, Listening, Character)

Each job page has the same shape, top to bottom:

1. **Now**: one line ("Thinking: Qwen 3 8B on This PC, working") with a
   status dot and, if broken, the fix button.
2. **Where it runs**: three cards. Choosing one expands its options in place
   with the recommended option preselected and one primary button.
3. **Devices** (Voice: speakers; Listening: microphone): picker, *Test*, level
   meter, inline.
4. **More**: job-specific extras (below).

Changing anything on this page updates *Now* on this page. Nothing on this
page opens another window.

### B1. Thinking on This PC

1. Thinking → **This PC**.
2. Expands: list of models that fit this machine, each with size, speed
   estimate and quality hint; the recommended one is selected and marked.
3. **Set up Qwen 3 8B** → installs Ollama if missing, downloads the model with
   a progress bar, loads it, sends a tiny local test prompt, switches *Now*.
4. If the user had already accepted terms for this in setup, nothing is asked.
   If not (a new engine chosen later), the terms appear inline above the
   button as a single tick box: still setup, never runtime.

### B2. Thinking on another of my computers

1. Thinking → **Another of my computers**.
2. Expands: every network member with its ability for this job (*ready with
   Llama 3 70B*, *can install*, *can't: 4 GB GPU*). Best one preselected.
3. **Use GAMING-PC** → if the engine/model is missing there, Martlet installs
   it remotely with inline progress, then switches *Now*.
4. *Add a computer* links to A2/A7 and returns here when done.

### B3. Thinking with a cloud provider

1. Thinking → **A cloud provider**.
2. Expands: provider list (OpenRouter, OpenAI, NVIDIA Build, custom) with a
   short "what it sends, may cost money" line; model list with the recommended
   one selected; API key field (shows *saved* if a key exists).
3. **Use OpenRouter** → saves the key, sends one tiny test request, switches
   *Now*. The data-destination agreement is the tick box above the button.

### B4. Voice

- Same three cards as Thinking (This PC: F5 or Windows speech; another
  computer: F5 host; cloud: OpenAI TTS etc.).
- **Speakers** picker with *Play test sound*, inline.
- **Voice** list (bundled voices, Voice Library voices, cloned voices) with
  *Preview*. Picking one switches *Now*.
- **Make a new voice** expands in place: record or choose a WAV, transcript
  auto-filled by speech-to-text and editable, one *This is my voice or I have
  permission* tick box, **Create voice**. It joins the list and is selected.

### B5. Listening

- Same three cards (This PC: whisper or Windows speech; another computer;
  cloud).
- **Microphone** picker with live level meter and *Say something* test that
  shows the transcript, inline.
- **How I talk**: *Hold a key to talk* (default, key shown) or *Hands-free*
  (sensitivity slider).
- **Recognise my voice** (optional): record three phrases inline; Martlet
  ignores other voices.

### B6. Character

- **Character** gallery: bundled and imported Live2D/VRM models with
  thumbnails. Picking one shows it immediately; *Show on desktop* toggle.
- **Import a character** expands inline (file picker, validation result).
- **Lip-sync**: *This PC*, *Another of my computers*, or *Voice loudness*,
  same expand-and-click pattern; Audio2Face install happens inline.
- **Personality**: name, description, style sliders, inline (no separate
  window).
- **Customise mapping** stays under *Advanced*.

## C. Terms, permissions and privacy

### C1. Accept everything once

*As a user, I want to agree to terms during setup, not while I'm talking.*

- The terms sheet (A1 step 3) is built from the plan: only items that will be
  installed or used are listed.
- Accepting stores the item and version. Later setups that add a new item show
  only that item, inline on the job page (B1 step 4).
- Runtime (conversation, character, listening) never shows a licence, EULA,
  or data-destination agreement.

### C2. Standing permissions instead of per-turn checkboxes

*As a user, I want to decide once what Martlet may do, and see when it does
it.*

- At the end of setup, one **What Martlet may do** card with switches:
  *Use the microphone while I talk* (on), *Send my voice to the cloud for
  transcription* (only when listening is cloud), *Remember things between
  chats* (off), *Look at my screen* (off).
- While talking, active permissions show as indicators in the conversation
  header (mic, screen, memory, cloud). Clicking one turns it off on the spot.
- Locking Windows, *Stop* or closing still stops capture immediately.
- These switches are also in Settings → Privacy.

> This replaces the per-action authorization checkboxes in the conversation
> window. It is a deliberate policy change: consent becomes standing,
> visible and revocable instead of re-asked per turn.

## D. Everyday use

### D1. Talk

- Home → **Start talking** (or a global hotkey) → conversation opens ready.
  Type and press Enter, or hold the talk key. No checkboxes to tick first.
- One **Send** button; "Finish and send" goes away (releasing the talk key
  sends).

### D2. Hands-free and screen commentary

- Toggle *Hands-free* and *Watch my screen* from the conversation header. If
  the standing permission is off, the toggle turns it on (one click) and the
  indicator appears.

### D3. Show or hide the character

- One *Character* toggle on Home, in the conversation header and in the tray.
  The same word everywhere ("Show character" / "Hide character").

### D4. Memory

- *Remember this* / *Forget this* in the conversation. Settings → Memory lists
  facts with edit and delete inline. Export is one button with a save dialog.

## E. Managing the network

### E1. See what each machine is doing

- Devices map (as today) with each machine's profile and jobs.

### E2. Move a job to another machine

- From the map or the job page: pick the machine → **Move here**. Missing
  engines install inline first. Network sync is always on inside a network
  (no separate toggle).

### E3. Failover

- On by default inside a network for jobs that more than one machine can run.
  Home says "Thinking moved from GAMING-PC to DESKTOP-01" when it happens.

### E4. Change what this PC is for

- Settings → Network → *This PC*: tick boxes *Talk to Martlet here* and *Lend
  this PC to the network*. Turning on lending shows what it can take and sets
  it up inline.

## F. Keeping things working

### F1. Something broke

- Home shows "Martlet can't hear you" with the reason and **Fix it** (restart
  engine, reinstall model, move to another machine, re-enter key). The job
  page shows the same.

### F2. Updates

- Martlet checks automatically (default on) and shows *Update ready*.
  **Update** updates this PC and then every network member, inline.

### F3. Report a problem

- Settings → Help → **Save a problem report** → save dialog. The
  record/freeze/preview stages move under *Advanced*.

### F4. Back up and restore

- Settings → **Back up** / **Restore** with a file dialog and one default-No
  confirm for restore. Inside a network, a new member already gets the
  configuration, so backup is mostly for standalone PCs.

### F5. Start over

- Settings → **Reset Martlet on this PC** (default-No confirm) → leaves the
  network, removes local setup, returns to Welcome.

## Implementation order

Each slice is shippable on its own:

1. **Inline runner**: one in-app progress component that replaces every
   visible `cmd.exe`/PowerShell launch (Ollama pull, prerequisites, Docker
   install, host setup). Removes the manual console path from the main flow.
2. **Job pages own *Now***: fold Audio setup, Avatar, Companion, Voice Library
   and the F5 voice dialog into the four job pages; expand-in-place *Where it
   runs* with recommended preselection and one primary button; retire the
   legacy Setup window and advisor as separate entry points.
3. **First-run plan and terms sheet**: hardware-based plan, one terms sheet,
   one UAC prompt, inline checklist (A1, C1).
4. **Standing permissions**: replace per-turn conversation checkboxes (C2,
   D1, D2).
5. **Network membership**: network identity, LAN discovery, approve-to-join,
   network-wide trust replacing per-desktop host pairing, machine profiles,
   optional key transfer (A2–A6, E2–E4). Builds on the existing cluster plan
   ([CLUSTER.md](CLUSTER.md)).
6. **Maintenance**: one-click network update, simplified problem report,
   reset (F2–F5).
