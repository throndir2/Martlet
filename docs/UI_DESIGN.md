# Martlet desktop UI design

Feedback on the first screen was clear: it was overwhelming, and people could
not tell what to do next. The old home window stacked about twenty buttons, a
set of update checks and three diagnostic text boxes on one scrolling
page. This document describes the replacement: one job per screen, a single
obvious next step at every stage, and a living map of the user's computers.

## Principles

1. **One screen, one job.** Each stage shows one primary action. Everything
   else moves behind navigation or a *Details* expander.
2. **Say what to do, in plain words.** Headlines say where you are ("Almost
   there"), and the button says what happens next ("Set up thinking").
   Exact legal, cost and data wording stays available, one click away, and is
   never removed.
3. **The companion is the hero.** The animated Martlet mascot, the cream bird
   from the app icon, greets you. The Live2D
   or VRM character is one toggle away on every main surface.
4. **Show the hardware as a map.** The *Devices* page shows every computer and
   cloud service, what each one runs, and what it has. Select a node to
   configure it.
5. **Motion with purpose.** Page changes slide and fade, progress fills in,
   connections flow and online nodes pulse. When Windows *Show animations* is
   off, looping motion stops and transitions happen instantly.
6. **Nothing changes by surprise.** Consent, per-action authorization and
   *no network on launch* still apply. The redesign changes layout and wording,
   not permissions.

## Stages and surfaces

```text
first launch ──> Welcome tour ──┬── "Talk with my companion here" ──> Home (Now lines ─> ready)
                                └── "Lend this PC to Martlet"     ──> Host dashboard (host service)
Every mode: Home · Devices (map) · Companion* · Settings      (* companion mode only)
```

Each page answers one question, so no two pages do the same thing:

| Page | Question | Changes settings? |
| --- | --- | --- |
| **Home** | Is Martlet ready, and can I talk to it? | No. It shows status and links to where each thing changes. |
| **Companion** | How does Martlet think, sound, listen, look and remember? | Yes: the one place each of those choices is made. |
| **Devices** | Which computer does what? | Machines and handing jobs between them. |
| **Settings** | How does the app itself behave? | Appearance, this PC's role, updates, tools. |

### 1. Welcome tour (first launch)

This full-window overlay appears when no device-role choice has been saved
(`device-role.txt`). The installer asks no setup questions, so this is where
setup starts. It has up to four short cards, animated between steps and marked
with step dots:

1. **Hi, I'm Martlet.** One sentence about what Martlet does, plus *Let's begin*.
   *Skip for now* leaves the tour and uses companion mode.
2. **What's this computer for?** Two large choice cards:
   - *Talk with my companion here* (the PC you sit at).
   - *Lend this PC to Martlet* (a spare or gaming PC with a GPU that runs heavy
     parts, such as lip-sync, for another PC).
3. **Get this PC ready** (only when something is missing). One tick box per
   missing prerequisite, read from the registry and files: WebView2 and blocked
   microphone access are ticked; WSL 2 + Docker Desktop is ticked for a host
   with an NVIDIA GPU; Windows speech and Ollama wait unticked for the setup
   advisor. *Install selected* runs the prerequisites tool for the ticked items
   hidden, with its output in a Martlet run window; *Not now* moves on.
4. **How would you like to start?** (companion mode) *Recommend a setup for me*
   opens the setup advisor (its plan adds *Install on this PC* for what it runs
   here), and *I know what I want* opens Companion › *Thinking*. In host mode,
   the tour ends on the host dashboard.

The tour saves only the device role and contacts nothing; it installs only the
items you tick and confirm with *Install selected*.
Settings > *This PC's role* changes the role or replays the tour.

### 2. Home (main PC)

Home answers "is Martlet ready, and can I talk to it?" It shows status and
offers *Start talking*; it changes no settings itself. Every choice is made in
Companion (section 7), and Home links there.

The hero card changes with setup progress:

| Stage | Headline | Primary action |
| --- | --- | --- |
| Nothing saved yet | "Let's bring your companion to life" | *Set up thinking* (opens Companion › *Thinking*) |
| Thinking not chosen, or chosen without destination consent | "Almost there" | *Finish setup* |
| Ready (thinking chosen, consented and on) | "Ready when you are" (with a time-of-day greeting) | **Start talking** |

*Show character* sits next to the primary action at every stage. *Not sure?
Get a recommendation* opens the advisor.

Below the hero, **Now** has one line per job: *Thinking*, *Voice*,
*Listening* and *Character*. Each line has a status dot (green working, amber
needs attention or not working, grey not set up), what Martlet uses now in one
sentence, and one button (*Set up*, *Review* or *Change*) that opens that
job's Companion page. Only Thinking is required; while it is missing its line
is highlighted and its button is primary. When lip-sync stops working, the
*Character* line becomes a *Lip-sync* line that opens the Lip-sync page. The Windows default microphone is
assumed to work: the Listening line says *No microphone found* or *Your chosen
microphone isn't connected* (amber, with *Fix mic* once listening itself is
ready) only when this PC has no microphone or the chosen one is unplugged, and
the Listening tab's **Now** card says the same. A microphone test is optional.

