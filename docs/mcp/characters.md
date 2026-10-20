# MCP: Characters

Part of [MCP in Martlet](../MCP.md): the tools and screens of the *Characters* area of `martlet_guide` (making, changing and checking characters: personalities, looks, voices, motions and touch). The [index](../MCP.md#pages) lists the other pages.

## Tools while you talk (MCP client)

### Motion

Martlet's optional `motion` host role makes body motions for VRM characters
with HY-Motion 1.0 and returns VRMA clips ([Motion host](../MOTION_HOST.md)).

`motion_status` (with `dataDirectory`, and optionally `deviceId`, the companion
PC to answer for; files only, read-only) returns `pool` (the Motion list on
Companion › Motion, the `motion` area of `pools.json`: `listed`, `from` (the
list, or motion.json while no list exists), `off`, `members` with their `key`,
`kind`, `name`, `on`, `onlyFor` and `usableHere`, `device`, `ownHost`, and
`tries`: the computers this PC tries in order with the production planner
`MotionPool.Members`, each with whether it is paired here),
`settings` (motion.json, the older shared `motion` setting: `file` state, `place`
`Automatic`, `Off` or `Host`, `hostId`, `on`, `isDefault`, `describe`),
`planHosts` (the computers the shared plan, `cluster.json`, last saw running
the motion role, with their model and whether this PC is paired with them),
`paired` (this PC's pairings in `hosts.json`, without hosts a friend shares),
`motionHost` (the computer a motion tries first, or null), `fixtureVariable`
(`MARTLET_MOTION_FIXTURE`), `route` (`martlet.gateway.motion.v1`, its path and
the longest a request may take), `role` (the two models and their graphics
memory minimums) and `bounds` (the request bounds of `MotionRequest`).
`work_sharing_status` lists the Motion list with the other pools.

`motion_pool_check` (no arguments) rehearses the motion pool (`MotionPool`
through `WorkQueue`, what the desktop's `PoolMotionMaker` runs) with simulated
motion computers, NOT real hosts or models. It returns `ok` and each step: the
list's order (members on, paired here and kept for this PC; This PC as its own
host service; an empty list or none on is off), the older choice before a list
(automatic: every computer running the role, this PC's own first; one computer;
off), the first list made from it, a free first computer taking the motion with
no other asked, a busy one (`motion.busy`) or one not answering passed over,
every one busy waiting for the first to free, a motion that fails on its
computer not made again elsewhere, and an empty list refused
(`motion.unavailable`).

`motion_check` makes one motion from `prompt` (default *A person waves with
the right hand, then bows.*), `seconds` (1-12, default 3) and `seed` (default 7)
and returns `ok`, `place`, `where`, `fixture`, `engine`, `model`, `frames`,
`fps`, `seconds`, `seed`, `bytes`, `sha256`, `vrma` (`specVersion`,
`humanBones`, `channels`, `keyframes`, `lastKeySeconds` from the clip's glTF),
`took`, `status` and `freed`. `place`:

- `fixture` (default): the FIXTURE - NOT AI wave maker (`FixtureMotionMaker`).
- `worker` with `endpoint` (a numeric loopback address, such as
  `http://127.0.0.1:50090/`): the Motion role's worker itself (`GET /status`,
  then `POST /generate`). A worker started with `martlet-motion serve --listen
  0.0.0.0 --random-weights 2` (for example `docker run -p
  127.0.0.1:50090:50090 martlet-motion:1 serve --listen 0.0.0.0
  --random-weights 2`) runs HY-Motion's real code with RANDOM weights on the
  processor (FIXTURE - NOT AI: `fixture` is true and the motion is noise).
- `host` with `dataDirectory` (and `hostId`, or the first computer of the Motion
  list that `motion_status` names): through that paired computer's gateway route, as the desktop asks it
  (`status`, then `generate`).

`free` unloads the model afterwards (`POST /free`, or the route's `free`).
`saveDirectory` (absolute) writes `motion.vrma` there. An invalid prompt or
seconds is a tool error. A real motion needs an NVIDIA GPU with 24 GB or more.

### Body motions

While motions are set up (the Motion list has a computer that is on and paired
here, or, before the list, the shared `motion` choice names one; or
`MARTLET_MOTION_FIXTURE=1`) and the chosen character is a
VRM, every reply on a route that does function calling also gets `body_motion`
(`name`, and for a new motion `motion` and `seconds`), last of Martlet's own tools.
A tag the character has plays at once; a new motion starts a quiet `motion-N`
background job and returns at once, and the motion is kept as a `motion` creation
(out of the creation tools) and played on the character when it is ready. Without
motions or a VRM, the tools are exactly as before. See [Motions in
conversation](../AVATARS.md#motions-in-conversation). The talk window's status says
*Moving…* while it is called, and the desktop log notes `Motions:` lines (name,
size, seconds, where; never the description).

The local server's `kept_motions_status` reads a data directory's kept motions (name,
seconds, size, fixture, starter, model, where, whether the clip is on this PC;
never the prompt's words), whether this computer saw the starter motions made,
whether `motion.json` is saved and the fixture switch is on, the `body_motion`
tool, its quiet job kind and the motion creation kind (`forTools` false); with
`model` (an absolute path to a `.vrm`) it lists the kept motions as that model's
emotes and motions with their tags and whether replies are offered them.
`kept_motions_check` makes one motion (`name`, `motion`, `seconds`), or with `starters`
Martlet's five starter motions, with the FIXTURE - NOT AI motion maker and checks
it the way Martlet keeps and plays it: the clip checked as the VRM renderer checks
it, kept as a motion creation in `dataDirectory` (a disposable one when left out)
and read back, the creation tools' words unchanged and never listing it, and the
motion listed as a VRM's motion with its reply tag (`model`, or a FIXTURE VRM). It
returns `ok`, each step and the motions with their ids, renderer clip names
(`kept-<id>`) and the note the conversation gets. It never uses a motion computer;
`motion_check` (above) does.
`creations_check` also checks that a kind kept out of the creation tools leaves
their words exactly as they were.

Desktop automation: on Companion › Emotes and motions for a VRM,
`CharacterMotionStatus` (whether body motions are set up, how many are kept and
how the starter motions went, or how making one goes) is a safe value. Typing
what the body does (`CharacterMotionDescription`) and a name
(`CharacterMotionName`) is `ui_set_text`, and **Make** (`CharacterMotionMake`)
uses the motion computer and keeps a motion, so it needs `--allow-ui-effects`. A
kept motion is then a row (`CharacterActionName-<n>`: `cheer  ·  kept motion`)
whose **Try** (`CharacterActionTry-<n>`) plays it; `CharacterActionsLast` says
what played, and `character_pose`'s `animation` names the clip (`kept-<id>`).

## Local MCP control (Windows)

`f5_voices` lists Martlet's [starter voices](../F5_VOICE.md#desktop-voices-and-playback)
as `starters` (key, name, `female`, `cute`, description, licence, transcript,
SHA-256, sample rate and duration; each clip is checked against its SHA-256 and the
reference store's audio, name and transcript rules, `valid` or the failure; a new
voice list starts with them, after which they are ordinary voices), the
`default` key, `defaultName`, `defaultFemale` (true) and `defaultCute` (false;
the default is the first starter voice, `jenny-dioco`), `cute`, the
keys of the two cute voices (`librivox-annie` and `librivox-woollybee`), and `retired`, the recordings Martlet no
longer ships (`key` and `name`: `retired-sample`, the F5-TTS example clip, and
`retired-librivox-annie-anime` and `retired-librivox-woollybee-anime`, the
former "anime" voices). From a data
directory (optional absolute `dataDirectory`, default the current user's) it reads
the shared voice list as `library` (`speaking-voices.json`; `state` `none` until a
voice is first used, added or removed or the desktop shares voices with a host,
`loaded` or `unreadable`): the number of live `voices`, `revision`, the keys of
the starter voices in it (`starters`), the keys of former starter voices it still
lists (`retired`: an older Martlet's list, empty once this desktop brings its
voices in step), the count of the owner's own voices
(`own`), tombstones (`removed`) and the keys of removed starter and former
starter voices (`removedStarters`), and the voice chosen on all computers
(`chosen`: a starter key, a retired key, `own` or null) with the device that chose it (`chosenBy`), and
`severalRecordings`: each voice made from several recordings, by the key the
Voices page uses (the first 16 hex digits of its ID), with how many `recordings`,
each one's length (`clipMs`), the joined `durationMs` and `sampleRate`, the
`engines` that can clone it and those that learn from each recording
(`learnsFromEach`; the others hear them joined). `list` is this PC's
recordings (the `f5-voices` store): `state` (`none`, `loaded`, `busy` while the desktop holds it,
or `unreadable`), the number of voices, the keys of starter voices in it (`starters`), the
count of the owner's own voices, the keys of retired recordings still there
(`retired`; each is deleted once nothing speaks with it) and the applied voice
(a starter key, a retired key, `own` or null).
`speaking` reads `settings.json`: `state` (`none`, `loaded` or `unreadable` with
the settings rule it broke or the error type as `problem`), the
speaking route's type (for example `GatewayF5`, null without one) and the voice it
records (a starter key, a retired key, `own` or null), plus `engine` (the
self-hosted voice engine whose route it records: `chatterbox`, `chatterbox-original`, `chatterbox-nano`, `f5`, `xtts`, `gpt-sovits` or `dia`), `host` and
`model` for a host route. `engines` lists the voice engines
([Chatterbox Turbo, Chatterbox Original and Chatterbox Nano](../CHATTERBOX_VOICE.md), [F5-TTS](../F5_VOICE.md),
[XTTS-v2](../XTTS_VOICE.md), [GPT-SoVITS](../GPT_SOVITS_VOICE.md), [Dia](../DIA_VOICE.md); `key`, `name`,
`hostRole`, `routeId`, `path`, `model`, `weightsLicence`, `minimumGpuMemoryGb`
(0 for Chatterbox Nano, which also runs on the CPU),
`minimumReferenceMs`, `maximumReferenceMs`, `summary`, `default` (true for
Chatterbox Turbo), `supportsTags` and `tags`, each tag's `text` in the engine's
syntax, `kind` `Sound` or `Emotion` and `usage`, and `multipleReferences` (true
for XTTS-v2 and GPT-SoVITS, which learn from each of a voice's several
recordings); Chatterbox clones only
recordings longer than 5 s, GPT-SoVITS only 3,000-10,000 ms), `features` (its
other needs: Docker, recording lengths, streaming, languages), `runsOn` (where
it runs: `on` `gpu`, `cpu` or `online`, `vramGb` its typical and `peakVramGb`
its most graphics memory, `minimumGpuGb` the smallest card, `evidence`
`Measured`, `Sourced` or `Estimate`, all from the footprint catalog
([resource footprints](../RESOURCE_FOOTPRINTS.md)), and `text`, the line
Companion › Voice shows) and `abilities`, the rundown Companion › Voice shows for it:
`cloning` and `sounds` (true or false), `emotions` (`None`, `WhisperOnly`,
`Intensity` or `Tags`), `items` (`Voice cloning`, `Laughs & sighs` and
`Emotions`, each with `level` `Yes`, `Partly` or `No`, a `note` when partly, its
`help` sentence and the `tags` that do it) and `summary` ("Voice cloning: yes;
Laughs & sighs: yes; Emotions: whispering only." for Chatterbox Turbo, whose
tones other than `[whispering]` don't change the voice and whose `[whispering]`
the model itself whispers only now and then). `otherVoices` gives the
same `abilities` and `runsOn` for the `openai` (online) voice, which doesn't
clone, laugh or show emotions (Windows voices were removed). Each starter
voice adds `engines` (the engines that can clone it) and `language` (`en` or
`ja`, read from its transcript), `chosenEngine` is the engine chosen on this
desktop (`speaking-engine.txt`, default `chatterbox`) and `chatterboxStyle` is
Chatterbox Original's style saved there (`saved`, `generalExaggeration`,
`generalCfgWeight`, `expressiveExaggeration`, `expressiveCfgWeight` and
`summary`; Resemble's suggestions until Companion › Voice saves one). After the desktop
loads settings, a route or applied voice that was a retired key reads the
chosen or first voice. It never returns own voices' names, transcripts or audio, plays
nothing and contacts nothing.

`character_models_selftest` (no arguments) rehearses the
[shared character models](../CLUSTER.md#the-shared-character-models) end to end
with the production code: two real gateways on 127.0.0.1 (pinned TLS, in-memory
`character-models.json` and pieces) and three simulated desktops that keep their
character copies in a temporary folder and use the desktop's paired client and
Martlet.Avatar.Hosting's import and reconcile engine. The fixtures are generated
bytes in the Live2D folder and VRM file shapes (NOT real models; nothing is
rendered). It runs `src\Martlet.NodeLinkCheck` (mode `characters`,
`CharacterRehearsal.cs`) and returns `{exitCode, report}` like
`network_selftest`. Its steps: adding a Live2D model (its model3.json declares a
7 MiB file in three pieces, a texture and a motion in subfolders; a readme beside
it stays behind) and a VRM keeps copies the
renderer's own file reader reads exactly like the originals; the list and every
3 MiB piece reach a host, each piece in one signed request; a new, empty desktop
copies both characters byte for byte; a copy interrupted after three pieces
continues with only the rest; a desktop passes the characters on to a host the
first desktop never reached; removing a character deletes its pieces on both
hosts and the other desktops' copies; a stale copy can't bring it back; a
computer keeps the copy it shows until another is chosen; a host restart keeps
the list and pieces; a wrong SHA-256, a piece no character has and a removed
character's piece are refused; reading a missing piece answers none; character
1,001 is refused, a merge with another computer's list past that keeps all 1,001
characters, and a Live2D model that refers to a script is refused. Nothing leaves
loopback, the temporary folder is deleted and Windows Credential Manager is not
touched; it does not cover the desktop window and its 30-second sync, the Linux
host's files, rendering a copied model or a real LAN.

`character_models` reads the shared character models from a data directory
(optional absolute `dataDirectory`, default the current user's): `state`
(`none`, `loaded` or `unreadable` for `character-models.json`), `live`,
`tombstones`, `totalBytes`, `revision`, and per live character its `key` (the
first 16 hex digits of its ID, as in `CharacterModelState-<key>`), `renderer`
(`live2d` or `vrm`), `files`, `pieces`, `bytes`, `addedBy`, `addedAt`,
`updatedBy`, `ready` (this PC's copy is complete) and `shown` (`avatar.json`
shows it); `copies` (folders in `character-models`), `incoming` (copies still
arriving: key and pieces so far) and `showing` (`built-in`, `shared:<key>`,
`unlisted-copy:<key>` for a copy removed elsewhere that this PC still shows, or
`model-file-outside-list`). Character names and file paths are never returned.
Read-only; it contacts nothing.

`character_profiles` reads the character profiles (Companion › Profiles) from a
data directory (optional absolute `dataDirectory`, default the current user's):
`state` (`no-settings`, `none` or `loaded`), `count`, `lastUsed` (the key of the
profile switched to last), `current` (the key of the profile that matches what
Martlet uses now: the active persona, the look in `avatar.json` and the voice the
speaking route keeps or the shared voice list chose; null when none does),
`look` (what this PC shows: `builtin`, `shared:<key>` or
`model-file-outside-list`), `voiceChosen`, and per profile its `key` (the first 8
hex digits of its ID, as in `CharacterProfileState-<key>`), `personaSaved`,
`personaActive`, `look` (`keep`, `builtin`, `ready`, `copying` or `missing`),
`voice` (`keep`, `listed` or `missing`) and `inUse`. It also reads what each
profile keeps on this PC (`character-profiles-local.json`, never shared):
`hereState` (`none`, `loaded` or `unreadable`), `hereInUse` (the key of the
profile whose choices this PC uses now: the one switched to here or followed
after a switch on another computer), and per profile `here` (null when it keeps
nothing here yet) with `place` (`locked`, `left`, `top`, `width`, `height` in
device-independent pixels and `screen`, or null), `gaze` (`personality`,
`mouse`, `near`, `ahead` or `window`), `gazeFree` and `touchInterrupts` (`any`,
`intimate` or `never`). Per profile, `sharing` is how the account in use shares
it with the household: `private`, `copy`, `together`, or `joined` (someone else's
character shared together that it talks to). It is null without accounts. Names
are never returned. Read-only; it contacts nothing.

`character_actions` reads a character model's
[emotes and motions](../AVATARS.md#emotes-and-motions) the way Companion ›
Character › Emotes and motions uses them: `modelPath` (a `.model3.json` or
`.vrm` on this PC) or the model `dataDirectory`'s `avatar.json` shows (with a
`dataDirectory`, its `character-actions.json` and edited prompts are used too).
It returns `renderer`, `key` (first 16 hex digits of the model's ID), `files`
(what the renderer reads, a VTube Studio model's `.vtube.json` and loose
`.exp3.json`/`.motion3.json` included), `expressions`, `motions`,
`fromVTubeStudio` (expressions and motion groups taken from outside the
model3.json, with their model-relative file names, and the `Idle` group made
from VTube Studio's idle animation), `saved`, `detectedBy` (`names` or
`thinking`), `actions` (each one's `n` as in `CharacterActionName-<n>`, `id`,
`kind`, `name`, `detail`, `tag`, `cue`, `use` (the When to use text, null while
the box is empty), `hint` (what the reply prompt says next to the tag: `use`, or
Martlet's own hint while `use` is null), `enabled`, `mode` (`brief`, or
`lingering`: stays on after `{tag}` until `{/tag}`), `modeSaved` (false while it
is the default), `vtsToggle` (a VTube Studio ToggleExpression hotkey turns it
on) and whether replies
are `offered` it for `engine`, a voice engine key, `none` or absent for a voice
without tags), `blushLevels` (the model's blush levels, faintest first: `level`
1 to 3 for `blush`, `blush_deep` and `blush_fierce`, with the row's `n`, `id`,
`kind`, `name`, `tag`, `mode` and `offered`; the first is the model's own emote
tagged `blush` when that replaces Martlet's blush), `replyPrompt` and `replyTags` (what replies get while the
character shows; lingering emotes add their `{/tag}` off tags) and `namingPrompt` (`instructions` and the numbered `list` the
Thinking model is sent). With `showing`, the lingering emotes the character
would show now (`["glasses", "blush:12"]`, minutes after the colon),
`showingNote` is the line the newest message's notes get (*Your character is
showing {blush} (12 min), ...*); it never goes in the instructions. With `voiceTag`, a voice's tag such as `[laugh]` or
`(sighs)` or a reply tag such as `{nod}` or `{/blush}`, `setsOff` lists what it sets off
(`kind`, `name` and `holds`, whether it lingers, such as the `laugh` voice emote; one expression and one
motion picked at random when several share a cue; for a combo's tag such as `{flustered}`, each of its parts that is on)
and `turnsOff` lists what an off tag turns off (the lingering emote, or a combo's lingering parts); `combo` is the combo
the tag names, or null. `combos` lists the model's [combos](../AVATARS.md#emotes-and-motions) (Martlet's own and the owner's): each one's `n`
as in `CharacterComboTag-<n>`, `tag`, `parts` (each part's `id`, `kind`, `name`, `tag`, `enabled` and `mode`; `kind`
is `missing` for a part the model doesn't have), `use`, `hint` (what replies get next to the tag), `enabled`, `lingers`
(a part that is on lingers, so `{/tag}` is offered too) and `offered`. `martletCombos` lists each of Martlet's own
combos: `tag`, `parts` (gesture tags), `use`, `startsOn` (false for `ahegao`), `given` (whether the model was given
it; `given_combos` in `character-actions.json`) and `n` (its row in `combos` while one has its tag, otherwise null,
such as after the owner removed it). With `combos`, strings such as
`flustered: blush hearts nod | when flattered` read the way the Combos section's boxes are read, those combos replace
the saved ones; when they can't be saved, `combosProblem` says why (such as *no emote has the tag 'wave'.*) and the
saved ones stay. With `answer`, a simulated Thinking reply such as
`1: blush | - | stays | when shy` (the mode may be left out), `parsed` shows what the production parser makes of
it (`read`, `problem`, `actions`, `prompt`). Model-authored names only, never
the model's path; it reads and contacts nothing else.

`character_touch_zones` rehearses Companion › Touch › [Touch zones](../AVATARS.md#touch-zones)
with no vision request: `zones` (how many Martlet knows, which are
`intimate`, used only with *Include intimate zones*, and the `defaults`
*Detect zones* looks for on every character, with `defaultParts` in words),
`request` (the step-by-step requests for the model: `wanted`, the zones a
detection looks for, which are the defaults and the ones the owner added
(`added`), and `required`, the ones it must end with; `parts` with the whole
character (its `ids`: head, upper_body and lower_body, and a tail, wings or
held item only when the owner added one), `zones` with each close-up (its
`regions` and the wanted zones each asks for) and `check` with the numbered
boxes, each with its `instructions` and an example `text`; `extras`, the
tail, wings or held item it looks for; `special`, what is special about the
character: the `maximum` zones of its own the model may add (6 by default),
its `instructions`, an example `text` and `before`, the special zones found
before with their `id` and `name`, whose IDs the model is asked to keep), `parsed`
(what the production parser makes of `answer`, a simulated vision reply about
the whole picture: JSON boxes as fractions or named edges (`left`, `top`,
`right`, `bottom`), pixels of a `width` × `height` picture, 400 × 800 by
default, or Qwen-style 0..1000 `bbox_2d` grounding; an answer cut off part way
keeps the zones it finished), `detected` (those zones
bound to `probe`, a simulated renderer zones probe of Live2D `drawables` (each
with the `part` it belongs to) and VRM `bones` in page fractions, and a Live2D
model's own `parts` (`id`, its DisplayInfo `name`, such as 头 or 右腿, and its
`parent`); a zone also takes the drawables of its own feature's part that lie
by its box, such as an eye's white and lashes outside a tight eye box; a
probe's `chains` (a Live2D model's swinging parts: each `name`, the physics
settings' names, its `drawables` from root to tip and its reach `left`, `top`,
`right`, `bottom` in page fractions) and `springs` (a VRM's spring-bone chains:
`name` and `joints`, each `bone` (its node's name), `x` and `y`) let a tail,
wings or animal ears follow all of their part, and the hair a ponytail; each
zone then has `follows` (the part's own names) and `areas` (each `box` as edges,
its `drawables`, `bones` and `nodes`, and `fromModel`, true for an area that
follows the model's own part)), with `crop`, `"left,top,width,height"` where
the snapshot sat on the page; `probePath` reads the `probe.json` that *Detect
zones* keeps with the pictures it sent, crop and all, or a bare probe), `hints`
(what the probe tells: how many parts the model has (`modelParts`) and names
(`namedModelParts`), the body parts its names place (`namedParts`; each of
`areas` with its `part`, the character's own `side` for one that comes in pairs
and its `box` as left, top, right and bottom fractions of the snapshot), the
close-ups' windows they give (`regions`: head, upper_body and lower_body, or
null), the `extras` its names place (a tail, wings or animal ears, which the
first guess and *Detect zones* add as zones special to the character), and its swinging parts: `chains` (each `name`, how many
`drawables`, the body `part` its name says, its `root` and `tip` drawable) and
`springs` (each `name`, how many `joints` and the `part` its name says)), `saved` (the model's zones in
`character-touch-zones.json`: how many, how many are `active`, who found them,
whether they were found with the character framed `whole`, the `crop` (where
the picture sat on the page with the character framed whole, as `left`, `top`,
`width` and `height` fractions; a `top` plus `height` above 1 means the picture
zoomed out to show parts the model draws past its own canvas, such as legs),
whether a snapshot
is kept and its `snapshotProbe` (how many `chains` and `springs` the probe kept
beside it has; the desktop binds the zones again with it when the owner moves
one), what the last detection `sent` (its plain `line`, `requests`,
`pictures`, `steps` and, from its `probe.json`, the `probe`'s hints as above) and
each zone's parts, its number of `areas`, what it `follows`, `plays`, its
`reactions` (the reaction list as saved, in play order: emote, gesture and
motion IDs and `sound:<cue>` voice sounds, every entry kept, also one the model
doesn't have; null for a zone not filled yet), its `seed` (the list a new zone
gets, which *Defaults* fills in), its `sounds` (the cues of its voice sounds),
whether
Martlet `notices` it, whether the owner `added` it, whether it is `special` to
the character (found as special, not added) and the owner's `hint`) and,
with `touch` (a `CharacterTouch` object as JSON; `wholeX` and `wholeY` are where
it lands with the character framed whole, `restX`, `restY`, `restWholeX` and
`restWholeY` where the touched point of the character was in its rest pose, now
and framed whole, as the renderer traces it on the touched mesh, and `hair`
whether the topmost drawable is hair), `match`: the zone it lands
in, `how` (`drawable`, `node`, `bone`, `hair`, `box` or `coarse`; a VRM spring-bone
joint or a drawable a zone follows by the model's own part, such as a tail's, is
that zone's first, before hair (cat ears hang from the head as hair does), and
reads `node` or `drawable`, with `traced` false; with a VRM `bone`, the
smallest zone on the part of the body that bone moves whose box holds the
point wins, and reads `bone` when it holds that bone, else `box`; a zone whose
box holds the point inside the box of a zone that owns the touched drawable,
with none of its own drawables under the touch, wins as its finer part and reads `box`;
without `hair`, a drawable lower in `drawables` that a smaller zone inside the
top drawable's zones owns wins, as an overlay over that part), `traced`
(whether the boxes were compared with the rest point), `at` (the point they were
compared with: `x`, `y` and `rest`, true when it is the rest point), `area` and
`areas` (for a zone with several areas, which one the touch landed in, from 0, and
how many it has; `area` is null for a zone with one), `follows` (what of the model
the zone follows, or null), `touched`
(every zone the touch lands in, the matched one first: where zones overlap, each
other zone in use with an area whose box holds that point, on the same part of the body as
the touch, the hit VRM bone's part, else the matched zone's; a zone whose box
frames a smaller touched zone is left out), its rough `coarse`
zone, what it `plays` (the matched zone's reaction), whether Martlet `notices` it,
`noticing` (the touched zones Martlet notices), the line the Thinking model would
get for that one touch on all of them (`noticed`, such as *They patted the top of
your head once.* or *They poked your groin and your left thigh once.*; a press of
600 ms or more in `heldMilliseconds` is a hold) and how long it `rests`.
With `detect`, the production detection (`TouchZoneDetection`) runs on
`snapshotPath` (a PNG of the character, transparent around it, as the renderer
takes it): it composes and encodes every picture it would send (the whole
character on its backdrop with the grid, each close-up, each check with the
numbered boxes; `previewDirectory` keeps them as files to look at) while a
FIXTURE - NOT AI stand-in answers each request from `answer`'s zones (fractions
of the snapshot). `guess`, a wrong first answer in the same form, answers the
close-ups instead, so the checks have something to correct; `checks` sets the
rounds per part (0 to 5, 2 by default); `failAt` makes that request (1 for the
first) fail instead of answering, as a model whose computer stopped answering,
so the detection stops there. The detection looks for `request`'s `wanted`
zones. `add` (zone IDs, comma-separated, such as `hand_left,tail`) adds zones
as the owner's *Add zone* does (in the middle of the picture, marked `added`;
a zone the model already has is only marked): the detection then looks for
them too and must end with them, and `detected` keeps one it can't place where
it was (moved with the picture when the `crop` changed). With `save` and no
`answer` or `detect`, `add` saves the zones with the ones added, as *Add zone*
does. With Include intimate zones on (`includeIntimate`, on unless it or the
saved zones turn it off) the intimate zones it looks for (by default the neck,
mouth (`lips`), ears, breasts, hips (`hip_left`, `hip_right`) and groin;
`TouchZoneDetection.Erogenous` lists every intimate kind) must be found, and
so must the zones the owner added: the ones the close-ups miss are
asked for again on the whole character (the `missing` step), then the intimate
ones are worked out from the zones around them. After the close-ups (and the
`missing` step), the `special` step asks the whole character what is special
about it (at most `special` zones, 0 to 10, 6 by default; 0 doesn't ask): the
stand-in lists `answer`'s zones that are special, which are the extras Martlet
knows (such as `tail`) and zones of its own that `answer` names with an ID and
a `name` (such as `{"id":"hair_bow","name":"hair bow",...}`). Each becomes a
zone with that name; ordinary body parts and anything intimate are left out.
A tail, wings or animal ears the probe's part names place are added too. The
`special check` rounds then check them on the whole character (the `steps` say
*special: found hair_bow (Hair bow)*). With a probe whose parts name body parts, the
close-ups' windows come from them and boxes that clearly miss their named part
are moved onto it (the `steps` say *took head, upper_body, lower_body from the
model's own named parts* and *moved neck onto the model's own neck*).
`detection` then reports each request
(`asked`: its `step`, `kind`, picture size and type, its `region` in the
snapshot, `marks`, the message,
the stand-in's answer and whether it `failed`), the `steps` (what each found,
swapped, moved, removed, added or worked out), `requestCount`, the `failure` it
stopped at (null when none), what it `missed`, the `wanted` zones, the `required` zones and those
still missing (`requiredMissing`), the zones `special` to the character it
found (each `Id` and `Name`), and how far the found boxes are
from `answer`'s (`worstEdge`, `meanEdge`).
With `estimate` (and no `answer` or `detect`), the first guess that the Touch
zones page places on a model with no zones and no picture
(`TouchZoneDetection.Estimate`) runs on `snapshotPath`, with the probe's hints
(`probe` or `probePath`: its face, named parts and skeleton). No vision request
is made. `estimate` then reports the `face` the probe gave, the `steps` (where
the face came from, how far the body was stretched and which zones each source
placed: *from the model's own named parts: ...*, *from the model's skeleton:
...*, *from the body's proportions: ...*), the `count`, the `wanted` zones and
the ones it couldn't place (`missing`), and the `zones`; `detected` shows them
bound to the probe, and `save` writes them as the page does (marked
`estimate`, with `snapshotPath` as their picture).
The model is `modelPath`, `modelId` or the one the `dataDirectory`'s
`avatar.json` shows. `save` (an explicit, disposable `dataDirectory` only)
writes the parsed zones (with `detect`, the detected ones and every picture the
detection sent, as *Detect zones* would) with `snapshotPath` (a PNG)
as their picture and `includeIntimate` setting the switch, each zone with no
reaction list yet given the one a new zone gets (as the desktop saves them),
so the section can
be checked with `-Desktop`. With `temperament` (a simulated Thinking answer
for [Touch temperament](../AVATARS.md#touch-temperament), such as
`{"groups":{"head":{"attitude":2,"reactions":["hearts","blush"]}}}`) or
`personaId` (the temperament that persona uses in the `dataDirectory`'s
`character-temperaments.json`: its own, the built-in reactions or a custom
one), `match` plays the zone's reaction list (a zone not filled yet: what the
temperament gives a new zone), and its `reaction` tells `from`
(`owner` for the zone's own list; `temperament` or `default` for a zone not
filled yet), the `attitude` word, whether it
`escalated` (with `repeats`, the touches in a row), how long it `linger`s and
the cues of its voice `sounds`.
`temperament` in the result shows the request Thinking gets (with
`personality`, its text), the `vocabulary` and `attitudes` allowed, the six
`categories` (each `Id`, `Label` and the zone kinds it covers, `parts`; every
zone kind is in exactly one, and `intimate` holds the intimate ones), whether the
answer was `read`, and what is `used` (who decided it, or `custom` with
`custom` naming the custom temperament, a `summary`, each category and zone
and the escalation). `personas` lists each persona of `settings.json` (and any
other the file names): its `personaId`, `name`, whether it is `active`, what it
`uses` (`own`, `built-in` or `custom`), the `custom` temperament's name and its
`own` temperament's source and summary. `custom` lists the custom temperaments:
`Id`, `Name`, `summary`, `groups`, `zones` and the personas that use it
(`usedBy`). Never the model's path; it contacts nothing.

`character_eyes` rehearses Companion › Eyes › [Where the face is](../AVATARS.md#eyes)
with no vision request: `request` (the close-up's `edge`, 768 pixels, and its
width in `faceWidths`, 1.6, with the `instructions`, the first `text`, the
`check` message that goes with the boxes drawn and numbered, the `again`
message after an answer Martlet couldn't use, and `boxNames`, the name of each
numbered box), `closeUp` (the picture's
`width` and `height`, the `area` it cuts from the snapshot and the `face` in
the snapshot's pixels with its `rollDegrees`), `measurement` (with `answer`, a
simulated vision reply about the close-up such as
`{"left":{"iris":{"left":0.34,"top":0.46,"right":0.41,"bottom":0.52},"eye":{...},"cheek":{...}},"right":{...},"mouth":{...}}`,
as fractions, pixels or the 0..1000 grid, or flat keys such as `left_iris`,
`left_cheek` and `mouth`;
and `second`, the answer to the second request when the first can't be read
or fails Martlet's checks: the `steps`, each request `asked` (its `step`,
`kind` `Eyes`, `Check` or `Again`, picture, `exchange` (the `.txt` file with
the exact request and answer), `marks`, message and answer), the
`boxes` as fractions of the close-up (`leftIris`, `leftEye`, `rightIris`,
`rightEye`, and `leftCheek`, `rightCheek` and `mouth` when given), the
`problems` the checks found (for a measurement that saved, the cheeks or mouth
it left out and why), the `failure`, and the `hint` the renderer gets: each
eye's `iris` (`x`, `y`, `r`) and opening (`eye`: `x`, `y`, `rx`, `ry`), and
`cheekLeft`, `cheekRight` and `mouth` (`x`, `y`, `r`) when they pass, in face
widths from the face's middle, roll removed), `checks` (the limits the checks
use, in face widths, with `cheekWidth`, `cheekDrop` and `mouthWidth`),
`saved` (the model's measurement in `character-eyes.json`: who measured it,
when, the `hint`, the pictures kept, the `answers` (`.txt` files) kept, whether the picture of its boxes is kept
and its plain `line`) and, with `eyesFrom` (`mesh`, `bones`, `vision` or
`estimate`, what the renderer says the eyes use), the `status` line the
section shows. Without `snapshotPath` the close-up is exactly 1.6 face widths
around an upright face; with `snapshotPath` (a PNG of the character,
transparent around it) and `face` (`"x,y,width[,rollDegrees]"`, the face's
middle and width as fractions of the snapshot) the production close-up is
composed and encoded as the desktop sends it (`previewDirectory` keeps the
pictures). `save` (an explicit, disposable `dataDirectory` only) writes the
measurement for the model (`modelPath`, `modelId` or the one the
`dataDirectory`'s `avatar.json` shows) with its pictures, marked FIXTURE - NOT
AI, as *Measure the face* would; `forget` removes it. Never the model's path;
it contacts nothing.

Touch temperament (on Companion › Touch, below Touch zones) reads through `TouchTemperamentStatus`
(for which persona and who decided it: built-in reactions, the Thinking model,
`FIXTURE - NOT AI` or your own choices; or what it uses instead: *built-in
reactions, as you chose* or *your custom temperament "Shy cat"*; its `help` is
the whole temperament in words: the attitude per category and zone, such as
*head loves, torso neutral (no reaction), ..., intimate hates*, or *intimate as
body groups* for a temperament without an intimate line, the eyes (*eyes: look
straight ahead*), the parts whose touch turns them to your mouse and after how
many touches it escalates),
`TouchTemperamentDecision` (how deciding went, or that a personality change
left your own choices in place; shown until you change something yourself),
`TouchTemperamentSaveState` (whether table edits saved),
`TouchTemperamentUseState` (what the last *Uses*, *Create*, *Rename* or
*Delete* did, or why not, such as *Not saved: "Built-in reactions" is already
a choice...*; these three only while they have something to say),
`TouchTemperamentUse` (*Uses*: *Decided from its personality*, *Built-in
reactions* or a custom temperament's name), `TouchTemperamentNewName` (the
name typed for a new custom temperament), `TouchTemperamentName` and
`TouchTemperamentCustomUsers` (shown while the persona uses a custom
temperament: its name and *Used by Mira and Aki...*),
`TouchTemperamentGaze` (*Eyes usually*: a gaze's label or *(not decided:
follow your mouse)*), `TouchTemperamentAfter` (touches in a row before it
escalates), each table line's `TouchTemperamentAttitude-<category or zone
ID>` (an attitude word, *(built-in)*, or for `intimate` *(as the body)*),
`TouchTemperamentReaction-` (*(default)*, the feeling's usual reactions,
*(nothing)* or a reaction such as *look away*), `TouchTemperamentReaction2-`
(*(nothing)* or a reaction), `TouchTemperamentLinger-` and
`TouchTemperamentLook-<category or zone ID>` (the seconds the first reaction
stays on and the eyes look at your mouse after a touch there), each category's
`TouchTemperamentParts-<category ID>` (*Parts:* and the zones it covers; the six
categories are `head`, `torso`, `arms`, `lower_body`, `extras` and
`intimate`, and `TouchTemperamentParts-intimate` names the breasts and the
groin) and `TouchTemperamentAddKind` (the part chosen to give its own line;
it offers every zone). A line shows only the controls that apply: a category at
*(built-in)* shows only its attitude, `TouchTemperamentReaction2-` shows after
a chosen first reaction and `TouchTemperamentLinger-` not after *(nothing)*,
so the others are not in `ui_snapshot` until then.
`TouchZonesLast` names the attitude, where the reaction came from (*its
reaction list*, or for a zone not filled yet the temperament) and how long it
looks at your mouse; `TouchZoneState-<n>` names the attitude (*loves it*).
`TouchTemperamentDecide` (*Decide from personality* before anything is
decided, then *Re-decide from personality*) sends the personality to Thinking
(and the persona then uses its own decided temperament),
and `TouchTemperamentUse`, `TouchTemperamentNewName`, `TouchTemperamentNew`
(*Create*: a custom temperament copied from what the persona uses now, which
it then uses), `TouchTemperamentName`, `TouchTemperamentRename`,
`TouchTemperamentDelete` (the personas that used it use their own again),
`TouchTemperamentGaze`, `TouchTemperamentAttitude-`,
`TouchTemperamentReaction-`, `TouchTemperamentReaction2-`,
`TouchTemperamentLinger-`, `TouchTemperamentLook-`, `TouchTemperamentAfter`, `TouchTemperamentAddKind`,
`TouchTemperamentAdd` and `TouchTemperamentRemove-<zone ID>` (the small ✕ by a
part's name) save, so they all need
`--allow-ui-effects`. While the persona uses a custom temperament, the table
edits that custom temperament for every persona that uses it; with *Built-in
reactions* the table is hidden. `character_touch_zones` shows the temperament's `gaze`,
each entry's `look`, the matched touch's `reaction.look`, the categories, the
custom temperaments and which persona uses which. Setting `MARTLET_TOUCH_TEMPERAMENT_FIXTURE` to a textfile before launching the desktop makes deciding read that file (read again
each time) as the Thinking model's answer (FIXTURE - NOT AI, shown in
`TouchTemperamentStatus` and `TouchTemperamentDecision`, and saved with the
source `fixture`). Saving a changed personality (`OpenCompanion`,
`CompanionText`, `CompanionClose`) then runs the real decide, store and route
path with no model.

`character_reaction_changes` rehearses Companion › Touch ›
[Changes the character made](../AVATARS.md#changes-the-character-made) with no
model request. It returns `set` (the Touch reactions tool set the How I react
check-in calls: its `id`, `tools` and `limits`), `model` (`modelId`, the
`catalog` used: the model's own emotes when `modelPath` is given, else a
FIXTURE catalog of Martlet's gestures, and how many `zones` the
`dataDirectory`'s `character-touch-zones.json` has for it), `temperament` (what
`personaId` uses, in words), `changes` (every change kept in
`character-reaction-changes.json`: `id`, `persona`, `kind`, `target`, `what`,
`why`, `at`, `until`, `active`, `endedBy` and `by`), and `before` and `after`
(for `personaId`: how many changes are in effect, the feeling for each
category, and for each zone in use its `feeling`, what it `plays`, its `list`
and `changedByCharacter`). `tool` (`read_touch_reactions`,
`change_touch_feeling`, `change_zone_reactions`, `set_touch_mood` or
`undo_touch_change`) with `arguments` (the JSON a model sends) runs one call
as the desktop runs it, with every limit, and returns `call` (`result`,
`failed`, `changed` and `saved`). `undo` (a change ID, or `all`) ends changes
as the owner's **Undo** does. `save` writes the call's change or the undo into
an explicit, disposable `dataDirectory`. `at` sets the time (ISO 8601) and
`checkInId` the calling check-in (`reactions`). `read` is what
`read_touch_reactions` returns afterwards.

The card's `ReactionChangesStatus` says how many changes of the active persona
are in effect. `ReactionChange-<n>` is each change in effect (what it does,
until when, and when and by which check-in it was made),
`ReactionChangeEnded-<n>` each of the newest five that ended (and who ended
it), and `ReactionChangesState` what the last **Undo** did. The character's
reasons (`ReactionChangeWhy-<n>`, `ReactionChangeEndedWhy-<n>`) come from the
conversation, so they aren't values MCP reads; `character_reaction_changes`
shows `why` and `read` only for an explicit `dataDirectory`.
`ReactionChangeUndo-<n>` and `ReactionChangesUndoAll` end changes, so they need
`--allow-ui-effects`.

`character_gaze` shows [where the character looks](../SCREEN_COMMENTARY.md#where-the-character-looks).
`usual` is the usual gaze from the `dataDirectory`'s `talk-preferences.json`
(Companion › Eyes › Where the character looks and the overlay's Eyes
menu): `choice` (`personality`, the default, `mouse`, `near`, `ahead` or
`window`; `GazeUsual`), `free` (whether replies may change it; `GazeFree`),
`personality` (the gaze of the temperament that the active persona of
`settings.json`, or `personaId`, uses in `character-temperaments.json`: its own
or a custom one), `personaId`, `gaze` (the gaze
that applies) and `from` (`owner`, `personality` or `default`), `prompt`
(what every reply is told: `instructions`, with the data directory's edited
prompts, and `tags`; null when the character may not change it or *Where you
look* is emptied) and `noteWhenChanged` (the note a reply gets while its own
choice holds the eyes, one minute after it chose another gaze). `aim`
rehearses the production `CharacterGaze.Aim` the overlay runs every 50 ms for
the character's frame near the lower-right corner, each with its `expected`
and actual `target` (`mouse`, `ahead`, `window` or `point`) and the point `at`:
`mouseFar`, `nearModeMouseFar` (`ahead`), `nearModeMouseNear` (`mouse`),
`aheadModeMouseNear` (`ahead`), `windowMode` (the window's middle),
`windowModeWorking` (where you worked in the window), `windowModeWorkedOutside`
(a place outside the window: its middle again), `windowModeNoWindow`
(`ahead`), `touchWhileAhead` (`mouse`) and `glanceWhileFollowing` (`point`).
`watch` rehearses the production `WindowWatch` the window gaze uses, with
`CharacterGaze.Aim`, over a sequence of moments, each with its `expected` and
actual `watching` (`pointer`, `text cursor` or `middle`; null without a
window), `target` and point `at`: `switchedWithTheKeyboard` (`middle`),
`pointerMovesOverTheWindow` (`pointer`), `typingWhileThePointerRests` and
`typingOn` (`text cursor`), `pointerOnTheCharacter` (still the text cursor),
`pointerMovesOverTheWindowAgain` (`pointer`), `pointerBackOnTheCharacter`
(still the pointer), `anotherWindowWithATextCursor` (`text cursor`),
`desktopInFront` (`ahead`) and `clickedBackIntoTheWindow` (`pointer`). `saved` is Companion › Vision › Glances at
your screen (`usual gaze`, the default, or `martlet decides`, from `DecideGaze`
in `talk-preferences.json`), then the change `grid` (32×18 cells, `ChangeThreshold`
and the stronger `CharacterChangeThreshold` under the character's overlay) and
`holdSeconds` (`glance`, `chosen` and the `gap` between glances), then
`scenarios`: the production `GazeDirector` on generated 1920×1080 pictures
(NOT screenshots; nothing is captured or shown), each with its `expected` and
actual `verdict` and the `spot` looked at (`x`, `y` in physical pixels,
`Place`, `reason`, `holdSeconds`): `still`, a `notification` popping up near
the lower-right corner (a glance at the bottom right),
`anotherChangeRightAfter` (`TooSoon`), `sameSpotAgainSoon` (`Seen`),
`sameSpotAgainLater` (a glance), `notificationBehindTheCharacter` (a glance),
`onlyTheCharacterMoved` and `speechBubble` (`OnlyCharacter`), `newScene`
(`Everywhere`), `byTheMouse` (`ByMouse`) and `changesAllOver` (`Scattered`);
`ok` is true when every verdict, aim and watch moment is as expected. `tags` gives where each look tag
points on one screen and on two side by side, `modeTags` the five gaze tags
and the gaze each sets (`usual` for `{look usual}`), `notTags` lists tags that
aren't look tags, `prompt` is what a screen glance is told (`instructions`, with the
data directory's edited prompts, and `tags`; null when *Where the character
looks* is emptied) and `replies` shows what the production segmenter makes of
answers (`answer` replaces the samples): `spoken`, `shown`, `quiet`
(a `[pass]`), `looks` (each look cue's tag, `place` and `afterPiece`, -1 for
a look without words) and `gazes` (each gaze cue's tag, `gaze` and
`afterPiece`). Read-only; it contacts nothing.

Companion › Eyes › Where the character looks (`CompanionTab-Eyes`) reads through
`CharacterGazeNow`: what the eyes do now and why (*The character looks straight
ahead, as its personality decided. It may change where it looks in its
replies.*; *as you chose*; what a reply chose and when; *Right now it looks at
your mouse after a touch on top of head.*), and its `help` is the overlay's
last answer (*The character's overlay last turned toward: mouse (-0.86,
-0.39); usual gaze ahead.*). Its `CharacterGaze-personality`, `-mouse`,
`-near`, `-ahead` and `-window` radio buttons (`selected`) and the
`CharacterGazeFree` check box (`checkedState`) save `talk-preferences.json`, so
they need `--allow-ui-effects`. The desktop log records *The character looks at
the mouse for 3 s after a touch on top of head.*, *The Thinking model chose the
character's gaze: ahead (usually mouse).* and the overlay menu's *The
character's menu chose 'look-window'.*

`character_theme` makes a character model's colors and palettes the way
Settings › Appearance does ([Character palettes](../UI_DESIGN.md#character-palettes)),
with the production code: `modelPath` (a `.model3.json` or `.vrm` on this PC),
else the model `dataDirectory`'s `avatar.json` shows, else the built-in
character (only where the Live2D runtime is beside the server). It returns
`renderer`, `key`, `textures`, `thumbnail` (a VRM's own picture), `analyzeMs`,
`swatches` (each main color's `Hex`, `share`, `Kind` and `Name`), `sources`
(`tint` and `tintStrength`, the `accents` candidates best first, the `light`
and `darkest` neutrals), `rules` (`light` and `dark`: `colors` by role,
`problems` and the `lowestContrast` of each role) and `saved` (how many colors
`dataDirectory`'s `character-themes.json` keeps for the model). With
`previewDirectory` (an absolute folder) it writes `<label>-rules-light.png` and
`<label>-rules-dark.png` (Martlet's window drawn with the real styles in each
palette); `label` defaults to the key. It never returns the model's path,
writes only to `previewDirectory` and contacts nothing.

`character_status` reads Companion › Personality and Character as saved in a
data directory (optional absolute `dataDirectory`, default the current
user's): `personality` (`state` `none`, `loaded` or `unreadable` with
`problem`; `active`, the persona Martlet uses; and each persona's `name`,
`active`, `instructionCharacters` and `speechBreaks`
(`periods`, `questionMarks`, `exclamationMarks`, `shortEndingWords`
and `isDefault`), never its
instructions), `character` (from `avatar.json`: `model` `built-in` with
`builtInCharacter`, or `own model` with `ownModelType` `.vrm` or
`.model3.json`, never the path; `renderer`, `lipSync`, `autoShow` and
`lipSyncHost`, the paired host's ID), `placement` (from
`character-placement.json`, this PC only: `state` `none` when no position is
saved (the character shows at its default spot), or `loaded` with `locked`,
`left`, `top`, `width` and `height` in device-independent pixels, and
`screen` (the monitor's device name, such as `\\.\DISPLAY2`), `screenLeft` and
`screenTop` (its spot on that monitor's work area); see the character overlay below), `clickThrough`
(from `character-click-through.json`, this PC only: `state` `none`, `loaded`
or `unreadable` and `on`, whether clicks pass through the character; see the
character overlay below), `voice`
(from `talk-preferences.json`: `state` `none`, `loaded` or `unreadable`,
`speakReplies`, Companion › Voice's *Speak Martlet's replies aloud*, on unless
saved off, `muted`, its opposite, which the overlay menu's *Mute voice* and
*Unmute voice* change, and `volume`, Companion › Voice's *Voice volume* from 0
to 1, full unless saved lower; see below) and
`lorebooks` (`books`, `on` and
`entries` counts, and `account`: the account folder they were read from, named
by `accounts\working-copy.json`; null for a data folder from before accounts). Those editors have no Save button; each change saves on its
own into the newest saved file, keeping what was saved elsewhere meanwhile
(another page, or sync from your other computers, such as the lip-sync host).
`OpenCompanion` (Personality's *Edit personality*), `OpenAvatar` (Character's
*Choose and customize*), `OpenLorebooks` and `OpenMemory` open their windows
(the character itself doesn't show), and their `CompanionClose`,
`AvatarClose`, `LorebookClose` and `MemoryClose` close them; these are passive
clicks, as are the character window's `AvatarAdvanced` and
`RemoteHostSection` expanders. Companion > Memory's *Conversation history* card
shows `HistoryStatus` (text: whether Martlet keeps a record and may search it,
and how many conversations and exchanges it holds since when; never what was
said) and the checkboxes `HistoryKeep` and `HistorySearch` (toggling either
saves `conversation-history.json`, so it needs `--allow-ui-effects`).
`OpenHistory` (*Open conversations*) goes to the Conversations page, the same
page as the side menu's `NavConversations` (a passive click; the page itself
has the automation ID `ConversationsPage` and is hidden on a host PC);
`HistorySearchRun` and `HistoryShowAll` only filter what it lists, and
`HistoryEditMessage` only opens the editor (`HistoryEditCancel` closes it). Its
`HistoryWindowStatus` reads as text (counts per app, or what a search or the
last change did), `HistoryPlatformStatus` reads the changes waiting for
Telegram and Discord (*2 changes waiting for Discord (not connected; they go
once it is).*) and `HistorySyncStatus` reads the record sync's line (*Conversations:
42 exchanges kept the same on 2 of 2 hosts; checked 9:41 PM.*; counts only). `HistorySearchText` and `HistoryEditText` take text through
`ui_set_text`; `HistoryAppFilter` (*All apps*, *This PC*, *Telegram*,
*Discord*, *WhatsApp*), `HistoryConversations` (items named *Conversation 2
(Telegram)*) and `HistoryMessages` (items named *Message 3: Martlet ·
Discord*) take `ui_select`; what was said is not a readable value.
`HistoryAlsoThere` (on) makes deletes and edits also queue changes for the
apps. `HistoryEditSave`, `HistoryDeleteMessage`, `HistoryDeleteConversation`,
`HistoryDeleteAll` and `HistoryPlatformCancel` write (deletes ask first, No by
default) and need `--allow-ui-effects`. Each editor's footer line, `CompanionSaveState`,
`AvatarSaveState` and `LorebookSaveState`, reads *All changes saved.*,
*Saving...*, *Not saved yet: <why>* (for example an empty persona name, or
*Choose your model file: an existing .vrm or
.model3.json file.*) or *Not saved: <why>*; `AvatarStatus` reads the
character's state (*Character is showing. ...*, *Character hidden.*). Memory's
`MemoryFactStatus` reads how many facts it remembers, how many belong to how
many people Martlet knows by voice and how many to forgotten voices, how many
are kept in each [memory space](../MEMORY.md#memory-spaces-on-the-desktop)
besides yours (*3 in Household*), how many replaced facts it keeps as history
(*1 replaced kept as history*), how many are kept for a game
([Memories for a game](../MEMORY.md#memories-for-a-game): *2 kept for 1 game or
activity*), how many
its *Show* choice (`MemoryPersonFilter`), *Show replaced facts*
(`MemoryShowHistory`, a check box `ui_toggle` turns without
`--allow-ui-effects` because it only changes the list) and search (`MemorySearch`, set with
`ui_set_text`; it only filters the list and also matches hidden search words) list (*Showing N.*) and what the last
action did, never a fact or a name; `MemoryStatus` (its bottom line) reads
whether memory is on, is saving or why it can't be (*Memory is on.*, *Saved.
Memory is off: ...*), never a fact or a folder. The window reads its facts the
moment it opens, even while a reply holds the setup slot, and follows facts
remembered, changed or forgotten elsewhere (remembering after a reply,
`manage_memories`) on its own, so `MemoryFactStatus` changes without
`MemoryReload` (*Refresh*). `MemoryNewFact` (clears the fact editor) and
the `MemoryStorageSection` and `MemoryExportSection` expanders are passive
clicks; `MemoryDeleteFact` (the selected fact or facts), `MemoryDeleteShown`
(every fact listed now: one person's or what the search found) and
`MemoryDeleteAll` ask first and need `--allow-ui-effects`. With accounts the
Memory window also shares ([Sharing memories](../MEMORY.md#sharing-memories)):
`MemoryShareAboutMe` (*Share new memories about me with the household*,
`ui_toggle`), `MemoryShareTarget` (*Share selected with*: "The household's
memories", "<name>'s memories" for each other person, "<name>'s memories (shared
together)" for each character shared together, chosen with `ui_select`),
`MemoryShareCopy` and `MemoryShareMove` (enabled with facts selected; select
facts in `MemoryFacts` with `ui_select` and the fact's words). These change
memories, so they need `--allow-ui-effects`. `MemoryShareStatus` reads what they
did, with counts and the kind of place only (*Copied 1 fact to the household's
memories.*, *Moved 2 facts to another person's memories.*, *New facts about you
now go to the household's memories.*). A share to another person without a
paired host puts *Pair a Martlet host to share facts with someone else's
memories.* in `MemoryFactStatus` and moves nothing. `memory_status` then counts
the facts of each space. Their
fields (`CompanionName`, `CompanionText`, the fixed note under it
`CompanionTextHelp` (a safe value: the persona is the character's part of the
system prompt, and Companion › Prompts › Persona holds Martlet's part),
the *Where the voice pauses* check boxes
`CompanionBreakPeriods`, `CompanionBreakQuestions` and
`CompanionBreakExclamations` (their `checkedState` is the persona's choice) and
`CompanionShortEnding` (its value reads *Never*, *1 word* or *Up to N words*),
`CompanionPersona`, which also makes the chosen persona the one Martlet uses,
`CompanionNew`, `CompanionDuplicate`, `CompanionDelete`, `CharacterChoice`,
`AvatarModelPath`, `LipSyncChoice`, `AutoShowCharacter`, the lorebook fields
(among them the scan setting `LorebookWordForms`, *Match other word forms*, and
an entry's three-state `LorebookEntryWordForms`, *Other word forms*; *Try it*'s
`LorebookTestResult` reads which entries a sample message would add, by title,
lorebook and keyword, never their text),
`MemoryEnable` and Memory's fact fields: `MemoryFactContent`, `MemoryPerson`
(*Belongs to*), `MemoryWhere` (*Where it matters*: *Everywhere*, *While gaming
(any game)*, the game played now and each game a fact is kept for; the selected
fact's `MemoryFactDetails` says *Matters: while playing <game>*),
`MemoryRetention`, `MemorySaveFact`, `MemoryEditFact`) save, and
choosing in `MemoryPersonFilter` (it only filters the list) still goes through
`ui_select`, so these need `--allow-ui-effects`; `ShowCharacter`/`StopAvatar` show or hide the
character, so they need `--allow-ui-effects` too. Typing a persona name or text and
closing at once still saves it; closing with a change that can't be saved asks
with `ConfirmationYes` (close and drop it) or `ConfirmationNo` (stay). With the
character showing, choosing another model or lip-sync mode switches it right
away (`AvatarStatus` changes, and `logs_tail` `desktop` records *Avatar renderer
stopped by Martlet.* for the old one).

The character overlay itself is drawn by Martlet's renderer child process
(`Martlet.Avatar.RendererHost`); `ui_snapshot` includes its windows (the
overlay is titled *Martlet character overlay*; another Martlet's renderer is
never included). With `layout`, its window bounds are twice the character
frame's width, centered on the frame: the extra half-frame on each side is
transparent room for the model's motion and may run past the screen's edge.
Its drag surface `MoveAvatar` supports UI Automation
expand/collapse, so `ui_click` on it opens (or closes again) the character's
right-click menu with no flag; opened this way, the menu stays open until a
choice or another `MoveAvatar` click. While it is open, snapshots list
`CharacterMenu` and its items: `CharacterTalk` (*Talk to Martlet*, like
`TrayTalk`), `CharacterMuteVoice` (*Mute voice*, or *Unmute voice* while
Martlet's voice is muted; see below), `CharacterOpenMartlet` (*Open Martlet*, shows the window like
`TrayOpen`, also from the notification area) and `CharacterSettings`
(*Character settings*, opens Companion › Character), which are passive clicks
(except `CharacterMuteVoice`), and `CharacterEyes` (*Eyes*), whose click only
opens its submenu: `CharacterEyes-personality` (*As the personality decides*),
`CharacterEyes-mouse`, `CharacterEyes-near`, `CharacterEyes-ahead`,
`CharacterEyes-window` and `CharacterEyes-free` (*Let the character change
it*). Their `checkedState` shows what Martlet last told the overlay applies;
choosing one goes through Martlet (*The character's menu chose
'look-ahead'.*, `look-free-off`), saves `talk-preferences.json` like Companion ›
Character › Where the character looks (`character_gaze` `usual`), so they need
`--allow-ui-effects`;
then `CharacterZoomIn`, `CharacterZoomOut`, `CharacterResetZoom` (disabled at
the default zoom), `CharacterResetPosition`, `CharacterLockPosition`,
`CharacterClickThrough` (*Let clicks pass through*; see below), the checkable `CharacterOnTop`
(*Keep on top*, on by default; its `checkedState` is the current choice for
this showing) and `CharacterHide` (*Hide character*; Esc on the overlay does
the same), which need `--allow-ui-effects`. Talk, Mute, Open, Settings and Hide are
carried out by Martlet itself, so the desktop log records *The character's menu
chose 'hide'.* (and so on), and a hide is followed by *Avatar renderer stopped
by Martlet.* and `SetupCharacterNow` reading *hidden*.

**Renderer health** shows in the logs (`logs_tail`). Martlet and the renderer
send only whole messages, so a slow reply or a timeout never puts later
commands out of step; a reply that comes after its caller stopped waiting is
dropped. When the renderer's messages break (an unreadable message, a reply
when nothing was asked, a failed write) or it answers nothing for 90 seconds,
Martlet ends it and the character counts as stopped. Then `desktop` records
*Martlet ended the character renderer because it stopped answering properly:
...* and *Avatar renderer ended by Martlet (code 0x00000001) ...*. When the
renderer closes by itself, `avatar-renderer` records why: *The character
overlay closed: its window was closed (not by Martlet).* or *The character
renderer stopped after an error; it tells Martlet and closes.* with the error.
Then `desktop` records the warning *Avatar renderer closed itself ...* (with
the error code when it sent one). Only another exit code (a crash or a kill)
is an error: *Avatar renderer exited unexpectedly with code ...*.

**Voice volume**: Companion › Voice's `VoiceVolume` slider (0 to 100, full by
default) sets how loud Martlet speaks and sings on this PC, applied to
Martlet's own audio (never Windows' volume); a reply or song playing now
follows within one device buffer. Both it (its number) and `VoiceVolumeLevel`
(*80%*) are in `SafeValues`. `ui_set_range` on it needs `--allow-ui-effects`
because it saves `talk-preferences.json` (`VoiceVolume`, 0 to 1, this PC only
and not shared with paired computers); `character_status`'s `voice.volume`
reads the saved level (1 without a file).

**Chatterbox Original style**: while Speaking uses Chatterbox Original or it is
the chosen engine (`speaking-engine.txt` is `chatterbox-original`), Companion ›
Voice shows its card with four sliders,
`ChatterboxStyle-GeneralExaggeration`, `ChatterboxStyle-GeneralCfgWeight`,
`ChatterboxStyle-ExpressiveExaggeration` and `ChatterboxStyle-ExpressiveCfgWeight`
(exaggeration 0.25-2 and CFG weight 0-1, in steps of 0.05), each value's label
`ChatterboxStyleValue-<name>` ("0.7") and `ChatterboxStyleState` ("Resemble's
suggestions. General: exaggeration 0.5, CFG weight 0.5. Expressive: exaggeration
0.7, CFG weight 0.3." or "Saved on this PC. ..."), all in `SafeValues`.
`ui_set_range` on a slider and clicking `ChatterboxStyleReset` (*Use Resemble's
suggestions*) save `chatterbox-style.json`, so they need `--allow-ui-effects`;
`f5_voices`' `chatterboxStyle` reads what is saved and `voice_engine_check` with
the same `dataDirectory` sends it.

`MoveAvatar` also supports UI Automation's move: with `--allow-ui-effects`,
`ui_move` moves the character by `dx`, `dy` screen pixels like a drag and
returns its bounds before and after, and `ui_snapshot` reports `movable` for
it.

**Tapping the character**: a left click on the character that doesn't drag
it (it comes up within Windows' drag distance, within 0.7 seconds; also when
its position is locked, while zoomed in with Ctrl and in the camera view) is a
tap. The renderer page hit-tests the point: Live2D reports the model3.json
HitAreas there and the visible drawables under it (topmost first, at most 8)
and whether the topmost is hair (it sits in a part the model names as hair,
by its ID or DisplayInfo name, such as `PartHairSide` or 前髪),
VRM the humanoid bone of the mesh skinned most to the hit triangle (or its
nearest humanoid ancestor), the actual node, whether that node is hair (a
spring-bone or hair-named joint under the head), the mesh and the material.
The VRM hit test measures the skinned meshes in the pose they have now, so a
sleeve or glove that the idle pose moved away from the T-pose is hit where it
shows.
Martlet then reacts locally, without asking any model: the model's own tap
motion when it has one (a group named like `TapHead`, `Tap@Head`, `TapBody`
or `Tap`), else a head tilt (or nod) for the head, hair and face and a
surprised look (or gasp or nod) elsewhere; Companion › Touch › Touch zones (`character_touch_zones`) replaces that with the reaction of the zone the tap lands in (`TouchZonesLast`). The desktop log records *The
character was tapped on the body (hit areas Body).* and *Character motion
'TapBody' played for a tap on the body.* `character_touch` taps it through
UI Automation (`MoveAvatar`'s value, `"x,y"`) at `x`, `y` (fractions 0 to 1
of the overlay's drawing, +y down; unzoomed the head is near 0.5, 0.15),
which needs `--allow-ui-effects`, waits for the hit test and returns it as
`last`: `n` (the tap's number), `x`, `y`, `hit`, `zone` (`head`, `hair`,
`face`, `body`, `arm`, `hand`, `leg` or `foot`; null on a miss), `hitAreas`,
`drawables`, `bone`, `node`, `hair`, `mesh` and `material` (model-authored
names only, never paths), `held` (how long the press lasted, in ms) and `rest`
(`{x, y}`, fractions like `x` and `y`: where the touched point of the character
was in its rest pose, the pose the touch zones picture shows, traced on the
touched mesh and drawn as the overlay frames it now; null when the renderer
can't trace it). At rest `rest` equals `x`, `y`; while the head follows the mouse
or a motion moves the character, it stays on the spot of the skin that was
touched, and the touch zones compare it with their boxes. `holdMs` presses
that long (`"x,y,ms"` as `MoveAvatar`'s value; 600 or more is a hold), `repeat`
taps the same point up to 20 times `gapMs` apart, and `taps`
(`[{x, y, holdMs}]`, up to 20) taps a sequence of points, each after the hit
test of the one before. With Companion › Touch › Touch zones showing,
`noticed` reads what Martlet noticed after `settleMs`: `waiting`
(`TouchZonesNoticed`), `last` (`TouchZonesNoticedLast`) and `zone`
(`TouchZonesLast`). Without `x`, `y` or `taps` it only reads the last tap, as
does `MoveAvatar`'s `value` in `ui_snapshot`.

**Touches reach the Thinking model** for zones with *Martlet notices* on (on
by default; a `character-touch-zones.json` of version 1 where no zone of a model
had it on loads with it on for all of that model's zones, and saves as version
2): a
reply to what you say or type carries the touch line in its notes (the desktop
log's *Touches: 3 went to Thinking in the notes of your message.*), and touches
on their own start a short reply of their own (*... as a short reply of their
own.*; the talk window's `LiveTurnInputs` reads *Last reply took 2 touches.*).
Its message (Companion › Prompts › *Touched*) asks for a sound or words out
loud, never silence, an emote alone or `[pass]`. The touch line names how the
persona feels about the touched zones (its touch temperament) and, after 5 or
more touches on one place in the last 10 minutes across replies, *They keep
coming back to ...*.
Without a Thinking setup `TouchZonesNoticed` says the touches wait for your next
message.

**Touching Martlet while it talks**: with *When you touch Martlet while it
talks* on *any* (the default) or, for a touch on an intimate part, *intimate*,
a touch on the character while Martlet says a reply, a report or a screen
remark aloud (never its reaction to an earlier touch) stops it at once
(`controller.Stop(..., keepContext: true)`, status `touch.cut_in`, or
`commentary.interrupted` for a remark; the reply's note reads *Stopped for
your touch.*). The touch ledger keeps what Martlet had said aloud
(`ConversationTurn.SaidAloud`, the sentences that started playing, at most
the last 300 characters) and the user's own words it was answering, and the
reaction starts 0.5 seconds after the last touch with no cooldown; its message
adds Companion › Prompts › *Touched, cutting you off*. The desktop log reads
*Touches: Martlet stopped its reply for a touch (poke on groin); its reaction
starts about 0.5 s after the last touch.* Moving or zooming the character never
stops Martlet, and a touch while it still thinks waits for its next reply.

`character_physical_check` runs the same summary and wording headless, with no
desktop and no model request: `stroke` (a JSON `CharacterStroke` with its
hit-tested samples) is summarized against the touch zones saved for `modelId`
(or the rough zones before any were found), `changes` (a JSON array of
`RendererPhysical`) are worded, and both go into a touch ledger. It returns the
stroke's zones (where zones overlap, each zone a sample was on), pace, passes, `dx` and `dy` (where it ended from where it
began, page heights), `sideways`, `way` (`down`, `up` or null) and `words`
(`where`, `label`, `pace`, `times` and `hint`: how the ledger says its whole
path), each change's ledger kind, words and `startsTurn` (true for touches,
strokes and moves), the plain
`line` the next reply would carry, the `history` line and `startsTurn`,
`character` (what the talk window and the Thinking model call the character:
the name of the persona the data directory's settings use, else *Martlet*)
and `note` (the talk window's note for a reply to them alone, such as *You
touched Ivy (touch: hair stroke x4, moved)*, or null when they wouldn't start
one).
`noticeAll` (default true) treats every zone as having *Martlet notices* on;
false uses the zones' own setting. `personaId` takes that persona's touch
temperament from `character-temperaments.json` and returns the stroke's
`feeling` (*you love it on your chest, and hate it on your groin*), with
`intimate` and `interrupts` (whether the stroke would stop Martlet talking with
`touchInterrupts`: `any`, the default, `intimate` or `never`). `earlier` (0 to
20) records the same stroke that many times before, a minute apart, each taken
by a reply, so `often` lists the places the user keeps coming back to (`place`,
`count`, `minutes`) and the line ends with *They keep coming back to ...*.
`said` and `answering` stand for a touch that stopped Martlet talking. It also
returns `told` (what the Thinking model hears of the touches, with *Touched,
cutting you off* when a touch stopped Martlet), `message` (a touch-only
reply's whole message, Companion › Prompts › *Touched*) and `notes` (*Touched,
with your message*), with the prompts saved in the data directory.

**Locking the character's position**: Companion › Character's
`SetupCharacterLock` (*Lock position*, shown while the character shows or is
locked) and the overlay menu's `CharacterLockPosition` (*Lock position*,
carried out by Martlet: *The character's menu chose 'lock'.*) lock it where it
is; both need `--allow-ui-effects` because they save
`character-placement.json` (see `character_status`'s `placement`). Home has
no lock button. Locked, `movable` is false and `ui_move` is
refused; the overlay ignores dragging, the arrow keys and Home, and its own
`CharacterResetPosition` is disabled; and zoom (the wheel, the menu or
`SetupCharacterZoomIn`) only zooms the camera, keeping the overlay's bounds.
The same `SetupCharacterLock` then reads *Unlock position* (also while the
character is hidden), and the overlay menu's `CharacterLockPosition` reads
*Unlock position* (carried out by Martlet: *The character's menu chose
'unlock'.*); each unlocks it. `SetupCharacterPlacement` says whether the position is locked and
where (device-independent pixels and the monitor), and `SetupCharacterView` ends with
*Position locked.* when the overlay reports it. A locked character shows at
its locked place again after Hide/Show or a Martlet restart (at its default
spot, still locked, if that place is no longer on a screen); the desktop log
records *Character position locked at ...* and *Character position unlocked.*,
and `avatar-renderer` *The character's position is locked.*

**Letting clicks pass through the character**: the overlay menu's
`CharacterClickThrough` (*Let clicks pass through*, carried out by Martlet:
*The character's menu chose 'click-through-on'.*), Companion › Character's
`SetupCharacterClickThrough` (*Turn on click-through*, shown while the
character shows or click-through is on) and the notification-area
menu's checkable `TrayCharacterClickThrough` (*Let clicks pass through the
character*, listed while the character shows or click-through is on) turn it
on; all need `--allow-ui-effects` because they save
`character-click-through.json` on this PC (`character_status`'s
`clickThrough`; never shared, and Reset position leaves it alone). Home has no
click-through button. On, the
overlay window (and its speech bubble) gets `WS_EX_TRANSPARENT`, so every
click, the wheel and right-click go to the window under it: `ui_snapshot`'s
`windowStates` reads `clickThrough` true for *Martlet character overlay*, and
the character can't be dragged, zoomed, tapped, stroked or right-clicked with
the mouse (its eyes still follow the mouse, and it still talks and moves).
The mouse can't reach the character's menu then, so it is turned off in
Martlet: `SetupCharacterClickThrough` reads
*Turn off click-through* (also while the character is hidden),
`TrayCharacterClickThrough`'s `checkedState` is `On`, and clicking any of them
turns it off (opened through UI Automation, the overlay menu's
`CharacterClickThrough` reads *Stop letting clicks pass through* and does the
same). `SetupCharacterClickThroughNote` says whether it is on in one sentence
(its *?* `Help-SetupCharacterClickThroughNote` has the rest and links to the
wiki's *Move, zoom and lock*), and
`SetupCharacterView` ends with *Clicks pass through.* when the overlay reports
it. A newly shown character starts click-through when it is on (after a
Hide/Show or a restart). The camera view always catches clicks and applies
click-through again when it closes. The desktop log records *Click-through
turned on: clicks pass through the character.* (or *... turned off ...*) and
`avatar-renderer` *Clicks pass through the character.* or *The character
catches clicks again.*

**Remembering where the character is**: whenever the character is dragged,
nudged, resized, zoomed or sent home (`ui_move` included), the overlay asks
Martlet (request `placed`, never logged as a menu choice) to read its place
(renderer command `where`) and save it in `character-placement.json`, locked
or not, with the monitor it is on; the desktop log records *Character position
saved at ... on DISPLAY2.* The next showing (Hide/Show, a restart, a shutdown
or an update) puts it back on that monitor at the same spot, with the
character's middle kept on the monitor's work area; if that monitor is gone,
where it was when that spot is still on a screen, else its default spot
(`avatar-renderer` logs *The character is back where it was left on
DISPLAY2.*). Home's `ResetCharacterPosition` and Companion's
`SetupCharacterResetPosition` (*Reset position*, need `--allow-ui-effects`)
show while the character shows or a place is saved: showing, they move it to
the lower-right of the main screen even when locked (it stays locked there)
and save that; hidden, they forget the saved place so it next shows at its
default spot, unlocked (`placement.state` `none`).

**When the character's renderer fails a command** (its pipe breaks, it sends
something unreadable or it runs out of time): only the work that draws the
character ends. A sentence's lip-sync (loudness mouth or Audio2Face frames), a
song's mouth, a gaze, an emote or saving where the character is stops, and the
voice goes on. `ToggleCharacter` (*Hide character*) always finishes, and no
*Martlet recovered from an unexpected error* dialog shows. Each kind of failure
gets one short desktop log line a minute, without a stack trace, for example
*The character's new position couldn't be read to save it: Renderer message
length is invalid (InvalidDataException).*; the next line for the same failure
adds *(N more like it in the minute before weren't logged.)*. To check this
without a broken renderer, set `MARTLET_SIMULATE_RENDERER_FAILURE` to the
renderer commands to fail, comma-separated (for example `where,lock,zoom`),
before launching the desktop (`-Desktop` passes the environment on). FIXTURE,
never a real failure: the shown character's renderer fails those commands the
way a broken pipe does (*Renderer message length is invalid (simulated by
MARTLET_SIMULATE_RENDERER_FAILURE).*), and the desktop log says so each time
the character shows (*FIXTURE: the character renderer fails its ... commands*).
The renderer still starts, draws and closes normally. Commands include `where`
(saving its place after `ui_move`), `lock` (`SetupCharacterLock`), `zoom`
(`ResetCharacterZoom`), `home` (`ResetCharacterPosition`), `place` (a character
profile's place when you switch profiles), `mouth` (the
loudness mouth), `reset` and `apply` (Audio2Face frames), `gaze`, `action`
(emotes and motions), `say` (speech bubbles), `theme` and `camera`.

Companion › Speech bubbles (`CompanionTab-SpeechBubbles`) starts with its Now line, `SpeechBubblesNow`, then
the main choice, *Where Martlet's words show*: the radio buttons
`SetupCharacterSpeechBubbles` (*Speech bubbles*, the default), `SetupCharacterSubtitles`
(*Subtitles*), `SetupCharacterSpeechBoth` (*Speech bubbles and subtitles*) and
`SetupCharacterSpeechOff` (*Off*); snapshots return which one is `selected`, and `SetupCharacterSpeechDisplay`
returns whether each is on and whether bubbles show now (character showing) or
once it is. Choosing one with `ui_click` saves `speech-display.json` (the same choices as the
character window's `SpeechBubbleChoice` and `SubtitleChoice`), so it
needs `--allow-ui-effects`. With the character showing,
`SetupCharacterPreviewBubble` (also `--allow-ui-effects`) sends a sample bubble
to the overlay for a few seconds; `SetupCharacterSpeechDisplay` then says
whether the overlay took it and where it put it (to the left or right of the
character's head, above it, or in its fixed place, with the bubble's screen
position and size), whether the text as laid out on screen lies inside the
bubble (*holding all its text*, or *but its text doesn't fit inside it*), and
whether the colors it is drawn in are the palette Martlet's windows use now
(*in the Character dark colors*, or *but not in the Pink light colors (fill
#..., outline #...)* naming each part that differs). The overlay reports the
fill, outline, text and halo colors of the bubble as shown; they are the
palette's Surface, Accent, Text and Glow (no halo in Windows' high contrast).
Every bubble is sized to its whole text, including the first one after the
bubble was hidden; very long speech widens it (up to 640 pixels) so it stays
within half the screen's height. The bubble itself is drawn by the separate
renderer process, so its text is not in snapshots.

The same card sets where the bubble goes. `SetupCharacterBubblePlacement`
returns *Follows the character* (default: beside the head on whichever side has
room on its screen, then above it, following moves, zoom and pan) or *Stays in
one place*; `SetupCharacterBubbleOffsetX` and `SetupCharacterBubbleOffsetY`
return the pixel offsets (positive is right and down; in one place they are
measured from the top-left of the character's screen). `ui_select` and
`ui_set_text` on them save `speech-display.json` (`StaticBubble`,
`BubbleOffsetX`, `BubbleOffsetY`), so they need `--allow-ui-effects`;
`SetupCharacterSpeechDisplay` reads back the saved position, or says an offset
isn't a number from -4000 to 4000. Where the bubble sits depends on the
computer's screens, so it stays with each computer; whether bubbles and
subtitles show is the same on all of them (the `speech-display` shared setting).

Companion › Character's *Your characters* card lists the built-in character and
every [shared character](../CLUSTER.md#the-shared-character-models) in the order
they joined. `CharacterModelsStatus` reads how many characters of the owner's
own there are and what this PC shows ("1 character of your own. This PC shows
one of your characters." or "... the built-in character.", never a name).
`CharacterModelsShared` reads whether they are shared with the paired Martlet
computers ("Characters shared with 2 of 2 computers at 7:15 PM.", characters
still copying to this PC, hosts to update, or "No other Martlet computers are
paired yet, so your characters stay on this PC."). Each row's detail line
`CharacterModelState-<key>` (`builtin`, or the first 16 hex digits of the
character's ID) returns its renderer, size, where and when it was added,
"Copying to this PC..." while pieces are missing and "Shown on this PC." for the
one shown, never its name. Its controls are `CharacterModelUse-<key>` (disabled
while it is shown or still copying) and, unless shown, `CharacterModelRemove-<key>`
(asks with `ConfirmationYes`/`ConfirmationNo` and removes it on every computer).
`CharacterModelAdd` opens *Add a character* (`CharacterModelAddDialog`):
`CharacterModelAddPath` (the `.model3.json` or `.vrm` full path),
`CharacterModelAddName`, `CharacterModelAddOk` (adds, shares and shows it on this
PC) and `CharacterModelAddCancel` (passive); `CharacterModelAddProblem` returns
why it couldn't (never the typed name or path). Use, Remove and adding need
`--allow-ui-effects`. In the character settings window (`OpenAvatar`), a model
typed into `AvatarModelPath` (after `ui_select CharacterChoice` "My own model
file") joins the shared list as soon as it is saved (once it names an existing model file; there is no Save button) or shown (`ShowCharacter`), and the
saved profile then shows Martlet's copy. `character_models` reads the same list
and copies headlessly.

**Character profiles** (`CompanionTab-Profiles`, first under *Who it is*) switch
the look, the voice and the personality together. `CharacterProfilesNow` is the
Now card (it names the character, so it isn't a safe value);
`CharacterProfilesStatus` reads how many profiles there are and whether one is
in use ("2 profiles. One of them is in use." or "None matches what Martlet uses
now."). Each row's `CharacterProfileState-<key>` (the first 8 hex digits of the
profile's ID) reads "In use.", "Ready." or why a part can't switch here ("Its
look is still copying to this PC. Using it switches the rest.", "Its voice is no
longer in your voices."), never a name. Each row's `CharacterProfileHere-<key>`
reads what the profile keeps on this PC ("On this PC: its own spot and size (360
× 480, locked) · Eyes: Watch the window you're using, replies can't change it ·
While it talks: only intimate touches stop it." or "On this PC: nothing yet.
..."). While a profile is in use, moving, resizing, locking or resetting the
character, choosing where it looks (`CharacterGaze-<choice>`,
`CharacterGazeFree` or the overlay's Eyes menu) and choosing which touches stop
it (`TouchInterrupt-<choice>`) are kept for that profile on this PC. Using a
profile first keeps the one in use, then puts back the new one's choices: the
showing character moves to its place without counting as a move of yours
(renderer command `place`; `avatar-renderer` logs *The character moved to where
its profile left it on DISPLAY1.*) and the desktop log records *Switched to a
character profile (<key>); on this PC: place 360 × 480 locked on DISPLAY1, gaze
window (fixed), touches while talking intimate.* A switch made on another
computer, also while Martlet was closed, is followed after the shared settings
arrive (*Following the character profile switched to last (<key>); ...*). A
profile that keeps nothing here yet takes on what this PC uses. Its controls are
`CharacterProfileUse-<key>` (disabled while in use), `CharacterProfileEdit-<key>`
(passive: opens the form) and `CharacterProfileRemove-<key>` (asks with
`ConfirmationYes`/`ConfirmationNo`). `CharacterProfileNew` (passive) opens the
form filled in with what Martlet uses now: `CharacterProfileName`,
`CharacterProfilePersona`, `CharacterProfileLook` ("Keep the current look", the
built-in character or one of your characters), `CharacterProfileVoice` ("Keep
the current voice" or one of your voices), `CharacterProfileSave` and
`CharacterProfileCancel` (passive); `CharacterProfileEditorProblem` returns why
it couldn't save. `ProfilesOpenCharacter`, `ProfilesOpenVoice`,
`ProfilesOpenPersonality`, the Character and Personality pages' `OpenProfiles`
and Home's `HomeManageCharacters` only open pages. Home's `HomeCharacterProfile`
combo box lists every profile ("A mix of your own" when none matches) and
switches on selection; the notification-area menu's `TrayCharacterProfiles`
submenu has `TrayCharacterProfile-<key>` items, the one in use ticked. Use,
Save, Remove and switching from Home or the menu need `--allow-ui-effects`.
`character_profiles` reads them headlessly.

The card ends with **Combos**. `CharacterCombosStatus` reads how many combos
the model has and which tags replies get (*9 combos. Replies can use 8:
{lovestruck} {flustered} {overheated} {fuming} {heartbroken} {dozing}
{starstruck} {shocked}.* for the built-in character, which has all of
Martlet's combos with `{ahegao}` off, or *No combos yet.*). `CharacterCombosAdd` (*Add a combo*,
passive) adds an empty row; nothing is saved until the row has a tag and parts.
Row `<n>` (as in `character_actions`' `combos`) has `CharacterComboOn-<n>`
(check box: its `checkedState`, and `ui_toggle` turns it on or off),
`CharacterComboName-<n>` (*{flustered} · combo*, or *New combo*;
a status field), `CharacterComboState-<n>` (a status field: what it sets off,
such as *Turns on "hearts" until {/flustered}; plays "blush" and "nod" once.*,
or why its parts can't be read, such as *No emote has the tag 'wave'.*),
`CharacterComboTag-<n>`, `CharacterComboParts-<n>` (the parts' tags, such as
`blush hearts nod`), `CharacterComboUse-<n>` (the When to use box),
`CharacterComboHint-<n>` (the grey hint in that box while it is empty, such as
*a combination of {blush}, {hearts} and {nod}*), `CharacterComboTry-<n>` (its
label, a status field, reads *Turn off* while one of its lingering parts is
on) and `CharacterComboRemove-<n>`. A part's tag that names no emote reads
*Not saved: no emote has the tag 'wave'.* in `CharacterActionsSaveState`. After
Try, `CharacterActionsLast` reads *Combo {flustered} for a try at 6:22:47 PM:
turned on "hearts"; played "blush" and "nod".* (*Combo {/flustered} for a try
...: turned off "hearts".* after Turn off, and *for a reply* when a reply's tag
set it off), and `logs_tail` `desktop` has a line for each part and *Character
combo {flustered} for a try: 1 turned on, 0 kept on, 2 played, 0 failed.* Typing
in a combo's boxes, its check box and Remove save, and Try and Turn off change
the overlay, so they need `--allow-ui-effects`.

Editing an emote's row saves `character-actions.json`, `CharacterActionsDetect` (*Name them with
Thinking*) sends the model's emote and motion names and details to the Thinking
model, `CharacterActionsReset` goes back to the model's own names (the combos stay), and Try,
Turn off and Clear emotes change the overlay, so all of them need `--allow-ui-effects`. The first time a model
shows with a Thinking model set up, Martlet names its emotes once on its own.
`character_actions` reads the same settings headlessly.

For voices, open `CompanionTab-Voice` (the Voices card shows unless the
voice comes from a cloud provider). There are no built-in voices and no groups:
one list, in the order voices joined it (a new list starts with the starter
voices). `F5VoicesStatus` reads how many voices there are and which is chosen or in
use (a starter voice's name, "one of your recordings", or "a voice no longer in
the list"), for example "6 voices; none chosen yet, so Martlet starts with Jenny
(Dioco)." `F5VoicesShared` reads whether the list is shared with the
paired Martlet computers ("Voices shared with 2 of 2 computers at 7:15 PM.",
voices still copying to this PC, hosts to update, or "No other Martlet computers
are paired yet, so your voices stay on this PC."). Each voice whose recording is a
starter clip has a title `F5VoiceRow-<key>` (for example `F5VoiceRow-arctic-slt`)
that returns its name with "· chosen" or "· in use" when it is. Its controls are
`F5VoicePlay-<key>`, `F5VoiceUse-<key>` and, unless it is in use or chosen on all
computers, `F5VoiceRemove-<key>`; any other voice's controls use the first 16 hex
digits of its ID (its reference revision) instead of the key, and its name is not
returned. Every voice's detail line, `F5VoiceDetail-<key>`, returns its length
(or, for a voice made from several recordings, "3 recordings, 10.5 seconds
joined, added ..." and whether the speaking engine learns from each recording or
hears them joined), where a starter voice comes from and why the engine can't
use it; never its name or words. When the speaking engine cannot clone a voice (Chatterbox: 5 seconds or
shorter; GPT-SoVITS: shorter than 3 or longer than 10 seconds, unless one of
its recordings is 3-10 seconds) its title adds
"· wrong length for this engine" and `F5VoiceUse-<key>` is disabled, with the reason
as its help text; Play and Use are also disabled while its recording is still
being copied to this PC. Remove asks with `ConfirmationYes`/`ConfirmationNo` and
removes the voice on every computer. `F5AddVoice` opens *Add a voice*
(`F5AddVoiceDialog`): `F5AddVoicePath` (the recording's full path: almost any
audio or video file), `F5AddVoiceRecording` (read about half a second after the
path changes: "Reading the recording...", then what Martlet found, for example
"MP3, 7.5 seconds. Martlet converts it to a mono 16-bit WAV at 44.1 kHz.", or
why it can't be used, such as "This recording is 45 seconds long. ..."),
`F5AddVoiceName`,
`F5AddVoiceTranscript`, `F5AddVoiceBasis` (whose voice), `F5VoiceRights` (the
rights confirmation) and `F5AddVoiceOk`, which adds the voice (from the
converted WAVs, which are also what Play and Play joined play), shares it and
uses it; `F5AddVoiceProblem` returns why it couldn't (the typed name, transcript
and path are never returned). `F5AddVoiceMore` (passive: it only adds an empty row)
adds another recording of the same voice, whose controls end in its number
(`F5AddVoicePath-2`, `F5AddVoiceRecording-2`, `F5AddVoiceTranscript-2`,
`F5AddVoicePlay-2`, `F5AddVoiceBrowse-2`); with several, each row has
`F5AddVoiceDrop-<n>` (passive) and `F5AddVoicePlayJoined` plays them joined.
`F5AddVoiceRecordings` returns how many recordings there are and, once their
files are read, how long they are joined with the pauses ("3 recordings make one
voice, 10.5 seconds joined with the pauses.", more than 30 seconds, "Reading the
recordings...", or which recording Martlet can't use); never
paths or words. `F5AddVoiceAbout` returns the dialog's intro, which names the
speech-to-text that fills in each recording's words ("... Martlet fills in its
words with Parakeet on this PC ...") or, without one, says to download Parakeet.
With one, each recording's transcript is filled in from the converted WAV once
the file is read, unless the owner typed words there, and `F5AddVoiceHeard`
(`F5AddVoiceHeard-2`, ...) returns how that went ("Filled in by Parakeet on this
PC: 25 words. Check them and fix anything it misheard.", no words heard, "Kept
the words you typed." or why it couldn't; never the words). `F5AddVoiceFill`
(`F5AddVoiceFill-2`, ...) fills them in again, replacing what is there; it runs
speech-to-text (and may send the recording to the Listening host), so it needs
`--allow-ui-effects`. Use, Remove and adding change the voice list and need
`--allow-ui-effects`; Play plays audio and is not for automated verification.
`F5RecordVoice` (passive: it only opens the dialog) opens *Record your own
voice* (`OwnVoiceDialog`); `OwnVoiceCancel` (passive) closes it. `OwnVoiceAbout`
returns its intro (one sentence; `Help-OwnVoiceAbout` has the rest and links to the wiki's *Record your own voice*). `OwnVoiceConsent` is the box that says it is the owner's own
voice; its checked state is readable, and `ui_toggle` on it needs
`--allow-ui-effects`. Each line has its fixed words (`OwnVoiceLine-<n>`, for
example "“Would you enjoy a quiet cup of cocoa while we measure the
treasure?”"), its state (`OwnVoiceLineState-<n>`: "Not recorded yet.",
"Recording..." or "Recorded: 5.4 seconds.") and its Record button
(`OwnVoiceRecord-<n>`, readable as "Record line 2", "Stop recording line 2" or
"Record line 2 again"; off until the box is ticked). `OwnVoiceRecorder` returns
what the microphone is doing or why a line wasn't kept ("Press Record beside a
line, then read it out loud.", "Line 2 recorded; next, record line 3.", "Only
0.8 seconds of speech was heard on line 1, so ..."), `OwnVoiceSummary` how many
lines are recorded and how long they are together ("No lines recorded yet;
record all 4 lines to make your voice.", "2 of 4 lines recorded, 11.6 seconds
together with the pauses.") and `OwnVoiceProblem` why `OwnVoiceOk` (Add voice)
couldn't add the voice ("Record line 1 first.", or that together the lines are
longer than 30 seconds and which line to read again). Record uses the
microphone, and Play (`OwnVoicePlay-<n>`), Play all (`OwnVoicePlayAll`) and
`OwnVoiceOk` play audio or change the voice list; the recordings and the typed
name (`OwnVoiceName`) are never returned.
Passive navigation writes nothing: the list is shown as it would start until a
voice is first used, added or removed. `f5_voices` reads the same list headlessly
and `voice_recording_check` runs the same conversion on a file.

Above the voices, the Voice engine card is an option picker of every way Martlet
can speak on the shown computer, one compact row each, keyed by engine
(`Picker-VoiceEngine-chatterbox`, `-chatterbox-original`, `-chatterbox-nano`,
`-f5`, `-xtts`, `-gpt-sovits` and `-dia`; engines that can't run there are
listed last; when none can, `VoiceEngineNone` says so).
`PickerFacts-VoiceEngine-<key>` reads a row's facts ("NVIDIA GPU · 4.2 GB VRAM ·
0.45 s to first audio"). Clicking a row only shows that engine's details:
`PickerDetail-VoiceEngine` its name and badge ("Chatterbox Turbo ·
recommended", "Chatterbox Nano · in use"), `PickerSummary-VoiceEngine` its
strength, `PickerFact-VoiceEngine-<fact>` every fact (runs on, graphics memory,
memory, processor, download, first audio, quality, voice cloning, laughs and
sighs, emotions, streaming, languages, your recordings, license and whether it
allows commercial use, numbers), `PickerState-VoiceEngine` where it stands
("Speaking on this PC.", "Ready on gpu-pc.", "Setting up on gpu-pc...", or why
it can't run there), `VoiceEngineAbilities-<key>` the rundown of what it can do
("Voice cloning: yes; Laughs & sighs: yes; Emotions: whispering only."; shown
as ✓ yes, ◐ partly and ✕ no, the same as `abilities.summary` in `f5_voices`),
`VoiceEngineFeatures-<key>` its other needs as chips ("Docker, 5 s+ samples,
English" for Chatterbox Turbo; the same list as `features` in `f5_voices`), and
`VoiceEngineUse-<key>` its one button ("Set up and use Dia", "Use XTTS-v2", "In
use Chatterbox Nano"; disabled with the reason as help text when the computer can't
run it). Every option picker shows at most four rows (the one in use, the recommended one and the shown one first);
`PickerMore-<id>` (*Show 3 more*, then *Show fewer*) only shows or hides the rest, so it is a safe click, and a
page may show fewer by default (`Picker-Fallback-*` shows *Off* and the recommended provider, `Picker-CloudThinking-*`
three rows). `PickerCompare-VoiceEngine` opens a table of the facts that differ
(`PickerCell-VoiceEngine-<key>-<fact>`). Under *Another of your computers*, the computer pills
`SpeakingHost-<host ID>` ("gpu-pc · speaking", "laptop · not reachable") only
choose which computer the rows set up, so clicking one is passive;
`SpeakingHostStatus` says when the shown one isn't reachable and
`HostChoices-speaking` when no other computer is paired. Clicking a
`VoiceEngineUse-<key>` button needs `--allow-ui-effects`: it asks one
confirmation, sets the engine up on that computer when needed (a run window) and
switches Speaking to it. Switching cleans up: a host runs one voice engine at a
time (adding one stops the others there; using one already installed stops the
leftovers), and the engine Speaking leaves on another computer, or when it moves
to a Windows or cloud voice, stops there once Speaking has moved (named in the
confirmation; kept when failover keeps the same engine there as a backup).
`SpeakingEngineOthers` (shown only then) names voice engines the speaking
computer still runs besides the one that speaks, for example a host set up before
that rule; `SpeakingEngineRelease` stops them after a confirmation
(`martlet-host remove`, downloads kept) and needs `--allow-ui-effects`.
`SpeakingEngineSharedGpu` (shown only then) warns that the shown computer runs
on Windows and its graphics card also does other jobs (its other roles, and
Thinking in Ollama on this PC), so its voice can fall behind
([Chatterbox](../CHATTERBOX_VOICE.md#sharing-the-graphics-card)); it names the
voice engine there, or says one would share the card before one is set up.
`f5_voices` returns the chosen engine as `chosenEngine`.
