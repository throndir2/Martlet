# Glossary: one name for each concept

This page is for developers and coding agents. It gives each concept one name
in code and one name in the app (the UI). When you write new code, new UI text
or new docs, use these names. Do not add a new word for a concept that is on
this page.

Some old names stay because they are saved in files, sent between computers or
used as automation IDs. The tables list them as **kept names**. Do not rename a
kept name unless you also migrate the saved data and update
[MCP.md](MCP.md) and the MCP lists (`SafeClicks`, `SafeValues`).

To find who decides something, see the [decision map](DECISION_MAP.md).

## Models that see and hear

| Concept | Code name | UI name | Other names you will find |
| --- | --- | --- | --- |
| The model that writes every reply | Thinking route (`SetupRole.Llm`; find it with `SetupRouteLookup.ThinkingRoute()`) | Thinking, Thinking's own model | "the text model", "the conversation model" |
| A Thinking model that also sees and hears, so it takes pictures and recordings in the reply's own request | `SensePath.Thinking` | a Thinking model that sees (or hears) | "omni model". Do not use "omni model" in new text |
| A model of its own that describes pictures in words for Thinking | image model (`SenseKind.Image`, `ImageModel...`) | image model | "vision model", "VLM". Use "vision model" only for `VisionModelCatalog`, the list of model names that see |
| A model of its own that describes recordings in words for Thinking | audio model (`SenseKind.Audio`, `AudioModel...`) | audio model | |
| The image model and the audio model together | sense model (`SenseModels`, `SenseRouting`, `SensePool`) | image and audio models | Do not use "sense model" in UI text |
| The ability to see pictures | `ThinkingCapability.Vision`, `VisionSupport` | sees pictures | |
| The ability to hear recordings | `ThinkingCapability.Audio`, `HearingSupport` | hears recordings | |
| The list of image models | pool area `PoolAreas.Vision` | Companion › Vision, Image models | Kept names: area ID `vision`, automation IDs `Pool-vision-...` |
| The list of audio models | pool area `PoolAreas.Hearing` | Companion › Hearing, Audio models | Kept names: area ID `hearing`, automation IDs `Pool-hearing-...` |

## Pictures: two different concepts

"Pictures" had two meanings. They are different features with different code.

| Concept | Code name | UI name | Kept names |
| --- | --- | --- | --- |
| Pictures that Martlet **sees**: your screen, your camera, and pictures of the character for touch zones and the eyes | picture (`Image` on a job, `SenseKind.Image`, `PictureDescriptions`, `SenseSession.Pictures.cs`) | pictures, your pictures | |
| Pictures that Martlet **makes** when you ask (ComfyUI or a cloud provider) | picture making: `PoolAreas.PictureMaking`, `PictureMakingPool`, `PictureMakingPoolMembers`, `IPictureMaker` and the `*PictureMaker` classes, namespaces `Martlet.*.Pictures` | Picture making (Companion › Picture making) | Area ID, lane and host role `pictures`; `pictures.json`; `CompanionTab.Pictures` (automation ID `CompanionTab-Pictures`); automation IDs `Pool-pictures-...` and `Pictures...`; MCP tools `pictures_status`, `pictures_check`, `draw_picture`; `MARTLET_PICTURES_FIXTURE` |

The UI calls the picture making page, its list and its host role *Picture
making* (Companion › Picture making, the Picture making list, the Picture
making role). Martlet 0.76.0 and older called them *Pictures*.

