# Platforms: what each device can do

> **2026-10-06:** a Linux desktop companion and a .NET Mac companion/host are
> now planned; see [Linux and macOS desktop companion](DESKTOP_LINUX_MACOS.md)
> (slices DX01-DX05). The tables below are updated as those slices land.

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

Slice IDs: IO = [iOS plan](IOS.md#delivery-slices), MA = [macOS
plan](MACOS.md#delivery-slices), AN = [Android plan](ANDROID.md#delivery-slices),
PL = [below](#delivery-slices). Minimum systems: Windows 10/11 x64 (today),
macOS 14, iOS/iPadOS 26, Android 8.0.

| Job or feature | Windows | macOS | iPhone / iPad | Android | Linux |
| --- | --- | --- | --- | --- | --- |
| Thinking: OpenAI, OpenRouter, NVIDIA Build, any OpenAI-compatible server | **Works** | Planned (MA04) | Planned (IO05) | Planned (AN07) | Not planned: no Linux companion app; Linux computers are hosts |
| Thinking on the device itself | **Works**: a local server (Ollama, LM Studio) through Chat Completions | Planned: Apple Intelligence (M1+, macOS 26+; screen images from 27) and Ollama on the Mac's GPU (MA04); MLX models (MA02). Intel: CPU-only 1-4B models | Planned (IO05): Apple Intelligence (iPhone 15 Pro or later, M-series iPad, iOS 26+; images from 27) | Planned (AN07): Gemini Nano on supported flagships, **only while Martlet is in front, never behind a game**; small LiteRT or llama.cpp models (old phones: 0.5-1B, slow) | - |
| Thinking: Apple Private Cloud Compute | - | Not planned: needs Apple's entitlement and most likely the paid Developer Program, which Martlet doesn't use | Not planned: same | - | - |
| Listening: OpenAI | **Works** | Planned (MA04) | Planned (IO05) | Planned (AN07) | - |
| Listening on the device | Planned (PL02): Windows speech and whisper.cpp can be saved but are not used yet | Planned (MA04): Apple speech (macOS 26; unverified on Intel), whisper.cpp | Planned (IO05): Apple speech (iOS 26+) | Planned (AN07): Android speech, on the phone from Android 12 (older phones may send audio to Google, with a warning); whisper.cpp tiny/base | - |
| Speaking: OpenAI | **Works** | Planned (MA04) | Planned (IO05) | Planned (AN07) | - |
| Speaking on the device | Planned (PL02): Windows voices | Planned (MA04): Apple voices, Personal Voice | Planned (IO05): Apple voices, Personal Voice | Planned (AN07): Android voices | - |
| Your own cloned voice (F5) | **Works** through an NVIDIA host | Through an NVIDIA host (MA07); F5 on MLX on Apple silicon, 16 GB+ suggested (MA03) | Through a host (IO09) | Through a host (AN10) | - |
| Lip-sync | **Works**: loudness; Audio2Face on this PC or a host (NVIDIA) | Planned (MA05): loudness; Audio2Face through an NVIDIA host | Planned (IO07): same | Planned (AN09): same | - |
| Character over other windows and games | **Works**: transparent always-on-top window | Planned (MA05): floating panel over full-screen games and Spaces | Planned (IO07/IO08): in the app, beside a game on iPad; over a full-screen game only through Picture-in-Picture (experimental) | Planned (AN09): needs *Display over other apps*; taps reach the game only through a mostly transparent overlay | - |
| Watch my screen | **Works** (borderless/windowed games) | Planned (MA06): Screen Recording permission, asked again from time to time | Planned (IO06): only while a screen broadcast you start is running | Planned (AN08): screen-capture permission each session | - |
| Watch a camera or a phone's camera | **Works**: webcams, capture cards, phone-as-webcam apps (Phone Link, DroidCam, Camo, iVCam), HTTP snapshot/MJPEG/RTSP addresses ([details](SCREEN_COMMENTARY.md#cameras-phones-and-other-video-sources)) | - | The iPhone can serve its camera to Windows (IO06) | Martlet on the phone can serve its camera to Windows (AN11: password-protected plain HTTP, readable on your Wi-Fi, only while sharing); any IP-camera app works today | - |
| Hands-free listening | **Works** | Planned (MA04), with a global push-to-talk hotkey | Planned (IO05): behind a game only while listening is on | Planned (AN07): behind a game with a notification showing; echo cancellation varies by phone | - |
| Voice ID, local memory, personas | **Works** | Planned (MA07) | Planned (IO09) | Planned (AN10) | - |
| Pair with hosts, who does what, failover | **Works** | Planned (MA07) | Planned (IO09) | Planned (AN10) | - |

## Devices that do jobs (hosts)

| Job or feature | Linux (Docker or native with systemd, x86_64) | Windows (Docker Desktop, This PC) | macOS (Martlet's Mac host) | iPhone / iPad | Android |
| --- | --- | --- | --- | --- | --- |
| Thinking: Ollama | **Works** (NVIDIA makes it fast; small models on the CPU) | **Works** | **Built** (DX04, not yet run on a Mac): relays to native Ollama, on the GPU (Metal) on Apple silicon; Intel CPU-only, 1-4B | Impossible: Apple Intelligence does the thinking there | Not planned: no Ollama for Android; a LiteRT or llama.cpp model serves the same route |
| Thinking: Apple Intelligence | Impossible | Impossible | Planned (MA02; Apple silicon, macOS 26+) | Planned (IO03; Apple Intelligence devices, iOS 26+) | Impossible |
| Thinking: MLX models (including vision) | Impossible | Impossible | Planned (MA02; Apple silicon) | - | - |
| Thinking: Gemini Nano, LiteRT or llama.cpp | - | - | - | - | Planned (AN04): LiteRT and llama.cpp in the background; Gemini Nano **only while the Hosting screen is in front** |
| Listening: whisper | **Works** (CPU is fine; NVIDIA faster) | **Works** | **Built** (DX04, not yet run on a Mac): whisper.cpp from Homebrew, Metal on Apple silicon; base/small on Intel | Not planned: Apple speech instead | Planned (AN04): tiny/base on old phones |
| Listening: Parakeet | **Works** (CPU; English or 25 European languages) | **Works** | - | - | - |
| Listening: Apple speech | Impossible | Impossible | Planned (MA02; macOS 26, unverified on Intel) | Planned (IO03, iOS 26+) | Impossible |
| Listening: Android speech | - | - | - | - | Planned (AN04; Android 13+, unverified that it can take the desktop's audio) |
| Speaking: F5 voice cloning | **Works**: NVIDIA GPU with **6 GB+** | **Works**: NVIDIA 6 GB+ | F5 on MLX serves the same route (MA03; Apple silicon) | Impossible | Impossible |
| Speaking: device voices | - | - | Planned (MA03): Apple voices, Personal Voice after you confirm on the Mac | Planned (IO04): Apple voices, Personal Voice after you confirm on the phone | Planned (AN05): Android voices |
| Lip-sync: Audio2Face | **Works**: NVIDIA GPU with **4 GB+** | **Works**: NVIDIA 4 GB+ | Impossible: no NVIDIA GPU | Impossible | Impossible |
| Microphone and speaker for another computer (satellite) | Not planned | Not planned | Planned (MA09) | Planned (IO10) | Planned (AN06): old 3-4 GB phones are enough |
| Keeps hosting in the background | **Yes** | **Yes**, while Docker Desktop runs | **Built** (DX04): yes, as a launchd agent while you are logged in | **No**: only while Martlet is open on the screen | Planned (AN03): yes, with a notification, even with the screen off |
| Roles installed and removed from your desktop | **Yes** (SSH or console) | **Yes** | No: chosen on the Mac with `macos-setup` | No: switched on in Martlet on the device | No: switched on in Martlet on the device |

A Mac can also be a **Linux host**: Docker Desktop for Mac runs the host engine
on the CPU only (MA10; the host image now builds for ARM64, DX04, but has not run on a Mac),
and an old Intel Mac reinstalled with Ubuntu is a normal Linux host today
(T2 models need the t2linux kernel; never run on Mac hardware).

Hardware thresholds come from the host roles and the platform plans:
Audio2Face needs an NVIDIA GPU with 4 GB+ and F5 needs 6 GB+. Ollama and
whisper run on any CPU, and a GPU makes them faster. Apple Intelligence
requires the [Apple Intelligence devices](https://support.apple.com/en-us/121115).
Gemini Nano needs one of Google's supported phones. ARM64 Linux computers
such as a Raspberry Pi are **unknown**: the host image builds for ARM64 (DX04)
and Ollama's image is multi-architecture, but the pinned whisper.cpp image is
x86_64 only and nothing has been qualified on ARM64 (PL04).

## Using the hardware you already own

"Today" means the current Windows app and host engine. The other rows need
their plan's slices.

| What you have | Best use | When |
| --- | --- | --- |
| Gaming PC with an NVIDIA GPU (8 GB+, even an older RTX 20/30) | Host: Audio2Face, your F5 voice, whisper; a small local model if VRAM allows | Today |
| PC or mini PC without NVIDIA (x86_64, Linux or Windows) | Companion with cloud thinking; host for whisper on the CPU, or a small Ollama model | Today |
| Old Windows laptop | Companion with OpenAI or OpenRouter; talk to Martlet from another room | Today |
| Any old phone with a camera | A camera for Watch my screen through Phone Link, DroidCam, Camo, iVCam or an IP-camera app | Today, on Windows |
| Old Intel Mac reinstalled with Ubuntu | Linux host for whisper or a small model on the CPU | Today (never run on Mac hardware) |
| Raspberry Pi or other ARM Linux | Small whisper or Ollama host once the images are built for ARM64 | PL04 |
| Apple-silicon Mac (16 GB+ ideal, 8 GB works for small models) | Companion with a floating character; host for Ollama/whisper.cpp on the GPU (built: DX04), later F5 on MLX, Apple speech, voices and Apple Intelligence | DX04 (host), MA03-MA07 |
| Intel Mac (2018-2020, last macOS is 26) | Companion with cloud thinking; host for CPU whisper (built: DX04), later Apple voices and satellite microphone | DX04 (host), MA03-MA09 |
| iPhone 15 Pro or later, iPad with M-series | Companion while gaming on it; host for thinking, listening and speaking while the app is open | IO03-IO09 |
| Older iPhone or iPad on iOS 26 (no Apple Intelligence) | Companion with cloud thinking; host for listening and speaking while the app is open; satellite microphone | IO03-IO10 |
| Recent Android phone (supported flagship, 8 GB+) | Companion while gaming; host for listening, speaking and LiteRT/llama.cpp thinking with the screen off | AN03-AN10 |
| Old Android phone (2018-2020, 3-4 GB, Android 8+) | Satellite microphone and speaker on a charger; a camera for Watch my screen; companion with cloud thinking; Android voices or tiny whisper host | AN03-AN07, AN11 |
## What the app guarantees

These rules apply to every Martlet companion. The Windows app implements them
now. The macOS, iOS and Android apps must implement them the same way.

1. **No impossible choices.** *Done by* menus, Devices-map commands and
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
4. **Loss is visible.** A card at the top of **Home** and under the **Devices** map
   lists every job that is not working or is reduced, with the reason, what it
   means ("Martlet can't hear you; you can still type") and one-click fixes:
   use the Setup choice instead, check the host now, or open Setup or Devices.
   When thinking is down, the Home headline says "Martlet can't reply right
   now", and each job row on the Devices page repeats the problem.
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
| The host answers but the role was removed or never installed | Shown as not working (lip-sync: reduced): "gpu-1 answers but doesn't run Ollama: it isn't installed there". Fixes: install it there, use the Setup choice, open Devices |
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
| iPhone/iPad build expired (free Apple ID, 7 days) | Martlet there stops opening until AltStore or SideStore refreshes it, so the host stops answering like any other. The Devices map reminds you to keep the refresh going |
| Phone host without the job switched on | Shown as "can't take it now: switch on listening in Martlet on that device" |
| A phone or Mac serves a job through an existing route (for example Apple speech on the transcription route) | Works like any host. Menus show the model it advertises instead of "whisper" |
| Failover moves thinking to a model that can't see images | Screen watching says the model can't see ([SCREEN_COMMENTARY](SCREEN_COMMENTARY.md#incompatibility-warnings)); talking keeps working |
| Lip-sync host down | Reduced: the mouth follows the voice's loudness. Fix: take lip-sync back to this PC |
| Only speaking is down | Martlet still hears you and replies as text |
| Battery-powered host (reports `battery`) | Noted on the Devices map: it may slow down or stop hosting |
| Android host using Gemini Nano when its Hosting screen leaves the front | The phone stops advertising that model (Android allows Gemini Nano only in the front). Thinking shows as not working, with no silent switch to another model (AN04) |
| Android host stopped by the phone's battery saver | It stops answering like any host. The phone's **Keep Martlet running** checklist explains how to exempt it (AN03) |
| Mac host asleep, or its user logged out | It stops answering. Martlet on the Mac keeps the Mac awake while it hosts and keeps hosting after the window closes, but not after logout (MA02) |
| Windows PC running its own host service, left idle | Martlet there keeps it awake (a Windows power request) whenever it is a Martlet host PC, from the moment Martlet starts, and on a companion PC while that host service answers and serves another computer, as on a Mac; the screen can still turn off. Settings › Startup and closing says so. Only while Martlet runs there (keep it in the notification area and starting with Windows); put to sleep or shut down by hand, it stops answering like any host |
| Mac with Docker Desktop, or Ubuntu installed | It is a Linux host: CPU-only roles, GPU roles shown disabled with the reason |
| Sync off and no check yet | Devices shows "not checked since Martlet started". Home stays quiet until something is known to be wrong |

## Machine report platform fields

Hosts describe themselves at `GET /martlet/v1/machine`
([host guide](../deploy/host/README.md#what-the-host-tells-martlet)). These
optional fields are additive: older desktops ignore them, and hosts without
them count as Linux.

| Field | Values | Sent by |
| --- | --- | --- |
| `method` | `docker`, `native`, `app` (the host runs inside the Martlet app on a Mac, phone or tablet) | every host |
| `platform` | `linux`, `windows`, `macos`, `ios`, `android` (what the roles run on) | `martlet-host` sends `linux`; the Mac host sends `macos`; app hosts send their own |
| `os_version` | for example `26.1`, `14` | app hosts |
| `architecture` | `x64`, `arm64` | every host |
| `features` | up to 16 of `apple-intelligence`, `gemini-nano`, `foreground-only`, `battery` | app hosts |
| `chip`, `unified_memory`, `gpu_working_set_gb` | for example `Apple M2 Pro`, `true`, `21.3` (Metal's recommended working set, which model suggestions use) | the Mac host (DX04) |

## Decisions (2026-10-01)

The owner asked for every decision that costs nothing to be made, and for
anything that costs money to be skipped:

| Topic | Decision | Effect |
| --- | --- | --- |
| Paid programs (Apple Developer Program, Google Play, Android full verification) | **None** | iPhone/iPad: free Apple ID sideloading, re-signed every 7 days; no TestFlight, App Store, App Groups or Private Cloud Compute. Mac: not notarized; users click Open Anyway once. Android: sideloaded APK; Google's free limited-distribution account only |
| Hardware | Only devices the owner already has | Results on devices nobody has stay NOT RUN; nothing is bought |
| Signing and update keys | Free, self-made, created once by the owner with a script, stored as repository secrets, backed up offline; never committed and never created by an agent | Android updates install over each other; Mac permissions survive updates; Mac in-app updates are verified |
| Folders | `apple/` (iPhone, iPad, Mac), `android/` | - |
| Releases | Every platform's files attach to the same `v<version>` GitHub release | One version number everywhere |
| Android package name | `io.github.throndir2.martlet` | Permanent |
| On-phone models | Apache-2.0 by default; Gemma 3/3n after accepting its free terms | - |
| Live2D | Same bundled Cubism Core for Web on every platform; the owner names the new platforms in the Expandable Application review already applied for | VRM needs no review |

The details are in each plan: [iOS](IOS.md#decisions-2026-10-01),
[macOS](MACOS.md#decisions-2026-10-01), [Android](ANDROID.md#decisions-2026-10-01).

## Delivery slices

| ID | Deliverable | Status |
| --- | --- | --- |
| PL01 | Platform catalog, coverage card, guardrails in the Windows app, impact-aware forget/remove, machine report platform fields | Done in this change; device results NOT RUN |
| PL02 | Use Windows speech, Windows voices and whisper.cpp in conversations (today they can be saved but are refused) | Planned |
| PL03 | Setup advisor asks about Macs, phones, tablets and old PCs, and recommends jobs for them from this catalog | Planned |
| PL04 | ARM64 Linux hosts (Raspberry Pi 5 class, Docker Desktop on Apple silicon): host and role images for arm64, then qualification. Shared with MA10 | Host image builds for arm64 (DX04); role images and qualification planned |
| PL05 | Conversation window shows the coverage card before a turn instead of failing at dispatch | Planned |
| IO, MA, AN | Platform apps: [iOS](IOS.md#delivery-slices) IO01-IO11, [macOS](MACOS.md#delivery-slices) MA01-MA10, [Android](ANDROID.md#delivery-slices) AN01-AN11 | Planned |
