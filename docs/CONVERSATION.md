# Explicit Desktop API conversation (V04b)

**Internal functional integration, not account/device/release qualification.**
**Start talking** on Home opens the talk window: the conversation history, what
you said and a message box, nothing else. It sits beside Martlet rather than
blocking it, so Home, Companion and Settings stay usable while you talk; Home's
button reads **Show conversation** while it is open and brings it back to the
front. How Martlet listens, speaks and sees is chosen in Companion (Listening,
Voice and Vision), and an open talk window follows a change there at once.
Always listening starts only when you press **Start listening** (on Home, in the
notification-area menu or in the window) and stops with **Stop listening**.
Vision works the same way with its own button: once Companion › Vision turns it
on, **Start watching** (on Home, in the notification-area menu or in the window)
starts looking and **Stop watching** stops it; neither button starts or stops
the other. Neither needs the talk window: Home's **Start listening** and
**Start watching** run the conversation
hidden, Home's listening and watching indicators say what each is doing (*Listening*, *Hearing
you…*, *Watching your active window.*, *Taking a look…* or why it can't), **Show conversation**
shows its history, and closing the window while Martlet listens or watches only
hides it (**End the conversation** in the notification-area menu ends it). A
Home Assistant or tool question shows the window. Settings › *Startup and
closing* › *When Martlet starts, show the character and start listening (and
watching, while vision is on)* does that on every start, including Start with
Windows in the notification area. A
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

**Vision** (Companion › Vision, on by default and looking at your whole screen)
lets Martlet glance at your active window, screen or a camera while you have it
watching (**Start watching**) and occasionally comment; it needs a Thinking
model that can see images. See [Screen commentary](SCREEN_COMMENTARY.md).

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
   Martlet's replies aloud* is on (Companion › Voice, or **Mute voice** /
   **Unmute voice** on the character's right-click menu); otherwise they are text
   only, with no TTS request and no output device.
5. Type and press Enter (Shift+Enter for a new line). With **Always listening**
   (Companion › Listening, the default once the microphone is tested) press
   **Start listening** and just speak; it keeps listening until you press
   **Stop listening**. With **Push-to-talk**, hold the talk button with the mouse or Space,
   then release to send (invoking it starts a recording and invoking it again
   sends). **Stop (Esc)** stays in the header at every size: it stops the reply,
   discards a recording instead of sending it, and stops watching. It never
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
   Replies: *Off*, the default, or *On*) decides whether a reasoning model
   thinks before it answers; *Off* skips that hidden thinking, so replies start
   sooner and spend no tokens on it, and *On* asks for it. Nothing chosen is Off
   (a saved Off from an earlier version reads the same), so every request says
   Off or On. It applies to replies, glances and remembering. Ollama on this PC gets
   `reasoning_effort` (`none` turns thinking off), OpenRouter its `reasoning`
   object, a paired host's Ollama its own `think` (a host must run this Martlet
   version or later), and other Chat Completions servers (NVIDIA Build, vLLM,
   llama.cpp) the chat template's `enable_thinking`, which works only where the
   model's template has it; the OpenAI route's models don't reason. A model
   that always thinks (OpenRouter lists some as reasoning-mandatory) or a
   server that doesn't take the control refuses the request before answering:
   Martlet asks once more with the model's own default, logs *refused Thinking
   steps Off*, and keeps the default for that model until it restarts, so the
   reply still comes. The reply latency line names the choice next to the
   model (*thinking steps off*). Chat Completions streams tolerate
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
   **Mute voice** on the character's right-click menu (**Unmute voice** while
   muted) turns *Speak Martlet's replies aloud* off (or on) from there: a reply
   Martlet is saying stops being said at once, with no failure and a *Muted
   partway* note when some of it was heard, and its words still stream in and
   go to the speech bubble the same way. A reply that isn't spoken shows each
   sentence in the speech bubble and subtitles too, without a voice request or
   an output device.
