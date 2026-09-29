# Martlet desktop UI design

Feedback on the first screen was clear: it was overwhelming, and people could
not tell what to do next. The old home window stacked about twenty buttons, a
fixture demo, update checks and three diagnostic text boxes on one scrolling
page. This document describes the replacement: one job per screen, a single
obvious next step at every stage, and a living map of the user's computers.

## Principles

1. **One screen, one job.** Each stage shows one primary action. Everything
   else moves behind navigation or a *Details* expander.
2. **Say what to do, in plain words.** Headlines say where you are ("Almost
   there"), and the button says what happens next ("Choose how Martlet thinks").
   Exact legal, cost and data wording stays available, one click away, and is
   never removed.
3. **The companion is the hero.** An animated Martlet mark greets you. The Live2D
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
first launch ──> Welcome tour ──┬── "Talk with my companion here" ──> Companion home (setup checklist ─> ready)
                                └── "Lend this PC to Martlet"     ──> Host dashboard (host service)
Every mode: Home · Devices (map) · Companion* · Settings      (* companion mode only)
```

### 1. Welcome tour (first launch)

This full-window overlay appears when no device-role choice has been saved
(`device-role.txt`). It has three short cards, animated between steps and marked
with step dots:

1. **Hi, I'm Martlet.** One sentence about what Martlet does, plus *Let's begin*.
   *Skip for now* leaves the tour and uses companion mode.
2. **What's this computer for?** Two large choice cards:
   - *Talk with my companion here* (the PC you sit at).
   - *Lend this PC to Martlet* (a spare or gaming PC with a GPU that runs heavy
     parts, such as lip-sync, for another PC).
3. **How would you like to start?** (companion mode) *Recommend a setup for me*
   opens the setup advisor, *I know what I want* opens Setup, and *Try the
   offline demo* opens the demo. In host mode, the tour ends on the host
   dashboard.

The tour saves only the device role. It runs nothing and contacts nothing.
Settings > *This PC's role* changes the role or replays the tour.

### 2. Companion home (main PC)

The hero card changes with setup progress:

| Stage | Headline | Primary action |
| --- | --- | --- |
| Nothing saved yet | "Let's bring your companion to life" | *Choose how Martlet thinks* (Setup) |
| Conversation model not chosen, or chosen without destination consent | "Almost there" | *Finish setup* |
| Ready (conversation model chosen, consented and on) | "Ready when you are" (with a time-of-day greeting) | **Start talking** |

*Show character* sits next to the primary action at every stage. *Not sure?
Get a recommendation* opens the advisor.

Below the hero, **Your setup** shows a progress bar and five steps. Only the
first is required:

1. *How Martlet thinks* (conversation model and route) opens Setup.
2. *Its voice and ears* (speech-to-text and text-to-speech; optional) opens Setup.
3. *Microphone and speakers* (optional) opens Audio setup.
4. *Character* (optional) opens Character settings.
5. *More computers* (optional) opens the Devices map or the Add a computer wizard.

Each row shows a done or to-do mark and one action. Completed marks pop in.

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
  model* node points to Setup. Every paired host appears, whether or not it has
  a job yet.
- **Who does what** (above the map, companion mode) has one tile per job:
  *Thinking*, *Listening*, *Speaking* and *Lip-sync (Audio2Face)*. Each tile
  names the computer or service in charge (select it to show that node).
  Thinking, listening and speaking change in Setup, because each needs a model
  and consent. **Lip-sync switches on the spot** from a drop-down: *This PC*,
  any paired host, or *Nobody (mouth follows voice loudness)*. A showing
  character keeps showing; the next sentence uses the new computer.
  Handing lip-sync to a host first checks it over its pinned pairing. If it
  does not run Audio2Face yet, Martlet offers to install it there in the same
  step and keeps the mouth on voice loudness until the host is ready. The host
  in charge goes first; this PC's own Audio2Face service and voice loudness
  are the fallbacks. *Check hosts* reads every host's roles (explicit only).
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
    there* and *Remove Audio2Face from it*, its status console, *Pair again*
    and *Forget this host*. Install, remove and status run the same
    `martlet-host` engine on that computer the way Martlet reaches it (SSH with
    Docker, SSH native Ubuntu, or this PC's Docker Desktop), in a console where
    the host owner confirms each change; the desktop never gets a shell,
    Docker socket or admin rights on the host. *How Martlet reaches it* sets
    that route; without one, Martlet copies the command to run on the host.
  - *Cloud*: address, key storage, data sent and a "may cost money" note.
  - *Actions* for the node: Audio setup, Character, Prerequisites, *Host
    services on this PC*, *Change in Setup*, *Manage host* and similar.

Reading hardware is local only. It opens no port, starts no process and makes
no network request.

### 4. Host dashboard (host service UI)

When the device role is *host*, Home becomes the dashboard for the Martlet host
service on this PC:

- **Hero**: "This PC is a Martlet host", with a status pill and the address
  desktops use (`https://<LAN IP>:9443`).
- **Four steps**, each with one button:
  1. *Docker Desktop*: installed or running, read locally. Opens the install
     console.
  2. *Host service*: set up the gateway, including the one-time firewall prompt.
  3. *Pair a desktop*: opens the host's pairing console, which shows the
     one-use code.
  4. *Roles*: add or remove Audio2Face lip-sync, and show host status.
- **Check host service** is an explicit TCP reachability probe of this PC's
  host port. It never runs automatically.
- *Use this PC as a companion instead* switches the device role.

### 5. Add a computer (Martlet hosts wizard)

The long form became a four-step wizard with a step rail:

1. **Where it runs**: large cards for *This PC (Docker Desktop)*, *Another
   computer over SSH (Docker)*, *Another computer over SSH (Ubuntu, native)*
   and *I'll type the commands myself*.
2. **Install**: only the fields that method needs, *Set up host*, and the exact
   command in a *Show the command* expander.
3. **Pair**: three numbered mini-steps, the device ID with *Copy*, *Open
   pairing console*, the pasted code and *Pair*, plus *Check* and *Forget*.
   Pairing adds the host to `hosts.json` (every paired host and how Martlet
   reaches it; nonsecret, secrets stay in Windows Credential Manager). The
   first host paired takes over lip-sync; later hosts stand by until you hand
   them a job under *Who does what*.
4. **Roles**: role cards (Audio2Face today; planned roles shown as coming soon),
   with *Add*, *Remove* and *Host status*.

### 6. Talk (conversation)

The conversation window puts the chat first:

- **Header**: companion mark, title, and **Stop (Esc)** always visible. Links
  to Reload, Setup, Audio, Character and Troubleshooting.
- **Conversation (left)**: the result line, a *You said* bubble for the
  push-to-talk transcript, the reply bubble, a separate refusal bubble, and a
  *Details* expander with the configuration, supported IDs and stage timeline.
- **Your next message (right)**: *Before your next message* scrolls: the voice
  toggle, the exact data, cost and output envelope, the per-action approvals,
  and Pause and Mute. Below it, the composer is always in view: the message
  box, *Send*, *Hold to talk* (Space) and *Finish and send*.

### 7. Companion and Settings pages

- **Companion** (make it yours): *How it thinks* (Setup), *Microphone and
  speakers*, *Character*, *Personality*, *Voice Library* and *Memory*, one card
  each with one line of explanation.
- **Settings**: palette, this PC's role and the tour, app updates, the offline
  demo (fixture), tools (Troubleshooting, Backup and restore, Prerequisites,
  Martlet hosts), and *Diagnostics* (pipeline, status details, local audio
  evidence, refresh and stop, create profile). Exit is also here.

### 8. Setup (configuration)

Setup keeps its four checkpoints, now drawn as a numbered stepper with a
connecting line, and *Next* is the primary button. Its consent and credential
behavior did not change.

## Motion system

| Motion | Where | Spec |
| --- | --- | --- |
| Page enter | nav changes, tour steps, wizard steps, Setup steps | opacity 0 to 1 and Y +14 to 0, 260 ms, cubic ease-out |
| Float | hero mark | Y ±5, 3.2 s, sine, forever |
| Twinkle | sparkles | opacity 0.35 to 1, 1.6 s, staggered |
| Heartbeat | mark heart | scale 1 to 1.08 to 1, 1.8 s |
| Progress fill | setup progress | width, 600 ms, cubic ease-out |
| Check pop | completed step | scale 0.4 to 1, 320 ms, back ease |
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
   host dashboard, devices map with detail panel, Companion and Settings pages.
   Every existing handler and named control is kept (tests use
   `ConversationButton`, `SetupButton`, `AudioSetupButton` and `ActionText`).
4. `HostsWindow` becomes the wizard, with the connection check and firewall
   helper exposed for the map and the host dashboard.
5. `LiveConversationWindow` gets the chat-first layout with every named
   control unchanged.
6. `SetupWindow` gets the stepper style.
