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
7. **Settings save themselves.** A setting is saved as soon as it changes:
   picks and toggles at once, typing a moment after it stops (a name or path
   that is shared or used elsewhere when you leave the field or press Enter),
   and anything still waiting when its window closes. There is no *Save* or
   *Apply* button for a setting. Editors with many fields (Personality,
   Character, Lorebooks) keep one footer line saying *All changes saved.*,
   *Saving...* or *Not saved yet:* with the reason (an empty name, a model file
   that doesn't exist); a change that can't be saved never blocks typing, and
   closing asks only then. Every save goes into the newest saved copy of its
   file and replaces only its own part, so a change synced in from your other
   computers (who does what, the lip-sync host, voices) is never overwritten by
   an open editor, and a saved change to a shared list syncs out on its own.
   Only commitments stay explicit buttons, named for what they do: choosing a
   paid provider with its key and consent (*Use OpenRouter*, *Use as
   fallback*), connecting with a token (*Connect with token*, *Reconnect*),
   starting programs (*Save and start* for MCP servers), adding or updating a
   remembered fact (*Add fact*), and the legacy Setup window's checkpoints.

## Stages and surfaces

```text
first launch ──> Welcome tour ──┬── "Talk with my companion here" ──> Home (what needs attention ─> ready)
                                └── "Lend this PC to Martlet"     ──> Host dashboard (host service)
Every mode: Home · Devices (map) · Companion* · Creations · Background tasks · Diagnostics · Settings      (* companion mode only)
```

Each page answers one question, so no two pages do the same thing:

| Page | Question | Changes settings? |
| --- | --- | --- |
| **Home** | Is anything wrong or missing, and can I talk to it? | No. It shows problems with their fixes and links to where each thing changes. |
| **Companion** | How does Martlet think, sound, listen, look and remember? | Yes: the one place each of those choices is made. |
| **Devices** | Which computer does what? | Machines and handing jobs between them. |
| **Background tasks** | What is Martlet working on, and how did it go? | No. It lists long steps (setup, updates, pairing, downloads) running or finished this session, shows their window again and cancels them after asking. |
| **Settings** | How does the app itself behave? | Appearance, this PC's role, updates, tools. |

### 1. Welcome tour (first launch)

This full-window overlay appears when no device-role choice has been saved
(`device-role.txt`). The installer asks no setup questions, so this is where
setup starts. It has up to three short cards, animated between steps and marked
with step dots:

1. **Hi, I'm Martlet.** One sentence about what Martlet does, plus *Let's begin*.
   *Skip for now* leaves the tour and uses companion mode.
2. **What's this computer for?** Two large choice cards:
   - *Talk with my companion here* (the PC you sit at).
   - *Lend this PC to Martlet* (a spare or gaming PC with a GPU that runs heavy
     parts, such as lip-sync, for another PC).
3. **How would you like to start?** (companion mode) *Set it all up for me
   (recommended)* (on a PC not paired with other computers) reads this PC's
   graphics card and, after one confirmation, sets up Thinking (the smallest
   local model that hears, Gemma 4 E2B, in Ollama), Voice (a voice engine on the
   NVIDIA card when it has room beside Thinking, otherwise a Windows voice) and
   Listening (the default microphone with Parakeet on the processor, or Whisper
   on the card when room is left after the voice); its line under the title
   shows the plan. *Recommend a setup for me*
   opens the setup advisor (its plan adds *Install on this PC* for what it runs
   here), and *I know what I want* opens Companion › *Thinking*. In host mode,
   the tour ends on the host dashboard.

The tour saves only the device role; it contacts nothing and installs nothing
until you choose *Set it all up for me* and confirm.
Each setup installs what it needs (Thinking's *This PC* installs Ollama, the
advisor's plan installs its items), and **Prerequisites** under Settings › Tools
installs any item by hand. A tick list of single
prerequisites was removed because it set up only a fragment of any plan.
Settings > *This PC's role* changes the role or replays the tour.

### 2. Home (main PC)

Home answers "is anything wrong or missing, and can I talk to it?" It is where
you find what isn't set up, what stopped working and what to do about it, and
it offers *Start talking*. It changes no settings itself: each fix opens the
place where that thing changes (usually a Companion page) or runs the one step
it names (*Start Ollama*, *Check gpu-1 now*, *Install WebView2*). Everything
on it comes from what this PC already knows (saved settings, the last host
checks, Windows' device and app lists, the local log and this PC's own
loopback services such as Ollama and Audio2Face); Home contacts no other
computer or provider by itself.

The hero card says how Martlet is doing overall:

| State | Headline | Primary action |
| --- | --- | --- |
| Nothing saved yet | "Let's bring your companion to life" | *Set it all up for me* (no other computer paired; otherwise *Set up thinking*, which opens Companion › *Thinking*) |
| A problem stops replies | The problem in one line, such as "Martlet can't reply right now" or "Martlet can't read its settings" | That problem's first fix |
| Replies work, warnings remain | "Ready, with 2 things to look at" | **Start talking** |
| Everything checked works | "Ready when you are" (with a time-of-day greeting) | **Start talking** |

*Show character* sits next to the primary action at every stage; *Not sure
what you need? Get a recommendation* (the advisor) shows while thinking isn't
set up. On a new PC (nothing saved yet and no other computer paired), *Connect
to your other computers* sits next to *Set up thinking* and in that item's
fixes: it opens *Add a computer* on *Martlet on your network*, so a PC that
joins your other computers uses your hosts and the setup they share without
setting anything up itself. Pairing never waits for Setup: hosts are paired,
and who does what is followed, before this PC saves any settings.

Below the hero, **Needs attention** (*All good* when nothing does) lists every
item with what it means and its fixes, most serious first:

- **Problems** stop Martlet replying: the data folder or settings can't be
  used, thinking isn't set up, its model was retired, its Ollama on this PC
  isn't installed, running or doesn't have the model, its host isn't
  answering, or Docker Desktop is stopped while this PC's host service thinks.
- **Warnings** mean something chosen doesn't work, or works in a reduced way:
  a job's host, key, consent or route (the [coverage
  rules](PLATFORMS.md#what-the-app-guarantees)), the last reply or
  transcription failing, a missing or Windows-blocked microphone, unplugged
  speakers, the WebView2 runtime the character needs, vision turned on with a
  text-only model or no camera, a paired computer not answering or on an
  older Martlet, a tool server that failed, an update that didn't install, or
  unexpected errors since Martlet started.
- **Good to know** (muted, below): optional jobs not set up yet (listening,
  voice), lifelike lip-sync on an NVIDIA PC that runs no Audio2Face, a new
  Martlet version, and Martlet closing unexpectedly last time. Errors and the
  crash note can be dismissed for this session.

*Check again* re-reads settings, devices and this PC's own services (it
contacts no other computer). Items also refresh by themselves after a check,
a save, a failed request or an error.

**Health** closes the page: one tile per part Martlet checks (Thinking,
Listening, Voice, Lip-sync, Microphone, Speakers, Character, Devices, Tools
when servers are set up, Updates and This app), each with a status dot (green
working, amber needs attention, grey not set up or not checked) and its state
in a few words. A tile opens the page where that part changes. The old *Now*
lines and *Your devices* chips are gone: what each job uses is on its tile,
and the Devices page is one click away in the rail.

### 3. Devices (hardware map)

This page answers "what do I have, and what is each machine doing?" From top
to bottom: the **map**, the **selected device** with what it does, and
**Settings for all devices**.

```text
   [NVIDIA Build] ~~.                   .~~ [gpu-pc-host]
                     >~~ [ This PC ] ~~<
 [Add a computer] ~~'                   '~~ [laptop]
```

- **The map** fills the top of the page. *This PC* sits in the middle; cloud
  services (and a ghost *Conversation model* node while no thinking model is
  chosen, pointing to Companion › *Thinking*) sit on its left, and your paired
  Martlet hosts and self-hosted gateways on its right, each column on a gentle
  arc. The ghost *Add a computer* node joins the side with fewer devices, and
  This PC moves toward an empty side so the map stays balanced. Soft curves
  join each device to This PC, edge to edge; animated dashes show which way
  data flows and turn amber for a device that needs attention. Every paired
  host appears, whether or not it has a job yet. The map grows taller when a
  side holds many devices, and a narrow window scales the whole map down
  instead of squeezing devices together.
- **Cards** show an icon, name, address, up to three role chips ("Thinks",
  "Listens", "Speaks", "Lip-sync", "Character", "+2" for more) and a status
  dot. Ready nodes pulse. Selecting a card highlights it and shows its
  details below (scrolling just enough on a short window).
- **What isn't working** (companion mode) sits under the map when a job is
  down or its host hasn't been checked since Martlet started: one line per
  job with its one-click fixes and **Show**, which selects the device doing
  that job.
- **The selected device** opens with its name, address and status (plus
  *Check connection* for a host) and these parts:
  - **What it does**: one row per job or part it runs, each with an icon, its
    model or voice and the controls that change it right there. *Thinking*,
    *Listening*, *Speaking* and *Lip-sync* rows have **Done by** once a host is
    paired: thinking, listening and speaking go to the Setup choice or any
    paired host (with its model, or "not installed"); handing a job to a host
    checks it, offers to install the role (Ollama, whisper or F5) there and
    switches over once it is ready, and the Setup choice comes back without
    re-entering a key. **Lip-sync switches on the spot**: *This PC*, any paired
    host, or *Nobody (mouth follows voice loudness)*; a showing character
    keeps showing and the next sentence uses the new computer. Handing
    lip-sync to a host first checks it over its pinned pairing; if it does not
    run Audio2Face yet, Martlet offers to install it there in the same step and
    keeps the mouth on voice loudness until the host is ready. The host in
    charge goes first; this PC's own Audio2Face service and voice loudness are
    the fallbacks. A host that can't run an engine at all (F5 without a 6 GB
    NVIDIA GPU, Ollama on an iPhone) appears in *Done by* as *can't take it
    now* with the reason. Each job row has *Change in Companion* (lip-sync's
    opens *Lip-sync*; speaking's model and voice change there, because they
    need consent), shows the job's problem in amber when it isn't working, and
    adds *Change ... settings* (the host role's model, GPU or CPU and graphics
    card, in the role's dialog starting from what it runs now) and *Remove ...
    from it* when a host runs it. *Character* (show or hide,
    settings), *Microphone and speakers* (*Choose and test*) and this PC's
    *Martlet host service* (check, update, status, pair again, forget) have
    their own rows; a role installed on a host but doing no job yet shows as
    *standing by* with *Hand ... to this computer*, *Change ... settings* and *Remove*.
  - **Give it more to do**: hand the device a job it doesn't do yet (*Hand
    thinking to this computer*...), *Run host services on this PC* or *Take
    lip-sync back to this PC*. *Install or remove roles* (collapsed) holds a
    host's install and remove commands; an impossible role is explained in
    *Details* instead. Phones and tablets have no install, remove, update or
    SSH commands; their roles are switched on on the device.
  - **Manage**: *Update host* (primary *Update it to Martlet x.y.z* when it is
    older than this PC), *Show its status*, *Prepare this computer*, *Wake it
    up*, *Restart it*, *Shut it down*, *Pair again or change its setup* and
    *Forget this host*; *Prerequisites* for This PC. Install, remove, update
    and status run the same `martlet-host` engine on that computer the way
    Martlet reaches it (SSH with Docker, SSH native Linux, or this PC's Docker
    Desktop), in a Martlet run window with live output, *Hide* and *Cancel task*
    (never a console window; see
    [Run windows and Background tasks](#run-windows-and-background-tasks)); the
    owner's click is the confirmation. On this PC, roles
    with a GPU-or-CPU choice preselect the suggestion from the graphics card's
    free memory. The desktop never gets a shell, Docker socket or admin rights
    on the host.
  - **Hardware and details** (collapsed): for This PC, read locally from the
    registry and Windows: CPU, threads, memory, each GPU with VRAM, Windows
    version, LAN address and whether Docker Desktop is installed or running,
    with capability hints such as "NVIDIA 4 GB+: can run Audio2Face lip-sync".
    For a host: address, pinned TLS identity, this PC's device ID, how Martlet
    reaches it, the Martlet version its gateway reported on *Check connection*
    (compared with this PC's) and the hardware it reported. For a cloud
    service: address, key storage, data sent and a "may cost money" note.
  - **How Martlet reaches it** (hosts, open while no route is set) sets the
    SSH or Docker Desktop route; without one, Martlet copies the command to
    run on the host.
- **Settings for all devices** (companion mode) closes the page: **Check all
  hosts** reads every host's roles (explicit only), and **Keep Martlet the same
  on all my computers** (ON by default; unticking it saves `off`) makes Martlet
  one app on all your computers through every paired host ([details](CLUSTER.md)):
  who does what, the settings and API keys, memories, people, speaking voices,
  characters and Home Assistant. Every 15 seconds it checks the hosts, follows
  changes made elsewhere and pushes changes made here. Status lines say how many
  hosts hold the current plan and which need an update, how many settings are
  the same on how many hosts, and how many facts Martlet remembers on how many
  hosts. While it is on, each job row offers **Fail over
  to another host**: when its host stops answering for about 30 seconds the
  job moves to another paired host that runs the same engine, and the row
  says where it moved from or why this PC cannot follow the plan (host not
  paired here, no voice chosen). Jobs nobody does yet are listed here with
  *Set up in Companion*.

Reading hardware is local only. It opens no port, starts no process and makes
no network request.

### 4. Host dashboard (host service UI)

When the device role is *host*, Home becomes the dashboard for the Martlet host
service on this PC:

- **Hero**: "This PC is a Martlet host", with a status pill and the address
  desktops use (the one the host service publishes, `https://<LAN IP>:9443`).
- **Five steps** that tick by themselves: the dashboard reads this PC's host
  service from Docker (`LocalHostService`: the gateway container, its published
  port, its role records and the Martlet network roster it accepted) when it
  opens, every 30 seconds while the window shows and when it shows again. A
  done step shows what it found and no setup button.
  1. *Docker Desktop*: installed or running, read locally (and whether its
     engine answers). Installs it with winget in a run window.
  2. *Host service*: done when the gateway runs and answers at an address this
     PC still has. Otherwise *Set up host service*, *Start host service* (set
     up but stopped) or *Set up again* (this PC's address changed), including
     the one-time firewall prompt.
  3. *Pair a desktop*: done when a computer is in the host's Martlet network
     (named, for example *Paired with DIVA and this PC*). The main PC finds
     this PC under Add a computer › *Martlet on your network*; *Allow* here
     when both show the same check number. *Show a pairing code* (*Pair
     another computer* once paired) still shows this PC's address and a short
     one-use code in large type in a run window, to type on the main PC. When
     Windows Firewall keeps other computers from finding this PC, *Let my
     other computers find this PC* adds the rule (one administrator prompt).
  4. *Roles*: lists the installed roles, with *Add* for each other role and
     *Remove* for each installed one.
  5. *Keep it up to date*: rebuilds the host service from this app's version
     (`martlet-host update`); done when its gateway image matches the app.
     Martlet does this by itself in the background after it updates (the
     step then says it is updating); *Update host service* does it now.
- The steps' heading reads *This host is ready* once Docker Desktop, the host
  service and pairing are done, and the line under it says what is left.
- **Check again** repeats the read at once and says what it found.
- *Use this PC as a companion instead* switches the device role.

### 5. Add a computer (Martlet hosts wizard)

The long form became a two-step wizard with a step rail: connect a host, then
choose its jobs.

1. **Connect**: first a *Martlet on your network* card. Opening the
   wizard sends Martlet's discovery query and lists the owner's other
   computers that can share a host this PC isn't paired with yet (*GAMING-PC
   (192.168.1.31): gaming-pc-host · Martlet 0.17.0* with *Connect*), plus
   *Find again*. *Connect* shows a large check number and *Stop asking*; the
   other computer asks *Allow* or *Deny* with the same number, then sends a
   one-use code for each host and this PC pairs with them by itself
   ([how](ARCHITECTURE.md#finding-your-other-computers)). When a host isn't
   listed (guest Wi-Fi or client isolation, a VPN, another subnet, a firewall
   blocking UDP/TCP 9444, or discovery turned off there), *Enter a pairing
   code* shows the host's address and short code fields and *Pair with host*;
   they open by themselves when nobody answers. Below it, *Or set up a new
   host*: *This PC* (*Set up this PC*: Docker Desktop, installed on request,
   firewall, setup and pairing in one run window) and *A Linux computer over
   SSH* (one SSH target and *Set up over SSH*: connect, then run the host in
   Docker when that account can use Docker, directly or with sudo, otherwise
   natively on any Linux with systemd; set up, pair and read the machine report in a run
   window). Its private address is filled in from the computer and sits in a
   collapsed expander. Another Windows PC needs no SSH: Martlet there sets
   itself up (*Use as a Martlet host*) and then appears in the network list.
   Any successful connection moves on to Roles. Pairing adds the host to
   `hosts.json` (every paired host and how Martlet reaches it; nonsecret,
   secrets stay in Windows Credential Manager) and hands it no job: it stands
   by until you hand it a job here or from a job row's *Done by* on the
   Devices page.
2. **Roles**: which paired host (a picker when there are several), *Check*,
   *Update* and *Forget* for it, its role cards with *Add* and *Remove* (run
   over SSH, on this PC, or through Martlet on that computer), and this PC's
   device ID in a collapsed expander.

### 6. Talk (conversation)

*Start talking* opens the conversation and nothing else: its history, what you
said and the message box. It is a separate window beside Martlet, never a
blocking dialog: Home, Companion and Settings stay usable while it is open,
and Home's button reads *Show conversation* and brings it to the front. Every
choice about how Martlet listens, speaks and sees is made in Companion, and an
open talk window follows a change there right away; the window has no
settings, approvals, cost envelopes, timelines or links to other windows.

- **Header**: the mascot, *Martlet* and one status line (*Listening. Just
  talk, or type below.*, *Martlet is thinking*, *Martlet is speaking. Esc
  stops it.*, or what went wrong in plain words). On the right: **Start
  listening** (shown with always listening; a primary button until pressed,
  then **Stop listening** with a green dot, an amber dot when the microphone
  can't be opened, or *Can't listen* with the reason while listening isn't set
  up), **Start watching** (shown when vision is on; then **Stop watching** with
  a green dot that twinkles while a look is with the model, or *Can't see* with
  the reason), then **Stop (Esc)**.
- **History**: chat bubbles for the whole conversation while the window is
  open: what you typed, what you said (the transcript, captioned *You
  (spoken)*), Martlet's replies as they stream in, its remarks about your
  screen, and short notes under a reply (*Remembered*, *Cut short*, a
  refusal). It follows new messages unless you scroll up to read.
- **Message box**: always in view. Enter sends, Shift+Enter starts a new line.
  With push-to-talk chosen, *Hold to talk* (hold the mouse or Space) sits next
  to *Send*; invoking it starts a recording and invoking it again sends.

**Always listening** (the default, with the chosen or Windows
default microphone; no test needed) starts only when you press *Start
listening*, and **vision** (on by default, looking at your whole screen; Companion
turns it off) only
when you press *Start watching*: each has its own button here, on Home (beside
a listening and a watching indicator) and in the notification-area menu, and
neither starts or stops the other. Typing while Martlet listens hands the microphone over for the typed
message, and listening resumes after the reply. **Stop (Esc)** stops the reply,
any recording and watching at once and keeps the conversation; listening carries
on (only *Stop listening* ends it). Locking Windows stops listening and watching
and starts a fresh conversation (both resume on unlock), and closing the
window ends it unless Martlet is listening or watching, which only hides it.

### 7. Companion and Settings pages

- **Companion** answers "how does Martlet think, sound, listen, look and
  remember?" It is the one place each of those choices is made, as pages in a
  side list grouped by what they decide. Home's fixes and Health tiles, the
  Devices tiles and nodes, fix cards, the tour and the advisor all open the matching page
  (Companion opens on the last page used, *Thinking* at first):

  - **How it works** (where each job runs):
    1. *Thinking*: where the conversation model runs, the provider, the model
       and its API key.
    2. *Voice*: where the voice runs and the voice itself, then the speakers,
       then *Speak Martlet's replies aloud* (on by default).
    3. *Listening*: the speech-to-text provider, model and key, then the
       microphone, then **How you talk**: *Always listening* (the default;
       once you press *Start listening* in the talk window Martlet hears you
       until *Stop listening*, with sensitivity
       and how long a pause ends your turn) or *Push-to-talk*, and Voice ID
       (*Only respond to my voice* and *Set up Voice ID*).
    4. *Vision*: whether Martlet may look at your screen or a camera when you
       press *Start watching* (on by default, looking at your whole screen):
       what it looks at (whole screen, active
       window, a camera found with *Find cameras*, a phone or
       network camera address, or a Home Assistant camera once Smart home is
       connected), how chatty it is, what is captured and where it is sent,
       and *Turn vision off* / *Turn vision on* (which only allows it; *Start
       watching* starts looking).
    5. *Lip-sync*: who moves the character's mouth, and where it runs.
  - **Who it is**:
    6. *Character*: what it looks like now, then the character model (show,
       hide, choose and customize, reset). The character window has one
       *Show character*/*Hide character (Esc)* button at the top and saves
       each choice on its own; a showing character switches at once.
    7. *Personality*: the active persona and its style mix, *Edit
       personality*, and *Import a character card*. The Personality window
       makes the persona chosen in its list the one Martlet uses and saves
       every edit on its own (no *Apply*, *Save* or *Reload*).
    8. *Lorebook*: how many lorebooks are on for the active persona, each
       lorebook with *Turn on/off*, *Edit lorebooks* and *Import a lorebook*
       (see [Lorebooks](LOREBOOKS.md)).
    9. *Memory*: whether memory is on, and *Manage memory* for its facts.
  - **What it does**: how it answers and acts: *Replies* (generation
    settings, and *Thinking steps*: whether a reasoning model thinks before it
    answers, Off by default), *Tools* (the MCP servers Martlet may call while you talk, whether
    each runs without asking, and recent tool use) and *Smart home*.

  A page gets its own entry only if it has its own **Where it runs** choice,
  its own consent or data destination, or its own list to edit; anything else
  is a card on an existing page.

  Every page starts with **Now**: what it uses and any problem stopping it.
  Cards appear only when they apply to the chosen place: Voices (the F5 voice
  list) shows wherever F5 can speak (this PC or another of your computers),
  never for a cloud provider; a one-provider cloud card names the
  provider instead of offering a one-item list; Ollama's download and check
  buttons appear once Ollama is installed.

  Each job tab (1-3) then asks **Where it runs**, defaulting to *This PC
  (recommended)*:

  - *This PC*: thinking uses Ollama at `http://127.0.0.1:11434/v1` (*Install
    Ollama and use it* installs Ollama with the suggested model sized to the
    graphics card, switches to it and tests it; the tab reads which models
    Ollama already has when it opens (loopback only) and *Check Ollama* reads it
    again; *Download model* shows Ollama's progress in a run window, *Test model*
    loads the chosen model and asks it for a short streamed reply the way replies
    do, in a run window, and shows the result under the buttons; *Use Ollama on
    this PC* gets the model ready first in a run window, downloading it after one
    confirmation when Ollama doesn't have it and then loading it, and switches
    Thinking only once it is loaded, so the current Thinking answers until then
    and the first reply doesn't wait; a model that can't download or load leaves
    Thinking unchanged). Voice offers two one-click choices, the one in use (or the one this
    PC's hardware suits) first: **F5 voice, with Docker** (*Set up F5 with
    Docker* sets up and pairs Martlet's host service on this PC, so this PC
    also becomes one of your hosts, installs F5 and switches over with the first
    of Martlet's starter voices, a cute, high-pitched one) or **Windows voice, no Docker** (*Use a Windows
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
  cards, with two places: *This PC* and *Another of your computers* (there is
  no cloud provider). Voice loudness is worked out on this PC, so it is one of
  *This PC*'s ways rather than a place. The recommended place is *This PC*
  unless this PC lacks an NVIDIA graphics card of 4 GB or more and another
  computer can run Audio2Face. Each card's button commits the choice, which
  switches right away, even while the character talks:

  - *This PC*: two ways, the one in use first, like the voice. **Audio2Face,
    with Docker** (*Set up Audio2Face with Docker* sets up and pairs Martlet's
    host service on this PC, then hands lip-sync to it, installing Audio2Face
    with its open-source engine, no key; once the host service exists, *Use Audio2Face on this
    PC* and *Check it*; recommended with an NVIDIA graphics card of 4 GB or
    more) or **Voice loudness, no setup** (*Use voice loudness* turns
    Audio2Face off; recommended otherwise). Below them, an advanced **Your own
    Audio2Face service** line covers an Audio2Face service you run yourself at
    the character's loopback endpoint (*Use my own service*). That is
    Martlet's default: it only looks for a service there before each sentence,
    so voice loudness is marked *in use* and the line says *not running* when
    nothing answers; only when one answers (or Audio2Face-only is activated)
    does it become a full option marked *in use*. It never implies Audio2Face
    is installed.
  - *Another of your computers*: the same host list as the job tabs, with *Use
    it*, *Add a computer*, *Check hosts* and the Devices map.

  *Advanced setup* at the bottom of each job tab opens the full Setup window on
  that job, for every route type and stored or detached keys. Microphone and
  speakers, character customization, personality and memory facts still open
  their own windows from their tabs; the F5 voices are listed inline on Voice.

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
- **Settings**: palette (Martlet's own or one made from the character, see
  [Character palettes](#character-palettes)), this PC's role and the tour, app updates (automatic
  checks and their interval, automatic installs, keeping hosts on this PC's
  version, *Check for updates now*, *Install*, *Update hosts now*, and a line
  on this PC's own host service, which always follows this app's version in
  the background), *Your
  other computers* (whether they may send this PC commands, and **Let my
  other computers find this PC and ask to use its hosts**: ON by default,
  unticking it saves `off` in `nearby.txt`; when this PC runs a host or
  reaches one over SSH it answers *Martlet on your network*, and its status
  line names the hosts it offers, the last request, and when Windows Firewall
  or a Public network keeps other computers out, with *Let my other computers
  reach this PC*, one administrator prompt), tools
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

### Run windows and Background tasks

Long steps (setting up, updating or pairing a computer, adding or removing a
role, downloads, Docker Desktop and Windows features) run in a *run window*:
live output, a status line, *Hide* and *Cancel task...*. *Hide*, Esc and the
window's close button only hide it while it runs, so a stray click never stops
the work; *Cancel task...* sits apart on the left and always asks first (*Keep
running* is the default). A question the run asks (a password, a role's
choices) brings its window back with it, and a run outlives the window that
started it. Once it ends, *Hide* becomes *Close*; a run that ends while hidden
closes its window.

**Background tasks** in the navigation rail (its count shows how many run now)
lists every run since Martlet started, newest first, with how long it has run
and its status, or when and how it ended. *Show* brings a running task's window
back, *Show output* reopens a finished task's output (kept in memory, pairing
codes hidden), *Cancel...* asks the same question as *Cancel task...*, and
*Clear finished* drops the finished ones. Every run's output also stays in
`host-runs.log` (Diagnostics). Exiting Martlet while a task runs names it
(*<run> (in Background tasks)*) and asks first.

## Character palettes

Every window, menu and the character overlay are drawn with twelve brushes
(`AppearancePalette.cs`): `Canvas` (window background), `Surface` (cards),
`Soft` (buttons, chips, inputs), `Text`, `Muted`, `Border`, `Accent` (primary
buttons, selection, links, icons, headings; also used as text), `OnAccent`,
`Focus` (focus and hover rings), `Success`, `Warning` and `Glow` (the halos
behind the mascot), plus the mascot itself, whose badge takes the accent's hue
in a character palette (Martlet's pink otherwise). Everything refers to them as
`DynamicResource`, so a palette swaps at once.
The character's speech bubble is drawn wholly in them too, like a piece of
Martlet's window floating beside the character: a `Surface` card with an
`Accent` outline, `Text` in Martlet's typeface (Segoe UI) and a soft `Glow` halo
(no halo in high contrast), in Martlet's own palettes and the character ones
alike.
Windows' high contrast always wins.

Settings › Appearance offers *Pink light*, *Rose dark* and two palettes made by
Martlet's rules from the character this PC shows: **Character light** and
**Character dark**. They follow the character: choosing or showing another
character recolors Martlet within a moment, and the last colors are kept in
`appearance-colors.json` so Martlet starts in them. Under the choice the page
shows the character's main colors and a small picture of Martlet's window in
each of the two palettes. (The *by Thinking* palettes an older Martlet offered
were retired: they looked the same as the rules' own. A saved or shared choice
of one reads as the matching rule-based palette.)

**Reading the colors** (`Martlet.Avatar.Hosting`, `CharacterTheme.cs`): the
model's textures (a Live2D model's texture sheets, a VRM's base color
textures) are decoded small (WIC), nearly opaque pixels are grouped with
deterministic k-means in OKLab into up to seven main colors, and small vivid
touches (eyes, trims, ribbons) are clustered separately and kept as up to three
more. Each color has its share of the textures and a kind: vivid, muted,
neutral or skin. They are read once per model and kept in
`character-themes.json` by the model's ID. Texture sheets over-represent some
parts (mouths, eyes, effects), so the rules can pick a color that is small on
the character itself.

**Martlet's rules** (in OKLCH): the backgrounds are near white (light) or
near black (dark), tinted with the hue that covers most of the model, more
faintly when that hue covers little of it; a dark canvas is the model's own
darkest color when it shares that hue. Text reuses the model's darkest (light)
or lightest (dark) neutral when it has one. The accent is one of the model's
most vivid colors (pale tints and specks never): the one that suits both
palettes best, unless it would have to change a lot for one of them (a yellow
turning olive on white), which then takes the candidate that suits it. A
model's color is used as it is whenever it already keeps the rules; otherwise
only its lightness moves. Glow is a second vivid color (or the tint), success
and warning keep their green and amber, moved off the accent's hue when close.

**Rules every palette keeps** (`CharacterThemeRules.Repair`): light backgrounds with dark text in a light
palette and dark backgrounds with light text in a dark one; text 7:1 on
`Canvas`, `Surface` and `Soft`; `Muted`, `Accent`, `Success` and `Warning`
4.5:1 on them; `OnAccent` 4.5:1 on `Accent`; `Border` and `Focus` 3:1 on
`Surface` and `Canvas` (WCAG 2); and body text keeps only a hint of color
(OKLCH chroma at most 0.05 for `Text`, 0.08 for `Muted`). A color that breaks
one is moved in lightness (or chroma, for text) only, keeping its hue, and the
page says how many were adjusted.

Verified looks (2026-10-03, `character_theme` previews and the desktop): the
rules give calm, readable palettes for all ten models tried (the Live2D samples
Hiyori, Haru, Mao, Mark, Natori, Ren and Rice, the VRM samples Seed-san and
Constraint Twist, and a VTube Studio model).

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
