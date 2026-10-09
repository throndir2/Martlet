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

1. On Companion › **Thinking**, **Voice** and **Listening**, choose *A cloud
   provider* and a supported model ID, paste the provider's key next to it (it
   goes to its scoped Windows vault target), tick the consent box and press the
   page's *Use* button (changing a key invalidates the choice until you confirm
   it again). Do not put keys in model fields or files.
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
   Martlet's replies as they stream in. The talk window calls the character by
   the name of the persona Martlet uses (Companion › Personality): its title,
   header, message box and empty conversation, each reply's label (*Ivy ·
   10:39 PM*, *Ivy, about your whole screen*) and the notes in the history
   (*You touched Ivy (touch: ...)*, *Ivy saw your whole screen.*, *Ivy stayed
   quiet.*). Excerpts of conversations the Thinking model reads (past
   conversations, remembering and learning names) label the character's lines
   with that name too. Status lines and settings keep saying Martlet, the app.
   A refusal is shown as such and never
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
   Thinking model: the persona wrapper,
   reply length, always listening, tools, Thinking longer, who is talking,
   lorebook, memory and past conversations introductions, notes with messages, the screen and
   camera glance instructions, messages (including the one sent
   when a notification pops up or a taskbar button flashes) and chattiness
   lines (including *Martlet decides* and *Chattiness right now*), *Screen with your message* (sent with what you type or say while
   vision is on), *What you saw* (the `[seen: ...]` tag a look or a reply with a picture ends with), the background work notes and the Thinking longer task, the
   Remembering and Learning names requests and the prompt
   that joins them, and the smart home notes. Each
   one is editable; a saved edit replaces the built-in text wherever it is used
   (settings `prompts.overrides`, by prompt ID, absent while nothing is
   edited). Words in braces such as `{name}`, `{persona}` or
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
revision and the reply-length instruction (both as worded in Companion › Prompts). Persona and user input share the existing byte/token
reservation; an over-budget combination is rejected without truncation or a
provider call. Martlet has no response styles: the persona text alone sets how
Martlet talks. Response-style weights and style prompt edits that older
versions saved still load, are ignored and are not saved again; a Persona
prompt edit loses its old `{style}` line. Valid legacy v1/v2 profiles upload no implicit persona
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
  message needs them, then, for a spoken reply, the *Short first sentence*
  prompt, and the reply length last. A screen glance has its glance
  instructions there instead.
