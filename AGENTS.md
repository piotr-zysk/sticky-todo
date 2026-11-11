# StickyTodo agent guide

This guide applies to the whole repository. Keep it current when changing architecture, storage formats, development commands, or user-visible workflows. Treat the behavior below as the compatibility baseline unless the requested change explicitly updates it.

## App and toolchain

- StickyTodo is a small Windows desktop sticky-note task manager built with C# and WPF. It supports ordered tasks, recursive subtasks, priority, optional due dates/times, per-task text notes, delayed completion, appearance settings, and optional Google Drive sync.
- The main window stays on top, uses custom borderless chrome with rounded corners, allows horizontal resizing, and sizes its height to the visible task count.
- `StickyTodo.csproj` targets `net10.0-windows`, enables WPF, nullable reference types, and implicit usings, and produces a `WinExe`. Use Windows with the .NET 10 SDK and Windows Desktop support for builds and execution.
- Direct NuGet dependencies are YamlDotNet (`16.*`) and Google.Apis.Drive.v3 (`1.77.0.4301`). Check the project file before changing versions.
- `StickyTodo.slnx` at the repository root includes the app and regression runner. There is no separate frontend/build toolchain. Run commands from the repository root.

## Versioning

- The app version is the three-part `<Version>` in `StickyTodo.csproj`; the main header displays the generated assembly version. Do not duplicate the version as a hard-coded UI string.
- For every future project change, bump the version and append a corresponding entry to `versionlog.md`: increment the first component for breaking changes, the second for smaller features or improvements, and the third for bug fixes.

## Commands and verification

```powershell
# Build all projects (also restores NuGet packages)
dotnet build StickyTodo.slnx

# Launch the desktop app
dotnet run --project StickyTodo.csproj

# Run the regression checks
dotnet run --project Tests/RegressionRunner/RegressionRunner.csproj
```

- The regression runner is a console executable with a WPF-enabled, `[STAThread]` entry point, custom assertions, and dispatcher pumping. It is not an xUnit/NUnit/MSTest project; `dotnet test` does not run these checks. Success prints `All regression checks passed.` and returns exit code 0.
- `Tests/RegressionRunner/Program.cs` covers settings, rendering, timers, details sessions/windows, file failures, and storage migration. `DetailsChecks.cs` covers configurable autosave, clipboard copying, live details indicators, and settings preview/Cancel behavior. `SyncChecks.cs` covers timestamps, transactional storage, merges, retry logic, sync UI, token protection, and Drive HTTP behavior. `ConnectionChecks.cs` covers account persistence and close behavior.
- Tests use fake transports/HTTP handlers, dummy credentials, and temporary connection directories; real Google access is unnecessary. Some storage tests temporarily replace files in the runner's executable directory and restore them in `finally`. Use disposable build output and avoid running multiple regression runners against the same output directory.
- `SubtaskChecks.cs` covers recursive storage, deletion rollback, independent child merges, ancestor restoration, empty-list behavior, nested windows, completion confirmation/locking, and sync rebinding. If Windows Application Control blocks the generated runner `.exe`, run its built `RegressionRunner.dll` through the .NET host instead.
- For code changes, build and run the regression runner. Add focused behavioral coverage to the existing runner for meaningful logic changes, especially persistence, synchronization, and failure recovery. Do not introduce a new test framework without a task-specific reason.
- For UI changes, also check the affected flow in a Windows desktop session: keyboard/focus behavior, popups, resize/scroll behavior, and appearance where relevant. Do not claim visual verification from a build alone.
- Live Google verification needs a desktop OAuth configuration and a test account. Follow [GOOGLE-SYNC.md](GOOGLE-SYNC.md) for setup and the two-computer sync checklist. Report checks that could not run and their concrete blocker.

## Code map

