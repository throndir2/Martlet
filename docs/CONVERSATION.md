# Explicit Desktop API conversation (V04b)

**Internal functional integration, not account/device/release qualification.**
**Start talking** on Home opens the talk window: the conversation history, what
you said and a message box, nothing else. It sits beside Martlet rather than
blocking it, so Home, Companion and Settings stay usable while you talk; Home's
button reads **Show conversation** while it is open and brings it back to the
front. How Martlet listens, speaks and sees is chosen in Companion (Listening,
Voice and Vision), and an open talk window follows a change there at once.
Always listening starts only when you press **Start listening** (on Home, in the
notification-area menu or in the window) and stops with **Stop listening**. It
doesn't need the talk window: Home's **Start listening** runs the conversation
hidden, Home's listening indicator says what it is doing (*Listening*, *Hearing
you…*, *Martlet is replying…* or why it can't listen), **Show conversation**
shows its history, and closing the window while Martlet listens or watches only
hides it (**End the conversation** in the notification-area menu ends it). A
Home Assistant or tool question shows the window. Settings › *Startup and
closing* › *When Martlet starts, show the character and start listening* does
both on every start, including Start with Windows in the notification area. A
PC used as a Martlet host never talks, listens or shows the character: switching
it to a host ends a running conversation and hides the character, and at start
it skips this choice, the character's *Show at startup* and Parakeet's warm-up
while keeping them saved for when it's your companion PC again. Opening setup never resolves a key,
enumerates devices, records, plays, discovers a model or makes an API request.
Ordinary Doctor/status remains read-only and is not a live connection test.

The character is shown and hidden from the main window. Its [feature guide](../src/Martlet.Avatar.Hosting/README.md)
describes separately permitted local renderer inspection and generated-speech
analysis. Opening it is passive; activation is never inherited from conversation
permission or persisted. Only accepted generated TTS PCM is observed, never mic
capture or token arrival. Avatar backpressure, missing actual device clock,
renderer failure and Audio2Face unavailability do not delay or fail voice.
Only explicit A2F mouth/expression mapping is currently wired; alternatives and
other aspects require explicit omission, not automatic fallback.

**Vision** (Companion › Vision, off by default) lets Martlet glance at your
active window, screen or a camera while the talk window is open and occasionally
comment; it needs a Thinking model that can see images. See
[Screen commentary](SCREEN_COMMENTARY.md).

## First configured action

1. In **Setup / resume**, choose the cloud API profile. Apply explicit
   supported model IDs, store each role's key in its scoped Windows vault target,
   then review that role's destination choice again (changing a key invalidates
   the choice). Save the checkpoint. Do not put keys in model fields or files.
2. The OpenAI LLM route supports `gpt-4.1-mini-2025-04-14` and
   `gpt-4.1-2025-04-14`. Alternatively the LLM can use OpenRouter, NVIDIA Build
   or any OpenAI-compatible Chat Completions endpoint with the exact model ID
   you enter (see [Setup](SETUP.md)); a local loopback server may be keyless.
   STT supports `gpt-transcribe`,
   `gpt-4o-transcribe`, `gpt-4o-mini-transcribe`,
   `gpt-4o-mini-transcribe-2025-12-15`, or `whisper-1`.
   Optional TTS supports `gpt-4o-mini-tts-2025-12-15`, voice `alloy` or `coral`,
   raw mono 24 kHz PCM16. Setup copies exact entries from local adapter catalogs
   and refuses unsupported IDs without replacing the prior route. These are
   adapter allowlists, **not verified account
   access, quality recommendations or automatically chosen defaults**.
3. For PTT or voice output, explicitly save the intended policies in **Audio
   setup (local only)**. Fixed input/output is recommended for predictable
   routing. A default-input choice resolves again only on a fresh press and
   stops on mid-capture change. Default output binds once and never moves an
   active stream. Local tests require their own permission and are not mandatory
   live-readiness gates.
4. Open **Start talking**. The destinations were confirmed when each job was
   chosen in Companion, so the window asks nothing more: pressing **Send**
   (or Enter), holding the talk button, or speaking while always listening is
   on is the action. Replies are spoken when a voice is set up and *Speak
   Martlet's replies aloud* is on (Companion › Voice); otherwise they are text
   only, with no TTS request and no output device.
5. Type and press Enter (Shift+Enter for a new line). With **Always listening**
   (Companion › Listening, the default once the microphone is tested) press
   **Start listening** and just speak; it keeps listening until you press
   **Stop listening**. With **Push-to-talk**, hold the talk button with the mouse or Space,
   then release to send (invoking it starts a recording and invoking it again
   sends). **Stop (Esc)** stays in the header at every size: it stops the reply,
   discards a recording instead of sending it, and pauses vision. It never
   stops always listening, so Martlet doesn't miss what you say next; only
   **Stop listening** does.
   Escape works anywhere in the window and does not close it or send anything.
