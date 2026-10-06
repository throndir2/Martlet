# Watch my screen: screen-aware commentary

Martlet can watch what you are playing or doing and, now and then, say something
about it, like a friend in the room. It stays quiet most of the time. This page
explains who sees the pictures, which models can, and what Martlet tells you when
your setup can't.

## How it works

1. **Companion › Vision** shows whether your Thinking model can see (see
   below), what Martlet looks at (**my whole screen**, the default: every
   monitor with the taskbar and pop-up notifications, **my active window**, or a
   camera) and
   **how chatty** it is (Quiet, Normal, Chatty or [Martlet
   decides](#martlet-decides-how-chatty-it-is); the same choice sets how often
   it reacts to [what this PC plays](CONVERSATION.md#hearing-what-this-pc-plays)),
   and says exactly what is captured and where it is sent.
2. Vision is on by default (**Turn vision off** in Companion stops it for
   good; a saved choice is kept). That only allows it: Martlet
   starts looking when you press **Start watching** (on Home, in the talk
   window or from the notification-area icon), with or without the talk window
   open, and Home's watching indicator, the talk window's **Stop watching**
   button and its title show it. It keeps going in the background (while you
   play) until you press **Stop watching**, Stop or Esc, pause Martlet, lock
   Windows (it carries on when you unlock) or end the conversation. Listening
   has its own Start listening / Stop listening button; neither starts or stops
   the other.
3. Every 3 seconds Martlet captures the screen **on this PC** (DXGI Desktop
   Duplication, falling back to GDI; kept only in memory) and compares a 16x9
   grey thumbnail with the last one to notice change. The active window is
   downscaled to at most 1024 px. The whole screen is every monitor side by
   side as Windows arranges them, each at most 1024 px and the picture at most
   2048 px, so the taskbar, the notification area and pop-up notifications are
   in it; it doesn't need a window in front (the desktop counts). The
   **Stop watching** button's dot blinks on each capture (it twinkles
   while a look is with the model), and a short line under the
   talk window's status says what it sees (*Watching your whole screen.*) and
   only what changes that: a look in progress, why it is holding off (you seem
   away, the hourly budget is used, a busy provider) or a failed look. Its
   tooltip says how the last look went (*nothing to say*, *commented*), how
   many monitors the whole screen spans and what wanted your attention but
   wasn't looked at.
   Captures are never added to the history; what Martlet saw in them is (see
   [What Martlet saw stays in the conversation](#what-martlet-saw-stays-in-the-conversation)).
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
     look is one model request; while Martlet decides, the level it picked sets
     these, so it may use up to Chatty's 45 (what Companion discloses).
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

## Martlet decides how chatty it is

**How often it comments** has a fourth choice, **Martlet decides** (the same
choice appears under Listening › *Watch along*). Martlet then picks Quiet,
Normal or Chatty itself and switches as things happen: it goes quiet when you
are focused, busy, on a call, watching closely, seem tired of its remarks or ask
it to hush; it gets chatty when you invite its reactions, ask what it thinks,
play or watch something together or things get exciting; and it settles back to
normal. Asking for more or less talk switches it right away. It starts at
Normal and keeps the level it picked until Martlet closes.

- **How it is told.** While vision or hearing what this PC plays is turned on,
  every glance, every reply to what the PC plays and every reply to what you
  type or say gets Companion › Prompts › *Chattiness: Martlet decides* (what
  the three levels mean, when to switch and how), in place of a fixed level's
  line. It is the same at every level, so the instructions, and the model's
  prompt cache, stay the same when it switches. The level itself goes in the
  message's notes (*Chattiness right now*), only when the conversation's notes
  don't already say it.
- **How it switches.** A reply or look ends with a tag such as
  `[chattiness:quiet]` (`[chattiness:normal]`, `[chattiness:chatty]`; a space
  after the colon and any case work), after its last sentence or after
  `[pass]`. The tag goes at the end so the first words are never held back:
  as soon as what follows a finished sentence can only be a chattiness tag
  (`[cha...`), that sentence goes to the voice without waiting for the rest of
  the tag. It is never shown, spoken, captioned or kept in the history (the runtime's
  control tags: `ConversationRequest.ControlTags`, reported in
  `ConversationTurn.Controls`). The last tag of a reply whose words all
  arrived wins; a reply cut off before then switches nothing.
- **What follows.** The pacer retunes on the spot (the looks already taken
  still count toward the new hourly budget), what the PC plays on its own goes
  to Thinking every 45 / 20 / 12 seconds at Quiet / Normal / Chatty, the talk
  window's history notes the switch (*Martlet went quiet about what it sees and
  hears.*), its `LiveChattiness` line says *Chattiness: quiet (Martlet
  decides).* (its tooltip says when it switched), Companion says the level it picked, and
  the desktop log writes *Chattiness: Martlet went from normal to quiet (your
  message; Martlet decides).*
- **Cost.** A switch adds a few tokens to the end of a reply and nothing before
  its first word. The prompt adds a few hundred tokens to the instructions,
  which the prompt cache keeps; Companion's disclosure counts Chatty's budget.

## Martlet sees what you see when you talk to it

While vision is on, everything you type or say also goes with the **newest
picture** of what Martlet watches (taken in the last 10 seconds; a skipped
capture, say a private window in front, sends none), so you can ask *"what do
you think of this?"*, *"who just messaged me?"* or *"how do I beat this boss?"*.
The reply is told the picture is what you see right now and to use it only when
it helps, without describing it unprompted. Your message's bubble says *Martlet
saw your whole screen.* (or your active window, or the camera). These pictures don't count
toward the looks per hour, but they make each reply's request larger, which may
cost more. If the Thinking model rejects the picture, Martlet asks again with
your words only and says so on your message; a model Martlet doesn't know can
see also stops vision with the fix, like a rejected look. Memory never gets the
picture.

## What Martlet saw stays in the conversation

Pictures are never kept, but what Martlet saw in them is, so later replies know
what was on screen (*"what was that game I was playing?"*) and the companion
answers your words, what the PC plays, what it sees and finished background
work as one conversation.

- **Every look**, passed or not, becomes one exchange in the conversation the
  next replies send: a line that starts with `[Screen]` (or `[Camera]`) and says
  where Martlet looked and what it saw, then its remark or `[pass]`, such as
  *[Screen] You looked at the user's active window "Program.cs - Visual Studio
  Code": a code editor, a build running.* The source and the window's title (or
  the camera's name) are cleaned the way the look's prompt gets them; a look a
  notification or a flashing taskbar button started says so in brackets.
- **Passes don't pile up.** A look Martlet passes on takes the place of the
  exchange just before it when that is also a passed look, so a quiet stretch
  keeps only its last look; a remark (or anything said in between) ends the
  stretch. Only the end of the next request changes, which is new anyway, so
  the prompt cache keeps the start.
- **A message with a picture** keeps a line after its words: *[Screen] With
  this message you saw the user's whole screen (active window "Discord"): a
  chat app with a new message.* A picture the model rejected leaves no line.
- **What it saw** comes from the reply itself, adding no wait: a look, and a
  reply whose message came with a picture, is told (Companion › Prompts ›
  *What you saw*, the same on every request, so the instructions stay the same)
  to end with `[seen: a few words]`, after its last sentence or after
  `[pass]`. It is a control tag like the chattiness tags: never shown, spoken,
  captioned or kept as Martlet's words, and as soon as what follows a finished
  sentence can only be it (`[see...`), that sentence goes to the voice. Its
  words (one line, at most 120 characters) go in the `[Screen]` line instead.
  Empty the prompt and the line says only where Martlet looked.
- **Never your words.** Like `[PC audio]` lines, `[Screen]` and `[Camera]` lines
  are never read as what you said: memory, learning names, the record of
  conversations and the smart home leave them out (memory reads only the latest
  exchange; earlier lines are context).
- **Seeing it.** The talk window still hides passed looks. The desktop log writes
  *Vision: the conversation keeps a screen glance (passed, described, in place of
  the passed look before it).* (never the title or the words), and Martlet MCP's
  `vision_history_check` rehearses it all with the production code
  ([MCP](MCP.md)).

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

## Where the character looks

The character's head and eyes follow your mouse. Companion › Vision › **Where
the character looks** can instead let **Martlet decide** (off by default; saved
on this PC as `DecideGaze` in `talk-preferences.json`). While vision watches your
active window or whole screen and the character shows, every new screenshot (every
3 seconds) is a chance to look somewhere else:

- **Something new in one place.** Martlet compares the screenshot with the one
  before it as a 32×18 grid of average greys (far too coarse to carry content;
  on this PC only, never sent) and glances for 2.5 seconds at a change that
  stands out: a notification popping up, a new chat line, a window opening. It
  doesn't when much of the picture changed at once (a new scene, scrolling,
  another window in front), when things changed all over (an animated page),
  when the change was right by your mouse (the eyes are already there) or when
  only the character moved: under its overlay only a strong change counts (a
  notification behind it does, its own breathing and head turns don't), and its
  speech bubble and menus never do. It glances at most every 6 seconds and tires
  of a spot that keeps changing (a video): looking there again waits 20 seconds,
  then 40 and so on, up to two minutes.
- **What the Thinking model picks.** A look Martlet already takes (above) also
  offers nine look tags, `{look top left}` to `{look bottom right}`, for the
  ninths of the picture (Companion › Prompts › *Where the character looks*;
  empty it to turn this off). A tag at the start of the answer turns the
  character's eyes to the middle of that part of the screen for 6 seconds,
  timed with the remark, and works with `[pass]` too, so Martlet can look at
  something without saying anything. Tags are never shown, spoken or kept in the
  conversation. They are offered only when they fit beside the character's emote
  tags (128 at most) and never with replies to you, so the time to Martlet's
  first word and the conversation's prompt cache don't change.
- Otherwise, **your mouse**.

No extra request is sent and nothing leaves this PC for it beyond the looks
vision already takes. With a camera the character follows your mouse. The talk
window's line under the vision status says what the eyes are on and when they
last looked away (*Glancing at something new at the bottom right of your
screen.*, *Looking at your mouse: much of the screen changed at once.*).

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
| `gemma4:e2b` | ~5 GB (also runs on the CPU) | small, talks, sees, hears and uses tools; the default |
| `gemma4:e4b` | ~7 GB | a smarter talker for 12 GB graphics cards |
| `qwen3.5:4b` | ~4 GB | a smarter small model that sees and uses tools on this PC, but doesn't hear (replies get the transcript) |
| `gemma4:12b` | ~9 GB | a smarter talker that also sees, for 16 GB graphics cards |
| `gemma4:26b` | ~19-20 GB | strongest single-GPU option, and quick (4B active parameters) |

`qwen3-vl:8b` is no longer suggested on this PC: in Ollama 0.35 it keeps
thinking with Thinking steps Off (`reasoning_effort: none` and `think: false`
alike), so replies start only after seconds of hidden reasoning. The host's
Ollama role (`ollama/ollama:0.34.4`) still offers it.

These are one model that both talks and sees (and calls tools for Smart home
and MCP), so a host does not need a second model or more GPU memory for
vision. Companion › Thinking › This PC, the prerequisites tool and the host's
Ollama role all suggest them by GPU memory. On this PC Companion › Thinking
recommends `gemma4:e2b` on every card (the fastest replies) and names the
largest that fits as the smartest (a 12 GB card `gemma4:e4b`, a 16 GB card
`gemma4:12b`), each leaving about 5 GB of the card for a game and Martlet's
character. Existing hosts keep their model
until you add the Thinking role again (Devices page) and pick one; the host
must also run this Martlet version so its gateway accepts images.

## Incompatibility warnings

Companion › Vision always states the result for the **current** Thinking selection:

- **Ready:** the model sees; screenshots go to the named destination.
- **Can't see yet:** the model is text-only. Vision may be on (it is by
  default), but **Turn vision on** stays disabled once it is off, Home shows a
  *Martlet can't see with your thinking model* warning while it is on, and the
  talk window's button says *Can't see*; the message names the fix for your route: on a host, add the
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
never retries; it stops watching, the talk window and Home's watching indicator
say why, and **Start watching** tries again. A settings change
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
