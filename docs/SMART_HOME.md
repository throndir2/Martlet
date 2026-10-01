# Smart home and cameras: research and plan

**Research and plan, 2026-09-30. No smart home or camera code exists yet;
everything below is dated upstream research and design. Every device result is
NOT RUN.** The owner asked whether Martlet can support smart home technology
such as Matter, which other stacks exist, whether IP security cameras fit, and
how a user can optionally let the companion act as a smart home assistant
("turn the lights down", "is the garage open?", "who's at the door?").

This is a separate post-MVP track, like the [iOS plan](IOS.md). It touches an
MVP non-goal, **general tool execution**
([non-goals](../DEVELOPMENT_PLAN.md#explicit-non-goals-for-mvp)): the plan
below adds a narrow, typed, permission-gated home tool surface, not arbitrary
tool or command execution.

## Short answer

1. **Connect to Home Assistant first; do not write a Matter controller.**
   Home Assistant (Apache-2.0) already speaks Matter, Thread, Zigbee, Z-Wave,
   Hue, Shelly, ESPHome, cameras and thousands of other integrations. Since
   2025 it ships an official **Model Context Protocol Server** integration at
   `/api/mcp` (Streamable HTTP, long-lived token or OAuth) that exposes its
   Assist API as MCP tools, limited to the entities the user exposes to voice
   assistants. One Martlet integration therefore reaches almost every device a
   user owns, and Home Assistant stays the authority on what the companion may
   touch.
2. **Martlet needs two new capabilities for any of this:** LLM tool calling
   (today both chat adapters send `tools:[]` and `tool_choice:"none"`, see
   [Providers](../src/Martlet.Providers/README.md)) and an MCP *client*
   (`Martlet.Mcp` is a server only). Both are reusable beyond the smart home.
3. **Native Matter later, as an optional host role, not in .NET.** There is
   no mature .NET Matter controller. The maintained open-source controller is
   the Open Home Foundation **matterjs-server** (Node.js, Apache-2.0, Matter
   1.6, WebSocket API, beta, drop-in successor to the archived
   python-matter-server). A Linux host could run it as a container, and Martlet
   would join devices as an extra admin using a pairing code shared from
   Apple/Google/Alexa/Home Assistant (multi-admin), which avoids Bluetooth and
   Thread hardware on the PC.
4. **IP cameras overlap directly with video source input.** A camera is one
   more frame source for the existing glance/pacer/vision path in
   [screen commentary](SCREEN_COMMENTARY.md). Pull a JPEG snapshot from
   **go2rtc** (`/api/frame.jpeg`, MIT), **Frigate** (MIT NVR with object
   detection, MQTT events and generative-AI review summaries) or Home
   Assistant's camera snapshot endpoint, rather than decoding RTSP inside
   Martlet. Matter 1.5 cameras (WebRTC) arrive through the same bridges.
5. **The persona acts only when the user asks.** Tool calls are allowed only
   on turns started by the user's own speech or typing, never from a screen or
   camera look, and locks, garage doors, alarms and covers need a spoken
   confirmation each time (or stay unexposed). See [Safety](#safety-and-consent).

## Technology landscape

| Stack | What it is | Fit for Martlet | Notes |
| --- | --- | --- | --- |
| **Home Assistant MCP server** | Official HA integration; Assist API tools over MCP Streamable HTTP; `GetLiveContext` snapshot resource | **First choice** | Exposed-entities page is the allowlist; "Control Home Assistant" option can make it read-only. Sampling and notifications unsupported, so no push events over MCP |
| Home Assistant REST/WebSocket API | `/api/states`, `/api/services`, WebSocket event subscriptions, `/api/camera_proxy/<entity>` | Second channel | Needed for push events (doorbell, motion) and camera snapshots, which MCP does not stream |
| **Matter** (CSA) | IP-based device standard; 1.5 (Nov 2025) added cameras over WebRTC, closures, energy; 1.5.1 (Mar 2026) camera fixes; 1.6 (Jun 2026) NFC commissioning, Joint Fabric, thermostat suggestions | Via HA now; matterjs-server host role later | Commissioning needs BLE (or NFC on 1.6) and, for Thread devices, a Thread border router (HomePod/Apple TV, Nest hub, recent Echo, OpenThread BR). Multi-admin pairing codes sidestep both |
| matterjs-server / matter.js | OHF Node.js controller with WebSocket API (`@matter-server/ws-client`) | Optional host container | Beta, not yet re-certified by the CSA; Node 22.13+/24 |
| connectedhomeip | Official C++ Matter SDK | Not recommended | No .NET bindings; heavy native build |
| **Thread** | Low-power IPv6 mesh under Matter | Indirect | PCs rarely have a Thread radio; rely on an existing border router |
| **Zigbee** | Mesh radio (Hue, IKEA, Aqara) | Via HA (ZHA) or Zigbee2MQTT | Zigbee2MQTT is GPL-3.0: talk to it over MQTT as a separate service, never bundle |
| **Z-Wave** | Sub-GHz mesh (locks, sensors) | Via HA or Z-Wave JS UI WebSocket | Needs a USB stick |
| **MQTT** | Pub/sub bus (Mosquitto) used by Zigbee2MQTT, Frigate, Shelly, Tasmota | Event source | Useful for "react when the doorbell rings"; plain client library in .NET |
| ESPHome | DIY ESP32 devices, native protobuf API | Via HA | |
| Philips Hue | Bridge with local CLIP v2 HTTPS API and event stream | Possible direct adapter | Link-button pairing; local only |
| Shelly Gen2+ | Local HTTP/WebSocket RPC and MQTT | Possible direct adapter | |
| Google Home APIs | Device, Automation and Commissioning APIs | Not on Windows | Android (Kotlin) and iOS (Swift) SDKs only; relevant to the [iOS track](IOS.md) |
| Apple HomeKit | Home framework | iOS track only | Controller access exists only inside Apple apps; Martlet iOS could read/control Home accessories with the user's grant |
| SmartThings | Cloud REST API (token/OAuth); Home API preview for Matter on Android | Optional cloud adapter | Sends home data to Samsung's cloud |
| Amazon Alexa | Skills are inbound (Alexa calls you) | No | No general consumer control API |
| Google Nest (Device Access/SDM) | Cloud API incl. WebRTC camera streams | Not planned | One-time registration fee (spending); HA already integrates it |
| openHAB / Hubitat / Homey | Alternative hubs with REST APIs | Later, by demand | Same MCP/REST pattern if they expose one |
| Wyoming protocol | HA's TCP protocol for wake word/STT/TTS satellites | Reverse direction, later | Could let HA voice satellites use Martlet's voices, or Martlet's listening. Does not give Martlet device control |

### IP security cameras

| Route | How Martlet gets a frame | Pros | Cons |
| --- | --- | --- | --- |
| **go2rtc** (MIT, single binary) | `GET /api/frame.jpeg?src=<name>` | Any RTSP/ONVIF/HomeKit/vendor stream; WebRTC/two-way audio for later live view | Another service to run (host container or the user's own) |
| **Frigate** (MIT NVR) | `/api/<camera>/latest.jpg`; MQTT `frigate/events`; GenAI review summaries (0.17+) | Person/car/package detection gives *meaningful* triggers instead of polling; works with Ollama | GPU/Coral recommended; Docker host |
| **Home Assistant** | `/api/camera_proxy/<entity_id>` (REST, token) | Zero extra setup if HA already has the camera, including Ring/Nest/Reolink/Tapo integrations | Snapshot quality and latency vary by integration |
| Direct RTSP/ONVIF in Martlet | FFmpeg/libVLC decode on the PC | No middleman | Codec/licensing/CPU cost, credential handling and per-brand quirks Martlet would own; not recommended |
| Matter 1.5 cameras | WebRTC via a Matter controller | Standard, cross-ecosystem | Very few shipping devices yet; comes "for free" through HA/matterjs-server |

All three recommended routes are **pull a JPEG on demand**, which matches the
screen watcher's one-image-per-look design and its image disclosure permission
(`AllowImageDisclosure`). Vendor clouds without local APIs (Ring, Arlo, Wyze)
are reached only through Home Assistant integrations, never scraped.

## Overlap with video source input

Screen commentary already has the pieces a camera needs: a capture source,
a change detector (16x9 thumbnail), a pacer, one JPEG per look, `[pass]`
handling and the vision-capable Thinking route. A camera should be a new
**frame source** behind the same seam, not a second pipeline:

| Concern | Screen (today) | Camera (planned) |
| --- | --- | --- |
| Source | DXGI Desktop Duplication / GDI (`ScreenGlancer`) | HTTP snapshot from go2rtc, Frigate or HA, keyed by a camera ID |
| Trigger | Pacer: change + time + presence | Pacer, **or** an event (Frigate MQTT object event, HA doorbell/motion state) |
| Prompt context | Window title, "friend in the room" | Camera name/area, event label ("person at front door"), "assistant at home" |
| Privacy filter | Window-title keyword list | Per-camera opt-in; bystander notice; never record |
| Output | Remark or `[pass]` | Remark, `[pass]`, or (only on user request) a home action |

Whatever abstraction the video-source work introduces should accept a
**pull-based snapshot source** with a stable ID and display name, so cameras
plug in without touching the pacer or the vision routes.

## Model requirements

Tool calling and vision are separate model abilities. Verified on
ollama.com on 2026-09-30:

| Ollama tag | Vision | Tools |
| --- | --- | --- |
| `gemma3` (`gemma3:4b` is today's suggested easy default) | Yes | **No** |
| `qwen2.5vl` | Yes | **No** |
| `qwen3-vl` | Yes | Yes |
| `gemma4` | Yes | Yes |

OpenAI `gpt-4.1`/`gpt-4.1-mini` support both. A `ToolModelCatalog` (same
curated Supported/Unsupported/Unknown pattern as `VisionModelCatalog`) must
drive the same kind of "can't control your home yet" warning that screen
watching shows for text-only models, and the host model suggestions should
move to a model that sees **and** calls tools.

## Proposed architecture

```mermaid
flowchart LR
    User["User speech / typing"] --> Turn["Conversation turn"]
    Glance["Screen / camera look"] --> Turn
    Turn --> LLM["Thinking model\n(tools enabled only on user turns)"]
    LLM -->|"tool call"| Gate["Home policy gate\n(allowlist, tiers, confirmation, audit)"]
    Gate --> Client["MCP client"]
    Client -->|"Streamable HTTP + token"| HA["Home Assistant /api/mcp"]
    HA --> Devices["Matter / Zigbee / Z-Wave / Wi-Fi devices"]
    Events["HA WebSocket / Frigate MQTT events"] --> Glance
    Cams["go2rtc / Frigate / HA snapshots"] --> Glance
```

- **`Martlet.Home` library (new):** MCP client over Streamable HTTP (reusing
  `Martlet.Mcp.Protocol` JSON-RPC types where they fit), tool discovery and
  caching, the policy gate and a local action log. The HA token lives in the
  Windows credential vault (`Martlet.Credentials.Windows`), never in prompts,
  settings exports or support bundles.
- **Providers:** add an opt-in tools path to the OpenAI Responses, Chat
  Completions and Ollama `/api/chat` encoders and parse streamed tool calls;
  the default stays `tool_choice:"none"`. Gateway relay of `tools` to a host's
  Ollama follows the screen-commentary `images` precedent.
- **Conversation:** a bounded tool loop (at most a few calls per turn, a short
  deadline, cancellation by Stop/Esc/barge-in), then the spoken reply in the
  persona's voice ("Done, the living room is at 30%").
- **Events:** subscribe to selected HA entities (doorbell, motion, door open)
  over the HA WebSocket API and, optionally, Frigate MQTT. An event can start a
  camera look or a short remark; it can never start an action.
- **Host role (later):** optional `home` role containers on a Linux host:
  go2rtc and/or matterjs-server, for users without Home Assistant. Users with
  HA should keep HA as the hub.

## Safety and consent

- **Off by default.** Connecting a home hub, enabling control, and enabling
  each camera are separate explicit settings; read-only status can be enabled
  without control.
- **Home Assistant's exposed entities are the outer allowlist.** Martlet adds
  tiers on top:
  - *Read:* states, sensors, "is the door locked?"
  - *Comfort:* lights, media, fans, climate within a range, scenes.
  - *Sensitive:* locks, garage doors, covers, alarm panels, valves, anything
    that opens the home: **disabled by default**; when enabled, each action
    needs a spoken or clicked confirmation that names the device.
- **No autonomous actions.** Tools are offered to the model only on turns the
  user started by speech, typing or push-to-talk. Screen looks, camera looks,
  events and memory recall never get tools: on-screen text, a camera image or
  a TV in the room can contain instructions (prompt injection), and Martlet has
  no speaker identification, so anyone in the room can talk to it.
- **Visible and reversible.** Every action is shown in the conversation and in
  a local action log, spoken back, and stopped by Stop/Esc. Martlet never
  creates or edits Home Assistant automations, users or integrations.
- **Cameras show other people.** Each camera needs its own opt-in, frames are
  never saved, logged or put in memory, and sending a frame to a cloud model
  uses the same image disclosure permission and destination wording as screen
  watching. Prefer a local vision model for cameras; the setup should say so.
- **Network:** connect only to the user-entered HA/go2rtc/Frigate URL; HTTPS
  or a LAN address; no port opening, cloud relay or remote access setup by
  Martlet.

## Delivery slices

| Slice | Outcome | Depends on |
| --- | --- | --- |
| SH01 | Opt-in tool calling in the three chat encoders + streamed tool-call parsing + `ToolModelCatalog`; default unchanged | - |
| SH02 | `Martlet.Home` MCP client: connect to HA `/api/mcp` with a vaulted token, list tools, read `GetLiveContext`; Devices page "Smart home" card with connection status | SH01 |
| SH03 | Policy gate, tiers, confirmation prompt, action log; tools only on user-started turns; spoken result | SH02 |
| SH04 | Camera frame source (HA camera_proxy, go2rtc, Frigate snapshot) behind the video-source seam; "Look at the front door" on request | video source input, SH02 for HA |
| SH05 | Event-triggered looks/remarks from HA WebSocket and optional Frigate MQTT (doorbell, person detected); never actions | SH04 |
| SH06 | Host `home` role: go2rtc and matterjs-server containers; multi-admin Matter pairing from a shared code | SH02, host roles |
| SH07 | Direct adapters only if users lack HA: Hue CLIP v2, Shelly RPC, MQTT | SH03 |
| SH08 | iOS track: HomeKit read/control through the Home framework | [iOS plan](IOS.md) |

SH01-SH03 deliver "my waifu turns off the lights" for anyone with Home
Assistant. SH04 is the camera overlap and should land after (or together with)
the video source abstraction.

## Owner decisions

- **Accept Home Assistant as the hub** for v1 (recommended) rather than a
  native Matter controller. Users without HA would install HA (or the SH06
  host role) first.
- **Relaxing the "general tool execution" non-goal** to this narrow, typed,
  user-turn-only home tool surface.
- **Default host vision model** change from `gemma3`/`qwen2.5vl` to a
  vision + tools model (`qwen3-vl` or `gemma4`) once SH01 lands.

## Not planned

- Writing a Matter, Thread, Zigbee or Z-Wave stack in .NET.
- Autonomous routines decided by the companion, editing HA automations, or
  any action triggered by a screen, camera, event or memory.
- Recording, storing or searching camera footage (that is an NVR's job).
- Face recognition or identifying people on camera.
- Cloud-relay or remote access to the home from outside the LAN.

## Sources

Accessed 2026-09-30. Summary in the
[research ledger, S49](RESEARCH.md#s49-smart-home-matter-and-ip-camera-research-2026-09-30).

- Home Assistant MCP Server integration:
  <https://www.home-assistant.io/integrations/mcp_server/>
- Home Assistant LLM API developer docs:
  <https://developers.home-assistant.io/docs/core/llm/>
- Home Assistant REST and WebSocket APIs:
  <https://developers.home-assistant.io/docs/api/rest/>,
  <https://developers.home-assistant.io/docs/api/websocket/>
- CSA Matter 1.5 and 1.6 announcements: <https://csa-iot.org/newsroom/>
- matterjs-server (Open Home Foundation): <https://github.com/matter-js/matterjs-server>
- python-matter-server (archived): <https://github.com/home-assistant-libs/python-matter-server>
- Matter SDK: <https://github.com/project-chip/connectedhomeip>
- go2rtc: <https://github.com/AlexxIT/go2rtc>
- Frigate: <https://docs.frigate.video/>
- Wyoming protocol: <https://github.com/OHF-Voice/wyoming>
- Zigbee2MQTT: <https://www.zigbee2mqtt.io/>; Z-Wave JS UI: <https://github.com/zwave-js/zwave-js-ui>
- Philips Hue CLIP API v2: <https://developers.meethue.com/develop/hue-api-v2/>
- Shelly Gen2+ API: <https://shelly-api-docs.shelly.cloud/gen2/>
- Google Home APIs: <https://developers.home.google.com/apis>
- SmartThings API: <https://developer.smartthings.com/docs/api/public>
- Apple HomeKit: <https://developer.apple.com/documentation/homekit>
- Ollama model capabilities: <https://ollama.com/library/qwen3-vl>,
  <https://ollama.com/library/gemma4>, <https://ollama.com/library/gemma3>