6. The history shows your messages, what you said (the transcript) and
   Martlet's replies as they stream in. A refusal is shown as such and never
   spoken as ordinary speech; a stopped or failed reply keeps its text with a
   *Cut short* note. Replies are kept short by asking, not by cutting: every
   reply to what you type or say ends its instructions (after the persona and
   the other instructions; lore and memory go in the message's notes) with an
   instruction to answer in one or two short sentences at
   most, with no lists, second paragraph or closing offers (longer only when
   you explicitly ask for detail, steps or a list), and to finish its last
   sentence. The max reply length (Companion › Replies; 1,024 tokens by
   default, or 4,096 on a Chat Completions route such as OpenRouter or NVIDIA
   Build or on a paired host's Ollama, whose budget also covers a reasoning
   model's hidden thinking; on a host it stays under half a saved context
   size) is only
   a ceiling against a runaway answer. **Thinking steps** (Companion ›
   Replies: *Default*, *Off* or *On*) decides whether a reasoning model thinks
   before it answers; *Off* skips that hidden thinking, so replies start sooner
   and spend no tokens on it, and *Default* leaves it to the model. It applies
   to replies, glances and remembering. Ollama on this PC gets
   `reasoning_effort` (`none` turns thinking off), OpenRouter its `reasoning`
   object, a paired host's Ollama its own `think` (a host must run this Martlet
   version or later), and other Chat Completions servers (NVIDIA Build, vLLM,
   llama.cpp) the chat template's `enable_thinking`, which works only where the
   model's template has it; the OpenAI route's models don't reason. Chat Completions streams tolerate
   provider extras (other delta fields, repeated usage or finish chunks,
   changing ids) instead of ending the reply mid-sentence. When a spoken reply
   outgrows the speech budget below, Martlet stops saying it aloud but still
   shows all of it, with an *Only the start was said aloud* note. The voice
   never cuts a reply short either: when the voice fails (a paired host's voice
   worker fails or is reloading, the voice takes too long, or the speakers
   fail), Martlet stops speaking and the rest of the reply still streams in
   from the Thinking model and is shown in full, with a *The voice failed, so
   this wasn't spoken* or *The voice stopped partway* note and the voice's own
   remedy below the history. Nothing else is asked to speak it instead. The
   speech bubble beside the character (and the subtitles, when on) still shows
   what the voice couldn't say: the sentence that failed and each one after it,
   one after another for about as long as reading it takes (2-20 s), until the
   next reply or *Stop*. The
   desktop log records it as `Spoken reply failed (...)` against the Speaking
   route, not as a Thinking failure.