7. **Companion › Prompts** lists every internal prompt Martlet sends to the
   Thinking model: the persona wrapper, the style line and each response style,
   reply length, always listening, tools, Thinking longer, who is talking,
   lorebook, memory and past conversations introductions, notes with messages, the screen and
   camera glance instructions, messages (including the one sent
   when a notification pops up or a taskbar button flashes) and chattiness
   lines (including *Martlet decides* and *Chattiness right now*), *Screen with your message* (sent with what you type or say while
   vision is on), the background work notes and the Thinking longer task, the
   Remembering and Learning names requests and the prompt
   that joins them, and the smart home notes. Each
   one is editable; a saved edit replaces the built-in text wherever it is used
   (settings `prompts.overrides`, by prompt ID, absent while nothing is
   edited). Words in braces such as `{name}`, `{persona}`, `{style}` or
   `{silent}` are filled in when the prompt is sent, and an emptied prompt
   sends nothing (the glance messages, the Thinking longer task and *Background
   work finished* can't be emptied). Martlet still parses
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
  carries, what was said in earlier conversations when the message refers to
  one ([Memory › Conversation history](MEMORY.md#conversation-history)), who is
  talking when that changed, a smart home result, and the
  picked style when the persona has several and it changed. The first notes
  start with what notes are (Companion › Prompts › *Notes with messages*).
  Notes are never shown and never what the user said.
- When the conversation outgrows the context, Martlet lets go of a quarter
  more of the oldest exchanges than it must (and forgets them), so the next
  several replies start at the same exchange instead of moving by one every
  reply.
- After a reply, remembering and learning names share one request; on a
  Thinking model on this PC it continues the reply's own conversation (its
  tools described again, never run), so the model's cache still holds it for
  the next reply (see [Memory](MEMORY.md#automatic-recall-and-remembering)).
- While **Thinking longer** is on (the default) and Deep thinking can run where
  it is set to think, every reply on a route that does function calling is
  offered `think_longer` and `cancel_thinking`, always both, first and in the
  same order, with the *Thinking longer* prompt after the tools prompt, so the
  start never changes from reply to reply. A background think with the Thinking
  model continues a reply's request (instructions, tools and messages
  unchanged, then what Martlet said, then the task), so it reuses the cache
  ([Thinking longer](#thinking-longer-and-background-work)).

Each reply, glance and after-reply request writes a desktop log line such as
*Thinking input (Reply): first words after 2004 ms; 568 input tokens, 525 of
them (92 %) from the model's prompt cache.* when the provider reports it
(OpenAI, OpenRouter and others report cached tokens unasked; Martlet asks
Ollama on this PC for them), and the talk window's context line ends with
*Last reply: 92% of its 568 input tokens came from the model's cache.*

## Thinking longer and background work

Replies answer right away (Thinking steps are Off by default). **Thinking
longer** (Companion › Deep thinking; on by default, and *Where it thinks* ›
*Off* turns it off on all your computers) lets Martlet decide, sparingly, that a
task needs real thought and hand it to **Deep thinking**, which works it out in
the background while Thinking keeps talking with you: parallel thinking, so it
needs a model of its own.

**How it goes.** Replies on a Thinking route that does function calling (OpenAI
or a Chat Completions endpoint, Ollama on this PC included; not a paired host's
gateway, not a model that turned tools down), while Deep thinking can run where
it is set to think, get `think_longer(task, reason)`:
`task` is a complete, self-contained instruction (what to work out and exactly
what the result must contain), `reason` a few words on why. The tool
description and the *Thinking longer* prompt say to use it rarely (real
multi-step reasoning or long creative work such as song lyrics, a story, a plan,
tricky math or code; never casual chat or quick answers) and to tell the user
first, in character, that it'll take a while. The call returns at once
(`{"status":"started","id":"think-1",...}`); what Martlet said before the call
is already being spoken, and if it said nothing the result tells it to say so
now (a reply may end with nothing more after its tool calls). The tool never
holds up the reply. `cancel_thinking` (optional `id`) stops a think.

**The background request** runs on its own runtime with Thinking steps **On**
at the chosen effort (*Medium* or *High*: `reasoning_effort` medium/high for
Ollama on this PC, OpenAI and Gemini, OpenRouter's `reasoning.effort`, the chat
template's `enable_thinking` elsewhere, a paired computer's Ollama `think`;
the OpenAI route's models just write it out), whatever replies use. It has its
own bounds: 8,192 output tokens (Medium) or 16,384 (High), up to 65,534 stream
events and the time limit (2, 5 or 10 minutes) for the whole job. It is never
spoken. Its message continues a reply's request (Companion › Prompts ›
*Thinking longer: the task*); the picture or recording the message went with
isn't sent again.

**Where it thinks** (Companion › Deep thinking › *Where it thinks*; this PC's
own choice, `deep-thinking.json` in the data folder, never shared, because
which machine is free to think depends on the computer you talk to):

- *Off*: Martlet answers everything right away and never offers to think
  something over (Thinking longer off; saved with the reply settings, so all
  your computers share it). Choosing any place below turns it back on.
- *Same as Thinking* (default): Thinking's own model, with its tools described
  so the request starts like the reply's and shares its prompt cache, and the
  Thinking fallback. Only when Thinking's provider answers several requests at
  once (a cloud provider), never Thinking's model on this PC or a paired
  computer.
- *Another of your computers*: a paired computer's own Deep thinking model (its
  Deep thinking role: a second Ollama server of its own, route
  `martlet.gateway.deep-thinking-chat.v1`) through its pinned gateway with this
  PC's pairing, or, on a computer without that role, its Ollama (its Thinking
  role). The page offers *Add Deep thinking* for a computer that lacks the role
  (`DeepThinkingAddRole-<host>`; its dialog asks which model it runs) and
  switches Deep thinking to it once it runs, and *Change model*
  (`DeepThinkingChangeModel-<host>`) for one that has it: the role's settings
  there, with its current model selected. The old model keeps thinking until the
  new one is downloaded and loaded; then this PC (and each of your computers, on
  its next check) thinks with the new one.
  The same dialog asks how many thinks it runs at once (*Thinks at once*, 1 to
  4: its Ollama's `OLLAMA_NUM_PARALLEL`), so one graphics card counts as several
  places for background work. Ollama loads the model once and reserves one
  think's context per slot when it loads, so only the choice costs memory, never
  a running think: Martlet recommends the most that fit on that computer's card
  beside its other roles (Thinking's model with its context, the voice, lip-sync,
  listening) for each model (`DeepThinkingSlots`, shown under the choice as
  `HostInputFit-OLLAMA_NUM_PARALLEL-OLLAMA_MODEL`), so the Thinking and voice
  models are never pushed off the card. The role advertises its slots as its
  route's `maximum_concurrency`, the gateway admits that many thinks at once
  (one more gets `job.busy`), and a host check reads them
  (`HostCheck.DeepThinkingSlots`; the host's line says "Deep thinking (2 thinks at
  once)").
  The conversation's newest
  exchanges that fit the gateway's 16 KiB and 16 messages go with the task (no
  tools), and the computer loads 32,768 tokens of context for it. A computer's
  Martlet must be this version or later for thinks over a minute: an older one
  takes at most 60 seconds and 4,096 tokens a request (Martlet holds a think
  there to that and logs that the computer should be updated). A computer that
  also does Thinking for the conversation thinks only with its Deep thinking
  role, never with Thinking's own model.
- *Ollama on this PC*: a second model of its own here, beside Thinking's (a
  larger one can think while a small, fast one answers you), never Thinking's
  own model. The page shows whether it fits beside Thinking's on the graphics
  card (`DeepThinkingLocalFit`).
- *A cloud provider or server*: OpenRouter, NVIDIA Build, OpenAI or any
  OpenAI-compatible server (HTTPS, or a server on this PC), with its own key in
  Windows Credential Manager, Thinking's key for the same base URL, or none. The
  conversation that fits the model's context and the task go there, no tools.

**Always in parallel** (`DeepThinkingPlan`, shown on the page as
`DeepThinkingParallel`). A think always runs alongside the conversation and is
never paused, so it needs a model that can answer while Thinking answers you.
Thinking's own model on this PC or a paired computer can't: Ollama or LM Studio
answer one request at a time per model (Ollama on this PC runs with
`OLLAMA_NUM_PARALLEL` 1 by default) and keep one conversation in their prompt
cache, and a paired computer's gateway serves one request per job. There, Deep
thinking isn't available: `think_longer` isn't offered, the page says why, and a
single PC whose Thinking model is local simply doesn't think in the background
until another place is chosen. A paired computer's Deep thinking role is a
separate Ollama server with its own route, so it thinks in parallel even on the
computer that does Thinking (they share its graphics card).

**A second model on this PC.** Ollama runs each loaded model in a process of its
own, so a second model answers at the same time as Thinking's, without touching
its prompt cache, but only while both fit on the graphics card: otherwise Ollama
unloads one (Thinking's, when idle) or makes a request wait until one finishes,
either of which would hold up a reply. So before each think Martlet has Ollama
load Thinking's model if it isn't loaded (as the talk window's warm-up does),
then compares what both take (Ollama's own figure from `/api/ps` for a loaded
model, what it reported earlier in the session, else the download size from
`/api/tags` times 1.2 plus 0.5 GB for context and buffers) with the graphics
card's memory less what other programs use (nvidia-smi; else Windows' total for
the card less Ollama's other models) and 0.75 GB kept free (`OllamaSideBySide`).
When they don't fit, the think doesn't start and its result says why (*gemma4:12b
(about 9.5 GB) doesn't fit beside gemma4:e4b ...: choose a smaller model*). While
its model loads, Martlet watches `/api/ps`; if Ollama unloaded Thinking's model
or pushed part of it off the card after all, it stops the think, unloads the
think's model and loads Thinking's again. The two share the graphics card's
compute, so replies may start a little later while a think runs there; another
computer or a cloud provider leaves the conversation's hardware alone. Running
Thinking's own model twice needs a second server (for example another `ollama
serve` on its own port, chosen as a server on this PC under *A cloud provider or
server*), which holds a second copy of the model in graphics memory.

**While it runs** you keep talking and Martlet keeps replying. The talk window's
header shows a background tasks chip (a spinner and *1 running*, then *1 ready*
once it finishes and *1 done* once Martlet brought it up); clicking it opens the
task list over the conversation: one card per task with its kind, what it is
about, its status and time, its result (*Show result*) and *Cancel* (`LiveTasks`,
`LiveJobs`, `LiveJobState-<id>`); the desktop log notes each start, fit check and end
(`Background thinking:`) and a *Thinking input (Background thinking)* line.
Stop (Esc) ends a reply, never a think; the task's Cancel, `cancel_thinking`,
closing the conversation, quitting Martlet or the time limit do. At most one
think runs at a time (a second call is refused and Martlet is told to wait or
cancel the first) and at most 3, 6 (default) or 12 start in any hour.
**Delivery.** When a job finishes (or fails, or runs out of time) its result is
added at the end of the conversation as a new message, never by rewriting
anything before it:

- *As soon as Martlet is free* (default): once nobody is talking, nothing waits
  to be answered, no reply, look or other work runs, Martlet isn't paused and
  the conversation has been quiet for 2 seconds, Martlet starts a reply of its
  own whose message is its note with the results (Companion › Prompts ›
  *Background work finished*). It brings it up in character, offering rather
  than acting when a result needs the user's go-ahead, with the normal tools
  available. Talking before it speaks (or over it) stops it, and Esc holds it:
  the results then go with your next message.
- *When I talk next*: the results go in the notes of your next message
  (Companion › Prompts › *Background work finished, with your message*).

Either way the note and Martlet's answer stay in the conversation like any
exchange. A job you canceled is only mentioned with your next message; one
Martlet canceled isn't mentioned; closing the conversation drops the rest.

### Background job API (for new kinds of background work)

`Martlet.Conversation.BackgroundJobs` (one per `LiveConversationController`,
`controller.Jobs`) runs any kind of background work beside the conversation;
think_longer is the first kind and a song is next. A kind is a
`BackgroundJobKind(Name, MaxActive, MaxPerHour, TimeLimit, Offer, Doing)`:
`Name` is lowercase letters and the job IDs' prefix (`think-1`, `song-1`),
`MaxActive` how many of that kind may run at once (other kinds run alongside),
`MaxPerHour` how many may start in any hour, `TimeLimit` how long one may take
(up to 30 minutes), `Offer` marks a result to offer before using it (a song:
*wanna hear it?*), and `Doing` is what the talk window calls a running one
(*Making a song*). To add one:

1. Give the kind a tool on the reply's toolset the way think_longer does
   (`LiveConversationController.BuiltIns`: always offered while its feature is
   on, so the request start never changes; the handler returns at once).
2. In the handler, call `jobs.Start(kind, label, runAsync)`. `label` is a few
   words for the task list and the conversation (it is never logged). `runAsync(job,
   token)` does the work on a thread-pool thread and returns
   `BackgroundJobOutcome.Done(result)` (the text the conversation gets; for a
   song, what is ready and how to play it, such as its ID) or
   `BackgroundJobOutcome.Failed(problem)` (a few plain words); the token is
   canceled by Cancel, the conversation ending, Martlet quitting and the time
   limit. While it works it may call `job.Report(Running/Waiting/Paused,
   "a few words")`, which the task's card shows. `Start` returns `BackgroundJobStart`:
   the job, or `Refusal` (`busy`, `hourly_limit`, `closed`) with a `Message` to
   tell the model.
3. Return something like `ThinkLonger.Started(job, toldUser)` to the model.
4. Background work runs in parallel with the conversation, never in turns with
   it: run it where it doesn't hold up a reply (a song made by a host role on a
   computer that isn't speaking, a provider of its own), and check first that
   it fits beside the conversation when it shares a machine with it (as a
   think on a second model in Ollama on this PC does with `OllamaSideBySide`),
   refusing with `BackgroundJobOutcome.Failed` when it doesn't.

The job list does the rest: limits, cancellation, the time limit (`TimedOut`),
the header chip and task list (`LiveTasks`, `LiveJobs`; give a new kind its title and icon in
`LiveConversationWindow.KindTitle`/`KindGlyph`), `background-jobs.json` (kinds, states and times only),
and delivery: `Take(onItsOwn)` hands finished jobs to the next reply, which
completes or returns them, and `BackgroundJobs.ReportMessage` /
`ReportNotes` word them (with `Offer` kinds marked to offer first). The model
acting on the user's yes is an ordinary later tool call in that conversation
(for a song, `play_song` with the song's ID; see [Singing in
conversation](#singing-in-conversation)).

## Reminders

*"Remind me to do the dishes in an hour."* Every reply on a route that does
function calling gets Martlet's own `reminders` tool (last, after
`manage_memories`, always worded the same so the start of every request stays
the same): `set` with `text` and `in_minutes` or `at` (a local time such as
*17:30*, *5:30 pm*, *tomorrow 9:00* or *2026-12-24 18:00*: the next such time,
up to a year ahead), `list` and `cancel` with an `id`. Set answers with the id
and when it is due, so Martlet confirms it with the right time without a clock
of its own.

**When it is due**, Martlet brings it up the way it brings up finished
background work, whatever *When it shares the result* says for Thinking longer:

- *Martlet is free* (nobody talking, nothing to answer, no reply or look, not
  paused, quiet for 2 seconds): it starts a reply of its own whose message is
  its note (Companion › Prompts › *Reminder due*): *"Hey, it's dishes time!"*
- *You talk first* (or Martlet is mid-reply and you talk again): the reminder
  goes in the notes of your message (Companion › Prompts › *Reminder due, with
  your message*), so it fits into the answer: *"Nice job on that boss! Oh, and
  by the way, you wanted me to remind you about the dishes."*

When no conversation runs on that PC, Martlet starts one without the talk
window (as Start listening does, but without listening) to say it. Where Martlet
can't talk (Thinking isn't set up), a Windows notification shows the reminder
after 30 seconds instead. A reminder due while no companion PC ran is said late
with how late it is when Martlet starts within 12 hours, and let go after that.
The talk window's task list shows a due reminder (*Due now. Martlet brings it
up as soon as it's free.*) until it is said.

**On all your computers.** Reminders travel with the [shared
settings](CLUSTER.md#one-martlet-on-every-computer) as one entry per computer,
`reminders.<device ID>`: the reminders set there and what that computer did
about anyone's (offered, took, said, canceled, let go). Only that computer
writes its entry, so nothing conflicts and no host needs updating; any computer
lists and cancels any reminder. When it is due and other companion PCs could
say it, **the one you used most recently says it, once**:

1. each running companion PC records an offer with how long since someone used
   it (keyboard, mouse or talking with Martlet there) and syncs at once;
2. 6 seconds later it syncs again and the offer with the shortest idle time
   (then the lowest device ID) takes it; the others stay quiet;
3. that PC says it and marks it said, and every computer sees it settled.

A PC that took it and didn't say it within 10 minutes (it closed, say) lets the
others try again. A PC alone (sync off, or no other companion PC) says it at
once. Two companion PCs that can't reach a host meanwhile can't see each other's
offers, so each says it. Saying the same line on every computer at once was
left out on purpose: in one room the voices would echo, and a reply already
under way on one PC would be cut across; the PC you are at is where you hear it.

Checked locally: the tool, times, offers, taking it and wording with
`RemindersTests` and `SharedRemindersTests`, the real controller and talk window
bringing a due reminder up on its own through a fixture Thinking endpoint while
finished work waits for the next message, and the tool offered last with every
request starting the same (Desktop tests), the whole flow on two simulated
PCs with MCP `reminders_check`, and on a disposable data folder through
`-Desktop` a due reminder taken by the desktop, brought into a conversation
started without the window and, with no Thinking set up, shown as a
notification and marked said (`reminders_status`). A real model setting and
saying one, and two real companion PCs through a real host, are **NOT RUN**.

## Singing in conversation

*"Martlet, sing me a song."* Martlet answers in character (*"Ooh, I'd love to!
Let me work on a song for you."*) and calls `sing_song` in that same reply,
choosing what the song is about itself when the user didn't say (small models
told to talk first and call afterwards, or given a line to say, often said they'd
sing and never called it, so nothing was made). It makes
the song in the background while the conversation carries on, brings it up when
it's ready (*"Nice job on killing that noob! Oh, and that song's ready, wanna
hear?"*) and sings it on a yes. It is offered while singing is set up (Companion
› Voice › Singing, see [Singing](SINGING.md)) and the Thinking route does
function calling; replies then always get the same three tools after Martlet's
other own tools (`think_longer` and `cancel_thinking` while Deep thinking can
think), with the *Singing* prompt (Companion › Prompts), so the start of every
request stays the same. Without singing set up, requests are exactly as before.

**`sing_song(about, lyrics?, style?, duration?)`** starts a `song` job (one at a
time, 4 an hour, 15 minutes at most; `Offer`, *Making a song*) and returns at
once; the result tells Martlet to tell the user now if it hadn't. Without
lyrics, the job first writes the title, style, tempo, key and tagged lyrics with
a background think on Deep thinking that continues the reply's request exactly
like think_longer's (Thinking steps On, alongside the conversation, checking a
second model in Ollama on this PC fits beside Thinking's first; Companion ›
Prompts › *Singing: writing the song*; its own runtime and authorization, and
*Thinking input (Song lyrics)* in the log; at most Thinking longer's time limit).
Where Deep thinking can't think (Thinking's own model on this PC or a paired
computer, which can't think something over while it answers), the result asks
the reply to write the lyrics itself and call `sing_song` again with them. Then
the song maker
(`ISongMaker`, the singing host) makes it in the voice Martlet speaks with and
the Singing card's quality and voice match; the task's card follows its stages
(*Writing the lyrics*, *Writing the music*, *Matching the singing to the
voice*..., *Timing the mouth to the singing*). Its mouth track is made once,
from the vocals stem (never the mix; see *Lip sync* below). The finished song is
kept as a creation of the `song` kind in Martlet's shared Creations library
(`SongCreations`: the mix, vocals and backing as FLAC, its map (lines and words
with their times, the beat grid) and its mouth track as JSON; the title, lyrics,
voice, engine and where the mouth came from in its entry), which copies it to
every paired Martlet computer, so any of them can sing it. Old songs are cleaned
up when a new creation needs the room. The job's result gives the song's ID (its
creation key, such as `3fa2c19b0d71`), title, length and map (each section and
line with its start time) and says to offer it.

**`play_song(song_id, from)`** (or the Creations library's `perform_creation`
with `{"from": ...}`) is the only way a song plays: Martlet performs it in
conversation; nothing in the UI plays one. It plays through Martlet's voice
output: the backing and vocals as two sample-aligned streams mixed on this PC
into one stream beside the speech stream on the same output device (Windows'
volume for Martlet applies to both). The speech bubble shows the line being
sung; the subtitles and the talk window show it word by word as it is sung
(karaoke, from the sung words' times).
`from` is `start`, `resume` (the line where it stopped; `resume section` for
its section), a section (`chorus`, `verse 2`, *second verse*), `line:N` or a
time (`1:05`; a line starting within a bar of it is where it goes). From
anywhere but the top it never starts hard in the middle of the music
(`SongTransport.PlanStart`): the backing enters on the latest downbeat that
leaves at least half a bar and 1.5 seconds before the singing (one bar before
the line; two for a long pickup or short bars) with an equal-power fade-in over
that bar, and the vocals stay muted until 80 ms before the line's onset. If
Martlet is still talking when the lead-in reaches the line (*"Okay, where was
I... oh right!"*), the band repeats that bar (at most 4 times, crossfaded on the
downbeat) under its words and goes into the line once it's done. A song that
had ended resumes from the top.

**Stopping.** `stop_singing`, a stop request heard while it sings
(`BargeInPolicy`'s song rule: a stop word with Martlet's name or with *sing*,
*singing*, *song* or *music*, checked by the quick word check as it is said and
by the transcript), the talk window's *Stop singing* and the talk button stop
it musically (`SongTransport.PlanStop`): the vocals finish the word (the next
dip in their loudness within 0.6 s, then a 150 ms fade) and the backing rings
to the next beat and fades over one beat, about 1-1.5 s after the request. Stop
(Esc) and ending the conversation fade both in 300 ms. Where it stopped is
recorded at once (song time, section, line number and words, and why: the
user's words, the button or Martlet's own call) and goes at the end of the
conversation as a note with the next message, never rewriting anything before
it: *You stopped singing "Morning Light" (3fa2c19b0d71) at 0:22, in verse line 4
of 12, "When you're laughing like before" because the user said: "okay okay
Martlet, stop singing". play_song with from=resume restarts that line.*
`stop_singing`'s own result carries the note instead; a song that played to its
end leaves *You finished singing ...*.

**While it sings** the song keeps going (`PlaybackMode.Song`). Always listening
keeps listening when barge-in is on or echo reduction works (the song is taken
out of what the microphone hears), otherwise it holds off as for a reply; hearing what
this PC plays holds off unless Windows leaves Martlet out. What is heard passes
the utterance filter and goes to the Thinking model with the *Said while you
were singing* note (the song and where it is), so Martlet answers only when
talked to and otherwise stays quiet (`[pass]`). When it does answer, or anything
else speaks, the song is turned down 12 dB under the speech (60 ms ramps) and
back up after; the character's mouth follows the speech meanwhile. Finished
background work and screen glances wait until the song is over.

**Lip sync.** The character's mouth follows the sung words from a mouth track
made once, when the song is made, from the vocals stem only (`SongMouthTrack`,
ARKit mouth blendshapes and an overall opening every 20 ms): Audio2Face run over
the vocals offline (this PC's service on the character's endpoint, or the paired
lip-sync host's relay) when one answers; otherwise visemes from the sung words'
times (the song maker's `SongResult.Words`, or each line's words spread over its
singing), each word's spelling turned into shapes (aa, ih, ee, oh, ou, closed
lips for m/b/p), opened as far as the vocals are loud and blended between
sounds; the vocals' loudness only as a last resort. Its timing against the vocal
onsets is measured when it is made and logged (*mouth opens +10 ms from the
vocal onsets (median ...)*). During playback the track is read at the song time
being heard, on the song's own playback clock, so it stays on the words after a
lead-in, a vamp or a resume from any line, and the mouth stays closed while the
vocals are muted. With Automatic lip-sync and the character's mouth mapping, its
blendshapes go through the same composition and reset/apply frames as
Audio2Face for speech (VRM's aa, ih, ou, ee and oh; Live2D's mouth open and form);
otherwise its opening goes to the loudness mouth. A reply's Audio2Face frames
own the face while they play; the song's mouth takes it back (with a new
playback identity) when they stop, and pauses while Martlet talks over the song.

**Latency.** Song work never holds up a reply: the job runs on its own runtime,
the song maker on its own computer, and the lyrics are written only where Deep
thinking runs alongside the conversation. Playback never blocks the reply
slot. The tools and prompt only change the request while singing is set up, and
then they stay the same reply after reply, so they stay in the prompt cache.

The talk window's song panel (`LiveSongPanel`) shows the song playing (`LiveSong`:
its ID, state, position, line number and section; `LiveSongLine`: its title and
the line so far) with *Stop singing* (`LiveSongStop`), or where the last song
stopped and why, or that a new one is ready for Martlet to offer. It has no Play
button: only Martlet performs songs.
`songs-status.json` in the data folder has the song playing and the last stop
(never a title or words). MCP's `songs_status` and `song_playback_check` read and
exercise it ([MCP](MCP.md)).

[Memory](MEMORY.md) is ON by default (Companion › Memory turns it off). When
on, each explicit typed/PTT/hands-free turn automatically recalls up to twelve
saved facts (best lexical matches for the current input, then the newest) as one
labeled background block inside the same input budget; the full store, path and
consent UUIDs are never uploaded. If the store can't be read, the reply goes
ahead without memory and the status says why. After a completed reply, the
exchange is sent once more, as one extra text-only request (shared with
learning names when both are due), to the same Thinking model, which picks out
lasting facts to save locally (shown under the reply and
listed in Memory). Screen glances are never remembered. The exchanges kept in
mind for the next replies stay in memory only; separately, while memory and
*Keep a record of my conversations* are on, each finished exchange is added to
the [record of conversations](MEMORY.md#conversation-history) on this PC, which
a later message that mentions an earlier conversation brings back. TTS receives only eligible
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
| Background think (think_longer) | One at a time, 3/6/12 an hour; its own text-only runtime and authorization, never spoken; Thinking steps On at Medium or High; 8,192 or 16,384 output tokens, 65,534 stream events and 16 MiB; the time limit (2, 5 or 10 minutes) for the whole job; at most one declined tool round |
| TTS | At most eight requests, 1536 input UTF-8 bytes each / 12,288 total; 10 seconds / 240,000 samples reserved per request, 80 seconds / 1,920,000 samples total; at most 20 seconds per request. Reaching this budget ends speech for the reply, not the reply's text |
| Content and timeline | Current bounded input/transcript/answer/refusal in memory; 32 metadata timeline entries, existing bounded engine event rings; no audio files or ordinary content logs; finished exchanges (the user's own words and the reply, never audio, glances or what the PC plays) go to the [record of conversations](MEMORY.md#conversation-history) on this PC only while memory and *Keep a record of my conversations* are on |

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
Unlocking resumes the listening and watching you had started; a stopped mic or
watching button stays stopped until clicked. Native/credential/HTTP work and cleanup
run off the dispatcher; the UI remains responsive. Noncooperative native work
or callbacks can outlive a timeout or closed observer. The shared slot remains
reserved; failed cleanup is quarantined rather than replaced with a fresh
factory. Closing the main window exits the app, not a background tray listener.
This is not a measured 250 ms physical-stop guarantee.

The fixed **Stop (Esc)** control also drops a typed message still waiting to be
sent, and what always listening heard that was still waiting for a reply, and
stops watching (*Start watching* turns it back on); listening itself carries on. Escape works from the message box, the
history and the held talk button. Releasing Space after Escape cannot send that
discarded recording or rearm PTT. Stop during settings loading or a slow worker
requests cancellation without releasing the shared ownership slot early.
Partial response text remains available; stopped speech is not replayed.
The shortcut is local to this conversation window, not a system-wide hotkey.

A job changed while the talk window is open (Thinking's model or provider, the
Listening or Speaking engine, the computer that does a job, the voice or the
Thinking fallback, saved in Companion or on the Devices map) needs no reopening:
the window takes the saved setup once no reply or turn is running, keeps what
was said so far as context and says *Your setup changed. Martlet picked it up
and carries on.* (logged as *The open conversation follows the changed setup
between replies: ...*). It doesn't stop a reply to switch.
Switching to an engine first gets it ready where that is possible (a host role is
installed and answering, a local Ollama model is downloaded and loaded) and only
then saves the change, so the old one keeps answering until the switch.

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
  each finished sentence goes to the voice right away. Only sentence ends
  (`.`, `?`, `!` followed by a space), a new line and the end of the reply
  break a reply into pieces; commas, semicolons and dashes never do, so each
  piece is one or more whole sentences, which sounds more natural.
- **Where each persona's voice pauses.** Each piece is said on its own, so a
  break in the wrong place sounds awkward ("That was a wonderful idea. |
  Cutie!"). Personality › **Where the voice pauses** sets, per persona, which
  sentence ends may break a reply: periods, question marks and exclamation
  marks, all on by default. A stop that is off doesn't break until the piece
  has grown long (100 characters); then any sentence end does, so a piece
  rarely runs past what the voice can say at once (past the voice's byte limit
  it is cut there). **Say a
  short ending with the words before it** (up to two words by default; *Never*
  turns it off) keeps an ending such as ". Cutie!" with the piece
  before it: each piece waits until a few more words have streamed in (or the
  line or reply ends) before it goes to the voice.
- **Overlapped synthesis.** While one sentence plays, the next is already being
  synthesized (one sentence ahead, never more), so there is no synthesis gap
  between sentences. A voice failure on the next sentence surfaces only when
  playback reaches it, so what is already playing finishes; then the voice
  stops for the rest of the reply while its text keeps streaming.
- **Barge-in.** Optional and off by default. Always listening keeps the
  microphone open while Martlet speaks either way (whenever echo reduction
  works; see [listening while Martlet speaks](#hands-free-voice-activity-and-voice-id)),
  so what you say then is heard and answered after the reply. Ticking
  *Let me interrupt Martlet by talking* in Companion › Listening also lets you
  stop a reply by talking over it. Talking over a reply with real words
  stops it: the Thinking request is canceled, the queued audio is dropped and
  what you said is answered next, with the reply so far kept in context. Only
  the microphone can do this, and only with words (`BargeInPolicy`, the one
  place that decides): a stop word ("stop", "wait", "hold on", "shh", "never
  mind"...) or Martlet's name stops it at once, a quick backchannel ("yeah",
  "right", "okay", "mm-hmm", "thank you") never does and is answered after the
  reply, and anything else needs two words (*Word check* Normal; one on
  Sensitive, three on Relaxed). A hum, a long "mmmm", laughter, a cough or a
  breath never stops Martlet, however long it lasts. With Parakeet on this PC
  as Listening, Martlet checks your words while you talk (`BargeInGate`): once
  your voice has gone on for 300 ms (200 ms Sensitive, 450 ms Relaxed), again
  100 ms later, then every 400 ms more, and the moment you pause for 160 ms, a
  quick Parakeet transcript of what you said so far (with the listener's
  pre-roll) goes through the [utterance filter](#listening-for-words) and the
  policy. A check never blocks the microphone, at most one runs at a time and
  each stretch of voice gets at most six. On this PC (MCP
  `utterance_filter_check`, Windows-voice fixtures, Normal) "Stop!" stopped a
  reply 440-590 ms after the voice began, "Wait, hold on a second." about
  590 ms and "Can you tell me more about that?" about 440 ms; each check took
  about 100-250 ms of Parakeet on the processor. The check uses Listening's
  own Parakeet model; a few words it was far from sure of (its mean and its
  least sure token probability both low: under 0.65 and 0.36 on Normal) don't
  stop a reply on their count alone, since Parakeet's English models write
  "Come on." or "Cosmos was" for a laugh, and a word said over and over
  counts once (v2 wrote "One, one, one." for "ha ha ha"), while a stop word or
  Martlet's name still does. With each of the three Parakeet models, `utterance_filter_check`
  stopped for "Stop!", "Wait, ..." and the question and never for laughter, a
  hum or noise. With a host's or a cloud
  speech-to-text there is no quick check (it would be a second upload): the
  utterance's own transcript decides once you pause, so Martlet stops after
  your pause and the transcription. Each stop writes *Barge-in: Martlet stopped
  its reply N ms after you started talking over it (why; how it knew)* to the
  desktop log (never what you said). What this PC plays never stops Martlet:
  the PC listener ([hearing what this PC plays](#hearing-what-this-pc-plays))
  never interrupts anything, and with [echo reduction](#echo-reduction) the
  microphone's frames that were the speakers' sound don't count toward the
  voice. Words that were heard but didn't stop Martlet wait and are answered
  after the reply; restarting a reply because you kept talking applies only
  before Martlet starts saying it. Through speakers this relies on echo
  reduction (on by default); if Martlet still stops itself, use headphones or
  turn the choice off. With it off (the default), talking while Martlet speaks
  is still heard (with echo reduction working) and answered after the reply, but
  never stops it; Stop, Esc or the talk button interrupt. Preferences
  saved before barge-in became opt-in had it on only because it was the old
  default, so it starts off once after updating; tick it again to use it.
- **What is said aloud decides what stops it.** Each reply carries a playback
  mode (`PlaybackMode`: `Reply`, or `Song` for singing). A song keeps going
  while you talk and stops only when asked to: a stop word with Martlet's name
  or with "sing", "singing", "song" or "music" ("okay okay Martlet, stop
  singing"). The mode is `LiveConversationController.Start(..., playback:)`
  and the controller's `Speaking`; the policy reads it, so a new kind of
  playback plugs in there without touching the listener.
- **Measured.** Each spoken reply's snapshot reports `FirstTextAfter` and
  `FirstAudioAfter` (from the start of the reply), and the desktop log records
  them as *Reply latency: first words after … ms, first audio after … ms*.

## Listening for words

Always listening hears everything near the microphone, and speech-to-text
writes something for much of it: "Mm.", "Hmm.", "Uh-huh." for sounds that
aren't words, "Yeah." for a cough or a breath (Parakeet and whisper both do),
"Thank you." or "Thanks for watching!" for silence and music (whisper learned
those from subtitles). `UtteranceFilter` (Martlet.Providers) drops these
before they become a turn or stop Martlet; push-to-talk and typing are never
filtered. It is local and deterministic: about 3-8 µs per utterance on this PC
(`utterance_filter_check`'s `filterCost`), no request, and nothing in the
Thinking request changes, so the time to Martlet's first word and the prompt
cache are untouched.

- **Not words.** Only fillers and vocalizations (mm, hmm, uh, um, er, ah, oh,
  huh, uh-huh, mm-hmm, and their stretched forms), laughter (haha, "Ha ha ha
  ha."), sound markers ([Music], (coughs), *laughs*, ♪) or punctuation.
  "Uh-huh" and "mm-hmm" count as an answer for 30 seconds after a reply that
  ends by asking something.
- **Made up from noise.** Phrases speech-to-text writes for noise ("Thank
  you.", "Thanks.", "Bye.", "you", "Yeah.") go when the evidence that they were
  said is weak; credits and calls to subscribe ("Subtitles by the Amara.org
  community", "Thanks for watching!") go unless the engine was clearly sure.
  Text that repeats itself (whisper's compression ratio above 2.4) goes too.
- **Evidence.** What the engine says where it says it: Parakeet's mean and
  lowest token probability (`TranscriptionEvidence`, carried by
  `ILocalTranscriber` and `TranscriptionResult.Evidence`; uncalibrated, never
  the participation policy's confidence) and, for any engine that gives them,
  whisper's no-speech and average log probabilities. And always how much of
  the utterance was a voice: the loud 20 ms frames the speakers don't explain
  (`LiveConversationOperation.Voiced`), and how long that voice went on, from
  its onset to the silence after it with short pauses included and the frames
  the speakers explain left out (`LiveConversationOperation.Speech`;
  `BargeInGate.Speech` for a quick check). Measured on this PC: Parakeet's
  "Yeah." for a cough had a mean of 0.67-0.70 but a lowest token of
  0.20-0.32, a Windows voice saying it 0.79 and 0.51; whisper.cpp's "Yeah."
  for two coughs had a word probability of 0.07. A paired host's whisper and
  cloud speech-to-text send only text: whisper.cpp's `verbose_json` (with the
  probabilities) took about 65 ms longer per utterance on this PC (190 against
  255 ms), so Martlet doesn't ask for it, and there the voice decides (a phrase
  speech-to-text makes up needs 400 ms of voice on Normal).
- **Too many words for the speech.** More than about seven words per second of
  speech plus one ("I think the second one is better." from 300 ms of
  speech). It counts how long the voice went on, not only its loudest frames:
  fluent speech is often under half that loud, so "I'm gonna make it public."
  (460 ms of loud voice in about a second of speech, Parakeet sure of every
  word) was once dropped as *5 words from 460 ms of voice*.
- **Unsure and lone words.** A short utterance the engine was unsure of, a
  lone word that says nothing on its own ("the", "so", "you"), or a lone word
  shorter than 200 ms of voice. Short answers and commands ("yes", "no",
  "stop", "wait", "okay", "hello") stay, more readily right after Martlet asked
  something, and anything with Martlet's name (or the persona's) is kept.
- **Word check.** Companion › Listening › *Word check* (`TalkWordCheck`,
  `TalkPreferences.WordCheck`, shared with your other computers): Relaxed,
  Normal (default) or Sensitive (`ListeningSensitivity`) sets the evidence
  thresholds (`UtteranceFilter.For`), the voice before barge-in checks words
  and how many words stop a reply. Relaxed also needs a lone word to be
  clearly heard (Parakeet 0.75) and drops a lone "Yeah." that isn't an answer.
- **What you see.** An ignored utterance shows as a faded note in the talk
  window, *Ignored "Mmm" (not words).*, several in a row sharing one note, and
  the desktop log records *Always listening ignored what it heard: reason
  (kind, voice in speech, evidence, word check)*, never the words. What the PC plays
  that isn't words is simply let go.
- **Martlet stays quiet.** What passes the filter but isn't meant for Martlet
  (people talking in the room, a muttered word) still goes to the Thinking
  model with the always-listening instructions (Companion › Prompts ›
  *Always listening*), which ask it to answer exactly `[pass]` then; the reply
  is never spoken or shown, the talk window notes *Martlet stayed quiet.*, the
  latency line ends *; Martlet stayed quiet* and the pass stays in context.
  `StayQuiet` (Martlet.Conversation) reads only the reply's text, so it works
  the same for a transcript and for a Thinking model that hears the audio.
- **Thinking models that hear.** With no transcript there is nothing to filter:
  the voice-activity gate (`ListeningOptions.MinimumUtterance`, the speakers'
  frames left out) and `StayQuiet` still apply. Barge-in still needs words:
  Parakeet's quick check when Listening is Parakeet on this PC (it needs no
  turn transcript), otherwise whatever transcript the route has.

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
  restarts listening without it. Echo reduction is also what lets always
  listening go on while Martlet speaks: without it (off, or not running) and
  without barge-in, listening pauses while Martlet speaks so it doesn't hear
  itself.

## Thinking models that hear and see

With Companion › Listening › **Let Thinking hear my voice** on, the recording of
what you said goes to the Thinking model, when the model hears: straight away on
its own, or with the transcript ([Straight to Thinking](#straight-to-thinking)),
as an `input_audio` WAV part, so only an OpenAI-compatible (Chat
Completions) endpoint takes it. **Ollama on this PC** does too, for models it
says hear (Ollama 0.35 and later; Gemma 4 E2B, E4B and 12B). A paired host's
Ollama and OpenAI's own route don't. Vision works the same way with pictures.

**On or off.** Your own choice always wins: tick it, or untick it to keep
Thinking to the transcript. Until you choose, it is **on only while the
recording stays on this PC**: Thinking is Ollama on this PC (`http://127.0.0.1:11434/v1`)
with a model that isn't one Ollama forwards to its cloud (a `:cloud` or `-cloud`
tag). Another server on this PC's loopback (llama.cpp, LM Studio, a LiteLLM-style
proxy) needs the tick too, since it may send the audio on. So a new setup with
Gemma 4 E2B in Ollama hears you straight away, while a cloud or paired-host
Thinking model never gets your recording until you tick the box. The line under
it says which applies (*On: your voice stays on this PC (Thinking runs here), so
Thinking hears it unless you turn this off.* or *Off until you tick it: your
recording would leave this PC for ...*), and the conversation checks again for
each message, so switching Thinking to a cloud model stops the recordings at
once. `talk-preferences.json` keeps the choice as `true`, `false` or nothing;
one saved before this (version 3 or older) with `false` counts as never chosen,
since off was only the old default. Shared with your other computers: an older
Martlet's off is likewise taken as never chosen.

Martlet finds out what a model takes instead of guessing from its name:

- **From the server, automatically.** Choosing, testing or checking a Thinking
  model asks its server for the model's metadata (the same request as the
  context check, no conversation content): OpenRouter's model list
  (`architecture.input_modalities`), Ollama's `/api/show` (`capabilities`:
  `audio`, `vision`), llama.cpp's `/props` (`modalities`), LM Studio's model
  type or a `capabilities` list. Hosted APIs that don't publish this (OpenAI,
  Gemini, NVIDIA Build) keep the name-based guess.