| File(s) | Responsibility |
| --- | --- |
| `App.xaml`, `App.xaml.cs` | Application lifetime, main-window creation, startup storage error reporting. |
| `MainWindow.xaml`, `MainWindow.xaml.cs` | Main chrome, root task collection, recursive completion/locking, details ownership, appearance, sync and shutdown orchestration. |
| `TaskListControl.xaml`, `TaskListControl.xaml.cs` | Shared main/subtask rows, add form, title/due editors, priority, sibling-only drag/drop, appearance resources, and list sizing. |
| `TaskTree.cs` | Iterative hierarchy assembly and traversal with cycle/missing-parent checks. |
| `TaskItem.cs` | Bindable task state and editor state, stable identity, monotonic modification timestamps, due-date formatting/parsing and overdue calculation. |
| `Storage.cs` | DTOs, YAML serialization, details files, settings, legacy migrations, archive, snapshots, multi-file transactions and recovery. |
| `DetailsSaveSession.cs` | UI-independent details dirty state, configurable autosave throttle, forced saves, discard and replacement rules. |
| `DetailsWindow.xaml`, `DetailsWindow.xaml.cs` | Text editor, save timer, close-time flush, sync refresh, appearance. |
| `RoundedBorder.cs` | Rounded window border that clips content to its inner corners (used by the main and details window templates). |
| `AppearanceSettings.cs`, `SettingsWindow.xaml`, `SettingsWindow.xaml.cs` | Settings defaults/bounds, validation, live preview, persistence, Google account controls. |
| `SyncSnapshot.cs` | Versioned sync schema and validation, whole-record merge, `IDriveTransport`, conditional upload retry engine. |
| `GoogleDriveTransport.cs` | Drive folder/file discovery, snapshot download, ETag-based conditional upload, duplicate detection. |
| `GoogleConnection.cs` | Desktop OAuth, account state, last successful sync, DPAPI-protected token store. |
| `SyncFailureWindow.xaml`, `SyncFailureWindow.xaml.cs` | Retry / Close anyway / Cancel close choices. |
| `GOOGLE-SYNC.md` | User-facing OAuth setup, sync semantics, transport rationale, verification instructions. |

## Implementation conventions

- Follow the existing file-scoped `StickyTodo` namespace, four-space indentation, nullable annotations, PascalCase public members, and underscore-prefixed instance fields.
- The app uses WPF code-behind and `TaskItem : INotifyPropertyChanged`, with small extracted logic classes. Extend these patterns; avoid broad architectural rewrites for a focused feature.
- Keep XAML event handlers, named controls, bindings, and resource keys aligned with code-behind. Regression checks also use reflection and named controls; inspect affected checks when renaming them.
- Keep WPF collection/control mutations on the dispatcher thread. Use asynchronous APIs for Google I/O and retain cancellation/timeouts. Reserve `async void` for UI event handlers.
- Distinguish persistent task mutations from temporary UI/editor state. Raise the necessary dependent property notifications and route saves through the established storage/session APIs.
- The main project explicitly excludes `Tests/**/*.cs` from compilation. Keep additional regression code under that directory and preserve the exclusion.
- Build output and IDE files are ignored by `.gitignore`. Do not edit generated `bin/` or `obj/` files. Do not commit personal task data, account state, credentials, or tokens; runtime YAML/text data is not universally ignored.

## Local data and compatibility

Storage is based on the executable's directory (`Environment.ProcessPath`, falling back to `AppContext.BaseDirectory` for the shared `dotnet` host or a missing process path), **not** the working directory or source root. Launching a managed DLL stores data alongside that DLL rather than beside `dotnet.exe`. Development runs normally store data alongside the built executable. The app therefore needs a writable executable directory.

```text
<executable directory>/
  settings.yaml
  tasks/
    tasks.yaml              # active task metadata, in local display order
    task-archive.yaml       # completed task metadata
    subtask-deletions.yaml  # IDs, parent IDs and timestamps only; no deleted content
    <task-id-in-N-format>.txt
    .transaction.json       # transient durable undo journal
```

