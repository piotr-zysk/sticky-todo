# StickyTodo agent guide

This guide applies to the whole repository. Keep it current when changing architecture, storage formats, development commands, or user-visible workflows. Treat the behavior below as the compatibility baseline unless the requested change explicitly updates it.

## App and toolchain

- StickyTodo is a small Windows desktop sticky-note task manager built with C# and WPF. It supports ordered tasks, priority, optional due dates/times, per-task text notes, delayed completion, appearance settings, and optional Google Drive sync.
- The main window stays on top, uses custom borderless chrome, allows horizontal resizing, and sizes its height to the visible task count.
- `StickyTodo.csproj` targets `net10.0-windows`, enables WPF, nullable reference types, and implicit usings, and produces a `WinExe`. Use Windows with the .NET 10 SDK and Windows Desktop support for builds and execution.
- Direct NuGet dependencies are YamlDotNet (`16.*`) and Google.Apis.Drive.v3 (`1.77.0.4301`). Check the project file before changing versions.
- There is no solution file or separate frontend/build toolchain. Run commands from the repository root with explicit project paths.

## Commands and verification

```powershell
# Build (also restores NuGet packages)
dotnet build StickyTodo.csproj

# Launch the desktop app
dotnet run --project StickyTodo.csproj

# Run the regression checks
dotnet run --project Tests/RegressionRunner/RegressionRunner.csproj
```

- The regression runner is a console executable with a WPF-enabled, `[STAThread]` entry point, custom assertions, and dispatcher pumping. It is not an xUnit/NUnit/MSTest project; `dotnet test` does not run these checks. Success prints `All regression checks passed.` and returns exit code 0.
- `Tests/RegressionRunner/Program.cs` covers settings, rendering, timers, details sessions/windows, file failures, and storage migration. `SyncChecks.cs` covers timestamps, transactional storage, merges, retry logic, sync UI, token protection, and Drive HTTP behavior. `ConnectionChecks.cs` covers account persistence and close behavior.
- Tests use fake transports/HTTP handlers, dummy credentials, and temporary connection directories; real Google access is unnecessary. Some storage tests temporarily replace files in the runner's executable directory and restore them in `finally`. Use disposable build output and avoid running multiple regression runners against the same output directory.
- For code changes, build and run the regression runner. Add focused behavioral coverage to the existing runner for meaningful logic changes, especially persistence, synchronization, and failure recovery. Do not introduce a new test framework without a task-specific reason.
- For UI changes, also check the affected flow in a Windows desktop session: keyboard/focus behavior, popups, resize/scroll behavior, and appearance where relevant. Do not claim visual verification from a build alone.
- Live Google verification needs a desktop OAuth configuration and a test account. Follow [GOOGLE-SYNC.md](GOOGLE-SYNC.md) for setup and the two-computer sync checklist. Report checks that could not run and their concrete blocker.

## Code map

| File(s) | Responsibility |
| --- | --- |
| `App.xaml`, `App.xaml.cs` | Application lifetime, main-window creation, startup storage error reporting. |
| `MainWindow.xaml`, `MainWindow.xaml.cs` | Main UI, observable task collection, edit commits, ordering/drag-and-drop, completion timers, details ownership, appearance, sync and shutdown orchestration. |
| `TaskItem.cs` | Bindable task state and editor state, stable identity, monotonic modification timestamps, due-date formatting/parsing and overdue calculation. |
| `Storage.cs` | DTOs, YAML serialization, details files, settings, legacy migrations, archive, snapshots, multi-file transactions and recovery. |
| `DetailsSaveSession.cs` | UI-independent details dirty state, one-minute save throttle, forced saves, discard and replacement rules. |
| `DetailsWindow.xaml`, `DetailsWindow.xaml.cs` | Text editor, save timer, close-time flush, sync refresh, appearance. |
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

Storage is based on the executable's directory (`Environment.ProcessPath`, falling back to `AppContext.BaseDirectory`), **not** the working directory or source root. Development runs normally store data alongside the built executable. The app therefore needs a writable executable directory.

```text
<executable directory>/
  settings.yaml
  tasks/
    tasks.yaml              # active task metadata, in local display order
    task-archive.yaml       # completed task metadata
    <task-id-in-N-format>.txt
    .transaction.json       # transient durable undo journal
```

