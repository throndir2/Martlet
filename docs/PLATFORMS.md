# Platforms: what each device can do

**Status, 2026-10-01.** Only the **Windows companion** and **Linux / Windows
Docker hosts** exist today. Everything marked *planned* comes from the
[iOS](IOS.md), [macOS](MACOS.md) and [Android](ANDROID.md) plans and has not
been built. No result on a Mac, iPhone, iPad or Android device has been run
(**NOT RUN**).

The goal is to put **any hardware you already own** to work, without
letting you pick something a device cannot do, and to say plainly when a job
stops working ("Martlet can't reply right now: gpu-1 isn't answering").

The rules on this page are code, not only documentation:
[`PlatformCatalog`](../src/Martlet.Core/Platforms/PlatformCatalog.cs) decides
what each platform and device can run, and
[`JobCoverageRules`](../src/Martlet.Core/Platforms/JobCoverage.cs) decides
what still works and what an action takes away. When the platform plans
change, update the catalog and this page together. Future macOS, iOS and
Android apps follow the same rules.

## Words used here

- **Companion:** the device you talk to. It owns the microphone, speakers,
  character, consent and keys.
- **Host:** a device that does a **job** for a companion.
- **Jobs:** *thinking* (the conversation model), *listening* (speech to text),
  *speaking* (text to speech) and *lip-sync*. Each job has exactly one doer: a
  cloud provider, the companion itself, one paired host, or (lip-sync only)
  nobody.
- **Status:** **Works** today · **Planned** (with its slice) · **Not planned** ·
  **Impossible** on that platform.

## The device you talk to

| Job or feature | Windows | macOS | iPhone / iPad | Android | Linux |
| --- | --- | --- | --- | --- | --- |
| Thinking: OpenAI, OpenRouter, NVIDIA Build, any OpenAI-compatible server | **Works** | Planned | Planned (IO05) | Planned | Not planned: no Linux companion app; Linux computers are hosts |
| Thinking on the device itself | Local server through Chat Completions (**Works**) | Planned: Apple Intelligence (M1+, macOS 26+), MLX models (Apple silicon) | Planned: Apple Intelligence (iPhone 15 Pro or later, M-series iPad, iOS 26+); Private Cloud Compute (iOS 27, entitlement) | Planned: Gemini Nano (supported phones), small LiteRT / llama.cpp model (6 GB+) | - |
| Listening: OpenAI | **Works** | Planned | Planned | Planned | - |
| Listening on the device | Planned (PL02): Windows speech and whisper.cpp can be saved but are not used yet | Planned: Apple speech (macOS 26+) | Planned: Apple speech (iOS 26+) | Planned: Android speech (on the phone from Android 12) | - |
| Speaking: OpenAI | **Works** | Planned | Planned | Planned | - |
| Speaking on the device | Planned (PL02): Windows voices | Planned: Apple voices, Personal Voice | Planned: Apple voices, Personal Voice | Planned: Android voices | - |
| Your own cloned voice (F5) | Through a host with an NVIDIA GPU (**Works**) | Through a host; or F5 on MLX on the Mac (IO11) | Through a host | Through a host | - |
| Lip-sync | Loudness (**Works**); Audio2Face on this PC or a host (**Works**, NVIDIA) | Loudness; Audio2Face through a host | Loudness; Audio2Face through a host | Loudness; Audio2Face through a host | - |
| Character over other windows and games | **Works** (transparent always-on-top) | Planned: floating window | Planned (IO07/IO08): in the app, beside a game on iPad; over a full-screen game only with Picture-in-Picture (experimental) | Planned: needs *Display over other apps* | - |
| Watch my screen | **Works** (borderless/windowed games) | Planned: Screen Recording permission | Planned (IO06): only while a screen broadcast you start is running | Planned: screen-capture permission each session | - |
| Hands-free listening | **Works** | Planned | Planned: behind a game only while listening is on | Planned: behind a game with a notification showing | - |
| Voice ID, local memory, personas | **Works** | Planned | Planned (IO09) | Planned | - |
| Pair with hosts, who does what, failover | **Works** | Planned | Planned (IO09) | Planned | - |

