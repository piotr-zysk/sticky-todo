# Version history

## 2.10.1 — 2026-11-20

- Match details and subtask window settings to the main window's three-dot icon and Settings label.

## 2.10.0 — 2026-11-20

- Add local background color settings for details and subtask windows, inherited by descendant windows unless they have their own color. Clear restores inheritance.

## 2.9.0 — 2026-11-20

- Store settings in `_settings.yaml` and migrate the legacy `settings.yaml` file on load.

## 2.8.0 — 2026-11-20

- Close a task's details or subtask window when its row button is clicked again.

## 2.7.0 — 2026-11-20

- Double the default width of details and subtask windows to 800 px.

## 2.6.0 — 2026-11-17

- Open details windows below the main window (above when there is no room) and keep them from overlapping the main window and each other.

## 2.5.1 — 2026-11-17

- Fix recent color boxes: clicks reliably apply the color, no size change or text border on click, and the 4 remembered colors now span the full row width.

## 2.5.0 — 2026-11-17

- Remember the last 4 unique saved background colors and show them as selectable color boxes in Settings; click a box to apply it, or select its text and press Ctrl+C to copy.

## 2.4.0 — 2026-11-17

- Replace the zoom select box in Settings with a 50–150% slider styled like the opacity slider.
- Allow any whole zoom percentage from 50 to 150 and keep the Settings window at 100% regardless of zoom.

## 2.3.0 — 2026-11-11

- Place Copy before the list switch in the text-details header.
- Show a plus on the text/list switch icon when its target is empty, updating live for child-list changes and unsaved or whitespace-only text.

## 2.2.0 — 2026-11-11

- Open nonempty subtask lists directly from their row icon, including switching an already-open text window to its subtask view. Tasks without subtasks still open text details.

## 2.1.0 — 2026-11-11

- Show only the list icon in the details button for tasks with subtasks, replacing the text bubble or plus and removing the separate list indicator.

## 2.0.0 — 2026-11-11

- Add recursive subtask lists with the main list's editing, ordering, dates, priority, and completion behavior; switch between text and subtasks in the details header.
- Warn before completing tasks with unfinished descendants, with editing locked during the undo delay. Archive main-list tasks only and delete completed subtasks and descendant text.
- Treat empty subtask lists as absent and show a separate indicator for nonempty lists.
- Upgrade Google sync to schema 2 with per-task merging, content-free deletion markers, and ancestor restoration for newer descendant edits. All syncing computers must upgrade; schema 1 remains readable.
- Keep DLL-hosted development and regression data beside the managed application rather than beside the shared .NET host.

## 1.7.1 — 2026-11-11

- Show the date instead of the time for timed tasks due at least 24 hours ago.

## 1.7.0 — 2026-11-11

- Add a root `.slnx` solution containing the app and regression runner projects.

## 1.6.0 — 2026-11-04

- Make the details-window copy icon lighter and more subtle.

## 1.5.0 — 2026-11-04

- Add a details-window copy button for the task title and current details text.

## 1.4.0 — 2026-11-04

- Default the due date to today when adding a task on top.

## 1.3.0 — 2026-11-04

- Move the task details button before the title.

## 1.2.0 — 2026-11-04

- Show only the time for timed tasks due within 24 hours and size the due column to its content.

## 1.1.0 — 2026-11-04

- Give buttons and text fields in the settings window rounded corners and clear interaction feedback.

## 1.0.0 — 2026-11-03

- Establish the initial versioned baseline and display the app version beside “To do” in the main window header.
