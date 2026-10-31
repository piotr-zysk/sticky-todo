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

Sync first commits task editors, saves pending details immediately, and archives checked tasks even if their retention period has not elapsed. Editing is paused while syncing. If closing sync fails, choose **Retry**, **Close anyway**, or **Cancel close**. Local-save failures keep the app open.

Google Drive contains a `StickyTodo` folder with a versioned `sticky-todo.json` file. Local tasks retain their YAML and text files beside the executable. Sync merges active tasks and archived tasks by their IDs:

- A task present on only one side is copied to the other.
- For the same ID, the later `UpdatedAt` replaces the entire other record.
- Google wins exact timestamp ties.
- Priority and completion changes advance `UpdatedAt`, as do title and due date/time changes.
- Details advance `UpdatedAt` when saved, using the existing one-minute throttle. Sync and window close force pending saves.
- Completed tasks are retained in the archive without details. A newer completion removes an older active task on another computer.

Existing task order is retained locally, and newly imported tasks are appended. Appearance settings and reordering are local. Keep computer clocks correct because timestamps determine winners. Tasks independently created before sync have distinct IDs, even if their titles match.

Google connection configuration and the last successful sync time are stored under `%LOCALAPPDATA%\StickyTodo\Google`. Access and refresh tokens are encrypted for the current Windows user with DPAPI. Sign-out clears cached tokens; it does not delete local or Google tasks. Account connection changes take effect independently of the appearance dialog's Save/Cancel buttons.

Do not edit the sync JSON while syncing. Conditional uploads detect another client's changes and retry up to three times. Ambiguous duplicate sync folders/files and invalid or unsupported snapshots stop sync with an error instead of choosing a copy silently.

The transport uses Drive v3 for account/folder operations and downloads, and the supported Drive v2 metadata/update endpoints for ETag-based conditional writes. Drive v3 does not expose file ETags. Both versions use the same authorization and Drive API project configuration.

## Verify changes

With the .NET 10 Windows SDK installed:

```powershell
dotnet build StickyTodo.csproj
dotnet run --project Tests/RegressionRunner/RegressionRunner.csproj
```

The regression runner uses fake Google transports and temporary credentials. For live verification, connect a test account on two computers: create and sync a task on the first, fetch it on the second, change different fields in turn, verify the newer complete record wins, then complete and sync it. Also verify pending detail edits survive a manual sync and that an offline close offers all three choices. Browser sign-in and live Google access require a real desktop OAuth configuration.
