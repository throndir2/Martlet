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
| Page kit | `PageKit.cs` | The small builders that every page uses: `Card`, `Heading`, `Note`, `Warning`, `Row`, `PageButton`, `TerminalRow`, `TakeKey` and the dense rows (`Compact`, `RowLabel`, `RowGroup`, `RowSwitch`). |

Moved pages: Thinking pool (`ThinkingPool\`) and Check-ins (`CheckIns\`).

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
`Card(...)` or `Note(...)` directly.