## Devices that do jobs (hosts)

| Job or feature | Linux (Docker or native Ubuntu, x86_64) | Windows (Docker Desktop, This PC) | macOS | iPhone / iPad | Android |
| --- | --- | --- | --- | --- | --- |
| Thinking: Ollama | **Works** (NVIDIA makes it fast; small models on the CPU) | **Works** | Planned: native, uses the Mac's GPU | Impossible: Apple Intelligence does the thinking there | Not planned: no Ollama for Android |
| Thinking: Apple Intelligence | Impossible | Impossible | Planned (M1+, macOS 26+) | Planned (IO03; Apple Intelligence devices, iOS 26+) | Impossible |
| Thinking: Gemini Nano or a small on-phone model | - | - | - | - | Planned |
| Thinking: MLX models | Impossible | Impossible | Planned (Apple silicon) | - | - |
| Listening: whisper | **Works** (CPU is fine; NVIDIA faster) | **Works** | Planned: whisper.cpp on the Mac's GPU | Not planned: Apple speech instead | Planned: small models on the CPU |
| Listening: Apple speech | Impossible | Impossible | Planned (macOS 26+) | Planned (IO03, iOS 26+) | Impossible |
| Listening: Android speech | - | - | - | - | Planned (Android 12+) |
| Speaking: F5 voice cloning | **Works**: NVIDIA GPU with **6 GB+** | **Works**: NVIDIA 6 GB+ | Impossible as the F5 host role; F5 on MLX planned (IO11, Apple silicon) | Impossible | Impossible |
| Speaking: device voices | - | - | Planned: Apple voices | Planned (IO04): Apple voices, Personal Voice after you confirm on the phone | Planned: Android voices |
| Lip-sync: Audio2Face | **Works**: NVIDIA GPU with **4 GB+** | **Works**: NVIDIA 4 GB+ | Impossible: no NVIDIA GPU | Impossible | Impossible |
| Microphone and speaker for another computer (satellite) | Not planned | Not planned | Planned | Planned (IO10) | Planned |
| Keeps hosting in the background | **Yes** | **Yes**, while Docker Desktop runs | Planned: yes | **No**: only while Martlet is open on the screen | Planned: yes, with a notification showing |
| Roles installed and removed from your desktop | **Yes** (SSH or console) | **Yes** | Planned | No: switched on in Martlet on the device | No: switched on in Martlet on the device |

