# Google Drive sync

## Connect an account

1. Open [Google Cloud Console](https://console.cloud.google.com/) and create or select a project.
2. Enable **Google Drive API** for the project.
3. Configure **Google Auth Platform** (the OAuth consent screen). If the app is in Testing, add your Google account to its test users.
4. Create an OAuth client with application type **Desktop app**, then download its credentials JSON.
5. In StickyTodo Settings, choose **Import credentials…**, select the JSON, and choose **Sign in to Google**. Finish authorization in your browser.
6. Import the **same OAuth client JSON** and connect the **same Google account** on each computer.

The app requests `drive.file` access, which limits access to files it creates or that you explicitly open with it. A folder created manually or by a different OAuth client may therefore be invisible to this app. Let StickyTodo create its folder. Testing-mode Google authorizations can expire after seven days; sign in again when necessary.

## Use sync

The circular-arrow button appears on the right of the main header after sign-in. Hover over it for the last successful sync time; click it to sync. The app also syncs before closing. Startup and sign-in do not run a sync automatically.

Sync first commits task editors and saves pending details immediately at every depth. It then completes checked tasks even if their retention period has not elapsed: main-list tasks enter the archive, while subtasks and their descendants are deleted. Editing is paused in all open lists and details windows while syncing. If closing sync fails, choose **Retry**, **Close anyway**, or **Cancel close**. Local-save failures keep the app open.

The details header places Copy before the list switch in text mode. Its list/document switch icons show a small plus when the target subtask list or text is empty, including unsaved text changes. Use this switch to move between text and subtasks. Each subtask can have its own text and child list. An empty list is treated as absent; simply opening it does not create stored data. The task-details button shows only a list icon when a task has children, replacing its text bubble or plus. Clicking the list icon opens the subtask view directly, including when its text window is already open. Without children, the icon reflects the text details again and newly opened details start in text mode. Checking a task with unfinished descendants asks for confirmation; during the undo delay, descendant editing is paused. Uncheck the task to undo. Completion deletes all descendant task content and text, even when the parent itself is archived.

Google Drive contains a `StickyTodo` folder with a versioned `sticky-todo.json` file. Local tasks retain their YAML and text files beside the executable. Sync merges active tasks and archived tasks by their IDs:

- A task present on only one side is copied to the other unless a newer deletion or ancestor completion suppresses it.
- For the same ID, the later `UpdatedAt` replaces the entire other record.
- Google wins exact timestamp ties.
- Priority and completion changes advance `UpdatedAt`, as do title and due date/time changes.
- Details advance `UpdatedAt` when saved, using the configured autosave interval (default 60 seconds; 0 disables autosave). Sync and window close force pending saves even when autosave is disabled.
- Only completed main-list tasks are retained in the archive, without details. Subtasks are deleted; separate sync markers retain only their IDs, parent IDs, and deletion timestamps, never their task content.
- Each task and subtask merges independently. A newer child edit can restore its deleted or completed ancestors using active records from the other computer; unrelated deleted siblings stay deleted. Without such a newer edit, ancestor completion deletes stale children, including children created offline before completion.
- Restored ancestors retain a subtree-deletion timestamp so an older child from a third computer cannot return later. This bookkeeping history is preserved independently of title/text changes.

Existing sibling order is retained locally, and newly imported siblings are appended. Appearance settings and reordering are local. Keep computer clocks correct because timestamps determine winners. Tasks independently created before sync have distinct IDs, even if their titles match.

StickyTodo 2.0 writes sync schema version 2 and can read the previous version 1 as main-list tasks. **Upgrade every syncing computer before syncing with 2.0.** Older app versions cannot read version 2; downgrading with hierarchical local data is unsupported. Unsupported future versions stop sync explicitly.

Google connection configuration and the last successful sync time are stored under `%LOCALAPPDATA%\StickyTodo\Google`. Access and refresh tokens are encrypted for the current Windows user with DPAPI. Sign-out clears cached tokens; it does not delete local or Google tasks. Account connection changes take effect independently of the appearance dialog's Save/Cancel buttons.

Do not edit the sync JSON while syncing. Conditional uploads detect another client's changes and retry up to three times. Ambiguous duplicate sync folders/files and invalid or unsupported snapshots stop sync with an error instead of choosing a copy silently.

The transport uses Drive v3 for account/folder operations and downloads, and the supported Drive v2 metadata/update endpoints for ETag-based conditional writes. Drive v3 does not expose file ETags. Both versions use the same authorization and Drive API project configuration.

## Verify changes

With the .NET 10 Windows SDK installed:

```powershell
dotnet build StickyTodo.slnx
dotnet run --project Tests/RegressionRunner/RegressionRunner.csproj
```

The regression runner uses fake Google transports and temporary credentials. For live verification, connect a test account on two upgraded computers: create a task with children and grandchildren on the first and fetch it on the second. Edit different subtasks offline and verify both edits survive sync; edit the same task and verify the newer whole record wins. Complete a child and verify deletion without an archive entry. Complete a parent and verify that stale descendants disappear. Separately test a descendant edit newer than parent completion and verify its ancestor chain returns while stale siblings remain deleted. Also verify pending text survives manual sync, empty lists show no indicator, and an offline close offers all three choices. Browser sign-in and live Google access require a real desktop OAuth configuration.
