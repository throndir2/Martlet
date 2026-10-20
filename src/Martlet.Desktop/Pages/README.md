# Pages of the desktop app

This folder holds the Companion pages that are moved out of `MainWindow`.
Each page has its own sub-folder. A page that is not moved yet is still a
`Render...` method in a `MainWindow.*.cs` file.

## How a page is built

A moved page has these parts:

| Part | Example | What it does |
| --- | --- | --- |
| Page | `ThinkingPool\ThinkingPoolPage.cs` | Makes the WPF controls and connects their events. It has no decisions and reads no settings files. |
| Page model | `ThinkingPool\ThinkingPoolPageModel.cs` | Reads the settings once each time the page draws, decides what each line says and saves the changes. It has no WPF types. |
| Services | `ThinkingPool\IThinkingPoolPageServices.cs` | Lists everything the page and its model need from the app. `MainWindow` implements it (for example in `MainWindow.ThinkingPool.cs`). |
| Page kit | `PageKit.cs` | The small builders that every page uses: `Card`, `Heading`, `Note`, `Status` (a status line with an automation ID), `Warning`, `Row`, `PageButton`, `Choice` (a large radio choice), `TerminalRow`, `TakeKey`, the dense rows (`Compact`, `RowLabel`, `RowGroup`, `RowSwitch`), the option rows (`Option`, `OptionTitle`), `PageNowCard` and `WithHint`. |

Moved pages: Thinking pool (`ThinkingPool\`), Check-ins (`CheckIns\`), Vision
(`Vision\`), Picture making (`Pictures\`), App guides (`AppGuides\`), Reading (`Reading\`), Singing (`Singing\`), Replies (`Replies\`), People (`People\`), Personality (`Personality\`), Memory (`Memory\`), Prompts (`Prompts\`), Lorebook (`Lorebook\`), Motion (`Motion\`), Tools (`Tools\`), Character (`Character\`), Eyes (`Eyes\`), Lip sync (`LipSync\`), Hearing (`Hearing\`), Smart home (`SmartHome\`), Discord (`Discord\`), Messaging (`Messaging\`), the job pages Thinking, Listening and Voice (`Jobs\`) and Touch (`Touch\`, with its Touch zones card in `TouchZones\`).
The Discord page reads the app's `DiscordService` and `DiscordCallService`
through its services interface; they hold no WPF.
The job pages' cards are in `Thinking\` (Model app, your model app, Ollama on
this PC, If Thinking fails, the situation fixture), `Voice\` (Voice engine,
Voices, Chatterbox Original's style), `Listening\` (This PC's listening, How you
talk, echo, what this PC plays) and `Talk\` (Speak replies, quick sounds). Each
card group has its own services interface, which MainWindow implements
(`MainWindow.ThinkingCards.cs`, `MainWindow.VoiceCards.cs`,
`MainWindow.ListeningCards.cs`); the job page gets the cards as builders through
`IJobPageServices` (`MainWindow.JobPage.cs`). The pool list card (`JobPoolCard`),
Keys from before and the lines that other pages also show stay in MainWindow
and come to the page as builders too.
The host dashboard's steps are `HostDashboard\HostDashboardModel` (no WPF), with
`IHostDashboardServices`; MainWindow draws them and runs what their buttons start. `Companion\CompanionPages.cs` is the catalog of all Companion pages
(titles, groups, icons, intros and job roles), without WPF.
`MainWindow.Companion.cs` is only the frame (the side list and `RenderTab`). The
pages that are not moved yet have one file each: Character and Eyes in
`MainWindow.CharacterPages.cs`. `Shared\` has the parts that more than one page uses, such as
`ChattinessPicker` (Vision and Listening) and `PictureFiles` (the decoded
pictures that Touch zones and Eyes show). Vision's image-model list and its Now
lines are shared with Hearing, so `MainWindow` still makes them and gives them
to `VisionPage` as builders.

Rules:

1. Give the page its services through its constructor. Do not let a page or a
   page model read `MainWindow` fields. Static catalogs on `MainWindow` (for
   example `MainWindow.LocalChatModels`) are allowed.
2. Keep the page model free of WPF, so that tests can use it.
3. Make the page model again each time the page draws. Keep state that must
   live longer (for example the last check of a model) in the page.
4. When `MainWindow` runs the feature that the page shows (for example the
   check-in runner in `MainWindow.CheckIns.cs`), keep the runner there. Let the
   services interface read its state and call its actions
   (see `CheckIns\ICheckInsPageServices.cs`).
5. Split a page into partial files by card when one file gets near 20 KB,
   so that an agent can read each file in one pass.
6. Keep the automation IDs and the UI text the same when you move a page. Show
   with `scripts\Invoke-MartletMcp.ps1 -Desktop` that `ui_snapshot` reads the
   same values before and after the move. `ui_snapshot` returns at most 200
   controls, so use `idPrefix` on a large page.

`Martlet.Desktop.csproj` imports `PageKit` everywhere with
`<Using Include="Martlet.Desktop.PageKit" Static="true" />`, so any file can call
`Card(...)`, `Note(...)` or `Status(...)` directly.
