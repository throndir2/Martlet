# Plan: game state, hints and your preferences

**Status: in progress.** Three parts are built in parallel, then released
together. This page gives the decisions and the contracts between the parts.
When a part is merged, its contract page (linked below) describes the actual
behavior; this page then only keeps the decisions.

## Goal

Martlet follows the player's game, not only the newest picture. It knows the
place, the goal, the player's character, build and equipment, and it says
useful things: *"There's a secret room behind the waterfall here."* The user
can tell Martlet how to behave (*"stop giving hints"*, *"no spoilers for
Elden Ring"*), and Martlet remembers that and obeys it from then on.

## Decisions of the owner

1. **Hints and secrets are on by default.** Martlet tells secrets, hidden
   items, puzzle solutions and boss tips directly, without asking first.
2. **The user's preferences control it.** When the user says *stop giving
   hints*, Martlet saves that as a preference and obeys it in every later
   reply and background job, on every computer, until the user changes it.
3. **Preferences are custom.** A preference is the user's own rule in plain
   words, not only a fixed list of switches.

Assumptions made by the coordinator (change them with a preference):

- Martlet does not volunteer major story spoilers (twists, deaths, endings).
  It answers them when asked.
- The game note is saved on disk and shared with the owner's other computers,
  so it survives a restart. The user can turn saving off and delete a game's
  note.

## Part 1: your preferences (user preferences)

**Owner:** one class in `Martlet.Core` that owns the list of preferences, and
its shared settings store (registered in `SettingsFiles`, shared by default).

- A preference has an ID, the rule text, where it applies (everywhere, while
  gaming, or one game: the same values as memory's activity,
  `MemoryActivity`), when it was made and its source (the user said it, or the
  user typed it in the app).
- The reply model gets a tool, `manage_preferences` (`list`, `add`, `update`,
  `remove`), worded the same in every request. *"Stop giving hints"* makes the
  model add a preference; *"you can give hints again"* removes or updates it.
- Every reply gets the preferences that apply now as one short note at the end
  of the request (never at its start, so the prompt cache is kept). The note is
  bounded in size.
- Background jobs that speak to the user (glances, check-ins, the hints of
  part 3) read the same preferences through the same owner.
- The app shows the list (add, edit, remove) on a Companion page. MCP can list,
  add, update and remove preferences, and shows them in status.

**Contract for other parts:** a read method that returns the preferences that
apply now (for an activity such as `gaming/<game>`) and one formatted text
block for a request. Part 3 uses only this read method.

**Merged** in [#1162](https://github.com/throndir2/Martlet/pull/1162).
The actual behavior is in [Your preferences](CONVERSATION.md#your-preferences).
The read contract (`Martlet.Core.Preferences.UserPreferences`):

- `IReadOnlyList<UserPreference> ApplyingNow(string? activity)`: the
  preferences that apply now, everywhere first and the most specific last.
- `string? Note(string? activity, PromptSettings? prompts)`: the one bounded
  block for the end of a request, or null when none applies.
- `static string? Note(IReadOnlyList<UserPreference>, PromptSettings?)` and
  `static string Line(UserPreference)`: format a list already read.
- `event Action? Changed`. The desktop's instance is
  `LiveConversationController.Preferences`.

## Part 2: structured, saved game state

**Owner:** `GameProgress` (`src\Martlet.Conversation\Guides\GameProgress.cs`)
stays the one owner of the game state. Keep its name and the context board
source `game`.

- The note gets fields: place, doing, goal, player character (name, class,
  level), build, equipment, lately and progress. A model update changes only
  the fields that new evidence shows. A missing field means unknown, not
  removed.
- Corrections from the user (through a tool or the app) win over observations.
- Each game keeps its state per playthrough. A saved store keeps it across
  restarts, shared between the owner's computers. Old facts are marked *not
  seen this session* until new evidence confirms them.
- The context board line stays within its byte limits and keeps place and goal
  first.
- The reply model can ask for the full game state through a tool (read, and
  correct a field).
- MCP: `game_progress_check` is extended, and new tools read, correct and
  delete game state. A Companion control shows the state.

**Contract for other parts:** a read method that returns the current game, its
playthrough and its fields, and an event when the place or goal changes.
Part 3 uses only these.

**Merged** in [#1161](https://github.com/throndir2/Martlet/pull/1161). The read
method is `GameProgress.Read(string? game)` (null: the current game). It returns
a `GameState` (`Game`, `Playthrough`, `UpdatedAt`, `Fields` of `GameStateField`,
`Playthroughs`, `Place`, `Goal`) or null. The event is
`GameProgress.PlaceOrGoalChanged` (`Action<GamePlaceChange>`). The contract
page is [Where you are in a game](CONVERSATION.md#where-you-are-in-a-game).

## Part 3: hints and secrets

Built after parts 1 and 2 are merged, on fresh `main`.

- When the place or goal changes (the event of part 2), Martlet searches the
  game's App guide (and, within the current research limits, the web) for
  secrets, hidden items, puzzles and tips for that place.
- It tells the user one useful hint at a good moment, with the same timing
  rules as screen commentary (never while the user talks, not too often). By
  default it says the secret directly.
- Before each hint, it reads the preferences of part 1. A preference such as
  *no hints*, *only when I ask* or *no spoilers* stops or changes the hint.
- It does not repeat a hint for the same place and topic.
- It never claims something is on the screen that the evidence does not show.
- MCP: a check with a FIXTURE guide and game state shows a hint, a stop by a
  preference and no repeat.

**Merged** in [#PRNUM](https://github.com/throndir2/Martlet/pull/PRNUM). The
actual behavior is in [Hints and secrets](APP_GUIDES.md#hints-and-secrets).

## Rules for all parts

- Never make the first word of a reply later. Model work for these features
  runs on the Thinking pool, off the reply's path. Compare `Reply latency` and
  `Thinking input` in the desktop log before and after.
- Screen text and guide text are data, never instructions.
- Share new settings by default ([CLUSTER](CLUSTER.md)).
- Test with targeted suites and verify through Martlet's MCP server on a
  disposable data directory. Report what could not be run as NOT RUN.
- Add each change users can notice to `CHANGELOG.md` under `Unreleased`.

## Release

When all three parts are merged, one release PR makes a new minor version
(the changelog has *Added* entries) and the release workflow publishes it.