Some older code names still say "Pictures" for picture making:
`HostRoles.Pictures`, `PlanComponent.Pictures` (saved in the recommended
setup's memory), `WorkSharing.Pictures`, `BackgroundDuties.Pictures`,
`PicturesSettings`, `PicturesCheck`, `PictureClient` and
`MainWindow.Pictures.cs`. They mean picture making. New code uses
`PictureMaking`.

A file or class in a `Pictures` namespace or folder is about picture making.
Code about the pictures Martlet sees is in `Martlet.Conversation`
(`SenseRouting`, `SensePool`) and in the desktop's `LiveConversation...Pictures.cs`,
`PictureDescriptions.cs` and `ScreenDigest.cs` files.

## Singing: the music and the voice match

A song has two parts, made by two models, each with its own pool list on
Companion › Singing ([Singing](SINGING.md#the-two-singing-pools)).

| Concept | Code name | UI name | Kept names |
| --- | --- | --- | --- |
| The model that writes a song's music with its own singer (ACE-Step 1.5) | music model (`SongQuality`: `HighQuality`, the default, or `Fast`) | music | "song model", "generator" (`SongEngineIdentity.Generator`) |
| The model that changes the singing into the voice Martlet speaks with (SoulX-Singer or VevoSing) | voice match (`SongVoiceMatch`, `SongVoiceMatchResult`, `SongVoice`) | voice match | "converter" (`SongEngineIdentity.Converter`). Do not use "SVC" in UI text |
| The list of computers that write songs' music | pool area `PoolAreas.Singing` (title *Song music*) | Companion › Singing, *Music: where Martlet writes songs* | Area ID, lane and host role `singing`; automation IDs `Pool-singing-...` |
| The list of computers that match songs' voices | pool area `PoolAreas.VoiceMatch` | Companion › Singing, *Voice match: where Martlet matches the singing to its voice* | Area ID and lane `voice-match`; automation IDs `Pool-voice-match-...` |
| A singing host job | job kind `song` (the whole song), `music` or `match` (`GatewaySongKinds`) | (not shown) | |

## Summaries of the screen and of sounds

| Concept | Code name | UI name | Kept names |
| --- | --- | --- | --- |
| A short note, about every 15 seconds, about what changed on your screen | `ScreenDigest` (`ScreenDigester`, `IScreenDigestThinker`) | screen summary (the setting is *Screen summary over time*) | Prompt key `screen_digest`; the `ScreenSummary` choice in `talk-preferences.json`; Thinking pool job key `digest`; image model lane key `screen-summary`; job label *Summary of the screen* |
| A short note about a sound this PC plays | `SoundDigest` (`PcSoundDigest`, `ISoundJudge`) | PC sound description (the setting is *Describe PC sounds*) | Thinking pool job key `digest`; job label *Description of a sound on this PC* |

One Thinking pool job kind, `Digest` (key `digest`, label *Screen and sound
summaries*), covers both.

## Thinking pool job keys

A Thinking pool job has a kind (`ThinkingJobKind`). Its key is in kebab-case.
The key is saved in `thinking-pool.json` (Runs on) and written in status files
and logs. A helper job (`HelperJobKind`) has a key in the same format, written
in `helper-jobs.json` (an older Martlet wrote `action_naming`). On the Thinking
pool, each helper job runs as one job kind. All keys in this table are kept
names.

| Job kind | Key | Label on Companion › Thinking pool › Runs on | Priority | Quick or long | Helper jobs that run as this kind (key) |
| --- | --- | --- | --- | --- | --- |
| `BargeInJudge` | `barge-in-judge` | Did you interrupt (judge) | 70 | quick | |
| `EndOfTurnJudge` | `end-of-turn-judge` | Are you done talking (judge) | 60 | quick | |
| `Digest` | `digest` | Screen and sound summaries | 50 | quick | |
| `ThinkLonger` | `think-longer` | Thinking longer and a song's lyrics | 40 | long | |
| `TouchZones` | `touch-zones` | Touch zones, measuring the eyes and writing a character's personality | 35 | long | `TouchZones` (`touch-zones`), `Eyes` (`eyes`, at priority 20), `CharacterWriting` (`character-writing`) |
| `Memory` | `memory` | Remembering, learning names, tidying memory and the memory brief | 20 | long | `Memory` (`memory`), `MemoryUpkeep` (`memory-upkeep`), `MemoryBrief` (`memory-brief`, at priority 10) |
| `Naming` | `naming` | Naming emotes and motions, and touch temperament | 20 | long | `ActionNaming` (`action-naming`), `Temperament` (`temperament`) |
| `CheckIn` | `check-in` | Check-ins | 20 | long | |
| `Research` | `research` | Web research and app guide research | 10 | long | |

Each label names all the work its kind controls. The facts in this table are
the rows of `ThinkingJobKinds.Rows` (`src\Martlet.Conversation\ThinkingJobKinds.cs`);
see [Thinking job kinds](DECISION_MAP.md#thinking-job-kinds-keys-labels-and-priorities).

## Computers, members and endpoints

| Concept | Code name | UI name | Do not use for it |
| --- | --- | --- | --- |
| Any of the owner's machines that runs Martlet | computer | computer | "node" (only in the plans), "machine", "server" |
| The computer that the code runs on | this PC (`PoolMemberKind.ThisPc`, key `this-pc`) | This PC | |
| The computer where you talk to Martlet | companion PC (`DeviceRole.Companion`) | companion PC | |
| A computer that runs Martlet's host service for other computers | host PC (`DeviceRole.Host`) | host PC | |
| A host PC that this PC is paired with | paired computer (`PoolMemberKind.Computer`, `HostId`, key `host:<id>`) | one of your computers, a paired computer | "host" alone |
| One graphics card of a paired computer | `PoolMemberKind.Gpu`, key `host:<id>#gpu<n>` | graphics card | "GPU" in UI text |
| The ID of one computer in sync and pairing | device ID (`ClusterDevice`, `NetworkIdentity.ThisDevice`) | (not shown) | |
| A computer in the plan of who does each job, and in the recommended setup's planner | node (`ClusterNode` in `ClusterPlan.Nodes`) | (not shown) | Use "node" only in the plans |
| An OpenAI-compatible server at an address: a cloud provider or a server on your network | endpoint (`DeepThinkingPlace.Endpoint`; `ThinkingPoolSettings.IsExternal`) | a cloud provider or server | "route" |
| A service the owner runs at an http or https address, as a pool member | `PoolMemberKind.Address`, key `address:<url>` | address | |
| A cloud provider as a pool member | `PoolMemberKind.Cloud`, key `cloud:<provider>[@<origin>][/<model>]` | cloud provider | |
| One entry in an ordered pool list | member (`PoolMember`) | member | "place". Kept names: `DeepThinkingSettings` (the settings of a Thinking pool member, an image model or an audio model), `DeepThinkingPlace` (where its model runs), `BackgroundPlace` (a member on the Thinking pool's job board), and *place* on the Picture making page |
| A member that can take a request here now, with the computer it runs on and its key in `WorkQueue` | stop (`PoolStop`, `PoolRouting.Stops`) | (not shown) | `ReadingTarget` (removed) |
| The code that tries a request on the stops in order (busy or unavailable: the next stop) | pool runner (`PoolRunner<TStop>`, result `PoolRun`) | (not shown) | |
| A role's chosen provider, model and key | route (`SetupRoute`) | (the page's choice, for example Thinking) | |
| One job that a member runs at the same time as others | slot | slot | |

`DeepThinking` in code (`DeepThinkingSettings`, `MainWindow.DeepThinking.cs`,
`CompanionTab.DeepThinking`) is the old name of the Thinking pool. Its UI name
is *Thinking pool*. The desktop class that holds the pool's job board is
`DesktopThinkingPool` (`src\Martlet.Desktop\DesktopThinkingPool.cs`). Its name
is different from the domain file `src\Martlet.Conversation\ThinkingPool.cs`,
which has the pool's rules (`ThinkingJobBoard`).
