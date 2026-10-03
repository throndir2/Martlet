# Watch my screen: screen-aware commentary

Martlet can watch what you are playing or doing and, now and then, say something
about it, like a friend in the room. It stays quiet most of the time. This page
explains who sees the pictures, which models can, and what Martlet tells you when
your setup can't.

## How it works

1. **Companion › Vision** shows whether your Thinking model can see (see
   below), what Martlet looks at (**my active window**, **my whole screen**:
   every monitor with the taskbar and pop-up notifications, or a camera) and
   **how chatty** it is (Quiet, Normal, Chatty), and says exactly what is
   captured and where it is sent.
2. Click **Turn vision on** (off by default). From then on, opening **Start
   talking** starts looking; the talk window's **Watching** button and its
   title show it. It keeps going in the background (while you play) until you
   click that button, Stop or Esc, lock Windows or close the talk window.
   **Turn vision off** in Companion stops it for good.
3. Every 3 seconds Martlet captures the screen **on this PC** (DXGI Desktop
   Duplication, falling back to GDI; kept only in memory) and compares a 16x9
   grey thumbnail with the last one to notice change. The active window is
   downscaled to at most 1024 px. The whole screen is every monitor side by
   side as Windows arranges them, each at most 1024 px and the picture at most
   2048 px, so the taskbar, the notification area and pop-up notifications are
   in it; it doesn't need a window in front (the desktop counts). The
   **Watching** button's dot blinks on each capture (it twinkles
   and reads **Looking…** while a look is with the model), and a line under the
   talk window's status says what it sees (*Watching your whole screen (2
   monitors).*), how the last look went (*nothing worth saying*, *said
   something*), why it is holding off (you're talking, you seem away, the
   hourly budget is used), what wanted your attention and whether your last
   message went with the picture.
   Captures are never added to the history; only remarks are.
4. A **pacer** decides when to take a real look, the way a person would:
   - never while you are talking to Martlet (hands-free speech, typing, a
     reply playing) and not for a while after (12-30 s);
   - not soon after its last remark (40 s to 4 min plus jitter) or its last
     look (25-90 s);
   - more likely right after the picture changes (a new scene, a death, a
     menu), rarely when nothing moves;
   - never to an empty room: no keyboard/mouse input for 5 minutes and a still
     screen means you are away;
   - at most 12 / 24 / 45 looks per hour (Quiet / Normal / Chatty), because each
     look is one model request.
5. A look sends **one** screenshot (JPEG) with the window title, your persona
   and recent conversation to the Thinking model. The model is told that real
   friends stay quiet and to answer exactly `[pass]` unless something is worth a
   remark, never to narrate the screen, repeat itself (it is given what it said
   in the last 30 minutes) or read out private details. A new message, call or
   reminder is worth a heads-up, naming only who or which app it is from (*Sam
   just messaged you*), never the message itself. `[pass]` is never
   spoken; a remark is spoken with the selected voice like any reply.
6. You come first: typing or push-to-talk stops a remark in progress. With
   always listening on, an idle listen (nobody speaking) briefly yields to a
   look and re-arms right after. Remarks appear in the talk window's history.

## Martlet sees what you see when you talk to it

While vision is on, everything you type or say also goes with the **newest
picture** of what Martlet watches (taken in the last 10 seconds; a skipped
capture, say a private window in front, sends none), so you can ask *"what do
you think of this?"*, *"who just messaged me?"* or *"how do I beat this boss?"*.
The reply is told the picture is what you see right now and to use it only when
it helps, without describing it unprompted. Your message's bubble says *Martlet
saw your whole screen.* (or your active window, or the camera), and the vision
line says *Your message at 10:14 PM went with it.* These pictures don't count
toward the looks per hour, but they make each reply's request larger, which may
cost more. If the Thinking model rejects the picture, Martlet asks again with
your words only and says so on your message; a model Martlet doesn't know can
see also stops vision with the fix, like a rejected look. Memory never gets the
picture.

## Notifications and taskbar buttons

With **my whole screen**, Martlet also notices what wants your attention and
looks **right away**, while it is still on screen, instead of waiting for its
pacer:

- a **taskbar button that starts flashing** (a chat app's new message, an
  invite, a finished download), through Windows' documented shell hook
  (`RegisterShellHookWindow`, `HSHELL_FLASH`). The flashing window's title
  goes with the screenshot (*the taskbar button of "Sam - Chat" just started
  flashing*);