- **Test hearing** (Companion › Listening, under the switch) sends the model one
  short recording of a random word said by Windows speech (never your voice)
  and asks which word it heard, with Thinking steps off. Saying the word means
  it hears; another answer, or the server refusing the audio, means it
  doesn't. On this PC it stays local; a cloud model asks first, since it is one
  small request with your key.
- **A refused recording.** When a model rejects a reply's recording, Martlet
  asks again with the transcript only and remembers that the model can't hear.

What it finds is kept in `model-abilities.json` (the model, the server, whether
it hears and sees, where that came from and when) and shared with your other
computers as the `model-abilities` setting ([shared
settings](CLUSTER.md#one-martlet-on-every-computer)), so a model is found out
once. A conversation uses it at once. The Listening and Vision pages say what is
known and where it came from (*This Thinking model can hear (Ollama on this PC
says so, checked 3 Oct)*). MCP `model_ability_check` and `hearing_check` show it
([MCP](MCP.md)).

### Straight to Thinking

While Thinking hears and **Let Thinking hear my voice** is on (by default with a
model on this PC, see above), Companion ›
Listening asks **When Thinking can hear you** (shared with your other computers
like the other listening choices):

- **Send my voice straight to Thinking (fastest)**, the default. Once always
  listening hears you stop (the end-of-speech pause), the reply's request starts
  at once with your recording alone: an `input_audio` part beside a short
  stand-in text, *(spoken: listen to the recording)*, never a transcript, and
  the picture of what vision watches when that goes along. Companion › Prompts ›
  *Your recorded voice, without a transcript* tells Thinking to listen to it.
- **Transcribe first, then send both**: the reply waits for speech-to-text, then
  sends the recording with the transcript (*Your recorded voice*), as before.

On the straight path speech-to-text still runs, beside the reply and never in
front of it. Where Listening and Thinking both run on this PC (Parakeet and
Ollama), it starts once the reply's first audio plays (its first words without
a voice, or when it ends; at most 4 seconds later), so the two never compete for
the processor before you hear Martlet (except the quick check of something short,
below); elsewhere it starts at once. Its words:

- **Talk window.** Your bubble shows *(your voice; transcribing…)* until the
  words come, then the words. When speech-to-text couldn't transcribe it, the
  bubble goes away and nothing takes its place (Martlet's reply, if any,
  stays). One the word check wouldn't count as words keeps
  them with a note (*Word check: not words. Thinking heard it anyway.*): Thinking
  already heard it and decided, often with `[pass]`.
- **The conversation.** The exchange is kept as soon as the reply ends and the
  words replace the stand-in once they come, with the notes the message went
  with, so later requests carry the transcript (never the recording, which is
  dropped after its turn as before) and the after-reply request, which continues
  the reply's own request on a model on this PC, already has them. A reply that
  starts while an earlier message's words are still on their way waits for them
  for at most 1.5 seconds, then carries that message as the stand-in until they
  come. Words speech-to-text couldn't make keep the message as *(spoken;
  speech-to-text couldn't transcribe it)*.
- **The record of conversations, memory and learning names** get the words like
  any spoken message, once they come; words the word check wouldn't count go to
  the record marked (*(not words) mm*) and are never remembered.

What needs your words before the request does without them on the straight
path: memory recall goes by the conversation so far and who is speaking (the
speaker's and the newest facts), lorebook entries trigger on the conversation
but not yet on this message, a message isn't checked for a mention of an
earlier conversation (search_conversations still works), and Home Assistant is
reached only through its tools. Tools, finished background work and vision go
with the message as always. The checks that need no words still apply before
anything is sent: the voice-activity gate (an utterance shorter than 0.45 s, or
mostly what the speakers played, is let go), Voice ID and who is speaking (the
reply waits for voice recognition, at most 3 s, as Thinking is told who
talks). Always listening's instructions let Thinking stay quiet with `[pass]`,
which works on the reply's text as before. Barge-in keeps its own quick check.

**Not words.** Something short that went straight (less than 1 s of voice in
all, measured by the voice-activity detector) may be a cough, a hum or *mm*
rather than words. With Parakeet on this PC as Listening, its speech-to-text
starts the moment the reply's request has started (beside it, never before it),
and the word check decides. Not words (nothing, *mm*, laughter, a sound) and
nothing played yet: the reply stops silently, its exchange is never kept,
recorded or remembered, and the talk window shows the faded note instead of
your bubble (*Ignored "M" (not words).*). Once the reply's first audio has
started, it finishes and the words are only labeled, as before. Longer speech
is unchanged (the 0.45 s gate, the speakers-mostly check and `[pass]`). The log
says *Not words: Martlet dropped its reply before it played; the quick check
found ... N ms after the reply started* or *Not words, too late: ...*.

Some messages are transcribed first anyway: push-to-talk, a message with what
the PC played (`[PC audio]` lines are transcripts, context rather than you, so
your words go beside them as words), something said over Martlet while it
speaks that the quick check didn't decide (its words decide whether it stops
Martlet), a message for Home Assistant's Assist (Smart home control on without
model tools), and any message once the model refused a recording.

