# Version history

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