Hardware thresholds come from the host roles and the platform plans:
Audio2Face needs an NVIDIA GPU with 4 GB+ and F5 needs 6 GB+. Ollama and
whisper run on any CPU, and a GPU makes them faster. Apple Intelligence
requires the [Apple Intelligence devices](https://support.apple.com/en-us/121115).
ARM64 Linux computers such as a Raspberry Pi are **unknown**: the host image
and role images have only been built for x86_64 (PL04).

## Using the hardware you already own

Planned rows depend on their platform plan. "Today" means the current
Windows app and host engine.

| What you have | Best use | When |
| --- | --- | --- |
| Gaming PC with an NVIDIA GPU (8 GB+, even an older RTX 20/30) | Host: Audio2Face, your F5 voice, whisper; a small local model if VRAM allows | Today |
| PC or mini PC without NVIDIA (x86_64, Linux or Windows) | Companion with cloud thinking; host for whisper on the CPU, or a small Ollama model | Today |
| Old Windows laptop | Companion with OpenAI/OpenRouter; talk to Martlet from another room | Today |
| Raspberry Pi or other ARM Linux | Small whisper or Ollama host once the images are built for ARM64 | PL04 |
| Apple-silicon Mac (8 GB+) | Companion; host for Apple speech, Apple voices, Apple Intelligence, whisper.cpp, Ollama, F5 on MLX | [macOS plan](MACOS.md) |
| Intel Mac | Companion with cloud thinking; CPU-only whisper or a small model | [macOS plan](MACOS.md) |
| iPhone 15 Pro or later, iPad with M-series | Companion while gaming on it; host for thinking, listening and speaking while the app is open | [iOS plan](IOS.md) |
| Older iPhone or iPad on iOS 26 (no Apple Intelligence) | Companion with cloud thinking; host for listening and speaking while the app is open; satellite microphone | [iOS plan](IOS.md) |
| Recent Android phone (Gemini Nano, 8 GB+) | Companion while gaming; host for thinking, listening and speaking in the background | [Android plan](ANDROID.md) |
| Old Android phone (3-4 GB) | Satellite microphone and speaker; companion with cloud thinking; Android voices host | [Android plan](ANDROID.md) |

## What the app guarantees

These rules apply to every Martlet companion. The Windows app implements them
now. The macOS, iOS and Android apps must implement them the same way.

1. **No impossible choices.** *Who does what* menus, Devices-map commands and
   role installs show a device that cannot run an engine as disabled, with the
   reason. Examples: "F5 voice cloning needs an NVIDIA GPU with 6 GB+; gpu-1
   reports Radeon RX 6600 (8 GB)"; "Ollama can't run on iphone (iOS/iPadOS
   host)". The same check runs again when an action starts, so an old menu or
   a map shortcut cannot get around it.
2. **Unknown is allowed, and the app says so.** A host that has not reported its
   hardware (it is older, or has not been checked) can still be chosen. The
   install confirmation names what Martlet could not check.
3. **Planned means "not built yet".** Planned items carry their slice ID and
   cannot be chosen.
4. **Loss is visible.** A card at the top of **Home** and **Who does what**
   lists every job that is not working or is reduced, with the reason, what it
   means ("Martlet can't hear you; you can still type") and one-click fixes:
   use the Setup choice instead, check the host now, or open Setup or Devices.
   When thinking is down, the Home headline says "Martlet can't reply right
   now", and each job tile repeats the problem.
5. **No surprises before destructive actions.** Before you forget a host or
   remove a role, Martlet lists what each job will do: move by failover to a
   named host, go back to your Setup choice (naming the provider, what it
   receives and that it may cost money), or nobody does it. When you confirm,
   jobs go back to their Setup choices first, so no route is left pointing at
   a host that is gone. Computers that keep who does what in sync are
   mentioned too.
6. **Never a silent cloud fallback.** A job moves to a cloud provider only
   after a confirmation that names it. Failover moves jobs only between your
   own hosts that run the same engine.
7. **Phones and tablets manage their own roles.** Their Devices entries have no
   SSH, install, remove, update or prepare commands. An iPhone or iPad host is
   marked as hosting only while Martlet is open on it, with failover
   suggested.
8. **Nothing is contacted to find out.** Coverage uses what Martlet already
   knows: saved routes, the last **Check connection** and, when sync is on, the
   15-second check. Otherwise the job shows as "not checked since Martlet
   started", with a **Check now** button.

## Edge cases