- a **pop-up notification**, noticed when the shell's notification window
  appears (best effort: a visible `Windows.UI.Core.CoreWindow` of
  ShellExperienceHost or ShellHost that isn't in front and is smaller than half
  its monitor). Windows doesn't show pop-ups during Do not disturb or while a
  full-screen app is in front.

The model is asked for a quick heads-up only when it is a message, call or
reminder you would want to know about. Such a look still waits for you to
finish talking (up to a minute), keeps to the hourly budget, isn't taken when
you seem away and happens at most every 20 seconds; Martlet's own windows,
windows already in front, private windows and a window that flashed in the
last two minutes are ignored. Nothing is hooked into other apps and no
notification text is read; the picture shows what popped up. The vision line
says *Last look 10:17 PM (a flashing taskbar button): commented.*, or why it
didn't look (*Noticed a notification at 10:17 PM but didn't look: you seem
away.*).

Never captured: minimized windows, and a password manager or private/incognito
browser window in front (by window title). When a Martlet window is in front
(say, you clicked the talk window to read it), Martlet looks at the window you
were using behind it instead. Martlet's own windows, and password managers and
private browser windows anywhere in the picture, are painted grey wherever they
show (working down the z-order, so a window on top of them stays visible; a
picture that is almost all Martlet is skipped), so the model never reads its
own conversation. Protected video reads back black and is skipped. Screenshots
are never saved, logged, put in local memory or support bundles.

## Cameras, phones and other video sources

**What Martlet looks at** also offers two non-screen sources. The pacer,
chattiness, looks per hour, `[pass]` rule and the stop conditions are the same;
keyboard/mouse idle is not used for them (only a still picture counts as an
empty room).

- **A camera (webcam, capture card, phone as webcam).** Click **Find cameras**
  to list Windows video capture devices (Media Foundation). Anything Windows
  shows as a camera works: USB webcams, capture cards (a console or a second
  PC), OBS Virtual Camera, and **phone cameras** through Windows 11 Phone Link
  "connected camera", DroidCam, Camo, iVCam or similar apps. The camera is
  opened only while watching (its light is on only then) and released on stop.
- **A phone or network camera address.** Enter one of:
  - `http(s)://` a JPEG snapshot URL (fetched once per look), for example the
    Android "IP Webcam" app's `http://phone:8080/shot.jpg`, go2rtc
    `/api/frame.jpeg?src=<cam>` or Frigate `/api/<cam>/latest.jpg`;
  - `http(s)://` an MJPEG stream (one frame is read per look), for example
    `http://phone:8080/video`;
  - `rtsp://` a camera stream, or a path to a video file, read through the
    Media Foundation Source Reader.

  `user:password@` in the address is used until Martlet closes but never
  saved; the saved address has credentials stripped, so enter it again after a
  restart. With Home Assistant connected (Companion › Smart home), **Use a Home
  Assistant camera** lists its cameras; picking one fills in its
  `camera_proxy` snapshot address, and Martlet adds the saved Home Assistant
  token (from Windows Credential Manager) only to requests for that address.
  Other header-based tokens are not supported; use a snapshot URL that carries
  its own access instead.

A future Martlet phone app can be a source by serving the same contract: a
JPEG snapshot or MJPEG over HTTP(S) on the local network.

Camera privacy: frames are kept only in memory (never saved, logged or put in
memory or support bundles), the picture goes to the Thinking model only when a
look happens, and the model is told never to identify people or comment on
bodies, looks or clothes. Tell anyone in view that the camera is watching.

## Full-screen games

Martlet reads the monitor through the **DXGI Desktop Duplication API**: a copy
of exactly what the monitor shows. Microsoft documents that it duplicates "even
full screen DirectX applications", so full-screen games are seen without
switching them to borderless. It is a documented Windows API, not a hook: Martlet
never injects into or reads from the game process, so there is nothing for an
anti-cheat to flag. (OBS's *Game Capture* works differently, by injecting a DLL
into the game. Martlet deliberately does not do that.)

| Game mode | Seen? |
| --- | --- |
| Windowed / borderless | Yes |
| Full screen (DirectX 11/12, Vulkan, OpenGL on Windows 10/11) | Yes |
| Legacy exclusive full screen (for example DirectX 9, or fullscreen optimizations turned off) | Yes in most cases. While the game switches display mode, Windows resets the duplication; Martlet reopens it on the next look |
| Protected video (DRM), windows that exclude themselves from capture, UAC prompts | No: Windows returns black and the look is skipped |