7. **Companion › Prompts** lists every internal prompt Martlet sends to the
   Thinking model: the persona wrapper, the style line and each response style,
   reply length, always listening, tools, who is talking, lorebook and memory
   introductions, notes with messages, the screen and camera glance
   instructions, messages (including the one sent
   when a notification pops up or a taskbar button flashes) and chattiness
   lines, *Screen with your message* (sent with what you type or say while
   vision is on), the Remembering and Learning names requests and the prompt
   that joins them, and the smart home notes. Each
   one is editable; a saved edit replaces the built-in text wherever it is used
   (settings `prompts.overrides`, by prompt ID, absent while nothing is
   edited). Words in braces such as `{name}`, `{persona}`, `{style}` or
   `{silent}` are filled in when the prompt is sent, and an emptied prompt
   sends nothing (the glance messages can't be emptied). Martlet still parses
   the answers to Remembering and Learning names, so their line formats must
   stay. Reload an open conversation to use saved prompts. Each prompt shows
   its estimated tokens and the page shows all prompts together, by Martlet's
   own request-size estimate (about a token per three UTF-8 bytes, the rule
   that keeps requests within the model's limit), counted before placeholders
   are filled in.

STT receives only the selected microphone's completed bounded utterance. LLM
receives the typed text or that final transcript plus the fixed active persona
revision, one weighted response style selected only after participation
accepts the turn, and the reply-length instruction (all as worded in Companion › Prompts). Persona/style and user input share the existing byte/token
reservation; an over-budget combination is rejected without truncation or a
provider call. Valid legacy v1/v2 profiles upload no implicit persona/style
instruction until settings v3 is explicitly
saved. The conversation so far is supplied from volatile memory: every
completed exchange of the open talk window, the newest that fit the context
size (Companion › Replies; blank is 100,000 tokens for a cloud model, never
more than the model's own limit when Martlet knows it, 8,192 on a paired host
and Ollama's own context length on this PC); oldest pairs are omitted until the
whole request fits. Failed/refused/suppressed turns are excluded, and Refresh
context, pause, lock, configuration load/change or closing the talk window
clears the buffer; Stop keeps it, so the conversation continues after an
interruption. The talk window's context line says how many exchanges are kept
and about how many tokens of the context they take.

**Context size.** Companion › Replies › Context size bounds every route:
persona, lore, memory, the conversation so far, the message and room for the
reply (tool descriptions and results have their own room on top). Martlet
estimates tokens locally (a token per three UTF-8 bytes plus eight per
message), not with the provider's tokenizer. It finds the model's own limit
when you choose it (Companion › Thinking), test it (Ollama on this PC), load it
in the talk window (Ollama on this PC) or press *Check model limit* on the
Replies page: an OpenAI-compatible server's model list (`GET {base}/models`:
OpenRouter's `context_length`, vLLM's `max_model_len`, Groq's
`context_window`, Mistral and LM Studio's `max_context_length`, llama.cpp's
`meta.n_ctx_train`; for a server on this PC also LM Studio's, llama.cpp's and
Ollama's own endpoints), Ollama's `/api/show` and `/api/ps` on this PC, and
OpenAI's documented 1,047,576 tokens for gpt-4.1 and gpt-4.1-mini. The check
reads model metadata only, sends a saved key only to its own base URL and
follows no redirect. What it finds is kept per PC in `model-limits.json`
(never in settings, so a check doesn't interrupt a conversation) and used from
the next conversation. Ollama on this PC can't be sent a context size through
its OpenAI-compatible endpoint, so its context length setting (the app's
slider or `OLLAMA_CONTEXT_LENGTH`) is the limit there, assumed to be its
smallest default (4,096) until Martlet sees it. A paired host's Ollama loads
the saved size, at most 32,768 (its gateway's bound, which also keeps the
host's 16 KiB and 16-message request limit). A larger size sends more with
every reply, which costs more on paid providers; the Thinking fallback gets
the same request.

## Prompt caching and the request layout

Every Thinking request is laid out so it starts like the one before, because
providers only reuse what they already read when a request *starts* the same
way: OpenAI, OpenRouter and others bill cached input for less and answer
sooner, and Ollama on this PC skips reading it again. A model with
sliding-window attention there (Gemma and others in Ollama's llama.cpp) can
only reuse a request that starts with a whole earlier one, so it otherwise
reads the entire conversation again before every reply (on a 12B model, about
2 ms a token: seconds for a long conversation).

- **Instructions** (the system message, or OpenAI's `instructions`) hold what
  doesn't change from message to message: the persona (with its style when it
  has one), tools, voice tags, the smart home tools prompt, who-is-talking,
  always-listening, what-this-PC-plays, recording and picture prompts as each
  message needs them, and the reply length last. A screen glance has its
  glance instructions there instead.
- **The conversation so far** follows, each earlier message exactly as it was
  sent, with its notes (a paired host gets the plain messages and the notes
  with its instructions, as before).
- **The message** comes last and ends with Martlet's **notes** between
  `[MARTLET_NOTES]` labels, only when something is new: lorebook entries and
  remembered facts not already in the notes of an earlier message the request
  carries, who is talking when that changed, a smart home result, and the
  picked style when the persona has several and it changed. The first notes
  start with what notes are (Companion › Prompts › *Notes with messages*).
  Notes are never shown and never what the user said.
- When the conversation outgrows the context, Martlet lets go of a quarter
  more of the oldest exchanges than it must (and forgets them), so the next
  several replies start at the same exchange instead of moving by one every
  reply.
- After a reply, remembering and learning names share one request; on a
  Thinking model on this PC it continues the reply's own conversation, so the
  model's cache still holds it for the next reply (see
  [Memory](MEMORY.md#automatic-recall-and-remembering)).

Each reply, glance and after-reply request writes a desktop log line such as
*Thinking input (Reply): first words after 2004 ms; 568 input tokens, 525 of
them (92 %) from the model's prompt cache.* when the provider reports it
(OpenAI, OpenRouter and others report cached tokens unasked; Martlet asks
Ollama on this PC for them), and the talk window's context line ends with
*Last reply: 92% of its 568 input tokens came from the model's cache.*

[Memory](MEMORY.md) is ON by default (Companion › Memory turns it off). When
on, each explicit typed/PTT/hands-free turn automatically recalls up to twelve
saved facts (best lexical matches for the current input, then the newest) as one
labeled background block inside the same input budget; the full store, path and
consent UUIDs are never uploaded. If the store can't be read, the reply goes
ahead without memory and the status says why. After a completed reply, the
exchange is sent once more, as one extra text-only request (shared with
learning names when both are due), to the same Thinking model, which picks out
lasting facts to save locally (shown under the reply and
listed in Memory). Screen glances are never remembered. The volatile exchange
buffer itself is still not persisted. TTS receives only eligible
generated segments. All provider routes have the fixed HTTPS origin
`https://api.openai.com`; there is no custom endpoint, model discovery,
fallback provider, retry loop or hidden continuation.

**Typed input, push-to-talk or always listening only.** Learned VAD, acoustic
wake words, automatic name/group listening and unsolicited participation are OFF. Transcript
words cannot grant trusted-control provenance. Unknown STT confidence stays
unknown. A PC microphone does not automatically capture remote participants or
game/call audio. Capturing other people requires their permission.

## The accepted envelope

| Boundary | Hard app choice for each explicit action |
| --- | --- |
| Overall permission | Original monotonic and absolute expiry within 150 seconds, including scheduling/capture/authorization; never restored or extended |
| Capture | At most 25 seconds / 800,000 bytes, canonical mono 16 kHz PCM16; original capture permission at most 30 seconds including cleanup and transfer |
| STT | At most one request, 800,044 WAV bytes, 30-second request, 4096 transcript characters |
| LLM | At most one request, 4096 user characters; current user + persona + style + reply-length instruction + the conversation so far within the context size (Companion › Replies, 2,048-2,000,000 estimated tokens: blank is 100,000 for a cloud model within its known limit, 8,192 on a paired host, Ollama's context length on this PC; at most 8 MiB of UTF-8 and 4,096 earlier messages, 16 KiB and 16 on a paired host), 1,024 requested output tokens by default as a ceiling (16-2,048 via Companion > Replies, which also sets optional sampling: temperature, top P/K, min P and repetition penalties, each sent only to routes whose API accepts it, and Thinking steps), 16,384 response characters, 45-second request |
| Conversation runtime | At most 90 seconds; existing bounded two-segment pending queue, one active TTS/playback segment |
| TTS | At most eight requests, 1536 input UTF-8 bytes each / 12,288 total; 10 seconds / 240,000 samples reserved per request, 80 seconds / 1,920,000 samples total; at most 20 seconds per request. Reaching this budget ends speech for the reply, not the reply's text |
| Content and timeline | Current bounded input/transcript/answer/refusal in memory; 32 metadata timeline entries, existing bounded engine event rings; no audio/transcript files or ordinary content logs |

These are admission and request limits, **not a measured latency promise or a
currency/invoice ceiling**. Input-token reservations are conservative local
units, not measured upstream usage. Price, quota, taxes, model availability,
final cost and other clients' activity are **UNKNOWN**. A failed/canceled
upload or request may already have cost money. A ChatGPT subscription does not
prove API quota. Review official [pricing](https://openai.com/api/pricing/) and
[data retention](https://platform.openai.com/docs/guides/your-data) separately.
No-retention or free-service promise is made.

Each message or utterance is its own bounded action. Internal TTS callbacks do not
prompt for every sentence: they derive one-use permissions and exact
`OperationBudget` reservations only for actual segments inside this accepted
envelope. The runtime enforces its own original stage/turn clocks as well.
Speech limits can end what is said aloud before all possible segments (the
rest of the reply is still shown); unused reservation is not silently recycled.

## Stop, ownership and privacy

`LiveConversationController` is app-lifetime composition, not another inference,
capture or playback implementation. It reserves the existing
`SetupOperationRunner` for the entire live action. Credential setup, local audio
tests and the fixture share that same slot. There is no pending-turn queue:
busy input is rejected and requires a fresh deliberate action after cleanup.
An old Stop/release handle cannot cancel a newer action.

`ConversationAuthorization` validates the exact snapshotted settings revision,
profile, role, origin, model/voice and opaque credential reference before and
after native retrieval. It resolves a fresh secret only after the matching
one-use role permission/reservation exists. Production uses
`WindowsCredentialStore`; no placeholder account, environment-variable key,
exported secret or fake-native production default is supplied. Key readability
is not API validity. Native/managed `SecretLease` values and returned provider
credentials are disposed on all owned exits. The provider's header interface
requires a managed string: neither HTTP-internal copies nor garbage-collected
strings can be promised securely erased.

PTT uses the existing `MicrophoneCapture`, fresh IDs/epochs and original capture
expiry through `TakeUtterance`. The transferred lease and intermediate PCM copy
are promptly cleared; `BoundedWaveAudio.FromPcm` supplies the existing validated
canonical WAV, not an alternate codec/transcription stack. Its bounded private
managed WAV is dropped after the owned request; no secure erasure of provider/
OS/HTTP copies is claimed. No-speech, failed or canceled STT never dispatches LLM.

`ParticipationPolicy` evaluates the exact typed/final-STT content before runtime
Start. Commit and Start are serialized with current pause/consent state. The
final transcript intent is issued at its actual receipt time, not by renewing
an intent that waited through STT. Policy consent signals are not provider
permission. The policy lease remains owned until actual runtime
`OwnershipRelease`, not merely `Completion`.

Stop, losing the held control, session lock and Close stop only this operation.
Unlocking resumes the listening and vision chosen in Companion (unless paused
in the window); a paused mic or vision button stays paused until clicked. Native/credential/HTTP work and cleanup
run off the dispatcher; the UI remains responsive. Noncooperative native work
or callbacks can outlive a timeout or closed observer. The shared slot remains
reserved; failed cleanup is quarantined rather than replaced with a fresh
factory. Closing the main window exits the app, not a background tray listener.
This is not a measured 250 ms physical-stop guarantee.

The fixed **Stop (Esc)** control also drops a typed message still waiting to be
sent, and what always listening heard that was still waiting for a reply, and
pauses vision; listening itself carries on. Escape works from the message box, the
history and the held talk button. Releasing Space after Escape cannot send that
discarded recording or rearm PTT. Stop during settings loading or a slow worker
requests cancellation without releasing the shared ownership slot early.
Partial response text remains available; stopped speech is not replayed.
The shortcut is local to this conversation window, not a system-wide hotkey.

The STT adapter's backwards-compatible two-token overload retains the original
caller and app-operation tokens independently through credentials, serialization,
send and result acceptance. A blocking newer cancellation callback cannot hide
either original cancellation flag. LLM/TTS keep their reviewed original
caller/enumerator guards; no application bridge substitutes linked-only
permission for those original sources.

## Voice latency: streaming, overlap and barge-in

Time to first audio is what makes a spoken reply feel conversational, so the
voice pipeline never waits for a whole reply:

- **Pipelined chunking.** Text is spoken as it streams from the Thinking model:
  each finished sentence goes to the voice right away. The first piece of a
  reply is cut even earlier, at a comma, semicolon or dash once it is at least
  24 characters long, so audio starts before the first sentence is finished;
  later pieces stay whole sentences, which sound more natural.
- **Where each persona's voice pauses.** Each piece is said on its own, so a
  break in the wrong place sounds awkward ("I'm so glad you're here, | cutie.").
  Personality › **Where the voice pauses** sets, per persona, which stops may
  break a reply: commas, semicolons and dashes (first piece only), periods,
  question marks and exclamation marks, all on by default. A stop that is off
  doesn't break until the piece has grown long (100 characters); then any stop
  does, so a piece never runs past what the voice can say at once. **Say a
  short ending with the words before it** (up to two words by default; *Never*
  turns it off) keeps an ending such as ", cutie." or ". Cutie!" with the piece
  before it: each piece waits until a few more words have streamed in (or the
  line or reply ends) before it goes to the voice.
- **Overlapped synthesis.** While one sentence plays, the next is already being
  synthesized (one sentence ahead, never more), so there is no synthesis gap
  between sentences. A voice failure on the next sentence surfaces only when
  playback reaches it, so what is already playing finishes; then the voice
  stops for the rest of the reply while its text keeps streaming.
- **Barge-in.** Optional and off by default. With always listening, ticking
  *Let me interrupt Martlet by talking* in Companion › Listening keeps the
  microphone open while Martlet speaks. Talking over a reply stops it: the Thinking request is
  canceled, the queued audio is dropped and what you said is answered next,
  with the reply so far kept in context. Only the microphone can do this, and
  only with a sustained voice (`TalkOverDetector`): at least a second of
  voice-loud 20 ms frames, where a pause longer than half a second starts the
  count again, so a cough, a click, a quick "mm-hmm" or a word from across the
  room never stops Martlet. What this PC plays never does either: the PC
  listener ([hearing what this PC plays](#hearing-what-this-pc-plays)) never
  interrupts anything, and with [echo reduction](#echo-reduction) the
  microphone's frames that were the speakers' sound don't count. Words that
  were heard but didn't talk over Martlet wait and are answered after the
  reply; restarting a reply because you kept talking applies only before
  Martlet starts saying it. Through speakers this relies on echo reduction (on
  by default); if Martlet still stops itself, use headphones or turn the
  choice off. With it off (the default), listening holds off while Martlet
  speaks, and Stop, Esc or the talk button still interrupt. Preferences saved
  before barge-in became opt-in had it on only because it was the old default,
  so it starts off once after updating; tick it again to use it.
- **Measured.** Each spoken reply's snapshot reports `FirstTextAfter` and
  `FirstAudioAfter` (from the start of the reply), and the desktop log records
  them as *Reply latency: first words after … ms, first audio after … ms*.

## Echo reduction

*Reduce echo from my speakers* (Companion › Listening › Speakers and echo, on
by default) lets you talk to Martlet without headphones. Whenever the
microphone listens (always listening or push-to-talk), Martlet also reads what
this PC plays on the output its own voice uses (the chosen speakers, or
Windows' default) through a WASAPI loopback, and WebRTC's acoustic echo
canceller (AEC3, the one AudioTranscriber uses on its microphone track)
subtracts that sound from the microphone before anything else hears it: voice
activity, Voice ID, speech-to-text and a Thinking model that hears. So Martlet
neither hears its own voice as you (barge-in through speakers works) nor a
video, music or game playing on the PC.

- Both streams are lined up by their devices' timestamps; each 10 ms of
  microphone audio is cleaned against the speaker audio 40 ms later on that
  timeline (an echo always arrives after it was played), and the canceller
  finds the room's actual delay itself. Speaker audio that hasn't arrived 30 ms
  after it was due counts as silence, so cleaned audio arrives at most about
  80 ms after it was heard.
- The room's echo model is kept in memory for a minute between listens, so it
  doesn't relearn the room every time Martlet listens again; pausing, muting,
  locking Windows or closing the conversation drops it.
- Always listening also keeps, per capture, a record of which 10 ms frames
  were the speakers' sound (`EchoTimeline`: the canceller took more than
  10 dB away while the speakers played or their echo could still be heard).
  Those frames never count toward talking over Martlet, and a sound that was
  mostly the speakers' (Martlet's own voice or a video leaking past the
  canceller) is let go like a cough rather than transcribed as you. Only
  metadata is kept, never audio.
- The speaker audio is used only to cancel the echo, on this PC, while the
  microphone listens. It is never saved, logged or sent. A high-pass filter
  removes rumble; noise suppression and gain control stay off so your voice
  reaches speech-to-text as it was. The microphone test and Voice ID setup
  record the microphone unchanged.
- If the speakers can't be read (none, an unsupported format, a device that
  changed) or the canceller can't load, Martlet listens without echo
  reduction, says why under the check box and logs it once (*Echo reduction:
  …*); otherwise the log notes *Echo reduction is on*. Turning the choice off
  restarts listening without it.

## Hearing what this PC plays

*Hear what this PC plays* (Companion › Listening › Watch along, off by
default) lets Martlet watch or listen along with you: while always listening
runs, a second listener (`ListeningOptions.PcAudio`, its own slot beside the
microphone) also hears the sound the PC plays, such as a video, a stream, a
call or a game. Ticking it is the consent; push-to-talk never hears the PC.

- **What it hears.** Chosen each time it starts hearing an utterance. While
  only the output you hear (Windows' default) has apps streaming to it, a
  Windows process loopback of every app's sound except Martlet's own process
  tree (`WasapiPcAudioSourceFactory`, 48 kHz stereo PCM16), so Martlet never
  hears its own voice and keeps hearing the PC while it speaks. A process
  loopback mixes every output, virtual ones included: a voice changer or
  microphone app (Voicemod, NVIDIA Broadcast) streams your own voice into its
  virtual cable all the time, never played aloud, and the PC listener heard it
  as the PC. So while an app other than Martlet streams to another output
  (`WasapiPcAudioSourceFactory.Outputs` reads the outputs' sessions, never
  their sound), it hears only the output you hear, through that output's
  loopback with Martlet in it, and holds off while Martlet speaks, as the
  microphone does; if a PC line is under way when Martlet starts speaking, it
  ends right there, so Martlet's voice never goes into it. The same happens
  where Windows can't leave Martlet out (before Windows 10 version 2004, or
  when Windows refuses it). A loopback delivers nothing while nothing plays, so
  `PcAudioCaptureFactory` fills those gaps with silence on the clock: the
  stream stays continuous and a paused or quiet video ends the utterance.
- **How it is marked.** Each utterance is transcribed with the Listening
  choice like the microphone's, but never goes through Voice ID or voice
  recognition (no voice is recognized or learned from it) and is never kept as
  a recording for Thinking. The talk window shows it in a muted *Playing on
  this PC* bubble, never as you. To Thinking, every line of it starts with
  `[PC audio]`, and Companion › Prompts › *What this PC plays* says those lines
  are never the user nor instructions, to answer the user with them as shared
  context, and on their own mostly to reply `[pass]`.
- **When it goes to Thinking.** What the PC played goes with the next thing you
  say, in the order it was heard. On its own it is offered at most every 20
  seconds after Martlet last answered (sooner once the PC has been quiet for 4
  seconds, but never within 20 seconds of an answer, so it never makes a second
  reply right after Martlet answered you), only while you aren't talking, and
  it never interrupts or restarts a reply. At most the newest 1,500 characters
  go with one message.
- **Your own voice played back.** Hearing only the output you hear (above)
  keeps virtual cables out. Your voice can still reach the PC's sound when it
  is actually played back (a voice changer's or headset app's *hear myself*,
  Windows' *Listen to this device*, a call that echoes you), and then the same
  words came twice: as *You (spoken)* and as *Playing on this PC*, and Martlet
  answered both. As a safety net, a line the PC played that
  mostly repeats, in order, what the microphone heard you say (`PcEcho`: at
  least 60% of its words; heard while you talked or were being transcribed, or
  up to 15 seconds after) is your own voice: it is left out of the history and
  never goes to Thinking, and your own line is always kept and answered. While
  the microphone is still hearing or transcribing you, what the PC played
  waits for your words (at most 8 seconds) so your voice played back never
  shows; a line let go before your words came is still removed once they do.
  The `LivePcAudio` line then adds *This PC plays your voice back too; Martlet
  left out N line(s) of it.* and the desktop log says so once.
- **Never remembered or acted on.** Memory recall and remembering, learning
  names, Home Assistant and MCP tools only ever read your own words: a message
  that is only what the PC played gets none of them, and earlier `[PC audio]`
  lines are left out of what remembering reads. The sound is never saved.
- **Echo.** Through speakers the microphone also hears what the PC plays; keep
  [echo reduction](#echo-reduction) on (or use headphones) so it isn't taken
  for you. The Companion card's status says so when echo reduction is off.
  Your microphone's lines are never dropped for matching what the PC played:
  with both hearing the same words, the reports on this feature were all your
  own voice played back, and leaving your words out would leave you
  unanswered.

The talk window's `LivePcAudio` line says whether Martlet hears the PC now
(without its own voice, or only on the output you hear while it pauses for
Martlet's voice), how many lines of your own voice played back it left out, or
why it can't. The Companion card's status names the other output in use.
`pc_audio_check` in
[Martlet MCP](MCP.md) reads the choice, asks Windows whether Martlet can be
left out without recording anything, says which outputs are in use and what
Martlet would hear, rehearses the production path with a fixture loopback and
runs the own-voice comparison on fixed samples.

## Voice tags

Some voice engines turn tags written in the reply into sounds and tones of
voice. Each engine declares its own catalog in its own syntax in
`Martlet.Core.Settings.SpeechEngines` (one shared place; an engine without a
catalog reads words only). [Chatterbox Turbo](CHATTERBOX_VOICE.md#tags), the
default self-hosted engine, has `[laugh]`, `[chuckle]`, `[sigh]`, `[gasp]`,
`[cough]`, `[clear throat]`, `[groan]`, `[sniff]`, `[shush]` and the tones
`[happy]`, `[sarcastic]`, `[surprised]`, `[angry]`, `[fear]`, `[crying]`,
`[whispering]`, `[dramatic]`. Another engine registers its own with one
`SpeechEngines.Register(new SpeechEngine(..., TagCatalog: [new("(laughs)",
VoiceTagKind.Sound, "a laugh"), ...]))` call; nothing else changes.

- **Thinking prompt.** When a spoken reply's voice has tags, Companion ›
  Prompts › *Voice sounds and tones* is added to its instructions with exactly
  that engine's tags, one per line with when to use it, and asks for them
  sparingly. Text-only replies and voices without tags never get it.
- **Segmenter.** The speech segmenter (which still silences lines with
  markdown, links, code or other bracketed text) lets the speaking engine's
  tags through, case-insensitively, in the engine's own spelling, even when a
  tag arrives split across stream deltas. Any other registered engine's tag is
  dropped without silencing its sentence, so OpenAI, Windows voices, F5 and
  XTTS never read "[laugh]" aloud.
- **Chat and captions.** The chat, the saved conversation and the speech
  bubble/captions never show tags: they are stripped as the reply streams, and
  captions strip the spoken piece's tags.
- **The desktop character.** Each tag also has an engine-independent cue
  (`VoiceTag.Cue`: `laugh` for both `[laugh]` and Dia's `(laughs)`). While the
  character shows, an emote or motion linked to a cue plays when the voice
  speaks that tag, and the others are offered to replies as English
  [character tags](AVATARS.md#emotes-and-motions) such as `{blush}`, which the
  segmenter and the chat drop like another engine's tags. Both reach the
  character through the runtime's `CharacterCueFeed`, timed within the sentence
  as it starts playing (or at once for a reply that isn't spoken).

`voice_tags` in [Martlet MCP](MCP.md) shows all of these for any engine (with
`characterTags`, the character cues too).

## Hands-free voice activity and Voice ID

**How you talk** in Companion › Listening offers **Always listening** (default)
or **Push-to-talk**. The choice, sensitivity, pause length, Voice ID toggle,
*Speak replies* and Vision choices are remembered in `talk-preferences.json` in
the data folder.

- With always listening, **Start listening** in the talk window opens the
  microphone chosen in Companion › Listening (the Windows default unless another
  is picked; testing it there is optional); opening the window alone never does.
  If listening isn't set up, the button says *Can't listen* and why. If the
  microphone can't be opened (absent, busy, denied), the button's state is *Mic
  unavailable* with the fix, Martlet tries it again every
  5 seconds, and you can type meanwhile. Switching to push-to-talk in Companion
  while the window is open stops listening; switching back resumes it if you had
  started it.
  An adaptive energy detector (`EnergyVoiceActivityDetector`,
  20 ms frames read from the capture's own buffer through `TryCopyMonoFrame`)
  waits for speech, then releases the capture after your chosen pause
  (0.5/0.8/1.2 s). Only the detected speech plus 300 ms pre-roll and 200 ms tail
  is uploaded, not the idle wait before it. Sounds shorter than 450 ms (coughs,
  clicks) are ignored. **Sensitivity** trades missed quiet speech against false
  triggers from noise.
- Listening never stops by itself. It runs on its own slot beside replies
  (`LiveListener`): it records one utterance at a time and transcribes each, in
  order, while it already listens for the next, so nothing said while Martlet
  thinks is lost. Each utterance is still its own action: a fresh authorization,
  capture epoch, Voice ID check and STT request. It holds off only while Martlet
  speaks (a reply or a remark, plus 300 ms for the room's echo), so it never
  hears itself, and while other setup work (a microphone test, Voice ID
  enrollment) owns the app slot. Idle listening restarts the bounded capture
  every 12 seconds; nothing is uploaded when nobody spoke.
- What it hears appears in the history right away. Once you pause, everything
  heard since the last reply goes to the Thinking model as one message
  (`InputSource.HandsFreeListening`, reason `ExplicitHandsFree`) with
  instructions that it hears an always-on microphone: it answers what is meant
  for it and replies `[pass]` (never shown or spoken; the message is marked
  *Martlet stayed quiet*) when it wasn't meant for it or needs no answer. When
  what you said trails off ("so, um", "and", a trailing comma or dash), Martlet
  waits 1.5 s longer for the rest.
- If you keep talking before Martlet says anything, that reply is dropped and,
  once you pause, asked again with everything you said (at most three times in
  a row, so background talk can't loop it). A reply that already acted through
  Home Assistant or a tool finishes, and what you added is answered after it.
- Typed messages go to the same slot and are always answered; listening carries
  on beside them. It continues while the window is in the background. Only the
  **Listening** button pauses and resumes it; Stop/Esc quiets Martlet but
  leaves listening on; session lock and Close end it (unlocking resumes it).
  Reply, provider and speech-to-text failures are shown and never pause
  listening; nothing is retried automatically. When listening can't start
  (for example *Only respond to my voice* is on but Voice ID isn't set up), the
  button says *Can't listen* with why and Martlet keeps trying, so it listens
  again as soon as that is fixed.

**Voice ID** (Companion › Listening › **Set up Voice ID**) recognizes the enrolled user locally:

- Enrollment records three read-aloud phrases with a separate local-only
  permission. A bundled speaker encoder (a managed port of Resemblyzer's GE2E
  LSTM, Apache-2.0; see `packaging\windows\DEPENDENCIES.txt`) turns speech into a
  256-number voiceprint. Recordings stay in memory and are zeroed; only the
  voiceprint, threshold and consistency score are saved in `voice-id.json`.
  **Test** reports the score, the threshold slider tunes strictness, and
  **Delete voiceprint** removes the file.
- With **Only respond to my voice** checked (PTT or hands-free), each utterance
  is compared with the voiceprint on this PC *before* upload. Another voice, TV
  audio or too little speech (<0.8 s) is discarded and never sent to STT. Each
  ~1.6 s part is also scored, so a turn that is mostly you but includes another
  voice is flagged ("another voice may also be in this recording").
- Voice ID is a convenience filter, not authentication: recordings of you or a
  similar voice can pass, and a cold or a new microphone can lower your score.
  Same-person clean speech typically scores 0.80-0.95 and other people
  0.45-0.75; enrollment suggests a threshold from how consistent your phrases were.

**Recognizing who is talking** (Companion › **People**, part of Martlet and on
by default) tells several people at the microphone apart with AudioTranscriber's
sherpa-onnx speaker recognition, names the speaker to the Thinking model, labels
earlier messages with who said them, and learns the names each voice goes by
from the conversation. The list of voices can follow you to every computer
through your paired hosts. **Parakeet** (Companion › Listening › This PC) is
AudioTranscriber's more accurate speech-to-text, running inside Martlet with no
Docker. See [Recognizing people by voice, and Parakeet](VOICES.md).

## Troubleshooting

| Visible condition | Meaning and next action |
| --- | --- |
| Setup required / unsupported role | Review the displayed exact catalog IDs; store each role key, reselect its destination and save. No automatic fallback or capability request occurs. |
| Configuration changed | Loaded revision/role/key/output no longer matches this action. An open talk window loads the saved change once Martlet is free and continues the conversation with its context. External profile editing/copying while running is unsupported. |
| Credential missing / access denied | Review the signed-in Windows user and selected role reference. Explicit setup retrieval can check local readability only. Do not elevate or disable protection. |
| STT no speech | No LLM/TTS followed. Review intended input and local microphone test; start a fresh PTT action or type instead. Silence samples are not VAD evidence. |
| Hands-free never hears me / triggers on noise | Raise or lower **Sensitivity**; watch the level bar while speaking. Choose a longer pause if it cuts you off mid-sentence (talking on before Martlet answers also merges what you say into one message). |
| Martlet doesn't answer something it heard | The message is marked *Martlet stayed quiet*: the Thinking model decided it wasn't meant for it. Say its name or ask directly, or type. |
| Voice ID ignores me | Run **Test** in Set up Voice ID. Lower the threshold slightly or re-enroll with your usual microphone and distance. Turn Voice ID off to talk meanwhile. |
| Mic access/busy/lost/default-change/format | Use Audio setup's specific privacy/device remedy. Always listening shows *Mic unavailable* and tries the same chosen microphone again every 5 seconds; there is no loopback or device fallback. Typed input remains available. |
| Provider auth/model/quota/rate/network failure | Inspect the stable provider code; review account/model availability and current limits outside Martlet. A failed request is not a safe automatic retry. |
| Refused / partial answer | Refusal is separate from answer text. Partial answer remains visible; unfinished/unsupported speech is discarded, not replayed. |
| TTS/output failed | The reply's text still completes and stays visible; only the voice stopped (`SpeechFailure`, and the voice's provider code with `FailedProvider` Tts). For the next new action choose text-only, or review the selected output/model/voice or the paired host's voice role. Earlier speech may have played. |
| Cleanup pending / quarantined | No new effectful action may take the slot. Wait for actual release; close Martlet if the native worker never returns. Do not start a replacement factory to evade quarantine. |

Submitted samples, device-consumed samples and observed drain are different
measurements and **none proves that a person heard sound**. "REAL provider
response" describes this production attempt, not readiness inferred from a
catalog, saved checkpoint or unit test. In-process fixtures remain visibly
`FIXTURE ... NOT inference`.

## Fixture-safe developer reproduction

Use SDK 10.0.401 and the committed normal locks:

```powershell
dotnet restore Martlet.slnx --locked-mode --artifacts-path $artifacts
dotnet build Martlet.slnx --no-restore -c Release --artifacts-path $artifacts
dotnet test Martlet.slnx --no-build -c Release --artifacts-path $artifacts
.\scripts\Smoke-Desktop.ps1 -ExecutablePath "$artifacts\bin\Martlet.Desktop\release\Martlet.Desktop.exe"
```

On the pooled-drive developer host, use one unique session-owned **C:** artifacts
directory consistently for restore/build/test, `CI=true`, process-only SDK PATH/
DOTNET_ROOT, own CLI home, telemetry off and ASP.NET certificate generation
disabled. Do not build in the primary checkout or change test/SDK pins.
Dedicated provider/runtime/policy project commands remain available for focused
local runs even though their projects are now also in the root solution.
Hosted validation is removed under the
[local-only repository policy](../README.md#local-only-validation-policy).

`LiveConversationTests` drives actual WPF controls and the app controller,
Windows credential wrapper with injected fake native access, selected capture,
STT serializer/parser, participation policy, runtime LLM/TTS parsers and real
PCM sink with controlled devices. There are no real accounts, vault entries,
recordings, physical endpoints or billable requests in these tests. Native
Desktop smoke opens the live surface without a profile/key, requires Send/PTT
disabled and voice/permission OFF, then exercises existing no-key setup.
Package smoke launches actual self-contained
Desktop/Doctor apphosts; it does not run the installer.

In-process WPF regression cases exercise fixed Stop bounds and hit testing at
minimum/default window sizes and top/middle/bottom scroll positions, routed
Escape from input/response/PTT, discarded capture and late Space release,
controlled playback cleanup, unused consent revocation, and retained ownership
during blocked settings/fake-vault work. These are managed UI and controlled
HTTP/audio/vault evidence, not physical keyboard/device or apphost qualification.

## Separately authorized manual qualification (NOT RUN)

An owner must separately authorize the account, data, audience, device and cost
before a real trial. Do not send a key to a developer/chat or infer permission
from this checklist.

1. Record exact internal build/OS and intended input/headset; review publisher/
   installer trust first. Do not bypass Windows protection for unsigned artifacts.
2. Using the intended signed-in user, explicitly store/check the real role keys;
   record only outcomes, never key values or exported vault contents.
3. Review current provider pricing/retention/account/model access and each
   displayed action envelope. Set appropriate provider-side spending controls
   where available; do not assume a guaranteed hard cap.
4. Confirm local microphone/output selection with separate local test permission.
   Trial typed text-only first and confirm no TTS/output-device activity.
5. Separately select voice, approve one short typed action, inspect response/
   playback progress and have the listener confirm whether it was heard.
6. With permission from all audible people, approve one short PTT action and
   inspect transcript -> policy -> LLM -> TTS -> selected output. Record exact
   stage outcomes, unknowns, real charges if known and observed quality.
7. Exercise Stop/revocation, lock/deactivation, missing/changed devices and
   typed fallback. Do not replay partial speech automatically or promote one
   successful run into a full device, latency or group-listening qualification.

Actual API account/model quality/cost/performance, real OS-vault roundtrip,
physical audio, full first-conversation novice trial, clean Windows installer
lifecycle, signing, rights and release gates remain **NOT RUN / NOT PASSED**.
V06b adds [local Troubleshooting](TROUBLESHOOTING.md) using the existing Support
engine. Optional typed stage metadata uses a separate bounded worker, not the
conversation effect slot; no input, response or audio enters its journal.
Screen/memory capture, model download, host service/driver change, deployment
or release is not included.
