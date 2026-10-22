# MCP: Thinking and models

Part of [MCP in Martlet](../MCP.md): the tools and screens of the *Thinking and models* area of `martlet_guide` (Thinking, the Thinking pool, models, vision and context). The [index](../MCP.md#pages) lists the other pages.

## Tools while you talk (MCP client)

### Thinking longer

**Companion > Background thinking > Think longer about hard questions** (on by default; its own
`ThinkLongerOn` box turns it off) gives every reply on a route that does function calling
Martlet's own `think_longer` (`task`, the complete instruction, and an optional
`reason`) and `cancel_thinking` (optional `id`), while Deep thinking can run where
it is set to think. The call never asks first and returns at once; the task is
worked out in parallel, in a background request with Thinking steps on, by Deep
thinking (the Thinking model when its provider answers several requests at once,
another of your computers, a second model in Ollama on this PC or a cloud
provider, chosen on each PC), and brought up when it's done
([Thinking longer and background work](../CONVERSATION.md#thinking-longer-and-background-work)).
The Tools page's *Recent tool use* lists each call (`Martlet > think_longer:
started think-1`); the desktop log notes each start, fit check and end without the
task or result (`{"name":"logs_tail","arguments":{"contains":"Background"}}`).
A check-in with the **Background work** tool set (`background-work`) can start
the same think or research after the exchange. `check-ins-status.json` and the
check-in's card show its calls (`think_longer: Started think-1.`), and the log
line ends with `(started by the check-in <name>)`. `check_ins_check` checks
the set ([Check-ins](apps.md#check-ins)).

### Thinking pool

**Companion > Background thinking** says what background thinking does and where
it runs. The machines it runs on are the Thinking pool: one shared set of
Thinking models for background work
([The Thinking pool](../CONVERSATION.md#the-thinking-pool)).
`thinking_pool_status` (`dataDirectory`) reads `thinking-pool.json` (or what
Martlet would make from the older `deep-thinking.json`, without writing it):
each member with its slots, whether it sees pictures or hears recordings,
whether it can run and its `quickJobs` and `longJobs` boxes; the machines the
owner turned off (`offMembers`: each one's key, where, model, card, slots and
boxes; they take no jobs) and `poolOff` (true when no member is on: the list is
the pool, so the pool is off); the computers the
owner keeps out (`leftByOwner`, host IDs only: they never join by themselves);
whether thinking longer and research use the conversation model when no member
takes long jobs (`useConversationModelWhenEmpty`); the usable slots and whether
one stays free for fast jobs; each job kind's priority, whether it is fast and
whether a member that takes that kind can run it (`canRun`);
guidance and likely-slowdown warnings; and the desktop's
`thinking-pool-status.json` (`leftByOwner`, running and waiting jobs by kind, the live floor's
level, the jobs waiting for the conversation and the ones it stopped this turn
and in all, `resting`: each member whose computer refused a request as invalid
with what such jobs need and when it gets them again, `cooling`: each member
whose provider limits requests with `coolsUntil`, `slotsNow` of `slots` and
what it says in plain words, and each member's `slotsNow` and `coolsUntil`,
never a job's text; the `presence` summary repeats the plain words as
`limiting`).
`thinking_pool_check` rehearses the production job board with simulated
members (NOT models): no member, capabilities, the fast slot, priorities, retry
on another member, a stale job dropped, the migration, a member that
refused a request as invalid resting for such jobs while it still takes the
others, the `rate limits` steps (an endpoint member at NVIDIA Build's address
answers 429 with Retry-After 1 s: it cools down, runs 2 of 4 jobs at once, and
the job waits for it instead of failing with no retries allowed; while it cools
down, its jobs go to another member), and the `quick and long jobs` steps (a judge passes over a member
kept from quick jobs, a long job passes over a member kept from long jobs, a
long job on a member that takes no quick jobs keeps no slot free, and a member
that leaves takes its boxes with it). Its `auto-join` steps
run the production rule (`ThinkingPoolAutoJoin`) on sample hosts: a host with
the Thinking pool role joins with its slots, an Ollama-only host joins unless it
does this PC's Thinking, a member on Ollama moves to the role, and a computer
kept out, one Sharing work never uses, a full pool and a host PC are skipped.
Its `per GPU` steps run the same rule for a host with a Thinking pool model on
each of two graphics cards: it joins as two members (`host:diva` and
`host:diva#gpu2`) with their own slots, the second card's member leaves when
the host no longer runs a model there and comes back by itself, taking the
computer out removes every card, a card the owner turned off stays off at each
check while the computer's other card keeps working and comes back with its
boxes when it is on again, and only the member on the Thinking model's
own card is warned about sharing it (`ThinkingPoolWarnings.OwnCard`, from the
cards the host names for each route). `thinking_pool_status` gives each member
on a paired computer its `card` (1, or 2 to 4 for a Thinking pool model on an
extra card) and `route`; `thinking-pool-status.json` gives each member `host`,
`card`, `route` and `gpus` (the cards the host said serve that route).

`thinking_pool_status` gives the line's rules from `thinking-pool.json` as
`line` (`preemptLowerPriority`, `raiseAfterStops`, `retries`). The `priority`
steps of `thinking_pool_check` use one member with one slot (a sequential
local model) and set these rules on each board:

- `priority stop`: a barge-in judge that finds the slot busy stops research.
  The judge runs, and research runs again later at its priority. With
  `preemptLowerPriority` off, the judge waits for research to end.
- The stopped job goes to the front of the line for its priority, before a
  job of the same priority that waited before. A digest stopped for priority
  waits again and is not dropped.
- `priority raise` (`raiseAfterStops` 2): research stopped twice goes from
  priority 10 to 11. Then a job of priority 11 no longer stops it.
- `retries`: a job that failed or timed out is tried again at its priority.
  A job raised to 11 and then failed is tried again at 11. With `retries` 0
  the job fails.

The report's `priority` section gives each job's outcome (`priorityStops`,
`retries`, final `priority`), the order the member ran the jobs, and the
board's `stoppedForPriority`, `raised` and `retried` counters.
On the desktop, the page is **Companion › Background thinking**. It has
three parts: *What it does*, *Where it runs* and one *Advanced* section
(`Fold-ThinkingPoolAdvanced`, closed by default). The top reads through
`DeepThinkingNow` and `DeepThinkingParallel`.

*What it does* has three switches. `ThinkLongerOn` (*Think longer about hard
questions*) and `WebResearchOn` (*Look things up on the web*, under it) save
the reply settings. `ThinkingPoolBackup` (*Help when a reply is slow to
start*) saves `thinking-pool.json`. When it is turned on and no machine is
ticked to answer, every free machine that is on (not a paid cloud provider)
may answer. The status lines are `ThinkLongerStatus`, `WebResearchStatus`,
`WebResearchDisclosure` and `ThinkingPoolBackupStatus`.

*Where it runs* is the pool, as one list. Each machine in the pool (one
graphics card of a paired computer, a model in Ollama on this PC, or a cloud
provider or server) has its box `ThinkingPoolOn-<n>` and its model
`ThinkingPoolModel-<n>`. Unticked, a machine keeps its settings and takes no
jobs, and a paired computer's card then never joins again by itself. The rows
of machines that are on come first. Under each row are `ThinkingPoolBadges-<n>`
(*Waits while you talk*, *Costs money*, *Offline*, *Limiting requests*; hidden
when none applies), `ThinkingPoolCantRun-<n>` (*Can't run now: ...*, only
then) and `DeepThinkingShare-<host>[-gpu<n>]` (the shared-card warning, only
then). The model list is, on a paired computer's card, the models its Thinking
pool role offers (choosing one runs the role's change there with that model,
in a run window, and the old model works until the new one is ready). For
Ollama on this PC, it is the models Ollama has and Martlet recommends (never
Thinking's own model). For a cloud provider or server, it is an editable list
of the model catalog's chat models there that call tools, free first and then
the smartest; a typed ID saves on Enter or when the box loses focus (its rules
move to the new member key). A cloud provider or server also has
`ThinkingPoolRemove-<n>`.

Then each machine that Martlet knows and that isn't in the pool has a row of
its own: `ThinkingPoolJoin-<id>` (its box, unticked), `ThinkingPoolJoinModel-<id>`
(the model to use) and `ThinkingPoolJoinDetail-<id>` (what it runs, or why it
can't be ticked). The `<id>` is the host ID for a paired computer that isn't a
member (`ThinkingPoolJoin-diva`), `<host>-gpu<n>` for a graphics card of a
member computer without a Thinking pool model (`ThinkingPoolJoin-diva-gpu2`),
and `this-pc` for Ollama on this PC while no member uses it. Ticking a paired
computer or a card sets up the Thinking pool role there with the chosen model
(a run window; a model that would share the card with the computer's Thinking
model asks first with `DeepThinkingShareQuestion`). A computer whose role or
Ollama already runs a model joins at once (another model for its role is set up
first). Ticking `this-pc` adds the model in Ollama on this PC. A shared-card
warning shows as `ThinkingPoolJoinShare-<id>`. `ThinkingPoolWarnings` gives
the likely slowdowns. *Add a cloud provider* (`Fold-ThinkingPoolAddCloud`,
open while the pool has no machine) has `DeepThinkingProvider`,
`DeepThinkingBaseUrl` (for another server only), `DeepThinkingKey` with
`DeepThinkingKeyStatus` (a provider already in the list with its own key uses
that key again unless you paste a new one), `DeepThinkingModel` (an editable
list of the catalog's models there), `DeepThinkingConsent` and
`DeepThinkingSaveCloud` (*Add*). Below it is `DeepThinkingAddComputer` (*Pair
another computer*, with no paired computer) or `DeepThinkingCheckHosts`.

*Advanced* has, in order: *Thinking longer* (`ThinkLongerEffort`,
`ThinkLongerDelivery`); *Each machine* (`ThinkingPoolSummary`: *The Thinking
pool is off: no machine is on. ...* or *2 machines are on, with 3 usable slots
in all.*; `ThinkingPoolConversation`, the conversation's own model, never in
the pool, with its `ThinkingPoolUseConversationModel` box; then for each
machine its line `ThinkingPoolMember-<n>`, its boxes `ThinkingPoolQuick-<n>`
(*Quick jobs*: the judges and the screen and sound summaries),
`ThinkingPoolLong-<n>` (*Long jobs*: thinking longer, research, lyrics and the
other helpers), `ThinkingPoolAnswers-<n>` (*Backup for slow replies*) and,
for an external member, `ThinkingPoolMedia-<n>`, its `ThinkingPoolSmarts-<n>`
and its `ThinkingPoolSlots-<n>`; then `ThinkingPoolGuidance` and
`ThinkingPoolLiveFloor`); *Runs on*; *Backup for slow replies*
(`ThinkingPoolBackupDelay`); *Busy pool*; and *More ways to add a machine*
(`Fold-ThinkingPoolAddLocal`, *Another model in Ollama on this PC*, with its fit
check, and `Fold-ThinkingPoolAddComputer`, *Another of your computers*, which
reads through `DeepThinkingAutoJoin`). A machine line `ThinkingPoolMember-<n>`
reads *Reads text and pictures; writes text. 2 slots, set on diva.*, with
*Can't run now*, *Offline now* or what a member that limits requests does, such
as *NVIDIA is limiting requests; tries again in 40 s*. An external member (an
endpoint not on this PC) gets no job with a screenshot or a recording without
`ThinkingPoolMedia-<n>` (*May receive pictures and recordings*, off by
default). `thinking_pool_status` shows each member's `external` and
`mayReceiveMedia`, `canRun` counts only members that may receive them, and
`thinking-pool-status.json` gives each member `mayReceiveMedia`. The
`thinking_pool_check` step *pictures and recordings* checks the gate with the
production board. The folds are passive. Every box, model list, slot choice,
`ThinkingPoolRemove-<n>` and `ThinkingPoolJoin-<id>` saves
`thinking-pool.json`, the reply settings or a computer's role, so they need
`--allow-ui-effects`.

Each member also has `ThinkingPoolSmarts-<n>`, its *Smarts* (`Guessed: Smart`
first, then `Fast`, `Standard`, `Smart`). Martlet guesses it from the model
name: about 4B or less is Fast, about 26B or more and big cloud models such as
Nemotron are Smart, others are Standard. The *Runs on* card has
`ThinkingPoolRunsOn-<kind>` for each kind (`think-longer`, `research`,
`check-in`, `memory`, `naming`, `touch-zones`, `digest`, `end-of-turn-judge`,
`barge-in-judge`) with *Any member* (the default), *Prefer smart*, *Smart only*
and *These members*, and `ThinkingPoolRunsOnMember-<kind>-<n>` for each member
under *These members*. `ThinkingPoolRunsOnLabel-<kind>` (read-only text) is the
kind's label, which names all the work the kind controls (for example
`ThinkingPoolRunsOnLabel-touch-zones` reads *Touch zones, measuring the eyes
and writing a character's personality*). The kinds, their keys and their labels
come from one table, `ThinkingJobKinds.Rows`
([Job kinds](../CONVERSATION.md#job-kinds)). Each check-in card has `CheckInRunsOn-<id>` (first
*Like other check-ins*) and `CheckInRunsOnMember-<id>-<n>` under *More
settings*. All of them save `thinking-pool.json` or `check-ins.json`, so they
need `--allow-ui-effects`. `thinking_pool_status` gives each member `smarts`,
`smartsGuess` and `smartsChosen`, `runsOn` for each kind (`mode`, `members`
and `may`, the members the kind may use), and `kinds`: the table of job kinds
in the card's order, each with its saved `key`, `label`, `type` (its name on
the Thinking requests page), `priority`, `fast`, `needs` (the most its jobs ask
of a member), `whileLive` (`Starts`, `Waits` or `Stops`) and `helpers` (each
helper job that runs as the kind, with its `key`, `priority` and `ownerWaits`).
The `thinking_pool_check` step *kinds* checks that every kind has one row, the
keys are unique and in one format, and every helper job is in one row. `thinking-pool-status.json` gives each
member `smarts` and `smartsGuessed`, `runsOn` for each kind, and `placements`:
the latest jobs with the member each went to, its smarts, the outcome and `why`
(for example *Prefer smart: no Smart member came free within 30 s, so a
Standard one*). `check-ins-status.json` gives each check-in its own `runsOn`
(null: the kind's) and `firstMember`. The `thinking_pool_check` steps *runs on*
and the `check_ins_check` step *Runs on on its card* check these rules with
the production board.
([Computers that go offline](../CONVERSATION.md#computers-that-go-offline)).
`thinking-pool-status.json` gives each member `online` (whether its computer
answers now) and `offlineSince`, and the pool `slots` and `free` (members that
answer now), `configuredSlots` (every member) and `conversationModelStandsIn`
(every member that would run is offline, so thinking longer and research use the
conversation model). The desktop writes the file again on each change.
`thinking_pool_status` copies this into each member's `online` and
`offlineSince` and into `presence` (`slots`, `free`, `configuredSlots`, the
`offline` names); both stay null until the desktop has written the file. The
`presence` steps of `thinking_pool_check` take a member offline and back with
the production broker and plan. They check that its slots leave and come back,
jobs go to the other members, a job waiting in line starts on the member that
answers again, and the conversation model stands in when every member is
offline. They also check that the `think_longer` tool text stays byte-identical
during all of this. The member list in `background-jobs.json` (`places`) gives
each place `offline`.

```powershell
.\scripts\Invoke-MartletMcp.ps1 -Calls '[{"name":"thinking_pool_check"},{"name":"thinking_pool_status"}]'
```

### Thinking requests

The **Thinking requests** page (the side menu's `NavThinkingRequests`) lists
every request for the Thinking pool's slots since Martlet started: Thinking
pool jobs (the judges, summaries, check-ins, memory, naming and touch zones)
and the conversation's background work on the same slots (thinking longer and
research). The requests that wait or run come first, then the newest ended
ones. The journal (`ThinkingRequests`) keeps every request that has not ended
and the last 200 that ended, plus totals by kind. It keeps no request's text in
a log or a status file. For this PC's page only, it keeps each request's input
(what it sent its model, read into text the first time it shows, at most
32,000 characters) and each ended request's output (what it answered, its
first 16,000 characters). The input and the output never go in a log, a status
file or an MCP result.

`thinking_requests` (`dataDirectory`, and the optional filters `state`, `kind`
and `limit`) reads the desktop's `thinking-requests.json` in that data
directory. The desktop writes this file about one second after a change. The
result's `state` is `none` (no file yet), `unreadable` (the file is not valid
JSON or is larger than 4 MiB) or `loaded`. A loaded result gives `updated`,
`active` (how many wait, run or are paused), `kept`, the `totals` by kind
(`count`, `succeeded`, `problems`, `retries`, `preemptions`, and the average and
maximum wait and run in ms) and the `requests` that match the filters. Each
request gives its `id` (`tr-<n>`), `kind`, `source` (`Pool`, `Conversation` or
`Simulated`), `task`, `origin` (the companion), `priority`, `fast`, `needs`,
`state` and `note`, its `position` in line and `waitingForConversation`, the
`posted`, `started` and `finished` times, `firstWaitMs`, `waitedMs`, `ranMs`
and `totalMs`, and each try in `attempts` (`member`, `model`, `started`,
`ended`, `ms` and `ending`). It also gives `retries`, `preemptions`,
`answerCharacters`, `cut`, `inputCharacters` (the input's length, or null
when it isn't known yet), `inputs` (how many inputs a think or research job
sent; 0 for a pool job) and the request's limits. The filters are:

- `state`: `active` (waiting, running or paused), `done`, `problems` (ended
  other than succeeded or canceled, or with a retry or a stop for the
  conversation), or one state (`waiting`, `running`, `paused`, `succeeded`,
  `failed`, `timedout`, `stale`, `nomember`, `preempted` or `canceled`).
- `kind`: one job kind, such as `memory` or `barge-in-judge`.
- `limit`: 1 to 200 requests (50 by default). `matched` gives how many
  requests match and `shown` how many are returned.

On the desktop, `ThinkingRequestsSummary` reads, for example, *1 running, 0
waiting; 5 ended since Martlet started (1 with a problem): they waited 2.0 s
and ran 2.0 s on average.* `ThinkingRequestsPool` gives the pool's slots now,
`ThinkingRequestsEmpty` the empty state, and `NavThinkingRequestsCount` how
many requests wait or run (shown only when some do). Each row
`ThinkingRequest-tr-<n>` is a safe click: it only selects the request and
shows its details. `ThinkingRequestDetailTitle` gives the request's ID, type
and task, and `ThinkingRequestDetail` gives the request in full (type, task,
companion, needs, times, timings, tries, limits and the answer's length).
Beside it, `ThinkingRequestInput` shows what the request sent its model (the
instructions, the earlier messages, the message with its notes, an unfinished
answer it goes on with, and the picture, recording and tools that went with
it), and `ThinkingRequestOutput` shows what the request answered. The input and
the output are private: MCP never returns them. `ThinkingRequestInputState` (a
safe value) gives only the input's length, for example *Input: 1,240
characters.*, *Input: 52 characters, never sent.* or *Input: 9,800 characters,
the newest of 4 requests it sent.* (each research step and each new try of a
think sends a new input), or why there is none (*Input: none yet. It shows here
when the request is sent.*). An input longer than 32,000 characters keeps its
start and its end: *Input: 120,000 characters, 88,000 in the middle left out
here.* `ThinkingRequestOutputState` (a safe value) gives only the output's
length, for example *Output: 98 characters.*, or why there is none
(*Output: none yet. It shows here when the request ends.*, *Output: none. It
ended: failed.*). A long output reads *Output: the first 16,000 of 40,000
characters*. `ThinkingRequestDetailClose` (*Close*, or Esc) is a safe click: it
only hides the details again. The filters
`ThinkingRequestsFilter-all`, `-active` and `-problems`, the
`ThinkingRequestsTimingExpander` (*Timing by type*) and `ThinkingRequestsKind`
(it only opens) are safe clicks. `ThinkingRequestsTiming` is a safe value.
Choosing a type in `ThinkingRequestsKind` is `ui_select`, which always needs
`--allow-ui-effects`. `ThinkingRequestsClear` (*Clear finished*: it forgets the
ended requests, the totals stay) and `ThinkingRequestsCopy` (the clipboard)
need `--allow-ui-effects`. `ThinkingRequestTopic` is what the conversation asked
for (private): MCP never returns it.

Setting `MARTLET_SIMULATE_THINKING_REQUESTS` to a number (1-50) before you
launch the desktop posts that number of simulated requests once the window
shows. They wait, run on made-up members (*gpu-box (simulated)* and *laptop
(simulated)*), retry, fail and end over about half a minute. Each one has a
made-up input, and each one that succeeds has a made-up output. No model is
asked and nothing is sent. The
desktop log says *Posting N simulated Thinking requests*.

The `requests` steps of `thinking_pool_check` run the production job board with
simulated members (NOT models). They check that the journal records a success
(one try, with its run time and its output), a retry (a failed member, then a
member that answers: two tries) and a stale judge dropped after its wait (no
try and no output), each with plausible timings. They also check that
`Requests.Totals` counts the three by kind, and that each request's
[Thinking trace](../VOICE_LATENCY.md#thinking-trace-each-thinking-request-step-by-step)
lines say when it waited in line, started on a member, let it go and ended
(`thinking_trace` reads them from the desktop log; simulated requests write
them too).

```powershell
$env:MARTLET_SIMULATE_THINKING_REQUESTS = '6'
.\scripts\Invoke-MartletMcp.ps1 -Desktop -Calls '[{"name":"ui_click","arguments":{"id":"TourSkip"}},{"name":"ui_click","arguments":{"id":"NavThinkingRequests"},"waitMs":25000},{"name":"ui_click","arguments":{"id":"ThinkingRequest-tr-2"}},{"name":"ui_snapshot","arguments":{"idPrefix":"ThinkingRequest"}},{"name":"thinking_requests","arguments":{"state":"problems"}}]'
```

**Backup Thinking** ([a hedged request](../CONVERSATION.md#backup-thinking-a-hedged-request),
off by default): when the reply's Thinking model has no first words after the
wait, the same request also goes to a member that may answer for the
conversation, and the first to start gives the reply. `thinking_pool_status`
shows each member's `answersForConversation` and `paid` (a cloud provider), a
`backup` part (on, `delayMs` or automatic, the automatic wait's limits and
`wouldAsk`: the member the production choice picks now for a reply that is
taken, and why), and in the desktop's file `backup` (the wait used, the
automatic wait, recent replies, its 95th percentile, the last results and
counts; never what was said). `backup_thinking_check` (`scenario`, `delayMs`
one of 500-3000, 900 by default) rehearses the production race in
`ConversationTurn`, `ConversationRuntime.OpenTextAsync` and the member choice
(`ThinkingBackupMembers`) with two fixture Chat Completions endpoints on
127.0.0.1 (canned words, NOT AI), which note when the client stopped their
stream. Scenarios: `backup-wins`, `conversation-wins`, `late-conversation`
(the member asked, then stopped), `conversation-fails`, `no-member`,
`open-fails` (a member is chosen but its stream can't be opened: `Failed` with
the reason in `why`, not "no member"), `held`
(only a paid cloud member: asked once the reply is taken), `let-go` (a reply
started early and let go stops its member's stream too) and `members` (the
rules). Each turn reports its outcome, the member, when it was asked, the
first words, each endpoint's requests and whether its stream was stopped, and
the reply latency line. On the desktop the card reads through
`ThinkingPoolBackup` (the box), `ThinkingPoolBackupDelay` (the wait),
`ThinkingPoolBackupStatus` (who may answer and the wait now) and each member's
`ThinkingPoolAnswers-<n>` (*Backup for slow replies*); the box, the
wait and each member's box save `thinking-pool.json`, so they need
`--allow-ui-effects`.

```powershell
.\scripts\Invoke-MartletMcp.ps1 -Calls '[{"name":"backup_thinking_check"}]'
```

The *Busy pool* card reads through `ThinkingPoolPreempt` (*Higher priority
requests may stop lower ones*, on by default), `ThinkingPoolRaiseAfterStops`
(a stopped request becomes more important after 1-20 stops, 3 by default),
`ThinkingPoolRetries` (a failed request is tried again 0-10 times, once by
default) and `ThinkingPoolPriorityStatus` (the choices in words and, since
Martlet started, how many requests were stopped, made more important and tried
again). The box and both choices save `thinking-pool.json`
(`PreemptLowerPriority`, `RaisePriorityAfterStops`, `RetriesOnFailure`), so
they need `--allow-ui-effects`. The desktop's `thinking-pool-status.json` has a
`priority` part with the same choices (`preemptLowerPriority`,
`raiseAfterStops`, `retriesOnFailure`) and the counters `stoppedForPriority`,
`raised` and `retried`.

### What was sent (a message's context)

Each of Martlet's messages in the talk window (a reply, a report, a reaction
to a touch, a screen or camera remark) has a **What was sent** link
(`LiveMessageContext-<n>`: `-1` on the first message that kept it, then `-2`
and so on) beside its label while Martlet still keeps what its
Thinking requests carried: the newest 40 messages of the open conversation, in
memory only. A message from before the window opened, a picture and a note have
no link. Beside the link, each message shows its token line
(`LiveMessageTokens-<n>`, a safe value with the same number as its link), such
as *3,412 input tokens · instructions about 1,150*: the input tokens its
Thinking requests read (Martlet's estimate, *about ...*, when the model didn't
say; *in 2 requests* when it sent more than one), and the estimated tokens of
Martlet's instructions. It stays when the message lets *What was sent* go. The
link is a safe click: it only opens the *What was sent* window
(`MessageContextWindow`, owned by the talk window). That window shows the
request section by section, in the order it is sent, each with a colored bar:
*Instructions* (part by part: the persona, the tools prompt, tool guidance,
voice sounds and tones, character emotes and motions, the instructions for
this kind of reply, where the character looks and the closing instructions),
*Conversation history* (each earlier message, with the notes the conversation
kept with yours), *The message*, *Notes with the message* (part by part:
lorebook entries, remembered facts, who is talking, about this message, how
chatty to be), *Context notes* (sent once, never kept), *Picture* (click it for
`MessageContextPictureViewer`), *Recording* (`MessageContextPlay` plays it from
memory on Windows' default speakers), *Its unfinished answer*, *Tool rounds*,
*Tools it may call* and *Request settings* (model, destination, sampling,
Thinking steps, max reply tokens and tags). A reply that called tools or was
asked again sent more than one request: `MessageContextRequest` picks which
(the last, which wrote the answer, shows first). The bar at the top shows how
much of the request each section takes.

The text is private: MCP never returns `MessageContextText-<section>-<n>` or
the picture. Safe values: `MessageContextTitle` (*What was sent for this
reply*), `MessageContextSummary` (the model, how many requests, the shown
request's characters and estimated tokens, whether a picture or recording went
with it, and the input tokens the model said its requests read, with how many
came from its prompt cache), `MessageContextRequest` (*Request 2: after tool
round 1*), `MessageContextSize-<section>` (*4,210 characters, about 1,100
tokens, in 3 parts*), `MessageContextShare-<section>` (*Instructions 45%*) and
`MessageContextPart-<section>-<n>` (a part's name and size, such as *Persona ·
3,200 characters*; earlier messages have none, because they say who spoke).
`MessageContextSection-<section>` (`instructions`, `history`, `message`,
`notes`, `context`, `picture`, `recording`, `continuation`, `toolrounds`,
`tools`, `settings`) and `MessageContextClose` are safe clicks: they only
show or hide a section, or close the window. `MessageContextPlay` (it plays
audio), `MessageContextCopyAll` (the clipboard) and choosing a request with
`ui_select` need `--allow-ui-effects`.

### Image and audio models

Thinking, the text model, writes every reply. Pictures and recordings go to it,
or to an image or audio model of their own that puts them into words for it
([Image and audio models](../SENSE_MODELS.md)). `sense_models_status`
(`dataDirectory`) reads the Vision and Hearing lists (`pools.json`, shared; `file`
is `lists`, or before the desktop made them `loaded` for `sense-models.json`,
`none` or `unreadable`; it never makes them): for each kind its `list` as saved
(each member's `position`, `key`, `kind`, `on`, `model`, `engine`, `mayReceive`
(pictures and recordings may go there), `consented` and `usable` on this PC;
never a key), the chosen model as `source` (`Own`, or `Thinking` for an empty
list) and `own` (where it runs, whether it uses its own key or Thinking's, and
whether it sees and hears), the Thinking model with whether it sees and hears,
and for pictures and recordings the `path` (`Thinking`, `Described` or `None`),
the model, `unknown` (Martlet can't tell whether that model sees or hears) and
`why`. `oneModel` says both kinds use the same model of their own, and
`allThinking` that neither has one. `desktop` is the desktop's
`sense-models-status.json`: `conversation` (whether a talk window loaded the
settings; before that, a kind that goes to the text model says to set up
Thinking), and for each kind its path, model and why, `sharesConversation`, and
its line (`busy`, `waiting`, `held` for a reply, `runs` and the `last` job's
purpose, outcome, milliseconds, model and problem; never what was sent or
said). Its `jobs` are the decisions of which model takes each job with a
picture or a recording now (`SenseJobRouting`,
[Who takes each picture and recording](../SENSE_MODELS.md#who-takes-each-picture-and-recording)):
for each `job` (`ReplyPicture`, `ScreenSummary`, `HelperPicture`,
`CheckInScreenshot`, `ReplyVoice`, `PcSound`, `CheckInRecording`) its `kind`,
`name`, `poolKind`, the `taker` (`SenseModel`, `PoolMember`, `ThinkingModel`,
`ThisPc` or `Nobody`), its `model` and `why`, and each of its `steps` in order
(`taker`, `takes`, `model`, `why`). The runners follow these decisions.

Each kind's `pool` ([The image and audio pools](../SENSE_MODELS.md#the-image-and-audio-pools))
is in both parts. From the data directory, while a model of its own describes
the kind: its `lane` (`vision` or `hearing`) and the `members` a job tries in
order (`position`, `key`, `name`, `place`, `chosen`): the chosen model, then the
list's other models that may take the kind, without the Thinking model. The
desktop's `pool` has the `list` too and also leaves out members on the
conversation's own computer and graphics card. It adds `jobs`, `elsewhere`
(another member took the job), `waited` (the job waited for a busy pool) and
the `last` job's `member`, `name`, `position`, `chosen`, `busy`,
`unavailable`, `waitedMs` and `at`.

`sense_models_check` rehearses the production routing (`SenseRouting`) over
the combinations of text, image and audio models with fixture model names (a
paired computer's model that hears takes recordings), the one rule for what a
model takes (`one-rule-for-every-place`, `ModelSenses`: a paired computer's
Test vision counts for the Vision list and the Thinking pool alike, and a
model Martlet can't tell about is tried from the list but gets no Thinking
pool picture jobs), the order of each job with a picture or a recording
(`job-routing`, `SenseJobRouting`: with an image model, an audio model and a
Thinking pool member, summaries go to the image and audio models first and
helper jobs and check-ins to the pool first; with none, Thinking takes the
reply's picture, recording and helper jobs and the sound tagger names PC
sounds), the `sense-models.json`
round trip and the lists made from it once (`lists-made-once`; a damaged list
file reads as the text model), and the production lines (`SenseLanes`) with a
simulated runner, NOT models: no model of its own, one job at a time, a newer
picture taking the place of a waiting one, priorities, a stale job, refusals,
failures, a timeout, the kind check, one line for one model used for both
kinds, and the conversation first (`lanes-hold`: a job waits while a reply
holds the model's hardware, a running job is stopped, and a job the hold
outlasts is dropped; `lanes-yield`: a helper job gives way to a reply's
picture and starts again, while a summary waits behind it). Then the pools
(`SensePool`) with simulated members on a queue of their own: `pool-members`
(a Vision list's members as places: a computer not paired here, a cloud
provider without agreement, a server not allowed pictures, a member off or
kept for another PC left out; the first model that may see chosen),
`pool-first-free` (a free chosen model takes the job with one request),
`pool-busy-next` (a busy and an unreachable member pass it on at once),
`pool-all-busy-waits` (the first member to free takes it) and `pool-held` (a
member held for a live turn is passed over; a pool held everywhere ends at
once). In-process; it writes only a temporary folder.

```powershell
.\scripts\Invoke-MartletMcp.ps1 -Calls '[{"name":"sense_models_check"},{"name":"sense_models_status"}]'
```

`image_model_check` (`dataDirectory`; optional `imageOrigin` and `imageModel`
to rehearse an image model of its own that isn't saved; optional `delayMs`,
200 to 10,000, default 1,500) shows the image model's side
([Pictures: the image model](../SENSE_MODELS.md#pictures-the-image-model)).
`route` is where pictures go now (the production `SenseRouting` over
`sense-models.json`, the Thinking route and `model-abilities.json`), and
`prompts` says whether *Image model: describe the picture*, *Pictures as words*
and *What the image model saw* are the default, edited or empty. `live` is the
desktop's `image-model-status.json`: the path, model and why, `sharesConversation`,
how many descriptions were `made` and `failed` and how many are `running`, the
`newest` one's age, time, trigger, model and source, how many replies took a
description (`replies.described`) or went without one (`replies.without`), how
many looks were described, `reused` a description or `failed`, and the
`lastReply`. Never a description. `lastTurn` is the newest `Picture path:` and
`Image model:` lines of the desktop log.

`rehearsal` runs the production `SenseLanes`, `PictureDescriptions`, prompts
and Chat Completions adapter against two fixture endpoints on 127.0.0.1 (the
image model and Thinking; canned words, NOT AI) with synthetic screenshots (no
screen capture). `ahead`: you start to talk, and the image model describes the
newest picture with the program in front, the window's title and what was said
lately. `describedReply`: the reply takes the description at once, as a note;
Thinking gets no picture (`picturesToThinking` 0), the fixed *Pictures as
words* instruction and no seen tag; `keptLine` is the `[Screen]` line the
conversation keeps. `neverWaits`: the picture changed and the image model
answers only after `delayMs`; the reply is sent at once without a description
(`replySentWithoutWaitingMs`), and the description is ready for the next
reply. `look`: a look at the same picture uses the description again (no new
request) and sends Thinking the glance message with it; a look at a new
picture asks the image model first. `ok` is true when all of these hold.

```powershell
.\scripts\Invoke-MartletMcp.ps1 -Calls '[{"name":"image_model_check","arguments":{"imageOrigin":"http://127.0.0.1:11434/v1","imageModel":"qwen2.5vl:7b"}}]'
```

The talk window shows the image model too: `LiveTurnInputs` says *the image
model's description of the picture* when a reply or look took one, and the help
text of `LiveVisionStatus` names the image model, how long its last
description took and whether the last reply took it. Companion › Vision's
`VisionStatus` and `VisionDisclosure` say where pictures go.

`audio_model_check` rehearses the audio model ([Recordings: the audio
model](../SENSE_MODELS.md#recordings-the-audio-model)) with the production pieces
against two fixture Chat Completions endpoints on 127.0.0.1 (canned answers,
NOT AI): the Chat Completions adapter, `SenseLanes`, `SenseRouting`,
`ContextBoard` and the desktop's own `VoiceNote.cs` (the job, the note, the kept
line and the consent rule). `steps` (each `Name`, `Passed`, `Detail`):
`route-*` (where recordings go: the defaults, an audio model of its own beside
a text-only or a hearing Thinking model, one that is exactly Thinking's model,
the same as an image model that can't hear, one that refused a recording
before), `consent` (with `consent` listing Ollama on this PC, an Ollama cloud
model, another server on this PC and a cloud provider: `staysOnThisPc` and the
result never chosen, ticked and unticked), `in-time-audio-model-request` (the
audio model got the recording, its fixed instructions and the last lines said),
`in-time-reply-takes-the-words` (Thinking got the transcript and the note,
never the recording), `in-time-kept-line` (the next request keeps only
`(voice: ...)`), `late-reply-never-waits` (the reply's request went without
the words while the audio model still answered), `late-words-go-to-the-board`
(one consume-once note that the next request took), `nothing-stands-out`
(*none* adds nothing) and `refused` (`Refused`). `requests` counts what each
fixture got; `ok` needs every step. With `dataDirectory` (the script passes its
disposable one), `saved` says where recordings go with its `sense-models.json`:
`path`, `model`, `why`, `staysOnThisPc` and `thinkingGetsRecordings`. The
recording is a synthesized clip, never a microphone; nothing leaves loopback and
no credentials are read.

**The image model and the audio model.** Companion › Vision's and Companion ›
Hearing's Now cards read as status (ui_snapshot with `idPrefix` `ImageModel` or
`AudioModel`). `ImageModelNow` is the list in words (*Nothing in the list:
the text model takes the pictures itself (Thinking: gemma4:e2b).*, *Ollama on
this PC (qwen2.5vl:7b) is first in the list; 1 more takes a job when it is
busy.*). `ImageModelRoute` is where
pictures go and why (the routing's words). `ImageModelKnown` is what the model
that takes them is known to do: *lab/sees sees pictures, as Martlet found out
(last check on 10/8/2026: a test request).*, *By its name, qwen3.5:4b sees
pictures. Test vision makes sure.*, or that Martlet doesn't know yet.
`ImageModelSent` (a model of its own only) is what is sent and where: pictures
stay on this PC with Ollama, go to an app on this PC that may pass them on, go
to a provider's host where requests may cost money, or go to a paired computer
through its pinned connection. `ImageModelTestStatus` says what Test vision
sends or what it last found (the model's one-word answer and the time).
`VisionToggle` (*Turn vision off* or *on*) and `HearingToggle` (*Turn hearing
off* or *on*) end each Now card.

Below it, each page has the shared pool list for its models (the pool list
control, described with Devices › Sharing work): `Pool-vision-...` and `Pool-hearing-...`
(`-Summary` reads, for an empty list, who takes each job instead, from
`SenseJobRouting`: *Nothing in the list is on. Instead: Thinking's own model
(gemma4:e2b) takes the picture with a reply; diva (qwen2.5vl:7b) in the
Thinking pool takes screen summaries, touch zones, the character's eyes and
check-ins with a screenshot.*; `-Member-<i>` reads *1: This PC: Each companion PC
itself; qwen2.5vl:7b: sees pictures; in use: it takes the pictures first.*).
Under the members, *Also gets your pictures: the Thinking pool* names every
Thinking pool model that sees (on Hearing: hears), so the page lists every
model that gets them, cloud providers and servers included:
`Pool-vision-AlsoSummary` (*Your pictures also go to 1 model in the Thinking
pool, for background work. ...* or that no Thinking pool model sees) and one
`Pool-vision-Also-<n>` per model (*integrate.api.nvidia.com
(google/gemma-3-27b-it). A cloud provider or server in the Thinking pool. It
gets your pictures for screen summaries, and touch zones, the character's eyes
and check-ins with a screenshot. Requests there may cost money.*, or why it
gets none: off, May receive pictures and recordings off, or no quick or long
jobs). `Pool-vision-AlsoOpen` (*Open Background thinking*) is passive. The same
model names show in `VisionScreenSummaryStatus`, `VisionDisclosure` and
`ThinkingSenses`, from the saved settings, so they are right before a
conversation runs.
`Pool-vision-Settings-<i>` and `Pool-vision-AddCloud` are passive: they only
show a member's settings or the cloud form. In This PC's settings,
`ImageModelLocalModel` (a combo box you can type in; `ui_set_text` works on it)
is the model picked, `ImageModelLocalStatus` what Ollama has and what each
model takes, `ImageModelLocalKnown` what the picked one is known to do and
`ImageModelLocalFit` whether it fits beside Thinking's on the graphics card.
Another member's settings have its model (`Pool-vision-Model-<i>`), a
computer's engine (`Pool-vision-Engine-<i>`), a server's permission
(`Pool-vision-Media-<i>`) and a key box (`ImageModelKey-<i>`, never read). In
the cloud form, `ImageModelProvider` is the provider and `ImageModelKeyStatus`
what the key box will do (never the key, base URL or model ID typed:
`ImageModelBaseUrl`, `ImageModelModelId` and `ImageModelKey` aren't read). The
same IDs exist with `AudioModel` and `Pool-hearing-`. These save
`pools-local.json` (and a key), ask a server or send a test request, so they
need `--allow-ui-effects`: the list's On, Up, Down, Remove and Add buttons,
`ImageModelUseLocal`, `ImageModelPullModel`, `ImageModelCheckOllama`,
`ImageModelSaveCloud` (*Add to the list*, with `ImageModelConsent`),
`Pool-vision-Save-<i>`, `ImageModelTest` and the `AudioModel` and
`Pool-hearing-` ones, `VisionToggle` and `HearingToggle`.
Companion › Thinking's `ThinkingSenses` says where pictures and recordings go;
its `ThinkingOpenImageModel` and `ThinkingOpenAudioModel` links (to Vision and
Hearing) and Listening's `ListeningOpenHearing` are passive.

`model_lab` (`action`: `start`, `status` or `stop`; optional `port`
1024-65535 for a scripted run) keeps an OpenAI-compatible fixture endpoint on
127.0.0.1 running for the desktop, so these cards (and Thinking) can choose and
test models with no real provider (NOT AI). `start` returns `baseUrl` (type it
as a *Custom OpenAI-compatible server*'s base URL, or as Thinking's *Another
address on this PC*). `/v1/models` lists `lab/sees` (text and pictures),
`lab/hears` (text and recordings), `lab/omni` (text, pictures, recordings and
video), `lab/text` (text only), `lab/notools` (text only, no tool calls) and
`lab/gone` (a retired model) with their input modalities and supported
parameters (`tools`), as OpenRouter lists them. Chat completions
refuse a picture or a recording that a lab model doesn't take (error 400),
`lab/notools` refuses a request with tools (error 400), `lab/gone` answers
every chat completion with HTTP 410 Gone, and the tool models call Test tools'
made-up tool; the lab
reads Test vision's word by comparing the picture with this PC's own drawings
of the test words, reads Test hearing's word with Windows speech recognition
limited to the test words (when this PC has an English recognizer), and answers
anything else with fixed text, streamed when asked. `status` lists the recent
requests by model, the kinds of parts they carried and what the lab answered,
never their content. Each chat completion also has `start`: short SHA-256s of
its `Tools` and of each of its `Messages` as sent. Compare two runs to see
whether the start of each request stayed byte-identical, so prompt caches still
match. It ends with the MCP server.

```powershell
# Add lab/sees to the image models in a disposable desktop and test it (it is first in an empty list).
.\scripts\Invoke-MartletMcp.ps1 -Desktop -AllowUiEffects -Calls '[
  {"name":"model_lab","arguments":{"action":"start","port":52817}},
  {"name":"ui_click","arguments":{"id":"TourSkip"}},
  {"name":"ui_click","arguments":{"id":"NavCompanion"}},
  {"name":"ui_click","arguments":{"id":"CompanionTab-Vision"}},
  {"name":"ui_click","arguments":{"id":"Pool-vision-AddCloud"}},
  {"name":"ui_select","arguments":{"id":"ImageModelProvider","item":"Custom OpenAI-compatible server"}},
  {"name":"ui_set_text","arguments":{"id":"ImageModelBaseUrl","text":"http://127.0.0.1:52817/v1"}},
  {"name":"ui_set_text","arguments":{"id":"ImageModelModelId","text":"lab/sees"}},
  {"name":"ui_toggle","arguments":{"id":"ImageModelConsent"}},
  {"name":"ui_click","arguments":{"id":"ImageModelSaveCloud"}},
  {"name":"ui_snapshot","arguments":{"idPrefix":"Pool-vision"},"until":"lab/sees"},
  {"name":"ui_click","arguments":{"id":"ImageModelTest"}},
  {"name":"ui_snapshot","arguments":{"idPrefix":"ImageModel"},"until":"read the test word"}]'
```

### Live floor (the live turn first)

The live floor puts the live conversation turn before all background work
([The live floor](../CONVERSATION.md#the-live-floor-the-live-turn-comes-first)).
`live_floor_status` (`dataDirectory`) reads the data directory's routes and
Thinking pool: what the conversation runs on (`resources`: Thinking, voice and
listening, each on `this-pc`, a home computer or a cloud provider, with the
paired hosts and routes the floor holds while you talk), which pool members
share it (`members[].shares`), what the floor does to each job kind on such a
member at Listening and at Live (`rules`), and the desktop's `live-floor.json`
(`desktop.file`: its level, replies holding it, Live periods, live resources,
members, what it held and stopped by kind this turn and in all with the reply
latency part, the hold client, the hosts asked and the holds they granted, the
work queue's stopped background requests and the last changes with why; never
what was said). `Invoke-MartletMcp.ps1` gives it the disposable data directory
unless one is named. `live_floor_check` (optional `said`: up to 32 lines of
your own to classify) rehearses the production `LiveFloor`, `LiveFloorRules`,
`ThinkingJobBoard`, `BackgroundJobs` and `WorkQueue` with fixture inputs and
simulated members (NOT models), and returns `passed`, each line's `realWords`
and each step: which words go Live, the levels on a clock of their own (voice,
quiet, a sound, words, a reply and its grace), the microphone on the production
detector (`LiveFloor.Voice`: a 20 ms click, 2.8 s of typing and Martlet's own
voice from the speakers stay Idle; 300 ms of voice is Listening), the board at
Listening (new work
waits for the conversation, running work and judges go on) and at Live (a
summary dropped as `Preempted`, remembering and naming stopped and queued again,
touch zones going on, judges running), a member on another computer never held,
a think stopped and going on from what it wrote (in place as the unfinished
assistant message, or again with it as context), research waiting for the
conversation instead of being refused, the conversation model's own place held
above Idle, and the work queue stopping this PC's background request for a live
reply (once, also when the stopped request takes 100 ms to free the computer).
The desktop log's `Live floor:` lines say each change, and the reply
latency line ends with what the floor held and stopped
(`latency_report` returns it as `liveFloor`).

```powershell
.\scripts\Invoke-MartletMcp.ps1 -Calls '[{"name":"live_floor_check","arguments":{"said":["Mm-hmm.","Can you check the weather?"]}},{"name":"live_floor_status"}]'
```

### Reading

Companion › Reading ([Reading](../READING.md)) lists the places that read the text
on the screen while Martlet watches, in order: This PC with Windows OCR (the
default) or with the `ocr` host role (route `martlet.gateway.ocr.v1`,
[Reading host role](../OCR_HOST.md)), and your computers' Reading role. With
nothing on, reading is off. The local server's `reading_check` reads a data
directory's Reading list (`dataDirectory`, see `pool` below), then reads a drawn 1024 x 576 test picture with known text
(`HEALTH 87 / 100`, `Score: 12450`, `VICTORY`, a chat line) with Windows OCR on
this PC, as watching does. It returns the lines, the joined text, the missing
words and the milliseconds (the first read and a second one). Its `desktop`
part draws a 1920 x 1080 desktop with 108 lines of small (12 px) text. Windows
OCR reads it at full size, as Martlet reads the screen, and at the 1024 x 576
of a vision look. For each size, `desktop` gives the lines found, the drawn lines
read right (`linesRead`) and the milliseconds; `ok` is true when the full-size
read gets at least 90% of the lines. On the test machine, the full-size read got
102 of 108 lines in 138 ms, and the 1024 x 576 read got none. Its
`desktop.fourK` part draws a 3840 x 2160 desktop with 192 lines of 18 px text
and reads it the same way. With `endpoint`, a Reading worker on loopback such
as `http://127.0.0.1:50087/`, it also calls the worker's `GET /status` (its
engine, model and accelerator) and `POST /read` with the same picture as a PNG
(`worker.read`). Then the worker reads both drawn desktops as PNGs
(`worker.fullHd` and `worker.fourK`: lines found, `linesRead`, the PNG size and
the milliseconds). It never captures the real screen.

`reading_check` also shows the [Reading pool](../READING.md#the-reading-pool).
With `dataDirectory`, `pool` gives this PC's Reading list and the places a read
tries: `area` and `lane` (`reading`), `file` (`pools-local.json`), `list`
(`saved`, or `made from reading.json (not saved yet)` before the page saved it),
`off` (nothing on), `priority` (`Background`), `waitSeconds` (3), `ownHost`
(this PC's own host service), `members` (each member's `index`, `key`, `name`,
`kind`, `on`, `reads` in words, its `engine` and `model` settings, `onlyFor` and
whether it is `paired` here) and `tries` (the production `ReadingPool.Targets`:
each place's `key`, `hostId`, null for Windows OCR, and the `member` that named
it). `poolCheck` rehearses the production list code, planner and queue
(`WorkQueue`) with simulated computers that read one screenshot at a time and
turn another away at once (`job.busy`), **NOT real hosts**. Each step reports
`passed` and its detail: the list's order (Windows OCR, then each computer once;
a member off, kept for another companion PC or not paired left out); This PC
with the Reading role as its own host service; nothing on as off; the list made
from `reading.json`; each PC keeping its own list; the first member reading
while free (`WaitedMs` near 0); a busy or unanswering one passed over at once; a
read waiting for the first to free; a read giving up as busy after the wait; and
a friend's host busy with its owner's work handing the read to your own
computer, or, when it is the only one, leaving it for later (`Refusal`
`later`). `poolCheck.ok` is true when every step passed.

`reading_check` also shows how often Martlet reads. With `dataDirectory`,
`pace` gives Companion › Reading › **Read the screen** (`talk-preferences.json`'s
`ReadEveryMs`): `everyMs` (500, 1000, 2000, 3000 by default, 5000 or 10000),
`describe` in words, `source`, `choicesMs`, `refreshSeconds` (9: a still
screen is read again after this long) and `pictureChange`. `paceCheck` runs
the production pace (`ScreenReadPace`) for each choice over a simulated minute,
polled every 100 ms as the talk window does, one read at a time. For each
choice it gives `changingFastPerMinute` (a screen that changes in every
picture, with 150 ms reads, as on a graphics card; it must equal
`expectedFastPerMinute`), `changingSlowPerMinute` (2 s reads, as PP-OCRv5
mobile on a processor: the read sets the pace) and `stillPerMinute` (a still
screen: at most one read every 9 seconds). `paceCheck.ok` is true when every
choice matches. It uses simulated time and pictures only, **NOT the real
screen**. In that simulated minute, twice a second gives 120 reads with fast
reads and 30 with slow ones, where the old 3-second pace gives 20.

`settleCheck` runs the production flicker filter (`ScreenText.Settle`, see
[Reading](../READING.md#what-martlet-does-with-the-text)) over eight simulated
reads of a game screen. Each step gives the `read` lines, the lines that
`counts`, the `pending` lines that wait for the next read and the `change` of
the text that counts (what the look pacer hears). The steps are: a first read
(nothing counts yet), the same read (all lines count), OCR reading "Apple" as
"App1e" once while a timer ticks (nothing changes, `change` 0), the timer going
on (it counts as a live value), "VICTORY" in two reads (it counts at the
second), and another window (the old text goes at once). `settleCheck.ok` is
true when every step matches. It uses simulated lines only, **NOT the real
screen**.

On the test machine (a 24-thread processor, Docker Desktop), the workers read
the drawn desktops as follows:

| Engine | 1920 x 1080, 12 px (108 lines) | 3840 x 2160, 18 px (192 lines) |
| --- | --- | --- |
| Windows OCR | 102 lines, 123-188 ms | 145 lines, 282-310 ms |
| RapidOCR (PP-OCRv4), processor | 101 lines, 1.7-2.9 s | 112 lines, 2.3-3.4 s |
| PP-OCRv5 mobile, processor | 103 lines, 1.7-1.9 s | 192 lines, 3.6-4.2 s |
| PP-OCRv5 server, processor | 108 lines, 65 s | 192 lines, 131 s |

The server model is for an NVIDIA graphics card only: on a processor, one read
takes longer than the gateway's 15 s limit.

Desktop automation: Companion › Reading's list is the shared pool list
(`Pool-reading-Summary`, `Pool-reading-Member-<i>` such as *1: gpu-pc: A paired
computer; runs PP-OCRv5 mobile.*, `Pool-reading-On-<i>`, `-Up-<i>`, `-Down-<i>`,
`-Remove-<i>`, `-OnlyFor-<i>`, `-AddThisPc` and `-AddHost-<host>`; the pool list
control is described after `work_sharing_check`). `Pool-reading-Settings-<i>`
only shows or hides that member's settings. In them, This PC's engine pills
(`ReadingEngine-windows-ocr` and `ReadingEngine-ocr`) save the member's engine,
so they need `--allow-ui-effects`, and `ReadingWindowsState` says whether Windows
can read text here. For the Reading role, the model pills
(`ReadingModel-<place>-<model>`, place `this-pc` or the host, model
`ppocrv5-mobile`, `ppocrv5-mobile-gpu` (PP-OCRv5 mobile on the graphics card),
`ppocrv5-server` or `rapidocr-ppocrv4`) are passive clicks:
they only show that model. The pills that need a graphics card are disabled on a
computer without an NVIDIA graphics card. `ReadingHostState-<place>` (where the role stands and with
which model), `ReadingModelNote-<place>` (where the chosen model runs, how
accurate it is and its download) and the labels of `ReadingSetUp-<place>` and
`ReadingSwitch-<place>` are safe values. Set up and Switch (which changes a
computer's Reading role to the chosen model) need `--allow-ui-effects`.
`PickerCompare-Reading` only shows or hides the comparison table. `ReadingNow`,
`ReadingLast`, `ReadingTestState` and the label of `ReadingTest` are safe values.
`ReadingEvery` (*Read the screen*: the chosen option, from *Twice a second* to
*Every 10 seconds*) and `ReadingEveryStatus` (what it means) are safe values.
Choosing an option with `ui_select` saves `talk-preferences.json`, so it needs
`--allow-ui-effects`; `reading_check`'s `pace` then shows it. `ReadingLast` and
`ReadingTestState` also say how long after the read before a read started.
Read my screen now (`ReadingTest`) captures the screen, so it needs
`--allow-ui-effects` too. `ReadingTestState` gives the size of the full-size
screenshot it read, for example *(1920 x 1080 screenshot)*. The text it read
(`ReadingTestText`) is never returned.

### Picture making

While Companion › Picture making has a place (or `MARTLET_PICTURES_FIXTURE=1`), every
reply on a route that does function calling also gets `draw_picture`
(`description`, optional `title`, `shape`, `avoid`), after the song tools. It
starts a `picture-N` background job and returns at once; the finished picture is
kept as a `picture` creation and shown in the talk window (`LivePicture`, whose
click opens `LivePictureViewer`), and `perform_creation` shows a kept one again.
See [Picture making](../PICTURES.md). The talk window's status says *Starting a picture in
the background…* while it is called, and the desktop log notes `Pictures:`
lines (where, size, seconds; never the description).

The local server's `pictures_status` reads a data directory's `pictures.json`
(place, workflow, checkpoint or model, whether an own key is saved; never a key),
its Picture making list as `pool` (`pools.json`, shared: `configured`, `off`, each
member's `key`, `kind`, `name`, `settings`, `consented`, `ownKeySaved` and the
`place` it reads as; without a list yet, the list the desktop would make from
`pictures.json`) and `tries` (the places a picture tries now, in order),
the loaded workflow's node count, the picture creations (shape, size, engine,
model, seconds, fixture; never titles or descriptions), the tool and job kind,
and `afterReply` (the Songs, pictures and creations check-in tool set, as
`songs_status` shows it).
`pictures_check` draws one picture through the production maker: `place`
`fixture` (default) or `comfyui` with `address` (and `workflow`
`z-image-turbo`, `checkpoint` with `checkpoint`, or `custom` with `workflowFile`),
reporting availability, every progress stage, the media type, size, SHA-256 and
seconds; with `dataDirectory` it keeps the picture as a creation there and reads it
back, with `saveDirectory` it writes the file. `place` `pool` rehearses the
Picture making list's routing (`PictureMakingPool`, `WorkQueue` and `ComfyPictureMaker`) on
simulated ComfyUI computers (FIXTURE - NOT real hosts or models): a busy, absent
or unsuitable computer is passed over, every one busy waits in the shortest
queue, two pictures go to two computers, one computer gets no extra request, and
the first list is made from `pictures.json`. It returns `ok` and each step. It
never calls OpenRouter or NVIDIA
Build (a picture costs money). Desktop automation: Companion › Picture making's *Where
it draws* is the shared pool list (`Pool-pictures-...`, see the pool list
automation IDs): `Pool-pictures-Summary`, `Pool-pictures-Member-<i>` (a place,
its kind and its state: whether the Picture making role is ready there, its workflow,
or a cloud provider's key status), `Pool-pictures-On-<i>`, `-Up-`, `-Down-`,
`-Remove-`, `-OnlyFor-<i>`, `-AddThisPc`, `-AddHost-<host>`, `-Address` and
`-AddAddress`. `Pool-pictures-Settings-<i>` (show or hide a place's settings),
`PicturesCheck` and a place's `PicturesComfyConnect-<i>` contact the places, so they need `--allow-ui-effects`.
`PicturesNow` (where it draws and the places that draw when the first is busy),
`PicturesTestState` (each place ready or why not, or the test picture's size,
place and seconds), and in a place's settings `PicturesMemberState-<i>`,
`PicturesSetUp-<i>`, `PicturesWorkflow-<i>`, `PicturesCheckpoint-<i>`,
`PicturesComfyState-<i>`, `PicturesLoadWorkflow-<i>`, `PicturesSaveSettings-<i>`,
`PicturesKeyStatus-<i>` and `PicturesSaveKey-<i>`, and the cloud provider form's
`PicturesCloudProvider`, `PicturesModel`, `PicturesKeyStatus`, `PicturesConsent`,
`PicturesAddCloud` and `PicturesCloudState` (what the last Add did, or why it
didn't), return their text (never a key). The list's On, Up,
Down, Remove and Add, and Set up, Save, Load workflow file, Save key, Add
OpenRouter or NVIDIA Build and Draw a test picture save, install or draw, so
they need `--allow-ui-effects`; the Creations page shows a picture as
`CreationPicture`.

## Local MCP control (Windows)

`deep_thinking_role_selftest` (no arguments) rehearses the Deep thinking host
role (`deep-thinking`, deploy/host/roles/deep-thinking) end to end with the
production code: one real gateway (`lab-deep`: Kestrel, pinned TLS, a throwaway
certificate) on `127.0.0.1` that publishes both of a host's Ollama roles the way
`martlet-host` does, Thinking's `martlet.gateway.ollama-chat.v1` (the real
`OllamaRelayWorker` for `gemma4:e4b`) and Deep thinking's own
`martlet.gateway.deep-thinking-chat.v1` (`OllamaRelayWorker.DeepThinking` for
`qwen3:8b`), each over its own fixture Ollama (canned text, NOT AI) and placed on
its own graphics card (so live turn first never stops the think), and a
simulated desktop that pairs and streams through the desktop's paired client
(`HostChat.cs`). It runs `src\Martlet.NodeLinkCheck` (mode `deep-thinking`,
`DeepThinkingRehearsal.cs`) and returns `{exitCode, report}` like
`network_selftest`. Its steps: the host advertises both routes with their own
paths and models and the same contract and fifteen-minute bound; Thinking's
advertised route saves as this PC's Thinking route through the desktop's own
handoff (`HostHandoff.ToHost` with `HostRoute.Snapshot`), so its long-think
bound fits the saved settings; a think on the
Deep thinking route is held mid-answer while a reply streams on Thinking's
route, and the reply finishes first (parallel, never queued); each request
reached its own Ollama (the think with `"think":true`, its own model and a
32,768-token context, the reply without Thinking steps); a Thinking pool job's
request (remembering: a budget of 25,600 tokens, the role's largest 32,768-token
window and Thinking steps off) is accepted and loads that window (Martlet 0.54.0
sent the budget as `maximum_context_tokens` below `context_tokens`, and the
gateway refused it with `request.invalid`); two thinks run at once
on the Deep thinking role's two slots (`OllamaRelayWorker.DeepThinking` with
`slots: 2`, advertised as the route's `maximum_concurrency` and read as
`HostRoute.MaximumConcurrency`) while a reply streams, a third is turned away
with `job.busy` and each finishes once released; and the chat client
refuses a route whose ID and path don't match. Nothing leaves loopback and
nothing is written to disk or Windows Credential Manager; it does not cover
`martlet-host` installing the role, a real Ollama or model (the slots' graphics
memory), a GPU or a real LAN.

`model_ability_check` shows what models were found to take on each route (a
route is one server and one model ID): hearing (recorded audio), seeing
(pictures), video itself (apart from seeing frames), tool calls and whether the
server retired the model. It also rehearses how Martlet finds out (optional
absolute `dataDirectory`, default the current user's). `saved` lists
`model-abilities.json` (`file` `none` or `loaded`, `count`, and per model
`Origin`, `ModelId`, `Hears`, `Sees`, `Video`, `Tools`, `Retired` (the date the
route answered HTTP 410 Gone), `Source` and `CheckedAt` (the latest finding),
and `sources`: each fact's own `Source` and `At`). The same list
travels to the owner's other computers as the `model-abilities` shared setting
(`settings_sync_status` shows its value). `fixture` runs the production
detection against servers on 127.0.0.1 shaped like OpenRouter's model list
(`architecture.input_modalities` and `supported_parameters`: `openRouterOmni`
hears, sees, takes video and calls tools, `openRouterSight` only sees,
`openRouterUnlisted` unknown), llama.cpp
(`/props` `modalities`: `llamaCpp` hears, doesn't see), Ollama
(`/api/show` `capabilities`: `ollamaGemma4E2b` hears, sees and calls tools,
`ollamaQwen3` only calls tools; Ollama takes no video) and NVIDIA Build (a
model list with IDs only, then `models.md` and the model's page:
`nvidiaGemma` sees, takes video and calls tools, with its context, from the
page's Specifications; `nvidiaService`, a page that isn't a hosted chat
model, says nothing), each with `Hears`, `Sees`, `Video`, `Tools` and
`AbilitySource`. Then Test hearing
(`ModelHearingTest`) against a fixture Chat Completions endpoint that answers
the test word only when the request carries the recording (it is told the
word: NOT AI): `testHears` true, `testDropsAudio` (answers something else) and
`testRefusesAudio` (error 400 about audio) false, `testWrongKey` (401) null,
`testGone` (410) null with `Status` 410;
`testRequest` checks the request: `text` and `input_audio` parts, a valid WAV,
not streamed, Thinking steps off and the word only in the recording
(`wordInRequestText` false). Test vision (`ModelVisionTest`) runs the same way
with the desktop's own picture of the word (`VisionTestPicture`, drawn on an
STA thread): `testSees` true, `testDropsImage` and `testRefusesImage` false,
`testWrongKeyVision` null; `visionRequest` checks `text` and `image_url`
parts, a PNG data URL that is the drawn picture (`samePicture`), not streamed,
Thinking steps off and the word only in the picture; `picture` is the drawing
(640 x 240 PNG, its bytes and `darkShare`, the part the letters cover).
Test tools (`ModelToolTest`, one made-up tool `say_test_word` and a request to
call it with a test word): `testCallsTools` true, `testWordsOnly` (answers in
words) and `testRefusesTools` (error 400 about tools) false, `testGoneTools`
(410) and `testWrongKeyTools` (401) null; `toolRequest` checks one tool, one
message and no streaming. `routeFacts` needs all the video, tools, NVIDIA and
410 rows.
`decisions` are the decisions
replies use (Gemma 4 E2B in Ollama on this PC hears by name, Gemma 4 12B only
once Ollama says so, OpenRouter's grok-4.3 doesn't when its list says so,
Thinking in Ollama on a paired computer is decided like any other model:
`hostOllamaGemma4E2b` and `hostOllamaGemma4E4bAlias` hear by name and
`hostOllamaRefusedFound` doesn't after a refused recording; OpenAI's Responses
route never, a retired model never). Video and tools go only by what was found,
never by a name (`gemma4VideoFound`, `qwen3ToolsFound`, `noToolsFound`,
`unknownVideo` and `unknownTools` Unknown; OpenAI's route calls tools, a
paired computer's gateway doesn't). A route found retired (HTTP 410) neither
sees nor hears (`goneSees`, `goneHears`, `goneRetired` with its date and
source) until it answers again (`answeredRetired` false, `answeredSees`
Supported); Martlet's built-in list of retired models stays the fallback
(`builtInRetired`). A test's answer stays when the server's metadata says
otherwise later (`testKeptOverMetadata`), and a later test replaces it
(`laterTestReplaces`). `hostRoute` checks whether a paired
computer's Thinking route takes a recording (`HostRoute.CarriesAudio`): a
route of this version (`requestBytes`, room for a 30-second recording) and the
Deep thinking route do, an older host's route (`olderHost`, room for a screen
image only, so it gets the transcript and should be updated) and a voice route
don't. `shared` checks the shared value's round trip (`roundTrip`, `keptBoth`: a list
that says only what a model sees doesn't erase a test's answer,
`newerRefused`), and that a Martlet from before video, tools and retired still
reads it (`olderMartletReads`: the routes that say hearing or seeing; the
routes with only the new facts go in `MoreModels`, `keptApart`). Each has `ok`. With `baseUrl` (an `http://` server on this PC
only, for example `http://127.0.0.1:11434/v1` or a llama.cpp server) and
`modelId`, `real.metadata` asks that real server what the model takes
(`routeHearing`, `routeVision`, `routeVideo` and `routeTools` are the decisions it gives), `test: true`
also sends it the real Test hearing request (`real.hearingTest`: the `word`,
`Hears`, `Reply` and `Milliseconds`), a word said by an English Windows voice,
`testVision: true` the real Test vision request (`real.visionTest`: the
`word`, `Sees`, `Reply` and `Milliseconds`), a word drawn on this PC, and
`testTools: true` the real Test tools request (`real.toolsTest`: the `word`,
`Tools`, `Reply`, `Status` and `Milliseconds`); nothing
leaves this PC, no credentials are read and nothing is saved. On Companion ›
Listening, `TalkHearVoiceTestStatus` reads what Test hearing does (and whether
it stays on this PC) or the last result (the model's one-word answer and how
long it took). The `TalkHearVoiceTest` button sends the Thinking model a test
recording (a provider request; a cloud model asks first, with
`ConfirmationYes`/`ConfirmationNo`; Thinking in Ollama on a paired computer
gets it through that computer's paired, pinned gateway), so it needs
`--allow-ui-effects`. The
Image model and Audio model cards' `ImageModelTest` and `AudioModelTest` do the
same for the model that takes pictures and for an audio model of its own
([Image and audio models](#image-and-audio-models)).

On Companion › Thinking's Now card, `ThinkingAbilities` reads what Martlet
knows the Thinking model takes on its route, each with where it came from and
when (*What Martlet knows about lab/sees: hears recordings: no (the server on
this PC's model list, 10 Oct); sees pictures: yes (...); takes video: no
(...); calls tools: yes (found by a test request, 10 Oct).*), hearing and seeing
by its name when nothing was found, the tests that find out what isn't known,
or that its server retired it (*127.0.0.1 retired lab/gone (an HTTP 410 Gone
answer, 10 Oct). Choose another model in Companion › Thinking.*).
`ThinkingToolsTestStatus` reads what Test tools sends or what it last found.
`ThinkingToolsTest` (shown for an OpenAI-compatible endpoint, Ollama on this PC
included) sends the model one request with a made-up tool (a cloud model asks
first), so it needs `--allow-ui-effects`. Test hearing, Test vision and Test
tools keep what they find in `model-abilities.json`; an HTTP 410 answer marks
the model retired on that route (Home shows `HealthIssue-thinking-retired`),
and any answer, or choosing the model again, clears it. A reply or an image or
audio model's job that gets HTTP 410 marks it retired the same way, after the
request ended.
```powershell
# Thinking on the lab's lab/text: video known from its model list, tools not known; Test tools finds out.
.\scripts\Invoke-MartletMcp.ps1 -Desktop -AllowUiEffects -Calls '[
  {"name":"model_lab","arguments":{"action":"start","port":52819}},
  {"name":"ui_click","arguments":{"id":"TourSkip"}},
  {"name":"ui_click","arguments":{"id":"NavCompanion"}},
  {"name":"ui_click","arguments":{"id":"CompanionTab-Thinking"}},
  {"name":"ui_click","arguments":{"id":"Picker-LocalApp-Address"},"waitMs":1500},
  {"name":"ui_set_text","arguments":{"id":"LocalServerAddress","text":"http://127.0.0.1:52819/v1"}},
  {"name":"ui_click","arguments":{"id":"LocalServerFind"},"waitMs":1500},
  {"name":"ui_set_text","arguments":{"id":"LocalServerModel","text":"lab/text"}},
  {"name":"ui_click","arguments":{"id":"LocalServerUse"},"waitMs":2000},
  {"name":"ui_snapshot","arguments":{"idPrefix":"Thinking"},"until":"calls tools: not known"},
  {"name":"ui_click","arguments":{"id":"ThinkingToolsTest"}},
  {"name":"ui_snapshot","arguments":{"idPrefix":"Thinking"},"until":"found by a test request"}]'
```

`local_model_facts` says what an open-weight model needs to run locally
([Resource footprints](../RESOURCE_FOOTPRINTS.md#estimating-a-model-martlet-doesnt-list),
[Model catalog](../MODEL_CATALOG.md#memory-and-running-locally)), through the
production `LocalModelFactsReader` and `LocalModelMemory`. `model` is a Hugging
Face repository (`google/gemma-4-E2B-it`), `hf.co/{repo}:{quant}` or an Ollama
tag (`gemma4:e2b`); or give `huggingFaceRepo` and `ollamaTag` together (the
repository gives the shape and inputs, the tag its exact Ollama download).
`quantization` picks one (default: the Ollama tag's, else Q4_K_M, else another
4-bit one), `contextTokens` the context (default 8,192), and `card` (for
example `"RTX 4070"`) or `bandwidthGbps` the memory bandwidth for the speed.
- `facts`: `HuggingFaceRepo` (the model's own), `GgufRepo` (the GGUF repository
  read: ggml-org, unsloth, lmstudio-community or bartowski first, else the most
  downloaded), `OllamaTag`, `LocallyHostable`, `parametersBillions` (the maker's,
  encoders included), `weightsParametersBillions` (in the weights file),
  `activeParametersBillions` (less for a mixture-of-experts model), `License`,
  `PipelineTag`, `MaxContext`, `Gated`, `inputs` (`Image`, `Audio`, `Video` and
  their `Source`: `config.json`, or the Ollama registry's projector layer, which
  says only that it sees), `architecture` (layers with a cache of their own:
  `FullLayers`, `SlidingLayers`, `LinearLayers`, `SharedCacheLayers`; `KvHeads`,
  `HeadSize`, `SlidingWindow`, `Experts`, `ActiveExperts`,
  `fullAttentionKibPerToken`, `perLayerEmbeddingBillions`), `quantizations`
  (each `Name`, `weightsGb`, `encoderGb`, `draftGb`, `InstallName` and
  `Source`), `encoders` and `drafts` (the `mmproj` and `mtp` files),
  `GgufRepos`, `Sources` (each address read) and `Problems` (what couldn't be
  read, for example a gated `config.json` or no GGUF repository).
- `estimate`: weights + encoder + draft + KV cache at the context + 0.5 GB
  buffers: `totalGb`, `graphicsGb` and `systemMemoryGb` (Gemma's per-layer
  embeddings, which Ollama keeps in system memory), `kvCacheGb` with
  `KvCacheKnown` (false without a `config.json`: a tenth of the weights is
  guessed), `gbReadPerToken` and `described`.
- `speed`: `matched` card, `bandwidthGbps` and `maxTokensPerSecond`, an upper
  bound (bandwidth divided by `gbReadPerToken`); real speed is often 50-70% of it.
- `footprintCatalog`: Martlet's own entry for the Ollama tag (`vramGb`,
  `evidence` Measured or Estimate). `measured`: what Ollama on this PC reported
  once it loaded the model (`model-memory.json` in `dataDirectory`: `Host`,
  `gb`, `graphicsGb`, `OnGraphicsCard`, `ContextTokens`, `measuredAt`).
- `requests`: requests sent to Hugging Face and the Ollama registry. A lookup
  sends at most five; each answer is kept for a day, and at most 100 go to each
  source in five minutes (Hugging Face allows about 500). Martlet reads
  `registry.ollama.ai`, never ollama.com, whose terms forbid automated access.

`fixture: true` runs the same code on a FIXTURE transport shaped like Hugging
Face and the Ollama registry (NOT the real services; the real sizes and config
fields read on 2026-10-10). Without a `model` it checks the estimate against
the measured Gemma 4 E2B, E4B and Qwen3.5 4B numbers (`calibration`, each
`percentOff`, within 10%), Gemma 4's KV cache as Resource footprints works it
out, the GGUF repository picked, the install name, E2B's audio and 26B A4B's
none, 26B A4B's active parameters and the cache (`checks`), and `measured`: the
production `OllamaMeasuredMemory` against a FIXTURE `/api/ps` (NOT Ollama) in a
temporary folder of its own, deleted afterwards (`savedFirst` true,
`savedAgain` false while nothing changed, `graphicsGb`, `ContextTokens`), with
`ok`. Keyless; downloads no model; saves nothing.

`model_catalog_status`, `model_catalog_lookup` and `model_catalog_refresh` show
Martlet's internal model catalog ([Model catalog](../MODEL_CATALOG.md)). All take
an optional absolute `dataDirectory`; the script gives them a disposable one.
- `model_catalog_status`: `using` (`snapshot`, shipped with Martlet, or `daily
  copy`), `built`, `folder` (where the daily copy is: `%ProgramData%\Martlet`
  for the default data folder, else the data folder itself) and
  `sharedByEveryWindowsUser`; `snapshot` and `dailyCopy` (`file` `none`,
  `loaded` or `unreadable`) with each source's read date and item count;
  `refresh` (`LastAttempt`, `LastFinished`, `LastSuccess`, `LastSaved`,
  `Problem`, `due`, `nextDue`, and for each source `Tried`, `Read`, `Items`,
  `Bytes`, `Seconds` and `Problem`); `catalog` counts (models, routes, open
  weights, sees, hears, takes video, calls tools, inputs the sources disagree
  on, ranked, LMArena rated, free and retired routes, routes per provider).
  Read-only.
- `model_catalog_lookup` with `name`: any name of a model (a Hugging Face
  repository, an OpenRouter, NVIDIA Build, models.dev or provider model ID, a
  `:free` variant, an Ollama tag such as `gemma4:26b`, `hf.co/{repo}:{quant}`,
  or an LMArena name). Returns `found`, `how` it was found, and `model`: `Key`,
  `Name`, `names` on each route, `Family`, `Size`, `inputs` (`text`, `image`,
  `audio`, `video`, `videoAsFrames`: `yes`, `no`, `unknown` or `unknown
  (sources disagree)`), `openWeights`, `locallyHostable`, `local` (when the
  catalog looked it up on Hugging Face: `installName`, `quantization`,
  `downloadGb`, `memoryAt8192`, `graphicsGb`, `systemMemoryGb`, the other
  `quantizations` and `Problems`), `facts` (each fact's `value`, `from`,
  `disagree` and every source's `answers` with `source`, `value`, `note` and
  `checked`) and `smartness` (`Words`, `Tier`, `From`, `lmArenaText`,
  `lmArenaVision`, `Credit` "LMArena (CC-BY-4.0)" and `lmArenaName`; never the
  Artificial Analysis index or the rank). `routes` lists each server's
  `Provider`, `ModelId`, `BaseUrl`, `Free`, `FreeNote`, `Context`, `Expires`,
  `Retired`, `Deprecated`, `inputs`, `tools`, `reasoning` and `facts`. A name
  the catalog doesn't know returns `found` false and the quality tier from the
  name. Read-only.
- `model_catalog_refresh` without `live` rehearses the production
  `ModelCatalogRefresh` against FIXTURE sources on 127.0.0.1 (NOT the real
  ones) and the FIXTURE local model facts, in a temporary folder deleted
  afterwards. `report.steps`: `reads-every-source`, `no-analysis-index-kept`,
  `once-a-day`, `gemma-4-26b-video`, `qwen3.5-122b-audio`,
  `gemma-4-31b-provider-split`, `nvidia-route`, `expired-route-retired`,
  `find-by-any-name`, `smartness`, `config-json-decides-by-size`,
  `local-memory`, `keeps-last-good` (OpenRouter answers 500 and vLLM's answer
  is over its size limit) and `waits-while-replying` (no request while a reply
  is held for two seconds), each with `ok` and `detail`. `live: true` reads the
  real public sources (no key) into the data folder's daily copy when a refresh
  is due (`force: true`: now), and returns `Saved`, `Models`, `Routes` and the
  status. With `snapshotPath` (absolute) it writes the release snapshot instead,
  only when every source was read (`snapshotWritten`, `bytes`, `sources`,
  `catalog`); `scripts\Update-ModelCatalog.ps1` runs this.

`local_model_servers` is Companion › Thinking › This PC › *A model app you
already use* without the window ([Local model apps](../LOCAL_MODEL_APPS.md)).
`apps` lists the apps Martlet looks for, each with `Id`, `Name`, `BaseUrl`
(its default address on 127.0.0.1), `OwnedBy` (set when other apps share the
port) and `HowToStart`. `found` is the production `LocalModelServers.DetectAsync`
on this PC's loopback ports now (about 1.5 s): each answering app's `Id`
(`port-8080` when its model list doesn't say which app it is), `Name`,
`baseUrl`, `Models`, `Unusable` (names Martlet can't send), `NeedsKey` and
`HowToStart`. `address` takes what the owner would type (`localhost:1234`,
`8080`, `http://127.0.0.1:5001/v1`) and returns the canonical `baseUrl` with
the app's `name` and `answer` (`kind` `Models`, `NeedsKey`, `NotAModelServer`
or `NoAnswer`, with `Models`, `OwnedBy` and `Problem`), or `problem` for an
address that isn't on this PC. `test: true` sends the production Test model
request to `model` at `address` (or to the first found app other than Ollama):
streamed, the default reply length and Thinking steps, one tool offered and
asked again without it when the app refuses tools. It returns `passed`,
`Summary`, `Warning`, `ToolsRejected`, `firstWordsMs`, `totalMs` and the run
window's `output`, or `error`. It runs the real model on this PC. `fixture:
true` rehearses the same code against fixture servers on 127.0.0.1 (canned
words, NOT AI): llama.cpp without `--jinja` (`llamaCpp`: its model list says
`llamacpp`, the request with a tool is refused, the test asks again without it
and warns) and an app that asks for a key (`keyed`: `NeedsKey` without the key,
its models with it), plus an address off this PC (`offComputer`, refused).
`fixture.ok` needs all of them. Loopback only; never a key; saves nothing.

`ollama_model_file` is Companion › Thinking › This PC › *Use a model file you
downloaded* without the window. `paths` (absolute paths of GGUF files the owner
would pick) returns `plan`: the `files` Martlet would add to this PC's Ollama
(every part of a split model `name-00001-of-0000N.gguf`, and `addedProjector`,
a lone `mmproj*.gguf` in the same folder that lets the model see), the Ollama
`model` name made from the file name (*Qwen3-8B-Q4_K_M.gguf* is
`qwen3-8b-q4_k_m`) and the `bytes` Ollama would copy, or `refused` (not a GGUF,
only a projector, a missing part). It reads only the folder listing and file
sizes and contacts nothing. `fixture: true` runs the production import
(`OllamaModelImport`: the SHA-256 of each file, `HEAD` and `POST
/api/blobs/sha256:...`, then `POST /api/create` with the files) on FIXTURE
files (small text, NOT real models) against a fixture Ollama on 127.0.0.1 that
checks each digest and already has one part: `import` (`blobsSent` 2,
`blobsChecked`, `createdModel`, `createdFiles`, `output`), `ollamaRefuses`
(Ollama's own words when it refuses a model) and `refusals`. `fixture.ok` needs
all of them. Never touches the real Ollama; saves nothing.

In the desktop, `SetupAddModelFile` (*Use a model file you downloaded...*, on
the *Ollama on this PC* card; `SetupAddModelFileNote` reads what it does)
opens a file dialog, asks `AddModelFileQuestion` (the files, the model name and
the space Ollama's copy takes), adds the files in a run window titled *Add
<model> to Ollama*, then switches Thinking to the model like
`SetupUseLocalThinking` and tests it. It needs `--allow-ui-effects`.

On the Thinking page, *Model app* is an option picker without a details panel:
`Picker-LocalApp-Ollama`, one row per app found on this PC
(`Picker-LocalApp-lm-studio-1234`: the app's id and port) and
`Picker-LocalApp-Address` (an app typed by its address). Each row's
`PickerFacts-LocalApp-<key>` reads its short facts (*GPU · Martlet sets it up ·
installed*, *2 models · you set it up*); choosing one only shows that app's
card. `LocalServersStatus` reads what looking found (*Found on this PC:
LM Studio at http://127.0.0.1:1234/v1 (3 models).*, or *No other model app
answers on this PC...*) and the model Thinking uses there. `LocalServerModel` the model,
`LocalServerModels` what the app lists, `LocalServerKeyStatus` what the key box
will do (never the key), `LocalServerHint` how to start an app typed by address,
`LocalServerPrivacy` where messages go, and `LocalServerTestResult` the last Test model result. `Help-LocalServerAbout`
(the card's *?*) links to the wiki's *Use a model app you already run*.
`LocalServersScan` (*Look again*) and `LocalServerFind` (*Find models* for the
typed address) only ask this PC's loopback, so they are safe clicks.
`LocalServerTest` (a real request to the model) and `LocalServerUse` (switches
Thinking; a model the app doesn't list first asks `LocalServerModelQuestion`)
need `--allow-ui-effects`. Ollama's models are an option picker too
(`Picker-OllamaModel-gemma4:e2b` and the other suggestions, each model Ollama
already has, and `Picker-OllamaModel-Other`). A row's facts lead with whether it
fits this PC's graphics card (*tight here*, *too big here*), then its graphics
memory, how soon it answers and whether it hears (*3.3 GB VRAM · 0.15 s to
first sentence · hears*); the shown model's details add whether it calls tools,
its context, *On this PC* (downloaded or not) and Ollama's buttons for that
model. Under *Another Ollama model*, `SetupLocalModel` is the model typed and
`SetupLocalOwnModels` says that any Ollama model works. When the app Thinking uses stops answering, Home shows
*<app> isn't ready on this PC* (`HealthIssue-local-model-app`, and `StageText`
when it is the top problem). On
Martlet.Companion, `LocalModelsFound` reads what *Find model apps*
(`FindLocalModels`, which fills the unsaved Thinking fields, so it needs
`--allow-ui-effects`) found.

`ollama_recovery` says why Ollama on this PC doesn't answer, the way Home and
Companion › Thinking find it (the production `OllamaRecovery` diagnosis). It
reads only: whether Ollama answers on 127.0.0.1:11434, whether its tray app
(`ollama app.exe`) and server run, the end of its `server.log` and `app.log`
(`log`: `Starts`, `FailedStarts`, `LastStartFailed`, `ListeningVersion`, the
`modelsPath` of the last start, the `lastError` line and the app's
`appReason`), the model location in its app database (`app`: `db.sqlite`
`settings.models`, read through Windows' own SQLite, with `KnownShape`), the
user's `OLLAMA_MODELS` and each models folder (`modelsFolders`: `Linked`,
`linkPath`, `linkTarget`, `realPath`, `RealPathUsable`). `thisPc.kind` is
`Answering`, `NotInstalled`, `NotRunning`, `StopsOnStart` (the last start
stopped with an error) or `CrashLoop` (it keeps stopping: the log changed in
the last 10 minutes and the tray app runs or two starts failed). `knownCause`
means Ollama refuses its models folder because the folder is reached through a
junction or symbolic link (Ollama 0.40.1 and later: *mkdir ...: ensure path
elements are traversable*, and the app's *The path cannot be traversed because
it contains an untrusted mount point*). `canRepair` means Martlet can point
Ollama at the real folder. `message` and `guidance` are what Home shows. Paths
under the user's folders read as `%LOCALAPPDATA%` and `%USERPROFILE%`. It never
stops, starts or changes Ollama, its folders, its settings or `OLLAMA_MODELS`.
`fixture: true` also runs the production diagnosis and repair on FIXTURE
folders in the temporary folder (`OllamaRecoveryFixture`: a junction made with
`mklink /J`, a `db.sqlite` made with Windows' SQLite, Ollama's own log lines
and a stand-in for its processes), and deletes them afterwards. The scenarios
are `Linked` (repaired: `events` is `stop`, `set OLLAMA_MODELS=...`,
`start OLLAMA_MODELS=...`; `modelsWhenStopped` is still the junction, so the
database changed only after Ollama stopped; `savedModels` is the real folder;
`repair.backup` holds the old database), `UnknownSettings` (no `models` column:
guidance, `databaseUnchanged`), `OtherError` (Ollama's own words, nothing
changed) and `Answering` (nothing stopped). `fixture.passed` needs all of them.

When Thinking uses Ollama on this PC and it doesn't answer, Home's
`HealthIssue-ollama` reads Ollama's own error when its logs have one: *Problem:
Ollama keeps stopping on this PC. Thinking uses fixture-model:1b on this PC,
but Ollama keeps stopping (5 failed starts in its log). Ollama says: "..."*. For the known cause,
Martlet points Ollama at the real folder by itself, once for each cause in a
run: it stops Ollama's tray app and servers, backs up `db.sqlite` into
`%LOCALAPPDATA%\Ollama\martlet-backup-<time>`, sets `settings.models` (only when
it is empty or the linked folder) and the user's `OLLAMA_MODELS` (only when it
isn't set or is the linked folder) to the real folder, starts Ollama and checks
that it answers. Then `HealthIssue-ollama-fixed` reads *Good to know: Martlet
fixed Ollama on this PC — ...* with the backup folder, and the local log has
each change. While it works, the item says so and `HealthFix-ollama-repair` is
disabled (*Fixing Ollama...*). Otherwise `HealthFix-ollama-repair` (*Fix
Ollama's models folder*, or *Try the fix again*) runs it again and
`HealthFix-ollama-ollama-logs` opens Ollama's log folder; both need
`--allow-ui-effects`. Companion › Thinking's *Check Ollama* (`SetupCheckOllama`),
*Use Ollama on this PC* and the Test, Load and Download run windows say the same
and repair the same way, and `SetupOllamaStatus` reads *Ollama is installed,
but it stops when it starts. Ollama says: ...*. To exercise this without the
real Ollama, set `MARTLET_SIMULATE_OLLAMA_CRASH_LOOP` to `linked`,
`unknown-settings` or `other-error` before launching the desktop (FIXTURE,
`SimulatedOllamaCrashLoop` in `LocalOllamaRecovery.cs`). Martlet then uses an
`OllamaRecoveryFixture` in the data directory's `ollama-fixture` folder for
Ollama's logs, app database, models folder, processes and `OLLAMA_MODELS`,
and the local log says *FIXTURE (MARTLET_SIMULATE_OLLAMA_CRASH_LOOP=...)*.
Test, Load and Download stop with a FIXTURE message, so no request goes to the
real Ollama. Seed the data directory's `settings.json` with Thinking on
Ollama on this PC (`fixture-model:1b`, the model the fixture lists once it
answers) to see Home's item.

`vision_history_check` rehearses how what Martlet sees is kept in the
conversation ([Screen commentary](../SCREEN_COMMENTARY.md#what-martlet-saw-stays-in-the-conversation))
with the desktop's production code: `prompts.seen` is Companion › Prompts ›
*What you saw* as the data directory's settings.json sends it (optional
absolute `dataDirectory`, default the current user's), `tag` the seen tag
(`[seen:…]`, any words on one line) and `markers` the line markers
(`[Screen]`, `[Camera]`). Each sample look reply (or `reply`, up to 1,024
characters of one line) goes through the production speech segmenter and chat
stripper with the seen and chattiness tags a look is offered (`spoken`,
`shown`, `passed`, `tags`, `seen`: the description kept, `tagHidden`) and is
kept in a production conversation buffer as the desktop keeps a look
(`replacedPassedLook` when it took the place of the passed look before it),
then a typed message that came with a picture. `history` is the conversation
as the next reply sends it (`role`, `text`, `vision`, and `memoryReads`: what
memory and learning names may read of a user line, null for a look). A running
conversation's looks show in `logs_tail` `contains` `Vision:` as *Vision: the
conversation keeps a screen glance (passed, described, in place of the passed
look before it).* It reads no credentials and contacts nothing.

`active_app_check` reads the [program in
front](../SCREEN_COMMENTARY.md#the-program-in-front) the way Martlet's screen
glances do, with the desktop's production `ActiveApp`: `inFront` is the window
in front right now (`found`, `app`, its name such as *Visual Studio Code*,
`fullScreen`, `told`, the words a look sends, such as *Google Chrome (full
screen)*, and `readMs`, how long reading it took the `first` time and `again`
with the name already known, as each capture every 3 seconds reads it; never
the window's title), `sample` is the name `explorer.exe` gets
(*Windows Explorer*), and `rules` is the full-screen rule on FIXTURE windows
(`window`, `maximized`, `titleBar`, `fullScreen`): a borderless full-screen
game or video and a maximized borderless window fill their monitor, a
maximized window with a title bar never does (also over a taskbar that hides
itself), a window smaller than its monitor doesn't, and a borderless window
filling the second monitor does. `prompts` shows, for a FIXTURE full-screen
game, Companion › Prompts › *Screen glance message* (`glance`) and *Active app
with your message* (`withMessage`) as the data directory's settings.json fills
them (`state` `none`, `loaded` or `unreadable`; optional absolute
`dataDirectory`, a disposable one through `scripts\Invoke-MartletMcp.ps1`), and
the `[Screen]` line the conversation keeps (`kept`). `sinceLook` runs the
desktop's production `LookChange` on FIXTURE thumbnails of that game: each
`scenario` gets `since` (`First`, `Same`, `Little` or `Changed`), `looks`
(false while nothing changed since the last look: Martlet doesn't look at
all), `picture` (how much the thumbnail changed, 0 to 1) and `changed`, the
words a look's message gets (*It changed since your last look 3 min ago.
Nobody has used the keyboard or mouse for 6 min.*). A clock that ticks counts
as `Same`; a pop-up or a new window title is `Little`; new words on the
screen, a new scene or another program is `Changed`. In a running
conversation, the talk window's `LiveVisionStatus` shows *Nothing changed
since the last look.* while it holds off, and its `help` shows *Active
app: Google Chrome (full screen).* It reads no credentials and contacts
nothing.

`screen_digest_check` runs the [screen summary over
time](../SCREEN_COMMENTARY.md#martlet-knows-what-changed-over-time) once with the
desktop's production `ScreenDigester` on **FIXTURE** frames (made-up pictures of
a code editor, then a game whose health drops; no screen capture) and a FIXTURE
thinker and context board (no model; nothing is sent). `setting` is Companion ›
Vision › *Screen summary over time* from the data directory's
talk-preferences.json (on by default; optional absolute `dataDirectory`, a
disposable one through `scripts\Invoke-MartletMcp.ps1`), `timing` the window,
spacing, fresh, job time (`jobSeconds`: how long one summary may wait for a
model and run), stale and board ages and `sheetFrames` (the most screenshots on
one contact sheet: 6, in a 3 x 2 grid), `observed` which frames the ring kept or
skipped, `job` the reason, frames (6 for the fixture game), contact sheet size
and bytes and the message
as Companion › Prompts › *Screen summary over time* makes it, `answer` the
FIXTURE reply (or `reply`, up to 1,024 characters) and `parsed` what Martlet
keeps of it, `board` the note posted as source `screen` with its maximum age,
`status` the frames, last summary, its age, time taken and job counts, `line`
the talk window's `LiveScreenSummary` line, and `staleAnswer` that an answer
that comes after the stale limit is dropped. It reads no credentials and
contacts nothing.

`context_board` rehearses the [context board](../CONVERSATION.md#context-board)
with the production board, request layout and Chat Completions adapter
against a fixture endpoint on 127.0.0.1 (canned reply, NOT AI). It posts
FIXTURE notes from four sources (`screen`, `character`, a stale `sound` note
and a `touch` note that is consumed on read), plus an optional test note of
your own: `source` (1-32 lower-case letters, digits or `-`), `text`,
`maxAgeSeconds` (1-3600, default 60), `ageSeconds` (0-7200, how long ago it
was posted) and `consume`. It sends two requests in a row and returns
`firstRequest` and `secondRequest` (`notes`: the sources each snapshot took,
`bytes`, `carried`: the fixture sources found in the sent message,
`outcome`), `staleSkipped`, `consumedOnce`, `orderStable`, `boardAt` (*end
of the user's message, after Martlet's other notes*),
`historyKeepsBoardNotes` (false), `keptMessageIsStartOfSent`,
`messagesSentAgainUnchanged`, `lastSent`, `posted` (your note: `inFirstRequest`,
`inSecondRequest`) and `limits` (`noteBytes` 600, `totalBytes` 2048,
`sources` 16, `maxAgeSeconds` 3600); `ok` is true when all of them hold.
Live replies and looks log *Context board: the request took N notes (sources;
bytes; consumed)* in the desktop log (`logs_tail`), and `LiveTurnInputs`
counts them (*... and 2 context notes*). Loopback only; no credentials.

`game_progress_check` rehearses [where you are in a
game](../CONVERSATION.md#where-you-are-in-a-game) with the production note keeper
(`GameProgress`) and context board and a FIXTURE Thinking pool member (canned
answers, NOT AI; nothing is sent). Two FIXTURE screen summaries make a note
and a newer note built on the first, a `[same]` answer keeps the note, and a
bad answer leaves the board as it was. It returns `updates` (each step's
`Updated`, `Kept`, `Characters` and `Problem`), `secondBuiltOnFirst`,
`instructionsStayTheSame`, `board` (`sources`: `screen`, then `game`; `game`:
the note's text and `maxAgeSeconds` 600; `gameAfterScreen`), `counts` and
`limits` (`everySeconds` 90, `noteAgeSeconds` 600, `earlierNoteHours` 6,
`maximumCharacters` 480, `maximumGames` 8, `maxOutputTokens` 200); `ok` is
true when all of them hold. An optional `reply` is cleaned like a pool
member's answer and shown as `reply.cleaned` and its `board` line. Live
updates log *Game progress: updated the note in N s (N characters).* in the
desktop log (`logs_tail` `contains` `Game progress`). It reads no credentials
and contacts nothing.

`context_check` shows the Thinking model's [context](../CONVERSATION.md) as
replies use it (optional absolute `dataDirectory`, default the current user's):
`settings` (`none`, `loaded` or `unreadable`), `thinking` (`routeType`,
`localOllama`, `inNetwork`, `replyRequestSeconds`, `model`; Thinking on this
PC, a paired host or a Chat Completions server at a private or local address is
`inNetwork` and gets 120 s per reply request and the whole action, a cloud
route 45 s), `savedContextTokens` (Companion › Replies › Context
size, null when blank), `modelLimit` (what `model-limits.json` says about the
model: `ContextTokens`, `ModelMaximum`, `Source`, `checkedAt`) and
`modelLimitsKept`, and `context` (the production `ContextBudget`: `Tokens`,
`source` such as `Saved`, `Default`, `ModelLimit`, `HostDefault`, `Planned`, `Ollama` or
`OllamaAssumed`, `ReplyTokens`, `InputTokens`, `ModelTokens` and `described`,
the words the Replies page shows), and `look` (how much room a screen glance
or camera look has: `personaTokens` and `pictureTokens` estimated as the
desktop estimates them, `textTokens`, `maxTextBytes` on a paired host (1 MiB; an
older host takes 16 KiB),
`roomForTheRest` for Martlet's instructions, the glance message and its notes,
`leavesOut` (what a look goes without when it doesn't fit, in order) and a
`problem` when the picture and the persona alone don't fit, so vision stops;
see [When a look is too long for Thinking](../SCREEN_COMMENTARY.md#when-a-look-is-too-long-for-thinking)).
Its `hostWindow` shows a paired host's conversation window
([ContextWindowPlan](../RESOURCE_FOOTPRINTS.md)): `saved` (for a Thinking route
on a host: `plannedTokens` and `planned`, the window planned from its graphics
card in `model-limits.json`; `loadedTokens`, the window its Ollama has loaded
now from `model-memory.json`; `windowTokens`, `inputTokensPerReply` and
`source`), `before` (the old 8,192-token window: `inputTokensPerReply` 6,144,
`ReplyTokens` 4,096, `exchangesSent`, `estimatedTokens` and
`textAndReplyFit` false, because 6,144 + 4,096 is more than 8,192) and
`cards`, the production plan and budget on fixture cards (NOT real hardware):
Gemma 4 12B on an RTX 3090 (32,768 tokens), on an RTX 3080 (8,192) and Gemma 4
E4B beside a 4 GB voice on an RTX 3060 (32,768), each with `Why`, `extraGb`,
`textAndReplyFit` and the same `inputTokensPerReply`, `exchangesSent` and
`estimatedTokens` as `before`, so a bigger window never makes a reply send more.
Its `probe` rehearses the production model
limit check against fixture servers on 127.0.0.1 shaped like OpenRouter (the
smaller of `context_length` and the top provider's), vLLM, Groq and llama.cpp
model lists, an unlisted model, a redirect (never followed; the fixture key
goes only to its own base URL) and Ollama's `/api/show`, `/api/ps` and
`/api/generate` (loaded, not loaded, loaded by the check, missing). Its `fit`
fits a 1,000-exchange synthetic conversation the way a reply does into a cloud
model's default 100,000 tokens, 1,000,000 tokens on gpt-4.1, a paired host at
its default 8,192 tokens and at its largest 262,144 (1 MiB and 4,096 messages,
like the other routes) and Ollama on this PC at 32,768:
`exchangesSent`, `exchangesLeftOut`, `estimatedTokens` and `bytes`, `ok` when
the newest exchanges fit and one more wouldn't. Its `cache` part rehearses the
[request layout that lets prompt caches work](../CONVERSATION.md#prompt-caching-and-the-request-layout)
with the production Chat Completions adapter against a fixture endpoint on
127.0.0.1 (canned reply and usage, NOT AI): two replies in a row whose notes
differ send the instructions and earlier messages again unchanged
(`messagesSentAgainUnchanged` of `messagesBeforeTheMessage`, `stableStart`),
the notes close the user's message (`notesAt`, `notesInInstructions` false),
the usage chunk's cached tokens are read (`usage`), only Ollama on this PC is
asked for usage (`asksUsageFromOllamaOnThisPc`, `asksUsageFromOtherServers`),
and `trimming` replays 60 replies of a synthetic conversation into Ollama's
smallest context: how often the request's start moved letting go of the
oldest quarter at once (`startMovedLettingGoAQuarter`, 7) against one exchange
at a time (`startMovedOneAtATime`, 37). Each part has an `ok`. It
reads no credentials and nothing leaves loopback. A real model's cache use shows in
the desktop log's *Thinking input (Reply): first words after … ms; N input
tokens, M of them (P %) from the model's prompt cache.* lines
(`{"name":"logs_tail","arguments":{"contains":"Thinking input"}}`, also for
glances and *Remembering*/*Learning names*) and in the `help` (tooltip) of the talk
window's `LiveContext`. On Companion › Replies,
`RepliesContextStatus` reads the size in use and where it comes from and what
Martlet knows of the model's own limit (for a paired host: the window planned
from its graphics card, the window its Ollama has loaded now and how much each
reply sends); `RepliesMaxReplyTokensRange` and
`RepliesContextTokensRange` read each setting's allowed range and blank value as
the page shows them for the Thinking route (*16-32768 tokens, blank = 4096*, and
*2048-262144 tokens on a host, blank = the window planned from its graphics card
(8,192-32,768), each reply sending about 6,144* for a paired host); `RepliesCheckContext` (*Check model
limit*) asks the Thinking model's server (loading the model in Ollama on this
PC first), so it needs `--allow-ui-effects`. Choosing a Chat Completions model
on Companion › Thinking checks it the same way, and *Test model* and the talk
window's model loading record what Ollama on this PC gives the model; the
desktop log says *Context of gemma4:12b: 16,384 tokens (Ollama on this PC).* or
*Ollama on this PC gives gemma4:12b 16,384 tokens of context; the next
conversation uses it.*

`thinking_steps_check` shows Companion › Replies › **Thinking steps** (whether a
reasoning model thinks before it answers) as replies use it (optional absolute
`dataDirectory`, default the current user's; optional `model` and `live`):
`settings`, `thinkingSteps` (`Off` or `On`: what replies use; Off unless On is
chosen), `chosen` (whether a choice is saved), `thinking` (the
Thinking route's `routeType`, `localOllama`, `model`, its `control` and `use`
and `sends`, exactly what its replies add for that choice), and `routes`,
what Off and On send on every kind of route: `reasoning_effort` `none`/`medium`
for Ollama on this PC (Used), OpenAI and Gemini; OpenRouter's `reasoning`
(`{"effort":"none"}` or `{"enabled":true}`, Used); `chat_template_kwargs`
`enable_thinking` and `thinking` for NVIDIA Build and other servers (vLLM,
SGLang, llama.cpp; ServerDependent); Ollama's `think` on a paired host (Used,
carried by the gateway); nothing on the OpenAI route (Unused). Its `fixture`
runs the production Chat Completions adapter against an endpoint on 127.0.0.1
(canned reply, NOT AI) for Off, On and the model's own default (what a reply is
asked again with after a model refused the choice: no field), `ok` when each
request carries exactly the expected fields. With `live: true`, `live` asks Ollama on this PC (the
saved local Thinking model, or `model`) a fixed question, never anything the
owner said, with the model's default and with Off: the production adapter's
`reply` (`Outcome`, `FirstWordsMs`, `TotalMs`, `text`) and one `plain` request
each (`ReasoningCharacters`, `CompletionTokens`), `thinksByDefault`, and `ok`
when Off completed with no thinking. On this PC with `gemma4:12b`, Off answered
with first words after about 0.1 s and 16 tokens against about 1-3 s and
260-300 tokens (600 characters of thinking) by default. It reads no credentials
and nothing leaves loopback. A paired host's `think` (desktop client, gateway,
Ollama relay) is checked by `OllamaRelayTests`; real cloud providers are NOT RUN.

`helper_jobs_status` (optional absolute `dataDirectory`, default the current
user's) reads the desktop's `helper-jobs.json`: for each helper job kind
(`memory`: remembering after a reply; `speaker-naming`: learning the names of
the people speaking after a reply, a request of its own; `action-naming`:
naming a character's emotes; `temperament`: deciding its touch temperament;
`touch-zones`: finding its touch zones; `eyes`: measuring its eyes;
`character-writing`: writing a character's personality in *Find a character*;
`memory-upkeep`: tidying memory; `memory-brief`: writing the
[memory brief](../MEMORY.md#the-memory-brief)) the last
`route` (`pool` with the `member` that ran it and its model, or `fallback`: the
conversation's own Thinking model after the reply finished speaking),
`priority`, `outcome` (`answered`, `no answer` or `failed: <why>`), `at` and
`waitedMs` (how long the fallback waited for the reply). It never holds a prompt
or an answer. `state` is `none` until the desktop runs a helper job with that
data directory. These keys use the same format as the Thinking pool's job
kinds (lower-case words with hyphens); a `helper-jobs.json` written by an
older Martlet has underscores (`action_naming`) until the next helper job
writes it again. The jobs go to the [Thinking pool](../CONVERSATION.md#the-thinking-pool) as its
Memory, Naming and TouchZones kinds (learning names and emote naming as Naming
jobs, the eyes as a TouchZones job at the helpers'
low priority, the memory brief as a Memory job at the lowest priority;
`ThinkingJobKinds.Rows` says which helper job runs as which kind);
`thinking_pool_status` shows the pool itself.
`helper_jobs_check` (no arguments) rehearses the desktop's production router
(`HelperJobs`) with a fixture pool and fixture answers (NOT AI): memory and
naming go to a free text member, touch zones wait for a running reply and fall
back while no member can see, then go to a vision member, tidying memory
(`memory-upkeep`) runs on a free text member and, when none is free, waits
instead of falling back, and memory falls back
when no member is free. It returns each step with `passed` and the status file
it wrote. See [helper jobs](../MEMORY.md#helper-jobs-on-the-thinking-pool).

`think_longer_status` shows Companion › **Deep thinking** as replies use it(optional absolute `dataDirectory`, default the current user's): `settings`,
`thinkLonger` (`enabled`, on by default and turned off by Thinking longer's own
`ThinkLongerOn` box; `effort` *Medium* or *High*; `timeLimit` and `hourlyLimit` *none*;
`delivery` *WhenFree* or *NextMessage*; `chosen`; `webResearch`, Companion ›
Deep thinking › *Web research*, on by default, and `researches`, whether it
applies with Thinking longer), `thinking` (the Thinking
route's `routeType`, `model`, `supportsTools`, `toolsRejected` from
`tools-unsupported.json`, `offered` (only where Deep thinking can run),
`researchOffered` (whether replies get `research`), `onThisPc`), `deepThinking` (this PC's `deep-thinking.json`: `file` *none*,
*loaded* or *unreadable*, `place` *SameAsThinking*, *Endpoint* or *Host*, `where`,
`model`, `hostId`, `hostRoute` (a paired computer's route a think goes to:
`martlet.gateway.deep-thinking-chat.v1` for its Deep thinking role, else its
Ollama role's) and `hostRole` (true for the Deep thinking role), an endpoint's `origin`, `ownKey` and `usesThinkingKey`
(never a key), `available` (and `parallel`, the same: a think always runs
alongside the conversation), `checksFit` (a second model in Ollama on this PC,
checked to fit beside Thinking's before each think) and `why` from the
production `DeepThinkingPlan`, its Thinking steps `use`, what a think `sends` at
that effort, such as `{"reasoning_effort":"medium"}` or `{"think":true}`,
`outputTokens` and `carriesTools`, true only with the Thinking model, and
`pool`: every place it thinks on, the first place then each computer ticked
*Think here too*, each with `computer` (its name), `where`, `place`,
`hostRole`, `available`, `rank` (lower goes first), `checksFit` and `why`, then
`usable`, `maxThinks` (slots in all), `atOnce` (how many thinks run at once:
one fewer than the slots when there are two or more, because the last free
slot stays free for quick jobs) and the pool's `available` and
`why`), `tools`
(`think_longer` and `cancel_thinking`, and `research` while web research is on,
exactly as the model gets them), the filled `prompt` and `researchPrompt`, and
`jobs`: the desktop's `background-jobs.json` (`active` and
`recent` jobs with `id`, `kind`, `state`, `progress`, `startedAt`,
`finishedAt`, `elapsedSeconds`, `timeLimitSeconds`, `offer`,
`resultCharacters`, `cut`, `problem`, `canceledBy`, `delivery` and `place`, the
computer it runs on; `startedLastHour`; `thinks`, each running think's `id`,
`where`, `computer`, `available`, `checksFit`, `why`, `rank`, `parallel` and
`attempts` (`thinking` is the first of them); `places`, each Deep thinking place
with `computer`, `where`, `available`, `rank`, `slots` and `heldBy` (the job IDs holding
it now); `maxThinks` (slots in all); each job's `inLine` (its place in the line for a
free computer, 0 when it isn't waiting), the broker's `line` (who waits, first
in line first) and `keptFor` (the other work each computer is kept free for,
such as `singing`)),
never a task or result. Read-only.

`said_lately_check` rehearses [what Martlet said
lately](../CONVERSATION.md#what-you-said-lately) with the production code
(`SaidLately`, the prompt, `MomentTurn`, `BoundedTextInput`, `CheckIns`) and
FIXTURE sayings at fixed times (NOT anything Martlet said): `what is noted`
(never a `[pass]` or nothing; one line, cut to 160 characters; the newest 10
within the hour), `lines with when` (`- 10:05 PM (12 min ago): "..."`),
`the note` (Companion › Prompts › *What you said lately*, your own edit of it,
and nothing when it is emptied or nothing was said), `which requests carry it`
(`Look`, `Report` and `PcAudio` do; `User` and `Touch` never, so a reply to you
starts as fast as before), `sent once, never kept` (the last notes of the
message, left out of what the conversation keeps, and the talk window's line
*the picture and 5 things Martlet said lately*) and `the check-in reads the
same lines` (Saying the same things, and its wait with too little said).
`passed` and each step's `passed` and `detail`. No model, network or
credentials. Live looks and replies count it in the desktop log's *Turn took*
line and the talk window's `LiveTurnInputs`, never what was said.

`think_longer_check` rehearses Thinking longer with the production scheduler
(`BackgroundJobs`), think runner (`BackgroundThink`), tool texts and request
layout (`ThinkLonger`), conversation runtime and Chat Completions adapter
against a fixture endpoint on 127.0.0.1 (canned replies, NOT AI; optional
`reasoningMs` 200-3000, default 1200, for the fixture's hidden reasoning).
`flow`: a reply says it'll think it over and calls `think_longer`; the tool
returns within a few ms (`toolTookMs`), after the first words, and the reply
completes with nothing more to say; the background request repeats the reply's
messages and tools unchanged before what Martlet said and the task
(`layout.sameStart`, `sameTools`, `then`), with Thinking steps on and the
Medium budget against the reply's off and 4,096 (`replyThinking`,
`thinkThinking`, `replyBudget`, `thinkBudget`); the result is brought up as
Martlet's own reply whose message carries it and whose request starts like the
reply's (`delivery.report`), after which nothing waits, and the notes for the
next message carry it too. `limits`: one think at a time (the second is refused
with what the model is told) beside a song job, the user's Cancel (mentioned
only with the next message, kept when that reply didn't happen), the time limit
(`TimedOut`), the hourly limit, Martlet's own cancel (nothing to bring up) and
the conversation ending (dropped), all on a fixture kind with limits;
`deepThinkingUnlimited`: Deep thinking's own kind has no time or hourly limit
(`timeLimit`/`hourlyLimit` *none*, 20 thinks `startedInARow`, one still running
past the fixture's time limit, `requestTimeHours` 24); and `fullLine`: on three
one-slot places, Deep thinking's own kind lets six thinks run or wait
(`runningOrWaiting`, `maxActive`) and runs two at once (`atOnce`), and a seventh
is refused as `busy` with those real numbers (`message`: *Martlet already has 6
thinks running or waiting, and it runs up to 2 at once...*, and `toldModel`);
`startFailure`: a job waiting in line whose seating throws (a clock that fails,
as at shutdown) ends `Failed` (*it couldn't start on this PC*) and is
`reported` through `BackgroundJobs.Faulted` (the desktop log's warning), so
its kind is never blocked; and `waitingTrimmed`: finished jobs that wait to be
brought up stay at most `limit` (`BackgroundJobs.KeptWaiting`), the oldest
dropped. `plans`: the production `DeepThinkingPlan`
for thirteen setups (Same as Thinking with Thinking on this PC, on OpenRouter or
on a paired computer; OpenRouter with Thinking local; Ollama on this PC with
another model or Thinking's own beside Thinking local, or with the voice on
another computer or in this PC's host service; a paired computer that does
nothing else, also speaks, or also does Thinking; that computer's Deep thinking
role beside its Thinking, or with Thinking local), each `available` against
`expected` with `checksFit` and its `why`.
`parallel`: a think on a destination of its own (a second fixture endpoint
stands in for the other machine) keeps working while three replies go to the
conversation's endpoint (`replyFirstWordsMs`), is never stopped (`attempts` 1)
and its request has no tools, Thinking steps on and the conversation then the
task. `sideBySide`: the production `OllamaSideBySide` reads a fixture Ollama's
`/api/ps` and `/api/tags` (`readFromOllama`, `loaded`, `downloads`) and decides
whether a second model fits beside Thinking's (`decisions`, each `fits` against
`expected` with `needGb`, `roomGb` and `why`: a large model on a 24 GB and a
12 GB card, a small one on a 12 GB card free or filled by a game, only
Windows' total known, graphics memory unknown, Thinking's own model, a model
not downloaded, both already loaded, Thinking's already partly on the
processor), and once the think's model has loaded
whether Thinking's was unloaded or pushed partly off the card (`afterLoading`,
each `stops` against `expected`). `hostFit`: a 160-message conversation
fitted to a paired computer's gateway (16 KiB, 16 messages, no tools, the newest
kept, `inputTokens` 24,576 beside 8,192 for output). `pool`: the production
`DeepThinkingPool` of three paired computers' Deep thinking roles (diva and
ripley do none of the conversation's jobs, imouto also speaks: `configured`
with each `rank`), `maxThinks` 3 (slots), `atOnce` 2, the tool's *Up to 2 at
once; more wait in line* (`tool`) and the pool's *Up to 2 thinks run at once...*
(`plan`); the
production job list starts thinks as the desktop does (waiting in line when no
place may take them), places think-1 on diva and think-2 on ripley, both
working at once on their own fixture endpoints (standing in for the two
computers, each through a runtime of its own: `thinkingAtOnce`, `overlapped`).
A long job never takes the pool's last free slot while the pool has two or more
slots, so think-3 waits in line behind them (`waitedInLine`, with its
`progress`) and imouto's slot stays free (`heldWhileBusy`); a quick job (a
screen summary through the production `ThinkingJobBoard` on the same broker, a
simulated member, NOT a model) takes that slot at once while think-3 still
waits (`quickJob`: `on` imouto, `tookMs`, `held`). Think-3 then runs on the
first of diva and ripley to free up, never on imouto (`placed`,
`waitedInLine.startedOn`); every place is free once they finish (`freedAfter`)
and the next think goes to diva again (`nextPlacedOn`). `moment`: the production
`MomentTurn` plan for eight situations (`plans.cases`: a look that comes due
while the PC played and work finished, while only work finished, or alone;
finished work that comes up while the PC played or a look is due; the PC's pace
coming up while work finished or while Esc held it; you talking while all of it
waits), each `route` (*Reply*, *Report* or *Glance*) with what it takes along
(`takesPcAudio`, `takesFinishedWork`, `takesTheLook`), and `combinedTurn`: the
owner's example (a song and a report finish while the game plays and a look is
due) sent as one reply whose message `carries` the PC's marked lines and both
results in its notes (the song marked to offer), after which both jobs are
delivered (`newsAfter` false), and whose instructions start exactly like a
plain reply's up to the end of the One moment instruction
(`sameStartAsAPlainReply`, `sharedStartCharacters`; `momentInstruction` is the
text). `broker`: the production
background broker (`BackgroundPlaces`) and scheduler place four thinks on a
companion PC's three hosts, one general, one kept for image generation and one
that sings (`places` with `Rank`, `Slots`, `Duties` and `Standing`): the
general one first, then the singing one; the images one is the pool's last free
slot, which a long job never takes while the pool has two or more slots, so the
third and fourth wait in line (`placed`, `queuedBehind`, `inLine`) and a quick
job (an end-of-turn judge) takes that slot at once (`quickJob`). A song holding
the singing computer keeps both waiting when that computer's think ends
(`waitedWhileTheSongHeldTheSinger`); the third runs on the general one once
that frees up (`nextInLineRanOn`), the fourth waits until two slots are free
again (`lastInLineWaitedForTwoFreeSlots`) and runs on the singing one when the
song ends (`lastInLineRanOn`);
`decidedMs` is how long the four placements took (no model is asked). Each part has an `ok`; on
this PC the tool returned in 33 ms and replies beside a parallel think answered
in 2-7 ms. Loopback only; reads no credentials.

`research_check` rehearses [web research](../CONVERSATION.md#web-research) end to
end with Martlet's own tool texts and job kind (`WebResearch`), scheduler
(`BackgroundJobs`), web client (`WebAccess`), research loop (`WebResearchRun`,
each step a `BackgroundThink` through the conversation runtime and Chat
Completions adapter) and report creation (`ResearchReports`), against fixtures
on 127.0.0.1 (NOT AI): a DuckDuckGo-like search page (an ad, results behind
redirect links), web pages (one with scripts and navigation, a PDF, one
redirecting to 192.168.1.1, one only a later step asks for) and a model with
canned answers. `settings`: on by default, off when turned off or with Thinking
longer off, saved lean (only off is saved). `guard`: which addresses count as public (`WebAccess.IsPublic`: no
loopback, private, shared, link-local, cloud metadata, ULA, multicast or mapped
private addresses). `flow`: a reply says it'll look into it and calls
`research`; the tool returns before the reply ends (`toolReturnedMs`,
`replyMs`) and the job is still running when the reply completes
(`researchStillRunning`); the job (`research-1`, `Succeeded`, `offer` true,
`Researching`, `timeLimit` and `perHour` *none*, `atOnce` 1) made 2 searches (`searchQueries`: the
topic, then the model's), read 3 pages and found 2 unreadable (the PDF, and the
redirect to a private address, never followed: `privateRedirectFollowed`) in 2
model steps, each a background message under 16 KiB carrying the numbered
sources (`steps`); the first step starts with no notes, and the second carries
the notes the first one wrote and only the pages read since as new pages
(`notesCarried`, `steps[].carriesNotes`, `run.notesCharacters`); the `note` the conversation gets marks it to offer first and
says to call `perform_creation` with the report's id; the report is kept as a
`report` creation in a temporary Creations library (`creation`) and showing it
writes a page with its source links (`page.links`) and no scripts (`shown`).
`flow.searchable`: the kept report is indexed for
[finding it again](../CONVERSATION.md#finding-a-report-again) (`reportsIndexed`,
`sections`); a question about laser pointers finds its section (`hits`,
`best` relevance) with the sources it cites (`cited`); words of its title name
it and other words name none (`namedByTitleWords`, `namedByOtherWords`);
deleting the creation drops it (`reportsAfterDelete` 0). `firstSearchMs` and
`searchMs` time the first and a warm search (on this PC 22 ms and 0.16 ms), and
`searchGuide` is `search_guide`'s text exactly as replies get it, with its
`bytes` (639).
`tool` and `prompt` are exactly what replies get. `limits`: a second research is
refused as `busy` (and what the model is told) while a think runs beside it,
Cancel ends it as `Canceled`, ten in a row all start once the one before is done
(`noHourlyLimit`: there is no hourly limit),
and a failed first search fails the job (`failedSearch`). `limits.placement`:
on Deep thinking's places, a think holding the only place keeps research from
starting (the message names it); research is a long job, so with one other
place free it is refused too, because the pool's last free slot stays free for
quick jobs (`lastFreeSlot`, where a screen summary takes it at once), and with
two other places free it runs on the one sharing least with the conversation
(`researchOn`). `budget`: the production budget (`WebResearch.Budget`: 30
searches, 60 pages, 40 model steps, 25 MB, 3 pages a step, 5,000 characters of
notes, no time limit or hourly limit, one at a time) run against an in-memory web
and a fixture model that takes notes and searches again until no more pages can
be read: on this PC it read all 60 pages in 20 model steps and 20 searches, its
largest step was 11,701 bytes (`largestTaskBytes`, at most `maxTaskBytes`
13,000), its notes stayed within 5,000 characters and the report lists all 60
sources (`reportSources`). `flow.run.foundEarly`: the first step's `FOUND`
line, read through the real adapter, is the watcher's finding at step 1, with
its page numbers left out of `answer`. `levels`: the
[levels of looking things up](../CONVERSATION.md#answer-look-up-or-research):
`tool` has the `depth` enum (`quick`, `full`, `deep`; none or an unknown word
reads as `full`) and the reply's prompt has every rung (`ladder`); `budgets`
per depth (on this PC quick 3 searches, 9 pages, 4 steps; full 30, 60, 40; deep
60, 120, 80); `kinds`: `lookup` (2 at once, not offered, 10 minutes, Thinking
pool priority 40 against research's 10), `finding` (a notice spoken as
finished work, so it never drops for its age) and `sourcecheck` (quiet), all on
the Thinking pool's research places; `placement`: a quick look-up starts on
another place beside full research (`research` diva, `lookup` imouto);
`quick`: a look-up whose answer is only near the end of a long page ends after
1 step with a settled answer, because the step was shown the page's matching
parts (`pageShownByParts`), and `result` is what the conversation gets;
`watcher`: full research's first step finds the answer, and its `finding-1`
notice is finished and waits for Martlet (`noticeBeforeReport`,
`noticeWaitsForMartlet`) while the second step still runs; `noticeNotes` is
the note with the user's next message; `sourceCheck`: the report's result is
ready before the `sourcecheck-1` job finishes (`resultReadyFirst`), the quiet
job checks 2 claims in 1 request (1 supported, 1 not supported), revises the
kept report (`revised`, `sectionInReport`) and its `note` names the claim that
isn't supported. Each part has an `ok`.
No real web search or model is used; reads no credentials; the temporary folder
is deleted.

`pc_activity_check` checks [where what this PC plays comes from](../CONVERSATION.md#where-it-comes-from-and-what-you-are-doing)
(optional `seconds`, 1-20, default 3). `live` runs the production
`PcActivityMonitor` with `WindowsPcActivitySource` on this PC for those seconds
(the volume mixer's session meters, the apps' windows, the window in front, full
screen and the GPU Engine counters; nothing is recorded, played, kept or sent,
so `recorded` is always false, and raw window titles are never returned):
`ticks`, `averageTickMs`, `problem`, the `summary` and the `note` a reply would
read (the context board's `activity` note), and `apps`: every app with an audio
session (`app`, `kind`, `label`, `peak`, `audible`, `foreground`, `fullScreen`,
`exclusiveFullScreen`, `gpuPercent`, `programKnown`, `windows`). `rehearsal`
runs FIXTURE apps, levels and lines on a simulated clock: `classify` (the
production classifier on fixed apps such as Plex full screen, a YouTube video
or a Twitch stream in a browser, Netflix in a browser, a private window, Discord,
Zoom, Google Meet, Spotify, a game in a Steam library, an unknown app full screen
with the graphics card busy, OBS and VLC with a movie or a song; each `scene`,
`kind`, `label`, `expected`, `ok`), `lines` (a voice chat over a game, the game
alone, a YouTube video and silence: each fixture line's `[PC audio] From ...:`
text against `expected` with `mediaOnly`, then the `note` against `expectedNote`), `speakers` (the
production check `PcEcho.Speakers` on a simulated clock that tells the
microphone hearing this PC's speakers apart from you: a video or a game line
the microphone heard at that same moment is the speakers; the same words the
video said 10 seconds before, your voice played back by a voice changer, a game
while a voice changer also played, a voice chat repeating you, a short answer,
common words scattered through a long video, you quoting the video and you
talking over it are not; each `scene`, `heard`, `played`,
`playedFrom`, `expected`, `fromSpeakers`, `ok`) and `prompts` (the built-in
*What this PC plays* and *Always listening* prompts with what they must say,
and `missing`). `ok` is true when the live look had no problem and every
fixture came out as expected. It reads no credentials and contacts nothing.
