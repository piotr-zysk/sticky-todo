using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StickyTodo;

static partial class Program
{
    static void CheckSubtaskStorage() => WithCleanStorage(() =>
    {
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            Check(Path.GetDirectoryName(Storage.TasksDirectory) == Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),
                "shared-host execution stores data beside the managed app, not beside dotnet.exe");
        var root = new TaskItem { Title = "Root", UpdatedAt = Baseline };
        var child = new TaskItem { ParentId = root.Id, Title = "Private child title", UpdatedAt = Baseline.AddSeconds(1) };
        var grandchild = new TaskItem { ParentId = child.Id, Title = "Grandchild", UpdatedAt = Baseline.AddSeconds(2) };
        var sibling = new TaskItem { ParentId = root.Id, Title = "Sibling", IsHigh = true, UpdatedAt = Baseline.AddSeconds(3) };
        root.Children.Add(child);
        root.Children.Add(sibling);
        child.Children.Add(grandchild);
        sibling.SetDue(new DateTime(2027, 2, 3), new TimeSpan(9, 15, 0));
        Storage.SaveTasks(new[] { root });
        Storage.SaveDetails(child.Id, "Private text");
        Storage.SaveDetails(grandchild.Id, "Nested text");
        var loaded = Storage.LoadTasks().Single();
        Check(loaded.Id == root.Id && loaded.Children.Select(t => t.Id).SequenceEqual(new[] { child.Id, sibling.Id }) &&
              loaded.Children[0].Children.Single().Id == grandchild.Id, "recursive YAML preserves identities, parents and sibling order");
        Check(loaded.HasSubtasks && loaded.Children[0].HasDetails && loaded.Children[1].IsHigh &&
              loaded.Children[1].DueTime == new TimeSpan(9, 15, 0), "subtasks retain details, priority and due time");
        Storage.SaveTaskDetails(child, "New private text", new[] { root });
        Check(child.UpdatedAt > Baseline.AddSeconds(1) && root.UpdatedAt == Baseline &&
              TaskTree.Flatten(Storage.LoadTasks()).Single(t => t.Id == child.Id).UpdatedAt == child.UpdatedAt,
              "child text advances only its own persisted timestamp");

        var previousYaml = File.ReadAllText(Storage.TasksPath);
        using (var locked = new FileStream(Storage.DetailsPath(grandchild.Id), FileMode.Open, FileAccess.Read, FileShare.Read))
            ExpectStorageFailure(() => Storage.CompleteTask(root, new[] { root }));
        Storage.InitializeTaskStorage();
        Check(File.ReadAllText(Storage.TasksPath) == previousYaml && !File.Exists(Storage.ArchivePath) &&
              !File.Exists(Storage.DeletionsPath) && Storage.LoadDetails(child.Id) == "New private text" &&
              Storage.LoadDetails(grandchild.Id) == "Nested text", "failed recursive completion rolls back metadata, markers and text");

        Storage.CompleteTask(child, new[] { root });
        root.Children.Remove(child);
        var snapshot = Storage.LoadSnapshot();
        Check(snapshot.Tasks.Select(t => t.Id).ToHashSet().SetEquals(new[] { root.Id, sibling.Id }) &&
              snapshot.Tasks.All(t => !t.CompletedAt.HasValue) && snapshot.Deletions.Count == 2,
              "completing a subtask deletes its tree without archiving it");
        Check(!File.Exists(Storage.DetailsPath(child.Id)) && !File.Exists(Storage.DetailsPath(grandchild.Id)) &&
              !File.ReadAllText(Storage.DeletionsPath).Contains("Private"), "deletion markers retain no task content");

        Storage.CompleteTask(root, new[] { root });
        snapshot = Storage.LoadSnapshot();
        Check(snapshot.Tasks.Count == 1 && snapshot.Tasks[0].Id == root.Id && snapshot.Tasks[0].CompletedAt.HasValue &&
              snapshot.Deletions.Count == 3 && Storage.LoadTasks().Count == 0, "only the main-list task enters the archive");
        Check(!File.ReadAllText(Storage.ArchivePath).Contains("Private") && !File.ReadAllText(Storage.ArchivePath).Contains("Sibling"),
              "archived main task contains no descendant content");

        var restoredRoot = Record(root.Id, "Restored root", 10);
        restoredRoot.UpdatedAt = TaskItem.NextTimestamp(snapshot.Tasks[0].UpdatedAt);
        var remote = Snapshot(restoredRoot);
        remote.Deletions = snapshot.Deletions;
        Storage.ApplySnapshot(remote);
        loaded = Storage.LoadTasks().Single();
        Check(!loaded.HasSubtasks && loaded.Children.Count == 0 && !File.ReadAllText(Storage.TasksPath).Contains("children"),
              "an empty child collection has no persisted list representation");
    });

    static SyncTask ChildRecord(Guid id, Guid parentId, string title, int seconds = 0)
    {
        var task = Record(id, title, seconds);
        task.ParentId = parentId;
        return task;
    }

    static void CheckSubtaskMerging()
    {
        var root = Record(Guid.NewGuid(), "Root");
        var child = ChildRecord(Guid.NewGuid(), root.Id, "Child", 1);
        var sibling = ChildRecord(Guid.NewGuid(), root.Id, "Sibling", 1);
        var grandchild = ChildRecord(Guid.NewGuid(), child.Id, "Grandchild", 1);
        var localChild = ChildRecord(child.Id, root.Id, "Edited child", 2);
        var remoteSibling = ChildRecord(sibling.Id, root.Id, "Edited sibling", 3);
        var merged = TaskMerge.Merge(Snapshot(root, localChild, sibling), Snapshot(root, child, remoteSibling));
        Check(merged.Tasks.Single(t => t.Id == child.Id).Title == "Edited child" &&
              merged.Tasks.Single(t => t.Id == sibling.Id).Title == "Edited sibling", "independent child edits merge independently");

        var completion = Snapshot(Record(root.Id, root.Title, 4, completed: true));
        completion.Deletions = new[] { child, sibling, grandchild }.Select(t => new TaskDeletion
        {
            Id = t.Id, ParentId = t.ParentId!.Value, DeletedAt = Baseline.AddSeconds(4)
        }).ToList();
        var stale = Snapshot(root, child, sibling, grandchild);
        merged = TaskMerge.Merge(completion, stale);
        Check(merged.Tasks.Count == 1 && merged.Tasks.Single().CompletedAt.HasValue && merged.Deletions.Count == 3,
              "stale children cannot resurrect a completed tree");

        var newer = ChildRecord(grandchild.Id, child.Id, "New offline edit", 5);
        foreach (var reverse in new[] { false, true })
        {
            var edited = Snapshot(root, child, sibling, newer);
            merged = reverse ? TaskMerge.Merge(edited, completion) : TaskMerge.Merge(completion, edited);
            Check(merged.Tasks.Select(t => t.Id).ToHashSet().SetEquals(new[] { root.Id, child.Id, grandchild.Id }) &&
                  merged.Tasks.All(t => !t.CompletedAt.HasValue) && merged.Deletions.Single().Id == sibling.Id,
                  "newer grandchild restores only the needed ancestor chain in either merge direction");
            Check(merged.Tasks.Single(t => t.Id == root.Id).Details == root.Details &&
                  merged.Tasks.Single(t => t.Id == child.Id).UpdatedAt > newer.UpdatedAt,
                  "restored ancestors use available active content and advance their timestamps");
            var again = TaskMerge.Merge(merged, completion);
            Check(again.Tasks.Count == 3 && again.Deletions.Count == 1, "restoration survives another sync with stale completion");
            var oldUnknown = ChildRecord(Guid.NewGuid(), root.Id, "Old third-computer child", 2);
            var thirdRoot = Record(root.Id, "Later third-computer title");
            thirdRoot.UpdatedAt = merged.Tasks.Single(t => t.Id == root.Id).UpdatedAt.AddMinutes(1);
            again = TaskMerge.Merge(merged, Snapshot(thirdRoot, oldUnknown));
            Check(again.Tasks.Count == 3 && again.Deletions.Any(d => d.Id == oldUnknown.Id) &&
                  again.Tasks.Single(t => t.Id == root.Id).Title == thirdRoot.Title,
                  "restored ancestors preserve deletion history even after a later title edit from a third computer");
        }

        // A task added offline was unknown when the other computer completed its ancestor.
        var unknown = ChildRecord(Guid.NewGuid(), root.Id, "Offline addition", 3);
        merged = TaskMerge.Merge(completion, Snapshot(root, unknown));
        Check(merged.Tasks.Count == 1 && merged.Deletions.Any(d => d.Id == unknown.Id), "older offline additions are deleted with their ancestor");
        unknown.UpdatedAt = Baseline.AddSeconds(6);
        merged = TaskMerge.Merge(completion, Snapshot(root, unknown));
        Check(merged.Tasks.Count == 2 && merged.Tasks.All(t => !t.CompletedAt.HasValue), "newer offline additions preserve their ancestor");

        var deletedChild = Snapshot(root);
        deletedChild.Deletions.Add(new TaskDeletion { Id = child.Id, ParentId = root.Id, DeletedAt = Baseline.AddSeconds(4) });
        var revivedChild = ChildRecord(child.Id, root.Id, "Newer child edit", 5);
        merged = TaskMerge.Merge(deletedChild, Snapshot(root, revivedChild));
        merged = TaskMerge.Merge(merged, Snapshot(root, child, grandchild));
        Check(merged.Tasks.Any(t => t.Id == child.Id) && merged.Tasks.All(t => t.Id != grandchild.Id) &&
              merged.Deletions.Any(d => d.Id == grandchild.Id), "a child edit winning its own deletion retains history against older unknown grandchildren");

        var version1 = Snapshot(root);
        version1.Version = 1;
        Check(TaskMerge.Merge(version1, Snapshot()).Version == 2, "version-1 root data upgrades during merge");
        var json = JsonSerializer.Serialize(merged);
        JsonSerializer.Deserialize<SyncSnapshot>(json)!.Validate();
        var orphan = ChildRecord(Guid.NewGuid(), Guid.NewGuid(), "Orphan");
        ExpectFailure<InvalidDataException>(() => Snapshot(orphan).Validate(), "orphan child");
        var cycleA = ChildRecord(Guid.NewGuid(), child.Id, "Cycle A");
        var cycleB = ChildRecord(child.Id, cycleA.Id, "Cycle B");
        ExpectFailure<InvalidDataException>(() => Snapshot(cycleA, cycleB).Validate(), "cyclic hierarchy");
        var completedChild = ChildRecord(child.Id, root.Id, "Invalid completed child");
        completedChild.CompletedAt = Baseline.UtcDateTime;
        completedChild.Details = "";
        ExpectFailure<InvalidDataException>(() => Snapshot(root, completedChild).Validate(), "archived subtask");
        var duplicateMarker = Snapshot(root, child);
        duplicateMarker.Deletions.Add(new TaskDeletion { Id = child.Id, ParentId = root.Id, DeletedAt = Baseline });
        ExpectFailure<InvalidDataException>(duplicateMarker.Validate, "active and deleted duplicate identity");
    }

    static ObservableCollection<TaskItem> MainTasks(MainWindow main) =>
        (ObservableCollection<TaskItem>)typeof(MainWindow).GetField("_tasks", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;

    static Dictionary<Guid, DetailsWindow> Notes(MainWindow main) =>
        (Dictionary<Guid, DetailsWindow>)typeof(MainWindow).GetField("_detailsWindows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;

    static void SetCompletionAnswer(MainWindow main, Func<TaskItem, int, bool> answer) =>
        typeof(MainWindow).GetProperty("CompletionConfirmation", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(main, answer);

    static T? VisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T result) return result;
            if (VisualChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    static void CheckSubtaskWindows() => WithCleanStorage(() =>
    {
        var settings = Backup(Storage.SettingsPath);
        MainWindow? main = null;
        try
        {
            Storage.SaveSettings(new AppearanceSettings { DetailsAutoSaveIntervalSeconds = 0, ItemDeleteRetentionSeconds = 30 });
            Storage.SaveTasks(new[] { new TaskItem { Title = "Parent", UpdatedAt = Baseline } });
            main = new MainWindow(enableGoogle: false);
            main.Show();
            var root = MainTasks(main).Single();
            Invoke<object?>(main, "OpenDetails", root);
            var note = Notes(main)[root.Id];
            var listSwitchIcon = Find<System.Windows.Shapes.Path>(note, "ViewIcon");
            var emptyListGeometry = listSwitchIcon.Data.ToString();
            Check(Grid.GetColumn(Find<Button>(note, "CopyButton")) < Grid.GetColumn(Find<Button>(note, "ViewButton")),
                  "text header places Copy before the list switch");
            var originalYaml = File.ReadAllText(Storage.TasksPath);
            var editor = Find<TextBox>(note, "Editor");
            editor.Text = "Pending parent text";
            var width = note.Width;
            var height = note.Height;
            Find<Button>(note, "ViewButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            Check(note.IsListView && !root.HasSubtasks && root.UpdatedAt == Baseline &&
                  File.ReadAllText(Storage.TasksPath) == originalYaml && !File.Exists(Storage.DetailsPath(root.Id)),
                  "opening an empty child list creates no list, text write or timestamp change");
            Check(note.SizeToContent == SizeToContent.Height && Find<Button>(note, "CopyButton").Visibility == Visibility.Collapsed,
                  "list view sizes to its contents and hides text copying");
            var textSwitchIcon = Find<System.Windows.Shapes.Path>(note, "TextViewIcon");
            var populatedTextGeometry = textSwitchIcon.Data.ToString();
            Check(textSwitchIcon.Visibility == Visibility.Visible && listSwitchIcon.Visibility == Visibility.Collapsed,
                  "list view shows the text-target icon");
            editor.Text = " \t\r\n";
            PumpDispatcher(TimeSpan.FromMilliseconds(10));
            Check(textSwitchIcon.Data.ToString() != populatedTextGeometry && !root.HasDetails,
                  "text-target icon changes immediately for unsaved whitespace-only text");
            editor.Text = "Pending parent text";
            PumpDispatcher(TimeSpan.FromMilliseconds(10));
            Check(textSwitchIcon.Data.ToString() == populatedTextGeometry && root.UpdatedAt == Baseline &&
                  !File.Exists(Storage.DetailsPath(root.Id)), "restoring unsaved text restores the populated icon without saving");
            Find<Button>(note, "ViewButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(editor.Text == "Pending parent text" && note.Width == width && note.Height == height,
                  "switching back restores editor text and dimensions");
            Find<Button>(note, "ViewButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var view = Find<TaskListControl>(note, "ChildView");
            Check(Math.Abs(view.ActualWidth - (note.ActualWidth - 2)) < 2,
                  "child-list view fills the window width without an empty trailing strip");
            var listWidth = note.Width;
            ApplySettings(main, new AppearanceSettings { DetailsAutoSaveIntervalSeconds = 0, ItemDeleteRetentionSeconds = 30, ZoomPercent = 150 });
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            Check(Math.Abs(note.ActualWidth - Math.Min(listWidth * 1.5, SystemParameters.WorkArea.Width)) < 2,
                  "list view scales its width during live zoom rather than collapsing to its minimum width");
            ApplySettings(main, new AppearanceSettings { DetailsAutoSaveIntervalSeconds = 0, ItemDeleteRetentionSeconds = 30 });
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            Find<TextBox>(view, "NewTitle").Text = "Child";
            Invoke<object?>(view, "AddNew", true);
            var child = root.Children.Single();
            Check(child.ParentId == root.Id && child.DueDate == DateTime.Today && root.HasSubtasks &&
                  Find<ListBox>(view, "TaskList").Items.Count == 1, "shared add behavior creates a dated child on top");
            PumpDispatcher(TimeSpan.FromMilliseconds(10));
            Check(listSwitchIcon.Data.ToString() != emptyListGeometry,
                  "adding the first child changes the list-target icon from empty to populated");
            root.Title = "Renamed parent";
            Check(note.Title == root.Title, "details title follows parent edits");
            var template = (DataTemplate)Find<TaskListControl>(main, "TaskView").FindResource("TaskTemplate");
            var row = (FrameworkElement)template.LoadContent();
            row.DataContext = root;
            PumpDispatcher(TimeSpan.FromMilliseconds(10));
            var listIcon = Find<System.Windows.Shapes.Path>(row, "SubtasksIndicator");
            var textIcon = Find<System.Windows.Shapes.Path>(row, "DetailsIcon");
            var plusIcon = Find<TextBlock>(row, "AddDetailsIcon");
            Check(listIcon.Visibility == Visibility.Visible && textIcon.Visibility == Visibility.Collapsed &&
                  plusIcon.Visibility == Visibility.Collapsed && listIcon.Parent is Grid iconHost &&
                  ReferenceEquals(Find<Button>(row, "DetailsButton").Content, iconHost),
                  "a parent with text and subtasks shows only the list icon inside its details button");
            root.HasDetails = false;
            PumpDispatcher(TimeSpan.FromMilliseconds(10));
            Check(listIcon.Visibility == Visibility.Visible && textIcon.Visibility == Visibility.Collapsed && plusIcon.Visibility == Visibility.Collapsed,
                  "a parent without text still shows only the list icon rather than a plus");
            root.HasDetails = true;

            Find<Button>(note, "ViewButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var parentContainer = (ListBoxItem)Find<ListBox>(main, "TaskList").ItemContainerGenerator.ContainerFromItem(root);
            var parentButton = VisualChild<Button>(parentContainer)!;
            parentButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(note.IsListView && Notes(main).Count == 1 && editor.Text == "Pending parent text" &&
                  !File.Exists(Storage.DetailsPath(root.Id)),
                  "clicking the list icon switches an existing text window to subtasks without saving or losing pending text");
            note.Close();
            parentButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            note = Notes(main)[root.Id];
            listSwitchIcon = Find<System.Windows.Shapes.Path>(note, "ViewIcon");
            editor = Find<TextBox>(note, "Editor");
            view = Find<TaskListControl>(note, "ChildView");
            Check(note.IsListView && editor.Visibility == Visibility.Collapsed && view.Visibility == Visibility.Visible,
                  "clicking the list icon opens a new window directly in subtask mode");

            Invoke<object?>(main, "OpenDetails", child);
            var childNote = Notes(main)[child.Id];
            Check(!childNote.IsListView, "a task without children still opens text details");
            Find<TextBox>(childNote, "Editor").Text = "Pending child text";
            var grandchild = new TaskItem { ParentId = child.Id, Title = "Grandchild" };
            Invoke<object?>(main, "AddTask", grandchild, false);
            Invoke<object?>(main, "Save");
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            var childContainer = (ListBoxItem)Find<ListBox>(view, "TaskList").ItemContainerGenerator.ContainerFromItem(child);
            VisualChild<Button>(childContainer)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(childNote.IsListView && Find<TextBox>(childNote, "Editor").Text == "Pending child text",
                  "list icons inside subtask lists open nested lists and retain pending text");
            int warnings = 0;
            SetCompletionAnswer(main, (_, count) => { warnings++; Check(count == 2, "confirmation counts unfinished descendants recursively"); return false; });
            var beforeCancel = root.UpdatedAt;
            root.IsDone = true;
            Check(!root.IsDone && root.RemoveTimer == null && root.UpdatedAt == beforeCancel && warnings == 1,
                  "cancelled completion leaves the parent unchecked without advancing its timestamp");
            var mainList = Find<ListBox>(main, "TaskList");
            var rootRow = (ListBoxItem)mainList.ItemContainerGenerator.ContainerFromItem(root);
            var checkbox = VisualChild<CheckBox>(rootRow)!;
            checkbox.SetCurrentValue(CheckBox.IsCheckedProperty, true);
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            Check(checkbox.IsChecked == false && !root.IsDone && warnings == 2,
                  "cancelling through the bound checkbox also restores its visual unchecked state");
            SetCompletionAnswer(main, (_, _) => true);
            root.IsDone = true;
            Check(child.IsBlocked && grandchild.IsBlocked && Find<TextBox>(childNote, "Editor").IsReadOnly && !view.IsEnabled,
                  "confirmed completion locks descendant rows and editors during undo delay");
            root.IsDone = false;
            Check(!child.IsBlocked && !grandchild.IsBlocked && !Find<TextBox>(childNote, "Editor").IsReadOnly && view.IsEnabled,
                  "undo restores descendant editing");
            SetCompletionAnswer(main, (_, _) => throw new InvalidOperationException("All descendants are checked; no warning is needed."));
            grandchild.IsDone = true;
            var grandchildTimer = grandchild.RemoveTimer!;
            child.IsDone = true;
            var childTimer = child.RemoveTimer!;
            root.IsDone = true;
            Check(!grandchildTimer.IsEnabled && !childTimer.IsEnabled, "parent completion pauses existing descendant timers");
            root.IsDone = false;
            Check(childTimer.IsEnabled && !grandchildTimer.IsEnabled, "undo resumes the nearest checked child while its own descendants stay paused");
            child.IsDone = false;
            Check(grandchildTimer.IsEnabled, "undoing the child resumes its grandchild timer");
            grandchild.IsDone = false;
            SetCompletionAnswer(main, (_, _) => true);

            // Sync refresh must preserve selected modes and rebind existing nested windows/sessions.
            Invoke<object?>(main, "PrepareLocalForSync");
            var incoming = Storage.LoadSnapshot();
            incoming.Tasks.Single(t => t.Id == child.Id).Title = "Synced child";
            incoming.Tasks.Single(t => t.Id == child.Id).UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(1);
            var drive = new FakeDrive(incoming) { Hold = new TaskCompletionSource() };
            var syncing = Invoke<Task>(main, "SynchronizeAsync", drive);
            Check(!note.IsEnabled && !childNote.IsEnabled, "sync disables windows at every depth");
            drive.Hold.SetResult();
            FinishOnDispatcher(syncing);
            root = MainTasks(main).Single();
            child = root.Children.Single();
            Check(note.IsListView && childNote.Title == "Synced child" &&
                  ReferenceEquals(Find<ListBox>(view, "TaskList").ItemsSource, root.Children),
                  "sync rebinds open child lists and nested details without changing view");
            child.IsDone = true;
            Invoke<object?>(main, "PrepareLocalForSync");
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            Check(!root.HasSubtasks && root.Children.Count == 0 && !Notes(main).ContainsKey(child.Id),
                  "completing a child deletes its descendants and closes nested windows");
            row.DataContext = root;
            PumpDispatcher(TimeSpan.FromMilliseconds(10));
            Check(Find<System.Windows.Shapes.Path>(row, "SubtasksIndicator").Visibility == Visibility.Collapsed &&
                  textIcon.Visibility == Visibility.Visible && plusIcon.Visibility == Visibility.Collapsed &&
                  listSwitchIcon.Data.ToString() == emptyListGeometry &&
                  Storage.LoadSnapshot().Tasks.All(t => !t.CompletedAt.HasValue),
                  "deleting the last child restores the text bubble and empty list-target icon without an archive entry");
            note.Close();
            Invoke<object?>(main, "OpenDetails", root);
            Check(!Notes(main)[root.Id].IsListView, "reopened details starts with text even after using a child list");
        }
        finally
        {
            if (main != null) { main.Close(); PumpDispatcher(TimeSpan.FromMilliseconds(20)); }
            Restore(Storage.SettingsPath, settings);
        }
    });
}