**A refused recording.** If the model refuses the recording, the reply waits
for the words and asks again with them (the Thinking fallback gets the words
too), and Martlet remembers that the model can't hear, so later messages are
transcribed first with the transcript only. Without a model that hears, nothing
changes.

**Prompt cache.** Every request starts the same way: the instructions are the
same from one straight message to the next, and earlier messages go exactly as
the conversation keeps them. Ollama on this PC reuses its cache only for a
request that continues a whole earlier one, and a request that carried a
recording is never continued by the next reply (its recording isn't sent
again), on either path. The after-reply request (remembering and learning
names, when either runs), which continues the reply's request with the words in
place of the recording, keeps the conversation in the cache for the next reply;
[Voice latency](VOICE_LATENCY.md#straight-to-thinking-measured) has the measured share.

**Logs.** Each reply with your recording logs *Voice path: straight to Thinking
(your recording alone, no transcript)...* or *Voice path: transcribe first
(your recording with the transcript).*; a straight one then logs *Background
transcript ready N ms after the reply started (speech-to-text M ms).* and
*Straight to Thinking: the transcript replaced the recording in the
conversation, went to the record of conversations and to remembering.* (never
the words). MCP `hearing_check` reads them as `lastTurn`; `straight_voice_check`
rehearses the path headless ([MCP](MCP.md)).

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
  it never interrupts or restarts a reply. Those 20 seconds follow how chatty
  Martlet is (Companion › Vision › *How often it comments*, also shown under
  *Watch along*): 45 seconds when Quiet, 12 when Chatty, and with [Martlet
  decides](SCREEN_COMMENTARY.md#martlet-decides-how-chatty-it-is) the level it
  picked, which a reply to what plays may switch. At most the newest 1,500
  characters go with one message.
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
  The `LivePcAudio` line's tooltip then adds *This PC plays your voice back too; Martlet
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
(*Also hearing this PC.*) or why it can't; its tooltip says how (without its
own voice, or only on the output you hear while it pauses for Martlet's voice)
and how many lines of your own voice played back it left out. The Companion card's status names the other output in use.
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
  that engine's tags in two groups: its non-word sounds (written inline where
  the sound happens) and its tones of voice (written at the start of a
  sentence; each spoken piece is synthesized on its own, so a tone reaches only
  that sentence), one tag per line with when to use it. It asks for them
  sparingly. Text-only replies and voices without tags never get it.
- **Segmenter.** The speech segmenter (which still silences lines with
  markdown, links, code or other bracketed text) lets the speaking engine's
  tags through, case-insensitively, in the engine's own spelling, even when a
  tag arrives split across stream deltas. Any other registered engine's tag is
  dropped without silencing its sentence, so OpenAI, Windows voices, F5 and
  XTTS never read "[laugh]" aloud.
- **Other spellings.** Models sometimes write a tag they were given in other
  brackets or as a stage direction. Those spellings count as the tag itself
  (`VoiceTags.Spellings`): its words in any of `[ ]`, `( )`, `{ }` and
  `< >`; a sound's or tone's cue the same way (`[laugh]` for Dia's
  `(laughs)`); words joined by spaces, underscores or hyphens alike; and, for
  sounds and character tags, the action a stage direction writes (`[nods]`,
  `*nods*`, `(sighs)`, `*clears throat*`). So `[nod]` for `{nod}` plays the
  nod and `*laughs*` is spoken as Chatterbox's `[laugh]` (or Dia's `(laughs)`),
  instead of the tag showing in the chat and silencing the rest of its line.
  A sound's or tone's other words (`VoiceTags.Synonyms`) count too, in any
  bracket and as `*...*` when they read as a stage direction (ending in -s or
  -ing): `[whisper]`, `(whispers)`, `*whispers softly*`, `{hushed}` and
  `(in a whisper)` are Chatterbox's `[whispering]`, `[sobbing]` its `[crying]`
  and `*giggles*` its `[chuckle]`. Without them a hallucinated `[whisper]` was
  unknown bracketed text and silenced its whole sentence.
  Tones of voice otherwise take only other brackets (and `*...*` for an -ing
  tone such as `*whispering*`), and emphasis such as
  `*so*` is left alone. When two tags share a spelling, the speaking voice's
  own tag wins, then the character's, then another engine's: `[happy]` is
  Chatterbox's tone while Chatterbox speaks and the character's `{happy}` emote
  otherwise.
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
- **Under the reply.** The talk window notes how a reply was acted out under
  its bubble (`ReplyTag.Note`): *Tone: happy. Sound: laugh. Emotes: nod,
  blush.*, each part only when the reply wrote one: the tones and sounds its
  voice performed and the character tags it wrote. The desktop log has
  *Reply acted: {nod} (written [nod]), [laugh].* (tag names and spellings only,
  never the words).

`voice_tags` in [Martlet MCP](MCP.md) shows all of these for any engine (with
`characterTags`, the character cues too, plus `acted` and `note`), and
`spoken_reply_check` with `characterTags` runs them through the production
runtime.

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
  thinks or speaks is lost. Each utterance is still its own action: a fresh
  authorization, capture epoch, Voice ID check and STT request. It goes on
  while Martlet speaks (a reply, a remark or a song) whenever it can tell
  Martlet's own voice from yours: [echo reduction](#echo-reduction) works (on
  by default; what is mostly the speakers' sound is let go like a cough) or
  barge-in is on. What you say then shows in the history at once and is
  answered once the reply finishes (with barge-in, real words stop it instead);
  the desktop log notes *Always listening heard you while Martlet spoke; ...*
  (never the words).
  Without either (echo reduction off, or it couldn't start or was lost: no
  speaker audio, the canceller failed), it holds off while Martlet speaks (plus
  300 ms for the room's echo) so it never hears itself and answers its own
  words; the talk window's listening button then reads *Not listening while
  Martlet speaks*. Each capture's own echo state decides (`EchoTimeline.Reducing`,
  `EchoReducer.Works`), and before one opens the last capture's report does.
  It also holds off while other setup work (a microphone test, Voice ID
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