When a chosen job stops working (its host isn't answering, its role was
removed, its key was deleted, or Setup saved a route this version can't use),
a **What isn't working** card appears under the hero with the reason, what it
means ("Martlet can't hear you; you can still type") and one-click fixes; the
job's Now line says *Not working now*. If thinking is down, the headline
becomes "Martlet can't reply right now". The rules are in
[Platforms](PLATFORMS.md#what-the-app-guarantees).

### 3. Devices (hardware map)

This page answers "what do I have, and what is each machine doing?"

```text
            [OpenAI]          [OpenRouter]
                 \              /
                  \            /
   [Add a computer] - - [ This PC ] - - [gpu-pc-host]
                         (center)
```

- **Nodes** are *This PC* (center), each paired Martlet host or self-hosted
  gateway, each cloud destination grouped by origin, and a ghost *Add a
  computer* node. When no conversation model is chosen, a ghost *Conversation
  model* node points to Companion › *Thinking*. Every paired host appears, whether or not it has
  a job yet.
- **Who does what** (above the map, companion mode) has one tile per job:
  *Thinking*, *Listening*, *Speaking* and *Lip-sync (Audio2Face)*. Each tile
  names the computer or service in charge (select it to show that node).
  Thinking and listening also switch from a drop-down once a host is paired:
  the Setup choice or any paired host (with its model, or "not installed");
  handing the job to a host checks it, offers to install the role (Ollama or
  whisper) there and switches over once it is ready, and the Setup choice comes
  back without re-entering a key. Each tile's *Change in Companion* opens that
  job's page (lip-sync's opens *Lip-sync*); speaking changes there, because it needs a model and consent. **Lip-sync switches on the spot**
  from a drop-down once a host is paired: *This PC*,
  any paired host, or *Nobody (mouth follows voice loudness)*. A showing
  character keeps showing; the next sentence uses the new computer.
  Handing lip-sync to a host first checks it over its pinned pairing. If it
  does not run Audio2Face yet, Martlet offers to install it there in the same
  step and keeps the mouth on voice loudness until the host is ready. The host
  in charge goes first; this PC's own Audio2Face service and voice loudness
  are the fallbacks. *Check hosts* reads every host's roles (explicit only).
  The same card as on Home sits at the top of the board, also listing jobs
  whose host hasn't been checked since Martlet started. A host that can't run
  an engine at all (F5 without a 6 GB NVIDIA GPU, Ollama on an iPhone) appears
  in the drop-down as *can't take it now* with the reason, and its install
  command is replaced by that reason on the map. Phones and tablets have no
  install, remove, update or SSH commands; their roles are switched on on the
  device.
- **Keep who does what in sync on all my computers** (under the board, OFF by
  default) shares the assignments with every paired host and your other
  computers ([details](CLUSTER.md)): every 15 seconds it checks the hosts,
  follows changes made elsewhere and pushes changes made here. A status line
  says how many hosts hold the current plan and which need an update. Each
  tile then offers **Fail over to another host**: when its host stops
  answering for about 30 seconds the job moves to another paired host that runs
  the same engine, and the tile says where it moved from. Tiles also explain
  when this PC cannot follow the plan (host not paired here, no voice chosen).
- **Cards** show an icon, name, address, up to three role chips ("Thinks",
  "Listens", "Speaks", "Lip-sync", "Character") and a status dot. Ready nodes
  pulse, and animated dashes on the connections show which way data flows.
  Cloud services sit on the upper arc, computers and *Add a computer* on the
  lower arc. When the window is narrow, the details panel moves under the map.
- **Select a node** to open the detail panel with a slide-in:
  - *What it runs*: each hosted role and its model or voice.
  - *Hardware* for This PC, read locally from the registry and Windows: CPU,
    threads, memory, each GPU with VRAM, Windows version, LAN address, and
    whether Docker Desktop is installed or running. It adds capability hints,
    for example "NVIDIA 4 GB+: can run Audio2Face lip-sync". Remote hosts do not
    report hardware yet, and the panel says so.
  - *Connection* for remote hosts: address, pinned TLS identity, this PC's
    device ID, how Martlet reaches it, and a *Check connection* button that
    uses the existing pinned pairing and shows which roles it runs.
  - *Roles on a host*: *Hand lip-sync to this computer*, *Install Audio2Face
    there* and *Remove Audio2Face from it*, *Show its status*, *Update host*,
    *Pair again* and *Forget this host*. Install, remove, update and status run the same
    `martlet-host` engine on that computer the way Martlet reaches it (SSH with
    Docker, SSH native Ubuntu, or this PC's Docker Desktop), in a Martlet run
    window with live output and *Cancel* (never a console window); the owner's
    click is the confirmation. On this PC, roles with a GPU-or-CPU choice
    preselect the suggestion from the graphics card's free memory. The desktop never gets a shell,
    Docker socket or admin rights on the host. *How Martlet reaches it* sets
    that route; without one, Martlet copies the command to run on the host.
  - *Martlet version* for hosts: the release the host's gateway reports on
    *Check connection*, compared with this PC's. An older host shows *Update
    available* and a primary *Update it to Martlet x.y.z*.
  - *Cloud*: address, key storage, data sent and a "may cost money" note.
  - *Actions* for the node: Audio setup, Character, Prerequisites, *Host
    services on this PC*, *Change thinking in Companion* (one per job a cloud
    service or computer does), *Manage host* and similar.

Reading hardware is local only. It opens no port, starts no process and makes
no network request.

### 4. Host dashboard (host service UI)

When the device role is *host*, Home becomes the dashboard for the Martlet host
service on this PC:

- **Hero**: "This PC is a Martlet host", with a status pill and the address
  desktops use (`https://<LAN IP>:9443`).
- **Five steps**, each with one button:
  1. *Docker Desktop*: installed or running, read locally. Installs it with
     winget in a run window.
  2. *Host service*: set up the gateway, including the one-time firewall prompt.
  3. *Pair a desktop*: *Show a pairing code* asks for the main PC's device ID
     and shows the one-use code in a run window (copied to the clipboard).
  4. *Roles*: add or remove Audio2Face lip-sync, and show host status.
  5. *Keep it up to date*: rebuilds the host service from this app's version
     (`martlet-host update`); done when its gateway image matches the app.
- **Check host service** is an explicit TCP reachability probe of this PC's
  host port. It never runs automatically.
- *Use this PC as a companion instead* switches the device role.

### 5. Add a computer (Martlet hosts wizard)

The long form became a four-step wizard with a step rail:

1. **Where it runs**: large cards for *This PC (Docker Desktop)*, *Another
   computer over SSH (Docker)*, *Another computer over SSH (Ubuntu, native)*
   and *I'll type the commands myself*.
2. **Install**: only the fields that method needs, *Set up host* (*Add this
   computer* for SSH: connect, check Docker, set up, pair and read the machine
   report in a run window with live output and *Cancel*), and the exact command
   in a *Show the command* expander.
3. **Pair**: three numbered mini-steps, the device ID with *Copy*, *Pair
   automatically* (*Pair automatically over SSH* for SSH hosts), the pasted
   code and *Pair*, plus *Check* and *Forget*.
   Pairing adds the host to `hosts.json` (every paired host and how Martlet
   reaches it; nonsecret, secrets stay in Windows Credential Manager). Pairing
   hands the host no job (re-pairing keeps the ones it had): it stands by
   until you hand it a job under *Who does what*. Lip-sync goes to a host only
   once it runs Audio2Face, or with its install in the same step.
4. **Roles**: role cards (Audio2Face today; planned roles shown as coming soon),
   with *Add*, *Remove* and *Host status*.

### 6. Talk (conversation)

*Start talking* opens the conversation and nothing else: its history, what you
said and the message box. Every choice about how Martlet listens, speaks and
sees is made in Companion; the window has no settings, approvals, cost
envelopes, timelines or links to other windows.

- **Header**: the mascot, *Martlet* and one status line (*Listening. Just
  talk, or type below.*, *Martlet is thinking*, *Martlet is speaking. Esc
  stops it.*, or what went wrong in plain words). On the right, small toggles
  for what is on: **Listening** (shown with always listening; click to pause or
  resume, or *Mic not set up* until the microphone is tested) and **Vision**
  (shown when vision is on; click to pause or resume), then **Stop (Esc)**.
- **History**: chat bubbles for the whole conversation while the window is
  open: what you typed, what you said (the transcript, captioned *You
  (spoken)*), Martlet's replies as they stream in, its remarks about your
  screen, and short notes under a reply (*Remembered*, *Cut short*, a
  refusal). It follows new messages unless you scroll up to read.
- **Message box**: always in view. Enter sends, Shift+Enter starts a new line.
  With push-to-talk chosen, *Hold to talk* (hold the mouse or Space) sits next
  to *Send*; invoking it starts a recording and invoking it again sends.

Opening the window starts what Companion chose: **always listening** (the
default, once a microphone is set up and tested) and **vision** (off by
default). Typing while Martlet listens hands the microphone over for the typed
message, and listening resumes after the reply. **Stop (Esc)** stops the reply,
any recording, listening and vision at once and keeps the conversation;
locking Windows does the same and starts a fresh conversation, and closing the
window ends it.

### 7. Companion and Settings pages

- **Companion** answers "how does Martlet think, sound, listen, look and
  remember?" It is the one place each of those choices is made, as pages in a
  side list grouped by what they decide. Home's Now lines, the Devices tiles and
  nodes, fix cards, the tour and the advisor all open the matching page
  (Companion opens on the last page used, *Thinking* at first):

  - **How it works** (where each job runs):
    1. *Thinking*: where the conversation model runs, the provider, the model
       and its API key.
    2. *Voice*: where the voice runs and the voice itself, then the speakers,
       then *Speak Martlet's replies aloud* (on by default).
    3. *Listening*: the speech-to-text provider, model and key, then the
       microphone, then **How you talk**: *Always listening* (the default;
       Martlet hears you whenever the talk window is open, with sensitivity
       and how long a pause ends your turn) or *Push-to-talk*, and Voice ID
       (*Only respond to my voice* and *Set up Voice ID*).
    4. *Vision*: whether Martlet may look at your screen or a camera while
       the talk window is open (off by default): what it looks at (active
       window, whole screen, a camera found with *Find cameras*, a phone or
       network camera address, or a Home Assistant camera once Smart home is
       connected), how chatty it is, what is captured and where it is sent,
       and *Turn vision on*.
    5. *Lip-sync*: who moves the character's mouth, and where it runs.
  - **Who it is**:
    6. *Character*: what it looks like now, then the character model (show,
       hide, choose and customize, reset).
    7. *Personality*: the active persona and its style mix, *Edit
       personality*, and *Import a character card*.
    8. *Lorebook*: how many lorebooks are on for the active persona, each
       lorebook with *Turn on/off*, *Edit lorebooks* and *Import a lorebook*
       (see [Lorebooks](LOREBOOKS.md)).
    9. *Memory*: whether memory is on, and *Manage memory* for its facts.
  - **What it does**: how it answers and acts (generation settings, tools,
    smart home). The group appears once it has a page.

  A page gets its own entry only if it has its own **Where it runs** choice,
  its own consent or data destination, or its own list to edit; anything else
  is a card on an existing page. Planned pages and their groups: *Tools*
  (what it does).

  Every page starts with **Now**: what it uses and any problem stopping it.
  Cards appear only when they apply to the chosen place: the Voice Library
  shows only where F5 speaks (this PC's F5 or another of your computers), never
  for a cloud provider or a Windows voice; a one-provider cloud card names the
  provider instead of offering a one-item list; Ollama's download and check
  buttons appear once Ollama is installed.

  Each job tab (1-3) then asks **Where it runs**, defaulting to *This PC
  (recommended)*:

  - *This PC*: thinking uses Ollama at `http://127.0.0.1:11434/v1` (*Install
    Ollama and use it* installs Ollama with the suggested model sized to the
    graphics card and switches to it; *Download model* shows Ollama's progress
    in a run window, *Check Ollama* over loopback on request, *Use Ollama on this
    PC*). Voice offers two one-click choices, the one in use (or the one this
    PC's hardware suits) first: **F5 voice, with Docker** (*Set up F5 with
    Docker* sets up and pairs Martlet's host service on this PC, so this PC
    also becomes one of your hosts, installs F5 and switches over with F5-TTS's
    published sample voice) or **Windows voice, no Docker** (*Use a Windows
    voice* picks an installed voice in this PC's language, with no host
    service; a voice list and *Hear it* follow). Listening offers the same kind
    of two choices for whisper in Martlet's host service: **On the graphics
    card** or **On the processor**. Martlet reads the card live (nvidia-smi:
    memory in use, driver 580+ for whisper's CUDA build) and adds what it
    already runs there (Ollama's model, F5, Audio2Face), then recommends the
    card (large-v3-turbo, or small when memory is tight) unless it is too busy
    or can't run it, in which case the processor (small) is recommended with
    the reason. One click and one confirmation run Docker, the host service,
    the install with that choice and the switch in a single run window; the
    engine asks nothing.
  - *Another of your computers*: every paired host with what it runs and *Use
    it*, plus *Add a computer*, *Check hosts* and the Devices map.
  - *A cloud provider*: provider, model (and voice), API key and an explicit
    choice checkbox. Saving stores the route, then the key in Windows Credential
    Manager, then the confirmed choice.

  Lip-sync on its own page uses the same **Where it runs** chooser and
  cards, with *Voice loudness* in place of a cloud provider. The recommended
  place follows the hardware: *This PC* with an NVIDIA graphics card of 4 GB or
  more, otherwise another computer that can run Audio2Face, otherwise voice
  loudness. Each card's button commits the choice, which switches right away,
  even while the character talks:

  - *This PC*: two choices, the one in use first, like the voice. **Audio2Face,
    with Docker** (*Set up Audio2Face with Docker* sets up and pairs Martlet's
    host service on this PC, then hands lip-sync to it, installing Audio2Face
    with its NGC key; once the host service exists, *Use Audio2Face on this
    PC* and *Check it*) or **Your own Audio2Face service** at the character's
    loopback endpoint (*Use my own service*).
  - *Another of your computers*: the same host list as the job tabs, with *Use
    it*, *Add a computer*, *Check hosts* and the Devices map.
  - *Voice loudness*: *Use voice loudness* turns Audio2Face off.

  *Advanced setup* at the bottom of each job tab opens the full Setup window on
  that job, for every route type and stored or detached keys. Microphone and
  speakers, the Voice Library, character customization, personality and
  memory facts still open their own windows from their tabs.

  **Microphone and speakers** is one short page with two cards. Each card has
  the device picker (Windows default first; the list refreshes on its own when
  the window opens and each time it is opened, so a newly plugged-in device
  appears), one test button and a state chip with one plain sentence: *Ready*
  (the Windows default or chosen device is connected and assumed to work),
  *Not found* (no device, or the chosen one isn't connected), *Testing*,
  *Working*, *Needs attention* (with the fix, such as "Too quiet. Check that
  the microphone isn't muted...") or *Did you hear it?* (with *Yes, I heard
  it*). Testing is optional and *Done* is the primary button. Picking and
  finished tests save on their own; there is no Save button. Each test still
  asks first, and the exact evidence and *Troubleshooting* sit under
  *Details*. Home and the Listening tab warn about the microphone only when
  none is found or the chosen one isn't connected.
- **Settings**: palette, this PC's role and the tour, app updates (automatic
  checks and their interval, automatic installs, keeping hosts on this PC's
  version, *Check for updates now*, *Install*, *Update hosts now*), tools
  (Troubleshooting, Backup and restore, Prerequisites, Martlet hosts), and
  *Diagnostics* (pipeline, status details, local audio
  evidence, refresh and stop, create profile). Exit is also here.

### 8. Setup (configuration)

Setup keeps its four checkpoints (Overview, Jobs, Credentials, Review), drawn as
a numbered stepper with a connecting line, and *Next* is the primary button.
*Overview* describes using AI models. *Jobs* sets up one job at a time
(Thinking, Listening, Speaking) and shows only that job's fields, with the
provider's recommended model prefilled. It is the *Advanced setup* behind each
Companion job tab, which opens it on the matching job. Consent and credential
behavior did not change. The broader redesign
(jobs, placement on hosts, audio separation and queued local model hosting) is
in [COMPONENTS.md](COMPONENTS.md).

## Motion system

| Motion | Where | Spec |
| --- | --- | --- |
| Page enter | nav changes, tour steps, wizard steps, Setup steps | opacity 0 to 1 and Y +14 to 0, 260 ms, cubic ease-out |
| Float | hero mascot | Y ±5, 3.2 s, sine, forever |
| Twinkle | sparkles | opacity 0.35 to 1, 1.6 s, staggered |
| Sway | Martlet mascot (hero, tour, Talk header) | rotate ±4° (±3° in Talk), 3.6 s (4 s in Talk), sine, forever |
| Check pop | completed host step | scale 0.4 to 1, 320 ms, back ease |
| Pulse ring | online nodes, host status | scale 1 to 1.9 and opacity 0.6 to 0, 2 s, forever |
| Flow | map connections | dash offset, 1.2 s, linear, forever |
| Panel slide | node detail | X +24 to 0 and fade, 240 ms |
| Toast | status line | fade and slide in, fade out after 6 s |
| Press | all buttons | scale 0.97, 90 ms |

`Motion.Enabled` follows `SystemParameters.ClientAreaAnimation`, and is also off
when the `MARTLET_REDUCE_MOTION` environment variable is `1`. With it off,
looping animations do not start and transitions complete immediately.

## Implementation plan

1. Shared styles in `Themes/Motion.xaml` (nav items, choice cards, chips, node
   cards, step rows, link buttons), merged by `App` and the `ThemedWindow`
   fallback. `Motion.cs` provides the animation helpers.
2. Models: `DeviceRolePreference` (`device-role.txt`), `MachineInfo` (local
   hardware probe) and `NetworkMap` (turns settings, the avatar pairing and
   hardware into nodes, roles, facts and actions).
3. `MainWindow` becomes the shell: nav rail, welcome tour, stage-aware home,
   host dashboard, devices map with detail panel, Companion tabs and Settings.
   Tests drive `ConversationButton`, `NavCompanion`, the Companion buttons by
   automation ID (`OpenSetup`, `OpenAudioSetup`) and `ActionText`.
4. `HostsWindow` becomes the wizard, with the connection check and firewall
   helper exposed for the map and the host dashboard.
5. `LiveConversationWindow` is only the conversation: history bubbles, the
   message box and the Listening, Vision and Stop toggles (section 6). Its
   choices live in `TalkPreferences` (`talk-preferences.json`), edited on the
   Companion Listening, Voice and Vision pages (`MainWindow.Talk.cs`).
6. `SetupWindow` gets the stepper style.