- `TaskDto` stores `Id`, optional `ParentId`, `Title`, optional `Due`, `Priority`, optional `CompletedAt`, `UpdatedAt`, and optional `SubtasksDeletedAt` deletion history. Active metadata is flat; sibling order follows record order. Records without `ParentId` belong to the main list. YAML keys use camelCase and null values are omitted. Unknown YAML properties are tolerated.
- `TaskItem.Children` is observable, and `HasSubtasks` is derived from its count. Empty child collections are absent lists: no list IDs/files/payloads, no indicator, and no timestamp change from viewing one. Persist only child records and their parent relationships.
- Details filenames use the task GUID in `N` format (32 hexadecimal characters without hyphens). Persist identity before creating details. Preserve IDs across edits, reordering, completion, and sync.
- Due dates are local wall-clock values serialized with invariant formatting as `yyyy-MM-dd` or `yyyy-MM-dd HH:mm`. A date without a time is due at 23:59:59 that day; clearing the date clears the time. Modification timestamps are separate UTC-based `DateTimeOffset` values serialized in round-trip `O` format.
- Adding a task on top defaults a missing due date to the current local date, while preserving an explicitly selected date. Adding to the bottom does not default the date; time-only bottom insertion retains its next-occurrence inference.
- The task list displays only `HH:mm` for a task with an explicit due time less than 24 hours after the current local wall-clock time, including already-overdue timed tasks; otherwise it displays only `yyyy-MM-dd`. This is presentation-only and does not change due-date serialization.
- Task title, priority, completion state, and due changes advance `UpdatedAt`. `TaskItem.NextTimestamp` guarantees advancement even when the system clock has not advanced. Loading DTOs restores the persisted timestamp after applying task fields; do not accidentally timestamp imports as fresh edits.
- Startup migrates legacy `tasks.yaml` and `task-archive.yaml` from the executable root into `tasks/`. If old and new copies both exist, initialization fails rather than selecting one silently.
- Record migration repairs missing/duplicate IDs within each metadata file, infers missing timestamps from existing file/details/completion times, and resolves an ID present in active and archived data by keeping the newer record (archive wins ties).
- Task YAML read errors attempt a `.bad` backup and propagate; startup reports the failure. Settings load errors fall back to defaults. Do not silently replace unreadable task data with an empty list.
- Writes use temporary files and replacement. Multi-file task saves use a durable undo journal: write previous contents, apply changes, then delete the journal. Recovery restores previous contents after failure or interrupted execution. Preserve transaction boundaries, recovery order, and journal path validation.
- Completion atomically removes the task and its descendant records and text. Archive only a main-list task's own metadata; subtasks are never archived. Separate deletion markers retain only subtask ID, parent ID, and timestamp for sync. Retain the archive and deletion bookkeeping so stale copies cannot return.
- `Storage.SaveTaskDetails` commits details and the new metadata timestamp together, updating the in-memory timestamp only after success. Do not bypass it with a plain text write in the normal editor flow.

## UI and details lifecycle

- Checked tasks remain visible for `ItemDeleteRetentionSeconds` (default 5, minimum 0), allowing uncheck before completion. Sync and app close finalize checked tasks immediately, regardless of remaining delay.
- Checking any task recursively counts unchecked descendants and asks for confirmation if any exist (default No). Cancel leaves it unchecked. Confirmation locks descendant rows and editors and pauses descendant completion timers during the undo delay; unchecking restores them. Final completion deletes descendants, including their text, and archives only main-list tasks. Sync and close finalize checked subtasks by deletion.
- The details header toggles between text and the shared child-list control. Clicking a row's list icon opens its nonempty child list directly, switching an existing text window to list mode if necessary. Tasks without children start with text on opening. Preserve the selected mode during sync refresh. The list heading follows the task title. Child-list mode sizes its height to its rows and permits horizontal resizing; text mode restores its editor dimensions. Copy is available only in text mode, and Add subtask only in list mode. No per-list settings or Google controls are shown.
- Text-mode header buttons appear in Copy, List, Close order. The view switch uses a list icon for the subtask target and a document icon for the text target; a small plus marks an empty target. Update these icons from `HasSubtasks` and live `HasDetails`, including unsaved text, whitespace-only text, child removal, and sync refresh.
- The list shows up to `MaxItemsToDisplay` rows (default 10, bounded 1–100) before scrolling; row height is currently 24. Preserve content-driven height and horizontal-only resizing.
- In each task row, the details button appears before the title, after the priority marker; due-date content remains at the trailing edge.
- The main and details windows are transparent (`AllowsTransparency`), so Windows does not round them; their `Window.Template` draws a `RoundedBorder` (radius 8) bound to the window `Background`, `BorderBrush`, and `BorderThickness`. Keep that binding when changing appearance code. The due-date popup is also transparent with a rounded border and uses the `AppBackground`/`TaskForeground` resources set in `ApplyAppearance`. The shared `Calendar` style replaces the stock `CalendarItem` template to drop its hard-coded white frame. The settings and sync-failure dialogs use standard chrome, which Windows 11 rounds itself.
- Defaults include background `#F9EDB5`, opacity 1, and non-bold high-priority titles. Appearance application clamps opacity to 0.2–1 and chooses foreground contrast. Propagate appearance changes to open details windows.
- `ZoomPercent` defaults to 100 and snaps to the nearest level in `AppearanceSettings.ZoomLevels` (50–150%). `Zoom.Apply` sets a `LayoutTransform` on each window's root content (`RootDock`, `RootPanel`, `RootGrid`), which also scales popups placed inside it. Window sizes scale with zoom, capped to the work area. The main window keeps its user-chosen width in unzoomed units. Keep layout constants such as row height in unscaled units. Native message boxes and file dialogs are not scaled.
- Appearance settings edit a copy with live preview. Save persists it; Cancel restores the prior appearance. Google connection actions take effect independently of appearance Save/Cancel. Include new settings in defaults, copy/reset logic, validation, persistence, and relevant checks.
- There is one details window and one save session per task ID. Sessions survive closing/reopening a window so reopening does not reset the throttle.
- The details-window copy button copies the current task title, two newline characters, and live editor text; it does not save pending edits.
- The details button shows only the list icon when a task has active children, regardless of text content. Otherwise it shows `+` for missing, empty, or whitespace-only details and the bubble icon for populated text. Empty child lists do not override the text icon. `TaskItem.HasDetails` reflects current editor text, including unsaved edits, and refreshes on load and sync; it is not persisted and does not advance `UpdatedAt`.
- `DetailsAutoSaveIntervalSeconds` defaults to 60 and is bounded to 0–3600. Positive values allow the first dirty details save immediately and throttle subsequent saves from the last successful save; 0 disables autosave. Settings preview updates existing sessions and reschedules open notes without resetting save history; Cancel restores the previous interval.
- Closing a note, syncing, or closing the app forces pending details saves even when autosave is disabled. A failed write leaves the session dirty and the editor available for retry; automatic retries use the configured positive interval.
- Discard sessions when their task is completed so an old editor cannot recreate deleted details. Replacing session text from sync requires a clean, undiscarded session and must not trigger a fresh local edit.