- `TaskDto` stores `Id`, `Title`, optional `Due`, `Priority`, optional `CompletedAt`, and `UpdatedAt`. YAML keys use camelCase and null values are omitted. Unknown YAML properties are tolerated.
- Details filenames use the task GUID in `N` format (32 hexadecimal characters without hyphens). Persist identity before creating details. Preserve IDs across edits, reordering, completion, and sync.
- Due dates are local wall-clock values serialized with invariant formatting as `yyyy-MM-dd` or `yyyy-MM-dd HH:mm`. A date without a time is due at 23:59:59 that day; clearing the date clears the time. Modification timestamps are separate UTC-based `DateTimeOffset` values serialized in round-trip `O` format.
- Task title, priority, completion state, and due changes advance `UpdatedAt`. `TaskItem.NextTimestamp` guarantees advancement even when the system clock has not advanced. Loading DTOs restores the persisted timestamp after applying task fields; do not accidentally timestamp imports as fresh edits.
- Startup migrates legacy `tasks.yaml` and `task-archive.yaml` from the executable root into `tasks/`. If old and new copies both exist, initialization fails rather than selecting one silently.
- Record migration repairs missing/duplicate IDs within each metadata file, infers missing timestamps from existing file/details/completion times, and resolves an ID present in active and archived data by keeping the newer record (archive wins ties).
- Task YAML read errors attempt a `.bad` backup and propagate; startup reports the failure. Settings load errors fall back to defaults. Do not silently replace unreadable task data with an empty list.
- Writes use temporary files and replacement. Multi-file task saves use a durable undo journal: write previous contents, apply changes, then delete the journal. Recovery restores previous contents after failure or interrupted execution. Preserve transaction boundaries, recovery order, and journal path validation.
- Completion atomically removes the active record, adds archived metadata, and deletes details. Archived records retain identity and timestamps so completion can propagate through sync. Do not prune the archive as routine cleanup.
- `Storage.SaveTaskDetails` commits details and the new metadata timestamp together, updating the in-memory timestamp only after success. Do not bypass it with a plain text write in the normal editor flow.

## UI and details lifecycle

- Checked tasks remain visible for `ItemDeleteRetentionSeconds` (default 5, minimum 0), allowing uncheck before archival. Sync and app close archive checked tasks immediately, regardless of remaining delay.
- The list shows up to `MaxItemsToDisplay` rows (default 10, bounded 1–100) before scrolling; row height is currently 24. Preserve content-driven height and horizontal-only resizing.
- Defaults include background `#F9EDB5`, opacity 1, and non-bold high-priority titles. Appearance application clamps opacity to 0.2–1 and chooses foreground contrast. Propagate appearance changes to open details windows.
- Appearance settings edit a copy with live preview. Save persists it; Cancel restores the prior appearance. Google connection actions take effect independently of appearance Save/Cancel. Include new settings in defaults, copy/reset logic, validation, persistence, and relevant checks.
- There is one details window and one save session per task ID. Sessions survive closing/reopening a window so reopening does not reset the throttle.
- The first dirty details save can happen immediately; subsequent saves are throttled to once per minute. Closing a note, syncing, or closing the app forces pending saves. A failed write leaves the session dirty and the editor available for retry.
- Discard sessions when their task is completed so an old editor cannot recreate deleted details. Replacing session text from sync requires a clean, undiscarded session and must not trigger a fresh local edit.

## Google sync contract

- Sync is optional. Local tasks must remain usable when Google configuration is missing or damaged. Startup and sign-in do not automatically sync; the header button and signed-in app close do.
- Before syncing, commit task editors, force-save details sessions, archive checked tasks, and save local tasks. While syncing, disable editing in the main/details windows and pause completion timers; restore UI state in `finally`. The current sync timeout is 30 seconds.
- The remote file is `StickyTodo/sticky-todo.json`, with camelCase JSON and schema version 1. Validate snapshots before merging/applying/uploading: unique nonempty IDs, nonblank titles, valid timestamps/priorities/due values, and no details on completed records. Required JSON fields must remain enforced. Unsupported versions fail explicitly.
- Merge by task ID, choosing the entire record with the later `UpdatedAt`; Google wins exact ties. This is not a field-by-field merge. Keep local ordering and append remote-only tasks. Appearance and reordering remain local.
- Completed records participate in merges. A newer completion replaces an older active task; retaining archived records prevents old active copies from returning through sync.
- Upload the merged snapshot conditionally before applying it locally and recording success. `IDriveTransport.WriteAsync` returns false for a concurrent modification; `SyncEngine` re-reads/re-merges for up to three total attempts. Preserve this concurrency protection.
- Drive v3 handles discovery and media download. Drive v2 metadata provides the file ETag, and the v2 media update endpoint uses `If-Match`. A media-download ETag is not a file-update token. Do not replace this with unconditional v3 uploads or silently accept missing ETags.
- Multiple matching folders/files are an error. Do not pick an arbitrary duplicate. Use the same desktop OAuth client and Google account across computers because `drive.file` scope limits file visibility.
- On close, remote sync failure offers Retry, Close anyway, or Cancel close. Local read/save failures keep the app open and must not offer Close anyway. Preserve guards against overlapping sync/close operations.
- Google configuration lives under `%LOCALAPPDATA%\StickyTodo\Google`: `client.json`, `connection.json`, and encrypted token files under `tokens/`. Access/refresh tokens use Windows DPAPI with `CurrentUser`, never portable task files or `settings.yaml`. Sign-out clears cached tokens without deleting tasks. Last successful sync is tracked per account.

## Finishing a change

- Keep changes within the requested scope and preserve existing user data and compatibility unless migration is part of the task.
- Update this guide when its commands or architectural guidance change, and update `GOOGLE-SYNC.md` when Google setup or user-facing sync behavior changes.
- Summarize the behavior changed, verification performed, and any checks blocked by the environment. Do not describe unrun builds, regression checks, desktop flows, or live Google operations as passing.
