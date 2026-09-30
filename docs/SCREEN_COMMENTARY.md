# Watch my screen: screen-aware commentary

Martlet can watch what you are playing or doing and, now and then, say something
about it, like a friend in the room. It stays quiet most of the time. This page
explains who sees the pictures, which models can, and what Martlet tells you when
your setup can't.

## How it works

1. In **Talk with Martlet**, the **Watch my screen** panel shows whether your
   Thinking model can see (see below), what Martlet looks at (**my active
   window** or **my whole screen**, the monitor your active window is on) and
   **how chatty** it is (Quiet, Normal, Chatty).
2. Tick the screen permission and click **Start watching**. A red dot and the
   window title show that watching is on. It keeps going in the background
   (while you play) until you click **Stop watching**, Stop, Esc, pause, mute,
   lock Windows or close the window. It never starts by itself and is never
   saved as on.
3. Every 3 seconds Martlet captures the screen **on this PC** (GDI, downscaled
   to at most 1024 px, kept only in memory) and compares a 16x9 grey thumbnail
   with the last one to notice change.
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
   in the last 30 minutes) or read out private details. `[pass]` is never
   spoken; a remark is spoken with the selected voice like any reply.
6. You come first: talking, typing or push-to-talk stops a remark in progress.
   With hands-free listening on, an idle listen (nobody speaking) briefly
   yields to a look and re-arms right after.

Never captured: Martlet's own windows, minimized windows, password managers and
private/incognito browser windows (by window title). Exclusive full-screen games
and protected video read back black; Martlet skips them and asks you to switch
the game to **borderless** or **windowed**. Screenshots are never saved, logged,
put in local memory or support bundles.

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
| Host Ollama `gemma3:4b`, `qwen2.5vl:7b`, `gemma3:12b`, `gemma3:27b` | Yes (now the suggested host models) |
| Host Ollama `llama3.2:3b`, `qwen2.5:7b`, `llama3.1:8b`, `qwen2.5:14b` | **No, text-only** |
| Chat Completions | Depends on the model: names with `vl`/`vision`, `gemma-3` (4B+), `gpt-4o`/`4.1`/`5`, `gemini`, `claude`, `pixtral`, `llama-4`... are recognized; others are *unknown* |

`VisionModelCatalog` classifies the configured model as **Supported**,
**Unsupported** or **Unknown**. There is no shared capability-discovery API
across these routes, so it is a curated name list, not proof.

### Local models that see

| Ollama tag | GPU memory | Why |
| --- | --- | --- |
| `gemma3:4b` | ~4 GB (also runs on the CPU) | small, talks and sees; the default |
| `qwen2.5vl:7b` | ~6-8 GB | best at reading on-screen text and game HUDs at this size |
| `gemma3:12b` | ~9-11 GB | a smarter talker that also sees |
| `gemma3:27b` | ~18-20 GB | strongest single-GPU option |

These are one model that both talks and sees, so a host does not need a second
model or more GPU memory for vision. The host's Ollama role now offers them and
suggests them by GPU memory. Existing hosts keep their model until you add the
Thinking role again (Devices page) and pick one; the host must also run this
Martlet version so its gateway accepts images.

## Incompatibility warnings

The panel always states the result for the **current** Thinking selection:

- **Ready:** the model sees; screenshots go to the named destination.
- **Can't see yet:** the model is text-only. **Start watching** stays
  disabled and the message names the fix for your route: on a host, add the
  Thinking (Ollama) role again with `gemma3:4b` / `qwen2.5vl:7b` / `gemma3:12b`;
  on Chat Completions, pick a vision model on the endpoint or run one locally;
  or switch Thinking to OpenAI `gpt-4.1-mini`. Talking keeps working.
- **Not sure:** an unknown Chat Completions model. You may try; if the model
  rejects the first screenshot, watching stops (no automatic retry) and the
  message says it most likely can't see images, with the same fix.

Other edges: a host running an older Martlet refuses the larger request; the
look fails and the message says to update the host. A failed or expired look
never retries. Setup, Audio and Reload are unavailable while watching; changing
voice output stops watching, and a settings change made elsewhere stops it at
the next look.

## Limits and what is not done

- GDI capture only: no Windows Graphics Capture/Desktop Duplication yet, so
  exclusive full-screen games and HDR/protected content are skipped, not seen.
- Keyboard/mouse idle only: a controller-only player is "away" after 5 minutes
  *if the picture is also still*; a moving game keeps watching.
- Window-title privacy filtering is a keyword list; anything else in the
  captured window can reach the model when a look happens.
- No separate "eyes" model (describe with model A, talk with model B). A
  text-only Thinking model has to be swapped for a vision one.
- Real capture on a game and a real vision model reply were not run in the
  change that added this; see the pull request for what was checked.
