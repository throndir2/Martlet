# Decision map: which file owns each decision

This page is for developers and coding agents. It names, for each important
decision in Martlet, the one place in the code that makes it (its **owner**),
the tests that check it and the MCP tools that show it. Pages, status files
and MCP tools only show a decision. They must not make it again.

Words on this page follow the [glossary](GLOSSARY.md).

## How to use this map

1. Before you change a decision, find it on this page.
2. Change the owner. Do not copy the rule into a page, a status tool or a
   comment.
3. When a page or a tool shows the decision, make it read the owner's result
   (for example `SenseRoute.Why`).
4. In code comments and docs, name the owner ("see `SenseRouting.For`") instead
   of repeating its rule. A repeated rule goes stale when the owner changes.
5. When you move a decision to a new owner, update its section here in the
   same pull request.

Some decisions have more than one owner today. Their sections say so and name
the planned owner. Work to make one owner is in progress; a section marked
**Moving** changes when that work merges.

## Which model takes a picture or a recording (picture and recording routing)

**What it decides:** for each job with a picture or a recording, which model
gets it, in which order, and what happens when no one takes it.

**Owner:** `SenseJobRouting` (`src\Martlet.Conversation\SenseJobRouting.cs`).
`SenseJobRouting.Rows` is the one table of the orders (one row for each
`SenseJobKind`), and `SenseJobRouting.Decide` gives a `SenseDecision`: each
step in order (`SenseTaker`: the image or audio model, a Thinking pool member,
Thinking's own model, the sound tagger on this PC, no one), whether it takes
the job now and why. The reply jobs read their path from `SenseRouting.For`
(`src\Martlet.Conversation\SenseRouting.cs`); whether a model sees or hears is
`ModelSenses` (below). The orders are in
[Who takes each picture and recording](SENSE_MODELS.md#who-takes-each-picture-and-recording).

| Job | Order | Runner that follows the decision |
| --- | --- | --- |
| The picture with a reply | Image model, Thinking's own model | The reply (`SenseRouting.For`, `SensePool` for the list's members) |
| A screen summary | Image model, Thinking pool member that sees (`digest`) | `ImageModelScreenDigestThinker` and `PoolScreenDigestThinker` (`src\Martlet.Desktop\PoolScreenDigestThinker.cs`) |
| Touch zones and the eyes | Thinking pool member that sees (`touch-zones`), image model, Thinking's own model after the reply | `HelperJobs.RunAsync` (the pool first), then `AskHelperAsync`'s fallback (`src\Martlet.Desktop\LiveConversationController.cs`) |
| A check-in with a screenshot | Thinking pool member that sees (`check-in`) | `CheckIns.Needs` (`src\Martlet.Conversation\CheckIns.cs`) and the check-in runner |
| Your voice with a reply | Audio model, Thinking's own model (each when *Hear how you say it* allows it) | The reply (`SenseRouting.For`) |
| A PC sound description | Audio model, Thinking pool member that hears (`digest`), the sound tagger | `SenseSession.SoundJudge()` (`src\Martlet.Desktop\SenseSession.AudioModel.cs`) |
| A check-in with a recording | Thinking pool member that hears (`check-in`) | `CheckIns.Needs` and the check-in runner |

The conversation decides with what it runs with
(`LiveConversationController.SenseDecision`); a page decides from the saved
settings with the same rules (`MainWindow.SenseDecisionFor`).

**Shows it:** Companion › Vision (`ImageModelRoute` shows `SenseRoute.Why`;
`VisionScreenSummaryStatus`; the list summary `Pool-vision-Summary` while
nothing is on), Companion › Hearing (`Pool-hearing-Summary`), Companion ›
Listening (`TalkDescribePcSoundsStatus`), Companion › Touch
(`TouchZonesVision`) and Companion › Eyes. The *Also gets your pictures* rows
(`SensePoolUsers`) read the jobs from the table.

**Tests:** `SenseJobRoutingTests` (each job in each setup), `SenseRoutingTests`,
`SensePoolTests` and `CheckInsTests` (Martlet.Conversation.Tests);
`SenseJobAgreementTests` (the page and the conversation pick the same model,
and the page's text names it), `ImageModelPipelineTests`, `ScreenDigestTests`,
`HelperJobsTests`, `AudioModelVoiceTests`, `PcSoundDigestTests`,
`SensePoolDesktopTests` and `SensePoolUsersTests` (Martlet.Desktop.Tests).

**MCP:** `sense_models_status` (`desktop.file.jobs`: each job's decision),
`sense_models_check` (`job-routing`), `image_model_check`,
`audio_model_check`, `screen_digest_check`, `sound_digest_check`,
`helper_jobs_status`, `check_ins_status`; `ui_snapshot` with `idPrefix`
`ImageModel`, `Pool-vision` or `Pool-hearing`.

**Docs:** [SENSE_MODELS.md](SENSE_MODELS.md).

## Whether a model sees, hears or calls tools (sense capability)

**What it decides:** whether one model takes pictures, recordings and tool
calls, and what Martlet does when it can't tell ("unknown").

**Owner:** `ModelSenses` (`src\Martlet.Conversation\ModelSenses.cs`): one
rule for the Thinking route, a Vision or Hearing list member, a Thinking pool
member and Ollama on this PC. `ModelSenses.For` gives the facts (sees, hears,
calls tools) in one order for every place: what the route carries, a retired
model, what Martlet found out (a paired computer's model by its gateway's
origin), then the name. `ModelSenses.Takes` (`SenseUse`) is the one table for
"unknown": a list member is tried, Thinking's reply tries pictures but not
recordings, and a Thinking pool job needs a known yes
([What a model takes](SENSE_MODELS.md#what-a-model-takes)).

| Caller | For which models |
| --- | --- |
| `SenseRouting.For`, `SensePool.Refuses` (`src\Martlet.Conversation`) | The Thinking route and Vision and Hearing list members |
| `LiveConversationConfiguration.Vision`, `Hearing`, `Tools` (`src\Martlet.Desktop\LiveConversationConfiguration.cs`) | The Thinking route in a running conversation |
| `ThinkingPoolCapabilities.For` (`src\Martlet.Conversation\ThinkingPool.cs`) | Thinking pool members (recordings only on an endpoint) |
| `MainWindow.LocalTakes` (`src\Martlet.Desktop\MainWindow.SenseModels.cs`) | Ollama on this PC |
| `ModelSenses.Retired` | A retired Thinking model's message (Health, Companion › Thinking) |

**Facts they read:** `ModelAbilities` (`model-abilities.json`,
`src\Martlet.Core\Settings\ModelAbilities.cs`: server metadata, Test vision,
Test hearing, refusals), then the model's name (`VisionModelCatalog`,
`HearingModelCatalog`, `src\Martlet.Providers`).

**Tests:** `ModelSensesTests`, `SenseRoutingTests` and `ThinkingPoolTests`
(Martlet.Conversation.Tests); `ModelAbilitiesTests` (Martlet.Core.Tests);
`VisionModelCatalogTests` and `ModelAbilityDetectionTests`
(Martlet.Providers.Tests).

**MCP:** `sense_models_check` (`one-rule-for-every-place`),
`model_ability_check`, `hearing_check`, `sense_models_status`,
`thinking_pool_status` (each member's `can`).

## Which Thinking pool member takes a job (Thinking pool job routing)

**What it decides:** whether a member may take a job of one kind, which free
member takes it, when it starts, and what happens when it waits, fails or is
stopped.

**Owner:** `ThinkingJobBoard` (`src\Martlet.Conversation\ThinkingPool.cs`).
Its parts:

| Part | Owner |
| --- | --- |
| Quick jobs and long jobs (fast kinds) | `ThinkingJobKinds.IsFast` and `BackgroundPlace.Takes` (`src\Martlet.Conversation\BackgroundPlaces.cs`) |
| Runs on (Any member, Prefer smart, Smart only, These members) | `ThinkingRunsOnRules` (`src\Martlet.Conversation\ThinkingPool.cs`); saved in `ThinkingPoolSettings.RunsOn` |
| May receive pictures and recordings | `ThinkingPoolSettings.MayReceiveMedia` (`src\Martlet.Core\Settings\ThinkingPoolSettings.cs`) and `ThinkingPoolMedia` |
| What a member can do | See [sense capability](#whether-a-model-sees-hears-or-calls-tools-sense-capability) |
| Slots, the kept fast slot and the live floor | `BackgroundPlaces` and the board's `MayStartNow` and `CanRunBeside` |
| Priority, stops for priority and retries | `ThinkingPriority`, `ThinkingPoolPolicy` |
| The members, from the settings | `LiveConversationController.PoolMembers` in `src\Martlet.Desktop\DesktopThinkingPool.cs` |
| The desktop's handle on the board | `DesktopThinkingPool` (`src\Martlet.Desktop\DesktopThinkingPool.cs`), from `LiveConversationController.ThinkingPool` |

**Tests:** `ThinkingPoolTests` (Martlet.Conversation.Tests);
`ThinkingPoolSettingsTests` (Martlet.Core.Tests); `ThinkingPoolPresenceTests`
and `ThinkingPoolRateLimitTests` (Martlet.Desktop.Tests).

**MCP:** `thinking_pool_status`, `thinking_pool_check`, `thinking_requests`,
`backup_thinking_check`.

**Docs:** [The Thinking pool](CONVERSATION.md#the-thinking-pool) and the
helper jobs in [MEMORY.md](MEMORY.md).

## Thinking job kinds: keys, labels and priorities

**What it decides:** the job kinds of the Thinking pool, the key of each kind
in saved files and logs, its label on Companion › Thinking pool, its priority,
whether it is quick, what it needs, what the live floor does to it, and which
helper jobs run as which kind.

**Owner:** one row for each kind in `ThinkingJobKinds.Rows`
(`src\Martlet.Conversation\ThinkingJobKinds.cs`), with the enums
`ThinkingJobKind`, `ThinkingPriority` and `HelperJobKind`. These read it:
`ThinkingJobKinds.Name`, `Priority` and `IsFast`; `LiveFloorRules.Stops` and
`ServesTheTurn`; the Runs on card (`RunsOnCard`,
`src\Martlet.Desktop\Pages\ThinkingPool\ThinkingPoolPage.BusyPool.cs`);
`ThinkingRequestWords.Kind`; `HelperJobs.Name` and `PriorityOf`; and
`ThinkingPoolHelpers.Kind` and `Priority`. To add a kind, add its enum value
and its row. A key is saved, so a changed key needs a migration.
The keys are in the [glossary](GLOSSARY.md#thinking-pool-job-keys).

**Tests:** `ThinkingJobKindsTests` and `ThinkingPoolTests`
(Martlet.Conversation.Tests), `HelperJobsTests` (Martlet.Desktop.Tests).

**MCP:** `thinking_pool_status` (`kinds`: the table; `jobs` by kind),
`thinking_pool_check` (step *kinds*), `helper_jobs_status`.

## Pool summary text

**What it decides:** the sentence above a pool list that says what the list
does now, and what happens when no member is on.

**Owner:** `PoolListSummary.Describe` (`src\Martlet.Core\Cluster\PoolListSummary.cs`).
`Pool-<area>-Summary` on every pool list (`PoolListCard`,
`src\Martlet.Desktop\MainWindow.Pools.cs`) and the area's line on Devices ›
Sharing work (`WorkSharingPool-<area>`, `MainWindow.WorkSharing.cs`) both show
it. While nothing in the Vision or Hearing list is on, the *Instead* words
come from the decisions above (`SenseJobRouting.Instead`, through
`MainWindow.SenseInstead`), not from `PoolArea.Fallback`.

| Other text | Owner |
| --- | --- |
| *Also gets your pictures* (or *recordings*) under the Vision and Hearing lists | `SensePoolUsers.Summary` and `SensePoolUsers.Line` (`src\Martlet.Desktop\SensePoolUsers.cs`), with the jobs from `SenseJobRouting.Rows` |
| The Thinking pool's notes (no member, offline, no member that sees) | `ThinkingJobBoard.Guidance` (`src\Martlet.Conversation\ThinkingPool.cs`) |

**Tests:** `PoolListSummaryTests` and `PoolsTests` (Martlet.Core.Tests);
`SenseJobAgreementTests`, `SensePoolUsersTests` and `SensePoolDesktopTests`
(Martlet.Desktop.Tests).

**MCP:** `ui_snapshot` (`Pool-<area>-Summary`, `WorkSharingPool-<area>`,
`Pool-vision-AlsoSummary`, `Pool-vision-Also-<n>`), `work_sharing_status`.

## Shared or local: which settings travel to the owner's other computers

**What it decides:** whether a setting is the same on all the owner's
computers (shared), stays on one computer (local), and, with accounts, whether
it belongs to the household or to each person.

**Owner:** `SettingsFiles` (`src\Martlet.Core\Settings\SettingsFiles.cs`):
one row for each settings file in the data folder, with its scope (shared,
account or local), the shared setting that carries it, the store type that
owns its path, load, save and defaults, and, for what stays on this computer,
the reason from [What stays with each computer](CLUSTER.md#what-stays-with-each-computer).
Read and write a file only through its store, also in MCP checks (for example
`TalkPreferences.Read` and `TalkPreferences.Load` for `talk-preferences.json`).

These parts carry the shared settings and must agree with `SettingsFiles`:

| Part | Owner |
| --- | --- |
| Shared sections of `settings.json` and `lorebooks.json` (Thinking, Listening, Speaking, the Thinking pool and others); `settings.json` is not a `SettingsFiles` row | `AppSettingsSections` (`src\Martlet.Core\Sync\AppSettingsSections.cs`) |
| Shared sections of Core stores (`model-abilities`, `work-sharing`, `motion`, `openai-endpoint`, `recommendation-preferences`, `pools`) and the one read, apply and default pattern every file-backed section uses | `StoreSections`, `FileSection` (`src\Martlet.Core\Sync\`) |
| Shared sections of the desktop's own files (`talk`, `character`, `speech-display`, `updates` and others), and what the desktop does after it took a value (by key, `FollowSectionsAsync`) | `DesktopSections()` (`src\Martlet.Desktop\MainWindow.SettingsSync.cs`) |
| The pool lists' API keys (`pool-keys`) and Picture making's custom workflows | `PoolKeyShare` (`src\Martlet.Core\Cluster\PoolKeyShare.cs`), `PictureWorkflowFiles` |
| Whether a hosted provider has a saved key anywhere (routes, If Thinking fails, the Thinking pool, the Vision, Hearing and Picture making lists), so no page asks for it again and a new place uses a copy; also what the free-key prompt and the planner count as configured | `SavedProviderKeys` (`src\Martlet.Core\Settings\SavedProviderKeys.cs`) |
| The Linux and macOS companion's `settings.json`: `thinking`, `listening`, `speaking` (with their keys) and `companion`, and what stays on that computer | `CompanionSharedSections` and `CompanionSync` (`src\Martlet.Companion.Platform\CompanionSharing.cs`); guardrails: `CompanionGuardrails.Admit` |
| The per-model character files (touch zones, eyes, VRoid Hub credits, the character's own reaction changes) | `CharacterSharedSections` (`src\Martlet.Avatar.Hosting\CharacterSharedSections.cs`) |
| Keeping clips of unnamed voices | `VoiceChoices.ClipsSection` (`src\Martlet.Core\Speakers\VoiceChoices.cs`) |
| Discord (with its bot token) and Telegram and WhatsApp (with their secrets) | `DiscordSharing` (`src\Martlet.Discord\DiscordSharing.cs`), `MessagingSharing` (`src\Martlet.Messaging\MessagingSharing.cs`) |
| Household or each person | `SettingScopes` (`src\Martlet.Core\Sync\SettingScopes.cs`) |
| Pool lists: `pools.json` (shared; Picture making, Vision and Hearing too since #805) or `pools-local.json` (Reading only) | `PoolArea.Shared` (`src\Martlet.Core\Cluster\Pools.cs`) |

The rule is in [AGENTS.md](../AGENTS.md#martlet-is-one-app-configuration-is-shared):
share by default; keep a setting local only when it describes the computer.
A new settings file gets a `SettingsFiles` row, a store and, when it is not
shared, a row in CLUSTER.md. A file that holds no choice (status, cache,
record) is named in `SettingsFiles.NotSettings` instead.

**Tests:** `SettingsFilesTests` (every local file has its CLUSTER.md row, and
every file name a store declares is registered or named as not a setting),
`SharedFilesTwoComputersTests`,
`AccountSettingsTests` and `ThinkingPoolShareTests` (Martlet.Core.Tests);
`SettingsFileStoreTests` and `ClusterSyncTests` (Martlet.Desktop.Tests).

**MCP:** `settings_sync_status`, `settings_sync_selftest`, `settings_get`,
`settings_schema`, `settings_set`.

**Docs:** [One Martlet on every computer](CLUSTER.md#one-martlet-on-every-computer).

## The current Thinking route

**What it decides:** which `SetupRoute` is the conversation's Thinking route
(provider, model and key).

**Owner:** `SetupRouteLookup.ThinkingRoute()`
(`src\Martlet.Core\Settings\SetupRouteLookup.cs`). It gives the first route
with `SetupRole.Llm`; `SetupSettings.Validate` allows only one route for each
role. It never throws and does not check `Enabled`. Do not search the routes
for `SetupRole.Llm` again in a caller.

**Tests:** `SetupRouteLookupTests` (Martlet.Core.Tests).

## Which pairing a connection to a host uses

**What it decides:** which of this PC's pairings (credential ID, device ID,
address and pinned key) a connection to a paired host uses for a saved job: a
Setup route, a Thinking pool member, a list entry or the lip-sync host.

**Owner:** `HostPairingChoice.For`
(`src\Martlet.Avatar.Hosting\HostPairingChoice.cs`). This PC's current pairing
with the same host ID (`hosts.json`) wins over the pairing saved with the job,
because pairing a host again deletes the old secret. The desktop applies it in
`HostPairingCredential.Connect` (`src\Martlet.Desktop\HostText.cs`), which
Thinking, the Thinking pool, Listening and Speaking use, and which logs why a
pairing secret is missing.

**Tests:** `HostPairingChoiceTests` (Martlet.Desktop.Tests).

**MCP:** `host_connections_selftest` (its *paired again* step).

**Docs:** [Your Martlet network](NETWORK.md#how-it-works-for-you).

## Which hosts and computers of the network a PC knows

**What it decides:** how a change to the network (a host or a computer added on
one PC) reaches every other member, including when the PC that made it can't
reach the hosts the others use; and what each PC says when the network isn't
the same everywhere yet.

**Owner:** `NetworkSyncEngine.SyncAsync`
(`src\Martlet.HostClient\NetworkSync.cs`): it merges every
reachable host's roster, looks for hosts of the network that no reachable host
has heard of on the home network (`NetworkHostFinder`, `NetworkFind.cs`),
pairs by itself with every host of the roster and brings its roster to a host
that doesn't count it yet (`HostNetworkPairing`, `HostNetwork.cs`; the host
side is `GatewayNetworkStore.PairMember`, `src\Martlet.Gateway\GatewayNetwork.cs`).
What isn't the same yet, and why, is `NetworkReach` (`NetworkFind.cs`); the
Share page, the Devices summary and the map read it
(`MainWindow.Network.cs`, `NetworkMap.Build`'s network hosts).

**Tests:** `NetworkReachTests` (Martlet.Desktop.Tests),
`MemberPairingRosterTests` (Martlet.Gateway.Tests).

**MCP:** `network_selftest` (the MIKO, DIVA, LUMINE and NAS steps),
`role_lab add_host` with the desktop's `NetworkGap` and `DevicesSummary`.

**Docs:** [When a host is added where your other computers can't reach](NETWORK.md#when-a-host-is-added-where-your-other-computers-cant-reach).

## Which address a connection to a host dials

**What it decides:** which address the TCP connection to a paired host goes
to: its paired (home) address, where it was found on the home network after its
computer got a new address, or one of its outside addresses; when Martlet looks
for the host on the home network; and which address that answered it uses (only
one where the host shows its pinned key). The paired origin stays the host's
name for TLS, requests and signatures.

**Owner:** `HostRoutes.ConnectAsync` (`src\Martlet.HostClient\HostRoutes.cs`),
with the find question in `HostFind` (`src\Martlet.Core\Network\HostFind.cs`).
`HostRegistry.Moved` (`src\Martlet.Desktop\HostControl.cs`) keeps the new
address with the pairing (`movedTo`) and moves an SSH target that named the old
one. On a host, `GatewayHomeListeners` (`src\Martlet.Gateway`) and the Docker
keeper's `follow_home` (`deploy\host\lib\docker.sh`) decide where it listens.

**Tests:** `HostFindTests` (Martlet.Core.Tests), `HostMoveTests`
(Martlet.HostClient.Tests), `HomeListenerTests` (Martlet.Gateway.Tests),
`HostMovedTests` and `HostOutsideAddressTests` (Martlet.Desktop.Tests).

**MCP:** `host_move_selftest`, `hosts_find`, `exposure_selftest` (outside
addresses).

**Docs:** [When a computer gets a new address](NETWORK.md#when-a-computer-gets-a-new-address).

## Which member takes a request of an ordered pool

**What it decides:** for an area with an ordered list (Speaking, Listening,
lip-sync, picture making, singing and its voice match, vision, hearing, reading, motion), which
members a request tries, in which order, and what a busy or unavailable member
does.

**Owner:** `PoolRouting.Order` and `PoolRouting.Stops`
(`src\Martlet.Core\Cluster\Pools.cs`) give the members to try. `PoolRunner`
(`src\Martlet.Core\Cluster\PoolRunner.cs`) tries them in order: a busy or
unavailable member passes the request to the next. `PoolRefusals` says which
answers count as busy. Each area keeps only its own rule for where a member
runs. Reading (`ReadingPool`, run in `src\Martlet.Desktop\ScreenReading.cs`), `MotionPool`, `SingingPool` and
`PictureMakingPool` use the runner. Vision and Hearing use `SensePool` (see
[picture and recording routing](#which-model-takes-a-picture-or-a-recording-picture-and-recording-routing)).

**Tests:** `PoolsTests` and `PoolRunnerTests` (Martlet.Core.Tests);
`PictureMakingPoolTests` (Martlet.Providers.Tests).

**MCP:** `reading_check` (its pool steps), `motion_pool_check`, `singing_pool_check`,
`pictures_check` (`place` `pool`), `work_sharing_status`.

**Docs:** [Pools](CLUSTER.md#pools-one-ordered-list-of-members-per-area).

## Which models Singing can run on a computer

**What it decides:** the music models and voice matches a singing computer can run, their names on the wire, the role
choice that adds each one, its download, licence and terms.

**Owner:** `SingingModels` (`src\Martlet.Core\Singing\SingingModels.cs`) for the app, the gateway and MCP;
`workers\singing\martlet_singing\pins.py` (`MUSIC_MODELS`, `CONVERTER_IDS`, `EXTRA_CHOICES`, the pinned files) for the
singing host. `deploy\host\roles\singing\role.conf` has the matching choices and terms.

**Tests:** `SingingModelsTests` (Martlet.Core.Tests); `AddedModelTests` (python:singing).

**MCP:** `singing_check` (`model`, `voiceMatch`), `singing_status`.

**Docs:** [Singing](SINGING.md#other-music-and-voice-match-options).

## How a speech host service checks requests and downloads its models

**What it decides:** for the xtts, dia, gpt-sovits, f5, chatterbox, singing, parakeet and voice-recognition host
services: the answer to a request with a missing (411), bad (400) or too large (413) `Content-Length`, the default body
limit, how a pinned model file is downloaded and checked (exact size and SHA-256, no partial file kept), and how a host
owns its model process.

**Owner:** `workers\common\martlet_worker_host.py` (`JsonHandler`, `download`, `fetch_pinned`, `ModelProcess`). Each
role image copies it beside its host (build context `workers`, `workers\<engine>\host\Dockerfile`).

**Tests:** `python:common`; each engine's own suite (`python:<engine>`); `Every_worker_a_role_builds_from_is_in_the_host_image`
(Martlet.Desktop.Tests).

**MCP:** `singing_check` with endpoint `fixture` starts this checkout's singing service, which uses it.

**Docs:** [Host roles](../deploy/host/README.md).

## How much Martlet looks a question up (answer, look up or research)

**What it decides:** when a reply answers from what Martlet knows, searches a guide or report, does a quick look-up, or
starts full or deep research; each depth's budget and job kind; when the answer counts as found early; and how a
report's sources are checked after it is ready.

**Owner:** the *Web research* prompt (`PromptCatalog.DefaultWebResearchInstructions`,
`src\Martlet.Core\Settings\PromptCatalog.Texts.cs`) for when to use each level; `WebResearch` (`Depth`, `BudgetFor`,
`KindFor`, `LookupKind`, `FindingKind`, `src\Martlet.Conversation\Research\WebResearch.cs`) for the depths, budgets and
jobs; `ResearchWatcher` (`src\Martlet.Conversation\Research\ResearchWatcher.cs`) for what counts as found;
`SourceCheck` (`src\Martlet.Conversation\Research\SourceCheck.cs`) for the source check. The desktop's
`LiveConversationController.Research.cs` only places the jobs on the Thinking pool and keeps the report.

**Tests:** `WebResearchTests` (Martlet.Conversation.Tests); `CreationsTests` (Martlet.Core.Tests) for revising a report.

**MCP:** `research_check` (`levels`, `flow.run.foundEarly`).

**Docs:** [Answer, look up or research](CONVERSATION.md#answer-look-up-or-research),
[Deep research](DEEP_RESEARCH.md#levels-the-watcher-and-the-source-check).

## Which host runs an OpenAI-compatible request, and how it ranks

**What it decides:** for a request of the [OpenAI-compatible
endpoint](OPENAI_ENDPOINT.md), whether its key may make it, which host runs it
(this one, or another host of the network that has the model), which real
model a [network model name](OPENAI_ENDPOINT.md#network-model-names) runs on,
how it ranks against the owner's work, whether and how long it waits for a
free slot, and the context size it runs with.

**Owner:** `GatewayOpenAiRoutes.RelayOpenAiAsync` and `ListModelsAsync`
(`src\Martlet.Gateway\GatewayOpenAi.cs`) for the key check, the model list and
the order (this host's own model first, then the hosts of the roster that list
the model; a host never sends a request on twice), and for where a request
waits (`WaitHereAsync`; `X-Martlet-Wait` to another host) with the limits of
`GatewayOpenAiQueue`. Which real models a network model name fits is
`OpenAiNetworkModels` (`src\Martlet.Core\Access`), with the rating of
`ThinkingSmartsGuess`. The rank, the slots and the queue are
`GatewayInferenceRouteRegistry.BeginOutside`, `EnqueueOutside` and
`WaitOutsideAsync` (`src\Martlet.Gateway\GatewayInferenceOutside.cs`) with the
ranks of `GatewayInferencePriority.cs`: rank 0 below all of the owner's work,
or rank 1 with the owner's background work for a key with `equal-priority`
(`GatewayApiKeyStore.EqualPriority`); the owner's requests never look at the
queue. The context size is
`OllamaRelayWorker.OpenAiContextAsync`; the translation to Ollama's own API is
`OllamaOpenAi` (`src\Martlet.Gateway.Ollama`). The scopes and the key's
priority text are `ApiKeyScopes` and `OpenAiEndpoint` (`src\Martlet.Core\Access`).

**Shows it:** Share › *Share your language models* (`OpenAiModels`,
`OpenAiKey-<id>`, `OpenAiStatus`), the host log (*OpenAI-compatible ... request
from ... finished here* or *sent on to ...*, *... waits for a free slot*,
*Owner first: stopped an OpenAI-compatible ... request*), the answer's
`model`, `X-Martlet-Host` and `X-Martlet-Queued`, and `GET
/martlet/v1/priority` (its running requests and preemptions).

**Tests:** `OpenAiEndpointTests` (Martlet.Gateway.Tests and Martlet.Core.Tests),
`OpenAiNetworkModelsTests` (Martlet.Core.Tests).

**MCP:** `openai_selftest`, `openai_endpoint_status`.

## What the front door serves and who may sign in to its page

**What it decides:** which paths a host's [front
door](OPENAI_ENDPOINT.md#the-front-door) (its public port) serves, what its web
page shows to anyone and to a person signed in with a key, and when a web
session ends.

**Owner:** `GatewayFrontDoorRoutes.InvokeFrontDoorAsync` and
`GatewayFrontDoorSessions` (`src\Martlet.Gateway\GatewayFrontDoor.cs`) for the
routes (`/v1`, `/`, `/portal/sign-in`, `/portal/sign-out`, `/health/live`;
404 for the rest), sign-in and sessions; `GatewayFrontDoorPage.cs` for the
page; `GatewayGuard` (`Failed` never locks out the `openai` and `front-door`
classes). Whether a host has a front door, and on which port, is
`GatewayExposure.FrontDoorPort`, set by `martlet-host exposure --front-door`
(`HostExposure`, `exposure.json`) and the `host.exposure` node command
(`NodeCommandRules`). The setup texts are `OpenAiEndpoint` (`ExposeHelp`,
`ProxyManagerSteps`, `NginxConfig`, `CaddyConfig`, `CloudflareTunnelConfig`,
`TailscaleCommand`). What *Set it up in Nginx Proxy Manager* changes there (the
site that has the name, its forward address and settings, which certificate)
is `ProxyManager` (`src\Martlet.Core\Access\ProxyManager.cs`).

**Shows it:** Share › *Share your language models* › *Front door*
(`OpenAiFrontDoorStatus`), *Check address* (`OpenAiCheckResult`), *Show
setup*; the host's security audit (`front_door_port`, `front_door_listeners`,
`front_door_sessions`); the host log (*Front door open on ...*, *Front door:
... signed in to the web page*).

**Tests:** `FrontDoorTests` (Martlet.Gateway.Tests), `ExposureTests`
(Martlet.Gateway.Host.Linux.Tests), `NodeCommandExposureTests`
(Martlet.Core.Tests), `FrontDoorCheckTests` (Martlet.Desktop.Tests).

**MCP:** `front_door_selftest`, `openai_endpoint_status`.

**Docs:** [OPENAI_ENDPOINT.md](OPENAI_ENDPOINT.md).

## Which endpoints are outside your computers, and what each is for

**What it decides:** which cloud providers and servers on your network a saved choice sends work to (never this PC or a
paired computer), whether each is a cloud provider or a server on your network, its name and address (scheme, host and
port only), and the list of uses (purpose, page, model, on).

**Owner:** `ExternalEndpoints` (`src\Martlet.Core\Cluster\ExternalEndpoints.cs`). It reads the routes, *If Thinking
fails*, the Thinking pool, every pool list and the HTTP MCP servers that its caller gives it.

**Shows it:** Devices › *Cloud providers and servers* (`MainWindow.ExternalEndpoints.cs`).

**Tests:** `ExternalEndpointsTests` (Martlet.Core.Tests).

**MCP:** `external_endpoints_status`; `ui_snapshot` (`ExternalEndpointsStatus`, `ExternalEndpoint-<host[-port]>`).

## What MCP may click and read, and where each tool is

**What it decides:** which controls the MCP `ui_*` tools may click, and which
status values they may read, without `--allow-ui-effects`; and in which
`martlet_guide` area each MCP tool is.

**Owner:** `AutomationPolicy` (`src\Martlet.Mcp.Protocol\AutomationPolicy.Rules.cs` has the rules; the lists are in `AutomationPolicy.Clicks.cs` and `AutomationPolicy.Values.cs`):
one line for each safe click and each safe value,
`new("<ID>", "<page>", "<what it does or shows>")`. A tool is one `Tool(...)`
registration in `src\Martlet.Mcp.Protocol\Tools\<Area>Tools.cs` (helpers in `<Area>Tools.<Feature>.cs` next to it), and
`ToolGroups()` (`src\Martlet.Mcp.Protocol\McpServer.cs`) puts each group in
its guide area. A new control or tool goes there, and the page of its guide area
in `docs\mcp\` names it (`docs\MCP.md` is the index).

**Tests:** `AutomationPolicyTests` and `McpDocsAgreementTests` (every tool is
named in `docs\MCP.md` or a page in `docs\mcp\` as `` `name` ``;
Martlet.Desktop.Tests).

**Docs:** [MCP.md](MCP.md) and its pages in [mcp/](mcp/).
