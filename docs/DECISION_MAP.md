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
| A PC sound description | Audio model, Thinking pool member that hears (`digest`), the sound tagger | `LiveConversationController.SoundJudge()` (`src\Martlet.Desktop\LiveConversationController.AudioModel.cs`) |
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

**Owner:** `SetupRouteLookup.ThinkingRoute()`
(`src\Martlet.Core\Settings\SetupRouteLookup.cs`). It gives the first route
with `SetupRole.Llm`; `SetupSettings.Validate` allows only one route for each
role. It never throws and does not check `Enabled`. Do not search the routes
for `SetupRole.Llm` again in a caller.

**Tests:** `SetupRouteLookupTests` (Martlet.Core.Tests).

## Which member takes a request of an ordered pool

**What it decides:** for an area with an ordered list (Speaking, Listening,
lip-sync, picture making, singing, vision, hearing, reading, motion), which
members a request tries, in which order, and what a busy or unavailable member
does.

**Owner:** `PoolRouting.Order` and `PoolRouting.Stops`
(`src\Martlet.Core\Cluster\Pools.cs`) give the members to try. `PoolRunner`
(`src\Martlet.Core\Cluster\PoolRunner.cs`) tries them in order: a busy or
unavailable member passes the request to the next. `PoolRefusals` says which
answers count as busy. Each area keeps only its own rule for where a member
runs. Reading (`ReadingPool`, run in `src\Martlet.Desktop\ScreenReading.cs`), `MotionPool`, `SingingPool` and
`PictureMakingPool` use the runner. Vision and Hearing use `SensePool` (see
[picture routing](#which-model-looks-at-a-picture-picture-routing)).

**Tests:** `PoolsTests` and `PoolRunnerTests` (Martlet.Core.Tests);
`PictureMakingPoolTests` (Martlet.Providers.Tests).

**MCP:** `reading_check` (its pool steps), `motion_pool_check`, `singing_pool_check`,
`pictures_check` (`place` `pool`), `work_sharing_status`.

**Docs:** [Pools](CLUSTER.md#pools-one-ordered-list-of-members-per-area).

## What MCP may click and read, and where each tool is

**What it decides:** which controls the MCP `ui_*` tools may click, and which
status values they may read, without `--allow-ui-effects`; and in which
`martlet_guide` area each MCP tool is.

**Owner:** `AutomationPolicy` (`src\Martlet.Mcp.Protocol\AutomationPolicy.cs`):
one line for each safe click and each safe value,
`new("<ID>", "<page>", "<what it does or shows>")`. A tool is one `Tool(...)`
registration in `src\Martlet.Mcp.Protocol\Tools\<Area>Tools.cs`, and
`ToolGroups()` (`src\Martlet.Mcp.Protocol\McpServer.cs`) puts each group in
its guide area. A new control or tool goes there, and `docs\MCP.md` names it.

**Tests:** `AutomationPolicyTests` and `McpDocsAgreementTests` (every tool is
named in `docs\MCP.md` as `` `name` ``; Martlet.Desktop.Tests).

**Docs:** [MCP.md](MCP.md).