The duplication stays open only while watching (one per monitor for the whole
screen). Each look copies the newest frame on the GPU and downscales it, which
takes about 15 ms at 1080p on the test machine, every 3 seconds. Unlike a GDI
screen read, it does not stall the game's rendering. A fresh duplication's
first frame can be black on some drivers, so Martlet waits for a second frame
when it opens the duplication.

When duplication is unavailable, Martlet falls back to GDI, which sees windowed
and borderless games only. The watch status then says why duplication is off:

- **Laptops with two GPUs:** duplication must run on the GPU that drives the
  monitor. Martlet opens it on that GPU, but if Windows still refuses, set
  Martlet to **Power saving** in Windows Settings > System > Display >
  Graphics.
- A **rotated** monitor or an unusual desktop pixel format.
- Another app already using the maximum number of duplications.

## What processes the images?

The **Thinking model itself** (single stage): the image rides with the current
message on the same route that answers you. There is no separate captioning
model. The routes carry it like this:

| Thinking route | How the image is sent |
| --- | --- |
| OpenAI (`gpt-4.1-mini`, `gpt-4.1`) | Responses API `input_image` data URL |
| OpenRouter / NVIDIA Build / any OpenAI-compatible Chat Completions (including a local Ollama, LM Studio or llama.cpp server on this PC) | `image_url` content part with a data URL |
| Ollama on a paired Martlet host | Gateway `images` field (one base64 JPEG/PNG, at most 1 MiB), relayed to Ollama's `/api/chat` `images` on the current message |

An image always needs its own permission (`AllowImageDisclosure`); text
permission never covers it. The old isolated `Martlet.Perception` P01/P02
foundations remain unwired; this feature does not use them.

## Which models can see?

| Setup | Sees images? |
| --- | --- |
| OpenAI `gpt-4.1-mini` / `gpt-4.1` | Yes |
| NVIDIA Build `google/diffusiongemma-26b-a4b-it`, OpenRouter `google/gemma-4-26b-a4b-it` (the prefilled defaults) | Yes |
| Host Ollama `gemma4:e2b`, `gemma4:e4b`, `qwen3-vl:8b`, `gemma4:12b`, `gemma4:26b` | Yes, and they call tools (now the suggested host models) |
| Host Ollama `gemma3:4b`, `qwen2.5vl:7b`, `gemma3:12b`, `gemma3:27b` | Yes (no tool calling) |
| Host Ollama `llama3.2:3b`, `qwen2.5:7b`, `llama3.1:8b`, `qwen2.5:14b` | **No, text-only** |
| Chat Completions | Depends on the model: names with `vl`/`vision`, `gemma-3` (4B+), `gemma-4`, `gpt-4o`/`4.1`/`5`, `gemini`, `claude`, `pixtral`, `llama-4`... are recognized; others are *unknown* |
| A model its named endpoint has retired (NVIDIA Build `meta/llama-3.3-70b-instruct`) | **No**: it no longer answers at all |

`VisionModelCatalog` classifies the configured model as **Supported**,
**Unsupported** or **Unknown**. There is no shared capability-discovery API
across these routes, so it is a curated name list, not proof.

### Vision on NVIDIA Build's Free Endpoints

Checked on 2026-10-01 with a free developer key: one 1024 px JPEG game HUD in a
streamed Chat Completions request shaped like Martlet's (persona as `system`,
the image as an `image_url` data URL, and the same request with a tool). Every
model below read the HP, gold, quest text and both shapes correctly.

| Model | Image reply (median) | Text reply | Tool call | Notes |
| --- | --- | --- | --- | --- |
| `google/diffusiongemma-26b-a4b-it` | ~0.5 s | ~0.4 s | Yes | **Default.** Answers `[pass]` on a dull screen and remarks on a boss win |
| `meta/llama-3.2-11b-vision-instruct` | ~3 s | ~1 s | Yes | Older; weaker remarks |
| `meta/llama-3.2-90b-vision-instruct` | ~7 s | ~6 s | Yes | Truncated tool arguments |
| `meta/muse-glimmer-30b`, `z-ai/glm-5.3-flash` | 2-6 s | 5-12 s | Yes | Reasoning models: empty glances within a 256-token reply cap |
| `moonshotai/kimi-k3`, `deepseek-ai/deepseek-v4.1-flash`, `google/gemma-4-31b-it` | 10-35 s | slow | Yes | Too slow for talking; Gemma 4 31B also timed out |

