# MCP: Settings

Part of [MCP in Martlet](../MCP.md): the tools and screens of the *Settings* area of `martlet_guide` (settings, prompts, shared settings and the recommended setup). The [index](../MCP.md#pages) lists the other pages.

## Local MCP control (Windows)

`settings_sync_status` reads [one Martlet on every computer](../CLUSTER.md#one-martlet-on-every-computer)
from a data directory (optional absolute `dataDirectory`, default the current
user's): `sync` as above, `state` (`none` before the first sync, else `loaded`),
the copy's `revision` and `count`, and for each shared setting its `key`
(`thinking`, `listening`, `speaking`, `thinking-fallback`, `companion`,
`replies`, `prompts`, `memory`, `lorebooks`, `character`, `character-actions`,
`talk`, `speech-display`, `appearance`, `appearance-custom`, `voice-recognition`, `voice-id`,
`smart-home`, `updates`, a computer's own `pc.<device ID>`, a computer's
`role.<device ID>` (`companion` or `host`: written by that computer, or by
another one asking it to switch), or a newer
Martlet's), `updatedBy`, `updatedAt`,
`revision`, `usesKey`, `characters`, `off` (the value is null) and `here`:
`same` when this PC had exactly that value at its last sync, `different` while
it can't follow it yet (or changed it since), `unknown` when it never had the
setting. `value` is shown only for non-personal settings: each job's route
(`type`, `origin`, `model`, `voice`), the fallback's `origin` and `model`,
memory, how you talk, speech bubbles and subtitles, the theme and the custom
palette's colors (`Colors` by role), recognizing
voices (`on`), what Martlet may do with Home Assistant (`control`,
`allow_sensitive`, `model_tools`), app updates (`checks`,
`interval_minutes`, `auto_install`, `auto_update_hosts`) and each computer's
`pc.<device ID>` (`role`, `host`) and `role.<device ID>`. It never returns keys,
key digests, personality, prompt, lorebook or emote text, or the Voice ID
voiceprint, and contacts nothing.
With [accounts](../ACCOUNTS.md#account-settings), each setting also has a `scope`
(`household` or `account`), and `here` can be `adopting`: the files don't hold
the signed-in account's value yet (a switch waits for it). `account` describes
the account whose settings the data folder's files hold
(`accounts\working-copy.json`): its `id` (32 hex digits), `filesSince`, and
its own copy in `accounts\<id>\shared-settings.json` (`state`, `revision`,
`count` and `settings` with the same fields). `accounts` lists `filesHold` and
the account `folders` on this PC (`id`, `settingsCopy`), IDs only. Both are
null or empty for a data folder from before accounts.

`settings_sync_selftest` (no arguments) rehearses shared settings end to end,
including a simulated Linux companion (`Martlet.Companion.Platform`'s settings,
guardrails, `hosts.json` and `CompanionSync`) that takes a desktop's Thinking route,
key and personality, shares a persona edit back and keeps its own listening, with
the catalog's reason, when Parakeet is shared,
with the production code (`src\Martlet.NodeLinkCheck`, mode `settings`,
`SettingsRehearsal.cs`, run as its own process like `node_link_check`; returns
`{exitCode, report}`): two real gateways (`lab-settings-1`, `lab-settings-2`;
Kestrel, pinned TLS, signed requests, an in-memory `shared-settings.json`) and
three simulated desktops (`lab-desktop-a..c`) with real `settings.json`,
`lorebooks.json` and `shared-settings.json` in a temporary folder, an in-memory
stand-in for Windows Credential Manager (with write times), the desktop's paired
client (`HostSettings.cs`) and the real sync engine and sections
(`Martlet.Core.Sync`). Its steps follow the owner's case: B chose NVIDIA Build
with its key on Sept 1, A chose OpenRouter with its key on Sept 20; after
updating, B (now the companion) syncs first and shares NVIDIA Build stamped
Sept 1, A (now the host PC) syncs and OpenRouter wins, and B takes OpenRouter,
the model and A's key into its own credential store with the choice recorded
and its NVIDIA key set aside, not deleted; the same in the other order on a
third host; a model change (B reuses its key, no new credential); a new key;
offline edits of different settings on both (both kept) and of the same
setting (the later edit wins); a host that was down while a change was made,
restarted with its saved copy and got the change on the next sync; a stale copy
that can't undo newer changes; a newer Martlet's setting passed through; a
Parakeet model a new computer lacks (it waits, records nothing, then follows); a
new computer taking everything without its defaults overriding anything; the
Thinking fallback with its own key, turned off again (the key removed); two
NVIDIA Build models in A's Thinking pool with one key, which reach B beside B's
own model in Ollama and leave again with B's copy of the key; A's Thinking pool
choices (Backup for slow replies and its wait, Busy pool) reaching B while B
keeps its own model; lorebooks;
no key in any desktop file while the hosts' private copy holds them and every
copy ends the same; and an unsigned request refused (HTTP 401). Then a fourth
simulated desktop on this Martlet (`lab-desktop-d`) keeps each person's
settings apart ([account settings](../ACCOUNTS.md#account-settings)): the owner
(Sam) signs in and Sam's own copy takes the personality the older desktops
share; edits on older desktop B and on D reach each other through the owner's
bridge; Alex signs in and the files take the default personality with nothing
of Sam's in Alex's copy; Alex's personality goes only to Alex's copy while the
household copy and B keep Sam's; back to Sam, Sam's personality returns and the
hosts keep both people's copies apart; a copy with an API key is refused before
sending and an unsigned request for a person's settings is refused. Loopback
only; the folder is deleted and the real vault is never touched. Two more simulated desktops (`lab-desktop-e`, `lab-desktop-f`) also carry
the files that travel as their own shared settings (`voice-clips`,
`character-touch-zones`, `character-eyes`, `vroid-hub-looks`, `discord` with
its bot token and `messaging` with its secrets, through the real sections and a
stand-in credential store): E's choices, zones, credit, bot and chats reach F,
F connects no bot, and no token is in F's files or the shared value.

`api_keys_status` reads the [API keys](../API.md) of this PC's Martlet network
from a data directory (optional absolute `dataDirectory`, default the current
user's; the script supplies its disposable one): `state` (`none`, `loaded` or
`unreadable`), counts of `live`, `revoked` and `expired` keys, and each key's
`id`, `name`, `scopes`, `createdBy`, `createdAt`, `expiresAt`, `revoked`,
`expired`, `updatedBy` and `hasVerifier`. It never returns a key or its
verifier (Martlet keeps no key) and contacts nothing.

`openai_endpoint_status` reads Share › *Share your language models* (the
[OpenAI-compatible endpoint](../OPENAI_ENDPOINT.md)) from a data directory
(optional absolute `dataDirectory`): `settings` (`state` of
`openai-endpoint.json`, `publicAddress` and `frontDoorHostId`), `frontDoor`
(`open`, the `hostId` and `port` the Share page last saw it open on, where it
is `listening`, the `upstream` a proxy sends to, and the
`lastAddressCheck` text of *Check address*), `paths` (`/v1/models` and the
POST paths), each key with the `openai` scope (`id`, `name`, `scopes`,
`priority`: `your-martlet-first` or `equal-to-background-work`, `createdBy`,
`createdAt`, `expiresAt`, `revoked`, `expired`, `updatedBy`), `models` (what
the Share page last read, `openai-models.json`: each host's models or problem,
the models of hosts friends share with this PC, the front door and the last
address check) and `setup` (the `help` text behind the *?* next to the
address, and the `tailscale`, `proxyManager`, `nginx`, `caddy` and
`cloudflareTunnel` text *Show setup* gives for the front door). It never
returns a key or its verifier and contacts nothing.

`prompts_status` reads Companion › Prompts from a data directory's
`settings.json` (optional absolute `dataDirectory`, default the current
user's): `state` (`none`, `loaded` or `unreadable` with `problem`),
`total`, `edited` and `emptied` counts, and every internal prompt Martlet
sends to the Thinking model (`id`, `group`, `title`, `placeholders`,
`state` `builtin`, `edited` or `empty`, `characters` and `tokens`), plus
`tokens`, the estimate for all prompts together. Token counts are Martlet's
own request-size estimate (`BoundedTextInput.TextTokens`: about a token per
three UTF-8 bytes, no message overhead; 0 for an emptied prompt), not a
provider's count, and cover each prompt as written, before placeholders are
filled in. With an
`id` it also returns `prompt` with that prompt's effective `text` (the
saved edit or the built-in text), exactly what Martlet fills in and sends.
`shortFirstSentence` is Companion › Replies › *Short first sentence*: `on`
(on by default), `chosen` (whether a choice is saved), the prompt's `state`,
and `spokenClosing` and `unspokenClosing`, what closes a spoken and an
unspoken reply's instructions, built by the desktop's own
`PromptSettings.ReplyClosing` (the pinned skills, *Keep the conversation
going*, the short first sentence prompt, then *Reply
length*; *Reply length* alone when it is off, emptied or the reply isn't
spoken). On Companion › Replies, `RepliesShortFirstSentence` reads the chosen
option (*On* or *Off*; choosing one with `ui_select` saves it, so it needs
`--allow-ui-effects`) and `RepliesNow` says whether spoken replies start with a
short first sentence.
`adultContent` is Companion › Replies › *Adult content*: `on` (off by
default), the *Adult content* prompt's `state`, and while it is on the
`instructions` it adds after the *One moment* prompt of every reply and screen
remark (never in a Discord call), exactly as the desktop sends them.
`RepliesAdultContent` reads the chosen option (*Off* or *On (18+)*; choosing
one with `ui_select` saves it, so it needs `--allow-ui-effects`), and
`RepliesNow` adds *adult content is on* while it is.
`personalContent` is Companion › Replies › *Personal content*: the `level`
(`Private` by default, `Messages` or `Everything`), the `prompt` that fills
`{private}` in the screen and camera prompts (*Personal content: private* or
*Personal content: shared*) with its `instructions`, and `seesPrivateWindows`
(true only with *Everything*: screen looks then also see password managers and
private windows). `settings_set` with path `generation.personal_content` and
value `"private"`, `"messages"` or `"everything"` changes it.
`RepliesPersonalContent` reads the chosen option (*Keep private*, *Read my
messages* or *Everything*; choosing one with `ui_select` saves it, so it needs
`--allow-ui-effects`), and `RepliesNow` adds *Martlet may read your messages*
(and *see private windows* with *Everything*).
On the page, `PromptsNow` reads how many prompts are edited or emptied,
`PromptsTokens` the estimated tokens of all prompts together as typed
(*All prompts together: about 3,456 tokens (each request sends only the prompts it needs).*) and
`PromptState-<id>` each prompt's state (*Built-in text, about 52 tokens.*,
*Edited, about 52 tokens.*, *Empty: nothing is sent for this prompt.*, with
*, saving.* at the end while an edit is still being saved); none returns prompt text.
Each prompt's title has a *?*, `Help-Prompt-<id>`: its card (`Help-Prompt-<id>-Card`)
says when Martlet sends the prompt and which words in braces it fills in, and
`Help-Prompt-<id>-Wiki` links to the wiki's *Prompts* section. `Help-Prompts`,
`Help-PromptTokens` and `Help-PromptsBuiltIn` explain the page.
`OpenPrompts` (Personality's *Edit
prompts*) only opens the page. There is no Save button: an edit saves a moment
after typing stops (or at once when another page opens), into the newest saved
settings, and `PromptsNow` then reads the new counts. The editors
`Prompt-<id>`, their `PromptReset-<id>` buttons and `PromptsDefaults` write
settings, so they need `--allow-ui-effects`; `ui_set_text` with an empty
`text` empties a prompt.

`skills_status` reads Companion › Skills from a data directory's
`settings.json` (optional absolute `dataDirectory`): `state`, `total`, `own`
and `edited` counts, and every skill (`key`, `name`, `useWhen`, `builtIn`,
`pinned`, `tools`: the reply tools it explains, `state` `builtin`, `edited`,
`own` or `empty`, and `tokens`). `everyReply` has the `pinned` keys, their
`text` exactly as the closing of every reply's instructions carries it, and
its `tokens`. `movedPrompts` maps the prompt IDs that became skills to their
skill keys. With a `key` it also returns `skill`, that skill's text.
`settings_set` with path `skills` and a value such as
`{"skills":[{"key":"recipes","name":"Recipes","use_when":"...","body":"...","pinned":true}]}`
saves edits and own skills (an edit equal to the built-in skill is dropped), and
`settings_set` on `prompts` moves an edit of a prompt that became a skill to
its skill. On the page, `SkillsNow` reads the counts and the pinned skills'
names and tokens, and `SkillState-<key>` each skill's state (*Built-in skill;
pinned: every reply gets it, about 51 tokens.*, *Your own skill; empty:
nothing is sent.*, with *, saving.* at the end while it saves); neither
returns a skill's text. `SkillName-<key>`, `SkillUseWhen-<key>`, `Skill-<key>`,
`SkillPinned-<key>`, `SkillReset-<key>`, `SkillDelete-<key>` and `SkillsAdd`
write settings, so they need `--allow-ui-effects`.

`chattiness_status` reads Companion › Vision › **How often it comments** (the
same choice as Listening › Watch along) from a data directory's
`talk-preferences.json` (optional absolute `dataDirectory`, default the current
user's): `choice` (*Quiet*, *Normal*, *Chatty* or *Martlet decides*), `saved`
and `source` (`saved` or `default`), `martletDecides`, `visionOn` (on unless
saved off) and `hearPcOn` (replies are told about Martlet decides only while
one of them is on), `visionLooksAt` (what Companion › Vision looks at: `whole
screen` unless saved as `active window`, `camera` or `camera address`),
`startsAt` (*normal*), `tags` (every spelling a reply switches with) and
`prompts` (`state` of settings.json, `decides`: Companion › Prompts ›
*Chattiness: Martlet decides* exactly as it is sent, and `notes`: *Chattiness
right now* for each level). Its `rehearsal` sends sample replies (or `reply`,
up to 1,024 characters of one line) through the production speech segmenter
and chat stripper with those tags offered: `spoken`, `shown`, `silent` (the
shown text is `[pass]`), `tags` and `switchesTo`. The level a running
conversation picked shows in the talk window's `LiveChattiness` line (*Chattiness:
quiet (Martlet decides).*) and in
`logs_tail` as *Chattiness: Martlet went from normal to quiet (your message;
Martlet decides).* It reads no credentials and contacts nothing.

`recommended_setup_status` shows Home's **Recommended setup** without the
desktop. It builds the network recommender's request with the desktop's own
builder (`RecommendedSetupInputs`) and runs the production recommender
(`NetworkRecommender`). Give an absolute `dataDirectory` (default the current
user's; the script gives a disposable one) to plan from that directory's
`hosts.json`, `host-hardware.json`, `cluster.json`, `settings.json`,
`work-sharing.json`, `thinking-pool.json` and `speaking-engine.txt`, and this
PC's page choices from `talk-preferences.json` (vision on, Let ... hear my
voice), `sense-models.json`, `reading.json` and `smart-home.json` (its address
only, never the token). Its `configuredProviders` lists the hosted providers
that count as having a key, and `savedKeys` says where each key is saved (for
example `{ "provider": "nvidia-build", "where": "the Thinking pool" }`; never
the key): a key saved anywhere in Martlet counts, so the free-key prompt stays
away. A data
directory has no live host checks: a host that `node-presence.json` (the
desktop's presence report) last saw not answering counts as offline, as the
desktop plans it, and every other host counts as online. Its roles are the
shared plan's record. This PC's hardware is its own host service's
report (the desktop reads this PC live). Give `fixture: "network"` to plan the
built-in four-computer network instead (**NOT real computers**): this PC, a
companion PC with an RTX 4080 that runs Thinking, Speaking and Listening on its
own host service; `gpu-box`, a Linux host PC with an RTX 4090 and nothing
installed; `DIVA`, a companion PC whose host service runs Deep thinking; and
`old-box`, a host without a hardware report. Give `fixture: "offline"` to plan
the built-in offline network (**NOT real computers**): this PC, a companion PC
with no graphics card, 4 GB memory and 4 threads, and two hosts, `MIKU` and
`IMOUTO`, that haven't answered for 155 minutes (MIKU ran Thinking, Speaking
and Lip-sync, IMOUTO ran Listening), with no API key saved. Its recommended
setup has nobody doing Thinking, so the desktop's review shows its "Martlet
can't reply" problem. Give `fixture: "served"` to plan the built-in served
network (**NOT real computers or apps**): this PC alone, a companion PC with an
RTX 5090 (32 GB) that thinks with Gemma 4 E2B in Martlet's Ollama. LM Studio on
it serves `qwen3-32b` (19.8 GB) and an embedding model, and Ollama serves
`llama3.3:70b` (42.5 GB). Its recommended setup thinks with
`served:qwen3-32b` in LM Studio; the 70B model doesn't fit beside the voice.
Give `fixture: "hostmodels"` to plan the built-in host-models network (**NOT
real computers or models**): this PC, a companion PC with no graphics card, and
`gpu-box`, a Linux host with an RTX 4090 (24 GB) that thinks with Gemma 4 E4B
and keeps `qwen2.5:14b` downloaded. *Prefer models your hosts already have* is
on, so its recommended setup thinks with `qwen2.5:14b` on `gpu-box` and
downloads nothing.
Give `fixture: "better"` to plan the built-in better-setup network (**NOT real
computers or measurements**): this PC, a companion PC with no graphics card,
and `gpu-box`, a Linux host with an RTX 4090 (24 GB) that thinks with Gemma 4
E2B, with made-up measurements (its first word 0.18 s over 3 replies, 3.6 GB in
Ollama on the card). Thinking is locked, so the recommendation keeps Gemma 4
E2B and `suggestions` has *Gemma 4 12B on gpu-box: smarter, ...*; with
`preferences.locks` `[{ "job": "thinking", "locked": false }]` the change
`ChangeModel` to `gemma4:12b` is in the recommendation and the suggestion is
`applied`.
Give `fixture: "bighost"` to plan the built-in big-hardware network (**NOT real
computers**) with the model catalog: this PC, a companion PC with no graphics
card, and `big-box`, a Linux host with five RTX 5090s (32 GB each) that thinks
with Gemma 4 E2B and runs Deep thinking with Gemma 4 E4B. Its recommendation
keeps live Thinking on the fast Gemma 4 E2B and has a `ChangeModel` that
switches Deep thinking to a smarter model a free card has room for (Gemma 4 26B
with the snapshot), because Deep thinking runs in the background. Give
`fixture: "bigsplit"` for the same network with Deep thinking on
`llama3.3:70b` (about 46 GB), which no single 32 GB card has room for: the
recommendation keeps it, split across two cards no live job uses (a
`MoveToGpu` change with `gpuIndexes`, the machine's `cards` entry with
`split: true`). Each change also has `gpuIndex` and `gpuIndexes` (0-based), and
each machine `cards` (each role's cards, 1-based, as the review says them) and
`gpus` (each card's `usedGb` and `capacityGb`). Give `fixture: "hostserved"` for
a host (`lab-box`, an RTX 4060 with 8 GB and 128 GB of memory, installed
natively) whose machine report lists the chat models LM Studio serves there
(`openai/gpt-oss-120b`, loaded, and an embedding model): `servedModels.found`
lists it with `computer: "lab-box"` and the host's own address, and the
recommendation has an `AddRole` of the `deep-thinking-app` role with
`model: "openai/gpt-oss-120b"` and `choices` (`APP_PORT` and `APP_PATH`), so
the host's Thinking pool place relays to it instead of Gemma 4 E4B. Live
Thinking stays a fast model. Each change has `choices` (more choices the role
is installed with) when it names any.
Give `lookOnThisPc: true` with a data directory to ask the model apps on this
PC (Ollama, LM Studio, llama.cpp, vLLM and the others Companion › Thinking
finds; 127.0.0.1 only) for the models they serve, as the desktop's review does.
It asks nothing when *Use models your apps already run* is off.
It plans with the directory's recommendation preferences
(`recommendation-preferences.json`; before the owner saved any, the defaults
with the two model choices from `recommended-setup.json`), or with the
fixture's. Give `preferences` to plan with others instead, nothing saved; each
field is optional: `quality` (`balanced`, `quick`, `smarter`), `online`
(`backup`, `never`, `yes`), `preferHearing`, `hostGpuShare` (90, 75, 50),
`useServedModels`, `preferHostModels`, `games` (`[{ "device": "fixture-desk",
"plays": true }]`, by Martlet device ID) and `locks` (`[{ "job": "thinking",
"locked": false }]`: `thinking`, `listening` or `lip-sync`; unlocking here lets
Martlet choose whatever runs today). An unknown value is an error. A data
directory has no game library guess: a companion PC without an answer counts
as No. It plans as the desktop does, with the model catalog
([Recommendation design](../RECOMMENDATION_DESIGN.md#build-stages), stage 3): the
data directory's daily copy, else the snapshot shipped with Martlet (a fixture
always uses the snapshot), its local facts and the data directory's route facts
(`model-abilities.json`), plus Martlet's own list. Speed estimates are for this
PC's best graphics card. Give `catalog: "seed"` to plan with Martlet's own list
alone, as before the catalog, to compare. The result has:
- `planning`: where the options came from. `catalog` (`models` or `seed`),
  `from` (in words, with the copy's date), `counts` (options by origin:
  `Seed`, Martlet's own list; `Catalog`, a hosted model the catalog chose by
  rule; `LocalFacts`, an open-weight model sized and timed from its local
  facts; `Served`, a model your model app serves), `jobs` (each job's `option`,
  `model`, `from` and `fromWords`), `deepThinking` (each Deep thinking role's
  computer, model, origin and tier), `onlineDeepThinking` (the hosted Deep
  thinking model suggested when no host card fits a model at least one quality
  step smarter than Thinking's and online services allow it, else null) and
  `catalogOptions`: every Thinking, Deep thinking, Vision and Hearing option the
  catalog gave or added facts to, with `tier`, `smartness` (words, never a rank
  number), `smartnessCredit` (LMArena's rating with its CC-BY-4.0 credit),
  `firstWordMs`, `wordsPerSecond`, `graphicsGb`, `takes`, `callsTools`, `free`,
  `onlyThisPcsOllama` (the host roles don't offer it), `evidence` and `source`;
  and `providerModels`: each named provider's suggested model for live
  Thinking (`thinking`, what Companion › Thinking, If Thinking fails and the
  welcome tour's key step prefill) and for the Thinking pool (`thinkingPool`,
  the smartest free one), with `presetDefault`, today's fixed suggestion.
- `preferences`: what it planned with: `saved` (the directory has
  `recommendation-preferences.json`), `overridden`, `quality` and
  `firstWordTargetMs`, `online` and `hosting` (the planners'
  `HostingPreference`: `Backup`, `PreferLocal` or `Balanced`), `preferHearing`,
  `hostGpuShare`, `useServedModels`, `preferHostModels`, `games` (each
  companion PC's `device`, `computer`, `plays` and `answered`),
  `keepGpuForGames` (the computers planned as gaming PCs) and `describe` (one
  line).
- `source` and `computers`: each computer's `id` (the cluster plan's host ID,
  else the device ID), `name`, `kind` (`Companion` or `Host`), `thisPc`,
  `hasHostService`, `manageable`, `online`, `planned` (false: left as it is),
  `hardware`, `roles` (`kind=model`) and `downloads` (`kind=model`: the role
  models its host service keeps from roles it turned off, from the `downloads`
  of its report in `host-hardware.json`).
- `notes`: why a computer is left as it is.
- `servedModels`: `use` (*Use models your apps already run* is on), `looked`
  (models were looked for: the fixture's, or `lookOnThisPc`), `found` (each
  model's `model`, `app`, `address`, `computer`, `chat` (false for an
  embedding, speech or picture model, which Thinking can't use), `downloadGb`
  (the app's own size: Ollama's `/api/tags`, LM Studio's `/api/v1/models`
  `size_bytes`, or its older `/api/v0/models` quantization at the parameters
  in the name), `estimatedGb` (the graphics memory the planner counts),
  `loaded` and `loadedGb` (the app has the model loaded now: Ollama's
  `/api/ps` `size_vram`, or LM Studio's loaded state, where `loadedGb` is 0
  because it doesn't say the memory), `newGb` (what the first-run plan adds to
  the graphics memory in use, which counts a loaded model already), `sizeFrom`
  (where the size comes from: the app's size, the model catalog's parameters
  or the name) and `option` (`served:<model>`, the job option the
  recommendation uses)), `choices` (the review's explicit Thinking choices,
  in its order: each `button` `RecommendedSetupUseServed-<n>`, its `line`,
  `model` and `address`; empty when the choice is off) and `note`.
- `hostModels`: `prefer` (*Prefer models your hosts already have* is on: the
  fixture's, or `PreferHostModels` in the directory's
  `recommended-setup.json`; off by default), `found` (each Thinking chat model
  a host that answers runs or keeps downloaded: `model`, `computer`, `running`
  (false: kept downloaded) and `tier` (the quality tier the planner ranks by))
  and `note`.
- `today`: each job's `host`, `off`, `option` and `pool` (the other computers
  that take its requests when the one in charge is busy, in Sharing work
  order), `thinkingPool`, `thinkingPoolOptOut`, `voiceEngine`, `preference`,
  `off` (the optional parts turned off in the review on this PC, from
  `recommended-setup.json`) and `choices` (this PC's choice for each part set
  on its Companion page: `part` (Vision, Reading, Hearing or Smart home),
  `on`, `option` (the catalog option, such as `vision:thinking`,
  `reading:windows-ocr`), `model`, `where` and `host`).
- `recommendation`: `alreadyOptimal`, `worthAsking`, `fingerprint`,
  `components` (every part in priority order: `rank`, `part`, `need`
  (`needed` or `optional`), `on`, `where` (*Gemma 4 E4B in Ollama on This PC's
  NVIDIA GeForce RTX 4070*, or *Off: Martlet doesn't sing.*), `why`, `canBeOff`,
  `ownerOff`, `offInReview` (the review has an Off box for it; false for the
  parts this PC sets on their Companion page) and `page` (*Companion ›
  Vision*)), the
  `changes` in the order Reconfigure makes them (`kind`, `computer`, `summary`, `why`, `away` (the sentence in
  `why` about a computer that isn't answering, which the review leaves out),
  `benefit`, `roleKind`, `model`, `job`, `needsSomeoneThere`, `downloadGb`,
  `onProcessor` (the role installs its processor variant with
  `choice.accelerator=cpu`, because its computer's card has no room for it)),
  each computer's recommended `roles`, `why` and `load` (percent of graphics
  memory, memory and processor), the `jobs`, the `thinkingPool`, `cannotReply`
  and `cannotReplyNote` (nobody does Thinking, and why, such as *No computer
  has room for a Thinking model, and no free API key is saved.*),
  `cannotSpeak` and `cannotSpeakNote` (no computer and no hosted voice with a
  saved key can speak, and how to give Martlet a voice), `offline`
  (each computer that isn't answering, so the plan leaves it out: `computer`,
  `minutes` and its `note`) and
  `notes`. The review window reads `cannotReply` and `offline`, not the
  sentences in `notes`.
- `companionInUseAsks`: what a companion PC someone uses would do after an
  automatic check (`Ask`, `Wait` or `Nothing`, and why), with `declinedHere`
  (this setup is in the directory's `recommended-setup.json`).
- `measured`: the numbers Martlet measured, which the recommendation plans with
  instead of its estimates: `firstWords` (from `model-speed.json`: each server's
  `host`, the `model`, `ms` (the middle of the last replies) and `replies`),
  `memory` (from `model-memory.json`: `host`, `model`, `gb`, `graphicsGb`,
  `onGraphicsCard`, `contextTokens`; this PC's Ollama and each paired host's
  `loaded_models`), `replaced` (each catalog option whose numbers they
  replaced: `option`, `firstWordMs` and `estimatedFirstWordMs`, `gpuGb` and
  `estimatedGpuGb`, `describe`) and `note`.
- `locks`: each job with a lock (`thinking`, `listening`, `lip-sync`): `today`
  (the option doing it today), `state` (`Kept`: never chosen, so Martlet keeps
  it; `Locked`; `Unlocked`; `ChangedByHand`: unlocked, but it runs another choice
  now, so it counts as locked), `unlocked` (the recommendation may change it) and
  `chose` (the choice it remembers).
- `suggestions`: each job's clearly better choice: `job`, `option`, `host`,
  `today`, `kind` (`Smarter` or `Lighter`), `why`, `locked` (only shown),
  `applied` (in `recommendation.changes`) and `text`.
- `betterSetup`: `available` (a suggestion exists), `fingerprint`, `seenHere`
  (the owner saw it in a review; `recommended-setup.json`) and `homeButton`
  (*A better setup is available* when Home's Recommended setup button says so,
  else null).
- `retired`: with a data directory, the Thinking route (a Chat Completions
  endpoint) and the If Thinking fails route: `route`, `origin`, `model`,
  `retired`, `source` (an HTTP 410 answer, Martlet's list or the model
  catalog's expiration date), `since` and `replacement` (the model Home's
  **Use** button proposes). Null without such a route.
It is read-only and reads no keys. It contacts nothing, except the model apps
on this PC (127.0.0.1) when `lookOnThisPc` is true. On the desktop, Home's
`HomeRecommendedSetup` is in `SafeClicks`. In a Martlet network it opens the
review window (`RecommendedSetupWindow`). On a PC alone, it opens Set it all up
for me's question (`DefaultSetupQuestion`). In the review, `ui_snapshot` reads
every `RecommendedSetup*` text: `RecommendedSetupBannerTitle` and
`RecommendedSetupBannerText` (at the top: *No computer can do thinking, so
Martlet can't reply* when nobody does Thinking in the recommended setup, else
*Add a free API key* while no hosted provider has a saved key; hidden
otherwise), `RecommendedSetupTitle`,
`RecommendedSetupSummary`, `RecommendedSetupPart-<Part>` (the priority list:
`RecommendedSetupPart-Thinking` reads *1. Thinking (needed): Gemma 4 E4B in
Ollama on This PC's NVIDIA GeForce RTX 4070.* and an optional part that is off
*7. Singing (optional): Off: Martlet doesn't sing.*, with why; parts are
`Thinking`, `Voice`, `Listening`, `Character`, `LipSync`, `Vision`, `Reading`,
`Hearing`, `People`, `DeepThinking`, `SmartHome`, `Singing`, `Pictures` and `Motion`), `RecommendedSetupOffline` (one sentence about the
computers that aren't answering, such as *MIKU and IMOUTO haven't answered for 2
hours, so Martlet plans without them.*; each change's why then leaves those
words out; their `RecommendedSetupTarget-<n>` reads *Recommended: left out while
it isn't answering*), `RecommendedSetupServedModels` (the chat models the model
apps on this PC serve, biggest first, such as *Found: llama3.3:70b in Ollama
(about 46 GB), qwen3-32b in LM Studio (about 22 GB).*, *No model app on this
PC serves a chat model now.* or *Off: Martlet plans only with its own models
and your keys.*),
`RecommendedSetupPartFacts-<Part>` (under Thinking and Deep thinking once the
model catalog is loaded: what the model takes, how smart it is in words or
LMArena's credited rating, its first sentence, words a second, graphics memory,
free, and where it comes from; under Deep thinking the online model suggested
when no host card fits a smarter one), `RecommendedSetupNote-<n>` (the notes,
such as *Deep thinking: NVIDIA Build: Kimi K3 (free endpoint) is free and
smarter than Thinking's model ...*),
`RecommendedSetupChange-<n>` (its name is the
benefit, the summary and why), `RecommendedSetupComputer-<n>`,
`RecommendedSetupComputerKind-<n>`, `RecommendedSetupToday-<n>`,
`RecommendedSetupTarget-<n>`, `RecommendedSetupLoad-<n>`,
`RecommendedSetupBar-<n>-<vram|ram|cpu>` (its `help` is the bar's hover
breakdown, as for `DeviceResource-<key>` on Devices), `RecommendedSetupJob-<n>`,
`RecommendedSetupSituation-<i>` (the title of one of the three plans:
*Normal*, *While gaming* or *Host away*) and `RecommendedSetupSituation-<i>-<j>`
(where each live job goes in that plan, such as *On DESK: Thinking moves to
gpu-box (qwen3:8b), then NVIDIA Build, then DESK's own model ... until the
game ends.*; `recommended_setup_status` returns the same plans in
`situations`),
`RecommendedSetupManual-<n>`, `RecommendedSetupDownloads`,
`RecommendedSetupPreflight-<n>`, `RecommendedSetupTerms-<n>`,
`RecommendedSetupSecret-<n>` (the label only; the key box `SetupSecretInput-<n>`
is never read), `RecommendedSetupNote-<n>` and `RecommendedSetupStatus` (the
preflight state, or why Reconfigure couldn't start, such as *Martlet is already
reconfiguring your computers. Background tasks shows its progress.*).
`RecommendedSetupClose` only closes the window. The banner's
`RecommendedSetupFreeKeyAdd` (*Add your key*, or *Open Thinking* when a key is
saved) is in `SafeClicks`: it closes the review and opens Companion ›
Thinking with NVIDIA Build chosen for the key. When Martlet can't reply, the
key goes to Thinking itself (A cloud provider: `SetupCloudKeyStatus-Thinking`
reads *Paste your NVIDIA Build API key...*); else to If Thinking fails
(`FallbackKeyStatus` reads the same), so a local model still answers first.
When a key for the provider is saved anywhere else in Martlet (for example in
the Thinking pool, also from another computer), there is no prompt, and these
lines read *Your NVIDIA Build key from the Thinking pool is saved: leave this
empty to use it here too...*; saving with the box empty copies that key.
The status line (`Settings action result`) says which box to fill. Its `RecommendedSetupFreeKeyGet` (*Get
a free key*) opens NVIDIA Build in the browser, so it is not a safe click.
After a key is saved, Home plans again and opens the review with the new setup
when the review sent the owner there. `RecommendedSetupApply`
(Reconfigure: it changes every computer), `RecommendedSetupCancel` (Not now:
it saves `recommended-setup.json`) and each optional part's Off checkbox
`RecommendedSetupOff-<Part>` (`ui_toggle`: it saves the part as off, or on
again, in `recommended-setup.json`, and the review opens again planned that
way; Vision, Reading, Hearing, People and Smart home have none, because their
Companion page turns them off) and Your preferences need `--allow-ui-effects`:
`RecommendedSetupQuality` (`ui_select`: *Balanced: first word in 0.4 s or
less*, *Quick replies: first word in 0.25 s or less* or *Smarter replies:
first word in 1 s or less*), `RecommendedSetupOnline` (`ui_select`: *Only as a
backup*, *Never* or *Yes, when they're faster or smarter*),
`RecommendedSetupGames-<n>` (`ui_toggle`, one per companion PC, this PC
first; *(Martlet's guess)* while it is not answered), `RecommendedSetupHearing`
(`ui_toggle`, *Prefer models that hear you*), `RecommendedSetupHostShare`
(`ui_select`: *Up to 90%*, *Up to 75%* or *Up to 50%*),
`RecommendedSetupUseServedModels` (`ui_toggle`, *Use models your apps already
run*, on by default) and `RecommendedSetupPreferHostModels` (`ui_toggle`,
*Prefer models your hosts already have*, off by default). Each saves
`recommendation-preferences.json` (shared with your other computers) and the
review opens again planned that way; the values read the choice in effect and
`RecommendedSetupPreferences` reads the section's intro. Settings ›
*Recommended setup preferences* has the same controls with the
`Recommendation` prefix (`RecommendationQuality`, `RecommendationOnline`,
`RecommendationGames-<n>`, `RecommendationHearing`, `RecommendationHostShare`,
`RecommendationUseServedModels`, `RecommendationPreferHostModels`; also
`--allow-ui-effects`), and `RecommendationPreferencesStatus` reads *Saved for
all your computers. ...* after a change. `RecommendedSetupHostModels` reads the models your hosts
keep (*Found: qwen2.5:14b on gpu-box.*), or that the choice is off. While *Use
models your apps already run* is on and an app on this PC serves chat models,
`RecommendedSetupServedHeading` (*Or choose one of the models your apps already
run for Thinking:*) lists each as `RecommendedSetupServed-<n>` (*qwen3-32b in LM
Studio (about 22 GB)*, biggest first) with its `RecommendedSetupUseServed-<n>`
(*Think with it*): it closes the review and switches Thinking to that model in
that app, a change by hand that Recommended setup keeps, so it needs
`--allow-ui-effects`. Reconfigure
closes the review and starts the background task *Reconfigure your computers*
in its run window: `HostRunStatus` and `TaskState-<id>` read the progress
(*Configuring your computers: 0 of 2 finished. gpu-box: Installing Chatterbox
Turbo (1 of 6).*), then the outcome (*Reconfigured your computers: 9 of 9
changes made.*), and `logs_tail` `host-runs` returns its output: the numbered
changes, each computer's steps (*gpu-box: done. All 6 changes made.*), what
each host engine prints and each change's outcome (*Done: ...*, *Failed: ...* or
*Needs you: ...*). Cancel task stops the changes not made yet (*Needs you: ...
Stopped before it ran.*) and the task reads *Canceled at ...*. To drive
Reconfigure without your computers, set `MARTLET_SIMULATE_RECOMMENDED_SETUP` to a
number of seconds (1-600) before launching the desktop (FIXTURE, **NOT real
computers**): Home's Recommended setup then plans the fixture network that
`recommended_setup_status` plans with `fixture: "network"` (also on a PC
alone), and Reconfigure applies it to simulated computers. Also set
`MARTLET_SIMULATE_RECOMMENDED_SETUP_NETWORK` to `offline` to plan the
`fixture: "offline"` network instead, to `better` for the `fixture:
"better"` network (a host that thinks with Gemma 4 E2B; put a
`model-speed.json` in the data directory to plan with measured first words), or
to `served` for the `fixture: "served"` network (model apps on this PC serve
models that Martlet's own list doesn't have). If the review can't open, Home's
status line (`Settings action result`) reads *Martlet couldn't open the
recommended setup: ...* and the log keeps why. Each role change
takes that many seconds and writes `FIXTURE` lines; nothing is installed,
contacted, saved or shared, and the run shows on this PC's Home
(`HomeConfiguringStatus`) only.

The review's **Your choices** (`RecommendedSetupChoices`, intro
`RecommendedSetupChoicesIntro`) has one row for Thinking, Listening, Lip-sync
and the voice: `RecommendedSetupChoice-<job>` (*Thinking: Gemma 4 E2B on
gpu-box. Locked: ...*; jobs are `thinking`, `listening`, `lip-sync` and
`speaking`), `RecommendedSetupLockState-<job>` (the lock in words: kept,
locked, unlocked, or changed by hand; the voice is *Always yours*),
`RecommendedSetupLock-<job>` (`checkedState` `On` while locked; `ui_toggle`
saves `recommendation-preferences.json` and the review opens again, so it
needs `--allow-ui-effects`), `RecommendedSetupSuggestion-<job>` (*Suggested:
Gemma 4 12B on gpu-box: smarter, and its first word comes in about 0.4 s
(today about 0.18 s).*, plus *The recommendation makes this change.* when it
is in the changes) and `RecommendedSetupUseSuggestion-<job>` (in `SafeClicks`:
it closes the review and opens it again with that suggestion planned once;
nothing is saved, and the job stays locked). `RecommendedSetupMeasured` reads
the numbers measured on your computers that the plan used (*gemma4:e2b: first
word 0.18 s (estimate 0.15 s) from 3 replies, measured on
https://gpu-box.lan:8443*). Three minutes after start, after the daily model
catalog refresh and when a computer comes back or stays away, each companion
PC plans again off the reply path; while a suggestion waits that the owner
hasn't seen in a review, Home's `HomeRecommendedSetup` reads *Recommended
setup: a better setup is available* and `HomeBetterSetup` (under its label)
reads *A better setup is available* (collapsed otherwise). The desktop log
says *Recommended setup: a better setup is available after ...* with each
suggestion. When the Thinking model or the If Thinking fails model is retired
on its server, `HealthIssue-thinking-retired` (or `HealthIssue-fallback-retired`)
names the replacement, and `HealthFix-thinking-retired-use-replacement` (or
`HealthFix-fallback-retired-use-fallback-replacement`) saves it, so it needs
`--allow-ui-effects`.

When an automatic
check finds a better setup, Home shows `HealthIssue-recommended-setup`. Its
Review (`HealthOpen-recommended-setup-review`) opens the review, and its Not now
(`HealthFix-recommended-setup-decline`) saves `recommended-setup.json`, so it
needs `--allow-ui-effects`. When nobody does Thinking in that setup, the issue
is a warning titled *No computer can do thinking, so Martlet can't reply*.
Without a saved key its fixes are `HealthOpen-recommended-setup-free-key`
(*Add your key*: Thinking itself, A cloud provider with NVIDIA Build),
`HealthFix-recommended-setup-free-key-get` (opens the browser), Review and Not
now; with a key, `HealthOpen-recommended-setup-thinking` (*Open Thinking*). While no hosted provider has a saved key, Home's
Thinking problems (`HealthIssue-thinking-setup` and
`HealthIssue-job-thinking`) add *A free API key from NVIDIA Build keeps
Martlet able to reply when your computers can't.* and the passive
`HealthOpen-<issue>-free-key` (*Add your key*), and Companion › Thinking shows
the same tip (`FreeKeyTip-Thinking`) with `FreeKeyAdd-Thinking` (in
`SafeClicks`) and `FreeKeyGet-Thinking` (opens the browser) until a key is
saved or A cloud provider is chosen. `SetupCloudGetKey-Thinking` (*Get a free
key*) shows under A cloud provider while NVIDIA Build has no saved key.

`setup_run_status` shows how applying the recommended setup to all your
computers stands ([Applying the recommended setup](../CLUSTER.md#applying-the-recommended-setup),
[Configuring](../CLUSTER.md#configuring)), from a data directory (optional
absolute `dataDirectory`): `state` (*none* without `shared-settings.json`,
*no-runs*, *loaded*), every computer's published run (`setup-run.<device>`
entries: `runId`, `startedBy`, `startedAt`, `updatedAt`, `finishedAt`,
`active`, `shown`, `summary` and each computer's `machineId`, `state`
*Pending*, *Configuring*, *Done*, *Failed* or *NeedsAttention*, `step`, `done`
and `steps`; an entry a newer Martlet wrote is `readable: false`), `plan` (who
does each job in `cluster.json`, with `failover` and `movedFrom`) and `sharing`
(Sharing work: each job's `shares`, `order` and `never`). Machine IDs, role
names and counts only. Read-only.

`setup_run_check` rehearses applying a recommended setup with the production
executor (`SetupExecutor`) on a fixture recommendation against simulated
computers (FIXTURE, NOT real hosts): gpu-box with two NVIDIA cards (through
Martlet there), desk-host (this PC's own host service), linux-box (SSH),
old-box (Martlet can't reach it) and laptop (no host service). The preflight:
Chatterbox Turbo's terms with the RTX 4090 by UUID, Parakeet chosen as the stt
variant with that variant's terms only, moving Thinking keeping its model, Audio2Face
showing and sending its default engine's terms, an NGC key the owner enters, old-box needing someone there, laptop unable to run
host roles, the Thinking pool joining by itself, thinking moving to each companion
PC's own Ollama (ready, with the download's terms), a hosted provider the owner
must choose in Companion (never made here) and the role still doing that job
kept until it moves, and the downloads added up.
The run: the host commands in order with their arguments (nothing for skipped
changes), the key only to Audio2Face and never in text, the terms recorded as
accepted, speaking on gpu-box with failover and thinking back to each PC's
choice in the plan, thinking switched through the Companion path, the hosted
provider reported as needing the owner with its plan unchanged, loudness
lip-sync off in the plan, the role whose job didn't move kept, Sharing work, one cluster check, a failed removal that
doesn't stop the others, a job this PC can't follow yet reported, the run
record (all waiting, then *Configuring* with the step and its count, then how
each computer ended) read back from the shared settings as
`setup-run.<device>`, a per-computer entry, and a change missing from the
review skipped. `passed` and each step's `passed` and `detail`. In-process; no
network, model or credential.

Home's `HomeConfiguring` (on a host PC `HostConfiguring`; passive: it opens
the Devices map) and `HomeConfiguringStatus` (`HostConfiguringStatus`) show
the newest run from any computer, for example *Configuring your computers: 1
of 3 finished. gpu-box: Installing Chatterbox Turbo (2 of 4). Started on
desk-b.*, then for ten minutes how it ended (*Your computers were reconfigured
at 6:30 PM: 2 done, 1 needs you.*), or a host role this PC changes now.
Hidden when nothing is configured.
