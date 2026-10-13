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

## Which model looks at a picture (picture routing)

**What it decides:** for each job with a picture, which model gets the picture,
in which order.

**Moving.** Today four jobs each decide in their own place, in different
orders. The planned owner is `PictureRouting`
(`src\Martlet.Conversation\Vision\PictureRouting.cs`): one decision record
with an ordered list of steps and a *why* text for each step.

| Picture job | Order today | Owner today |
| --- | --- | --- |
| A picture with a reply (a look, or the newest picture while Martlet watches) | 1. The image model (Companion › Vision's list). 2. Else Thinking's own model, in the reply's request. 3. Else no picture. | `SenseRouting.For(SenseKind.Image, ...)` (`src\Martlet.Conversation\SenseRouting.cs`). The list's members in order: `SensePool` (`src\Martlet.Conversation\SensePool.cs`) |
| A screen summary | 1. The image model, but only while reply pictures go to it (path Described). 2. Else a Thinking pool member that sees (job kind `Digest`). 3. Else the simple rules. Never Thinking's own model. | `ImageModelScreenDigestThinker` and `PoolScreenDigestThinker` (`src\Martlet.Desktop\PoolScreenDigestThinker.cs`), joined in `LiveConversationController.cs` |
| Touch zones and measuring the eyes (helper jobs) | 1. A free Thinking pool member that sees (job kind `TouchZones`). 2. Else the image model. 3. Else Thinking's own model, after the reply. | `HelperJobs.RunAsync` (`src\Martlet.Desktop\HelperJobs.cs`) with `ThinkingPoolHelpers` (`src\Martlet.Desktop\ThinkingPoolHelpers.cs`); the image model step is `AskImageModelAsync` (`src\Martlet.Desktop\LiveConversationController.Senses.cs`) |
| A check-in with a screenshot | A Thinking pool member that sees (job kind `CheckIn`). Else the check-in waits. | `CheckIns.Needs` (`src\Martlet.Conversation\CheckIns.cs`) and the check-in runner in `src\Martlet.Desktop\MainWindow.CheckIns.cs` |

**Shows it:** Companion › Vision (`ImageModelRoute` shows `SenseRoute.Why`;
`VisionScreenSummaryStatus`; the *Also gets your pictures* rows from
`SensePoolUsers`, `src\Martlet.Desktop\SensePoolUsers.cs`), Companion › Touch
(`TouchZonesVision`) and Companion › Eyes. These pages compute their own text
today (see [Pool summary text](#pool-summary-text)).

**Tests:** `SenseRoutingTests`, `SensePoolTests` and `CheckInsTests`
(Martlet.Conversation.Tests); `ImageModelPipelineTests`, `ScreenDigestTests`,
`HelperJobsTests`, `SensePoolDesktopTests` and `SensePoolUsersTests`
(Martlet.Desktop.Tests).

**MCP:** `sense_models_status`, `sense_models_check`, `image_model_check`,
`screen_digest_check`, `helper_jobs_status`, `helper_jobs_check`,
`check_ins_status`, `check_ins_check`; `ui_snapshot` with `idPrefix`
`ImageModel` or `Pool-vision`.

**Docs:** [SENSE_MODELS.md](SENSE_MODELS.md).

## Which model hears a recording (recording routing)

**What it decides:** for each job with a recording, which model gets the
recording.

**Moving**, with picture routing, to one owner for pictures and recordings.

| Recording job | Order today | Owner today |
| --- | --- | --- |
| Your voice with a reply | 1. The audio model (Companion › Hearing's list) describes it. 2. Else Thinking's own model, when it hears and *Let Thinking hear my voice* allows it. 3. Else the transcript only. | `SenseRouting.For(SenseKind.Audio, ...)`; the voice job is in `src\Martlet.Desktop\LiveConversationController.AudioModel.cs` |
| What this PC plays (PC sound descriptions) | 1. The audio model. 2. Else a Thinking pool member that hears (job kind `Digest`). 3. Else the sound tagger on the processor. | `LiveConversationController.SoundJudge()` (`SenseSoundJudge`, then `PoolSoundJudge`, `src\Martlet.Desktop\PoolSoundJudge.cs`), then `CpuSoundJudge` in `src\Martlet.Desktop\PcSoundDigest.cs` |
| A check-in with a recording | A Thinking pool member that hears (job kind `CheckIn`). | `CheckIns.Needs` |

**Shows it:** Companion › Hearing (`AudioModel...`, the *Also gets your
recordings* rows) and Companion › Listening.

**Tests:** `SenseRoutingTests` (Martlet.Conversation.Tests);
`AudioModelVoiceTests` and `PcSoundDigestTests` (Martlet.Desktop.Tests).

**MCP:** `audio_model_check`, `sound_digest_check`, `hearing_check`,
`sense_models_status`.

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
and which helper jobs run as which kind.

**Moving.** Today the facts are in several lists that nothing keeps the same:
`ThinkingJobKind`, `ThinkingJobKinds.Name`, `ThinkingJobKinds.Priority` and
`ThinkingPriority` (`src\Martlet.Conversation\ThinkingPool.cs`); `RunsOnKinds`
(`src\Martlet.Desktop\MainWindow.DeepThinking.cs`); `HelperJobs.Name`
(`src\Martlet.Desktop\HelperJobs.cs`); `ThinkingPoolHelpers.Kind` and
`ThinkingPoolHelpers.Priority`. The planned owner is one row for each kind in
`ThinkingJobKinds.Rows` (`src\Martlet.Conversation\ThinkingJobKinds.cs`).
The keys are in the [glossary](GLOSSARY.md#thinking-pool-job-keys).

**Tests:** `ThinkingPoolTests` (Martlet.Conversation.Tests),
`HelperJobsTests` (Martlet.Desktop.Tests).

**MCP:** `thinking_pool_status` (`jobs` by kind), `helper_jobs_status`.

## Pool summary text

**What it decides:** the sentence above a pool list that says what the list
does now, and what happens when no member is on.

**Moving.** Today each page makes its own sentence from the settings:

| Text | Owner today |
| --- | --- |
| `Pool-<area>-Summary` on every pool list | `PoolListCard` (`src\Martlet.Desktop\MainWindow.Pools.cs`), with the fixed `PoolArea.WhenEmpty` text (`src\Martlet.Core\Cluster\Pools.cs`) |
| The same sentence on Devices › Sharing work | `src\Martlet.Desktop\MainWindow.WorkSharing.cs` (a second copy, other words) |
| *Also gets your pictures* (or *recordings*) under the Vision and Hearing lists | `SensePoolUsers.Summary` and `SensePoolUsers.Line` (`src\Martlet.Desktop\SensePoolUsers.cs`), shown by `MainWindow.SensePools.cs` |
| The Thinking pool's notes (no member, offline, no member that sees) | `ThinkingJobBoard.Guidance` (`src\Martlet.Conversation\ThinkingPool.cs`) |

For Vision and Hearing, `PoolArea.WhenEmpty` is true only for a picture or a
recording with a reply. Screen summaries and other background jobs never go
to Thinking's own model. The *Also gets your pictures* rows name the Thinking
pool members that get them. The planned owner is one summary function that
reads the routing decision (picture routing above), not `PoolArea.WhenEmpty`.

**Tests:** `SensePoolUsersTests` and `SensePoolDesktopTests`
(Martlet.Desktop.Tests); `PoolsTests` (Martlet.Core.Tests).

**MCP:** `ui_snapshot` (`Pool-<area>-Summary`, `Pool-vision-AlsoSummary`,
`Pool-vision-Also-<n>`), `work_sharing_status`.

## Shared or local: which settings travel to the owner's other computers

**What it decides:** whether a setting is the same on all the owner's
computers (shared), stays on one computer (local), and, with accounts, whether
it belongs to the household or to each person.

**Owner today:** several lists that must agree by hand.

| Part | Owner today |
| --- | --- |
| Shared sections of `settings.json` and `lorebooks.json` (Thinking, Listening, Speaking, the Thinking pool and others) | `AppSettingsSections` (`src\Martlet.Core\Sync\AppSettingsSections.cs`) |
| Shared sections of the desktop's own files (`talk`, `character`, `model-abilities`, `work-sharing` and others) | `DesktopSections()` (`src\Martlet.Desktop\MainWindow.SettingsSync.cs`) |
| Household or each person | `SettingScopes` (`src\Martlet.Core\Sync\SettingScopes.cs`) |
| Pool lists: `pools.json` (shared) or `pools-local.json` (this PC) | `PoolArea.Shared` (`src\Martlet.Core\Cluster\Pools.cs`) |
| Why a setting stays on one computer | The table [What stays with each computer](CLUSTER.md#what-stays-with-each-computer) |

The rule is in [AGENTS.md](../AGENTS.md#martlet-is-one-app-configuration-is-shared):
share by default; keep a setting local only when it describes the computer.
A settings file registry (one row for each file, with its scope) is planned.

**Tests:** `ClusterSyncTests` (Martlet.Desktop.Tests); `AccountSettingsTests`
and `ThinkingPoolShareTests` (Martlet.Core.Tests).

**MCP:** `settings_sync_status`, `settings_sync_selftest`.

**Docs:** [One Martlet on every computer](CLUSTER.md#one-martlet-on-every-computer).

## The current Thinking route

**What it decides:** which `SetupRoute` is the conversation's Thinking route
(provider, model and key).

**Moving.** Today each caller finds it again with `Role == SetupRole.Llm`
(about 57 files). The planned owner is `SetupRouteLookup`
(`src\Martlet.Core\Settings\SetupRouteLookup.cs`).
