using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using StickyTodo;

static class Program
{
    [STAThread]
    static int Main()
    {
        try
        {
            CheckSettingsDefaultsAndBounds();
            CheckSettingsPersistence();
            CheckSettingsDialog();
            CheckTaskRenderingAndTimers();
            CheckDetailsSaving();
            CheckDetailsFileFailures();
            CheckTaskStorageMigration();
            CheckDetailsWindows();
            Console.WriteLine("All regression checks passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    static void CheckSettingsDefaultsAndBounds()
    {
        var settings = new AppearanceSettings();
        Check(settings.ItemDeleteRetentionSeconds == 5, "default retention is 5 seconds");
        Check(settings.MaxItemsToDisplay == 10, "default display limit is 10");
        Check(!settings.BoldHighPriority, "bold high-priority titles default off");

        settings.ItemDeleteRetentionSeconds = -1;
        settings.MaxItemsToDisplay = 0;
        Check(settings.ItemDeleteRetentionSeconds == 0, "negative retention is clamped to zero");
        Check(settings.MaxItemsToDisplay == 1, "display limit is clamped to at least one");
        settings.MaxItemsToDisplay = 101;
        Check(settings.MaxItemsToDisplay == 100, "display limit is clamped to at most 100");
    }

    static void CheckSettingsPersistence()
    {
        string path = Storage.SettingsPath;
        var previous = Backup(path);
        try
        {
            File.WriteAllText(path, "backgroundColor: '#AABBCC'\nopacity: 0.8\n");
            var legacy = Storage.LoadSettings();
            Check(legacy.ItemDeleteRetentionSeconds == 5 && legacy.MaxItemsToDisplay == 10 && !legacy.BoldHighPriority,
                "settings missing new keys use backward-compatible defaults");

            var saved = new AppearanceSettings
            {
                ItemDeleteRetentionSeconds = 0,
                MaxItemsToDisplay = 37,
                BoldHighPriority = true
            };
            Storage.SaveSettings(saved);
            var loaded = Storage.LoadSettings();
            Check(loaded.ItemDeleteRetentionSeconds == 0 && loaded.MaxItemsToDisplay == 37 && loaded.BoldHighPriority,
                "settings survive YAML save and load");
        }
        finally
        {
            Restore(path, previous);
        }
    }

    static void CheckSettingsDialog()
    {
        var original = new AppearanceSettings
        {
            ItemDeleteRetentionSeconds = 8,
            MaxItemsToDisplay = 20
        };
        int previews = 0;
        var dialog = new SettingsWindow(original, _ => previews++);
        Check(!ReferenceEquals(original, dialog.Settings), "dialog edits a copied settings object");
        Check(dialog.Settings.ItemDeleteRetentionSeconds == 8 && dialog.Settings.MaxItemsToDisplay == 20,
            "dialog copies the configured values");
        Check(Find<Button>(dialog, "SaveButton").IsEnabled, "valid settings enable Save");

        var retention = Find<TextBox>(dialog, "DeleteRetentionSeconds");
        var maxItems = Find<TextBox>(dialog, "MaxItemsToDisplay");
        retention.Text = "-1";
        Check(!Find<Button>(dialog, "SaveButton").IsEnabled, "negative retention disables Save");
        maxItems.Text = "101";
        Check(!Find<Button>(dialog, "SaveButton").IsEnabled, "out-of-range display limit disables Save");
        retention.Text = "0";
        Check(!Find<Button>(dialog, "SaveButton").IsEnabled, "all numeric fields must be valid before Save is enabled");
        maxItems.Text = "100";
        Check(Find<Button>(dialog, "SaveButton").IsEnabled, "boundary values are accepted");
        Check(dialog.Settings.ItemDeleteRetentionSeconds == 0 && dialog.Settings.MaxItemsToDisplay == 100,
            "valid numeric changes update the dialog settings");

        Find<CheckBox>(dialog, "BoldHighPriority").IsChecked = true;
        Check(dialog.Settings.BoldHighPriority && previews > 0, "bold preference updates and previews");
        Find<TextBox>(dialog, "HexColor").Text = "#GGGGGG";
        Check(!Find<Button>(dialog, "SaveButton").IsEnabled, "invalid color disables Save");
        Find<TextBox>(dialog, "HexColor").Text = "#112233";
        Check(Find<Button>(dialog, "SaveButton").IsEnabled, "valid color restores Save when numeric fields are valid");

        Find<Button>(dialog, "ResetButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(dialog.Settings.ItemDeleteRetentionSeconds == 5 && dialog.Settings.MaxItemsToDisplay == 10 &&
              !dialog.Settings.BoldHighPriority, "Reset all restores the new defaults");
        Check(original.ItemDeleteRetentionSeconds == 8 && original.MaxItemsToDisplay == 20,
            "dialog edits do not mutate the original settings");
    }

    static void CheckTaskRenderingAndTimers()
    {
        var paths = new[] { Storage.SettingsPath, Storage.TasksPath, Storage.ArchivePath };
        var previousFiles = paths.Select(Backup).ToArray();
        MainWindow? window = null;
        try
        {
            Storage.SaveSettings(new AppearanceSettings { MaxItemsToDisplay = 2 });
            Storage.SaveTasks(Array.Empty<TaskItem>());
            window = new MainWindow();

            var tasks = (ObservableCollection<TaskItem>)typeof(MainWindow)
                .GetField("_tasks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var attach = typeof(MainWindow).GetMethod("Attach", BindingFlags.Instance | BindingFlags.NonPublic)!;
            for (int i = 0; i < 4; i++) attach.Invoke(window, new object[] { new TaskItem { Title = $"Task {i}" }, false });

            var preview = new AppearanceSettings { MaxItemsToDisplay = 2, ItemDeleteRetentionSeconds = 30, BoldHighPriority = true };
            ApplySettings(window, preview);
            Check(Find<ListBox>(window, "TaskList").Height == 48, "display count preview controls the visible list height");
            Check((FontWeight)window.Resources["HighPriorityFontWeight"] == FontWeights.Bold,
                "bold preference previews the high-priority font weight");
            CheckPriorityTemplate(window);

            preview.MaxItemsToDisplay = 1;
            preview.BoldHighPriority = false;
            ApplySettings(window, preview);
            Check(Find<ListBox>(window, "TaskList").Height == 24, "minimum list display limit is reflected in its height");
            Check((FontWeight)window.Resources["HighPriorityFontWeight"] == FontWeights.Normal,
                "disabling bold previews the normal font weight");

            var undo = new TaskItem { Title = "Undo completion" };
            attach.Invoke(window, new object[] { undo, false });
            undo.IsDone = true;
            var timer = undo.RemoveTimer;
            Check(timer?.Interval == TimeSpan.FromSeconds(30), "completion timer uses the configured retention");
            undo.IsDone = false;
            Check(undo.RemoveTimer == null && timer is { IsEnabled: false }, "undo stops the pending deletion timer");

            var expiring = new TaskItem { Title = "Expired completion" };
            attach.Invoke(window, new object[] { expiring, false });
            ApplySettings(window, new AppearanceSettings { ItemDeleteRetentionSeconds = 1, MaxItemsToDisplay = 10 });
            expiring.IsDone = true;
            PumpDispatcher(TimeSpan.FromSeconds(3));
            Check(!tasks.Contains(expiring), "completed task is removed when its retention expires");
        }
        finally
        {
            window?.Close();
            for (int i = 0; i < paths.Length; i++) Restore(paths[i], previousFiles[i]);
        }
    }

    static void ApplySettings(MainWindow window, AppearanceSettings settings) =>
        typeof(MainWindow).GetMethod("ApplyAppearance", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { settings });

    static void CheckDetailsSaving()
    {
        var now = DateTimeOffset.UtcNow;
        var writes = new List<string>();
        var session = new DetailsSaveSession("", writes.Add, () => now);
        session.Save(true);
        Check(writes.Count == 0, "untouched notes do not save");
        session.Edit("First\nŻółć ☁");
        session.Save();
        Check(writes.Count == 1, "first edit saves immediately");
        for (int i = 0; i < 59; i++)
        {
            now = now.AddSeconds(1);
            session.Edit($"Text {i}");
            session.Save();
        }
        Check(writes.Count == 1 && session.IsDirty, "continuous edits are throttled");
        now = now.AddSeconds(1);
        session.Save();
        Check(writes.Count == 2 && writes[^1] == "Text 58", "pending text saves at minute boundary");
        session.Edit("Close");
        session.Save(true);
        Check(writes[^1] == "Close", "closing bypasses throttle");
        session.Edit("Reopened");
        session.Save();
        Check(session.IsDirty && writes.Count == 3, "same session retains throttle across reopen");
        session.Discard();
        session.Save(true);
        Check(writes.Count == 3, "discarded sessions cannot recreate deleted notes");

        var failing = new DetailsSaveSession("", _ => throw new IOException("Test failure"), () => now);
        failing.Edit("Keep me");
        try { failing.Save(true); } catch (IOException) { }
        Check(failing.IsDirty && failing.Text == "Keep me", "failed writes retain unsaved text");
    }

    static void CheckTaskStorageMigration()
    {
        var root = Path.Combine(Path.GetTempPath(), "StickyTodo-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "tasks.yaml"), "[]");
            File.WriteAllText(Path.Combine(root, "task-archive.yaml"), "[]");
            Storage.InitializeTaskStorage(root);
            Check(!File.Exists(Path.Combine(root, "tasks.yaml")) && File.Exists(Path.Combine(root, "tasks", "tasks.yaml")), "legacy tasks move to subfolder");
            Check(File.Exists(Path.Combine(root, "tasks", "task-archive.yaml")), "archive moves to subfolder");
            Storage.InitializeTaskStorage(root);
            File.WriteAllText(Path.Combine(root, "tasks.yaml"), "legacy");
            bool conflict = false;
            try { Storage.InitializeTaskStorage(root); } catch (IOException) { conflict = true; }
            Check(conflict && File.ReadAllText(Path.Combine(root, "tasks.yaml")) == "legacy" && File.ReadAllText(Path.Combine(root, "tasks", "tasks.yaml")) == "[]", "migration conflicts preserve both files");
        }
        finally { Directory.Delete(root, true); }
    }

    static void CheckDetailsFileFailures()
    {
        var id = Guid.NewGuid();
        var path = Storage.DetailsPath(id);
        try
        {
            Storage.SaveDetails(id, "Original\nŻółć ☁");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                bool readFailed = false, deleteFailed = false, saveFailed = false;
                try { Storage.LoadDetails(id); } catch (IOException) { readFailed = true; }
                try { Storage.DeleteDetails(id); } catch (IOException) { deleteFailed = true; }
                var session = new DetailsSaveSession("Original\nŻółć ☁", text => Storage.SaveDetails(id, text));
                session.Edit("Pending replacement");
                try { session.Save(true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { saveFailed = true; }
                Check(readFailed && deleteFailed && saveFailed && session.IsDirty, "locked files surface failures and preserve pending text");
            }
            Check(Storage.LoadDetails(id) == "Original\nŻółć ☁", "failed replacement preserves the original file");
            Storage.SaveDetails(id, "");
            Check(File.Exists(path) && Storage.LoadDetails(id) == "", "clearing details persists an empty file");
        }
        finally
        {
            Storage.DeleteDetails(id);
            File.Delete(path + ".tmp");
        }
    }

    static void CheckDetailsWindows()
    {
        var paths = new[] { Storage.TasksPath, Storage.ArchivePath, Storage.SettingsPath };
        var backups = paths.Select(Backup).ToArray();
        var noteIds = new List<Guid>();
        MainWindow? main = null;
        try
        {
            File.WriteAllText(Storage.TasksPath, "- title: Same\n- title: Same\n");
            var loaded = Storage.LoadTasks();
            noteIds.AddRange(loaded.Select(t => t.Id));
            Check(loaded[0].Id != loaded[1].Id && loaded.All(t => t.Id != Guid.Empty), "legacy tasks receive distinct IDs");
            Check(Storage.LoadTasks().Select(t => t.Id).SequenceEqual(loaded.Select(t => t.Id)), "assigned IDs are persisted");
            loaded[0].Title = "Renamed";
            Storage.SaveDetails(loaded[0].Id, "Unicode Żółć ☁\nSecond line");
            Storage.SaveTasks(loaded.AsEnumerable().Reverse());
            Check(Storage.LoadTasks()[1].Id == loaded[0].Id && Storage.LoadDetails(loaded[0].Id).Contains("Second line"), "rename and reorder preserve details");
            main = new MainWindow();
            main.Show();
            var tasks = (ObservableCollection<TaskItem>)typeof(MainWindow).GetField("_tasks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
            var open = typeof(MainWindow).GetMethod("OpenDetails", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var windows = (Dictionary<Guid, DetailsWindow>)typeof(MainWindow).GetField("_detailsWindows", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
            var task = tasks[0];
            open.Invoke(main, new object[] { task });
            var note = windows[task.Id];
            Check(note.ResizeMode == ResizeMode.CanResize && note.MinWidth == 240 && note.MinHeight == 180, "notes support resizing with minimum dimensions");
            open.Invoke(main, new object[] { task });
            Check(windows.Count == 1 && ReferenceEquals(windows[task.Id], note), "repeated opening reuses the window");
            open.Invoke(main, new object[] { tasks[1] });
            Check(windows.Count == 2, "different tasks can have notes open simultaneously");
            windows[tasks[1].Id].Close();
            task.Title = "Live title";
            Check(note.Title == "Live title", "note title follows task title");
            var editor = Find<TextBox>(note, "Editor");
            editor.Text = "First edit";
            editor.Text = "Pending edit\nŻółć";
            Check(Storage.LoadDetails(task.Id) == "First edit", "window throttles subsequent edits");
            note.Close();
            Check(Storage.LoadDetails(task.Id) == "Pending edit\nŻółć" && windows.Count == 0, "note close flushes pending text and unregisters window");
            open.Invoke(main, new object[] { task });
            note = windows[task.Id];
            ApplySettings(main, new AppearanceSettings { Opacity = 0.8 });
            Check(note.Opacity == 0.8 && note.Background == main.Background, "notes follow appearance changes");
            Find<TextBox>(note, "Editor").Text = "Shutdown save";
            task.IsDone = true;
            task.IsDone = false;
            Check(File.Exists(Storage.DetailsPath(task.Id)), "undo retains the details file");
            main.Close();
            Check(Storage.LoadDetails(task.Id) == "Shutdown save", "application close flushes notes");
            main = new MainWindow();
            main.Show();
            windows = (Dictionary<Guid, DetailsWindow>)typeof(MainWindow).GetField("_detailsWindows", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
            Check(windows.Count == 0, "notes are not reopened automatically on startup");
            tasks = (ObservableCollection<TaskItem>)typeof(MainWindow).GetField("_tasks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
            task = tasks.Single(t => t.Id == task.Id);
            open.Invoke(main, new object[] { task });
            Find<TextBox>(windows[task.Id], "Editor").Text = "Immediate first edit";
            Find<TextBox>(windows[task.Id], "Editor").Text = "Pending at completion";
            task.IsDone = true;
            main.Close();
            Check(!File.Exists(Storage.DetailsPath(task.Id)), "final completion removes details even with an open note");
        }
        finally
        {
            main?.Close();
            foreach (var id in noteIds) Storage.DeleteDetails(id);
            for (int i = 0; i < paths.Length; i++) Restore(paths[i], backups[i]);
        }
    }

    static void CheckPriorityTemplate(MainWindow window)
    {
        var template = (DataTemplate)window.FindResource("TaskTemplate");
        var root = (FrameworkElement)template.LoadContent();
        var title = (TextBlock)root.FindName("TaskTitle")!;
        var highTrigger = title.Style.Triggers.OfType<DataTrigger>().Single(trigger =>
            trigger.Binding is Binding binding && binding.Path?.Path == nameof(TaskItem.IsHigh));
        Check(string.Equals(highTrigger.Value?.ToString(), "True", StringComparison.OrdinalIgnoreCase) && highTrigger.Setters.OfType<Setter>().Any(setter => setter.Property == TextBlock.FontWeightProperty),
            "high-priority titles have a conditional font-weight setter");
    }

    static void PumpDispatcher(TimeSpan timeout)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = timeout };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    static T Find<T>(FrameworkElement element, string name) where T : class =>
        element.FindName(name) as T ?? throw new InvalidOperationException($"Could not find {name}.");

    static byte[]? Backup(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;

    static void Restore(string path, byte[]? content)
    {
        if (content == null)
        {
            if (File.Exists(path)) File.Delete(path);
        }
        else
        {
            File.WriteAllBytes(path, content);
        }
    }

    static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException($"Regression check failed: {description}");
    }
}