Gone (HTTP 410) or not found on that date: `meta/llama-3.3-70b-instruct`,
`meta/llama-4-maverick-17b-128e-instruct`, `microsoft/phi-4-multimodal-instruct`,
`nvidia/nemotron-nano-12b-v2-vl`, `google/gemma-3-4b-it`, `google/gemma-3-12b-it`,
`microsoft/phi-3-vision-128k-instruct` and `moonshotai/kimi-k2.6`. NVIDIA
changes this list often; build.nvidia.com marks the free ones *Free Endpoint*.

### Local models that see

| Ollama tag | GPU memory | Why |
| --- | --- | --- |
| `gemma4:e2b` | ~5 GB (also runs on the CPU) | small, talks, sees and uses tools; the default |
| `gemma4:e4b` | ~7 GB | a smarter talker for 12 GB graphics cards |
| `qwen3-vl:8b` | ~7 GB | best at reading on-screen text and game HUDs at this size |
| `gemma4:12b` | ~9 GB | a smarter talker that also sees, for 16 GB graphics cards |
| `gemma4:26b` | ~19-20 GB | strongest single-GPU option, and quick (4B active parameters) |

These are one model that both talks and sees (and calls tools for Smart home
and MCP), so a host does not need a second model or more GPU memory for
vision. Companion › Thinking › This PC, the prerequisites tool and the host's
Ollama role all suggest them by GPU memory. On this PC the suggestion leaves
about 5 GB of the card for a game and Martlet's character (a 12 GB card gets
`gemma4:e4b`, a 16 GB card `gemma4:12b`). Existing hosts keep their model
until you add the Thinking role again (Devices page) and pick one; the host
must also run this Martlet version so its gateway accepts images.

## Incompatibility warnings

Companion › Vision always states the result for the **current** Thinking selection:

- **Ready:** the model sees; screenshots go to the named destination.
- **Can't see yet:** the model is text-only. **Turn vision on** stays
  disabled (and the talk window's button says *Can't see*); the message names the fix for your route: on a host, add the
  Thinking (Ollama) role again with `gemma4:e2b` / `gemma4:e4b` / `qwen3-vl:8b`;
  on Chat Completions, pick a vision model on the endpoint (the named
  endpoint's recommended model is named) or run one locally;
  or switch Thinking to OpenAI `gpt-4.1-mini`. Talking keeps working (unless
  the endpoint has retired the model, which the message says instead).
- **Not sure:** an unknown Chat Completions model. You may try; if the model
  rejects the first screenshot, watching stops (no automatic retry) and the
  message says it most likely can't see images, with the same fix.

Other edges: a host running an older Martlet refuses the larger request; the
look fails and the message says to update the host. A failed or expired look
never retries; it stops looking and the talk window says why. A settings change
made elsewhere (a synced change or a failover) restarts looking with the new
choices once Martlet is free.

## Limits and what is not done

- Full-screen capture was checked with Desktop Duplication on a 1080p desktop
  over Remote Desktop, not yet with a real full-screen game, HDR or a two-GPU
  laptop.
- Keyboard/mouse idle only: a controller-only player is "away" after 5 minutes
  *if the picture is also still*; a moving game keeps watching.
- Window-title privacy filtering is a keyword list; anything else in the
  captured window can reach the model when a look happens.
- No separate "eyes" model (describe with model A, talk with model B). A
  text-only Thinking model has to be swapped for a vision one.
- Real capture on a game and a real vision model reply were not run in the
  change that added this; see the pull request for what was checked.
- Pop-up notification detection relies on the shell's window class and was not
  seen firing on the test machine (Windows held pop-ups back there); taskbar
  flashes were checked with a test window. Apps that only badge their taskbar
  icon, without flashing or a pop-up, are seen only when a look happens.
- Several monitors share one picture of at most 2048 px, so each shows smaller
  than a single monitor would.
- Camera capture was checked with a local video file and HTTP snapshot/MJPEG
  test servers, not yet with a physical webcam, Phone Link camera or RTSP
  camera.