| Situation | What Martlet does |
| --- | --- |
| The host doing a job stops answering | The job is shown as not working, with its effect. Fixes: check it now, use the Setup choice, open Devices. With failover on, the card names the host it will move to, or says no other host runs the engine |
| The host answers but the role was removed or its model is gone | Same as above: "gpu-1 answers but no longer runs Ollama" |
| This PC reaches no host at all | Every host job shows as not answering. Failover does not run, because this PC's own network is the likelier problem (see [CLUSTER](CLUSTER.md#edge-cases)) |
| Forgetting a host that does jobs | The confirmation lists each job's fate. Jobs go back to their Setup choices first. A job with no Setup choice is left with nobody, and the card says so |
| Removing a role that does a job here | Failover target: the job moves there. Otherwise it goes back to the Setup choice first ("Hand back and remove"). Otherwise nobody does it, as the confirmation said |
| Removing a role other synced computers use | The confirmation says they lose it too, unless failover moves them |
| A cloud key was removed | "The OpenAI key was removed." Fix: open Setup |
| A route is turned off or needs review | Shown as not working, with Open Setup |
| Windows speech or whisper.cpp chosen in Setup | Shown as not working: it can be saved but conversations don't use it yet (PL02). Choose OpenAI or a host |
| Host with an AMD/Intel GPU or a too-small NVIDIA GPU | F5 and Audio2Face are shown disabled with the reason. Ollama and whisper stay available |
| NVIDIA driver broken on the host (memory not reported) | Unknown: allowed, and the confirmation says Martlet couldn't check it |
| Docker host whose containers can't use the GPU | Treated as having no usable NVIDIA GPU, with that reason |
| Host older than platform reporting | Counted as a Linux host. GPU checks use its hardware report when it has one; otherwise they are unknown |
| Older desktop paired with a phone host | The older desktop ignores the new platform fields, treats the phone as a Linux host and may offer installs that fail. **Update desktops before pairing a phone or Mac host** |
| iPhone/iPad host goes to the background | It stops answering. The card says it hosts only while Martlet is open on its screen. Failover is suggested when you hand it a job |
| Phone host without the job switched on | Shown as "can't take it now: switch on listening in Martlet on that device" |
| A phone or Mac serves a job through an existing route (for example Apple speech on the transcription route) | Works like any host. Menus show the model it advertises instead of "whisper" |
| Failover moves thinking to a model that can't see images | Screen watching says the model can't see ([SCREEN_COMMENTARY](SCREEN_COMMENTARY.md#incompatibility-warnings)); talking keeps working |
| Lip-sync host down | Reduced: the mouth follows the voice's loudness. Fix: take lip-sync back to this PC |
| Only speaking is down | Martlet still hears you and replies as text |
| Battery-powered host (reports `battery`) | Noted on the Devices map: it may slow down or stop hosting |
| Sync off and no check yet | Devices shows "not checked since Martlet started". Home stays quiet until something is known to be wrong |

## Machine report platform fields

Hosts describe themselves at `GET /martlet/v1/machine`
([host guide](../deploy/host/README.md#what-the-host-tells-martlet)). These
optional fields are additive: older desktops ignore them, and hosts without
them count as Linux.

| Field | Values | Sent by |
| --- | --- | --- |
| `method` | `docker`, `native`, `app` (the host runs inside the Martlet app on a Mac, phone or tablet) | every host |
| `platform` | `linux`, `windows`, `macos`, `ios`, `android` (what the roles run on) | `martlet-host` sends `linux`; app hosts send their own |
| `os_version` | for example `26.1`, `14` | app hosts |
| `architecture` | `x64`, `arm64` | every host |
| `features` | up to 16 of `apple-intelligence`, `gemini-nano`, `foreground-only`, `battery` | app hosts |

## Delivery slices

| ID | Deliverable | Status |
| --- | --- | --- |
| PL01 | Platform catalog, coverage card, guardrails in the Windows app, impact-aware forget/remove, machine report platform fields | Done in this change; device results NOT RUN |
| PL02 | Use Windows speech, Windows voices and whisper.cpp in conversations (today they can be saved but are refused) | Planned |
| PL03 | Setup advisor asks about Macs, phones, tablets and old PCs, and recommends jobs for them from this catalog | Planned |
| PL04 | ARM64 Linux hosts (Raspberry Pi 5 class): host and role images for arm64, then qualification | Planned |
| PL05 | Conversation window shows the coverage card before a turn instead of failing at dispatch | Planned |
| IO, MA, AN | Platform apps: [iOS](IOS.md#delivery-slices), [macOS](MACOS.md), [Android](ANDROID.md) | Planned |