## Google sync contract

- Sync is optional. Local tasks must remain usable when Google configuration is missing or damaged. Startup and sign-in do not automatically sync; the header button and signed-in app close do.
- Before syncing, commit task editors, force-save details sessions, archive checked tasks, and save local tasks. While syncing, disable editing in the main/details windows and pause completion timers; restore UI state in `finally`. The current sync timeout is 30 seconds.
- The remote file is `StickyTodo/sticky-todo.json`, with camelCase JSON and schema version 2. Version 1 is readable as root-only records; writes upgrade it to 2. All syncing computers must upgrade to 2.0; older apps reject schema 2 and local downgrade is unsupported. Validate unique nonempty IDs, nonblank titles, timestamps/priorities/due values, active parent references, acyclic hierarchy, deletion markers, and no completed subtasks or details on completed records. Required JSON fields remain enforced; unsupported versions fail explicitly.
- Merge by task ID, choosing the entire record with the later `UpdatedAt`; Google wins exact ties. This is not a field-by-field merge. Keep local ordering and append remote-only tasks. Appearance and reordering remain local.
- Main-list completed records and content-free subtask deletion markers participate in merges. A newer completion/deletion suppresses stale descendants. A newer active descendant restores the required ancestor chain using available active records, leaving unrelated deleted siblings absent. Restored ancestors retain `SubtasksDeletedAt` so old children unknown at completion cannot return from another computer later; merge that bookkeeping by maximum independently of editable task fields.
- Upload the merged snapshot conditionally before applying it locally and recording success. `IDriveTransport.WriteAsync` returns false for a concurrent modification; `SyncEngine` re-reads/re-merges for up to three total attempts. Preserve this concurrency protection.
- Drive v3 handles discovery and media download. Drive v2 metadata provides the file ETag, and the v2 media update endpoint uses `If-Match`. A media-download ETag is not a file-update token. Do not replace this with unconditional v3 uploads or silently accept missing ETags.
- Multiple matching folders/files are an error. Do not pick an arbitrary duplicate. Use the same desktop OAuth client and Google account across computers because `drive.file` scope limits file visibility.
- On close, remote sync failure offers Retry, Close anyway, or Cancel close. Local read/save failures keep the app open and must not offer Close anyway. Preserve guards against overlapping sync/close operations.
- Google configuration lives under `%LOCALAPPDATA%\StickyTodo\Google`: `client.json`, `connection.json`, and encrypted token files under `tokens/`. Access/refresh tokens use Windows DPAPI with `CurrentUser`, never portable task files or `settings.yaml`. Sign-out clears cached tokens without deleting tasks. Last successful sync is tracked per account.

## Finishing a change

- Keep changes within the requested scope and preserve existing user data and compatibility unless migration is part of the task.
- Update this guide when its commands or architectural guidance change, and update `GOOGLE-SYNC.md` when Google setup or user-facing sync behavior changes.
- Summarize the behavior changed, verification performed, and any checks blocked by the environment. Do not describe unrun builds, regression checks, desktop flows, or live Google operations as passing.
