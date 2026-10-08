# Image and audio models: who sees, who hears, who answers

Martlet works with three kinds of input: text, pictures and recordings. This
page says which model takes each kind, how the words of an image or audio
model go to the model that answers, and the rules that keep the time to
Martlet's first word the same.

Status (2026-10-08): in place. The foundation (#634) has the settings file,
the routing, one job line for each model, the runner, the status file and MCP.
The image pipeline (#640) and the audio pipeline (#637) use it, and the
Companion cards, Test vision and the metadata checks (#644) choose the models.
Helper jobs with a picture use the image model (#635, #641). A real image or
audio model was NOT RUN; see [Checks](#checks).

## Short answers

- **Where does vision run today?** On the Thinking model. A picture goes in the
  same request as your message, and the model that answers looks at it. There
  was no separate vision model (VLM). Gemma 4 E2B, Martlet's default on this
  PC, sees and hears, so one model does everything.
- **Which model writes Martlet's reply?** Always the text model: Companion ›
  Thinking. An image or audio model never talks to you. It only puts what it
  sees or hears into words for Thinking.
- **Why choose separate models?** To talk with a strong model that only reads
  text (for example Qwen3 8B) and to use a small vision model or a model that
  hears beside it. This needs enough hardware, or cloud models.
- **What is the default?** "Use the same model as the text model", for pictures
  and for recordings. With the default, nothing changes: every request is the
  same, byte for byte.
- **Does it make replies slower?** No. A reply never waits for an image or
  audio model (see [Latency rules](#latency-rules)).

## The models and what they do

| Role | Where you choose it | What it does |
| --- | --- | --- |
| Listening (speech-to-text) | Companion › Listening | Makes the words of what you say and of what this PC plays. Barge-in, the end-of-turn judge and the word check read these words. |
| Thinking, the text model | Companion › Thinking | Writes every reply, remark and report. Takes pictures and recordings itself when its route is set to. |
| Voice (text-to-speech) | Companion › Voice | Says the reply. |
| If Thinking fails | Companion › Thinking | Answers when Thinking fails before it says anything. Gets text and pictures, never recordings. |
| Thinking pool | Companion › Thinking pool | Background jobs: thinking longer, research, screen and sound summaries, judges and helpers. |
| Image model | Companion › Vision | Takes pictures when it isn't the text model. New. |
| Audio model | Companion › Listening | Takes recordings when it isn't the text model. New. |

**A reply's request.** It starts with the instructions, which stay the same
from request to request. The conversation so far follows, each message exactly
as it was sent. Your message comes last, with Martlet's notes when something is
new, and then the [context board's](CONVERSATION.md#context-board) notes, which
go with this request only. This layout lets prompt caches reuse the start of
every request ([the request layout](CONVERSATION.md#prompt-caching-and-the-request-layout)).

**Pictures and recordings before this change.** While vision is on, the newest
picture of what Martlet watches (at most 10 seconds old) goes with your
message, and a look sends one screenshot. With *Let Thinking hear my voice* and
a Thinking model that hears, your recording goes with your message, alone or
with the transcript ([Thinking models that hear and
see](CONVERSATION.md#thinking-models-that-hear-and-see)).

**Background describers that already exist.** The [screen summary over
time](SCREEN_COMMENTARY.md#martlet-knows-what-changed-over-time) and the
[sound digest](CONVERSATION.md#describing-pc-sounds) run on Thinking pool
members. They post short notes to the context board, and a reply never waits
for them. The image and audio models use the same idea for your own pictures
and recordings.

## Choosing the models

| Image model | Audio model |
| --- | --- |
| Use the same model as the text model (the default) | Use the same model as the text model (the default) |
| Use the same model as the audio model | Use the same model as the image model |
| Ollama on this PC | Ollama on this PC |
| A cloud provider or an OpenAI-compatible server | A cloud provider or an OpenAI-compatible server |
| One of your computers (a paired computer's Ollama) | Not offered: a paired computer's gateway takes no recordings |

- "The same model as the other kind" in both directions means the text model.
- A model of its own that is exactly Thinking's (the same endpoint and model,
  or the same paired computer and model) counts as the text model.
- The choice belongs to this PC (`sense-models.json` in the data folder). It is
  never shared, because the models that fit beside Thinking depend on the
  computer.
- A model's key is in Windows Credential Manager. A model on the same base URL
  as Thinking can use Thinking's key.

### The cards on Vision and Listening

Companion › Vision has an **Image model** card right after *Now*, and
Companion › Listening has an **Audio model** card above *Hear how you say it*
(`MainWindow.SenseModels.cs`). Each card has two parts.

1. **What takes it now:** the choice (`ImageModelNow`), where the input goes
   and why (`ImageModelRoute`, the words of `SenseRouting.For`), what the
   model is known to do (`ImageModelKnown`) and, for a model of its own, what
   is sent and where (`ImageModelSent`). Then the card's test: **Test vision**
   (`ImageModelTest`) asks the model that takes pictures, and **Test hearing**
   (`AudioModelTest`) asks an audio model of its own. Thinking's own Test
   hearing stays under *Hear how you say it* (`TalkHearVoiceTest`).
2. **The choice:** one option for each row of the table above
   (`Place-ImageModel-Thinking`, `-OtherSense`, `-ThisPc`, `-Cloud` and
   `-Computer`; `Place-AudioModel-...` without `-Computer`). An option only
   shows its panel. The panel's own button saves `sense-models.json`, and the
   running conversation follows at once (`ReloadSenseModels`).

The panels:

| Option | What you fill in | Saves with |
| --- | --- | --- |
| The text model, the other kind's model | Nothing | `ImageModelUseThinking`, `ImageModelUseOther` |
| Ollama on this PC | A model Ollama has (`ImageModelLocalModel`), with what each one takes (`ImageModelLocalStatus`, `ImageModelLocalKnown`) and whether it fits beside Thinking's on the graphics card (`ImageModelLocalFit`) | `ImageModelUseLocal` (`ImageModelPullModel` downloads it) |
| A cloud provider or server | The provider (also a model app found on this PC), the base URL, the model ID, a key and the consent tick | `ImageModelSaveCloud` |
| One of your computers | Nothing: each paired computer offers its Thinking pool role, or its Ollama when it doesn't do Thinking for this PC (`ImageModelHost-<host>`) | `ImageModelUseHost-<host>` |

Keys follow these rules (`SenseModelChoice`):

- A model keeps its saved key for the same base URL. One key can serve both
  kinds: an audio model on the image model's base URL uses the image model's
  key.
- Without a key of its own, a model on Thinking's base URL uses Thinking's
  key.
- A new key replaces the old one. Martlet deletes a key from Windows Credential
  Manager when no choice uses it any more.

Companion › Thinking's *Now* card says that Thinking is the text model and
where pictures and recordings go (`ThinkingSenses`), with links to the two
cards (`ThinkingOpenImageModel`, `ThinkingOpenAudioModel`).

## Where each kind of input goes

Each kind of input takes one of three paths (`SensePath`):

| Path | What happens |
| --- | --- |
| Thinking | The picture or recording goes in Thinking's own request, as before. |
| Described | The image or audio model gets it and answers in words. The words go to Thinking as text. |
| None | No model takes it. No picture is sent, and Thinking gets the transcript only. |

`SenseRouting.For` decides the path:

1. When the kind's model is the text model, the path is Thinking when Thinking
   takes it. Pictures go to a Thinking model that Martlet can't tell about (it
   tries, as before). Recordings go only to a Thinking model known to hear, as
   before. Otherwise the path is None.
2. When the kind has a model of its own, the path is Described when that model
   sees (or hears), or when Martlet can't tell: it tries, and a refusal is
   remembered. The path is None when the model is known not to see (or hear),
   and for recordings to a paired computer.

### Combinations

The examples use model names Martlet knows: Gemma 4 E2B sees and hears, Qwen3
8B reads only text, Qwen3.5 4B sees but doesn't hear, Qwen2.5-VL 7B sees,
Gemma 3n E4B hears, and Nemotron 3 Nano Omni sees and hears.

| Text model | Image model | Audio model | Pictures | Recordings |
| --- | --- | --- | --- | --- |
| Gemma 4 E2B | Same as text | Same as text | Thinking | Thinking |
| Qwen3 8B | Same as text | Same as text | None | None (transcript only) |
| Gemma 4 E2B | Qwen2.5-VL 7B | Same as text | Described | Thinking |
| Qwen3.5 4B | Same as text | Gemma 3n E4B | Thinking | Described |
| Qwen3 8B | Qwen2.5-VL 7B | Gemma 3n E4B | Described | Described |
| Qwen3 8B | Nemotron 3 Nano Omni | Same as image | Described | Described, on the same model |
| Qwen3 8B | Qwen2.5-VL 7B | Same as image | Described | None: the image model can't hear |
| Qwen3 8B | Same as audio | Same as image | None | None: both point at the other, so the text model |

The third row is the owner's example: the text model and the audio model are
the same. Your words and your recording go to Thinking together, as before.
Only pictures go to the image model, which describes them for Thinking.

`sense_models_check` in [MCP](MCP.md) runs every row with the production code.

## How described input reaches Thinking

This is the design that the image and audio pipelines follow.

1. The image or audio model gets the picture or recording, a fixed instruction
   for its job, and a short context: for a picture, the window title and the
   program in front; for both, the last few lines of the conversation, so it
   knows what matters. It never gets the persona, memory, tools or the whole
   conversation.
2. It answers with one summary line, then details. For a picture: what you do
   or watch, text that matters and what changed. For a recording: tone and
   feeling, laughs, sighs, pauses, whispering or shouting, other voices and
   background sounds, or *none* when nothing stands out.
3. The words go into the reply's request as a note after your message. The note
   goes with that request only, so the start of the next request stays the
   same and prompt caches keep working.
4. The conversation keeps a short line made from the words (for a picture, the
   `[Screen]` line, as now), never the picture or the recording.
5. A fixed instruction tells Thinking what these notes are. It is in the
   instructions while the path is Described, the same in every request, so
   the prompt cache keeps it.
6. The words are never your words. Memory, learning names, the record of
   conversations and Home Assistant leave them out, as they do `[Screen]` and
   `[PC audio]` lines.

## Pictures: the image model

Built by the image pipeline's pull request. With an image model of its own
(the Described path), Thinking never gets a picture: the image model puts
each picture into words, and Thinking gets the words.

**When it describes.** While Martlet watches, the talk window gives each
screenshot a *picture version*. The version stays the same while nothing
changes on the picture, and changes when it changes enough (the glancer's
change score is 0.01 or more) or shows another window, program, full-screen
state or source. The image model describes the newest picture ahead of time:

1. While you talk or type: always listening hears you, you press the talk
   button, you type, or something you said or typed waits for a reply. At
   most one new description every 4 seconds.
2. When a look is due, and the picture Martlet keeps of something that wants
   your attention.
3. When the picture changed, at most one every 15 seconds, and only while
   you talked with Martlet in the last 2 minutes. Nothing is described while
   you play or watch alone.

All picture jobs use one key (`picture`) on the image model's line, so only
the newest picture waits. After a failed description, nothing new starts for
5 seconds, and the same picture isn't asked for again for 30 seconds.

**What the image model gets.** Companion › Prompts › *Image model: describe
the picture* (the same every time), the picture, and a message: what the
picture shows, the program in front and the window's title (a screen only),
and the last lines of the conversation (at most 6 messages, 200 characters
each and 1,200 in all; seen tags removed, `[pass]` left out). It answers with
one summary line, then details, in at most 300 tokens. Martlet keeps at most
1,200 characters of it.

**Replies.**

1. When a reply's request is built, the reply takes a description only if one
   is ready for the picture it would have sent: the same source and the same
   window (title and program), and either nothing changed on the picture since
   the described screenshot, or that screenshot is at most 10 seconds old
   (the freshness rule for a picture that goes with a message). It never takes
   one older than 60 seconds. A look at something that wants your attention
   needs the exact picture.
2. The description goes in the notes sent with that request only (*What the
   image model saw*), after the *Active app with your message* note, which
   names the window the description was made of.
3. While the path is Described, every reply's instructions carry *Pictures as
   words*, with or without a description, so prompt caches keep them. The
   reply isn't told *Screen with your message* or *What you saw*, and it gets
   no `[seen: ...]` tag.
4. The conversation keeps `[Screen] With this message you saw <where>:
   <summary>.`, never the note.
5. Without a ready description, the reply goes without the picture. Your
   message's bubble says so: *The image model's description of your whole
   screen wasn't ready, so Martlet answered without it.* With one, it says
   *Martlet saw your whole screen through the image model's description.*

**Looks.** A screen glance or camera look has two stages:

1. The image model describes the look's own picture. A description of that
   exact picture is used again, and one being made of it is awaited.
2. Thinking gets the glance message with the description (and the text read
   on the screen, last), no picture, no seen tag and no look tags: without the
   picture, Thinking can't say where the character should look, so the
   character's eyes follow what changes on screen. The conversation keeps
   `[Screen] You looked at <where>: <summary>.`

When the image model can't describe the picture, the look ends without asking
Thinking, and the next look waits a while, as for a busy provider. When the
model refuses the picture, Martlet remembers that it can't see, and watching
stops with what to change. Talking or typing stops a look in its first stage
as it stops any look; a description being made goes on for the reply.

**Screen summary over time.** While the path is Described, the summary job
goes to the image model's line first, with the lowest priority (key
`screen-summary`), so the next reply's picture goes first. On the
conversation's own computer and graphics card it starts only while the live
floor is Idle. Otherwise, or when no image model takes it, the Thinking pool
makes it, as before.

**The conversation comes first.** On the conversation's computer and graphics
card, the foundation's hold stops a description when a reply starts and
starts none until the reply's voice is made ([Latency rules](#latency-rules)).
A stopped description leaves nothing for that reply, and no back-off follows.

**Can't see.** Vision works when the path is Thinking or Described. Companion
› Vision (`VisionStatus`, `VisionDisclosure`), Home's warning (*Martlet can't
see with your image model* or *... with your thinking model*), the talk
window's *Can't see* and `commentary.vision_unsupported` follow the path and
apply only to None. An image model that can't see never sends the pictures to
Thinking instead.

**Privacy.** Pictures go to the image model only while vision is on and you
pressed Start watching. Martlet's own windows, password managers and private
windows are greyed out or skipped, as for every look. The image model is told
never to copy private details and to say only who or which app a notification
is from; Thinking is told never to read out private details. Descriptions are
kept in memory only (the newest three), go when the conversation is cleared,
paused or locked and when watching stops, and never go to logs or status
files.

**How to check it.** The desktop log says `Image model: described your whole
screen in 1.4 s (...)` and `Picture path: described (...)` with times only.
`image-model-status.json` counts described replies, replies that went without,
looks and descriptions. MCP's `image_model_check` reads both and rehearses the
pipeline against fixture endpoints ([MCP](MCP.md#image-and-audio-models)).
Code: `PictureDescriptions.cs` (shared with MCP), `LiveConversationController.Pictures.cs`,
`LiveConversationWindow.Pictures.cs` and `ImageModelScreenDigestThinker`
(`PoolScreenDigestThinker.cs`). Tests: `ImageModelPipelineTests`
(Martlet.Desktop.Tests).

NOT RUN in the change that built it: a real image model and a real screen
capture in the talk window; fixture endpoints stood in.

## Recordings: the audio model

In place since the audio pipeline's pull request. With an audio model of its
own (`SenseRoute(Audio).Model` is set), Thinking never gets a recording; with
the default (the text model) nothing below runs and no request changes.

- **Your voice.** When the audio path is Described, your recording never goes
  to Thinking, and the straight path is off (it needs Thinking to hear).
  Speech-to-text makes the words as before, and the reply waits only for them.
  As soon as an utterance ends, the audio model gets it beside speech-to-text
  (a `your voice` job on the audio lane, priority 10, 15 seconds at most):
  the recording, the fixed instructions *Describing your voice* (Companion ›
  Prompts) and a short context (your last message and Martlet's last words,
  without `[PC audio]`, `[Screen]` and `[Camera]` lines). It answers with one
  summary line of at most 20 words and, when it helps, a line of details, or
  *none* when nothing stands out.
- **With the reply, or with the next request.** When its words are ready as
  the reply's request is built, they go in that request's context notes
  (*How you sounded*: "How the user sounded saying this message, as the audio
  model heard it: ..."), and the conversation keeps only a short line after the
  message, such as `(voice: sighs, sounds tired)`. Otherwise the request goes
  without them, and once they come they wait on the context board as one
  consume-once note (source `voice`, fresh for 3 minutes, up to 3 late notes
  together, "... in what they said before this message ..."), kept as
  `(voice, earlier: ...)` after the message that carried them. A reply never
  waits for them. Words that came after the conversation was cleared are
  dropped.
- **The fixed instruction.** While the audio path is Described, every reply's
  instructions have *Your voice, described by the audio model*, the same in
  every request, so the prompt cache keeps the start of each request. Looks
  get no new instruction; the note says what it is.
- **When nothing follows.** What isn't words, another voice, a failed
  transcription or something the talk window drops cancels the job. When
  Martlet doesn't answer a message, its words go to the board for the next
  request. A reply started early takes no words; they go to the next request.
- **Shared hardware.** When the audio model shares the conversation's computer
  and graphics card (`SenseSharesConversation`), an utterance's job starts only
  once its reply's request has started (or no reply follows, at most 30 seconds
  later); the lane then holds it until the reply's voice is made, so it never
  runs beside Thinking before the first audio. Its words always go to the next
  request. A job stopped for a new reply (`Preempted`) goes once more.
- **Consent.** A recording goes to an audio model under the same rule as *Let
  Thinking hear my voice*: your own choice wins; never chosen, it is on only
  while the recording stays on this PC (the audio model is Ollama on this PC,
  not a `:cloud` or `-cloud` model). The check box, its words and the
  disclosure on Companion › Listening name the audio model then (*Let the
  audio model hear my voice*; `TalkHearVoice`, `TalkHearVoiceChoice`,
  `TalkHearVoiceStatus`), and the straight path and *Test hearing* (both about
  Thinking hearing you) are hidden.
- **What this PC plays.** The sound digest uses the audio model first (a `PC
  sounds` job with the same clip and prompt as a pool judge), then a Thinking
  pool member that hears, then the CPU sound tagger, as before. On a model that
  shares the conversation's computer, it skips its turn while the live floor
  isn't idle.
- **What you see.** The talk window notes under your words whether the reply
  took the audio model's words (*The audio model described how you sounded (0.6
  s after you stopped), and the reply took it.*) or that they go with your next
  message, and `LiveTurnInputs` says *how you sounded*. The desktop log says
  *Voice path: described by the audio model (...)* and, for late words,
  *Voice description: ready N ms after you stopped ...* (times only, never the
  words). MCP `hearing_check`, `sound_digest_check` and `audio_model_check`
  show it ([MCP](MCP.md#image-and-audio-models)).

## Helper jobs with a picture

Finding a character's touch zones and measuring its eyes send pictures of the
character (never your screen) to a model that sees. They are [helper
jobs](MEMORY.md#helper-jobs-on-the-thinking-pool):

1. A free Thinking pool member that sees takes the job first, as before.
2. Otherwise, while pictures go to an image model of its own, that model takes
   the job in place of the Thinking model. The job waits behind the image
   model's other jobs, at the lowest priority. When a reply's picture or your
   voice comes for the same model, the job gives way: it stops, and it starts
   again from the beginning when the model is free. A screen summary doesn't
   make it give way. It may write as much and run as long as on a pool member
   (4,096 tokens; 3 minutes for touch zones, 5 for the eyes).
3. Otherwise the Thinking model takes it after any reply, as before.

Companion › Touch (`TouchZonesVision`) names the model that takes the
pictures, and *Detect zones* and *Measure the eyes* stay on while an image
model of its own takes them, also with a Thinking model that reads only text.

## Latency rules

The time from the end of your speech to Martlet's first word must never grow
([AGENTS.md](../AGENTS.md#never-add-conversation-latency)).

1. A reply never waits for an image or audio model. It takes a description
   only when the description is ready as the request is built.
2. Descriptions are made ahead of time: a picture when you start to speak and
   when the picture changes, a recording as soon as you stop speaking.
3. Each model runs one job at a time (`SenseLanes`). When both kinds use the
   same model, they share one line. A newer picture takes the place of an
   older one that waits. A background job (priority below zero, such as a
   helper job) gives way to a job for the conversation (priority above zero)
   and starts again after it.
4. The conversation comes first. A model that shares the conversation's
   computer and graphics card starts no job while a reply runs, until the
   reply's voice is all made: its Thinking request and its voice may need the
   same card. A job that runs when a reply starts is stopped (`Preempted`). A
   job that waits starts after that.
5. A second model in Ollama on this PC runs only while it fits beside
   Thinking's model on the graphics card. Martlet checks this before each job,
   so the conversation's prompt cache stays loaded.
6. With the defaults, nothing new runs and no request changes.

To check a change, compare the desktop log's `Reply latency` and `Thinking
input` lines before and after.

## Finding out what a model can do

What Martlet finds out is in `model-abilities.json`: for each server and model,
whether it hears and sees, where that came from and when. It is shared with
your other computers as the `model-abilities` setting
([Thinking models that hear and see](CONVERSATION.md#thinking-models-that-hear-and-see)).

1. **The server's metadata,** when a model is chosen or checked: OpenRouter's
   input modalities, Ollama's capabilities, llama.cpp's modalities and LM
   Studio's model type. Hosted APIs that publish nothing keep the name.
2. **Test hearing and Test vision:** one small request with a recording of a
   spoken word, or a picture of a written word, made on this PC (never your
   voice or screen). The right word means the model hears or sees. A cloud
   model asks first.
3. **A refusal during use:** when a model refuses a recording or a picture,
   Martlet keeps *doesn't hear* (*a refused recording*) or *doesn't see* (*a
   refused picture*) and sends it no more until a check or a test says
   otherwise. A paired computer's model is kept by its gateway's origin.
4. **The model's name,** last (`VisionModelCatalog`, `HearingModelCatalog`).

How each check works for each model:

| Model | Metadata | Test |
| --- | --- | --- |
| Thinking | When it is chosen or tested (`CheckNewModelContextAsync`) | Test hearing (Listening) and Test vision (Vision), on an OpenAI-compatible endpoint |
| An image or audio model on an endpoint | When it is chosen (`CheckChosenModelAsync`): Ollama on this PC through `/api/show` without loading the model, another server through its model list with the key the model uses. Ollama on this PC is also asked about a model when you pick it in the panel. | Test vision or Test hearing on the card, sent straight to the endpoint |
| An image model on a paired computer | None: its gateway shows no metadata, so its name counts | Test vision through its gateway with the image model's runner: in its lane while it takes pictures (`RunSenseAsync`), else straight to it (`TestSenseAsync`), so a model found not to see can be tested again. It waits while a reply needs that computer |

**Test vision** (`ModelVisionTest`) sends one picture of a single word, drawn
on this PC in large dark capitals on white (`VisionTestPicture`, a 640 x 240
PNG), with the question *The attached picture shows one English word. Reply
with only that word.* The request has Thinking steps Off and a small reply
budget, like Test hearing. The word is one of twelve, drawn at random, so a
model that doesn't see can't guess it. The right word means *sees*. Another
answer, or a server that refuses the picture, means *doesn't see*. A key
problem, a missing model or an unreachable server tells nothing. A test of a
cloud model asks first, because it is one small request with your key.

`model-abilities.json` keeps one source and date for each model, so the
cards say *as Martlet found out (last check on <date>: <source>)*.

## Barge-in, end of turn and the word check

These don't change. They run on the words of speech-to-text (and Parakeet's
quick transcripts) and on text judges. An image or audio model is never on
these paths and never decides whether Martlet stops. A job of theirs on a
computer that the conversation shares stops when a reply starts.

## Privacy

- Pictures go to an image model only while vision is on, which is the
  permission for pictures, as for Thinking.
- Recordings go to an audio model only under the hearing rule above.
- Pictures and recordings are never kept. The status file and the desktop log
  have purposes, outcomes and times, never what was sent or said.
- The Thinking fallback never gets recordings.

## Files

`sense-models.json` (this PC's choice; DeepThinkingSettings fields not shown
are null):

```json
{
  "SchemaVersion": 1,
  "Image": {
    "Source": "Own",
    "Own": { "Place": "Endpoint", "Origin": "http://127.0.0.1:11434/v1", "ModelId": "qwen2.5vl:7b" },
    "ChosenAt": "2026-10-08T19:00:00+00:00"
  },
  "Audio": { "Source": "OtherSense" }
}
```

`Source` is `Thinking` (the default), `OtherSense` or `Own`. A file Martlet
can't read counts as the default for both kinds.

`sense-models-status.json` is written by the desktop: whether a talk window
loaded the settings (`conversation`), and for each kind its path, model and
why, whether its model shares the conversation's computer, and its line (busy,
waiting, held for a reply, runs, and the last job's purpose, outcome and
time). Before a talk window loads the settings, a model of its own is routed
by what Martlet found out about it (`model-abilities.json`), and a kind that
goes to the text model says to set up Thinking.

## For developers

The types are `SenseModels` (Martlet.Core), `SenseRouting` and `SenseLanes`
(Martlet.Conversation). The desktop's controller runs the jobs
(`LiveConversationController.Senses.cs`).

```csharp
// Where pictures go now: Thinking, Described or None, the model and why.
SenseRoute route = conversation.SenseRoute(SenseKind.Image);
if (route.Described)
{
    // A reply never awaits this: start it ahead of time and keep the result.
    SenseJobResult result = await conversation.RunSenseAsync(SenseKind.Image, new SenseJob
    {
        Purpose = "reply picture",   // for the log and the status file
        Key = "picture",             // a newer job with this key takes the place of a waiting one
        Priority = 10,               // higher goes first among waiting jobs
        Instructions = "Describe what the user sees ...",
        Text = "Window: ...; the last lines said: ...",
        Image = picture,             // an audio job carries Audio instead
        Timeout = TimeSpan.FromSeconds(8)
    }, token);
    // Outcome: Succeeded, NoModel, Stale, Failed, TimedOut, Refused or Preempted; Text, Model, Problem, Took.
}
bool shares = conversation.SenseSharesConversation(SenseKind.Image); // on the conversation's computer and card
bool held = conversation.SenseHeld(SenseKind.Image);                 // and a reply runs until its voice is made
conversation.ReloadSenseModels();                                    // after Companion saves sense-models.json
```

The runner sends each job with the kind's own runtime and a one-use
authorization for that model, as a Thinking pool job is sent. A paired
computer's model gets the job as background work. A job waits while it is
held; with `DropWhenStale` (the default) it ends `Stale` when it can't start
within its `Timeout`, so a job that must outlast a long reply sets
`DropWhenStale = false` and passes its own token.

Tests replace three things on the controller: `SenseRunner` answers a job in
place of the model's request (a refused answer is still remembered),
`SenseSharing` says whether a kind's model shares the conversation's hardware,
and `SenseFit` replaces the check with Ollama. Without a data folder, a refusal
is kept in memory only.

## Checks

- MCP: `sense_models_status` reads a data folder's choices, routes and the
  desktop's status file. `sense_models_check` rehearses the routing and the
  lines with a simulated runner ([MCP](MCP.md#image-and-audio-models)).
  `image_model_check` and `audio_model_check` rehearse the picture and voice
  paths against fixture endpoints on 127.0.0.1 with the production code.
  `model_ability_check` rehearses Test vision and Test hearing, and
  `model_lab` runs a fixture endpoint for the desktop's cards.
- Tests: `SenseModelsTests` and `SenseModelChoiceTests` (Martlet.Core.Tests),
  `SenseRoutingTests` and `SenseLanesTests` (Martlet.Conversation.Tests),
  `ModelVisionTestTests` (Martlet.Providers.Tests), and in
  Martlet.Desktop.Tests `SenseModelsDesktopTests` (a fixture endpoint gets the
  picture, a refused picture is remembered, a job waits for or stops for a
  reply on the same computer, helper jobs), `ImageModelPipelineTests` (no
  picture to Thinking, a reply never waits, two-step looks, the default
  requests the same byte for byte), `AudioModelVoiceTests` (no recording
  goes to Thinking, the reply never waits, ready words go with it and late
  ones with the next request, the consent rule, shared hardware, the sound
  digest's judge, the words and the defaults) and `SenseModelCardsTests`.
- NOT RUN: a real image or audio model, and the desktop log's `Reply latency`
  and `Thinking input` lines with real models. Each pull request says what it
  ran.