- **The conversation so far** follows, each earlier message exactly as it was
  sent, with its notes (a paired host gets the plain messages and the notes
  with its instructions, as before). What Martlet saw is part of it: every
  screen glance and camera look, passed or not, is an exchange whose line
  starts with `[Screen]` or `[Camera]` (where it looked and what it saw, from
  the look's own `[seen: ...]` tag; passed looks in a row keep only the last),
  and a message that came with a picture keeps such a line after its words.
  Pictures are never kept, and these lines are never your words: memory,
  learning names, the record of conversations and the smart home leave them
  out, as they do `[PC audio]` lines ([Screen
  commentary](SCREEN_COMMENTARY.md#what-martlet-saw-stays-in-the-conversation)).
- **The message** comes last and ends with Martlet's **notes** between
  `[MARTLET_NOTES]` labels, only when something is new: lorebook entries and
  remembered facts not already in the notes of an earlier message the request
  carries, what was said in earlier conversations when the message refers to
  one ([Memory › Conversation history](MEMORY.md#conversation-history)), who is
  talking when that changed, a smart home result, and the
  picked style when the persona has several and it changed. The first notes
  start with what notes are (Companion › Prompts › *Notes with messages*).
  Notes are never shown and never what the user said.
- **The context board's notes** close the message, in their own
  `[MARTLET_NOTES]` block after the other notes: the newest short note of each
  background source that is still fresh ([Context board](#context-board)).
  They are sent with this message only and never kept in the conversation,
  so the message the next request carries again is the start of what was sent.
  What Martlet says on its own also gets what it said lately, last in that
  block ([What you said lately](#what-you-said-lately)).
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

## Context board

The context board is where background sources keep their newest short note
for the live conversation: what the character shows now, where its eyes are
while a reply's own choice holds them, a digest of the last seconds of the
screen, a line about the sounds this PC plays, touches on the
character. A reply or a look never waits for a source. As it builds its
request, it takes a snapshot of the board, and the fresh notes go last in the
message (see [the request layout](#prompt-caching-and-the-request-layout)).
The board's notes are never kept in the conversation, so the start of every
request stays the same and prompt caches keep working.

The talk window's `LiveTurnInputs` line counts them (*Last reply took your
words and 2 context notes.*), and the desktop log says *Context board: the
request took 2 notes (screen, touch; 96 bytes; 1 consumed).* MCP's
`context_board` tool rehearses the board with the production request layout
([MCP](MCP.md)).

### Context board API (for new sources)

The board is `Martlet.Conversation.ContextBoard`
(`src\Martlet.Conversation\ContextBoard.cs`). It is thread-safe, so a source
can post from any thread.

1. Get the instance. In the desktop, use `MainWindow.contextBoard`
   (`MainWindow.CharacterActions.cs`). The live conversation has the same
   instance as `LiveConversationController.Board`. Pass it to a service
   through its constructor.
2. Post your newest note with
   `Post(source, text, at, maxAge, consume: false, kept: null)`. A new post
   replaces the source's note before it. Empty text clears the note, and so
   does `Clear(source)`.
3. Use a known source name when one fits: `ContextBoard.Character`
   (`character`, posted by the conversation itself), `ContextBoard.Gaze`
   (`gaze`, also the conversation's own), `ContextBoard.Screen` (`screen`),
   `ContextBoard.Sound` (`sound`), `ContextBoard.Activity` (`activity`: what you
   seem to be doing on this PC, see [Where it comes
   from](#where-it-comes-from-and-what-you-are-doing)) or `ContextBoard.Touch`
   (`touch`). Another name is 1 to 32 lower-case letters, digits or `-`.
4. Write the text as one short sentence to the model, such as *Screen over
   the last 20 s: a code editor, then a browser.* The board makes it one line
   and cuts it to 600 UTF-8 bytes.
5. Choose `maxAge` (at most one hour). A note older than that at the time of
   a request is skipped and removed from the board.

Rules of the board:

- **Order.** A request takes the notes in a stable order: `character`,
  `gaze`, `screen`, `sound`, `activity`, `touch`, then other sources by name. The notes of one
  request are at most 2,048 UTF-8 bytes together; a note later in the order
  that does not fit is left out. The board keeps at most 16 sources.
- **Consume on read.** A note posted with `consume: true` goes with exactly
  one request: the board removes it when a request that carried it is sent,
  unless the source posted a newer note since.
- **Delivery.** The `Sent` event is raised once for each request that was
  actually sent (after the request started), with the snapshot it carried.
  Find your note in `snapshot.Notes` by its `Version` (`Post` returns the
  note): if it is there, it was delivered. A turn stopped before its request
  was sent raises nothing, and its notes stay on the board for the next one.
  `LastSent` is the newest delivered snapshot.
- **Kept line.** `kept` is an optional short line, such as *(touch: a pat on
  the head)*. When a request that carried the note is sent and its reply
  completes, the conversation keeps this line as the last line of the user's
  message (or of the look's line). It goes at the end, never at the start, so
  the prompt cache still holds the message before it.

## One moment: everything in one reply

Martlet hears and sees four things as one conversation: what you say (typed or
heard), what this PC plays ([Hear what this PC plays](#hearing-what-this-pc-plays)),
what vision watches ([Watch my screen](SCREEN_COMMENTARY.md)) and background
work that finished ([Thinking longer](#thinking-longer-and-background-work),
songs). Martlet answers one reply at a time, so whatever starts a reply takes
everything else that waits into that one request (`MomentTurn`), and the model
answers it all in one breath: *"Nice killing that monster! I can sing that song
you asked for as a celebration, and I've also finished that report. Wanna
see?"*

- **You come first and never wait.** Typed text, then what always listening
  heard, then what the PC played on its own pace, then finished work, then a
  look, as before. A reply to you takes only what is already there (the lines
  the PC played meanwhile, the newest picture, all finished work, the look
  vision was about to take); it never waits for a look or a job, so the time to
  Martlet's first word doesn't grow. A recording that goes [straight to
  Thinking](#straight-to-thinking) stays that way unless the PC played
  something meanwhile (then it goes as words, as before).
- **What the PC played on its own** takes the finished work Martlet may bring
  up on its own (Thinking longer shares results as soon as Martlet is free, or a
  reminder is due; nothing you stopped with Esc, no song playing), in its notes, and then gets
  the tools a report gets, so a later tool can act on your yes. It takes the
  newest picture and a look that is due.
- **Finished work** that comes up while the PC played becomes a reply to those
  lines with the results in its notes; otherwise Martlet's report, now with the
  newest picture (and a due look).
- **A look** that comes due while the PC's lines or finished work wait is one
  reply that takes them all (counted as a look: the pacer's spacing and hourly
  budget, and a look at a notification isn't repeated); only a look with
  nothing else waiting is a plain glance with the glance prompts.
- **Pacing stays.** What the PC played on its own still goes at most every
  PcPace, looks keep the pacer's budget, results keep the 2 seconds of quiet
  when they start a reply themselves, and nothing starts while Martlet is
  paused, locked or (for looks and results) singing.
- **Never your words.** Lines the PC played stay marked `[PC audio]`, and
  neither they nor a picture are ever your words: memory, learning names and
  Home Assistant read only what you said yourself.

Every reply and glance carries the same Companion › Prompts › *One moment*
instruction, first among Martlet's own, so the start of every request stays
the same and prompt caches are reused: one message can bring several of these
at once; answer them together in one short, natural reply in character, your
own words first, and `[pass]` when it holds none of your words and nothing
worth a word. A reply that took a look at something that wants your attention
gets *Something wants your attention, with a reply* in its notes. The talk
window's `LiveTurnInputs` line says what the newest reply took (*Last reply took
2 lines this PC played, the picture and 1 finished job, counted as a look.*),
the desktop log has a *Turn took: ...* line per reply and look, and
`think_longer_check`'s `moment` part in [Martlet MCP](MCP.md) rehearses the
plan and a combined request.

## What you said lately

A small model often says the same thing again and again: the same remark on a
game, the same joke, the same opener. So Martlet keeps what it said lately,
each with when it said it (`Martlet.Conversation.SaidLately`). What Martlet
says on its own checks that list before it speaks.

- **What is kept.** Each reply, remark and reaction that the conversation
  keeps, with the local time: replies to you, screen and camera remarks,
  remarks on what this PC plays, reactions to touches and reports. A `[pass]`
  is not kept. Each one is one line of at most 160 characters. Martlet keeps
  the newest 10 and uses those from the last hour. The list is in memory only:
  it is never saved or logged, and Martlet forgets it with the conversation
  (Refresh context, pause, lock, closing the talk window).
- **Which requests carry it.** Only what Martlet says on its own while nobody
  waits for its first words (`SaidLately.Carries`): a screen or camera look,
  a reply to what this PC played that holds none of your words, and a report
  (finished work, a due reminder, a check-in's `SAY:`). A reply to your words
  (typed, spoken, straight to Thinking or from a messaging chat) or to a
  touch never carries it. Its request stays exactly as it was, so the time to
  its first words does not change.
- **Where it goes.** Companion › Prompts › *What you said lately* gets the
  list, oldest first, each line with the time of day and how long ago
  (`- 10:05 PM (12 min ago): "Ooh, that boss is almost down!"`), the time now
  and the `[pass]` word. It goes last in the context board's notes block, at
  the end of the message. That block is sent with this request only and is
  never kept, so the next request starts like this one and prompt caches keep
  working. An emptied prompt sends nothing.
- **What it asks.** The model checks what it is about to say against the
  list. It does not say a thing again, not even in other words, unless
  something changed or enough time has passed; then it says it differently.
  When nothing new is worth saying and nothing asks it to speak, it answers
  `[pass]`.
- **What you see.** The talk window's `LiveTurnInputs` line and the desktop
  log's *Turn took* line count it (*Last look took the picture and 3 things
  Martlet said lately.*), never what was said.

It replaces the glances' *Earlier remarks* prompt (the last four screen
remarks, without times). Saved edits of that prompt are dropped, and a glance
prompt edited with `{remarks}` gets nothing there.

The *Saying the same things* check-in reads the same list
([Check-ins](#check-ins)). When Martlet keeps repeating itself, a reminder
goes in the notes of its next reply, a reply to you included.

Checked locally: `SaidLatelyTests`, `CheckInsTests` and `PromptSettingsTests`,
the real controller with a fixture Thinking endpoint (`SaidLatelyDesktopTests`:
a look, a reply to what the PC played and a due reminder carry it with when;
replies to you and to a touch don't; Refresh context and an emptied prompt
send nothing), and MCP `said_lately_check`. A real model reading it is **NOT
RUN**.

## The Thinking pool

The **Thinking pool** (Companion › Thinking pool; this PC's own choice,
`thinking-pool.json` in the data folder, never shared) is one shared set of
Thinking models for background work. It replaces the older *Deep thinking*
places. The live conversation keeps its own Thinking route and prompt cache;
pool jobs never use that route, except the empty-pool fallback below.

**Members.** A member is one place with a slot count (how many jobs it runs at
once, 1 to 8):

- A paired Martlet host with a Thinking model joins by itself
  (`ThinkingPoolAutoJoin`, Martlet.Core). Each time a check sees it answer (the
  cluster sync every 15 s while *Keep in sync* is on, *Check computers*, any
  other check of all hosts and the host update check), a host that offers its
  Thinking pool role (a second Ollama server of its own, route
  `martlet.gateway.deep-thinking-chat.v1`) joins on that role with its slots
  (the role's `OLLAMA_NUM_PARALLEL`, advertised as `maximum_concurrency`, at
  most 8). A host that offers only its Ollama joins on it when it doesn't do
  this PC's conversation Thinking. A member on a host's Ollama moves to the
  host's Thinking pool role once it has one, and a member on the role follows
  the role's slot count. The host that runs the conversation may join with its
  role too. Martlet writes `thinking-pool.json` only when something changes,
  never over a file it can't read, and says it once in the status line and
  the desktop log (*diva joined the Thinking pool by itself (its Thinking pool
  role, qwen3:8b, 2 slots). Untick it in Companion › Thinking pool to keep it
  out.*). A member whose computer stops answering stays a member; its line
  says *In the pool, offline now* and its slots come back when it answers
  (see *Computers that go offline* below).
  A computer never joins by itself when the owner took it out (`LeftByOwner`,
  below), Devices › Sharing work says the Thinking pool never uses it or keeps
  it for other companion PCs, the pool already has 8 members, or this PC is a
  host PC.
- *In the pool* (`DeepThinkingPool-<host>`, on each paired computer's row) is
  the owner's opt-out. Unticking it, or *Remove* on the computer's member, takes the computer out and adds its host ID to
  `LeftByOwner` in `thinking-pool.json` (a file saved before this list existed
  reads as empty), so it doesn't join again. Ticking it clears that and adds
  the computer at once. `DeepThinkingAutoJoin` (*Add a machine*) states the rule
  and names the computers kept out.
- A model in Ollama on this PC, beside Thinking's (checked to fit on the
  graphics card before each job).
- An OpenAI-compatible endpoint (a cloud provider or another server).

The *Machines* card is one list. The first row is the conversation's own model
(`ThinkingPoolConversation`): it is never in the pool, so no pool job waits in
front of a reply. Then each member (`ThinkingPoolMember-<n>`) shows what it
reads and writes (text; pictures when its model sees; recordings only on an
endpoint whose model hears, never a paired host, whose gateway takes no audio)
and its slots. Badges (`ThinkingPoolBadges-<n>`) say *Waits while you talk*
(it shares the conversation's computer, see the live floor), *Costs money* (a
cloud provider) and *Offline*. Each member has three boxes:

- *Quick jobs* (`ThinkingPoolQuick-<n>`): the fast kinds, the judges
  (`BargeInJudge`, `EndOfTurnJudge`) and the screen and sound summaries
  (`Digest`).
- *Long jobs* (`ThinkingPoolLong-<n>`): every other kind, such as
  `think_longer`, research, a song's lyrics and the other helpers.
- *Backup for slow replies* (`ThinkingPoolAnswers-<n>`, see
  [Backup Thinking](#backup-thinking-a-hedged-request)).

Quick jobs and Long jobs are on for every member. Unticking one saves the key
in `NoQuickJobs` or `NoLongJobs` in `thinking-pool.json` (a file without these
lists reads as every member taking every job). Removing a member clears its
keys. A paired computer's Thinking pool role sets its slots on that computer, so
its row shows *N slots, set on diva* and *Change model*
(`DeepThinkingChangeModel-<host>`), never a slot choice: Martlet resets those
slots from the role at each check. Other members have a slot choice
(`ThinkingPoolSlots-<n>`). A paired computer's row has *In the pool*; another
member's row has *Remove* (`ThinkingPoolRemove-<n>`). Each paired
computer that isn't a member follows the members, with why it isn't in
(`DeepThinkingHost-<host>`). Priorities are fixed (see the job board), so the
page has no priority choices.

**Migration.** The first time Martlet reads the pool and `thinking-pool.json`
is missing, it reads `deep-thinking.json` once and writes every separate place
as a member (slots and keys kept). *Same as Thinking* is not a member: it
becomes the empty-pool fallback.

**Empty pool.** *Do thinking longer and research here when no machine in the
pool takes long jobs* (`ThinkingPoolUseConversationModel`, on the conversation
row, on by default): with no member that takes long jobs, `think_longer` and
research run on the conversation's own Thinking route, as
*Same as Thinking* did (only where its provider answers several requests at
once). Other job kinds get `NoMember` at once, so their callers use their own
fallback (simple rules, a CPU path, or nothing).

**Warnings, never blocks.** Pool jobs may run on every graphics card. The card
shows guidance (`ThinkingPoolGuidance`, such as "1 slot: long thinking can delay
screen and sound summaries; add a second slot for the full experience.") and
warnings (`ThinkingPoolWarnings`) for a member on the same computer, graphics
card or Ollama server as the conversation's Thinking model, or on the same
computer as the voice.

### Computers that go offline

The pool follows which of your computers answer, with no setting to change.
`HostPresence` (Martlet.Desktop) records which paired computers answer now. Three
things feed it:

- The device sync check of every paired host (every 15 s while *Keep in sync*
  is on). One failed check marks the computer offline, and the next check that
  reaches it marks it online again.
- *Check all hosts* (Devices), and the other checks of all hosts.
- A pool job, think or research step that can't reach its member's computer (a
  network failure, never a model's). This marks the computer offline at once,
  so the next job goes to another member.

While a member's computer is offline, Martlet asks it again every 15 s, also
when *Keep in sync* is off. A computer that was never checked counts as online.

When a member's computer goes offline:

1. Its slots leave the pool. The broker (`BackgroundPlaces.Reachable`) puts no
   new work on it, and its slots don't count as free for the last-free-slot
   rule. `CanRun`, `Find` and the status count only the members that answer.
2. Work already running there ends through the normal path. A pool job tries
   the next member. A think or a research step that failed because its computer
   stopped answering keeps what it wrote and goes on on another member of its
   pool (*Paused. Its computer stopped answering, so it goes on on another*),
   at most 3 times. This is not counted as a stop for the conversation.
3. When every member that would run is offline, `think_longer` and research use
   the conversation model, as with an empty pool, when the conversation row's
   box is ticked and that model can think in parallel.
   Otherwise they say *Every Thinking pool computer is offline (diva and
   ripley); their slots come back when they answer again.* Other job kinds get
   `NoMember` (*every Thinking pool member that can do text is offline*) and use
   their own fallback.

When the computer answers again, its slots come back at once, and a job waiting
in line starts there (`BackgroundPlaces.Reconsider`). The desktop log says each
change in plain words, for example *Thinking pool: diva went offline; the pool
has 3 slots on 2 members now (5 when all answer).* and *Thinking pool: diva
answers again; 5 slots.* The desktop also writes `thinking-pool-status.json`
again (`online`, `offlineSince`, `slots`, `configuredSlots`).

**The tools stay the same.** Whether `think_longer` and research are offered,
and the *Up to N at once* text, come from the configured members alone, with
every computer counted as online (`LiveConversationController.ReplyThinkTools`).
So the start of every Thinking request stays the same for prompt caches while
computers come and go. Only placement, `CanRun`, `Find`, the status files and
the log use the presence-aware plan (`ThinkingPoolSettings.Plan` with the
offline host IDs, which gives an offline member a `DeepThinkingPlan` with
`Offline` set).

### Job board

One in-process board (`ThinkingJobBoard`, Martlet.Conversation) over the same
`BackgroundPlaces` broker as the conversation's background jobs, so pool jobs,
thinks and research count against the same slots:

1. A job goes to a free slot on a member whose capabilities include the job's
   needs (text, vision, audio) and that takes the job's kind (*Quick jobs* or
   *Long jobs*), the member that shares least with the conversation first.
2. A member that fails, is busy (`job.busy`) or is unavailable is passed over
   for the next capable member. Each member is tried once. A member whose
   computer is offline gets no job
   ([Computers that go offline](#computers-that-go-offline)).
3. A member whose computer refuses the request itself as invalid (a paired
   computer's gateway answers `request.invalid`, for example when the two
   computers run Martlet versions that don't agree) rests for 10 minutes
   (`ThinkingJobBoard.RefusedRest`). In that time the board gives it no job that
   needs at least what the refused job needed, and `CanRun` and `Find` pass it
   over, so callers use their fallback without a request. The desktop log says
   once which computer to update, and `thinking-pool-status.json` lists it
   under `resting`.
4. When every capable slot is busy, the job waits in line. A freed slot goes
   to the highest priority first, then the oldest.
5. Long kinds never take the last free slot that takes quick jobs while the
   pool has two or more slots: that slot stays for fast kinds (`BargeInJudge`,
   `EndOfTurnJudge`, `Digest`). Only the slots of members that answer and take
   quick jobs count. A long job on a member without *Quick jobs* never takes
   such a slot, so it needs none kept free. With exactly one slot, long kinds
   may take it, and fast jobs wait until their deadline. Only the
   [live floor](#the-live-floor-the-live-turn-comes-first) stops running jobs.

| Kind (`ThinkingJobKind`) | Priority (`ThinkingPriority`) | Fast |
| --- | --- | --- |
| `BargeInJudge` | 70 | yes |
| `EndOfTurnJudge` | 60 | yes |
| `Digest` (screen or sound) | 50 | yes |
| `ThinkLonger` | 40 | no |
| `TouchZones` | 35 | no |
| `Memory`, `Naming`, `CheckIn` | 20 (`Helper`) | no |
| `Research` | 10 | no |

Helper jobs use these kinds through `HelperJobs` and `ThinkingPoolHelpers`.
Remembering after a reply is a `Memory` job. Emote naming and touch temperament
are `Naming` jobs, and touch-zone detection is a `TouchZones` job that needs
vision. When the pool can't do one, the conversation's Thinking model does it
after the reply finishes speaking
([helper jobs](MEMORY.md#helper-jobs-on-the-thinking-pool)). A
[check-in](#check-ins) is a `CheckIn` job; it has no fallback, so it waits
until a member can take it.

### Pool API (desktop)

```csharp
// Martlet.Desktop: the controller owns the pool.
ThinkingPool pool = conversation.ThinkingPool;   // LiveConversationController.ThinkingPool

// Cheap, synchronous, takes no slot: choose a fallback without posting.
if (!pool.CanRun(ThinkingJobKind.Digest, ThinkingCapability.Text | ThinkingCapability.Vision)) { /* local rules */ }

ThinkingJobResult result = await pool.RunAsync(new ThinkingJob
{
    Kind = ThinkingJobKind.Digest,            // sets the priority and the fast-slot rule
    Instructions = "Summarize the screen in one sentence.",
    Text = "What is on the screen?",
    Image = boundedImage,                     // optional BoundedImage: needs Vision
    Audio = boundedWaveAudio,                 // optional BoundedWaveAudio: needs Audio
    Timeout = TimeSpan.FromSeconds(8),        // the run; with DropWhenStale also the wait
    DropWhenStale = true,                     // drop (Stale) when no member frees up in time
    MaxOutputTokens = 256,
    Reasoning = false                         // null: the model's default
}, token);

// result.Outcome: Succeeded, NoMember, Stale, Failed, TimedOut or Preempted (a summary the live floor stopped: dropped).
// result.Text, result.Member (computer name), result.MemberId, result.Model, result.Problem, result.Attempts, result.Preemptions.
BackgroundPlace? first = pool.Find(ThinkingJobKind.Digest, ThinkingCapability.Text | ThinkingCapability.Audio); // Name, Model; null: no member
ThinkingPoolStatus status = pool.Status();     // members, slots, free, KeepsFastSlot, Running/Waiting by kind, Guidance
```

`Priority` and `Needs` override the kind's priority and the needs taken from
the image and audio. Canceling the token throws `OperationCanceledException`.
The job's text is private: it never goes to logs or status files. Background
jobs on the job list use the same rules through `BackgroundJobKind.PoolKind`
(`think_longer` is `ThinkLonger`, research is `Research`, a song's lyrics take
a place as `ThinkLonger`).

**Status.** The desktop writes `thinking-pool-status.json` (members with
whether each one's computer answers now, the slots of the members that answer
and of every member, running and waiting jobs by kind, resting members,
guidance and warnings; never a job's text).
MCP's `thinking_pool_status` reads it with the settings and plan, and
`thinking_pool_check` rehearses the board ([MCP](MCP.md)).

### Backup Thinking: a hedged request

**Backup Thinking** (Companion › Thinking pool › *Backup Thinking*,
`ThinkingPoolBackup`, off by default) helps when the conversation's Thinking
model is slow to start a reply because it is busy, loading or far away. It is
a *hedged request* (Dean and Barroso, "The Tail at Scale", Communications of
the ACM 56(2), 2013). When the reply's Thinking request has no first words
after a wait, the same request also goes to a pool member, and the stream with
words first gives the reply. The other stream stops at once.

- **Who may answer.** Each member has *Backup for slow replies*
  (`ThinkingPoolAnswers-<n>`, off by default; `AnswersForConversation` in
  `thinking-pool.json`). Choose members with the same model as the
  conversation, or a similar one. A paid cloud member is asked only when it is
  ticked, and never for a reply started early that isn't taken yet.
- **The wait** (`ThinkingPoolBackupDelay`, `BackupDelayMs`): automatic by
  default, the 95th percentile of the first words of the last 20 replies
  (counted from each Thinking request's start), never under 900 ms, and 1.5 s
  until 3 replies are known (`FirstWordTimes`); or a fixed 0.5 s to 3 s. The
  automatic wait starts again when the Thinking model changes.
- **Which member** (`ThinkingBackupMembers.Choose`, when the wait ends): the
  first ticked member, the one sharing least with the conversation first, that
  can run now, shares no hardware with the live conversation
  (`LiveResources.Shares`) and can take the request as it is. Tools go only to
  an endpoint (a paired computer's gateway takes none), a picture only to a
  member that sees, a recording only to one that hears, and the request must
  fit the member's limits.
- **Only the reply's first request.** Never the Thinking fallback, a tool
  round or a retry. When the member fails too, the reply goes on as without
  it, so a failure leads to the usual retries and the fallback. When the
  conversation's model fails after the member was asked, the member can still
  answer.
- **Live work.** The member's request is live work. A paired computer gets it
  as `WorkPriority.Live` and keeps its graphics card for it (`ILiveGpuHold`).
  While its stream is read, its computer is one of the live floor's resources
  (job `backup thinking`), so pool work there stops and waits. A reply started
  early that is let go stops its member's stream too.
- **Prompt caches.** The conversation's own request doesn't change, so its
  prompt cache is kept. The member's first request may read nothing from its
  own cache.

**Observability.** The reply latency line says `Backup Thinking won at 1104 ms
(diva (qwen3-8b), asked at 912 ms).`, `Backup Thinking asked at 912 ms (diva
(qwen3-8b)); the conversation's model won.`, `... it gave no answer.` or
`Backup Thinking: no member could take it.`; a fast reply says nothing. The
desktop log has one line each time a member was asked, and
`thinking-pool-status.json` has a `backup` part: the wait used, the automatic
wait and its replies, and the last results and counts (never what was said).
MCP's `thinking_pool_status` shows the choices and the member it would ask now,
`backup_thinking_check` rehearses the race with two fixture endpoints, and
`latency_report` counts the results ([MCP](MCP.md)).

```csharp
// Martlet.Conversation: Backup Thinking for one reply (the desktop's LiveConversationController.Backup.cs implements it).
public interface IThinkingBackup
{
    TimeSpan Delay { get; }                        // counted from the reply's Thinking request's start
    Task<ThinkingBackupStream?> OpenAsync(ConversationRequest reply, BoundedTextInput input, CorrelationIds ids, long epoch,
        bool held, CancellationToken token);       // null: no member now; held: started early and not taken yet
    void Ended(ThinkingBackupResult result);       // once: NotNeeded, NoMember, Won, Lost or Failed
}
var request = new ConversationRequest(input, model, textLimits, limits /* ... */) { Backup = backup };
// The member's own runtime opens the same request under the member's one-use authorization; nothing is sent until it is read.
ITextGenerationStream stream = await memberRuntime.OpenTextAsync(memberRequest, memberAuthorization, ids, epoch, token);
ThinkingBackupResult? what = turn.Snapshot.Backup;   // Outcome, Delay, Member, AskedAfter, FirstWordsAfter, Why
```

### The live floor: the live turn comes first

When you say real words to Martlet, or address it by name, the reply's time
to its first audio comes before all background work. Windows gives no
graphics card priority between processes, so Martlet does it with its own
scheduling: it holds new background work, stops running work, and lets that
work go on later. The **live floor** (`LiveFloor`, Martlet.Conversation) has
three levels:

| Level | Starts when | Ends when |
| --- | --- | --- |
| Idle | Nothing below holds the floor. | |
| Listening | The microphone hears your voice (a frame the speakers don't explain: never Martlet's own voice and never what this PC plays). | 6 seconds without your voice, or at once when the speech was only a sound or filler (the word check dropped it). |
| Live | A quick transcript has real words (`LiveFloor.RealWords`: the word check keeps them and they are more than backchannel words such as "yeah" or "mm-hmm"), or you say Martlet's name; the talk button; a reply to you starts (said, typed, a touch, a paired chat, people in your Discord call). | The reply's voice is all made (`ConversationTurn.Synthesized`) or the reply stops, then a 2-second grace for a fast answer; 8 seconds after words that no reply followed. Real words said over Martlet make it Live again. |

In a participation mode that answers only when addressed, words make the
floor Live early only when they name Martlet; otherwise the reply makes it
Live when it starts. When the participation policy turns down what you said,
your words no longer hold the floor (`LiveFloor.Dismiss`).

**Replies started early.** A reply that starts early (Companion › Listening ›
*Start replies early*, see [Hands-free voice activity](#hands-free-voice-activity-and-voice-id)) holds the
floor from its start, before your turn ends
(`BeginReply("a reply started early")`). The quick transcript it starts on
made the floor Live a moment before, so it never calls `Words` again. Its
request goes to a paired host as live work (`WorkPriority.Live`), as any
reply's does. Taken as the reply, it keeps the same hold, which ends once its
voice is made (`ConversationTurn.Synthesized`), so it is never counted twice.
Let go, it ends its hold at once (*the reply started early was let go*); the
2-second grace, then your voice, hold the floor while you go on talking. The
reply latency line says *Started early ...* just before its *Live floor* part.

**What the conversation runs on.** `LiveResources` lists the live Thinking,
voice and listening routes, each on a computer (`this-pc`, a home computer
such as `lan:192.168.1.20`, or a cloud provider, which shares nothing with your
computers) and its graphics cards when the host says (route metadata `gpus`).
A pool member **shares** the conversation's hardware when it is on the same
computer and the graphics cards match; when either side doesn't know its
cards, the same computer is enough. The conversation model's own place (the
empty-pool fallback) always shares. While a
[Backup Thinking](#backup-thinking-a-hedged-request) member's stream is read,
its computer is one of the resources too (job `backup thinking`).

**What the floor does** (`LiveFloorRules`, the broker's `BackgroundPlaces.Rules`),
on members that share the conversation's hardware only; other members never
wait, and while the floor isn't Idle they get new work first:

| Kind | Listening | Live |
| --- | --- | --- |
| `BargeInJudge`, `EndOfTurnJudge` | Start, on a member that shares nothing first. | The same. |
| `Digest` (screen or sound summary) | Doesn't start. | Doesn't start; a running one stops and its result is dropped (`ThinkingJobOutcome.Preempted`). |
| `Memory`, `Naming` (touch temperament too), `CheckIn` | Doesn't start. | Doesn't start; a running one stops and waits in line again. |
| `TouchZones` | Doesn't start. | Doesn't start; a running one goes on. |
| `ThinkLonger`, `Research` | Doesn't start. | Doesn't start; a running one stops, keeps what it wrote, says *Paused: waiting for the conversation* and goes on later on any free member (one that shares nothing first). |

Listening never stops running work. A job that waits only because of the
floor shows as *waiting for the conversation* (the board's `Held` kinds, the
task list's job state). Research and thinks started while their only places
are kept for the conversation wait instead of being refused
(`BackgroundJobStart.ForConversation`).

**Going on from what it wrote.** A stopped think keeps the text its request
wrote (`BackgroundThink.Partial`). Where the next place's server continues an
unfinished assistant message (Ollama, on its own port 11434:
`ThinkLonger.ContinuesInPlace`), the next request ends with that text as the
assistant's own message (`BoundedTextInput.Continuation`) and Thinking steps
off, and the server writes on from there; the two parts are joined
(`ThinkResume.Join`). Elsewhere it starts again with the text as context
(`ThinkLonger.ResumeNote`) and writes the whole result.
`YieldingThink.RunAsync` runs this loop for `think_longer` and each research
step.

**The live route's fallbacks.** Remembering, learning names, emote naming and
touch temperament on the conversation's own Thinking model (when the pool
can't take them) start only while the floor is Idle and no reply runs, and stop
and run again later when the floor goes Live (`HelperJobs.Floor`). On a paired
host they are background requests in the work queue
([Sharing work](CLUSTER.md#sharing-work-between-your-computers)): a live reply
never waits behind one.

**Screen and sound summaries.** The screen summary that starts when you begin
to speak runs only on a vision member that shares nothing with the
conversation, and no contact sheet is made while no member may take a summary.
The sound digest skips its turn (`SoundDigestStep.Held`) while no member that
hears may start one. The contact sheet, the screenshot's JPEG for the Reading
role, Windows OCR and the CPU sound tagger run below normal thread priority
(`LowPriority.RunAsync`).

**Paired hosts.** When the floor goes Live, Martlet asks every paired host
that serves a live route to keep those graphics cards free of pool work
(`ILiveGpuHold.HoldAsync` with the route IDs and 10 seconds), renews it every
5 seconds while Live and lets it go at Idle. A host that refuses pool work for
a live turn (`job.busy` with detail `live`) or stops it (`job.preempted`) gives
`WorkRefusal.Preempted`: the job waits and goes on later, never a failure.
Until a host supports holds, the desktop uses `NoLiveGpuHold`.

**Observability.** The desktop log says each change ("Live floor: Live (the
transcript had real words)."), each stopped job and, at Idle, what the floor
did that turn. The reply latency line ends with what it held and stopped
(`Live floor: held 2 pool jobs, stopped 1 (think longer).`). The desktop
writes `live-floor.json` (level, live resources, which members share them,
held and stopped jobs by kind this turn and in all, holds, last changes; never
what was said). MCP's `live_floor_status` reads it with the settings, and
`live_floor_check` rehearses the floor with fixture inputs ([MCP](MCP.md)).

```csharp
// Martlet.Conversation: the floor and its observers.
LiveFloor floor = conversation.LiveFloor;          // LiveConversationController.LiveFloor (the desktop's)
LiveFloorLevel level = floor.Level;                // Idle, Listening or Live (lock-free)
floor.Changed += change => { /* change.From, change.To, change.Why, change.At; raised in order */ };
floor.Heard();                                     // the user's voice (call per frame; cheap)
floor.NotWords("a sound, not words");             // the speech was only a sound or filler: Listening ends
floor.Words("real words");                         // Live for 8 s, or until a reply takes over
LiveFloorReply reply = floor.BeginReply("a reply to what you typed started");
reply.End("the reply's voice was made");          // then a 2-second grace
floor.Dismiss();                                   // Martlet won't answer what was heard
floor.Clear();                                     // the conversation ended: Idle at once
bool words = LiveFloor.RealWords(text, utteranceContext, ListeningSensitivity.Normal);

LiveFloorRules rules = conversation.LiveFloorRules;   // the broker's rules; Resources, Period and Total counts
string? note = rules.Period.Describe();             // "held 2 pool jobs, stopped 1 (think longer)"
```

## Thinking longer and background work

Replies answer right away (Thinking steps are Off by default). **Thinking
longer** (Companion › Thinking pool; on by default, and *Let Martlet think
things over in the background* (`ThinkLongerOn`) turns it off on all your computers) lets Martlet decide, sparingly, that a
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
own bounds: 8,192 output tokens (Medium) or 16,384 (High) and up to 65,534 stream
events. It has no time limit: it runs until it is done or canceled (its request
gets the providers' ceiling of a day; on a paired computer, the gateway route's
longest request, 15 minutes). It is never
spoken. Its message continues a reply's request (Companion › Prompts ›
*Thinking longer: the task*); the picture or recording the message went with
isn't sent again.

**Where it thinks** (Companion › Thinking pool; this PC's own choice,
`thinking-pool.json` in the data folder, never shared, because which machine
is free to think depends on the computer you talk to). A think runs on a pool
member ticked for *Long jobs*:

- *The conversation's own model*: only when no member takes long jobs and *Do
  thinking longer and research here when no machine in the pool takes long
  jobs* is ticked (on by default). Its tools are described so the request
  starts like the reply's and shares its prompt cache, with the Thinking
  fallback. Only when Thinking's provider answers several requests at once (a
  cloud provider), never Thinking's model on this PC or a paired computer.
- *A paired computer*: a paired computer's own Deep thinking model (its
  Thinking pool role: a second Ollama server of its own, route
  `martlet.gateway.deep-thinking-chat.v1`) through its pinned gateway with this
  PC's pairing, or, on a computer without that role, its Ollama (its Thinking
  role). The page offers *Add the Thinking pool role* for a computer that lacks the role
  (`DeepThinkingAddRole-<host>`; its dialog asks which model it runs) and
  adds it to the pool once it runs, and *Change model*
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
  models are never pushed off the card. The sizes come from the planner's
  footprint catalog ([Resource footprints](RESOURCE_FOOTPRINTS.md)): a Gemma 4
  think's context is 0.3-0.9 GB, so a 24 GB card beside Thinking's Gemma 4 E4B
  fits four Gemma 4 12B thinks; models the catalog doesn't know count about three
  quarters of their size per think. The role advertises its slots as its
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
- *Ollama on this PC* (*Add a machine*): a second model of its own here, beside Thinking's (a
  larger one can think while a small, fast one answers you), never Thinking's
  own model. The page shows whether it fits beside Thinking's on the graphics
  card (`DeepThinkingLocalFit`).
- *A cloud provider or server* (*Add a machine*): OpenRouter, NVIDIA Build, OpenAI or any
  OpenAI-compatible server (HTTPS, or a server on this PC), with its own key in
  Windows Credential Manager, Thinking's key for the same base URL, or none. The
  conversation that fits the model's context and the task go there, no tools.

**Several computers at once.** Your paired computers with a Thinking model join
the pool by themselves (see Members above); each one in the *Machines* list
has *In the pool* (`DeepThinkingPool-<host>`): ticked, the pool thinks
there as well as on its other members, so several thinks run at once, one on
each place (up to 8 places). Untick a computer to keep it out; tick it again to
add it back. `think_longer` may then run several thinks at once: one fewer than the usable
slots in all of the members ticked for *Long jobs*. A long job never takes the
last free slot that takes quick jobs while
the pool has two or more slots, because that slot stays free for quick jobs
(judges and summaries); with one slot in all, one think runs at a time. The
tool's description says how many (*Up to 2 at once; more wait in line* for
three slots, *One at a time; more wait in line* for one or two). It comes from
the settings only, so the request start changes only when the slots change, never
when a computer goes offline or answers again. A member whose computer is offline
gets no new think until it answers again, and a think waiting in line starts on
it then ([Computers that go offline](#computers-that-go-offline)).
Each new think goes to a free place: the one that
shares least with the conversation first (its plan's `Rank`: 0 does none of the
conversation's jobs, 1 shares a computer with the voice or listening, or is a
cloud provider or this PC, 2 shares Thinking's computer or provider, 3 is a
second model beside Thinking's on this PC's graphics card), then the order they
were chosen. One place that can't think (a computer that does Thinking without
its Thinking pool role) doesn't stop the others. A song's lyrics are written on
the same places: a free one, else the least busy. When every place is busy (or
only the last free slot is left), a new think waits in line (*waiting for a
free computer*) and starts on the first place that frees up. The model is told
what holds each place (*Every computer that thinks is busy (think-1 on diva and
think-2 on ripley)...*). Each think has its own
runtime and authorization on its place, and its result reaches the speaking
computer exactly as one think's does (see Delivery). With several places, the
page's `DeepThinkingParallel` line says how many thinks run at once.

**Always in parallel** (`DeepThinkingPlan`, shown on the page as
`DeepThinkingParallel`). A think always runs alongside the conversation and is
never paused, so it needs a model that can answer while Thinking answers you.
Thinking's own model on this PC or a paired computer can't: Ollama or LM Studio
answer one request at a time per model (Ollama on this PC runs with
`OLLAMA_NUM_PARALLEL` 1 by default) and keep one conversation in their prompt
cache, and a paired computer's gateway serves one request per job. There, Deep
thinking isn't available: `think_longer` isn't offered, the page says why, and a
single PC whose Thinking model is local simply doesn't think in the background
until another place is chosen. A paired computer's Thinking pool role is a
separate Ollama server with its own route, so it thinks in parallel even on the
computer that does Thinking (they share its graphics card). On a shared card the
live turn goes first: while a reply, a voice or listening runs on that card, or
a companion PC holds it for a live turn, the host turns a new think there away
(`job.busy`, detail `live`) and stops a running one at once (`job.preempted`),
and the Thinking pool runs it on another place or later
([Live turn first](CLUSTER.md#live-turn-first-on-a-shared-graphics-card)). On a
host with two or more NVIDIA cards, give the role a card of its own (its *Graphics
card* choice) so its thinks never stop for a reply.

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
about, the computer it runs on (*on diva*), its status and time, its result
(*Show result*) and *Cancel* (`LiveTasks`, `LiveJobs`: *think-1 on diva running
for 0:12*, `LiveJobState-<id>`); the desktop log notes each start with where it
was placed (*placed on diva, 1 of 2 places busy*), fit check and end
(`Background thinking:`) and a *Thinking input (Background thinking)* line.
Stop (Esc) ends a reply, never a think; the task's Cancel, `cancel_thinking`,
closing the conversation or quitting Martlet do (there is no time limit). Each
place has slots for thinks (one on a computer of yours unless its Thinking pool
role says it runs more, four on a cloud provider and on the conversation model
while the pool is empty). While the places have two or more slots in all, the
last free slot stays free for quick jobs, so one think fewer than the slots
runs at once (three on the conversation model's four). When no slot is free
for it, a new think waits in line (*waiting for a free
computer*) and runs on the first place that frees up, first come, first served.
Up to twice the slots (at most 8) may run or wait at once; there is no limit
on how many start in an hour.

**Which computer thinks** is decided by a deterministic broker
(`BackgroundPlaces`), never by a model, so placing a think takes microseconds.
A new think goes to a free slot on the place that shares least with the
conversation (its rank: none of the conversation's jobs, the voice or
listening, Thinking, Thinking's graphics card on this PC). Among places of the
same rank, a computer kept free for other work (the one that sings, Companion ›
Singing, and the one that makes pictures) comes after the general
ones. Then the least busy place, then the order you chose them in. While a song
is being made, its computer is held whole, so no think is placed there until
the song is done (thinks already running there carry on); lyrics are written
first, on a place of their own from the same broker.
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
  the results then go with your next message. It is [one moment](#one-moment-everything-in-one-reply)
  like any reply: lines this PC played meanwhile make it a reply to them with
  the results in its notes, and it takes the newest picture (and a look that is
  due) along. Whatever Martlet answers first while results wait (what this PC
  played, a look that came due) takes them too, without the 2 seconds of quiet.
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
`MaxActive` how many of that kind may run or wait in line at once (other kinds
run alongside; when that many are running or waiting, a new one is refused,
and on a pool the model is told how many run or wait and how many run at once),
`MaxPerHour` how many may start in any hour (null: no hourly limit), `TimeLimit`
how long one may take (up to 30 minutes; null: no time limit, as for a think),
`Offer` marks a result to offer before using it (a song:
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
   To run on another computer, pass a pool: `jobs.Start(kind, label, runAsync,
   places)`, where each `BackgroundPlace(Id, Name, Rank)` is a computer or
   provider (`Name` is the computer's name only; lower `Rank` goes first). The
   job list picks the best free place atomically with the limits, holds it
   until the job finishes (whatever its kind: places are shared by every kind
   started on them) and refuses `busy` naming what holds each place when none
   is free (with `wait: true` it starts waiting in line instead:
   `BackgroundJobStart.Queued` says what holds the places, and it runs on the
   first that frees up); `runAsync` reads `job.Place`. Deep thinking's places are
   `ThinkLonger.Places(DeepThinkingPool.For(deepThinkingSettings, routes))`,
   and `pool.Find(job.Place.Id)` gives that place's settings (a paired
   computer's route or an endpoint) to build the request with, as
   `ThinkLongerAsync` does. A step that needs a place for a while asks
   `jobs.Places.TryAcquire(places, holder, share)` (with `share`, the least
   busy place when none is free) and disposes the `BackgroundPlaceLease`. Set
   the kind's `MaxActive` to the places' slots, doubled for the line (at most
   8). A place also has `Slots` (how many jobs it runs at once) and `Duties`
   (other work its computer is kept free for, so it goes last among its rank).
   A step waits for a place with `jobs.Places.AcquireAsync(pool, holder,
   token)`, and other work on a computer keeps background work off it with
   `jobs.Places.Hold(place, holder)`; dispose either to free it. A new kind of
   on-demand work on a computer (image generation) adds that computer to
   `BackgroundDuties` so thinks go there last.
3. Return something like `ThinkLonger.Started(job, toldUser)` to the model.
4. Background work runs in parallel with the conversation, never in turns with
   it: run it where it doesn't hold up a reply (a song made by a host role on a
   computer that isn't speaking, a provider of its own), and check first that
   it fits beside the conversation when it shares a machine with it (as a
   think on a second model in Ollama on this PC does with `OllamaSideBySide`),
   refusing with `BackgroundJobOutcome.Failed` when it doesn't.

The job list does the rest: limits, placement, cancellation, the time limit (`TimedOut`),
the header chip and task list (`LiveTasks`, `LiveJobs`; give a new kind its title and icon in
`LiveConversationWindow.KindTitle`/`KindGlyph`; each names its place), `background-jobs.json` (kinds, states, places and times only),
and delivery: `Take(onItsOwn)` hands finished jobs to the next reply, which
completes or returns them, and `BackgroundJobs.ReportMessage` /
`ReportNotes` word them (with `Offer` kinds marked to offer first). The model
acting on the user's yes is an ordinary later tool call in that conversation
(for a song, `play_song` with the song's ID; see [Singing in
conversation](#singing-in-conversation); for a research report,
`perform_creation` with its ID; see [Web research](#web-research)).

## Web research

*"Martlet, can you look up which toys cats like best?"* Martlet says in
character that it'll look into it, calls `research` in the same reply and keeps
talking; a few minutes later it brings up what it found (*"...oh, and I found out
about those cat toys. Wanna see the report?"*) and shows the report on a yes.

**Consent, off by default.** Searching sends the search words to a third party,
so it is a switch of its own on Companion › Thinking pool › *Web research*
(`WebResearchOn`, saved with the reply settings as `ThinkLonger.WebResearch`,
so all your computers share it, like Deep thinking's Off). Its disclosure
(`WebResearchDisclosure`) says what leaves the PC: the search words go to
DuckDuckGo, each page read sees the PC's internet address, and what the pages
say goes to where Deep thinking thinks with the recent conversation. Deep
thinking off (Thinking longer off) turns it off too; its status line
(`WebResearchStatus`) says when Martlet can't use it yet (no Thinking that does
function calling, nowhere for Deep thinking to think).

**The tool.** While it is on, Thinking longer is on, the Thinking route does
function calling and Deep thinking can think, every reply gets
`research(topic, what_to_find)` right after `think_longer` and
`cancel_thinking`, always worded the same, with the *Web research* prompt
(Companion › Prompts), so the start of every request stays the same; with it off,
requests are exactly as before. The prompt has Martlet use it only when the user
asks to look something up, search for it or research it, and tell them first.
The call returns at once (`WebResearch.Started`).

**The job** (`research`: one at a time beside a think or a song, 4 an hour, 12
minutes, `Offer`, *Researching*) runs off the reply path on the thread pool,
placed like a think on one of Deep thinking's places (`jobs.Start` with
`ThinkLonger.Places(pool)`: the free place sharing least with the conversation,
held for the whole job with that place's own runtime and authorization; when
every place is busy it is refused and the model is told what holds them):

1. Where Deep thinking uses a second model in Ollama on this PC, it first checks
   it fits beside Thinking's and stops if Thinking's gets pushed off the card, as
   a think does.
2. `WebResearchRun` searches for the topic and reads the top 3 results, then asks
   the model up to 4 times, each a fresh, bounded background request continuing
   the reply's request exactly like a think (`PrepareThink`, Thinking steps on,
   its own runtime and authorization; *Thinking input (Web research)* in the
   log), with the task from Companion › Prompts › *Web research: each step*: what
   to research, the latest results and the pages read so far as numbered
   excerpts (at most 13,000 bytes in all, so it fits a paired computer's 16 KiB).
   The model answers in text, so it also works where Deep thinking takes no
   tools: `SEARCH: <query>` (another search, then its 2 best unread results),
   `READ: <link>` (up to 3), or the report (`TITLE`, `SUMMARY`, `REPORT`). The
   last step always asks for the report.
3. Caps: 3 searches, 8 pages, 3 MB downloaded, 400 KB and 15 seconds a request,
   20,000 characters of text kept a page.
4. The report gets its sources listed by Martlet itself (the pages read, as
   numbered links) and is kept as a `report` creation (see
   [CREATIONS](CREATIONS.md)), which the Creations page shows and every paired
   Martlet computer gets.

The job's result is the title, the summary and how to show it
(`perform_creation` with the report's ID), and as an `Offer` kind it is brought
up to be offered first. On a yes, `perform_creation` writes the report as a
plain web page in `research-reports` in the data folder and opens it in the
browser. The talk window's task list shows it as *Research* with its step (*Searching the
web*, *Reading pages (2 of at most 8)*, *Thinking it over*, *Writing the
report*); the desktop log notes each start, step failure and end with counts only
(`Web research:` searches, pages, bytes, model steps), never the topic, a query,
a link or what was read.

**The web client** (`WebAccess`, Martlet's own; no key or account): DuckDuckGo's
HTML search page (results parsed from its `result__a` links with ads left out and
its redirect links unwrapped) and a page reader that keeps readable text
(scripts, styles, navigation and footers left out). It connects only to public
internet addresses, checked on every connection including each redirect (never
this PC, the local network, link-local or cloud metadata addresses), uses no
proxy or cookies and follows at most 4 redirects. DuckDuckGo has no official
results API, so it may limit or refuse automated searches; the search is behind
`IWebSearch`, where a self-hosted SearXNG or a keyed search API can be added.

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

## Check-ins

A small conversation model forgets what it left on. It writes `{blush}` and
never `{/blush}`, chooses `{look ahead}` and never looks back, says *"I'll
remind you in 10 minutes!"* and never calls `reminders`, or says the same
thing again and again. **Check-ins**
(Companion › Check-ins) fix this. Every few minutes a Thinking pool member
answers one short question about the companion, with only the facts that
matter for that question, and Martlet acts on the answer.

**Built-in check-ins.** All are on by default. The choices are this PC's own
(`check-ins.json` in the data folder, never shared), because each PC shows its
own character and runs its own conversation.

| Check-in | Every | Asks, with these facts | Martlet then |
| --- | --- | --- | --- |
| Lingering emotes (`emotes`) | 5 min | Do the emotes a reply turned on still fit? The emotes with their hints and how long each has shown, the end of the conversation, how long it has been quiet, the day and time. | Turns off each emote the answer names (`OFF {blush}`), as `{/blush}` does. `KEEP` changes nothing. |
| Where the character looks (`gaze`) | 5 min | Does the gaze a reply chose still fit? What the eyes do now and usually, how long ago the reply chose it, the end of the conversation. | `USUAL` takes the eyes back to their usual gaze, as `{look usual}` does. `KEEP` changes nothing. |
| Promises (`promises`) | 5 min | Did the character say it would do something it never started? The end of the conversation, the reminders set and this conversation's background work. | A `REMIND:` line goes in the notes of the next message. `OK` changes nothing. |
| Staying in character (`character`) | 15 min | Did the last replies drift (out of character, generic, repeating, long, talking about notes or tools)? The personality and the last replies. | A `REMIND:` line goes in the notes of the next message. `OK` changes nothing. |
| Saying the same things (`repeats`) | 10 min | Does the character keep saying the same things (the same remark, joke, question, opener or topic again and again, or something it said not long ago while nothing new happened)? [What it said lately](#what-you-said-lately), each with when (`10:05 PM (12 min ago)`), and the day and time. | A `REMIND:` line goes in the notes of the next message. `OK` changes nothing. |

**One flow for every check-in.** A built-in check-in is only data: a prompt,
the facts it gets to know, the conditions it waits for and what its answer
does. Your own check-ins have the same parts, and Martlet runs both the same
way (`CheckIns.Wait`, `Message`, `Read`). Each card on Companion › Check-ins
has the same editor:

- *What it asks*: the prompt of a built-in check-in, or your own words.
- *It gets to know*: the facts (table below).
- *It runs when*: the conditions (table below).
- *Its answer*: what Martlet does with the answer.
- *It takes*: a screenshot, a recording or a script's output, and the model
  it needs.
- *It may use these tools*: the [tool sets](#check-in-tool-sets) it may call.

Change a built-in check-in on its card, and only what you changed is saved
(`CheckInChoice`, null for each default). *Use built-in settings* puts its
prompt, facts, conditions, answer, inputs and tools back; On and Every stay. *Copy as your
own* adds an own check-in (off) with the same prompt and choices, so every
built-in check-in can be recreated, and changed, as your own.

| Built-in | It gets to know | It runs when |
| --- | --- | --- |
| Lingering emotes | Emotes and gaze, the conversation | The character shows, an emote a reply turned on shows, slower when nothing changes |
| Where the character looks | Emotes and gaze, the conversation | The character shows, a reply chose where the eyes look, slower when nothing changes |
| Promises | The conversation, reminders and background work | You talked lately, something new was said |
| Staying in character | Its personality, Martlet's last replies | A personality is active, Martlet replied twice, 4 new replies |
| Saying the same things | What Martlet said in the last hour | Martlet said 3 things lately, something new was said |

**When a check-in runs.** Every 15 seconds a companion PC looks at its
check-ins, and the first one that may run starts. Only one runs at a time. A
check-in waits:

- until its pace (1, 2, 5, 10, 15, 30, 60 or 120 minutes) has passed since it last
  ran. With *Slower when nothing changes*, after an answer that kept everything
  (`KEEP` or `OK`), with nothing new said since, it waits three times its
  pace, so the pool isn't asked the same question again and again;
- until each condition ticked under *It runs when* is met:

  | Condition | It waits |
  | --- | --- |
  | You talked lately (`Talked`) | until something was said in the conversation, and while it has been quiet for more than 30 minutes (not for *Check now*) |
  | Something new was said (`SomethingNew`) | after it runs, until something new was said |
  | 4 new replies (`NewReplies`) | after it runs, until the conversation had 4 new exchanges |
  | Martlet replied twice (`Replies`) | until Martlet gave at least 2 replies |
  | Martlet said 3 things lately (`Sayings`) | until Martlet said at least 3 things in the last hour |
  | A personality is active (`Persona`) | while no personality is active |
  | The character shows (`CharacterShows`) | while the character isn't on the desktop |
  | An emote a reply turned on shows (`EmoteShown`) | until such an emote has shown for 3 minutes (any age for *Check now*); it reads only those emotes |
  | A reply chose where the eyes look (`GazeChosen`) | until a reply chose a gaze at least 3 minutes ago (any age for *Check now*) |
  | Slower when nothing changes (`SlowWhenKept`) | three times its pace, as above |

- while its prompt is empty;
- for a check-in that records the microphone or what this PC plays,
  until Martlet hears it (*Martlet doesn't hear the microphone now*);
- while the conversation is busy (you talk, or something happened in the last
  10 seconds), so it never races a reply;
- while nobody used this PC for 10 minutes (keyboard, mouse or talking with
  Martlet), so the pool isn't asked again and again while nobody is there;
- while no Thinking pool member can take it (*no Thinking pool member can take
  it (it needs a model for text and pictures)*).

An answer that turns off emotes looks only at emotes a reply turned on (their
own tag or a combo's). It never turns off an emote you turned on with *Try*,
or one a touch holds: `HeldEmote.Why` records why each emote went on.

**Where it runs.** A check-in is a `ThinkingJobKind.CheckIn` job
([Job board](#job-board)): the helpers' priority (20), never the slot kept
free for fast kinds, stopped and queued again while the
[live floor](#the-live-floor-the-live-turn-comes-first) is Live on a member
that shares the conversation's hardware, and dropped when no member takes it
within 2 minutes. It never runs on the conversation's own Thinking route, not
even with an empty pool, so the conversation's prompt cache and its time to
first words stay the same. With no member, check-ins wait. A check-in asks for
Thinking steps off and at most 600 tokens.

**Reminders for the next reply.** A `REMIND:` answer goes on the
[context board](#context-board) as source `check-in-<id>` (such as
`check-in-promises`), filled into Companion › Prompts › *Check-in: reminder
for the next reply*: *A reminder from your own check-in, for you only: ...
Follow it in this reply where it fits, without mentioning it.* It is posted
with `consume`, so it goes with exactly one request, in the notes after your
words. It is never kept in the conversation, so the start of every request
stays the same. It waits at most 30 minutes for a message.

**What a check-in adds to what Martlet knows.** A `KNOW:` answer is
descriptive context, not an instruction: a short, vivid description of what is
happening, which the next reply may draw on. It goes on the context board as
source `check-in-<id>`, filled into Companion › Prompts › *Check-in: adds to
what Martlet knows*: *What is happening now, from your own check-in, for you
only: ... This is background you may draw on in this reply where it fits, not
a reminder to follow. Never repeat it word for word and don't mention it.* It
is posted with `consume`, so it goes with exactly one request, in the notes
after your words, and never in the conversation or its instructions. It
describes the moment, so it waits at most 3 minutes (`CheckIns.ContextAge`).
`OK` or `KNOW: nothing to add` changes nothing. No reply waits for it, so the
time to first words and the start of every request stay the same.

**Your own check-ins.** *Add a check-in* adds one (off, at most 8), and *Copy
as your own* adds a copy of any check-in. Each has a name, what it checks (your
words, up to 8,192 characters), how often it runs, what it gets to know, when
it runs and what happens with its answer. It always gets the day and time.
Point at a fact or a condition on the page to see what it does:

| Fact | What the check-in gets | Placeholder |
| --- | --- | --- |
| The conversation | The last 6 exchanges, oldest first, and how long it has been quiet since. | `{conversation}` |
| Its personality | The active personality: the character's name and its text. | `{persona}` |
| Martlet's last replies | Martlet's last 6 replies, numbered, oldest first, without what you said. | `{replies}` |
| What Martlet said in the last hour | Everything Martlet said in the last hour (the newest 10), each with when, such as *10:05 PM (12 min ago)*. | `{said}` |
| Emotes and gaze | The emotes a reply turned on that still show, how long each has shown, and where the eyes look. | `{emotes}`, `{example}`, `{looking}`, `{usual}`, `{since}` |
| Reminders and background work | The reminders set and the background work started or finished in this conversation. | `{work}` |
| What changed on screen | Martlet's newest words about what changed on the screen, while it watches. Not a screenshot. | `{screen}` |
| What the PC plays | Martlet's newest words about what this PC plays, while it hears it. Not a recording. | `{sound}` |
| Whether you're at the PC | Whether someone uses this PC now, or how long since someone last did. | `{presence}` |
| How you touched the character | What you did to the character on the desktop in the last 10 minutes (pokes, pats, holds, strokes with their path and direction, moves), oldest first, each with when; which touches were intimate, how the personality feels about them and the places you keep coming back to. | `{touches}` |

A placeholder puts that fact where you write it in the prompt; `{name}` and
`{time}` work too. The facts you tick that the prompt doesn't name go after it.

*How you touched the character* (`CheckInFacts.Touches`) uses the same words
as the touch reaction's own request (`TouchWording`, with each stroke's path
and the personality's feeling), so "you" is the character. The desktop reads
it from the conversation's touch ledger with `TouchLedger.History`: a bounded
log of the last 10 minutes (`OftenWindow`, at most 24 runs) that a reply's
`Drain` leaves. Reading it never takes or changes the touches the next reply
gets, and nothing is added to a reply's request. Like the other facts, it goes
only to the pool member with the check, never to the log, the status file or
MCP output.

What happens with its answer:

- *Reminds Martlet in its next reply*: a `REMIND:` line goes on the context
  board, as above.
- *Martlet brings it up*: a `SAY:` line becomes a notice job (`checkin-1`,
  `CheckIns.SayKind`) that comes up like a due reminder: on Martlet's own as
  soon as it is free (Companion › Prompts › *Check-in: brought up on its own*),
  or in the notes of your next message (*Check-in: brought up, with your
  message*). Where no conversation runs, Martlet starts one without the talk
  window, as for a reminder. A notice kind's own prompts are its
  `BackgroundJobKind.Wording`; due reminders keep theirs and come first.
- *Adds to what Martlet knows*: a `KNOW:` line, a short description of what is
  happening, goes on the context board for the next reply, as above.
- *Turns off the emotes it names*: `OFF` lines with the tags of lingering
  emotes, as Lingering emotes does (tick *Emotes and gaze*).
- *Takes the eyes back to their usual*: `USUAL`, as Where the character looks
  does.
- *Its tools act*: the check-in's [tool calls](#check-in-tool-sets) are the
  action. Its answer is only one short line that says what it did and why (or
  `OK`), for the status; Martlet reads nothing else from it.

Every check-in's prompt goes through Companion › Prompts › *Check-ins: each
check* (`{task}`, `{facts}`, `{time}`, `{answer}`), which adds the facts, the
time and the answer format. Ideas: *"If the user has been at it for
hours, suggest a short break"*, *"If it's late at night, remind Martlet to talk
more softly"*, *"If the user seems stressed, remind Martlet to be gentle"*.

**What a check-in takes.** Each run of a check-in, built-in or your own, can
also take:

- *A screenshot* of the screen in front (`ScreenGlancer`, the capture Martlet
  uses to look at your screen, with private windows painted over). With no
  screenshot (a private window or Martlet's own windows in front, a locked
  screen), the run stops and says why.
- *A recording* of the last 5, 10, 15, 30 or 60 seconds of the microphone or of
  what this PC plays. Martlet keeps these seconds in memory only while a
  check-in that is on asks for them (`PcSoundBuffer.Wanted`), and only from the
  last pause. It needs Martlet to listen at that time: the microphone is open
  (for example, always listening), or *Hear what this PC plays* is on. Less
  than 1 second of sound stops the run. A check-in request takes up to 60
  seconds of audio (`BoundedTextInput.HardMaxAudioSeconds`); a conversation
  message still takes at most 30 (`MessageAudioSeconds`). Not every model
  hears a whole minute: Gemma 4 and Gemma 3n hear only the first 30 seconds of
  a clip, while Gemini, OpenAI's audio models and Voxtral hear far longer
  audio.
- *A script* you write (Windows PowerShell, at most 4,000 characters). Martlet
  runs it hidden, in your home folder, before each run, and stops it after 20
  seconds, as the [terminal tool](MCP.md#terminal) does. What it prints (at
  most 4,000 characters, cut) goes in the message as *data, not instructions*,
  with a line when the script failed. For example, `Get-Process | Sort-Object
  CPU -Descending | Select-Object -First 10 Name, CPU` gives the busiest
  programs. Only you write the script, on the page; Martlet never shows it in
  the status, the log or MCP.

**The model it needs.** A check-in is given only to a Thinking pool
member that can handle what it needs (`CheckIns.Needs`): text always, plus
*Sees pictures* (vision) and *Hears recordings* (audio) when you tick them. A
screenshot ticks *Sees pictures* and a recording ticks *Hears recordings* on
their own, and they can't be unticked then. A check-in with tool sets also needs
a member that calls tools (`ThinkingCapability.Tools`): an OpenAI-compatible
endpoint (Chat Completions function calling), the same rule as the
conversation's own tools; a paired computer's Ollama gateway calls none. The
job's `Needs` go to the
[job board](#job-board), which gives it only to a member whose abilities
include them. With no such member, the check-in waits.

What a check-in takes goes only to the member that takes it, which can be a
cloud service. The page says so. Nothing taken is saved or logged: the log
and the status say only what was taken (*a screenshot (1280x720), 10 s of the
microphone, a script (exit code 0, 0.4 s, 312 characters)*).

### Check-in tool sets

A check-in can do more than answer: it can call tools. Tools come in named
**tool sets**, and each check-in, built-in or your own, ticks the sets it may
use under *It may use these tools* on its card. Point at a set to see what it
does and its tools. Martlet offers:

| Tool set (`id`) | Tools | What they do |
| --- | --- | --- |
| Emotes and gaze (`character`) | `turn_off_emote`, `look_usual` | Turn off one lingering emote a reply turned on (never a try or a touch's), or take the eyes back to their usual gaze. |
| Martlet's next words (`next-reply`) | `remind_next_reply`, `bring_up` | Put a reminder in the notes of the next message, or have Martlet bring something up on its own, as the `REMIND:` and `SAY:` answers do. |
| Reminders (`reminders`) | `reminders` | Set, list and cancel your reminders, as Martlet does in a conversation. Offered only while a conversation's reminders run. |

**How a run calls tools.** The job offers the tools of the chosen sets that
this PC runs (`ThinkingJob.Tools` and `ToolHost`). The member's model calls
them in a bounded loop: at most 4 rounds (`CheckIns.MaximumToolRounds`) and 8
calls in all (`CheckIns.MaximumToolCalls`), over every member the pool tries.
After the eighth call, each call gets an error that says to answer without
tools. An unknown tool and a tool that fails are errors the model reads; they
never stop the run. With the answer *Its tools act*, the calls are the action
and the run counts as acted on when at least one call worked. With any other
answer, the message says *You may call your tools first if they help*, and
Martlet still reads the answer as before. The calls run at once on this PC:
the tool calls of a run that the pool later drops or stops still happened.

**What you see.** The card's status line and `check-ins-status.json` say which
tools the last run called and what came of each (`CheckInRun.Tools`: the set,
the tool, the first line of its answer, at most 120 characters, and whether it
failed), for example *Tools it called: remind_next_reply: Reminded the next
reply.* The log says the same. A handler's first line never holds what was
said or reminded.

**Add a tool set** (`Martlet.Conversation.CheckInToolSets`):

1. Make a `CheckInToolSet(Id, Name, Does, Tools)`: a lowercase kebab-case ID
   that check-ins.json keeps, a name and plain words for the card, and its
   `TextToolDefinition`s. Tool names are unique across every set.
2. Add it to `CheckInToolSets.All` (after the static sets it lists, so they
   exist when the list is made).
3. Register its handler in `MainWindow.CheckInToolHandlers()`
   (`MainWindow.CheckInTools.cs`): a `CheckInToolHandler` gets the call and a
   `CheckInToolContext` (the check-in's ID and name, the active personality's
   ID and when the run began, the same for every call of one run). It runs on a
   pool thread, so it does UI work through the dispatcher, and returns a
   `ConversationToolResult` (an error, not an exception, for a declined call).
4. To have a built-in check-in use it, give its `CheckIns.BuiltIn` entry
   `ToolSets = [<id>]`, usually with `Outcome = CheckInOutcome.Tools`.

The card, the settings check (an unknown set or the same set twice is refused),
the status and MCP pick up the new set by themselves.

**Answers.** Martlet reads the last decisive line, so thinking written before
the answer doesn't count. Markdown, bullets, quotes and a reasoning model's
`<think>` block are skipped (`CheckIns.Read`). `OK`, `KEEP`, `REMIND:
nothing` and `KNOW: nothing to add` change nothing, and so does an answer Martlet can't read (the status
says so).

**Prompts.** Each built-in check-in's card on Companion › Check-ins has its
prompt (*What it asks*), the same way your own check-ins have their task. Edit
it there: it saves a moment after you stop typing, and the next run uses it.
*Use built-in settings* puts Martlet's own text and choices back. The page saves only the
prompts you typed there, into the newest saved settings, so edits to other
prompts stay. Companion › Prompts › *Check-ins* also lists every check-in
prompt: the instructions every check-in gets, one for each built-in check-in,
*Check-ins: each check* (the wrapper of every check), the reminder for the next
reply, what adds to what Martlet knows and the two prompts
for what is brought up. Empty a built-in check-in's prompt and it doesn't run.

**What you see.** Companion › Check-ins shows how many are on and the member
that takes them first (`CheckInsNow`), the last check-in that ran
(`CheckInsLast`), and for each check-in why it waits, its last run and how many
times it ran and acted (`CheckInStatus-<id>`). *Check now* runs one at once,
whether it is on or due, when it has something to check. The desktop log notes
each run with words and counts only (*Check-ins: Lingering emotes ran on diva
(qwen3:8b) in 1.2 s: turned off {blush}.*), and `check-ins-status.json` says
the same for [MCP](MCP.md#check-ins). Neither keeps what was said, answered or
reminded.

**API** (`Martlet.Conversation.CheckIns`): `All(settings)` lists the
check-ins, `Wait(checkIn, state, last, now)` says why one waits (null: it
runs), `Focus` narrows the facts to what it may act on, `Prepare` makes the
`ThinkingJob` (with `Needs`, and the screenshot and recording it took),
`Gathered` words what a check-in took for the message, `ScriptRan` reads a
script's run, `Read` reads the answer into a `CheckInVerdict` and `Note` words
a reminder. The desktop gathers `CheckInState` on its UI thread
(`MainWindow.CheckIns.cs`), and the screenshot, recording and script output
just before the run. To add a built-in check-in:

1. Add it to `CheckIns.BuiltIn` with its facts, conditions and outcome, and
   its prompt in `PromptCatalog` (the task only; *Check-ins: each check* adds
   the answer format).
2. For a new fact, add a `CheckInFacts` flag, its text in `CheckIns.Facts` and
   its placeholder in `CheckIns.Placeholders`. For a new condition, add a
   `CheckInConditions` flag and its wait in `CheckIns.Wait`. Add each to the
   page's choices, so your own check-ins can use it too.
3. Give it an outcome Martlet already acts on, or act on a new one in
   `MainWindow.ActOnCheckInAsync`.

Checked locally: `CheckInsTests` (waits, messages, answers, the board note, the
wording beside a due reminder, settings, the model a check-in needs, what it
takes, a script's run, each built-in check-in recreated as your own with the
same message, waits and answers, a built-in check-in's changes saved and
read back, and tool sets: the tools a job offers, the call limit, unknown and
failing tools, and the sets saved and read back), `SoundDigestTests` (the kept microphone and the
sound kept for a check-in), the Desktop tests for an emote a reply
turned on against a try, a check-in taking the eyes back to their usual gaze and
the real talk window bringing up a check-in's `SAY:` through a fixture Thinking
endpoint, MCP's `check_ins_check` and `check_ins_status`, and the page on a
disposable data folder through `-Desktop`. A real model answering a check-in,
a real model calling a check-in's tools, and a real screenshot or recording
sent to a pool member, are **NOT RUN**.

## Singing in conversation

*"Martlet, sing me a song."* Martlet answers in character (*"Ooh, I'd love to!
Let me work on a song for you."*) and calls `sing_song` in that same reply,
choosing what the song is about itself when the user didn't say (small models
told to talk first and call afterwards, or given a line to say, often said they'd
sing and never called it, so nothing was made). It makes
the song in the background while the conversation carries on, brings it up when
it's ready (*"Nice job on killing that noob! Oh, and that song's ready, wanna
hear?"*) and sings it on a yes. It is offered while singing is set up (Companion
› Singing, see [Singing](SINGING.md)) and the Thinking route does
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
*Thinking input (Song lyrics)* in the log; within the song's own 15 minutes).
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
| Background think (think_longer) | One at a time on each Deep thinking place (up to 8), no hourly limit; its own text-only runtime and authorization, never spoken; Thinking steps On at Medium or High; 8,192 or 16,384 output tokens, 65,534 stream events and 16 MiB; no time limit (the request gets the providers' one-day ceiling, a paired computer's route its 15 minutes); at most one declined tool round |
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
- **Short first sentence.** Companion › Replies › *Short first sentence* (on
  by default) asks every spoken reply to begin with a few words (*"Oh, nice
  one!"*, *"Hmm, good question."*) and then go on. The voice gets a piece as
  soon as its sentence is written, so a short first sentence reaches the voice
  after a few words instead of a whole long sentence. The prompt (Companion ›
  Prompts › *Short first sentence*) goes just before *Reply length* in the
  instructions and is the same text every time, so prompt caches keep it;
  turning it off or on changes the start of the requests once. A reply that
  isn't spoken never gets it. The rules for pieces don't change: a short first
  sentence still waits for the next few words, in case they are a short ending
  to say with it (*Where the voice pauses*, below). MCP's `prompts_status`
  shows the setting and what closes a spoken reply's instructions.
- **Quick sounds while Martlet thinks.** Off by default (Companion › Voice).
  When a confirmed spoken reply has no audio of its own 700 ms after it was
  confirmed (or 300 ms when the Thinking model thinks first), Martlet plays one
  short clip in its own voice (*"Mm,"*, *"Hmm..."*) and the reply follows it on
  its own playback run, uncut (`ConversationTurn.PlayQuickSound`,
  `QuickSoundWatcher`). Never on a fast reply, never twice in one reply, at
  most once every 20 seconds, never while a reply started early is held or
  while a reply is paused for you, never for a song, never in the text or
  history. The clips are made once per voice and character with the reply's
  own voice and kept in `quick-sounds\`; a paid cloud voice makes them only on
  the owner's click. Details and measurements:
  [Voice latency](VOICE_LATENCY.md#quick-sounds-while-martlet-thinks).
- **Backup Thinking.** Off by default (Companion › Thinking pool). When the
  reply's Thinking request has no first words after a wait (automatic: the
  95th percentile of recent replies, at least 900 ms), the same request also
  goes to a pool member the owner allowed to answer for the conversation, and
  the stream with words first gives the reply; the other stops at once. See
  [Backup Thinking](#backup-thinking-a-hedged-request).
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
  what you said is answered next, with the reply so far kept in context. (A
  touch on the desktop character can stop a reply too, under its own setting:
  *Touching Martlet while it talks* in [Touch zones](AVATARS.md#touch-zones).)
  Of your voice, only the microphone can do this, and only with words
  (`BargeInPolicy`, the one place that decides): a stop word ("stop", "wait", "hold on", "shh", "never
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
- **Pause and decide.** *When you talk over Martlet* in Companion › Listening
  (`BargeInBehavior`, saved in `talk-preferences.json` and shared with your
  other PCs) chooses what real words over a reply do. *Pause and decide* (the
  default): a clear cue (a stop word or Martlet's name, `BargeInDecision.Cue`)
  still stops it at once and is never judged. Other words that `BargeInPolicy`
  would stop on pause the reply at once instead (`ConversationTurn.Pause`): the
  sentence playing stops reading audio (`PlaybackRun.Pause`; what the device
  already took, at most 100 ms, plays out), and a sentence that starts meanwhile
  starts paused. The Thinking text and the voice's synthesis go on and buffer,
  so playing on (`Resume`) starts at once from the exact sample where it
  paused, with a 10 ms fade-in and nothing made again. The character's emotes
  that the reply hasn't reached wait too, and play at the same point in the
  speech. A judge then decides
  (`IBargeInJudge`, through `BargeInJudging.RuleAsync`): *interrupt* stops the
  reply as before and what you said is answered next; *not for Martlet* plays
  it on. The judge reads Martlet's current sentence (`ConversationTurn.Sentence`),
  the reply's last 400 characters, your words so far and speech-to-text's
  confidence. With a [Thinking pool](#the-thinking-pool) member that can run it,
  the judge is a model: `ModelBargeInJudge` posts a `BargeInJudge` job (the
  pool's highest priority, a fast kind that never waits behind long thinking
  when the pool has two or more slots; 16 tokens out, no reasoning steps, never
  the conversation's own route, so the reply's prompt cache is left alone) and
  reads INTERRUPT or NOTFORME from the answer. A pool with no member lets the
  local rules decide at once; no member free before the deadline, a failed job
  or an answer that names no verdict lets them decide too. Otherwise it is the local rules judge (`RulesBargeInJudge`): the
  policy first (a backchannel, too few words or non-words are not for Martlet),
  then Martlet's own sentence heard back (three words in a row from it, or
  three different words all in it: a TV, a call or a missed echo) and words
  that only agree or laugh along ("yeah that's so true", "haha no way") are not
  for Martlet; everything else is. The model judge has 400 ms
  (`BargeInJudging.Deadline`); a slower one gives way to the rules. While paused,
  `BargeInHold` watches your voice frame by frame: an interrupt verdict stops
  the reply, talking on for 1.5 s in all (`KeepTalkingLimit`) stops it
  whatever the verdict, and with a not-for-Martlet verdict 240 ms of quiet
  (`QuietToResume`) or the end of your utterance plays it on. Later quick
  checks of the same words judge again, and a stop word stops it. A pause never
  lasts more than 4 s (`MaximumPause`; with no verdict it plays on). Without a
  quick check (speech-to-text on a host or the cloud) the utterance's own
  transcript is judged once you pause, so the reply pauses only for the
  judge. Words that were not for Martlet are still answered after the reply,
  as before. A song is never paused (its own rule applies). The desktop log
  says *Barge-in: Martlet paused its reply N ms after you started talking over
  it (...)*, then *Barge-in: Martlet resumed its reply after a N ms pause: what
  you said wasn't for it (why; the rules judge in N ms; N ms of your voice
  during the pause)* or the stop line with *paused N ms first, then the rules
  judge (N ms) said it was for Martlet*; the reply latency line ends *, paused
  N ms when you talked over it, then resumed* or *, paused N ms, then stopped
  when you talked over it*. The talk window's `LiveBargeIn` line shows the last
  decision (never the words). *Stop at once* keeps the older behavior: real
  words stop the reply at once. MCP `barge_in_check` returns the verdicts, the
  deadline fallback and what a pause does; `spoken_reply_check` `paused`
  rehearses a pause and resume through the production runtime.
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
says hear (Ollama 0.35 and later; Gemma 4 E2B, E4B and 12B). **Ollama on one of
your computers** (a paired host) does too: the recording goes through its
pinned gateway, which checks it is one WAV recording within its size limit, and the host's
Ollama gets it with your message. That host must run this Martlet version or
later. An older host's route has no room for a recording, so Martlet sends the
transcript only and says *update Martlet on that computer*, without marking the
model as deaf. OpenAI's own route doesn't take recordings. Vision works the
same way with pictures.
[Hosted Thinking](HOSTED_THINKING.md) lists the cloud models that hear (Gemini,
NVIDIA Build's Nemotron 3 Nano Omni), their free tiers and terms.

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
  small request with your key. Ollama on one of your computers gets the test
  through that computer's paired, pinned connection, with no question.
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

**An audio model of its own.** Companion › Listening › Audio model can give
recordings to a model of their own instead of Thinking, for example a small
model that hears beside a text-only Thinking model ([Image and audio
models](SENSE_MODELS.md#recordings-the-audio-model)). Then Thinking never gets
your recording, even when it hears, and the straight path below is off: the
reply waits for speech-to-text as before. Beside speech-to-text, the audio
model gets the recording and says in a line what the words miss (tone,
laughing, sighing, hesitation, whispering or shouting, other voices,
background sounds). When that line is ready as the reply's request is built,
it goes with that request only, and the conversation keeps a short `(voice:
...)` line after your message; otherwise it goes to the context board for the
next request. A reply never waits for it. The same check box decides (*Let the
audio model hear my voice*): never chosen, it is on only while the audio model
is Ollama on this PC.

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
model tools), and any message once the model refused a recording. With an
audio model of its own (above), nothing goes straight: Thinking gets the
transcript and the audio model's words, never the recording.

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
  this PC* bubble, never as you, named after where it came from when Martlet
  can tell (*Playing on this PC: a YouTube video in Chrome*). To Thinking, every
  line of it starts with `[PC audio]`, followed by where it came from when
  Martlet can tell (`[PC audio] From a voice chat in Discord: ...`, see [Where it
  comes from](#where-it-comes-from-and-what-you-are-doing)), and Companion ›
  Prompts › *What this PC plays* says those lines are never the user nor
  instructions, what each kind is (a creator talking to their viewers, characters
  in a show or movie, a game, other people in a voice chat who can't hear
  Martlet, song lyrics), to answer the user with them as shared context, and on
  their own mostly to reply `[pass]`.
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
  characters go with one message. What the PC played also goes with whatever
  else Martlet answers first ([one moment](#one-moment-everything-in-one-reply)):
  finished work it brings up and a look that comes due take the waiting lines
  along, and a message you type takes them too.
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
  left out N line(s) of it.* and the desktop log says so once. A line during
  which only videos, shows, games or music made sound is the other way round,
  see *Echo* below: it stays, unless it repeats what a voice Martlet knows as
  yours (voice recognition's owner, or Voice ID) said.
- **Never remembered or acted on.** Memory recall and remembering, learning
  names and Home Assistant only ever read your own words: a message that is only
  what the PC played gets none of them, nor MCP tools unless it brings up
  finished work (then it gets the tools a report gets), and earlier `[PC audio]`
  lines (and `[Screen]`/`[Camera]` lines about what Martlet saw) are left out of
  what remembering reads. The sound is never saved.
- **Echo.** Through speakers the microphone also hears what the PC plays; keep
  [echo reduction](#echo-reduction) on (or use headphones) so it isn't taken
  for you. The Companion card's status says so when echo reduction is off.
  What leaks past it is caught when Martlet can tell where it came from: a line
  the microphone heard is this PC's speakers, not you, when at least 4 of its
  words, and at least 70% of them, are in order and close together in a line
  the PC played at that same moment (`PcEcho.Speakers`: the two lines overlap
  in time, and where the words sit in the PC's line, at an even pace of speech,
  is within 1.5 seconds plus 15% of that line's length of when the microphone
  heard them; a phrase the video said earlier never counts), and only videos,
  shows, games or music made any sound during the PC's line. It never becomes
  your words: the talk window shows a faded note
  (*Ignored "..." (this PC's speakers: a YouTube video in Chrome).*), the PC's
  own line carries the words to Thinking, a reply started early for it is let
  go and its words never stop Martlet. When the microphone's line came first and
  still waits for a reply, the PC's line takes its place the same way. Any
  sound during the PC's line from a voice chat, a call, a voice changer, an
  unknown app or Windows itself means it doesn't count (they may play your own
  voice back), and a browser that shows a call in any of its windows counts as
  that call. A voice Martlet knows as yours never counts as the speakers. The
  `LivePcAudio` line's tooltip adds *The microphone also heard this PC's
  speakers; Martlet left out N line(s) of it.* and the desktop log says so once.

The talk window's `LivePcAudio` line says whether Martlet hears the PC now
(*Also hearing this PC.*) or why it can't; its tooltip says how (without its
own voice, or only on the output you hear while it pauses for Martlet's voice),
what you seem to be doing on the PC (*Now: playing a game (Elden Ring), full
screen; in a voice chat in Discord.*) and how many lines of the speakers or of
your own voice played back it left out. The Companion card's status names the other output in use.
`pc_audio_check` in
[Martlet MCP](MCP.md) reads the choice, asks Windows whether Martlet can be
left out without recording anything, says which outputs are in use and what
Martlet would hear, rehearses the production path with a fixture loopback and
runs the own-voice comparison on fixed samples. `pc_activity_check` looks at
what this PC plays now and rehearses where lines come from (next section).

### Where it comes from and what you are doing

While Martlet hears what this PC plays, it also follows which app plays it and
what kind of thing that is, so a video or a voice chat is never taken for you.
It is deterministic and local (`PcActivityMonitor` with
`WindowsPcActivitySource`, on its own thread at below-normal priority, only
while the PC listener runs and Windows isn't locked):

- **What it reads.** About ten times a second, the peak meter of every app's
  audio sessions on every output (the volume mixer's meters, never the sound;
  Martlet's own processes are left out). Every two seconds, for the apps that
  played lately and the window in front: the process and where its program is,
  the titles of its visible windows (a private window's title, such as
  *InPrivate*, *Incognito* or a password manager, is never read), whether the
  window in front fills its screen, whether Windows says a game runs in
  exclusive full screen, and how busy the app keeps the graphics card's 3D
  engine (Windows' GPU Engine counters). Nothing is recorded, saved or sent;
  on this PC a look takes well under a millisecond after the first.
- **What it tells apart** (`PcActivity.Classify`): a site in a browser by the
  name at the end of its window title (YouTube and other video sites, Twitch
  and Kick streams, Netflix, Prime Video, Disney+, Plex and other shows or
  movies, Spotify, YouTube Music and other music, Google Meet and Teams calls,
  Discord; a call or voice chat in any of the browser's windows wins, since the
  sound may be that call), video players (VLC, MPC-HC, mpv and others: music when the title
  names a music file, otherwise a show or movie), known apps (Plex, Netflix,
  Kodi, Jellyfin, Spotify, Discord, TeamSpeak, Mumble, Zoom, Teams, Slack and
  others) and games: an app installed in a game library (Steam, Epic, GOG,
  Xbox, Riot, EA, Ubisoft and others), one Windows runs in exclusive full
  screen, one that fills the screen with the 3D engine at least 10% busy, or one
  that keeps it at least 35% busy in a window. Launchers, OBS, voice changers,
  editors and Windows itself are never games. Anything else is *something
  playing in* its app.
- **Where a line came from.** Each line the PC played is named after the apps
  that were loud while it was heard (`PcActivityMonitor.Between`, from when its
  voice began to when its recording ended): the loudest, and a second one only
  when it was nearly as loud (*From a voice chat in Discord or a game (Elden
  Ring)*). Every app that made any sound then is kept with the line too, and
  only when all of them were videos, shows, games or music does the line count
  for the speakers check above. The talk window's bubble and the line to
  Thinking say where it came from; a line with no app loud then says nothing
  more, as before. In the Discord call mode lines are named after who spoke
  instead.
- **What you are doing.** The apps that played in the last 8 seconds (a voice
  chat or call: 60 seconds, people pause) and the one that fills the screen go
  to the [context board](#context-board) as source `activity`, fresh for 30
  seconds and posted again every 10 seconds while it stays the same: *What the
  user seems to be doing on this PC now (a guess from which apps play sound and
  which window fills the screen): playing a game (Elden Ring), full screen; in
  a voice chat in Discord; watching a YouTube video in Chrome.* Several things
  at once are all there, what fills the screen first. A reply never waits for
  it and it never goes into the history or memory; Companion › Prompts ›
  *Always listening* tells Thinking that during a voice chat or a game you may
  be talking to other people, and that sound from the speakers can reach the
  microphone. The desktop log says *What you're doing on this PC, as Martlet
  guesses it: ...* (kinds and app names, never a game's or a page's title) when
  it changes.

None of this adds to the time before Martlet's first word: a line is named
from levels already read, the note is taken from the board as the request is
built, and both prompts stay the same from request to request.

`pc_activity_check` in [Martlet MCP](MCP.md) looks at this PC for a few seconds
(every app with an audio session, its kind and label, and the note a reply
would read; raw titles are never returned) and rehearses the classifier, where
fixture lines came from, the note, the speakers check and the prompts.

### Describing PC sounds

Transcripts keep only words. Music, its mood, game and video sounds,
laughter, applause and alarms are lost. *Describe PC sounds* (Companion ›
Listening › Watch along, on by default, works only while *Hear what this PC
plays* is on) adds one short line about that sound for the next reply. It
never adds raw audio to the live request: audio costs many tokens, slows the
first word and paired hosts reject it.

- **What is kept.** While the PC listener runs, `PcAudioDevice` also
  normalizes each packet to 16 kHz mono into `PcSoundBuffer`: the last 15
  seconds, in memory only, never saved or logged. It fills only while the
  digest is on; turning it off clears it.
- **When it runs.** `SoundDigestScheduler` ticks each second. About every 10
  seconds it takes the newest 10-second clip and gives it to the judge, but
  only when something plays (at least 30% of 100 ms windows louder than about
  -45 dBFS; silence is skipped), only when no judge is still busy, and never
  with Martlet's own voice: where Windows can't leave Martlet out, the clip
  starts 1 second after Martlet last spoke or sang. A judge that takes longer
  than 15 seconds is canceled and its clip dropped. Each clip is cleared once
  judged.
- **Who judges.** The audio model of its own, while it takes recordings
  (Companion › Listening › Audio model, [Image and audio
  models](SENSE_MODELS.md#recordings-the-audio-model)), gets the clip first
  (a `PC sounds` job on its lane, the same WAV and prompt; on a model that
  shares the conversation's computer, only while the live floor is idle).
  Otherwise an audio-capable model in the Thinking pool gets the clip as
  a WAV with `SoundDigest.Prompt` (one line of at most 20 words about the
  non-speech sound; *none* for speech or silence). Without either, the small CPU
  sound tagger bundled with Martlet (`SoundTagger`: the sherpa-onnx Zipformer
  small AudioSet tagger, Apache-2.0, on one thread below normal priority,
  about 150 ms for 10 seconds) names what it hears, and `SoundDigest.Line`
  turns the labels into a line such as *Music: pop with singing, happy;
  laughter*. Speech, room tone and noise labels are left out.
- **Where the line goes.** To the context board as source `sound` (*Sound
  playing on this PC besides speech: ...*), with a maximum age of 45 seconds,
  and cleared when the digest stops. A reply never waits for it, and it never goes
  into the history or memory. Companion's `TalkDescribePcSoundsStatus` says
  which judge is used and shows the last line with its age (in memory only);
  `sound-digest.json` in the data folder keeps the state, the judge, counts
  and times for MCP, never a line or a sound.

`sound_digest_check` in [Martlet MCP](MCP.md) reads the choices and that
status, and rehearses the path with a FIXTURE clip through the bundled tagger.

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
[ElevenLabs](ELEVENLABS_VOICE.md#tags), a cloud voice that speaks with a
voice cloned from yours, has its own catalog (`SpeechEngines.ElevenLabsTags`:
`[laughs]`, `[sighs]`, `[whispers]`, `[happy]`, `[sad]`...) in
`SpeechEngines.CloudVoices`. Cloud voices are not host engines, so they are not
in `SpeechEngines.All`, but their tags work the same way below and are
stripped from the chat for every voice.

- **Thinking prompt.** When a spoken reply's voice has tags, Companion ›
  Prompts › *Voice sounds and tones* is added to its instructions with exactly
  that engine's tags in two groups: its non-word sounds (written inline where
  the sound happens) and its tones of voice (written at the start of a
  sentence; each spoken piece is synthesized on its own, so a tone reaches only
  that sentence, and a reply that should keep a tone, such as one asked to
  whisper, starts every sentence with it), one tag per line with when to use
  it. It asks for them sparingly. Text-only replies and voices without tags
  never get it.
- **Segmenter.** The speech segmenter (which still silences lines with
  markdown, links, code or other bracketed text) lets the speaking engine's
  tags through, case-insensitively, in the engine's own spelling, even when a
  tag arrives split across stream deltas. Any other registered engine's tag is
  dropped without silencing its sentence, so OpenAI, F5 and
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
  as it starts playing (or at once for a reply that isn't spoken). Each cue
  waits for its moment on the reply's own clock (`CharacterCueLine.ReachedAsync`).
  The clock stands still while the reply is paused, so a cue keeps its place in
  the speech. The clock stops when the reply is stopped, replaced or fails, so
  the character doesn't act the cues the reply hasn't reached. The desktop log
  then says *Character cue {wink} wasn't acted: the reply stopped before it got
  there.* Muting the voice drops no cues: the reply goes on in the captions, and
  so do its cues.
- **Under the reply.** The talk window notes how a reply was acted out under
  its bubble (`ReplyTag.Note`): *Tone: happy. Sound: laugh. Emotes: nod,
  blush.*, each part only when the reply wrote one: the tones and sounds its
  voice performed and the character tags it wrote. The desktop log has
  *Reply acted: {nod} (written [nod]), [laugh].* (tag names and spellings only,
  never the words).

`voice_tags` in [Martlet MCP](MCP.md) shows all of these for any engine (with
`characterTags`, the character cues too, plus `acted` and `note`), and
`spoken_reply_check` with `characterTags` runs them through the production
runtime. `elevenlabs_check` runs ElevenLabs' tags through the production
runtime against a local ElevenLabs protocol fixture.

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
- **Judge when I finish talking** (on by default, Companion › Listening ›
  How you talk) lets an end-of-turn judge decide when you finished instead of
  the pause alone. After 260 ms of silence (`EndOfTurnGate`), Smart Turn v3.2
  (`SmartTurnJudge`, a small model bundled in `turn-detection\` that runs on
  this PC's processor in about 25-50 ms) hears the end of what you said. With
  Parakeet on this PC as Listening, a quick transcript of exactly the speech
  that would be kept starts at the same moment. *Complete* ends the turn at once,
  and speech-to-text reuses that quick transcript when the kept audio is the same
  (no second transcription). *Incomplete* keeps listening for up to twice your
  pause (at least 1.6 s), so trailing off mid-thought is cut off less. A
  missing, failed or slow judge (no answer before your pause ends) leaves your
  pause to decide, exactly as with the judge off. Each decision writes one line
  to the desktop log (*End of turn: complete (Smart Turn v3.2, 0.93) after 280
  ms of silence; judge 31 ms.*), and the reply latency line shows *end-of-turn
  wait* and *end-of-turn judge* in place of *end of speech*. A wrong *complete*
  is recovered the usual way: keep talking (barge-in or the restart below).
  When Smart Turn is missing or fails, a [Thinking pool](#pool-api-desktop)
  member judges the quick transcript instead (`PoolTurnJudge`: an
  `EndOfTurnJudge` job that must answer COMPLETE or INCOMPLETE within 500 ms;
  without a quick transcript or an answer in time, your pause decides). Other
  judges plug in the same way through `IEndOfTurnJudge` and
  `EndOfTurnJudges.WithFallback`.
- **Start replies early** (on by default, Companion › Listening › How you
  talk) starts the reply at that same short pause, as soon as the quick
  transcript (Parakeet on this PC) has real words, without waiting for the
  verdict (`EarlyReplyGate`, `ConversationRuntime.StartEarly`). It is built
  exactly as the talk window will ask for it (`EarlyReplyPlan`: only while
  nothing else waits for a reply) and *held*: Thinking streams and, with
  **Prepare the voice early too** (on), the first spoken piece is made, but
  nothing shows, plays, acts or calls a tool. When the turn ends in that pause
  and the talk window asks for the same request (`EarlyAsk`), it is promoted
  (`ConversationTurn.Release`): no second request, its words show and its
  first piece plays at once. Your own voice coming back, the turn ending in a
  later pause, other words or something else going with them (a picture, what
  this PC played, typed text) let it go: its request and voice work stop, and
  the next pause starts another (at most three a turn). Only a reply that is
  taken commits to answering, lets go of old history, consumes the context
  board's consume-on-read notes or reaches the history, memory, the talk
  window and the reply latency line. **Also for cloud models (may add a small
  cost)** (off by default) allows it with a cloud Thinking model, which charges
  for a request let go; a paid cloud voice is prepared early only with it, and
  a held reply asks the Thinking fallback (*If Thinking fails*) only once it is
  taken.
  Barge-in never sees a held reply as Martlet speaking, and it holds the
  [live floor](#the-live-floor-the-live-turn-comes-first) from its start (let
  go, it ends that hold at once). The desktop log has
  *Early reply: ...* lines, the reply latency line *Started early at 262 ms,
  promoted.*, and Companion › Listening's status counts the newest outcomes
  ([Voice latency](VOICE_LATENCY.md#starting-replies-early)).
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
- When the microphone fails (missing, busy, blocked by Windows privacy settings,
  changed or lost while it records), always listening opens it again after
  1 s, then 5, 10, 20 and 30 s while the failures go on in a row; an utterance
  that ends normally resets the wait. Home's listening indicator says why and
  how often Martlet tries (*… Martlet keeps trying every 10 s.*). The desktop
  log has one *Always listening: the microphone failed …* line for each
  failure: the error code, whether the microphone gave sound first, Windows'
  default or a chosen microphone, echo reduction, and the wait. After five
  failures in a row it logs only a changed error and every tenth failure. When
  the microphone works again, the log says *… works again after N failures in
  a row*. Hearing what this PC plays uses the same waits.

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
.\scripts\Invoke-MartletMcp.ps1 -Desktop -Calls '[{"name":"ui_snapshot"}]'
```

On the pooled-drive developer host, use one unique session-owned **C:** artifacts
directory consistently for restore/build/test, `CI=true`, process-only SDK PATH/
DOTNET_ROOT, own CLI home, telemetry off and ASP.NET certificate generation
disabled. Do not build in the primary checkout or change test/SDK pins.
Dedicated provider/runtime/policy project commands remain available for focused
local runs even though their projects are now also in the root solution.
Hosted validation is removed under the
[local-only repository policy](../CONTRIBUTING.md#local-only-validation-policy).

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
