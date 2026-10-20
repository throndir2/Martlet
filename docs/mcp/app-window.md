# MCP: The app's window

Part of [MCP in Martlet](../MCP.md): the tools and screens of the *The app's window* area of `martlet_guide` (driving the desktop window: `ui_*` tools, the character window and the notification area). The [index](../MCP.md#pages) lists the other pages.

## Local MCP control (Windows)

The section's status fields (on Companion › Touch, `CompanionTab-Touch`) are `TouchZonesStatus` (how many zones, how many in
use and who found them: the Thinking model, you, or *A first guess Martlet placed from the character's own parts and shape,
with no AI...*; or that none are found yet), `TouchZonesVision`
(which model sees the pictures: a Thinking pool member that can see, else
whether the Thinking model can see and where pictures go; read from the saved
setup, so it is right before the talk window opens), `TouchZonesDetectNote`
(shown only when `TouchZonesDetect` is off because no model can see pictures:
*Detect zones is off: no model that can see pictures is set up...* or *...the
Thinking model is text-only...*), `TouchZonesDetection`
(the first guess on a model with no zones and no picture: *Drawing the
character to place its first zones (no AI, nothing is sent)...* as soon as the
page opens, then *First zones: Martlet placed 24 zones at ... with no AI and
nothing sent...*, with `TouchZonesPicture` and one `TouchZoneRect-<n>` per zone,
no click and no `--allow-ui-effects` needed; a first guess that couldn't be
placed is tried again when the page opens again. Then
how *Detect zones* went, and each step while it runs: *Step 2: finding the
zones of the character's head in a close-up...*, *Checking the zones of ...,
round 1 of 2...*, *Step 5: asking again for 3 zones the close-ups missed, on
the whole character...*; it shows *Taking a picture of the character...* at
once, even when the click left the keyboard focus on `TouchZonesDetect`. When a
request fails it reads *Finding zones stopped at request 4: couldn't ask the
Thinking model (ResponseTruncated). Your 38 zones from before are kept. Try
again when it answers.*: the zones from before and their picture stay (over a
first guess: *The 24 zones of the first guess are kept.*), or, with
none before, *The 5 zones found until then are kept. Press Detect again to find
the rest.*; while it runs over a first guess, the zones it has found replace
their first guesses on the picture and the others stay until it is done), `TouchZonesSent` (what the last detection sent: how many
pictures, how large and what they showed, such as *..., and the whole character
again for the zones the close-ups missed*, or *FIXTURE - NOT AI answered
these.*), `TouchZonesLast` (the zone the last click landed in,
how it was found (*box, traced to the rest pose* when the renderer traced the
touched point back to the rest pose the zones were found in), the other zones it
landed in where zones overlap (*Groin (box), with Left thigh, at ...*), what it
played or that it was resting, and which of the zones Martlet
noticed: *it*, *them* or their names), `TouchZonesNoticed` (what Martlet noticed that waits for a reply,
the plain touch line, *Martlet stopped talking for it.* when a touch stopped
Martlet talking, and when a touch-only reply starts, or that it waits for
your next message because you started talking or typing or Martlet can't reply
now), `TouchZonesNoticedLast` (which reply took the last touches, the short
history line and exactly what the Thinking model was told),
`CharacterPhysicalLast` (the last stroke across the locked character: zones
crossed, pace, passes, seconds and samples on the character; or the last move,
zoom, pan, lock, hide or show as Martlet's touch ledger heard it),
`TouchInterrupt-any`, `TouchInterrupt-intimate` and `TouchInterrupt-never`
(*When you touch Martlet while it talks*: radio buttons whose `selected` state
reads in `ui_snapshot`; choosing one saves `talk-preferences.json`, so it
needs `--allow-ui-effects`),
`TouchZonesSaveState` and each zone's `TouchZoneState-<n>` (its ID, the parts
it follows, for a zone with several areas how many, for a zone that follows the
model's own part *follows the model's own 尾巴 wherever it moves: 21 parts in 6
areas*, *added by you* for a zone the owner added, which *Detect again*
looks for too and keeps where it is when it can't find it, or *special to this
character* for a zone *Detect zones* found as special to the character, and how
the persona's touch temperament feels about it, such as *loves it*), each entry
of its reaction list in play order (`TouchZoneReactionItem-<n>-<k>`: *Blush ·
emote*, *nod · gesture*, *TapHead · motion*, *laugh · sound*, or *F05 · not on
this model* for an entry the model doesn't have, which stays; or
`TouchZoneReactionNone-<n>`: *nothing* for an empty list), and `TouchZonesAddNote` (which zones *Detect zones* looks for:
*Detect zones looks for the hair, eyes, ears, nose, mouth, neck, breasts, upper
arms, forearms, stomach, hips, groin, thighs, calves and feet, and for anything
special to this character, such as animal ears, a tail, wings, a hat or a bow.
Add any other zone here...*). When a detection finds zones special to the
character, `TouchZonesDetection` names them: *Found 27 zones (2 special to this
character: hair bow and tail) at ...*. `TouchZonesDetect` sends the character's
pictures to Thinking; it is disabled only while a detection runs, while the
character is still being read, or when no model can see pictures (never because
the character is hidden). The picture comes from a second renderer that loads
the character off screen (its window *Martlet character picture* sits outside
every screen and is fully transparent), never animates, draws one frame in the
rest pose and closes; the character on the desktop (`SetupCharacterNow`,
`SetupCharacterView`) doesn't change. The desktop log records *Character model
loaded off screen for its touch zones picture* and *Finding touch zones: a ...
picture of the character in its rest pose, drawn off screen*, with *zoomed out
to 0.94x to show the parts drawn past the model's own canvas* when the model
draws past its own canvas, and how many bones and named parts the model gives
(*0 bones and 12 named parts from the model (39 of its 39 parts named in its
DisplayInfo file, so the close-ups hold their parts and boxes that miss their
part move onto it)*). `TouchZonesStop` (shown while it runs; a passive click)
stops it and keeps the zones found until then, `TouchZonesSentView` (*Show the
picture Thinking saw*, a check box) shows the whole character as Thinking saw
it under the boxes, `TouchZonesSentOpen` opens the folder of pictures in
Explorer, `TouchZoneTry-<n>` plays on the character, and
`TouchZonesIntimate` (its value is its label, which names every intimate part,
the breasts and the groin too), `TouchZonesAdd`/`TouchZonesAddKind` (its value
is the zone chosen to add; it offers every zone the model doesn't have yet, and
the zone it adds is marked *added by you*) and each zone's
`TouchZoneOn-`, `TouchZoneName-`, the reaction list's
`TouchZoneReactionAdd-` (*Add a reaction...*: choosing an emote, gesture or
motion with `ui_select` adds it at the end, up to 8; it is off while the list
is full), `TouchZoneReactionEarlier-<n>-<k>` (*‹*: plays entry k one place
earlier), `TouchZoneReactionRemove-<n>-<k>` (*×*) and
`TouchZoneReactionDefaults-` (*Defaults*: fills the list again with what a new
zone gets),
`TouchZoneNotices-` (*Martlet notices*; its checked state reads in `ui_snapshot`), `TouchZoneNarration-` (the owner's optional hint; it shows only while *Martlet notices* is on), `TouchZoneCooldown-`, `TouchZoneBox-`
(under *More*: `TouchZoneMore-<n>` is a passive click that shows or hides the
zone's box and Delete, and reads *Less* while they show; its value is each area's box, left, top, width and height in percent, the areas
separated by `|`; for a zone that follows the model's own part, setting a
different box places the zone there as one box again),
`TouchZoneAddArea-` (*Add area*: a box beside the zone's last one; shown while
the zone has fewer than 8 areas and doesn't follow the model's own part),
`TouchZoneRemoveArea-` (*Remove area*: takes the last area away; shown while the
zone has more than one),
`TouchZoneDelete-` and its areas on the picture (`TouchZoneRect-<n>` for the
first, `TouchZoneRect-<n>-<k>` for area k from 2, inside
`TouchZonesPicture`) save, so they all (except Stop and More) need `--allow-ui-effects`.
Saving binds the zones to the model again with the probe kept beside the
picture, so a box moved onto a tail gets that tail's areas, and the page then
shows them. `TouchZonesShowOnCharacter` (*Show the zones on the character*, a
check box for this session only; `ui_toggle`, like every check box, needs
`--allow-ui-effects`) draws the zones' areas over the showing character as they
move; `character_zones` reads them, drawn or not.
`ui_snapshot` lists the picture and each area's box on it as custom controls;
with `{"idPrefix":"TouchZoneRect","layout":true}` each box's `bounds` show
where it sits on the picture (`TouchZonesPicture`'s `bounds`), one box per
area of each saved zone.
The picture sits in the zone map, `TouchZonesMap` (a frame that zooms, for
moving and resizing boxes precisely). `TouchZonesZoomIn`, `TouchZonesZoomOut`
and `TouchZonesZoomReset` (*Zoom in*, *Zoom out*, *Reset zoom*) are passive
clicks: they change only how large the map shows the picture, in steps of 1x,
1.5x, 2x, 3x, 4x, 6x and 8x, and save nothing. `TouchZonesZoom` reads how far
it is zoomed in (*Zoom 2x*). Zoomed in, `TouchZonesPicture`'s `bounds` grow
with the zoom while `TouchZonesMap`'s keep the height they have at 1x (the
map grows as wide as the picture or the page), each `TouchZoneRect-<n>` (and
`TouchZoneRect-<n>-<k>`)
grows with the picture, and `ui_scroll` on `TouchZonesMap` reads and moves the
part of the picture that shows (`shows`: left, right, top and bottom in percent
of the picture). The zoom buttons keep the middle of what shows; Ctrl+wheel over
the picture zooms where the pointer is. The page keeps the zoom and the part
shown while it draws again (Add zone, each step of Detect zones) and shows the
whole picture when it opens again or shows another model. A zone added while
zoomed in starts in the middle of the part shown, as large on the screen as
one added at 1x (a fifth of the picture each way).
The Character page lists more than `ui_snapshot`'s 200 controls; read the
section with `{"idPrefix":"TouchZone"}`. With `"layout":true`, each row's text
boxes, choices and buttons in Touch zones, Touch temperament and Emotes and
motions read 32 pixels tall in `bounds`, with their check boxes and labels on
the same centre line. Setting
`MARTLET_TOUCH_ZONES_FIXTURE` to a text file before launching the desktop makes
a FIXTURE - NOT AI stand-in answer every request of *Detect zones* from that
file's zones (JSON about the whole snapshot, as `answer` above; shown in
`TouchZonesVision`, `TouchZonesDetection` and `TouchZonesSent`) after taking the real snapshot,
composing and keeping every picture and probing the showing model's drawables
or bones, so the whole detection runs with no vision request (and *Detect
zones* is on without a model that can see). With it, setting
`MARTLET_TOUCH_ZONES_FIXTURE_FAIL_AT` to a request number (1 for the first)
makes the stand-in fail that request instead of answering, as a model whose
computer stopped answering: *Detect zones* then stops there and keeps the zones
from before (FIXTURE - NOT AI in `TouchZonesDetection`).

At the end of the Touch zones card, *Start over* resets the character's touch
to how a fresh character starts, for the model shown and the active persona
only. `TouchZonesResetLevel` (a choice; its value is the level: *Zone
reactions*, *Zones*, *Touch temperament*, *The character's own changes* or
*Everything*) chooses how much,
and `TouchZonesResetNote` reads what that level clears (fixed text).
`TouchZonesReset` (*Reset...*) opens a confirmation whose question,
`TouchZonesResetQuestion`, lists exactly what is lost (zone names and counts,
the persona's name, a custom temperament's name, the date the temperament was
decided) and what happens after; `ConfirmationNo` (*Cancel*) leaves everything
as it was and `ConfirmationYes` (*Reset*) resets. When the level has nothing to
lose, no question opens. `TouchZonesResetState` reads what the last reset did
(*Reset zones at 11:40 PM. Martlet places a first guess...*), *Nothing was
reset.* after Cancel, or why it couldn't (*Not reset: ...*). *Zone reactions*
gives every zone the reaction a fresh zone gets (its reactions, rest, *Martlet
notices* and own words) and keeps the zones; *Zones* forgets this model's zones,
found and added, with the picture and what the last detection sent, and the
page then places a first guess again; *Touch temperament* forgets the persona's
own temperament and what it uses instead, and the Thinking model decides it
again from the personality when Martlet isn't replying; *Everything* does the
last two. Choosing a level, Reset and its `ConfirmationYes` need
`--allow-ui-effects`.

Companion › Eyes › *Where the face is* (`CompanionTab-Eyes`) reads through `CharacterEyesStatus`
(where the shown model's eyes come from: *From the model's own meshes.*,
*From the model's own eye bones and meshes.*, *Measured with vision at 3:12
PM.* or *Estimated: Martlet guesses where the eyes are from the face...*; while
the character is hidden, the saved measurement or *Show the character to see
where its eyes come from.*; with a measurement, then *The blush goes on the
measured cheeks.* or *The cheeks weren't measured, so the blush uses an
estimate. Measure the face to place it.*), `CharacterEyesProgress` (how measuring goes or
went: each step while it runs, the result or why it failed, such as *Couldn't
ask the Thinking model (...)* or *The Thinking model's eyes didn't pass
Martlet's checks: ...*) and `CharacterEyesNote` (shown only when no model can
see pictures: *Measure the face is off: ...*). `CharacterEyesMeasure` (*Measure
the face*, or *Measure the face again* once a measurement is saved) sends a
close-up of the character's face to Thinking and
`CharacterEyesForget` (*Forget the measurement*) deletes it, so both need
`--allow-ui-effects`; `CharacterEyesPicture` is the close-up Thinking saw with
its numbered boxes (1 to 4 the eyes, 5 and 6 the cheeks, 7 the mouth), and
`CharacterEyesLegend` names each box in its color (*Boxes, left and right as
you see them: 1 left iris, 2 left eye's opening, ...*). `CharacterEyesOpen`
(*Open the pictures and answers*) opens the model's folder in File Explorer,
so it needs `--allow-ui-effects`. Each request is kept beside its picture as
`NN-step.txt` (the instructions, the message and the model's exact answer,
with a FIXTURE header when a stand-in answered), and the desktop log records
*Measuring the eyes (step): the answer: ...*. An answer that copies the old
example boxes from the instructions fails the checks (*These boxes are an
example's numbers...*), and a saved measurement with those boxes loads as absent,
so the model is measured again. The measurement uses the same off-screen still
renderer as *Detect zones*, whose picture now also carries the face anchor (that
renderer never gets the hint, so a new measurement is always relative to
Martlet's own anchor); the
desktop log records *Measuring the eyes: a ... picture of the character in its
rest pose, drawn off screen, with its face ... pixels wide*, each picture sent
and each step. When a new model shows (one with no measurement of its cheeks
saved, which includes one measured before Martlet measured cheeks) and a model that
can see is set up, the desktop measures the model once on its own (once per
model each time Martlet starts, and never again after *Forget the
measurement* until it starts again). The hint goes to the renderer (`eyes`)
after each model load and after each measurement; the desktop log records
*The character's eyes got Martlet's vision measurement; they use ...*. With
the hint, `character_face` shows the cheeks and the mouth where vision saw them
(and, on a Live2D model or a VRM without eye bones, the face's middle on the
measured eye line).
Setting `MARTLET_EYES_FIXTURE` to a text file before launching the desktop
makes a FIXTURE - NOT AI stand-in answer every request of the measurement with
that file's text (JSON about the close-up, as `answer` above; shown in
`CharacterEyesProgress` and `CharacterEyesStatus`, and saved with `by`
`fixture`) after taking the real snapshot and composing and keeping every
picture, so the whole path runs with no vision request (and *Measure the face*
is on without a model that can see).

To drive the visible desktop, start `Martlet.Desktop.exe` yourself in the **same
interactive Windows session** (ideally with a disposable `--data-directory`).
Call `ui_connect` with that process ID (without one it finds the Martlet desktop
running in this Windows session, and says so when none or several run). Its
`ready` says whether the main window is shown, enabled and has its navigation
loaded: right after Martlet starts, the window can show a moment before its
controls are in the UI Automation tree (longer on a busy PC), so call
`ui_connect` again until `ready` is true before you click.
`scripts\Invoke-MartletMcp.ps1 -Desktop` waits for it. A click with `focus`
tries the keyboard focus again for about a second while Windows brings another
window to the front. `ui_snapshot` returns window accessible names,
automation IDs, enabled states, checkbox states, and selected read-only status
fields (a text block's text, or a button's accessible name); it does not dump arbitrary editable fields or credentials.
It returns the first 200 controls; `idPrefix` keeps only those whose automation ID
starts with it (`TouchZone` for Companion › Touch › Touch zones, below the
long emotes list).
`ui_connect` and `ui_snapshot` also return `resources`: how many Window Manager
(USER) and GDI objects the desktop holds (`windowManagerObjects`,
`windowManagerPeak`, `gdiObjects`, `gdiPeak`) of what Windows allows one process
(`windowManagerLimit`, `gdiLimit`, 10,000 each by default), and its windows
(`windows`, also hidden and message-only ones, on `windowThreads` threads) with the
ten largest `windowClasses` (`{class, count}`; GUIDs in class names read `*`, as in
`HwndWrapper[Martlet.Desktop;;*]`). A count that keeps growing is a leak: at the
limit, timers and windows fail with *The current process has used all of its system
allowance of handles for Window Manager objects*. The desktop log has the same
reading as *Window resources: ...* lines (`logs_tail` `contains` `Window resources`):
a minute after start, then once an hour, when a count grew by 1,000 (with the window
classes), a warning at 50%, 75% and 90% of a limit, and one after such an error.
A status text whose details sit in its tooltip (the talk window's `LiveVisionStatus`,
`LivePcAudio`, `LiveChattiness` and `LiveContext`) also returns them as `help`
(its accessible help text). A box you type in (`kind` `ControlType.Edit`, also a
password box) returns its example text as `help`, such as
*https://martlet.example.com/v1* for Share's `OpenAiPublicAddress`: the dimmed text
the empty box shows (`InputHint`), which is also its accessible help text unless
the box has help of its own. It never returns what is typed in the box.
`{"name":"ui_snapshot","arguments":{"layout":true}}` also returns each control's
screen `bounds` (`[x, y, width, height]` in pixels) and, for text controls, the
`textBounds` of their first line of text (geometry only, never the text), so
alignment can be checked: in the talk window, the empty box's hint
`LivePlaceholder` must have the same `bounds` position as the `textBounds` of
text typed into `LiveInput`. Each message bubble (`LiveMessage-*`) is only as
wide as its words: for a one-line message, `bounds` is about 4 pixels wider
than `textBounds` (room for the caret), even under a longer caption or note.
`windowStates` lists each window's `name`, automation `id`, `enabled`, and
whether its frame is `resizable`, `minimizable` and `maximizable`, whether it is
`minimized`, whether it is the `foreground` window (has the focus) and whether
it is `clickThrough` (the mouse passes through it to the window under it,
`WS_EX_TRANSPARENT`: the character overlay while click-through is on), and
`ownedBy`: the name of the Martlet window it belongs to and always stays in
front of (a dialog over its window), or null for a window of its own that
other Martlet windows can cover. `windows` (and `windowStates`) list the windows
front first, in the order Windows stacks them; with `layout` it adds the window's `bounds` and its monitor's `workArea` (the screen
minus the taskbar), both in physical screen pixels. Every Martlet window opens
within that work area at any display scale: no larger than it (minimum sizes
shrink to fit), centered over the window it belongs to (or the main window),
title bar on screen. Dialogs that size to their content (confirmations, the
problem dialog, *Add a voice*, *Add a character*, API keys, host input, a
computer asking to join) are resizable with only Close, scroll when the screen
is shorter than they are, and stay inside the work area as they grow. To check
a higher display scale than this PC uses, set `MARTLET_SIMULATE_DISPLAY_SCALE`
(a percentage, such as `300`) before launching the desktop (for example before
`Invoke-MartletMcp.ps1 -Desktop`): windows then fit a work area shrunk from its
top-left corner as that scale would shrink it, which `workArea` does not show
(at 300% on a 2560 x 1332 work area at 225%, windows stay within
`[0, 0, 1920, 999]`).
Every read-only text box has a Copy button `Copy-<box ID>` (the box's
automation ID, or its `x:Name` when it has none: `Copy-HostRunOutput`,
`Copy-PrepareOutput`, `Copy-SupportReport`, `Copy-LogDetail`) above its
top-right corner (its `bounds` sit above the
box's text and scroll bar, which keep the box's full width), shown only while
the box has text. Snapshots return
its label (*Copy*, or *Copied*/*Couldn't copy* for about three seconds after a
click), never the copied text. A run window's and *Prepare this computer*'s Copy
starts with *Martlet <version>: <title>* (and *SSH target: ...* for Prepare)
and *Status: <status line>*, then a blank line and the output.
`ConfirmationCopy` (confirmation dialogs: version, title and question) and
`HostInputCopy` (install and prerequisites dialogs: version, title, heading,
message and the terms shown; never what was typed) work the same way. Copy
buttons write the clipboard, so they need `--allow-ui-effects`; check the
outcome with `ui_snapshot` and, on the dev machine, `Get-Clipboard`. The
problem dialog (`ProblemDialog`: an unexpected error, or *Martlet couldn't
start*) returns `ProblemHeading`; its report `ProblemText` (exception text and
paths) is not returned, `Copy-ProblemText` copies it, `ProblemClose` is
passive and `ProblemOpenLogs` opens Explorer (`--allow-ui-effects`).
`ui_connect` also attaches to a Martlet that shows only its problem dialog.
Status fields include `VisionNow` (Companion › Vision's *Now* line: *On: Martlet looks at your whole screen now and then (comments: Normal).* by default, or *Off: ...* once turned off; a saved `talk-preferences.json` keeps its choices, and nothing is captured until Start watching), `VisionToggle` (*Turn vision off* in the Now card while vision is on, *Turn vision on* while it is off; clicking it saves `talk-preferences.json`, so it needs `--allow-ui-effects`; the `VisionSource-ActiveWindow`, `-ActiveScreen`, `-Camera` and `-Url` choices report `selected`, `-ActiveScreen` by default), `VisionStatus` (Companion › Vision: whether the Thinking model can see, or has been retired, and the fix), `VisionDisclosure` (Companion › Vision: exactly what vision captures and sends and where, including that what you type or say goes with the newest picture and, for the whole screen, the looks at notifications and flashing taskbar buttons), `VisionGazeStatus` (Companion › Vision › Glances at your screen: the character's usual gaze, such as *The character follows your mouse.*, why Martlet can't decide yet (vision off, a camera, the character hidden, not watching yet) or what the eyes are on now; its `VisionGaze-Mouse` (*Keep its usual gaze*) and `VisionGaze-Martlet` choices save `talk-preferences.json`, so they need `--allow-ui-effects`, and `character_gaze` reads the saved choice as `saved`), `FallbackNow` (Companion › Thinking › If Thinking fails: the saved fallback endpoint and model and whether it has its own key, uses Thinking's or none; never the key), `FallbackKeyStatus` (what the fallback's key box will do; its choices are an option picker, `Picker-Fallback-Off` first, then `Picker-Fallback-<provider>` (`openrouter`, `nvidia-build`, `google-gemini`, `openai`, `ollama`, `custom`; clicking one only shows its details: what it costs, its key and what leaves this PC); the shown provider's fields `FallbackBaseUrl`, `FallbackModel`, `FallbackKey`, `FallbackConsent` and its `FallbackSave` button, and Off's `FallbackOff`, write settings or a key, so they need `--allow-ui-effects`; `logs_tail` shows each use as *Thinking failed (...) ... the Thinking fallback ... answered instead*, and a rate-limited glance shows in `LiveVisionStatus`'s `help` as *Last look 10:17 PM: the provider is limiting requests, so Martlet looks again in 1 minute.*), `RepliesNow` (Companion › Replies: that Martlet asks for replies of one or two sentences, the max reply length ceiling in effect, 4096 tokens including any hidden thinking on a Chat Completions or paired-host Ollama route unless set, whether Thinking steps are off (the default) or on, and the other saved settings), `RepliesThinking` (Companion › Replies › Thinking steps: *Off*, the default, or *On*; choosing one with `ui_select` saves it, so it needs `--allow-ui-effects`) and `RepliesThinkingStatus` (how the Thinking route takes it: *Used by Ollama on this PC.*, *Depends on the model at ...* for servers where it depends on the model, or not used on the OpenAI route), `SetupCloudHint-Thinking` (a retired-model warning, and what the chosen provider needs that its summary doesn't say: Google Gemini's free-key steps, that any exact OpenRouter model ID works, a custom server's address; hidden when there is nothing to add, since `PickerSummary-CloudThinking` names the recommended model), `SetupJobNow-Thinking`, `SetupJobNow-Voice` and `SetupJobNow-Listening` (the job's *Now* line: where it runs and the model, such as *Ollama on this PC: gemma4:12b*), `SetupJobNetwork-Thinking`, `-Voice` and `-Listening` (shown when a host does the job for your Martlet network: *Your Martlet network does thinking on diva-host, as chosen on desktop-diva, and this PC switches to it as soon as it can: pair diva-host with this PC first.*, or *Your other computers use this PC for thinking, through diva-host.*; a computer that hasn't chosen yet then selects `Place-Thinking-Computer` and its *Now* line reads *Not set up on this PC yet.*), `SetupCloudKeyStatus-Thinking`, `-Voice` and `-Listening` (under *A cloud provider*, what the key field does for the chosen provider: keep the saved key, use again *Your OpenRouter key from before*, set aside when the job left that provider, or ask for one; never the key; `SetupCloudSave-<page>` and `SetupUseLocalThinking` save the route, so they need `--allow-ui-effects`, and keys set aside never block them), `SetupOldKey-Thinking-<n>`, `-Voice-<n>` and `-Listening-<n>` (the page's *Keys from before*, shown only while the job has a key Martlet set aside when it stopped using it, newest first: *Your OpenRouter key* or *The pairing key for diva-host*; never the key; each `SetupOldKeyRemove-<page>-<n>` button reads *Remove your OpenRouter key* and first asks `OldKeyRemoveQuestion`, *Remove your OpenRouter key from this PC? You can't undo this.*, where `ConfirmationYes` deletes the key from Windows Credential Manager, so it needs `--allow-ui-effects`; `Invoke-MartletMcp.ps1 -LabCredentials` keeps such keys in the disposable data directory instead), `SetupLocalRecommendation` (the local Ollama model recommended for this PC: the fastest, Gemma 4 E2B, on every graphics card; its `help` and `Help-LocalRecommendation` add this PC's graphics card and the largest model that fits it as the smarter, slower choice, each leaving about 5 GB for a game and Martlet's character), `Picker-OllamaModel-<model>` (an Ollama model in the list: clicking it only shows its details, with *recommended*, *smartest that fits*, *from the catalog* (up to three models from Martlet's model catalog that fit this PC's card, by their install name, such as `hf.co/ggml-org/Qwen3.8-27B-GGUF:Q4_K_M`) or *in use*; `PickerFacts-OllamaModel-<model>` reads whether it fits this card, its graphics memory, how soon it answers and whether it hears or gets the transcript, and for a model the catalog knows what it takes, how smart it is and where it comes from), `AdvisorStep`, `AdvisorSummary` and `AdvisorChoice-<n>` (the setup advisor that Home's `OpenSetupAdvisor` opens: which step it shows, its plan's summary and each role's pick and status, such as *Speech-to-text: Parakeet speech recognition (Available)*; `GoalFastest` and the other goals, `AdvisorNext`, `AdvisorBack` and `AdvisorClose` only change what it shows), `SetupOllamaStatus` (whether Ollama is installed or running and which models it has, read over loopback when the Thinking tab opens, and which one Thinking uses), `SetupLocalModelTest` (Thinking › This PC: the last *Test model* result for the model in the box, or that it isn't tested yet; a model that doesn't fit in the free graphics memory says so and names a smaller one), `AppUpdateStatus` (Settings › App updates: the installed version, the check schedule and the last check or download result), `OwnHostUpdateStatus` (Settings › App updates, only on a PC running its own host service: where keeping it on this app's version stands), `AppCurrentVersion` (Settings › App updates: always-visible *Current version: Martlet x.y.z*). On Companion › Voice › Voice engine, `VoiceEngineUse-<engine key>` under This PC asks one confirmation (what it installs, the engine it replaces and its model's licence; installing Docker Desktop still asks for its own terms) and then sets up and switches in a run window, so it needs `--allow-ui-effects`. `SetupTestLocalModel` (Thinking › This PC's *Test model*) starts Ollama if needed, loads the model in the box and sends it one short loopback chat request in a run window, so it needs `--allow-ui-effects` too; read the outcome from `HostRunStatus` and `SetupLocalModelTest`. `SetupUseLocalThinking` (*Use Ollama on this PC*, `--allow-ui-effects`) gets the model in `SetupLocalModel` ready before Thinking switches: for a model Ollama doesn't have it first asks `LocalModelDownloadQuestion` (the tag, its size when Martlet knows it and what Thinking keeps using until then; `ConfirmationYes` downloads, `ConfirmationNo` logs *Status: Thinking didn't change.*), then a run window titled *Switch Thinking to <model>* downloads (when needed) and loads it, ending with `HostRunStatus` *<model> is loaded (n s). Thinking switches to it now.*, and only then does `SetupOllamaStatus` say *Thinking uses <model>*. An open talk window follows any saved job change between replies and logs *The open conversation follows the changed setup between replies: Llm ChatCompletions <model>, ...* (`logs_tail` `contains` `open conversation follows`). A run window (`HostRunWindow`, titled `Martlet - <run>`) returns its status line as `HostRunStatus` (for example *Waiting for Docker Desktop to start...* or why it stopped); its output (`HostRunOutput`, which can show a one-use pairing code) is not returned, so read it with `logs_tail` `host-runs`, which also records each status change. `HostRunHide` (*Hide*, also Esc and the window's close button) only hides a running run, which keeps going in Background tasks, and closes the window once the run has finished; `HostRunHideHint` says so while it runs. `HostRunCancel` (*Cancel task...*) asks first (`CancelTaskQuestion`; `ConfirmationYes` cancels, `ConfirmationNo` keeps it running), so it needs `--allow-ui-effects`. A fresh data directory needs no saved settings first: pairing, setting up this PC's host service and a voice engine's setup all work before Setup. Setting `DOCKER_HOST` (for example to a local test named pipe) before launching the desktop points its Docker checks away from the real engine. `ui_click` invokes a control by automation ID and `ui_select` selects a named combo-box
option. By default only passive navigation and
diagnostics controls can be clicked. `ui_click` with `"focus": true` gives the
control the keyboard focus first, as a mouse click does (its window comes to
the front when Windows lets it): use it to check a page that updates while a
button it just started still has the focus, such as *Detect zones*. `ui_scroll`
reads or scrolls a control that scrolls, through UI Automation's Scroll
pattern: a page, such as the open Companion page (`CompanionScroll`: its
`bounds` height divided by the part that `shows`, `bottom` less `top`, gives the
page's length), or Companion › Touch's zone map (`TouchZonesMap`) when it is
zoomed in. `horizontal` and `vertical` (0 to 100, each optional) say how far
along to scroll it; without them it only reads. It returns how far along it is
each way (-1 when it can't scroll that way) and which part of its content shows
(`shows`: `left`, `right`, `top` and `bottom` in percent of the content), plus
its `bounds`. Scrolling changes only what shows, so `ui_scroll` needs no
`--allow-ui-effects`. The main window is split into pages, and a
page's controls are only visible after you open it: click `NavHome`,
`NavDevices`, `NavShare`, `NavCompanion`, `NavConversations`, `NavCreations`, `NavTasks`, `NavThinkingRequests`, `NavDiagnostics` or `NavSettings` first (for example
`NavCompanion` before `CompanionTab-Listening`). Many pages keep their longer
explanations behind a small round *?* (`Help-<id>`, such as `Help-CheckInsAbout`; Listening, Voice, People,
Singing, *Microphone and speakers* and *Voice ID* have, for example, `Help-ListeningRecognizer`,
`Help-VoicesAbout`, `Help-ElevenLabsConsent`, `Help-PeopleRecognize`, `Help-SingingAbout`,
`Help-AudioSetupIntro` and `Help-VoiceIdAbout`; Tools, Smart home, Discord, Messaging and App guides have `Help-ToolsServers`,
`Help-Terminal`, `Help-SmartHomeAddress`, `Help-DiscordSetup`, `Help-TelegramSetup`, `Help-WhatsAppSetup` and `Help-AppGuidesAbout`,
the *MCP servers* and *MCP directory* windows `Help-McpConfigAbout` and `Help-McpDirectoryAbout`, and *Outside access*
`Help-OutsideAccessAbout`; Vision, Reading and Picture making have `Help-VisionChattiness`,
`Help-VisionAddress`, `Help-VisionGaze`, `Help-VisionScreenSummary`, `Help-Reading`, `Help-ReadingModels-<place>`,
`Help-Pool-reading-Intro`, `Help-PicturesAsk`, `Help-Pool-pictures-Intro`, `Help-PicturesRole-<i>`, `Help-PicturesComfy-<i>` and
`Help-PicturesWorkflowFile-<i>`):
`ui_snapshot` returns its name as `value` (*About check-ins*) and the whole
explanation as `help`, and `ui_click` on it only opens a small card with that
text (passive, no `--allow-ui-effects`). While the card is open, `ui_snapshot`
also lists its text as `Help-<id>-Card` (`value`: the explanation) and, when the
explanation has a wiki page, its *Read more in the wiki* link as `Help-<id>-Wiki`
(`value`: *Read more in the wiki*; `help`: the wiki address, such as
`https://github.com/throndir2/Martlet/wiki/Thinking#check-ins`). Read the
address from `help`: `ui_click` on `Help-<id>-Wiki` opens the browser, so it
needs `--allow-ui-effects`. An explanation line that shows only its
first sentence before its *?* returns that sentence as `value` and all of the
text as `help`. `ui_long_texts` lists the texts that show now with more than one
sentence (text blocks and check box or radio button labels): each one's
`window`, `id` (or `near`, the nearest container's automation ID), `sentences`
and `text`, and `count` for all of them (at most 200 are listed). Martlet 1.0
shows one short sentence and puts the rest behind a *?* with a wiki link, so a
page that follows the rule lists none. It leaves out lists, tables, trees, text
boxes, help cards, tooltips and controls whose automation ID `ui_snapshot` does
not read, so the owner's own content does not show; still, use it on a
disposable data directory. It changes nothing and needs no `--allow-ui-effects`. Settings most people leave alone sit in sections you open and
close (`Fold-<id>`, such as `Fold-CheckInMore-emotes`): `ui_snapshot` returns
`expanded` (true or false), their controls show in `ui_snapshot` only while the
section is open, and `ui_click` opens or closes it (passive). A section stays
open or closed while Martlet runs, also when its page is built again. Settings › Tools'
`OpenTroubleshooting` opens Troubleshooting: `SupportReport` returns the status
report (each check's state and remedy, and the last conversation activity),
`SupportRefresh` runs the read-only status checks again and `SupportClose`
closes it. On a fresh data directory, the welcome wizard shows
([WELCOME_WIZARD.md](../WELCOME_WIZARD.md)): `TourSkip` dismisses it, and `TourBegin`
and `TourBack` step through it. Step 1's `WizardNewNetwork` and
`WizardJoinNetwork` save the device role, so they need `--allow-ui-effects`;
`WizardJoinNetwork` also looks for Martlet on the local network, and
`WizardScanAgain` (passive) looks again: `WizardScanStatus` says what answered
and each `WizardFound-<n>` reads *name (address): Martlet version, with hosts*.
`WizardConnect-<n>` (*Join*, `--allow-ui-effects`) opens *Add a computer*
already asking that computer (`NearbyNumber` shows the check number), and
`WizardJoinManual` (passive) opens *Add a computer* for an address and code.
`TourHost` makes this a host PC and closes the wizard on the host dashboard.
Step 2 reads `WizardSpecRow-Gpu`, `-Vram` (with the memory in use when
nvidia-smi answers), `-Ram`, `-Cpu` and `WizardSpecs` (the whole line, for
example *NVIDIA GeForce RTX 2070 SUPER (Nvidia, 8 GB graphics memory) · 32 GB
memory · 24 processor threads*); `WizardSpecsNext` is passive. Step 3 asks
three questions. Their answers are radio buttons that only change the
selection (passive, `ui_click`): `WizardGamesYes` and `WizardGamesNo` (Martlet
selects Yes when it finds a game library on this PC; `WizardGamesHint` says
which it found), `WizardOnlineNever`, `WizardOnlineBackup` (the default) and
`WizardOnlineYes`, and `WizardQualityQuick`, `WizardQualityBalanced` (the
default) and `WizardQualitySmarter`; each reads `selected`.
`WizardQuestionsNext` (*Show my setup*, `--allow-ui-effects`) saves the
answers in `recommendation-preferences.json` (this PC's games answer by its
device ID; `recommended_setup_status` with the same data directory reads them
back under `preferences`) and shows the placement engine's suggestion planned
with them: `WizardPlanSummary` (the three answers and whether Thinking goes
online), `WizardPlanItem-Thinking`, `-Voice`,
`-Listening` and `-LipSync` (what, where, *Uses 64% graphics memory, 5% memory,
6% processor* and why, or why it's left out; a part that grows while it works
reads *Uses 31-35% graphics memory*: what it usually holds, then the most),
`WizardJoinSuggestion-<n>` after
joining a network, and `WizardPlanTotals`. `WizardAccept` (*Use these
suggestions*) needs `--allow-ui-effects`: when Thinking goes to NVIDIA Build and
none is set up it shows the key step (`WizardKeyIntro`, `WizardKeySteps`,
`WizardKeyStatus`, readable; never the key), otherwise it closes the wizard on
Home and asks `DefaultSetupQuestion`. `WizardKeyOpen` opens the browser and
`WizardKeySave` saves a key (both `--allow-ui-effects`; never type a real key);
`WizardKeySkip` goes on to `DefaultSetupQuestion` without one.
`DefaultSetupQuestion` (readable) lists what *Set it all up for me* sets up on
this PC, lip-sync and the downloads; `ConfirmationYes` installs and downloads,
so it needs `--allow-ui-effects` (as do `HealthFix-thinking-setup-defaults` on
Home and `HealthFix-listening-setup-defaults` / `HealthFix-voice-setup-defaults`
for one job). `ConfirmationNo` changes nothing. When Thinking would run on this PC and a
model app on it serves a chat model, the question also has the checkbox
`ConfirmationOption` (*Use models your apps already run*, readable; ticked by
default). While it is ticked, `DefaultSetupQuestion` names the served model
that Thinking uses (*Thinking: qwen3-32b in LM Studio on this PC (about 22 GB
on the graphics card). You already run it there, so nothing downloads. ...*), or says
why none fits (*Your apps run qwen3-32b in LM Studio, but none fits the
graphics card beside the voice, so Martlet uses its own model.*). Changing it
saves `recommended-setup.json` and asks again, so it needs
`--allow-ui-effects`. Companion's side list items (`CompanionTab-<Page>`,
for example `CompanionTab-People`) and `OpenPeople` (on Listening) are passive
navigation too. People shows `PeopleNow` (its Now line: *Voice recognition on: 3 voices known, 2 with a name.*,
*Off: Martlet doesn't check who is talking.*) and `PeopleNowProblem` (the
installation lacks the voice recognition files, the voices couldn't be read, or
*Voice recognition on this PC stopped working: ... Martlet loads it again and
tries at 15:05.*), `PeopleRecognitionWhere` (where the last voice was recognized
and how long it took: *Last voice recognized by this PC's processor in 149 ms
(3 s of speech, at 15:05).*, or what the Voice recognition list means),
`PeopleRecognitionCheck` (*Check it now*: it sends a test tone through the
list, so it needs `--allow-ui-effects`) and `PeopleRecognitionCheckResult`
(*Checked at 15:05: this PC's processor recognized the test tone in 149 ms.*,
or *... nothing could recognize the test tone.* with the reason). *Where it
runs* is the shared pool list (`Pool-voice-recognition-Summary`,
`-Member-<i>` with each member's state: *ready; about 47 ms for each second of
speech*, *stopped working: ...*, *runs Voice recognition; last answered in 45
ms* or *left out until 15:06 (...); this PC recognizes instead*; `-On-<i>`,
`-AddThisPc`, `-AddHost-<host>` and the other list controls). While this PC's
engine has failed, Home shows `HealthIssue-voice-recognition` (*Warning: Voice
recognition stopped working on this PC. ...*) with
`HealthFix-voice-recognition-retry` (*Try again now*: loads the engine again,
so it needs `--allow-ui-effects`) and `HealthOpen-voice-recognition-open-people`. `PeopleSyncStatus`,
`PeopleVoiceCount` and `PeopleLinkStatus` (counts only: *1 of 3 voices is linked to people's accounts. Your
account has 1 voice. ...*, or before this PC knows its account whether a voice is marked as yours), and Listening › This PC shows its speech recognizers as an
option picker: `Picker-Listening-<model ID>` for each Parakeet model (inside
Martlet, no Docker), `Picker-Listening-whisper-gpu` and
`Picker-Listening-whisper-cpu`. Each row's `PickerFacts-Listening-<key>` reads
where it runs, how soon the transcript comes and its languages
(*Processor (in Martlet) · 0.09 s to transcript · English*); the shown one's
details read every fact (`PickerFact-Listening-<fact>`: languages, accuracy,
streaming, memory, processor threads, download, *On this PC*),
`PickerState-Listening` (*Downloaded.* or *Downloads once: 477 MB.*) and, while
a Parakeet model downloads, `ListenParakeetModelState-<model ID>`
(*Downloading: 46% of 477 MB...*); snapshots return
these status texts, as does the talk window's `LiveStatus` (the line under "Martlet": what it is doing, or why the last reply failed, naming the job that failed: *Martlet couldn't speak: ...* for the voice, and *Your Martlet host <ID> didn't answer ...* when the job runs on a paired host). Each voice's controls are numbered by voice: its name chips
`PeopleNameShow-3-<i>` (make name *i* the one shown; the first is the one
shown) and `PeopleNameRemove-3-<i>`, the box `PeopleAddName-3` with
`PeopleAddNameButton-3` (Add; Enter also adds), `PeopleClip-3-<i>` (plays
clip *i*, newest first), `PeopleLink-3` (*This voice is <name>*: ticked links
voice 3 to the signed-in account, unticked unlinks it; moving a voice linked to
someone else's account asks one confirmation, `ConfirmationYes`; its
accessible name is *This voice is mine*, never an account name),
`PeopleMergeTarget-3` with `PeopleMerge-3` (enabled once a voice is chosen)
and `PeopleForget-3`; names save at once, then sync. `PeopleClips-3` reads
*Hear them (N):* while voice 3 has clips (a passive value). Like
the main choice's radio buttons `PeopleRecognize` (*Recognize voices in conversations*, the default; a shared setting)
and `PeopleRecognizeOff` (*Off*; saved voices are kept), `PeopleKeepClips`
(ticked by default; the shared `voice-clips` setting, the same on every computer; unticking deletes every clip),
`PeopleSync`, `PeopleForgetAll` and `SetupListenParakeet-<model ID>` (*Download
and use* asks one confirmation, `ConfirmationYes`, then downloads that model
and switches Listening to it; *Use it* switches to a downloaded model at once),
they
change data or download and need `--allow-ui-effects` (People has no sharing
switch: the list always syncs with your paired hosts, whatever `ClusterSync`
says, and `PeopleSyncStatus` shows the last sync). While Listening uses a paired
host or OpenAI, Listening's *Now* card has `SetupJobStandIn-Listening` (a passive
value): what hears you on this PC's processor when that route can't (*If OpenAI
can't hear you, Parakeet TDT 110M (English) hears you on this PC's processor
instead. Nothing is sent anywhere.*), or *... Martlet can't either. Download
Parakeet TDT 110M (English) and this PC's processor hears you instead.* with
`SetupListenStandInDownload`, which asks one confirmation (`ConfirmationYes`),
downloads that model and leaves Listening as it is, so it needs
`--allow-ui-effects`. A turn the stand-in heard shows in `logs_tail` as
*Transcription failed (outcome Failed, provider ...; Parakeet &lt;model&gt; on
this PC heard it instead in N ms)*, then, for 60 s while the route isn't asked,
*Listening: Parakeet &lt;model&gt; on this PC heard it instead in N ms, without
asking Listening's own route ...*; its *Reply latency* line ends with
*speech-to-text &lt;model&gt; on this PC, standing in for &lt;route model&gt;*,
and Home's `HealthIssue-failed-listening` keeps the route's failure until it
answers again. Each voice's
`PeopleMemories-3` (*Memories*: what Martlet remembers about them) only opens Memory
showing that voice's facts, so it is a passive click; read `MemoryFactStatus`
there (*Showing N.*) or `memory_status` for whose facts are. On Devices, `Node-<id>`
selects a device on the map (`Node-this-pc`, `Node-host:<host ID>`,
`Node-pc:<device ID>` for another Martlet computer that runs no host service,
`Node-cloud:<server>`, `Node-add`, `Node-missing:brain`) and
`CoverageShow-<job>` selects the device doing a job; both only show details, so
they are passive clicks, as are the `DeviceFactsSection`, `DeviceRolesSection`
and `DeviceReachSection` expanders. Each `Node-<id>` also returns the device's
card as text: its name, subtitle, status and what it runs (for example
`Imouto, imouto-host. Connected. Runs: Martlet companion, Martlet host, Listening`,
`Nahida, Nahida-host. Connected. Runs: Martlet host` for a NAS that runs only a host service,
or `Diva, diva-host. Connected. Runs: Martlet host PC, Speaking, Lip-sync`),
so one snapshot shows the whole map. Every computer is named the same way on
every system ([one device per computer](../NETWORK.md#who-is-connected)): the
name it gives itself, from its Martlet app or its host's machine report, with
the host ID (or, without a host service, the device ID) as the subtitle. The
map places devices in a grid of four columns around This PC: one on each side
in This PC's row and four in every row above and below, nearest places first,
cloud services on the left. A narrow map has the two inner columns only, two
devices in every row above and below This PC, so its cards stay large enough to
read. Connections run only through the gaps between cards, and cards never
overlap (with `layout: true`, `ui_snapshot` returns each `Node-<id>`'s screen
bounds to check it). From 19 devices on, the cards are compact: name, computer
and status instead of jobs. The map draws up to 33 devices; the rest fold into
a `Node-more:computers` (or `Node-more:services`, or `Node-more:devices` when
both fold) card, *89 more computers* with how many need attention, whose click
opens the list.
To check the page with many devices without real ones, set
`MARTLET_SIMULATE_DEVICES` to a number (1-500) before launching the desktop
(`-Desktop` passes the environment on): Martlet lists that many FIXTURE
computers, SIM-001, SIM-002 and so on, and contacts none of them. Most are
members of your Martlet network (every fourth idle); every third is a host PC
that runs `sim-NNN-host` (pair a host with that ID in the disposable
`hosts.json`, for example on `192.0.2.x`, to show it as one device with its
computer); every seventh uses your hosts outside the network and every eleventh
asks to join. They get made-up jobs (FIXTURE: none runs anywhere), so their
cards have one or two rows of chips. The desktop log says so at start
(*Devices: 120 simulated computers from MARTLET_SIMULATE_DEVICES (FIXTURE, not
real computers)*).
A device that Martlet changes now (a recommended setup applied from any of
your computers, a host role this PC changes, a host PC running a role command
from another computer, this PC following a plan change) shows the status
*Configuring: <step>* on its card and in `SelectedDeviceHealth`, for example
*Configuring: Installing Chatterbox Turbo (2 of 4)*
([Configuring](../CLUSTER.md#configuring)).
`DevicesViewMap` and `DevicesViewList` switch
between the map and the list (passive); the list shows by itself once the map
can't fit every device. The list (`DeviceList`, whose value is the selected
row) shows every device as one compact `Node-<id>` row (status, name, computer,
status text and up to three jobs) under the headings `DeviceGroup-attention`,
`DeviceGroup-computers` and `DeviceGroup-cloud` (values such as *Needs
attention: 2 devices*; This PC first in its group, the others by name; Add a
computer last). A row's `ui_click` only selects it and shows its details
(passive). The list is one Tab stop: with a row focused (`ui_click` with
`focus: true`), `ui_type` `keys` `up` and `down` move the selection. A list drawn
again (a device changed) keeps its place (`ui_scroll` `DeviceList`). It has
`DeviceFilter-all`,
`-attention`, `-hosts`, `-computers` and `-cloud` pills (passive; each returns
its count) and a `DeviceSearch` box (`ui_set_text`, `--allow-ui-effects`).
`DevicesSummary` returns *53 devices, 2 need attention. Select one to see
details.* and `DeviceListStatus` *Showing 12 of 53 devices.* (or *No device
matches "gpu".*). Martlet remembers up to 64 paired hosts.

The selected computer's **Resources** section (`DeviceResources`, for This PC
and paired hosts) shows how much of the device each job takes, from the
placement engine's measure of today's setup (`PlacementEngine.Measure` over the
footprint catalog). `DeviceSpecs` returns its hardware (*NVIDIA GeForce RTX
5090 (32 GB) · 64 GB memory · 32 processor threads*), one
`DeviceResource-<vram|ram|cpu|disk>` per resource it reported (*Graphics
memory: 14 of 32 GB planned (44%), 15 GB free for Martlet.*; This PC's memory
adds *; in use now: 9.5 GB (59%).*, read live; hosts report no live use yet). A
resource whose jobs grow while they work shows a range: what they usually hold,
then the most they take (*Graphics memory: 11-14 of 32 GB planned (34-44%), ...*).
When the usual amount fits but the most does not, the bar is tight (*..., tight:
at their busiest the jobs can need 2 GB more than it can give, and slow down or
fail.*); when even the usual amount does not fit, it is over (*..., 1-3 GB more
than it can give.*). Hovering a bar (or its line) shows its breakdown, and each
`DeviceResource-<key>` returns that breakdown as its `help`, one line each: the
heading (*Graphics memory (12 GB)*), a line per job in segment order
(*Singing (ACE-Step + SoulX): 5-7.2 GB (42-60%)*), then *Free for Martlet: 1.1 GB
(9%)* (or *More than it can give: ...* when it is tight or over), *Kept for the
system: 1.2 GB* when Martlet leaves room for the system, and *In use now: 27 GB
(84%)* where the device reports it. Hovering one job's segment shows the same
breakdown with that job in bold. One
`DeviceShare-<option>` per job (*Deep thinking (Gemma 4 12B): 16-25% graphics
memory, 3% memory, 6% processor.*; one number when the job does not grow),
`DeviceHeadroom` (one sentence: *Left free: ...*, then *; tight on graphics memory, ...* or
*; planned to use more ... than it has.*) and
`DeviceAlsoFits-<n>` from `PlacementEngine.Afford` (*Room for another Deep
thinking model (Gemma 4 12B) here.*). The **What your computers can run** card
(`CapacityCard`) returns `CapacityCoverage` (*On your computers: Thinking
(gpu-box); online: Voice (OpenAI voice); not set up: Singing, Picture making.*),
`CapacityTotals` (*Totals across 2 computers: 32 GB graphics memory, 80 GB
memory, ...*) and `CapacityFits` (*Your computers could also run 2 more Thinking
models (Gemma 4 E4B) and another Deep thinking model (Gemma 4 12B).*). All are
read-only. The **Cloud providers and servers** card (`ExternalEndpointsCard`,
`MainWindow.ExternalEndpoints.cs`) lists every endpoint outside this PC and the
paired computers that a saved choice sends work to, from the production
`ExternalEndpoints` (the same as `external_endpoints_status`; a host PC leaves
out its saved Thinking, Listening and Speaking routes and the fallback).
`ExternalEndpointsStatus` returns the count (*2 cloud providers and 1 server on
your network. Each line says what Martlet uses it for.*, or *None. Martlet sends
work only to this PC and your own computers.*), and one
`ExternalEndpoint-<host[-port]>` per endpoint returns its name and line
(*OpenRouter. Cloud provider at https://openrouter.ai. Used for Thinking
(google/gemma-4-26b-a4b-it); Thinking pool (x-ai/grok-4.3, turned off).*).
Addresses are scheme, host and port only; never a path or a key.

`SelectedDevice` and `SelectedDeviceHealth`
return the selected device's name and status. When a paired host is older
than this PC, its status *Update available* is a button,
`SelectedDeviceHealthAction` (returned: its status and what it does, for
example *Update available: Update to Martlet 0.40.0*); clicking it runs the
same update as `NodeAction-UpdateHost`, so it needs `--allow-ui-effects`. For a
paired host Martlet manages (this PC's host service, one over SSH, or one whose
own Martlet runs its commands), `SelectedDeviceOutside`
(in *Details*) returns its outside access in counts and choices only (*2 outside
addresses; pairing codes from outside home refused; every connection treated as
outside home.*, with *Outside access paused: sign-in is off; sign-in needed first
(signin.not_set_up)* while it is, and *also listening on 100.101.102.103:9443
(overlay network)* once a native host listens on its Tailscale address), and `NodeAction-OutsideAccess` opens the *Outside access* dialog
(`HostInput-addresses`, `HostInput-allowCodes`, `HostInput-treatAll`, on by
default for Docker hosts; while sign-in isn't usable on the host its note
`OutsideAccessBlockedReason` returns why in one sentence, starting *Outside access is
paused because* when the host has outside addresses, ending with the reason code, the
outsideAccessBlockedReason `signin.not_set_up` or `signin.no_allowed_identity`,
and `OutsideAccessSetUpSignIn` opens the host's sign-in settings (a passive click);
`HostInputCancel` closes it, `HostInputOk` runs
`martlet-host exposure` and restarts the host's gateway, so it needs
`--allow-ui-effects`). For a
paired host, `SelectedDeviceRelease` (in *Details*) returns its Martlet release
as this PC knows it, kept current by the release every host announces on each
network sync (`0.22.0, up to date`, `Needs update from 0.21.0 to 0.22.0`), and
`SelectedDeviceUpdate` the note on what this PC last did to update it (for
example *Asked Martlet on gpu-pc to update to 0.22.0 ...*, *Waiting to update
to Martlet 0.22.0: that host is busy (...)* or *Another update of that host was
already running (...)*, then *Updated to Martlet 0.22.0 (seen at 9:41 PM).* once
the host announces it, a check finds it current or another route of this
Martlet updated it). `SelectedDeviceSharedGpu` (shown only then) warns that the
computer runs on Windows and its voice engine shares the graphics card with its
other roles (the same wording as `SpeakingEngineSharedGpu`). Each row title
`DeviceComponent-<part>` (`job-Llm`, `job-Stt`, `job-Tts`, `lipsync`,
`character`, `audio`, `host-service`, `host`, `users`, `member`, `role-<role>`)
returns the job's name, and its detail line `DeviceComponentDetail-<part>`
returns the row's text. Another Martlet computer's `member` row (*Martlet
companion*, *Martlet host PC*, or *Martlet app* for one on an older Martlet)
gives its device ID, what it is, where it stands with your network and where it
was last active, for example `desktop-imouto. Conversations, its microphone and
speakers; it runs a host service too (imouto-host). In your Martlet network.
Active now on diva-host.`. A paired host's `users` row (*Computers using it*) lists
the computers paired with it as the host reports them, for example
`DeviceComponentDetail-users`: `IMOUTO (desktop-imouto), active now; This PC,
active now.`; on this PC's own host service, `DeviceComponentDetail-host-service`
reads `Paired as diva-host, used by IMOUTO (desktop-imouto), active now.` (or *Paired as diva-host, and no
other computer uses it yet.*). Job owners are `ThinkingOwner`, `ListeningOwner`, `SpeakingOwner`
and `LipSyncOwner`, device commands `NodeAction-<action>`
(`NodeAction-InstallRole-<role>`, `NodeAction-ChangeRole-<role>` (*Change ... settings*, on the row of a role the host
runs: its dialog shows what the role runs with now, so it needs `--allow-ui-effects`) and `NodeAction-RemoveRole-<role>` for host
roles; on another of your computers that said what it is, `NodeAction-MakeHostPc` (*Make it a host PC*) or
`NodeAction-MakeCompanionPc` (*Make it a companion PC*, or *Keep it a companion PC* while an ask to become a host PC waits),
which switch that computer, so they need `--allow-ui-effects` and then `ConfirmationYes`; while the ask waits,
its `DeviceComponentDetail-member` ends with *Asked by this PC at ... to become a host PC: it switches the next time
Martlet there syncs its settings ...*; once it has switched, the desktop log and status line say *IMOUTO is a host PC now,
as you asked.*, and a host PC with no host service yet reads *Host PC; its host service isn't set up yet, so it does no work for your
other computers until someone at it chooses Set up host service on its Home ...* there instead of the host service it runs;
Settings › What this PC is for lists the same computers of your network: `Help-OtherRoles` (the "?" beside *Your other computers*), `OtherRolesStatus` (what the list offers, or why it
is empty or can't switch them), `OtherRole-<device ID>` (*IMOUTO (desktop-imouto). Companion PC that also runs a host service
(imouto-host). Active now on diva-host.*, with the waiting ask) and `OtherRoleSwitch-<device ID>`, the same button, which
needs `--allow-ui-effects`), and Settings for all devices holds `CheckHosts`, `ClusterSync` (checked by
default; unticking it needs `--allow-ui-effects` and saves `off`),
`ClusterStatus` (returned as text), `SettingsSyncStatus` (text: how many
settings are shared, on how many hosts they are the same, when checked and
what was last taken from another computer), `MemorySyncStatus` (text: how many
facts Martlet remembers in each memory space (*42 yours, 3 household*), on how
many hosts they are the same, when checked, how many were taken from or
forgotten because of other computers, hosts that don't know this PC's account
yet, or why it waits: no host paired, the switch or memory off, the store in
use; never a fact),
`SettingsSyncWaiting` (text, shown
only when this PC can't follow a setting yet: which, and why),
`SettingsSyncClaim` (*Use this PC's settings on all my computers*; it changes
every computer's settings, so it needs `--allow-ui-effects` and then
`ConfirmationYes`) and `RoleSetup-<role>` for jobs nobody does.
`Node-add` (*Add a computer*) shows only the ways to add one, each a whole
clickable card named for what it does, with its line as help text:
`NodeAction-AddComputer` (*Add a computer*, the highlighted first card),
`NodeAction-PrepareComputer` (*Prepare a Linux computer*) and, on a companion,
`NodeAction-HostThisPc` (*Run host services on this PC*; it sets up this PC's
host service, so it needs `--allow-ui-effects`). The + beside the title,
`SelectedDeviceAdd`, opens the same *Add a computer* wizard as
`NodeAction-AddComputer` and the page's `AddComputer`, so all three are
passive clicks.
In *Prepare this computer* (`PrepareHostWindow`), each GPU whose power limit
can change has a `PreparePower-<index>` slider (watts), its chosen limit
`PreparePowerValue-<index>` (*300 W*) and its limits
`PreparePowerDetail-<index>` (*Now 370 W, default 370 W, allowed 100-450 W.*);
all three are in `SafeValues`. The slider stretches to the checklist's width,
so its handle stays in view at any window width.
The **Your Martlet network** card ([NETWORK](../NETWORK.md)) holds `NetworkStatus`
(status text: member with how many computers and hosts and how many are reached
from outside home right now, waiting to join with
the check number, a host PC in no network that only watches, or in no network),
`NetworkGap` (status text, shown only when your computers don't all see the
same network yet: which of them aren't paired with a host and why, the hosts
this PC couldn't reach to tell them, and Martlet hosts on the home network
outside your network with how to bring them in; `DevicesSummary` on Devices
ends with the same sentence; see [NETWORK](../NETWORK.md#when-a-host-is-added-where-your-other-computers-cant-reach)),
`NetworkCheck` (syncs now and looks for new hosts on the home network; it contacts the paired hosts, so it is not a passive
click), each computer's row title `NetworkMember-<desktop|host>-<ID>` (status
text, for example `lab-gpu. Host, not paired with this PC yet; added on
desktop-diva. 2 outside addresses; reached from outside home (outside address 1).`;
or *not reachable at home or outside right now*; for a host with outside
addresses also the guard's totals it reported, *Guard: 3 failed and 1 throttled
request(s) since it started, 0 address(es) locked out now.*; for a host some of
your computers aren't paired with, who and why, *DIVA isn't paired with it yet.
...*),
and for another computer where it was last active, *Active now
on diva-host.*, and when no host here has seen it for 12 hours why that
matters, *It hasn't used the hosts this PC reaches since then, so a host added
on MIKO shows here ...*) with `NetworkRemove-<desktop|host>-<ID>` and, for hosts,
`NetworkOutside-<ID>` (opens the *Outside addresses* dialog, field
`HostInput-addresses`, saved by `HostInputOk`; it signs the roster, so it needs
`--allow-ui-effects`), each computer that uses
one of this PC's hosts without being a member `NetworkPaired-<device ID>` (status
text: which hosts it uses and when it was last active), and each request to join
`NetworkJoin-<device ID>` (status text with the check number) with
`NetworkAllow-<device ID>` and `NetworkDeny-<device ID>`. Remove, Allow and
Turn down change the network and need `--allow-ui-effects` (then
`ConfirmationYes`); Allow also hands that computer access to every host, so
keep it to disposable lab networks. The card works on a host PC too: one that
is in a network keeps syncing (so it can let others in), and one in no network
only watches and makes no key or `network.json` (`network_status` then reads
`state: none`, `key: false`); each change in what the PC sees is logged as a
`Martlet network: ...` line (`logs_tail` with `contains: "network"`).

The **Share** page (`NavShare`, `SharePage`) holds the cards about what you
share and what is shared with you: *Your Martlet network*, *Friends*, *Share
your language models*, *Apps and API keys* and *Hosts shared with this PC*.
`ShareSummary` (status text) counts the computers in your network, the friends
you share hosts with, the people with a key to your language models and the
hosts shared with this PC. The cards keep their IDs from when they were on the
Devices page.

The **Share your language models** card ([OpenAI-compatible
endpoint](../OPENAI_ENDPOINT.md)) holds `OpenAiStatus` (status text: how many
models on how many hosts, how many people have a key, hosts that need an
update, when it was checked), `OpenAiCheck` (needs `--allow-ui-effects`: it reads each host's
`GET /v1/models` with the signed pairing and keeps `openai-models.json`),
`OpenAiPublicAddress` (the address to give people; an editable field, never
returned), `OpenAiSaveAddress`, `OpenAiCheckAddress` (needs `--allow-ui-effects`: sends `GET
<address>/models` without a key and shows the answer in `OpenAiCheckResult`),
`Help-OpenAiExpose` (the *?* with the ways to give the front door an address),
`Help-OpenAiPriority` (the *?* about a key's priority),
`OpenAiAddressProblem`, `OpenAiAddressStatus`, `OpenAiHomeAddresses` and
`OpenAiModels` (status text: each model with its hosts), and each key's row
`OpenAiKey-<key ID>` (status text: name, priority, who made it, last use,
expiry) with `OpenAiKeyPriority-<key ID>` (*Your Martlet first* or *Same as
your background work*) and `OpenAiKeyRevoke-<key ID>`. Above them the *Front
door* block holds `Help-OpenAiFrontDoor`, `OpenAiFrontDoorStatus` (status
text: the host and port it is open on and where it listens), and, when this PC
can run commands on your hosts, `OpenAiFrontDoorHost` (the host),
`OpenAiFrontDoorPort` (the port, 9445 by default), `OpenAiFrontDoorOpen` and
`OpenAiFrontDoorClose` (these two run `martlet-host exposure --front-door` on
the host and need `--allow-ui-effects`). `OpenAiShowProxy` (*Show setup*,
passive) opens `OpenAiProxyDialog` with `OpenAiProxySteps` (with
`Help-OpenAiProxyWays`),
`OpenAiProxyTailscale`, `OpenAiProxyManager`, `OpenAiProxyNginx`,
`OpenAiProxyCaddy` and `OpenAiProxyCloudflare` (text) and `OpenAiProxyClose`.
`OpenAiGiveAccess` (passive) opens `OpenAiAccessDialog` with
`Help-OpenAiAccessKey`, `OpenAiAccessName`, `OpenAiAccessPriorityFirst`, `OpenAiAccessPriorityEqual`,
`OpenAiAccessExpiry`, `OpenAiAccessConfirm` and `OpenAiAccessCancel`
(passive). Making a key shows `OpenAiInviteDialog`: `OpenAiInviteTitle` and
`OpenAiInviteNote` (with `Help-OpenAiInviteKey`) are returned as text; `OpenAiInviteText` holds the key and
is never returned; `OpenAiInviteCopy` writes the clipboard; `OpenAiInviteDone`
(passive) closes it. Save, Make key, the priority choice, Copy and Revoke
change data and need `--allow-ui-effects`.

The **Apps and API keys** card ([API](../API.md)) holds `ApiKeysStatus` (status
text: how many keys, and on how many hosts they are or why not), each key's
row title `ApiKeyRow-<key ID>` (status text: name, what it may do, which
computer made it, last use, expiry and the ID's first characters) with
`ApiKeyRevoke-<key ID>`, and `ApiKeyCreate`. Create opens
`ApiKeyCreateDialog` with `ApiKeyName`, `ApiKeyScope-<read|voice|perception|manage|openai>`
(read is ticked), `ApiKeyExpiry` (*Never*, *In 30 days*, *In 90 days*, *In a
year*), `ApiKeyCreateConfirm` and `ApiKeyCreateCancel` (passive). Creating
shows `ApiKeyCreatedDialog`: `ApiKeyCreatedTitle`, `ApiKeyHosts` (the hosts'
addresses and public key pins) and `ApiKeyExample` (a curl request naming
`$MARTLET_API_KEY`) are returned as text; the key itself (`ApiKeyValue`) is
never returned, and `ApiKeyCopy` writes the clipboard; `ApiKeyCreatedDone`
(passive) closes it. Create, the dialog's fields, Copy and Revoke (then
`ConfirmationYes`) change data and need `--allow-ui-effects`.

A paired host's `DeviceReachSection` holds `HostReachNow` (*Reached via: ...*,
status text), `HostReachMethod` (a combo box: *Through Martlet on that computer
(paired connection)*, *SSH, with Docker there*, *SSH, native Linux*, *This
PC, with Docker Desktop*; `ui_select` needs `--allow-ui-effects` and saves at
once), `HostReachHint` (status text: its first sentence, with the rest behind
`Help-DeviceReachMethod`) and `HostReachSsh` (saved when it loses
focus, on Enter or 1.5 seconds after typing stops; there is no Save button).
Host
actions (`NodeAction-UpdateHost`, `NodeAction-HostStatus`, roles) on a host
reached through Martlet there open a run window (`HostRunStatus`) that sends
the command through its gateway; on a disposable data directory without a
stored pairing secret it stops at *This PC's pairing secret is missing*.
Settings › *Your other computers* has `AllowNodeCommands` (checked by default;
`ui_toggle` needs `--allow-ui-effects` and saves `node-commands.txt`) and
`NodeAgentStatus` (status text: off, no host service on this PC, ready, the
last command it ran, or where bringing its own host service to this PC's
version stands). The same card
has `NearbyShare` (*Let my other computers find this PC and ask to use its
hosts*, checked by default; unticking it needs `--allow-ui-effects` and saves
`off` in `nearby.txt`), `NearbyShareStatus` (returned: off, nothing to share,
the port in use, *On: checking Windows Firewall...*, Windows Firewall or a Public
network keeping other computers out (then only Martlet on this PC can find
it), or on with the hosts it offers; then *Last request:* allowed, denied,
withdrawn or stopped) and `NearbyFirewall` (*Let my other computers reach this
PC*, shown only when blocked: an administrator prompt, never part of
verification).
Home and host-dashboard steps have their buttons as `Step-<step>-<n>` (returned:
the button's label and step, for example *Add Thinking: Add roles*; the host
dashboard's *Add roles* step offers every role in `HostRoles` that this PC's host
service doesn't run yet, *Add Deep thinking* included, then *Remove* for each it
runs), whether
each is ticked as `StepState-<step>` (returned: *Host service: done*, *Pair your
main PC: to do* or *Add roles: optional, not done*) and their detail line as
`StepDetail-<step>` (status text). A step with more than two
buttons (or long labels, like `Step-roles-<n>`) wraps them on rows under its
detail, so with `layout` the buttons' `bounds` start at the detail's left edge
and stay inside the window. The host dashboard reads this PC's own host service
by itself (the same read as `host_service_status`): when it opens, every 30
seconds while the window shows and when it shows again, so steps tick without a
button. `HostServiceStatus` (returned) is the status under its icon (*Checking...*,
*Needs Docker Desktop*, *Needs Windows restart*, *Windows isn't ready for Docker
Desktop*, *Waiting for Docker Desktop*, *Not set up yet*, *Host
service stopped*, *Address changed*, *Not answering yet* or *Host is running*),
`HostStepsHeading` (returned) reads *This host is ready* once the required steps
(Docker Desktop, host service, pairing) are done and `HostStepsSummary`
(returned) says how many steps are left and the next one, or *All set*, and when
it last checked. `CheckHostService` (*Check again*) only repeats that read and
says what it found in the status line, so it is a passive click. On the host
dashboard,
`StepDetail-docker` says whether Docker Desktop's **engine** answers, or why
Windows can't start it (firmware/nested virtualization, Windows features, WSL
status, missing/disabled host services or a pending restart), with the same
recovery guidance as `virtualization_status`. This is checked even while Docker
Desktop's window is open, and refreshed by `CheckHostService`. `Step-docker-0`
then reads *Restart Windows*, *Turn on virtualization*, *Turn on Windows features*
or *Review Windows setup*. Setup and *Start Docker Desktop* use the same guarded
run-window recovery. These actions can ask for administrator approval or a
restart, so they need `--allow-ui-effects`; never execute an actual Windows
change or restart during verification. `StepDetail-service`
says the host service is not set up (`Step-service-0` *Set up host service*),
set up but stopped (*Start host service*), set up for an address this PC no
longer has or not answering (*Set up again*), or *Running and reachable on your
network as <host ID>* (done, no button). `StepDetail-pair` names the computers
in the host's Martlet network and, when this PC is paired with its own host
service, every other computer that service reports as paired, member or not,
with when each was last active (*Paired with IMOUTO (active now) and this PC.*,
done, with `Step-pair-0` *Pair another computer*). `StepDetail-roles` lists the installed
roles (*Runs Lip-sync and Listening.*, done) with an *Add* button for each other
role and a *Remove* button for each installed one. `StepDetail-update` reads
*Up to date: the host service runs Martlet x.y.z* (done, no button) or offers
*Update host service*. After a restart for
virtualization, Martlet opens a run window by itself (`HostRunWindow`) that
continues the setup; `continueSetup` in `virtualization_status` shows what is
pending.
When this PC becomes a host PC (Settings' *Use this PC as a host*, the welcome
wizard, or a request from another computer) or Martlet starts on one, it brings
the host roles up by itself: when the host service on this PC runs roles (read
now, or remembered in `this-pc-host-roles.txt` from the last read, or, before
any read, because this PC is paired with its own host service), Docker Desktop
is installed and Windows is ready for it, a hidden run *Start this host's roles*
(in Background tasks and `HostRunsNow`) starts Docker Desktop when needed, waits
for the network holder to restart the gateway and roles, and runs
`martlet-host warm`, which loads each role's model (`warm=` in its
`role.conf`). `HostAutoStart` (returned) says what it decided or did (*Starting
Docker Desktop, then this host's roles (ollama, stt), and loading their
models.*, *Martlet started this host's roles by itself at 7:02 PM, and their
models are loaded.*) or why it didn't (*Docker Desktop isn't
installed, so nothing started by itself...*, *Windows isn't ready for Docker
Desktop...*, *This PC's host service runs no roles yet...*); it never installs
anything.
Setup runs go **side by side**; nothing refuses a step because another one is
working. What two runs share is done once: installing Docker Desktop, getting
Windows ready for it, starting it, building the host image, checking Windows
Firewall and setting up and pairing this PC's own host service. Changes that
aren't a setup or update (adding or removing a role, status, pairing, warming)
don't wait for this version's host image: while it isn't built yet (for
example while *Keep this PC's host service current* builds it right after
Martlet updated itself), they run with the engine of the version the host
service runs now, when its image is on this PC and knows the role, and their
output says so. Opening Windows Firewall during *Set up this PC as a host* runs
while Docker Desktop starts and the image builds, and *Update hosts now* checks
and updates every paired host side by side. The second run's
`HostRunStatus` reads *Waiting: "<other run>" is starting Docker Desktop. This
continues once that's done...* and carries on afterwards; when the run doing it
fails, the waiting one stops with the same reason, and when it is canceled, the
waiting one does the step itself (the host-runs log says *"<run>" was canceled
before it finished ..., so this run carries on by itself*). Changes to the host
service itself run side by side in its engine, where only colliding ones wait
([Changes side by side](../../deploy/host/README.md#changes-side-by-side)). Pressing a step whose
run is still working brings that run's window forward instead of starting it
twice. While runs work, `HostRunsNow` (returned) lists them with their status
lines (*2 runs working side by side: Start Docker Desktop: Waiting for Docker
Desktop to start... · Check this PC's host: Waiting: ...*), `StepDetail-docker`
says which run is installing or starting Docker Desktop (*"Start Docker Desktop"
is starting Docker Desktop. You don't have to wait: ...*), and `Step-docker-0`
says what that run does (*Installing Docker Desktop...*, *Starting Docker
Desktop...* or *Getting Windows ready...*) and is disabled (`enabled: false`)
until it ends. This includes the hidden *Start this host's roles* run of a PC
that just became a host PC. When no run works on Docker Desktop any more, the
dashboard reads the host service again at once (the desktop log says *No run
works on Docker Desktop any more; reading this PC's host service again now.*),
so the step ticks or offers its button again. While a host
service Martlet hasn't seen set up waits for Docker Desktop, `StepDetail-service`
offers `Step-service-0` *Set up host service* already (its run waits for Docker
Desktop and continues). The dashboard keeps reading the host service every 30
seconds while runs work. Several run windows have the same controls, so name the
one to click: `ui_click` `{"id":"HostRunCancel","window":"Martlet - Start Docker
Desktop"}`. To exercise it without the real engine, launch the desktop with
`DOCKER_HOST` pointing at a missing pipe and Docker Desktop already running:
*Start Docker Desktop* (`Step-docker-0`) then waits for an engine that never
answers, and `HostStatusConsole` (*Show host status*) waits for it. To exercise
a start without touching Docker Desktop or Windows at all, also set
`MARTLET_SIMULATE_DOCKER_START` to a number of seconds (1-600) before launching
the desktop (FIXTURE, `SimulatedDockerStart.cs`): every Docker Desktop start
then waits that long, shared by the runs that need it as a real start is, and
stops as a start that failed; the host-runs log says *FIXTURE
(MARTLET_SIMULATE_DOCKER_START)*. With `this-pc-host-roles.txt` (one role per
line, such as `ollama`) in the data directory, a desktop made a host PC (for
example with `role_lab ask host`) starts Docker Desktop by itself, so
`Step-docker-0` reads *Starting Docker Desktop...* and is disabled until the
fixture ends.
Devices' `AddComputer` (and Settings' `OpenHosts`, and on a new PC Home's
`HomeConnectComputers` or the `HealthOpen-thinking-setup-network` fix) opens the *Add a computer*
wizard (`HostsWindow`, titled *Martlet - add a computer*; the click may return
`completed: false` while that dialog stays open). Pairing needs no saved
settings: on a fresh data directory the wizard opens with `HostStatus` *No
Martlet host paired.* and `PairHost` goes straight to the host. It has two
steps. Its rail steps (`HostsStepConnect`, `HostsStepRoles`), `HostsBack`,
`HostsNext`, `HostsClose`, `HostsEnterCode` (*Enter a pairing code*: shows the
address and code fields, which also open by themselves when nobody answers on
the network) and the `HostAddressSection` and `DeviceIdSection` expanders only
change what the wizard shows, so they are passive clicks. `SetupThisPc` (*Set
up this PC*) and `SetupHost` (*Set up over SSH*, with `SshTarget`; Martlet
picks Docker or native Linux from what the computer has) set up and pair a
new host and need `--allow-ui-effects`; a successful connection of any kind
moves to Roles. Snapshots return `HostStatus` (the wizard's status line: what
pairing did, or why it was refused, such as *That code doesn't match...* or *No
Martlet host answered at ...*), `DockerState` (whether Docker Desktop is
running, installed or missing), `PairCodeHelp`, `PairedHost` (the host the
Roles step acts on; `HostChoice` picks another when several are paired) and
`RolesSummaryText` (how its roles run, or *Connect a computer first...*).
The wizard's *?* marks (`Help-HostsConnect`, `Help-HostsNearby`,
`Help-HostsNotListed`, `Help-HostsInvite`, `Help-HostsPairingCode`,
`Help-HostsSsh`, `Help-HostsAddress`, `Help-HostsOtherWindowsPc` and
`Help-HostsDeviceId`) open their cards and link to the wiki's *Setting Up a
Host* page; *Prepare a Linux computer* has `Help-PrepareHost`.
`AddRole-<role>`/`RemoveRole-<role>` act on that host. `PairAddress` and
`PairingCode` take the host's address and short code (`ui_set_text`, so
`--allow-ui-effects`), and `PairHost` pairs; a successful pairing stores a
device secret in Windows Credential Manager, so verification stops at refused
codes. On the host dashboard, *Show a pairing code* (`Step-pair-0`) shows the
address (`HostRunPairAddress`, returned) and the one-use code (`HostRunPairCode`,
never returned) in the run window's `HostRunPairing` panel; the host-runs log
masks codes. The code has no deadline: it works until the other desktop uses it
or the run is canceled, and `HostRunPairNote` (returned) says so. *Copy code*
(`HostRunPairCopy`, its label returned: *Copy code*, then *Copied* or *Couldn't
copy*) puts the code on the clipboard, kept out of Windows clipboard history and
the cloud clipboard and cleared again when the code stops working if it is still
there, so clicking it needs `--allow-ui-effects`. While the host isn't paired, `StepDetail-pair` tells the owner to find this PC from the
main PC (*Martlet on your network*), and when Windows Firewall keeps other
computers out it says so and `Step-pair-1` (*Let my other computers find this
PC*, an administrator prompt) appears. While a computer asks to
join the network this host PC is in, a step `join-<device ID>` (*Let IMOUTO into
your Martlet network*) follows it: `StepDetail-join-<device ID>` gives the check
number, `Step-join-<device ID>-0` is **Allow** and `Step-join-<device ID>-1`
**Turn down** (both change the network, so they need `--allow-ui-effects`).

*Martlet on your network* ([how it works](../ARCHITECTURE.md#finding-your-other-computers))
is the first card of the wizard's *Connect* step. Opening the wizard on
that step (so `AddComputer`) and `NearbyFind` (*Find again*) send Martlet's
discovery query to port 9444 on loopback and the local network's broadcast
addresses and list who answers; they pair nothing and change nothing, so they
are passive clicks. `NearbyStatus` (returned) says what was found (*Found 1
computer with a host this PC doesn't use yet.*, *No other Martlet
answered...*), what a request is doing or why it stopped (*DIVA denied the
request (or it expired there).*, *Stopped asking DIVA.*, the sharing
computer's reason, *Paired with ... through ...*). Each found computer is a row
with `NearbyItem-<n>` (returned: *<name> (<address or this PC>): <hosts> ·
Martlet <version>*) and `NearbyConnect-<n>`, which starts a request and needs
`--allow-ui-effects`. While asking, `NearbyNumber` returns the check number and
`NearbyCancel` (*Stop asking*) withdraws the request (passive). On the
computer asked, a separate window `JoinRequestWindow` (*Martlet - <name> wants
to use your hosts*) returns `JoinRequestTitle`, `JoinRequestText` (who, from
which address, which hosts), `JoinRequestNumber` (must equal the asking side's
`NearbyNumber`) and `JoinRequestExpiry`; `JoinAllow` and `JoinDeny` need
`--allow-ui-effects`. Allow opens a run window (`HostRunWindow`, *Martlet - Let
<name> use your hosts*) that asks each host for a one-use code; its
`HostRunStatus` and the host-runs log show progress, never the codes. To
exercise it on one PC, run two desktops on disposable data directories with
`DOCKER_HOST` pointed at a missing pipe (so neither finds this PC's real host
service): give the sharing one a `hosts.json` with an SSH host that refuses
(for example `martlet@127.0.0.1:1`), so it answers on loopback, Allow reaches
the run window and the request fails with that host's reason on both sides
before any real code or credential exists.
Use `ui_snapshot` again to observe asynchronous effects. Modal
actions may return `completed: false` while their dialog remains open; this
means the invoke is still pending, not that the action finished.

Home (companion mode) shows what needs attention, then one Health tile per
part. `StageTitle` and `StageText` return the hero's headline and line,
`HealthTitle` (*Needs attention* or *All good*) and `HealthSummary` (for
example *1 problem stops Martlet replying · 3 good to know.*) the list's
heading, and `HealthAllClear` shows when nothing needs attention. Each item's
title `HealthIssue-<id>` returns its level, title and detail (*Problem: Ollama
isn't running on this PC. ...*); ids include `data-folder`, `settings`,
`thinking-setup`, `thinking-retired`, `ollama`, `job-<job>` (coverage, for
example `job-listening`), `host-service`, `failed-thinking`, `failed-listening`,
`failed-voice` (a reply's text arrived but speaking it failed),
`microphone`, `microphone-blocked`, `speakers`, `listening-setup`,
`voice-setup`, `audio2face`, `webview2`, `vision`, `vision-source`,
`host-<host ID>`, `host-update-<host ID>`, `tools-config`, `tools-<server>`,
`update`, `update-failed`, `update-cleanup`, `errors` and `crash`. Its fixes
are `HealthOpen-<id>-<fix>` when they only open a page or hide the item
(passive clicks, for example `HealthOpen-thinking-setup-open-thinking` or
`HealthOpen-crash-dismiss`) and `HealthFix-<id>-<fix>` when they do something
(start or install software, check a host, install an update), which needs
`--allow-ui-effects`. Tiles `HealthCheck-<part>` (`thinking`, `listening`,
`voice`, `lipsync`, `microphone`, `speakers`, `character`, `devices`,
`hostservice`, `tools`, `updates`, `app`) return *<Part>: OK*, *needs attention* or *not checked or
not set up* with the state, and clicking one only opens its page.
A companion PC paired with a host service on this PC (`DeviceRoleSummary`
reads *Companion PC + host*) reads that host service from this PC's Docker as
the host dashboard does (every 30 seconds while the window shows, not while a
conversation replies or hears you): `HealthCheck-hostservice` shows its stage
(*Not set up yet*, *Waiting for Docker Desktop*, *Host service stopped*,
*Host is running*), and while it isn't ready `HealthIssue-host-service` says
why and which of this PC's jobs stop (they no longer show as `job-<job>`
items), with the host dashboard's next step as `HealthFix-host-service-repair-0`
(Install or Start Docker Desktop, Set up or Start host service; its label is
returned, such as *Start Docker Desktop: This PC's host service isn't working*),
then `HealthFix-host-service-check` and `HealthOpen-host-service-show`. While a
run installs or starts Docker Desktop, `HealthFix-host-service-repair-0` reads
*Starting Docker Desktop...* (or *Installing Docker Desktop...*) and is
disabled, the hero (when this item is the top problem) offers *Check again*
instead, and on Devices the
`CoverageFix-<job>-RepairHostService` buttons are disabled with the same label
until that run ends. On Devices,
`Node-this-pc` and `SelectedDeviceHealth` read *Host service not working* (or
*not answering*), and coverage names it *This PC's host service*, never by its
host ID. To see it, pair a disposable data directory's `hosts.json` with
this PC's LAN address (`method` `ThisPcDocker`) on a PC whose Docker has no
`martlet-host-gateway` container.
`HealthRecheck` re-reads settings, devices and this PC's own loopback services
(Ollama when Thinking uses it) and contacts no other computer, so it is
passive. To see a problem on a disposable data directory, put invalid JSON in
`settings.json` (*settings*), a stale `logs\desktop.<pid>.running` marker
(*crash*), or choose Ollama on this PC in Companion › Thinking while Ollama
isn't running (*ollama*). After an unclean exit the desktop looks up that
run's process ID in Windows' Application event log in the background:
`HealthIssue-crash` then reads *Windows recorded <exception> (0x<code>) in
<module>* when Windows has a crash record, and `logs_tail` (`contains`:
`Windows recorded`) returns the full line, or the *no crash* line for a kill or
power loss (a made-up marker PID gives the latter, about 20 seconds after
launch).

The Background tasks page (`NavTasks`) lists every run window's run since
Martlet started, newest first: setting up, updating or pairing a computer,
reconfiguring your computers with the recommended setup (*Reconfigure your
computers*), a download (*Download Martlet x.y.z*, *Download Parakeet ...*,
*Download cloudflared*, a model), Docker Desktop and the like. What Martlet
starts by itself runs out of sight from the start and is listed the same way:
the automatic update download, host updates (*Update gpu-box to Martlet
x.y.z*; a try that finds the host busy changes nothing and leaves the list),
*Keep this PC's host service current*, and the changes your other computers ask
this PC's host service to make (*Install chatterbox (from desk-main)*, *Update
this PC's host service to Martlet x.y.z (from desk-main)*; their output still
goes back to that computer, and Cancel task tells it the owner canceled it
here). Hiding a run window (`HostRunHide`, Esc
or its close button) only hides it while it runs; a question the run asks (a
password, a role's choices, a confirmation) shows its window again with it, and
a run outlives the window that started it (the hosts wizard). `NavTasksCount`
is how many run now (shown only while some run). `TasksSummary` reads *1 running
now.*, *Nothing is running now. 1 finished since Martlet started; ...* or
*Nothing has run in the background since Martlet started.* (then `TasksEmpty`
shows). Each task `<id>` (1, 2, ... in this run of Martlet) has `TaskTitle-<id>`
(its run window's title), `TaskState-<id>` (*Running for 2 min. <status>*,
*Done at 3:47 PM after 25 s. <summary>*, *Canceled at ...*, *Stopped at ...
<why>* or *Paused at ... for a Windows restart*), `TaskShow-<id>` (*Show:
<title>* shows the run window again; *Show output: <title>* once it has finished,
which opens a window with the output it kept, pairing codes hidden) and, while
it runs, `TaskCancel-<id>` (*Cancel: <title>*), which asks the same
`CancelTaskQuestion` and needs `--allow-ui-effects`. A run that finishes while
hidden closes its window. `TasksClear` (*Clear finished*) drops the finished
tasks and their kept output from the list; `logs_tail` `host-runs` keeps every
run's output. `NavTasks`, `HostRunHide`, `TaskShow-<id>` and `TasksClear` are
safe clicks. The desktop log records *Run window hidden; the run keeps going in
Background tasks: <run>* and *Background task canceled by the owner: <run>*.
Setting `MARTLET_SIMULATE_BACKGROUND_TASK` to a number of seconds (1-3600)
before launching the desktop starts *Simulated background task* in a run window
once the window shows: it writes a line each second and changes nothing, to
check Hide, Background tasks, Cancel task and the exit question.
The Diagnostics page (`NavDiagnostics`) lists log lines newest first. Each
shown line is a list item `LogEntry-<n>` (`LogEntry-0` is the newest shown)
whose value reads *<time> <level> <computer> · <part>: <first line>*, where
*<computer>* is *This PC* for this PC's desktop app and for its own host
service's gateway (its host ID, paired here or read from Docker on a host PC);
file paths in it read as `[path]` and e-mail addresses as `[email]`;
clicking one only selects it, and `LogDetail` then returns the whole line
(time, level, computer with its ID, such as *This PC (diva-host) · Host
gateway*, part, who passed it on and every following line).
`LogSummary` says how many lines are shown of how many, from how many
computers (this PC's app and host service count once), the last 24 hours'
errors and warnings. Lines from other computers come from this PC's copy of
everyone's lines (`logs\network-logs.json`, which `logs_timeline` also reads).
The filters are
pills that only filter: `LogLevel-all`, `LogLevel-warnings`, `LogLevel-errors`,
`LogPart-<part>` (`all`, `desktop`, `avatar-renderer`, `host-runs`, `gateway`)
and `LogSource-<computer>` (`all`, this PC's device ID such as
`LogSource-desktop-diva`, which also covers this PC's host service, or another
computer's ID); each returns its label as its value (*From: All computers*,
*From: This PC (desktop-diva, diva-host)*, *From: gpu-pc*). All are passive clicks, and snapshots
report which is chosen in `selected`. `LogSearch` needs `ui_set_text` (and so
`--allow-ui-effects`). `LogsRefresh` reads the logs again and sends nothing, so
it is passive (it reads each paired host's new lines); `LogsCopy` (clipboard),
`LogsOpenFolder` (Explorer) and `LogsSave` (*Save logs to share...*: a save
dialog, then the ZIP written and shown in Explorer; `logs_export` writes the
same ZIP headlessly) are not. There is no log host to choose: `LogShareStatus`
says how sharing with every paired host went (*Sharing logs with 2 of 3 hosts.
Waiting for gpu-box. Update old-box to share its logs. Checked at 1:40 AM.*),
or that no host is paired yet (*Checking your hosts...* until the first run).
To see lines on a
disposable data directory, write `logs\desktop.log` (lines like
`2026-10-01 22:15:44.974 -07:00 WARN [1] message`), `logs\avatar-renderer.log`
or `logs\host-runs.log` before launching (other computers' lines come from
`logs\network-logs.json`: `{"schema_version": 1, "entries": [{"source",
"component", "seq", "at", "level", "message"}]}`, lines from the last week only; a
`logs\network-logs.journal` beside it adds one such line object per text line). Home's `HealthOpen-errors-diagnostics`
and `HealthOpen-crash-diagnostics` open this page.

`ui_snapshot` reports `selected` (true or false) for controls that are chosen
rather than ticked (navigation, Companion's side list, radio buttons and
filter pills, list items), a combo box in the status fields reads as its
chosen option, and a check box in the status fields reads as its label (its
`checkedState` says whether it is ticked).

Keyboard shortcuts (underlined letters, such as the *x* of Settings' *Exit
Martlet*) need Alt: Martlet ignores the plain letter, so a stray key never
presses a button. Enter and Esc still press a dialog's default and cancel
buttons, and an open menu still takes plain letters. The controls that talk,
listen, watch or show the character have no shortcut at all: Home's
`HomeListen`, `HomeWatch`, `OpenLiveConversation`, `ToggleCharacter` and the
character buttons beside them, the talk window's `LiveStop` and `LivePtt`, and
the notification-area menu's `TrayTalk`, `TrayStartListening`,
`TrayStopListening`, `TrayStartWatching`, `TrayStopWatching`, `TrayPause`,
`TrayResume`, `TrayEndTalk`, `TrayCharacter`, `TrayCharacterClickThrough` and
`TrayCharacterProfiles`. UI Automation doesn't report WPF's shortcuts, so
`ui_snapshot` can't show them; `AccessKeysTests` in `Martlet.Desktop.Tests`
checks the Alt rule.

Companion's side list starts with *How it works*, the jobs Martlet needs in
priority order: `CompanionTab-Thinking`, `CompanionTab-Listening`,
`CompanionTab-Voice` and `CompanionTab-LipSync`. *Optional extras* follows with
the jobs Martlet works without, each with an intro that starts with
"Optional:": `CompanionTab-DeepThinking` (Thinking pool),
`CompanionTab-Singing`, `CompanionTab-Pictures` (Picture making; its ID keeps the old name), `CompanionTab-Vision`,
`CompanionTab-Reading` and `CompanionTab-Motion`. Every page shows its group as `CompanionGroupTitle`
("HOW IT WORKS", "OPTIONAL EXTRAS", ...), its name as `CompanionPageTitle` (*Thinking*, *Picture making*,
...) and its intro as `CompanionIntro` (its first sentence; `help` and `Help-CompanionIntro` have all of it, with what Off means on an optional page).

The desktop character has its own group in Companion's side list, *How it
looks*: `CompanionTab-Character` (the character model, showing and hiding it,
its position, zoom and your characters), `CompanionTab-SpeechBubbles`,
`CompanionTab-Emotes` (emotes, motions and combos), `CompanionTab-Eyes` (where
the character looks and where its eyes are) and `CompanionTab-Touch` (touch
zones and touch temperament). When Emotes and motions or Touch opens (or is
drawn again while scrolled to its top), only its first six rows show at once;
the other rows join a batch at a time once the page has drawn, within about
half a second, so poll `ui_snapshot` with `until` for a later row's ID (such as
`CharacterActionOn-40` or `TouchTemperamentStatus`). Every row is built at
once, so a save always includes every row. Drawn again further down the page,
every row shows at once and the page keeps its place. The desktop log records
how long each Companion page took to show when it opens, such as *Companion ›
Touch drew in 62 ms (21 ms to build, 41 ms to lay out), 919 elements.*, and,
for a page whose rows join in batches, *Companion › Touch showed all 43 rows
in 8 batches, 290 ms after it opened.*

Every Companion page starts with its *Now* card. On the pages that aren't jobs or
optional extras, its line and problem are readable values named for the page:
`SpeechBubblesNow` (*Speech bubbles: beside the character; subtitles off.*),
`EmotesNow` (*12 emotes and 4 motions on, 2 combos.*, with Martlet's own
gestures when the model gets them), `EyesNow` (*Where it looks: follow your
mouse, as the personality decides; replies may change it. Eyes: from the
model's own meshes.*), `TouchNow` (*12 touch zones on, found by the Thinking
model; a touch stops Martlet mid-sentence.*), `PeopleNow`, `ToolsNow` (*Tools: 3
MCP servers on, the terminal off.*), `SmartHomeNow` (*Smart home: connected to
Home at http://homeassistant.local:8123; Martlet may control lights, ...*),
`DiscordNow` (*Discord: the bot Martlet is connected to 2 servers.*) and
`MessagingNow` (*Telegram: answering @martlet_bot (1 paired chat); WhatsApp:
off.*). Each has a `<Page>NowProblem` value (for example `TouchNowProblem`,
*The character is hidden, so touches do nothing until it shows.*) while
something stops the page's feature. The lines come from what Martlet already
knows; opening a page fetches nothing.

For the desktop character, open `CompanionTab-Character`; with
`--allow-ui-effects`, `SetupCharacterToggle` shows or hides it and
`SetupCharacterZoomIn`, `SetupCharacterZoomOut` and `SetupCharacterResetZoom`
zoom its overlay. `SetupCharacterNow` returns the page's Now line (the model,
then *on your desktop* or *hidden*), and `SetupCharacterNowProblem` appears when
the character's last stop did not finish cleanly (pressing Show or Hide
character retries; details go to the `desktop` log). Exiting never waits on the
character: Settings' `ExitMartlet` (needs `--allow-ui-effects`) closes Martlet
even then, and Windows ends the renderer with it (see **Exiting** below). After a zoom, the `SetupCharacterView` status reports the
character frame's size (with, in parentheses, the overlay's full width: the
frame plus the transparent room on each side the model can move into), its
distance from the top of the screen, the camera zoom and where the
top of the character's head sits relative to the frame's top edge (it must
stay in view at every zoom). While the character shows, `SetupCharacterModel`
describes what its model drives, for example *Model: 236 controls; 1 texture
shown at 1/2 size to fit the graphics budget; blinks with ParamEyeLOpen,
ParamEyeROpen; mouth moves ParamMouthOpenY; no idle motions; 0 expressions;
physics on.* (parameter IDs only, never paths); the desktop log records the same
as *Character model loaded: ...*.

In the character window (`OpenAvatar`, see above), `AvatarModelInfo` gives the
same *Model: ...* description while the character shows, or *Model not loaded:*
and why the chosen model couldn't be shared or shown (for example *The model
refers to x.png, which isn't in its folder.* or, when the renderer rejects it,
*This model can't be shown: ...*, which `logs_tail` `avatar-renderer` also
records).

**Stroking the character**: while the character's position is locked, a left
press that drags beyond Windows' drag distance can't move it, so it strokes
the character. The overlay samples the path every 40 ms (at most 400
samples), hit-tests the samples in batches with one `touches` message to the
renderer page, and sends them to Martlet as `stroke` messages on the request
pipe (`move` batches while the stroke goes on, then `end`). Martlet matches
each sample to a touch zone, and to every other zone it lands in where zones
overlap (`CharacterTouchZones.Touched`): each zone the stroke enters as the
matched zone plays its reaction
(unless it is resting), and the first zone's first emote or gesture is held
until the stroke ends. At the end Martlet summarizes the stroke (zones crossed
in order, each overlapping zone a sample was on included, pace `slow`, `steady` or `quick`, passes back and forth, seconds,
where it ended from where it began and its main direction)
and, for the crossed zones with *Martlet notices* on (like a tap there),
records it in the touch ledger once for each pass (at most 8) with its whole
path (`CharacterPhysicalWords.Stroke`: every zone in the order first crossed,
at most 8 named, a left and a right zone crossed one after the other said
together), so the next reply hears *They slowly stroked your hair 4 times* or
*They slowly stroked down from your chest over your stomach to your thighs
once* (*up and down over* or *back and forth over* the zones when it turned
back); a stroke, like a tap, can
start a touch-only reply. Ctrl+drag or middle-drag still pans a zoomed view,
and an unlocked drag still moves the character. `character_stroke` strokes it
through UI Automation (`MoveAvatar`'s value, `"stroke:ms;x,y;x,y;..."`) along
`points` (`[[x, y], ...]`, 2 to 200, fractions of the overlay's drawing), one
every `stepMs` (default 40), which needs `--allow-ui-effects` and a locked
position, waits for the stroke to end and returns the overlay's reading as
`last`: the last tap's fields plus `stroke` (`n`, `samples`, `hits`, `ms` and
the coarse `zones` crossed) and `physical`. Without `points` it only reads.
Companion › Touch › Touch zones' `CharacterPhysicalLast` shows Martlet's
summary.

**Where Martlet draws over the face**: the blush levels (the blush on a model
without a blush of its own, and `blush_deep` and `blush_fierce` on every model,
over its own blush) and the overlay emotes are drawn around the face each time
the renderer page draws a frame. A Live2D model's face is pinned to its own
face meshes. Live2D draws a face in layers that move apart as the head nods and
turns (the back hair, the skin, the eyes, nose and mouth over it), so when the
model loads, the page first looks for the face's skin: of the drawables that
show, the one drawn highest whose triangles hold the face's middle and both
cheeks (one more than three face widths across or high is not skin). The face
is then pinned to every vertex of the skin, so the blush stays on the cheeks,
also on a model whose physics turns its head (head angles that only feed
parameters such as `ParamFaceAngleX`). A model without such a drawable is
pinned to the vertices that turn with the head instead: the page moves each
head angle (`ParamAngleX`, `ParamAngleY`, `ParamAngleZ`) to find them, then
moves every other parameter to its limits, to drop the vertices that change
shape on their own (hair physics, eyelids, eyes, mouth, brows), and puts every
parameter back. In each frame the eyes, cheeks,
mouth and top of the head move with those vertices, so they follow idle
motions, body sway, breathing, the mouse, a look at a point and gestures as
the model draws them. A model with neither uses the
earlier estimate from its head angles. A VRM's face follows its posed head
bone. Each blush lies on its cheek's surface: a turned head shows the near
cheek wider and the far cheek narrower, and the far cheek fades out as it
turns away. `character_face` reads this through UI Automation (`MoveAvatar`'s
value `"face"`), `samples` times (1 to 60) `gapMs` apart (default 250). It
changes nothing, so it needs no `--allow-ui-effects`. Each reading in `faces`
has `n`, `found`, `tracking` (`mesh`, `bones` or `estimate`), `x`, `y` and
`width` (fractions of the overlay's drawing, +y down), `tilt` (degrees,
clockwise), `cheekLeft` and `cheekRight` (`x`, `y`, `visible` from 0 to 1,
`across`, the cheek's width against the face's width, and the hit test there:
`hit`, `drawables`, `bone`, `mesh`), `eyeLeft`, `eyeRight`, `mouth` and `top`
(`x`, `y`: the eye and mouth points the overlay emotes such as `tears` or
`tongue_out` are drawn from, and the top of the head; left out when the
renderer has none; an eye with a known iris has its point at the eye's
middle; with a face measured by vision the cheeks and mouth are where vision
saw them), `cheekSize` (the measured cheeks' radius in face widths, which
sizes the blush; `null` without a measurement), `overlays` (the overlays showing, such as
`["blush_deep"]`; one fading out is listed until it is gone) and
`pinned` (Live2D: `carriers`, how many mesh vertices the face rides on,
`skin`, the ID of the face's skin drawable when they are its vertices, or
`null` when they are the vertices that turn with the head,
`milliseconds`, how long finding them took at load, and `eyeMilliseconds`, how
long finding the eyes' meshes took). The eyes for drawings over them come with
each reading: `eyesFrom` (`mesh`: a Live2D model's iris and eye-white meshes;
`bones`: a VRM's eye bones with its iris and eye-white meshes; `vision`: eyes
measured by vision fill what the model can't give; `estimate`: an eye has
neither, so its iris and opening are left out), `irisLeft` and `irisRight`
(`x`, `y`, `rx`, `ry`: the iris's middle and radii across and down the face;
`x` and `rx` are fractions of the drawing's width, `y` and `ry` of its height;
`null` when unknown), and `eyeLeftShape` and `eyeRightShape` (the eye's visible
opening now: `points`, `triangles`, `null` for an outline, its box `left`,
`top`, `right`, `bottom`, and `irisInside`, whether the iris's middle is in
it; 0 `points` when the eye is closed or hidden). `summary` gives the
`tracking` used, how far the face `moved` (`x`, `y`, `width`, `tilt`) and, for
each cheek, `onCharacter` (the share of readings over the character),
`mostlyOver` and `mostlyOverShare` (the topmost drawable, mesh or bone there
most often, and for what share of readings), `visibleLeast` and `across`
(`least`, `most`). It also gives `eyesFrom` (the sources seen) and, for
`eyeLeft` and `eyeRight`, `iris` (the share of readings with an iris),
`irisMoved` (`x`, `y`: how far the iris's middle moved), `irisInside` (the
share of open readings with the iris inside its opening), `opening` (`least`,
`most`: the opening's height, which a blink closes) and `closed` (the share of
readings with no opening). Sample several times to see the irises follow the
gaze and the openings close on a blink. `MoveAvatar`'s value in `ui_snapshot`
shows the last reading as `face`.

`character_picture` takes a picture of the showing character as it shows now
(`MoveAvatar`'s value `"picture"`). It is the renderer's own capture of the
overlay's page, so Martlet's drawings over the face are in it, such as the
blush glow and overlay emotes like heart eyes. It is cropped to the character
with a little room. Zoom the character first (`SetupCharacterZoomIn`) to see
small parts such as the eyes larger. It returns `taken`, `picture` and `saved`.
`picture` has `n`, `path` (the renderer's PNG file in the temp folder, replaced
each time), `width` and `height` in pixels, and `left`, `top`, `cropWidth` and
`cropHeight`: where it sits on the overlay's drawing, as fractions like
`character_face`'s positions. When the picture can't be taken, `picture` has
`error`. `saved` is the copy at `outputPath` (a full path to a `.png` file)
when given. It changes nothing on the character, so it needs no
`--allow-ui-effects`. `MoveAvatar`'s value then shows the last picture as
`picture`.

**Where the touch zones are now**: `character_zones` reads where each area of
the showing character's touch zones is, as each frame is drawn (`MoveAvatar`'s
value `"zones"`), `samples` times (1 to 60, default 1) `gapMs` apart (0 to
5000, default 250). The zones are the ones Martlet last gave the renderer: the
saved zones in use of the model it shows. *Show the zones on the character*
(`TouchZonesShowOnCharacter`) draws the same areas over the character. An area
that follows Live2D drawables (a tail's, which swings) is the box around those
drawables where they are drawn now (`from`: `drawables`). An area that follows
two or more of a VRM's spring-bone joints (a tail's) is the box around those
joints where they are now, as wide on each side as its box is at its narrowest
(`nodes`), so a tail segment that swung sideways lies sideways. An area that
follows VRM bones, or one joint, keeps its size and moves with them (`bones`). Any other
area stays at its box, moved only by the view's zoom and pan (`box`). `last`
has `n`, `found`, `renderer`, `draw` (whether the areas are drawn on the
character) and `areas`: each has `zone`, `area` (0 for the zone's first),
`from`, and `left`, `top`, `right` and `bottom` (fractions of the overlay's
drawing, +y down). `summary` gives, for each area, what placed it, its last box
and how far its middle moved (`x`, `y`). Sample several times while the idle
motion plays to see a tail's areas move with it. Reading changes nothing, so it
needs no `--allow-ui-effects`. `MoveAvatar`'s value in `ui_snapshot` shows the
last reading as `zones`.

**The character's idle body**: while it shows, a VRM stands in a relaxed pose
(arms hanging close to the body, elbows softly bent, fingers and thumbs
curled), breathes and sways slowly; see *VRM idle pose and breathing* in
[Emotes and motions](../AVATARS.md#emotes-and-motions). `character_pose` reads
it through UI Automation (`MoveAvatar`'s value `"pose"`), `samples` times (1
to 60) `gapMs` apart (default 250). It changes nothing, so it needs no
`--allow-ui-effects`. Each reading in `poses` has `n`, `found` (false before a
model shows and for a Live2D model, whose own breathing isn't read),
`renderer`, `idle`, `breathing` (`phase`, 0 to 1 of one breath; `inhale`, how
full the chest is, 0 to 1; `perMinute`), `arms` (`left` and `right`:
`fromDown`, the upper arm's angle from straight down, and `elbow`, its bend,
in degrees measured on the posed bones), `curl` (`left` and `right`: the
middle finger's curl in degrees, 0 straight as in the T-pose), `sway` (the
spine's sideways lean, degrees) and `bones` (`head`, `neck`, the shoulders,
upper arms, hands and upper legs: `x`, `y` as fractions of the overlay's
drawing, +y down), `animation` (the VRMA clip that plays now: `name`, `time`
and `duration` in seconds and `weight`, how much of the body it has, 0 to 1;
null when none) and `clips` (the names of the clips the character keeps).
`summary` gives `found`, whether it stayed `idle`, the
`inhale`, `perMinute`, `arms`, `curl` and `sway` ranges (`least`, `most`),
the `animations` seen playing with their `animationWeight` range, and how far
each bone `moved` (`x`, `y`). Over 20 readings 250 ms apart, a VRM breathes
in from 0 to about 1 at 13 to 16 breaths a minute, with its arms 15
to 18 degrees from straight down, its elbows bent about 16 degrees and its
fingers curled about 60 degrees. `MoveAvatar`'s value in `ui_snapshot` shows
the last reading as `pose`.

**VRMA clips on the character**: a VRM plays VRM Animation clips (`.vrma`; see
*VRMA clips on a VRM* in [Emotes and motions](../AVATARS.md#emotes-and-motions)).
`character_animation` gives the showing character a clip from a file on this
PC through UI Automation (`MoveAvatar`'s value `"animation:{...}"` with
`path`, `name`, `play` and `inPlace`) and waits up to 10 seconds for the
answer. It changes the character, so it needs `--allow-ui-effects`. Its
arguments:
- `path`: the full path of a `.vrma` file (at most 4 MB and 120 seconds). The
  renderer reads it, checks it and keeps it as `name` while the model stays
  loaded (at most 16 clips; the oldest goes first).
- `name`: the clip's name, 1 to 128 characters (default: the file's name).
  Without `path`, the tool plays the clip the character keeps by that name.
- `play` (default true): play the clip once. It blends in over the idle pose
  and gives the body back to the idle pose, breathing and sway by its end.
- `inPlace` (default true): the hips stay at rest across the ground and keep
  only their up and down movement, so a clip that walks or drifts keeps the
  character in its window.
- `samples` (1 to 60) and `gapMs` (default 250): afterwards, read the pose as
  `character_pose` does (`pose`), to see the clip play.
It returns `played`: `n`, `name`, `kept`, `started`, `duration` (seconds),
`clips` (every clip kept), `file` (the file's name, never its folder) and
`reason` when it didn't start (the character isn't a VRM, no clip has that
name, or the file isn't a usable clip, such as a `.vrm` model). A file that
can't be used never stops the character. `MoveAvatar`'s value in
`ui_snapshot` shows the last answer as `animation`. For example, on a VRM:
1. Call `character_animation` with the `path` of a 10-second clip and
   `samples` 8, `gapMs` 0.
2. Check that the readings show `animation.name` and a rising `time` at
   `weight` 1, and that the arms follow the clip.
3. Check that, after the clip's length, `animation` is null and the arms hang
   15 to 18 degrees from straight down again.
4. Call it again with only `name`: the kept clip plays again.

On this PC, each pose reading takes about 2 seconds, so sample a clip that is
longer than 2 seconds to see it play.

**Who moves the character's mouth**: while Martlet speaks, its voice has the
character's mouth, and an emote's mouth comes back about a second after the
voice stops (see *The voice has the mouth* in
[Emotes and motions](../AVATARS.md#emotes-and-motions)). `character_mouth` reads
it through UI Automation (`MoveAvatar`'s value `"mouth"`), `samples` times (1
to 60) `gapMs` apart (default 250). Each reading in `mouths` has `n`, `found`
(false before a model shows), `renderer`, `parameter` (Live2D: the parameter
read, `ParamMouthOpenY` when the model has it), `voice` (0 to 1: how much the
voice has the mouth), `speaking` (the voice moved the mouth within the last
second), `level` (the voice's loudness on it), `emote` (how far the emotes
open the mouth before the voice takes it; a VRM: its held open mouth), `open`
(how far the mouth is open now, 0 at rest to 1) and, for a VRM, `blocked` (how
much the expressions showing block its mouth expressions). `summary` gives
`found`, the share of readings `speaking`, the `voice`, `level`, `emote`,
`open` and `blocked` ranges (`least`, `most`) and the `last` reading. Reading
changes nothing, so it needs no `--allow-ui-effects`.
With `levels` (1 to 400 loudness levels from 0 to 1), the mouth first moves as
Martlet's loudness lip-sync moves it, one level every `stepMs` (10 to 1000,
default 50), without a sound (`MoveAvatar`'s value `"voice:ms;level;..."`).
The readings start at once. This changes the character, so it needs
`--allow-ui-effects`. For example:
1. Hold `mouth_open` with its `CharacterActionTry-<n>` button.
2. Send 20 levels with 40 samples 100 ms apart.
3. Check that `open` follows the levels while `speaking`, stays near 0 in a
   pause, and goes back to `emote` about a second after the last level.

`MoveAvatar`'s value in `ui_snapshot` shows the last reading as `mouth`.

**Where the character looks now**: `character_look` reads it through UI
Automation (`MoveAvatar`'s value `"look"`), `samples` times (1 to 60) `gapMs`
apart (default 250), as the overlay last turned the head and eyes (it does
every 50 ms; see [Where the character looks](../SCREEN_COMMENTARY.md#where-the-character-looks)).
Each reading in `looks` has `n`, `target` (`mouse`; `window`: where you work in
the window you're using; `point`: a spot Martlet asked it to look at; or
`ahead`), `x` and `y` (the direction, -1 to 1, +x right, +y up), `at` (the
point on the desktop in screen pixels, or null), `usual` (the usual gaze),
`window` (the window you're using: `left`, `top`, `width` and `height`, never
its title; null when none) and `watching` (for the window gaze: `pointer`,
`text cursor` or `middle`). `summary` gives the `targets` and `watching` seen
and the `x` and `y` ranges (`least`, `most`). Reading changes nothing, so it
needs no `--allow-ui-effects`. While the Windows session is locked, no window
is in front and the mouse can't be read, so the window gaze keeps the last
window it knew, or reads `ahead` with no `window` when it knew none.
`MoveAvatar`'s value in `ui_snapshot` shows the last reading as `look`.

**Moves, zooms and other changes Martlet hears about**: the overlay notes each
drag, arrow-key nudge, `ui_move`, zoom (wheel, menu, keys or Martlet's zoom
buttons), reset zoom, pan of a zoomed view and Reset position, and once it has
settled for 0.8 seconds sends one `physical` message (`moved`, `home`,
`zoomed`, `zoom_reset` or `panned`, with how far and which way it moved, the
monitors before and after, the character's size before and after, and the
renderer page's hit test of what a zoom closed in on or a pan centers on).
Martlet records it in the touch ledger with Locked, Unlocked, Hidden and Shown
(from its own buttons and the overlay menu): *They moved you to their other
monitor*, *They zoomed in on your face*. A move (`moved` or `home`) starts a
short reply of its own like a touch (`TouchZonesNoticed` says when); the
others never do and go with the next one. `MoveAvatar`'s value carries the last one as
`physical` (`kind`, `dx`, `dy`, `from`, `to`, `zoomFrom`, `zoomTo`, `focus`),
and `CharacterPhysicalLast` shows the ledger's words. The camera view's own
framing is not reported.

Companion › *Emotes and motions* (`CompanionTab-Emotes`) lists the
[emotes and motions](../AVATARS.md#emotes-and-motions) of the character this PC
shows (or would show). `CharacterActionsStatus` reads how many emotes and
motions the model has (with the Martlet gestures its rig supports) and whether they were named
by the Thinking model (and when) or from the model's own files, or why they
couldn't be read; `CharacterActionsNaming` the Thinking model's naming
(*Asking the Thinking model...*, *The Thinking model named 10 emotes and motions
at 3:12 PM and turned off 3 that aren't feelings or gestures.*, or why it
couldn't, such as *Thinking isn't set up yet*); `CharacterActionsOffered` the
tags replies get with the voice chosen now and which follow the voice's cues;
`CharacterActionsLast` the last one played (*Played the expression "脸红" for
{blush} at 3:14:05 PM.*, *... for a try ...*, or *The character couldn't play
...*; for a gesture followed by what the renderer now plays and every gesture
and drawing it holds, *Gestures now: wink playing, eyes_up, mouth_open, blush,
hearts held.* (held gestures layer; see
[Layers](../AVATARS.md#emotes-and-motions)); an emote Martlet drew over the face itself, such
as the blush glow on a model without a blush of its own, or `blush_deep` and
`blush_fierce` on any model, adds *drawn by Martlet
over the face at 414, 88 (50 pixels wide, tilted 3°, pinned to the face's meshes)* with the face's middle and
width in the overlay's page pixels, the head's roll (clockwise) and how the face is followed (*pinned to the
face's meshes* for Live2D, *following the head bone* for VRM, or *estimated from the head's angles* for a
Live2D model without face meshes to pin to; a Live2D estimate's roll is a damped share of `ParamAngleZ`, at
most 12°; see *Where Martlet draws over the face* and `character_face`), or *(not in view now)* when the face can't be found
or faces away), also in `logs_tail` `desktop` as *Character expression '脸红'
played for {blush}.* (*Character gesture 'blush' played for a try, drawn by
Martlet over the face at ...*, a gesture's line ending with the same *Gestures
now: ...*); and `CharacterActionsSaveState` *All changes saved.* or *Not saved:
<why>*. Row `<n>` (as in `character_actions`) has `CharacterActionName-<n>`
(its name and kind; a status field), `CharacterActionOn-<n>` (check box),
`CharacterActionTag-<n>` (an English tag; a tag in another script reads *Not
saved: ... use up to 24 English letters (a-z) ...*), `CharacterActionCue-<n>` (combo box: `(none)` or a
cue such as `laugh`), `CharacterActionUse-<n>` (the When to use box),
`CharacterActionHint-<n>` (the grey
hint in that box while it is empty, the text replies get then, such as *nod, for
yes or agreement* or *the character's emote named "Glasses"*; hidden once the
box has text), `CharacterActionMode-<n>` (the
*Stays on* check box, its mode as `checkedState`: on for a lingering emote) and `CharacterActionTry-<n>`
(plays it on the showing character, or turns a lingering one on; its label, a
status field, reads *Turn off* while that lingering emote is on, and clicking it
then turns it off; disabled while it is hidden). `CharacterActionsHeld` reads
the lingering emotes on now, all of them, held gestures and drawings included
(*On now: Glasses (12 min), Blushing (just now).
Clear emotes on the character's menu turns them off.* or *No lingering emotes
are on.*); `CharacterActionsLast` then reads *Turned on the expression ...* or
*Turned off ...* (for a gesture with the renderer's *Gestures now: ...*, which
shows the others still held), and `logs_tail` `desktop` *Character expression 'Glasses' held
for a try.* `CharacterActionsClear` (*Clear emotes*) and the character overlay
menu's `CharacterClearEmotes` turn every lingering emote off (*Cleared 2
lingering emotes for Clear emotes ...*).

**Notification area.** Closing the main window keeps Martlet running in the
notification area by default, and Martlet started with `--tray` (Start with
Windows) shows no window, so `ui_connect` also attaches when only the icon's
window exists (it returns `inTray`). `ui_tray` drives the icon:
`{"name":"ui_tray"}` (or `"action":"status"`) returns `running`, `trayIcon`
(the icon is in the notification area), `mainWindowVisible`, `inTray`,
`menuOpen` (the icon's menu is open) and `menuBounds` (the open menu's
`[x, y, width, height]` in physical screen pixels, null when closed);
`"action":"open"` and `"action":"menu"` post the icon what Explorer
sends for a left click (show Martlet) and a right click (its menu), at the
mouse pointer or at optional `x`, `y`, in physical screen pixels as Explorer
reports them whatever Martlet's display scale, and let Martlet take the
foreground as Explorer does when the MCP server may itself, so they need no
flag. The menu opens beside that point (its corner on it, flipped to stay on
the screen) even when Martlet's display scale differs from the monitor's (the
scale changed after Martlet started, or a monitor with another scale): to
check that here, launch the desktop with `__COMPAT_LAYER=DPIUNAWARE` set on a
display scaled above 100% and compare `menuBounds` with `x`, `y`.
`"action":"close"` presses the main window's
close button, which hides Martlet or (with *Keep running when closed* off) exits
it, so it needs `--allow-ui-effects`. While the menu is open `ui_snapshot` lists
`TrayMenu` and its items: `TrayStatus` (status text: *Martlet is running*,
*Martlet is listening*, *Martlet is paused*, *Martlet is watching* or *Martlet:
talk window open*), `TrayOpen`, `TrayTalk` (*Talk to Martlet*, or *Show the
talk window* while it is open), and while the talk window is open `TrayPause` or
`TrayResume` and `TrayEndTalk`, then `TrayCharacter`, the checkable
`TrayCharacterClickThrough` (while the character shows or click-through is on;
see the character overlay above), the checkable
`TrayCloseToTray` and `TrayStartWithWindows` (their `checkedState` is the
current choice) and `TrayExit`. Each item reads as its label in `value`
(*Start listening*, *Pause Martlet*, *Exit Martlet*...). The menu, like text boxes' Cut/Copy/Paste
menus, is drawn in Martlet's palette (Themes\Controls.xaml), with no light icon
column in the dark palettes; `ui_snapshot` returns the palette as `AppearanceTheme`
(*Pink light*, *Rose dark*, *Character light*, *Character dark* or *Custom*; choosing one with
`ui_select` saves `appearance.txt`, so it needs `--allow-ui-effects`) and
Settings' line about it as `AppearanceStatus`. Settings › Appearance also has
`AppearanceCharacterStatus` (the character's colors: how many and where the
accent comes from, or why they couldn't be read; never its name),
`AppearanceColor-<n>` (each main color: *#2B3440 31% dark grayish blue*),
and `AppearancePreview-rules-<light|dark>` (each character palette's colors by
role). While *Custom* is chosen, the custom palette's editor shows:
`CustomThemeRole-<role>` (each of the twelve parts with its color, for example
*Accent: #A52D64*; clicking one only selects it, so it needs no
`--allow-ui-effects`), `CustomThemeRoleName` and `CustomThemeRoleHelp` (the
selected part), `CustomThemeHex` (its color code), `CustomThemeHue`,
`CustomThemeSaturation` and `CustomThemeLightness` (sliders: degrees and
percent), `CustomThemeHexHint` (shown when the typed code isn't a color),
`CustomThemePick-<n>` (the character's colors: *Use #6F92D1 blue*),
`CustomThemeBase` (the palette *Start from* names), `CustomThemeCheck` (*Easy
to read: ...* or what may be hard to read, such as *Text on window background:
1.3:1 (needs 7:1)*) and `CustomThemeFix` (*Make it easy to read*, shown only
when something is hard to read). `ui_set_text` on `CustomThemeHex`,
`ui_set_range` on a slider and `ui_click` on a `CustomThemePick-<n>`,
`CustomThemeFix` or `CustomThemeStartFrom` change the palette and save
`appearance-custom.json`, so they need `--allow-ui-effects`.
`CustomThemeStartFrom` asks `CustomThemeStartFromQuestion` first when the
colors were changed from every other palette (`ConfirmationYes` replaces them).
The
desktop log records *Read N colors from the character's textures.* and
*Applied the Character dark palette (#D194AE accent on #161E24).*
`character_theme` makes the same colors and palettes headlessly.
On a Martlet host (Settings › *Use as a Martlet
host*) the menu has no `TrayTalk`, `TrayStartListening`, `TrayStartWatching` or `TrayCharacter`: a
host doesn't talk, listen, watch or show the character. With always listening the menu
has `TrayStartListening` (*Start listening*) or, once started, `TrayStopListening`
(*Stop listening*); with vision on in Companion it also has `TrayStartWatching`
(*Start watching*) or, once started, `TrayStopWatching` (*Stop watching*), each
working without the other. `TrayOpen`, `TrayTalk` (like
`OpenLiveConversation`) and `TrayEndTalk` (like `CloseLive`) are passive
clicks; `TrayPause`, `TrayStopListening` and `TrayStopWatching` (they pause
Martlet until you turn it on again), `TrayStartListening`, `TrayStartWatching`,
`TrayResume`, `TrayCharacter`, `TrayCharacterClickThrough`, the two
choices and `TrayExit` need `--allow-ui-effects`. The menu's status line reads
*Martlet is listening and watching* when both run. While another Martlet dialog
(Setup, Companion...) is open, `TrayTalk` and `TrayCharacter` are disabled and
`TrayExit` shows Martlet instead of exiting (the status line names the open
window to close first). Settings › *Startup and closing* has
`CloseToTray` (checked by default; saves `background.json`), `StartWithWindows`
(the per-user Run entry `Martlet`: this executable, the same `--data-directory`
and `--tray` when `StartInTray` is checked; verify with a disposable data
directory and turn it off again afterwards), `StartInTray`, `StartCompanion`
(*When Martlet starts, show the character and start listening (and watching,
while vision is on)*; saves
`background.json` and applies on every start, `--tray` included, so the
desktop log records *Martlet started with the character and listening.*, or
*... and listening, and watching.* with vision on),
`GreetAtStart` (*Greet me once Martlet has warmed up after it starts*; checked
by default, saves `background.json`, so toggling it needs
`--allow-ui-effects`) with `GreetingStatus` under it (*Martlet greets you once
a conversation has warmed up (Start talking or Start listening on Home).*,
*Martlet greets you once it has warmed up.*, *Martlet greets you as soon as
it's free, and it knows ...*, *Martlet is greeting you.*, *Martlet warmed up at
5:08 PM and greeted you, knowing the time of day, your name, the update and 3
open apps.*, *... and stayed quiet because your character wouldn't greet you (...)*,
or why it didn't: the greeting was off, you were already talking,
or it was let go; counts only, never your name, app names, reminders or what was said;
the talk window's task list shows the job as *Greeting* and the log says
*Greeting: Martlet warmed up; greeting-1 waits...*; seed `greeting.json` in the
disposable data directory with an older `Version` to see *the update*) and
`BackgroundStatus`
(status text: what closing does, and whether Windows starts Martlet, including
when Windows' own Startup apps switch turned it off). On a PC that runs its own
host service, `StayAwakeStatus` (hidden on other PCs) says whether Martlet keeps
the PC awake: while that host service answers and serves another computer (the
computers paired with it, as its network sync reports them every 20 seconds,
window shown or not), Martlet holds a Windows power request
(`PowerRequestSystemRequired`, reason *Martlet: this PC's host service <id>
serves <names>*, listed by an administrator's `powercfg /requests`) so idle sleep
doesn't take the host off the network; the screen can still turn off and Sleep
or shutting down by hand still work. A host service that misses a check keeps
the request for 2 minutes. A Martlet host PC holds it whenever Martlet runs,
from the moment it starts and whether its host service serves anyone or not
(reason *Martlet: this PC is a Martlet host (<id>)*). The desktop log records
*This PC stays awake while its host service <id> serves <names>...*, *This PC
stays awake because it is a Martlet host...* and *This PC can sleep again when
it is left idle...* on each change. A host PC whose host service serves nobody
(a disposable data directory holding `device-role.txt` with `Host`, for
example) reads *This PC stays awake because it is a Martlet host...*; a
companion PC whose own host service serves nobody reads *This PC can sleep when
it is left idle: its host service serves none of your other computers right
now...*. On a Martlet host
`StartCompanion` is disabled but keeps its saved state, `BackgroundStatus` says
the character and listening don't start there, and the log records *Martlet
started as a Martlet host: the character and listening stay off on this PC*
instead (the character's *Show at startup* and Parakeet's warm-up are skipped
too). Choosing `UseAsHost` (Settings › *What this PC is for*; it saves
`device-role.txt`, so it needs `--allow-ui-effects`) ends a running
conversation and hides the character, logging *This PC became a Martlet host,
so Martlet ended the conversation...*. `UseAsCompanion` on a host PC (the
host dashboard's `SwitchToCompanion` at the top of Home does the same; its label
reads *Switch to companion PC*), or
another computer's ask that it be a companion PC again, brings back at once what
was on when it became a host: the character, always listening and watching
(*This PC is your companion PC again, so Martlet brings back what was on before
it became a host: showing the character.*); after Martlet started as a host it
does what it does at the start of a companion PC instead (the character's *Show
at startup*, `StartCompanion`). It changes no saved companion choice. `DeviceRoleSummary`
(*Companion PC* or *Host PC*) and `DeviceRoleText` return the role as text.
`DeviceRoleWhere` says where the choice is kept and who shares it: *Every
Windows user of this PC shares this choice and this PC's host service
(%ProgramData%\Martlet).* for the default data folder, *This data folder keeps
this choice...* for a disposable one, and whose Docker Desktop Martlet last
read this PC's host service in (this or another Windows user); `pc_scope`
returns the same headless. A second start with the
same data directory shows the running Martlet and exits (with `--tray` it only
exits); a different `--data-directory` runs beside it, so disposable
verification desktops never reach your own Martlet. `-DesktopArguments '--tray'`
on `scripts\Invoke-MartletMcp.ps1` starts the disposable desktop in the
notification area.

**Exiting.** `ExitMartlet`, `TrayExit` and (with *Keep running when closed*
off) `ui_tray` `close` exit Martlet, so they need `--allow-ui-effects`. An exit
that would cut work short (backup and restore, a setup task other than a reply,
a troubleshooting report being made or waiting to be exported, a command
from another computer, a running run window, shown or hidden in Background
tasks and listed as *<run> (in Background tasks)* (for example an update,
Parakeet or cloudflared download, a host update, reconfiguring your computers or
a change another computer asked for), or *Prepare this computer*) waits
up to 1.5 seconds for quick work to finish, then shows the window and asks in
an *Exit Martlet* confirmation whose `ExitBusyQuestion` lists what Martlet is
still busy with: `ConfirmationYes` (*Exit anyway*) interrupts it,
`ConfirmationNo` (*Keep Martlet open*) keeps Martlet running and logs *Status:
Martlet stays open...*. Windows signing out never asks. While Martlet closes,
`ClosingPanel` covers the window (the rest is disabled) and `ClosingStatus`
names the step (*Stopping your tool servers...*, *Ending the conversation...*,
*Closing the character...*); the tray icon's tooltip says *Martlet is closing:
<step>*. After three seconds the window shows even from the notification area
(clicking the icon, its menu or starting Martlet again shows it at once),
`ClosingSlow` says what exiting without waiting leaves undone and
`ClosingExitNow` (or closing the window again, `ui_tray` `close`) opens *Exit
Martlet now*, whose `ExitNowQuestion` names the step: `ConfirmationYes` exits
at once (logging *Exited without waiting: Martlet was still <step>*),
`ConfirmationNo` keeps waiting. A step that fails is logged (*While exiting,
<step> didn't finish; Martlet exits anyway.*) and closing goes on. Setting
`MARTLET_SIMULATE_SLOW_EXIT` to a number of seconds (1-600) before launching
the desktop adds a last step that only waits that long (*Waiting on a simulated
slow step*), to check the closing panel and Exit now.
